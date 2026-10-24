namespace SqlServerSimulator.Parser;

/// <summary>
/// The key range a <c>LIKE</c> pattern's literal prefix confines its matches
/// to — what lets an index on the subject column seek a <c>LIKE</c>, as real's
/// plan seeks one between <c>LikeRangeStart</c> and <c>LikeRangeEnd</c> of the
/// pattern. The range holds every subject the pattern can match, and others
/// besides, which the <c>LIKE</c> itself, kept in the residual <c>WHERE</c>,
/// rejects.
/// </summary>
/// <remarks>
/// The prefix read is the pattern's leading run of ASCII letters and digits,
/// or — under a collation whose order keeps them together across a run's
/// spaces and punctuation
/// (<see cref="Collation.SortsPrintableAsciiPrefixesTogether"/>) — its run of
/// printable ASCII up to its first wildcard, as names, codes and addresses
/// carry them, cut after the run's last letter or digit. Every subject
/// starting with the run, wherever a padded comparison or a weighted
/// <c>CHAR(0)</c> puts it relative to the run itself, sorts after the run
/// with that letter or digit stepped back (<c>Smith, J</c> →
/// <c>Smith, I</c>) and before it stepped on (<c>Smith, K</c>): at the
/// stepped position it carries the greater character, in code-point order
/// and in a collation's primary order alike. A space or a punctuation mark
/// is never stepped, since a collation's primary order places it apart from
/// its code point, so what follows the last letter or digit is left out of
/// the bounds.
/// </remarks>
internal static class LikePrefixRange
{
    /// <summary>
    /// The bounds of the range for <paramref name="pattern"/>, the run read
    /// through spaces and punctuation when <paramref name="throughPunctuation"/>
    /// is set, or false when it holds no letter or digit to seek on.
    /// <paramref name="upper"/> is null when no successor exists (no letter
    /// or digit in the run but <c>z</c>, <c>Z</c> and <c>9</c>), which leaves
    /// the range open above.
    /// </summary>
    public static bool TryGet(string pattern, char? escape, bool throughPunctuation, out string lower, out string? upper)
    {
        Span<char> run = stackalloc char[Math.Min(pattern.Length, 64)];
        var length = 0;
        var end = 0;
        for (var i = 0; i < pattern.Length && length < run.Length; i++)
        {
            var c = pattern[i];
            // An escaped character is a literal, wildcards included.
            if (c == escape)
            {
                if (++i == pattern.Length)
                    break;
                c = pattern[i];
            }
            else if (c is '%' or '_' or '[')
            {
                break;
            }
            var letterOrDigit = char.IsAsciiLetterOrDigit(c);
            if (c is < ' ' or > '~' || (!letterOrDigit && !throughPunctuation))
                break;
            run[length++] = c;
            if (letterOrDigit)
                end = length;
        }
        if (end == 0)
        {
            lower = "";
            upper = null;
            return false;
        }

        run = run[..end];
        lower = string.Concat(run[..^1], [(char)(run[^1] - 1)]);
        upper = null;
        for (var at = end; at > 0; at--)
        {
            var c = run[at - 1];
            if (!char.IsAsciiLetterOrDigit(c) || c is 'z' or 'Z' or '9')
                continue;
            run[at - 1] = (char)(c + 1);
            upper = new string(run[..at]);
            break;
        }
        return true;
    }
}
