using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the WHERE pushdown into a view / derived-table body
/// (<c>Selection.PushWhereIntoDeferredSources</c>): a filter written above a
/// body reaches the base scan underneath it, so the seek there can use it, and a
/// chain of bodies carries it all the way down. The push is result-transparent —
/// <c>Tests</c>' <c>ViewPredicatePushdownTests</c> pins the rows — so a silent
/// revert would show up only as a workload crawling, which is what the
/// motivating measurement was (a five-deep view chain filtered on a key: 177 ms
/// against the live server's 11 ms). Reads the opt-in
/// <see cref="IndexSeekDiagnostics"/> trace rather than timing, so the guard is
/// exact and non-flaky.
/// </summary>
[TestClass]
public sealed class ViewPredicatePushdownStrategyTests
{
    private const string Setup = """
        create table ord (ord_id int not null primary key, cust_id int not null, region_id int not null, total int not null);
        create index ix_ord_cust on ord (cust_id);
        create table cust (cust_id int not null primary key, cust_name varchar(20) not null);
        insert ord values (1, 10, 1, 100), (2, 10, 2, 200), (3, 20, 1, 300), (4, 30, 2, 400);
        insert cust values (10, 'a'), (20, 'b'), (30, 'c');
        create view v1 as select ord_id, cust_id, region_id, total from ord;
        """;

