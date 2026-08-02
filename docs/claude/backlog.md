# Backlog

Forward-looking work list: missing features, fidelity gaps in shipped behavior, and design choices worth revisiting.
**Not a checklist** — completed work is removed, not ticked.

Ordering within each section leans toward predicted importance (popularity × ease, the Operating-goal weighting in [`../../CLAUDE.md`](../../CLAUDE.md)), but is **explicitly non-authoritative**: anything here is valid to pick up, and so is anything *not* here.

## Completion process

When an item ships:

1. **Remove it from this file.**
   No checkmarks, no archive section — git records *what* changed; this file is only the open list.
2. **Ensure a `docs/claude/` deep-dive documents it** — explicit function/feature names, operational structure, probe-confirmed quirks and divergences.
3. **Ensure CLAUDE.md carries trigger keywords** linking to that deep-dive.
   The detail must be reachable from a fresh clone in one hop: CLAUDE.md keyword/phrase → deep-dive.
   Never rely on git history (or this file) for the *how*.

Per project convention, probe the live SQL Server 2025 reference instance before encoding "matches SQL Server" behavior.
**Re-verify an entry before building on it** — entries go stale as the surface moves, and this file has held claims that no longer reproduced.
Two traps, both hit in practice:
give each claim its own batch, because a reference probe that creates a table and queries it in one batch fails compile-time column resolution on real (Msg 207) for reasons that have nothing to do with the claim, and reads exactly like the divergence you were looking for;
and **a hand-built probe that passes is not proof the entry is stale** — an `OUTPUT … INTO` entry was deleted on the strength of a wide matrix of hand-written shapes that all passed, when the real trigger was a destination-column *type mismatch* (`CAST(id AS bigint)`) that none of them happened to have.
Prefer re-running the oracle that found the bug over reconstructing it from the entry's prose.

This file is the home for net-new non-function feature proposals too.
CLAUDE.md's **Not modeled yet** section is the complementary *descriptive* map (what raises `NotSupportedException` / Msg today, so the surface isn't over-promised); this list is the *prospective* one.
An item can appear in both with opposite intent.

## Missing features

### Unbuilt feature areas

A standing survey of areas with no build behind them, one line each pointing at the deep-dive that holds the detail.
Presence here is status, not a priority claim — the ordering caveat at the top of this file applies.
The subsections that follow carry the areas with work in flight.

