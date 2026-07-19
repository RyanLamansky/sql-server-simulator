# System-versioned temporal tables

Read this when working on `PERIOD FOR SYSTEM_TIME`, `GENERATED ALWAYS AS ROW START / END`, `WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))`, the auto-created history sibling, `FOR SYSTEM_TIME ALL / AS OF` query syntax, or related `HeapTable` / `HeapColumn` metadata.

## What ships

- **CREATE TABLE** with `PERIOD FOR SYSTEM_TIME (startCol, endCol)` table-level declaration + per-column `GENERATED ALWAYS AS ROW START | END [HIDDEN] NOT NULL`.
  The two period columns must be `datetime2(N)` NOT NULL; nullable or non-datetime2 raises Msg 13501 / 13587.
  Asymmetric definitions raise Msg 13504 / 13505; period names not matching the GENERATED columns raise Msg 13506 / 13507; orphan GENERATED-AS-ROW columns without a `PERIOD` declaration raise Msg 13509.
  Probe-confirmed verbatim wording against SQL Server 2025.
- **`WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = schema.table))`** trailing clause auto-creates the sibling history `HeapTable` at parent-creation time.
  The history table mirrors the parent's column shape — names, types, nullability, hidden flag, persisted-computed expressions — but strips engine-managed flags (IDENTITY, GENERATED ALWAYS), inline constraints, and DEFAULTs (history rows carry materialized values from the parent).
  `sys.tables.temporal_type = 2` for the parent, `1` for history; `sys.tables.history_table_id` references the sibling's object_id (NULL for non-temporal and history tables themselves).
  The auto-created history table starts as a plain heap (no PK), so its `sys.indexes` is a single HEAP row until a `CREATE CLUSTERED INDEX` is added — as the bacpac loader emits for the WWI `*_Archive` siblings — at which point it becomes a single CLUSTERED row at `index_id 1` with **no** phantom heap row (the index-id allocation authority suppresses it; see [`indexes.md`](indexes.md#index-id-allocation)).
  A history table therefore always projects exactly one `sys.indexes` row with `index_id < 2`, which is what DacFx's `SqlTable` export query joins against.
- **Auto-named history (`HISTORY_TABLE` omitted)** raises `NotSupportedException` at parse.
  EF Core 10 always emits the explicit form, so the auto-generated `MSSQL_TemporalHistoryFor_<hash>` shape isn't needed.
- **INSERT** on a system-versioned parent auto-populates the period columns: ROW START = the statement's frozen `BatchContext.CurrentStatement.UtcNow`, ROW END = `DateTime.MaxValue` (datetime2(7) precision = `9999-12-31 23:59:59.9999999`).
  Explicit values for a GENERATED ALWAYS column raise Msg 13536.
  Implicit insert column lists exclude GENERATED columns (so `INSERT INTO Customers (Id, Name) VALUES (...)` works without listing period columns).
- **UPDATE** on a system-versioned parent: pre-update full row is captured (`oldSnapshotNeeded` forced true), the post-SET row's ROW START is bumped to UtcNow, then `WriteHistoryRowsForUpdate` writes the captured pre-update row to the history sibling with ROW END overwritten to UtcNow (the period during which the row was current).
  Setting a GENERATED ALWAYS column in SET raises Msg 13537.
- **DELETE** on a system-versioned parent: pre-delete full row captured (`needsFullForHistory` forced true), then history row written with ROW END = UtcNow before tombstoning the current row.
- **`SELECT *`** excludes hidden columns (probe-confirmed: real SQL Server omits `IsHidden` columns from star expansion).
  Explicit references continue to bind by name, including in INSERT column lists and OUTPUT clauses (EF Core 10 emits `OUTPUT INSERTED.[PeriodEnd], INSERTED.[PeriodStart]` and lists period columns by name in tracked-entity SELECTs).
- **`FROM <table> FOR SYSTEM_TIME ALL [AS] <alias>`** unions current + history rows.
  **`FROM <table> FOR SYSTEM_TIME AS OF <expr> [AS] <alias>`** filters parent + history rows where `ROW START <= <expr> < ROW END`.
  The expression is evaluated once on iteration start (no per-row re-evaluation, matching SQL Server's "constant per query" contract); column references inside the expression raise Msg 207.
  ISO 8601 string literals with trailing `Z` (UTC marker — EF Core 10 emits this) are accepted by datetime2 coercion.
  The remaining temporal-query forms (`BETWEEN ... AND ...`, `FROM ... TO ...`, `CONTAINED IN (..., ...)`) raise `NotSupportedException` until an application needs them.
- **DROP TABLE** on a system-versioned parent or its history sibling raises Msg 13552; caller must `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)` first.
- **`ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = OFF)`** flips the parent's `HeapTable.SystemVersioning` to `null` and the history sibling's `HeapTable.IsHistoryTable` to `false` — both tables revert to plain regular status, and `DROP TABLE` on either now succeeds.
  Period definition and GENERATED ALWAYS / HIDDEN column metadata stay intact on the parent (probe-confirmed 2026-05-13 against SQL Server 2025: `sys.tables.temporal_type` flips to 0 on both, `history_table_id` clears to NULL, but the parent's `sys.columns.generated_always_type_desc` keeps `AS_ROW_START` / `AS_ROW_END` and `is_hidden` stays `True`).
  Post-SET-OFF DML semantics: INSERT still auto-populates the period columns (the per-column GENERATED ALWAYS marker drives the auto-populate in `Simulation.Insert.cs`, independent of the versioning link); explicit INSERT into a GENERATED ALWAYS column still raises Msg 13536; UPDATE does **not** bump ROW START (the engine treats the marker as "no longer engine-maintained" for writes once versioning is off); DELETE doesn't copy to the (former) history table.
  Error paths: Msg 4902 if the target name doesn't resolve (the alter-table-specific table-not-found wording, distinct from Msg 208's generic name-resolution); Msg 13591 if the target exists but isn't system-versioned (fires both for plain regular tables and for the history sibling itself — the history sibling carries the `HISTORY_TABLE` role but doesn't "have" versioning, only the parent does).
- **`ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = name [, DATA_CONSISTENCY_CHECK = ON|OFF]))`** is the inverse of OFF: takes a base with a `PERIOD FOR SYSTEM_TIME` declaration (but no versioning link) and an existing history table, sets `base.SystemVersioning = history` + `history.IsHistoryTable = true`.
  The bacpac loader's phase-5 wire-up step emits this for every SqlTable carrying a `TemporalSystemVersioningHistoryTable` relationship (SqlPackage's emit shape).
  Error paths: Msg 4902 (history target doesn't resolve); Msg 13558 (base doesn't have PERIOD FOR SYSTEM_TIME); Msg 13530 (base already system-versioned); Msg 13533 (history table is already in use as another temporal sibling or is itself a system-versioned base).
  `DATA_CONSISTENCY_CHECK = ON|OFF` parses-and-discards — the simulator doesn't enforce the temporal-data-consistency rules that the option toggles (caller-trusted history rows in the loader path).
  Column-shape match validation between base and history is deferred — currently the link is established whenever both endpoints exist; mismatches will surface at query time.
  Other `ALTER TABLE` shapes (ADD / DROP COLUMN, ADD / DROP CONSTRAINT, DROP PERIOD, REBUILD, etc.) raise `NotSupportedException`.
- **Direct INSERT into history** raises Msg 13559.
  History rows are populated only by the engine via parent UPDATE / DELETE.
- **`sys.periods`** projects one row per table with a `PERIOD FOR SYSTEM_TIME` declaration (`HeapTable.PeriodColumns`), **excluding history siblings** (they carry a copied `PeriodColumns` for the `FOR SYSTEM_TIME` query machinery but hold no period of their own — real SQL Server surfaces only the base table).
  Row shape: `name` = `SYSTEM_TIME`, `period_type` = 1 / `period_type_desc` = `SYSTEM_TIME_PERIOD`, `object_id`, `start_column_id` / `end_column_id` = the 1-based ordinals of the ROW START / ROW END columns.
  `sys.columns.generated_always_type` reports 1 (`AS_ROW_START`) / 2 (`AS_ROW_END`) for the period columns.
  See [`catalog-views.md`](catalog-views.md).

## Data-model footprint

- **`HeapColumn`** gained `GeneratedAs` (`GeneratedAlwaysAsRow.None / Start / End`) and `IsHidden` fields.
- **`HeapTable`** gained `PeriodColumns: (int StartOrdinal, int EndOrdinal)?` (set at construction), `SystemVersioning: HeapTable?` (mutable; set after history is auto-created — points from parent → history; null on regular and history tables), and `IsHistoryTable: bool` (mutable; true on the history sibling).
- **Parser scope:** `ParseColumnList`'s `pendingPeriod: List<(string StartCol, string EndCol)>?` carries the period names through column-list parsing; `ResolvePeriodColumns` (in `Simulation.Create.cs`) validates and resolves to ordinals.
  `ParseSystemVersioningOption` parses the trailing `WITH (...)` clause and returns the history table name.
  `BuildHistoryTable` mirrors the parent column shape into the history sibling.
- **Query scope:** `Selection.ParseOptionalForSystemTime` peeks for `FOR SYSTEM_TIME` between the FROM source's table name and any alias, returns `null` when absent (cursor restored), or a row enumerator wrapping `parent.Rows + history.Rows` (ALL) / `TemporalAsOfRowSource` (AS OF) when present.

## EF Core 10 emit shape

`ModelBuilder.Entity<T>().ToTable("Customers", b => b.IsTemporal())` defaults the period columns to **`PeriodStart`** and **`PeriodEnd`** (not `ValidFrom` / `ValidTo` — that's the SQL Server documentation convention but not EF's default).
Tests bootstrap their tables with EF's expected names.
INSERT emits `INSERT INTO [tbl] ([cols-without-period]) OUTPUT INSERTED.[PeriodEnd], INSERTED.[PeriodStart] VALUES (...)`; UPDATE emits `UPDATE [tbl] SET [col] = @p OUTPUT INSERTED.[PeriodEnd], INSERTED.[PeriodStart] WHERE [Id] = @p`; tracked-entity SELECTs explicitly list period columns by name.
`.TemporalAll()` → `FROM [tbl] FOR SYSTEM_TIME ALL AS [c]`; `.TemporalAsOf(@t)` → `FROM [tbl] FOR SYSTEM_TIME AS OF '<iso-literal-with-Z>' AS [c]`.

## Not modeled

- **`HISTORY_RETENTION_PERIOD`** option on `SYSTEM_VERSIONING = ON`.
  Real SQL Server prunes history rows beyond the retention period; the simulator stores history forever.
- **Auto-named history** (`SYSTEM_VERSIONING = ON` without `(HISTORY_TABLE = ...)`).
- **`FOR SYSTEM_TIME BETWEEN ... AND ...`** / **`FROM ... TO ...`** / **`CONTAINED IN (..., ...)`** query forms.
  Only `ALL` and `AS OF <expr>` ship.
- **Column-shape match validation** between base and history on `ALTER ... SET (SYSTEM_VERSIONING = ON ...)`.
  Real SQL Server validates same column count + names + types + nullability + period-column wiring; the simulator establishes the link unconditionally (matching CREATE WITH SYSTEM_VERSIONING = ON, which builds the history from the base and so doesn't need separate validation).
  Mismatched-shape history tables will surface at query time rather than at ALTER time.
- **Msg 13544 qualified-name format**: real SQL Server pads temp-table names with their internal allocation suffix (`#x____...___…000000000148`); the simulator emits the bare `tempdb.dbo.#x` form.
  Same Msg number / framing, less verbose name.
- **LOB-eligible columns** (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) on a temporal table — the `FOR SYSTEM_TIME` row source presents one `lobStore` reference to the FROM machinery; mixing parent and history rows in the same enumerator would need per-row LOB-store dispatch.
  No test scenario reaches this path.
