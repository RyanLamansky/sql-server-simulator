using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>XACT_STATE()</c> and <c>ROWCOUNT_BIG()</c>: the
/// transaction-state tristate (smallint, 0/1/-1) and the wide-int
/// sibling of <c>@@ROWCOUNT</c> (bigint). The simulator doesn't model
/// the doomed-transaction state, so <c>XACT_STATE</c> only produces
/// 0 / 1.
/// </summary>
[TestClass]
public sealed class TransactionScalarTests
{
    [TestMethod]
    public void XactState_NoTransaction_ReturnsZero()
        => AreEqual((short)0, new Simulation().ExecuteScalar("select xact_state()"));

    [TestMethod]
    public void XactState_InsideTransaction_ReturnsOne()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        using (var c1 = conn.CreateCommand())
        {
            c1.CommandText = "begin tran";
            _ = c1.ExecuteNonQuery();
        }
        using var c2 = conn.CreateCommand();
        c2.CommandText = "select xact_state()";
        AreEqual((short)1, c2.ExecuteScalar());
    }

    [TestMethod]
    public void RowCountBig_AfterInsert_ReturnsRowsAffected()
        => AreEqual(3L, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1), (2), (3);
            select rowcount_big()
            """));

    [TestMethod]
    public void RowCountBig_Type_IsBigint()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select rowcount_big()";
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("bigint", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void XactState_Type_IsSmallint()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select xact_state()";
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("smallint", reader.GetDataTypeName(0));
    }

    // CURRENT_TRANSACTION_ID() is stable across a user transaction and fresh
    // for each autocommit statement, and the transaction DMVs list the same id
    // (probed 2026-09-25 against SQL Server 2025).
    [TestMethod]
    public void CurrentTransactionId_MatchesTheTransactionDmvs()
    {
        using var connection = new Simulation().CreateOpenConnection();
        long Id(string sql) => (long)connection.CreateCommand(sql).ExecuteScalar()!;
        var first = Id("select current_transaction_id()");
        var second = Id("select current_transaction_id()");
        IsGreaterThan(first, second);

        _ = connection.CreateCommand("begin tran named").ExecuteNonQuery();
        var inside = Id("select current_transaction_id()");
        AreEqual(inside, Id("select current_transaction_id()"));
        AreEqual(inside, Id("select transaction_id from sys.dm_tran_current_transaction"));
        AreEqual(
            $"named|1|2|258|-1",
            connection.CreateCommand($"select concat(name, '|', transaction_type, '|', transaction_state, '|', transaction_status2, '|', dtc_isolation_level) from sys.dm_tran_active_transactions where transaction_id = {inside}").ExecuteScalar());
        _ = connection.CreateCommand("begin tran").ExecuteNonQuery();
        AreEqual(
            $"{inside}|0x0100000033000000|1",
            connection.CreateCommand("select concat(transaction_id, '|', convert(varchar(20), transaction_descriptor, 1), '|', open_transaction_count) from sys.dm_tran_session_transactions where session_id = @@spid").ExecuteScalar());
        _ = connection.CreateCommand("commit; commit").ExecuteNonQuery();

        AreEqual(0, connection.CreateCommand("select count(*) from sys.dm_tran_session_transactions").ExecuteScalar());
        AreEqual(
            "SELECT|2|0|0|same",
            connection.CreateCommand("select concat(name, '|', transaction_type, '|', transaction_status2, '|', dtc_isolation_level, '|', iif(transaction_id = current_transaction_id(), 'same', 'other')) from sys.dm_tran_active_transactions").ExecuteScalar());
    }
}
