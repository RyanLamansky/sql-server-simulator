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
- `uniqueidentifier`: pre-CoerceTo branch (Msg 8170 char/varchar, Msg 8115 nchar/nvarchar).
- `datetimeoffset → varchar` too narrow: real SQL Server raises Msg 241; simulator silently truncates (niche).

**CAST/CONVERT context defaults missing length to 30** for `varchar`/`nvarchar`/`varbinary` (column-context default is 1).

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

- **`varbinary`/`binary` → `varchar`/`nvarchar`** reinterprets each byte through the target's encoding (CP1252 for varchar/char, UTF-16 LE for nvarchar/nchar).
  Probe-confirmed 2026-05-22: `CAST(0x414243 AS varchar(10))` → `'ABC'`.
  `image` deliberately stays rejected to match real SQL Server's Msg 8116 on `LEN(image)` etc.
- **`varchar`/`char`/`nvarchar`/`nchar` → `varbinary`/`binary`** encodes the string with the source's natural encoding (CP1252 for varchar/char/sysname, UTF-16 LE for nvarchar/nchar/ntext/text).
  `varbinary(N)` receives the raw bytes and the CAST-level path truncates to N.
  `binary(N)` routes through `FromBinary` for zero-pad-or-truncate.
  Probe-confirmed: `CAST('abc' AS varbinary(10))` → `0x616263`, `CAST('abc' AS binary(10))` → `0x61626300000000000000`, `CAST(N'abc' AS varbinary(10))` → `0x610062006300`.
  Hex-string CAST forms (`CONVERT(varbinary, '0x010203', 1)` / `style 2`) still route through `CoerceStringToBinaryWithStyle`.

`VarcharSqlType`/`NVarcharSqlType`/`VarbinarySqlType` are per-length singletons via `Get(N)` (parallel to `CharSqlType`); `Unspecified` (length 0) is the runtime sentinel; `MaxForm` (length -1) is the LOB form.
**Equality**: `value.Type == SqlType.Varchar` is true only for the unspecified form; "is any varchar" needs `is VarcharSqlType`.
The encoder accepts any same-family pair regardless of length (write-time truncation enforced upstream).

## Binary ↔ integer / money CAST

Both directions live in `SqlValue.CoerceTo` (probe-confirmed against SQL Server 2025, 2026-07-14).
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

## `TRY_CAST` / `TRY_CONVERT`
Wrap regular CAST/CONVERT in try/catch that swallows documented "conversion failed" error numbers (returning typed NULL) while letting structural errors propagate.

Swallow set (`Cast.IsConversionFailure`): **241** (datetime-from-string parse), **242** (datetime out-of-range), **244** (tinyint/smallint INT1/INT2 overflow), **245** (string→numeric parse), **248** (int overflow), **295** (smalldatetime parse), **8114** (decimal conversion), **8115** (generic arithmetic overflow), **8169** (uniqueidentifier-from-string), **8170** (uniqueidentifier→too-narrow-string), **9807** (CONVERT-style mismatch on string input).

NOT swallowed: Msg 529 (explicit-cast disallowed pair like `int → date`), Msg 243 (unknown target type), and any source-evaluation error that fires before the cast itself runs.
`TRY_CAST(1/0 AS INT)` raises Msg 8134 in real SQL Server; the simulator surfaces a raw `DivideByZeroException` (pre-existing fidelity gap orthogonal to TRY_CAST).

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

**String → date-like** (`SqlValue.CoerceStringToDateLikeWithStyle`): mirrors SQL Server's flexible string-to-datetime parser (probed against SQL Server 2025, 2026-05-27).
On success re-encodes through `datetime2(7)` and narrows to the target; on failure, an input that's a valid date by some other format raises **Msg 9807** (`"The input character string does not follow style N, …"`), a non-date raises **Msg 241**.
`TRY_CONVERT` swallows both.
Two style classes:

- **Strict styles** — `12` (`yymmdd`), `112` (`yyyymmdd`), `23` (`yyyy-mm-dd`), `126`/`127` (ISO 8601 with `T`): exact `TryParseExact` format match, no separator flexibility.
  `CONVERT(date, '05/13/2026', 112)` → Msg 9807.
- **General styles** (everything else) — route through `DateTime.TryParse` with a family culture.
  Separators (`/ - .`) are interchangeable; numeric, ISO year-first, and English month-name forms (`Apr 5 2003`, `April 5, 2003`, `5 Apr 2003`) plus optional trailing time / AM-PM / bare time (anchored to 1900-01-01) all parse.
  The **only** family distinction is date-part order for ambiguous numeric dates: the dmy set (`3`/`4`/`5`/`13`/`14`/`103`/`104`/`105`/`113`/`114`/`130`/`131` → `en-GB`) reads day-first, every other style (→ `en-US`) month-first.
  A leading 4-digit token is the year, trailing pair in family order — so `2003-04-05` is Apr-5 under style 101 but May-4 under 103 (a 3-format `yyyy{sep}d{sep}M` pre-check supplies the dmy year-first ordering `TryParse` won't).
  Separatorless `yyyyMMdd` is accepted under every general style.

**Known leniency divergences** (simulator accepts more than the live server, low-value edges): the 2-digit-vs-4-digit-year with/without-century restriction isn't enforced (`CONVERT(datetime, '04/05/03', 101)` succeeds; live rejects), and a `T`-separated time is accepted under general styles (live reserves `T` for 126/127, raising out-of-range otherwise).

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
| 0 | bytes reinterpreted as characters: CP1252 for `varchar`, UTF-16 LE for `nvarchar` | string bytes copied verbatim: CP1252 for varchar-family source, UTF-16 LE for nvarchar-family |
| 1 | `"0xHHHH…"` uppercase hex with `0x` prefix | parses hex with required `0x` prefix (missing → Msg 8114) |
| 2 | bare `"HHHH…"` uppercase hex | parses hex with prefix explicitly disallowed (presence → Msg 8114) |

Style 1 and 2 hex parsing requires an even number of digits and rejects any non-hex character; both failure paths raise **Msg 8114**, swallowed by `TRY_CONVERT` to NULL.
Empty source / empty payload round-trip cleanly.
Unknown styles raise Msg 281 with `"varbinary"` (output direction) or `"varchar"`/`"nvarchar"` (input direction).
