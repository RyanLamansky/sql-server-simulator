using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The rows a query keeps when a filter — or a join's own key set — reaches
/// <em>below</em> a <c>GROUP BY</c> body: a conjunct on a grouping column moves
/// into the body's WHERE, and a body equi-joined on a grouping column is reduced
/// to the keys its partner carries. Both rewrites are result-transparent, so
/// every test here pins the rows the shape answered before them, and the ones
/// the rewrites could plausibly change were probed against SQL Server 2025 first
/// (an outer join on either side, a preserved body, HAVING, NULL partner keys,
/// the joined UPDATE / DELETE, ROLLUP). The strategy each shape resolves to is
/// asserted in <c>SqlServerSimulator.Tests.Internal</c>'s
/// <c>GroupedBodyPushdownStrategyTests</c>.
/// </summary>
[TestClass]
public sealed class GroupedBodyReductionTests
{
    /// <summary>
    /// Four customers in two categories and six orders — one with no customer
    /// (a NULL join key), one naming a customer that doesn't exist (the body row
    /// no partner key keeps), and one customer with no orders at all.
    /// </summary>
    private static Simulation Sales()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table cust (cid int not null primary key, cat int not null, nm varchar(10) not null);
            create table ord (oid int not null primary key, cid int null, total int not null);
            create index ix_ord_cid on ord (cid);
            create index ix_cust_cat on cust (cat);
            insert cust values (10, 1, 'a'), (20, 1, 'b'), (30, 2, 'c'), (40, 2, 'd');
            insert ord values (1, 10, 100), (2, 10, 200), (3, 20, 300), (4, 30, 400), (5, null, 500), (6, 50, 600)
            """);
        return sim;
    }

    /// <summary>Every row of a result set, each rendered as its pipe-joined values.</summary>
    private static List<string> Rows(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var rows = new List<string>();
        foreach (var record in reader.EnumerateRecords())
        {
            var values = new object[record.FieldCount];
            _ = record.GetValues(values);
            rows.Add(string.Join("|", values));
        }

        return rows;
    }

    /// <summary>The grouped body every join test reads through.</summary>
    private const string Body = "(select cid, sum(total) as s from ord group by cid)";

    // ---- a conjunct on a grouping column --------------------------------------

    /// <summary>
    /// The shape the pushdown opens up: a filter written above a grouped body
    /// names one of its grouping columns, so it can move below the grouping.
    /// The group's aggregates are the ones the unfiltered body reports.
    /// </summary>
    [TestMethod]
    public void GroupedBody_FilteredOnItsGroupingColumn_AnswersThatGroupsRow() =>
        CollectionAssert.AreEqual((string[])["10|300|2"], Rows(
            Sales(), "select * from (select cid, sum(total) s, count(*) n from ord group by cid) d where d.cid = 10"));

    /// <summary>A renamed grouping column maps through the body's projection.</summary>
    [TestMethod]
    public void GroupedBody_FilteredOnARenamedGroupingColumn_AnswersThatGroupsRow() =>
        CollectionAssert.AreEqual((string[])["20|300"], Rows(
            Sales(), "select k, s from (select cid as k, sum(total) s from ord group by cid) d where k = 20"));

    /// <summary>
    /// A range on the grouping column keeps every group in it — and no group
    /// gains rows from one the filter removed.
    /// </summary>
    [TestMethod]
    public void GroupedBody_FilteredByARangeOnItsGroupingColumn_KeepsEveryGroupInRange() =>
        CollectionAssert.AreEqual((string[])["10|300", "20|300"], Rows(
            Sales(), $"select cid, s from {Body} d where d.cid between 10 and 20 order by cid"));

    /// <summary>
    /// HAVING decides per group, so it commutes with a filter that removes whole
    /// groups: the surviving group is the one the unfiltered body also reports.
    /// </summary>
    [TestMethod]
    public void GroupedBodyWithHaving_FilteredOnItsGroupingColumn_AnswersTheSurvivingGroup()
    {
        var sim = Sales();
        CollectionAssert.AreEqual((string[])["30|400"], Rows(
            sim, "select cid, s from (select cid, sum(total) s from ord group by cid having sum(total) > 350) d where d.cid = 30"));
        CollectionAssert.AreEqual((string[])[], Rows(
            sim, "select cid, s from (select cid, sum(total) s from ord group by cid having sum(total) > 350) d where d.cid = 10"));
    }

    /// <summary>
    /// The body's own WHERE still decides first: the pushed conjunct appends
    /// after it, so a row the body excluded stays excluded.
    /// </summary>
    [TestMethod]
    public void GroupedBodyWithItsOwnWhere_TakesThePushedConjunctAfterIt() =>
        CollectionAssert.AreEqual((string[])["10|200"], Rows(
            Sales(), $"select cid, s from (select cid, sum(total) s from ord where total > 150 group by cid) d where d.cid = 10"));

    /// <summary>
    /// A grouped <em>view</em> body takes the conjunct too — it travels as a
    /// template into the body parse the reference triggers.
    /// </summary>
    [TestMethod]
    public void GroupedViewBody_FilteredOnItsGroupingColumn_AnswersThatGroupsRow()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vg as select cid, sum(total) as s from ord group by cid");
        CollectionAssert.AreEqual((string[])["10|300"], Rows(sim, "select cid, s from vg where cid = 10"));
    }

    /// <summary>
    /// A grouping <em>expression</em> is not a grouping column: the filter names
    /// the expression's value, which no row of the body's input carries, so the
    /// push declines and the answer is the one the body always gave.
    /// </summary>
    [TestMethod]
    public void GroupedBody_GroupedByAnExpression_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["1|300"], Rows(
            Sales(), "select g, s from (select cid / 10 as g, sum(total) s from ord group by cid / 10) d where d.g = 1"));

    /// <summary>An aggregate output column isn't pushable either, and answers unchanged.</summary>
    [TestMethod]
    public void GroupedBody_FilteredOnItsAggregate_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["20|300", "10|300"], Rows(
            Sales(), $"select cid, s from {Body} d where d.s = 300 order by cid desc"));

    // ---- the join's key set reaches the body ----------------------------------

    /// <summary>
    /// The motivating shape: the filter names the <em>other</em> side, and the
    /// body is reduced to the one key the equi-join can use.
    /// </summary>
    [TestMethod]
    public void PointJoinAgainstAGroupedBody_AnswersTheJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["10|300"], Rows(
            Sales(), $"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid = 10"));

    /// <summary>An IN list on the partner reduces the body to those keys.</summary>
    [TestMethod]
    public void InListJoinAgainstAGroupedBody_AnswersEveryJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["10|300", "30|400"], Rows(
            Sales(), $"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid in (10, 30, 40) order by c.cid"));

    /// <summary>A range-narrowed partner does too.</summary>
    [TestMethod]
    public void RangeNarrowedPartnerJoinedToAGroupedBody_AnswersEveryJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["10|300", "20|300"], Rows(
            Sales(), $"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid <= 20 order by c.cid"));

    /// <summary>A partner narrowed on a non-key column — the workload's report shape.</summary>
    [TestMethod]
    public void PartnerNarrowedOnACategory_JoinedToAGroupedBody_AnswersEveryJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["10|300", "20|300"], Rows(
            Sales(), $"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cat = 1 order by c.cid"));

    /// <summary>The body written first is reduced the same way.</summary>
    [TestMethod]
    public void GroupedBodyNamedFirst_AnswersTheJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["10|300"], Rows(
            Sales(), $"select c.cid, d.s from {Body} d join cust c on c.cid = d.cid where c.cid = 10"));

    /// <summary>The CTE spelling of the same join.</summary>
    [TestMethod]
    public void CteGroupedBody_JoinedToANarrowedPartner_AnswersTheJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["10|300"], Rows(Sales(), """
            with agg as (select cid, sum(total) as s from ord group by cid)
            select c.cid, agg.s from cust c join agg on agg.cid = c.cid where c.cid = 10
            """));

    /// <summary>The comma-FROM spelling, whose join predicate lives in the WHERE.</summary>
    [TestMethod]
    public void CommaJoinAgainstAGroupedBody_AnswersTheJoinedGroup() =>
        CollectionAssert.AreEqual((string[])["20|300"], Rows(
            Sales(), $"select c.cid, d.s from cust c, {Body} d where d.cid = c.cid and c.cid = 20"));

    /// <summary>
    /// A grouped view joined to a narrowed partner: the reduction's key set
    /// crosses into the view body exactly as a written conjunct does.
    /// </summary>
    [TestMethod]
    public void GroupedViewBody_JoinedToANarrowedPartner_AnswersTheJoinedGroup()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vg as select cid, sum(total) as s from ord group by cid");
        CollectionAssert.AreEqual((string[])["10|300"], Rows(
            sim, "select c.cid, vg.s from cust c join vg on vg.cid = c.cid where c.cid = 10"));
    }

    /// <summary>
    /// A conjunct on the body's own grouping column and a reduction from the
    /// join's partner reach the same body together.
    /// </summary>
    [TestMethod]
    public void GroupedBody_FilteredAndReducedAtOnce_AnswersTheOverlap() =>
        CollectionAssert.AreEqual((string[])["20|300"], Rows(
            Sales(), $"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where d.cid >= 20 and c.cat = 1 order by c.cid"));

    /// <summary>
    /// NULL never equi-joins, so a NULL partner key is simply not in the set —
    /// and the row carrying it drops out of the join as it always did
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void NullPartnerKey_JoinedToAGroupedBody_KeepsOnlyTheMatchedRow() =>
        CollectionAssert.AreEqual((string[])["10|300"], Rows(
            Sales(), $"select o.cid, d.s from ord o join {Body} d on d.cid = o.cid where o.oid in (1, 5) order by o.oid"));

    /// <summary>
    /// A ROLLUP body's subtotal row carries NULL in the grouping column, which
    /// the equi-join excludes either way (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void RollupGroupedBody_JoinedToANarrowedPartner_AnswersTheLeafGroup() =>
        CollectionAssert.AreEqual((string[])["10|300"], Rows(
            Sales(), "select c.cid, d.s from cust c join (select cid, sum(total) s from ord group by rollup(cid)) d on d.cid = c.cid where c.cid = 10"));

    /// <summary>
    /// Past the key cap the reduction declines silently, and the join answers
    /// exactly what it answered before — 1,100 partner rows, no reduction.
    /// </summary>
    [TestMethod]
    public void PartnerWiderThanTheKeyCap_AnswersTheSameRows()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("""
            create table wide (wid int not null primary key, cid int not null);
            with n as (select 1 as i union all select i + 1 from n where i < 1100)
            insert wide (wid, cid) select i, case when i <= 4 then i * 10 else i + 1000 end from n option (maxrecursion 2000)
            """);
        CollectionAssert.AreEqual((string[])["10|300", "20|300", "30|400"], Rows(
            sim, $"select w.cid, d.s from wide w join {Body} d on d.cid = w.cid order by w.cid"));
    }

    // ---- a preserved body is never reduced ------------------------------------

    /// <summary>
    /// The body on the NULL-supplied side of a LEFT JOIN is reducible: every
    /// partner row still reports, NULL-extended where the body has no group
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void BodyOnTheNullSuppliedSideOfALeftJoin_ReportsEveryPartnerRow() =>
        CollectionAssert.AreEqual((string[])["10|300", "20|300", "30|400", "40|"], Rows(
            Sales(), $"select c.cid, d.s from cust c left join {Body} d on d.cid = c.cid order by c.cid"));

    /// <summary>
    /// The body <em>preserved</em> by a LEFT JOIN keeps the groups no partner
    /// key names — the row for the deleted customer 50 and the NULL-keyed group
    /// (probe-confirmed). Reducing here would delete rows real returns.
    /// </summary>
    [TestMethod]
    public void BodyPreservedByALeftJoin_KeepsTheGroupsNoPartnerKeyNames() =>
        CollectionAssert.AreEqual((string[])["|500", "10|300", "20|300", "50|600"], Rows(
            Sales(), $"select d.cid, d.s from {Body} d left join cust c on c.cid = d.cid where c.cat = 1 or c.cat is null order by d.cid"));

    /// <summary>The RIGHT JOIN mirror: the body is the preserved side (probe-confirmed).</summary>
    [TestMethod]
    public void BodyPreservedByARightJoin_KeepsEveryGroup() =>
        CollectionAssert.AreEqual((string[])["|500", "10|300", "20|300", "|400", "|600"], Rows(
            Sales(), $"select c.cid, d.s from cust c right join {Body} d on d.cid = c.cid and c.cat = 1 order by d.cid"));

    /// <summary>FULL preserves both sides, so it declines outright (probe-confirmed).</summary>
    [TestMethod]
    public void BodyInAFullJoin_KeepsEveryGroup() =>
        CollectionAssert.AreEqual((string[])["|500", "40|", "30|400", "|600"], Rows(
            Sales(), $"select c.cid, d.s from cust c full join {Body} d on d.cid = c.cid where c.cat = 2 or c.cid is null order by d.cid, c.cid"));

    /// <summary>
    /// An <c>OUTER APPLY</c> body re-executes per outer row rather than reading
    /// a fixed rowset, so it is left alone and answers as it did.
    /// </summary>
    [TestMethod]
    public void GroupedBodyUnderOuterApply_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["10|300", "20|300", "30|400", "40|"], Rows(
            Sales(), """
                select c.cid, d.s from cust c
                outer apply (select sum(total) as s from ord o where o.cid = c.cid group by o.cid) d
                order by c.cid
                """));

    // ---- the joined UPDATE / DELETE take the same pass ------------------------

    /// <summary>
    /// The joined-UPDATE route reaches the reduction through the same seam: the
    /// target is the partner here, read for its keys before the enumeration —
    /// where a joined mutation reads the pre-statement rows anyway
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void JoinedUpdateFromAGroupedBody_WritesEachTargetRowsGroup()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("select cid, cast(0 as int) as rev into tgt from cust where cat = 1");
        _ = sim.ExecuteNonQuery($"update t set rev = d.s from tgt t join {Body} d on d.cid = t.cid");
        CollectionAssert.AreEqual((string[])["10|300", "20|300"], Rows(sim, "select cid, rev from tgt order by cid"));
    }

    /// <summary>The DELETE counterpart, whose WHERE reads the body's aggregate.</summary>
    [TestMethod]
    public void JoinedDeleteAgainstAGroupedBody_DeletesTheMatchedRows()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("select cid, cast(0 as int) as rev into tgt from cust");
        _ = sim.ExecuteNonQuery($"delete t from tgt t join {Body} d on d.cid = t.cid where d.s > 350");
        CollectionAssert.AreEqual((string[])["10|0", "20|0", "40|0"], Rows(sim, "select cid, rev from tgt order by cid"));
    }

    /// <summary>
    /// A joined UPDATE whose target has no rows still commits nothing, and the
    /// body's own reduction changes none of that.
    /// </summary>
    [TestMethod]
    public void JoinedUpdateWithAnEmptyTarget_WritesNothing()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("select cid, cast(0 as int) as rev into tgt from cust where cat = 99");
        AreEqual(0, sim.ExecuteNonQuery($"update t set rev = d.s from tgt t join {Body} d on d.cid = t.cid"));
    }

    /// <summary>
    /// A skipped branch's joined UPDATE reaches no pass at all — the reduction
    /// sits behind the same skip-mode guard the materialization does.
    /// </summary>
    [TestMethod]
    public void SkippedBranchJoinedUpdate_RaisesNothing()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("select cid, cast(0 as int) as rev into tgt from cust where cat = 99");
        _ = sim.ExecuteNonQuery($"if 1 = 0 begin update t set rev = d.s from tgt t join {Body} d on d.cid = t.cid end");
    }

    // ---- what the reduction must not change -----------------------------------

    /// <summary>
    /// A body drawing <c>NEWID()</c> keeps its per-outer-row execution whether
    /// or not it was reduced — the volatility gate is sampled around the
    /// materializing execution, and a reduced body is still that body.
    /// </summary>
    [TestMethod]
    public void ReducedBodyDrawingNewid_StillDrawsPerOuterRow()
    {
        var rows = Rows(Sales(), $"""
            select c.cid, cast(d.g as varchar(40)) from cust c
            join (select cid, sum(total) as s, max(newid()) as g from ord group by cid) d on d.cid = c.cid
            where c.cat = 1 order by c.cid
            """);
        HasCount(2, rows);
        AreNotEqual(rows[0].Split('|')[1], rows[1].Split('|')[1]);
    }

    /// <summary>
    /// The reduction is per execution, not per plan: the same cached plan run
    /// twice with different parameter values reduces to each run's own key set.
    /// </summary>
    [TestMethod]
    public void CachedPlanRunTwice_ReducesToEachExecutionsKeys()
    {
        var sim = Sales();
        using var connection = sim.CreateOpenConnection();
        var answers = new List<string>();
        foreach (var customer in (int[])[10, 30, 10])
        {
            using var command = connection.CreateCommand(
                $"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid = @c", ("@c", customer));
            using var reader = command.ExecuteReader();
            foreach (var record in reader.EnumerateRecords())
                answers.Add($"{record.GetInt32(0)}|{record.GetInt32(1)}");
        }

        CollectionAssert.AreEqual((string[])["10|300", "30|400", "10|300"], answers);
    }

    /// <summary>
    /// The join's own multiplicity is untouched: a partner carrying a key twice
    /// still reports the body's group twice.
    /// </summary>
    [TestMethod]
    public void PartnerRepeatingAKey_ReportsTheGroupOncePerPartnerRow() =>
        CollectionAssert.AreEqual((string[])["1|300", "2|300"], Rows(
            Sales(), $"select o.oid, d.s from ord o join {Body} d on d.cid = o.cid where o.cid = 10 order by o.oid"));
}
