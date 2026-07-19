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
}
