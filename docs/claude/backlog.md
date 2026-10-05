# Backlog

Forward-looking work list: missing features, fidelity gaps in shipped behavior, and design choices worth revisiting.
**Not a checklist** — completed work is removed, not ticked.

Ordering within each section leans toward predicted importance (popularity × ease, the Operating-goal weighting in [`../../CLAUDE.md`](../../CLAUDE.md)), but is **explicitly non-authoritative**: anything here is valid to pick up, and so is anything *not* here.

Most entries are triggers: the feature doc each points at holds the detail and the probe results, in its **Not modeled yet** or **Divergences** section.
An entry carries its detail here only when no single feature doc owns it.

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
The probing traps hit in practice are under [other differential oracles](sqllogictest.md#other-differential-oracles); the first is to re-run the oracle that found a bug rather than rebuild it from the entry's prose.

An entry marked **Deferred until requested** is parked by the user's call, not by cost: it stays listed so someone looking for it finds its findings, but isn't picked up autonomously — only when a user asks for that behavior.
Non-Latin collation work (the East Asian families, legacy and hidden collations, non-Latin code pages) is deferred this way as of 2026-09-29; the common Latin collations and code page 1252 stay active.

This file is the home for net-new non-function feature proposals too.
CLAUDE.md's **Not modeled yet** section is the complementary *descriptive* map (what raises `NotSupportedException` / Msg today, so the surface isn't over-promised); this list is the *prospective* one.
An item can appear in both with opposite intent.

## Missing features

### Unbuilt feature areas

A standing survey of areas with work left, one entry each pointing at the deep-dive that holds the detail.
Presence here is status, not a priority claim — the ordering caveat at the top of this file applies.
The subsections that follow carry the areas whose detail no single feature doc owns.

