using System.Buffers.Binary;
using System.Text;

namespace SqlServerSimulator.Network;

/// <summary>
/// Accumulates a server-to-client token stream in a growable buffer and
/// flushes it through the transport as tabular-result packets. Token writes
/// are synchronous; the session flushes between rows so memory stays bounded
/// by the larger of one row and one packet.
/// </summary>
internal sealed class TdsTokenWriter(TdsPacketTransport transport)
{
    private readonly TdsPacketTransport transport = transport;
    private byte[] buffer = new byte[8192];
    private int length;

    /// <summary>
    /// When set, non-final flushes accumulate rather than send — the MARS path
    /// buffers a session's whole response under the connection's execution lock
    /// and sends it only on the final flush, once the lock is released, so a
    /// window-blocked send never stalls another session's execution.
    /// </summary>
    public bool DeferFlush;

    /// <summary>
    /// Sends every full packet's worth of buffered bytes; when
    /// <paramref name="final"/>, sends the remainder with the end-of-message
    /// bit, completing the response.
    /// </summary>
    public async ValueTask FlushAsync(bool final, CancellationToken cancellationToken)
    {
        if (this.DeferFlush && !final)
            return;

        var capacity = this.transport.PacketSize - Tds.HeaderSize;
        var offset = 0;
        while (this.length - offset > capacity)
        {
            await this.transport.WritePacketAsync(Tds.PacketTabularResult, this.buffer.AsMemory(offset, capacity), endOfMessage: false, cancellationToken).ConfigureAwait(false);
            offset += capacity;
        }

        if (final)
        {
            await this.transport.WritePacketAsync(Tds.PacketTabularResult, this.buffer.AsMemory(offset, this.length - offset), endOfMessage: true, cancellationToken).ConfigureAwait(false);
            offset = this.length;
        }

        if (offset > 0)
        {
            Buffer.BlockCopy(this.buffer, offset, this.buffer, 0, this.length - offset);
            this.length -= offset;
        }
    }

    public void WriteByte(byte value)
    {
        this.Ensure(1);
        this.buffer[this.length++] = value;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        this.Ensure(value.Length);
        value.CopyTo(this.buffer.AsSpan(this.length));
        this.length += value.Length;
    }

    public void WriteUInt16(ushort value)
    {
        this.Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(this.buffer.AsSpan(this.length), value);
        this.length += 2;
    }

    public void WriteInt32(int value)
    {
        this.Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(this.buffer.AsSpan(this.length), value);
        this.length += 4;
    }

    public void WriteUInt32(uint value)
    {
        this.Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(this.buffer.AsSpan(this.length), value);
        this.length += 4;
    }

    public void WriteInt64(long value)
    {
        this.Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(this.buffer.AsSpan(this.length), value);
        this.length += 8;
    }

    public void WriteUInt64(ulong value)
    {
        this.Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(this.buffer.AsSpan(this.length), value);
        this.length += 8;
    }

    /// <summary>Writes UCS-2 characters with no length prefix.</summary>
    public void WriteUcs2(string value)
    {
        var byteCount = value.Length * 2;
        this.Ensure(byteCount);
        _ = Encoding.Unicode.GetBytes(value, this.buffer.AsSpan(this.length));
        this.length += byteCount;
    }

    /// <summary>B_VARCHAR: a one-byte character count followed by UCS-2 text.</summary>
    public void WriteBVarchar(string value)
    {
        this.WriteByte(checked((byte)value.Length));
        this.WriteUcs2(value);
    }

    /// <summary>US_VARCHAR: a two-byte character count followed by UCS-2 text.</summary>
    public void WriteUsVarchar(string value)
    {
        this.WriteUInt16(checked((ushort)value.Length));
        this.WriteUcs2(value);
    }

    /// <summary>ENVCHANGE with old and new values in B_VARCHAR form.</summary>
    public void WriteEnvChange(byte type, string newValue, string oldValue)
    {
        this.WriteByte(Tds.TokenEnvChange);
        this.WriteUInt16(checked((ushort)(1 + 1 + (newValue.Length * 2) + 1 + (oldValue.Length * 2))));
        this.WriteByte(type);
        this.WriteBVarchar(newValue);
        this.WriteBVarchar(oldValue);
    }

