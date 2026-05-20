# Table-valued parameters (`CREATE TYPE … AS TABLE`)

User-defined table types + their TVP / `DECLARE @t MyType` consumers. Probed against SQL Server 2025 (2026-05-12). Shares the column-list parser with `CREATE TABLE` / `DECLARE @t TABLE` (`Simulation.ParseColumnList` with an `isTableType` flag).

## Storage scope

`CREATE TYPE schema.name AS TABLE (column_list)` registers a `TableType` in `Schema.TableTypes` (per-database, per-schema dict). Each consumer site (`DECLARE @t MyType`, TVP procedure parameter, ADO.NET `SqlDbType.Structured`-shaped parameter) calls `TableType.Clone` to materialize a fresh `HeapTable` instance with `IsTableVariable = true` and (for procedure parameters / ADO.NET TVPs) `IsTableValuedParameter = true`. The clone's `object_id` is freshly allocated; constraint names are regenerated per clone (matching probe — each `DECLARE @t MyType` gets unique PK / UNIQUE name hashes).

Type-name namespace is separate from the object namespace: a table named `foo` and a table type named `foo` coexist (probe-confirmed; Msg 2714 only fires within the object namespace). The type-name namespace **is** shared with `Schema.AliasTypes` (scalar UDDTs) — duplicate-name collisions across either dict raise Msg 219 verbatim. See [`alias-types.md`](alias-types.md) for the parallel `CREATE TYPE … FROM <builtin>` shape.

Allocators:
- `Database.AllocateObjectId()` — assigns `type_table_object_id` per type (and the per-clone object_id at each `Clone` call).
- `Database.AllocateUserTypeId()` — assigns `user_type_id` starting at 256 (system types occupy ids 0–255 per real SQL Server convention).

## Grammar (CREATE TYPE)

```
CREATE TYPE schema.name AS TABLE ( column_or_table_constraint [, column_or_table_constraint]... )
DROP TYPE [IF EXISTS] schema.name
```

Coverage mirrors `DECLARE @t TABLE` (shared parser):

- Columns with type + optional `(N)` / `(p, s)` spec.
- `NULL` / `NOT NULL` nullability.
- `DEFAULT expr` column default.
- `IDENTITY [(seed, increment)]`.
- Inline anonymous `PRIMARY KEY` (single column) and table-level anonymous `PRIMARY KEY (cols)` (bare-nullable columns promoted to NOT NULL, explicit-NULL columns referenced by table-level PK → Msg 8111).
- Inline / table-level anonymous `UNIQUE`.
- Inline / table-level `CHECK (predicate)` (inline form rejects peer-column refs via Msg 8141).
- Computed columns `col AS expr [PERSISTED [NOT NULL]]` (PERSISTED is parsed but functionally a no-op).
- `rowversion` / `timestamp` (one per type — second declaration raises Msg 2738).

