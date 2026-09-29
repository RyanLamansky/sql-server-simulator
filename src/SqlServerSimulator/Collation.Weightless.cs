using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;

namespace SqlServerSimulator;

internal abstract partial class Collation
{
    /// <summary>
    /// Where real's weight table and <see cref="CompareInfo"/> disagree about
    /// which characters carry a weight; <see langword="null"/> for a collation
    /// that doesn't compare linguistically.
    /// </summary>
    internal virtual WeightlessCharacters? Weightless => null;
}

/// <summary>
/// The characters a linguistic collation's comparison has to treat
/// differently from <see cref="CompareInfo"/>, in both directions.
/// </summary>
/// <remarks>
/// <para><b>Dropped</b> — a code point real's collation version has no weight
/// for is ignorable wherever the collation compares: <c>N'a' + NCHAR(0x0378) +
/// N'b' = N'ab'</c> under every non-binary name. Which code points those are is
/// a property of the <em>version</em> alone (the unversioned names, 90, 100
/// and 140), identical across every language, case, kana and width flag, and
/// widened by an accent-insensitive name (probed 2026-09-29 against SQL Server
/// 2025 by testing <c>N'a' + NCHAR(n) + N'b' = N'ab'</c> for every BMP code
/// unit under 31 names). Unassigned code points dominate — 21,229 under the
/// unversioned names, 5,838 under version 100 — and the surrogates split by
/// version: an unversioned name ignores every surrogate code unit, so a whole
/// supplementary character weighs nothing, while version 90 and later weigh
/// every surrogate <em>pair</em> and ignore only a lone high surrogate in
/// U+D880..U+DB7F. The ones <see cref="CompareInfo"/> weighs are removed
/// before it compares.</para>
/// <para><b>Minimal</b> — a character <see cref="CompareInfo"/> ignores but
/// real weighs (the C0 / C1 controls, the Hebrew accents, <c>U+00AD</c> before
/// version 100 …) joins hyphen and apostrophe in the minimal treatment: no
/// primary weight, but a final-level weight that keeps
/// <c>N'x' + NCHAR(1) = N'x'</c> false and sorts the copy carrying it after
/// the copy without.</para>
/// </remarks>
internal sealed class WeightlessCharacters
{
    private static readonly ConcurrentDictionary<(int Version, CompareInfo Info, CompareOptions Options), WeightlessCharacters> cache = new();

    /// <summary>Everything real ignores, NUL aside.</summary>
    private readonly SearchValues<char> weightless;

    /// <summary>Real ignores, <see cref="CompareInfo"/> weighs.</summary>
    private readonly SearchValues<char> dropped;

    /// <summary>Hyphen, apostrophe, and what <see cref="CompareInfo"/> ignores where real weighs.</summary>
    private readonly SearchValues<char> minimal;

    /// <summary>The union of the two: a string holding none of these compares through <see cref="CompareInfo"/> as it stands.</summary>
    private readonly SearchValues<char> special;

    /// <summary>What <see cref="ForSearch"/> rewrites: the dropped characters and the keys of <see cref="searchStandIns"/>.</summary>
    private readonly SearchValues<char> searchSpecial;

    /// <summary>Each character <see cref="CompareInfo"/> ignores where real weighs it, mapped to a private-use character <see cref="CompareInfo"/> weighs.</summary>
    private readonly FrozenDictionary<char, char> searchStandIns;

    /// <summary>Version 90 and later weigh every surrogate pair; the unversioned names drop both halves.</summary>
    private readonly bool pairsWeighted;

    /// <summary>
    /// A character both engines ignore, which stands in for a dropped one where
    /// a search has to keep its positions.
    /// </summary>
    internal readonly char Filler;

    private WeightlessCharacters(int version, CompareInfo info, CompareOptions options)
    {
        var accentInsensitive = (options & CompareOptions.IgnoreNonSpace) != 0;
        var real = new bool[0x10000];
        Mark(real, version switch { >= 140 => Weightless140, 100 => Weightless100, 90 => Weightless90, _ => Weightless80 });
        if (accentInsensitive)
            Mark(real, version switch { >= 140 => WeightlessAccentInsensitive140, 100 => WeightlessAccentInsensitive100, 90 => WeightlessAccentInsensitive90, _ => WeightlessAccentInsensitive80 });

        var dropped = new List<char>();
        var minimal = new List<char> { '\'', '-' };
        var weightless = new List<char>();
        for (var c = 1; c < 0x10000; c++)
        {
            var ch = (char)c;
            var ignored = info.Compare(string.Concat("a", ch.ToString(), "b"), "ab", options) == 0;
            if (real[c])
                weightless.Add(ch);
            if (real[c] && !ignored)
                dropped.Add(ch);
            else if (!real[c] && ignored)
                minimal.Add(ch);
        }

        // The word joiner breaks no grapheme cluster, so a search reads the
        // characters around it as it would read them without it.
        this.Filler = '\u2060';
        this.pairsWeighted = version >= 90;
        this.weightless = SearchValues.Create(weightless.ToArray());
        this.dropped = SearchValues.Create(dropped.ToArray());
        this.minimal = SearchValues.Create(minimal.ToArray());
        this.special = SearchValues.Create([.. dropped, .. minimal]);
        // Hyphen and apostrophe already weigh something to CompareInfo; the
        // stand-ins come from the top of the private-use area, one apiece.
        // The no-break space and the U+2000..U+200A spaces are spaces to
        // CompareInfo but characters of their own to real (probed 2026-09-29
        // against SQL Server 2025 over every BMP code unit: only U+0020 and
        // the ideographic space U+3000 match a space), so a search holds them
        // apart the same way.
        var ignoredByCompareInfo = minimal.Where(static c => c is not ('\'' or '-')).Distinct()
            .Concat(Enumerable.Range(0x2000, 11).Select(static c => (char)c).Append('\u00A0')).Distinct().ToArray();
        this.searchStandIns = ignoredByCompareInfo.Select(static (c, index) => (c, (char)(0xF8FF - index))).ToFrozenDictionary(static pair => pair.c, static pair => pair.Item2);
        this.searchSpecial = SearchValues.Create([.. dropped, .. ignoredByCompareInfo]);
    }

    /// <summary>
    /// The shared instance for a collation version (<c>80</c> for the
    /// unversioned names) and the comparison it pairs with. Building one reads
    /// every BMP code unit through <paramref name="info"/> once.
    /// </summary>
    internal static WeightlessCharacters For(int version, CompareInfo info, CompareOptions options) =>
        cache.GetOrAdd((version, info, options), static key => new WeightlessCharacters(key.Version, key.Info, key.Options));

