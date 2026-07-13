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

### Bespoke per-name body

`CreateInstance` (in `Collation.Parser.cs`) special-cases a name when the generic `CultureCollation` / `BinaryCollationBody` construction doesn't capture its real behavior: it builds the generic body, then wraps or replaces it. One name is special-cased today — the default `SQL_Latin1_General_CP1_CI_AS`, where the freshly-built `CultureCollation` is handed to `new SqlLatin1Cp1CiAsCollation(cultureBody)` (see [byte-exact sort](#sql_latin1_general_cp1_ci_as--byte-exact-sort)); that wrapper keeps the culture body for metadata + the non-CP1252 fallback and overrides `Compare` / `Equals` / `GetHashCode`. The wrapped instance is interned like any other, so `TryGet` and `Baseline` return it. Every other modeled name routes through `CultureCollation` or `BinaryCollationBody` with the appropriate flag-driven options.

### Behavioral notes by family

- **SQL_\* family**: the default `SQL_Latin1_General_CP1_CI_AS` is the bespoke byte-exact override (see [byte-exact sort](#sql_latin1_general_cp1_ci_as--byte-exact-sort)); the rest route through invariant `CompareInfo` (unless the human-prefix description maps to a locale-specific culture, e.g., `SQL_Croatian_CP1250_CI_AS` → `hr-HR`) with the [two-pass minimal-punctuation treatment](#symbol-sort-weighting-other-sql_--windows--locale-families). Description carries the per-name SQL Server Sort Order number + Code Page (extracted from the `CP*` token).
- **Windows-style Latin1_General**: invariant `CompareInfo`; two-pass minimal-punctuation sort. `_BIN` engages the pre-2005 position-0-codeunit / position-1+-codepoint quirk; `_BIN2` is pure UTF-16 code-unit ordinal.
- **`_UTF8` collations**: storage encoding flips from CP1252 to UTF-8 for varchar/char columns. `_BIN2_UTF8` substitutes `Utf8CodepointBinaryCollation` (codepoint-order = UTF-8 byte order) on varchar storage.
- **`_SC_` collations** (and v140+ implicitly): set `IsSupplementaryCharacterAware` on the constructed instance, driving codepoint-aware LEN/SUBSTRING/etc. dispatch (see [`_SC_` function-semantics dispatch](#_sc_-function-semantics-dispatch)).
- **`_KS_` / `_WS_` flags**: flip `CompareOptions.IgnoreKanaType` / `IgnoreWidth` off (default = both on).
- **Locale prefixes** (Japanese, Chinese, Turkish, Korean, etc.): map to the closest .NET culture via `KnownPrefixes`; fall back to invariant when no clean .NET equivalent exists (Tamazight, Traditional_Spanish, Indic_General). Sort-parity caveat in [Locale-comparer sort-parity gap](#locale-comparer-sort-parity-gap) applies — equality / CI/CS / KS / WS folding align, secondary sort tiebreakers within equivalence classes may diverge.

## `SQL_Latin1_General_CP1_CI_AS` — byte-exact sort

The default collation routes through a dedicated body (`SqlLatin1Cp1CiAsCollation` in `Collation.SqlLatin1Sort.cs`, [special-cased in the parser](#bespoke-per-name-body)) that reproduces SQL Server's ordering **byte-for-byte over the entire CP1252 repertoire**, for both `varchar`/`char` and `nvarchar`/`nchar`. Validated by a fuzz harness diffing 138k+ random CP1252 string-pair comparisons against the live server (both storage types, zero divergence); the lone real-world divergence that motivated it — base64 `MIN(PasswordHash)` on AdventureWorks `Person.Password` (`varchar`, `+`/`/` order) — is closed.

Why a bespoke body instead of `CompareInfo`: real SQL Server sorts this collation's non-Unicode and Unicode data through **two different multi-level weight tables**, and neither matches .NET's `CompareInfo`. The override bakes four probe-extracted rank tables (DENSE_RANK over `CHAR(n)` / the decoded char, under both the CI_AS and accent-insensitive CI_AI forms) and runs a multi-level comparison:

- **Primary** = the accent-folded (CI_AI) rank, so `'à' < 'Ao'` (base letter `a` before `Ao`). **Secondary** = the accent-sensitive (CI_AS) rank, breaking primary ties so `'cafe' < 'café'`, `'az' < 'àz'`. Case folds at both levels.
- **varchar** (SQL sort order 52, CP1252): pure per-character; **no** ignorable characters. Expands `æ Æ ß` to their base letters at the primary level, with a **tertiary** so the ligature sorts just after its expansion (`'ae' < 'æ'`, `'ss' < 'ß'`). `œ Œ þ Þ` are single-weight letters here (no expansion).
- **nvarchar** (Unicode weights): control characters plus apostrophe, hyphen, en/em dash, and soft-hyphen are minimal-weight — ignored at the primary/secondary levels, consulted only to break a remaining tie (`'coop' < 'co-op'`, `'cant' < "can't"`, `'A' < "'A"`). Expands the full Latin ligature set `æ Æ œ Œ ß þ Þ` and treats a ligature as **equal** to its expansion (`'æ' = 'ae'`, `'ß' = 'ss'` — no tertiary).
- **nvarchar — Thai block** (U+0E00–U+0E7F): extended onto the *same unified rank scale* as CP1252, from one combined `DENSE_RANK` over CP1252 ∪ Thai. SQL Server's SqlLatin1 Unicode sort places Thai by its own NLS weights — **not** code-point order and **not** matched by .NET/ICU (even ICU's `th-TH` orders them differently). Thai letters rank above all Latin; the leading vowels `เ แ โ ใ ไ` rank just above `'z'`; Thai digits between `'0'` and `'a'`. So `เบญจศร < คณาพล < บางสุขศรี` (the AdventureWorks `vJobCandidate.[Name.Last]` order). Thai tone-mark combining characters carry the lowest primary weight rather than SQL Server's secondary-diacritic treatment — a documented edge that doesn't affect tone-free data.
- `Equals` is `Compare == 0` for in-repertoire pairs; a pair with any out-of-repertoire character uses the inner `CultureCollation`'s **plain** equality rather than its two-pass ordering (see [Equality and hash across the repertoire boundary](#equality-and-hash-across-the-repertoire-boundary)). `GetHashCode` hashes the primary+secondary weight runs after a hash canonicalization pass, so DISTINCT / GROUP BY stay consistent with equality. Equality keeps every symbol significant (only trailing spaces fold, at the `SqlValue` layer), so `'co-op' = 'coop'` is false, and apostrophe ≠ hyphen even off-repertoire (probe-confirmed 2026-07-13: `N'ab''cＸ' = N'ab-cＸ'` is false and the two group separately).

One hand-adjustment in the data: the legacy varchar CI_AI form classifies cedilla (`Ç`/`ç`) as a distinct primary letter, but its CI_AS *sort* folds it onto `c` (probe-confirmed `'Çm' < 'cn'`), so those two primary entries are pinned to `c`'s rank. Strings with a character outside the active repertoire (CP1252, plus Thai for nvarchar) fall back to the inner `CultureCollation`'s `CompareInfo` two-pass (below) — close for arbitrary Unicode, exact for CP1252 and the Thai block. Adding the Thai block re-baked the nvarchar tables on the unified scale (now `ushort` — the union pushes the max rank past 255); the CP1252-only relative order is unchanged (`DENSE_RANK` is monotonic) and the 138k-pair fuzz stays at zero divergence.

**Known gap — trailing-space MAX/MIN representative.** This body sorts by collation weight, where SPACE is the lowest non-zero primary weight, so a trailing space makes a string sort *after* its trimmed form. Real SQL Server's sort instead treats trailing-space variants as *equal* (they interleave under `ORDER BY`), and `MAX`/`MIN` then returns a scan-order-dependent representative (empirically the last-seen of the equal group, vs. the aggregate `MinMaxAggregator`'s keep-first). So `MAX` over a column holding both `'ก'` and `'ก '` can return the other byte-variant than SQL Server. Surfaces on three AdventureWorks XML-demographic metrics (`vJobCandidateEducation` / `vJobCandidateEmployment` country/state); deferred — matching it needs trailing-space-insensitive compare *plus* SQL Server's unspecified MAX-tie + physical-scan-order semantics, for synthetic data.

Implementation: this is the engine's hottest string path (it backs `Baseline`), so `Compare` / `GetHashCode` stream each operand's weights through a `WeightCursor` `ref struct` — one element per `MoveNext`, ignorables skipped and ligatures expanded inline — rather than materializing weight lists. A comparison walks the cursors at the primary level, dropping to a second walk only on a primary tie and a third only on a secondary tie; the common case resolves in one pass with **zero allocation**. Keep the storage-aware `InRepertoire` gate ahead of the streaming path: the "any out-of-repertoire char ⇒ `CompareInfo` fallback for the whole pair" contract requires scanning both operands before choosing the repertoire path, which a bail-mid-walk detector would break.

## Equality and hash across the repertoire boundary

The hybrid body's `IEqualityComparer<string>` contract (`Equals(x, y)` ⇒ equal hashes) has to hold across two different equality sources: weight comparison for in-repertoire pairs and the inner `CultureCollation` for pairs with any out-of-repertoire character. Three pieces make it hold (`Collation.SqlLatin1Sort.cs`):

- **Cross-boundary `Equals` is the inner's *plain* equality**, not `inner.Compare == 0`. The inner's two-pass minimal-punctuation logic is an *ordering* device whose tie-break checks only minimal-vs-real per position and would equate apostrophe with hyphen; plain `CompareInfo` equality keeps them distinct marks — matching the live server (probed 2026-07-13) and matching `CultureCollation`'s own `Equals`/`Compare` split. Consequence: cross-boundary sort-equal-but-not-equal pairs exist, as they do on `CultureCollation` itself.
- **`GetHashCode` has a fast path and a canonicalized path.** A string whose every character is in the per-body *hash-clean* set (repertoire minus `hashFolds` keys — the overwhelmingly common case) hashes straight off its weight runs, unchanged from before. Anything else is canonicalized — NFC (composes `e`+U+0301 → `é`), then per-rune folds — and the canonical form takes the weight-run hash if it lands in-repertoire, else the inner hash (consistent with the inner equality that governs such pairs by construction).
- **Every fold substitutes inner-equal content**, so canonicalization preserves the inner equality relation; that plus "weight-equal in-repertoire pairs already hash equal" is the whole consistency argument. In-repertoire folds live in the hard-coded `hashFolds` table: ICU-ignorable controls + soft hyphen → empty, NBSP → space, `ª º ¹ ² ³` → base, Thai digits → ASCII digits, vulgar fractions → their FRACTION SLASH decompositions (deliberately out-of-repertoire targets: both spellings then take the inner hash together), the CP1252 case pairs whose legacy *varchar* weights are asymmetric (`Œ Š Ÿ Ž` → lowercase), and Thai SARA AM → NIKHAHIT + SARA AA. Out-of-repertoire runes resolve lazily (`ComputeRuneFold`, cached process-wide): NFKC+lowercase candidate accepted only when the inner collation confirms equality (so `ſ` → `s`, which ICU rejects, never lands), with a one-time repertoire scan fallback for wrong-direction decompositions (Greek `μ` → CP1252 `µ`, other scripts' decimal digits).

Why folds of *in-repertoire* characters exist at all: an out-of-repertoire spelling can be `Equals`-equal to two in-repertoire strings that are unequal to each other (fullwidth `２` equals both `2` and `²` through the inner collation), so those in-repertoire strings must share a hash — a legal collision of unequal strings. `CollationHashConsistencyTests` (Tests.Internal) guards the contract: repertoire-wide ICU-class sweep, Unicode-block normalization-variant sweep, seeded substitution fuzz, and the named triangles.

Downstream effect (the bug this closed): every hash container keyed by the collation now folds alternate spellings — fullwidth / decomposed / homoglyph references to user tables, schemas, and procedures resolve (`Database.Schemas`, `Schema.HeapTables` / `Procedures`), EXEC duplicate-named-argument detection folds (`@a` + fullwidth `@ａ` → Msg 8143 echoing the first-seen spelling), and GROUP BY / DISTINCT buckets fold data-level variants (`N's'`, `N'ｓ'`, `N'S'` → one group — probe-confirmed). Coverage: the `Regime1_*` fullwidth/decomposed tests in `NameComparisonRegimeTests.cs`.

## Name regimes outside the database collation

Two identifier surfaces do **not** follow the database collation (both probe-confirmed 2026-07-13 on a real `SQL_Latin1_General_CP1_CS_AS` database):

- **Variable / table-variable names** fold case, width, and kana type unconditionally — `declare @vx int; set @VX = 5` succeeds on a CS database, as does fullwidth `@ｖx` ≡ `@vx`. Comparer: `BatchContext.VariableNameComparer` (invariant `CompareInfo`, `IgnoreCase | IgnoreKanaType | IgnoreWidth`), keying `Variables` and `TableVariables` everywhere they're constructed. Note the contrast: *named-argument-to-parameter matching* (`exec p @A=1` against declared `@a`) **does** follow the database collation — on the CS database the case-flipped name doesn't bind (Msg 8144 too-many-arguments for sp_executesql).
- **Temp-table names** stay case-insensitive on a CS database (`#zzc` ≡ `#ZZC`), consistent with tempdb's server-collation inheritance.

Related tokenizer rule: non-spacing combining marks are identifier *continuation* characters (`Tokenizer.IsIdentifierBodyChar`) — a decomposed spelling (`zzcafe` + U+0301) both tokenizes and resolves against a composed `zzcafé` table on the live server (probed 2026-07-13); resolution comes free from the NFC step in hash canonicalization plus the inner equality.

## Symbol sort weighting (other SQL_\* / Windows / locale families)

`CultureCollation.Compare` (the `CompareInfo`-routed comparer behind every collation **other than the default**) gives hyphen (`-`) and apostrophe (`'`) the **minimal-weight** treatment SQL Server applies, while every other symbol keeps a real primary weight:

- **Non-minimal symbols (`#`, `+`, `,`, `!`, `~`, `_`, …) sort first** — ahead of digits and letters. .NET's `CompareOptions.IgnoreSymbols` would *strip* these (mis-ranking `'#500-75'` among the digits as `50075`); plain `CompareInfo` without it keeps them, which is what the comparer uses.
- **Hyphen and apostrophe drop out of the primary key** but carry a secondary weight, so the copy bearing the mark sorts *after*: `'coop' < 'co-op'`, `'cant' < "can't"`, `'A' < "'A"`.

Implementation: a fast path (`compareInfo.Compare(x, y, equalityOptions)`) when neither operand contains a minimal mark; otherwise a primary pass over hyphen/apostrophe-stripped copies, then `MinimalPunctuationTiebreak` (a two-pointer scan where a minimal mark sorts after a real character). This is structurally faithful but not byte-exact for symbol-internal order or accent multi-level — only the default collation gets the bespoke exact tables. The same approach could extend to other heavily-used names if a divergence surfaces.

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

**Guard — nvarchar `_BIN2` is UTF-16 *code-unit*, not code-point; don't "fix" it.** Microsoft's `_BIN2` documentation describes the ordering in code-point terms, which invites a well-meaning correction toward 32-bit scalar comparison. Empirically (probed on SQL Server 2025, 2026-05-21) the box compares UTF-16 code units — i.e. surrogate pairs are compared as their two 16-bit halves, not as the combined scalar. So `(nchar(0xD83D)+nchar(0xDE00)) < nchar(0xE000)` is **true** under `Latin1_General_BIN2` (emoji U+1F600 sorts before U+E000 because the high surrogate 0xD83D < 0xE000), even though the scalar 0x1F600 > 0xE000. `StringComparer.Ordinal` is UTF-16 code-unit comparison, so `BinaryCollation` over `Ordinal` is byte-exact for nvarchar BIN2 *including* emoji / supplementary chars — adding code-point logic would break currently-correct behavior. (The varchar `_BIN2_UTF8` substitute *is* codepoint-ordered, because UTF-8 byte order equals surrogate-pair-combined scalar order — that's the one place the two coincide.)

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
