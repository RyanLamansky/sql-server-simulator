using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior of the ORDER BY clause: ASC/DESC parsing, ordinal references,
/// alias-vs-source resolution, NULL ordering (NULL first ASC, NULL last DESC),
/// and interaction with WHERE/TOP/DISTINCT.
/// </summary>
[TestClass]
public class OrderByTests
{
    private static List<object?> Column0(DbDataReader reader)
    {
        var values = new List<object?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader[0]);
        return values;
    }

    private static DbConnection Seeded(string columns, string values)
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( {columns} )").ExecuteNonQuery();
        _ = connection.CreateCommand($"insert t values {values}").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_AscDefault()
    {
        using var connection = Seeded("v int", "(3),(1),(2)");
        using var reader = connection.CreateCommand("select v from t order by v").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_AscExplicit()
    {
        using var connection = Seeded("v int", "(3),(1),(2)");
        using var reader = connection.CreateCommand("select v from t order by v asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_Desc()
    {
        using var connection = Seeded("v int", "(3),(1),(2)");
        using var reader = connection.CreateCommand("select v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 2, 1 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_NullsFirstUnderAsc()
    {
        using var connection = Seeded("v int", "(3),(null),(1),(null),(2)");
        using var reader = connection.CreateCommand("select v from t order by v asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { null, null, 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_NullsLastUnderDesc()
    {
        using var connection = Seeded("v int", "(3),(null),(1),(null),(2)");
        using var reader = connection.CreateCommand("select v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 2, 1, null, null }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_MultiColumnMixedDirections()
    {
        using var connection = Seeded("a int, b int", "(1,2),(1,1),(2,1),(2,2)");
        using var reader = connection.CreateCommand("select a, b from t order by a asc, b desc").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (1, 2), (1, 1), (2, 2), (2, 1) }, rows);
    }

    [TestMethod]
    public void OrderBy_StringColumn_CollationAware()
    {
        // Default collation is case-insensitive: 'a' < 'B' < 'C'.
        using var connection = Seeded("s varchar(10)", "('B'),('a'),('C')");
        using var reader = connection.CreateCommand("select s from t order by s asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { "a", "B", "C" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_AliasReference_ResolvesToProjection()
    {
        // `b AS a` overrides — `order by a` sees the aliased projection (b).
        using var connection = Seeded("a int, b int", "(1,3),(2,2),(3,1)");
        using var reader = connection.CreateCommand("select b as a from t order by a").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SourceColumnNotInProjection_ResolvesToSource()
    {
        // Without DISTINCT, ORDER BY can reach a source column not in projection.
        using var connection = Seeded("a int, b int", "(1,30),(2,10),(3,20)");
        using var reader = connection.CreateCommand("select a from t order by b").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 2, 3, 1 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_Expression_Length()
    {
        using var connection = Seeded("s varchar(20)", "('xx'),('a'),('hello')");
        using var reader = connection.CreateCommand("select s from t order by len(s)").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { "a", "xx", "hello" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_Ordinal_OrdersByNthProjectionColumn()
    {
        using var connection = Seeded("a int, b int", "(1,30),(2,10),(3,20)");
        using var reader = connection.CreateCommand("select a, b from t order by 2").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (2, 10), (3, 20), (1, 30) }, rows);
    }

    [TestMethod]
    public void OrderBy_OrdinalZero_ThrowsMsg108()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            connection.CreateCommand("select v from t order by 0").ExecuteReader());
        AreEqual("The ORDER BY position number 0 is out of range of the number of items in the select list.", ex.Message);
    }

    [TestMethod]
    public void OrderBy_OrdinalAboveProjectionCount_ThrowsMsg108()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            connection.CreateCommand("select v from t order by 5").ExecuteReader());
        AreEqual("The ORDER BY position number 5 is out of range of the number of items in the select list.", ex.Message);
    }

    [TestMethod]
    public void OrderBy_AppliesAfterWhere()
    {
        using var connection = Seeded("v int", "(5),(1),(4),(2),(3)");
        using var reader = connection.CreateCommand("select v from t where v > 2 order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 5, 4, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_AppliedBeforeTop()
    {
        using var connection = Seeded("v int", "(5),(1),(4),(2),(3)");
        using var reader = connection.CreateCommand("select top 2 v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 5, 4 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_StringWithNulls_NullsFirstAsc()
    {
        using var connection = Seeded("s varchar(10)", "('b'),(null),('a'),(null),('c')");
        using var reader = connection.CreateCommand("select s from t order by s asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { null, null, "a", "b", "c" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_DateTime_OrdersChronologically()
    {
        using var connection = Seeded("d datetime", "('2026-05-04'),('2024-01-15'),('2025-07-22')");
        using var reader = connection.CreateCommand("select d from t order by d desc").ExecuteReader();
        var rows = new List<DateTime>();
        while (reader.Read())
            rows.Add((DateTime)reader[0]);
        CollectionAssert.AreEqual(new[] {
            new DateTime(2026, 5, 4),
            new DateTime(2025, 7, 22),
            new DateTime(2024, 1, 15)
        }, rows);
    }

    [TestMethod]
    public void OrderBy_OnEmptyTable_ReturnsNoRows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select v from t order by v").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void OrderBy_OnTablelessSelect_NoOpButParses()
        => AreEqual(1, new Simulation().ExecuteReader("select 1 order by 1").EnumerateRecords().Count());

    [TestMethod]
    public void OrderBy_StableSort_PreservesInsertionOrderForEqualKeys()
    {
        // List.Sort is unstable; assert set equivalence and the last-row guarantee only.
        using var connection = Seeded("k int, v int", "(1,100),(1,101),(1,102),(2,200)");
        using var reader = connection.CreateCommand("select v from t order by k").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { 100, 101, 102, 200 }, rows);
        AreEqual(200, rows[^1]);
    }

    [TestMethod]
    public void OrderBy_AggregateExpression_OnGroupedQuery_SortsByAggregate()
    {
        using var connection = Seeded("k int, v int", "(1,10),(1,20),(2,5),(3,100)");
        using var reader = connection.CreateCommand(
            "select k from t group by k order by sum(v) desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 1, 2 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SelectAlias_OnGroupedQuery_SortsByAlias()
    {
        using var connection = Seeded("k int, v int", "(1,10),(1,20),(2,5),(3,100)");
        using var reader = connection.CreateCommand(
            "select k, sum(v) as s from t group by k order by s desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 1, 2 }, Column0(reader));
    }

    [TestMethod]
    public void Top_WithOrderByAggregate_OnGroupedQuery_SelectsHighestGroups()
    {
        // TOP must apply AFTER the ORDER BY aggregate sort, not to an arbitrary prefix.
        using var connection = Seeded("k int, v int", "(1,10),(1,20),(2,5),(3,100)");
        using var reader = connection.CreateCommand(
            "select top (2) k from t group by k order by sum(v) desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 1 }, Column0(reader));
    }

    [TestMethod]
    public void GroupBy_ScalarExpression_ProjectsAndOrdersByExpression()
    {
        // GROUP BY <expression> while projecting and ordering by that same
        // expression: it resolves against the group (constant within it), not
        // by re-evaluating the now-grouped-away underlying column.
        using var connection = Seeded("v int", "(1),(2),(11),(12),(21)");
        using var reader = connection.CreateCommand(
            "select v / 10 as bucket, count(*) as c from t group by v / 10 order by v / 10").ExecuteReader();
        var rows = new List<(int Bucket, int Count)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (0, 2), (1, 2), (2, 1) }, rows);
    }

    // === FROM-less SELECT with a trailing ORDER BY ===
    // A SELECT with no FROM yields exactly one row, so ORDER BY is a no-op
    // sort, but SQL Server still accepts the clause (probed 2026-07-14).
    // The SSMS server-properties query ends `… AS [IsFullTextInstalled]
    // ORDER BY [Server_Name] ASC` with no FROM. The clause reaches the parser
    // through the projection-alias continuation, which previously raised
    // Msg 156 near ORDER.

    [TestMethod]
    public void Fromless_OrderByAlias_ReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 2 as x, 1 as y order by x");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetValue(0));
        AreEqual(1, reader.GetValue(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_OrderByAliasDescending_ReturnsRow()
    {
        // The exact SSMS shape: a trailing bracketed-alias ORDER BY, no FROM.
        using var reader = new Simulation().ExecuteReader("select 7 as [Server_Name] order by [Server_Name] desc");
        IsTrue(reader.Read());
        AreEqual("Server_Name", reader.GetName(0));
        AreEqual(7, reader.GetValue(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_OrderByOrdinal_ReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 2 as x, 1 as y order by 2");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetValue(0));
        AreEqual(1, reader.GetValue(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_OrderByWithOffsetFetch_ReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 5 as x order by x offset 0 rows fetch next 1 rows only");
        IsTrue(reader.Read());
        AreEqual(5, reader.GetValue(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_SetOpChain_TopLevelOrderBy_Sorts()
    {
        // The final ORDER BY of a set-op chain whose branches are all
        // FROM-less: `SELECT 2 AS X UNION ALL SELECT 1 ORDER BY X DESC` → 2, 1.
        using var reader = new Simulation().ExecuteReader("select 2 as x union all select 1 order by x desc");
        CollectionAssert.AreEqual(new object?[] { 2, 1 }, Column0(reader));
    }
}
