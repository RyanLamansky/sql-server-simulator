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
- **`WRITETEXT BULK` / `UPDATETEXT BULK`** — the statement forms ship (see [`legacy-lob.md`](legacy-lob.md)); the `BULK` keyword raises `NotSupportedException` because real's bulk form is a bulk-copy data stream fed over the wire rather than a statement, which a normal client can't issue at all (real answers **Msg 185**, `Data stream is invalid for WRITETEXT statement in bulk form.`, probed 2026-08-06).
  Closing it means a TDS bulk-load path keyed to a text pointer rather than a table.
- **XQuery beyond the expression subset** — the computed `attribute name {…}` and `text {…}` constructors in a *read* method (both ship in `.modify()`'s insert content, and the computed `element name {…}` ships in both), the direct comment / processing-instruction forms, `sql:variable()` / `sql:column()` accessors outside `.modify()`'s value terms, the `xs:` constructor functions, and named axis steps (`child::` / `descendant::` …).
  Predicates, the comparison / boolean / arithmetic operators, the function library, FLWOR / quantified / conditional expressions and the direct element constructor ship — see [`xml.md`](xml.md#the-xquery-subset) for the catalog.
  Every gap is reached by `.modify()` too, since the mutator's paths run through the same evaluator; its insert *content* is a separate sublanguage, so a `{…}` there still takes only literals and the `sql:` accessors → [`xml.md`](xml.md#not-modeled-yet).
- **XSD validation against `xml(collection)` bindings** — the collection's XSD is read only for each element declaration's *occurrence*, which is what an XQuery path's static cardinality needs ([`xml.md`](xml.md#a-schema-collection-narrows-the-cardinality)); nothing validates an INSERT, an UPDATE, or a `.modify()` edit against it.
  That is what real's Msg 6923 (a validation failure after an edit) and Msg 2247 (a `with` value that isn't a subtype of the schema type) report, and what makes `replace value of` legal against a *typed* element rather than only its `text()` node.
  The **static type name** is unbuilt with it: a typed instance's paths still carry `xdt:untypedAtomic`, so a Msg 2389 over one quotes that where real quotes the schema type (`xs:string`), and the narrowing itself is keyed on the element *name* rather than resolved through the containing type — narrower than real, never wider.
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
- **A re-executable plan artifact for DML** — `INSERT` / `UPDATE` / `DELETE` / `MERGE` parse and execute in one interleaved pass, so the plan cache has nothing to store for them and a `SaveChanges` batch re-parses (its tokens are memoized; see [`plan-cache.md`](plan-cache.md#statement-kind-eligibility-what-can-be-replayed)).
  Each would need the parse/execute split `Selection` already has, preserving an error ordering that is probe-pinned to the interleaving.
  Same for `SET` / `DECLARE` as recordable effects, which is what would let the EF modification-batch prefix cache as a sequence rather than declining the batch.
  **The ceiling was measured 2026-07-30** and is real but bounded: a plan-cache hit costs 14.4 µs/op against 26.8 forced to miss, so ~46% where it applies, and ~5.7 µs/op (~28%) is the headroom on the two-statement shape.
  That reading is of the *plan* half only; the token half shipped separately and turned out larger than it was first measured to be — [`plan-cache.md`](plan-cache.md#the-token-memo) carries both the numbers and why the first measurement was low.
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

### Complex-query execution — perf residuals

The complexity batteries (`.vs/workload` `compare` subcommand + `complex*.sql`, local-only) are the standing measurement instrument; sub-4× ratios against the live reference are constant-factor or parallelism territory, larger ones name a missing execution strategy.
Open residuals, in measured-impact order:

- **A catalog "seek" is a filtered scan, so catalog introspection sits ~8× behind live** — a real constraint-inventory query over a 300-table database (`sys.objects` ⟕ `sys.indexes` ⟕ `sys.key_constraints` with a `CROSS APPLY` reading `sys.index_columns` ⋈ `sys.columns`, plus three `UNION ALL` branches) measures **659 ms against live's 81 ms**, decomposing as **472 ms for the APPLY branch** and ~190 ms for everything else — the other three branches are 7 / 18 / 27 ms and the bare three-way outer join is 16 ms, so the correlated branch is the whole residual.
  **Already done, don't re-pitch**: the statement-scoped materialization memo, the correlated-comparand pushdown, and the transitive hop across an inner join (all in [`catalog-views.md`](catalog-views.md)); together they took this query from a 30-second timeout to 659 ms, and the branch from 74 s to 472 ms.
  **What's left is that `FilteredRowGenerator` doesn't seek.** It walks `database.Schemas.Values` × `schema.HeapTables.Values` and `continue`s past every non-matching `ObjectId`, so each "seek" is O(objects in the database) — and `EnumerateColumns` re-sorts each schema's tables with `.OrderBy(t => t.ObjectId)` on *every* call, allocating a fresh ordering per seek.
  At ~849 body executions × two seeked views × 302 tables that is the 472 ms.
  The fix is an `object_id`-keyed lookup the generators seek into rather than a predicate they filter with, which is the thing real has and the simulator doesn't: real's catalog views are views over **indexed base tables** (`sysschobjs` / `sysiscols` / `syscolpars`), and its plan for this query is an index seek per outer row reading ~2 rows (probed with `SET SHOWPLAN_ALL ON`, 2026-08-06).
  Ordering is part of the contract, so the lookup has to preserve the `ObjectId` order the sort currently imposes — building it per statement alongside `StatementContext.CatalogViewRows` is the natural home, since that is already the scope over which the schema is fixed.
  **Secondary, smaller**: the query's own `LEFT JOIN`s to `sys.indexes` / `sys.key_constraints` can never be narrowed (dropping rows from a null-supplying side is unsound), so real's index-nested-loop over those is a strategy the simulator has no analogue for; `sys.key_constraints` / `sys.check_constraints` / `sys.default_constraints` / `sys.tables` aren't pushdown-aware at all; and the transitive hop is one level, so a three-source chain leaves the far end scanning.
- **DML through a join view bypasses the joined-source passes** — `JoinViewDml.cs` enumerates without the materialization/narrowing preparation `UPDATE`/`DELETE` take, and the **narrowed-source-first reorder is declined for every DML statement**; its WHERE names view output columns resolved through the chain resolvers, so wiring it is a name-resolution correctness question before a perf one.
- **A fan-out-aware semi-join crossover was built and measured out** (2026-08-05) — **don't re-pitch it as stated.** The seekable-inner delay (`evaluations × 4 > innerRowCount`) does assume each key selects about one row, and the seek cache's bucket count is a free estimator for the real fan-out; both were implemented (`HeapSeekCache.TryGetKeyCount`, the ordinal carried on `SemiJoinShape`) and neither helped.
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
- **A bound past the selection heap's ceiling still sorts its partition** — a deep-paging `rn BETWEEN 50001 AND 50050` narrows what gets projected but keeps the full sort, since a heap of 50k over 73k rows is no cheaper than sorting once.
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
- **A leftmost derived table draws `NEWID()` once** where real draws per output row — the leftmost source executes exactly once by construction; the deferred-source `NEWID` gate covers only the sources the materialization pass touches.

### sqllogictest differential sweep — surfaced gaps

The oracle itself — corpus, sharded runner, how to re-run and re-read it, and the traps that silently invalidate a run — is documented in [`sqllogictest.md`](sqllogictest.md).
Nothing from it is checked in; it regenerates in minutes.

Standing measurement on the 391-file `random/` slice (5,295,251 records, 16 shards, ~460 s): **5 divergent records** (3 × Msg 8134, 2 × Msg 8115), all of them the plan-dependent residue below.
The count was 26 until the constant-fold fixes in `05f71c5`; every run since reports 5, replay included.
Re-run it after any bundle touching the parser, the expression evaluator or the type system.

Still open from what it surfaced:

- **One trailing garbage identifier after a completed clause is swallowed**: `SELECT a FROM t zzz qqq`, and the same after `WHERE` / `GROUP BY` / `ORDER BY` / a comma-FROM list, return rows where real raises Msg 102 at the second identifier.
  Exactly one extra identifier is consumed, and the no-`FROM` path is already tight (`SELECT 1 zzz qqq` is Msg 102 on both).
  This is the identifier half of the trailing-token rule [`grammar.md`](grammar.md) records as narrowed to value literals — now with concrete shapes.
- **Msg 102 where real raises Msg 156 naming the keyword**, at a residue of sites (`NOT`, `NULL`, `INTO`, `OR`, `UPDATE`, `DELETE`, `INSERT`); the simulator already reports Msg 156 correctly elsewhere, so this is site-specific rather than a missing error.
  Also `DROP INDEX <1-part>` is real's Msg 159, the simulator's Msg 102, and `CREATE PROCEDURE p BEGIN …` — the procedure form that omits the body's `AS`, which [`programmable.md`](programmable.md#the-body-introducing-as-is-optional) covers for functions — is real's Msg 156 near `BEGIN` against the simulator's Msg 102 (probed 2026-08-06).
  The `the keyword` wording is Msg 156's own text rather than a variant of Msg 102's, so a site reporting the wrong number reports the wrong wording with it.
- **Many-way joins do not scale**: `select5`'s 20-24-table equi-joins answer in milliseconds on real and exceed a 15-second `CommandTimeout` here, one of them running past a 40-second wall without honoring its own timeout.
  Not a correctness gap, but it is why the sweep's file list is `random/` rather than the whole corpus — see the join-strategy notes in [`joins.md`](joins.md).
- **A `FROM`-less star is three behaviors real distinguishes and the simulator answers Msg 102 for all**: `SELECT *`, `SELECT 1, *` and `SELECT COUNT(*), *` are **Msg 263** ("Must specify table to select from."), `SELECT t.*` is **Msg 107**, and `EXISTS (SELECT *)` is legal.
- **`<binary> <operator> <approximate>`** (`0x02 + CAST(2 AS real)`) is real's **Msg 206** in both operand orders for `+ - * /`; the simulator raises `NotSupportedException`.
- **`STDEV` / `VAR` over `money`** is `float` on real; the simulator raises **Msg 529**.
- **An integer literal padded past 12 characters is `numeric(significant_digits, 0)`**, not `int` — `SELECT 0000000000300` is `numeric(3, 0)` on real while the 11-character `00000000300` is `int`.
  The rule belongs to the bare-literal tokenizer — see [`arithmetic.md`](arithmetic.md).
- **Real answers a statement's binder errors together where the simulator raises the leading one alone** — `INSERT` reports 207 + 110, and 273 + 10709, as one multi-error response.
  The module-body bind already gathers every error of a *body*; this is the same shape for a single statement — see [`programmable.md`](programmable.md).
- **`SELECT TOP (-1)`** returns no rows where real raises **Msg 127**; the DML `UPDATE` / `DELETE TOP (-1)` path already raises it, so only the SELECT site misses it.
- **`FORMAT(x, 'P')` renders three decimal places where real renders two** (`0.000%` against `0.00%`, for positive zero as well), an invariant-culture percent-digits difference; changing FORMAT's culture defaults reaches every specifier, so it wants its own probe pass.

**Five sweep divergences remain, each demonstrated irreducible** — real's own answer flips under something the simulator cannot legitimately model, so matching them would mean modeling plan selection rather than semantics.
Two are the trivial-plan boundary: `WHERE <overflow> <= 18 / CAST(NULL AS int)` raises as written, and answers 0 rows the moment `DISTINCT`, `GROUP BY`, `TOP 2` or a join is added — while `ORDER BY` / `MAX()` / `COUNT(*)` leave it raising.
One is written order inside an un-negated `IN` list (`x IN (x/0, x)` answers, `x IN (x, x/0)` raises — same elements).
Two are per-row short-circuiting that flips with the *data*, not the text (a row satisfying the cheap conjunct makes real raise in both written orders).
The same comparison in a **`HAVING`** folds unconditionally — a HAVING always carries a grouping, so it never gets the trivial plan — which is why that position is closed and `WHERE` is not.
Precisely-scoped list in the "Not folded yet" section of [`query.md`](query.md).

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
- **A `decimal` beyond .NET `decimal`'s range — closed.** `model_fields.test_decimalfield`'s `max_digits=38` model surfaced as `SqlServerSimulator: unhandled OverflowException` (Msg 50000); the exact-numeric type carries all 38 digits, so the value computes and stores, and the only remaining narrowing is the reader's, which is SqlClient's own shed-or-`OverflowException` rule — see [`arithmetic.md`](arithmetic.md#the-backing-type).
- **Reverse delta: `expressions_window` — closed** (2026-08-05), and the filed diagnosis was wrong.
  `test_fail_update` has nothing to do with it: the poisoner is `test_key_transform`, whose `SUM(…) OVER (PARTITION BY <JSON_VALUE> ORDER BY <JSON_VALUE>)` orders a RANGE frame by an `nvarchar(max)` expression.
  Real answers **Msg 8728** and rolls the whole transaction back, so Django's next `ROLLBACK TRANSACTION <savepoint>` fails (Msg 6401) and `needs_rollback` sticks for the rest of the class; the simulator ran the query.
  Msg 8728 and its transaction-aborting semantics now ship ([`query.md`](query.md#range-frame-order-by-msg-8728), [`transactions.md`](transactions.md#the-transaction-aborting-error-class)), along with the Msg 6401 that used to be Msg 102 and the TDS transaction-state repairs behind it.
  Re-measured over the wire: `expressions_window` is **0 sim-only and 0 real-only**, 27 failing identically on both.
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
- **`ALTER TABLE … ALTER COLUMN <c> { ADD | DROP } PERSISTED`** raises `NotSupportedException`.
  Real converts a computed column both ways in place: `ADD PERSISTED` on a deterministic expression flips `sys.computed_columns.is_persisted` to 1 and `DROP PERSISTED` back to 0, both idempotent (probed 2026-08-06).
  The refusals are all probed — a non-computed column is **Msg 4919** (`PERSISTED attribute cannot be altered on column 'a' because this column is not computed.`), a nondeterministic expression **Msg 4936**, a missing column **Msg 4924 State 2** (the ROWGUIDCOL / SPARSE forms report State 1), and a `DROP PERSISTED` whose column an index or CHECK depends on is **Msg 5074** followed by **Msg 4922 State 9**.
  What makes this more than a flag flip is storage: `HeapColumn.IsStored` is `Computed is null || IsPersisted`, so the toggle inserts or removes a stored column and needs its own heap rewrite — `RewriteHeapForAlterColumn` maps stored ordinals 1:1 and can't express it, and `ADD PERSISTED` additionally has to evaluate the expression per row on the way in.
  The **Msg 4936 gate itself ships** at the two declaration sites (`CREATE TABLE` inline, `ALTER TABLE … ADD`) — see [`constraints.md`](constraints.md).
- **`ALTER TABLE … ALTER COLUMN <c> { ADD MASKED WITH (FUNCTION = '…') | DROP MASKED }`** raises `NotSupportedException`, and there is no `sys.masked_columns` view.
  Probed 2026-08-06: the DDL sets a per-column masking function that `sys.masked_columns` projects as `(object_id, name, is_masked, masking_function)`, with `default()` / `email()` / `partial(p, 'X', s)` / `random(a, b)` the accepted functions; an unrecognized one is **Msg 16002** (`Invalid data masking function in column 'n'.`), one the column's type doesn't support **Msg 16003** (`The data type of column 'a' does not support data masking function 'partial'.`), and `DROP MASKED` where nothing is masked **Msg 16007** (`The column 'e' does not have a data masking function.`).
  The DDL + catalog half is self-contained; the *enforcement* half — a principal without `UNMASK` reading the masked value instead of the real one — is the larger piece and would want its own permission wiring.
- **A schema owner other than `dbo` doesn't break an ownership chain** — `CREATE SCHEMA … AUTHORIZATION` records the owner and `sys.schemas.principal_id` projects it, but `PermissionChecker` still assumes every object is dbo-owned (`ChainsAcross`'s comment says so outright), so a module in schema A reading a table in schema B owned by a different principal stays chained where real breaks the chain and checks the caller's own grant.
  Wiring it means threading the schema's `PrincipalId` into the same-database chain suppression, which touches every module invocation — see [`permissions.md`](permissions.md).
- **A CHECK constraint's predicate isn't bound over an empty table** — `ALTER TABLE t ADD CHECK (nosuch > 0)` on a table with no rows succeeds, because the predicate's names resolve per row during the existing-data validation pass and there is no row to run it against; real binds the predicate and reports **Msg 207** whatever the table holds.
  A populated table reports 207 correctly, so the gap is the empty-table path rather than the resolver.
  The same shape closed for module bodies through CREATE-time binding (see [`programmable.md`](programmable.md)); a CHECK / computed-column / DEFAULT expression wants the equivalent.
- **A mixed `ALTER TABLE … ADD` list of columns *and* constraints** — `ADD x int, CONSTRAINT ck CHECK (…)` is accepted by real; the simulator's column-add branch consumes the rest of the statement, so a constraint element after a column definition is a syntax error.
  The constraint-only multi-element list ships, with its rollback — see [`alter-table.md`](alter-table.md#multi-element-add).
- **`CREATE SCHEMA`'s element rollback leaves permission rows behind** — an element list that granted a permission and then failed removes the schema (and the objects inside it) but not the `sys.database_permissions` rows keyed on those object ids, which are then unreachable.
  Real rolls the whole statement back including the grants.
- **Real's terminating trailers after a refused ALTER TABLE** — Msg **1750** (`Could not create constraint or index. See previous errors.`) follows Msg 4925 / 4926 / 8111 / 1764 on real and the simulator omits it, as it omits Msg **5069** after `ALTER DATABASE`'s Msg 5011.
  The first message is the load-bearing signal in each pair; the trailer is a client-visible second error a strict comparison would see.

## Over-permissive register

The simulator accepting what real rejects is the more dangerous divergence direction — the query passes here and fails in production — and it is invisible to any sim-only failure list (see the reverse-delta note under the Django shakedown).
This is the standing list: each entry names the error real raises that the simulator doesn't, and the linked deep-dive carries the detail.
Entries are verified against the simulator, so one that no longer reproduces is removed rather than re-worded.

- **Statement-permission residue** — every modeled CREATE / ALTER / DROP statement is gated (see [`permissions.md`](permissions.md#ddl-statement-gates)), but three securable classes real accepts a grant on have no GRANT surface here, so the alternative each offers isn't honored: `CONTROL ON TYPE::t` (DROP TYPE takes schema ALTER only), `CONTROL ON XML SCHEMA COLLECTION::c` (same), and `CONTROL ON <fulltext catalog>` (DROP FULLTEXT CATALOG takes `ALTER ANY FULLTEXT CATALOG` only).
  That direction is *under*-permissive, so it isn't a register entry — the register keeps it because closing it is the same piece of work.
  → [`permissions.md`](permissions.md#known-gaps).
- **An unterminated delimited identifier tokenizes as if it closed** — `SELECT [abc` reads as the column `abc` and answers Msg 207 here, where real reports **Msg 105** (`Unclosed quotation mark after the character string 'abc'`, the same wording it uses for a character literal) followed by Msg 102 (probed 2026-08-05).
  The `'…'` half already raises Msg 105; only the bracket form runs off the end silently.
- **A CHECK constraint carrying an illegal explicit conversion is created** — `ALTER TABLE t ADD CONSTRAINT ck CHECK (CAST(d AS int) > 0)` is **Msg 529** followed by **Msg 1750** on real and is accepted here (probed 2026-08-05).
  The Msg 529 compile-time gate ships everywhere an expression's type is resolved (see [`casting.md`](casting.md#conversion-legality-is-settled-while-compiling)), and a computed column's expression goes through it; a CHECK predicate's operands are only typed when a row is measured against them.
- **A character real weights and `CompareInfo` ignores compares equal to nothing** — `N'x' + NCHAR(0x00AD) = N'x'` (soft hyphen) is true here and false on real, so a row real excludes comes back (probed 2026-08-05, matrix re-run 2026-08-05 across the whole ignorable family).
  The probed set is the C0 controls U+0001..U+001F, U+200B, U+2007, U+00A0, U+2028, U+2029 and U+E0001 everywhere, plus U+00AD and U+200C on the pre-100 names only.
  It reaches the [character-matching scalars](collations.md#the-character-matching-string-scalars-search-under-the-collation-too) as well as `=` and `LIKE`'s literal runs, and the **reverse** direction exists too: `CompareInfo` folds NBSP onto a space where real holds them apart, so `TRIM(N' ' FROM …)` removes an NBSP real keeps.
  `LIKE`'s `_` is unaffected: it counts characters, so `LIKE N'x'` answers no there as real does.
  Two companion divergences run the *other* way and aren't register entries — real expands a ligature (`N'ß' = N'ss'`, full probed table in the doc) and equates any two standalone combining marks, where `CompareInfo` does neither → [`collations.md`](collations.md#known-gaps).
- **A syntax error past a clause doesn't outrank an earlier binding error** — `SELECT 1 FROM t WHERE` over a missing `t` is **Msg 208** here and **Msg 102** at the `WHERE` on real, because real parses a batch before binding any of it (probed 2026-08-06, same for `FROM t PIVOT`).
  `ParserContext.PendingGroupByBindError` already defers the GROUP BY clause's own binding error for exactly this reason; the FROM clause's object resolution has no equivalent, and giving it one runs against the parse-and-execute-in-one-pass design the simulator is built on — see [`grammar.md`](grammar.md#trailing-token-tightening).
- **Non-Framework CLR assemblies load** — real resolves every `AssemblyRef` against a fixed .NET Framework catalog and raises **Msg 6503** otherwise (probe-confirmed for .NET 10 and for .NET Standard 2.0); the simulator runs on .NET so all of them bind, which is also what lets the tests emit a fixture assembly without a Framework toolchain.
  → [`clr-assemblies.md`](clr-assemblies.md#divergences).
- **A join view over a join view is Msg 4405** for the INSERT or UPDATE naming one base table that real accepts, flattening both levels (probe-confirmed).
  A chain of *single-source* levels above one join view ships; a level reading several sources of its own doesn't, because the target source is then a view rather than a heap and there is no `(page, slot)` address behind the row the write would claim.
  Recursing the level walk into that source's own sources is the work.
  → [`programmable.md`](programmable.md#dml-through-a-join-view).
- **MERGE into a join view is Msg 4405** where real accepts a `WHEN NOT MATCHED THEN INSERT` whose column list names a single base table's columns and writes that table (probe-confirmed).
  MERGE reads `View.RejectionReason` up front; routing it wants the per-action column lists to pick the target the way INSERT's does.
  → [`programmable.md`](programmable.md#dml-through-a-join-view).
- **A filtered index's WHERE takes predicate shapes real's grammar refuses** — `CREATE INDEX ix ON t(s) WHERE s LIKE 'a%'` and `... WHERE v = 1 OR v = 2` are both **Msg 156** on real (`Incorrect syntax near the keyword 'like'` / `'or'`, probed 2026-08-06), and the simulator creates the index with a NULL `filter_definition`.
  Real's `<filter_predicate>` grammar is a restricted one — a comparison of a column against a literal, `IN`, `IS [NOT] NULL`, joined by `AND` only — so closing it means gating the predicate parse rather than the rendering, which is where the null comes from today.
  → [`indexes.md`](indexes.md).
- **`clr strict security` is a `sp_configure` option nothing reads** — real refuses `CREATE ASSEMBLY` of an unsigned SAFE / EXTERNAL_ACCESS assembly with **Msg 10343** while the option is 1; the simulator registers and validates the option but never consults it, and the Msg 10343 factory was removed as dead code rather than left as an unreferenced promise.
- **A GROUP BY view's aggregate column is Msg 4403** where real reports **Msg 4406** — real splits by which column the write names, `SET <group-by column>` being 4403 and `SET <aggregate column>` 4406 since the aggregate is a derived field (probe-confirmed, through a chained view too).
  `RejectionReason` settles the whole view before any column is looked at, so the per-column gate never runs on a shape that already failed; letting the 4406 walk run first on an aggregate / DISTINCT body is the work.
  → [`programmable.md`](programmable.md#updatable-views-dml-through-views).
- **A batch that is nothing but `@var <type>` is Msg 102 where real says Msg 137** — real parses `@x int` as a statement far enough to bind `@x` and reports the undeclared variable; the simulator's dispatcher rejects it as a syntax error at the `@x`.
  Reachable through `EXEC sp_executesql N'@x int'` and, more realistically, through an `sp_executesql` call whose two leading arguments were transposed — real runs the declaration string as the statement and reports 137, which is how the positional-binding rule was probed in the first place.
  Both engines refuse the batch either way; only the number and wording differ.
- **An `sp_executesql` declaration string that isn't a declaration list reports the mini-parser's own Msg 102** rather than real's Msg 137 / **Msg 4124** (`The parameters supplied for the batch are not valid.`).
  Real evidently validates the string as a whole before reading entries out of it; `ParseSpExecuteSqlParamDefinitions` fails at whichever token it reaches first.
  Only malformed input reaches this.
- **`EXEC <proc>` with an unrecognized argument name reports the wrong parameter in Msg 201** — for `exec p @a = 1, @zz = 2` against `p @a int, @b int`, real names `'@b'` (the first declared parameter still unbound) and the simulator names `'@a'`, as though the successful binding had been discarded.
  The message is right in every case where all the names are known (probed across three parameters, with and without defaults, in and out of order); it is only the unknown-name path that misreports.
  Distinct from the `sp_executesql` argument-binding path, which now matches.

## Fidelity gaps in shipped behavior

Real bugs / limitations against shipped behavior — fixes are concrete work, not design decisions.

- **A text pointer's row half is a hash of the cell's value, not of the row** — the pointer `TEXTPTR` hands out is derived from (column name, cell value), with a per-table cache binding the pair to the row address the statements settled on, which is what carries one pointer through the chunked `WRITETEXT`-then-`UPDATETEXT` idiom (see [`legacy-lob.md`](legacy-lob.md#the-pointer-encoding)).
  Three consequences follow, each probed against real and each wanting the row address at `TEXTPTR` evaluation time — which the expression layer doesn't see, since a FROM source yields row bytes and drops the RID:
  two rows of one column holding the **same value** share a pointer and resolve to the first;
  an **ordinary `UPDATE`** of the cell strands a pointer read before it (Msg 7123 on next use) where real's stays valid and reads the new value;
  and a **`WRITETEXT` of NULL** leaves the cell with no pointer where real keeps handing one out, since real's pointer reflects an allocated LOB root rather than a non-NULL value.
- **A constant negative length is a statement error where real aborts the batch** — `SELECT SUBSTRING('abc', 1, -1)` is settled while compiling on both engines and reports the same Msg 536 (see [`legacy-lob.md`](legacy-lob.md#negative-length-msg-536-while-compiling-msg-537-at-run-time)), but real's is a batch-level compile failure that the same batch's `BEGIN TRY` can't catch, while the simulator's is an ordinary statement error.
  The runtime half (Msg 537 for `LEFT` / `SUBSTRING`, Msg 536 state 2 for `RIGHT`) matches on both engines.

- **`FORMAT`'s culture data is .NET's ICU set where real's is the .NET Framework's NLS set** — every divergence below is width-independent, reproducing for an `int`, a `money` and a narrow `decimal` alike, and each is what .NET itself produces for the same call (probed 2026-08-06):
  a default-precision `'P'` writes three fractional digits (`FORMAT(CAST(123.456 AS decimal(10, 3)), 'P')` → `12,345.600%`) where real writes two (`12,345.60%`), and a negative `'C'` under `en-US` writes `-$0.50` where real writes the parenthesized `($0.50)`.
  `fr-FR`'s group separator is U+00A0 on real and U+202F in .NET's data (`FORMAT(CAST(1234 AS int), 'N0', 'fr-FR')` differs in that one code point).
  Two further rows are real's own oddities rather than .NET's: `FORMAT(CAST(0 AS decimal(5, 0)), '#')` is real's `0` and .NET's empty string, and `FORMAT(CAST(0 AS decimal(5, 0)), 'P')` is real's `000.00%`.
  Closing the first two means carrying an NLS-shaped `NumberFormatInfo` override per culture; the last two are per-case rules on zero.
- **`FORMAT` over a value wider than a .NET `decimal` doesn't take a scientific custom pattern** — the standard specifiers and the `0` / `#` / `.` / `,` / `%` / `‰` / section-separator custom subset all ship at full 38-digit width (see [`scalars.md`](scalars.md#effunctions-driven-string-scalars-patindex--stuff--quotename--replicate--space--format)); a pattern carrying `E+0` raises `NotSupportedException` there.
- **A fixed-length `char(N)` / `nchar(N)` CAST target doesn't run the numeric-source length rules** — `CAST(CAST(123456789 AS decimal(9, 0)) AS char(5))` truncates to `12345` where real raises Msg 8115 state 5, because the fixed-length targets normalize inside `SqlValue.FromChar` / `FromNChar` and never reach `Cast.EnforceTargetMaxLength`, which is where the per-source-family rules (asterisk fallback, Msg 8115 / 232 / 234) live.
  The variable-length targets carry the whole family (see [`casting.md`](casting.md#castconvert-to-narrow-varchar--nvarchar--varbinary)); closing this means routing the fixed-length pair through the same gate before padding.
- **`SqlValue.FromDecimal` validates scale but not precision** — it restates the payload at the declared scale and leaves the declared precision to the caller, which is what lets the storage decoder reconstruct whatever is on disk.
  The coercion path is the precision gate for every conversion, but a *computation* that lands on a narrower type has to check for itself — `ROUND` does (see [`scalars.md`](scalars.md#math-scalar-functions)), and a future scalar that narrows its own result would have to.

- **GROUP BY containment reaches only the expression kinds the reference walk descends** — the structural rule ships (see [`query.md`](query.md#group-by-containment)), but the walk descends through arithmetic, concatenation, parentheses, CAST / CONVERT, COLLATE, negation and the length / spatial / hierarchyid / XML members only.
  A column buried in any other composite — a `CASE` arm, `COALESCE`, a date or JSON scalar's argument — is never visited, so `SELECT COALESCE(a, 0) FROM t GROUP BY b` runs here and is **Msg 8120** on real.
  Every missing kind is one `VisitColumnReferencesCore` override; the coverage gap predates the containment rule and is shared with CREATE TABLE's Msg 8141 peer-reference check.
- **An unterminated `BEGIN` block names the wrong token** — `BEGIN TRY` at end of batch is Msg 102 `near 'TRY'` here and `near 'BEGIN'` on real, which names the block opener rather than the last token it read (probed 2026-08-05).
  The end-of-batch naming rule otherwise matches across the whole probed family (see [`grammar.md`](grammar.md#what-a-syntax-error-names-at-end-of-batch)); a block is the one construct real reports against its own start.
- **An unbalanced paren around a non-boolean reports Msg 4145 rather than Msg 102** — `IF ((1)` is real's Msg 102 `near ')'` and the simulator's Msg 4145 at the same token (probed 2026-08-05).
  Both engines refuse; the non-boolean check fires before the group's own closer is missed.
- **Three `NEXT VALUE FOR` refusals report the wrong sibling message** — the whole nine-message family ships with real's precedence order (see [`sequences.md`](sequences.md#where-next-value-for-is-rejected)); what's left is which of two refusals a statement carrying both reports, and both engines refuse either way.
  A reference in a **restricted clause** (Msg 11720) or a **conditional arm** (Msg 11741) inside a statement that also carries an `ORDER BY` keeps its own message where real reports Msg 11723 — those two fire where the reference parses, which is before the `ORDER BY` is read, while `DISTINCT` and `TOP` are known by then and do report real's.
  A reference in a **windowed aggregate**'s argument (`SUM(NEXT VALUE FOR s) OVER ()`) is Msg 11725 where real is 11720, the trailing `OVER` likewise being read after the argument.
  Closing either wants the site-level refusal deferred to the end of the query spec, which needs a catch-all resolution point for the expression sites outside a `SELECT` (`PRINT`, a `SET` initializer) so a pending refusal can't leak as a silent acceptance.
- **`SET LANGUAGE` doesn't move `@@DATEFIRST`** — real sets the language's own first weekday when the language changes (`French` reads 1, `us_english` 7), and leaves an explicit `SET DATEFIRST` standing when the language it names is already the current one (probed 2026-08-06).
  `SET DATEFIRST` itself ships (see [`scalars.md`](scalars.md#set-datefirst-and-the-parts-that-read-it)); what this wants is the per-language table `SET LANGUAGE` would read, which is the same table the month / weekday names and the date-format order would need.
- **Msg 3930's write gate covers DML and object DDL only** — a doomed transaction (see [`transactions.md`](transactions.md#set-xact_abort)) refuses `INSERT` / `UPDATE` / `DELETE` / `MERGE`, `CREATE` / `ALTER` / `DROP` / `TRUNCATE`, `SAVE TRANSACTION` and `COMMIT`, where real refuses everything that writes to the log — a `GRANT`, an `sp_rename`, an extended-property write still run here.
  Each is a `RejectWriteInDoomedTransaction` call at the statement's own entry; the shared seam every one of them routes through is what doesn't exist.
- **Msg 245 is transaction-aborting on real without `XACT_ABORT`** — a conversion failure rolls the transaction back and reads `XACT_STATE()` −1 from a `CATCH` even under `SET XACT_ABORT OFF` (probed 2026-08-06), where the simulator treats it as statement-terminating like its neighbours until the option is on.
  Real's transaction-aborting list is wider than the one modeled member (Msg 8728); 245 is the one whose divergence a probe caught, and the rest of the list is unenumerated.
- **`SET ROWCOUNT` caps a `MERGE`'s source rows rather than its actions** — real counts the actions it took, so a source row every `WHEN` clause declines consumes a slot of the cap here and none there (see [`query.md`](query.md#set-rowcount-n)).
  The three pending-action lists are built across several matching paths, so a shared running budget is what the exact rule wants.
- **An emptiness probe evaluates the body's projection where real doesn't** — real needs only whether a body yields a row for `EXISTS` and for a NULL-left-side `IN`, so `EXISTS (SELECT 1/0 FROM <non-empty>)` answers TRUE there and `NULL IN (SELECT 1/0 FROM <non-empty>)` answers UNKNOWN, while the simulator raises Msg 8134 from projecting the first row (probed 2026-08-05; a raising **WHERE** raises on both, since emptiness can't be known without it).
  Pre-dates the NULL-left-side work and is shared by both consumers; closing it wants a row-existence execution mode that skips the projection.

- **The ANSI code-page fold happens at the storage boundary, not at the conversion** — real narrows the moment a Unicode string becomes a `varchar` / `char` / `text`, so `UNICODE(CAST(N'水' AS varchar(10)))` is **63** there (`?`) and **27700** here (probed 2026-08-05); the simulator's value still holds `水` and only loses it when a row of it is encoded.
  Everything that *stores* or *returns* the value already agrees — a column write, a derived table or subquery (which encodes at its own level), and the reader path, whose `RowEncoder.StorageForm` applies the same fold (see [`data-reader.md`](data-reader.md#the-row-form-the-reader-reads)) — so the divergence is confined to a scalar reading a converted value inside the same projection: `UNICODE`, and a `+` / `REPLICATE` / `UPPER` that carries the character further.
  `ASCII` already answers 63, because it encodes its argument itself.
  Closing it means folding in the string→string arm of `SqlValue.CoerceTo` (and wherever a `varchar` literal is typed), which then makes the value form and the byte form agree by construction rather than by the cursor reapplying it.
- **A `STRING_SPLIT` separator that is one supplementary character is Msg 214** — real accepts a surrogate pair as a single-character separator under an `_SC` collation and splits on it (probed 2026-08-05); the length check counts UTF-16 units, so it refuses the pair.
  The `_SC` character count is already available (`Collation.IsSupplementaryCharacterAware`, the same dispatch `CHARINDEX` and `LEN` take), so the gate is what needs the reading, not the split.
- **`TRANSLATE` maps the halves of a surrogate pair separately** — real answers `ZZ` for a pair whose two halves sit in `chars` with different translations, the simulator answers `ZQ` (probed 2026-08-05), and under an `_SC` collation real counts the pair as one character for the Msg 9828 length check where the simulator counts two.
  The per-character walk is by code unit; a `_SC`-aware walk would settle both halves of this at once.
- **A view's `CREATE`-time errors carry no `Procedure` attribution** — real attributes *every* error raised inside a `CREATE VIEW` to the view being defined (probed 2026-08-04 for the syntax family Msg 156 / 102, the binder's Msg 207, the body-shape Msg 1033 / 4511, and even the Msg 2714 name collision), where the simulator leaves the field empty.
  Functions already attribute, because their bodies go through `BindModuleBodyAtCreate`, which sets `BatchContext.ErrorProcedureName`; a view's body is parsed inline instead, so nothing sets it.
  The batch-position check added alongside this attributes explicitly and is the one view-`CREATE` error that carries the name.
  → [`programmable.md`](programmable.md#where-a-module-statement-may-sit-in-its-batch).
- **The read-only-database gate doesn't reach every write statement** — `Database.IsReadOnly` refuses the DML row writes and the object-DDL family, but the `GRANT` / `REVOKE` / `DENY` family, `sp_rename`, `sp_addextendedproperty`, `ALTER SCHEMA … TRANSFER`, `ALTER INDEX` / `DROP INDEX` and the principal / assembly DDL carry no gate, where real raises **Msg 3906** for all of them (probe-confirmed for GRANT and `sp_addextendedproperty`).
  Real also varies the Msg 3906 state at a few sites (`ALTER TABLE` reports state 12) where the simulator raises state 1 throughout.
  → [`database-options.md`](database-options.md#read-only-databases).
- **Skip-mode deferred name resolution — DML target tables not placeholder-continued** — the skip-mode parse-continuation fix substitutes placeholder metadata for a missing *FROM-clause table* or *schema-qualified function* so an un-taken branch parses to completion and is discarded whole (killing the orphaned-`ELSE` cascade — see [`control-flow.md`](control-flow.md)).
  Re-probed 2026-07-29: the **spurious Msg 208 is gone** — `IF 1=0 INSERT INTO missing SELECT * FROM other; SELECT 'after'` now returns `after`, as do the UPDATE and DELETE forms, so a dead branch with a missing DML target no longer breaks the following statement.
  What still reproduces is the **orphaned `ELSE`**: the bare (non-`BEGIN`/`END`) form `IF 1=0 INSERT INTO missing … ELSE SELECT 'else-ran'` raises **Msg 102** near `else` where real runs the ELSE, and a missing **MERGE** target raises Msg 102 near `;`.
  Wrapping the dead branch in `BEGIN`/`END` parses correctly, as does the same shape over an existing table, which localizes it to the object-name swallow's flat recovery scan consuming the `ELSE` when the throw fires before the statement body is.
  Narrow (requires a *missing* DML target in a dead branch — the common safe-guard idiom targets an existing table), .
  The faithful fix is placeholder-continuation through the DML column-validation surface (INSERT column-list / arity, UPDATE SET / DELETE WHERE against a placeholder target), which is a broad, per-processor change — deferred as low-frequency.
- **A parenthesized *boolean* carrying a trailing value operator reports Msg 4145 rather than real's own syntax error** — `WHERE (a = 'a') COLLATE X = 'x'` and `(a = 'a') LIKE 'x'` are **Msg 156** on the keyword for real, and `(a = 'a') + 1` is **Msg 102** on the operator (probed 2026-08-05); the simulator reports Msg 4145 at the parenthesized boolean's own operand for all three.
  Both engines reject, so this is message shape only.
  Naming the operator would mean re-reporting at the token the value-LHS lookahead peeked when `Expression.Parse` refuses the group — which would misreport a genuinely malformed *value* expression, so it wants its own probe pass over that neighbourhood.
  The accepting half — a parenthesized value expression carrying a postfix `COLLATE` in a predicate — ships; see [`collations.md`](collations.md#the-postfix-on-a-parenthesized-group-in-a-predicate).
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
- **Leaked-connection session cleanup — shipped** (2026-08-05); see [`locking.md`](locking.md#abandoned-session-reclamation).
  All three pins are broken by the one-way `SessionToken` indirection the scope note called for (`LockResource.Hold.Owner`, `HeapTable.OwnerSession`, and an `ActiveSnapshotTxs` keyed by token and valued by a copied registration rather than by the transaction), `Simulation.Connections` became a token registry, and `Component`'s existing finalizer resurrects an abandoned connection into a queue drained on a normal worker thread by `CreateDbConnection` / `LockManager.TryAcquire` / the version-store collector.
  What's left is the timing, which is GC-nondeterministic on both sides and documented as such.
- **Msg 8729 — the RANGE-frame ORDER BY size cap** — the sibling of the Msg 8728 gate that ships (see [`query.md`](query.md#range-frame-order-by-msg-8728)).
  Real refuses a RANGE-framed window whose ORDER BY list's declared byte widths sum past **900**: "ORDER BY list of RANGE window frame has total size of N bytes. Largest size supported is 900 bytes.", class 16 state 1, transaction-aborting like its sibling, with the LOB check winning when both apply.
  Probed boundaries (2026-08-05): `nvarchar(450)` = 900 bytes passes, `nvarchar(451)` = 902 doesn't, `char(1000)` and `varchar(8000)` don't, and two keys sum (`nvarchar(400)` + `nvarchar(100)` reports 1000).
  Held back because it needs a declared-byte-width answer for an arbitrary expression's `SqlType`, and the length-unspecified container forms (the declared-string-widths entry below) don't give one — a wrong width there would refuse a query real accepts, which is the worse direction.
- **A transaction the engine ends isn't announced with real's transaction ENVCHANGE in every case** — surfaced while closing the `expressions_window` reverse delta, where the fix landed for the one case that had a client waiting on it.
  Real emits a transaction ENVCHANGE whenever it ends a session's transaction on its own (a transaction-aborting error, a deadlock victim, a SNAPSHOT update conflict), and a manual-commit driver reads it as "open the next one" — which is why `@@TRANCOUNT` reads 1 rather than 0 on the statement after an abort over ODBC while the same session over sqlcmd reads 0.
  `TdsSession.WriteTransactionEndIfAny` emits it, and the TM begin arm nests rather than refusing so a driver that lost track doesn't fault the session.
  The deadlock-victim and update-conflict paths were not re-probed at the wire level, so whether their ENVCHANGE / descriptor bookkeeping matches real is unverified.
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
- **`LikeMatcher.Cache`'s collation key component can't be varied by any test** — the resolved collation is a per-expression-node constant in every shape reachable through SQL, so mutation-testing it catches nothing; it guards a hypothetical future caller that rebinds a node's collation between executions.

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

## Won't-model / explicitly excluded

Excluded on **correctness**, not priority: these are cloud-only surfaces the SQL Server 2025 RTM box product itself rejects, so modeling them would *diverge* from the box-product fidelity oracle.
Don't re-surface as candidates (unless a future box release promotes one).

- **ANY_VALUE(expr)** — Azure/Fabric-only, not in the box product (probe-confirmed).
  With it excluded, the **Analytic** category is complete for the box product (CUME_DIST / PERCENT_RANK / PERCENTILE_CONT / PERCENTILE_DISC all ship).
- **SESSION_ID()** — dedicated-SQL-pool / cloud surface; the box raises Msg 195 (probe-confirmed).
  `@@SPID` is the box session-id mechanism.
