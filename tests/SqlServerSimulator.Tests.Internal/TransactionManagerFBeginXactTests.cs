using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Byte-level regression guard for the TDS Transaction Manager commit /
/// rollback response, which the SqlClient oracle can't reach: SqlClient begins
/// each transaction explicitly and never sets <c>fBeginXact</c>, so the ODBC
/// manual-commit path (drive: pyodbc / SQLAlchemy) is exercised only here.
/// Real SQL Server 2025 (captured cleartext via a tee proxy, 2026-07-23):
/// a commit / rollback ENVCHANGE carries the ending transaction's 8-byte
/// descriptor in its old-value field, and when the request sets
/// <c>fBeginXact</c> the response opens the next transaction immediately — end
/// ENVCHANGE, then a begin ENVCHANGE with a fresh descriptor, then DONE. The
/// old stunted descriptor-less form desynced ODBC Driver 18.
/// </summary>
[TestClass]
public sealed class TransactionManagerFBeginXactTests
{
    [TestMethod]
    public void Commit_WithFBeginXact_EmitsCommitThenBegin_WithFreshDescriptor()
    {
        using var fixture = new TmFixture();
        _ = fixture.Run(Begin(isolation: 2));                 // opens tx (descriptor 1)
        var envs = EnvChanges(fixture.Run(CommitOrRollback(Tds.TmCommitTransaction, beginNext: true)));

        HasCount(2, envs);
        AreEqual(Tds.EnvCommitTransaction, envs[0].Type);     // ends the current tx...
        AreEqual(1UL, envs[0].OldDescriptor);                 // ...carrying its descriptor (old-value)
        AreEqual(Tds.EnvBeginTransaction, envs[1].Type);      // ...then opens the follow-on tx
        AreEqual(2UL, envs[1].NewDescriptor);                 // ...with a fresh descriptor (new-value)
    }

    [TestMethod]
    public void Rollback_WithFBeginXact_EmitsRollbackThenBegin()
    {
        using var fixture = new TmFixture();
        _ = fixture.Run(Begin(isolation: 2));
        var envs = EnvChanges(fixture.Run(CommitOrRollback(Tds.TmRollbackTransaction, beginNext: true)));

        HasCount(2, envs);
        AreEqual(Tds.EnvRollbackTransaction, envs[0].Type);
        AreEqual(1UL, envs[0].OldDescriptor);
        AreEqual(Tds.EnvBeginTransaction, envs[1].Type);
        AreEqual(2UL, envs[1].NewDescriptor);
    }

    [TestMethod]
    public void Commit_WithoutFBeginXact_EmitsCommitOnly()
    {
        using var fixture = new TmFixture();
        _ = fixture.Run(Begin(isolation: 2));
        var envs = EnvChanges(fixture.Run(CommitOrRollback(Tds.TmCommitTransaction, beginNext: false)));

        HasCount(1, envs);
        AreEqual(Tds.EnvCommitTransaction, envs[0].Type);
        AreEqual(1UL, envs[0].OldDescriptor);
    }

    [TestMethod]
    public void FBeginXactFollowOn_LeavesALiveTransaction_ForTheNextCommit()
    {
        // The follow-on begin must open a real transaction: a subsequent commit
        // (without fBeginXact) finds it and doesn't raise "no corresponding
        // BEGIN TRANSACTION" (no ERROR token in the response).
        using var fixture = new TmFixture();
        _ = fixture.Run(Begin(isolation: 2));
        _ = fixture.Run(CommitOrRollback(Tds.TmCommitTransaction, beginNext: true));   // commits tx1, opens tx2
        var response = fixture.Run(CommitOrRollback(Tds.TmCommitTransaction, beginNext: false)); // commits tx2

        IsFalse(ContainsErrorToken(response), "follow-on transaction was not live");
        var envs = EnvChanges(response);
        HasCount(1, envs);
        AreEqual(Tds.EnvCommitTransaction, envs[0].Type);
        AreEqual(2UL, envs[0].OldDescriptor);
    }

