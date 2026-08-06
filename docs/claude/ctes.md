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

**Msg 1033 covers all five constructs its own text names**, not just the CTE: a view body, an inline function's body, a derived table, and every subquery shape (scalar, `EXISTS`, the quantified comparisons, `IN`) take the same test — `HasOrderBy && !HasTopOrOffsetOrFetch` — at four seams: `Simulation.With.cs` and `Simulation.CreateView.cs` for the two stored bodies, the derived-table arm of the FROM parser, and `Expression.ParseSubqueryRejectingNextValueFor`, which every subquery funnels through.
A companion `TOP`, `OFFSET` or `FETCH` clears it in each (probe-confirmed against SQL Server 2025 on 2026-08-06; `OFFSET 0 ROWS` clears the rejection without dropping a row).
The parenthesized `INSERT … (SELECT …)` source is *not* one of them and takes a stricter rule — real refuses an ORDER BY there even with TOP, as Msg 156 — see [`backlog.md`](backlog.md).

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

## An unread prefix — Msg 422

Real refuses a `WITH` prefix on exactly one statement shape: a bare
`SELECT <expression list>` — no FROM, WHERE, ORDER BY, TOP, DISTINCT, INTO, set operator, subquery, `FOR JSON` / `FOR XML` or `OPTION` clause.
**Msg 422** class 16 state 4, `"Common table expression defined but not used."`, naming no CTE.
Probed against SQL Server 2025 (2026-08-05):

| Statement | Real |
| --- | --- |
| `WITH c AS (…) SELECT 1` | Msg 422 |
| `WITH c AS (…) SELECT 1, 2` / `SELECT getdate()` / `SELECT NULL` / `SELECT @v = 1` | Msg 422 |
| `WITH c1 AS (…), c2 AS (SELECT * FROM c1) SELECT 1` | Msg 422 — a CTE reading a CTE is not a use of the prefix |
| `WITH c AS (…) SELECT 1 WHERE 1 = 1` / `ORDER BY 1` / `TOP 1 1` / `DISTINCT 1` | runs |
| `WITH c AS (…) SELECT 1 UNION SELECT 2` / `SELECT (SELECT MAX(a) FROM t)` | runs |
| `WITH c AS (…) SELECT 1 FOR JSON PATH` / `OPTION (MAXDOP 1)` | runs |
| `WITH c1 AS (…), c2 AS (…), c3 AS (…) SELECT * FROM c1` | runs — one read is enough for the whole prefix |
| `WITH c AS (…) INSERT … / UPDATE … / DELETE … / MERGE … / SELECT … INTO …` | runs |

That shape can name no CTE — it has no FROM and no subquery — so the diagnostic is settled from the statement's shape alone, at the point the outermost query expression finishes parsing.
A stored body that parses outside the dispatch loop (a view, an inline TVF, a cursor declaration) never asks the question, which is what keeps those accepting the shape as real does; a procedure's body dispatches statements of its own, so its bare-projection statement raises at `CREATE` like real's.

One divergence remains: real doesn't bind an unread CTE's body at all, so `WITH c AS (SELECT * FROM nosuchtable) SELECT 1` is Msg 422 there and Msg 208 here.
Both refuse.

## Where a prefix may appear

A **statement** may carry one, and so may a **stored body** — but a *parenthesized query* may not, on real or here.

The statement side is the dispatch loop: it clears `ParserContext.CteBindings` per statement and repopulates from a leading WITH before the switch dispatches.
That covers every module whose body is a statement sequence — stored procedures, triggers, multi-statement TVFs, scalar UDF bodies, dynamic SQL.

`WITH XMLNAMESPACES (…)` shares the prefix and so shares this seam, registering on `ParserContext.XmlNamespaces` beside the bindings; it may lead the list (`WITH XMLNAMESPACES (…), c AS (…) SELECT …`) but not follow a CTE → [`xml.md`](xml.md#with-xmlnamespaces).

A stored body is its own parse unit and never reaches that loop, so the prefix is recognized at the body-parse seam instead: `Simulation.ParseBodyQuery` (an optional WITH prefix + `Selection.Parse` at depth 0), shared by every site that parses a body's query.
Those sites are `CREATE` / `ALTER VIEW` and each later re-parse of a view's stored text (invocation, indexed-view materialization, shape analysis, base-table collection), the inline TVF's `RETURN` body at both create and invoke, and `DECLARE … CURSOR FOR`.
The view forms compose with everything else the header carries: the column-rename list, `WITH SCHEMABINDING`, a trailing `WITH CHECK OPTION`, `ALTER VIEW` / `CREATE OR ALTER VIEW`, and the body's own Msg 1033 ORDER BY rule.
A recursive CTE works in a body the same as at statement level — the body re-parses per invocation, so each execution owns fresh bindings.

Parenthesized query positions never route through that seam and so keep rejecting the WITH, which is what real does: a derived table, a scalar or `IN` subquery, a scalar UDF's `RETURN (…)` expression, and `MERGE … USING (…)` all answer **Msg 156** (`Incorrect syntax near the keyword 'with'.`).
Real follows that with Msg 319 and Msg 102; the simulator raises the first.
The FROM-clause leg takes one deliberate step to get there: a `(` whose first interior token is WITH is classified as a derived table rather than a parenthesized join group, so the rejection carries 156 instead of the join group's 102.

Two body-side interactions live in their own features:

- **Schema binding** excludes the names a body's own WITH prefix declares from the Msg 4512 two-part-name rule — a one-part CTE reference is the CTE even when the default schema holds a table of that name, whereas a real one-part table reference *inside* a CTE definition still trips it → [`programmable.md`](programmable.md#schema-binding-with-schemabinding).
- **A CTE-bearing view can't be indexed**: `CREATE INDEX` on it is **Msg 10137**, naming the first CTE the body declares → [`indexes.md`](indexes.md).

### Not modeled yet

DML through a CTE-bodied view (`INSERT` / `UPDATE` / `DELETE` against `CREATE VIEW v AS WITH c AS (SELECT … FROM t) SELECT … FROM c`) reports Msg 4403, where real passes it through to the base table → [`programmable.md`](programmable.md#updatable-views-dml-through-views).
