using System.Data.Common;
using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the two rewrites that reach below a <c>GROUP BY</c>
/// body: a conjunct on a grouping column moving into the body's WHERE
/// (<c>Selection.PushWhereIntoDeferredSources</c>), and a body equi-joined on a
/// grouping column being reduced to its partner's key set
/// (<c>Selection.ReduceGroupedBodiesByJoinKeys</c>). Both are
/// result-transparent — <c>Tests</c>' <c>GroupedBodyReductionTests</c> pins the
/// rows — so a silent revert would show up only as a workload crawling, which is
/// what the motivating measurement was (a point join against a 663-group
/// aggregate over 228k rows: 156 ms against the live server's 4 ms). Reads the
/// opt-in <see cref="JoinDiagnostics"/> / <see cref="IndexSeekDiagnostics"/>
/// traces rather than timing, so the guard is exact and non-flaky.
/// </summary>
[TestClass]
public sealed class GroupedBodyPushdownStrategyTests
{
    private const string Setup = """
        create table cust (cid int not null primary key, cat int not null, nm varchar(10) not null);
        create table ord (oid int not null primary key, cid int null, total int not null);
        create index ix_ord_cid on ord (cid);
        create index ix_cust_cat on cust (cat);
        insert cust values (10, 1, 'a'), (20, 1, 'b'), (30, 2, 'c'), (40, 2, 'd');
        insert ord values (1, 10, 100), (2, 10, 200), (3, 20, 300), (4, 30, 400), (5, null, 500), (6, 50, 600);
        """;

    /// <summary>The grouped body every join test reads through.</summary>
    private const string Body = "(select cid, sum(total) as s from ord group by cid)";

