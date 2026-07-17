namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>hierarchyid</c> OrdPath binary encoding: the byte form a real
/// server stores, compares (unsigned bytewise = depth-first tree order),
/// serializes over the TDS UDT wire, and reports via <c>DATALENGTH</c>. This is
/// the simulator's canonical in-memory representation for <c>hierarchyid</c>
/// (<see cref="SqlValue.FromHierarchyId(int[][])"/> encodes into it,
/// <see cref="SqlValue.AsHierarchyId"/> decodes back), so a stored value's page
/// bytes, its <c>CAST(node AS varbinary)</c> bytes, and its wire bytes are all
/// the same buffer with zero re-encoding.
/// </summary>
/// <remarks>
/// <para>Each label of a path is a self-delimiting bit sequence: a prefix-free
/// tier code, then value bits interleaved with fixed structural bits, then a
/// terminator bit. Labels concatenate left-to-right with no separator and the
/// byte stream is zero-padded to a byte boundary; unsigned bytewise comparison
/// of two encodings therefore equals depth-first pre-order traversal (a shorter
/// ancestor sorts before every descendant because every label carries a 1-bit,
/// so a descendant always differs upward from the ancestor's zero pad).</para>
///
/// <para>A <b>dotted sub-ordinal</b> segment (<c>/1/2.3/</c> = <c>[[1],[2,3]]</c>)
/// exploits the terminator: within a segment every label but the last encodes
/// <c>ordinal + 1</c> with its terminator cleared to <c>0</c>, and the last
/// label encodes normally with terminator <c>1</c> — the order-preserving trick
/// that sorts a dotted continuation after the plain node and before its next
/// sibling. Probe-anchored against SQL Server 2025 (2026-07-17).</para>
///
/// <para><b>Modeled tier domain</b>: ordinals <c>-4168 .. 5199</c> (the twelve
/// tiers in <see cref="Tiers"/>). Every tier boundary is byte-anchored by a live
/// probe (see the encoder/decoder tests). Ordinals outside that window (the
/// wider 6-byte tiers real supports) raise <see cref="NotSupportedException"/>
/// on encode/decode; the storage layer still round-trips their raw bytes
/// opaquely (BACPAC import stores them verbatim), only <c>ToString()</c> / the
/// instance methods need a decode and so surface the limitation.</para>
///
/// <para>Tier table (V = value bit group MSB→LSB, digits = fixed structural
/// bits, final bit = terminator; value spans the V groups, ordinal = base +
/// value):</para>
///
/// <list type="table">
/// <listheader><term>Prefix</term><description>Layout / range</description></listheader>
/// <item><term>01</term><description><c>VV 1</c> — 0..3</description></item>
/// <item><term>100</term><description><c>VV 1</c> — 4..7</description></item>
/// <item><term>101</term><description><c>VVV 1</c> — 8..15</description></item>
/// <item><term>110</term><description><c>VV 0 V 1 VVV 1</c> — 16..79</description></item>
/// <item><term>1110</term><description><c>VVV 0 VVV 0 V 1 VVV 1</c> — 80..1103</description></item>
/// <item><term>11110</term><description><c>VVVVV 0 VVV 0 V 1 VVV 1</c> — 1104..5199</description></item>
/// <item><term>0011</term><description><c>VVVV 1</c> — -8..-1</description></item>
/// <item><term>0010</term><description><c>VV 0 V 1 VVV 1</c> — -72..-9</description></item>
/// <item><term>00011011</term><description><c>VVV 0 VVV 0 V 1 VVV 1</c> — -1096..-73</description></item>
/// <item><term>00011010</term><description>same layout — -2120..-1097</description></item>
/// <item><term>00011001</term><description>same layout — -3144..-2121</description></item>
/// <item><term>00011000</term><description>same layout — -4168..-3145</description></item>
/// </list>
/// </remarks>
internal static class HierarchyIdOrdPath
{
    /// <summary>
    /// One prefix-free ordinal tier. <see cref="Groups"/> are the value-bit
    /// group widths MSB→LSB (their concatenation is the tier's value);
    /// <see cref="Separators"/> is the fixed structural bit written after each
    /// group, the last entry being the terminator (always <c>1</c>).
    /// <see cref="Base"/> maps value to ordinal (<c>ordinal = Base + value</c>).
    /// </summary>
    private sealed class Tier(uint prefix, int prefixBits, int[] groups, int[] separators, long @base, long minOrdinal, long maxOrdinal)
    {
        public readonly uint Prefix = prefix;
        public readonly int PrefixBits = prefixBits;
        public readonly int[] Groups = groups;
        public readonly int[] Separators = separators;
        public readonly long Base = @base;
        public readonly long MinOrdinal = minOrdinal;
        public readonly long MaxOrdinal = maxOrdinal;
        public readonly int ValueBits = Sum(groups);

