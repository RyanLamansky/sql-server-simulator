# BACPAC import — prerequisite feature checklist

Working document for the eventual `Simulation.FromBacpac` (or `FromBacPac` — naming TBD) entry point. The plan is **emit T-SQL CREATE statements from `model.xml`, feed them through the existing parser**, then load BCP data files in `DataPhaseTables` order. The loader is a translator, not a second object-construction pipeline; the more T-SQL the parser already accepts, the smaller the loader.

Reference sample: `.vs/AdventureWorks2025.bacpac` (Microsoft AdventureWorks2025, 71 tables, 760,167 total rows, 17 MB compressed). Element counts and AW-usage tallies below are probe-confirmed from that file's `model.xml` + `Origin.xml` on 2026-05-14.

## Model.xml — the simulator already handles

These Element types map 1:1 to features the parser already eats; the loader synthesizes the appropriate `CREATE …` text and the existing code paths do the work. No new simulator features needed.

| Element | AW count | Maps to |
|---|---|---|
| `SqlSchema` | 5 | `CREATE SCHEMA` |
| `SqlTable` / `SqlSimpleColumn` / `SqlComputedColumn` / `SqlTypeSpecifier` | 71 / 481 / 302 | `CREATE TABLE` with columns + computed |
| `SqlPrimaryKeyConstraint` / `SqlUniqueConstraint` | 71 / 1 | inline + table-level PK/UQ |
| `SqlForeignKeyConstraint` (incl. `OnDeleteAction`=CASCADE) | 90 (2 cascade) | `CONSTRAINT … FOREIGN KEY … REFERENCES … ON DELETE CASCADE` |
| `SqlCheckConstraint` (raw T-SQL in `CheckExpressionScript`) | 89 | `CONSTRAINT … CHECK (…)` |
| `SqlDefaultConstraint` (raw T-SQL in `DefaultExpressionScript`) | 152 | `CONSTRAINT … DEFAULT (…)` |
| `SqlIndex` / `SqlIndexedColumnSpecification` | 95 | `CREATE [UNIQUE] [CLUSTERED] INDEX` |
| `SqlView` (raw SELECT in `QueryScript`) | 20 | `CREATE VIEW … [WITH SCHEMABINDING] AS …` (SCHEMABINDING parses + discards) |
| `SqlProcedure` (raw body in `BodyScript`, header in `SysCommentsObjectAnnotation.HeaderContents`) | 10 | `CREATE PROCEDURE` |
| `SqlScalarFunction` / `SqlMultiStatementTableValuedFunction` / `SqlScriptFunctionImplementation` | 10 / 1 / 11 | `CREATE FUNCTION` |
| `SqlDmlTrigger` (`SqlTriggerType` 2=AFTER, 3=INSTEAD OF; `IsInsert/Update/DeleteTrigger`) | 10 | `CREATE TRIGGER` |
| `SqlSubroutineParameter` (`IsOutput`, type via `TypeSpecifier`) | 41 | function/proc parameters |
| `SqlInlineConstraintAnnotation` | 1 | constraint inline-vs-table-level marker |
| `OnlinePropertyAnnotation Name="[LastValue]"` | (per identity column) | identity high-water resume |
| `SysCommentsObjectAnnotation` (`HeaderContents`, `FooterContents`) | 52 | header reconstruction for proc/func/view/trigger |

DDL emission strategy: walk the model in dependency-correct order (schemas → tables → table constraints/indexes → views → functions → procedures → triggers), use each `<Element>`'s properties to assemble the `CREATE` header, concatenate `BodyScript` / `QueryScript` / `CheckExpressionScript` / etc. as-is (they're already valid T-SQL), feed the result through `SimulatedDbCommand.ExecuteNonQuery`. The `HeaderContents` annotation gives a probe-confirmed canonical form to copy when in doubt.

## Model.xml — prerequisite features (blocking AW load)

Sorted approximately by surface-area / effort, smallest first. Each is a candidate for its own bundle.

