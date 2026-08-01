# Query semantics — set ops, CASE, pagination, aggregates, windows

## Boolean / set ops / projection / CASE
- Boolean combinators (WHERE / MERGE-ON / CHECK): `AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `IS [NOT] DISTINCT FROM` (SQL Server 2022 NULL-safe equality — never UNKNOWN), `[NOT] IN (literal,...)`, `[NOT] IN (SELECT ...)`, `EXISTS (SELECT ...)`, `expr <op> {ANY|SOME|ALL} (SELECT col FROM ...)` quantified comparison, `value [NOT] BETWEEN lower AND upper`.
  Tri-valued except for the two `IS`-family forms (always definitive).
- `IS [NOT] DISTINCT FROM` (`BooleanExpression.DistinctFromExpression`): two NULLs are *not* distinct (match), exactly one NULL *is* distinct (no match), two non-NULLs distinct iff unequal under the regular promote-and-compare.
  Type-mismatch operand pairs still surface the underlying Msg 245 / Msg 402 — the NULL-safety lives in the per-side null check, the value-side coerces normally.
  Reachable in any boolean context (WHERE / HAVING / ON / CASE-WHEN / CHECK); bare SELECT-list use raises Msg 156 in real SQL Server because `IS` isn't a value operator.
- `[NOT] BETWEEN` desugars to `value >= lower AND value <= upper` (inclusive on both ends, probe-confirmed).
  Reversed bounds (low > high) collapse to a definite false; NULL in any operand position propagates through three-valued AND.
  `value` is evaluated once per row by `BetweenExpression.Run` (the desugaring is per-row semantics, not duplicated evaluation).
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

## `WINDOW w AS (…)` named-window clause (SQL Server 2022+)
- A trailing `WINDOW name AS (<over-body>) [, …]` clause (between HAVING and ORDER BY) defines named windows a bare `OVER w` resolves to.
- `OVER w` registers spec-less at parse (the definition follows the projection) and is patched once the WINDOW clause is read; an undefined name → **Msg 5362**.
- WINDOW is **contextual** (still a valid identifier / table alias) — recognized as the clause only in the `WINDOW <name> AS (` shape via lookahead.
- **Deferred**: the partial-inheritance form `OVER (w ORDER BY …)` (real SQL Server accepts it) is not modeled — it stays Msg 102, as does real's own rejection of empty `OVER (w)`.
  Named windows are resolved per top-level query block; a WINDOW clause nested in a subquery of the same statement is a known limitation of the shared parse-context list.

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

Rejected (probe-confirmed): a literal of any type (`'x'`, `1.5`, `1e0`, `0x01`, `NULL`), arithmetic or concatenation over literals (`1 + 0`, `'a' + 'b'`), `CAST` / `CONVERT` of one, `COALESCE` over literals, and any of those in parentheses.
Accepted: a variable inside an expression (`@v + 1`), a subquery (`(SELECT 1)`), a scalar UDF call, and every function reading server or session state (`GETDATE()`, `NEWID()`, `RAND()`, `DB_NAME()`, `@@SPID`, `@@VERSION`) — real evaluates rather than folds those.
`ISNULL(NULL, 1)` is accepted where `COALESCE(NULL, 1)` is rejected, matching real's own split (COALESCE desugars to a CASE the folder reaches; ISNULL stays a runtime call).

A **variable** term is real's variable-column-position shape and gets its own error, **Msg 1008** (`The SELECT item identified by the ORDER BY number N contains a variable …`), whenever the variable is reachable through pure conversions only — `@v`, `(@v)`, `((@v))`, `CAST(@v AS int)`.
A variable inside arithmetic is a sort expression and orders the rows: `@v + 1`, `-@v` and `(@v) + 0` all sort (probe-confirmed).

Detection is `Expression.IsWrittenConstant`, a conservative opt-in walk (default false) over the literal-bearing node types, so a shape it doesn't recognize sorts rather than raising.
**Divergences**: real additionally folds deterministic scalar calls over literals (`ABS(-1)`, `LEN('abc')`) and `CASE` / `IIF` over literal arms, which sort here; and where real reports every ORDER BY error it finds, the simulator raises the first, so `ORDER BY nosuch, 'x'` is Msg 408 (position 2) rather than real's leading Msg 207.
The window and ordered-aggregate clauses (`OVER (ORDER BY 'x')`, `WITHIN GROUP (ORDER BY 'x')`) have their own rejection on real — **Msg 5309** — which isn't modeled; those terms sort.

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

## Outer-scope correlation in the select list

A SELECT's FROM clause is bound **before** its select list, matching SQL Server's binder rather than the written order.
The reason is that a select-list subquery can reference the enclosing query (`SELECT (SELECT t.col) FROM t` returns one value per outer row on real), and a projection's type is resolved statically by `GetSqlType` — unlike a WHERE reference, which defers to `Run` and so never needed the scope at parse time.
That asymmetry is why correlation through WHERE always worked while the same reference in the select list raised Msg 207.

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

**Not modeled: an aggregate reading only the enclosing query's columns.**
`(SELECT MAX(t.col) FROM u)` inside a query over `t` binds to the *outer* query on real, collapsing it to one row.
The simulator binds it to the query it is written in, which would silently return one row per outer row, so `RejectAggregateOverOuterScope` raises `NotSupportedException` instead of answering.
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
