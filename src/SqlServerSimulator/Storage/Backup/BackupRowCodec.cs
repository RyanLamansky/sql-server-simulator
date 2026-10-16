using System.Buffers.Binary;

namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// Writes a table's rows into a backup image and reads them back: each stored
/// column's value in its type's own encoding (<see cref="SqlType.Encode"/>),
/// every off-row value inline, so a row doesn't depend on the heap it came
/// from — its LOB chains' page numbers mean nothing to the restored heap.
/// </summary>
internal static class BackupRowCodec
{
    private const byte NullMarker = 0;
    private const byte ValueMarker = 1;

    /// <summary>Appends <paramref name="values"/>, one per column of <paramref name="columns"/>, to <paramref name="output"/>.</summary>
    public static void WriteRow(HeapColumn[] columns, SqlValue[] values, MemoryStream output)
    {
        Span<byte> length = stackalloc byte[4];
        for (var i = 0; i < columns.Length; i++)
        {
            var value = values[i];
            if (value.IsNull)
            {
                output.WriteByte(NullMarker);
                continue;
            }
            output.WriteByte(ValueMarker);
            var type = columns[i].Type;
            if (type == SqlType.Bit)
            {
                output.WriteByte(value.AsBoolean ? (byte)1 : (byte)0);
                continue;
            }
            var size = type.IsFixedLength ? type.FixedLength : type.GetVariableByteCount(value);
            if (!type.IsFixedLength)
            {
                BinaryPrimitives.WriteInt32LittleEndian(length, size);
                output.Write(length);
            }
            var start = checked((int)output.Length);
            output.SetLength(start + size);
            output.Position = start + size;
            _ = type.Encode(value, output.GetBuffer().AsSpan(start, size));
        }
    }

    /// <summary>One row in this form, alone.</summary>
    public static byte[] Portable(HeapColumn[] columns, SqlValue[] values)
    {
        using var output = new MemoryStream();
        WriteRow(columns, values, output);
        return output.ToArray();
    }

    /// <summary>Reads the row at <paramref name="offset"/> into <paramref name="values"/>, advancing past it.</summary>
    public static void ReadRow(HeapColumn[] columns, byte[] rows, ref int offset, SqlValue[] values)
    {
        for (var i = 0; i < columns.Length; i++)
        {
            var type = columns[i].Type;
            if (rows[offset++] == NullMarker)
            {
                values[i] = SqlValue.Null(type);
                continue;
            }
            if (type == SqlType.Bit)
            {
                values[i] = SqlValue.FromBoolean(rows[offset++] != 0);
                continue;
            }
            int size;
            if (type.IsFixedLength)
            {
                size = type.FixedLength;
            }
            else
            {
                size = BinaryPrimitives.ReadInt32LittleEndian(rows.AsSpan(offset, 4));
                offset += 4;
            }
            values[i] = type.Decode(rows.AsSpan(offset, size));
            offset += size;
        }
    }
}
