# Type promotion and decimal arithmetic

## Operator precedence

Two levels of left-associative binary operators, plus the unary prefixes.
`* / %` bind tightest (tightness 2 in `Expression.BinaryTightness`); `+ - & | ^` and the `<< >>` shifts share the looser level (tightness 1); comparison and below are the boolean layer's, and terminate an expression.
Probe-confirmed that the shifts sit with the additive family rather than above it: `2 * 3 << 1` = 12, `4 | 1 << 2` = 20.

### The unary signs bind at the additive level

`+` and `-` as **prefixes** sit at the additive level too — *below* `* / %` — so a sign reaches past its immediate operand and takes the whole following multiplicative chain.
`a / -b / c` is therefore `a / (-(b / c))`, not `(a / -b) / c`.
Probe-confirmed against SQL Server 2025 (2026-08-03):

| expression | value | grouping |
| --- | --- | --- |
| `100 / -10 / 2` | -20 | `100 / (-(10 / 2))` |
| `8 / - 2 * 4` | -1 | `8 / (-(2 * 4))` |
| `100 / - 20 % 7` | -16 | `100 / (-(20 % 7))` — `%` joins the chain |
| `- 6 + 2` | -4 | `(-6) + 2` — the additive level stops the reach |
| `- 6 & 3` | 2 | `(-6) & 3`, not `-(6 & 3)` = -2 |
| `- 2 << 3` | -16 | `(-2) << 3` |
| `100 / -(10) / 2` | -20 | parenthesizing the *operand* doesn't stop the reach |
| `100 / (-10) / 2` | -5 | parenthesizing the *sign expression* does |

A single leading sign agrees under either binding — negation commutes with `*` and integer division truncates symmetrically — so the divergence only shows when a second multiplicative operator follows the sign's operand (`-6 / 3` is -2 either way).

`~` is the exception: it binds **tighter** than `*` and takes a lone operand, so `~ 2 * 3` is `(~2) * 3` = -9, not `~(2 * 3)` = -7.
Its operand may itself be a sign, which then reaches for the chain again: `~ - 2 * 3` is `~(-(2 * 3))` = 5.

