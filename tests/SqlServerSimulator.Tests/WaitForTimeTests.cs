using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>WAITFOR TIME</c>, which waits until the server's clock next
/// reads a time of day — <c>GETDATE()</c>'s clock, UTC in the simulator. The
/// operand grammar and its errors are shared with <c>WAITFOR DELAY</c>
/// (<see cref="WaitForDelayTests"/>). To keep the suite fast, a target is
/// built a few hundred milliseconds ahead of <c>GETDATE()</c> inside the batch,
/// and a target already passed — which waits until tomorrow — is proved to wait
/// by cancelling it. Each waiting command carries a short
/// <c>CommandTimeout</c>, so a stalled runner fails rather than hangs. Behavior
/// probed 2026-10-10 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class WaitForTimeTests
{
    private static (object? Result, TimeSpan Elapsed) RunTimed(string sql)
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        var start = Stopwatch.GetTimestamp();
        var result = command.ExecuteScalar();
        return (result, Stopwatch.GetElapsedTime(start));
    }

    [TestMethod]
    [DataRow("declare @t datetime = dateadd(day, -400, dateadd(ms, 300, getdate()))")]
    [DataRow("declare @t varchar(12) = convert(varchar(12), dateadd(ms, 300, getdate()), 114)")]
    [DataRow("declare @t nchar(12) = convert(nchar(12), dateadd(ms, 300, getdate()), 114)")]
    public void Time_LaterToday_WaitsUntilThen(string declaration)
    {
        // A datetime variable's date is ignored: only its time of day counts.
        var (result, elapsed) = RunTimed($"{declaration}; waitfor time @t; select 1");
        AreEqual(1, result);
        IsGreaterThanOrEqualTo(250, elapsed.TotalMilliseconds, $"Expected about 300ms, got {elapsed.TotalMilliseconds}ms");
        IsLessThan(5000, elapsed.TotalMilliseconds, $"Expected about 300ms, got {elapsed.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void Time_Literal_ComparesWithTheServerClock()
    {
        // The literal is built from the simulated server's clock (UTC), not
        // the host's local time, which a test machine set to UTC would never catch.
        var target = DateTime.UtcNow.AddMilliseconds(300).ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var (result, elapsed) = RunTimed($"waitfor time '{target}'; select 1");
        AreEqual(1, result);
        IsLessThan(5000, elapsed.TotalMilliseconds, $"Expected about 300ms, got {elapsed.TotalMilliseconds}ms");
    }

    /// <summary>
    /// A time already passed today waits until it comes round tomorrow — five
    /// seconds behind the clock is a wait of nearly a day, which a cancel ends
    /// with Msg 0 as it ends a <c>WAITFOR DELAY</c>; the connection stays usable.
    /// </summary>
    [TestMethod]
    [DataRow("declare @t datetime = dateadd(second, -5, getdate()); waitfor time @t; select 1")]
    [DataRow("declare @t varchar(12) = convert(varchar(12), dateadd(second, -5, getdate()), 114); waitfor time @t; select 1")]
    public void Time_AlreadyPassed_WaitsUntilTomorrow_InterruptedByCancel(string sql)
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        var done = false;
        var canceller = new Thread(() =>
        {
            Thread.Sleep(300);
            while (!Volatile.Read(ref done))
            {
                command.Cancel();
                Thread.Sleep(100);
            }
        });

        var start = Stopwatch.GetTimestamp();
        canceller.Start();
        var cancelled = Throws<SimulatedSqlException>(() => _ = command.ExecuteScalar());
        Volatile.Write(ref done, true);
        canceller.Join();
        var elapsed = Stopwatch.GetElapsedTime(start);

        AreEqual(0, cancelled.Number);
        IsGreaterThanOrEqualTo(250, elapsed.TotalMilliseconds, $"Expected the wait to last until the cancel, got {elapsed.TotalMilliseconds}ms");
        IsLessThan(10000, elapsed.TotalMilliseconds, $"Expected the cancel to interrupt promptly, got {elapsed.TotalMilliseconds}ms");

        using var probe = connection.CreateCommand();
        probe.CommandText = "select 42";
        AreEqual(42, probe.ExecuteScalar());
    }

    // Sequential, like WaitForDelayTests' timeout tests: the wait holds its
    // thread for the whole second CommandTimeout can't go below.
    [TestMethod]
    [DoNotParallelize]
    public void Time_AlreadyPassed_CommandTimeout_RaisesMinus2()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "declare @t datetime = dateadd(second, -5, getdate()); waitfor time @t";
        command.CommandTimeout = 1;

        var timeout = Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());

        AreEqual(-2, timeout.Number);
    }

    [TestMethod]
    [DataRow("declare @t varchar(20)")]
    [DataRow("declare @t char(10)")]
    [DataRow("declare @t datetime")]
    [DataRow("declare @t int")]
    [DataRow("declare @t smallint")]
    public void Time_NullVariable_ReturnsAtOnce(string declaration)
    {
        var (result, elapsed) = RunTimed($"{declaration}; waitfor time @t; select 1");
        AreEqual(1, result);
        IsLessThan(2000, elapsed.TotalMilliseconds, $"Expected no wait, got {elapsed.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void Time_InUntakenIf_DoesNotWait()
    {
        // '' is midnight, which would otherwise wait until then.
        var (result, elapsed) = RunTimed("if 1 = 0 waitfor time ''; select 1");
        AreEqual(1, result);
        IsLessThan(2000, elapsed.TotalMilliseconds, $"Expected no wait, got {elapsed.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void Time_ResetsRowCountAndLeavesTheTransaction()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            begin tran;
            select 1 union all select 2;
            declare @t datetime = dateadd(ms, 100, getdate());
            waitfor time @t;
            select @@rowcount, @@trancount, xact_state();
            """;
        command.CommandTimeout = 10;
        using var reader = command.ExecuteReader();
        while (reader.Read()) { }
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual((short)1, reader.GetInt16(2));
    }

    [TestMethod]
    [DataRow("'not a time'")]
    [DataRow("'24:00'")]
    [DataRow("'25:00'")]
    [DataRow("'2020-01-01 10:00'")]
    [DataRow("'00:00:00.0001'")]
    [DataRow("N'not'")]
    public void Time_MalformedLiteral_Msg148(string operand)
        => _ = new Simulation().AssertSqlError($"waitfor time {operand}", 148);

    [TestMethod]
    public void Time_MalformedLiteral_Msg148Wording()
        => new Simulation().AssertSqlError(
            "waitfor time 'not a time'", 148,
            "Incorrect time syntax in time string 'not a time' used with WAITFOR.");

    [TestMethod]
    [DataRow("waitfor time 1")]
    [DataRow("waitfor time cast('10:00' as time)")]
    public void Time_OperandOutsideTheGrammar_Msg102(string sql)
        => _ = new Simulation().AssertSqlError(sql, 102);

    [TestMethod]
    [DataRow("declare @t varchar(20) = 'bogus'", 241)]
    [DataRow("declare @t varchar(max) = '10:00'", 241)]
    [DataRow("declare @t time = '10:00'", 9815)]
    [DataRow("declare @t datetime2 = '10:00'", 9815)]
    [DataRow("declare @t smalldatetime = '10:00'", 9815)]
    [DataRow("declare @t date = '2020-01-01'", 9815)]
    [DataRow("declare @t datetimeoffset = '2020-01-01'", 9815)]
    public void Time_VariableOutsideTheTypes_Refused(string declaration, int number)
        => _ = new Simulation().AssertSqlError($"{declaration}; waitfor time @t", number);

    [TestMethod]
    public void Time_RefusedVariableType_EndsOnlyItsStatement()
    {
        var ex = new Simulation().AssertSqlError("declare @t float = 0; waitfor time @t; select 1 / 0", 9815);
        AreEqual("Waitfor delay and waitfor time cannot be of type float.", ex.Errors[0].Message);
        AreEqual(8134, ex.Errors[1].Number);
    }
}
