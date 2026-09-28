using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The transaction ENVCHANGE tokens a response carries: one whenever the
/// session's transaction begins or ends — by SQL text, an implicit
/// transaction, a transaction-manager request or the engine — none for a
/// nested level, and an engine rollback after the error that caused it
/// (probed 2026-09-28 against SQL Server 2025 with a raw TDS client).
/// </summary>
[TestClass]
public sealed class TransactionEnvChangeTests
{
    private static List<string> Batch(TdsSessionFixture fixture, string sql) =>
        TdsSessionFixture.Tokens(fixture.RunBatch(sql)).FindAll(token => token != "DONE");

    [TestMethod]
    public void SqlText_BeginAndCommit_AreAnnounced_NestedLevelsAreNot()
    {
        using var fixture = new TdsSessionFixture();
        CollectionAssert.AreEqual(new[] { "ENV8 new=1" }, Batch(fixture, "begin tran"));
        IsEmpty(Batch(fixture, "begin tran; commit"));
        CollectionAssert.AreEqual(new[] { "ENV9 old=1" }, Batch(fixture, "commit"));
        CollectionAssert.AreEqual(new[] { "ENV8 new=2", "ENV10 old=2" }, Batch(fixture, "begin tran; rollback"));
    }

    [TestMethod]
    public void XactAbortRollback_FollowsTheErrorThatCausedIt()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int)");
        CollectionAssert.AreEqual(
            new[] { "ENV8 new=1", "ERR 8134", "ENV10 old=1" },
            Batch(fixture, "set xact_abort on; begin tran; insert t values (1); declare @x int = 1/0; insert t values (2)"));
        AreEqual(0, fixture.TranCount);
    }

    [TestMethod]
    public void TriggerRollback_PrecedesTheErrorItsReturnRaises()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int)");
        _ = fixture.RunBatch("create trigger tr on t after insert as rollback");
        CollectionAssert.AreEqual(
            new[] { "ENV8 new=1", "ENV10 old=1", "ERR 3609" },
            Batch(fixture, "begin tran; insert t values (1)"));
    }

    [TestMethod]
    public void ImplicitTransaction_IsAnnouncedWhenAStatementOpensIt()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int)");
        IsEmpty(Batch(fixture, "set implicit_transactions on"));
        CollectionAssert.AreEqual(new[] { "ENV8 new=1" }, Batch(fixture, "insert t values (1)"));
        CollectionAssert.AreEqual(new[] { "ENV9 old=1" }, Batch(fixture, "commit"));
    }

    [TestMethod]
    public void TransactionManagerBegin_InsideSqlText_NestsSilently()
    {
        using var fixture = new TdsSessionFixture();
        CollectionAssert.AreEqual(new[] { "ENV8 new=1" }, Batch(fixture, "begin tran"));
        CollectionAssert.AreEqual(new[] { "DONE" }, TdsSessionFixture.Tokens(fixture.Run(TransactionManagerRequest(5, 0x00, 0x00))));
        AreEqual(2, fixture.TranCount);
        CollectionAssert.AreEqual(new[] { "DONE" }, TdsSessionFixture.Tokens(fixture.Run(TransactionManagerRequest(7, 0x00, 0x00))));
        CollectionAssert.AreEqual(new[] { "ENV9 old=1" }, Batch(fixture, "commit"));
    }

    // ALL_HEADERS length 4 (empty), the request type, then its body.
    private static Network.TdsMessage TransactionManagerRequest(byte requestType, params byte[] body)
    {
        var payload = new byte[4 + 2 + body.Length];
        payload[0] = 4;
        payload[4] = requestType;
        body.CopyTo(payload, 6);
        return new Network.TdsMessage(Network.Tds.PacketTransactionManager, 0x01, payload);
    }
}
