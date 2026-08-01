using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Cursors whose query limits rows — <c>TOP n</c>, <c>TOP n PERCENT</c>,
/// <c>TOP n WITH TIES</c>, <c>OFFSET … FETCH</c> — written on the cursor's own
/// statement or inside a body it follows (view, derived table, CTE). Real SQL
/// Server converts these to KEYSET rather than to a read-only snapshot
/// (probed against SQL Server 2025 via
/// <c>sys.dm_exec_cursors(@@SPID).properties</c>, which reports
/// <c>Keyset</c> with the limited row count): the limit picks membership at
/// OPEN, and later FETCHes re-read the frozen key set live.
/// </summary>
[TestClass]
public sealed class CursorRowLimitTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int primary key, v int not null); insert t values (1,10),(2,20),(3,30),(4,40),(5,50);",
            "create view vtop as select top 3 id, v from t order by id;",
            "create view voffset as select id, v from t order by id offset 1 rows fetch next 2 rows only;",
            "create view vplain as select id, v from t;");
        return simulation;
    }

    // ---- sensitivity + membership count ----

    /// <summary>
    /// Every row-limited shape resolves to KEYSET, whose <c>@@CURSOR_ROWS</c>
    /// is the <em>limited</em> count — not <c>-1</c> (DYNAMIC) and not the
    /// unlimited count. Probe-confirmed row by row against real SQL Server;
    /// <c>top 50 percent</c> of five rows is three (the cap is a ceiling), and
    /// <c>top 2 with ties</c> over <c>v / 20</c> admits a third tying row.
    /// </summary>
    [TestMethod]
    [DataRow("select top 3 id, v from t order by id", 3)]
    [DataRow("select top 3 id, v from t", 3)]
    [DataRow("select top (3) id, v from t order by id", 3)]
    [DataRow("select top 50 percent id, v from t order by id", 3)]
    [DataRow("select top 2 with ties id, v from t order by v / 20", 3)]
    [DataRow("select id, v from t order by id offset 1 rows fetch next 2 rows only", 2)]
    [DataRow("select id, v from t order by id offset 3 rows", 2)]
    [DataRow("select id, v from vtop", 3)]
    [DataRow("select id, v from voffset", 2)]
    [DataRow("select d.id, d.v from (select top 3 id, v from t order by id) d", 3)]
    [DataRow("with k as (select top 3 id, v from t order by id) select id, v from k", 3)]
    [DataRow("select d.id, d.v from (select id, v from vtop) d", 3)]
    [DataRow("select top 2 id, v from vplain order by id", 2)]
    public void RowLimitedShapeIsKeysetWithTheLimitedCount(string query, int cursorRows)
        => AreEqual(cursorRows, Seeded().ExecuteScalar<int>($"declare c cursor for {query}; open c; select @@cursor_rows"));

    /// <summary>The limit also bounds what the loop walks — admitting the shape
    /// without applying it would return the unlimited rowset.</summary>
    [TestMethod]
    [DataRow("select top 3 id from t order by id", "1;2;3;")]
    [DataRow("select id from t order by id offset 1 rows fetch next 2 rows only", "2;3;")]
    [DataRow("select top 2 with ties id from t order by v / 20", "1;2;3;")]
    [DataRow("select id from vtop", "1;2;3;")]
    [DataRow("select d.id from (select top 2 id from t order by id desc) d", "4;5;")]
    public void RowLimitedCursorWalksOnlyTheLimitedRows(string query, string expected)
        => AreEqual(expected, Seeded().ExecuteScalar($$"""
            declare @id int, @log varchar(100) = '';
            declare c cursor for {{query}};
            open c; fetch next from c into @id;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar, @id) + ';';
              fetch next from c into @id;
            end
            select @log
            """));

    /// <summary>An APPLY right side carrying its own <c>TOP</c> converts the
    /// cursor to KEYSET too (probe-confirmed) and the limit runs per left row,
    /// so the cursor walks one partner each.</summary>
    [TestMethod]
    public void ApplyBodyWithTop_LimitsPerLeftRow()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table a (id int primary key); create table b (id int primary key, a_id int not null, w int not null);",
            "insert a values (1),(2); insert b values (100,1,1),(101,1,2),(200,2,3);");
        AreEqual("1/1;2/3;", simulation.ExecuteScalar("""
            declare @id int, @w int, @log varchar(100) = '';
            declare c cursor for select a.id, x.w from a cross apply (select top 1 w from b where b.a_id = a.id order by w) x;
            open c; fetch next from c into @id, @w;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar, @id) + '/' + convert(varchar, @w) + ';';
              fetch next from c into @id, @w;
            end
            select @log
            """));
    }

    /// <summary>A <c>TOP</c> operand may be a variable, which resolves against
    /// the batch that OPENs — the same per-execution rule the read path
    /// follows.</summary>
    [TestMethod]
    public void TopVariableOperand_ResolvesAtOpen()
        => AreEqual(2, Seeded().ExecuteScalar<int>("""
            declare @n int = 2;
            declare c cursor for select top (@n) id from t order by id;
            open c; select @@cursor_rows
            """));

    // ---- scrollability ----

    /// <summary>
    /// The conversion to KEYSET doesn't make a bare cursor scrollable: a
    /// cursor naming no sensitivity is forward-only whatever it resolved to,
    /// so a scrolling direction is Msg 16911 — and ABSOLUTE reports 16911 too
    /// here (16925 is dynamic-sensitivity only). Probe-confirmed.
    /// </summary>
    [TestMethod]
    [DataRow("prior", "fetch: The fetch type prior cannot be used with forward only cursors.")]
    [DataRow("absolute 2", "fetch: The fetch type absolute cannot be used with forward only cursors.")]
    public void BareRowLimitedCursor_StaysForwardOnly(string direction, string message)
        => Seeded().AssertSqlError($"""
            declare @id int;
            declare c cursor for select top 3 id from t order by id;
            open c;
            fetch {direction} from c into @id;
            """, 16911, message);

    /// <summary>With SCROLL the row-limited cursor is a scrollable KEYSET, so
    /// ABSOLUTE positions within the limited membership (probe-confirmed:
    /// <c>fetch absolute 3</c> over <c>top 4 … order by id</c> lands on
    /// id 3).</summary>
    [TestMethod]
    public void ScrollRowLimitedCursor_AbsoluteWorks()
        => AreEqual(3, Seeded().ExecuteScalar<int>("""
            declare @id int;
            declare c cursor scroll for select top 4 id from t order by id;
            open c;
            fetch absolute 3 from c into @id;
            select @id
            """));

    /// <summary>An explicit STATIC request over a row-limited query stays a
    /// read-only snapshot (probe-confirmed <c>Snapshot | Read Only</c>), so
    /// positioned DML is Msg 16929.</summary>
    [TestMethod]
    public void StaticRequestOverRowLimit_StaysReadOnlySnapshot()
        => Seeded().AssertSqlError("""
            declare @id int;
            declare c cursor static for select top 3 id from t order by id;
            open c; fetch next from c into @id;
            update t set v = 0 where current of c;
            """, 16929, "The cursor is READ ONLY.");

    // ---- frozen membership, live values ----

    /// <summary>Membership is frozen at OPEN: a row inserted mid-loop that
    /// would have been inside the window never appears.</summary>
    [TestMethod]
    public void MidLoopInsert_IsInvisibleToTheKeyset()
        => AreEqual("1;2;3;", Seeded().ExecuteScalar("""
            declare @id int, @log varchar(100) = '';
            declare c cursor for select top 3 id from t order by id;
            open c; fetch next from c into @id;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar, @id) + ';';
              if @id = 1 insert t values (0, 5);
              fetch next from c into @id;
            end
            select @log
            """));

    /// <summary>A value change to a non-identity column shows through on the
    /// next FETCH — the keyset re-reads the live row.</summary>
    [TestMethod]
    public void NonKeyUpdateMidLoop_IsVisible()
        => AreEqual(4444, Seeded().ExecuteScalar<int>("""
            declare @id int, @v int;
            declare c cursor for select top 3 id, v from t order by id;
            open c;
            fetch next from c into @id, @v;
            update t set v = 4444 where id = 2;
            fetch next from c into @id, @v;
            select @v
            """));

    /// <summary>A member deleted out from under the keyset fetches as
    /// <c>@@FETCH_STATUS = -2</c>, and the loop resumes past the hole.</summary>
    [TestMethod]
    public void DeletedMember_FetchesMinusTwoThenLoopResumes()
        => AreEqual("-2:0;", Seeded().ExecuteScalar("""
            declare @id int, @log varchar(100) = '';
            declare c cursor for select top 3 id from t order by id;
            open c;
            fetch next from c into @id;
            delete t where id = 2;
            fetch next from c into @id;
            set @log = convert(varchar, @@fetch_status) + ':';
            fetch next from c into @id;
            set @log = @log + convert(varchar, @@fetch_status) + ';';
            select @log
            """));

    /// <summary>Changing a member's unique-key column unmakes the match, so the
    /// fetch reports <c>-2</c> — the keyset tracks the unique index, as on
    /// real.</summary>
    [TestMethod]
    public void KeyColumnUpdateMidLoop_FetchesMinusTwo()
        => AreEqual(-2, Seeded().ExecuteScalar<int>("""
            declare @id int;
            declare c cursor for select top 3 id from t order by id;
            open c;
            fetch next from c into @id;
            update t set id = 33 where id = 2;
            fetch next from c into @id;
            select @@fetch_status
            """));

    /// <summary>
    /// A statement-level limit is <em>not</em> re-applied per FETCH: a member
    /// pushed out of the window by a mid-loop insert still fetches with status
    /// 0 and its live values. Probe-confirmed for the statement, a derived
    /// table and a CTE alike (real inlines those, so the limit lands on the
    /// statement either way).
    /// </summary>
    [TestMethod]
    [DataRow("select top 3 id, v from t order by id")]
    [DataRow("select d.id, d.v from (select top 3 id, v from t order by id) d")]
    [DataRow("with k as (select top 3 id, v from t order by id) select id, v from k")]
    public void MemberPushedOutOfATransparentWindow_StillFetches(string query)
        => AreEqual("0:3", Seeded().ExecuteScalar($"""
            declare @id int, @v int;
            declare c cursor for {query};
            open c;
            fetch next from c into @id, @v;
            fetch next from c into @id, @v;
            insert t values (0, 5);
            fetch next from c into @id, @v;
            select convert(varchar, @@fetch_status) + ':' + convert(varchar, @id)
            """));

    /// <summary>
    /// A limit inside a <em>view</em> body is the exception: real re-evaluates
    /// it on every FETCH, so a member the view no longer returns reports
    /// <c>-2</c> even though its base row still exists. Probe-confirmed for a
    /// TOP view, an OFFSET/FETCH view, and a TOP view read through a derived
    /// table (the view is the fence, and it composes outward).
    /// </summary>
    [TestMethod]
    [DataRow("select id, v from vtop")]
    [DataRow("select d.id, d.v from (select id, v from vtop) d")]
    public void MemberPushedOutOfAViewsWindow_FetchesMinusTwo(string query)
        => AreEqual(-2, Seeded().ExecuteScalar<int>($"""
            declare @id int, @v int;
            declare c cursor for {query};
            open c;
            fetch next from c into @id, @v;
            fetch next from c into @id, @v;
            insert t values (0, 5);
            fetch next from c into @id, @v;
            select @@fetch_status
            """));

    /// <summary>The OFFSET/FETCH view behaves the same way — the fence is the
    /// view, not the particular limiting clause.</summary>
    [TestMethod]
    public void MemberPushedOutOfAnOffsetViewsWindow_FetchesMinusTwo()
        => AreEqual(-2, Seeded().ExecuteScalar<int>("""
            declare @id int, @v int;
            declare c cursor for select id, v from voffset;
            open c;
            fetch next from c into @id, @v;
            insert t values (0, 5);
            fetch next from c into @id, @v;
            select @@fetch_status
            """));

    // ---- positioned DML ----

    /// <summary>A row-limited cursor is updatable, so positioned DML reaches
    /// the base row (probe-confirmed).</summary>
    [TestMethod]
    public void PositionedUpdate_ThroughRowLimitedCursor()
        => AreEqual(7777, Seeded().ExecuteScalar<int>("""
            declare @id int;
            declare c cursor for select top 3 id from t order by id;
            open c;
            fetch next from c into @id;
            fetch next from c into @id;
            update t set v = 7777 where current of c;
            close c; deallocate c;
            select v from t where id = 2
            """));

    [TestMethod]
    public void PositionedDelete_ThroughRowLimitedCursor()
        => AreEqual(4, Seeded().ExecuteScalar<int>("""
            declare @id int;
            declare c cursor for select top 3 id from t order by id;
            open c;
            fetch next from c into @id;
            delete from t where current of c;
            close c; deallocate c;
            select count(*) from t
            """));

    /// <summary>
    /// Positioned DML while the cursor sits on a keyset hole (the last FETCH
    /// reported <c>-2</c>) is Msg 16947 + Msg 3621 — the row the statement
    /// means is gone. Real splits this from the Msg 16931 an <em>unpositioned</em>
    /// cursor reports before the first FETCH or past the end; probe-confirmed
    /// on an explicit KEYSET cursor and on the row-limited conversion alike.
    /// </summary>
    [TestMethod]
    [DataRow("select top 3 id, v from t order by id")]
    [DataRow("select id, v from t order by id")]
    public void PositionedUpdateOnAKeysetHole_IsMsg16947(string query)
        => Seeded().AssertSqlError($"""
            declare @id int, @v int;
            declare c cursor keyset for {query};
            open c;
            fetch next from c into @id, @v;
            delete t where id = 2;
            fetch next from c into @id, @v;
            update t set v = 1 where current of c;
            """, 16947, "No rows were updated or deleted.\nThe statement has been terminated.");

    /// <summary>Past the end the cursor isn't on a hole, just unpositioned, so
    /// it stays Msg 16931 (probe-confirmed).</summary>
    [TestMethod]
    public void PositionedUpdatePastTheEnd_IsMsg16931()
        => Seeded().AssertSqlError("""
            declare @id int;
            declare c cursor for select top 3 id from t order by id;
            open c;
            while @@fetch_status = 0 fetch next from c into @id;
            update t set v = 1 where current of c;
            """, 16931, "There are no rows in the current fetch buffer.");

    /// <summary>A positioned write through a view whose body carries the limit
    /// names the view, exactly as through any other view.</summary>
    [TestMethod]
    public void PositionedUpdate_ThroughARowLimitedView()
        => AreEqual(8888, Seeded().ExecuteScalar<int>("""
            declare @id int, @v int;
            declare c cursor for select id, v from vtop;
            open c;
            fetch next from c into @id, @v;
            update vtop set v = 8888 where current of c;
            close c; deallocate c;
            select v from t where id = 1
            """));

    // ---- TYPE_WARNING ----

    /// <summary>
    /// <c>DYNAMIC TYPE_WARNING</c> over a row-limited query fires Msg 16956 —
    /// the request was downgraded to KEYSET — while <c>KEYSET TYPE_WARNING</c>
    /// stays silent, since KEYSET is exactly what it got. Both
    /// probe-confirmed, on the statement-level and view-body limits alike.
    /// </summary>
    [TestMethod]
    [DataRow("dynamic", "select top 3 id from t order by id", 1)]
    [DataRow("dynamic", "select id, v from vtop", 1)]
    [DataRow("keyset", "select top 3 id from t order by id", 0)]
    [DataRow("keyset", "select id, v from vtop", 0)]
    public void TypeWarning_FiresOnlyWhenTheRequestWasDowngraded(string requested, string query, int expected)
    {
        using var connection = Seeded().CreateDbConnection();
        connection.Open();
        var messages = new List<SimulatedError>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add(error);
        };
        _ = connection.CreateCommand($"declare c cursor {requested} type_warning for {query}").ExecuteNonQuery();
        HasCount(expected, messages);
        if (expected > 0)
        {
            AreEqual(16956, messages[0].Number);
            AreEqual("The created cursor is not of the requested type.", messages[0].Message);
        }
    }
}
