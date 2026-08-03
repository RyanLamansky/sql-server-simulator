namespace SqlServerSimulator;

/// <summary>
/// DML through a chain of single-source views sitting on top of a join view.
/// Real SQL Server passes an UPDATE and an INSERT through every level as long
/// as the SET list / column list lands in one base table, refuses a DELETE
/// with Msg 4405 naming the view the statement wrote, and composes each
/// level's WHERE — both for which rows are reachable and for
/// <c>WITH CHECK OPTION</c>. All behavior here was probed against SQL Server
/// 2025 (17.0.4065.4).
/// </summary>
[TestClass]
public sealed class ViewOverJoinViewDmlTests
{
    /// <summary>
    /// The one-to-many pair, the join view over it, and a renaming view over
    /// that. The renames are what make the per-level name mapping observable:
    /// the statement writes <c>vtop</c>'s names, the join view's projections
    /// carry its own, and the heap carries a third set.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table dbo.one (id int primary key, name varchar(30), n int);
            create table dbo.many (id int primary key, one_id int, val int, note varchar(10) not null default 'dflt');
            insert dbo.one values (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);
            insert dbo.many (id, one_id, val) values (11, 1, 100), (12, 1, 200), (21, 2, 400)
            """,
            "create view dbo.v as select o.id as oid, o.name, o.n, m.id as mid, m.one_id, m.val from dbo.one o join dbo.many m on m.one_id = o.id",
            "create view dbo.vtop as select oid as tid, name as tname, n as tn, mid as tmid, one_id as tone, val as tval from dbo.v");
        return simulation;
    }

    /// <summary>
    /// An UPDATE whose SET list lands in one base table writes it through both
    /// levels, with each level's rename resolved on the way down.
    /// </summary>
    [TestMethod]
    public void UpdateThroughTwoLevelsWritesTheNamedBaseTable()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vtop set tval = tval + 1 where tmid = 12"));
        Assert.AreEqual(201, simulation.ExecuteScalar<int>("select val from dbo.many where id = 12"));

        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vtop set tn = 99 where tid = 2"));
        Assert.AreEqual(99, simulation.ExecuteScalar<int>("select n from dbo.one where id = 2"));
    }

    /// <summary>
    /// An INSERT whose explicit column list names one base table's columns
    /// writes that table through both levels, the untargeted columns taking
    /// their defaults; a list spanning two is Msg 4405 naming the view the
    /// statement wrote, not the join view under it.
    /// </summary>
    [TestMethod]
    public void InsertThroughTwoLevelsWritesTheNamedBaseTable()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.vtop (tmid, tone, tval) values (31, 1, 5)"));
        Assert.AreEqual("dflt", simulation.ExecuteScalar("select note from dbo.many where id = 31"));

        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.vtop (tid, tname, tn) values (4, 'd', 40)"));
        Assert.AreEqual(40, simulation.ExecuteScalar<int>("select n from dbo.one where id = 4"));

        var ex = simulation.AssertSqlError("insert dbo.vtop (tmid, tval, tname) values (32, 1, 'x')", 4405);
        Assert.AreEqual("View or function 'dbo.vtop' is not updatable because the modification affects multiple base tables.", ex.Message);
    }

    /// <summary>
    /// A DELETE removes a whole row and so touches every base table whatever
    /// the chain projects — Msg 4405 naming the view the statement wrote.
    /// </summary>
    [TestMethod]
    public void DeleteThroughTwoLevelsIsMsg4405()
    {
        var ex = Seeded().AssertSqlError("delete from dbo.vtop where tmid = 12", 4405);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual("View or function 'dbo.vtop' is not updatable because the modification affects multiple base tables.", ex.Message);
    }

    /// <summary>A SET list spanning two base tables is Msg 4405 through the chain too.</summary>
    [TestMethod]
    public void SetListSpanningTwoBaseTablesIsMsg4405()
        => _ = Seeded().AssertSqlError("update dbo.vtop set tn = 1, tval = 2 where tmid = 11", 4405);

    /// <summary>
    /// A derived projection at the top level is Msg 4406 naming the top view,
    /// for both verbs — the derived column can sit at any level and real still
    /// reports the one written.
    /// </summary>
    [TestMethod]
    public void DerivedTopLevelColumnIsMsg4406()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vderived as select oid as tid, val + 1 as tplus, mid as tmid from dbo.v");
        var ex = simulation.AssertSqlError("update dbo.vderived set tplus = 5 where tmid = 11", 4406);
        Assert.AreEqual("Update or insert of view or function 'dbo.vderived' failed because it contains a derived or constant field.", ex.Message);
        _ = simulation.AssertSqlError("insert dbo.vderived (tmid, tplus) values (33, 5)", 4406);
    }

    /// <summary>A third level composes the same way; the chain has no depth limit of its own.</summary>
    [TestMethod]
    public void ThreeLevelsCompose()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.v3 as select tmid as w, tval as wv, tone as wo from dbo.vtop");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.v3 set wv = 777 where w = 21"));
        Assert.AreEqual(777, simulation.ExecuteScalar<int>("select val from dbo.many where id = 21"));
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.v3 (w, wo, wv) values (41, 1, 5)"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 41"));
    }

    /// <summary>
    /// Every level's WHERE gates which rows the statement can reach — a row
    /// the top level filters out, and one the join drops, are both unreachable.
    /// </summary>
    [TestMethod]
    public void EveryLevelsWhereGatesWhichRowsAreReachable()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vf as select mid as fid, val as fval, one_id as fone from dbo.v where val < 300");
        Assert.AreEqual(0, simulation.ExecuteNonQuery("update dbo.vf set fval = 0 where fid = 21"));
        Assert.AreEqual(400, simulation.ExecuteScalar<int>("select val from dbo.many where id = 21"));

        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.many (id, one_id, val) values (91, null, 5)"));
        Assert.AreEqual(0, simulation.ExecuteNonQuery("update dbo.vf set fval = 0 where fid = 91"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 91"));
    }

    /// <summary>
    /// <c>WITH CHECK OPTION</c> on the top level covers its own WHERE plus
    /// everything the levels below it show, so a write that leaves either is
    /// Msg 550.
    /// </summary>
    [TestMethod]
    public void TopLevelCheckOptionCoversTheWholeChain()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vc as select mid as cid, val as cval, one_id as cone from dbo.v where val < 300 with check option");

        _ = simulation.AssertSqlError("update dbo.vc set cval = 9999 where cid = 11", 550);
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select val from dbo.many where id = 11"));

        _ = simulation.AssertSqlError("update dbo.vc set cone = 77 where cid = 11", 550);
        _ = simulation.AssertSqlError("insert dbo.vc (cid, cone, cval) values (51, 1, 9999)", 550);
        _ = simulation.AssertSqlError("insert dbo.vc (cid, cone, cval) values (51, 99, 5)", 550);

        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.vc (cid, cone, cval) values (51, 1, 5)"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 51"));
    }

    /// <summary>
    /// A CHECK OPTION on the join view underneath still fires for a write
    /// issued against an unchecked view above it — real's "or spans a view
    /// that specifies WITH CHECK OPTION" wording.
    /// </summary>
    [TestMethod]
    public void LowerLevelCheckOptionFiresThroughAnUncheckedTopView()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table dbo.one (id int primary key, n int);
            create table dbo.many (id int primary key, one_id int, val int);
            insert dbo.one values (1, 10), (2, 20);
            insert dbo.many values (11, 1, 100), (21, 2, 400)
            """,
            "create view dbo.vk as select o.id as oid, m.id as mid, m.one_id, m.val from dbo.one o join dbo.many m on m.one_id = o.id where m.val < 300 with check option",
            "create view dbo.vplain as select mid as pid, one_id as pone, val as pval from dbo.vk");

        _ = simulation.AssertSqlError("update dbo.vplain set pval = 9999 where pid = 11", 550);
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select val from dbo.many where id = 11"));
        _ = simulation.AssertSqlError("insert dbo.vplain (pid, pone, pval) values (31, 1, 9999)", 550);

        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.vplain (pid, pone, pval) values (31, 1, 5)"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 31"));
    }

    /// <summary>
    /// A derived column of a lower level is still readable in the statement's
    /// WHERE even though writing one is Msg 4406 — the chain evaluates each
    /// level's projection rather than mapping it to a base column.
    /// </summary>
    [TestMethod]
    public void DerivedLowerLevelColumnIsReadableInTheWhere()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vr as select o.id as oid, m.id as mid, m.val, m.val * 2 as doubled from dbo.one o join dbo.many m on m.one_id = o.id");
        _ = simulation.ExecuteNonQuery("create view dbo.vrtop as select mid as rid, val as rval, doubled as rdoubled from dbo.vr");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vrtop set rval = 1 where rdoubled = 400"));
        Assert.AreEqual(1, simulation.ExecuteScalar<int>("select val from dbo.many where id = 12"));
    }

    /// <summary>OUTPUT through a view still isn't modeled, chained ones included.</summary>
    [TestMethod]
    public void OutputThroughTheChainIsNotSupported()
    {
        _ = Assert.Throws<NotSupportedException>(
            () => Seeded().ExecuteNonQuery("update dbo.vtop set tval = 1 output inserted.tval where tmid = 11"));
        _ = Assert.Throws<NotSupportedException>(
            () => Seeded().ExecuteNonQuery("insert dbo.vtop (tmid, tone, tval) output inserted.tval values (31, 1, 5)"));
    }

    /// <summary>
    /// A view over an <em>aggregate</em> view is Msg 4403 naming the view the
    /// statement wrote: only a multi-source bottom joins the chain-walking
    /// path.
    /// </summary>
    [TestMethod]
    public void ViewOverANonUpdatableViewIsMsg4403()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create view dbo.vagg as select one_id, count(*) as c from dbo.many group by one_id",
            "create view dbo.vaggtop as select one_id as aid, c from dbo.vagg");
        var ex = simulation.AssertSqlError("update dbo.vaggtop set aid = 1 where aid = 1", 4403);
        Assert.AreEqual("Cannot update the view or function 'dbo.vaggtop' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", ex.Message);
    }
}
