using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>KILL</c> over the TDS endpoint: the victim's error tokens as SqlClient
/// surfaces them (captured 2026-09-30 against SQL Server 2025 through
/// SqlClient 7.0.2), and the rollback either way.
/// </summary>
[TestClass]
public sealed class KillWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunningVictim_GetsMsg596ThenSqlClientsSevereError_AndTheConnectionCloses()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (a int)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var victim = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var killer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var spidCommand = new SqlCommand("select @@spid", victim);
        var spid = Convert.ToInt32(await spidCommand.ExecuteScalarAsync(TestContext.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        await using var running = new SqlCommand("begin tran; insert t values (1); waitfor delay '00:00:30'", victim);
        var execution = running.ExecuteNonQueryAsync(TestContext.CancellationToken);
        await using (var probe = new SqlCommand($"select count(*) from sys.dm_exec_requests where session_id = {spid}", killer))
        {
            while ((int)(await probe.ExecuteScalarAsync(TestContext.CancellationToken))! == 0)
                await Task.Delay(50, TestContext.CancellationToken);
        }
        await using (var kill = new SqlCommand($"kill {spid}", killer))
            _ = await kill.ExecuteNonQueryAsync(TestContext.CancellationToken);

        var error = await ThrowsExactlyAsync<SqlException>(async () => await execution);
        AreEqual(596, error.Errors[0].Number);
        AreEqual(21, error.Errors[0].Class);
        AreEqual(0, error.Errors[1].Number);
        AreEqual(System.Data.ConnectionState.Closed, victim.State);
        await using var count = new SqlCommand("select count(*) from t", killer);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task IdleVictimWithATransaction_RollsBackAtOnce_AndItsConnectionIsGone()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (a int)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var victim = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var killer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var spidCommand = new SqlCommand("select @@spid", victim);
        var spid = Convert.ToInt32(await spidCommand.ExecuteScalarAsync(TestContext.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await using (var begin = new SqlCommand("begin tran; insert t values (1)", victim))
            _ = await begin.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using (var kill = new SqlCommand($"kill {spid}", killer))
            _ = await kill.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var count = new SqlCommand("select count(*) from t", killer);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
        await using var next = new SqlCommand("select 1", victim);
        _ = await ThrowsAsync<SqlException>(async () => await next.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task KillRefusals_ArriveAsTheirOwnErrors_OverTheWire()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        foreach (var (statement, number) in new[] { ("kill 9999", 6106), ("kill 0", 6101), ("kill 9999 with commit", 6108), ("begin tran; kill 9999", 6115) })
        {
            await using var command = new SqlCommand(statement, connection);
            var error = await ThrowsExactlyAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(number, error.Number);
        }
    }
}
