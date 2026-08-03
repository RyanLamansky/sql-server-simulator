namespace SqlServerSimulator;

/// <summary>
/// UPDATE through a view whose body reads several sources. Real SQL Server
/// accepts one whose SET list lands entirely in a single base table and
/// refuses everything else about such a view — a SET list spanning two base
/// tables, a DELETE (which touches whole rows), and an INSERT that doesn't
/// name one base table's columns are all Msg 4405. All behavior here was
/// probed against SQL Server 2025 (17.0.4065.4).
/// </summary>
[TestClass]
public sealed class JoinViewUpdateTests
{
    /// <summary>
    /// One-to-many pair plus the inner-join view over it: <c>one</c> id 1
    /// carries three <c>many</c> rows, id 2 carries one, id 3 carries none.
    /// The multiplicity is what makes the once-per-base-row rule observable.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table dbo.one (id int primary key, name varchar(30), n int);
            create table dbo.many (id int primary key, one_id int, val int);
            insert dbo.one values (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);
            insert dbo.many values (11, 1, 100), (12, 1, 200), (13, 1, 300), (21, 2, 400)
            """,
            "create view dbo.v as select o.id as oid, o.name, o.n, m.id as mid, m.val from dbo.one o join dbo.many m on m.one_id = o.id");
        return simulation;
    }

    /// <summary>
    /// The many side is written through the view, filtered by a column of
    /// the <em>other</em> base table — the single-base-table rule constrains
    /// the SET targets only, not what the WHERE may read.
    /// </summary>
    [TestMethod]
    public void UpdateWritesTheOneBaseTableTheSetListNames()
    {
        var simulation = Seeded();
        Assert.AreEqual(3, simulation.ExecuteNonQuery("update dbo.v set val = val + 1 where oid = 1"));
        Assert.AreEqual(101, simulation.ExecuteScalar<int>("select val from dbo.many where id = 11"));
        Assert.AreEqual(301, simulation.ExecuteScalar<int>("select val from dbo.many where id = 13"));
        Assert.AreEqual(400, simulation.ExecuteScalar<int>("select val from dbo.many where id = 21"));
    }

    /// <summary>
    /// The one side surfaces in three join rows, and the SET applies to its
    /// base row exactly once — <c>n</c> advances by 1, not 3, and
    /// <c>@@ROWCOUNT</c> reports the one base row.
    /// </summary>
    [TestMethod]
    public void JoinMultipliedTargetRowUpdatesOnce()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.v set n = n + 1 where oid = 1"));
        Assert.AreEqual(11, simulation.ExecuteScalar<int>("select n from dbo.one where id = 1"));
        Assert.AreEqual(1, simulation.ExecuteScalar<int>("update dbo.v set n = n + 1 where oid = 1; select @@rowcount"));
    }

    /// <summary>
    /// When the SET reads the many side, the one row it lands on is
    /// undefined in real and the first matching tuple's here — heap order,
    /// the same rule the alias-form joined UPDATE follows.
    /// </summary>
    [TestMethod]
    public void JoinMultipliedTargetTakesTheFirstTuplesValue()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("update dbo.v set n = val where oid = 1");
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select n from dbo.one where id = 1"));
    }

    /// <summary>A WHERE that matches only some of a base row's join rows still updates it once.</summary>
    [TestMethod]
    public void FilteringToOneJoinRowStillReachesTheOneSide()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.v set n = n + 5 where mid = 12"));
        Assert.AreEqual(15, simulation.ExecuteScalar<int>("select n from dbo.one where id = 1"));
    }

    [TestMethod]
    public void SetListSpanningTwoBaseTablesIsMsg4405()
    {
        var ex = Seeded().AssertSqlError("update dbo.v set n = 5, val = 6 where oid = 1", 4405);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual("View or function 'dbo.v' is not updatable because the modification affects multiple base tables.", ex.Message);
    }

    /// <summary>
    /// A DELETE through a multi-source view is Msg 4405 whatever it touches
    /// — the row it would remove spans every base table — including through
    /// a view that projects one table's columns only.
    /// </summary>
    [TestMethod]
    public void DeleteThroughAJoinViewIsMsg4405()
    {
        _ = Seeded().AssertSqlError("delete from dbo.v where mid = 11", 4405);

        var oneSided = Seeded();
        _ = oneSided.ExecuteNonQuery("create view dbo.vm as select m.id as mid, m.val from dbo.one o join dbo.many m on m.one_id = o.id");
        _ = oneSided.AssertSqlError("delete from dbo.vm where mid = 11", 4405);
    }

    /// <summary>
    /// Writing a derived output column is Msg 4406, reported as the
    /// left-to-right walk of the SET list meets it — so a derived target
    /// beside a single other one is 4406 whichever order they appear in, and
    /// only a list whose earlier pair already spans two base tables reports
    /// Msg 4405 ahead of it (all three probe-confirmed).
    /// </summary>
    [TestMethod]
    public void SettingADerivedOutputColumnIsMsg4406()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vd as select o.id as oid, o.n + 1 as nplus, m.id as mid, m.val from dbo.one o join dbo.many m on m.one_id = o.id");
        var ex = simulation.AssertSqlError("update dbo.vd set nplus = 5 where oid = 1", 4406);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual("Update or insert of view or function 'dbo.vd' failed because it contains a derived or constant field.", ex.Message);
        _ = simulation.AssertSqlError("update dbo.vd set nplus = 5, val = 6 where oid = 1", 4406);
        _ = simulation.AssertSqlError("update dbo.vd set val = 6, nplus = 5 where oid = 1", 4406);
        _ = simulation.AssertSqlError("update dbo.vd set oid = 1, val = 6, nplus = 5 where oid = 1", 4405);
    }

    /// <summary>A derived output column is readable in the WHERE even though writing it isn't.</summary>
    [TestMethod]
    public void DerivedOutputColumnIsReadableInTheWhere()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vd as select o.id as oid, o.n + 1 as nplus, m.id as mid, m.val from dbo.one o join dbo.many m on m.one_id = o.id");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vd set val = val + 1 where nplus > 20"));
        Assert.AreEqual(401, simulation.ExecuteScalar<int>("select val from dbo.many where id = 21"));
    }

    /// <summary>
    /// Through a LEFT JOIN view the preserved side is writable on a
    /// NULL-extended row, while the nullable side has no row to write there
    /// and the statement affects nothing rather than raising.
    /// </summary>
    [TestMethod]
    public void OuterJoinViewWritesThePreservedSideAndSkipsTheNullExtendedOne()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vl as select o.id as oid, o.n, m.id as mid, m.val from dbo.one o left join dbo.many m on m.one_id = o.id");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vl set n = 999 where oid = 3"));
        Assert.AreEqual(999, simulation.ExecuteScalar<int>("select n from dbo.one where id = 3"));
        Assert.AreEqual(0, simulation.ExecuteNonQuery("update dbo.vl set val = 777 where oid = 3"));
    }

    /// <summary>A comma FROM is the same shape to the join-view rewrite as an explicit JOIN.</summary>
    [TestMethod]
    public void CommaJoinViewIsUpdatable()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vc as select o.id as oid, o.n, m.id as mid, m.val from dbo.one o, dbo.many m where m.one_id = o.id");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vc set val = val + 1 where oid = 2"));
        Assert.AreEqual(401, simulation.ExecuteScalar<int>("select val from dbo.many where id = 21"));
    }

    /// <summary>
    /// <c>WITH CHECK OPTION</c> on a join view covers the body's WHERE and
    /// the join itself: a value that leaves the WHERE is Msg 550, and so is
    /// one that leaves the join entirely — but re-pointing the row at a
    /// different partner it still joins to is accepted.
    /// </summary>
    [TestMethod]
    public void CheckOptionCoversTheWhereAndTheJoin()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create view dbo.vk as select o.id as oid, m.id as mid, m.one_id, m.val from dbo.one o join dbo.many m on m.one_id = o.id where m.val < 500 with check option");

        var ex = simulation.AssertSqlError("update dbo.vk set val = 9999 where mid = 11", 550);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select val from dbo.many where id = 11"));

        _ = simulation.AssertSqlError("update dbo.vk set one_id = 77 where mid = 11", 550);
        Assert.AreEqual(1, simulation.ExecuteScalar<int>("select one_id from dbo.many where id = 11"));

        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vk set one_id = 2 where mid = 11"));
        Assert.AreEqual(2, simulation.ExecuteScalar<int>("select one_id from dbo.many where id = 11"));
    }

    /// <summary>A join view without CHECK OPTION lets a write push the row out of its own WHERE.</summary>
    [TestMethod]
    public void UncheckedJoinViewLetsARowLeaveTheView()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vw as select m.id as mid, m.val, o.n from dbo.one o join dbo.many m on m.one_id = o.id where m.val < 500");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vw set val = 9999 where mid = 11"));
        Assert.AreEqual(9999, simulation.ExecuteScalar<int>("select val from dbo.many where id = 11"));
    }

    /// <summary>The body's WHERE bounds which rows the statement can reach at all.</summary>
    [TestMethod]
    public void BodyWhereGatesWhichRowsAreCandidates()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vw as select m.id as mid, m.val from dbo.one o join dbo.many m on m.one_id = o.id where o.id = 1");
        Assert.AreEqual(0, simulation.ExecuteNonQuery("update dbo.vw set val = 0 where mid = 21"));
        Assert.AreEqual(400, simulation.ExecuteScalar<int>("select val from dbo.many where id = 21"));
    }

    /// <summary>An identity / computed / rowversion base column is no more writable through a join view than directly.</summary>
    [TestMethod]
    public void ComputedBaseColumnIsNotWritableThroughAJoinView()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table dbo.p (id int primary key, v int, doubled as v * 2);
            create table dbo.c (id int primary key, p_id int);
            insert dbo.p (id, v) values (1, 5);
            insert dbo.c values (9, 1)
            """,
            "create view dbo.vp as select p.id as pid, p.doubled, c.id as cid from dbo.p p join dbo.c c on c.p_id = p.id");
        _ = simulation.AssertSqlError("update dbo.vp set doubled = 3 where pid = 1", 271);
    }

    /// <summary>
    /// An AFTER UPDATE trigger on the base table sees the write the join
    /// view routed to it, once per affected base row.
    /// </summary>
    [TestMethod]
    public void AfterTriggerOnTheBaseTableFires()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create table dbo.audit (n int)",
            "create trigger dbo.tr on dbo.one after update as insert dbo.audit select n from inserted");
        _ = simulation.ExecuteNonQuery("update dbo.v set n = 42 where oid = 1");
        Assert.AreEqual(1, simulation.ExecuteScalar<int>("select count(*) from dbo.audit"));
        Assert.AreEqual(42, simulation.ExecuteScalar<int>("select n from dbo.audit"));
    }

    /// <summary>
    /// <c>UPDATE TOP (n)</c> caps the affected base rows, not the join
    /// tuples that reached them.
    /// </summary>
    [TestMethod]
    public void TopCapsAffectedBaseRows()
    {
        var simulation = Seeded();
        Assert.AreEqual(2, simulation.ExecuteNonQuery("update top (2) dbo.v set val = 0"));
        Assert.AreEqual(2, simulation.ExecuteScalar<int>("select count(*) from dbo.many where val = 0"));
    }

    /// <summary>OUTPUT through a view still isn't modeled, join view included.</summary>
    [TestMethod]
    public void OutputThroughAJoinViewIsNotSupported()
        => _ = Assert.Throws<NotSupportedException>(
            () => Seeded().ExecuteNonQuery("update dbo.v set val = 1 output inserted.val where mid = 11"));

    /// <summary>An unknown column name in the SET list reports against the view's own columns.</summary>
    [TestMethod]
    public void UnknownSetColumnIsMsg207()
        => _ = Seeded().AssertSqlError("update dbo.v set nope = 1 where mid = 11", 207);

    /// <summary>
    /// A three-source body picks whichever of the three the SET list names,
    /// including one the middle join reaches.
    /// </summary>
    [TestMethod]
    public void ThreeSourceViewWritesTheNamedTable()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create table dbo.tag (id int primary key, many_id int, note varchar(10)); insert dbo.tag values (5, 11, 'first')",
            "create view dbo.v3 as select o.id as oid, o.n, m.id as mid, m.val, t.id as tid, t.note from dbo.one o join dbo.many m on m.one_id = o.id join dbo.tag t on t.many_id = m.id");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.v3 set val = 111 where oid = 1"));
        Assert.AreEqual(111, simulation.ExecuteScalar<int>("select val from dbo.many where id = 11"));
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.v3 set note = 'kept' where oid = 1"));
        Assert.AreEqual("kept", simulation.ExecuteScalar("select note from dbo.tag where id = 5"));
    }

    /// <summary>
    /// A self-joined body reaches the same heap through two sources; the SET
    /// list picks one of them and the address side-channel keys off that
    /// source's own yielded rows.
    /// </summary>
    [TestMethod]
    public void SelfJoinedViewWritesTheNamedSource()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vs as select l.id as lid, l.n as ln, r.id as rid, r.n as rn from dbo.one l join dbo.one r on r.id = l.id + 1");
        Assert.AreEqual(1, simulation.ExecuteNonQuery("update dbo.vs set rn = 555 where lid = 1"));
        Assert.AreEqual(555, simulation.ExecuteScalar<int>("select n from dbo.one where id = 2"));
        Assert.AreEqual(10, simulation.ExecuteScalar<int>("select n from dbo.one where id = 1"));
        _ = simulation.AssertSqlError("update dbo.vs set ln = 1, rn = 2 where lid = 1", 4405);
    }
}
