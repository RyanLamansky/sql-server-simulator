using System.Collections.Frozen;
using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server's collation-precedence rank. Used by the expression layer to
/// decide which collation wins when two string operands meet; mismatched
/// peers raise <c>Msg 468</c> / <c>Msg 457</c>.
/// </summary>
/// <remarks>
/// Real SQL Server defines additional ranks (<c>Coercible-default</c>,
/// <c>Implicit X</c>, <c>Explicit X</c>, <c>No-collation</c>); the simulator
/// collapses them to the three that drive observable behavior. Hierarchy:
/// <see cref="Explicit"/> beats <see cref="Implicit"/> beats
/// <see cref="CoercibleDefault"/>; peers with the same rank but different
/// collations raise the conflict error.
/// </remarks>
internal enum Coercibility : byte
{
    /// <summary>Literal, parameter, system-function result, CAST of a coercible-default source. Yields to <see cref="Implicit"/> / <see cref="Explicit"/> peers.</summary>
    CoercibleDefault = 0,

    /// <summary>Column reference, CAST of a column, computed-column expression. Two implicits with different collations raise Msg 468 / 457.</summary>
    Implicit = 1,

    /// <summary>Explicit <c>COLLATE</c> postfix. Beats both lower ranks; two explicits with different collations raise Msg 468 / 457.</summary>
    Explicit = 2,
}

/// <summary>
/// The SQL Server equivalent to .NET's <see cref="IComparer{T}"/> for strings.
/// Three concrete subclasses match the names on the
/// <see cref="Recognized"/> whitelist; each implements its own case /
/// accent / symbol semantics rather than routing through a shared
/// hack-stripped comparer.
/// </summary>
internal abstract class Collation : IComparer<string>, IEqualityComparer<string>
{
    private protected Collation()
    {
    }

    public abstract string Name { get; }

    /// <summary>
    /// True when the collation name carries the <c>_CS_</c> or <c>_BIN</c>
    /// marker — i.e. case differences are significant. Consulted by
    /// <c>LIKE</c>'s regex builder to decide whether to set
    /// <c>System.Text.RegularExpressions.RegexOptions.IgnoreCase</c>.
    /// Other string ops (sort, equality, hash) honor the collation
    /// directly through <see cref="Compare"/> / <see cref="Equals"/> /
    /// <see cref="GetHashCode"/> via the column type's pinned collation.
    /// </summary>
    public virtual bool CaseSensitive => false;

    /// <summary>
    /// The instance returned for "SQL_Latin1_General_CP1_CI_AS" — the
    /// simulator's default database collation. Used by
    /// <see cref="Database.CollationName"/> when no explicit
    /// <c>ALTER DATABASE COLLATE</c> has been issued, and as the fallback
    /// when a string-typed value's <see cref="SqlType.Collation"/> is
    /// <see langword="null"/> (literals built via the singleton
    /// <c>SqlValue.FromVarchar(string)</c> / etc. paths).
    /// </summary>
    internal static readonly SQL_Latin1_General_CP1_CI_AS Default = new();

    /// <summary>
    /// "Latin1_General_100_CI_AS" — the Windows-style v100 CI_AS sort
    /// rules. Sort treats primary-weight-zero symbols (apostrophe, hyphen,
    /// and the rest of <see cref="CompareOptions.IgnoreSymbols"/>) as
    /// ignorable; equality keeps them significant — matches probe-confirmed
    /// real-SQL-Server behavior where <c>MIN('Aaronsburg','''Aiea') =
    /// 'Aaronsburg'</c> while <c>'OBrien' = 'O''Brien'</c> evaluates 0.
    /// </summary>
    internal static readonly Latin1_General_100_CI_AS Latin1General100CiAs = new();

    /// <summary>
    /// "Latin1_General_CI_AS" — the older Windows-style CI_AS (pre-v100).
    /// For the Latin-1 + ASCII strings the simulator's regression bar
    /// exercises, semantically identical to <see cref="Latin1General100CiAs"/>
    /// (the v100 update changed Unicode-table ordering for non-Latin
    /// scripts and a handful of newly-added supplementary code points;
    /// none of those reach BACPAC payloads the simulator loads).
    /// </summary>
    internal static readonly Latin1_General_CI_AS Latin1GeneralCiAs = new();

    /// <summary>
    /// "Latin1_General_CS_AS" — Windows-style case-sensitive, accent-
    /// sensitive. Same kanatype-/width-insensitive options as the
    /// <see cref="WindowsCiAs"/> family minus <see cref="CompareOptions.IgnoreCase"/>.
    /// The simulator's <c>LIKE</c> path consults <see cref="CaseSensitive"/>
    /// to flip <c>RegexOptions.IgnoreCase</c>; comparison / sort outside
    /// LIKE still falls through to <see cref="Default"/>.
    /// </summary>
    internal static readonly Latin1_General_CS_AS Latin1GeneralCsAs = new();

