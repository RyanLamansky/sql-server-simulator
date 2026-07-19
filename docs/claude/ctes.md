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

`OPTION (MAXRECURSION N)` parses inside `Selection.ParseQueryExpression` after ORDER BY/OFFSET/FETCH and writes to every binding in scope.
Other hints (`OPTIMIZE FOR`, `RECOMPILE`, etc.) → `NotSupportedException`.
EF emits non-recursive CTEs in some shapes (TPC inheritance, certain Distinct/OrderBy/Skip patterns); recursive CTEs only via raw SQL.
