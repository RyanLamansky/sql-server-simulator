# Common table expressions

`WITH name [(col, …)] AS (SELECT …) [, …] {SELECT|INSERT|UPDATE|DELETE|MERGE} …`.
WITH prefix scopes to exactly one immediately-following statement.
Both non-recursive and recursive forms modeled.

CTE name shadows a real table for the prefixed statement.
Multiple comma-separated CTEs cascade — later ones see earlier ones.

**Recursive form**: anchor branches (no self-ref) run once into a seed rowset; recursive branches (one self-ref each) iterate against the previous rowset until empty or `MaxRecursion` trips Msg 530.
Default 100; `OPTION (MAXRECURSION N)` overrides; `0` disables.

Errors:
- **Msg 240**: anchor and recursive parts produce different per-column types (strict type-equality, no Promote-style widening — must explicitly cast).
- **Msg 247**: anchor branch appears after a recursive branch.
- **Msg 252**: self-reference but no top-level UNION ALL splitting it from an anchor; also fires when UNION-without-ALL is used between branches.
- **Msg 253**: one recursive branch references the CTE more than once.
- **Msg 530**: MAXRECURSION exceeded.
- **Msg 239** duplicate CTE name; **Msg 8158**/**8159** rename-list count mismatch; **Msg 1033** ORDER BY in CTE body without TOP/OFFSET/FETCH.

## Recursive-member restrictions

SQL Server forbids a set of constructs in a recursive CTE's **recursive member**, each with its own error.
All probe-confirmed verbatim against SQL Server 2025 (2026-07-31):

| Code | Construct |
| --- | --- |
| 460 | `SELECT DISTINCT` |
| 461 | `TOP`, `OFFSET` or `FETCH` |
| 462 | an outer join |
| 467 | `GROUP BY`, `HAVING` or an aggregate |

The restriction covers the recursive member's **whole text**, not just its own SELECT: a `DISTINCT` in a nested subquery, a derived table joined into the member, or an aggregate in a scalar subquery all trip it.
That's why the parser records these at their parse sites (`ParserContext.RecursiveBranchConstructs`) rather than reading them off the member's plan.

They're recorded rather than raised on sight because a branch only becomes *the recursive member* once its parse turns up a self-reference, which can come after the construct — `Simulation.With.cs` raises once the branch is classified.
The **anchor** member and a **non-recursive** CTE keep every one of these constructs.

Two related rejections fire earlier and differ from real in *which* error, not in whether the query is refused: an `ORDER BY` in the CTE body is Msg 102 where real gives Msg 1033, and two self-references in one member surface as Msg 209 (ambiguous column) before the Msg 253 check meant for them.

`OPTION (MAXRECURSION N)` parses inside `Selection.ParseQueryExpression` after ORDER BY/OFFSET/FETCH and writes to every binding in scope.
Other hints (`OPTIMIZE FOR`, `RECOMPILE`, etc.) → `NotSupportedException`.
EF emits non-recursive CTEs in some shapes (TPC inheritance, certain Distinct/OrderBy/Skip patterns); recursive CTEs only via raw SQL.
