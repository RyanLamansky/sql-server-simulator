namespace SqlServerSimulator;

/// <summary>
/// INSERT through a view whose body reads several sources. Real SQL Server
/// accepts one whose explicit column list names a single base table's columns
/// — it writes that table, the untargeted columns taking their defaults — and
/// refuses every other shape with Msg 4405: an implicit column list,
/// <c>DEFAULT VALUES</c>, and a list spanning two base tables. All behavior
/// here was probed against SQL Server 2025 (17.0.4065.4).
/// </summary>
[TestClass]
public sealed class JoinViewInsertTests
{
    /// <summary>
    /// One-to-many pair plus the inner-join view over it. <c>many.note</c>
    /// carries a DEFAULT so an INSERT that leaves it out is observable, and
    /// <c>many.val</c> is NOT NULL with no default so an INSERT that leaves
    /// <em>it</em> out reports the base table's own Msg 515.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table dbo.one (id int primary key, name varchar(30), n int);
            create table dbo.many (id int primary key, one_id int, val int not null, note varchar(10) not null default 'dflt');
            insert dbo.one values (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);
            insert dbo.many (id, one_id, val) values (11, 1, 100), (12, 1, 200), (21, 2, 400)
            """,
            "create view dbo.v as select o.id as oid, o.name, o.n, m.id as mid, m.one_id, m.val, m.note from dbo.one o join dbo.many m on m.one_id = o.id");
        return simulation;
    }

    /// <summary>
    /// The column list names the many side, so the many side is written and
    /// the one side is left alone — <c>note</c> takes its DEFAULT the way it
    /// would on a direct INSERT.
    /// </summary>
    [TestMethod]
    public void ExplicitSingleBaseColumnListWritesThatTable()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.v (mid, one_id, val) values (31, 1, 999)"));
        Assert.AreEqual("dflt", simulation.ExecuteScalar("select note from dbo.many where id = 31"));
        Assert.AreEqual(3, simulation.ExecuteScalar<int>("select count(*) from dbo.one"));
    }

    /// <summary>Either base table is a legal target; the list picks it.</summary>
    [TestMethod]
    public void ColumnListNamingTheOtherBaseWritesTheOtherTable()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.v (oid, name, n) values (4, 'd', 40)"));
        Assert.AreEqual(40, simulation.ExecuteScalar<int>("select n from dbo.one where id = 4"));
        Assert.AreEqual(3, simulation.ExecuteScalar<int>("select count(*) from dbo.many"));
    }

    /// <summary>
    /// A NOT NULL column of the targeted table that the list leaves out
    /// reports the base table's own Msg 515 — the view routes the write, it
    /// doesn't relax the target's constraints.
    /// </summary>
    [TestMethod]
    public void OmittedNotNullColumnOfTheTargetIsMsg515()
        => _ = Seeded().AssertSqlError("insert dbo.v (mid, one_id) values (32, 1)", 515);

    /// <summary>
    /// Without a column list there is nothing to route on, so real refuses
    /// the INSERT as affecting several base tables — <c>DEFAULT VALUES</c>
    /// included.
    /// </summary>
    [TestMethod]
    public void ImplicitColumnListIsMsg4405()
    {
        var simulation = Seeded();
        var ex = simulation.AssertSqlError("insert dbo.v values (4, 'd', 40, 31, 1, 999, 'x')", 4405);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual("View or function 'dbo.v' is not updatable because the modification affects multiple base tables.", ex.Message);
        _ = simulation.AssertSqlError("insert dbo.v default values", 4405);
    }

    /// <summary>A list spanning two base tables is Msg 4405, as on real.</summary>
    [TestMethod]
    public void ColumnListSpanningTwoBaseTablesIsMsg4405()
        => _ = Seeded().AssertSqlError("insert dbo.v (mid, val, name) values (33, 1, 'x')", 4405);

    /// <summary>
    /// Naming a derived output column is Msg 4406, and the two rejections are
    /// positional: the walk reports whichever fault it meets first, so a list
    /// whose earlier pair already spans two tables is Msg 4405 even with a
    /// derived column behind it (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void DerivedOutputColumnIsMsg4406AndTheWalkIsPositional()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vd as select o.id as oid, o.n + 1 as nplus, m.id as mid, m.val from dbo.one o join dbo.many m on m.one_id = o.id");

        var ex = simulation.AssertSqlError("insert dbo.vd (mid, val, nplus) values (34, 1, 5)", 4406);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual("Update or insert of view or function 'dbo.vd' failed because it contains a derived or constant field.", ex.Message);

        _ = simulation.AssertSqlError("insert dbo.vd (oid, mid, nplus) values (5, 34, 5)", 4405);
    }

    /// <summary>An unknown column reports against the view's own columns.</summary>
    [TestMethod]
    public void UnknownColumnIsMsg207()
        => _ = Seeded().AssertSqlError("insert dbo.v (nope) values (1)", 207);

    /// <summary>
    /// The targeted base table's IDENTITY allocates through the view and
    /// SCOPE_IDENTITY reports it; <c>SET IDENTITY_INSERT</c> names the base
    /// table (real answers Msg 8105 for the view name).
    /// </summary>
    [TestMethod]
    public void IdentityOfTheTargetedBaseAllocatesThroughTheView()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table dbo.one (id int primary key, n int);
            create table dbo.ai (id int identity(1, 1) primary key, one_id int, val int);
            insert dbo.one values (1, 10);
            insert dbo.ai (one_id, val) values (1, 100)
            """,
            "create view dbo.va as select o.id as oid, o.n, a.id as aid, a.one_id, a.val from dbo.one o join dbo.ai a on a.one_id = o.id");

        Assert.AreEqual(2, simulation.ExecuteScalar<int>("insert dbo.va (one_id, val) values (1, 200); select cast(scope_identity() as int)"));
        _ = simulation.AssertSqlError("insert dbo.va (aid, one_id, val) values (9, 1, 300)", 544);
        Assert.AreEqual(1, simulation.ExecuteNonQuery("set identity_insert dbo.ai on; insert dbo.va (aid, one_id, val) values (9, 1, 300); set identity_insert dbo.ai off"));
        Assert.AreEqual(300, simulation.ExecuteScalar<int>("select val from dbo.ai where id = 9"));
    }

    /// <summary>A SELECT source routes the same way a VALUES source does.</summary>
    [TestMethod]
    public void SelectSourceRoutesToTheSameBaseTable()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.v (mid, one_id, val) select 41, 1, 5"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 41"));
    }

    /// <summary>
    /// <c>WITH CHECK OPTION</c> on a join view covers the join as well as the
    /// body's WHERE: a row that joins to nothing never surfaces through the
    /// view, so inserting one is Msg 550 and the heap stays unchanged.
    /// </summary>
    [TestMethod]
    public void CheckOptionCoversTheJoinOnInsert()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vk as select o.id as oid, m.id as mid, m.one_id, m.val from dbo.one o join dbo.many m on m.one_id = o.id where m.val < 500 with check option");

        var ex = simulation.AssertSqlError("insert dbo.vk (mid, one_id, val) values (51, 99, 5)", 550);
        Assert.AreEqual(1, ex.State);
        _ = simulation.AssertSqlError("insert dbo.vk (mid, one_id, val) values (51, 1, 9999)", 550);
        Assert.IsNull(simulation.ExecuteScalar("select val from dbo.many where id = 51"));

        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.vk (mid, one_id, val) values (51, 1, 5)"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 51"));
    }

    /// <summary>
    /// A join view without CHECK OPTION accepts a row that won't surface
    /// through it — the body's WHERE and its join only filter reads.
    /// </summary>
    [TestMethod]
    public void UncheckedJoinViewAcceptsARowThatNeverSurfaces()
    {
        var simulation = Seeded();
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.v (mid, one_id, val) values (61, 99, 5)"));
        Assert.AreEqual(5, simulation.ExecuteScalar<int>("select val from dbo.many where id = 61"));
        Assert.IsNull(simulation.ExecuteScalar("select val from dbo.v where mid = 61"));
    }

    /// <summary>
    /// An un-taken IF branch routes and binds but writes nothing — the chain
    /// is walked to reach a target either way, and the heap insert returns on
    /// the skip gate the single-base path already carries.
    /// </summary>
    [TestMethod]
    public void SkippedBranchRoutesWithoutWriting()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("if 1 = 0 insert dbo.v (mid, one_id, val) values (99, 1, 5)");
        Assert.AreEqual(3, simulation.ExecuteScalar<int>("select count(*) from dbo.many"));
    }

    /// <summary>OUTPUT through a view still isn't modeled, join view included.</summary>
    [TestMethod]
    public void OutputThroughAJoinViewInsertIsNotSupported()
        => _ = Assert.Throws<NotSupportedException>(
            () => Seeded().ExecuteNonQuery("insert dbo.v (mid, one_id, val) output inserted.val values (71, 1, 5)"));

    /// <summary>
    /// A join view reading another join view flattens on real but not here:
    /// the target source is a view rather than a heap, so both verbs stay
    /// Msg 4405 — see docs/claude/programmable.md.
    /// </summary>
    [TestMethod]
    public void JoinViewOverAJoinViewIsStillMsg4405()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create view dbo.vjj as select v.mid as jid, v.val as jval, o2.n as j2n from dbo.v v join dbo.one o2 on o2.id = v.oid");
        _ = simulation.AssertSqlError("insert dbo.vjj (jid, jval) values (81, 1)", 4405);
        _ = simulation.AssertSqlError("update dbo.vjj set jval = 1 where jid = 11", 4405);
    }
}
