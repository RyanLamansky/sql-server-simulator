using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>EXISTS (SELECT ...)</c> and <c>IN (SELECT ...)</c>
/// / <c>NOT IN (SELECT ...)</c>: non-correlated and correlated forms, NULL
/// semantics that mirror the literal-list IN, and SQL Server's single-column
/// requirement (Msg 116).
/// </summary>
[TestClass]
public sealed class SubqueryTests
{
    private static List<int?> ReadIntColumn(DbCommand command, int ordinal = 0)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<int?>();
        while (reader.Read())
            rows.Add(reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal));
        return rows;
    }

    [TestMethod]
    public void Exists_NonEmpty_ReturnsTrue() => AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select 1)"));

    [TestMethod]
    public void Exists_AllNullRow_ReturnsTrue()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select null)"));

    [TestMethod]
    public void Exists_Empty_ReturnsFalse()
        => IsNull(new Simulation().ExecuteScalar("select 1 where exists (select 1 where 1=0)"));

    [TestMethod]
    public void NotExists_Empty_ReturnsTrue()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 where not exists (select 1 where 1=0)"));

    [TestMethod]
    public void Exists_MultiColumnInner_Allowed()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select 1, 2)"));

    [TestMethod]
    public void Exists_Correlated_FiltersByMatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t1 (id int, name nvarchar(20));
            create table t2 (id int, parent_id int);
            insert t1 values (1, 'one'), (2, 'two'), (3, 'three');
            insert t2 values (1, 1), (1, 2)
            """);

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where exists (select 1 from t2 where t2.parent_id = t1.id)"));
        CollectionAssert.AreEquivalent(new int?[] { 1, 2 }, matched);
    }

    [TestMethod]
    public void NotExists_Correlated_FiltersByNoMatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t1 (id int);
            create table t2 (id int, parent_id int);
            insert t1 values (1), (2), (3);
            insert t2 values (1, 1), (1, 2)
            """);

        using var connection = simulation.CreateOpenConnection();
        var unmatched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where not exists (select 1 from t2 where t2.parent_id = t1.id)"));
        CollectionAssert.AreEquivalent(new int?[] { 3 }, unmatched);
    }

    // Both tables have `id`; qualifier `t1.id` inside inner SELECT must resolve to outer scope's id, not inner's.
    [TestMethod]
    public void Exists_QualifierShadow_ResolvesOuterColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t1 (id int);
            create table t2 (id int, parent_id int);
            insert t1 values (10);
            insert t2 values (20, 10)
            """);

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where exists (select 1 from t2 where t2.parent_id = t1.id)"));
        CollectionAssert.AreEqual(new int?[] { 10 }, matched);
    }

    [TestMethod]
    public void InSelect_Match_ReturnsTrue()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (v int);
            insert t values (5), (6);
            select 1 where 5 in (select v from t)
            """));

    [TestMethod]
    public void InSelect_NoMatch_ReturnsFalse()
        => IsNull(new Simulation().ExecuteScalar("""
            create table t (v int);
            insert t values (5), (6);
            select 1 where 7 in (select v from t)
            """));

    [TestMethod]
    public void InSelect_NullLhs_ReturnsUnknown()
        => IsNull(new Simulation().ExecuteScalar("""
            create table t (v int);
            insert t values (5);
            select 1 where cast(null as int) in (select v from t)
            """));

    [TestMethod]
    public void InSelect_NullRowOnly_ReturnsUnknown()
        => IsNull(new Simulation().ExecuteScalar("""
            create table t (v int null);
            insert t values (null);
            select 1 where 5 in (select v from t)
            """));

    [TestMethod]
    public void InSelect_MatchPlusNullRow_MatchWins()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (v int null);
            insert t values (5), (null);
            select 1 where 5 in (select v from t)
            """));

    [TestMethod]
    public void InSelect_NoMatchPlusNullRow_ReturnsUnknown()
        => IsNull(new Simulation().ExecuteScalar("""
            create table t (v int null);
            insert t values (5), (null);
            select 1 where 6 in (select v from t)
            """));

    // Classic NOT IN gotcha: NULL in subquery → UNKNOWN even when LHS clearly isn't a non-NULL match.
    [TestMethod]
    public void NotInSelect_NoMatchPlusNullRow_ReturnsUnknown()
        => IsNull(new Simulation().ExecuteScalar("""
            create table t (v int null);
            insert t values (5), (null);
            select 1 where 6 not in (select v from t)
            """));

    [TestMethod]
    public void NotInSelect_NoMatchNoNullRow_ReturnsTrue()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (v int);
            insert t values (5);
            select 1 where 6 not in (select v from t)
            """));

    [TestMethod]
    public void InSelect_Empty_ReturnsFalse()
        => IsNull(new Simulation().ExecuteScalar("""
            create table t (v int);
            select 1 where 5 in (select v from t)
            """));

    [TestMethod]
    public void NotInSelect_Empty_ReturnsTrue()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (v int);
            select 1 where 5 not in (select v from t)
            """));

    [TestMethod]
    public void InSelect_Correlated_FiltersByMatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t1 (id int);
            create table t2 (id int, parent_id int);
            insert t1 values (1), (2), (3);
            insert t2 values (10, 1), (20, 2)
            """);

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where t1.id in (select t2.parent_id from t2)"));
        CollectionAssert.AreEquivalent(new int?[] { 1, 2 }, matched);
    }

    [TestMethod]
    public void InSelect_MultiColumnInner_RaisesMsg116()
        => new Simulation().AssertSqlError("select 1 where 5 in (select 1, 2)", 116,
            "Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.");

    [TestMethod]
    public void InSelect_InHavingClause_FiltersGroups()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table q (k int, v int);
            create table allowed (s int);
            insert q values (1, 10), (1, 20), (2, 30);
            insert allowed values (30), (50)
            """);

        using var connection = simulation.CreateOpenConnection();
        // Group sums: k=1 → 30, k=2 → 30. Both match `s in (30)`.
        var matched = ReadIntColumn(connection.CreateCommand(
            "select k from q group by k having sum(v) in (select s from allowed)"));
        CollectionAssert.AreEquivalent(new int?[] { 1, 2 }, matched);
    }

    [TestMethod]
    public void Exists_TwoLevelNesting_ResolvesOuterAcrossMiddle()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (x int);
            create table b (y int);
            create table c (z int);
            insert a values (1), (2);
            insert b values (1);
            insert c values (1)
            """);

        using var connection = simulation.CreateOpenConnection();
        // Innermost predicate `c.z = a.x` reaches TWO levels up — through b's scope.
        var matched = ReadIntColumn(connection.CreateCommand(
            "select x from a where exists (select 1 from b where exists (select 1 from c where c.z = a.x))"));
        CollectionAssert.AreEqual(new int?[] { 1 }, matched);
    }

    [TestMethod]
    public void InSelect_NonCorrelated_ScansSubqueryPerOuterRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table outer_t (v int);
            create table inner_t (v int);
            insert outer_t values (1), (2), (3), (4);
            insert inner_t values (2), (4)
            """);

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select v from outer_t where v in (select v from inner_t)"));
        CollectionAssert.AreEquivalent(new int?[] { 2, 4 }, matched);
    }

    [TestMethod]
    public void Scalar_InProjection_ReturnsValue() => AreEqual(1, new Simulation().ExecuteScalar("select (select 1)"));

    [TestMethod]
    public void Scalar_InArithmetic_FlowsThroughOperator()
        => AreEqual(6, new Simulation().ExecuteScalar("select (select 1) + 5"));

    [TestMethod]
    public void Scalar_EmptyResult_IsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select (select 1 where 1=0)"));

    [TestMethod]
    public void Scalar_EmptyInArithmetic_PropagatesNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select (select 1 where 1=0) + 5"));

    [TestMethod]
    public void Scalar_InWhereComparison_FiltersByValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (x int);
            insert t values (5), (10), (15)
            """);

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand("select x from t where x = (select max(x) from t)"));
        CollectionAssert.AreEqual(new int?[] { 15 }, matched);
    }

    [TestMethod]
    public void Scalar_NullValueInRow_PropagatesNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select (select cast(null as int)) + 5"));

    [TestMethod]
    public void Scalar_MultiRow_RaisesMsg512()
        => new Simulation().AssertSqlError("""
            create table t (x int);
            insert t values (1), (2);
            select (select x from t)
            """, 512,
            "Subquery returned more than 1 value. This is not permitted when the subquery follows =, !=, <, <= , >, >= or when the subquery is used as an expression.");

    [TestMethod]
    public void Scalar_MultiColumn_RaisesMsg116()
        => _ = new Simulation().AssertSqlError("select (select 1, 2)", 116);

    [TestMethod]
    public void Scalar_Correlated_ResolvesPerRow()
    {
        // For each a.id, look up b.val. id=3 has no match → NULL.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int);
            create table b (id int, val int);
            insert a values (1), (2), (3);
            insert b values (1, 100), (2, 200)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a.id, (select b.val from b where b.id = a.id) as v from a").ExecuteReader();
        var ids = new List<int>();
        var vals = new List<int?>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
            vals.Add(reader.IsDBNull(1) ? null : reader.GetInt32(1));
        }
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
        CollectionAssert.AreEqual(new int?[] { 100, 200, null }, vals);
    }

    // Per-outer-row Msg 512: a.id=1 matches two b rows.
    [TestMethod]
    public void Scalar_CorrelatedMultiRow_RaisesMsg512()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int);
            create table b (id int, val int);
            insert a values (1);
            insert b values (1, 100), (1, 200)
            """);

        using var connection = simulation.CreateOpenConnection();
        var ex = Throws<DbException>(() =>
        {
            using var reader = connection.CreateCommand("select (select b.val from b where b.id = a.id) from a").ExecuteReader();
            while (reader.Read()) { }
        });
        AreEqual("512", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Scalar_AggregateInner_ReturnsAggregate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int);
            create table b (id int);
            insert a values (1), (2);
            insert b values (1), (2), (3)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a.id, (select count(*) from b) as c from a").ExecuteReader();
        var ids = new List<int>();
        var counts = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
            counts.Add(reader.GetInt32(1));
        }
        CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
        CollectionAssert.AreEqual(new[] { 3, 3 }, counts);
    }

    [TestMethod]
    public void Exists_TableAlias_QualifiesCorrelatedRef()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table parent (id int);
            create table child (parent_id int);
            insert parent values (1), (2);
            insert child values (1)
            """);

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select p.id from parent as p where exists (select 1 from child as c where c.parent_id = p.id)"));
        CollectionAssert.AreEqual(new int?[] { 1 }, matched);
    }
}
