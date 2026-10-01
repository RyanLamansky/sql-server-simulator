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

An entry marked **Deferred until requested** is parked by the user's call, not by cost: it stays listed so someone looking for it finds its findings, but isn't picked up autonomously — only when a user asks for that behavior.
Non-Latin collation work (the East Asian families, legacy and hidden collations, non-Latin code pages) is deferred this way as of 2026-09-29; the common Latin collations and code page 1252 stay active.

This file is the home for net-new non-function feature proposals too.
CLAUDE.md's **Not modeled yet** section is the complementary *descriptive* map (what raises `NotSupportedException` / Msg today, so the surface isn't over-promised); this list is the *prospective* one.
An item can appear in both with opposite intent.

## Missing features

### Unbuilt feature areas

A standing survey of areas with no build behind them, one line each pointing at the deep-dive that holds the detail.
Presence here is status, not a priority claim — the ordering caveat at the top of this file applies.
The subsections that follow carry the areas with work in flight.

- **Full-text linguistic residue** — the query pipeline ships with real's English word breaker, fitted to `sys.dm_fts_parser` and measured identical on 99.5–99.9% of three corpora's inputs, the matching rules its output drives, every language's system stoplist, and `sys.dm_fts_parser` itself (see [`full-text.md`](full-text.md#the-query-pipeline)).
  What remains is the languages other than English, whose breakers carry locale rules (German's decimal comma and apostrophes, its month names) and whose morphologies expand through their own paradigms (Spanish `hablar` to some 270 forms), which the simulator neither breaks nor stems; a populated **thesaurus** and `sys.sp_fulltext_load_thesaurus_file` (real's default one is empty, which is modeled); the index keyword DMVs; the part-of-speech lexicon real's inflection listing consults; and the last stemming shape a single-stem model can't hold — a surface form spanning two lemmas, where real expands `leaves` to `leaf` *and* `leave` and the simulator picks one.
  Also unbuilt: `SEMANTIC*` rowsets (`NotSupportedException`), and `RANK` values, which are the simulator's own (see the divergence note in [`full-text.md`](full-text.md#rank)).
