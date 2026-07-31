using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The TDS session's terminal crash boundary: an exception the typed catch list
/// does not anticipate (and no handler converted to an ERROR token) used to kill
/// the session silently, leaving the client with a bare transport reset. The
/// backstop now emits a best-effort Msg 0 / severity 20 ERROR — the shape real
/// SQL Server sends for an internal failure — so SqlClient surfaces a
/// <c>SqlException</c> and marks the connection dead, the same way it treats a
/// severity ≥ 20 error from a real server.
/// </summary>
[TestClass]
public sealed class CrashBoundaryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task UnexpectedSessionException_SurfacesSevereError_NotTransportReset()
    {
        var simulation = new Simulation
        {
            // A type absent from RunAsync's typed catch list, so it escapes to the
            // terminal backstop rather than a per-statement ERROR conversion.
            NetworkBatchCrashHookForTesting = () => throw new InvalidOperationException("forced internal failure"),
        };
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));

        // Msg 0, severity 20 — a SqlException, not a transport-layer exception.
        AreEqual(0, exception.Number);
        AreEqual((byte)20, exception.Class);
        Contains("A severe error occurred on the current command", exception.Message);
    }

    [TestMethod]
    public async Task SevereError_LeavesConnectionUnusable()
    {
        var simulation = new Simulation
        {
            NetworkBatchCrashHookForTesting = () => throw new InvalidOperationException("forced internal failure"),
        };
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);

        _ = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));

        // Severity 20 is fatal client-side: the connection is no longer open.
        AreNotEqual(System.Data.ConnectionState.Open, connection.State);
    }

    /// <summary>
    /// The statement-level tier below that boundary: a fault raised while
    /// executing one statement is reported as a severity-16 error naming the
    /// exception type, and the session survives. Without it a single unmodeled
    /// statement took the whole connection down, so in a test suite every later
    /// test sharing that connection failed too.
    /// </summary>
    /// <remarks>
    /// The forcing case is a genuine one rather than a hook: a statement whose
    /// feature isn't built raises a bare <see cref="NotSupportedException"/>
    /// that no handler anticipates. What is asserted here is the wire
    /// behavior — severity 16, a diagnosable message, session intact — not the
    /// particular statement, so when this one gains an implementation any other
    /// unmodeled statement serves.
    /// </remarks>
    [TestMethod]
    public async Task UnexpectedStatementFault_ReportsSeverity16AndKeepsTheSession()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var faulting = new SqlCommand("begin distributed transaction", connection))
        {
            var exception = await Assert.ThrowsExactlyAsync<SqlException>(
                async () => await faulting.ExecuteScalarAsync(TestContext.CancellationToken));
            AreEqual(50000, exception.Number);
            AreEqual((byte)16, exception.Class);
            // An unmodeled feature names itself; an unanticipated exception
            // type would instead be reported as "unhandled <Type>", which is
            // what makes a simulator defect findable.
            Contains("isn't modeled", exception.Message);
        }

        AreEqual(System.Data.ConnectionState.Open, connection.State);
        await using var next = new SqlCommand("select 42", connection);
        AreEqual(42, await next.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A modeled SQL error keeps its own number and severity — the statement
    /// tier only catches what the typed handlers didn't.
    /// </summary>
    [TestMethod]
    public async Task ModeledSqlError_IsUnaffectedByTheStatementTier()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1/0", connection);

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(8134, exception.Number);
        AreEqual(System.Data.ConnectionState.Open, connection.State);
    }
}
