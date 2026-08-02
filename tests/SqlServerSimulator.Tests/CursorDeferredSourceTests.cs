using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Cursors whose FROM reaches base tables through a deferred source — a view,
/// a derived table, a CTE, or an APPLY right side. Real SQL Server keeps these
/// DYNAMIC (probed against SQL Server 2025 via
/// <c>sys.dm_exec_cursors(@@SPID).properties</c>), so the simulator follows the
/// deferred body down to the base heaps and re-folds it per FETCH. Positioned
/// <c>WHERE CURRENT OF</c> DML addresses the reference <em>as written</em>: a
/// view is named by the view, a derived table / CTE / APPLY body is
/// transparent and named by its base table.
/// </summary>
[TestClass]
public sealed class CursorDeferredSourceTests
{
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table a (id int primary key, v int not null, s varchar(20) null);
            create table b (id int primary key, a_id int not null, w int not null);
            insert a values (1,10,'a'),(2,20,'b'),(3,30,'c');
            insert b values (100,1,1),(200,2,2),(300,3,3);
            """,
            "create view va as select id, v, s from a;",
            "create view vwhere as select id, v from a where v > 5;",
            "create view vjoin as select a.id as aid, a.v, b.id as bid, b.w from a join b on b.a_id = a.id;",
            "create view vv as select id, v from va;",
            "create view vchk as select id, v from a where v < 100 with check option;",
            "create view vdistinct as select distinct v from a;",
            "create view vtop as select top 2 id, v from a order by id;",
            "create view vagg as select v, count(*) c from a group by v;");
        return simulation;
    }

    // ---- sensitivity by shape ----

    /// <summary>
    /// Probe-confirmed shape table: every deferred FROM real keeps DYNAMIC
    /// reports <c>@@CURSOR_ROWS = -1</c>, while the shapes it converts report
    /// the materialized count — a body carrying DISTINCT / GROUP BY / a set op
    /// becomes a read-only snapshot, a body carrying TOP / OFFSET becomes
    /// KEYSET (whose count is the limited one — see
    /// <see cref="CursorRowLimitTests"/>), and a TVF is a snapshot.
    /// </summary>
    [TestMethod]
    [DataRow("select id, v from va", -1)]
    [DataRow("select id, v from vwhere", -1)]
    [DataRow("select aid, v, w from vjoin", -1)]
    [DataRow("select id, v from vv", -1)]
    [DataRow("select d.id, d.v from (select id, v from a) d", -1)]
    [DataRow("select d.id, d.v from (select id, v from a where v > 5) d", -1)]
    [DataRow("select d.id, d.w from (select a.id, b.w from a join b on b.a_id = a.id) d", -1)]
    [DataRow("select e.id from (select d.id from (select id from a) d) e", -1)]
    [DataRow("with k as (select id, v from a) select id, v from k", -1)]
    [DataRow("with k as (select id, v from a where v > 5) select id, v from k", -1)]
    [DataRow("select a.id, x.w from a cross apply (select w from b where b.a_id = a.id) x", -1)]
    [DataRow("select a.id, x.w from a outer apply (select w from b where b.a_id = a.id) x", -1)]
    [DataRow("select d.id, d.v from (select id, v from va) d", -1)]
    [DataRow("select va.id, b.w from va join b on b.a_id = va.id", -1)]
    [DataRow("select v from vdistinct", 3)]
    [DataRow("select v, c from vagg", 3)]
    [DataRow("select id, v from vtop", 2)]
    [DataRow("select d.v from (select distinct v from a) d", 3)]
    [DataRow("select value from string_split('x,y,z', ',')", 3)]
    public void DeferredShapeResolvesToProbedSensitivity(string query, int cursorRows)
        => AreEqual(cursorRows, Seeded().ExecuteScalar<int>($"declare c cursor for {query}; open c; select @@cursor_rows"));

    /// <summary>
    /// A plain <c>SCROLL</c> over a deferred source resolves to KEYSET with the
    /// materialized row count, exactly as over a base table — probe-confirmed
    /// for both a view and a derived table.
    /// </summary>
    [TestMethod]
    [DataRow("select id, v from va")]
    [DataRow("select d.id, d.v from (select id, v from a) d")]
    public void ScrollOverDeferredSourceIsKeyset(string query)
        => AreEqual(3, Seeded().ExecuteScalar<int>($"declare c cursor scroll for {query}; open c; select @@cursor_rows"));

    // ---- mid-loop change visibility ----

    /// <summary>
    /// The deferred body is re-folded from the live base heaps on every FETCH,
    /// so a value change to a row the cursor hasn't reached shows through —
    /// probe-confirmed for each deferred shape.
    /// </summary>
    [TestMethod]
    [DataRow("select id, v from va")]
    [DataRow("select id, v from vwhere")]
    [DataRow("select id, v from vv")]
    [DataRow("select d.id, d.v from (select id, v from a) d")]
    [DataRow("with k as (select id, v from a) select id, v from k")]
    public void DeferredCursorSeesMidLoopUpdate(string query)
        => AreEqual(2020, Seeded().ExecuteScalar<int>($"""
            declare c cursor for {query};
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            update a set v = 2020 where id = 2;
            fetch next from c into @id, @v;
            select @v;
            """));

    /// <summary>A row inserted into the base table mid-loop joins the live set
    /// a deferred DYNAMIC cursor walks.</summary>
    [TestMethod]
    public void DeferredCursorSeesMidLoopInsert()
        => AreEqual(4, Seeded().ExecuteScalar<int>("""
            declare c cursor for select id, v from va;
            open c;
            declare @id int, @v int, @n int = 0;
            while 1 = 1
            begin
                fetch next from c into @id, @v;
                if @@fetch_status <> 0 break;
                set @n = @n + 1;
                if @n = 1 insert a values (4, 40, 'd');
            end
            select @n;
            """));

    /// <summary>A row deleted ahead of a deferred DYNAMIC cursor silently
    /// vanishes rather than being fetched.</summary>
    [TestMethod]
    public void DeferredCursorSkipsMidLoopDelete()
        => AreEqual(3, Seeded().ExecuteScalar<int>("""
            declare c cursor for select d.id, d.v from (select id, v from a) d;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            delete a where id = 2;
            fetch next from c into @id, @v;
            select @id;
            """));

    /// <summary>A KEYSET cursor over a deferred source keeps the membership it
    /// snapshotted at OPEN, so a member deleted out from under it fetches
    /// <c>@@FETCH_STATUS = -2</c> — the deferred body's addresses reach the
    /// keyset exactly as a base table's do.</summary>
    [TestMethod]
    public void KeysetOverDeferredSourceReportsDeletedMember()
        => AreEqual(-2, Seeded().ExecuteScalar<int>("""
            declare c cursor keyset for select id, v from va;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            delete a where id = 2;
            fetch next from c into @id, @v;
            select @@fetch_status;
            """));

    /// <summary>
    /// Re-folding a deferred body must reproduce the query's own rowset, not a
    /// cross product: the APPLY right side is re-run per left row, so the
    /// cursor walks exactly the correlated pairs.
    /// </summary>
    [TestMethod]
    public void ApplyCursorWalksTheCorrelatedPairs()
        => AreEqual("1:1|2:2|3:3", Seeded().ExecuteScalar("""
            declare c cursor for select a.id, x.w from a cross apply (select w from b where b.a_id = a.id) x order by a.id;
            open c;
            declare @id int, @w int, @acc varchar(100) = '';
            while 1 = 1
            begin
                fetch next from c into @id, @w;
                if @@fetch_status <> 0 break;
                set @acc = @acc + case when @acc = '' then '' else '|' end + cast(@id as varchar(10)) + ':' + cast(@w as varchar(10));
            end
            select @acc;
            """));

    /// <summary>An APPLY cursor's right side is re-run per FETCH too, so a
    /// change to the inner table shows through mid-loop.</summary>
    [TestMethod]
    public void ApplyCursorSeesMidLoopChangeOnTheRightSide()
        => AreEqual(222, Seeded().ExecuteScalar<int>("""
            declare c cursor for select a.id, x.w from a cross apply (select w from b where b.a_id = a.id) x order by a.id;
            open c;
            declare @id int, @w int;
            fetch next from c into @id, @w;
            update b set w = 222 where id = 200;
            fetch next from c into @id, @w;
            select @w;
            """));

    /// <summary>The projected rowset of a view cursor matches the view's own
    /// SELECT, including a body WHERE that excludes base rows.</summary>
    [TestMethod]
    public void ViewCursorWalksTheViewsOwnRowset()
        => AreEqual("2|3", Seeded().ExecuteScalar("""
            update a set v = 1 where id = 1;
            declare c cursor for select id, v from vwhere order by id;
            open c;
            declare @id int, @v int, @acc varchar(100) = '';
            while 1 = 1
            begin
                fetch next from c into @id, @v;
                if @@fetch_status <> 0 break;
                set @acc = @acc + case when @acc = '' then '' else '|' end + cast(@id as varchar(10));
            end
            select @acc;
            """));

    // ---- positioned DML: reference provenance ----

    /// <summary>
    /// Positioned DML through a view cursor names the <em>view</em> and writes
    /// through to the base table; naming the base table under it is Msg 16933
    /// (probe-confirmed — real resolves positioned DML against the reference as
    /// the cursor wrote it).
    /// </summary>
    [TestMethod]
    public void PositionedUpdateThroughViewNamesTheView()
    {
        const string Declare = """
            declare c cursor for select id, v from va order by id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            """;

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{Declare} update va set v = 999 where current of c;");
        AreEqual(999, simulation.ExecuteScalar<int>("select v from a where id = 1"));

        _ = Seeded().AssertSqlError($"{Declare} update a set v = 999 where current of c;", 16933);
    }

    /// <summary>A positioned DELETE through a view cursor removes the base
    /// row.</summary>
    [TestMethod]
    public void PositionedDeleteThroughViewRemovesTheBaseRow()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("""
            declare c cursor for select id, v from va order by id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            delete from va where current of c;
            """);
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from a where id = 1"));
    }

    /// <summary>
    /// A view over a view is addressed by the <em>outer</em> view alone: the
    /// inner view and the base table both report Msg 16933 (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void PositionedUpdateThroughViewOverViewNamesTheOuterView()
    {
        const string Declare = """
            declare c cursor for select id, v from vv order by id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            """;

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{Declare} update vv set v = 333 where current of c;");
        AreEqual(333, simulation.ExecuteScalar<int>("select v from a where id = 1"));

        _ = Seeded().AssertSqlError($"{Declare} update va set v = 1 where current of c;", 16933);
        _ = Seeded().AssertSqlError($"{Declare} update a set v = 1 where current of c;", 16933);
    }

    /// <summary>
    /// A derived table and a CTE are transparent: positioned DML names the base
    /// table the body read, and the derived-table alias is an ordinary
    /// unresolvable object name (Msg 208) — probe-confirmed.
    /// </summary>
    [TestMethod]
    [DataRow("select d.id, d.v from (select id, v from a) d order by d.id", "d")]
    [DataRow("with k as (select id, v from a) select id, v from k order by id", "k")]
    public void PositionedDmlThroughDerivedBodyNamesTheBaseTable(string query, string bodyName)
    {
        var declare = $"""
            declare c cursor for {query};
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            """;

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{declare} update a set v = 777 where current of c;");
        AreEqual(777, simulation.ExecuteScalar<int>("select v from a where id = 1"));

        var deleted = Seeded();
        _ = deleted.ExecuteNonQuery($"{declare} delete from a where current of c;");
        AreEqual(0, deleted.ExecuteScalar<int>("select count(*) from a where id = 1"));

        _ = Seeded().AssertSqlError($"{declare} update {bodyName} set v = 1 where current of c;", 208);
    }

    /// <summary>
    /// Transparency composes: a derived table over a view is addressed by the
    /// view inside it, not by the base table (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void PositionedUpdateThroughDerivedTableOverViewNamesTheView()
    {
        const string Declare = """
            declare c cursor for select d.id, d.v from (select id, v from va) d order by d.id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            """;

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{Declare} update va set v = 91 where current of c;");
        AreEqual(91, simulation.ExecuteScalar<int>("select v from a where id = 1"));

        _ = Seeded().AssertSqlError($"{Declare} update a set v = 92 where current of c;", 16933);
    }

    /// <summary>
    /// An APPLY cursor reaches both sides by base-table name — the correlated
    /// right body is transparent like any other derived table
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ApplyCursorPositionedDmlReachesBothSides()
    {
        const string Declare = """
            declare c cursor for select a.id, x.w from a cross apply (select w from b where b.a_id = a.id) x order by a.id;
            open c;
            declare @id int, @w int;
            fetch next from c into @id, @w;
            """;

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{Declare} update a set v = 222 where current of c;");
        AreEqual(222, simulation.ExecuteScalar<int>("select v from a where id = 1"));

        var right = Seeded();
        _ = right.ExecuteNonQuery($"{Declare} update b set w = 111 where current of c;");
        AreEqual(111, right.ExecuteScalar<int>("select w from b where id = 100"));
    }

    /// <summary>An OUTER APPLY whose right body yields nothing NULL-extends the
    /// slot, so positioned DML against that side has nothing to mutate — Msg
    /// 16947, the same answer a NULL-extended outer join gives.</summary>
    [TestMethod]
    public void OuterApplyNullExtendedSideHasNothingToMutate()
        => _ = Seeded().AssertSqlError("""
            delete b;
            declare c cursor for select a.id, x.w from a outer apply (select w from b where b.a_id = a.id) x order by a.id;
            open c;
            declare @id int, @w int;
            fetch next from c into @id, @w;
            update b set w = 1 where current of c;
            """, 16947);

    // ---- view semantics carried through positioned DML ----

    /// <summary>
    /// A positioned UPDATE through a <c>WITH CHECK OPTION</c> view is checked
    /// like any other write through it: a value that would leave the view's
    /// predicate is Msg 550 (probe-confirmed), while a DELETE is unaffected.
    /// </summary>
    [TestMethod]
    public void CheckOptionAppliesToPositionedUpdateThroughView()
    {
        const string Declare = """
            declare c cursor for select id, v from vchk order by id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            """;

        _ = Seeded().AssertSqlError($"{Declare} update vchk set v = 500 where current of c;", 550);

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{Declare} update vchk set v = 15 where current of c;");
        AreEqual(15, simulation.ExecuteScalar<int>("select v from a where id = 1"));
    }

    /// <summary>
    /// <c>FOR UPDATE OF</c> narrows a view cursor by the view's own column
    /// names: a listed column updates, an unlisted one is Msg 16932, and DELETE
    /// carries no column gate (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ForUpdateOfNarrowsAViewCursorByViewColumns()
    {
        const string Declare = """
            declare c cursor for select id, v, s from va order by id for update of v;
            open c;
            declare @id int, @v int, @s varchar(20);
            fetch next from c into @id, @v, @s;
            """;

        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery($"{Declare} update va set v = 11 where current of c;");
        AreEqual(11, simulation.ExecuteScalar<int>("select v from a where id = 1"));

        _ = Seeded().AssertSqlError($"{Declare} update va set s = 'z' where current of c;", 16932);

        var deleted = Seeded();
        _ = deleted.ExecuteNonQuery($"{Declare} delete from va where current of c;");
        AreEqual(0, deleted.ExecuteScalar<int>("select count(*) from a where id = 1"));
    }

    /// <summary>
    /// A view's WHERE bounds cursor membership, and a positioned UPDATE that
    /// pushes the row outside it succeeds when the view carries no CHECK
    /// OPTION (probe-confirmed) — the row simply leaves the live set the next
    /// FETCH walks.
    /// </summary>
    [TestMethod]
    public void PositionedUpdateMayPushARowOutOfAnUncheckedView()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("""
            declare c cursor for select id, v from vwhere order by id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            update vwhere set v = 1 where current of c;
            """);
        AreEqual(1, simulation.ExecuteScalar<int>("select v from a where id = 1"));
    }

    /// <summary>
    /// A view over a JOIN reads as a DYNAMIC cursor, and a positioned DELETE
    /// through it is Msg 4405 — matching real, which refuses any modification
    /// spanning several base tables. So is a positioned UPDATE whose SET list
    /// spans both, while one touching a single base table writes through.
    /// </summary>
    [TestMethod]
    public void PositionedDmlThroughAJoinViewFollowsTheSingleBaseTableRule()
    {
        const string Declare = """
            declare c cursor for select aid, v, w from vjoin order by aid;
            open c;
            declare @aid int, @v int, @w int;
            fetch next from c into @aid, @v, @w;
            """;

        _ = Seeded().AssertSqlError($"{Declare} delete from vjoin where current of c;", 4405);
        _ = Seeded().AssertSqlError($"{Declare} update vjoin set v = 1, w = 2 where current of c;", 4405);

        var updatedLeft = Seeded();
        _ = updatedLeft.ExecuteNonQuery($"{Declare} update vjoin set v = 77 where current of c;");
        AreEqual(77, updatedLeft.ExecuteScalar<int>("select v from a where id = 1"));
        AreEqual(20, updatedLeft.ExecuteScalar<int>("select v from a where id = 2"));

        var updatedRight = Seeded();
        _ = updatedRight.ExecuteNonQuery($"{Declare} update vjoin set w = 88 where current of c;");
        AreEqual(88, updatedRight.ExecuteScalar<int>("select w from b where id = 100"));
        AreEqual(2, updatedRight.ExecuteScalar<int>("select w from b where id = 200"));
    }

    /// <summary>
    /// Naming the base table under a join view is Msg 16933 like any other
    /// view — the cursor addresses the reference as written.
    /// </summary>
    [TestMethod]
    public void PositionedUpdateNamingTheBaseTableUnderAJoinViewIsMsg16933()
        => _ = Seeded().AssertSqlError("""
            declare c cursor for select aid, v, w from vjoin order by aid;
            open c;
            declare @aid int, @v int, @w int;
            fetch next from c into @aid, @v, @w;
            update a set v = 1 where current of c;
            """, 16933);

    /// <summary>
    /// A cursor reading the same view through two slots is a self-join: real
    /// binds the first instance and says so through the severity-0 Msg 16961,
    /// and the write lands on the first slot's row.
    /// </summary>
    [TestMethod]
    public void SelfJoinedViewCursorBindsTheFirstInstance()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("""
            declare c cursor for select l.id, r.id from va l join va r on r.id = l.id order by l.id;
            open c;
            declare @l int, @r int;
            fetch next from c into @l, @r;
            update va set v = 55 where current of c;
            """);
        AreEqual(55, simulation.ExecuteScalar<int>("select v from a where id = 1"));
    }

    /// <summary>
    /// An OPTIMISTIC cursor's conflict detection reaches the base row behind a
    /// view: a change made out of band between FETCH and positioned DML raises
    /// the Msg 16947 chain.
    /// </summary>
    [TestMethod]
    public void OptimisticConflictDetectedThroughAView()
        => _ = Seeded().AssertSqlError("""
            declare c cursor optimistic for select id, v from va order by id;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            update a set v = 4242 where id = 1;
            update va set v = 5 where current of c;
            """, 16947);

    // ---- TYPE_WARNING ----

    /// <summary>
    /// <c>TYPE_WARNING</c> stays silent for a view real keeps DYNAMIC and fires
    /// for a body it converts (probe-confirmed: <c>DECLARE … DYNAMIC
    /// TYPE_WARNING</c> over a DISTINCT view prints Msg 16956, over a plain view
    /// prints nothing).
    /// </summary>
    [TestMethod]
    [DataRow("select id, v from va", 0)]
    [DataRow("select v from vdistinct", 1)]
    public void TypeWarningFollowsTheConversionBoundary(string query, int expectedMessages)
    {
        var messages = 0;
        var simulation = Seeded();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        connection.InfoMessage += (_, e) => messages += e.Errors.Count;
        using var command = connection.CreateCommand($"declare c cursor dynamic type_warning for {query}; open c;");
        _ = command.ExecuteNonQuery();
        AreEqual(expectedMessages, messages);
    }
}
