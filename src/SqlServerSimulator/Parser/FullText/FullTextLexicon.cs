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
        ["be", "am", "is", "are", "was", "were", "been", "being", "aren't", "isn't", "wasn't", "weren't"],
        ["begin", "began", "begun", "beginning", "begins"],
        ["break", "broke", "broken", "breaking", "breaks"],
        ["bring", "brought", "bringing", "brings"],
        ["build", "built", "building", "builds"],
        ["buy", "bought", "buying", "buys"],
        ["catch", "caught", "catching", "catches"],
        ["child", "children"],
        ["choose", "chose", "chosen", "choosing", "chooses"],
        ["come", "came", "coming", "comes"],
        ["do", "does", "did", "done", "doing", "didn't", "doesn't", "don't"],
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
        ["have", "has", "had", "having", "hadn't", "hasn't", "haven't"],
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
        ["ax", "axe", "axes", "axed", "axing"],
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
    /// The irregular surface forms that are lemmas of their own as well, each
    /// mapped to that lemma, whose expansion real's lexicon unions with their
    /// irregular one: <c>saw</c> reaches <c>see</c> and <c>sawing</c>,
    /// <c>lives</c> both <c>life</c> and <c>living</c>, <c>building</c> both
    /// <c>build</c> and <c>buildings</c> — probe-anchored against
    /// <c>sys.dm_fts_parser</c> on SQL Server 2025 (2026-10-06), each listing
    /// its own plural, past or gerund. An inflection of one reaches that
    /// lemma alone, so <c>buildings</c> doesn't reach <c>build</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> SelfLemmas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["am"] = "am",
        ["analyses"] = "analyse",
        ["are"] = "are",
        ["bases"] = "base",
        ["beginning"] = "beginning",
        ["being"] = "being",
        ["breaking"] = "breaking",
        ["building"] = "building",
        ["calves"] = "calve",
        ["chose"] = "chose",
        ["coming"] = "coming",
        ["diagnoses"] = "diagnose",
        ["doing"] = "doing",
        ["drawing"] = "drawing",
        ["drove"] = "drove",
        ["falling"] = "falling",
        ["feeling"] = "feeling",
        ["fell"] = "fell",
        ["felt"] = "felt",
        ["finding"] = "finding",
        ["found"] = "found",
        ["given"] = "given",
        ["going"] = "going",
        ["halves"] = "halve",
        ["hearing"] = "hearing",
        ["holding"] = "holding",
        ["keeping"] = "keeping",
        ["known"] = "known",
        ["leaving"] = "leaving",
        ["left"] = "left",
        ["lives"] = "live",
        ["making"] = "making",
        ["meaning"] = "meaning",
        ["meeting"] = "meeting",
        ["met"] = "met",
        ["people"] = "people",
        ["ran"] = "ran",
        ["reading"] = "reading",
        ["sat"] = "sat",
        ["saw"] = "saw",
        ["saying"] = "saying",
        ["shelves"] = "shelve",
        ["singing"] = "singing",
        ["sitting"] = "sitting",
        ["spoke"] = "spoke",
        ["standing"] = "standing",
        ["taking"] = "taking",
        ["teaching"] = "teaching",
        ["thieves"] = "thieve",
        ["thought"] = "thought",
        ["understanding"] = "understanding",
        ["winning"] = "winning",
        ["writing"] = "writing",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The accented spellings real's English lexicon files under their plain
    /// lemma, so an inflectional search for either reaches the other and
    /// their inflections on an accent-sensitive catalog — <c>cafe</c> finds
    /// <c>café</c> and <c>cafés</c>, where <c>resume</c> doesn't find
    /// <c>résumé</c> (probed 2026-10-06 against SQL Server 2025, each pair
    /// confirmed by search).
    /// </summary>
    private static readonly FrozenDictionary<string, string> AccentedLemmas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["café"] = "cafe",
        ["château"] = "chateau",
        ["crêpe"] = "crepe",
        ["début"] = "debut",
        ["décor"] = "decor",
        ["dénouement"] = "denouement",
        ["détente"] = "detente",
        ["divorcée"] = "divorcee",
        ["éclair"] = "eclair",
        ["élite"] = "elite",
        ["entrée"] = "entree",
        ["exposé"] = "expose",
        ["façade"] = "facade",
        ["fête"] = "fete",
        ["jalapeño"] = "jalapeno",
        ["matinée"] = "matinee",
        ["naïve"] = "naive",
        ["plié"] = "plie",
        ["précis"] = "precis",
        ["première"] = "premiere",
        ["purée"] = "puree",
        ["régime"] = "regime",
        ["soirée"] = "soiree",
        ["vicuña"] = "vicuna",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every irregular surface form mapped to its lemma, so both the query term
    /// and the indexed term reduce to the same key — to both lemmas for a form
    /// two rows share (<c>leaves</c>: <c>leave</c> and <c>leaf</c>).
    /// </summary>
    private static readonly FrozenDictionary<string, (string Primary, string? Secondary)> IrregularLemmas = BuildIrregularLemmas();

    private static FrozenDictionary<string, (string Primary, string? Secondary)> BuildIrregularLemmas()
    {
        var map = new Dictionary<string, (string Primary, string? Secondary)>(StringComparer.Ordinal);
        foreach (var row in IrregularForms)
        {
            foreach (var form in row)
                map[form] = map.TryGetValue(form, out var earlier) && earlier.Primary != row[0] ? (earlier.Primary, row[0]) : (row[0], null);
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The key an inflectional match compares on — see <see cref="Stems"/>,
    /// whose primary key this is.
    /// </summary>
    public static string Stem(string term) => Stems(term).Primary;

    /// <summary>
    /// Reduces a term to the keys an inflectional match compares on: the
    /// irregular lemma when the term has one (two for a form two lemmas share,
    /// or for a form that is a lemma of its own as well), otherwise the term
    /// with its possessive and one regular suffix removed, an accented
    /// spelling the lexicon knows folded to its plain lemma. Both sides of a
    /// <c>FREETEXT</c> / <c>FORMSOF(INFLECTIONAL, …)</c> comparison stem
    /// through this and match when a key is shared, so <c>running</c> and
    /// <c>ran</c> both reach <c>run</c>, <c>mice</c> reaches <c>mouse</c>, and
    /// <c>saw</c> reaches <c>see</c> and <c>sawing</c> while those two don't
    /// reach each other.
    /// </summary>
    public static (string Primary, string? Secondary) Stems(string term)
    {
        var word = StripPossessive(term);
        if (IrregularLemmas.TryGetValue(word, out var lemmas))
            return lemmas.Secondary is null && SelfLemmas.TryGetValue(word, out var own) ? (lemmas.Primary, own) : lemmas;
        if (AccentedLemmas.TryGetValue(word, out var plain))
            return (plain, null);
        if (SibilantStems.Contains(word))
            return (word, null);
        var reduced = StripRegularSuffix(word);
        // A regular strip can land on an irregular surface form
        // (`children` → `children`, but `buildings` → `building`), so ask
        // again — unless that form is a lemma of its own, whose inflection
        // this is (`sawing` → `saw`, not `see`).
        if (SelfLemmas.TryGetValue(reduced, out var reducedOwn))
            return (reducedOwn, null);
        return IrregularLemmas.TryGetValue(reduced, out var reducedLemmas) ? reducedLemmas
            : AccentedLemmas.TryGetValue(reduced, out var reducedPlain) ? (reducedPlain, null)
            : (reduced, null);
    }

    /// <summary>Whether <paramref name="term"/> shares a key with <paramref name="key"/>'s.</summary>
    private static bool SharesKey((string Primary, string? Secondary) term, (string Primary, string? Secondary) key) =>
        term.Primary == key.Primary || term.Primary == key.Secondary
        || (term.Secondary is not null && (term.Secondary == key.Primary || term.Secondary == key.Secondary));

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
        var keys = Stems(word);
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        AddCandidates(candidates, keys.Primary);
        if (keys.Secondary is { } secondary)
            AddCandidates(candidates, secondary);
        List<string> forms = [];
        foreach (var form in candidates)
        {
            if (form != word && SharesKey(Stems(form), keys))
                forms.Add(form);
        }
        return forms;
    }

    /// <summary>The forms one key's paradigm offers: its irregular row, or the regular plural, past and gerund, with the two possessives.</summary>
    private static void AddCandidates(SortedSet<string> candidates, string key)
    {
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
    }

    /// <summary>
    /// True for a noise word an inflectional leaf still expands: a single
    /// letter (<c>x</c> → <c>x's</c>, <c>xs'</c> on real), where a function
    /// word such as <c>of</c> has no forms — which is why real's
    /// <c>FORMSOF(INFLECTIONAL, "vitamin b complex")</c> finds nothing and
    /// <c>FORMSOF(INFLECTIONAL, "word of mouth")</c> finds the phrase.
    /// </summary>
    public static bool ExpandsAsNoise(string term) => (term.Length == 1 && char.IsLetter(term[0])) || HasIrregularForms(term);

    /// <summary>
    /// True for a word the irregular table holds — a noise word such as
    /// <c>see</c> or <c>is</c> that real's lexicon still expands, so a search
    /// for it reaches its forms the stoplist keeps (<c>saw</c>, <c>seen</c>;
    /// probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    public static bool HasIrregularForms(string term) => IrregularLemmas.ContainsKey(StripPossessive(term));

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
        : stem.Length > 1 && stem[^1] == 's' && stem[^2] != 's' && !SibilantStems.Contains(stem) ? stem + "e"
        : stem;

    /// <summary>
    /// The stems ending in a single <c>s</c> that take <c>-ed</c> / <c>-ing</c>
    /// without a silent <c>e</c> (<c>focused</c>, <c>biased</c>), where real's
    /// lexicon otherwise restores one — <c>based</c> → <c>base</c>,
    /// <c>used</c> → <c>use</c>, <c>rinsed</c> → <c>rinse</c> (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static readonly FrozenSet<string> SibilantStems = FrozenSet.ToFrozenSet(
        ["alias", "atlas", "bias", "bonus", "canvas", "census", "chorus", "focus"], StringComparer.Ordinal);
}
