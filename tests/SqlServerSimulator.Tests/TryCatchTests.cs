using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH</c>,
/// the <c>ERROR_*()</c> functions, live <c>@@ERROR</c>, and <c>THROW</c>
/// (both forms). Semantics probed against SQL Server 2025 (2026-05-12).
/// </summary>
[TestClass]
public sealed class TryCatchTests
{
    // ---- basic TRY/CATCH ----

    [TestMethod]
    public void Try_NoError_ReturnsTryBody()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "begin try select 1 end try begin catch select 2 end catch"));

    [TestMethod]
    public void Try_BadConversion_Caught_ReturnsCatchBody()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select 2 end catch"));

    [TestMethod]
    public void Try_ConversionError_Caught()
        => AreEqual("caught", new Simulation().ExecuteScalar(
            "declare @x int; begin try set @x = 'abc' end try begin catch select 'caught' end catch"));

    [TestMethod]
    public void Try_ConstraintViolation_Caught()
        => AreEqual("caught", new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert into t values (1);
            begin try insert into t values (1) end try begin catch select 'caught' end catch
            """));

    // ---- statement-level atomicity preserved ----

    [TestMethod]
    public void Try_MultiRowInsert_MidFailure_RollsBackPartial()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table #t (id int primary key);
            begin try insert into #t values (1), (1), (2) end try begin catch select count(*) from #t end catch
            """));

    // ---- ERROR_*() functions ----

    [TestMethod]
    public void ErrorNumber_BadConversion_Returns245()
        => AreEqual(245, new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select error_number() end catch"));

    [TestMethod]
    public void ErrorMessage_BadConversion_ReturnsMessage()
    {
        var msg = (string?)new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select error_message() end catch");
        Contains("Conversion failed", msg!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ErrorSeverity_Returns16()
        => AreEqual(16, new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select error_severity() end catch"));

    [TestMethod]
    public void ErrorState_BadConversion_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select error_state() end catch"));

    [TestMethod]
    public void ErrorLine_SingleLineBatch_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select error_line() end catch"));

    /// <summary>
    /// The failing SET is on batch line 3; ERROR_LINE() reports it, not the
    /// TRY / batch start.
    /// </summary>
    [TestMethod]
    public void ErrorLine_MultiLineBatch_ReportsFailingStatementLine()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "declare @x int\nbegin try\nset @x = cast('abc' as int)\nend try begin catch select error_line() end catch"));

    [TestMethod]
    public void ErrorLine_MatchesExceptionLineNumber()
    {
        // ERROR_LINE() reports the same line the surfaced exception carries.
        var sim = new Simulation();
        var caught = sim.AssertSqlError("declare @x int\nset @x = cast('abc' as int)", 245);
        AreEqual(2, caught.LineNumber);
        AreEqual(2, sim.ExecuteScalar(
            "declare @x int\nbegin try set @x = cast('abc' as int) end try begin catch select error_line() end catch"));
    }

    [TestMethod]
    public void ErrorProcedure_OutsideProc_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader(
            "begin try select cast('abc' as int) end try begin catch select error_procedure() end catch");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void Error_Functions_OutsideCatch_ReturnNull()
    {
        using var reader = new Simulation().ExecuteReader(
            "select error_number(), error_message(), error_severity(), error_state(), error_line(), error_procedure()");
        IsTrue(reader.Read());
        for (var i = 0; i < 6; i++)
            IsTrue(reader.IsDBNull(i), $"column {i} should be NULL outside CATCH");
    }

    // ---- @@ERROR live behavior ----

    [TestMethod]
    public void AtAtError_InsideCatch_ReturnsErrorNumber()
        => AreEqual(245, new Simulation().ExecuteScalar(
            "begin try select cast('abc' as int) end try begin catch select @@error end catch"));

    [TestMethod]
    public void AtAtError_AfterCatch_Returns0()
    {
        // Inside CATCH: SELECT @@ERROR reads 245 (failing stmt's value).
        // That SELECT succeeds, so @@ERROR resets to 0 immediately after.
        // The follow-up SELECT @@ERROR reads 0.
        using var reader = new Simulation().ExecuteReader(
            "begin try select cast('abc' as int) end try begin catch select @@error as inside end catch; select @@error as after");
        IsTrue(reader.Read());
        AreEqual(245, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void AtAtError_NoTryCatch_StaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select @@error"));

    // ---- nested TRY/CATCH ----

    [TestMethod]
    public void NestedTry_InnerCatchSwallows_OuterDoesntFire()
    {
        // The inner CATCH eats the bad-conversion, the outer TRY runs to
        // completion without an error, and its CATCH is skipped. Result:
        // nothing visible to the caller.
        using var reader = new Simulation().ExecuteReader(
            "begin try begin try select cast('abc' as int) end try begin catch end catch end try begin catch select 'outer' end catch");
        // No row set produced.
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void NestedTry_InnerRethrows_OuterCatches()
        => AreEqual(245, new Simulation().ExecuteScalar(
            "begin try begin try select cast('abc' as int) end try begin catch throw end catch end try begin catch select error_number() end catch"));

    [TestMethod]
    public void NestedTry_InnerCatchOwnError_OuterDoesntSee()
    {
        // Inner CATCH absorbs the bad conversion, then the outer CATCH
        // won't run since the outer TRY block completes normally.
        using var reader = new Simulation().ExecuteReader(
            "begin try begin try select cast('abc' as int) end try begin catch select 'inner-caught' end catch end try begin catch select 'outer' end catch");
        IsTrue(reader.Read());
        AreEqual("inner-caught", reader.GetString(0));
        IsFalse(reader.NextResult());
    }

    // ---- THROW ----

    [TestMethod]
    public void Throw_NoArgs_OutsideCatch_Msg10704()
        => new Simulation().AssertSqlError("throw", 10704);

    /// <summary>
    /// The Msg 10704 check is lexical — a bare THROW inside a CATCH whose
    /// TRY body succeeded (so the CATCH skip-parses) must not raise it.
    /// SSMS's Select-Top-1000 server-properties batch has this shape:
    /// the CATCH rethrows unless ERROR_NUMBER() is a tolerated permission
    /// error, and the TRY body normally succeeds.
    /// </summary>
    [TestMethod]
    public void Throw_NoArgs_InsideSkippedCatch_Parses()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            declare @x int;
            begin try set @x = 1 end try
            begin catch
                if (error_number() not in (297, 300)) begin throw end
            end catch
            select 7
            """));

    [TestMethod]
    public void Throw_NoArgs_InsideCatch_Rethrows()
    {
        // Bare `throw` re-raises out of the batch (no outer TRY/CATCH).
        var ex = new Simulation().AssertSqlError(
            "begin try select cast('abc' as int) end try begin catch throw end catch", 245);
        Contains("Conversion failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Throw_Value_RaisesCustomError()
    {
        var ex = new Simulation().AssertSqlError(
            "throw 50001, 'custom message', 7", 50001);
        AreEqual("custom message", ex.Message);
        AreEqual(7, ex.State);
        AreEqual(16, ex.Class);
    }

    /// <summary>Only the state's low byte is kept, so 256 raises state 0.</summary>
    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("255", 255)]
    [DataRow("256", 0)]
    [DataRow("300", 44)]
    [DataRow("2147483647", 255)]
    [DataRow("@s", 1)]
    public void Throw_Value_StateKeepsItsLowByte(string state, int expected)
        => AreEqual(expected, new Simulation().AssertSqlError($"declare @s int = 257; throw 50000, 'x', {state}", 50000).State);

    [TestMethod]
    [DataRow("throw 50000, 'x', -1", "Invalid value -1 for state. State value must not be less than 0.")]
    [DataRow("declare @s int = -3; throw 50000, 'x', @s", "Invalid value -3 for state. State value must not be less than 0.")]
    public void Throw_Value_NegativeState_RaisesMsg2756(string sql, string message)
        => new Simulation().AssertSqlError(sql, 2756, message);

    /// <summary>
    /// A number below 50000 is Msg 35100 — including the 0 a NULL variable
    /// reads as — and it is checked before the state.
    /// </summary>
    [TestMethod]
    [DataRow("throw 49999, 'x', 1", 49999)]
    [DataRow("throw -5, 'x', 1", -5)]
    [DataRow("throw 49999, 'x', -1", 49999)]
    [DataRow("declare @n int; throw @n, 'x', 1", 0)]
    public void Throw_Value_NumberBelow50000_RaisesMsg35100(string sql, int number)
    {
        var ex = new Simulation().AssertSqlError(sql, 35100);
        AreEqual($"Error number {number} in the THROW statement is outside the valid range. Specify an error number in the valid range of 50000 to 2147483647.", ex.Message);
        AreEqual(10, ex.State);
    }

    /// <summary>
    /// A variable argument converts the way <c>CAST</c> would, and a NULL one
    /// reads as 0 or the empty message.
    /// </summary>
    [TestMethod]
    [DataRow("declare @n varchar(10) = '50002';", "@n, 'x', 1", 50002, "x", 1)]
    [DataRow("declare @n decimal(10, 2) = 50003.7;", "@n, 'x', 1", 50003, "x", 1)]
    [DataRow("declare @m int = 5;", "50000, @m, 1", 50000, "5", 1)]
    [DataRow("declare @m nvarchar(10);", "50000, @m, 1", 50000, "", 1)]
    [DataRow("declare @s int;", "50000, 'x', @s", 50000, "x", 0)]
    public void Throw_Value_VariableArgumentsConvert(string declare, string arguments, int number, string message, int state)
    {
        using var reader = new Simulation().ExecuteReader($"""
            {declare}
            begin try throw {arguments} end try
            begin catch select error_number(), error_message(), error_state() end catch
            """);
        IsTrue(reader.Read());
        AreEqual(number, reader.GetInt32(0));
        AreEqual(message, reader.GetString(1));
        AreEqual(state, reader.GetInt32(2));
    }

    [TestMethod]
    public void Throw_Value_NumberVariableOverflowingInt_RaisesMsg8115()
        => new Simulation().AssertSqlError("declare @n bigint = 3000000000; throw @n, 'x', 1", 8115);

    /// <summary>
    /// Each argument is a literal or a variable: an expression, parentheses or
    /// a unary <c>+</c> is a syntax error, and a number literal that isn't an
    /// <c>int</c> is Msg 1080. A leading <c>-</c> on a literal is accepted.
    /// </summary>
    [TestMethod]
    [DataRow("throw 50000, 'a' + 'b', 1", 102, "Incorrect syntax near '+'.")]
    [DataRow("throw +50000, 'x', 1", 102, "Incorrect syntax near '+'.")]
    [DataRow("throw 50000, 'x', (1)", 102, "Incorrect syntax near '('.")]
    [DataRow("throw 50000, 1, 1", 102, "Incorrect syntax near '1'.")]
    [DataRow("throw null, 'x', 1", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("throw 50000, 'x', null", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("throw 3000000000, 'x', 1", 1080, "The integer value 3000000000 is out of range.")]
    [DataRow("throw 50000.5, 'x', 1", 1080, "The integer value 50000.5 is out of range.")]
    [DataRow("throw - 50000, 'x', 1", 35100, "Error number -50000 in the THROW statement is outside the valid range. Specify an error number in the valid range of 50000 to 2147483647.")]
    public void Throw_Value_ArgumentGrammar(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);

    [TestMethod]
    public void Throw_Value_Caught_ErrorFunctionsReflectValues()
    {
        using var reader = new Simulation().ExecuteReader("""
            begin try throw 50042, 'custom', 9 end try
            begin catch select error_number() as n, error_message() as m, error_state() as st, error_severity() as sev end catch
            """);
        IsTrue(reader.Read());
        AreEqual(50042, reader.GetInt32(0));
        AreEqual("custom", reader.GetString(1));
        AreEqual(9, reader.GetInt32(2));
        AreEqual(16, reader.GetInt32(3));
    }

    [TestMethod]
    public void Throw_Value_WithVariableArgs()
    {
        using var reader = new Simulation().ExecuteReader("""
            declare @num int = 50100, @msg nvarchar(50) = 'param', @st tinyint = 3;
            begin try throw @num, @msg, @st end try
            begin catch select error_number() as n, error_message() as m, error_state() as st end catch
            """);
        IsTrue(reader.Read());
        AreEqual(50100, reader.GetInt32(0));
        AreEqual("param", reader.GetString(1));
        AreEqual(3, reader.GetInt32(2));
    }

    // ---- grammar edge cases ----

    [TestMethod]
    public void EmptyTryBody_Msg102()
        => new Simulation().AssertSqlError(
            "begin try end try begin catch select 1 end catch", 102);

    [TestMethod]
    public void EmptyCatchBody_Works()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "begin try select 1 end try begin catch end catch"));

    [TestMethod]
    public void CaseInsensitiveTryAndCatch()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "BEGIN TrY SELECT cast('abc' as int) END try BEGIN catch SELECT 2 END Catch"));

    [TestMethod]
    public void EmptyCatch_NoError_NoOutput()
    {
        using var reader = new Simulation().ExecuteReader(
            "begin try select 1 end try begin catch end catch");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.NextResult());
    }

    // ---- transactions inside CATCH ----

    [TestMethod]
    public void TranInsideCatch_RollbackUndoesPartial()
    {
        // Standard pattern: catch the error, roll back the transaction.
        // The simulator preserves the implicit transaction state — caught
        // errors don't auto-rollback (matches real SQL Server's XACT_ABORT
        // OFF default).
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var setup = conn.CreateCommand();
        setup.CommandText = "create table t (id int primary key)";
        _ = setup.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            begin try
                begin tran;
                insert into t values (1);
                insert into t values (1);
            end try
            begin catch
                if @@trancount > 0 rollback
            end catch;
            select count(*) from t
            """;
        AreEqual(0, cmd.ExecuteScalar());
    }

    // ---- skip-mode interaction ----

    [TestMethod]
    public void TryCatch_InUnTakenIfBranch_NeverRuns()
    {
        // IF cond is false → TRY body skip-dispatches. The bad conversion
        // doesn't execute (skip-mode gates the actual evaluation), so CATCH
        // never fires.
        using var reader = new Simulation().ExecuteReader(
            "if 1=0 begin try select cast('abc' as int) end try begin catch select 'caught' end catch select 'after'");
        IsTrue(reader.Read());
        AreEqual("after", reader.GetString(0));
    }

    [TestMethod]
    public void TryCatch_AfterErrorInTry_RestOfTryBodySkipped()
    {
        // After bad-conversion in TRY, the subsequent INSERT and SELECT
        // skip; CATCH then runs.
        using var reader = new Simulation().ExecuteReader("""
            create table #t (id int);
            begin try
                select cast('abc' as int)
                insert into #t values (999)
                select 'after-error-in-try'
            end try
            begin catch
                select count(*) from #t
            end catch
            """);
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    // ---- fidelity divergence: parse-time errors ARE caught ----

    [TestMethod]
    public void Try_SelectFromMissingTable_IsCaught_Divergence()
    {
        // Real SQL Server reports Msg 208 *outside* TRY/CATCH because name
        // resolution fires during compile, before TRY's runtime activates.
        // The simulator has no compile / runtime split — parse-time errors
        // surface through the same dispatch path as runtime errors, so the
        // TRY/CATCH wrapper catches them. Documented divergence; same root
        // cause as the un-taken IF Q15 gap.
        AreEqual("caught", new Simulation().ExecuteScalar(
            "begin try select * from nonexistent end try begin catch select 'caught' end catch"));
    }

    // ---- ROWCOUNT interaction ----

    [TestMethod]
    public void Throw_OutsideTry_PropagatesErrorOutOfBatch()
        => new Simulation().AssertSqlError(
            "throw 50500, 'standalone', 1", 50500);

    // ---- THROW is a contextual keyword (not in the reserved list) ----

    [TestMethod]
    public void Throw_UsableAsColumnAlias()
    {
        // Real SQL Server (probe-confirmed 2026-05-12): `select 1 throw`
        // parses as `select 1 AS throw` — THROW is non-reserved so it can
        // be a column alias. Statement adjacency for THROW requires a `;`.
        using var reader = new Simulation().ExecuteReader("select 1 throw");
        IsTrue(reader.Read());
        AreEqual("throw", reader.GetName(0));
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void Throw_UsableAsVariableName()
        => AreEqual(5, new Simulation().ExecuteScalar("declare @throw int = 5; select @throw"));

    [TestMethod]
    public void SelectSemicolonThrow_Works()
    {
        // With `;` separator, the SELECT runs and THROW raises.
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select 1; throw 50000, 'msg', 1";
        _ = Throws<SimulatedSqlException>(() =>
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read() || reader.NextResult()) { }
        });
    }
}
