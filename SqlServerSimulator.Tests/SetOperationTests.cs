using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's set operators: <c>UNION</c> / <c>UNION ALL</c> /
/// <c>INTERSECT</c> / <c>EXCEPT</c>. Covers dedup semantics (NULL-equals-NULL,
/// opposite of <c>=</c>'s tri-state), type promotion across branches, the precedence
/// rule (INTERSECT &gt; UNION/EXCEPT), Msg 205 on column-count mismatch, Msg 156 on
/// per-branch ORDER BY, and post-chain top-level ORDER BY.
/// </summary>
[TestClass]
public sealed class SetOperationTests
{
    private static List<int> ReadInts(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        return values;
    }

    [TestMethod]
    public void Union_Dedupes()
        => CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(new Simulation().CreateCommand("select 1 union select 2 union select 1")));

    [TestMethod]
    public void UnionAll_PreservesDuplicates()
        => CollectionAssert.AreEqual(new[] { 1, 2, 1 },
            ReadInts(new Simulation().CreateCommand("select 1 union all select 2 union all select 1")));

    [TestMethod]
    public void Union_NullsCompareEqual_DedupedToSingleRow()
    {
        // SET ops treat NULLs as equal — opposite of `=` operator's UNKNOWN.
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select cast(null as int) union select cast(null as int)").ExecuteReader();
        var rows = 0;
        while (reader.Read())
            rows++;
        AreEqual(1, rows);
    }

    [TestMethod]
    public void Intersect_KeepsCommonRows()
        => CollectionAssert.AreEqual(new[] { 1 }, ReadInts(new Simulation().CreateCommand("select 1 intersect select 1")));

    [TestMethod]
    public void Intersect_NoOverlap_Empty()
        => IsEmpty(ReadInts(new Simulation().CreateCommand("select 1 intersect select 2")));

    [TestMethod]
    public void Intersect_NullsMatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select cast(null as int) intersect select cast(null as int)").ExecuteReader();
        var rows = 0;
        while (reader.Read()) rows++;
        AreEqual(1, rows);
    }

    [TestMethod]
    public void Except_RemovesRightSide()
        => CollectionAssert.AreEqual(new[] { 1 }, ReadInts(new Simulation().CreateCommand("select 1 except select 2")));

    [TestMethod]
    public void Except_AllRemoved_Empty()
        => IsEmpty(ReadInts(new Simulation().CreateCommand("select 1 except select 1")));

    [TestMethod]
    public void Except_DedupesLeftBeforeFiltering()
    {
        // INTERSECT/EXCEPT both dedupe their left side (probe-confirmed).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (1), (2)");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(connection.CreateCommand("select v from t except select 99")));
    }

    [TestMethod]
    public void TypePromotion_IntPlusDecimal_ProducesDecimal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select 1 union select 2.5").ExecuteReader();
        var values = new List<decimal>();
        while (reader.Read())
            values.Add(reader.GetDecimal(0));
        CollectionAssert.AreEquivalent(new[] { 1m, 2.5m }, values);
    }

    [TestMethod]
    public void MismatchedColumnCount_RaisesMsg205()
        => new Simulation().AssertSqlError("select 1, 2 union select 3", 205,
            "All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.");

    [TestMethod]
    public void Intersect_BindsTighterThanUnion()
    {
        // `1 union 2 intersect 2` parses as `1 union (2 intersect 2)` = {1, 2}.
        CollectionAssert.AreEquivalent(new[] { 1, 2 },
        ReadInts(new Simulation().CreateCommand("select 1 union select 2 intersect select 2")));
    }

    [TestMethod]
    public void ThreeBranchUnion_LeftAssociative()
        => CollectionAssert.AreEquivalent(new[] { 1, 2, 3 },
            ReadInts(new Simulation().CreateCommand("select 1 union select 2 union select 3")));

    [TestMethod]
    public void UnionAllAfterUnion_PreservesDupAtEnd()
    {
        // `(1 union 2) union all 1` = {1, 2} ++ {1} = {1, 2, 1}.
        CollectionAssert.AreEqual(new[] { 1, 2, 1 },
        ReadInts(new Simulation().CreateCommand("select 1 union select 2 union all select 1")));
    }

    [TestMethod]
    public void TopLevelOrderBy_AppliesToCombinedResult()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select 1 as v union select 2 union select 3 order by v desc").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, values);
    }

    [TestMethod]
    public void PerBranchOrderBy_RaisesMsg156()
        => _ = new Simulation().AssertSqlError("select 1 order by 1 union select 2", 156);

    [TestMethod]
    public void SingleSelect_OrderByNonProjectedSource_StillWorks()
    {
        // Non-set-op SELECT can ORDER BY a non-projected source column.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int)");
        _ = simulation.ExecuteNonQuery("insert into t values (3, 30), (1, 10), (2, 20)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select b from t order by a").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, values);
    }

    [TestMethod]
    public void Union_AcrossTwoTables_Dedupes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table left_t (v int)");
        _ = simulation.ExecuteNonQuery("create table right_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into left_t values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("insert into right_t values (3), (4), (5)");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 },
            ReadInts(connection.CreateCommand("select v from left_t union select v from right_t")));
    }

    [TestMethod]
    public void Intersect_AcrossTwoTables_Common()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table left_t (v int)");
        _ = simulation.ExecuteNonQuery("create table right_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into left_t values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("insert into right_t values (3), (4), (5)");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(new[] { 3 },
            ReadInts(connection.CreateCommand("select v from left_t intersect select v from right_t")));
    }

    [TestMethod]
    public void Except_AcrossTwoTables_LeftMinusRight()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table left_t (v int)");
        _ = simulation.ExecuteNonQuery("create table right_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into left_t values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("insert into right_t values (3), (4), (5)");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(connection.CreateCommand("select v from left_t except select v from right_t")));
    }
}
