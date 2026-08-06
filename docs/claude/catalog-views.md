# `sys.*` and `INFORMATION_SCHEMA.*` catalog views

`Simulation.CatalogViews` is a process-static dict of virtual catalog-view projections keyed by fully-qualified name (`"sys.tables"`, `"INFORMATION_SCHEMA.COLUMNS"`, etc.) so one resolver serves both namespaces without per-schema dispatch.
Each `CatalogView` carries a fixed `HeapColumn[]` schema and a `Func<BatchContext, IEnumerable<SqlValue[]>>` row generator that runs against live `Database` / `Schema` / `HeapTable` metadata; rows aren't cached, so CREATE / DROP / TRUNCATE changes made earlier in the same batch are visible on the next read.
The FROM-source parser detects catalog views via `BatchContext.TryResolveCatalogView` (case-insensitive on the qualifier, 2-part or `<currentDb>.qualifier.<view>` 3-part), wraps the view in `Selection.ForCatalogView`, and threads it as the `FromSource.LateralPlan` — so each Execute re-runs the generator.
The `RowEncoder.EncodeRow(HeapColumn[], SqlValue[])` overload bridges the SqlValue-array generator output into the byte stream the FromSource consumes.

**Metadata-visibility filtering (restricted principals).**
For a genuinely restricted session principal, the object-scoped views (`sys.tables` / `objects` / `columns` / the constraint / index / trigger / parameter / module views, and the `INFORMATION_SCHEMA` object views) surface only rows for objects the principal may view metadata for; `BuiltInResources.ApplyMetadataFilter` wraps each generator's output at the `Selection.ForCatalogView` seam.
A `dbo` / full-visibility session (including SMO-as-sysadmin) short-circuits before any allocation, so the projections above are byte-identical for them.
The rule, the exact filtered / unfiltered view lists, and the `OBJECT_ID` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` NULL behavior live in [`permissions.md`](permissions.md#metadata-visibility).

**Materialize-once in joins (perf).**
A catalog view's row generator takes only `(BatchContext, Database)` — never an outer-row resolver — so it is *provably uncorrelated*: its rows can't depend on an enclosing join row.
The catalog-view `FromSource` carries `MaterializeOnce = true`, and at the start of each query execution `Selection.Execution.cs`'s `MaterializeUncorrelatedDeferredSources` pass runs each such plan **once**, replacing the deferred `LateralPlan` with a re-enumerable `Rows` list (`FromSource.WithMaterializedRows`).
Two wins follow: (1) a nested-loop join stops re-generating the whole view per outer row (regenerating every column of every table for `sys.all_columns`, every type for `sys.types`, …), and (2) the plain `Rows` source becomes eligible for `TryPlanEquiJoin`'s O(L + R) hash path (see [`joins.md`](joins.md)) instead of the O(L × R) loop.
This is what collapsed SMO's per-column property-bag mega-join (`sys.all_columns` LEFT JOIN `sys.types` ×2 + `sys.identity_columns` + `sys.computed_columns`, filtered to one table): a 200-user-table schema dropped that query from ~69 ms to ~9 ms, and — more importantly — its join cost went from super-linear in table count to flat.
The correlation-safety contract is narrow: **only** the catalog-view construction site sets `MaterializeOnce`, which is what lets a catalog view materialize *wherever it sits* — including the leftmost slot and the right side of an APPLY.
The same pass materializes a second family on a positional rule instead (a non-leftmost, non-APPLY derived table / CTE / view), and declines the generator-backed sources whose arguments bind in the enclosing FROM's scope; the full rule set is in [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration).
The strategy is guarded by `JoinStrategyTests.CatalogView_EquiJoin_TakesHashPath` (asserts the hash path via `JoinDiagnostics`) and the rowset by `CatalogViewTests.PerColumnBagQuery_MultiJoin_*`.
The complementary win — not materializing the non-selected objects' rows in the first place — is the predicate pushdown below.

**Predicate pushdown (perf).**
When a *pushdown-aware* catalog view is a FROM source and some top-level AND-conjunct is `<key> = <comparand>` — where `<key>` is one of the view's eligible columns (qualified to that source, or unqualified when it's the sole source) and `<comparand>` holds one value for the whole execution — the comparand is evaluated once per execution and passed to the view's `FilteredRowGenerator` so it enumerates only the matching object, one table's columns instead of every table's.
This is what real does too: its plan for the same query is an index seek on the underlying base table (`sysschobjs` / `sysiscols` / `syscolpars`) carrying OUTER REFERENCES, never a scan — real's catalog views are views over *indexed storage*, where the simulator's are projected on demand, and the pushdown is what stands in for the index.
This closes the last dominant term in SMO's per-column property-bag: at 200 user tables × 20 columns, the per-table `sys.columns` query drops from ~11.6 ms/query to ~0.08 ms/query (~143×).
Purely an optimization: the enclosing SELECT keeps applying the **full WHERE as a residual filter**, so the generator-side key match can only over-produce, never drop a row the predicate keeps.

- **Contract.**
  Detection is in `Selection.Execution.cs::DetectCatalogPushdowns`, run in `BuildSqlProjection` after the comma-join rewrite; it rebuilds each pushed source's `LateralPlan` via `Selection.ForCatalogView(view, db, column, comparand)`.
  The decision is compiled into the shared plan; the comparand *value* is resolved per execution (a variable / parameter that differs between runs re-evaluates each time — plan-cache safe).
  It composes with materialize-once: the pushed-down plan is what the `MaterializeUncorrelatedDeferredSources` pass runs once.
- **Which conjuncts count.** WHERE conjuncts, plus the ON conjuncts of **inner** joins — for an inner join the two are interchangeable, so an ON equality narrows the generator exactly as safely.
  An outer join's ON is excluded, and so is any source reachable only through one: dropping rows from a null-supplying side turns matched rows into null-extended ones, which the residual filter cannot undo.
- **Which sources count.** Any pushdown-aware source, not just the leftmost.
  A view reached through an inner join that keeps a full scan is not a missing optimization but the dominant cost of the whole query, because the scan repeats per execution of the enclosing body.
- **Constant for one execution** is the comparand test (`Selection.Execution.cs::IsConstantForOneExecution`), and it admits two shapes.
  *Row-independent* expressions (`Expression.IsRowIndependent`, default `false`): literals, variables, parameters, and pass-throughs of those (`Parenthesized` / `Cast` / `COLLATE` / arithmetic / `OBJECT_ID` of a row-independent argument); column references, subqueries and unrecognized nodes stay out.
  And a **reference qualified by a name that belongs to no source of this query** — necessarily an enclosing query's column, and a correlated body re-executes once per outer row, so it is fixed for the duration of each execution.
  That second shape is the load-bearing one: `WHERE ic.object_id = t.object_id` inside a `CROSS APPLY` is what a catalog-introspection query is *made* of, and without it the body regenerates every view it names once per outer row.
  An **unqualified** reference is never taken — it could bind to a local source and vary per row, and since the pushdown only narrows, a wrong narrowing loses rows the residual can't add back.
- **Transitive hop.** Given `ic.object_id = t.object_id` in WHERE and `ic.object_id = col.object_id` in an inner join's ON, `col.object_id` is fixed too, and the comparand carries across.
  Real derives the same seek; without it the joined side stays a full scan and dominates whatever the direct seek saved.
  One hop only, and only between two pushdown-aware sources on a pushdown column each.
- **NULL comparand** (`= NULL`, or `OBJECT_ID` of a missing object) yields no rows — `col = NULL` is UNKNOWN for every candidate, which is exactly what the residual filter would give.
- **Eligible views × keys** (`CatalogView.PushdownColumns` / `FilteredRowGenerator`, registered via the `SysP` helper): `sys.columns` / `sys.all_columns` (`object_id` — the headline), `sys.indexes` (`object_id`), `sys.index_columns` (`object_id`), `sys.parameters` / `sys.all_parameters` (`object_id`), `sys.extended_properties` (`major_id`).
  Each generator skips a non-matching object's inner loop instead of materializing then discarding it.
  A 3-part cross-database reference (`otherdb.sys.columns`) pushes through the same seam (the generator's target database rides `FromSource.BackingCatalogDatabase`).
- **Diagnostics.**
  `CatalogPushdownDiagnostics.Sink` (opt-in `[ThreadStatic]`, mirroring `IndexSeekDiagnostics`) records `Seek(view.column)` on a narrowed scan, `SeekEmpty(view.column)` on a NULL comparand, and `Scan(view)` when an eligible view runs its full generator.
  `CatalogPushdownTests` (internal) asserts the path fired (or correctly didn't); `CatalogPushdownResultTests` (public) asserts result parity.
- **Residual gaps** (correctness-neutral — they only leave the full-scan cost in place): name-equality (`WHERE name = …`) isn't pushed; `sys.objects` / `sys.all_objects` aren't pushed (a catalog `object_id` there can be a constraint id nested under a non-matching parent table, so a table-level skip would be unsound and a per-row filter saves little — the view already emits one row per object); `sys.foreign_keys` (two candidate key columns) is unpushed; the transitive hop is one level and doesn't chain across three sources.

**Statement-scoped materialization (perf).**
A catalog view that *isn't* narrowed to a seek is still projected only once per statement: the first read that drains the sequence to completion stores the encoded rows on `StatementContext.CatalogViewRows`, keyed by view and target database, and every later read in the same statement is served from there.
The statement is the right scope because that is the span over which a metadata view's content is fixed — DDL runs as its own statement, and the session identity the visibility filter reads can't change mid-statement either.
Only a **fully drained** sequence is stored, so a `TOP 1` or `EXISTS` read keeps streaming and stops early instead of paying to materialize the whole view.
The **dynamic management views are excluded** (`CatalogView.StableWithinStatement`, off the `dm_` prefix): they report live runtime state — locks held, sessions connected, page counts — that a statement moves as it runs, so a read of one has to see it as it stands.
This is the backstop for what pushdown can't narrow; the two together are what took a real catalog-introspection query over a 300-table database from a 30-second timeout to well under a second.

**Where the code lives:** `BuiltInResources` is a `partial class` split across topical files `BuiltInResources.<Topic>.cs`.
The registrations are grouped by topic into per-file `Register<Topic>(views)` methods — `CoreObjects`, `ColumnFamily`, `Programmable` (incl. the `INFORMATION_SCHEMA.*` views), `ConstraintsAndTriggers`, `Indexes`, `Security`, `FullTextXmlSpatial`, `ServerAndDatabases` — which the root `BuiltInResources.cs` bootstrap (`BuildCatalogViews`) invokes in registration order.
Each view's `Sys("name", columns, rows)` (or `Iso(...)` for `INFORMATION_SCHEMA`) registration is colocated in the same partial as its row-provider enumerator and any view-private helpers; the local `Sys` / `Iso` helpers are redeclared per `Register` method.
Shared statics (`nvarchar60Catalog` / `nvarchar128Catalog` / `lsnNumeric` / `charTwo` / `charOne` / `notMsShipped` / `defaultCollation` / `EmptyCatalogRows`) and the `systypes` system heap table live in the root and `BuiltInResources.SystemTables.cs`.
**New catalog work lands in the matching topic partial** (add the `Sys(...)` call to its `Register` method and the enumerator to the same file); a genuinely new topic gets a new `BuiltInResources.<Topic>.cs` + a `Register<Topic>` call in the bootstrap.

Views:
- **`sys.schemas`** projects `name sysname`, `schema_id int`, `principal_id int NULL`.
  Lists the thirteen fixed system schemas every real database ships — dbo (1), guest (2), INFORMATION_SCHEMA (3), sys (4), and one per fixed database role (db_owner 16384 … db_denydatawriter 16393, 16388 skipped) — plus every user CREATE SCHEMA addition.
  Only dbo / INFORMATION_SCHEMA / sys are materialized as object-hosting `Schema` instances; guest and the nine role-named schemas exist purely for catalog fidelity and are injected by the generator (`FixedCatalogOnlySchemas`), so `SCHEMA_ID('db_owner')` does not resolve.
  `principal_id` matches real (probe-confirmed against SQL Server 2025): every fixed schema is owned by the like-id principal (`principal_id = schema_id` for ids ≤ 4 or ≥ 16384), and a user schema (ids 5..16383) reports the owner it carries — dbo (`principal_id = 1`) unless a `CREATE SCHEMA … AUTHORIZATION` named another (see [`schemas.md`](schemas.md#create-schemas-owner-and-its-element-list)).
  JDBC's `DatabaseMetaData.getSchemas` reads this view, so the thirteen-row shape is what tooling sees.
- **`sys.tables`** projects user heap tables only: `object_id`, `name sysname`, `schema_id`, `type char(2)` (always `'U '` — trailing-space padded, probe-confirmed), `type_desc nvarchar(60)` (`USER_TABLE`), `create_date datetime`, `modify_date datetime`, `is_ms_shipped bit` (always 0), `temporal_type tinyint` (0=NON_TEMPORAL, 1=HISTORY, 2=SYSTEM_VERSIONED), `temporal_type_desc nvarchar(60)`, `history_table_id int` (NULL for non-temporal and history tables themselves), and the table-flavor flags SMO's Object-Explorer Tables node filters on: `is_memory_optimized` / `is_filetable` / `is_external` / `is_node` / `is_edge bit` and `ledger_type tinyint` — all constant 0 (none of those table kinds are modeled; `ledger_type` 0 = NON_LEDGER_TABLE, probe-confirmed non-null on SQL Server 2025).
  The **"Script Table as → CREATE To"** SMO query reads a further batch, all probe-confirmed constants: `principal_id int` (NULL — no explicit table owner modeled), `uses_ansi_nulls bit` (the table's creation-time `SET ANSI_NULLS` capture — see [Creation-time SET-option capture](#creation-time-set-option-capture)), `is_dropped_ledger_table bit` (0), `lock_escalation tinyint` / `lock_escalation_desc nvarchar(60)` (0 / `TABLE`), `durability tinyint` / `durability_desc nvarchar(60)` (0 / `SCHEMA_AND_DATA`), `ledger_view_id int` (NULL — ledger unmodeled), `filestream_data_space_id int` (NULL), and `lob_data_space_id int` (the filegroup holding the table's LOB allocation unit: the single PRIMARY filegroup `1` once any column is LOB-eligible — `varchar`/`nvarchar`/`varbinary(MAX)`, `text`/`ntext`/`image`, `xml`, `geography`/`geometry` — and 0 otherwise; probe-confirmed, with `hierarchyid` and `sql_variant` leaving it 0).
  `is_replicated bit` (nullable in real SQL Server; constant 0 — replication isn't modeled) rounds out the set the SMO **Table property-bag** reads (projected `AS [Replicated]`); a single missing column fails the whole bag query Msg 207 and every Table property errors.
  `lock_on_bulk_load bit` (constant 0 — BULK INSERT / bcp table-lock behavior isn't modeled; the fresh-table default) is read by DacFx's bacpac-export reverse-engineering as `CAST([st].[lock_on_bulk_load] AS bit)`.
  `history_retention_period int` / `history_retention_period_unit int` / `history_retention_period_unit_desc nvarchar(60)` carry a system-versioned table's `HISTORY_RETENTION_PERIOD` — -1 / -1 / `INFINITE` until one is set, NULL on history and non-temporal tables (unit codes DAY 3, WEEK 4, MONTH 5, YEAR 6, all probe-confirmed) — see [`temporal-tables.md`](temporal-tables.md).
- **`sys.objects`** is the superset: one row per `HeapTable` plus one row per constraint of every family — `KeyConstraint` (type `PK` / `UQ`), `DefaultConstraint` (`D `), `CheckConstraint` (`C `) and `ForeignKey` (`F `) — with `parent_object_id` linking to the owning table, so a tool enumerating a table's constraints through this view alone sees all five.
  Constraint object_ids allocate from the same `Database.AllocateObjectId` counter as tables, so every constraint gets a globally-unique id that `sys.objects.object_id` surfaces and `OBJECT_ID('<constraint name>')` resolves (see [`schemas.md`](schemas.md#object-identifiers--object_id)).
  Carries `principal_id int` (always NULL — no explicit object owner modeled, ownership follows the schema); SMO's Object-Explorer function / procedure enumeration reads `ISNULL(o.principal_id, OBJECTPROPERTY(o.object_id, 'OwnerId'))` off `sys.all_objects` to resolve the owner.
- **`sys.columns`** projects per-column metadata: `object_id`, `name sysname`, `column_id` (1-based), `system_type_id tinyint`, `user_type_id int`, `max_length smallint` (byte-length — `nvarchar(50)→100`, `char(5)→5`, `-1` for the MAX form, `16` for text/ntext/image LOB pointers, `256` for sysname), `precision` / `scale tinyint` (decimal/numeric carry their declared (p,s); date/time fractional types follow `(time(N): 8+N, N)` / `(datetime2(N): 19+N, N)` / `(datetimeoffset(N): 26+N, N)`; 0 for everything else), `is_nullable` / `is_identity` / `is_computed bit`, `collation_name sysname` (set only for string types), `is_sparse bit` (the column's own marker, which `ALTER COLUMN … ADD | DROP SPARSE` moves — metadata here, since the row encoder already omits a NULL from the row), and the SMO column-node filter columns `is_xml_document` / `is_column_set` / `is_dropped_ledger_column bit` (all 0), `xml_collection_id int` (the bound collection's `xml_collection_id` for an `xml(collection)` column, 0 for untyped `xml` and every non-xml column), `vector_dimensions int` / `vector_base_type_desc nvarchar(20)` (both NULL — no vector columns modeled).
  The **"Script Table as → CREATE To"** column query reads a further batch: `is_ansi_padded bit` (1 for char/varchar/nchar/nvarchar/binary/varbinary — every table is ANSI_PADDING ON — 0 for all other types including the LOB text/ntext/image), `default_object_id int` (the column's DEFAULT constraint object_id, 0 when none), `generated_always_type tinyint` (0/1/2 for none/ROW-START/ROW-END, from the temporal period marker), `is_hidden bit` (a HIDDEN period column), and the always-NULL/0/constant `column_encryption_key_id` / `encryption_type int` / `encryption_algorithm_name sysname` (Always Encrypted unmodeled), `graph_type int` (NULL), `is_filestream` / `is_masked bit` (0), `is_rowguidcol bit` (1 for a column declared `ROWGUIDCOL` at CREATE TABLE, else 0 — `HeapColumn.IsRowGuidCol`; DacFx reads it to re-emit `IsRowGuidColumn=True`), `rule_object_id int` (0), and `ledger_view_column_type int` / `ledger_view_column_type_desc nvarchar(60)` (NULL — ledger unmodeled).
  The replication / added-metadata columns SSMS's **Table-Designer** column query reads round out the shape: `is_replicated` / `is_non_sql_subscribed` / `is_merge_published` / `is_dts_replicated` / `is_data_deletion_filter_column bit` (all 0 — replication and retention-policy filters unmodeled), `generated_always_type_desc nvarchar(60)` (the `generated_always_type` enum text — `NOT_APPLICABLE` / `AS_ROW_START` / `AS_ROW_END`), `encryption_type_desc nvarchar(64)` / `column_encryption_key_database_name sysname` (NULL — Always Encrypted), `graph_type_desc nvarchar(60)` (NULL — graph tables unmodeled), and `vector_base_type tinyint` (NULL — vector columns unmodeled).
  The added columns are appended to the existing shape (name-addressable; sys.columns' own ordinal order needn't match real, and the SqlBulkCopy 7.x metadata query orders by the *destination* table's columns).
  Backed by `SqlType.SystemTypeId` (byte-typed switch on `this` matching real SQL Server's `sys.types.system_type_id`) and `SqlType.UserTypeId` (== `SystemTypeId` except `sysname=256`).
  `system_type_id` covers the 22 base types modeled.
- **`sys.all_columns`** shares `sys.columns`'s exact shape and row generator — a deliberate **user-object-parity** shortcut: real SQL Server's `sys.all_columns` additionally surfaces system objects' negative-`object_id` columns, but SMO's Object-Explorer Tables node correlates only on user tables' object_ids (`HasSparseColumn` / `HasXmlData` / `HasSpatialData` probes: `select … from sys.all_columns where object_id = tbl.object_id …`), so the identical user-column row set suffices.
  `is_sparse` reads off it.
  Divergence: system/DMV columns don't appear.
- **`sys.table_types`** projects one row per user-defined table type (`CREATE TYPE … AS TABLE`), keyed by `type_table_object_id int`: `name sysname`, `is_user_defined bit` (1), `schema_id int`, `user_type_id int`, `is_memory_optimized bit` (constant 0 — no memory-optimized table types modeled).
  Because `sys.table_types` derives from `sys.types`, it also carries the sys.types column set — all **constant for a table type** (probe-confirmed SQL Server 2025): `system_type_id tinyint` (243), `max_length smallint` (-1), `precision` / `scale tinyint` (0), `collation_name sysname` (NULL), `is_nullable bit` (0), `is_assembly_type bit` (0), `is_table_type bit` (1), `principal_id int` (NULL).
  These are **appended** after the original six columns (not slotted into real SQL Server's `sys.table_types` position) so positional consumers of the first six keep working; SMO reads every column by name.
  SMO's UDTT property-bag / Script query reads `tt.max_length` / `is_nullable` / `collation_name` / `principal_id`; its SSMS Object-Explorer index / key / FK sub-node queries branch on `(SELECT tt.is_memory_optimized FROM sys.table_types tt WHERE tt.type_table_object_id = i.object_id)`.
  Generated by `EnumerateSysTableTypes`.
- **`sys.types`** projects `name sysname`, `system_type_id tinyint`, `user_type_id int`, `schema_id int`, `is_user_defined` / `is_table_type` / `is_nullable` / `is_assembly_type bit`, and `max_length smallint` / `precision` / `scale tinyint`.
  `is_assembly_type` is 1 only for the CLR-backed system types (`hierarchyid` / `geometry` / `geography`) and 0 for every other built-in, table type, and scalar alias — SMO's column-node query reads `baset.is_assembly_type` off it to select the base-type join arm.
  The `max_length` / `precision` / `scale` triple (SMO's User-Defined-Data-Types node reads it) mirrors the probe-confirmed `systypes` `length` / `xprec` / `xscale` columns for system types (`int` 4/10/0, `nvarchar` 8000/0/0, `sql_variant` 8016/0/0), is `-1`/0/0 for table types, and comes from the underlying built-in's `sys.columns` metadata for scalar alias types (UDDTs — e.g. an alias of `nvarchar(50)` reports byte width 100).
  Also projects (appended after the triple) `collation_name sysname` (database collation for the character-family types + string alias types, NULL otherwise), `principal_id int` (NULL), `default_object_id int` (0 — no sp_bindefault model), and `rule_object_id int` (0 — no sp_bindrule model; probe-confirmed real reports 0 for unbound types; DacFx's UDDT reverse-engineering query INNER JOINs `sys.objects` on it).
  Generated by `EnumerateSysTypes`.
- **`sys.data_spaces`** enumerates `Database.Filegroups` (seeded PRIMARY = `data_space_id` 1; additional filegroups registered by the bacpac loader's `SqlFilegroup` dispatch get sequential ids from 2) ordered by id: `name sysname`, `data_space_id int` (PRIMARY = 1 — the same id every `sys.indexes` row reports, so SMO's `LEFT JOIN sys.data_spaces dsidx ON dsidx.data_space_id = idx.data_space_id` resolves), `type char(2)` (`FG`), `type_desc nvarchar(60)` (`ROWS_FILEGROUP`), `is_default bit` (1 for PRIMARY, 0 for others), `is_system bit` (0).
  No physical file / placement model — every heap lives on PRIMARY regardless, so no table/index reports a non-PRIMARY `data_space_id`.
  Partition schemes (`type = 'PS'` — what SMO's `IsPartitioned` probe compares against) aren't modeled, so `type` is always `FG`.
  Generated by the shared `EnumerateFilegroupRows`.
- **`sys.partitions`** (11-column, probe-confirmed SQL Server 2025): one row per `(object_id, index_id)` that `sys.indexes` reports — the heap (`index_id 0`) or clustered entry (`index_id 1`, a clustered PK / UNIQUE constraint or `CREATE CLUSTERED INDEX`) plus every nonclustered index — always with `partition_number = 1` (a single, unpartitioned partition per index/heap).
  `rows bigint` carries the table's **live** row count (`HeapTable.Heap.RowCount`), projected at read time so it tracks same-batch INSERT/DELETE.
  `partition_id` / `hobt_id bigint` are synthetic-deterministic (`(object_id << 16) | index_id`; distinct per index, **not** byte-matching SQL Server's allocation-unit ids).
  **Compression is unmodeled**, so `data_compression tinyint` = 0 (`NONE`) / `data_compression_desc` = `'NONE'` and `xml_compression bit` = 0 / `xml_compression_desc varchar(3)` = `'OFF'` on every row — a divergence from compression-enabled databases (e.g. WWI-Full uses PAGE compression on Sales.Invoices, so SMO's `HasCompressedPartitions` reads True there but False here).
  `filestream_filegroup_id smallint` = 0.
  Shares the `(table, index_id)` identity stream `EnumerateTableIndexIdentities` with `sys.stats` — a per-database flattening of `HeapTable.IndexIdentities()`, the single index-id allocation authority (see [`indexes.md`](indexes.md#index-id-allocation)); generated by `EnumerateSysPartitions`.
- **`sys.allocation_units`** (8-column, probe-confirmed SQL Server 2025): the storage-allocation surface SSMS's Database Properties → General page joins to compute DbSize / SpaceUsed (`sys.partitions p JOIN sys.allocation_units a ON p.partition_id = a.container_id`).
  One **IN_ROW_DATA** row (`type` 1) per `sys.partitions` row, with `container_id` = that partition's synthetic `partition_id` (the join key), plus one **LOB_DATA** row (`type` 2) per table that has off-row LOB pages, attached to the base heap/clustered partition (the simulator's LOB-page chain is per-table, not per-index).
  **ROW_OVERFLOW_DATA (`type` 3) isn't surfaced** — the row encoder pushes oversize columns into the LOB chain, so there's no separate row-overflow allocation.
  `total_pages` / `used_pages` / `data_pages bigint` read the table's **live** heap page count (`Heap.Pages.Count` for IN_ROW, `Heap.LobPages.Count` for LOB), projected at read time so they track same-batch INSERT/DELETE; IN_ROW reports all three equal (no separate index/IAM overhead modeled), LOB reports `data_pages` = 0 (matching real).
  `data_space_id` = 1 (the single PRIMARY filegroup); `allocation_unit_id` is synthetic-deterministic (`(container_id << 8) | type`; distinct per partition/type, **not** SQL Server's real id).
  **Divergence**: because separate nonclustered-index storage isn't modeled, every index partition's IN_ROW unit reports the *base heap's* page count — an over-count for multi-index tables.
  **Self-consistency contract**: `SumDataFilePages` (Σ `total_pages` over all units) is what `sys.database_files` / `sys.master_files` size the data file from (via `ComputeDataFileSizePages` = used + headroom, floored at 640) and what `FILEPROPERTY(<db>_Data, 'SpaceUsed')` returns, so SSMS's `SpaceAvailable = DbSize − SpaceUsed` (data-file size − Σ `total_pages`) is always ≥ 0.
  Shares `EnumerateTableIndexIdentities` with `sys.partitions`; generated by `EnumerateSysAllocationUnits` (raw stream `EnumerateAllocationUnitData`).
  See [`heap-storage.md`](heap-storage.md) for the underlying page model and [`scalars.md`](scalars.md) for `FILEPROPERTY`.
- **`sys.dm_db_partition_stats`** (14-column, probe-confirmed SQL Server 2025): the per-partition page/row-count DMV SMO's Table **IndexSpaceUsed** query sums.
  One row per `(object_id, index_id)` that `sys.partitions` / `sys.allocation_units` report — `partition_number = 1`, `partition_id` = the same synthetic id those views use (`(object_id << 16) | index_id`, the join key).
  Page counts derive from the table's **live** heap page count, kept **consistent with `sys.allocation_units`**: `in_row_data_page_count` / `in_row_used_page_count` / `in_row_reserved_page_count bigint` = `Heap.Pages.Count` on every partition; `lob_used_page_count` / `lob_reserved_page_count` = `Heap.LobPages.Count` **only on the base heap/clustered partition** (`index_id 0`/`1`, the first identity per table — matching allocation_units' per-table LOB attachment), 0 on nonclustered partitions; `row_overflow_used_page_count` / `row_overflow_reserved_page_count` = 0 (the row encoder pushes oversize columns into the LOB chain, so no separate row-overflow allocation — the same reason allocation_units omits `type` 3).
  `used_page_count` / `reserved_page_count` are the `in_row + lob + overflow` row-level sums.
  `row_count bigint` = live `Heap.RowCount`.
  **Cross-view consistency contract**: a table's Σ `used_page_count` across its partitions equals its Σ allocation-unit `total_pages` (both derive from the same heap page counts) and never exceeds `SumDataFilePages`, so SMO's `IndexSpaceUsed = (Σ used_page_count − base-partition (in_row+lob+overflow)) × 8 KB` stays ≥ 0.
  (**DataSpaceUsed** reads `sys.allocation_units` instead — the `is_memory_optimized = 0` arm joins `indexes ⋈ partitions ⋈ allocation_units`.)
  Generated by `EnumerateSysDmDbPartitionStats`, sharing `EnumerateTableIndexIdentities` with `sys.partitions` / `sys.allocation_units`.
- **`sys.dm_db_xtp_table_memory_stats`** (5-column, probe-confirmed SQL Server 2025, always **empty**): the in-memory-OLTP per-table memory DMV.
  Memory-optimized tables aren't modeled, so zero rows via the shared `EmptyCatalogRows` (`object_id`, `memory_allocated_for_table_kb` / `memory_used_by_table_kb` / `memory_allocated_for_indexes_kb` / `memory_used_by_indexes_kb bigint`).
  **Load-bearing for parse binding, not data**: SMO's Table DataSpaceUsed / IndexSpaceUsed queries branch on `is_memory_optimized` and reference this view in the never-taken-but-compile-bound memory-optimized arm (`ELSE isnull((select tms.memory_used_by_table_kb from sys.dm_db_xtp_table_memory_stats tms …), 0.0)`); before it was modeled the whole statement failed Msg 208 and the property errored.
- **`sys.stats`** (17-column, probe-confirmed SQL Server 2025): one row per index `sys.indexes` reports **excluding the heap** (`index_id 0` carries no statistic), with `stats_id = index_id` and `name` = the index/constraint name (real SQL Server's index-backing statistic shares the index's id and name).
  **Auto-created column statistics (`_WA_Sys_*`) aren't modeled** — the simulator has no stats lifecycle — so `auto_created` / `user_created` / `no_recompute` are always 0 and no column-only stats rows appear (SMO's `NoAutomaticRecomputation` reads `ISNULL(no_recompute, 0)` = 0).
  `stats_generation_method` = 0 / `_desc` = `'Sort based statistics'`; the filter / temporary / incremental columns are 0 or NULL, and `replica_role_id` / `_desc` are `1` / `PRIMARY` — the value a stand-alone instance reports (probe-confirmed), with `replica_name` still NULL.
  Generated by `EnumerateSysStats`.
- **`sys.stats_columns`** (4-column: `object_id` / `stats_id` / `stats_column_id` / `column_id`): one row per **KEY** column of each index-backed statistic `sys.stats` reports (`stats_id = index_id`), mirroring `sys.index_columns`'s key-column rows exactly — `stats_column_id` = the key ordinal (1..N), `column_id` = the `sys.columns` id — but **omitting INCLUDE columns** (a statistic covers only the index key; probe-confirmed against SQL Server 2025 WWI: `stats_columns` count per index-backed stat equals the index's `is_included_column = 0` count, e.g. `IX_Sales_Invoices_ConfirmedDeliveryTime` = 1 key column despite an INCLUDE).
  The heap (`index_id 0`) carries no statistic and is skipped, like `sys.stats`.
  Auto-created column statistics (`_WA_Sys_*`) aren't modeled, so no column-only `stats_columns` rows appear — the same divergence as `sys.stats`.
  Generated by `EnumerateSysStatsColumns` (shared key-column emission with `EmitStatsColumns`), in `BuiltInResources.Indexes.cs`.
- **`sys.internal_tables`** (18-column), **`sys.hash_indexes`** (21-column), **`sys.json_indexes`** (21-column), **`sys.index_resumable_operations`** (13-column), **`sys.selective_xml_index_paths`** (20-column), **`sys.filetable_system_defined_objects`** (2-column) — index-feature views for capabilities the simulator doesn't model (system internal tables, memory-optimized hash indexes, JSON indexes, resumable index builds, selective XML indexes, FileTables).
  Each ships the **full probe-confirmed column shape (SQL Server 2025) but zero rows** via the shared `EmptyCatalogRows`, following the AlwaysOn-DMV precedent so SMO's index-scripting mega-query — which `LEFT JOIN`s all six and reads specific columns (`hi.bucket_count`, `ji.optimize_for_array_search`, `op.state`, `it.name`/`it.parent_id`, `indexedpaths.name`, `filetableobj.object_id`) — resolves every reference without Msg 207/208 and returns the correct rows.
  Real WWI populates `sys.internal_tables` (LOB/full-text/ledger internals) but none match SMO's `extended_index_%` name filter for a non-spatial-indexed table, so the empty view is behavior-correct for that path.
- **`sys.all_objects`** shares `sys.objects`'s exact shape and row generator — user-object parity, like `sys.all_columns`.
  SMO's "Script Table as → CREATE To" index-scripting query `LEFT JOIN`s it by the synthetic `extended_index_%` object name (spatial-index internals, never present here), so the identical user-object row set suffices.
- **`sys.all_views`** / **`sys.all_sql_modules`** share `sys.views` / `sys.sql_modules`'s shape and row generator — user-object parity, same precedent as `sys.all_objects`.
  SMO's Object-Explorer Views enumeration filters `sys.all_views` on `v.type = 'V'` and reads `create_date` / `modify_date datetime`, `principal_id int` (NULL), `is_ms_shipped bit` (0), and `ledger_view_type tinyint` (0 — ledger unmodeled) alongside the `type char(2)` = `'V '` + `type_desc nvarchar(60)` = `VIEW` pair and the load-bearing `object_id` / `name` / `schema_id` / `with_check_option` / `is_date_correlation_view` subset; its Script-As trigger query `LEFT JOIN`s `sys.all_sql_modules` to read `uses_native_compilation` / `is_schema_bound` off the trigger body.
  The simulator ships no system views / system modules with stored T-SQL, so the union is just the user objects.
- **`sys.assemblies`** (10-column, SQL Server 2025 shape: `name` / `principal_id` / `assembly_id` / `clr_name` / `permission_set` (+`_desc`) / `is_visible` / `create_date` / `modify_date` / `is_user_defined`) — one row per `CREATE ASSEMBLY` registration plus the `Microsoft.SqlServer.Types` system row real always carries.
  SMO's Script-As trigger query `LEFT JOIN`s it via `sys.assembly_modules.assembly_id` to pull a CLR trigger's assembly name; only CLR scalar functions are modeled, so triggers never appear.
  See [`clr-assemblies.md`](clr-assemblies.md).
- **`sys.assembly_types`** projects the three CLR-backed system types (`hierarchyid` / `geometry` / `geography`), which the simulator models and whose `sys.types.is_assembly_type` already reads 1.
  Probe-confirmed shape (`name sysname`, `system_type_id tinyint` = 240, `user_type_id int` = 128/129/130, `schema_id int` = 4/sys, `principal_id int` NULL, `max_length smallint` = 892 for hierarchyid / -1 for the spatial pair, `precision` / `scale tinyint` 0, `collation_name` NULL, `is_nullable` 1, `is_user_defined bit` = 0, `is_assembly_type bit` = 1, `default_object_id` / `rule_object_id int` = 0, `assembly_id int` = 1, `assembly_class sysname`, `is_binary_ordered bit` = 1 for hierarchyid / 0 for the spatial pair, `is_fixed_length bit` = 0, `prog_id nvarchar(40)` NULL, `assembly_qualified_name nvarchar(4000)` = the full CLR AssemblyQualifiedName (`Microsoft.SqlServer.Types.Sql{HierarchyId,Geometry,Geography}, Microsoft.SqlServer.Types, Version=11.0.0.0, …`), `is_table_type bit` = 0).
  Column order matches real (SSMS's **Table-Designer** UDT query self-joins on `is_user_defined = 1` and left-joins `sys.objects` on `default_object_id` / `rule_object_id`; the self-join yields no rows since all three report `is_user_defined = 0`).
  SMO's User-Defined Types node reads `name` / `assembly_id` / `is_user_defined` / `schema_id` and filters `is_user_defined = 1` — so these system rows surface nothing in that node (matching real SQL Server, which lists only user CLR types).
  No user-defined CLR types are modeled.
  Generated by `EnumerateAssemblyTypes`.
  The `Microsoft.SqlServer.Types` row in `sys.assemblies` (assembly_id 1) is what this view joins against.
- **`sys.plan_guides`** — plan guides aren't modeled, so an **empty view** (shared `EmptyCatalogRows`) with the SQL Server 2025 shape (`plan_guide_id int` / `name sysname` / `create_date` / `modify_date datetime` / `is_disabled bit` / `query_text` / `scope_type tinyint` (+`_desc`) / `scope_object_id int` / `scope_batch` / `parameters` / `hints nvarchar`).
  SMO's Object-Explorer Plan Guides node reads `name` / `is_disabled`.
- **`sys.database_scoped_configurations`** (5-column, server-scope static defaults independent of the database argument): `configuration_id int`, `name sysname`, `value` / `value_for_secondary sql_variant`, `is_value_default bit`.
  `value` is a **first-class `sql_variant`** (see [the sql_variant type](#the-sql_variant-type) below) carrying each knob's real per-row inner base type — probe-confirmed against SQL Server 2025: `MAXDOP` int, the bit-valued knobs bit.
  A bit knob therefore reads back as `bool` (SSMS's ON/OFF) and DacFx's `(bool)reader[value]` unbox on the `LEGACY_CARDINALITY_ESTIMATION` row succeeds — the fix that unblocked `sqlpackage /Action:Export`'s `SqlDatabaseOptions` reverse-engineering (the earlier flat-`nvarchar` `value` threw `Unable to cast object of type 'System.String' to type 'System.Boolean'` client-side on that row).
  `value_for_secondary` is a variant NULL on every row; SSMS's `ISNULL(value_for_secondary, 'PRIMARY')` projection stays `sql_variant` (ISNULL fixes the result to its first argument's type) and reads back as the wrapped string fallback.
  Four fresh-database-default rows (`MAXDOP` int 0, `LEGACY_CARDINALITY_ESTIMATION` bit false, `PARAMETER_SNIFFING` bit true, `QUERY_OPTIMIZER_HOTFIXES` bit false) — **static catalog data, not a live settings model** (`ALTER DATABASE SCOPED CONFIGURATION` changes aren't reflected).
  Built by `BuiltInResources.BuildDatabaseScopedConfigurationRows`.

### The `sql_variant` type

`SqlType.SqlVariant` / `SqlVariantSqlType` (`Storage/SqlVariantType.cs`) model SQL Server's `sql_variant` as a wrapper: a variant `SqlValue` stores its inner `SqlValue` in the reference slot (`SqlValue.FromVariant` / `AsVariantInner`), so one variant-typed column surfaces a per-row inner base type.
Contracts:

- **Storage codec** — the row encoder/decoder round-trips a variant column via a simulator-internal inner-type descriptor (1-byte kind + per-family params — precision/scale for decimal, scale for the fractional temporals, collation-name + declared length for strings) followed by the inner value's own bytes.
  The set of storable inner types is everything except MAX/LOB/xml/spatial/hierarchyid/rowversion (enforced by the codec's default throw).
  This is what lets the DSC catalog view — which re-encodes each generator row through `RowEncoder` — carry variant cells.
- **Coercion** — `CAST(x AS sql_variant)` wraps (`CoerceTo` a variant target); `CAST(variant AS <concrete>)` unwraps the inner and re-coerces.
  `ISNULL(variant, x)` / `COALESCE(variant, …)` stay `sql_variant` (ISNULL fixes to the first arg's type; `Promote(variant, X) → variant`).
- **Comparison** — a variant operand against a base-typed one unwraps to its inner value before the promote-and-compare (`CompareValuesPromoted`), so `value = 0` unwraps a variant int/bit and compares numerically; a both-variant pair follows the datatype-family rules.
  Identity and ordering (Equals / GetHashCode / CompareTo for GROUP BY / DISTINCT / ORDER BY) follow SQL Server's two-level family comparison — family rank, then value within the family, with cross-type equal values collapsing into one bucket (`Storage/SqlVariantOrdering.cs`; probed rules in [`scalars.md`](scalars.md#sql_variant-expression-semantics)).
- **Arithmetic / concatenation** — probe-confirmed against SQL Server 2025: `variant + non-variant` raises **Msg 257** (`Implicit conversion from data type sql_variant to <target> is not allowed. Use the CONVERT function to run this query.`); `variant + variant` and `string + variant` raise **Msg 402** (`… incompatible in the <op> operator`).
  `SqlType.PromoteForArithmetic` is the single source; a runtime guard in `IntegerArithmetic` routes through it so `Run`-time and projection-schema errors agree.
- **TDS wire** — a `sql_variant` result column emits COLMETADATA type `0x62` (SSVARIANTTYPE) and the MS-TDS 2.2.5.5.3 per-value body; RPC `sql_variant` parameters ship in both directions, TVP variant columns included (see [`tds-endpoint.md`](tds-endpoint.md)).

**Consumers surfacing true `sql_variant`:** the `SERVERPROPERTY` / `SESSIONPROPERTY` / `CONNECTIONPROPERTY` / `COLLATIONPROPERTY` / `LOGINPROPERTY` / `SESSION_CONTEXT` / `SQL_VARIANT_PROPERTY` / `DATABASEPROPERTYEX` / `OBJECTPROPERTYEX` scalar family (see [`scalars.md`](scalars.md)), `sys.configurations`.`value*`, `sys.database_scoped_configurations`.`value*`, `sys.sequences` (`start_value` / `increment` / `minimum_value` / `maximum_value` / `current_value` / `last_used_value`, inner = the sequence's declared type), `sys.identity_columns` (`seed_value` / `increment_value` / `last_value`, inner = the identity column's declared type), `sys.parameters.default_value` (always a NULL variant), `sys.partition_range_values.value` (always empty), and the `sys.symmetric_keys` / `sys.asymmetric_keys` sql_variant columns (`key_thumbprint` / `cryptographic_provider_algid`, always empty).
No inner-type substitution remains.
Remaining quirk: a decimal-declared sequence's inner reports BaseType `numeric` rather than real's `decimal` (the single-decimal-family naming divergence).
- **`sys.filegroups`** is the row-filegroup subset of `sys.data_spaces` (same `Database.Filegroups` enumeration via `EnumerateFilegroupRows` — PRIMARY plus any bacpac-registered filegroup like WWI's `[USERDATA]`) plus `filegroup_guid uniqueidentifier` (NULL), `log_filegroup_id int` (NULL), `is_read_only` / `is_autogrow_all_files bit` (0).
  SMO's CREATE-scripting index/full-text queries read it; DacFx's bacpac export re-emits a standalone `SqlFilegroup` element per non-PRIMARY row.
- **`sys.periods`** projects one row per table carrying a `PERIOD FOR SYSTEM_TIME` declaration (`HeapTable.PeriodColumns`), **history siblings excluded** (they hold no period of their own even though the simulator copies `PeriodColumns` onto them for the `FOR SYSTEM_TIME` query machinery).
  Columns: `name sysname` (always `SYSTEM_TIME`), `period_type tinyint` (1), `period_type_desc nvarchar(60)` (`SYSTEM_TIME_PERIOD`), `object_id int`, `start_column_id` / `end_column_id int` (the 1-based ordinals of the ROW START / ROW END columns).
  See [`temporal-tables.md`](temporal-tables.md).
- **`sys.computed_columns`** / **`sys.identity_columns`** project the load-bearing subset SMO's column-scripting query `LEFT JOIN`s (real SQL Server surfaces the full `sys.columns` shape plus a few extra columns).
  `computed_columns`: `object_id` / `name` / `column_id` (the stable id, per [Stable column ids](#stable-column-ids)) / `is_computed` (1) / `is_persisted bit` / `is_nullable bit` / `uses_database_collation bit` (1 — real reports 1 for every computed column, purely numeric expressions included) / `definition nvarchar(max)` — **`definition` carries the captured parenthesized source text** of the `AS (…)` body (`HeapColumn.ComputedDefinition`, captured at CREATE TABLE / ALTER TABLE ADD via `ParserContext.SourceTextFrom`, wrapped in a single paren pair when not already fully parenthesized; the bacpac loader inherits it by replaying `ALTER TABLE ADD col AS <script>`).
  Re-parseable but not byte-identical to SQL Server's bracket-normalized form.
  DacFx / SMO re-emit a valid computed-column DDL from it.
  `identity_columns`: `object_id` / `name` / `column_id` (the stable id for a table's column; a table type's stays its position, since a table type's columns can't be dropped) / `seed_value` / `increment_value` / `last_value` (all three **`sql_variant`** carrying the identity column's declared type as inner base type — int → int, bigint → bigint; `last_value` = the identity high-water mark, a NULL variant before the first insert) / `is_not_for_replication bit` (1 for an `IDENTITY(s, i) NOT FOR REPLICATION` column, else 0 — `IdentityState.NotForReplication`; replication has no runtime effect, the flag exists for DacFx to re-emit `IdentityIsNotForReplication=True`) / `is_identity bit` (NOT NULL, always 1 — every row in this view is an identity column; appended after the subset, name-addressable, so SQLAlchemy's `get_columns` LEFT JOIN reads it).
- **`sys.trigger_events`** projects one row per (DML trigger, event) pair from `Trigger.Actions`: `object_id int`, `type int` (real SQL Server's dense codes 1 = INSERT, 2 = UPDATE, 3 = DELETE — distinct from the internal action-flag bits 1/2/4), `type_desc nvarchar(128)`, `is_first` / `is_last bit` (that action's `sp_settriggerorder` slot, off `Trigger.FirstForActions` / `LastForActions` — the same state `OBJECTPROPERTY(… 'ExecIsFirstInsertTrigger')` reads, so ordering a multi-action trigger first for INSERT leaves its UPDATE row at 0), `event_group_type int` / `_desc` (NULL), `is_trigger_event bit` (1).
  DDL triggers aren't surfaced (their events are DDL event types SMO's per-table query never reads).
  SMO's trigger-scripting query `LEFT JOIN`s it three times (one per DML event) to build the `FOR` clause.
- **`sys.syslanguages`** (legacy compatibility view) models only the default **us_english** language (`langid 0` / `lcid 1033` / `name`/`alias`), which a stock instance's default-language configuration (`configuration_id 124`, `value_in_use 0`) resolves to — SMO's server-settings query joins it by `langid` to name the default language.
- **Empty unmodeled-feature views** (full probe-confirmed SQL Server 2025 shape, zero rows — the AlwaysOn-DMV precedent): `sys.change_tracking_tables` (5), `sys.external_tables` (29), `sys.filetables` (5), `sys.external_data_sources` (11), `sys.external_file_formats` (13), `sys.masked_columns` (load-bearing subset — Dynamic Data Masking unmodeled), `sys.column_encryption_keys` (4) / `sys.sensitivity_classifications` (10) (Always Encrypted / data classification), `sys.fulltext_stoplists` (5) / `sys.registered_search_property_lists` (5) / `sys.fulltext_languages` (2) (full-text feature surfaces), and the database-level `sys.database_recovery_status` (7) / `sys.change_tracking_databases` (6) / `sys.database_filestream_options` (4).
  All are `LEFT JOIN`ed by the "Script Table as → CREATE To" and database-properties SMO queries; the empty projection resolves each reference and defaults each property via `ISNULL`.
- **DacFx / `sqlpackage /Action:Export` empty views** (full probe-confirmed SQL Server 2025 shape, zero rows — the accepted-but-empty precedent): the bacpac-export reverse-engineering references 38 catalog views for genuinely-unmodeled features; each is modeled empty so a reference resolves rather than raising Msg 208.
  **All 38 exist on real SQL Server 2025** (none skipped).
  Grouped by topic and home file:
  - **Service Broker** (`BuiltInResources.ServiceBroker.cs`, new topic): `sys.services` / `service_queues` / `service_contracts` / `service_contract_usages` / `service_contract_message_usages` / `service_message_types` / `routes` / `conversation_priorities` / `remote_service_bindings` / `event_notifications`.
    Real SQL Server **seeds system rows** into several (a stock WWI reports `service_queues` = 3, `services` = 3, `service_contracts` = 6, `service_message_types` = 14, `routes` = 1) — all `is_ms_shipped` system objects a bacpac export never scripts — so empty-with-full-shape is the cheapest faithful option for DacFx's user-object reverse-engineering (documented divergence: the system seed rows are omitted).
  - **Partitioning** (`Indexes.cs`): `sys.partition_functions` / `partition_schemes` / `partition_range_values` / `partition_parameters` / `destination_data_spaces`.
    Partitioning is unmodeled (every table is a single unpartitioned partition — see `sys.data_spaces` / `sys.partitions`); WWI-Standard has none, so all ship **empty** with the probe-confirmed SQL Server 2025 column shapes.
    `partition_range_values.value` is a first-class **`sql_variant`** matching real (always-empty, so only the column type carries).
    SSMS's Table Designer joins `partition_functions LEFT JOIN (partition_parameters JOIN sys.types …)` — a parenthesized join group (see [`joins.md`](joins.md#parenthesized-join-groups)) — so the group grammar and these views must both be present for the designer to open.
  - **Encryption / key management / Always Encrypted / RLS / audits** (`Security.cs`): `sys.symmetric_keys` / `cryptographic_providers` (server-scope) / `crypt_properties` / `key_encryptions` / `column_master_keys` / `column_encryption_key_values` / `database_credentials` / `database_scoped_credentials` / `security_policies` / `security_predicates` / `server_audits` (server-scope) / `server_file_audits` (server-scope — was **not** previously modeled).
    DacFx joins `sys.symmetric_keys LEFT JOIN sys.cryptographic_providers`; the `sql_variant` columns (`sys.symmetric_keys.key_thumbprint` / `.cryptographic_provider_algid`, `sys.asymmetric_keys.cryptographic_provider_algid`) are first-class **`sql_variant`** matching real (always empty, so only the column type carries), and the `nvarchar(max)`/`varbinary(max)` columns use the `MaxForm` / `MaxLengthSentinel` shapes.
  - **External languages / models / graph / assemblies / numbered procs** (`Programmable.cs` + `ConstraintsAndTriggers.cs`): `sys.external_languages` / `external_libraries` / `external_models` (its `json`-typed `parameters` → nvarchar(max); `create_time`/`modify_time` `datetime2(7)`) / `external_library_files` (`external_library_id` / `content varbinary(max)` / `platform tinyint` / `platform_desc nvarchar(60)`) / `external_language_files` (adds `file_name` / `parameters` / `environment_variables sysname` — probe-confirmed shapes, both empty, DacFx reads them when reverse-engineering EXTERNAL LIBRARY / LANGUAGE objects) / `numbered_procedure_parameters` / `function_order_columns` in Programmable; `sys.edge_constraints` / `edge_constraint_clauses` / `events` in ConstraintsAndTriggers.
    **`sys.events`** relates to trigger / event-notification events (the broader superset of the modeled `sys.trigger_events`); WWI has zero triggers and an empty `sys.events` (probe-confirmed), so always-empty matches real — if DDL/event-notification projection is added later, populate there.
  - **Index / search feature views** (`Indexes.cs` + `FullTextXmlSpatial.cs`): `sys.json_index_paths` / `selective_xml_index_namespaces` / `vector_indexes` (JSON indexes, selective XML index namespaces, DiskANN vector indexes) in Indexes; `sys.registered_search_properties` in FullTextXmlSpatial.
- **`sys.synonyms`** (schema-scoped, 13-column) projects one row per `CREATE SYNONYM`, in ObjectId order per schema — see [`schemas.md`](schemas.md#catalog-surface) for the values and the `base_object_name` shape.
  SSMS's "Edit Top 200 Rows" commit reads `[db].sys.synonyms` via a three-part name to test whether the edit target is a synonym, so the read must resolve through that form as well as the plain one.
  Probe-confirmed SQL Server 2025 shape (`name sysname`, `object_id`/`schema_id`/`parent_object_id`/`principal_id int`, `type char(2)`, `type_desc nvarchar(60)`, `create_date`/`modify_date datetime`, `is_ms_shipped`/`is_published`/`is_schema_published bit`, `base_object_name nvarchar(1035)`).
  Registered in `BuiltInResources.CoreObjects.cs` (`EnumerateSynonyms`); the same objects appear in `sys.objects` / `sys.all_objects` / `sysobjects` with type `SN` through `Schema.SchemaObjects()`.
- **`sys.dm_exec_sessions`** projects one row per **live connection** on the server (`Simulation.Connections`, snapshotted under the registry lock).
  Session-backed columns reflect the connection's real state: `quoted_identifier` / `arithabort` / `ansi_nulls` / `ansi_padding` / `ansi_warnings` / `concat_null_yields_null` (the live `SimulatedDbConnection` toggles `SESSIONPROPERTY` reads) / `text_size` (`SET TEXTSIZE`) / `lock_timeout` / `transaction_isolation_level` (1/2/3/4/5 for RU/RC/RR/Serializable/Snapshot) / `context_info` / `row_count` (`@@ROWCOUNT`) / `prev_error` (`@@ERROR`) / `open_transaction_count` / `database_id`; `status` is `running` for the querying session, `sleeping` for the rest.
  `login_name` is the session's **effective** login (`Security.Effective.LoginName`, so it follows `EXECUTE AS LOGIN`) and `original_login_name` the connect-time one; `security_id` / `original_security_id` are each the deterministic 16-byte SID `BuiltInResources.DeriveLoginSid` derives from that name, the same bytes `SUSER_SID()` reports.
  `host_name` / `program_name` carry the client identity the session reported — the connection string's `Workstation ID` / `WSID` and `Application Name` / `App` keywords in-process, LOGIN7's `HostName` / `AppName` fields over the TDS endpoint, and the empty string when the client sent neither (the same pair `HOST_NAME()` / `APP_NAME()` answer with).
  The remainder are probe-confirmed fresh-session defaults (SQL Server 2025): `endpoint_id 4`, `group_id 2`, `client_version 7`, `authenticating_database_id 1`, and `ansi_null_dflt_on 1` — the one option bit with no session field behind it, since `SET ANSI_NULL_DFLT_ON` / `_OFF` is parse-and-discard.
  SMO's contained-authentication check (`authenticating_database_id … WHERE session_id = @@SPID`) reads it.
  Generated by `EnumerateSysDmExecSessions`.
- **DMV server-state gating**: a restricted session (non-`dbo` — a mapped user or `guest`) reading a modeled DMV is gated by the `VIEW …STATE` permissions — server-scope DMVs (`dm_tran_locks` / `dm_os_waiting_tasks` / `dm_tran_version_store*` / `dm_tran_active_snapshot_database_transactions` / `dm_hadr_cluster`) raise **Msg 300**, database-scope DMVs (`dm_db_partition_stats` / `dm_hadr_database_replica_states`) raise **Msg 262**, `dm_exec_sessions` self-filters to the own session, and `dm_os_host_info` / `fn_helpcollations` / `dm_db_xtp_table_memory_stats` stay public.
  `dbo` / sysadmin bypass with zero added cost (short-circuit on `EffectiveIsDbo`).
  The per-DMV gate descriptor and its checker live in `BuiltInResources.DmvGating.cs` / `ServerPermissionChecker` — see [`permissions.md`](permissions.md#dmv-server-state-gating).
- **`INFORMATION_SCHEMA.TABLES`** (4 cols): TABLE_CATALOG / TABLE_SCHEMA / TABLE_NAME / TABLE_TYPE.
  TABLE_TYPE is `'BASE TABLE'` for user heap tables and `'VIEW'` for views (added with the views bundle — see [`programmable.md`](programmable.md)).
- **`INFORMATION_SCHEMA.COLUMNS`** (full 23-col ISO shape): the always-NULL columns (DOMAIN_*, CHARACTER_SET_SCHEMA, COLLATION_CATALOG, etc.) ship anyway since tooling does `SELECT *`.
  Rows cover base tables **and view output columns**, the same two sources `sys.columns` walks (probe-confirmed).
  ORDINAL_POSITION resequences 1..N over the live columns — unlike `sys.columns.column_id` it fills the hole a `DROP COLUMN` leaves.
  COLUMN_DEFAULT carries the column's captured DEFAULT text, the same string `sys.default_constraints.definition` projects (`(N'x')` / `((42))` / `(sysutcdatetime())`); a view's column and a column with no default report NULL.
  IS_NULLABLE is `varchar(3)` `'YES'`/`'NO'` (not bit).
  CHARACTER_MAXIMUM_LENGTH is declared **chars** (`nvarchar(50)→50`); CHARACTER_OCTET_LENGTH is **bytes** (`nvarchar(50)→100`).
  Text-family sentinels: text/image = `2147483647`; ntext = `1073741823` chars / `2147483646` bytes.
  NUMERIC_PRECISION_RADIX is 10 for integer/decimal/money, 2 for float/real; NUMERIC_SCALE is NULL for float/real, otherwise the actual scale.
  DATETIME_PRECISION carries the fractional-seconds digit count (0 for date/smalldatetime, 3 for datetime, N for datetime2/time/datetimeoffset).
  CHARACTER_SET_NAME: `'UNICODE'` for nvarchar/nchar/ntext/sysname; `'iso_1'` for varchar/char/text; NULL for binary/varbinary/image.
- **`INFORMATION_SCHEMA.SCHEMATA`** (6 cols): the materialized schemas plus the catalog-only fixed ones `sys.schemas` injects (`guest` and the nine fixed-database-role schemas), so a fresh database lists real's 13.
  SCHEMA_OWNER follows the same ownership rule `sys.schemas.principal_id` does: a fixed schema (schema_id ≤ 4 or ≥ 16384) mirrors its own name, a user schema reports `dbo` (probe-confirmed).
  DEFAULT_CHARACTER_SET_NAME is `'iso_1'`.
- **`sys.procedures`** projects per-procedure rows: `object_id`, `name sysname`, `schema_id`, `type char(2)` (always `'P '` — trailing-space padded, probe-confirmed), `type_desc nvarchar(60)` (`SQL_STORED_PROCEDURE`), `create_date datetime`, `modify_date datetime` (advances on each `ALTER` / `CREATE OR ALTER`; equal to `create_date` until the first one), `is_ms_shipped bit`, `is_auto_executed bit` (constant 0 — startup procedures via `sp_procoption` aren't modeled).
  The SMO **StoredProcedure property-bag** reads `is_auto_executed` (projected `AS [Startup]`); a single missing column fails the whole bag query Msg 207 and every StoredProcedure property errors.
- **`sys.sql_modules`** projects one row per programmable module (procedure / view / DML + DDL trigger / scalar / inline / multi-statement function), keyed by `object_id`: `definition nvarchar(max)` (the verbatim CREATE source — see the OBJECT_DEFINITION scalar below; NULL for WITH ENCRYPTION).
  **Wire-typing note**: the `definition` column is declared with a length-0 `.Type` plus a `MaxLength` of `MaxLengthSentinel`, so an expression that *references* it — SMO reads proc bodies as `ISNULL(smsp.definition, ssmsp.definition)` — must recover the MAX-ness from `MaxLength`, not the length-0 `.Type`; `ResolveColumnTypeAcrossSources` folds `MaxLength == MaxLengthSentinel` into `SqlType.AsMaxVariant` so the expression types `nvarchar(max)` and streams as PLP.
  Without the fold, a >32,767-char definition overflowed the bounded 2-byte wire prefix and silently killed the session (the SMO API sweep's residual transport crash — distinct from the OBJECT_DEFINITION scalar path, which SMO does *not* use here).
  Remaining columns: **`uses_ansi_nulls bit`** and **`uses_quoted_identifier bit`** (the module's creation-time SET-option captures — see [Creation-time SET-option capture](#creation-time-set-option-capture)), `is_schema_bound bit` (1 for a `WITH SCHEMABINDING` view or function — read from `View.IsSchemaBound` / `UserDefinedFunction.IsSchemaBound`; 0 otherwise), `null_on_null_input bit` (1 only for a scalar function declared `WITH RETURNS NULL ON NULL INPUT`), **`execute_as_principal_id int`** and the **`inline_type` / `is_inlineable`** pair (their own sections below), and `uses_database_collation` / `is_recompiled` / `uses_native_compilation bit` (all 0 — placeholder constants).
  Generated by `EnumerateSqlModules` filtering `Schema.SchemaObjects()` to `Procedure` / `View` / `Trigger` / `UserDefinedFunction` plus `Database.DdlTriggers`.

### Creation-time SET-option capture

`SchemaObject.UsesQuotedIdentifier` and `SchemaObject.UsesAnsiNulls` each record the session setting in effect when the object was created, re-stamped by `ALTER` / `CREATE OR ALTER`.
They surface as `sys.sql_modules.uses_quoted_identifier` / `uses_ansi_nulls` and, for a table, `sys.tables.uses_ansi_nulls`, plus the `OBJECTPROPERTY` read-backs described [below](#objectproperty--objectpropertyex).
The two differ in what a *table* records: `QUOTED_IDENTIFIER` is a constant 1 for any table (real answers 1 regardless of the creating session), while `ANSI_NULLS` genuinely captures — a table created under `SET ANSI_NULLS OFF` reports 0, and `SELECT … INTO` captures the same way (all probe-confirmed).
The `QUOTED_IDENTIFIER` capture is behavioral as well as metadata — a module body parses under it (see [`grammar.md`](grammar.md#per-object-creation-time-capture)).
The `ANSI_NULLS` capture is **metadata only**: real freezes a module's `= NULL` comparison semantics to the captured setting, but the simulator doesn't model `SET ANSI_NULLS OFF` comparison semantics at all (the SET parses and records, and every comparison stays ANSI), so nothing behavioral rides on this half.

### `execute_as_principal_id`

The database principal the module's `WITH EXECUTE AS` clause resolved to at CREATE, stored on `SchemaObject.ExecuteAsPrincipalId` by `Simulation.ResolveExecuteAsPrincipalId`.
Real's encoding, probe-confirmed across procedures, scalar functions and triggers:

| clause | value |
| --- | --- |
| none / `CALLER` | NULL |
| `OWNER` | `-2` (a sentinel — the owner is resolved per execution, so no id is pinned) |
| `SELF` | the creating session's database principal id (1 for `dbo`) |
| `'user'` | that user's `sys.database_principals.principal_id` |

The textual clause stays on the concrete module type, which is what the invocation path pushes as an impersonation frame — see [`permissions.md`](permissions.md).
A named user the database doesn't hold stores NULL; real refuses the CREATE outright, while the simulator defers the miss to invocation time (Msg 15517).

### `inline_type` / `is_inlineable`

The scalar-UDF-inlining pair, computed by `Schemas/ModuleInlining.cs`.
An inline TVF and a plain scalar function both report 1 / 1; a procedure, view, DML or DDL trigger, and multi-statement TVF report 0 / 0 (all probe-confirmed).
Neither column is compatibility-level gated: a scalar function created at level 140 still reports 1 / 1, and lowering the level afterwards doesn't move it — the level gates whether the optimizer actually inlines, not what the catalog records.
`is_inlineable` answers whether the body *could* be inlined and `inline_type` whether it *would* be; the two part only on `WITH INLINE = OFF` (0 / 1), an option the simulator's `CREATE FUNCTION` grammar doesn't accept, so they always agree here.

The analysis re-tokenizes the stored body the way [`IsDeterministic`](#isdeterministic) does, and covers the disqualifiers probed against SQL Server 2025 — a body whose only disqualifying construct sits outside that set reports 1 where real reports 0:

- a time-dependent intrinsic: `GETDATE` / `GETUTCDATE` / `SYSDATETIME` / `SYSUTCDATETIME` / `SYSDATETIMEOFFSET` / `CURRENT_TIMESTAMP`
- `@@ROWCOUNT`
- more than one `RETURN` statement
- a `WHILE` loop
- a table variable (`DECLARE @t TABLE`)
- recursion — the body naming the function itself
- a non-`CALLER` `WITH EXECUTE AS` clause
- an XML data-type method (`.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()`)
- variable accumulation in a `SELECT` that reads a table — `SELECT @v = @v + col FROM t`

Probed *inlineable* despite looking otherwise, so deliberately not disqualifying: `WITH SCHEMABINDING`, reading a table, `IF` / `ELSE`, `CASE`, multiple `DECLARE`s and `SET`s, a nested `BEGIN` / `END`, `TOP` with `ORDER BY`, a subquery aggregate, `WITH RETURNS NULL ON NULL INPUT`, the session and metadata scalars (`@@SPID`, `USER_ID`, `OBJECT_ID`, `ERROR_NUMBER`), plain `SELECT` assignment with a `FROM` (`SELECT @v = col FROM t`), self-reference without one (`SELECT @v = @v + 1`), and calling another user function — even one that is itself not inlineable, since inlineability isn't transitive.
- **`sys.system_sql_modules`** shares `sys.sql_modules`'s exact 12-column shape but is scoped to system objects' module definitions — always **empty** (no system-defined modules with stored T-SQL are modeled).
  SMO's SSMS Object-Explorer Trigger sub-node query `LEFT JOIN sys.system_sql_modules ssmtr ON ssmtr.object_id = tr.object_id` reads it to distinguish a `WITH ENCRYPTION` module; user triggers never appear here, so the empty view is correct.
- **`INFORMATION_SCHEMA.ROUTINES`** (9 col subset of the ISO shape): `SPECIFIC_CATALOG` / `SPECIFIC_SCHEMA` / `SPECIFIC_NAME` (lead the ISO shape; mirror the `ROUTINE_*` triple — for T-SQL routines there's no overloading so the specific name equals the routine name), `ROUTINE_CATALOG`, `ROUTINE_SCHEMA`, `ROUTINE_NAME`, `ROUTINE_TYPE` (`'PROCEDURE'` / `'FUNCTION'`), `DATA_TYPE` (NULL for procs, the return-type family name for scalar UDFs, `'TABLE'` for inline TVFs), `ROUTINE_DEFINITION nvarchar(4000)` (same source text as `OBJECT_DEFINITION`, truncated to the first 4000 chars like SQL Server; NULL for WITH ENCRYPTION).
  `SPECIFIC_NAME` is load-bearing: SSMS's aggregate-function enumeration joins `sysobjects.name = INFORMATION_SCHEMA.ROUTINES.SPECIFIC_NAME`.
  Real SQL Server ships further columns (`CREATED`, `LAST_ALTERED`, …) — fidelity gap.
- **`INFORMATION_SCHEMA.PARAMETERS`** (8 col subset): `SPECIFIC_CATALOG` / `_SCHEMA` / `_NAME`, `ORDINAL_POSITION`, `PARAMETER_MODE`, `PARAMETER_NAME` (with `@` prefix), `DATA_TYPE`, `CHARACTER_MAXIMUM_LENGTH`.
  A **scalar function's return value is row 0** — `PARAMETER_MODE` `'OUT'`, `PARAMETER_NAME` the empty string, `DATA_TYPE` and `CHARACTER_MAXIMUM_LENGTH` describing the return type; table-valued functions have no such row and declared parameters start at 1.
  `PARAMETER_MODE` is `'IN'` for non-OUTPUT and `'INOUT'` for OUTPUT-declared procedure parameters (functions have no OUTPUT semantics).
  `CHARACTER_MAXIMUM_LENGTH` covers the **binary family as well as the string one** — a `varbinary(20)` parameter reports 20 — carries the MAX sentinel `-1` for MAX-declared types and for `xml`, the documented sentinels for the legacy LOB types, and NULL for everything else.
  All probe-confirmed 2026-07-30.
- **Alias resolution in the ISO views**: `INFORMATION_SCHEMA.COLUMNS` and `PARAMETERS` report `DATA_TYPE` as the type an alias *stands for*, so a `sysname` column or parameter surfaces as `nvarchar` (length 128).
  The `sys.*` catalog views keep the alias instead, which is why this lives in the ISO projections rather than on the type itself.
- **`INFORMATION_SCHEMA.TABLE_CONSTRAINTS`** (9 cols, probe-confirmed SQL Server 2025): one row per PRIMARY KEY / UNIQUE / FOREIGN KEY / CHECK constraint in the current database.
  `CONSTRAINT_CATALOG` / `_SCHEMA` (db / object schema), `CONSTRAINT_NAME sysname`, `TABLE_CATALOG` / `_SCHEMA` / `_NAME`, `CONSTRAINT_TYPE varchar(11)` (`'PRIMARY KEY'` / `'UNIQUE'` / `'FOREIGN KEY'` / `'CHECK'`), `IS_DEFERRABLE` / `INITIALLY_DEFERRED varchar(2)` (constant `'NO'` — SQL Server has no deferrable constraints).
  Reuses the same domain-object traversal as `sys.key_constraints` / `sys.foreign_keys` / `sys.check_constraints` (`HeapTable.KeyConstraints` / `.OutgoingForeignKeys` / `.CheckConstraints`).
  SQLAlchemy's `get_pk_constraint` reads it.
- **`INFORMATION_SCHEMA.KEY_COLUMN_USAGE`** (8 cols, probe-confirmed SQL Server 2025): one row per column participating in a PRIMARY KEY / UNIQUE / FOREIGN KEY constraint, in key order (`ORDINAL_POSITION int` 1..N; CHECK constraints don't appear).
  **Real SQL Server's view does NOT carry the ISO-standard `POSITION_IN_UNIQUE_CONSTRAINT` column** (referencing it raises Msg 207), so it is deliberately omitted — the 8-column shape is what real exposes.
  For a FK the row names the child (constrained) column.
  PK / UNIQUE key columns resolve through the shared `StorageOrdinalToColumnId` authority; FK child columns use `ForeignKey.ChildColumnOrdinals`.
  SQLAlchemy's `get_pk_constraint` / `get_foreign_keys` read it.
- **`INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS`** (9 cols, probe-confirmed SQL Server 2025): one row per FOREIGN KEY.
  `UNIQUE_CONSTRAINT_CATALOG` / `_SCHEMA` / `_NAME sysname` name the referenced PK / UNIQUE constraint (resolved by matching the FK's referenced column set against the referenced table's `KeyConstraints`), `MATCH_OPTION varchar(7)` (constant `'SIMPLE'`), `UPDATE_RULE` / `DELETE_RULE varchar(11)` (ISO spaced wording `'NO ACTION'` / `'CASCADE'` / `'SET NULL'` / `'SET DEFAULT'` — distinct from `sys.foreign_keys`' underscore desc form).
  SQLAlchemy's `get_foreign_keys` reads it.
- **`INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE`** (7 cols, probe-confirmed SQL Server 2025): one row per (constraint, column) pair, over PRIMARY KEY / UNIQUE / FOREIGN KEY / CHECK.
  The table columns lead and the constraint columns trail — `TABLE_CATALOG` / `_SCHEMA` / `_NAME`, `COLUMN_NAME sysname`, `CONSTRAINT_CATALOG` / `_SCHEMA` / `_NAME sysname` — the reverse of `TABLE_CONSTRAINTS`.
  PK / UNIQUE name their key columns and a FOREIGN KEY its child columns, the same sets `KEY_COLUMN_USAGE` reports; a CHECK names the columns its predicate reads.
  A CHECK's columns come from the declaring column for the inline form and otherwise from matching the stored definition text against the parent table's column names — an approximation of real's expression walk, which over-reports a column whose name also appears as a string literal inside the predicate.
  `mssql-django`'s `get_relations` joins it twice to resolve a foreign key's child and referenced columns, and its absence used to fail every Django introspection / constraint / index test.
- **`INFORMATION_SCHEMA.CONSTRAINT_TABLE_USAGE`** (6 cols, probe-confirmed SQL Server 2025): one row per constraint, naming the table it sits on — the same shape as `CONSTRAINT_COLUMN_USAGE` without `COLUMN_NAME`.
- **`sys.dm_os_host_info`** (single-row, server-scope DMV): `host_platform` / `host_distribution` / `host_release` / `host_service_pack_level` / `host_architecture nvarchar(256)`, `host_sku` / `os_language_version int`.
  SSMS selects `host_platform` from it on every connect.
  The row reflects the **actual .NET host process** rather than a canned Windows row (honest values per user preference): `host_platform` is `'Windows'` / `'Linux'` / `'macOS'` via `OperatingSystem.Is*` (macOS is a deliberate divergence — real SQL Server never runs there); `host_architecture` is `RuntimeInformation.OSArchitecture` uppercased (`'X64'` / `'ARM64'`).
  On Linux `host_distribution` / `host_release` parse `NAME=` / `VERSION_ID=` from `/etc/os-release` (quote-stripped, file-access failures fall back to `'Linux'` / `''`); on Windows they are `'Windows'` / `Environment.OSVersion.Version` as `"major.minor"`.
  `host_sku` is `48` on Windows and **NULL off-Windows** (matching real SQL Server on Linux); `os_language_version` is `1033`; `host_service_pack_level` is the empty string.
  Materialized once into `BuiltInResources.DmOsHostInfoRows` since host identity is fixed for the process lifetime.
- **`sys.configurations`** (106-row, server-scope static config catalog): `configuration_id int`, `name nvarchar(35)`, `value` / `minimum` / `maximum` / `value_in_use`, `description nvarchar(255)`, `is_dynamic` / `is_advanced bit`.
  Like real SQL Server the four `value*` columns are **`sql_variant`**, each carrying an **`int`** inner (probe-confirmed against SQL Server 2025 — every stock config value, even `'max server memory (MB)'`, reports an `int` inner).
  The 106 rows are a stock instance's **defaults** baked into `BuiltInResources.ConfigurationData` and materialized once into `ConfigurationsRows` — `configuration_id` and `name` are stable across instances, and `value` mirrors `value_in_use` on a fresh server.
  `sp_configure` writes are layered over those defaults per simulation (`Simulation.ServerConfiguration`, keyed by `configuration_id`): a staged value moves `value`, and `RECONFIGURE` moves `value_in_use` to match — see the `sp_configure` entry below.
  `SET` options are still not reflected here.
  The two CLR rows are the exception and report the `Simulation.EnableClr` host opt-in whatever `sp_configure` wrote, since that opt-in — not the option — is what gates assembly registration.
  The row set is independent of the database argument (server-scoped).
  SSMS's SMO reads `select value_in_use from sys.configurations where configuration_id = 16384` (Agent XPs) during its Object-Explorer database-node preamble — without this the Msg 208 aborted the request before the database enumeration and the Databases folder showed empty.
- **`sys.databases`** (full **98-column** projection matching SQL Server 2025; one row per `Database` via `DbId.DatabasesWithIds`, ordered by `database_id`).
  SSMS's SMO Object-Explorer "Databases" node enumeration references `owner_sid` / `create_date` / `state_desc` / `recovery_model_desc` / `containment` / `source_database_id` and a long list of `is_*` option flags — a single missing column raised Msg 207 and left the Databases folder empty, so the projection is the complete column set.
  **`database_id` is `int`** (SMO/consumers expect int) — distinct from the `DB_ID()` scalar, which still returns `smallint`.
  Columns backed by live `Database` state: `name`, `database_id`, `compatibility_level` (`CompatibilityLevel`), `collation_name` (`CollationName`), `snapshot_isolation_state`(+`_desc` ON/OFF, from `AllowSnapshotIsolation`), `is_read_committed_snapshot_on` (`ReadCommittedSnapshot`), `is_recursive_triggers_on` (`RecursiveTriggers` — see [`triggers.md`](triggers.md#nesting-and-recursion-options)), `physical_database_name` (= name); `state`/`state_desc` are always `0`/`ONLINE`.
  `recovery_model` is `3`/`SIMPLE` for `master` / `tempdb` / `msdb` and `1`/`FULL` for `model` and every user database, which inherits the template's (probe-confirmed against a fresh `CREATE DATABASE`).
  `is_broker_enabled` is `1` everywhere but `master` and `model`, which report `0` (probe-confirmed; Service Broker itself is unmodeled, so this is the flag alone).
  All remaining option-flag columns carry the stock defaults a **freshly created user database** reports on SQL Server 2025 (probe-confirmed): `user_access` `0`/`MULTI_USER`, `page_verify_option` `2`/`CHECKSUM`, `containment` `0`/`NONE`, `log_reuse_wait` `0`/`NOTHING`, `delayed_durability` `0`/`DISABLED`, `catalog_collation_type` `0`/`DATABASE_DEFAULT`, `data_compaction` / `data_lake_log_publishing` `0`/`UNSUPPORTED`, the whole ANSI family (`is_ansi_null_default_on` / `is_ansi_nulls_on` / `is_ansi_padding_on` / `is_ansi_warnings_on` / `is_arithabort_on` / `is_concat_null_yields_null_on` / `is_numeric_roundabort_on`), `is_quoted_identifier_on` and `is_local_cursor_default` all 0 while `is_fulltext_enabled` and `is_temporal_history_retention_enabled` are 1, `owner_sid` a fixed `0x01` (sa-style) sid, `create_date` a fixed seed (`2025-01-01` — no per-database creation timestamp is tracked), `service_broker_guid` a fixed constant (Service Broker unmodeled), and the contained-DB-only columns (`default_language_*`, `default_fulltext_language_*`, `is_nested_triggers_on`, `is_transform_noise_words_on`, `two_digit_year_cutoff`) plus `source_database_id` / `replica_id` / `group_database_id` / `resource_pool_id` all NULL.
  Code↔`_desc` pairs are always internally consistent.
  **`is_query_store_on` is the one flag deliberately left at real's opposite value (0 where a fresh 2025 database reports 1)**: it is read together with `sys.database_query_store_options`, whose single OFF row is itself a deliberate choice (see that view below), and a 1 here beside an OFF row there would be a self-contradiction a tool reading both would trip on.
  Flipping the pair together is the coherent alternative and stays open.
  Generated by `BuiltInResources.EnumerateSysDatabases`.
- **`sys.database_mirroring`** (21-column, **one row per database** via `DbId.DatabasesWithIds`, join key `database_id`).
  The simulator never mirrors a database, so every `mirroring_*` column is NULL on every row — the exact non-mirrored shape a live SQL Server 2025 returns (probe-confirmed: only `database_id` populated).
  SSMS's Object-Explorer "Databases" enumeration does `master.sys.databases dtb LEFT JOIN sys.database_mirroring dmi ON dmi.database_id = dtb.database_id` and reads `ISNULL(dmi.mirroring_role, 0)` / `ISNULL(dmi.mirroring_state + 1, 0)`; Msg 208 on this view blanked the folder.
  The three LSN columns (`mirroring_failover_lsn` / `_end_of_log_lsn` / `_replication_lsn`) are `numeric(25, 0)` on the server — modeled as `numeric(25,0)`, surfaced NULL.
  Generated by `BuiltInResources.EnumerateSysDatabaseMirroring`.
- **`sys.endpoints`** (10-column, server-scope, always **empty**) — the simulator's TDS listener isn't surfaced as a configured endpoint object, and the real server's built-in system endpoints (TSQL Local Machine / Named Pipes / Default TCP / VIA) aren't modeled.
  SMO's `Server.Endpoints` enumeration does `SELECT e.name FROM sys.endpoints AS e ORDER BY [Name]`; the empty projection resolves it and lists no endpoints (probe-confirmed shape: `name sysname` / `endpoint_id int` / `principal_id int` / `protocol tinyint` (+`_desc`) / `type tinyint` (+`_desc`) / `state tinyint` (+`_desc`) / `is_admin_endpoint bit`).
  Registered in `BuiltInResources.ServerAndDatabases.cs` via the shared `EmptyCatalogRows`.
- **`sys.availability_replicas`** (22-column), **`sys.availability_groups`** (19-column), **`sys.dm_hadr_database_replica_states`** (39-column) — AlwaysOn Availability-Group catalog views / DMV, **server-scope, always empty** (no AGs are ever configured in the simulator).
  Full column shape modeled so future tooling selecting arbitrary columns doesn't hit Msg 207, but every one projects zero rows via the shared `BuiltInResources.EmptyCatalogRows`.
  SSMS's enumeration seeds `#temp` tables from these (`insert into #tmp select replica_id, group_id, replica_server_name from master.sys.availability_replicas`, etc.); the insert-from-empty-view path must resolve and add zero rows.
  The `dm_hadr_database_replica_states` LSN columns are `numeric(25,0)`.
  The AGs are empty because AlwaysOn is *available but not enabled* — `xp_qv` reports the feature available (edition capability) while `SERVERPROPERTY('IsHadrEnabled')` = 0 and no AGs are configured.
