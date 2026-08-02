using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Cursors whose query carries an ORDER BY: whether an index delivers that
/// order decides the sensitivity. Real SQL Server keeps the cursor DYNAMIC
/// when a scan can read the order out of a key and converts it to KEYSET when
/// the plan has to sort (probed against SQL Server 2025 via
/// <c>sys.dm_exec_cursors(@@SPID).properties</c>, which reports
/// <c>Dynamic</c> with <c>@@CURSOR_ROWS = -1</c> in the first case and
/// <c>Keyset</c> with the row count in the second).
/// </summary>
[TestClass]
public sealed class CursorOrderCoverageTests
{
    /// <summary>
    /// <c>clu</c> is clustered on <c>id</c> with a nonclustered index on
    /// <c>v</c> and an unindexed <c>w</c>; <c>comp</c> carries a composite
    /// <c>(a, b)</c>; <c>hp</c> is a heap whose only index is on <c>v</c>;
    /// <c>edge</c> reaches its keys through a UNIQUE constraint, a filtered
    /// index and a disabled one.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table clu (id int primary key clustered, v int not null, w int not null);
            create index ix_clu_v on clu (v);
            insert clu values (1,30,300),(2,20,200),(3,10,100);
            create table comp (id int primary key clustered, a int not null, b int not null, c int not null);
            create index ix_comp_ab on comp (a, b);
            insert comp values (1,1,1,1),(2,1,2,2),(3,2,1,3);
            create table hp (id int not null, v int not null, w int not null);
            create index ix_hp_v on hp (v);
            insert hp values (1,30,300),(2,20,200),(3,10,100);
            create table dsc (id int primary key, d int not null);
            create index ix_dsc_d on dsc (d desc);
            insert dsc values (1,10),(2,20),(3,30);
            create table edge (id int primary key nonclustered, u int not null unique, f int null, g int null);
            create index ix_edge_f on edge (f) where f > 0;
            create index ix_edge_g on edge (g);
            insert edge values (1,10,1,1),(2,20,2,2),(3,30,3,3);
            alter index ix_edge_g on edge disable;
            """,
            "create view vclu as select id, v, w from clu;",
            "create view vren as select id, v as vv, w as ww from clu;");
        return simulation;
    }

    private static int CursorRows(string query) =>
        Seeded().ExecuteScalar<int>($"declare c cursor for {query}; open c; select @@cursor_rows");

    // ---- which orders an index delivers ----

    /// <summary>
    /// Probe-confirmed coverage matrix. <c>-1</c> is DYNAMIC (an index reads
    /// the order out), a positive count is the KEYSET the conversion lands on.
    /// A key's columns are matched as a leading prefix in one direction — a
    /// clustered or nonclustered key, forward or backward, a composite prefix,
    /// and the clustering key a nonclustered index carries after its own
    /// (<c>ORDER BY v, id</c>) all read in order, while an unindexed column,
    /// an out-of-order or over-long composite, and mixed directions all sort.
    /// The counts match real throughout; the heap's two sorting rows are the
    /// one place the <em>type</em> behind the count differs, since real's
    /// keyset needs a unique index and converts to a snapshot without one.
    /// </summary>
    [TestMethod]
    [DataRow("select id, v, w from clu", -1)]
    [DataRow("select id, v, w from clu order by id", -1)]
    [DataRow("select id, v, w from clu order by id desc", -1)]
    [DataRow("select id, v, w from clu order by v", -1)]
    [DataRow("select id, v, w from clu order by v desc", -1)]
    [DataRow("select id, v, w from clu order by v, id", -1)]
    [DataRow("select id, v, w from clu order by id, v", -1)]
    [DataRow("select id, v, w from clu order by w", 3)]
    [DataRow("select id, v, w from clu order by v, w", 3)]
    [DataRow("select a, b, c from comp order by a", -1)]
    [DataRow("select a, b, c from comp order by a, b", -1)]
    [DataRow("select a, b, c from comp order by a desc, b desc", -1)]
    [DataRow("select a, b, c from comp order by a desc", -1)]
    [DataRow("select a, b, c from comp order by a, b desc", 3)]
    [DataRow("select a, b, c from comp order by b, a", 3)]
    [DataRow("select a, b, c from comp order by b", 3)]
    [DataRow("select a, b, c from comp order by a, b, c", 3)]
    [DataRow("select id, v, w from hp order by v", -1)]
    [DataRow("select id, v, w from hp order by w", 3)]
    [DataRow("select id, v, w from hp order by id", 3)]
    [DataRow("select id, d from dsc order by d", -1)]
    [DataRow("select id, d from dsc order by d desc", -1)]
    [DataRow("select id, u, f from edge order by id", -1)]
    [DataRow("select id, u, f from edge order by u", -1)]
    [DataRow("select id, u, f from edge order by u, f", -1)]
    [DataRow("select id, u, f from edge order by f", 3)]
    [DataRow("select id, u, g from edge order by g", 3)]
    public void OrderResolvesToProbedSensitivity(string query, int cursorRows)
        => AreEqual(cursorRows, CursorRows(query));

    /// <summary>A WHERE clause doesn't change what the ORDER BY needs: the
    /// order is read from an index or sorted regardless of which column the
    /// predicate filters on (probe-confirmed both ways).</summary>
    [TestMethod]
    [DataRow("select id, v, w from clu where w = 100 order by id", -1)]
    [DataRow("select id, v, w from clu where v = 10 order by w", 1)]
    public void WhereClauseDoesNotDecideTheOrder(string query, int cursorRows)
        => AreEqual(cursorRows, CursorRows(query));

    /// <summary>
    /// The item is resolved down to the base column it reads: a positional
    /// ordinal, an output alias, a view's renamed column. A term reading no
    /// column at all constrains no order and drops out, so
    /// <c>ORDER BY (select null), v</c> is decided by <c>v</c> alone — all
    /// probe-confirmed.
    /// </summary>
    [TestMethod]
    [DataRow("select id, v, w from clu order by 2", -1)]
    [DataRow("select id, v, w from clu order by 3", 3)]
    [DataRow("select id, v as vv, w from clu order by vv", -1)]
    [DataRow("select id, v, w as ww from clu order by ww", 3)]
    [DataRow("select id, vv, ww from vren order by vv", -1)]
    [DataRow("select id, vv, ww from vren order by ww", 3)]
    [DataRow("select id, v, w from vclu order by v", -1)]
    [DataRow("select id, v, w from vclu order by w", 3)]
    [DataRow("select d.id, d.v from (select id, v from clu) d order by d.v", -1)]
    [DataRow("select d.id, d.w from (select id, w from clu) d order by d.w", 3)]
    [DataRow("with k as (select id, w from clu) select id, w from k order by w", 3)]
    [DataRow("select id, v, w from clu order by (select null), v", -1)]
    [DataRow("select id, v, w from clu order by (select null), w", 3)]
    public void OrderItemResolvesThroughAliasesAndBodies(string query, int cursorRows)
        => AreEqual(cursorRows, CursorRows(query));

    /// <summary>
    /// Across a join the order is read run by run: an index on either side's
    /// column delivers its own run, and a run that another source's items
    /// follow has to be unique so the next source decides the rest
    /// (probe-confirmed — <c>a.id, b.id</c> over two primary keys stays
    /// DYNAMIC, <c>a.id, b.c</c> sorts).
    /// </summary>
    [TestMethod]
    [DataRow("select a.id, b.id from clu a join comp b on a.id = b.id order by a.id", -1)]
    [DataRow("select a.id, b.id from clu a join comp b on a.id = b.id order by a.id, b.id", -1)]
    [DataRow("select a.id, b.id from clu a join comp b on a.id = b.id order by a.w", 3)]
    [DataRow("select a.id, b.id from clu a join comp b on a.id = b.id order by b.c", 3)]
    [DataRow("select a.id, b.id from clu a cross join comp b order by a.id, b.id", -1)]
    [DataRow("select a.id, b.id from clu a cross join comp b order by a.id, b.c", 9)]
    public void JoinOrderResolvesRunByRun(string query, int cursorRows)
        => AreEqual(cursorRows, CursorRows(query));

    // ---- what the conversion changes ----

    /// <summary>
    /// The conversion reaches every request that would otherwise be DYNAMIC —
    /// the bare forward-only default and an explicit <c>DYNAMIC</c>, with or
    /// without <c>SCROLL</c> — and leaves the rest alone, since KEYSET is
    /// already what it lands on. An index-delivered order converts none of
    /// them. Probe-confirmed pair by pair.
    /// </summary>
    [TestMethod]
    [DataRow("", "order by w", 3)]
    [DataRow("", "order by v", -1)]
    [DataRow("dynamic", "order by w", 3)]
    [DataRow("dynamic", "order by v", -1)]
    [DataRow("scroll dynamic", "order by w", 3)]
    [DataRow("scroll dynamic", "order by v", -1)]
    [DataRow("keyset", "order by w", 3)]
    [DataRow("keyset", "order by v", 3)]
    [DataRow("scroll", "order by w", 3)]
    [DataRow("scroll", "order by v", 3)]
    [DataRow("static", "order by w", 3)]
    public void SensitivityKeywordsDecideWhatTheOrderConverts(string declaration, string order, int cursorRows)
        => AreEqual(cursorRows, Seeded().ExecuteScalar<int>(
            $"declare c cursor {declaration} for select id, v, w from clu {order}; open c; select @@cursor_rows"));

    /// <summary>
    /// The converted cursor is an ordinary KEYSET: membership is frozen at
    /// OPEN so a row inserted into the window mid-loop never appears, values
    /// are re-read live, and a deleted member reports
    /// <c>@@FETCH_STATUS = -2</c>. Probe-confirmed against real, which walks
    /// exactly this sequence.
    /// </summary>
    [TestMethod]
    public void ConvertedCursorHasKeysetSemantics()
        => AreEqual("1/100/0;2/222/0;0/0/-2;", new Simulation().ExecuteScalar("""
            create table t (id int primary key, w int not null, val int not null);
            insert t values (1,10,100),(2,20,200),(3,30,300);
            declare @id int, @w int, @val int, @log varchar(200) = '';
            declare c cursor dynamic for select id, w, val from t order by w;
            open c;
            fetch next from c into @id, @w, @val;
            set @log = @log + convert(varchar, @id) + '/' + convert(varchar, @val) + '/' + convert(varchar, @@fetch_status) + ';';
            insert t values (4,15,400);
            update t set val = 222 where id = 2;
            delete from t where id = 3;
            fetch next from c into @id, @w, @val;
            set @log = @log + convert(varchar, @id) + '/' + convert(varchar, @val) + '/' + convert(varchar, @@fetch_status) + ';';
            set @id = 0; set @val = 0;
            fetch next from c into @id, @w, @val;
            set @log = @log + convert(varchar, @id) + '/' + convert(varchar, @val) + '/' + convert(varchar, @@fetch_status) + ';';
            close c; deallocate c;
            select @log
            """));

    /// <summary>An index-delivered order stays DYNAMIC through and through: a
    /// row inserted mid-loop ahead of the position shows up.</summary>
    [TestMethod]
    public void IndexDeliveredOrderStaysDynamic()
        => AreEqual("10;15;20;30;", new Simulation().ExecuteScalar("""
            create table t (id int primary key, w int not null);
            create index ix_t_w on t (w);
            insert t values (1,10),(2,20),(3,30);
            declare @id int, @w int, @log varchar(200) = '';
            declare c cursor dynamic for select id, w from t order by w;
            open c;
            fetch next from c into @id, @w;
            set @log = @log + convert(varchar, @w) + ';';
            insert t values (4,15);
            while @@fetch_status = 0
            begin
              fetch next from c into @id, @w;
              if @@fetch_status = 0 set @log = @log + convert(varchar, @w) + ';';
            end
            close c; deallocate c;
            select @log
            """));

    /// <summary>The conversion doesn't make the cursor scrollable — a bare
    /// forward-only cursor over an unindexed order still reports Msg 16911 for
    /// a scrolling direction, exactly as the row-limited conversion does.</summary>
    [TestMethod]
    public void ConvertedCursorIsStillForwardOnly()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, w int not null);
            insert t values (1,10);
            declare c cursor for select id, w from t order by w;
            open c;
            fetch prior from c;
            """, 16911);

    /// <summary>Positioned DML reaches the base row through the converted
    /// cursor, which is updatable like any KEYSET.</summary>
    [TestMethod]
    public void ConvertedCursorSupportsPositionedUpdate()
        => AreEqual(8888, new Simulation().ExecuteScalar<int>("""
            create table t (id int primary key, w int not null, val int not null);
            insert t values (1,10,100),(2,20,200);
            declare @id int, @w int, @val int;
            declare c cursor for select id, w, val from t order by w;
            open c;
            fetch next from c into @id, @w, @val;
            update t set val = 8888 where current of c;
            close c; deallocate c;
            select val from t where id = 1
            """));

    // ---- TYPE_WARNING ----

    /// <summary>
    /// <c>TYPE_WARNING</c> reports Msg 16956 exactly when the requested type
    /// was converted. An unindexed ORDER BY converts a DYNAMIC request — and
    /// the DYNAMIC a bare or <c>FORWARD_ONLY</c> cursor implies — while a
    /// KEYSET request, the KEYSET a plain <c>SCROLL</c> implies, and a
    /// <c>STATIC</c> / <c>FAST_FORWARD</c> one all get what they asked for.
    /// An index-delivered order converts nothing. All probe-confirmed.
    /// </summary>
    [TestMethod]
    [DataRow("dynamic", "order by w", 1)]
    [DataRow("", "order by w", 1)]
    [DataRow("forward_only", "order by w", 1)]
    [DataRow("keyset", "order by w", 0)]
    [DataRow("scroll", "order by w", 0)]
    [DataRow("static", "order by w", 0)]
    [DataRow("fast_forward", "order by w", 0)]
    [DataRow("dynamic", "order by v", 0)]
    [DataRow("", "order by v", 0)]
    public void TypeWarningFiresOnlyWhenTheRequestWasConverted(string declaration, string order, int expected)
    {
        using var connection = Seeded().CreateDbConnection();
        connection.Open();
        var messages = new List<SimulatedError>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add(error);
        };
        _ = connection.CreateCommand(
            $"declare c cursor {declaration} type_warning for select id, v, w from clu {order}").ExecuteNonQuery();
        HasCount(expected, messages);
        if (expected > 0)
        {
            AreEqual(16956, messages[0].Number);
            AreEqual("The created cursor is not of the requested type.", messages[0].Message);
        }
    }

    /// <summary>A plain <c>SCROLL</c> cursor implies KEYSET, so a shape that
    /// converts all the way to a snapshot warns it too — the request the
    /// keywords imply counts, not only one spelled out (probe-confirmed).</summary>
    [TestMethod]
    public void TypeWarningFiresForAnImpliedKeysetConvertedToASnapshot()
    {
        using var connection = Seeded().CreateDbConnection();
        connection.Open();
        var messages = new List<SimulatedError>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add(error);
        };
        _ = connection.CreateCommand("declare c cursor scroll type_warning for select distinct v from clu").ExecuteNonQuery();
        HasCount(1, messages);
        AreEqual(16956, messages[0].Number);
    }
}
