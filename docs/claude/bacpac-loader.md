# BACPAC loader

`Simulation.ImportBacpac` loads a `.bacpac` end-to-end.
The implementation is a **translator**, not a second object-construction pipeline: emit T-SQL `CREATE …` from `model.xml`, feed it through the existing parser, then load BCP data files into the resulting tables.

## Public surface

```csharp
// Add a new Database to a Simulation. Default target name:
// Path.GetFileNameWithoutExtension(path) for the file overload, "simulated"
// for the stream overload. Throws InvalidOperationException if a database
// of that name already exists (DACFx-style create-only).
public void ImportBacpac(string path, out BacpacImportResult result, BacpacImportOptions? options = null);
public void ImportBacpac(Stream stream, out BacpacImportResult result, BacpacImportOptions? options = null);
```

`BacpacImportOptions` carries `string? DatabaseName` (null → derive default per overload) and `int MaxDegreeOfParallelism = -1` (`-1` = `Environment.ProcessorCount`, matching `ParallelOptions`).
It's a sealed `record class` so callers can `options with { … }`; passing the whole `options` argument as `null` is equivalent to passing `new BacpacImportOptions()`.

`BacpacImportResult` is public; `BacpacSkipped` is public:
- `IReadOnlyList<BacpacSkipped> Skipped` — per-element load failures + intentional non-fatal skips
- `IReadOnlyList<string> Warnings` — degradation notices (e.g. unrecognized collation falls back to default)
- `IReadOnlyDictionary<string, int> ElementCounts` — model.xml census, useful for cross-checking against the source bacpac
- Internal mutators (`AddSkipped` / `AddWarning` / `IncrementElementCount` / `AddToElementCount`) are loader-only.
- `TableColumnIsAlias` stays internal.

### Default-database & connection routing

`Simulation`'s ctor leaves `Databases` empty.
`SimulatedDbConnection`'s ctor resolves `CurrentDatabase` in three tiers: (1) `"simulated"` if present; (2) lazy-create `"simulated"` when `Databases` is empty (so `new Simulation().ExecuteScalar(...)` still just works); (3) the alphabetically-first existing database otherwise.
The lazy-create makes `new Simulation().ImportBacpac(stream, out _)` succeed against a fresh simulation — the stream-default `"simulated"` name has nothing to collide with until the first connection materializes the seed.
Once a connection has opened (or an `ImportBacpac` has landed a database named `"simulated"`), a subsequent default-name stream import collides and throws — correct create-only behavior.
`sys.databases` iterates every hosted database regardless of which one a given connection points at.
`USE <db>` switches the session's current database (Msg 911 on miss); 3-part names route reads (SELECT / JOIN / catalog views) across databases.
Cross-DB writes raise `NotSupportedException` — issue `USE` first.
See [`docs/claude/schemas.md`](schemas.md) for the full multi-database resolution rules.

## Resilient-loader contract

Per-element exceptions land on `Skipped` with a `"Load failed: …"` prefix and the load continues; the entire load doesn't abort because one constraint / view / proc fails.
Deferred-computed-column failures (phase 8, for the rare UDF-forward-ref table) use a `"Deferred: …"` prefix instead, so the `Load_AW_No_Per_Element_Failures` guard test stays meaningful — it would otherwise spuriously fire on known unmodeled-function gaps.

## Code layout — `src/SqlServerSimulator/Storage/Bacpac/`

- **`BacpacReader.cs`** — OPC zip walker, dispatches to model + data readers
- **`ModelXmlReader.cs`** — `model.xml` → DDL emitter (the 9-phase dispatcher)
- **`BcpRowReader.cs`** — `*.BCP` → row decoder (the wire-format matrix below)
- **`BacpacImportResult.cs`** — diagnostics carrier
- **`BacpacImportOptions.cs`** — target-database-name + parallelism options
- **`HierarchyIdWireDecoder.cs`** — see [`hierarchyid.md`](hierarchyid.md)
- **`SpatialWkbDecoder.cs`** — see [`spatial.md`](spatial.md)

