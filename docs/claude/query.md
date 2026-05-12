# Query semantics — set ops, CASE, pagination, aggregates, windows

## Boolean / set ops / projection / CASE
- Boolean combinators (WHERE / MERGE-ON / CHECK): `AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `[NOT] IN (literal,...)`. Tri-valued.
- Set ops (UNION / UNION ALL / INTERSECT / EXCEPT): standard precedence (INTERSECT > UNION/EXCEPT). **NULLs are equal during set-op dedup/matching** (opposite of `=`'s tri-state). Per-branch ORDER BY in non-final branch → Msg 156. Top-level ORDER BY references first-branch column names only.
- `SELECT *`: bare and qualified `<source>.*`. Multi-source `*` keeps duplicate names. Unbound `<qualifier>.*` → Msg 4104.
- CASE: searched + simple. UNKNOWN excludes (matches WHERE); simple-form `CASE NULL WHEN NULL` falls through. Result type from `SqlType.Promote` over THEN/ELSE. **Msg 8133** fires at parse when every result expression (every THEN body + the explicit ELSE if present; an absent ELSE counts as implicit bare NULL) is a bare `NULL` literal — `Expression.IsBareNullLiteral` unwraps `Parenthesized` so `(NULL)` still trips. A single typed branch (e.g. `CAST(NULL AS int)`) satisfies the rule. `IIF` enforces the same check on its two value arms (real SQL Server desugars IIF to CASE).
- `ISNULL` truncates fallback to first arg's type. `IIF` = sugar for searched CASE. `NULLIF(a, b)` = `CASE WHEN a = b THEN NULL ELSE a END`. EF emits `ISNULL` only for `??` with a CAST; bare `??` emits `COALESCE`. Neither IIF nor NULLIF is EF-emitted (LINQ ternary → CASE) — load-bearing for `FromSqlInterpolated`.

## Pagination (`OFFSET ... FETCH`)
- OFFSET requires ORDER BY (else Msg 102).
- FETCH alone (no preceding OFFSET) → **Msg 153**.
- Negative offset → **Msg 10742** (`"...a OFFSET clause may not be negative."` — verbatim "a OFFSET").
- Fetch ≤ 0 → **Msg 10744** (verbatim typo "greater then zero").
- TOP + OFFSET → **Msg 10741**.
- Counts resolve at parse time (constants, parameters, arithmetic).

## Aggregates
`COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT)` / `COUNT_BIG`, `SUM` / `AVG`, `MAX` / `MIN`, statistical (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`. `AVG(int)` truncates; `AVG(decimal(p,s))` widens to `decimal(38, max(s,6))`.

`STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY ...)` reorders concatenation per group (EF emits this from `GroupBy(...).Select(g => string.Join(sep, g.OrderBy(...)))`). NULL operand rows skip both ORDER BY input and output. Non-`STRING_AGG` aggregate with `WITHIN GROUP` → **Msg 10757**; ORDER BY ordinal in this context → **Msg 5308** (distinct from projection-level ORDER BY which accepts ordinals); `WITHIN` is contextual (not reserved). Cross-aggregate Msg 8711 isn't modeled (EF doesn't emit).

## Window functions
- `ROW_NUMBER() OVER([PARTITION BY ...] ORDER BY ...)` — bigint, ORDER BY required (else Msg 4112). EF wraps in derived-table subquery.
- Aggregate windows: `SUM`/`AVG`/`COUNT`/`COUNT_BIG`/`MIN`/`MAX`/`STDEV*`/`VAR*`/`CHECKSUM_AGG`/`APPROX_COUNT_DISTINCT(expr) OVER ([PARTITION BY ...])`. **Implicit-frame whole-partition only** (no ORDER BY in OVER for aggregates).
- `RANK`/`DENSE_RANK`/analytic family/explicit frames/aggregate-window ORDER BY → `NotSupportedException` (not silent Msg 102) so users get a diagnostic.
- Errors: `STRING_AGG OVER` → Msg 4113; `COUNT(DISTINCT) OVER` / `SUM(DISTINCT) OVER` → Msg 10759; windowed function in WHERE/HAVING/GROUP BY/ON → Msg 4108. Window + GROUP BY/HAVING in same SELECT → `NotSupportedException`.
