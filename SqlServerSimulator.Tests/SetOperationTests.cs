using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's set operators: <c>UNION</c> /
/// <c>UNION ALL</c> / <c>INTERSECT</c> / <c>EXCEPT</c>. Covers dedup
/// semantics (NULL-equals-NULL, opposite of <c>=</c>'s tri-state
/// behavior), type promotion across branches, the precedence rule
/// (INTERSECT binds tighter than UNION/EXCEPT), Msg 205 on column-count
/// mismatch, Msg 156 on per-branch ORDER BY, and the top-level ORDER BY
/// that applies post-chain. Sourced from probes against SQL Server 2025.
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

    // === UNION / UNION ALL ===

    [TestMethod]
    public void Union_Dedupes()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 union select 2 union select 1"));
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, values);
    }

    [TestMethod]
    public void UnionAll_PreservesDuplicates()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 union all select 2 union all select 1"));
        CollectionAssert.AreEqual(new[] { 1, 2, 1 }, values);
    }

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

    // === INTERSECT ===

    [TestMethod]
    public void Intersect_KeepsCommonRows()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 intersect select 1"));
        CollectionAssert.AreEqual(new[] { 1 }, values);
    }

    [TestMethod]
    public void Intersect_NoOverlap_Empty()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 intersect select 2"));
        IsEmpty(values);
    }

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

    // === EXCEPT ===

    [TestMethod]
    public void Except_RemovesRightSide()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 except select 2"));
        CollectionAssert.AreEqual(new[] { 1 }, values);
    }

    [TestMethod]
    public void Except_AllRemoved_Empty()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 except select 1"));
        IsEmpty(values);
    }

    [TestMethod]
    public void Except_DedupesLeftBeforeFiltering()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (1), (2)");

        using var connection = simulation.CreateOpenConnection();
        var values = ReadInts(connection.CreateCommand("select v from t except select 99"));
        // INTERSECT/EXCEPT both dedupe their left side (probe-confirmed).
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, values);
    }

    // === Type promotion / column count ===

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
    {
        var ex = Throws<DbException>(() =>
            _ = new Simulation().ExecuteScalar("select 1, 2 union select 3"));
        AreEqual("205", ex.Data["HelpLink.EvtID"]);
        AreEqual("All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.", ex.Message);
    }

    // === Precedence / chaining ===

    [TestMethod]
    public void Intersect_BindsTighterThanUnion()
    {
        // `1 union 2 intersect 2` should parse as `1 union (2 intersect 2)` = {1, 2}.
        var values = ReadInts(new Simulation().CreateCommand("select 1 union select 2 intersect select 2"));
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, values);
    }

    [TestMethod]
    public void ThreeBranchUnion_LeftAssociative()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 union select 2 union select 3"));
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, values);
    }

    [TestMethod]
    public void UnionAllAfterUnion_PreservesDupAtEnd()
    {
        // `(1 union 2) union all 1` = {1, 2} ++ {1} = {1, 2, 1}.
        var values = ReadInts(new Simulation().CreateCommand("select 1 union select 2 union all select 1"));
        CollectionAssert.AreEqual(new[] { 1, 2, 1 }, values);
    }

    // === ORDER BY interaction ===

    [TestMethod]
    public void TopLevelOrderBy_AppliesToCombinedResult()
    {
        // ORDER BY at the very end applies to the combined result and
        // can reference the first branch's column name.
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
    {
        var ex = Throws<DbException>(() =>
            _ = new Simulation().ExecuteScalar("select 1 order by 1 union select 2"));
        AreEqual("156", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SingleSelect_OrderByNonProjectedSource_StillWorks()
    {
        // The set-op refactor must not break the existing branch-internal
        // ORDER BY path: a non-set-op SELECT can still ORDER BY a source
        // column that's not in the projection list.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int)");
        _ = simulation.ExecuteNonQuery("insert into t values (3, 30), (1, 10), (2, 20)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select b from t order by a").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        // a=1,2,3 → b=10,20,30 in that order.
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, values);
    }

    // === Tabled-source set ops ===

    [TestMethod]
    public void Union_AcrossTwoTables_Dedupes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table left_t (v int)");
        _ = simulation.ExecuteNonQuery("create table right_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into left_t values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("insert into right_t values (3), (4), (5)");

        using var connection = simulation.CreateOpenConnection();
        var values = ReadInts(connection.CreateCommand(
            "select v from left_t union select v from right_t"));
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, values);
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
        var values = ReadInts(connection.CreateCommand(
            "select v from left_t intersect select v from right_t"));
        CollectionAssert.AreEqual(new[] { 3 }, values);
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
        var values = ReadInts(connection.CreateCommand(
            "select v from left_t except select v from right_t"));
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, values);
    }
}
