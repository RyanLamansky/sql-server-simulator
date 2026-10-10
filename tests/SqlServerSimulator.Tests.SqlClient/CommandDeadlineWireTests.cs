using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A command over the wire runs until it finishes or its client sends an
/// attention: SqlClient enforces <c>CommandTimeout</c> itself, and real SQL
/// Server cuts nothing off on its own. The endpoint's commands once kept the
/// in-process default of 30 seconds, so every query running longer failed
/// with a severe error (Msg 0) whatever the client's timeout. Each test
/// shortens the simulation's in-process default to one second and runs a
/// command for two, so the regression shows without a 30-second wait;
/// <see cref="AttentionTests"/> covers the client's own timeout.
/// </summary>
[TestClass]
public sealed class CommandDeadlineWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string OutlastsTheDefault = "waitfor delay '00:00:02'; select 1";

    private static Simulation ShortDefaultSimulation() => new() { DefaultCommandTimeout = 1 };

    [TestMethod]
    public async Task Batch_OutlastingTheInProcessDefault_Completes()
    {
        var simulation = ShortDefaultSimulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand(OutlastsTheDefault, connection) { CommandTimeout = 3600 };
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Rpc_OutlastingTheInProcessDefault_Completes()
    {
        var simulation = ShortDefaultSimulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("waitfor delay '00:00:02'; select @p", connection) { CommandTimeout = 0 };
        _ = command.Parameters.AddWithValue("@p", 7);
        AreEqual(7, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task StoredProcedureRpc_OutlastingTheInProcessDefault_Completes()
    {
        var simulation = ShortDefaultSimulation();
        Wire.ExecInProc(simulation, "create procedure dbo.slow as begin waitfor delay '00:00:02'; select 3 end");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.slow", connection) { CommandType = System.Data.CommandType.StoredProcedure, CommandTimeout = 3600 };
        AreEqual(3, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MarsBatch_OutlastingTheInProcessDefault_Completes()
    {
        var simulation = ShortDefaultSimulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";MultipleActiveResultSets=True");

        await using var command = new SqlCommand(OutlastsTheDefault, connection) { CommandTimeout = 3600 };
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// The client's own timeout still ends a command shorter than the
    /// in-process default, through its attention, and the session stays usable.
    /// </summary>
    [TestMethod]
    public async Task ClientTimeout_StillEndsTheCommandThroughItsAttention()
    {
        var simulation = new Simulation { DefaultCommandTimeout = 3600 };
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand("waitfor delay '00:00:30'", connection) { CommandTimeout = 1 })
        {
            var error = await ThrowsExactlyAsync<SqlException>(
                async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(-2, error.Number);
        }

        await using var probe = new SqlCommand("select 42", connection);
        AreEqual(42, await probe.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
