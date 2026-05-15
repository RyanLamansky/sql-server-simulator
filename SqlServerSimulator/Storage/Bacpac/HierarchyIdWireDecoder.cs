namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Decodes SQL Server's <c>hierarchyid</c> binary wire format into the
/// simulator's segment-array internal representation (<see cref="HierarchyIdSqlType"/>).
/// </summary>
/// <remarks>
/// <para>SQL Server's OrdPath binary encoding is a variable-bit Huffman-style
/// prefix code: each ordinal in the path is encoded as a self-contained bit
/// sequence (prefix bits + value bits + terminator bit), ordinals concatenate
/// left-to-right, and the byte stream is padded with zero bits to a byte
/// boundary. The path ends when remaining bits are all zero.</para>
///
/// <para>Encoding table for positive ordinals (derived empirically from the
/// SQL Server 2025 reference on 2026-05-15, ground-truth via
/// <c>SELECT n.ToString(), CAST(n AS varbinary)</c> over a synthetic sweep
/// plus AdventureWorks2025's HR.Employee.OrganizationNode):</para>
///
/// <list type="bullet">
/// <item>Prefix <c>01</c> (2 bits) → 2 value bits + 1 terminator → range [0..3], 5 bits total</item>
/// <item>Prefix <c>100</c> (3 bits) → 2 value bits + 1 terminator → range [4..7], 6 bits</item>
/// <item>Prefix <c>101</c> (3 bits) → 3 value bits + 1 terminator → range [8..15], 7 bits</item>
/// <item>Prefix <c>110</c> (3 bits) → 6 value bits with structural-bit insertion → range [16..79], 12 bits</item>
/// </list>
///
/// <para>The <c>110</c>-prefix encoding has a peculiar layout: after the 3-bit
/// prefix, 2 high-value bits are followed by a static <c>0</c>, then 1 more
/// high-value bit, then a static <c>1</c> (mid-byte separator), then 3
/// low-value bits, then the terminator <c>1</c>. The static bits are likely
/// artifacts of the original byte-aligned design — they're load-bearing
/// for round-trip but carry no value information.</para>
///
/// <para>Coverage notes for AdventureWorks2025: probe shows AW uses 27
/// distinct ordinals (0..22 inclusive — all comfortably within the
/// [0..79] envelope this decoder ships). Larger ordinals (80+) and the
/// negative range raise <see cref="NotSupportedException"/>; the BCP
/// loader's per-file try/catch routes those to a Skipped diagnostic
/// rather than aborting the whole load. AW doesn't use the dotted
/// sub-ordinal form (<c>/N.M/</c>) either; that's also deferred.</para>
/// </remarks>
internal static class HierarchyIdWireDecoder
{
    /// <summary>
    /// Parses a SQL Server hierarchyid binary payload into the simulator's
    /// segment-array form. Empty input → empty path (the canonical <c>/</c> root).
    /// Each ordinal in the path becomes a single-label segment, matching
    /// SQL Server's "<c>/N/M/</c>" canonical convention; the dotted-form
    /// <c>/N.M/</c> sub-ordinal grammar is unmodeled.
    /// </summary>
    public static int[][] Decode(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            return [];
        var reader = new BitReader(source);
        var ordinals = new List<int>();
        while (reader.HasMoreNonZeroBits())
            ordinals.Add(ReadOrdinal(ref reader));
        var result = new int[ordinals.Count][];
        for (var i = 0; i < ordinals.Count; i++)
            result[i] = [ordinals[i]];
        return result;
    }

    private static int ReadOrdinal(ref BitReader reader)
    {
        var bit1 = reader.ReadBit();
        var bit2 = reader.ReadBit();
        if (!bit1)
        {
            // Starts with 0_
            if (bit2)
            {
                // 01XX1 → range [0..3]: 2 value bits, terminator
                var value = reader.ReadBits(2);
                _ = reader.ReadBit();
                return value;
            }
            throw new NotSupportedException("hierarchyid binary decoder doesn't yet handle the negative-ordinal range (prefix 00).");
        }
        // Starts with 1_
        if (!bit2)
        {
            // 10
            var bit3 = reader.ReadBit();
            if (!bit3)
            {
                // 100XX1 → range [4..7]: 2 value bits, terminator
                var value = reader.ReadBits(2);
                _ = reader.ReadBit();
                return 4 + value;
            }
            // 101XXX1 → range [8..15]: 3 value bits, terminator
            var v = reader.ReadBits(3);
            _ = reader.ReadBit();
            return 8 + v;
        }
        // 11
        var bit3b = reader.ReadBit();
        if (!bit3b)
        {
            // 110 → range [16..79], 12-bit encoding with structural bits.
            // After prefix: bits 4-5 = high-2, bit 6 = static 0, bit 7 = mid,
            // bit 8 = static 1, bits 9-11 = low-3, bit 12 = terminator.
            var high2 = reader.ReadBits(2);
            _ = reader.ReadBit();
            var midHigh = reader.ReadBit() ? 1 : 0;
            _ = reader.ReadBit();
            var low3 = reader.ReadBits(3);
            _ = reader.ReadBit();
            var value = (high2 << 4) | (midHigh << 3) | low3;
            return 16 + value;
        }
        throw new NotSupportedException("hierarchyid binary decoder doesn't yet handle ordinals >= 80 (prefix 111).");
    }

    /// <summary>
    /// Bit-stream reader over a byte source. Bits are read MSB-first within
    /// each byte (matches the SQL Server hierarchyid wire convention).
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int bitOffset;

        public BitReader(ReadOnlySpan<byte> source)
        {
            this.bytes = source;
            this.bitOffset = 0;
        }

        public bool ReadBit()
        {
            if (this.bitOffset >= this.bytes.Length * 8)
                throw new InvalidDataException("hierarchyid wire decoder: read past end of input.");
            var byteIndex = this.bitOffset >> 3;
            var bitInByte = 7 - (this.bitOffset & 7);
            this.bitOffset++;
            return ((this.bytes[byteIndex] >> bitInByte) & 1) == 1;
        }

        /// <summary>
        /// Reads <paramref name="count"/> bits as an unsigned integer (first
        /// bit read = MSB of the result).
        /// </summary>
        public int ReadBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
                value = (value << 1) | (this.ReadBit() ? 1 : 0);
            return value;
        }

        /// <summary>
        /// True if any of the remaining unread bits is 1 — used to detect the
        /// end of path vs. the all-zero padding tail.
        /// </summary>
        public readonly bool HasMoreNonZeroBits()
        {
            for (var pos = this.bitOffset; pos < this.bytes.Length * 8; pos++)
            {
                var byteIndex = pos >> 3;
                var bitInByte = 7 - (pos & 7);
                if (((this.bytes[byteIndex] >> bitInByte) & 1) == 1)
                    return true;
            }
            return false;
        }
    }
}
