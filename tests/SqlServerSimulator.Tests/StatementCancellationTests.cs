using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A cancellation observed <em>inside</em> a running statement — a join whose
/// fold would run for minutes, a scan whose every row calls a looping scalar
/// function, a recursion without a limit — rather than at a statement
/// boundary. Probed 2026-09-27 against SQL Server 2025 through SqlClient 7.0.2
/// with the same nine-way cross product and a one-second
/// <c>CommandTimeout</c>: Msg -2, no <c>CATCH</c>, the interrupted statement
/// rolled back, an open transaction kept under <c>XACT_ABORT OFF</c> and
/// rolled back under <c>ON</c>; and 2026-09-30 for the single-source loops,
/// the Msg 3621 an interrupted write sends, and what the next command sees.
/// </summary>
// Sequential, like the WAITFOR timeout tests: each one occupies a thread for
// its whole timeout, and several at once starve the pool.
[TestClass]
[DoNotParallelize]
public sealed class StatementCancellationTests
{
    /// <summary>
    /// A cross product of nine ten-row tables whose WHERE no tuple passes and
    /// no seek, hash or reorder can shortcut: a billion tuples, each one
    /// evaluated.
    /// </summary>
    private const string LongJoin = """
        select count(*) from pa a, pa b, pa c, pa d, pa e, pa f, pa g, pa h, pa i
        where a.x + b.x + c.x + d.x + e.x + f.x + g.x + h.x + i.x < 0
        """;

