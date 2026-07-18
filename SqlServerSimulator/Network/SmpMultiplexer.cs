using System.Buffers.Binary;

namespace SqlServerSimulator.Network;

/// <summary>
/// The per-connection owner an <see cref="SmpMultiplexer"/> drives: it runs one
/// TDS batch loop per SMP session and cancels the executing session on a
/// targeted attention. <see cref="TdsSession"/> is the production
/// implementation; the seam lets frame-level tests drive the multiplexer with a
/// canned session runner (no TLS / login / engine).
/// </summary>
internal interface ISmpHost
{
    Task RunMarsSessionAsync(SmpSession session, CancellationToken cancellationToken);

    void CancelConnectionExecution();
}

/// <summary>
/// The SMP (Session Multiplex Protocol, [MC-SMP]) demux/mux that sits below the
/// per-session TDS reader/writer on a MARS-negotiated connection. It owns the
/// post-login socket stream: a single read loop reads 16-byte SMP frames and
/// demultiplexes them into per-session logical streams, while writes wrap each
/// outbound TDS packet in an SMP DATA frame with per-session sequence numbers
/// and honor the peer's receive window. Each SMP session drives its own TDS
/// batch loop against the one shared <see cref="SimulatedDbConnection"/>, with
/// engine execution serialized by a per-connection semaphore — cooperative
/// multiplexing, never parallel execution against the engine.
/// </summary>
internal sealed class SmpMultiplexer(Stream stream, ISmpHost owner) : IDisposable
{
    private readonly Dictionary<ushort, SmpSession> sessions = [];
    private readonly Lock sessionsGate = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public void Dispose() => this.writeGate.Dispose();

    /// <summary>
    /// Runs the SMP read loop until the client closes the connection. Each
    /// inbound frame is dispatched by flag; SYN spawns a logical session, DATA
    /// feeds one, ACK advances a session's send window, FIN closes one.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var header = new byte[Tds.SmpHeaderSize];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (header[0] != Tds.SmpSmid)
                    throw new InvalidDataException($"Expected SMP frame identifier 0x53, saw 0x{header[0]:x2}.");

