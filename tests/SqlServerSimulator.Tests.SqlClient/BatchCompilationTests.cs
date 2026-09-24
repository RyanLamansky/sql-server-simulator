using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A batch that fails to compile answers with its errors alone: no result set
/// precedes them, so <c>ExecuteReader</c> throws, carrying every binder error
/// the batch holds (probed 2026-09-24 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class BatchCompilationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CompileError_ExecuteReaderThrowsBeforeAnyResult()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand(
            "select 1; select nosuch1 from sys.objects; select nosuch2 from sys.objects", connection);
        var ex = await ThrowsAsync<SqlException>(async () =>
            await command.ExecuteReaderAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(
            new[] { "Invalid column name 'nosuch1'.", "Invalid column name 'nosuch2'." },
            ex.Errors.Cast<SqlError>().Select(e => e.Message).ToArray());

        // The connection is ready for the next batch.
        await using var next = new SqlCommand("select 2", connection);
        AreEqual(2, await next.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
