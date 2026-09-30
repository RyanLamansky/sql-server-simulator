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
        HasCount(2, rows);
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
        HasCount(1, rows);
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

        var ex = Throws<SimulatedSqlException>(() =>
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
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select top 2 distinct v from t").ExecuteReader().Read());
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

    /// <summary>
    /// An expression term under DISTINCT must match a select item by shape,
    /// not merely read projected columns: columns match by the source column
    /// they resolve to and parentheses are transparent, and the term sorts by
    /// the item it matches (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select distinct t.a + 1 as x from t order by a + 1 desc", 4)]
    [DataRow("select distinct a + 1 from t order by (t.a + 1) desc", 4)]
    [DataRow("select distinct -a from t order by -a", -3)]
    [DataRow("select distinct count(*) from t group by a order by count(*)", 1)]
    [DataRow("select distinct a, b * 2 from t order by b * 2", 2)]
    public void Distinct_OrderByExpressionMatchingSelectItem_Sorts(string select, int first)
        => AreEqual(first, new Simulation().ExecuteScalar("create table t (a int, b int); insert t values (1, 30), (2, 10), (3, 20); " + select));

    /// <summary>
    /// Anything else is Msg 145 while compiling — even an expression over
    /// projected columns alone, a reordered or retyped operand, and a subquery
    /// written in both places — the shapes Django's <c>nulls_first</c> ordering
    /// over a subquery annotation emits (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select distinct a, b from t order by b + 0")]
    [DataRow("select distinct 1 + a from t order by a + 1")]
    [DataRow("select distinct a, b + 1 from t order by b + 1.0")]
    [DataRow("select distinct a from t order by case when a is null then 0 else 1 end, a")]
    [DataRow("select distinct a, s from t order by s collate Latin1_General_BIN")]
    [DataRow("select distinct count(*) as c from t group by a order by count(*) + 1")]
    [DataRow("select distinct a, (select max(b) from t u where u.a = t.a) as m from t order by case when (select max(b) from t u where u.a = t.a) is null then 0 else 1 end")]
    [DataRow("select distinct a, (select 1) as s from t order by (select 1)")]
    public void Distinct_OrderByExpressionNotASelectItem_Msg145AtCompile(string select)
        => new Simulation().AssertSqlError("create table t (a int, b int, s varchar(10)); insert t values (1, 30, 'x'); print 'ran'; " + select, 145,
            "ORDER BY items must appear in the select list if SELECT DISTINCT is specified.");
}
