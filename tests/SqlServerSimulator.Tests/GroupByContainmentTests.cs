using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The GROUP BY containment rule (Msg 8120 select-list / 8121 HAVING / 8127
/// ORDER BY): in an aggregate query, a column referenced outside an aggregate
/// must be a GROUP BY column. Every case here was probed against SQL Server 2025
/// — the simulator previously accepted all of them, an over-permissive gap that
/// let malformed aggregate queries "work" against the simulator but fail on real.
/// SQL Server is strict: no functional-dependency relaxation (grouping by a PK
/// does not license the table's other columns), and it binds the rule at parse
/// time before any row is read.
/// </summary>
[TestClass]
public sealed class GroupByContainmentTests
{
    // id is the PK — its presence probes (and rules out) any functional-dependency relaxation.
    private const string Setup =
        "create table t (id int primary key, a int, b int, name varchar(20)); " +
        "insert t values (1, 10, 100, 'x'), (2, 10, 200, 'y'), (3, 20, 300, 'z'); ";

    private static object? Run(string select) => new Simulation().ExecuteScalar(Setup + select);

    private static void Rejects(string select, int number, string quotedColumn)
    {
        var ex = new Simulation().AssertSqlError(Setup + select, number);
        Assert.Contains(quotedColumn, ex.Message);
    }

    [TestMethod]
    public void SelectList_UngroupedColumn_Msg8120()
        => Rejects("select a, b, count(*) from t group by a", 8120, "'t.b'");

    [TestMethod]
    public void SelectList_NoFunctionalDependency_GroupedPkDoesNotLicenseOtherColumns()
        => Rejects("select id, name, count(*) from t group by id", 8120, "'t.name'");

    [TestMethod]
    public void SelectList_TwoColumnsOneGrouped_Msg8120()
        => Rejects("select a, b from t group by a", 8120, "'t.b'");

    [TestMethod]
    public void SelectList_AggregateWithoutGroupBy_BareColumnInvalid_Msg8120()
        => Rejects("select a, count(*) from t", 8120, "'t.a'");

    [TestMethod]
    public void Having_UngroupedColumn_Msg8121()
        => Rejects("select a, count(*) from t group by a having b > 0", 8121, "'t.b'");

    [TestMethod]
    public void OrderBy_UngroupedColumn_Msg8127_DoubleQuoted()
        => Rejects("select a, count(*) from t group by a order by b", 8127, "\"t.b\"");

    [TestMethod]
    public void Join_UngroupedColumnFromOtherSource_Msg8120()
        => Rejects(
            "select t.a, u.b from t join t as u on t.id = u.id group by t.a",
            8120,
            "'u.b'");

    // --- Licensed shapes: must NOT be flagged (false-positive guards) ---

    [TestMethod]
    public void ExpressionOverGroupedColumn_Licensed()
        => AreEqual(11, Run("select a + 1 from t group by a order by a")); // 10 + 1

    [TestMethod]
    public void ExpressionMatchingGroupedExpression_Licensed()
        => AreEqual(110, Run("select a + b from t group by a + b order by a + b")); // 10 + 100

    [TestMethod]
    public void Constant_NeedsNoGrouping_Licensed()
        => AreEqual("k", Run("select 'k' from t group by a"));

    [TestMethod]
    public void GroupedColumnOnly_DistinctLike_Licensed()
        => AreEqual(10, Run("select a from t group by a order by a"));

    [TestMethod]
    public void ColumnInsideAggregateOnly_Licensed()
        => AreEqual(100, Run("select min(b) from t group by a order by a"));

    [TestMethod]
    public void AggregateOnly_NoBareColumns_Licensed()
        => AreEqual(3, Run("select count(*) from t"));

    [TestMethod]
    public void OrderBy_GroupedColumn_Licensed()
        => AreEqual(10, Run("select a from t group by a order by a"));

    [TestMethod]
    public void OrderBy_SelectAlias_Licensed()
        // ORDER BY c (a SELECT alias) is licensed; ExecuteScalar returns the first
        // column `a` of the top row — group a=10 has the higher count (2), so a=10.
        => AreEqual(10, Run("select a, count(*) as c from t group by a order by c desc, a"));

    [TestMethod]
    public void CorrelatedSubqueryOverGroupedColumn_Licensed()
        // The correlated t.a is grouped; the subquery's own u.b never resolves
        // against the outer sources, so neither trips the check.
        => AreEqual(10, Run(
            "select a, (select count(*) from t as u where u.b = t.a) from t group by a order by a"));

    // --- Documented conservative divergence ---

    [TestMethod]
    public void SelectBareComponentOfGroupedExpression_ConservativelyAccepted()
        // Real rejects this (Msg 8120): grouping by a+1 licenses `select a+1`, not
        // a bare `select a`. Distinguishing it from the valid `select (a+1)*2`
        // needs sub-expression structural matching the simulator doesn't do, so a
        // column appearing only inside a compound GROUP BY expression is left
        // unflagged rather than risk a false positive. A rare, deliberate miss.
        => IsNotNull(Run("select a from t group by a + 1"));
}
