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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);

        _ = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));

        // Severity 20 is fatal client-side: the connection is no longer open.
        AreNotEqual(System.Data.ConnectionState.Open, connection.State);
    }
}
