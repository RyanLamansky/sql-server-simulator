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

`VarcharSqlType`/`NVarcharSqlType`/`VarbinarySqlType` are per-length singletons via `Get(N)` (parallel to `CharSqlType`); `Unspecified` (length 0) is the runtime sentinel; `MaxForm` (length -1) is the LOB form. **Equality**: `value.Type == SqlType.Varchar` is true only for the unspecified form; "is any varchar" needs `is VarcharSqlType`. The encoder accepts any same-family pair regardless of length (write-time truncation enforced upstream).

## `TRY_CAST` / `TRY_CONVERT`
Wrap regular CAST/CONVERT in try/catch that swallows documented "conversion failed" error numbers (returning typed NULL) while letting structural errors propagate.

Swallow set (`Cast.IsConversionFailure`): **241** (datetime-from-string parse), **242** (datetime out-of-range), **244** (tinyint/smallint INT1/INT2 overflow), **245** (string→numeric parse), **248** (int overflow), **295** (smalldatetime parse), **8114** (decimal conversion), **8115** (generic arithmetic overflow), **8169** (uniqueidentifier-from-string), **8170** (uniqueidentifier→too-narrow-string), **9807** (CONVERT-style mismatch on string input).

NOT swallowed: Msg 529 (explicit-cast disallowed pair like `int → date`), Msg 243 (unknown target type), and any source-evaluation error that fires before the cast itself runs. `TRY_CAST(1/0 AS INT)` raises Msg 8134 in real SQL Server; the simulator surfaces a raw `DivideByZeroException` (pre-existing fidelity gap orthogonal to TRY_CAST).

String-source truncation isn't a "conversion failure" path either way — `TRY_CAST('hello' AS varchar(3))` → `'hel'`. EF doesn't emit TRY_CAST/TRY_CONVERT from idiomatic LINQ (raw SQL only).

## `CONVERT` style codes
Three category-specific style families dispatch from `ConvertExpression.Run`'s style-code branch:

**Date-like → string** (`SqlValue.CoerceDateTimeToStringWithStyle`):

| Style | Format | Notes |
| --- | --- | --- |
| 0 | per-type default | `datetime`/`smalldatetime` use legacy `"Mon dd yyyy hh:miAM"`; date-with-time types use `"yyyy-MM-dd HH:mm:ss"` |
| 1 / 101 | `mm/dd/yy` / `mm/dd/yyyy` | US |
| 10 / 110 | `mm-dd-yy` / `mm-dd-yyyy` | USA |
| 12 / 112 | `yymmdd` / `yyyymmdd` | ISO compact |
| 102 | `yyyy.mm.dd` | ANSI |
| 103 | `dd/mm/yyyy` | UK / French |
| 23 | `yyyy-mm-dd` | date-only ISO; date-only-emit even for datetime sources |
| 120 / 121 | `yyyy-mm-dd HH:mm:ss` / `…HH:mm:ss.fff` (or full precision) | ODBC canonical |
| 126 / 127 | `yyyy-mm-ddTHH:mm:ss.fff…` (full source precision) | ISO 8601 with `T` separator; for `datetimeoffset`, 126 keeps the offset, 127 converts to UTC with `Z` suffix |

Date-only styles (1/10/12/23/101/102/103/110/112) emit just the calendar portion regardless of source — `CONVERT(varchar, dt_datetime, 112)` returns `'20260513'`, not `'20260513 14:25:36'`. `time` only supports styles 0/120/121 (no calendar portion); other styles raise Msg 281. Unknown styles raise Msg 281 with the source-family name in the wording.

**String → date-like** (`SqlValue.CoerceStringToDateLikeWithStyle`): each style hosts a list of `DateTime.TryParseExact` format strings. On parse success, re-encodes through `datetime2(7)` and narrows to the target. On parse failure: if the same input parses under the default style-less parser, raises Msg 9807 (`"The input character string does not follow style N, …"`); otherwise raises Msg 241 (`"Conversion failed when converting date and/or time from character string."`). `TRY_CONVERT` swallows both. Styles supported: same 1/10/12/23/101/102/103/110/112/126/127 set as the inverse direction. Each style accepts an optional trailing time component (so `CONVERT(date, '20260513 14:25:36', 112)` works).

**Money → string** (`SqlValue.CoerceMoneyToStringWithStyle`):

| Style | Format | Example |
| --- | --- | --- |
| 0 | no thousands separator, 2 decimal places | `1234567.89` |
| 1 | comma thousands separators, 2 decimal places | `1,234,567.89` |
| 2 | no thousands separator, 4 decimal places | `1234567.8910` |

Negative values use a leading `-` sign (no parens). `smallmoney` uses the same formatter as `money`. Unknown styles raise Msg 281 with `"money"` as the source-family wording.
