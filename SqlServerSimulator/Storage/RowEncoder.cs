using System.Buffers.Binary;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Encodes a single row into the simulator's data-page record format.
/// The format is structurally aligned with SQL Server's in-row record layout
/// (TagA flags, fixed-length offset, fixed data, column count, NULL bitmap,
/// optional variable-length section) but the specific bit assignments are
/// simulator-defined.
/// </summary>
/// <remarks>
/// <para>
/// Structural shape is informed by public references on SQL Server's record
/// format: Paul Randal's "Inside the Storage Engine" blog series at sqlskills.com,
/// and Kalen Delaney et al., <em>Microsoft SQL Server Internals</em> (Microsoft Press).
/// SQL Server's exact bit layout in TagA/TagB is not publicly specified at the
/// byte level, so this implementation defines its own assignments and uses the
/// reference material only for the high-level shape (header, fixed section,
/// column count, NULL bitmap, variable section).
/// </para>
/// </remarks>
internal static class RowEncoder
{
    private const byte TagA_NullBitmap = 0x10;
    private const byte TagA_VarSection = 0x20;

    /// <summary>
    /// Encodes a row of values against a schema.
    /// </summary>
    /// <param name="schema">Per-column types; defines the row layout.</param>
    /// <param name="values">Per-column values; each value's type must match the corresponding schema entry (NULL is allowed regardless).</param>
    /// <remarks>
    /// <para>
    /// Layout for a row of <c>N</c> columns, of which <c>V</c> are variable-length:
    /// </para>
    /// <list type="table">
    /// <item><description>[0]                         TagA: 0x10 if V==0, 0x30 if V&gt;0 (NULL bitmap always present; var-length section conditional).</description></item>
    /// <item><description>[1]                         TagB: reserved (0x00).</description></item>
    /// <item><description>[2-3]                       Fixed-length data end offset (UInt16 LE) — equal to <c>4 + sum(fixed widths, with bit packing)</c>.</description></item>
    /// <item><description>[4 .. fixedEnd)             Fixed-length data, in schema order, only for fixed-length columns; NULL slots are zero-filled.</description></item>
    /// <item><description>[fixedEnd .. +2)            Column count N (UInt16 LE).</description></item>
    /// <item><description>[+ ceil(N/8))               NULL bitmap; column <c>i</c> is NULL iff bit <c>i mod 8</c> of byte <c>i / 8</c> is set. Includes both fixed and var columns.</description></item>
    /// <item><description>[bitmapEnd .. +2)           Var column count V (UInt16 LE) — only when V&gt;0.</description></item>
    /// <item><description>[+ 2*V)                     Var offset array — only when V&gt;0; entry <c>i</c> is the absolute byte position where var column <c>i</c>'s data ENDS. NULL var columns share the previous entry (zero-length data).</description></item>
    /// <item><description>[offsetArrayEnd ..)         Var-length data, packed in schema order — only when V&gt;0.</description></item>
    /// </list>
    /// <para>
    /// Bit columns share bytes within a contiguous run: each successive
    /// <c>bit</c> column in a run takes the next bit slot in the current byte,
    /// rolling over to a new byte every 8 bits. A non-bit fixed column ends
    /// the run; a subsequent <c>bit</c> column starts a fresh byte. NULL bit
    /// columns still occupy a slot (their value is undefined; the NULL bitmap
    /// authoritatively indicates the NULL). This matches SQL Server in spirit
    /// — bit columns share bytes — though the exact slot ordering is
    /// simulator-defined.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is empty, lengths differ, or a value's type doesn't match its schema entry.</exception>
    public static byte[] EncodeRow(ReadOnlySpan<SqlType> schema, ReadOnlySpan<SqlValue> values)
    {
        if (schema.Length == 0)
            throw new ArgumentException("Row must have at least one column.", nameof(schema));
        if (schema.Length != values.Length)
            throw new ArgumentException($"Schema has {schema.Length} columns but values has {values.Length}.", nameof(values));

        var n = schema.Length;
        var fixedSectionLength = 0;
        var varColumnCount = 0;
        var varDataLength = 0;
        var varByteCounts = new int[n]; // indexed by schema position; 0 for fixed columns and NULL var columns
        var bitsInRun = 0;

        for (var i = 0; i < n; i++)
        {
            if (!values[i].IsNull && values[i].Type != schema[i])
                throw new ArgumentException($"Value at column {i} has type {values[i].Type}, schema declares {schema[i]}.", nameof(values));

            if (schema[i] == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                    fixedSectionLength++;
                bitsInRun++;
            }
            else if (schema[i].IsFixedLength)
            {
                fixedSectionLength += schema[i].FixedLength;
                bitsInRun = 0;
            }
            else
            {
                varColumnCount++;
                if (!values[i].IsNull)
                {
                    var count = schema[i].GetVariableByteCount(values[i]);
                    varByteCounts[i] = count;
                    varDataLength += count;
                }
            }
        }

        var fixedEnd = 4 + fixedSectionLength;
        var nullBitmapLength = (n + 7) / 8;
        var bitmapStart = fixedEnd + 2;
        var bitmapEnd = bitmapStart + nullBitmapLength;
        var hasVar = varColumnCount > 0;
        var totalLength = bitmapEnd + (hasVar ? 2 + (2 * varColumnCount) + varDataLength : 0);

        var bytes = new byte[totalLength];

        bytes[0] = hasVar ? (byte)(TagA_NullBitmap | TagA_VarSection) : TagA_NullBitmap;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), checked((ushort)fixedEnd));

        var fixedOffset = 4;
        var bitByteOffset = -1;
        bitsInRun = 0;
        for (var i = 0; i < n; i++)
        {
            if (values[i].IsNull)
                bytes[bitmapStart + (i / 8)] |= (byte)(1 << (i % 8));

            if (schema[i] == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                {
                    bitByteOffset = fixedOffset;
                    fixedOffset++;
                }
                if (!values[i].IsNull && values[i].AsBoolean)
                    bytes[bitByteOffset] |= (byte)(1 << (bitsInRun % 8));
                bitsInRun++;
            }
            else if (schema[i].IsFixedLength)
            {
                var width = schema[i].FixedLength;
                if (!values[i].IsNull)
                    _ = schema[i].Encode(values[i], bytes.AsSpan(fixedOffset, width));
                fixedOffset += width;
                bitsInRun = 0;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(fixedEnd, 2), checked((ushort)n));

        if (hasVar)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitmapEnd, 2), checked((ushort)varColumnCount));

            var offsetArrayStart = bitmapEnd + 2;
            var dataPos = offsetArrayStart + (2 * varColumnCount);
            var varIndex = 0;
            for (var i = 0; i < n; i++)
            {
                if (schema[i].IsFixedLength)
                    continue;

                if (!values[i].IsNull)
                {
                    var width = varByteCounts[i];
                    _ = schema[i].Encode(values[i], bytes.AsSpan(dataPos, width));
                    dataPos += width;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offsetArrayStart + (2 * varIndex), 2), checked((ushort)dataPos));
                varIndex++;
            }
        }

        return bytes;
    }
}
