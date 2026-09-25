using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>RAISERROR</c>. Behavior probed against SQL Server 2025
/// (2026-05-12); the suite covers the printf-style formatter (every supported
/// specifier + width / precision / left-align / zero-pad), severity routing
/// (≤ 10 informational vs ≥ 11 catchable), <c>WITH SETERROR</c>/<c>NOWAIT</c>/
/// <c>LOG</c> option handling, the <c>msg_id</c> error matrix (Msg 2732 /
/// 18054), and arg-validation paths (Msg 2786 / 2787 / 2747). Real SQL Server's
/// sysadmin-gated paths (Msg 2754 for sev &gt; 18, Msg 2778 for WITH LOG) pass
/// for a sysadmin and for the in-process default session.
/// </summary>
[TestClass]
public sealed class RaiserrorTests
{
    public TestContext TestContext { get; set; } = null!;

    // ---- severity routing ----

    [TestMethod]
    public void Sev16_OutsideTry_Throws_50000()
        => new Simulation().AssertSqlError("raiserror('boom', 16, 1)", 50000);

    [TestMethod]
    public void Sev11_InTry_IsCaught()
        => AreEqual("caught", new Simulation().ExecuteScalar(
            "begin try raiserror('x', 11, 1) end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void Sev16_InTry_IsCaught()
        => AreEqual("caught", new Simulation().ExecuteScalar(
            "begin try raiserror('x', 16, 1) end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void Sev18_InTry_IsCaught()
        => AreEqual(18, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 18, 1) end try begin catch select error_severity() end catch"));

    [TestMethod]
    public void Sev10_NotCaught_BodyContinues()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "begin try raiserror('info', 10, 1); select 'after' end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void Sev0_NotCaught_BodyContinues()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "begin try raiserror('info', 0, 1); select 'after' end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void NegativeSev_TreatedAsInformational()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "begin try raiserror('x', -1, 1); select 'after' end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void NullSev_TreatedAsInformational()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "declare @s int = null; begin try raiserror('x', @s, 1); select 'after' end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void Sev19_RaisesMsg2754()
        => new Simulation().AssertSqlError("raiserror('x', 19, 1)", 2754);

    [TestMethod]
    public void Sev20_RaisesMsg2754()
        => new Simulation().AssertSqlError("raiserror('x', 20, 1)", 2754);

    [TestMethod]
    public void Sev26_RaisesMsg2754()
        => new Simulation().AssertSqlError("raiserror('x', 26, 1)", 2754);

    // ---- error captured in CATCH ----

    [TestMethod]
    public void Caught_Number_Is_50000()
        => AreEqual(50000, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 14, 5) end try begin catch select error_number() end catch"));

    [TestMethod]
    public void Caught_Severity_PreservesSuppliedSeverity()
        => AreEqual(14, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 14, 5) end try begin catch select error_severity() end catch"));

    [TestMethod]
    public void Caught_State_PreservesSuppliedState()
        => AreEqual(5, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 14, 5) end try begin catch select error_state() end catch"));

    [TestMethod]
    public void Caught_Message_RendersFormattedText()
        => AreEqual("hello world", new Simulation().ExecuteScalar(
            "begin try raiserror('hello %s', 16, 1, 'world') end try begin catch select error_message() end catch"));

    // ---- state clamping ----

    [TestMethod]
    public void State256_ClampedTo0_NotAnError()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 16, 256) end try begin catch select error_state() end catch"));

    [TestMethod]
    public void NullState_ClampedTo0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "declare @st int = null; begin try raiserror('x', 16, @st) end try begin catch select error_state() end catch"));

    [TestMethod]
    [DataRow("-5", 1)]
    [DataRow("-256", 1)]
    [DataRow("300", 44)]
    [DataRow("1000", 232)]
    [DataRow("2147483647", 255)]
    public void State_NegativeIs1_LargeWrapsModulo256(string state, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            $"begin try raiserror('x', 16, {state}) end try begin catch select error_state() end catch"));

    // ---- format specifiers ----

    [TestMethod]
    public void Format_String_S()
        => AreEqual("hi world", FormattedMessage("raiserror('hi %s', 16, 1, 'world')"));

    [TestMethod]
    public void Format_NString_S()
        => AreEqual("hi world", FormattedMessage("raiserror('hi %s', 16, 1, N'world')"));

