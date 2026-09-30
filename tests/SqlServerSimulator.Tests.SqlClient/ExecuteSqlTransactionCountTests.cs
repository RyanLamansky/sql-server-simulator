using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A parameterized command reaches the server as an <c>sp_executesql</c> RPC,
/// which real judges as a procedure is: leaving <c>@@TRANCOUNT</c> changed is
/// Msg 266 after the RPC's results (probed 2026-09-30 against SQL Server 2025
/// through SqlClient 7.0.2). The transaction stays open across the RPCs.
/// </summary>
[TestClass]
public sealed class ExecuteSqlTransactionCountTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AnOpenedTransaction_Is266_AndStaysOpenForTheNextRpc()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var open = new SqlCommand("begin tran; select @@trancount, @zz", connection))
        {
            _ = open.Parameters.AddWithValue("@zz", 1);
            var error = await ThrowsExactlyAsync<SqlException>(async () => await open.ExecuteScalarAsync(TestContext.CancellationToken));
            AreEqual(266, error.Number);
            AreEqual("Transaction count after EXECUTE indicates a mismatching number of BEGIN and COMMIT statements. Previous count = 0, current count = 1.", error.Errors[0].Message);
        }

        await using var next = new SqlCommand("select @@trancount, @zz", connection);
        _ = next.Parameters.AddWithValue("@zz", 1);
        AreEqual(1, await next.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ABalancedBody_RaisesNothing()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("begin tran; commit; select @@trancount + @zz", connection);
        _ = command.Parameters.AddWithValue("@zz", 1);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