`Expression.ParsePrimary` implements this — the two sign arms parse their operand through `ParseSignedOperand` (`ParseBinaryContinuation` at `minTightness: 2`), the `~` arm through `ParsePrimary`.
Because a stack of signs recurses through `ParseSignedOperand` once per sign without passing through `Expression.Parse`, that method carries the Msg 8631 stack probe itself — see [`grammar.md`](grammar.md#msg-8631-backstop).

The regrouping is not cosmetic.
It moves which operands pair up, so it moves what `PromoteForArithmetic` computes: `cast(1 as decimal(9,2)) / -cast(3 as decimal(9,4)) / cast(7 as decimal(9,6))` is `decimal(38, 17)` where the parenthesized `/ (-cast(3 as decimal(9,4))) /` form is `decimal(38, 21)`.
And it moves error behavior — `5 / -0 * CAST(NULL AS int)` pulls the NULL into the divisor's group, so the division is by NULL and returns NULL, while `-91 / - 0 * - 45` divides by zero and raises **Msg 8134**.

The legacy paren-less `SELECT TOP n` takes no unary prefix at all (its count is a bare constant or variable): real raises **Msg 102** naming the operator for `TOP -1` / `TOP +1` / `TOP ~1`, where the parenthesized `TOP (-1)` takes the sign and validates the resulting value.

## Integer ↔ string promotion
Cross-category `int ↔ string` lands the integer's specific subtype (`tinyint + '3'` stays tinyint; `bigint + '3'` stays bigint).
String parses through the integer's CAST path: empty/whitespace → 0, `+`/`-` accepted, leading/trailing whitespace trimmed.
**Decimal-shaped strings (`'5.5'`) raise Msg 245** rather than routing through decimal.
Hex (`'0x05'`) likewise rejected.

`bit ↔ string` asymmetry: comparison works (`'true'`/`'false'`/empty → true/false/false; non-zero digit string → True regardless of magnitude); `bit + str` rejected — `+`/`-`/`%` → Msg 402, `*`/`/` → Msg 8117 with LEFT operand's type only.

WHERE on a varchar column compared against int halts on the first unparseable row (not isolated as per-row UNKNOWN).
SQL Server's lazy-IN quirk (unparseable IN-list value suppressed when another matches) isn't modeled.

### The promotion conversion of a written operand is memoized

`WHERE OrderDate >= '2015-01-01'` promotes to `date`, so the literal's conversion — `DateTimeParse`, the whole per-style grammar of [`casting.md`](casting.md) — runs once and is reused for every scanned row; per-row re-conversion measures at **65% of a 73k-row filtered `SELECT … INTO`'s CPU**, more than the join, the projection and the write together.

`StringCoercionMemo`, one instance per operand slot of a `CompareExpression` (and three on a `BETWEEN`: subject, lower, upper), holds the last conversion keyed on the source string's *reference*, its declared type and the target type, all by reference.
Reference identity is the right key and not merely a cheap approximation: a string is immutable, so an identical instance under an identical (source type, target type) pair is the same call to a function of nothing else.
A literal or a parameter hands out one instance per statement, which is what makes a single entry enough; a per-row string operand (`WHERE dv = <varchar column>`) misses every row and converts exactly as before.

Two gates keep it from costing anything where it can't pay:

- **the source must be a string**, which is what makes the key sound — the payload behind a non-string value can be a `byte[]`, whose contents a reference test wouldn't cover;
- **the target must not be a string**, because a string-to-string promotion re-tags the same instance and would allocate an entry per row on an operand that varies, while the non-string targets are the parses (the date/time grammar, decimal, `uniqueidentifier`).

A failed conversion is never memoized, so Msg 241 still surfaces from the row that carries the bad text, as many times as it did before.
Like the [LIKE pattern memo](collations.md#the-pattern-compilation-is-memoized-per-node) the entry is immutable and published through `Volatile`, since a cached plan shares the node across sessions.
`InExpression` doesn't carry one — a list of literals would thrash a single entry.

## `bit` operand pairs

A `bit` paired with a `bit` has no arithmetic on real, and the rejection splits by operator the same way the binary pair does: `*` / `/` raise **Msg 8117** (`"Operand data type bit is invalid for multiply operator."`), while `+` / `-` / `%` raise **Msg 402** (`"The data types bit and bit are incompatible in the add operator."`, and the subtract / modulo wordings).
The gate lives in `SqlType.PromoteForArithmetic`, so it fires from the static type path as well as the runtime one.

Two neighbours stay legal and are deliberately outside the gate: the **bitwise** operators (`&`, `|`, `^`) accept a bit pair, and a **mixed** `bit + int` promotes to `int` and computes normally — only the same-type arithmetic pair is refused.
`SUM(bit)` is refused separately by the aggregate dispatch, also with Msg 8117.

## Binary operand promotion
One `binary`/`varbinary` operand paired with one integer-family operand converts the **binary side** to the integer type — for arithmetic (`+ - * / %`) *and* bitwise (`& | ^`), so the result keeps the integer partner's specific subtype (`1 + 0x01` → int 2; `cast(5 as bigint) / 0x02` → bigint 2; `cast(5 as tinyint) + 0x01` → tinyint 6; `255 & 0x01` → int 1).
Comparison converts the same way (`0x01 = 1` compares equal).
`SqlType.Promote` handles the type unification (binary-vs-integer → the integer type), and `TwoSidedExpression.IntegerArithmetic` coerces the runtime binary value via the binary→integer path (see [`casting.md`](casting.md)); the string↔integer normalization sitting beside it excludes bitwise, but the binary path does not.

Two **binary** operands: `+` is byte concatenation (`0x01 + 0x01` → varbinary `0x0101`; `binary(N) + binary(M)` → `binary(N+M)`, else `varbinary(N+M)`, capped 8000 — `Add.BinaryConcatenation` + `PromoteForArithmetic`'s `BinaryPairResultType`).
Every other operator errors, matching SQL Server: `- % & | ^` → **Msg 402** (`"The data types varbinary and varbinary are incompatible in the '&' operator."`), `* /` → **Msg 8117** (`"Operand data type varbinary is invalid for multiply operator."`).
`PromoteForArithmetic` raises for the static schema; `IntegerArithmetic` re-raises the same wording at runtime.

`BuildSynthesizedSqlRow` (FROM-less SELECT) runs each expression first (surfacing runtime-only errors with operator-name wording), then `GetSqlType` for schema, then bridges any mismatch via `CoerceTo` — required for mixed-type CASE/Coalesce without a FROM clause.

## The approximate family (`float` / `real`)

`real` is not a way-station on the road to `float`: it **wins over every arithmetic partner except `float`**, so `real + int`, `real * decimal(10, 2)`, `real / money` and `real - bit` are all `real`, in either operand order, and only a `float` on either side makes the result `float`.
Probe-confirmed against SQL Server 2025 over the whole approximate row and column of the operator matrix (both approximate types × `int` / `bigint` / `smallint` / `tinyint` / `decimal` / `numeric` / `money` / `smallmoney` / `bit`, both orders, `+ - * /`).

The **value** is computed in `double` whatever the result type, then rounded to single for a `real` result — real's own answer, not an approximation of it: `CAST(16777216 AS real) + CAST(1 AS bigint)` returns `0x4B800000` exactly as `… + CAST(1 AS real)` does (the `+1` falls off the end of single's mantissa), and `CAST(1 AS real) / 7` is bit-identical to `CAST(CAST(1 AS float) / 7 AS real)`.
So one `double` computation plus a `(float)` narrowing reproduces both types, and the only thing that has to be got right is *which* type the result is.

`ApproximateArithmetic` takes that result type from `SqlType.PromoteForArithmetic` rather than deciding it locally — the same single source of truth `TwoSidedExpression.GetSqlType` and `DecimalArithmetic` read.
Deciding it locally is what broke: a former `left == Real && right == Real` test made `real + int` produce a `double` while the projection schema declared `real`, and the row encoder's `valueType == columnType` check surfaced that to the consumer as a raw `ArgumentException` — the mismatch this file's static/runtime-parity requirement exists to prevent.
The FROM-less path hid it, since `BuildSynthesizedSqlRow` bridges a mismatch with a `CoerceTo`; only a read with a real FROM clause reached the encoder.
`RealTypePromotionTests.Arithmetic_EveryNumericPair_RuntimeValueMatchesDeclaredType` walks every numeric pair × every operator through a table for exactly that reason: reaching the encoder *is* the parity assertion.

**Modulo has no float form at all.**
An approximate operand on either side raises, splitting the way the `bit` and binary pairs do: two approximate operands raise **Msg 8117** naming the **left** one (`"Operand data type real is invalid for modulo operator."` — for `real % float` as much as `real % real`, and `float % real` names `float`), while an approximate paired with any exact-numeric, string or binary partner raises **Msg 402** naming both in written order (`"The data types real and int are incompatible in the modulo operator."`).
The gate lives in `PromoteForArithmetic` beside the `bit`-pair one, so it fires from the static path and the runtime path alike.
Real's Msg 402 here beats the Msg 206 operand-type clash it reports for a binary partner under `+` — `0x02 % CAST(2 AS real)` is 402 while `0x02 + CAST(2 AS real)` is 206 (the latter unmodeled; see [Not modeled yet](#not-modeled-yet-approximate)).

`SUM` and `AVG` are the one place `real` does **not** survive: both **widen it to `float`**, while `MIN` / `MAX` keep `real` and the statistical family (`STDEV` / `STDEVP` / `VAR` / `VARP`) was already `float` for every operand type.
Accumulation is in `double`, each single widening exactly on the way in, which is what real does — `SUM` over `real` values `{16777216, 1, 1, 1, 1}` returns `16777220`, where a single-width running total would have stuck at `16777216`.
Probed alongside it: `AVG(smallmoney)` reports **`money`**, not `smallmoney` (`SUM(smallmoney)` already did), so neither `SUM` nor `AVG` can produce `real` or `smallmoney` and the accumulator dispatch no longer carries arms for them.
Oracle: `RealTypePromotionTests`.

<a id="negative-zero"></a>
### Negative zero

`float` / `real` carry IEEE 754's signed zero, and real reports it: `SELECT -CAST(0 AS real)` renders `-0`.
Unary minus is the shape that produces it, so `Negate` flips the sign bit for the approximate family rather than taking the shared `0 - x` path, which would fold the sign away — under round-to-nearest `0.0 - 0.0` is `+0.0`, and only a true negation gives `-0.0`.
Everything else is straight IEEE arithmetic, which already agreed: `0e0 * -1`, `-1 * 0e0` and `0e0 / -1` are `-0`, while `-0 + 0` is `+0`, `-0 * 1` is `-0`, and `SUM` / `AVG` accumulating from `+0` come back positive.

**The sign of zero is a stored value, never an identity.**
It survives a `float` / `real` column, `SELECT … INTO`, a table variable and the wire (SqlClient hands back bit pattern `0x8000000000000000`), but `-0` and `+0` compare **equal** everywhere identity is asked: `WHERE f = 0` matches both, `DISTINCT` / `GROUP BY` / `UNION` / `INTERSECT` collapse them to one row and `EXCEPT` to none, and a unique index calls them duplicates (**Msg 1505**).
The surviving row is whichever arrived first, so the reported sign follows insertion order — `MIN` and `MAX` likewise keep the first of the pair, since neither compares less than the other.
`SqlValue` gets this by folding the negative-zero bit pattern onto the positive one in `Equals` / `GetHashCode` (`CompareTo` needed nothing — .NET's `double.CompareTo` already returns 0 for the pair).
Only the zero pattern folds, so NaN — unreachable through SQL Server's own arithmetic, which raises instead — keeps the reflexive bitwise identity IEEE equality would deny it.

**The exact numerics have no signed zero**, so a `decimal` / `numeric` / integer zero widens to a *positive* float however it was produced: `CAST(-0.0 AS float)`, `CAST(0.0 * -1 AS float)` and `CAST(-CAST(0 AS decimal(10, 2)) AS float)` are all `0` on real, and a `float` `-0` narrowing back to `decimal` renders `0.00`.
This needs guarding rather than falling out, because .NET's `decimal` *does* keep a sign bit through a zero result — `0.0m * -1` and `decimal.Negate(0m)` both set it, and the widening conversion would carry it into an IEEE negative zero real never produces from an exact numeric.
The decimal→approximate coercions fold it away; the sign is invisible on every other decimal surface (`ToString`, equality, ordering, and the storage round-trip all already treat it as `+0`).

Every string surface reports the sign — `CAST … AS varchar`, `CONCAT`, `STR`, `CONVERT` styles 1/2/3, `PRINT`, `FOR JSON`, `FOR XML` — **except `FORMAT`**, which gives an unsigned zero for every format string probed (`G` / `N2` / `F3` / `E2` / `C` / `0.00` / `#.##`), the .NET Framework rendering its CLR implementation carries; .NET Core signs it, so `Format` folds it.
`SIGN` and `ABS` of `-0` are `0`, as on real.

<a id="not-modeled-yet-approximate"></a>
**Not modeled yet.**
`<binary> <op> <approximate>` (`0x02 + CAST(2 AS real)`) is real's **Msg 206** operand-type clash in both orders and for `+ - * /`; the simulator raises `NotSupportedException` from the promotion dispatch instead.
Modulo is unaffected — its own gate answers first, with real's Msg 402 — except in a FROM-less `SELECT`, where `BuildSynthesizedSqlRow` runs the value before the schema and a *binary left operand* reaches the runtime dispatch's unsupported-pair fallback first.
`STDEV` / `VAR` over `money` is real's `float`; the simulator raises Msg 529 (`"Explicit conversion from data type money to float is not allowed."`) from the statistical aggregator's operand coercion.

## Integer arithmetic overflow
SQL Server keeps the narrow integer type through arithmetic instead of widening, so a result outside the operand width raises **Msg 8115 St 2** (`"Arithmetic overflow error converting expression to data type {type}."`) rather than wrapping.
Probe-confirmed across `+ - * / %`, unary minus and `ABS`, for `tinyint` / `smallint` / `int` / `bigint` alike — `cast(255 as tinyint) + cast(1 as tinyint)` raises naming `tinyint`, not `int`.
A mixed-width pair promotes first and so doesn't overflow at all: `int + bigint` → bigint, `smallint + int` → int.

`PureIntegerArithmetic` computes in `long` and narrows back with `checked`, funnelling `OverflowException` into the Msg 8115 factory.
The `bigint` width is covered by `checked` inside the `Add` / `Subtract` / `Multiply` compute lambdas — which is why those read `checked(a + b)` rather than `a + b`, since the narrowing can't see an overflow that already happened at `long` width.
Bitwise `& | ^` can't leave the operand width, so they never trip it.

`<type minimum> / -1` **and `<type minimum> % -1`** both overflow: SQL Server forms the quotient for `%` too, even though the mathematical remainder is 0 (probe-confirmed for smallint / int / bigint; `-5 % -1` and `<min> % 1` compute normally).
The `long`-width computation hides the narrow cases from the checked narrowing — the remainder is in range — so `PureIntegerArithmetic` guards them explicitly via `SignedMinimum`.

Aggregates are covered by the same rule: `SumAggregator.LongSum.Wrap` and `NumericAggregator` raise the same 8115, so `SUM(int)` past int range errors rather than wrapping — the realistic way to hit this, and the reason the per-row wrap was a wrong-answer divergence rather than a cosmetic one.
The checked path's cost is below measurement noise (300k-row arithmetic scan: 94–140 ms run-to-run on checked and unchecked builds alike, minima 95.3 vs 94.2 ms).
Oracle: `IntegerArithmeticOverflowTests`.

## Decimal arithmetic precision / scale
Per-operator decimal scale rules differ from the joint-envelope rule used for non-arithmetic uses (comparison / COALESCE / set ops):
- `+` / `-`: `p = max(p1-s1, p2-s2) + max(s1, s2) + 1`, `s = max(s1, s2)`
- `*`: `p = p1 + p2 + 1`, `s = s1 + s2`
- `/`: `s = max(6, s1 + p2 + 1)`, `p = p1 - s1 + s2 + s`
- `%`: `p = min(p1-s1, p2-s2) + max(s1, s2)`, `s = max(s1, s2)`

When precision exceeds 38 it clips to 38, and the scale gives way — but by a rule that splits by operator family.

`*` and `/` reduce the scale by the whole excess, floored at `min(originalScale, 6)`.
The floor stabilizes division (`s ≥ 6` always, so it is effectively 6) and binds for multiplication whenever the excess would take the scale under 6: `decimal(20, 5) * decimal(23, 5)` and `decimal(25, 8) * decimal(25, 8)` both land on `decimal(38, 6)` where the raw excess would give 4 and 3, while `decimal(30, 3) / decimal(10, 4)` keeps its above-floor reduced scale of 7.

`+` and `-` instead give the integral part everything it needs and hand the rest to the scale: the formula's `+1` carry digit is dropped first and there is **no** 6-floor, so `s = min(s, 38 - max(p1-s1, p2-s2))`.
`decimal(38, 20) + decimal(38, 20)` keeps scale 20, `decimal(38, 10) + decimal(38, 30)` is `decimal(38, 10)`, `decimal(30, 20) + decimal(38, 30)` is `decimal(38, 28)`, `decimal(38, 7) + decimal(38, 7)` is `decimal(38, 7)`, and `decimal(38, 38) + decimal(38, 0)` is `decimal(38, 0)` — where the excess rule would say 19 / 9 / 27 / 6 / 6.
Probe-confirmed against SQL Server 2025; the common narrow-scale shapes (`decimal(38, 2) + decimal(38, 2)`) agree under either reading, which is why the divergence stayed hidden.

`%` never reaches the cap at all: its precision is `min(p1-s1, p2-s2) + max(s1, s2)`, which 38-wide operands bound at 38.

Integer/money operands canonicalize before formulas apply (bit→(1,0) … bigint→(19,0); money→(19,4); smallmoney→(10,4)).
Pure integer-pair, pure money-pair, and float-involving arithmetic skip the decimal path (joint-envelope `Promote` instead).

`SqlType.Promote` (joint-envelope, `scale = max(s1, s2); precision = min(38, max(p1-s1, p2-s2) + scale)`) stays the right rule for non-arithmetic uses.

### Division truncates where every other operator rounds

Those formulas settle *how many* fractional digits the result keeps; the digits past them are dropped by two different rules.
Every operator but division rounds **half away from zero** at the result scale.
Division **truncates toward zero** — and does so at every cap depth rather than only where the 38-precision cap moved the scale, which is what makes the uncapped `CAST(4.00 AS decimal(5, 2)) / 7` (`decimal(9, 6)`) return `0.571428` exactly as the capped `CAST(4.00 AS decimal(38, 2)) / 7` (`decimal(38, 6)`) does.
Probe-confirmed against SQL Server 2025 (2026-08-04) across cap depths (uncapped, capped by 1, capped by 4, exactly at `p = 38`), scales (6 / 8 / 13 / 15 / 22 / 28) and both signs.

An exact half at the cut drops too, so this is truncation rather than a rounding mode: `CAST(1 AS decimal(5, 0)) / 1600000` is `0.00000062`, and `CAST(3.00 AS decimal(38, 2)) / 2000000` is `0.000001`.
The sign only moves the sign — `-4.00 / 7`, `4.00 / -7` and `-4.00 / -7` all cut at the same digit.

`money` follows the same split at its fixed scale of 4 (`$1.00 / 7` is `0.1428`, `$2.00 / 3` is `0.6666`, while `$1.0001 * $0.5555` rounds to `0.5556`), and **`AVG` inherits it** — real computes `AVG` as `SUM` / `COUNT`, so seven values summing to `4.00` average to `0.571428`, `AVG(money)` of `$1.00` over seven rows is `0.1428`, and the explicit `SUM(v) / COUNT(*)` spelling agrees.
`CAST` / `CONVERT` are untouched: a narrowing conversion still rounds (`CAST(CAST(0.5714285 AS decimal(10, 7)) AS decimal(10, 6))` is `0.571429`), so the truncation is arithmetic's, not the conversion's.

`Storage/DecimalMath.cs` carries both halves of the rule: `Truncating(dividend, divisor, scale)` is the one seam `DecimalArithmetic`'s `/`, `MoneyArithmetic`'s `/` and `AverageAggregator`'s finalize all divide through.
It scales the dividend up front rather than dividing and dropping digits after, because .NET's own division rounds at its 28-significant-digit ceiling and that rounding lands *inside* the kept digits once the result scale approaches 28 — `CAST(2 AS decimal(38, 28)) / 3` is real's `0.6666…6666` where the rounded quotient would truncate to `…6667`.
The pre-scaling runs only where it can't overflow (a divisor of magnitude 1 or more can't grow the quotient past the scaled dividend); everything else falls back to dividing first and truncating after.

### The value carries the declared scale

Those formulas settle the result *type*; the .NET `decimal` behind the value carries the same scale, so `CAST(1 AS numeric(10, 2))` is `1.00m` and not `1m`.
`SqlValue.FromDecimal` stamps it — widening adds a zero of the target scale (.NET's addition takes the larger operand's scale, leaving the number untouched) and a payload with more fractional digits than the declared scale rounds half-away-from-zero, the same rule the `F<scale>` rendering paths apply.
Because it sits in the factory every decimal-typed value passes through, one stamp covers `CAST` / `CONVERT` / `TRY_*`, arithmetic, the aggregates, the math scalars, `GENERATE_SERIES`, a column read, and the TVP / BCP / TDS / CLR ingestion paths alike; `SqlValue.AsMoney` does the same for `money` / `smallmoney`'s fixed scale of 4, which the scaled-integer storage would otherwise divide away.

It matters wherever a surface writes the raw `decimal` instead of formatting from the declared type — [the JSON builders](json.md), `FOR JSON`, `FORMAT`, and `SimulatedDbDataReader.GetDecimal` / `GetValue` (SqlClient's readers hand back the declared scale too).
Scale is invisible to `decimal` equality, comparison and hashing, so `GROUP BY` keys, `DISTINCT`, index seek keys and `SqlValue.Equals` are untouched; `CHECKSUM` renders its decimal input with `G29` so it keys off the numeric value the way real's does (`CHECKSUM(CAST(1 AS numeric(10, 2)))` equals `CHECKSUM(CAST(1 AS numeric(10, 0)))`).
The TDS encoder already rescales to the declared scale from the column metadata, so the wire bytes don't move.

The stamp's precision check bounds the value against the target's integer-digit count, which is a table lookup of the powers of ten .NET `decimal` can hold: computing that bound by repeated multiplication cost up to 28 decimal multiplies per coerced value, a seventh of a decimal-summing aggregate's whole CPU.
The magnitude test reads the value directly rather than truncating first (`|trunc(v)| > 10^k - 1` and `|v| >= 10^k` agree for every `v`, since `10^k` is an integer and truncation towards zero never crosses it), and a target with 28 or more integer digits admits everything `decimal` can hold, so it skips the compare entirely.
`SUM` over a decimal skips the coercion altogether where it provably changes nothing — the operand a decimal of the *same scale* and no more integer digits than the result type, which is the shape SQL Server's own `SUM(decimal(p, s)) → decimal(38, s)` rule produces — because a decimal `SqlValue` boxes its payload, so the discarded intermediate costs an allocation per accumulated row.

The one thing that can't be carried is a declared scale past .NET `decimal`'s 28 fractional digits, or trailing zeros with no room beside the integer part — `numeric(38, 30)`, or `numeric(38, 20)` holding a 15-digit integer part — where the value settles at the widest representation available rather than failing.
Probed against SQL Server 2025 through `JSON_ARRAY`, which writes the raw value: `+ - %` carry `max(s1, s2)`, `*` carries `s1 + s2`, `/` carries `max(6, s1 + p2 + 1)`, `SUM` keeps the column's scale, `AVG` promotes to `numeric(38, max(s, 6))`, `ROUND` / `ABS` / `SIGN` / `POWER` keep the operand's, and `CEILING` / `FLOOR` drop to `numeric(p, 0)`.
Oracle: `DecimalTests`, `MoneyTests`, `JsonBuilderTests`, and `TypeRoundTripTests` for the wire reader.

### Integer literals size by digit count against a decimal
SQL Server types an integer **literal** as `numeric(digit_count, 0)` — not `int`'s fixed precision 10 — when it is unified with a decimal/numeric partner, so `10.0/3` is `numeric(8, 6)`, not `numeric(14, 12)` (the `3` contributes `(1, 0)`; `10.0/CAST(3 AS int)` keeps `(14, 12)` since a non-literal `int` stays `(10, 0)`).
The rule is literal-specific and pervasive — it fires across `/ * + -`, `CASE`, `COALESCE` / `IIF`, and set ops — but only when the partner is decimal-category: `3 + 4` and `SELECT 1 UNION SELECT 2` stay `int`, and a money/float partner ignores the digit count (`$10.00/3` stays `money`).
`digit_count` is the significant-decimal-digit count with leading zeros excluded and a floor of 1 (`3`→1, `30`→2, `007`→1, `1234567890`→10); a negated integer literal stays a digit-count literal (`10.0/-3` matches `10.0/3` at `numeric(8, 6)`), and each literal keeps its own count through a fold (`CASE … 1 … 100 … 2.5` → `numeric(4, 1)`).
The literal never carries this sizing in a pure-integer context — an arithmetic *result* (`3 * 2`) is a plain `int`, so `10.0/(3*2)` is `numeric(14, 12)`.
The `Tokenizer`'s `Numeric` token records the count on the integer-literal branches; `Expression.IntegerLiteralDigits` reads it (seeing through parentheses, unary minus, and the projection-alias wrapper), and the promotion sites (`TwoSidedExpression` arithmetic, `SqlType.PromoteBranches` for `CASE`/`COALESCE`/`IIF`, and `Selection.CombineSetOps`) substitute `numeric(digit_count, 0)` for the literal's type when its partner is decimal.
Static (`GetSqlType`) and runtime (`Run`) stay in parity: arithmetic coerces the runtime literal *value* to `numeric(digit_count, 0)` at the node so `DecimalArithmetic` derives the same result type the schema does, and `CASE`/`COALESCE`/set ops coerce each value to the cached/combined result type.

### Integer literals past int's range type `numeric(digit_count, 0)`
SQL Server never types a bare integer literal `bigint` — it is `int` while the value fits and `numeric(digit_count, 0)` past that, with the precision tracking the written digit count: `2147483648` → `numeric(10, 0)`, `9999999999` → `numeric(10, 0)`, `10000000000` → `numeric(11, 0)`, `99999999999999999999` → `numeric(20, 0)`.
Only a CAST reaches `bigint`, so `SELECT 3000000000` and `SELECT CAST(3000000000 AS bigint)` advertise different wire types for the same value (NUMERICN at precision 10 vs BIGINT at precision 19).
Leading zeros are excluded from the count (`0000000003000000000` → `numeric(10, 0)`) and a sign doesn't change it (`-3000000000` → `numeric(10, 0)`); past 38 digits real reports **Msg 1007** rather than letting the literal reach the type factory.
Probe-confirmed against SQL Server 2025 via `sql_variant_property` and `sp_describe_first_result_set`.

The literal is always numeric-named rather than decimal-named, so it flows through the [numeric-vs-decimal](#numeric-vs-decimal-reported-type-name) metadata like any other literal, and its arithmetic follows the ordinary decimal formulas (`3000000000 + 1` → `numeric(11, 0)`, `* 2` → `numeric(12, 0)`, `/ 2` → `numeric(16, 6)`).
Because the declared precision already equals the digit count, these literals carry **no** separate `IntegerLiteralDigitCount` annotation — the digit-count sizing above is only needed on the `int` branch, where the type's fixed `(10, 0)` would otherwise win.

**The negated-constant fold.** Real folds `- <integer constant>` and types the *resulting value*, so `-2147483648` is `int` even though `2147483648` alone is `numeric(10, 0)` — and the fold sees through parentheses (`-(2147483648)` is `int` too).
`2147483648` is the only magnitude where this applies, since int's range is asymmetric by exactly one.
The fold is literal-only: unary minus over a `numeric(10, 0)` *variable* holding the same value stays `numeric(10, 0)`.
`Negate.Of` implements it at the one construction site in `Expression.ParsePrimary`.

**Row counts.** `TOP` / `OFFSET` / `FETCH` accept any integer-family value and any exact numeric at **scale 0**, narrowing the operand to `bigint`, so a past-int row count is an ordinary accepted value; a fractional scale is still the grammar's **Msg 1060**, and a 20-digit literal overflows `bigint` with Msg 8115 naming it.

### `NULLIF` narrows an integer literal

An `int`-typed integer **literal** in `NULLIF`'s *first* slot is sized down to the narrowest integer type that holds its value — `tinyint` for `0`..`255`, `smallint` for `-32768`..`32767`, `int` otherwise.
So `NULLIF(60, 76)` is `tinyint`, `NULLIF(-3, 78)` and `NULLIF(300, 4)` are `smallint`, and `NULLIF(99999999, 4)` stays `int`, where a bare `SELECT 60` is `int` like any other integer literal.
Probe-confirmed against SQL Server 2025 through `sys.dm_exec_describe_first_result_set` and through `SELECT … INTO`, whose destination column is declared at the narrowed type — so the narrowing is real DDL, not just reported metadata.

The rule reads the **first argument alone**.
The second contributes nothing whatever it is: a wider literal (`NULLIF(1, 2147483648)` → `tinyint`), a `CAST`, a column, a variable, or a type that doesn't even compare.
And it is `NULLIF`'s own — the `CASE` it is defined as, and every sibling value-selecting form (`COALESCE` / `ISNULL` / `IIF` / `CHOOSE` / `GREATEST` / `LEAST`), all leave the same `60` at `int` — which is why it can't ride the shared `PromoteBranches` seam.

Only a **written literal** narrows, seen through the wrappers real's own fold sees through (parentheses, unary `+`, unary `-` to any depth, so `-(-60)` narrows to `tinyint`): `NULLIF(CAST(60 AS int), 76)` and `NULLIF(60 + 0, 76)` stay `int`.
A literal whose own type isn't `int` keeps that type, so `NULLIF(60.0, 76)` is `numeric(3, 1)` and `NULLIF(2147483648, 1)` is `numeric(10, 0)` per the past-int-range rule above; the `-2147483648` negated-constant fold reaches the check already folded and narrows against int's range like any other int literal.

`Expression.IntegerLiteralValue` walks the same wrappers `IntegerLiteralDigits` does but reports the signed value (in `long`, so `-(-2147483648)` reports out of range rather than wrapping), and `NullIf` settles the narrowed type at construction — it depends only on the syntax tree — so `Run` and `GetSqlType` read one answer and the surviving value is coerced to it.
Oracle: `NullIfLiteralNarrowingTests`.

**Divergence.** A literal written with enough leading zeros to exceed 12 characters is `numeric(significant_digits, 0)` on real rather than `int` (`SELECT 0000000000300` → `numeric(3, 0)`, while the 11-character `00000000300` is `int`), which `NULLIF` then inherits — `NULLIF(0000000000300, 1)` is `numeric(3, 0)` on real and `smallint` here.
That is the bare-literal typing rule's, not `NULLIF`'s; the simulator types `0000000000300` as `int` at the tokenizer.

### Decimal-literal precision (leading zero, leading dot)
A decimal literal's precision is its significant-digit count where an integer part of exactly `0` contributes nothing, plus the fractional digit count, floored at 1; scale is the fractional digit count.
So `0.1` → `numeric(1, 1)`, `0.05` → `(2, 2)`, `0.00` → `(2, 2)`, `0.10` → `(2, 2)` (a written trailing zero still counts), while a significant leading digit counts normally (`1.5` → `(2, 1)`, `100.0` → `(4, 1)`) — probe-confirmed against SQL Server 2025.
The floor applies to the *summed* precision, not the integer part alone (`Math.Max(1, integerDigits + fractionalDigits)`); flooring the integer part first over-counted `0.1` to `(2, 1)`.
A literal may also omit the leading integer digit: `.5` = `0.5` → `(1, 1)`, `.05` → `(2, 2)` — the `Tokenizer` dispatches a `.` immediately followed by a digit to the same decimal-literal path (a bare `.` or a trailing second `.` stays an operator, so `SELECT .` and `SELECT 1..2` still raise Msg 102).
Both live in `Parser/Tokens/Numeric.cs` (precision) and `Parser/Tokenizer.cs` (`NextToken` dispatch + `ParseNumeric` leading-dot span).

### Numeric-vs-decimal reported type name
`decimal(p, s)` and `numeric(p, s)` are the same storage type (one `DecimalSqlType`), but SQL Server reports two different type names, and the choice propagates through expressions.
A projected decimal-family column reports `numeric` when its value traces back to a numeric-named source, else `decimal` — probe-confirmed: a decimal/numeric **literal** is always numeric-named (`10.0` → numeric), `CAST`/`CONVERT … AS numeric` → numeric (`… AS decimal` → decimal), **arithmetic** is numeric if ANY contributing decimal-family operand is numeric-named (`10.0 + 1` → numeric, `d + 1` → decimal, `d * 100.0` → numeric), decimal-returning **functions preserve** their operand's name (`ROUND`/`CEILING`/`FLOOR`/`ABS`/`SIGN`/`DEGREES`/`RADIANS`/unary-minus of a literal → numeric; `POWER` takes its base's name; `SUM`/`AVG` of a decimal column → decimal), and **value-selecting** forms are numeric if ANY value arm they can produce is (`CASE`/`COALESCE`/`IIF`/`ISNULL`/`NULLIF`/`CHOOSE`/`GREATEST`/`LEAST` with a numeric-named arm → numeric).
The name is **metadata only** — never part of `SqlType` identity/equality, since `decimal(5, 2)` and `numeric(5, 2)` must stay storage-equal or the row encoder's `valueType == columnType` check rejects inserts.
It rides `Expression.ResultReportsNumeric` (a structural recursion, default `false`, overridden on `Value`/`Cast`/`ConvertExpression`/`TwoSidedExpression`/`Round`/`Ceiling`/`Floor`/`AbsoluteValue`/`Sign`/`Degrees`/`Radians`/`Power`/`Negate`/`Parenthesized`/`NamedExpression`/`AggregateExpression`/`CaseExpression`/`Coalesce`/`Iif`/`IsNullExpression`/`NullIf`/`Choose`/`GreatestLeast`), gets computed per projection column into `Selection.ColumnReportsNumeric` (only where the column is `decimal`-family), flows to `SimulatedQueryResult.ColumnReportsNumeric`, and is read by `SimulatedDbDataReader.GetDataTypeName` (→ `numeric`) and the TDS COLMETADATA writer (NUMERICN `0x6C` vs DECIMALN `0x6A`; identical wire body).
**Deferred boundary — column-source name.** A decimal value read from a *column source* — a declared column, or a derived-table / `VALUES` / set-op-subquery column — reports `decimal` even where real reports `numeric` (`SELECT n FROM t` with `n numeric`, `d + n`, `AVG(v) FROM (VALUES(1.0),(2.0)) t(v)`, `SELECT v FROM (SELECT 1 UNION SELECT 2.5) t`). Each needs the column source to remember its name (on `HeapColumn` / the derived-table schema), which risks the storage-equality invariant, so these stay unmodeled; every direct-expression source is covered.

### Unary minus preserves the operand's type
Unary minus is a dedicated `Negate` node, not `0 - x` — negating through a subtraction against a typed `int` zero would inflate an exact-numeric's precision by one (the additive `+1`) and re-type integers against `(10, 0)`.
`Negate` preserves the operand's own precision/scale/family (`-1.1` → `numeric(2, 1)`, `-CAST(1.5 AS decimal(5, 3))` → `decimal(5, 3)`, `-CAST(1 AS bigint)` → `bigint`, `-$1.00` → `money`, `-CAST(1 AS real)` → `real`), widens the unsigned `tinyint` to `smallint` (negation needs a signed type), and raises Msg 8117 for `bit`.
The *value* is still computed via the shared `0 - x` arithmetic (so string coercion, date rejection, NULL propagation, and overflow all match the subtraction path), then re-boxed to the preserved type; only the five diverging cases (decimal / real / smallint / tinyint / bit) override the additive result — money / smallmoney / float / int / bigint the additive path already types correctly.
The one exception is `float` / `real`, which negate by flipping the IEEE sign bit instead, because the subtraction folds the two zeros together — see [Negative zero](#negative-zero).

### Untyped NULL yields to a typed operand
A bare `NULL` keyword is typed `int` as a placeholder (SQL Server has no truly untyped NULL), but that placeholder must not win a joint promotion: `COALESCE(NULL, 'z')` and `ISNULL(NULL, 'z')` are `varchar` (returning `'z'`), not `int`
The bare-`NULL` `Value` carries an `IsUntypedNull` flag (distinct from a typed NULL like `@@REMSERVER` or `CAST(NULL AS varchar)`); `COALESCE` / `ISNULL` / `CASE` / `IIF` skip untyped-NULL arms in `SqlType.PromoteBranches` (via `Expression.PromoteValueArms`), so an untyped NULL yields to any typed sibling.
A NULL with no typed sibling still resolves to `int` (`SELECT NULL` stays `int`), matching real.
`ISNULL` fixes the result to its first argument's type but yields when that argument is an untyped NULL; it never joint-promotes, so no digit-count sizing applies there (`ISNULL(1, 2.5)` stays `int`).

## String / binary width algebra

String and binary literals type at their **exact value width**, and that width flows through the combining operators to COLMETADATA / `GetColumnSchema().ColumnSize` — a bare `'included'` advertises `varchar(8)`, not the `varchar(8000)` container it once did (probed against SQL Server 2025; sqlcmd was rendering absurdly wide columns off the container width).

### Literal typing (`Tokenizer`)
- `'abc'` → `varchar(3)`, `N'abc'` → `nvarchar(3)` (code units, not bytes), `0xAABB` → `varbinary(2)`.
  Trailing spaces count (`'ab  '` → `varchar(4)`).
- The empty literal floors to width 1 (`''` → `varchar(1)`, `N''` → `nvarchar(1)`, `0x` → `varbinary(1)`) — SQL Server has no zero-width string type.
- A literal past the family bound widens to the MAX form (`'…'` > 8000 chars → `varchar(MAX)`, `N'…'` > 4000 → `nvarchar(MAX)`, `0x…` > 8000 bytes → `varbinary(MAX)`).
- Collation typing is unchanged — literals still carry the active collation at `CoercibleDefault`.
  Only the length parameter moved off the length-0 sentinel.
  `FromVarchar(string)` / `FromNVarchar(string)` / `FromVarbinary(byte[])` (the length-0 factories used by built-in scalars) are untouched, so built-in message scalars (`@@VERSION`, `DATENAME`, `FORMAT`, `ERROR_MESSAGE`) stay container-width — see the rejected-blanket-flip note below.

### Combine rules
- **Concatenation `+`** (`PromoteForArithmetic` string arm / `StringConcatResult`): sum of widths, capped at the family maximum (`'ab' + 'cde'` → `varchar(5)`; `varchar(5000) + varchar(5000)` → `varchar(8000)`, **not** MAX).
  National family (nvarchar/nchar) wins and the sum stays in characters across the family change (`'ab' + N'cde'` → `nvarchar(5)`).
  Either operand MAX → MAX.
- **CASE / COALESCE / IIF / NULLIF / set ops / comparison common-type** (`SqlType.Promote` → `PromoteStringPair`): **maximum** of the operand widths (not the sum).
  `CASE … 'ab' … 'wxyz'` → `varchar(4)`; `… N'wxyz'` → `nvarchar(4)`; `SELECT 'ab' UNION ALL SELECT 'wxyz'` → `varchar(4)`.
  Fixed pairs (char/nchar) stay fixed; any variable operand drops to the variable form; either operand MAX → MAX.
  The length-0 sentinel contributes 0 to the max so a bare var\* operand yields to a sized partner.
  (Before this, `PromoteFromString` fell through to a precedence pick that returned one whole operand — `Promote(varchar(2), varchar(4))` gave `varchar(2)`, narrower than a runtime value; the max rule is the parity fix.)
- **ISNULL** fixes the result to the **first** argument's declared type/width (`ISNULL('ab', 'wxyz')` → `varchar(2)`), unlike COALESCE's joint promote — see [`dml.md`](dml.md)/`IsNullExpression`.

### Per-function widths (`StringScalars` helpers)
Length-deriving scalars compute their projected width the way SQL Server does when the count/length argument is a **constant literal** (const-folded via `StringScalars.TryConstantCount`), else fall back to the family container:
- `LEFT` / `RIGHT` / `SUBSTRING` → `min(inputWidth, n)` (start does not affect SUBSTRING's width); width 0 floors to 1.
- `REPLICATE` → `min(cap, inputWidth × count)`; `REPLICATE(varchar(5), 3)` → `varchar(15)`; a `varchar(MAX)` input carries MAX through.
- `SPACE` → `varchar(min(8000, n))`; `SPACE(0)` → `varchar(1)`.
- `STUFF` → `inputWidth − min(length, inputWidth − start + 1) + replacementWidth`, capped; `STUFF(varchar(10), 8, 5, 'XY')` → `varchar(9)` (only 3 chars remain to delete).
- `REPLACE` / `TRANSLATE` → family container (`varchar(8000)` / `nvarchar(4000)`) always — they can grow the input by an unbounded factor.
  `UPPER` / `LOWER` / `LTRIM` / `RTRIM` / `TRIM` / `REVERSE` preserve the input width.
  `QUOTENAME` → `nvarchar(258)` fixed.

**Static / runtime parity is load-bearing** here: the projected width (`GetSqlType`) must never fall below the value `Run` materializes, or the row encoder / wire prefix rejects.
The const-fold path guarantees this — a value fits its declared input width, so `input × count`, `input − delete + replacement`, etc. bound the runtime output; the non-constant path falls back to the container, which is always wide enough.

### Binary length-variance comparison
Two binary literals carry distinct exact widths, so `SqlValue.CompareTo` / `Equals` admit **length-only variance within a binary family** (`varbinary(1)` vs `varbinary(2)`, `binary(N)` vs `binary(M)`) — the arms already compare raw byte spans, and `varbinary` coercion doesn't pin the target length, so the strict type-identity guard would otherwise throw (`IsLengthOnlyBinaryVariance`).
Byte-span ordering is unchanged (`0x01 < 0x0100`: shorter-is-less, no right-padding).

### Error-message wording
The Msg 244 / 248 overflow and Msg 8116 / 447 invalid-type factories render the source type by its **bare** `SqlServerName` / `FamilyRootName` (`varchar`), never `ToString()` (`varchar(3)`) — real SQL Server's wording omits the width.
(The literal-width change surfaced three sites still using `ToString()`.)

### Rejected: systemic length-0 → MAX wire flip
Considered and rejected (during the length-0 max-scalar audit — the `OBJECT_DEFINITION`-style silent-session-kill class): making the TDS codec treat *every* length-0 (value-width) var-column as MAX at COLMETADATA + value time would blanket-defend against any residual length-0 result over 32,767 chars, but it's a **fidelity regression** — real SQL Server advertises correctly-bounded scalars (`DATENAME`, `FORMAT`, `ERROR_MESSAGE`, string literals) as `nvarchar(4000)` / `varchar(8000)`, not MAX, so the blanket flip would make the common case less faithful to defend a rare one.
Rejected because (a) the acute silent-kill is already neutralized generically by `TdsTypeCodec.BoundedWireLength`, which converts a residual bounded-column overflow into a caught `InvalidDataException` (clean session end, not a silent transport death), and (b) every genuinely-MAX length-0 scalar was retyped per-scalar (`SqlType.NVarcharMax` / `VarcharMax` / `VarbinaryMax`: JSON_QUERY/MODIFY/OBJECT/ARRAY, STRING_ESCAPE, CONCAT/CONCAT_WS max-propagation, TRANSLATE max-propagation, COMPRESS/DECOMPRESS) or capped to a safe bound (JSON_VALUE 4000 → NULL, FORMATMESSAGE 2047, STRING_AGG Msg 9829 at 8000 bytes).
If a future length-0 crash vector surfaces, prefer per-scalar retyping over the blanket flip.

### Residual divergences
- `@@VERSION` and the built-in message scalars stay container-class (`nvarchar(4000)`), not real's exact `nvarchar(300)` — retyping the built-in catalog *wholesale* to real's exact widths was weighed and rejected as broad churn (see the rejected flip); per-scalar retyping stays the route when a specific width is shown to matter.
- `TRANSLATE` projects `nvarchar` container even for a `varchar` input (a family divergence — it coerces to nvarchar internally); real keeps the `varchar` family.
