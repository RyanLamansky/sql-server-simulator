using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A plan cached by one wire session and replayed by another runs as the
/// replaying session: meeting a third session's lock after the compiling
/// session has closed is that session's own lock timeout, and the connection
/// stays usable afterward.
/// </summary>
[TestClass]
public sealed class PlanCacheSessionWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReplayAfterCompilerClosed_MeetingALock_TimesOutAndKeepsTheSession()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int primary key, v int); insert t values (1, 1), (2, 2)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);

        await using (var compiler = await Wire.OpenAsync(listener, TestContext.CancellationToken))
        {
            await using var compile = new SqlCommand("select count(*) from t", compiler);
            AreEqual(2, await compile.ExecuteScalarAsync(TestContext.CancellationToken));
        }

        await using var writer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var hold = new SqlCommand("begin tran; update t set v = 10 where id = 1", writer))
            _ = await hold.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var timeout = new SqlCommand("set lock_timeout 50", reader))
            _ = await timeout.ExecuteNonQueryAsync(TestContext.CancellationToken);
        await using (var replay = new SqlCommand("select count(*) from t", reader))
        {
            var error = await ThrowsAsync<SqlException>(async () => await replay.ExecuteScalarAsync(TestContext.CancellationToken));
            AreEqual(1222, error.Number);
        }

        await using var alive = new SqlCommand("select 1", reader);
        AreEqual(1, await alive.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
