# JOINs / APPLY

INNER / bare `JOIN` (= INNER) / LEFT [OUTER] / RIGHT [OUTER] / FULL [OUTER] / CROSS / CROSS APPLY / OUTER APPLY. Multi-table chains compose left-to-right. ON-predicate UNKNOWN excludes. APPLY is the lateral form: the right side is re-executed per outer row and takes no ON clause. Execution lives in `Selection.Execution.Joins.cs`.

## Comma-separated FROM (ANSI-89)

`FROM a, b WHERE a.id = b.id` parses as a sequence of explicit-join chains spliced with `JoinKind.Cross` joins. Each comma starts a fresh chain via the same `ParseExplicitJoinChain` helper the JOIN-keyword loop calls, so any explicit JOINs *within* a chain bind before the cross-splice.

**Quirk — back-reference across a comma silently succeeds** (e.g. `FROM a, b JOIN c ON c.id = a.id`): real SQL Server binds `b JOIN c ON …` as its own scope and raises Msg 4104 because `a` isn't visible there; the simulator doesn't do parse-time scope-checking on ON predicates (column refs resolve at runtime through `ResolveAcrossTuple` across all FROM sources in the tuple), so the query runs and returns the Cartesian-filtered rowset. The common shapes — basic `FROM a, b WHERE …`, multi-comma chains, comma + derived table, explicit JOIN followed by comma — all match real SQL Server byte-for-byte; only this rare back-reference-across-comma case diverges, toward "more permissive" rather than wrong rowset.

### Cross→Inner equi-join rewrite

A bare `JoinKind.Cross` (from either a comma or an explicit `CROSS JOIN`) carries no ON predicate, so `ApplyJoin` never computes an equi-plan for it — it would always fall to the O(L×R) nested loop, even when WHERE carries a clean `a.k = b.k`. `RewriteCommaJoinsToEquiJoins` (run once at parse time, inside `BuildSqlProjection`, so the rewritten `joins[]` is captured in the cached plan) closes that gap: for each Cross level with no ON and a re-enumerable (non-lateral) right side, it scans the WHERE conjuncts for equi-keys connecting a prior source to that level (the same `TryExtractEquiKey` classifier the explicit-JOIN equi-plan uses), synthesizes them into an ON via `BooleanExpression.And`, and flips the level to `JoinKind.Inner`. It then rides `EquiJoinSeekOrHash` / `HashEquiJoin` exactly like an explicitly-written `INNER JOIN … ON`.

`FROM a, b WHERE a.k = b.k` ≡ `a INNER JOIN b ON a.k = b.k` — the textbook inner-join ON/WHERE identity. **Correctness is anchored by the residual invariant**: every pulled conjunct *stays* in the WHERE excluders (never removed), so flipping Cross→Inner can only drop rows the WHERE would drop anyway. The post-WHERE result is therefore provably unchanged regardless of outer joins elsewhere in the chain — converting a `Cross` level mixed with a preceding LEFT JOIN's null-extended side, or feeding a later LEFT JOIN, leaves the surrounding outer-join semantics intact (the rewrite only ever touches Cross levels, never one that already has an ON). Only equi-keys are pulled; a non-equi WHERE term (`b.id > 10`) stays a post-join filter, so the synthesized ON's residual count is 0. A derived-table right side after a comma keeps its `LateralPlan`, so it's skipped (can't be seeked/hashed anyway) and stays on the nested loop.

## JoinDriver

`JoinDriver` is a fold over `joins[]`: the leftmost rowset is wrapped with each join's operator in turn to produce the final enumerator. `ApplyJoin` picks the operator per join level — and is the single point where the strategy (hash vs nested loop) is decided.

### Equi-join fast path

`TryPlanEquiJoin` splits an INNER / LEFT / RIGHT / FULL ON predicate into `left.col = right.col` conjuncts (bare-column `Reference`s only, each classified by a single `FindSourceColumn` lookup) plus a residual of everything else. With ≥1 equi-key, RIGHT / FULL route straight to `HashEquiJoin` (their unmatched-right tracking needs the inner materialized), while **INNER / LEFT go through `EquiJoinSeekOrHash`**, which adaptively chooses between a per-outer index seek on the inner and the hash build:

- It buffers the outer up to `SeekOuterRowCap` (128). If the outer stays small **and** the inner is a base table whose join key the seek can use (probed once on the first buffered outer row — `MaybeApplyIndexSeek` returns the same `FromSource` on decline, a narrowed one on seek; the decline is value-independent), it seeks the inner per outer row and re-checks the full ON predicate as a residual filter. The inner's per-`Heap` seek cache builds once and **persists across outer rows and across query executions**, whereas `HashEquiJoin` rebuilds its dictionary every execution — so the repeated small-outer "filter parent, fetch children" shape collapses from a full inner scan per call to a seek (`order.detail` on AdventureWorks: ~290 ms → 0.08 ms after the one-time build).
- Otherwise (large outer past the cap, or an unindexed / non-base-table inner) it replays the buffered outer rows — then the remainder — into `HashEquiJoin`, so a large outer never pays per-outer overhead. LEFT keeps its NULL-extend-on-no-match semantic on both paths.
- This is gated by **leftmost-source predicate pushdown** (`NarrowLeftmostJoinSource`): single-source WHERE equality predicates (`leftmostCol = literal/variable`) seek the always-preserved driving table before the join, shrinking the outer to the few rows that make the per-outer inner seek win. Probe values are restricted to non-column constants/variables there (`MaybeApplyIndexSeek(..., allowCorrelatedColumnValue: false)`) because a not-yet-joined sibling column isn't resolvable pre-join.

