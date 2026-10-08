using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A batch whose compile ends without a message — a table variable defaulting
/// to a sequence missing as it compiles — reaches SqlClient as real's does
/// (probed 2026-10-08 against SQL Server 2025): run directly, as a batch that
/// returned nothing; called under a <c>TRY</c>, as the client's own class-11
/// severe error, the connection still open.
/// </summary>
[TestClass]
public sealed class SilentCompileEndWireTests
{
    private const string Missing = "declare @t table (a int default next value for nosuch, b int);";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Batch_ReturnsNothing()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select 1; " + Missing, connection);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
            IsFalse(await reader.NextResultAsync(TestContext.CancellationToken));
        }
        await using var next = new SqlCommand("select 2", connection);
        AreEqual(2, await next.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CallUnderTry_IsTheClientsSevereError()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand($"begin try exec('{Missing}'); select 5 end try begin catch select 6 end catch", connection);
        var ex = await ThrowsAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(0, ex.Number);
        AreEqual((byte)11, ex.Class);
        StartsWith("A severe error occurred on the current command.", ex.Message);
        await using var next = new SqlCommand("select 2", connection);
        AreEqual(2, await next.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
