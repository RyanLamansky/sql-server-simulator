namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// One broken word and the 1-based ordinal it occupies in the document. A
/// compound's own composite term shares the position of its first part, so
/// <c>red-hot chili</c> breaks to <c>red-hot</c>@1, <c>red</c>@1, <c>hot</c>@2,
/// <c>chili</c>@3 — real's numbering, visible in
/// <c>sys.dm_fts_parser</c>'s <c>occurrence</c> column.
/// </summary>
internal readonly struct FullTextTerm(string text, int position)
{
    public readonly string Text = text;
    public readonly int Position = position;
}

/// <summary>
/// The word breaker the query pipeline runs over both the indexed column values
/// and the search condition's own terms. Models the English (LCID 1033) breaker
/// as a small rule set rather than real's language component; the rules and the
/// places they part company with real are catalogued in
/// <c>docs/claude/full-text.md</c>.
/// </summary>
/// <remarks>
/// <para>Modeled rules, each probe-anchored against SQL Server 2025:</para>
/// <list type="bullet">
/// <item><description>A word is a maximal run of Unicode letters and digits.</description></item>
/// <item><description>An <b>interior apostrophe joins</b>: <c>O'Brien</c>,
/// <c>don't</c> and <c>rock'n'roll</c> are each one term, so <c>obrien</c>
/// matches nothing.</description></item>
/// <item><description>An <b>interior hyphen or underscore compounds</b>: the
/// whole run is emitted as one term and each part is emitted too, so
/// <c>red-hot</c> is findable as <c>red-hot</c>, as <c>red</c>, and as
/// <c>hot</c>, and <c>under_score</c> is findable as <c>score</c>.</description></item>
/// <item><description>An <b>interior period or comma between two digits
/// joins</b>, keeping <c>3.14</c> and <c>1,000</c> whole; elsewhere both
/// break, so <c>a.b.c</c> is three terms.</description></item>
/// <item><description>Terms fold to lower case — matching is case-insensitive
/// whatever the column's collation says, which real does too (probed against a
/// <c>Latin1_General_CS_AS</c> column).</description></item>
/// <item><description>Terms fold their accents only when the catalog was
/// created <c>WITH ACCENT_SENSITIVITY = OFF</c>; the default is ON, where
/// <c>café</c> and <c>cafe</c> stay distinct.</description></item>
/// </list>
/// </remarks>
internal static class FullTextWordBreaker
{
    /// <summary>
    /// Breaks <paramref name="text"/> into positioned terms. Stopwords are
    /// included: they occupy a position in real's index, which is what makes
    /// <c>"over the lazy dog"</c> match while <c>"jumps over lazy"</c> does not,
    /// and what makes <c>NEAR</c>'s distance count them.
    /// </summary>
    public static List<FullTextTerm> Break(string text, bool accentSensitive)
    {
        var terms = new List<FullTextTerm>();
        var position = 1;
        var index = 0;
        while (index < text.Length)
        {
            if (!IsWordCharacter(text[index]))
            {
                index++;
                continue;
            }
            var start = index;
            index++;
            while (index < text.Length)
            {
                if (IsWordCharacter(text[index]))
                {
                    index++;
                }
                else if (JoinsInterior(text, index))
                {
                    index++;
                }
                else
                {
                    break;
                }
            }
            AppendRun(terms, text.AsSpan(start, index - start), accentSensitive, ref position);
        }
        return terms;
    }

    /// <summary>
    /// Emits one compound run: when it carries a hyphen or underscore, the
    /// composite lands first (sharing the first part's position) and every part
    /// follows at its own position; otherwise the run is a single term.
    /// </summary>
    private static void AppendRun(List<FullTextTerm> terms, ReadOnlySpan<char> run, bool accentSensitive, ref int position)
    {
        var hasSplitter = false;
        foreach (var ch in run)
        {
            if (ch is '-' or '_')
            {
                hasSplitter = true;
                break;
            }
        }

        if (!hasSplitter)
        {
            terms.Add(new FullTextTerm(Normalize(run, accentSensitive), position));
            position++;
            return;
        }

        terms.Add(new FullTextTerm(Normalize(run, accentSensitive), position));
        var partStart = 0;
        for (var i = 0; i <= run.Length; i++)
        {
            if (i != run.Length && run[i] is not ('-' or '_'))
                continue;
            if (i > partStart)
            {
                terms.Add(new FullTextTerm(Normalize(run[partStart..i], accentSensitive), position));
                position++;
            }
            partStart = i + 1;
        }
    }

    /// <summary>
    /// Applies the two folds every term goes through — case always, accents
    /// only for an accent-insensitive catalog.
    /// </summary>
    public static string Normalize(ReadOnlySpan<char> term, bool accentSensitive)
    {
        // Lower case, not CA1308's preferred upper: real's own normal form for
        // a full-text term is lower (sys.dm_fts_parser reports every
        // display_term that way), and the stoplist is stored to match.
#pragma warning disable CA1308
        var lowered = term.ToString().ToLowerInvariant();
#pragma warning restore CA1308
        return accentSensitive ? lowered : FullTextLexicon.FoldAccents(lowered);
    }

    private static bool IsWordCharacter(char ch) => char.IsLetterOrDigit(ch);

    /// <summary>
    /// True when the character at <paramref name="index"/> is one of the
    /// joiners and sits between two characters that keep the run going.
    /// </summary>
    private static bool JoinsInterior(string text, int index)
    {
        if (index == 0 || index + 1 >= text.Length)
            return false;
        var previous = text[index - 1];
        var next = text[index + 1];
        return text[index] switch
        {
            '\'' or '-' or '_' => IsWordCharacter(previous) && IsWordCharacter(next),
            '.' or ',' => char.IsDigit(previous) && char.IsDigit(next),
            _ => false,
        };
    }
}