    /// <summary>
    /// "Latin1_General_BIN" — pre-SQL-Server-2005 binary collation. nvarchar
    /// / nchar values use the asymmetric "code unit at position 0,
    /// code point at position 1+" rule (see
    /// <see cref="Latin1_General_BIN.Compare"/>); varchar / char values use
    /// CP1252 byte compare under <see cref="Latin1GeneralBinForVarchar"/>
    /// via <see cref="ForVarcharStorage"/>. Diverges from
    /// <see cref="Latin1GeneralBin2"/> only when supplementary characters
    /// appear at position 1+ — uncommon, but probe-confirmed against
    /// SQL Server 2025.
    /// </summary>
    internal static readonly Latin1_General_BIN Latin1GeneralBin = new();

    /// <summary>
    /// "Latin1_General_BIN2" — v2 binary collation. Same storage-aware
    /// dispatch as <see cref="Latin1GeneralBin"/>: nvarchar / nchar
    /// columns use UTF-16 code-unit ordinal compare; varchar / char
    /// columns use <see cref="Latin1GeneralBin2ForVarchar"/> for CP1252
    /// byte compare. The BIN-vs-BIN2 distinction on legacy varchar
    /// (code-page-prefix-then-byte vs pure byte) isn't observable through
    /// the simulator's single-codepage value layer.
    /// </summary>
    internal static readonly Latin1_General_BIN2 Latin1GeneralBin2 = new();

    /// <summary>
    /// CP1252-byte body for <see cref="Latin1GeneralBin"/> when pinned on
    /// a varchar / char column. Substituted in by
    /// <see cref="VarcharSqlType.WithCollation"/> /
    /// <see cref="CharSqlType.WithCollation"/> via
    /// <see cref="ForVarcharStorage"/>. <see cref="Name"/> matches the
    /// nvarchar sibling so catalog views report a single collation name.
    /// </summary>
    internal static readonly Cp1252BinaryCollation Latin1GeneralBinForVarchar = new("Latin1_General_BIN");

    /// <summary>
    /// CP1252-byte body for <see cref="Latin1GeneralBin2"/> when pinned
    /// on a varchar / char column. See <see cref="Latin1GeneralBinForVarchar"/>
    /// for the dispatch.
    /// </summary>
    internal static readonly Cp1252BinaryCollation Latin1GeneralBin2ForVarchar = new("Latin1_General_BIN2");

    /// <summary>
    /// "Japanese_XJIS_140_CI_AS" — the modern Japanese collation that
    /// handles supplementary characters via the XJIS-140 mapping table.
    /// Comparison routes through .NET's <c>ja-JP</c> <see cref="CompareInfo"/>
    /// with the simulator's standard CI/AS options (case-insensitive,
    /// accent-sensitive, kana-type-insensitive, width-insensitive).
    /// Equality + kana-type / width folding match SQL Server end-to-end;
    /// the secondary sort tiebreaker inside the hiragana / full-width
    /// katakana / half-width katakana equivalence classes diverges
    /// (probe-confirmed against SQL Server 2025: ~half the positions
    /// reorder on nvarchar; varchar essentially unusable since CP1252
    /// can't represent Japanese where real SQL Server uses CP932). See
    /// <c>docs/claude/collations.md</c> "Locale-comparer sort-parity gap".
    /// </summary>
    internal static readonly CultureCollation JapaneseXJIS140CiAs = new("Japanese_XJIS_140_CI_AS", "ja-JP", caseSensitive: false);

    /// <summary>
    /// "Chinese_PRC_CI_AS" — Simplified Chinese (pinyin sort) via .NET's
    /// <c>zh-CN</c> <see cref="CompareInfo"/>. Internal pinyin ordering
    /// mostly aligns with SQL Server 2025; the .NET vs SQL Server
    /// convention for Latin-vs-CJK block position is reversed (.NET puts
    /// CJK before Latin, SQL Server puts Latin before CJK), so any
    /// mixed-script ORDER BY will shift every position. See
    /// <c>docs/claude/collations.md</c> "Locale-comparer sort-parity gap".
    /// </summary>
    internal static readonly CultureCollation ChinesePrcCiAs = new("Chinese_PRC_CI_AS", "zh-CN", caseSensitive: false);

    /// <summary>
    /// "Turkish_CI_AS" — Turkish collation via .NET's <c>tr-TR</c>
    /// <see cref="CompareInfo"/>. Notably handles the i / İ / ı / I
    /// folding that catches non-Turkish-aware code (the "Turkish-i
    /// problem"). Equality / case-folding match SQL Server end-to-end on
    /// nvarchar; tiebreaker within case-equivalence classes (e.g. `çay`
    /// vs `Çay`) differs (~2 / 19 position drift on probed inputs). See
    /// <c>docs/claude/collations.md</c> "Locale-comparer sort-parity gap".
    /// </summary>
    internal static readonly CultureCollation TurkishCiAs = new("Turkish_CI_AS", "tr-TR", caseSensitive: false);

