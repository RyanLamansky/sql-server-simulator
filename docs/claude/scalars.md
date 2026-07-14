# Built-in scalar functions

## Math scalar functions
`ABS`, `ROUND` (2-/3-arg, half-away-from-zero + truncate mode), `FLOOR`, `CEILING`, `POWER`, `SQRT`, `SIGN`, `LOG` (1-/2-arg), `EXP`, `LOG10`, trig family (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`), `PI`, `DEGREES`/`RADIANS`, `SQUARE`. EF emits all from `Math.X` LINQ; `Math.Truncate(x)` → `ROUND(x, 0, 1)`; `Math.Atan2` → `ATN2`.

**Type-widening rule** (shared across `ABS`/`FLOOR`/`CEILING`/`ROUND`/`SIGN`/`POWER`'s first arg): `tinyint`/`smallint` → `int`; `smallmoney` → `money`; `real`/`bit` → `float` (sic — bit widens to float, not int); everything else preserves. `POWER` returns the post-widen type of the *first* arg regardless of exponent — `POWER(int, float) → int` with truncation toward zero. `SQRT`/`LOG`/`EXP`/`LOG10` always return float.

**Implicit string coercion** (full math family — `ABS`/`FLOOR`/`CEILING`/`SIGN`/`SQRT`/`DEGREES`/`RADIANS`/`POWER`/`ROUND`/`LOG`/`LOG10`/`EXP`/`SQUARE`/`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`): string operands route through `MathScalars.CoerceImplicit` → `CoerceTo(SqlType.Float)`. Bad strings produce Msg 8114 ("Error converting data type varchar to float.") through the existing string-to-float parser. Probe-confirmed against SQL Server 2025 (2026-05-22). The widening rule treats string input as float for projection-schema parity. Two per-function nuances: `POWER`'s result type follows the **first** arg's widen rule (so `POWER('2', 3) → float` but `POWER(2, '3') → int` with truncation toward zero); `ROUND`'s **value** arg coerces but the `length` / `function` args stay strict-int (Msg 8116 on string, matching real).

Errors: `SQRT(neg)` / `LOG(<= 0)` / `LOG10(<= 0)` / `LOG(x, 1)` / `POWER(neg, frac)` → Msg 3623. `POWER(0, neg)` → Msg 8134. `EXP` / `SQUARE` overflow → Msg 8115 float. `ABS(int.MinValue)` / `ABS(bigint.MinValue)` → Msg 8115 with the result type's family. `POWER` int-result overflow → Msg 232.

**Trig family** (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`/`PI`/`SQUARE`) always returns `float`. Domain errors → Msg 3623 (including `ATN2(0, 0)`, which diverges from .NET's `Math.Atan2(0, 0) = 0`). Wrong arg count → Msg 174 (`"The {lower-name} function requires {N} argument(s)."`) — `pi(1)` raises Msg 174 not Msg 102.

**`DEGREES`/`RADIANS`** are type-preserving with one tweak: `decimal(p, s)` widens to `decimal(38, max(s, 18))` rather than preserving. Integer arm truncates toward zero; out-of-range integer results raise Msg 8115 with the family name. Decimal arm uses a 28-digit `DecimalPi` constant in evaluation order `(input * 180m) / DecimalPi` for trailing-digit fidelity. .NET decimal's 28-digit precision cap means scale > 28 results land at scale 28.

## Additional date scalars: `DATENAME` / `DATETRUNC` / `SWITCHOFFSET` / `TODATETIMEOFFSET` / `DATE_BUCKET` / `CURRENT_DATE`

- **`DATENAME(part, date)`** — sibling of `DATEPART` but returns the localized string for the matched part (`'January'` / `'Sunday'` / `'12'` / etc.) as `nvarchar`. Reuses `DATEPART`'s keyword tables for part validation and per-type compatibility (same Msg 9810 rejection set). Localized names follow .NET's `CultureInfo.InvariantCulture` — month names in English, weekday names in English, numeric parts as base-10 strings.
- **`DATETRUNC(part, date)`** (`Parser/Expressions/DateTimeAdjustments.cs`) — floor to start of the named part. Supported parts: `year`/`quarter`/`month`/`week`/`day`/`hour`/`minute`/`second` plus the millisecond/microsecond/nanosecond family. Result preserves the input's type (`datetime` → `datetime`, `datetime2(N)` → `datetime2(N)`); `date` source rejects time-bearing parts via Msg 9810 (reused factory).
- **`SWITCHOFFSET(dto, offset)`** — adjust a `datetimeoffset`'s offset while preserving the UTC instant; both offset (numeric `±N` minutes or string `'±HH:MM'`) forms accepted. Result type = input precision preserved (`datetimeoffset(N)`).
- **`TODATETIMEOFFSET(dt, offset)`** — attach an offset to a `datetime` / `datetime2` value, treating the input wall-clock as already in the named zone. Result `datetimeoffset(N)` matching input precision.
- **`DATE_BUCKET(part, bucket_width, date [, origin])`** (`Parser/Expressions/DateBucket.cs`) — bucket-aligned floor. `origin` defaults to `1900-01-01` for date/datetime inputs and `1900-01-01 00:00:00` for time-bearing types; `bucket_width` must be positive. Returns the same type as `date`.
- **`CURRENT_DATE`** — parens-less, dispatched directly from `Expression.Parse`'s expression-start switch (same shape as `CURRENT_TIMESTAMP`). Returns `date`. Equivalent to `CAST(SYSDATETIME() AS DATE)` — uses the same per-statement freeze.

## Date scalar functions: `DATEPART` / `DATEADD` / `DATEDIFF` / `DATEDIFF_BIG`
All take a bare datepart keyword. Result types: `DATEPART` → int; `DATEADD` preserves input type; `DATEDIFF` → int; `DATEDIFF_BIG` → bigint.

`DATEPART`/`DATEADD` enforce per-type keyword compatibility: `date` accepts only date parts; `time(N)` only time parts; `datetime`/`smalldatetime`/`datetime2(N)` accept both; `datetimeoffset(N)` adds `tzoffset`. Wrong combination → Msg 9810. `DATEADD` overflow → Msg 517. `DATEPART(weekday)` uses default `DATEFIRST 7` (Sunday=1); changing `DATEFIRST` not modeled.

**Implicit operand coercion** (date argument, all three functions): string operands route through `DatePartKinds.CoerceDateArgumentImplicit` → `CoerceTo(datetime2(7))`; integer operands → `CoerceTo(datetime)` (days-since-1900-01-01). Probe-confirmed against SQL Server 2025 (2026-05-22): `DATEPART(year, 0) = 1900`, `DATEADD(day, 1, 0) = 1900-01-02`, `DATEDIFF(day, 0, '2024-01-31') = 45320`. `DATEADD`'s offset (second) arg stays strict-int — string offsets raise Msg 9810 ("Argument data type varchar is invalid for argument 2 of dateadd function") just like real SQL Server. Minor projection-schema quirk: real SQL Server reports `DATEADD(day, 1, '2024-01-15')` as `datetime`; the simulator reports it as `datetime2(7)` (the convention from `DATEDIFF`'s existing string path).

