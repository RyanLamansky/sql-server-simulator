# Collations — per-column declaration, coercibility, Msg 468 / 457

Every string-categorized `SqlType` instance carries a `(Collation, Coercibility)` pair. CREATE TABLE / ALTER COLUMN pin the declared collation at `Implicit` rank onto the column's `SqlType`; values decoded from the column inherit that type instance through row decode, so `SqlValue.CompareTo` / `Equals` / `GetHashCode` honor the declared rules. Cross-collation operand pairs that can't be resolved by coercibility raise Msg 468 (comparison / set ops / LIKE) or Msg 457 (concat / UNION ALL / DISTINCT over unresolved).

## Type-side wiring

`VarcharSqlType` / `NVarcharSqlType` / `CharSqlType` / `NCharSqlType` intern per `(length, Collation, Coercibility)` trio via a 3-tuple `ConcurrentDictionary`. The existing length-only `Get(N)` overloads return the `(N, Collation.Baseline, CoercibleDefault)` variant — matching the literal / parameter / CAST-of-literal contexts that historically didn't pin collation. New `Get(length, collation, coercibility)` overloads return the column-pinned variant.

`SystemNameSqlType` / `TextSqlType` / `NTextSqlType` are single shared instances reporting `Collation.Baseline` at `Implicit` rank — sysname/text/ntext don't accept per-column COLLATE in the simulator (sysname rejects per grammar; text/ntext deferred as deprecated).

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

`SqlValue.CompareTo` / `Equals` / `GetHashCode` route through `this.Type.Collation ?? Collation.Baseline`. Same-type pairs (which after the interning split are also same-collation pairs) take the fast path. Cross-type cases flow through `CompareValuesPromoted` (in `BooleanExpression.cs`), which:

1. Rejects LOB-typed operands (Msg 402 — unchanged).
2. Runs `Collation.Resolve` for string-string pairs with different types; raises **Msg 468 State 9** on conflict (probe-confirmed wording: `Cannot resolve the collation conflict between "X" and "Y" in the <op> operation.`). The check fires before NULL short-circuits, matching real SQL Server (`NULL = NULL` across cross-collation columns also raises).
3. Falls through to `SqlType.Promote` + per-side `CoerceTo` for the value coercion.

The fast path uses `SqlValue.WithType` to re-tag a value with a different `SqlType` instance — used by `CollateExpression.Run` to apply the explicit collation rewrap without re-allocating the underlying string reference.

## Decode preserves column type instance

`VarcharSqlType.Decode` and `NVarcharSqlType.Decode` thread `this` (the actual interned instance) into `SqlValue.FromVarchar(VarcharSqlType, string)` / `FromNVarchar(NVarcharSqlType, string)` rather than the singleton `FromVarchar(string)` overload, so the column's collation/coercibility survives the decode. The parallel `SqlValue.FromString(type, value)` similarly preserves the target type during cross-string coercion.

`CharSqlType.Decode` / `NCharSqlType.Decode` already passed `this` (via `FromChar` / `FromNChar`); collation came along free once the type-side wiring landed.

## Operator-site enforcement

- **Comparison (`=` / `&lt;&gt;` / `&lt;` / `&gt;` / `&lt;=` / `&gt;=` / `IN` / `BETWEEN` / `ALL` / `ANY`)** — all funnel through `CompareValuesPromoted`. Msg 468 with the per-op name (`"equal to"`, `"not equal to"`, `"less than"`, …).
- **`LIKE`** — `LikeExpression.Run` calls `Collation.Resolve(l.Type, r.Type)` on the runtime values' types. Replaces the old parse-time `PeelExplicitCollation` walk, which only caught explicit COLLATE postfixes. Conflict raises Msg 468 with operator name `"like"`. The resolved `Collation.CaseSensitive` flag flips `RegexOptions.IgnoreCase`.
- **String concat (`+`)** — `Add.StringConcatenation` calls `Collation.Resolve` on the operand pair. Conflict raises **Msg 457 State 1** (`Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict.`). `TwoSidedExpression.GetSqlType` mirrors the same resolution so the projection schema's result type matches the runtime value's type — RowEncoder rejects mismatched instances, so the GetSqlType / Run paths must stay aligned.

## `COLLATE` postfix

