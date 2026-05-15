namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Decodes SQL Server's <c>hierarchyid</c> binary wire format into the
/// simulator's segment-array internal representation (<see cref="HierarchyIdSqlType"/>).
/// </summary>
/// <remarks>
/// <para>SQL Server's OrdPath binary encoding is an order-preserving
/// variable-length prefix code: each ordinal in the path is encoded as a
/// self-contained bit sequence (prefix bits + value bits + terminator bit),
/// ordinals concatenate left-to-right, and the byte stream is padded with
/// zero bits to a byte boundary. The path ends when remaining bits are all
/// zero.</para>
///
/// <para><b>Index-relevant design properties</b> (the simulator stores the
/// decoded segment-array form and does linear scans, so none of these are
/// exercised today — but any future B-tree index work depends on them, and
/// "cleaning up" the encoding without understanding what each property
/// buys would break index behavior cross-engine):</para>
///
/// <list type="bullet">
/// <item><b>Byte-wise <c>memcmp</c> ordering equals depth-first pre-order
/// traversal of the tree.</b> A B-tree on a hierarchyid column naturally
/// clusters siblings together and places ancestors immediately before their
/// descendants. SQL Server's two recommended index shapes — depth-first
/// (<c>CREATE INDEX … ON t(node)</c>) and breadth-first
/// (<c>… ON t(node.GetLevel(), node)</c>) — both fall out of this property.
/// Verified observationally from the decoder tests: <c>/3/</c> = <c>0x78</c>,
/// <c>/3/0/</c> = <c>0x7A40</c>; the zero-padding tail of an ancestor is
/// always less than the next-ordinal continuation of a descendant.</item>
/// <item><b>Ancestor encoding is a bit-prefix (not byte-prefix) of every
/// descendant's encoding</b>, modulo the trailing zero pad. <c>IsDescendantOf</c>
/// reduces to a bit-prefix check, which an optimizer can rewrite to an
/// index range seek.</item>
/// <item><b>The prefix codes are sort-ordered, not frequency-optimal.</b>
/// <c>01</c> &lt; <c>100</c> &lt; <c>101</c> &lt; <c>110</c> &lt; <c>1110</c>
/// bit-wise, mapping to ordinal ranges [0..3] &lt; [4..7] &lt; [8..15] &lt;
/// [16..79] &lt; [80..]. This is <i>not</i> a Huffman code — Huffman
/// optimizes for compression, which would re-pick codes by ordinal frequency
/// and break the sort. Re-deriving "optimal" codes would break
/// <c>memcmp = DFS</c>.</item>
/// <item><b>The static bits in the <c>110</c> encoding</b> (position 6 = static
/// <c>0</c>, position 8 = static <c>1</c>) are load-bearing for round-trip.
/// Their precise role in the original design isn't pinned down by the
/// probe, but they almost certainly serve sort preservation across length
/// boundaries, self-delimiting ordinal boundaries during decoding, or
/// both. Treat them as inviolate: re-encoding without them would compress
/// better but risks breaking <c>memcmp = DFS</c> or the bit-prefix property.</item>
/// </list>
///
/// <para><b>Pointer for B-tree index work in the simulator</b>: implementing
/// a hierarchical index does <i>not</i> require this wire format — a
/// DFS-pre-order comparer on <see cref="HierarchyIdSqlType"/>'s
/// <c>int[][]</c> form (lexicographic compare with shorter-less) suffices,
/// since the simulator stores the decoded segment-array. This wire format
/// matters only for the separate, currently-deferred symmetric
/// <i>encoder</i> that would make <c>CAST(node AS varbinary)</c>
/// byte-identical with SQL Server. Keep the two pieces of work separate.</para>
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
/// <item>Prefix <c>110</c> (3 bits) → 6 value bits with 2 static bits → range [16..79], 12 bits</item>
/// </list>
///
/// <para><c>110</c>-prefix bit layout (12 bits): <c>110</c> + 2 high-value
/// bits + static <c>0</c> + 1 mid-value bit + static <c>1</c> + 3 low-value
/// bits + terminator <c>1</c>. See the index-relevant section above for
/// why those static bits matter.</para>
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