- **Spatial evaluation** — the value model, **all three measures for both spatial types**, the **whole topological surface of both** and the **constructive operations** all ship: `geometry`'s eight predicates plus `STRelate`'s DE-9IM matrix, `geography`'s six over a round-earth engine of the same shape, `STIsValid` for each, the Msg 24144 gate an invalid instance puts on most instance methods, and the set operations, envelope, hull, boundary, buffers, `Reduce`, `MakeValid` and the four spatial aggregates on real's precision grid (see [`spatial.md`](spatial.md#topological-predicates-the-de-9im-engine), [round-earth topology](spatial.md#round-earth-topology-geographys-predicates) and [constructive operations](spatial.md#constructive-operations)).
  The round-earth measures work along the *great elliptic arc*, which is the curve real uses and not the geodesic — length, area and the closest approach between any two shapes (see [`spatial.md`](spatial.md#round-earth-measures-the-great-elliptic-arc) for the derivations and the residuals against real).
  The derived-point members each type carries alone ship too — `geometry`'s `STCentroid` / `STPointOnSurface` / `STIsSimple` and `geography`'s `EnvelopeAngle` / `EnvelopeCenter` — as does the **property form of a spatial column** (`Location.Lat`), decided against the query scope with real's Msg 326 where both readings bind.
  The **curved kinds** and **`FULLGLOBE`** ship as values and members for both types — version 2 of the serialization, the curve WKT grammar and well-known binary records, exact planar arc measures and closest approach, real's linearization rules, `BufferWithCurves` of a point and `geography`'s `EnvelopeAggregate` (see [`spatial.md`](spatial.md#curved-shapes)).
  What remains:
  the **curve-producing results** — real answers `STConvexHull`, `STBoundary`, the set operations, `MakeValid` and `BufferWithCurves` over curves with arcs of its own, which the simulator answers linearized — and `IsValidDetailed()`'s report on an invalid instance; the **vertices of `geography`'s line and polygon buffers** (real's area, not real's outline — its curve construction's side and ring-start rules are open), `geography` operands no cap narrower than 89.5° holds, and the order of a `geography` result spanning tens of degrees (line directions and a complemented result's ring starts);
  the **last digits of an oblique crossing** — real's floating-point crossing arithmetic on the grid is pinned for axis-aligned edges and 78% of oblique ones, which leaves roughly one set-operation result in ten a unit or two off in the last place, and the **buffers' last digits**, whose vertex layout matches real's;
  the **lobe split for planar validity** — real accepts a ring that revisits one of its own vertices when the second lobe nests inside the first with the opposite winding, which the round-earth validator reproduces and `SpatialValidator` does not, so the two disagree on such a ring (one WideWorldImporters border carries the arrangement) — and a handful of near-retrace lines whose validity on real the grid offset doesn't predict;
  and a **`geography` `STRelate`** oracle — the round-earth engine computes the nine cells but real exposes no `STRelate` there, so only the six predicate masks are checked against a reference.
  Also open: `STPointOnSurface`'s pick for a polygon **with a hole** (real bridges the hole into the ring before clipping the ear; the simulator falls back to a scanline point, which is on the surface but not real's), the property form at a **scope-less site** (an UPDATE's SET list, a CHECK constraint, a computed column), GML, SRID transformation, `sys.spatial_reference_systems` seed rows, `ALTER SPATIAL INDEX`, and query-planner use of the spatial index → [`spatial.md`](spatial.md#not-modeled-yet).
  One metadata cell surfaced during the round-earth predicate probes and left unbuilt: `MinDbCompatibilityLevel()` answers **110** for an *invalid* instance on real (100 for a valid one) where the simulator answers 100 always.
- **`WRITETEXT BULK` / `UPDATETEXT BULK`** — the statement forms ship (see [`legacy-lob.md`](legacy-lob.md)); the `BULK` keyword raises `NotSupportedException` because real's bulk form is a bulk-copy data stream fed over the wire rather than a statement, which a normal client can't issue at all (real answers **Msg 185**, `Data stream is invalid for WRITETEXT statement in bulk form.`, probed 2026-08-06).
  Closing it means a TDS bulk-load path keyed to a text pointer rather than a table.
- **XQuery residue** — the constructors, the named axes, the `xs:` constructor functions, `cast as` / `instance of`, the node comparisons, the `sql:` accessors in every method, arithmetic typed by its operands, a schema collection's static types and `.modify()`'s whole-expression insert content all ship ([`xml.md`](xml.md#the-xquery-subset)).
  Open: an outer reference to a `.nodes()` row column from a nested query isn't refused with real's Msg 493, the context item inside a predicate stays untyped, numbers compute in `double` (an integer past 2⁵³ loses digits, and the decimal scale rule is six digits everywhere), and dynamic XQuery → [`xml.md`](xml.md#not-modeled-yet).
- **Typed-XML residue** — `xml(DOCUMENT …)` on a column or variable still admits a fragment (the `CAST` target enforces it), and an XSD that doesn't compile is accepted at `CREATE` / `ALTER … ADD` where real refuses it (an undefined type reference is its Msg 2308) → [`xml.md`](xml.md#typed-writes--validation-and-canonical-form).
- **Cursor residue** — keyset identity (the clustered key and its uniquifier stand-in), key-order navigation, FAST_FORWARD, the declaration refusals, cursor OUTPUT parameters, the `sp_describe_cursor` family and `sys.dm_exec_cursors` ship ([`cursors.md`](cursors.md#what-a-keyset-member-keys-on)).
  Open: `sys.dm_exec_cursors` rows for API server cursors and for another session's local cursors, asynchronous keyset population under a non-default `cursor threshold`, the API cursor flag protocol past the plain types (PARAMETERIZED_STMT, AUTO_FETCH, CHECK_ACCEPTED_TYPES → [`tds-endpoint.md`](tds-endpoint.md#api-server-cursors-sp_cursor-rpc-family)), and a FAST_FORWARD `DISTINCT` real's optimizer drops over a key → [`cursors.md`](cursors.md#not-modeled-yet).
- **Row order without ORDER BY, where real's plan decides it** — windows, sort-fed grouping, DISTINCT, ROLLUP and the deduplicating set operations follow real's sort ([`query.md`](query.md#row-order-without-order-by)); open are the order among rows real's unstable sort ties, `CUBE` / general `GROUPING SETS`, the constant-only set operations real merges anyway, and a join's order, which follows real's cost-based choice of driving input ([`joins.md`](joins.md#row-order-of-a-join)).
  The sqllogictest oracle sorts `rowsort` records before comparing, so none of it shows there as a divergence.
- **Key locks follow the simulator's access path, not real's optimizer's** — a SERIALIZABLE read locks the keys of the index the seek cache chooses, where real may scan a small table and lock every key; a nonclustered read takes its lookup row S even when the index covers the query; and READ COMMITTED's lock avoidance is judged per heap rather than per page → [`locking.md`](locking.md#divergences).
- **Write-locking residue** — a written row's index keys show in the lock DMVs beside its row lock, writers read their target under U — the joined UPDATE / DELETE forms, a write through a join view, a joined write through a view or CTE and a MERGE into a join view included, and a row another session's in-flight delete or rewrite hid from the walk waited out — `IGNORE_DUP_KEY` checks take real's key ranges, and a statement redefining a table waits out its open writers ([`locking.md`](locking.md#a-writers-target-read)).
  Open: what the lock DMVs show differs where real holds the joined partner's S, or the index key's U on a nonclustered seek, while it waits, or waits on that index key itself, and where a MERGE into a join view keeps U on rows it doesn't write; a second writer of a key holds nothing on its own new row while it waits, since the simulator checks before it writes; and key descriptions print the key where real prints a hash → [`locking.md`](locking.md#a-writers-target-read), [`locking.md`](locking.md#writers-racing-to-one-new-key).
- **Server permissions whose statements aren't built** — every modeled server-scope statement is gated and every stored server permission enforced, with the fixed server roles carrying real's grants; what remains is catalog truth only because its statement is unbuilt (`SHUTDOWN`, bulk operations, credentials, endpoints, event sessions, audits, traces, the error log) or waits on `clr strict security` (`UNSAFE ASSEMBLY`), plus `fn_my_permissions` for the database-scope classes → [`permissions.md`](permissions.md#not-modeled-yet).
  `ON SERVER::` / `ON LOGIN::` securables and application roles ship.
- **Partitioning residue** — functions, schemes, `$PARTITION`, placement, the per-partition catalog, partition `TRUNCATE` and `SWITCH` ship with rows assigned logically rather than stored apart.
  Open: an indexed view's index placement, a `FILESTREAM` column, LOB data spilling onto a filegroup without files (Msg 622), per-partition data compression, `SELECT … INTO … ON`, partition-level lock escalation, and SWITCH's filegroup / index-alignment checks → [`partitioning.md`](partitioning.md#not-modeled-yet).
- **Query Store residue** — capture, the runtime statistics, lock waits and all eleven `sp_query_store_*` procedures ship ([`database-options.md`](database-options.md#query-store)).
  Open: size-based and stale-query cleanup and `MAX_PLANS_PER_QUERY`, module-relative offsets and module context settings, the compile-CPU capture threshold, and applying a forced plan or a hint → [`database-options.md`](database-options.md#not-modeled-yet).
- **DML plan residue** — `INSERT … VALUES`, the single-table `UPDATE` / `DELETE` and a `MERGE` from a `VALUES` list or a table cache a plan per statement and replay it inside any batch, `OUTPUT … INTO` a table variable or table included, so every EF Core `SaveChanges` shape replays ([`plan-cache.md`](plan-cache.md#dml-statement-plans)).
  Open: `INSERT … SELECT`, a `MERGE` from a query or through a view, the joined forms, DML through a view, a statement holding a subquery, and a principal permission checks apply to → [`plan-cache.md`](plan-cache.md#not-modeled--future).
  **Benchmark note**: naive in-process A/B here is worthless — measuring the cases in one process made results order-dependent by up to 2× (whichever case ran first absorbed tiered-JIT warmup; "fixed text" read 28.3 µs first and 14.5 µs last). One case per process is the only shape that reproduced.
  Warm-up is also longer than it looks: a single-row `UPDATE` batch read ~100 µs after 3,000 iterations and 12 µs after 100,000, so a run warms by elapsed time (seconds), not by an iteration count.
  How a benchmark resets its table between iterations is part of what it measures: `DELETE` leaves dead pages an insert's reuse walk visits and `TRUNCATE` doesn't, and a table that just grows makes any target scan grow with it — so measure the shape you mean, and say which.

### TDS network endpoint — follow-up phases

The endpoint ships with SQLBatch + RPC + Transaction Manager support and credential enforcement via the `CREATE LOGIN` registry (see [`tds-endpoint.md`](tds-endpoint.md)); EF Core runs over the wire through vanilla `UseSqlServer`.
Remaining phases, roughly in value order:

- **Tool shakedown** — point real client tools at the endpoint and harvest their exotic catalog queries / SET shapes into this backlog.
  Tool scope (user decision): tools a SQL Server + .NET developer already has — SSMS, sqlcmd, Visual Studio (SQL Server Object Explorer / DacFx), LINQPad; DBA-flavored tools like DBeaver are out of scope.
  **SSMS is the final boss** — an ongoing campaign, not a single leg: each surface is its own multi-round harvest, and clearing one unlocks the next.
  Cleared legs are recorded in the per-feature deep-dives (catalog surface in [`catalog-views.md`](catalog-views.md), wire behavior in [`tds-endpoint.md`](tds-endpoint.md)); the discovery harnesses are the gitignored `.vs/ssms-host` TDS host and the headless SMO property-bag drain.
  **Remaining frontier**: Table Designer, Activity Monitor, standard reports, and IntelliSense's background metadata harvest.
  Candidate follow-on legs within tool scope: Visual Studio's SQL Server Object Explorer (DacFx-driven, a different query dialect from SMO) and LINQPad.
- **SMO API sweep campaign** — `.vs/smo-sweep` (gitignored local harness) walks SMO's full reachable read surface against the self-hosted simulator and, identically, against the local SQL Server 2025 reference, draining every `Property.Value` and `Script()`-ing every `IScriptable`; modes `sweep` / `sweep --live` / `diff` → sorted JSON reports + `reports/triage.md` (which sorts every non-matching row into bug / unbuilt / environmental / documented-divergence); workflow = sweep both sides → triage → fix bundles → graduated tests → re-sweep.
  The walk never fetches a shipped module's T-SQL (its `Text`-family properties and `Script()` are skipped on both sides), keeping the harness black-box.
  Open items from the 2026-09-30 sweep, every simulator bug it found fixed:
  - **`DBCC SHOW_STATISTICS … WITH STATS_STREAM`** (SMO `Statistic.Stream`, 142 properties) stays `NotSupportedException` — it wants the raw serialized statistics blob, which the simulator has no faithful source for.
  - **The shipped modules' own T-SQL** isn't carried: `sys.system_sql_modules.definition` is NULL, so SMO reads each system view / procedure / function as encrypted (305 `IsEncrypted` properties) and won't script one or read its `Text`.
  - **Most system objects are unmodeled** — SMO lists 1,388 procedures, 421 views and 141 functions the reference ships that the simulator doesn't (44,046 properties).
  - **No system base tables in `sys.partitions` / `sys.allocation_units`**, so a database with no user table sums to NULL there and SMO's `DataSpaceUsage` / `IndexSpaceUsage` / `SpaceAvailable` raise `InvalidCastException` (9 properties: master, model, tempdb — and any empty user database).
  - **msdb's Service Broker seed objects** — SMO's `IsMailHost` finds no `InternalMailService` in `sys.services` (1 property).
  - **Storage accounting**: a LOB-eligible value always takes an off-row LOB page, where real keeps one that fits in row, and every nonclustered index reports its heap's page count, so the sizes SMO reads run 10–90× the reference's (WWI `Application.Cities` data space 306,472 KB against 3,400).
  - **DacFx's import naming** — a bacpac import keeps `CREATE DATABASE`'s file names where DacFx names the primary pair `<db>_Primary.mdf` / `.ldf`, and index ids follow the loader's statement order rather than DacFx's.
  - **`xp_instance_regread`'s Msg 22001** informational message for a value it doesn't find isn't sent.
- **Browse mode residue** — `KeyInfo` / `SET NO_BROWSETABLE` ship over the wire (hidden key and rowversion columns, TABNAME / COLINFO — see [`tds-endpoint.md`](tds-endpoint.md#browse-mode-commandbehaviorkeyinfo)); what's left is real's flattening of a derived table or view into its base tables for that metadata.
- **MARS requests don't interleave** (details in [`tds-endpoint.md`](tds-endpoint.md#mars-multiple-active-result-sets)): real streams a request's results as the client reads them, so a reader still draining a SELECT sees a second request's writes to the rows it hasn't reached; the simulator runs each request whole under the execution gate, which also bounds a session's response memory by the whole result.
  Closing it means suspending an engine enumeration mid-statement while another request runs on the same connection — the one-executor-per-connection contract the gate exists for.

### Complex-query execution — perf residuals

The complexity batteries (`.vs/workload` `compare` subcommand + `complex*.sql`, local-only) are the standing measurement instrument; sub-4× ratios against the live reference are constant-factor or parallelism territory, larger ones name a missing execution strategy.
Open residuals, in measured-impact order:

- **Catalog introspection's remaining per-statement cost** — with the cross-statement catalog row cache ([`catalog-views.md`](catalog-views.md#cross-statement-row-cache-and-indexes)), SMO's per-table column-properties query runs at ~3.6× the live reference (59 ms against 17 ms over 30 tables, measured 2026-09-28) and its whole-database table-properties query at ~2.3×, while the rest of the introspection battery runs at or below live.
  The catalog work is gone from those profiles; what remains is ordinary per-statement cost — resolving and projecting forty-odd columns across seventeen joined sources — so it is constant-factor territory shared with every wide join.
  Catalog-specific leftovers: `sys.partitions` and `sys.sequences` aren't cached (their DML-moved state has no cheap stamp, where `sys.identity_columns` has one), so a statement joining them regenerates them; and the transitive seek hop is one level.
- **DML through a join view bypasses the joined-source passes** — `Simulation.JoinViewDml.cs` enumerates without the materialization/narrowing preparation `UPDATE`/`DELETE` take, and the **narrowed-source-first reorder is declined for every DML statement**; its WHERE names view output columns resolved through the chain resolvers, so wiring it is a name-resolution correctness question before a perf one.
- **A fan-out-aware semi-join crossover was built and measured out** (2026-08-05) — **don't re-pitch it as stated.** The seekable-inner delay (`evaluations × 4 > innerRowCount`) does assume each key selects about one row, and the seek cache's bucket count is a free estimator for the real fan-out; both were implemented — a key-count probe on `HeapSeekCache` plus an ordinal carried on `SemiJoinShape` — and neither helped, so the probe was reverted and only the ordinal remains.
  Two reasons, each measured against a control binary: the shape the entry named — `delete.exists_73k` — correlates on `o.OrderID`, `Sales.Orders`' **primary key**, so its fan-out is 1 and the guard is unchanged (96.40 med / 51.54 min with the estimator, 91.52 / 46.22 without, `compare wwi 9`); and where fan-out genuinely is high, the build loses anyway — `corr.not_exists` (663 Customers over 73k Orders on `CustomerID`, fan-out 111) is exactly what the estimator flips onto the build and it regresses from **40.31 med / 38.51 min (2.5×) to 45.31 / 39.57 (3.1×)**.
  The model the entry rested on is missing an asymmetry: the per-`Heap` seek cache **persists across executions** while the decorrelated build re-runs every statement, so a per-row seek is cheaper than its row count says.
  A crossover that improves on the constant has to price that in, not just the fan-out.
  Real picks a set-based operator for every one of these shapes (probed 2026-08-05 with `SET STATISTICS XML ON`: a **Merge Join** for the two ordered-key semi-joins, a **Hash Match / Left Anti Semi Join** for the 663-row-outer one the simulator keeps per row, and a **Nested Loops** over the seeks for the small-`IN` drive side).
- **`COUNT(DISTINCT …)` per group over a big join sits at ~1.9× live, and build-side choice is not the lever** — **measured out 2026-08-05, don't re-pitch.** Building the hash over the smaller side was implemented (INNER at fold level 1, both sides un-narrowed base tables, probe side ≥ 4096 rows and ≥ 2× the build side) and moved nothing: building over WWI's 70,510-row `Sales.Invoices` instead of the 228,265-row `Sales.InvoiceLines` measured **102.2 ms min against the control's 97.4**, with allocation unchanged at 74 MB — both arrangements compute one key per row of *both* sides, and that is the cost the build's row-list appends hide behind.
  The query decomposes (`bench wwi 6`, min ms) as **10.6** for a bare 228k-row scan, **49.3** for the two-table join, **81.4** with the `GROUP BY`, **102.5** with the `COUNT(DISTINCT)` — so the join is 48%, grouping 31%, the distinct sets 21%, against live's 54 ms at DOP 8.
  What's left is the intra-query-parallelism residual below plus the grouping path, not the join strategy.
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
- **Intra-query parallelism is the systematic remainder** — the scan-bound shapes that survive every constant-factor pass (`daterange.*`, `conditional.agg_pivot`, `window.three_sorts`, `union.dedup_big`, the year-aggregating reports) sit at 1.5-3.5× live with real running DOP 8, and profiling shows the simulator using *less CPU* than real on several of them — the gap is parallel execution, not waste.
  An unprofiled small neighbor: a `TOP (1000)` ordered by the PK over a wide table sits ~3× (`format.heavy_1000`).
  **Covered so far**: the streaming single-grouping-set aggregate path's per-row *consumer* work forks across worker threads while the calling thread produces the stream — built, tested and measured at 1.2-2.4× on a single-session battery, and **off by default** because the concurrent workload driver loses 25-30% throughput from even a handful of forks (see [`query.md`](query.md#parallel-grouped-accumulation-built-proven-off-by-default) for the gates, the merge contract and the error rule).
  **What that leaves.** Three things, in the order they are worth doing:
  1. **The residual process-wide cost of forking at all** — the AdventureWorks driver loses its throughput across a phase in which the fan-out never engages again, so the cost outlives the forked statements. Thread lifetime (a retiring pool; a 1 ms idle timeout) and block-allocation size were both ruled out by measurement. Until this is explained the default cannot flip, and explaining it is the whole gate on the rest of the work.
  2. **The producer is still serial**, so Amdahl bounds every join-fed aggregate: a bare `COUNT(*)` over the 228k-row `Invoices ⋈ InvoiceLines` join costs ~70 ms on its own. Parallelising the join itself means building the hash once and partitioning the probe side, which is a change to the join driver rather than to the aggregate path.
  3. **The shapes the pilot doesn't reach**: an ordinary (non-aggregate) projection, `DISTINCT` and the set-operation dedup, the window executor's sorts, and a high-cardinality `GROUP BY` (the per-worker maps' merge is what makes 73k groups lose). Each needs its own merge argument; none inherits the aggregate path's.
  **Scheduling (user direction, 2026-08-05): none of the above proceeds until the easier algorithmic wins elsewhere in this section are exhausted** — every "parallelism territory" shape profiled so far hid an algorithmic or constant-factor win under the label, and the MAXDOP-1 comparison confirms most residual ratios are constant factors at equal threading.
  Item 1's unexplained fork cost is additionally the hard gate on flipping the pilot's default, whenever the topic reopens.
- **Two `NEWID()` placements still differ from real** — a merged body's drawn column that reads a body column (`CAST(b AS varchar(3)) + CAST(NEWID() AS varchar(36))`) can't be re-drawn outside the body, so the leftmost slot keeps the body's draw; and an `APPLY` body `TOP 1 … ORDER BY` a non-drawn column draws once on real but per row here — see [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration).

### sqllogictest differential sweep — surfaced gaps

The oracle itself — corpus, sharded runner, how to re-run and re-read it, and the traps that silently invalidate a run — is documented in [`sqllogictest.md`](sqllogictest.md).
Nothing from it is checked in; it regenerates in minutes.

Standing measurement on the 391-file `random/` slice (5,295,251 records, 16 shards, ~460 s): **5 divergent records** (3 × Msg 8134, 2 × Msg 8115), all of them the plan-dependent residue below.
The count was 26 until the constant-fold fixes in `05f71c5`; every run since reports 5, replay included.
Re-run it after any bundle touching the parser, the expression evaluator or the type system.

Still open from what it surfaced:

- **The top-level `select1`–`select5` scripts have no captured reference.**
  `select5`'s many-way comma joins, which once kept it out by timing out, now answer (see [`joins.md`](joins.md#join-order-reorder)), and a differential run of `lists/pilot-phase1.txt` (`select1`–`select5` plus `evidence/`) had every query agree with real (2026-09-27, SQL Server 2025).
  The routine replay covers `random/` and `evidence/`; the whole `index/` slice agreed with real record for record in the same week and has its own reference, replayed separately because it takes longer than the routine replay.
- **What probing around the `evidence/` capture's one divergence found, and didn't close** — the quantified-call family and the delimited one-part call ship with their error positions; the stray-`(` neighbors left open — a `(` after an `ORDER BY` / `GROUP BY` name, a `DELETE` target's state 31, a delimited call as an `sp_executesql` argument, and recovery's Msg 422 past a `(WITH …)` statement — are in [`grammar.md`](grammar.md#divergences).
- **Every statement is parsed twice**, once by the batch-compile walk and once to run.
  Replaying `index/in/1000` single-threaded (measured 2026-09-27), the walk is 8.8% of process CPU — 7.8 points of it parsing the SELECT, tokenizing already shared with the run — and skipping it outright bounds the win at 13.0 → 12.1 s.
  Its texts are all unique, so `compiledBatches` never hits, and parameterized workloads (WWI) reach the plan cache before the walk runs, so a wider cache recovers nothing measured.
  Reusing the walk's `Selection` for the run isn't sound as-is: skip mode takes `DataLockPlan.Bypass`, and permission checks, FROM-less evaluation, placeholder sources and the `TOP (@p)` declared-type check all differ under it.
  Skipping the walk for a one-statement batch isn't a shortcut either: a compile error surfaces at `ExecuteReader` where a run-time one surfaces at `Read()`, and the batch-abort rules differ.
  The prerequisite for any real win is moving those parse-time run-only effects into an execution step, so that a skip-mode plan equals the run's.

**Five sweep divergences remain, each demonstrated irreducible** — real's own answer flips under something the simulator cannot legitimately model, so matching them would mean modeling plan selection rather than semantics.
Two are the trivial-plan boundary: `WHERE <overflow> <= 18 / CAST(NULL AS int)` raises as written, and answers 0 rows the moment `DISTINCT`, `GROUP BY`, `TOP 2` or a join is added — while `ORDER BY` / `MAX()` / `COUNT(*)` leave it raising.
One is written order inside an un-negated `IN` list (`x IN (x/0, x)` answers, `x IN (x, x/0)` raises — same elements).
Two are per-row short-circuiting that flips with the *data*, not the text (a row satisfying the cheap conjunct makes real raise in both written orders).
The same comparison in a **`HAVING`** folds unconditionally — a HAVING always carries a grouping, so it never gets the trivial plan — which is why that position is closed and `WHERE` is not.
Precisely-scoped list in the "Not folded yet" section of [`query.md`](query.md).

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

- **A `decimal` beyond .NET `decimal`'s range — closed.** `model_fields.test_decimalfield`'s `max_digits=38` model surfaced as `SqlServerSimulator: unhandled OverflowException` (Msg 50000); the exact-numeric type carries all 38 digits, so the value computes and stores, and the only remaining narrowing is the reader's, which is SqlClient's own shed-or-`OverflowException` rule — see [`arithmetic.md`](arithmetic.md#the-backing-type).
- **Reverse delta: `expressions_window` — closed** (2026-08-05), and the filed diagnosis was wrong.
  `test_fail_update` has nothing to do with it: the poisoner is `test_key_transform`, whose `SUM(…) OVER (PARTITION BY <JSON_VALUE> ORDER BY <JSON_VALUE>)` orders a RANGE frame by an `nvarchar(max)` expression.
  Real answers **Msg 8728** and rolls the whole transaction back, so Django's next `ROLLBACK TRANSACTION <savepoint>` fails (Msg 6401) and `needs_rollback` sticks for the rest of the class; the simulator ran the query.
  Msg 8728 and its transaction-aborting semantics now ship ([`query.md`](query.md#range-frame-order-by-msg-8728), [`transactions.md`](transactions.md#the-transaction-aborting-error-class)), along with the Msg 6401 that used to be Msg 102 and the TDS transaction-state repairs behind it.
  Re-measured over the wire: `expressions_window` is **0 sim-only and 0 real-only**, 27 failing identically on both.

Re-measured 2026-09-26 on the same 35-app slice after a busy stretch of fidelity work: **0 sim-only, 0 real-only**, 72 failing on both — the one sim-only failure on the way there (`bulk_create.test_bulk_insert_nullable_fields`) was the missing `sp_describe_undeclared_parameters`, which pyodbc uses to type a `None` bound for a `varbinary` column.

Widened 2026-09-30 to a 132-app slice (**9,295 tests**, the apps listed in the harness's `apps.txt`) weighted toward the ORM, migrations, fixtures, serializers, transactions, admin and the model / relation apps, with `schema` (219 tests) run as its own batch: **0 sim-only, 0 real-only**, 154 failing on both, and `schema` 0 / 0 with 29 on both.
Per batch on the way there: the query apps (1,247 tests) 0 / 2 / 43, the model and relation apps (671) 0 / 0 / 2, fixtures to caches (2,909) 0 / 0 / 19, migrations with backends and introspection (1,086) 2 / 2 / 13, `schema` 1 / 13 / 16, admin and views (1,414) 0 / 0 / 4.
`many_to_one_null` and `m2m_through_regress` stay out: their `to_field` onto a nullable unique column fails migration on both sides (Msg 1776, mssql-django's filtered unique index being no candidate key), and `get_or_create.UpdateOrCreateTransactionTests.test_creation_in_transaction` is a thread-timing race that fails on both in different ways.
The roots, each now matching real:

- **A subquery's reference to a grouped query's ungrouped column** ran where real raises Msg 8120 (Django's `Exists(OuterRef(...))` next to an aggregate) — see [`query.md`](query.md#group-by-containment).
- **`SELECT DISTINCT … ORDER BY` an expression** was accepted whenever it read projected columns; real wants the term to match a select item by shape (Django's `nulls_first` ordering over a subquery annotation) — see [`query.md`](query.md#order-by-term-resolution).
- **`ALTER COLUMN` over a key or foreign-key column** refused restating the column unchanged, which Django's schema editor does for a comment or a nullability flip; real's per-source rules are in [`alter-table.md`](alter-table.md#blockers-msg-5074).
- **`ALTER COLUMN … double precision`** was a syntax error.
- **`sp_rename`** refused a case-only rename, didn't infer a column or index for a NULL `@objtype`, and renamed columns a computed column or CHECK reads — see [`catalog-views.md`](catalog-views.md).
- **`CREATE INDEX` over an `nvarchar(max)` key** (Django's `TextField(db_index=True)`) was accepted; real's Msg 1919 closes the connection Django's test case holds, which is what the 13 real-only `schema` failures were — see [`indexes.md`](indexes.md#grammar).

Getting there took eleven roots, and the pattern worth keeping is that failures cluster by *cause*, not by test — grouping them that way found each one:

- **Cascade beats breadth.** An unmodeled statement used to kill the TDS connection, so every later test in the class failed too; one statement accounted for 27 of 50 at the time. Now a statement-level fault is Msg 50000 severity 16 and the session survives ([`tds-endpoint.md`](tds-endpoint.md#statement-tier--severity-16-session-survives)).
- **Qualifier-blindness in name resolution** was the single largest class — a leaf-only match binds to the wrong column whenever a join brings a same-named one into scope, silently. It was wrong in four resolvers ([`query.md`](query.md#order-by-term-resolution)).
- The rest: outer-scope correlation from the select list, `UPDATE … SET` subqueries, parenthesized set-op branches, `OUTPUT … INTO` destination coercion, DISTINCT over a grouped projection, collation-aware `REPLACE` / `CHARINDEX`, aggregate re-homing across scopes, and `sys.time_zone_info`.

**Over-permissive validation — the simulator *accepts* what real *rejects*.** This is the more dangerous divergence direction (an app query works on the simulator and breaks on real), and it is invisible to a sim-only failure list: surface it with the *reverse* delta `comm -13 <sim fails> <real fails>`, where real-only failures mean the simulator over-passes. **Whole-suite audits should always run the reverse delta — a green "matches real" claim requires both directions.**

Worth keeping from that round: the backlog's own statement of the Msg 164 rule was wrong until probed (it is **not** about non-determinism — `GROUP BY a + DATEPART(year, GETDATE())` is legal — but purely "contains at least one column of the query's own sources"), which is the argument for probing a rule before encoding it even when a prior entry states it confidently.

Not sim bugs (**fail on real too** — leave alone): boolean-expression `=` comparison `WHERE (a<%s)=(b<%s)` → Msg 4145 on both; `CAST(<numeric> AS datetime2)` → Msg 529 on both (Django's DurationField tests expect it); most `get_or_create` `manual_pk`/duplicate IntegrityError tests (the savepoint-rollback-after-constraint pattern was probed identical to real). Not Django-specific: default-path string→date parsing is language-neutral, so `'1/2/3'` raises Msg 241 where real's `us_english` reads it mdy (deliberate — see [`casting.md`](casting.md)).

### Edge-case differential sweep — surfaced gaps

A hand-written corpus of 548 deliberately odd statements, run through the simulator's TDS listener and against SQL Server 2025 (17.0.4065.4) with identical SqlClient code on both sides, a fresh database per case, and every error routed through `InfoMessage` so a whole batch's output compares (probed 2026-09-23).
The harness is local-only and not checked in; its three connection-killing findings shipped, and the accept-what-real-rejects half lives in the [over-permissive register](#over-permissive-register).
Already listed elsewhere here and not repeated: parenthesized set-op branches.

**Type-pair neighbors** — found by the type-pair probes and left open (probed 2026-09-23):

- An alias type over numeric reads `decimal`, as does a numeric column's name in a type-pair message (raised while binding, before references are marked); everything else carries the name (see [`arithmetic.md`](arithmetic.md#numeric-vs-decimal-reported-type-name)).

**Message stream**: what's left — Msg 5703's localized wording and Msg 8153 over a constant `VALUES` grouping — is in [`errors.md`](errors.md#not-modeled-yet).

**Batch compilation** ships ([`control-flow.md`](control-flow.md#batch-compilation)); what the sweep found past it:

- The compile walk still stops at a deferral raised mid-statement — an `ALTER TABLE` of a table the batch creates, a binder error in a statement reading one — so an error past it surfaces only when its statement runs ([`control-flow.md`](control-flow.md#not-modeled-yet)).
- Syntax-error recovery ([`errors.md`](errors.md#syntax-error-recovery)) restarts at statement keywords rather than walking real's grammar, so a restart the simulator's own parser reads differently diverges: `begin try end try begin catch select 1 end catch` on one line adds Msg 102 near the last `catch` on real and nothing here, and a Msg 178 after a misplaced `CREATE PROCEDURE` names the procedure on real and nothing here (probed 2026-09-28).

**Wrong results**:

- A multi-row write's client `OUTPUT` sends no rows when a later row raises, where real streams the rows produced before it (probed 2026-10-01; [`dml.md`](dml.md#update--delete)).
- `STRING_AGG(s, CAST(',' AS varchar(2)))` over a table is Msg 8733 on real and aggregates here; over a `VALUES` source real accepts it too (probed 2026-09-24).
  What separates the two is plan-shaped rather than grammatical (probed 2026-09-27): the refusal needs a single table or view source and no `GROUP BY`, `HAVING`, `TOP`, `LIKE` filter or `OPTION (RECOMPILE)` — any of those, a derived table, a `#temp` table or a table variable accepts it — and it follows the value expression too (`UPPER(s)`, `LEFT(s, 10)`, `ISNULL(s, '')` accept; `s + ''`, `(s)`, `CAST(i AS varchar)`, `'x'` refuse), and a `CONVERT`, a `char(1)` or `varchar(max)` target and a `COLLATE` refuse like the `CAST`.
  The shapes line up with simple parameterization's eligibility — a `CAST`'s literal turned into a parameter is no longer a literal — but `PARAMETERIZATION FORCED` doesn't make the accepted shapes refuse, and none of the refused statements leaves a parameterized plan in `sys.dm_exec_cached_plans` (probed 2026-09-28), so that reading isn't confirmed.
  Probed 2026-09-28: the refusal survives `WHERE i = 1`, `WITHIN GROUP`, a table alias, `dbo.t`, `WITH (NOLOCK)`, a column alias and another statement in the batch, while `CHAR(13) + CHAR(10)`, `', ' + ' '`, `CONCAT(',', ' ')`, `CHAR(44)` and `SPACE(1)` are accepted over the same table.
- **Deferred until requested.** **Real accepts collations `sys.fn_helpcollations()` doesn't list**, in columns, `COLLATE` and `COLLATIONPROPERTY` alike, where the simulator refuses them as unrecognized (probed 2026-09-28 against SQL Server 2025).
  Searching every listed language base against every version suffix finds only `Azeri_Latin_90_*` (LCID 1068, code page 1254) and `Azeri_Cyrillic_90_*` (2092, 1251), version 1, with every flag suffix including `_SC` and `_SC_UTF8`.
  They are not aliases: they sort by the Azeri alphabet as `_100` does but order `I i ı İ` where `_100` orders `I ı i İ`, and they carry no dotted / dotless I case rule (`UPPER(N'iı')` is `II`, `_100`'s is `İI`).
  Four unversioned names outside the listed bases resolve too, at version 0: `Hindi_CI_AS` (1081, code page 0), `Macedonian_CI_AS` (1071, 1251), `Korean_Wansung_Unicode_CI_AS` (66578, 949) and `Lithuanian_Classic_CI_AS` (2087, 1257); other historical names tried (`Mexican_Trad_Spanish`, unversioned `Azeri_*` / `Uzbek_*` / `Tatar` / `Kazakh` …) and non-listed `_UTF8` / `SQL_` combinations do not.

**Same error, different number, state or class** (probed 2026-09-30):

- An error escaping a trigger body is followed by Msg 3621 carrying the trigger as its `Procedure` on real, and by none after a Msg 208 a called function raises; here the Msg 3621 follows unattributed in both (probed 2026-10-01; [`triggers.md`](triggers.md#not-modeled-yet)).

**Built-in values** (probed 2026-09-26):

- `DIFFERENCE` scores from a code of its own rather than the two `SOUNDEX` results — `'xc'` and `'x'` share `X000` yet score differently against `'abcd'` — and is asymmetric (`DIFFERENCE('x', '1')` is 0, `DIFFERENCE('1', 'x')` 3); here it compares the codes position by position, which matches real on most pairs but not all.
  A substring search of the second code in the first, with a first letter that doesn't suppress the next code, fits 342 of 400 random pairs; the rest weren't explained.
  A second pass (probed 2026-09-27) fit 3515 of 4000 random pairs and 5224 of a 6400-pair grid of every code over the digits 1–3, first letters A and E, with that rule and a non-letter-led string's code empty; where it still misses, a short code's zero padding scores as though two positions matched — `DIFFERENCE('a', 'abob')` (`A000`, `A100`) is 3 and `DIFFERENCE('ab', 'acoc')` (`A100`, `A220`) is 3.
  A third pass (probed 2026-09-28, a 6561-pair grid over first letters A and B, zero to three of the digits 1–3 and the empty string) fits 6001 pairs with the *first* code's digits as the needle: equal codes score 4; otherwise a matching first letter scores 1, plus 3 when the first code's three padded digits occur in the second code's, else 2 when its last or first two do, else one per digit found searching left to right from the previous hit's own position (not past it).
  Most misses have a repeated digit in the first code — `DIFFERENCE('aababab', 'babab')` (`A111`, `B110`) is 3 where the rule gives 2, `A112` against any `A12x` is 4 — and `DIFFERENCE('', x)` is 3 for a code with no digits, 2 for one or two and 0 for three, so padding and repeats are what the rule still gets wrong.
- `REGEXP_COUNT` / `REGEXP_INSTR` / `REGEXP_SUBSTR` with a `datetime` pattern or start position kill the session on real (severity 21); here they are Msg 8116, which is the answer kept.

### Result-set serialization: `FOR XML` / `FOR JSON`

Both clauses ship (see [`xml.md`](xml.md#for-xml-result-serialization), [`json.md`](json.md#for-json-result-serialization)); these are the parts that don't:
- **`XMLSCHEMA` / `XMLDATA`** (inline schema emission).
  EXPLICIT + `XMLSCHEMA` reports real's own Msg 3625 instead.
- **EXPLICIT's `idrefs` / `nmtokens` accept path** — real admits one where the column's expression is statically nullable and merges the per-row values into one space-joined attribute; the simulator has no expression-nullability model, so every such column reports real's Msg 6826 (which is what real gives the non-nullable shape).

### Built-in functions

Captured from a Microsoft Learn category-by-category audit (cross-checked against `Parser/Expression.cs::ResolveBuiltIn`, `Parser/AtAtKeyword.cs` + `Value.cs`, `Parser/Expressions/AggregateExpression.cs`, `Parser/Expressions/WindowExpression.cs`, and the FROM-source rowset dispatch in `Parser/Selection.{OpenJson,StringSplit,ListExtendedProperty}.cs`).
Re-fetch <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the function surface complete. 🎯 marks an item whose completion closes a Microsoft category.

Blocked on a larger unmodeled parent feature (shipping a function here implies the parent ships too):

- **ML scoring** (PREDICT surface not modeled) — PREDICT(MODEL = …, DATA = …).

- **System stored procedures** (`sp_*` family) — formatted-metadata / management procs invoked via `EXEC sp_name`.
  Shipped so far: the `sp_help` family (`sp_help` / `sp_helptext` / `sp_helpindex` / `sp_helpconstraint` / `sp_helpdb` / `sp_helpfile` / `sp_helpstats` / `sp_helprotect` / `sp_helptrigger` / `sp_helpuser`), `sp_depends`, the ODBC/JDBC catalog set (`sp_tables` / `sp_columns` / `sp_columns_100` / `sp_pkeys` / `sp_fkeys` / `sp_statistics` / `sp_statistics_100` / `sp_stored_procedures` / `sp_sproc_columns` / `sp_sproc_columns_100` / `sp_special_columns` / `sp_special_columns_100` / `sp_table_privileges` / `sp_column_privileges` / `sp_datatype_info` / `sp_datatype_info_100` / `sp_server_info` / `sp_databases`), `sp_spaceused`, `sp_who` / `sp_who2`, `sp_MSforeachtable` / `sp_MSforeachdb`, `sp_rename`, `sp_configure` and the `sp_xml_preparedocument` / `sp_xml_removedocument` pair — see [`catalog-views.md`](catalog-views.md).
  Also shipped: the message, legacy security, type, option, statistics and report procs listed under [System procedures over the registries](catalog-views.md#system-procedures-over-the-registries).
  Not modeled yet: internal-table rows in `sp_updatestats` / `sp_createstats`, login SIDs honouring `@sid`, `sp_password` and `ALTER LOGIN … WITH PASSWORD` on `sa` past the policy check (recording it would switch the TDS endpoint from accepting any credentials to enforcing them; a wrong `OLD_PASSWORD` is Msg 15151 on real), real's system message rows, and the message language of the session for Msg 2786 / 2787 / 5703 wording; other procedures still raise Msg 2812.
  A broad surface — each proc is its own result-shape contract over the catalog views.
  Ships piecemeal by popularity, not as a bundle.

- **SQL Server 2025's vector residue** — the binary vector TDS type a vector-aware client negotiates, and the smaller gaps [`vector.md`](vector.md#not-modeled-yet) lists (the type, its functions, the `float16` base type, vector indexes and `VECTOR_SEARCH` ship; the search is exact where real's DiskANN approximates).
- **SQL Server 2025's `json` residue** — the native json TDS type a json-aware client negotiates, a JSON index's internal table (`sys.objects` / `sys.internal_tables`), and the smaller gaps [`json-type.md`](json-type.md#not-modeled-yet) lists (the type, `JSON_CONTAINS`, the advanced array accessors, `JSON_VALUE … RETURNING`, the `modify` method and `CREATE JSON INDEX` ship).

Low priority / niche — simulatable (as placeholder constants or a small model) but rarely hit, so not worth attention yet:

- **Settled — don't re-pitch:** `msdb.dbo.syspolicy_configuration.current_value` stays `nvarchar` — it's a *view-body* projection (not a resource column) mixing `int` rows with a `binary` GUID row, every consumer reads a single named row and CASTs it, so a variant migration there would only touch the view SQL text for no observable gain.
- **Graph residue** — a `SHORTEST_PATH` repeating more than one hop or starting from a derived source, two in one WHERE, `LAST_NODE(x) = LAST_NODE(y)`, the `NOCHECK` / `CHECK CONSTRAINT` toggle over an edge constraint, and the smaller divergences — see [`graph.md`](graph.md#not-modeled-yet).
- **Dynamic Data Masking residue** — a masked `geography` / `geometry` / `vector` value raises `NotSupportedException` (real sends a single `0x00` byte its own client can't read back) — see [`data-masking.md`](data-masking.md#not-modeled-yet).
- **Small residues found while probing the permission and sequence entries** (probed 2026-09-29 against SQL Server 2025): `INSERT … VALUES … OPTION (RECOMPILE)` is accepted on real and Msg 156 here; a function may take a table-type `READONLY` parameter on real where a scalar function's is Msg 2715 here; `ORDER BY x + 1` over a select alias `x` is Msg 207 *and* the sequence refusal on real where only the refusal shows here; `fn_my_permissions` is modeled for the server and login classes only, not `type`, `xml schema collection` or `fulltext catalog`.
- **Ownership residue** — `ALTER AUTHORIZATION` on the classes the simulator doesn't carry (certificates, keys, Service Broker, endpoints …), and the assembly gate's divergence (it asks at database scope because `GRANT` carries no assembly class) → [`permissions.md`](permissions.md#ownership).
  `Database.Name` is fixed at construction, so a rename reaches every structure keyed by it.

## Over-permissive register

The simulator accepting what real rejects is the more dangerous divergence direction — the query passes here and fails in production — and it is invisible to any sim-only failure list (see the reverse-delta note under the Django shakedown).
This is the standing list: each entry names the error real raises that the simulator doesn't, and the linked deep-dive carries the detail.
Entries are verified against the simulator, so one that no longer reproduces is removed rather than re-worded.

- **Non-Framework CLR assemblies load** — real resolves every `AssemblyRef` against a fixed .NET Framework catalog and raises **Msg 6503** otherwise (probe-confirmed for .NET 10 and for .NET Standard 2.0); the simulator runs on .NET so all of them bind, which is also what lets the tests emit a fixture assembly without a Framework toolchain.
  → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **`clr strict security` is a `sp_configure` option nothing reads** — real refuses `CREATE ASSEMBLY` of an unsigned SAFE / EXTERNAL_ACCESS assembly with **Msg 10343** while the option is 1; the simulator registers and validates the option but never consults it, and the Msg 10343 factory was removed as dead code rather than left as an unreferenced promise.
- **A CLR routine's exceptions from .NET's own base library carry .NET's wording and frames** (probed 2026-09-28) → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **A SQLCLR routine's context connection leaves a few of real's shapes unmodeled** — a function's command refused whole where real refuses the statement, `SELECT WITHOUT QUERY` / Msg 557, connections other than the context one from `EXTERNAL_ACCESS` / `UNSAFE`, and the provider's internal stack frames (probed 2026-09-28) → [`clr-assemblies.md`](clr-assemblies.md#the-context-connection).
- **A CLR aggregate's state never round-trips through `Write` / `Read`**, and `Merge` never runs — one in-memory instance accumulates each group, so an aggregate whose `IBinarySerialize` drops a field answers here where real may lose it; and a Msg 6522 / 6260 stack lists only the author-visible frames, real's own internal ones having no counterpart (probed 2026-09-28) → [`clr-assemblies.md`](clr-assemblies.md#divergences).

- **A scalar function whose *column* no longer exists inlines here** — real fails to inline it too, sending its binder report twice (probed 2026-09-30 against SQL Server 2025) → [`programmable.md`](programmable.md#inlining-a-call-as-the-query-compiles).
- **Session-option residues** — the `SHOWPLAN_*` / `STATISTICS XML` plan result sets, `STATISTICS IO`'s system base tables and table order where it follows the simulator's own reads, `FORCEPLAN`, and a same-batch `ALTER DATABASE … ANSI_NULL_DEFAULT` that real settles before the batch runs → [`session-options.md`](session-options.md#not-modeled-yet).
- **`tempdb`'s catalog names temp objects by their written names** — a `#temp` table's padded name and a table variable's `#`-and-hex name ship for the messages but not the catalog, and a table variable isn't listed in `tempdb.sys.tables` at all where real lists it (probed 2026-09-28) → [`temp-tables.md`](temp-tables.md#tempdbs-catalog-lists-them), [`table-variables.md`](table-variables.md#fidelity-gaps-remaining).
- **Expression and predicate remoting for a four-part read** — the projection is pushed down, so a query never naming a `vector` column reads its table, but one naming it anywhere but its output (`WHERE v IS NOT NULL`, `COUNT(v)`, `CAST(v AS varchar(…))`) or excluding every non-NULL row with its `WHERE` is Msg 7346 here where real runs those parts on the server (probed 2026-09-30) → [`linked-servers.md`](linked-servers.md#divergences).
- **Index-option residues** — option names in the column-level clauses; see [`indexes.md`](indexes.md#fidelity-gaps).

- **`SqlValue.FromDecimal` validates scale but not precision** — it restates the payload at the declared scale and leaves the declared precision to the caller, which is what lets the storage decoder reconstruct whatever is on disk.
  The coercion path is the precision gate for every conversion, but a *computation* that lands on a narrower type has to check for itself — `ROUND` does (see [`scalars.md`](scalars.md#math-scalar-functions)), and a future scalar that narrows its own result would have to.

- **Weightless-character residue** — real's per-version ignorable sets ship for comparison, sort, hashing, search and `LIKE`, and a search tells the no-break and U+2000..U+200A spaces from a space (see [`collations.md`](collations.md#characters-real-gives-no-weight-by-collation-version)).
  What's left: `=` under the culture comparers still reads those spaces as a space; the characters `CompareInfo` ignores and real weighs all take the final-level weight, where real gives U+200B / U+200C a low primary one (between U+200A and U+2028) and the Hebrew accents a diacritic one; `CompareInfo` equates `ª`, `ℬ` and the circled letters with their base letter where real's search doesn't; and non-`_SC` `TRANSLATE` over a surrogate pair's halves maps each half by its own position, where real, treating the halves as weightless riders on one character, answers `xZZ` for `TRANSLATE(N'x' + <pair>, <pair>, N'ZQ')` (probed 2026-09-25, 2026-09-29) → [`collations.md`](collations.md#known-gaps).
  Also: under the default collation, `nvarchar` U+0085 (NEXT LINE) sorts between U+0084 and U+0086 on real and elsewhere here (probed 2026-09-30 against SQL Server 2025 over code points 1-255; the other 254 match).
- **DML through a derived table** — a view or CTE body reading a derived table, a join view's derived-table source, and a joined write naming a derived table as its target (`UPDATE d SET … FROM (SELECT … FROM t) d`) pass no write through, where real writes through all three (probed 2026-09-30 and 2026-10-01) → [`programmable.md`](programmable.md#updatable-views-dml-through-views).
  The unstored view a CTE builds (`Simulation.CteDmlView`) is the shape a derived table would take too; a target adds real's own refusals for one, Msg 4417 / 4418 / 4420 / 4421 naming the derived table.
- **The views' remaining write gaps** — a FROM clause naming only a view whose `INSTEAD OF` trigger takes the write, a joined write's `OUTPUT` reading the other FROM sources' columns (a table target's too), and a stored `UNION ALL` view's partitioned-view rules (Msg 4436 / 4440 …) → [`programmable.md`](programmable.md#updatable-views-dml-through-views), [`dml.md`](dml.md).
- **Nested paren/subquery/function caps below real's absolute thresholds** — the expression-depth restructure (iterative precedence-climbing parse, iterative `Run`/`GetSqlType`, n-ary `AND`/`OR`, NOT-collapse) removed the process-death risk and lifted flat operator chains to no artificial cap.
  What remains is a *fidelity* gap on the deterministic nesting caps (see [`grammar.md`](grammar.md) "Expression depth limits"): the shared paren/subquery/function budget caps at 500 units (paren 500, subquery 83) vs real's stack-dependent 1015/168, because the simulator's parse frames are fatter (a 1 MB Debug thread parses only ~990 nested parens).
  The subquery ≈ 6× paren ratio matches real; the absolute numbers are lower to keep Msg 191 firing with headroom before the stack probe.
  Deep *function* nesting additionally surfaces Msg 8631 instead of Msg 191 on tight (≤1 MB) threads (its frames are fattest).
  Closing the gap toward real's numbers requires slimming the function-argument recursion frame (`ResolveBuiltIn` + per-function ctors are on the live path).
  Low demand — generated SQL rarely nests past tens; both outcomes are graceful.
  CASE/IIF nesting (cap 10, Msg 125) already matches real exactly.
- **Dependency-surface residue** — the four surfaces ship (see [`catalog-views.md`](catalog-views.md#expression-dependencies)), with known divergences.
  Column granularity is name-based (a statement frame touches column `C` of referenced object `T` when it names `T` and mentions `C`); a qualified mention narrows to its own source, so joins / `APPLY` / `MERGE` match real exactly, but an **unqualified** mention in a multi-source frame still lands on every source that has a column by that name, and a MERGE target's key column picks up an extra `is_updated` when it appears in both the `ON` and a `WHEN NOT MATCHED THEN INSERT` column list.
  Closing both wants parse-time (source, ordinal) capture, which the per-row name-keyed resolver doesn't do.
  `sys.dm_sql_referenced_entities`' **Msg 2020 arrives before the rows** rather than after them, because the reader materializes the rowset before delivering it, where real yields what it found and then raises.
  A reference mixing a whole-object write with a column-level one loses the legacy pair's object row, since the aggregated `Reference` no longer says which statement contributed which (recorded in [`catalog-views.md`](catalog-views.md#divergences)).
  `sys.dm_sql_referenced_entities` orders a comma-separated `FROM` list's sources by a rule the probes haven't pinned down (recorded in [`catalog-views.md`](catalog-views.md#divergences)).
- **`OBJECTPROPERTY(id, 'IsDeterministic')` — a CTE's or derived table's untyped output** — the module walk ships whole, the `CAST` / `CONVERT` style rule included (see [`catalog-views.md`](catalog-views.md#isdeterministic)), and a derived column that aliases a conversion, a qualified user function's return type, a folded constant style and the ANSI type spellings all decide as real does.
  What is left is a column name the body's referenced tables don't carry and no aliased conversion types (`SELECT convert(datetime, s) FROM (SELECT name s FROM t) q`, where `s` is a table's string column reached through the derived table), which reads deterministic where real reads it as its source column's type.
  Closing it wants the source extent bound as an expression rather than classified from tokens.
- **Which tied spelling a grouped `MIN` / `MAX` reports** — real reports the first value its operator reads among collation-equal spellings (trailing spaces, case under `_CI_`), and so does the simulator; they part only where real's plan sorts first, since its Sort isn't stable and that tie order isn't modeled (see [`collations.md`](collations.md#sql_latin1_general_cp1_ci_as--byte-exact-sort) and [`query.md`](query.md#row-order-without-order-by); probed 2026-09-29).
- **Leaked-connection session cleanup — shipped** (2026-08-05); see [`locking.md`](locking.md#abandoned-session-reclamation).
  All three pins are broken by the one-way `SessionToken` indirection the scope note called for (`LockResource.Hold.Owner`, `HeapTable.OwnerSession`, and an `ActiveSnapshotTxs` keyed by token and valued by a copied registration rather than by the transaction), `Simulation.Connections` became a token registry, and `Component`'s existing finalizer resurrects an abandoned connection into a queue drained on a normal worker thread by `CreateDbConnection` / `LockManager.TryAcquire` / the version-store collector.
  What's left is the timing, which is GC-nondeterministic on both sides and documented as such.
- **Deferred until requested.** **East Asian collations without a primary table of their own** — six unversioned families ship real's primary order and match it on 99.998–99.999% of probed string pairs; the versioned families borrow the nearest one, which leaves `Korean_90` / `Korean_100` at 84% / 51%, the bopomofo families at 84–92% and the `_90` / `_100` Chinese and Japanese ones at 94–99% (see [`collations.md`](collations.md#locale-comparer-sort-parity-gap), probed 2026-09-29).
  Each further table costs 25–80 KB deflated; the probe query is in that section.
- **The `Pref` names' grouped and deduplicated spelling** — `GROUP BY`, `DISTINCT` and `UNION` over a `SQL_*_Pref_*_CI_AS` `varchar` column report the uppercase-preferred spelling of a case pair on real (`SELECT DISTINCT v` over `c, C` is `C`, whichever arrived first) where the simulator reports the first-seen one; the sort order itself ships.
  It is a plan effect, not a rule of the collation (probed 2026-09-30 against SQL Server 2025): the preferred spelling shows while the plan sorts (up to about 735 input rows, whatever the column's width or the aggregates beside it, the `UNION` at about 370 rows a side) and the first-seen one once the plan hashes, so closing it takes a cost model for the aggregation, not a tiebreak at the dedup sites.
  `INTERSECT` and `EXCEPT` report the left row's spelling, `MIN` / `MAX` the first seen, and a window's `PARTITION BY` lists the preferred spelling's partition first → [`collations.md`](collations.md#sql_latin1_general_cp1_ci_as--byte-exact-sort).
- **Deferred until requested.** **Code page 932's best fit** — `N'Á'` stored in a `Japanese_CI_AS` `varchar` column is `A` on real and `?` here (probed 2026-09-29), the same shape as code page 874's two misses in [`collations.md`](collations.md#known-gaps).
- **The TDS token stream's residue** — under `SET NOEXEC ON` real still sends every statement's DONE with its kind, `ALTER SCHEMA … TRANSFER`'s error DONE names a kind of its own, and a system procedure's own errors other than `sp_help`'s aren't known to return status 1 as assumed; the 370-case capture catalog matches otherwise (probed 2026-09-28 with a raw TDS client) → [`tds-endpoint.md`](tds-endpoint.md#per-statement-done-tokens).
- **DBCC residue** — `PAGE` / `IND` / `SHOWCONTIG` and the other unbuilt subcommands, the system base tables real's consistency checks list, and the storage-derived values that can't match real's physical layout → [`dbcc.md`](dbcc.md#not-modeled-yet).
- **Workload-harness divergence reporting quirks** (`.vs/workload/Program.cs`, local-only) — the parity report's example line rebuilds parameters from the op seed and can mismatch the actual divergent instance, and divergent instances aren't re-run single-threaded to classify transient-vs-stable.
  Both made the shared-plan-state hunt slower than it needed to be (the fixed bug class itself — instance-bound aggregate/window results, baked TOP/OFFSET counts, frozen RAND, unstamped replay clock — is documented in [`plan-cache.md`](plan-cache.md)'s shared-plan contract section).

## Live-but-untested surfaces

Measured 2026-07-30 (`dotnet test --collect:"XPlat Code Coverage"` → reportgenerator; 90.7% line / 79.2% branch / 92.2% method).
These are reachable code paths no test exercises — not gaps in behavior, gaps in the safety net.
Of the ~990 lines in fully-uncovered methods, ~376 are `DebugDisplay` / `ToString` debugger helpers and ~48 are `Stream` boilerplate overrides, both of which are deliberately not worth testing; what remains is this list.

- **`sp_cursorprepare`** (`TdsSession.CursorPrepare`) — SqlClient issues the `sp_cursoropen` family instead, so reaching it needs a driver that prepares a cursor explicitly.
- **The foreign-key scan fallback** (`FkTuplesMatch` + `EnumerateChildRows`) — **structurally unreachable**, not merely untested: it runs only when an FK column has no storage slot, which is true only of a non-persisted computed column, and the simulator rejects one in a FOREIGN KEY the way real does (Msg 1764 from the table-level and ALTER forms, Msg 8183 from the inline one — see [`foreign-keys.md`](foreign-keys.md#computed-columns-in-a-foreign-key)).
  A PERSISTED computed column is accepted and does have a slot, so it takes the seek path.
  Left in place as a guard on the storage layout rather than deleted.
- **`ClrAssemblyMetadata.ComputePublicKeyToken` / `DescribeReference`** — strong-named assembly identity and the assembly-reference description.
- **`LikeMatcher.Cache`'s collation key component can't be varied by any test** — the resolved collation is a per-expression-node constant in every shape reachable through SQL, so mutation-testing it catches nothing; it guards a hypothetical future caller that rebinds a node's collation between executions.

Worth re-measuring after a large bundle rather than routinely, and worth acting on when it does run: **nothing this pass surfaced was merely an untested-but-correct path.**
It found two pieces of dead code — a duplicated MERGE type resolver, and an unreachable ON-UPDATE-CASCADE branch whose stub threw an exception saying so — and every gap that was then covered turned out to be hiding a behavior bug.
Uncovered code here has consistently meant *wrong* code, not just unwatched code.

## Design choices to revisit

Queued structural refactors (behavior-neutral, one per commit, each proven by the full gate plus a timing A/B; measured 2026-09-29, when simulator source had grown from 193k to 276k lines over five days):

- **`Selection`'s long phases** (`ParseSingleFromSourceCore` 735 lines, `ParseQueryBlock` 675, `BuildSqlProjection` 703, the aggregate builder 703) split opportunistically, when feature work next touches them — they sit on measured hot paths, so each extraction takes a perf A/B.

Shipped intentionally and correct under their documented contract, but the original rationale may have aged.
Worth a look before re-affirming or changing.
(Rationale lives in [`scalars.md`](scalars.md)'s divergence notes and CLAUDE.md's Quirks.)

- **APPROX_COUNT_DISTINCT** implemented as exact `COUNT(DISTINCT)`.
  Real's answer is a 4096-register HyperLogLog estimate that parts from the exact count as soon as two values share a register (51 for the integers 1 to 52), but its hash isn't identifiable from query outputs and its answer also varies with batch- versus row-mode execution — see [`query.md`](query.md#the-approximate-aggregates) for the corpus and the hypotheses ruled out.
  Revisit if a public description of real's hash surfaces; until then exact is the one answer that is right wherever real is.

## Won't-model / explicitly excluded

Excluded on **correctness**, not priority: these are cloud-only surfaces the SQL Server 2025 RTM box product itself rejects, so modeling them would *diverge* from the box-product fidelity oracle.
Don't re-surface as candidates (unless a future box release promotes one).

- **ANY_VALUE(expr)** — Azure/Fabric-only, not in the box product (probe-confirmed).
  With it excluded, the **Analytic** category is complete for the box product (CUME_DIST / PERCENT_RANK / PERCENTILE_CONT / PERCENTILE_DISC all ship).
- **SESSION_ID()** — dedicated-SQL-pool / cloud surface; the box raises Msg 195 (probe-confirmed).
  `@@SPID` is the box session-id mechanism.
