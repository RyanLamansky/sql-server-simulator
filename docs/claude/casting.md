# CAST / CONVERT family

## CAST/CONVERT to narrow `varchar` / `nvarchar` / `varbinary`
Per-source-category rule applied after `SqlValue.CoerceTo`:
- String / varbinary / date-time-family source → silent truncation.
  `CAST('hello world' AS varchar(5))` → `'hello'`.
- `tinyint`/`smallint`/`int` source → `varchar` too narrow → asterisk fallback (`'*'`).
  Quirk specific to `varchar`; `nvarchar` raises Msg 8115.
  `bigint` doesn't get fallback either.
- `decimal`/`numeric` source → Msg 8115 with "numeric" wording (distinct from int/bigint's "expression" wording).
- `money`/`smallmoney` → Msg 234 (`"There is insufficient result space to convert a money value to <target>."` — "money" regardless of source variant).
- `float`/`real` → Msg 232 with formatted source value (F6).
- `uniqueidentifier`: pre-CoerceTo branch (Msg 8170 char/varchar, Msg 8115 nchar/nvarchar) — fires only for a *bounded* target under 36 chars; a MAX target (length sentinel -1) has unbounded width and holds the 36-char dashed form, so the check guards `max is >= 0 and < 36` (a plain `< 36` treated the sentinel as too-narrow and wrongly raised Msg 8115 on `CAST(newid() AS nvarchar(max))` — tiberius-surfaced).
- `datetimeoffset → varchar` too narrow: real SQL Server raises Msg 241; simulator silently truncates (niche).

**CAST/CONVERT context defaults missing length to 30** for `varchar`/`nvarchar`/`varbinary` (column-context default is 1).

## `float`/`real` → `decimal`/`numeric`

A permitted conversion (implicit and explicit), **not** the Msg 529 explicit-conversion rejection — `CoerceToDecimal` converts through .NET `decimal` (rounding half-away-from-zero to the target scale; `real` converts from its own 4-byte value, keeping the ~7-significant-digit representation real does).
An out-of-range magnitude (NaN / ±Infinity / past `decimal`'s range, or more integer digits than the target holds) raises Msg 8115 arithmetic overflow.
Load-bearing for ODBC / pyodbc callers, which bind a Python/CLR `float` parameter as `float`: a decimal-column insert (e.g. SQLAlchemy's) arrives as a float-to-decimal assignment, which previously hit the wrong Msg 529 path.

## `PARSE` / `TRY_PARSE` (culture-aware conversion)

`PARSE(string AS type [USING culture])` / `TRY_PARSE(...)` — culture-aware Convert.NET surface (`Parser/Expressions/ParseFunction.cs`).
The string argument routes through .NET's `<Type>.Parse(string, CultureInfo)` rather than the simulator's existing CAST machinery, so the accepted formats follow the CLR's culture rules (commas vs dots, locale-specific date orderings) rather than SQL Server's CAST grammar.
Culture defaults to `en-US` when the USING clause is omitted; unknown culture name raises Msg 9819 (probe-confirmed) via a dedicated `ParseConversionFailed` factory.
`PARSE` re-raises any `FormatException` / `OverflowException` as Msg 9819 with the source value embedded; `TRY_PARSE` catches the same set and returns NULL.

Accepted target types: `int` / `bigint` / `smallint` / `tinyint` / `decimal(p, s)` / `numeric(p, s)` / `float` / `real` / `money` / `smallmoney` / `bit` / `date` / `datetime` / `datetime2(N)` / `smalldatetime` / `datetimeoffset(N)` / `time(N)` / `uniqueidentifier`.
String targets (`varchar` / `nvarchar` / `char` / `nchar`) raise Msg 9819 since PARSE only handles parsing INTO a non-string type — matches real SQL Server's rejection.

NULL input → NULL (both forms).
Result type: the requested target type with declared precision / scale preserved.

## `varbinary` → date-time family (SSMS wire format)

`CAST(0x… AS date | time | datetime | datetime2 | datetimeoffset | smalldatetime)` decodes the byte payload via SQL Server's documented wire format.
SSMS bulk-INSERT exports emit every date column literal this way (e.g. `CAST(0x07A00627C0A5173D0B0000 AS DateTimeOffset)`), so this path is load-bearing for BACPAC-style seed-data scripts.
Layouts probed against SQL Server 2025:

- `date` — 3 bytes LE: days since `0001-01-01`.
- `time(N)` — 1 scale byte + LE time count in `10^(-N)`-second units; 3 / 4 / 5 bytes for scales 0–2 / 3–4 / 5–7.
- `datetime2(N)` — scale + LE time + LE 3-byte date.
- `datetimeoffset(N)` — same as `datetime2(N)` + LE `int16` offset minutes.
  SQL Server stores the time + date in **UTC**; the offset shifts back to the original wall-clock during round-trip.
- `datetime` — 8 bytes **BE**: `int32` days since `1900-01-01` + `uint32` 1/300-second ticks since midnight.
- `smalldatetime` — 4 bytes **BE**: `uint16` days + `uint16` minutes.

Decoders live next to `VarbinaryToGuid` in `Storage/SqlValue.Coerce.cs`.
Reverse direction (date-family → varbinary) isn't modeled — no production scripts emit that direction; `bcp` and BACPAC do the encoding upstream.

## String ↔ binary CAST

Both directions are in `SqlValue.CoerceTo` (style 0, the default CAST form):

- **`varbinary`/`binary` → `varchar`/`nvarchar`** reinterprets each byte through the target's encoding (the collation's ANSI code page for varchar/char — CP1252 on the default — and UTF-16 LE for nvarchar/nchar).
  Probe-confirmed: `CAST(0x414243 AS varchar(10))` → `'ABC'`.
  `image` source is rejected: the explicit CAST `image → varchar/nvarchar/char/nchar` raises **Msg 529** (`"Explicit conversion from data type image to <target> is not allowed."`, tiberius-surfaced), while the implicit-coerce path (`LEN(image)` etc.) raises Msg 8116.
  The Msg 529 target renders the `(max)` suffix for a MAX target but drops a bounded declared length to the root name (`nvarchar(max)` vs `nvarchar`) — real's rendering, matched by `FamilyRootName`'s MAX-form arms.
- **`varchar`/`char`/`nvarchar`/`nchar` → `varbinary`/`binary`** encodes the string with the source's natural encoding — the source collation's ANSI code page for varchar/char, UTF-16 LE for nvarchar/nchar/ntext/sysname — so the bytes agree with `DATALENGTH` over the same expression.
  Not ISO-8859-1: it differs from CP1252 across 0x80-0x9F and best-fit-folds (`€` → `?`, `Š` → `S`), which real never does.
  See [`collations.md`](collations.md#storage-code-page).
  `varbinary(N)` receives the raw bytes and the CAST-level path truncates to N.
  `binary(N)` routes through `FromBinary` for zero-pad-or-truncate.
  Probe-confirmed: `CAST('abc' AS varbinary(10))` → `0x616263`, `CAST('abc' AS binary(10))` → `0x61626300000000000000`, `CAST(N'abc' AS varbinary(10))` → `0x610062006300`.
  Hex-string CAST forms (`CONVERT(varbinary, '0x010203', 1)` / `style 2`) still route through `CoerceStringToBinaryWithStyle`.

`VarcharSqlType`/`NVarcharSqlType`/`VarbinarySqlType` are per-length singletons via `Get(N)` (parallel to `CharSqlType`); `Unspecified` (length 0) is the runtime sentinel; `MaxForm` (length -1) is the LOB form.
**Equality**: `value.Type == SqlType.Varchar` is true only for the unspecified form; "is any varchar" needs `is VarcharSqlType`.
The encoder accepts any same-family pair regardless of length (write-time truncation enforced upstream).

## Binary ↔ integer / money CAST

Both directions live in `SqlValue.CoerceTo` (probe-confirmed against SQL Server 2025).
This is what makes SSMS's connect queries (`CAST(0x0001 AS int)`, `(@@microsoftversion / 0x1000000) & 0xff`) and hex `nchar(0x41)` resolve.

| Source → target | Rule |
| --- | --- |
| `binary`/`varbinary` → `bit`/`tinyint`/`smallint`/`int`/`bigint` | Big-endian; **left-truncate** to the target width (keep the rightmost bytes), zero-fill high bytes when shorter, read two's-complement. **Silent — never overflows.** `cast(0x0102 as int)`=258, `cast(0x0102030405 as int)`=33752069, `cast(0xFF01 as tinyint)`=1 (no Msg 244), `cast(0xFFFFFFFF as int)`=-1, `cast(0x as int)`=0. `bit` tests the final byte for non-zero (`cast(0x0100 as bit)`=0, `cast(0x01 as bit)`=1). |
| `binary`/`varbinary` → `money`/`smallmoney` | Rightmost 8 (money) / 4 (smallmoney) bytes = raw **scale-4 units**, big-endian two's-complement ÷ 10000. `cast(0x01 as money)`=0.0001, `cast(0x01 as smallmoney)`=0.0001. |
| `binary`/`varbinary` → `decimal`/`numeric` | **Msg 8114** (`"Error converting data type varbinary to numeric."`, class 16 state 5) — *not* the Msg 529 used elsewhere. `TRY_CAST` swallows it to NULL. |
| `binary`/`varbinary` → `float`/`real` | **Msg 529** (`"Explicit conversion from data type varbinary to float is not allowed."`, class 16 state 1) — via `CoerceToApproximate`'s default arm. NOT swallowed by `TRY_CAST` (`try_cast(0x41 as float)` still raises 529). |
| `bit`/`tinyint`/`smallint`/`int`/`bigint` → `binary(N)` | Native-width big-endian two's-complement (bit/tinyint→1, smallint→2, int→4, bigint→8), then **left-zero-pad or left-truncate to exactly N** (fixed width). `cast(258 as binary(4))`=`0x00000102`, `cast(258 as binary(1))`=`0x02`, `cast(-1 as binary(4))`=`0xFFFFFFFF`, `cast(258 as binary)`=30 zero-padded bytes (CAST default length 30). |
| `bit`/`tinyint`/`smallint`/`int`/`bigint` → `varbinary(N)` | Native-width bytes, **left-truncated only when N < native, never left-padded** (variable width). `cast(258 as varbinary(4))`=`0x00000102`, `cast(cast(1 as tinyint) as varbinary(4))`=`0x01`, `cast(cast(258 as smallint) as varbinary(1))`=`0x02`, `cast(258 as varbinary)`=`0x00000102`. |

Helpers: `VarbinaryToInteger` / `VarbinaryToMoneyUnits` / `EncodeIntegerToBinary` in `SqlValue.Coerce.cs`.
`binary(N)` targets carry their length on the `BinarySqlType`; `varbinary(N)` targets carry it on the `VarbinarySqlType` (length ≤ 0 — unspecified / MAX — keeps native width).
Arithmetic/bitwise/comparison with a binary operand routes through these same paths — see [`arithmetic.md`](arithmetic.md)'s *Binary operand promotion*.

## Legacy LOB explicit conversions (`text` / `ntext` / `image`)
An explicit `CAST` / `CONVERT` out of a legacy LOB type is gated by **source family**, and the payload's parseability never enters into it — `CAST(<text '5'> AS int)` is refused as firmly as a non-numeric one would be.
Probe-confirmed; **Msg 529 St 1**, `"Explicit conversion from data type {source} to {target} is not allowed."`, with bare family-root names on both sides (`decimal`, not `decimal(10,2)`).

- `text` / `ntext` convert only **within the string family** — `char` / `nchar` / `varchar` / `nvarchar` / `text` / `ntext` / `xml`.
  `xml` is string-category in the simulator's type model, so `SqlType.IsStringCategory` *is* the whole allow-list.
  `int` / `bigint` / `decimal` / `float` / `money` / `bit` / `date` / `datetime` / `uniqueidentifier` / `varbinary` / `sql_variant` all raise 529.
- `image` converts only **within the binary family** — `varbinary` / `binary` / `image`.
  Note the asymmetry: `xml` and `sql_variant` raise 529 from `image` even though `xml` is reachable from `text`.
  (`image` → string is also rejected a layer down in `SqlValue.CoerceTo`; the explicit path answers from the same gate as the rest.)

`TRY_CAST` / `TRY_CONVERT` raise it rather than returning NULL — 529 is an *illegal conversion*, not a conversion failure (see the swallow set below).

The gate is `Cast.IsRejectedLegacyLobConversion`, checked at the top of `Cast.ApplyCoercion` (the shared CAST/CONVERT seam) and deliberately **not** inside `SqlValue.CoerceTo`, because the *implicit* path answers differently: `textcol = 5` → **Msg 206** (`"Operand type clash: text is incompatible with tinyint"`), `textcol = 'x'` → **Msg 402**.
Gating in the shared coercion would trade one divergence for another.
Oracle: `LegacyLobCastTests`.

## `TRY_CAST` / `TRY_CONVERT`
Wrap regular CAST/CONVERT in try/catch that swallows documented "conversion failed" error numbers (returning typed NULL) while letting structural errors propagate.

Swallow set (`Cast.IsConversionFailure`): **241** (datetime-from-string parse), **242** (datetime out-of-range), **244** (tinyint/smallint INT1/INT2 overflow), **245** (string→numeric parse), **248** (int overflow), **295** (smalldatetime parse), **8114** (decimal conversion), **8115** (generic arithmetic overflow), **8169** (uniqueidentifier-from-string), **8170** (uniqueidentifier→too-narrow-string), **9807** (CONVERT-style mismatch on string input).

NOT swallowed: Msg 529 (explicit-cast disallowed pair like `int → date`), Msg 243 (unknown target type), and any source-evaluation error that fires before the cast itself runs.
`TRY_CAST(1/0 AS INT)` raises Msg 8134 on both — the divide-by-zero fires during operand evaluation, before the cast runs, and 8134 isn't in the swallow set either way.

String-source truncation isn't a "conversion failure" path either way — `TRY_CAST('hello' AS varchar(3))` → `'hel'`.
EF doesn't emit TRY_CAST/TRY_CONVERT from idiomatic LINQ (raw SQL only).

## `CONVERT` style codes
Five category-specific style families dispatch from `ConvertExpression.Run`'s style-code branch:

**Date-like → string** (`SqlValue.CoerceDateTimeToStringWithStyle`): full coverage of SQL Server's published style table — every shipping style code is implemented across all six source types (`date` / `datetime` / `smalldatetime` / `datetime2(N)` / `time(N)` / `datetimeoffset(N)`).

| Style group | Pattern | Notes |
| --- | --- | --- |
| 0 / 100 | `Mmm d yyyy h:miAM/PM` | legacy default; day right-aligned in 2 chars, hour right-aligned in 2 chars |
| 1/101 · 2/102 · 3/103 · 4/104 · 5/105 · 6/106 · 7/107 · 10/110 · 11/111 · 12/112 | date-only forms across US / ANSI / British / German / Italian / `dd Mon yy` / `Mon dd, yy` / USA / JAPAN / ISO compact | 2-digit and 4-digit-year pair per locale |
| 8 / 24 / 108 | `HH:mm:ss` | time-of-day, no fractional |
| 9 / 109 | `Mmm d yyyy h:mi:ss[sep]frac AM/PM` | legacy default + ms; `sep` is `:` for legacy datetime / smalldatetime, `.` for `datetime2(N)` / `datetimeoffset(N)` / `time(N)` |
| 13 / 113 | `d Mmm yyyy HH:mm:ss[sep]frac` | Europe default + ms |
| 14 / 114 | `HH:mm:ss[sep]frac` | time-of-day with fractional |
| 20 / 120 | `yyyy-MM-dd HH:mm:ss` | ODBC canonical |
| 21 / 25 / 121 | `yyyy-MM-dd HH:mm:ss.fff…` (period sep) | ODBC canonical + ms; modern types use source precision (datetime2(0) suppresses fractional entirely) |
| 22 | `MM/dd/yy h:mm:ss AM/PM` | single space between date and AM/PM-time, single space before `AM`/`PM` |
| 23 | `yyyy-MM-dd` | date-only ISO |
| 126 / 127 | `yyyy-MM-ddTHH:mm:ss.fff…` | ISO 8601 with `T` separator; `datetimeoffset` style 126 keeps the offset, 127 projects to UTC with `Z` suffix |
| 130 / 131 | Hijri (Kuwaiti/tabular) date + AM/PM time | 130 emits Arabic month name (e.g. `ذو القعدة`); 131 emits zero-padded numeric month with `/` separators. SQL Server uses .NET's `HijriCalendar` (default `HijriAdjustment = 0`), NOT `UmAlQuraCalendar` — the two differ by ±1 day in some months |

Fractional-second separator follows the **source family**: legacy `datetime` / `smalldatetime` use COLON in 9/13/14/109/113/114/130/131 (e.g. `14:25:36:123`) and PERIOD in 21/25/121/126/127; `datetime2(N)` / `datetimeoffset(N)` / `time(N)` always use PERIOD with source-precision digits.
Precision 0 omits the fractional portion entirely.

**Date-only source rejections** (probe-confirmed split):
- Styles 8/24/108 raise **Msg 8114** ("Error converting data type date to varchar") — valid time-of-day styles, but the source has no time portion.
- Styles 14/114 raise **Msg 281** — these are explicitly "not valid styles" for a date source per SQL Server's grammar.

**Time-only source rejections**: every date-bearing style (1/2/3/4/5/6/7/10/11/12/23/101/102/103/104/105/106/107/110/111/112) raises **Msg 8114**.
Unknown styles (anything not in the published table) raise **Msg 281** with the source family in the wording.

**Time-only source hour-padding quirk**: styles 0/9/100/109 emit single-digit hour WITHOUT leading-space padding (`2:25PM`), but styles 22/130/131 DO pad (` 2:25:36 PM`) — verified against SQL Server 2025.
The rationale isn't documented; the simulator mirrors it.

**String → date-like** (`SqlValue.CoerceStringToDateLikeWithStyle`): each style carries its own input grammar, and the grammar depends on the **target family** as well as the style.
Probed exhaustively against SQL Server 2025 (2026-07-30): 40 styles × 22 input shapes × 6 targets for accepted values, plus a separate pass for error numbers.

There are exactly **two** families, and within each the members are indistinguishable:

- **Legacy** — `datetime`, `smalldatetime`.
- **Modern** — `date`, `datetime2`, `time`, `datetimeoffset` (all four differ from the legacy pair in exactly the same cells).

**Shared forms**, accepted regardless of style or family: separatorless `yyyyMMdd` / `yyMMdd`, the English month-name spellings (`Jan 2 1999`, `2 Jan 1999`, `Jan 2, 1999`), and a bare time anchored to 1900-01-01.
Legacy style 127 accepts none of them; 130 / 131 exclude the month-name ones.

**Legacy numeric grammar** — a date-part order plus a year width, with separators (`/ - .`) interchangeable:

| Order | Two-digit-year styles | Four-digit-year styles |
| --- | --- | --- |
| mdy | 1, 10 | 20, 21, 101, 102, 110, 111, 120, 121 |
| dmy | 3, 4, 5 | 103, 104, 105 |
| ymd (year leads) | 2, 11 | — |
| ISO dash only | — | 126, 127 |
| **none** | 6, 7, 8, 9, 12, 13, 14, 22, 23, 24, 25, 100, 106, 107, 108, 109, 112, 113, 114 | |

The year width **is** the published table's "with century" / "without century" split, and it's a rejection rather than a reinterpretation: `CONVERT(datetime, '01/02/99', 101)` and `CONVERT(datetime, '01/02/1999', 1)` both raise Msg 241.
A style in the *none* row parses no separator-bearing numeric date at all whatever its own output looks like — style 23 rejects its own `yyyy-mm-dd` shape here.
Four-digit legacy styles additionally accept a **year-leading** form with the remaining pair still in the style's order, which is why 103 reads `2003-04-05` as 5 April.
Style 0 is the permissive default: mdy at either width, and the only non-ISO style taking a `T`.

**Modern numeric grammar** — each style accepts **only its own published output layout**, with no year-leading alternative:
101 / 110 take `mm/dd/yyyy` but not year-first; 20 / 21 / 23 / 25 / 102 / 111 / 120 / 121 / 126 / 127 take `yyyy-mm-dd`; 22 takes `mm/dd/yy`; 1 / 10 mdy two-digit, 3 / 4 / 5 dmy two-digit, 103 / 104 / 105 dmy four-digit, 2 / 11 ymd two-digit; everything else reads no numeric date.
So `CONVERT(date, '2026-05-13', 101)` raises Msg 241 where `CONVERT(datetime, …)` succeeds — a different grammar, not a leniency difference.
The modern family also accepts an unambiguous ISO **date-with-`T`** under every style (`CONVERT(date, '1999-01-02T10:00:00', 3)` parses) independently of that style's numeric grammar.

**`T` separator**: legacy reserves it for 0 / 126 / 127; modern accepts it everywhere.
Conversely legacy 126 / 127 reject a *space*-separated time, since ISO 8601 wants the `T`.
A trailing **`Z`** is universal on the modern targets, and legacy-side belongs to style 0 and style 127 (whose own output carries it) — legacy 126 rejects it.

**Error selection**:

| Situation | Error |
| --- | --- |
| Input matches the style's layout but a field is out of range (`05/13/2026` day-first under 103 → month 13) | **Msg 242**, legacy targets only |
| Format failure, `smalldatetime` target | **Msg 295** |
| Format failure, every other target | **Msg 241** |
| A numeric date under a *none*-row style, modern target | **Msg 9807** (style mismatch) |

The Msg 242 case is narrow: the token count, the year-token width and the two non-year tokens (≤ 2 digits) all have to fit the style, so style 2 reports 242 for `01/02/99` (y-m-d with an impossible day) but 241 for `01/02/1999`, which isn't its layout at all.
`TRY_CONVERT` swallows all of them.

**Default (no-style) path** (`SqlValue.Parse.cs` — `ParseDateTime2` / `ParseDate` / `TryParseLegacyDateTime`, distinct from the with-style parser above): a `CAST`/`CONVERT` to a date/time target with **no style argument** routes through a deliberately restrictive **language-neutral** exact-format parser, not the flexible culture-based one.
Accepted: ISO `yyyy-MM-dd` / `yyyyMMdd`, ISO with `T`/space time and 1-7 fractional digits, and — since the Django shakedown — **year-first slash / dot** forms `yyyy/M/d` / `yyyy.M.d` (unambiguous: the 4-digit year leads, so no mdy/dmy assumption; the `.dates()`/`.datetimes()` truncation an ORM emits builds these).
Locale-ordered numeric forms the with-style *general* parser accepts (`M/d/y` mdy like `'1/2/3'`) are **not** accepted here and raise Msg 241 — the language-neutral stance means the no-style path is stricter than an explicit style-0 CONVERT (a modeled divergence from real, which treats them identically under `us_english`).

**Money → string** (`SqlValue.CoerceMoneyToStringWithStyle`):

| Style | Format | Example |
| --- | --- | --- |
| 0 | no thousands separator, 2 decimal places | `1234567.89` |
| 1 | comma thousands separators, 2 decimal places | `1,234,567.89` |
| 2 | no thousands separator, 4 decimal places | `1234567.8910` |

Negative values use a leading `-` sign (no parens).
`smallmoney` uses the same formatter as `money`.
Unknown styles raise Msg 281 with `"money"` as the source-family wording.

**Float/real → string** (`SqlValue.CoerceFloatToStringWithStyle`):

| Style | Significant digits | Form | Notes |
| --- | --- | --- | --- |
| 0 | 6 | fixed-point in `[1e-4, 1e6)`, else scientific | trailing zeros stripped; scientific exponent is 3-digit `e±NNN` |
| 1 | 8 | always scientific | `1.2345679e+006` |
| 2 | 16 | always scientific | for `real`, value is promoted to float precision first (showing precision artifacts like `1.234567875000000e+006`) |
| 3 | 17 | always scientific | SQL 2016+ round-trippable form |
| 126 | source precision (16 for float, 8 for real) | always scientific | distinct from style 2: doesn't promote real to float — keeps source-precision digits |

Exponent is always 3 digits with explicit sign and lowercase `e`.
`-0` preserves the negative sign.
Unknown styles raise Msg 281 with `"float"` or `"real"` in the wording.

**Varbinary ↔ string** (`SqlValue.CoerceBinaryToStringWithStyle` / `CoerceStringToBinaryWithStyle`):

| Style | Output direction | Input direction |
| --- | --- | --- |
| 0 | bytes reinterpreted as characters: the collation's code page for `varchar`, UTF-16 LE for `nvarchar` | string bytes copied verbatim: the collation's code page for a varchar-family source, UTF-16 LE for nvarchar-family |
| 1 | `"0xHHHH…"` uppercase hex with `0x` prefix | parses hex with required `0x` prefix (missing → Msg 8114) |
| 2 | bare `"HHHH…"` uppercase hex | parses hex with prefix explicitly disallowed (presence → Msg 8114) |

Style 1 and 2 hex parsing requires an even number of digits and rejects any non-hex character; both failure paths raise **Msg 8114**, swallowed by `TRY_CONVERT` to NULL.
Empty source / empty payload round-trip cleanly.
Unknown styles raise Msg 281 with `"varbinary"` (output direction) or `"varchar"`/`"nvarchar"` (input direction).

## Type-name synonyms
Every type-name position (`CAST` / `CONVERT` / `DECLARE` / column & parameter declarations / `sp_executesql` / `OPENJSON`) accepts SQL Server's ANSI synonym set, mapped to its base type.
Single-word synonyms resolve inside `SqlType.GetByName` (so they reach every site, including the `CREATE TYPE FROM` / `CREATE SEQUENCE AS` positions): `integer` → int, `dec` → decimal, `character` → char (`rowversion` → timestamp already shipped).
Multi-word synonyms are folded by `TypeNameSynonyms` (a `SynonymTypeName` leaf whose `Span` is the canonical name, spanning the source words for line attribution): `double precision` → float, `character varying` / `char varying` → varchar, `national character` / `national char` → nchar, `national character varying` / `national char varying` → nvarchar, `binary varying` → varbinary, `national text` → ntext.
The leading word may be a reserved keyword (`double`, `national`) or an identifier (`character`, `char`, `binary`), so the fold runs ahead of a site's "type name must be an identifier" guard.
Default lengths follow the base type's own context rules (bare `character varying` → varchar(1) in a column, varchar(30) in a CAST) — the synonym only rewrites the name.

## `CAST`/`CONVERT … AS numeric` vs `AS decimal` reported name
`decimal(p, s)` and `numeric(p, s)` resolve to the same storage type, but the source keyword decides the *reported* result-column type name: `CAST(1.5 AS numeric(6,2))` reports `numeric`, `CAST(1.5 AS decimal(6,2))` reports `decimal` (probe-confirmed).
`Cast` / `ConvertExpression` capture this at parse (`Cast.ReportsNumeric` on the raw type-name token — `dec` folds to decimal-named, `numeric` is the only numeric name) and surface it through `Expression.ResultReportsNumeric`; the propagation rule and the storage-equality constraint that keeps this metadata-only live in [`arithmetic.md`](arithmetic.md#numeric-vs-decimal-reported-type-name).
