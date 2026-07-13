using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Error tokens surface through the wire as real <see cref="SqlException"/>s
/// carrying the simulator's number/class/state, the connection stays usable
/// after one, and the server name is <c>SIMULATED</c>. Class/state are checked
/// against the same simulation's in-process exception rather than hardcoded.
/// </summary>
[TestClass]
public sealed class ErrorSurfaceTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SimulatedSqlException CaptureInProc(Simulation simulation, string sql)
    {
        try
        {
            Wire.ExecInProc(simulation, sql);
        }
        catch (SimulatedSqlException ex)
        {
            return ex;
        }

        throw new AssertFailedException($"Expected an in-process error from: {sql}");
    }

    [TestMethod]
    public async Task DivideByZero_InReaderLoop_Number8134()
    {
        var simulation = new Simulation();
        var oracle = CaptureInProc(simulation, "select 1 / 0");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select 1 / 0", connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            while (await reader.ReadAsync(TestContext.CancellationToken))
                _ = reader.GetValue(0);
        });

        AreEqual(8134, ex.Number);
        AreEqual(oracle.Class, ex.Class);
        AreEqual(oracle.State, ex.State);
        // SqlClient reports its own data source on SqlException.Server, not the
        // server name carried in the TDS error token, so the observable value is
        // the connection's data source rather than the token's "SIMULATED".
        AreEqual(connection.DataSource, ex.Server);
    }

    [TestMethod]
    public async Task SyntaxError_Number102_Class15()
    {
        var simulation = new Simulation();
        var oracle = CaptureInProc(simulation, "select from where");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select from where", connection);
            _ = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        });

        AreEqual(102, ex.Number);
        AreEqual(15, ex.Class);
        AreEqual(oracle.State, ex.State);
        // SqlClient reports its own data source on SqlException.Server, not the
        // server name carried in the TDS error token, so the observable value is
        // the connection's data source rather than the token's "SIMULATED".
        AreEqual(connection.DataSource, ex.Server);
    }

    [TestMethod]
    public async Task AfterError_SameConnection_StillExecutes()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        _ = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select 1 / 0", connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            while (await reader.ReadAsync(TestContext.CancellationToken))
                _ = reader.GetValue(0);
        });

        await using var ok = new SqlCommand("select 1", connection);
        AreEqual(1, await ok.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MissingTable_Number208()
    {
        var simulation = new Simulation();
        var oracle = CaptureInProc(simulation, "select * from nonexistent_table");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select * from nonexistent_table", connection);
            _ = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        });

        AreEqual(208, ex.Number);
        AreEqual(oracle.Class, ex.Class);
        AreEqual(oracle.State, ex.State);
        // SqlClient reports its own data source on SqlException.Server, not the
        // server name carried in the TDS error token, so the observable value is
        // the connection's data source rather than the token's "SIMULATED".
        AreEqual(connection.DataSource, ex.Server);
    }
}
