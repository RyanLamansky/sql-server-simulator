# Built-in scalar functions

## Math scalar functions
`ABS`, `ROUND` (2-/3-arg, half-away-from-zero + truncate mode), `FLOOR`, `CEILING`, `POWER`, `SQRT`, `SIGN`, `LOG` (1-/2-arg), `EXP`, `LOG10`, trig family (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`), `PI`, `DEGREES`/`RADIANS`, `SQUARE`. EF emits all from `Math.X` LINQ; `Math.Truncate(x)` → `ROUND(x, 0, 1)`; `Math.Atan2` → `ATN2`.

**Type-widening rule** (shared across `ABS`/`FLOOR`/`CEILING`/`ROUND`/`SIGN`/`POWER`'s first arg): `tinyint`/`smallint` → `int`; `smallmoney` → `money`; `real`/`bit` → `float` (sic — bit widens to float, not int); everything else preserves. `POWER` returns the post-widen type of the *first* arg regardless of exponent — `POWER(int, float) → int` with truncation toward zero. `SQRT`/`LOG`/`EXP`/`LOG10` always return float.

Errors: `SQRT(neg)` / `LOG(<= 0)` / `LOG10(<= 0)` / `LOG(x, 1)` / `POWER(neg, frac)` → Msg 3623. `POWER(0, neg)` → Msg 8134. `EXP` / `SQUARE` overflow → Msg 8115 float. `ABS(int.MinValue)` / `ABS(bigint.MinValue)` → Msg 8115 with the result type's family. `POWER` int-result overflow → Msg 232.

**Trig family** (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`/`PI`/`SQUARE`) always returns `float`. Domain errors → Msg 3623 (including `ATN2(0, 0)`, which diverges from .NET's `Math.Atan2(0, 0) = 0`). Wrong arg count → Msg 174 (`"The {lower-name} function requires {N} argument(s)."`) — `pi(1)` raises Msg 174 not Msg 102.

**`DEGREES`/`RADIANS`** are type-preserving with one tweak: `decimal(p, s)` widens to `decimal(38, max(s, 18))` rather than preserving. Integer arm truncates toward zero; out-of-range integer results raise Msg 8115 with the family name. Decimal arm uses a 28-digit `DecimalPi` constant in evaluation order `(input * 180m) / DecimalPi` for trailing-digit fidelity. .NET decimal's 28-digit precision cap means scale > 28 results land at scale 28.

## Date scalar functions: `DATEPART` / `DATEADD` / `DATEDIFF` / `DATEDIFF_BIG`
All take a bare datepart keyword. Result types: `DATEPART` → int; `DATEADD` preserves input type; `DATEDIFF` → int; `DATEDIFF_BIG` → bigint.

`DATEPART`/`DATEADD` enforce per-type keyword compatibility: `date` accepts only date parts; `time(N)` only time parts; `datetime`/`smalldatetime`/`datetime2(N)` accept both; `datetimeoffset(N)` adds `tzoffset`. Wrong combination → Msg 9810. `DATEADD` overflow → Msg 517. `DATEPART(weekday)` uses default `DATEFIRST 7` (Sunday=1); changing `DATEFIRST` not modeled.

`DATEDIFF`/`DATEDIFF_BIG` count `datepart`-unit *boundaries crossed*, not elapsed time — `datediff(year, '2023-12-31', '2024-01-01')` = 1. More permissive than DATEPART/DATEADD: every datepart works against every date/time-family combo. Only `tzoffset` and `iso_week` are rejected unconditionally → Msg 9806. String literals implicitly cast to `datetime2(7)`. `datetimeoffset` operands compare via UTC instant. Result-width overflow → Msg 535.

Unknown keyword → Msg 155 with the calling function's lowercase name embedded. NULL on any operand → typed NULL.

## Current-time scalars
Result types: `GETDATE`/`GETUTCDATE`/`CURRENT_TIMESTAMP` → `datetime`; `SYSDATETIME`/`SYSUTCDATETIME` → `datetime2(7)`; `SYSDATETIMEOFFSET` → `datetimeoffset(7)`. EF emits these from `DateTime.UtcNow`/`Now`/`DateTimeOffset.UtcNow` and `HasDefaultValueSql("getutcdate()")`.

**Per-statement freeze**: two `SYSDATETIME()` calls in one SELECT return identical values; an UPDATE that stamps every row writes the same value; successive SELECTs in one batch DO advance. Captured once per statement-loop iteration into `BatchContext.CurrentStatement.UtcNow`.

**UTC == Local** (Azure SQL Database default): no local-time conversion; all six functions return the same UTC instant (rounded per type — datetime variants quantize to 1/300s tick). `SYSDATETIMEOFFSET` reports `+00:00`. Apps depending on `GETDATE` ≠ `GETUTCDATE` differing by zone won't behave like on-prem; matches cloud default.

**`CURRENT_TIMESTAMP` is parens-less** — only zero-arg function in the grammar without `()`. Surfaces as `ReservedKeyword { Keyword: Keyword.Current_Timestamp }`, dispatched directly from `Expression.Parse`'s expression-start switch (NOT via `ResolveBuiltIn`). `CURRENT_TIMESTAMP()` with parens → Msg 102.

## Variadic string concat: `CONCAT` / `CONCAT_WS`
Both stringify each arg via CAST-to-varchar/nvarchar, **skip NULL args** (don't propagate), and **never return NULL** — all-NULL input → `''`. Result is `nvarchar` if any arg has a national-string type, else `varchar`. Arg-count rules → Msg 189: `CONCAT` requires 2-254 args; `CONCAT_WS` requires 3-254 (separator + ≥2 values).

`CONCAT_WS` quirks: NULL separator silently degrades to empty string (NOT NULL propagation despite docs); NULL values skipped entirely (no double separators); `concat_ws(sep, single_value)` → Msg 189 (refuses no-op stringify).

**EF doesn't emit `CONCAT` from `string.Concat`** — that translates to `[a] + N'-' + [b]` (the `+` operator, NULL-propagating). CONCAT/CONCAT_WS are reachable from raw SQL (`FromSqlInterpolated` / direct command).

## String `+` operator (concatenation)
**NULL-propagating** (matches default `CONCAT_NULL_YIELDS_NULL ON`; OFF setting not modeled). Result is `nvarchar` when either operand is national-string, else `varchar`. EF's dominant string-concat path. `text` / `ntext` / `image` / `varbinary` operands → Msg 402.

**Bare-NULL divergence**: simulator's untyped `NULL` literal carries `SqlType.Int32`, so `'a' + NULL` and `'a' + cast(NULL as int)` are indistinguishable at runtime. Both treated as string concat (returning NULL of the result string type); matches real SQL Server on bare NULL but diverges from `cast(NULL as int) + 'a'` (real raises Msg 245). Bare NULL dominates in practice; typed-null-int is a rare hand-written shape EF never emits.

**Result-type fidelity**: `char(N) + char(M)` → `char(N+M)` (capped at 8000); `nchar` analogous; mixed `char + nchar` → `nchar`. Variable-length pairs and mixed fixed/variable → length-bearing `varchar(N+M)` / `nvarchar(N+M)` (capped at 8000/4000). LOB and unspecified-length operands fall back to the unspecified form. `Subtract`/`Multiply`/etc. on string operands → `NotSupportedException` (real SQL Server: Msg 402 / Msg 8117).

## Date-construction scalars: `*FROMPARTS` family + `EOMONTH`
Six builders (`DATE`/`DATETIME`/`DATETIME2`/`DATETIMEOFFSET`/`SMALLDATETIME`/`TIME` + `FROMPARTS`). Shared shape: NULL on any non-precision arg propagates; non-int operands coerce through CAST; out-of-range → Msg 289 with type-specific State (1=date, 2=time, 3=datetime, 5=datetime2, 6=datetimeoffset). Variable-precision builders (`datetime2`/`datetimeoffset`/`time`) take the precision as a constant-foldable expression — column refs → Msg 10760; out-of-`[0, 7]` → Msg 1002.

Per-builder quirks: `DATETIMEFROMPARTS` ms 999 + h23:m59:s59 rolls to next day (1/300s tick rounding); `DATETIMEOFFSETFROMPARTS` enforces sign-consistency between hour/minute_offset (mixed → Msg 289 St 6) and |offset| ≤ 14:00. `EOMONTH(start_date [, month_offset])` always returns `date` and silently treats NULL `month_offset` as zero (NULL `start_date` propagates normally).

## `AT TIME ZONE`
Postfix operator; LHS-type-discriminated semantics:
- `datetime2`/`datetime`/`smalldatetime AT TIME ZONE 'X'`: treats LHS wall-clock as already in zone X, attaches X's offset. Skipped (spring-forward) wall-clocks shift forward by DST delta with post-transition offset; ambiguous (fall-back) picks daylight (pre-fall-back).
- `datetimeoffset AT TIME ZONE 'X'`: preserves UTC instant; both offset and wall-clock change.

Result is always `datetimeoffset` with LHS fractional precision preserved (`datetime2(N)`/`datetimeoffset(N)` → `datetimeoffset(N)`; legacy `datetime`/`smalldatetime` → `datetimeoffset(3)`). `date`/`time` LHS → Msg 8116. Unrecognized zone → Msg 9820. NULL on either side propagates.

Zone-name resolution via `TimeZoneInfo.FindSystemTimeZoneById` (accepts both Windows-style and IANA names cross-platform via ICU); cached in a process-static `ConcurrentDictionary`.

**Precedence**: `AT TIME ZONE` binds tighter than `+`. The zone-name slot parses as a primary expression only — literals, `@variables`, single-segment column refs, or parenthesized full expressions. Multi-part dotted refs and binary chains in the zone slot aren't modeled; wrap in parens. `AT`/`TIME`/`ZONE` are contextual keywords (still valid identifiers).
