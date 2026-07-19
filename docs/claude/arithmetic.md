# Type promotion and decimal arithmetic

## Integer ↔ string promotion
Cross-category `int ↔ string` lands the integer's specific subtype (`tinyint + '3'` stays tinyint; `bigint + '3'` stays bigint).
String parses through the integer's CAST path: empty/whitespace → 0, `+`/`-` accepted, leading/trailing whitespace trimmed.
**Decimal-shaped strings (`'5.5'`) raise Msg 245** rather than routing through decimal.
Hex (`'0x05'`) likewise rejected.

`bit ↔ string` asymmetry: comparison works (`'true'`/`'false'`/empty → true/false/false; non-zero digit string → True regardless of magnitude); `bit + str` rejected — `+`/`-`/`%` → Msg 402, `*`/`/` → Msg 8117 with LEFT operand's type only.

WHERE on a varchar column compared against int halts on the first unparseable row (not isolated as per-row UNKNOWN).
SQL Server's lazy-IN quirk (unparseable IN-list value suppressed when another matches) isn't modeled.

## Binary operand promotion
One `binary`/`varbinary` operand paired with one integer-family operand converts the **binary side** to the integer type — for arithmetic (`+ - * / %`) *and* bitwise (`& | ^`), so the result keeps the integer partner's specific subtype (`1 + 0x01` → int 2; `cast(5 as bigint) / 0x02` → bigint 2; `cast(5 as tinyint) + 0x01` → tinyint 6; `255 & 0x01` → int 1).
Comparison converts the same way (`0x01 = 1` compares equal).
`SqlType.Promote` handles the type unification (binary-vs-integer → the integer type), and `TwoSidedExpression.IntegerArithmetic` coerces the runtime binary value via the binary→integer path (see [`casting.md`](casting.md)); the string↔integer normalization sitting beside it excludes bitwise, but the binary path does not.

Two **binary** operands: `+` is byte concatenation (`0x01 + 0x01` → varbinary `0x0101`; `binary(N) + binary(M)` → `binary(N+M)`, else `varbinary(N+M)`, capped 8000 — `Add.BinaryConcatenation` + `PromoteForArithmetic`'s `BinaryPairResultType`).
Every other operator errors, matching SQL Server: `- % & | ^` → **Msg 402** (`"The data types varbinary and varbinary are incompatible in the '&' operator."`), `* /` → **Msg 8117** (`"Operand data type varbinary is invalid for multiply operator."`).
`PromoteForArithmetic` raises for the static schema; `IntegerArithmetic` re-raises the same wording at runtime.

`BuildSynthesizedSqlRow` (FROM-less SELECT) runs each expression first (surfacing runtime-only errors with operator-name wording), then `GetSqlType` for schema, then bridges any mismatch via `CoerceTo` — required for mixed-type CASE/Coalesce without a FROM clause.

## Decimal arithmetic precision / scale
Per-operator decimal scale rules differ from the joint-envelope rule used for non-arithmetic uses (comparison / COALESCE / set ops):
- `+` / `-`: `p = max(p1-s1, p2-s2) + max(s1, s2) + 1`, `s = max(s1, s2)`
- `*`: `p = p1 + p2 + 1`, `s = s1 + s2`
- `/`: `s = max(6, s1 + p2 + 1)`, `p = p1 - s1 + s2 + s`
- `%`: `p = min(p1-s1, p2-s2) + max(s1, s2)`, `s = max(s1, s2)`

When precision exceeds 38, scale reduces by the excess down to a floor of `min(originalScale, 6)`; precision clips to 38.
The 6-floor stabilizes division (`s ≥ 6` always); for `+ - * %` it binds only when original scale was already ≤ 6.

Integer/money operands canonicalize before formulas apply (bit→(1,0) … bigint→(19,0); money→(19,4); smallmoney→(10,4)).
Pure integer-pair, pure money-pair, and float-involving arithmetic skip the decimal path (joint-envelope `Promote` instead).

`SqlType.Promote` (joint-envelope, `scale = max(s1, s2); precision = min(38, max(p1-s1, p2-s2) + scale)`) stays the right rule for non-arithmetic uses.

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
Two binary literals now carry distinct exact widths, so `SqlValue.CompareTo` / `Equals` admit **length-only variance within a binary family** (`varbinary(1)` vs `varbinary(2)`, `binary(N)` vs `binary(M)`) — the arms already compare raw byte spans, and `varbinary` coercion doesn't pin the target length, so the strict type-identity guard would otherwise throw (`IsLengthOnlyBinaryVariance`).
Byte-span ordering is unchanged (`0x01 < 0x0100`: shorter-is-less, no right-padding).

### Error-message wording
The Msg 244 / 248 overflow and Msg 8116 / 447 invalid-type factories render the source type by its **bare** `SqlServerName` / `FamilyRootName` (`varchar`), never `ToString()` (`varchar(3)`) — real SQL Server's wording omits the width.
(The literal-width change surfaced three sites still using `ToString()`.)

### Rejected: systemic length-0 → MAX wire flip
Considered and rejected (2026-07-16, during the length-0 max-scalar audit — the `OBJECT_DEFINITION`-style silent-session-kill class): making the TDS codec treat *every* length-0 (value-width) var-column as MAX at COLMETADATA + value time would blanket-defend against any residual length-0 result over 32,767 chars, but it's a **fidelity regression** — real SQL Server advertises correctly-bounded scalars (`DATENAME`, `FORMAT`, `ERROR_MESSAGE`, string literals) as `nvarchar(4000)` / `varchar(8000)`, not MAX, so the blanket flip would make the common case less faithful to defend a rare one.
Rejected because (a) the acute silent-kill is already neutralized generically by `TdsTypeCodec.BoundedWireLength`, which converts a residual bounded-column overflow into a caught `InvalidDataException` (clean session end, not a silent transport death), and (b) every genuinely-MAX length-0 scalar was retyped per-scalar (`SqlType.NVarcharMax` / `VarcharMax` / `VarbinaryMax`: JSON_QUERY/MODIFY/OBJECT/ARRAY, STRING_ESCAPE, CONCAT/CONCAT_WS max-propagation, TRANSLATE max-propagation, COMPRESS/DECOMPRESS) or capped to a safe bound (JSON_VALUE 4000 → NULL, FORMATMESSAGE 2047, STRING_AGG Msg 9829 at 8000 bytes).
If a future length-0 crash vector surfaces, prefer per-scalar retyping over the blanket flip.

### Residual divergences (deliberate)
- `@@VERSION` and the built-in message scalars stay container-class (`nvarchar(4000)`), not real's exact `nvarchar(300)` — retyping built-ins is deliberately out of scope (see the rejected flip).
- `TRANSLATE` projects `nvarchar` container even for a `varchar` input (a pre-existing family divergence — it coerces to nvarchar internally); real keeps the `varchar` family.
- `COALESCE(NULL, 'literal')` types as `int` (bare `NULL` is `Int32`, which poisons the promote), where real gives the string type — a pre-existing bare-NULL-typing quirk orthogonal to width.
