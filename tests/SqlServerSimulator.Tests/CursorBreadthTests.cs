using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Cursor breadth + concurrency: GLOBAL / LOCAL scope, cursor variables
/// (<c>DECLARE @c CURSOR</c>) with refcounted references and proc OUTPUT
/// params, <c>FOR UPDATE OF</c> column gating, <c>TYPE_WARNING</c>, and the
/// <c>SCROLL_LOCKS</c> / <c>OPTIMISTIC</c> concurrency models. Behavior probed
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CursorBreadthTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Seed =
        "create table t (id int primary key, name varchar(20)); " +
        "insert t values (1,'a'),(2,'b'),(3,'c');";

    // ---- GLOBAL / LOCAL scope ----

    [TestMethod]
    public void DefaultScope_IsGlobal()
        => AreEqual((short)1, ExecuteScalar<short>(Seed + """
            declare c cursor dynamic for select id from t;
            open c;
            select cursor_status('global', 'c')
            """));

    /// <summary>
    /// A LOCAL and a GLOBAL cursor may share a name; opening the LOCAL one
    /// leaves the GLOBAL one closed.
    /// </summary>
    [TestMethod]
    public void LocalAndGlobal_SameName_AreIndependent()
        => AreEqual("1|-1", new Simulation().ExecuteScalar(Seed + """
            declare c cursor local static for select id from t;
            declare c cursor global static for select id from t;
            open c;
            select cast(cursor_status('local','c') as varchar) + '|' + cast(cursor_status('global','c') as varchar)
            """));

    /// <summary>
    /// Unqualified OPEN / FETCH bind to the LOCAL cursor when both exist.
    /// </summary>
    [TestMethod]
    public void UnqualifiedName_ResolvesLocalFirst()
        => AreEqual(10, ExecuteScalar<int>("""
            create table t (id int primary key);
            insert t values (10),(20),(30);
            declare c cursor local static for select id from t order by id;
            declare c cursor global static for select id from t order by id desc;
            open c;
            declare @v int; fetch next from c into @v;
            select @v
            """));

    [TestMethod]
    public void LocalCursor_DeallocatedAtBatchEnd()
    {
        // A LOCAL cursor is gone in the next batch on the same connection;
        // a GLOBAL one persists.
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int); insert t values (1);").ExecuteNonQuery();
        _ = conn.CreateCommand("declare lc cursor local static for select id from t; open lc; declare gc cursor global static for select id from t; open gc;").ExecuteNonQuery();
        AreEqual((short)-3, (short)conn.CreateCommand("select cursor_status('local','lc')").ExecuteScalar()!);
        AreEqual((short)1, (short)conn.CreateCommand("select cursor_status('global','gc')").ExecuteScalar()!);
    }

    [TestMethod]
    public void DeallocateUnqualified_RemovesLocalFirst()
        => AreEqual("-3|-1", new Simulation().ExecuteScalar(Seed + """
            declare c cursor local static for select id from t;
            declare c cursor global static for select id from t;
            open c;
            deallocate c;
            select cast(cursor_status('local','c') as varchar) + '|' + cast(cursor_status('global','c') as varchar)
            """));

    // ---- CURSOR_STATUS matrix ----

    [TestMethod]
    public void CursorStatus_OpenEmptyStatic_ReturnsZero()
        => AreEqual((short)0, ExecuteScalar<short>("""
            create table t (id int);
            declare c cursor global static for select id from t where id = 999;
            open c;
            select cursor_status('global','c')
            """));

    [TestMethod]
    public void CursorStatus_ScopeMismatch_ReturnsMinusThree()
        => AreEqual((short)-3, ExecuteScalar<short>(Seed + """
            declare c cursor global static for select id from t;
            open c;
            select cursor_status('local','c')
            """));

    [TestMethod]
    public void CursorStatus_ClosedNamed_ReturnsMinusOne()
        => AreEqual((short)-1, ExecuteScalar<short>(Seed + """
            declare c cursor global static for select id from t;
            select cursor_status('global','c')
            """));

    // ---- Cursor variables ----

    [TestMethod]
    public void CursorVariable_DeclaredUnset_StatusMinusTwo()
        => AreEqual((short)-2, ExecuteScalar<short>("""
            declare @c cursor;
            select cursor_status('variable','@c')
            """));

    [TestMethod]
    public void CursorVariable_SetUnopened_StatusMinusOne()
        => AreEqual((short)-1, ExecuteScalar<short>(Seed + """
            declare @c cursor;
            set @c = cursor local static for select id from t;
            select cursor_status('variable','@c')
            """));

    [TestMethod]
    public void CursorVariable_FullLifecycle()
        => AreEqual(1, ExecuteScalar<int>(Seed + """
            declare @c cursor;
            set @c = cursor local static scroll for select id from t order by id;
            open @c;
            declare @v int;
            fetch next from @c into @v;
            close @c; deallocate @c;
            select @v
            """));

    /// <summary>
    /// SET @c2 = @c makes both variables reference one cursor object; a
    /// fetch on either advances the shared position.
    /// </summary>
    [TestMethod]
    public void CursorVariable_AssignmentSharesCursorAndPosition()
        => AreEqual(2, ExecuteScalar<int>("""
            create table t (id int primary key);
            insert t values (1),(2),(3);
            declare @c cursor;
            set @c = cursor local scroll for select id from t order by id;
            open @c;
            declare @c2 cursor; set @c2 = @c;
            declare @v int;
            fetch first from @c2 into @v;
            fetch next from @c into @v;
            select @v
            """));

    [TestMethod]
    public void CursorVariable_FetchUnset_RaisesMsg16950()
        => new Simulation().AssertSqlError("""
            declare @u cursor;
            declare @v int;
            fetch next from @u into @v;
            """, 16950);

    [TestMethod]
    public void CursorVariable_SetFromNamedCursor_SharesPosition()
        => AreEqual(2, ExecuteScalar<int>("""
            create table t (id int primary key);
            insert t values (1),(2),(3);
            declare cn cursor local scroll for select id from t order by id;
            open cn;
            declare @c cursor; set @c = cn;
            declare @v int;
            fetch next from @c into @v;
            fetch next from cn into @v;
            select @v
            """));

    [TestMethod]
    public void CursorVariable_ProcOutputParam_CallerFetches()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key)",
            "insert t values (10),(20),(30)",
            "create procedure p_getcur @oc cursor varying output as begin set @oc = cursor local scroll for select id from t order by id; open @oc; end");
        using var conn = sim.CreateOpenConnection();
        AreEqual(10, (int)conn.CreateCommand("""
            declare @c cursor;
            exec p_getcur @oc = @c output;
            declare @v int;
            fetch next from @c into @v;
            select @v
            """).ExecuteScalar()!);
    }

    // ---- FOR UPDATE OF ----

    [TestMethod]
    public void ForUpdateOf_ColumnNotInList_RaisesMsg16932()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, a int, b int);
            insert t values (1,10,100);
            declare c cursor for select id,a,b from t for update of a;
            open c;
            declare @id int,@a int,@b int; fetch next from c into @id,@a,@b;
            update t set b = 999 where current of c;
            """, 16932);

    [TestMethod]
    public void ForUpdateOf_ColumnInList_Updates()
        => AreEqual(111, ExecuteScalar<int>("""
            create table t (id int primary key, a int, b int);
            insert t values (1,10,100);
            declare c cursor for select id,a,b from t for update of a;
            open c;
            declare @id int,@a int,@b int; fetch next from c into @id,@a,@b;
            update t set a = 111 where current of c;
            close c; deallocate c;
            select a from t where id = 1
            """));

    [TestMethod]
    public void FastForwardCursor_PositionedUpdate_RaisesMsg16929()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, a int);
            insert t values (1,10);
            declare ff cursor fast_forward for select id,a from t;
            open ff;
            declare @i int,@x int; fetch next from ff into @i,@x;
            update t set a = 1 where current of ff;
            """, 16929);

    // ---- TYPE_WARNING ----

    [TestMethod]
    public void TypeWarning_DowngradedCursor_EmitsMsg16956()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        var messages = new List<SimulatedError>();
        conn.InfoMessage += (_, e) =>
        {
            foreach (var err in e.Errors)
                messages.Add(err);
        };
        _ = conn.CreateCommand("create table t (id int, a int); insert t values (1,10),(2,10);").ExecuteNonQuery();
        _ = conn.CreateCommand("declare c cursor dynamic type_warning for select distinct a from t").ExecuteNonQuery();
        HasCount(1, messages);
        AreEqual(16956, messages[0].Number);
        AreEqual("The created cursor is not of the requested type.", messages[0].Message);
    }

    [TestMethod]
    public void TypeWarning_NoDowngrade_NoMessage()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        var count = 0;
        conn.InfoMessage += (_, _) => count++;
        _ = conn.CreateCommand("create table t (id int primary key, a int); insert t values (1,10);").ExecuteNonQuery();
        _ = conn.CreateCommand("declare c cursor dynamic type_warning for select id,a from t").ExecuteNonQuery();
        AreEqual(0, count);
    }

    // ---- OPTIMISTIC ----

    [TestMethod]
    public void Optimistic_RowModifiedOutOfBand_PositionedUpdateRaises16947()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global optimistic for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();
        _ = c2.CreateCommand("update t set a = 999 where id = 1").ExecuteNonQuery();
        var ex = Throws<DbException>(() => c1.CreateCommand("update t set a = 111 where current of c").ExecuteNonQuery());
        AreEqual("16947", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Optimistic_RowModifiedOutOfBand_PositionedDeleteRaises16947()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global optimistic for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();
        _ = c2.CreateCommand("update t set a = 888 where id = 1").ExecuteNonQuery();
        var ex = Throws<DbException>(() => c1.CreateCommand("delete from t where current of c").ExecuteNonQuery());
        AreEqual("16947", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Optimistic_NoOutOfBandChange_PositionedUpdateSucceeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20);");
        using var c1 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global optimistic for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();
        _ = c1.CreateCommand("update t set a = 111 where current of c").ExecuteNonQuery();
        AreEqual(111, (int)c1.CreateCommand("select a from t where id = 1").ExecuteScalar()!);
    }

    [TestMethod]
    public void Optimistic_RowVersionTable_DetectsConflict()
    {
        // Detection basis when the table carries a rowversion column: the
        // rowversion bump (part of the stored bytes) trips the conflict check.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int, rv rowversion); insert t (id,a) values (1,10),(2,20);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global optimistic for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();
        _ = c2.CreateCommand("update t set a = 999 where id = 1").ExecuteNonQuery();
        var ex = Throws<DbException>(() => c1.CreateCommand("update t set a = 111 where current of c").ExecuteNonQuery());
        AreEqual("16947", ex.Data["HelpLink.EvtID"]);
    }

    // ---- SCROLL_LOCKS ----

    [TestMethod]
    public async Task ScrollLocks_HeldRow_BlocksWriter_Msg1222()
    {
        // The scroll-lock U on the fetched row conflicts with a second
        // connection's row-X; LOCK_TIMEOUT 0 makes the conflict deterministic.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20),(3,30);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global scroll scroll_locks for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();

        var ex = await Task.Run(() =>
            Throws<DbException>(() =>
                c2.CreateCommand("set lock_timeout 0; update t set a = 5 where id = 1").ExecuteNonQuery()),
            TestContext.CancellationToken);
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void ScrollLocks_UnlockedRow_WriterProceeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20),(3,30);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global scroll scroll_locks for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();
        // A row the cursor is NOT sitting on is writable.
        AreEqual(1, c2.CreateCommand("set lock_timeout 0; update t set a = 5 where id = 3").ExecuteNonQuery());
    }

    [TestMethod]
    public async Task ScrollLocks_ScrollAway_ReleasesPriorRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20),(3,30);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global scroll scroll_locks for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery(); // id=1 locked
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery(); // moved to id=2, id=1 released

        // id=1 is now writable; id=2 blocks.
        AreEqual(1, c2.CreateCommand("set lock_timeout 0; update t set a = 5 where id = 1").ExecuteNonQuery());
        var ex = await Task.Run(() =>
            Throws<DbException>(() =>
                c2.CreateCommand("set lock_timeout 0; update t set a = 5 where id = 2").ExecuteNonQuery()),
            TestContext.CancellationToken);
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void ScrollLocks_Close_ReleasesLock()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int); insert t values (1,10),(2,20);");
        using var c1 = sim.CreateOpenConnection();
        using var c2 = sim.CreateOpenConnection();
        _ = c1.CreateCommand("declare c cursor global scroll scroll_locks for select id,a from t order by id for update; open c;").ExecuteNonQuery();
        _ = c1.CreateCommand("fetch next from c").ExecuteNonQuery();
        _ = c1.CreateCommand("close c; deallocate c").ExecuteNonQuery();
        // Lock released → writer proceeds.
        AreEqual(1, c2.CreateCommand("set lock_timeout 0; update t set a = 5 where id = 1").ExecuteNonQuery());
    }

    // ---- FOR SYSTEM_TIME sources ----

    private const string TemporalSeed = """
        create table s (
            id int not null primary key,
            v int not null,
            Vf datetime2 generated always as row start hidden not null,
            Vt datetime2 generated always as row end hidden not null,
            period for system_time (Vf, Vt)
        ) with (system_versioning = on (history_table = dbo.sHistory));
        insert s (id, v) values (1,10),(2,20),(3,30);
        update s set v = 111 where id = 1;
        """;

    /// <summary>
    /// Every <c>FOR SYSTEM_TIME</c> form is a read-only snapshot on real SQL
    /// Server — probed against SQL Server 2025, where
    /// <c>sys.dm_exec_cursors(@@SPID).properties</c> reports
    /// <c>Snapshot | Read Only</c> with the row count for <c>AS OF</c>,
    /// <c>ALL</c>, <c>BETWEEN</c>, <c>FROM … TO</c> and <c>CONTAINED IN</c>
    /// alike, and adding <c>SCROLL</c> changes nothing. A temporal read mixes
    /// the base heap with the history sibling, so there is no live set to
    /// navigate. <c>@@CURSOR_ROWS</c> is therefore positive, never <c>-1</c>.
    /// </summary>
    [TestMethod]
    [DataRow("select id, v from s for system_time all", 4)]
    [DataRow("select id, v from s for system_time as of '2099-01-01'", 3)]
    [DataRow("select id, v from s for system_time between '2000-01-01' and '2099-01-01'", 4)]
    [DataRow("select id, v from s for system_time from '2000-01-01' to '2099-01-01'", 4)]
    [DataRow("select id, v from s for system_time contained in ('2000-01-01', '2099-01-01')", 1)]
    public void TemporalSourceCursor_IsAReadOnlySnapshot(string query, int cursorRows)
        => AreEqual(cursorRows, new Simulation().ExecuteScalar<int>($"{TemporalSeed} declare c cursor for {query}; open c; select @@cursor_rows"));

    /// <summary>Being a snapshot makes it read-only, so positioned DML through
    /// a <c>FOR SYSTEM_TIME</c> cursor is Msg 16929 — probe-confirmed
    /// verbatim, class 16 state 1.</summary>
    [TestMethod]
    public void TemporalSourceCursor_PositionedUpdateIsReadOnly()
        => new Simulation().AssertSqlError($"""
            {TemporalSeed}
            declare @id int, @v int;
            declare c cursor for select id, v from s for system_time as of '2099-01-01';
            open c; fetch next from c into @id, @v;
            update s set v = 5 where current of c;
            """, 16929, "The cursor is READ ONLY.");

    /// <summary>An <c>AS OF</c> read is time-fixed: a mid-loop UPDATE to the
    /// base row doesn't change what the cursor returns.</summary>
    [TestMethod]
    public void TemporalAsOfCursor_IgnoresMidLoopUpdates()
        => AreEqual("111;20;30;", new Simulation().ExecuteScalar($$"""
            {{TemporalSeed}}
            declare @id int, @v int, @log varchar(100) = '';
            declare c cursor for select id, v from s for system_time as of '2099-01-01';
            open c; fetch next from c into @id, @v;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar, @v) + ';';
              update s set v = 999 where id = 3;
              fetch next from c into @id, @v;
            end
            select @log
            """));

    /// <summary>A cursor over a temporal table <em>without</em> a
    /// <c>FOR SYSTEM_TIME</c> clause reads the current rows through the base
    /// heap, so it stays DYNAMIC (<c>@@CURSOR_ROWS = -1</c>) and positioned DML
    /// works — probe-confirmed <c>Dynamic | Optimistic</c>.</summary>
    [TestMethod]
    public void CurrentTimeCursorOverTemporalTable_IsDynamicAndUpdatable()
        => AreEqual("-1|8888", new Simulation().ExecuteScalar($"""
            {TemporalSeed}
            declare @id int, @v int, @rows int;
            declare c cursor for select id, v from s order by id;
            open c;
            set @rows = @@cursor_rows;
            fetch next from c into @id, @v;
            update s set v = 8888 where current of c;
            close c; deallocate c;
            select convert(varchar, @rows) + '|' + convert(varchar, (select v from s where id = 1))
            """));

    /// <summary><c>DYNAMIC TYPE_WARNING</c> over a <c>FOR SYSTEM_TIME</c>
    /// source fires Msg 16956 — the request was converted to a snapshot
    /// (probe-confirmed).</summary>
    [TestMethod]
    public void TypeWarning_TemporalSource_EmitsMsg16956()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        var messages = new List<SimulatedError>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add(error);
        };
        _ = connection.CreateCommand(TemporalSeed).ExecuteNonQuery();
        _ = connection.CreateCommand("declare c cursor dynamic type_warning for select id, v from s for system_time all").ExecuteNonQuery();
        HasCount(1, messages);
        AreEqual(16956, messages[0].Number);
    }
}
