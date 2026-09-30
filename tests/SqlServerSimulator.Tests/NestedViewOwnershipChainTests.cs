using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A write through a view over a view checks the ownership chain one link at a
/// time, each object's owner compared with the next one's (probed 2026-09-29
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class NestedViewOwnershipChainTests
{
    // dbo.td is dbo-owned, s1.v2 sits in u1's schema, dbo.v1 reads s1.v2 and
    // the caller c is granted on v1 (plus whatever else the case adds).
    private static Simulation Nested(string v2Source, string grants)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user u1 without login",
            "create schema s1 authorization u1",
            "create table dbo.td (a int, b int); insert dbo.td values (1, 2); create table s1.t1 (a int, b int); insert s1.t1 values (1, 2)",
            $"create view s1.v2 as select a, b from {v2Source}",
            "create view dbo.v1 as select a, b from s1.v2");
        _ = sim.ExecuteNonQuery(grants);
        return sim;
    }

    [TestMethod]
    [DataRow("dbo.td", "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("dbo.td", "grant select, update on dbo.v1 to c; grant select, update on s1.v2 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'td'")]
    [DataRow("dbo.td", "grant insert on dbo.v1 to c", "insert dbo.v1 values (7, 8)", "The INSERT permission was denied on the object 'v2'")]
    [DataRow("dbo.td", "grant delete, select on dbo.v1 to c", "delete dbo.v1 where a = 1", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("dbo.td", "grant select, update, insert, delete on dbo.v1 to c", "merge dbo.v1 t using (select 1 a, 9 b) s on t.a = s.a when matched then update set b = s.b;", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("s1.t1", "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'v2'")]
    public void BrokenLink_NamesTheObjectWhoseOwnerDiffersFromItsPredecessor(string v2Source, string grants, string statement, string message)
    {
        var ex = Nested(v2Source, grants).AssertSqlError($"execute as user = 'c'; {statement}", 229);
        Contains(message, ex.Errors[0].Message);
    }

    [TestMethod]
    public void IntactLinkBelowABrokenOne_NeedsOnlyTheGrantOnTheMiddleView()
        => AreEqual(1, Nested("s1.t1", "grant select, update on dbo.v1 to c; grant select, update on s1.v2 to c")
            .ExecuteNonQuery("execute as user = 'c'; update dbo.v1 set b = 5 where a = 1"));

    /// <summary>
    /// A read through a view whose body reads another owner's table is refused
    /// with the statement, from <c>ExecuteReader</c>, not from the first
    /// <c>Read</c> after the result set opened; the view's own grant is judged
    /// first (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("grant select on dbo.v1 to c", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("grant select on dbo.v1 to c; grant select on s1.v2 to c", "The SELECT permission was denied on the object 'td'")]
    [DataRow("grant select on s1.v2 to c", "The SELECT permission was denied on the object 'v1'")]
    public void BrokenChainThroughAView_IsRefusedAtExecuteReader(string grants, string message)
    {
        var sim = Nested("dbo.td", grants);
        using var connection = sim.CreateOpenConnection();
        using (var setup = connection.CreateCommand("execute as user = 'c'"))
            _ = setup.ExecuteNonQuery();
        using var command = connection.CreateCommand("select a from dbo.v1");
        var ex = Throws<SimulatedSqlException>(() => command.ExecuteReader().Dispose());
        AreEqual(229, ex.Number);
        Contains(message, ex.Errors[0].Message);
    }

    // A join view above or below other views: t1 and t2 are dbo's, u1 owns s1,
    // and the caller c holds whatever the case grants.
    private static Simulation OverJoinView(string topDefinition, string grants)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user u1 without login",
            "create schema s1 authorization u1",
            "create table dbo.t1 (a int, b int); insert dbo.t1 values (1, 2); create table dbo.t2 (a int, c int); insert dbo.t2 values (1, 3); create table dbo.t3 (a int, d int); insert dbo.t3 values (1, 4)",
            "create view s1.j as select t1.a, t1.b, t2.c from dbo.t1 join dbo.t2 on t1.a = t2.a",
            topDefinition);
        _ = sim.ExecuteNonQuery(grants);
        return sim;
    }

    private const string ViewOverJoin = "create view dbo.v1 as select a, b from s1.j";
    private const string JoinOverJoin = "create view dbo.v1 as select j.a, j.b, t3.d from s1.j join dbo.t3 on j.a = t3.a";

    /// <summary>
    /// An UPDATE through a view over another owner's join view is refused at the
    /// join view, then at the join's tables — the last-bound one named — link by
    /// link, top down; a join view the write only reads takes the SELECT alone
    /// (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(ViewOverJoin, "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5", "The UPDATE permission was denied on the object 'j'")]
    [DataRow(ViewOverJoin, "grant update on dbo.v1 to c", "update dbo.v1 set b = 5", "The UPDATE permission was denied on the object 'j'")]
    [DataRow(ViewOverJoin, "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'j'")]
    [DataRow(ViewOverJoin, "grant select, update on dbo.v1 to c; grant select, update on s1.j to c", "update dbo.v1 set b = 5", "The SELECT permission was denied on the object 't2'")]
    [DataRow(ViewOverJoin, "grant select, update on dbo.v1 to c; grant select, update on s1.j to c; grant select, update on dbo.t1 to c", "update dbo.v1 set b = 5", "The SELECT permission was denied on the object 't2'")]
    [DataRow(JoinOverJoin, "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5", "The SELECT permission was denied on the object 'j'")]
    [DataRow(JoinOverJoin, "grant select, update on dbo.v1 to c", "update dbo.v1 set d = 5", "The SELECT permission was denied on the object 'j'")]
    [DataRow(JoinOverJoin, "grant select, update on dbo.v1 to c; grant select, update on s1.j to c", "update dbo.v1 set b = 5", "The SELECT permission was denied on the object 't2'")]
    public void UpdateThroughAJoinViewChain_IsRefusedAtTheFirstBrokenLink(string top, string grants, string statement, string message)
    {
        var ex = OverJoinView(top, grants).AssertSqlError($"execute as user = 'c'; {statement}", 229);
        Contains(message, ex.Errors[0].Message);
    }

    /// <summary>Where both the SELECT and the UPDATE are missing on the view a write crosses, real reports both, SELECT first.</summary>
    [TestMethod]
    public void UpdateThroughAJoinViewChain_MissingBothOnTheJoinView_ReportsBoth()
    {
        var ex = OverJoinView(ViewOverJoin, "grant select, update on dbo.v1 to c")
            .AssertSqlError("execute as user = 'c'; update dbo.v1 set b = 5 where a = 1", 229);
        AreEqual(2, ex.Errors.Count);
        Contains("The SELECT permission was denied on the object 'j'", ex.Errors[0].Message);
        Contains("The UPDATE permission was denied on the object 'j'", ex.Errors[1].Message);
    }

    /// <summary>With the whole chain granted the write runs; a view sharing its owner's chain costs nothing.</summary>
    [TestMethod]
    public void UpdateThroughAJoinViewChain_WithEveryLinkGranted_Runs()
        => AreEqual(1, OverJoinView(ViewOverJoin, "grant select, update on dbo.v1 to c; grant select, update on s1.j to c; grant select, update on dbo.t1 to c; grant select on dbo.t2 to c")
            .ExecuteNonQuery("execute as user = 'c'; update dbo.v1 set b = 5 where a = 1"));

    /// <summary>An INSERT through the same chain needs INSERT on each view whose owner differs, then on the table (probed 2026-09-29).</summary>
    [TestMethod]
    [DataRow("grant insert on dbo.v1 to c", "The INSERT permission was denied on the object 'j'")]
    [DataRow("grant insert on dbo.v1 to c; grant insert on s1.j to c", "The INSERT permission was denied on the object 't1'")]
    public void InsertThroughAJoinViewChain_IsRefusedAtTheFirstBrokenLink(string grants, string message)
    {
        var ex = OverJoinView(ViewOverJoin, grants).AssertSqlError("execute as user = 'c'; insert dbo.v1 (a, b) values (5, 6)", 229);
        Contains(message, ex.Errors[0].Message);
    }

    /// <summary>
    /// A MERGE through a view over another owner's join view is refused at the
    /// join view — SELECT, then the action's own permission — and then at the
    /// join's tables (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("update", "grant select, insert, update, delete on dbo.v1 to c", "The SELECT permission was denied on the object 'j'", "The UPDATE permission was denied on the object 'j'")]
    [DataRow("insert", "grant select, insert, update, delete on dbo.v1 to c", "The SELECT permission was denied on the object 'j'", "The INSERT permission was denied on the object 'j'")]
    [DataRow("delete", "grant select, insert, update, delete on dbo.v1 to c", "The SELECT permission was denied on the object 'j'", "The DELETE permission was denied on the object 'j'")]
    [DataRow("update", "grant select, insert, update, delete on dbo.v1 to c; grant select, insert, update, delete on s1.j to c", "The SELECT permission was denied on the object 't2'", null)]
    [DataRow("insert", "grant select, insert, update, delete on dbo.v1 to c; grant select on s1.j to c", "The INSERT permission was denied on the object 'j'", null)]
    public void MergeThroughAJoinViewChain_IsRefusedAtTheFirstBrokenLink(string action, string grants, string first, string? second)
    {
        var statement = action switch
        {
            "update" => "merge dbo.v1 using (select 1 k) s on v1.a = s.k when matched then update set b = 5;",
            "insert" => "merge dbo.v1 using (select 9 k) s on v1.a = s.k when not matched then insert (a, b) values (9, 9);",
            _ => "merge dbo.v1 using (select 1 k) s on v1.a = s.k when matched then delete;",
        };
        var ex = OverJoinView(ViewOverJoin, grants).AssertSqlError($"execute as user = 'c'; {statement}", 229);
        Contains(first, ex.Errors[0].Message);
        if (second is not null)
            Contains(second, ex.Errors[1].Message);
    }

    /// <summary>With no grant on either of two tables a statement reads, real names the last one bound (probed 2026-09-29).</summary>
    [TestMethod]
    [DataRow("select ta.a from dbo.ta join dbo.tb on ta.a = tb.a", "tb")]
    [DataRow("select tb.a from dbo.tb join dbo.ta on ta.a = tb.a", "ta")]
    [DataRow("select a from dbo.ta where a = (select max(a) from dbo.tb)", "tb")]
    [DataRow("select a from dbo.ta union all select a from dbo.tb", "tb")]
    [DataRow("select * from s1.v", "ta")]
    public void SeveralDeniedObjects_NameTheLastBound(string query, string denied)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user u1 without login",
            "create schema s1 authorization u1",
            "create table dbo.ta (a int); create table dbo.tb (a int)",
            "create view s1.v as select tb.a from dbo.tb join dbo.ta on ta.a = tb.a",
            "grant select on s1.v to c");
        var ex = sim.AssertSqlError($"execute as user = 'c'; {query}", 229);
        Contains($"on the object '{denied}'", ex.Errors[0].Message);
    }
}