    private static SimulatedDbConnection Open()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Run(connection, "create table pa (x int); insert pa values (1), (2), (3), (4), (5), (6), (7), (8), (9), (10); create table logt (v int)");
        return connection;
    }

    private static void Run(SimulatedDbConnection connection, string sql, int timeout = 30)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        _ = command.ExecuteNonQuery();
    }

    private static object? Scalar(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    public void Timeout_InterruptsALongJoin()
    {
        using var connection = Open();
        var elapsed = Stopwatch.StartNew();

        var timeout = Throws<SimulatedSqlException>(() => Run(connection, LongJoin, timeout: 1));

        AreEqual(-2, timeout.Number);
        AreEqual(11, timeout.Class);
        IsLessThan(10, elapsed.Elapsed.TotalSeconds);
    }

    [TestMethod]
    public void Timeout_InsideTry_IsNotCaught()
    {
        using var connection = Open();

        var timeout = Throws<SimulatedSqlException>(() => Run(connection, $"begin try {LongJoin} end try begin catch insert logt values (1) end catch", timeout: 1));

        AreEqual(-2, timeout.Number);
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Timeout_RollsBackTheStatement_AndKeepsTheTransaction()
    {
        using var connection = Open();

        _ = Throws<SimulatedSqlException>(() => Run(connection, $"begin tran; insert logt values (1); insert logt {LongJoin}", timeout: 1));

        AreEqual(1, Scalar(connection, "select @@trancount"));
        AreEqual(1, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Timeout_UnderXactAbort_RollsBackTheTransaction()
    {
        using var connection = Open();

        _ = Throws<SimulatedSqlException>(() => Run(connection, $"set xact_abort on; begin tran; insert logt values (1); {LongJoin}", timeout: 1));

        AreEqual(0, Scalar(connection, "select @@trancount"));
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Cancel_InterruptsALongJoin_AsMsg0()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = LongJoin;
        command.CommandTimeout = 0;

        var done = false;
        var canceller = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                Thread.Sleep(100);
                command.Cancel();
            }
        });
        canceller.Start();
        var cancelled = Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());
        Volatile.Write(ref done, true);
        canceller.Join();

        AreEqual(0, cancelled.Number);
    }

    private const string SlowFunction = "create function dbo.slow(@x int) returns int as begin declare @i int = 0; while @i < 2000 set @i += 1; return @i + @x end";

    /// <summary>
    /// <see cref="Open"/> plus a thousand-row <c>big</c>, a trigger target
    /// <c>tt</c>, and <c>dbo.slow</c>, whose loop makes one call cost more than
    /// any test waits, so a statement calling it per row is certainly still
    /// running when the cancel lands.
    /// </summary>
    private static SimulatedDbConnection OpenWithSlowFunction()
    {
        var connection = Open();
        Run(connection, "create table big (v int); insert big select value from generate_series(1, 1000); create table tt (v int)");
        Run(connection, SlowFunction);
        return connection;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> with no timeout while another thread cancels
    /// it every 50 ms until it returns — a cancel landing before execution
    /// begins is lost, so one timer-fired cancel could miss — and hands back
    /// what it raised.
    /// </summary>
    private static SimulatedSqlException Interrupted(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        var done = false;
        var canceller = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                Thread.Sleep(50);
                command.Cancel();
            }
        });
        canceller.Start();
        try
        {
            return Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());
        }
        finally
        {
            Volatile.Write(ref done, true);
            canceller.Join();
        }
    }

    private static int[] Numbers(SimulatedSqlException error) => [.. error.Errors.Select(static e => e.Number)];

    [TestMethod]
    public void Timeout_InterruptsARecursionWithoutALimit()
    {
        using var connection = Open();
        var elapsed = Stopwatch.StartNew();

        var timeout = Throws<SimulatedSqlException>(() => Run(connection, "with r as (select 1 n union all select n + 1 from r) select count(*) from r option (maxrecursion 0)", timeout: 1));

        AreEqual(-2, timeout.Number);
        IsLessThan(10, elapsed.Elapsed.TotalSeconds);
        AreEqual("0,0", Scalar(connection, "select concat(@@error, ',', @@rowcount)"));
    }

    /// <summary>
    /// Two billion generated rows, none of which passes: nothing but the row
    /// loop's own poll can stop it.
    /// </summary>
    [TestMethod]
    public void Cancel_InsideASingleSourceRowLoop_EndsTheStatement()
    {
        using var connection = Open();
        var elapsed = Stopwatch.StartNew();

        var cancelled = Interrupted(connection, "select count(*) from generate_series(1, 2000000000) where value < 0");

        CollectionAssert.AreEqual(new[] { 0 }, Numbers(cancelled));
        IsLessThan(10, elapsed.Elapsed.TotalSeconds);
    }

    [TestMethod]
    public void Cancel_InsideAFunctionCalledPerRow_EndsTheWriteWithMsg3621()
    {
        using var connection = OpenWithSlowFunction();

        var cancelled = Interrupted(connection, "insert logt select dbo.slow(v) from big");

        CollectionAssert.AreEqual(new[] { 0, 3621 }, Numbers(cancelled));
        AreEqual(1, cancelled.Errors[1].LineNumber);
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Cancel_InsideAScan_EndsTheBatch_AndLeavesRowCountAndErrorAtZero()
    {
        using var connection = OpenWithSlowFunction();

        var cancelled = Interrupted(connection, "insert logt values (1); select count(*) from big where dbo.slow(v) < 0; insert logt values (2)");

        CollectionAssert.AreEqual(new[] { 0 }, Numbers(cancelled));
        AreEqual("0,0", Scalar(connection, "select concat(@@error, ',', @@rowcount)"));
        AreEqual(1, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Cancel_InsideAnUpdate_RollsItBack()
    {
        using var connection = OpenWithSlowFunction();

        var cancelled = Interrupted(connection, "update big set v = v + 1 where dbo.slow(v) > 0");

        CollectionAssert.AreEqual(new[] { 0, 3621 }, Numbers(cancelled));
        AreEqual(500500, Scalar(connection, "select sum(v) from big"));
    }

    [TestMethod]
    public void Cancel_InsideATrigger_RollsBackTheFiringStatement_WithoutMsg3621()
    {
        using var connection = OpenWithSlowFunction();
        Run(connection, "create trigger ttr on tt after insert as begin declare @c int; select @c = count(*) from big where dbo.slow(v) < 0 end");

        var cancelled = Interrupted(connection, "insert tt values (1)");

        CollectionAssert.AreEqual(new[] { 0 }, Numbers(cancelled));
        AreEqual(0, Scalar(connection, "select count(*) from tt"));
    }

    [TestMethod]
    public void Cancel_InsideAProcedure_KeepsItsCompletedStatements()
    {
        using var connection = OpenWithSlowFunction();
        Run(connection, "create procedure p as insert logt values (1); declare @c int; select @c = count(*) from big where dbo.slow(v) < 0; insert logt values (2)");

        _ = Interrupted(connection, "exec p");

        AreEqual(1, Scalar(connection, "select count(*) from logt"));
        AreEqual(0, Scalar(connection, "select @@trancount"));
    }

    [TestMethod]
    public void Cancel_InsideAWhileLoop_KeepsTheIterationsThatCompleted()
    {
        using var connection = OpenWithSlowFunction();

        _ = Interrupted(connection, "declare @c int; while 1 = 1 begin insert logt values (1); select @c = count(*) from big where v < 0 end");

        IsGreaterThan(0, (int)Scalar(connection, "select count(*) from logt")!);
    }

    [TestMethod]
    public void Cancel_UnderXactAbort_RollsBackWithoutMsg3621()
    {
        using var connection = OpenWithSlowFunction();

        var cancelled = Interrupted(connection, "set xact_abort on; begin tran; insert logt values (1); insert logt select dbo.slow(v) from big");

        CollectionAssert.AreEqual(new[] { 0 }, Numbers(cancelled));
        AreEqual(0, Scalar(connection, "select @@trancount"));
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
        Run(connection, "set xact_abort off");
    }

    /// <summary>
    /// A write interrupted in a transaction an earlier batch began earns its
    /// Msg 3621; one the interrupted batch began itself doesn't show it, since
    /// SqlClient drops the notice from a response carrying the transaction's
    /// ENVCHANGE.
    /// </summary>
    [TestMethod]
    [DataRow("", "begin tran; insert logt select dbo.slow(v) from big", new[] { 0 })]
    [DataRow("begin tran", "insert logt select dbo.slow(v) from big", new[] { 0, 3621 })]
    [DataRow("begin tran", "begin tran; insert logt select dbo.slow(v) from big", new[] { 0, 3621 })]
    public void Cancel_InAnOpenTransaction_KeepsIt(string before, string interrupted, int[] numbers)
    {
        using var connection = OpenWithSlowFunction();
        if (before.Length > 0)
            Run(connection, before);

        var cancelled = Interrupted(connection, interrupted);

        CollectionAssert.AreEqual(numbers, Numbers(cancelled));
        AreEqual((short)1, Scalar(connection, "select xact_state()"));
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
        Run(connection, "rollback");
    }

    [TestMethod]
    public void Cancel_DeallocatesTheGlobalCursorsItsBatchDeclared()
    {
        using var connection = OpenWithSlowFunction();
        Run(connection, "declare earlier cursor global for select v from big; open earlier");

        _ = Interrupted(connection, "declare later cursor global for select v from big; open later; declare @v int; fetch next from earlier into @v; select @v = count(*) from big where dbo.slow(v) < 0");

        AreEqual((short)-3, Scalar(connection, "select cursor_status('global', 'later')"));
        AreEqual((short)1, Scalar(connection, "select cursor_status('global', 'earlier')"));
    }
}
