# BACPAC loader

`Simulation.ImportBacpac` loads a `.bacpac` end-to-end. The implementation is a **translator**, not a second object-construction pipeline: emit T-SQL `CREATE …` from `model.xml`, feed it through the existing parser, then load BCP data files into the resulting tables.

## Public surface

```csharp
// Add a new Database to a Simulation. Default target name:
// Path.GetFileNameWithoutExtension(path) for the file overload, "simulated"
// for the stream overload. Throws InvalidOperationException if a database
// of that name already exists (DACFx-style create-only).
public void ImportBacpac(string path, out BacpacImportResult result, BacpacImportOptions? options = null);
public void ImportBacpac(Stream stream, out BacpacImportResult result, BacpacImportOptions? options = null);
```

`BacpacImportOptions` carries `string? DatabaseName` (null → derive default per overload) and `int MaxDegreeOfParallelism = -1` (`-1` = `Environment.ProcessorCount`, matching `ParallelOptions`). It's a sealed `record class` so callers can `options with { … }`; passing the whole `options` argument as `null` is equivalent to passing `new BacpacImportOptions()`.

`BacpacImportResult` is public; `BacpacSkipped` is public:
- `IReadOnlyList<BacpacSkipped> Skipped` — per-element load failures + intentional non-fatal skips
- `IReadOnlyList<string> Warnings` — degradation notices (e.g. unrecognized collation falls back to default)
- `IReadOnlyDictionary<string, int> ElementCounts` — model.xml census, useful for cross-checking against the source bacpac
- Internal mutators (`AddSkipped` / `AddWarning` / `IncrementElementCount` / `AddToElementCount`) are loader-only.
- `TableColumnIsAlias` stays internal.

### Default-database & connection routing

`Simulation`'s ctor leaves `Databases` empty. `SimulatedDbConnection`'s ctor resolves `CurrentDatabase` in three tiers: (1) `"simulated"` if present; (2) lazy-create `"simulated"` when `Databases` is empty (so `new Simulation().ExecuteScalar(...)` still just works); (3) the alphabetically-first existing database otherwise. The lazy-create makes `new Simulation().ImportBacpac(stream, out _)` succeed against a fresh simulation — the stream-default `"simulated"` name has nothing to collide with until the first connection materializes the seed. Once a connection has opened (or an `ImportBacpac` has landed a database named `"simulated"`), a subsequent default-name stream import collides and throws — correct create-only behavior. `sys.databases` iterates every hosted database regardless of which one a given connection points at; `USE <db>` isn't wired up yet, so cross-database querying through a single connection isn't possible beyond catalog-view inspection.

## Resilient-loader contract

Per-element exceptions land on `Skipped` with a `"Load failed: …"` prefix and the load continues; the entire load doesn't abort because one constraint / view / proc fails. Computed-column failures use a `"Deferred: …"` prefix instead, so the `Load_AW_No_Per_Element_Failures` guard test stays meaningful — it would otherwise spuriously fire on known unmodeled-function gaps.

## Code layout — `SqlServerSimulator/Storage/Bacpac/`

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
| 1 | DB options + schemas + UDDTs + sequences + roles + table types + filegroups (silent skip) + partition function/scheme/columnstore (silent skip) |
| 2 | Tables (columns only, defaults inline, computed columns deferred to phase 8) |
| 3 | Constraints (PK / UQ / CHECK / DEFAULT) |
| 4 | Foreign keys |
| 5 | Deferred system-versioning links (`ALTER TABLE … SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))`) |
| 6 | Views |
| 7 | Programmable objects (procs, scalar / multi-stmt TVFs, DML + DDL triggers, GRANT statements) |
| 8 | Deferred computed columns + indexes (order matters: computed cols before filtered indexes that reference them) |
| 9 | Extended properties |

After all 9 phases: BCP data load (parallel per-table with LPT scheduling — see `BacpacReader.cs`).

## BCP wire format

Per-table data lives in `Data/<schema>.<table>/TableData-NNN-NNNNN.BCP`. Type matrix probe-confirmed against AdventureWorks2025 hex-dumps:

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
- `bit` is 1-byte-length-prefixed regardless of nullability — matches the UDDT-aliased bit shape. Probe against AW's plain-bit `Production.Document.FolderFlag` (the only non-UDDT bit column in AW) confirmed.
- MAX types + xml + CLR-UDT all share the **simple inline** 8-byte-prefix shape, NOT the chunked PLP form used in TDS network traffic. Probe-confirmed via `ProductPhoto.ThumbNailPhoto` (1077 bytes inline, no chunk markers) and `HumanResources.JobCandidate.Resume` xml (9086 bytes likewise inline).