    [TestMethod]
    public void Begin_OnAnOpenTransaction_Nests()
    {
        // Real nests a TM begin onto an open transaction — the parallel-
        // transaction refusal is SqlClient's client-side rule, not the
        // server's. A manual-commit driver that lost track of a transaction the
        // engine ended sends exactly this, and it must not fault the session.
        using var fixture = new TmFixture();
        _ = fixture.Run(Begin(isolation: 2));
        var response = fixture.Run(Begin(isolation: 2));

        IsFalse(ContainsErrorToken(response), "a nested TM begin faulted");
        AreEqual(2, fixture.TranCount);
    }

    [TestMethod]
    public void CommitOrRollback_AfterTheEngineEndedTheTransaction_IsAccepted()
    {
        // A transaction-aborting error (Msg 8728) rolls the whole stack back
        // underneath the TM layer. The client hasn't heard yet, so its commit /
        // rollback still arrives, and it must find nothing left to do rather
        // than a completed transaction to re-finish.
        foreach (var requestType in new[] { Tds.TmCommitTransaction, Tds.TmRollbackTransaction })
        {
            using var fixture = new TmFixture();
            _ = fixture.Run(Begin(isolation: 2));
            fixture.EndTransactionInTheEngine();
            var response = fixture.Run(CommitOrRollback(requestType, beginNext: false));
            IsFalse(ContainsErrorToken(response), $"TM request {requestType} faulted after an engine-ended transaction");
        }
    }

    // --- request builders -------------------------------------------------

    // ALL_HEADERS length 4 (empty) so SkipAllHeaders lands on the request body.
    private static TdsMessage Tm(params byte[] body)
    {
        var payload = new byte[4 + body.Length];
        payload[0] = 4;
        body.CopyTo(payload, 4);
        return new TdsMessage(Tds.PacketTransactionManager, 0x01, payload);
    }

    private static TdsMessage Begin(byte isolation) =>
        Tm((byte)Tds.TmBeginTransaction, 0x00, isolation);

    // commit/rollback body: requestType (2) + name (B_VARBYTE, empty) + flags (fBeginXact = bit 0).
    private static TdsMessage CommitOrRollback(ushort requestType, bool beginNext) =>
        Tm((byte)requestType, 0x00, 0x00, (byte)(beginNext ? 1 : 0));

    // --- response parsing -------------------------------------------------

    private readonly record struct EnvChange(byte Type, ulong OldDescriptor, ulong NewDescriptor);

    private static List<EnvChange> EnvChanges(byte[] response)
    {
        var result = new List<EnvChange>();
        var i = 0;
        while (i < response.Length && response[i] == Tds.TokenEnvChange)
        {
            var len = response[i + 1] | (response[i + 2] << 8);
            var body = response.AsSpan(i + 3, len);
            var type = body[0];
            // BEGIN carries the descriptor in the new-value field, COMMIT /
            // ROLLBACK in the old-value field; each is a 1-byte length + value.
            var newLen = body[1];
            var newDesc = newLen == 8 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(2, 8)) : 0UL;
            var oldLen = body[2 + newLen];
            var oldDesc = oldLen == 8 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(3 + newLen, 8)) : 0UL;
            result.Add(new EnvChange(type, oldDesc, newDesc));
            i += 3 + len;
        }
        return result;
    }

    private static bool ContainsErrorToken(byte[] response)
    {
        foreach (var b in response)
        {
            if (b == Tds.TokenError)
                return true;
        }
        return false;
    }

    // --- fixture ----------------------------------------------------------

    private sealed class TmFixture : IDisposable
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly X509Certificate2 certificate;
        private readonly TdsSession session;
        private readonly SimulatedDbConnection connection;

        public TmFixture()
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

        public byte[] Run(TdsMessage message)
        {
            var stream = new MemoryStream();
            var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
            var writer = new TdsTokenWriter(transport);
            this.session.RunTransactionManagerRequestForTesting(this.connection, message, writer);
            writer.FlushAsync(final: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return stream.ToArray()[Tds.HeaderSize..];
        }

        public void Dispose()
        {
            this.connection.Dispose();
            this.session.Dispose();
            this.certificate.Dispose();
            this.socket.Dispose();
        }
    }
}
