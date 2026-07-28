using System.Data.Common;
using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>WAITFOR DELAY</c>. The simulator blocks the calling thread on
/// a cancellable wait, matching real SQL Server's "blocks the connection"
/// semantics while staying interruptible by a command cancel. To keep CI fast,
/// only one test actually sleeps a non-trivial duration; everything else
/// exercises parsing / dispatch / skip-mode / error paths with a
/// <c>'00:00:00'</c> operand. Behavior probed against SQL Server 2025
/// (2026-05-11).
/// </summary>
[TestClass]
public sealed class WaitForDelayTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Delay_Zero_StringLiteral_ReturnsImmediately()
        => _ = new Simulation().ExecuteNonQuery("waitfor delay '00:00:00'");

    [TestMethod]
    public void Delay_EmptyString_ReturnsImmediately()
    {
        // Probe-confirmed: empty string is silently accepted as zero delay.
        _ = new Simulation().ExecuteNonQuery("waitfor delay ''");
    }

    [TestMethod]
    public void Delay_Variable_String_ReturnsImmediately()
        => _ = new Simulation().ExecuteNonQuery(
            "declare @t varchar(20) = '00:00:00'; waitfor delay @t");

    [TestMethod]
    public void Delay_Variable_NullValue_ReturnsImmediately()
    {
        // Probe-confirmed: a NULL-valued variable is silently accepted as
        // zero delay (distinct from the bare NULL literal which is a Msg 156
        // syntax error at parse time — different code path).
        _ = new Simulation().ExecuteNonQuery(
            "declare @t varchar(20); waitfor delay @t");
    }

    /// <summary>
    /// The only test that actually sleeps. ~50ms is well under the user's
    /// 100ms ceiling; the assertion checks the sleep was at least nearly
    /// the requested span (giving 10ms of OS scheduler slack on the lower
    /// bound) and not absurdly long.
    /// </summary>
    [TestMethod]
    public void Delay_50ms_ActuallySleeps()
    {
        var start = Stopwatch.GetTimestamp();
        _ = new Simulation().ExecuteNonQuery("waitfor delay '00:00:00.050'");
        var elapsed = Stopwatch.GetElapsedTime(start);
        IsGreaterThanOrEqualTo(40, elapsed.TotalMilliseconds,
            $"Expected ≥40ms sleep, got {elapsed.TotalMilliseconds}ms");
        IsLessThan(2000, elapsed.TotalMilliseconds,
            $"Expected well under a misparsed-magnitude sleep, got {elapsed.TotalMilliseconds}ms");
    }

    /// <summary>
    /// In-process <c>Cancel()</c> from another thread interrupts a running
    /// <c>WAITFOR DELAY</c> — the same abort machinery the TDS attention path
    /// drives, exposed through the ADO surface. The 30-second wait aborts
    /// promptly and surfaces the cancelled-command exception (<b>Msg 0</b>,
    /// what real SqlClient manufactures for an attention — probe-confirmed
    /// against SqlClient 7.0.2); the connection stays usable afterwards.
    /// </summary>
    [TestMethod]
    public void Delay_InterruptedByInProcessCancel_ReturnsPromptly()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "waitfor delay '00:00:30'";
        // Retry the cancel until ExecuteNonQuery returns: a cancel landing
        // before the execution scope opens targets the prior scope's token
        // and is dropped, so a single timer-fired cancel can miss on a
        // stalled runner and let the 30-second wait run to completion.
        var done = false;
        var canceller = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                Thread.Sleep(100);
                command.Cancel();
            }
        });

        var start = Stopwatch.GetTimestamp();
        canceller.Start();
        var cancelled = Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());
        Volatile.Write(ref done, true);
        canceller.Join();
        var elapsed = Stopwatch.GetElapsedTime(start);

        AreEqual(0, cancelled.Number);
        IsLessThan(10000, elapsed.TotalMilliseconds,
            $"Expected the cancel to interrupt the 30s wait promptly, got {elapsed.TotalMilliseconds}ms");

        using var probe = connection.CreateCommand();
        probe.CommandText = "select 42";
        AreEqual(42, probe.ExecuteScalar());
    }

    [TestMethod]
    public void Delay_InUntakenIf_DoesNotSleep()
    {
        // The string operand here would otherwise sleep 10 seconds — verify
        // skip-mode suppresses the actual sleep.
        var start = Stopwatch.GetTimestamp();
        _ = new Simulation().ExecuteNonQuery("if 1=0 waitfor delay '00:00:10'");
        var elapsed = Stopwatch.GetElapsedTime(start);
        IsLessThan(2000, elapsed.TotalMilliseconds,
            $"Expected skip well under the guarded 10s sleep, got {elapsed.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void Delay_AfterReturn_DoesNotSleep()
    {
        var start = Stopwatch.GetTimestamp();
        _ = new Simulation().ExecuteNonQuery("return; waitfor delay '00:00:10'");
        var elapsed = Stopwatch.GetElapsedTime(start);
        IsLessThan(2000, elapsed.TotalMilliseconds,
            $"Expected skip well under the guarded 10s sleep, got {elapsed.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void Delay_AfterBreak_DoesNotSleep()
    {
        var start = Stopwatch.GetTimestamp();
        _ = new Simulation().ExecuteNonQuery("""
            while 1=1
            begin
                break;
                waitfor delay '00:00:10';
            end
            """);
        var elapsed = Stopwatch.GetElapsedTime(start);
        IsLessThan(2000, elapsed.TotalMilliseconds,
            $"Expected skip well under the guarded 10s sleep, got {elapsed.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void Delay_ResetsRowCountToZero()
    {
        using var reader = new Simulation().ExecuteReader("""
            select 1 union all select 2 union all select 3;
            waitfor delay '00:00:00';
            select @@rowcount as rc
            """);
        while (reader.Read()) { }
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void Delay_MalformedString_Msg148()
        => new Simulation().AssertSqlError(
            "waitfor delay 'not a time'", 148,
            "Incorrect time syntax in time string 'not a time' used with WAITFOR.");

    [TestMethod]
    public void Delay_Over24h_Msg148()
        => new Simulation().AssertSqlError("waitfor delay '25:00:00'", 148);

    [TestMethod]
    public void Delay_NegativeString_Msg148()
        => new Simulation().AssertSqlError("waitfor delay '-00:00:01'", 148);

    [TestMethod]
    public void Delay_DayComponent_Msg148()
        => new Simulation().AssertSqlError("waitfor delay '99:00:00:00'", 148);

    [TestMethod]
    public void Delay_BadVariableValue_Msg148()
        => new Simulation().AssertSqlError(
            "declare @t varchar(20) = 'bogus'; waitfor delay @t", 148,
            "Incorrect time syntax in time string 'bogus' used with WAITFOR.");

    [TestMethod]
    public void Delay_TimeTypedVariable_Msg9815()
        => new Simulation().AssertSqlError(
            "declare @t time = '00:00:00.100'; waitfor delay @t", 9815,
            "Waitfor delay and waitfor time cannot be of type time.");

    [TestMethod]
    public void Delay_IntegerLiteralOperand_SyntaxError()
    {
        // Real SQL Server raises Msg 102 here; the simulator routes through
        // the same Msg-102 factory (SyntaxErrorNear).
        var ex = Throws<DbException>(
            () => _ = new Simulation().ExecuteNonQuery("waitfor delay 1"));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Delay_BareNullLiteralOperand_SyntaxError()
    {
        // Real SQL Server: Msg 156 "Incorrect syntax near the keyword 'null'."
        // Simulator: Msg 102 (NULL falls through to the operand's
        // SyntaxErrorNear catch-all). Wording differs but rejection is
        // consistent — a hand-typed `waitfor delay null` is a programmer
        // error either way.
        var ex = Throws<DbException>(
            () => _ = new Simulation().ExecuteNonQuery("waitfor delay null"));
        var num = ex.Data["HelpLink.EvtID"] as string;
        IsTrue(num is "102" or "156", $"Expected Msg 102 or 156, got {num}");
    }

    [TestMethod]
    public void Delay_CastOperand_SyntaxError()
        => _ = Throws<DbException>(
            () => _ = new Simulation().ExecuteNonQuery(
                "waitfor delay cast('00:00:00.050' as time)"));

    [TestMethod]
    public void WaitforTime_NotSupported()
        => _ = Throws<NotSupportedException>(
            () => _ = new Simulation().ExecuteNonQuery("waitfor time '23:59:59'"));

    [TestMethod]
    public void Delay_BetweenSelects_BothRun()
    {
        using var reader = new Simulation().ExecuteReader(
            "select 'before' as v; waitfor delay '00:00:00'; select 'after' as v");
        IsTrue(reader.Read());
        AreEqual("before", reader.GetString(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("after", reader.GetString(0));
    }

    [TestMethod]
    public void Delay_InWhile_RunsEachIteration()
    {
        // Three iterations, each waiting 0 — exercises dispatch in the loop
        // body. The loop completes in nominal time.
        var start = Stopwatch.GetTimestamp();
        _ = new Simulation().ExecuteNonQuery("""
            declare @i int = 0;
            while @i < 3
            begin
                set @i = @i + 1;
                waitfor delay '00:00:00';
            end
            """);
        var elapsed = Stopwatch.GetElapsedTime(start);
        IsLessThan(2000, elapsed.TotalMilliseconds,
            $"Expected fast loop, got {elapsed.TotalMilliseconds}ms");
    }

    /// <summary>
    /// A <see cref="CancellationToken"/> cancelled <em>mid-execution</em>
    /// surfaces the same Msg 0 as an explicit <c>Cancel()</c> — the ADO.NET
    /// base class registers the token to call <c>Cancel()</c>, so both reach
    /// the engine's abort the same way. Real SqlClient throws rather than
    /// handing back an empty reader / zero rows, so a caller can't mistake a
    /// cancelled batch for a legitimately empty answer.
    /// </summary>
    [TestMethod]
    public async Task Delay_CancelledTokenMidExecution_ThrowsMsg0()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        await connection.OpenAsync(TestContext.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "waitfor delay '00:00:30'; select 42";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var cancelled = await ThrowsAsync<SimulatedSqlException>(async () =>
        {
            using var reader = await command.ExecuteReaderAsync(cts.Token);
        });

        AreEqual(0, cancelled.Number);

        using var probe = connection.CreateCommand();
        probe.CommandText = "select 42";
        AreEqual(42, probe.ExecuteScalar());
    }

    /// <summary>
    /// A token already cancelled <em>before</em> execute keeps the ADO.NET
    /// base class's <see cref="TaskCanceledException"/> — real SqlClient
    /// behaves identically, so only the mid-execution case routes to Msg 0.
    /// </summary>
    [TestMethod]
    public async Task PreCancelledToken_StaysTaskCanceled()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        await connection.OpenAsync(TestContext.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "select 1";
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await ThrowsAsync<TaskCanceledException>(async () =>
        {
            using var reader = await command.ExecuteReaderAsync(cts.Token);
        });
    }

    /// <summary>
    /// <c>CommandTimeout</c> expiry aborts the batch and surfaces <b>Msg -2</b>
    /// (Class 11 / State 0) — SqlClient's own manufactured timeout exception,
    /// distinct from the Msg 0 a caller-driven cancel produces. Probed against
    /// SqlClient 7.0.2 / SQL Server 2025.
    /// </summary>
    // Sequential phase: a timeout can't be expressed in under a second
    // (CommandTimeout is whole seconds), and WAITFOR blocks the calling
    // thread for that whole second. Several of those running concurrently
    // starve the threadpool, which surfaces as failures in whichever test
    // was waiting on a thread — LockingTests carries the reciprocal note.
    [TestMethod]
    [DoNotParallelize]
    [DataRow("waitfor delay '00:00:10'")]
    [DataRow("waitfor delay '00:00:10'; select 42")]
    public void CommandTimeout_Expiry_RaisesMinus2(string sql)
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1;

        var timeout = Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());

        AreEqual(-2, timeout.Number);
        AreEqual(11, timeout.Class);
        AreEqual(0, timeout.State);
    }

    /// <summary>
    /// A caller-driven cancel keeps Msg 0 even with a timeout armed — the two
    /// causes stay distinguishable, which is the whole point of tracking them
    /// separately.
    /// </summary>
    [TestMethod]
    public void ExplicitCancel_KeepsMsg0_EvenWithTimeoutArmed()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "waitfor delay '00:00:20'";
        command.CommandTimeout = 30;

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

    /// <summary>
    /// <c>CommandTimeout = 0</c> is infinite, the SqlClient convention
    /// (probe-confirmed: a wait longer than any finite default completes).
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CommandTimeout_Zero_IsInfinite()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "waitfor delay '00:00:01'";
        command.CommandTimeout = 0;

        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// The default matches SqlClient's 30 seconds — a batch under it runs
    /// untouched.
    /// </summary>
    [TestMethod]
    public void CommandTimeout_DefaultsToThirtySeconds()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();

        AreEqual(30, command.CommandTimeout);
    }

    /// <summary>
    /// After a timeout the connection stays usable and an open transaction
    /// **survives** — probe-confirmed against SQL Server 2025, which reports
    /// <c>@@TRANCOUNT = 1</c> and serves the next command normally (the same
    /// shape as a cancel under the default <c>SET XACT_ABORT OFF</c>).
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CommandTimeout_LeavesConnectionUsableAndTransactionOpen()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "begin tran; waitfor delay '00:00:10'";
            command.CommandTimeout = 1;
            _ = Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());
        }

        using var probe = connection.CreateCommand();
        probe.CommandText = "select @@TRANCOUNT";
        AreEqual(1, probe.ExecuteScalar());
    }
}
