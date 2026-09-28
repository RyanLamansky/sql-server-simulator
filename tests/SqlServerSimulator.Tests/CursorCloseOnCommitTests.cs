using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET CURSOR_CLOSE_ON_COMMIT ON</c> closes, as a transaction ends by
/// <c>COMMIT</c> or <c>ROLLBACK</c>, the cursors opened inside it. Every
/// expectation probed 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CursorCloseOnCommitTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int primary key); insert t values (1), (2), (3)");
        return sim;
    }

    [TestMethod]
    [DataRow("commit")]
    [DataRow("rollback")]
    public void On_EndingTheTransaction_ClosesTheCursor(string ending)
        => AreEqual((short)-1, Seeded().ExecuteScalar($"""
            set cursor_close_on_commit on
            declare c cursor for select a from t
            begin tran
            open c
            {ending}
            select cursor_status('global', 'c')
            """));

    [TestMethod]
    public void On_StaticCursorAndCursorVariable_CloseToo()
        => AreEqual("-1 -1", Seeded().ExecuteScalar("""
            set cursor_close_on_commit on
            declare c cursor static for select a from t
            declare @v cursor
            set @v = cursor for select a from t
            begin tran
            open c
            open @v
            commit
            select concat(cursor_status('global', 'c'), ' ', cursor_status('variable', '@v'))
            """));

    [TestMethod]
    public void On_FetchAfterCommit_RaisesMsg16917()
        => _ = Seeded().AssertSqlError("""
            set cursor_close_on_commit on
            declare c cursor for select a from t
            begin tran
            open c
            commit
            fetch next from c
            """, 16917);

    [TestMethod]
    public void Off_CommitAndRollback_LeaveTheCursorOpen()
        => AreEqual("1 1", Seeded().ExecuteScalar("""
            declare c cursor for select a from t
            declare d cursor keyset for select a from t
            begin tran
            open c
            commit
            begin tran
            open d
            rollback
            select concat(cursor_status('global', 'c'), ' ', cursor_status('global', 'd'))
            """));

    [TestMethod]
    [DataRow("declare c cursor for select a from t; open c; begin tran; insert t values (9); commit")]
    [DataRow("declare c cursor for select a from t; open c; insert t values (9)")]
    [DataRow("declare c cursor for select a from t; begin tran; begin tran; open c; commit")]
    [DataRow("declare c cursor for select a from t; begin tran; save tran s; open c; rollback tran s")]
    public void On_CursorOutliving_ItsTransactionsEnd_StaysOpen(string batch)
        => AreEqual((short)1, Seeded().ExecuteScalar($"set cursor_close_on_commit on; {batch}; select cursor_status('global', 'c')"));

    [TestMethod]
    public void UnderImplicitTransactions_CommitClosesIt()
        => AreEqual((short)-1, Seeded().ExecuteScalar("""
            set implicit_transactions on
            set cursor_close_on_commit on
            declare c cursor for select a from t
            open c
            commit
            select cursor_status('global', 'c')
            """));

    [TestMethod]
    public void AtAtOptions_ReportsIt_AndAProcedureSetReverts()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p as begin set cursor_close_on_commit on; select @@options & 4; end");
        using var connection = sim.CreateOpenConnection();
        AreEqual(4, connection.CreateCommand("exec p").ExecuteScalar());
        AreEqual(0, connection.CreateCommand("select @@options & 4").ExecuteScalar());
    }
}