The original equi-join win still stands — with ≥1 equi-key the inner is indexed by the promoted keys and probed once per left row, O(L + R) vs the nested loop's O(L × R) (an AdventureWorks 9-table view drops from a multi-minute hang to sub-second).

- Bucket keys reuse GROUP BY's collation-consistent `SqlValueKey`, coercing both sides to the `SqlType.Promote` common type so equality matches the `=` operator exactly.
- Bucket membership is a forward-linked chain over row ordinals (`buckets[key] = (head, tail)` + one shared `next` list) rather than a `List<int>` per key — the per-key list allocations and growth churn were the hash build's dominant profiled cost on a 228k-row build side. Forward links keep probe emission in build order, byte-identical to the per-key-list behavior.
- NULL keys are excluded (NULL = NULL is UNKNOWN) but retained for the unmatched-right tail of RIGHT / FULL.
- Residual non-equi conjuncts are re-checked per probed candidate (a conjunct passes only when it evaluates to `true`, matching the streaming path's `== true` gate).
- Falls back to the nested-loop operators below for non-equi ON predicates, lateral / derived-table right sides, CROSS / APPLY, and key-type pairs `SqlType.Promote` rejects (LOB, collation conflict, cross-category) — preserving their exact per-row error behavior.

**Uncorrelated catalog views hash-join too.** `sys.*` catalog views are deferred `LateralPlan` sources, which `TryPlanEquiJoin` normally rejects (line "`sources[level].LateralPlan is not null`"). But a catalog view's generator can't correlate (it takes no outer resolver), so the execution-time `MaterializeUncorrelatedDeferredSources` pass replaces each `FromSource.MaterializeOnce` catalog source with a once-materialized `Rows` list *before* the join planner runs — so `TryPlanEquiJoin` sees a plain re-enumerable source and keys it into the O(L + R) hash build. This removes both the per-outer-row re-generation and the O(L × R) loop from catalog multi-joins (SMO's per-column property-bag query). Correlated / lateral sources never set the flag and keep their per-outer-row execution. See [`catalog-views.md`](catalog-views.md) for the correlation-safety contract and measured improvement.

### Nested-loop fallback

INNER / CROSS / LEFT / CROSS APPLY / OUTER APPLY stream one upstream tuple at a time. RIGHT / FULL materialize `sources[level].Rows` into a list and track a `matched[]` bitmap across the entire upstream iteration so unmatched right rows can be emitted (with all prior slots NULL-filled) after upstream is exhausted.

**RIGHT / FULL with a derived-table right side** materialize the lateral plan once via the enclosing-scope `outerResolver` (not the joined-tuple resolver), so non-correlated and outer-correlated derived tables work; lateral correlation to the left side is rejected because the derived-table parse doesn't wire the left-source snapshot resolver — left-side references raise Msg 207 ("Invalid column name") at runtime when `Reference.Run` hits the null outer resolver. Real SQL Server raises Msg 4104 at bind time for the same shape; different code, same end state.

**Table-value-constructor (`(VALUES …) alias(cols)`) sources** are one more `LateralPlan` shape: a `CROSS` / `OUTER APPLY` VALUES source correlates to the left row (its cell expressions re-evaluate per outer tuple through the joined-tuple resolver), and `JoinDriver` treats it exactly like a derived-table SELECT right side. Parsing / type-promotion / error surface live in [`query.md`](query.md) (projection section).

## EF Core mapping

EF Core 10's LINQ `LeftJoin` / `RightJoin` operators translate to LEFT / RIGHT JOIN respectively and route through this pipeline. .NET 10 LINQ doesn't expose a `FullJoin` operator, so FULL OUTER JOIN is reachable only via raw SQL.

## Strategy guard (test diagnostics)

The strategy chosen per join is recorded through the opt-in `JoinDiagnostics.Sink` — a `[ThreadStatic]` ambient list, null by default. Most kinds log at the single `ApplyJoin` dispatch point; INNER / LEFT equi-joins log from inside `EquiJoinSeekOrHash` once the seek-vs-hash choice is made (`NestedLoopIndexSeek(keys=N)` vs `HashMatch(keys=N,residual=M)`). `Tests.Internal/JoinStrategyTests` reads it to assert the per-outer seek engages for a small filtered outer with an indexed inner, the hash build for a large outer or unindexed inner, and the nested loop for non-equi / CROSS — guarding against a silent fall-back to the O(L × R) loop, a perf regression the correctness suite wouldn't catch.