- **`sys.master_files`** (32-column, **one data file (`type` 0, ROWS) + one log file (`type` 1, LOG) per database** via `DbId.DatabasesWithIds`, join key `database_id`).
  SSMS probes for an in-memory-OLTP filegroup via `... from master.sys.master_files mf join master.sys.databases db ... where mf.[type] = 2`; the simulator emits **no `type`-2 file** (FILESTREAM / memory-optimized), so that filter returns nothing (the bracket-escaped `[type]` column name parses — `type` is already a catalog-view column name, e.g. `sys.objects`).
  File contents are synthetic: logical name `<db>_Data` / `<db>_Log`, physical path `/var/opt/mssql/data/<db>.mdf` / `_log.ldf`, `state` 0/ONLINE, and a page-denominated `max_size` / `growth` pair — real measures both in 8 KB pages whenever `is_percent_growth` is 0, so the 64 MB default growth is **8192**, not 65536, and `max_size` is -1 (unlimited) on the data file but the 2 TB ceiling **268435456** on the log (probe-confirmed).
  `sp_helpfile` renders the same two values in KB (`65536 KB` of growth; the log's ceiling as `2147483648 KB` rather than "Unlimited"), which is why `BuiltInResources.FileGrowthKilobytes` and `FileGrowthPages` are separate constants.
  All `is_*` flags 0.
  The data-file **`size`** is `ComputeDataFileSizePages(db)` — the live allocation-unit page total (`SumDataFilePages`, from `sys.allocation_units`) plus headroom, floored at 640 pages — so it always exceeds Σ `total_pages` and SSMS never computes a negative SpaceAvailable; the log file is a fixed 128 pages (`LogFileSizePages`).
  All LSN columns (`create_lsn` / `drop_lsn` / … / `backup_lsn`) are `numeric(25,0)`, surfaced NULL (no physical log).
  Generated by `BuiltInResources.EnumerateSysMasterFiles`.
- **`sys.database_files`** (18-column) — the current-database projection of `sys.master_files`: one data file (`file_id` 1, `type` 0 ROWS) + one log file (`file_id` 2, `type` 1 LOG) for the database the reference resolves to.
  Includes `drop_lsn numeric(25,0)` (always NULL — no file is ever dropped); SSMS's FileGroup→Files enumeration filters on `df.drop_lsn is null`, so the column must resolve (`sys.master_files` already carried it, `database_files` was the missing sibling).
  Names / file_ids / types / **sizes** **agree with `sys.master_files`** (`<db>_Data` / `<db>_Log`, physical `/var/opt/mssql/data/<db>.mdf` / `_log.ldf`, the data-file size from `ComputeDataFileSizePages`); there is no `database_id` column (the view is implicitly current-database), so a three-part `master.sys.database_files` read returns master's two files (per-database catalog views receive the resolved target `database`, the same cross-DB routing `sys.database_query_store_options` uses).
  SSMS reads `master.sys.database_files where name=N'master'` / `name=N'mastlog'` on connect to derive the master data/log directory; because the simulator names master's files `master_Data` / `master_Log` (master_files consistency, not real's `master` / `mastlog`), that name lookup returns nothing and the derived MasterDBPath is blank — a deliberate divergence favoring intra-simulator file-name agreement over the SSMS name probe.
  Generated by `BuiltInResources.EnumerateSysDatabaseFiles`.
