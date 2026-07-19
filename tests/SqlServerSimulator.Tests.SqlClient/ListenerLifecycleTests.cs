using System.Data.Common;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Listener lifetime and isolation: disposal tears live sessions down, the
/// port frees for rebind, distinct simulations route independently, and the
/// endpoint tolerates concurrent connections.
/// </summary>
[TestClass]
public sealed class ListenerLifecycleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Dispose_WhileConnectionOpen_SubsequentCommandThrows()
    {
        var simulation = new Simulation();
        var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await listener.DisposeAsync();

        var threw = false;
        try
        {
            await using var command = new SqlCommand("select 1", connection);
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
        }
        catch (DbException)
        {
            threw = true;
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        IsTrue(threw);
    }

    [TestMethod]
    public async Task Dispose_ReleasesPort_ForRebind()
    {
        var simulation = new Simulation();
        var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var port = listener.Port;
        await listener.DisposeAsync();

        await using var rebound = await simulation.ListenLocalAsync(port, TestContext.CancellationToken);
        AreEqual(port, rebound.Port);

        await using var connection = await Wire.OpenAsync(rebound, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TwoListeners_DistinctSimulations_RouteIndependently()
    {
        var first = new Simulation();
        Wire.ExecInProc(first, "create table t (v int); insert t values (111)");
        var second = new Simulation();
        Wire.ExecInProc(second, "create table t (v int); insert t values (222)");

        await using var firstListener = await first.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var secondListener = await second.ListenLocalAsync(0, TestContext.CancellationToken);

        await using var firstConnection = await Wire.OpenAsync(firstListener, TestContext.CancellationToken);
        await using var secondConnection = await Wire.OpenAsync(secondListener, TestContext.CancellationToken);

        await using var firstCommand = new SqlCommand("select v from t", firstConnection);
        await using var secondCommand = new SqlCommand("select v from t", secondConnection);
        AreEqual(111, await firstCommand.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(222, await secondCommand.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EightParallelConnections_TwentyQueriesEach_AllSucceed()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var token = TestContext.CancellationToken;

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = await Wire.OpenAsync(listener, token);
            for (var i = 0; i < 20; i++)
            {
                await using var command = new SqlCommand($"select {i}", connection);
                AreEqual(i, await command.ExecuteScalarAsync(token));
            }
        });

        await Task.WhenAll(tasks);
    }
}
