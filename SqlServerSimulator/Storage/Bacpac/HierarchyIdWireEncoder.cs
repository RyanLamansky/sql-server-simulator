namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Exact inverse of <see cref="HierarchyIdWireDecoder"/> over its supported
/// ordinal domain: encodes a hierarchyid path (the simulator's segment-array
/// form, <see cref="HierarchyIdSqlType"/>) into SQL Server's OrdPath binary
/// wire encoding — the byte form real SQL Server produces on
/// <c>CAST(node AS varbinary(max))</c> and that DacFx reads over the wire /
/// writes into a BACPAC's BCP data files. Feeds the TDS UDT wire form and
/// <c>DATALENGTH</c>.
/// </summary>
/// <remarks>
/// <para>Probe-anchored against the SQL Server 2025 reference (2026-07-16 via
/// <c>SELECT CAST(CAST('/N/' AS hierarchyid) AS varbinary(892))</c>), matching
/// the four positive-ordinal tiers <see cref="HierarchyIdWireDecoder"/>
/// documents. Each ordinal is a self-delimiting bit sequence (prefix + value
/// bits + a terminator bit); sequences concatenate left-to-right and the byte
/// stream is zero-padded to a byte boundary.</para>
///
/// <list type="table">
/// <listheader><term>Range</term><description>Template (V = value bit, MSB→LSB; final bit = terminator)</description></listheader>
/// <item><term>0..3</term><description><c>01 VV 1</c> — value = N</description></item>
/// <item><term>4..7</term><description><c>100 VV 1</c> — value = N − 4</description></item>
/// <item><term>8..15</term><description><c>101 VVV 1</c> — value = N − 8</description></item>
/// <item><term>16..79</term><description><c>110 VV 0 V 1 VVV 1</c> — value = N − 16 (split 2 + 1 + 3, static <c>0</c>/<c>1</c> sub-tier markers)</description></item>
/// </list>
///
/// <para><b>Dotted sub-ordinals</b> (a segment with more than one label, e.g.
/// <c>/1/2.3/</c> = <c>[[1], [2, 3]]</c>): within a segment every label but the
/// last is encoded as <c>ordinal + 1</c> with its terminator bit cleared to
/// <c>0</c>, and the last label is encoded normally with terminator <c>1</c> —
/// the order-preserving trick that sorts a dotted continuation after the plain
/// node and before its next sibling. Probe-confirmed 2026-07-16 across the tier
/// boundary (<c>/3.1/</c> = <c>0x8160</c>: the non-final <c>3</c> encodes as the
/// <c>4</c> template). The <c>+ 1</c> can push a non-final ordinal past 79 into
/// the unmodeled tier — that raises <see cref="NotSupportedException"/> like any
/// out-of-range ordinal.</para>
///
/// <para><b>Domain</b>: positive ordinals 0..79 only, matching the decoder.
/// Ordinals ≥ 80 and the negative range (a separate <c>0</c>-prefixed tier set,
/// unmodeled) raise <see cref="NotSupportedException"/>. The decoder cannot read
/// dotted forms back (it treats every label as its own single-label segment and
/// discards terminator bits), so a dotted path encoded here does not round-trip
/// through <see cref="HierarchyIdWireDecoder.Decode"/> — the encoder is the
/// exact inverse only over single-label-segment paths, which is the decoder's
/// full domain. See <c>docs/claude/hierarchyid.md</c>.</para>
/// </remarks>
internal static class HierarchyIdWireEncoder
{
    /// <summary>
    /// Encodes <paramref name="path"/> (segment array; each segment a
    /// dot-separated label tuple) into the OrdPath binary form. The empty path
    /// (canonical <c>/</c> root) encodes to zero bytes.
    /// </summary>
    public static byte[] Encode(int[][] path)
    {
        var writer = new BitWriter();
        foreach (var segment in path)
        {
            if (segment.Length == 0)
                throw new NotSupportedException("hierarchyid binary encoder: a path segment must have at least one label.");
            for (var i = 0; i < segment.Length; i++)
            {
                var isLast = i == segment.Length - 1;
                WriteOrdinal(ref writer, isLast ? segment[i] : segment[i] + 1, terminator: isLast);
            }
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Writes one ordinal's prefix + value bits followed by
    /// <paramref name="terminator"/>. <paramref name="ordinal"/> is the encoded
    /// value (already carrying the non-final <c>+ 1</c> shift, if any), so it
    /// must land in the modeled 0..79 range.
    /// </summary>
    private static void WriteOrdinal(ref BitWriter writer, int ordinal, bool terminator)
    {
        switch (ordinal)
        {
            case < 0:
                throw new NotSupportedException("hierarchyid binary encoder doesn't yet handle the negative-ordinal range.");
            case <= 3:
                writer.WriteBits(0b01, 2);
                writer.WriteBits(ordinal, 2);
                break;
            case <= 7:
                writer.WriteBits(0b100, 3);
                writer.WriteBits(ordinal - 4, 2);
                break;
            case <= 15:
                writer.WriteBits(0b101, 3);
                writer.WriteBits(ordinal - 8, 3);
                break;
            case <= 79:
                var value = ordinal - 16;
                writer.WriteBits(0b110, 3);
                writer.WriteBits((value >> 4) & 0b11, 2);
                writer.WriteBit(false);
                writer.WriteBit(((value >> 3) & 1) == 1);
                writer.WriteBit(true);
                writer.WriteBits(value & 0b111, 3);
                break;
            default:
                throw new NotSupportedException("hierarchyid binary encoder doesn't yet handle ordinals >= 80.");
        }

        writer.WriteBit(terminator);
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
        public void WriteBits(int value, int count)
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
}
