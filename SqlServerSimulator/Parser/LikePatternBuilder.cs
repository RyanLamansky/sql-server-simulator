using System.Text;
using System.Text.RegularExpressions;

namespace SqlServerSimulator.Parser;

/// <summary>
/// SQL Server pattern-translation primitives shared by <c>LIKE</c> /
/// <c>NOT LIKE</c> (full-string match, anchored at both ends with trailing-
/// space slack) and <c>PATINDEX</c> (find-first-position, anchoring decided
/// by leading / trailing <c>%</c>). Single source of truth for the wildcard
/// (<c>%</c>, <c>_</c>), character-class (<c>[abc]</c>, <c>[^abc]</c>,
/// <c>[a-z]</c>), and escape-clause translation so a fix to one site fixes
/// the other.
/// </summary>
internal static class LikePatternBuilder
{
    private const RegexOptions BaseOptions = RegexOptions.CultureInvariant | RegexOptions.Singleline;

    /// <summary>
    /// Builds the fully-anchored regex used by <c>LIKE</c>: <c>^body[ ]*\z</c>.
    /// The trailing <c>[ ]*</c> slack mirrors SQL Server's documented LIKE
    /// behavior where the subject's leftover U+0020 spaces are accepted even
    /// when the pattern doesn't end with <c>%</c>. <c>\z</c> rather than
    /// <c>$</c> so a final <c>\n</c> in the subject isn't silently allowed
    /// outside Multiline mode. <paramref name="caseSensitive"/> is sourced
    /// from the resolved <see cref="Collation"/> (default + every <c>_CI_</c>
    /// entry on <see cref="Collation.Recognized"/> is case-insensitive;
    /// <c>_CS_</c> and <c>_BIN</c> are case-sensitive).
    /// </summary>
    public static Regex BuildAnchored(string pattern, char? escapeChar, bool caseSensitive)
    {
        var sb = new StringBuilder(pattern.Length + 8);
        _ = sb.Append('^');
        AppendPatternBody(pattern, escapeChar, sb);
        _ = sb.Append("[ ]*\\z");
        return new Regex(sb.ToString(), caseSensitive ? BaseOptions : BaseOptions | RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Builds the regex used by <c>PATINDEX</c>. Anchoring is decided by the
    /// pattern itself: a leading <c>%</c> means "find anywhere" (no <c>^</c>),
    /// and a trailing <c>%</c> means "any tail" (no <c>\z</c>). The wildcard
    /// translation otherwise matches LIKE exactly. Probe-confirmed:
    /// <list type="bullet">
    /// <item><c>PATINDEX('abc', 'abcx') = 0</c> — no leading <c>%</c>, no trailing <c>%</c>: anchored both ends; <c>abc</c> isn't equal to <c>abcx</c>.</item>
    /// <item><c>PATINDEX('abc%', 'abcx') = 1</c> — trailing <c>%</c> drops the end anchor; subject starts with <c>abc</c> at position 1.</item>
    /// <item><c>PATINDEX('%abc', 'xabc') = 2</c> — leading <c>%</c> drops the start anchor; the <c>abc</c> match starts at position 2.</item>
    /// <item><c>PATINDEX('%abc%', 'xabcx') = 2</c> — both ends loose; first match of <c>abc</c> starts at position 2.</item>
    /// </list>
    /// PATINDEX has no <c>ESCAPE</c> clause (Msg 156 at parse), so <paramref name="escapeChar"/>
    /// is always <see langword="null"/> from the PATINDEX call site; the
    /// parameter is kept for symmetry with the anchored form. No trailing-
    /// space slack is added — PATINDEX doesn't get the ANSI-padding benefit
    /// that LIKE does (probe-confirmed for nvarchar; varchar's match still
    /// works for trailing-space subjects because the pre-stored value gets
    /// the trailing whitespace clipped via the varchar collation rules
    /// already in effect at the call site).
    /// </summary>
    public static Regex BuildForPatIndex(string pattern, char? escapeChar = null)
    {
        var startsWithPercent = StartsWithUnescapedPercent(pattern, escapeChar);
        var endsWithPercent = EndsWithUnescapedPercent(pattern, escapeChar);

        // Leading and trailing % are consumed by the anchoring decision —
        // they do NOT contribute to the regex body. Without this stripping,
        // a pattern like `%abc%` would translate to `.*abc.*` and an
        // unanchored match would index 0 (the `.*` consuming the empty
        // prefix), losing the probe-confirmed semantic that the returned
        // position should point at the non-% content's start in the subject.
        var bodyStart = startsWithPercent ? 1 : 0;
        var bodyEnd = endsWithPercent && pattern.Length > bodyStart ? pattern.Length - 1 : pattern.Length;
        var body = pattern[bodyStart..bodyEnd];

        var sb = new StringBuilder(body.Length + 4);
        if (!startsWithPercent)
            _ = sb.Append('^');
        AppendPatternBody(body, escapeChar, sb);
        if (!endsWithPercent)
            _ = sb.Append("\\z");
        return new Regex(sb.ToString(), BaseOptions | RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Appends the regex translation of a SQL Server LIKE-style pattern body
    /// to <paramref name="sb"/>, without any anchors.
    /// <see cref="RegexOptions.Singleline"/> makes <c>.</c> (the translation
    /// for <c>_</c>) and <c>.*</c> (for <c>%</c>) match newlines.
    /// </summary>
    private static void AppendPatternBody(string pattern, char? escapeChar, StringBuilder sb)
    {
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (escapeChar.HasValue && c == escapeChar.Value)
            {
                if (i + 1 < pattern.Length)
                {
                    _ = sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                    i += 2;
                }
                else
                {
                    _ = sb.Append(Regex.Escape(c.ToString()));
                    i++;
                }
                continue;
            }

            switch (c)
            {
                case '%':
                    _ = sb.Append(".*");
                    i++;
                    break;
                case '_':
                    _ = sb.Append('.');
                    i++;
                    break;
                case '[':
                    i = TranslateClass(pattern, i, sb);
                    break;
                default:
                    _ = sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// Translates a single <c>[...]</c> character class starting at
    /// <paramref name="start"/> (the position of <c>[</c>). Returns the
    /// position after the closing <c>]</c>. Three never-match cases emit
    /// <c>(?!)</c> (always-fails lookahead): unterminated <c>[</c>, empty
    /// <c>[]</c>, and reversed ranges (<c>[c-a]</c>) — all confirmed by
    /// real-server probing. The <c>[^]</c> any-char form translates to
    /// <c>.</c> because .NET regex doesn't accept <c>[^]</c> directly.
    /// </summary>
    private static int TranslateClass(string pattern, int start, StringBuilder sb)
    {
        var contentStart = start + 1;
        var end = pattern.IndexOf(']', contentStart);
        if (end < 0)
        {
            _ = sb.Append("(?!)");
            return pattern.Length;
        }

        var content = pattern.AsSpan(contentStart, end - contentStart);
        if (content.IsEmpty)
        {
            _ = sb.Append("(?!)");
            return end + 1;
        }
        if (content is "^")
        {
            _ = sb.Append('.');
            return end + 1;
        }

        for (var j = 1; j < content.Length - 1; j++)
        {
            if (content[j] == '-' && content[j - 1] > content[j + 1])
            {
                _ = sb.Append("(?!)");
                return end + 1;
            }
        }

        _ = sb.Append('[');
        foreach (var cc in content)
        {
            if (cc is '\\' or ']')
                _ = sb.Append('\\');
            _ = sb.Append(cc);
        }
        _ = sb.Append(']');
        return end + 1;
    }

    private static bool StartsWithUnescapedPercent(string pattern, char? escapeChar) =>
        pattern.Length > 0 && pattern[0] == '%' && !(escapeChar.HasValue && pattern.Length >= 2 && pattern[0] == escapeChar.Value);

    /// <summary>
    /// True when the pattern's final character is <c>%</c> and isn't
    /// preceded by the escape character. Requires scanning from the start
    /// because an escape-char-then-<c>%</c> consumes the <c>%</c> as a
    /// literal, and the parity of consecutive escape chars determines
    /// whether the final char is itself escaped.
    /// </summary>
    private static bool EndsWithUnescapedPercent(string pattern, char? escapeChar)
    {
        if (pattern.Length == 0 || pattern[^1] != '%')
            return false;
        if (!escapeChar.HasValue)
            return true;
        var esc = escapeChar.Value;
        var escaped = false;
        for (var i = 0; i < pattern.Length - 1; i++)
            escaped = pattern[i] == esc && !escaped;
        return !escaped;
    }
}