    /// <summary>The seek / scan decisions a query reached, over <see cref="Setup"/> plus <paramref name="extraSetup"/>.</summary>
    private static List<string> SeekTrace(string query, string extraSetup = "")
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        RunScript(connection, Setup);
        RunScript(connection, extraSetup);
        IndexSeekDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // Drain: the row source is lazy, so the decisions land as rows flow.
            }

            return IndexSeekDiagnostics.Sink;
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    /// <summary>
    /// Runs a <c>;</c>-separated script one statement per batch — <c>CREATE
    /// VIEW</c> has to be the first statement of its own.
    /// </summary>
    private static void RunScript(System.Data.Common.DbConnection connection, string script)
    {
        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            _ = command.ExecuteNonQuery();
        }
    }

    // ---- the push reaches the base scan --------------------------------------

    /// <summary>A filter above a one-level view seeks the base table's index.</summary>
    [TestMethod]
    public void ViewFilteredOnAnIndexedColumn_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace("select ord_id from v1 where cust_id = 10"));

    /// <summary>
    /// The conjunct arrives either way: filtered on a column no index leads, the
    /// body's scan is <em>attempted</em> as a seek and declines, which is the
    /// <c>Scan</c> entry — and with no push at all there is no entry, since the
    /// body carries no excluder for the seek to consider.
    /// </summary>
    [TestMethod]
    public void ViewFilteredOnAnUnindexedColumn_ScansTheBaseTable()
    {
        var trace = SeekTrace("select ord_id from v1 where region_id = 1");
        Contains("Scan(ord)", trace);
        DoesNotContain("Seek(ord)", trace);
    }

    /// <summary>The motivating shape: five nested views, the filter reaching the base scan through all of them.</summary>
    [TestMethod]
    public void FiveDeepViewChain_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace(
            "select count(*) from v5 where cust_id = 10",
            """
            create view v2 as select ord_id, cust_id, region_id, total from v1 where total > 0;
            create view v3 as select ord_id, cust_id, region_id, total from v2 where region_id > 0;
            create view v4 as select ord_id, cust_id, region_id, total from v3 where ord_id > 0;
            create view v5 as select ord_id, cust_id, region_id, total from v4 where total > 1;
            """));

    /// <summary>The derived-table spelling of the same chain pushes identically.</summary>
    [TestMethod]
    public void FiveDeepDerivedTableChain_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace("""
            select count(*) from (
              select * from (
                select * from (
                  select * from (
                    select ord_id, cust_id, region_id, total from ord
                  ) d1 where total > 0
                ) d2 where region_id > 0
              ) d3 where ord_id > 0
            ) d4 where total > 1 and cust_id = 10
            """));

    /// <summary>A renamed body column carries the push: the outer name maps to the body's own by ordinal.</summary>
    [TestMethod]
    public void RenamedViewColumn_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace(
            "select o from vr where c = 10",
            "create view vr as select ord_id as o, cust_id as c from ord"));

    /// <summary>A range bound pushes the same way an equality does.</summary>
    [TestMethod]
    public void RangeBoundAboveAView_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace("select ord_id from v1 where cust_id > 25"));

    /// <summary>An IN list pushes as the equality family it decomposes into.</summary>
    [TestMethod]
    public void InListAboveAView_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace("select ord_id from v1 where cust_id in (10, 20)"));

    /// <summary>A parameter's value is evaluated once at the push and crosses into the body's own batch as a constant.</summary>
    [TestMethod]
    public void VariableComparandAboveAView_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace("declare @c int = 10; select ord_id from v1 where cust_id = @c"));

    /// <summary>A CTE body is a query body like any other.</summary>
    [TestMethod]
    public void CteBody_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace(
            "with c as (select ord_id, cust_id from ord) select ord_id from c where cust_id = 10"));

    /// <summary>A view joined to a table pushes into the view and keeps the join.</summary>
    [TestMethod]
    public void ViewJoinedToATable_SeeksTheBaseTableBehindTheView() =>
        Contains("Seek(ord)", SeekTrace(
            "select v1.ord_id from cust join v1 on v1.cust_id = cust.cust_id where v1.cust_id = 10"));

    // ---- the declines --------------------------------------------------------

    /// <summary>A body carrying its own row limit sees a different row set once filtered, so it declines.</summary>
    [TestMethod]
    public void TopBody_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select ord_id from vt where cust_id = 10",
            "create view vt as select top 3 ord_id, cust_id from ord order by ord_id"));

    /// <summary>DISTINCT reads the row set as a whole, so it declines.</summary>
    [TestMethod]
    public void DistinctBody_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select cust_id from vd where cust_id = 10",
            "create view vd as select distinct cust_id from ord"));

    /// <summary>
    /// A grouped body takes a conjunct on a column it <em>groups by</em>: the
    /// filter removes whole groups, so it moves below the grouping and reaches
    /// the base seek. <c>GroupedBodyPushdownStrategyTests</c> owns the rest of
    /// the grouped shapes (the expression-grouping decline included).
    /// </summary>
    [TestMethod]
    public void GroupedBodyFilteredOnItsGroupingColumn_SeeksTheBaseTable() =>
        Contains("Seek(ord)", SeekTrace(
            "select cust_id from vg where cust_id = 10",
            "create view vg as select cust_id, count(*) as n from ord group by cust_id"));

    /// <summary>A windowed body declines: the window spans the rows the body produced.</summary>
    [TestMethod]
    public void WindowedBody_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select ord_id from vw where cust_id = 10",
            "create view vw as select ord_id, cust_id, row_number() over (order by ord_id) as rn from ord"));

    /// <summary>A set-operation body declines — the branch plans carry no pushdown at all.</summary>
    [TestMethod]
    public void SetOperationBody_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select ord_id from vu where cust_id = 10",
            "create view vu as select ord_id, cust_id from ord where total > 100 union all select ord_id, cust_id from ord where total <= 100"));

    /// <summary>An output column the body computes can't be written as a filter over the body's row.</summary>
    [TestMethod]
    public void ExpressionProjection_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select ord_id from vx where cust_id = 10",
            "create view vx as select ord_id, cust_id + 0 as cust_id from ord"));

    /// <summary>A conjunct over an expression of the column — not a bare reference — declines.</summary>
    [TestMethod]
    public void ExpressionOverTheColumn_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace("select ord_id from v1 where cust_id + 0 = 10"));

    /// <summary><c>IS NULL</c> isn't NULL-rejecting, so it stays where it was written.</summary>
    [TestMethod]
    public void IsNullConjunct_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select ord_id from cust left join v1 on v1.cust_id = cust.cust_id where v1.cust_id is null"));

    /// <summary>A conjunct reading a sibling source's column isn't in the body's scope.</summary>
    [TestMethod]
    public void SiblingColumnConjunct_KeepsItsScan() =>
        DoesNotContain("Seek(ord)", SeekTrace(
            "select v1.ord_id from cust join v1 on v1.ord_id = cust.cust_id where v1.cust_id = cust.cust_id"));

    /// <summary>The pushed filter lands in the body's WHERE, so the body's own conjuncts still decide first.</summary>
    [TestMethod]
    public void BodyWithItsOwnWhere_StillSeeks() =>
        Contains("Seek(ord)", SeekTrace(
            "select ord_id from vf where cust_id = 10",
            "create view vf as select ord_id, cust_id from ord where total > 150"));
}
