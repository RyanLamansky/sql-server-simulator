# JOINs / APPLY

INNER / bare `JOIN` (= INNER) / LEFT [OUTER] / RIGHT [OUTER] / FULL [OUTER] / CROSS / CROSS APPLY / OUTER APPLY.
Multi-table chains compose left-to-right.
ON-predicate UNKNOWN excludes.
APPLY is the lateral form: the right side is re-executed per outer row and takes no ON clause.
Execution lives in `Selection.Execution.Joins.cs`.

## Comma-separated FROM (ANSI-89)

`FROM a, b WHERE a.id = b.id` parses as a sequence of explicit-join chains spliced with `JoinKind.Cross` joins.
Each comma starts a fresh chain via the same `ParseExplicitJoinChain` helper the JOIN-keyword loop calls, so any explicit JOINs *within* a chain bind before the cross-splice.

**Quirk — back-reference across a comma silently succeeds** (e.g. `FROM a, b JOIN c ON c.id = a.id`): real SQL Server binds `b JOIN c ON …` as its own scope and raises Msg 4104 because `a` isn't visible there; the simulator binds an ON predicate against the statement's *whole* source set rather than the chain's own scope (and `ResolveAcrossTuple` resolves the same way per row), so the query runs and returns the Cartesian-filtered rowset.
The common shapes — basic `FROM a, b WHERE …`, multi-comma chains, comma + derived table, explicit JOIN followed by comma — all match real SQL Server byte-for-byte; only this rare back-reference-across-comma case diverges, toward "more permissive" rather than wrong rowset.

### Cross→Inner equi-join rewrite

A bare `JoinKind.Cross` (from either a comma or an explicit `CROSS JOIN`) carries no ON predicate, so `ApplyJoin` never computes an equi-plan for it — it would always fall to the O(L×R) nested loop, even when WHERE carries a clean `a.k = b.k`.
`RewriteCommaJoinsToEquiJoins` (run once at parse time, inside `BuildSqlProjection`, so the rewritten `joins[]` is captured in the cached plan) closes that gap: for each Cross level with no ON and a re-enumerable (non-lateral) right side, it scans the WHERE conjuncts for equi-keys connecting a prior source to that level (the same `TryExtractEquiKey` classifier the explicit-JOIN equi-plan uses), synthesizes them into an ON via `BooleanExpression.And`, and flips the level to `JoinKind.Inner`.
It then rides `EquiJoinSeekOrHash` / `HashEquiJoin` exactly like an explicitly-written `INNER JOIN … ON`.

`FROM a, b WHERE a.k = b.k` ≡ `a INNER JOIN b ON a.k = b.k` — the textbook inner-join ON/WHERE identity.
**Correctness is anchored by the residual invariant**: every pulled conjunct *stays* in the WHERE excluders (never removed), so flipping Cross→Inner can only drop rows the WHERE would drop anyway.
The post-WHERE result is therefore provably unchanged regardless of outer joins elsewhere in the chain — converting a `Cross` level mixed with a preceding LEFT JOIN's null-extended side, or feeding a later LEFT JOIN, leaves the surrounding outer-join semantics intact (the rewrite only ever touches Cross levels, never one that already has an ON).
Only equi-keys are pulled; a non-equi WHERE term (`b.id > 10`) stays a post-join filter, so the synthesized ON's residual count is 0.
A derived-table right side after a comma keeps its `LateralPlan` at parse time, so it's skipped and its level stays `Cross` — the execution-time materialization below still collapses its re-execution, but the level nested-loops over the materialized list rather than hashing.

## Parenthesized join groups

`FROM A LEFT JOIN (B JOIN C ON c1) ON c2` — a **parenthesized join expression as a join operand**.
The group is a *grammar grouping*, not a derived-table scope: `B` and `C` keep their own qualifiers and resolve outside the parens (the outer `ON` and the SELECT list reference `B.*` / `C.*` directly, unlike a `(SELECT …) x` derived table).
It changes associativity from the default left-deep fold — the interior `ON c1` binds first, then `ON c2` joins the accumulated left spine against the *whole group*, and an outer-join miss NULL-fills **every** group member (both `B` and `C` read as typed NULL).
Semantics probed against SQL Server 2025.

