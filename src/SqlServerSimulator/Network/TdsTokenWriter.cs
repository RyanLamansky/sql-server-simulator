using System.Buffers.Binary;
using SqlServerSimulator.Storage;

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
    /// True when the buffer ends at a complete-token boundary — nothing
    /// half-written. Every self-contained token method here leaves it true (each
    /// runs to completion synchronously); the only writers that interleave a
    /// throw-capable sub-write between bytes of a single token — COLMETADATA and
    /// ROW (their per-column <c>WriteTypeInfo</c> / <c>WriteValue</c>) and
    /// RETURNVALUE — bracket their body with <see cref="EnterComposite"/> /
    /// <see cref="LeaveComposite"/>, so a throw mid-token leaves this false. The
    /// terminal crash backstop reads it to decide whether an in-band severe-error
    /// token can be appended without desyncing the stream (already-flushed bytes
    /// stay well-formed because their remainder is still buffered, and an ERROR
    /// token legally follows any complete tokens — even a partial result set).
    /// </summary>
    public bool AtTokenBoundary = true;

    /// <summary>Marks the start of a multi-write token whose body may throw; pair with <see cref="LeaveComposite"/>.</summary>
    public void EnterComposite() => this.AtTokenBoundary = false;

    /// <summary>Marks a composite token fully written; the buffer is at a token boundary again.</summary>
    public void LeaveComposite() => this.AtTokenBoundary = true;

    /// <summary>
    /// When set, non-final flushes accumulate rather than send — the MARS path
    /// buffers what a request's statement produces under the connection's
    /// execution gate and sends it between statements
    /// (<see cref="FlushCompletePacketsAsync"/>) or on the final flush, so a
    /// window-blocked send never stalls another session's execution.
    /// </summary>
    public bool DeferFlush;

    /// <summary>
    /// The MARS logical session this writer's packets ride, whose send window
    /// says whether what is buffered can go out without waiting on the
    /// client; null outside MARS.
    /// </summary>
    public SmpSession? MarsSession;

    /// <summary>
    /// The client negotiated vector support at login, so a float32
    /// <c>vector</c> column or output parameter goes out as the native type
    /// (<c>0xF5</c>) rather than its down-level <c>varchar(max)</c> text.
    /// </summary>
    public bool NativeVector;

    /// <summary>
    /// The client negotiated JSON support at login, so a <c>json</c> value
    /// goes out as the native type (<c>0xF4</c>) rather than its down-level
    /// <c>varchar(max)</c> text.
    /// </summary>
    public bool NativeJson;

    /// <summary>
    /// Runs once the response's last packet is ready, just before it is sent:
    /// the MARS path ends the request's in-flight count here, so a client that
    /// acts on the response's end — a commit on another session — never finds
    /// the request still counted.
    /// </summary>
    public Action? BeforeEndOfMessage;

    /// <summary>
    /// The MARS request whose response this writer is sending, which a DML
    /// statement's <c>OUTPUT</c> rows hold the session for while they go out
    /// (see <see cref="HoldSession"/>); null outside MARS.
    /// </summary>
    public SessionRequest? Request;

    /// <summary>The buffered stretches, start and end, that hold <see cref="Request"/>'s session.</summary>
    private readonly List<(int Start, int End)> sessionHolds = [];

    /// <summary>Where the result set being written started (see <see cref="MarkResultStart"/>).</summary>
    private int resultStart;

    /// <summary>Notes where the result set about to be written starts, for <see cref="HoldSession"/>.</summary>
    public void MarkResultStart() => this.resultStart = this.length;

    /// <summary>
    /// Marks the result set written since <see cref="MarkResultStart"/> — a
    /// DML statement's <c>OUTPUT</c> rows, which real can't suspend to run
    /// another request — as holding the session while any of it is going
    /// out: the statement is running once everything ahead of it has gone,
    /// and done once it has gone too.
    /// </summary>
    public void HoldSession()
    {
        if (this.Request is not null)
            this.sessionHolds.Add((this.resultStart, this.length));
    }

    /// <summary>
    /// Sends every full packet's worth of buffered bytes; when
    /// <paramref name="final"/>, sends the remainder with the end-of-message
    /// bit, completing the response. The response's last DONE stays unsent
    /// until <see cref="BeforeEndOfMessage"/> has run, so it can still
    /// <see cref="ReopenFinalDone">reopen</see> it.
    /// </summary>
    public async ValueTask FlushAsync(bool final, CancellationToken cancellationToken)
    {
        if (this.DeferFlush && !final)
            return;

        var capacity = this.transport.PacketSize - Tds.HeaderSize;
        var offset = 0;
        var holdFrom = final && this.BeforeEndOfMessage is not null && this.FinalDoneBuffered ? this.finalDoneAt : this.length;
        while (this.length - offset > capacity && offset + capacity <= holdFrom)
            offset = await this.SendPacketAsync(offset, capacity, cancellationToken).ConfigureAwait(false);

        if (final)
        {
            this.BeforeEndOfMessage?.Invoke();
            while (this.length - offset > capacity)
                offset = await this.SendPacketAsync(offset, capacity, cancellationToken).ConfigureAwait(false);
            this.NoteSending(offset, this.length - offset);
            await this.transport.WritePacketAsync(Tds.PacketTabularResult, this.buffer.AsMemory(offset, this.length - offset), endOfMessage: true, cancellationToken).ConfigureAwait(false);
            offset = this.length;
            this.sessionHolds.Clear();
            if (this.Request is { } request)
                request.HoldsSession = false;
        }

        this.Compact(offset);
    }

    /// <summary>
    /// How many whole packets are buffered ahead of the response's last DONE,
    /// which stays unsent while it may yet be reopened or taken back.
    /// </summary>
    public int CompletePacketsPending()
    {
        var capacity = this.transport.PacketSize - Tds.HeaderSize;
        var holdFrom = this.FinalDoneBuffered ? this.finalDoneAt : this.length;
        // The response's end needs at least one byte for its last packet.
        return Math.Min(holdFrom, this.length - 1) / capacity;
    }

    /// <summary>
    /// Sends the whole packets buffered ahead of the response's last DONE
    /// (see <see cref="CompletePacketsPending"/>), waiting on the client's
    /// window as each goes: what a MARS request's finished statements
    /// produced, sent before its next statement runs.
    /// </summary>
    public async ValueTask FlushCompletePacketsAsync(CancellationToken cancellationToken)
    {
        var capacity = this.transport.PacketSize - Tds.HeaderSize;
        var offset = 0;
        for (var packets = this.CompletePacketsPending(); packets > 0; packets--)
            offset = await this.SendPacketAsync(offset, capacity, cancellationToken).ConfigureAwait(false);
        this.Compact(offset);
        // What still holds the session is what hasn't gone yet.
        if (this.Request is { } request)
            request.HoldsSession = this.sessionHolds.Count > 0;
    }

    /// <summary>Drops the <paramref name="sent"/> bytes from the front of the buffer, moving every position kept into it.</summary>
    private void Compact(int sent)
    {
        if (sent == 0)
            return;
        Buffer.BlockCopy(this.buffer, sent, this.buffer, 0, this.length - sent);
        this.length -= sent;
        this.trailingDoneAt -= sent;
        this.finalDoneAt -= sent;
        this.resultStart -= sent;
        for (var i = this.sessionHolds.Count - 1; i >= 0; i--)
        {
            var (start, end) = this.sessionHolds[i];
            if (end <= sent)
                this.sessionHolds.RemoveAt(i);
            else
                this.sessionHolds[i] = (Math.Max(start - sent, 0), end - sent);
        }
    }

    /// <summary>
    /// Clears the more bit of the response's last DONE while it is still the
    /// last thing buffered — a DONE written while another statement was still
    /// to run, which produced nothing — first writing the
    /// <paramref name="ahead"/> tokens a final DONE follows; false when the
    /// buffer doesn't end in a DONE carrying the bit.
    /// </summary>
    public bool TryCloseFinalDone(Action<TdsTokenWriter> ahead)
    {
        if (!this.FinalDoneBuffered)
            return false;
        var at = this.finalDoneAt;
        var status = BinaryPrimitives.ReadUInt16LittleEndian(this.buffer.AsSpan(at + 1));
        if ((status & Tds.DoneMore) == 0)
            return false;
        var done = this.buffer.AsSpan(at, DoneTokenLength).ToArray();
        var trailing = this.trailingDoneAt == at;
        this.length = at;
        this.finalDoneAt = this.trailingDoneAt = -1;
        ahead(this);
        BinaryPrimitives.WriteUInt16LittleEndian(done.AsSpan(1), (ushort)(status & ~Tds.DoneMore));
        if (trailing)
            this.trailingDoneAt = this.length;
        this.finalDoneAt = this.length;
        this.WriteBytes(done);
        return true;
    }

    /// <summary>
    /// Sends one full packet from <paramref name="offset"/>, releasing the
    /// session a DML statement's rows held once they are all out, and returns
    /// where the next packet starts.
    /// </summary>
    private async ValueTask<int> SendPacketAsync(int offset, int capacity, CancellationToken cancellationToken)
    {
        this.NoteSending(offset, capacity);
        await this.transport.WritePacketAsync(Tds.PacketTabularResult, this.buffer.AsMemory(offset, capacity), endOfMessage: false, cancellationToken).ConfigureAwait(false);
        return offset + capacity;
    }

    /// <summary>
    /// Whether the packet about to go out — <paramref name="count"/> bytes from
    /// <paramref name="offset"/>, which may wait on the client — carries a
    /// stretch that holds the session.
    /// </summary>
    private void NoteSending(int offset, int count)
    {
        if (this.Request is not { } request)
            return;
        var holds = false;
        foreach (var (start, end) in this.sessionHolds)
            holds |= offset < end && offset + count > start;
        request.HoldsSession = holds;
    }

    /// <summary>
    /// Where the last DONE, DONEPROC or DONEINPROC written starts, while it
    /// may still be the buffer's final token (see <see cref="ReopenFinalDone"/>).
    /// </summary>
    private int finalDoneAt = -1;

    private bool FinalDoneBuffered => this.finalDoneAt >= 0 && this.finalDoneAt + DoneTokenLength == this.length;

    /// <summary>
    /// Sets the more bit on the response's closing DONE token while it is
    /// still the last thing buffered and unsent, so tokens can follow it;
    /// false when there is no such token.
    /// </summary>
    public bool ReopenFinalDone()
    {
        if (!this.FinalDoneBuffered)
            return false;
        var status = this.buffer.AsSpan(this.finalDoneAt + 1, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(status, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(status) | Tds.DoneMore));
        return true;
    }

    /// <summary>
    /// Where the last batch-level DONE written starts, while it may still be
    /// the buffer's final token (see <see cref="TryTakeTrailingDone"/>).
    /// </summary>
    private int trailingDoneAt = -1;

    /// <summary>
    /// Takes back a batch-level DONE that is still the last thing buffered —
    /// no token written after it and no flush having sent it — handing back
    /// the statement kind it named. An attention acknowledgment folds such a
    /// DONE into the <c>DONE_ERROR</c> that ends the interrupted response, as
    /// real, whose statement DONE waits for the next token, does.
    /// </summary>
    public bool TryTakeTrailingDone(out ushort curCmd)
    {
        var at = this.trailingDoneAt;
        if (at < 0 || at + DoneTokenLength != this.length)
        {
            curCmd = 0;
            return false;
        }
        curCmd = BinaryPrimitives.ReadUInt16LittleEndian(this.buffer.AsSpan(at + 3));
        this.length = at;
        this.trailingDoneAt = -1;
        return true;
    }

    private const int DoneTokenLength = 13;

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
        _ = SystemNameSqlType.Utf16LeEncode(value, this.buffer.AsSpan(this.length));
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
        // An empty message goes out as one space, as an uncaught THROW of an
        // empty or all-escape message shows (probed 2026-09-30 against SQL Server 2025).
        this.WriteUsVarchar(message.Length == 0 ? " " : message);
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

    /// <summary>
    /// FEATUREEXTACK (<c>0xAE</c>): each acknowledged feature's id, a DWORD
    /// data length and its one-byte version, then the 0xFF terminator
    /// (MS-TDS 2.2.7.11).
    /// </summary>
    public void WriteFeatureExtAck(ReadOnlySpan<(byte FeatureId, byte Version)> features)
    {
        this.WriteByte(Tds.TokenFeatureExtAck);
        foreach (var (featureId, version) in features)
        {
            this.WriteByte(featureId);
            this.WriteUInt32(1);
            this.WriteByte(version);
        }
        this.WriteByte(0xFF);
    }

    public void WriteDone(ushort status, long rowCount) => this.WriteDoneToken(Tds.TokenDone, status, rowCount);

    /// <summary>
    /// Marks the next DONE token written with <c>DONE_ERROR</c> and clears its
    /// <c>DONE_COUNT</c>, keeping the count itself — what real does to the DONE
    /// that follows an error sent with no DONE of its own, a compile's
    /// non-aborting one (captured 2026-09-30 against SQL Server 2025).
    /// </summary>
    public void CarryErrorToNextDone() => this.errorCarriedToNextDone = true;

    private bool errorCarriedToNextDone;

    /// <summary>
    /// DONE, DONEPROC, and DONEINPROC share one 13-byte layout. <paramref name="curCmd"/>
    /// is the kind of statement that produced the token (<see cref="StatementDoneKind"/>).
    /// </summary>
    public void WriteDoneToken(byte token, ushort status, long rowCount, ushort curCmd = 0)
    {
        if (this.errorCarriedToNextDone)
        {
            this.errorCarriedToNextDone = false;
            status = (ushort)((status | Tds.DoneError) & ~Tds.DoneCount);
        }
        if (token == Tds.TokenDone)
            this.trailingDoneAt = this.length;
        this.finalDoneAt = this.length;
        this.WriteByte(token);
        this.WriteUInt16(status);
        this.WriteUInt16(curCmd);
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
    public void WriteEnvChangeTransaction(byte type, ulong descriptor)
    {
        // A transaction ENVCHANGE always carries the 8-byte descriptor: BEGIN
        // in the new-value field (the transaction being opened), COMMIT /
        // ROLLBACK in the old-value field (the one being ended) — matching real
        // SQL Server byte-for-byte (captured 2026-07-23). The old stunted len-3
        // commit / rollback form (no descriptor) desynced ODBC Driver 18's
        // manual-commit mode, which pairs the descriptor across begin/end.
        this.WriteByte(Tds.TokenEnvChange);
        this.WriteUInt16(1 + 1 + 8 + 1);
        this.WriteByte(type);
        if (type == Tds.EnvBeginTransaction)
        {
            this.WriteByte(8);
            this.WriteUInt64(descriptor);
            this.WriteByte(0);
        }
        else
        {
            this.WriteByte(0);
            this.WriteByte(8);
            this.WriteUInt64(descriptor);
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
