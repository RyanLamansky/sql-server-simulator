# CAST / CONVERT family

## Odd-length binary reinterpreted as `nvarchar`

`CAST(<varbinary> AS nvarchar)` reads the bytes as UTF-16 LE, and an odd byte count **zero-pads the dangling byte into a final character** rather than substituting U+FFFD — so `0x010203` converts back to `0x01020300` and the byte survives the round trip (probe-confirmed 2026-07-30).
Substituting a replacement character would destroy it irreversibly.

## CAST/CONVERT to narrow `varchar` / `nvarchar` / `varbinary`
Per-source-category rule applied after `SqlValue.CoerceTo`:
- String / varbinary / date-time-family source → silent truncation.
  `CAST('hello world' AS varchar(5))` → `'hello'`.
- `tinyint`/`smallint`/`int` source → `varchar` too narrow → asterisk fallback (`'*'`).
  Quirk specific to `varchar`; `nvarchar` raises Msg 8115.
  `bigint` doesn't get fallback either.
- `decimal`/`numeric` source → an ANSI target is Msg 8115 **state 5** with "numeric to data type varchar" wording (the family root whatever the target's declared length), while a Unicode target is Msg 8115 **state 2** with the generic "expression to data type nvarchar" wording — probed both ways.
- `money`/`smallmoney` → Msg 234 (`"There is insufficient result space to convert a money value to <target>."` — "money" regardless of source variant).
- `float`/`real` → Msg 232 with formatted source value (F6).
- `uniqueidentifier`: pre-CoerceTo branch (Msg 8170 char/varchar, Msg 8115 nchar/nvarchar) — fires only for a *bounded* target under 36 chars; a MAX target (length sentinel -1) has unbounded width and holds the 36-char dashed form, so the check guards `max is >= 0 and < 36` (a plain `< 36` treated the sentinel as too-narrow and wrongly raised Msg 8115 on `CAST(newid() AS nvarchar(max))` — tiberius-surfaced).
- `datetimeoffset → varchar` too narrow: real SQL Server raises Msg 241; simulator silently truncates (niche).

**CAST/CONVERT context defaults missing length to 30** for `varchar`/`nvarchar`/`varbinary` (column-context default is 1).

## Numeric narrowing overflow: the source-type-keyed error family

A numeric value overflowing an integer conversion target picks its error by **source type**, identically across CAST/CONVERT, INSERT/UPDATE column assignment, `SET @v`, and ALTER COLUMN (probe-confirmed 2026-07-31; chooser = `SimulatedSqlException.TryConversionOverflow`, called from `Cast`'s runtime catch, `CoerceForInsert`, the ALTER COLUMN per-row coercion, and the float/money paths inside `SqlValue.CoerceTo`):

- `tinyint`/`smallint`/`int` source → **Msg 220** `Arithmetic overflow error for data type <t>, value = <v>.` (state: tinyint 2, smallint 1).
- `bigint` or `decimal`/`numeric` source → generic **Msg 8115**.
  A bare integer literal past int's range is a `numeric(digit_count, 0)` source rather than a `bigint` one (see [`arithmetic.md`](arithmetic.md#integer-literals-past-ints-range-type-numericdigit_count-0)), so `CAST(3000000000 AS int)` lands on this arm — same number, state and "expression" wording either way.
- `float`/`real` source → **Msg 232** `Arithmetic overflow error for type <t>, value = <v>.` with six fractional digits (`F6`), state target-keyed tinyint 1 / smallint 2 / int 3; a `bigint` target stays Msg 8115.
- `money` source splinters per target: tinyint → Msg 232 state 11; smallint → **Msg 220 state 7 with the value in money's ×10000 tick representation** (70000 reports `value = 700000000`); int → **Msg 237** `There is insufficient result space to convert a money value to int.`; `smallmoney` takes none of these and stays Msg 8115.
- String source → Msg 244 (`INT1`/`INT2`, state 1/2 target-keyed) for tinyint/smallint, Msg 248 for int, Msg 8115 for bigint — see the string→integer section.

A `float` / `real` source's value slot carries **seventeen significant digits** before its six fractional ones, so a magnitude past that shows trailing zeros rather than the double's own binary tail: `CAST(CAST(1e30 AS float) AS int)` names `1000000000000000000000000000000.000000`, not `…19884624838656`.

A `money` / `smallmoney` value **rounds** to an integer, half away from zero (`$1.5` is 2, `-$0.5` is -1), where `decimal` and `float` truncate; a `money` value past `int`'s range is Msg 237 to every narrower target, its state naming it (int 1, smallint 2, tinyint 3) — judged on the rounded value for `int` and the unrounded one for the other two — and a `smallmoney` source reports Msg 8115 for `tinyint` and Msg 220 state 5 naming its whole part for `smallint` (probed 2026-09-25 against SQL Server 2025).

TRY_CAST/TRY_CONVERT swallow all of these (232 and 237 are in the swallow set).

### The `money` / `smallmoney` target

A magnitude past the target's range splits by source the same way, and the states were probed against SQL Server 2025:

| source | error |
| --- | --- |
| `decimal` / `numeric` | **Msg 8115 state 4**, `"…converting numeric to data type money"` |
| `bigint`, character | **Msg 8115 state 2**, `"…converting expression to data type money"` |
| `tinyint` / `smallint` / `int` | **Msg 220 state 3**, `"Arithmetic overflow error for data type smallmoney, value = 1000000."` |
| `money` → `smallmoney` | **Msg 237 state 3**, `"There is insufficient result space to convert a money value to smallmoney."` |
| `float` / `real` | **Msg 232 state 2**, the value-bearing form |

A number into a fixed-length `char(N)` / `nchar(N)` too short for it overflows exactly as into the var form of the same length — an int's `*`, the numeric Msg 8115, money's Msg 234 — and a `float` / `real` names its value in Msg 232 state 2 into a `varchar` / `char` but reports the generic Msg 8115 into an `nvarchar` / `nchar` (probed 2026-09-25 against SQL Server 2025).

## `float`/`real` → `decimal`/`numeric`

A permitted conversion (implicit and explicit), **not** the Msg 529 explicit-conversion rejection.
The operand's **exact binary value** is read and rounded half away from zero at the target's scale — real's own rule, and it is visible from scale 8 onward: `CAST(CAST(1.1 AS real) AS decimal(20, 10))` is `1.1000000238` rather than a seven-significant-digit approximation, `CAST(CAST(0.333333 AS float) AS decimal(38, 30))` is `0.333332999999999990414778494596`, and `CAST(CAST(1e30 AS float) AS decimal(38, 0))` is the double's exact `1000000000000000019884624838656`.
`real` reads its own 4-byte value the same way rather than a decimal-digit rendering of it.

NaN, an infinity, or a magnitude past the target raises **Msg 8115 state 6** naming the source family (`"…converting float to data type numeric."`).
The guard is on the binary exponent rather than a `>= 1e38` magnitude test, since the double spelled `1e38` *is* `99999999999999997748809823456034029568` — below 10^38, and a value real converts.

`float` / `real` reach `money` / `smallmoney` by the same exact-binary reading at money's fixed scale of 4.

Load-bearing for ODBC / pyodbc callers, which bind a Python/CLR `float` parameter as `float`: a decimal-column insert (e.g. SQLAlchemy's) arrives as a float-to-decimal assignment.

## Which message an unreadable string reports

The target picks the message (probed 2026-09-25 against SQL Server 2025): `tinyint` / `smallint` / `int` / `bit` report Msg 245 naming the value, `bigint` — like `decimal` / `numeric` / `float` / `real`, each named as itself — Msg 8114 naming only the types, `money` Msg 235 and `smallmoney` a message of its own, Msg 293.
A `char` / `nchar` source is named `varchar` / `nvarchar` in all of them, column or CAST alike.
A string or a binary compared with a *constant* `tinyint` or `smallint` — a literal, a variable, anything reading no column — converts to `int` rather than to the narrow type (`'300' = CAST(1 AS tinyint)` is false), while against a column-derived one it takes the narrow type and overflows as arithmetic and unification do (`'300' > ti` is Msg 244); an integer compared with `smallmoney` compares as `money` either way (`BooleanExpression.ComparisonType`).

## Whitespace around a number, by target

Which characters a string → number conversion trims differs by target, varchar and nvarchar alike (probed 2026-09-24 over every character 1–32 plus U+00A0 and U+3000 on each side):

| target | leading | trailing |
| --- | --- | --- |
| integer family, `decimal`, `bit`, `datetime` | space | space |
| `float` | any whitespace (tab through CR, NBSP, U+3000) | space |
| `real` | as `float`, except a *varchar* NBSP | space |
| `money` / `smallmoney` | space | any whitespace |
| `uniqueidentifier` | nothing | anything past the 36th character |
| `date` / `time` | space, tab | space, tab |

A string of spaces alone reads as zero for `float` and the integer family; a tab alone is unreadable.

## String → `decimal` / `numeric`

The accepted grammar is narrow: surrounding spaces, one leading `+` or `-`, digits with at most one `.`, and nothing else.
A bare leading point (`'.5'`) and a bare trailing one (`'5.'`) read, and leading zeros are free at any count.
Everything else is **Msg 8114 state 5** — `''`, `'   '`, `'.'`, `'abc'`, `'1,000'`, `'$1.00'`, `'(1)'`, `'++1'`, `'1+'`, `'1.2.3'`, and **every exponent form** (`'1e5'`, `'1E5'`, `'1.5e2'`), whatever the magnitude.

Fractional digits past the target's scale **round half away from zero**, and the digit count is judged after that rounding — so a 40-significant-digit string converts when the rounded value fits (`CAST('1.0000000000000000000000000000000000000005' AS decimal(38, 2))` is `1.00`).

A value that doesn't fit reports **Msg 8115** naming the source family, at a state settled by the **text's own** natural precision — its integer digits past any leading zeros plus every written fractional digit:

| text | target | state |
| --- | --- | --- |
| `'123456789012345678901234567890123456789'` (39 digits) | `decimal(38, 0)` or `decimal(10, 0)` | **6** — the text outran `numeric`'s own domain |
| `'0.999999999999999999999999999999999999995'` (39) | `decimal(38, 38)` | **6** |
| `'1.005'` (4) | `decimal(38, 38)` | **8** — the rescaled value needs 39 digits, while the text stays inside 38 |
| `'99.5'` (3) | `decimal(2, 0)` | **8** |
| `'99999999999999999999999999999999999999'` (38) | `decimal(38, 1)` | **8** |

Text wider than 38 digits is not an error by itself: `CAST('0.00000000000000000000000000000000000000000005' AS decimal(38, 38))` is `0`.

A **`decimal` source** narrowing to a `decimal` target runs the split on the *rescaled value* instead — state 6 where restating it at the target's scale would need more than 38 digits, state 8 where it stays inside 38 — and names itself `numeric` on both sides.

## `PARSE` / `TRY_PARSE` (culture-aware conversion)

`PARSE(string AS type [USING culture])` / `TRY_PARSE(...)` — culture-aware Convert.NET surface (`Parser/Expressions/ParseFunction.cs`).
The string argument routes through .NET's `<Type>.Parse(string, CultureInfo)` rather than the simulator's existing CAST machinery, so the accepted formats follow the CLR's culture rules (commas vs dots, locale-specific date orderings) rather than SQL Server's CAST grammar.
Culture defaults to `en-US` when the USING clause is omitted; unknown culture name raises Msg 9819 (probe-confirmed) via a dedicated `ParseConversionFailed` factory.
`PARSE` re-raises any `FormatException` / `OverflowException` as Msg 9819 with the source value embedded; `TRY_PARSE` catches the same set and returns NULL.
An exact-numeric target is named **`numeric`** in that message however the statement spelled it.

An exact-numeric target reads at the full 38 digits: the culture's group separators come off and the digits go through `Decimal38`, with .NET's own parse kept as the grammar gate for anything a `decimal` could have held.
So `PARSE('99.999.999.999.999.999.999.999.999.999.999.999.999' AS decimal(38, 0) USING 'de-DE')` answers, and `NumberStyles.Number`'s grammar still decides what the culture accepts (a trailing sign reads, `'(1234.56)'` and `'$1,234.56'` do not).
Excess fractional digits round as they do for `CAST`, but **text carrying more than 38 digits is refused outright rather than rounded into range** — `PARSE('1.0000000000000000000000000000000000000005' AS decimal(38, 2))` raises Msg 9819 where the `CAST` answers `1.00`.

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

The two legacy forms read the **rightmost** 8 / 4 bytes, zero-padding a shorter payload on the left the way the integer conversions do — `CAST(0x0102 AS datetime)` is `1900-01-01 00:00:00.860`, and an 8-byte payload reaches `smalldatetime` through its last four bytes.
A day outside the type's range or a time part past midnight is **Msg 210** (`Conversion failed when converting datetime from binary/varbinary string.`) — `CAST(0x6100 AS smalldatetime)`, whose 24832 minutes overrun the day (probed 2026-09-23).
A `timestamp` source refuses the same `smalldatetime` payload with **Msg 8115** (`Arithmetic overflow error converting expression to data type smalldatetime.`) instead.
These are the conversions a binary meeting a legacy date in a comparison, a CASE or `+` / `-` takes (see [`arithmetic.md`](arithmetic.md#type-pair-legality)).

Decoders live next to `VarbinaryToGuid` in `Storage/SqlValue.Coerce.cs`.
The reverse direction is modeled for the legacy pair only: `datetime` / `smalldatetime` → `binary(N)` / `varbinary(N)` writes the same big-endian form right-aligned, `binary(N)` padding or cutting on the left and `varbinary(N)` only cutting (`CAST(<datetime> AS binary(4))` keeps the time half).
The other date types → binary isn't modeled — no production scripts emit that direction; `bcp` and BACPAC do the encoding upstream.

Bytes the layout can't read are Msg 241, the failure a string reports (probed 2026-09-25 against SQL Server 2025).
**Not modeled yet**: real also reads a few other lengths — an all-zero `0x00000000` as `0001-01-01`, a five-byte time — which the simulator refuses with that Msg 241.

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
A `float` / `real` converts to binary as its big-endian IEEE bits and a `money` / `smallmoney` as its scaled units, each at its own width and fitted to the target as an integer is — `CAST(1e0 AS varbinary(4))` keeps the rightmost four bytes, `0x00000000` (probed 2026-09-25 against SQL Server 2025); the reverse, binary to `float` / `real`, is Msg 529.

| Source → target | Rule |
| --- | --- |
| `binary`/`varbinary` → `bit`/`tinyint`/`smallint`/`int`/`bigint` | Big-endian; **left-truncate** to the target width (keep the rightmost bytes), zero-fill high bytes when shorter, read two's-complement. **Silent — never overflows.** `cast(0x0102 as int)`=258, `cast(0x0102030405 as int)`=33752069, `cast(0xFF01 as tinyint)`=1 (no Msg 244), `cast(0xFFFFFFFF as int)`=-1, `cast(0x as int)`=0. `bit` tests the final byte for non-zero (`cast(0x0100 as bit)`=0, `cast(0x01 as bit)`=1). |
| `binary`/`varbinary` → `money`/`smallmoney` | Rightmost 8 (money) / 4 (smallmoney) bytes = raw **scale-4 units**, big-endian two's-complement ÷ 10000. `cast(0x01 as money)`=0.0001, `cast(0x01 as smallmoney)`=0.0001. |
| `binary`/`varbinary` → `decimal`/`numeric` | Reads SQL Server's own `numeric` byte form — precision, scale, a reserved byte, a sign byte (0 = negative), then the magnitude little-endian — and converts that value to the target with the usual rounding: `cast(0x0502000196000000 as decimal(5,2))` = 1.50, `cast(0x05020000E1000000 as decimal(4,1))` = -2.3, a 1-byte magnitude (`0x0502000196`) suffices. Any other payload — shorter than 5 bytes, precision 0 or past 38, scale past precision, a magnitude wider than the precision, a negative zero — is **Msg 8114** (`"Error converting data type varbinary to numeric."`, class 16 state 5), spelled `varbinary` for a `binary(N)` source too and `timestamp` for a rowversion; `TRY_CAST` swallows it to NULL. |
| `decimal`/`numeric` → `binary`/`varbinary` | The same byte form, the magnitude in as few four-byte words as hold it: `cast(cast(1.5 as decimal(5,1)) as varbinary)` = `0x050100010F000000`. `binary(N)` left-pads it with zeros like an integer, but a narrower target of either kind keeps the **leading** bytes (`binary(2)` = `0x0501`) — probed 2026-09-24. |
| `binary`/`varbinary` → `float`/`real` | **Msg 529** (`"Explicit conversion from data type varbinary to float is not allowed."`, class 16 state 1) — via `CoerceToApproximate`'s default arm. NOT swallowed by `TRY_CAST` (`try_cast(0x41 as float)` still raises 529). |
| `bit`/`tinyint`/`smallint`/`int`/`bigint` → `binary(N)` | Native-width big-endian two's-complement (bit/tinyint→1, smallint→2, int→4, bigint→8), then **left-zero-pad or left-truncate to exactly N** (fixed width). `cast(258 as binary(4))`=`0x00000102`, `cast(258 as binary(1))`=`0x02`, `cast(-1 as binary(4))`=`0xFFFFFFFF`, `cast(258 as binary)`=30 zero-padded bytes (CAST default length 30). |
| `bit`/`tinyint`/`smallint`/`int`/`bigint` → `varbinary(N)` | Native-width bytes, **left-truncated only when N < native, never left-padded** (variable width). `cast(258 as varbinary(4))`=`0x00000102`, `cast(cast(1 as tinyint) as varbinary(4))`=`0x01`, `cast(cast(258 as smallint) as varbinary(1))`=`0x02`, `cast(258 as varbinary)`=`0x00000102`. |

Helpers: `VarbinaryToInteger` / `VarbinaryToMoneyUnits` / `EncodeIntegerToBinary` in `SqlValue.Coerce.cs`.
`binary(N)` targets carry their length on the `BinarySqlType`; `varbinary(N)` targets carry it on the `VarbinarySqlType` (length ≤ 0 — unspecified / MAX — keeps native width).
Arithmetic/bitwise/comparison with a binary operand routes through these same paths — see [`arithmetic.md`](arithmetic.md)'s *Binary operand promotion*.

A `timestamp` converts out as the `binary(8)` it is, to every target a binary reaches, and a binary or ANSI string converts **in** as `binary(8)` would — padded or cut on the right (`CAST(0x0102 AS timestamp)` is `0x0102000000000000`).
An ANSI string reaches `image` as its code-page bytes, and a string reaches `hierarchyid` by parsing its `/1/2/` path (and back), which is what a comparison or CASE between the two needs.
`money` / `smallmoney` → `float` / `real` converts rather than raising Msg 529.
Probed 2026-09-23 against SQL Server 2025.

## Reading a date-time string

Every string → date-time CAST reads through one grammar, `DateTimeText.TryParse`, whose XML doc lists what it accepts; the two legacy types and the four newer ones differ in both directions, so the grammar takes which it reads for.
It was built against a differential matrix of some two hundred and sixty strings — ISO, numeric, unseparated, month-name, time-first, AM / PM, fractional, offset and malformed shapes — cast to all six types with `TRY_CAST` on SQL Server 2025, then every refused cell cast again for its error (probed 2026-09-24): every value and every error number matches, as does `ISDATE` over the same strings.
A third of the matrix was a holdout written after the grammar, which surfaced its last four rules (the nine-digit fraction cap, a `T` time's attached offset, tabs as spaces for the newer types, `2024 Jun` as the first of the month).

Leading and trailing spaces are trimmed first, as real does, so a padded `char(N)` value converts — `CAST('  2024-01-02  ' AS datetime)`, `CAST('12:34:56   ' AS time)` — and a `char(N)` column compares against a date column (probed 2026-09-23).

The failure numbers split by type: the newer four report Msg 241 for every string they refuse, while `datetime` / `smalldatetime` report Msg 241 / 295 for a string they can't read and Msg 242 for one naming a value that doesn't exist — including a few shapes real's tokenizer reads further than it looks (`'2024-12-31 23'`, `'Jan-05-2024'`, `'2024-12-31 .5'`).
A `datetimeoffset` string whose offset carries its UTC instant outside years 1–9999 is Msg 8114 state 31.

### `SET DATEFORMAT`

A numeric date's three parts read in the session's `SET DATEFORMAT` order — a name, a string literal or a variable, case aside, Msg 2741 for anything but the six — which a module body's own `SET` changes for its duration only and `SET LANGUAGE` moves to the language's order unless the same batch set it first (probed 2026-09-24 against SQL Server 2025).
The per-order rules, which differ between the legacy pair and the newer types, are on `DateTimeText.OrderNumericDate`; the matrix behind them was twenty-three strings under all six orders and five types, every cell matching.
The conversion runs inside `SqlValue.CoerceTo`, which has no session, so each statement publishes the session's order as `DateOrder.Current`, an `AsyncLocal` rather than a thread-static so it flows into the parallel grouped accumulation.
The plan cache keys on the order too, as real's does.

### Not modeled yet

- **Month names in other languages** — only the English names are recognized.

## A precision or scale past its type's range

What real raises depends on where the type is written, so `SqlType.GetByName` takes a `TypeSpecSite` (probed 2026-09-24 against SQL Server 2025).
A `CAST` / `CONVERT` target written with a lone precision past the maximum **clamps** to it — `numeric(39)` is `numeric(38, 0)`, `float(54)` is `float` — while one written with a scale is Msg 2717.
A variable, a parameter or an alias type raises Msg 2750 for a lone precision (numbered among the batch's variables, the routine's parameters, or `#0` followed by Msg 225 for an alias type) and Msg 2717 with a scale; a column raises Msg 2750 either way, numbered by its ordinal.
A scale past the precision is Msg 192 outside a column and Msg 183 naming the column inside one, and a `DECLARE` refused this way still declares its variable, so a later reference doesn't add Msg 137.

## Conversion legality is settled while compiling

**Msg 529** (`"Explicit conversion from data type {source} to {target} is not allowed."`, class 16 state 1, bare family-root names on both sides) is decided from the two *types* — no value enters into it.
Real settles it while compiling, so a typed NULL raises it, an empty rowset raises it, `TRY_CAST` / `TRY_CONVERT` raise it rather than returning NULL (529 is an illegal conversion, not a conversion failure), and a module body carrying one refuses its own `CREATE`.
`Cast.IsIllegalExplicitConversion` is the table, asked from `Cast.GetSqlType` / `Convert.GetSqlType`; an untyped `NULL` source is exempt, having no type to judge.

The table is the probed one — SQL Server 2025, 2026-08-05, every ordered pair of the 28 common type names, 284 refusals — and reads by source family:

| Source | Refuses |
| --- | --- |
| a number (integer / decimal / money / float / real) | `date`, `time`, `datetime2`, `datetimeoffset`, `uniqueidentifier`, `xml` |
| `date` / `time` / `datetime2` / `datetimeoffset` | every number; `date` and `time` also refuse *each other*; `uniqueidentifier`, `xml` |
| `datetime` / `smalldatetime` | `uniqueidentifier`, `xml` — the two day-count conversions are what a number reaches, and reaches back |
| `uniqueidentifier` | every number, the whole date/time family, `xml` |
| `xml` | every number, the whole date/time family, `uniqueidentifier`, `sql_variant` |
| `sql_variant` | `xml` |
| `binary` / `varbinary` | `float`, `real` |
| anything but a character string | `text` / `ntext` |
| anything but an ANSI character string or a binary | `image` |

| `hierarchyid` / `geography` / `geometry` | everything but a character string or a binary — another CLR type included |
| anything but a character string or a binary | `hierarchyid` / `geography` / `geometry` |

The CLR rows were probed 2026-09-25 against SQL Server 2025, and real names a CLR type in the message by its three-part name in the current database (`simulated.sys.hierarchyid`) and a literal decimal `numeric`.
Types outside that grid (`rowversion`, alias types) are not in the table and keep whatever the value path decides.
A module body's Msg 529 **ends the bind report where it is**: real gathers name-resolution errors across a whole body but stops at this one, so a body whose first statement carries a conversion error reports it alone even when a later statement names a missing column, while a name error found first reports with the conversion behind it (probed 2026-08-05).
Oracle: `ConversionLegalityTests`.

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
| 2 / 126 | no thousands separator, 4 decimal places | `1234567.8910` |

Negative values use a leading `-` sign (no parens).
`smallmoney` uses the same formatter as `money`.
Every other style formats as style 0 (probed 2026-09-25 against SQL Server 2025).

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
Every other style formats as style 0 (probed 2026-09-25 against SQL Server 2025), save 128 and 129, which have renderings of their own (`1.2345675E6`) that aren't modeled yet and raise Msg 281.
**Binary → string** takes styles 0 / 1 / 2 and refuses the rest with Msg 9809 naming the target's family.

**A styleless conversion is style 0**, and that is what every ordinary string surface takes: `CAST(<float> AS varchar)`, `CONVERT` with no style, `+` concatenation, `CONCAT` / `CONCAT_WS`, assignment to a string variable or column, `sql_variant` unwrapping, and the Msg 2627 duplicate-key rendering.
So `CAST(CAST(1234567890 AS float) AS varchar(50))` is `1.23457e+009`, not `1234567890` — six significant digits, fixed-point only while the *rounded* magnitude stays in `[1e-4, 1e6)`.
`real` takes the same six digits: `CAST(CAST(1234567 AS real) AS varchar(50))` is `1.23457e+006`.

The two producers that don't are **JSON and XML**, which write **style 126** — the source-precision scientific form, sixteen significant digits for `float` and eight for `real`.
That covers `FOR JSON`, `FOR XML`, `JSON_OBJECT` / `JSON_ARRAY`, and `JSON_MODIFY`'s written value: `JSON_OBJECT('f': CAST(1234567890 AS float))` is `{"f":1.234567890000000e+009}`.
All probe-confirmed against SQL Server 2025 (2026-08-08); `SqlValue.FormatApproximateWithStyle` is the one renderer behind every one of them.

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
