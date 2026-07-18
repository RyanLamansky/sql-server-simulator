using System.Buffers.Binary;
using System.Text;

namespace SqlServerSimulator.Network;

/// <summary>
/// A forward-only, bounds-checked cursor over a TDS payload. Shared by the RPC
/// parameter reader (<see cref="TdsRpcRequest"/>), the bulk-load reader
/// (<see cref="TdsBulkLoadReader"/>), and the TVP decoder — all three walk the
/// same little-endian value encodings, so the cursor plus the per-column value
/// decoder in <see cref="TdsColumnDecoder"/> are the single shared primitive.
/// </summary>
internal sealed class TdsValueReader(byte[] data)
{
    private readonly byte[] data = data;

    /// <summary>The read offset; callers advance it by skipping header blocks.</summary>
    public int Position;

    public bool AtEnd => this.Position >= this.data.Length;

    public byte PeekByte() =>
        this.Position < this.data.Length ? this.data[this.Position] : throw Truncated();

    public byte ReadByte() =>
        this.Position < this.data.Length ? this.data[this.Position++] : throw Truncated();

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || (long)this.Position + count > this.data.Length)
            throw Truncated();

        var span = this.data.AsSpan(this.Position, count);
        this.Position += count;
        return span;
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(this.ReadBytes(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(this.ReadBytes(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(this.ReadBytes(8));

    /// <summary>Reads <paramref name="charCount"/> UCS-2 characters as a string.</summary>
    public string ReadUcs2(int charCount) =>
        charCount == 0 ? "" : Encoding.Unicode.GetString(this.ReadBytes(charCount * 2));

    private static InvalidDataException Truncated() =>
        new("The TDS payload ends before a value was fully read.");
}
