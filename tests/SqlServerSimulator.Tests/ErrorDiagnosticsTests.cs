using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Line-number, server-name, and procedure attribution on
/// <see cref="SimulatedSqlException"/>, mirroring the values real SqlClient
/// surfaces (probe-confirmed against SQL Server 2025, 2026-07-18/20). Semantics:
/// runtime / bind errors report the failing statement's start line, syntax
/// errors (severity 15) report the offending token's line, procedure- and
/// trigger-body errors report a line relative to the whole <c>CREATE</c>
/// statement (plus the schema-qualified procedure name, or the trigger's
/// unqualified name), scalar-UDF / TVF / view body errors report the outer
/// invoking statement's line with no procedure (real inlines them for
/// attribution), and dynamic SQL reports a line relative to the dynamic batch.
/// <see cref="SimulatedError.Server"/> matches the connection data source (real
/// <c>SqlException.Server</c> carries the data source, not <c>@@SERVERNAME</c>).
/// See <c>docs/claude/errors.md</c>.
/// </summary>
[TestClass]
public sealed class ErrorDiagnosticsTests
{
    /// <summary>
    /// Runs <paramref name="tryBody"/> inside a <c>BEGIN TRY … END TRY BEGIN
    /// CATCH</c> that projects <c>ERROR_LINE()</c> / <c>ERROR_PROCEDURE()</c>,
    /// returning the CATCH-observed diagnostics. The erroring statement may emit
    /// a leading empty result set (real does too), so this drains to the
    /// two-column CATCH set. <c>BEGIN TRY</c> is line 1, so a statement on the
    /// first body line reports <c>ERROR_LINE</c> 2 when the error attributes to
    /// that outer statement.
    /// </summary>
    private static (int Line, string? Procedure) CatchDiagnostics(Simulation sim, string tryBody)
    {
        using var reader = sim.ExecuteReader(
            $"begin try\n{tryBody}\nend try\nbegin catch\nselect error_line() l, error_procedure() p\nend catch");
        do
        {
            if (reader.FieldCount == 2 && reader.Read())
                return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        } while (reader.NextResult());
        throw new InvalidOperationException("no CATCH result set");
    }

    [TestMethod]
    public void RuntimeError_ReportsFailingStatementStartLine()
        => AreEqual(2, new Simulation().AssertSqlError("declare @x int\nset @x = 1 / 0", 8134).LineNumber);

    /// <summary>
    /// The SET spans lines 2-3; the divide-by-zero token sits on line 3, but
    /// the reported line is the statement start (line 2), matching real.
    /// </summary>
    [TestMethod]
    public void RuntimeError_SpanningLines_ReportsStatementStartNotExpressionLine()
        => AreEqual(2, new Simulation().AssertSqlError("declare @x int\nset @x =\n 1 / 0", 8134).LineNumber);

    [TestMethod]
    public void BindError_MissingTable_ReportsStatementStartLine()
        => AreEqual(2, new Simulation().AssertSqlError("declare @x int\nselect * from no_such_table_xyz", 208).LineNumber);

