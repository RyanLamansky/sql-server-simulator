# Subqueries

`EXISTS`/`NOT EXISTS` (multi-column inner OK); `expr [NOT] IN (SELECT …)` (single inner column, Msg 116); scalar `(SELECT col FROM …)` (single column, single-row Msg 512 per outer row, empty → typed NULL); `expr <op> {ANY|SOME|ALL} (SELECT col …)` quantified comparison, all six operators + T-SQL synonyms (`!=` `!<` `!>`), predicate-only (SELECT-list use → Msg 102 at the operator); SOME aliases ANY.

Redundant parentheses around an `EXISTS` subquery are accepted at any depth — `EXISTS((SELECT …))`, `EXISTS(((SELECT …)))` (probe-confirmed legal on SQL Server 2025; DacFx emits the doubly-parenthesized form in its extended-properties reverse-engineering query).
`ParseExists` (`BooleanExpression.cs`) counts the extra opening parens after the mandatory first one and demands a matching close-paren count; an unbalanced close raises Msg 102.
The `IN ((SELECT …))` and scalar `((SELECT …))` forms already accepted the extra parens naturally — `(SELECT …)` is a parenthesized scalar-subquery expression that the generic primary-expression / IN-list parser wraps, so no special-casing was needed there.

Three-valued semantics: empty inner → ALL vacuously true / ANY vacuously false (independent of LHS NULL); a NULL on either side taints to UNKNOWN.

All forms correlate at arbitrary depth (via `outerResolver` / `outerTypeResolver` — see the Selection notes in the root CLAUDE.md architecture section).

## An outer-independent inner plan runs once per statement

A correlated subquery re-executes its inner `Selection` per outer row; one that never reads the enclosing row executes **once for the whole statement** and every later row reads the memoized result (`Parser/UncorrelatedSubqueryCache.cs`).
All four consuming shapes share it — the scalar `(SELECT …)`, `EXISTS`, `[NOT] IN (SELECT …)` and the quantified comparison — each memoizing its own result shape under its own expression instance.

**Detection is a runtime probe on the first execution, not a parse-time analysis.**
The site wraps the caller's outer-row resolver in a delegate that latches when it is consulted, runs the plan under that wrapper, and asks afterwards whether the latch tripped.
An untripped latch means the work that produced this result never looked at the outer row; data being fixed for the statement's duration, every later row would recompute the identical value.
A tripped latch records the site as per-row and it executes exactly as it did before — the probing execution's own result is still correct for the row that triggered it, so nothing is wasted, and the recorded verdict skips the probe allocation for the rest of the statement.
The reasoning survives partial consumption (`EXISTS` stops at the first row): what matters is that the *consumed prefix* was produced without reading the outer row, which makes that prefix — and the answer drawn from it — the same for every outer row.

Two sites in one statement are memoized independently, and the scope is the statement rather than the lateral invocation: a subquery under a `CROSS APPLY` that re-runs per outer row still executes once when it reads neither scope.

