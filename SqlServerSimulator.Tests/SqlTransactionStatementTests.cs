using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for SQL-text transaction control: <c>BEGIN TRANSACTION</c>,
/// <c>COMMIT</c>, <c>ROLLBACK</c> (no savepoint), and <c>@@TRANCOUNT</c>.
/// EF Core never emits these — it uses SqlClient's API path — so this is
/// hand-written-SQL fidelity. Probe-confirmed against SQL Server 2025
/// (2026-05-08): nesting via TRANCOUNT (BEGIN increments; COMMIT decrements;
/// ROLLBACK without a savepoint name zeroes regardless of depth);
/// COMMIT / ROLLBACK without an active tx raise Msg 3902 / 3903 verbatim.
/// </summary>
[TestClass]
public sealed class SqlTransactionStatementTests
{
    private static int CountRows(DbConnection conn, string table) =>
        (int)conn.CreateCommand($"select count(*) from {table}").ExecuteScalar()!;

    private static DbConnection NewSeededConnection()
    {
        var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int primary key, val int)").ExecuteNonQuery();
        return conn;
    }

    [TestMethod]
    public void BeginCommit_PersistsWrites()
    {
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("""
            begin transaction;
            insert t values (1, 10);
            commit
            """).ExecuteNonQuery();
        AreEqual(1, CountRows(conn, "t"));
    }

    [TestMethod]
    public void BeginRollback_UndoesWrites()
    {
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("""
            begin transaction;
            insert t values (1, 10);
            rollback
            """).ExecuteNonQuery();
        AreEqual(0, CountRows(conn, "t"));
    }

    [TestMethod]
    public void TranCount_TracksNestingDepth()
    {
        using var conn = NewSeededConnection();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());

        _ = conn.CreateCommand("begin transaction").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());

        _ = conn.CreateCommand("begin tran").ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select @@trancount").ExecuteScalar());

        _ = conn.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());

        _ = conn.CreateCommand("commit transaction").ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    // Probe-confirmed: only the outermost COMMIT actually commits. Inner COMMITs
    // just decrement TRANCOUNT — the writes survive an outer ROLLBACK only if the
    // OUTER level commits too. Here we inner-COMMIT then outer-ROLLBACK; everything
    // must rollback.
    [TestMethod]
    public void NestedBeginCommit_InnerCommitDoesNotPersist()
    {
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("""
            begin transaction;
            insert t values (1, 10);
            begin transaction;
            insert t values (2, 20);
            commit
            """).ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());
        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, CountRows(conn, "t"));
    }

    [TestMethod]
    public void RollbackWithoutSavepoint_WipesAllNestingLevels()
    {
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("""
            begin transaction;
            insert t values (1, 10);
            begin transaction;
            insert t values (2, 20);
            rollback
            """).ExecuteNonQuery();

        AreEqual(0, CountRows(conn, "t"));
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    // BEGIN TRANSACTION my_tx — name is cosmetic; matches SQL Server.
    [TestMethod]
    public void BeginTransaction_AcceptsName()
    {
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("begin transaction my_tx").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());
        _ = conn.CreateCommand("commit transaction my_tx").ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void CommitWork_RollbackWork_AcceptedAsAnsiVariants()
    {
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("""
            begin transaction;
            commit work
            """).ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());

        _ = conn.CreateCommand("""
            begin transaction;
            rollback work
            """).ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Commit_WithoutActiveTx_RaisesMsg3902()
    {
        using var conn = NewSeededConnection();
        var ex = Throws<DbException>(() => _ = conn.CreateCommand("commit").ExecuteNonQuery());
        AreEqual("3902", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Rollback_WithoutActiveTx_RaisesMsg3903()
    {
        using var conn = NewSeededConnection();
        var ex = Throws<DbException>(() => _ = conn.CreateCommand("rollback").ExecuteNonQuery());
        AreEqual("3903", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SqlClientApi_AndSqlText_ShareTheSameTransaction()
    {
        // SqlClient API BeginTransaction → TRANCOUNT 1.
        // SQL BEGIN inside → TRANCOUNT 2.
        // SQL COMMIT → TRANCOUNT 1.
        // API tx.Commit() → TRANCOUNT 0, persists writes.
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "begin transaction";
        _ = cmd.ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select @@trancount").ExecuteScalar());

        cmd.CommandText = "insert t values (1, 10)";
        _ = cmd.ExecuteNonQuery();

        cmd.CommandText = "commit";
        _ = cmd.ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());

        tx.Commit();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(1, CountRows(conn, "t"));
    }

    [TestMethod]
    public void BeginTran_SkippedBranch_DoesNotOpenTransaction()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("if 1=0 begin tran").ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void SaveTran_SkippedBranch_DoesNotRecordSavepoint()
    {
        // No active tx; the un-taken branch must short-circuit before the
        // active-tx lookup that would otherwise throw.
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("if 1=0 save tran my_sp").ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void RollbackTranNamed_SkippedBranch_DoesNotThrow()
    {
        // Without skip mode, ROLLBACK TRAN <name> with no active tx raises
        // Msg 3903; the un-taken branch short-circuits before that.
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("if 1=0 rollback tran my_sp").ExecuteNonQuery();
        AreEqual(0, conn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void RollbackTran_UnknownSavepoint_Throws()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("begin tran").ExecuteNonQuery();
        _ = Throws<DbException>(() =>
            conn.CreateCommand("rollback tran nosuch").ExecuteNonQuery());
    }
}
