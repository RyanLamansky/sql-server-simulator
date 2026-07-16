# Subqueries

`EXISTS`/`NOT EXISTS` (multi-column inner OK); `expr [NOT] IN (SELECT …)` (single inner column, Msg 116); scalar `(SELECT col FROM …)` (single column, single-row Msg 512 per outer row, empty → typed NULL); `expr <op> {ANY|SOME|ALL} (SELECT col …)` quantified comparison, all six operators + T-SQL synonyms (`!=` `!<` `!>`), predicate-only (SELECT-list use → Msg 102 at the operator); SOME aliases ANY.

Redundant parentheses around an `EXISTS` subquery are accepted at any depth — `EXISTS((SELECT …))`, `EXISTS(((SELECT …)))` (probe-confirmed legal on SQL Server 2025; DacFx emits the doubly-parenthesized form in its extended-properties reverse-engineering query). `ParseExists` (`BooleanExpression.cs`) counts the extra opening parens after the mandatory first one and demands a matching close-paren count; an unbalanced close raises Msg 102. The `IN ((SELECT …))` and scalar `((SELECT …))` forms already accepted the extra parens naturally — `(SELECT …)` is a parenthesized scalar-subquery expression that the generic primary-expression / IN-list parser wraps, so no special-casing was needed there.

Three-valued semantics: empty inner → ALL vacuously true / ANY vacuously false (independent of LHS NULL); a NULL on either side taints to UNKNOWN.

All forms correlate at arbitrary depth (via `outerResolver` / `outerTypeResolver` — see the Selection notes in the root CLAUDE.md architecture section).

Set ops (`UNION`/`UNION ALL`/`INTERSECT`/`EXCEPT`) are legal in every subquery context (via `Selection.Parse` → `ParseQueryExpression`), so EF Core 7+'s TPC shape (UNION ALL in a derived table) ships end-to-end.

Set-op semantics themselves (dedup rules, NULL-equality in set-op matching, precedence, ORDER BY placement) are covered in [`query.md`](query.md).
