using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using SqlServerSimulator.Network;

namespace SqlServerSimulator;

/// <summary>
/// A bare TDS 8.0 client over a listener's socket, for the requests no driver
/// sends on its own — a bulk-load packet carrying a <c>WRITETEXT BULK</c>'s
/// data, a request arriving while one waits for it — sent and answered one
/// message at a time, each response read whole and handed back as its token
/// bytes for <see cref="TdsSessionFixture.Describe"/>.
/// </summary>
internal sealed class RawTdsClient : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly SslStream stream;

    private RawTdsClient(TcpClient tcp, SslStream stream)
    {
        this.tcp = tcp;
        this.stream = stream;
    }

    /// <summary>
    /// Connects with strict encryption (TLS first, ALPN <c>tds/8.0</c>), then
    /// sends the prelogin and a SQL login as <c>sa</c> into
    /// <paramref name="database"/>.
    /// </summary>
    public static async Task<RawTdsClient> ConnectAsync(SimulatedNetworkListener listener, string database, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", listener.Port, cancellationToken);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [new SslApplicationProtocol("tds/8.0")],
            },
            cancellationToken);
        var client = new RawTdsClient(tcp, ssl);
        // VERSION, ENCRYPTION (strict), TERMINATOR.
        _ = await client.SendAsync(Tds.PacketPrelogin, [0x00, 0x00, 0x0B, 0x00, 0x06, 0x01, 0x00, 0x11, 0x00, 0x01, 0xFF, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03], cancellationToken);
        _ = await client.SendAsync(Tds.PacketLogin7, Login7("sa", "anything", database), cancellationToken);
        return client;
    }

    /// <summary>Sends a SQL batch, with a transaction-descriptor ALL_HEADERS ahead of its text.</summary>
    public Task<byte[]> BatchAsync(string sql, CancellationToken cancellationToken) =>
        this.SendAsync(Tds.PacketSqlBatch, [.. AllHeaders(), .. Encoding.Unicode.GetBytes(sql)], cancellationToken);

    /// <summary>Sends an RPC request whose payload is <paramref name="body"/>, after the transaction-descriptor ALL_HEADERS.</summary>
    public Task<byte[]> RpcAsync(byte[] body, CancellationToken cancellationToken) =>
        this.SendAsync(Tds.PacketRpc, [.. AllHeaders(), .. body], cancellationToken);

    /// <summary>Sends a transaction-manager begin request.</summary>
    public Task<byte[]> BeginTransactionAsync(CancellationToken cancellationToken) =>
        this.SendAsync(Tds.PacketTransactionManager, [.. AllHeaders(), 0x05, 0x00, 0x00, 0x00], cancellationToken);

    /// <summary>
    /// Sends one message and reads the response through its last packet; an
    /// empty array when the server closed the connection instead.
    /// </summary>
    public async Task<byte[]> SendAsync(byte packetType, byte[] payload, CancellationToken cancellationToken)
    {
        const int maxData = Tds.DefaultPacketSize - Tds.HeaderSize;
        var offset = 0;
        byte packetId = 1;
        do
        {
            var length = Math.Min(maxData, payload.Length - offset);
            var packet = new byte[Tds.HeaderSize + length];
            packet[0] = packetType;
            packet[1] = (byte)(offset + length == payload.Length ? 0x01 : 0x00);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            packet[6] = packetId++;
            payload.AsSpan(offset, length).CopyTo(packet.AsSpan(Tds.HeaderSize));
            await this.stream.WriteAsync(packet, cancellationToken);
            offset += length;
        }
        while (offset < payload.Length);
        await this.stream.FlushAsync(cancellationToken);

        var response = new List<byte>();
        var header = new byte[Tds.HeaderSize];
        while (true)
        {
            if (!await this.ReadExactlyOrEndAsync(header, cancellationToken))
                return [.. response];
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - Tds.HeaderSize];
            if (!await this.ReadExactlyOrEndAsync(body, cancellationToken))
                return [.. response];
            response.AddRange(body);
            if ((header[1] & 0x01) != 0)
                return [.. response];
        }
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await this.stream.ReadExactlyAsync(buffer, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return false;
        }
    }

    private static byte[] AllHeaders()
    {
        var headers = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(headers, 22);
        BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(4), 18);
        BinaryPrimitives.WriteUInt16LittleEndian(headers.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(headers.AsSpan(18), 1);
        return headers;
    }

    private static byte[] Login7(string user, string password, string database)
    {
        string[] fields = ["raw", user, password, "raw", "localhost", "", "raw", "", database];
        const int fixedLength = 94;
        var data = new List<byte>();
        var offsets = new byte[fixedLength - 36];
        for (var i = 0; i < fields.Length; i++)
        {
            var bytes = Encoding.Unicode.GetBytes(fields[i]);
            if (i == 2)
            {
                for (var b = 0; b < bytes.Length; b++)
                    bytes[b] = (byte)(((bytes[b] << 4) | (bytes[b] >> 4)) ^ 0xA5);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan(i * 4), (ushort)(fixedLength + data.Count));
            BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan((i * 4) + 2), (ushort)fields[i].Length);
            data.AddRange(bytes);
        }
        // ClientID, then the empty SSPI / AtchDBFile / ChangePassword fields.
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan(42 + (i * 4)), (ushort)(fixedLength + data.Count));
        var login = new byte[fixedLength + data.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(login, (uint)login.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(login.AsSpan(4), Tds.Version8);
        BinaryPrimitives.WriteUInt32LittleEndian(login.AsSpan(8), Tds.DefaultPacketSize);
        login[24] = 0xE0;
        login[25] = 0x03;
        offsets.CopyTo(login.AsSpan(36));
        data.CopyTo(login, fixedLength);
        return login;
    }

    public async ValueTask DisposeAsync()
    {
        await this.stream.DisposeAsync();
        this.tcp.Dispose();
    }
}
