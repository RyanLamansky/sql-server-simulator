using System.Globalization;
using System.Text.RegularExpressions;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET STATISTICS IO</c> / <c>TIME</c>: which statements send Msg 3615,
/// 3612 and 3613 and in what order, as SQL Server 2025 does (probed
/// 2026-09-28). The counts and times are the simulator's own.
/// </summary>
[TestClass]
public sealed partial class StatisticsIoTimeTests
{
    private const string Rest = ", physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.";

    [GeneratedRegex(@"CPU time = \d+ ms, ( ?)elapsed time = \d+ ms")]
    private static partial Regex Times();

    /// <summary>
    /// What a batch sent, in order: each message as <c>number Lline [procedure]: text</c>
    /// with times blanked and the IO lines' fixed tail dropped, and each row
    /// of a result set as its values.
    /// </summary>
    private static List<string> Run(SimulatedDbConnection connection, string sql)
    {
        var sent = new List<string>();
        void Collect(object? sender, SimulatedInfoMessageEventArgs e)
        {
            foreach (var error in e.Errors)
            {
                var text = Times().Replace(error.Message.Replace(Rest, "", StringComparison.Ordinal), "CPU time = # ms, $1elapsed time = # ms");
                sent.Add($"{error.Number} L{error.LineNumber}{(error.Procedure.Length == 0 ? "" : " " + error.Procedure)}: {text}");
            }
        }
        connection.InfoMessage += Collect;
        try
        {
            using var reader = connection.CreateCommand(sql).ExecuteReader();
            do
            {
                while (reader.Read())
                    sent.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
            while (reader.NextResult());
        }
        finally
        {
            connection.InfoMessage -= Collect;
        }
        return sent;
    }

    private static SimulatedDbConnection Open(string setup)
    {
        var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(setup).ExecuteNonQuery();
        return connection;
    }

    /// <summary>Compares line lists as one text, so a failure shows both whole.</summary>
    private static void AreSame(IEnumerable<object> expected, IEnumerable<object> actual) =>
        AreEqual(string.Join("\n", expected), string.Join("\n", actual));

    private static string Io(int line, string table, int scans, int reads) => $"3615 L{line}: Table '{table}'. Scan count {scans}, logical reads {reads}";

    [TestMethod]
    public void Io_ScanAndSeeksReportTheirOwnReads()
    {
        using var connection = Open("create table t (id int primary key, v int); insert t values (1, 1), (2, 2), (3, 3); set statistics io on");
        AreEqual(
            string.Join("\n",
                "1 | 1", "2 | 2", "3 | 3", Io(1, "t", 1, 1),
                "2 | 2", Io(2, "t", 0, 1),
                Io(3, "t", 0, 0),
                "1 | 1", "2 | 2", Io(4, "t", 2, 1),
                "1"),
            string.Join("\n", Run(connection, "select * from t\nselect * from t where id = 2\nselect * from t where id = 99\nselect * from t where id in (1, 2)\nselect 1")));
    }

    [TestMethod]
    public void Io_EmptyHeapIsOneScanOfNoPages()
    {
        using var connection = Open("create table h (x int); set statistics io on");
        AreSame([Io(1, "h", 1, 0)], Run(connection, "select * from h"));
    }

    [TestMethod]
    public void Io_TemporaryTablesGoByTheirInternalNames()
    {
        using var connection = Open("create table #tt (x int); declare @t table (x int); set statistics io on");
        var sent = Run(connection, "insert #tt values (1)\ndeclare @v table (x int); insert @v values (1)");
        MatchesRegex(new Regex(@"^3615 L1: Table '#tt_{113}\d{12}'\. Scan count 0, logical reads 1$"), sent[0]);
        MatchesRegex(new Regex(@"^3615 L2: Table '#[0-9A-F]{8}'\. Scan count 0, logical reads 1$"), sent[1]);
    }

    [TestMethod]
    public void Io_OrdersTablesAsRealDoes()
    {
        using var connection = Open("""
            create table a (id int, g int); insert a values (1, 1), (2, 1);
            create table b (id int, g int);
            create table c (id int primary key, g int); insert c values (1, 1);
            set statistics io on
            """);
        AreSame(
            [
                Io(1, "b", 0, 2), Io(1, "a", 1, 1),
                Io(2, "c", 0, 2), Io(2, "Worktable", 0, 0), Io(2, "a", 1, 1),],
            Run(connection, """
                insert b select * from a
                merge c using a on c.id = a.id when matched then update set g = a.g when not matched then insert values (a.id, a.g);
                """));
    }

    /// <summary>
    /// An ON settled false while compiling — EF Core's multi-row insert —
    /// leaves real's MERGE nothing to read in the target: scan count 0 and no
    /// worktable (probed 2026-10-01 against SQL Server 2025). A NOT MATCHED BY
    /// SOURCE clause still has every target row to visit; whether real lists a
    /// worktable beside that scan is its plan's to say (it doesn't for this
    /// one), so that row leaves the worktable unasserted.
    /// </summary>
    [TestMethod]
    [DataRow("on 1 = 0 when not matched then insert values (s.id, s.g)", 0, false)]
    [DataRow("on 0 = 1 and c.g = s.g when not matched then insert values (s.id, s.g)", 0, false)]
    [DataRow("on null = 1 when not matched then insert values (s.id, s.g)", 0, false)]
    [DataRow("on 1 = 0 when not matched by source and c.id = 1 then delete", 1, null)]
    [DataRow("on c.g = s.g when not matched then insert values (s.id, s.g)", 1, true)]
    public void Io_MergeOnAConstantFalseReadsNoTarget(string tail, int scans, bool? worktable)
    {
        using var connection = Open("create table c (id int primary key, g int); insert c values (1, 1), (2, 2); set statistics io on");
        var sent = Run(connection, $"merge c using (values (5, 7), (6, 7)) as s (id, g) {tail};");
        StartsWith($"3615 L1: Table 'c'. Scan count {scans}, ", sent[0]);
        if (worktable is { } listed)
            AreEqual(listed, sent.Any(line => line.Contains("'Worktable'", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("select * from c order by g", true)]
    [DataRow("select * from c order by id desc", false)]
    [DataRow("select top 1 * from a order by g", false)]
    [DataRow("select g, count(*) from a group by g", true)]
    [DataRow("select id, count(*) from c group by id", false)]
    [DataRow("select distinct g from a", true)]
    [DataRow("select distinct id from c", false)]
    [DataRow("select id from a union select id from c", true)]
    [DataRow("select id from a union all select id from c", false)]
    [DataRow("select id, row_number() over (order by g) from c", true)]
    public void Io_SortsAndHashesListAWorktable(string sql, bool worktable)
    {
        using var connection = Open("create table a (id int, g int); insert a values (1, 1); create table c (id int primary key, g int); insert c values (1, 1); set statistics io on");
        AreEqual(worktable, Run(connection, sql).Any(line => line.Contains("'Worktable'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Io_HashJoinListsWorkfileThenWorktable()
    {
        using var connection = Open("create table a (id int); insert a values (1); create table b (id int); insert b values (1); set statistics io on");
        var sent = Run(connection, "select count(*) from a join b on a.id = b.id");
        StartsWith("3615 L1: Table 'Workfile'", sent[1]);
        StartsWith("3615 L1: Table 'Worktable'", sent[2]);
    }

    [TestMethod]
    public void Io_NothingForCatalogViewsFailedStatementsOrWhenOff()
    {
        using var connection = Open("create table t (id int primary key); insert t values (1); set statistics io on");
        AreSame(["0"], Run(connection, "select count(*) from sys.tables where 1 = 0"));
        _ = Throws<SimulatedSqlException>(() => Run(connection, "insert t values (1)"));
        AreSame(["1"], Run(connection, "set statistics io off; select * from t"));
    }

    [TestMethod]
    public void Io_CountsLobPagesRead()
    {
        using var connection = Open("create table h (id int, v varchar(max)); insert h values (1, replicate(cast('x' as varchar(max)), 20000)); set statistics io on");
        var withLob = Run(connection, "select len(v) from h")[1];
        var withoutLob = Run(connection, "select id from h")[1];
        MatchesRegex(new Regex(@"lob logical reads [1-9]\d*,"), withLob);
        AreEqual(Io(1, "h", 1, 1), withoutLob);
    }

    [TestMethod]
    public void Io_ConditionReportsAheadOfItsBody()
    {
        using var connection = Open("create table t (id int primary key); insert t values (1); set statistics io on");
        AreSame(
            [Io(1, "t", 1, 1), "0 L1: yes"],
            Run(connection, "if exists (select * from t) print 'yes'"));
    }

    [TestMethod]
    public void Io_FiringStatementReportsAheadOfItsTrigger()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int primary key); create table l (id int)",
            "create trigger tr on t after insert as insert l select id from inserted");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        _ = connection.CreateCommand("set statistics io on").ExecuteNonQuery();
        AreSame(
            [Io(1, "t", 0, 1), "3615 L1 tr: Table 'l'. Scan count 0, logical reads 1"],
            Run(connection, "insert t values (1)"));
    }

    [TestMethod]
    public void Io_MultiStatementFunctionReportsItsReturnTable()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int primary key); insert t values (1)",
            "create function f() returns @r table (id int) as begin insert @r select id from t; return; end");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        _ = connection.CreateCommand("set statistics io on").ExecuteNonQuery();
        var sent = Run(connection, "select * from f()");
        HasCount(2, sent);
        MatchesRegex(new Regex(@"^3615 L1: Table '#[0-9A-F]{8}'\. Scan count 1, logical reads 1$"), sent[1]);
    }

    [TestMethod]
    public void Io_ProcedureBodySetRevertsAtExit()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int primary key); insert t values (1)",
            "create proc p as set statistics io on; select * from t");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        AreSame(
            ["1", "3615 L1 p: Table 't'. Scan count 1, logical reads 1", "1"],
            Run(connection, "exec p; select * from t"));
    }

    private const string Compile = "SQL Server parse and compile time: \n   CPU time = # ms, elapsed time = # ms.";
    private const string Execution = "\n SQL Server Execution Times:\n   CPU time = # ms,  elapsed time = # ms.";

    [TestMethod]
    public void Time_ReportsTheBatchCompileAndEachStatementClosingWithADone()
    {
        using var connection = Open("create table t (id int primary key); set statistics time on");
        AreSame(
            [
                $"3613 L3: {Compile}",
                $"3612 L1: {Execution}",
                $"3612 L1: {Execution}",
                "0 L2: p",
                $"3612 L2: {Execution}",
                "1",
                $"3612 L3: {Execution}",],
            Run(connection, "declare @x int; declare @y int = 1; set @x = 2\nprint 'p'\nselect 1"));
    }

    [TestMethod]
    public void Time_TheSetsTurningItOnAndOffReportNothing()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        AreSame(
            ["1", $"3612 L1: {Execution}", "2"],
            Run(connection, "set statistics time on; select 1; set statistics time off; select 2"));
    }

    [TestMethod]
    public void Time_SimplyParameterizedStatementCompilesOfItsOwn()
    {
        using var connection = Open("create table t (id int primary key, v int); insert t values (1, 1); set statistics time on");
        AreSame(
            [
                $"3613 L2: {Compile}",
                $"3613 L1: {Compile}", "1 | 1", $"3612 L1: {Execution}",
                "1 | 1", $"3612 L2: {Execution}",],
            Run(connection, "select * from t where id = 1\nselect * from t"));
    }

    [TestMethod]
    public void Time_ProcedureCompilesOnEveryCall()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("\n-- lead\ncreate proc p as\nselect 5");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        _ = connection.CreateCommand("set statistics time on").ExecuteNonQuery();
        string[] expected = [$"3613 L1: {Compile}", $"3613 L4 p: {Compile}", "5", $"3612 L4 p: {Execution}", $"3612 L1: {Execution}"];
        AreSame(expected, Run(connection, "exec p"));
        AreSame(expected, Run(connection, "exec p"));
    }

    [TestMethod]
    public void Time_ModuleCreateIsAttributedToTheModule()
    {
        using var connection = Open("set statistics time on");
        AreSame(
            [$"3613 L1 v: {Compile}", $"3612 L1 v: {Execution}"],
            Run(connection, "create view v as select 1 a"));
    }

    /// <summary>
    /// The failed statement reports its time after its error and ahead of
    /// Msg 3621, and <c>ExecuteReader</c>'s exception carries the rest of the
    /// batch's messages, the next statement's time included (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Time_FailedStatementReportsAfterItsErrorAheadOfMsg3621()
    {
        using var connection = Open("create table t (id int primary key); insert t values (1); set statistics time on");
        var error = Throws<SimulatedSqlException>(() => Run(connection, "insert t values (1); select 'next'"));
        AreEqual("2627,3612,3621,3612", string.Join(",", error.Errors.Cast<SimulatedError>().Select(static e => e.Number)));
    }

    [TestMethod]
    public void UserOptions_ListsBothSwitches() =>
        AreEqual(2, new Simulation().ExecuteScalar("""
            set statistics io on; set statistics time on;
            declare @o table (o nvarchar(128), v nvarchar(128));
            insert @o exec('dbcc useroptions');
            select count(*) from @o where o in ('statistics io', 'statistics time')
            """));
}