- **`sys.database_query_store_options`** (22-column, **per-database**: the row set keys off the database *context* the view resolved to, not a `database_id` column).
  Query Store is never enabled in the simulator, so a **user database returns exactly one OFF row** and a **system database (master/tempdb/model/msdb per `Simulation.SystemDatabaseNames`) returns zero rows** — the exact split a live SQL Server 2025 returns (probe-confirmed).
  A three-part `master.sys.database_query_store_options` read therefore yields nothing while the unqualified 2-part form on a user connection yields the OFF row.
  The single row is the fixed disabled shape: `desired_state`/`actual_state` `0`/OFF (smallint + `nvarchar(60)` desc), `query_capture_mode` `2`/AUTO (real's own default is round-trippable through DacFx's model schema in a way CUSTOM is not — its import rejects capture mode 4), `size_based_cleanup_mode`/`wait_stats_capture_mode` `0`/OFF, the bigint knobs at their stock defaults (`flush_interval_seconds` 900, `interval_length_minutes` 60, `max_storage_size_mb` 1000, `stale_query_threshold_days` 30, `max_plans_per_query` 200, capture-policy trio 30/1000/100 + `capture_policy_stale_threshold_hours` 24), `readonly_reason`/`current_storage_size_mb` 0, and `actual_state_additional_info nvarchar(4000)` the empty string (NOT NULL-valued).
  SSMS's Query Store probe gates on `OBJECT_ID(N'[sys].[database_query_store_options]') IS NOT NULL` and then reads `actual_state`.
  **`sys.databases.is_query_store_on` is held at 0 to agree with this row** — the two are read together, and a real 2025 database reports 1 / READ_WRITE on both, so flipping either alone is a self-contradiction; flipping the pair together stays open.
  Generated by `BuiltInResources.EnumerateSysDatabaseQueryStoreOptions`.
