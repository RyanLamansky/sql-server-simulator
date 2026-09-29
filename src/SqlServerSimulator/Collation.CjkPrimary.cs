using System.Collections.Concurrent;
using System.IO.Compression;

namespace SqlServerSimulator;

/// <summary>
/// Real's primary order over the BMP for an East Asian collation family, which
/// <see cref="System.Globalization.CompareInfo"/> doesn't reproduce: SQL
/// Server's Chinese collations rank Latin ahead of Han where ICU's
/// <c>zh-CN</c> ranks it after, read polyphonic ideographs their own way
/// (重 as <i>zhòng</i>, 长 as <i>cháng</i>), and every family interleaves Han
/// with Hangul, kana and the compatibility blocks in an order of its own.
/// </summary>
/// <remarks>
/// <para>Each table is the dense rank of every BMP code unit under the family's
/// <c>_CI_AI</c> name — <c>DENSE_RANK() OVER (ORDER BY NCHAR(n) COLLATE
/// Chinese_PRC_CI_AI)</c> over <c>generate_series(0, 65535)</c>, probed
/// 2026-09-29 against SQL Server 2025 — so case, accent, kana and width fold
/// into one primary, and a code unit ranked with <c>NCHAR(0)</c> weighs
/// nothing.</para>
/// <para>The embedded resource carries one table per family as raw DEFLATE in
/// base64 under a <c>#Family</c> line. Decompressed, it lists every weighted
/// code unit in code-point order as two varints: the gap from the previous one
/// listed, then the change in its <em>run</em> (zig-zag, shifted left once)
/// with a low bit set when it ties the code unit before it in sort order. A
/// run is an ascending stretch of the sort order, so sorting by (run, code
/// unit) recovers it.</para>
/// </remarks>
internal sealed class CjkPrimaryOrder
{
    private static readonly ConcurrentDictionary<string, CjkPrimaryOrder?> cache = new(StringComparer.Ordinal);

    private static Dictionary<string, string>? sources;

    private readonly ushort[] ranks;

    private CjkPrimaryOrder(ushort[] ranks) => this.ranks = ranks;

    /// <summary>
    /// The table for a collation's family (its name prefix) and version (null
    /// for an unversioned name), or <see langword="null"/> when none ships.
    /// </summary>
    internal static CjkPrimaryOrder? For(string prefix, int? version) =>
        cache.GetOrAdd(version is null ? prefix : $"{prefix}_{version}", static key => Load(key))
        ?? cache.GetOrAdd(Family(prefix), static key => Load(key));

    // A family without a table of its own borrows the unversioned one its
    // ideograph order is closest to, which still places the scripts right.
    private static string Family(string prefix) => prefix switch
    {
        "Chinese_Hong_Kong_Stroke" or "Chinese_Taiwan_Bopomofo" or "Chinese_Traditional_Bopomofo" or "Chinese_Traditional_Stroke_Count" or "Chinese_Traditional_Stroke_Order" => "Chinese_Taiwan_Stroke",
        "Chinese_Simplified_Pinyin" or "Chinese_Traditional_Pinyin" => "Chinese_PRC",
        "Chinese_Simplified_Stroke_Order" => "Chinese_PRC_Stroke",
        "Japanese_Bushu_Kakusu" or "Japanese_XJIS" => "Japanese",
        "Korean" => "Korean_Wansung",
        _ => prefix,
    };

    /// <summary>Whether <paramref name="s"/> holds a character past U+2E80, where the East Asian scripts and the fullwidth forms live.</summary>
    internal static bool Reaches(ReadOnlySpan<char> s) => s.ContainsAnyInRange('⺀', '￿');

    /// <summary>
    /// Compares the primary weights of two strings code unit by code unit,
    /// skipping what weighs nothing and what <paramref name="weightless"/>
    /// marks minimal; <c>0</c> when they tie there.
    /// </summary>
    internal int ComparePrimary(string x, string y, WeightlessCharacters weightless)
    {
        var i = 0;
        var j = 0;
        while (true)
        {
            var a = this.Next(x, ref i, weightless);
            var b = this.Next(y, ref j, weightless);
            if (a != b)
                return a < b ? -1 : 1;
            if (a == 0)
                return 0;
        }
    }

    // The next weighted code unit's rank at or after index, or 0 at the end.
    private int Next(string s, ref int index, WeightlessCharacters weightless)
    {
        while (index < s.Length)
        {
            var c = s[index++];
            var rank = this.ranks[c];
            if (rank != 0 && !weightless.IsMinimal(c))
                return rank;
        }

        return 0;
    }

    private static CjkPrimaryOrder? Load(string key)
    {
        if (sources is null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var stream = typeof(CjkPrimaryOrder).Assembly.GetManifestResourceStream("SqlServerSimulator.Collation.CjkPrimary.txt")
                ?? throw new InvalidOperationException("The CJK primary-order resource is missing.");
            using var reader = new StreamReader(stream);
            string? name = null;
            var body = new System.Text.StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith('#'))
                {
                    if (name is not null)
                        map[name] = body.ToString();
                    name = line[1..];
                    _ = body.Clear();
                }
                else
                {
                    _ = body.Append(line);
                }
            }

            if (name is not null)
                map[name] = body.ToString();
            sources = map;
        }

        if (!sources.TryGetValue(key, out var encoded))
            return null;

        using var inflated = new DeflateStream(new MemoryStream(Convert.FromBase64String(encoded)), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        inflated.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        var codeUnits = new List<(int Run, int CodeUnit, bool Tie)>();
        var position = 0;
        var codeUnit = -1;
        var run = 0;
        while (position < bytes.Length)
        {
            codeUnit += ReadVarint(bytes, ref position) + 1;
            var packed = ReadVarint(bytes, ref position);
            var zigzag = packed >> 1;
            run += (zigzag & 1) == 0 ? zigzag >> 1 : -((zigzag + 1) >> 1);
            codeUnits.Add((run, codeUnit, (packed & 1) != 0));
        }

        codeUnits.Sort(static (a, b) => a.Run != b.Run ? a.Run.CompareTo(b.Run) : a.CodeUnit.CompareTo(b.CodeUnit));
        var ranks = new ushort[0x10000];
        ushort rank = 0;
        foreach (var (_, unit, tie) in codeUnits)
        {
            if (!tie)
                rank++;
            ranks[unit] = rank;
        }

        return new CjkPrimaryOrder(ranks);
    }

    private static int ReadVarint(ReadOnlySpan<byte> bytes, ref int position)
    {
        var value = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[position++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80)
                return value;
            shift += 7;
        }
    }
}
