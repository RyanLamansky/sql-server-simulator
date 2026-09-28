using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>BeginTransaction</c> against a transaction SQL text opened, and the
/// states SqlClient's transaction object passes through, as SqlClient 7
/// behaves against SQL Server 2025 (probed 2026-09-28).
/// </summary>
[TestClass]
public sealed class NestedApiTransactionTests
{
    private static SimulatedDbConnection Open(string setup)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand(setup).ExecuteNonQuery();
        return connection;
    }

    private static object? Scalar(SimulatedDbConnection connection, SimulatedDbTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    [DataRow("create table t (id int); begin tran; insert t values (1)")]
    [DataRow("create table t (id int); set implicit_transactions on; insert t values (1)")]
    public void BeginTransaction_NestsInASqlTextTransaction_AndItsCommitEndsOneLevel(string setup)
    {
        using var connection = Open(setup);
        var transaction = connection.BeginTransaction();
        AreEqual(2, Scalar(connection, transaction, "select @@trancount"));
        transaction.Commit();
        IsNull(transaction.Connection);
        AreEqual(1, Scalar(connection, null, "select @@trancount"));
        _ = connection.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(1, Scalar(connection, null, "select count(*) from t"));
    }

    [TestMethod]
    public void NestedRollback_RollsBackTheWholeTransaction()
    {
        using var connection = Open("create table t (id int); begin tran; begin tran; insert t values (1)");
        var transaction = connection.BeginTransaction();
        AreEqual(3, Scalar(connection, transaction, "select @@trancount"));
        transaction.Rollback();
        AreEqual(0, Scalar(connection, null, "select @@trancount + (select count(*) from t)"));
        AreEqual(3902, Throws<SimulatedSqlException>(() => connection.CreateCommand("commit").ExecuteNonQuery()).Number);
    }

    [TestMethod]
    public void NestedDispose_RollsBackTheWholeTransaction()
    {
        using var connection = Open("begin tran");
        connection.BeginTransaction().Dispose();
        AreEqual(0, Scalar(connection, null, "select @@trancount"));
    }

    [TestMethod]
    [DataRow("rollback")]
    [DataRow("commit; commit")]
    public void TextEndingTheTransaction_LeavesTheNestedObjectCompleted(string ending)
    {
        using var connection = Open("begin tran");
        var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = ending;
            _ = command.ExecuteNonQuery();
        }
        IsNull(transaction.Connection);
        _ = Throws<InvalidOperationException>(transaction.Commit);
        _ = Throws<InvalidOperationException>(transaction.Rollback);
        AreEqual(0, Scalar(connection, null, "select @@trancount"));
    }

    [TestMethod]
    public void TextCommitOfTheNestedLevel_LeavesTheObjectToCommitTheRest()
    {
        using var connection = Open("begin tran");
        var transaction = connection.BeginTransaction();
        _ = Scalar(connection, transaction, "commit; select 1");
        AreEqual(1, Scalar(connection, transaction, "select @@trancount"));
        _ = Throws<InvalidOperationException>(connection.BeginTransaction);
        transaction.Commit();
        AreEqual(0, Scalar(connection, null, "select @@trancount"));
    }

    [TestMethod]
    [Description("While the API's transaction is pending, SqlClient refuses a command without it and a second BeginTransaction.")]
    public void PendingApiTransaction_RefusesAParallelOne_AndACommandWithoutIt()
    {
        using var connection = Open("begin tran");
        var transaction = connection.BeginTransaction();
        AreEqual(
            "SqlConnection does not support parallel transactions.",
            Throws<InvalidOperationException>(connection.BeginTransaction).Message);
        AreEqual(
            "ExecuteScalar requires the command to have a transaction when the connection assigned to the command is in a pending local transaction.  The Transaction property of the command has not been initialized.",
            Throws<InvalidOperationException>(() => Scalar(connection, null, "select 1")).Message);
        transaction.Rollback();
        AreEqual(0, Scalar(connection, null, "select @@trancount"));
    }

    [TestMethod]
    [Description("A commit through the API that ends only the level SQL text nested inside it leaves the transaction pending.")]
    public void ApiCommitUnderATextNestedLevel_StaysPending()
    {
        using var connection = Open("select 1");
        var transaction = connection.BeginTransaction();
        _ = Scalar(connection, transaction, "begin tran; select @@trancount");
        transaction.Commit();
        _ = Throws<InvalidOperationException>(connection.BeginTransaction);
        _ = Throws<InvalidOperationException>(() => Scalar(connection, null, "select 1"));
        AreEqual(1, Scalar(connection, transaction, "select @@trancount"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RequestedIsolation_AppliesToTheSession_AndOutlivesTheTransaction(bool nested)
    {
        using var connection = Open(nested ? "begin tran" : "select 1");
        var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        const string Level = "select transaction_isolation_level from sys.dm_exec_sessions where session_id = @@spid";
        AreEqual((short)4, Scalar(connection, transaction, Level));
        transaction.Rollback();
        AreEqual((short)4, Scalar(connection, null, Level));
    }

    [TestMethod]
    public void UnspecifiedIsolation_BeginsAtReadCommitted()
    {
        using var connection = Open("set transaction isolation level serializable");
        var transaction = connection.BeginTransaction();
        AreEqual(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
        AreEqual((short)2, Scalar(connection, transaction, "select transaction_isolation_level from sys.dm_exec_sessions where session_id = @@spid"));
        transaction.Commit();
    }
}