**Parsing** (`Selection.cs`).
`NextSourceIsJoinGroup` peeks one token past the source's opening `(`: a `SELECT` marks a derived table and `VALUES` a table-value constructor (both keep their existing paths); anything else is a join group.
`ParseJoinGroup` recurses into `ParseExplicitJoinChain` over the *same* flat `sources[]` / `joins[]` lists, so the group's members occupy their own slots.
Two positions accept a group:

- **Leftmost operand** (`(A JOIN B ON x) LEFT JOIN C …`) splices directly into the spine with no marker — a left-deep spine already groups its left operand, so a left-side group is a no-op.
- **Right operand** (`A LEFT JOIN (B JOIN C ON c1) ON c2`) is the associativity-changing case.
  The connecting `JoinSpec` records `GroupCount` (the group's source count) and is inserted *ahead* of the interior joins the recursion appended (at the pre-recursion `joins.Count`, since the flat `joins.Count == sources.Count − 1` invariant doesn't hold mid-parse of an enclosing group).

A group must contain at least one join — a parenthesized single source (`(t)`) is Msg 102 (`near ')'`), matching real.
A group takes **no alias**: `(…) AS x` → Msg 156 near `AS`, `(…) x` → Msg 102 near the name.
A comma inside the parens (`(A, B)`) is Msg 102 near `,` (real rejects it too).

