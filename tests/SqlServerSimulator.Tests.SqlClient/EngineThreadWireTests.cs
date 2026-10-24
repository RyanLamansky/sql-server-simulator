using System.Diagnostics;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The endpoint runs its sessions' statements on threads of the engine's own,
/// never the shared .NET thread pool, so sessions waiting in a statement leave
/// the pool to everything else in the process — new logins among it.
/// </summary>
[TestClass]
public sealed class EngineThreadWireTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Many times more sessions than the pool's floor start waiting at once —
    /// in a <c>WAITFOR</c>, or on a row another session's open transaction
    /// holds — and a login made meanwhile still gets in at once. Were each
    /// waiting statement to hold a pool thread, the login's own work would
    /// queue behind them all while the pool added threads a few a second.
    /// </summary>
    // Sequential phase: the login's time is the time its continuations wait
    // for this process's shared pool, which tests running beside it can hold —
    // a whole-solution run on a 20-core box broke the bound twice, while the
    // test alone and its project alone never did.
    [TestMethod]
    [DoNotParallelize]
    [DataRow("waitfor delay '00:01:00'", "WAITFOR")]
    [DataRow("select v from held where id = 1", "LCK_M_S")]
    public async Task ManyWaitingSessions_LeaveLoginsFast(string wait, string waitType)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var observer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var holder = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var hold = new SqlCommand("create table held (id int primary key, v int); insert held values (1, 1); begin tran; update held set v = 2 where id = 1", holder))
            _ = await hold.ExecuteNonQueryAsync(TestContext.CancellationToken);

        ThreadPool.GetMinThreads(out var floor, out _);
        var count = (4 * floor) + 32;
        var sessions = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Wire.OpenAsync(listener, TestContext.CancellationToken)));
        using var release = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        try
        {
            var waiting = sessions.Select(async session =>
            {
                await using var command = new SqlCommand(wait, session) { CommandTimeout = 0 };
                try
                {
                    _ = await command.ExecuteNonQueryAsync(release.Token);
                }
                catch (Exception ex) when ((ex is SqlException or OperationCanceledException) && release.IsCancellationRequested)
                {
                }
            }).ToArray();

            var login = Stopwatch.StartNew();
            await using (var latecomer = await Wire.OpenAsync(listener, TestContext.CancellationToken))
            await using (var query = new SqlCommand("select 1", latecomer))
                AreEqual(1, await query.ExecuteScalarAsync(TestContext.CancellationToken));
            IsLessThan(5_000, login.ElapsedMilliseconds, "a login behind the waiting sessions");

            // Every one of them was waiting at once, the login's time included.
            await using var probe = new SqlCommand($"select count(*) from sys.dm_exec_requests where wait_type = '{waitType}'", observer);
            var settled = Stopwatch.StartNew();
            while ((int)(await probe.ExecuteScalarAsync(TestContext.CancellationToken))! < count)
            {
                IsLessThan(10_000, settled.ElapsedMilliseconds, "the sessions never all waited");
                await Task.Delay(10, TestContext.CancellationToken);
            }

            await release.CancelAsync();
            await Task.WhenAll(waiting);
        }
        finally
        {
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
    }
}