    private static void Mark(bool[] set, ReadOnlySpan<ushort> ranges)
    {
        for (var i = 0; i < ranges.Length; i += 2)
            set.AsSpan(ranges[i], ranges[i + 1]).Fill(true);
    }

    /// <summary>Whether <paramref name="s"/> holds anything <see cref="CompareInfo"/> can't be trusted with.</summary>
    internal bool IsSpecial(ReadOnlySpan<char> s) => s.ContainsAny(this.special);

    /// <summary>Whether <paramref name="s"/> holds a character real ignores and <see cref="CompareInfo"/> doesn't.</summary>
    internal bool HasDropped(ReadOnlySpan<char> s) => s.ContainsAny(this.dropped);

    private bool IsDroppedAt(ReadOnlySpan<char> s, int i, out int width)
    {
        var c = s[i];
        if (this.pairsWeighted && char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            width = 2;
            return false;
        }

        width = 1;
        return this.dropped.Contains(c);
    }

    /// <summary><paramref name="s"/> without the characters real ignores; the same instance when it holds none.</summary>
    internal string RemoveDropped(string s)
    {
        if (!this.HasDropped(s))
            return s;
        var buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var count = 0;
        for (var i = 0; i < s.Length;)
        {
            if (!this.IsDroppedAt(s, i, out var width))
            {
                s.AsSpan(i, width).CopyTo(buffer[count..]);
                count += width;
            }

            i += width;
        }

        return new string(buffer[..count]);
    }

    /// <summary>
    /// <paramref name="s"/> made ready for a <see cref="CompareInfo"/> search
    /// that keeps its positions: each character real ignores becomes
    /// <see cref="Filler"/>, and each one <see cref="CompareInfo"/> ignores
    /// where real weighs it becomes a private-use stand-in of its own, so
    /// <c>CHARINDEX(NCHAR(1), N'a' + NCHAR(1))</c> finds it as real does;
    /// <paramref name="s"/> itself when it holds neither.
    /// </summary>
    internal ReadOnlySpan<char> ForSearch(ReadOnlySpan<char> s)
    {
        if (!s.ContainsAny(this.searchSpecial))
            return s;
        var copy = s.ToArray();
        for (var i = 0; i < copy.Length;)
        {
            if (this.IsDroppedAt(copy, i, out var width))
                copy[i] = this.Filler;
            else if (width == 1 && this.searchStandIns.TryGetValue(copy[i], out var standIn))
                copy[i] = standIn;
            i += width;
        }

        return copy;
    }

