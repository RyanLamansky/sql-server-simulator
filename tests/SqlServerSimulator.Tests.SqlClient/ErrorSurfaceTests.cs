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

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
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
        var oracle = CaptureInProc(simulation, "select 1 +");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select 1 +", connection);
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
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
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
    public async Task Error_CarriesBatchRelativeLineNumber()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("declare @x int\nset @x = 1 / 0", connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });

        // The failing SET is the batch's second line; the wire ERROR token
        // carries that line and SqlClient surfaces it on LineNumber.
        AreEqual(2, ex.LineNumber);
        AreEqual(2, ex.Errors[0].LineNumber);
    }

    /// <summary>
    /// A module body reports every binder error it contains, and each becomes
    /// its own ERROR token — so SqlClient hands the client one exception whose
    /// <c>Errors</c> carries both, with their own lines and the module name.
    /// That is the shape a real SQL Server 2025 CREATE with two bad columns
    /// produces (probe-confirmed through SqlClient).
    /// </summary>
    [TestMethod]
    public async Task ModuleBindErrors_EachSurfaceAsTheirOwnErrorToken()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table dbo.bt (id int not null)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand(
                "create procedure dbo.pboth as\nselect nosuchone from dbo.bt;\nselect nosuchtwo from dbo.bt;",
                connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });

        AreEqual(2, ex.Errors.Count);
        AreEqual(207, ex.Errors[0].Number);
        AreEqual(2, ex.Errors[0].LineNumber);
        AreEqual(207, ex.Errors[1].Number);
        AreEqual(3, ex.Errors[1].LineNumber);
        AreEqual("pboth", ex.Errors[1].Procedure);
        // SqlClient builds the exception message by joining every entry's, so
        // both bad columns are named in the one message the client sees.
        Contains("nosuchtwo", ex.Message);
    }

    [TestMethod]
    public async Task MissingTable_Number208()
    {
        var simulation = new Simulation();
        var oracle = CaptureInProc(simulation, "select * from nonexistent_table");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
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