    /// <summary>
    /// "Latin1_General_CI_AS_KS_WS" — Latin1 variant with kana-type and
    /// width <em>sensitive</em>. Used in real databases for some sysname
    /// columns and a handful of user columns. Comparison routes through
    /// the invariant culture with `IgnoreKanaType` / `IgnoreWidth` left
    /// OFF, so full-width katakana / hiragana / half-width katakana of
    /// the same logical character all compare distinct
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static readonly CultureCollation Latin1GeneralCiAsKsWs = new(
        "Latin1_General_CI_AS_KS_WS",
        CultureInfo.InvariantCulture.Name,
        caseSensitive: false,
        kanaTypeSensitive: true,
        widthSensitive: true);

    /// <summary>
    /// "SQL_Latin1_General_CP437_CS_AS" — legacy CP437 code-page binding
    /// (the original IBM PC code page), case-sensitive. The simulator
    /// routes comparison through invariant culture case-sensitive options
    /// — close enough for the system-column shapes this collation actually
    /// appears in; non-ASCII CP437 glyphs aren't probed.
    /// </summary>
    internal static readonly CultureCollation SqlLatin1GeneralCp437CsAs = new("SQL_Latin1_General_CP437_CS_AS", CultureInfo.InvariantCulture.Name, caseSensitive: true);

    /// <summary>
    /// "UNICODE_CODEPOINT" — pure ordinal Unicode codepoint comparison.
    /// Semantically equivalent to <see cref="Latin1GeneralBin2"/> at the
    /// value level; the name appears on a handful of system columns in
    /// some BACPACs (notably AdventureWorks2025). Reuses the binary
    /// codepath via a separate metadata-only instance.
    /// </summary>
    internal static readonly UNICODE_CODEPOINT UnicodeCodepoint = new();

    /// <summary>Metadata-only binary collation under the
    /// <c>UNICODE_CODEPOINT</c> name; behavior body is <see cref="BinaryCollation"/>.</summary>
    internal sealed class UNICODE_CODEPOINT : BinaryCollation
    {
        public override string Name => "UNICODE_CODEPOINT";
    }

    /// <summary>"Korean_100_CI_AS" — Korean (Hangul) v100 sort via .NET's
    /// <c>ko-KR</c> <see cref="CompareInfo"/>. Same sort-tiebreaker
    /// divergence caveat as the other locale collations
    /// (see <c>docs/claude/collations.md</c>).</summary>
    internal static readonly CultureCollation Korean100CiAs = new("Korean_100_CI_AS", "ko-KR", caseSensitive: false);

    /// <summary>"Korean_Wansung_CI_AS" — legacy Korean Wansung code-page
    /// binding. Behavior body identical to <see cref="Korean100CiAs"/> at
    /// the simulator's value layer; the Wansung-vs-v100 distinction is a
    /// non-Unicode codepage detail that the simulator's UTF-16 storage
    /// doesn't materialize.</summary>
    internal static readonly CultureCollation KoreanWansungCiAs = new("Korean_Wansung_CI_AS", "ko-KR", caseSensitive: false);

    /// <summary>"Greek_CI_AS" — Greek collation via .NET's <c>el-GR</c>
    /// <see cref="CompareInfo"/>. Tonos / dialytika fold under accent-
    /// sensitive rules; final-sigma (ς) vs medial-sigma (σ) treated as
    /// case-insensitive peers (matches real SQL Server).</summary>
    internal static readonly CultureCollation GreekCiAs = new("Greek_CI_AS", "el-GR", caseSensitive: false);

    /// <summary>"Greek_100_CI_AS" — v100 Greek collation. Same culture as
    /// <see cref="GreekCiAs"/>; the v100 update touches Unicode-table
    /// ordering for supplementary characters not relevant to most Greek
    /// text.</summary>
    internal static readonly CultureCollation Greek100CiAs = new("Greek_100_CI_AS", "el-GR", caseSensitive: false);

    /// <summary>"Cyrillic_General_CI_AS" — pan-Cyrillic collation routed
    /// through .NET's <c>ru-RU</c> <see cref="CompareInfo"/>. Covers
    /// Russian, Ukrainian, Bulgarian, Serbian, etc. at the same fidelity
    /// bar as the other locale collations — equality / case folding align;
    /// secondary sort tiebreakers may differ.</summary>
    internal static readonly CultureCollation CyrillicGeneralCiAs = new("Cyrillic_General_CI_AS", "ru-RU", caseSensitive: false);

    /// <summary>"Cyrillic_General_100_CI_AS" — v100 Cyrillic. Same culture
    /// as <see cref="CyrillicGeneralCiAs"/>.</summary>
    internal static readonly CultureCollation CyrillicGeneral100CiAs = new("Cyrillic_General_100_CI_AS", "ru-RU", caseSensitive: false);

    /// <summary>"German_PhoneBook_CI_AS" — German with phonebook sort
    /// (ä → ae, ö → oe, ü → ue, ß → ss equivalence at sort time). The
    /// simulator routes through .NET's <c>de-DE</c> default ordering
    /// (umlaut-as-letter, not phonebook). Recognized for BACPAC quiet-
    /// loading; sort order for the umlauted letters diverges from real
    /// SQL Server. Apps that rely on phonebook ordering hit the broader
    /// locale-comparer sort-parity gap documented in collations.md.</summary>
    internal static readonly CultureCollation GermanPhoneBookCiAs = new("German_PhoneBook_CI_AS", "de-DE", caseSensitive: false);