                await stream.ReadExactlyAsync(header.AsMemory(1, Tds.SmpHeaderSize - 1), cancellationToken).ConfigureAwait(false);
                var flags = header[1];
                var sid = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var window = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                var payloadLength = checked((int)length - Tds.SmpHeaderSize);
                byte[] payload = [];
                if (payloadLength > 0)
                {
                    payload = new byte[payloadLength];
                    await stream.ReadExactlyAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                // The WNDW field on every inbound frame — SYN, DATA, ACK, FIN
                // alike — advertises the client's receive window, i.e. the
                // highest sequence number we may send on this session. The
                // client piggybacks it on its DATA frames and sends no standalone
                // ACKs, so updating it only on ACK would stall our send window.
                if ((flags & Tds.SmpFlagSyn) != 0)
                {
                    await this.OpenSessionAsync(sid, window, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                this.AdvanceWindow(sid, window);
                if ((flags & Tds.SmpFlagData) != 0)
                    await this.DispatchDataAsync(sid, payload, cancellationToken).ConfigureAwait(false);
                else if ((flags & Tds.SmpFlagFin) != 0)
                    await this.CloseSessionAsync(sid).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException or System.Security.Authentication.AuthenticationException or EndOfStreamException)
        {
            // Client disconnect / teardown / malformed traffic: end the mux.
        }
        finally
        {
            List<SmpSession> live;
            lock (this.sessionsGate)
            {
                live = [.. this.sessions.Values];
                this.sessions.Clear();
            }

            foreach (var session in live)
                await session.InboundWriter.CompleteAsync().ConfigureAwait(false);
        }
    }

    private ValueTask OpenSessionAsync(ushort sid, uint peerWindow, CancellationToken cancellationToken)
    {
        SmpSession session;
        lock (this.sessionsGate)
        {
            if (this.sessions.ContainsKey(sid))
                return ValueTask.CompletedTask;
            session = new SmpSession(sid, this, peerWindow);
            this.sessions[sid] = session;
        }

        // A client SYN opens the session; the real server sends NO server SYN in
        // reply (probe-confirmed 2026-07-18 — its first server-to-client frame on
        // a session is the DATA response). Emitting a server SYN is a protocol
        // violation that Windows native SNI rejects with "Physical connection is
        // not usable" (SMux error 19); managed SNI on Linux tolerated it.
        _ = owner.RunMarsSessionAsync(session, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private async ValueTask DispatchDataAsync(ushort sid, byte[] payload, CancellationToken cancellationToken)
    {
        SmpSession? session;
        lock (this.sessionsGate)
            _ = this.sessions.TryGetValue(sid, out session);
        if (session is null)
            return;

        session.ReceivedSequence++;

        // The SMP DATA payload is a complete TDS packet (header byte 1 carries
        // the TDS EOM bit). A TDS attention (type 6) targets one session: if that
        // session is mid-execution its batch loop isn't reading its pipe, so fire
        // cancellation directly and let the loop ack DONE_ATTN when it unwinds
        // (consumed here, not fed to the pipe). The real server sends a standalone
        // ACK when it takes in client data it will not answer with an immediate
        // DATA frame — an attention during a running command, matching the probed
        // cadence — so ack here too before returning.
        if (payload.Length >= 1 && payload[0] == Tds.PacketAttention)
        {
            _ = Interlocked.Exchange(ref session.AttentionState, 1);
            if (session.Executing)
            {
                owner.CancelConnectionExecution();
                await this.SendControlFrameAsync(session, Tds.SmpFlagAck, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var memory = session.InboundWriter.GetMemory(payload.Length);
        payload.CopyTo(memory);
        session.InboundWriter.Advance(payload.Length);
        _ = await session.InboundWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Window management, matching the probed real-server cadence: a complete
        // (EOM-set) request gets NO standalone ACK — the DATA response the batch
        // loop produces piggybacks the advanced receive window (received + slack).
        // A mid-message packet (EOM clear) of a large multi-packet client request
        // WILL get an ACK, since no response comes until the whole message
        // arrives, and the client's send window would otherwise stall at its
        // initial size. Emitting an ACK per received packet in the common
        // request/response case is exactly the extra chatter native SNI treats as
        // a violation, so it is confined to the mid-message case.
        var endOfMessage = payload.Length >= 2 && (payload[1] & Tds.StatusEndOfMessage) != 0;
        if (!endOfMessage)
            await this.SendControlFrameAsync(session, Tds.SmpFlagAck, cancellationToken).ConfigureAwait(false);
    }

    private void AdvanceWindow(ushort sid, uint peerWindow)
    {
        SmpSession? session;
        lock (this.sessionsGate)
            _ = this.sessions.TryGetValue(sid, out session);
        session?.SetPeerWindow(peerWindow);
    }

    private async ValueTask CloseSessionAsync(ushort sid)
    {
        SmpSession? session;
        lock (this.sessionsGate)
        {
            if (!this.sessions.Remove(sid, out session))
                return;
        }

        await session.InboundWriter.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps one outbound TDS packet in an SMP DATA frame for the session,
    /// blocking until the peer's send window permits, then writing under the
    /// serialized socket-write gate. Called by the session's logical stream.
    /// </summary>
    public async ValueTask SendDataAsync(SmpSession session, ReadOnlyMemory<byte> tdsPacket, CancellationToken cancellationToken)
    {
        await session.WaitForSendWindowAsync(cancellationToken).ConfigureAwait(false);
        var seq = ++session.SendSequence;
        var frame = new byte[Tds.SmpHeaderSize + tdsPacket.Length];
        BuildHeader(frame, Tds.SmpFlagData, session.Sid, (uint)frame.Length, seq, session.ReceiveWindow);
        tdsPacket.Span.CopyTo(frame.AsSpan(Tds.SmpHeaderSize));
        await this.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendControlFrameAsync(SmpSession session, byte flags, CancellationToken cancellationToken)
    {
        var frame = new byte[Tds.SmpHeaderSize];
        BuildHeader(frame, flags, session.Sid, Tds.SmpHeaderSize, session.SendSequence, session.ReceiveWindow);
        await this.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends the session's FIN once its response is fully written.</summary>
    public ValueTask SendFinAsync(SmpSession session, CancellationToken cancellationToken)
    {
        lock (this.sessionsGate)
            _ = this.sessions.Remove(session.Sid);
        return this.SendControlFrameAsync(session, Tds.SmpFlagFin, cancellationToken);
    }

    private static void BuildHeader(Span<byte> frame, byte flags, ushort sid, uint length, uint seq, uint window)
    {
        frame[0] = Tds.SmpSmid;
        frame[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(frame[2..], sid);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[4..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[12..], window);
    }

    private async ValueTask WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = this.writeGate.Release();
        }
    }
}