    [TestMethod]
    public void Format_Int_D()
        => AreEqual("count=42", FormattedMessage("raiserror('count=%d', 16, 1, 42)"));

    [TestMethod]
    public void Format_Int_I_AliasOfD()
        => AreEqual("count=42", FormattedMessage("raiserror('count=%i', 16, 1, 42)"));

    [TestMethod]
    public void Format_NegativeInt_D()
        => AreEqual("n=-7", FormattedMessage("raiserror('n=%d', 16, 1, -7)"));

    [TestMethod]
    public void Format_LiteralPercent()
        => AreEqual("50% off", FormattedMessage("raiserror('50%% off', 16, 1)"));

    [TestMethod]
    public void Format_Hex_LowerX()
        => AreEqual("hex=ff", FormattedMessage("raiserror('hex=%x', 16, 1, 255)"));

    [TestMethod]
    public void Format_Hex_UpperX()
        => AreEqual("hex=FF", FormattedMessage("raiserror('hex=%X', 16, 1, 255)"));

    [TestMethod]
    public void Format_Octal_O()
        => AreEqual("oct=10", FormattedMessage("raiserror('oct=%o', 16, 1, 8)"));

    [TestMethod]
    public void Format_UnsignedNegativeOne_U()
        => AreEqual("u=4294967295", FormattedMessage("raiserror('u=%u', 16, 1, -1)"));

    [TestMethod]
    public void Format_Long_Ld_SameAsBareD()
        => AreEqual("ld=12345", FormattedMessage("raiserror('ld=%ld', 16, 1, 12345)"));

    [TestMethod]
    public void Format_BigInt_I64d()
        => AreEqual("big=5000000000", FormattedMessage(
            "declare @b bigint = 5000000000; raiserror('big=%I64d', 16, 1, @b)"));

    [TestMethod]
    public void Format_Width_RightAlign()
        => AreEqual("[        42]", FormattedMessage("raiserror('[%10d]', 16, 1, 42)"));

    [TestMethod]
    public void Format_Width_LeftAlign()
        => AreEqual("[hi        ]", FormattedMessage("raiserror('[%-10s]', 16, 1, 'hi')"));

    [TestMethod]
    public void Format_Width_ZeroPad()
        => AreEqual("[00042]", FormattedMessage("raiserror('[%05d]', 16, 1, 42)"));

    [TestMethod]
    public void Format_Width_ZeroPad_Negative()
        => AreEqual("[-0042]", FormattedMessage("raiserror('[%05d]', 16, 1, -42)"));

    [TestMethod]
    public void Format_StringPrecisionTruncates()
        => AreEqual("[hel]", FormattedMessage("raiserror('[%.3s]', 16, 1, 'hello')"));

    [TestMethod]
    public void Format_StringWidthRightAlign()
        => AreEqual("[                  hi]", FormattedMessage("raiserror('[%20s]', 16, 1, 'hi')"));

    [TestMethod]
    public void Format_Multiple_Args()
        => AreEqual("Alice is 30 years old", FormattedMessage(
            "raiserror('%s is %d years old', 16, 1, 'Alice', 30)"));

    [TestMethod]
    public void Format_NullArg_RendersNullLiteral()
        => AreEqual("val=(null)", FormattedMessage("raiserror('val=%s', 16, 1, null)"));

    [TestMethod]
    public void Format_NullVarArg_RendersNullLiteral()
        => AreEqual("v=(null)", FormattedMessage(
            "declare @v nvarchar(20) = null; raiserror('v=%s', 16, 1, @v)"));

    [TestMethod]
    public void Format_MissingArg_RendersNullLiteral()
        => AreEqual("only and (null)", FormattedMessage(
            "raiserror('%s and %s', 16, 1, 'only')"));

    [TestMethod]
    public void Format_ExtraArgs_Ignored()
        => AreEqual("plain", FormattedMessage("raiserror('plain', 16, 1, 'extra')"));

    // ---- format errors ----

    [TestMethod]
    public void Format_UnsupportedC_RaisesMsg2787()
        => new Simulation().AssertSqlError("raiserror('%c', 16, 1)", 2787);

