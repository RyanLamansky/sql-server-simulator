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

- **Full-text linguistic residue** — the query pipeline ships: the word breaker, the English stoplist, the inflectional stemmer, the whole `contains_search_condition` grammar, `FREETEXT`, and both `CONTAINSTABLE` / `FREETEXTTABLE` rowsets (see [`full-text.md`](full-text.md#the-query-pipeline)).
  What remains is real's *language components*, which are lexicons rather than rules: the word breaker's own token list (real keeps `.net`, `c#` and `at&t` whole and emits normalized `nn` / `dd` forms beside numbers and dates), the **thesaurus** (`FORMSOF(THESAURUS, …)` matches only the written word, which is what real's shipped empty files give — a populated one would diverge), **languages other than English** (every LCID is broken and stemmed by the English rules), and the last stemming shape a single-stem model can't hold — a surface form spanning two lemmas, where real expands `leaves` to `leaf` *and* `leave` and the simulator picks one.
  Also unbuilt: `SEMANTIC*` rowsets (`NotSupportedException`), and `RANK` values, which are the simulator's own (see the divergence note in [`full-text.md`](full-text.md#rank)).
- **Spatial evaluation** — the value model, **all three measures for both spatial types** and the **whole topological surface of both** all ship: `geometry`'s eight predicates plus `STRelate`'s DE-9IM matrix, `geography`'s six over a round-earth engine of the same shape, `STIsValid` for each, and the Msg 24144 gate an invalid instance puts on most instance methods (see [`spatial.md`](spatial.md#topological-predicates-the-de-9im-engine) and [round-earth topology](spatial.md#round-earth-topology-geographys-predicates)).
  The round-earth measures work along the *great elliptic arc*, which is the curve real uses and not the geodesic — length, area and the closest approach between any two shapes (see [`spatial.md`](spatial.md#round-earth-measures-the-great-elliptic-arc) for the derivations and the residuals against real). What remains:
  The derived-point members each type carries alone ship too — `geometry`'s `STCentroid` / `STPointOnSurface` / `STIsSimple` and `geography`'s `EnvelopeAngle` / `EnvelopeCenter` — as does the **property form of a spatial column** (`Location.Lat`), decided against the query scope with real's Msg 326 where both readings bind.
  What remains:
  the **constructive operations** (`STUnion` / `STIntersection` / `STDifference` / `STSymDifference` / `STBuffer` / `STConvexHull` / `STBoundary` / `STEnvelope` / `MakeValid` / `Reduce` / …), which want polygon clipping and are the largest piece left;
  the **lobe split for planar validity** — real accepts a ring that revisits one of its own vertices when the second lobe nests inside the first with the opposite winding, which the round-earth validator reproduces and `SpatialValidator` does not, so the two disagree on such a ring (one WideWorldImporters border carries the arrangement);
  and a **`geography` `STRelate`** oracle — the round-earth engine computes the nine cells but real exposes no `STRelate` there, so only the six predicate masks are checked against a reference.
  Also open: `STPointOnSurface`'s pick for a polygon **with a hole** (real bridges the hole into the ring before clipping the ear; the simulator falls back to a scanline point, which is on the surface but not real's), the property form at a **scope-less site** (an UPDATE's SET list, a CHECK constraint, a computed column), curved shapes and FULLGLOBE, GML, SRID transformation, `sys.spatial_reference_systems` seed rows, `ALTER SPATIAL INDEX`, and query-planner use of the spatial index → [`spatial.md`](spatial.md#not-modeled-yet).
  One metadata cell surfaced during the round-earth predicate probes and left unbuilt: `MinDbCompatibilityLevel()` answers **110** for an *invalid* instance on real (100 for a valid one) where the simulator answers 100 always.
- **XQuery beyond the expression subset** — the computed `attribute name {…}` and `text {…}` constructors in a *read* method (both ship in `.modify()`'s insert content, and the computed `element name {…}` ships in both), the direct comment / processing-instruction forms, `sql:variable()` / `sql:column()` accessors outside `.modify()`'s value terms, the `xs:` constructor functions, and named axis steps (`child::` / `descendant::` …).
  Predicates, the comparison / boolean / arithmetic operators, the function library, FLWOR / quantified / conditional expressions and the direct element constructor ship — see [`xml.md`](xml.md#the-xquery-subset) for the catalog.
  Every gap is reached by `.modify()` too, since the mutator's paths run through the same evaluator; its insert *content* is a separate sublanguage, so a `{…}` there still takes only literals and the `sql:` accessors → [`xml.md`](xml.md#not-modeled-yet).
- **XSD validation against `xml(collection)` bindings** — the collection's XSD is stored verbatim and never parsed, so nothing validates an INSERT, an UPDATE, or a `.modify()` edit, and a typed instance's paths carry untyped static types.
  That is what real's Msg 6923 (a validation failure after an edit) and Msg 2247 (a `with` value that isn't a subtype of the schema type) report, and what makes `replace value of` legal against a *typed* element rather than only its `text()` node.
  `ALTER XML SCHEMA COLLECTION ADD` sits behind the same missing parse → [`xml.md`](xml.md#known-gaps).
- **Fragment residue** — the value model admits a fragment everywhere the evaluator reads or edits one, but the *storage* path still keeps an `xml` payload's text verbatim, so a `CAST` doesn't drop the insignificant top-level whitespace and the XML declaration real drops (`CAST('  <a/>  ' AS xml)` is real's `<a/>` and the simulator's `  <a/>  `) — the normalization happens only on the `.modify()` round trip.
  A single-root instance also keeps the **document-element** context item for a relative path (`@x.query('a')` over `<r><a/></r>` selects the `a` where real, whose context is the document node, selects nothing), which is what makes a `.nodes()` row — re-serialized and re-parsed as its own instance — resolve a relative read; a fragment already uses real's own root-node context.
  Closing it wants a node-reference value for a `.nodes()` row rather than a re-parse → [`xml.md`](xml.md#the-value-model-documents-and-fragments).
- **A KEYSET cursor's identity over a clustered-only table** rides the row's stable heap address, where real keys on the clustered key plus its uniquifier — so an UPDATE moving that key re-fetches with status `0` and the new values instead of real's `@@FETCH_STATUS = -2`.
  The row-locator gate that converts a cursor over a table carrying *no* locator ships, and a PK / UNIQUE table already tracks by its key; what's left is the middle case, which would need a uniquifier equivalent on the clustered-index path → [`cursors.md`](cursors.md#the-keyset-row-locator).
  Cursor sensitivity otherwise matches shape for shape: row limiting, an ORDER BY no index delivers, deferred sources, temporal sources and the row-locator conversion all behave the way real behaves.
- **`TRUSTWORTHY`'s authenticator rule** — the flag is modeled and the crossing it widens ships, but real also requires the source database's *owner* to hold `AUTHENTICATE` in the target (probed as the exact line: a `sa`-owned source qualifies through `dbo`; an owner with no user there, or one whose user lacks `AUTHENTICATE`, is refused).
  Every simulated database is dbo-owned and there is no `ALTER AUTHORIZATION ON DATABASE` surface, so the refusing halves aren't reachable; a database-owner model would bring them in, and would also give `DB_CHAINING` its owner-match half → [`permissions.md`](permissions.md#cross-database-references).
- **Key-range coverage past a sargable predicate on a leading key prefix** — key ranges ship for the shapes that carry `=` / `>` / `<` / `BETWEEN` / `IN` bounds on a **leading prefix** of some key or index, single-column and composite alike, in `RangeS-S` / `RangeS-U` / `RangeX-X` as the hints name.
  Everything else a SERIALIZABLE / HOLDLOCK reader can be — a whole-table scan, a predicate on an unindexed or non-leading column, an `ORDER BY`-eliminated ordered scan, a view / multi-source / derived-table source — still takes the whole-table S, which is what real degenerates to for the unindexed cases and equivalent to what it does for the scans → [`locking.md`](locking.md#key-range-locks).
- **A SERIALIZABLE writer takes no fence of its own** — probed, real converts an `UPDATE` / `DELETE`'s key locks to `RangeX-X` under SERIALIZABLE, where the simulator's writer path takes table-IX plus row-X whatever the isolation level.
  The rows the writer touches are locked and the gaps between them are not, so a concurrent insert into the range the writer's own WHERE named goes through.
  The reader-side machinery is all there; what's missing is running the writer's WHERE conjuncts through `ComputeSerializableKeyRange` at the UPDATE / DELETE / MERGE target sites → [`locking.md`](locking.md#key-range-locks).
- **Range-versus-range conflicts need identical intervals** — ranges intern per interval, so two readers fencing overlapping-but-different intervals take different resources and never test each other's mode.
  Containment is tested on the write path only.
  Closing it means either interning coarser or having the reader walk held ranges the way `ProbeKeyRangesForWrite` does → [`locking.md`](locking.md#divergences).
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

### sqllogictest differential sweep — surfaced gaps

SQLite's sqllogictest corpus (7,195,342 query and 225,371 statement records across 622 `.test` scripts, cross-validated against several engines including SQL Server 2005 circa 2008, with per-engine `skipif mssql` / `onlyif mssql` directives) is staged at `.vs/sqllogictest/` (gitignored; provenance and re-download URL in its `README-provenance.md`).
It is used in **differential mode**: a C# runner (`.vs/sqllogictest/runner/`, gitignored, usage in its `RUNNING.txt`) replays each record against the simulator in-process and the live local SQL Server 2025 side by side and diffs both directions.
The stored expected results are 2008-era SQLite canonicalizations and are **not** the oracle — real is; the file tally is corroboration only (232,935 records where all three agree, 7 where both engines agree with each other and differ from the file).

A pilot over 45 scripts (`select1..5`, all 12 `evidence/`, and a seeded sample of `index/` and `random/` recorded in `runner/pilot-phase2.txt`) covered 315,592 records at ~520 records/second, against a baseline of 206,380 exact and 26,560 order-insensitive matches.
`select1..4` and every `index/` category ran clean; nearly every root below came from `random/expr` and `random/aggregates`, so a full sweep should be **weighted toward those 250 files** (~1 hour) rather than spread evenly over the ~4-6 hours the whole corpus costs.

Two harness lessons worth carrying to any future differential runner, since both silently fabricate or inflate findings:
a swallowed simulator exception reports as a *clean run*, which manufactures entries in the over-permissive class specifically (the simulator materializes a row-returning statement's error on the first `Read` rather than at `ExecuteReader`, so a loop that skips `Read` when `FieldCount == 0` never sees it);
and a `statement` record that errors on both engines can still have applied a different prefix of its batch to each, so state divergence has to taint the rest of the script or every later mismatch reads as an independent wrong-answer bug.

Roots surfaced (each reproduced minimally from a clean state, against both engines):

- **One trailing garbage identifier after a completed clause is swallowed**: `SELECT a FROM t zzz qqq`, and the same after `WHERE` / `GROUP BY` / `ORDER BY` / a comma-FROM list, return rows where real raises Msg 102 at the second identifier.
  Exactly one extra identifier is consumed, and the no-`FROM` path is already tight (`SELECT 1 zzz qqq` is Msg 102 on both).
  This is the identifier half of the trailing-token rule [`grammar.md`](grammar.md) records as narrowed to value literals — now with concrete shapes.
- **`DbDataReader.RecordsAffected` counts rows returned** rather than rows affected: 0 for an INSERT that affected one row, and the row count for a SELECT where SqlClient answers -1.
  `ExecuteNonQuery` matches real exactly (-1 for DDL, N for DML), so only the reader path diverges — see [`data-reader.md`](data-reader.md).
- **Real folds a comparison against the NULL literal at compile time and never evaluates the other operand**, while the simulator evaluates eagerly: `WHERE NULL > a*a*a*a*79` is 0 rows on real and Msg 8115 here, and `HAVING NULL <> b` is 0 rows on real and Msg 8121 here — real skips even the *binding* check.
  A constant-false conjunct written first (`WHERE 1 = 0 AND a/0 > 1`) agrees on both.
  Whether to model optimizer-visible short-circuiting is a judgement call, not just a fix.
- **Msg 102 where real raises Msg 156 naming the keyword**, at a residue of sites (`NOT`, `NULL`, `INTO`, `OR`, `UPDATE`, `DELETE`, `INSERT`); the simulator already reports Msg 156 correctly elsewhere, so this is site-specific rather than a missing error.
  Also `DROP INDEX <1-part>` is real's Msg 159, the simulator's Msg 102.
- **Many-way joins do not scale**: `select5`'s 20-24-table equi-joins answer in milliseconds on real and exceed a 15-second `CommandTimeout` here, one of them running past a 40-second wall without honoring its own timeout.
  Not a correctness gap, but it consumed roughly three quarters of the pilot's wall clock — see the join-strategy notes in [`joins.md`](joins.md).

Closing those five surfaced further divergences, none of them touched, each probe-confirmed against SQL Server 2025 (2026-08-03):

- **A `FROM`-less star is three behaviors real distinguishes and the simulator answers Msg 102 for all**: `SELECT *`, `SELECT 1, *` and `SELECT COUNT(*), *` are **Msg 263** ("Must specify table to select from."), `SELECT t.*` is **Msg 107**, and `EXISTS (SELECT *)` is legal.
- **`<binary> <operator> <approximate>`** (`0x02 + CAST(2 AS real)`) is real's **Msg 206** in both operand orders for `+ - * /`; the simulator raises `NotSupportedException`.
- **`STDEV` / `VAR` over `money`** is `float` on real; the simulator raises **Msg 529**.
- **An integer literal padded past 12 characters is `numeric(significant_digits, 0)`**, not `int` — `SELECT 0000000000300` is `numeric(3, 0)` on real while the 11-character `00000000300` is `int`.
  `NULLIF` inherits it, so `NULLIF(0000000000300, 1)` is `numeric(3, 0)` on real and `smallint` here; the rule belongs to the bare-literal tokenizer, not to NULLIF — see [`arithmetic.md`](arithmetic.md).
- **Real answers a statement's binder errors together where the simulator raises the leading one alone** — `INSERT` reports 207 + 110, and 273 + 10709, as one multi-error response.
  The module-body bind already gathers every error of a *body*; this is the same shape for a single statement — see [`programmable.md`](programmable.md).

The sweep also produced a **data-loss repro for the parse-phase batch divergence** [`control-flow.md`](control-flow.md) already lists as accepted: `INSERT INTO t VALUES(3,'z'); SELECT ~~~ FROM;` leaves the row inserted here and rejects the whole batch on real (Msg 156, and Msg 159 for the `DROP INDEX` shape), so the accepted-divergence rationale — that real tooling never sends invalid batches — now has a measured cost in silent state divergence rather than only in error timing.
Runtime errors (Msg 208 deferred name, Msg 8134) correctly leave earlier statements applied on both, so the divergence is specifically parse-phase.

### Django ORM test-suite shakedown — surfaced gaps

Running Django 5.1's own ORM test apps over the wire (mssql-django 1.7 / pyodbc) against the endpoint is a high-yield real-application oracle (harness: the runner's own `test_*` database via real `CREATE`/`DROP DATABASE` — no configuration override needed since those ship — plus an incremental failing-SQL logger wrapping `mssql.base.CursorWrapper.execute`).
**Give the `other` alias a database of its own, not a `TEST MIRROR`**: a mirror aliases the same database, so Django's `MultiDbTests` write through one connection while the `TestCase`'s atomic block holds locks on the other and the two self-block forever — `order_with_respect_to.test_database_routing` and `prefetch_related.MultiDbTests` both hang, on real SQL Server exactly as on the simulator, so it is a harness artifact and not an oracle signal.
**The bar is parity with real, not absolute 100%**: many Django ORM tests fail on *real* SQL Server + mssql-django too (its own emulation limits), so the target is that the simulator fails exactly the tests real fails. Measured on a 20-app ORM slice (1021 tests): real fails 42, the simulator fails 43 — a **13-test sim-only delta** (the other 30 sim failures also fail on real). Compute the delta with `comm -23 <sorted sim FAIL/ERROR test names> <sorted real ones>`, not the raw sim count.

A `dbo.REGEXP_LIKE` built-in was **tried and reverted** — faking it as a built-in is a fidelity break, because on real the name resolves only when mssql-django's regex **CLR assembly** is installed. CLR scalar functions now ship, so the authentic path works: `EnableClr` + mssql-django's own `install_regex_clr` sequence loads `regex_clr.dll` and `dbo.REGEXP_LIKE(...)` evaluates (verified end-to-end against the real `regex_clr.dll`, with `clr_name` and MvID matching the live server byte-for-byte). See [`clr-assemblies.md`](clr-assemblies.md).

Re-measured 2026-07-29 on a 21-app ORM slice (**2069 tests**): **sim-only 0**, real-only 27, 74 failing on both.
The runner is `runtests.py --settings=<sim|real> --parallel=1 --noinput -v2 <apps>` against a `ListenLocalAsync` host, with the delta taken **both** ways.

**Widened 2026-08-02 to a 35-app slice weighted toward ORM SQL and schema emission** (`annotations backends bulk_create constraints custom_columns custom_lookups dates datetimes db_functions defer defer_regress distinct_on_fields expressions_case expressions_window field_defaults force_insert_update generic_relations indexes introspection m2m_through model_fields model_indexes nested_foreign_keys null_queries one_to_one order_with_respect_to pagination prefetch_related queryset_pickle select_for_update select_related signals transactions update update_only_fields`, **2402 tests**): sim-only **26**, real-only **23**, 49 failing on both.
Of the 26, 15 closed in the same pass; the rest are filed here.
`schema` (219 tests) was dropped from the measured slice for runtime — it is minutes-per-test on *both* sides over the wire, so it needs its own session rather than a place in a whole-slice run.
Run the two sides **one at a time**: two runners against one endpoint share the `test_*` database and wedge each other on locks, which reads exactly like a simulator blocking bug.

Roots **filed** (still open):

- **`DBCC CHECKIDENT` isn't parsed** → Msg 102 near `CHECKIDENT`, failing Django's `sql_flush` (`backends.base.test_operations.SqlFlushTests.test_execute_sql_flush_statements`, `backends.tests.LongNameTest.test_sequence_name_length_limits_flush`).
  The `RESEED` / `NORESEED` forms with `WITH NO_INFOMSGS`, and the informational row real prints, are the work.
- **A `decimal` beyond .NET `decimal`'s range surfaces as `SqlServerSimulator: unhandled OverflowException`** (Msg 50000) rather than a modeled error — `model_fields.test_decimalfield`'s `max_digits=38` model. The 28-significant-digit ceiling is the documented backing-type quirk; the *unhandled-exception* surface is the part worth closing.
- **Reverse delta (23 tests, one root): `expressions_window.WindowFunctionTests` cascades on real and not here.** `test_fail_update` runs a `.update(salary=Window(…))` that Django refuses client-side with `FieldError`; on real that poisons the enclosing atomic block, so the 23 alphabetically-later tests fail with `TransactionManagementError`, while on the simulator the block stays usable. No SQL reaches the server for the failing statement, so the difference is in what the *previous* statement left behind — worth pinning, since "the simulator's transaction survived where real's didn't" is the over-permissive direction.
- Smaller reverse-delta entry: `indexes.tests.PartialIndexTests.test_multiple_conditions` errors on real and passes here.

Getting there took eleven roots, and the pattern worth keeping is that failures cluster by *cause*, not by test — grouping them that way found each one:

- **Cascade beats breadth.** An unmodeled statement used to kill the TDS connection, so every later test in the class failed too; one statement accounted for 27 of 50 at the time. Now a statement-level fault is Msg 50000 severity 16 and the session survives ([`tds-endpoint.md`](tds-endpoint.md#statement-tier--severity-16-session-survives)).
- **Qualifier-blindness in name resolution** was the single largest class — a leaf-only match binds to the wrong column whenever a join brings a same-named one into scope, silently. It was wrong in four resolvers ([`query.md`](query.md#order-by-term-resolution)).
- The rest: outer-scope correlation from the select list, `UPDATE … SET` subqueries, parenthesized set-op branches, `OUTPUT … INTO` destination coercion, DISTINCT over a grouped projection, collation-aware `REPLACE` / `CHARINDEX`, aggregate re-homing across scopes, and `sys.time_zone_info`.

**Over-permissive validation — the simulator *accepts* what real *rejects*.** This is the more dangerous divergence direction (an app query works on the simulator and breaks on real), and it is invisible to a sim-only failure list: surface it with the *reverse* delta `comm -13 <sim fails> <real fails>`, where real-only failures mean the simulator over-passes. **Whole-suite audits should always run the reverse delta — a green "matches real" claim requires both directions.**

Worth keeping from that round: the backlog's own statement of the Msg 164 rule was wrong until probed (it is **not** about non-determinism — `GROUP BY a + DATEPART(year, GETDATE())` is legal — but purely "contains at least one column of the query's own sources"), which is the argument for probing a rule before encoding it even when a prior entry states it confidently.

Not sim bugs (**fail on real too** — leave alone): boolean-expression `=` comparison `WHERE (a<%s)=(b<%s)` → Msg 4145 on both; `CAST(<numeric> AS datetime2)` → Msg 529 on both (Django's DurationField tests expect it); most `get_or_create` `manual_pk`/duplicate IntegrityError tests (the savepoint-rollback-after-constraint pattern was probed identical to real). Not Django-specific: default-path string→date parsing is language-neutral, so `'1/2/3'` raises Msg 241 where real's `us_english` reads it mdy (deliberate — see [`casting.md`](casting.md)).

### Result-set serialization: `FOR XML` / `FOR JSON`

Both clauses ship (see [`xml.md`](xml.md#for-xml-result-serialization), [`json.md`](json.md#for-json-result-serialization)); these are the parts that don't:
- **`XMLSCHEMA` / `XMLDATA`** (inline schema emission).
  EXPLICIT + `XMLSCHEMA` reports real's own Msg 3625 instead.
- **One-row chunking** — real chunks the string across ~2033-char rows; the simulator returns it whole (shared by both clauses).
- **EXPLICIT's `idrefs` / `nmtokens` accept path** — real admits one where the column's expression is statically nullable and merges the per-row values into one space-joined attribute; the simulator has no expression-nullability model, so every such column reports real's Msg 6826 (which is what real gives the non-nullable shape).

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
- **A module body carrying an illegal explicit conversion creates** where real raises the conversion's own error at `CREATE` — `CAST(<date> AS int)` in a function body is **Msg 529** on real and creates here, surfacing only when the module runs (probed 2026-08-02 while pinning the `IsDeterministic` conversion rule; the same statement outside a body raises Msg 529 in both).
  CREATE-time body binding gathers the binder errors real gathers, and a conversion's legality isn't one of them.
  → [`programmable.md`](programmable.md#create-time-body-binding).
- **An unreferenced CTE is accepted** where real raises **Msg 422** (probed 2026-08-02 while closing the subquery permission seams); nothing leaks — an unreferenced CTE's plan never executes — so this is a pure parse-acceptance divergence.
- **Non-Framework CLR assemblies load** — real resolves every `AssemblyRef` against a fixed .NET Framework catalog and raises **Msg 6503** otherwise (probe-confirmed for .NET 10 and for .NET Standard 2.0); the simulator runs on .NET so all of them bind, which is also what lets the tests emit a fixture assembly without a Framework toolchain.
  → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **A join view over a join view is Msg 4405** for the INSERT or UPDATE naming one base table that real accepts, flattening both levels (probe-confirmed).
  A chain of *single-source* levels above one join view ships; a level reading several sources of its own doesn't, because the target source is then a view rather than a heap and there is no `(page, slot)` address behind the row the write would claim.
  Recursing the level walk into that source's own sources is the work.
  → [`programmable.md`](programmable.md#dml-through-a-join-view).
- **MERGE into a join view is Msg 4405** where real accepts a `WHEN NOT MATCHED THEN INSERT` whose column list names a single base table's columns and writes that table (probe-confirmed).
  MERGE reads `View.RejectionReason` up front; routing it wants the per-action column lists to pick the target the way INSERT's does.
  → [`programmable.md`](programmable.md#dml-through-a-join-view).
- **A GROUP BY view's aggregate column is Msg 4403** where real reports **Msg 4406** — real splits by which column the write names, `SET <group-by column>` being 4403 and `SET <aggregate column>` 4406 since the aggregate is a derived field (probe-confirmed, through a chained view too).
  `RejectionReason` settles the whole view before any column is looked at, so the per-column gate never runs on a shape that already failed; letting the 4406 walk run first on an aggregate / DISTINCT body is the work.
  → [`programmable.md`](programmable.md#updatable-views-dml-through-views).

## Fidelity gaps in shipped behavior

Real bugs / limitations against shipped behavior — fixes are concrete work, not design decisions.

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
- **Result-set `fNullable` inference — residue** — the five clusters that used to sit here (the per-built-in table, `@@`-variable nullability, string-vs-arithmetic `+`, the CASE-family constant folds, and the arm-conversion rule) all ship; the rule set is documented on `Expression.ResultIsNullable` and pinned by `ResultNullabilityTests` + `ColumnNullabilityWireTests` (see [`tds-endpoint.md`](tds-endpoint.md)).
  What's left is `CURSOR_STATUS`, which propagates its arguments where real is unconditionally NOT NULL — the simulator's `CURSOR_STATUS(scope, NULL)` returns NULL, and a NOT NULL fixed-width column has no NULL wire form, so the claim has to stay the one the value can't contradict until that return is made total.
- **Arm-unification result types past the shipped rules** — surfaced while pinning the arm-conversion nullability rule above, which reads the promoted type: three shapes unify to a type real doesn't pick.
  A pair of decimals whose joint envelope exceeds 38 digits caps by keeping the wider scale where real keeps the integral digits (`COALESCE(<decimal(38, 18)>, <decimal(38, 0)>)` → `decimal(38, 18)` vs real's `decimal(38, 0)`; the nullability answer agrees either way).
  `money` / `smallmoney` beside `float` / `real` raises the sim's own "explicit conversion … is not allowed" where real unifies to the approximate type, and so does `time` beside `datetime2` / `datetimeoffset` (real unifies to the date/time one) and `uniqueidentifier` beside `varbinary` (real unifies to `uniqueidentifier`).
  All three are `SqlType.Promote` / `SqlValue.Coerce` table entries.
- **Dependency-surface residue** — the four surfaces ship (see [`catalog-views.md`](catalog-views.md#expression-dependencies)), with three known divergences.
  Column granularity is name-based (a statement frame touches column `C` of referenced object `T` when it names `T` and mentions `C`); a qualified mention narrows to its own source, so joins / `APPLY` / `MERGE` match real exactly, but an **unqualified** mention in a multi-source frame still lands on every source that has a column by that name, and a MERGE target's key column picks up an extra `is_updated` when it appears in both the `ON` and a `WHEN NOT MATCHED THEN INSERT` column list.
  Closing both wants parse-time (source, ordinal) capture, which the per-row name-keyed resolver doesn't do.
  `sys.dm_sql_referenced_entities`' **Msg 2020 arrives before the rows** rather than after them, because the reader materializes the rowset before delivering it, where real yields what it found and then raises.
  Two more surfaced while projecting the legacy `sys.sql_dependencies` / `sysdepends` pair, both recorded in [`catalog-views.md`](catalog-views.md#divergences): a computed-column / CHECK / DEFAULT expression marks the columns it names `is_selected` where real leaves all three use flags 0 (one `ColumnUse`, so every surface reads it), and a reference mixing a whole-object write with a column-level one loses the legacy pair's object row, since the aggregated `Reference` no longer says which statement contributed which.
- **`OBJECTPROPERTY(id, 'IsDeterministic')` — the converted expression's own type** — the module walk ships whole, the `CAST` / `CONVERT` style rule included (see [`catalog-views.md`](catalog-views.md#isdeterministic)).
  The named target type and the style read off the token stream exactly; the *source* expression's type is inferred from the evidence its extent carries, which leaves four shapes undecided and reading deterministic: a column name the body's referenced tables don't carry (a CTE or derived table's own output, an alias-type column), a user function whose return type isn't its argument's, a style written as a constant expression (`121 + 0`, which real folds), and an ANSI type synonym (`character varying`).
  Closing them wants the source extent bound as an expression rather than classified from tokens.
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
- **A declared decimal scale past 28 fractional digits can't ride on the value** — `SqlValue.FromDecimal` stamps the [declared scale](arithmetic.md#the-value-carries-the-declared-scale) onto the .NET `decimal`, but .NET caps at 28 fractional digits and a 96-bit mantissa, so `numeric(38, 30)` — or `numeric(38, 20)` holding a 15-digit integer part — settles at the widest representation available and renders fewer trailing zeros than real through the surfaces that write the raw value (the JSON builders, `GetDecimal`).
  The string-rendering paths format from the declared `SqlType` and are unaffected, and the same 28-digit ceiling already bounds the type's *value* range, so closing this means a decimal representation of the simulator's own rather than a change at the stamp.
- **`DEGREES` / `RADIANS` over a `numeric` argument diverge from real in the last ~3 of the 18-digit result** — the simulator's `DecimalPi` constant carries less effective precision than real's conversion; surfaced while auditing the declared-scale stamp (2026-08-02), pre-existing and independent of it.
  Closing it means re-deriving the constant (or the multiply) at full `decimal` precision and diffing the two functions' probed answers across magnitudes.
- **Declared string widths — four neighbours surfaced while fixing `CONCAT`'s width-less argument** (2026-08-03), all probe-confirmed against SQL Server 2025 and each independent of the others.
  A **bound string parameter** carries the length-unspecified `varchar` / `nvarchar` form rather than the width its RPC declaration (or `DbParameter.Size`) names, so `SELECT @p` advertises the family container where real advertises the declared width and `CONCAT(?, ?)` over two `nvarchar(2)` parameters is `nvarchar(4000)` where real says `nvarchar(4)`; carrying the width also means carrying real's truncation of a longer value to it (`EXEC sp_executesql N'SELECT @a', N'@a nvarchar(2)', N'abcdef'` → `ab`), which is what makes it a change of substance rather than a type-stamp.
  **`sys.columns.max_length` reports 1 for a container-typed expression** — a computed column or `SELECT … INTO` destination off `REPLACE` / `TRANSLATE` / a width-less `CONCAT` — where real reports 8000 / 4000; the value stores in full, only the reported width is wrong, and it comes from the length-0 form having no `max_length` mapping of its own.
  **`REPLACE` drops MAX-ness**: its result is the bounded container whatever the input, so `REPLACE(<varchar(max)>, …)` advertises `varchar(8000)` where real carries `varchar(max)` — the other length-deriving scalars branch on `StringScalars.IsMaxForm` first and `REPLACE` doesn't.
  And smallest, from the same probe: **`TRANSLATE` projects `nvarchar` for a `varchar` input** where real keeps `varchar`.
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
