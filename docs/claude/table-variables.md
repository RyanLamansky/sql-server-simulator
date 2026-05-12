# Table variables (`DECLARE @t TABLE (...)`)

A per-batch heap table referenced via `@`-prefixed names. Probed against SQL Server 2025 (2026-05-12); the simulator's coverage centers on the EF / app-compat surface (`OUTPUT INTO @t` for SaveChanges, simple error-log staging, multi-row capture from DML).

## Storage scope

Backed by a `HeapTable` with `IsTableVariable = true` stored on `BatchContext.TableVariables` (a `Dictionary<string, HeapTable>` keyed by name with the leading `@` stripped, mirroring `BatchContext.Variables`'s convention). The dict is discarded with the `BatchContext` at end of batch — the per-batch lifetime real SQL Server documents. Cross-batch (via `GO` or separate `ExecuteReader` / `ExecuteNonQuery` calls) the variable is gone, same as in real SQL Server.

Variable names live in a shared namespace with scalar variables: `DECLARE @t int; DECLARE @t TABLE (...)` raises Msg 134 ("variable name '@t' has already been declared"). Probe-confirmed both directions (scalar-then-table and table-then-scalar).

`HeapTable.IsTableVariable` routes a few behavioral exceptions from regular heap tables:
- DML mutations (INSERT / UPDATE / DELETE / OUTPUT INTO @t) bypass the undo log. ROLLBACK doesn't undo @t mutations (probe-confirmed: `INSERT @t inside BEGIN TRAN; ROLLBACK` leaves the row intact). The classic error-log pattern (`INSERT @errors ... ; ROLLBACK; SELECT * FROM @errors` inside CATCH) works.
- The table never appears in `sys.tables` / `INFORMATION_SCHEMA.TABLES` (the row generators only walk schema heap-table dicts, not per-batch table-variable dicts).
- Constraint / NOT-NULL error messages render the bare `@t` name (probe-confirmed: `table '@t'` wording, no schema qualifier).

## Grammar (v1 scope)

```
DECLARE @t TABLE ( col_def_or_table_pk [, col_def_or_table_pk]... )
```

Coverage in v1:
- **Columns**: type + optional `(N)` / `(p, s)` length-or-scale spec.
- **`NULL` / `NOT NULL`** column nullability.
- **`DEFAULT expr`** column default.
- **Inline anonymous `PRIMARY KEY`** on a single column. Promotes the column to `NOT NULL` if not declared (`DECLARE @t TABLE (id int PRIMARY KEY)` works; the column is implicitly NOT NULL). Explicit `NULL` + inline PK raises Msg 8111.
- **Table-level anonymous `PRIMARY KEY (col_list)`** — promotes referenced columns to NOT NULL if not declared. Multiple PKs on one @t raise Msg 8110.

Rejected at parse time:
- **`CONSTRAINT name`** (named constraints, inline or table-level) → Msg 102 ("Incorrect syntax near 'CONSTRAINT'") matching probe-confirmed real SQL Server behavior. Real SQL Server's grammar doesn't allow named constraints inside `DECLARE @t TABLE`.
- **`REFERENCES`** (foreign keys) → Msg 102 (probe-confirmed).

Rejected as `NotSupportedException` (deferred to v2 — real SQL Server accepts these in table variables, but the simulator's v1 surfaces a loud feature-named error rather than silently dropping):
- `IDENTITY` columns
- `UNIQUE` constraints
- Inline `CHECK` constraints
- Computed columns (`col AS expr`)
- `rowversion` columns

Rejected because real SQL Server also rejects:
- **Multi-variable DECLARE with a table variable** (`DECLARE @t1 TABLE (...), @t2 TABLE (...)`) → Msg 102. Only one table variable per DECLARE statement. Mixed scalar + table (`DECLARE @x int = 5, @t TABLE (...)`) also rejected (Msg 156 / Msg 102 in the simulator — the probe shows Msg 156 first then a cascading 1087, the simulator surfaces the parse-rejection path directly).

## DML routing

`@`-prefixed leaves route through `BatchContext.TableVariables` instead of the schema dict. `BatchContext.ParseObjectName(context, acceptTableVariable: true)` accepts the `@t` form as a 1-part name with the `@` kept in the leaf for downstream routing; without `acceptTableVariable: true` the parser rejects `@t` so non-DML statements (CREATE TABLE / ALTER TABLE / DROP TABLE / TRUNCATE TABLE / SELECT…INTO) raise Msg 102 matching probe-confirmed real-server behavior.

Resolution sites that accept `@t`:
- **INSERT** target — `INSERT @t VALUES (...)`, `INSERT INTO @t SELECT ...`.
- **UPDATE** target — `UPDATE @t SET col = expr`.
- **DELETE** target — `DELETE [FROM] @t`.
- **MERGE** target — `MERGE @t USING source ON ... WHEN NOT MATCHED THEN INSERT ...`.
- **SELECT FROM** source — `SELECT * FROM @t`, with optional alias, in joins, derived tables, CTE bodies.
- **OUTPUT INTO** target on INSERT / UPDATE / DELETE / MERGE — see below.

Missing `@t` (not declared) raises Msg 1087 (`"Must declare the table variable \"@t\""`) — distinct from regular tables' Msg 208. The leaf spelling (with `@`) tells the resolver which error to raise.

`dbo.@t` (2-part name with @-prefixed leaf) raises Msg 102 at parse (probe-confirmed). `ParseObjectName` rejects any `.` after an @-prefixed segment.

`@t` in expression position (e.g. `SET @x = @t`) raises Msg 137 ("Must declare the scalar variable '@t'") — probe-confirmed: real SQL Server treats `@t` in expression context as a scalar-variable reference and fails to find it (since `@t` is registered as a table variable, not a scalar).

## Non-transactional semantics

Probe-confirmed: table variables aren't affected by `ROLLBACK`. INSERT / UPDATE / DELETE against `@t` skip the undo log (`destinationTable.IsTableVariable ? null : context.Batch.CurrentUndoLog` at every heap-mutation call site). The pattern:

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

This is the load-bearing pattern for many error-logging idioms in legacy T-SQL. Statement-level atomicity (multi-row INSERT failing mid-statement) isn't modeled for table variables — partial rows from a failed multi-row INSERT into `@t` stay. Fidelity gap; document if an app surfaces it.

## `OUTPUT … INTO @t [(cols)]`

Extends the OUTPUT clause on INSERT / UPDATE / DELETE / MERGE to direct rows to a table-variable target instead of the result set. Probe-confirmed: when `INTO @t` is present, the rows go to the target only — nothing surfaces to the client. The dispatch returns `SimulatedNonQuery` (matching the probe where `INSERT t OUTPUT … INTO @out VALUES (...)` showed no result rows; a subsequent `SELECT * FROM @out` showed the captured rows).

Column mapping resolves at parse time:
- **No column list** (`OUTPUT col1, col2 INTO @t`): positional fill — projection column 0 → target column 0, etc. Counts must match (Msg 213 on mismatch).
- **Explicit column list** (`OUTPUT col1, col2 INTO @t (insid, insname)`): projection column 0 → target column named `insid`, etc. Column names must exist in target (Msg 207); counts must match (Msg 213).

Target columns not covered by the projection receive NULL. Real SQL Server applies the column's DEFAULT (if any) for unfilled targets — the simulator's v1 always writes NULL. Apps using OUTPUT INTO @t typically project every non-default target column, so this gap is mostly theoretical.

`OUTPUT … INTO <regular_table>` raises `NotSupportedException`. Real SQL Server accepts both targets; EF / app patterns center on table variables, so the simulator's v1 covers @t only.

## Fidelity gaps documented in this bundle

- Per-statement atomicity for `@t` mutations is not preserved (mid-statement multi-row INSERT failure leaves partial rows in `@t`). Real SQL Server's statement-level rollback covers @t too.
- `OUTPUT … INTO <regular_table>` not supported (NotSupportedException).
- `IDENTITY`, `UNIQUE`, inline `CHECK`, computed columns, `rowversion` in `@t` raise NotSupportedException — real SQL Server accepts all of these.
- Target columns not filled by `OUTPUT INTO` receive NULL (not the column's DEFAULT).
- `@t` doesn't appear in `sys.tables` / `INFORMATION_SCHEMA.TABLES` (real SQL Server doesn't either, so this is fidelity-aligned but worth noting).

## Architecture notes

- **Where the dict lives**: `BatchContext.TableVariables` is the natural per-batch home. Variables share the same scope and the duplicate-name check (Msg 134) is one resolution.
- **Why `HeapTable.IsTableVariable` instead of a separate type**: table variables reuse 90% of `HeapTable`'s storage / encoding / constraint enforcement. The behavioral exceptions (non-transactional, no catalog visibility, name-wording) are narrow enough to gate on a flag rather than fork into a new type.
- **Why `OutputTarget` lives inside `Simulation.Output.cs`**: the OUTPUT projection class already owned per-row encoding; INTO-target is one more option on the same path. The nullable return from `ProjectRow` signals "consumed by target" vs "produced for result set" — the caller branches on `HasTarget` to suppress the result-set construction.
