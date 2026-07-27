# Table variables (`DECLARE @t TABLE (...)`)

A per-batch heap table referenced via `@`-prefixed names.
Probed against SQL Server 2025.
The column-list parser is shared with CREATE TABLE (see `Simulation.ParseColumnList`); table-variable-only restrictions (CONSTRAINT-named, REFERENCES) are gated on an `isTableVariable` flag.

## Storage scope

Backed by a `HeapTable` with `IsTableVariable = true` stored on `BatchContext.TableVariables` (a `Dictionary<string, HeapTable>` keyed by name with the leading `@` stripped, mirroring `BatchContext.Variables`'s convention).
The dict is discarded with the `BatchContext` at end of batch — the per-batch lifetime real SQL Server documents.
Cross-batch (via `GO` or separate `ExecuteReader` / `ExecuteNonQuery` calls) the variable is gone, same as in real SQL Server.

Variable names live in a shared namespace with scalar variables: `DECLARE @t int; DECLARE @t TABLE (...)` raises Msg 134 ("variable name '@t' has already been declared").
Probe-confirmed both directions (scalar-then-table and table-then-scalar).

`HeapTable.IsTableVariable` routes a few behavioral exceptions from regular heap tables:
- DML mutations (INSERT / UPDATE / DELETE / OUTPUT INTO @t) log to `BatchContext.CurrentTableVarUndoLog` (per-statement scope), not `CurrentUndoLog` (connection-tx scope).
  The per-statement log is dropped on statement success and replayed on statement failure — preserves statement-level atomicity while keeping `ROLLBACK TRAN` blind to `@t` writes (probe-confirmed).
  The classic error-log pattern (`INSERT @errors ... ; ROLLBACK; SELECT * FROM @errors` inside CATCH) works.
- The table never appears in `sys.tables` / `INFORMATION_SCHEMA.TABLES` (the row generators only walk schema heap-table dicts, not per-batch table-variable dicts).
- Constraint / NOT-NULL error messages render the bare `@t` name (probe-confirmed: `table '@t'` wording for PK/UNIQUE; `table "@t"` for CHECK, no schema qualifier).

## Grammar

```
DECLARE @t TABLE ( column_or_table_constraint [, column_or_table_constraint]... )
```

Coverage:
- **Columns**: type + optional `(N)` / `(p, s)` length-or-scale spec.
- **`NULL` / `NOT NULL`** column nullability.
- **`DEFAULT expr`** column default.
- **`IDENTITY [(seed, increment)]`** — `SCOPE_IDENTITY()` / `@@IDENTITY` observe @t inserts (probe-confirmed).
  `SET IDENTITY_INSERT @t ON` raises Msg 102 — real SQL Server's grammar forbids it for table variables, so there's no way to force a value into an @t identity column.
- **Inline anonymous `PRIMARY KEY`** on a single column.
  Promotes the column to `NOT NULL` if not declared.
  Explicit `NULL` + inline PK raises Msg 8111.
- **Table-level anonymous `PRIMARY KEY (col_list)`** — promotes bare-nullable referenced columns to NOT NULL (probe-confirmed: `DECLARE @t TABLE (a int, b int, PRIMARY KEY (a, b))` works and rejects NULL inserts with Msg 515).
  Explicit-NULL columns referenced by a table-level PK raise Msg 8111.
  Multiple PKs on one @t raise Msg 8110.
- **Inline `UNIQUE`** and **table-level `UNIQUE (col_list)`** — violations raise Msg 2627.
- **Inline `CHECK (predicate)`** and **table-level `CHECK (predicate)`** — violations raise Msg 547.
  Inline-CHECK peer-column refs raise Msg 8141 (same structural walk as CREATE TABLE).
- **Computed columns (`col AS expr [PERSISTED [NOT NULL]]`)** — non-persisted columns evaluate per-read; the PERSISTED keyword is accepted but functionally a no-op for table variables (no on-disk store, so the storage distinction doesn't matter).
- **`rowversion` / `timestamp`** — backed by the database-scoped 8-byte counter, same as regular tables.
  Two rowversion columns in one `@t` raise Msg 2738.

Rejected at parse time (probe-confirmed against real SQL Server's grammar):
- **`CONSTRAINT name`** (named constraints, inline or table-level) → Msg 102 ("Incorrect syntax near 'CONSTRAINT'").
  Real SQL Server's grammar doesn't allow named constraints inside `DECLARE @t TABLE`.
- **`REFERENCES`** (foreign keys) → Msg 102.
- **Multi-variable DECLARE with a table variable** (`DECLARE @t1 TABLE (...), @t2 TABLE (...)`) → Msg 102.
  Only one table variable per DECLARE statement.
  Mixed scalar + table (`DECLARE @x int = 5, @t TABLE (...)`) also rejected.

## DML routing

`@`-prefixed leaves route through `BatchContext.TableVariables` instead of the schema dict.
`BatchContext.ParseObjectName(context, acceptTableVariable: true)` accepts the `@t` form as a 1-part name with the `@` kept in the leaf for downstream routing; without `acceptTableVariable: true` the parser rejects `@t` so non-DML statements (CREATE TABLE / ALTER TABLE / DROP TABLE / TRUNCATE TABLE / SELECT…INTO) raise Msg 102 matching probe-confirmed real-server behavior.

Resolution sites that accept `@t`:
- **INSERT** target — `INSERT @t VALUES (...)`, `INSERT INTO @t SELECT ...`.
- **UPDATE** target — `UPDATE @t SET col = expr`.
- **DELETE** target — `DELETE [FROM] @t`.
- **MERGE** target — `MERGE @t USING source ON ... WHEN NOT MATCHED THEN INSERT ...`.
- **SELECT FROM** source — `SELECT * FROM @t`, with optional alias, in joins, derived tables, CTE bodies.
- **OUTPUT INTO** target on INSERT / UPDATE / DELETE / MERGE — see below.

Missing `@t` (not declared) raises Msg 1087 (`"Must declare the table variable \"@t\""`) — distinct from regular tables' Msg 208.
The leaf spelling (with `@`) tells the resolver which error to raise.

`dbo.@t` (2-part name with @-prefixed leaf) raises Msg 102 at parse (probe-confirmed).
`ParseObjectName` rejects any `.` after an @-prefixed segment.

`@t` in expression position (e.g. `SET @x = @t`) raises Msg 137 ("Must declare the scalar variable '@t'") — probe-confirmed: real SQL Server treats `@t` in expression context as a scalar-variable reference and fails to find it (since `@t` is registered as a table variable, not a scalar).

## Non-transactional semantics + statement-level atomicity

Probe-confirmed against real SQL Server:
- `@t` mutations are NOT affected by `ROLLBACK TRAN` (writes survive a tx-scoped rollback).
- `@t` mutations ARE statement-atomic — a multi-row INSERT that hits a NOT NULL / PK / UNIQUE / CHECK violation mid-batch leaves zero rows from that statement.

Implementation: every heap-mutation site routes `@t` writes to `BatchContext.CurrentTableVarUndoLog` (allocated fresh per-statement by `RunMutation`) instead of `CurrentUndoLog` (the per-connection-tx log).
On statement success the per-statement log is discarded; on statement failure it's fully rolled back inside the same `catch` block that handles regular-table rollback.
The per-statement scope means `ROLLBACK TRAN` (which only walks the tx-scoped log) never sees `@t` entries — preserving the non-transactional invariant — while replay-on-exception covers the atomic-statement invariant.

The classic error-log pattern still works:

```sql
DECLARE @errors TABLE (msg nvarchar(200));
BEGIN TRAN;
BEGIN TRY
    -- some work that may fail
END TRY
BEGIN CATCH
    INSERT @errors VALUES (ERROR_MESSAGE());
    ROLLBACK;  -- regular table changes undone, @errors keeps the row
END CATCH;
SELECT * FROM @errors;  -- captures the rolled-back error
```

## `OUTPUT … INTO <target> [(cols)]`

Extends the OUTPUT clause on INSERT / UPDATE / DELETE / MERGE to direct rows to a `@t` or regular-table target instead of the result set.
Probe-confirmed: when `INTO target` is present, the rows go to the target only — nothing surfaces to the client.
The dispatch returns `SimulatedNonQuery` (matching the probe where `INSERT t OUTPUT … INTO target VALUES (...)` showed no result rows; a subsequent `SELECT * FROM target` showed the captured rows).

Target shapes (both probe-confirmed):
- **Table variable** (`@t`): writes route through the per-statement `@t` undo log; missing `@t` declaration raises Msg 1087.
- **Regular table**: writes route through the connection's main undo log (and participate in `ROLLBACK TRAN`); missing table raises Msg 208.

Column mapping resolves at parse time:
- **No column list** (`OUTPUT col1, col2 INTO target`): positional fill — projection column 0 → target column 0, etc. Counts must match the target's full column count (Msg 213 on mismatch).
- **Explicit column list** (`OUTPUT col1, col2 INTO target (insid, insname)`): projection column 0 → target column named `insid`, etc. Column names must exist in target (Msg 207); counts must match the list (Msg 213).

Target columns not covered by the projection evaluate the column's `DEFAULT` expression if declared (probe-confirmed: real SQL Server applies the DEFAULT, not NULL, for unfilled OUTPUT-INTO target columns); columns with no DEFAULT receive NULL.
The defaulted value is coerced into the target column's type via the same `CoerceForInsert` path INSERT uses.

## Fidelity gaps remaining

- `@t` doesn't appear in `sys.tables` / `INFORMATION_SCHEMA.TABLES`.
  Real SQL Server doesn't surface table variables there either, so this is fidelity-aligned.
- Auto-generated constraint names follow the simulator's convention (`PK__@t__<16hex>` / `UQ__@t__<16hex>` / `CK__@t__<col>__<8hex>`) — same shape as regular tables.
  Real SQL Server uses a tempdb-derived 8-char hex for the table portion (`PK__#A292BB6__…`), so the suffixes won't byte-match.
  This is the documented general constraint-name quirk applied to `@t` specifically.

## Architecture notes

- **Shared column-list parser**: `Simulation.ParseColumnList` handles both CREATE TABLE and DECLARE @t TABLE column-list bodies.
  The `isTableVariable` flag gates the two restrictions (CONSTRAINT-named → Msg 102, REFERENCES → Msg 102); everything else (IDENTITY / UNIQUE / CHECK / computed / rowversion) is shared.
  The shared parser also handles table-level PK promotion of bare-nullable columns to NOT NULL, fixing a CREATE TABLE fidelity bug as a side effect.
- **Where the dict lives**: `BatchContext.TableVariables` is the natural per-batch home.
  Variables share the same scope and the duplicate-name check (Msg 134) is one resolution.
- **Why `HeapTable.IsTableVariable` instead of a separate type**: table variables reuse most of `HeapTable`'s storage / encoding / constraint enforcement.
  The behavioral exceptions (per-statement undo log instead of tx-scoped, no catalog visibility, name-wording, no `SET IDENTITY_INSERT`) are narrow enough to gate on a flag rather than fork into a new type.
- **Why two parallel undo logs**: `CurrentUndoLog` is per-connection-tx (lifetime extended by `BEGIN TRAN`); `CurrentTableVarUndoLog` is always per-statement.
  Routing on `IsTableVariable` keeps the two invariants (tx-rollback skips @t, statement-rollback covers @t) disjoint without requiring a new transaction model.
- **Why `OutputTarget` lives inside `Simulation.Output.cs`**: the OUTPUT projection class already owned per-row encoding; INTO-target is one more option on the same path.
  The nullable return from `ProjectRow` signals "consumed by target" vs "produced for result set" — the caller branches on `HasTarget` to suppress the result-set construction.
  The target's `Append` method handles default-eval for unfilled columns and routes through the appropriate undo log based on the target's `IsTableVariable` flag.