- **Full-text linguistic residue** — the languages other than English (their breakers' locale rules and their morphologies), a populated thesaurus and `sys.sp_fulltext_load_thesaurus_file`, the index keyword DMVs, the part-of-speech lexicon behind real's inflection listing, a surface form spanning two lemmas (`leaves` → `leaf` *and* `leave`), the `SEMANTIC*` rowsets and `RANK` values → [`full-text.md`](full-text.md#not-modeled-yet).
- **Spatial residue** — curve-producing results, `IsValidDetailed()` on an invalid instance, `geography` operations spanning a hemisphere, the last digits of oblique crossings and buffers, the planar validator's lobe split, `STPointOnSurface` over a polygon with a hole, `MinDbCompatibilityLevel()` of an invalid `geography`, the property form outside a query scope, GML, SRID transformation, `sys.spatial_reference_systems` rows, `ALTER SPATIAL INDEX` and planner use of the spatial index → [`spatial.md`](spatial.md#not-modeled-yet).
- **`WRITETEXT BULK` / `UPDATETEXT BULK`** — real's bulk form is a bulk-copy data stream fed over the wire rather than a statement, so closing it means a TDS bulk-load path keyed to a text pointer → [`legacy-lob.md`](legacy-lob.md#not-modeled-yet).
- **XQuery and typed-XML residue** — an outer reference to a `.nodes()` row column, the context item's static type, numbers past 2⁵³, dynamic XQuery → [`xml.md`](xml.md#not-modeled-yet); `xml(DOCUMENT …)` on a column or variable and an XSD that doesn't compile → [`xml.md`](xml.md#typed-writes--validation-and-canonical-form).
- **Cursor residue** — `sys.dm_exec_cursors` rows for API cursors and another session's local cursors, asynchronous keyset population, per-fetch projection errors, a FAST_FORWARD `DISTINCT` real's optimizer drops over a key → [`cursors.md`](cursors.md#not-modeled-yet); the API cursor flag protocol → [`tds-endpoint.md`](tds-endpoint.md#api-server-cursors-sp_cursor-rpc-family).
- **Row order without ORDER BY, where real's plan decides it** — open are `CUBE` / general `GROUPING SETS` and the constant-only set operations real merges anyway ([`query.md`](query.md#row-order-without-order-by)).
  **A join's order is settled — don't re-pitch**: it follows real's cost-based choice of driving input, which real doesn't guarantee ([`joins.md`](joins.md#row-order-of-a-join)).
  **The order among rows an `ORDER BY` leaves tied is settled — don't re-pitch**: real's own answer there is nondeterministic (its sort isn't stable and its plan choice moves it), so a test or probe that pins one tie order is pinning a plan accident, and the simulator's stable order is an equally valid answer.
  The sqllogictest oracle sorts `rowsort` records before comparing, so none of it shows there as a divergence.
- **Settled — don't re-pitch: predicate folding where real's plan decides it** — the five sqllogictest records that raise here and answer on real turn on the trivial-plan boundary, written order inside an `IN` list and per-row short-circuiting, none of which real guarantees → [`sqllogictest.md`](sqllogictest.md#standing-result), [`query.md`](query.md#not-folded-yet).
- **Settled — don't re-pitch: locks follow the simulator's access path, not real's optimizer's**, and the simulator keeps no ghost records; real's footprint and its ghost race follow its plan and its cleanup timing, which it doesn't guarantee → [`locking.md`](locking.md#divergences).
- **Concurrency residue** — a heap scan passing a key deleted and inserted behind it, a batch's schema locks taken per statement rather than at compile, a redefinition deadlocking the open writer it waits on, a joined write's wait type on a moved key, and a stress finding seen once → [`locking.md`](locking.md#concurrency-stress-findings).
- **Write-locking residue** — what the lock DMVs show while a writer waits on a joined partner or a nonclustered key, a second writer of a key holding nothing on its new row, and key descriptions printing the key rather than a hash → [`locking.md`](locking.md#a-writers-target-read), [`locking.md`](locking.md#writers-racing-to-one-new-key).
- **Server permissions whose statements aren't built** — `SHUTDOWN`, credentials, endpoints, event sessions, audits, traces, the error log, `UNSAFE ASSEMBLY` (which waits on `clr strict security`) → [`permissions.md`](permissions.md#not-modeled-yet).
- **Row-level security residue** — a non-schema-bound predicate's binding failure raised as each statement runs rather than as the batch compiles, an `INSTEAD OF UPDATE` trigger's replaced statement still judged by block predicates (and CHECK constraints), a filter predicate on a `SHORTEST_PATH` walk → [`row-level-security.md`](row-level-security.md#divergences).
- **Default-schema residue** — the dependency surfaces resolving a module's one-part names through `dbo`, Msg 2809 / state 224 for a default-schema object of the wrong kind, `guest`'s unmaterialized schema → [`schemas.md`](schemas.md#not-modeled-yet).
- **Security residue from the permissions differential sweep** — `PERMISSIONS()` bitmaps, the full-text catalog creator's ownership, and a handful of error-ordering cases → [`permissions.md`](permissions.md#known-gaps).
- **Temporal, change-tracking and sequence residue** from their differential sweep — ledger-style `GENERATED ALWAYS AS TRANSACTION_ID | SEQUENCE_NUMBER` columns, `FOR SYSTEM_TIME` after a CTE and an aliased `UPDATE … FROM` target ([`temporal-tables.md`](temporal-tables.md#not-modeled-yet)), and an `INSERT … SELECT`'s draw shared with a defaulted column, a table variable's or `#temp` table's sequence default ([`sequences.md`](sequences.md#not-modeled-yet)).
- **Partitioning residue** — an indexed view's index placement, `FILESTREAM`, LOB data on a filegroup without files, per-partition XML compression, `SELECT … INTO … ON`, partition-level lock escalation and SWITCH's remaining checks → [`partitioning.md`](partitioning.md#not-modeled-yet); a `numeric` function parameter, which neither reports type 108 nor refuses a `decimal` column, is among its [divergences](partitioning.md#divergences).
- **Query Store residue** — size-based and stale-query cleanup, `MAX_PLANS_PER_QUERY`, module-relative offsets, the compile-CPU capture threshold and applying a forced plan or a hint → [`database-options.md`](database-options.md#not-modeled-yet).
- **DML plan residue** — `INSERT … SELECT`, a `MERGE` from a query or through a view, the joined forms, DML through a view, a statement holding a subquery, and a principal permission checks apply to → [`plan-cache.md`](plan-cache.md#not-modeled--future).
- **Batch-compile deferrals** — the walk stops at a deferral raised mid-statement, and raises at compile a DML `TOP` refusal or a derived table's `NEXT VALUE FOR` that real defers with the batch's own table or sequence → [`control-flow.md`](control-flow.md#not-modeled-yet).
- **Syntax-error recovery** restarts at statement keywords rather than walking real's grammar → [`errors.md`](errors.md#divergences--residuals); the stray-`(` neighbors sqllogictest's `evidence/` slice surfaced → [`grammar.md`](grammar.md#divergences).
- **Message-stream residue** — the language of diagnostics other than Msg 5703 after `SET LANGUAGE`, Msg 8153 over a constant `VALUES` grouping and the Msg 1708 row-size warning → [`errors.md`](errors.md#not-modeled-yet); Msg 3621's attribution after a trigger's error → [`triggers.md`](triggers.md#not-modeled-yet).
- **Query-semantics residue** — a written-constant `CASE` / `IIF` / `COALESCE` typing a string as the arm it takes ([`query.md`](query.md#boolean--set-ops--projection--case)), a derived table's set-operation `OFFSET` / `FETCH` ([`query.md`](query.md#pagination-offset--fetch)), the groups and window rows streamed ahead of an aggregate's error and an `ORDER BY` pairing a unique outer key with an `APPLY` body column ([`query.md`](query.md#streaming-accumulation-and-where-an-error-surfaces)), and `TOP … ORDER BY` ahead of a `UNION` in an `IN` subquery ([`subqueries.md`](subqueries.md#divergences)); settled — don't re-pitch: Msg 8153 from a quantified comparison, which rides a rewrite real doesn't guarantee (same section).
- **DML residue** — the identity values a failing `MERGE`, window select list or `IDENTITY_INSERT` statement uses up on real, `INSERT TOP (n)` reading past its n rows, a FROM-less source's constant converting under a never-TRUE `WHERE`, `$IDENTITY`, a `SELECT … INTO`'s DONE after a projection error ([`dml.md`](dml.md)), the client `OUTPUT` rows real streams ahead of a later row's error ([`dml.md`](dml.md#update--delete)), and a `DEFAULT` or column-level clause real refuses ([`alter-table.md`](alter-table.md#fidelity-gaps)).
- **Procedure-call residue** — the argument errors real reports differently (a repeated named argument, `VARYING` on a scalar, `sp_executesql`'s missing `@`) and `@@PROCID` in an ad hoc batch → [`programmable.md`](programmable.md); `DROP TYPE`'s Msg 3732 running the batch on → [`table-valued-parameters.md`](table-valued-parameters.md).
- **Scalar residue** — `DIFFERENCE`'s scoring ([`scalars.md`](scalars.md)), `AT TIME ZONE` before a zone's first rule ([`scalars.md`](scalars.md#at-time-zone)), `CHECKSUM` of `decimal` and of strings outside `SQL_Latin1_General_CP1_CI_AS` ([`scalars.md`](scalars.md#checksum-family)), `CONVERT(binary(n), '', 2)` ([`casting.md`](casting.md#not-modeled-yet)) and a `hierarchyid` built from invalid bytes ([`hierarchyid.md`](hierarchyid.md#errors)).
- **Latin-collation residue** — characters outside the Latin1-General tables, the weightless and space characters under the culture comparers, standalone combining marks, `TRANSLATE` over surrogate halves, a `LIKE` / `CHARINDEX` operand moved into a `_UTF8` collation, `STRING_AGG`'s Msg 468, `TERTIARY_WEIGHTS` → [`collations.md`](collations.md#known-gaps); settled — don't re-pitch: the `Pref` names' grouped spelling and the tied spelling `MIN` / `MAX` report, which follow real's sort-or-hash choice and tie order → [`collations.md`](collations.md#the-latin1-general-names--byte-exact-sort).
- **Catalog and DDL residue** — `sys.identity_columns`' system rows and PAGE compression ([`catalog-views.md`](catalog-views.md)), `sys.column_store_segments` and a filtered index over a bare column ([`indexes.md`](indexes.md#fidelity-gaps)), `tempdb`'s names for temp objects ([`temp-tables.md`](temp-tables.md#tempdbs-catalog-lists-them), [`table-variables.md`](table-variables.md#fidelity-gaps-remaining)), sparse column sets ([`alter-table.md`](alter-table.md#fidelity-gaps)).
- **Session-option residue** — the `SHOWPLAN_*` / `STATISTICS XML` plan result sets, `STATISTICS IO`'s system base tables and table order, `SET ANSI_PADDING OFF` at `CREATE TABLE`, `FORCEPLAN`, `FIPS_FLAGGER`'s warnings, `SET ROWCOUNT` over a cursor's population, the plan-cache attribute DMVs, and a same-batch `ALTER DATABASE … ANSI_NULL_DEFAULT` → [`session-options.md`](session-options.md#not-modeled-yet).
- **JSON residue** — `OPENJSON … WITH` naming a column twice, Msg 2724 after a `COLLATE` on a non-string column, and a constant-folded arm's JSON text embedding raw → [`json.md`](json.md#divergences).
- **Programmable-object residue from their differential sweep** — the trigger message-stream cases and `COLUMNS_UPDATED()` in a function a trigger calls ([`triggers.md`](triggers.md#not-modeled-yet)), a DDL trigger's veto ([`triggers.md`](triggers.md#not-modeled-yet-1)), the `nested triggers` option read per batch ([`triggers.md`](triggers.md#not-modeled-yet-3)), a multi-statement function's `SPARSE` column, inline `INDEX` catalog row and `@@NESTLEVEL`, a joined write through an inline function's alias and `EXEC` of a table-valued function ([`programmable.md`](programmable.md#multi-statement-table-valued-functions)), and columnstore, statistics and `IsPrecise` on an indexed view ([`indexes.md`](indexes.md#fidelity-gaps)).
- **View-write residue** — a chained view whose `WHERE` reads a column the level below derives, an `INSTEAD OF` trigger two levels down, the row count before a session-ending positioned write, and distributed partitioned views → [`programmable.md`](programmable.md#updatable-views-dml-through-views).
- **Linked-server expression remoting** — a four-part read naming a `vector` column anywhere but its output is Msg 7346 here where real runs those parts on the server, and a remote UPDATE's failing expression raises locally where real's server raises it → [`linked-servers.md`](linked-servers.md#divergences).
- **Linked-server residue** — a loopback's remote call enlisting in the caller's transaction, the catalog procedures over a linked server (`sp_linkedservers`, `sp_testlinkedserver`, `sp_tables_ex` …), `sys.sysservers`, names with empty middle segments or five parts, remote function references and an INSERT listing a computed or `rowversion` column → [`linked-servers.md`](linked-servers.md#not-modeled-yet).
- **Dependency-surface residue** — an unqualified mention in a multi-source frame, a MERGE key column's extra `is_updated`, Msg 2020's position, a mixed whole-object and column reference, and a comma `FROM` list's source order → [`catalog-views.md`](catalog-views.md#divergences); `OBJECTPROPERTY(…, 'IsDeterministic')` through a derived table's untyped output → [`catalog-views.md`](catalog-views.md#isdeterministic).
- **CLR residue** — .NET's own wording and frames in a routine's exceptions, the context connection's unmodeled shapes, and an aggregate's state never round-tripping through `Write` / `Read` → [`clr-assemblies.md`](clr-assemblies.md#divergences), [`clr-assemblies.md`](clr-assemblies.md#the-context-connection).
- **TDS token-stream residue** — DONE tokens under `SET NOEXEC ON`, `ALTER SCHEMA … TRANSFER`'s error DONE, a system procedure's return status → [`tds-endpoint.md`](tds-endpoint.md#per-statement-done-tokens).
- **DBCC residue** — `PAGE` / `IND` / `SHOWCONTIG` and the other unbuilt subcommands, the system base tables the consistency checks list, and the storage-derived values → [`dbcc.md`](dbcc.md#not-modeled-yet).
- **Index, statistics and hint residue from their differential sweep** (2026-10-05) — histogram step compaction, a cached plan's statistics staleness, automatic statistics through a view and over a failing computed key ([`indexes.md`](indexes.md#the-statistics-lifecycle)), the usage and missing-index DMVs' counters ([`indexes.md`](indexes.md#index-dmvs)), a computed clustered key's and a dropped clustered index's row order ([`indexes.md`](indexes.md#fidelity-gaps)), and an index-hinted scan's row order, a filtered-index hint's Msg 8622, join-hint feasibility through `APPLY`, and where Msg 8625 lands ([`query-hints.md`](query-hints.md#not-enforced)).
- **Two `NEWID()` placements** — a merged body's drawn column that reads a body column, and an `APPLY` body's `TOP 1 … ORDER BY` a non-drawn column → [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration).
- **Result-set serialization** — `FOR XML`'s `XMLSCHEMA` / `XMLDATA` and EXPLICIT's `idrefs` / `nmtokens` accept path → [`xml.md`](xml.md#for-xml-result-serialization).
- **Deferred until requested.** **Real accepts collations `sys.fn_helpcollations()` doesn't list**, in columns, `COLLATE` and `COLLATIONPROPERTY` alike, where the simulator refuses them as unrecognized (probed 2026-09-28 against SQL Server 2025).
  Searching every listed language base against every version suffix finds only `Azeri_Latin_90_*` (LCID 1068, code page 1254) and `Azeri_Cyrillic_90_*` (2092, 1251), version 1, with every flag suffix including `_SC` and `_SC_UTF8`.
  They are not aliases: they sort by the Azeri alphabet as `_100` does but order `I i ı İ` where `_100` orders `I ı i İ`, and they carry no dotted / dotless I case rule (`UPPER(N'iı')` is `II`, `_100`'s is `İI`).
  Four unversioned names outside the listed bases resolve too, at version 0: `Hindi_CI_AS` (1081, code page 0), `Macedonian_CI_AS` (1071, 1251), `Korean_Wansung_Unicode_CI_AS` (66578, 949) and `Lithuanian_Classic_CI_AS` (2087, 1257); other historical names tried (`Mexican_Trad_Spanish`, unversioned `Azeri_*` / `Uzbek_*` / `Tatar` / `Kazakh` …) and non-listed `_UTF8` / `SQL_` combinations do not.
- **Deferred until requested.** **East Asian collations without a primary table of their own** — six unversioned families ship real's primary order and match it on 99.998–99.999% of probed string pairs; the versioned families borrow the nearest one, which leaves `Korean_90` / `Korean_100` at 84% / 51%, the bopomofo families at 84–92% and the `_90` / `_100` Chinese and Japanese ones at 94–99% (see [`collations.md`](collations.md#locale-comparer-sort-parity-gap), probed 2026-09-29).
  Each further table costs 25–80 KB deflated; the probe query is in that section.
- **Deferred until requested.** **Code page 932's best fit** — `N'Á'` stored in a `Japanese_CI_AS` `varchar` column is `A` on real and `?` here (probed 2026-09-29, re-checked 2026-10-03), the shape of the code page 874 fits `WindowsBestFitFallback` supplies past .NET's table.

### TDS network endpoint — follow-up phases

The endpoint ships with SQLBatch + RPC + Transaction Manager support and credential enforcement via the `CREATE LOGIN` registry (see [`tds-endpoint.md`](tds-endpoint.md)); EF Core runs over the wire through vanilla `UseSqlServer`.
Remaining phases, roughly in value order:

- **Tool shakedown** — point real client tools at the endpoint and harvest their exotic catalog queries / SET shapes into this backlog.
  Tool scope (user decision): tools a SQL Server + .NET developer already has — SSMS, sqlcmd, Visual Studio (SQL Server Object Explorer / DacFx), LINQPad; DBA-flavored tools like DBeaver are out of scope.
  **SSMS is the final boss** — an ongoing campaign, not a single leg: each surface is its own multi-round harvest, and clearing one unlocks the next.
  Cleared legs are recorded in the per-feature deep-dives (catalog surface in [`catalog-views.md`](catalog-views.md), wire behavior in [`tds-endpoint.md`](tds-endpoint.md)); the discovery harnesses are the gitignored `.vs/ssms-host` TDS host and the headless SMO property-bag drain.
  **Remaining frontier**: Table Designer, Activity Monitor, standard reports, and IntelliSense's background metadata harvest.
  Candidate follow-on legs within tool scope: Visual Studio's SQL Server Object Explorer (DacFx-driven, a different query dialect from SMO) and LINQPad.
- **What the SMO sweep leaves open** (the local-only `.vs/smo-sweep` drain; its standing result is under [other differential oracles](sqllogictest.md#other-differential-oracles)):
  - **`DBCC SHOW_STATISTICS … WITH STATS_STREAM`** (SMO `Statistic.Stream`) stays `NotSupportedException` — it wants the raw serialized statistics blob, which the simulator has no faithful source for.
  - **The shipped modules' own T-SQL** isn't carried: `sys.system_sql_modules.definition` is NULL, so SMO reads each system view / procedure / function as encrypted and won't script one or read its `Text`.
  - **Most system objects are unmodeled** — SMO lists some 1,400 procedures, 400 views and 140 functions the reference ships that the simulator doesn't.
  - **No system base tables in `sys.partitions` / `sys.allocation_units`**, so a database with no user table sums to NULL there and SMO's `DataSpaceUsage` / `IndexSpaceUsage` / `SpaceAvailable` raise `InvalidCastException` (master, model, tempdb and any empty user database).
  - **msdb's Service Broker seed objects** — SMO's `IsMailHost` finds no `InternalMailService` in `sys.services`.
  - **Storage accounting**: a LOB-eligible value always takes an off-row LOB page, where real keeps one that fits in row, every nonclustered index reports its heap's page count, and a partitioned table counts one page where real counts one per non-empty partition (`sp_statistics`' `PAGES`), so the sizes SMO reads run 10–90× the reference's.
  - **DacFx's import naming** — a bacpac import keeps `CREATE DATABASE`'s file names where DacFx names the primary pair `<db>_Primary.mdf` / `.ldf`, and index ids follow the loader's statement order rather than DacFx's.
  - **`xp_instance_regread`'s Msg 22001** for a value it doesn't find → [`catalog-views.md`](catalog-views.md).
- **Browse mode residue** — `KeyInfo` / `SET NO_BROWSETABLE` ship over the wire (hidden key and rowversion columns, TABNAME / COLINFO — see [`tds-endpoint.md`](tds-endpoint.md#browse-mode-commandbehaviorkeyinfo)); what's left is real's flattening of a derived table or view into its base tables for that metadata.
- **MARS requests don't interleave** (details in [`tds-endpoint.md`](tds-endpoint.md#mars-multiple-active-result-sets)): real streams a request's results as the client reads them, so a reader still draining a SELECT sees a second request's writes to the rows it hasn't reached; the simulator runs each request whole under the execution gate, which also bounds a session's response memory by the whole result.
  Closing it means suspending an engine enumeration mid-statement while another request runs on the same connection — the one-executor-per-connection contract the gate exists for.

### Complex-query execution — perf residuals

The complexity batteries (`.vs/workload` `compare` subcommand + `complex*.sql`, local-only) are the standing measurement instrument; sub-4× ratios against the live reference are constant-factor or parallelism territory, larger ones name a missing execution strategy.
How to measure without fooling yourself is in [`plan-cache.md`](plan-cache.md#performance-impact); two strategies measured out and **settled — don't re-pitch** are recorded where they would have been built — a fan-out-aware semi-join crossover ([`subqueries.md`](subqueries.md#an-equi-correlated-body-switches-to-a-hash-semi--anti-join)) and building a join's hash over the smaller side ([`joins.md`](joins.md#equi-join-fast-path)).
Open residuals, in measured-impact order:

- **Catalog introspection's remaining per-statement cost** — with the cross-statement catalog row cache ([`catalog-views.md`](catalog-views.md#cross-statement-row-cache-and-indexes)), SMO's per-table column-properties query runs at ~3.6× the live reference (59 ms against 17 ms over 30 tables, measured 2026-09-28) and its whole-database table-properties query at ~2.3×, while the rest of the introspection battery runs at or below live.
  The catalog work is gone from those profiles; what remains is ordinary per-statement cost — resolving and projecting forty-odd columns across seventeen joined sources — so it is constant-factor territory shared with every wide join.
  Catalog-specific leftovers: `sys.partitions` and `sys.sequences` aren't cached (their DML-moved state has no cheap stamp, where `sys.identity_columns` has one), so a statement joining them regenerates them; and the transitive seek hop is one level.
- **DML through a join view bypasses the joined-source passes** — `Simulation.JoinViewDml.cs` enumerates without the materialization/narrowing preparation `UPDATE`/`DELETE` take, and the **narrowed-source-first reorder is declined for every DML statement**; its WHERE names view output columns resolved through the chain resolvers, so wiring it is a name-resolution correctness question before a perf one.
- **Reorder decline list, narrowable** — a single-source ON conjunct (`ON a.k = b.k AND b.flag = 1`) declines the whole reorder where it is WHERE-equivalent for an all-INNER chain and could attach at its source's step; a chain whose outer joins all follow the driving position could still commute its INNER prefix; a source narrowed to more than 128 rows never drives even where it would win.
- **Grouped-body key reduction declines expression groupings** — a body grouping on `MONTH(d)`-style expressions takes no join-key reduction (only plain grouping-column projections qualify), and `ROLLUP` / `CUBE` / `GROUPING SETS` streams still buffer where a single grouping set streams.
- **The row-number bound doesn't reach a view body** — the greatest-n-per-group idiom takes a bounded per-partition selection through a derived table or a CTE (see [`query.md`](query.md#bounded-per-partition-row_number-selection)), but a `ROW_NUMBER()` body stored as a **view** doesn't: the wrapper's output ordinals aren't known until the reference executes, so the bound would have to travel to the body parse the way a pushed predicate template does (`Simulation.InvokeView`'s `pushedPredicates` seam is the shape it would take, offering a bound per constant-bounded column instead of the one ordinal the plan already knows).
- **A bound past the selection heap's ceiling still reads and buffers its partition** — a deep-paging `rn BETWEEN 100001 AND 100050` selects its window out of a buffer rather than sorting (see [`query.md`](query.md#bounded-per-partition-row_number-selection)), but still reads, keys and holds every row: 127 ms against real's 40 ms over 150k rows (measured 2026-09-30).
  Real reaches the same rows through an ordered index scan with a Top; the simulator's ordered-scan machinery (`OrderedSeek`) already positions a keyset page that way for a statement's own ORDER BY, so pointing a partitionless window at it is the natural next step.
- **The equality seek has no span gate of its own** — a range abandons the seek once its interval selects more than a quarter of the rows (see [`indexes.md`](indexes.md#the-span-gate)), because past that the per-address reads lose to the sequential scan.
  An equality on a **low-cardinality** column has the same shape (a flag whose bucket holds most of the table) and takes no such gate, so it pays the random-address materialization for a set the scan would have walked in order.
  The bucket length is already in hand at the point the candidates are handed back, so the gate is the same two lines; what it needs is a measurement of where the crossover actually sits for a hash hit, which is a cheaper lookup than the ordered walk the range gate was calibrated against.
- **The join reorder can't see a prefiltered source** — `NarrowJoinSources` picks its driver by *seeked candidate count*, and the scan prefilter (see [`indexes.md`](indexes.md#the-scan-prefilter-a-join-source-no-key-can-seek)) hands back a lazy stream with no count, so a heavily-filtered non-leftmost source never drives.
  The written order stands instead, which costs the reorder's win on `FROM <big> JOIN <filtered small>` written in that order.
  A sampled count (drain the filter's first `SeekOuterRowCap + 1` rows into the buffer the reorder would need anyway) would settle it without materializing the table.
- **A seeked source drops its remaining sargable conjuncts** — the prefilter is the seek's fallback, so `WHERE o.CustomerID = @c AND o.OrderDate BETWEEN @a AND @b` seeks on the customer and leaves the date to the residual rather than also filtering the seeked stream.
  Harmless while the seek is selective; a low-cardinality equality prefix plus a selective range is the shape where it would pay, and it composes with the equality span gate above.
- **A buffered `SqlValue` result costs more live memory than the page image it replaced** — the reader path carries the projection's own rows (see [`data-reader.md`](data-reader.md#the-row-form-the-reader-reads)), which cuts the statement's total allocation 35-43% but keeps a `SqlValue[]` per row alive while the reader is open: ~32 bytes per cell against the compact record, measured at 2.1× (4 columns) / 2.9× (10) / 3.4× (25) the buffered footprint on a 150k-row drain.
  The arrays are ones the projection allocated anyway — the encode was a *compaction* pass that then threw them away — so the trade is steady-state allocation against transient peak, and it is the right way round for the result sizes a test double sees.
  If a consumer draining a very wide, very long result feels it, the mitigation is a **form gate in `SimulatedSqlResultSet.MaterializeRows`**: encode when the projection is wide enough that compaction pays, keep values otherwise (the two forms are answer-for-answer identical, so the choice is pure policy). Wall-clock measurements on the dev box were too noisy to place the crossover — that measurement is the first half of the work.
- **Every statement is parsed twice**, once by the batch-compile walk and once to run.
  Replaying sqllogictest's `index/in/1000` single-threaded (measured 2026-09-27), the walk is 8.8% of process CPU — 7.8 points of it parsing the SELECT, tokenizing already shared with the run — and skipping it outright bounds the win at 13.0 → 12.1 s.
  Its texts are all unique, so `compiledBatches` never hits, and parameterized workloads (WWI) reach the plan cache before the walk runs, so a wider cache recovers nothing measured.
  Reusing the walk's `Selection` for the run isn't sound as-is: skip mode takes `DataLockPlan.Bypass`, and permission checks, FROM-less evaluation, placeholder sources and the `TOP (@p)` declared-type check all differ under it.
  Skipping the walk for a one-statement batch isn't a shortcut either: a compile error surfaces at `ExecuteReader` where a run-time one surfaces at `Read()`, and the batch-abort rules differ.
  The prerequisite for any real win is moving those parse-time run-only effects into an execution step, so that a skip-mode plan equals the run's.
- **Intra-query parallelism is the systematic remainder** — the scan-bound shapes that survive every constant-factor pass (`daterange.*`, `conditional.agg_pivot`, `window.three_sorts`, `union.dedup_big`, the year-aggregating reports) sit at 1.5-3.5× live with real running DOP 8, and profiling shows the simulator using *less CPU* than real on several of them — the gap is parallel execution, not waste.
  An unprofiled small neighbor: a `TOP (1000)` ordered by the PK over a wide table sits ~3× (`format.heavy_1000`).
  **Covered so far**: the streaming single-grouping-set aggregate path's per-row *consumer* work forks across worker threads while the calling thread produces the stream — built, tested and measured at 1.2-2.4× on a single-session battery, and **off by default** because the concurrent workload driver loses 25-30% throughput from even a handful of forks (see [`query.md`](query.md#parallel-grouped-accumulation-built-proven-off-by-default) for the gates, the merge contract and the error rule).
  **What that leaves.** Three things, in the order they are worth doing:
  1. **The residual process-wide cost of forking at all** — the AdventureWorks driver loses its throughput across a phase in which the fan-out never engages again, so the cost outlives the forked statements. Thread lifetime (a retiring pool; a 1 ms idle timeout) and block-allocation size were both ruled out by measurement. Until this is explained the default cannot flip, and explaining it is the whole gate on the rest of the work.
  2. **The producer is still serial**, so Amdahl bounds every join-fed aggregate: a bare `COUNT(*)` over the 228k-row `Invoices ⋈ InvoiceLines` join costs ~70 ms on its own. Parallelising the join itself means building the hash once and partitioning the probe side, which is a change to the join driver rather than to the aggregate path.
  3. **The shapes the pilot doesn't reach**: an ordinary (non-aggregate) projection, `DISTINCT` and the set-operation dedup, the window executor's sorts, and a high-cardinality `GROUP BY` (the per-worker maps' merge is what makes 73k groups lose). Each needs its own merge argument; none inherits the aggregate path's.
  **Scheduling (user direction, 2026-08-05): none of the above proceeds until the easier algorithmic wins elsewhere in this section are exhausted** — every "parallelism territory" shape profiled so far hid an algorithmic or constant-factor win under the label, and the MAXDOP-1 comparison confirms most residual ratios are constant factors at equal threading.
  Item 1's unexplained fork cost is additionally the hard gate on flipping the pilot's default, whenever the topic reopens.
- **What EF Core's functional classes still spend in the simulator** (measured 2026-10-03, see [`plan-cache.md`](plan-cache.md#second-pass-the-simulator-inside-the-test-process)) — inside a test process the simulator's methods mostly never leave tier 0, which is the consumer's tiering settings rather than the library's; of what the library controls, the costs left are:
  the per-outer-row seek of an INNER / LEFT join re-plans the whole seek (conjunct collection, equality and range analysis, a new seeked `FromSource`) for every outer row, ~1.1 µs and ~2.8 KB each, where the access path is value-independent after the first row and only the probe values change;
  an uncached statement builds two `BatchContext`s, each with eager dictionaries, so `SELECT 1` costs ~3 µs and ~9 KB;
  the first catalog read builds every catalog view (~50 ms per process, about half of it compiling the `Register*` methods), and `BuiltInResources`' initializer still carries `sys.types`' rows, the configuration table and the time-zone list (~7 ms of that);
  a correlated subquery reading only an outer-outer row re-runs per inner row — Northwind's `EXISTS` over three `TOP (1)` scalar subqueries on the customer runs them 20,000 times for 8 customers (~27 ms), where a memo keyed by the consulted outer values would run them 24 times;
  and a leftmost uncorrelated derived table inside a per-row body (`EXISTS (SELECT 1 FROM (SELECT TOP (100) … ORDER BY …) o1 WHERE …)`) re-sorts per outer row until the semi-join switch (~40 ms), because its consumer may stop early and the statement-scoped reuse the non-leftmost slots take only stores a fully drained source.
  The first pass's per-row ordered seek (`CappedCandidates`) didn't rank in these six classes.

### Built-in functions

Captured from a Microsoft Learn category-by-category audit; re-fetch <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the function surface complete.

- **ML scoring** — `PREDICT(MODEL = …, DATA = …)`, blocked on the unmodeled `PREDICT` surface it belongs to (shipping the function implies the parent ships too).
- **System stored procedures** (`sp_*` family) — the shipped set is listed in [`catalog-views.md`](catalog-views.md) and under [System procedures over the registries](catalog-views.md#system-procedures-over-the-registries); any other procedure still raises Msg 2812 — the ones a differential sweep met are listed there, beside `sp_describe_first_result_set`'s and `sp_describe_undeclared_parameters`' residue.
  Not modeled yet: internal-table rows in `sp_updatestats` / `sp_createstats`, login SIDs honouring `@sid`, `sp_password` and `ALTER LOGIN … WITH PASSWORD` on `sa` past the policy check ([`permissions.md`](permissions.md#not-modeled-yet)), real's system message rows, and the message language of the session for Msg 2786 / 2787 / 5703 wording.
  A broad surface — each proc is its own result-shape contract over the catalog views — that ships piecemeal by popularity, not as a bundle.
- **SQL Server 2025's vector residue** — a vector parameter from a vector-aware client over RPC, vector columns in BACPAC import, the later vector-index version, and a cascading `UPDATE` into a vector-indexed table → [`vector.md`](vector.md#not-modeled-yet).
- **SQL Server 2025's `json` residue** — a JSON index's internal table and json columns in BACPAC import → [`json-type.md`](json-type.md#not-modeled-yet).

Low priority / niche — simulatable (as placeholder constants or a small model) but rarely hit, so not worth attention yet:

- **Settled — don't re-pitch:** `msdb.dbo.syspolicy_configuration.current_value` stays `nvarchar` — it's a *view-body* projection (not a resource column) mixing `int` rows with a `binary` GUID row, every consumer reads a single named row and CASTs it, so a variant migration there would only touch the view SQL text for no observable gain.
- **Graph residue** — a `SHORTEST_PATH` repeating more than one hop or starting from a derived source, two in one WHERE, `LAST_NODE(x) = LAST_NODE(y)`, the `NOCHECK` / `CHECK CONSTRAINT` toggle over an edge constraint, and the smaller divergences — see [`graph.md`](graph.md#not-modeled-yet).
- **Dynamic Data Masking residue** — a masked `geography` / `geometry` / `vector` value raises `NotSupportedException` (real sends a single `0x00` byte its own client can't read back) — see [`data-masking.md`](data-masking.md#not-modeled-yet).
- **`INSERT … VALUES … OPTION (RECOMPILE)`** is accepted on real and Msg 156 here → [`query-hints.md`](query-hints.md#option-clause).
- **Ownership residue** — `ALTER AUTHORIZATION` on the classes the simulator doesn't carry (certificates, keys, Service Broker, endpoints …), and the assembly gate's divergence (it asks at database scope because `GRANT` carries no assembly class) → [`permissions.md`](permissions.md#ownership).

## Over-permissive register

The simulator accepting what real rejects is the more dangerous divergence direction — the query passes here and fails in production — and it is invisible to any sim-only failure list (see the reverse delta under [other differential oracles](sqllogictest.md#other-differential-oracles)).
This is the standing list: each entry names the error real raises that the simulator doesn't, and the linked deep-dive carries the detail.
Entries are verified against the simulator, so one that no longer reproduces is removed rather than re-worded.

- **Non-Framework CLR assemblies load** — real resolves every `AssemblyRef` against a fixed .NET Framework catalog and raises **Msg 6503** otherwise (probe-confirmed for .NET 10 and for .NET Standard 2.0); the simulator runs on .NET so all of them bind, which is also what lets the tests emit a fixture assembly without a Framework toolchain → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **`clr strict security` is a `sp_configure` option nothing reads** — real refuses `CREATE ASSEMBLY` of an unsigned SAFE / EXTERNAL_ACCESS assembly with **Msg 10343** while the option is 1; the simulator registers and validates the option but never consults it → [`clr-assemblies.md`](clr-assemblies.md#not-modeled-yet).
- **A CLR aggregate whose `IBinarySerialize` drops a field answers here** where real may lose it, since one in-memory instance accumulates each group and `Write` / `Read` / `Merge` never run → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **A scalar function whose *column* no longer exists inlines here**, where real fails to inline it and sends its binder report twice (probed 2026-09-30 against SQL Server 2025) → [`programmable.md`](programmable.md#inlining-a-call-as-the-query-compiles).
- **Index option names in the column-level clauses** — a constraint or inline index in `CREATE TABLE` / `CREATE TYPE` / `ALTER TABLE … ADD` accepts any option name, where real refuses an unknown one → [`indexes.md`](indexes.md#fidelity-gaps).
- **A subquery's `GROUP BY` item naming only the enclosing query's column** runs here where real raises **Msg 164** → [`query.md`](query.md#aggregate--group-by-binding-rules).
- **A CHECK calling a function that reads its own table** admits a row real refuses with **Msg 547**, since the check runs before the row lands → [`constraints.md`](constraints.md).
- **A variable in a `CREATE TABLE` column's `DEFAULT`** is accepted where real raises **Msg 112** → [`alter-table.md`](alter-table.md#fidelity-gaps).
- **An outer reference to a `.nodes()` row column** from a nested query reads the row's reference text where real raises **Msg 493** → [`xml.md`](xml.md#not-modeled-yet).
- **A schema `DENY ALTER` doesn't stop a database `CONTROL` holder's `DROP TABLE`** there, where real raises **Msg 3701** → [`permissions.md`](permissions.md#known-gaps).
- **A `numeric` column on a `decimal` partition function** (or the reverse) is placed here where real refuses it with **Msg 7726** → [`partitioning.md`](partitioning.md#divergences).
- **Typed XML admits what its declaration refuses** — a fragment in an `xml(DOCUMENT …)` column or variable (real's **Msg 6901**) and an XSD that doesn't compile (real's Msg 2308 for an undefined type in the schema namespace) → [`xml.md`](xml.md#typed-writes--validation-and-canonical-form).

The opposite direction — the simulator refusing what real accepts — includes the deterministic nesting caps, which sit below real's stack-dependent thresholds (paren 500 and subquery 83 against real's 1015 / 168, deep function nesting surfacing Msg 8631 rather than Msg 191 on tight threads) to keep Msg 191 firing with headroom before the stack probe; closing that wants a slimmer function-argument recursion frame → [`grammar.md`](grammar.md#expression-depth-limits-msg-8631--msg-191--msg-125).

## Live-but-untested surfaces

Reachable code paths no test exercises, from a coverage run (`dotnet test --collect:"XPlat Code Coverage"` → reportgenerator) — not gaps in behavior, gaps in the safety net.
The uncovered `DebugDisplay` / `ToString` debugger helpers and `Stream` boilerplate overrides are deliberately not worth testing; what remains is this list.

- **`sp_cursorprepare`** (`TdsSession.CursorPrepare`) — SqlClient issues the `sp_cursoropen` family instead, so reaching it needs a driver that prepares a cursor explicitly.
- **`ClrAssemblyMetadata.ComputePublicKeyToken` / `DescribeReference`** — strong-named assembly identity and the assembly-reference description.
- **`LikeMatcher.Cache`'s collation key component can't be varied by any test** — the resolved collation is a per-expression-node constant in every shape reachable through SQL, so mutation-testing it catches nothing; it guards a hypothetical future caller that rebinds a node's collation between executions.

Worth re-measuring after a large bundle rather than routinely, and worth acting on when it does run: the first pass surfaced no merely-untested-but-correct path.
It found two pieces of dead code, and every gap that was then covered turned out to be hiding a behavior bug — uncovered code here has consistently meant *wrong* code, not just unwatched code.

## Design choices to revisit

Queued structural refactors (behavior-neutral, one per commit, each proven by the full gate plus a timing A/B):

- **`Selection`'s long phases** (`ParseSingleFromSourceCore`, `ParseQueryBlock`, `BuildSqlProjection`, the aggregate builder) split opportunistically, when feature work next touches them — they sit on measured hot paths, so each extraction takes a perf A/B.

Settled design choices:

- **`APPROX_COUNT_DISTINCT` counts exactly — settled, don't re-pitch.**
  Real's answer is a 4096-register HyperLogLog estimate, but its hash isn't identifiable from query outputs and its answer varies between batch- and row-mode execution of one input, so real itself guarantees no particular estimate and exact is the one answer within its error everywhere → [`query.md`](query.md#the-approximate-aggregates).

## Won't-model / explicitly excluded

Excluded on **correctness**, not priority: these are cloud-only surfaces the SQL Server 2025 RTM box product itself rejects, so modeling them would *diverge* from the box-product fidelity oracle.
Don't re-surface as candidates (unless a future box release promotes one).

- **ANY_VALUE(expr)** — Azure/Fabric-only, not in the box product (probe-confirmed, re-checked 2026-10-03 against SQL Server 2025 CU7).
  With it excluded, the **Analytic** category is complete for the box product (CUME_DIST / PERCENT_RANK / PERCENTILE_CONT / PERCENTILE_DISC all ship).
- **SESSION_ID()** — dedicated-SQL-pool / cloud surface; the box raises Msg 195 (probe-confirmed, re-checked 2026-10-03).
  `@@SPID` is the box session-id mechanism.
