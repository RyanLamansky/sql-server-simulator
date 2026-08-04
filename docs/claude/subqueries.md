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

### The hashed `IN` probe

An uncorrelated `IN (SELECT …)` materializes its inner column once — non-NULL values in row order, plus a `sawNull` flag — and builds a `HashSet<SqlValue>` over them, so the per-row membership test is a lookup rather than a walk of the inner result.
`SqlValue`'s own `Equals` / `GetHashCode` pair carries the collation semantics (case folding, ANSI trailing-space padding), so the hash agrees with the `=` the walk would have run.

The probe set is built only where promotion stays inside one type family (same `SqlTypeCategory`, neither side LOB, category not `Other`), so coercing a value to the common type is a widening rather than the value-dependent conversion a cross-family pair runs.
That matters for error fidelity: `int IN (SELECT <varchar column>)` has to raise its Msg 245 from the comparison, in row order, not while a probe set is being built — so that pair declines the hash and takes the walk.
The set is also keyed by the LHS type it was built against; an LHS of any other type (a `sql_variant` row, a runtime-narrowed value) falls back to the walk for that row.
A correlated `IN` keeps the per-row streaming walk, short-circuiting on the first match.

### Divergences

The materializing first execution reads the inner plan to completion, where the pre-existing per-row walk short-circuited on the first match.
A runtime error carried by a *later* inner row therefore surfaces where a lucky early match used to hide it — which is the more faithful direction, since real materializes the subquery once.

Set ops (`UNION`/`UNION ALL`/`INTERSECT`/`EXCEPT`) are legal in every subquery context (via `Selection.Parse` → `ParseQueryExpression`), so EF Core 7+'s TPC shape (UNION ALL in a derived table) ships end-to-end.

Set-op semantics themselves (dedup rules, NULL-equality in set-op matching, precedence, ORDER BY placement) are covered in [`query.md`](query.md).
