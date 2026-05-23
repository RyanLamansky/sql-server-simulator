# Built-in function coverage TODO

Captured from a Microsoft Learn category-by-category audit of T-SQL built-in functions. The simulator's dispatch was cross-checked against `Parser/Expression.cs::ResolveBuiltIn`, the `@@`-prefixed dispatch in `Parser/AtAtKeyword.cs` + `Value.cs`, `Parser/Expressions/AggregateExpression.cs`, `Parser/Expressions/WindowExpression.cs`, and the FROM-source rowset dispatch in `Parser/Selection.{OpenJson,StringSplit,ListExtendedProperty}.cs`.

**Sort:** popular and/or cheap at top → rarely used and/or expensive at bottom. 🎯 marks an item whose completion closes a Microsoft category. Re-fetch the catalog page at <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the list complete in case Microsoft has added anything.

**Delete this file** (with user approval) once Tiers 1–4 are complete and each Tier 5 family has either graduated (parent feature implemented + functions ticked) or been explicitly deferred along with its parent feature. Tier 6 is always OK to leave unchecked.

## Tier 1 — Cheap dispatch additions

One-case extensions; infrastructure already exists.

- [x] **CHOOSE(index, val1, val2, ...)** — index into VALUES list; sibling of IIF. 🎯 closes **Logical (2/2)**.
- [x] **XACT_STATE()** — tristate -1/0/1 derived from `@@TRANCOUNT` + doomed flag.
- [x] **DB_ID([name])** / **DB_NAME([id])** — `Simulation.Databases` lookup, current-DB fallback.
- [x] **USER_NAME / SUSER_NAME / CURRENT_USER / SESSION_USER / SYSTEM_USER / ORIGINAL_LOGIN / SUSER_SNAME / USER** — current-principal placeholders ("dbo").
- [x] **HOST_NAME() / APP_NAME()** — session-attribute placeholder constants ("").
- [x] **DATENAME(part, date)** — reuses DATEPART's keyword tables; returns localized string.
- [x] **CURRENT_DATE** — `SYSDATETIME().Date`.
- [x] **IDENT_INCR(table) / IDENT_SEED(table)** — siblings of existing IDENT_CURRENT.
- [x] **ROWCOUNT_BIG()** — bigint cast of `@@ROWCOUNT`.
- [x] **@@DATEFIRST** — added to `AtAtKeyword` enum; constant 7.
- [x] **@@PROCID** — added; current proc's object_id, 0 outside a proc.
- [x] **@@MAX_PRECISION / @@MAX_CONNECTIONS / @@SERVERNAME / @@SERVICENAME / @@LANGID / @@LANGUAGE / @@TEXTSIZE / @@OPTIONS / @@NESTLEVEL / @@DBTS / @@REMSERVER** — all wired via `Value`'s constant switch + dedicated classes for session-state ones (NESTLEVEL, DBTS, PROCID).

## Tier 2 — Small additions

Existing infrastructure plus a small lookup, property table, or splitter.

- [x] **TYPE_NAME(id)** — sibling of TYPE_ID.
- [x] **PARSENAME('a.b.c.d', n)** — dot-split + segment indexing.
- [x] **MIN_ACTIVE_ROWVERSION()** — exposes the rowversion counter; over-approximates to "current next-to-allocate".
- [x] **GETANSINULL([db])** — usually 1; one-line.
- [x] **ORIGINAL_DB_NAME()** — returns `Simulation.DefaultDatabaseName`.
- [x] **COL_LENGTH(table, col) / COL_NAME(table_id, col_id)** — heap-column metadata lookup using sys.columns max_length conventions.
- [x] Verify three-part-name reach of **SCHEMA_ID / SCHEMA_NAME / OBJECT_ID / OBJECT_NAME** across the cross-DB read path. Probed against SQL Server 2025 (2026-05-23): `SCHEMA_ID` / `SCHEMA_NAME` are leaf-name-only / current-DB-scoped (multi-part input returns NULL — no 3-part-name reach exists for these); `OBJECT_ID`'s name-arg 3-part form (`'db.schema.tbl'`, `'[db].[schema].[tbl]'`, `'db..tbl'`) routes to the named DB; `OBJECT_NAME(id, db_id)`'s second arg is load-bearing — routes the lookup to the named DB by id. Fixed two divergences along the way: `OBJECT_ID('db..tbl')` was dropping the empty middle segment (now substitutes `dbo` like `BatchContext.ParseObjectName`); `OBJECT_NAME(id, db_id)` was ignoring the second arg (now routes via the same alphabetical-position id scheme as `DB_ID` / `DB_NAME`). Tests in `CrossDatabaseTests`.

