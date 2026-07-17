using System.Data.Common;
using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>WAITFOR DELAY</c>. The simulator uses
/// <see cref="Thread.Sleep(TimeSpan)"/> on the calling thread, matching real
/// SQL Server's "blocks the connection" semantics. To keep CI fast, only
/// one test actually sleeps a non-trivial duration; everything else
/// exercises parsing / dispatch / skip-mode / error paths with a
/// <c>'00:00:00'</c> operand. Behavior probed against SQL Server 2025
/// (2026-05-11).
/// </summary>
[TestClass]
public sealed class WaitForDelayTests
{
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
}