### [x] Database options parse-and-discard expansion (shipped 2026-05-14)
`SqlDatabaseOptions` carries 18 properties; the `ALTER DATABASE name SET …` parser accept-list now covers every database-scope toggle SqlPackage emits from `model.xml`. Closed-accept-list dict in `Simulation.Alter.cs` (`RecognizedDatabaseOptions`) maps each option to its value shape — `OnOff` (ANSI_NULLS / ANSI_PADDING / ANSI_WARNINGS / ARITHABORT / CONCAT_NULL_YIELDS_NULL / NUMERIC_ROUNDABORT / QUOTED_IDENTIFIER / TORN_PAGE_DETECTION / TEMPORAL_HISTORY_RETENTION), `EnumIdent` (RECOVERY: FULL / BULK_LOGGED / SIMPLE; PAGE_VERIFY: CHECKSUM / TORN_PAGE_DETECTION / NONE; CURSOR_DEFAULT: GLOBAL / LOCAL), `EqualsOnOff` (ACCELERATED_DATABASE_RECOVERY, OPTIMIZED_LOCKING — `=` required per probe), `IntegerWithUnit` (TARGET_RECOVERY_TIME = N SECONDS|MINUTES — unit required per probe), and `QueryStore` (a dedicated sub-grammar — `= ON [( … )] | = OFF | CLEAR [ALL]`). QUERY_STORE sub-options are themselves a closed accept-list (`RecognizedQueryStoreSubOptions`): OPERATION_MODE / CLEANUP_POLICY / DATA_FLUSH_INTERVAL_SECONDS / MAX_STORAGE_SIZE_MB / INTERVAL_LENGTH_MINUTES / SIZE_BASED_CLEANUP_MODE / QUERY_CAPTURE_MODE / MAX_PLANS_PER_QUERY / WAIT_STATS_CAPTURE_MODE / QUERY_CAPTURE_POLICY. The two nested-block sub-options (CLEANUP_POLICY, QUERY_CAPTURE_POLICY) eat balanced parens via `SkipBalancedParens` without enforcing inner-block sub-option names.

