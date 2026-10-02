# Query semantics — set ops, CASE, pagination, aggregates, windows

## Boolean / set ops / projection / CASE
- Boolean combinators (WHERE / MERGE-ON / CHECK): `AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `IS [NOT] DISTINCT FROM` (SQL Server 2022 NULL-safe equality — never UNKNOWN), `[NOT] IN (literal,...)`, `[NOT] IN (SELECT ...)`, `EXISTS (SELECT ...)`, `expr <op> {ANY|SOME|ALL} (SELECT col FROM ...)` quantified comparison, `value [NOT] BETWEEN lower AND upper`.
  Tri-valued except for the two `IS`-family forms (always definitive).
- `IS [NOT] DISTINCT FROM` (`BooleanExpression.DistinctFromExpression`): two NULLs are *not* distinct (match), exactly one NULL *is* distinct (no match), two non-NULLs distinct iff unequal under the regular promote-and-compare.
  Type-mismatch operand pairs still surface the underlying Msg 245 / Msg 402 — the NULL-safety lives in the per-side null check, the value-side coerces normally.
  The pair is checked against the comparison grid while compiling, with the operator spelled `is not`; two `xml` operands are the exception, compiling and refusing only a row where both carry a value (Msg 305, state 3).
  Reachable in any boolean context (WHERE / HAVING / ON / CASE-WHEN / CHECK); bare SELECT-list use raises Msg 156 in real SQL Server because `IS` isn't a value operator.
- `[NOT] BETWEEN` desugars to `value >= lower AND value <= upper` (inclusive on both ends, probe-confirmed).
  Reversed bounds (low > high) collapse to a definite false; NULL in any operand position propagates through three-valued AND.
  `value` is evaluated once per row by `BetweenExpression.Run` (the desugaring is per-row semantics, not duplicated evaluation), and the desugared halves are evaluated **left to right with the AND's own short-circuit**: a lower half that answers false settles the range, so `b BETWEEN 99999 AND b / 0` answers rows where `b BETWEEN 0 AND b / 0` is Msg 8134, and the same holds under `NOT` (probe-confirmed).
  An UNKNOWN lower half still evaluates the upper one — UNKNOWN AND FALSE is FALSE, which is what makes `CHECK (x BETWEEN NULL AND 5)` reject `x = 10`.
  BETWEEN binds tighter than the surrounding AND/OR, so `value between a and b and other` parses as `(value between a and b) AND (other)`; bounds may be arbitrary value expressions (Expression.Parse stops at the trailing AND keyword).
- Quantified comparison (`Parser/BooleanExpression.cs` — `QuantifiedComparisonExpression`): all six operators (`=`, `<>`, `<`, `<=`, `>`, `>=`) plus T-SQL synonyms (`!=` → `<>`, `!<` → `>=`, `!>` → `<=`) fold at parse time into a `ComparisonOp` enum.
  `SOME` is a pure alias of `ANY`.
  Inner SELECT must project exactly one column (Msg 116, shared factory with IN-subquery).
  **Predicate-only**: probe-confirmed that real SQL Server raises Msg 102 when the form appears in a SELECT-list expression slot — the simulator inherits the same restriction because parsing lives in `BooleanExpression.ParseComparison`, reachable only from the boolean-atom path.
  Empty inner: `ALL` is vacuously true, `ANY` vacuously false (LHS NULL irrelevant — the inner is consumed first).
  Non-empty inner runs three-valued fold: any comparison evaluating to `true` short-circuits `ANY` to `true`; any `false` short-circuits `ALL` to `false`; otherwise `null`-tainted comparisons turn the overall result UNKNOWN, fall-through is `false` for `ANY` / `true` for `ALL`.
  `<op> ANY` cannot be negated directly (`NOT <op> ANY` isn't grammar — apps must flip the operator: `NOT (x > ALL y)` ≡ `x <= ANY y`).
- Set ops (UNION / UNION ALL / INTERSECT / EXCEPT): standard precedence (INTERSECT > UNION/EXCEPT).
  **NULLs are equal during set-op dedup/matching** (opposite of `=`'s tri-state).
  An untyped `NULL` column takes its partner branch's type rather than its placeholder `int` (`SELECT NULL UNION ALL SELECT 'a'` is `varchar`), and stays untyped through a chain of them until a typed branch arrives (`Selection.ColumnIsUntypedNull`, probed 2026-09-23).
  Per-branch ORDER BY in non-final branch → Msg 156.
  `INTERSECT ALL` / `EXCEPT ALL` parse and are Msg 324, states 1 and 2 (probed 2026-10-01 against SQL Server 2025).
  A branch may be **parenthesized**, and the parentheses may wrap a whole nested chain rather than a single SELECT — `SELECT … UNION (SELECT … UNION SELECT …)` and `… EXCEPT (… INTERSECT …)` are what an ORM emits when it combines an already-combined queryset (`ParseSetOpBranch`).
  Without it the opening paren read as a scalar subquery, so the branch looked like a one-column select list and the chain failed the equal-expression-count check.
  A statement may open with one too — `(SELECT 1) UNION (SELECT 2) ORDER BY 1`, `(SELECT 1)` alone, after a CTE prefix or as an IF body — since the dispatcher routes a leading `(` to the SELECT parser; the parentheses end their query whatever its position (`QueryScope.InParentheses`), so a FROM-less branch inside them no longer reads its `)` as a stray token (probed 2026-09-26).
  So may every position that opens a query with its own parenthesis — a derived table, an `APPLY` body, a CTE, a view or inline function body, a scalar subquery, `IN`, `EXISTS`, a quantified comparison and `INSERT`'s parenthesized source: `((SELECT 1) UNION ALL (SELECT 2))` and `((SELECT 1))` are queries there, while `((SELECT 1) d JOIN …)` groups a join (probed 2026-10-01 against SQL Server 2025).
  `Selection.OpensParenthesizedQuery` tells them apart in one forward pass, so a site that reaches an inner `(` anyway pays a token test for an ordinary grouping; `IN ((SELECT …))` stays a list of one subquery and a scalar `((SELECT 1))` a parenthesized one, each level spending its nesting budget.
  A body keeps out of its stored text only the parentheses wrapping all of it (`CountWrappingParentheses`), and each nested parenthesis `ParseSetOpBranch` reads meets the stack probe expressions do (Msg 8631).
  An `ORDER BY` inside the parentheses — a branch's own, or one after a chain they hold — orders the rows the branch's `TOP` or `OFFSET` takes in a nested query (Msg 1033 without one), but anywhere in a statement's own query or an `INSERT` source it is **Msg 156** on the keyword, `TOP` or not (`QueryScope.ParenthesizedInStatement`; a view or function body takes the nested rule, probed 2026-10-01).
  **Not modeled yet**: in a derived table, real applies a set operation's `ORDER BY … OFFSET / FETCH` to its last branch alone — `(SELECT a FROM v UNION ALL SELECT 9 ORDER BY 1 OFFSET 1 ROWS) d` keeps every row of `v` and drops the `9` — where the simulator limits the combined rows as a statement does (probed 2026-10-01 against SQL Server 2025).
  Top-level ORDER BY binds against the first branch — see [Top-level ORDER BY over a set operation](#top-level-order-by-over-a-set-operation).
- `SELECT *`: bare and qualified `<source>.*`.
  Multi-source `*` keeps duplicate names.
  Unbound `<qualifier>.*` → Msg 4104.
- **Table-value-constructor derived tables** (`Selection.ParseValuesDerivedTable`): `(VALUES (row), (row), …) alias(col, …)` as a FROM source, a JOIN source, or a `CROSS` / `OUTER APPLY` source.
  Rides the same deferred `FromSource.LateralPlan` seam as a derived-table SELECT (`Selection.ForValuesConstructor`), so a VALUES source **under APPLY correlates to the outer row** — the SSMS server-properties shape `… CROSS APPLY (VALUES (1001, 'host_platform', 0, host_platform), …) t(id, [name], internal_value, [value])`, whose rows mix literals with outer-column references.
  Per-column result types unify across every row's cell the way a `UNION ALL` would (`SqlType.PromoteBranches`): an untyped `NULL` cell yields to its typed siblings (an all-`NULL` column is `int`), an integer literal sizes against a decimal one (`(1), (2.5)` → `numeric(2, 1)`), and varchar + N'…' → nvarchar; the unified type coerces each cell at runtime (so `(1),('abc')` promotes to int, then Msg 245 on the `'abc'` row).
  Both the **alias and its column-alias list are required**: no alias → Msg 102 near `)`; no column list → **Msg 8155**; more row columns than list names → **Msg 8158**, fewer → **Msg 8159** (shared factory with CTE / view rename lists); rows of differing arity → **Msg 10709**; empty row `()` → Msg 102.
- **FROM-less `SELECT` with a trailing `ORDER BY`**: `SELECT 2 AS X, 1 AS Y ORDER BY X` is legal (the one synthesized row makes the sort a no-op, but the clause must parse rather than raise Msg 156).
  Also legal as the final `ORDER BY` of a set-op chain whose branches are FROM-less (`SELECT 2 AS X UNION ALL SELECT 1 ORDER BY X DESC` → 2, 1), applied by `ApplyTopLevelOrderBy`.
  The `OFFSET`/`FETCH` tail attaches too.
  The clause reaches the parser through the projection-alias continuation, so `ParseInner`'s pre-expression switch fans `ORDER` (like `FROM` / `INTO`) through to the post-expression ORDER handler.
- Column aliases (`Selection.AssignColumnAlias` / `ReadAliasName`): the identifier forms (`AS x` / bare `x` / bracket `[x]` / alias-on-left `x = expr`) plus their **string-literal** equivalents — `AS 'x'`, bare postfix `expr 'x'` (T-SQL has no implicit string concatenation, so a string literal immediately after a complete select-list expression — including another string literal, `SELECT 'v' 'x'` — is always the alias), and legacy alias-on-left `'x' = expr` (peek-past-`=` disambiguation mirroring the identifier form).
  `N'x'` variants work everywhere a plain `'x'` alias does.
  An **empty** alias — `AS ''` / `N''` / `[]` / bare `''` / `'' = expr` — raises **Msg 1038** (Class 15, **State 4** — distinct from SELECT INTO's missing-column State 5, same wording).
  Double-quoted delimited identifiers work in every alias position (`AS "x"` / bare `"x"` / alias-on-left `"x" = expr`) under the default `QUOTED_IDENTIFIER ON`; under OFF the same token is a string literal instead, so the alias forms stop resolving as names — see [`grammar.md`](grammar.md#double-quoted-identifiers--set-quoted_identifier).
- CASE: searched + simple.
  UNKNOWN excludes (matches WHERE); simple-form `CASE NULL WHEN NULL` falls through.
  Result type from `SqlType.Promote` over the THEN / ELSE branches, **skipping bare untyped `NULL` literals** (`CaseExpression.GetSqlType`, which caches the result on first call): SQL Server treats an untyped NULL as typeless in CASE result resolution — it yields to the typed branches, so `CASE WHEN … THEN 'x' ELSE NULL END` is nvarchar, not int (a bare-NULL placeholder `int` would otherwise promote the whole CASE to int and a string arm then failed to convert — surfaced by SMO's `CASE … THEN (SELECT name … COLLATE catalog_default) … ELSE NULL END`).
  Only when *every* branch is a bare NULL does the placeholder int type stand — and that all-NULL case raises **Msg 8133** anyway.
  A typed NULL (`CAST(NULL AS int)`) is not skipped.
  **Msg 8133** fires at parse when every result expression (every THEN body + the explicit ELSE if present; an absent ELSE counts as implicit bare NULL) is a bare `NULL` literal — `Expression.IsBareNullLiteral` unwraps `Parenthesized` so `(NULL)` still trips.
  A single typed branch (e.g. `CAST(NULL AS int)`) satisfies the rule.
  `IIF` enforces the same check on its two value arms (real SQL Server desugars IIF to CASE).
- `ISNULL` truncates fallback to first arg's type.
  `IIF` = sugar for searched CASE.
  `NULLIF(a, b)` = `CASE WHEN a = b THEN NULL ELSE a END`.
  EF emits `ISNULL` only for `??` with a CAST; bare `??` emits `COALESCE`.
  Neither IIF nor NULLIF is EF-emitted (LINQ ternary → CASE) — load-bearing for `FromSqlInterpolated`.

## Compile-time predicate folding

Real SQL Server settles some predicates while compiling, and an operand it settled is never evaluated — so the error that operand would have raised never appears.
The simulator folds on the same rules (`BooleanExpression`'s `ConstantFoldedPredicate`), plus the ones only a filter position licenses.
Every one is **semantics-preserving**: each drops an operand whose value provably can't change the answer, so none of them commits the simulator to reproducing an optimizer's cost choices.
The same standard extends the rules to the *value*-side shapes a compile-time constant settles the same way — a simple `CASE`'s input, `NULLIF`'s first argument, and the arms a `CASE` / `COALESCE` can't reach.

**A comparison against a NULL constant is UNKNOWN**, whatever the other side holds, so real folds it without looking at that side at all.
`WHERE NULL > a * 2000000000` answers no rows where the expression alone is Msg 8115, `WHERE NULL > a / 0` where it is Msg 8134, and `WHERE 1 / 0 = NULL` folds too — the NULL rule beats even constant evaluation.
It covers all six comparison operators in either operand position, and the shapes that reduce to one: `NULL BETWEEN lo AND hi`, `x BETWEEN NULL AND NULL`, `NULL IN (…)`, `x IN (NULL)` and `x NOT IN (NULL)` (an all-NULL list only — one non-NULL element leaves an equality real evaluates).
`LIKE` takes no such fold in either position, `IS [NOT] NULL` resolves UNKNOWN rather than propagating it, and a quantified `NULL <op> ANY | ALL (SELECT …)` stays exact — real answers an empty subquery `TRUE` for `ALL`, not UNKNOWN.

`x BETWEEN NULL AND NULL` folds because **both** halves of the range it desugars to are UNKNOWN, which the surrounding `NOT` can't change either — so all four spellings (`NOT x BETWEEN …`, `x NOT BETWEEN …`, `NOT x NOT BETWEEN …`) read UNKNOWN, and the fold is context-free the way the absorbing collapse is: a value position reads not-TRUE and `CHECK (x BETWEEN NULL AND NULL)` admits the row.
*One* NULL bound is a different predicate — it leaves `UNKNOWN AND (x <= upper)`, which is FALSE when the surviving half is — so it folds no further than the filter-only never-TRUE rule below, and `HAVING NOT c BETWEEN NULL AND 5` is still Msg 8121 where `HAVING NOT c BETWEEN NULL AND NULL` answers no rows.

The **NULL constant** is the `NULL` keyword read through parentheses, unary minus and a `CAST` / `CONVERT` wrapper, matching what real folds (`CAST(NULL AS int)`, `-CAST(NULL AS int)`, `(NULL)`).
A NULL that *arithmetic* produced is not one: real evaluates `NULL + 1 > <bad>` and `CAST(NULL AS int) + 1 > <bad>`, and reports the other side's error.
`Expression.IsNullConstant` reads the shape syntactically, so `NULLIF(1, 1)` and `ISNULL(NULL, NULL)` — which real does fold, having reduced them to a NULL constant first — stay unfolded here; that direction raises where real answers rows, never the reverse.
(The simple-`CASE` and `NULLIF` rules below read the *evaluated* constant instead, because real's own line falls differently there — see `ConstantFolding.FoldsToNull` — and so does a comparison in a `HAVING`, which is its own section below.)

**An `AND` / `OR` chain carrying an absorbing written constant is that constant**: `x AND FALSE` is FALSE and `x OR TRUE` is TRUE whatever `x` is.
The collapse is position-independent (`1 = 0 AND <bad>` and `<bad> AND 1 = 0` both answer no rows) and context-free — it holds under `NOT`, inside a `CASE WHEN`, and in a CHECK constraint, where `CHECK (1 = 0 AND x / 0 = 1)` rejects the row with Msg 547 rather than raising Msg 8134.
A fold that *raises* leaves its own predicate standing for runtime, matching real: `WHERE 1 / 0 = 1` reports Msg 8134 per row, while `WHERE 1 / 0 = 1 AND 1 = 0` answers no rows because the chain collapsed first.
Only a **written** constant absorbs.
A predicate real evaluates once per execution rather than folding — `@v = 1`, `GETDATE() < '1900-01-01'`, `RAND() < 0` — is real's *startup filter*, which suppresses the runtime error but not the binding checks; the simulator doesn't model it, and the divergence only shows when such a predicate is written after the operand that raises.

The **range's implicit `AND` absorbs the same way**: `value BETWEEN lower AND upper` is `value >= lower AND value <= upper`, so a half that folds to a written-constant FALSE settles the range whatever the other half would have done.
`WHERE 84 BETWEEN <bad> AND 61` answers no rows (the `84 <= 61` half is FALSE), the `NOT` spelling answers every row, `CHECK (84 BETWEEN x / 0 AND 61)` rejects with Msg 547 rather than raising, and `HAVING 84 BETWEEN b AND 61` reports nothing for the ungrouped `b` — all probe-confirmed.
It takes a *constant* half: `WHERE 84 BETWEEN <bad> AND 99` still raises, and so does `WHERE col BETWEEN col / 0 AND 1`, where real's own answer (no rows) comes from a plan-dependent evaluation order rather than a fold.

**A simple `CASE` whose comparisons no arm can win runs its ELSE alone**, dropping the input and the compare values with the comparisons they fed.
Real settles it two ways, both probed: the input folds to NULL (`CASE CAST(NULL AS int) WHEN <bad> THEN …`, and `CASE NULLIF(1, 1) WHEN …` / `CASE CAST(NULL AS int) / 17 WHEN …` alike, since real folds the input first), or every compare value does (`CASE <bad> WHEN CAST(NULL AS int) THEN … WHEN NULL THEN …`, where the input itself then never runs).
`NULLIF(a, b)` is the `CASE WHEN a = b THEN NULL ELSE a END` it desugars to and takes the first of those: a constant-NULL `a` leaves `b` unevaluated.
Both read `ConstantFolding.FoldsToNull`, a **wider** rule than `Expression.IsNullConstant` on purpose — the comparison fold stays syntactic because real's does, and a folded-NULL operand there still raises the other side's error (`WHERE CAST(NULL AS int) / 17 > <overflowing expression>` is Msg 8115 on real, in both operand positions).
The fences are compile-time-ness and completeness: one non-NULL compare value beside the NULL one leaves the input standing (`CASE <bad> WHEN CAST(NULL AS int) THEN 1 WHEN 5 THEN 2 …` raises), a NULL the *row* supplies doesn't fold at all (`CASE nullcol WHEN <bad> THEN …` raises on real too), and `NULLIF(NULL, <bad>)` is left alone because real refuses that spelling outright with Msg 4151.

**A filter keeps only the rows a predicate answers TRUE for**, so a predicate real can see is never TRUE settles WHERE / HAVING / ON / a positioned DML predicate without the rest of it running (`BooleanExpression.SimplifyForFilter`).
That is what makes `WHERE NULL > a AND <bad>` and `WHERE <bad> BETWEEN NULL AND 5` answer no rows.
Unlike the absorbing collapse this is **not** context-free — a constant-UNKNOWN operand is FALSE-or-UNKNOWN rather than a definite value, and both `NOT` and a CHECK constraint distinguish those (real answers `T` for `2 NOT BETWEEN NULL AND 1`, and rejects `x = 10` from `CHECK (x BETWEEN NULL AND 5)`).
So it is applied at the filter sites only, never inside the predicate's own tree.

A filter that is *itself* a written constant folds here rather than in the tree, for the same reason: `WHERE 1 = 0`, `HAVING NULL IS NOT NULL`, `HAVING NOT NULL = NULL`, `HAVING 1 > 2` and `HAVING NOT 1 = 1` carry no chain for the absorbing collapse to work on, and the UNKNOWN ones are only actionable because a filter wants TRUE.
A fold that raises still leaves the predicate standing (`HAVING 1 / 0 = 1` reports Msg 8134).

Three-valued `NOT` is an involution, so the rule reads through a negation off the **mirror** property: `NOT p` is never TRUE exactly when `p` is never FALSE (`BooleanExpression.IsNeverFalse`).
Two shapes answer it, each the negation of one that already answered `IsNeverTrue`:

- a **negated range with a NULL-constant bound** is TRUE or UNKNOWN, so `WHERE NOT x NOT BETWEEN NULL AND <bad>` answers no rows (both bound positions) while the single negation `WHERE x NOT BETWEEN NULL AND <bad>` still raises;
- an **`IN` list carrying a NULL constant** is TRUE or UNKNOWN, so `WHERE x NOT IN (<bad>, 1, NULL)` and `WHERE NOT x IN (<bad>, 1, NULL)` answer no rows while the un-negated `WHERE x IN (<bad>, 1, NULL)` raises.

`AND` and `OR` propagate both properties (an `AND` is never FALSE only when every operand is; an `OR` is never TRUE only when every operand is), which is what settles `WHERE <bad> = 1 AND NOT (NULL) BETWEEN b AND 5` and `WHERE (x BETWEEN NULL AND 5) OR (x BETWEEN NULL AND <bad>)`.
Being filter-only, none of it moves a sibling out of the containment pass: `HAVING b NOT IN (1, NULL)` is still Msg 8121, and `CHECK (x NOT IN (1, NULL))` still rejects `x = 1` with Msg 547 — the never-TRUE reading would have admitted it.

An **`IN` list carrying its own left operand** answers the same pair, because `x IN (…, x, …)` *is* `x IS NOT NULL`: a non-NULL `x` matches itself and a NULL one leaves every comparison UNKNOWN.
So `WHERE x NOT IN (x / 0, x)` and the `NOT x IN (…)` spelling answer no rows in either element order, matching real.
The un-negated form is left alone deliberately — see the plan-dependent list below.
Only a whole column reference read through parentheses matches, which is the only spelling that provably names the same column.

### A never-TRUE HAVING empties the whole statement

A `HAVING` real can see is never TRUE keeps no group, so the statement answers nothing whatever the rest of it would have done — and real then runs **none** of it.
`SELECT a FROM t WHERE a / 0 IS NOT NULL GROUP BY a HAVING NULL IS NOT NULL` answers no rows there rather than Msg 8134, and so do the `1 = 0` / `1 > 2` / `NOT 1 = 1` / `NULL = NULL` / `NOT NULL = NULL` spellings, the shapes the never-TRUE rule already settled (`HAVING MAX(a) BETWEEN NULL AND 5`, `HAVING NOT NULL IN (a)`), and the ungrouped `SELECT MAX(a / 0) FROM t HAVING 1 = 0`.
`Selection`'s row-source closure returns an empty sequence for those, which skips the scan, the WHERE, the aggregate pass, the select list and the ORDER BY together.

The emptiness is settled *after* binding, not instead of it — the whole binding battery already ran to build the plan, so real's own errors still report: **Msg 207** for `WHERE zzz > 1 … HAVING 1 = 0`, **Msg 8120** for `SELECT b FROM t GROUP BY a HAVING 1 = 0`, **Msg 8121** for `WHERE 1 = 0 GROUP BY a HAVING b > 1` (all probe-confirmed).
A `HAVING` that can be TRUE settles nothing, so `WHERE a / 0 > 1 … HAVING MAX(a) > 1` still raises.

### A HAVING reads a folded NULL where a WHERE doesn't

In a `HAVING`, a comparison whose operand real *evaluates* to NULL while compiling is settled UNKNOWN — the arithmetic NULL the comparison rule above refuses.
`HAVING CAST(NULL AS int) / 17 = b` and `HAVING b NOT IN (CAST(NULL AS int) / 44 - 73)` report nothing for an ungrouped `b` and answer no rows over a bad WHERE, and so do the `NULL + 1` spelling, either operand position, and the `BETWEEN` form with both bounds folding.
`BooleanExpression.SettleFoldedNullComparisons` rewrites the parsed HAVING per comparison, which is the grain real works at: `HAVING <folded> AND b > 1` still reports Msg 8121 for the surviving conjunct.

The same comparison in a **`WHERE`** stays exact, because real's reading there is a *plan* rather than a rule: `WHERE CAST(NULL AS int) / 17 > <overflowing expression>` raises Msg 8115, and adding `DISTINCT`, a `GROUP BY`, a `TOP 2` or a join to that one statement flips it to no rows (all probe-confirmed, and `ORDER BY` / `MAX(…)` / `COUNT(*)` leave it raising).
A `HAVING` carries a grouping by construction, so its reading is the stable one and it is the only site the rewrite runs at.

### An arm real can't reach drops its aggregates

Real picks a `CASE`'s arm while compiling whenever it can settle the conditions there, and everything the arms it dropped carried goes with them — **including an aggregate**, which the aggregate pass would otherwise evaluate per row whether or not the arm holding it can be reached.
`SELECT CASE 23 WHEN -38 THEN COUNT(7 / 0) ELSE 2 END` answers 2 there, and so does the same shape over a column argument.
`CaseExpression.DecideArms` settles it: an arm is unreachable when its own condition folds to something other than TRUE, and once any arm's folds to TRUE every later arm and the ELSE go with it, whatever the arms before it did.
`COALESCE` settles the same way off its first non-NULL constant argument — `SELECT COALESCE(61, SUM(7 / 0))` answers 61 — and both mark rather than remove: the aggregate stays *registered*, because real keeps the statement a vector aggregate and keeps reporting **Msg 8120** for an ungrouped column beside it even when the arm holding its only aggregate is the one it dropped (`SELECT col, CASE WHEN 1 = 0 THEN SUM(other) ELSE 2 END FROM t`, probe-confirmed both ways).

The fence is compile-time-ness: an arm real decides *per row* keeps its aggregates, so `CASE WHEN col = 1 THEN SUM(7 / 0) ELSE 2 END` raises there as it does here, as do a reachable arm (`CASE 23 WHEN 23 THEN SUM(7 / 0) …`) and a `COALESCE` whose leading argument isn't a constant value.

The settled arm also decides the whole expression's **constant-ness**, which is what real's ORDER BY gates read: `ORDER BY CASE 1 WHEN 1 THEN 5 ELSE col END` and `ORDER BY COALESCE(61, col)` are Msg 408 while `ORDER BY CASE 1 WHEN 1 THEN col ELSE 5 END` and `ORDER BY COALESCE(col, 61)` sort, and the `OVER (ORDER BY …)` path agrees with Msg 5308 (all probe-confirmed).
A *folded predicate* is a settled condition too, so `ORDER BY CASE WHEN NULL > a THEN 1 ELSE 2 END` and `ORDER BY CASE WHEN 1 = 0 AND a > 1 THEN 1 ELSE 2 END` reach Msg 408 as they do on real.

### COUNT of an expression real types NOT NULL

Real reduces `COUNT(<expression it types NOT NULL>)` to `COUNT(*)` and never evaluates the argument, so `SELECT COUNT(61 / 0)` and `SELECT COUNT(2000000000 * 3)` answer a count there.
`Selection.ReduceConstantCounts` applies it behind two fences.
The argument has to be a computation over non-NULL literals (`Expression.IsNonNullConstantComputation` — literals, parentheses, unary minus and the binary operators), which is the nullability real's own reduction reads: narrower than the folded *value*, since a fold that raises has no value, and narrower than the projection metadata's, where arithmetic claims nullable even over two literals.
And the query must carry no **grouping expression**: real evaluates the argument once a `GROUP BY` names one (`SELECT COUNT(61 / 0) FROM t GROUP BY a` is Msg 8134 there, while the same statement without the GROUP BY — and with `GROUP BY ()` — answers).
`COUNT(DISTINCT 61 / 0)`, `SUM(61 / 0)`, `MAX(61 / 0)` and `COUNT(<column> / 0)` all raise on both.

### Where the fold sits among the binding checks

Real folds **after name resolution and before the GROUP BY containment pass**, and both halves are observable.

- An unknown column inside a dropped operand still reports **Msg 207** — `WHERE NULL > zzz` and `WHERE 1 = 0 AND zzz > 1` both raise it.
  So a folded node still binds its operands (`ConstantFoldedPredicate.Bind` forwards).
- An ungrouped column inside a dropped operand reports **nothing**: `HAVING NULL <> b`, `HAVING 1 = 0 AND b > 1` and `HAVING NOT (1 = 0 AND b > 1)` all answer no rows over an ungrouped `b`, where `HAVING b > 1` alone is Msg 8121.
  The containment pass therefore walks `VisitSurvivingOperandExpressions` — identical to the written operand walk everywhere except a folded node, so inline-CHECK validation, view updatability and read-column recording keep reading the predicate as written.

The filter-only rule doesn't take its siblings out of the tree, which is exactly the line real draws: `HAVING NULL <> b AND b > 1` **does** report Msg 8121 for the surviving conjunct, and so does `HAVING b > 1 AND NULL > 1`, while the absorbing `1 = 0` beside the same `b > 1` reports nothing.
A folded WHERE doesn't excuse the HAVING either (`WHERE NULL > <bad> GROUP BY a HAVING b > 1` is Msg 8121).
Structural parse-phase rules run ahead of everything and are untouched — an aggregate in a WHERE is Msg 147 even under `1 = 0 AND`.

The folded node keeps its **equality** shape readable to the seek planners, so `WHERE <catalog column> = NULL` still seeks empty instead of materializing the view; the range and equality-family shapes stay hidden, since their operands are arbitrary expressions a planner would evaluate for a row-independent bound — which is what folding took off the table.

### Not folded yet

- **Real's own evaluation ordering**, which is a per-row short-circuit over a conjunct order the plan chose.
  `WHERE a / 0 = 1 AND a = 999` answers no rows on real (it evaluates the cheap comparison first and never reaches the division) while the simulator raises Msg 8134.
  The choice is a *cost* one and it reverses with the data rather than with anything a compile-time rule could settle: `WHERE nullable_col IS NULL AND <bad> BETWEEN …` answers no rows over rows where the cheap conjunct is FALSE and raises as soon as one row satisfies it (probe-confirmed both ways over the same statement).
  The same freedom moves the two *operands* of one comparison and the two halves of one range: `WHERE <overflowing expression> <= 18 / CAST(NULL AS int)` raises on real, while `DISTINCT`, a `GROUP BY`, a `TOP 2` or a join on that one statement each flip it to no rows — real's arithmetic-NULL comparison fold reaches a WHERE only under the plan that isn't trivial, which is why the simulator applies it in a HAVING alone (above).
  An **empty constant interval** is the same story: `WHERE <bad> BETWEEN 41 AND 5` raises Msg 8134 on real, `SELECT DISTINCT` of the same statement answers no rows, and `HAVING b BETWEEN 41 AND 5` still reports Msg 8121 for the ungrouped `b`.
  These are cost choices, not semantic ones, and they reverse per plan.
- **`NULL <op> ANY | ALL (SELECT …)`.** Real runs the subquery for row existence but drops its projection, so `NULL <> ALL (SELECT a * 2000000000 FROM t)` answers no rows where the simulator raises Msg 8115.
  Folding it isn't available — the answer over an *empty* subquery is TRUE, not UNKNOWN — so this needs an existence-only execution path.
- **An un-negated `IN` list carrying its own left operand.** `x IN (…, x, …)` is `x IS NOT NULL` and could be folded, but real doesn't settle it consistently: `WHERE x IN (x / 0, x)` answers rows there while moving the same self element to the front — `WHERE x IN (x, x / 0)` — raises Msg 8134.
  Two written orders of one semantic list, two answers, so folding it would answer where real raises.
  The simulator keeps its own left-to-right evaluation; the negated spelling, whose two orders *do* agree on real, is settled by the never-TRUE rule above.
- **An `IN` list evaluates every element on real**, even after an earlier one matched: `WHERE a IN (2, 3, 0, a / 0)` is Msg 8134 there and answers rows here.
  The simulator keeps its left-to-right short-circuit, which costs an error real raises but avoids evaluating a long literal list per row.
  (A list carrying a NULL constant under a negation is settled by the never-TRUE rule above and never reaches this.)
- **Real's runtime want-TRUE cutoff in a `CASE WHEN` / `IIF` condition.** There real evaluates left to right and stops as soon as the answer can't be TRUE, which is order-sensitive rather than a fold: `CASE WHEN 5 BETWEEN NULL AND 1 / 0 …` answers `F` while `CASE WHEN 5 BETWEEN 1 / 0 AND NULL …` raises.
  Applying the compile-time never-TRUE rule at those sites would answer for the second shape too — the over-permissive direction — so the condition sites keep evaluating as written.

## Derived-table column-alias list

A derived table's **alias is mandatory** — `SELECT * FROM (SELECT 1 x)` raises **Msg 102** (`Incorrect syntax near ')'.`), matching real (probe-confirmed 2026-07-31).
The column-alias list stays optional; without one every projected column must already have a name (Msg 8155).


`FROM (SELECT …) s(a, b)` renames every output column of the derived table, overriding whatever the inner projection called them.
The same list applies to an `APPLY`'s derived table (`CROSS APPLY (SELECT t.a) x(v)`), and the `(VALUES …) v(a, b)` and `WITH c(m, n) AS (…)` forms route to the same `ParseColumnAliasList` by their own paths.

`Selection.ResolveDerivedTableColumnNames` is the shared gate. Probe-confirmed rules:

| Shape | Result |
| --- | --- |
| List shorter than the projection | **Msg 8158** — `'s' has more columns than were specified in the column list.` |
| List longer than the projection | **Msg 8159** — `'s' has fewer columns…` |
| A name repeated in the list | **Msg 8156** — `The column 'a' was specified multiple times for 's'.` |
| Empty list `s()` | **Msg 102** near `')'` |
| No list, and a column has no name of its own | **Msg 8155**, one error per unnamed column |

The Msg 8155 case reports the whole run rather than stopping at the first, so `(SELECT 1, 2) s` raises a single exception carrying two errors — `SimulatedSqlException.NoColumnNamesSpecified` builds it, and `Errors` / `Message` match real's.
Before this shipped the simulator returned empty-named columns there, and resolved them to the wrong values.

The 8155 check needs the alias for its message, so it only runs when the source has one.
An **unaliased** derived table is still accepted, where real requires the alias — a separate over-permissive gap.

## Pagination (`OFFSET ... FETCH`)
- OFFSET requires ORDER BY (else Msg 102).
- FETCH alone (no preceding OFFSET) → **Msg 153**.
- Negative offset → **Msg 10742** (`"...a OFFSET clause may not be negative."` — verbatim "a OFFSET").
- Fetch ≤ 0 → **Msg 10744** (verbatim typo "greater then zero").
- TOP + OFFSET → **Msg 10741**.
- Counts resolve at parse time (constants, parameters, arithmetic).

### The rows an `OFFSET` skips

Real evaluates the select list above its Top, so a row the `OFFSET` skips is never projected: `1 / (id - 5)` over a page past `id` 5 returns the page, while the same expression on a row the page returns is Msg 8134 (probed 2026-09-30 against SQL Server 2025).
The streaming projection models it — the WHERE still judges each skipped row, the select list doesn't run — and so does the index-ordered scan behind it, which does less still.

When an index supplies the `ORDER BY` order (see [`indexes.md`](indexes.md#order-by-elimination)) and no residual WHERE stands between the scan and the page, every row the scan yields is a result row, so the scan passes the `OFFSET`'s rows over where it walks them — locked and counted in `STATISTICS IO` like any row it reads, but never fetched, decoded or projected — and stops once the `FETCH` is full, which is real's plan: a Top over the ordered index scan reading offset + fetch rows.
Measured on a 150k-row table paged at offset 140k, fetch 100, `ORDER BY` the primary key: **53 ms → 5 ms** (real ~6 ms), with a residual WHERE 62 ms → 18 ms (real ~7 ms), and the first page 11.6 ms → 0.04 ms, since the key order it walks is kept between writes rather than rebuilt per query.
A sort no index serves ranks the rows by their keys and projects only the page — see [Top-N selection](#top-n-selection).

## `TOP n [PERCENT] [WITH TIES]`
- `TOP n` — the plain integer row cap; streams when no ORDER BY / DISTINCT, else read off the [Top-N selection](#top-n-selection).
- `TOP n PERCENT` — the cap is `ceil(rowcount × n / 100)` (probe-confirmed against SQL Server 2025).
  The percent value must resolve to a numeric in `[0, 100]` — outside → **Msg 1031**, NULL → **Msg 1014**.
  PERCENT buffers every row (the total rowcount must be known).
- `TOP n WITH TIES` — after the cap, additionally emits every following row whose ORDER BY key equals the boundary row's.
  Requires an ORDER BY (else **Msg 1062**).
- Both flags ride the existing TOP parse (`topPercent` / `topWithTies` on the projection build).

### The operand's type, and where it is checked

The row count is resolved per execution — the operand may carry a parameter or a variable, which is exactly what EF's `Skip` / `Take` emit — and validated once at parse for the immediate rejection real gives a bad literal: real accepts an integer other than `bit`, and an exact numeric at **scale 0** only when real names it `numeric` — a literal (`2.`), a `CAST … AS numeric(5, 0)` or arithmetic over one — while the same value typed `decimal(5, 0)`, a variable declared either way, `bit`, a fractional scale, another family and NULL are **Msg 1060** (an `OFFSET` takes its own Msg 10743 for all of them), and a negative one Msg 127 (probed 2026-10-01 against SQL Server 2025).
The legacy unparenthesized count takes a constant only: `TOP @n` is Msg 102 near the variable.

A `TOP` count may read an enclosing query's columns — a correlated subquery's or an `APPLY` body's `TOP (t.g)` — and then counts per outer row, Msg 1014 for a NULL and 127 for a negative as the row reaches it; a column of the query's own source is Msg 4115 (probed 2026-10-01).

A **module body** binds without running, so an operand naming a parameter has no value to read.
Real settles that one from the operand's *declared type* instead, which is why `SELECT TOP (@rows)` over an `int` parameter creates while the `nvarchar` and `decimal(5, 2)` spellings are refused at CREATE — WideWorldImporters' five `Website.SearchFor*` procedures are the shape that turns on it.
A **written constant** keeps the ordinary value check even at bind time, since real refuses `TOP (NULL)` and `TOP (1.5)` at CREATE too (probe-confirmed).
`OFFSET` / `FETCH` take the same operands and the same split.

### Top-N selection

An ORDER BY no index serves ranks the rows the WHERE keeps by their **keys alone**, and projects only the rows its `TOP` / `OFFSET` / `FETCH` window returns (`ProjectSorted` in `Selection.Execution.TopRows.cs`).
That is real's plan shape — its select list is a Compute Scalar above the Sort and Top — and it is observable: a select-list expression that raises on a row outside the window never runs, while one naming a sort key by alias or ordinal runs for every row, as real's sort key must (probed 2026-09-30 against SQL Server 2025, `1 / (id - 5)` in each position).
A key naming a select-list column hands its value to the projection rather than evaluating twice, so a `NEWID()` the rows sort by is the one they return.
The one exception is a select list drawing `NEXT VALUE FOR` (legal under its own `OVER`), which projects every row as it is read: a sequence draws once per row, deduplicated by the row stamp the read sets.

The ranking lives in `TopRows<T>` (`Selection.Execution.TopRows.cs`), shared with a grouped query's `TOP` and the bounded per-partition `ROW_NUMBER()`:
- **A bound known before the scan within 4096 rows** (`TOP`, or `OFFSET` + `FETCH`, resolved per execution, so EF's parameterized `Skip` / `Take` qualifies) is a bounded max-heap: once full, a candidate is rejected on one compare against the root and never copied, and a reused scratch key tuple means a rejected row allocates nothing.
  Under `WITH TIES` the heap also keeps the rejected candidates tying its root; the root only improves, so that list is either still the boundary's or wholly outside it.
- **Anything wider, `PERCENT`, or no cap** buffers every row as its tuple and keys and selects the window at the end: Hoare selection places the window's last rank, then its first, and only the window is sorted — about three compares a row for a deep page, where the full sort paid seventeen.

**The order is total**: the ORDER BY keys (with a `Pref` collation's uppercase preference once every key ties), then arrival.
So every strategy returns the full sort's rows in the full sort's order, ties at the boundary included, and ties everywhere keep arrival order — which is what [row order](#row-order-without-order-by) documents for a sort's ties, and what the old `List<T>.Sort` introsort only gave below its insertion-sort threshold.
Real leaves a tie's order to the plan: its full sort and its Top N Sort return one tie group in different orders for the same data (probed 2026-09-30), so no fixed rule matches it and arrival costs nothing.
The `DISTINCT` and windowed paths project first (deduplication and the windows read the projection) and then rank through the same selection.

Measured 2026-09-30 on 150k rows (`id int` primary key, sort columns unindexed; offsets 0 / 1k / 140k, fetch 100; `WHERE id % 3 <> 0` keeps 100k, paged at 90k), median ms, the reference server at DOP 8:

| shape | before | after | real |
|---|---|---|---|
| `int`, offset 0 / 1k / 140k | 73 / 159 / 132 | **25 / 30 / 86** | 9 / 6 / 34 |
| `nvarchar(40)`, offset 0 / 1k / 140k | 53 / 602 / 605 | **23 / 33 / 91** | 10 / 12 / 49 |
| `varchar(40)`, offset 0 / 1k / 140k | 54 / 439 / 450 | **22 / 29 / 79** | 10 / 9 / 54 |
| `g, s DESC`, offset 0 / 1k / 140k | 45 / 452 / 436 | **20 / 27 / 90** | 9 / 9 / 58 |
| `nvarchar` + WHERE, offset 0 / 1k / 90k | 56 / 411 / 415 | **33 / 40 / 87** | 10 / 10 / 34 |
| `TOP (100) WITH TIES` over a 50-valued key | 162 | **13** | 10 |
| `TOP (1) PERCENT` by `nvarchar` | 568 | **77** | 193 |
| EF `OFFSET @p ROWS FETCH NEXT @q` (5000, 100) | 600 | **88** | 14 |
| full sort by `nvarchar`, all 150k rows | 619 | **356** | 90 |

Allocation fell from 84–119 MB to 17–30 MB for a heap-served page and 37–63 MB for a buffered one.
A bare `SELECT COUNT(*), MAX(s)` over the same table is 24 ms here and 14 ms on real, so a heap-served page sits a few milliseconds above the scan it has to do anyway.
A deep page's median carries garbage-collection noise its minimum doesn't (`int` at 140k: 86 median, 39 minimum), since every row's tuple and keys survive to the selection.

**The comparer was most of a sort's cost.**
Profiled, the full sort by `nvarchar` spent 72% of its time inside `SQL_Latin1_General_CP1_CI_AS`'s compare, which read three frozen dictionaries per character; flat tables for the low code points (the `WeightTable` array in `Collation.Latin1GeneralSort.cs`, which covers U+0000–U+024F) cut that sort from 650 ms to 355 ms, and every comparison under the default collation gains the same.
The residual against real is its parallelism: the 150k-row sort is a single thread here against eight there.

## `SET ROWCOUNT n`

The session-wide sibling of `TOP`: a cap on how many rows a statement returns or changes, lifted by `SET ROWCOUNT 0` (the default).
Session state on `SimulatedDbConnection.RowCountLimit`, `bigint`-wide because real accepts a `bigint` variable there even though the literal form is int-bounded.
Probed against SQL Server 2025 (2026-08-06).

- **The two caps compose as a minimum**, in both directions: `TOP 5` under `ROWCOUNT 3` returns 3, `TOP 3` under `ROWCOUNT 5` returns 3.
- **The cap is on what the statement emits, not what it reads**: `SELECT COUNT(*)` under `ROWCOUNT 3` still answers over the whole table and reports one row.
- `@@ROWCOUNT` reports the capped count.
- Every row-emitting shape takes it — a plain `SELECT`, an `OFFSET … FETCH`, a `SELECT … INTO`, a `SELECT @v = …` assignment (which then keeps the last row inside the cap), and `INSERT` / `UPDATE` / `DELETE` / `MERGE`.
- A `MERGE` counts the **actions** it takes, in the order it takes them: a source row every `WHEN` clause declines costs nothing, and a `NOT MATCHED BY SOURCE` delete counts like any other action (probed 2026-09-25).
  Which rows fill the cap follows each engine's processing order — real's comes from its join plan (key order over a primary key), the simulator's is target order and then the unmatched source rows.
- `NEXT VALUE FOR` under an active cap is **Msg 11739**, the message real writes naming all three sources ("if ROWCOUNT option has been set, or the query contains TOP or OFFSET").

Enforcement is three seams, not sprinkled checks: `SimulatedSqlResultSet.WithRowCountLimit` wraps the statement's own row sequence lazily at the SELECT / `SELECT … INTO` dispatch, `ApplyDmlTopCap` — the list every `INSERT` / `UPDATE` / `DELETE` already collects its rows through — folds the session cap in beside `TOP`, and `MERGE` stops queuing actions once its pending lists hold the cap.
Inner plans (a join's sources, a subquery, a view body) are untouched, matching real.

**Scoping.** The cap persists into a called procedure, while a body's own `SET ROWCOUNT` reverts when it returns — dynamic SQL the same — through `SimulatedDbConnection.SessionOptionScope`, shared with `XACT_ABORT` and `DATEFIRST`.

**Argument errors.** A negative *literal* never reaches the option's own validation because the grammar has no sign slot, so real reports **Msg 102** near the `-`; a literal that isn't an int is **Msg 1080** echoing it (`The integer value 2.5 is out of range.`); a NULL or negative *variable* is **Msg 507** class 16 state 2 (`Invalid argument for SET ROWCOUNT. Must be a non-null non-negative integer.`).

**Divergence.** Real counts a `MERGE`'s *actions* against the cap; the simulator caps the materialized source rows, which agrees whenever each source row draws an action and otherwise lets a declining row consume a slot.

## `WINDOW w AS (…)` named-window clause (SQL Server 2022+)
- A trailing `WINDOW name AS (<over-body>) [, …]` clause (between HAVING and ORDER BY) defines named windows an `OVER w` reference resolves to.
- Every window kind reaches one — the ranking family (`ROW_NUMBER` / `RANK` / `DENSE_RANK` / `NTILE`), the distribution pair (`CUME_DIST` / `PERCENT_RANK`), the offset pair (`LAG` / `LEAD`), the value pair (`FIRST_VALUE` / `LAST_VALUE`), the ordered-set pair (`PERCENTILE_CONT` / `PERCENTILE_DISC`) and aggregate-OVER.
- The reference registers carrying only what it wrote inline (the definition follows the projection) and is patched once the WINDOW clause is read; an undefined name → **Msg 5362**, whose state says which miss it was — 3 when the query block has no WINDOW clause, 4 when an `OVER` names none of its windows, 7 when a definition names one (probed 2026-09-29 against SQL Server 2025).
- Window names are identifiers: they resolve under the database collation, so `OVER W` finds `WINDOW w AS (…)` under a case-insensitive one.
  One clause defining the same name twice → **Msg 16211**.
- References resolve from the statement's ORDER BY as well as its select list.
- WINDOW is **contextual** (still a valid identifier / table alias) — recognized as the clause only in the `WINDOW <name> AS (` shape via lookahead, which every alias position checks: a table's, a rowset function's (`generate_series`, `OPENJSON`, `STRING_SPLIT`, `CHANGETABLE`), and a select-list element's, where `SELECT 1 WINDOW w AS (…)` is an unnamed column and a clause.
- The clause needs no FROM ahead of it, and the ORDER BY after it reads its windows (probed 2026-09-29 against SQL Server 2025).
- Each query block owns its named windows (`ParserContext.NamedWindowScope`): a subquery's clause neither answers its enclosing block's references nor collides with its names, whatever the source — a table, `VALUES`, a derived table, a CTE, a view, a TVF or a rowset function.
- A windowed function in a subquery's select list is legal wherever the subquery sits, a WHERE included; Msg 4108 is only for the block's own WHERE / GROUP BY / HAVING.

### Refinement (`OVER (w …)`) and definition chaining

A reference may add the elements the window it names doesn't already carry, and a *definition* may refine another the same way (`WINDOW w AS (PARTITION BY g), w2 AS (w ORDER BY id)`) in either written order.
The reference must lead the body — `OVER (PARTITION BY g w)` is Msg 102.

- Each of PARTITION BY / ORDER BY / frame may be supplied by exactly one side; an overlap → **Msg 4123** ("Window element in OVER clause can not also be specified in WINDOW clause.").
  Real's state tracks the *referenced* window rather than the conflicting element — State 2 when that window carries a frame, State 3 when it doesn't (probe-confirmed).
- In the OVER position at least one refining element is required: `OVER (w)` is **Msg 102**, even though the bare `WINDOW w2 AS (w)` definition form is legal.
- A definition may carry a frame with no ORDER BY of its own — the reference that resolves it can supply the ordering — so the frame-needs-ordering gate waits until the merge.
- Definitions referencing each other in a loop → **Msg 5365**.
  A definition naming *itself* is not that error: real doesn't put a name in its own scope, so `WINDOW w AS (w ORDER BY id)` is **Msg 5362**.

### Per-kind rules through a named window

Real answers this position with its own error numbers rather than the inline ones, and the wording differs even where the number's inline twin shares it.

| Condition | Through a named window | Inline `OVER (…)` |
| --- | --- | --- |
| Frame on a kind that rejects one | **Msg 4106** (State 2 ranking / distribution, State 1 for `lag` / `lead` / the percentile pair) | Msg 10752 |
| Missing ORDER BY on a kind that requires one | **Msg 5366** ("must have an OVER clause or a WINDOW with ORDER BY"; State 3 ranking / distribution, State 2 offset / value) | Msg 4112 |
| ORDER BY on `PERCENTILE_CONT` / `PERCENTILE_DISC` | **Msg 5363** ("may not have ORDER BY in OVER or WINDOW clause") | Msg 10758 |
| Frame with nothing to order against | **Msg 5364** | Msg 10756 |

A frame written in the *refinement* rather than inherited stays on the inline Msg 10752 path, and the refinement's ORDER BY reaches the same Msg 5308 / 5309 constant gate an inline one does.
`PERCENTILE_CONT` / `PERCENTILE_DISC` take only PARTITION BY from the definition — their ordering always comes from WITHIN GROUP.

`NEXT VALUE FOR seq OVER w` takes a named window too; like the inline body, the ordering it names is discarded, so values follow the row order (probed 2026-09-24).

## `TABLESAMPLE`
- `TABLESAMPLE [SYSTEM] (n PERCENT | n ROWS) [REPEATABLE (seed)]` on a FROM source parses and is **discarded** — the query returns every row.
- SQL Server's sample is nondeterministic (any subset is a valid wire result), so returning all rows is a documented deterministic approximation; the win is accepting the syntax.

## ORDER BY term resolution

**Qualifier-awareness is the recurring rule across every resolver here.** A name matched on its leaf alone binds to the wrong column whenever a join brings a same-named one into scope — silently, with no error. It applies in four places, all now qualifier-aware: the plain ORDER BY resolver, the grouped ORDER BY resolver, the grouped-key resolver behind a grouped *projection* (`SELECT p.name` binding to a `b.name` grouping key), and the DISTINCT select-list check.

An **unqualified** term matches the select list first (output alias, then ordinal), falling back to a source column when it matches no output — SQL Server permits ordering by a non-selected source column.
Only a **bare** term (parentheses aside) may name an output alias.
A name inside any larger expression — `x + 1`, `-x`, `x COLLATE …`, a CASE — binds to the FROM sources alone, so it is **Msg 207** while compiling when only an alias carries it, followed by Msg 145 under DISTINCT, and it reads the source column when both do: `SELECT b AS a FROM t ORDER BY a + 0` sorts by `t.a` (probed 2026-09-24).
The same holds for a grouped query's `ORDER BY s + 1` over `SUM(b) AS s`.
A **qualified** term (`alias.col`) is a *source-column reference* and never matches an output alias: real orders `SELECT val AS id FROM ob t ORDER BY t.id` by `t`'s id column even though an output alias `id` exists (probe-confirmed).
That holds while compiling as well as per row, so an unknown qualifier is Msg 4104 over an empty table too, and in a derived table or subquery whose `ORDER BY` has no `TOP` / `OFFSET` the terms don't bind at all, real's Msg 1033 outranking them (probed 2026-09-26 against SQL Server 2025).
Matching on the leaf alone silently sorted by the wrong column whenever a join brought a same-named column into scope — `ORDER BY child.id` bound to the projected `parent.id`, which is the shape an ORM emits when ordering by a related model's field.

`DISTINCT` keeps its own rule: the term must appear in the select list, and a miss is Msg 145 rather than a source fallback.
An expression term appears there only when it matches a select item **by shape** — the structural match a grouping expression makes, columns keyed by the source column they resolve to and parentheses transparent — and sorts by that item: `ORDER BY id + 1` over `SELECT DISTINCT p.id + 1`, `ORDER BY COUNT(*)` over a projected `COUNT(*)` (probed 2026-09-30 against SQL Server 2025).
Reading projected columns alone is not enough: `ORDER BY n + 0`, `ORDER BY 1 + id` over `id + 1`, `n + 1.0` over `n + 1`, a `CASE` or `COLLATE` over a projected column and a subquery written in both places are all Msg 145, raised while compiling.
The term may name the **source column behind a projected one** rather than its output alias (`SELECT DISTINCT c.name AS Col5 … ORDER BY c.name`), which is the only spelling left when an ORM aliases every output positionally.
Under DISTINCT the qualified form follows the same source-reference rule as the non-DISTINCT path: it must name a source column that is itself projected, and a miss is Msg 145 rather than a leaf match against the output aliases (tightened 2026-07-31 — `SELECT DISTINCT val AS id … ORDER BY t.id` is an error, while `ORDER BY c.nm` over `SELECT DISTINCT nm AS Col5` is legal).

### Constant terms (Msg 408) and bare variables (Msg 1008)

A term the server folds to a constant is rejected with **Msg 408** `A constant expression was encountered in the ORDER BY list, position N.` — position being the term's 1-based index in the list, and the rule applying to a single SELECT, a set-op chain, a `TOP` / `OFFSET` query and a `SELECT … INTO` alike.
The gate is syntactic on the written term, not on what it resolves to: `SELECT 5 AS x FROM t ORDER BY x` sorts, because the term is an alias reference.

The **ordinal form is a signed integer literal**, parentheses included — `(1)` and `+1` name the first column, `-1` and `-(1)` are position -1 (Msg 108) — while an arithmetic expression folding to the same number is a constant instead (`2 - 1` → Msg 408).

Rejected (probe-confirmed): a literal of any type (`'x'`, `1.5`, `1e0`, `0x01`, `NULL`), arithmetic or concatenation over literals (`1 + 0`, `'a' + 'b'`), `CAST` / `CONVERT` of one, `COALESCE` over literals, a `COLLATE` postfix over one (`'x' COLLATE Latin1_General_CI_AS`), a `CASE` / `IIF` whose conditions and arms are all constant, a call to a **folded built-in** over constant arguments (below), and any of those in parentheses.
Accepted: a variable inside an expression (`@v + 1`), a subquery (`(SELECT 1)`), a scalar UDF call — schema-bound-deterministic or not — and every function reading server or session state (`GETDATE()`, `NEWID()`, `RAND()`, `DB_NAME()`, `@@SPID`, `@@VERSION`) — real evaluates rather than folds those.
`ISNULL(NULL, 1)` is accepted where `COALESCE(NULL, 1)` is rejected, matching real's own split (COALESCE desugars to a CASE the folder reaches; ISNULL stays a runtime call).

A **variable** term is real's variable-column-position shape and gets its own error, **Msg 1008** (`The SELECT item identified by the ORDER BY number N contains a variable …`), whenever the variable is reachable through pure conversions only — `@v`, `(@v)`, `((@v))`, `CAST(@v AS int)`.
A variable inside arithmetic is a sort expression and orders the rows: `@v + 1`, `-@v` and `(@v) + 0` all sort (probe-confirmed).

#### The folded built-in catalog

Real folds a call whose name is in its own intrinsic-folding list and whose every argument is itself constant, then rejects the folded term.
The list is **not** the deterministic set, in both directions (all probe-confirmed): `DATENAME` folds although `OBJECTPROPERTY(…, 'IsDeterministic')` calls it nondeterministic, while `UPPER`, `LOWER`, `QUOTENAME`, `STRING_ESCAPE`, `HASHBYTES`, `COMPRESS`, `DECOMPRESS`, `ISJSON`, `CHOOSE`, `ISNULL`, `PARSENAME`, `JSON_MODIFY`, `JSON_ARRAY`, `JSON_OBJECT`, `SQL_VARIANT_PROPERTY`, `FORMATMESSAGE` and `TRY_PARSE` are deterministic yet sort fine over literal arguments.
So the simulator carries its own probed list in `ConstantFolding.FoldedBuiltIns` (ABS / LEN / CHARINDEX / CONCAT / the math, date-part, date-from-parts, regexp, bit and JSON-read families, the CAST / CONVERT / TRY_ pair, NULLIF, GREATEST / LEAST, IIF …), and sharing the determinism table instead would have rejected terms real accepts.

Folding is bottom-up and one non-foldable argument stops it: `ABS(LEN('abc'))` is rejected, `LEN(UPPER('abc'))` and `CONCAT('a', GETDATE())` sort.
A column, variable or subquery anywhere inside likewise stops it — `IIF(1 = 1, col, 2)`, `CASE WHEN col = 1 THEN 1 ELSE 2 END` and `CASE WHEN EXISTS (SELECT 1) THEN 1 ELSE 2 END` all sort.

Detection is `Expression.IsWrittenConstant` = a parse-time mark (set by the built-in dispatcher and the CASE parser when every argument parsed inside came back constant) OR a conservative structural walk over the literal-bearing node types, default false — so a shape neither half recognizes sorts rather than raising.

A term whose *fold itself raises* is no constant to real: `ORDER BY 1/0`, `ORDER BY CAST('a' AS int)` and `ORDER BY POWER(CAST(2 AS int), 40)` stand as sort keys, and the error surfaces when the plan starts — see [runtime constants at plan start](#runtime-constants-at-plan-start).
Under `DISTINCT` such a term is Msg 145 and after a set operation Msg 104, both while compiling, as for any term missing from the select list.
`CHECKSUM(*)` / `BINARY_CHECKSUM(*)` hash the row, so they are no constant either.

**Divergences** on this path: where real reports every ORDER BY error it finds, the simulator raises the first, so `ORDER BY nosuch, 'x'` is Msg 408 (position 2) rather than real's leading Msg 207.

#### Inside `OVER` / `WITHIN GROUP` (Msg 5308 and 5309)

The same folded-constant predicate drives a second pair of rejections in the ORDER BY positions that carry **no ordinal semantics** — an inline `OVER (ORDER BY …)`, a named `WINDOW w AS (…)` definition, `WITHIN GROUP (ORDER BY …)` on `STRING_AGG` / `PERCENTILE_CONT` / `PERCENTILE_DISC`, and `NEXT VALUE FOR seq OVER (ORDER BY …)`.
Probing the two paths cell by cell found them in exact agreement on *what* counts as constant; only the message differs, and which of the two fires is decided on the **folded value**:

| Folded term | Message |
| --- | --- |
| An `int` of at least 1, however written — `1`, `+1`, `(1)`, `300`, `1 + 1`, `CAST(1 AS int)`, `COALESCE(NULL, 1)`, `ABS(-1)`, `LEN('abc')`, `IIF(1 = 1, 1, 2)` | **Msg 5308** `Windowed functions, aggregates and NEXT VALUE FOR functions do not support integer indices as ORDER BY clause expressions.` |
| Everything else — `'x'`, `NULL`, `1.5`, `0x01`, `$1`, `0`, `-1`, `1 - 2`, `CAST(1 AS bigint)`, `CAST(1 AS tinyint)`, `'a' + 'b'`, `CASE WHEN 1 = 1 THEN 'a' ELSE 'b' END` | **Msg 5309** `Windowed functions, aggregates and NEXT VALUE FOR functions do not support constants as ORDER BY clause expressions.` |

Both are Class 15, State 1.
Real applies **no range check** against the select list: `OVER (ORDER BY 100)` over a one-column SELECT is Msg 5308, not Msg 108.
`PARTITION BY` has no such rule — `OVER (PARTITION BY 'x' ORDER BY col)` and `PARTITION BY 1 + 1` sort (probe-confirmed) — and a bare variable is accepted here, so **Msg 1008 has no counterpart in this position** (`OVER (ORDER BY @v)` ranks the rows).

Deciding 5308 vs 5309 needs the folded *value*, so the gate evaluates the term at parse time; **a fold that raises is no rejection**, and in an `OVER` clause real never evaluates that key at all: `OVER (ORDER BY 1/0)`, `OVER (ORDER BY CAST('a' AS int))` and `OVER (PARTITION BY 1/0 …)` return their rows unraised (probed 2026-09-26), so the parser swaps such a key for a constant the sort reads instead.
A `WITHIN GROUP` key orders the aggregated values and real does evaluate it — `STRING_AGG(v, ',') WITHIN GROUP (ORDER BY 1/0)` raises — so that position keeps its term.
**A constant of a MAX type isn't folded at all**: `OVER (ORDER BY CAST('a' AS varchar(max)))`, `CAST(NULL AS nvarchar(max))` and `CAST('a' AS nvarchar(max)) + 'b'` order nothing and raise neither Msg 5309 nor, under a RANGE frame, the Msg 8728 a MAX-typed column earns (probed 2026-09-28 against SQL Server 2025), so the parser swaps such a key for the same stand-in constant.

**Divergences** on the window path:

- An `int`-typed NULL a `TRY_` conversion produced is Msg 5308 on real — its index test is a "not less than one" comparison, which NULL answers UNKNOWN — while the simulator reports 5309 for every NULL, matching real only for the written `NULL` and `CAST(NULL AS int)` spellings.
- Real doesn't fold some deterministic built-ins either — `OVER (ORDER BY UPPER('a'))` sorts (probed 2026-09-28) — where the simulator folds every written constant but a MAX-typed one and reports Msg 5309.
- Two cells land on the wrong side of the 5308 / 5309 split for type-modeling reasons unrelated to the gate: `NULLIF(1, 2)` is `tinyint` on real (small integer literals are typed by magnitude) but `int` here, and `JSON_PATH_EXISTS` returns `int` on real but `bit` here.
- `ROW_NUMBER() OVER w` — a named-window reference from a *non-aggregate* window function — isn't parsed at all (Msg 102); the aggregate form (`SUM(v) OVER w`) is, and carries the gate.

### Runtime constants at plan start

Real evaluates a query's runtime constants once, as its plan starts and before it reads a row, so an error in one raises over an empty table and under a WHERE that excludes every row alike (probed 2026-09-26 against SQL Server 2025): `SELECT 1/0 FROM t WHERE id = 999`, `SELECT id FROM t WHERE id = 999 AND id IN (1, 1/0)`, `SELECT id FROM empty ORDER BY 1/0` and `DECLARE @x int = 0; SELECT … WHERE id = 1/@x` all raise.
`ConstantFolding.CollectStartupConstants` gathers them while `BuildSqlProjection` compiles — from the projection, WHERE, JOIN ON and ORDER BY — and the plan runs them before enumerating.
Two kinds qualify, and only where evaluating one can raise: a written constant whose fold raises (a fold that succeeds can't raise later), and a side-effect-free computation over variables and literals (`@x / 0`), whose value is only known per execution.

What real leaves to the row, the walk leaves too:

- a branch real may not take — `CASE`, `IIF`, `COALESCE`, `NULLIF` and `CHOOSE` are not entered;
- an aggregate's or window function's operand — `SUM(1/0)` over no rows is NULL;
- a subquery, which starts its own plan — `WHERE EXISTS (SELECT 1/0 FROM t)` ignores the projection;
- HAVING, which filters groups rather than starting the plan;
- a scalar aggregate's ORDER BY, whose one-row sort real never runs — `SELECT COUNT(*) FROM t ORDER BY MAX(a) / 0` returns its row, so the simulator drops that sort outright.

And some plans never start the evaluation at all:

- a WHERE settled never-TRUE while compiling (`WHERE 1 = 0`), which on an ungrouped statement also skips every source, so a derived table's `1/0` under it doesn't raise;
- `TOP 0`;
- a FROM of constants alone (`VALUES`, no FROM), which real runs as a constant scan — `Selection.ReadsStorage` tells the two apart through derived tables;
- a singleton lookup — every key column of a unique key pinned by an equality — which real answers without starting the rest of the plan, so `SELECT 1/0 FROM t WHERE pk = 99` returns nothing when no row matches, while `pk IN (98, 99)` raises.

A set operation over FROM-less, subquery-free branches alone is one constant scan to real, computed whole before the first row goes out: `SELECT 1 UNION ALL SELECT 1/0` raises with no row sent.
A branch with a `FROM`, a subquery or a `WHERE` that doesn't fold to TRUE — `WHERE 1 = 0` included — keeps the chain streaming, so `SELECT 1 UNION ALL SELECT 2 WHERE 1 = 0 UNION ALL SELECT 1/0` sends its 1 first (`Selection.IsBareConstantRow`).

UPDATE, DELETE and `INSERT … SELECT` start the same way, and one more thing happens there: a statement-wide value written to a column — a literal, a variable or parameter, a computation over those — is converted to that column as the plan starts.
So `UPDATE t SET v = 'toolong' WHERE id = 999`, the same through an over-long `@p` (a SqlClient parameter included), and `INSERT t (v) SELECT 'toolong' FROM t WHERE id = 999` raise Msg 2628 over no qualifying row (`RunUpdateStartupConstants`, `ExecuteSelectSource`).
The same exemptions hold — a never-TRUE WHERE, `TOP (0)` (which reads no row at all), a unique-key singleton, `ANSI_WARNINGS OFF` for the truncation.

**Divergences**:

- A one-row sort over constants evaluates its key: `SELECT 1 ORDER BY 1/0` and `SELECT x FROM (VALUES (1)) v(x) ORDER BY 1/0` raise Msg 8134 where real, knowing the row count, sorts nothing.
- An uncorrelated subquery real also evaluates at plan start is left to the row: `SELECT id FROM empty WHERE id = (SELECT 1/0 FROM t WHERE id = 5)` raises there and returns nothing here.
- A `WITHIN GROUP` key isn't collected, so `STRING_AGG(v, ',') WITHIN GROUP (ORDER BY 1/0)` over no qualifying row returns NULL where real raises.

### Top-level ORDER BY over a set operation

The ORDER BY after a UNION / INTERSECT / EXCEPT chain is a different resolver (`ApplyTopLevelOrderBy`) with a stricter rule, because the combined stream carries only the projected columns — there is no source row left to reach into.
Real binds it at **compile time**, against the **leftmost branch's FROM scope**; the simulator does the same, in `ValidateSetOpOrderByTerms`, off the `BranchFromSources` / `ProjectionExpressions` the combined plan inherits from that branch.
Compile-time is load-bearing rather than incidental: resolution used to be per-row, so a query yielding at most one row skipped the sort and reported nothing at all.

A term is legal only as an **in-range ordinal** or a **bare reference to a projected column**, spelled either as its **output alias** (unqualified terms only) or as the **source column behind it** (`SELECT num AS Col2 … UNION … ORDER BY num` sorts by Col2 — what ORMs emit when they alias every output positionally).
The alias is checked first, so an alias shadowing a different source column keeps its binding; and a **qualified** term skips the alias scan entirely, exactly as on the single-SELECT path — `SELECT c.extra AS id, c.id AS other … UNION … ORDER BY c.id` sorts by `other`, while `ORDER BY id` sorts by the alias (both probe-confirmed).

Everything else is an error, and *which* error is the whole distinction (real emits the binding failure first and Msg 104 second, so the simulator's first-error contract makes them mutually exclusive):

| Term | Result |
| --- | --- |
| Column in the first branch's scope but not projected (`ORDER BY name`, `ORDER BY a.name`, a joined or derived table's column) | **Msg 104** `ORDER BY items must appear in the select list if the statement contains a UNION, INTERSECT or EXCEPT operator.` |
| Any expression over a bound name (`ORDER BY id + 1`, `LEN(name)`, `(SELECT 1)`, `c COLLATE …`) | **Msg 104** |
| Name nothing in scope carries — including a column only a *later* branch has (`ORDER BY other`), and an output alias used inside an expression (`ORDER BY zz + 1`) | **Msg 207** `Invalid column name '…'.` |
| Qualifier no FROM source answers to — the second branch's alias, an output alias, an unknown one (`ORDER BY b.id`) | **Msg 4104** `The multi-part identifier "…" could not be bound.` |
| Ordinal below 1 or past the column count | **Msg 108**, then Msg 104 (probed 2026-10-01) |

A FROM-less branch contributes an **empty** scope, not an unknown one: `SELECT 2 AS X UNION ALL SELECT 1 ORDER BY X` is legal and `ORDER BY Y` is Msg 207.
A skip-mode placeholder source suppresses the whole pass — real defers such a statement's binding, so no name in scope can be a compile error there.

A *constant* term never reaches this table — the ORDER BY parser rejects it up front with Msg 408 (below), on this path and the single-SELECT one alike.

## Result drain / ORDER BY representation
The FROM-bearing SELECT projection paths — streaming, buffered (ORDER BY / DISTINCT), windowed, and aggregate — all yield already-projected `SqlValue[]` rows, so `SimulatedSqlResultSet` serves the reader and TDS cursors directly with no encode-then-re-decode round-trip (see the `SimulatedSqlResultSet` doc + [`data-reader.md`](data-reader.md)).
**ORDER BY on a single SELECT ranks rows by their keys and projects the survivors into `SqlValue[]`** (see [Top-N selection](#top-n-selection)), so ordered drains ride the same decoded-once fast path as unordered ones, with no decode round-trip.

The **top-level ORDER BY after a set-op chain** (`ApplyTopLevelOrderBy`) is the exception: the inner UNION / INTERSECT / EXCEPT chain yields byte[] rows natively (branch dedup / coercion re-encode), so that path keeps the byte[] form through the sort and lets the drain cursor decode once — eagerly decoding every column into `SqlValue[]` for the whole buffer measured slower and heavier (strings re-materialized and retained across the sort).
Sort keys decode only the ORDER BY columns off each row (`ComputeTopLevelOrderKeys`), not the full tuple.

## Row order without ORDER BY

A query without ORDER BY has no specified order, but real's plan often sorts on its own, and a client reading rows positionally — or a test written against real — sees that order.
Where the order follows from the query's shape it is modeled (`Selection.Execution.ImplicitOrder.cs`); where it follows from real's cost choices or its hashing, arrival order stands.
Probed 2026-09-29 against SQL Server 2025 with each query's showplan beside its rows; oracle `ImplicitOrderTests`.

**Windows.**
Real evaluates windows in written order, one Sort + Segment per distinct requirement, and the rows leave in the **last** sort's order.
A window whose partition-then-order keys an earlier sort already satisfies joins that sort (partition keys in any order, ordering keys as a prefix), one whose own sort satisfies every window an earlier sort serves replaces it, and anything else sorts again.
So `ROW_NUMBER() OVER (ORDER BY x), RANK() OVER (ORDER BY s DESC), SUM(x) OVER (ORDER BY x)` sorts by `x` for the first and third, then by `s DESC`, and returns rows by `s DESC`; swapping the first two returns them by `x`.
`OVER ()` and an ordering by a constant (`ORDER BY (SELECT NULL)`) sort nothing.
The same holds over a grouped query's groups, and for the bounded per-partition `ROW_NUMBER()` path, which yields partition by partition in key order.
A DML statement through a windowed view or CTE runs its body with `BatchContext.WindowRowsInArrivalOrder` set, because it pairs the body's rows positionally with base rows.

**GROUP BY and DISTINCT.**
A small input takes a Sort + Stream Aggregate (or a Distinct Sort), so groups leave in key order:
- The keys sort in written order — except that a GROUP BY of exactly **two** keys that computes an aggregate sorts them the other way round: `SELECT COUNT(*) … GROUP BY a, b` sorts by `b, a`, while three, four and five keys, a GROUP BY with no aggregate (real runs it as a DISTINCT) and every DISTINCT keep the written order.
- Over one table, a key set equal to an index's leading columns reads in that index's order (the clustered index first), and a key set covering a unique key isn't aggregated at all, so the scan's order stands.
- `ROLLUP` — and the `GROUPING SETS` chain spelling the same thing — sorts in written order and emits each subtotal after the groups it totals, the grand total last.
- `DISTINCT` over a grouped query sorts the distinct rows again.

Real hashes a large input instead, in an order nothing reproduces: in a sweep of `x % G` over an `int` heap, 100 and 300 rows always streamed, 1,000 rows streamed only at 500 or more groups, 3,000 rows only when every row was its own group, and 10,000 and 50,000 rows always hashed.
Because a sort costs nothing in fidelity where real hashes, the rule sorts up to `ImplicitOrderSortCap` (4096) groups whatever real chose, and leaves arrival order past it.

**Set operations.**
`UNION`, `INTERSECT` and `EXCEPT` over a small input sort — the UNION's concatenation, or the INTERSECT / EXCEPT's left input — so their rows leave with every column ascending in select-list order; `UNION ALL` concatenates, and a `UNION ALL` after a `UNION` appends to the sorted part.
Real hashes the same shapes from about a thousand rows, and the sort stops at the same cap.
Over constants alone real proves the rows distinct and concatenates (`SELECT 2 UNION SELECT 1` is 2, 1), so a chain that reads no storage keeps arrival order.

**TOP, OFFSET and a derived table's ORDER BY.**
These already follow real: a bare `TOP` reads the scan's order (key order over a clustered table), a derived table's `TOP … ORDER BY` or `OFFSET` leaves its rows in that order, and `TOP 100 PERCENT … ORDER BY` in a derived table is dropped as real drops it.
A `TOP` over a grouped, distinct or set-operation query picks from the sorted rows, so it keeps the rows real keeps.

**Not modeled yet.**
- The order among rows a sort ties: real's sort isn't stable, and its tie order matched no textbook quicksort, heapsort or insertion variant tried against it; here ties keep the earlier sorts' order and then arrival, which is what an index already supplying the order gives on real too.
- `CUBE` and `GROUPING SETS` other than a rollup chain, which real runs as a concatenation of stream aggregates in an order of its choosing.
- A constant-only set operation real sorts anyway: a chain of three or more `UNION`s over literals is a merge (`SELECT 3 UNION SELECT 1 UNION SELECT 2` is 1, 2, 3), as is one whose constants repeat.
- Join order — see [`joins.md`](joins.md#row-order-of-a-join).

## Aggregates
`COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT)` / `COUNT_BIG`, `SUM` / `AVG`, `MAX` / `MIN`, statistical (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`, and SQL Server 2025's `PRODUCT` (SUM's result types, save a fractional decimal multiplying at scale 6; `ProductAggregator`).
`APPROX_COUNT_DISTINCT` counts exactly and `APPROX_PERCENTILE_CONT` / `APPROX_PERCENTILE_DISC` compute the exact percentile — see [the approximate aggregates](#the-approximate-aggregates) for where real's sketches part from that and why neither is reproduced.
`AVG(int)` truncates; `AVG(decimal(p,s))` widens to `decimal(38, max(s,6))`.
`SUM` / `AVG` also widen `real` to `float` and `smallmoney` to `money`, where `MIN` / `MAX` keep the operand's type — see [`arithmetic.md`](arithmetic.md#the-approximate-family-float--real).

A filter written **above** a grouped body — and the key set of an equi-join to one — reaches *below* the grouping when it names a grouping column, so the aggregate runs over the groups the statement keeps rather than every group in the table: see [`joins.md`](joins.md#join-key-reduction-of-a-grouped-body).

### A quantified call is an aggregate call whatever the name

Real's grammar reads `name(DISTINCT expr)` and `name(ALL expr)` as an aggregate call before it knows what `name` is, so the quantifier never reaches the name's own grammar and the refusal depends on what the name turns out to be (probed 2026-09-27 against SQL Server 2025, every built-in name; `QuantifiedCall`):
- The aggregates that take `DISTINCT`, and the functions with a grammar of their own (`COALESCE`, `CONVERT`, `TRY_CONVERT`, `LEFT`, `RIGHT`, `NULLIF`, which refuse the keyword where it stands with Msg 156), parse as they always do.
- Any other one-part name takes exactly one operand, so a second argument is Msg 102 at its comma (`group_concat(DISTINCT x, ':')`, `STRING_AGG(DISTINCT x, ',')`, `ISNULL(DISTINCT x, 1)`), a `WITHIN GROUP` after the call is Msg 102 at `WITHIN`, and `CAST(DISTINCT x AS int)` is Msg 156 at the `AS`.
- A scalar built-in or an unknown name is **Msg 195** "is not a recognized aggregate function", named as written, raised while parsing — so it outranks a later syntax error and fires in a dead branch — but only after any `OVER` clause has parsed.
  That clause is read for its grammar alone (`WindowExpression.ParseClauseSyntax`): a syntax error inside it wins, a frame standing without `PARTITION BY` or `ORDER BY` and a window name refined by nothing among them, while what binding settles — a missing column, a constant ordering term, a frame's bounds, the window a name refers to, a missing sequence — doesn't (probed 2026-10-01 against SQL Server 2025).
- `STRING_AGG` and the two JSON aggregates are Msg 313 state 2 and the approximate percentiles Msg 8726, both binding errors held in `ParserContext.PendingBindError`, so a later syntax error or the FROM clause's Msg 208 outranks them; with `DISTINCT` and an `OVER` they are Msg 10759 instead, once the clause has parsed.
  The Msg 313 joins a statement's binder report where the call's closing paren sits, and only while nothing ahead of it in the binder's order has failed — its operand, which binds as the call does, included: `STRING_AGG(DISTINCT s), x1` reports Msg 313 then Msg 207, `x1, STRING_AGG(DISTINCT s)`, a `HAVING` over `x1` and `STRING_AGG(DISTINCT x1)` the Msg 207 alone, an `ORDER BY` over `x1` both, and a second refused call nothing more (probed 2026-10-01).
  A Msg 8726 never joins one; a report holding anything else drops it.
- `APPROX_COUNT_DISTINCT(DISTINCT x)` is Msg 16200 while parsing; its `ALL` is harmless, and is what admits an `OVER` clause.
- A schema-qualified name is a user-defined aggregate, which takes a whole argument list and is Msg 208 state 214 when absent — even when the name is a scalar UDF — deferred like any missing object, so a dead branch compiles.
  With `DISTINCT` and an `OVER` it is Msg 102 near `'distinct'`, in lowercase, once the clause has parsed.

**Divergence.**
`APPROX_PERCENTILE_DISC(ALL 0.5) OVER ()` kills the session on real (severity 21, probed 2026-09-27); here it is Msg 8726.

### The approximate aggregates

`APPROX_COUNT_DISTINCT` is `CountAggregator` in its distinct form and the `APPROX_PERCENTILE` pair is `PercentileAggregator`, both exact.
Real's answers are reproducible neither way, for different reasons (probed 2026-09-29 against SQL Server 2025, from query outputs alone):

- **`APPROX_COUNT_DISTINCT`** is a HyperLogLog sketch (Microsoft's docs name the algorithm) of 4096 registers: over the integer prefixes up to 400 values every answer matches linear counting, `round(4096 · ln(4096 / V))` over `V` empty registers, exactly, and the spread of its errors over thirty disjoint 100,000-value ranges (σ ≈ 1.8%) fits that register count.
  So real is exact only while no two values share a register, and consecutive integers first share one at 52 values: `generate_series(1, 52)` answers 51.
  Which register a value lands in depends on a hash the outputs don't identify.
  Pairwise collisions among the integers 1 to 400 were compared with Murmur3 (32- and 128-bit), xxHash32 / 64, FNV-1 / 1a (32- and 64-bit), CRC-32, MD5, SHA-1 / 256 / 512, MurmurHash2 (32 and 64A), SplitMix64 and the Murmur finalizers, over 4- and 8-byte little- and big-endian encodings, the decimal text and UTF-16 text, 25 common seeds and every 12-bit window of the hash, with no match.
  Any CRC, or any other hash affine over GF(2), is ruled out outright: real's collision between 41 and 52 would force one between 1 and 28, which real doesn't show.
  Every integer type hashes a value alike (`tinyint` through `bigint` collide on the same pairs), while `decimal`, `float`, `real`, `money` and the string and binary types don't share those collisions.
  The answer also depends on the plan: over one input, real's batch-mode hash aggregate and its row-mode one disagree (`generate_series(1, 50000)` is 50,639 in batch mode and 49,570 under `DISALLOW_BATCH_MODE`), so a reproduced sketch would also have to follow real's choice of execution mode.
  Equality is the operand's own, as for `COUNT(DISTINCT)`: a case-insensitive collation folds `'a'` with `'A'`, trailing spaces don't count, `N'ß'` equals `N'ss'` under the default collation and `-0e0` equals `0e0`.
  Microsoft's page for the function says collation-sensitive strings match as under a `BIN` collation; the server folds them as above.
- **`APPROX_PERCENTILE_CONT` / `_DISC`** answer the exact percentile for up to 977 non-NULL values in a group, whatever the type.
  From 978 on real compacts its sketch, and the answer changes between executions of one statement — the 1.3th percentile of a fixed 978 values was 1,304 on some runs and 1,416 on others — while every group of one execution agrees.
  A randomized compaction is nothing a query output can predict, so the exact percentile stands in as the deterministic answer.

**Clauses.**
- `APPROX_COUNT_DISTINCT(x) OVER (…)` is Msg 4113 state 5, naming the function as written.
  It is raised once the window clause has parsed, so a syntax error inside the clause wins, and ahead of binding, so a dead branch raises it and a missing table doesn't preempt it.
- `APPROX_COUNT_DISTINCT(ALL x) OVER (…)` is accepted and counts distinct values over the window: partitions, running counts and sliding frames all apply.
- An approximate percentile's fraction is part of what the call aggregates.
  An aggregate or subquery there is Msg 130, an enclosing query's column beside a column of the call's own scope is Msg 8124, and a column of the scope that owns the call is Msg 8726.
  The Msg 8726 is settled after the rest of the statement has bound, so a Msg 207 elsewhere wins, and a dead branch raises it.
  A column of a one-row constant source — a FROM-less derived `SELECT`, a one-row `VALUES` — folds to a constant first and is accepted (`Selection.IsSingleConstantRow`), where a two-row one, a table's column or a `UNION ALL` of constants is refused.

Oracle: `ApproxAggregateTests`.

### Streaming accumulation, and where an error surfaces

A query with **one grouping set** — no GROUP BY at all, or a plain GROUP BY — reads each input row exactly once and accumulates straight off the enumeration.
`ROLLUP` / `CUBE` / `GROUPING SETS` partition the same rows several ways, so they still buffer the WHERE-passing rows: the second set can't re-read a stream the first consumed.

That is also the shape real runs: its plan pipelines the Filter into the Stream / Hash Aggregate rather than materializing between them, so **an aggregate operand that raises on an early row preempts a WHERE that would have raised on a later one**.
Over a table whose first row zeroes the divisor and whose last row's text isn't numeric, `SELECT SUM(1 / a) FROM t WHERE CAST(s AS int) > 0` reports **Msg 8134** (divide by zero), not the conversion error — probe-confirmed against SQL Server 2025, and the one observable difference the streaming shape makes.

The scaffolding follows the hoisted-resolver pattern the row-level loops use: one resolver delegate and one `RuntimeContext` for the whole loop, one reused grouping-key scratch buffer (a stable copy is taken only on a group's first row), and a per-group representative row snapshotted once rather than a snapshot per input row.
The representative is what a projection reaching the column *underneath* a grouping expression resolves against (`SELECT MONTH(d), MIN(d) … GROUP BY MONTH(d)`), so it has to be a copy: the join driver rewrites its tuple in place per row.

Oracle: `GroupedAggregateStreamingTests`.

**Not modeled yet**: real sends the groups (and window rows) that completed before an aggregate operand fails — `SELECT g, SUM(i) … GROUP BY g ORDER BY g` over an overflowing second group streams the first group's row, then the error — and follows the error with the Msg 8153 NULL-elimination warning when a NULL was skipped; here the statement fails before its first row and the warning isn't sent (probed 2026-09-26 against SQL Server 2025).
Which groups completed first depends on real's plan (a stream aggregate over sorted input finishes them in key order, a hash aggregate at the end).

### Parallel grouped accumulation (built, proven, **off by default**)

`Selection.Execution.AggregateParallel.cs` forks the streaming path's per-row *consumer* work — the WHERE excluders, the grouping-key evaluation, the aggregate operands, and the column decoding all three trigger — across worker threads while the calling thread keeps producing the row stream.
`AggregateDiagnostics.EnableParallelAccumulation` admits it and is **false**, so shipped behaviour is the serial path, unchanged.

**Why the partition boundary sits there.** Real runs these shapes at DOP 8 while the simulator runs on one core, which is the systematic remainder behind the surviving scan-bound ratios.
A heap-page partition reaches only a single-table scan, and the shapes that actually cost are two-table joins, where partitioning the *driving* side would multiply the hash build by the degree of parallelism.
So `EnumerateJoinedRows` is untouched and stays on the calling thread, and one mechanism then covers a scan and a join alike.
The ceiling is Amdahl over that producer: a bare `COUNT(*)` over the 228k-row `Invoices ⋈ InvoiceLines` join costs ~70 ms of producer on its own, which is the floor for every query reading it.

**Locking and MVCC are untouched.** Every lock probe, snapshot-Xid allocation and version-store read lives inside `BatchContext.WrapWithRowConflictChecks`, which is part of the enumerator — so isolation level, lock footprint and acquisition order are byte-identical and no isolation gate is needed.
Row bytes are safe to hand across threads because `HeapPage.TryReadLiveSlot` already returns a private copy of every scanned row.

**Engagement gates**, all of which must hold; a decline is today's serial path:

| Gate | Rule |
| --- | --- |
| scope | top-level only (`outerResolver is null`), one grouping set, no window function, not nested inside another parallel accumulation |
| size | past 16,384 rows *enumerated*, decided mid-stream so no cardinality estimate has to exist |
| group density | fewer than one group per 8 prefix rows — every worker builds its own map, so a `GROUP BY` over 73k groups pays the merge on nearly every row and measured as a regression |
| sources | every FROM source a plain base table, and every non-persisted computed column's own expression `ParallelSafe` |
| aggregates | only the exact merges: `COUNT` / `COUNT_BIG` / `APPROX_COUNT_DISTINCT` / `MIN` / `MAX` / `CHECKSUM_AGG`, and `SUM` / `AVG` whose accumulator is `long` or `decimal` — `DISTINCT` included |
| expressions | `Expression.ParallelSafe` / `BooleanExpression.ParallelSafe`, a **default-deny** virtual opted into node by node |
| ambient load | the fan-out's share shrinks with `Simulation.StatementsInFlight`, and a process-wide budget caps outstanding workers at `Environment.ProcessorCount` |

`SUM` / `AVG` over `float` / `real`, the statistical family, `STRING_AGG` and the JSON aggregates are declined outright: their merges re-associate or depend on arrival order.
Real's own DOP-8 plan is nondeterministic on exactly those, and the simulator chooses a determinism real doesn't offer rather than reproducing the nondeterminism.

**The error rule.** On any exception — a worker's row, or the producer's own once workers exist — the attempt is cancelled, every partial group is discarded, and the statement **re-runs serially from scratch**.
The re-run is the canonical row order and raises the canonical error, so the probe-pinned preemption semantic above survives verbatim, by construction rather than by argument.

**Serial order stays observable where it is observable.** Blocks go round-robin to fixed per-worker queues, so each worker sees rows in increasing order; the merge then keeps the lower-ordinal group's key tuple and representative row (`GroupState.FirstRowOrdinal`).
That reproduces the serial answer where a group's identity is coarser than its rendering — a case-insensitive collation buckets `'abc'` with `'ABC'` and projects whichever arrived first.
What ordinals can't settle — a `MIN` / `MAX` tie between values that compare equal and render differently — `Aggregator.TryMergeFrom` declines, which re-runs serially.

**Why it is off.** Measured on the in-container SQL Server 2025 (same silicon), the fan-out is worth **1.2-2.4×** on a single-session battery: `conditional.agg_pivot` 160 → 79 ms, `json.value_70k` 106 → 44 ms, `funcwrap.upper_scan` 36 → 20 ms, `having.profitable_items` 64 → 23 ms.
On the **concurrent** workload driver it is a loss: WideWorldImporters gains ~13% throughput, but AdventureWorks loses ~25-30% — and does so even across a phase in which the fan-out never engages again, so a handful of forks costs the process persistently.
Thread lifetime (a retiring pool, and a 1 ms idle timeout) and block-allocation size were both ruled out by measurement; the mechanism of that residual cost is unexplained, and explaining it is the gate on ever flipping the default.

Oracle: `ParallelAggregateTests` — serial-vs-parallel equality on every admitted kind and the boundary shapes, the error re-run, and each gate's decline read off the `AggregateDiagnostics` trace.

### Top-N over the grouped stream

A small `TOP (n)` / `FETCH` whose ORDER BY runs over the *groups* takes the same bounded heap the row-level projection does (see [Top-N selection](#top-n-selection) for the mechanism and the tie order).
Only a plain `TOP (n)` / `FETCH` of at most 1024 groups with no `OFFSET`, `PERCENT`, `WITH TIES` or `DISTINCT` takes it; the rest sort every group.
`TOP (5) OrderID, COUNT(*) … GROUP BY OrderID ORDER BY N DESC` over 73k groups keeps five in a bounded heap rather than sorting all 73k.
Under the heap the projection and key tuple are computed into reused scratch and copied only on admission, so the groups that lose cost no allocation at all — which is most of the win at that group count.

### Over a source-less SELECT

A SELECT with no FROM clause reads **one synthesized row**, so an aggregate written over it collapses that row to a single group exactly as one over a one-row table would: `SELECT COUNT(*)` is 1 and `SELECT MIN(-57)` is -57 on real.
`ParseInner`'s FROM-less exit routes such a query to the ordinary `BuildSqlProjection` over an *empty* source array whenever it carries an aggregate, a GROUP BY, a HAVING or a window, and `EnumerateJoinedRows` supplies that one row for a zero-length source array.
The whole aggregate / grouping / window machinery — implicit empty group, `Aggregator` dispatch, DISTINCT, TOP / OFFSET / FETCH, ORDER BY, the Msg 130 / 144 / 164 binding rules, `SELECT … INTO` schema inference — then applies unchanged.
The constant-row path (`BuildSynthesizedSqlRow`, which bakes the projection at parse time) still serves every other FROM-less SELECT, `SELECT 1` included; an aggregate cannot ride it, because an aggregate's value is not a property of its expression alone.

The row count is where the shapes diverge, and each is probe-confirmed:

| Shape | Real | Why |
| --- | --- | --- |
| `SELECT COUNT(*)` | `1` | one synthesized row, counted |
| `SELECT COUNT(*) WHERE 1 = 0` | `0` | WHERE removes the row, the implicit empty group survives it |
| `SELECT MIN(-57) WHERE 1 = 0` | `NULL` | same group, MIN of no rows |
| `SELECT 1, COUNT(*) WHERE 1 = 0` | `1, 0` | a constant projects beside the aggregate; it is not a column, so no GROUP BY rule reaches it |
| `SELECT COUNT(*) OVER () WHERE 1 = 0` | *no rows* | a window has no group to collapse to, so losing the row loses the result row |
| `SELECT 1 HAVING 1 = 2` | *no rows* | HAVING alone makes it an aggregate query, and the predicate discards the group |
| `SELECT COUNT(*) GROUP BY ()` | `1` | the empty grouping set is one group over the whole rowset |
| `SELECT COUNT(*) GROUP BY 1` | **Msg 164** | a GROUP BY item naming no column, the rule that leaves `()` as the only form a source-less query accepts |

A star with no FROM to expand against is **Msg 263** ("Must specify table to select from.") — `SELECT *`, `SELECT 1, *` and, through the routing above, `SELECT COUNT(*), *` alike — while a *qualified* one (`SELECT t.*`) is Msg 107, the unmatched-column-prefix error, and an `EXISTS (SELECT *)` body is legal since its projection is never read (`StarProjection.Unexpandable`).

Oracle: `SourcelessAggregateTests`.

## Outer-scope correlation in the select list

A SELECT's FROM clause is bound **before** its select list, matching SQL Server's binder rather than the written order.
The reason is that a select-list subquery can reference the enclosing query (`SELECT (SELECT t.col) FROM t` returns one value per outer row on real), and a projection's type is resolved statically by `GetSqlType`, which needs the scope in place at parse time.
The same scope now also backs the compile-time bind of WHERE / HAVING / GROUP BY / a JOIN's ON — see [`collations.md`](collations.md#compile-time-binding).

`FindOwnFromClause` locates this SELECT's own FROM by a depth-aware token scan; `ParseInner` then parses the sources there, rewinds to the select list, installs `ResolveColumnTypeAcrossSources` over them as `ParserContext.OuterTypeResolver`, and the FROM arm resumes after the already-parsed sources rather than re-parsing.
Sources are parsed exactly once.
Two details keep the scan honest:

- **`IS [NOT] DISTINCT FROM` carries a depth-0 FROM keyword** that belongs to an expression, not a clause.
  It always follows `DISTINCT` directly, whereas `SELECT DISTINCT … FROM` has the select list in between, so one token of history separates them.
  Every other FROM-bearing construct (`TRIM(… FROM …)`, `EXTRACT(… FROM …)`) is parenthesized and excluded by depth.
- **The pre-pass is speculative.**
  If the FROM can't be parsed on its own — an unresolvable table, a skip-mode dead branch, or a statement-level error the normal order reports first (a CTE's Msg 319 outranking a Msg 208) — the attempt is discarded and the original in-place path runs, so error identity and ordering are unchanged.
  Nothing is kept from a failed attempt.

A FROM-less SELECT that references an outer column can't bake its projection at parse time the way `SELECT 1` does, since the value changes per invocation.
`BuildSynthesizedSqlRow` detects any column reference and defers those expressions to the executor, where the outer resolver is supplied; the reference-free case keeps the baked fast path unchanged.
This is also why `NamedExpression` forwards `VisitColumnReferences` — an alias renames a projection, it doesn't hide what the expression reads, and without the forward `t.col AS v` looked reference-free.

Two more things defer for the same reason, neither of them a written column reference:

- **A parsed subquery.**
  `VisitColumnReferences` doesn't descend into a nested query body, so `SELECT (SELECT o.k FROM …)` looked reference-free and the bake ran the inner plan against a resolver that refuses every name.
  The projection's own subquery count is what settles it — `ParserContext.SubqueriesParsed` snapshotted around the select list, the same compare the aggregate binder uses — and a subquery is a plan of its own that re-reads the enclosing row on every invocation anyway, so the baked value would be stale as well as wrong.
- **A parse that isn't going to run the statement** — an un-taken branch, or a module body being bound at `CREATE`.
  The bake *evaluates* the projection, which for a side-effecting built-in is a side effect the statement never earned: `CREATE PROCEDURE p AS SELECT NEXT VALUE FOR s` drew a sequence value where real leaves it untouched (see [`sequences.md`](sequences.md#a-parse-that-isnt-going-to-run-draws-nothing)).

### A FROM-less body installs the enclosing scope for its nested queries

A nested subquery reads its outer chain from `ParserContext.OuterTypeResolver` — it never sees the `outerTypeResolver` argument its enclosing parse was handed.
A SELECT **with** a FROM installs that chain over its own sources; one **without** installed nothing, so a nested subquery chained through whatever the enclosing parse happened to have left there.
That is what made `CROSS APPLY (SELECT c.x AS x WHERE EXISTS (SELECT 1 FROM o WHERE o.k = c.k)) a` report Msg 207 on `c.k` while the same body carrying its own FROM answered.
`ParseInner` installs the inherited resolver whenever this statement's own FROM never bound — there is none, or the speculative pre-pass discarded it.

Probed against SQL Server 2025 (2026-08-05): the shape answers there, and so do a scalar subquery in the body's select list, an `IN (SELECT …)` and a quantified comparison in its WHERE, two levels of nesting, `APPLY` inside `APPLY`, and a subquery inside an `APPLY` inside a subquery.
The same seam covers a FROM-less scalar subquery wrapping a correlated one with no `APPLY` in sight (`SELECT (SELECT (SELECT COUNT(*) FROM o WHERE o.k = c.k)) FROM c`), which failed identically.

Covered shapes (all probe-confirmed): a FROM-less subquery projecting an outer column, a derived table (VALUES or SELECT, including a FROM-less or set-operation body) in a subquery's FROM, and APPLY both at the top level and nested inside a subquery.

An aggregate reading only the enclosing query's columns — `(SELECT MAX(t.col) FROM u)` inside a query over `t` — binds to the *outer* query on real, collapsing it to one row; see [Aggregate ownership across scopes](#aggregate-ownership-across-scopes).

## Aggregate / GROUP BY binding rules

Four rules SQL Server binds at parse time; without them the simulator is over-permissive (an app query would work here and break on real).
Probe-confirmed; oracle `AggregateBindingRuleTests`.

- **Msg 130 Cls 15 St 1** — `"Cannot perform an aggregate function on an expression containing an aggregate or a subquery."`
  Fires when an aggregate's *own argument* contains another aggregate or a subquery at any depth: `SUM(MAX(a))`, `SUM(a + MAX(b))`, `MAX(CASE WHEN EXISTS(…) THEN a END)`, `MAX((SELECT 1))`, and the correlated form.
  A subquery elsewhere — HAVING, projection, WHERE — is untouched.
  A **windowed** aggregate over an aggregate is legal on real (`SUM(SUM(b)) OVER ()` returns a value); the simulator doesn't parse that shape at all yet, so it can't reach this check — see [`backlog.md`](backlog.md).
- **Msg 8117 Cls 16 St 1** — `"Operand data type NULL is invalid for {aggregate} operator."`
  A bare untyped `NULL` operand, for count / count_big / sum / avg / max / min / stdev / checksum_agg.
  A derived table's or CTE's column its body fills only with bare `NULL`s is untyped too — through `VALUES`, a union and a pass-through level — so an aggregate, an aggregate window or `LAG` / `LEAD` / `FIRST_VALUE` / `LAST_VALUE` over it is refused the same way (`HeapColumn.IsUntypedNull`, probed 2026-09-25).
  A *typed* NULL is fine (`COUNT_BIG(CAST(NULL AS int))` → 0).
  `STRING_AGG(NULL, ',')` uses the argument form instead — Msg 8116, `Argument data type NULL is invalid for argument 1 of string_agg function.`
- **Msg 144 Cls 15 St 1** — `"Cannot use an aggregate or a subquery in an expression used for the group by list of a GROUP BY clause."`
  Takes precedence over Msg 164: a correlated-subquery grouping item reports 144 even though it does reference a local column.
- **Msg 164 Cls 15 St 1** — `"Each GROUP BY expression must contain at least one column that is not an outer reference."`
  Checked **per item**, so `GROUP BY a, GETDATE()` fails despite `a` being valid.
  The rule is purely about column presence, **not determinism** — `GROUP BY a + DATEPART(year, GETDATE())` and even a `NEWID()`-derived expression are legal because they contain `a`, while `GROUP BY 1` / `'x'` / `@v` / `GETDATE()` / `RAND()` are not.
  (`GROUP BY 1` is a constant, not an ordinal; SQL Server has no ordinal GROUP BY.)
  The empty grouping set is exempt — `GROUP BY ()`, `GROUPING SETS (())`, `GROUPING SETS ((a),())` and `GROUP BY (), a` all return rows on real, and contribute no expression for the rule to apply to.

Msg 144 and Msg 164 are **held rather than thrown**: real parses a batch before binding any of it, so a stray token after the clause reports Msg 102 instead (`GROUP BY 'a' 'b'` → `near 'b'`, where `GROUP BY 'a'` alone is Msg 164 — probe-confirmed).
The held message is raised once the statement's outermost query expression has parsed; see the trailing-token section of [`grammar.md`](grammar.md#trailing-token-tightening).

### Why these count at parse time

All four detect "does this sub-expression contain an aggregate / subquery / column?" from monotonic counters on `ParserContext` (`AggregatesParsed` / `SubqueriesParsed` / `ColumnReferencesParsed`), snapshotted before a sub-parse and compared after — **not** by walking the finished expression tree.

A tree walk was tried first and is unsound here: only 16 of the ~170 `Expression` subclasses override `VisitColumnReferences`, and `CASE` and the scalar function calls are not among them.
Demonstrated by `GROUP BY DATEADD(year, 1, a)`, where the walk cannot see `a` — so a walk-based Msg 164 would reject a perfectly legal query, the *worst* failure direction for this work.
Counting at construction is complete by construction instead.

The one wrinkle: a bare name is built as a `Reference` before the parser knows whether `(` follows, so `GETDATE()` briefly looks like a column.
`Expression.ParseCallArguments` — the single funnel for every `<reference>(` shape — decrements on entry to cancel that, leaving a net count of genuine column references.

Residual permissiveness: an *outer* column reference counts like a local one, so a grouping item naming only an outer column inside a correlated subquery stays accepted where real raises Msg 164.
Closing that needs source resolution, not a parse-time count.
Also, `STRING_AGG`'s `WITHIN GROUP (ORDER BY …)` and the JSON aggregates' key expression are parsed *after* the aggregate registers, so an aggregate or subquery hidden there escapes the Msg 130 bracket.

## GROUP BY containment

Msg 8120 (select list) / 8121 (HAVING) / 8127 (ORDER BY): outside an aggregate, every column reference has to be covered by the GROUP BY clause.
Real judges it one expression at a time and only while nothing ahead of the expression failed, so beside an unbindable name it may report or not — see [`errors.md`](errors.md#a-statements-whole-binder-report).
A **bare** grouping column covers references to that column anywhere.
A grouping **expression** covers a projection *sub-expression that matches it* — not the columns it happens to name.
Probed against SQL Server 2025 (2026-08-05):

| Statement (over `GROUP BY a + 1` unless shown) | Real |
| --- | --- |
| `SELECT a + 1` | runs |
| `SELECT (a + 1) * 2` | runs |
| `SELECT ((a + 1))`, `GROUP BY (a + 1)` | runs — parens are transparent |
| `SELECT CAST(a + 1 AS bigint)` | runs — the match is on the inner node |
| `SELECT YEAR(d) * 100 + MONTH(d)` over `GROUP BY YEAR(d), MONTH(d)` | runs |
| `SELECT a + 1 FROM t AS x GROUP BY x.a + 1` | runs — the qualifier's spelling doesn't enter it |
| `SELECT x.a + 1 … x JOIN t AS y … GROUP BY y.a + 1` | Msg 8120 — the column it resolves to does (probed 2026-09-23) |
| `SELECT b + 'X'` over `GROUP BY b + 'x'` | Msg 8120 — a string literal matches exactly, even under a case-insensitive collation (probed 2026-09-23) |
| `SELECT a` | Msg 8120 |
| `SELECT a + 0` | Msg 8120 — structural, not algebraic |
| `SELECT 1 + a` | Msg 8120 — operand order counts |
| `SELECT a + 1` over `GROUP BY a + 1.0` | Msg 8120 — the literal's type counts |
| `SELECT a + b` over `GROUP BY b + a` | Msg 8120, once per column |

The walk carries the covering predicate down the tree (`ColumnReferenceVisitor.CoversSubtree`), so it stops the moment a node matches a grouping expression and every reference that reaches the check is one nothing covered.

**A subquery's reference to the grouped query's own column is checked too** (probed 2026-09-30 against SQL Server 2025).
`SELECT a, (SELECT COUNT(*) FROM t AS u WHERE u.b = t.b) FROM t GROUP BY a` is Msg 8120 on `t.b`, and so is the reference from the subquery's select list, ON, a derived table inside it or a subquery nested in it; from HAVING it is Msg 8121 and from ORDER BY Msg 8127, and a bare scalar aggregate raises it too (`SELECT COUNT(*), (SELECT t.b) FROM t`).
What passes: a grouped column, an aggregate over the outer column alone (the outer query's own, see [aggregate ownership](#aggregate-ownership-across-scopes)), a subquery in WHERE, and a grouping expression's match, which crosses the boundary (`(SELECT b + 1)` under `GROUP BY b + 1` runs).
Each subquery plan keeps its select list and clauses (`Selection.ClauseExpressions`) for that walk, which resolves each name innermost first.
A node's identity is its `ShapeKey`: the tree's node kinds, their own state and their children, with each column keyed by the source column it resolves to.
The walk reaches every expression kind (each describes its children through `ExpressionNode.Describe`), so a column inside a `CASE` arm, `COALESCE` or any scalar's argument is checked like any other (`SELECT COALESCE(a, 0) … GROUP BY b` is Msg 8120, probed 2026-09-23); it stops only at an aggregate, a window function and a subquery.

In a **grouped** query — a `GROUP BY` (`GROUP BY ()` included) or a `HAVING` — real runs the check over the tree it folded, so a `CASE`-family operand a constant decides away is never checked (`Expression.AddFoldedAwayOperands`, probed 2026-09-24):
- `COALESCE` drops every argument after its first constant non-NULL one, wherever that sits (`COALESCE(a, 5, b) … GROUP BY a` runs), while a `NULL` or a raising constant (`1 / 0`) decides nothing;
- `CASE` and `IIF` drop a branch whose condition folds FALSE or UNKNOWN and every branch after one that folds TRUE; a simple `CASE` comparing a constant NULL on either side drops the branch, and once every branch is gone the input goes unchecked too;
- `NULLIF` over a constant NULL drops its second argument.

A bare **scalar aggregate** — aggregates with neither clause — folds nothing first: `SELECT COALESCE(5, b), COUNT(*) FROM t` is Msg 8120.

**Divergence:** in a scalar aggregate real's answer can depend on select-list order — `SELECT COUNT(*), NULLIF(CAST(NULL AS int), b)` runs where `SELECT NULLIF(CAST(NULL AS int), b), COUNT(*)` is Msg 8120 (probed 2026-09-24); the simulator raises for both.

The message names the object the FROM clause **wrote**, not the alias: `SELECT a, b FROM t AS x GROUP BY a` reports `'t.b'`, a self-join reports the table twice, a schema-qualified source keeps its schema (`'dbo.t.b'`), a view is named like a table (`FROM dbo.v AS x` reports `'dbo.v.b'`), and a source with no object of its own falls back to its alias (a derived table `'z.b'`, a table variable `'@t.b'`) — all probed 2026-08-05, the view and CTE rows 2026-08-08.
A **CTE** sits between the two: it has a name of its own and reports it however the reference aliased it (`FROM c AS q` reports `'c.b'`).
The same `FromSource.WrittenObjectName` answers the receiver name an XQuery diagnostic is bracketed with, probed independently and agreeing on every source kind — see [`xml.md`](xml.md#the-receiver-names-the-diagnostic).

## DISTINCT over a grouped query

`DISTINCT` dedupes the **grouped** projection, before ORDER BY and before any row limit.
Grouping alone doesn't imply distinct output — the projection can be narrower than the grouping key, which is how an ORM's `.dates()` collapses one row per record to one row per distinct year (`SELECT DISTINCT YEAR(pubdate) … GROUP BY id, pubdate`).

## Aggregate ownership across scopes

An aggregate whose operand reads only an **enclosing** query's columns belongs to that query: real evaluates it there, which makes the enclosing query an aggregate query and collapses it to one row per group.
`RehomeAggregatesOverOuterScope` moves the same `AggregateExpression` instance into the enclosing scope's collector at parse time (`ParserContext.EnclosingAggregateCollector`), so the nested expression tree keeps referencing it and reads the value the owning query bound.
This is what makes an ORM's `GREATEST` / `LEAST` emission work — `(SELECT MAX(value) FROM (VALUES (AVG(b.rating)), (AVG(b.price))) AS _G(value))` over a joined, grouped outer query.
An aggregate reading no column (`COUNT(*)`, `MAX(1)`) stays where it is written.
It moves one scope at a time, so one written two levels down reaches the outermost query through the middle one, and a derived table, a `VALUES` list (whose cells own no aggregate of their own) and a CTE body pass it along the same way (all probed 2026-09-28 against SQL Server 2025).

**The value changes per group without the subquery reading the outer row.**
The memoized outer-independent subquery ([`subqueries.md`](subqueries.md#an-outer-independent-inner-plan-runs-once-per-statement)) and the once-per-enumeration deferred source both judge replayability by whether the plan consulted the outer row, and a moved aggregate's value arrives through the batch's bound results instead, so `AggregateExpression.ReadsEnclosingGroup` counts each read among `SimulatedDbConnection.VolatileEvaluations`, as `GROUPING` / `GROUPING_ID` reads do.
Without it `SELECT b, (SELECT SUM(t.a) FROM u) FROM t GROUP BY b` answered the first group's sum for every group.

Where the aggregate lands decides whether it may stand there (probed 2026-09-28):

| Moved to | Real |
| --- | --- |
| a select list, `HAVING`, `ORDER BY` | stands |
| `WHERE` | Msg 147 |
| a join's `ON`, in a `SELECT`, joined `UPDATE` / `DELETE` or `MERGE` | Msg 1015 |
| an `UPDATE`'s or a `MERGE` action's `SET` list | Msg 157 |
| a `MERGE`'s `WHEN … AND` condition | Msg 5319 |
| a table value constructor — `INSERT … VALUES`, a `MERGE` insert action, a `VALUES` derived table — when it reads no enclosing column | Msg 5310 |
| a `TOP` / `OFFSET` / `FETCH` count | Msg 4115, naming the column's leaf |
| across an `APPLY`, reading the left side's columns | Msg 4101 |

The clause-owned refusals are raised by a collector the clause installs (`Selection.RefuseClauseAggregates`), and an `APPLY`'s right side sees an `ApplyAggregateBoundary` in its enclosing collector's place: an aggregate reading the left side stops there, one reading columns from further out passes through to the query holding the `APPLY` and on outward.
An operand reading an enclosing column beside a column of any other scope — its own, or a scope between — is Msg 8124 (`MAX(t.a + u.c)` in a subquery over `u`), where `MAX(t.a + t.b)` and `MAX(t.a + @v)` belong to `t`'s query.
A subquery in a DML `OUTPUT` clause is Msg 10705 before its body binds, so an aggregate over `DELETED` never gets that far.

A name that resolves in **no** scope isn't this case at all — it is real's **Msg 207**, at compile time, in a plain statement and at CREATE of a module alike (probe-confirmed: `HAVING MAX(nosuchcol) = 1` refuses a `CREATE VIEW` outright, while the genuinely-outer form creates and only misbehaves when run).
The enclosing type resolver settles which of the two it is — it raises Msg 207 itself once the scope chain runs out — and a FROM-clause placeholder suspends the question entirely, since the name could belong to the missing object.

Ownership is decided by walking the operand's column references, so **an expression wrapper that doesn't forward `VisitColumnReferences` hides them** — `CONVERT` didn't, while its `CAST` sibling did, which made `AVG(CONVERT(float, b.rating))` look reference-free and left the aggregate unbound.

## GROUP BY extensions

`FromClause.GroupingSets: List<Expression[]>` holds the flat grouping-set list — simple `GROUP BY a, b` parses as `[[a, b]]`, `GROUP BY ROLLUP(a, b)` as `[[a, b], [a], []]`, `GROUP BY CUBE(a, b)` as the 2^N power-set entries, `GROUP BY GROUPING SETS((a, b), (a), ())` verbatim.
Mixed forms (`GROUP BY a, ROLLUP(b, c)`) Cartesian-combine each top-level item's fragments at parse time.
A `GROUPING SETS` member may be a `ROLLUP` or `CUBE` (all of its sets) or a parenthesized composite whose elements, `ROLLUP` and `CUBE` included, combine the same way (`GROUPING SETS ((g, ROLLUP(h)))` is `(g, h), (g)`), and a `ROLLUP` / `CUBE` element may be a composite `(a, b)` that rolls up as one unit; an empty `()` inside `ROLLUP` and a `GROUPING SETS` inside another are Msg 102.
Past 4096 sets the query is Msg 10703 — `CUBE` over twelve columns is the largest that passes — and `STRING_AGG` under more than one set is Msg 8710, every other aggregate merging (all probed 2026-10-01 against SQL Server 2025).
The legacy `GROUP BY <cols> WITH ROLLUP` / `WITH CUBE` modifier is equivalent to `GROUP BY ROLLUP(<cols>)` / `CUBE(<cols>)` — after the column list parses to its single Cartesian set, `RollupExpansion` / `CubeExpansion` re-expand it in place (probe-confirmed the row output matches the function forms).
`FromClause.AllGroupingExpressions` is the union (first-seen order) used by GROUPING() validation.

The aggregate executor (`Selection.Execution.Aggregate.cs`) **buffers** WHERE-filtered rows once (snapshotting each tuple because `EnumerateJoinedRows` reuses a single shared array in-place) then iterates each grouping set, partitioning the buffer per set's columns and accumulating fresh aggregators per group.
The projection's column resolver returns typed NULL for columns that aren't in the current set but appear in another set's columns — that's the subtotal/total-row semantic.
Without GROUP BY the executor synthesizes a single empty grouping set `[[]]` and runs one implicit group; same code path covers `GROUPING SETS(())` and the bare **`GROUP BY ()`** form (the empty grouping set = grand total over all rows, one aggregate row).
`ParseGroupByItem` distinguishes `GROUP BY ()` (a `(` immediately followed by `)` → the empty fragment `[[]]`) from `GROUP BY (expr)` (a parenthesized grouping key) via a checkpoint peek.
TOP / OFFSET / FETCH apply to the concatenated stream across all grouping sets, and so does any window in the query — see [Windows over ROLLUP / CUBE / GROUPING SETS](#windows-over-rollup--cube--grouping-sets).

**GROUP BY a scalar expression** (e.g. `GROUP BY MONTH(d)`) projects and orders correctly: a column buried inside a grouping expression resolves against the group's first-seen **representative row** (`GroupState.Representative`) — within a non-empty group every grouping expression is constant, so any row yields the right value for a projection / HAVING / ORDER BY item that's functionally determined by the grouping (`SELECT MONTH(d) … GROUP BY MONTH(d)`, `MONTH(d) + 1`, etc.).
Under a grouping-set form a projection matching a grouping expression by shape reads that expression's key, NULL in a set that groups it away — unless it reads only bare grouping columns, which compute from them, so over `GROUPING SETS ((a + 1), (a))` real computes `a + 1` from `a` while over `((a + b), (a))` `a + b` keeps its own key where `a` is grouped away (`ProjectionGroupingKeys`, probed 2026-09-24); HAVING and a windowed query still resolve column by column.
The representative fallback fires only for non-empty grouping sets, preserving the ROLLUP/CUBE grand-total NULL.

**GROUP BY containment (Msg 8120 select-list / 8121 HAVING / 8127 ORDER BY)** is enforced at parse time on the cached plan build (`Selection.Execution.cs` `ValidateGroupByReferences`), whenever the query is an aggregate query (any aggregate, GROUP BY, or HAVING present).
SQL Server is strict — no functional-dependency relaxation, so grouping by a table's PK does *not* license its other columns — and binds the rule before any row is read.
The check leans on `Expression.VisitColumnReferences` already excluding aggregate-internal columns (an `AggregateExpression` doesn't visit its operand), so it walks each SELECT / HAVING / ORDER BY expression's bare references, resolves each to a source column, and requires it to be a *bare* GROUP BY column; a correlated / outer reference (unresolved against the local sources) is skipped, and an ORDER BY reference matching an unqualified select-list alias is a validated projection, not a source-column violation.
The one deliberate conservative miss: a column appearing only *inside* a compound grouping expression (`GROUP BY a+1`, then a bare `SELECT a`) is left unflagged — distinguishing it from the valid `SELECT (a+1)*2` shape would need sub-expression structural matching, so it errs toward no false positive on the valid form.
Oracle: `GroupByContainmentTests`; Msg 130 / 8117 / 164 aggregate-validation rules remain over-permissive (see [`backlog.md`](backlog.md)).

**ORDER BY on a grouped query** sorts the full grouped stream (across all grouping sets) before TOP / OFFSET / FETCH, so `SELECT TOP (n) … GROUP BY … ORDER BY SUM(x) DESC` selects the correct rows in order.
ORDER BY items resolve a select-list **alias** first (`ORDER BY Total`, bare terms only — see [ORDER BY term resolution](#order-by-term-resolution)), then through the grouped-key / representative-row resolver — so an aggregate (`ORDER BY SUM(x)`, whose `AggregateExpression` is collected and bound like any projection aggregate), a grouped column, or a grouping expression all sort correctly.
Parse-time type-checking is alias-aware to match.

`GROUPING(col)` / `GROUPING_ID(c1, ..., cN)` read the executor-published context off `BatchContext.GroupingSetExpressions` (current set's column list) and `BatchContext.AllGroupingExpressions` (union across query).
Returns `tinyint` 0/1 and `int` bitmap respectively, both NOT NULL, and either in the query's own `WHERE` is the aggregate's Msg 147 (probed 2026-10-01); **leftmost arg of `GROUPING_ID` occupies the most-significant bit** (probe-confirmed against SQL Server 2025 — `GROUPING_ID(region, product)` with region grouped + product not grouped returns `2`, the inverse case returns `1`).
Argument must match a GROUP BY expression.
Arg not in any grouping set → Msg 8161; same Msg for GROUPING outside any GROUP BY context.
The match is the containment rule's `ShapeKey` with each column keyed by its name alone, so it is qualifier-tolerant (`GROUPING(x.a + 1)` over `ROLLUP(a + 1)` matches on real, probed 2026-09-23) and parentheses are transparent.
Probe-confirmed: `GROUPING(a+1)` / `GROUPING_ID(a+1, b)` with matching GROUP BY expressions return their 0/1 markers, `GROUPING((a+1))` (extra parens) still matches, while `GROUPING(1+a)` (operand order differs — no commutative normalization) and `GROUPING(a+2)` (value mismatch) both raise Msg 8161.

`STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY ...)` reorders concatenation per group (EF emits this from `GroupBy(...).Select(g => string.Join(sep, g.OrderBy(...)))`).
NULL operand rows skip both ORDER BY input and output.
The result widens to the family maximum — `varchar(8000)` / `nvarchar(4000)`, MAX staying MAX — and a numeric or date/time operand converts to `nvarchar(4000)` the way a default-style `CAST` would; every other type is Msg 8116 (`StringAggAggregator.ResultType`, probed 2026-09-23).
**A bounded (non-MAX) operand whose concatenation exceeds 8000 bytes raises Msg 9829** (`"STRING_AGG aggregation result exceeded the limit of 8000 bytes. Use LOB types to avoid result truncation."`, probe-confirmed against SQL Server 2025; state 0 for a `varchar` result, 1 for an `nvarchar` one, probed 2026-10-01) rather than truncating — the byte count uses UTF-16 width for `nvarchar` and the result collation's ANSI code page for `varchar`; a `varchar(max)` / `nvarchar(max)` operand streams unbounded and skips the check (and, retyped through the operand, rides PLP over the TDS wire).
Non-`STRING_AGG` aggregate with `WITHIN GROUP` → **Msg 10757**; ORDER BY ordinal in this context → **Msg 5308** (distinct from projection-level ORDER BY which accepts ordinals); `WITHIN` is contextual (not reserved).
Cross-aggregate Msg 8711 isn't modeled (EF doesn't emit).

### `GROUP BY ALL`

Real's deprecated form keeps every group the source produces, whatever the `WHERE` does to its rows, and the aggregates read only the rows the `WHERE` keeps — a group none of whose rows pass has `COUNT` 0 and every other aggregate NULL, and a dropped row never evaluates an aggregate's operand (`SUM(10 / v)` over a filtered zero is NULL, not Msg 8134).
Without a `WHERE` it is a plain `GROUP BY`.
The groups are those of the joined rows, `HAVING` reads the filtered aggregates, and with no `ORDER BY` the groups leave in the order a plain `GROUP BY`'s would ([row order](#row-order-without-order-by)) — all probed 2026-09-30 against SQL Server 2025, at compatibility level 100 as at 170.
The parse moves the `WHERE`'s conjuncts from `FromClause.Excluders` to `FromClause.GroupByAllFilter`, so the seek, pushdown and parallel paths see a query with no filter and read every row, and the aggregate executor forms each row's group before asking the filter whether the row feeds the aggregates.

What real refuses:
- **Msg 1028** for a `ROLLUP`, `CUBE` or `GROUPING SETS` item, the legacy `WITH ROLLUP` / `WITH CUBE`, or an empty set `()` beside another item — `GROUP BY ALL ()` alone is one group.
  It is a parse-phase error at the token after the grouping list (at the legacy form's `ROLLUP` / `CUBE`), so it outranks a missing table, fires in a dead branch, follows the syntax errors recovery found before it and ends the parse there.
- **Msg 1054** state 8 (*"Syntax 'ALL' is not allowed in schema-bound objects."*) anywhere in a schema-bound view's or function's body, raised by the parser at the `ALL` and ahead of anything the body binds, with recovery reporting what follows — a later `WITH CUBE`'s Msg 319, a later select-list star's own Msg 1054 ([`programmable.md`](programmable.md#schema-binding-with-schemabinding)); a plain view takes it.
- **Msg 7417** when the query's own `FROM` — a derived table included — reads a linked server's table or an `OPENQUERY` rowset and the query has a `WHERE`; a remote read only in a `WHERE` or select-list subquery is fine.
  It is a binding error raised while the batch compiles (a dead branch raises it, a `TRY` doesn't catch it), at the statement's line.
- `GROUP BY ALL` with nothing after it is Msg 102, and the containment rules (Msg 8120, 144, 164) apply as to a plain `GROUP BY`.

**Msg 8153.** Over a `WHERE`, real sends the NULL-elimination warning whenever a row reached the grouping and the query holds an aggregate that reports a skipped NULL — `COUNT(*)` included, and even when every row passed the `WHERE` — as though each group took one extra row feeding every aggregate a NULL; `STRING_AGG` alone doesn't warn, nor does an empty source or a query with no aggregate (probed 2026-09-30).
A FROM-less `SELECT COUNT(*) WHERE 1 = 0 GROUP BY ALL ()` doesn't warn either, where the same shape over a variable that is false does (`Selection.GroupByAllWarns`).

**Divergences.**
- In a statement naming a table the batch itself creates, Msg 1028 surfaces when the statement runs, after the statements before it — the compile's deferral stops before such a statement's grouping list, as it does for its syntax errors (see [`control-flow.md`](control-flow.md#not-modeled-yet)).
- A column that doesn't exist, in a query over a linked server, is Msg 7417 here and Msg 207 on real: the simulator binds a remote source's columns only when the query runs.
- `OPENROWSET` over a provider and `OPENDATASOURCE` don't count as remote for Msg 7417 — not probed.
- Real's Msg 7417 over a remote table the batch creates wasn't observable (the loopback server couldn't see it), so the simulator's deferral there — raising it when the statement runs, where a `TRY` catches it — is unconfirmed.

## Window functions
- Ranking functions (ORDER BY required, raises a generic syntax error otherwise — Msg 4112 territory):
  - `ROW_NUMBER() OVER([PARTITION BY ...] ORDER BY ...)` — bigint.
    EF wraps in a derived-table subquery for `Skip`/`Take`.
  - `RANK()` — bigint; ties share rank, next distinct group jumps to (position + 1).
  - `DENSE_RANK()` — bigint; ties share rank, no gaps in the rank sequence.
  - `CUME_DIST()` — float; per row, `(rows with ORDER BY key ≤ this, peers included) / N`.
    NULLs **participate** in the ordering (NULL sorts first under ASC and forms its own peer group), so they count toward `N`.
  - `PERCENT_RANK()` — float; `(RANK − 1) / (N − 1)` (reuses RANK's tie-with-gaps semantics).
    A single-row partition is defined as 0 (no divide-by-zero).
  - `NTILE(N) OVER ([PARTITION BY ...] ORDER BY ...)` — bigint.
    Distributes the partition into `N` buckets; the first `count % N` buckets carry one extra row each.
    `N <= 0` at runtime → Msg 9819.
    The bucket-count expression is evaluated once per query against the first buffered row's resolver (constants and parameters work; column references would surface as resolver errors — real SQL Server rejects non-constant bucket counts at compile time, the simulator surfaces it as a runtime issue).
    A constant count of zero or less is Msg 4116 while compiling, so it refuses over an empty input too.
- Value functions (ORDER BY required, operand re-evaluated against another row's resolver):
  - `LAG(expr [, offset [, default]]) OVER (...)` — operand type.
    Offset defaults to 1; default expression is evaluated in the boundary row's resolver context when the offset crosses the partition boundary (and typed NULL when no default is given).
    An offset reading the row's own columns is the row's: `LAG(v, ti)` steps back by each row's `ti`, a NULL one reading NULL; and a `LEAD` offset added to the row's one-based position past bigint's maximum is Msg 8115 rather than the default (probed 2026-10-01 against SQL Server 2025).
    The default then converts to the operand's exact type with CAST semantics — `'7'` becomes 7, `2.9` becomes 2, a longer string is cut to the operand's declared length, and `'x'` over an int is Msg 245 — but only a default actually used converts, so one that never reaches a row raises nothing (probed 2026-09-23).
  - `LEAD(expr [, offset [, default]]) OVER (...)` — same shape, opposite direction.
  - `FIRST_VALUE(expr) OVER ([PARTITION BY ...] ORDER BY ... [frame])` — operand type.
    Returns the operand evaluated against the frame's first row (default frame `RANGE UNBOUNDED PRECEDING TO CURRENT ROW` → partition's leading row, broadcast).
  - `LAST_VALUE(expr) OVER ([PARTITION BY ...] ORDER BY ... [frame])` — operand type.
    Returns the operand evaluated against the frame's last row.
    The default frame is `RANGE UNBOUNDED PRECEDING TO CURRENT ROW` — under RANGE+CURRENT ROW the last row is the current row's last peer (by ORDER BY key), so `LAST_VALUE` over the default frame returns the current row's value (or peer-tie last).
    The intuitive "partition last" semantic requires `ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING`.
    Probe-confirmed against SQL Server 2025.
  - **`IGNORE NULLS` / `RESPECT NULLS`** after the four functions' closing paren: `FIRST_VALUE` / `LAST_VALUE` take the first / last non-null value in the frame, and `LAG` / `LEAD` step the offset as usual and then past a null target in the same direction — the default applies only when that first step leaves the partition, and running off its end afterwards is NULL.
    Any other function (ranking, aggregate-OVER) is Msg 16208 naming it and the treatment; without `OVER` the keywords are Msg 156 (probed 2026-09-24 against SQL Server 2025).
- Ordered-set analytic functions — `PERCENTILE_CONT(p)` / `PERCENTILE_DISC(p) WITHIN GROUP (ORDER BY sort [ASC|DESC]) OVER ([PARTITION BY ...])`.
  Modeled as `WindowKind.PercentileCont` / `PercentileDisc` on `WindowExpression`: the percentile fraction lands in `PercentileArg`, the single `WITHIN GROUP` sort key reuses the `OrderBy` field, and the per-partition result is broadcast to every row (no per-row frame).
  The `OVER` clause is **mandatory** (Msg 10753 when absent) and may carry only `PARTITION BY` — an `ORDER BY` inside `OVER` is rejected with Msg 10758 (the ordering must come from `WITHIN GROUP`).
  NULL sort keys are excluded from the computation; an all-NULL / empty partition yields NULL.
  `PERCENTILE_CONT` returns `float` and interpolates at the one-based row number `rn = 1 + p·(n−1)` as `(1 − f)·lo + f·hi`, `f` being `rn`'s fraction — the arrangement that reproduces real's last bit, where `lo + f·(hi − lo)` at the zero-based rank misses it (`APPROX_PERCENTILE_CONT` weights the same way at the zero-based rank, which is what its last bit fits); `PERCENTILE_DISC` returns the **sort expression's own type**, NOT NULL over a NOT NULL key, and picks the smallest value whose CUME_DIST ≥ p (index `ceil(p·n) − 1`, clamped).
  A fraction reading the query's own columns is Msg 8726 and a second `WITHIN GROUP` key Msg 10751 state 1 (all probed 2026-10-01 against SQL Server 2025).
  The fraction `p` is evaluated once per query (constant, variable, or parameter); NULL or a value outside `[0, 1]` → Msg 8727 at runtime.
  `DESC` reverses the sort.
- Aggregate windows: `SUM`/`AVG`/`COUNT`/`COUNT_BIG`/`MIN`/`MAX`/`STDEV*`/`VAR*`/`CHECKSUM_AGG`/`PRODUCT(expr) OVER ([PARTITION BY ...] [ORDER BY ...] [frame])`, and `APPROX_COUNT_DISTINCT(ALL expr)` — only with its `ALL` written (see [the approximate aggregates](#the-approximate-aggregates)).
  Default frame without ORDER BY = whole partition; with ORDER BY = `RANGE UNBOUNDED PRECEDING TO CURRENT ROW` (running total with peer-tie grouping).
- Explicit frame specs: `ROWS BETWEEN <start> AND <end>` and `RANGE BETWEEN <start> AND <end>`, plus the single-bound shorthand `ROWS <start>` ≡ `ROWS BETWEEN <start> AND CURRENT ROW`.
  `ROWS` accepts the full bound family: `UNBOUNDED PRECEDING`, `N PRECEDING`, `CURRENT ROW`, `N FOLLOWING`, `UNBOUNDED FOLLOWING`.
  `RANGE` rejects `N PRECEDING` / `N FOLLOWING` with Msg 4194 (matches real SQL Server's restriction).
  Frame extents are computed per row; empty extents (out-of-partition bounds, or post-clamp inversion) emit typed NULL for SUM/AVG/MIN/MAX/FIRST_VALUE/LAST_VALUE and 0 for COUNT family.
  Aggregate frames execute **incrementally**, not by re-aggregating each row's extent: both bounds advance monotonically as the row index rises, so a single aggregator slides through the sorted partition — `Add` as the end advances, `Remove` as the start advances — and each row's operand is evaluated exactly once.
  That makes a running total / sliding window O(n) per partition rather than O(n²).
  `Remove` is supported by SUM/AVG/COUNT/COUNT_BIG/STDEV*/VAR* (arithmetic / moment subtraction), CHECKSUM_AGG (XOR self-inverse), and MIN/MAX (a directional multiset built only when the frame start can advance — GROUP BY and forward-cumulative windows keep the cheaper single-extreme path).
  Frames whose start is pinned at `UNBOUNDED PRECEDING` (the default ORDER BY frame, and `ROWS/RANGE UNBOUNDED PRECEDING TO …`) never remove, so they're a pure forward accumulation valid for every aggregator.
  Frame rejection paths: ranking + LAG/LEAD with a frame → Msg 10752; frame without ORDER BY → Msg 10756; `BETWEEN ... FOLLOWING AND ... PRECEDING` → Msg 4193; `BETWEEN CURRENT ROW AND UNBOUNDED PRECEDING` / `BETWEEN UNBOUNDED FOLLOWING AND ...` → Msg 102 near the direction word; an offset that isn't an `int` literal (`1.5`, `3000000000`) → Msg 102 near it (probed 2026-10-01 against SQL Server 2025).
  An `OVER` with no `ORDER BY` for a function that needs one is Msg 4112 naming it, as is a frame after `PARTITION BY` for `FIRST_VALUE` / `LAST_VALUE`; Msg 10752's state is 3 for the ranking and distribution family and 1 for `LAG` / `LEAD` (probed 2026-09-24 against SQL Server 2025).
- **The star-count exemption from the frame-needs-ORDER-BY gate.** `COUNT(*)` and `COUNT_BIG(*)` — the two aggregates that carry no operand — may frame an unordered partition, and the frame applies (probe-confirmed against SQL Server 2025: `COUNT(*) OVER (PARTITION BY g ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)` climbs 1, 2, 3 through each partition).
  The exemption is the star operand's, not `COUNT`'s: `COUNT(1)` is a constant argument and raises Msg 10756 with `COUNT(v)` / `SUM(v)` / `MIN(v)`.
  `RANGE` takes the exemption too, but with no ordering every row is its own partition's peer, so the extent is the whole partition.
  A frame written as the `OVER` body's **only** element — no PARTITION BY and no ORDER BY — is Msg 102 near the frame keyword whatever the function, ahead of the ordering rule.
  A **named window** carries no exemption: real validates the resolved body before it knows which function reads it, so `COUNT(*) OVER w` against a frame-carrying, orderless `w` is Msg 5364 like every other function's.
  Divergence: with no ORDER BY the row order a `ROWS` frame counts along is unspecified, and real doesn't run it in the emitted order for every bound pair — a frame ending at `UNBOUNDED FOLLOWING` counts opposite to the rows it emits (probe-confirmed), which the simulator's straight partition-order walk doesn't reproduce.
  The `PRECEDING`-anchored bounds, which is what the shape is written for, match.
- Errors: `STRING_AGG OVER` → Msg 4113; `COUNT(DISTINCT) OVER` / `SUM(DISTINCT) OVER` → Msg 10759; windowed function in WHERE/HAVING/GROUP BY/ON, a `VALUES` row or an `UPDATE`'s `SET` list → Msg 4108; one inside an aggregate's argument, windowed or not (`SUM(ROW_NUMBER() OVER (…))`) → Msg 4109.

### RANGE-frame ORDER BY (Msg 8728)

A window whose frame is `RANGE` may not order by an expression of a MAX (LOB) type — `varchar(max)`, `nvarchar(max)`, `varbinary(max)` — and real answers **Msg 8728** class 16 state 1, "ORDER BY list of RANGE window frame cannot contain expressions of LOB type."
Probed against SQL Server 2025 (2026-08-05); the line it draws is narrow enough that the accepted neighbours are the more informative half:

| Shape | Real |
| --- | --- |
| `SUM(n) OVER (ORDER BY <max col>)` — the default frame an ORDER BY confers | Msg 8728 |
| `… RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW` written out | Msg 8728 |
| `… ROWS UNBOUNDED PRECEDING` — same ordering, other frame kind | accepted |
| `MAX` / `COUNT(*)` / `FIRST_VALUE` / `LAST_VALUE` / **`CUME_DIST`** over a max-typed ordering | Msg 8728 |
| `ROW_NUMBER` / `RANK` / `DENSE_RANK` / `NTILE` / `PERCENT_RANK` / `LAG` / `LEAD` over the same | accepted |
| `PARTITION BY <max col> ORDER BY <int col>` | accepted |
| statement-level `ORDER BY <max col>` | accepted |
| `ORDER BY <int col>, <max col>` — any position in the list | Msg 8728 |
| `ORDER BY <max col> + N'x'` — a computed expression of the type | Msg 8728 |
| `WINDOW w AS (ORDER BY <max col>)` | Msg 8728 |
| `ORDER BY CAST(<max col> AS nvarchar(50))` | accepted |
| over an empty rowset | Msg 8728 |

`CUME_DIST` is the surprise: it is the one ranking-shaped function that carries a RANGE frame, which is why `WindowExpression.HasRangeFrame` names it alongside the aggregate windows and the value pair.
`text` / `ntext` / `image` reach **Msg 306** and `xml` **Msg 305** on real ahead of this check, so the gate matches only the three MAX forms; a term whose static type this parse can't settle declines the check rather than reporting an error at a position that never had one.

Msg 8728 is also a member of the **transaction-aborting** error class — it rolls the session's whole transaction stack back and a `BEGIN TRY` can't catch it.
See [`transactions.md`](transactions.md#the-transaction-aborting-error-class) for the probed evidence and the neighbours that don't behave that way.

The sibling size rule is **Msg 8729** ("ORDER BY list of RANGE window frame has total size of N bytes. Largest size supported is 900 bytes."), which fires when the RANGE ORDER BY list's declared byte widths sum past 900 — `nvarchar(450)` (900 bytes) passes, `nvarchar(451)` (902) and `varchar(8000)` don't, two keys sum, and the LOB check wins when both apply.
It is transaction-aborting in the same way.
The width is the catalog width of each key's static type, and a key whose type this parse can't settle (`sql_variant`, a CLR type) leaves the check undecided rather than guessed (probed 2026-08-05 and 2026-09-24 against SQL Server 2025).

### Bounded per-partition `ROW_NUMBER()` selection

The greatest-n-per-group idiom writes the row number in a derived table and filters it above:

```sql
SELECT … FROM (SELECT …, ROW_NUMBER() OVER (PARTITION BY p ORDER BY s) AS rn FROM t) x WHERE rn = 1
```

Read literally that ranks every row of every partition and projects all of them for the filter to throw away — 73k rows and 663 partitions to answer 663.
`Selection.BoundRowNumberBodies` (`Selection.Execution.WindowTopN.cs`) hands the body the **inclusive row-number window the enclosing WHERE leaves surviving** instead, and the body then keeps each partition's top rows in a bounded max-heap (`TopRows<T>`) — the mechanism [Top-N selection](#top-n-selection) gives a statement's own `TOP (n)`, read against one partition rather than the whole result.
A row the bound can't reach is dropped as it is read: not cloned, not ranked, not projected.

**The bounding conjunct stays in the enclosing WHERE**, the residual invariant every narrowing pass rests on (see [`joins.md`](joins.md) and [`indexes.md`](indexes.md#the-scan-prefilter-a-join-source-no-key-can-seek)).
The body drops only rows whose row number falls outside the window that conjunct itself rejects, so the pass can only remove tuples the statement was going to discard — which is what makes it safe for every join kind, including a bounded body on an outer join's NULL-supplied side (a tuple NULL-extended because this side lost its rows reads UNKNOWN for the very conjunct that justified the bound).

**What binds.** The body's single window function has to be a bare `ROW_NUMBER()` **projection** — the one kind whose per-row value is settled one row at a time.
Every other kind's value is a property of the whole partition: a `SUM(x) OVER (PARTITION BY p)` beside the row number reads every row, and `RANK` / `DENSE_RANK` number ties alike, so no row count bounds how many rows carry a rank at or below *k*.
Otherwise the body has to be a plain SELECT-project-filter (no DISTINCT, no TOP / OFFSET / FETCH, no GROUP BY / HAVING / aggregate, no ORDER BY) — anything reading the row set as a whole would see a different one once the bound drops rows.
A join body qualifies, and so does a chain reaching the window through plain derived tables, since the ordinary predicate push carries the conjunct down to the level that reads it.

The bound itself is collected **per bounded column** rather than per conjunct, so `rn > 50000 AND rn <= 50050` combines; `rn = k`, `rn <= k`, `rn < k`, `rn BETWEEN a AND b`, `rn IN (…)` and either operand order all contribute, and the value side has to be row-independent (a literal, a variable, a parameter, arithmetic over those) and is evaluated once per execution.
Rounding is outward on both ends — `rn <= 2.5` keeps two rows, `rn >= 2.5` starts at three — so a fractional comparand can never tighten the window past what the residual comparison rejects.
A lower bound with no upper one declines: every row of the partition would still have to be ranked.

Past **4096** rows the heap stands down for the buffer [Top-N selection](#top-n-selection) falls back to, which selects the bound's window rather than sorting the partition, and projects only that window.
Measured 2026-09-30, `rn BETWEEN 100001 AND 100050` over 150k rows ordered by an unindexed `nvarchar`: 546 ms → 127 ms median (88 ms minimum), real 40 ms.

**Ties, and why the ranking sort is total.** A heap can only reproduce a full sort's answer against an order with no ties in it, so both paths order a partition by the window's ORDER BY keys **and then by the row's own arrival position**.
That settles which member of a tie group takes which number; a bare `List<int>.Sort` introsort would pick arbitrarily above its insertion-sort threshold and stably below it — not even consistent with itself across partition sizes.
Ranking ties is what real leaves plan-dependent, so pinning it costs no fidelity; only `ROW_NUMBER` takes the tiebreak, since `RANK` / `DENSE_RANK` number peers alike either way.
Measured cost on a 231k-row three-window query: none (445 ms against a 466 ms control, identical allocation).

Measured on WWI `Sales.Orders` (73k rows, 663 partitions), against the same query written so no bound reads it (`rn + 0 = 1`), interleaved in one process:

| shape | full sort | bounded | |
|---|---|---|---|
| `rn = 1` | 60.0 ms, 46.5 MB | **25.8 ms, 16.3 MB** | 2.3× |
| `rn <= 10` | 109.7 ms | **33.5 ms** | 3.3× |
| `rn BETWEEN 1 AND 50` (no partition) | 67.3 ms | **28.4 ms** | 2.4× |
| `rn BETWEEN 50001 AND 50050` (no partition, past the heap ceiling) | 64.9 ms | **38.0 ms** | 1.7× |

A bare `SELECT COUNT(*), MAX(OrderDate)` over the same table costs ~21 ms here, so the `rn = 1` shape sits about 5 ms above the scan it has to do anyway — and below the reference server's own time for it.

### Argument rules

Probed 2026-09-25 against SQL Server 2025, all while compiling (`WindowExpression.BindArguments`): a PARTITION BY or ORDER BY key must be comparable, reporting a query clause's own Msg 306 / 305 / 249 (the last naming `PARTITION BY` or `ORDER BY`); NTILE's bucket count must be an integer type other than `bit` (Msg 4116) and may read an outer query's columns but not its own level's (Msg 4195, checked at parse); PERCENTILE_CONT interpolates, so a non-numeric ordering key is Msg 402 naming the fraction's type and the key's; a LAG / LEAD default converts to the operand's type as an assignment does, and a literal default converts at once, so `LAG(v, 1, 'x')` over an int is Msg 245 even over no rows.
At run time a NULL offset makes every row NULL, default included, and an offset past the partition — a bigint one too — takes the default.
**Divergence**: real drops the whole plan of a contradiction like `WHERE 1 = 0` before the window runs, so a literal default that can't convert raises nothing there; the simulator doesn't model contradiction elimination and raises.

### Windows over a grouped query

A window sharing a SELECT with GROUP BY / aggregates runs over the query's **groups**, not its base rows — the reporting shape `SELECT cat, SUM(amt), RANK() OVER (ORDER BY SUM(amt) DESC) … GROUP BY cat`.
Semantics probed against SQL Server 2025:

- **The row set is the post-HAVING group stream.** `COUNT(*) OVER ()` returns the number of surviving groups (while a plain `COUNT(*)` still counts that group's base rows), and a group filtered out by HAVING contributes nothing to any window.
- **`AGG(AGG(x)) OVER (…)` is the aggregate-over-aggregate shape** — the inner aggregate is the group's value, the outer window spans groups, so `SUM(SUM(amt)) OVER ()` repeats the grand total on every row.
  Exactly one nesting level is legal: the bare `SUM(SUM(amt))` and the doubled `SUM(SUM(SUM(amt))) OVER ()` both stay **Msg 130** (see [Aggregate / GROUP BY binding rules](#aggregate--group-by-binding-rules)).
- **Window operands and PARTITION BY / ORDER BY expressions are group-level**, so they carry the same containment obligation as the select list: a grouping column or an aggregate binds, a bare non-grouped column is **Msg 8120** with the select-list wording — `SUM(amt) OVER ()` and `PARTITION BY region` both reject under `GROUP BY cat`.
  This is the one rule that makes identical window text legal in an ungrouped query and illegal in a grouped one.
- Frames apply over the group rows (`SUM(SUM(amt)) OVER (ORDER BY cat ROWS UNBOUNDED PRECEDING)` is a running total of group totals), and a window is legal in the grouped query's ORDER BY.

Implementation: `ComputeWindowResults` (`Selection.Execution.Window.cs`) is the shared window engine — it addresses rows only by index through a `WindowRowContext` accessor, so it is agnostic to whether a "row" is a joined base tuple or a group.
`BuildAggregateProjectionRows` materializes the post-HAVING groups, caches each group's aggregate results, and supplies an accessor that re-binds them per group — which is what lets a window operand's inner aggregate resolve to that group's value without any expression rewriting.

#### Windows over ROLLUP / CUBE / GROUPING SETS

`ROLLUP` / `CUBE` / `GROUPING SETS` emit one group stream per set, and a window spans the **concatenation** — the complete grouped result, subtotal and grand-total rows included.
`SUM(SUM(amt)) OVER ()` under `GROUP BY ROLLUP(region)` therefore adds the grand-total row's own total to the per-region totals (325 + 500 + 825 = 1650 over the three-row result), and `COUNT(*) OVER ()` counts every output row across every set.
Ranking treats it as one row set too, so two groups from *different* sets that carry the same ORDER BY value tie under `RANK` and take consecutive `ROW_NUMBER`s.

- **A subtotal row's grouped-away key reads as NULL, and `PARTITION BY` can't tell it from a data NULL.**
  Under `ROLLUP(region, product)` a `PARTITION BY product` partition for NULL holds the genuine NULL-product leaf row *and* every row where `product` was rolled away.
- **`GROUPING()` / `GROUPING_ID()` are legal inside `PARTITION BY` / `ORDER BY`**, and adding one to the partition key is the way to keep subtotal rows out of the data-NULL partition — each group's window keys are evaluated with that group's own grouping set published.
- HAVING still runs first (it filters across all sets), DISTINCT still dedupes the windowed projection afterwards, and TOP / OFFSET-FETCH still trim last.
- The binding rules are unchanged from the single-set path: bare non-grouped column in an operand or `PARTITION BY` → **Msg 8120**, a second nesting level under `OVER` → **Msg 130**, a window in HAVING → **Msg 4108**.

Implementation: the executor's per-set loop only *buffers* survivors — each tagged with the grouping set that produced it — and the single window pass runs after the loop over the concatenated buffer.
The per-group resolution scaffolding (grouped-key resolver, ORDER BY resolver, runtimes) is hoisted above the set loop and reads a mutable `currentGroupingSet` slot, so the window pass can re-point it per row; `RuntimeAtGroup` restores both that slot and `BatchContext.GroupingSetExpressions` from the survivor, which is what makes `GROUPING()` in a window clause read the row's own set rather than whichever set ran last.

- EF Core 10 reach: only `ROW_NUMBER` (via `Skip`/`Take`/`OrderBy + Take` per group) and aggregate-OVER (via grouped-projection patterns) are reached from LINQ.
  `EF.Functions` does NOT expose `Rank` / `DenseRank` / `CumeDist` / `PercentRank` / `Lag` / `Lead` / `NTile` / `FirstValue` / `LastValue` / `PercentileCont` / `PercentileDisc` — those are reachable only through raw SQL (`FromSqlInterpolated` / `SqlQuery`), so the simulator's expanded coverage helps applications that use raw SQL but doesn't intersect EF's LINQ→SQL translation surface.
