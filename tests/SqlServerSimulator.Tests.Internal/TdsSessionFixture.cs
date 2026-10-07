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

    /// <summary>
    /// The response's tokens as short text naming each DONE's kind, bits and
    /// count — <c>DONE D4 MORE 0</c>, <c>DONEINPROC C1 MORE|COUNT 1</c>,
    /// <c>DONE FD ERROR|ATTN 0</c>,
    /// <c>DONEPROC E0 FINAL 0</c> — with <c>RET n</c> for a RETURNSTATUS,
    /// <c>ENVn</c> for an ENVCHANGE, <c>ERR n</c> / <c>INFO n</c>, and
    /// <c>ROWS</c> for a result set, which may carry only <c>int</c> columns.
    /// </summary>
    public static List<string> Describe(byte[] response)
    {
        var tokens = new List<string>();
        var fixedInt = new List<bool>();
        var i = 0;
        while (i < response.Length)
        {
            var token = response[i++];
            switch (token)
            {
                case Tds.TokenEnvChange:
                    tokens.Add($"ENV{response[i + 2]}");
                    i += 2 + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                    break;
                case Tds.TokenError:
                case Tds.TokenInfo:
                    tokens.Add($"{(token == Tds.TokenError ? "ERR" : "INFO")} {BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(i + 2))}");
                    i += 2 + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                    break;
                case Tds.TokenTabName:
                case Tds.TokenColInfo:
                    i += 2 + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                    break;
                case Tds.TokenColMetadata:
                    {
                        var columns = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                        i += 2;
                        fixedInt.Clear();
                        for (var c = 0; c < columns; c++)
                        {
                            i += 6; // user type, flags
                            // INT4 (NOT NULL) is the type byte alone, INTN
                            // carries its length.
                            fixedInt.Add(response[i] == 0x38);
                            i += response[i] == 0x26 ? 2 : 1;
                            i += 1 + (response[i] * 2); // name
                        }
                        tokens.Add("ROWS");
                        break;
                    }
                case Tds.TokenRow:
                    foreach (var isFixed in fixedInt)
                        i += isFixed ? 4 : 1 + response[i];
                    break;
                case Tds.TokenReturnStatus:
                    tokens.Add($"RET {BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(i))}");
                    i += 4;
                    break;
                case Tds.TokenDone:
                case Tds.TokenDoneProc:
                case Tds.TokenDoneInProc:
                    {
                        var status = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                        var bits = new List<string>();
                        if ((status & Tds.DoneMore) != 0)
                            bits.Add("MORE");
                        if ((status & Tds.DoneError) != 0)
                            bits.Add("ERROR");
                        if ((status & Tds.DoneCount) != 0)
                            bits.Add("COUNT");
                        if ((status & Tds.DoneAttention) != 0)
                            bits.Add("ATTN");
                        if ((status & Tds.DoneServerError) != 0)
                            bits.Add("SRVERROR");
                        var name = token == Tds.TokenDone ? "DONE" : token == Tds.TokenDoneProc ? "DONEPROC" : "DONEINPROC";
                        tokens.Add($"{name} {BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i + 2)):X2} {(bits.Count == 0 ? "FINAL" : string.Join('|', bits))} {BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(i + 4))}");
                        i += 12;
                        break;
                    }
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
