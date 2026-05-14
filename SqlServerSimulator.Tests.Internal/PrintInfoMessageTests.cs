using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Internal-only tests for the <see cref="SimulatedDbConnection.InfoMessage"/>
/// event surface that delivers buffered <c>PRINT</c> output. The event itself
/// is internal pending a public-API decision (DbConnection has no equivalent
/// in the base class); these tests pin the wire-up so that consumer-shape
/// changes catch a failure rather than silent behavior shift.
/// </summary>
/// <remarks>
/// Probed against SQL Server 2025 (2026-05-14):
/// <list type="bullet">
/// <item>Multiple <c>PRINT</c>s in one batch coalesce into one event with
/// messages joined by <c>\n</c>.</item>
/// <item>NULL operand emits a single space, not empty.</item>
/// <item>Skip-mode IF suppresses the PRINT.</item>
/// <item>Event fires once per <c>ExecuteNonQuery</c> command, after all
/// statements complete (even when later statements roll back).</item>
/// </list>
/// </remarks>
[TestClass]
public sealed class PrintInfoMessageTests
{
    private static (Simulation Sim, System.Data.Common.DbConnection Conn, List<SimulatedInfoMessageEventArgs> Captured) NewWithCapture()
    {
        var sim = new Simulation();
        var conn = sim.CreateDbConnection();
        conn.Open();
        var captured = new List<SimulatedInfoMessageEventArgs>();
        ((SimulatedDbConnection)conn).InfoMessage += (_, e) => captured.Add(e);
        return (sim, conn, captured);
    }

    private static int RunNonQuery(System.Data.Common.DbConnection conn, string commandText)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        return cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void Print_StringLiteral_FiresOneMessage()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'hello'");
        HasCount(1, captured);
        AreEqual("hello", captured[0].Message);
    }

    [TestMethod]
    public void Print_TwoStatements_CoalesceWithLineFeed()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'first'; print 'second'");
        HasCount(1, captured);
        AreEqual("first\nsecond", captured[0].Message);
    }

    [TestMethod]
    public void Print_NullOperand_EmitsSingleSpace()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print null");
        HasCount(1, captured);
        AreEqual(" ", captured[0].Message);
    }

    [TestMethod]
    public void Print_StringPlusNull_EmitsSingleSpace()
    {
        // 'a' + NULL collapses to NULL under ANSI string-concat NULL semantics,
        // and the NULL-operand-formats-as-single-space rule kicks in.
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'a' + null");
        HasCount(1, captured);
        AreEqual(" ", captured[0].Message);
    }

    [TestMethod]
    public void Print_Integer_FormatsAsInvariantString()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 42");
        HasCount(1, captured);
        AreEqual("42", captured[0].Message);
    }

    [TestMethod]
    public void Print_StringLiteral_PreservesEmbeddedNewlines()
    {
        // T-SQL string literals can carry raw newlines; the captured message
        // should preserve them verbatim. (The `CHAR(13) + CHAR(10)` form used
        // by some SQL idioms isn't reachable here — CHAR() isn't modeled —
        // but the literal-newline form covers the same observation.)
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'line1\nline2'");
        HasCount(1, captured);
        AreEqual("line1\nline2", captured[0].Message);
    }

    [TestMethod]
    public void Print_NoStatements_NoEventFires()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "select 1");
        IsEmpty(captured);
    }

    [TestMethod]
    public void Print_SkipModeIf_SuppressesEvent()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "if 1 = 0 print 'unreachable'");
        IsEmpty(captured);
    }

    [TestMethod]
    public void Print_ElseBranchTaken_FiresMessage()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "if 1 = 0 print 'no' else print 'yes'");
        HasCount(1, captured);
        AreEqual("yes", captured[0].Message);
    }

    [TestMethod]
    public void Print_InsideWhile_CoalescesAllIterations()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, """
            declare @i int = 0;
            while @i < 3
            begin
                print 'iter ' + cast(@i as varchar(5));
                set @i = @i + 1;
            end
            """);
        HasCount(1, captured);
        AreEqual("iter 0\niter 1\niter 2", captured[0].Message);
    }

    [TestMethod]
    public void Print_InTransactionRollback_StillDelivers()
    {
        // Info messages aren't transactional — probe-confirmed PRINT inside
        // BEGIN TRAN / ROLLBACK still fires.
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, """
            begin tran;
            print 'inside';
            rollback
            """);
        HasCount(1, captured);
        AreEqual("inside", captured[0].Message);
    }

    [TestMethod]
    public void Print_BeforeAndAfterTryCatch_BothCoalesced()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, """
            begin try
                print 'before';
                throw 50000, 'oops', 1;
            end try
            begin catch
                print 'caught';
            end catch
            """);
        HasCount(1, captured);
        AreEqual("before\ncaught", captured[0].Message);
    }

    [TestMethod]
    public void Print_TwoSeparateCommands_FireTwoEvents()
    {
        // Each ExecuteNonQuery is its own batch; events are per-batch, not
        // per-connection-lifetime.
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'first command'");
        _ = RunNonQuery(conn, "print 'second command'");
        HasCount(2, captured);
        AreEqual("first command", captured[0].Message);
        AreEqual("second command", captured[1].Message);
    }

    [TestMethod]
    public void Print_LineNumber_PointsToFirstPrint()
    {
        var (_, conn, captured) = NewWithCapture();
        // Three lines, first PRINT on line 2 (after the comment + select).
        _ = RunNonQuery(conn, """
            select 1;
            print 'first';
            print 'second'
            """);
        HasCount(1, captured);
        AreEqual(2, captured[0].LineNumber);
    }

    [TestMethod]
    public void Print_Source_IsSimulatorIdentifier()
    {
        var (_, conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'hi'");
        AreEqual("SqlServerSimulator", captured[0].Source);
    }

    [TestMethod]
    public void Print_NoSubscriber_DoesNotThrow()
    {
        // Event delivery is null-safe — running PRINT without a subscriber
        // is the common ad-hoc case.
        var sim = new Simulation();
        using var conn = sim.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "print 'no listener'";
        _ = cmd.ExecuteNonQuery();
    }
}
