using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Frame-level regression guard pinning the server-to-client SMP (Session
/// Multiplex Protocol) shape the real SQL Server 2025 emits — captured
/// cleartext (Encrypt=False login-only encryption) via a tee proxy on
/// 2026-07-18. The critical facts, and why they matter: Windows native SNI
/// validates SMUX strictly and drops the physical connection ("Physical
/// connection is not usable", SMux error 19) on shapes managed SNI (Linux)
/// tolerates. These assertions fail on Linux if the multiplexer regresses,
/// rather than only surfacing on a Windows host.
/// <list type="bullet">
/// <item>The server sends NO SYN frame — a client SYN opens a session and the
/// server's first frame on it is the DATA response.</item>
/// <item>A complete (EOM) request gets NO standalone ACK; its DATA response
/// piggybacks the advanced receive window (received + slack).</item>
/// <item>A mid-message (EOM-clear) packet of a multi-packet request DOES get a
/// standalone ACK advancing the window, since no response comes until the whole
/// message arrives.</item>
/// <item>Session close echoes a FIN whose SEQNUM is the last DATA sequence sent
/// and whose WNDW is received + slack.</item>
/// </list>
/// The multiplexer is driven directly over a loopback socket pair through the
/// <see cref="ISmpHost"/> seam with a canned session runner, so the test needs
/// no TLS / login / engine.
/// </summary>
[TestClass]
public sealed class SmpFrameTests
{
    public TestContext TestContext { get; set; } = null!;

    private const byte Syn = Tds.SmpFlagSyn;
    private const byte Ack = Tds.SmpFlagAck;
    private const byte Fin = Tds.SmpFlagFin;
    private const byte Data = Tds.SmpFlagData;

    private sealed record Frame(byte Flags, ushort Sid, uint Length, uint Seq, uint Window, byte[] Payload);

    /// <summary>
    /// Canned host: reads one whole TDS message per session then writes a single
    /// tabular-result packet as the response, mirroring the real session loop's
    /// FIN on completion. No engine involved.
    /// </summary>
    private sealed class CannedHost : ISmpHost
    {
        public SmpMultiplexer Multiplexer = null!;

        public async Task RunMarsSessionAsync(SmpSession session, CancellationToken cancellationToken)
        {
            using var stream = new SmpSessionStream(session);
            var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
            var message = await transport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is not null)
                await transport.WritePacketAsync(Tds.PacketTabularResult, new byte[] { Tds.TokenDone, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            await this.Multiplexer.SendFinAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        public void CancelConnectionExecution()
        {
        }
    }

    private static byte[] BuildFrame(byte flags, ushort sid, uint seq, uint window, byte[]? payload = null)
    {
        payload ??= [];
        var frame = new byte[Tds.SmpHeaderSize + payload.Length];
        frame[0] = Tds.SmpSmid;
        frame[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), sid);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), window);
        payload.CopyTo(frame.AsSpan(Tds.SmpHeaderSize));
        return frame;
    }

    /// <summary>A minimal single-packet SQLBatch TDS packet (header + trivial body).</summary>
    private static byte[] TdsBatchPacket(bool endOfMessage)
    {
        var body = new byte[] { 0, 0, 0, 0, 1, 0 }; // ALL_HEADERS-free trivial payload; content is irrelevant to framing.
        var packet = new byte[Tds.HeaderSize + body.Length];
        packet[0] = Tds.PacketSqlBatch;
        packet[1] = endOfMessage ? Tds.StatusEndOfMessage : (byte)0;
        packet[2] = (byte)((Tds.HeaderSize + body.Length) >> 8);
        packet[3] = (byte)(Tds.HeaderSize + body.Length);
        body.CopyTo(packet.AsSpan(Tds.HeaderSize));
        return packet;
    }

    private static async Task<List<Frame>> RunAsync(CancellationToken cancellationToken, params byte[][] clientFrames)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
        using var serverTcp = await acceptTask;
        await using var serverStream = serverTcp.GetStream();
        await using var clientStream = clientSocket.GetStream();

        var host = new CannedHost();
        using var mux = new SmpMultiplexer(serverStream, host);
        host.Multiplexer = mux;
        var muxTask = mux.RunAsync(cancellationToken);

        foreach (var frame in clientFrames)
        {
            await clientStream.WriteAsync(frame, cancellationToken);
            await clientStream.FlushAsync(cancellationToken);
        }

        var frames = new List<Frame>();
        var header = new byte[Tds.SmpHeaderSize];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                await clientStream.ReadExactlyAsync(header, readCts.Token);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var payload = new byte[length - Tds.SmpHeaderSize];
                if (payload.Length > 0)
                    await clientStream.ReadExactlyAsync(payload, readCts.Token);
                frames.Add(new Frame(
                    header[1],
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)),
                    length,
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
                    payload));

                // Stop once every opened session has been FIN-ed by the server.
                if ((header[1] & Fin) != 0)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
        }

        return frames;
    }

    [TestMethod]
    public async Task CompleteRequest_ServerEmitsNoSyn_DataThenFin()
    {
        var frames = await RunAsync(
            TestContext.CancellationToken,
            BuildFrame(Syn, sid: 1, seq: 0, window: 4),
            BuildFrame(Data, sid: 1, seq: 1, window: 4, TdsBatchPacket(endOfMessage: true)));

        var synCount = frames.Count(f => (f.Flags & Syn) != 0);
        var ackCount = frames.Count(f => (f.Flags & Ack) != 0);
        AreEqual(0, synCount, "Server must never emit a SYN frame (native SNI drops the connection on one).");
        AreEqual(0, ackCount, "A complete request needs no standalone ACK — the DATA response piggybacks the window.");

        var data = frames.Single(f => (f.Flags & Data) != 0);
        AreEqual((ushort)1, data.Sid);
        AreEqual(1u, data.Seq);
        AreEqual(5u, data.Window); // received 1 + slack 4

        var fin = frames.Single(f => (f.Flags & Fin) != 0);
        AreEqual((ushort)1, fin.Sid);
        AreEqual(1u, fin.Seq); // SEQNUM = last DATA sequence sent
        AreEqual(5u, fin.Window);
    }

    [TestMethod]
    public async Task MultiPacketRequest_MidMessagePacketGetsAck()
    {
        var frames = await RunAsync(
            TestContext.CancellationToken,
            BuildFrame(Syn, sid: 1, seq: 0, window: 4),
            BuildFrame(Data, sid: 1, seq: 1, window: 4, TdsBatchPacket(endOfMessage: false)),
            BuildFrame(Data, sid: 1, seq: 2, window: 5, TdsBatchPacket(endOfMessage: true)));

        var synCount = frames.Count(f => (f.Flags & Syn) != 0);
        AreEqual(0, synCount, "Server must never emit a SYN frame.");

        // The mid-message (EOM-clear) first packet is ACKed to advance the send
        // window; the completing second packet is not (its response piggybacks).
        var ack = frames.Single(f => f.Flags == Ack);
        AreEqual((ushort)1, ack.Sid);
        AreEqual(5u, ack.Window); // received 1 + slack 4

        var dataCount = frames.Count(f => (f.Flags & Data) != 0);
        var finCount = frames.Count(f => (f.Flags & Fin) != 0);
        IsGreaterThan(0, dataCount, "The completed request must produce a DATA response.");
        IsGreaterThan(0, finCount, "The session must be FIN-ed on completion.");
    }
}
