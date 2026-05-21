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
| `Latin1_General_BIN` | nvarchar / nchar: `StringComparer.Ordinal`. varchar / char: CP1252 byte compare (`Cp1252BinaryCollation`) substituted in via `Collation.ForVarcharStorage()`. | Pure codepoint on Unicode types; pure CP1252 byte on non-Unicode types (matches real SQL Server's CP1252 byte sort). |
| `Latin1_General_BIN2` | Same per-storage dispatch as BIN above. | Identical body to BIN at the simulator's value layer — the BIN-vs-BIN2 code-page-prefix asymmetry only matters when the underlying codepage isn't CP1252, which the simulator collapses. |
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

## Binary collation storage-aware dispatch

`Latin1_General_BIN` and `Latin1_General_BIN2` carry two comparer bodies and dispatch on the column's storage type:

- **nvarchar / nchar**: `BinaryCollation` body via `StringComparer.Ordinal`. UTF-16 code-unit ordinal compare equals real SQL Server's nvarchar BIN/BIN2 storage byte compare for any BMP input, and matches code-unit-by-code-unit for supplementary characters too (the surrogate pair is compared as two separate 16-bit code units, not as a unified 32-bit scalar — see the "code unit, not code point" note below).
- **varchar / char**: `Cp1252BinaryCollation` body. Encodes each operand to CP1252 via `CharSqlType.Cp1252Encoder`, then `SequenceCompareTo` on the bytes. Matches real SQL Server's CP1252 byte sort byte-for-byte.

The two bodies diverge for any string containing characters whose CP1252 byte lies in the 0x80-0x9F window: those bytes map to Unicode codepoints scattered across the BMP (`€` U+20AC → 0x80, `ƒ` U+0192 → 0x83, `Ÿ` U+0178 → 0x9F, `‚` U+201A → 0x82, …), so codepoint order ≠ byte order. Probe-confirmed against SQL Server 2025 — the simulator now matches.

The dispatch hangs off `Collation.ForVarcharStorage()` (a virtual returning `this` by default; the BIN / BIN2 singletons override to point at the `Cp1252BinaryCollation` sibling). `VarcharSqlType.WithCollation` and `CharSqlType.WithCollation` call it at column-pin time; `NVarcharSqlType` / `NCharSqlType` don't substitute. Both bodies share the same `Name` so catalog views report one collation name and `Collation.Resolve` treats them as the same collation for cross-operand coercibility.

`Latin1_General_100_BIN2_UTF8` keeps the codepoint-order body — UTF-8 byte order equals codepoint order (UTF-8 invariant), so the substitution isn't needed even though it's a varchar collation. `UNICODE_CODEPOINT` is Unicode-only (the simulator doesn't reject it on varchar at parse time — a low-priority gap).

### Microsoft-docs-vs-real-behavior gap: BIN2 is code *unit*, not code point

Microsoft's [Collation and Unicode Support](https://learn.microsoft.com/en-us/sql/relational-databases/collations/collation-and-unicode-support) page states "In a `BIN2` collation all characters are sorted according to their code points." This is **inaccurate for supplementary characters on nvarchar**. Empirical behavior on SQL Server 2025 (probed 2026-05-21, three routes — `NCHAR`-synthesized, parameter-passed .NET string, raw SQL literal — all agree): BIN2 nvarchar compares UTF-16 16-bit code units, which differs from code-point order when surrogate pairs are involved.

Demo: `(NCHAR(0xD83D) + NCHAR(0xDE00))` (the surrogate pair for 😀 U+1F600) sorts BEFORE `NCHAR(0xE000)` under BIN2, because the high surrogate D83D (0xD83D) < E000 (0xE000) as 16-bit values. Under code-point order, U+1F600 (0x1F600) > U+E000 would put the emoji last. Real SQL Server returns the code-unit answer; the simulator's `StringComparer.Ordinal` (which is also code-unit) matches.

Community sources documenting the same gap:
- [Solomon Rutzky — Differences Between the Various Binary Collations (Sql Quantum Leap, 2019)](https://sqlquantumleap.com/2019/03/13/differences-between-the-various-binary-collations-cultures-versions-and-bin-vs-bin2/): "the BIN2 collations, when dealing with NVARCHAR data, sort by code *unit*, not by code *point*."
- [SQLServerCentral mirror of the same analysis](https://www.sqlservercentral.com/blogs/differences-between-the-various-binary-collations-cultures-versions-and-bin-vs-bin2).

This aligns with the [Unicode specification](https://www.unicode.org/versions/latest/) — UTF-16 binary order is not codepoint order when supplementary characters are present. SQL Server matches the Unicode spec; only its own product docs are out of step. Don't "fix" the simulator by adding code-point logic — that would introduce a divergence where none exists.

The pre-2005 `_BIN` (not `_BIN2`) variant has a different, real quirk: at position 0 it's code-unit (same as BIN2), but at position 1+ it switches to code-point. Probe-confirmed via `'Z'+emoji > 'Z'+nchar(0xE000)` returning TRUE under BIN and FALSE under BIN2. **The simulator models this**: `Latin1_General_BIN.Compare` (the nvarchar body) overrides `BinaryCollation.Compare` to walk the strings with the asymmetric rule — first 16-bit unit raw, then surrogate-pair-combining scalar compare. `Equals` / `GetHashCode` stay on `Ordinal` because equality of code-unit sequences implies equality of scalar sequences regardless of which rule walked them.

## KS / WS suffix dispatch

`Latin1_General_CI_AS_KS_WS` is currently the only `_KS_WS`-marked collation in the recognized catalog. Real SQL Server's `_KS_` (kanatype-sensitive) and `_WS_` (width-sensitive) suffixes flip the corresponding `IgnoreKanaType` / `IgnoreWidth` flags OFF. Without them (e.g. plain `_CI_AS`), the trio { full-width katakana ア U+30A2, hiragana あ U+3042, half-width katakana ｱ U+FF71 } folds together under equality and DISTINCT. With `_KS_WS` they distinguish.

`CultureCollation` takes optional `kanaTypeSensitive` / `widthSensitive` parameters (default `false`); the `Latin1_General_CI_AS_KS_WS` instance passes `true` for both. Probe-confirmed against SQL Server 2025: `nchar(0x30A2) = nchar(0x3042)` is FALSE under `_KS_WS` and TRUE under plain `_CI_AS`.

## Known gaps

- **Set ops (UNION / UNION ALL / INTERSECT / EXCEPT) don't apply collation-conflict checks at the column-pair level yet.** Probe showed UNION raises Msg 468, UNION ALL raises Msg 457 across cross-collation columns; the simulator's set-op type-promotion path doesn't call `Collation.Resolve`. Cross-collation set-op columns currently fall through to the legacy type-precedence resolution.
- **`text` / `ntext` columns can't be declared with an explicit COLLATE in the simulator.** Real SQL Server allows it; the simulator's single-instance modeling collapses all text/ntext to the default. Low impact (text/ntext deprecated since SQL Server 2005).
- **Sysname's collation is always `Collation.Default`** at `Implicit` rank — real SQL Server's sysname inherits the server's catalog collation which can differ from the user database's collation; the simulator's single-instance modeling collapses them.
- **`UNICODE_CODEPOINT` is over-permitted.** Real SQL Server 2025 rejects `COLLATE UNICODE_CODEPOINT` with `Invalid collation 'UNICODE_CODEPOINT'.` — it's an internal-only collation that surfaces on XML-index storage columns in `sys.columns.collation_name` (probed against `AdventureWorks2025.sys.xml_index_nodes_*`) but isn't on `sys.fn_helpcollations()` and can't be applied in a `COLLATE` clause. The simulator accepts it everywhere via the same `Recognized` whitelist that drives BACPAC catalog round-trip. Splitting "loader-recognized" from "COLLATE-acceptable" requires a separate dispatch flag; deferred because real exposure is narrow (someone manually typing `COLLATE UNICODE_CODEPOINT` in user SQL).
- **`Latin1_General_100_BIN2_UTF8` and the other `*_UTF8` collations are storage-misencoded.** Real SQL Server stores values as UTF-8 bytes (`€` U+20AC → 3 bytes `E2 82 AC`, `NBSP` U+00A0 → 2 bytes `C2 A0`, 😀 U+1F600 → 4 bytes `F0 9F 98 80`). The simulator collapses all varchar storage to CP1252 regardless of collation, so `DATALENGTH`, `LEN` (under `_SC_UTF8`), and storage-size budgeting diverge for non-ASCII inputs. Sort happens to match for most cases because UTF-8 byte order == UTF-16 codepoint order == `StringComparer.Ordinal` (the simulator's value layer). Fixing this requires per-collation storage encoder dispatch in `VarcharSqlType.Encode/Decode`; deferred as a significant refactor.
- **`_SC_` (supplementary-character-aware) semantics aren't modeled.** Probe showed three observable effects on SQL Server 2025 the simulator doesn't replicate:
  - `LEN(N'😀')` returns 2 under non-`_SC_` collations, 1 under `_SC_`. The simulator always returns code-unit count (2 in this case).
  - `SUBSTRING(N'😀X', 1, 1)` returns a lone high surrogate under non-`_SC_`, the full emoji under `_SC_`. The simulator's SUBSTRING is code-unit-based — non-`_SC_` semantics, no `_SC_` path.
  - ORDER BY on supplementary chars: non-`_SC_` `SQL_Latin1_General_CP1_CI_AS` sorts `Z+emoji` BEFORE `Z+U+E000` (code-unit at position 1+); `_SC_` flips this to `Z+U+E000` BEFORE `Z+emoji` (codepoint at supplementary positions). The simulator's CompareInfo-routed body doesn't model the `_SC_` codepoint-aware path. Fixing all three requires collation-aware LEN / SUBSTRING / Compare; deferred.
- **Most `_SC_` and locale variants aren't in the recognized catalog.** `sys.fn_helpcollations()` lists 3008 `_SC_` collations on SQL Server 2025; the simulator recognizes 3 of them (`Latin1_General_100_CI_AS_SC_UTF8`, `_CS_AS_SC_UTF8`, `_BIN2_UTF8`). The non-`_UTF8` `_SC_` variants (e.g. plain `Latin1_General_100_CI_AS_SC`) and the locale `_SC_` variants (e.g. `Japanese_XJIS_140_CI_AS_SC`) aren't recognized. Adding entries is mechanical (one line per name in `Recognized` + `ByName`), and behavior body falls back to the closest non-`_SC_` sibling. Currently surfaces as `NotSupportedException` in direct SQL or a `BacpacImportResult.Warnings` entry on import — graceful degradation per the existing pattern.

## Cross-references

- Database-level `ALTER DATABASE COLLATE` and the `Collation.Recognized` whitelist → [`database-options.md`](database-options.md).
- BACPAC import collation handling (loader warns on names outside `Recognized` and continues) → [`bacpac-loader.md`](bacpac-loader.md).