- **Full-text query pipeline** — the tokenizer / stemmer / inverted-index build behind `CONTAINS` / `FREETEXT`, which raise `NotSupportedException`; the catalog + index DDL, the BACPAC round-trip, and the property scalars ship → [`full-text.md`](full-text.md#known-gaps).
- **Spatial evaluation** — the value model, the **planar** measures and the **round-earth length / point-to-point distance** all ship; the last is measured along the *great elliptic arc*, which is the curve real uses and not the geodesic (see [`spatial.md`](spatial.md#round-earth-measures-the-great-elliptic-arc) for the derivation and the ~6e-9 residual against real). What remains:
  **ellipsoidal polygon area** for `geography`'s `STArea` — the companion problem to the arc integral, wanting spherical excess plus an ellipsoidal correction;
  **`STDistance` between shapes that aren't both points**, which needs closest-approach geometry;
  the **topological predicates** (`STIntersects` / `STContains` / … / `STIsValid`), which want a DE-9IM engine and would also supply real's Msg 24144 on a stored-but-invalid instance;
  and the **constructive operations** (`STUnion` / `STBuffer` / …), which want polygon clipping.
  Also open: `STCentroid` / `STPointOnSurface` / `EnvelopeAngle` / `EnvelopeCenter`, a spatial *column*'s property form (`Location.Lat` reads as a two-part column name — the method form works), curved shapes and FULLGLOBE, GML, SRID transformation, `sys.spatial_reference_systems` seed rows, `ALTER SPATIAL INDEX`, and query-planner use of the spatial index → [`spatial.md`](spatial.md#not-modeled-yet).
- **XQuery beyond the path subset** — FLWOR, comparison / boolean / arithmetic operators, value predicates (`[@x="1"]`), `local-name()`-style functions in the source text, and constructors in a *read* method's argument.
  Every one of them is reached by `.modify()` too, since the mutator's paths run through the same evaluator → [`xml.md`](xml.md#known-gaps).
- **XSD validation against `xml(collection)` bindings** — the collection's XSD is stored verbatim and never parsed, so nothing validates an INSERT, an UPDATE, or a `.modify()` edit, and a typed instance's paths carry untyped static types.
  That is what real's Msg 6923 (a validation failure after an edit) and Msg 2247 (a `with` value that isn't a subtype of the schema type) report, and what makes `replace value of` legal against a *typed* element rather than only its `text()` node.
  `ALTER XML SCHEMA COLLECTION ADD` sits behind the same missing parse → [`xml.md`](xml.md#known-gaps).
- **`.modify()` residue** — an `insert … before | after` on the instance's own top-level element raises `NotSupportedException` where real answers a multi-root fragment (the evaluator parses an instance as a document throughout, so the read methods can't consume one either); an `insert attribute` lands at the end of the attribute list where real threads it into its internal node order; a computed `element {…}` constructor raises `NotSupportedException`; and XML-DML text that fails to parse *before* a statement keyword reports Msg 6305 where real reports Msg 2209 → [`xml.md`](xml.md#modify--xml-dml).
- **Cursor sensitivity for an ORDER BY on a non-indexed column** — the one shape left where the simulator's effective type differs from real's: real downgrades DYNAMIC to KEYSET there (and says so via Msg 16956 under `TYPE_WARNING`), while the simulator has no index-coverage notion in cursor planning and stays DYNAMIC.
  Row limiting, deferred sources and temporal sources all ship — a `TOP` / `OFFSET` / `FETCH` cursor converts to KEYSET with the limit applied at OPEN, a derived table / CTE / APPLY right side / view is followed down to its base heaps and re-folded per FETCH, and a `FOR SYSTEM_TIME` cursor is the read-only snapshot real reports → [`cursors.md`](cursors.md).
- **Cross-database metadata visibility for the `OBJECT_*` scalars** — a catalog-view read of another database now resolves the login's user there and filters by it ([`permissions.md`](permissions.md#cross-database-metadata-visibility)), but `OBJECT_ID` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` still gate a three-part name against the *session's* database, so a restricted principal reads an id real would hide and no Msg 916 fires where the login has no user in the target.
  The scalars share `MetadataVisibilityApplies` + `CanViewMetadata`; threading the resolved object's own database through their `Gate` closure is the work.
- **A database-scoped `dbo` frame crosses unchecked** — the `dbo` bypass short-circuits ahead of the database-scoped-frame test, so a module's `WITH EXECUTE AS OWNER` / `SELF` frame reaches another database even out of a non-trustworthy source, where real raises Msg 916 (probe-confirmed, and it refuses even when the session's login is `sa`).
  Closing it means the bypass stops being a single bool read for that one frame shape → [`permissions.md`](permissions.md#cross-database-references).
- **`TRUSTWORTHY`'s authenticator rule** — the flag is modeled and the crossing it widens ships, but real also requires the source database's *owner* to hold `AUTHENTICATE` in the target (probed as the exact line: a `sa`-owned source qualifies through `dbo`; an owner with no user there, or one whose user lacks `AUTHENTICATE`, is refused).
  Every simulated database is dbo-owned and there is no `ALTER AUTHORIZATION ON DATABASE` surface, so the refusing halves aren't reachable; a database-owner model would bring them in, and would also give `DB_CHAINING` its owner-match half → [`permissions.md`](permissions.md#cross-database-references).
- **Key-range locks** — the one unbuilt piece of the locking model; HOLDLOCK widens to table-S in their place → [`locking.md`](locking.md).
- **Server-permission enforcement past the four gated points** — the DMV `VIEW …STATE` gate, `EXECUTE AS LOGIN`, server-principal metadata visibility and login DDL consult the server registry; the rest of the stored server permissions (`CONNECT SQL` as a connect-time gate, `ALTER ANY DATABASE`, `CREATE ANY DATABASE`, …) are catalog truth only, and `CONTROL SERVER` is folded into the sysadmin bypass rather than modeled → [`permissions.md`](permissions.md#known-gaps).
  `ON SERVER::` / `ON LOGIN::` securables and application roles ship.
- **Multi-statement plan caching** — the cache keys single-SELECT batches, so every SaveChanges INSERT-then-`SELECT SCOPE_IDENTITY()` round trip re-parses → [`plan-cache.md`](plan-cache.md).
  **Measured 2026-07-30 before building, and the headroom is smaller than this entry used to imply.** Against a 200-row table, one process per case, 20k warm + best of 5×20k: a cache **hit** costs 14.4 µs/op, the same SELECT forced to always miss costs 26.8, and `SET NOCOUNT ON; SELECT` — never cached — costs 20.1.
  So the cache is worth ~46% where it applies, but the ceiling on extending it to the two-statement shape is ~5.7 µs/op (~28%), and collecting it means making **every** statement kind produce a replayable plan: batches are parsed-and-executed statement by statement, and `Selection` is the only reusable plan object today.
  Worth re-scoping rather than building as stated.
  **A token-stream cache was considered and measured out** (2026-07-30): tokenizing the shapes costs 1.27 µs for the lone SELECT (12 tokens), 1.62 µs for `SET NOCOUNT ON; SELECT` (16), and 2.08 µs for EF's three-statement SaveChanges batch (34) — roughly **10% of the ~12.4 µs parse cost** and ~5-8% of the whole operation.
  The remaining 90% is the parser proper (expression trees, name resolution, schema binding, projection planning), so caching tokens recovers almost nothing and only caching *parsed plans* addresses the real cost.
  Don't re-pitch the token cache without new evidence.
  **Benchmark note**: naive in-process A/B here is worthless — measuring the cases in one process made results order-dependent by up to 2× (whichever case ran first absorbed tiered-JIT warmup; "fixed text" read 28.3 µs first and 14.5 µs last). One case per process is the only shape that reproduced.

### TDS network endpoint — follow-up phases

The endpoint ships with SQLBatch + RPC + Transaction Manager support and credential enforcement via the `CREATE LOGIN` registry (see [`tds-endpoint.md`](tds-endpoint.md)); EF Core runs over the wire through vanilla `UseSqlServer`.
Remaining phases, roughly in value order:

- **Tool shakedown** — point real client tools at the endpoint and harvest their exotic catalog queries / SET shapes into this backlog.
  Tool scope (user decision): tools a SQL Server + .NET developer already has — SSMS, sqlcmd, Visual Studio (SQL Server Object Explorer / DacFx), LINQPad; DBA-flavored tools like DBeaver are out of scope.
  **SSMS is the final boss** — an ongoing campaign, not a single leg: each surface is its own multi-round harvest, and clearing one unlocks the next.
  Cleared legs are recorded in the per-feature deep-dives (catalog surface in [`catalog-views.md`](catalog-views.md), wire behavior in [`tds-endpoint.md`](tds-endpoint.md)); the discovery harnesses are the gitignored `.vs/ssms-host` TDS host and the headless SMO property-bag drain.
  **Remaining frontier**: Table Designer, Activity Monitor, standard reports, and IntelliSense's background metadata harvest.
  Candidate follow-on legs within tool scope: Visual Studio's SQL Server Object Explorer (DacFx-driven, a different query dialect from SMO) and LINQPad.
- **SMO API sweep campaign** — `.vs/smo-sweep` (gitignored local harness) walks SMO's full reachable read surface against the self-hosted simulator and, identically, against the live reference, draining every `Property.Value` and `Script()`-ing every `IScriptable`; modes `sweep` / `sweep --live` / `diff` → sorted JSON reports + `reports/triage.md`; workflow = sweep both sides → triage → fix bundles → graduated `Tests.Smo` tests → re-sweep.
  Open items from the latest triage: (a) `DBCC SHOW_STATISTICS … WITH STATS_STREAM` (SMO `Statistic.Stream`) stays `NotSupportedException` — it wants the raw serialized statistics-histogram blob, which the simulator has no faithful source for; (b) the unmodeled runtime/OS surfaces SMO reaches as absent objects (backup history `msdb.dbo.backupset`, `sys.dm_tran_persistent_version_store_stats`, file-space/IO DMVs, `sys.dm_os_process_memory`, `master.dbo.sysprocesses`, registry/OS xps) — surfaced as `PropertyCannotBeRetrievedException` / defaults, the legitimate-gap category (`FILEPROPERTY` ships — see [`catalog-views.md`](catalog-views.md)).
- **Open residuals of shipped wire features** (details in [`tds-endpoint.md`](tds-endpoint.md)): cancel/attention reaction is bounded by the in-flight statement's materialization, not interruptible inside a single statement's row loop; MARS never raises Msg 8628/8651 and fully materializes each session's response under the execution gate.
- **Chunked `OFFSET/FETCH` paging: per-page constant factor, complexity class matches real** (probed, plans + timings): real SQL Server also redoes the work on every page — `Top(OFFSET…)` over an ordered index scan reading offset+fetch rows when an index supplies the order, or a full scan + `Sort(TOP offset+fetch)` re-sorted per page when not; no cross-query sorted-result caching exists on either side, so "sort once, serve many pages" is rejected (it would invent behavior real doesn't have).
  The residual is constant-factor only: the simulator materializes (and for non-index order, sorts) all n rows per page regardless of offset where real's indexed plan touches only offset+fetch (measured at 150k rows / fetch 100 / offset 140k: sim ~41 ms vs real ~13 ms indexed, ~116 ms vs ~22 ms unindexed — real's sort is also 16-way parallel).
  Possible lever if paged drains ever matter: recognize index-supplied order in the `OFFSET/FETCH` path (real's plan shape) to skip the sort and bound the scan.
  Perf polish, not a fidelity gap.

### Django ORM test-suite shakedown — surfaced gaps

Running Django 5.1's own ORM test apps over the wire (mssql-django 1.7 / pyodbc) against the endpoint is a high-yield real-application oracle (harness: the runner's own `test_*` database via real `CREATE`/`DROP DATABASE` — no configuration override needed since those ship — `other` alias as a `TEST MIRROR`, incremental failing-SQL logger).
**The bar is parity with real, not absolute 100%**: many Django ORM tests fail on *real* SQL Server + mssql-django too (its own emulation limits), so the target is that the simulator fails exactly the tests real fails. Measured on a 20-app ORM slice (1021 tests): real fails 42, the simulator fails 43 — a **13-test sim-only delta** (the other 30 sim failures also fail on real). Compute the delta with `comm -23 <sorted sim FAIL/ERROR test names> <sorted real ones>`, not the raw sim count.

Fixed across the passes (parity-closing): `SET NOCOUNT ON` count suppression (blocked every identity insert — [`control-flow.md`](control-flow.md) / the DONE-token contract); year-first slash/dot date parsing ([`casting.md`](casting.md)); `INSERT … VALUES (DEFAULT)` / `db_default` ([`dml.md`](dml.md)); the implicit-conversion cluster (varchar→temporal in DATEDIFF/DATEPART, varchar operand in numeric arithmetic, DATEADD `bigint` interval — [`casting.md`](casting.md) / [`arithmetic.md`](arithmetic.md)); universal non-string→varchar coercion in `LIKE`; `@ $ #` in unquoted identifier bodies ([`grammar.md`](grammar.md)).
A `dbo.REGEXP_LIKE` built-in was **tried and reverted** — faking it as a built-in is a fidelity break, because on real the name resolves only when mssql-django's regex **CLR assembly** is installed. CLR scalar functions now ship, so the authentic path works: `EnableClr` + mssql-django's own `install_regex_clr` sequence loads `regex_clr.dll` and `dbo.REGEXP_LIKE(...)` evaluates (verified end-to-end against the real `regex_clr.dll`, with `clr_name` and MvID matching the live server byte-for-byte). See [`clr-assemblies.md`](clr-assemblies.md).
The compatibility-level-170 keyword reservation that makes that unbracketed spelling a Msg 156 syntax error now ships too, alongside the native `REGEXP_*` family — see [`grammar.md`](grammar.md#compatibility-gated-reservation-regexp_like) and [`scalars.md`](scalars.md#the-native-regexp_-family-sql-server-2025).

Re-measured 2026-07-29 on a 21-app ORM slice (**2069 tests**): **sim-only 0**, real-only 27, 74 failing on both.
The simulator now fails only tests that fail on real too — mssql-django's own emulation limits — so this oracle is exhausted at this slice width.
Re-widen the slice (or move to another real application) to find more; the runner is `runtests.py --settings=<sim|real> --parallel=1 <apps>` against a `ListenLocalAsync` host, with the delta taken **both** ways.

Getting there took eleven roots, and the pattern worth keeping is that failures cluster by *cause*, not by test — grouping them that way found each one:

- **Cascade beats breadth.** An unmodeled statement used to kill the TDS connection, so every later test in the class failed too; one statement accounted for 27 of 50 at the time. Now a statement-level fault is Msg 50000 severity 16 and the session survives ([`tds-endpoint.md`](tds-endpoint.md#statement-tier--severity-16-session-survives)).
- **Qualifier-blindness in name resolution** was the single largest class — a leaf-only match binds to the wrong column whenever a join brings a same-named one into scope, silently. It was wrong in four resolvers ([`query.md`](query.md#order-by-term-resolution)).
- The rest: outer-scope correlation from the select list, `UPDATE … SET` subqueries, parenthesized set-op branches, `OUTPUT … INTO` destination coercion, DISTINCT over a grouped projection, collation-aware `REPLACE` / `CHARINDEX`, aggregate re-homing across scopes, and `sys.time_zone_info`.

The set-op ORDER BY binding this exposed (Msg 104 for a term that binds in the first branch's FROM scope but isn't projected, Msg 207 / 4104 for one that binds nowhere) ships — see [`query.md`](query.md#top-level-order-by-over-a-set-operation).
The DISTINCT counterpart (qualified term leaf-matched against the output names) is fixed.

The constant-term rejection that exposed (**Msg 408**, plus **Msg 1008** for a bare variable term, and **Msg 5308** / **5309** for the same folded constant inside `OVER` / `WITHIN GROUP`) ships — see [`query.md`](query.md#constant-terms-msg-408-and-bare-variables-msg-1008).

**Over-permissive validation — the simulator *accepts* what real *rejects*.** This is the more dangerous divergence direction (an app query works on the simulator and breaks on real), and it is invisible to a sim-only failure list: surface it with the *reverse* delta `comm -13 <sim fails> <real fails>`, where real-only failures mean the simulator over-passes. **Whole-suite audits should always run the reverse delta — a green "matches real" claim requires both directions.**

The aggregate / GROUP BY binding rules this exposed (Msg 8120 / 8121 / 8127 containment, then Msg 130 / 8117 / 144 / 164) all ship — see [`query.md`](query.md#aggregate--group-by-binding-rules) and `GroupByContainmentTests` / `AggregateBindingRuleTests`.
Worth keeping from that round: the backlog's own statement of the Msg 164 rule was wrong until probed (it is **not** about non-determinism — `GROUP BY a + DATEPART(year, GETDATE())` is legal — but purely "contains at least one column of the query's own sources"), which is the argument for probing a rule before encoding it even when a prior entry states it confidently.

Not sim bugs (**fail on real too** — leave alone): boolean-expression `=` comparison `WHERE (a<%s)=(b<%s)` → Msg 4145 on both; `CAST(<numeric> AS datetime2)` → Msg 529 on both (Django's DurationField tests expect it); most `get_or_create` `manual_pk`/duplicate IntegrityError tests (the savepoint-rollback-after-constraint pattern was probed identical to real). Not Django-specific: default-path string→date parsing is language-neutral, so `'1/2/3'` raises Msg 241 where real's `us_english` reads it mdy (deliberate — see [`casting.md`](casting.md)).

### Result-set serialization: `FOR XML`

The JSON/XML *functions* (OPENJSON / JSON_VALUE / JSON_QUERY / JSON_MODIFY / JSON_OBJECT / JSON_ARRAY / etc.; the XML type + XQuery-subset methods — see [`json.md`](json.md), [`xml.md`](xml.md)) all ship.

**`FOR JSON` ships** — PATH (fully, incl. dotted-alias nesting + all four options), AUTO including join-nesting, the probed value-formatting/escaping table, raw-embedding of nested FOR JSON / JSON_QUERY, Msg 13600 / 13601 / 13602 / 13605 / 13620.
See [`json.md`](json.md#for-json-result-serialization).

**`FOR XML` ships** — RAW / AUTO / PATH (fully: `@attr` / element / `parent/child` nesting / `text()` / `data()` / unnamed-as-text / `PATH('')` row-tag omission / same-name concatenation), the `ELEMENTS [XSINIL|ABSENT]`, `BINARY BASE64`, `TYPE` and `ROOT[('name')]` options, the `WITH XMLNAMESPACES` prefix (declaration placement per mode, prefixed PATH / row / ROOT names, `DEFAULT`, and Msg 6868 / 6869 / 6870 / 6871 / 6872 / 6873 / 6874), AUTO's `dbobject` binary references and their Msg 6830 / 6831 split, AUTO over a set-operation result, the probed value-formatting (bit → `1`/`0`, scientific float, ISO dates, base64 binary, uppercase GUID) + position-dependent escaping table, NULL handling, the typed-vs-untyped result column and its empty-rowset asymmetry, node-embedding of every `xml`-typed column, the `_xHHHH_` **XML-name escaping** RAW / AUTO apply and the rejections PATH and the explicit row / ROOT names raise instead (Msg 6850 / 6846 / 6867 / 6849), the Msg 6819 refusal on an INSERT / SELECT INTO / assignment SELECT, and Msg 6800 / 6809 / 6851 / 6864 / 6852 / 6861 / 6829.
AUTO's join-nesting heuristics (level order, computed-column placement, consecutive-row collapse) are tabulated in [`xml.md`](xml.md#auto-nesting-shared-with-for-json-auto) and shared with FOR JSON AUTO.
See [`xml.md`](xml.md#for-xml-result-serialization).

Not built yet within them:
- **EXPLICIT mode** — the universal-table format; complex, rarely hand-authored.
- **`XMLSCHEMA` / `XMLDATA`** (inline schema emission) and the exotic PATH node functions beyond `text()`/`data()` (`comment()`, `processing-instruction()`, `node()`, `*`, `@*`).
- **One-row chunking** — real chunks the string across ~2033-char rows; the simulator returns it whole (shared by both clauses).
- **Duplicate-option rejection** — a repeated `TYPE` / `ROOT` / `BINARY BASE64` is accepted; real reports Msg 102 near `'XML'`.

### Built-in functions

Captured from a Microsoft Learn category-by-category audit (cross-checked against `Parser/Expression.cs::ResolveBuiltIn`, `Parser/AtAtKeyword.cs` + `Value.cs`, `Parser/Expressions/AggregateExpression.cs`, `Parser/Expressions/WindowExpression.cs`, and the FROM-source rowset dispatch in `Parser/Selection.{OpenJson,StringSplit,ListExtendedProperty}.cs`).
Re-fetch <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the function surface complete. 🎯 marks an item whose completion closes a Microsoft category.

Blocked on a larger unmodeled parent feature (shipping a function here implies the parent ships too):

- **Graph** (node/edge tables) — EDGE_ID_FROM_PARTS / GRAPH_ID_FROM_EDGE_ID / GRAPH_ID_FROM_NODE_ID / NODE_ID_FROM_PARTS / OBJECT_ID_FROM_EDGE_ID / OBJECT_ID_FROM_NODE_ID.
- **Change tracking** — CHANGETABLE(CHANGES …) / CHANGETABLE(VERSION …).
- **Partitioning** — `$PARTITION.partition_function_name(value)`.
- **CLR procedures / TVFs / aggregates / UDTs** — CLR *scalar functions* ship (see [`clr-assemblies.md`](clr-assemblies.md)); the rest reference `Microsoft.SqlServer.Server.SqlContext` / `SqlPipe` / `SqlDataRecord` / `SqlMetaData`, which lived in .NET Framework's `System.Data.dll` and are absent from .NET's facade, so they need a substitute `System.Data` injected into the load context that type-forwards `SqlTypes` onward and supplies the missing namespace. That shim is the whole cost; scalar functions needed none, which is why they shipped first.
- **ML scoring** (PREDICT surface not modeled) — PREDICT(MODEL = …, DATA = …).
- **Ad-hoc data sources** — OPENROWSET (file/bulk + provider rowsets); OPENDATASOURCE (the inline four-part-name form; `OPENQUERY` ships — see [`linked-servers.md`](linked-servers.md), and `OPENXML` + the `sp_xml_preparedocument` / `sp_xml_removedocument` pair ship too — see [`xml.md`](xml.md#openxml)).
  Probed: real *parses* `OPENROWSET('MSDASQL', …)` then errors on disabled ad-hoc access (**Msg 7222**) and `OPENROWSET(BULK 'file', SINGLE_CLOB)` on the missing file (**Msg 4860**); the simulator doesn't parse the FROM-source form at all (Msg 102). Ad-hoc / external data access is a feature, not a syntax tweak — the parse-then-runtime-error shape depends on the whole external-data model.

- **System stored procedures** (`sp_*` family) — formatted-metadata / management procs invoked via `EXEC sp_name`.
  Shipped so far: the `sp_help` family (`sp_help` / `sp_helptext` / `sp_helpindex` / `sp_helpconstraint` / `sp_helpdb` / `sp_helpfile` / `sp_helpstats` / `sp_helprotect` / `sp_helptrigger` / `sp_helpuser`), `sp_depends`, the ODBC/JDBC catalog set (`sp_tables` / `sp_columns_100` / `sp_pkeys` / `sp_statistics_100` / `sp_stored_procedures` / `sp_datatype_info_100`), `sp_spaceused`, `sp_who` / `sp_who2`, `sp_MSforeachtable` / `sp_MSforeachdb`, `sp_rename`, `sp_configure` and the `sp_xml_preparedocument` / `sp_xml_removedocument` pair — see [`catalog-views.md`](catalog-views.md).
  Still unregistered → **Msg 2812** ("Could not find stored procedure '…'."): `sp_MSforeach_worker` (the two `sp_MSforeach*` procs materialize their name lists rather than driving the global cursor it consumes), the `sp_add*` management family.
  A broad surface — each proc is its own result-shape contract over the catalog views.
  Ships piecemeal by popularity, not as a bundle.

Low priority / niche — simulatable (as placeholder constants or a small model) but rarely hit, so not worth attention yet:

- **`sql_variant` minor quirk** (cross-type family ordering and one-side-variant comparison both ship — see [`scalars.md`](scalars.md#sql_variant-expression-semantics)): a decimal-declared inner reports BaseType `numeric` rather than real's `decimal`.
  Probed: real preserves the declared keyword *distinctly* — `decimal` and `numeric` never collapse — through literals, table columns, variant columns, and variant variables assigned from typed variables.
  The faithful fix splits the per-`(p, s)` `DecimalSqlType` singleton by declared keyword, forking the reference-identity space the row encoder, promote paths, and catalog surfaces key on — a medium refactor whose blast radius far exceeds the one metadata string it corrects, so it's deliberately deferred.
  Deliberate exclusion, don't re-pitch: `msdb.dbo.syspolicy_configuration.current_value` stays `nvarchar` — it's a *view-body* projection (not a resource column) mixing `int` rows with a `binary` GUID row, every consumer reads a single named row and CASTs it, so a variant migration there would only touch the view SQL text for no observable gain.

## Over-permissive register

The simulator accepting what real rejects is the more dangerous divergence direction — the query passes here and fails in production — and it is invisible to any sim-only failure list (see the reverse-delta note under the Django shakedown).
This is the standing list: each entry names the error real raises that the simulator doesn't, and the linked deep-dive carries the detail.
Entries are verified against the simulator, so one that no longer reproduces is removed rather than re-worded.

- **Statement-permission residue** — every modeled CREATE / ALTER / DROP statement is gated (see [`permissions.md`](permissions.md#ddl-statement-gates)), but three securable classes real accepts a grant on have no GRANT surface here, so the alternative each offers isn't honored: `CONTROL ON TYPE::t` (DROP TYPE takes schema ALTER only), `CONTROL ON XML SCHEMA COLLECTION::c` (same), and `CONTROL ON <fulltext catalog>` (DROP FULLTEXT CATALOG takes `ALTER ANY FULLTEXT CATALOG` only).
  That direction is *under*-permissive, so it isn't a register entry — the register keeps it because closing it is the same piece of work.
  → [`permissions.md`](permissions.md#known-gaps).
- **Non-Framework CLR assemblies load** — real resolves every `AssemblyRef` against a fixed .NET Framework catalog and raises **Msg 6503** otherwise (probe-confirmed for .NET 10 and for .NET Standard 2.0); the simulator runs on .NET so all of them bind, which is also what lets the tests emit a fixture assembly without a Framework toolchain.
  → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **`CREATE CLUSTERED INDEX … INCLUDE (…)` is accepted** — real raises **Msg 10601**, *"Cannot specify included columns for a clustered index."* (probe-confirmed), where the simulator stores the INCLUDE list and numbers those columns past the key count in `sys.index_columns`.
  → [`indexes.md`](indexes.md#sysindex_columns--10-column-probe-confirmed-shape).
- **INSERT through a view over a JOIN is Msg 4405 whatever the column list**, where real accepts one whose explicit list names a single base table's columns and writes that table, the untargeted columns taking their defaults (probe-confirmed; an implicit list or one spanning two tables is Msg 4405 on real too, and DELETE is Msg 4405 whatever it touches).
  UPDATE through such a view ships, and picks its base table from the SET list — which has already parsed by the time it routes.
  INSERT's column list hasn't, so routing it wants a parser checkpoint and a pre-scan before `ProcessHeapInsert` claims a target.
  → [`programmable.md`](programmable.md#update-through-a-join-view).
- **A view over a join view is Msg 4403** where real passes INSERT / UPDATE / DELETE through both levels.
  The chain analysis composes through each level's base-table map and a join view has none; flattening the levels into one source set, or chaining the per-level output-column resolvers, is the work — and would carry the chained `WITH CHECK OPTION` composition with it.
  → [`programmable.md`](programmable.md#update-through-a-join-view).
Tracked elsewhere: the recursive-CTE construct restrictions (Msg 460 / 461 / 462 / 467) now ship — see [`ctes.md`](ctes.md#recursive-member-restrictions).
A malformed JSON document now raises **Msg 13609** wherever real does, root-level scalars included, across `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `OPENJSON` / `ISJSON` — see [`json.md`](json.md#msg-13609--the-document-isnt-json-text).
`JSON_MODIFY` now splices the document's own text instead of reserializing it, and a repeated property name resolves to the first occurrence everywhere (which is also what closed the `ArgumentException` `JsonNode.Parse` used to escape) — see [`json.md`](json.md#json_modify-edits-the-source-text) and [Duplicate property names](json.md#duplicate-property-names--the-reader-stops-at-the-first).
Integer-literal typing and the Msg 8116 id / style argument gates that depend on it now ship too — see [`arithmetic.md`](arithmetic.md#integer-literals-past-ints-range-type-numericdigit_count-0) and [`scalars.md`](scalars.md#gated-argument-slots).
So does compile-time binding of predicates: a cross-collation comparison / unification (Msg 468 / 457), a legacy-LOB string-scalar argument (Msg 8116) and an unknown column (Msg 207) now all report over an **empty** rowset and at CREATE of a module — see [`collations.md`](collations.md#compile-time-binding).
That last one covers `HAVING MAX(nosuchcol) = 1`, whose name resolves in no scope at all.
A module body now reports **every** binder error it contains rather than the first, with the shape violations behind them and Msg 455 last, and a bare `RETURN` in a scalar UDF raises **Msg 1075** while `NEWSEQUENTIALID()` in a function body raises **Msg 443** — see [`programmable.md`](programmable.md#create-time-body-binding).
The dependency surfaces ship too — `sys.sql_expression_dependencies`, `sys.dm_sql_referencing_entities` / `sys.dm_sql_referenced_entities` and `sp_depends`, all computed on read from stored definition text, which is what makes real's own name-based refresh semantics (a DROP nulls the id, a recreate restores it, an `sp_rename` leaves the stale name) fall out — see [`catalog-views.md`](catalog-views.md#expression-dependencies).
An unresolved collation now propagates as SQL Server's *No collation* label instead of throwing where it arises, so the Msg 457 / 451 split keys off the result family and the consuming operation reports its own Msg 4191 / 446 / 456 / 5335 — see [`collations.md`](collations.md#an-unresolved-collation-propagates--coercibilitynocollation).
The function body-shape rules (Msg 455 / 444 / 443 / 1075) ship too — see [`programmable.md`](programmable.md#body-shape-rules--msg-455--444--443--1075).

## Fidelity gaps in shipped behavior

Real bugs / limitations against shipped behavior — fixes are concrete work, not design decisions.

- **`JSON_OBJECT` / `JSON_ARRAY` / the JSON aggregates leave `/` unescaped** — real writes `\/` in a string value (`JSON_OBJECT('a': 'a/b')` → `{"a":"a\/b"}`, probe-confirmed 2026-08-01), where `JsonValueRender.AppendJsonString` emits it literally.
  `FOR JSON` and `JSON_MODIFY` already escape it; the escaper takes an `escapeSolidus` flag, so closing this is flipping the builders' call sites and re-probing `REGEXP_MATCHES`' `substring_matches`, which real leaves *un*escaped (also probe-confirmed) and which shares the same helper.
  → [`json.md`](json.md).
- **`JSON_MODIFY`'s third argument accepts types real refuses** — `money` / `date` / `datetime2` / `uniqueidentifier` / `varbinary` all raise **Msg 8116**, `Argument data type <type> is invalid for argument 3 of json_modify function.` (probe-confirmed), where the simulator renders each through `JsonValueRender` — a number for money, a quoted ISO / hex / base64 string for the rest.
  The accepted set is the string, integer, decimal, float, real and bit categories.
  → [`json.md`](json.md#json_modify-edits-the-source-text).
- **A JSON path can't carry trailing whitespace** — `JSON_MODIFY('{ "a" : 1 }', ' $.a ', 2)` raises Msg 13607 where real answers `{ "a" : 2 }`; `JsonPath.Parse` skips whitespace ahead of the `$` but the segment loop takes any remaining character as a segment start.
  → [`json.md`](json.md).
- **Msg 13607's wording and State are the simulator's own** — real reports State 14 and names the offending character and its index (`Unexpected character 'a' is found at position 7.`), where `SimulatedSqlException.JsonInvalidPath` reports State 1 and `Unexpected character at position 0 in path '<path>'`.
  Closing it wants the path parser to carry the position it failed at, which every raise site already knows.
  → [`json.md`](json.md#divergences).
- **Skip-mode deferred name resolution — DML target tables not placeholder-continued** — the skip-mode parse-continuation fix substitutes placeholder metadata for a missing *FROM-clause table* or *schema-qualified function* so an un-taken branch parses to completion and is discarded whole (killing the orphaned-`ELSE` cascade — see [`control-flow.md`](control-flow.md)).
  Re-probed 2026-07-29: the **spurious Msg 208 is gone** — `IF 1=0 INSERT INTO missing SELECT * FROM other; SELECT 'after'` now returns `after`, as do the UPDATE and DELETE forms, so a dead branch with a missing DML target no longer breaks the following statement.
  What still reproduces is the **orphaned `ELSE`**: the bare (non-`BEGIN`/`END`) form `IF 1=0 INSERT INTO missing … ELSE SELECT 'else-ran'` raises **Msg 102** near `else` where real runs the ELSE, and a missing **MERGE** target raises Msg 102 near `;`.
  Wrapping the dead branch in `BEGIN`/`END` parses correctly, as does the same shape over an existing table, which localizes it to the object-name swallow's flat recovery scan consuming the `ELSE` when the throw fires before the statement body is.
  Narrow (requires a *missing* DML target in a dead branch — the common safe-guard idiom targets an existing table), .
  The faithful fix is placeholder-continuation through the DML column-validation surface (INSERT column-list / arity, UPDATE SET / DELETE WHERE against a placeholder target), which is a broad, per-processor change — deferred as low-frequency.
- **Nested paren/subquery/function caps below real's absolute thresholds** — the expression-depth restructure (iterative precedence-climbing parse, iterative `Run`/`GetSqlType`, n-ary `AND`/`OR`, NOT-collapse) removed the process-death risk and lifted flat operator chains to no artificial cap.
  What remains is a *fidelity* gap on the deterministic nesting caps (see [`grammar.md`](grammar.md) "Expression depth limits"): the shared paren/subquery/function budget caps at 500 units (paren 500, subquery 83) vs real's stack-dependent 1015/168, because the simulator's parse frames are fatter (a 1 MB Debug thread parses only ~990 nested parens).
  The subquery ≈ 6× paren ratio matches real; the absolute numbers are lower to keep Msg 191 firing with headroom before the stack probe.
  Deep *function* nesting additionally surfaces Msg 8631 instead of Msg 191 on tight (≤1 MB) threads (its frames are fattest).
  Closing the gap toward real's numbers requires slimming the function-argument recursion frame (`ResolveBuiltIn` + per-function ctors are on the live path).
  Low demand — generated SQL rarely nests past tens; both outcomes are graceful.
  CASE/IIF nesting (cap 10, Msg 125) already matches real exactly.
- **Result-set `fNullable` inference — remaining long tail** — the projection nullability that drives the COLMETADATA `fNullable` flag (see `Expression.ResultIsNullable`) covers the structural cases: direct refs, literals, ISNULL, CASE, and (added in the go-mssqldb pass) CONCAT / CONCAT_WS (always NOT NULL), IIF (both-arm rule), and VALUES row-constructor columns (OR over rows).
  Still over-claiming nullable vs real: (1) **per-function** signatures — `CEILING` / `FLOOR` / `ROUND` / `SIGN` / `GETDATE` project NOT NULL on real while `ABS` / `POWER` / `SQUARE` / `NEWID` / `RAND` stay nullable, an idiosyncratic per-builtin table with no clean rule; (2) **`@@`-variable** nullability (`@@ROWCOUNT` / `@@SPID` are NOT NULL on real); (3) **string `+` concatenation** of two non-null operands (real projects NOT NULL, but the resolver has no `BatchContext` to distinguish string-vs-arithmetic `+` — `1+1` stays nullable on real, so it can't blanket-propagate); (4) **constant-fold** cases where real eliminates a null arm — `NULLIF(1,2)`, no-ELSE `CASE WHEN <constant> …`, all-constant `COALESCE(NULL,5)` (realistic `COALESCE(agg,0)` already matches: nullable on both).
  All are metadata-only over-claims (nullable is the safe direction); low demand, no clean rule.
- **Dependency-surface residue** — the four surfaces ship (see [`catalog-views.md`](catalog-views.md#expression-dependencies)), with three known divergences.
  Column granularity is name-based (a statement frame touches column `C` of referenced object `T` when it names `T` and mentions `C`); a qualified mention narrows to its own source, so joins / `APPLY` / `MERGE` match real exactly, but an **unqualified** mention in a multi-source frame still lands on every source that has a column by that name, and a MERGE target's key column picks up an extra `is_updated` when it appears in both the `ON` and a `WHEN NOT MATCHED THEN INSERT` column list.
  Closing both wants parse-time (source, ordinal) capture, which the per-row name-keyed resolver doesn't do.
  `sys.dm_sql_referenced_entities`' **Msg 2020 arrives before the rows** rather than after them, because the reader materializes the rowset before delivering it, where real yields what it found and then raises.
  `sys.sysdepends` / `sys.sql_dependencies` aren't projected — real's own `sp_depends` reads them, the simulator reads the shared analysis directly, so a caller querying the legacy tables gets Msg 208.
- **`OBJECTPROPERTY(id, 'IsDeterministic')` and the `CAST` / `CONVERT` style rule** — the module walk ships (schema-binding precondition, nondeterministic-built-in table, transitive module references — see [`catalog-views.md`](catalog-views.md#isdeterministic)), but it classifies conversions between a date/time type and a character string as deterministic where real keys off the style argument.
  Closing it needs the conversion's source and target types, which the token-level body scan doesn't carry; the probed style table is recorded in `catalog-views.md` for whoever picks it up.
- **Runtime-error streaming shape** — a per-row runtime error (`SELECT 10/0`, arithmetic overflow) is emitted by real *after* COLMETADATA, so a streaming client surfaces it while draining rows; the simulator raises it at execute-time before any COLMETADATA, so the client sees it from the initial execute call.
  Message / number / class match; only the wire position differs.
  Deferred — deep change to statement execution ordering, low practical impact.
- **Trailing-space MIN/MAX representative** — for a group of values differing only in trailing spaces (sort-equal under SQL Server), MIN/MAX returns a different byte-variant than the live server's scan-order representative.
  Surfaced by the AdventureWorks crosscheck on synthetic XML data (`vJobCandidateEducation._max_Edu_Loc_CountryRegion`).
  Needs trailing-space-insensitive compare + SQL Server's unspecified MAX-tie scan-order.
  See [`collations.md`](collations.md) "byte-exact sort" trailing-space note.
  **Deferred** — synthetic data, and the representative is unspecified scan-order on the live side.
- **Leaked-connection session cleanup** — a `SimulatedDbConnection` that's never `Dispose`d never reclaims its session state: an open transaction holds its locks and pins the MVCC version store, `##temp` tables linger, session-owned app locks stay held, and the SPID accumulates.
  Real SqlClient's GC-finalization eventually closes a leaked connection and the server resets the session, so this is a genuine fidelity divergence.
  **Scope correction: the fix is bigger than "weaken the registry."**
  Investigation found *three* global strong-reference cycles that pin exactly the connections that hold session state, so GC can't collect them and a finalizer never fires: (1) `LockResource.Hold.Owner` is a strong `SimulatedDbConnection` (reachable Database → table → lock → hold) — pins any lock- or session-app-lock-holding connection; (2) `HeapTable.OwnerConnection` is strong and `Simulation.GlobalTempTables` holds the table — pins any `##temp` owner; (3) `Database.ActiveSnapshotTxs` holds the transaction, which strongly refs its connection — pins any open-snapshot session.
  Weakening `Simulation.Connections` alone accomplishes nothing because the resource *is* the pin.
  A correct fix must break all three cycles — cleanest via a one-way `SessionToken` indirection (resources reference a lightweight token identity; the connection references the token, not vice versa) plus a finalizer that enqueues a **deferred teardown** drained on a normal worker thread (next `CreateDbConnection` / version-store GC) so transaction rollback stays off the finalizer thread.
  This is a broad, mechanical owner-indirection refactor landing on the most regression-sensitive subsystem (lock manager × GC timing × threading).
  Payoff is bounded (EF disposes scrupulously; only buggy consumer code leaks), so it's **deliberately deferred** as high-risk / low-frequency.
  Eventual home: [`locking.md`](locking.md).
- **MVCC history keeps one version per UPDATE, not one per committed transaction** — real collapses intra-transaction intermediate states so only the pre- and post-transaction states are visible.
  Visibility matches for the common single-UPDATE-per-transaction case; a snapshot landing between two UPDATEs of one transaction sees a state real never exposes.
  See [`locking.md`](locking.md#known-mvcc-limitations).
- **`Chinese_PRC_CI_AS` ORDER BY parity** — 13 of 18 adjacent pairs consistent with real, diverging two ways: `zh-CN` ranks CJK *before* Latin where real ranks it after, and it picks the other reading for polyphonic characters (real reads 重 as *zhòng*, 长 as *cháng*).
  Closing it needs a per-character primary-rank table like the default collation's byte-exact body, because the rank is interleaved rather than layered — three cheaper approximations were tried and verified to fail.
  `Turkish_CI_AS` (30/30) and `Japanese_XJIS_140_CI_AS` (27/28, the lone miss being half-width prolonged-sound-mark folding) need nothing; equality, CI/CS / KS / WS folding, grouping and LIKE align everywhere.
  Scores are tie-robust — a position-by-position diff miscounts unspecified CI tie order as divergence, which is what the earlier, larger-looking numbers were measuring.
  See [`collations.md`](collations.md#locale-comparer-sort-parity-gap).
- **Workload-harness divergence reporting quirks** (`.vs/workload/Program.cs`, local-only) — the parity report's example line rebuilds parameters from the op seed and can mismatch the actual divergent instance, and divergent instances aren't re-run single-threaded to classify transient-vs-stable.
  Both made the shared-plan-state hunt slower than it needed to be (the fixed bug class itself — instance-bound aggregate/window results, baked TOP/OFFSET counts, frozen RAND, unstamped replay clock — is documented in [`plan-cache.md`](plan-cache.md)'s shared-plan contract section).
- **`OBJECT_ID`'s type filter rejects a trailing space** — real compares the argument against a `char(2)` code under ANSI padding, so `OBJECT_ID('t', 'U ')` and `OBJECT_ID('fk', 'F ')` both resolve (probe-confirmed; a *leading* space is still NULL), where the simulator's `BuiltInToken.EqualsAny` treats `'U '` as no match.
  The code's own comment records the opposite, so the fix is a trailing-space trim plus the comment.
  See [`schemas.md`](schemas.md#object-identifiers--object_id).
- **`sys.database_permissions` has no seed rows** — a fresh real database already carries the grants every database starts with (`public` holding `CONNECT`, `VIEW ANY COLUMN` and `VIEW ANY DEFINITION`, a per-user `CONNECT`, and the `SELECT` grants on the system objects); the simulator's view projects only what `GRANT` / `DENY` explicitly added, so a tool that reads the starting grant set sees an empty one.
  Large: the seed set is per-principal and per-system-object, and `HAS_PERMS_BY_NAME` / the enforcement walk would have to agree with it.
  See [`permissions.md`](permissions.md).

## Live-but-untested surfaces

Measured 2026-07-30 (`dotnet test --collect:"XPlat Code Coverage"` → reportgenerator; 90.7% line / 79.2% branch / 92.2% method).
These are reachable code paths no test exercises — not gaps in behavior, gaps in the safety net.
Of the ~990 lines in fully-uncovered methods, ~376 are `DebugDisplay` / `ToString` debugger helpers and ~48 are `Stream` boilerplate overrides, both of which are deliberately not worth testing; what remains is this list.

- **`sp_cursorprepare`** (`TdsSession.CursorPrepare`) — SqlClient issues the `sp_cursoropen` family instead, so reaching it needs a driver that prepares a cursor explicitly.
- **The foreign-key scan fallback** (`FkTuplesMatch` + `EnumerateChildRows`) — **structurally unreachable**, not merely untested: it runs only when an FK column has no storage slot, which is true only of a non-persisted computed column, and the simulator rejects one in a FOREIGN KEY the way real does (Msg 1764 from the table-level and ALTER forms, Msg 8183 from the inline one — see [`foreign-keys.md`](foreign-keys.md#computed-columns-in-a-foreign-key)).
  A PERSISTED computed column is accepted and does have a slot, so it takes the seek path.
  Left in place as a guard on the storage layout rather than deleted.
- **`ClrAssemblyMetadata.ComputePublicKeyToken` / `DescribeReference`** — strong-named assembly identity and the assembly-reference description.

Covered since the first measurement, each of which turned out to be hiding a behavior bug rather than just a missing test: `SqlBulkCopy` / TVP rows carrying the `time` / `smalldatetime` / `datetimeoffset` / ANSI-string / `xml` families (which is where the xml byte-order-mark strip surfaced), the BCP temporal family (DacFx writes `time` / `datetime2` / `datetimeoffset` at maximum width scaled to 7 digits, and `datetimeoffset` in UTC — reading them per-precision failed the entire table's data file, invisible while only precision-7 fixtures existed), `INFORMATION_SCHEMA.PARAMETERS` (no scalar-function return row, wrong `CHARACTER_MAXIMUM_LENGTH` rule, `sysname` not resolved to its base type), the DYNAMIC cursor's scroll directions (`DECLARE … CURSOR DYNAMIC` was treated as forward-only, `RELATIVE` was rejected outright, and the forward-only rejection used Msg 16925's wording instead of Msg 16911's), and the public `SimulatedDbParameterCollection` indexers plus `SimulatedSqlResultSet.HasRows`.

Worth re-measuring after a large bundle rather than routinely, and worth acting on when it does run: **nothing this pass surfaced was merely an untested-but-correct path.**
It found two pieces of dead code — a duplicated MERGE type resolver, and an unreachable ON-UPDATE-CASCADE branch whose stub threw an exception saying so — and every gap that was then covered turned out to be hiding a behavior bug.
Uncovered code here has consistently meant *wrong* code, not just unwatched code.

## Design choices to revisit

Shipped intentionally and correct under their documented contract, but the original rationale may have aged.
Worth a look before re-affirming or changing.
(Rationale lives in [`scalars.md`](scalars.md)'s divergence notes and CLAUDE.md's Quirks.)

- **APPROX_COUNT_DISTINCT** implemented as exact `COUNT(DISTINCT)`.
  Original rationale: same semantic guarantee, no HyperLogLog dependency.
  Review: is the perf gap visible against in-process workloads?
  If not, the simpler form stays defensible.
- **CHECKSUM_AGG** uses an order-independent XOR fold.
  Rationale: same-multiset-same-checksum preserved, bit-identical wasn't required.
  Review: has any consumer needed bit-identical checksums (e.g. replication-comparison parity)?
- **`float` CAST/CONVERT** text formatting uses .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific.
  Rationale: .NET formatting is the default; the specific format wasn't a fidelity-oracle requirement.
  Review: do users hit float-as-string comparisons in real workloads?
- **`decimal` / `numeric`** backed by .NET `decimal`; values needing more than 28 significant digits aren't modeled (declarations through `decimal(38, *)` accepted so storage byte-width matches).
  Rationale: .NET decimal is the simplest path.
  Review: do real schemas use the high-precision range, or is 28 sig digits enough in practice?

## Won't-model / explicitly excluded

Excluded on **correctness**, not priority: these are cloud-only surfaces the SQL Server 2025 RTM box product itself rejects, so modeling them would *diverge* from the box-product fidelity oracle.
Don't re-surface as candidates (unless a future box release promotes one).

- **ANY_VALUE(expr)** — Azure/Fabric-only, not in the box product (probe-confirmed).
  With it excluded, the **Analytic** category is complete for the box product (CUME_DIST / PERCENT_RANK / PERCENTILE_CONT / PERCENTILE_DISC all ship).
- **SESSION_ID()** — dedicated-SQL-pool / cloud surface; the box raises Msg 195 (probe-confirmed).
  `@@SPID` is the box session-id mechanism.
