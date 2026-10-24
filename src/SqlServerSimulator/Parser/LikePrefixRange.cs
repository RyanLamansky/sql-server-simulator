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
/// Only the pattern's leading run of ASCII letters and digits is read, and
/// only under a collation that orders such runs as
/// <see cref="Collation.SortsAsciiPrefixesTogether"/> describes. Every subject
/// starting with the run, wherever a padded comparison or a weighted
/// <c>CHAR(0)</c> puts it relative to the run itself, sorts after the run
/// with its last character stepped back (<c>Sm</c> → <c>Sl</c>) and before
/// the run's successor (<c>Sn</c>): at the stepped position it carries the
/// greater character, in code-point order and in a collation's primary order
/// alike.
/// </remarks>
internal static class LikePrefixRange
{
    /// <summary>
    /// The bounds of the range for <paramref name="pattern"/>, or false when
    /// it starts with no letter or digit to seek on. <paramref name="upper"/>
    /// is null when no successor exists (a run of <c>z</c>, <c>Z</c> and
    /// <c>9</c> alone), which leaves the range open above.
    /// </summary>
    public static bool TryGet(string pattern, char? escape, out string lower, out string? upper)
    {
        Span<char> run = stackalloc char[Math.Min(pattern.Length, 64)];
        var length = 0;
        for (var i = 0; i < pattern.Length && length < run.Length; i++)
        {
            var c = pattern[i];
            // An escaped character is a literal, wildcards included; one
            // that isn't a letter or digit ends the run like any other.
            if (c == escape && ++i < pattern.Length)
                c = pattern[i];
            else if (c == escape)
                break;
            if (!char.IsAsciiLetterOrDigit(c))
                break;
            run[length++] = c;
        }
        if (length == 0)
        {
            lower = "";
            upper = null;
            return false;
        }

        run = run[..length];
        var last = run[^1];
        lower = string.Concat(run[..^1], [(char)(last - 1)]);
        upper = null;
        for (var end = length; end > 0; end--)
        {
            var c = run[end - 1];
            if (c is 'z' or 'Z' or '9')
                continue;
            run[end - 1] = (char)(c + 1);
            upper = new string(run[..end]);
            break;
        }
        return true;
    }
}
