namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for row-overflow on bounded variable-length columns
/// (<c>varchar(N)</c>, <c>nvarchar(N)</c>, <c>varbinary(N)</c>): when the
/// encoded row would exceed SQL Server's 8060-byte per-row cap, the encoder
/// pushes the largest variable-length column to a chain page (greedy,
/// repeating until the row fits) and the row carries a pointer in its place.
/// </summary>
[TestClass]
public sealed class RowOverflowTests
{
    [TestMethod]
    public void Insert_TwoVarchar8000_BothAtMax_RoundTrips()
    {
        var aValue = new string('A', 8000);
        var bValue = new string('B', 8000);
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (a varchar(8000), b varchar(8000));insert t values (@a, @b)",
            ("@a", aValue), ("@b", bValue)).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(aValue, reader.GetString(0));
        Assert.AreEqual(bValue, reader.GetString(1));
    }

    // Each nvarchar(4000) value is 8000 bytes (UTF-16) — same overflow pressure as
    // the varchar(8000) case, just through a different type.
    [TestMethod]
    public void Insert_NVarchar4000_BothAtMax_RoundTrips()
    {
        var aValue = new string('a', 4000);
        var bValue = new string('b', 4000);
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (a nvarchar(4000), b nvarchar(4000));insert t values (@a, @b)",
            ("@a", aValue), ("@b", bValue)).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(aValue, reader.GetString(0));
        Assert.AreEqual(bValue, reader.GetString(1));
    }

    [TestMethod]
    public void Insert_Varbinary8000_BothAtMax_RoundTrips()
    {
        var aValue = new byte[8000];
        var bValue = new byte[8000];
        new Random(1).NextBytes(aValue);
        new Random(2).NextBytes(bValue);
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (a varbinary(8000), b varbinary(8000));insert t values (@a, @b)",
            ("@a", aValue), ("@b", bValue)).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual(aValue, (byte[])reader.GetValue(0));
        CollectionAssert.AreEqual(bValue, (byte[])reader.GetValue(1));
    }

    // Fixed-length columns can never be pushed off-row; the encoder must overflow
    // only the bounded var column.
    [TestMethod]
    public void Insert_VarcharWithFixedColumn_OverflowDoesNotAffectFixed()
    {
        var payload = new string('x', 8000);
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (id int, payload varchar(8000));insert t values (42, @p)",
            ("@p", payload)).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select id, payload from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
        Assert.AreEqual(payload, reader.GetString(1));
    }

    // No overflow needed; this guards against accidentally pushing values that
    // already fit (the round-trip works either way, so this pairs with an internal
    // test that asserts no LOB pages were used).
    [TestMethod]
    public void Insert_RowJustUnderCap_StaysInlineAndRoundTrips()
    {
        var simulation = new Simulation();
        var aValue = new string('a', 7000);
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (a varchar(8000));insert t values (@a)",
            ("@a", aValue)).ExecuteNonQuery();
        Assert.AreEqual(aValue, simulation.ExecuteScalar("select a from t"));
    }

    // Each insert independently triggers overflow. Confirms the chain pool stays
    // consistent across multiple allocations and that per-row pointers don't get
    // crossed.
    [TestMethod]
    public void Insert_ManyRowsOverflowing_AllRoundTrip()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a varchar(8000), b varchar(8000))").ExecuteNonQuery();
        for (var i = 0; i < 5; i++)
        {
            var aValue = new string((char)('A' + i), 8000);
            var bValue = new string((char)('a' + i), 8000);
            _ = connection.CreateCommand("insert t values (@a, @b)", ("@a", aValue), ("@b", bValue)).ExecuteNonQuery();
        }
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        for (var i = 0; i < 5; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(new string((char)('A' + i), 8000), reader.GetString(0));
            Assert.AreEqual(new string((char)('a' + i), 8000), reader.GetString(1));
        }
        Assert.IsFalse(reader.Read());
    }

    // Predicate evaluation has to chase the off-row pointer back through the LOB
    // chain just like SELECT does.
    [TestMethod]
    public void Insert_OverflowedColumn_FilterableInWhereClause()
    {
        var aValue = new string('A', 8000);
        var bValue = new string('B', 8000);
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (a varchar(8000), b varchar(8000));insert t values (@a, @b)",
            ("@a", aValue), ("@b", bValue)).ExecuteNonQuery();
        Assert.AreEqual(bValue, connection.CreateCommand("select b from t where a = @a", ("@a", aValue)).ExecuteScalar());
    }
}
