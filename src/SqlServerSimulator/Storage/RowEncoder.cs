using System.Buffers;
using System.Buffers.Binary;
using System.Text;

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
    /// Column count up to which <see cref="EncodeRow(ReadOnlySpan{HeapColumn}, ReadOnlySpan{SqlValue}, Heap?)"/>'s
    /// per-row scratch comes off the stack. Past it the scratch is heap
    /// allocated, so a 1024-column table can't blow the frame.
    /// </summary>
    private const int StackScratchColumns = 64;

    /// <summary>Byte count up to which <see cref="StorageForm"/>'s single-value
    /// scratch comes off the stack.</summary>
    private const int StackScratchBytes = 256;

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
    /// Array-schema form of
    /// <see cref="EncodeRow(ReadOnlySpan{SqlType}, ReadOnlySpan{SqlValue})"/>,
    /// which overload resolution binds every caller holding its schema as an
    /// array to. It routes the conversion through
    /// <see cref="RowDecoder.ColumnsFor"/>, so a result set encoding row after
    /// row against one schema builds its <see cref="HeapColumn"/>[] once —
    /// the span form rebuilt the array <em>and</em> a column object per column
    /// on every row, which on a five-column, 73k-row <c>SELECT … INTO</c> was
    /// the largest single allocation in the statement.
    /// </summary>
    public static byte[] EncodeRow(SqlType[] schema, ReadOnlySpan<SqlValue> values) =>
        EncodeRow(RowDecoder.ColumnsFor(schema), values, lobStore: null);

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
        byte[]? exact = null;
        _ = EncodeRowInto(schema, values, lobStore, ref exact);
        // Starting from null, the encoder allocated the row's exact length, so
        // the buffer IS the row — callers that retain it can rely on Length.
        return exact!;
    }

    /// <summary>
    /// Row-buffer-reusing form of
    /// <see cref="EncodeRow(ReadOnlySpan{HeapColumn}, ReadOnlySpan{SqlValue}, Heap?)"/>,
    /// for the callers that hand the bytes to a heap and drop them: it writes
    /// into <paramref name="buffer"/>, growing it only when the row doesn't
    /// fit, and returns the row's length. Pass the same buffer across a loop's
    /// iterations and a per-row array becomes one per loop; a caller that
    /// retains the bytes wants the allocating overload instead, since a reused
    /// buffer is longer than its row and is overwritten by the next one.
    /// </summary>
    /// <remarks>
    /// The row's bytes are zeroed before the value pass, which the encoding
    /// depends on: the NULL bitmap and the bit-column runs are written with
    /// <c>|=</c>, a NULL fixed-length column is skipped rather than written,
    /// and TagB (byte 1) is never written at all. A freshly allocated array
    /// arrives zeroed and is used as-is; a reused one is cleared.
    /// </remarks>
    public static int EncodeRowInto(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<SqlValue> values, Heap? lobStore, ref byte[]? buffer)
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
        // Per-row scratch, taken off the stack for the column counts a table
        // actually has: an INSERT-heavy statement paid two heap arrays per row
        // for state that dies with the row. A wider schema falls back to the
        // heap rather than growing the frame without bound.
        var varByteCounts = n <= StackScratchColumns ? stackalloc int[n] : new int[n];
        // Pre-resolved off-row pointers for values pushed to a chain on
        // <paramref name="lobStore"/>: indexed by schema position; entry is
        // (chainHead, totalLength) when the value is non-NULL AND the encoder
        // chose to store it off-row (always for LOB-eligible columns when a
        // store is provided; for bounded var columns when the row otherwise
        // wouldn't fit).
        var chainPointers = n <= StackScratchColumns ? stackalloc (int Head, int Length)?[n] : new (int Head, int Length)?[n];
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

        // A fresh array is already zeroed; a reused one carries the previous
        // row and has to be cleared — see the remarks on why the encoding
        // needs it.
        if (buffer is null || buffer.Length < totalLength)
            buffer = new byte[totalLength];
        else
            buffer.AsSpan(0, totalLength).Clear();
        var bytes = buffer.AsSpan(0, totalLength);

        bytes[0] = hasVar
            ? (byte)(TagA_NullBitmap | TagA_VarSection | (varOffsetEntrySize == 4 ? TagA_WideVarOffsets : 0))
            : TagA_NullBitmap;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(2, 2), checked((ushort)fixedEnd));

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
                    _ = t.Encode(values[i], bytes.Slice(fixedOffset, width));
                fixedOffset += width;
                bitsInRun = 0;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(fixedEnd, 2), checked((ushort)n));

        if (hasVar)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(bitmapEnd, 2), checked((ushort)varColumnCount));

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
                    WriteVarPayload(schema[i], values[i], chainPointers[i], bytes.Slice(dataPos, width));
                    dataPos += width;
                }

                if (varOffsetEntrySize == 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offsetArrayStart + (4 * varIndex), 4), checked((uint)dataPos));
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offsetArrayStart + (2 * varIndex), 2), checked((ushort)dataPos));
                varIndex++;
            }
        }

        return totalLength;
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
    /// Marks the columns of <paramref name="schema"/> whose values a storage
    /// round trip can change, or returns <see langword="null"/> when none can —
    /// the answer for most schemas, which is what lets a consumer serving
    /// already-projected <see cref="SqlValue"/> rows skip the question per cell.
    /// </summary>
    /// <remarks>
    /// Every value factory normalizes its payload at construction — a
    /// <c>time(3)</c> value is already quantized to its precision, a
    /// <c>decimal(9, 2)</c> already carries scale 2, a <c>char(5)</c> is already
    /// padded to five bytes — so <c>Decode(Encode(v))</c> returns <c>v</c>
    /// unchanged for those families. The exception is the character data an ANSI
    /// code page cannot represent: the <c>varchar</c> / <c>char</c> / <c>text</c>
    /// encoders fold it to <c>?</c> on the way to bytes, which is SQL Server's
    /// own lossy narrowing, and a <c>sql_variant</c> holding one of those
    /// inherits it.
    /// </remarks>
    internal static bool[]? NarrowingColumns(SqlType[] schema)
    {
        bool[]? narrowing = null;
        for (var i = 0; i < schema.Length; i++)
        {
            if (schema[i] is not (VarcharSqlType or CharSqlType or TextSqlType or SqlVariantSqlType))
                continue;
            narrowing ??= new bool[schema.Length];
            narrowing[i] = true;
        }

        return narrowing;
    }

    /// <summary>
    /// The value a storage round trip would return for <paramref name="value"/>
    /// in a <paramref name="type"/> column: the encoder's own lossy narrowing
    /// applied without a page image. Only called for a column
    /// <see cref="NarrowingColumns"/> flagged, and it does no work past the
    /// all-ASCII test in <see cref="CanNarrow"/> — every ANSI code page the
    /// simulator stores through is ASCII-transparent, so an ASCII payload is
    /// already its own storage form.
    /// </summary>
    internal static SqlValue StorageForm(SqlValue value, SqlType type)
    {
        if (value.IsNull || !CanNarrow(value, type))
            return value;
        var length = type.IsFixedLength ? type.FixedLength : type.GetVariableByteCount(value);
        // Zero-filled, matching the row buffer the encoder writes into: a char(N)
        // payload encoding to fewer than N bytes reads its tail back as NUL there
        // too.
        var buffer = length <= StackScratchBytes ? stackalloc byte[length] : new byte[length];
        buffer.Clear();
        _ = type.Encode(value, buffer);
        return type.Decode(buffer);
    }

    /// <summary>Whether <paramref name="value"/> carries character data the
    /// column's own code page would fold — directly, or inside a
    /// <c>sql_variant</c>.</summary>
    private static bool CanNarrow(SqlValue value, SqlType type) => type is SqlVariantSqlType
        ? value.AsVariantInner is { IsNull: false } inner && CanNarrow(inner, inner.Type)
        : type is VarcharSqlType or CharSqlType or TextSqlType && !Ascii.IsValid(value.AsString);

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