    /// <summary>
    /// Whether the code unit at <paramref name="i"/> weighs nothing on real —
    /// which <c>LIKE</c>'s <c>_</c> doesn't count as a character of its own
    /// (<c>N'a' + NCHAR(0x0378) + N'b' LIKE N'a_b'</c> is false and
    /// <c>LIKE N'ab'</c> true, and so for U+200D and NUL; probed 2026-09-29
    /// against SQL Server 2025). A surrogate pair real weighs is never.
    /// </summary>
    internal bool IsWeightlessAt(ReadOnlySpan<char> s, int i)
    {
        var c = s[i];
        if (c == '\0')
            return true;
        if (!this.weightless.Contains(c))
            return false;
        return !this.pairsWeighted || !char.IsSurrogate(c)
            || (char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]) : i == 0 || !char.IsHighSurrogate(s[i - 1]));
    }

    /// <summary>Whether <paramref name="c"/> weighs only at the final level: hyphen, apostrophe, and what <see cref="CompareInfo"/> ignores where real weighs it.</summary>
    internal bool IsMinimal(char c) => this.minimal.Contains(c);

    /// <summary><paramref name="s"/> without its minimal-weight characters, for the primary comparison.</summary>
    internal string StripMinimal(string s)
    {
        if (!s.AsSpan().ContainsAny(this.minimal))
            return s;
        var buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var count = 0;
        foreach (var c in s)
        {
            if (!this.minimal.Contains(c))
                buffer[count++] = c;
        }

        return new string(buffer[..count]);
    }

    /// <summary>
    /// Orders two primary-equal strings by their minimal-weight characters: a
    /// string carrying fewer sorts first (<c>coop</c> &lt; <c>co-op</c>), a mark
    /// nearer the start sorts later, and two marks at the same place order by
    /// their own weight — the controls, then apostrophe, then hyphen, then the
    /// rest by code point.
    /// </summary>
    internal int MinimalTiebreak(string x, string y)
    {
        int i = 0, j = 0, xBefore = 0, yBefore = 0;
        while (true)
        {
            var xMark = this.NextMinimal(x, ref i, ref xBefore);
            var yMark = this.NextMinimal(y, ref j, ref yBefore);
            if (xMark < 0 || yMark < 0)
                return xMark < 0 ? (yMark < 0 ? 0 : -1) : 1;
            if (xBefore != yBefore)
                return xBefore < yBefore ? 1 : -1;
            if (xMark != yMark)
                return MinimalRank((char)xMark).CompareTo(MinimalRank((char)yMark));
        }
    }

    // The next minimal character at or after index, or -1; before counts the
    // weighted characters passed on the way.
    private int NextMinimal(string s, ref int index, ref int before)
    {
        while (index < s.Length)
        {
            var c = s[index++];
            if (this.minimal.Contains(c))
                return c;
            before++;
        }

        return -1;
    }

    private static int MinimalRank(char c) => c switch
    {
        < ' ' or (>= '\u007F' and <= '\u009F') => c,
        '\'' => 0x100,
        '-' => 0x101,
        _ => 0x200 + c,
    };

    // Version 80: the code points real ignores under an accent-sensitive name.
    private static ReadOnlySpan<ushort> Weightless80 =>
    [
        0x0000, 1, 0x01F6, 4, 0x0218, 56, 0x02A9, 7, 0x02DF, 1, 0x02EA, 22, 0x0349, 39, 0x0373, 1,
        0x0376, 4, 0x037B, 3, 0x037F, 5, 0x0387, 1, 0x038B, 1, 0x038D, 1, 0x03A2, 1, 0x03CF, 1,
        0x03D7, 3, 0x03F3, 14, 0x040D, 1, 0x0450, 1, 0x045D, 1, 0x0487, 9, 0x04CD, 100, 0x0557, 2,
        0x0560, 1, 0x0587, 2, 0x058A, 7, 0x05C4, 12, 0x05EB, 5, 0x05F5, 23, 0x060D, 14, 0x061C, 3,
        0x0620, 1, 0x063B, 6, 0x0653, 13, 0x066D, 3, 0x06D6, 26, 0x06FA, 519, 0x0904, 1, 0x093A, 2,
        0x094E, 2, 0x0955, 3, 0x0971, 16, 0x0984, 1, 0x098D, 2, 0x0991, 2, 0x09A9, 1, 0x09B1, 1,
        0x09B3, 3, 0x09BA, 2, 0x09BD, 1, 0x09C5, 2, 0x09C9, 2, 0x09CE, 9, 0x09D8, 4, 0x09DE, 1,
        0x09E4, 2, 0x09FB, 7, 0x0A03, 2, 0x0A0B, 4, 0x0A11, 2, 0x0A29, 1, 0x0A31, 1, 0x0A34, 1,
        0x0A37, 1, 0x0A3A, 2, 0x0A43, 4, 0x0A49, 2, 0x0A4D, 12, 0x0A5D, 1, 0x0A5F, 7, 0x0A75, 12,
        0x0A84, 1, 0x0A8C, 3, 0x0A91, 2, 0x0AA9, 1, 0x0AB1, 1, 0x0AB4, 1, 0x0ABA, 2, 0x0AC6, 1,
        0x0AC9, 2, 0x0ACE, 2, 0x0AD1, 15, 0x0AE1, 5, 0x0AF0, 17, 0x0B04, 1, 0x0B0D, 2, 0x0B11, 2,
        0x0B29, 1, 0x0B31, 1, 0x0B34, 2, 0x0B3A, 2, 0x0B44, 3, 0x0B49, 2, 0x0B4E, 9, 0x0B58, 4,
        0x0B5E, 1, 0x0B62, 4, 0x0B71, 17, 0x0B84, 1, 0x0B8B, 3, 0x0B91, 1, 0x0B96, 3, 0x0B9B, 1,
        0x0B9D, 1, 0x0BA0, 3, 0x0BA5, 3, 0x0BAB, 3, 0x0BB6, 1, 0x0BBA, 4, 0x0BC3, 3, 0x0BC9, 1,
        0x0BCE, 9, 0x0BD8, 15, 0x0BF3, 14, 0x0C04, 1, 0x0C0D, 1, 0x0C11, 1, 0x0C29, 1, 0x0C34, 1,
        0x0C3A, 4, 0x0C45, 1, 0x0C49, 1, 0x0C4E, 7, 0x0C57, 9, 0x0C62, 4, 0x0C70, 18, 0x0C84, 1,
        0x0C8D, 1, 0x0C91, 1, 0x0CA9, 1, 0x0CB4, 1, 0x0CBA, 4, 0x0CC5, 1, 0x0CC9, 1, 0x0CCE, 7,
        0x0CD7, 7, 0x0CDF, 1, 0x0CE2, 4, 0x0CF0, 18, 0x0D04, 1, 0x0D0D, 1, 0x0D11, 1, 0x0D29, 1,
        0x0D3A, 4, 0x0D44, 2, 0x0D49, 1, 0x0D4E, 9, 0x0D58, 8, 0x0D62, 4, 0x0D70, 18, 0x0D84, 1,
        0x0D8C, 2, 0x0D91, 1, 0x0DA9, 1, 0x0DB1, 1, 0x0DB4, 1, 0x0DBA, 4, 0x0DC5, 1, 0x0DC9, 1,
        0x0DCE, 7, 0x0DD6, 1, 0x0DD8, 6, 0x0DDF, 1, 0x0DE1, 6, 0x0DF7, 10, 0x0E3B, 4, 0x0E4C, 1,
        0x0E5C, 20, 0x0E75, 12, 0x0E83, 1, 0x0E85, 2, 0x0E89, 1, 0x0E8B, 2, 0x0E8E, 6, 0x0E98, 1,
        0x0EA0, 1, 0x0EA4, 1, 0x0EA6, 1, 0x0EA8, 2, 0x0EAC, 1, 0x0EBA, 1, 0x0EBE, 2, 0x0EC5, 1,
        0x0EC7, 1, 0x0ECE, 2, 0x0EDA, 2, 0x0EDE, 18, 0x0EF5, 267, 0x1023, 3, 0x102D, 1, 0x1032, 1,
        0x103F, 1, 0x104D, 83, 0x10C6, 10, 0x10F7, 4, 0x10FC, 4, 0x115A, 5, 0x11A3, 5, 0x11FA, 3078,
        0x1E9B, 5, 0x1EFA, 262, 0x200D, 3, 0x202A, 6, 0x2047, 41, 0x2071, 3, 0x208F, 17, 0x20AD, 35,
        0x20E2, 30, 0x2139, 26, 0x2183, 13, 0x21EB, 21, 0x22F2, 14, 0x237B, 133, 0x2425, 27, 0x244B, 21,
        0x24EB, 21, 0x2596, 10, 0x25F0, 16, 0x2614, 6, 0x2670, 145, 0x2705, 1, 0x270A, 2, 0x2728, 1,
        0x274C, 1, 0x274E, 1, 0x2753, 3, 0x2757, 1, 0x275F, 2, 0x2768, 14, 0x2795, 3, 0x27B0, 1,
        0x27BF, 2113, 0x3007, 1, 0x3033, 3, 0x3037, 8, 0x3040, 1, 0x3095, 4, 0x309F, 2, 0x30FF, 6,
        0x312D, 4, 0x318F, 3, 0x31A0, 96, 0x321D, 3, 0x3244, 28, 0x327C, 3, 0x32B1, 31, 0x32FF, 1,
        0x3358, 35, 0x33DE, 6690, 0x9FA6, 3162, 0xD7A4, 2140, 0xFA2E, 210, 0xFB07, 23, 0xFB1F, 785, 0xFE45, 4,
        0xFE53, 1, 0xFE67, 1, 0xFE6C, 4, 0xFE73, 1, 0xFE75, 1, 0xFEF5, 12, 0xFF5F, 2, 0xFFBF, 3,
        0xFFC8, 2, 0xFFD0, 2, 0xFFD8, 2, 0xFFDD, 3, 0xFFE7, 1, 0xFFEF, 17,
    ];

    // Version 80: the further code points an accent-insensitive name ignores.
    private static ReadOnlySpan<ushort> WeightlessAccentInsensitive80 =>
    [
        0x02B9, 13, 0x02C8, 1, 0x02CC, 4, 0x02D1, 7, 0x02DE, 1, 0x02E4, 6, 0x0300, 73, 0x0370, 3,
        0x0483, 4, 0x0559, 2, 0x0591, 45, 0x05BF, 4, 0x0901, 2, 0x093C, 1, 0x0941, 8, 0x094D, 1,
        0x0951, 4, 0x0962, 3, 0x0981, 1, 0x09BC, 1, 0x09C1, 4, 0x09CD, 1, 0x09E2, 2, 0x0A02, 1,
        0x0A3C, 1, 0x0A41, 2, 0x0A47, 2, 0x0A4B, 2, 0x0A70, 2, 0x0A81, 2, 0x0ABC, 1, 0x0AC1, 5,
        0x0AC7, 2, 0x0ACD, 1, 0x0B01, 1, 0x0B3C, 1, 0x0B3F, 1, 0x0B41, 3, 0x0B4D, 1, 0x0BC0, 1,
        0x0BCD, 1, 0x0C3E, 3, 0x0C46, 3, 0x0C4A, 4, 0x0C55, 2, 0x0CBF, 1, 0x0CC6, 1, 0x0CCD, 1,
        0x0D41, 3, 0x0D4D, 1, 0x0E47, 5, 0x0E4D, 1, 0x0EB1, 1, 0x0EB4, 6, 0x0EBB, 2, 0x0EC8, 6,
        0x1026, 5, 0x102E, 1, 0x1030, 1, 0x1036, 2, 0x103B, 1, 0x103D, 2, 0x104B, 2, 0x20D0, 18,
        0x302A, 6, 0x3099, 4, 0xFB1E, 1, 0xFF9E, 2,
    ];

    // Version 90: the code points real ignores under an accent-sensitive name.
    private static ReadOnlySpan<ushort> Weightless90 =>
    [
        0x0000, 1, 0x01F6, 4, 0x0218, 56, 0x02A9, 7, 0x02DF, 1, 0x02EA, 22, 0x0349, 43, 0x0376, 4,
        0x037B, 3, 0x037F, 5, 0x0387, 1, 0x038B, 1, 0x038D, 1, 0x03A2, 1, 0x03CF, 1, 0x03D7, 3,
        0x03F3, 14, 0x040D, 1, 0x0450, 1, 0x045D, 1, 0x0487, 9, 0x04C5, 2, 0x04C9, 2, 0x04CD, 11,
        0x04DA, 14, 0x04EA, 71, 0x0557, 2, 0x0560, 1, 0x0587, 2, 0x058A, 7, 0x05A2, 1, 0x05BA, 1,
        0x05C4, 12, 0x05EB, 5, 0x05F5, 23, 0x060D, 14, 0x061C, 3, 0x0620, 1, 0x063B, 6, 0x0653, 13,
        0x066D, 3, 0x06D6, 26, 0x06FA, 6, 0x070E, 1, 0x072D, 3, 0x074B, 53, 0x07B1, 336, 0x0904, 1,
        0x093A, 2, 0x094E, 2, 0x0955, 3, 0x0971, 16, 0x0984, 1, 0x098D, 2, 0x0991, 2, 0x09A9, 1,
        0x09B1, 1, 0x09B3, 3, 0x09BA, 2, 0x09BD, 1, 0x09C5, 2, 0x09C9, 2, 0x09CE, 9, 0x09D8, 4,
        0x09DE, 1, 0x09E4, 2, 0x09FB, 7, 0x0A03, 2, 0x0A0B, 4, 0x0A11, 2, 0x0A29, 1, 0x0A31, 1,
        0x0A34, 1, 0x0A37, 1, 0x0A3A, 2, 0x0A3D, 1, 0x0A43, 4, 0x0A49, 2, 0x0A4E, 11, 0x0A5D, 1,
        0x0A5F, 7, 0x0A75, 12, 0x0A84, 1, 0x0A8C, 1, 0x0A8E, 1, 0x0A92, 1, 0x0AA9, 1, 0x0AB1, 1,
        0x0AB4, 1, 0x0ABA, 2, 0x0AC6, 1, 0x0ACA, 1, 0x0ACE, 2, 0x0AD1, 15, 0x0AE1, 5, 0x0AF0, 17,
        0x0B04, 1, 0x0B0D, 2, 0x0B11, 2, 0x0B29, 1, 0x0B31, 1, 0x0B34, 2, 0x0B3A, 2, 0x0B44, 3,
        0x0B49, 2, 0x0B4E, 9, 0x0B58, 4, 0x0B5E, 1, 0x0B62, 4, 0x0B71, 17, 0x0B84, 1, 0x0B8B, 3,
        0x0B91, 1, 0x0B96, 3, 0x0B9B, 1, 0x0B9D, 1, 0x0BA0, 3, 0x0BA5, 3, 0x0BAB, 3, 0x0BB6, 1,
        0x0BBA, 4, 0x0BC3, 3, 0x0BC9, 1, 0x0BCE, 9, 0x0BD8, 15, 0x0BF3, 14, 0x0C04, 1, 0x0C0D, 1,
        0x0C11, 1, 0x0C29, 1, 0x0C34, 1, 0x0C3A, 4, 0x0C45, 1, 0x0C49, 1, 0x0C4E, 7, 0x0C57, 9,
        0x0C62, 4, 0x0C70, 18, 0x0C84, 1, 0x0C8D, 1, 0x0C91, 1, 0x0CA9, 1, 0x0CB4, 1, 0x0CBA, 4,
        0x0CC5, 1, 0x0CC9, 1, 0x0CCE, 7, 0x0CD7, 7, 0x0CDF, 1, 0x0CE2, 4, 0x0CF0, 18, 0x0D04, 1,
        0x0D0D, 1, 0x0D11, 1, 0x0D29, 1, 0x0D3A, 4, 0x0D44, 2, 0x0D49, 1, 0x0D4E, 9, 0x0D58, 8,
        0x0D62, 4, 0x0D70, 145, 0x0E3B, 4, 0x0E4C, 1, 0x0E5C, 37, 0x0E83, 1, 0x0E85, 2, 0x0E89, 1,
        0x0E8B, 2, 0x0E8E, 6, 0x0E98, 1, 0x0EA0, 1, 0x0EA4, 1, 0x0EA6, 1, 0x0EA8, 2, 0x0EAC, 1,
        0x0EBA, 1, 0x0EBE, 2, 0x0EC5, 1, 0x0EC7, 1, 0x0ECE, 2, 0x0EDA, 2, 0x0EDE, 450, 0x10C6, 10,
        0x10F7, 4, 0x10FC, 4, 0x115A, 5, 0x11A3, 5, 0x11FA, 3078, 0x1E9B, 5, 0x1EFA, 262, 0x200C, 4,
        0x202A, 6, 0x2047, 41, 0x2071, 3, 0x208F, 17, 0x20AD, 35, 0x20E2, 30, 0x2139, 26, 0x2183, 13,
        0x21EB, 21, 0x22F2, 14, 0x237B, 133, 0x2425, 27, 0x244B, 21, 0x24EB, 21, 0x2596, 10, 0x25F0, 16,
        0x2614, 6, 0x2670, 145, 0x2705, 1, 0x270A, 2, 0x2728, 1, 0x274C, 1, 0x274E, 1, 0x2753, 3,
        0x2757, 1, 0x275F, 2, 0x2768, 14, 0x2795, 3, 0x27B0, 1, 0x27BF, 2113, 0x3007, 1, 0x3033, 3,
        0x3037, 8, 0x3040, 1, 0x3095, 4, 0x309F, 2, 0x30FF, 6, 0x312D, 4, 0x318F, 3, 0x31A0, 96,
        0x321D, 3, 0x3244, 28, 0x327C, 3, 0x32B1, 31, 0x32FF, 1, 0x3358, 35, 0x33DE, 34, 0x4DB6, 74,
        0x9FA6, 3162, 0xD7A4, 92, 0xD880, 768, 0xFA2E, 210, 0xFB07, 23, 0xFB1F, 785, 0xFE45, 4, 0xFE53, 1,
        0xFE67, 1, 0xFE6C, 4, 0xFE73, 1, 0xFE75, 1, 0xFEF5, 12, 0xFF5F, 2, 0xFFBF, 3, 0xFFC8, 2,
        0xFFD0, 2, 0xFFD8, 2, 0xFFDD, 3, 0xFFE7, 1, 0xFFEF, 17,
    ];

    // Version 90: the further code points an accent-insensitive name ignores.
    private static ReadOnlySpan<ushort> WeightlessAccentInsensitive90 =>
    [
        0x02B9, 13, 0x02C8, 1, 0x02CC, 4, 0x02D1, 7, 0x02DE, 1, 0x02E4, 6, 0x0300, 73, 0x0483, 4,
        0x0559, 2, 0x0591, 17, 0x05A3, 23, 0x05BB, 3, 0x05BF, 4, 0x0711, 1, 0x0730, 27, 0x07A6, 11,
        0x093C, 1, 0x0951, 4, 0x0981, 1, 0x09BC, 1, 0x09C1, 4, 0x09CD, 1, 0x09E2, 2, 0x0A02, 1,
        0x0A3C, 1, 0x0A4D, 1, 0x0A70, 2, 0x0ABC, 2, 0x0B01, 1, 0x0B3C, 1, 0x0B3F, 1, 0x0B41, 3,
        0x0B4D, 1, 0x0BCD, 1, 0x0C55, 2, 0x0CD5, 2, 0x0D41, 3, 0x0D4D, 1, 0x0E47, 5, 0x0E4D, 1,
        0x0EB1, 1, 0x0EB4, 6, 0x0EBB, 2, 0x0EC8, 6, 0x20D0, 18, 0x302A, 6, 0x3099, 4, 0xFB1E, 1,
        0xFF9E, 2,
    ];

    // Version 100: the code points real ignores under an accent-sensitive name.
    private static ReadOnlySpan<ushort> Weightless100 =>
    [
        0x0000, 1, 0x00AD, 1, 0x034F, 1, 0x0370, 4, 0x0376, 4, 0x037F, 5, 0x038B, 1, 0x038D, 1,
        0x03A2, 1, 0x03CF, 1, 0x0487, 1, 0x0514, 29, 0x0557, 2, 0x0560, 1, 0x0588, 1, 0x058B, 6,
        0x05C8, 8, 0x05EB, 5, 0x05F5, 11, 0x0604, 7, 0x0616, 5, 0x061C, 2, 0x0620, 1, 0x063B, 6,
        0x065F, 1, 0x070E, 1, 0x074B, 2, 0x076E, 18, 0x07B2, 14, 0x07FB, 262, 0x093A, 2, 0x094E, 2,
        0x0955, 3, 0x0971, 10, 0x0980, 1, 0x0984, 1, 0x098D, 2, 0x0991, 2, 0x09A9, 1, 0x09B1, 1,
        0x09B3, 3, 0x09BA, 2, 0x09C5, 2, 0x09C9, 2, 0x09CF, 8, 0x09D8, 4, 0x09DE, 1, 0x09E4, 2,
        0x09FB, 6, 0x0A04, 1, 0x0A0B, 4, 0x0A11, 2, 0x0A29, 1, 0x0A31, 1, 0x0A34, 1, 0x0A37, 1,
        0x0A3A, 2, 0x0A3D, 1, 0x0A43, 4, 0x0A49, 2, 0x0A4E, 11, 0x0A5D, 1, 0x0A5F, 7, 0x0A75, 12,
        0x0A84, 1, 0x0A8E, 1, 0x0A92, 1, 0x0AA9, 1, 0x0AB1, 1, 0x0AB4, 1, 0x0ABA, 2, 0x0AC6, 1,
        0x0ACA, 1, 0x0ACE, 2, 0x0AD1, 15, 0x0AE4, 2, 0x0AF0, 1, 0x0AF2, 15, 0x0B04, 1, 0x0B0D, 2,
        0x0B11, 2, 0x0B29, 1, 0x0B31, 1, 0x0B34, 1, 0x0B3A, 4, 0x0B44, 3, 0x0B49, 2, 0x0B4E, 8,
        0x0B58, 4, 0x0B5E, 1, 0x0B62, 4, 0x0B70, 1, 0x0B72, 16, 0x0B84, 1, 0x0B8B, 3, 0x0B91, 1,
        0x0B96, 3, 0x0B9B, 1, 0x0B9D, 1, 0x0BA0, 3, 0x0BA5, 3, 0x0BAB, 3, 0x0BBA, 4, 0x0BC3, 3,
        0x0BC9, 1, 0x0BCE, 9, 0x0BD8, 14, 0x0BFB, 6, 0x0C04, 1, 0x0C0D, 1, 0x0C11, 1, 0x0C29, 1,
        0x0C34, 1, 0x0C3A, 4, 0x0C45, 1, 0x0C49, 1, 0x0C4E, 7, 0x0C57, 9, 0x0C62, 4, 0x0C70, 18,
        0x0C84, 1, 0x0C8D, 1, 0x0C91, 1, 0x0CA9, 1, 0x0CB4, 1, 0x0CBA, 2, 0x0CC5, 1, 0x0CC9, 1,
        0x0CCE, 7, 0x0CD7, 7, 0x0CDF, 1, 0x0CE4, 2, 0x0CF0, 1, 0x0CF3, 15, 0x0D04, 1, 0x0D0D, 1,
        0x0D11, 1, 0x0D29, 1, 0x0D3A, 4, 0x0D44, 2, 0x0D49, 1, 0x0D4E, 9, 0x0D58, 8, 0x0D62, 4,
        0x0D70, 18, 0x0D84, 1, 0x0D97, 3, 0x0DB2, 1, 0x0DBC, 1, 0x0DBE, 2, 0x0DC7, 8, 0x0DD5, 1,
        0x0DD7, 1, 0x0DE0, 18, 0x0DF5, 12, 0x0E3B, 4, 0x0E4C, 1, 0x0E5C, 37, 0x0E83, 1, 0x0E85, 2,
        0x0E89, 1, 0x0E8B, 2, 0x0E8E, 6, 0x0E98, 1, 0x0EA0, 1, 0x0EA4, 1, 0x0EA6, 1, 0x0EA8, 2,
        0x0EAC, 1, 0x0EBA, 1, 0x0EBE, 2, 0x0EC5, 1, 0x0EC7, 1, 0x0ECC, 1, 0x0ECE, 2, 0x0EDA, 2,
        0x0EDE, 34, 0x0F48, 1, 0x0F6B, 6, 0x0F8C, 4, 0x0F98, 1, 0x0FBD, 1, 0x0FCD, 2, 0x0FD2, 46,
        0x1022, 1, 0x1028, 1, 0x102B, 1, 0x1033, 3, 0x103A, 6, 0x105A, 70, 0x10C6, 10, 0x10FD, 3,
        0x115A, 5, 0x11A3, 5, 0x11FA, 6, 0x1249, 1, 0x124E, 2, 0x1257, 1, 0x1259, 1, 0x125E, 2,
        0x1289, 1, 0x128E, 2, 0x12B1, 1, 0x12B6, 2, 0x12BF, 1, 0x12C1, 1, 0x12C6, 2, 0x12D7, 1,
        0x1311, 1, 0x1316, 2, 0x135B, 4, 0x137D, 3, 0x139A, 6, 0x13F5, 12, 0x1677, 9, 0x169D, 3,
        0x16F1, 15, 0x170D, 1, 0x1715, 11, 0x1737, 9, 0x1754, 12, 0x176D, 1, 0x1771, 1, 0x1774, 12,
        0x17DE, 2, 0x17EA, 6, 0x17FA, 6, 0x1806, 1, 0x180B, 3, 0x180F, 1, 0x181A, 6, 0x1878, 8,
        0x18AA, 86, 0x191D, 3, 0x192C, 4, 0x193C, 4, 0x1941, 3, 0x196E, 2, 0x1975, 11, 0x19AA, 6,
        0x19CA, 6, 0x19DA, 4, 0x1A1C, 2, 0x1A20, 224, 0x1B4C, 4, 0x1B7D, 387, 0x1DCB, 51, 0x1E9C, 4,
        0x1EFA, 6, 0x1F16, 2, 0x1F1E, 2, 0x1F46, 2, 0x1F4E, 2, 0x1F58, 1, 0x1F5A, 1, 0x1F5C, 1,
        0x1F5E, 1, 0x1F7E, 2, 0x1FB5, 1, 0x1FC5, 1, 0x1FD4, 2, 0x1FDC, 1, 0x1FF0, 2, 0x1FF5, 1,
        0x1FFF, 1, 0x200C, 4, 0x202A, 5, 0x2060, 16, 0x2072, 2, 0x208F, 1, 0x2095, 11, 0x20B6, 26,
        0x20F0, 16, 0x214F, 4, 0x2185, 11, 0x23E8, 24, 0x2427, 25, 0x244B, 21, 0x269D, 3, 0x26B3, 78,
        0x2705, 1, 0x270A, 2, 0x2728, 1, 0x274C, 1, 0x274E, 1, 0x2753, 3, 0x2757, 1, 0x275F, 2,
        0x2795, 3, 0x27B0, 1, 0x27BF, 1, 0x27CB, 5, 0x27EC, 4, 0x2B1B, 5, 0x2B24, 220, 0x2C2F, 1,
        0x2C5F, 1, 0x2C6D, 7, 0x2C78, 8, 0x2CEB, 14, 0x2D26, 10, 0x2D66, 9, 0x2D70, 16, 0x2D97, 9,
        0x2DA7, 1, 0x2DAF, 1, 0x2DB7, 1, 0x2DBF, 1, 0x2DC7, 1, 0x2DCF, 1, 0x2DD7, 1, 0x2DDF, 33,
        0x2E18, 4, 0x2E1E, 98, 0x2E9A, 1, 0x2EF4, 12, 0x2FD6, 26, 0x2FFC, 4, 0x3040, 1, 0x3097, 2,
        0x3100, 5, 0x312D, 4, 0x318F, 3, 0x31B8, 8, 0x31D0, 32, 0x321F, 1, 0x3244, 12, 0x32FF, 1,
        0x4DB6, 10, 0x9FBC, 68, 0xA48D, 3, 0xA4C7, 569, 0xA71B, 5, 0xA722, 222, 0xA82C, 20, 0xA878, 904,
        0xD7A4, 92, 0xD880, 768, 0xFA2E, 2, 0xFA6B, 5, 0xFACF, 3, 0xFAD5, 3, 0xFADA, 38, 0xFB07, 12,
        0xFB18, 5, 0xFB37, 1, 0xFB3D, 1, 0xFB3F, 1, 0xFB42, 1, 0xFB45, 1, 0xFBB2, 33, 0xFD40, 16,
        0xFD90, 2, 0xFDC8, 40, 0xFDFE, 18, 0xFE1A, 6, 0xFE24, 12, 0xFE53, 1, 0xFE67, 1, 0xFE6C, 4,
        0xFE75, 1, 0xFEFD, 2, 0xFF00, 1, 0xFFBF, 3, 0xFFC8, 2, 0xFFD0, 2, 0xFFD8, 2, 0xFFDD, 3,
        0xFFE7, 1, 0xFFEF, 17,
    ];

    // Version 100: the further code points an accent-insensitive name ignores.
    private static ReadOnlySpan<ushort> WeightlessAccentInsensitive100 =>
    [
        0x02B9, 13, 0x02C8, 1, 0x02CC, 4, 0x02D1, 7, 0x02DE, 2, 0x02E4, 107, 0x0350, 19, 0x0374, 1,
        0x0483, 4, 0x0559, 2, 0x0591, 45, 0x05BF, 4, 0x05C4, 2, 0x05C7, 1, 0x0602, 1, 0x0610, 6,
        0x064B, 20, 0x0670, 1, 0x06D8, 5, 0x06E1, 8, 0x06EA, 4, 0x0711, 1, 0x0730, 27, 0x07A6, 11,
        0x07EB, 9, 0x093C, 1, 0x0951, 4, 0x0981, 1, 0x09BC, 1, 0x09C1, 4, 0x09CD, 1, 0x09E2, 2,
        0x0A01, 2, 0x0A3C, 1, 0x0A4D, 1, 0x0A70, 2, 0x0ABC, 2, 0x0B4D, 1, 0x0BCD, 1, 0x0C55, 2,
        0x0CBC, 2, 0x0CD5, 2, 0x0D4D, 1, 0x0E47, 5, 0x0E4D, 1, 0x0F39, 1, 0x0F71, 1, 0x0F7F, 1,
        0x0F84, 2, 0x0F88, 4, 0x10FC, 1, 0x135F, 1, 0x1361, 8, 0x16EB, 3, 0x1B6B, 9, 0x1DC0, 11,
        0x1DFE, 2, 0x20D0, 32, 0x302A, 6, 0x303C, 3, 0x3099, 4, 0xFB1E, 1, 0xFC5E, 6, 0xFCF2, 3,
        0xFE20, 4, 0xFE70, 3, 0xFE74, 1, 0xFE76, 10, 0xFF9E, 2,
    ];

    // Version 140: the code points real ignores under an accent-sensitive name.
    private static ReadOnlySpan<ushort> Weightless140 =>
    [
        0x0000, 1, 0x00AD, 1, 0x034F, 1, 0x0378, 2, 0x037F, 5, 0x038B, 1, 0x038D, 1, 0x03A2, 1,
        0x0524, 2, 0x0528, 9, 0x0557, 2, 0x0560, 1, 0x0588, 1, 0x058B, 6, 0x05C8, 8, 0x05EB, 5,
        0x05F5, 11, 0x0604, 2, 0x061C, 2, 0x0640, 1, 0x070E, 1, 0x074B, 2, 0x07B2, 14, 0x07FB, 5,
        0x082E, 2, 0x083F, 1, 0x085C, 2, 0x085F, 162, 0x0978, 1, 0x0980, 1, 0x0984, 1, 0x098D, 2,
        0x0991, 2, 0x09A9, 1, 0x09B1, 1, 0x09B3, 3, 0x09BA, 2, 0x09C5, 2, 0x09C9, 2, 0x09CF, 8,
        0x09D8, 4, 0x09DE, 1, 0x09E4, 2, 0x09FB, 6, 0x0A04, 1, 0x0A0B, 4, 0x0A11, 2, 0x0A29, 1,
        0x0A31, 1, 0x0A34, 1, 0x0A37, 1, 0x0A3A, 2, 0x0A3D, 1, 0x0A43, 4, 0x0A49, 2, 0x0A4E, 3,
        0x0A52, 7, 0x0A5D, 1, 0x0A5F, 7, 0x0A76, 11, 0x0A84, 1, 0x0A8E, 1, 0x0A92, 1, 0x0AA9, 1,
        0x0AB1, 1, 0x0AB4, 1, 0x0ABA, 2, 0x0AC6, 1, 0x0ACA, 1, 0x0ACE, 2, 0x0AD1, 15, 0x0AE4, 2,
        0x0AF0, 1, 0x0AF2, 15, 0x0B04, 1, 0x0B0D, 2, 0x0B11, 2, 0x0B29, 1, 0x0B31, 1, 0x0B34, 1,
        0x0B3A, 2, 0x0B45, 2, 0x0B49, 2, 0x0B4E, 8, 0x0B58, 4, 0x0B5E, 1, 0x0B64, 2, 0x0B70, 1,
        0x0B78, 10, 0x0B84, 1, 0x0B8B, 3, 0x0B91, 1, 0x0B96, 3, 0x0B9B, 1, 0x0B9D, 1, 0x0BA0, 3,
        0x0BA5, 3, 0x0BAB, 3, 0x0BBA, 4, 0x0BC3, 3, 0x0BC9, 1, 0x0BCE, 2, 0x0BD1, 6, 0x0BD8, 14,
        0x0BFB, 6, 0x0C04, 1, 0x0C0D, 1, 0x0C11, 1, 0x0C29, 1, 0x0C34, 1, 0x0C3A, 3, 0x0C45, 1,
        0x0C49, 1, 0x0C4E, 7, 0x0C57, 1, 0x0C5A, 6, 0x0C64, 2, 0x0C70, 8, 0x0C80, 2, 0x0C84, 1,
        0x0C8D, 1, 0x0C91, 1, 0x0CA9, 1, 0x0CB4, 1, 0x0CBA, 2, 0x0CC5, 1, 0x0CC9, 1, 0x0CCE, 7,
        0x0CD7, 7, 0x0CDF, 1, 0x0CE4, 2, 0x0CF0, 1, 0x0CF3, 15, 0x0D04, 1, 0x0D0D, 1, 0x0D11, 1,
        0x0D3B, 2, 0x0D45, 1, 0x0D49, 1, 0x0D4F, 8, 0x0D58, 8, 0x0D64, 2, 0x0D76, 3, 0x0D80, 2,
        0x0D84, 1, 0x0D97, 3, 0x0DB2, 1, 0x0DBC, 1, 0x0DBE, 2, 0x0DC7, 3, 0x0DCB, 4, 0x0DD5, 1,
        0x0DD7, 1, 0x0DE0, 18, 0x0DF5, 12, 0x0E3B, 4, 0x0E4C, 1, 0x0E5C, 37, 0x0E83, 1, 0x0E85, 2,
        0x0E89, 1, 0x0E8B, 2, 0x0E8E, 6, 0x0E98, 1, 0x0EA0, 1, 0x0EA4, 1, 0x0EA6, 1, 0x0EA8, 2,
        0x0EAC, 1, 0x0EBA, 1, 0x0EBE, 2, 0x0EC5, 1, 0x0EC7, 1, 0x0ECC, 1, 0x0ECE, 2, 0x0EDA, 2,
        0x0EDE, 34, 0x0F48, 1, 0x0F6D, 4, 0x0F98, 1, 0x0FBD, 1, 0x0FCD, 1, 0x0FDB, 37, 0x10C6, 10,
        0x10FD, 3, 0x1249, 1, 0x124E, 2, 0x1257, 1, 0x1259, 1, 0x125E, 2, 0x1289, 1, 0x128E, 2,
        0x12B1, 1, 0x12B6, 2, 0x12BF, 1, 0x12C1, 1, 0x12C6, 2, 0x12D7, 1, 0x1311, 1, 0x1316, 2,
        0x135B, 2, 0x137D, 3, 0x139A, 6, 0x13F5, 11, 0x169D, 3, 0x16F1, 15, 0x170D, 1, 0x1715, 11,
        0x1737, 9, 0x1754, 12, 0x176D, 1, 0x1771, 1, 0x1774, 12, 0x17DE, 2, 0x17EA, 6, 0x17FA, 6,
        0x1806, 1, 0x180B, 3, 0x180F, 1, 0x181A, 6, 0x1878, 8, 0x18AB, 5, 0x18F6, 10, 0x191D, 3,
        0x192C, 4, 0x193C, 4, 0x1941, 3, 0x196E, 2, 0x1975, 11, 0x19AC, 4, 0x19CA, 6, 0x19DB, 3,
        0x1A1C, 2, 0x1A5F, 1, 0x1A7D, 2, 0x1A8A, 6, 0x1A9A, 6, 0x1AAE, 82, 0x1B4C, 4, 0x1B7D, 3,
        0x1BAB, 3, 0x1BBA, 6, 0x1BF4, 8, 0x1C38, 3, 0x1C4A, 3, 0x1C80, 80, 0x1CF3, 13, 0x1DE7, 21,
        0x1F16, 2, 0x1F1E, 2, 0x1F46, 2, 0x1F4E, 2, 0x1F58, 1, 0x1F5A, 1, 0x1F5C, 1, 0x1F5E, 1,
        0x1F7E, 2, 0x1FB5, 1, 0x1FC5, 1, 0x1FD4, 2, 0x1FDC, 1, 0x1FF0, 2, 0x1FF5, 1, 0x1FFF, 1,
        0x200C, 4, 0x202A, 5, 0x2060, 16, 0x2072, 2, 0x208F, 1, 0x209D, 3, 0x20B6, 3, 0x20BB, 21,
        0x20F1, 15, 0x218A, 6, 0x23F4, 12, 0x2427, 25, 0x244B, 21, 0x26BD, 3, 0x2700, 1, 0x2795, 3,
        0x27B0, 1, 0x27BF, 1, 0x27CB, 1, 0x27CD, 1, 0x2B4D, 3, 0x2B5A, 166, 0x2C2F, 1, 0x2C5F, 1,
        0x2CF2, 7, 0x2D26, 10, 0x2D66, 9, 0x2D71, 14, 0x2D97, 9, 0x2DA7, 1, 0x2DAF, 1, 0x2DB7, 1,
        0x2DBF, 1, 0x2DC7, 1, 0x2DCF, 1, 0x2DD7, 1, 0x2DDF, 1, 0x2E32, 78, 0x2E9A, 1, 0x2EF4, 12,
        0x2FD6, 26, 0x2FFC, 4, 0x3040, 1, 0x3097, 2, 0x3100, 5, 0x312E, 3, 0x318F, 3, 0x31BB, 5,
        0x31E4, 12, 0x321F, 1, 0x32FF, 1, 0x4DB6, 10, 0x9FCC, 52, 0xA48D, 3, 0xA4C7, 9, 0xA62C, 20,
        0xA660, 2, 0xA674, 8, 0xA698, 8, 0xA6F8, 8, 0xA78F, 1, 0xA792, 14, 0xA7AA, 80, 0xA82C, 4,
        0xA836, 10, 0xA878, 8, 0xA8C5, 9, 0xA8DA, 24, 0xA8F8, 3, 0xA8FC, 4, 0xA954, 11, 0xA97D, 3,
        0xA9CE, 1, 0xA9DA, 4, 0xA9E0, 32, 0xAA37, 9, 0xAA4E, 2, 0xAA5A, 2, 0xAA7C, 4, 0xAAC3, 24,
        0xAAE0, 33, 0xAB07, 2, 0xAB0F, 2, 0xAB17, 9, 0xAB27, 1, 0xAB2F, 145, 0xABEE, 2, 0xABFA, 6,
        0xD7A4, 12, 0xD7C7, 4, 0xD7FC, 4, 0xD880, 768, 0xFA2E, 2, 0xFA6E, 2, 0xFADA, 38, 0xFB07, 12,
        0xFB18, 5, 0xFB37, 1, 0xFB3D, 1, 0xFB3F, 1, 0xFB42, 1, 0xFB45, 1, 0xFBB2, 33, 0xFD40, 16,
        0xFD90, 2, 0xFDC8, 40, 0xFDFE, 18, 0xFE1A, 6, 0xFE27, 9, 0xFE53, 1, 0xFE67, 1, 0xFE6C, 4,
        0xFE75, 1, 0xFEFD, 4, 0xFFBF, 3, 0xFFC8, 2, 0xFFD0, 2, 0xFFD8, 2, 0xFFDD, 3, 0xFFE7, 1,
        0xFFEF, 17,
    ];

    // Version 140: the further code points an accent-insensitive name ignores.
    private static ReadOnlySpan<ushort> WeightlessAccentInsensitive140 =>
    [
        0x02B9, 13, 0x02C8, 1, 0x02CC, 4, 0x02D1, 7, 0x02DE, 2, 0x02E4, 107, 0x0350, 19, 0x0374, 1,
        0x0483, 5, 0x0559, 2, 0x0591, 45, 0x05BF, 4, 0x05C4, 2, 0x05C7, 1, 0x0602, 1, 0x0610, 11,
        0x064B, 21, 0x0670, 1, 0x06D8, 5, 0x06E1, 8, 0x06EA, 4, 0x0711, 1, 0x0730, 27, 0x07A6, 11,
        0x07EB, 9, 0x0818, 2, 0x081C, 18, 0x0859, 3, 0x093C, 1, 0x0951, 4, 0x0971, 1, 0x09BC, 1,
        0x0A01, 2, 0x0A3C, 1, 0x0A70, 2, 0x0ABC, 1, 0x0B3C, 1, 0x0C55, 2, 0x0CBC, 1, 0x0CD5, 2,
        0x0E47, 5, 0x0E4D, 1, 0x0F39, 1, 0x0F71, 1, 0x0F7F, 1, 0x0F84, 2, 0x0F88, 8, 0x10FC, 1,
        0x135D, 3, 0x1361, 8, 0x16EB, 3, 0x1A74, 9, 0x1A7F, 1, 0x1B6B, 9, 0x1C37, 1, 0x1DC0, 18,
        0x1DFC, 4, 0x20D0, 33, 0x2CEF, 3, 0x2D7F, 1, 0x302A, 6, 0x303C, 3, 0x3099, 4, 0xA66F, 1,
        0xA67C, 2, 0xA6F0, 2, 0xA92B, 3, 0xFB1E, 1, 0xFC5E, 6, 0xFCF2, 3, 0xFE20, 7, 0xFE71, 1,
        0xFE77, 1, 0xFE79, 1, 0xFE7B, 1, 0xFE7D, 1, 0xFE7F, 1, 0xFF9E, 2,
    ];
}
