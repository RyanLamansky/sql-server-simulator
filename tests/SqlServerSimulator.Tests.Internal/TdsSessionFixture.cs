using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SqlServerSimulator.Network;

namespace SqlServerSimulator;

/// <summary>
/// A <see cref="TdsSession"/> over an open in-process connection, with no
/// socket or login, that runs one request at a time and returns the response's
/// token bytes (packet header stripped) — for asserting token streams SqlClient
/// consumes without surfacing.
/// </summary>
internal sealed class TdsSessionFixture : IDisposable
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly X509Certificate2 certificate;
    private readonly TdsSession session;
    private readonly SimulatedDbConnection connection;

    public TdsSessionFixture()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=sss-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // A cert the session only stores (no TLS handshake runs in-test);
        // the validity window is nominal.
        this.certificate = request.CreateSelfSigned(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var simulation = new Simulation();
        this.session = new TdsSession(simulation, this.socket, this.certificate);
        this.connection = simulation.CreateDbConnection();
        this.connection.Open();
    }

    /// <summary>Session nesting depth as the engine sees it.</summary>
    public int TranCount => this.connection.CurrentTransaction?.TranCount ?? 0;

    /// <summary>
    /// Ends the session's transaction the way a transaction-aborting error
    /// does — through the engine, behind the TM layer's back.
    /// </summary>
    public void EndTransactionInTheEngine() => this.connection.CurrentTransaction?.Rollback();

    /// <summary>Runs a transaction-manager request.</summary>
    public byte[] Run(TdsMessage message) =>
        this.Respond(writer => this.session.RunTransactionManagerRequestForTesting(this.connection, message, writer));

    /// <summary>Runs a SQL batch, with an empty ALL_HEADERS ahead of its text.</summary>
    public byte[] RunBatch(string sql)
    {
        var text = Encoding.Unicode.GetBytes(sql);
        var payload = new byte[4 + text.Length];
        payload[0] = 4;
        text.CopyTo(payload, 4);
        var message = new TdsMessage(Tds.PacketSqlBatch, 0x01, payload);
        return this.Respond(writer => this.session.RunBatchForTesting(this.connection, message, writer));
    }

    private byte[] Respond(Action<TdsTokenWriter> request)
    {
        var stream = new MemoryStream();
        var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
        var writer = new TdsTokenWriter(transport);
        request(writer);
        writer.FlushAsync(final: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return stream.ToArray()[Tds.HeaderSize..];
    }

    /// <summary>
    /// The response's tokens as short text — <c>ENV8 new=1</c>, <c>ENV10 old=1</c>,
    /// <c>ERR 8134</c>, <c>INFO 3621</c>, <c>DONE</c> — for a response carrying no
    /// result set.
    /// </summary>
    public static List<string> Tokens(byte[] response)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < response.Length)
        {
            var token = response[i++];
            switch (token)
            {
                case Tds.TokenEnvChange:
                    {
                        var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                        var body = response.AsSpan(i + 2, length);
                        if (body[0] is Tds.EnvBeginTransaction)
                            tokens.Add($"ENV8 new={BinaryPrimitives.ReadUInt64LittleEndian(body[2..])}");
                        else if (body[0] is Tds.EnvCommitTransaction or Tds.EnvRollbackTransaction)
                            tokens.Add($"ENV{body[0]} old={BinaryPrimitives.ReadUInt64LittleEndian(body[3..])}");
                        i += 2 + length;
                        break;
                    }
                case Tds.TokenError:
                case Tds.TokenInfo:
                    {
                        var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                        tokens.Add($"{(token == Tds.TokenError ? "ERR" : "INFO")} {BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(i + 2))}");
                        i += 2 + length;
                        break;
                    }
                case Tds.TokenDone:
                case Tds.TokenDoneProc:
                case Tds.TokenDoneInProc:
                    tokens.Add("DONE");
                    i += 12;
                    break;
                case Tds.TokenReturnStatus:
                    i += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unexpected token 0x{token:X2}.");
            }
        }
        return tokens;
    }

    public void Dispose()
    {
        this.connection.Dispose();
        this.session.Dispose();
        this.certificate.Dispose();
        this.socket.Dispose();
    }
}
