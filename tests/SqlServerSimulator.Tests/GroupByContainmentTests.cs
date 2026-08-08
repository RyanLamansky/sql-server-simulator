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
        // The message names the object the FROM clause wrote, not the alias, so a
        // self-join reports the table twice (probed 2026-08-05).
        => Rejects(
            "select t.a, u.b from t join t as u on t.id = u.id group by t.a",
            8120,
            "'t.b'");

    [TestMethod]
    public void AliasedSource_MessageNamesTheTable_NotTheAlias()
        => Rejects("select a, b from t as x group by a", 8120, "'t.b'");

    [TestMethod]
    public void DerivedTableSource_MessageNamesItsAlias()
        => Rejects("select z.a, z.b from (select a, b from t) z group by z.a", 8120, "'z.b'");

    [TestMethod]
    public void SchemaQualifiedSource_MessageKeepsTheSchema()
        => Rejects("select a, b from dbo.t group by a", 8120, "'dbo.t.b'");

    /// <summary>
    /// A view is named the same way a table is — as the FROM clause wrote it,
    /// with any alias ignored (probed 2026-08-08).
    /// </summary>
    [TestMethod]
    public void ViewSource_MessageNamesTheViewAsWritten()
    {
        // CREATE VIEW must open its own batch, so the shared setup can't carry it.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        _ = sim.ExecuteNonQuery("create view dbo.v as select a, b from t");
        foreach (var select in new[] { "select a, b from dbo.v group by a", "select x.a, x.b from dbo.v as x group by x.a" })
            Assert.Contains("'dbo.v.b'", sim.AssertSqlError(select, 8120).Message);
    }

    /// <summary>
    /// A CTE has a name of its own, so it reports that rather than the alias
    /// the reference gave it — unlike a derived table, which has only the
    /// alias (probed 2026-08-08).
    /// </summary>
    [TestMethod]
    public void CteSource_MessageNamesTheCte_NotTheAlias()
    {
        Rejects("with c as (select a, b from t) select a, b from c group by a", 8120, "'c.b'");
        Rejects("with c as (select a, b from t) select q.a, q.b from c as q group by q.a", 8120, "'c.b'");
    }

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

    [TestMethod]
    public void ParenthesizedGroupingKey_IsStillTheColumn_Licensed()
        => AreEqual(10, Run("select a from t group by (a) order by a"));

    [TestMethod]
    public void SubExpressionOfGroupedExpression_Licensed()
        // Real matches a grouping expression against any *sub*-expression of the
        // projection, so wrapping arithmetic around it stays licensed.
        => AreEqual(220, Run("select (a + b) * 2 from t group by a + b order by a + b"));

    [TestMethod]
    public void CompoundOverTwoGroupedExpressions_Licensed()
        => AreEqual(210, Run("select (a + b) + (a * 10) from t group by a + b, a * 10 order by a + b"));

    [TestMethod]
    public void GroupingExpressionQualifiedDifferently_Licensed()
        // The match is on the bound column, so the two clauses may spell the
        // qualifier differently (probed 2026-08-05).
        => AreEqual(110, Run("select a + b from t as x group by x.a + x.b order by x.a + x.b"));

    [TestMethod]
    public void ParenthesesAroundGroupingExpression_Licensed()
        => AreEqual(110, Run("select a + b from t group by (a + b) order by a + b"));

    [TestMethod]
    public void CastOverGroupedExpression_Licensed()
        => AreEqual(110L, Run("select cast(a + b as bigint) from t group by a + b order by a + b"));

    // --- The structural rule's rejections ---

    [TestMethod]
    public void BareComponentOfGroupedExpression_Msg8120()
        // Grouping by a + 1 licenses `select a + 1`, not a bare `select a`.
        => Rejects("select a from t group by a + 1", 8120, "'t.a'");

    [TestMethod]
    public void DifferentExpressionOverGroupedComponent_Msg8120()
        // Structural, not algebraic: a + 0 is not a + 1.
        => Rejects("select a + 0 from t group by a + 1", 8120, "'t.a'");

    [TestMethod]
    public void OperandOrderMatters_Msg8120()
        => Rejects("select 1 + a from t group by a + 1", 8120, "'t.a'");

    [TestMethod]
    public void LiteralTypeMatters_Msg8120()
        // a + 1.0 is a different expression from a + 1.
        => Rejects("select a + 1 from t group by a + 1.0", 8120, "'t.a'");

    [TestMethod]
    public void BareComponentInHaving_Msg8121()
        => Rejects("select count(*) from t group by a + 1 having a > 0", 8121, "'t.a'");

    [TestMethod]
    public void BareComponentInOrderBy_Msg8127()
        => Rejects("select count(*) from t group by a + 1 order by a", 8127, "\"t.a\"");
}
