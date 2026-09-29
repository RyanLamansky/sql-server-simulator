using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// The English morphology the query pipeline reads: the accent fold an
/// accent-insensitive catalog applies, and the inflectional stemmer
/// <c>FREETEXT</c> / <c>FORMSOF(INFLECTIONAL, …)</c> expand through. The
/// stoplists live with <see cref="FullTextLanguage"/>.
/// </summary>
internal static class FullTextLexicon
{
    /// <summary>
    /// Strips diacritics the way an accent-<i>insensitive</i> catalog folds
    /// them — <c>café</c> → <c>cafe</c>, <c>ÄÖÜ</c> → <c>aou</c>. Decomposes to
    /// NFD and drops the combining marks, after the compatibility expansions
    /// that have no combining form (<c>ß</c> → <c>ss</c>, <c>æ</c> → <c>ae</c>,
    /// <c>ø</c> → <c>o</c>, <c>đ</c> → <c>d</c>), which real folds the same way.
    /// A catalog created accent-<i>sensitive</i> (the default) never calls this,
    /// so <c>café</c> and <c>cafe</c> stay distinct terms.
    /// </summary>
    public static string FoldAccents(string term)
    {
        var expanded = ExpandNonDecomposing(term);
        var decomposed = expanded.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var baseFolds = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                // Only the European scripts' marks are accents to real: a
                // Devanagari virama or a kana voicing mark stays.
                baseFolds = ch is < '\u0530' or (>= '\u1E00' and < '\u2000');
                _ = builder.Append(ch);
            }
            else if (!baseFolds)
            {
                _ = builder.Append(ch);
            }
        }
        // A capital whose lower case decomposes (`İ`) folds to lower case only
        // once its mark is gone.
