# Type promotion and decimal arithmetic

## Integer ↔ string promotion
Cross-category `int ↔ string` lands the integer's specific subtype (`tinyint + '3'` stays tinyint; `bigint + '3'` stays bigint). String parses through the integer's CAST path: empty/whitespace → 0, `+`/`-` accepted, leading/trailing whitespace trimmed. **Decimal-shaped strings (`'5.5'`) raise Msg 245** rather than routing through decimal. Hex (`'0x05'`) likewise rejected.

`bit ↔ string` asymmetry: comparison works (`'true'`/`'false'`/empty → true/false/false; non-zero digit string → True regardless of magnitude); `bit + str` rejected — `+`/`-`/`%` → Msg 402, `*`/`/` → Msg 8117 with LEFT operand's type only.

WHERE on a varchar column compared against int halts on the first unparseable row (not isolated as per-row UNKNOWN). SQL Server's lazy-IN quirk (unparseable IN-list value suppressed when another matches) isn't modeled.

## Binary operand promotion
One `binary`/`varbinary` operand paired with one integer-family operand converts the **binary side** to the integer type — for arithmetic (`+ - * / %`) *and* bitwise (`& | ^`), so the result keeps the integer partner's specific subtype (`1 + 0x01` → int 2; `cast(5 as bigint) / 0x02` → bigint 2; `cast(5 as tinyint) + 0x01` → tinyint 6; `255 & 0x01` → int 1). Comparison converts the same way (`0x01 = 1` compares equal). `SqlType.Promote` handles the type unification (binary-vs-integer → the integer type), and `TwoSidedExpression.IntegerArithmetic` coerces the runtime binary value via the binary→integer path (see [`casting.md`](casting.md)); the string↔integer normalization sitting beside it excludes bitwise, but the binary path does not.

Two **binary** operands: `+` is byte concatenation (`0x01 + 0x01` → varbinary `0x0101`; `binary(N) + binary(M)` → `binary(N+M)`, else `varbinary(N+M)`, capped 8000 — `Add.BinaryConcatenation` + `PromoteForArithmetic`'s `BinaryPairResultType`). Every other operator errors, matching SQL Server: `- % & | ^` → **Msg 402** (`"The data types varbinary and varbinary are incompatible in the '&' operator."`), `* /` → **Msg 8117** (`"Operand data type varbinary is invalid for multiply operator."`). `PromoteForArithmetic` raises for the static schema; `IntegerArithmetic` re-raises the same wording at runtime.

`BuildSynthesizedSqlRow` (FROM-less SELECT) runs each expression first (surfacing runtime-only errors with operator-name wording), then `GetSqlType` for schema, then bridges any mismatch via `CoerceTo` — required for mixed-type CASE/Coalesce without a FROM clause.

## Decimal arithmetic precision / scale
Per-operator decimal scale rules differ from the joint-envelope rule used for non-arithmetic uses (comparison / COALESCE / set ops):
- `+` / `-`: `p = max(p1-s1, p2-s2) + max(s1, s2) + 1`, `s = max(s1, s2)`
- `*`: `p = p1 + p2 + 1`, `s = s1 + s2`
- `/`: `s = max(6, s1 + p2 + 1)`, `p = p1 - s1 + s2 + s`
- `%`: `p = min(p1-s1, p2-s2) + max(s1, s2)`, `s = max(s1, s2)`

When precision exceeds 38, scale reduces by the excess down to a floor of `min(originalScale, 6)`; precision clips to 38. The 6-floor stabilizes division (`s ≥ 6` always); for `+ - * %` it binds only when original scale was already ≤ 6.

Integer/money operands canonicalize before formulas apply (bit→(1,0) … bigint→(19,0); money→(19,4); smallmoney→(10,4)). Pure integer-pair, pure money-pair, and float-involving arithmetic skip the decimal path (joint-envelope `Promote` instead).

`SqlType.Promote` (joint-envelope, `scale = max(s1, s2); precision = min(38, max(p1-s1, p2-s2) + scale)`) stays the right rule for non-arithmetic uses.
