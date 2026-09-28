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
        using var fixture = new TdsSessionFixture();
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
        using var fixture = new TdsSessionFixture();
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
        using var fixture = new TdsSessionFixture();
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
        using var fixture = new TdsSessionFixture();
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
        using var fixture = new TdsSessionFixture();
        _ = fixture.Run(Begin(isolation: 2));
        var response = fixture.Run(Begin(isolation: 2));

        IsFalse(ContainsErrorToken(response), "a nested TM begin faulted");
        AreEqual(2, fixture.TranCount);
    }

    [TestMethod]
    public void CommitOrRollback_AfterTheEngineEndedTheTransaction_IsRefused()
    {
        // A transaction-aborting error rolls the whole stack back underneath
        // the TM layer and announces it with a rollback ENVCHANGE; a commit /
        // rollback request arriving afterwards finds nothing open, which real
        // refuses as Msg 3902 state 3 / Msg 3903 state 2 and opens nothing
        // even under fBeginXact (probed 2026-09-28 against SQL Server 2025).
        foreach (var (requestType, number, state) in new[] { (Tds.TmCommitTransaction, 3902, 3), (Tds.TmRollbackTransaction, 3903, 2) })
        {
            using var fixture = new TdsSessionFixture();
            _ = fixture.Run(Begin(isolation: 2));
            fixture.EndTransactionInTheEngine();
            var response = fixture.Run(CommitOrRollback(requestType, beginNext: true));
            AreEqual((number, state), FirstError(response));
            AreEqual(0, fixture.TranCount);
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

    // The first token must be an ERROR: number (int32) and state follow its length.
    private static (int Number, int State) FirstError(byte[] response)
    {
        AreEqual(Tds.TokenError, response[0]);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(3, 4)), response[7]);
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
}
