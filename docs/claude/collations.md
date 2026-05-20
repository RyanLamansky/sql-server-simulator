# Collations — per-column declaration, coercibility, Msg 468 / 457

Every string-categorized `SqlType` instance carries a `(Collation, Coercibility)` pair. CREATE TABLE / ALTER COLUMN pin the declared collation at `Implicit` rank onto the column's `SqlType`; values decoded from the column inherit that type instance through row decode, so `SqlValue.CompareTo` / `Equals` / `GetHashCode` honor the declared rules. Cross-collation operand pairs that can't be resolved by coercibility raise Msg 468 (comparison / set ops / LIKE) or Msg 457 (concat / UNION ALL / DISTINCT over unresolved).

## Type-side wiring

`VarcharSqlType` / `NVarcharSqlType` / `CharSqlType` / `NCharSqlType` intern per `(length, Collation, Coercibility)` trio via a 3-tuple `ConcurrentDictionary`. The existing length-only `Get(N)` overloads return the `(N, Collation.Default, CoercibleDefault)` variant — matching the literal / parameter / CAST-of-literal contexts that historically didn't pin collation. New `Get(length, collation, coercibility)` overloads return the column-pinned variant.

`SystemNameSqlType` / `TextSqlType` / `NTextSqlType` are single shared instances reporting `Collation.Default` at `Implicit` rank — sysname/text/ntext don't accept per-column COLLATE in the simulator (sysname rejects per grammar; text/ntext deferred as deprecated).

`SqlType.WithCollation(Collation, Coercibility)` is the virtual that rewraps a string type with new metadata; non-string types and the sysname/text/ntext singletons return `this`. Used by `CollateExpression` for the postfix override and by CREATE TABLE / ALTER COLUMN for the column declaration.

## Coercibility precedence

`Coercibility` enum: `CoercibleDefault` (0) &lt; `Implicit` (1) &lt; `Explicit` (2). Maps to SQL Server's collation-precedence ranks:

| Rank | Source | Wins over |
|---|---|---|
| `CoercibleDefault` | Literal, parameter, CAST of a coercible-default source, system-function result | nothing |
| `Implicit` | Column reference, CAST of a column, computed-column expression | `CoercibleDefault` |
| `Explicit` | `COLLATE` postfix on an expression | both lower ranks |

`Collation.Resolve(SqlType, SqlType)` returns `(Collation, Coercibility)?`: the winning pair when one rank is higher, the shared collation when both are the same rank, `null` when both are the same rank but the collations differ (caller raises Msg 468 / 457).

## Value-side compare path

`SqlValue.CompareTo` / `Equals` / `GetHashCode` route through `this.Type.Collation ?? Collation.Default`. Same-type pairs (which after the interning split are also same-collation pairs) take the fast path. Cross-type cases flow through `CompareValuesPromoted` (in `BooleanExpression.cs`), which:

1. Rejects LOB-typed operands (Msg 402 — unchanged).
2. Runs `Collation.Resolve` for string-string pairs with different types; raises **Msg 468 State 9** on conflict (probe-confirmed wording: `Cannot resolve the collation conflict between "X" and "Y" in the <op> operation.`). The check fires before NULL short-circuits, matching real SQL Server (`NULL = NULL` across cross-collation columns also raises).
3. Falls through to `SqlType.Promote` + per-side `CoerceTo` for the value coercion.

The fast path uses `SqlValue.WithType` to re-tag a value with a different `SqlType` instance — used by `CollateExpression.Run` to apply the explicit collation rewrap without re-allocating the underlying string reference.

## Decode preserves column type instance

`VarcharSqlType.Decode` and `NVarcharSqlType.Decode` now thread `this` (the actual interned instance) into `SqlValue.FromVarchar(VarcharSqlType, string)` / `FromNVarchar(NVarcharSqlType, string)`. Previously they hit the singleton `FromVarchar(string)` overload and lost the column's collation/coercibility — same latent bug also affected `SqlValue.FromString(type, value)` which used to drop the target type during cross-string coercion. Both fixed in this bundle.

`CharSqlType.Decode` / `NCharSqlType.Decode` already passed `this` (via `FromChar` / `FromNChar`); collation came along free once the type-side wiring landed.

## Operator-site enforcement

- **Comparison (`=` / `&lt;&gt;` / `&lt;` / `&gt;` / `&lt;=` / `&gt;=` / `IN` / `BETWEEN` / `ALL` / `ANY`)** — all funnel through `CompareValuesPromoted`. Msg 468 with the per-op name (`"equal to"`, `"not equal to"`, `"less than"`, …).
- **`LIKE`** — `LikeExpression.Run` calls `Collation.Resolve(l.Type, r.Type)` on the runtime values' types. Replaces the old parse-time `PeelExplicitCollation` walk, which only caught explicit COLLATE postfixes. Conflict raises Msg 468 with operator name `"like"`. The resolved `Collation.CaseSensitive` flag flips `RegexOptions.IgnoreCase`.
- **String concat (`+`)** — `Add.StringConcatenation` calls `Collation.Resolve` on the operand pair. Conflict raises **Msg 457 State 1** (`Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict.`). `TwoSidedExpression.GetSqlType` mirrors the same resolution so the projection schema's result type matches the runtime value's type — RowEncoder rejects mismatched instances, so the GetSqlType / Run paths must stay aligned.

## `COLLATE` postfix

