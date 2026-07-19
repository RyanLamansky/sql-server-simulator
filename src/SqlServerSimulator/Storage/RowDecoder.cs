using System.Buffers.Binary;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Decodes a row encoded by <see cref="RowEncoder"/> against a known schema.
/// </summary>
/// <remarks>
/// Validates the structural fields (TagA, fixed-length offset, declared column
/// count, var section presence, var column count, offset array bounds) so
/// encoder/decoder/schema drift surfaces in tests rather than as silent
/// corruption.
/// </remarks>
internal static class RowDecoder
{
    private const byte TagA_NullBitmap = 0x10;
    private const byte TagA_VarSection = 0x20;
    private const byte TagA_WideVarOffsets = 0x40;

    /// <summary>
    /// Reads the var-section offset entry at the supplied index, honoring
    /// the row header's 2-byte vs 4-byte offset width. Wide offsets show
    /// up only on rows the encoder produced when the inline var section
    /// would overflow the legacy 16-bit cap — projection result rows that
    /// hold MAX-form varbinary / varchar / nvarchar values are the only
    /// path that triggers this in practice.
    /// </summary>
    private static int ReadVarOffset(in RowHeader header, ReadOnlySpan<byte> bytes, int varIndex) =>
        header.VarOffsetEntrySize == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(header.VarOffsetArrayStart + (2 * varIndex), 2))
            : (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(header.VarOffsetArrayStart + (4 * varIndex), 4));

    /// <summary>
    /// <see cref="SqlType"/>-only entry point. LOB-eligibility is determined
    /// by <see cref="SqlType.IsLob"/> alone — sufficient for
    /// <c>text</c>/<c>ntext</c>/<c>image</c>, but MAX siblings of
    /// varchar/nvarchar/varbinary need the
    /// <see cref="DecodeRow(ReadOnlySpan{HeapColumn}, ReadOnlySpan{byte}, Heap?)"/>
    /// overload to surface as LOB-eligible.
    /// </summary>
    public static SqlValue[] DecodeRow(ReadOnlySpan<SqlType> schema, ReadOnlySpan<byte> bytes)
    {
        var columns = new HeapColumn[schema.Length];
        for (var i = 0; i < schema.Length; i++)
            columns[i] = new HeapColumn(string.Empty, schema[i], maxLength: null, nullable: true);
        return DecodeRow(columns, bytes, lobStore: null);
    }

    /// <summary>
    /// Column-aware row decode. <paramref name="lobStore"/> resolves any
    /// off-row LOB pointers; pass <c>null</c> only when the encoded row is
    /// known to have all LOB-eligible columns inline (e.g. projection result
    /// sets).
    /// </summary>
    public static SqlValue[] DecodeRow(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<byte> bytes, Heap? lobStore = null)
    {
        var header = ValidateHeader(schema, bytes);
        var values = new SqlValue[schema.Length];
        var fixedPos = 4;
        var varIndex = 0;
        var prevVarEnd = header.VarDataStart;
        var bitByteOffset = -1;
        var bitsInRun = 0;

        for (var i = 0; i < schema.Length; i++)
        {
            var isNull = IsNullColumn(bytes, header.BitmapStart, i);

            if (schema[i].Type == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                {
                    bitByteOffset = fixedPos;
                    fixedPos++;
                }
                values[i] = isNull
                    ? SqlValue.Null(SqlType.Bit)
                    : SqlValue.FromBoolean((bytes[bitByteOffset] & (1 << (bitsInRun % 8))) != 0);
                bitsInRun++;
            }
            else if (schema[i].Type.IsFixedLength)
            {
                var width = schema[i].Type.FixedLength;
                values[i] = isNull ? SqlValue.Null(schema[i].Type) : schema[i].Type.Decode(bytes.Slice(fixedPos, width));
                fixedPos += width;
                bitsInRun = 0;
            }
            else
            {
                var end = ReadVarOffset(header, bytes, varIndex);
                if (end < prevVarEnd)
                    throw new InvalidDataException($"Var offset {end} at index {varIndex} regresses past previous {prevVarEnd}.");

                values[i] = isNull
                    ? SqlValue.Null(schema[i].Type)
                    : DecodeVarValue(schema[i], bytes[prevVarEnd..end], lobStore);
                prevVarEnd = end;
                varIndex++;
            }
        }

        return values;
    }

    /// <summary>
    /// Decodes a single column from a row's bytes without materializing the
    /// other columns. The data reader uses this to navigate row bytes directly
    /// per <see cref="System.Data.Common.DbDataReader"/> accessor call.
    /// Routes through <see cref="ColumnsFor"/> so repeated reads against the
    /// same schema array reuse one <see cref="HeapColumn"/>[] — and therefore
    /// one cached <see cref="RowLayout"/> — instead of allocating and
    /// re-laying-out per call (the per-call rebuild dominated result-drain CPU
    /// at 34% and its discarded arrays defeated the layout cache's identity
    /// key).
    /// </summary>
    public static SqlValue DecodeColumn(SqlType[] schema, ReadOnlySpan<byte> bytes, int ordinal) =>
        DecodeColumn(ColumnsFor(schema), bytes, ordinal, lobStore: null);

    /// <summary>
    /// The nameless all-nullable <see cref="HeapColumn"/>[] equivalent of a
    /// type-only schema, cached by the schema array's identity (schema arrays
    /// are per-result-set and long-lived, mirroring <see cref="RowLayout"/>'s
    /// keying).
    /// </summary>
    public static HeapColumn[] ColumnsFor(SqlType[] schema) =>
        typeOnlyColumns.GetValue(schema, static s =>
        {
            var columns = new HeapColumn[s.Length];
            for (var i = 0; i < s.Length; i++)
                columns[i] = new HeapColumn(string.Empty, s[i], maxLength: null, nullable: true);
            return columns;
        });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SqlType[], HeapColumn[]> typeOnlyColumns = [];

    /// <summary>
    /// Array-schema fast path of
    /// <see cref="DecodeColumn(ReadOnlySpan{HeapColumn}, ReadOnlySpan{byte}, int, Heap?)"/>:
    /// navigates through the schema's cached <see cref="RowLayout"/>, so the
    /// read is O(1) instead of two O(columns) walks (header validation +
    /// navigate-to-ordinal). Overload resolution binds every caller holding
    /// the schema as an array here — the per-row query-execution resolvers,
    /// whose repeated walks dominated scan-bound query CPU — while span-based
    /// callers keep the fully-validating walk. One header word is still
    /// checked (the fixed-section end at byte 2) so a schema/row mismatch
    /// raises the same <see cref="InvalidDataException"/> instead of
    /// misreading; the remaining per-read validation is entrusted to span
    /// bounds, as encoder and decoder share one format authority.
    /// </summary>
    public static SqlValue DecodeColumn(HeapColumn[] schema, ReadOnlySpan<byte> bytes, int ordinal, Heap? lobStore = null)
    {
        if ((uint)ordinal >= (uint)schema.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal), $"Ordinal {ordinal} is out of range for schema of {schema.Length} columns.");

        var layout = RowLayout.For(schema);
        var fixedEnd = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        if (fixedEnd != layout.ExpectedFixedEnd)
            throw new InvalidDataException($"Fixed-length end offset {fixedEnd} does not match schema's expected {layout.ExpectedFixedEnd}.");

        if (IsNullColumn(bytes, layout.BitmapStart, ordinal))
            return SqlValue.Null(schema[ordinal].Type);

        switch (layout.Kinds[ordinal])
        {
            case RowLayout.ColumnKind.Fixed:
                return schema[ordinal].Type.Decode(bytes.Slice(layout.Offsets[ordinal], schema[ordinal].Type.FixedLength));
            case RowLayout.ColumnKind.Bit:
                return SqlValue.FromBoolean((bytes[layout.Offsets[ordinal]] & (1 << layout.BitIndexes[ordinal])) != 0);
            default:
                var entrySize = (bytes[0] & TagA_WideVarOffsets) != 0 ? 4 : 2;
                var directoryStart = layout.VarCountPosition + 2;
                var varDataStart = directoryStart + (entrySize * layout.VarColumnCount);
                var varIndex = layout.Offsets[ordinal];
                var start = varIndex == 0 ? varDataStart : ReadDirectoryOffset(bytes, directoryStart, entrySize, varIndex - 1);
                var end = ReadDirectoryOffset(bytes, directoryStart, entrySize, varIndex);
                return DecodeVarValue(schema[ordinal], bytes[start..end], lobStore);
        }
    }

    // Reads one var-offset directory entry for the fast path (the span-based
    // walk reads through ReadVarOffset's RowHeader instead).
    private static int ReadDirectoryOffset(ReadOnlySpan<byte> bytes, int directoryStart, int entrySize, int varIndex) =>
        entrySize == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(directoryStart + (2 * varIndex), 2))
            : (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(directoryStart + (4 * varIndex), 4));

    /// <summary>
    /// Column-aware single-column decode. <paramref name="lobStore"/> resolves
    /// any off-row LOB pointers.
    /// </summary>
    public static SqlValue DecodeColumn(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<byte> bytes, int ordinal, Heap? lobStore = null)
    {
        if ((uint)ordinal >= (uint)schema.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal), $"Ordinal {ordinal} is out of range for schema of {schema.Length} columns.");

        var header = ValidateHeader(schema, bytes);
        var fixedPos = 4;
        var varIndex = 0;
        var prevVarEnd = header.VarDataStart;
        var bitByteOffset = -1;
        var bitsInRun = 0;

        for (var i = 0; i <= ordinal; i++)
        {
            var isNull = IsNullColumn(bytes, header.BitmapStart, i);

            if (schema[i].Type == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                {
                    bitByteOffset = fixedPos;
                    fixedPos++;
                }
                if (i == ordinal)
                {
                    return isNull
                        ? SqlValue.Null(SqlType.Bit)
                        : SqlValue.FromBoolean((bytes[bitByteOffset] & (1 << (bitsInRun % 8))) != 0);
                }
                bitsInRun++;
            }
            else if (schema[i].Type.IsFixedLength)
            {
                var width = schema[i].Type.FixedLength;
                if (i == ordinal)
                    return isNull ? SqlValue.Null(schema[i].Type) : schema[i].Type.Decode(bytes.Slice(fixedPos, width));
                fixedPos += width;
                bitsInRun = 0;
            }
            else
            {
                var end = ReadVarOffset(header, bytes, varIndex);
                if (end < prevVarEnd)
                    throw new InvalidDataException($"Var offset {end} at index {varIndex} regresses past previous {prevVarEnd}.");

                if (i == ordinal)
                {
                    return isNull
                        ? SqlValue.Null(schema[i].Type)
                        : DecodeVarValue(schema[i], bytes[prevVarEnd..end], lobStore);
                }
                prevVarEnd = end;
                varIndex++;
            }
        }

        throw new InvalidOperationException("Unreachable: loop terminates on hit.");
    }

    /// <summary>
    /// Collects the off-row LOB chain head-indices the row references — one per
    /// variable-length column the encoder stored as a
    /// <see cref="RowEncoder.VarPointerMarker"/> pointer — into
    /// <paramref name="heads"/>, without materializing any value. The heap's
    /// reclamation paths (undo-log commit / rollback free hooks and
    /// version-store GC) use it to locate the chains a superseded or
    /// rolled-back row owned. NULL columns and inline values contribute
    /// nothing; a row with no off-row column adds nothing.
    /// </summary>
    public static void CollectLobHeads(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<byte> bytes, List<int> heads)
    {
        var header = ValidateHeader(schema, bytes);
        var varIndex = 0;
        var prevVarEnd = header.VarDataStart;
        for (var i = 0; i < schema.Length; i++)
        {
            var type = schema[i].Type;
            if (type == SqlType.Bit || type.IsFixedLength)
                continue;

            var end = ReadVarOffset(header, bytes, varIndex);
            if (!IsNullColumn(bytes, header.BitmapStart, i))
            {
                var payload = bytes[prevVarEnd..end];
                if (payload.Length > 0 && payload[0] == RowEncoder.VarPointerMarker)
                    heads.Add(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4)));
            }
            prevVarEnd = end;
            varIndex++;
        }
    }

    private static SqlValue DecodeVarValue(HeapColumn column, ReadOnlySpan<byte> payload, Heap? lobStore)
    {
        if (payload.Length == 0)
            throw new InvalidDataException($"Variable-length column has zero-byte payload but isn't NULL; the marker prefix is required.");

        var marker = payload[0];
        return marker switch
        {
            RowEncoder.VarInlineMarker => column.Type.Decode(payload[1..]),
            RowEncoder.VarPointerMarker when lobStore is null
                => throw new InvalidDataException($"Var-length pointer in row, but no chain store was provided to resolve it."),
            RowEncoder.VarPointerMarker
                => lobStore.ReadLobChain(
                    BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5, 4)),
                    column.Type,
                    static (span, type) => type.Decode(span)),
            _ => throw new InvalidDataException($"Unknown variable-length marker byte 0x{marker:X2}."),
        };
    }

    private static bool IsNullColumn(ReadOnlySpan<byte> bytes, int bitmapStart, int ordinal) =>
        (bytes[bitmapStart + (ordinal / 8)] & (1 << (ordinal % 8))) != 0;

    private readonly record struct RowHeader(int BitmapStart, int VarOffsetArrayStart, int VarDataStart, int VarOffsetEntrySize);

    private static RowHeader ValidateHeader(ReadOnlySpan<HeapColumn> schema, ReadOnlySpan<byte> bytes)
    {
        if (schema.Length == 0)
            throw new ArgumentException("Schema must have at least one column.", nameof(schema));

        var n = schema.Length;
        var fixedSectionLength = 0;
        var varColumnCount = 0;
        var bitsInRun = 0;
        for (var i = 0; i < n; i++)
        {
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
            }
        }

        var expectedFixedEnd = 4 + fixedSectionLength;
        var nullBitmapLength = (n + 7) / 8;
        var bitmapStart = expectedFixedEnd + 2;
        var bitmapEnd = bitmapStart + nullBitmapLength;
        var hasVar = varColumnCount > 0;

        if (bytes.Length < 1)
            throw new InvalidDataException($"Row is too short: {bytes.Length} bytes (need at least 1 for the tag byte).");

        var tagA = bytes[0];
        var wideVarOffsets = (tagA & TagA_WideVarOffsets) != 0;
        var varOffsetEntrySize = wideVarOffsets ? 4 : 2;
        var expectedTagA = hasVar ? (byte)(TagA_NullBitmap | TagA_VarSection | (wideVarOffsets ? TagA_WideVarOffsets : 0)) : TagA_NullBitmap;
        if (tagA != expectedTagA)
            throw new InvalidDataException($"Unexpected TagA: 0x{tagA:X2} (expected 0x{expectedTagA:X2}).");

        var minLength = bitmapEnd + (hasVar ? 2 + (varOffsetEntrySize * varColumnCount) : 0);
        if (bytes.Length < minLength)
            throw new InvalidDataException($"Row is too short: {bytes.Length} bytes (need at least {minLength} for this schema).");

        var fixedEnd = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        if (fixedEnd != expectedFixedEnd)
            throw new InvalidDataException($"Fixed-length end offset {fixedEnd} does not match schema's expected {expectedFixedEnd}.");

        var declaredColumnCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(fixedEnd, 2));
        if (declaredColumnCount != n)
            throw new InvalidDataException($"Column count mismatch: schema has {n} columns, header declares {declaredColumnCount}.");

        var offsetArrayStart = 0;
        var varDataStart = 0;
        if (hasVar)
        {
            var declaredVarCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(bitmapEnd, 2));
            if (declaredVarCount != varColumnCount)
                throw new InvalidDataException($"Var column count mismatch: schema has {varColumnCount} var columns, header declares {declaredVarCount}.");

            offsetArrayStart = bitmapEnd + 2;
            varDataStart = offsetArrayStart + (varOffsetEntrySize * varColumnCount);

            var lastEnd = wideVarOffsets
                ? (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offsetArrayStart + (4 * (varColumnCount - 1)), 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offsetArrayStart + (2 * (varColumnCount - 1)), 2));
            if (lastEnd > bytes.Length)
                throw new InvalidDataException($"Var offset array references byte {lastEnd}, beyond row length {bytes.Length}.");
            if (lastEnd < varDataStart)
                throw new InvalidDataException($"Var offset array's last entry {lastEnd} precedes var data start {varDataStart}.");
        }

        return new RowHeader(bitmapStart, offsetArrayStart, varDataStart, varOffsetEntrySize);
    }
}
