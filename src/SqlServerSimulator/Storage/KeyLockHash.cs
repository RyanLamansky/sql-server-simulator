using System.Buffers;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The hash real prints for a <c>KEY</c> lock's <c>resource_description</c>
/// (and <c>%%lockres%%</c>), recovered from those outputs alone (probed
/// 2026-10-07 against SQL Server 2025): the 64-bit CRC with the ECMA-182
/// polynomial, initial value and final XOR all ones, fed MSB-first, over the
/// index row's key columns in key order, each in its stored form — fixed-width
/// types their storage bytes, strings their code-page or UTF-16 bytes as
/// stored, trailing spaces kept — with a NULL or zero-length value replaced by
/// its 1-based position among the key columns as two little-endian bytes. Of
/// the result's eight little-endian bytes, real prints bytes 4, 5, 0, 1, 2 and
/// 3, in that order, as twelve hex digits in parentheses.
/// <para>
/// The key columns are the index key, then for a non-unique nonclustered index
/// its row locator — the clustered key columns it doesn't already name, then
/// a non-unique clustered index's uniquifier. A non-unique clustered index
/// counts its uniquifier as column 1 and its key from 2; a row's uniquifier is
/// absent until a second row shares its key, so the first row of a key hashes
/// it as zero-length.
/// </para>
/// </summary>
internal static class KeyLockHash
{
    private const ulong Polynomial = 0x42F0E1EBA9EA3693;

    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];
        for (var i = 0; i < 256; i++)
        {
            var r = (ulong)i << 56;
            for (var bit = 0; bit < 8; bit++)
                r = (r & 0x8000_0000_0000_0000) != 0 ? (r << 1) ^ Polynomial : r << 1;
            table[i] = r;
        }
        return table;
    }

    /// <summary>
    /// The description of the key <paramref name="key"/> of columns typed
    /// <paramref name="types"/>, the uniquifier placed as
    /// <paramref name="uniquifier"/> says.
    /// </summary>
    public static string Describe(SqlValueKey key, SqlType[] types, KeyLockUniquifier uniquifier)
    {
        var crc = ulong.MaxValue;
        var ordinal = uniquifier == KeyLockUniquifier.First ? 2 : 1;
        byte[]? rented = null;
        Span<byte> scratch = stackalloc byte[64];
        try
        {
            for (var i = 0; i < key.ComponentCount; i++, ordinal++)
            {
                var value = key.ComponentAt(i);
                var type = i < types.Length ? types[i] : value.Type;
                var length = value.IsNull ? 0 : type.IsFixedLength ? type.FixedLength : type.GetVariableByteCount(value);
                if (length == 0)
                {
                    crc = Absent(crc, ordinal);
                    continue;
                }
                if (length > scratch.Length)
                {
                    if (rented is not null)
                        ArrayPool<byte>.Shared.Return(rented);
                    rented = ArrayPool<byte>.Shared.Rent(length);
                    scratch = rented;
                }
                var bytes = scratch[..length];
                bytes.Clear();
                if (type is BitSqlType)
                    bytes[0] = value.AsBoolean ? (byte)1 : (byte)0;
                else
                    _ = type.Encode(value, bytes);
                crc = Update(crc, bytes);
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
        crc = uniquifier switch
        {
            KeyLockUniquifier.First => Absent(crc, 1),
            KeyLockUniquifier.Last => Absent(crc, ordinal),
            _ => crc,
        };
        return Format(~crc);
    }

    private static ulong Absent(ulong crc, int ordinal)
    {
        Span<byte> marker = [(byte)ordinal, (byte)(ordinal >> 8)];
        return Update(crc, marker);
    }

    private static ulong Update(ulong crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            crc = (crc << 8) ^ Table[(int)(crc >> 56) ^ b];
        return crc;
    }

    private static string Format(ulong hash) => string.Create(14, hash, static (span, h) =>
    {
        span[0] = '(';
        span[13] = ')';
        ReadOnlySpan<int> order = [4, 5, 0, 1, 2, 3];
        for (var i = 0; i < order.Length; i++)
        {
            var b = (byte)(h >> (8 * order[i]));
            span[1 + (2 * i)] = HexDigit(b >> 4);
            span[2 + (2 * i)] = HexDigit(b & 0xF);
        }
    });

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
}

/// <summary>Where a key lock's hashed key carries a non-unique clustered index's uniquifier.</summary>
internal enum KeyLockUniquifier
{
    /// <summary>None: a unique index, or a nonclustered one over a unique clustered key or a heap.</summary>
    None,

    /// <summary>A non-unique clustered index's own key: the uniquifier is column 1.</summary>
    First,

    /// <summary>A non-unique nonclustered index over a non-unique clustered one: the uniquifier ends the row locator.</summary>
    Last,
}
