# CAST / CONVERT family

## CAST/CONVERT to narrow `varchar` / `nvarchar` / `varbinary`
Per-source-category rule applied after `SqlValue.CoerceTo`:
- String / varbinary / date-time-family source → silent truncation. `CAST('hello world' AS varchar(5))` → `'hello'`.
- `tinyint`/`smallint`/`int` source → `varchar` too narrow → asterisk fallback (`'*'`). Quirk specific to `varchar`; `nvarchar` raises Msg 8115. `bigint` doesn't get fallback either.
- `decimal`/`numeric` source → Msg 8115 with "numeric" wording (distinct from int/bigint's "expression" wording).
- `money`/`smallmoney` → Msg 234 (`"There is insufficient result space to convert a money value to <target>."` — "money" regardless of source variant).
- `float`/`real` → Msg 232 with formatted source value (F6).
- `uniqueidentifier`: pre-CoerceTo branch (Msg 8170 char/varchar, Msg 8115 nchar/nvarchar).
- `datetimeoffset → varchar` too narrow: real SQL Server raises Msg 241; simulator silently truncates (niche).

**CAST/CONVERT context defaults missing length to 30** for `varchar`/`nvarchar`/`varbinary` (column-context default is 1).

## `varbinary` → date-time family (SSMS wire format)

`CAST(0x… AS date | time | datetime | datetime2 | datetimeoffset | smalldatetime)` decodes the byte payload via SQL Server's documented wire format. SSMS bulk-INSERT exports emit every date column literal this way (e.g. `CAST(0x07A00627C0A5173D0B0000 AS DateTimeOffset)`), so this path is load-bearing for BACPAC-style seed-data scripts. Layouts probed against SQL Server 2025:

- `date` — 3 bytes LE: days since `0001-01-01`.
- `time(N)` — 1 scale byte + LE time count in `10^(-N)`-second units; 3 / 4 / 5 bytes for scales 0–2 / 3–4 / 5–7.
- `datetime2(N)` — scale + LE time + LE 3-byte date.
- `datetimeoffset(N)` — same as `datetime2(N)` + LE `int16` offset minutes. SQL Server stores the time + date in **UTC**; the offset shifts back to the original wall-clock during round-trip.
- `datetime` — 8 bytes **BE**: `int32` days since `1900-01-01` + `uint32` 1/300-second ticks since midnight.
- `smalldatetime` — 4 bytes **BE**: `uint16` days + `uint16` minutes.

Decoders live next to `VarbinaryToGuid` in `Storage/SqlValue.Coerce.cs`. Reverse direction (date-family → varbinary) isn't modeled — no production scripts emit that direction; `bcp` and BACPAC do the encoding upstream.

`VarcharSqlType`/`NVarcharSqlType`/`VarbinarySqlType` are per-length singletons via `Get(N)` (parallel to `CharSqlType`); `Unspecified` (length 0) is the runtime sentinel; `MaxForm` (length -1) is the LOB form. **Equality**: `value.Type == SqlType.Varchar` is true only for the unspecified form; "is any varchar" needs `is VarcharSqlType`. The encoder accepts any same-family pair regardless of length (write-time truncation enforced upstream).

## `TRY_CAST` / `TRY_CONVERT`
Wrap regular CAST/CONVERT in try/catch that swallows documented "conversion failed" error numbers (returning typed NULL) while letting structural errors propagate.

Swallow set (`Cast.IsConversionFailure`): **241** (datetime-from-string parse), **242** (datetime out-of-range), **244** (tinyint/smallint INT1/INT2 overflow), **245** (string→numeric parse), **248** (int overflow), **295** (smalldatetime parse), **8114** (decimal conversion), **8115** (generic arithmetic overflow), **8169** (uniqueidentifier-from-string), **8170** (uniqueidentifier→too-narrow-string), **9807** (CONVERT-style mismatch on string input).

NOT swallowed: Msg 529 (explicit-cast disallowed pair like `int → date`), Msg 243 (unknown target type), and any source-evaluation error that fires before the cast itself runs. `TRY_CAST(1/0 AS INT)` raises Msg 8134 in real SQL Server; the simulator surfaces a raw `DivideByZeroException` (pre-existing fidelity gap orthogonal to TRY_CAST).

String-source truncation isn't a "conversion failure" path either way — `TRY_CAST('hello' AS varchar(3))` → `'hel'`. EF doesn't emit TRY_CAST/TRY_CONVERT from idiomatic LINQ (raw SQL only).

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

Fractional-second separator follows the **source family**: legacy `datetime` / `smalldatetime` use COLON in 9/13/14/109/113/114/130/131 (e.g. `14:25:36:123`) and PERIOD in 21/25/121/126/127; `datetime2(N)` / `datetimeoffset(N)` / `time(N)` always use PERIOD with source-precision digits. Precision 0 omits the fractional portion entirely.

**Date-only source rejections** (probe-confirmed split):
- Styles 8/24/108 raise **Msg 8114** ("Error converting data type date to varchar") — valid time-of-day styles, but the source has no time portion.
- Styles 14/114 raise **Msg 281** — these are explicitly "not valid styles" for a date source per SQL Server's grammar.

**Time-only source rejections**: every date-bearing style (1/2/3/4/5/6/7/10/11/12/23/101/102/103/104/105/106/107/110/111/112) raises **Msg 8114**. Unknown styles (anything not in the published table) raise **Msg 281** with the source family in the wording.

**Time-only source hour-padding quirk**: styles 0/9/100/109 emit single-digit hour WITHOUT leading-space padding (`2:25PM`), but styles 22/130/131 DO pad (` 2:25:36 PM`) — verified against SQL Server 2025. The rationale isn't documented; the simulator mirrors it.

**String → date-like** (`SqlValue.CoerceStringToDateLikeWithStyle`): style-aware parser hosts a list of `DateTime.TryParseExact` format strings per style. On parse success, re-encodes through `datetime2(7)` and narrows to the target. On parse failure: if the same input parses under the default style-less parser, raises **Msg 9807** (`"The input character string does not follow style N, …"`); otherwise raises **Msg 241** (`"Conversion failed when converting date and/or time from character string."`). `TRY_CONVERT` swallows both. Currently supports the same 1/10/12/23/101/102/103/110/112/126/127 input forms as the inverse direction; the wider style table from the output direction (styles 2/3/4/5/6/7/11/etc.) doesn't have input-direction parsers and falls through to default parsing — `CONVERT(date, '13.05.2024', 104)` parses via the default ISO parser instead of the German-style format. Minor pre-existing gap; expand on demand.

**Money → string** (`SqlValue.CoerceMoneyToStringWithStyle`):

| Style | Format | Example |
| --- | --- | --- |
| 0 | no thousands separator, 2 decimal places | `1234567.89` |
| 1 | comma thousands separators, 2 decimal places | `1,234,567.89` |
| 2 | no thousands separator, 4 decimal places | `1234567.8910` |

Negative values use a leading `-` sign (no parens). `smallmoney` uses the same formatter as `money`. Unknown styles raise Msg 281 with `"money"` as the source-family wording.

**Float/real → string** (`SqlValue.CoerceFloatToStringWithStyle`):

| Style | Significant digits | Form | Notes |
| --- | --- | --- | --- |
| 0 | 6 | fixed-point in `[1e-4, 1e6)`, else scientific | trailing zeros stripped; scientific exponent is 3-digit `e±NNN` |
| 1 | 8 | always scientific | `1.2345679e+006` |
| 2 | 16 | always scientific | for `real`, value is promoted to float precision first (showing precision artifacts like `1.234567875000000e+006`) |
| 3 | 17 | always scientific | SQL 2016+ round-trippable form |
| 126 | source precision (16 for float, 8 for real) | always scientific | distinct from style 2: doesn't promote real to float — keeps source-precision digits |

Exponent is always 3 digits with explicit sign and lowercase `e`. `-0` preserves the negative sign. Unknown styles raise Msg 281 with `"float"` or `"real"` in the wording.

**Varbinary ↔ string** (`SqlValue.CoerceBinaryToStringWithStyle` / `CoerceStringToBinaryWithStyle`):

| Style | Output direction | Input direction |
| --- | --- | --- |
| 0 | bytes reinterpreted as characters: CP1252 for `varchar`, UTF-16 LE for `nvarchar` | string bytes copied verbatim: CP1252 for varchar-family source, UTF-16 LE for nvarchar-family |
| 1 | `"0xHHHH…"` uppercase hex with `0x` prefix | parses hex with required `0x` prefix (missing → Msg 8114) |
| 2 | bare `"HHHH…"` uppercase hex | parses hex with prefix explicitly disallowed (presence → Msg 8114) |

Style 1 and 2 hex parsing requires an even number of digits and rejects any non-hex character; both failure paths raise **Msg 8114**, swallowed by `TRY_CONVERT` to NULL. Empty source / empty payload round-trip cleanly. Unknown styles raise Msg 281 with `"varbinary"` (output direction) or `"varchar"`/`"nvarchar"` (input direction).