`CollateExpression.Run` rewraps the value's type via `WithCollation(this.ResolvedCollation, Coercibility.Explicit)`. `GetSqlType` propagates the same override through projection. Non-string inner raises Msg 447 at runtime (real SQL Server raises at bind time — same message, just earlier; lazy-plan parity).

Chained `expr COLLATE A COLLATE B` rejects with Msg 156 at parse time (probe-confirmed). Unknown collation name raises Msg 448 at parse time.

## Database default and `#temp` inheritance

`Simulation.Create.cs`'s column wiring (shared by CREATE TABLE / ALTER TABLE ADD / DECLARE @t / CREATE TYPE AS TABLE / temp-table paths) resolves the column's pinned collation as: explicit `COLLATE` clause first, else the active database's `Database.CollationName`, else `Collation.Default`. So `#temp` tables created while a BACPAC-loaded non-default-collation database is active inherit that database's collation — avoiding the EF temp-join footgun (real SQL Server's tempdb is independent, but the common shape — tempdb matches server default which matches user DB — collapses to the same behavior).

`ALTER COLUMN` without an explicit `COLLATE` clause preserves the existing column's collation (probe-aligned). With an explicit `COLLATE`, the new collation pins at `Implicit` rank.

## Recognized catalog

12 entries today. Resolution at parse / load time consults the case-insensitive `Collation.ByName` map; names outside the set raise `NotSupportedException` in direct SQL and surface on `BacpacImportResult.Warnings` for BACPAC loads (graceful degradation).

| Name | Comparer | Notes |
|---|---|---|
| `SQL_Latin1_General_CP1_CI_AS` | Invariant culture CI + IgnoreKanaType + IgnoreWidth | Default. Sort doesn't ignore symbols (apostrophe / hyphen significant). |
| `Latin1_General_100_CI_AS` | Invariant culture CI + IgnoreKanaType + IgnoreWidth | Sort layers `IgnoreSymbols` on top — primary-weight-zero punctuation collapses. |
| `Latin1_General_CI_AS` | Same body as the `_100_` variant | Pre-v100 Unicode-table difference not modeled (no non-Latin script change on the inputs the regression bar exercises). |
| `Latin1_General_CS_AS` | Invariant culture + IgnoreKanaType + IgnoreWidth | No IgnoreCase. |
| `Latin1_General_BIN` | `StringComparer.Ordinal` | Pure codepoint. |
| `Latin1_General_BIN2` | `StringComparer.Ordinal` | Same body as BIN; the BIN-vs-BIN2 non-Unicode-`varchar` asymmetry isn't observable through the simulator's SQL surface. |
| `Latin1_General_CI_AS_KS_WS` | Invariant culture CI (KS / WS preserved) | Appears on sysname-backed columns in real DBs; included for BACPAC quiet-loading. |
| `SQL_Latin1_General_CP437_CS_AS` | Invariant culture case-sensitive | Legacy CP437 binding; one column per AdventureWorks-class DB. |
| `UNICODE_CODEPOINT` | `StringComparer.Ordinal` (`BinaryCollation` body) | Semantically equivalent to BIN2 at the value level; appears in AdventureWorks2025. |
| `Japanese_XJIS_140_CI_AS` | `ja-JP` `CompareInfo` + CI + KanaType-/Width-insensitive | Per-name sort-order parity with real SQL Server not yet probed. |
| `Chinese_PRC_CI_AS` | `zh-CN` `CompareInfo` + CI | Per-name parity not yet probed. |
| `Turkish_CI_AS` | `tr-TR` `CompareInfo` + CI | Handles the i / İ / ı / I folding (the "Turkish-i problem"). Per-name parity not yet probed. |

Generic culture-based collations use the `CultureCollation` class — name + culture + case-sensitive flag drive comparer construction.

## Known gaps

- **Per-name sort-order parity for the three locale comparers isn't probe-verified.** `CompareInfo` for `ja-JP` / `zh-CN` / `tr-TR` produces plausible orderings, but SQL Server's collations don't always 1:1 match .NET cultures (e.g. SQL's `Japanese_XJIS_*` mapping table for supplementary characters isn't `CompareInfo`'s default). Apps that assert on exact byte-for-byte sort order of CJK data need per-name probing.
- **Broader locale catalog deferred** — Korean, Greek, Cyrillic, German, Modern Spanish, the `_140_*_UTF8` variants. The pattern (add a `CultureCollation` instance + entries to `Recognized` / `ByName`) is the same; each new locale needs a probe-and-verify pass before shipping.
- **Set ops (UNION / UNION ALL / INTERSECT / EXCEPT) don't apply collation-conflict checks at the column-pair level yet.** Probe showed UNION raises Msg 468, UNION ALL raises Msg 457 across cross-collation columns; the simulator's set-op type-promotion path doesn't call `Collation.Resolve`. Cross-collation set-op columns currently fall through to the legacy type-precedence resolution.
- **`text` / `ntext` columns can't be declared with an explicit COLLATE in the simulator.** Real SQL Server allows it; the simulator's single-instance modeling collapses all text/ntext to the default. Low impact (text/ntext deprecated since SQL Server 2005).
- **Sysname's collation is always `Collation.Default`** at `Implicit` rank — real SQL Server's sysname inherits the server's catalog collation which can differ from the user database's collation; the simulator's single-instance modeling collapses them.

## Cross-references

- Database-level `ALTER DATABASE COLLATE` and the `Collation.Recognized` whitelist → [`database-options.md`](database-options.md).
- BACPAC import collation handling (loader warns on names outside `Recognized` and continues) → [`bacpac-loader.md`](bacpac-loader.md).