**Load-bearing options unchanged**: COMPATIBILITY_LEVEL, ALLOW_SNAPSHOT_ISOLATION, READ_COMMITTED_SNAPSHOT dispatch via their dedicated helpers and keep their behavior wiring. **COLLATE clause** (separate top-level grammar, not under SET): `ALTER DATABASE name COLLATE <name>` hard-errors with `NotSupportedException` on anything other than `SQL_Latin1_General_CP1_CI_AS` — silently accepting would mean the bacpac loader silently mis-loads collation-sensitive data on non-default-collation models. **IsFullTextEnabled** is not handled here (emitted as `EXEC sp_fulltext_database 'enable|disable'`, a system sproc the simulator doesn't model — defer with the rest of full-text below).

Coverage: 53 new tests in `AlterDatabaseOptionsTests.cs` exercise every option × value-shape combination, the QUERY_STORE block (single sub-option / nested / multi / CLEAR forms), the COLLATE hard-error, the load-bearing-options-still-wired regression, and the three syntax-error paths the probe found (`SET RECOVERY = FULL`, `SET ACCELERATED_DATABASE_RECOVERY ON` without `=`, `SET TARGET_RECOVERY_TIME = 60` without unit — all Msg 102, matching probe).

### [x] UDDTs / alias types (`CREATE TYPE … FROM …`) (shipped 2026-05-14)
Real-feature path landed: `CREATE TYPE schema.name FROM <builtin>[(N[, S])] [NULL | NOT NULL]` parses to a new `AliasType` (`SqlServerSimulator/AliasType.cs`) registered on `Schema.AliasTypes` (`ConcurrentDictionary<string, AliasType>`, sharing the type-name namespace with `TableTypes` — duplicate-name collision raises Msg 219 verbatim across both dicts). The 6 AW alias types (`AccountNumber` / `Flag` / `Name` / `NameStyle` / `OrderNumber` / `Phone`) load successfully end-to-end; a smoke test in `AliasTypeTests` declares all 6 + a `Customer` table using `[dbo].[AccountNumber]` / `[dbo].[Name]` / `[dbo].[NameStyle]` / `[dbo].[Phone]` and verifies `is_nullable=0` propagation for the three NOT-NULL aliases.

**Type-reference parsing** at every consumer site (CREATE TABLE column, DECLARE @v, ALTER TABLE ALTER COLUMN, CREATE PROCEDURE / FUNCTION / SEQUENCE parameter, OPENJSON column, sp_executesql parameter) now accepts 1- or 2-part dotted type names — was previously single-`Name` only. Each site routes through `Simulation.ResolveTypeReference(BatchContext, MultiPartName, Name leaf, …)` which checks `Schema.AliasTypes` first and falls back to `SqlType.GetByName` for built-in types. A length-parameter at the alias-usage site raises **Msg 2716 St 3** verbatim (probe-confirmed against SQL Server 2025; distinct from the State-1 form for built-ins).

**Nullability inheritance**: probe-confirmed semantics — bare `CREATE TYPE T FROM int` and explicit `FROM int NULL` both set the alias's `IsNullable=true`; `NOT NULL` sets false. When a column / variable references an alias without its own explicit `NULL` / `NOT NULL` marker, the alias's default propagates. Column-site explicit marker (`c MyAlias NULL`) overrides the alias default.

**Errors enforced verbatim**: Msg 219 (duplicate type name, alias-vs-alias or alias-vs-table-type), Msg 222 (`The base type "X" is not a valid base type for the alias data type.`), Msg 2716 St 3 (column width at alias-usage site), Msg 218 (DROP TYPE on missing alias without IF EXISTS).

**`sys.types`** rows for alias types ship via `BuiltInResources.cs::EnumerateSysTypes` — `system_type_id` from the **underlying** builtin (e.g. 231 for nvarchar-of-… , 56 for int-of-…), `user_type_id` from the alias's per-database allocation (starts at 256), `schema_id` from the owning schema, `is_user_defined=1`, `is_table_type=0`, `is_nullable` from the alias's stored marker.

**Known fidelity gaps** (deferred — not load-bearing for the bacpac baseline):
- `HeapColumn` doesn't retain a back-pointer to its declaring `AliasType`. Consequence: `sys.columns.user_type_id` surfaces the underlying built-in's id (not the alias's) when a column is alias-typed, and `DROP TYPE` on an alias type doesn't enforce **Msg 3732** (referenced-by-object). Real bacpac load never drops alias types during import, so this is acceptable for the baseline.
- Alias-type max-length surfaces on `sys.types.max_length` aren't emitted (the catalog view's shipped subset doesn't include `max_length` yet — pre-existing gap from before this bundle, not specific to alias types).
- Alias-of-alias not modeled — `CREATE TYPE T2 FROM T1` where T1 is itself an alias raises Msg 222 (matches probe: real SQL Server rejects alias-of-alias the same way).

### [ ] Extended properties (`sp_addextendedproperty` + `sys.extended_properties`) (medium)
538 in AW — they're how SQL Server attaches descriptions/metadata to schemas/tables/columns/etc. Surface needed:
- `sp_addextendedproperty @name='MS_Description', @value='…', @level0type='SCHEMA', @level0name='dbo', @level1type='TABLE', @level1name='ErrorLog', @level2type='COLUMN', @level2name='ErrorMessage'` (the canonical EXEC form the loader would emit)
- `sys.extended_properties` catalog view (read-back)
- `fn_listextendedproperty` table-valued system function (often used by ORMs and Schema Compare tools)
- Storage: probably a `Dictionary<(int class, int major_id, int minor_id), Dictionary<string, SqlValue>>` on `Database`.

Pure metadata — no semantic effect on queries. Probably one bundle on its own.

### [ ] `hierarchyid` data type (medium)
2 columns in AW (`[HumanResources].[Employee].[OrganizationNode]` + procedure parameter). hierarchyid is variable-length binary with a documented encoding for paths like `/1/2/3/`. Surface:
- Storage type + literal parser (`hierarchyid::Parse('/1/2/3/')`, `hierarchyid::GetRoot()`)
- Methods: `.GetAncestor(n)`, `.GetDescendant(child1, child2)`, `.GetLevel()`, `.IsDescendantOf(other)`, `.GetReparentedValue()`, `.ToString()`, `.Read()`/`.Write()`. AW procs use `.GetDescendant` + `.GetAncestor` per probe.
- BCP wire format: variable-length binary, length-prefixed.

Sizable but self-contained. The path encoding is well-documented (variable-bit ordinal encoding).

### [ ] DDL trigger (`CREATE TRIGGER … ON DATABASE`) (small if scoped to parse-and-discard)
1 in AW: `[ddlDatabaseTriggerLog]` — fires on `DDL_DATABASE_LEVEL_EVENTS`, writes to `dbo.DatabaseLog`. Surface: `CREATE TRIGGER … ON DATABASE … FOR <event_type_group> AS …` parser + storage + dispatch. Could legitimately be parse-and-store-but-never-fire for the baseline — the trigger only fires on DDL events the simulator may not even dispatch to a trigger loop in the first place. Worth a probe to confirm AW apps actually depend on its side effects.

### [ ] Permission statements (`GRANT` / `REVOKE` / `DENY`) (medium — needs principal model)
2 in AW. Real surface needs:
- `CREATE USER` / `CREATE ROLE` / `ALTER ROLE … ADD MEMBER` (or accept-as-no-op for the principals AW references — `public` and the schema authorizers)
- `GRANT <perm> ON <object> TO <principal>` / `REVOKE` / `DENY`
- `sys.database_principals`, `sys.database_permissions`, `sys.database_role_members`

For the loader's "baseline AW load" goal, parse-and-discard is probably enough — the simulator has no permission enforcement, so GRANT/REVOKE are no-ops semantically. The catalog views surface as empty/synthesized. Real feature work deferred.

### [ ] Full-text catalog + index (large — likely skip-with-diagnostic)
1 catalog (`[AW2025FullTextCatalog]`) + 3 indexes in AW. Full surface: `CREATE FULLTEXT CATALOG` / `CREATE FULLTEXT INDEX … ON tbl(col LANGUAGE 1033) KEY INDEX <pk> ON <catalog>`, `CONTAINS()` / `FREETEXT()` predicates, `CONTAINSTABLE` / `FREETEXTTABLE` rowset functions, `sys.fulltext_catalogs` / `sys.fulltext_indexes`.

The query-time predicates (`CONTAINS`, `FREETEXT`) are the hard part — they need a tokenizer/stemmer/inverted-index/relevance-rank pipeline. Recommend **skip-with-diagnostic** for the loader; AW data still loads, full-text-using queries fail at parse with `NotSupportedException("Full-text search is not modeled")`. Real feature deferred indefinitely unless an application needs it.

### [ ] `xml` data type + XML schema collections + XML methods + XML indexes (very large)
9 column uses in AW (`Production.Document.DocumentSummary`, `Person.Person.AdditionalContactInfo`, `HumanResources.JobCandidate.Resume`, etc.), 6 `SqlXmlSchemaCollection` (with embedded XSD schemas in `SchemaExpression`), 8 `SqlXmlIndex` (`PrimaryXmlIndexUsage` 3, secondary index types). Surface:
- Storage type + `xml(SchemaCollection)` parametrization
- `CREATE XML SCHEMA COLLECTION` (with XSD payload)
- XML methods: `.value('xpath', 'sqltype')`, `.nodes('xpath')`, `.query('xpath')`, `.exist('xpath')`, `.modify('xml dml')`
- Implicit/explicit cast between `xml` and `[n]varchar`
- XML primary + secondary indexes (PATH / VALUE / PROPERTY)
- `FOR XML` query-output clause (separate but related)
- `sys.xml_schema_collections`, `sys.xml_indexes`

Genuinely large — XPath + XML DML are independent sub-languages. Recommend **skip-with-diagnostic** in the loader for the baseline (load xml columns as `nvarchar(MAX)` containing the raw XML — preserves application read-back via `.ToString()`, breaks XPath methods). Real feature could be one or several bundles down the road.

### [ ] `geography` / `geometry` data types (large — likely skip-with-diagnostic)
1 column in AW (`Person.Address.SpatialLocation`). Spatial types have their own large surface (WKT/WKB parsing, OGC methods, spatial indexes). Recommend **skip-with-diagnostic**; load as `varbinary(MAX)` or `nvarchar(MAX)` in degraded mode, application queries that call `.STDistance` etc. fail at parse.

## BCP wire format

The `Data/<schema>.<table>/TableData-NNN-NNNNN.BCP` files are the per-table data payload. Probed against `Production.ProductCategory` (4 rows, 192 bytes, schema `int IDENTITY NOT NULL, nvarchar(50) NOT NULL, uniqueidentifier NOT NULL, datetime NOT NULL` — verified row 1 = `(1, 'Bikes', <guid>, 2019-04-30 00:00:00)`):

| Type family | Wire layout | Notes |
|---|---|---|
| Fixed-width numeric (`int`, `bigint`, `smallint`, `tinyint`, `bit`) | raw bytes, little-endian, no prefix | `int` = 4 bytes LE |
| Fixed-width temporal (`datetime`, `smalldatetime`, `date`) | raw bytes, no prefix | `datetime` = 4-byte int32 days + 4-byte uint32 ticks-of-day (1/300 sec) |
| Variable-length text/binary (`nvarchar`, `varchar`, `varbinary`) | 2-byte LE byte-length prefix + bytes | nvarchar = UTF-16 LE; `0xFFFF` likely = NULL (needs probe confirm) |
| Length-prefixed fixed (`uniqueidentifier`, `decimal`/`numeric`, `money`, `smallmoney`, `datetime2`, `datetimeoffset`, `time`) | 1-byte length-prefix (= type width) + bytes | guid = `0x10` + 16 bytes; `0x00` likely = NULL |
| MAX types (`varchar(MAX)`, `nvarchar(MAX)`, `varbinary(MAX)`, `text`, `ntext`, `image`) | length prefix likely 8-byte for full size + bytes (or chunked) | needs probe — not in ProductCategory |
| `hierarchyid` | variable-length binary, length-prefixed | covered by hierarchyid feature work |
| `xml` | variable-length text/binary, length-prefixed | covered by xml feature work; if xml is loaded as nvarchar(MAX) in degraded mode, falls through to the MAX-types row |
| `sql_variant` | special envelope (type byte + value) | not yet investigated, AW may or may not use |

Encoding-edge probes needed (carve out tiny custom BACPACs locally via `SqlPackage`):
- NULL sentinel for each prefix class (1-byte / 2-byte / fixed-no-prefix)
- `decimal(p, s)` precision/scale layout (probably sign byte + LE mantissa)
- `datetime2(N)` and `datetimeoffset(N)` precision dependence
- MAX-type encoding when row >> 8KB
- `varbinary(N)` for `rowversion` columns (auto-generated server-side — does BACPAC export them or skip?)
- `IDENTITY` reseed: confirm `LastValue` annotation matches the actual max-allocated rather than max-inserted

## Order of operations toward AW baseline

Rough sequence — work each bundle to completion, update this checklist, then revisit BACPAC scoping once the prerequisites land:

1. ~~**Database options expansion**~~ + ~~**UDDTs / alias types**~~ (both shipped 2026-05-14)
2. **Extended properties** (next — mid-size, self-contained)
3. **`hierarchyid`** (large but self-contained, unblocks `HumanResources.Employee`)
4. **DDL trigger + permission statements** as parse-and-store-but-no-enforce (smallest scope; both end up as catalog-view-visible no-ops)
5. **Loader baseline implementation**, with `xml` / `geography` / full-text **loaded in degraded mode** (xml/geography → nvarchar(MAX), full-text indexes → parse-and-discard). Diagnostics report which features were degraded.
6. **Real xml + spatial + full-text** as separate post-baseline initiatives, each promoted from degraded-mode-via-diagnostic to first-class as bundles complete.

Loader code layout (target, when baseline lands):
- `SqlServerSimulator/Storage/Bacpac/BacpacReader.cs` — OPC zip walker, dispatches to model + data readers
- `SqlServerSimulator/Storage/Bacpac/ModelXmlReader.cs` — `model.xml` → DDL emitter
- `SqlServerSimulator/Storage/Bacpac/BcpRowReader.cs` — `*.BCP` → row decoder
- `SqlServerSimulator/Storage/Bacpac/BacpacLoadResult.cs` — diagnostics carrier (Skipped + Degraded lists)
- Public surface: `internal static Simulation Simulation.FromBacpac(string path, out BacpacLoadResult diagnostics)` + Stream overload, kept internal until baseline AW load works end-to-end

## Status

Pre-implementation. Scoping done 2026-05-14. Implementation paused until prerequisite features land; resume by reopening the session named `bacpac`.