**Wire-format gaps** (none seen in AW or WWI):
- `text` / `ntext` / `image` legacy LOB family — likely same 8-byte-prefix shape as MAX, but not confirmed.
- `sql_variant` envelope — probably type-tag byte + per-type encoding.
- TDS-PLP chunked form (8-byte length = `0xFFFFFFFFFFFFFFFE` "unknown total", 4-byte-length chunks terminating on 4-byte zero) — not seen in any bacpac shard; reserved for live TDS traffic only.

## BCP-filters-computed-columns contract

`BacpacReader.LoadRowsFromBcp` strips columns with `HeapColumn.Computed != null` before passing to `BcpRowReader` / `RowEncoder` — DACFx-emitted BCP files exclude computed columns from the wire layout regardless of PERSISTED. The loader emits computed columns *without* the PERSISTED qualifier so the simulator recomputes on every read; recomputing on every read gives identical query semantics with the only cost being a per-read evaluation. A persisted-computed column would have no stored bytes for existing rows, since BCP doesn't carry data for them.

## Reference sample coverage

Reference bacpacs live in `.vs/<probe>/` as gitignored cross-check probes (`aw-crosscheck`, `wwi-crosscheck`). The retired `*.Tests.Internal/Storage/BacpacLoaderTests.cs` suite has been replaced by synthetic in-memory bacpacs built via `SqlServerSimulator.Bacpac.BacpacBuilder` + `TableBuilder` in `SqlServerSimulator.Tests/Bacpac/`. CI runs against synthetic builders in seconds.

**AdventureWorks2025** — 100% row coverage (760,167 / 760,167 rows, zero BCP-file failures). 5/5 schemas, 71/71 tables, 90/90 FKs, 89/89 CHECKs, 152/152 DEFAULTs, 89/95 indexes, 11/20 views, 10/10 procs, 11/11 functions, 10/10 DML triggers, 1/1 DDL trigger, 527/538 extended properties.

**WideWorldImporters-Standard** — end-to-end clean. 48/48 tables, 26/26 sequences, 9/9 roles, 4/4 table types, 8/8 computed columns, 7/7 CHECK constraints, 42/42 procedures, 94/94 indexes, 3/3 views, 1/1 scalar function, 2/2 encryption-key GRANTs, 414/414 extended properties, 4.7M rows. **Zero remaining Skipped categories.**

**WideWorldImporters-Full** — same row volume + schema as Standard, adds partitioning + columnstore + one natively-compiled procedure + 17 system-versioned base→history pairings. Loader survives with `SqlPartitionFunction` / `SqlPartitionScheme` / `SqlColumnStoreIndex` as silent skips (storage-organization decorations with no semantic effect on row-store queries — same pattern as `SqlFilegroup`). After the NATIVE_COMPILATION + BEGIN ATOMIC parser support, **WWI-Full reaches zero remaining Skipped categories** — every element type loads cleanly.

**Cross-check probes** capture each reference DB's per-column COUNT / MIN / MAX / SUM / SUM(DATALENGTH) against the live SQL Server 2025 reference instance. WideWorldImporters-Standard cross-checks 100% clean (51/51 tables, every per-column metric byte-equal, zero divergences). AdventureWorks2025 cross-check is the active fidelity oracle; current state is 55/91 tables clean, 32 → 10 errored, 32 → 9 of 10 scalar UDFs clean.

## Deferred work

- **Hierarchyid byte-identical CAST encoding** — the BCP wire decoder ships (covers AW's `[0..79]` positive-ordinal envelope; throws `NotSupportedException` on negative ordinals, ordinals ≥ 80, and dotted sub-ordinals so a follow-up bundle can extend cleanly). The simulator's own `HierarchyIdSqlType.Encode` / `Decode` still uses its segment-array-LE internal form. Replacing both with the documented OrdPath encoding makes cross-engine CAST byte equality hold round-trip. See [`hierarchyid.md`](hierarchyid.md) for the cracked tiers + remaining research notes.
- **Real XPath/XQuery evaluation** — see [`xml.md`](xml.md).
- **Real OGC method evaluation** — see [`spatial.md`](spatial.md).
- **Full-text search** — see [`full-text.md`](full-text.md).
- **Per-collation comparison/sort/LIKE** — see [`database-options.md`](database-options.md). The metadata round-trips; the comparison semantics still route through default.
