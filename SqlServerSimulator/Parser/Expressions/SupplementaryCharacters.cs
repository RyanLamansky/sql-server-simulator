namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Codepoint-aware string operations used by scalar functions
/// (<c>LEN</c>, <c>SUBSTRING</c>, <c>LEFT</c>, <c>RIGHT</c>, <c>CHARINDEX</c>,
/// <c>PATINDEX</c>, <c>STUFF</c>, <c>REVERSE</c>, <c>UNICODE</c>) when the
/// input value's collation has the <c>_SC_</c> flag. Non-SC paths stay on
/// .NET <see cref="string.Length"/> / <see cref="string.Substring(int, int)"/>
/// (code-unit semantics — matches real SQL Server's non-SC behavior).
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-21): under a
/// <c>_SC_UTF8</c> collation, <c>LEN(N'😀')</c> = 1, <c>SUBSTRING(N'😀X',1,1)</c> =
/// `'😀'`, <c>LEFT(N'😀X',1)</c> = `'😀'`, <c>CHARINDEX(N'X',N'😀X')</c> = 2,
/// <c>UNICODE(N'😀')</c> = 128512. Under non-SC the same calls return 2 /
/// lone-high-surrogate / lone-high-surrogate / 3 / 55357 respectively.
/// </remarks>
internal static class SupplementaryCharacters
{
    /// <summary>Counts the Unicode codepoints (Runes) in <paramref name="s"/> — alloc-free.</summary>
    internal static int CodepointCount(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    /// <summary>
    /// Returns the code-unit offset corresponding to the
    /// <paramref name="codepointOffset"/>-th codepoint boundary in
    /// <paramref name="s"/>. Clamps to the string length on overshoot.
    /// </summary>
    internal static int CodepointToCodeUnit(string s, int codepointOffset)
    {
        if (codepointOffset <= 0) return 0;
        var seen = 0;
        var i = 0;
        while (i < s.Length && seen < codepointOffset)
        {
            var c = s[i];
            i += char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            seen++;
        }
        return i;
    }

    /// <summary>
    /// Returns the codepoint offset corresponding to
    /// <paramref name="codeUnitOffset"/> in <paramref name="s"/>. A lone
    /// high surrogate immediately preceding <paramref name="codeUnitOffset"/>
    /// counts as a full codepoint (matches real SQL Server's CHARINDEX
    /// behavior when the input is malformed). Used to translate
    /// <see cref="string.IndexOf(string)"/> results back to codepoint terms
    /// under SC.
    /// </summary>
    internal static int CodeUnitToCodepoint(string s, int codeUnitOffset)
    {
        if (codeUnitOffset <= 0) return 0;
        var cp = 0;
        var i = 0;
        while (i < codeUnitOffset && i < s.Length)
        {
            var c = s[i];
            i += char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            cp++;
        }
        return cp;
    }

    /// <summary>Returns the leftmost <paramref name="codepointCount"/> codepoints of <paramref name="s"/> (clamping at end).</summary>
    internal static string LeftByCodepoints(string s, int codepointCount) =>
        codepointCount <= 0 ? string.Empty
        : codepointCount >= s.Length ? s
        : s[..CodepointToCodeUnit(s, codepointCount)];

    /// <summary>Returns the rightmost <paramref name="codepointCount"/> codepoints of <paramref name="s"/> (clamping at start).</summary>
    internal static string RightByCodepoints(string s, int codepointCount)
    {
        if (codepointCount <= 0) return string.Empty;
        var total = CodepointCount(s);
        return codepointCount >= total ? s : s[CodepointToCodeUnit(s, total - codepointCount)..];
    }

    /// <summary>
    /// Reverses <paramref name="s"/> by codepoint — surrogate pairs stay
    /// intact. Used by <c>REVERSE</c> under SC. The non-SC path reverses by
    /// code unit (splits surrogate pairs); call <see cref="ReverseByCodeUnits"/>.
    /// </summary>
    internal static string ReverseByCodepoints(string s)
    {
        var reversed = new char[s.Length];
        var src = 0;
        var dst = s.Length;
        while (src < s.Length)
        {
            var c = s[src];
            if (char.IsHighSurrogate(c) && src + 1 < s.Length && char.IsLowSurrogate(s[src + 1]))
            {
                dst -= 2;
                reversed[dst] = c;
                reversed[dst + 1] = s[src + 1];
                src += 2;
            }
            else
            {
                dst--;
                reversed[dst] = c;
                src++;
            }
        }
        return new string(reversed);
    }

    /// <summary>Reverses <paramref name="s"/> by code unit — surrogate pairs are torn (high/low order swaps). Real SQL Server's non-SC <c>REVERSE</c> behavior.</summary>
    internal static string ReverseByCodeUnits(string s)
    {
        var reversed = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
            reversed[s.Length - 1 - i] = s[i];
        return new string(reversed);
    }

    /// <summary>
    /// Returns the codepoint (full Unicode scalar) at the start of
    /// <paramref name="s"/>. A leading surrogate pair returns its combined
    /// value (e.g. <c>U+1F600 = 128512</c>); a lone code unit returns its
    /// 16-bit value. Caller must guard against empty input.
    /// </summary>
    internal static int LeadingCodepoint(string s) =>
        char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])
            ? char.ConvertToUtf32(s[0], s[1])
            : s[0];
}