    [TestMethod]
    public void Format_UnsupportedP_RaisesMsg2787()
        => new Simulation().AssertSqlError("raiserror('%p', 16, 1)", 2787);

    [TestMethod]
    public void Format_TrailingLonePercent_RaisesMsg2787()
        => new Simulation().AssertSqlError("raiserror('end%', 16, 1)", 2787);

    [TestMethod]
    public void Format_DWithString_RaisesMsg2786()
        => new Simulation().AssertSqlError("raiserror('n=%d', 16, 1, 'hello')", 2786);

    [TestMethod]
    public void Format_SWithInt_RaisesMsg2786()
        => new Simulation().AssertSqlError("raiserror('s=%s', 16, 1, 42)", 2786);

    [TestMethod]
    public void Format_DWithBigint_RaisesMsg2786()
        => new Simulation().AssertSqlError(
            "declare @b bigint = 5000000000; raiserror('n=%d', 16, 1, @b)", 2786);

    [TestMethod]
    public void TooManyArgs_RaisesMsg2747()
        => new Simulation().AssertSqlError(
            "raiserror('many', 16, 1, 'a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p','q','r','s','t','u')",
            2747);

    [TestMethod]
    public void Exactly20Args_Succeeds()
        => AreEqual("plain", FormattedMessage(
            "raiserror('plain', 16, 1, 'a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p','q','r','s','t')"));

    // ---- msg_id matrix ----

    [TestMethod]
    public void MsgId_50000_Literal_RaisesMsg2732()
        => new Simulation().AssertSqlError("raiserror(50000, 16, 1)", 2732);

    [TestMethod]
    public void MsgId_Below13000_RaisesMsg2732()
        => new Simulation().AssertSqlError("raiserror(12345, 16, 1)", 2732);

    [TestMethod]
    public void MsgId_60000_Unregistered_RaisesMsg18054()
        => new Simulation().AssertSqlError("raiserror(60000, 16, 1)", 18054);

    [TestMethod]
    public void MsgId_13001_NotInRegistry_RaisesMsg18054()
        => new Simulation().AssertSqlError("raiserror(13001, 16, 1)", 18054);

    // ---- message via @var ----

    [TestMethod]
    public void MessageVar_FormatsCorrectly()
        => AreEqual("from var: X", FormattedMessage(
            "declare @m nvarchar(200) = 'from var: %s'; raiserror(@m, 16, 1, 'X')"));

    [TestMethod]
    public void NullMessageVar_RaisesEmptyMessage()
    {
        var msg = (string?)new Simulation().ExecuteScalar("""
            declare @m nvarchar(50) = null;
            begin try raiserror(@m, 16, 1) end try begin catch select error_message() end catch
            """);
        // Probe-confirmed: NULL message renders as a single space.
        AreEqual(" ", msg);
    }

    // ---- WITH options ----

    [TestMethod]
    public void WithSetError_Sev10_SetsAtAtError_To_50000()
        => AreEqual(50000, new Simulation().ExecuteScalar(
            "raiserror('info', 10, 7) with seterror; select @@error"));