- **`sys.query_store_runtime_stats`** (per-plan runtime-statistics capture, **always empty** — the simulator never runs Query Store).
  Full 81-column shape modeled so `SELECT`/`IF EXISTS` over it resolves and returns zero rows via the shared `EmptyCatalogRows`; SSMS's probe does `IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats)`, which falls to its ELSE.
  Column shape probe-confirmed against SQL Server 2025: the nine "core" metric groups (`duration`, `cpu_time`, `logical_io_reads`/`_writes`, `physical_io_reads`, `clr_time`, `dop`, `query_max_used_memory`, `rowcount`) expose NOT NULL `last`/`min`/`max` bigint columns; the four "extended" groups (`num_physical_io_reads`, `log_bytes_used`, `tempdb_space_used`, `page_server_io_reads`) expose them NULL.
  Built by `BuiltInResources.BuildQueryStoreRuntimeStatsColumns`.
- **`OBJECT_ID` resolves catalog views**: `OBJECT_ID('sys.<view>')` / `OBJECT_ID('[sys].[<view>]')` (and the `'V'`-typed form) return a non-NULL `int` for any registered `sys.*` / `INFORMATION_SCHEMA.*` catalog view, via `BatchContext.TryResolveCatalogView`.
  The id is `CatalogView.ObjectId` — a process-stable 32-bit FNV-1a hash of the leaf name forced negative (catalog views are process-wide, not per-`Database`, so they can't draw from `Database.AllocateObjectId`; the negative range keeps them disjoint from the positive ids user objects allocate).
  Not byte-identical to real SQL Server's small fixed system-view ids — the load-bearing property is non-NULL, which SSMS's Query Store probe gates on.
  Before this, `OBJECT_ID('sys.tables')` returned NULL (catalog views weren't reachable through `OBJECT_ID` at all).
- **`xp_msver`** system procedure (`Simulation.XpMsver.cs`, dispatched via `ResolveSystemProcedureName` / `ParseExec` like the `sp_*` family): returns a single 20-row result set with columns `Index smallint`, `Name nvarchar`, `Internal_Value int` (nullable), `Character_Value nvarchar` (nullable) — the version / host-info table SSMS calls on connect.
  Callable as `xp_msver`, `dbo.xp_msver`, and `master.dbo.xp_msver` from any current database (the leaf resolves regardless of qualifier, matching real SQL Server's `sp_`/`xp_`-through-master resolution).
  The result flows through the standard outcome enumerator (not a bypass) so a future `INSERT … EXEC` can consume it.
  Every cell is a fixed value mirroring the SQL Server 2025 reference instance (17.0.4065.4, RTM-CU7): version-identity cells carry the real build (ProductVersion `Internal_Value` packs `major << 16` = `1114112`, `'17.0.4065.4'`; FileVersion `'2025.0170.4065.04 ((sql2025_rtm_qfe-cu7).260709-0512)'`), and the host-shaped cells report the reference's fixed values rather than the simulator's live host — `Platform` `'NT x64'`, `WindowsVersion` `266403844` / `'6.3 (20348)'`, `ProcessorCount` `16`, `ProcessorType` `8664`, `PhysicalMemory` `3072` / `'3072 (3221225472)'` (documented fixed placeholders).
  This matches real, whose `xp_msver` reports Windows-style host strings even on Linux.
  All rows are materialized once into `XpMsverRows`.
  **`@optname` filtering** (`FilterXpMsverRows`, probe-confirmed against SQL Server 2025): each argument names one row to return — the result carries only the requested rows, **always in `Index` order regardless of argument order**; with no arguments every row returns.
  Name matching is case-insensitive; an unknown optname is silently skipped (`EXEC xp_msver 'bogus'` → empty set, no error); a duplicated optname yields its row once.
  **RPC-by-name path**: DacFx's `sqlpackage /Action:Export` invokes `xp_msver` as a direct TDS RPC (`CommandType.StoredProcedure`) with five repeated `@optname` parameters.
  The engine's `Simulation.InvokeFromCommandTypeStoredProcedure` routes a modeled system-proc name (matched via `ResolveSystemProcedureName`) through `InvokeSystemProcedureFromRpc`, which synthesizes an equivalent top-level `EXEC <name> <args>` batch — arguments **literalized positionally** (`LiteralizeRpcArgument`), because named-argument synthesis would collide on the repeated `@optname`.
  This makes every modeled system proc reachable via `CommandType.StoredProcedure`, not just `xp_msver`.
- **`xp_qv`** system procedure (`Simulation.XpQv.cs`, dispatched like `xp_msver`): SSMS's Object-Explorer AlwaysOn-availability probe (`EXECUTE @alwayson = master.dbo.xp_qv N'<feature-hash>', @@SERVICENAME`).
  Consumes and ignores its arguments (the feature hash isn't validated), yields **no result set**, and returns status `2` — AlwaysOn *available*.
  This is the edition-capability answer (the simulator reports `EngineEdition` = 3 / Enterprise, which supports AlwaysOn) and is a distinct axis from whether AlwaysOn is *configured* (`SERVERPROPERTY('IsHadrEnabled')` = 0, zero AGs).
  Probe-confirmed against SQL Server 2025: a normal Enterprise instance with no AGs returns `xp_qv` = 2 and `IsHadrEnabled` = 0.
  **This value is load-bearing for the Databases node**: SMO's Object-Explorer database enumeration is HADR-aware and keys off it — reporting the edition-inconsistent `0` made SMO take a degraded path and skip the user-database enumeration entirely (the node stayed empty with no error); with `2` SMO issues its standard HADR-aware enumeration, which resolves against the empty AG DMVs and the per-database `sys.database_mirroring` / `sys.master_files` views.
  Callable as `xp_qv`, `dbo.xp_qv`, and `master.dbo.xp_qv` from any current database, and reachable via the `EXECUTE @rc = …` return-status path.
  Without this proc the AlwaysOn probe raised Msg 2812 and SMO aborted the Databases node.
  The `@@SERVICENAME` argument works because `ParseExecArgument` accepts a `@@`-niladic function as an EXEC argument value (evaluated in a column-less runtime context).
- **`xp_instance_regread`** system procedure (`Simulation.XpInstanceRegread.cs`, dispatched like `xp_msver` / `xp_qv`): reads instance registry defaults.
  SSMS calls `master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLPath', @out OUTPUT` on connect to derive the SMO RootDirectory.
  The positional args are hive, subkey, value-name, and an optional `@output OUTPUT`.
  Registry values are machine-specific on a real server; the simulator returns **synthetic fictional paths rooted at `/var/opt/mssql`** (consistent with the `sys.master_files` / `sys.database_files` physical paths): `SQLPath` → `/var/opt/mssql`; the common default-directory names (`DefaultData` / `DefaultLog` / `BackupDirectory`) → `/var/opt/mssql/data`; every other value name reads NULL (value not found).
  When an OUTPUT variable is supplied (SSMS's shape) the value is written into it and **no result set** is yielded; without one, real's two-column `(Value, Data)` result set is produced.
  Callable as `xp_instance_regread` or `master.dbo.xp_instance_regread` from any current database.
  Without this proc the SSMS connect batch raised Msg 2812.
- **`sp_datatype_info_100`** system procedure (`Simulation.DatatypeInfo.cs`, dispatched like `xp_msver` / `sp_tablecollations_100`): the ODBC `SQLGetTypeInfo` backing proc a driver (ODBC Driver 18, JDBC, any `SQLGetTypeInfo` caller) queries on connect to learn each type's precision/scale — without it the driver bound temporal parameters at scale 0, dropping a `datetime2` parameter's sub-second fraction before it was even sent.
  Returns a fixed 20-column result set (`TYPE_NAME nvarchar(128)`, `DATA_TYPE smallint`, `PRECISION int`, `LITERAL_PREFIX`/`LITERAL_SUFFIX`/`CREATE_PARAMS varchar(32)`, `NULLABLE`/`CASE_SENSITIVE`/`SEARCHABLE`/`UNSIGNED_ATTRIBUTE`/`MONEY`/`AUTO_INCREMENT smallint`, `LOCAL_TYPE_NAME nvarchar(128)`, `MINIMUM_SCALE`/`MAXIMUM_SCALE`/`SQL_DATA_TYPE`/`SQL_DATETIME_SUB smallint`, `NUM_PREC_RADIX int`, `INTERVAL_PRECISION`/`USERTYPE smallint`), probe-captured from the SQL Server 2025 reference.
  `@data_type int = 0` (positional or named, NULL/absent → 0) selects a single `DATA_TYPE` when non-zero or every type when 0; `@ODBCVer tinyint = 2` (positional or named, absent → 2) collapses to 2 (values < 3) or 3, choosing a version-tagged row set (37 rows each).
  The ODBCVer=2/3 split drives the temporal `DATA_TYPE` codes (`datetime2`/`datetime`/`smalldatetime` = concise 11 in v2, verbatim 93 in v3; `date` = 9 vs 91) and `float`/`real` precision + radix (`float` = precision 15 / radix 10 in v2 vs 53 / 2 in v3).
  Rows are filtered by `DATA_TYPE` in range and sorted by (`DATA_TYPE`, `AUTO_INCREMENT`, `MONEY`, `USERTYPE`) with NULL `AUTO_INCREMENT` first — mirroring real's `ORDER BY`.
  Reachable as `sp_datatype_info_100` / `sys.sp_datatype_info_100` (the form ODBC's `SQLGetTypeInfo` RPC sends) from any current database.
- **`sp_tables` / `sp_columns_100`** system procedures (`Simulation.CatalogProcs.cs`, dispatched like `sp_datatype_info_100`): the ODBC `SQLTables` / `SQLColumns` backing procs JDBC's `DatabaseMetaData.getTables` / `getColumns` (Hibernate schema validation, generic tooling) call on connect to enumerate the catalog.
  Unlike `sp_datatype_info_100`'s static type table, these project the **live catalog** — the current database's user `HeapTables` (`'TABLE'`) and `Views` (`'VIEW'`), the same object set `sys.tables` / `sys.columns` enumerate.
  All result-set shapes and per-column values are probe-confirmed against SQL Server 2025.
  - **`sp_tables`** yields the five ODBC columns `TABLE_QUALIFIER` / `TABLE_OWNER` / `TABLE_NAME sysname`, `TABLE_TYPE varchar(32)` (`'TABLE'` | `'VIEW'`), `REMARKS varchar(254)` (always NULL), one row per table/view, sorted by (`TABLE_TYPE`, `TABLE_QUALIFIER`, `TABLE_OWNER`, `TABLE_NAME`).
    `@table_name` / `@table_owner` are T-SQL LIKE patterns (NULL → all); `@table_qualifier` restricts to a database name (a mismatch against the current database yields zero rows); `@table_type` is a quoted comma-list (`'TABLE','VIEW'`, embedded quotes stripped).
  - **`sp_columns_100`** yields the 29-column ODBC `SQLColumns` set, one row per column of every matching table/view in `ORDINAL_POSITION` order.
    `@table_name` / `@table_owner` / `@column_name` are LIKE patterns; `@ODBCVer` (< 3 → 2) selects the temporal `DATA_TYPE` codes and `float`/`real` precision the same way `sp_datatype_info_100` does (`datetime2` `DATA_TYPE` = 11 in v2, 93 in v3).
    The type facts — `DATA_TYPE`, `SQL_DATA_TYPE`, `SQL_DATETIME_SUB`, `RADIX`, and the parameterless `PRECISION` — are read out of the **shared `sp_datatype_info_100` raw tables** (`SpDatatypeInfoByName*`, keyed by base type name) so the two procs can't drift on the shared type mapping.
    `PRECISION` for parameterized types, `LENGTH` (ODBC transfer octet size — storage bytes for numerics, the `*_STRUCT` size for date/time, byte width for strings/binary, precision+2 for decimal/money), `SCALE`, `CHAR_OCTET_LENGTH` (set only for string/binary/LOB/xml columns, where it equals `LENGTH`), and the legacy `SS_DATA_TYPE` token (the old `syscolumns.type`; nullable numerics/datetimes switch to their `N` variant — e.g. `int` 56/38, `bigint`'s documented 63/108 quirk) are computed per column.
    `TYPE_NAME` carries the `' identity'` suffix for identity columns; `COLUMN_DEF` is the stored default-constraint definition text (or NULL); `SS_IS_IDENTITY` / `SS_IS_COMPUTED` reflect the column; the `SS_UDT_*` / `SS_XML_*` columns are NULL for ordinary columns.
  Both reachable unqualified or `sys.`-qualified from any current database, via `EXEC` or the name-form RPC (the same seam as `sp_datatype_info_100`).
- **`sp_pkeys` / `sp_statistics_100` / `sp_stored_procedures`** system procedures (`Simulation.CatalogProcs.cs`, dispatched like `sp_tables`): the ODBC `SQLPrimaryKeys` / `SQLStatistics` / `SQLProcedures` backing procs JDBC's `DatabaseMetaData.getPrimaryKeys` / `getIndexInfo` / `getProcedures` call to enumerate the live catalog's keys, indexes, and stored procedures.
  All result-set shapes and per-column values are probe-confirmed against SQL Server 2025.
  - **`sp_pkeys`** yields the six ODBC columns `TABLE_QUALIFIER` / `TABLE_OWNER` / `TABLE_NAME` / `COLUMN_NAME` / `PK_NAME` (`sysname`) and `KEY_SEQ` (`smallint`), one row per PRIMARY KEY column of the named table in key order.
    `KEY_SEQ` is the 1-based position within the key; `PK_NAME` is the constraint name — the simulator's auto-generated `PK__<table8>__<hex>` shape (real's is object-id-derived, so it won't byte-match, consistent with existing constraint-name behavior).
    `@table_name` (required) / `@table_owner` are exact identifiers (not LIKE patterns — probe-confirmed: a wildcard matches nothing); `@table_qualifier` restricts to a database name; a table with no PRIMARY KEY yields zero rows.
  - **`sp_statistics_100`** yields the 13-column ODBC `SQLStatistics` set: a table-cardinality **summary row first** (`TYPE` = 0 `SQL_TABLE_STAT`, every index column NULL), then one row per index key column.
    `TYPE` is 1 (`SQL_INDEX_CLUSTERED`) / 3 (`SQL_INDEX_OTHER`), `NON_UNIQUE` 0/1, `SEQ_IN_INDEX` 1-based, `COLLATION` `'A'`/`'D'` (a constraint-backed index is always unique + ascending; a `CREATE INDEX` one carries its own UNIQUE flag and per-column ASC/DESC direction).
    `INDEX_QUALIFIER` is the **table name** (not the schema), `FILTER_CONDITION` is a filtered index's predicate text (else NULL).
    `CARDINALITY` is the live row count for the summary + clustered-index rows (NULL for nonclustered — no separate stats); `PAGES` is the heap's data-page count for those same rows (an approximation — the simulator keeps no separate clustered-index or statistics storage) and NULL for nonclustered.
    Rows sort by (`NON_UNIQUE`, `TYPE`, `INDEX_NAME`, `SEQ_IN_INDEX`) after the summary; `@table_name` is an exact identifier, `@index_name` is a **LIKE pattern** over the index rows (probe-confirmed: JDBC `getIndexInfo` passes `'%'` for all indexes, and a NULL / omitted `@index_name` yields the summary row alone), `@is_unique = 'Y'` restricts to unique indexes (the summary row is always emitted), `@accuracy` / `@ODBCVer` are accepted and ignored.
    Indexes are enumerated through `HeapTable.IndexIdentities()` — the same single index-id authority `sys.indexes` reads.
  - **`sp_stored_procedures`** yields the eight-column ODBC `SQLProcedures` set, one row per user stored procedure: `PROCEDURE_QUALIFIER` / `PROCEDURE_OWNER` (`sysname`), `PROCEDURE_NAME` (`nvarchar(134)`, carrying the trailing `;1` group number), `NUM_INPUT_PARAMS` / `NUM_OUTPUT_PARAMS` / `NUM_RESULT_SETS` (`int`, always `-1` — real doesn't compute them), `REMARKS` (`varchar(254)`, NULL), `PROCEDURE_TYPE` (`smallint`, 2 = `SQL_PT_PROCEDURE`).
    `@sp_name` / `@sp_owner` are LIKE patterns; rows sort by (`PROCEDURE_OWNER`, `PROCEDURE_NAME`).
    The simulator has no system-procedure catalog, so — unlike real, which also lists the ~1600 `sys` procs — only user procedures (each schema's `Procedures`) are projected.
  All three reachable unqualified or `sys.`-qualified from any current database.
- **`sp_rename`** system procedure (`Simulation.Rename.cs`, dispatched like `sp_tables`): the object / column / index rename schema-migration tools (Alembic's `rename_table` / `alter_column`, SSMS) emit.
  Signature `sp_rename @objname, @newname [, @objtype]`; positional and named (`@objname=` / `@newname=` / `@objtype=`) arguments both bind, reachable via `EXEC` **and** the name-form RPC (the RPC re-synthesizes an `EXEC`, so one dispatch arm serves both).
  Mutates catalog state and buffers the sev-10 **Msg 15477** "Caution: Changing any part of an object name could break scripts and stored procedures." info message (delivered through the `InfoMessage` / PRINT path, never thrown); the proc returns 0.
  All message wording / numbers / severities probe-confirmed against SQL Server 2025.
  - **`@objtype` NULL / omitted** renames a table (or object): the resolved leaf moves within its `Schema.HeapTables` and `SchemaObject.Name` updates in place (object identity / `object_id` preserved, matching real).
    A missing object → **Msg 15225** (`No item by the name of '<objname>' … given that @itemtype was input as '(null)'`); a `@newname` colliding anywhere in the shared object namespace → **Msg 15335** (`… already in use as a object name …` — the ungrammatical "a object" matched verbatim).
  - **`@objtype` = COLUMN** (case-insensitive) renames a column of `[schema.]table.column`: `HeapColumn.Name` updates in place (storage is by ordinal — no row re-encode).
    A duplicate column name → **Msg 15335** (kind `COLUMN`).
  - **`@objtype` = INDEX** renames an index of `[schema.]table.index`: `Index.Name` updates in place (surfaces through `sys.indexes`).
    A duplicate index name → **Msg 15335** (kind `INDEX`).
  - A missing parent table or missing column / index (the COLUMN / INDEX paths) → **Msg 15248** (`Either the parameter @objname is ambiguous or the claimed @objtype (<type>) is wrong`).
  - `@newname` is used **verbatim** as the new leaf — real does not parse it as a multi-part name.
  - A table or column a `WITH SCHEMABINDING` module references can't be renamed → **Msg 15336** (`Object '<objname>' cannot be renamed because the object participates in enforced dependencies.`, echoing `@objname` as passed); a column no schema-bound body names renames fine — see [`programmable.md`](programmable.md#schema-binding-with-schemabinding).
  - Every rename kind **bumps `Simulation.SchemaVersion`** so cached plans that resolved the old name re-parse.
  - Any raised error is attributed to `sp_rename` (`ERROR_PROCEDURE()` / `SqlException.Procedure`).
  - **Divergences.** Other `@objtype` values (USERDATATYPE / STATISTICS / DATABASE / …) raise `NotSupportedException` naming the unmodeled type — real distinguishes Msg 15248 (recognized-but-not-found) from Msg 15249 (unrecognized) there.
    The rename mutates catalog state directly, not through the undo log, so a `ROLLBACK` does **not** revert it (real's sp_rename is transactional).
    `#temp` tables aren't special-cased: their current-database resolution miss surfaces Msg 15225, which matches real (real also resolves `@objname` in the current database, so a bare `#t` isn't found).
- **`sp_configure` + `RECONFIGURE`** (`Simulation.Configure.cs`, dispatched like `sp_tables`; `RECONFIGURE [WITH OVERRIDE]` is its own top-level statement): reads and writes the server-configuration catalog `sys.configurations` projects.
  Every shape and message probe-confirmed against SQL Server 2025.
  - **No arguments** lists every visible option ordered by name; **`@configname` alone** reports one option.
    Either way the result set is `name nvarchar(35)`, `minimum` / `maximum` / `config_value` / `run_value int`.
  - **`@configname, @configvalue`** stages the value, returns no rows, and buffers the sev-0 **Msg 15457** `Configuration option '<name>' changed from <a> to <b>. Run the RECONFIGURE statement to install.` — which real emits even when the value doesn't change ("from 1 to 1").
  - The name **prefix-matches**: `'nested'` finds `nested triggers`.
    An ambiguous prefix is **Msg 15124**; an unknown name is **Msg 15123**, as is an *advanced* option while `show advanced options`' **installed** value is 0 (so hiding takes effect only after RECONFIGURE); a value outside the option's minimum / maximum is **Msg 15129**.
  - **`RECONFIGURE`** installs every staged value — the reason a `sp_configure` write alone changes nothing.
    Real validates the staged values against the running server and `WITH OVERRIDE` waives that; the simulator validates at `sp_configure` time, so the clause parses and makes no difference.
  - The values are **server-scoped** (`Simulation.ServerConfiguration`), so every connection into the simulation reads the same ones.
    Only **`nested triggers`** carries behavior — see [`triggers.md`](triggers.md#nesting-and-recursion-options); the rest round-trip through the catalog.
  - **Divergences.** Real also requires ALTER SETTINGS permission, and returns the matching names as a `duplicate_options` result set alongside Msg 15124; neither is modeled.
- **The `sp_help` family** (`Simulation.HelpProcs.cs` + `Simulation.HelpProcs.SpHelp.cs`, dispatched like `sp_tables`): `sp_helptext`, `sp_help`, `sp_helpindex`, `sp_helpconstraint` — the formatted-metadata procs interactive sessions and scripting fall back on.
  Result-set shapes, column types, wording and row ordering are probe-confirmed against SQL Server 2025; each proc's algorithm mirrors the shipped system procedure's own body (read back through `OBJECT_DEFINITION` on the reference instance) rather than being re-derived.
  All four accept `@objname` positionally or by name, resolve 1-/2-/3-part and bracket-quoted names, and share one preamble: no argument → **Msg 201**, a three-part name whose database component isn't the current database → **Msg 15250**, an unresolvable name → **Msg 15009**.
  `@objname` resolves across the schema's whole object namespace **plus the table-attached constraints** — real exposes CHECK / DEFAULT / key / foreign-key constraints as objects with their own ids, so `sp_helptext 'CK_x'` and `sp_help 'CK_x'` both bind.
  Database-scoped DDL triggers are not reachable (probe-confirmed: `OBJECT_ID` can't see one, so real answers Msg 15009 too).
  - **`sp_helptext @objname [, @columnname]`** yields one `Text nvarchar(255)` column carrying the same stored definition `sys.sql_modules.definition` / `OBJECT_DEFINITION` project.
    The line rule is real's, not fixed 255-char chunking: the definition splits at each **CR+LF pair** (the pair stays on the end of its line) and only a resulting line longer than 255 characters is further cut into 255-char pieces — so an LF-only module body under 255 characters comes back as a **single row with the newlines embedded**, and a lone CR or lone LF is not a break.
    A definition ending in CR+LF yields no trailing empty row.
    Modules (procedure / view / scalar / inline / multi-statement function / DML trigger) and CHECK / DEFAULT constraints carry text; a table, sequence, synonym, key constraint, foreign key or CLR routine raises **Msg 15197**.
    `@columnname` reports a computed column's definition and is gated in real's order: a non-table `@objname` → **Msg 15218**, an unknown column → **Msg 15645**, a non-computed column → **Msg 15646**.
    A `WITH ENCRYPTION` module raises nothing — real emits the severity-10 **Msg 15471** and returns *no* result set (the branch ships; `WITH ENCRYPTION` itself is still parse-and-ignore, so no simulator module reaches it).
  - **`sp_helpindex @objname`** yields `index_name sysname` / `index_description varchar(210)` / `index_keys nvarchar(2126)`, one row per index of a table or indexed view, sorted by index name.
    `index_description` is real's clause phrase in its fixed order — clustered-ness, `, ignore duplicate keys`, `, unique`, `, primary key` | `, unique key`, then `located on PRIMARY`.
    `index_keys` lists **key columns only** (INCLUDE columns never appear) with `(-)` marking a descending key.
    No indexes → the severity-10 **Msg 15472** and no result set.
  - **`sp_helpconstraint @objname [, @nomsg]`** yields an `Object Name nvarchar(776)` echo (suppressed by `@nomsg = 'nomsg'`, the form `sp_help` uses), then the seven-column set `constraint_type nvarchar(256)` / `constraint_name sysname` / `delete_action` / `update_action varchar(11)` / `status_enabled varchar(8)` / `status_for_replication varchar(19)` / `constraint_keys nvarchar(2126)`, then a `Table is referenced by foreign key nvarchar(516)` set.
    `constraint_type` is `CHECK on column <col>` / `CHECK Table Level ` (real's trailing space is verbatim) / `DEFAULT on column <col>` / `PRIMARY KEY (clustered)` | `(non-clustered)` / `UNIQUE (…)` / `FOREIGN KEY`; a foreign key contributes a **second, all-blank row** whose only content is `REFERENCES <db>.<schema>.<table> (cols)`.
    Referential actions render as `No Action` / `Cascade` / `Set Null` / `Set Default`; a `NOCHECK`ed CHECK or FK reports `Disabled`.
    Rows sort by constraint name with the blank continuation row second.
    An empty constraint set → severity-10 **Msg 15469**; an empty referencing set → **Msg 15470**.
  - **`sp_help [@objname]`** emits a sequence of result sets whose membership depends on what the name resolved to.
    **No argument**: every object in the current database (`Name` / `Owner sysname`, `Object_type nvarchar(31)` — the `spt_values` `'O9T'` display text like `user table` / `view` / `check cns`), ordered by owner ascending, object type **descending**, name ascending; then every user-defined type (alias types and table types) ordered by name.
    **A user-defined type name**: the nine-column set `Type_name` / `Storage_type sysname` / `Length smallint` / `Prec` / `Scale int` / `Nullable varchar(35)` / `Default_name` / `Rule_name` / `Collation sysname` (a table type reports `Storage_type = 'table type'`, `Length -1`, `Prec 0`).
    Objects win over types on a name collision (real looks in `sys.all_objects` first).
    **An object name**: `Name` / `Owner sysname` / `Type nvarchar(31)` / `Created_datetime datetime`; then, when the object has columns, the ten-column detail set `Column_name` / `Type sysname` / `Computed` / `Length int` / `Prec` / `Scale varchar(5)` / `Nullable` / `TrimTrailingBlanks` / `FixedLenNullInSource varchar(35)` / `Collation sysname`; then — for type `S ` / `U ` / `V ` / `TF` only, so an *inline* TVF is excluded — the `Identity` / `Seed` / `Increment numeric(38,0)` / `Not For Replication int` set and the `RowGuidCol` set; then, when the object has parameters, `Parameter_name` / `Type sysname` / `Length smallint` / `Prec` / `Scale` / `Param_order int` / `Collation sysname` (a scalar function leads with its return value as an empty-named row at `Param_order` 0).
    A **table** additionally gets `Data_located_on_filegroup` (always `PRIMARY`), `sp_helpindex`'s set, `sp_helpconstraint @nomsg = 'nomsg'`'s sets, and a `Table is referenced by views` set (or severity-10 **Msg 15647**).
    A **view** gets the Msg 15469 / 15470 pair real prints unconditionally, then its own index set.
    `Length` is the `sys.columns` byte width; `Prec` / `Scale` are `char(5)`-padded and blank for the types real excludes (`bit`, `datetime`, `smalldatetime`, the string / binary / LOB / `uniqueidentifier` / `xml` / `sql_variant` families).
    `Prec` is the ODBC **display** width, so a scaled date/time type adds one for its decimal point (`datetime2(3)` → 23, `time(4)` → 13, `datetimeoffset(2)` → 29) — this is `ColumnProperty(…, 'precision')`, not `sys.columns.precision`.
    `TrimTrailingBlanks` reports `no` for `char` / `varchar` / `binary` / `varbinary` / `sql_variant` (ANSI_PADDING is always on) and `(n/a)` elsewhere — including `nchar` / `nvarchar`, matching real; `FixedLenNullInSource` reports the nullability for `char` / `varchar` / `binary` / `varbinary` and `(n/a)` elsewhere.
  - **Divergences.** An error one of these procs raises aborts the whole batch here; on real it is `raiserror` inside a procedure, so the proc returns and the **batch continues** (probe-confirmed for Msg 15009 / 15010 / 15330).
    This holds for the whole family, `sp_helpstats` and `sp_helprotect` included.
    Real interleaves `print ' '` blanks between its result sets; the simulator omits them, since they carry no data and the batch-wide info-message coalescing would merge them into the meaningful severity-10 text.
    Those severity-10 messages carry class 10 rather than the class 0 real's wire maps severity ≤ 10 to — the simulator's existing `RAISERROR` severity-to-class convention, shared with `PRINT`.
    The no-argument object list projects **user** objects and their constraints only; real also lists the ~2000 `sys` objects, the same user-object-parity shortcut `sp_stored_procedures` takes.
    `Owner` is `dbo` for every user schema (schemas carry no principal model) and the schema name for `sys` / `INFORMATION_SCHEMA`.
    `Table is referenced by views` lists the schema-bound views the simulator tracks as dependencies (indexed views); a non-indexed schema-bound view isn't tracked, so it falls to Msg 15647.
    A constraint reports its **parent table's** create date (constraints carry no timestamp of their own).
    A column typed with an alias type reports the underlying base type name — the simulator doesn't track a column's alias type, matching `sys.columns`.
- **`sp_spaceused`** (`Simulation.SpaceUsed.cs`, dispatched like `sp_tables`): the size report for one object or for the whole current database.
  Result-set shapes, column types, wording and the KB / MB rendering are probe-confirmed against SQL Server 2025; the arithmetic mirrors the shipped procedure's own body applied to the simulator's page model.
  Page counts come from the same per-(table, index) identities `sys.dm_db_partition_stats` projects (`BuiltInResources.SpaceUsedTotals`), so the proc and the DMV can't disagree, and the database-level file size is the one `sys.database_files` reports.
  - **No `@objname`** yields two sets — `database_name nvarchar(128)` / `database_size` / `unallocated space varchar(18)`, then `reserved` / `data` / `index_size` / `unused varchar(18)`.
    `@oneresultset = 1` fuses them into one seven-column set, and `@include_total_xtp_storage = 1` extends that with `xtp_precreated` / `xtp_used` / `xtp_pending_truncation` — always NULL, which is what real reports for a database with no memory-optimized filegroup.
  - **With `@objname`** yields `name nvarchar(128)` / `rows char(20)` plus the same four size cells.
    A **view** always takes real's no-partition-stats branch (an indexed view's rows aren't materialized here): one row with NULL `rows` / `reserved` / `data`, typed `int`, and `'0 KB'` for `index_size` / `unused`.
  - `@updateusage` accepts `true` / `false`; `true` emits real's trailing single-space `PRINT` and recomputes nothing (the page counts are read live).
    Anything else → **Msg 15143** with the value lower-cased, as real lower-cases it before validating.
  - `@mode` accepts `ALL` / `LOCAL_ONLY` / `REMOTE_ONLY`; anything else → **Msg 14822**, and `REMOTE_ONLY` → **Msg 14821** (no database is stretched) with state 1 on the database form and 2 on the object form.
  - Name resolution shares the `sp_help` preamble: **Msg 15250** for another database's qualifier, **Msg 15009** for an unresolvable name, and **Msg 15234** for an object kind with no storage (a procedure, a function, a constraint).
  - **Divergences.** The page model reserves exactly what it uses, so `unused` is always `0 KB`; a nonclustered index contributes its base table's page count, the way the DMV already reports it.
- **`sp_who` / `sp_who2`** (`Simulation.SessionProcs.cs`, dispatched like `sp_tables`): the session lists, one row per open `SimulatedDbConnection` in the live registry, sorted by spid.
  `@loginame` selects the same three ways real does — a numeric argument is a spid, `'active'` drops the sessions whose command is `AWAITING COMMAND`, anything else is a login name (unrecognized → **Msg 15007**, checked against the login registry plus `sa` and the logins the live sessions report, so a known login with no session simply reports nothing).
  - **`sp_who`** yields `spid` / `ecid smallint`, `status nchar(30)`, `loginame nvarchar(128)`, `hostname nchar(128)`, `blk char(5)`, `dbname nvarchar(128)`, `cmd nchar(26)`, `request_id int`.
    `status` is `runnable` for a session with a statement in flight, `suspended` for one blocked on a lock, `sleeping` otherwise; `blk` is the spid of a conflicting lock holder — the same attribution `sys.dm_os_waiting_tasks.blocking_session_id` makes — or `0`.
    `loginame` is the session's original login, `dbname` follows `USE`.
  - **`sp_who2`** yields `SPID char(5)`, `Status nchar(30)`, `Login`, `HostName`, `BlkBy char(5)`, `DBName`, `Command`, `CPUTime`, `DiskIO`, `LastBatch`, `ProgramName`, a repeated `SPID`, and `REQUESTID char(5)`.
    Real builds this set through a generated `EXEC()` whose `substring(col, 1, N)` widths are the measured maximum data lengths of the rows being reported, so the column types vary with the data; that measurement is reproduced, including its per-column floor and its use of **byte** length for `Login` (real's `datalength` over an nvarchar) against character length for the rest.
    `Status` lower-cases `sleeping` and upper-cases everything else, and `HostName` / `BlkBy` render real's `"  ."` placeholder for an empty host name / an unblocked session.
    `HostName` and `ProgramName` read the session's reported client identity, the same fields `sys.dm_exec_sessions.host_name` / `program_name` project.
  - **Divergences.** `ecid` is 0 (no parallel worker threads) and `request_id` is 0.
    `CPUTime` and `DiskIO` report `0`: neither is metered per session, and `0` is what real reports for a session that has done neither.
    `LastBatch` is the session's login instant in real's `MM/DD hh:mm:ss` rendering, the nearest analogue to a last-batch timestamp.
    `cmd` is `AWAITING COMMAND` for a session with no statement in flight and the generic `SELECT` otherwise — real reports the statement's own verb (`UPDATE`, `MERGE`, `ALTER TABLE`), which isn't tracked per session; `SELECT` is exactly what real reports for the session running `sp_who` itself.
- **`sp_helpdb`** (`Simulation.HelpProcs.Database.cs`, dispatched like `sp_tables`): the database list, or one database's detail plus its file allocation.
  Yields `name sysname` / `db_size nvarchar(13)` / `owner sysname` / `dbid smallint` / `created nvarchar(11)` / `status nvarchar(600)` / `compatibility_level tinyint`, sorted by name.
  `created` is real's `convert(nvarchar(11), crdate)` — style-0 datetime text cut to its date prefix, so the day is space-padded (`Apr  8 2003`).
  `status` is assembled in real's clause order from the same `DATABASEPROPERTYEX` values the scalar function serves, so it tracks live state rather than a canned list; `IsRecursiveTriggersEnabled` was added to that scalar's accept-list so both surfaces agree.
  A database `HAS_DBACCESS` reports 0 for is dropped from the report with the severity-10 **Msg 15622** in its place — which excludes `model` (the restricted template) from the no-argument listing.
  An unknown `@dbname` → **Msg 15010**.
  With one argument the summary is followed by real's single-space `PRINT` and `sp_helpfile`'s own set: `name sysname` / `fileid smallint` / `filename nchar(260)` / `filegroup sysname` / `size` / `maxsize` / `growth nvarchar(18)` / `usage varchar(9)`, over the same synthetic two-file model `sys.database_files` / `sys.master_files` project (`<db>_Data` on PRIMARY, `<db>_Log` with a NULL filegroup, unlimited max size, 64 MB growth).
  - **Divergences.** `owner` is `dbo` for every database — schemas and databases carry no owner principal.
- **`sp_helpfile`** (`Simulation.HelpProcs.Database.cs`, dispatched like `sp_tables`): the current database's file allocation, over the same synthetic two-file model `sp_helpdb`'s appended set reports.
  With no argument it is that set verbatim; `@filename` selects one file through real's *second* SELECT, which drops the `fileid` column and keeps the other seven.
  Name matching is trailing-space insensitive, the way `FILE_ID`'s is; a name neither file carries → **Msg 15325**.
- **`sp_helpstats`** (`Simulation.HelpProcs.cs`, dispatched like `sp_tables`): a table's or indexed view's statistics as `statistics_name sysname` / `statistics_keys nvarchar(2078)`, sorted by name.
  The key list is real's `index_col` walk, so it names **key columns only** and marks no direction — the one rendering difference from `sp_helpindex`'s `index_keys`.
  `@results` defaults to `STATS` (statistics not backed by an index) and accepts `ALL` (those plus the index-backed ones); it is declared `nvarchar(5)`, so a longer argument is truncated before validation (probe-confirmed: `'statsZZZ'` is accepted as `stats`, `'ALLXX'` is not), and an unrecognized value emits the severity-1 **Msg 50000** `Invalid option: …` and no result set.
  Name resolution shares the `sp_help` preamble (**Msg 15250** / **Msg 15009**), and real checks `@results` only *after* it.
  An empty report emits the severity-10 **Msg 15574** (`STATS`) or **Msg 15575** (`ALL`) and no result set.
  - **Divergences.** Only index-backed statistics are modeled (`sys.stats` carries no auto-created `_WA_Sys_*` rows and there is no `CREATE STATISTICS`), so the default `STATS` form always takes the Msg 15574 branch and `ALL` reports exactly the indexes.
- **`sp_helprotect`** (`Simulation.HelpProcs.Protect.cs`, dispatched like `sp_tables`): the permission report over `Database.Permissions`, as `Owner` / `Object` / `Grantee` / `Grantor` / `ProtectType char(10)` / `Action` / `Column`.
  Rows sort by permission area (object rows first), then owner, object, grantee, grantor, protect type, action and column ordinal.
  `@permissionarea` selects the areas by letter — `o` for object permissions (`sys.database_permissions.class` 1), `s` for statement permissions (class 0, major_id 0), default `'o s'`; a value carrying neither letter → **Msg 15300** reporting the upper-cased value.
  Schema-scope (class 3) and principal-scope (class 4) grants carry no area letter, so this report never shows them — real's own coverage, not a gap.
  `@name` filters an object's schema and name, or a statement permission's name (`sp_helprotect 'CREATE TABLE'`); a database qualifier on it → **Msg 15302** (distinct from the `sp_help` family's Msg 15250 — this proc refuses the qualifier rather than checking it) and an unparseable identifier → **Msg 15253**.
  `@username` / `@grantorname` naming no principal match nothing rather than raising, and a filter that selects no rows → **Msg 15330**.
  `Action` is the mixed-case Shiloh spelling for the permissions `sysprotects` could express (`Select` / `Insert` / `Update` / `Delete` / `References` / `Execute` / `Create Table` / `Create View` / `Create Procedure` / `Create Function` / `Create Default` / `Create Rule` / `Create Database` / `Backup Database` / `Backup Transaction` — note the last is what real reports for `BACKUP LOG`) and the uppercase canonical `permission_name` for everything else (`ALTER`, `CONTROL`, `VIEW DEFINITION`, `CONNECT`, …).
  `ProtectType` is `Grant` / `Deny` / `Grant_WGO`, space-padded to the `char(10)` real's temp table declares.
  The `Column` cell is `.` for a permission with no column form; for SELECT / UPDATE / REFERENCES it is `(All)` when the object-level grant stands alone, `(All+New)` on a base table (whose column set can still grow), and the column's own name for a column-level row.
  An object-level grant that coexists with column-level rows for the same grantee, grantor and action reads `(New)` on a table / `.` on a view and is additionally **expanded** into one row per column it still covers.
  The six name columns are typed the way real's generated `EXEC()` types them — `substring(col, 1, max(datalength(col)))` — so each width is twice the longest reported value's character count, capped at the source column's own width (`sysname`, or `nvarchar(60)` for `Action`); probe-confirmed against SqlClient's schema table.
  - **Divergences.** Real applies **no** `@name` / `@username` / `@grantorname` filter to the column-expansion pass, so a filtered report can carry another object's expanded rows; that omission is reproduced (probe-confirmed).
    `Grantor` is `dbo` for every row the simulator stores, since an unimpersonated session grants as `dbo`.
    A column-level row's `minor_id` is the column's 1-based position (`Simulation.ResolveColumnMinorId`'s convention) rather than `sys.columns.column_id`, so the expanded `Column` cell reads that position — the two agree unless a column has been dropped.
    Real's report also lists the `public` grants a fresh database carries on its `sys` views; the simulator seeds no such rows, so only what `GRANT` / `DENY` wrote (plus each user's auto-seeded `CONNECT`) appears.
- **`sp_helptrigger`** (`Simulation.HelpProcs.Principals.cs`, dispatched like `sp_tables`): one row per DML trigger attached to a table or view — `trigger_name` / `trigger_owner sysname`, the five `isupdate` / `isdelete` / `isinsert` / `isafter` / `isinsteadof int` flags real reads out of `OBJECTPROPERTY`, and `trigger_schema sysname` — sorted by trigger name.
  `@triggertype` restricts to `insert` / `update` / `delete`; anything else → **Msg 15305**.
  A name that resolves to something other than a table or a view → **Msg 15009**, since real filters `sys.objects` to `type in ('U','V')` before its own existence check.
- **`sp_helpuser`** (`Simulation.HelpProcs.Principals.cs`, dispatched like `sp_tables`): the database's users and roles.
  With no argument or a user name it yields one row per (user, role-membership) pair — `UserName` / `RoleName` / `LoginName` / `DefDBName` / `DefSchemaName` (each an nvarchar whose width real measures from the reported rows the way `sp_who2` measures its own), `UserID char(10)` and `SID varbinary(85)` — sorted by user name, with `public` standing in for a user that belongs to no role and database roles themselves excluded.
  A role name instead yields the membership set `Role_name nvarchar(25)` / `Role_id int` / `Users_in_role nvarchar(25)` / `Userid int`.
  A name that is neither → **Msg 15198**.
  - **Divergences.** `DefSchemaName` and `SID` report NULL, which is what `sys.database_principals` reports for the same principals — the principal model carries no per-*user* default schema (every name resolves through `dbo`; only an application role tracks one) and no security identifier.
    `LoginName` is the user's `CREATE USER … FOR LOGIN` link rather than a SID join, and `DefDBName` is that login's default database, the `master` every login reports through `sys.server_principals`.
- **`sp_MSforeachtable`** (`Simulation.ForEachTable.cs`, dispatched like `sp_tables`): runs one to three command templates once per user table, with the table's bracketed `[schema].[table]` name substituted for each `@replacechar` (default `?`) occurrence.
  `@precommand` runs once first, `@command1` / `@command2` / `@command3` run once per table in that order, `@postcommand` runs once last; every one is dispatched through the same `ExecuteDynamicBatch` path `EXEC('…')` uses, so each yields its own result sets, row counts and errors to the caller.
  The table list is the one real's cursor selects — every object `OBJECTPROPERTY(id, 'IsUserTable')` reports 1 for whose `sysobjects.category` carries no MS-shipped bit — and `@whereand` is appended to that query verbatim, so the usual `'and o.name like ''…'''` filters bind against the same `o` alias real exposes.
  Substitution follows real's rules for the character preceding `@replacechar`: a `'` doubles every quote in the name and a `[` doubles every closing bracket; anywhere else the already-bracketed name goes in as-is.
  - **Divergences.** Real reads `dbo.sysobjects`; the simulator registers the legacy view unqualified and under `sys`, so the generated query says `sysobjects o`.
    The table list is materialized up front instead of driven by a global `hCForEachTable` cursor, so that cursor isn't observable and `sp_MSforeach_worker` isn't separately dispatchable.
    Real's worker re-escapes an already-bracketed name a second time for a bare `?`, so a table whose name contains `]` comes back over-escaped there and correctly escaped here.
    The `++` command-continuation prefix and the 2000-character overflow splitting real's worker performs aren't modeled; a command is expanded and dispatched whole.
- **`sp_MSforeachdb`** (`Simulation.ForEachDatabase.cs`, dispatched like `sp_tables`): the same shape one scope out — one to three command templates run once per accessible database, `@precommand` first and `@postcommand` last, each its own dynamic batch.
  Its parameter list carries **no `@whereand`**, so a positional `@precommand` sits one place earlier than `sp_MSforeachtable`'s; the database list therefore comes from catalog state in C# (`DbId.DatabasesWithIds` filtered by `HAS_DBACCESS`, so system databases are included and `model` is not) rather than from an embedded query.
  The proc does **not** switch database context on the caller's behalf — probe-confirmed: a command reading `DB_NAME()` reports the session's own database every time.
  Running against each database is the caller's job through the idiomatic `'USE [?]; …'` command, and because each command is its own dynamic batch that `USE` binds for that command only (see [`programmable.md`](programmable.md#dynamic-sql-exec-sql--sp_executesql)).
  Substitution follows the same preceding-character rules, with one difference: a bare `?` expands to `[<database>]`, because real's worker `QUOTENAME`s a name that isn't already bracketed.
  - **Divergences.** The database list is materialized up front instead of driven by a global `hCForEachDatabase` cursor, so that cursor isn't observable and `sp_MSforeach_worker` isn't separately dispatchable.
    Real's cursor also drops a database whose `sysdatabases.status` carries an inaccessible bit or whose `UserAccess` is `SINGLE_USER`; no simulator database is either, so only the `HAS_DBACCESS` filter carries.
- **`sp_xml_preparedocument` / `sp_xml_removedocument`** (`Simulation.OpenXml.cs`, dispatched like `sp_tables`): the session-scoped document store `OPENXML` reads, with an `@hdoc OUTPUT` handle bound the way `sp_setapprole`'s `@cookie` is — see [`xml.md`](xml.md#openxml).
- **`SCHEMA_ID([name])`** scalar: no-arg returns `Database.DboSchemaId` (=1) — the simulator's "caller default schema" (no user model means dbo is universal).
  With an arg, returns the schema's id or NULL.
- **Legacy SQL-Server-2000 compatibility views** (`BuiltInResources.LegacyCompat.cs`): `sysobjects` / `sysusers` and `sys.system_objects`, the surface SSMS's Database-Properties dialog reaches.
  - **`sysobjects`** (25-column legacy shape) / **`sysusers`** (20-column) resolve **unqualified** — probe-confirmed against SQL Server 2025: bare `SELECT … FROM sysobjects` works while bare `objects` / `tables` raise Msg 208 (the modern catalog views require the `sys.` qualifier).
    Both live in the `sys` schema, so they're registered under **both** the bare leaf key (the 1-part path added to `BatchContext.TryResolveCatalogView`) and the `sys.<name>` key (2-part); every modern catalog view is keyed `sys.<name>` / `INFORMATION_SCHEMA.<name>`, so a bare user-table name never collides.
    They project **live metadata**: `sysobjects` emits one row per schema object (tables / views / procs / functions / triggers, `type` = the object's `ObjectTypeCode` — `'U '`/`'V '`/`'P '`/`'FN'`/`'IF'`/`'TR'`) plus one row per PK/UNIQUE (`'K '`), CHECK (`'C '`), and FK (`'F '`) constraint, with `id = object_id`, `uid = schema_id`; columns SSMS doesn't read surface as 0.
    `sysusers` projects `Database.Principals` (the fixed public=0 / dbo=1 / guest=2 / INFORMATION_SCHEMA=3 / sys=4 plus CREATE USER/ROLE principals) with `uid = principal_id` — which **coincides with `schema_id`** for the fixed principals, so the `sysobjects.uid = sysusers.uid` join lands.
    `issqluser`/`issqlrole`/`isapprole` derive from the principal `TypeCode`; `hasdbaccess` is 1 for dbo and non-fixed SQL users.
    SSMS's aggregate-function enumeration (`… FROM sysobjects so JOIN sysusers su ON so.uid = su.uid JOIN INFORMATION_SCHEMA.ROUTINES isr ON so.name = isr.SPECIFIC_NAME WHERE so.type = N'AF'`) returns zero rows (no CLR aggregates modeled) — cleanly, now that the join resolves.
  - **`sys.system_objects`** (10-column, same shape as `sys.objects` / `sys.all_objects`) is an **honest projection of the simulator's actual system surface**: every distinct modeled catalog view (as a `'V '` row carrying `CatalogView.ObjectId`, `schema_id` 4 for `sys.*` / 3 for `INFORMATION_SCHEMA.*`) plus the modeled system procedures (`BuiltInResources.SystemProcedureNames` — the canonical list shared with `Simulation.ResolveSystemProcedureName`, `'P '` for `sp_*` / `'X '` for `xp_*`, deterministic negative object_ids).
    It **deliberately omits `sp_db_vardecimal_storage_format`** — SSMS's Database-Properties vardecimal probe gates `insert #tmp exec sys.sp_db_vardecimal_storage_format` on `if exists (select … from sys.system_objects where name = N'sp_db_vardecimal_storage_format')`; the honest absence makes SSMS **skip the (unmodeled) proc call and read vardecimal storage as OFF**, which is the correct simulator answer.
    Modeling the proc would force actually implementing it; the empty gate is the right fidelity move.
    The full vardecimal batch (create `#tmp` / gated insert-exec / select / drop `#tmp`) replays clean end-to-end with the gate skipping.
  - **`master.dbo.spt_values`** — the static SQL-Server compatibility helper table (a `dbo`-schema **table** in `master`, not a `sys` catalog view).
    SMO's Table space math reads it for the page size: `select @PageSize = v.low / 1024.0 from master.dbo.spt_values v where v.number = 1 and v.type = 'E'` (the `WINDOWS/NT` row, `low` = 8192 → 8 KB).
    Registered under the `dbo.spt_values` key (serving the 3-part `master.dbo.spt_values` and 2-part `dbo.spt_values` forms) and the bare `spt_values` key (unqualified 1-part), both flagged `CatalogView.MasterScoped` so `BatchContext.TryResolveCatalogView` binds them **only when the reference lands in `master`** (unqualified `spt_values` in a user database → Msg 208; the 3-part `master.dbo.spt_values` resolves from anywhere).
    **Only the two type codes SMO / SSMS actually reference are modeled** (probe-confirmed against the harvested space queries): type **`'E'`** — the four environment rows (`number` 0..3: `SQLSERVER HOST TYPE`/0, `WINDOWS/NT`/8192, `int high bit`/`int.MinValue`, `int4 high byte`/1; the load-bearing page-size source), and type **`'P'`** — the 2048-row power-of-2 helper (`number` 0..2047, `low = number / 8 + 1`, `high = 1 << (number % 8)`, `name` NULL — the commonly-referenced bitmask/numbers helper).
    The other ~27 type codes a live `master` carries (A/B/D/D2/DBR/…) are **deliberately omitted** — no modeled tooling reads them.
    Shape probe-confirmed (SQL Server 2025): `name nvarchar(35)`, `number int NOT NULL`, `type nchar(3) NOT NULL`, `low`/`high`/`status int` NULL.
    Static data (row generator ignores the database argument), built once by `BuildSptValuesRows`; registered in `BuiltInResources.LegacyCompat.cs`.
  - **`sysconfigures`** — the legacy SQL-Server-2000-shaped projection of the server-configuration catalog (`RegisterSysconfigures` in `BuiltInResources.LegacyCompat.cs`).
    DacFx's bacpac-export preamble reads `SELECT [c].[value] FROM [master].[dbo].[sysconfigures] AS [c] WITH (NOLOCK) WHERE [c].[config] = 1126` (→ 1033, the default full-text language).
    **Four columns** (`value int`, `config int`, `comment nvarchar`, `status smallint`) — narrower than `sys.configurations`, and with **no `name` column** (probe-confirmed: selecting `name` raises Msg 207).
    Rows mirror `sys.configurations` (`BuildSysconfiguresRows` reuses `ConfigurationData`): `value` = the configured value (int — every stock value fits int range), `config` = `configuration_id`, `comment` = `description`, and **`status = is_dynamic + 2 * is_advanced`** (probe-confirmed: config 1126 → status 3 = dynamic + advanced, config 102 → status 1 = dynamic only).
    `sp_configure` writes show through here too, on the `value` column — the narrower legacy shape has nowhere to carry the installed value.
    **Not `MasterScoped`** (unlike `spt_values`): probe-confirmed against SQL Server 2025 it resolves from **every** database under the bare leaf (`sysconfigures`), the `sys.` qualifier, and the `dbo.` qualifier — so it's registered under all three keys (`sysconfigures`, `sys.sysconfigures`, `dbo.sysconfigures`); the 3-part `master.dbo.sysconfigures` DacFx uses routes through the `dbo.sysconfigures` key.
    Built once per simulation; only the `value` overlay varies.

## Expression dependencies

Six surfaces answer "what depends on this", all off one analysis in `Schemas/ModuleDependencies.cs`:

- **`sys.sql_expression_dependencies`** — the catalog view (`BuiltInResources.Dependencies.cs`).
- **`sys.dm_sql_referencing_entities(name, class)`** / **`sys.dm_sql_referenced_entities(name, class)`** — two-argument system TVFs (`Parser/Selection.SqlDependencies.cs`), dispatched from the FROM-source parser on the same `sys.`-qualified terms as `fn_virtualfilestats`.
- **`sys.sql_dependencies`** and **`sysdepends`** — the two views `sys.sql_expression_dependencies` replaced, same file as the catalog view. See [The legacy pair](#the-legacy-pair-syssql_dependencies-and-sysdepends).
- **`sp_depends`** — the deprecated report (`Simulation/Simulation.Depends.cs`).

### Computed on read, never stored

The graph is derived from each entity's own saved definition text every time a surface asks for it, the way [`SchemaBinding`](programmable.md#schema-binding-with-schemabinding) derives its gate.
That isn't only cheaper than a registry — it *is* real's semantic, and each of these was probed:

| event | what real reports |
| --- | --- |
| `ALTER` of the referencing module | rows refresh to the new body |
| `DROP` of the referenced object | the row survives, name intact, `referenced_id` NULL |
| recreating an object of that name | `referenced_id` comes back |
| `sp_rename` of the referenced table | the row keeps naming the **old** name, `referenced_id` NULL |

Every one of those falls out of recomputing from definition text, which is what makes the store name-based rather than id-based.

### What contributes a row

Extraction is a token walk over the stored definition — the same shape `SchemaBinding` and `ModuleDeterminism` use, and for the same reason (scalar-function, procedure and trigger bodies are stored as source, so there is no expression tree at CREATE).
The walk splits a definition into statement frames, classifies each dotted name chain by the keyword introducing it, and resolves the result against the live schema.

Referencing kinds, all probe-confirmed to record:

- the four module kinds (view, procedure, scalar UDF, inline TVF, multi-statement TVF) and DML triggers — `referencing_class` 1, `referencing_minor_id` 0;
- database-scoped DDL triggers — `referencing_class` **12** / `DATABASE_DDL_TRIGGER`;
- a **computed column** — under its *table's* object id with the column's own `column_id` as `referencing_minor_id`;
- a **CHECK** or **DEFAULT** constraint — under the constraint's own object id.

Referenced kinds: tables, views, synonyms (recorded as the synonym, never the base behind it), sequences reached through `NEXT VALUE FOR`, functions, procedures reached through `EXEC`, and a procedure's **table-valued parameter** — the one `referenced_class` **6** / `TYPE` row, carrying the type's `user_type_id` and taken off the parameter declaration rather than the body.

Recording **nothing**, all probe-confirmed: dynamic SQL (`EXEC('…')` is invisible to real too), `#temp` tables, table variables, a trigger's `INSERTED` / `DELETED`, and system objects (`sys.*` / `INFORMATION_SCHEMA.*`, in this database or another).

### The flag rules

- **`is_schema_bound_reference`** — 1 for a `WITH SCHEMABINDING` module body and for every computed-column / CHECK / DEFAULT expression.
- **`is_caller_dependent`** — 1 for a one-part `EXEC` name, whose row carries a NULL schema *and* a NULL id even when a procedure of that name exists.
  An unqualified name in a **FROM** clause is the opposite: NULL schema, resolved id.
- **`is_ambiguous`** — 1 for a two-part call whose qualifier names no schema, since the binder can't tell `schema.function()` from an XML or UDT method on a column.
  Probe-confirmed in both directions: an unresolvable `mystery.value('…')` and a genuine `doc.value('…')` over a real `xml` column both report it, with the qualifier as `referenced_schema_name`.
- **Cross-database / cross-server** — the leading segments are kept and `referenced_id` is NULL (ids are database-local).

### Column rows

The two surfaces split on who gets them:

- **`sys.sql_expression_dependencies`** emits a `referenced_minor_id = 0` row for every reference, and column rows **only for a schema-bound reference**.
  So a plain view over `dbo.t` is one row; the same view `WITH SCHEMABINDING` is that row plus one per bound column.
  A computed column / CHECK / DEFAULT is column rows *only* — it reaches its own table's columns without naming the table, so there is no minor-0 companion.
- **`sys.dm_sql_referenced_entities`** emits the object row plus column rows for **every** referencing kind, plain views and procedures included.

**Object-row flags follow the reference position, not the columns** (probe-confirmed): a body whose only mention of a table is `UPDATE t SET a = 5 WHERE b = 'q'` reports the object as `is_updated` and *not* `is_selected`, even though column `b` is read; the same body plus a `SELECT … FROM t` reports both.
Per-column, `a` is `is_updated` and `b` is `is_selected`.

Two shapes real folds:

- `SELECT *` sets `is_select_all` on the object row and on every column, and clears `is_selected` — even for a column the WHERE names separately.
- `INSERT t VALUES (…)` with no column list sets `is_insert_all` with **no** column rows; a column list instead marks those columns `is_updated`.

`is_all_columns_found` is 1 when the referenced object resolves.
A reference that doesn't resolve marks its rows `is_incomplete` and makes the DMV raise **Msg 2020** — real hands back the rows it found and *then* raises.

### The legacy pair: `sys.sql_dependencies` and `sysdepends`

The two views the expression-dependency catalog replaced, and the ones real's own `sp_depends` reads.
Both project the same rows out of `EnumerateLegacyDependencies`, differing only in shape:

| | `sys.sql_dependencies` | `sysdepends` |
| --- | --- | --- |
| referencing object | `object_id` / `column_id` | `id` / `number` |
| referenced object | `referenced_major_id` / `referenced_minor_id` | `depid` / `depnumber` |
| schema-bound | `class` 0 / 1 (+ `class_desc`) | `deptype` 0 / 1 |
| use flags | `is_selected` / `is_updated` / `is_select_all` | `readobj` / `resultobj` / `selall`, packed again into `status` |

`sysdepends` resolves both unqualified and under the `sys.` qualifier, the way `sysobjects` does; `sql_dependencies` takes the qualifier only (a bare `SELECT … FROM sql_dependencies` is **Msg 208** on real).
`status` is `8 * readobj + 4 * resultobj + 2 * selall` — so a whole-object read reads 8, an `UPDATE`'s SET column 4, a `SELECT *` column 2, and a column both read and starred 10.
`number` is the referencing entity's minor id — a computed column's own `column_id` — except on a procedure, where it is the procedure group number a numbered `CREATE PROC p;n` would set and 1 stands for the single ungrouped body.
`depdbid` / `depsiteid` addressed a cross-database or replicated dependency the legacy store never populated: 0 on every row.

Two things the legacy pair does that the modern surfaces don't:

- **It stores ids, not names.**
  So a reference whose id the analysis can't produce contributes nothing at all — another database or server, and an object that doesn't exist.
  A procedure's table-valued parameter is absent too: its `TYPE` class is outside a domain that is object-or-column only.
  Conversely the id is the one the reference actually binds to, so a one-part `EXEC` name reports the procedure the default schema holds where `sys.sql_expression_dependencies` reports NULL and `is_caller_dependent`.
- **The `referenced_minor_id = 0` row is narrower.**
  Real records the object itself only where the reference reaches none of its columns — a whole-object read (`SELECT 1 FROM t`), a `DELETE`, an `INSERT` carrying no column list, an `EXEC` or a function call — plus every schema-bound reference, which binds the object as well as its columns.
  A plain `SELECT a FROM t` reports column `a` and nothing else, where `sys.sql_expression_dependencies` reports the object row instead.

### `sp_depends`

Up to two result sets, each preceded by its own severity-10 header, matching real's `raiserror` calls:

| set | header | shape |
| --- | --- | --- |
| what the object references | **Msg 15459** | `name` / `type` / `updated` / `selected` / `column` |
| what references the object | **Msg 15460** | `name` / `type` |

An object on neither side gets **Msg 15461** and no result set; a three-part name naming another database is **Msg 15250**; a name that resolves to nothing is **Msg 15009**.
The `type` cell is real's `spt_values` type-`'O9T'` label (`user table`, `view`, `scalar function`, `inline function`, `table function`, `stored procedure`, `trigger`, `synonym`, `sequence object`, …).
A reference carrying no column detail — a function call, an `EXEC`, a synonym — reports a NULL `column` cell with both flags `no`.
The `selected` cell is real's `readobj | selall`, so a column reached through a `*` reads `yes` here even though the catalog view keeps `is_selected` and `is_select_all` apart.
A trigger is listed against what its **body reads**, never against the table it is attached to.

### Divergences

- **Column granularity is name-based**, inheriting `SchemaBinding`'s model: a statement frame counts as touching column `C` of referenced object `T` when it names `T` and mentions the identifier `C`, and `T` has a column by that name.
  A **qualified** mention narrows to the source its qualifier names, so a join, an `APPLY` and a `MERGE` between two objects sharing a column name each report exactly what real reports.
  What is left over-broad is an **unqualified** mention in a multi-source frame: `WHERE id IN (SELECT aid FROM dbo.b)` over `dbo.a` reports `id` against both, where real binds it to `a` alone.
  Closing that wants parse-time (source, ordinal) capture, which the per-row name-keyed resolver doesn't do.
- **A MERGE's target key column carries an extra `is_updated`** when the same column appears in both the `ON` clause and a `WHEN NOT MATCHED THEN INSERT` column list; real reports it selected only.
- **Msg 2020 arrives before the rows rather than after them.** Real yields `sys.dm_sql_referenced_entities`'s rows and then raises; the simulator's reader surfaces the error at `ExecuteReader`.
- **`sp_depends` row order is by object id.** Real's procedure carries no `ORDER BY`, so its order is unspecified; the simulator's is deterministic.
- **A computed-column / CHECK / DEFAULT expression marks the columns it names `is_selected`**, where real leaves all three use flags 0 for that referencing kind — visible on `sys.dm_sql_referenced_entities`, the legacy pair, and `sp_depends`'s `selected` cell alike, since all three read the one `ColumnUse`.
- **A reference mixing a whole-object write with a column-level one loses the object row in the legacy pair.**
  A procedure that both `DELETE`s from `t` and `INSERT`s `t (a)` reports real's `referenced_minor_id = 0` *and* column `a`; the simulator reports column `a` alone, because the aggregated `Reference` no longer says which statement contributed which.
  Every single-shape case matches.

## `sys.time_zone_info`

The Windows time-zone catalog, server-scope: `name` / `current_utc_offset` / `is_currently_dst`, 141 rows matching the reference server.
ORMs probe it as a zoneinfo-capability check (mssql-django's `has_zoneinfo_database` runs `SELECT TOP 1 1 FROM sys.time_zone_info`), and the capability is genuine — `AT TIME ZONE` already matches real including DST.

**Names are baked, offsets are computed.**
Real always reports Windows ids, while the ICU mapping behind `TimeZoneInfo` yields IANA names on Linux, so the id list is a probed constant (`BuiltInResources.TimeZones.cs`); the offset and DST flag resolve per query against the current instant the way real's do.
132 of the 141 ids resolve directly through `TimeZoneInfo.FindSystemTimeZoneById`; the rest go through a small IANA fallback table checked against the reference server's reported values.

**Divergence**: `Kamchatka Standard Time` and `Mid-Atlantic Standard Time` are deprecated Windows zones that Microsoft retains with obsolete DST rules and that have no IANA equivalent, so they report their standard offset with `is_currently_dst = 0` where real reports the DST-shifted offset.
The other 139 match.

## Stable column ids

`sys.columns.column_id` is a **stable identity**, not the column's position in `HeapTable.Columns`.
The two coincide until a `DROP COLUMN`, which shifts positions and leaves ids alone.
Probe-confirmed rules:

- Dropping a column leaves a **permanent hole** — a three-column table that loses its middle column keeps ids `1, 3`.
- `sys.tables.max_column_id_used` is a **monotonic watermark**: it doesn't shrink on DROP, and a newly added column takes `watermark + 1` rather than filling the hole (drop `c9` from a nine-column table and the next column added is id **10**).
  The watermark never resets, so ids keep climbing even as the live column count falls.
- `ALTER COLUMN` (a type change) and `sp_rename` both **preserve** the id.

**Storage**: `HeapColumn.ColumnId` plus `HeapTable.MaxColumnIdUsed`, seeded by `HeapTable.AssignColumnIds`.
Assignment **preserves an id that's already set**, which matters because the trigger pseudo-tables (`INSERTED` / `DELETED`) are constructed over the parent table's own `HeapColumn` instances — renumbering there would rewrite the parent's catalog identity.
`ALTER TABLE ADD` re-runs the same seeding to give new columns `watermark + 1`, and rolls the watermark back if the add fails.

**Reading it back**: `IndexLookup.StorageOrdinalToColumnId` is the single storage-ordinal → column_id authority.
Its sibling `StorageOrdinalToFullOrdinal` returns the **position** instead, and the distinction is load-bearing — callers indexing back into `HeapTable.Columns` (`INDEX_COL`, the FK referenced-key matcher, `INFORMATION_SCHEMA.KEY_COLUMN_USAGE`) want the ordinal, while everything reporting catalog metadata wants the id.
Index key / INCLUDE columns carry full ordinals, so `sys.index_columns` / `sys.stats_columns` map them through `FullOrdinalToColumnId`; an **indexed view's** index passes `table: null` there and keeps `ordinal + 1`, since a view's columns can't be dropped individually and its `sys.columns` ids are contiguous.

The `COLUMNS_UPDATED()` bitmask is keyed on these ids and sized from the watermark, so a dropped column keeps its bit position — see [`triggers.md`](triggers.md#change-detection-intrinsics).

## Key column direction

`ASC` / `DESC` on a key column is captured for `CREATE INDEX` (on `IndexKeyColumn.IsDescending`) and for `PRIMARY KEY` / `UNIQUE` constraints (on `KeyConstraint`, parallel to its `StorageOrdinals`), and surfaces identically from `sys.index_columns.is_descending_key` and `INDEXKEY_PROPERTY(…, 'IsDescending')`.

It has **no runtime effect** — the simulator stores rows unordered either way — so this is metadata a schema-diff or index-scripting tool reads.

Probe-confirmed shapes: the table-level `PRIMARY KEY (a DESC, b ASC)` and `UNIQUE (a DESC, b)` forms both record it, as does `ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY (b DESC, a DESC)`.
The **inline column-level** form takes no direction at all: real raises **Msg 156** near the keyword for `a int PRIMARY KEY DESC`, so the simulator rejects it there rather than silently accepting.

## Metadata scalars

Function-form metadata queries that read from the same underlying state as the catalog-view rows.

**`OBJECT_DEFINITION(object_id)`** (`Parser/Expressions/ObjectDefinition.cs`): the source-text definition of a programmable module (procedure / view / DML + DDL trigger / scalar / inline / multi-statement function), scoped to the current database; result type `nvarchar(max)`.
Reads `SchemaObject.DefinitionText`, captured at CREATE / ALTER time by `Simulation.BuildModuleDefinition` — a slice of the original command text from the statement's leading verb (`StatementContext.StartIndex`) through the end of the body.
**Byte-exact vs SQL Server** (probe-confirmed): preserves original comments / spacing / keyword casing / abbreviations, and normalizes the leading verb to `CREATE` for `ALTER` (`ALTER` keyword → `CREATE`, trailing spacing kept) and `CREATE OR ALTER` (the `OR` / `ALTER` keyword tokens removed but their surrounding whitespace kept, so `CREATE OR ALTER PROCEDURE` is stored as `CREATE   PROCEDURE`).
Returns NULL for a NULL / missing / non-module id and for modules created `WITH ENCRYPTION` (whose `DefinitionText` is null).
Single argument only — no `database_id` form (unlike `OBJECT_NAME`).
**One documented divergence**: leading whitespace / comment trivia *before* the `CREATE` keyword isn't captured (the tokenizer skips it before `StartIndex` is recorded), so a definition SQL Server stores with a leading `\n` comes back without it.
The canonical `OBJECT_DEFINITION(OBJECT_ID('trg'))` idiom works because `OBJECT_ID` resolves DML triggers (`'TR'`) via `BatchContext.TryResolveTrigger`.

**`OBJECTPROPERTY(object_id, property)`** (`Parser/Expressions/ObjectProperty.cs`): 10-property switch returning `int` (1 / 0 / NULL).
Recognized property names (case-insensitive, length-bucketed `Span<char>` switch per SSS003): `IsTable`, `IsView`, `IsProcedure`, `IsTrigger`, `IsScalarFunction`, `IsTableFunction`, `IsInlineFunction`, `IsMSShipped`, `IsDeterministic`, `IsSchemaBound`.
Unknown property → NULL (matches real SQL Server's silent-fall-through).
NULL `object_id` → NULL.
The lookup walks `Database.Schemas` for the matching object (same path as `OBJECT_NAME`).
`IsMSShipped` always returns 0 (the simulator owns no MS-shipped objects).
`IsSchemaBound` returns 1 for a `WITH SCHEMABINDING` view, scalar function, inline TVF or multi-statement TVF (read from `View.IsSchemaBound` / `UserDefinedFunction.IsSchemaBound`), 0 for a non-schema-bound one and for a procedure (a module that can never carry the option), and NULL for a non-module object — probe-confirmed that a table, trigger, sequence and synonym all answer NULL.
`IsDeterministic` walks the module's body and its references — see [its own section](#isdeterministic) below.
**`IsQuotedIdentOn` / `ExecIsQuotedIdentOn`** and **`IsAnsiNullsOn` / `ExecIsAnsiNullsOn`** each read the object's [creation-time SET-option capture](#creation-time-set-option-capture) and agree on every module; within a pair they diverge on a **table**, which the shorter spelling answers for while the module-only `ExecIs…` form answers NULL.
A table's `IsQuotedIdentOn` is 1 regardless of the creating session, while its `IsAnsiNullsOn` is the captured value.
A sequence, synonym or key constraint answers NULL to all four.
Capture semantics — including that a module's *body* runs under the captured `QUOTED_IDENTIFIER` — are in [`grammar.md`](grammar.md#per-object-creation-time-capture).

**Constraint object ids resolve too**, through `ObjectProperty.TryFindConstraint` → the shared `ConstraintLookup` — a `CheckConstraint` / `DefaultConstraint` / `KeyConstraint` / `ForeignKey` is not a `SchemaObject`, so the object walk above can't reach one.
A CHECK or DEFAULT constraint answers `IsQuotedIdentOn` = **0** (a constant, not the creating session's setting — probe-confirmed under ON as well as OFF, and uniformly 0 across msdb's shipped constraints) while the key and foreign-key families answer NULL; `IsAnsiNullsOn` is NULL for all five.
Every object-kind discriminator plus `IsEncrypted` / `IsMSShipped` / `IsSystemTable` answers 0, and the module- and table-scoped names answer NULL.
`OBJECTPROPERTYEX` gives the same answers.
`OBJECT_ID('<constraint name>')` reaches the same ids through the same lookup, so the property read composes the way it does for a table.

**`OBJECTPROPERTYEX(object_id, property)`** (`Parser/Expressions/ObjectPropertyEx.cs`): extension of `OBJECTPROPERTY` with non-integer-valued properties.
Like real SQL Server the result is always **`sql_variant`** (`GetSqlType` returns `SqlType.SqlVariant`), each property carrying its probed inner base type — `BaseType` as `char(2)`, `Cardinality` as `bigint`, and every other shipped property (`SchemaId`, the `Is*` booleans, the `TableHas*` flags) as `int`; SqlClient's `GetValue` surfaces the inner object.
(`OBJECTPROPERTY` itself stays `int` — real types only the `-Ex` form as `sql_variant`.)
Boolean Is-X properties delegate to the shared `ObjectProperty.EvaluateProperty` helper (same set as `OBJECTPROPERTY`).
The `TableHas*` family answers from **both** `OBJECTPROPERTY` and `OBJECTPROPERTYEX`, matching real (probe-confirmed), off one shared mapping; `BaseType` and `Cardinality` are the genuinely EX-only pair, returning NULL from the plain form because neither is integer-valued.
Extended properties (probe-confirmed against SQL Server 2025): `BaseType` (`'U '` / `'V '` / `'P '` / `'FN'` / `'IF'` / `'TF'` / `'TR'` / `'SO'`), `SchemaId`, `Cardinality` (row count from `Heap.RowCount`), `TableHasIdentity`, `TableHasPrimaryKey`, `TableHasClustIndex`, `TableHasIndex`, `TableHasUniqueCnst`, `TableHasCheckCnst`, `TableHasForeignKey`, `TableHasForeignRef`, `TableHasRowGuidCol` (1 when any column carries the `ROWGUIDCOL` marker, off the same `HeapColumn.IsRowGuidCol` that `sys.columns.is_rowguidcol` projects).
The `TableHas*` flags read directly from the resolved `HeapTable`'s `KeyConstraints` / `CheckConstraints` / `OutgoingForeignKeys` / `IncomingForeignKeys` / `Indexes`.
NULL `object_id` / NULL property / non-table object on a `TableHas*` query → NULL.

**`COLUMNPROPERTY(table_id, column_name, property)`** (`Parser/Expressions/ColumnProperty.cs`): per-column metadata returning `int`.
Properties (probe-confirmed): `AllowsNull` / `IsIdentity` / `IsComputed` (1/0 from `HeapColumn.Nullable` / `Identity` / `Computed`), `IsRowGuidCol` (1/0 from `HeapColumn.IsRowGuidCol`), `IsIdNotForRepl` (1 for an `IDENTITY … NOT FOR REPLICATION` column, else 0 — 0 on non-identity columns, probe-confirmed), `Precision` (decimal-equivalent for integer family, declared `N` for `varchar(N)` / `nvarchar(N)`, 19/10 for money/smallmoney), `Scale` (4 for money, declared scale for decimal, 0 otherwise), `CharMaxLen` (`N` for character types, NULL otherwise), `ColumnId` (the [stable column id](#stable-column-ids), agreeing with `sys.columns.column_id` after a DROP COLUMN — probe-confirmed that real reports the same value from both surfaces), `UsesAnsiTrim` (1 for character types, 0 otherwise).
Column lookup matches by name through `Collation.Baseline` (case-insensitive).
NULL on any arg / unknown column / unknown property / unknown table → NULL.

**`INDEXPROPERTY(object_id, index_name, property)`** (`Parser/Expressions/IndexProperty.cs`): per-index metadata returning `int`.
Index lookup unions `HeapTable.Indexes` (CREATE INDEX) and `HeapTable.KeyConstraints` (PK / UNIQUE — surface in `sys.indexes` by constraint name; the simulator auto-generates these as `PK__<table8>__<hex>`).
Properties: `IsClustered` / `IsUnique` (from `Index.IsClustered` / `Index.IsUnique`, or true / Kind=PK for constraint-backed entries), plus the always-0 properties `IsAutoStatistics`, `IndexDepth`, `IndexFillFactor`, `IsHypothetical`, `IsPadIndex`, `IsStatistics`, `IsFulltextKey`, `IsOptimizedForSequentialKey` (no B-tree / no stats; matches probed behavior on freshly-created indexes — `IsFulltextKey` is 0 rather than NULL because SMO's index-scripting query reads it without an `ISNULL` wrapper).
NULL on any arg / unknown index / unknown property / unknown table → NULL.

**`INDEX_COL(table, index_id, key_id)`** (`Parser/Expressions/IndexCol.cs`): the `sysname` name of the key column at position `key_id` (1-based) of the index identified by `index_id` on `table`.
Table-name argument follows the same dotted-string convention as `OBJECT_ID` (1- to 4-part with optional bracket quoting; the parser is reused verbatim).
`index_id` resolution reads the same allocation authority as `sys.indexes` via the shared `IndexLookup.ResolveByIndexId` → `HeapTable.IndexIdentities()` (see [`indexes.md`](indexes.md#index-id-allocation)): `index_id=1` is the clustered entry (clustered PK / UNIQUE constraint or `CREATE CLUSTERED INDEX`), a heap table's synthetic `index_id 0` yields NULL (no key columns), and `index_id≥2` are the nonclustered constraints + named indexes in object-id order.
INCLUDE columns are NOT reachable — only key positions.
NULL on any NULL arg / unknown index / out-of-range key (including ≤ 0) → NULL.

**`INDEXKEY_PROPERTY(object_id, index_id, key_id, property)`** (`Parser/Expressions/IndexKeyProperty.cs`): per-key-column metadata on an index, returning `int`.
Index / key resolution is identical to `INDEX_COL` (shared `IndexLookup` helpers).
Properties: `ColumnId` (the stable `sys.columns.column_id` via `IndexLookup.StorageOrdinalToColumnId`), `IsDescending` (1 if DESC, 0 if ASC).
Reads the same per-column direction `sys.index_columns.is_descending_key` reports, so a `PRIMARY KEY (a DESC)` key column answers 1 from both surfaces — see [key column direction](#key-column-direction).
NULL on any NULL arg / unknown object / unknown index / out-of-range key (INCLUDE positions included) / unknown property → NULL.

**`STATS_DATE(object_id, stats_id)`** (`Parser/Expressions/StatsDate.cs`): returns the `datetime` of the last statistics refresh.
**Intentional divergence from real**: real SQL Server returns NULL on a freshly-created index that hasn't triggered an auto-stats run yet; the simulator returns the owning `HeapTable.CreateDate` as a fake-but-realistic placeholder — the simulator has no stats lifecycle (no `UPDATE STATISTICS`, no auto-stats threshold tracking), and "stats were computed when the table was created" is consistent with the no-update-stats-yet reality.
`stats_id` follows the same `sys.indexes.index_id` resolution as `INDEX_COL` (standalone `CREATE STATISTICS` objects aren't modeled).
NULL on any NULL arg / unknown object / unknown index → NULL.

**`TYPEPROPERTY(type_name, property)`** (`Parser/Expressions/TypeProperty.cs`): per-system-type metadata returning `int`.
Backed by a static lookup table keyed by canonical lowercase type name (the 32 system types — `int` / `bigint` / `smallint` / `tinyint` / `bit` / `decimal` / `numeric` / `float` / `real` / `money` / `smallmoney` / `varchar` / `nvarchar` / `char` / `nchar` / `text` / `ntext` / `image` / `binary` / `varbinary` / `date` / `datetime` / `datetime2` / `smalldatetime` / `time` / `datetimeoffset` / `xml` / `uniqueidentifier` / `sysname` / `timestamp` / `hierarchyid` / `sql_variant`).
Properties: `Precision` (int=10, bigint=19, decimal=38, varchar=8000, money=19, hierarchyid=892, uniqueidentifier=16, sql_variant=0, **xml=-1**), `Scale` (int=0, money=4, decimal=38, time / datetime2=7, datetime=3), `AllowsNull` (0 for `sysname` / `timestamp`, 1 otherwise), `UsesAnsiTrim` (1 for `char` / `varchar` / `binary` / `varbinary` / `sql_variant`).
**A property the type has no value for answers NULL, not 0**, and that covers most of the table: only the exact-numeric and date/time types carry a `Scale`, and the national-character types answer NULL for `UsesAnsiTrim` where their single-byte counterparts answer 1.
The whole table is pinned row-by-row against SQL Server 2025 by `PropertyFunctionsTests.TypeProperty_Table_MatchesSqlServer` (2026-08-02).
**`integer` and `rowversion` are not names this function recognizes** — every property is NULL — even though the T-SQL grammar takes both as synonyms; their canonical `int` and `timestamp` resolve.
User-defined alias types (UDDT) aren't reachable via this function in the shipped slice — apps probing them are rare.
NULL on any arg / unknown type / unknown property → NULL.

**`SERVERPROPERTY(name)`** (`Parser/Expressions/ServerProperty.cs`): 36-property table of producers.
Like real SQL Server the result is always **`sql_variant`** (`GetSqlType` returns `SqlType.SqlVariant` unconditionally), and each property carries its probed inner base type — numeric properties an `int` inner (`EngineEdition`=3, `LCID`=1033, `CollationID`, `ComparisonStyle`, `EditionID`, the `Is*` flags, …) or `tinyint` (`SqlCharSet`=1, `SqlSortOrder`), string properties an `nvarchar` inner (`Edition`, `ProductVersion`, `MachineName`, `Collation`, …).
SSMS's `(int)SERVERPROPERTY('EngineEdition')` unbox works because the reader surfaces the inner `int` object (`GetValue`) while `GetDataTypeName` reports `sql_variant`.
`SqlSortOrder` is derived from the server collation's SQL sort-order id (`Collation.SqlServerSortOrders`; 52 for the default `SQL_Latin1_General_CP1_CI_AS`, 0 for collations with no SQL sort order); `SqlSortOrderName` reports `"nocase_iso"` only for sortId 52 (no name table ships), else `"BIN"`.
The version-identity properties mirror the SQL Server 2025 reference build 17.0.4065.4 (RTM-CU7): `ProductVersion` `"17.0.4065.4"`, `ProductBuild` `"4065"`, `ProductUpdateLevel` `"CU7"`, `ProductUpdateReference` `"KB5096981"`, `ResourceVersion` `"17.00.4065"`; `ProductBuildType` is NULL (real reports NULL on a CU build, non-null only for GDR/OD branches).
A `UNION`/`CASE` mixing a numeric property with a string one keeps each row's own inner type (no promotion, like real).
An unknown property (or NULL name) → a NULL `sql_variant`.

**`COL_LENGTH(table, col)`** / **`COL_NAME(table_id, col_id)`** (`Parser/Expressions/ColumnNameLength.cs`): heap-column metadata.
`COL_LENGTH` accepts a 1-/2-/3-part dotted runtime string for the table and returns `smallint` byte-length matching `sys.columns.max_length` (`nvarchar(50)→100`, `char(5)→5`, `-1` for MAX, `16` for text/ntext/image LOB pointers, `256` for sysname); missing table / missing column → NULL.
`COL_NAME` accepts an `object_id` + 1-based `column_id` and returns the column's leaf name as `sysname`; out-of-range column / missing object → NULL.
NULL on any arg propagates.

**`TYPE_NAME(type_id)`** / **`TYPE_ID(name)`** scalars: round-trip the 22 modeled base types through `SqlType.SystemTypeId`.
NULL → NULL; unknown id / name → NULL.
Type names follow SQL Server's lowercase conventions (`int`, `nvarchar`, `datetime2`, etc.).

**`PARSENAME('a.b.c.d', segment_index)`**: dot-split on the input, return the `segment_index`-from-the-right segment (1-based, so 1 = leaf, 4 = server).
Result type `sysname`.
Empty / NULL input → NULL; out-of-range index → NULL.
Treats bracket-quoting at the segment level (`'[a.b].c'` keeps the dotted segment intact when the outer quotes balance).
Common use in dynamic-SQL identifier manipulation.

**File / filegroup metadata scalars** (`Parser/Expressions/DatabaseScalarFunctions.cs`, `FilegroupProperty.cs`, `FileProperty.cs`) — return types probe-confirmed against SQL Server 2025:

- **`FILE_ID('file_name')`** (`smallint`) / **`FILE_IDEX('file_name')`** (`int`) / **`FILE_NAME(file_id)`** (`sysname`): the two forms of `FILE_ID` differ only in projected result type (real: FILE_ID → smallint, FILE_IDEX → int) and resolve identically over the placeholder file model.
- **`FILEGROUP_ID('filegroup_name')`** (`smallint`) / **`FILEGROUP_NAME(filegroup_id)`** (`sysname`): read `Database.Filegroups` (PRIMARY = `data_space_id` 1; user filegroups 2, 3, … in registration order — same registry `sys.filegroups` / `sys.data_spaces` enumerate).
- **`FILEGROUPPROPERTY('filegroup_name', 'property')`** (`int`): `IsDefault` (1 for PRIMARY, 0 for user — the simulator has no `MODIFY FILEGROUP … DEFAULT`, so PRIMARY is always the default), `IsUserDefinedFG` (0 for PRIMARY, 1 for a registered user filegroup), `IsReadOnly` (always 0 — no read-only filegroups modeled).
- **`FILEPROPERTY('file_name', 'property')`** (`int`): `IsPrimaryFile` / `IsLogFile` / `IsReadOnly` / `SpaceUsed` — full deep-dive in [`scalars.md`](scalars.md); see the `sys.database_files` self-consistency contract above.

Shared conventions across the family (probe-confirmed):
- NULL on any NULL argument, an unknown / unregistered name or id (including `FILE_NAME` / `FILEGROUP_NAME` of 0, a negative, or an out-of-range id), and an unknown property.
- Name lookups are case-insensitive and trailing-space insensitive (SQL Server's internal `=` comparison — the `FILE_ID` / `FILEGROUP_ID` / `FILEGROUPPROPERTY` argument is `TrimEnd(' ')`'d before matching, since the modeled names carry no trailing spaces).
- Property names are case-insensitive and trailing-space insensitive.
- All operate on the **current** database only.

**Placeholder file model** (the file-level divergence): there is no physical file model.
Each database exposes exactly two synthetic files, mirroring `sys.database_files` / `sys.master_files`: `<db>_Data` (`file_id` 1, primary ROWS, on PRIMARY) and `<db>_Log` (`file_id` 2, LOG).
So `FILE_ID(N'simulated_Data')` = 1, `FILE_NAME(1)` = `simulated_Data`, and the file-level scalars stay consistent with those views; real SQL Server's actual logical file names (e.g. master's `master` / `mastlog`) are database-install artifacts the simulator doesn't reproduce.

Cross-cutting notes:
- **Column subset (sys.* only)**: real SQL Server's `sys.tables` / `sys.objects` / `sys.columns` have 30+ columns each; the simulator ships the load-bearing subset that EF / migration tooling and the probe queried.
  `SELECT *` returns fewer columns than real SQL Server — apps that depend on a specific full-column shape will surface gaps, address those as needed.
  INFORMATION_SCHEMA views ship the full ISO column set.
- **Temp tables not in `sys.tables` / `INFORMATION_SCHEMA.TABLES`**: the per-connection `TempTables` dict isn't walked by the row generators (real SQL Server lists temp tables in `tempdb.sys.tables`, which the simulator's single-database model doesn't separate).
  Catalog views show user tables in `dbo` + any user schema only.
- **No write paths**: `INSERT sys.tables …` / `UPDATE sys.tables …` / `DROP TABLE INFORMATION_SCHEMA.COLUMNS` etc. all raise Msg 208 — catalog views aren't in `Schema.HeapTables`, so the regular table-lookup miss path fires.
- **Constraint object_ids**: `KeyConstraint.ObjectId` and `CheckConstraint.ObjectId` are allocated at CREATE TABLE alongside the table's own id (via `Database.AllocateObjectId()`).
  The order is: schema resolution → allocate constraint ids (inside `ResolveKeyConstraints` / `ResolveCheckConstraints`) → allocate table id → construct `HeapTable`.
  The constraint resolvers take a `Database` parameter to thread the allocation.
- **Per-type metadata covers every column type the simulator stores.**
  The row generator materializes every column in the database *before* any WHERE filter applies, so a type it can't describe fails the whole view rather than one row — an `xml` column anywhere once made a query filtered to an unrelated table raise `NotSupportedException`.
  The CLR-backed and variant types report a length pair and nothing else (probe-confirmed): `xml` / `geography` / `geometry` carry the MAX sentinel `-1` for `CHARACTER_MAXIMUM_LENGTH` and `CHARACTER_OCTET_LENGTH`, `hierarchyid` its 892-byte bound, `sql_variant` a literal 0.
- **`sys.index_columns` computed-column mapping**: `Index` key/INCLUDE entries carry full column ordinals rather than storage ordinals, because every non-persisted computed column shares storage ordinal -1 — a storage-ordinal reverse mapping collapses such references onto the first computed column, which mis-scripts WWI's `IX_Sales_Invoices_ConfirmedDeliveryTime` as `INCLUDE([ConfirmedDeliveryTime])` instead of `INCLUDE([ConfirmedReceivedBy])` (cosmetic in SMO Script-As; fatal Msg 1909 when real SQL Server imports a simulator-exported bacpac).
  `IndexKeyColumn.ColumnOrdinal` + `Index.IncludedColumnOrdinals` carry the full column ordinals for the catalog (`sys.index_columns`, `sys.stats_columns`); storage ordinals remain the runtime (seek/enforcement) representation.
  ALTER TABLE's remap is guarded against the -1 storage ordinal for the same reason.
  Regression: `IndexIntrospectionTests.IndexColumns_ComputedKeyAndInclude_ReportDistinctColumnIds`.
- **`precision` is a reserved keyword in the simulator's parser**: `select precision from sys.columns` raises Msg 102; bracket it (`[precision]`) or alias it.
  Real SQL Server accepts the bare name.
  Minor fidelity gap — fix would loosen `Keyword.Precision` to a contextual-keyword classification.

- **`sys.dm_hadr_cluster`** (single-row failover-clustering DMV): probe-confirmed against a non-clustered SQL Server 2025 — even with no cluster the view returns **one row**: `cluster_name = N''`, `quorum_type 0` / `NODE_MAJORITY`, `quorum_state 1` / `NORMAL_QUORUM`.
  SSMS's Select-Top-1000 server-properties batch reads it inside a TRY/CATCH that tolerates only permission errors (297/300/15562/371), so an empty view or Msg 208 escapes to the user as a THROW.

### `IsDeterministic`

`IsDeterministic` answers for the four module kinds real answers it for — views, scalar functions, inline TVFs and multi-statement TVFs — and NULL for everything else (procedure, trigger, table, sequence, synonym; all probe-confirmed).
The rule, computed by `Schemas/ModuleDeterminism.cs`, is a conjunction of three probe-confirmed conditions:

1. the module was declared `WITH SCHEMABINDING` — **schema-binding is a precondition, not a contributing signal**: a body with nothing nondeterministic in it still reports 0 without the option;
2. the body reaches no nondeterministic built-in;
3. every module the body references — user function, view or TVF — is itself deterministic, transitively.

Reading a table is deterministic (a schema-bound function doing `SELECT COUNT(*) FROM dbo.t` reports 1), as are aggregates, window functions, and `TOP` with or without `ORDER BY`.
A referenced module that is *not* schema-bound reports 0 and propagates that outward — a defensive branch rather than a reachable one, since creating that pair is Msg 4513 (see [`programmable.md`](programmable.md#schema-binding-with-schemabinding)).

The body walk re-tokenizes the module's stored source and scans the token stream rather than visiting an expression tree: scalar-function and multi-statement-TVF bodies are stored as text and re-parsed per call, so no tree exists at CREATE time, and the token scan reaches all four module kinds through one mechanism.
The scan runs per `OBJECTPROPERTY` read rather than being cached at CREATE, so redefining a referenced module moves the caller's answer instead of leaving a stale one.
Reaching that state takes a drop and recreate: altering the referenced module in place is Msg 3729 while the caller stands.
Only *qualified* names are collected for the transitive walk: SQL Server requires a schema qualifier on every user-function call, and a view or TVF is only reachable through one, so an unqualified name followed by `(` is necessarily a built-in.

The nondeterministic built-in table lives in `ModuleDeterminism.NondeterministicBuiltIns`, restricted to names the simulator's own `ResolveBuiltIn` catalog recognizes.
The families: current-time readers, the side-effecting generators (`NEWID` / `RAND` — real rejects these inside a function body outright with Msg 443, so the classification only matters for the simulator's more permissive parse), session and connection state, server- and database-metadata lookups, the security and principal scalars, the `ERROR_*` family, and the language-dependent formatters (`FORMAT`, `DATENAME`, `ISDATE`, `PARSE` / `TRY_PARSE`).
Every `@@`-constant is nondeterministic (probed across `@@SPID` / `@@ROWCOUNT` / `@@ERROR` / `@@TRANCOUNT` / `@@NESTLEVEL` / `@@VERSION` / `@@SERVERNAME` / `@@DBTS` / `@@IDENTITY` / `@@LANGID` / `@@DATEFIRST`), as are the niladic keyword forms (`CURRENT_TIMESTAMP`, `CURRENT_USER`, `SESSION_USER`, `SYSTEM_USER`, `USER`) and `AT TIME ZONE` (which reads the server's zone table — `SWITCHOFFSET` / `TODATETIMEOFFSET`, which take the offset as an argument, are deterministic).

Two argument-sensitive splits, both probed:

- `DATEPART` is nondeterministic only for the `SET DATEFIRST`-dependent units — `week` / `wk` / `ww` / `weekday` / `dw` / `w`.
  `DATEPART(year, …)` and even `DATEPART(iso_week, …)` are deterministic.
  `DATENAME` needs no such split: it is language-dependent for every unit.
- Probed *deterministic* despite looking otherwise, so deliberately absent from the table: `CHECKSUM` / `BINARY_CHECKSUM` / `HASHBYTES`, `QUOTENAME` (while `PARSENAME` is nondeterministic), `MIN_ACTIVE_ROWVERSION`, `DECOMPRESS` (while `COMPRESS` is nondeterministic — probed, not a typo), `APPROX_COUNT_DISTINCT`, `ISNUMERIC`, `TEXTPTR`, `DATEDIFF` / `DATEADD` / `DATETRUNC` / `DATE_BUCKET` / `EOMONTH`, and every window function.

**The `CAST` / `CONVERT` style rule** (`Schemas/ModuleDeterminism.Conversions.cs`).
A conversion *between a date/time type and a character string* is nondeterministic unless an explicit style from the deterministic set is supplied — in both directions, at every member of both families (`char` / `nchar` / `nvarchar` / `sysname` / `text` / `ntext` / `varchar`, and `date` / `datetime` / `datetime2` / `datetimeoffset` / `smalldatetime` / `time`), and identically for the `TRY_` spellings.
A `CAST` carries no style at all, so a date/time ↔ string `CAST` is always nondeterministic.
The probed style table:

| | styles |
| --- | --- |
| deterministic | 20, 21, 101, 102, 103, 104, 105, 108, 110, 111, 112, 114, 120, 121, 126, 127, 130, 131 |
| nondeterministic | no style at all, 0–19, 22–25, 100, 106, 107, 109, 113 |

Every other conversion is left alone: `CONVERT(varchar(20), <int>)`, `CONVERT(datetime, <int>)`, `CONVERT(int, <string>)` and `CONVERT(datetime2, <datetime>)` are all deterministic.

The named type and the style read straight off the token stream; the *converted expression's* own type is what the scan has to infer, and it does so from the evidence the source extent carries — a bare or alias-qualified column name resolved against the columns of the tables and views the body references, a `@name` against the module's declared parameters and the body's own `DECLARE`s, a character literal, and a nested `CAST` / `CONVERT`'s named type.
A call to a built-in whose result family doesn't follow its arguments contributes that family and hides what it wraps (`YEAR` / `DATEPART` / `DATEDIFF` / `LEN` → neither family, `DATEADD` / the `…FROMPARTS` constructors → date/time, `LEFT` / `CONCAT` / `STR` → string), which is what keeps `CONVERT(varchar(20), YEAR(<date>))` deterministic while `CONVERT(varchar(20), DATEADD(day, 1, <date>))` is not; every other call propagates, so `ISNULL` / `CASE` / an aggregate over a date column still reads as a date.

What stays undecidable, all erring toward *deterministic* — the answer the module had before the rule shipped, so the inference only moves a cell toward real: a column name the body's tables don't carry (a CTE or derived table's own output, an alias-type column), a user function whose return type isn't its argument's, a style written as an expression rather than a literal (`121 + 0`, which real folds and reports deterministic), and an ANSI type synonym (`character varying`).
