using System.Buffers.Binary;
using System.Text;

namespace SqlServerSimulator.Storage.Bulk;

/// <summary>
/// Splits a data file into fields by byte layout — the reader behind native
/// and wide-native loads and every format file. A field is bounded by its
/// length prefix (all ones for NULL), its terminator or its fixed width; the
/// end-of-file rules are <see cref="BulkTextReader"/>'s.
/// </summary>
internal sealed class BulkBinaryReader(byte[] data, int start, BulkField[] fields, Encoding charEncoding)
{
    private int position = start;

    /// <summary>The row the next <see cref="Read"/> reads, counted from 1.</summary>
    public long Row = 1;

    /// <summary>
    /// Reads the next row: per field, its decoded text or value, or null for
    /// NULL. False at the end of the file.
    /// </summary>
    public bool Read(object?[] values)
    {
        var rowTerminator = fields[^1].Terminator;
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var last = i == fields.Length - 1;
            if (i == 0 && this.position >= data.Length)
                return false;

            ReadOnlySpan<byte> bytes;
            if (field.PrefixLength > 0)
            {
                var length = this.ReadPrefix(field.PrefixLength);
                if (length < 0)
                {
                    values[i] = null;
                    this.SkipTerminator(field);
                    continue;
                }
                bytes = this.Take(length);
                this.SkipTerminator(field);
            }
            else if (field.Terminator.Length > 0)
            {
                var end = data.AsSpan(this.position).IndexOf(field.Terminator);
                if (end < 0)
                {
                    var rest = data.AsSpan(this.position);
                    this.position = data.Length;
                    if (last)
                    {
                        if (rest.IsEmpty)
                            return false;
                        values[i] = field.Decode(rest, charEncoding);
                        break;
                    }
                    if (rowTerminator.Length > 0 && rest.SequenceEqual(rowTerminator))
                        return false;
                    throw SimulatedSqlException.BulkUnexpectedEndOfFile();
                }
                bytes = data.AsSpan(this.position, end);
                this.position += end + field.Terminator.Length;
            }
            else
            {
                bytes = this.Take(field.Length);
            }
            // An empty terminated field is NULL, as in a character-mode load;
            // an empty prefixed one is the empty string.
            values[i] = bytes.IsEmpty && field.PrefixLength == 0 ? null : field.Decode(bytes, charEncoding);
        }
        this.Row++;
        return true;
    }

    private long ReadPrefix(int width)
    {
        var bytes = this.Take(width);
        return width switch
        {
            1 => bytes[0] == 0xFF ? -1 : bytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes) is 0xFFFF ? -1 : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes) is 0xFFFFFFFF ? -1 : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadInt64LittleEndian(bytes),
        };
    }

    private ReadOnlySpan<byte> Take(long length)
    {
        if (length < 0 || length > data.Length - this.position)
            throw SimulatedSqlException.BulkUnexpectedEndOfFile();
        var span = data.AsSpan(this.position, (int)length);
        this.position += (int)length;
        return span;
    }

    private void SkipTerminator(BulkField field)
    {
        if (field.Terminator.Length > 0 && !this.Take(field.Terminator.Length).SequenceEqual(field.Terminator))
            throw SimulatedSqlException.BulkUnexpectedEndOfFile();
    }
}