    [TestMethod]
    public void WithoutSetError_Sev10_LeavesAtAtError_At0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "raiserror('info', 10, 7); select @@error"));

    [TestMethod]
    public void WithNowait_Sev16_StillRaises()
        => new Simulation().AssertSqlError("raiserror('boom', 16, 1) with nowait", 50000);

    [TestMethod]
    public void WithNowait_Sev10_NoEffect()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "raiserror('info', 10, 1) with nowait; select @@error"));

    [TestMethod]
    public void WithLog_Sev16_Raises50000()
        => new Simulation().AssertSqlError("raiserror('x', 16, 1) with log", 50000);

    [TestMethod]
    public void WithLog_Sev19_RaisesAtClass19()
        => AreEqual(19, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 19, 1) with log end try begin catch select error_severity() end catch"));

    [TestMethod]
    [DataRow(10)]
    [DataRow(16)]
    public void WithLog_NotSysadmin_RaisesMsg2778(int severity)
        => new Simulation().AssertSqlError($"""
            create login lo with password = 'S3cret!Pass';
            create user lo for login lo;
            execute as login = 'lo';
            raiserror('x', {severity}, 1) with log
            """, 2778);

    [TestMethod]
    public void WithMultipleOptions_NowaitAndSetError_Works()
        => AreEqual(50000, new Simulation().ExecuteScalar(
            "raiserror('info', 10, 1) with nowait, seterror; select @@error"));

    // ---- @@ERROR / @@ROWCOUNT side effects ----

    [TestMethod]
    public void Raiserror_Sev16_ThenAtAtError_ReadsViaCatch()
    {
        // Outside TRY the exception terminates the batch, so @@ERROR can only
        // be observed inside CATCH or after WITH SETERROR.
        var n = (int)new Simulation().ExecuteScalar("""
            begin try raiserror('x', 16, 1) end try begin catch select @@error end catch
            """)!;
        AreEqual(50000, n);
    }

    [TestMethod]
    public void Raiserror_Sev10_ResetsRowCountTo0()
    {
        using var reader = new Simulation().ExecuteReader("""
            select 1 union all select 2;
            raiserror('info', 10, 1);
            select @@rowcount
            """);
        while (reader.Read()) { }
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void Raiserror_Sev10_WithSetError_RowCountStill0()
    {
        using var reader = new Simulation().ExecuteReader("""
            select 1 union all select 2;
            raiserror('info', 10, 1) with seterror;
            select @@rowcount, @@error
            """);
        while (reader.Read()) { }
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        AreEqual(50000, reader.GetInt32(1));
    }

    [TestMethod]
    public void AtAtError_ResetsAfter_NextSuccessfulStatement()
    {
        using var reader = new Simulation().ExecuteReader("""
            raiserror('info', 10, 1) with seterror;
            select @@error as first;
            select @@error as second
            """);
        // first ResultSet: 50000
        IsTrue(reader.Read());
        AreEqual(50000, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        // probe-confirmed: SELECT @@ERROR clears @@ERROR after running.
        AreEqual(0, reader.GetInt32(0));
    }

    // ---- skip-mode interaction ----

    [TestMethod]
    public void Raiserror_InUntakenIf_DoesNotRaise()
        => _ = new Simulation().ExecuteNonQuery("if 1=0 raiserror('skip', 16, 1)");

    [TestMethod]
    public void Raiserror_InTakenIf_StillRaises()
        => new Simulation().AssertSqlError("if 1=1 raiserror('boom', 16, 1)", 50000);

    [TestMethod]
    public void Raiserror_AfterReturn_DoesNotRaise()
        => _ = new Simulation().ExecuteNonQuery("return; raiserror('boom', 16, 1)");

    // ---- `*` width and precision, integer precision ----

    [TestMethod]
    [DataRow("raiserror('[%*.*s]', 16, 1, 7, 3, 'abcdef')", "[    abc]")]
    [DataRow("raiserror('[%-*d]', 16, 1, 6, 42)", "[42    ]")]
    [DataRow("raiserror('[%0*d]', 16, 1, 6, 42)", "[000042]")]
    [DataRow("raiserror('[%*s]', 16, 1, -8, 'abc')", "[abc]")]
    [DataRow("raiserror('[%.*s]', 16, 1, -1, 'abc')", "[abc]")]
    [DataRow("raiserror('[%*.*d]', 16, 1, 8, 5, 42)", "[   00042]")]
    [DataRow("raiserror('[%.3d]', 16, 1, -4)", "[-004]")]
    [DataRow("raiserror('[%08.3d]', 16, 1, 42)", "[     042]")]
    [DataRow("raiserror('[%.0d]', 16, 1, 0)", "[]")]
    [DataRow("raiserror('[%.3x]', 16, 1, 10)", "[00a]")]
    public void Format_StarAndIntegerPrecision(string statement, string expected)
        => AreEqual(expected, FormattedMessage(statement));

    [TestMethod]
    [DataRow("raiserror('%*s', 16, 1, 'x', 'abc')")]
    [DataRow("raiserror('%*s', 16, 1, null, 'abc')")]
    [DataRow("raiserror('%*s', 16, 1)")]
    [DataRow("raiserror('%I64d', 16, 1, 42)")]
    [DataRow("raiserror('%d', 16, 1, 4200000000)")]
    public void Format_ArgumentOfTheWrongType_RaisesMsg2786(string statement)
        => new Simulation().AssertSqlError(statement, 2786);

    [TestMethod]
    public void Format_FractionalLiteral_ArrivesAsBigInt()
        => AreEqual("[5]", FormattedMessage("raiserror('[%I64d]', 16, 1, 5.5)"));

    [TestMethod]
    [DataRow("raiserror('[%c|] tail', 16, 1, 42)", "Invalid format specification: '%c|] tail'.")]
    [DataRow("raiserror('[%**d]', 16, 1, 6, 42)", "Invalid format specification: '%**d]'.")]
    [DataRow("raiserror('[%.s]', 16, 1, 'abc')", "Invalid format specification: '%.s]'.")]
    public void Format_InvalidSpecification_NamesTheRestOfTheFormat(string statement, string message)
        => new Simulation().AssertSqlError(statement, 2787, message);

    [TestMethod]
    [DataRow("declare @v datetime = 1; raiserror('x', 16, 1, @v)", "Cannot specify datetime data type (parameter 4) as a substitution parameter.")]
    [DataRow("declare @v bit = 1; raiserror('%d', 16, 1, 5, @v)", "Cannot specify bit data type (parameter 5) as a substitution parameter.")]
    [DataRow("declare @v decimal(10, 0) = 5; raiserror('%d', 16, 1, @v)", "Cannot specify decimal(10,0) data type (parameter 4) as a substitution parameter.")]
    public void Format_DisallowedSubstitutionType_RaisesMsg2748(string statement, string message)
        => new Simulation().AssertSqlError(statement, 2748, message);

    [TestMethod]
    public void Format_UnreadDecimalSubstitution_IsAccepted()
        => AreEqual("x", FormattedMessage("declare @v numeric(5, 2) = 1; raiserror('x', 16, 1, @v)"));

    // ---- syntax errors / arg-position restrictions ----

    [TestMethod]
    public void RaiserrorArg_AcceptsSignedNumeric()
        => AreEqual(7, new Simulation().ExecuteScalar(
            "begin try raiserror('x', 16, 7) end try begin catch select error_state() end catch"));

    /// <summary>
    /// Real SQL Server's grammar rejects CAST in arg position with Msg 102.
    /// The simulator surfaces a generic syntax error (Msg 102).
    /// </summary>
    [TestMethod]
    public void RaiserrorArg_RejectsCast_Msg102()
        => _ = new Simulation().AssertSqlError("raiserror('x', 16, 1, cast(3 as int))", 102);

    /// <summary>
    /// Resolves a RAISERROR statement and returns the formatted error message
    /// by wrapping in TRY/CATCH and reading <c>ERROR_MESSAGE()</c>. Used by
    /// every format-string assertion so the test names describe the rendered
    /// output rather than the input pattern.
    /// </summary>
    private static string FormattedMessage(string raiserrorStatement)
    {
        var sql = $"begin try {raiserrorStatement} end try begin catch select error_message() end catch";
        return (string)new Simulation().ExecuteScalar(sql)!;
    }

    // Severity 20 WITH LOG ends the session: four errors, the transaction
    // rolled back, no CATCH, the rest of the batch skipped and the connection
    // closed (probed 2026-09-25 against SQL Server 2025).
    [TestMethod]
    public void Severity20WithLog_EndsTheSession()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int)");
        using var connection = simulation.CreateOpenConnection();
        var spid = connection.CreateCommand("select @@spid").ExecuteScalar();
        using var command = connection.CreateCommand(
            "begin tran; insert t values (1); begin try raiserror('boom %d', 20, 3, 7) with log; end try begin catch insert t values (2) end catch; insert t values (3)");
        var ex = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        AreEqual(
            $"50000/20/3 boom 7|2745/16/2 Process ID {spid} has raised user error 50000, severity 20. SQL Server is terminating this process.|596/21/1 Cannot continue the execution because the session is in the kill state.|0/20/0 A severe error occurred on the current command.  The results, if any, should be discarded.",
            string.Join("|", ex.Errors.Cast<SimulatedError>().Select(error => $"{error.Number}/{error.Class}/{error.State} {error.Message}")));
        AreEqual(System.Data.ConnectionState.Closed, connection.State);
        var closed = Throws<InvalidOperationException>(() => connection.CreateCommand("select 1").ExecuteScalar());
        AreEqual("ExecuteScalar requires an open and available Connection. The connection's current state is closed.", closed.Message);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t"));
    }
}
