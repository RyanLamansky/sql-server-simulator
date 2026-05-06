using System.Buffers;
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

    internal const byte LobInlineMarker = 0x00;
    internal const byte LobPointerMarker = 0x01;

    /// <summary>
    /// Bytes added to a LOB-eligible value's variable-section payload when
    /// it goes inline: 1-byte marker (<see cref="LobInlineMarker"/>). Pointer
    /// form adds <see cref="LobPointerMarker"/> + 8 bytes
    /// (<c>Int32 chainHead</c> + <c>Int32 totalLength</c>) instead — see
    /// <see cref="LobPointerSize"/>.
    /// </summary>
    internal const int LobPointerSize = 1 + 4 + 4;

    /// <summary>
    /// Encodes a row of values against a <see cref="SqlType"/>-only schema.
    /// LOB-eligibility is determined by <see cref="SqlType.IsLob"/> alone
    /// (i.e. <c>text</c>/<c>ntext</c>/<c>image</c>); MAX siblings of
    /// varchar/nvarchar/varbinary aren't reachable through this overload
    /// because they need <see cref="HeapColumn.MaxLength"/> to surface as
    /// LOB-eligible. Callers with full column metadata should use the
    /// <see cref="EncodeRow(ReadOnlySpan{HeapColumn}, ReadOnlySpan{SqlValue}, Heap?)"/>
    /// overload to opt into LOB-chain storage on a <see cref="Heap"/>.
    /// </summary>
    public static byte[] EncodeRow(ReadOnlySpan<SqlType> schema, ReadOnlySpan<SqlValue> values)
    {
        var columns = new HeapColumn[schema.Length];
        for (var i = 0; i < schema.Length; i++)
            columns[i] = new HeapColumn(string.Empty, schema[i], maxLength: null, nullable: true);
        return EncodeRow(columns, values, lobStore: null);
    }

    /// <summary>
    /// Encodes a row of values against a column-aware schema. When
    /// <paramref name="lobStore"/> is non-null, every LOB-eligible non-NULL
    /// value (<see cref="HeapColumn.IsLob"/>) is allocated to a LOB chain
    /// in the store and the row carries an 8-byte pointer in its place.
    /// When <paramref name="lobStore"/> is null, LOB-eligible values stay
    /// inline (with a 1-byte marker prefix); the row stays self-contained
    /// but is bounded by the 65535-byte var-offset cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Format layers on the existing var-section structure: each
    /// LOB-eligible column's variable-section bytes start with a 1-byte
    /// marker (<see cref="LobInlineMarker"/> = inline content follows;
    /// <see cref="LobPointerMarker"/> = 8 bytes of pointer follow). NULL
    /// values still surface through the row's NULL bitmap and contribute
    /// 0 bytes to the var section, just like non-LOB var columns.
    /// </para>
    /// </remarks>
    public static byte[] EncodeRow(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<SqlValue> values, Heap? lobStore = null)
    {
        if (schema.Length == 0)
            throw new ArgumentException("Row must have at least one column.", nameof(schema));
        if (schema.Length != values.Length)
            throw new ArgumentException($"Schema has {schema.Length} columns but values has {values.Length}.", nameof(values));

        var n = schema.Length;
        var fixedSectionLength = 0;
        var varColumnCount = 0;
        var varDataLength = 0;
        var varByteCounts = new int[n];
        // Pre-resolved LOB pointers for off-row values: indexed by schema
        // position; entry is (chainHead, totalLength) when the column is
        // LOB-eligible AND lobStore is provided AND the value is non-NULL.
        var lobPointers = new (int Head, int Length)?[n];
        var bitsInRun = 0;

        for (var i = 0; i < n; i++)
        {
            if (!values[i].IsNull && values[i].Type != schema[i].Type)
                throw new ArgumentException($"Value at column {i} has type {values[i].Type}, schema declares {schema[i].Type}.", nameof(values));

            if (schema[i].Type == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                    fixedSectionLength++;
                bitsInRun++;
            }
            else if (schema[i].Type.IsFixedLength)
            {
                fixedSectionLength += schema[i].Type.FixedLength;
                bitsInRun = 0;
            }
            else
            {
                varColumnCount++;
                if (!values[i].IsNull)
                {
                    var byteCount = ComputeVarByteCount(schema[i], values[i], lobStore, out var pointer);
                    varByteCounts[i] = byteCount;
                    varDataLength += byteCount;
                    lobPointers[i] = pointer;
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

            if (schema[i].Type == SqlType.Bit)
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
            else if (schema[i].Type.IsFixedLength)
            {
                var width = schema[i].Type.FixedLength;
                if (!values[i].IsNull)
                    _ = schema[i].Type.Encode(values[i], bytes.AsSpan(fixedOffset, width));
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
                if (schema[i].Type.IsFixedLength)
                    continue;

                if (!values[i].IsNull)
                {
                    var width = varByteCounts[i];
                    WriteVarPayload(schema[i], values[i], lobPointers[i], bytes.AsSpan(dataPos, width));
                    dataPos += width;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offsetArrayStart + (2 * varIndex), 2), checked((ushort)dataPos));
                varIndex++;
            }
        }

        return bytes;
    }

    /// <summary>
    /// Computes the encoded byte count for a single variable-length column's
    /// non-NULL value. For non-LOB columns this is the type's natural
    /// byte-count; for LOB-eligible columns it adds a 1-byte marker prefix
    /// and routes oversize values to <paramref name="lobStore"/> when
    /// available — returning <see cref="LobPointerSize"/> for the off-row
    /// path. The pre-allocated chain pointer is returned via
    /// <paramref name="pointer"/> so the second pass can write it without
    /// re-scanning the value.
    /// </summary>
    /// <remarks>
    /// The off-row path encodes into a scratch buffer that's stack-allocated
    /// for small values and rented from <see cref="ArrayPool{T}.Shared"/>
    /// for large ones — varchar(MAX) values can be megabytes, so unconditional
    /// stackalloc would overflow.
    /// </remarks>
    private static int ComputeVarByteCount(HeapColumn column, SqlValue value, Heap? lobStore, out (int Head, int Length)? pointer)
    {
        var natural = column.Type.GetVariableByteCount(value);
        if (!column.IsLob)
        {
            pointer = null;
            return natural;
        }

        if (lobStore is null)
        {
            // Inline mode without a LOB store: full bytes plus the inline
            // marker. Caller is responsible for not exceeding row-format
            // limits with oversize values.
            pointer = null;
            return 1 + natural;
        }

        // Off-row mode: allocate the chain up front so the byte-count phase
        // and the byte-write phase agree on the layout. The marker byte plus
        // chain head + length is a fixed-size payload regardless of value
        // size.
        int head;
        if (natural <= Heap.LobScratchStackThreshold)
        {
            Span<byte> chunk = stackalloc byte[natural];
            _ = column.Type.Encode(value, chunk);
            head = lobStore.AllocateLobChain(chunk);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(natural);
            try
            {
                var chunk = rented.AsSpan(0, natural);
                _ = column.Type.Encode(value, chunk);
                head = lobStore.AllocateLobChain(chunk);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        pointer = (head, natural);
        return LobPointerSize;
    }

    /// <summary>
    /// Writes the encoded bytes for a single variable-length column's
    /// non-NULL value into <paramref name="destination"/>. The destination's
    /// length must equal the value's previously computed byte count
    /// (<see cref="ComputeVarByteCount"/>).
    /// </summary>
    private static void WriteVarPayload(HeapColumn column, SqlValue value, (int Head, int Length)? pointer, Span<byte> destination)
    {
        if (!column.IsLob)
        {
            _ = column.Type.Encode(value, destination);
            return;
        }

        if (pointer is { } p)
        {
            destination[0] = LobPointerMarker;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(1, 4), p.Head);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(5, 4), p.Length);
            return;
        }

        destination[0] = LobInlineMarker;
        _ = column.Type.Encode(value, destination[1..]);
    }
}