    /// <summary>
    /// ENVCHANGE type 7: the server's default SQL collation as the 5-byte
    /// wire structure. SqlClient stores it as the default collation it stamps
    /// onto outbound RPC parameter TYPE_INFO — without it, parameterized
    /// commands fail client-side.
    /// </summary>
    public void WriteEnvChangeSqlCollation(uint info, byte sortId)
    {
        this.WriteByte(Tds.TokenEnvChange);
        this.WriteUInt16(1 + 1 + 5 + 1);
        this.WriteByte(Tds.EnvSqlCollation);
        this.WriteByte(5);
        this.WriteUInt32(info);
        this.WriteByte(sortId);
        this.WriteByte(0);
    }

    /// <summary>The empty ENVCHANGE acknowledging a connection reset.</summary>
    public void WriteResetConnectionAck()
    {
        this.WriteByte(Tds.TokenEnvChange);
        this.WriteUInt16(3);
        this.WriteByte(Tds.EnvResetConnectionAck);
        this.WriteByte(0);
        this.WriteByte(0);
    }

    /// <summary>
    /// ERROR and INFO share one layout; the token byte is the only
    /// difference (severity below 11 is informational).
    /// </summary>
    public void WriteErrorOrInfo(byte token, int number, byte state, byte severity, string message, string server, string procedure, int line)
    {
        this.WriteByte(token);
        var lengthPosition = this.length;
        this.WriteUInt16(0);
        this.WriteInt32(number);
        this.WriteByte(state);
        this.WriteByte(severity);
        this.WriteUsVarchar(message);
        this.WriteBVarchar(server);
        this.WriteBVarchar(procedure);
        this.WriteInt32(line);
        BinaryPrimitives.WriteUInt16LittleEndian(this.buffer.AsSpan(lengthPosition), checked((ushort)(this.length - lengthPosition - 2)));
    }

    public void WriteLoginAck(uint tdsVersion, string programName, byte versionMajor, byte versionMinor, ushort build)
    {
        this.WriteByte(Tds.TokenLoginAck);
        this.WriteUInt16(checked((ushort)(1 + 4 + 1 + (programName.Length * 2) + 4)));
        this.WriteByte(1);
        this.Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(this.buffer.AsSpan(this.length), tdsVersion);
        this.length += 4;
        this.WriteBVarchar(programName);
        this.WriteByte(versionMajor);
        this.WriteByte(versionMinor);
        // ProgVersion build is a big-endian 16-bit field; SqlConnection.ServerVersion
        // reads it as "major.minor.build" (build 4065 → "17.00.4065"), matching the
        // SQL Server 2025 reference instance the simulator emulates.
        this.WriteByte((byte)(build >> 8));
        this.WriteByte((byte)(build & 0xFF));
    }

    public void WriteDone(ushort status, long rowCount) => this.WriteDoneToken(Tds.TokenDone, status, rowCount);

    /// <summary>DONE, DONEPROC, and DONEINPROC share one 13-byte layout.</summary>
    public void WriteDoneToken(byte token, ushort status, long rowCount)
    {
        this.WriteByte(token);
        this.WriteUInt16(status);
        this.WriteUInt16(0);
        this.WriteInt64(rowCount);
    }

    public void WriteReturnStatus(int value)
    {
        this.WriteByte(Tds.TokenReturnStatus);
        this.WriteInt32(value);
    }

    /// <summary>
    /// ENVCHANGE for transaction lifecycle: begin carries the new 8-byte
    /// transaction descriptor; commit and rollback carry empty values.
    /// </summary>
    public void WriteEnvChangeTransaction(byte type, ulong newDescriptor)
    {
        this.WriteByte(Tds.TokenEnvChange);
        if (type == Tds.EnvBeginTransaction)
        {
            this.WriteUInt16(1 + 1 + 8 + 1);
            this.WriteByte(type);
            this.WriteByte(8);
            this.WriteUInt64(newDescriptor);
            this.WriteByte(0);
        }
        else
        {
            this.WriteUInt16(3);
            this.WriteByte(type);
            this.WriteByte(0);
            this.WriteByte(0);
        }
    }

    private void Ensure(int more)
    {
        var needed = this.length + more;
        if (needed <= this.buffer.Length)
            return;

        var newSize = this.buffer.Length * 2;
        while (newSize < needed)
            newSize *= 2;

        Array.Resize(ref this.buffer, newSize);
    }
}