`DATEDIFF`/`DATEDIFF_BIG` count `datepart`-unit *boundaries crossed*, not elapsed time — `datediff(year, '2023-12-31', '2024-01-01')` = 1. More permissive than DATEPART/DATEADD: every datepart works against every date/time-family combo. Only `tzoffset` and `iso_week` are rejected unconditionally → Msg 9806. `datetimeoffset` operands compare via UTC instant. Result-width overflow → Msg 535.

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

## Char-code scalars: `ASCII` / `UNICODE` / `CHAR` / `NCHAR`
Basic one-arg conversions between a character and its code point.

- **`ASCII(input)`** returns `int`. Reads the first character of `input` and returns its CP1252 byte value. NULL → NULL; empty string → NULL. Unicode input is CP1252-encoded first, so `ASCII(N'€')` returns 128 (CP1252's `€`); unrepresentable Unicode (emoji etc.) returns 63 via the encoder's `'?'` replacement fallback. Non-string inputs implicitly stringify *before* the first-char read, so `ASCII(65)` is 54 (the byte for `'6'`, the first char of `"65"`), not 65.
- **`UNICODE(input)`** returns `int`. Same input-handling shape as `ASCII`, but reads the .NET `char` directly rather than CP1252-encoding it. Supplementary code points (above U+FFFF, e.g. `N'😀'`) return the high surrogate value (55357 for 😀) under the non-SC default collation — not the full Unicode code point. An SC-aware variant returning 128512 would need explicit collation modeling; matches the simulator's "default collation only" stance.
- **`CHAR(code)`** returns `char(1)` (not `varchar(1)` — probe-confirmed via `sql_variant_property(CHAR(65), 'basetype')`). NULL → NULL; out-of-range (negative / > 255) → NULL. Non-integer inputs truncate-to-int (`CHAR(65.7)` → `'A'`, `CHAR('65')` → `'A'`). `CHAR(0)` is a valid NUL character with `DATALENGTH = 1`, not NULL.
- **`NCHAR(code)`** returns `nchar(1)`. NULL / out-of-range (negative, > 65535) → NULL. Supplementary code points like `NCHAR(128512)` (😀) return NULL rather than emitting a surrogate pair — non-SC collation behavior.

## Basic string scalars: `LEN` / `LOWER` / `UPPER` / `LTRIM` / `RTRIM` / `REVERSE` / `LEFT` / `RIGHT` / `REPLACE` / `CHARINDEX`
**Implicit operand coercion** is shared across the family via `StringScalars.CoerceToVarchar` (mirrors the `MathScalars` pattern). Non-string operands — integer, decimal, money, float/real, date-time, uniqueidentifier, varbinary/binary — implicit-cast to `varchar` in the active database's collation before the function runs. Varbinary/binary route through `SqlValue.CoerceBinaryToStringWithStyle(target, 0)`: each byte reinterpret-through CP1252 (varchar) or UTF-16 LE (nvarchar). `LEN(0x4142202020) = 2` because the trailing 0x20 bytes are CP1252 spaces and trim like ASCII spaces; `LEN(CAST(0x010203 AS binary(10))) = 10` because binary's zero-padding survives `TrimEnd(' ')`. **Image stays rejected** (Msg 8116) — real SQL Server rejects too, and `IsCoerceableToVarchar` deliberately excludes the legacy LOB form. Probe-confirmed against SQL Server 2025 (2026-05-22): `LOWER(12345) = '12345'`, `LEN(CAST('2024-01-15' AS DATE)) = 10`, `LOWER(CAST('2024-01-15 12:34:56' AS DATETIME)) = 'jan 15 2024 12:34pm'` (legacy datetime default format), `REPLACE(CAST('2024-01-15' AS DATE), '-', '/') = '2024/01/15'`. Source families outside the coerce-able set (varbinary, xml, spatial, table types) raise Msg 8116 via `InvalidArgumentDataType`. The projection-schema result type for `LEN` is always `int`; the other functions project as `varchar` for non-string sources and preserve the input string type otherwise. `REPLACE` runs the coerce per argument with the matching argument index in the Msg 8116 wording. `CHARINDEX`'s **haystack** (arg 2) coerces (`CHARINDEX('2', 12345) = 2`); the **needle** (arg 1) and **start** (arg 3) stay strict-int / strict-string respectively, matching real's Msg 8116 rejection.

## EF.Functions-driven string scalars: `PATINDEX` / `STUFF` / `QUOTENAME` / `REPLICATE` / `SPACE` / `FORMAT`
Bundle that fills out the raw-SQL string surface that EF's `FromSqlInterpolated` and `DefaultValueSql` workloads commonly reach. None of these are exposed as `EF.Functions.X` LINQ extensions; coverage targets raw-SQL paths.

- **`PATINDEX(pattern, subject)`** shares the LIKE wildcard compiler via `LikePatternBuilder` (single source of truth for `%`/`_`/`[...]`). Anchoring is decided by leading / trailing `%` in the pattern: a leading `%` strips the start anchor (find-anywhere); a trailing `%` strips the end anchor; without either, the pattern is anchored at both ends and only a full-subject match returns 1. Leading and trailing `%` characters are consumed by the anchoring decision and don't translate to `.*` in the regex body — that's what makes `PATINDEX('%abc%', 'xabcx')` return 2 (position of `abc`) rather than 1 (position of the empty `.*` prefix). Subject NULL raises Msg 8116 (asymmetric with NULL pattern, which silently returns NULL). Subject non-string raises Msg 8116; pattern non-string implicitly coerces to the subject's string family. Result type is `int` for bounded subjects and `bigint` for `varchar(MAX)` / `nvarchar(MAX)` / legacy LOB family. No `ESCAPE` clause (Msg 156 at parse, falls out of the general grammar).
- **`STUFF(input, start, length, replacement)`** uses 1-based `start` ∈ `[1, len(input)]`; out-of-range start, `start > len(input)`, `start == len(input) + 1`, and negative `length` all silently return NULL. `length` is clamped to remaining when greater than `len(input) - start + 1`. NULL `replacement` deletes the range without inserting. Result type promotes input and replacement via the standard string-type promotion (nvarchar wins). Non-string `input` / `replacement` implicit-coerce to varchar via `StringScalars.CoerceToVarchar` (probe-confirmed: `STUFF(99, 2, 1, 99) = '999'`).
- **`QUOTENAME(name [, delim])`** returns `nvarchar(258)`. Supported delimiter chars: `[`/`]`, `(`/`)`, `<`/`>`, `{`/`}`, `"`, `'`, `` ` ``. The pair is selected by either side (probe-verified: `QUOTENAME('a)b', '(')` doubles `)` inside the body). Multi-char delimiter argument picks the first char. NULL input, NULL delimiter, unsupported delimiter character, and input > 128 chars all return NULL.
- **`REPLICATE(input, count)`** preserves the input's string type. Result truncates to 8000 bytes for non-MAX `varchar`/`nvarchar`; `varchar(MAX)` / `nvarchar(MAX)` / legacy LOB input bypass the cap. MAX detection runs at `Run` time off the runtime input value's type: a MAX-declared column or CAST target decodes to a max-form (length -1) / LOB string type that survives `StringScalars.CoerceToVarchar`, so `Replicate.IsMaxForm` reads it directly — no parse-time resolver needed, which is what lets a FROM-source `varchar(MAX)` column bypass the cap (probe-confirmed 2026-07-10: `DATALENGTH(REPLICATE(vmaxcol, 200))` = 20000, and the `nvarchar(MAX)` sibling = 40000, while bounded `varchar(20)` / `nvarchar(20)` columns and plain literals stay capped at 8000 — MAX-ness is a property of the declared type, per Microsoft's docs). Non-string `input` implicit-coerces to varchar via `StringScalars.CoerceToVarchar` (probe-confirmed: `REPLICATE(12345, 2) = '1234512345'`).
- **`SPACE(count)`** always returns `varchar` (never nvarchar), truncated to 8000 chars. NULL / negative count → NULL.
- **`FORMAT(value, format [, culture])`** returns `nvarchar`. Implementation routes through .NET's `IFormattable.ToString(format, culture)` on the underlying CLR value, matching SQL Server's CLR-passthrough shape. Accepted value types: numeric (integer / decimal / float / real / money / smallmoney) and date-time family (date / datetime / smalldatetime / datetime2 / datetimeoffset / time). Strings, bit, binary, uniqueidentifier, rowversion → Msg 8116 at runtime. NULL value → NULL; NULL format → Msg 8116 (probed: ordering doesn't matter — the format-NULL check fires first). Culture defaults to en-US; invalid culture name silently falls back to en-US. .NET `FormatException` (e.g. `decimal.ToString("D5")`) → NULL; unrecognized custom-format tokens that .NET passes through (e.g. `int.ToString("qq qq")`) are echoed verbatim.

## Known gap: out-of-`int`-range integer arguments

A length / position / count / code-point argument that exceeds `int` range surfaces a raw .NET `OverflowException` from the `int` narrowing instead of the function's normal result. Affected: `SUBSTRING` (length), `CHARINDEX` (start), `STUFF` (start / length — preempts the documented out-of-range-returns-NULL handling above), `REPLICATE` (count), `SPACE` (count), `CHOOSE` (index), `CHAR` / `NCHAR` (code point). `LEFT` / `RIGHT` (Msg 8115) and `DATEADD` (Msg 517) harden this argument; the rest don't. Left point-local deliberately: real SQL Server's response is per-function (clamp, compute as bigint, or a value-class error), so a single shared error wouldn't be faithful, and the trigger is pathological — no realistic query or EF emission passes a count / position outside `int` range. A value held in a variable could reach it; the failure is a clean abort, just not the SQL-Server-shaped one.

## EF.Functions-driven type-check / random scalars: `ISNUMERIC` / `ISDATE` / `RAND`
- **`ISNUMERIC(expression)`** returns `int` (1 / 0); NULL → 0 (not NULL). Famously lossy on real SQL Server: a bare sign / decimal point / comma / currency symbol returns 1, hex prefixes return 0, internal whitespace breaks the match. The simulator's hand-rolled scanner consumes (in order: optional sign and currency in either order; digit / decimal / comma run; optional `e`/`E`/`d`/`D` exponent requiring a leading digit AND a trailing digit after optional sign). At least one of {digit, decimal/comma, sign, currency} must have been consumed for the result to be true. Bit-typed input returns 0 even though bit lives in the Integer category (probe-confirmed). Anything that doesn't fully consume after trimming whitespace returns 0.
- **`ISDATE(expression)`** returns `int` (1 / 0) and validates against the legacy `datetime` range (1753-9999). Empty string short-circuits to 0 (the shared `TryParseLegacyDateTime` treats `""` as datetime base-date for CAST support, but ISDATE specifically rejects). Modern `date` / `time` / `datetimeoffset` raise Msg 8116 — ISDATE intentionally lives in the legacy datetime domain. Integer input is implicitly stringified and re-parsed (so `ISDATE(20260512)` = 1 via `'20260512'` matching `yyyyMMdd`; `ISDATE(1)` = 0 because `'1'` parses to year 1 < 1753). Float / decimal / non-integer-non-string types always return 0.
- **`RAND([seed])`** returns `float`. The defining behavior is the **runtime-constant** rule: a given `RAND(...)` call site produces ONE value reused across every row of the query — distinct call sites in the same projection each get their own constant. The simulator implements this by caching the first-evaluation result on the `Rand` expression instance; a fresh parse (each batch / statement) gets a fresh cache. Seeded form: any numeric / string-convertible seed coerces to `float`; the int passed to `new Random(int)` is XOR-folded from the 64-bit double's bits so small integer seeds (`1` vs `999999`) don't collapse to the same hash (their mantissas live in the high bits which a naive int cast would discard). Determinism per seed is preserved but the values aren't byte-identical to SQL Server's undocumented seed algorithm. NULL seed → NULL output.

## SOUNDEX-family + STR + TRANSLATE + STRING_ESCAPE

- **`SOUNDEX(s)`** (`Parser/Expressions/SoundexStrAdditions.cs`) — returns a 4-character `varchar` SOUNDEX code under the standard algorithm (first letter uppercased, then the consonant-digit map B/F/P/V=1, C/G/J/K/Q/S/X/Z=2, D/T=3, L=4, M/N=5, R=6, vowels/H/W skipped, runs of identical-code letters collapsed, padded with `0` or truncated to length 4). Empty input → `'0000'`. NULL → NULL.
- **`DIFFERENCE(s1, s2)`** — counts matching positions (0–4) between the two strings' SOUNDEX codes. Result `int`; NULL on either side → NULL.
- **`STR(float [, length [, decimals]])`** — right-aligned fixed-width numeric-to-string. Defaults: length 10, decimals 0 (rounds half-away-from-zero, doesn't truncate). Overflow (formatted value exceeds `length`) returns a string of `*` characters of length `length`. NULL → NULL. Always projects `varchar`.
- **`TRANSLATE(input, chars, translations)`** (`Parser/Expressions/StringScalarAdditions.cs`) — character-by-character substitution. The `chars` and `translations` arguments must have equal length; mismatch raises Msg 9819 via a dedicated `TranslateUnequalChars` factory. NULL on any operand → NULL. Result `nvarchar`.
- **`STRING_ESCAPE(text, 'json')`** — JSON-string escape pass on `text` (escapes `"` `\` `\b` `\f` `\n` `\r` `\t`, `/`, control chars as `\uXXXX`). Documentation says only `'json'` is a valid mode; the simulator accepts any string for the mode and treats it as `'json'` (real SQL Server raises Msg 9806 on unknown mode — minor divergence). NULL `text` → NULL. Result `nvarchar`.