Results live on `StatementContext.SubqueryResults` (keyed by expression instance, reference identity), never on the expression — the shared-plan contract in [`plan-cache.md`](plan-cache.md#the-shared-plan-contract-per-execution-state-lives-per-execution) requires it, since one `Selection` is executed by many commands, possibly concurrently.
The dispatch loop clears the memo at the top of each statement alongside the `UtcNow` refresh, so a `WHILE` body's next iteration re-reads a table the previous iteration wrote.

### A per-call-varying built-in declines the reuse

`SimulatedDbConnection.VolatileEvaluations` counts evaluations of the built-ins whose value differs between two calls inside one statement — `NEWID()` and `NEXT VALUE FOR`.
It is sampled around the probing execution, and a plan that moved it is recorded as per-row however outer-independent it is.

This is fidelity, not caution.
Probe-confirmed against SQL Server 2025: `SELECT COUNT(DISTINCT g) FROM (SELECT (SELECT TOP 1 NEWID() FROM Sales.Customers) AS g FROM Sales.Orders WHERE OrderID <= 100) x` reports **100** distinct values on real — it re-draws per outer row — while `RAND()` in the same shape reports **1**.
`RAND()` and the current-time family therefore need no gate: both engines already freeze them for the statement (`StatementContext`).

The counter has a second consumer with the same reasoning: the once-per-enumeration materialization of a deferred FROM source, in [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration).

## An equi-correlated body switches to a hash semi / anti-join

A correlated `EXISTS` / `NOT EXISTS` / `[NOT] IN (SELECT …)` re-executes its inner plan per outer row.
Past **128 evaluations within one statement** the site executes a **decorrelated key plan** once instead — the same body with its correlation equalities removed and its projection replaced by the inner columns those equalities named — and every later row probes the result (`Parser/SemiJoinIndex.cs`, `Parser/Selection.SemiJoin.cs`).
The quantified `ANY` / `SOME` / `ALL` forms keep their per-row comparison.

**Eligibility is classified once at parse** and captured in the plan (so it is value-independent and survives the plan cache).
The WHERE has to split into *correlation equi-conjuncts* — `<bare column of this body> = <enclosing-scope expression>`, the value side taking the same stability rule an index seek's probe does and naming at least one enclosing column — plus a residual reading only this body's own sources.
Everything that reads the row set as a whole declines, because the body's answer would then depend on *which* rows the correlation kept: `DISTINCT`, `TOP` / `OFFSET` / `FETCH`, `GROUP BY` / `HAVING`, an aggregate, a window, an `ORDER BY`.
So does a correlated reference anywhere but those conjuncts — a residual conjunct, a JOIN `ON`, the projection — and a key pair whose types `TryPromoteComparableKeyTypes` won't settle (the same gate the equi-join hash buckets rest on, so a bucket means what evaluating the `=` meant: collation folding, ANSI trailing-space padding, cross-width promotion).

**The switch is adaptive**, mirroring `EquiJoinSeekOrHash`'s philosophy.
Below the threshold nothing changes, so a small outer never pays a build it can't amortize.
Past it, an inner the per-row path **seeks** (a lone base table with a key / index leading on a correlation column) keeps running per row until the outer reaches a quarter of that table's row count — the build costs one pass over the whole table while the per-row path pays only for the rows each key selects, the same conservative 4:1 crossover the join planner uses.
An inner with no such index is a scan per outer row and takes no delay.

**The one-shot execution runs under the correlation latch** the outer-independence probe already owns (`OuterRowProbe`): a key plan that consulted the outer row — a correlation hidden inside a nested subquery, which the parse-time classification doesn't see into — or that drew a per-call-varying built-in declines the site for the rest of the statement, and the row that triggered the build falls back to its own per-row execution.
An error raised while building declines the same way rather than surfacing, so the per-row path stays the one that decides whether a row's inner result raises: this transform reads rows a short-circuiting per-row evaluation might never touch, but it never converts that into an error the query didn't have.

**NULL rules** (probed against SQL Server 2025 as the four forms × NULL outer key × NULL inner key × NULL inner projection matrix, and identical to what the per-row path already answered):

- A row whose **inner** key has a NULL component is dropped while building — `NULL = NULL` is UNKNOWN, so it equi-matches no outer key, a NULL one included.
- A NULL **outer** key selects no inner row: `EXISTS` false, `NOT EXISTS` true, `IN` false, `NOT IN` true.
- `IN` / `NOT IN` carry **`sawNull` per correlation key**, not globally: a NULL projection under key *k* turns a miss into UNKNOWN only for the outer rows whose key is *k*. A key no inner row carries is a definite miss (false / true) however many NULLs the other keys' groups hold.

### A NULL left side settles on the inner's emptiness

`x IN (S)` is an OR of `x = s` over S, so a NULL left side has no bearing on the answer beyond whether S is empty: an OR over no elements is FALSE whatever `x` is, and one over a non-empty S is UNKNOWN because every comparison against NULL is.
`InSubqueryExpression.NullLeftSide` reads exactly that — `negated` (FALSE for `IN`, TRUE for `NOT IN`) when the body produced no row, UNKNOWN when it produced any — through the same three sources of inner rows the value path uses, so the decorrelated per-key index, the statement's materialized memo and the per-row execution all answer identically.
Per correlation key, a key no inner row carries (a NULL-component key included) is the empty case.

Probed against SQL Server 2025 (2026-08-05): `NULL IN (SELECT v FROM t WHERE 1 = 0)` is `F` and `NULL NOT IN (…)` is `T`, the same pair over a non-empty body is `U` in both directions, and a body whose only row is NULL is non-empty and therefore `U`.
A `TOP 0` body and a `(VALUES …) WHERE 1 = 0` body are two more ways to be empty.

**Divergence — the emptiness probe reads one row.**
Real needs the body's *shape* and not its values, so a projection that raises answers UNKNOWN there (`NULL IN (SELECT 1/0 FROM <non-empty>)`) where the simulator raises Msg 8134 from projecting the first row.
Its **WHERE** raises on both, since emptiness can't be known without evaluating it.
`EXISTS` carries the same divergence for the same reason and pre-dates this.

### The hashed `IN` probe

An uncorrelated `IN (SELECT …)` materializes its inner column once — non-NULL values in row order, plus a `sawNull` flag — and builds a `HashSet<SqlValue>` over them, so the per-row membership test is a lookup rather than a walk of the inner result.
`SqlValue`'s own `Equals` / `GetHashCode` pair carries the collation semantics (case folding, ANSI trailing-space padding), so the hash agrees with the `=` the walk would have run.

The probe set is built only where promotion stays inside one type family (same `SqlTypeCategory`, neither side LOB, category not `Other`), so coercing a value to the common type is a widening rather than the value-dependent conversion a cross-family pair runs.
That matters for error fidelity: `int IN (SELECT <varchar column>)` has to raise its Msg 245 from the comparison, in row order, not while a probe set is being built — so that pair declines the hash and takes the walk.
The set is also keyed by the LHS type it was built against; an LHS of any other type (a `sql_variant` row, a runtime-narrowed value) falls back to the walk for that row.
A correlated `IN` keeps the per-row streaming walk, short-circuiting on the first match, until the semi-join switch above takes over.

### A small uncorrelated `IN` set drives the read

An uncorrelated `col IN (SELECT …)` whose materialized set is at most **64** non-NULL values exposes them to the index seek as an equality family on `col`, so a read whose subject column **leads** some key / index *drives from the values* — one seek per value — instead of scanning the outer and probing every row against the hash.
It is the same family a written `IN (1, 2, 3)` list decomposes into, and it rides the seek's existing per-column probe expansion.

The values only exist once the body has run, so the seek planner asks the conjunct for its subject first (an indexable, key-leading column of the source it is narrowing) and materializes only then — through the statement's own subquery memo, so the per-row evaluation reads the same values rather than running the body twice, and a **correlated** body simply records itself as per-row there and declines.
`NOT IN` never drives (its matches are the complement).
NULL values are left out of the probes — they equi-match nothing — and the `IN` conjunct stays in the residual WHERE like every other matched conjunct, which is what keeps the three-valued answer exact for the rows the seek selected and makes the rows it skipped ones the predicate answered FALSE or UNKNOWN for anyway.

Measured on WideWorldImporters (`Sales.Orders.CustomerID IN (SELECT … FROM Sales.Customers WHERE CustomerID IN (801, 802, 803))`): **17.3 ms → 0.2 ms**, against 1.4 ms live.

### Divergences

The materializing first execution reads the inner plan to completion, where the pre-existing per-row walk short-circuited on the first match.
A runtime error carried by a *later* inner row therefore surfaces where a lucky early match used to hide it — which is the more faithful direction, since real materializes the subquery once.

Set ops (`UNION`/`UNION ALL`/`INTERSECT`/`EXCEPT`) are legal in every subquery context (via `Selection.Parse` → `ParseQueryExpression`), so EF Core 7+'s TPC shape (UNION ALL in a derived table) ships end-to-end.

Set-op semantics themselves (dedup rules, NULL-equality in set-op matching, precedence, ORDER BY placement) are covered in [`query.md`](query.md).
