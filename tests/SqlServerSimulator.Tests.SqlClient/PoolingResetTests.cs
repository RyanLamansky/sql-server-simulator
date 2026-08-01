using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A pooled physical connection is reset on reuse: session temp tables from
/// the prior logical connection are gone, and the reopen itself works. Drives
/// the reset-connection status bit in the batch header.
/// </summary>
[TestClass]
public sealed class PoolingResetTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PooledReopen_ResetsSessionTempTables()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = Wire.PooledConnectionString(listener);

        await using (var first = new SqlConnection(connectionString))
        {
            await first.OpenAsync(TestContext.CancellationToken);
            await using var create = new SqlCommand("create table #t (id int); insert #t values (1)", first);
            _ = await create.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await using var second = new SqlConnection(connectionString);
        await second.OpenAsync(TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var gone = new SqlCommand("select * from #t", second);
            _ = await gone.ExecuteReaderAsync(TestContext.CancellationToken);
        });
        AreEqual(208, ex.Number);

        await using var ok = new SqlCommand("select 1", second);
        AreEqual(1, await ok.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// An application role set on a pooled connection does not survive the
    /// reset: the reused physical connection is back to its login's own
    /// principal and is usable. Real SQL Server instead refuses to reset a
    /// connection with an active application role and kills the session
    /// (Msg 596, class 21, on the reopen) — the simulator's reset clears the
    /// role rather than poisoning the pooled connection.
    /// </summary>
    [TestMethod]
    public async Task PooledReopen_ClearsAnActiveApplicationRole()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        Wire.ExecInProc(simulation, "create application role app1 with password = 'App!Pass123'");
        var connectionString = Wire.PooledConnectionString(listener);

        await using (var first = new SqlConnection(connectionString))
        {
            await first.OpenAsync(TestContext.CancellationToken);
            await using var set = new SqlCommand("exec sp_setapprole 'app1', 'App!Pass123'", first);
            _ = await set.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await using var during = new SqlCommand("select user_name()", first);
            AreEqual("app1", await during.ExecuteScalarAsync(TestContext.CancellationToken));
        }

        await using var second = new SqlConnection(connectionString);
        await second.OpenAsync(TestContext.CancellationToken);
        await using var after = new SqlCommand("select user_name()", second);
        AreEqual("dbo", await after.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