## Model.xml — 9-phase dispatcher

| Phase | Elements |
|---|---|
| 1 | DB options + schemas + UDDTs + sequences + roles + table types + XML schema collections + **full-text catalogs** + **filegroups** (registered on `Database.Filegroups` so `sys.filegroups` / `sys.data_spaces` surface them — no physical file model) + partition function/scheme/columnstore (silent skip) |
| 2 | Tables (columns + computed columns inline at model ordinal, defaults inline; a computed expression that forward-references a not-yet-created UDF makes the CREATE TABLE throw, so that one table is re-created with computed columns stripped and they defer to phase 8) |
| 3 | Constraints (PK / UQ / CHECK / DEFAULT — DACFx already parenthesizes `DefaultExpressionScript` (`(NEXT VALUE FOR …)`), so `EmitDefaultConstraint` wraps only an unparenthesized script; wrapping an already-`(…)` script would double the parens the `ALTER … DEFAULT (…)` parser re-derives, diverging from real's single-pair `sys.default_constraints.definition`) |
| 4 | Foreign keys |
| 5 | Deferred system-versioning links (`ALTER TABLE … SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))`) |
| 6 | Views |
| 7 | Programmable objects (procs, scalar / multi-stmt TVFs, DML + DDL triggers, GRANT statements) |
| 8 | Deferred computed columns (only for tables phase 2 fell back on — a forward UDF reference; these append at the end, so their `sys.columns.column_id` lands after the simple columns rather than at the model ordinal) + indexes (order matters: computed cols before filtered indexes that reference them) + **XML indexes** (`CREATE [PRIMARY] XML INDEX`; primaries precede secondaries in DACFx's name-sorted document order, so a secondary's `USING XML INDEX` reference resolves) + **full-text indexes** (`CREATE FULLTEXT INDEX … KEY INDEX … ON catalog`; needs the table's clustered PK / unique KEY INDEX from phase 3 + the catalog from phase 1) |
| 9 | Extended properties (incl. the database-DDL-trigger + filegroup host kinds — `@level0type=N'TRIGGER'` / `N'FILEGROUP'`) |

After all 9 phases: BCP data load (parallel per-table with LPT scheduling — see `BacpacReader.cs`).

## BCP wire format

Per-table data lives in `Data/<schema>.<table>/TableData-NNN-NNNNN.BCP`.
Type matrix probe-confirmed against AdventureWorks2025 hex-dumps:

| Type family | Wire layout | NULL sentinel |
|---|---|---|
| Fixed-width numeric (`int`, `bigint`, `smallint`, `tinyint`), temporal (`datetime`, `smalldatetime`, `date`) NOT NULL | raw bytes LE, no prefix | n/a |
| Same types NULLABLE | 1-byte length prefix (= type width) + raw bytes | `0xFF` |
| `bit` (NOT NULL or NULL, with-or-without UDDT alias) | 1-byte length prefix (= 1) + 1 raw byte | `0xFF` |
| Length-prefixed fixed (`uniqueidentifier`, `decimal`/`numeric`, `money`, `smallmoney`, `datetime2(N)`, `time(N)`, `datetimeoffset(N)`) — always 1-byte-prefix even NOT NULL | 1-byte length prefix (= width) + raw bytes | `0xFF` |
| UDDT-aliased columns of any base type (e.g. `dbo.Flag` over bit, `dbo.OrderNumber` over nvarchar) | shaped as if nullable (1-byte / 2-byte / 8-byte prefix per base family) regardless of declared nullability | per base-family NULL sentinel |
| Variable-length bounded (`nvarchar(N)`, `varchar(N)`, `nchar(N)`, `char(N)`, `varbinary(N)`, `binary(N)`) | 2-byte LE byte-length prefix + bytes | `0xFFFF` |
| MAX types (`varchar(MAX)`, `nvarchar(MAX)`, `varbinary(MAX)`), `xml`, CLR-UDT family (`hierarchyid`, `geography`, `geometry`) | 8-byte LE length prefix + N bytes inline (NOT the TDS-PLP chunked encoding) | `0xFFFFFFFFFFFFFFFF` (-1 signed) |

**Probe-confirmed corrections from the original first-cut matrix**:
- `money` / `smallmoney` / `time(N)` / `datetime2(N)` / `datetimeoffset(N)` are fixed-raw with no prefix when NOT NULL (only nullable variants prefix).
- `bit` is 1-byte-length-prefixed regardless of nullability — matches the UDDT-aliased bit shape.
  Probe against AW's plain-bit `Production.Document.FolderFlag` (the only non-UDDT bit column in AW) confirmed.
- MAX types + xml + CLR-UDT all share the **simple inline** 8-byte-prefix shape, NOT the chunked PLP form used in TDS network traffic.
  Probe-confirmed via `ProductPhoto.ThumbNailPhoto` (1077 bytes inline, no chunk markers) and `HumanResources.JobCandidate.Resume` xml (9086 bytes likewise inline).

**Wire-format gaps** (none seen in AW or WWI):
- `text` / `ntext` / `image` legacy LOB family — likely same 8-byte-prefix shape as MAX, but not confirmed.
- `sql_variant` envelope — probably type-tag byte + per-type encoding.
- TDS-PLP chunked form (8-byte length = `0xFFFFFFFFFFFFFFFE` "unknown total", 4-byte-length chunks terminating on 4-byte zero) — not seen in any bacpac shard; reserved for live TDS traffic only.

## BCP-filters-computed-columns contract

DACFx-emitted BCP files exclude computed columns from the wire layout regardless of PERSISTED — neither real `bcp.exe` nor DACFx export them.
`BacpacReader.LoadRowsFromBcp` reads only the non-computed (wire) columns.

**PERSISTED survives to the catalog.**
`ModelXmlReader` reads the `SqlComputedColumn` element's `IsPersisted` property (and `IsPersistedNullable`) and appends a `PERSISTED` / `PERSISTED NOT NULL` marker to the `AS (expr)` fragment (`ComputedPersistedSuffix`) — without it, `sys.computed_columns.is_persisted` reads 0 and DacFx's re-export drops `IsPersisted=True` (WWI's `Application.People.SearchName` and the two `*Transactions.IsFinalized` columns lost the flag).
The explicit `NOT NULL` is required because the simulator's parser defaults a bare `PERSISTED` computed column to nullable (it doesn't infer nullability from the expression), so `IsPersistedNullable=False` must map to `PERSISTED NOT NULL` for `sys.computed_columns.is_nullable` to match the source.

**ROWGUIDCOL and identity NOT FOR REPLICATION survive to the catalog.**
`TranslateSimpleColumn` reads the `SqlSimpleColumn`'s `IsRowGuidColumn` property and appends a `ROWGUIDCOL` keyword after the type (round-trips through `sys.columns.is_rowguidcol`), and reads `IdentityIsNotForReplication` to append `NOT FOR REPLICATION` to the `IDENTITY(seed, increment)` clause (round-trips through `sys.identity_columns.is_not_for_replication`).
Both are metadata-only — replication and the `$ROWGUID` pseudo-column aren't modeled — but DacFx reads the catalog columns on export, so without emitting the clauses the re-exported model dropped `IsRowGuidColumn=True` (29 AW columns) and `IdentityIsNotForReplication=True` (4 AW columns).
The extended-property `value` similarly round-trips as `sql_variant` (see [`extended-properties.md`](extended-properties.md)) so DacFx re-scripts the N-prefix — together these closed the AW property-diff tail.

**Persisted computed columns are computed at load.**
A `PERSISTED` marker makes the column `IsStored` — it has a physical storage slot but no BCP wire bytes.
`LoadRowsFromBcp` detects any `IsStored` computed column and switches from the fast wire-encode path to a compute path: read the wire values into their full-table ordinals, evaluate every computed column against its siblings via the shared `Simulation.EvaluateComputedColumns` (using a throwaway `BatchContext` bound to the target database), then `ProjectStoredValues` down to the stored layout and encode.
This mirrors what real SQL Server does on bacpac import (recompute-and-store, since BCP carries no bytes).
Tables *without* a persisted computed column keep the untouched fast path — non-persisted computed columns aren't stored, so their wire layout already equals the stored layout.

## Computed-column ordinal preservation

`EmitTable` emits computed columns **inline in CREATE TABLE at their model ordinal** (`col AS (expr)`), so `sys.columns.column_id` matches the source database.
This matters for system-versioned pairs: DacFx re-export orders `model.xml` columns by `column_id`, and re-import into real SQL Server fails with **Msg 13524** if the base table and its history sibling disagree on ordinals.
WWI's `Application.People` (computed `SearchName` at 4, `OtherLanguages` at 18) and its `People_Archive` sibling (all simple columns, true order) now share identical ordinals; all 17 WWI temporal pairs align.

A computed expression that forward-references a user function can't resolve in the CREATE TABLE column-list parser (the UDF only lands in phase 7).
`EmitTable` runs a **two-attempt** strategy per table: build the full DDL with computed columns inline and try it; on any failure *when the table has computed columns*, re-create with computed columns stripped and register the table in a `deferredComputedTables` set that phase 8 consumes (it processes only those tables).
The stripped-then-appended path leaves those computed columns at the **end** of `sys.columns` for that one table — the accepted tradeoff.
AW's `Sales.Customer.AccountNumber` (`isnull('AW'+[dbo].[ufnLeadingZeros]([CustomerID]),'')`) is the only such column across AW + WWI; it lands at ordinal 7 rather than its true 5.
No temporal table in either reference has a UDF-referencing computed column, so no history pair is affected.

The alias side-map (`TableColumnIsAlias`, consumed by the BCP decoder) is built index-aligned to the resulting `HeapTable.Columns` order: full model order (computed slots `false`, never read since BCP filters computed columns out) on the inline path, simple-columns-only on the fallback path (computed appended last).

## XML schema collections + typed-xml columns

`SqlXmlSchemaCollection` elements dispatch in phase 1 (`EmitXmlSchemaCollection`), alongside the other type-namespace objects (UDDTs / table types) so they exist before any table that binds one.
The element carries a single `SchemaExpression` property whose CDATA body is a **complete T-SQL string literal** — DACFx stores the `N'…'` prefix, the surrounding quotes, and any doubled embedded quotes verbatim — so the loader forwards it into `CREATE XML SCHEMA COLLECTION [schema].[name] AS <literal>` without re-wrapping.

Typed-xml columns arrive as `SqlXmlTypeSpecifier` (vs `SqlTypeSpecifier` for every other type) carrying an `XmlSchemaCollection` relationship whose `References` names the bound `[schema].[collection]`.
`TranslateTypeSpecifier` detects the relationship and emits `xml([schema].[collection])` instead of bare `xml`, so `HeapColumn.XmlSchemaCollection` binds and `sys.columns.xml_collection_id` reports the collection's id (0 when untyped).
Only the default CONTENT facet is handled — AW carries no `XmlStyle`/DOCUMENT property (probe: zero occurrences across the model), so a DOCUMENT facet would need a separate property read.

This closes the round-trip that made AW's re-exported bacpac lose typed xml: `Person.vAdditionalContactInfo`'s `.value()` XQuery needs typed xml for singleton inference, and untyped columns raise Msg 2389 at real-server re-import.
On the export side, `sys.columns.xml_collection_id` populates from the binding so DacFx re-serializes the `SqlXmlTypeSpecifier` shape.

## XML indexes, full-text, filegroups, DDL-trigger + filegroup extended properties

`ModelXmlReader` dispatches these element families (AW's 16-element export gap → 0).
A `SqlIndex` whose `IndexedObject` resolves to a view is an **indexed view**: the ordinary `EmitIndex` path produces `CREATE UNIQUE CLUSTERED INDEX … ON <view>` (views land in phase 6, before this phase-8 index emission, and the schema-bound view already exists), which `CREATE INDEX` routes to the view path — see [`indexes.md`](indexes.md).
The remaining four families:

- **`SqlXmlIndex`** (phase 8) → `CREATE [PRIMARY] XML INDEX`.
  Primary form carries `IsPrimary=True` + the indexed `Column` + `IndexedObject`; secondary form carries `PrimaryXmlIndexUsage` (1=PATH / 2=PROPERTY / 3=VALUE) + a `UsingPrimaryXmlIndex` reference.
  The loader-wiring was cheap; the **export** side needed new catalog surface — DacFx's XML-index reverse-engineering query INNER JOINs `sys.index_columns` (one row per XML index) *and* an internal "node table" (`sys.objects` type `IT` / `INTERNAL_TABLE`, one per primary index, parent = base table) joined to `sys.stats` (one row per XML index, named after the index, on the node table's object_id).
  A primary XML index allocates the node-table object id at CREATE; see [`xml.md`](xml.md).
- **`SqlFullTextCatalog`** (phase 1) → `CREATE FULLTEXT CATALOG … WITH ACCENT_SENSITIVITY = {ON|OFF} [AS DEFAULT] AUTHORIZATION owner`.
- **`SqlFullTextIndex`** (phase 8) → `CREATE FULLTEXT INDEX ON t (col [TYPE COLUMN c] LANGUAGE n, …) KEY INDEX key ON catalog`.
  Export needed `sys.fulltext_languages` populated (DacFx INNER JOINs it by `language_id` to name the column's language — an empty view NREs the column-specifier populator) and `sys.fulltext_indexes.data_space_id` = 1 (PRIMARY) + `stoplist_id` = 0 (system stoplist), both previously NULL — DacFx INNER JOINs `sys.data_spaces` on the former (NULL drops the parent index element, orphaning its column specifiers → NRE) and reads the latter to decide `DoUseSystemStopList` vs `IsStopListOff`.
  See [`full-text.md`](full-text.md).
- **`SqlFilegroup`** (phase 1) → registers the (non-PRIMARY) filegroup on `Database.Filegroups` so `sys.filegroups` / `sys.data_spaces` surface it and DacFx re-emits the standalone element.
  No physical file / placement model — every heap lives on PRIMARY, so no table/index `Filegroup` relationships are emitted (the model-diff ignores relationships anyway).
  WWI's `[USERDATA]` closes its 1-element gap.
  See [`database-options.md`](database-options.md).
- **`SqlExtendedProperty`** on a **database DDL trigger** (`@level0type=N'TRIGGER'` → class 1, major_id = trigger object_id) or a **filegroup** (`@level0type=N'FILEGROUP'` → class 20 DATASPACE, major_id = data_space_id).
  See [`extended-properties.md`](extended-properties.md).

Both AW + WWI exports re-import cleanly into a real SQL Server 2025 (CU7, full-text installed) with all these elements present; the USERDATA filegroup imports with a DacFx-created default file.

## Reference sample coverage

Reference bacpacs live in `.vs/<probe>/` as gitignored cross-check probes (`aw-crosscheck`, `wwi-crosscheck`).
The retired `*.Tests.Internal/Storage/BacpacLoaderTests.cs` suite has been replaced by synthetic in-memory bacpacs built via `SqlServerSimulator.Bacpac.BacpacBuilder` + `TableBuilder` in `tests/SqlServerSimulator.Tests/Bacpac/`.
CI runs against synthetic builders in seconds.

**AdventureWorks2025** — 100% row coverage (760,167 / 760,167 rows, zero BCP-file failures).
5/5 schemas, 71/71 tables, 90/90 FKs, 89/89 CHECKs, 152/152 DEFAULTs, 89/95 indexes, 11/20 views, 10/10 procs, 11/11 functions, 10/10 DML triggers, 1/1 DDL trigger, 538/538 extended properties, 8/8 XML indexes, 1/1 full-text catalog, 3/3 full-text indexes, 2/2 indexed-view `SqlIndex` elements.
**Import skips are 0.**
The DacFx-export element gap vs the Microsoft original is 0 missing; property diffs are 0.
One pre-existing divergence surfaces as an "extra" element: AW's system-named UNIQUE constraint on `Production.Document.rowguid` is scripted anonymously (name in an annotation) by DacFx normally, but *named* once the table has a full-text index — and the simulator's auto-generated constraint name (FNV-based) differs from real's object-id-derived one (a documented quirk), so the named form doesn't byte-match.
Doesn't block real import (verified: aw-export re-imports into a live SQL Server 2025, both view indexes present at index_id 1 / CLUSTERED / unique).

**WideWorldImporters-Standard** — end-to-end clean.
48/48 tables, 26/26 sequences, 9/9 roles, 4/4 table types, 8/8 computed columns, 7/7 CHECK constraints, 42/42 procedures, 94/94 indexes, 3/3 views, 1/1 scalar function, 2/2 encryption-key GRANTs, 414/414 extended properties, 4.7M rows.
**Zero remaining Skipped categories.**

**WideWorldImporters-Full** — same row volume + schema as Standard, adds partitioning + columnstore + one natively-compiled procedure + 17 system-versioned base→history pairings.
Loader survives with `SqlPartitionFunction` / `SqlPartitionScheme` / `SqlColumnStoreIndex` as silent skips (storage-organization decorations with no semantic effect on row-store queries — same pattern as `SqlFilegroup`).
After the NATIVE_COMPILATION + BEGIN ATOMIC parser support, **WWI-Full reaches zero remaining Skipped categories** — every element type loads cleanly.

**Cross-check probes** capture each reference DB's per-column COUNT / MIN / MAX / SUM / SUM(DATALENGTH) against the live SQL Server 2025 reference instance.
WideWorldImporters-Standard cross-checks 100% clean (51/51 tables, every per-column metric byte-equal, zero divergences).
AdventureWorks2025 cross-check is the active fidelity oracle; current state is 55/91 tables clean, 32 → 10 errored, 32 → 9 of 10 scalar UDFs clean.

## Export verification workflow

DacFx export (sqlpackage over the TDS endpoint) is verified in escalating rigor, each layer catching what the previous can't:

1. **Simulator round-trip** — export, re-import through `Simulation.ImportBacpac`, per-table row-count + value spot checks.
   Blind to losses both sides agree on.
2. **Model-diff pre-flight** — diff the exported bacpac's model.xml against the Microsoft-authored original: element inventory by (Type, Name), per-table column order, property bags (harness: the session-local reimport tool's `modeldiff` mode).
   The cheap standing detector for export fidelity gaps — most real-import rejections (duplicate index column Msg 1909, temporal ordinal Msg 13524, Query Store capture mode) were visible here before any import ran.
3. **Real-server import** — `sqlpackage /Action:Import` into an actual SQL Server (the in-container instance gives full sysadmin), then live parity queries against the original imported beside it.
   The strictest oracle: real DacFx validates harder than the simulator's own loader (XQuery singleton inference over typed xml, SYSTEM_VERSIONING ordinal identity, model-schema value ranges).
   Note the original AW bacpac needs full-text installed (`mssql-server-fts` on Linux) or its catalog aborts the import mid-script.

Both AW and WWI exports pass all three layers (row/FK/collection/typed-column parity, 0 missing elements for both).

## Deferred work

- **Real XPath/XQuery evaluation** — see [`xml.md`](xml.md).
- **Real OGC method evaluation** — see [`spatial.md`](spatial.md).
- **Full-text search** — see [`full-text.md`](full-text.md).
- **Per-collation comparison/sort/LIKE** — see [`database-options.md`](database-options.md).
  The metadata round-trips; the comparison semantics still route through default.
