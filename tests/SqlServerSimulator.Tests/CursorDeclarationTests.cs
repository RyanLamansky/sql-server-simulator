using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The declaration grammar's refusals, cursor OUTPUT parameters, and the
/// <c>sp_cursor_list</c> / <c>sp_describe_cursor</c> family — probed
/// 2026-09-29 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CursorDeclarationTests
{
    private const string Seed = "create table t (id int primary key, v int); insert t values (1, 10), (2, 20);";

    /// <summary>
    /// Contradicting options are Msg 1048 naming the pair in real's order,
    /// raised while the batch compiles, so nothing in it runs.
    /// </summary>
    [TestMethod]
    [DataRow("cursor static keyset", "", "STATIC and KEYSET")]
    [DataRow("cursor dynamic keyset", "", "KEYSET and DYNAMIC")]
    [DataRow("cursor local global", "", "LOCAL and GLOBAL")]
    [DataRow("cursor forward_only scroll", "", "SCROLL and FORWARD_ONLY")]
    [DataRow("cursor scroll fast_forward", "", "SCROLL and FAST_FORWARD")]
    [DataRow("cursor fast_forward optimistic", "", "FAST_FORWARD and OPTIMISTIC")]
    [DataRow("cursor read_only optimistic", "", "OPTIMISTIC and READ_ONLY")]
    [DataRow("cursor scroll_locks optimistic", "", "SCROLL_LOCKS and OPTIMISTIC")]
    [DataRow("cursor static", " for update", "FOR UPDATE and STATIC")]
    [DataRow("cursor fast_forward", " for update", "FAST_FORWARD and FOR UPDATE")]
    [DataRow("cursor scroll_locks", " for read only", "READ_ONLY and SCROLL_LOCKS")]
    [DataRow("insensitive cursor", " for update", "FOR UPDATE and INSENSITIVE")]
    [DataRow("cursor fast_forward static scroll_locks", "", "STATIC and FAST_FORWARD")]
    public void ConflictingOptions_Msg1048(string options, string tail, string pair)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Seed);
        simulation.AssertSqlError($"insert t values (3, 30);\ndeclare c {options} for select id from t{tail}", 1048, $"Conflicting cursor options {pair}.");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>The SQL-92 prefix takes no T-SQL option after CURSOR (Msg
    /// 1049), INSENSITIVE belongs to that prefix alone (Msg 153), and
    /// READ_ONLY beside FOR READ ONLY is Msg 1058.</summary>
    [TestMethod]
    [DataRow("declare c scroll cursor keyset for select id from t", 1049)]
    [DataRow("declare c insensitive cursor local for select id from t", 1049)]
    [DataRow("declare c cursor insensitive for select id from t", 153)]
    [DataRow("declare @c cursor; set @c = cursor INSENSITIVE for select id from t", 153)]
    [DataRow("declare c cursor read_only for select id from t for read only", 1058)]
    public void DeclarationGrammarRefusals(string declaration, int number)
        => _ = new Simulation().AssertSqlError(Seed + declaration, number);

    /// <summary>Real reports Msg 1048 at the token after the declaration, or
    /// at its last line when the batch ends there.</summary>
    [TestMethod]
    [DataRow("select 1\ndeclare c cursor static keyset for select 1 a\nopen c", 3)]
    [DataRow("select 1\ndeclare c cursor forward_only\n  static\n  keyset for select 1 a", 4)]
    public void ConflictingOptions_ReportAfterTheDeclaration(string batch, int line)
        => AreEqual(line, new Simulation().AssertSqlError(batch, 1048).LineNumber);

    /// <summary>The SQL-92 INSENSITIVE cursor is a forward-only snapshot
    /// unless SCROLL is written too.</summary>
    [TestMethod]
    public void Sql92Insensitive_IsForwardOnly()
        => _ = new Simulation().AssertSqlError(Seed + "declare c insensitive cursor for select id from t; open c; fetch last from c", 16911);

    /// <summary>
    /// A FOR UPDATE OF list binds against the query's sources while the batch
    /// compiles, whatever the cursor resolves to: an unknown name or an output
    /// alias is Msg 207 and a VALUES column Msg 412, while a column the query
    /// doesn't project, a joined table's, a view's own and a qualified one
    /// all bind.
    /// </summary>
    [TestMethod]
    [DataRow("select id from t for update of nope", 207)]
    [DataRow("select id, v as vv from t for update of vv", 207)]
    [DataRow("select distinct id from t for update of nope", 207)]
    [DataRow("select x from (values (1)) q(x) for update of x", 412)]
    public void ForUpdateOf_BindsAtCompile(string query, int number)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Seed);
        _ = simulation.AssertSqlError($"insert t values (3, 30); if 1 = 0 declare c cursor for {query}", number);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    [DataRow("select id from t for update of v", "update t set v = 5 where current of c")]
    [DataRow("select id from t for update of t.v", "update t set v = 5 where current of c")]
    public void ForUpdateOf_UnprojectedAndQualifiedColumnsBind(string query, string write)
        => AreEqual(5, new Simulation().ExecuteScalar(Seed + $"declare @i int; declare c cursor for {query}; open c; fetch next from c into @i; {write}; select v from t where id = 1"));

    // ---- cursor OUTPUT parameters ----

    private static Simulation WithProcedure(string body)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Seed, "create proc p @c cursor varying output as " + body);
        return simulation;
    }

    /// <summary>Only an open cursor reaches the caller, and only through an
    /// argument written OUTPUT; otherwise the variable stays unallocated.</summary>
    [TestMethod]
    [DataRow("set @c = cursor for select id from t", "exec p @c = @x output", -2)]
    [DataRow("set @c = cursor for select id from t; open @c", "exec p @c = @x", -2)]
    [DataRow("set @c = cursor for select id from t; open @c", "exec p @c = @x output", 1)]
    [DataRow("set @c = cursor for select id from t; open @c", "exec p @x output", 1)]
    public void OutputCursor_ReachesTheCallerOnlyOpenAndThroughOutput(string body, string call, int status)
        => AreEqual((short)status, WithProcedure(body).ExecuteScalar($"declare @x cursor; {call}; select cursor_status('variable', '@x')"));

    /// <summary>A scalar variable passed OUTPUT is Msg 206 and a cursor
    /// variable already holding a cursor Msg 16951, neither running the
    /// body.</summary>
    [TestMethod]
    public void OutputCursor_ArgumentRefusals()
    {
        var simulation = WithProcedure("set @c = cursor for select id from t; open @c; update t set v = 0");
        AreEqual(206, simulation.AssertSqlError("declare @x int; exec p @c = @x output", 206).Number);
        simulation.AssertSqlError("declare @x cursor; set @x = cursor for select 1 a; exec p @c = @x output", 16951, "The variable '@x' cannot be used as a parameter because a CURSOR OUTPUT parameter must not have a cursor allocated to it before execution of the procedure.");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t where v = 0"));
    }

    /// <summary>A cursor parameter needs VARYING OUTPUT, in that order.</summary>
    [TestMethod]
    [DataRow("@c cursor output")]
    [DataRow("@c cursor varying")]
    [DataRow("@c cursor output varying")]
    public void CursorParameter_NeedsVaryingOutput(string parameter)
        => _ = new Simulation().AssertSqlError($"create proc p {parameter} as select 1", 1051);

    /// <summary>DEALLOCATE of an unallocated cursor variable is Msg 16950.</summary>
    [TestMethod]
    public void Deallocate_UnallocatedVariable_Msg16950()
        => _ = new Simulation().AssertSqlError("declare @x cursor; deallocate @x", 16950);

    // ---- sp_cursor_list / sp_describe_cursor ----

    private static string DescribeRow(string setup, string call)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Seed);
        return (string)simulation.ExecuteScalar($"""
            {setup}
            declare @r cursor;
            {call};
            declare @rn sysname, @cn sysname, @sc tinyint, @st int, @mo tinyint, @co tinyint, @scr tinyint, @os tinyint, @cr decimal(10, 0), @fs smallint, @cc smallint, @rc decimal(10, 0), @lo tinyint, @h int, @s varchar(max) = '';
            fetch next from @r into @rn, @cn, @sc, @st, @mo, @co, @scr, @os, @cr, @fs, @cc, @rc, @lo, @h;
            while @@fetch_status = 0
            begin
                set @s += concat(@rn, ',', @cn, ',', @sc, ',', @st, ',', @mo, ',', @co, ',', @scr, ',', @os, ',', @cr, ',', @fs, ',', @cc, ',', @rc, ',', @lo, ';');
                fetch next from @r into @rn, @cn, @sc, @st, @mo, @co, @scr, @os, @cr, @fs, @cc, @rc, @lo, @h;
            end
            select @s
            """)!;
    }

    /// <summary>
    /// Each cursor's scope, status, model (1 static, 2 keyset, 3 dynamic, 4
    /// fast forward), concurrency (1 read-only, 2 scroll locks, 3 optimistic),
    /// scrollability, open state, row count, fetch status (-9 before any),
    /// column count, the last operation's row count and the operation itself.
    /// </summary>
    [TestMethod]
    public void CursorList_DescribesEveryCursor()
        => AreEqual(
            "c1,c1,2,1,1,1,1,1,2,0,1,1,2;c2,c2,2,1,2,3,1,1,2,-9,1,0,1;c3,c3,2,1,3,3,1,1,-1,-1,1,0,2;c4,c4,2,1,4,1,0,1,-1,-9,1,0,1;c5,c5,2,1,3,2,0,1,-1,-9,1,0,1;c6,c6,1,1,1,1,0,1,2,-9,1,0,1;c8,c8,2,1,1,1,0,1,2,-9,1,0,1;",
            DescribeRow(
                """
                declare c1 cursor static for select id from t;
                declare c2 cursor keyset for select id from t;
                declare c3 cursor dynamic for select id from t;
                declare c4 cursor fast_forward for select id from t;
                declare c5 cursor scroll_locks for select id from t;
                declare c6 cursor local forward_only static for select id from t;
                declare c8 cursor for select distinct id from t;
                open c1; open c2; open c3; open c4; open c5; open c6; open c8;
                declare @i int;
                fetch last from c1 into @i;
                fetch next from c3 into @i; fetch next from c3 into @i; fetch next from c3 into @i;
                """,
                "exec sp_cursor_list @cursor_return = @r output, @cursor_scope = 3"));

    /// <summary>The last operation follows the cursor through positioned
    /// writes and CLOSE, which keeps the fetch status.</summary>
    [TestMethod]
    [DataRow("", "c,c,2,-1,2,3,1,0,0,-9,2,0,0;")]
    [DataRow("open c; fetch next from c into @i, @v; update t set v = 5 where current of c;", "c,c,2,1,2,3,1,1,2,0,2,1,4;")]
    [DataRow("open c; fetch next from c into @i, @v; delete t where current of c;", "c,c,2,1,2,3,1,1,2,0,2,1,5;")]
    [DataRow("open c; fetch next from c into @i, @v; close c;", "c,c,2,-1,2,3,1,0,0,0,2,0,6;")]
    public void DescribeCursor_TracksTheLastOperation(string steps, string expected)
        => AreEqual(expected, DescribeRow("declare @i int, @v int; declare c cursor keyset for select id, v from t; " + steps, "exec sp_describe_cursor @r output, N'global', N'c'"));

    /// <summary>A cursor a variable's SET built is named by that variable, at
    /// local scope, whichever variable it is described through.</summary>
    [TestMethod]
    public void DescribeCursor_VariableCursor()
        => AreEqual("@d,@c,1,1,3,3,1,1,-1,-9,1,0,1;", DescribeRow(
            "declare @c cursor, @d cursor; set @c = cursor dynamic for select id from t; open @c; set @d = @c;",
            "exec sp_describe_cursor @r output, N'VARIABLE', N'@d'"));

    /// <summary>
    /// Column flags: 0x2 fixed length, 0x4 nullable as the projection reports
    /// it, 0x10 updatable — never for a read-only cursor, a rowversion or a
    /// computed column, and only for listed columns under FOR UPDATE OF.
    /// </summary>
    [TestMethod]
    public void DescribeCursorColumns_ReportsEachColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, v varchar(10) not null, n nvarchar(20), d decimal(9, 3), rv rowversion, w int);");
        AreEqual("id,0,18,4,56,10,0,1;v,1,16,10,167,0,0,2;n,2,20,40,231,0,0,3;d,3,22,5,106,9,3,4;rv,4,2,8,189,0,0,5;e,5,6,4,56,10,0,-1;", simulation.ExecuteScalar("""
            declare c cursor keyset for select id, v, n, d, rv, id + 1 e from t;
            open c;
            declare @r cursor;
            exec sp_describe_cursor_columns @r output, N'global', N'c';
            declare @cn sysname, @op int, @fl int, @sz int, @ty smallint, @pr tinyint, @scl tinyint, @opos int, @odir nvarchar(1), @hid smallint, @cid int, @oid int, @dbid int, @dbn sysname, @s varchar(max) = '';
            fetch next from @r into @cn, @op, @fl, @sz, @ty, @pr, @scl, @opos, @odir, @hid, @cid, @oid, @dbid, @dbn;
            while @@fetch_status = 0
            begin
                set @s += concat(@cn, ',', @op, ',', @fl, ',', @sz, ',', @ty, ',', @pr, ',', @scl, ',', @cid, ';');
                fetch next from @r into @cn, @op, @fl, @sz, @ty, @pr, @scl, @opos, @odir, @hid, @cid, @oid, @dbid, @dbn;
            end
            select @s
            """));
        AreEqual("v:0;w:22;", simulation.ExecuteScalar("""
            declare k cursor keyset for select v, w from t for update of w;
            open k;
            declare @r cursor;
            exec sp_describe_cursor_columns @r output, N'global', N'k';
            declare @cn sysname, @op int, @fl int, @sz int, @ty smallint, @pr tinyint, @scl tinyint, @opos int, @odir nvarchar(1), @hid smallint, @cid int, @oid int, @dbid int, @dbn sysname, @s varchar(max) = '';
            fetch next from @r into @cn, @op, @fl, @sz, @ty, @pr, @scl, @opos, @odir, @hid, @cid, @oid, @dbid, @dbn;
            while @@fetch_status = 0
            begin
                set @s += concat(@cn, ':', @fl, ';');
                fetch next from @r into @cn, @op, @fl, @sz, @ty, @pr, @scl, @opos, @odir, @hid, @cid, @oid, @dbid, @dbn;
            end
            select @s
            """));
    }

    /// <summary>The tables a cursor's query names — a view as itself — for a
    /// static cursor as much as a keyset one.</summary>
    [TestMethod]
    public void DescribeCursorTables_NamesTablesAndViews()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Seed + "create table u (id int primary key, w int);", "create view vw as select id, v from t");
        AreEqual("dbo.t;dbo.u;dbo.vw;", simulation.ExecuteScalar("""
            declare c cursor static for select t.id, u.w from t join u on u.id = t.id;
            declare d cursor for select id from vw;
            open c; open d;
            declare @r cursor, @o sysname, @n sysname, @h smallint, @l smallint, @sv sysname, @oid int, @dbid int, @dbn sysname, @s varchar(max) = '';
            exec sp_describe_cursor_tables @r output, N'global', N'c';
            fetch next from @r into @o, @n, @h, @l, @sv, @oid, @dbid, @dbn;
            while @@fetch_status = 0
            begin
                set @s += concat(@o, '.', @n, ';');
                fetch next from @r into @o, @n, @h, @l, @sv, @oid, @dbid, @dbn;
            end
            deallocate @r;
            exec sp_describe_cursor_tables @r output, N'global', N'd';
            fetch next from @r into @o, @n, @h, @l, @sv, @oid, @dbid, @dbn;
            set @s += concat(@o, '.', @n, ';');
            select @s
            """));
    }

    /// <summary>The describe procedures' refusals, each from the procedure's
    /// own line, leave the variable unallocated.</summary>
    [TestMethod]
    [DataRow("exec sp_describe_cursor @r output, N'global', N'nope'", 16916, 4)]
    [DataRow("exec sp_describe_cursor_columns @r output, N'bogus', N'c'", 16902, 42)]
    [DataRow("exec sp_describe_cursor_tables @r output, null, N'c'", 16902, 40)]
    [DataRow("exec sp_describe_cursor @r output, N'global', null", 16902, 43)]
    [DataRow("exec sp_describe_cursor @r output, N'variable', N'@q'", 137, 100)]
    public void DescribeCursor_Refusals(string call, int number, int state)
    {
        var ex = new Simulation().AssertSqlError("declare @r cursor; " + call, number);
        AreEqual((byte)state, ex.State);
        Assert.StartsWith("sp_describe_cursor", ex.Errors[0].Procedure);
    }

    /// <summary>An out-of-range scope is an informational message.</summary>
    [TestMethod]
    public void CursorList_InvalidScope_IsInformational()
        => AreEqual((short)-2, new Simulation().ExecuteScalar("declare @r cursor; exec sp_cursor_list @r output, 4; select cursor_status('variable', '@r')"));

    /// <summary>The returned cursor is a scrollable snapshot.</summary>
    [TestMethod]
    public void DescribedCursor_IsAScrollableSnapshot()
        => AreEqual("c:1:1", new Simulation().ExecuteScalar(Seed + """
            declare c cursor keyset for select id from t; open c;
            declare @r cursor, @cn sysname, @op int;
            exec sp_describe_cursor_columns @r output, N'global', N'c';
            declare @rows int = @@cursor_rows;
            fetch last from @r into @cn, @op, @op, @op, @op, @op, @op, @op, @cn, @op, @op, @op, @op, @cn;
            select concat('c:', @rows, ':', cursor_status('variable', '@r'))
            """));
}
