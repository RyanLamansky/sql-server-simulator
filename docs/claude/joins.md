# JOINs / APPLY

INNER / bare `JOIN` (= INNER) / LEFT [OUTER] / RIGHT [OUTER] / FULL [OUTER] / CROSS / CROSS APPLY / OUTER APPLY. Multi-table chains compose left-to-right. ON-predicate UNKNOWN excludes. APPLY is the lateral form: the right side is re-executed per outer row and takes no ON clause. Execution lives in `Selection.Execution.Joins.cs`.

## Comma-separated FROM (ANSI-89)

`FROM a, b WHERE a.id = b.id` parses as a sequence of explicit-join chains spliced with `JoinKind.Cross` joins. Each comma starts a fresh chain via the same `ParseExplicitJoinChain` helper the JOIN-keyword loop calls, so any explicit JOINs *within* a chain bind before the cross-splice. See the quirk in the root CLAUDE.md about a back-reference-across-comma case that diverges toward more-permissive.

## JoinDriver

`JoinDriver` is a fold over `joins[]`: the leftmost rowset is wrapped with each join's operator in turn to produce the final enumerator. `ApplyJoin` picks the operator per join level — and is the single point where the strategy (hash vs nested loop) is decided.

### Equi-join fast path

`TryPlanEquiJoin` splits an INNER / LEFT / RIGHT / FULL ON predicate into `left.col = right.col` conjuncts (bare-column `Reference`s only, each classified by a single `FindSourceColumn` lookup) plus a residual of everything else. With ≥1 equi-key it routes to `HashEquiJoin`, which materializes and indexes the right source by the promoted keys and probes once per left row — O(L + R) vs the nested loop's O(L × R) (an AdventureWorks 9-table view drops from a multi-minute hang to sub-second).

- Bucket keys reuse GROUP BY's collation-consistent `SqlValueKey`, coercing both sides to the `SqlType.Promote` common type so equality matches the `=` operator exactly.
- NULL keys are excluded (NULL = NULL is UNKNOWN) but retained for the unmatched-right tail of RIGHT / FULL.
- Residual non-equi conjuncts are re-checked per probed candidate (a conjunct passes only when it evaluates to `true`, matching the streaming path's `== true` gate).
- Falls back to the nested-loop operators below for non-equi ON predicates, lateral / derived-table right sides, CROSS / APPLY, and key-type pairs `SqlType.Promote` rejects (LOB, collation conflict, cross-category) — preserving their exact per-row error behavior.

### Nested-loop fallback

INNER / CROSS / LEFT / CROSS APPLY / OUTER APPLY stream one upstream tuple at a time. RIGHT / FULL materialize `sources[level].Rows` into a list and track a `matched[]` bitmap across the entire upstream iteration so unmatched right rows can be emitted (with all prior slots NULL-filled) after upstream is exhausted.

**RIGHT / FULL with a derived-table right side** materialize the lateral plan once via the enclosing-scope `outerResolver` (not the joined-tuple resolver), so non-correlated and outer-correlated derived tables work; lateral correlation to the left side is rejected because the derived-table parse doesn't wire the left-source snapshot resolver — left-side references raise Msg 207 ("Invalid column name") at runtime when `Reference.Run` hits the null outer resolver. Real SQL Server raises Msg 4104 at bind time for the same shape; different code, same end state.

## EF Core mapping

EF Core 10's LINQ `LeftJoin` / `RightJoin` operators translate to LEFT / RIGHT JOIN respectively and route through this pipeline. .NET 10 LINQ doesn't expose a `FullJoin` operator, so FULL OUTER JOIN is reachable only via raw SQL.

## Strategy guard (test diagnostics)

The strategy chosen per join is recorded through the opt-in `JoinDiagnostics.Sink` — a `[ThreadStatic]` ambient list, null by default, written at the single `ApplyJoin` dispatch point so it can't drift from the real decision (off-state cost is one null check per join, never per-row). `Tests.Internal/JoinStrategyTests` reads it to assert the hash path engages for equi-joins and the nested loop for non-equi / CROSS — guarding against a silent fall-back to the O(L × R) loop, a perf regression the correctness suite wouldn't catch.