    /// <summary>
    /// The join / seek decisions a statement reached, over <see cref="Setup"/>
    /// plus <paramref name="extraSetup"/>. Result sets are drained, so a lazy row
    /// source's decisions land before the trace is read.
    /// </summary>
    private static List<string> Trace(string statement, string extraSetup = "")
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        RunScript(connection, Setup);
        RunScript(connection, extraSetup);
        JoinDiagnostics.Sink = [];
        IndexSeekDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    // Drain: the row source is lazy, so the decisions land as rows flow.
                }
            }
            while (reader.NextResult());

            return [.. JoinDiagnostics.Sink, .. IndexSeekDiagnostics.Sink];
        }
        finally
        {
            JoinDiagnostics.Sink = null;
            IndexSeekDiagnostics.Sink = null;
        }
    }

    /// <summary>Runs a <c>;</c>-separated script one statement per batch — <c>CREATE VIEW</c> has to be the first statement of its own.</summary>
    private static void RunScript(DbConnection connection, string script)
    {
        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            _ = command.ExecuteNonQuery();
        }
    }

    /// <summary>Whether any trace entry names a key reduction of the source <c>d</c>.</summary>
    private static void AssertReduced(int keys, List<string> trace) => Contains($"KeyReduction(d,keys={keys})", trace);

    private static void AssertNotReduced(List<string> trace)
    {
        foreach (var entry in trace)
            IsFalse(entry.StartsWith("KeyReduction(", StringComparison.Ordinal), entry);
    }

    // ---- a conjunct on a grouping column reaches the base scan ----------------

    /// <summary>A filter on the body's grouping column seeks the base table underneath it.</summary>
    [TestMethod]
    public void GroupedBodyFilteredOnItsGroupingColumn_SeeksTheBaseTable() =>
        Contains("Seek(ord)", Trace($"select cid, s from {Body} d where d.cid = 10"));

    /// <summary>The renamed spelling maps through the body's projection and seeks all the same.</summary>
    [TestMethod]
    public void GroupedBodyFilteredOnARenamedGroupingColumn_SeeksTheBaseTable() =>
        Contains("Seek(ord)", Trace("select k, s from (select cid as k, sum(total) s from ord group by cid) d where k = 20"));

    /// <summary>A grouped view body takes the conjunct into its own parse.</summary>
    [TestMethod]
    public void GroupedViewBodyFilteredOnItsGroupingColumn_SeeksTheBaseTable() =>
        Contains("Seek(ord)", Trace(
            "select cid, s from vg where cid = 10",
            "create view vg as select cid, sum(total) as s from ord group by cid"));

    /// <summary>
    /// A grouping <em>expression</em> offers no column to filter on: the body
    /// keeps its scan.
    /// </summary>
    [TestMethod]
    public void BodyGroupedByAnExpression_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", Trace(
            "select g, s from (select cid / 10 as g, sum(total) s from ord group by cid / 10) d where d.g = 1"));

    /// <summary>An aggregate output column isn't a grouping column either.</summary>
    [TestMethod]
    public void BodyFilteredOnItsAggregate_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", Trace($"select cid, s from {Body} d where d.s = 300"));

    // ---- the join's key set reaches the body ---------------------------------

    /// <summary>The motivating shape: a point-filtered partner reduces the body to one key.</summary>
    [TestMethod]
    public void PointJoinAgainstAGroupedBody_ReducesItToOneKey()
    {
        var trace = Trace($"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid = 10");
        AssertReduced(1, trace);
        Contains("Seek(ord)", trace);
    }

    /// <summary>An IN list carries every key it names that the partner holds.</summary>
    [TestMethod]
    public void InListJoinAgainstAGroupedBody_ReducesItToThoseKeys() =>
        AssertReduced(3, Trace($"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid in (10, 30, 40)"));

    /// <summary>A range-narrowed partner reduces to the keys in range.</summary>
    [TestMethod]
    public void RangeNarrowedPartner_ReducesTheBodyToItsKeys() =>
        AssertReduced(2, Trace($"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cid <= 20"));

    /// <summary>A partner narrowed on a non-key indexed column — the workload's report shape.</summary>
    [TestMethod]
    public void PartnerNarrowedOnACategory_ReducesTheBodyToItsKeys() =>
        AssertReduced(2, Trace($"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid where c.cat = 1"));

    /// <summary>An unfiltered partner small enough to read still reduces — to every key it carries.</summary>
    [TestMethod]
    public void UnfilteredSmallPartner_ReducesTheBodyToItsWholeKeySet() =>
        AssertReduced(4, Trace($"select c.cid, d.s from cust c join {Body} d on d.cid = c.cid"));

    /// <summary>NULL never equi-joins, so a NULL partner key isn't in the set.</summary>
    [TestMethod]
    public void NullPartnerKeys_AreLeftOutOfTheKeySet() =>
        AssertReduced(1, Trace($"select o.cid, d.s from ord o join {Body} d on d.cid = o.cid where o.oid in (1, 5)"));

    /// <summary>The body written first is reduced the same way.</summary>
    [TestMethod]
    public void GroupedBodyNamedFirst_IsReduced() =>
        AssertReduced(1, Trace($"select c.cid, d.s from {Body} d join cust c on c.cid = d.cid where c.cid = 10"));

    /// <summary>The CTE spelling.</summary>
    [TestMethod]
    public void CteGroupedBody_IsReduced() =>
        AssertReduced(1, Trace("""
            with d as (select cid, sum(total) as s from ord group by cid)
            select c.cid, d.s from cust c join d on d.cid = c.cid where c.cid = 10
            """));

    /// <summary>The view spelling — the key set crosses into the body parse.</summary>
    [TestMethod]
    public void GroupedViewBody_IsReduced()
    {
        var trace = Trace(
            "select c.cid, d.s from cust c join vg d on d.cid = c.cid where c.cid = 10",
            "create view vg as select cid, sum(total) as s from ord group by cid");
        AssertReduced(1, trace);
        Contains("Seek(ord)", trace);
    }

    /// <summary>The comma-FROM spelling, whose join predicate lives in the WHERE.</summary>
    [TestMethod]
    public void CommaJoinAgainstAGroupedBody_IsReduced() =>
        AssertReduced(1, Trace($"select c.cid, d.s from cust c, {Body} d where d.cid = c.cid and c.cid = 20"));

    /// <summary>A body on the NULL-supplied side of a LEFT JOIN is reducible.</summary>
    [TestMethod]
    public void BodyOnTheNullSuppliedSideOfALeftJoin_IsReduced() =>
        AssertReduced(1, Trace($"select c.cid, d.s from cust c left join {Body} d on d.cid = c.cid where c.cid = 10"));

    // ---- and the shapes it must decline --------------------------------------

    /// <summary>A body the LEFT JOIN preserves keeps every group.</summary>
    [TestMethod]
    public void BodyPreservedByALeftJoin_IsNotReduced() =>
        AssertNotReduced(Trace($"select d.cid, d.s from {Body} d left join cust c on c.cid = d.cid where c.cat = 1"));

    /// <summary>The RIGHT JOIN mirror.</summary>
    [TestMethod]
    public void BodyPreservedByARightJoin_IsNotReduced() =>
        AssertNotReduced(Trace($"select c.cid, d.s from cust c right join {Body} d on d.cid = c.cid where c.cat = 1"));

    /// <summary>FULL preserves both sides.</summary>
    [TestMethod]
    public void BodyInAFullJoin_IsNotReduced() =>
        AssertNotReduced(Trace($"select c.cid, d.s from cust c full join {Body} d on d.cid = c.cid where c.cat = 1"));

    /// <summary>An APPLY right side re-executes per outer row rather than reading a fixed rowset.</summary>
    [TestMethod]
    public void GroupedBodyUnderCrossApply_IsNotReduced() =>
        AssertNotReduced(Trace(
            "select c.cid, d.s from cust c cross apply (select o.cid, sum(o.total) as s from ord o where o.cid = c.cid group by o.cid) d where c.cid = 10"));

    /// <summary>A plain project-filter body carries no grouping to reduce underneath.</summary>
    [TestMethod]
    public void UngroupedBody_IsNotReduced() =>
        AssertNotReduced(Trace(
            "select c.cid, d.total from cust c join (select cid, total from ord) d on d.cid = c.cid where c.cid = 10"));

    /// <summary>A partner that is itself a deferred body isn't a bounded read.</summary>
    [TestMethod]
    public void PartnerReadingThroughItsOwnBody_DoesNotReduce() =>
        AssertNotReduced(Trace(
            $"select p.cid, d.s from (select cid from cust where cat = 1) p join {Body} d on d.cid = p.cid"));

    /// <summary>
    /// A partner whose reads the extra probe would re-lock differently is left
    /// alone: a <c>HOLDLOCK</c> reader owes a phantom fence, and settling it
    /// early would change which key ranges the statement locks and when.
    /// </summary>
    [TestMethod]
    public void HoldlockPartner_DoesNotReduce() =>
        AssertNotReduced(Trace(
            $"select c.cid, d.s from cust c with (holdlock) join {Body} d on d.cid = c.cid where c.cid = 10"));

    /// <summary>Past the key cap the reduction declines silently.</summary>
    [TestMethod]
    public void PartnerWiderThanTheKeyCap_DoesNotReduce() =>
        AssertNotReduced(Trace(
            $"select w.cid, d.s from wide w join {Body} d on d.cid = w.cid",
            """
            create table wide (wid int not null primary key, cid int not null);
            with n as (select 1 as i union all select i + 1 from n where i < 1100)
            insert wide (wid, cid) select i, i from n option (maxrecursion 2000);
            """));

    // ---- the joined UPDATE / DELETE reach the same pass -----------------------

    /// <summary>The joined UPDATE routes through the same seam, with no DML-specific code.</summary>
    [TestMethod]
    public void JoinedUpdateFromAGroupedBody_ReducesIt() =>
        AssertReduced(2, Trace(
            $"update t set rev = d.s from tgt t join {Body} d on d.cid = t.cid",
            "select cid, cast(0 as int) as rev into tgt from cust where cat = 1;"));

    /// <summary>And the DELETE counterpart.</summary>
    [TestMethod]
    public void JoinedDeleteAgainstAGroupedBody_ReducesIt() =>
        AssertReduced(2, Trace(
            $"delete t from tgt t join {Body} d on d.cid = t.cid where d.s > 350",
            "select cid, cast(0 as int) as rev into tgt from cust where cat = 1;"));

    /// <summary>A skipped branch reaches no pass at all.</summary>
    [TestMethod]
    public void SkippedBranchJoinedUpdate_DoesNotReduce() =>
        AssertNotReduced(Trace(
            $"if 1 = 0 begin update t set rev = d.s from tgt t join {Body} d on d.cid = t.cid end",
            "select cid, cast(0 as int) as rev into tgt from cust where cat = 1;"));
}
