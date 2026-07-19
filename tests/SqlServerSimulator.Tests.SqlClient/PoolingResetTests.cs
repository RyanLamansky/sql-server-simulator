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
}
