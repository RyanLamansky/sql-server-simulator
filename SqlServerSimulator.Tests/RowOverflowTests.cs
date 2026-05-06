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
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a varchar(8000), b varchar(8000))");
        var aValue = new string('A', 8000);
        var bValue = new string('B', 8000);
        using var connection = simulation.CreateOpenConnection();
        using var insert = connection.CreateCommand("insert into t values (@a, @b)", ("@a", aValue), ("@b", bValue));
        _ = insert.ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(aValue, reader.GetString(0));
        Assert.AreEqual(bValue, reader.GetString(1));
    }

    [TestMethod]
    public void Insert_NVarchar4000_BothAtMax_RoundTrips()
    {
        // Each nvarchar(4000) value is 8000 bytes (UTF-16) — same overflow
        // pressure as the varchar(8000) case, just through a different type.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a nvarchar(4000), b nvarchar(4000))");
        var aValue = new string('a', 4000);
        var bValue = new string('b', 4000);
        using var connection = simulation.CreateOpenConnection();
        using var insert = connection.CreateCommand("insert into t values (@a, @b)", ("@a", aValue), ("@b", bValue));
        _ = insert.ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(aValue, reader.GetString(0));
        Assert.AreEqual(bValue, reader.GetString(1));
    }

    [TestMethod]
    public void Insert_Varbinary8000_BothAtMax_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a varbinary(8000), b varbinary(8000))");
        var aValue = new byte[8000];
        var bValue = new byte[8000];
        new Random(1).NextBytes(aValue);
        new Random(2).NextBytes(bValue);
        using var connection = simulation.CreateOpenConnection();
        using var insert = connection.CreateCommand("insert into t values (@a, @b)", ("@a", aValue), ("@b", bValue));
        _ = insert.ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual(aValue, (byte[])reader.GetValue(0));
        CollectionAssert.AreEqual(bValue, (byte[])reader.GetValue(1));
    }

    [TestMethod]
    public void Insert_VarcharWithFixedColumn_OverflowDoesNotAffectFixed()
    {
        // Fixed-length columns can never be pushed off-row; the encoder must
        // overflow only the bounded var column.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, payload varchar(8000))");
        var payload = new string('x', 8000);
        using var connection = simulation.CreateOpenConnection();
        using var insert = connection.CreateCommand("insert into t values (42, @p)", ("@p", payload));
        _ = insert.ExecuteNonQuery();
        using var reader = connection.CreateCommand("select id, payload from t").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
        Assert.AreEqual(payload, reader.GetString(1));
    }

    [TestMethod]
    public void Insert_RowJustUnderCap_StaysInlineAndRoundTrips()
    {
        // No overflow needed; this guards against accidentally pushing
        // values that already fit (the round-trip works either way, so this
        // pairs with an internal test that asserts no LOB pages were used).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a varchar(8000))");
        var aValue = new string('a', 7000);
        using var connection = simulation.CreateOpenConnection();
        using var insert = connection.CreateCommand("insert into t values (@a)", ("@a", aValue));
        _ = insert.ExecuteNonQuery();
        Assert.AreEqual(aValue, simulation.ExecuteScalar("select a from t"));
    }

    [TestMethod]
    public void Insert_ManyRowsOverflowing_AllRoundTrip()
    {
        // Each insert independently triggers overflow. Confirms the chain
        // pool stays consistent across multiple allocations and that
        // per-row pointers don't get crossed.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a varchar(8000), b varchar(8000))");
        using var connection = simulation.CreateOpenConnection();
        for (var i = 0; i < 5; i++)
        {
            var aValue = new string((char)('A' + i), 8000);
            var bValue = new string((char)('a' + i), 8000);
            using var insert = connection.CreateCommand("insert into t values (@a, @b)", ("@a", aValue), ("@b", bValue));
            _ = insert.ExecuteNonQuery();
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

    [TestMethod]
    public void Insert_OverflowedColumn_FilterableInWhereClause()
    {
        // Predicate evaluation has to chase the off-row pointer back through
        // the LOB chain just like SELECT does.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a varchar(8000), b varchar(8000))");
        var aValue = new string('A', 8000);
        var bValue = new string('B', 8000);
        using var connection = simulation.CreateOpenConnection();
        using var insert = connection.CreateCommand("insert into t values (@a, @b)", ("@a", aValue), ("@b", bValue));
        _ = insert.ExecuteNonQuery();
        using var select = connection.CreateCommand("select b from t where a = @a", ("@a", aValue));
        Assert.AreEqual(bValue, select.ExecuteScalar());
    }
}