    /// <summary>"German_PhoneBook_100_CI_AS" — v100 German phonebook.
    /// Same routing as <see cref="GermanPhoneBookCiAs"/>; same phonebook
    /// divergence applies.</summary>
    internal static readonly CultureCollation GermanPhoneBook100CiAs = new("German_PhoneBook_100_CI_AS", "de-DE", caseSensitive: false);

    /// <summary>"French_CI_AS" — French via .NET's <c>fr-FR</c>
    /// <see cref="CompareInfo"/>. Note: real SQL Server's French
    /// collation sorts accents from the END of the string (a French-
    /// specific rule); .NET's <c>fr-FR</c> default doesn't, so accented
    /// strings near each other sort differently. Same fidelity bar as
    /// the other locale collations.</summary>
    internal static readonly CultureCollation FrenchCiAs = new("French_CI_AS", "fr-FR", caseSensitive: false);

    /// <summary>"French_100_CI_AS" — v100 French.</summary>
    internal static readonly CultureCollation French100CiAs = new("French_100_CI_AS", "fr-FR", caseSensitive: false);

    /// <summary>"Modern_Spanish_CI_AS" — Spanish (modern, no ch/ll as
    /// separate letters) via .NET's <c>es-ES</c> <see cref="CompareInfo"/>.
    /// .NET's default Spanish sort already follows the modern convention,
    /// so equality / sort alignment is closer here than for the other
    /// European locales.</summary>
    internal static readonly CultureCollation ModernSpanishCiAs = new("Modern_Spanish_CI_AS", "es-ES", caseSensitive: false);

    /// <summary>"Modern_Spanish_100_CI_AS" — v100 modern Spanish.</summary>
    internal static readonly CultureCollation ModernSpanish100CiAs = new("Modern_Spanish_100_CI_AS", "es-ES", caseSensitive: false);

    /// <summary>"Latin1_General_100_CI_AS_SC_UTF8" — Latin1 v100 CI_AS
    /// with supplementary-character support and UTF-8 varchar storage.
    /// UTF-8 is a storage encoding only; sort / compare semantics are
    /// identical to <see cref="Latin1General100CiAs"/>. .NET's
    /// <see cref="CompareInfo"/> handles surrogate pairs natively, so the
    /// SC marker doesn't require special handling either.</summary>
    internal static readonly CultureCollation Latin1General100CiAsScUtf8 = new(
        "Latin1_General_100_CI_AS_SC_UTF8", CultureInfo.InvariantCulture.Name, caseSensitive: false);

    /// <summary>"Latin1_General_100_CS_AS_SC_UTF8" — case-sensitive UTF-8
    /// variant. Sort / compare matches <see cref="Latin1GeneralCsAs"/>;
    /// UTF-8 / SC distinctions are storage-layer only.</summary>
    internal static readonly CultureCollation Latin1General100CsAsScUtf8 = new(
        "Latin1_General_100_CS_AS_SC_UTF8", CultureInfo.InvariantCulture.Name, caseSensitive: true);

    /// <summary>"Latin1_General_100_BIN2_UTF8" — binary UTF-8 variant.
    /// Pure codepoint comparison via <see cref="BinaryCollation"/>;
    /// metadata-only instance for catalog recognition.</summary>
    internal static readonly Latin1_General_100_BIN2_UTF8 Latin1General100Bin2Utf8 = new();

    /// <summary>Metadata-only binary collation under the
    /// <c>Latin1_General_100_BIN2_UTF8</c> name.</summary>
    internal sealed class Latin1_General_100_BIN2_UTF8 : BinaryCollation
    {
        public override string Name => "Latin1_General_100_BIN2_UTF8";
    }