        private static int Sum(int[] values)
        {
            var total = 0;
            foreach (var v in values)
                total += v;
            return total;
        }
    }

    // Tail shared by the 6-value-bit tiers (110 / 0010): VV 0 V 1 VVV 1.
    private static readonly int[] Groups6 = [2, 1, 3];
    private static readonly int[] Sep6 = [0, 1, 1];

    // Tail shared by the 10-value-bit tiers (1110 and the four negative 8-bit
    // prefixes): VVV 0 VVV 0 V 1 VVV 1.
    private static readonly int[] Groups10 = [3, 3, 1, 3];
    private static readonly int[] Sep10 = [0, 0, 1, 1];

    private static readonly Tier[] Tiers =
    [
        new(0b01, 2, [2], [1], 0, 0, 3),
        new(0b100, 3, [2], [1], 4, 4, 7),
        new(0b101, 3, [3], [1], 8, 8, 15),
        new(0b110, 3, Groups6, Sep6, 16, 16, 79),
        new(0b1110, 4, Groups10, Sep10, 80, 80, 1103),
        new(0b11110, 5, [5, 3, 1, 3], [0, 0, 1, 1], 1104, 1104, 5199),
        new(0b0011, 4, [4], [1], -16, -8, -1),
        new(0b0010, 4, Groups6, Sep6, -72, -72, -9),
        new(0b00011011, 8, Groups10, Sep10, -1096, -1096, -73),
        new(0b00011010, 8, Groups10, Sep10, -2120, -2120, -1097),
        new(0b00011001, 8, Groups10, Sep10, -3144, -3144, -2121),
        new(0b00011000, 8, Groups10, Sep10, -4168, -4168, -3145),
    ];

