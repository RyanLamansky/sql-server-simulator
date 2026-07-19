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
    private const byte TagA_WideVarOffsets = 0x40;

    /// <summary>
    /// First byte of every non-NULL variable-length column's payload.
    /// <see cref="VarInlineMarker"/> means the rest of the payload is the
    /// value's bytes; <see cref="VarPointerMarker"/> means the rest is an
    /// 8-byte chain pointer (<c>Int32 chainHead</c> + <c>Int32 totalLength</c>)
    /// to off-row storage.
    /// </summary>
    internal const byte VarInlineMarker = 0x00;
    internal const byte VarPointerMarker = 0x01;

    /// <summary>
    /// Total bytes a pointer-form var-section entry occupies: 1-byte marker
    /// + <c>Int32 chainHead</c> + <c>Int32 totalLength</c>. Used for both
    /// always-LOB columns (<c>text</c>/<c>ntext</c>/<c>image</c>),
    /// MAX siblings, and overflowed bounded var columns.
    /// </summary>
    internal const int VarPointerSize = 1 + 4 + 4;

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
    /// <paramref name="lobStore"/> is non-null, the encoder may store
    /// values off-row in a chain on the store: LOB-eligible columns
    /// (<see cref="HeapColumn.IsLob"/>) go off-row unconditionally, and
    /// bounded variable-length columns are pushed off-row greedily — largest
    /// first — until the in-row size fits under <see cref="Heap.MaxRowSize"/>.
    /// When <paramref name="lobStore"/> is null, every variable-length
    /// value stays inline; the row stays self-contained but is bounded by
    /// the 65535-byte var-offset cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every non-NULL variable-length column's payload starts with a 1-byte
    /// marker (<see cref="VarInlineMarker"/> = inline content follows;
    /// <see cref="VarPointerMarker"/> = 8 bytes of pointer follow). NULL
    /// values still surface through the row's NULL bitmap and contribute
    /// 0 bytes to the var section.
    /// </para>
    /// </remarks>
    public static byte[] EncodeRow(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<SqlValue> values, Heap? lobStore = null)
    {
        if (schema.Length == 0)
            throw new ArgumentException("Row must have at least one column.", nameof(schema));
        if (schema.Length != values.Length)
            throw new ArgumentException($"Schema has {schema.Length} columns but values has {values.Length}.", nameof(values));

        // String/binary type instances are interned by (length, collation,
        // coercibility), so two same-family types with different collation
        // pinning are distinct references. Reference equality is too strict
        // for value-vs-column matching: cells built via FromVarchar /
        // FromNVarchar / FromVarbinary land on the length-unspecified
        // baseline form (the declared cap lives on the schema's HeapColumn,
        // not the cell type); catalog-view cells built via SqlType.GetChar /
        // GetNChar carry the baseline collation while the catalog column
        // pins Latin1_General_CI_AS_KS_WS. Both fall through to compatible
        // encoding because the byte representation depends on character
        // data and length, not on collation or coercibility tags. Var-family
        // pairs accept any length (cap lives on HeapColumn); char/nchar
        // pairs require matching length because the fixed-length encoder
        // reads exactly that many bytes.
        static bool IsCompatibleColumnType(SqlType valueType, SqlType columnType) =>
            valueType == columnType
            || (valueType is VarcharSqlType && columnType is VarcharSqlType)
            || (valueType is NVarcharSqlType && columnType is NVarcharSqlType)
            || (valueType is VarbinarySqlType && columnType is VarbinarySqlType)
            || (valueType is CharSqlType vCh && columnType is CharSqlType cCh && vCh.length == cCh.length)
            || (valueType is NCharSqlType vNCh && columnType is NCharSqlType cNCh && vNCh.length == cNCh.length);

        var n = schema.Length;
        var fixedSectionLength = 0;
        var varColumnCount = 0;
        var varDataLength = 0;
        var varByteCounts = new int[n];
        // Pre-resolved off-row pointers for values pushed to a chain on
        // <paramref name="lobStore"/>: indexed by schema position; entry is
        // (chainHead, totalLength) when the value is non-NULL AND the encoder
        // chose to store it off-row (always for LOB-eligible columns when a
        // store is provided; for bounded var columns when the row otherwise
        // wouldn't fit).
        var chainPointers = new (int Head, int Length)?[n];
        var bitsInRun = 0;

        for (var i = 0; i < n; i++)
        {
            var t = schema[i].Type;
            if (!values[i].IsNull && !IsCompatibleColumnType(values[i].Type, t))
                throw new ArgumentException($"Value at column {i} has type {values[i].Type}, schema declares {t}.", nameof(values));

            if (t == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                    fixedSectionLength++;
                bitsInRun++;
            }
            else if (t.IsFixedLength)
            {
                fixedSectionLength += t.FixedLength;
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
                    chainPointers[i] = pointer;
                }
            }
        }

        var fixedEnd = 4 + fixedSectionLength;
        var nullBitmapLength = (n + 7) / 8;
        var bitmapStart = fixedEnd + 2;
        var bitmapEnd = bitmapStart + nullBitmapLength;
        var hasVar = varColumnCount > 0;
        var varOffsetEntrySize = 2;
        var headerOverhead = bitmapEnd + (hasVar ? 2 + (varOffsetEntrySize * varColumnCount) : 0);
        var totalLength = headerOverhead + varDataLength;

        // Greedy row-overflow push: when the encoded row would exceed
        // <see cref="Heap.MaxRowSize"/>, repeatedly move the largest still-inline
        // bounded variable-length value to a chain on <paramref name="lobStore"/>
        // until the row fits or no more candidates remain. LOB-eligible columns
        // are already off-row at this point; only bounded varchar/nvarchar/
        // varbinary inline values are eligible to push.
        if (lobStore is not null && totalLength > Heap.MaxRowSize)
        {
            while (totalLength > Heap.MaxRowSize)
            {
                var pickIndex = -1;
                var pickInlinePayload = -1;
                for (var i = 0; i < n; i++)
                {
                    if (chainPointers[i] is not null || values[i].IsNull)
                        continue;
                    if (schema[i].Type == SqlType.Bit || schema[i].Type.IsFixedLength)
                        continue;
                    var inlinePayload = varByteCounts[i] - 1;
                    if (inlinePayload > pickInlinePayload)
                    {
                        pickIndex = i;
                        pickInlinePayload = inlinePayload;
                    }
                }
                if (pickIndex < 0)
                    break;

                var head = AllocateChainForValue(schema[pickIndex], values[pickIndex], lobStore, pickInlinePayload);
                chainPointers[pickIndex] = (head, pickInlinePayload);
                varDataLength -= varByteCounts[pickIndex] - VarPointerSize;
                varByteCounts[pickIndex] = VarPointerSize;
                totalLength = headerOverhead + varDataLength;
            }

            if (totalLength > Heap.MaxRowSize)
                throw SimulatedSqlException.RowSizeExceedsAllowableMaximum(totalLength, Heap.MaxRowSize);
        }

        // When no lobStore is provided (projection result rows are the main
        // path here — they have no associated heap) and the var section
        // would push the offset entries past the 16-bit cap, widen the
        // offset entries to 4 bytes. Heap-stored rows always stay under
        // the 8060-byte MaxRowSize so they keep the legacy 2-byte width.
        if (hasVar && totalLength > 65535)
        {
            varOffsetEntrySize = 4;
            headerOverhead = bitmapEnd + 2 + (varOffsetEntrySize * varColumnCount);
            totalLength = headerOverhead + varDataLength;
        }

        var bytes = new byte[totalLength];

        bytes[0] = hasVar
            ? (byte)(TagA_NullBitmap | TagA_VarSection | (varOffsetEntrySize == 4 ? TagA_WideVarOffsets : 0))
            : TagA_NullBitmap;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), checked((ushort)fixedEnd));

        var fixedOffset = 4;
        var bitByteOffset = -1;
        bitsInRun = 0;
        for (var i = 0; i < n; i++)
        {
            if (values[i].IsNull)
                bytes[bitmapStart + (i / 8)] |= (byte)(1 << (i % 8));

            var t = schema[i].Type;
            if (t == SqlType.Bit)
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
            else if (t.IsFixedLength)
            {
                var width = t.FixedLength;
                if (!values[i].IsNull)
                    _ = t.Encode(values[i], bytes.AsSpan(fixedOffset, width));
                fixedOffset += width;
                bitsInRun = 0;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(fixedEnd, 2), checked((ushort)n));

        if (hasVar)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitmapEnd, 2), checked((ushort)varColumnCount));

            var offsetArrayStart = bitmapEnd + 2;
            var dataPos = offsetArrayStart + (varOffsetEntrySize * varColumnCount);
            var varIndex = 0;
            for (var i = 0; i < n; i++)
            {
                if (schema[i].Type.IsFixedLength)
                    continue;

                if (!values[i].IsNull)
                {
                    var width = varByteCounts[i];
                    WriteVarPayload(schema[i], values[i], chainPointers[i], bytes.AsSpan(dataPos, width));
                    dataPos += width;
                }

                if (varOffsetEntrySize == 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsetArrayStart + (4 * varIndex), 4), checked((uint)dataPos));
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offsetArrayStart + (2 * varIndex), 2), checked((ushort)dataPos));
                varIndex++;
            }
        }

        return bytes;
    }

    /// <summary>
    /// Computes the encoded byte count for a single variable-length column's
    /// non-NULL value: 1-byte marker + natural inline bytes by default, or
    /// <see cref="VarPointerSize"/> bytes when the value is being stored
    /// off-row. LOB-eligible columns (<see cref="HeapColumn.IsLob"/>) go
    /// off-row whenever <paramref name="lobStore"/> is provided; bounded
    /// var columns start inline and may be moved off-row by the row-overflow
    /// pass in the caller. The pre-allocated chain pointer is returned via
    /// <paramref name="pointer"/> so the second pass can write it without
    /// re-scanning the value.
    /// </summary>
    private static int ComputeVarByteCount(HeapColumn column, SqlValue value, Heap? lobStore, out (int Head, int Length)? pointer)
    {
        var natural = column.Type.GetVariableByteCount(value);

        if (column.IsLob && lobStore is not null)
        {
            var head = AllocateChainForValue(column, value, lobStore, natural);
            pointer = (head, natural);
            return VarPointerSize;
        }

        pointer = null;
        return 1 + natural;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> into a scratch buffer and allocates
    /// it as a chain on <paramref name="lobStore"/>, returning the chain's
    /// head page index. The scratch buffer is stack-allocated for small
    /// values and rented from <see cref="ArrayPool{T}.Shared"/> for larger
    /// ones — <c>varchar(MAX)</c> and overflowing bounded values can be
    /// megabytes, so unconditional stackalloc would overflow.
    /// </summary>
    private static int AllocateChainForValue(HeapColumn column, SqlValue value, Heap lobStore, int payloadLength)
    {
        if (payloadLength <= Heap.LobScratchStackThreshold)
        {
            Span<byte> chunk = stackalloc byte[payloadLength];
            _ = column.Type.Encode(value, chunk);
            return lobStore.AllocateLobChain(chunk);
        }

        var rented = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            var chunk = rented.AsSpan(0, payloadLength);
            _ = column.Type.Encode(value, chunk);
            return lobStore.AllocateLobChain(chunk);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Writes the encoded bytes for a single variable-length column's
    /// non-NULL value into <paramref name="destination"/>. The destination's
    /// length must equal the value's previously computed byte count
    /// (<see cref="ComputeVarByteCount"/>).
    /// </summary>
    private static void WriteVarPayload(HeapColumn column, SqlValue value, (int Head, int Length)? pointer, Span<byte> destination)
    {
        if (pointer is { } p)
        {
            destination[0] = VarPointerMarker;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(1, 4), p.Head);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(5, 4), p.Length);
            return;
        }

        destination[0] = VarInlineMarker;
        _ = column.Type.Encode(value, destination[1..]);
    }
}