## Tier 3 — Modest effort, popular surface

- [x] **STRING_ESCAPE(text, 'json')** — JSON-string escape pass.
- [x] **TRANSLATE(input, chars, translations)** — character-by-character substitution.
- [x] **PARSE(string AS type [USING culture])** / **TRY_PARSE(...)** — culture-aware Convert via .NET Parse + CultureInfo. 🎯 closes **Conversion (6/6)**.
- [x] **DATETRUNC(part, date)** — floor to start of part (year/month/day/hour/minute/quarter/week).
- [x] **SWITCHOFFSET(dto, offset)** — adjust `datetimeoffset` preserving the UTC instant.
- [x] **TODATETIMEOFFSET(dt, offset)** — attach offset to `datetime` / `datetime2`.
- [x] **DATE_BUCKET(part, bucket_width, date [, origin])** — bucket-aligned floor (origin defaults to 1900-01-01). 🎯 closes **Date/Time (26/26)** with the items above.
- [ ] **ANY_VALUE(expr)** — aggregate returning any group value. (Note: probe showed not yet shipped in SQL Server 2025 RTM — Azure-only — verify before adding.)
- [x] **JSON_OBJECT(k:v, ...)** / **JSON_ARRAY(v, ...)** — JSON literal builders. Bare-`:` postfix in `Expression.Parse` is gated on a new `ParserContext.StopExpressionAtBareColon` flag the JSON_OBJECT key-parse sets transiently; the existing `::` type-prefix path still resolves (peek the second colon — bail to caller on single, run static-call on double). Nested `JSON_OBJECT` / `JSON_ARRAY` / `JSON_QUERY` values embed raw via compile-time `ProducesJson(Expression)` detection. Default null clause is `ABSENT ON NULL` (probe-confirmed); explicit `NULL ON NULL` / `ABSENT ON NULL` suffix parsed via `ReservedKeyword`-based `Keyword.On` / `Keyword.Null` match. Output format matches SQL Server: bit→true/false, varbinary→base64, datetime2→T-separated ISO, dates/times/guids→quoted ISO/uppercase-hex; float/real keep simulator's existing G15/G7 quirk. Tests in `JsonBuilderTests` (36).
- [x] **JSON_PATH_EXISTS(json, path)** — true if path resolves; routes through existing `JsonPath.Walk`.
- [ ] **FORMATMESSAGE(msg_id_or_string, args...)** — printf-style with `sys.messages` fallback (sys.messages not modeled).
- [x] **CHECKSUM(args...) / BINARY_CHECKSUM(args...)** — fast 32-bit FNV-1a fold; semantic guarantee matches, bit pattern does not (documented quirk).
- [x] **BIT_COUNT(num)** — popcount.
- [x] **GET_BIT(num, index)** — test bit.
- [x] **SET_BIT(num, index [, value])** — set/clear bit.
- [x] **LEFT_SHIFT(num, n) / RIGHT_SHIFT(num, n)** — shift operators (logical right shift, probe-confirmed). 🎯 closes **Bit Manipulation (5/5)** with the three above.
- [x] **GENERATE_SERIES(start, stop [, step])** — bigint TVF; popular for ad-hoc SQL and tests.

## Tier 4 — Higher effort or narrower popularity

Need new aggregators, large property tables, or principal-model wiring.