#pragma warning disable CA1308
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>
    /// Handles the letters whose accent-free form isn't reachable by dropping
    /// combining marks, because NFD leaves them atomic.
    /// </summary>
    private static string ExpandNonDecomposing(string term)
    {
        var needsExpansion = false;
        foreach (var ch in term)
        {
            if (ch is 'ß' or 'æ' or 'Æ' or 'ø' or 'Ø' or 'đ' or 'Đ' or 'ð' or 'Ð' or 'þ' or 'Þ' or 'ł' or 'Ł' or 'œ' or 'Œ' or 'ı' or 'ŉ')
            {
                needsExpansion = true;
                break;
            }
        }
        if (!needsExpansion)
            return term;

        var builder = new StringBuilder(term.Length + 2);
        foreach (var ch in term)
        {
            var replacement = ch switch
            {
                'ß' => "ss",
                'æ' or 'Æ' => "ae",
                'œ' or 'Œ' => "oe",
                'ø' or 'Ø' => "o",
                'đ' or 'Đ' or 'ð' or 'Ð' => "d",
                'þ' or 'Þ' => "th",
                'ł' or 'Ł' => "l",
                'ı' => "i",
                'ŉ' => "n",
                _ => null,
            };
            _ = replacement is null ? builder.Append(ch) : builder.Append(replacement);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Irregular English forms that no suffix rule reaches, each row listing
    /// every surface form of one lemma with the lemma first. Probe-anchored
    /// against <c>sys.dm_fts_parser(N'FORMSOF(INFLECTIONAL, …)', 1033, 0, 0)</c>
    /// on SQL Server 2025 — real's own expansion of <c>mouse</c> reaches
    /// <c>mice</c>, of <c>geese</c> reaches <c>goose</c>, and of <c>run</c>
    /// reaches <c>ran</c>. Real's lexicon covers the whole language; this table
    /// covers the forms a search over ordinary prose is likely to need.
    /// </summary>
    private static readonly string[][] IrregularForms =
    [
        ["be", "am", "is", "are", "was", "were", "been", "being"],
        ["begin", "began", "begun", "beginning", "begins"],
        ["break", "broke", "broken", "breaking", "breaks"],
        ["bring", "brought", "bringing", "brings"],
        ["build", "built", "building", "builds"],
        ["buy", "bought", "buying", "buys"],
        ["catch", "caught", "catching", "catches"],
        ["child", "children"],
        ["choose", "chose", "chosen", "choosing", "chooses"],
        ["come", "came", "coming", "comes"],
        ["do", "does", "did", "done", "doing"],
        ["draw", "drew", "drawn", "drawing", "draws"],
        ["drive", "drove", "driven", "driving", "drives"],
        ["eat", "ate", "eaten", "eating", "eats"],
        ["fall", "fell", "fallen", "falling", "falls"],
        ["feel", "felt", "feeling", "feels"],
        ["find", "found", "finding", "finds"],
        ["foot", "feet"],
        ["forget", "forgot", "forgotten", "forgetting", "forgets"],
        ["get", "got", "gotten", "getting", "gets"],
        ["give", "gave", "given", "giving", "gives"],
        ["go", "went", "gone", "going", "goes"],
        ["goose", "geese"],
        ["grow", "grew", "grown", "growing", "grows"],
        ["have", "has", "had", "having"],
        ["hear", "heard", "hearing", "hears"],
        ["hold", "held", "holding", "holds"],
        ["keep", "kept", "keeping", "keeps"],
        ["know", "knew", "known", "knowing", "knows"],
        ["leave", "left", "leaving", "leaves"],
        ["lose", "lost", "losing", "loses"],
        ["make", "made", "making", "makes"],
        ["man", "men"],
        ["mean", "meant", "meaning", "means"],
        ["meet", "met", "meeting", "meets"],
        ["mouse", "mice"],
        ["pay", "paid", "paying", "pays"],
        ["person", "people"],
        ["read", "reading", "reads"],
        ["run", "ran", "running", "runs"],
        ["say", "said", "saying", "says"],
        ["see", "saw", "seen", "seeing", "sees"],
        ["sell", "sold", "selling", "sells"],
        ["send", "sent", "sending", "sends"],
        ["sing", "sang", "sung", "singing", "sings"],
        ["sit", "sat", "sitting", "sits"],
        ["speak", "spoke", "spoken", "speaking", "speaks"],
        ["stand", "stood", "standing", "stands"],
        ["swim", "swam", "swum", "swimming", "swims"],
        ["take", "took", "taken", "taking", "takes"],
        ["teach", "taught", "teaching", "teaches"],
        ["tell", "told", "telling", "tells"],
        ["think", "thought", "thinking", "thinks"],
        ["tooth", "teeth"],
        ["understand", "understood", "understanding", "understands"],
        ["wear", "wore", "worn", "wearing", "wears"],
        ["win", "won", "winning", "wins"],
        ["woman", "women"],
        ["write", "wrote", "written", "writing", "writes"],
        // Latin / Greek plurals and the -f / -fe family, which no suffix rule
        // reaches. Real's lexicon relates each pair; probing `FREETEXT` one
        // word per row is what named these.
        ["analysis", "analyses"],
        ["appendix", "appendices"],
        ["axis", "axes"],
        ["basis", "bases"],
        ["bus", "buses", "bused", "busing", "busses"],
        ["calf", "calves"],
        ["criterion", "criteria"],
        ["crisis", "crises"],
        ["datum", "data"],
        ["diagnosis", "diagnoses"],
        ["half", "halves"],
        ["hypothesis", "hypotheses"],
        ["index", "indices", "indexes"],
        ["knife", "knives"],
        ["leaf", "leaves"],
        ["life", "lives"],
        ["loaf", "loaves"],
        ["matrix", "matrices"],
        ["ox", "oxen"],
        ["phenomenon", "phenomena"],
        ["self", "selves"],
        ["shelf", "shelves"],
        ["thesis", "theses"],
        ["thief", "thieves"],
        ["vertex", "vertices"],
        ["wife", "wives"],
        ["wolf", "wolves"],
    ];

    /// <summary>
    /// Every irregular surface form mapped to its lemma, so both the query term
    /// and the indexed term reduce to the same key.
    /// </summary>
    private static readonly FrozenDictionary<string, string> IrregularLemmas = BuildIrregularLemmas();

    private static FrozenDictionary<string, string> BuildIrregularLemmas()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in IrregularForms)
        {
            foreach (var form in row)
                map[form] = row[0];
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reduces a term to the key an inflectional match compares on: the
    /// irregular lemma when the term has one, otherwise the term with its
    /// possessive and one regular suffix removed. Both sides of a
    /// <c>FREETEXT</c> / <c>FORMSOF(INFLECTIONAL, …)</c> comparison stem through
    /// this, so <c>running</c> and <c>ran</c> both reach <c>run</c> and
    /// <c>mice</c> reaches <c>mouse</c>.
    /// </summary>
    public static string Stem(string term)
    {
        var word = StripPossessive(term);
        if (IrregularLemmas.TryGetValue(word, out var lemma))
            return lemma;
        var reduced = StripRegularSuffix(word);
        // A regular strip can land on an irregular surface form
        // (`children` → `children`, but `leaves` → `leave`), so ask again.
        return IrregularLemmas.TryGetValue(reduced, out var reducedLemma) ? reducedLemma : reduced;
    }

    /// <summary>
    /// The inflectional forms <c>sys.dm_fts_parser</c> lists for a word under
    /// <c>FORMSOF(INFLECTIONAL, …)</c>, ordinal-sorted and without the word
    /// itself: the irregular row's forms, or the regular noun and verb
    /// paradigm — plural, past, gerund — with the two possessives, kept only
    /// where <see cref="Stem"/> maps the form back to the word's own key, so
    /// the list is exactly what an inflectional search here matches. Real
    /// consults a part-of-speech lexicon and lists only the paradigms a word
    /// has (<c>quick</c> gets no verb forms), which this doesn't model.
    /// </summary>
    public static List<string> InflectionalForms(string word)
    {
        foreach (var ch in word)
        {
            if (!char.IsLetter(ch) && ch != '\'')
                return [];
        }
        var key = Stem(word);
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        var irregular = false;
        foreach (var row in IrregularForms)
        {
            if (row[0] != key)
                continue;
            irregular = true;
            foreach (var form in row)
                _ = candidates.Add(form);
            break;
        }
        if (!irregular)
        {
            _ = candidates.Add(key);
            _ = candidates.Add(Plural(key));
            _ = candidates.Add(Past(key));
            _ = candidates.Add(Gerund(key));
        }
        _ = candidates.Add(key + "'s");
        _ = candidates.Add(Plural(key) + "'");
        List<string> forms = [];
        foreach (var form in candidates)
        {
            if (form != word && Stem(form) == key)
                forms.Add(form);
        }
        return forms;
    }

    /// <summary>
    /// True for a noise word an inflectional leaf still expands: a single
    /// letter (<c>x</c> → <c>x's</c>, <c>xs'</c> on real), where a function
    /// word such as <c>of</c> has no forms — which is why real's
    /// <c>FORMSOF(INFLECTIONAL, "vitamin b complex")</c> finds nothing and
    /// <c>FORMSOF(INFLECTIONAL, "word of mouth")</c> finds the phrase.
    /// </summary>
    public static bool ExpandsAsNoise(string term) => term.Length == 1 && char.IsLetter(term[0]);

    private static bool IsVowel(char ch) => ch is 'a' or 'e' or 'i' or 'o' or 'u';

    private static bool EndsConsonantVowelConsonant(string word) =>
        word.Length == 3 && !IsVowel(word[0]) && IsVowel(word[1]) && !IsVowel(word[2]) && word[2] is not ('w' or 'x' or 'y');

    private static string Plural(string word) =>
        word.EndsWith('s') || word.EndsWith('x') || word.EndsWith('z') || word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal)
            ? word + "es"
        : word.Length > 1 && word[^1] == 'y' && !IsVowel(word[^2]) ? word[..^1] + "ies"
        : word + "s";

    private static string Past(string word) =>
        word.EndsWith('e') ? word + "d"
        : word.Length > 1 && word[^1] == 'y' && !IsVowel(word[^2]) ? word[..^1] + "ied"
        : EndsConsonantVowelConsonant(word) ? word + word[^1] + "ed"
        : word + "ed";

    private static string Gerund(string word) =>
        word.EndsWith("ie", StringComparison.Ordinal) ? word[..^2] + "ying"
        : word.Length > 2 && word.EndsWith('e') && !word.EndsWith("ee", StringComparison.Ordinal) && !word.EndsWith("ye", StringComparison.Ordinal) && !word.EndsWith("oe", StringComparison.Ordinal)
            ? word[..^1] + "ing"
        : EndsConsonantVowelConsonant(word) ? word + word[^1] + "ing"
        : word + "ing";

    /// <summary>
    /// Drops a trailing <c>'s</c> or bare <c>'</c> — the possessive forms real's
    /// expansion emits (<c>run's</c>, <c>runs'</c>) and the word breaker keeps
    /// as part of the token because an interior apostrophe joins.
    /// </summary>
    private static string StripPossessive(string word) =>
        word.EndsWith("'s", StringComparison.Ordinal) ? word[..^2]
        : word.Length > 1 && word[^1] == '\'' ? word[..^1]
        : word;

    /// <summary>
    /// One pass of the regular English suffix rules: the consonant-<c>y</c>
    /// pair <c>-ies</c> / <c>-ied</c>, then plural <c>-es</c> / <c>-s</c>, then
    /// verbal <c>-ing</c> / <c>-ed</c> with the doubled-consonant undo and the
    /// silent-<c>e</c> restore. A word too short to carry the suffix keeps it,
    /// which is what stops <c>is</c> and <c>bed</c> from being stripped to
    /// nothing.
    /// </summary>
    private static string StripRegularSuffix(string word) =>
        word.Length > 3 && (word.EndsWith("ies", StringComparison.Ordinal) || word.EndsWith("ied", StringComparison.Ordinal))
            ? string.Concat(word.AsSpan(0, word.Length - 3), "y")
        : word.Length > 4 && (word.EndsWith("sses", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal)
            || word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal)
            || word.EndsWith("zes", StringComparison.Ordinal))
            ? word[..^2]
        : word.Length > 3 && word.EndsWith("es", StringComparison.Ordinal) && word[^3] is 'o' or 'i'
            ? word[..^2]
        : word.Length > 3 && word.EndsWith('s') && word[^2] != 's' && word[^2] != 'u'
            ? word[..^1]
        : word.Length > 4 && word.EndsWith("ing", StringComparison.Ordinal)
            ? RestoreVerbStem(word[..^3])
        : word.Length > 3 && word.EndsWith("ed", StringComparison.Ordinal)
            ? RestoreVerbStem(word[..^2])
        : word;

    /// <summary>
    /// Undoes the spelling changes an <c>-ing</c> / <c>-ed</c> suffix triggers:
    /// a doubled final consonant collapses (<c>running</c> → <c>run</c>) and a
    /// stem left ending in a consonant cluster that needs its silent <c>e</c>
    /// back gets it (<c>moving</c> → <c>move</c>).
    /// </summary>
    private static string RestoreVerbStem(string stem) =>
        stem.Length > 2 && stem[^1] == stem[^2] && stem[^1] is not ('l' or 's' or 'f' or 'z') ? stem[..^1]
        : stem.Length > 2 && stem[^1] is 'v' or 'c' or 'g' or 'u' ? stem + "e"
        : stem;
}