Rejected at parse time (probe-confirmed against real SQL Server's grammar):

- **`CONSTRAINT name`** (named constraints, inline or table-level) → **Msg 156** ("Incorrect syntax near the keyword 'CONSTRAINT'") — distinct from `DECLARE @t TABLE`'s Msg 102 wording.
- **`REFERENCES`** (foreign keys) → **Msg 156** ("Incorrect syntax near the keyword 'REFERENCES'").
- **Inline non-unique `INDEX`** (`INDEX ix_n (n)`) → Msg 102 (deferred; real SQL Server accepts this from 2014+ — adding it would benefit `DECLARE @t TABLE` too via the shared parser).
- **`AS <basetype>` scalar UDT form** — only `AS TABLE` is modeled.

Existence checks (probe-confirmed):

- **Duplicate type name within the schema** → **Msg 219** ("The type 'X' already exists, or you do not have permission to create it.") — distinct wording from Msg 2714's cross-namespace object collision.
- **`DROP TYPE` against a missing name** → **Msg 218** ("Could not find the type 'X'. Either it does not exist or you do not have the necessary permission."). `IF EXISTS` suppresses silently.
- **`DROP TYPE` while referenced by a procedure** → **Msg 3732** ("Cannot drop type 'X' because it is being referenced by object 'Y'. There may be other objects that reference this type."). The simulator scans every procedure in every schema of the current database and names the first one found (real SQL Server emits a single name even when more than one referencer exists).

## `DECLARE @t MyType` binding

`Simulation.Declare.cs` accepts the type-name form alongside the inline `TABLE` form. Two disambiguation rules:

- **Multi-part type name** (e.g. `dbo.MyType`) — unambiguous; resolved via `BatchContext.TryResolveTableType`. Miss raises Msg 2715 ("Cannot find data type X. Parameter or variable '@t' has an invalid data type.").
- **1-part type name** — `BatchContext.TryResolveTableType` runs first against the default schema; on miss falls through to the scalar parser (`SqlType.GetByName`). Both `DECLARE @t int` and `DECLARE @t MyType` work through this path.

Per-clone `HeapTable` is registered on `BatchContext.TableVariables` (same dict as inline `@t TABLE`-form variables).

Multi-variable DECLARE with TVP types works (`DECLARE @t1 dbo.MyType, @t2 dbo.MyType`) — probe-confirmed differs from inline `TABLE` (which rejects multi-variable with Msg 102). `SET IDENTITY_INSERT @t ON` still rejects with Msg 102 (matching inline `@t TABLE`).

## Stored procedure TVP parameter

`CREATE PROC name @rows schema.MyType READONLY` registers a `ProcedureParameter` with `TableType` non-null. The READONLY keyword is mandatory (probe-confirmed Msg 352 wording: "The table-valued parameter '@rows' must be declared with the READONLY option."). Grammar restrictions on a TVP-typed parameter (all probe-confirmed Msg 102):

- `= default` after the type is rejected.
- `OUTPUT` after the type is rejected (TVP parameters are implicitly read-only — there's no writeback).

The procedure-body source capture is the same as scalar / table-returning procs: re-tokenized per invocation. DML statements (`INSERT` / `UPDATE` / `DELETE` / `MERGE`) targeting the parameter raise **Msg 10700** ("The table-valued parameter '@X' is READONLY and cannot be modified.") at body parse time (probe-confirmed wording; check fires when the resolved target table has `IsTableValuedParameter = true`).

**Fidelity gap on error timing**: real SQL Server raises Msg 10700 at `CREATE PROCEDURE` time (body validation runs at CREATE). The simulator captures the body as text and validates at first invocation, so Msg 10700 surfaces from `EXEC dbo.p1 @t` rather than from the CREATE itself. Same gap as scalar UDFs / regular procs.

`DROP TYPE` reference-scan walks every schema's `Procedures` dict and rejects with Msg 3732 if any parameter's `TableType` matches.

## EXEC with TVP argument

Two-shape recognition in `ParseExecArgument`:

- `EXEC p @local_t` where `@local_t` resolves to a table variable (`BatchContext.TableVariables` lookup hit): the `ProcArgument.TableValue` field carries the live `HeapTable` reference. Scalar fallback through `GetVariableSlot` runs only on a TableVariables miss.
- `EXEC p @rows = @local_t` named form: same path inside `ParseExecArgument` after the named-arg checkpoint logic resolves `@rows = ` as the parameter name.

`Simulation.InvokeProcedure` binding:

- Allocates a parallel `boundTableValues` array alongside `boundValues`.
- For each TVP parameter: clones the type into a fresh TVP-flagged `HeapTable`, then bulk-copies the supplied source's rows via `Heap.EnumerateRows()` → `Heap.Insert()`. Pass-by-value semantics matching real SQL Server's TVP copy.
- Unsupplied TVP parameters get an empty clone (probe-confirmed: `EXEC p` with the TVP arg omitted is legal).
- Scalar argument passed to a TVP parameter raises **Msg 206** ("Operand type clash: int is incompatible with `<typeleaf>`") — probe-confirmed wording, with the table-type leaf name (no schema qualifier).

The child `BatchContext` is constructed via a new ctor overload that accepts the seeded `tableVariables` dict alongside `variables`.

## ADO.NET Structured parameter surface

`SimulatedDbParameter.TypeName` provides the `SqlParameter.TypeName`-shaped API. The ADO.NET concrete-pipeline chain is strongly typed end-to-end (`SimulatedDbConnection.CreateCommand()` → `SimulatedDbCommand`; `SimulatedDbCommand.CreateParameter()` → `SimulatedDbParameter`; `Parameters` → `SimulatedDbParameterCollection`), so no downcast is needed when the variables are typed concretely:

```csharp
using var con = simulation.CreateDbConnection();
using var cmd = con.CreateCommand();
var p = cmd.CreateParameter();
p.ParameterName = "@rows";
p.Value = dataTable;        // or any IDataReader
p.TypeName = "dbo.MyType";
cmd.Parameters.Add(p);
```

`TypeName` is a plain `[AllowNull] string TypeName { get; set; } = ""` on `SimulatedDbParameter`. `SimulatedDbParameterCollection` implements `IReadOnlyList<SimulatedDbParameter>` and ships strongly-typed indexers + an `Add(SimulatedDbParameter)` overload returning the parameter — mirroring the `SqlParameterCollection` shape.

`BatchContext` constructor seeds TVP-shaped parameters into `TableVariables` (via `SeedTableVariablesFromStructuredParameters`) before the dispatch loop runs. Detection is value-type-based: `Value is DataTable or IDataReader`. Missing `TypeName` on such a value raises `ArgumentException` from the seeding path (mirroring the `Microsoft.Data.SqlClient`-side check); unknown `TypeName` raises Msg 2715.

Row binding is positional (probe-confirmed F2 / F2b): column names on a `DataTable` source are ignored entirely — column N → target column N. Column-count mismatch raises **Msg 500** ("Trying to pass a table-valued parameter with N column(s) where the corresponding user-defined table type requires M column(s)."). Identity columns receive auto-generated values; supplying a non-null value for an identity column raises **Msg 1077** ("INSERT into an identity column not allowed on table variables.") — probe-confirmed F8.

`IDataReader` is the System.Data interface (not `DbDataReader` specifically) so any implementation works; `Microsoft.Data.SqlClient`'s `SqlDataReader` (the documented TVP source) flows through naturally. Reading the parameter source requires a separate connection from the consuming command (MARS) — the simulator doesn't model multi-active result sets on one connection, so the same connection limitation applies in practice.

Not modeled: `IEnumerable<SqlDataRecord>` (the third documented TVP value type). `SqlDataRecord` lives in `Microsoft.Data.SqlClient.Server`; adding it would require either a SqlClient dependency (load-bearing-no) or a reflection-based duck-typed path.

## Catalog views

- **`sys.types`**: ships full surface (35 system types + every user-defined table type). Table-type rows carry `system_type_id = 243`, `is_user_defined = 1`, `is_table_type = 1`, `is_nullable = 0` (probe-confirmed G1). The new view ships alongside the legacy bare `systypes` table (kept for old code paths that read it directly).
- **`sys.table_types`**: per-database list of user-defined table types only — `name / type_table_object_id / is_user_defined / schema_id / user_type_id`.
- **`sys.columns`**: extended to project columns through `type_table_object_id` (probe G3 join shape works end-to-end). is_identity / is_computed inherit from the column definition.
- **`sys.parameters`**: extended with `is_readonly` (true for TVP parameters, false otherwise). TVP rows surface `system_type_id = 243` and the TVP's `user_type_id` instead of the placeholder `Int32` type.
- **`INFORMATION_SCHEMA.DOMAINS`**: ships with one row per user-defined table type; `data_type` is the literal `'table type'` (probe G6).
- **`sys.objects`**: no rows for table types (probe G7 — types don't live in sys.objects).

## `TYPE_ID(name)`

Scalar function resolving a system or user-defined type name to its `user_type_id` (or NULL on miss). 1- or 2-part name; brackets stripped. The common idiom `IF type_id('dbo.MyType') IS NOT NULL DROP TYPE dbo.MyType` works.

## Fidelity gaps remaining

- **CREATE-time body validation** — Msg 10700 against a TVP parameter surfaces at first EXEC, not at CREATE PROC (pre-existing gap with all stored-proc bodies). Real SQL Server validates body references at CREATE time.
- **Inline non-unique `INDEX` clause** — Msg 102 in v1; real SQL Server accepts it. Adding it via the shared parser would close the gap for both `DECLARE @t TABLE` and `CREATE TYPE`.
- **`IEnumerable<SqlDataRecord>`** as a TVP value source isn't accepted (SqlClient dependency / reflection path).
- **`CREATE TYPE … FROM <basetype>`** (scalar UDT form) — not modeled; only the AS TABLE form ships.
- **Constraint-name hashes** — clones embed the @t name in the hash. Real SQL Server's table-type clone names differ in suffix derivation; constraint-violation error wording byte-matches the wider quirks documented in CLAUDE.md (the `PK__#<hex>__<8hex>` shape uses the simulator's FNV-1a convention).
- **TVP value-source column-name matching** — the simulator follows real SQL Server's positional binding (column names ignored) verbatim. No fidelity gap there.
- **`ALTER TYPE`** — doesn't exist in real SQL Server for table types (must DROP + CREATE). Not modeled, matching that.

## Architecture notes

- **`Schema.TableTypes` is its own dict** (not folded into a unified `UserDefinedTypes` dict). Future scalar UDTs can grow a sibling dict if/when they ship. Mirrors how Views and Procedures got their own dicts at their bundles.
- **`TableType.PendingKeys` / `PendingChecks` stored, not pre-resolved**. Constraint resolution runs per `Clone()` so each @t / TVP parameter gets fresh constraint names embedding its own target name. Computed columns and `HeapColumn[]` are resolved once at CREATE TYPE and shared by reference (immutable post-CREATE TYPE).
- **`HeapTable.IsTableValuedParameter` distinct from `IsTableVariable`**: every TVP is also a table variable; the extra flag gates the Msg 10700 enforcement at the four DML sites (INSERT / UPDATE / DELETE / MERGE). TVP parameter clones set both flags; `DECLARE @t MyType` only sets `IsTableVariable`.
- **Row copy at TVP binding**: real SQL Server's TVP semantics are pass-by-value (the proc body's modifications don't affect the caller). Since Msg 10700 already blocks all body-side modifications, pass-by-reference would be observably equivalent — but the simulator does a row-copy via `Heap.Insert` for clarity (and to match the documented semantics if Msg 10700 enforcement ever moves to CREATE-time validation).
- **`SeedTableVariablesFromStructuredParameters` runs after `Parser` is initialized**: needs `BatchContext.CurrentDatabase` (which routes through `Parser.CurrentDatabase`). Constructor ordering matters.
- **The ADO.NET concrete-pipeline classes (`SimulatedDbConnection` / `SimulatedDbCommand` / `SimulatedDbParameter` / `SimulatedDbParameterCollection` / `SimulatedDbDataReader` / `SimulatedDbTransaction`) are all public** with `new`-shadowed strongly-typed return shapes (`CreateCommand` → `SimulatedDbCommand`, `CreateParameter` → `SimulatedDbParameter`, `Parameters` → `SimulatedDbParameterCollection`, `ExecuteReader` → `SimulatedDbDataReader`, `BeginTransaction` → `SimulatedDbTransaction`). `TypeName` rides as an ordinary `SimulatedDbParameter` property; consumers never need a downcast when the variables are typed concretely. Previously `TypeName` was a C# 14 `extension(DbParameter)` block over a process-wide `ConditionalWeakTable` — the publicization replaced it.
