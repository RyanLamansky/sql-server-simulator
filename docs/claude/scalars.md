# Built-in scalar functions

## Math scalar functions
`ABS`, `ROUND` (2-/3-arg, half-away-from-zero + truncate mode), `FLOOR`, `CEILING`, `POWER`, `SQRT`, `SIGN`, `LOG` (1-/2-arg), `EXP`, `LOG10`, trig family (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`), `PI`, `DEGREES`/`RADIANS`, `SQUARE`.
EF emits all from `Math.X` LINQ; `Math.Truncate(x)` → `ROUND(x, 0, 1)`; `Math.Atan2` → `ATN2`.

**Type-widening rule** (shared across `ABS`/`FLOOR`/`CEILING`/`ROUND`/`SIGN`/`POWER`'s first arg): `tinyint`/`smallint` → `int`; `smallmoney` → `money`; `real`/`bit` → `float` (sic — bit widens to float, not int); everything else preserves.
`FLOOR`/`CEILING` add one specialization to that rule (`MathScalars.FloorCeilingResult`): an exact-numeric input keeps its precision but drops to **scale 0** (the result is integer-valued), so `CEILING(1.1)` → `numeric(2, 0)` value `2`, `CEILING(CAST(1 AS decimal(38,10)))` → `decimal(38, 0)` — probe-confirmed against SQL Server 2025; `money` stays `money`, `float` stays `float`, `int` stays `int`.
`POWER` returns the post-widen type of the *first* arg regardless of exponent — `POWER(int, float) → int` with truncation toward zero — but an exact-numeric base widens its precision to **38** while keeping its scale (`MathScalars.PowerResult`), so `POWER(2.0, 10)` → `numeric(38, 1)` value `1024` (the pre-fix `decimal(2, 1)` couldn't hold it), `POWER(CAST(2 AS decimal(5,3)), 10)` → `decimal(38, 3)`; `money` base stays `money`, `float`/`real` → `float`.
(The simulator has one decimal family — it reports `numeric` for a variant's `BaseType` even where real would say `decimal`; only precision/scale are matched, not the `numeric`-vs-`decimal` name.)
`SQRT`/`LOG`/`EXP`/`LOG10` always return float.

**Implicit string coercion** (full math family — `ABS`/`FLOOR`/`CEILING`/`SIGN`/`SQRT`/`DEGREES`/`RADIANS`/`POWER`/`ROUND`/`LOG`/`LOG10`/`EXP`/`SQUARE`/`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`): string operands route through `MathScalars.CoerceImplicit` → `CoerceTo(SqlType.Float)`.
Bad strings produce Msg 8114 ("Error converting data type varchar to float.") through the existing string-to-float parser.
Probe-confirmed against SQL Server 2025.
The widening rule treats string input as float for projection-schema parity.
Two per-function nuances: `POWER`'s result type follows the **first** arg's widen rule (so `POWER('2', 3) → float` but `POWER(2, '3') → int` with truncation toward zero); `ROUND`'s **value** arg coerces but the `length` / `function` args stay strict-int (Msg 8116 on string, matching real).

Errors: `SQRT(neg)` / `LOG(<= 0)` / `LOG10(<= 0)` / `LOG(x, 1)` / `POWER(neg, frac)` → Msg 3623.
`POWER(0, neg)` → Msg 8134.
`EXP` / `SQUARE` overflow → Msg 8115 float.
`ABS(int.MinValue)` / `ABS(bigint.MinValue)` → Msg 8115 with the result type's family.
`POWER` int-result overflow → Msg 232.

**Trig family** (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`/`PI`/`SQUARE`) always returns `float`.
Domain errors → Msg 3623 (including `ATN2(0, 0)`, which diverges from .NET's `Math.Atan2(0, 0) = 0`).
Wrong arg count → Msg 174 (`"The {lower-name} function requires {N} argument(s)."`) — `pi(1)` raises Msg 174 not Msg 102.

**`DEGREES`/`RADIANS`** are type-preserving with one tweak: `decimal(p, s)` widens to `decimal(38, max(s, 18))` rather than preserving.
Integer arm truncates toward zero; out-of-range integer results raise Msg 8115 with the family name.
Decimal arm uses a 28-digit `DecimalPi` constant in evaluation order `(input * 180m) / DecimalPi` for trailing-digit fidelity.
.NET decimal's 28-digit precision cap means scale > 28 results land at scale 28.

## Additional date scalars: `DATENAME` / `DATETRUNC` / `SWITCHOFFSET` / `TODATETIMEOFFSET` / `DATE_BUCKET` / `CURRENT_DATE`

- **`DATENAME(part, date)`** — sibling of `DATEPART` but returns the localized string for the matched part (`'January'` / `'Sunday'` / `'12'` / etc.) as a fixed **`nvarchar(30)`** regardless of the part (probe-confirmed against SQL Server 2025 — the earlier length-0 `nvarchar` container described as `nvarchar(4000)`).
  Reuses `DATEPART`'s keyword tables for part validation and per-type compatibility (same Msg 9810 rejection set).
  Localized names follow .NET's `CultureInfo.InvariantCulture` — month names in English, weekday names in English, numeric parts as base-10 strings.
- **`DATETRUNC(part, date)`** (`Parser/Expressions/DateTimeAdjustments.cs`) — floor to start of the named part.
  Supported parts: `year`/`quarter`/`month`/`week`/`day`/`hour`/`minute`/`second` plus the millisecond/microsecond/nanosecond family.
  Result preserves the input's type (`datetime` → `datetime`, `datetime2(N)` → `datetime2(N)`); `date` source rejects time-bearing parts via Msg 9810 (reused factory).
- **`SWITCHOFFSET(dto, offset)`** — adjust a `datetimeoffset`'s offset while preserving the UTC instant; both offset (numeric `±N` minutes or string `'±HH:MM'`) forms accepted.
  Result type = input precision preserved (`datetimeoffset(N)`).
- **`TODATETIMEOFFSET(dt, offset)`** — attach an offset to a `datetime` / `datetime2` value, treating the input wall-clock as already in the named zone.
  Result `datetimeoffset(N)` matching input precision.
- **`DATE_BUCKET(part, bucket_width, date [, origin])`** (`Parser/Expressions/DateBucket.cs`) — bucket-aligned floor.
  `origin` defaults to `1900-01-01` for date/datetime inputs and `1900-01-01 00:00:00` for time-bearing types; `bucket_width` must be positive.
  Returns the same type as `date`.
- **`CURRENT_DATE`** — parens-less, dispatched directly from `Expression.Parse`'s expression-start switch (same shape as `CURRENT_TIMESTAMP`).
  Returns `date`.
  Equivalent to `CAST(SYSDATETIME() AS DATE)` — uses the same per-statement freeze.

## Date scalar functions: `DATEPART` / `DATEADD` / `DATEDIFF` / `DATEDIFF_BIG`
All take a bare datepart keyword.
Result types: `DATEPART` → int; `DATEADD` preserves input type; `DATEDIFF` → int; `DATEDIFF_BIG` → bigint.

`DATEPART`/`DATEADD` enforce per-type keyword compatibility: `date` accepts only date parts; `time(N)` only time parts; `datetime`/`smalldatetime`/`datetime2(N)` accept both; `datetimeoffset(N)` adds `tzoffset`.
Wrong combination → Msg 9810.
`DATEADD`'s interval count is `bigint` (`DatePartKinds.CoerceCount` → `CoerceTo(BigInt)`) — real accepts an interval exceeding int32 (`DATEADD(second, 2147483648, …)` lands in 2092); only an interval that pushes the *result* past the target type's range raises **Msg 517** (the `Add`/`checked` narrowing re-wraps it).
`DATEPART(weekday)` uses default `DATEFIRST 7` (Sunday=1); changing `DATEFIRST` not modeled.

**Implicit operand coercion** (date argument, all three functions): string operands route through `DatePartKinds.CoerceDateArgumentImplicit` → `CoerceTo(datetime2(7))`; integer operands → `CoerceTo(datetime)` (days-since-1900-01-01).
`ParseDateTime2` also accepts a **bare time-of-day string** (`HH:mm[:ss[.fffffff]]`, anchored to 1900-01-01), so `DATEDIFF(second, '11:15:00', <time>)` / `DATEPART(microsecond, '11:15:00')` coerce like real (a Django DurationField/TimeField pattern) rather than raising Msg 241.
Probe-confirmed against SQL Server 2025: `DATEPART(year, 0) = 1900`, `DATEADD(day, 1, 0) = 1900-01-02`, `DATEDIFF(day, 0, '2024-01-31') = 45320`.
`DATEADD`'s offset (second) arg stays strict-int — string offsets raise Msg 9810 ("Argument data type varchar is invalid for argument 2 of dateadd function") just like real SQL Server.
Minor projection-schema quirk: real SQL Server reports `DATEADD(day, 1, '2024-01-15')` as `datetime`; the simulator reports it as `datetime2(7)` (the convention from `DATEDIFF`'s existing string path).

`DATEDIFF`/`DATEDIFF_BIG` count `datepart`-unit *boundaries crossed*, not elapsed time — `datediff(year, '2023-12-31', '2024-01-01')` = 1.
More permissive than DATEPART/DATEADD: every datepart works against every date/time-family combo.
Only `tzoffset` and `iso_week` are rejected unconditionally → Msg 9806.
`datetimeoffset` operands compare via UTC instant.
Result-width overflow → Msg 535.

Unknown keyword → Msg 155 with the calling function's lowercase name embedded.
NULL on any operand → typed NULL.

## Current-time scalars
Result types: `GETDATE`/`GETUTCDATE`/`CURRENT_TIMESTAMP` → `datetime`; `SYSDATETIME`/`SYSUTCDATETIME` → `datetime2(7)`; `SYSDATETIMEOFFSET` → `datetimeoffset(7)`.
EF emits these from `DateTime.UtcNow`/`Now`/`DateTimeOffset.UtcNow` and `HasDefaultValueSql("getutcdate()")`.

**Per-statement freeze**: two `SYSDATETIME()` calls in one SELECT return identical values; an UPDATE that stamps every row writes the same value; successive SELECTs in one batch DO advance.
Captured once per statement-loop iteration into `BatchContext.CurrentStatement.UtcNow`.
A view or inline-TVF body is inlined into the referencing statement and adopts *its* freeze — which batch a module body reads from is the seam described in [`programmable.md`](programmable.md#body-batches-and-the-per-statement-freeze).

**UTC == Local** (Azure SQL Database default): no local-time conversion; all six functions return the same UTC instant (rounded per type — datetime variants quantize to 1/300s tick).
`SYSDATETIMEOFFSET` reports `+00:00`.
Apps depending on `GETDATE` ≠ `GETUTCDATE` differing by zone won't behave like on-prem; matches cloud default.

**`CURRENT_TIMESTAMP` is parens-less** — only zero-arg function in the grammar without `()`.
Surfaces as `ReservedKeyword { Keyword: Keyword.Current_Timestamp }`, dispatched directly from `Expression.Parse`'s expression-start switch (NOT via `ResolveBuiltIn`).
`CURRENT_TIMESTAMP()` with parens → Msg 102.

## Variadic string concat: `CONCAT` / `CONCAT_WS`
Both stringify each arg via CAST-to-varchar/nvarchar, **skip NULL args** (don't propagate), and **never return NULL** — all-NULL input → `''`.
Result is `nvarchar` if any arg has a national-string type, else `varchar`; **any MAX-typed arg (`varchar(max)` / `nvarchar(max)` / `text` / `ntext`) widens the result to the MAX form** (`SqlType.NVarcharMax` / `SqlType.VarcharMax`), probe-confirmed against SQL Server 2025, so a concatenation past 32,767 chars streams as PLP over the wire instead of overflowing the bounded length prefix.
**Non-MAX result width** is the **sum of the per-argument widths** (`StringConcat.ArgumentWidth`), capped at 8000 (`varchar`) / 4000 (`nvarchar`) and floored at 1 — not the length-0 container (which described as `varchar(8000)`).
Each argument contributes its string-conversion maximum, probe-confirmed against SQL Server 2025: a string type its declared length; `bit` 1, `tinyint` 4, `smallint` 6, `int` 12, `bigint` 24, `real`/`float` 23, `money`/`smallmoney` 40, `decimal`/`numeric` 41 (fixed, precision-independent), `date`/`time`/`datetime`/`datetimeoffset`/`uniqueidentifier` 40; a **bare untyped `NULL` literal contributes 0** (a typed `CAST(NULL AS int)` contributes its type width — the distinction rides `Expression.IsBareNullLiteral`).
So `CONCAT('a',1,NULL,'b')` → `varchar(14)`, `CONCAT(N'a',1)` → `nvarchar(13)`, and `CONCAT_WS` adds one separator width between each value pair (`CONCAT_WS('-','a','b','c')` → `varchar(5)`).
Arg-count rules → Msg 189: `CONCAT` requires 2-254 args; `CONCAT_WS` requires 3-254 (separator + ≥2 values).

`CONCAT_WS` quirks: NULL separator silently degrades to empty string (NOT NULL propagation despite docs); NULL values skipped entirely (no double separators); `concat_ws(sep, single_value)` → Msg 189 (refuses no-op stringify).

**EF doesn't emit `CONCAT` from `string.Concat`** — that translates to `[a] + N'-' + [b]` (the `+` operator, NULL-propagating).
CONCAT/CONCAT_WS are reachable from raw SQL (`FromSqlInterpolated` / direct command).

## String `+` operator (concatenation)
**NULL-propagating** (matches default `CONCAT_NULL_YIELDS_NULL ON`; OFF setting not modeled).
Result is `nvarchar` when either operand is national-string, else `varchar`.
EF's dominant string-concat path.
`text` / `ntext` / `image` / `varbinary` operands → Msg 402.

**Bare-NULL divergence**: simulator's untyped `NULL` literal carries `SqlType.Int32`, so `'a' + NULL` and `'a' + cast(NULL as int)` are indistinguishable at runtime.
Both treated as string concat (returning NULL of the result string type); matches real SQL Server on bare NULL but diverges from `cast(NULL as int) + 'a'` (real raises Msg 245).
Bare NULL dominates in practice; typed-null-int is a rare hand-written shape EF never emits.

**Result-type fidelity**: `char(N) + char(M)` → `char(N+M)` (capped at 8000); `nchar` analogous; mixed `char + nchar` → `nchar`.
Variable-length pairs and mixed fixed/variable → length-bearing `varchar(N+M)` / `nvarchar(N+M)` (capped at 8000/4000).
LOB and unspecified-length operands fall back to the unspecified form.
A string operand paired with a **numeric** one in `+` `-` `*` `/` implicitly converts to that numeric type (SQL Server's low string-precedence rule — `decimal - '0.4'` → `10.10`, `'3' * float` → float, result carries the numeric partner's type; `TwoSidedExpression.IntegerArithmetic` + `SqlType.PromoteForArithmetic`). Two exceptions: `bit + string` → Msg 402/8117, and modulo (`%`) against a non-integer numeric → Msg 402 ("incompatible in the modulo operator") even though `+ - * /` coerce. A non-numeric string surfaces its target's conversion error (Msg 8114 / 245 / 235). String-vs-string / string-vs-non-numeric arithmetic → the unsupported-pair error.

`REGEXP_LIKE` is **not** modeled as a built-in: SQL Server 2025 ships it as a native *reserved predicate* (`WHERE REGEXP_LIKE(col, pattern)`), while `dbo.REGEXP_LIKE(...)` — the schema-qualified scalar form mssql-django emits for Django `__regex` lookups — exists on real only when mssql-django's regex **CLR assembly** is installed.
That CLR path now works (see [`clr-assemblies.md`](clr-assemblies.md)), so the schema-qualified form resolves once the assembly is registered; the bare native predicate remains unbuilt, and with it the compat-170 keyword reservation that makes real reject the unbracketed `dbo.REGEXP_LIKE(...)`.

## Date-construction scalars: `*FROMPARTS` family + `EOMONTH`
Six builders (`DATE`/`DATETIME`/`DATETIME2`/`DATETIMEOFFSET`/`SMALLDATETIME`/`TIME` + `FROMPARTS`).
Shared shape: NULL on any non-precision arg propagates; non-int operands coerce through CAST; out-of-range → Msg 289 with type-specific State (1=date, 2=time, 3=datetime, 5=datetime2, 6=datetimeoffset).
Variable-precision builders (`datetime2`/`datetimeoffset`/`time`) take the precision as a constant-foldable expression — column refs → Msg 10760; out-of-`[0, 7]` → Msg 1002.

Per-builder quirks: `DATETIMEFROMPARTS` ms 999 + h23:m59:s59 rolls to next day (1/300s tick rounding); `DATETIMEOFFSETFROMPARTS` enforces sign-consistency between hour/minute_offset (mixed → Msg 289 St 6) and |offset| ≤ 14:00.
`EOMONTH(start_date [, month_offset])` always returns `date` and silently treats NULL `month_offset` as zero (NULL `start_date` propagates normally).

## `AT TIME ZONE`
Postfix operator; LHS-type-discriminated semantics:
- `datetime2`/`datetime`/`smalldatetime AT TIME ZONE 'X'`: treats LHS wall-clock as already in zone X, attaches X's offset.
  Skipped (spring-forward) wall-clocks shift forward by DST delta with post-transition offset; ambiguous (fall-back) picks daylight (pre-fall-back).
- `datetimeoffset AT TIME ZONE 'X'`: preserves UTC instant; both offset and wall-clock change.

Result is always `datetimeoffset` with LHS fractional precision preserved (`datetime2(N)`/`datetimeoffset(N)` → `datetimeoffset(N)`; legacy `datetime`/`smalldatetime` → `datetimeoffset(3)`).
`date`/`time` LHS → Msg 8116.
Unrecognized zone → Msg 9820.
NULL on either side propagates.

Zone-name resolution via `TimeZoneInfo.FindSystemTimeZoneById` (accepts both Windows-style and IANA names cross-platform via ICU); cached in a process-static `ConcurrentDictionary`.

**Precedence**: `AT TIME ZONE` binds tighter than `+`.
The zone-name slot parses as a primary expression only — literals, `@variables`, single-segment column refs, or parenthesized full expressions.
Multi-part dotted refs and binary chains in the zone slot aren't modeled; wrap in parens.
`AT`/`TIME`/`ZONE` are contextual keywords (still valid identifiers).

## Char-code scalars: `ASCII` / `UNICODE` / `CHAR` / `NCHAR`
Basic one-arg conversions between a character and its code point.

- **`ASCII(input)`** returns `int`.
  Reads the first character of `input` and returns the first byte of its encoding in the argument collation's ANSI code page.
  NULL → NULL; empty string → NULL.
  Unicode input is encoded first, so `ASCII(N'€')` returns 128 (CP1252's `€`); unrepresentable Unicode (emoji etc.) returns 63 via the encoder's `'?'` replacement fallback.
  The code page is the argument's, not always CP1252 — `ASCII` of a `Turkish_CI_AS` column holding `Ğ` is 208 — and under a DBCS code page the result is the *lead* byte of a two-byte character (`こ` under `Japanese_XJIS_140_CI_AS` → 130).
  Non-string inputs implicitly stringify *before* the first-char read, so `ASCII(65)` is 54 (the byte for `'6'`, the first char of `"65"`), not 65.
**`REPLACE` and `CHARINDEX` compare under the collation their arguments resolve to** (`StringScalars.ComparisonFor`), so an explicit `COLLATE` on *any* argument decides the whole call: `REPLACE(name, 'r. r.', '' COLLATE …_CS_AS)` leaves a differently-cased match alone, which is how an ORM forces a case-sensitive replace on a case-insensitive database.
Case sensitivity is the whole of the approximation — the comparison stays culture-based rather than routing through the collation's own comparer, matching the surrounding string scalars.

- **`UNICODE(input)`** returns `int`.
  Same input-handling shape as `ASCII`, but reads the .NET `char` directly rather than encoding it, so it is code-page independent.
  Supplementary code points (above U+FFFF, e.g. `N'😀'`) return the high surrogate value (55357 for 😀) under the non-SC default collation — not the full Unicode code point.
  An SC-aware variant returning 128512 would need explicit collation modeling; matches the simulator's "default collation only" stance.
- **`CHAR(code)`** returns `char(1)` (not `varchar(1)` — probe-confirmed via `sql_variant_property(CHAR(65), 'basetype')`).
  NULL → NULL; out-of-range (negative / > 255) → NULL.
  Non-integer inputs truncate-to-int (`CHAR(65.7)` → `'A'`, `CHAR('65')` → `'A'`).
  `CHAR(0)` is a valid NUL character with `DATALENGTH = 1`, not NULL.
- **`NCHAR(code)`** returns `nchar(1)`.
  NULL / out-of-range (negative, > 65535) → NULL.
  Supplementary code points like `NCHAR(128512)` (😀) return NULL rather than emitting a surrogate pair — non-SC collation behavior.

## Basic string scalars: `LEN` / `LOWER` / `UPPER` / `LTRIM` / `RTRIM` / `REVERSE` / `LEFT` / `RIGHT` / `REPLACE` / `CHARINDEX`
**Implicit operand coercion** is shared across the family via `StringScalars.CoerceToVarchar` (mirrors the `MathScalars` pattern).
Non-string operands — integer, decimal, money, float/real, date-time, uniqueidentifier, varbinary/binary — implicit-cast to `varchar` in the active database's collation before the function runs.
Varbinary/binary route through `SqlValue.CoerceBinaryToStringWithStyle(target, 0)`: each byte reinterpret-through CP1252 (varchar) or UTF-16 LE (nvarchar).
`LEN(0x4142202020) = 2` because the trailing 0x20 bytes are CP1252 spaces and trim like ASCII spaces; `LEN(CAST(0x010203 AS binary(10))) = 10` because binary's zero-padding survives `TrimEnd(' ')`.
**Image stays rejected** (Msg 8116) — real SQL Server rejects too, and `IsCoerceableToVarchar` deliberately excludes the legacy LOB form.
Probe-confirmed against SQL Server 2025: `LOWER(12345) = '12345'`, `LEN(CAST('2024-01-15' AS DATE)) = 10`, `LOWER(CAST('2024-01-15 12:34:56' AS DATETIME)) = 'jan 15 2024 12:34pm'` (legacy datetime default format), `REPLACE(CAST('2024-01-15' AS DATE), '-', '/') = '2024/01/15'`.
Source families outside the coerce-able set (varbinary, xml, spatial, table types) raise Msg 8116 via `InvalidArgumentDataType`.
The projection-schema result type for `LEN` is always `int`; the other functions project as `varchar` for non-string sources and preserve the input string type otherwise.
`REPLACE` runs the coerce per argument with the matching argument index in the Msg 8116 wording.
`CHARINDEX`'s **haystack** (arg 2) coerces (`CHARINDEX('2', 12345) = 2`); the **needle** (arg 1) and **start** (arg 3) stay strict-int / strict-string respectively, matching real's Msg 8116 rejection.

## ANSI string-syntax alternatives: `||` / `TRIM([side] chars FROM x)` / 2-arg `LTRIM`/`RTRIM` / `GREATEST` / `LEAST`
Alternate / ANSI forms SQL Server 2025 accepts, each probed against the live reference.

- **`||` concatenation** (`Parser/Expressions/Concatenate.cs`, hooked in `Expression.ParseBinaryContinuation` as two adjacent `|` tokens).
  Distinct from `+`: `||` is *always* concatenation and implicitly converts a non-string operand to string (`'a' || 1` → `'a1'`, whereas `'a' + 1` raises Msg 245).
  Same precedence / left-associativity as `+` (`'a' || 'b' + 'c'` → `'abc'`).
  NULL yields NULL (default `CONCAT_NULL_YIELDS_NULL ON`); result is `nvarchar` when either operand is a national string, else `varchar`.
  Requires at least one string operand and both operands concat-compatible (numeric except `bit`, money, date/time, uniqueidentifier); two non-strings (`1 || 2`), a `binary`, or a `bit` raise **Msg 402** "incompatible in the concat operator".
- **ANSI `TRIM`** (`Parser/Expressions/Trim.cs`) parses `TRIM([ [LEADING|TRAILING|BOTH] chars FROM ] x)` alongside the legacy `TRIM(x)`.
  The trim characters form a *set*, not a substring: `TRIM('ab' FROM 'abxba')` → `'x'`.
  A side keyword makes `chars FROM` mandatory — `TRIM(LEADING FROM x)` → **Msg 156** near FROM.
  NULL `chars` or source yields NULL; an empty set removes nothing.
- **2-arg `LTRIM(x, chars)` / `RTRIM(x, chars)`** (SQL Server 2022+) strip any of the set's characters from the one side; NULL `chars` yields NULL.
  The 1-arg forms keep their space-only behavior.
- **`GREATEST` / `LEAST`** (`Parser/Expressions/GreatestLeast.cs`, `isLeast` flag) — horizontal max / min.
  All arguments promote to the single highest-precedence result type, NULLs are skipped, and the result is NULL only when every argument is NULL.
  The promotion is the CASE family's arm unification (`SqlType.PromoteBranches`), so an integer-literal argument sizes by its own digit count against a decimal sibling: `GREATEST(<decimal(9, 2) col>, 1)` stays `decimal(9, 2)` where `GREATEST(<decimal(9, 2) col>, 2147483647)` widens to `decimal(12, 2)`.
  Projection nullability follows the same family — see [`tds-endpoint.md`](tds-endpoint.md).
  `GREATEST(1.5, 2)` → `2` as `numeric`; `GREATEST('a','b',3)` → Msg 245 (the int-promoted set can't parse `'a'`), matching real.

## EF.Functions-driven string scalars: `PATINDEX` / `STUFF` / `QUOTENAME` / `REPLICATE` / `SPACE` / `FORMAT`
Bundle that fills out the raw-SQL string surface that EF's `FromSqlInterpolated` and `DefaultValueSql` workloads commonly reach.
None of these are exposed as `EF.Functions.X` LINQ extensions; coverage targets raw-SQL paths.

**Projected result widths** for the length-deriving members here (`STUFF` / `REPLICATE` / `SPACE`, plus `LEFT` / `RIGHT` / `SUBSTRING`) are const-folded from a literal count/length argument to match SQL Server's exact COLMETADATA width — see the *String / binary width algebra* section of [`arithmetic.md`](arithmetic.md).
The rules below describe the *runtime* value; the width bounds it.

- **`PATINDEX(pattern, subject)`** shares the LIKE wildcard compiler via `LikePatternBuilder` (single source of truth for `%`/`_`/`[...]`).
  Anchoring is decided by leading / trailing `%` in the pattern: a leading `%` strips the start anchor (find-anywhere); a trailing `%` strips the end anchor; without either, the pattern is anchored at both ends and only a full-subject match returns 1.
  Leading and trailing `%` characters are consumed by the anchoring decision and don't translate to `.*` in the regex body — that's what makes `PATINDEX('%abc%', 'xabcx')` return 2 (position of `abc`) rather than 1 (position of the empty `.*` prefix).
  Subject NULL raises Msg 8116 (asymmetric with NULL pattern, which silently returns NULL).
  Subject non-string raises Msg 8116; pattern non-string implicitly coerces to the subject's string family.
  The subject takes a `text` / `ntext` document but not an `image`, and the pattern refuses all three — see *Legacy LOB arguments* below.
  Result type is `int` for bounded subjects and `bigint` for `varchar(MAX)` / `nvarchar(MAX)` / legacy LOB family.
  No `ESCAPE` clause (Msg 156 at parse, falls out of the general grammar).
- **`STUFF(input, start, length, replacement)`** uses 1-based `start` ∈ `[1, len(input)]`; out-of-range start, `start > len(input)`, `start == len(input) + 1`, and negative `length` all silently return NULL.
  `length` is clamped to remaining when greater than `len(input) - start + 1`.
  NULL `replacement` deletes the range without inserting.
  Result type promotes input and replacement via the standard string-type promotion (nvarchar wins).
  Non-string `input` / `replacement` implicit-coerce to varchar via `StringScalars.CoerceToVarchar` (probe-confirmed: `STUFF(99, 2, 1, 99) = '999'`).
- **`QUOTENAME(name [, delim])`** returns `nvarchar(258)` **carrying the input argument's collation and coercibility** (like the `UPPER`/`LTRIM` family — the result is not pinned to the neutral `SqlType.NVarchar`).
  This matters when the database collation differs from `Collation.Baseline`: a plain literal carries the database collation while a sysname catalog column carries the baseline at Implicit rank, so `'text' + QUOTENAME(sys-catalog sysname)` (SMO's Object-Explorer Urn) resolves via the Implicit collation instead of raising the Msg 468 two-coercible-defaults conflict a Baseline/coercible-default result would trigger.
  Supported delimiter chars: `[`/`]`, `(`/`)`, `<`/`>`, `{`/`}`, `"`, `'`, `` ` ``. The pair is selected by either side (probe-verified: `QUOTENAME('a)b', '(')` doubles `)` inside the body). Multi-char delimiter argument picks the first char. NULL input, NULL delimiter, unsupported delimiter character, and input > 128 chars all return NULL.
  A non-string argument quotes its implicit `nvarchar` rendering (`QUOTENAME(123)` → `[123]`, a date → `[2024-01-15]`, a `varbinary` → the characters its bytes spell), so `image` — the one type with no implicit conversion to `nvarchar` — is the only rejection, and it raises **Msg 206** rather than Msg 8116.
- **`REPLICATE(input, count)`** preserves the input's string type.
  Result truncates to 8000 bytes for non-MAX `varchar`/`nvarchar`; `varchar(MAX)` / `nvarchar(MAX)` / legacy LOB input bypass the cap.
  MAX detection runs at `Run` time off the runtime input value's type: a MAX-declared column or CAST target decodes to a max-form (length -1) / LOB string type that survives `StringScalars.CoerceToVarchar`, so `Replicate.IsMaxForm` reads it directly — no parse-time resolver needed, which is what lets a FROM-source `varchar(MAX)` column bypass the cap (probe-confirmed: `DATALENGTH(REPLICATE(vmaxcol, 200))` = 20000, and the `nvarchar(MAX)` sibling = 40000, while bounded `varchar(20)` / `nvarchar(20)` columns and plain literals stay capped at 8000 — MAX-ness is a property of the declared type, per Microsoft's docs).
  Non-string `input` implicit-coerces to varchar via `StringScalars.CoerceToVarchar` (probe-confirmed: `REPLICATE(12345, 2) = '1234512345'`).
- **`SPACE(count)`** always returns `varchar` (never nvarchar), truncated to 8000 chars.
  NULL / negative count → NULL.
- **`FORMAT(value, format [, culture])`** returns `nvarchar`.
  Implementation routes through .NET's `IFormattable.ToString(format, culture)` on the underlying CLR value, matching SQL Server's CLR-passthrough shape.
  Accepted value types: numeric (integer / decimal / float / real / money / smallmoney) and date-time family (date / datetime / smalldatetime / datetime2 / datetimeoffset / time).
  Strings, bit, binary, uniqueidentifier, rowversion → Msg 8116 at runtime.
  NULL value → NULL; NULL format → Msg 8116 (probed: ordering doesn't matter — the format-NULL check fires first).
  Culture defaults to en-US; invalid culture name silently falls back to en-US.
  .NET `FormatException` (e.g. `decimal.ToString("D5")`) → NULL; unrecognized custom-format tokens that .NET passes through (e.g. `int.ToString("qq qq")`) are echoed verbatim.

## Integer arguments outside the parameter's range

An integer argument — a length, position, count, index, code point, object / database / index id, month offset, SRID — is narrowed to the type its parameter is declared as, and one that doesn't fit raises SQL Server's own conversion error rather than leaking .NET's narrowing exception.
`ScalarArguments.CoerceToInt` / `CoerceToSmallInt` is the single narrowing seam (`StringScalars.CoerceLengthArgument` delegates to it), and it routes an overflow through `SimulatedSqlException.TryConversionOverflow` — the same chooser CAST, column assignment and ALTER COLUMN use, so the **source** type picks the error family and the **target** picks the state:

| Argument's source type | Error |
| --- | --- |
| `bigint` / `numeric` | **Msg 8115** state 2, `Arithmetic overflow error converting expression to data type <target>.` |
| `int` narrowing to a `smallint` parameter | **Msg 220** state 1, `Arithmetic overflow error for data type smallint, value = 40000.` |
| `float` / `real` | **Msg 232**, `Arithmetic overflow error for type <target>, value = …` (state 3 for int, 2 for smallint) |
| `money` (int target) | **Msg 237**, `There is insufficient result space to convert a money value to int.` |
| `varchar` holding an out-of-range number | **Msg 248**, `The conversion of the varchar value '3000000000' overflowed an int column.` |

Nearly every parameter is declared `int`.
The `smallint` exceptions are `FILEGROUP_NAME`'s filegroup id, `INDEXKEY_PROPERTY`'s key ordinal (its object and index ids stay int), and the minute offset `SWITCHOFFSET` / `TODATETIMEOFFSET` take — each names `smallint` in its overflow.

Probe-confirmed 2026-07-31 across: the string scalars (SUBSTRING, CHARINDEX, STUFF, REPLICATE, SPACE, CHOOSE, CHAR, NCHAR, PARSENAME, STR, LEFT, RIGHT); the catalog-id scalars (COL_NAME, COLUMNPROPERTY, OBJECT_NAME, OBJECT_SCHEMA_NAME, OBJECT_DEFINITION, OBJECTPROPERTY, OBJECTPROPERTYEX, SCHEMA_NAME, DB_NAME, TYPE_NAME, FILE_NAME, FILEGROUP_NAME, USER_NAME, INDEX_COL, INDEXPROPERTY, INDEXKEY_PROPERTY, STATS_DATE, `fn_virtualfilestats`); the date scalars (EOMONTH, DATE_BUCKET, the `*FROMPARTS` family, SWITCHOFFSET, TODATETIMEOFFSET); CONVERT's style argument; the spatial index / SRID arguments (`STPointN`, `geometry::Point`, `SET @g.STSrid`); and `fn_varbintohexsubstring`'s offset and length.

Three sites answer differently, each probe-confirmed:

- **The bit-manipulation family never narrows at all** — see [Bit manipulation](#bit-manipulation-argument-rules) below.
- **A system procedure's parameter** reports **Msg 8114** state 5 naming both families (`Error converting data type numeric to int.` for a bare literal, `… bigint to int.` for a `CAST(… AS bigint)`) rather than the arithmetic-overflow family, via `ScalarArguments.CoerceProcedureParameter`.
  Confirmed for `sp_getapplock`'s `@LockTimeout` (int), `sp_columns_100`'s `@ODBCVer` (int), and `sp_datatype_info_100`'s `@data_type` (int) / `@ODBCVer` (**tinyint**, which its message names).
- **A cursor `FETCH ABSOLUTE` / `RELATIVE` offset *literal*** past int range is a grammar-level failure, **Msg 1080** class 15 (`The integer value 3000000000 is out of range.`); the same value through a *variable* is accepted and simply positions past the end.

## Gated argument slots

Four argument slots refuse a wrong type outright instead of converting it, reporting **Msg 8116** naming the offending family, the 1-based argument index and the lowercase function name (`Argument data type numeric is invalid for argument 1 of columnproperty function.`):

- `COLUMNPROPERTY` / `INDEXPROPERTY` / `INDEXKEY_PROPERTY`'s **first** argument (the object id).
- `CONVERT` / `TRY_CONVERT`'s **third** argument (the style).

Only the four signed / unsigned integer families pass — `int`, `bigint`, `smallint`, `tinyint`.
`bit`, the exact numerics, `float` / `real`, money, the string families and the date families all raise.
An in-family value past the parameter's range still takes the ordinary conversion surface instead (Msg 8115), so `CONVERT(varchar(30), GETDATE(), CAST(3000000000 AS bigint))` overflows where the bare literal `3000000000` — typed `numeric(10, 0)` per [`arithmetic.md`](arithmetic.md#integer-literals-past-ints-range-type-numericdigit_count-0) — is type-rejected.

The gate is a compile-time one on real, so it precedes the NULL short-circuit: a *typed* NULL raises (`COLUMNPROPERTY(CAST(NULL AS float), …)` → Msg 8116) where the bare `NULL` keyword returns NULL.
A decimal-family argument is spelled `numeric` or `decimal` following the same numeric-vs-decimal naming a projected column uses, read off the argument expression's `ResultReportsNumeric`.
`ScalarArguments.RequireIntegerArgument` is the shared implementation; probe-confirmed 2026-08-01 across all four slots.

The sibling id scalars carry **no** such gate — `OBJECT_NAME`, `COL_NAME`, `OBJECTPROPERTY`, `TYPE_NAME`, `SCHEMA_NAME`, `DB_NAME`, `FILE_NAME`, `FILEGROUP_NAME`, `INDEX_COL`, `STATS_DATE` and the string scalars' position arguments all convert, so a wrong-family or out-of-range argument reaches the ordinary Msg 8115 / 232 / 237 / 220 outcomes above.
The bit-manipulation family gates its own arguments under a separate rule — see [Bit manipulation](#bit-manipulation-argument-rules).

### Divergences

- **Argument evaluation order.**
  Real converts every argument before running the function body; the simulator converts each where it is used, so `INDEX_COL` / `INDEXKEY_PROPERTY` / `STATS_DATE` resolve the named object first and return NULL for a missing one where real reports the later argument's overflow.
- **A system procedure's Msg 8114 spells a decimal source `numeric`.**
  Real spells it `numeric` for a literal argument and `decimal` for a declared-`decimal` variable; the simulator's `SqlValue` carries no such name by the time it reaches the parameter boundary, so it takes the literal spelling — exact for every argument the EXEC literal grammar admits, and the same deferred column/variable-source-name boundary [`arithmetic.md`](arithmetic.md#numeric-vs-decimal-reported-type-name) records.

## Bit manipulation argument rules

`GET_BIT` / `SET_BIT` / `LEFT_SHIFT` / `RIGHT_SHIFT` take their position / distance / value argument as a **`bigint`** and never narrow it, so an out-of-`int`-range argument is an ordinary out-of-range outcome rather than a conversion overflow (probe-confirmed 2026-07-31):

- The argument must already be an integer type; decimal, float, money, string, binary and NULL all raise **Msg 8116** naming the type, argument index and function (`Argument data type decimal is invalid for argument 2 of get_bit function.`).
  `bit` is rejected everywhere except `SET_BIT`'s third argument, which alone admits it.
- A position outside `[0, bit-width - 1]` raises **Msg 9838** — `Parameter 2 in function 'get_bit' is out of range 0 to 31.` — at state **1** for `get_bit` and state **2** for `set_bit`.
  The width follows the *first* operand's type (0 to 7 for `tinyint`, 0 to 63 for `bigint`).
- `SET_BIT`'s third argument must be exactly 0 or 1; anything else, at any magnitude, raises **Msg 9839** (`Parameter 3 in function 'set_bit' must be 0 or 1.`).
- The shifts have **no range check**: a negative distance shifts the opposite way (`LEFT_SHIFT(255, -4)` = 15, `RIGHT_SHIFT(255, -4)` = 4080, and the `<<` / `>>` operators match), and any magnitude at or past the operand's bit width — including one beyond `int` range — zeroes the result whichever way it points.

Divergence: real distinguishes the bare `NULL` keyword (Msg 8116) from a NULL-valued *typed* argument (result NULL).
The simulator resolves both to the same placeholder-typed value, so every NULL argument takes the Msg 8116 path its first operand always has.

## Legacy LOB arguments

No string function accepts `text`, `ntext` or `image` as the operand it **transforms**: real raises **Msg 8116** naming the type, the 1-based argument position and the function, and so does the simulator.
Probe-confirmed 2026-07-31 across LEN / LEFT / RIGHT / UPPER / LOWER / LTRIM / RTRIM / TRIM / REVERSE / REPLACE / STUFF / REPLICATE / CHARINDEX / PATINDEX / ASCII / UNICODE / SOUNDEX / DIFFERENCE / TRANSLATE / STRING_ESCAPE / STRING_AGG / FORMAT.
Every argument position of a multi-argument member is covered — REPLACE and TRANSLATE reject one in all three of theirs, STUFF in its 1st and 4th, LTRIM / RTRIM in the 2nd (the character set), STRING_AGG in both.

Three members deviate, each probe-confirmed:

- **TRIM** numbers its arguments in written order, so the source is argument 1 in the bare `TRIM(x)` form and argument 2 once a `chars FROM` prefix claims the first slot.
  Written order also decides which of two offending arguments is named — `TRIM(<ntext> FROM <text>)` reports argument 1 — so the character set is gated ahead of the source rather than where it's consumed.
  Its function word is also the only capitalized one real emits — `of Trim function.` beside `of len function.`.
- **DIFFERENCE** takes a `text` argument: it converts implicitly to `varchar` and evaluates, while `ntext` and `image` raise.
  Its own SOUNDEX refuses all three.
- **QUOTENAME** quotes the `nvarchar` rendering of whatever it is given, so `text` / `ntext` and every non-string family quote normally; only `image`, which has no implicit conversion to `nvarchar`, fails — and with **Msg 206** (`Operand type clash: image is incompatible with nvarchar`) rather than the family's Msg 8116.

An argument that is **read** rather than transformed accepts a LOB, which is how a legacy `text` column is meant to be consumed — CHARINDEX's haystack, PATINDEX's subject and SUBSTRING's source all take one, as do CONCAT / CONCAT_WS / COALESCE / ISNULL and DATALENGTH.
`StringScalars.CoerceToVarchar`'s `allowLegacyLob` flag marks the coercing sites; `StringScalars.RejectLegacyLob` is the standalone gate for the members that read their argument directly.

The gate is a **compile-time** one, as it is on real: each member's `GetSqlType` resolves the argument's static type through `StringScalars.BindArgument` (or `BindCoercedArgument` for the members whose runtime path goes through `CoerceToVarchar`), which applies the identical `RejectLegacyLobType` / `RejectLegacyLobInCoercion` body the per-value gate calls — so the two phases can't drift, and an **empty** rowset, a never-taken branch, and a module body at CREATE all raise where real raises.
Probe-confirmed on SQL Server 2025: `SELECT LEN(nt) FROM t` fails on an empty table, `WHERE 1 = 0` doesn't rescue it, `WHERE LEN(nt) > 1` fails with the function nowhere in the projection, and `CREATE PROCEDURE` / `CREATE VIEW` / `CREATE FUNCTION` over such a body is refused.
Binding the argument is also what carries an unknown column's Msg 207 out of a predicate — see [`collations.md`](collations.md#compile-time-binding) for the drive sites.

The types are column-only besides: a local variable declared `text` / `ntext` / `image` raises **Msg 2739** (`The text, ntext, and image data types are invalid for local variables.`), so a string function only ever sees one through a column or a CAST.

### Divergences

- **`CHARINDEX(<needle>, <image>)`** reports Msg 8116 for argument 2 where real reports Msg 206 (`image is incompatible with varchar`); real accepts the pair when the needle is binary too, which needs the unbuilt binary CHARINDEX.
- **`SUBSTRING(<image>)` / `SUBSTRING(<varbinary>)`** raise Msg 8116 where real returns the binary slice — binary SUBSTRING isn't built.

## EF.Functions-driven type-check / random scalars: `ISNUMERIC` / `ISDATE` / `RAND`
- **`ISNUMERIC(expression)`** returns `int` (1 / 0); NULL → 0 (not NULL).
  Famously lossy on real SQL Server: a bare sign / decimal point / comma / currency symbol returns 1, hex prefixes return 0, internal whitespace breaks the match.
  The simulator's hand-rolled scanner consumes (in order: optional sign and currency in either order; digit / decimal / comma run; optional `e`/`E`/`d`/`D` exponent requiring a leading digit AND a trailing digit after optional sign).
  At least one of {digit, decimal/comma, sign, currency} must have been consumed for the result to be true.
  Bit-typed input returns 0 even though bit lives in the Integer category (probe-confirmed).
  Anything that doesn't fully consume after trimming whitespace returns 0.
- **`ISDATE(expression)`** returns `int` (1 / 0) and validates against the legacy `datetime` range (1753-9999).
  Empty string short-circuits to 0 (the shared `TryParseLegacyDateTime` treats `""` as datetime base-date for CAST support, but ISDATE specifically rejects).
  Modern `date` / `time` / `datetimeoffset` raise Msg 8116 — ISDATE intentionally lives in the legacy datetime domain.
  Integer input is implicitly stringified and re-parsed (so `ISDATE(20260512)` = 1 via `'20260512'` matching `yyyyMMdd`; `ISDATE(1)` = 0 because `'1'` parses to year 1 < 1753).
  Float / decimal / non-integer-non-string types always return 0.
- **`RAND([seed])`** returns `float`.
  The defining behavior is the **runtime-constant** rule: a given `RAND(...)` call site produces ONE value reused across every row of the query — distinct call sites in the same projection each get their own constant.
  The simulator implements this by caching the first-evaluation result on the `Rand` expression instance; a fresh parse (each batch / statement) gets a fresh cache.
  Seeded form: any numeric / string-convertible seed coerces to `float`; the int passed to `new Random(int)` is XOR-folded from the 64-bit double's bits so small integer seeds (`1` vs `999999`) don't collapse to the same hash (their mantissas live in the high bits which a naive int cast would discard).
  Determinism per seed is preserved but the values aren't byte-identical to SQL Server's undocumented seed algorithm.
  NULL seed → NULL output.

## SOUNDEX-family + STR + TRANSLATE + STRING_ESCAPE

- **`SOUNDEX(s)`** (`Parser/Expressions/SoundexStrAdditions.cs`) — returns a 4-character `varchar` SOUNDEX code under the standard algorithm (first letter uppercased, then the consonant-digit map B/F/P/V=1, C/G/J/K/Q/S/X/Z=2, D/T=3, L=4, M/N=5, R=6, vowels/H/W skipped, runs of identical-code letters collapsed, padded with `0` or truncated to length 4).
  Empty input → `'0000'`.
  NULL → NULL.
- **`DIFFERENCE(s1, s2)`** — counts matching positions (0–4) between the two strings' SOUNDEX codes.
  Result `int`; NULL on either side → NULL.
  Unlike SOUNDEX it accepts a `text` argument (implicitly converted to `varchar`) while refusing `ntext` / `image` — see *Legacy LOB arguments* above.
- **`STR(float [, length [, decimals]])`** — right-aligned fixed-width numeric-to-string.
  Defaults: length 10, decimals 0 (rounds half-away-from-zero, doesn't truncate).
  Overflow (formatted value exceeds `length`) returns a string of `*` characters of length `length`.
  NULL → NULL.
  Projects `varchar(length)` — the `length` argument (default 10) clamped to 1..8000 when it is a constant, else the `varchar(8000)` container (probe-confirmed against SQL Server 2025: `STR(3.14159, 6, 2)` → `varchar(6)`, a variable length → `varchar(8000)`); the earlier length-0 container described as `varchar(8000)` for every call.
- **`TRANSLATE(input, chars, translations)`** (`Parser/Expressions/StringScalarAdditions.cs`) — character-by-character substitution.
  The `chars` and `translations` arguments must have equal length; mismatch raises Msg 9819 via a dedicated `TranslateUnequalChars` factory.
  NULL on any operand → NULL.
  Result is the length family of `input`: a MAX-form input (`varchar(max)` / `nvarchar(max)` / `text` / `ntext`) projects `SqlType.NVarcharMax` so a large result streams as PLP; a bounded input keeps the length-0 `nvarchar` shape.
  (The simulator coerces every input to nvarchar before processing — a minor family divergence from real, which keeps the varchar family for varchar input.)
- **`STRING_ESCAPE(text, 'json')`** — JSON-string escape pass on `text` (escapes `"` `\` `\b` `\f` `\n` `\r` `\t`, `/`, control chars as `\uXXXX`).
  Documentation says only `'json'` is a valid mode; the simulator accepts any string for the mode and treats it as `'json'` (real SQL Server raises Msg 9806 on unknown mode — minor divergence).
  NULL `text` → NULL.
  Result `nvarchar(max)` (`SqlType.NVarcharMax`, probe-confirmed against SQL Server 2025) — escaping can more than double the input, so the result must stream as PLP.

## CHOOSE / IIF

- **`CHOOSE(index, val1, val2, ...)`** (`Parser/Expressions/Choose.cs`) — 1-based index into the trailing value list.
  Out-of-range (negative / zero / above the value count) → NULL.
  NULL `index` → NULL.
  Result type follows the standard CASE-style promotion across the value list (`SqlType.Promote` over all values).
  Sibling of `IIF`; both translate two-branch / multi-branch conditionals.

## Bit manipulation: `BIT_COUNT` / `GET_BIT` / `SET_BIT` / `LEFT_SHIFT` / `RIGHT_SHIFT`

Five integer scalars sharing one dispatch file (`Parser/Expressions/BitManipulation.cs`).
All operate on `tinyint` / `smallint` / `int` / `bigint` input (bit-width 8 / 16 / 32 / 64) and either preserve the input type or return a fixed scalar.
Bit positions are 0-based from the LSB.
Out-of-range bit position raises **Msg 8120** (`BitFunctionPositionOutOfRange` factory).
Non-integer input raises **Msg 8116** (`ArgumentDataTypeInvalidForBitFunction`).

- **`BIT_COUNT(num)`** — popcount, returns `int`.
- **`GET_BIT(num, index)`** — returns `bit` (0 / 1).
  NULL on either operand → NULL.
- **`SET_BIT(num, index [, value])`** — returns the input type with the bit at `index` set to `value` (defaults to 1).
  NULL on `num` / `index` → NULL.
- **`LEFT_SHIFT(num, n)`** — arithmetic left shift; high bits truncated when they overflow the input bit-width.
- **`RIGHT_SHIFT(num, n)`** — **logical** right shift (high bits zero-filled, probe-confirmed against SQL Server 2025 — diverges from C#'s `>>` on signed types which is arithmetic).
  Result type preserves input.

### `<<` / `>>` shift operators

The `<<` / `>>` binary operators desugar to the same `BitShift` class as `LEFT_SHIFT` / `RIGHT_SHIFT` — probe-confirmed that operator and function are byte-identical (`5 << 1` = `LEFT_SHIFT(5, 1)` = 10; `0x05 << 1` = `0x0A`; `5 << -1` = 2 reverses direction; binary operand → varbinary).
Tokenized as two adjacent `<` / `>` operators (the tokenizer emits single-char operators), the shift is recognized in `Expression.ParseBinaryContinuation` via a doubled-adjacent peek (`IsAdjacentDoubledOperator`), mirroring the `||`-concat detection — a lone `<` / `>` stays a comparison for the boolean layer.
**Precedence sits at the `+ - & | ^` level** (below `* / %`), left-associative: `2 * 3 << 1` = 12, `4 | 1 << 2` = 20, `5 << 1 + 1` = 11 (all probe-confirmed).
The shared `BitShift` class only accepts integer operands (binary-operand and negative-shift full fidelity remain a function gap — the corpus exercises integer positive shifts only).

## `HASHBYTES(algorithm, input)`

Cryptographic hash → `varbinary(8000)` (`Parser/Expressions/HashBytes.cs`), backed by the .NET BCL.
Probe-confirmed against SQL Server 2025:

- **Accepted algorithms (case-insensitive):** `MD5`, `MD4`, `SHA` / `SHA1` (identical output), `SHA2_256`, `SHA2_512`.
  The removed `MD2` and any unrecognized name yield a **NULL result** (not an error).
  `MD4` isn't in the BCL, so it's hand-rolled (RFC 1320); every other algorithm routes to the framework.
- **Input** must be a character or binary type: `char`/`varchar`/`text` encode through the collation's ANSI code page, `nchar`/`nvarchar`/`ntext` UTF-16LE, binary verbatim (so `HASHBYTES('SHA2_256','x')` == `HASHBYTES('SHA2_256',0x78)`).
  Probe-confirmed real: hashing a `Turkish_CI_AS` column holding `Ğğ` equals hashing `0xD0F0`, its CP1254 bytes.
  A non-character / non-binary argument (int, numeric, untyped `NULL` literal) raises **Msg 8116** (`InvalidArgumentDataType`, "…of hashbytes function"); a typed-but-NULL string / binary yields a NULL result.
- Divergence: the bare `NULL` literal is typed `int` in the simulator, so `HASHBYTES(alg, NULL)` reports the type word `int` where real reports `NULL` — both error, non-corpus edge.

### Unary `~` (bitwise NOT)

`~` (`Parser/Expressions/BitwiseNot.cs`) is the one's-complement prefix operator — tokenized as an `Operator` and parsed in `Expression.Parse`'s pre-primary switch alongside unary `+` / `-`.
Probe-confirmed against SQL Server 2025:

- **Result keeps the operand's exact integer type** — `~1` → `int` -2, `~CAST(0 AS tinyint)` → `tinyint` 255, `~CAST(5 AS smallint)` → `smallint` -6, `~CAST(5 AS bigint)` → `bigint` -6.
  `bit` flips: `~CAST(1 AS bit)` → 0, `~CAST(0 AS bit)` → 1.
  NULL propagates (typed).
- **Only integer-category operands** (`bit` / `tinyint` / `smallint` / `int` / `bigint`).
  Every other operand — decimal / numeric / float / money / string / binary — raises **Msg 8117** (`OperandDataTypeInvalid`, wording `Operand data type <type> is invalid for '~' operator.`) with **no coercion attempt** (even a numeric string like `~'5'` rejects).
  `GetSqlType` enforces the same rule so projection schema and runtime agree.
- **Highest-precedence operator** — binds tighter than `* / % + - & ^ |`, so `~2 * 3` = `(~2) * 3` = -9 and `~2 + 3 * 4` = `(~2) + (3*4)` = 9.
  Because the operand parse consumes the full following chain, `BitwiseNot.Create` re-homes the prefix onto the chain's leftmost leaf via `TwoSidedExpression.SinkUnaryPrefixToLeftmostLeaf` (the unary analogue of `AdjustForPrecedence`).

## CHECKSUM family

- **`CHECKSUM(args...)` / `BINARY_CHECKSUM(args...)`** (`Parser/Expressions/ChecksumAndRowVersion.cs`) — fast 32-bit fold over the argument list.
  Implementation uses FNV-1a; semantic guarantee matches SQL Server (same inputs → same checksum, deterministically).
  **Bit-pattern divergence**: real SQL Server uses an undocumented byte-mix; the simulator's FNV-1a output won't match real SQL Server bit-for-bit.
  Same-value-same-checksum invariant holds; same-multiset-same-checksum doesn't (CHECKSUM is order-sensitive, unlike CHECKSUM_AGG).
  Result `int`.
- **`CHECKSUM_AGG(expr)`** uses an order-independent XOR fold for the aggregate form — same multiset → same checksum, bit pattern won't match real SQL Server.
- **`APPROX_COUNT_DISTINCT(expr)`** is implemented as an exact `COUNT(DISTINCT expr)` — no HyperLogLog approximation, so results are exact rather than within real SQL Server's ~2% error bound.
- **`DATALENGTH(expr)`** returns `bigint` when the operand is `varchar(max)` / `nvarchar(max)` / `varbinary(max)`, else `int` (bounded strings, `xml`, `geography`/`geometry`, fixed-length types) — real's documented split, probe-confirmed against SQL Server 2025.
  Load-bearing for DacFx bacpac export, whose bulk reader emits `DATALENGTH([maxCol])` companions and validates their wire type.
  Spatial operands report the CLR-UDT serialization length (a 2D point = 22, probe-confirmed against WWI), not the simulator's stored WKT byte count — DacFx writes the companion value as the BCP length prefix for the wire bytes, so the two must measure the same form.
  The legacy LOB family (`text`/`ntext`/`image`) reports `int`, matching real — only the three MAX types widen.

## Gzip scalars: `COMPRESS` / `DECOMPRESS`

`COMPRESS(expr)` (`Parser/Expressions/Compress.cs`) gzip-deflates its argument and returns `varbinary(max)`; `DECOMPRESS(varbinary)` (`Decompress.cs`) inflates it back to `varbinary(max)`.
NULL in → NULL out on both.
Backed by `GZipStream` at the default compression level — real SQL Server doesn't expose the level either, so there's nothing to match.

**Input encoding** is the load-bearing detail: `COMPRESS` encodes `nchar`/`nvarchar`/`ntext` as UTF-16 LE and `char`/`varchar`/`text` through the collation's ANSI code page before compressing, matching the bytes real SQL Server compresses for those column types; binary types pass through, and anything else falls through to the value's UTF-16 string form.
`DECOMPRESS` returns raw inflated bytes, so callers cast to get text back — WWI's `Website.VehicleTemperatures` does `CAST(DECOMPRESS(…) AS nvarchar(1000))`, which is the shape DacFx-emitted views rely on.

**Divergence**: an invalid gzip stream raises **Msg 9803** in real SQL Server; the simulator catches the `InvalidDataException` and returns NULL instead.
DacFx-emitted views only call `DECOMPRESS` on known-compressed columns, so the bacpac path doesn't reach it.

## `FORMATMESSAGE`

**`FORMATMESSAGE(msg_number_or_string, [param, ...])`** (`Parser/Expressions/FormatMessage.cs`) — printf-style message renderer, `nvarchar` result truncated to **2047 characters** (probe-confirmed via `DATALENGTH`).
The formatter is a C-runtime `printf` subset (not `.NET string.Format`), distinct from the RAISERROR-path `Parser/MessageFormatter.cs` because FORMATMESSAGE's error handling differs (see below).

- **Specifier grammar** `%[flags][width][.precision][length]type`: flags `- + 0 # (space)`; width is a digit run or `*` (consumes an int argument); precision `.digits` (min digits for integers, max chars for `%s`); length `l`/`ll`/`h`/`hh` (ignored) or `I64` (bigint); type `s d i u o x X`.
  `%%` emits a literal `%`.
  All forms probe-confirmed: `%5d`→right-pad, `%-5d`→left, `%05d`→zero-pad, `%+d`→forced sign, `%#x`→`0x` prefix, `%.3d`→`005`, `%*d`→arg-driven width, `%u` of `-1`→`4294967295`, `%o`→octal, `%X`→uppercase hex.
- **Argument handling**: a NULL argument or a specifier with no argument renders the literal text `(null)`; extra arguments are ignored; a NULL format string → NULL.
- **Error handling** (the key divergence from RAISERROR, which throws): only a *consumed* argument whose type is fundamentally disallowed — anything but the integer family excluding `bit`, the string family, or the binary family — raises **Msg 2748** (`"Cannot specify <type> data type (parameter N) as a substitution parameter."`, probe-confirmed verbatim).
  Every other failure — a supported-but-mismatched argument (int into `%s`, string/binary into `%d`, bigint into a 32-bit specifier, int into `%I64d`), a malformed specifier, or an **empty format string** — does *not* throw; the whole result becomes SQL Server's terse in-server formatting-error diagnostic (`"Error: 50000, Severity: -1, State: 1. (Params:). The error is printed in terse mode…"` + trailing CRLF), captured byte-exact and returned as data.
- **`msg_id` overload not backed by `sys.messages`**: a numeric first argument (user id ≥50000 or any system id) returns **NULL**, matching real SQL Server's behavior for an *unknown* id.
  Known system-message text isn't modeled (no `sys.messages`).
- **Divergence**: a scale-0 `numeric`/`decimal` argument is a valid substitution type on real SQL Server (then fails formatting → terse diagnostic); the simulator raises Msg 2748 for any decimal/numeric regardless of scale.

## Password hashing: `PWDENCRYPT` / `PWDCOMPARE`

`Parser/Expressions/PasswordHashing.cs`.
The hash layout reproduces SQL Server's real on-disk format, so **simulator-generated hashes verify against a live server and vice versa** (both directions probe-confirmed against SQL Server 2025).
Layout: 2-byte big-endian version tag, 4-byte random salt, then the derived key.

- **`PWDENCRYPT(clear)`** — always emits the current SQL Server 2025 **`0x0300`** format: `0x0300 || salt(4) || PBKDF2-HMAC-SHA512(UTF-16LE(clear), salt, 100000 iterations, 64 bytes)` = **70 bytes** `varbinary` (probe-confirmed `DATALENGTH` 70).
  A fresh random salt per call, so successive hashes of the same password differ.
  NULL → NULL.
  A clear over **128 characters** raises **Msg 6607 Cls 16 St 5** `Password Encryption: The value supplied for parameter number 1 is invalid.` — probe-confirmed at exactly the 128/129 boundary, same shape for varchar and nvarchar input.
  (A `varchar(max)`-typed oversized input errs as Msg 8152 on real via parameter coercion — unmodeled; the simulator raises 6607 for every oversized input.)
  (Undocumented sibling of `PWDCOMPARE`.)
- **`PWDCOMPARE(clear, hash [, version])`** — `int` `1`/`0` for match/mismatch.
  Recognizes both `0x0300` (PBKDF2, current) and legacy `0x0200` (single-pass `SHA-512(UTF-16LE(clear) || salt)`, SQL Server 2012–2022) by reading the version tag, so it verifies hashes from any of those engines.
  NULL `clear` or NULL `hash` → NULL; a short / malformed / unrecognized-version hash → 0 (probe-confirmed, no error).
  A clear over 128 characters → 0 without hashing: real compares in full rather than truncating (probe-confirmed 0 for a 129-char clear against its own 128-char prefix's hash), and no genuine hash of one exists; real's Msg 8152 for much larger clears (an internal parameter-coercion boundary somewhere in 130–8000) is unmodeled — the simulator returns 0 there.
  The optional third `version` argument (legacy upgrade hint) is accepted and ignored — real SQL Server ignores it for comparison too.
  The 128-char bound is what lets the shared `PasswordHash` hashing paths stackalloc unconditionally.

## `LOGINPROPERTY`

**`LOGINPROPERTY(login_name, property_name)`** (`Parser/Expressions/LoginProperty.cs`) — resolves the single fixed login (`dbo`, the placeholder `SUSER_NAME` reports) plus any login registered via `CREATE LOGIN` (see [`permissions.md`](permissions.md)); any other name behaves like a nonexistent login and returns **NULL** for every property (probe-confirmed: nonexistent login → NULL across the board).
NULL login / NULL property / unrecognized property → NULL.
Property names case-insensitive.
Values are plausible constants matching the live probe's shape: `PasswordLastSetTime` → the login's actual password-set stamp for a registered login, a fixed seed date `2020-01-01 00:00:00.000` for `dbo`; `BadPasswordTime` / `LockoutTime` → the `1900-01-01` "never" sentinel; `BadPasswordCount` / `HistoryLength` / `IsExpired` / `IsLocked` / `IsMustChange` → `0`; `DaysUntilExpiration` / `PasswordHash` / `PasswordHashAlgorithm` → NULL (a low-privilege login sees NULL for the hash on the live server too, matching what the simulator exposes); `DefaultDatabase` → the session's current database; `DefaultLanguage` → `us_english`.
Like real SQL Server, the result is **`sql_variant`** carrying a per-property inner base type: `datetime` for the time properties (`PasswordLastSetTime` / `BadPasswordTime` / `LockoutTime`), `int` for the counters / `Is*` flags, `nvarchar` for the name properties (all probe-confirmed against SQL Server 2025).
`PasswordHash` is `varbinary` in real but always NULL here (no stored hash), so it surfaces as a NULL `sql_variant`.

## Legacy permission bitmap: `permissions()`

**`permissions([object_id [, 'column']])`** (`Parser/Expressions/PrincipalIdScalars.cs`, `Permissions`) — the deprecated statement/object/column permission bitmap.
Real SQL Server still evaluates it (SSMS's Table Designer issues a pre-open probe batch — `select user_name(), @@MAX_PRECISION, is_member('db_owner'), permissions(), DatabasePropertyEx(db_name(), N'collation'), SERVERPROPERTY('IsFullTextInstalled'), schema_name()` — whose failure on the missing built-in blocked the designer with "Unspecified error"), so **no deprecation warning is raised**.
The simulator's session principal is always the database-owning `dbo` (consistent with `HAS_PERMS_BY_NAME` always returning 1 and the current-principal placeholders resolving to `dbo`), so the returned masks are the fixed privileged (owner) defaults probed against SQL Server 2025 rather than a per-grant computation:

- **Niladic** → `50201342` — the statement-permission mask a `db_owner` (non-sysadmin) carries in a user database: low bits CREATE TABLE / PROCEDURE / VIEW / RULE / DEFAULT / FUNCTION + BACKUP DATABASE / LOG, each mirrored into the with-grant-option high half (`<<16`); the server-scope CREATE DATABASE bit (probed `+0x10001` in `master`) is absent.
- **`permissions(object_id)`** → `1948217375` (the owner mask for a user table/view) when the id resolves to an object in the current database (walks `Schema.SchemaObjects()` + `Schema.TableTypes`); NULL argument or an id resolving to no object → NULL.
- **`permissions(object_id, 'column')`** → `1082605703` when the id resolves to a table carrying the named column; NULL argument, an unresolved id, or an unknown column → NULL.

Result type `int`.
**Divergence**: because the simulator has no `EXECUTE AS` principal switching, the masks are the same regardless of grants/denies to other principals — dbo is always the owner.
The object mask is the user-table shape for every resolvable object type (EXECUTE-only procedure/function masks aren't distinguished).
See [`permissions.md`](permissions.md) for the GRANT/REVOKE/DENY model these defaults stand in for.

## Varbinary-to-hex system functions: `sys.fn_varbintohexsubstring` / `sys.fn_varbintohexstr`

**`sys.fn_varbintohexsubstring(@fFullLength bit, @value varbinary(max), @start int, @length int)`** and **`sys.fn_varbintohexstr(@value varbinary(max))`** (`Parser/Expressions/VarbinaryToHex.cs`) format a `varbinary` as a lowercase hex string → `nvarchar(max)`.
SMO scripts login SIDs and binary defaults through them (`SidHexString`, `fn_replp2pversiontotranid`).
These are **sys-schema-qualified system functions**, not unqualified built-ins: they resolve only as `sys.fn_…` (any database's sys schema, 2- or 3-part) — plus `master.dbo.fn_varbintohexstr` — and are wired into the multi-part call branch in `Expression.cs` (before user-function resolution), *not* into `ResolveBuiltIn`.
An unqualified `fn_varbintohexstr(…)` raises **Msg 195** (not a recognized built-in) and the current database's `dbo.fn_varbintohexstr` raises **Msg 4121** — both probe-confirmed, both preserved.

Probe-confirmed semantics (SQL Server 2025):
- Any NULL argument → NULL.
- `@fFullLength` non-zero prefixes the result with `0x`; zero omits the prefix (`sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 1, 0)` → `0x0123abcdef`; `@fFullLength = 0` → `0123abcdef`).
- `@start` is a 1-based **byte** offset; `@start < 1` or `@start >` the byte count → NULL (so start 1 on an empty `0x` → NULL).
- `@length <= 0` means "to the end"; a positive length clamps to the bytes remaining from `@start` (`(…, 2, 3)` → `0x23abcd`; `(…, 2, 99)` → `0x23abcdef`).
- Hex digits are lowercase (via `Convert.ToHexStringLower`).
- `fn_varbintohexstr(@value)` ≡ `fn_varbintohexsubstring(1, @value, 1, 0)`.

**Divergence**: the result carries the baseline collation (`SqlType.NVarcharMax`) rather than the database collation real SQL Server stamps — immaterial for the ASCII-only hex output and its standalone-projection use sites.

## Session / connection placeholders

Constants whose values don't carry real session/server identity in the simulator — they exist for SQL emitted by tooling that reads them (DACFx / EF Core / migration scripts) to receive a sensible non-NULL response.

- **`HOST_NAME()`** — the client workstation name the session reported: the connection string's `Workstation ID` / `WSID` keyword in-process, LOGIN7's `HostName` field over the TDS endpoint, `''` when neither supplied one (the common pool default on real SQL Server).
  The same value `sys.dm_exec_sessions.host_name` and `sp_who` / `sp_who2`'s `HostName` project.
- **`APP_NAME()`** — the client application name, from the connection string's `Application Name` / `App` keyword or LOGIN7's `AppName`, `''` when neither supplied one.
  The same value `sys.dm_exec_sessions.program_name` and `sp_who2`'s `ProgramName` project.
- **`ORIGINAL_DB_NAME()`** — returns `Simulation.DefaultDatabaseName` (`"simulated"`).
- **`GETANSINULL([db])`** — returns 1 (the simulator's ANSI-NULL behavior matches `SET ANSI_NULLS ON`, which is the only modeled mode).
- **`@@DATEFIRST`** — constant 7 (Sunday).
  `SET DATEFIRST` parses-and-discards.
- **`@@MAX_PRECISION`** — constant 38.
- **`@@MAX_CONNECTIONS`** — constant 32767.
- **`@@SERVERNAME`** — `"SIMULATED"`.
- **`@@SERVICENAME`** — `"MSSQLSERVER"`.
- **`@@LANGID`** — 0.
- **`@@LANGUAGE`** — `"us_english"`.
- **`@@TEXTSIZE`** — session state (`SimulatedDbConnection.TextSize`), default -1 (unlimited — the value a fresh SqlClient login establishes, probe-confirmed).
  `SET TEXTSIZE` carries semantic effect: the byte cap clips MAX-typed / legacy-LOB values at the client boundary (result columns via the `TextSizeCursor` decorator installed by `SimulatedQueryResult.CreateClientCursor`, output parameters at write-back), never server-side computation, variable assignment, or stored data; `varchar(max)`/`text` truncate at 1 byte per char, `nvarchar(max)`/`ntext` at 2 with an odd byte floored, `varbinary(max)`/`image` at raw bytes, while `xml`, bounded var types, and UDTs are exempt.
  Value mapping: `-1` preserved verbatim, `0` and every other negative collapse to 4096; a past-int-range literal raises **Msg 1080**; issued inside a proc body it reverts at proc exit while the body's result sets keep their production-time cap (`ClientTextSize` stamped per statement).
  All probe-confirmed against SQL Server 2025.
- **`@@OPTIONS`** — 5432 (composite of ANSI/ARITHABORT/QUOTED_IDENTIFIER/CONCAT_NULL_YIELDS_NULL flags matching the simulator's defaults).
- **`@@VERSION`** — a multi-line banner mirroring the real SQL Server 2025 `@@VERSION` shape (`Microsoft SQL Server 2025 (RTM-CU7) (KB5096981) - 17.0.4065.4 (X64)` / build-date line / copyright / edition line), with the simulator's own identity (`Developer Edition (64-bit) on SQL Server Simulator`) standing in for real's host-OS line.
- **`@@MICROSOFTVERSION`** — int `285216737` (`0x11000FE1`), the `(major << 24) | (minor << 16) | build` packing of version `17.0.4065.4` (`(17 << 24) | 4065`), self-consistent with `SERVERPROPERTY('ProductVersion')` = `"17.0.4065.4"` and the real reference instance's value.
  **Version-identity decision**: every version-bearing surface (`SERVERPROPERTY` family, `@@VERSION`, `@@MICROSOFTVERSION`, `xp_msver`, the TDS LOGINACK/prelogin build, `SimulatedDbConnection.ServerVersion`) reports the SQL Server 2025 reference build **17.0.4065.4** (RTM-CU7 / KB5096981) the simulator emulates; the single source of truth is the internal static `ReferenceBuild` class (root of `src/SqlServerSimulator/`), which holds the `Version` plus the non-derivable CU / KB / banner / xp_msver-FileVersion constants — bump there on a reference refresh, and the graduated tests' pinned literals catch any typo.
  The prior build-0 identity was changed because SSMS's Activity Monitor and report viewer gate on a per-build client feature check that stops immediately on build 0.
- **`@@REMSERVER`** — NULL (deprecated in SQL Server proper too).

Server-instance metadata accessed via **`SERVERPROPERTY(name)`** — see [`catalog-views.md`](catalog-views.md).

## System statistical counters + `sys.fn_virtualfilestats`

DBA-introspection surface for cumulative server activity.
Every `@@`-counter is **`int`** (probe-confirmed against SQL Server 2025); real reports live totals since server start, but the in-process simulator performs no physical IO, CPU-time accounting, or TDS-packet counting, so the elapsed-activity totals report a plausible **0** (the honest reading for a freshly started, idle instance).
Rarely read from application code; the constants exist so DBA tooling / health scripts receive a sensible non-error response.

- **`@@CPU_BUSY`** / **`@@IDLE`** / **`@@IO_BUSY`** — 0 (no CPU-time / idle-time / IO-time accounting).
- **`@@PACK_RECEIVED`** / **`@@PACK_SENT`** — 0 (no TDS packet counting, even on the network endpoint).
- **`@@PACKET_ERRORS`** / **`@@TOTAL_ERRORS`** — 0 (also 0 on a healthy real server).
- **`@@TOTAL_READ`** / **`@@TOTAL_WRITE`** — 0 (no physical disk-read / -write accounting).
- **`@@TIMETICKS`** — **31250**, the hardware-invariant microseconds-per-tick constant real reports (Value-constant form).
- **`@@CONNECTIONS`** — live signal, unlike the frozen constants above: a dedicated `ConnectionsExpression` reads `Simulation.ConnectionsAllocated` (the SPID allocator's distance past its seed of 50), so it advances on every session without separate instrumentation.
  Real reports cumulative login attempts since server start; the session-allocation count is the closest cheap in-process proxy.
  A fresh `Simulation`'s first connection reads 1.

**`sys.fn_virtualfilestats(database_id, file_id)`** — system TVF, invoked bare (`fn_virtualfilestats(...)`) or `sys.`-qualified.
Column shape mirrors real exactly: `(DbId smallint, FileId smallint, TimeStamp bigint, NumberReads bigint, BytesRead bigint, IoStallReadMS bigint, NumberWrites bigint, BytesWritten bigint, IoStallWriteMS bigint, IoStallMS bigint, BytesOnDisk bigint, FileHandle varbinary(8))`.
`NULL` is the wildcard at either argument (all databases / all files); a non-NULL id naming no database or file yields zero rows (including negatives such as `-1`), matching real.
The simulator has no physical file model, so it reports **one row per (database, `file_id 1`)** with every IO counter, `BytesOnDisk`, and `FileHandle` at 0 — the file cardinality per database differs from real (real seeds tempdb with multiple data files) but the wildcard / filter semantics and column shape match.
Wrong arg count matches real: one argument → **Msg 313** (insufficient arguments), three → **Msg 8144** (too many arguments).
The legacy `::fn_virtualfilestats(...)` prefix form isn't tokenized (`::` needs grammar work); the bare and `sys.`-qualified forms cover the documented invocations.
Dispatch lives in `Parser/Selection.VirtualFileStats.cs`, wired into `ParseSingleFromSourceCore` after `ParseObjectName` (so the `sys.` qualifier parses first, unlike the qualifier-less rowset functions).

## Session-state store: `SESSION_CONTEXT` / `CONTEXT_INFO` / connection scalars

These carry real per-session state on `SimulatedDbConnection` (not placeholder constants), so values persist across batches on the same connection and reset with a new connection.

- **`sp_set_session_context @key, @value [, @read_only]`** + **`SESSION_CONTEXT(N'key')`** — per-session key/value store (backs multi-tenant / row-level-security patterns).
  Named and positional argument forms both work.
  Keys are **case-sensitive** (ordinal — `TenantId` ≠ `tenantid`, matching SQL Server's binary key comparison regardless of database collation).
  A missing key reads as NULL; a NULL key argument to `SESSION_CONTEXT` raises **Msg 8116** (`session_context` lowercase in the wording).
  `sp_set_session_context` with a NULL `@key` raises **Msg 225**; re-setting a key previously stored with `@read_only = 1` raises **Msg 15664**.
  Like real SQL Server, `SESSION_CONTEXT` returns **`sql_variant`** preserving the stored value's base type — an `int` stored round-trips as `int`, an `nvarchar` as `nvarchar`.
  The common `WHERE int_col = SESSION_CONTEXT(N'key')` shape works by the comparison path converting the column side up to `sql_variant` and matching within the exact-numeric family (the family rules below).
- **`SESSIONPROPERTY(name)`** (`Parser/Expressions/SessionProperty.cs`) — the current session setting for one of the ANSI / arithmetic SET options: `ANSI_NULLS`, `ANSI_PADDING`, `ANSI_WARNINGS`, `ARITHABORT`, `CONCAT_NULL_YIELDS_NULL`, `NUMERIC_ROUNDABORT`, `QUOTED_IDENTIFIER`.
  DacFx's bacpac-export preamble reads `ISNULL(SESSIONPROPERTY('ANSI_NULLS'), 0)` / `ISNULL(SESSIONPROPERTY('QUOTED_IDENTIFIER'), 1)`.
  Like real SQL Server the result is **`sql_variant`** with an inner base type of `int` (each option reads back 1 / 0).
  **Fresh-session defaults** (probe-confirmed against SQL Server 2025 on a SqlClient connection): every option is 1 **except `ARITHABORT` and `NUMERIC_ROUNDABORT`, which default 0**.
  The six ANSI toggles are recorded as live state on `SimulatedDbConnection` (`AnsiNulls` / `AnsiPadding` / `AnsiWarnings` / `Arithabort` / `ConcatNullYieldsNull` / `NumericRoundabort`) via their `SET` handlers (`RecordSessionStateOption` in `Simulation.Set.cs`, wired into both the single and comma-list `SET opt1, opt2, … ON|OFF` forms); `QUOTED_IDENTIFIER` reads the tracked `QuotedIdentifiers` state.
  Recording follows the `QUOTED_IDENTIFIER` scoping rule — a top-level `SET` persists to the session, but a `SET` inside a procedure / function / trigger body or dynamic SQL does not write through.
  **These six toggles remain parse-and-discard for their actual storage/arithmetic semantics** (the simulator doesn't model `= NULL` comparison, trailing-space padding-on-assign, or round-abort); the state exists only so the option reads back consistently.
  Names are case-insensitive; an unknown option name returns NULL.
- **`CONTEXT_INFO()`** + **`SET CONTEXT_INFO <binary>`** — the legacy single 128-byte slot.
  NULL until set; once set, SQL Server stores exactly 128 bytes (right-padded / truncated), so `DATALENGTH(CONTEXT_INFO())` is always 128 afterward.
  Only the literal-binary `SET` form is modeled — a `@var` value side isn't accepted by the SET value parser.
- **`CONNECTIONPROPERTY(name)`** — `sql_variant` (like real).
  The modeled properties carry an `nvarchar` inner: probe-confirmed `net_transport` = `'TCP'`, `protocol_type` = `'TSQL'`; `auth_scheme` / `physical_net_transport` report placeholder constants.
  Address / port properties (real types them `nvarchar` / `smallint`) are unmodeled and, like unknown names, return a NULL `sql_variant`.
- **`CURRENT_TRANSACTION_ID()`** — bigint, approximated by the database's monotonic commit counter (a plausible increasing value, not a stable per-transaction id — apps use it for correlation, not correctness).
- **`CURRENT_REQUEST_ID()`** — int, returns 0 (the simulator doesn't multiplex requests per session; probe-confirmed value for a single-request session).

**`SESSION_ID()` is deliberately not modeled** — it's not a box-product function (raises Msg 195 on SQL Server 2025; it's a dedicated-SQL-pool / cloud surface).
`@@SPID` is the box session-id mechanism.

## `COLLATIONPROPERTY(collation_name, property)`

`Parser/Expressions/CollationProperty.cs`: metadata for a collation.
SSMS's Object-Explorer per-database follow-up runs `COLLATIONPROPERTY((select collation_name from sys.databases where name = …), 'CodePage')`.
Like real SQL Server the result is **`sql_variant`** carrying a per-property inner base type — `CodePage` / `LCID` / `ComparisonStyle` as `int`, **`Version` as `tinyint`** (probe-confirmed against SQL Server 2025), `Name` as `nvarchar`.
An **unrecognized collation name** or an **unknown property** returns a NULL `sql_variant` (matches the reference).
Property names are case-insensitive.

Values derive from the collation model (`Collation.TryGetMetrics`) so any recognized name resolves — the name is re-walked into its prefix / suffix-flags / version / code-page token.
Probe-confirmed against SQL Server 2025: `SQL_Latin1_General_CP1_CI_AS` → CodePage 1252, LCID 1033, ComparisonStyle 196609, Version 0, Name `SQL_Latin1_General_CP1_CI_AS`.

- **CodePage** — the ANSI code page.
  `_UTF8` names → 65001; SQL_\* names read their `CPnnn` name token (CP1 → 1252); Windows names come from the probe-built prefix registry (`Japanese*` → 932, `Latin1_General*` → 1252).
  The same `ResolveAnsiCodePage` that pins `Collation.StorageEncoding`, so the reported page and the stored bytes can't disagree; verified equal to the reference server across all 5540 `sys.fn_helpcollations()` names.
  Twelve Windows prefixes report 0 (Unicode-only) rather than falling back to 1252 — see [`collations.md`](collations.md#unicode-only-collations--msg-459).
- **LCID** — from the probe-built prefix registry (`SQL_Latin1_General` / `Latin1_General` → 0x0409 = 1033, `Japanese` → 0x0411 = 1041); defaults to 0x0409 for a recognized prefix that isn't tabulated.
  *Known minor divergences: sort-variant prefixes with a distinct sort-order LCID and the CP1254 SQL_Latin1 members fall back to the base-prefix LCID.*
- **ComparisonStyle** — derived from the suffix flags: binary (`_BIN` / `_BIN2`) → 0, else `ignore-case (0x1 when CI) + ignore-accent (0x2 when AI) + ignore-kana (0x10000 unless KS) + ignore-width (0x20000 unless WS)` (CI_AS → 196609, CI_AI → 196611, CS_AS → 196608, CI_AS_KS_WS → 1).
- **Version** — the version ordinal from the numeric name token: unversioned / SQL_\* → 0, 90 → 1, 100 → 2, 140 → 3, 160 → 4.
- **Name** — the collation's canonical name.

## `FILEPROPERTY(file_name, property)`

`Parser/Expressions/FileProperty.cs`: per-file metadata for a file of the **current** database.
SSMS's Database Properties → General page reads it (`CAST(FILEPROPERTY(s.name, 'SpaceUsed') AS float) * 8` over `sys.database_files WHERE type = 1`) to compute the log file's used space, and `Database.SpaceAvailable` in SMO drives it.
Returns **`int`** (probe-confirmed against SQL Server 2025).
The simulator models exactly two files per database, mirroring `sys.database_files`: the primary data file `<db>_Data` (file_id 1, ROWS) and the log file `<db>_Log` (file_id 2, LOG).
File names are matched with SQL Server's trailing-space-insensitive `=` semantics; property names are case-insensitive and trailing-space insensitive (the property arg is `TrimEnd(' ')`-ed before the switch, matching the probed reference which accepts `'SpaceUsed '`).

- **SpaceUsed** — for the data file, `BuiltInResources.SumDataFilePages` (the live page total across every modeled allocation unit — the same value `sys.allocation_units` / `sys.database_files.size` derive from, so SSMS's `SpaceAvailable = size − SpaceUsed` stays non-negative); for the log file, a small synthetic constant (`BuiltInResources.LogFileUsedPages` = 24 pages, well under the 128-page log size — a fixed plausible value, since the simulator has no log to measure).
- **IsReadOnly** — always 0 (no read-only files modeled).
- **IsPrimaryFile** — 1 for the data file (file_id 1), 0 for the log file.
- **IsLogFile** — 1 for the log file, 0 for the data file.

An **unknown property**, an **unknown file name**, a **NULL file name**, or a **NULL property** all return NULL (all probe-confirmed).
Data-file `SpaceUsed` is self-consistent with `sys.allocation_units` and `sys.database_files` — see the consistency contract in [`catalog-views.md`](catalog-views.md).

## `SQL_VARIANT_PROPERTY(expression, property)`

`Parser/Expressions/SqlVariantProperty.cs`: reports one facet of the `sql_variant` that would capture `expression` — `BaseType` / `Precision` / `Scale` / `MaxLength` / `TotalBytes` / `Collation`.
SSMS's Database Properties dialog reads `SQL_VARIANT_PROPERTY(value, 'BaseType')` off `sys.database_scoped_configurations`.
When `expression` is a **true `sql_variant`** (the primary use — reading the DSC `value` column, or `CAST(x AS sql_variant)`), the function unwraps to the inner value and describes *that* (a variant NULL yields NULL like any NULL).
For a non-variant argument it describes the value directly.
Like real SQL Server the *result* is **`sql_variant`** carrying a per-property inner base type: `BaseType` / `Collation` as `sysname` (an `nvarchar` inner), the four numeric facets as `int` (probe-confirmed against SQL Server 2025).
Property names are case-insensitive.
A NULL expression, a NULL property, an unknown property, or a value whose type can't live in a sql_variant (MAX strings, LOB, xml, spatial, hierarchyid) all return a NULL `sql_variant`.
Probe-confirmed against SQL Server 2025:

- **BaseType** — the bare type name (`1` → `int`, `'abc'` → `varchar`, `N'abc'` → `nvarchar`, `CAST(1 AS bit)` → `bit`, `GETDATE()` → `datetime`).
  Decimal-family values report **`numeric`** — matching a numeric literal's inference (`1.5` → `numeric`).
  *Divergence*: the simulator has one decimal family, so `CAST(1 AS decimal)` also reports `numeric` where real reports `decimal`.
- **Precision / Scale** — the value type's numeric/temporal precision-scale (`1.25` → 3 / 2; `1` → 10 / 0; `datetime` → 23 / 3; `time(7)` → 16 / 7).
  String / binary / guid → 0 / 0.
- **MaxLength** — the type's declared byte width, *not* the value's length: `varchar(10)` → 10, `nvarchar(10)` → 20, `int` → 4, `decimal(5,2)` → 5, `char(5)` → 5, `datetime` → 8.
  A generic string literal takes the value's byte length (`'abc'` → 3, `N'abc'` → 6).
- **TotalBytes** — the value's *actual* byte count plus a per-family overhead: strings **+8**, binary / decimal **+4**, the scale-carrying temporal types (time / datetime2 / datetimeoffset) **+3**, everything else **+2**.
  So `'abc'` → 11, `N'abc'` → 14, `1` → 6, `CAST(1 AS bit)` → 3, `decimal(5,2)` → 9.
- **Collation** — a string value's collation name; NULL for non-strings.

### `sql_variant` expression semantics

`SERVERPROPERTY` / `SESSIONPROPERTY` / `CONNECTIONPROPERTY` / `COLLATIONPROPERTY` / `LOGINPROPERTY` / `SESSION_CONTEXT` / `SQL_VARIANT_PROPERTY` / `DATABASEPROPERTYEX` / `OBJECTPROPERTYEX` all project true `sql_variant` (`SqlType.SqlVariant`), so the flows they realistically feed match real (probe-confirmed against SQL Server 2025):

- **ExecuteScalar / GetValue** surface the inner CLR object (a bare `int` for `SERVERPROPERTY('EngineEdition')`, `string` for `Edition`), so most existing value assertions are unchanged; `GetDataTypeName` reports `sql_variant` and `GetFieldType` is `object`.
- **`ISNULL` / `COALESCE` / `CASE`** keep the `sql_variant` result type (variant has highest data-type precedence in `SqlType.Promote`) and preserve each value's inner type — `ISNULL(SESSIONPROPERTY('ANSI_NULLS'), 0)` stays `sql_variant`.
- **`UNION [ALL]`** keeps each row's own inner base type — no schema unification/promotion (a variant column can be `int` on one row, `nvarchar` on the next).
- **Comparison** with a `sql_variant` on *either* side follows the family rules below — the base-typed side of a mixed pair converts *up* to `sql_variant` (probe-confirmed as `CONVERT_IMPLICIT(sql_variant, …)` in real's plan; the variant never unwraps into ordinary type-precedence promotion).
  So a string variant is less than any exact-numeric value and never equal to it (`variant nvarchar N'5' < 5`, never `=`), cross-family comparison stays value-blind even when one side is a plain literal or column, and **no comparison error is possible** — `variant nvarchar N'abc'` vs `int 5` is cleanly `<`, never Msg 245 (all probe-confirmed, including WHERE and JOIN forms over mixed-inner-type variant columns).
  A bare string literal against a `datetime` variant promotes to a *character*-family variant (not to `datetime`), so `variant datetime = '2020-01-01'` is false while `= CAST('2020-01-01' AS datetime)` is true.
- **`SELECT … INTO`** from a variant-producing built-in creates a `sql_variant` column (probe-confirmed).
- **Arithmetic** rejects: `variant + non-variant` → **Msg 257** (`Implicit conversion from data type sql_variant to <target> is not allowed. Use the CONVERT function to run this query.`); `variant + variant` and `string + variant` → **Msg 402** (`… incompatible in the add operator`).
  `PromoteForArithmetic` is the single source; a runtime guard in `IntegerArithmetic` routes through it so `Run`-time and projection-schema errors agree.
- **Cross-type ordering / grouping** (`Storage/SqlVariantOrdering.cs`, probe-confirmed): comparison is two-level — datatype-family rank first, then value within the family.
  Six families, lowest to highest: **1 `uniqueidentifier`; 2 binary** (`binary`/`varbinary`, byte-lexicographic); **3 character** (`char`/`varchar`/`nchar`/`nvarchar` — Unicode and non-Unicode are ONE family); **4 exact numeric** (`bit`, integer types, `decimal`, `money`/`smallmoney`, compared as decimal); **5 approximate** (`real`/`float` — above *every* exact value regardless of magnitude); **6 date/time** (compared as an instant: `time` anchored to 1900-01-01, `datetimeoffset` by UTC instant).
  Cross-family comparison is value-blind (`float 0.5 > bigint 1000000`); within a family, cross-type values compare by value and equal values are truly equal — `int 5` / `bigint 5` / `decimal 5.00` are one GROUP BY / DISTINCT bucket whose representative is the first value encountered (matching real's plan-order representative), and their ORDER BY tie order is undefined on real (plan-dependent), so tests must not pin it.
  NULL sorts lowest.
  `MIN`/`MAX` pick the family-hierarchy extremes.
  Same-collation character pairs compare under that collation; cross-collation pairs compare by code point **without** a Msg 468 conflict (probed) — the character family hashes by rank alone since no single hash agrees with both regimes.
  The `SqlValue` variant arms (`CompareTo` / `Equals` / `GetHashCode`) implement the rules, so ORDER BY, GROUP BY, DISTINCT, MIN/MAX, and hash joins all inherit them via `SqlValueKey`; a variant-vs-base equi-join key promotes to `sql_variant` (`Promote` → `CoerceTo` wraps the base side), so the hash fast path keys by the same family semantics.
  Oracle: `SqlVariantOrderingTests` (in-process; the one-side-variant comparison tests live there too).

**Conversion to text uses style 0 for a temporal payload**, where the same base type converted directly uses its own ISO default — a `time` reads `1:45PM` through a variant and `13:45:12.345` without one, and `date` / `datetime2` / `datetimeoffset` behave the same way (probe-confirmed 2026-07-30).
`datetime` and `smalldatetime` already default to style 0, so both routes agree there.

## Built-in TVF: `STRING_SPLIT`
`STRING_SPLIT(input, separator [, enable_ordinal])` dispatches in `ParseSingleFromSource` alongside `OPENJSON` — case-insensitive name match before generic name resolution.
Yields one row per substring split on the single-character separator.

- Schema is decided at parse time: 2-arg form projects `(value <input-string-type>)`; 3-arg form with literal `enable_ordinal = 1` adds `ordinal bigint`.
  `enable_ordinal = 0` or NULL collapses back to the 2-arg schema.
  The third argument must be a parse-time-constant integer expression (the schema is shape-fixed at compile time).
  The gate first walks the arg for any variable via `Expression.ContainsVariableReference` — **every** variable-bearing shape raises Msg 8748, not only a bare `@v` (probe-confirmed: `cast(@v as int)`, `@v + 0`, and `(@v)` all reject) — then evaluates against an empty resolver to catch column references.
  Constant shapes with no variable are accepted (probe-confirmed: `cast(1 as int)`, `(1)`, `1 + 0` all add the ordinal column).
  `ContainsVariableReference` recurses through the common containers (`VariableReference` / `Parenthesized` / `Cast` / `TwoSidedExpression`); a variable buried in a less-common container is a residual coverage gap.
- NULL `input` → zero rows; empty `input` → one row with empty value (and ordinal 1 in the ordinal-enabled form).
- NULL / empty / multi-character `separator` → Msg 214 at runtime (probe-confirmed: validated before the input — NULL sep raises 214 even when input is also NULL).
- Non-int third argument → Msg 8116; `enable_ordinal` literal outside {0, 1, NULL} → Msg 4199.
- Composes with `CROSS APPLY` / `OUTER APPLY` via the lateral-dispatch fast path: `ParseLateralFromSource` recognizes `STRING_SPLIT` (and `OPENJSON`) by name and routes back through `ParseSingleFromSource` with the chained outer-type resolver that includes left-side sources (so `STRING_SPLIT(t.col, ',')` correctly resolves `t.col` against the APPLY's left side).
- Input column type determines the `value` column's string family at parse time (`varchar` → `varchar`; `nvarchar` → `nvarchar`); non-string input maps to `nvarchar`.
  The value column inherits MAX-ness from the input's parse-time `GetSqlType` against the outer-type resolver (`ParseStringSplit`).

## Built-in TVF: `GENERATE_SERIES`
`GENERATE_SERIES(start, stop [, step])` (SQL Server 2022+) — third sibling of `STRING_SPLIT` / `OPENJSON` in the `ParseSingleFromSource` dispatch (and the `ParseLateralFromSource` allowlist, so `CROSS APPLY GENERATE_SERIES(1, t.n)` lateral-correlates correctly).
Projects a single column named `value`.
Probe-confirmed against SQL Server 2025.

- Allowed arg types: `tinyint`, `smallint`, `int`, `bigint`, `decimal` / `numeric`.
  Anything else (`float`, `real`, `money`, `varchar`, `date`, …) raises **Msg 8116** at parse, with verbatim wording `Argument data type <type> is invalid for argument <N> of generate_series function`.
- All three args must share the same type.
  Integer subtypes are distinct (`int` + `bigint` raises **Msg 5373**); `decimal` / `numeric` collapse to one family and tolerate differing precision / scale (unified via `SqlType.Promote`, so DECIMAL(10,1) + DECIMAL(10,2) projects DECIMAL with the wider scale).
- Output column type tracks the input type — `tinyint` args project `tinyint`, decimal args project decimal with the unified precision / scale.
- Step omitted: defaults to `-1` when `start > stop`, else `1` — so `GENERATE_SERIES(5, 1)` yields the descending sequence `5, 4, 3, 2, 1` (probe-confirmed; matches Microsoft's docs).
- Wrong-direction step (positive step with `start > stop`, or negative step with `start < stop`) → empty rowset, no error.
  Step `= 0` → **Msg 4199** (`Argument value 0 is invalid for argument 3 of generate_series function`).
- Any NULL arg → empty rowset (no error, no row).
  Bare untyped `NULL` is also accepted — the column type is inferred from the non-NULL siblings.
- Fewer than 2 args → **Msg 313**; more than 3 → **Msg 8144**.
  (Real server raises the procedure-shaped error numbers even though `GENERATE_SERIES` is a TVF; verbatim wording probed.)
- Internal generation uses `long` arithmetic for integer types and `decimal` for the decimal family.
  `bigint` near `MAX_INT64` terminates via the overflow-edge check (`cur > long.MaxValue - step`) before the addition would wrap, so `GENERATE_SERIES(MAX_INT-7, MAX_INT, 3)` yields three rows just like real SQL Server.

## SUSER_SID / SID_BINARY

- **`SUSER_SID([login [, Param2]])`** returns the server login's binary SID, mirroring the `sys.server_principals` sid surface: `sa` → the well-known single byte `0x01`, registry logins (`CREATE LOGIN`) → their deterministic 16-byte synthetic sid (`BuiltInResources.DeriveLoginSid`), unknown names → NULL, NULL → NULL.
  The no-argument form returns `0x01`, matching `sys.dm_exec_sessions.security_id` for the simulator's fixed session principal.
  The optional Param2 (real's skip-name-validation flag) parses and is ignored.
  Result type `varbinary(85)`-family (`SqlType.Varbinary`).
- **`SID_BINARY(name)`** is constant NULL — probe-confirmed against SQL Server 2025: it resolves only Windows / Entra-ID directory principals and returns NULL even for existing SQL-auth logins, so NULL is faithful for every input the simulator can host.
  The argument still parses and evaluates.
  Surfaced by SSMS's Select-Top-1000 server-properties batch (`suser_sname(sid_binary(@SqlGroup))`).

## Legacy text-pointer scalars: `TEXTPTR` / `TEXTVALID`

`Parser/Expressions/TextPointer.cs` (`LegacyTextPointer` helper + `TextPointer`), `Parser/Expressions/TextValid.cs`.
Probe-confirmed against SQL Server 2025.

- **`TEXTPTR(column)`** returns the 16-byte `varbinary` text pointer of a base-table `text` / `ntext` / `image` column, or NULL when the cell is NULL.
  The argument must be a base-table column reference: a literal, CAST, or computed expression raises **Msg 280** (`Only base table columns are allowed in the TEXTPTR function.`), and a column of any other type (including `varchar(max)`) raises **Msg 8116** (`Argument data type <t> is invalid for argument 1 of textptr function.`).
  Real varies the pointer per row; the simulator fabricates a shape carrying only column identity — an 8-byte signature plus an 8-byte FNV-1a-64 hash of the case-folded column name — since the only sanctioned consumers (`READTEXT` / `WRITETEXT` / `UPDATETEXT`) stay unmodeled.
  Two non-NULL cells of one column therefore share a pointer (a divergence with no observable consumer).
- **`TEXTVALID('table.column', text_ptr)`** returns `int` `1` when the pointer is valid for the named column, else `0`.
  A NULL pointer / NULL name, a pointer that isn't a simulator-fabricated text pointer (e.g. arbitrary bytes), and a name whose final (column) segment doesn't match the pointer's source column all return `0`.
  The name must have at least two dotted parts (`table.column`) — a bare single-part name returns `0`, matching real — but only its column segment is matched against the pointer's embedded column-identity hash; the table portion is not resolved against the catalog.
  A syntactically valid name whose column segment matches the pointer's source column therefore returns `1` even if its table portion names a different table (real cross-checks the exact column object); this is unobservable through the sanctioned `TEXTVALID('t.c', TEXTPTR(c))` idiom, where the two column names always agree.

## Placeholder security / FILESTREAM scalars: `CERTENCODED` / `CERTPRIVATEKEY` / `GET_FILESTREAM_TRANSACTION_CONTEXT`

`Parser/Expressions/CertificateFunctions.cs` (`CertificateFunction`, `isPrivateKey` flag), `Parser/Expressions/GetFilestreamTransactionContext.cs`.
The simulator models no certificate store or FILESTREAM storage, so each returns a NULL `varbinary(max)` — the faithful answer for the state the simulator is always in.
Probe-confirmed against SQL Server 2025.

- **`CERTENCODED(cert_id)`** → NULL (the answer real gives for a nonexistent certificate id).
  Exactly one argument; any other count raises **Msg 174** (`The CertEncoded function requires 1 argument(s).` — PascalCase function name, unlike the lowercase-rendered `PI` / `ISNULL` form).
- **`CERTPRIVATEKEY(cert_id, N'encryption_password' [, N'decryption_password'])`** → NULL.
  Two or three arguments; any other count raises **Msg 189** (`The CertPrivateKey function requires 2 to 3 arguments.`, via `SimulatedSqlException.FunctionArgumentCountRange`).
- **`GET_FILESTREAM_TRANSACTION_CONTEXT()`** → NULL, the faithful "no active FILESTREAM transaction" answer a FILESTREAM-enabled server gives outside such a transaction.
  (A reference instance with FILESTREAM file-system access disabled at the instance level instead raises Msg 5592; the simulator returns the enabled-but-idle answer.)
  Zero arguments; any argument raises **Msg 174** (`The get_filestream_transaction_context function requires 0 argument(s).` — lowercase function name).

The untyped-NULL-literal argument diagnostic (real raises Msg 8116 for `CERTENCODED(NULL)` because an untyped `NULL` literal has no type) is not modeled — the simulator's untyped `NULL` literal carries `Type=Int32`, so it flows through as a valid int argument and returns NULL.

## ODBC escape sequences: `{d}` / `{t}` / `{ts}` / `{guid}` / `{fn}`

The tokenizer emits `{` and `}` as single-char operators (no other T-SQL meaning); `Expression.ParseOdbcEscape` consumes the escape in expression position.

- **`{d 'yyyy-mm-dd'}`** and **`{ts 'yyyy-mm-dd hh:mm:ss'}`** → a `datetime` literal (the string coerced via `Cast.ApplyCoercion`); a date-only string lands at midnight.
- **`{t 'hh:mm:ss'}`** → a `datetime` on the **current date** at the given time — matching SQL Server 2025's time-escape semantics (probe-confirmed: `{t '12:00:00'}` returns today 12:00, not 1900-01-01).
  A plain datetime coercion of a time-only string lands on 1900-01-01, so the parse re-homes the time onto `DateTime.UtcNow.Date`.
- **`{guid '…'}`** → a `uniqueidentifier` literal.
- **`{fn NAME(args)}`** → the mapped built-in scalar function, parsed as a normal call.
  ODBC-distinct names are renamed to their T-SQL equivalents: `UCASE`→`UPPER`, `LCASE`→`LOWER`, `LENGTH`→`LEN`, `LOCATE`→`CHARINDEX`, `REPEAT`→`REPLICATE`, `IFNULL`→`ISNULL`, `INSERT`→`STUFF`, `NOW`→`GETDATE`, `ATAN2`→`ATN2`, `DAYOFMONTH`→`DAY` (`MapOdbcFunctionName`).
  A name already matching a T-SQL built-in (`CONCAT`, `LEFT`, `CEILING`, `ABS`, …) passes through unchanged.

**Known gap**: the ODBC `{fn}` functions with no same-arity T-SQL rename — `MOD` (→ `%`), `TRUNCATE` (→ `ROUND(x, y, 1)`), `CURDATE` / `CURTIME`, `DAYOFWEEK` / `DAYOFYEAR` / `HOUR` / `MINUTE` / `SECOND` / `WEEK` / `QUARTER` (→ `DATEPART(part, x)`), `USER` / `DATABASE`, the ODBC `CONVERT(val, SQL_type)` — are left unmapped (they fall to the normal not-a-built-in path).
The `{oj … LEFT OUTER JOIN …}` outer-join escape (a FROM-clause construct, not expression position) is likewise not modeled.

## The native `REGEXP_*` family (SQL Server 2025)

Seven members ship in the box product, in three shapes.
Probed against a live SQL Server 2025 (17.0.4065.4) reference instance.

| Member | Shape | Signature | Result | Compat gate |
| --- | --- | --- | --- | --- |
| `REGEXP_COUNT` | scalar | `(string, pattern [, start [, flags]])` | `int` | none |
| `REGEXP_INSTR` | scalar | `(string, pattern [, start [, occurrence [, return_option [, flags [, group]]]]])` | `int` | none |
| `REGEXP_REPLACE` | scalar | `(string, pattern [, replacement [, start [, occurrence [, flags]]]])` | input family, container width | none |
| `REGEXP_SUBSTR` | scalar | `(string, pattern [, start [, occurrence [, flags [, group]]]])` | input type | none |
| `REGEXP_LIKE` | **predicate** | `(string, pattern [, flags])` | boolean | **170 only** |
| `REGEXP_MATCHES` | rowset | `(string, pattern [, flags])` | 5 columns | **170 only** |
| `REGEXP_SPLIT_TO_TABLE` | rowset | `(string, pattern [, flags])` | 2 columns | **170 only** |

Implementation: `Parser/Expressions/RegexpScalar.cs` (one node, four kinds), `RegexpLikePredicate.cs`, `Parser/Selection.Regexp.cs`, with `RegexpArguments.cs` holding the shared argument rules and `RegexDialect.cs` the pattern translation.

### `REGEXP_LIKE` is a predicate, not a scalar

`SELECT REGEXP_LIKE('abc', 'a.c')` raises **Msg 156** on real, so the construct binds in the WHERE / HAVING / IF / CASE-WHEN / CHECK grammar (`BooleanExpression.ParsePredicateOperand`, beside `CONTAINS` / `FREETEXT` / `UPDATE(col)`) rather than in `ResolveBuiltIn`.
Its arity is enforced by the grammar too — a fourth argument or a bare `REGEXP_LIKE(x)` is **Msg 102** near the offending token, not the scalars' Msg 189.
A NULL in any argument yields UNKNOWN, so `NOT REGEXP_LIKE(NULL, 'a')` doesn't pass either.

The keyword reservation that comes with it is covered in [`grammar.md`](grammar.md#compatibility-gated-reservation-regexp_like).

### Argument rules

**Validation order** (probe-confirmed, and observable because the members differ in which check a caller sees first):

1. Operand **type** — Msg 8116, raised even for a typed NULL (`REGEXP_COUNT(CAST(NULL AS int), 'a')`).
   The string operands take *no* implicit conversion: `REGEXP_COUNT(123, '2')` raises where the rest of the string scalars would render the number.
   The bare `NULL` keyword is accepted (it has no type to reject).
2. **NULL** in any argument → NULL result, before the pattern compiles — so `REGEXP_COUNT(NULL, '(')` is NULL, not a pattern error.
   The one exception is `REGEXP_INSTR`'s `return_option`, where NULL reads as the default 0.
3. Numeric **range** — Msg 19301.
4. **Flags** — Msg 19303.
5. **Pattern** compilation — Msg 19300 / 19307 / 19308 / 19309.

**Msg 19301** wording is `'<ARG>' value should be greater than or equal to <min> but '<value>' is provided in '<FUNCTION>' function.`, and real's `<min>` isn't always the bound it enforces:

| Function | Argument | State | Reported min | Enforced |
| --- | --- | --- | --- | --- |
| `REGEXP_COUNT` | `START` | 1 | 1 | `>= 1` |
| `REGEXP_REPLACE` | `START` | 1 | 1 | `>= 1` |
| `REGEXP_REPLACE` | `OCCURRENCE` | 2 | 0 | `>= 0` (0 = every match) |
| `REGEXP_INSTR` | `START` | 3 | 1 | `>= 1` |
| `REGEXP_INSTR` | `OCCURRENCE` | 4 | 1 | `>= 1` |
| `REGEXP_INSTR` | `GROUP` | 5 | 1 | `>= 0` — 0 is the whole match |
| `REGEXP_INSTR` | `RETURN_OPTION` | 6 | 0 | exactly `{0, 1}` — 2 is rejected with the same "greater than or equal to 0" text |
| `REGEXP_SUBSTR` | `START` | 7 | 1 | `>= 1` |
| `REGEXP_SUBSTR` | `OCCURRENCE` | 8 | 1 | `>= 1` |
| `REGEXP_SUBSTR` | `GROUP` | 9 | 0 | `>= 0` |

**Flags** are exactly `c` (case-sensitive, the default), `i`, `s` (dot matches newline), `m` (multiline).
Oracle's `x` is **not** accepted.
The match is case-sensitive (`'I'` is rejected) and the characters apply left-to-right, so `'ic'` ends case-sensitive and `'ci'` case-insensitive.
Anything outside the set raises **Msg 19303**, which quotes the *whole* flags string rather than the offending character: `Invalid flag provided. 'imsxc' are not valid flags. Only {c,i,s,m} flags are valid.`

**Arity** is Msg 189 with the lowercase name — `The regexp_count function requires 2 to 4 arguments.` — for the scalars, with maxima 4 / 7 / 6 / 6.
The two rowset members instead report a TVF's **Msg 313** / **Msg 8144** at state 3.

### Result types

- `REGEXP_COUNT` / `REGEXP_INSTR` → `int`.
- `REGEXP_REPLACE` → the **input's** family at container width (`varchar(8000)` / `nvarchar(4000)`), independent of the replacement's family, with MAX carried through unbounded.
  A grown result **truncates silently** at that width rather than raising Msg 8152.
- `REGEXP_SUBSTR` → the input's own declared width (`varchar(10)` in → `varchar(10)` out; `char(5)` in → `varchar(5)`), MAX carried through.

### Per-member semantics

- `occurrence` is 1-based and counts from `start`; `REGEXP_REPLACE` alone accepts 0, meaning every match.
- `REGEXP_INSTR`'s `return_option` 0 reports the match's first character position, 1 reports one past its last.
  A `group` the pattern doesn't have — or that didn't participate — reports 0; `REGEXP_SUBSTR` reports NULL for the same case.
- `REGEXP_REPLACE`'s replacement uses **Oracle's backslash backreferences**, not `$`: `\1`…`\9` insert a capture group (out-of-range → empty), `\\` is one literal backslash, `$` is literal, and every other backslash escape passes through with its backslash intact (`\n` is two characters, `\0` is two characters).
- An **empty pattern** makes `REGEXP_REPLACE` a no-op — `REGEXP_REPLACE('abc', '', '-')` is `'abc'` — even though `x*`, which also matches empty, replaces at every position (`'-a-b-c-'`).
- **Collation is irrelevant** to matching: a `CI_AS` column still matches case-sensitively unless the `i` flag says otherwise, and there is no accent-insensitive mode.
  The `i` flag applies Unicode simple case folding (É matches é).

### The rowset members

`REGEXP_MATCHES` projects `(match_id bigint, start_position int, end_position int, match_value <input type>, substring_matches varchar(max))`.
`substring_matches` is `varchar(max)` whatever the input's family.
It carries one JSON object per capture group with the group's 1-based `start` and its `length`, `null` members for a group that didn't participate, and — when the pattern has **no** capture group — a single entry for the whole match.

A zero-width match reports the same value for `start_position` and `end_position`, **clamped to the input's length**: `REGEXP_MATCHES('aa', 'a*')` reports the trailing empty match at 2, not 3, and `REGEXP_MATCHES('', '')` reports 0.

`REGEXP_SPLIT_TO_TABLE` projects `(value <input type>, ordinal bigint)` and runs on a **different match enumeration than every other member**: a zero-width match landing exactly where the previous match ended is discarded.
That one rule is why `REGEXP_COUNT('aXbXc', 'X*')` is 6 while the same pattern splits into just `a` / `b` / `c`.
Beyond it the algorithm is the familiar one: a separator ending at position 0 contributes no leading empty segment, and a trailing segment is emitted only when the last separator didn't end at the input's end — so `('abc', '')` yields three single-character rows and `(',a,', ',')` yields an empty row on both ends.

A NULL in any argument yields an **empty result set** rather than a NULL row.
Both compose with `CROSS APPLY` / `OUTER APPLY` through the same `ParseLateralFromSource` allowlist `STRING_SPLIT` uses.

### Pattern dialect: RE2, not .NET

The engine underneath the box's `REGEXP_*` members is **RE2**.
Its parser error strings surface verbatim inside real's Msg 19300 wrapper, and its C++ octal quirk reproduces exactly — a bare `\1` is rejected as an unsupported backreference while `\101` parses as octal `A`, which is RE2's C++ `ParseEscape` and not Go's.

.NET's `Regex` accepts a strict superset, so `RegexDialect.cs` walks the pattern and does two jobs at once: raise real's error for what RE2 rejects, and rewrite what the two engines spell differently or *mean* differently.

**Rejected — Msg 19300**, `An invalid Pattern '<p>' was provided. Error '<detail>' occurred during evaluation of the Pattern.`:

| Construct | RE2 detail |
| --- | --- |
| `(ab)\1`, `\1a`, `\8` | `invalid escape sequence: \1` / `\8` |
| `(?=…)` / `(?!…)` / `(?<=…)` / `(?<name>…)` | `invalid perl operator: (?=` / `(?!` / `(?<` |
| `(?>…)` atomic group | `invalid perl operator: (?>` |
| `(?#comment)` | `invalid perl operator: (?#` |
| `(?x)` free-spacing | `invalid perl operator: (?x` |
| `(?P=name)` | `invalid perl operator: (?P` |
| `a++` / `a**` / `a*??` / `a+*` / `a{1,2}*` | `bad repetition operator: ++` / … |
| `?a` / `+a` / `\|*` / `(*a)` / `(?i)*` | `no argument for repetition operator: ?` / … |
| `a{1001}` / `a{2,1}` | `invalid repetition size: {1001}` / `{2,1}` |
| `\K` / `\Z` / `\e` / `\cA` / `\N{…}` | `invalid escape sequence: \K` / … |
| `\x{110000}` | `invalid escape sequence: \x{110000` (real drops the closing brace) |
| `[\b]` / `[a-\d]` | `invalid escape sequence: \b` / `\d` |
| `[z-a]` / `[[:foo:]]` / `\p{Foo}` | `invalid character class range: z-a` / `[:foo:]` / `\p{Foo}` |
| `(?P<>a)` / `(?P<a-b>a)` | `invalid named capture group: (?P<>` / `(?P<a-b>` |

Four structural failures get their own numbers instead: an unclosed group → **Msg 19308** `Missing ')' in the Pattern …`, an unclosed class → **Msg 19308** `Missing ']' …`, a stray `)` → **Msg 19307**, a trailing backslash → **Msg 19309**.
Their states split by member family — scalars and the predicate use one set, the two rowset members another:

| Message | Scalar / predicate | Rowset |
| --- | --- | --- |
| 19300 | 1 | 2 |
| 19307 | 1 | 2 |
| 19308 missing `)` | 1 | 3 |
| 19308 missing `]` | 2 | 4 |
| 19309 | 1 | 2 |

**Accepted, and rewritten for .NET**:

| RE2 construct | Why it needs rewriting |
| --- | --- |
| `$` outside multiline | RE2 anchors at end of text; .NET also matches before a trailing newline, so it becomes `\z` |
| `\d` `\D` `\s` `\S` `\w` `\W` | ASCII-only in RE2, Unicode-aware in .NET — each expands to its explicit ASCII class (`\s` is `[\t\n\f\r ]`, excluding vertical tab) |
| `\b` / `\B` | RE2's word set is ASCII, so each expands to the equivalent lookaround pair |
| `[[:alpha:]]` and the other POSIX names | no .NET spelling; expanded to ranges (negated forms to their complement) |
| `\x{…}`, `\0oo`, `\101` | converted to the literal character |
| `\Q…\E` | no .NET equivalent; each character emitted escaped |
| `(?P<name>…)` | emitted as a plain capture group — the name is unobservable through the SQL surface, which sidesteps .NET's stricter naming rules (RE2 accepts `(?P<1x>…)`) |
| `(?U)` ungreedy | no .NET option; the walk swaps each quantifier's greediness while the flag is in scope |

Everything else — `(?:`, `(?i)` / `(?-i)` / `(?ims:…)`, `\A`, `\z`, `\p{L}` general categories, lazy quantifiers, leftmost-first alternation — means the same thing in both engines and passes through.
Alternation is Perl-style leftmost-first, not POSIX leftmost-longest: `REGEXP_SUBSTR('abc', 'a|ab')` is `'a'`.

**Runaway patterns.** Real's RE2 is backtracking-free, so no pattern can run away there.
The simulator reaches the same guarantee by compiling with `RegexOptions.NonBacktracking` wherever the translation allows it, which is everywhere except the `\b` / `\B` expansions (.NET's non-backtracking engine refuses lookaround).
A 5-second per-match timeout (`RegexDialect.MatchTimeout`) bounds those, surfacing .NET's `RegexMatchTimeoutException` — real has no error to mirror there.
Compiled patterns are cached per translated-pattern-plus-options key, bounded at 512 entries.

#### Divergences

- **RE2 Unicode script names** (`\p{Greek}`, `\p{Han}`) raise `NotSupportedException` — .NET's `\p` covers general categories and named blocks, not scripts, and mapping a script onto a same-named block would be wrong.
  The general categories themselves (`\p{L}`, `\p{Lu}`, …) and `\p{Any}` pass through; a name RE2 itself rejects still raises real's Msg 19300, which is why `RegexDialect` carries RE2's script-name table.
- Matching runs over **UTF-16 code units** where RE2 runs over code points, so a supplementary character counts as two positions in `REGEXP_INSTR` / `REGEXP_MATCHES`.
- `ERROR_STATE()` inside a T-SQL CATCH reads **one lower than the wire state** on real for Msg 19301 and Msg 19303 (`REGEXP_COUNT`'s START rejection is state 1 on the wire, 0 to `ERROR_STATE()`).
  The simulator has one state field and models the wire value, which is what every client sees.
- `sp_describe_first_result_set` on real declares `REGEXP_REPLACE(<char(N)>, …)` as `char(8000)` while the value it returns is only the input's width — internally inconsistent, and unreachable in a value model that encodes by declared type.
  The simulator declares `varchar(8000)`, which produces identical values and identical `DATALENGTH`.
