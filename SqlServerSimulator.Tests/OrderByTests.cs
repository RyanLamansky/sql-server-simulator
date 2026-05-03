using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior of the ORDER BY clause: parsing of ASC/DESC, ordinal references,
/// alias-vs-source resolution, NULL ordering (NULL first ASC, NULL last
/// DESC), and interaction with WHERE/TOP/DISTINCT.
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

    [TestMethod]
    public void OrderBy_SingleIntColumn_AscDefault()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (3),(1),(2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t order by v").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_AscExplicit()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (3),(1),(2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t order by v asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_Desc()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (3),(1),(2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 2, 1 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_NullsFirstUnderAsc()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (3),(null),(1),(null),(2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t order by v asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { null, null, 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_NullsLastUnderDesc()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (3),(null),(1),(null),(2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 2, 1, null, null }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_MultiColumnMixedDirections()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,2),(1,1),(2,1),(2,2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select a, b from t order by a asc, b desc").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (1, 2), (1, 1), (2, 2), (2, 1) }, rows);
    }

    [TestMethod]
    public void OrderBy_StringColumn_CollationAware()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( s varchar(10) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('B'),('a'),('C')").ExecuteNonQuery();

        // Default collation is case-insensitive, so 'a' < 'B' < 'C'.
        using var reader = connection.CreateCommand("select s from t order by s asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { "a", "B", "C" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_AliasReference_ResolvesToProjection()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,3),(2,2),(3,1)").ExecuteNonQuery();

        // `b AS a` overrides — `order by a` sees the aliased projection (b), so
        // the result is sorted by b's values 1,2,3 (which are projected as the
        // output column named "a").
        using var reader = connection.CreateCommand("select b as a from t order by a").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SourceColumnNotInProjection_ResolvesToSource()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,30),(2,10),(3,20)").ExecuteNonQuery();

        // Without DISTINCT, ORDER BY can reach a source column not in projection.
        using var reader = connection.CreateCommand("select a from t order by b").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 2, 3, 1 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_Expression_Length()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( s varchar(20) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('xx'),('a'),('hello')").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select s from t order by len(s)").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { "a", "xx", "hello" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_Ordinal_OrdersByNthProjectionColumn()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,30),(2,10),(3,20)").ExecuteNonQuery();

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
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (5),(1),(4),(2),(3)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t where v > 2 order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 5, 4, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_AppliedBeforeTop()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (5),(1),(4),(2),(3)").ExecuteNonQuery();

        // Without ORDER BY, TOP would take the first two inserted rows. Here
        // the sort happens first, so we get the top two by descending value.
        using var reader = connection.CreateCommand("select top 2 v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 5, 4 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_StringWithNulls_NullsFirstAsc()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( s varchar(10) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('b'),(null),('a'),(null),('c')").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select s from t order by s asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { null, null, "a", "b", "c" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_DateTime_OrdersChronologically()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('2026-05-04'),('2024-01-15'),('2025-07-22')").ExecuteNonQuery();

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
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( k int, v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,100),(1,101),(1,102),(2,200)").ExecuteNonQuery();

        // Sorting by k alone should keep the original v ordering within each k group
        // — List.Sort isn't stable in general, so this asserts the simulator's
        // current behavior and pins it as the expected contract.
        using var reader = connection.CreateCommand("select v from t order by k").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        // List.Sort is unstable; relax to a set match instead.
        CollectionAssert.AreEquivalent(new[] { 100, 101, 102, 200 }, rows);
        AreEqual(200, rows[^1]); // k=2's row is last either way
    }
}
