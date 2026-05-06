using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>EXISTS (SELECT ...)</c> and
/// <c>IN (SELECT ...)</c> / <c>NOT IN (SELECT ...)</c>: non-correlated and
/// correlated forms, NULL semantics that mirror the literal-list IN, and
/// SQL Server's single-column requirement for IN(SELECT) (Msg 116). NULL
/// semantics, multi-column rejection, and correlation-across-multiple-levels
/// are all sourced from probes against the SQL Server 2025 reference.
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

    // === EXISTS — non-correlated ===

    [TestMethod]
    public void Exists_NonEmpty_ReturnsTrue()
    {
        AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select 1)"));
    }

    [TestMethod]
    public void Exists_AllNullRow_ReturnsTrue()
    {
        // EXISTS counts rows; NULL contents don't matter.
        AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select null)"));
    }

    [TestMethod]
    public void Exists_Empty_ReturnsFalse()
    {
        IsNull(new Simulation().ExecuteScalar("select 1 where exists (select 1 where 1=0)"));
    }

    [TestMethod]
    public void NotExists_Empty_ReturnsTrue()
    {
        AreEqual(1, new Simulation().ExecuteScalar("select 1 where not exists (select 1 where 1=0)"));
    }

    [TestMethod]
    public void Exists_MultiColumnInner_Allowed()
    {
        // EXISTS is the documented exception to the single-column rule.
        AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select 1, 2)"));
    }

    // === EXISTS — correlated ===

    [TestMethod]
    public void Exists_Correlated_FiltersByMatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t1 (id int, name nvarchar(20))");
        _ = simulation.ExecuteNonQuery("create table t2 (id int, parent_id int)");
        _ = simulation.ExecuteNonQuery("insert into t1 values (1, 'one'), (2, 'two'), (3, 'three')");
        _ = simulation.ExecuteNonQuery("insert into t2 values (1, 1), (1, 2)");

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where exists (select 1 from t2 where t2.parent_id = t1.id)"));
        CollectionAssert.AreEquivalent(new int?[] { 1, 2 }, matched);
    }

    [TestMethod]
    public void NotExists_Correlated_FiltersByNoMatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t1 (id int)");
        _ = simulation.ExecuteNonQuery("create table t2 (id int, parent_id int)");
        _ = simulation.ExecuteNonQuery("insert into t1 values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("insert into t2 values (1, 1), (1, 2)");

        using var connection = simulation.CreateOpenConnection();
        var unmatched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where not exists (select 1 from t2 where t2.parent_id = t1.id)"));
        CollectionAssert.AreEquivalent(new int?[] { 3 }, unmatched);
    }

    [TestMethod]
    public void Exists_QualifierShadow_ResolvesOuterColumn()
    {
        // Both tables have an `id` column. The qualifier `t1.id` inside the
        // inner SELECT must resolve to the outer scope's id, not the inner.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t1 (id int)");
        _ = simulation.ExecuteNonQuery("create table t2 (id int, parent_id int)");
        _ = simulation.ExecuteNonQuery("insert into t1 values (10)");
        _ = simulation.ExecuteNonQuery("insert into t2 values (20, 10)");

        using var connection = simulation.CreateOpenConnection();
        // Inner predicate `t2.parent_id = t1.id` — t1.id resolves to the
        // outer t1's id (10), not to t2.id (20).
        var matched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where exists (select 1 from t2 where t2.parent_id = t1.id)"));
        CollectionAssert.AreEqual(new int?[] { 10 }, matched);
    }

    // === IN (SELECT) — non-correlated, NULL semantics ===

    [TestMethod]
    public void InSelect_Match_ReturnsTrue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (5), (6)");
        AreEqual(1, simulation.ExecuteScalar("select 1 where 5 in (select v from t)"));
    }

    [TestMethod]
    public void InSelect_NoMatch_ReturnsFalse()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (5), (6)");
        IsNull(simulation.ExecuteScalar("select 1 where 7 in (select v from t)"));
    }

    [TestMethod]
    public void InSelect_NullLhs_ReturnsUnknown()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (5)");
        IsNull(simulation.ExecuteScalar("select 1 where cast(null as int) in (select v from t)"));
    }

    [TestMethod]
    public void InSelect_NullRowOnly_ReturnsUnknown()
    {
        // 5 IN (NULL) is UNKNOWN — same as 5 = NULL.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int null)");
        _ = simulation.ExecuteNonQuery("insert into t values (null)");
        IsNull(simulation.ExecuteScalar("select 1 where 5 in (select v from t)"));
    }

    [TestMethod]
    public void InSelect_MatchPlusNullRow_MatchWins()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int null)");
        _ = simulation.ExecuteNonQuery("insert into t values (5), (null)");
        AreEqual(1, simulation.ExecuteScalar("select 1 where 5 in (select v from t)"));
    }

    [TestMethod]
    public void InSelect_NoMatchPlusNullRow_ReturnsUnknown()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int null)");
        _ = simulation.ExecuteNonQuery("insert into t values (5), (null)");
        IsNull(simulation.ExecuteScalar("select 1 where 6 in (select v from t)"));
    }

    [TestMethod]
    public void NotInSelect_NoMatchPlusNullRow_ReturnsUnknown()
    {
        // The classic NOT IN gotcha: NULL in the subquery turns the answer
        // UNKNOWN even when the LHS clearly isn't a non-NULL match.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int null)");
        _ = simulation.ExecuteNonQuery("insert into t values (5), (null)");
        IsNull(simulation.ExecuteScalar("select 1 where 6 not in (select v from t)"));
    }

    [TestMethod]
    public void NotInSelect_NoMatchNoNullRow_ReturnsTrue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (5)");
        AreEqual(1, simulation.ExecuteScalar("select 1 where 6 not in (select v from t)"));
    }

    [TestMethod]
    public void InSelect_Empty_ReturnsFalse()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        IsNull(simulation.ExecuteScalar("select 1 where 5 in (select v from t)"));
    }

    [TestMethod]
    public void NotInSelect_Empty_ReturnsTrue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int)");
        AreEqual(1, simulation.ExecuteScalar("select 1 where 5 not in (select v from t)"));
    }

    // === IN (SELECT) — correlated ===

    [TestMethod]
    public void InSelect_Correlated_FiltersByMatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t1 (id int)");
        _ = simulation.ExecuteNonQuery("create table t2 (id int, parent_id int)");
        _ = simulation.ExecuteNonQuery("insert into t1 values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("insert into t2 values (10, 1), (20, 2)");

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select t1.id from t1 where t1.id in (select t2.parent_id from t2)"));
        CollectionAssert.AreEquivalent(new int?[] { 1, 2 }, matched);
    }

    // === Single-column requirement (Msg 116) ===

    [TestMethod]
    public void InSelect_MultiColumnInner_RaisesMsg116()
    {
        var ex = Throws<DbException>(() =>
            _ = new Simulation().ExecuteScalar("select 1 where 5 in (select 1, 2)"));
        AreEqual("116", ex.Data["HelpLink.EvtID"]);
        AreEqual("Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.", ex.Message);
    }

    // === Combined with HAVING ===

    [TestMethod]
    public void InSelect_InHavingClause_FiltersGroups()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table q (k int, v int)");
        _ = simulation.ExecuteNonQuery("create table allowed (s int)");
        _ = simulation.ExecuteNonQuery("insert into q values (1, 10), (1, 20), (2, 30)");
        _ = simulation.ExecuteNonQuery("insert into allowed values (30), (50)");

        using var connection = simulation.CreateOpenConnection();
        // group sums: k=1 → 30, k=2 → 30. Both match `s in (30)` so both groups pass.
        var matched = ReadIntColumn(connection.CreateCommand(
            "select k from q group by k having sum(v) in (select s from allowed)"));
        CollectionAssert.AreEquivalent(new int?[] { 1, 2 }, matched);
    }

    // === Two-level nesting ===

    [TestMethod]
    public void Exists_TwoLevelNesting_ResolvesOuterAcrossMiddle()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (x int)");
        _ = simulation.ExecuteNonQuery("create table b (y int)");
        _ = simulation.ExecuteNonQuery("create table c (z int)");
        _ = simulation.ExecuteNonQuery("insert into a values (1), (2)");
        _ = simulation.ExecuteNonQuery("insert into b values (1)");
        _ = simulation.ExecuteNonQuery("insert into c values (1)");

        using var connection = simulation.CreateOpenConnection();
        // Innermost predicate `c.z = a.x` reaches a TWO levels up — through
        // b's scope, which doesn't shadow `a.x` because the qualifier `a.`
        // routes past it.
        var matched = ReadIntColumn(connection.CreateCommand(
            "select x from a where exists (select 1 from b where exists (select 1 from c where c.z = a.x))"));
        CollectionAssert.AreEqual(new int?[] { 1 }, matched);
    }

    // === Plan reuse — non-correlated re-execution per outer row ===

    [TestMethod]
    public void InSelect_NonCorrelated_ScansSubqueryPerOuterRow()
    {
        // Functional check that the subquery semantically matches per outer
        // row even when the inner SELECT is non-correlated. (Performance
        // optimization to cache the inner result is left for later.)
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table outer_t (v int)");
        _ = simulation.ExecuteNonQuery("create table inner_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into outer_t values (1), (2), (3), (4)");
        _ = simulation.ExecuteNonQuery("insert into inner_t values (2), (4)");

        using var connection = simulation.CreateOpenConnection();
        var matched = ReadIntColumn(connection.CreateCommand(
            "select v from outer_t where v in (select v from inner_t)"));
        CollectionAssert.AreEquivalent(new int?[] { 2, 4 }, matched);
    }

    // === EXISTS with table-alias resolution ===

    [TestMethod]
    public void Exists_TableAlias_QualifiesCorrelatedRef()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table parent (id int)");
        _ = simulation.ExecuteNonQuery("create table child (parent_id int)");
        _ = simulation.ExecuteNonQuery("insert into parent values (1), (2)");
        _ = simulation.ExecuteNonQuery("insert into child values (1)");

        using var connection = simulation.CreateOpenConnection();
        // The alias `p` qualifies the outer reference inside the correlated
        // subquery, distinct from the inner alias `c`.
        var matched = ReadIntColumn(connection.CreateCommand(
            "select p.id from parent as p where exists (select 1 from child as c where c.parent_id = p.id)"));
        CollectionAssert.AreEqual(new int?[] { 1 }, matched);
    }
}