## CHOOSE / IIF

- **`CHOOSE(index, val1, val2, ...)`** (`Parser/Expressions/Choose.cs`) — 1-based index into the trailing value list. Out-of-range (negative / zero / above the value count) → NULL. NULL `index` → NULL. Result type follows the standard CASE-style promotion across the value list (`SqlType.Promote` over all values). Sibling of `IIF`; both translate two-branch / multi-branch conditionals.

## Bit manipulation: `BIT_COUNT` / `GET_BIT` / `SET_BIT` / `LEFT_SHIFT` / `RIGHT_SHIFT`

Five integer scalars sharing one dispatch file (`Parser/Expressions/BitManipulation.cs`). All operate on `tinyint` / `smallint` / `int` / `bigint` input (bit-width 8 / 16 / 32 / 64) and either preserve the input type or return a fixed scalar. Bit positions are 0-based from the LSB. Out-of-range bit position raises **Msg 8120** (`BitFunctionPositionOutOfRange` factory). Non-integer input raises **Msg 8116** (`ArgumentDataTypeInvalidForBitFunction`).

- **`BIT_COUNT(num)`** — popcount, returns `int`.
- **`GET_BIT(num, index)`** — returns `bit` (0 / 1). NULL on either operand → NULL.
- **`SET_BIT(num, index [, value])`** — returns the input type with the bit at `index` set to `value` (defaults to 1). NULL on `num` / `index` → NULL.
- **`LEFT_SHIFT(num, n)`** — arithmetic left shift; high bits truncated when they overflow the input bit-width.
- **`RIGHT_SHIFT(num, n)`** — **logical** right shift (high bits zero-filled, probe-confirmed against SQL Server 2025 — diverges from C#'s `>>` on signed types which is arithmetic). Result type preserves input.

## CHECKSUM family

- **`CHECKSUM(args...)` / `BINARY_CHECKSUM(args...)`** (`Parser/Expressions/ChecksumAndRowVersion.cs`) — fast 32-bit fold over the argument list. Implementation uses FNV-1a; semantic guarantee matches SQL Server (same inputs → same checksum, deterministically). **Bit-pattern divergence**: real SQL Server uses an undocumented byte-mix; the simulator's FNV-1a output won't match real SQL Server bit-for-bit. Same-value-same-checksum invariant holds; same-multiset-same-checksum doesn't (CHECKSUM is order-sensitive, unlike CHECKSUM_AGG). Result `int`.
- **`CHECKSUM_AGG(expr)`** uses an order-independent XOR fold for the aggregate form — same multiset → same checksum, bit pattern won't match real SQL Server.
- **`APPROX_COUNT_DISTINCT(expr)`** is implemented as an exact `COUNT(DISTINCT expr)` — no HyperLogLog approximation, so results are exact rather than within real SQL Server's ~2% error bound.
- **`DATALENGTH(expr)`** returns `int` even for `varchar(MAX)` / `nvarchar(MAX)` and the legacy LOB family, where real SQL Server returns `bigint`. The value still fits in int for anything the simulator can produce; only the declared result type doesn't widen.

## `FORMATMESSAGE`

**`FORMATMESSAGE(msg_number_or_string, [param, ...])`** (`Parser/Expressions/FormatMessage.cs`) — printf-style message renderer, `nvarchar` result truncated to **2047 characters** (probe-confirmed via `DATALENGTH`). The formatter is a C-runtime `printf` subset (not `.NET string.Format`), distinct from the RAISERROR-path `Parser/MessageFormatter.cs` because FORMATMESSAGE's error handling differs (see below).

- **Specifier grammar** `%[flags][width][.precision][length]type`: flags `- + 0 # (space)`; width is a digit run or `*` (consumes an int argument); precision `.digits` (min digits for integers, max chars for `%s`); length `l`/`ll`/`h`/`hh` (ignored) or `I64` (bigint); type `s d i u o x X`. `%%` emits a literal `%`. All forms probe-confirmed: `%5d`→right-pad, `%-5d`→left, `%05d`→zero-pad, `%+d`→forced sign, `%#x`→`0x` prefix, `%.3d`→`005`, `%*d`→arg-driven width, `%u` of `-1`→`4294967295`, `%o`→octal, `%X`→uppercase hex.
- **Argument handling**: a NULL argument or a specifier with no argument renders the literal text `(null)`; extra arguments are ignored; a NULL format string → NULL.
- **Error handling** (the key divergence from RAISERROR, which throws): only a *consumed* argument whose type is fundamentally disallowed — anything but the integer family excluding `bit`, the string family, or the binary family — raises **Msg 2748** (`"Cannot specify <type> data type (parameter N) as a substitution parameter."`, probe-confirmed verbatim). Every other failure — a supported-but-mismatched argument (int into `%s`, string/binary into `%d`, bigint into a 32-bit specifier, int into `%I64d`), a malformed specifier, or an **empty format string** — does *not* throw; the whole result becomes SQL Server's terse in-server formatting-error diagnostic (`"Error: 50000, Severity: -1, State: 1. (Params:). The error is printed in terse mode…"` + trailing CRLF), captured byte-exact and returned as data.
- **`msg_id` overload not backed by `sys.messages`**: a numeric first argument (user id ≥50000 or any system id) returns **NULL**, matching real SQL Server's behavior for an *unknown* id. Known system-message text isn't modeled (no `sys.messages`).
- **Divergence**: a scale-0 `numeric`/`decimal` argument is a valid substitution type on real SQL Server (then fails formatting → terse diagnostic); the simulator raises Msg 2748 for any decimal/numeric regardless of scale.

## Password hashing: `PWDENCRYPT` / `PWDCOMPARE`

`Parser/Expressions/PasswordHashing.cs`. The hash layout reproduces SQL Server's real on-disk format, so **simulator-generated hashes verify against a live server and vice versa** (both directions probe-confirmed against SQL Server 2025). Layout: 2-byte big-endian version tag, 4-byte random salt, then the derived key.

- **`PWDENCRYPT(clear)`** — always emits the current SQL Server 2025 **`0x0300`** format: `0x0300 || salt(4) || PBKDF2-HMAC-SHA512(UTF-16LE(clear), salt, 100000 iterations, 64 bytes)` = **70 bytes** `varbinary` (probe-confirmed `DATALENGTH` 70). A fresh random salt per call, so successive hashes of the same password differ. NULL → NULL. A clear over **128 characters** raises **Msg 6607 Cls 16 St 5** `Password Encryption: The value supplied for parameter number 1 is invalid.` — probe-confirmed at exactly the 128/129 boundary, same shape for varchar and nvarchar input. (A `varchar(max)`-typed oversized input errs as Msg 8152 on real via parameter coercion — unmodeled; the simulator raises 6607 for every oversized input.) (Undocumented sibling of `PWDCOMPARE`.)
- **`PWDCOMPARE(clear, hash [, version])`** — `int` `1`/`0` for match/mismatch. Recognizes both `0x0300` (PBKDF2, current) and legacy `0x0200` (single-pass `SHA-512(UTF-16LE(clear) || salt)`, SQL Server 2012–2022) by reading the version tag, so it verifies hashes from any of those engines. NULL `clear` or NULL `hash` → NULL; a short / malformed / unrecognized-version hash → 0 (probe-confirmed, no error). A clear over 128 characters → 0 without hashing: real compares in full rather than truncating (probe-confirmed 0 for a 129-char clear against its own 128-char prefix's hash), and no genuine hash of one exists; real's Msg 8152 for much larger clears (an internal parameter-coercion boundary somewhere in 130–8000) is unmodeled — the simulator returns 0 there. The optional third `version` argument (legacy upgrade hint) is accepted and ignored — real SQL Server ignores it for comparison too. The 128-char bound is what lets the shared `PasswordHash` hashing paths stackalloc unconditionally.

## `LOGINPROPERTY`

**`LOGINPROPERTY(login_name, property_name)`** (`Parser/Expressions/LoginProperty.cs`) — resolves the single fixed login (`dbo`, the placeholder `SUSER_NAME` reports) plus any login registered via `CREATE LOGIN` (see [`permissions.md`](permissions.md)); any other name behaves like a nonexistent login and returns **NULL** for every property (probe-confirmed: nonexistent login → NULL across the board). NULL login / NULL property / unrecognized property → NULL. Property names case-insensitive. Values are plausible constants matching the live probe's shape: `PasswordLastSetTime` → the login's actual password-set stamp for a registered login, a fixed seed date `2020-01-01 00:00:00.000` for `dbo`; `BadPasswordTime` / `LockoutTime` → the `1900-01-01` "never" sentinel; `BadPasswordCount` / `HistoryLength` / `IsExpired` / `IsLocked` / `IsMustChange` → `0`; `DaysUntilExpiration` / `PasswordHash` / `PasswordHashAlgorithm` → NULL (a low-privilege login sees NULL for the hash on the live server too, matching what the simulator exposes); `DefaultDatabase` → the session's current database; `DefaultLanguage` → `us_english`. **Divergence**: real SQL Server projects each property as `sql_variant` with a per-property base type (`datetime` / `int` / `nvarchar` / `varbinary`); the simulator doesn't model `sql_variant`, so — following `SERVERPROPERTY` — every value surfaces as `nvarchar`, reached through implicit conversion when a caller casts.

## Session / connection placeholders

Constants whose values don't carry real session/server identity in the simulator — they exist for SQL emitted by tooling that reads them (DACFx / EF Core / migration scripts) to receive a sensible non-NULL response.

- **`HOST_NAME()`** — returns `''`.
- **`APP_NAME()`** — returns `''`.
- **`ORIGINAL_DB_NAME()`** — returns `Simulation.DefaultDatabaseName` (`"simulated"`).
- **`GETANSINULL([db])`** — returns 1 (the simulator's ANSI-NULL behavior matches `SET ANSI_NULLS ON`, which is the only modeled mode).
- **`@@DATEFIRST`** — constant 7 (Sunday). `SET DATEFIRST` parses-and-discards.
- **`@@MAX_PRECISION`** — constant 38.
- **`@@MAX_CONNECTIONS`** — constant 32767.
- **`@@SERVERNAME`** — `"SIMULATED"`.
- **`@@SERVICENAME`** — `"MSSQLSERVER"`.
- **`@@LANGID`** — 0.
- **`@@LANGUAGE`** — `"us_english"`.
- **`@@TEXTSIZE`** — -1 (matches SQL Server's documented default).
- **`@@OPTIONS`** — 5432 (composite of ANSI/ARITHABORT/QUOTED_IDENTIFIER/CONCAT_NULL_YIELDS_NULL flags matching the simulator's defaults).
- **`@@VERSION`** — `"SQL Server Simulator"`.
- **`@@MICROSOFTVERSION`** — int `0x11000000` (285212672), the `(major << 24) | (minor << 16) | build` packing of version `17.0.0`, self-consistent with `SERVERPROPERTY('ProductVersion')` = `"17.0.0.0"`. The deliberately 0-build version doubles as an honest "not a real SQL Server build" marker; probed harmless to SSMS's Object Explorer (the Databases node populates regardless — the enumeration gate was ntext RPC-parameter support, not the reported version).
- **`@@REMSERVER`** — NULL (deprecated in SQL Server proper too).

Server-instance metadata accessed via **`SERVERPROPERTY(name)`** — see [`catalog-views.md`](catalog-views.md).

## Session-state store: `SESSION_CONTEXT` / `CONTEXT_INFO` / connection scalars

These carry real per-session state on `SimulatedDbConnection` (not placeholder constants), so values persist across batches on the same connection and reset with a new connection.

- **`sp_set_session_context @key, @value [, @read_only]`** + **`SESSION_CONTEXT(N'key')`** — per-session key/value store (backs multi-tenant / row-level-security patterns). Named and positional argument forms both work. Keys are **case-sensitive** (ordinal — `TenantId` ≠ `tenantid`, matching SQL Server's binary key comparison regardless of database collation). A missing key reads as NULL; a NULL key argument to `SESSION_CONTEXT` raises **Msg 8116** (`session_context` lowercase in the wording). `sp_set_session_context` with a NULL `@key` raises **Msg 225**; re-setting a key previously stored with `@read_only = 1` raises **Msg 15664**. Real SQL Server preserves the stored value's type via `sql_variant`; the simulator has no `sql_variant`, so `SESSION_CONTEXT` surfaces the value as **nvarchar** (the same proxy `SERVERPROPERTY` uses). The common `WHERE int_col = SESSION_CONTEXT(N'key')` shape still works because the comparison path coerces the nvarchar probe to the column's type.
- **`CONTEXT_INFO()`** + **`SET CONTEXT_INFO <binary>`** — the legacy single 128-byte slot. NULL until set; once set, SQL Server stores exactly 128 bytes (right-padded / truncated), so `DATALENGTH(CONTEXT_INFO())` is always 128 afterward. Only the literal-binary `SET` form is modeled — a `@var` value side isn't accepted by the SET value parser.
- **`CONNECTIONPROPERTY(name)`** — nvarchar proxy (sql_variant in real). Probe-confirmed `net_transport` = `'TCP'`, `protocol_type` = `'TSQL'`; `auth_scheme` / `physical_net_transport` report placeholder constants; address/port properties and unknown names return NULL.
- **`CURRENT_TRANSACTION_ID()`** — bigint, approximated by the database's monotonic commit counter (a plausible increasing value, not a stable per-transaction id — apps use it for correlation, not correctness).
- **`CURRENT_REQUEST_ID()`** — int, returns 0 (the simulator doesn't multiplex requests per session; probe-confirmed value for a single-request session).

**`SESSION_ID()` is deliberately not modeled** — it's not a box-product function (raises Msg 195 on SQL Server 2025; it's a dedicated-SQL-pool / cloud surface). `@@SPID` is the box session-id mechanism.

## `COLLATIONPROPERTY(collation_name, property)`

`Parser/Expressions/CollationProperty.cs`: metadata for a collation. SSMS's Object-Explorer per-database follow-up runs `COLLATIONPROPERTY((select collation_name from sys.databases where name = …), 'CodePage')`. Real SQL Server projects `sql_variant` with a per-property base type; the simulator doesn't model sql_variant, so it surfaces the **bare** base type — `CodePage` / `LCID` / `ComparisonStyle` / `Version` as `int`, `Name` as `nvarchar` (the same substitution `SERVERPROPERTY` uses). The base type flows to the projection schema only when the property-name argument is a compile-time constant; a non-constant name falls back to `nvarchar` with a runtime coerce (static/runtime parity). An **unrecognized collation name** or an **unknown property** returns NULL (matches the reference). Property names are case-insensitive.

Values derive from the collation model (`Collation.TryGetMetrics`) so any recognized name resolves — the name is re-walked into its prefix / suffix-flags / version / code-page token. Probe-confirmed against SQL Server 2025 (2026-07-14): `SQL_Latin1_General_CP1_CI_AS` → CodePage 1252, LCID 1033, ComparisonStyle 196609, Version 0, Name `SQL_Latin1_General_CP1_CI_AS`.

- **CodePage** — the ANSI code page. `_UTF8` names → 65001; SQL_\* names read their `CPnnn` name token (CP1 → 1252); Windows names come from the probe-built prefix registry (`Japanese*` → 932, `Latin1_General*` → 1252). *The simulator stores all non-UTF8 varchar as CP1252, so `StorageEncoding.CodePage` is not the source — the token/registry lookup is.*
- **LCID** — from the probe-built prefix registry (`SQL_Latin1_General` / `Latin1_General` → 0x0409 = 1033, `Japanese` → 0x0411 = 1041); defaults to 0x0409 for a recognized prefix that isn't tabulated. *Known minor divergences: sort-variant prefixes with a distinct sort-order LCID and the CP1254 SQL_Latin1 members fall back to the base-prefix LCID.*
- **ComparisonStyle** — derived from the suffix flags: binary (`_BIN` / `_BIN2`) → 0, else `ignore-case (0x1 when CI) + ignore-accent (0x2 when AI) + ignore-kana (0x10000 unless KS) + ignore-width (0x20000 unless WS)` (CI_AS → 196609, CI_AI → 196611, CS_AS → 196608, CI_AS_KS_WS → 1).
- **Version** — the version ordinal from the numeric name token: unversioned / SQL_\* → 0, 90 → 1, 100 → 2, 140 → 3, 160 → 4.
- **Name** — the collation's canonical name.

## Built-in TVF: `STRING_SPLIT`
`STRING_SPLIT(input, separator [, enable_ordinal])` dispatches in `ParseSingleFromSource` alongside `OPENJSON` — case-insensitive name match before generic name resolution. Yields one row per substring split on the single-character separator.

- Schema is decided at parse time: 2-arg form projects `(value <input-string-type>)`; 3-arg form with literal `enable_ordinal = 1` adds `ordinal bigint`. `enable_ordinal = 0` or NULL collapses back to the 2-arg schema. The third argument must be a parse-time-constant integer expression (the schema is shape-fixed at compile time). The gate first walks the arg for any variable via `Expression.ContainsVariableReference` — **every** variable-bearing shape raises Msg 8748, not only a bare `@v` (probe-confirmed 2026-07-10: `cast(@v as int)`, `@v + 0`, and `(@v)` all reject) — then evaluates against an empty resolver to catch column references. Constant shapes with no variable are accepted (probe-confirmed: `cast(1 as int)`, `(1)`, `1 + 0` all add the ordinal column). `ContainsVariableReference` recurses through the common containers (`VariableReference` / `Parenthesized` / `Cast` / `TwoSidedExpression`); a variable buried in a less-common container is a residual coverage gap.
- NULL `input` → zero rows; empty `input` → one row with empty value (and ordinal 1 in the ordinal-enabled form).
- NULL / empty / multi-character `separator` → Msg 214 at runtime (probe-confirmed: validated before the input — NULL sep raises 214 even when input is also NULL).
- Non-int third argument → Msg 8116; `enable_ordinal` literal outside {0, 1, NULL} → Msg 4199.
- Composes with `CROSS APPLY` / `OUTER APPLY` via the lateral-dispatch fast path: `ParseLateralFromSource` recognizes `STRING_SPLIT` (and `OPENJSON`) by name and routes back through `ParseSingleFromSource` with the chained outer-type resolver that includes left-side sources (so `STRING_SPLIT(t.col, ',')` correctly resolves `t.col` against the APPLY's left side).
- Input column type determines the `value` column's string family at parse time (`varchar` → `varchar`; `nvarchar` → `nvarchar`); non-string input maps to `nvarchar`. The value column inherits MAX-ness from the input's parse-time `GetSqlType` against the outer-type resolver (`ParseStringSplit`).

## Built-in TVF: `GENERATE_SERIES`
`GENERATE_SERIES(start, stop [, step])` (SQL Server 2022+) — third sibling of `STRING_SPLIT` / `OPENJSON` in the `ParseSingleFromSource` dispatch (and the `ParseLateralFromSource` allowlist, so `CROSS APPLY GENERATE_SERIES(1, t.n)` lateral-correlates correctly). Projects a single column named `value`. Probe-confirmed against SQL Server 2025 (2026-05-23).

- Allowed arg types: `tinyint`, `smallint`, `int`, `bigint`, `decimal` / `numeric`. Anything else (`float`, `real`, `money`, `varchar`, `date`, …) raises **Msg 8116** at parse, with verbatim wording `Argument data type <type> is invalid for argument <N> of generate_series function`.
- All three args must share the same type. Integer subtypes are distinct (`int` + `bigint` raises **Msg 5373**); `decimal` / `numeric` collapse to one family and tolerate differing precision / scale (unified via `SqlType.Promote`, so DECIMAL(10,1) + DECIMAL(10,2) projects DECIMAL with the wider scale).
- Output column type tracks the input type — `tinyint` args project `tinyint`, decimal args project decimal with the unified precision / scale.
- Step omitted: defaults to `-1` when `start > stop`, else `1` — so `GENERATE_SERIES(5, 1)` yields the descending sequence `5, 4, 3, 2, 1` (probe-confirmed; matches Microsoft's docs).
- Wrong-direction step (positive step with `start > stop`, or negative step with `start < stop`) → empty rowset, no error. Step `= 0` → **Msg 4199** (`Argument value 0 is invalid for argument 3 of generate_series function`).
- Any NULL arg → empty rowset (no error, no row). Bare untyped `NULL` is also accepted — the column type is inferred from the non-NULL siblings.
- Fewer than 2 args → **Msg 313**; more than 3 → **Msg 8144**. (Real server raises the procedure-shaped error numbers even though `GENERATE_SERIES` is a TVF; verbatim wording probed.)
- Internal generation uses `long` arithmetic for integer types and `decimal` for the decimal family. `bigint` near `MAX_INT64` terminates via the overflow-edge check (`cur > long.MaxValue - step`) before the addition would wrap, so `GENERATE_SERIES(MAX_INT-7, MAX_INT, 3)` yields three rows just like real SQL Server.
