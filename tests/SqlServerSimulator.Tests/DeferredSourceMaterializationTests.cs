using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior a FROM source keeps once a non-leftmost, non-APPLY deferred plan —
/// a derived table, a CTE reference or a view — runs once per enumeration
/// instead of once per left-side row: the rows every join kind produces, the
/// laterality APPLY keeps, the per-call-varying built-in that declines the
/// reuse, and the correlation a rowset function's arguments still carry. The
/// join strategy the materialized source becomes eligible for is asserted in
/// <c>SqlServerSimulator.Tests.Internal</c>'s <c>JoinStrategyTests</c>.
/// </summary>
[TestClass]
public sealed class DeferredSourceMaterializationTests
{
    /// <summary>
    /// Four customers in two categories, six order lines across three of them —
    /// the join-to-a-grouped-aggregate report shape, small enough to assert
    /// exact sums.
    /// </summary>
    private static Simulation WithSales()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table customers (id int not null primary key, category int not null);
            create table lines (id int not null primary key, customer_id int not null, amount int not null);
            insert customers values (1, 3), (2, 3), (3, 3), (4, 9);
            insert lines values (1, 1, 100), (2, 1, 5), (3, 2, 20), (4, 2, 2), (5, 3, 7), (6, 4, 1000);
            """);
        return sim;
    }

    // ---- the row sets, per join kind ------------------------------------

    /// <summary>
    /// The measured shape: a grouped aggregate written as the <em>second</em>
    /// FROM source. Its rows are the same whichever left row is being joined,
    /// so one execution answers all of them — and the sum over the matched
    /// category-3 customers is unchanged (105 + 22 + 7).
    /// </summary>
    [TestMethod]
    public void DerivedAggregate_JoinedSecond_SumsPerLeftRow()
        => AreEqual(134, WithSales().ExecuteScalar("""
            select sum(agg.revenue) from customers c
            join (select customer_id, sum(amount) as revenue from lines group by customer_id) agg
                on agg.customer_id = c.id
            where c.category = 3
            """));

    /// <summary>
    /// Same query with the derived table written first — the leftmost source is
    /// left deferred (it already executes once and streams), so this is the
    /// control that the two orders still agree.
    /// </summary>
    [TestMethod]
    public void DerivedAggregate_JoinedFirst_MatchesTheSecondPositionForm()
        => AreEqual(134, WithSales().ExecuteScalar("""
            select sum(agg.revenue)
            from (select customer_id, sum(amount) as revenue from lines group by customer_id) agg
            join customers c on c.id = agg.customer_id
            where c.category = 3
            """));

    /// <summary>
    /// A left row with no match in the derived table keeps its row with the
    /// right side NULL-filled: customer 4's only line is excluded by the
    /// derived table's own WHERE, so its revenue reads NULL.
    /// </summary>
    [TestMethod]
    public void DerivedTable_LeftJoined_NullFillsUnmatchedLeftRows()
        => AreEqual(1, WithSales().ExecuteScalar("""
            select count(*) from customers c
            left join (select customer_id, sum(amount) as revenue from lines
                       where amount < 500 group by customer_id) agg
                on agg.customer_id = c.id
            where agg.revenue is null
            """));

    /// <summary>An empty derived table drops every left row from an INNER join.</summary>
    [TestMethod]
    public void EmptyDerivedTable_InnerJoined_YieldsNoRows()
        => AreEqual(0, WithSales().ExecuteScalar("""
            select count(*) from customers c
            join (select customer_id from lines where amount > 100000) d on d.customer_id = c.id
            """));

    /// <summary>…and NULL-fills every left row from a LEFT join.</summary>
    [TestMethod]
    public void EmptyDerivedTable_LeftJoined_NullFillsEveryLeftRow()
        => AreEqual(4, WithSales().ExecuteScalar("""
            select count(*) from customers c
            left join (select customer_id from lines where amount > 100000) d on d.customer_id = c.id
            where d.customer_id is null
            """));

    /// <summary>
    /// RIGHT JOIN already materialized its right side before this change; the
    /// unmatched-right rows it emits with the left NULL-filled must survive it.
    /// Customer 9 has no row in <c>customers</c>, so it comes through with a
    /// NULL id.
    /// </summary>
    [TestMethod]
    public void DerivedTable_RightJoined_StillEmitsUnmatchedRightRows()
        => AreEqual(1, WithSales().ExecuteScalar("""
            select count(*) from customers c
            right join (select 9 as customer_id union all select 1) d on d.customer_id = c.id
            where c.id is null
            """));

    /// <summary>FULL JOIN emits both unmatched sides around the one match.</summary>
    [TestMethod]
    public void DerivedTable_FullJoined_EmitsBothUnmatchedSides()
        => AreEqual(5, WithSales().ExecuteScalar("""
            select count(*) from customers c
            full join (select 9 as customer_id union all select 1) d on d.customer_id = c.id
            """));

    /// <summary>CROSS JOIN multiplies both sides.</summary>
    [TestMethod]
    public void DerivedTable_CrossJoined_MultipliesBothSides()
        => AreEqual(8, WithSales().ExecuteScalar(
            "select count(*) from customers c cross join (select 1 as x union all select 2) d"));

    /// <summary>
    /// A CTE reference carries the same deferred plan a derived table does, so
    /// the join-to-a-grouped-aggregate shape answers identically through it.
    /// </summary>
    [TestMethod]
    public void CteReference_JoinedSecond_SumsPerLeftRow()
        => AreEqual(134, WithSales().ExecuteScalar("""
            with agg as (select customer_id, sum(amount) as revenue from lines group by customer_id)
            select sum(agg.revenue) from customers c
            join agg on agg.customer_id = c.id
            where c.category = 3
            """));

    /// <summary>
    /// A view body is isolated from the caller's column scope entirely, so a
    /// view written non-first materializes on the same rule.
    /// </summary>
    [TestMethod]
    public void View_JoinedSecond_SumsPerLeftRow()
    {
        var sim = WithSales();
        sim.ExecuteBatches(
            "create view revenue_by_customer as select customer_id, sum(amount) as revenue from lines group by customer_id");
        AreEqual(134, sim.ExecuteScalar("""
            select sum(v.revenue) from customers c
            join revenue_by_customer v on v.customer_id = c.id
            where c.category = 3
            """));
    }

    /// <summary>
    /// A derived table over a derived table: the inner one is the outer body's
    /// leftmost source, the outer one the enclosing join's second — both answer
    /// the same rows they did per-left-row.
    /// </summary>
    [TestMethod]
    public void NestedDerivedTables_JoinedSecond_SumsPerLeftRow()
        => AreEqual(134, WithSales().ExecuteScalar("""
            select sum(agg.revenue) from customers c
            join (select inner_lines.customer_id, sum(inner_lines.amount) as revenue
                  from (select customer_id, amount from lines) inner_lines
                  group by inner_lines.customer_id) agg
                on agg.customer_id = c.id
            where c.category = 3
            """));

    /// <summary>
    /// Two derived tables in one chain both materialize, and the row-limited one
    /// keeps the row its own ORDER BY / TOP chose — customer 3, whose 7 is the
    /// smallest per-customer total.
    /// </summary>
    [TestMethod]
    public void TwoDerivedTables_InOneChain_BothAnswerTheirOwnRows()
        => AreEqual(7, WithSales().ExecuteScalar("""
            select sum(top_agg.revenue) from customers c
            join (select id, category from customers) meta on meta.id = c.id
            join (select top 1 customer_id, sum(amount) as revenue from lines
                  group by customer_id order by sum(amount)) top_agg
                on top_agg.customer_id = c.id
            where meta.category = 3
            """));

    /// <summary>
    /// The right operand of a parenthesized join group: its own leftmost slot
    /// stays deferred (the group materializes as a unit) while the group's
    /// second slot is a derived table that materializes.
    /// </summary>
    [TestMethod]
    public void DerivedTable_InsideParenthesizedJoinGroup_SumsPerLeftRow()
        => AreEqual(134, WithSales().ExecuteScalar("""
            select sum(agg.revenue) from customers c
            left join (lines l
                       join (select customer_id, sum(amount) as revenue from lines group by customer_id) agg
                           on agg.customer_id = l.customer_id)
                on l.customer_id = c.id and l.id = (select min(id) from lines where customer_id = c.id)
            where c.category = 3
            """));

    // ---- APPLY keeps its laterality --------------------------------------

    /// <summary>
    /// CROSS APPLY's body reads the left row, so it must still run per left row:
    /// each customer's count of its own lines, weighted by id, is
    /// 1×2 + 2×2 + 3×1 + 4×1 rather than four copies of one row's answer.
    /// </summary>
    [TestMethod]
    public void CrossApply_StillExecutesPerLeftRow()
        => AreEqual(13, WithSales().ExecuteScalar("""
            select sum(c.id * a.n) from customers c
            cross apply (select count(*) as n from lines l where l.customer_id = c.id) a
            """));

    /// <summary>
    /// OUTER APPLY NULL-fills the row whose correlated body found nothing — and
    /// the body must still see each left row for the miss to be per-row.
    /// Customer 3 is the only one whose lines are all at or below 10, so it is
    /// the only id the NULL-filled rows sum to.
    /// </summary>
    [TestMethod]
    public void OuterApply_StillNullFillsPerLeftRow()
        => AreEqual(3, WithSales().ExecuteScalar("""
            select sum(c.id) from customers c
            outer apply (select l.id from lines l where l.customer_id = c.id and l.amount > 10) a
            where a.id is null
            """));

    /// <summary>
    /// A rowset function's arguments bind in a scope holding none of the FROM's
    /// own sources — only <c>APPLY</c> grants laterality — so naming a sibling
    /// is Msg 4104, probed against SQL Server 2025 (2026-08-05, probe N1.01).
    /// That refusal is what lets the materialization below take a generator
    /// source at all: post-4104 its arguments provably read no sibling.
    /// </summary>
    [TestMethod]
    public void CorrelatedRowsetFunction_UnderInnerJoin_IsMsg4104()
    {
        var ex = WithSales().AssertSqlError("""
            select count(*) from (select 1 as id, 'a,b' as csv union all select 2, 'c') t
            join string_split(t.csv, ',') s on 1 = 1
            """, 4104);
        Contains("t.csv", ex.Message);
    }

    /// <summary>The comma-join spelling of the same shape (probe N1.03).</summary>
    [TestMethod]
    public void CorrelatedRowsetFunction_UnderCommaJoin_IsMsg4104()
    {
        _ = WithSales().AssertSqlError("""
            select count(*) from (select 1 as id, 'a,b' as csv union all select 2, 'c') t,
                 string_split(t.csv, ',') s
            """, 4104);
    }

    /// <summary>
    /// The same shape written with <c>CROSS APPLY</c> — the form real accepts —
    /// still reads each left row's own CSV, so the refusal above is about the
    /// join form and not about the correlation (probe N1.04).
    /// </summary>
    [TestMethod]
    public void CorrelatedRowsetFunction_UnderCrossApply_ReadsEachLeftRow()
        => AreEqual(3, WithSales().ExecuteScalar("""
            select count(*) from (select 1 as id, 'a,b' as csv union all select 2, 'c') t
            cross apply string_split(t.csv, ',') s
            """));

    // ---- the enclosing statement's row -----------------------------------

    /// <summary>
    /// A derived table may read an <em>enclosing</em> statement's row, and that
    /// row is fixed only for one execution of the inner plan — so the enclosing
    /// query re-executing the plan per row must re-materialize it. Each outer id
    /// picks up its own line total (10 + 20 + 30), not three copies of the first.
    /// </summary>
    [TestMethod]
    public void OuterCorrelatedDerivedTable_ReMaterializesPerEnclosingRow()
        => AreEqual(60, new Simulation().ExecuteScalar("""
            create table ids (id int not null primary key);
            create table amounts (id int not null primary key, v int not null);
            create table anchor (x int not null);
            insert ids values (1), (2), (3);
            insert amounts values (1, 10), (2, 20), (3, 30);
            insert anchor values (1);
            select sum(x.s) from (
                select (select sum(d.v) from anchor a
                        join (select v from amounts where amounts.id = o.id) d on 1 = 1) as s
                from ids o) x
            """));

    /// <summary>
    /// The materialized rows belong to one execution, never to the plan: the
    /// same command text run twice over a shared connection (the plan-cache
    /// replay path) must see the row inserted in between.
    /// </summary>
    [TestMethod]
    public void RepeatedExecution_SeesRowsWrittenBetweenRuns()
    {
        var sim = WithSales();
        using var connection = sim.CreateOpenConnection();
        const string query = """
            select sum(agg.revenue) from customers c
            join (select customer_id, sum(amount) as revenue from lines group by customer_id) agg
                on agg.customer_id = c.id
            where c.category = 3
            """;
        using var command = connection.CreateCommand(query);
        AreEqual(134, command.ExecuteScalar());
        using (var insert = connection.CreateCommand("insert lines values (7, 3, 1000)"))
            _ = insert.ExecuteNonQuery();
        AreEqual(1134, command.ExecuteScalar());
    }

    // ---- the per-call-varying built-in declines --------------------------

    /// <summary>
    /// Probe-confirmed against SQL Server 2025: a one-row
    /// <c>(SELECT TOP 1 NEWID() …)</c> joined to a ten-row left side yields ten
    /// distinct values there, so the draw declines the reuse and the plan keeps
    /// running per left row.
    /// </summary>
    [TestMethod]
    public void NewIdInDerivedTable_KeepsDrawingPerLeftRow()
        => AreEqual(10, WithTenRows().ExecuteScalar("""
            select count(distinct d.g) from ten o
            join (select top 1 1 as grp, newid() as g from ten) d on d.grp = o.grp
            """));

    /// <summary>The same draw inside a CTE body declines identically.</summary>
    [TestMethod]
    public void NewIdInCteBody_KeepsDrawingPerLeftRow()
        => AreEqual(10, WithTenRows().ExecuteScalar("""
            with d as (select top 1 1 as grp, newid() as g from ten)
            select count(distinct d.g) from ten o join d on d.grp = o.grp
            """));

    /// <summary>
    /// The CROSS JOIN spelling of the same shape — no ON predicate to hash on,
    /// so this pins the nested-loop path's own gate.
    /// </summary>
    [TestMethod]
    public void NewIdInDerivedTable_UnderCrossJoin_KeepsDrawingPerLeftRow()
        => AreEqual(10, WithTenRows().ExecuteScalar(
            "select count(distinct d.g) from ten o cross join (select top 1 newid() as g from ten) d"));

    /// <summary>
    /// <c>RAND()</c> needs no gate — both engines freeze it for the statement,
    /// so one value reaches every row whether the plan ran once or ten times.
    /// </summary>
    [TestMethod]
    public void RandInDerivedTable_ReadsOneValueForTheStatement()
        => AreEqual(1, WithTenRows().ExecuteScalar(
            "select count(distinct d.r) from ten o cross join (select top 1 rand() as r from ten) d"));

    /// <summary>
    /// A derived table under CROSS APPLY draws per left row with or without the
    /// gate — the control that the gate isn't what makes APPLY lateral.
    /// </summary>
    [TestMethod]
    public void NewIdUnderCrossApply_KeepsDrawingPerLeftRow()
        => AreEqual(10, WithTenRows().ExecuteScalar(
            "select count(distinct a.g) from ten o cross apply (select top 1 newid() as g from ten) a"));

    private static Simulation WithTenRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table ten (id int not null primary key, grp int not null);
            insert ten values (1, 1), (2, 1), (3, 1), (4, 1), (5, 1),
                              (6, 1), (7, 1), (8, 1), (9, 1), (10, 1);
            """);
        return sim;
    }
}