    [TestMethod]
    public void ConstraintViolation_ReportsFailingInsertLine()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key); insert t values (1)");
        AreEqual(2, sim.AssertSqlError("declare @x int\ninsert t values (1)", 2627).LineNumber);
    }

    /// <summary>
    /// Severity-15 syntax errors report the token line, not the statement
    /// start: the batch's first statement is on line 1, the bad token on 2.
    /// </summary>
    [TestMethod]
    public void SyntaxError_ReportsOffendingTokenLine()
        => AreEqual(2, new Simulation().AssertSqlError("declare @x int\nselect )", 102).LineNumber);

    [TestMethod]
    public void Error_ServerMatchesConnectionDataSource()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        var ex = sim.AssertSqlError("select 1 / 0", 8134);
        AreEqual(connection.DataSource, ex.Server);
        AreEqual(connection.DataSource, ex.Errors[0].Server);
    }

    [TestMethod]
    public void TopLevelError_HasNoProcedure()
        => IsEmpty(new Simulation().AssertSqlError("select 1 / 0", 8134).Procedure);

    [TestMethod]
    public void ProcedureBodyError_ReportsCreateRelativeLineAndQualifiedName()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p_boom as\nbegin\n declare @x int\n set @x = 1 / 0\nend");
        var ex = sim.AssertSqlError("exec dbo.p_boom", 8134);
        // CREATE line 1, BEGIN line 2, DECLARE line 3, failing SET line 4.
        AreEqual(4, ex.LineNumber);
        AreEqual("dbo.p_boom", ex.Procedure);
    }

    [TestMethod]
    public void NestedProcedureError_ReportsInnermostFrame()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.inner_boom as\nbegin\n declare @x int\n set @x = 1 / 0\nend",
            "create procedure dbo.outer_p as\nbegin\n exec dbo.inner_boom\nend");
        var ex = sim.AssertSqlError("exec dbo.outer_p", 8134);
        AreEqual("dbo.inner_boom", ex.Procedure);
        AreEqual(4, ex.LineNumber);
    }

    [TestMethod]
    public void DynamicSql_ReportsLineRelativeToDynamicBatch_NoProcedure()
    {
        var ex = new Simulation().AssertSqlError("exec('declare @x int\nset @x = 1 / 0')", 8134);
        AreEqual(2, ex.LineNumber);
        IsEmpty(ex.Procedure);
    }

    [TestMethod]
    public void ThrowValueForm_ReportsThrowStatementLine()
    {
        var ex = new Simulation().AssertSqlError("declare @x int\nthrow 51000, N'boom', 1", 51000);
        AreEqual(2, ex.LineNumber);
    }

    [TestMethod]
    public void ThrowReRaise_PreservesOriginalErrorLine()
    {
        // A bare THROW; re-raising a divide-by-zero from line 3 preserves that
        // line rather than reporting the THROW's own line (probe-confirmed).
        var ex = new Simulation().AssertSqlError(
            "declare @x int\nbegin try\n set @x = 1 / 0\nend try\nbegin catch\n throw\nend catch",
            8134);
        AreEqual(3, ex.LineNumber);
    }

    // --- Body-type attribution (probe-confirmed SQL Server 2025, 2026-07-20) ---
    // Scalar-UDF / inline-TVF / multi-statement-TVF / view bodies inline for
    // attribution: an error inside them reports the OUTER invoking statement's
    // line with NO procedure name — not a body-relative line. Only procedures
    // and triggers push their own attribution frame.

    // Divide by a column value (dbo.nums.d = 0) rather than a literal 0: the
    // literal folds at CREATE time, whereas real SQL Server (and this shape)
    // errors at runtime, which is what body attribution is about.
    private static Simulation WithBoomBodies()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int); create table dbo.nums (n int, d int); insert dbo.nums values (1, 0)",
            "create function dbo.udf_boom() returns int as\nbegin\n return (select n / d from dbo.nums)\nend",
            "create function dbo.mstvf_boom() returns @r table (x int) as\nbegin\n insert into @r select n / d from dbo.nums\n return\nend",
            "create function dbo.itvf_boom() returns table as\nreturn (select n / d as x from dbo.nums)",
            "create view dbo.v_boom as select n / d as c from dbo.nums",
            "create trigger dbo.tr_boom on dbo.t after insert as\nbegin\n declare @z int\n select @z = n / d from dbo.nums\nend",
            "create procedure dbo.p_calls_udf as\nbegin\n select dbo.udf_boom()\nend");
        return sim;
    }

    [TestMethod]
    public void ScalarUdfBodyError_ReportsOuterStatementLine_NoProcedure()
    {
        var sim = WithBoomBodies();
        var ex = sim.AssertSqlError("select dbo.udf_boom()", 8134);
        AreEqual(1, ex.LineNumber);
        IsEmpty(ex.Procedure);
        // Inside the TRY the SELECT sits on batch line 2; attribution follows it.
        AreEqual((2, null), CatchDiagnostics(sim, "select dbo.udf_boom()"));
    }

    [TestMethod]
    public void MultiStatementTvfBodyError_ReportsOuterStatementLine_NoProcedure()
    {
        var sim = WithBoomBodies();
        var ex = sim.AssertSqlError("select * from dbo.mstvf_boom()", 8134);
        AreEqual(1, ex.LineNumber);
        IsEmpty(ex.Procedure);
        AreEqual((2, null), CatchDiagnostics(sim, "select * from dbo.mstvf_boom()"));
    }

    [TestMethod]
    public void InlineTvfBodyError_ReportsOuterStatementLine_NoProcedure()
    {
        var sim = WithBoomBodies();
        var ex = sim.AssertSqlError("select * from dbo.itvf_boom()", 8134);
        AreEqual(1, ex.LineNumber);
        IsEmpty(ex.Procedure);
        AreEqual((2, null), CatchDiagnostics(sim, "select * from dbo.itvf_boom()"));
    }

    [TestMethod]
    public void ViewBodyError_ReportsOuterStatementLine_NoProcedure()
    {
        var sim = WithBoomBodies();
        var ex = sim.AssertSqlError("select c from dbo.v_boom", 8134);
        AreEqual(1, ex.LineNumber);
        IsEmpty(ex.Procedure);
        AreEqual((2, null), CatchDiagnostics(sim, "select c from dbo.v_boom"));
    }

    [TestMethod]
    public void TriggerBodyError_ReportsCreateRelativeLineAndUnqualifiedName()
    {
        var sim = WithBoomBodies();
        // CREATE+AS line 1, BEGIN line 2, DECLARE line 3, failing SELECT line 4.
        // The trigger name is UNQUALIFIED (tr_boom, not dbo.tr_boom) — probed.
        var ex = sim.AssertSqlError("insert dbo.t values (1)", 8134);
        AreEqual(4, ex.LineNumber);
        AreEqual("tr_boom", ex.Procedure);
        AreEqual((4, "tr_boom"), CatchDiagnostics(sim, "insert dbo.t values (1)"));
    }

    [TestMethod]
    public void ProcedureCallingUdf_AttributesToProcedureNotUdf()
    {
        var sim = WithBoomBodies();
        // The UDF inlines, so the enclosing procedure's SELECT (body line 3)
        // wins with the schema-qualified proc name.
        var ex = sim.AssertSqlError("exec dbo.p_calls_udf", 8134);
        AreEqual(3, ex.LineNumber);
        AreEqual("dbo.p_calls_udf", ex.Procedure);
        AreEqual((3, "dbo.p_calls_udf"), CatchDiagnostics(sim, "exec dbo.p_calls_udf"));
    }

    // --- Tokenizer-thrown multi-line-token lines (probe-confirmed 2026-07-20) ---
    // Msg 105 (unclosed string) reports the line the literal OPENED on; Msg 113
    // (unclosed block comment) reports the END-OF-INPUT line it ran to.

    /// <summary>
    /// Quote opens on line 3, body runs to EOF on line 4 → reports 3.
    /// </summary>
    [TestMethod]
    public void UnclosedString_ReportsOpeningQuoteLine()
        => AreEqual(3, new Simulation().AssertSqlError("select 1\nselect 2\nselect 'abc\ndef", 105).LineNumber);

    [TestMethod]
    public void UnclosedString_OpeningLineTwo_ReportsLineTwo()
        => AreEqual(2, new Simulation().AssertSqlError("select 1\nselect 'abc\ndef\nghi", 105).LineNumber);

    /// <summary>
    /// Comment opens on line 3, runs to EOF on line 4 → reports 4 (not 3).
    /// </summary>
    [TestMethod]
    public void UnclosedBlockComment_ReportsEndOfInputLine()
        => AreEqual(4, new Simulation().AssertSqlError("select 1\nselect 2\n/* abc\ndef", 113).LineNumber);

    [TestMethod]
    public void UnclosedBlockComment_LongerRun_ReportsFinalLine()
        => AreEqual(6, new Simulation().AssertSqlError("select 1\nselect 2\nselect 3\n/* a\nb\nc", 113).LineNumber);
}
