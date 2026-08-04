# Query semantics — set ops, CASE, pagination, aggregates, windows

## Boolean / set ops / projection / CASE
- Boolean combinators (WHERE / MERGE-ON / CHECK): `AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `IS [NOT] DISTINCT FROM` (SQL Server 2022 NULL-safe equality — never UNKNOWN), `[NOT] IN (literal,...)`, `[NOT] IN (SELECT ...)`, `EXISTS (SELECT ...)`, `expr <op> {ANY|SOME|ALL} (SELECT col FROM ...)` quantified comparison, `value [NOT] BETWEEN lower AND upper`.
  Tri-valued except for the two `IS`-family forms (always definitive).
- `IS [NOT] DISTINCT FROM` (`BooleanExpression.DistinctFromExpression`): two NULLs are *not* distinct (match), exactly one NULL *is* distinct (no match), two non-NULLs distinct iff unequal under the regular promote-and-compare.
  Type-mismatch operand pairs still surface the underlying Msg 245 / Msg 402 — the NULL-safety lives in the per-side null check, the value-side coerces normally.
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
  Per-branch ORDER BY in non-final branch → Msg 156.
  A branch may be **parenthesized**, and the parentheses may wrap a whole nested chain rather than a single SELECT — `SELECT … UNION (SELECT … UNION SELECT …)` and `… EXCEPT (… INTERSECT …)` are what an ORM emits when it combines an already-combined queryset (`ParseSetOpBranch`).
  Without it the opening paren read as a scalar subquery, so the branch looked like a one-column select list and the chain failed the equal-expression-count check.
  **Not accepted yet**: a parenthesized *leading* branch at statement start (`(SELECT …) UNION SELECT …`) still raises Msg 102 — that needs the statement dispatcher to route a leading `(` into the SELECT parser, not just the branch position.
  Top-level ORDER BY binds against the first branch — see [Top-level ORDER BY over a set operation](#top-level-order-by-over-a-set-operation).
- `SELECT *`: bare and qualified `<source>.*`.
  Multi-source `*` keeps duplicate names.
  Unbound `<qualifier>.*` → Msg 4104.
- **Table-value-constructor derived tables** (`Selection.ParseValuesDerivedTable`): `(VALUES (row), (row), …) alias(col, …)` as a FROM source, a JOIN source, or a `CROSS` / `OUTER APPLY` source.
  Rides the same deferred `FromSource.LateralPlan` seam as a derived-table SELECT (`Selection.ForValuesConstructor`), so a VALUES source **under APPLY correlates to the outer row** — the SSMS server-properties shape `… CROSS APPLY (VALUES (1001, 'host_platform', 0, host_platform), …) t(id, [name], internal_value, [value])`, whose rows mix literals with outer-column references.
  Per-column result types promote across every row's cell via `SqlType.Promote` (set-op / CASE joint-envelope rule): int + decimal → decimal, varchar + N'…' → nvarchar; the promoted type coerces each cell at runtime (so `(1),('abc')` promotes to int, then Msg 245 on the `'abc'` row).
  Both the **alias and its column-alias list are required**: no alias → Msg 102 near `)`; no column list → **Msg 8155**; more row columns than list names → **Msg 8158**, fewer → **Msg 8159** (shared factory with CTE / view rename lists); rows of differing arity → **Msg 10709**; empty row `()` → Msg 102.
  Untyped `NULL` cells carry the simulator's default `int` type (`SELECT NULL` quirk), so an all-`NULL` column is `int` (matches real) but a `NULL` + string column stays `int` and the string fails to coerce (diverges from real's untyped-NULL adoption — the bare-NULL-is-int limitation, not VALUES-specific).
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
  Result type from `SqlType.Promote` over the THEN / ELSE branches, **skipping bare untyped `NULL` literals** (`CaseExpression.CombineArmType`): SQL Server treats an untyped NULL as typeless in CASE result resolution — it yields to the typed branches, so `CASE WHEN … THEN 'x' ELSE NULL END` is nvarchar, not int (a bare-NULL placeholder `int` would otherwise promote the whole CASE to int and a string arm then failed to convert — surfaced by SMO's `CASE … THEN (SELECT name … COLLATE catalog_default) … ELSE NULL END`).
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

## `TOP n [PERCENT] [WITH TIES]`
- `TOP n` — the plain integer row cap; streams when no ORDER BY / DISTINCT, else applied after the buffered sort.
- `TOP n PERCENT` — the cap is `ceil(rowcount × n / 100)` (probe-confirmed against SQL Server 2025).
  The percent value must resolve to a numeric in `[0, 100]` — outside → **Msg 1031**, NULL → **Msg 1014**.
  PERCENT forces the buffered path (the total rowcount must be known).
- `TOP n WITH TIES` — after the cap, additionally emits every following row whose ORDER BY key equals the boundary row's.
  Requires an ORDER BY (else **Msg 1062**); also forces the buffered path.
- Both flags ride the existing TOP parse (`topPercent` / `topWithTies` on the projection build) and are honored by the buffered, windowed, and aggregate projection paths via `ComputeTopCap`.

### Top-N heap

A plain `TOP (n)` over an ORDER BY doesn't sort its buffer at all: `ProjectBuffered` feeds rows into a bounded max-heap of `n` entries (`TopNRowHeap` in `Selection.Execution.OrderBy.cs`) whose root is the worst row admitted, so once it is full a candidate is rejected on a single `CompareOrderKeys`.
That turns the cap's cost from O(rows log rows) into O(rows) plus a sift for the few rows that get in — and it is **the operator shape real picks too**: its plan for `SELECT TOP (10) … ORDER BY <unindexed>` over 228k rows is a Clustered Index Scan under a *TopN Sort*.

Eligible when there is an ORDER BY, the cap is a plain `TOP (n)` or `FETCH` resolving into `1 … 1024`, and nothing behind it needs the full ordered set: `PERCENT` and `WITH TIES` both read the total row count or the boundary row's neighbours, an `OFFSET` skips into the middle of the order, and `DISTINCT` has to dedupe the whole set before the cap means anything.
Each of those keeps the full sort, and past the 1024 ceiling the per-row sift stops being cheaper than sorting once.

**Ties at the boundary.**
A candidate tying the root is rejected, so among rows with equal keys the earliest-scanned survive — a stable pick, where the full-sort path's `List<T>.Sort` is an unstable introsort.
Real leaves it unspecified too (its TopN Sort carries no stability guarantee), so the two agree on every row whose key is strictly inside the window and may differ only on which members of a tie group *spanning* the boundary come back.
No existing test depended on the old pick.

Measured on `SELECT TOP (10) InvoiceLineID, UnitPrice, ExtendedPrice FROM Sales.InvoiceLines ORDER BY UnitPrice DESC, InvoiceLineID DESC` (228k rows, no index on the sort column): 338 ms → 122 ms median, 8.8× live → ~3.1×.
The residual is not the cap — a bare `SELECT COUNT(*)` over the same table already costs ~70 ms here — but the scan, and real's own plan shows why it wins: 41 ms elapsed against **280 ms of CPU**, at `DegreeOfParallelism="8"`.
Single-threaded, the simulator uses less CPU than real does for the same query; the gap is intra-query parallelism, which no top-N strategy reaches.

## `WINDOW w AS (…)` named-window clause (SQL Server 2022+)
- A trailing `WINDOW name AS (<over-body>) [, …]` clause (between HAVING and ORDER BY) defines named windows an `OVER w` reference resolves to.
- Every window kind reaches one — the ranking family (`ROW_NUMBER` / `RANK` / `DENSE_RANK` / `NTILE`), the distribution pair (`CUME_DIST` / `PERCENT_RANK`), the offset pair (`LAG` / `LEAD`), the value pair (`FIRST_VALUE` / `LAST_VALUE`), the ordered-set pair (`PERCENTILE_CONT` / `PERCENTILE_DISC`) and aggregate-OVER.
- The reference registers carrying only what it wrote inline (the definition follows the projection) and is patched once the WINDOW clause is read; an undefined name → **Msg 5362**.
- Window names are identifiers: they resolve under the database collation, so `OVER W` finds `WINDOW w AS (…)` under a case-insensitive one.
  One clause defining the same name twice → **Msg 16211**.
- References resolve from the statement's ORDER BY as well as its select list.
- WINDOW is **contextual** (still a valid identifier / table alias) — recognized as the clause only in the `WINDOW <name> AS (` shape via lookahead.
- Named windows are resolved per top-level query block; a WINDOW clause nested in a subquery of the same statement is a known limitation of the shared parse-context list.

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

**Not modeled yet**: `NEXT VALUE FOR seq OVER w` (real accepts a named window there; the simulator's OVER-after-NEXT-VALUE-FOR parse takes only the inline body) → Msg 102.

## `TABLESAMPLE`
- `TABLESAMPLE [SYSTEM] (n PERCENT | n ROWS) [REPEATABLE (seed)]` on a FROM source parses and is **discarded** — the query returns every row.
- SQL Server's sample is nondeterministic (any subset is a valid wire result), so returning all rows is a documented deterministic approximation; the win is accepting the syntax.

## ORDER BY term resolution

**Qualifier-awareness is the recurring rule across every resolver here.** A name matched on its leaf alone binds to the wrong column whenever a join brings a same-named one into scope — silently, with no error. It applies in four places, all now qualifier-aware: the plain ORDER BY resolver, the grouped ORDER BY resolver, the grouped-key resolver behind a grouped *projection* (`SELECT p.name` binding to a `b.name` grouping key), and the DISTINCT select-list check.

An **unqualified** term matches the select list first (output alias, then ordinal), falling back to a source column when it matches no output — SQL Server permits ordering by a non-selected source column.
A **qualified** term (`alias.col`) is a *source-column reference* and never matches an output alias: real orders `SELECT val AS id FROM ob t ORDER BY t.id` by `t`'s id column even though an output alias `id` exists (probe-confirmed).
Matching on the leaf alone silently sorted by the wrong column whenever a join brought a same-named column into scope — `ORDER BY child.id` bound to the projected `parent.id`, which is the shape an ORM emits when ordering by a related model's field.

`DISTINCT` keeps its own rule: the term must appear in the select list, and a miss is Msg 145 rather than a source fallback.
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

**Divergences** on this path: where real reports every ORDER BY error it finds, the simulator raises the first, so `ORDER BY nosuch, 'x'` is Msg 408 (position 2) rather than real's leading Msg 207; and a term whose *fold itself raises* reports Msg 408 here where real reports the folding error (`ORDER BY 1/0` → real Msg 8134, `ORDER BY CAST('a' AS int)` → real Msg 245, `ORDER BY POWER(CAST(2 AS int), 40)` → real Msg 232).

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

Deciding 5308 vs 5309 needs the folded *value*, so the gate evaluates the term at parse time; **a fold that raises leaves the term standing**, matching real (`OVER (ORDER BY 1/0)`, `OVER (ORDER BY CAST('a' AS int))` and `OVER (ORDER BY POWER(CAST(2 AS int), 40))` are all accepted — the opposite of the statement-level path, which reports the folding error).

**Divergences** on the window path:

- A term whose fold raises is accepted at parse time as real does, but the simulator then evaluates it per row and raises the folding error at execution (`OVER (ORDER BY 1/0)` → Msg 8134) where real returns rows.
- An `int`-typed NULL a `TRY_` conversion produced is Msg 5308 on real — its index test is a "not less than one" comparison, which NULL answers UNKNOWN — while the simulator reports 5309 for every NULL, matching real only for the written `NULL` and `CAST(NULL AS int)` spellings.
- Two cells land on the wrong side of the 5308 / 5309 split for type-modeling reasons unrelated to the gate: `NULLIF(1, 2)` is `tinyint` on real (small integer literals are typed by magnitude) but `int` here, and `JSON_PATH_EXISTS` returns `int` on real but `bit` here.
- `ROW_NUMBER() OVER w` — a named-window reference from a *non-aggregate* window function — isn't parsed at all (Msg 102); the aggregate form (`SUM(v) OVER w`) is, and carries the gate.

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
| Ordinal below 1 or past the column count | **Msg 108** |

A FROM-less branch contributes an **empty** scope, not an unknown one: `SELECT 2 AS X UNION ALL SELECT 1 ORDER BY X` is legal and `ORDER BY Y` is Msg 207.
A skip-mode placeholder source suppresses the whole pass — real defers such a statement's binding, so no name in scope can be a compile error there.

A *constant* term never reaches this table — the ORDER BY parser rejects it up front with Msg 408 (below), on this path and the single-SELECT one alike.

## Result drain / ORDER BY representation
The FROM-bearing SELECT projection paths — streaming, buffered (ORDER BY / DISTINCT), windowed, and aggregate — all yield already-projected `SqlValue[]` rows, so `SimulatedSqlResultSet` serves the reader and TDS cursors directly with no encode-then-re-decode round-trip (see the `SimulatedSqlResultSet` doc + [`data-reader.md`](data-reader.md)).
**ORDER BY on a single SELECT sorts those projected `SqlValue[]` rows** (`ProjectBuffered.materialized.Sort`), so ordered drains ride the same decoded-once fast path as unordered ones; the only ordered-vs-unordered cost is the inherent buffer + per-row key computation + `List.Sort`, not a decode round-trip, and peak retained memory is effectively unchanged (measured within 0.2% on a 150k-row wide drain).

The **top-level ORDER BY after a set-op chain** (`ApplyTopLevelOrderBy`) is the exception: the inner UNION / INTERSECT / EXCEPT chain yields byte[] rows natively (branch dedup / coercion re-encode), so that path keeps the byte[] form through the sort and lets the drain cursor decode once — eagerly decoding every column into `SqlValue[]` for the whole buffer measured slower and heavier (strings re-materialized and retained across the sort).
Sort keys decode only the ORDER BY columns off each row (`ComputeTopLevelOrderKeys`), not the full tuple.

## Aggregates
`COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT)` / `COUNT_BIG`, `SUM` / `AVG`, `MAX` / `MIN`, statistical (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`.
`AVG(int)` truncates; `AVG(decimal(p,s))` widens to `decimal(38, max(s,6))`.
`SUM` / `AVG` also widen `real` to `float` and `smallmoney` to `money`, where `MIN` / `MAX` keep the operand's type — see [`arithmetic.md`](arithmetic.md#the-approximate-family-float--real).

