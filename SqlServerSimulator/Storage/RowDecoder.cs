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

    /// <summary>
    /// Decodes every column of a row against a known schema.
    /// </summary>
    /// <param name="schema">Per-column types; must match the schema used to encode the row.</param>
    /// <param name="bytes">The encoded row bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a valid row for the given schema.</exception>
    public static SqlValue[] DecodeRow(ReadOnlySpan<SqlType> schema, ReadOnlySpan<byte> bytes)
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

            if (schema[i] == SqlType.Bit)
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
            else if (schema[i].IsFixedLength)
            {
                var width = schema[i].FixedLength;
                values[i] = isNull ? SqlValue.Null(schema[i]) : schema[i].Decode(bytes.Slice(fixedPos, width));
                fixedPos += width;
                bitsInRun = 0;
            }
            else
            {
                var end = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(header.VarOffsetArrayStart + (2 * varIndex), 2));
                if (end < prevVarEnd)
                    throw new InvalidDataException($"Var offset {end} at index {varIndex} regresses past previous {prevVarEnd}.");

                values[i] = isNull ? SqlValue.Null(schema[i]) : schema[i].Decode(bytes[prevVarEnd..end]);
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
    /// </summary>
    /// <param name="schema">Per-column types; must match the schema used to encode the row.</param>
    /// <param name="bytes">The encoded row bytes.</param>
    /// <param name="ordinal">Zero-based column index within <paramref name="schema"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is out of range.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a valid row for the given schema.</exception>
    public static SqlValue DecodeColumn(ReadOnlySpan<SqlType> schema, ReadOnlySpan<byte> bytes, int ordinal)
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

            if (schema[i] == SqlType.Bit)
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
            else if (schema[i].IsFixedLength)
            {
                var width = schema[i].FixedLength;
                if (i == ordinal)
                    return isNull ? SqlValue.Null(schema[i]) : schema[i].Decode(bytes.Slice(fixedPos, width));
                fixedPos += width;
                bitsInRun = 0;
            }
            else
            {
                var end = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(header.VarOffsetArrayStart + (2 * varIndex), 2));
                if (end < prevVarEnd)
                    throw new InvalidDataException($"Var offset {end} at index {varIndex} regresses past previous {prevVarEnd}.");

                if (i == ordinal)
                    return isNull ? SqlValue.Null(schema[i]) : schema[i].Decode(bytes[prevVarEnd..end]);
                prevVarEnd = end;
                varIndex++;
            }
        }

        throw new InvalidOperationException("Unreachable: loop terminates on hit.");
    }

    private static bool IsNullColumn(ReadOnlySpan<byte> bytes, int bitmapStart, int ordinal) =>
        (bytes[bitmapStart + (ordinal / 8)] & (1 << (ordinal % 8))) != 0;

    private readonly record struct RowHeader(int BitmapStart, int VarOffsetArrayStart, int VarDataStart);

    private static RowHeader ValidateHeader(ReadOnlySpan<SqlType> schema, ReadOnlySpan<byte> bytes)
    {
        if (schema.Length == 0)
            throw new ArgumentException("Schema must have at least one column.", nameof(schema));

        var n = schema.Length;
        var fixedSectionLength = 0;
        var varColumnCount = 0;
        var bitsInRun = 0;
        for (var i = 0; i < n; i++)
        {
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
            }
        }

        var expectedFixedEnd = 4 + fixedSectionLength;
        var nullBitmapLength = (n + 7) / 8;
        var bitmapStart = expectedFixedEnd + 2;
        var bitmapEnd = bitmapStart + nullBitmapLength;
        var hasVar = varColumnCount > 0;
        var minLength = bitmapEnd + (hasVar ? 2 + (2 * varColumnCount) : 0);

        if (bytes.Length < minLength)
            throw new InvalidDataException($"Row is too short: {bytes.Length} bytes (need at least {minLength} for this schema).");

        var expectedTagA = hasVar ? (byte)(TagA_NullBitmap | TagA_VarSection) : TagA_NullBitmap;
        if (bytes[0] != expectedTagA)
            throw new InvalidDataException($"Unexpected TagA: 0x{bytes[0]:X2} (expected 0x{expectedTagA:X2}).");

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
            varDataStart = offsetArrayStart + (2 * varColumnCount);

            var lastEnd = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offsetArrayStart + (2 * (varColumnCount - 1)), 2));
            if (lastEnd > bytes.Length)
                throw new InvalidDataException($"Var offset array references byte {lastEnd}, beyond row length {bytes.Length}.");
            if (lastEnd < varDataStart)
                throw new InvalidDataException($"Var offset array's last entry {lastEnd} precedes var data start {varDataStart}.");
        }

        return new RowHeader(bitmapStart, offsetArrayStart, varDataStart);
    }
}
