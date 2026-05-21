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

27 entries today. Resolution at parse / load time consults the case-insensitive `Collation.ByName` map; names outside the set raise `NotSupportedException` in direct SQL and surface on `BacpacImportResult.Warnings` for BACPAC loads (graceful degradation).

### Latin1 / SQL_Latin1

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
| `Latin1_General_100_CI_AS_SC_UTF8` | Invariant culture CI (same body as `_100_CI_AS`) | UTF-8 is a storage encoding only at the simulator's UTF-16 value layer; `_SC` (supplementary characters) is handled natively by `CompareInfo`. |
| `Latin1_General_100_CS_AS_SC_UTF8` | Invariant culture CS | Same body as `Latin1_General_CS_AS`. |
| `Latin1_General_100_BIN2_UTF8` | `StringComparer.Ordinal` (`BinaryCollation` body) | Pure codepoint binary; UTF-8 storage doesn't alter compare semantics. |
| `UNICODE_CODEPOINT` | `StringComparer.Ordinal` (`BinaryCollation` body) | Semantically equivalent to BIN2 at the value level; appears in AdventureWorks2025. |

### CJK locales

| Name | Comparer | Notes |
|---|---|---|
| `Japanese_XJIS_140_CI_AS` | `ja-JP` `CompareInfo` + CI + KanaType-/Width-insensitive | Equality / kana-folding align; sort interleaves hiragana / full-width katakana / half-width katakana differently from SQL Server. See [Locale-comparer sort-parity gap](#locale-comparer-sort-parity-gap). |
| `Chinese_PRC_CI_AS` | `zh-CN` `CompareInfo` + CI | Pinyin ordering mostly aligns; Latin-vs-CJK block position is reversed (.NET puts CJK first, SQL Server puts Latin first). |
| `Korean_100_CI_AS` | `ko-KR` `CompareInfo` + CI | Hangul ordering routed through .NET culture; per-name sort-parity caveat applies. |
| `Korean_Wansung_CI_AS` | `ko-KR` `CompareInfo` + CI | Legacy Wansung code-page binding; at the simulator's UTF-16 value layer behaves identically to `Korean_100_CI_AS`. |

### European locales

| Name | Comparer | Notes |
|---|---|---|
| `Turkish_CI_AS` | `tr-TR` `CompareInfo` + CI | i / İ / ı / I folding correct end-to-end; tiebreaker within case-equivalence classes (`çay` vs `Çay`) differs. |
| `Greek_CI_AS` / `Greek_100_CI_AS` | `el-GR` `CompareInfo` + CI | Tonos / dialytika fold under accent-sensitive rules; final-sigma / medial-sigma case-insensitive peers. v100 and pre-v100 share the same body. |
| `Cyrillic_General_CI_AS` / `Cyrillic_General_100_CI_AS` | `ru-RU` `CompareInfo` + CI | Pan-Cyrillic (Russian / Ukrainian / Bulgarian / Serbian). v100 and pre-v100 share the same body. |
| `German_PhoneBook_CI_AS` / `German_PhoneBook_100_CI_AS` | `de-DE` `CompareInfo` + CI | Routed through .NET's default `de-DE` ordering (umlaut-as-letter), not phonebook (ä → ae, ß → ss). Sort divergence on umlauted letters; equality / case folding still align. |
| `French_CI_AS` / `French_100_CI_AS` | `fr-FR` `CompareInfo` + CI | Real SQL Server's French sorts accents from the END of the string; .NET `fr-FR` default doesn't, so accented-string adjacencies sort differently. |
| `Modern_Spanish_CI_AS` / `Modern_Spanish_100_CI_AS` | `es-ES` `CompareInfo` + CI | .NET's default Spanish sort already matches the modern convention (no `ch` / `ll` as separate letters), so alignment is closer here than for the other European locales. |

Generic culture-based collations use the `CultureCollation` class — name + culture + case-sensitive flag drive comparer construction.

## Locale-comparer sort-parity gap

Probed against SQL Server 2025 with a curated word set per locale (mixed-case ASCII, accented Latin, hiragana / katakana / half-width katakana, common CJK characters and 2-character compounds). For each `(collation, storage)` pair, ORDER BY result vs `CompareInfo.Compare` ordering compared position-by-position:

| Collation | nvarchar parity | varchar parity | Divergence shape |
|---|---|---|---|
| `Turkish_CI_AS` | 17 / 19 align | 12 / 19 align | nvarchar: only `çay` vs `Çay` case-tiebreaker order within the equality class. varchar: same, plus the `{İ, ı, I, i}` cluster interleaves with neighboring accented letters in a different order — CP1252 vs UTF-16 sort-key generation. |
| `Japanese_XJIS_140_CI_AS` | 11 / 21 align | 2 / 21 align | nvarchar: hiragana / full-width katakana / half-width katakana group correctly (kana-type folding works), but the secondary tiebreaker order inside each kana family flips for some characters. varchar: essentially unusable — CP1252 doesn't represent Japanese; real SQL Server would use a Japanese codepage (CP932). |
| `Chinese_PRC_CI_AS` | 0 / 17 align | 12 / 17 align | nvarchar: every position shifts by 2 because `.NET` puts CJK before Latin and SQL Server puts Latin before CJK; internal Chinese pinyin ordering is mostly aligned. varchar: small internal pinyin-order divergence on a few 2-char compounds (`上海` vs `韩国` swap). |

**Equality + CI/CS / KS / WS folding all align** for the inputs probed (Turkish-i, kana-type, width, accent). Pure sort-key parity within those equivalence classes doesn't — SQL Server's NLS sort tables aren't reproducible from .NET `CompareInfo` for these locales, and the simulator doesn't ship its own NLS data. Apps whose tests assert on exact byte-for-byte ORDER BY output of locale-collation columns will see divergence; apps using these collations for grouping / equality / LIKE / Turkish-i case folding match.

**`varchar(N)` on the Japanese / Chinese collations** is meaningfully wrong because the underlying codepage differs. Real SQL Server routes these through CP932 / CP936 respectively; the simulator routes through the invariant UTF-16 CompareInfo at the value layer. Use `nvarchar(N)` for any non-Latin column that needs even approximate sort parity.

## Known gaps

- **Set ops (UNION / UNION ALL / INTERSECT / EXCEPT) don't apply collation-conflict checks at the column-pair level yet.** Probe showed UNION raises Msg 468, UNION ALL raises Msg 457 across cross-collation columns; the simulator's set-op type-promotion path doesn't call `Collation.Resolve`. Cross-collation set-op columns currently fall through to the legacy type-precedence resolution.
- **`text` / `ntext` columns can't be declared with an explicit COLLATE in the simulator.** Real SQL Server allows it; the simulator's single-instance modeling collapses all text/ntext to the default. Low impact (text/ntext deprecated since SQL Server 2005).
- **Sysname's collation is always `Collation.Default`** at `Implicit` rank — real SQL Server's sysname inherits the server's catalog collation which can differ from the user database's collation; the simulator's single-instance modeling collapses them.

## Cross-references

- Database-level `ALTER DATABASE COLLATE` and the `Collation.Recognized` whitelist → [`database-options.md`](database-options.md).
- BACPAC import collation handling (loader warns on names outside `Recognized` and continues) → [`bacpac-loader.md`](bacpac-loader.md).