    /// <summary>
    /// Encodes a path (segment array; each segment a dot-separated label tuple)
    /// into the OrdPath binary form. The empty path (canonical <c>/</c> root)
    /// encodes to zero bytes.
    /// </summary>
    /// <exception cref="NotSupportedException">A label falls outside the modeled tier domain.</exception>
    public static byte[] Encode(int[][] path)
    {
        var writer = new BitWriter();
        foreach (var segment in path)
        {
            if (segment.Length == 0)
                throw new NotSupportedException("hierarchyid OrdPath encoder: a path segment must have at least one label.");
            for (var i = 0; i < segment.Length; i++)
            {
                var isLast = i == segment.Length - 1;
                WriteLabel(ref writer, isLast ? segment[i] : segment[i] + 1, terminator: isLast);
            }
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Decodes an OrdPath payload into the segment-array form. Empty input →
    /// empty path (the canonical <c>/</c> root). Reads dotted multi-label
    /// segments back via the terminator bit. Does not validate that the input
    /// is the canonical encoding — see <see cref="DecodeCanonical"/> for the
    /// strict form the <c>CAST(varbinary AS hierarchyid)</c> path needs.
    /// </summary>
    /// <exception cref="NotSupportedException">A label uses an unmodeled tier.</exception>
    /// <exception cref="InvalidDataException">The bit stream is malformed (read past end / dangling non-final label).</exception>
    public static int[][] Decode(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            return [];
        var reader = new BitReader(source);
        var segments = new List<int[]>();
        var current = new List<int>();
        while (reader.HasMoreNonZeroBits())
        {
            var (ordinal, terminator) = ReadLabel(ref reader);
            if (terminator)
            {
                current.Add(ordinal);
                segments.Add([.. current]);
                current.Clear();
            }
            else
            {
                // Non-final labels are encoded as ordinal + 1 with terminator 0.
                current.Add(ordinal - 1);
            }
        }
        return current.Count != 0
            ? throw new InvalidDataException("hierarchyid OrdPath decoder: path ended on a non-final (dotted) label with no terminator.")
            : [.. segments];
    }

    /// <summary>
    /// Strictly decodes a payload that must be the exact canonical OrdPath
    /// encoding, matching SQL Server's <c>CAST(varbinary AS hierarchyid)</c>,
    /// which rejects any non-canonical byte string (wrong pad bits, non-minimal
    /// tier, trailing garbage). Implemented by decoding then re-encoding and
    /// requiring byte equality, so canonicalization is enforced by construction.
    /// </summary>
    /// <exception cref="SimulatedSqlException">The input is not a canonical hierarchyid encoding (Msg 6522).</exception>
    public static int[][] DecodeCanonical(ReadOnlySpan<byte> source)
    {
        int[][] path;
        try
        {
            path = Decode(source);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            throw SimulatedSqlException.InvalidHierarchyIdInput(Convert.ToHexString(source));
        }
        return Encode(path).AsSpan().SequenceEqual(source)
            ? path
            : throw SimulatedSqlException.InvalidHierarchyIdInput(Convert.ToHexString(source));
    }

    private static void WriteLabel(ref BitWriter writer, long ordinal, bool terminator)
    {
        var tier = FindTierByOrdinal(ordinal)
            ?? throw new NotSupportedException($"hierarchyid OrdPath encoder: ordinal {ordinal} is outside the modeled tier range [-4168, 5199].");
        writer.WriteBits(tier.Prefix, tier.PrefixBits);
        var value = ordinal - tier.Base;
        var offset = tier.ValueBits;
        for (var g = 0; g < tier.Groups.Length; g++)
        {
            offset -= tier.Groups[g];
            writer.WriteBits((uint)((value >> offset) & ((1 << tier.Groups[g]) - 1)), tier.Groups[g]);
            var isTerminator = g == tier.Groups.Length - 1;
            writer.WriteBit(isTerminator ? terminator : tier.Separators[g] == 1);
        }
    }

    private static (int Ordinal, bool Terminator) ReadLabel(ref BitReader reader)
    {
        var tier = MatchTier(ref reader);
        long value = 0;
        var terminator = false;
        for (var g = 0; g < tier.Groups.Length; g++)
        {
            value = (value << tier.Groups[g]) | reader.ReadBits(tier.Groups[g]);
            terminator = reader.ReadBit();
        }
        return (checked((int)(tier.Base + value)), terminator);
    }

    /// <summary>
    /// Reads a prefix-free tier code by consuming bits until a modeled prefix
    /// matches. Prefixes are prefix-free, so the first full match is unambiguous.
    /// </summary>
    private static Tier MatchTier(ref BitReader reader)
    {
        uint prefix = 0;
        for (var bits = 1; bits <= 8; bits++)
        {
            prefix = (prefix << 1) | (reader.ReadBit() ? 1u : 0u);
            foreach (var tier in Tiers)
            {
                if (tier.PrefixBits == bits && tier.Prefix == prefix)
                    return tier;
            }
        }
        throw new NotSupportedException("hierarchyid OrdPath decoder: unrecognized ordinal tier prefix (unmodeled wide/6-byte tier).");
    }

    private static Tier? FindTierByOrdinal(long ordinal)
    {
        foreach (var tier in Tiers)
        {
            if (ordinal >= tier.MinOrdinal && ordinal <= tier.MaxOrdinal)
                return tier;
        }
        return null;
    }

    /// <summary>
    /// Bit-stream writer accumulating bits MSB-first within each byte (the SQL
    /// Server hierarchyid wire convention); the final partial byte is
    /// zero-padded, which the decoder reads as the end-of-path tail.
    /// </summary>
    private struct BitWriter
    {
        private readonly List<byte> bytes = new(8);
        private int current;
        private int bitCount;

        public BitWriter()
        {
        }

        public void WriteBit(bool bit)
        {
            this.current = (this.current << 1) | (bit ? 1 : 0);
            this.bitCount++;
            if (this.bitCount == 8)
            {
                this.bytes.Add((byte)this.current);
                this.current = 0;
                this.bitCount = 0;
            }
        }

        /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, MSB first.</summary>
        public void WriteBits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                this.WriteBit(((value >> i) & 1) == 1);
        }

        public readonly byte[] ToArray()
        {
            if (this.bitCount == 0)
                return [.. this.bytes];
            var padded = new byte[this.bytes.Count + 1];
            this.bytes.CopyTo(padded);
            padded[^1] = (byte)(this.current << (8 - this.bitCount));
            return padded;
        }
    }

    /// <summary>
    /// Bit-stream reader over a byte source. Bits are read MSB-first within
    /// each byte (matches the SQL Server hierarchyid wire convention).
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> bytes = source;
        private int bitOffset = 0;

        public bool ReadBit()
        {
            if (this.bitOffset >= this.bytes.Length * 8)
                throw new InvalidDataException("hierarchyid OrdPath decoder: read past end of input.");
            var byteIndex = this.bitOffset >> 3;
            var bitInByte = 7 - (this.bitOffset & 7);
            this.bitOffset++;
            return ((this.bytes[byteIndex] >> bitInByte) & 1) == 1;
        }

        /// <summary>Reads <paramref name="count"/> bits as an unsigned integer (first bit read = MSB).</summary>
        public uint ReadBits(int count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++)
                value = (value << 1) | (this.ReadBit() ? 1u : 0u);
            return value;
        }

        /// <summary>True if any remaining unread bit is 1 — separates a real label from the zero pad tail.</summary>
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
