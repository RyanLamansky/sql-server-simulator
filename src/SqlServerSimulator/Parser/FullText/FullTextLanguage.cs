using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// One full-text language as the query pipeline uses it: which words its
/// system stoplist ignores, and whether its inflectional forms are modeled.
/// Every language breaks words with the English (LCID 1033) rules, which the
/// neutral (0) and British English (2057) breakers match term for term on
/// real; the other languages' breakers differ in details — their date, time and
/// number companions — that <c>docs/claude/full-text.md</c> records.
/// </summary>
internal sealed class FullTextLanguage
{
    public readonly int Lcid;

    /// <summary>
    /// True for the languages whose inflectional forms the simulator models:
    /// English and the two that share its breaker and stemmer on real, neutral
    /// and British English. Real's other languages carry their own
    /// morphologies (German <c>Haus</c> reaches <c>Häuser</c>, Spanish
    /// <c>hablar</c> some 270 conjugated and enclitic forms), which aren't
    /// built, so their inflectional searches match the written form only.
    /// </summary>
    public bool EnglishMorphology => this.Lcid is 0 or 1033 or 2057;

    /// <summary>The system stoplist, folded to lower case.</summary>
    private readonly FrozenSet<string> stopwords;

    private FullTextLanguage(int lcid, FrozenSet<string> stopwords)
    {
        this.Lcid = lcid;
        this.stopwords = stopwords;
    }

    /// <summary>
    /// The system stoplist rows, verbatim from real's
    /// <c>sys.fulltext_system_stopwords</c> (probed 2026-09-29 against SQL
    /// Server 2025): 15,829 words across the 46 languages that have any, kept
    /// in the embedded <c>SystemStopwords.tsv</c> as <c>lcid</c>, tab, word.
    /// </summary>
    public static readonly (int Lcid, string Stopword)[] SystemStopwords = LoadSystemStopwords();

    private static readonly FrozenDictionary<int, FullTextLanguage> ByLcid = BuildLanguages();

    /// <summary>English, the language every unknown LCID falls back to.</summary>
    public static readonly FullTextLanguage English = ByLcid[1033];

    /// <summary>The language for <paramref name="lcid"/>, English when real ships no such language.</summary>
    public static FullTextLanguage For(int lcid) => ByLcid.TryGetValue(lcid, out var language) ? language : English;

    /// <summary>True when <paramref name="lcid"/> is one of the languages <c>sys.fulltext_languages</c> lists.</summary>
    public static bool IsKnown(int lcid) => ByLcid.ContainsKey(lcid);

    /// <summary>
    /// True when real reports <paramref name="term"/> as a noise word under
    /// this language's system stoplist: a stopword, or the <c>nn</c> companion
    /// of a number that is one (<c>nn5</c> beside a stopword <c>5</c>).
    /// </summary>
    public bool IsNoise(string term) =>
        this.stopwords.Contains(term)
        || (term.Length == 3 && term[0] == 'n' && term[1] == 'n' && char.IsAsciiDigit(term[2]) && this.stopwords.Contains(term[2..]));

    /// <summary>
    /// Resolves a <c>LANGUAGE</c> argument: an LCID (an integer, or a binary
    /// such as <c>0x407</c>), or a name or alias from <c>sys.syslanguages</c>
    /// (<c>'German'</c>, <c>'Deutsch'</c>). An LCID real ships no full-text
    /// language for is Msg 7696 and an unknown name Msg 7678, both probed
    /// 2026-09-29 against SQL Server 2025.
    /// </summary>
    public static FullTextLanguage Resolve(SqlValue value) =>
        SqlType.IsStringCategory(value.Type)
            ? For(ResolveName(value.AsString))
            : IsKnown(value.CoerceTo(SqlType.Int32).AsInt32) ? For(value.CoerceTo(SqlType.Int32).AsInt32)
            : throw SimulatedSqlException.FullTextInvalidLocale();

    /// <summary>The LCID a language name or alias names; see <see cref="Resolve"/>.</summary>
    public static int ResolveName(string name)
    {
        var trimmed = name.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var lcid))
            return IsKnown(lcid) ? lcid : throw SimulatedSqlException.FullTextInvalidLocale();
        var language = Language.Find(trimmed) ?? throw SimulatedSqlException.FullTextLanguageAliasUnknown(trimmed);
        return IsKnown(language.Lcid) ? language.Lcid : throw SimulatedSqlException.FullTextInvalidLocale();
    }

    private static (int Lcid, string Stopword)[] LoadSystemStopwords()
    {
        using var stream = typeof(FullTextLanguage).Assembly.GetManifestResourceStream("SqlServerSimulator.FullText.SystemStopwords.tsv")
            ?? throw new InvalidOperationException("The full-text stopword resource is missing.");
        using var reader = new StreamReader(stream);
        List<(int, string)> rows = [];
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            rows.Add((int.Parse(line.AsSpan(0, tab), System.Globalization.CultureInfo.InvariantCulture), line[(tab + 1)..]));
        }
        return [.. rows];
    }

    private static FrozenDictionary<int, FullTextLanguage> BuildLanguages()
    {
        var words = new Dictionary<int, HashSet<string>>();
        foreach (var (lcid, _) in BuiltInResources.FullTextLanguages)
            words[lcid] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (lcid, stopword) in SystemStopwords)
        {
            // Stored folded to lower case, because every lookup arrives
            // case-folded by the word breaker; real lists some capitals
            // (English's single letters) that match either way.
#pragma warning disable CA1308
            _ = words[lcid].Add(stopword.ToLowerInvariant());
#pragma warning restore CA1308
        }
        var languages = new Dictionary<int, FullTextLanguage>();
        foreach (var (lcid, set) in words)
            languages[lcid] = new FullTextLanguage(lcid, set.ToFrozenSet(StringComparer.Ordinal));
        return languages.ToFrozenDictionary();
    }
}
