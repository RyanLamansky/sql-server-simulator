using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-API tests for the <see cref="SimulatedDbConnection.InfoMessage"/>
/// event surface delivering buffered <c>PRINT</c> output and severity-0-10
/// <c>RAISERROR</c> messages. Mirrors the shape of
/// <c>SqlConnection.InfoMessage</c>: <see cref="SimulatedInfoMessageEventArgs.Errors"/>
/// is the per-message collection, <see cref="SimulatedInfoMessageEventArgs.Message"/>
/// is the joined-string shortcut.
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
public sealed class InfoMessageEventTests
{
    private static (SimulatedDbConnection Conn, List<SimulatedInfoMessageEventArgs> Captured) NewWithCapture()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        var captured = new List<SimulatedInfoMessageEventArgs>();
        conn.InfoMessage += (_, e) => captured.Add(e);
        return (conn, captured);
    }

    private static int RunNonQuery(SimulatedDbConnection conn, string commandText)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        return cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void Print_InsideProcedureBody_FiresMessage()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "create procedure noisy as print 'from proc'");
        _ = RunNonQuery(conn, "exec noisy");
        HasCount(1, captured);
        AreEqual("from proc", captured[0].Message);
    }

    [TestMethod]
    public void Print_StringLiteral_FiresOneMessage()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'hello'");
        HasCount(1, captured);
        AreEqual("hello", captured[0].Message);
        HasCount(1, captured[0].Errors);
        AreEqual("hello", captured[0].Errors[0].Message);
        AreEqual(0, captured[0].Errors[0].Number);
        AreEqual<byte>(0, captured[0].Errors[0].Class);
        AreEqual<byte>(1, captured[0].Errors[0].State);
    }

    [TestMethod]
    public void Print_TwoStatements_CoalesceWithLineFeed()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'first'; print 'second'");
        HasCount(1, captured);
        AreEqual("first\nsecond", captured[0].Message);
        // Coalesces into one Errors entry whose Message carries both lines.
        HasCount(1, captured[0].Errors);
        AreEqual("first\nsecond", captured[0].Errors[0].Message);
    }

    [TestMethod]
    public void Print_NullOperand_EmitsSingleSpace()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print null");
        HasCount(1, captured);
        AreEqual(" ", captured[0].Message);
    }

    [TestMethod]
    public void Print_StringPlusNull_EmitsSingleSpace()
    {
        // 'a' + NULL collapses to NULL under ANSI string-concat NULL semantics,
        // and the NULL-operand-formats-as-single-space rule kicks in.
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'a' + null");
        HasCount(1, captured);
        AreEqual(" ", captured[0].Message);
    }

    [TestMethod]
    public void Print_Integer_FormatsAsInvariantString()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 42");
        HasCount(1, captured);
        AreEqual("42", captured[0].Message);
    }

    [TestMethod]
    public void Print_StringLiteral_PreservesEmbeddedNewlines()
    {
        // T-SQL string literals can carry raw newlines; the captured message
        // should preserve them verbatim.
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'line1\nline2'");
        HasCount(1, captured);
        AreEqual("line1\nline2", captured[0].Message);
    }

    [TestMethod]
    public void Print_NoStatements_NoEventFires()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "select 1");
        IsEmpty(captured);
    }

    [TestMethod]
    public void Print_SkipModeIf_SuppressesEvent()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "if 1 = 0 print 'unreachable'");
        IsEmpty(captured);
    }

    [TestMethod]
    public void Print_ElseBranchTaken_FiresMessage()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "if 1 = 0 print 'no' else print 'yes'");
        HasCount(1, captured);
        AreEqual("yes", captured[0].Message);
    }

    [TestMethod]
    public void Print_InsideWhile_CoalescesAllIterations()
    {
        var (conn, captured) = NewWithCapture();
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
        var (conn, captured) = NewWithCapture();
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
        var (conn, captured) = NewWithCapture();
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
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'first command'");
        _ = RunNonQuery(conn, "print 'second command'");
        HasCount(2, captured);
        AreEqual("first command", captured[0].Message);
        AreEqual("second command", captured[1].Message);
    }

    [TestMethod]
    public void Print_LineNumber_PointsToFirstPrint()
    {
        var (conn, captured) = NewWithCapture();
        // Three lines, first PRINT on line 2 (after the leading select).
        _ = RunNonQuery(conn, """
            select 1;
            print 'first';
            print 'second'
            """);
        HasCount(1, captured);
        AreEqual(2, captured[0].LineNumber);
        AreEqual(2, captured[0].Errors[0].LineNumber);
    }

    [TestMethod]
    public void Print_Source_IsSimulatorIdentifier()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'hi'");
        AreEqual("SqlServerSimulator", captured[0].Source);
        AreEqual("SqlServerSimulator", captured[0].Errors[0].Source);
    }

    [TestMethod]
    public void Print_Server_MatchesConnectionDataSource()
    {
        // SqlError.Server mirrors the connection's DataSource.
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'hi'");
        AreEqual(conn.DataSource, captured[0].Errors[0].Server);
    }

    [TestMethod]
    public void Print_NoSubscriber_DoesNotThrow()
    {
        // Event delivery is null-safe — running PRINT without a subscriber
        // is the common ad-hoc case.
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "print 'no listener'";
        _ = cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void Raiserror_Severity10_FiresInfoEvent()
    {
        // Severity 0-10 RAISERROR doesn't throw; it routes through InfoMessage
        // with Class = severity, Number = 50000, State = state argument.
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "raiserror('progress', 10, 7)");
        HasCount(1, captured);
        AreEqual("progress", captured[0].Message);
        HasCount(1, captured[0].Errors);
        AreEqual<byte>(10, captured[0].Errors[0].Class);
        AreEqual<byte>(7, captured[0].Errors[0].State);
        AreEqual(50000, captured[0].Errors[0].Number);
    }

    [TestMethod]
    public void Raiserror_Severity0_FiresInfoEvent()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "raiserror('zero', 0, 1)");
        HasCount(1, captured);
        AreEqual("zero", captured[0].Message);
        AreEqual<byte>(0, captured[0].Errors[0].Class);
    }

    [TestMethod]
    public void Raiserror_NegativeSeverity_FiresInfoEventAtZero()
    {
        // Negative severity coerces to 0 (probed); still fires InfoMessage.
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "raiserror('neg', -5, 3)");
        HasCount(1, captured);
        AreEqual<byte>(0, captured[0].Errors[0].Class);
        AreEqual<byte>(3, captured[0].Errors[0].State);
    }

    [TestMethod]
    public void Raiserror_PrintMixedCoalesces_FirstContributorClassWins()
    {
        // Mixed PRINT + sev-≤10 RAISERROR in one batch coalesce; the first
        // contributor's metadata (here PRINT's class=0) wins on the single
        // coalesced Errors entry.
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'p'; raiserror('r', 8, 2)");
        HasCount(1, captured);
        AreEqual("p\nr", captured[0].Message);
        HasCount(1, captured[0].Errors);
        AreEqual<byte>(0, captured[0].Errors[0].Class);
        AreEqual<byte>(1, captured[0].Errors[0].State);
    }

    [TestMethod]
    public void Raiserror_FormatSubstitution_DeliversFormattedText()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "raiserror('row %d of %d', 10, 1, 7, 42)");
        HasCount(1, captured);
        AreEqual("row 7 of 42", captured[0].Message);
    }

    [TestMethod]
    public void Raiserror_Severity11_Throws_NoInfoEvent()
    {
        // Severity 11+ remains in the throwing-error path; no InfoMessage fires.
        var (conn, captured) = NewWithCapture();
        _ = Throws<System.Data.Common.DbException>(() => RunNonQuery(conn, "raiserror('boom', 16, 1)"));
        IsEmpty(captured);
    }

    [TestMethod]
    public void Raiserror_WithSetError_UpdatesAtAtError()
    {
        // WITH SETERROR forces @@ERROR = 50000 even at sev ≤ 10.
        var (conn, captured) = NewWithCapture();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "raiserror('msg', 5, 1) with seterror; select @@error";
        var result = cmd.ExecuteScalar();
        AreEqual(50000, result);
        // InfoMessage still fires for the sev-≤10 message.
        HasCount(1, captured);
    }

    [TestMethod]
    public void Errors_Collection_IsIndexableAndCountable()
    {
        var (conn, captured) = NewWithCapture();
        _ = RunNonQuery(conn, "print 'one'");
        HasCount(1, captured);
        var errors = captured[0].Errors;
        HasCount(1, errors);
        var indexed = errors[0];
        IsNotNull(indexed);
        // Enumerable shape.
        HasCount(1, errors.ToList());
    }
}
