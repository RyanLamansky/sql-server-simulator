using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A scan of a table with a clustered index reads its rows in that index's key
/// order, so a query with no ORDER BY — and the rows a TOP without one keeps,
/// and the rows an UPDATE / DELETE walks — follows the key rather than the order
/// the rows were written in; a heap keeps write order. Every expectation
/// probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ClusteredScanOrderTests
{
    private static string Column(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(0)}");
        return string.Join(",", values);
    }

    [TestMethod]
    [DataRow("create table t (a int primary key); insert t values (2), (1), (3)", "select a from t", "1,2,3")]
    [DataRow("create table t (a int primary key nonclustered); insert t values (2), (1), (3)", "select a from t", "2,1,3")]
    [DataRow("create table t (a int); insert t values (2), (1), (3)", "select a from t", "2,1,3")]
    [DataRow("create table t (a int primary key, b int); insert t values (5, 1), (3, 2), (9, 3)", "select top 1 a from t", "3")]
    [DataRow("create table t (a varchar(10) primary key); insert t values ('m'), ('b'), ('z'), ('a'); delete t where a = 'b'; insert t values ('c')", "select a from t", "a,c,m,z")]
    [DataRow("create table t (a int, b int, primary key (b, a)); insert t values (1, 2), (2, 1), (0, 2)", "select a from t", "2,0,1")]
    [DataRow("create table t (a int not null, b int); create clustered index cx on t (b); insert t values (1, 3), (2, null), (3, 1)", "select a from t", "2,3,1")]
    [DataRow("create table t (d int not null, n int, constraint pk primary key (d desc)); insert t values (1, 1), (2, 2), (3, 3)", "select n from t", "3,2,1")]
    [DataRow("create table t (s varchar(10) collate Latin1_General_CS_AS primary key, n int); insert t values ('b', 1), ('B', 2), ('a', 3), ('A', 4)", "select n from t", "3,4,1,2")]
    public void Scan_FollowsTheClusteredKey(string setup, string query, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(setup);
        AreEqual(expected, Column(simulation, query));
    }

    [TestMethod]
    public void TableVariable_ScansByItsKey()
        => AreEqual("1,2", Column(new Simulation(), "declare @t table (a int primary key); insert @t values (2), (1); select a from @t"));

    [TestMethod]
    public void DeleteTop_TakesTheLowestKey()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key); insert t values (3); insert t values (1); delete top (1) from t");
        AreEqual("3", Column(simulation, "select a from t"));
    }

    [TestMethod]
    public void UpdateOutput_FollowsTheKey()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b int); insert t values (2, 1), (1, 2)");
        AreEqual("1,2", Column(simulation, "update t set b = b + 10 output inserted.a"));
    }

    [TestMethod]
    public void KeyOrderHoldsAcrossWrites()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key); insert t values (1), (2), (4)");
        AreEqual("1,2,4", Column(simulation, "select a from t"));
        _ = simulation.ExecuteNonQuery("insert t values (3)");
        AreEqual("1,2,3,4", Column(simulation, "select a from t"));
        _ = simulation.ExecuteNonQuery("update t set a = 0 where a = 4");
        AreEqual("0,1,2,3", Column(simulation, "select a from t"));
        _ = simulation.ExecuteNonQuery("delete t where a = 0; insert t values (9)");
        AreEqual("1,2,3,9", Column(simulation, "select a from t"));
    }
}