- [ ] **CUME_DIST() OVER (...)** — cumulative distribution window.
- [ ] **PERCENT_RANK() OVER (...)** — relative-rank window.
- [ ] **PERCENTILE_CONT(p) WITHIN GROUP (ORDER BY ...)** — sort-and-interpolate aggregator.
- [ ] **PERCENTILE_DISC(p) WITHIN GROUP (ORDER BY ...)** — sort-and-pick aggregator. 🎯 closes **Analytic (9/9)** with CUME_DIST / PERCENT_RANK / PERCENTILE_CONT and ANY_VALUE from Tier 3.
- [ ] **JSON_OBJECTAGG(k VALUE v)** / **JSON_ARRAYAGG(expr)** — JSON aggregators. 🎯 closes **JSON (10/10)** with JSON_OBJECT / JSON_ARRAY / JSON_PATH_EXISTS from Tier 3.
- [x] **STR(float, len, decimals)** — float-to-string formatting (right-aligned, half-away-from-zero rounding, `*` overflow).
- [x] **DIFFERENCE(s1, s2)** — SOUNDEX-distance helper (0-4 matches).
- [x] **SOUNDEX(s)** — English phonetic encoding (4-character code, standard algorithm). 🎯 closes **String (31/31)** with STR, STRING_ESCAPE, TRANSLATE, DIFFERENCE.
- [ ] **SESSION_CONTEXT(key)** + **sp_set_session_context** — per-session key/value store.
- [ ] **CONTEXT_INFO()** + **SET CONTEXT_INFO** — single 128-byte binary slot.
- [x] **OBJECTPROPERTY(id, prop)** — 10 common Is-X boolean checks (IsTable/IsView/IsProcedure/IsTrigger/IsScalarFunction/IsTableFunction/IsInlineFunction/IsMSShipped/IsDeterministic/IsSchemaBound); unknown property → NULL.
- [x] **OBJECTPROPERTYEX(id, prop)** — Is-X (delegates to OBJECTPROPERTY) + BaseType / SchemaId / Cardinality / TableHas* extended properties; returns nvarchar (sql_variant proxy).
- [x] **COLUMNPROPERTY(table_id, col, prop)** — AllowsNull / IsIdentity / IsComputed / IsRowGuidCol / Precision / Scale / CharMaxLen / ColumnId / UsesAnsiTrim.
- [x] **INDEX_COL / INDEXKEY_PROPERTY / STATS_DATE** — index introspection. INDEX_COL and INDEXKEY_PROPERTY use the shared `IndexLookup` helper to resolve `index_id` against `HeapTable.Indexes` + PK/UQ constraints (no B-tree needed; the metadata is enough). STATS_DATE intentionally diverges from real by returning `HeapTable.CreateDate` instead of NULL — fake-but-realistic since the simulator has no stats lifecycle.
- [x] **INDEXPROPERTY(object_id, index, prop)** — IsClustered / IsUnique + always-0 physical-stat props (IsAutoStatistics / IndexDepth / etc.).
- [x] **SERVERPROPERTY(name)** — 35-property switch returning placeholder constants; values surface as nvarchar (real returns sql_variant).
- [x] **TYPEPROPERTY(type, prop)** — Precision / Scale / AllowsNull / UsesAnsiTrim against a 25-entry system-type lookup table.
- [ ] **OBJECT_DEFINITION(id)** — requires storing source text for procs/views/triggers/UDFs.
- [ ] **CONNECTIONPROPERTY(name) / SESSION_ID() / CURRENT_REQUEST_ID() / CURRENT_TRANSACTION_ID()** — session/request identity scalars.
- [x] **HAS_PERMS_BY_NAME / IS_MEMBER / IS_ROLEMEMBER / IS_SRVROLEMEMBER** — placeholder permission checks (simulator doesn't enforce permissions; HAS_PERMS_BY_NAME returns 1 for any non-NULL input; IS_MEMBER('public') returns 1, other roles return 0).
- [x] **SUSER_ID / USER_ID / DATABASE_PRINCIPAL_ID** — principal-id lookups against `Database.Principals`. SUSER_SID/SUSER_SNAME pending.
- [ ] **PWDCOMPARE(clear, hash) / PWDENCRYPT(clear)** — password hashing helpers.
- [ ] **LOGINPROPERTY(login, prop)** — login-property switch.

## Tier 5 — Blocked on a larger unmodeled feature

Real applications use these. Each family graduates when its parent feature lands; until then they sit as markers. Ticking an item here implies the parent feature is also done.

### Cursors (DECLARE CURSOR / OPEN / FETCH / CLOSE not modeled)
- [ ] **@@CURSOR_ROWS / @@FETCH_STATUS / CURSOR_STATUS**

### Graph (node/edge tables not modeled)
- [ ] **EDGE_ID_FROM_PARTS / GRAPH_ID_FROM_EDGE_ID / GRAPH_ID_FROM_NODE_ID / NODE_ID_FROM_PARTS / OBJECT_ID_FROM_EDGE_ID / OBJECT_ID_FROM_NODE_ID**

### Application locks (sp_getapplock / sp_releaseapplock not modeled)
- [ ] **APPLOCK_MODE / APPLOCK_TEST** — used in real apps for cross-session coordination.

### Change tracking (not modeled)
- [ ] **CHANGETABLE(CHANGES ...) / CHANGETABLE(VERSION ...)**

### Partitioning (not modeled)
- [ ] **$PARTITION.partition_function_name(value)**

### CLR assemblies (`CREATE ASSEMBLY` rejected)
- [ ] **ASSEMBLYPROPERTY**

### ML scoring (PREDICT model surface not modeled)
- [ ] **PREDICT(MODEL = ..., DATA = ...)**

### Ad-hoc data sources (bulk / heterogeneous adapter surface not modeled)
- [ ] **OPENROWSET** — file/bulk + provider rowsets.
- [ ] **OPENDATASOURCE / OPENQUERY** — inline linked-server access (four-part-name reads already ship; these are the ad-hoc forms).
- [ ] **OPENXML** — pre-`OPENJSON` XML rowset; SQL Server 2000-era but still hit in legacy code.

## Tier 6 — Skip candidates (genuinely rare, deprecated, or won't-model)

These can stay unchecked at delete time without blocking it.

### Legacy text/image
- [ ] **TEXTPTR / TEXTVALID** — paired with deprecated READTEXT / WRITETEXT / UPDATETEXT.

### System statistical (DBA introspection)
- [ ] **@@CONNECTIONS / @@CPU_BUSY / @@IDLE / @@IO_BUSY / @@PACK_RECEIVED / @@PACK_SENT / @@PACKET_ERRORS / @@TIMETICKS / @@TOTAL_ERRORS / @@TOTAL_READ / @@TOTAL_WRITE / fn_virtualfilestats** — could return constants but rarely called from app code.

### Files / filegroups (single-file simulator unlikely to model)
- [ ] **FILE_ID / FILE_IDEX / FILE_NAME / FILEGROUP_ID / FILEGROUP_NAME / FILEGROUPPROPERTY / FILEPROPERTY**

### Certificates (certificate model unlikely)
- [ ] **CERTENCODED / CERTPRIVATEKEY**

### Full-text properties (DDL exists; engine won't be modeled)
- [ ] **FULLTEXTCATALOGPROPERTY / FULLTEXTSERVICEPROPERTY**

### FILESTREAM (storage binding outside simulator scope)
- [ ] **GET_FILESTREAM_TRANSACTION_CONTEXT**

## Fidelity gaps in already-implemented functions

Real bugs / limitations against shipped functions — fixes are tickable work, not design decisions.

- [ ] **OPENJSON ... WITH ... AS JSON** on `nvarchar(max)` raises `NotSupportedException` (sub-tree extraction missing); non-`nvarchar(max)` raises Msg 13618.
- [ ] **REPLICATE** of a MAX-typed *column* reference truncates to 8000 bytes (parse-time type resolver doesn't reach FROM-source columns; literal / CAST-target inputs work).
- [ ] **GROUPING / GROUPING_ID** only accept `Reference` arguments — `GROUPING(a+1)` paired with `GROUP BY a+1` always raises Msg 8161 instead of matching structurally.
- [ ] **STRING_SPLIT(..., ..., CAST(@v AS INT))** wrapped-variable accepted; real SQL Server rejects all variable-bearing `enable_ordinal` shapes regardless of wrapping.

## Documented design choices (review rationale)

Function-level decisions documented in CLAUDE.md's Quirks section. These shipped intentionally — the simulator works correctly under the documented contract — but the original rationale may have aged out. Worth a look before either checking off or re-affirming.

- [ ] **APPROX_COUNT_DISTINCT** implemented as exact `COUNT(DISTINCT)`. Original rationale: same semantic guarantee, no HyperLogLog dependency. Review: is the perf gap visible against the simulator's in-process workloads? If not, the simpler implementation stays defensible.
- [ ] **CHECKSUM_AGG** uses an order-independent XOR fold. Original rationale: same-multiset-same-checksum guarantee preserved, bit-identical match wasn't required. Review: have any consumers needed bit-identical checksums (e.g., for replication-comparison parity)?
- [ ] **DATALENGTH** returns `int` for MAX-typed inputs; real returns `bigint`. Original rationale: result fits in int for any value the simulator can produce. Review: now that storage scales further, does the projection-schema mismatch break any consumer (EF Core mappings, ORM projections)?
- [ ] **`float` CAST/CONVERT** text formatting uses .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific. Original rationale: .NET formatting is the default; SQL Server's specific format wasn't required for fidelity oracle. Review: do users hit float-as-string comparisons in real workloads?
- [ ] **`decimal` / `numeric`** backed by .NET `decimal`; values requiring more than 28 significant digits aren't modeled (declarations through `decimal(38, *)` accepted so storage byte-width matches). Original rationale: .NET decimal is the simplest path. Review: do real schemas use the high-precision range, or is 28 sig digits enough in practice?
- [ ] **`hierarchyid` / `geography` / `geometry` CAST** encoding is simulator-native. Original rationale: byte-identical transfer wasn't a fidelity-oracle requirement. Review: would byte-identical encoding unlock cross-engine data movement that's now in scope?

## Notes for the next iteration

- Cross-listings: STRING_AGG and STRING_SPLIT appear under multiple categories on Microsoft's site; SCHEMA_ID and SCHEMA_NAME appear under both Metadata and Security. Each is counted once.
- Per the project convention, probe the live SQL Server 2025 reference instance before encoding "matches SQL Server" behavior — see `reference_real_sql_server.md` in user memory for connection details.
