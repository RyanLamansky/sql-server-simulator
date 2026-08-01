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
- **XML mutation and XQuery beyond the path subset** — `.modify()` XML-DML plus its `UPDATE … SET` integration, FLWOR / comparison / boolean / arithmetic operators, value predicates, constructors, XSD validation against `xml(collection)` bindings, `ALTER XML SCHEMA COLLECTION ADD` → [`xml.md`](xml.md#known-gaps).
- **Cursors over a deferred source** — a cursor whose FROM reaches a derived table, view, CTE or APPLY right side is forced STATIC where real is DYNAMIC, costing mid-loop change visibility, `@@CURSOR_ROWS = -1`, and positioned DML (real allows `WHERE CURRENT OF` naming the *view*, enforcing its CHECK OPTION, and Msg 16933 when the statement names the base table under it instead).
  JOIN / comma-FROM / self-join cursors ship — those fold live per FETCH over the per-source stable addresses; the deferred shapes need that same identity threaded out of a `LateralPlan`, which yields projected bytes carrying no heap address → [`cursors.md`](cursors.md).
  Two smaller neighbours of the same gap: a `TOP` / `OFFSET` cursor is forced STATIC where real is KEYSET, and a `FOR SYSTEM_TIME` cursor is forced STATIC (correct rowset, no sensitivity).
- **Temporal history-table indexing** — real gives every history table a clustered index on `(period end, period start)` and requires one before it accepts a finite `HISTORY_RETENTION_PERIOD` (Msg 13765); the simulator's history sibling is a plain heap, so it accepts retention unconditionally and can't raise that.
  Retention filtering, auto-named history tables, and the base-vs-history shape validation all ship → [`temporal-tables.md`](temporal-tables.md#divergences).
- **Cross-database permission and snapshot-read scoping** — three-part-name writes ship, but two per-database surfaces still read the *session's* database: the permission check (a write through `other.dbo.t` tests the session principal's grants, where real resolves the login's user in the target) and a SNAPSHOT / RCSI reader's snapshot stamp (so cross-database versioned reads compare stamps from two independent counters).
  The write side is self-consistent — rowversion, version-store commit ids and trigger dispatch all follow the target → [`schemas.md`](schemas.md#cross-database-writes).
- **Key-range locks** — the one unbuilt piece of the locking model; HOLDLOCK widens to table-S in their place → [`locking.md`](locking.md).
- **`sys.sql_expression_dependencies` / `sys.dm_sql_referencing_entities` / `sys.dm_sql_referenced_entities`** — neither the catalog view nor the two DMVs resolve, so a tool asking "what depends on this" gets Msg 208.
  The [schema-binding gate](programmable.md#schema-binding-with-schemabinding) now derives a reference set from a module body, but only for schema-bound modules and only name-approximately at column granularity, where these surfaces want dependency rows for **every** module (real records them for plain views and procedures too, resolving late-bound names when it can) down to `referenced_minor_id` per column — probed shape: one row per (referencing module, referenced object) plus one per referenced column, carrying `is_schema_bound_reference` / `referenced_class_desc` / `is_caller_dependent` / `is_ambiguous`.
  Building it means capturing column bindings at parse time, which the per-row name-keyed resolver doesn't do today; that capture is the real cost and would also sharpen the schema-binding gate's column granularity.
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

**`FOR JSON` ships** — PATH (fully, incl. dotted-alias nesting + all four options), AUTO including join-nesting, the probed value-formatting/escaping table, raw-embedding of nested FOR JSON / JSON_QUERY, Msg 13600 / 13601 / 13605 / 13620.
See [`json.md`](json.md#for-json-result-serialization).

**`FOR XML` ships** — RAW / AUTO / PATH (fully: `@attr` / element / `parent/child` nesting / `text()` / `data()` / unnamed-as-text / `PATH('')` row-tag omission / same-name concatenation), the `ELEMENTS [XSINIL|ABSENT]`, `TYPE` and `ROOT[('name')]` options, the probed value-formatting (bit → `1`/`0`, scientific float, ISO dates, base64 binary, uppercase GUID) + position-dependent escaping table, NULL handling, the typed-vs-untyped result column and its empty-rowset asymmetry, node-embedding of every `xml`-typed column, and Msg 6800 / 6809 / 6851 / 6864 / 6852 / 6861 / 6829 / 6830.
AUTO's join-nesting heuristics (level order, computed-column placement, consecutive-row collapse) are tabulated in [`xml.md`](xml.md#auto-nesting-shared-with-for-json-auto) and shared with FOR JSON AUTO.
See [`xml.md`](xml.md#for-xml-result-serialization).

Not built yet within them:
- **EXPLICIT mode** — the universal-table format; complex, rarely hand-authored.
- **`BINARY BASE64`/`HEX`, `XMLSCHEMA`, `WITH NAMESPACES`** options, and the exotic PATH node functions beyond `text()`/`data()` (`comment()`, `processing-instruction()`, `node()`, `*`, `@*`) — each raises `NotSupportedException` naming the feature.
- **AUTO over a set-operation result** — `NotSupportedException`; real names every element after the first branch's table.
- **One-row chunking** — real chunks the string across ~2033-char rows; the simulator returns it whole (shared by both clauses).
- **Msg 6819** — real rejects `FOR XML` inside an `INSERT … SELECT`; the simulator accepts it.
- **XML-name encoding** — RAW / AUTO escape a name that isn't a legal XML identifier as `_xHHHH_` (`[a b]` → `a_x0020_b`, `FROM #tmp` → `<_x0023_tmp>`), and PATH rejects one with **Msg 6850**; the simulator emits names verbatim, so an odd identifier yields ill-formed XML.
  The probed encoding table is in [`xml.md`](xml.md#not-modeled-yet) — a self-contained next step.

### Built-in functions

Captured from a Microsoft Learn category-by-category audit (cross-checked against `Parser/Expression.cs::ResolveBuiltIn`, `Parser/AtAtKeyword.cs` + `Value.cs`, `Parser/Expressions/AggregateExpression.cs`, `Parser/Expressions/WindowExpression.cs`, and the FROM-source rowset dispatch in `Parser/Selection.{OpenJson,StringSplit,ListExtendedProperty}.cs`).
Re-fetch <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the function surface complete. 🎯 marks an item whose completion closes a Microsoft category.

Blocked on a larger unmodeled parent feature (shipping a function here implies the parent ships too):

- **Graph** (node/edge tables) — EDGE_ID_FROM_PARTS / GRAPH_ID_FROM_EDGE_ID / GRAPH_ID_FROM_NODE_ID / NODE_ID_FROM_PARTS / OBJECT_ID_FROM_EDGE_ID / OBJECT_ID_FROM_NODE_ID.
- **Change tracking** — CHANGETABLE(CHANGES …) / CHANGETABLE(VERSION …).
- **Partitioning** — `$PARTITION.partition_function_name(value)`.
- **CLR procedures / TVFs / aggregates / UDTs** — CLR *scalar functions* ship (see [`clr-assemblies.md`](clr-assemblies.md)); the rest reference `Microsoft.SqlServer.Server.SqlContext` / `SqlPipe` / `SqlDataRecord` / `SqlMetaData`, which lived in .NET Framework's `System.Data.dll` and are absent from .NET's facade, so they need a substitute `System.Data` injected into the load context that type-forwards `SqlTypes` onward and supplies the missing namespace. That shim is the whole cost; scalar functions needed none, which is why they shipped first.
- **ML scoring** (PREDICT surface not modeled) — PREDICT(MODEL = …, DATA = …).
- **Ad-hoc data sources** — OPENROWSET (file/bulk + provider rowsets); OPENDATASOURCE (the inline four-part-name form; `OPENQUERY` ships — see [`linked-servers.md`](linked-servers.md)); OPENXML (pre-`OPENJSON` XML rowset, still hit in legacy code).
  Probed: real *parses* `OPENROWSET('MSDASQL', …)` then errors on disabled ad-hoc access (**Msg 7222**) and `OPENROWSET(BULK 'file', SINGLE_CLOB)` on the missing file (**Msg 4860**); the simulator doesn't parse the FROM-source form at all (Msg 102). Ad-hoc / external data access is a feature, not a syntax tweak — the parse-then-runtime-error shape depends on the whole external-data model.

- **System stored procedures** (`sp_*` family) — formatted-metadata / management procs invoked via `EXEC sp_name`.
  Shipped so far: the `sp_help` family (`sp_help` / `sp_helptext` / `sp_helpindex` / `sp_helpconstraint` / `sp_helpdb` / `sp_helpfile` / `sp_helpstats` / `sp_helprotect` / `sp_helptrigger` / `sp_helpuser`), the ODBC/JDBC catalog set (`sp_tables` / `sp_columns_100` / `sp_pkeys` / `sp_statistics_100` / `sp_stored_procedures` / `sp_datatype_info_100`), `sp_spaceused`, `sp_who` / `sp_who2`, `sp_MSforeachtable` / `sp_MSforeachdb`, `sp_rename` and `sp_configure` — see [`catalog-views.md`](catalog-views.md).
  Still unregistered → **Msg 2812** ("Could not find stored procedure '…'."): `sp_depends` (wants the dependency graph), `sp_MSforeach_worker` (the two `sp_MSforeach*` procs materialize their name lists rather than driving the global cursor it consumes), the `sp_add*` management family.
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

- **`CONCAT(a, b)` doesn't resolve its operands' collations** — real raises **Msg 451**, a message of its own distinct from `+`'s Msg 457: `Cannot resolve collation conflict between "R" and "L" in concat operator occurring in SELECT statement column 1.` (no leading *the*, and it names the projection ordinal the expression node doesn't know).
  The variadic form silently picks a collation; `+` and `||` both bind.
  → [`collations.md`](collations.md#known-gaps).
- **Statement-permission gates stop at the modeled set** — CREATE TABLE / VIEW / PROCEDURE / FUNCTION / SEQUENCE / ROLE / USER / SCHEMA, ALTER TABLE, DROP TABLE and DROP USER are checked; other CREATE / ALTER / DROP statements run unchecked, as does `ALTER` / `CREATE OR ALTER` of an existing module.
  → [`permissions.md`](permissions.md#known-gaps).
- **Non-Framework CLR assemblies load** — real resolves every `AssemblyRef` against a fixed .NET Framework catalog and raises **Msg 6503** otherwise (probe-confirmed for .NET 10 and for .NET Standard 2.0); the simulator runs on .NET so all of them bind, which is also what lets the tests emit a fixture assembly without a Framework toolchain.
  → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **An aggregate whose only column reference doesn't resolve locally isn't bound** — `HAVING MAX(nosuchcol) = 1` is taken for an [aggregate over an enclosing query](#unbuilt-feature-areas), so it raises `NotSupportedException`, which a module bind swallows rather than refusing a module real accepts — real reports **Msg 207 at CREATE**.
  The rest of that family closed: `WHERE` / `HAVING` / a `MERGE`'s `ON` / the value side of a `SET` bind through the static type path, carrying the collation and legacy-LOB rules with them.
  Capturing (source, ordinal) bindings at parse rather than re-resolving by name is still what [`sys.sql_expression_dependencies`](#unbuilt-feature-areas) wants.
  → [`programmable.md`](programmable.md#divergences), [`collations.md`](collations.md#compile-time-binding).
- **A module body reports one binder error, not all of them** — real emits every Msg 207 the body contains (probed: two statements, two errors) before refusing the CREATE; the simulator throws on the first.
  Worth pairing with a wider "collect rather than throw" pass if one is ever attempted; on its own it changes only how much a developer sees per round trip.
  → [`programmable.md`](programmable.md#divergences).
- **Malformed JSON input swallowed on the lax paths** — real raises **Msg 13609** ("JSON text is not properly formatted. Unexpected character '<c>' is found at position <n>.") whenever the *document* argument doesn't parse, regardless of the path's lax/strict prefix, and it counts a root-level JSON scalar (`'1'`, `'"abc"'`) as not parsing; the simulator answers NULL (0 for `JSON_PATH_EXISTS`) under lax across `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_PATH_EXISTS`.
  Closing it needs the position-bearing message text as well as the object-or-array root rule; `ISJSON` already matches.
  → [`json.md`](json.md).
- **A module body's shape rules stay unchecked** — **Msg 455** (a function's last statement must be `RETURN`), **Msg 444** (a body `SELECT` returning to the client) and **Msg 443** (a side-effecting operator inside a function) are all CREATE-time on real and absent here, so a function real refuses is created.
  These want body-shape analysis rather than a parse, which is why the body bind didn't pick them up; Msg 178's companion rule does ship.
  → [`programmable.md`](programmable.md#multi-statement-table-valued-functions).
Tracked elsewhere: the recursive-CTE construct restrictions (Msg 460 / 461 / 462 / 467) now ship — see [`ctes.md`](ctes.md#recursive-member-restrictions).
Integer-literal typing and the Msg 8116 id / style argument gates that depend on it now ship too — see [`arithmetic.md`](arithmetic.md#integer-literals-past-ints-range-type-numericdigit_count-0) and [`scalars.md`](scalars.md#gated-argument-slots).
So does compile-time binding of predicates: a cross-collation comparison / unification (Msg 468 / 457), a legacy-LOB string-scalar argument (Msg 8116) and an unknown column (Msg 207) now all report over an **empty** rowset and at CREATE of a module — see [`collations.md`](collations.md#compile-time-binding).

## Fidelity gaps in shipped behavior

Real bugs / limitations against shipped behavior — fixes are concrete work, not design decisions.

- **The six non-`QUOTED_IDENTIFIER` components of the Msg 1934 SET-option gate** — real also requires `ANSI_NULLS` / `ANSI_PADDING` / `ANSI_WARNINGS` / `ARITHABORT` / `CONCAT_NULL_YIELDS_NULL` ON and `NUMERIC_ROUNDABORT` OFF, listing every offending name comma-separated in one message; the QI component ships alone.
  Each already has a session field on `SimulatedDbConnection`, so the work is collecting names in `RejectIncorrectSetOptionsForWrite` rather than new plumbing.
  Low urgency: the four ON-by-default ones are only turned off deliberately, and the two OFF-by-default ones already match what the gate wants.
  See [`grammar.md`](grammar.md#set-option-gates--msg-1934--msg-1935).
- **`SELECT … WITH (NOEXPAND)` over an indexed view under `QUOTED_IDENTIFIER OFF`** — real raises Msg 1934; the simulator accepts it, because `NOEXPAND` parses into the table-hint accept-list with no field on `Selection.TableHintInfo` to carry it to the gate.
  The rest of the Msg 1934 matrix ships.
- **Creation-time `QUOTED_IDENTIFIER` capture on CHECK / DEFAULT constraints** — real answers `OBJECTPROPERTY(<constraint>, 'IsQuotedIdentOn')` with the capture (0 for one created under OFF); the simulator's `ObjectProperty.FindObject` doesn't resolve constraint object ids at all, so the property answers NULL.
  Metadata-only — constraint definitions are stored normalized at CREATE and never re-parsed, so nothing behavioral rides on it.
  Module and table capture ship — see [`grammar.md`](grammar.md#per-object-creation-time-capture).
- **`ALTER TABLE … ADD c AS <expr> PERSISTED` as a batch's final token** raises Msg 102 near `PERSISTED` — `ParseComputedSuffix` advances with `MoveNextRequired` after the keyword, so the form needs a following token; a trailing `;` or another column in the list parses fine.
- **`GROUP BY '<literal>'` binds before the trailing token parses** — the simulator raises Msg 164 on the constant grouping term where real parses the whole clause first and reports Msg 102 at the stray token after it (`group by 'a' 'b'`).
  An ordering divergence between binding and parsing, surfaced while probing the Msg 102 token-rendering fix; the `group by c 'b'` shape agrees on both sides.
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
- **`OBJECTPROPERTY(id, 'IsDeterministic')` and the `CAST` / `CONVERT` style rule** — the module walk ships (schema-binding precondition, nondeterministic-built-in table, transitive module references — see [`catalog-views.md`](catalog-views.md#isdeterministic)), but it classifies conversions between a date/time type and a character string as deterministic where real keys off the style argument.
  Closing it needs the conversion's source and target types, which the token-level body scan doesn't carry; the probed style table is recorded in `catalog-views.md` for whoever picks it up.
- **`COUNT(*) OVER (… ROWS …)` without ORDER BY** — real accepts a frame with no ordering for `COUNT(*)` alone and applies it (probe-confirmed 2026-08-01: `COUNT(*) OVER (PARTITION BY g ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)` runs, while `COUNT(v)` / `SUM(v)` / `MIN(v)` in the same shape raise Msg 10756); the simulator raises Msg 10756 for the whole family.
  An unexplained real-side exemption rather than a rule — closing it means special-casing the star operand in `ParseOptionalFrameSpec`'s ORDER BY gate.
  See [`query.md`](query.md).
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

- **Msg 3729 echoes a normalized name for an unqualified DROP** — `drop table sbt` reports `'dbo.sbt'` where real echoes `'sbt'` as written; the schema-binding gate renders the resolved two-part name rather than the statement's spelling.

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
