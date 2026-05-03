using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior of SELECT DISTINCT: dedup uses the same equality semantics as the
/// <c>=</c> operator (collation-aware string comparison, ANSI trailing-space
/// padding, two NULLs collapse to one, datetimeoffset by UTC instant). Also
/// covers ALL keyword acceptance, the DISTINCT-before-TOP ordering rule, and
/// the Msg 145 rejection when ORDER BY references a column not in the
/// projection.
/// </summary>
[TestClass]
public class DistinctTests
{
    [TestMethod]
    public void Distinct_RemovesIntDuplicates()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1),(2),(1),(3),(2)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct v from t order by v").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, rows);
    }

    [TestMethod]
    public void Distinct_NullsCollapseToOne()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1),(null),(2),(null),(1)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct v from t order by v").ExecuteReader();
        var rows = new List<object?>();
        while (reader.Read())
            rows.Add(reader.IsDBNull(0) ? null : reader[0]);
        CollectionAssert.AreEqual(new object?[] { null, 1, 2 }, rows);
    }

    [TestMethod]
    public void Distinct_StringsCollation_CaseInsensitive()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( s varchar(10) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('foo'),('FOO'),('Foo'),('bar')").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct s from t order by s").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add((string)reader[0]);
        // First-seen wins for the casing of the kept value (insertion order).
        CollectionAssert.AreEqual(new[] { "bar", "foo" }, rows);
    }

    [TestMethod]
    public void Distinct_StringsTrailingSpacePadding()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( s varchar(10) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('a'),('a   '),('a'),('b')").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct s from t order by s").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add((string)reader[0]);
        // ANSI padding makes 'a' and 'a   ' a single distinct value.
        AreEqual(2, rows.Count);
        AreEqual("b", rows[1]);
    }

    [TestMethod]
    public void Distinct_DateTimeOffset_DedupesByUtcInstant()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset(7) )").ExecuteNonQuery();
        _ = connection.CreateCommand(
            "insert t values ('2026-05-04 20:45:30 +07:00'),('2026-05-04 06:45:30 -07:00'),('2026-05-04 13:45:30 +00:00')").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct d from t").ExecuteReader();
        var rows = new List<DateTimeOffset>();
        while (reader.Read())
            rows.Add((DateTimeOffset)reader[0]);
        // All three rows refer to 2026-05-04 13:45:30 UTC; DISTINCT keeps one.
        AreEqual(1, rows.Count);
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero), rows[0].ToUniversalTime());
    }

    [TestMethod]
    public void Distinct_MultiColumn_DedupesByTuple()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,1),(1,2),(1,1),(2,1)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct a, b from t order by a, b").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (1, 1), (1, 2), (2, 1) }, rows);
    }

    [TestMethod]
    public void Distinct_WithTop()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1),(1),(2),(2),(3)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct top 2 v from t order by v").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        CollectionAssert.AreEqual(new[] { 1, 2 }, rows);
    }

    [TestMethod]
    public void Distinct_WithWhere()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1),(2),(3),(2),(1),(4)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct v from t where v >= 2 order by v").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, rows);
    }

    [TestMethod]
    public void Distinct_OrderByNonOutputColumn_ThrowsMsg145()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1,30),(2,10),(3,20)").ExecuteNonQuery();

        var ex = Throws<DbException>(() =>
        {
            using var reader = connection.CreateCommand("select distinct a from t order by b").ExecuteReader();
            while (reader.Read()) { /* drain so the lazy ORDER-key resolver fires */ }
        });
        AreEqual("ORDER BY items must appear in the select list if SELECT DISTINCT is specified.", ex.Message);
    }

    [TestMethod]
    public void All_KeywordAcceptedAsNoOp()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1),(2),(1)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select all v from t").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        CollectionAssert.AreEqual(new[] { 1, 2, 1 }, rows);
    }

    [TestMethod]
    public void Distinct_OnTablelessSelect_ReturnsOneRow()
    {
        using var reader = new Simulation().ExecuteReader("select distinct 1");
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void TopBeforeDistinct_IsSyntaxError()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();

        // SQL Server requires DISTINCT before TOP. Reversed order is a parse
        // failure (Msg 156 with "near 'distinct'").
        var ex = Throws<DbException>(() =>
            connection.CreateCommand("select top 2 distinct v from t").ExecuteReader());
        AreEqual("Incorrect syntax near the keyword 'distinct'.", ex.Message);
    }

    [TestMethod]
    public void Distinct_AllRowsIdentical_CollapseToOne()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (7),(7),(7),(7)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select distinct v from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(7, reader[0]);
        IsFalse(reader.Read());
    }
}