A filter written **above** a grouped body — and the key set of an equi-join to one — reaches *below* the grouping when it names a grouping column, so the aggregate runs over the groups the statement keeps rather than every group in the table: see [`joins.md`](joins.md#join-key-reduction-of-a-grouped-body).

### Streaming accumulation, and where an error surfaces

A query with **one grouping set** — no GROUP BY at all, or a plain GROUP BY — reads each input row exactly once and accumulates straight off the enumeration.
`ROLLUP` / `CUBE` / `GROUPING SETS` partition the same rows several ways, so they still buffer the WHERE-passing rows: the second set can't re-read a stream the first consumed.

That is also the shape real runs: its plan pipelines the Filter into the Stream / Hash Aggregate rather than materializing between them, so **an aggregate operand that raises on an early row preempts a WHERE that would have raised on a later one**.
Over a table whose first row zeroes the divisor and whose last row's text isn't numeric, `SELECT SUM(1 / a) FROM t WHERE CAST(s AS int) > 0` reports **Msg 8134** (divide by zero), not the conversion error — probe-confirmed against SQL Server 2025, and the one observable difference the streaming shape makes.

The scaffolding follows the hoisted-resolver pattern the row-level loops use: one resolver delegate and one `RuntimeContext` for the whole loop, one reused grouping-key scratch buffer (a stable copy is taken only on a group's first row), and a per-group representative row snapshotted once rather than a snapshot per input row.
The representative is what a projection reaching the column *underneath* a grouping expression resolves against (`SELECT MONTH(d), MIN(d) … GROUP BY MONTH(d)`), so it has to be a copy: the join driver rewrites its tuple in place per row.

Oracle: `GroupedAggregateStreamingTests`.

### Top-N over the grouped stream

A small `TOP (n)` / `FETCH` whose ORDER BY runs over the *groups* takes the same bounded heap the row-level projection does (see [Top-N heap](#top-n-heap) for the mechanism, the eligibility rules and the tie behaviour — they are the same rules, read against the grouped stream).
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

A star with no FROM to expand against is real's **Msg 263** ("Must specify table to select from.") where the simulator reports Msg 102 at the star — `SELECT *` and `SELECT 1, *` alike, and `SELECT COUNT(*), *` reaches the same Msg 102 through the routing above.
Real splits a *qualified* star off from that: `SELECT t.*` is Msg 107, the unmatched-column-prefix error, and an `EXISTS (SELECT *)` body is legal (its projection is discarded), so the three cases don't share one answer.

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

Covered shapes (all probe-confirmed): a FROM-less subquery projecting an outer column, a derived table (VALUES or SELECT, including a FROM-less or set-operation body) in a subquery's FROM, and APPLY both at the top level and nested inside a subquery.

**Not modeled: an aggregate reading only the enclosing query's columns, where there is no enclosing collector to move it to.**
`(SELECT MAX(t.col) FROM u)` inside a query over `t` binds to the *outer* query on real, collapsing it to one row.
Where the scope has a collector the simulator moves it across and matches real ([Aggregate ownership across scopes](#aggregate-ownership-across-scopes)); where it hasn't, binding the aggregate to the query it is written in would silently return one row per outer row, so `RehomeAggregatesOverOuterScope` raises `NotSupportedException` instead of answering.
An aggregate mixing inner and outer references, or reading no column (`COUNT(*)`, `MAX(1)`), is unaffected.

## Aggregate / GROUP BY binding rules

Four rules SQL Server binds at parse time; without them the simulator is over-permissive (an app query would work here and break on real).
Probe-confirmed; oracle `AggregateBindingRuleTests`.

- **Msg 130 Cls 15 St 1** — `"Cannot perform an aggregate function on an expression containing an aggregate or a subquery."`
  Fires when an aggregate's *own argument* contains another aggregate or a subquery at any depth: `SUM(MAX(a))`, `SUM(a + MAX(b))`, `MAX(CASE WHEN EXISTS(…) THEN a END)`, `MAX((SELECT 1))`, and the correlated form.
  A subquery elsewhere — HAVING, projection, WHERE — is untouched.
  A **windowed** aggregate over an aggregate is legal on real (`SUM(SUM(b)) OVER ()` returns a value); the simulator doesn't parse that shape at all yet, so it can't reach this check — see [`backlog.md`](backlog.md).
- **Msg 8117 Cls 16 St 1** — `"Operand data type NULL is invalid for {aggregate} operator."`
  A bare untyped `NULL` operand, for count / count_big / sum / avg / max / min / stdev / checksum_agg.
  A *typed* NULL is fine (`COUNT_BIG(CAST(NULL AS int))` → 0).
  `STRING_AGG(NULL, ',')` uses a different message (Msg 8116, the argument form) and isn't covered.
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

## DISTINCT over a grouped query

`DISTINCT` dedupes the **grouped** projection, before ORDER BY and before any row limit.
Grouping alone doesn't imply distinct output — the projection can be narrower than the grouping key, which is how an ORM's `.dates()` collapses one row per record to one row per distinct year (`SELECT DISTINCT YEAR(pubdate) … GROUP BY id, pubdate`).

## Aggregate ownership across scopes

An aggregate whose operand reads only an **enclosing** query's columns belongs to that query: real evaluates it there, which makes the enclosing query an aggregate query and collapses it to one row per group.
`RehomeAggregatesOverOuterScope` moves the same `AggregateExpression` instance into the enclosing scope's collector at parse time (`ParserContext.EnclosingAggregateCollector`), so the nested expression tree keeps referencing it and reads the value the owning query bound.
This is what makes an ORM's `GREATEST` / `LEAST` emission work — `(SELECT MAX(value) FROM (VALUES (AVG(b.rating)), (AVG(b.price))) AS _G(value))` over a joined, grouped outer query.
An aggregate mixing inner and outer references, or reading no column (`COUNT(*)`), is untouched; with no enclosing scope to move to, it still raises `NotSupportedException`.

A name that resolves in **no** scope isn't this case at all — it is real's **Msg 207**, at compile time, in a plain statement and at CREATE of a module alike (probe-confirmed: `HAVING MAX(nosuchcol) = 1` refuses a `CREATE VIEW` outright, while the genuinely-outer form creates and only misbehaves when run).
The enclosing type resolver settles which of the two it is — it raises Msg 207 itself once the scope chain runs out — and a FROM-clause placeholder suspends the question entirely, since the name could belong to the missing object.

Ownership is decided by walking the operand's column references, so **an expression wrapper that doesn't forward `VisitColumnReferences` hides them** — `CONVERT` didn't, while its `CAST` sibling did, which made `AVG(CONVERT(float, b.rating))` look reference-free and left the aggregate unbound.

## GROUP BY extensions

`FromClause.GroupingSets: List<Expression[]>` holds the flat grouping-set list — simple `GROUP BY a, b` parses as `[[a, b]]`, `GROUP BY ROLLUP(a, b)` as `[[a, b], [a], []]`, `GROUP BY CUBE(a, b)` as the 2^N power-set entries, `GROUP BY GROUPING SETS((a, b), (a), ())` verbatim.
Mixed forms (`GROUP BY a, ROLLUP(b, c)`) Cartesian-combine each top-level item's fragments at parse time.
The legacy `GROUP BY <cols> WITH ROLLUP` / `WITH CUBE` modifier is equivalent to `GROUP BY ROLLUP(<cols>)` / `CUBE(<cols>)` — after the column list parses to its single Cartesian set, `RollupExpansion` / `CubeExpansion` re-expand it in place (probe-confirmed the row output matches the function forms).
`FromClause.AllGroupingExpressions` is the union (first-seen order) used by GROUPING() validation.

The aggregate executor (`Selection.Execution.Aggregate.cs`) **buffers** WHERE-filtered rows once (snapshotting each tuple because `EnumerateJoinedRows` reuses a single shared array in-place) then iterates each grouping set, partitioning the buffer per set's columns and accumulating fresh aggregators per group.
The projection's column resolver returns typed NULL for columns that aren't in the current set but appear in another set's columns — that's the subtotal/total-row semantic.
Without GROUP BY the executor synthesizes a single empty grouping set `[[]]` and runs one implicit group; same code path covers `GROUPING SETS(())` and the bare **`GROUP BY ()`** form (the empty grouping set = grand total over all rows, one aggregate row).
`ParseGroupByItem` distinguishes `GROUP BY ()` (a `(` immediately followed by `)` → the empty fragment `[[]]`) from `GROUP BY (expr)` (a parenthesized grouping key) via a checkpoint peek.
TOP / OFFSET / FETCH apply to the concatenated stream across all grouping sets, and so does any window in the query — see [Windows over ROLLUP / CUBE / GROUPING SETS](#windows-over-rollup--cube--grouping-sets).

**GROUP BY a scalar expression** (e.g. `GROUP BY MONTH(d)`) projects and orders correctly: a column buried inside a grouping expression resolves against the group's first-seen **representative row** (`GroupState.Representative`) — within a non-empty group every grouping expression is constant, so any row yields the right value for a projection / HAVING / ORDER BY item that's functionally determined by the grouping (`SELECT MONTH(d) … GROUP BY MONTH(d)`, `MONTH(d) + 1`, etc.).
The representative fallback fires only for non-empty grouping sets, preserving the ROLLUP/CUBE grand-total NULL.

**GROUP BY containment (Msg 8120 select-list / 8121 HAVING / 8127 ORDER BY)** is enforced at parse time on the cached plan build (`Selection.Execution.cs` `ValidateGroupByReferences`), whenever the query is an aggregate query (any aggregate, GROUP BY, or HAVING present).
SQL Server is strict — no functional-dependency relaxation, so grouping by a table's PK does *not* license its other columns — and binds the rule before any row is read.
The check leans on `Expression.VisitColumnReferences` already excluding aggregate-internal columns (an `AggregateExpression` doesn't visit its operand), so it walks each SELECT / HAVING / ORDER BY expression's bare references, resolves each to a source column, and requires it to be a *bare* GROUP BY column; a correlated / outer reference (unresolved against the local sources) is skipped, and an ORDER BY reference matching an unqualified select-list alias is a validated projection, not a source-column violation.
The one deliberate conservative miss: a column appearing only *inside* a compound grouping expression (`GROUP BY a+1`, then a bare `SELECT a`) is left unflagged — distinguishing it from the valid `SELECT (a+1)*2` shape would need sub-expression structural matching, so it errs toward no false positive on the valid form.
Oracle: `GroupByContainmentTests`; Msg 130 / 8117 / 164 aggregate-validation rules remain over-permissive (see [`backlog.md`](backlog.md)).

**ORDER BY on a grouped query** sorts the full grouped stream (across all grouping sets) before TOP / OFFSET / FETCH, so `SELECT TOP (n) … GROUP BY … ORDER BY SUM(x) DESC` selects the correct rows in order.
ORDER BY items resolve a select-list **alias** first (`ORDER BY Total`), then through the grouped-key / representative-row resolver — so an aggregate (`ORDER BY SUM(x)`, whose `AggregateExpression` is collected and bound like any projection aggregate), a grouped column, or a grouping expression all sort correctly.
Parse-time type-checking is alias-aware to match.

`GROUPING(col)` / `GROUPING_ID(c1, ..., cN)` read the executor-published context off `BatchContext.GroupingSetExpressions` (current set's column list) and `BatchContext.AllGroupingExpressions` (union across query).
Returns `tinyint` 0/1 and `int` bitmap respectively; **leftmost arg of `GROUPING_ID` occupies the most-significant bit** (probe-confirmed against SQL Server 2025 — `GROUPING_ID(region, product)` with region grouped + product not grouped returns `2`, the inverse case returns `1`).
Argument must match a GROUP BY expression.
Arg not in any grouping set → Msg 8161; same Msg for GROUPING outside any GROUP BY context.
Two `Reference` operands match by leaf-name equality (qualifier-tolerant); any other pair matches by **structural equality** of the parenthesis-stripped parse tree (`Grouping.FindArg` strips redundant parens off both sides, then compares `DebugDisplay` renderings ordinal-ignore-case).
Probe-confirmed: `GROUPING(a+1)` / `GROUPING_ID(a+1, b)` with matching GROUP BY expressions return their 0/1 markers, `GROUPING((a+1))` (extra parens) still matches, while `GROUPING(1+a)` (operand order differs — no commutative normalization) and `GROUPING(a+2)` (value mismatch) both raise Msg 8161.

`STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY ...)` reorders concatenation per group (EF emits this from `GroupBy(...).Select(g => string.Join(sep, g.OrderBy(...)))`).
NULL operand rows skip both ORDER BY input and output.
The result type is the operand's string type.
**A bounded (non-MAX) operand whose concatenation exceeds 8000 bytes raises Msg 9829** (`"STRING_AGG aggregation result exceeded the limit of 8000 bytes. Use LOB types to avoid result truncation."`, probe-confirmed against SQL Server 2025) rather than truncating — the byte count uses UTF-16 width for `nvarchar` and the result collation's ANSI code page for `varchar`; a `varchar(max)` / `nvarchar(max)` operand streams unbounded and skips the check (and, retyped through the operand, rides PLP over the TDS wire).
Non-`STRING_AGG` aggregate with `WITHIN GROUP` → **Msg 10757**; ORDER BY ordinal in this context → **Msg 5308** (distinct from projection-level ORDER BY which accepts ordinals); `WITHIN` is contextual (not reserved).
Cross-aggregate Msg 8711 isn't modeled (EF doesn't emit).

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
  - `NTILE(N) OVER ([PARTITION BY ...] ORDER BY ...)` — int.
    Distributes the partition into `N` buckets; the first `count % N` buckets carry one extra row each.
    `N <= 0` at runtime → Msg 9819.
    The bucket-count expression is evaluated once per query against the first buffered row's resolver (constants and parameters work; column references would surface as resolver errors — real SQL Server rejects non-constant bucket counts at compile time, the simulator surfaces it as a runtime issue).
- Value functions (ORDER BY required, operand re-evaluated against another row's resolver):
  - `LAG(expr [, offset [, default]]) OVER (...)` — operand type.
    Offset defaults to 1; default expression is evaluated in the boundary row's resolver context when the offset crosses the partition boundary (and typed NULL when no default is given).
  - `LEAD(expr [, offset [, default]]) OVER (...)` — same shape, opposite direction.
  - `FIRST_VALUE(expr) OVER ([PARTITION BY ...] ORDER BY ... [frame])` — operand type.
    Returns the operand evaluated against the frame's first row (default frame `RANGE UNBOUNDED PRECEDING TO CURRENT ROW` → partition's leading row, broadcast).
  - `LAST_VALUE(expr) OVER ([PARTITION BY ...] ORDER BY ... [frame])` — operand type.
    Returns the operand evaluated against the frame's last row.
    The default frame is `RANGE UNBOUNDED PRECEDING TO CURRENT ROW` — under RANGE+CURRENT ROW the last row is the current row's last peer (by ORDER BY key), so `LAST_VALUE` over the default frame returns the current row's value (or peer-tie last).
    The intuitive "partition last" semantic requires `ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING`.
    Probe-confirmed against SQL Server 2025.
- Ordered-set analytic functions — `PERCENTILE_CONT(p)` / `PERCENTILE_DISC(p) WITHIN GROUP (ORDER BY sort [ASC|DESC]) OVER ([PARTITION BY ...])`.
  Modeled as `WindowKind.PercentileCont` / `PercentileDisc` on `WindowExpression`: the percentile fraction lands in `PercentileArg`, the single `WITHIN GROUP` sort key reuses the `OrderBy` field, and the per-partition result is broadcast to every row (no per-row frame).
  The `OVER` clause is **mandatory** (Msg 10753 when absent) and may carry only `PARTITION BY` — an `ORDER BY` inside `OVER` is rejected with Msg 10758 (the ordering must come from `WITHIN GROUP`).
  NULL sort keys are excluded from the computation; an all-NULL / empty partition yields NULL.
  `PERCENTILE_CONT` returns `float` and linearly interpolates at `rank = p·(n−1)` between the floor/ceil values; `PERCENTILE_DISC` returns the **sort expression's own type** and picks the smallest value whose CUME_DIST ≥ p (index `ceil(p·n) − 1`, clamped).
  The fraction `p` is evaluated once per query (constant, variable, or parameter); NULL or a value outside `[0, 1]` → Msg 8727 at runtime.
  `DESC` reverses the sort.
- Aggregate windows: `SUM`/`AVG`/`COUNT`/`COUNT_BIG`/`MIN`/`MAX`/`STDEV*`/`VAR*`/`CHECKSUM_AGG`/`APPROX_COUNT_DISTINCT(expr) OVER ([PARTITION BY ...] [ORDER BY ...] [frame])`.
  Default frame without ORDER BY = whole partition; with ORDER BY = `RANGE UNBOUNDED PRECEDING TO CURRENT ROW` (running total with peer-tie grouping).
- Explicit frame specs: `ROWS BETWEEN <start> AND <end>` and `RANGE BETWEEN <start> AND <end>`, plus the single-bound shorthand `ROWS <start>` ≡ `ROWS BETWEEN <start> AND CURRENT ROW`.
  `ROWS` accepts the full bound family: `UNBOUNDED PRECEDING`, `N PRECEDING`, `CURRENT ROW`, `N FOLLOWING`, `UNBOUNDED FOLLOWING`.
  `RANGE` rejects `N PRECEDING` / `N FOLLOWING` with Msg 4194 (matches real SQL Server's restriction).
  Frame extents are computed per row; empty extents (out-of-partition bounds, or post-clamp inversion) emit typed NULL for SUM/AVG/MIN/MAX/FIRST_VALUE/LAST_VALUE and 0 for COUNT family.
  Aggregate frames execute **incrementally**, not by re-aggregating each row's extent: both bounds advance monotonically as the row index rises, so a single aggregator slides through the sorted partition — `Add` as the end advances, `Remove` as the start advances — and each row's operand is evaluated exactly once.
  That makes a running total / sliding window O(n) per partition rather than O(n²).
  `Remove` is supported by SUM/AVG/COUNT/COUNT_BIG/STDEV*/VAR* (arithmetic / moment subtraction), CHECKSUM_AGG (XOR self-inverse), and MIN/MAX (a directional multiset built only when the frame start can advance — GROUP BY and forward-cumulative windows keep the cheaper single-extreme path).
  Frames whose start is pinned at `UNBOUNDED PRECEDING` (the default ORDER BY frame, and `ROWS/RANGE UNBOUNDED PRECEDING TO …`) never remove, so they're a pure forward accumulation valid for every aggregator.
  Frame rejection paths: ranking + LAG/LEAD with a frame → Msg 10752; frame without ORDER BY → Msg 10756; `BETWEEN ... FOLLOWING AND ... PRECEDING` → Msg 4193; `BETWEEN CURRENT ROW AND UNBOUNDED PRECEDING` / `BETWEEN UNBOUNDED FOLLOWING AND ...` → Msg 102 syntax.
- **The star-count exemption from the frame-needs-ORDER-BY gate.** `COUNT(*)` and `COUNT_BIG(*)` — the two aggregates that carry no operand — may frame an unordered partition, and the frame applies (probe-confirmed against SQL Server 2025: `COUNT(*) OVER (PARTITION BY g ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)` climbs 1, 2, 3 through each partition).
  The exemption is the star operand's, not `COUNT`'s: `COUNT(1)` is a constant argument and raises Msg 10756 with `COUNT(v)` / `SUM(v)` / `MIN(v)`.
  `RANGE` takes the exemption too, but with no ordering every row is its own partition's peer, so the extent is the whole partition.
  A frame written as the `OVER` body's **only** element — no PARTITION BY and no ORDER BY — is Msg 102 near the frame keyword whatever the function, ahead of the ordering rule.
  A **named window** carries no exemption: real validates the resolved body before it knows which function reads it, so `COUNT(*) OVER w` against a frame-carrying, orderless `w` is Msg 5364 like every other function's.
  Divergence: with no ORDER BY the row order a `ROWS` frame counts along is unspecified, and real doesn't run it in the emitted order for every bound pair — a frame ending at `UNBOUNDED FOLLOWING` counts opposite to the rows it emits (probe-confirmed), which the simulator's straight partition-order walk doesn't reproduce.
  The `PRECEDING`-anchored bounds, which is what the shape is written for, match.
- Errors: `STRING_AGG OVER` → Msg 4113; `COUNT(DISTINCT) OVER` / `SUM(DISTINCT) OVER` → Msg 10759; windowed function in WHERE/HAVING/GROUP BY/ON → Msg 4108.

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
