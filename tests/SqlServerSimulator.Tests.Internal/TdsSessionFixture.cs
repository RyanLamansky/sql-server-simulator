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

    public TdsSessionFixture(Simulation? simulation = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=sss-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // A cert the session only stores (no TLS handshake runs in-test);
        // the validity window is nominal.
        this.certificate = request.CreateSelfSigned(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        simulation ??= new Simulation();
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

        // Every packet's header stripped, a response long enough to span
        // several included.
        var packets = stream.ToArray();
        var tokens = new List<byte>();
        for (var offset = 0; offset < packets.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(packets.AsSpan(offset + 2));
            tokens.AddRange(packets.AsSpan(offset + Tds.HeaderSize, length - Tds.HeaderSize));
            offset += length;
        }
        return [.. tokens];
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
    /// <c>ENVn</c> for an ENVCHANGE, <c>ERR n</c> / <c>INFO n</c>,
    /// <c>ROWS</c> for a result set — whose rows, ROW or NBCROW, may carry fixed-width, integer,
    /// decimal and non-MAX string or binary columns — and <c>ORDER 2,1</c> for
    /// an ORDER token.
    /// </summary>
    public static List<string> Describe(byte[] response)
    {
        var tokens = new List<string>();
        // Per column, a fixed width (positive) or the width of the value's
        // length prefix (negative).
        var widths = new List<int>();
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
                case Tds.TokenOrder:
                    {
                        var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                        var columns = new List<ushort>();
                        for (var c = 0; c < length; c += 2)
                            columns.Add(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i + 2 + c)));
                        tokens.Add("ORDER " + string.Join(',', columns));
                        i += 2 + length;
                        break;
                    }
                case Tds.TokenColMetadata:
                    {
                        var columns = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i));
                        i += 2;
                        widths.Clear();
                        for (var c = 0; c < columns; c++)
                        {
                            i += 6; // user type, flags
                            var type = response[i++];
                            switch (type)
                            {
                                case 0x24: // GUID
                                case 0x26: // INTN
                                    widths.Add(-1);
                                    i++;
                                    break;
                                case 0x30 or 0x32: // INT1, BIT
                                    widths.Add(1);
                                    break;
                                case 0x34: // INT2
                                    widths.Add(2);
                                    break;
                                case 0x38 or 0x3A or 0x3B or 0x7A: // INT4, DATETIM4, FLT4, MONEY4
                                    widths.Add(4);
                                    break;
                                case 0x3C or 0x3D or 0x3E: // MONEY, DATETIME, FLT8
                                    widths.Add(8);
                                    break;
                                case 0x68 or 0x6D or 0x6E or 0x6F: // BITN, FLTN, MONEYN, DATETIMN
                                    widths.Add(-1);
                                    i++;
                                    break;
                                case 0x6A or 0x6C: // DECIMALN, NUMERICN
                                    widths.Add(-1);
                                    i += 3;
                                    break;
                                case 0x7F: // INT8
                                    widths.Add(8);
                                    break;
                                case 0xA5 or 0xAD: // the binary pair
                                    widths.Add(-2);
                                    i += 2;
                                    break;
                                case 0xA7 or 0xAF or 0xE7 or 0xEF: // the string families, with their collation
                                    widths.Add(-2);
                                    i += 7;
                                    break;
                                default:
                                    throw new InvalidDataException($"Unexpected column type 0x{type:X2}.");
                            }
                            i += 1 + (response[i] * 2); // name
                        }
                        tokens.Add("ROWS");
                        break;
                    }
                case Tds.TokenRow:
                case Tds.TokenNbcRow:
                    {
                        // An NBCROW leads with one bit per column, and a NULL's
                        // bit stands for its value.
                        var bitmap = token == Tds.TokenNbcRow ? i : -1;
                        if (token == Tds.TokenNbcRow)
                            i += (widths.Count + 7) / 8;
                        for (var c = 0; c < widths.Count; c++)
                        {
                            if (bitmap >= 0 && (response[bitmap + (c / 8)] & (1 << (c % 8))) != 0)
                                continue;
                            var width = widths[c];
                            i += width switch
                            {
                                > 0 => width,
                                -1 => 1 + response[i],
                                _ => BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i)) is var length && length == 0xFFFF ? 2 : 2 + length,
                            };
                        }
                        break;
                    }
                case Tds.TokenReturnStatus:
                    tokens.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RET {BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(i))}"));
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