`CollateExpression.Run` rewraps the value's type via `WithCollation(this.ResolvedCollation, Coercibility.Explicit)`. `GetSqlType` propagates the same override through projection. Non-string inner raises Msg 447 at runtime (real SQL Server raises at bind time — same message, just earlier; lazy-plan parity).

Chained `expr COLLATE A COLLATE B` rejects with Msg 156 at parse time (probe-confirmed). Unknown collation name raises Msg 448 at parse time.

## Database default and `#temp` inheritance

`Simulation.Create.cs`'s column wiring (shared by CREATE TABLE / ALTER TABLE ADD / DECLARE @t / CREATE TYPE AS TABLE / temp-table paths) resolves the column's pinned collation as: explicit `COLLATE` clause first, else the active database's `Database.CollationName`, else `Collation.Baseline`. So `#temp` tables created while a BACPAC-loaded non-default-collation database is active inherit that database's collation — avoiding the EF temp-join footgun (real SQL Server's tempdb is independent, but the common shape — tempdb matches server default which matches user DB — collapses to the same behavior).

## Server-level seed: `Simulation.ServerCollationName`

`Simulation.ServerCollationName` (string-typed `init`-only property, defaults to `SQL_Latin1_General_CP1_CI_AS`) is the seed for every freshly-created `Database`: both the lazy `"simulated"` seed picked up on first `CreateDbConnection` and bacpac imports that don't carry their own collation declaration. Mirrors SQL Server's `model.collation` role; `init`-only reflects real SQL Server's install-time immutability (the only way to change it on a real instance is the `sqlservr -m -q` rebuild-master dance, blocked outright on Azure SQL). Setter validates against `Collation.TryGet` and raises `ArgumentException` on an unrecognized name.

Important fidelity edge — the seed knob closes a documented identifier-dict-comparer gap. Per-database dict comparers (`Database.Schemas`, `Schema.HeapTables`, etc.) are built at `Database` construction time from the seeded collation; `ALTER DATABASE COLLATE` updates `Database.Collation` for future identifier compares but doesn't rebuild the existing dict comparers. Setting `ServerCollationName` at construction means the dict comparer is right from the start: e.g., `CREATE SCHEMA DBO` on a CS-seeded DB succeeds (both `dbo` and `DBO` coexist as distinct schemas, probe-confirmed verbatim on real SQL Server 2025), whereas the post-hoc `ALTER DATABASE simulated COLLATE SQL_Latin1_General_CP1_CS_AS` path raises Msg 2714 because the stale CI dict comparer still treats `DBO == dbo`. Coverage: the `ServerCollationName_*` region in `NameComparisonRegimeTests.cs`.

## Result-type collation routing through the active database

`Expression.GetSqlType` takes a `BatchContext batch` parameter (paired with `Expression.Run`'s `RuntimeContext.Batch`), so result types that depend on the active database — notably string-typed scalar function returns and the MERGE `$action` pseudo-column — pin the per-DB collation at both parse-time schema computation and runtime value construction. The parity contract is preserved: both sides resolve from the same `batch.CurrentDatabase.Collation`.

Sites routed through the active DB collation:
- `CHAR(N)` / `NCHAR(N)` result type (`CharFromCode` / `NCharFromCode`).
- `hierarchyid.ToString()` and spatial / XML method calls that return string-typed values.
- `MERGE … OUTPUT $action` (`Simulation.Merge.MergeActionReference`).
- `sys.fn_listextendedproperty` `value` projection column (`Selection.ListExtendedProperty`).
- `SqlType.PromoteForArithmetic`'s string-concat path derives the result collation via `Collation.Resolve(a, b)` from the operands' coercibility ranks rather than defaulting to `Collation.Baseline`.

Probe-confirmed fidelity (real SQL Server CS database, 2026-05-22): `SELECT IIF(CHAR(65) = CHAR(97), 'eq', 'neq')` returns `'neq'` (literals don't case-fold under CS). The simulator now matches; the `CsDatabase_*CharFunctionResultUsesActiveCollation` tests in `NameComparisonRegimeTests.cs` lock the behavior in.

Sites that intentionally stay on `Collation.Baseline`:
- `SqlType.Varchar` / `NVarchar` pseudo-singletons and `SqlType.GetChar` / `GetNChar` static bridges — type-identity placeholders.
- `text` / `ntext` / `sysname` `Collation` overrides — server-default-only types. (Real SQL Server pins these per-database for text/ntext and per-server for sysname; a per-Simulation routing model is deferred.)
- Error-message type placeholders, dynamic-SQL string extraction, PRINT formatting — collation irrelevant to the surfaced value.
- User-supplied catalog content columns: `sys.extended_properties.value`, `sys.indexes.filter_definition` — real SQL Server tags these with the database collation; the process-wide catalog view declaration can't reach per-`Database` state, so these stay at Baseline until the per-Simulation catalog model lands.
- `Simulation.ServerCollation` initializer — the deliberate anchor for "what does the simulator's hardcoded baseline resolve to."

## Catalog views pin `_desc` / enum columns to `Collation.Catalog`

`Collation.Catalog` resolves to `Latin1_General_100_CI_AS_KS_WS_SC` — the contained-database catalog collation real SQL Server reports through `sys.fn_helpcollations()`. Microsoft's "Contained Database Collations" doc names it as `Latin1_General_100_CI_AS_WS_KS_SC` (WS before KS — documentation typo); the canonical name confirmed via the live `fn_helpcollations` catalog is `_CI_AS_KS_WS_SC` (KS before WS), and the simulator's parser only accepts the canonical form. The simulator picks this as the catalog anchor even though it doesn't model containment, because the documented value is more authoritative than empirical probes of non-contained instances. A non-contained SQL Server 2025 probe surfaced `Latin1_General_CI_AS_KS_WS` for catalog `_desc` columns — a pre-100, no-`_SC` legacy carry-over rather than a reference value; both names give identical equality results for the ASCII English identifiers that dominate real catalog-view queries, and the documented `_SC` flag adds correct supplementary-character handling if catalog content ever includes any.

`BuildCatalogViews` in `BuiltInResources.cs` defines two shared locals (`nvarchar60Catalog` / `nvarchar128Catalog`) plus per-view `charTwo` / `charOne` locals, all at `Collation.Catalog` + `Coercibility.Implicit`. Sites that pin to catalog:

- 25 `_desc` columns (`type_desc`, `class_desc`, `state_desc`, `temporal_type_desc`, `delete_referential_action_desc`, etc.).
- `sys.database_permissions.permission_name`.
- The `type` / `state` char(1)/char(2) enum-code columns and matching cell-value sites (`fkType` 'F ', `ckType` 'C ', `dfType` 'D ', `pkType` 'PK', `uqType` 'UQ', etc.).
- The null-`_desc` placeholder in `sys.spatial_indexes` (cell carries the catalog tag for visual consistency; row encode/decode routes through the column type anyway).

`Coercibility.Implicit` matches real SQL Server's behavior for explicit-`COLLATE`-pinned columns: catalog-column-vs-literal comparisons resolve under the catalog collation rather than rank-ambiguously. `RowEncoder.IsCompatibleColumnType` accepts `CharSqlType` / `NCharSqlType` pairs with matching length but differing collation/coercibility (mirroring the existing var-family rule), so cells from `SqlType.GetChar(N)` bridges still flow through the catalog-pinned column types without false rejections.

## String literals carry the active DB collation

`Tokenizer.NextToken` takes a `Collation activeCollation` parameter; `ParserContext.MoveNext` threads `context.CurrentDatabase.Collation` in. The two string-literal entry points (`ParseStringLiteral` for `'foo'`, `ParseNPrefixedStringLiteral` for `N'foo'`) construct `VarcharSqlType.Get(0, activeCollation, Coercibility.CoercibleDefault)` / `NVarcharSqlType` and tag the resulting `SqlValue` with it. Other literal kinds (varbinary `0xHEX`, currency `$1.23`) don't carry collation and ignore the parameter.

Effect: `SELECT IIF('A' = 'a', 'eq', 'neq')` on a CS database returns `'neq'` (case-sensitive), matching real SQL Server. The `CsDatabase_TwoVarcharLiteralsCompareCaseSensitively` / `CsDatabase_TwoNVarcharLiteralsCompareCaseSensitively` tests in `NameComparisonRegimeTests.cs` lock the behavior in. The earlier deferral framing (literal pairs falling through to the CI baseline because the tokenizer was stateless) is closed.

`ALTER COLUMN` without an explicit `COLLATE` clause preserves the existing column's collation (probe-aligned). With an explicit `COLLATE`, the new collation pins at `Implicit` rank.

## Parser-driven catalog

`Collation.TryGet(name)` decodes the grammatical shape of a name and constructs the matching instance on demand; results are interned so the same name always resolves to the same reference. The complete `sys.fn_helpcollations()` catalog ships — 5540 names total (77 SQL_* + 5463 non-SQL_*), probed against SQL Server 2025 on 2026-05-21 and validated against the per-prefix tail-set tables in `Collation.Catalog.cs`. Names outside the catalog (whether outright misspellings or grammar-valid but never-shipped combinations like `Pashto_CI_AS` or `Latin1_General_140_BIN`) raise `NotSupportedException` in direct SQL and surface on `BacpacImportResult.Warnings` for BACPAC loads.

### Architecture

Three files carry the work:

- **`Collation.Catalog.cs`** — data. 124 prefix entries (`KnownPrefixes`: prefix → BCP-47 culture + human-readable description prefix); 77 SQL_* per-name entries (`SqlServerSortOrders`: full name → sort order number + human prefix); 9 distinct tail-set patterns (`Pattern0Tails`..`Pattern8Tails`) covering every (prefix, version, flag) combination real SQL Server ships across the non-SQL_* family; the 89 non-SQL_* prefixes share these 9 patterns via `PrefixToPattern`.
- **`Collation.Parser.cs`** — `TryParse(name)` tokenizes the suffix from the right, extracts version / code-page / flag bitmask, then validates: SQL_* names against `SqlServerSortOrders`, non-SQL_* names against the prefix's tail-set pattern. Description column is generated from the flag bitmask + prefix metadata + (for SQL_*) the baked sort order.
- **`Collation.cs`** — abstract base + four concrete bodies (`CultureCollation` for the generic comparer, `BinaryCollationBody` for `_BIN`/`_BIN2` with the pre-Bin2 position-0-quirk dispatch, `Cp1252BinaryCollation` and `Utf8CodepointBinaryCollation` for the varchar-storage substitutes).

### Override slot

The override registry (`overrides` ConcurrentDictionary in `Collation.Parser.cs`) takes precedence over the parser. Adding a bespoke implementation for a specific name is a 3-step source change: subclass one of the concrete bodies (or `Collation` directly) with a hand-tuned `Compare` / `Equals` / `GetHashCode`, instantiate it in `Collation`'s static initializer, and call `RegisterOverride`. The override fires before the parser; everything else continues to fall through to the grammatical path. No overrides ship today — every modeled name routes through `CultureCollation` or `BinaryCollationBody` with the appropriate flag-driven options.

### Behavioral notes by family

- **SQL_\* family**: routed through invariant `CompareInfo` (unless the human-prefix description maps to a locale-specific culture, e.g., `SQL_Croatian_CP1250_CI_AS` → `hr-HR`); same two-pass minimal-punctuation sort treatment as the Windows family (see [Symbol sort weighting](#symbol-sort-weighting)). Description carries the per-name SQL Server Sort Order number + Code Page (extracted from the `CP*` token).
- **Windows-style Latin1_General**: invariant `CompareInfo`; two-pass minimal-punctuation sort. `_BIN` engages the pre-2005 position-0-codeunit / position-1+-codepoint quirk; `_BIN2` is pure UTF-16 code-unit ordinal.
- **`_UTF8` collations**: storage encoding flips from CP1252 to UTF-8 for varchar/char columns. `_BIN2_UTF8` substitutes `Utf8CodepointBinaryCollation` (codepoint-order = UTF-8 byte order) on varchar storage.
- **`_SC_` collations** (and v140+ implicitly): set `IsSupplementaryCharacterAware` on the constructed instance, driving codepoint-aware LEN/SUBSTRING/etc. dispatch (see [`_SC_` function-semantics dispatch](#_sc_-function-semantics-dispatch)).
- **`_KS_` / `_WS_` flags**: flip `CompareOptions.IgnoreKanaType` / `IgnoreWidth` off (default = both on).
- **Locale prefixes** (Japanese, Chinese, Turkish, Korean, etc.): map to the closest .NET culture via `KnownPrefixes`; fall back to invariant when no clean .NET equivalent exists (Tamazight, Traditional_Spanish, Indic_General). Sort-parity caveat in [Locale-comparer sort-parity gap](#locale-comparer-sort-parity-gap) applies — equality / CI/CS / KS / WS folding align, secondary sort tiebreakers within equivalence classes may diverge.

## Symbol sort weighting

`CultureCollation.Compare` (the `CompareInfo`-routed comparer behind every SQL_\*, Windows, and locale family) gives hyphen (`-`) and apostrophe (`'`) the **minimal-weight** treatment SQL Server applies, while every other symbol keeps a real primary weight. Probe-confirmed identical across `SQL_Latin1_General_CP1_CI_AS`, `Latin1_General_100_CI_AS`, and `Latin1_General_CI_AS` on SQL Server 2025:

- **Non-minimal symbols (`#`, `+`, `,`, `!`, `~`, `_`, …) sort first** — ahead of digits and letters. So `MIN('#500-75', '00,', 'abc')` is `'#500-75'`. .NET's `CompareOptions.IgnoreSymbols` would *strip* these (mis-ranking `'#500-75'` among the digits as `50075`); plain `CompareInfo` without it keeps them, which is what the comparer uses.
- **Hyphen and apostrophe drop out of the primary key** — `'co-op'` ranks beside `'coop'`, `"'Aiea"` beside `'Aiea'` — but carry a secondary weight, so between two strings sharing a primary key the copy bearing the mark sorts *after*: `'coop' < 'co-op'`, `'cant' < "can't"`, `'A' < "'A"`.

Implementation: a fast path (`compareInfo.Compare(x, y, equalityOptions)`) when neither operand contains a minimal mark; otherwise a primary pass over hyphen/apostrophe-stripped copies, then `MinimalPunctuationTiebreak` (a two-pointer scan where a minimal mark sorts after a real character). Equality and `GetHashCode` keep *every* symbol significant (only trailing spaces fold, handled at the `SqlValue` layer) — so `'co-op' = 'coop'` is false, matching real SQL Server.

### Known gap: varchar symbol order under SQL_\* collations

`SQL_*` collations sort **non-Unicode (`varchar`/`char`) data through a CP1252 code-page sort table** that differs from their Unicode (`nvarchar`/`nchar`) rules — the same collation name orders the same characters differently by storage type. Probe-confirmed: under `SQL_Latin1_General_CP1_CI_AS`, `varchar` sorts `'+' < '/'` but `nvarchar` sorts `'/' < '+'`. The simulator routes both through one Unicode `CompareInfo`, so it matches the nvarchar order for both. Bites base64-bearing `varchar` columns (`+`/`/` are base64's two special chars) — e.g. AdventureWorks `Person.Password.PasswordHash` (`varchar`), where live `MIN(PasswordHash)` starts `++…` (code-page order) but the simulator picks `//…` (Unicode order). Closing it requires the CP1252 SQL sort-order weight table for varchar storage, which the simulator doesn't ship.

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

`Latin1_General_BIN`, `Latin1_General_BIN2`, and `Latin1_General_100_BIN2_UTF8` each carry two comparer bodies and dispatch on the column's storage type via `Collation.ForVarcharStorage()`. The virtual returns `this` by default; binary collations override to point at a storage-flavored sibling. `VarcharSqlType.WithCollation` and `CharSqlType.WithCollation` call it at column-pin time; `NVarcharSqlType` / `NCharSqlType` don't substitute (UTF-16 storage matches the UTF-16 code-unit-order body). Substituted siblings share the same `Name` so catalog views report one collation name and `Collation.Resolve` treats them as the same collation for cross-operand coercibility.

| Outer collation | nvarchar / nchar body | varchar / char body |
|---|---|---|
| `Latin1_General_BIN` | `BinaryCollation` via `StringComparer.Ordinal` at position 0; codepoint-combining at position 1+ (see "pre-2005 _BIN" note below) | `Cp1252BinaryCollation` — CP1252 byte sequence compare |
| `Latin1_General_BIN2` | `BinaryCollation` via `StringComparer.Ordinal` (UTF-16 code-unit ordinal throughout) | `Cp1252BinaryCollation` |
| `Latin1_General_100_BIN2_UTF8` | `BinaryCollation` (UTF-16 code-unit ordinal — `_UTF8` is a no-op on nvarchar storage) | `Utf8CodepointBinaryCollation` — codepoint-order compare (≡ UTF-8 byte order, ≡ surrogate-pair-combined scalar order) |

The three varchar bodies pairwise diverge on the same 0x80-0x9F window: codepoints whose CP1252 representation lands in that range scatter across the BMP — `€` U+20AC → CP1252 0x80, `ƒ` U+0192 → 0x83, `Ÿ` U+0178 → 0x9F, `‚` U+201A → 0x82. CP1252 byte order, UTF-8 byte order, and UTF-16 code-unit order give three different rankings for any set spanning that window. Probe-confirmed against SQL Server 2025: `varchar BIN2` of `{Z, €, ƒ, NBSP}` sorts Z, €, ƒ, NBSP (CP1252 byte order); same data on `varchar BIN2_UTF8` sorts Z, NBSP, ƒ, € (codepoint = UTF-8 byte order).

## UTF-8 storage encoding

Three modeled collations carry UTF-8 as their `StorageEncoding`: `Latin1_General_100_CI_AS_SC_UTF8`, `_CS_AS_SC_UTF8`, `_BIN2_UTF8`. The encoding is read by `VarcharSqlType.Encode` / `Decode` / `GetVariableByteCount` (and the same trio on `CharSqlType`) at row-encode time, and by `Simulation.Coerce.EnforceMaxLength` for the per-row byte-budget check. Net effects:

- **`DATALENGTH`** returns UTF-8 byte counts (`café` → 5, NBSP → 2, 😀 → 4) on varchar / char columns.
- **`varchar(N)`** budgets N **bytes**, not N characters: `é` (2 UTF-8 bytes) fits in `varchar(2)` exactly; appending one ASCII byte overflows to Msg 2628 / 8152.
- **`char(N)`** pads to N **bytes**: `é` in `char(5)` stores as the 2 UTF-8 bytes + 3 ASCII space bytes (= 5 bytes). The padding count is computed against the column's storage encoding via `NormalizeFixedLengthStringToByteCount` in `SqlValue`. Truncation walks runes to avoid splitting a UTF-8 sequence mid-codepoint.
- **Sort** behavior is varchar-storage-specific: `BIN2_UTF8` substitutes the `Utf8CodepointBinaryCollation` body (codepoint order); the two `*_SC_UTF8` siblings keep their `CompareInfo`-routed bodies (operate on UTF-16 strings; storage encoding doesn't affect them).
- **`nvarchar` / `nchar` with `*_UTF8` collation** is a partial no-op: storage stays UTF-16 (UTF-8 byte width never materializes), sort body stays the UTF-16-friendly one. The `_SC_` flag (on `_CI_AS_SC_UTF8` / `_CS_AS_SC_UTF8`) still affects LEN / SUBSTRING semantics on nvarchar — modeled separately under the `_SC_` gap.

### Microsoft-docs-vs-real-behavior gap: BIN2 is code *unit*, not code point

Microsoft's [Collation and Unicode Support](https://learn.microsoft.com/en-us/sql/relational-databases/collations/collation-and-unicode-support) page states "In a `BIN2` collation all characters are sorted according to their code points." This is **inaccurate for supplementary characters on nvarchar**. Empirical behavior on SQL Server 2025 (probed 2026-05-21, three routes — `NCHAR`-synthesized, parameter-passed .NET string, raw SQL literal — all agree): BIN2 nvarchar compares UTF-16 16-bit code units, which differs from code-point order when surrogate pairs are involved.

Demo: `(NCHAR(0xD83D) + NCHAR(0xDE00))` (the surrogate pair for 😀 U+1F600) sorts BEFORE `NCHAR(0xE000)` under BIN2, because the high surrogate D83D (0xD83D) < E000 (0xE000) as 16-bit values. Under code-point order, U+1F600 (0x1F600) > U+E000 would put the emoji last. Real SQL Server returns the code-unit answer; the simulator's `StringComparer.Ordinal` (which is also code-unit) matches.

Community sources documenting the same gap:
- [Solomon Rutzky — Differences Between the Various Binary Collations (Sql Quantum Leap, 2019)](https://sqlquantumleap.com/2019/03/13/differences-between-the-various-binary-collations-cultures-versions-and-bin-vs-bin2/): "the BIN2 collations, when dealing with NVARCHAR data, sort by code *unit*, not by code *point*."
- [SQLServerCentral mirror of the same analysis](https://www.sqlservercentral.com/blogs/differences-between-the-various-binary-collations-cultures-versions-and-bin-vs-bin2).

This aligns with the [Unicode specification](https://www.unicode.org/versions/latest/) — UTF-16 binary order is not codepoint order when supplementary characters are present. SQL Server matches the Unicode spec; only its own product docs are out of step. Don't "fix" the simulator by adding code-point logic — that would introduce a divergence where none exists.

The pre-2005 `_BIN` (not `_BIN2`) variant has a different, real quirk: at position 0 it's code-unit (same as BIN2), but at position 1+ it switches to code-point. Probe-confirmed via `'Z'+emoji > 'Z'+nchar(0xE000)` returning TRUE under BIN and FALSE under BIN2. **The simulator models this**: `Latin1_General_BIN.Compare` (the nvarchar body) overrides `BinaryCollation.Compare` to walk the strings with the asymmetric rule — first 16-bit unit raw, then surrogate-pair-combining scalar compare. `Equals` / `GetHashCode` stay on `Ordinal` because equality of code-unit sequences implies equality of scalar sequences regardless of which rule walked them.

## `_SC_` function-semantics dispatch

`Collation.IsSupplementaryCharacterAware` (virtual, default `false`; overridden `true` on `Latin1_General_100_CI_AS_SC_UTF8` and `Latin1_General_100_CS_AS_SC_UTF8`) drives eight scalar functions to switch between UTF-16 code-unit semantics (non-`_SC_`) and Unicode-codepoint semantics (`_SC_`). Each function reads the dispatch flag off its input value's `SqlType.Collation`, so a postfix `COLLATE …_SC_UTF8` flips the semantic per-call. Probe-confirmed against SQL Server 2025 (2026-05-21).

| Function | Non-`_SC_` (code units) | `_SC_` (codepoints) |
|---|---|---|
| `LEN(N'😀')` | 2 | 1 |
| `SUBSTRING(N'😀X', 1, 1)` | lone high surrogate (`0xD83D`) | full emoji (`0xD83D 0xDE00`) |
| `LEFT(N'😀X', 1)` | lone high surrogate | full emoji |
| `RIGHT(N'X😀', 1)` | lone low surrogate (`0xDE00`) | full emoji |
| `CHARINDEX(N'X', N'😀X')` | 3 | 2 |
| `PATINDEX(N'%X%', N'😀X')` | 3 | 2 |
| `REVERSE(N'😀X')` | `X` + low surrogate + high surrogate (split pair) | `X` + full emoji (intact pair) |
| `UNICODE(N'😀')` | 55357 (high surrogate value) | 128512 (U+1F600 codepoint) |
| `STUFF(N'😀X', 1, 1, N'Y')` | `Y` + lone low surrogate + `X` (replaces 1 code unit) | `Y` + `X` (replaces full codepoint) |

`SupplementaryCharacters` (in `Parser/Expressions/`) holds the rune-walking helpers (`CodepointCount`, `CodepointToCodeUnit`, `CodeUnitToCodepoint`, `LeftByCodepoints`, `RightByCodepoints`, `ReverseByCodepoints`, `ReverseByCodeUnits`, `LeadingCodepoint`). The non-`_SC_` path stays on .NET's native code-unit operations (`string.Length`, `Substring`, `IndexOf`, etc.), which already match real SQL Server's non-`_SC_` semantics.

**Lone-surrogate preservation:** the nvarchar / nchar / sysname / ntext row encoders now byte-copy UTF-16 LE directly (`SystemNameSqlType.Utf16LeEncode` / `Utf16LeDecode` via `MemoryMarshal.AsBytes`) instead of routing through `Encoding.Unicode.GetBytes`, which silently rewrites lone surrogates to `U+FFFD` via its `EncoderReplacementFallback`. Real SQL Server preserves lone surrogates end-to-end (probe-confirmed: `SUBSTRING(N'😀X', 1, 1)` on a non-`_SC_` column round-trips through `sys.columns` storage with the lone high surrogate intact); the byte-copy path keeps the simulator's fidelity bar.

## KS / WS suffix dispatch

`Latin1_General_CI_AS_KS_WS` is currently the only `_KS_WS`-marked collation in the recognized catalog. Real SQL Server's `_KS_` (kanatype-sensitive) and `_WS_` (width-sensitive) suffixes flip the corresponding `IgnoreKanaType` / `IgnoreWidth` flags OFF. Without them (e.g. plain `_CI_AS`), the trio { full-width katakana ア U+30A2, hiragana あ U+3042, half-width katakana ｱ U+FF71 } folds together under equality and DISTINCT. With `_KS_WS` they distinguish.

`CultureCollation` takes optional `kanaTypeSensitive` / `widthSensitive` parameters (default `false`); the `Latin1_General_CI_AS_KS_WS` instance passes `true` for both. Probe-confirmed against SQL Server 2025: `nchar(0x30A2) = nchar(0x3042)` is FALSE under `_KS_WS` and TRUE under plain `_CI_AS`.

## Known gaps

- **Set ops (UNION / UNION ALL / INTERSECT / EXCEPT) don't apply collation-conflict checks at the column-pair level yet.** Probe showed UNION raises Msg 468, UNION ALL raises Msg 457 across cross-collation columns; the simulator's set-op type-promotion path doesn't call `Collation.Resolve`. Cross-collation set-op columns currently fall through to the legacy type-precedence resolution.
- **`text` / `ntext` columns can't be declared with an explicit COLLATE in the simulator.** Real SQL Server allows it; the simulator's single-instance modeling collapses all text/ntext to the default. Low impact (text/ntext deprecated since SQL Server 2005).
- **Sysname's collation is always `Collation.Baseline`** at `Implicit` rank — real SQL Server's sysname inherits the server's catalog collation which can differ from the user database's collation; the simulator's single-instance modeling collapses them.
- **`CAST(expr AS varchar(N)) COLLATE …UTF8` doesn't re-truncate under the postfix collation.** The CAST runs against the local default (CP1252, single-byte), so a 3-char input into `varchar(2)` truncates to 2 chars; the postfix COLLATE then rewraps as `varchar(2)` UTF-8 with that 2-char .NET string, which under UTF-8 may be more than 2 bytes. Probe-confirmed against SQL Server 2025: real SQL Server effectively applies the postfix collation's byte budget at CAST time — `CAST(N'AéB' AS varchar(2)) COLLATE Latin1_General_100_CI_AS_SC_UTF8` returns `'A'` (1 byte), the simulator returns `'Aé'` (3 bytes). The fixed-length sibling `CAST(... AS char(N))` doesn't have this gap because `CollateExpression.Run` re-normalizes char(N) values through `FromString` when the storage encoding changes (the char(N) destination buffer is fixed at N bytes, so the regression would manifest as an encoder overflow; varchar sizes dynamically and only the truncation cutoff disagrees). Workaround: pin the UTF-8 collation directly on the CAST target via the column's declared collation, rather than as a postfix on a CAST output.
- **Pre-v100 collation sort divergence on supplementary chars at position 1+.** Probe-confirmed against SQL Server 2025: `SQL_Latin1_General_CP1_CI_AS` (the default) and `Latin1_General_CI_AS` (pre-v100) sort `Z+emoji` BEFORE `Z+U+E000` — code-unit order (high surrogate D83D < E000). The v100 family (`Latin1_General_100_CI_AS` and its SC sibling) sort the other way (codepoint U+1F600 > U+E000 → `Z+E000` first). The simulator routes both pre-v100 and v100 through `CompareInfo`, which always does codepoint compare — so both ranges of collations behave like v100 in the simulator. Narrow gap (only supplementary chars at non-position-0); fixing requires per-collation Compare bodies that drop to code-unit ordinal at supplementary positions.

## Cross-references

- Database-level `ALTER DATABASE COLLATE` and the parser-driven recognition gate → [`database-options.md`](database-options.md).
- BACPAC import collation handling (loader warns on names the parser rejects and continues) → [`bacpac-loader.md`](bacpac-loader.md).