    /// <summary>
    /// Closed accept-list of collation names the simulator recognizes.
    /// ALTER DATABASE COLLATE / CREATE TABLE column COLLATE accept these
    /// names without raising; the loader records them on the database /
    /// column for catalog-view round-trip and pins the resolved
    /// <see cref="Collation"/> instance onto the column's
    /// <see cref="SqlType"/> at <see cref="Coercibility.Implicit"/> rank,
    /// so subsequent comparisons / sorts honor the declared collation.
    /// Cross-collation operand pairs that can't be resolved by coercibility
    /// raise Msg 468 (comparison) / Msg 457 (concat). Names outside this
    /// set surface as <see cref="NotSupportedException"/> in direct SQL;
    /// the BACPAC loader catches and records on
    /// <c>BacpacImportResult.Warnings</c>. Each entry's value is the
    /// human-readable description that <c>sys.fn_helpcollations()</c>
    /// exposes verbatim (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static readonly FrozenDictionary<string, string> Recognized =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Default.Name] = "Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 52 on Code Page 1252 for non-Unicode Data",
            [Latin1General100CiAs.Name] = "Latin1-General-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Latin1GeneralCiAs.Name] = "Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Latin1GeneralCsAs.Name] = "Latin1-General, case-sensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Latin1GeneralBin.Name] = "Latin1-General, binary",
            [Latin1GeneralBin2.Name] = "Latin1-General, binary code point comparison sort",
            [Latin1GeneralCiAsKsWs.Name] = "Latin1-General, case-insensitive, accent-sensitive, kanatype-sensitive, width-sensitive",
            [SqlLatin1GeneralCp437CsAs.Name] = "Latin1-General, case-sensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 30 on Code Page 437 for non-Unicode Data",
            [UnicodeCodepoint.Name] = "Unicode code point comparison sort",
            [JapaneseXJIS140CiAs.Name] = "Japanese-XJIS-140, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [ChinesePrcCiAs.Name] = "Chinese-PRC, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [TurkishCiAs.Name] = "Turkish, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Korean100CiAs.Name] = "Korean-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [KoreanWansungCiAs.Name] = "Korean-Wansung, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [GreekCiAs.Name] = "Greek, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Greek100CiAs.Name] = "Greek-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [CyrillicGeneralCiAs.Name] = "Cyrillic-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [CyrillicGeneral100CiAs.Name] = "Cyrillic-General-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [GermanPhoneBookCiAs.Name] = "German-PhoneBook, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [GermanPhoneBook100CiAs.Name] = "German-PhoneBook-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [FrenchCiAs.Name] = "French, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [French100CiAs.Name] = "French-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [ModernSpanishCiAs.Name] = "Modern-Spanish, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [ModernSpanish100CiAs.Name] = "Modern-Spanish-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Latin1General100CiAsScUtf8.Name] = "Latin1-General-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive, supplementary characters, UTF8",
            [Latin1General100CsAsScUtf8.Name] = "Latin1-General-100, case-sensitive, accent-sensitive, kanatype-insensitive, width-insensitive, supplementary characters, UTF8",
            [Latin1General100Bin2Utf8.Name] = "Latin1-General-100, binary code point comparison sort, UTF8",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Name-keyed lookup of the recognized <see cref="Collation"/>
    /// instances. Case-insensitive (SQL Server collation names are
    /// themselves case-insensitive identifiers). Keys and entries are
    /// kept in sync with <see cref="Recognized"/>.
    /// </summary>
    internal static readonly FrozenDictionary<string, Collation> ByName =
        new Dictionary<string, Collation>(StringComparer.OrdinalIgnoreCase)
        {
            [Default.Name] = Default,
            [Latin1General100CiAs.Name] = Latin1General100CiAs,
            [Latin1GeneralCiAs.Name] = Latin1GeneralCiAs,
            [Latin1GeneralCsAs.Name] = Latin1GeneralCsAs,
            [Latin1GeneralBin.Name] = Latin1GeneralBin,
            [Latin1GeneralBin2.Name] = Latin1GeneralBin2,
            [Latin1GeneralCiAsKsWs.Name] = Latin1GeneralCiAsKsWs,
            [SqlLatin1GeneralCp437CsAs.Name] = SqlLatin1GeneralCp437CsAs,
            [UnicodeCodepoint.Name] = UnicodeCodepoint,
            [JapaneseXJIS140CiAs.Name] = JapaneseXJIS140CiAs,
            [ChinesePrcCiAs.Name] = ChinesePrcCiAs,
            [TurkishCiAs.Name] = TurkishCiAs,
            [Korean100CiAs.Name] = Korean100CiAs,
            [KoreanWansungCiAs.Name] = KoreanWansungCiAs,
            [GreekCiAs.Name] = GreekCiAs,
            [Greek100CiAs.Name] = Greek100CiAs,
            [CyrillicGeneralCiAs.Name] = CyrillicGeneralCiAs,
            [CyrillicGeneral100CiAs.Name] = CyrillicGeneral100CiAs,
            [GermanPhoneBookCiAs.Name] = GermanPhoneBookCiAs,
            [GermanPhoneBook100CiAs.Name] = GermanPhoneBook100CiAs,
            [FrenchCiAs.Name] = FrenchCiAs,
            [French100CiAs.Name] = French100CiAs,
            [ModernSpanishCiAs.Name] = ModernSpanishCiAs,
            [ModernSpanish100CiAs.Name] = ModernSpanish100CiAs,
            [Latin1General100CiAsScUtf8.Name] = Latin1General100CiAsScUtf8,
            [Latin1General100CsAsScUtf8.Name] = Latin1General100CsAsScUtf8,
            [Latin1General100Bin2Utf8.Name] = Latin1General100Bin2Utf8,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if <paramref name="name"/> is on the
    /// <see cref="Recognized"/> whitelist. Case-insensitive (SQL Server
    /// collation names are themselves case-insensitive identifiers).
    /// </summary>
    internal static bool IsRecognized(string name) => Recognized.ContainsKey(name);

    /// <summary>
    /// SQL Server's collation-coercibility resolution for two operands.
    /// Returns the winning <see cref="Collation"/> and <see cref="Coercibility"/>
    /// when the pair is resolvable; <see langword="null"/> when both
    /// operands have the same rank but different collations (the caller is
    /// expected to raise Msg 468 / Msg 457 with operator context).
    /// </summary>
    /// <remarks>
    /// <para>Hierarchy: <see cref="Coercibility.Explicit"/> beats
    /// <see cref="Coercibility.Implicit"/> beats
    /// <see cref="Coercibility.CoercibleDefault"/>. Mismatched higher rank
    /// wins regardless of the lower-rank operand's collation. Equal-rank
    /// peers must share a collation; different collations at the same rank
    /// are unresolvable.</para>
    /// <para>Non-string operands return <see langword="null"/> for
    /// <c>Collation</c> on the SqlType, which collapses to the default
    /// collation under this resolution (a non-string operand is
    /// coercible-default by definition, and there's nothing to conflict
    /// with).</para>
    /// </remarks>
    internal static (Collation Collation, Coercibility Coercibility)? Resolve(SqlType a, SqlType b)
    {
        var ca = a.Coercibility;
        var cb = b.Coercibility;
        if (ca > cb)
            return (a.Collation ?? Default, ca);
        if (cb > ca)
            return (b.Collation ?? Default, cb);
        var aCol = a.Collation ?? Default;
        var bCol = b.Collation ?? Default;
        return StringComparer.OrdinalIgnoreCase.Equals(aCol.Name, bCol.Name) ? (aCol, ca) : null;
    }

    public abstract int Compare(string? x, string? y);

    public abstract bool Equals(string? x, string? y);

    public abstract int GetHashCode(string obj);

    /// <summary>
    /// Returns the comparer body to use when this collation is pinned on
    /// a varchar / char column (CP1252 storage). The base implementation
    /// returns <see langword="this"/> — most collations compare via .NET
    /// <see cref="CompareInfo"/> on UTF-16 string content regardless of
    /// storage encoding. Binary collations override to return a CP1252-
    /// byte-aware sibling: real SQL Server's BIN / BIN2 on varchar
    /// byte-compares CP1252, which diverges from UTF-16 codepoint compare
    /// in the 0x80-0x9F window where Unicode codepoints (e.g. U+20AC `€`,
    /// U+0192 `ƒ`) scatter across the BMP. Called by
    /// <see cref="VarcharSqlType.WithCollation"/> and
    /// <see cref="CharSqlType.WithCollation"/>; <c>NVarcharSqlType</c> /
    /// <c>NCharSqlType</c> don't substitute (UTF-16 storage already
    /// matches the UTF-16 code-unit-order body).
    /// </summary>
    internal virtual Collation ForVarcharStorage() => this;

    /// <summary>
    /// Shared host for the Windows-flavored CI_AS pair
    /// (<see cref="Latin1_General_100_CI_AS"/> /
    /// <see cref="Latin1_General_CI_AS"/>). Both pin to the invariant
    /// culture's <see cref="CompareInfo"/> with
    /// <see cref="CompareOptions.IgnoreCase"/> +
    /// <see cref="CompareOptions.IgnoreKanaType"/> +
    /// <see cref="CompareOptions.IgnoreWidth"/> — the same option set the
    /// collation name advertises. <see cref="Compare"/> additionally
    /// ignores <see cref="CompareOptions.IgnoreSymbols"/> so primary-
    /// weight-zero punctuation (apostrophe, hyphen, …) sorts as if it
    /// weren't there; <see cref="Equals"/> / <see cref="GetHashCode"/>
    /// keep symbols significant, mirroring real SQL Server's
    /// sort-only ignore-symbols treatment.
    /// </summary>
    internal abstract class WindowsCiAs : Collation
    {
        private static readonly CompareInfo Compare_Info = CultureInfo.InvariantCulture.CompareInfo;

        private const CompareOptions EqualityOptions =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        private const CompareOptions SortOptions = EqualityOptions | CompareOptions.IgnoreSymbols;

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : Compare_Info.Compare(x, y, SortOptions);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && Compare_Info.Compare(x, y, EqualityOptions) == 0;

        public override int GetHashCode(string obj) =>
            Compare_Info.GetHashCode(obj, EqualityOptions);
    }

    /// <summary>
    /// SQL Server's "SQL_Latin1_General_CP1_CI_AS" — Sort Order 52 on
    /// CP1252 for non-Unicode and Latin1-General CI_AS rules for Unicode.
    /// Case-insensitive, accent-sensitive, and (unlike the Windows-style
    /// CI_AS family) does NOT treat apostrophe / hyphen as primary-
    /// weight-zero in sort. Modeled with the invariant culture's
    /// <see cref="CompareInfo"/> + <see cref="CompareOptions.IgnoreCase"/>
    /// for both <see cref="Compare"/> and <see cref="Equals"/>; this
    /// adds the linguistic-comparison fidelity (Unicode-normalization
    /// equivalence between NFD and NFC forms, halfwidth/fullwidth
    /// equivalence) that <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// lacks — both fold precomposed Latin-1 accented letters
    /// (<c>é = É</c>) identically.
    /// </summary>
    internal sealed class SQL_Latin1_General_CP1_CI_AS : Collation
    {
        private static readonly CompareInfo Compare_Info = CultureInfo.InvariantCulture.CompareInfo;

        private const CompareOptions Options =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        public override string Name => "SQL_Latin1_General_CP1_CI_AS";

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : Compare_Info.Compare(x, y, Options);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && Compare_Info.Compare(x, y, Options) == 0;

        public override int GetHashCode(string obj) =>
            Compare_Info.GetHashCode(obj, Options);
    }

    /// <summary>
    /// "Latin1_General_100_CI_AS" — Windows-style v100 sort rules.
    /// Behavior body lives on <see cref="WindowsCiAs"/>; this class
    /// supplies the metadata <see cref="Name"/> only.
    /// </summary>
    internal sealed class Latin1_General_100_CI_AS : WindowsCiAs
    {
        public override string Name => "Latin1_General_100_CI_AS";
    }

    /// <summary>
    /// "Latin1_General_CI_AS" — Windows-style pre-v100 sort rules.
    /// Behavior body lives on <see cref="WindowsCiAs"/>; this class
    /// supplies the metadata <see cref="Name"/> only.
    /// </summary>
    internal sealed class Latin1_General_CI_AS : WindowsCiAs
    {
        public override string Name => "Latin1_General_CI_AS";
    }

    /// <summary>
    /// "Latin1_General_CS_AS" — Windows-style case-sensitive, accent-
    /// sensitive. Compare/Equals/GetHashCode use the invariant culture's
    /// <see cref="CompareInfo"/> with <see cref="CompareOptions.IgnoreKanaType"/>
    /// + <see cref="CompareOptions.IgnoreWidth"/> only (no IgnoreCase).
    /// Used by <c>LIKE</c>'s regex builder via <see cref="CaseSensitive"/>;
    /// equality/sort outside LIKE still route through <see cref="Default"/>.
    /// </summary>
    internal sealed class Latin1_General_CS_AS : Collation
    {
        private static readonly CompareInfo Compare_Info = CultureInfo.InvariantCulture.CompareInfo;

        private const CompareOptions Options = CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        public override string Name => "Latin1_General_CS_AS";

        public override bool CaseSensitive => true;

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : Compare_Info.Compare(x, y, Options);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && Compare_Info.Compare(x, y, Options) == 0;

        public override int GetHashCode(string obj) =>
            Compare_Info.GetHashCode(obj, Options);
    }

    /// <summary>
    /// Shared host for the two binary collations
    /// (<see cref="Latin1_General_BIN"/> / <see cref="Latin1_General_BIN2"/>).
    /// Both route through <see cref="StringComparer.Ordinal"/>; case- and
    /// accent-sensitive by construction. Consulted by <c>LIKE</c>'s regex
    /// builder via <see cref="CaseSensitive"/> (codepoint-level matching
    /// is what .NET regex already does when IgnoreCase is off). The
    /// BIN-vs-BIN2 asymmetry on non-Unicode <c>varchar</c> sort (BIN does
    /// code-page-prefix-then-byte, BIN2 does pure byte) isn't visible
    /// through the simulator's SQL surface, which routes equality / sort
    /// outside LIKE through <see cref="Default"/> regardless.
    /// </summary>
    internal abstract class BinaryCollation : Collation
    {
        public override bool CaseSensitive => true;

        public override int Compare(string? x, string? y) => StringComparer.Ordinal.Compare(x, y);

        public override bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);

        public override int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(obj);
    }

    /// <summary>
    /// "Latin1_General_BIN" — pre-SQL-Server-2005 binary collation. nvarchar
    /// body overrides <see cref="Compare"/> to model the asymmetric
    /// position-0-vs-rest rule: the first 16-bit code unit compares as-is
    /// (matches BIN2), but at position 1+ surrogate pairs combine into
    /// 32-bit scalars before comparing (code-point order). Equals /
    /// GetHashCode stay on <see cref="StringComparer.Ordinal"/> from
    /// <see cref="BinaryCollation"/> — two strings that compare-equal under
    /// either rule produce the same code-unit sequence. Probe-confirmed
    /// against SQL Server 2025 (2026-05-21):
    /// <c>('Z' + N'😀') &gt; ('Z' + N'') collate Latin1_General_BIN</c>
    /// is TRUE (code-point: 0x1F600 &gt; 0xE000), while the same query
    /// under BIN2 returns FALSE (code-unit: 0xD83D &lt; 0xE000). For BMP-
    /// only inputs the two rules agree, so the override only matters when
    /// supplementary characters appear at position 1+.
    /// </summary>
    internal sealed class Latin1_General_BIN : BinaryCollation
    {
        public override string Name => "Latin1_General_BIN";

        internal override Collation ForVarcharStorage() => Latin1GeneralBinForVarchar;

        public override int Compare(string? x, string? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;
            var minLen = Math.Min(x.Length, y.Length);
            var i = 0;
            while (i < minLen)
            {
                int xVal, yVal, advance;
                if (i == 0)
                {
                    xVal = x[0];
                    yVal = y[0];
                    advance = 1;
                }
                else
                {
                    xVal = ScalarAt(x, i, out var xAdv);
                    yVal = ScalarAt(y, i, out _);
                    advance = xAdv;
                }
                if (xVal != yVal) return xVal.CompareTo(yVal);
                i += advance;
            }
            return x.Length.CompareTo(y.Length);
        }

        private static int ScalarAt(string s, int i, out int advance)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                advance = 2;
                return char.ConvertToUtf32(c, s[i + 1]);
            }
            advance = 1;
            return c;
        }
    }

    /// <summary>
    /// "Latin1_General_BIN2" — v2 binary collation. Behavior body lives
    /// on <see cref="BinaryCollation"/>; this class supplies the metadata
    /// <see cref="Name"/> and the varchar-storage substitution.
    /// </summary>
    internal sealed class Latin1_General_BIN2 : BinaryCollation
    {
        public override string Name => "Latin1_General_BIN2";

        internal override Collation ForVarcharStorage() => Latin1GeneralBin2ForVarchar;
    }

    /// <summary>
    /// CP1252-byte body for the binary collations when pinned on a
    /// <c>varchar</c> / <c>char</c> column. Encodes each operand to CP1252
    /// then byte-compares — matches real SQL Server's BIN / BIN2 varchar
    /// sort and equality. Diverges from <see cref="BinaryCollation"/>
    /// (UTF-16 codepoint compare) for any string containing characters
    /// whose CP1252 byte is in the 0x80-0x9F window: those bytes map to
    /// Unicode codepoints scattered across the BMP (`€` U+20AC = 0x80,
    /// `ƒ` U+0192 = 0x83, `Ÿ` U+0178 = 0x9F, …), so byte order doesn't
    /// equal codepoint order. <see cref="Name"/> is shared with the
    /// nvarchar-bodied sibling so catalog views report one collation
    /// name; <see cref="Resolve"/> treats them as the same
    /// collation for cross-operand coercibility.
    /// </summary>
    internal sealed class Cp1252BinaryCollation : Collation
    {
        private readonly string name;

        internal Cp1252BinaryCollation(string name)
        {
            this.name = name;
        }

        public override string Name => this.name;

        public override bool CaseSensitive => true;

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : CompareBytes(x, y);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && CompareBytes(x, y) == 0;

        public override int GetHashCode(string obj)
        {
            var bytes = CharSqlType.Cp1252Encoder.GetBytes(obj);
            var hash = default(HashCode);
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        private static int CompareBytes(string x, string y) =>
            CharSqlType.Cp1252Encoder.GetBytes(x).AsSpan().SequenceCompareTo(CharSqlType.Cp1252Encoder.GetBytes(y));
    }

    /// <summary>
    /// Generic culture-based collation: pins a <see cref="CompareInfo"/>
    /// and a <see cref="CompareOptions"/> set, routing
    /// <see cref="Compare"/> / <see cref="Equals"/> / <see cref="GetHashCode"/>
    /// through them. Sort options layer <see cref="CompareOptions.IgnoreSymbols"/>
    /// on top of the equality options, matching SQL Server's sort-only
    /// ignore-symbols treatment (the same asymmetry <see cref="WindowsCiAs"/>
    /// captures for Latin1). Used for the locale-specific collations that
    /// don't justify their own dedicated subclass yet.
    /// </summary>
    internal sealed class CultureCollation : Collation
    {
        private readonly string name;

        private readonly bool caseSensitive;

        private readonly CompareInfo compareInfo;

        private readonly CompareOptions equalityOptions;

        private readonly CompareOptions sortOptions;

        internal CultureCollation(string name, string cultureName, bool caseSensitive, bool kanaTypeSensitive = false, bool widthSensitive = false)
        {
            this.name = name;
            this.caseSensitive = caseSensitive;
            this.compareInfo = CultureInfo.GetCultureInfo(cultureName).CompareInfo;
            var baseOpts = caseSensitive
                ? CompareOptions.None
                : CompareOptions.IgnoreCase;
            // The _KS_ / _WS_ suffixes flip kanatype / width to *sensitive*.
            // Without them, SQL Server's "*_CI_AS" / "*_CS_AS" semantics ignore
            // both — probe-confirmed against SQL Server 2025: under
            // Latin1_General_CI_AS the full-width katakana ア (U+30A2),
            // hiragana あ (U+3042), and half-width katakana ｱ (U+FF71) all
            // compare equal; under Latin1_General_CI_AS_KS_WS the three
            // distinguish.
            if (!kanaTypeSensitive) baseOpts |= CompareOptions.IgnoreKanaType;
            if (!widthSensitive) baseOpts |= CompareOptions.IgnoreWidth;
            this.equalityOptions = baseOpts;
            this.sortOptions = baseOpts | CompareOptions.IgnoreSymbols;
        }

        public override string Name => this.name;

        public override bool CaseSensitive => this.caseSensitive;

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : this.compareInfo.Compare(x, y, this.sortOptions);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && this.compareInfo.Compare(x, y, this.equalityOptions) == 0;

        public override int GetHashCode(string obj) =>
            this.compareInfo.GetHashCode(obj, this.equalityOptions);
    }
}
