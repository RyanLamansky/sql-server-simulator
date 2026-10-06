using System.Collections.Frozen;
using System.Numerics;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// What <c>CHECKSUM</c> folds for a string a Latin1-General table compares —
/// a Windows <c>Latin1_General</c> name's data of either width, a
/// <c>SQL_Latin1_General</c> name's <c>nvarchar</c> data: the primary sort
/// weight of each character, two-byte units high byte first, with the
/// characters that weigh nothing there (a hyphen, an apostrophe) left out and
/// the trailing units of a space's weight dropped, so case and accents fold
/// away whatever the name's sensitivity (<c>N'aB'</c>, <c>N'ab'</c> and
/// <c>N'a-b '</c> hash alike, and so do <c>N'é'</c> and <c>N'e'</c>). The bytes
/// fold four at a time as little-endian words while four remain, then one at
/// a time, each step <c>h = rotl(h, 3) ^ unit</c> from zero (probed 2026-10-06
/// against SQL Server 2025).
/// </summary>
internal static partial class ChecksumWeights
{
    private static readonly FrozenDictionary<char, uint> version80 = Build(Version80Records, baseline: null);
    private static readonly FrozenDictionary<char, uint> version100 = Build(Version100Differences, version80);

    /// <summary>The weight units of a space, which real drops from the end of the string.</summary>
    private const ushort SpaceWeight = 0x0702;

    // Each record is a code point and its two units, packed high then low.
    private static FrozenDictionary<char, uint> Build(ReadOnlySpan<ushort> records, FrozenDictionary<char, uint>? baseline)
    {
        var map = baseline is null ? [] : new Dictionary<char, uint>(baseline);
        for (var i = 0; i < records.Length; i += 3)
            map[(char)records[i]] = ((uint)records[i + 1] << 16) | records[i + 2];
        return map.ToFrozenDictionary();
    }

    /// <summary>
    /// Folds <paramref name="text"/> through the unversioned or the
    /// <c>_100_</c> table; false, with nothing folded, when a character lies
    /// outside the table.
    /// </summary>
    internal static bool TryFold(string text, bool version100, out uint hash)
    {
        hash = 0;
        var table = version100 ? ChecksumWeights.version100 : version80;
        var units = text.Length <= 128 ? stackalloc ushort[text.Length * 2] : new ushort[text.Length * 2];
        var count = 0;
        foreach (var character in text)
        {
            if (!table.TryGetValue(character, out var packed))
                return false;
            if (packed >> 16 is var first and not 0)
                units[count++] = (ushort)first;
            if ((ushort)packed is var second and not 0)
                units[count++] = second;
        }
        while (count > 0 && units[count - 1] == SpaceWeight)
            count--;

        var length = count * 2;
        var position = 0;
        for (; length - position >= 4; position += 4)
            hash = BitOperations.RotateLeft(hash, 3) ^ (ByteAt(units, position) | (ByteAt(units, position + 1) << 8) | (ByteAt(units, position + 2) << 16) | (ByteAt(units, position + 3) << 24));
        for (; position < length; position++)
            hash = BitOperations.RotateLeft(hash, 3) ^ ByteAt(units, position);
        return true;

        static uint ByteAt(ReadOnlySpan<ushort> units, int index) =>
            (index & 1) == 0 ? (uint)(units[index >> 1] >> 8) : (uint)(units[index >> 1] & 0xFF);
    }
}
