using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for explicit transactions opened via <c>DbConnection.BeginTransaction()</c>
/// — the SqlClient API EF Core 10's <c>Database.BeginTransaction()</c> uses.
/// Confirms a connection-scoped <see cref="Storage.UndoLog"/> spans multiple
/// statements, that <c>Commit()</c> persists everything and <c>Rollback()</c>
/// undoes everything, and that a failed statement within a transaction only
/// undoes its own writes (the surrounding transaction stays alive — probe-
/// confirmed against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ExplicitTransactionTests
{
    private static int CountRows(DbConnection conn, string table) =>
        (int)conn.CreateCommand($"select count(*) from {table}").ExecuteScalar()!;

    private static DbConnection NewSeededConnection()
    {
        var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int primary key, val int)").ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> as a non-query bound to <paramref name="tx"/>.
    /// Keeps the test bodies focused on what's being executed rather than on
    /// the four-line ceremony of building a tx-bound DbCommand.
    /// </summary>
    private static void RunInTx(DbConnection conn, DbTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void Commit_PersistsAllWritesAcrossMultipleStatements()
    {
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        RunInTx(conn, tx, "insert into t values (1, 10)");
        RunInTx(conn, tx, "insert into t values (2, 20)");
        tx.Commit();
        AreEqual(2, CountRows(conn, "t"));
    }

    [TestMethod]
    public void Rollback_UndoesAllWritesAcrossMultipleStatements()
    {
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        RunInTx(conn, tx, "insert into t values (1, 10)");
        RunInTx(conn, tx, "insert into t values (2, 20)");
        tx.Rollback();
        AreEqual(0, CountRows(conn, "t"));
    }

    [TestMethod]
    public void OwnTransactionSeesOwnWrites_BeforeCommit()
    {
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        RunInTx(conn, tx, "insert into t values (42, 100)");

        using var sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "select count(*) from t";
        AreEqual(1, sel.ExecuteScalar());
    }

    [TestMethod]
    public void StatementFailureWithinTx_LeavesTxAlive_OnlyOwnWritesUndone()
    {
        // Probe-confirmed: a statement that fails inside an explicit transaction
        // rolls back ONLY its own writes; subsequent statements still run; commit
        // persists the surviving (pre-failure + post-failure-success) writes.
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        RunInTx(conn, tx, "insert into t values (1, 10)");

        // Mid-tx failure: PK collision against the just-inserted row.
        _ = Throws<DbException>(() => RunInTx(conn, tx, "insert into t values (1, 99)"));

        // Tx alive — third statement still works.
        RunInTx(conn, tx, "insert into t values (3, 30)");
        tx.Commit();

        var rows = new List<(int, int)>();
        using var reader = conn.CreateCommand("select id, val from t order by id").ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (3, 30) }, rows);
    }

    [TestMethod]
    public void Dispose_WithoutCommit_AutoRollsBack()
    {
        // SqlClient's SqlTransaction auto-rolls-back on dispose if neither
        // Commit nor Rollback ran. Mirrors `using var tx = ...; ... tx.Commit();`
        // where an early exception path leaves dispose to do the rollback.
        using var conn = NewSeededConnection();
        {
            using var tx = conn.BeginTransaction();
            RunInTx(conn, tx, "insert into t values (1, 10)");
            // Drop out of scope without committing.
        }
        AreEqual(0, CountRows(conn, "t"));
    }

    [TestMethod]
    public void ParallelBeginTransaction_RaisesInvalidOperation()
    {
        // SqlClient: "SqlConnection does not support parallel transactions."
        using var conn = NewSeededConnection();
        using var tx1 = conn.BeginTransaction();
        _ = Throws<InvalidOperationException>(() => _ = conn.BeginTransaction());
    }

    [TestMethod]
    public void Commit_AfterCommit_RaisesInvalidOperation()
    {
        using var conn = NewSeededConnection();
        var tx = conn.BeginTransaction();
        tx.Commit();
        _ = Throws<InvalidOperationException>(tx.Commit);
    }

    [TestMethod]
    public void Rollback_AfterCommit_RaisesInvalidOperation()
    {
        using var conn = NewSeededConnection();
        var tx = conn.BeginTransaction();
        tx.Commit();
        _ = Throws<InvalidOperationException>(tx.Rollback);
    }

    [TestMethod]
    public void NewTransaction_AfterCommit_Allowed()
    {
        // Once committed, the connection is free to begin a fresh transaction.
        using var conn = NewSeededConnection();
        var tx1 = conn.BeginTransaction();
        RunInTx(conn, tx1, "insert into t values (1, 10)");
        tx1.Commit();

        var tx2 = conn.BeginTransaction();
        RunInTx(conn, tx2, "insert into t values (2, 20)");
        tx2.Rollback();

        AreEqual(1, CountRows(conn, "t"));
    }

    [TestMethod]
    public void Savepoint_RollbackToName_UndoesOnlyPostSavepointWrites()
    {
        // SAVE TRANSACTION + ROLLBACK TRANSACTION savepoint_name partial
        // rollback. EF Core 10 emits this around each SaveChanges within
        // a Database.BeginTransaction so a failed save undoes only its
        // own writes while the surrounding transaction stays alive.
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        RunInTx(conn, tx, "insert into t values (1, 10)");
        RunInTx(conn, tx, "save transaction sp1");
        RunInTx(conn, tx, "insert into t values (2, 20)");
        RunInTx(conn, tx, "insert into t values (3, 30)");
        RunInTx(conn, tx, "rollback transaction sp1");
        tx.Commit();

        var rows = new List<(int, int)>();
        using var reader = conn.CreateCommand("select id, val from t order by id").ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10) }, rows);
    }

    [TestMethod]
    public void Savepoint_AcceptsTranAbbreviation()
    {
        // SAVE TRAN / ROLLBACK TRAN are interchangeable with TRANSACTION.
        using var conn = NewSeededConnection();
        using var tx = conn.BeginTransaction();
        RunInTx(conn, tx, "insert into t values (1, 10)");
        RunInTx(conn, tx, "save tran sp1");
        RunInTx(conn, tx, "insert into t values (2, 20)");
        RunInTx(conn, tx, "rollback tran sp1");
        tx.Commit();

        AreEqual(1, CountRows(conn, "t"));
    }

    [TestMethod]
    public void Rollback_RestoresExistingRows_NotJustNewOnes()
    {
        // A transaction that updates and deletes existing rows must restore
        // them on rollback. UPDATE = delete-old + insert-new, so each affected
        // row produces two log entries — undo unwinds both halves.
        using var conn = NewSeededConnection();
        _ = conn.CreateCommand("insert into t values (1, 10), (2, 20), (3, 30)").ExecuteNonQuery();

        using (var tx = conn.BeginTransaction())
        {
            RunInTx(conn, tx, "update t set val = val * 10 where id <= 2");
            RunInTx(conn, tx, "delete from t where id = 3");
            tx.Rollback();
        }

        var rows = new List<(int, int)>();
        using var reader = conn.CreateCommand("select id, val from t order by id").ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (2, 20), (3, 30) }, rows);
    }
}
