using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Cursors over more than one base table: the sensitivity a JOIN / comma FROM
/// resolves to, live re-folding between FETCHes, the composite per-source row
/// identity KEYSET membership and positioned <c>WHERE CURRENT OF</c> DML ride
/// on, and the Msg 16933 / 16947 / 16961 rejections. Behavior probed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CursorMultiSourceTests
{
    private const string Seed =
        "create table a (id int primary key, v int not null); " +
        "create table b (id int primary key, a_id int not null, w int not null); " +
        "insert a values (1,10),(2,20),(3,30); " +
        "insert b values (100,1,1),(200,2,2),(300,3,3);";

    // ---- sensitivity by shape ----

    /// <summary>
    /// Probe-confirmed shape table: every multi-source FROM real keeps DYNAMIC
    /// reports <c>@@CURSOR_ROWS = -1</c>, while the shapes it converts to a
    /// read-only snapshot (DISTINCT / GROUP BY / set op) report the
    /// materialized count.
    /// </summary>
    [TestMethod]
    [DataRow("select a.id, b.w from a join b on b.a_id = a.id", -1)]
    [DataRow("select a.id, b.w from a inner join b on b.a_id = a.id where a.v > 5", -1)]
    [DataRow("select a.id, b.w from a left join b on b.a_id = a.id", -1)]
    [DataRow("select a.id, b.w from a right join b on b.a_id = a.id", -1)]
    [DataRow("select a.id, b.w from a full join b on b.a_id = a.id", -1)]
    [DataRow("select a.id, b.id from a cross join b", -1)]
    [DataRow("select a.id, b.w from a, b where b.a_id = a.id", -1)]
    [DataRow("select a.id, c.v from a join a c on c.id = a.id", -1)]
    [DataRow("select a.id, b.w from a join b on b.a_id = a.id join a c on c.id = a.id", -1)]
    [DataRow("select a.id, b.w from a join b on b.a_id = a.id order by a.id", -1)]
    [DataRow("select distinct a.v from a join b on b.a_id = a.id", 3)]
    [DataRow("select a.v, count(*) c from a join b on b.a_id = a.id group by a.v", 3)]
    [DataRow("select a.id from a union all select b.id from b", 6)]
    public void ShapeResolvesToProbedSensitivity(string query, int cursorRows)
        => AreEqual(cursorRows, ExecuteScalar<int>($"{Seed} declare c cursor for {query}; open c; select @@cursor_rows"));

    /// <summary>
    /// The sensitivity keywords resolve on a JOIN exactly as on a single table
    /// (probe-confirmed): bare is forward-only DYNAMIC, a plain SCROLL is
    /// KEYSET, and an explicit STATIC is a read-only snapshot.
    /// </summary>
    [TestMethod]
    [DataRow("", -1)]
    [DataRow("dynamic", -1)]
    [DataRow("scroll", 3)]
    [DataRow("keyset", 3)]
    [DataRow("static", 3)]
    [DataRow("fast_forward", 3)]
    public void JoinCursorSensitivityKeywords(string declaration, int cursorRows)
        => AreEqual(cursorRows, ExecuteScalar<int>($"""
            {Seed}
            declare c cursor {declaration} for select a.id, b.w from a join b on b.a_id = a.id;
            open c;
            select @@cursor_rows
            """));

    // ---- mid-loop visibility ----

    [TestMethod]
    public void DynamicJoinCursor_SeesMidLoopUpdateOnEitherSide()
        => AreEqual("1/10/1;2/2222/22;3/30/3;", new Simulation().ExecuteScalar($"""
            {Seed}
            declare @id int, @v int, @w int, @log varchar(200) = '';
            declare c cursor for select a.id, a.v, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            fetch next from c into @id, @v, @w;
            update a set v = 2222 where id = 2;
            update b set w = 22 where a_id = 2;
            while @@fetch_status = 0
            begin
              set @log = @log + cast(@id as varchar) + '/' + cast(@v as varchar) + '/' + cast(@w as varchar) + ';';
              fetch next from c into @id, @v, @w;
            end
            select @log
            """));

    [TestMethod]
    public void DynamicJoinCursor_SeesMidLoopInsertAndSkipsMidLoopDelete()
        => AreEqual("1;2;5;", new Simulation().ExecuteScalar($"""
            {Seed}
            declare @id int, @w int, @log varchar(200) = '';
            declare c cursor for select a.id, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            fetch next from c into @id, @w;
            while @@fetch_status = 0
            begin
              set @log = @log + cast(@id as varchar) + ';';
              if @id = 2
              begin
                insert a values (5, 50);
                insert b values (500, 5, 55);
                delete b where a_id = 3;
              end
              fetch next from c into @id, @w;
            end
            select @log
            """));

    /// <summary>
    /// A DYNAMIC join cursor re-folds the whole join per FETCH, so a row
    /// inserted into the inner side mid-loop appears against an outer row the
    /// cursor has not yet passed (probe-confirmed on a CROSS JOIN).
    /// </summary>
    [TestMethod]
    public void DynamicCrossJoinCursor_SeesRowInsertedIntoTheInnerSide()
        => AreEqual("1/100;1/200;2/100;2/150;2/200;", new Simulation().ExecuteScalar("""
            create table a (id int primary key, v int not null);
            create table b (id int primary key, a_id int not null, w int not null);
            insert a values (1,10),(2,20);
            insert b values (100,1,1),(200,2,2);
            declare @p int, @q int, @log varchar(200) = '';
            declare c cursor for select a.id, b.id from a cross join b order by a.id, b.id;
            open c;
            fetch next from c into @p, @q;
            while @@fetch_status = 0
            begin
              set @log = @log + cast(@p as varchar) + '/' + cast(@q as varchar) + ';';
              if @q = 200 and @p = 1 insert b values (150, 1, 9);
              fetch next from c into @p, @q;
            end
            select @log
            """));

    /// <summary>
    /// KEYSET membership over a join is the composite of the per-source
    /// identities: values re-read live, an insert stays invisible, and either
    /// side of a member disappearing yields <c>@@FETCH_STATUS = -2</c>
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void KeysetJoinCursor_FreezesMembershipAndReportsMinusTwoForALostSide()
        => AreEqual("0:10;0:999;-2;-1;", new Simulation().ExecuteScalar($"""
            {Seed}
            declare @id int, @v int, @w int, @log varchar(200) = '';
            declare c cursor keyset for select a.id, a.v, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            fetch next from c into @id, @v, @w;
            set @log = @log + cast(@@fetch_status as varchar) + ':' + cast(@v as varchar) + ';';
            update a set v = 999 where id = 2;
            delete b where a_id = 3;
            insert a values (7, 70);
            insert b values (700, 7, 77);
            fetch next from c into @id, @v, @w;
            set @log = @log + cast(@@fetch_status as varchar) + ':' + cast(@v as varchar) + ';';
            fetch next from c into @id, @v, @w;
            set @log = @log + cast(@@fetch_status as varchar) + ';';
            fetch next from c into @id, @v, @w;
            set @log = @log + cast(@@fetch_status as varchar) + ';';
            select @log
            """));

    // ---- scroll rules ----

    [TestMethod]
    public void ScrollJoinCursor_IsKeysetSoAbsoluteWorks()
        => AreEqual(2, ExecuteScalar<int>($"""
            {Seed}
            declare @id int, @w int;
            declare c cursor scroll for select a.id, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            fetch absolute 2 from c into @id, @w;
            select @id
            """));

    [TestMethod]
    public void ScrollDynamicJoinCursor_RejectsAbsoluteButAllowsRelative()
    {
        new Simulation().AssertSqlError($"""
            {Seed}
            declare @id int, @w int;
            declare c cursor scroll dynamic for select a.id, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            fetch absolute 2 from c into @id, @w;
            """, 16925, "The fetch type Absolute cannot be used with dynamic cursors.");

        AreEqual(3, ExecuteScalar<int>($"""
            {Seed}
            declare @id int, @w int;
            declare c cursor scroll dynamic for select a.id, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            fetch first from c into @id, @w;
            fetch relative 2 from c into @id, @w;
            select @id
            """));
    }

    [TestMethod]
    public void ForwardOnlyJoinCursor_RejectsPriorWith16911()
        => new Simulation().AssertSqlError($"""
            {Seed}
            declare @id int, @w int;
            declare c cursor for select a.id, b.w from a join b on b.a_id = a.id;
            open c;
            fetch next from c into @id, @w;
            fetch prior from c into @id, @w;
            """, 16911, "fetch: The fetch type prior cannot be used with forward only cursors.");

    // ---- positioned DML ----

    /// <summary>
    /// Probe-confirmed: real allows <c>WHERE CURRENT OF</c> against <em>any</em>
    /// table the join cursor reads, resolving the row through that table's own
    /// slot of the joined tuple.
    /// </summary>
    [TestMethod]
    public void PositionedUpdate_TargetsEitherSideOfTheJoin()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {Seed}
            declare c cursor for select a.id, a.v, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            declare @id int, @v int, @w int;
            fetch next from c into @id, @v, @w;
            update a set v = 111 where current of c;
            update b set w = 222 where current of c;
            close c; deallocate c;
            """);
        AreEqual(111, sim.ExecuteScalar<int>("select v from a where id = 1"));
        AreEqual(222, sim.ExecuteScalar<int>("select w from b where id = 100"));
        AreEqual(20, sim.ExecuteScalar<int>("select v from a where id = 2"));
    }

    [TestMethod]
    public void PositionedDelete_TargetsTheNamedSideOfTheJoin()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {Seed}
            declare c cursor for select a.id, b.w from a join b on b.a_id = a.id order by a.id;
            open c;
            declare @id int, @w int;
            fetch next from c into @id, @w;
            delete from b where current of c;
            close c; deallocate c;
            """);
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from b where id = 100"));
        AreEqual(3, sim.ExecuteScalar<int>("select count(*) from a"));
    }

    [TestMethod]
    public void PositionedUpdate_ReachesTheMiddleTableOfAThreeTableJoin()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {Seed}
            declare c cursor for
              select a.id, b.id, c2.id from a join b on b.a_id = a.id join a c2 on c2.id = a.id order by a.id;
            open c;
            declare @x int, @y int, @z int;
            fetch next from c into @x, @y, @z;
            update b set w = 42 where current of c;
            close c; deallocate c;
            """);
        AreEqual(42, sim.ExecuteScalar<int>("select w from b where id = 100"));
    }

    /// <summary>Msg 16933 — the target isn't one of the cursor's own sources.</summary>
    [TestMethod]
    public void PositionedDml_OnATableTheCursorDoesNotRead_Raises16933()
        => new Simulation().AssertSqlError($"""
            {Seed}
            declare c cursor for select id, v from a;
            open c;
            declare @id int, @v int;
            fetch next from c into @id, @v;
            update b set w = 1 where current of c;
            """, 16933, "The cursor does not include the table being modified or the table is not updatable through the cursor.");

    /// <summary>
    /// Msg 16947 — the cursor is positioned, but the named table's slot is the
    /// NULL-extended side of the outer join, so there is no row to mutate.
    /// Real emits no descriptive Msg 16934 here (nothing changed out-of-band).
    /// </summary>
    [TestMethod]
    public void PositionedUpdate_OnTheNullExtendedSide_Raises16947()
        => new Simulation().AssertSqlError($"""
            {Seed}
            insert a values (9, 90);
            declare c cursor for select a.id, b.w from a left join b on b.a_id = a.id order by a.id desc;
            open c;
            declare @id int, @w int;
            fetch next from c into @id, @w;
            update b set w = 5 where current of c;
            """, 16947, "No rows were updated or deleted.\nThe statement has been terminated.");

    [TestMethod]
    public void PositionedDml_BeforeAnyFetch_StillRaises16931()
        => new Simulation().AssertSqlError($"""
            {Seed}
            declare c cursor for select a.id, b.w from a join b on b.a_id = a.id;
            open c;
            update a set v = 1 where current of c;
            """, 16931, "There are no rows in the current fetch buffer.");

    [TestMethod]
    public void PositionedDml_OnAStaticJoinCursor_Raises16929()
        => new Simulation().AssertSqlError($"""
            {Seed}
            declare c cursor static for select a.id, b.w from a join b on b.a_id = a.id;
            open c;
            declare @id int, @w int;
            fetch next from c into @id, @w;
            update a set v = 1 where current of c;
            """, 16929, "The cursor is READ ONLY.");

    /// <summary>
    /// A self-join reaches one table through two slots; real binds the first
    /// and says so through the severity-0 Msg 16961 (probe-confirmed: emitted
    /// at positioned-DML time, not at DECLARE).
    /// </summary>
    [TestMethod]
    public void PositionedUpdate_OnASelfJoin_BindsFirstInstanceAndInforms16961()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.Add(e.Message);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                {Seed}
                declare c cursor for select a.id, c2.v from a join a c2 on c2.id = a.id order by a.id;
                open c;
                declare @x int, @y int;
                fetch next from c into @x, @y;
                update a set v = -1 where current of c;
                close c; deallocate c;
                """;
            _ = command.ExecuteNonQuery();
        }
        AreEqual(-1, sim.ExecuteScalar<int>("select v from a where id = 1"));
        IsTrue(messages.Exists(m => m.Contains("adjusted to the first instance of their table", StringComparison.Ordinal)), string.Join("|", messages));
    }

    // ---- FOR UPDATE OF over a join ----

    /// <summary>
    /// <c>FOR UPDATE OF</c> narrows the cursor's updatable <em>tables</em> to
    /// those owning a listed column, so a positioned DML on any other table is
    /// Msg 16933 — even a DELETE, which takes no column gate. An unlisted
    /// column of an owning table is still Msg 16932. All probe-confirmed.
    /// </summary>
    [TestMethod]
    public void ForUpdateOf_NarrowsTheUpdatableTables()
    {
        const string Declare = """
            declare c cursor for select a.id, a.v, b.w from a join b on b.a_id = a.id order by a.id for update of v;
            open c;
            declare @id int, @v int, @w int;
            fetch next from c into @id, @v, @w;
            """;
        _ = new Simulation().AssertSqlError($"{Seed} {Declare} update b set w = 1 where current of c;", 16933);
        _ = new Simulation().AssertSqlError($"{Seed} {Declare} delete from b where current of c;", 16933);
        _ = new Simulation().AssertSqlError($"{Seed} {Declare} update a set id = 9 where current of c;", 16932);

        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{Seed} {Declare} update a set v = 7 where current of c; close c; deallocate c;");
        AreEqual(7, sim.ExecuteScalar<int>("select v from a where id = 1"));
    }

    // ---- shapes that stay STATIC ----

    /// <summary>
    /// A source with no stable heap address — a derived table, a view, a CTE,
    /// an APPLY right side — keeps the cursor on the read-only STATIC snapshot,
    /// so positioned DML through it is Msg 16929. The rowset is still correct;
    /// see docs/claude/cursors.md for the residual.
    /// </summary>
    [TestMethod]
    [DataRow("select d.id, d.v from (select id, v from a) d")]
    [DataRow("with k as (select id, v from a) select id, v from k")]
    [DataRow("select a.id, x.w from a cross apply (select w from b where b.a_id = a.id) x")]
    public void DeferredSourceShapes_StayStatic(string query)
    {
        AreEqual(3, ExecuteScalar<int>($"{Seed} declare c cursor for {query}; open c; select @@cursor_rows"));
        _ = new Simulation().AssertSqlError($"""
            {Seed}
            declare c cursor for {query};
            open c;
            declare @p int, @q int;
            fetch next from c into @p, @q;
            update a set v = 1 where current of c;
            """, 16929);
    }
}
