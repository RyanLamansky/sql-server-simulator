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

## Char-code scalars: `ASCII` / `UNICODE` / `CHAR` / `NCHAR`
Basic one-arg conversions between a character and its code point.

- **`ASCII(input)`** returns `int`. Reads the first character of `input` and returns its CP1252 byte value. NULL → NULL; empty string → NULL. Unicode input is CP1252-encoded first, so `ASCII(N'€')` returns 128 (CP1252's `€`); unrepresentable Unicode (emoji etc.) returns 63 via the encoder's `'?'` replacement fallback. Non-string inputs implicitly stringify *before* the first-char read, so `ASCII(65)` is 54 (the byte for `'6'`, the first char of `"65"`), not 65.
- **`UNICODE(input)`** returns `int`. Same input-handling shape as `ASCII`, but reads the .NET `char` directly rather than CP1252-encoding it. Supplementary code points (above U+FFFF, e.g. `N'😀'`) return the high surrogate value (55357 for 😀) under the non-SC default collation — not the full Unicode code point. An SC-aware variant returning 128512 would need explicit collation modeling; matches the simulator's "default collation only" stance.
- **`CHAR(code)`** returns `char(1)` (not `varchar(1)` — probe-confirmed via `sql_variant_property(CHAR(65), 'basetype')`). NULL → NULL; out-of-range (negative / > 255) → NULL. Non-integer inputs truncate-to-int (`CHAR(65.7)` → `'A'`, `CHAR('65')` → `'A'`). `CHAR(0)` is a valid NUL character with `DATALENGTH = 1`, not NULL.
- **`NCHAR(code)`** returns `nchar(1)`. NULL / out-of-range (negative, > 65535) → NULL. Supplementary code points like `NCHAR(128512)` (😀) return NULL rather than emitting a surrogate pair — non-SC collation behavior.

## EF.Functions-driven string scalars: `PATINDEX` / `STUFF` / `QUOTENAME` / `REPLICATE` / `SPACE` / `FORMAT`
Bundle that fills out the raw-SQL string surface that EF's `FromSqlInterpolated` and `DefaultValueSql` workloads commonly reach. None of these are exposed as `EF.Functions.X` LINQ extensions; coverage targets raw-SQL paths.

- **`PATINDEX(pattern, subject)`** shares the LIKE wildcard compiler via `LikePatternBuilder` (single source of truth for `%`/`_`/`[...]`). Anchoring is decided by leading / trailing `%` in the pattern: a leading `%` strips the start anchor (find-anywhere); a trailing `%` strips the end anchor; without either, the pattern is anchored at both ends and only a full-subject match returns 1. Leading and trailing `%` characters are consumed by the anchoring decision and don't translate to `.*` in the regex body — that's what makes `PATINDEX('%abc%', 'xabcx')` return 2 (position of `abc`) rather than 1 (position of the empty `.*` prefix). Subject NULL raises Msg 8116 (asymmetric with NULL pattern, which silently returns NULL). Subject non-string raises Msg 8116; pattern non-string implicitly coerces to the subject's string family. Result type is `int` for bounded subjects and `bigint` for `varchar(MAX)` / `nvarchar(MAX)` / legacy LOB family. No `ESCAPE` clause (Msg 156 at parse, falls out of the general grammar).
- **`STUFF(input, start, length, replacement)`** uses 1-based `start` ∈ `[1, len(input)]`; out-of-range start, `start > len(input)`, `start == len(input) + 1`, and negative `length` all silently return NULL. `length` is clamped to remaining when greater than `len(input) - start + 1`. NULL `replacement` deletes the range without inserting. Result type promotes input and replacement via the standard string-type promotion (nvarchar wins).
- **`QUOTENAME(name [, delim])`** returns `nvarchar(258)`. Supported delimiter chars: `[`/`]`, `(`/`)`, `<`/`>`, `{`/`}`, `"`, `'`, `` ` ``. The pair is selected by either side (probe-verified: `QUOTENAME('a)b', '(')` doubles `)` inside the body). Multi-char delimiter argument picks the first char. NULL input, NULL delimiter, unsupported delimiter character, and input > 128 chars all return NULL.
- **`REPLICATE(input, count)`** preserves the input's string type. Result truncates to 8000 bytes for non-MAX `varchar`/`nvarchar`; `varchar(MAX)` / `nvarchar(MAX)` / legacy LOB input bypass the cap. MAX detection runs at parse time via `Expression.GetSqlType` against the outer-type resolver — the simulator's runtime `SqlValue` doesn't carry the MAX-vs-bounded distinction, so the parse-time capture is the only signal. Falls back to "treat as bounded" when the input is a column reference the parse-time resolver can't reach; EF's REPLICATE emissions pass literal counts and literal / variable strings, so this covers the practical surface.
- **`SPACE(count)`** always returns `varchar` (never nvarchar), truncated to 8000 chars. NULL / negative count → NULL.
- **`FORMAT(value, format [, culture])`** returns `nvarchar`. Implementation routes through .NET's `IFormattable.ToString(format, culture)` on the underlying CLR value, matching SQL Server's CLR-passthrough shape. Accepted value types: numeric (integer / decimal / float / real / money / smallmoney) and date-time family (date / datetime / smalldatetime / datetime2 / datetimeoffset / time). Strings, bit, binary, uniqueidentifier, rowversion → Msg 8116 at runtime. NULL value → NULL; NULL format → Msg 8116 (probed: ordering doesn't matter — the format-NULL check fires first). Culture defaults to en-US; invalid culture name silently falls back to en-US. .NET `FormatException` (e.g. `decimal.ToString("D5")`) → NULL; unrecognized custom-format tokens that .NET passes through (e.g. `int.ToString("qq qq")`) are echoed verbatim.

## EF.Functions-driven type-check / random scalars: `ISNUMERIC` / `ISDATE` / `RAND`
- **`ISNUMERIC(expression)`** returns `int` (1 / 0); NULL → 0 (not NULL). Famously lossy on real SQL Server: a bare sign / decimal point / comma / currency symbol returns 1, hex prefixes return 0, internal whitespace breaks the match. The simulator's hand-rolled scanner consumes (in order: optional sign and currency in either order; digit / decimal / comma run; optional `e`/`E`/`d`/`D` exponent requiring a leading digit AND a trailing digit after optional sign). At least one of {digit, decimal/comma, sign, currency} must have been consumed for the result to be true. Bit-typed input returns 0 even though bit lives in the Integer category (probe-confirmed). Anything that doesn't fully consume after trimming whitespace returns 0.
- **`ISDATE(expression)`** returns `int` (1 / 0) and validates against the legacy `datetime` range (1753-9999). Empty string short-circuits to 0 (the shared `TryParseLegacyDateTime` treats `""` as datetime base-date for CAST support, but ISDATE specifically rejects). Modern `date` / `time` / `datetimeoffset` raise Msg 8116 — ISDATE intentionally lives in the legacy datetime domain. Integer input is implicitly stringified and re-parsed (so `ISDATE(20260512)` = 1 via `'20260512'` matching `yyyyMMdd`; `ISDATE(1)` = 0 because `'1'` parses to year 1 < 1753). Float / decimal / non-integer-non-string types always return 0.
- **`RAND([seed])`** returns `float`. The defining behavior is the **runtime-constant** rule: a given `RAND(...)` call site produces ONE value reused across every row of the query — distinct call sites in the same projection each get their own constant. The simulator implements this by caching the first-evaluation result on the `Rand` expression instance; a fresh parse (each batch / statement) gets a fresh cache. Seeded form: any numeric / string-convertible seed coerces to `float`; the int passed to `new Random(int)` is XOR-folded from the 64-bit double's bits so small integer seeds (`1` vs `999999`) don't collapse to the same hash (their mantissas live in the high bits which a naive int cast would discard). Determinism per seed is preserved but the values aren't byte-identical to SQL Server's undocumented seed algorithm. NULL seed → NULL output.

## Built-in TVF: `STRING_SPLIT`
`STRING_SPLIT(input, separator [, enable_ordinal])` dispatches in `ParseSingleFromSource` alongside `OPENJSON` — case-insensitive name match before generic name resolution. Yields one row per substring split on the single-character separator.

- Schema is decided at parse time: 2-arg form projects `(value <input-string-type>)`; 3-arg form with literal `enable_ordinal = 1` adds `ordinal bigint`. `enable_ordinal = 0` or NULL collapses back to the 2-arg schema. The third argument must be a parse-time-constant integer expression — column / parameter references raise `NotSupportedException`; real SQL Server's grammar enforces the same constraint (the schema is shape-fixed at compile time).
- NULL `input` → zero rows; empty `input` → one row with empty value (and ordinal 1 in the ordinal-enabled form).
- NULL / empty / multi-character `separator` → Msg 214 at runtime (probe-confirmed: validated before the input — NULL sep raises 214 even when input is also NULL).
- Non-int third argument → Msg 8116; `enable_ordinal` literal outside {0, 1, NULL} → Msg 4199.
- Composes with `CROSS APPLY` / `OUTER APPLY` via the lateral-dispatch fast path: `ParseLateralFromSource` recognizes `STRING_SPLIT` (and `OPENJSON`) by name and routes back through `ParseSingleFromSource` with the chained outer-type resolver that includes left-side sources (so `STRING_SPLIT(t.col, ',')` correctly resolves `t.col` against the APPLY's left side).
- Input column type determines the `value` column's string family at parse time (`varchar` → `varchar`; `nvarchar` → `nvarchar`); non-string input maps to `nvarchar`. The value column inherits MAX-ness through the same parse-time-resolver mechanism `REPLICATE` uses.
