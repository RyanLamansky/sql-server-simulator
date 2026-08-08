using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SSMS Query Store probe batch driven over the loopback wire through a
/// real SqlClient connection. It gates on
/// <c>OBJECT_ID(N'[sys].[database_query_store_options]')</c> resolving, reads
/// <c>actual_state</c> (2, the READ_WRITE a fresh SQL Server 2025 database
/// inherits from <c>model</c>), then falls to the ELSE of an IF EXISTS over
/// the always-empty <c>sys.query_store_runtime_stats</c>.
/// </summary>
[TestClass]
public sealed class QueryStoreProbeWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task SsmsQueryStoreProbeBatch_OverWire_ReturnsStateThenZero()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "IF OBJECT_ID (N'[sys].[database_query_store_options]') IS NOT NULL " +
            "BEGIN " +
            "SELECT ISNULL(actual_state, -2) FROM sys.database_query_store_options; " +
            "IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats) SELECT 1 ELSE SELECT 0; " +
            "END",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(2, Convert.ToInt32(reader.GetValue(0)));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));

        IsTrue(await reader.NextResultAsync(TestContext.CancellationToken));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(0, Convert.ToInt32(reader.GetValue(0)));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));

        IsFalse(await reader.NextResultAsync(TestContext.CancellationToken));
    }
}
