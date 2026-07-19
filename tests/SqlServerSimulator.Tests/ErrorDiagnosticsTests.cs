using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Line-number, server-name, and procedure attribution on
/// <see cref="SimulatedSqlException"/>, mirroring the values real SqlClient
/// surfaces (probe-confirmed against SQL Server 2025, 2026-07-18). Semantics:
/// runtime / bind errors report the failing statement's start line, syntax
/// errors (severity 15) report the offending token's line, procedure-body
/// errors report a line relative to the whole <c>CREATE</c> statement plus the
/// schema-qualified procedure name, and dynamic SQL reports a line relative to
/// the dynamic batch. <see cref="SimulatedError.Server"/> matches the
/// connection data source (real <c>SqlException.Server</c> carries the data
/// source, not <c>@@SERVERNAME</c>). See <c>docs/claude/errors.md</c>.
/// </summary>
[TestClass]
public sealed class ErrorDiagnosticsTests
{
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
}