**Execution** (`Selection.Execution.Joins.cs`).
The fold is generalized to `EnumerateFoldRange(sources, joins, start, count, …)` over a contiguous slot range — the whole FROM at the top level, and each group's interior when materialized.
A level whose `JoinSpec.GroupCount > 1` routes through `GroupJoin`: it materializes the group's interior fold once (uncorrelated to the left — a group can't correlate to the join's left side, matching real) into per-slot-range snapshots, then nested-loop joins the left spine against them by the connecting join's kind.
LEFT/FULL emit unmatched-left rows with the whole range NULL-filled; RIGHT/FULL emit unmatched group rows with the left spine NULL-filled.
Nesting recurses (`A LEFT JOIN (B JOIN (C JOIN D …) …) …`).
Because a group spans multiple slots per join level, `ContainsJoinGroup(joins)` bypasses the flat left-deep optimizations (comma→equi rewrite, `MaybeApplyIndexSeek`, `TryApplyOrderedScan`) — the equi-join fast path is skipped for group levels; interior single-source joins still take it.
Correctness over a rare, typically tiny shape (SSMS's Table Designer partition-metadata query is the motivating consumer — all-empty catalog views).

## JoinDriver

`JoinDriver` is a fold over `joins[]`: the leftmost rowset is wrapped with each join's operator in turn to produce the final enumerator.
`ApplyJoin` picks the operator per join level — and is the single point where the strategy (hash vs nested loop) is decided.

### Equi-join fast path

`TryPlanEquiJoin` splits an INNER / LEFT / RIGHT / FULL ON predicate into `left.col = right.col` conjuncts (bare-column `Reference`s only, each classified by a single `FindSourceColumn` lookup) plus a residual of everything else.
With ≥1 equi-key, RIGHT / FULL route straight to `HashEquiJoin` (their unmatched-right tracking needs the inner materialized), while **INNER / LEFT go through `EquiJoinSeekOrHash`**, which adaptively chooses between a per-outer index seek on the inner and the hash build:

- It buffers the outer up to `SeekOuterBufferCap` (4096).
  If the outer is worth seeking **and** the inner is a base table whose join key the seek can use (probed once on the first buffered outer row — `MaybeApplyIndexSeek` returns the same `FromSource` on decline, a narrowed one on seek; the decline is value-independent), it seeks the inner per outer row and re-checks the full ON predicate as a residual filter.
  The inner's per-`Heap` seek cache builds once and **persists across outer rows and across query executions**, whereas `HashEquiJoin` rebuilds its dictionary every execution — so the repeated small-outer "filter parent, fetch children" shape collapses from a full inner scan per call to a seek (`order.detail` on AdventureWorks: ~290 ms → 0.08 ms after the one-time build).
- "Worth seeking" is an outright cap plus a ratio: up to `SeekOuterRowCap` (128) outer rows the seek always wins, and past it up to the buffer cap the seek still wins when the inner **table** carries at least `SeekInnerRowsPerOuterRow` (4) rows per outer row.
  A seek costs one call per outer row while a hash costs one build row per inner row, so the crossover is a ratio rather than an absolute outer size — a 200-row outer hashes against a 200-row inner and seeks against a 4000-row one.
  This is what lets a reordered chain keep seeking past its first link, where a fixed cap stalled it the moment the driving set outgrew 128.
- Otherwise (an outer past the buffer cap, a mid-sized outer against a comparable inner, or an unindexed / non-base-table inner) it replays the buffered outer rows — then the remainder — into `HashEquiJoin`, so a large outer never pays per-outer overhead.
  LEFT keeps its NULL-extend-on-no-match semantic on both paths.
- The driving set is shrunk first by the WHERE pushdown below, which is what makes the per-outer inner seek win for the common filter-then-join shape.

### WHERE pushdown into every base-table source

`NarrowJoinSources` (`Selection.Execution.IndexSeek.cs`, run by all three projectors — row, aggregate and window) attempts the single-source equality / range seek against the statement's WHERE excluders for **each** base-table FROM source, not just the leftmost.
The array is cloned rather than mutated, per the shared-plan contract in [`plan-cache.md`](plan-cache.md).

A probe value naming a **column** is classified rather than refused outright, against the whole FROM the narrowed source belongs to (`MaybeApplyIndexSeek(…, planSources: sources)` → `IsEnclosingScopeReference`).
A name resolving to a **sibling** source of that FROM declines — it isn't readable before the join runs.
One resolving to **none** of them is what the per-row resolver hands to the enclosing scope (`ResolveAcrossTuple`'s fallback), so it is fixed for the duration of one execution of this plan — the plan being what re-executes per enclosing row — and anchors the seek exactly as a variable does.
That classification is what seeks the inner side of a correlated subquery whose own FROM is a join, instead of hash-building it once per outer row: the WWI `SUM` over `Invoices ⋈ InvoiceLines` correlated to `Customers` went **79,316 ms → ~205 ms** (live 214 ms), matching the same body wrapped in a scalar UDF, whose `@c` parameter always passed the stability test.
A single-table mutation keeps the flat refusal (`allowCorrelatedColumnValue: false`) — it has no enclosing scope to read a column from.

**Narrowing any one source is semantics-preserving for every join kind**, because the matched conjuncts *stay* in the residual WHERE — the same invariant the comma→equi rewrite rests on.
A tuple an outer join NULL-extends because the narrowed side lost its match reads that side's column as NULL, so the conjunct that justified the narrowing is UNKNOWN and the tuple is excluded — exactly as it excluded the matched-but-failing tuple before.
That covers the NULL-supplied side of LEFT / RIGHT / FULL and the unmatched-right tail alike.

A narrowed source drops its `DataLockPlan`, so it is never re-seeked per outer row by the join; it becomes a small hash build side instead.
Past the leftmost slot the pass skips a source whose lock plan owes a SERIALIZABLE / `HOLDLOCK` phantom fence — the fence is settled inside the seek attempt, so probing every source would change which key ranges a SERIALIZABLE reader locks and when.
The leftmost slot keeps its long-standing unconditional attempt.

A joined UPDATE / DELETE narrows through `NarrowMutationJoinSources`, the same pushdown restricted to its **non-target** sources and gated at every slot including the leftmost — see [`dml.md`](dml.md#joined-row-sources).

### WHERE pushdown into a view / derived-table body

A base-table source can be seeked where it stands; a source reading through a *query body* can't, because the filter above it never reaches the scan inside it.
`PushWhereIntoDeferredSources` (`Selection.Execution.PredicatePushdown.cs`, run at the top of the row-source closure ahead of the materialization below, so all three projectors take it) moves the eligible top-level WHERE conjuncts **into** such a body, where the body's own passes — this one included — take them from there.
Recursion needs no loop: the rebuilt body runs the same pass over its own FROM, which is what carries a filter down a chain of views.
Measured on a five-deep chain of WWI views filtered on a key (`WHERE CustomerID = 90` over `Sales.Orders`): **177 ms → 1.6 ms**, and the inline nested-derived-table spelling of the same query **129 ms → 1.2 ms** (0.1× the live server).

The **conjunct stays in the enclosing WHERE**, the same residual invariant the base-table pushdown above rests on — and here it is what makes the push safe for every join kind, because the pushable shapes are all NULL-rejecting.
A tuple an outer join NULL-extends because the pushed side lost its match reads UNKNOWN for the very conjunct that justified the push, and is excluded exactly as the matched-but-failing tuple was.
That is why `IS NULL` is *not* a pushable shape: pushing the anti-join idiom's `WHERE v.col IS NULL` would turn every row the body dropped into a NULL-extended match, which is the one way this rewrite can invent rows.

**Eligible body** — a plain SELECT-project-filter: no DISTINCT, no TOP / OFFSET / FETCH, no GROUP BY / HAVING / aggregate, no window, no ORDER BY, not a set-op branch.
Each reads the row set as a whole, so a filter one level up would see a different one.
A join body qualifies (the conjunct lands in its WHERE and its own narrowing takes over), as does a body carrying its own WHERE — the pushed conjuncts append *after* it, so the body's own filter still decides first per row.
That ordering is what keeps the push from changing which rows an operand is evaluated over: real, whose own pushdown carries no such guarantee, raises **Msg 245** for `SELECT code FROM (SELECT code FROM t WHERE ISNUMERIC(code) = 1) d WHERE code = 5` over a non-numeric row the inner filter excluded (probe-confirmed), where the simulator answers the row — the same answer it gave before the push existed.
The eligibility is recorded at parse as a `PredicatePushdown` delegate on the plan, which is also how every non-body `LateralPlan` (a TVF, VALUES, OPENJSON, PIVOT, a catalog view, a linked-server query) declines: it carries none.

**Eligible conjunct** — one whose every column operand resolves to *that* source (a sibling's column isn't in the body's scope, and an enclosing scope's could silently rebind to a same-named body column), in one of the shapes `BooleanExpression.TryRebindOperands` rebuilds: a comparison, a non-negated `BETWEEN`, or the equality family an `IN` list / OR-of-equalities decomposes into.
Every other operand has to be row-independent, and is **evaluated once at the push**.
That is what lets a conjunct cross into a view body, whose plan doesn't exist until the reference executes: the conjunct travels as a *template* whose column operands are output-column **ordinals** and whose value operands are already constants — the only two things a body parsed later (in a child `BatchContext` holding none of the caller's variables) can read.
`Selection.ForView`'s wrapper carries the templates to that parse and applies them after the body binds and its permission check runs; the projection plan rebinds each ordinal to its own projection expression, declining an output column that is anything but a plain column projection (identity or rename).
Every decline is silent — the conjunct simply stays where it was written.

Cloned, never mutated, per the shared-plan contract in [`plan-cache.md`](plan-cache.md): the push builds a new `Selection` over the same parse-time tree and a new `FromSource` reading through it, both per execution.
A view's *stored definition* is untouched, so `sp_helptext` and the dependency surfaces (which read `View.BodyText`, not plans) see the view as written.

### Narrowed-source-first reorder

When the pushdown narrows a source the FROM clause doesn't name first, `ReorderToDriveFromNarrowedSource` rebuilds a **pure INNER equi-join chain** to drive from it.
INNER joins commute and their ON conjuncts are WHERE-equivalent, so the conjunction of every ON conjunct over the cross product is the result whatever order the sources fold in: any permutation that keeps each conjunct's two sources both placed by the step it attaches to produces the same rows.
Row *order* can change, which is legal without an ORDER BY.
Column resolution is name-based and rejects an ambiguous unqualified name outright (Msg 209), so it is order-independent too.

It engages only when every one of these holds:

- The best driver is a **non-leftmost** narrowed source seeking at most `SeekOuterRowCap` rows.
  Several narrowed sources compete on the seek's own candidate count (ties break on the written order); a narrowed leftmost that seeks at least as few already drives, and a wider narrowing leaves the written order alone rather than trading a small outer's per-outer seeks for a large one's hash probes.
- Every join is `Inner` with an `ON` and no parenthesized group.
- Every ON conjunct decomposes into an equality between two **distinct** sources' bare column references, with a key-type pair the runtime `=` could promote — the level-independent counterpart of `TryExtractEquiKey`.
  A single-source filter conjunct, a non-equi conjunct, or an OR declines the whole reorder.
- No source carries a `LateralPlan` (moving it would change how often it runs) or is a skip-mode placeholder, and no two sources share an exposed name.

Placement is greedy from the driver: the candidates at each step are the unplaced sources connected to the placed set by an ON equi-conjunct, and a candidate whose connecting columns **cover one of its own unique keys** (a PRIMARY KEY / UNIQUE constraint, or an enabled unfiltered unique index) wins — that join can't multiply the driving set, so the outer stays small enough for the next link to seek.
Ties break on the written order; a disconnected join graph declines entirely.
Each conjunct then re-attaches at the step that places the later of its two sources, so a conjunct pairing two sources the reorder placed earlier rides along at the step that completed the pair.

A materialized derived table (see below) can be a reorder *member* — its rows are fixed for the enumeration — but never the driver, since only a seek-narrowed base table drives.

Measured on the WWI six-table chain filtered on its fourth source (`WHERE c.CustomerID = 90`): **246 ms → 1.4 ms** (live 7.4 ms), the same chain filtered on its last source **233 ms → 15.6 ms** (live 51 ms), and the hand-reordered control **57 ms → 10.8 ms**.

The original equi-join win still stands — with ≥1 equi-key the inner is indexed by the promoted keys and probed once per left row, O(L + R) vs the nested loop's O(L × R) (an AdventureWorks 9-table view drops from a multi-minute hang to sub-second).

- Bucket keys reuse GROUP BY's collation-consistent `SqlValueKey`, coercing both sides to the `SqlType.Promote` common type so equality matches the `=` operator exactly.
- Bucket membership is a forward-linked chain over row ordinals (`buckets[key] = (head, tail)` + one shared `next` list) rather than a `List<int>` per key — the per-key list allocations and growth churn were the hash build's dominant profiled cost on a 228k-row build side.
  Forward links keep probe emission in build order, byte-identical to the per-key-list behavior.
- NULL keys are excluded (NULL = NULL is UNKNOWN) but retained for the unmatched-right tail of RIGHT / FULL.
- Residual non-equi conjuncts are re-checked per probed candidate (a conjunct passes only when it evaluates to `true`, matching the streaming path's `== true` gate).
- Falls back to the nested-loop operators below for non-equi ON predicates, the lateral / derived-table right sides the materialization pass below declines, CROSS / APPLY, and key-type pairs `SqlType.Promote` rejects (LOB, collation conflict, cross-category) — preserving their exact per-row error behavior.

MERGE's own match phase hashes its source the same way when the target can't be seeked, over its two name spaces rather than a `FromSource[]`; the key-type rule is literally shared (`TryPromoteComparableKeyTypes`).
See [`dml.md`](dml.md#match-strategies).

### Deferred sources materialize once per enumeration

A `LateralPlan` source — a derived table, a CTE reference, a view, a catalog view, a TVF, `VALUES`, `OPENJSON` — is re-executed per left-side row by the streaming operators, and `TryPlanEquiJoin` rejects it outright (line "`sources[level].LateralPlan is not null`").
The execution-time `MaterializeUncorrelatedDeferredSources` pass (`Selection.Execution.cs`, run at the top of the row-source closure before any projection path builds its resolver closures) replaces the ones whose rows can't change across one enumeration with a once-materialized `Rows` list, so the nested loop stops re-executing them and `TryPlanEquiJoin` keys them into the O(L + R) hash build.
It runs *after* the WHERE pushdown above, so what a source materializes is already narrowed.
It clones the array rather than mutating the plan's own `FromSource[]`, per the shared-plan contract in [`plan-cache.md`](plan-cache.md).

Two kinds of source qualify:

- A **`MaterializeOnce` catalog view**, wherever it sits: its generator takes no outer resolver, so it can't correlate.
  This removes both the per-outer-row re-generation and the O(L × R) loop from catalog multi-joins (SMO's per-column property-bag query) — see [`catalog-views.md`](catalog-views.md) for the correlation-safety contract and measured improvement.
- A **non-leftmost, non-APPLY source whose plan is a query body with its own name scope** — a derived table, a CTE reference, or a view.
  SQL Server requires `APPLY` for laterality, so such a body can't read a sibling FROM source: the simulator's parser reports Msg 207 where real reports Msg 4104.
  It *can* read an enclosing statement's row, but that row is fixed for one execution of this `Selection` (the enclosing query re-executes the whole plan per enclosing row), so every re-execution within one enumeration would return identical rows.

The leftmost source stays deferred — a fold range's leftmost slot already executes its plan once and streams, so materializing it buys nothing and costs the buffer.
The leftmost slot of a parenthesized join group is skipped for the same reason (`GroupJoin` materializes the whole interior as a unit).

**A generator-backed source never qualifies.**
A TVF, `VALUES`, `OPENJSON` / `OPENXML`, `STRING_SPLIT`, PIVOT or a linked-server query takes *arguments parsed in the enclosing FROM's scope*, so it can read a sibling source's column per row even under a plain JOIN.
Real rejects that shape outright — `FROM t JOIN STRING_SPLIT(t.csv, ',') s ON …` and its comma spelling are Msg 4104 there, probe-confirmed — while the simulator answers it, so materializing would silently freeze the first left row's argument values instead of raising.

**A per-call-varying built-in declines the reuse.**
`SimulatedDbConnection.VolatileEvaluations` is sampled around the materializing execution — the same gate the uncorrelated-subquery memo applies, see [`subqueries.md`](subqueries.md) — and a plan that drew a `NEWID()` keeps its per-row execution however uncorrelated it is.
Probe-confirmed against SQL Server 2025: a one-row `(SELECT TOP 1 NEWID() AS g FROM …)` joined to a ten-row left side yields ten distinct values there under CROSS JOIN, INNER JOIN, LEFT JOIN and a CTE reference alike, so replaying one draw would be a fidelity regression.
`RAND()` needs no gate — both engines freeze it for the statement.
The declining source's probing execution is discarded; `NEXT VALUE FOR`, the other counter-bumping built-in, is Msg 11719 on real inside any of these bodies, so the discarded execution's only reachable side effect is an unobservable extra `NEWID()` draw.

Measured on the WWI report shape `Customers JOIN (SELECT CustomerID, SUM(…) FROM Invoices JOIN InvoiceLines … GROUP BY CustomerID) agg ON …`: **77.6 s → 170 ms** (0.8× the live server), the CTE spelling of the same query **78.8 s → 165 ms**, and the same query written derived-table-first unchanged at ~148 ms.

A joined UPDATE / DELETE takes this pass too, through `Selection.PrepareMutationJoinSources` — the same gates, the same volatility decline, and no reorder — see [`dml.md`](dml.md#joined-row-sources).
It declines in skip mode: a skipped statement commits nothing, so the pass is pure cost there and the materializing execution would run a body on behalf of a statement that never runs.

**Divergence — a body that raises is evaluated even when the left side is empty.**
The pass runs the plan to completion before the join driver asks for a row, so `FROM <empty t> JOIN (<body that raises at runtime>) d ON …` raises where real, which never drives a row into the derived table, answers the empty rowset (probe-confirmed against SQL Server 2025, for both the SELECT and the joined-UPDATE spelling).
A non-empty left side raises on both engines.
Making the materialization demand-driven would close it, but the volatility gate samples `VolatileEvaluations` *around* the execution to decide whether the source may be reused at all, so the decision can't be deferred to the first demand without restructuring that gate.

### Nested-loop fallback

INNER / CROSS / LEFT / CROSS APPLY / OUTER APPLY stream one upstream tuple at a time.
RIGHT / FULL materialize `sources[level].Rows` into a list and track a `matched[]` bitmap across the entire upstream iteration so unmatched right rows can be emitted (with all prior slots NULL-filled) after upstream is exhausted.

**RIGHT / FULL with a derived-table right side** materialize the lateral plan once via the enclosing-scope `outerResolver` (not the joined-tuple resolver) — these operators did it before the shared materialization pass existed and still hold the fallback for the sources the pass declines — so non-correlated and outer-correlated derived tables work; lateral correlation to the left side is rejected because the derived-table parse doesn't wire the left-source snapshot resolver — left-side references raise Msg 207 ("Invalid column name") at runtime when `Reference.Run` hits the null outer resolver.
Real SQL Server raises Msg 4104 at bind time for the same shape; different code, same end state.

**Table-value-constructor (`(VALUES …) alias(cols)`) sources** are one more `LateralPlan` shape: a `CROSS` / `OUTER APPLY` VALUES source correlates to the left row (its cell expressions re-evaluate per outer tuple through the joined-tuple resolver), and `JoinDriver` treats it exactly like a derived-table SELECT right side.
Parsing / type-promotion / error surface live in [`query.md`](query.md) (projection section).

## EF Core mapping

EF Core 10's LINQ `LeftJoin` / `RightJoin` operators translate to LEFT / RIGHT JOIN respectively and route through this pipeline.
.NET 10 LINQ doesn't expose a `FullJoin` operator, so FULL OUTER JOIN is reachable only via raw SQL.

## Strategy guard (test diagnostics)

The strategy chosen per join is recorded through the opt-in `JoinDiagnostics.Sink` — a `[ThreadStatic]` ambient list, null by default.
Most kinds log at the single `ApplyJoin` dispatch point; INNER / LEFT equi-joins log from inside `EquiJoinSeekOrHash` once the seek-vs-hash choice is made (`NestedLoopIndexSeek(keys=N)` vs `HashMatch(keys=N,residual=M)`).
A reordered chain logs one `Reorder(i,j,k,…)` entry naming the placement order in **written** source indices, so `Reorder(2,1,0)` reads "drive from the third-written source"; its absence means the written order stood.
`Tests.Internal/JoinStrategyTests` reads it to assert the per-outer seek engages for a small filtered outer with an indexed inner, the hash build for a large outer or unindexed inner, the nested loop for non-equi / CROSS, and each condition that engages or declines the reorder — guarding against a silent fall-back to the O(L × R) loop, a perf regression the correctness suite wouldn't catch.
The result-level counterpart is `Tests`' `JoinPredicatePushdownTests`, which pins the rows each shape produces either way.

MERGE's match phase logs into the same sink (`Merge:TargetSeek` / `Merge:HashMatch(keys=N,residual=M)` / `Merge:Scan`), guarded by `Tests.Internal/MergeMatchStrategyTests` — see [`dml.md`](dml.md#match-strategies).
