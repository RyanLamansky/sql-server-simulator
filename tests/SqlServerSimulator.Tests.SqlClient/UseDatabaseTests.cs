using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Mid-session <c>USE</c> over the wire. The database-change ENVCHANGE (+
/// INFO 5701) must precede the response's final DONE — SqlClient's token
/// reader stalls until command timeout on an ENVCHANGE that arrives after
/// the last DONE (probe-confirmed 2026-07-15; go-mssqldb tolerates the late
/// position, so only a real-SqlClient oracle catches the ordering).
/// </summary>
[TestClass]
public sealed class UseDatabaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task UseMaster_ExecuteNonQuery_SwitchesDatabase()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var use = new SqlCommand("use [master]", connection))
            _ = await use.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual("master", connection.Database);
        await using var query = new SqlCommand("select db_name()", connection);
        AreEqual("master", await query.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UseMaster_RaisesChangedDatabaseContextInfoMessage()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        await using var use = new SqlCommand("use [master]", connection);
        _ = await use.ExecuteNonQueryAsync(TestContext.CancellationToken);

        Contains("Changed database context to 'master'.", messages);
    }

    [TestMethod]
    public async Task UseInMultiStatementBatch_WithResultSet()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("use [master] select db_name()", connection);
        AreEqual("master", await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual("master", connection.Database);
    }

    [TestMethod]
    public async Task UseViaRpc_ParameterizedCommand_SwitchesDatabase()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // A parameter forces the sp_executesql RPC path instead of SQLBatch.
        await using var command = new SqlCommand("use [master] select @x", connection);
        _ = command.Parameters.AddWithValue("@x", 1);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual("master", connection.Database);
    }

    [TestMethod]
    public async Task UseThenError_StillSwitchesDatabase()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand("use [master] select * from nosuchtable", connection))
        {
            var ex = await ThrowsExactlyAsync<SqlException>(() => command.ExecuteScalarAsync(TestContext.CancellationToken));
            AreEqual(208, ex.Number);
        }

        AreEqual("master", connection.Database);
        await using var query = new SqlCommand("select db_name()", connection);
        AreEqual("master", await query.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UseRoundTrip_MasterAndBack()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var toMaster = new SqlCommand("use [master]", connection))
            _ = await toMaster.ExecuteNonQueryAsync(TestContext.CancellationToken);
        await using (var back = new SqlCommand("use [simulated]", connection))
            _ = await back.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual("simulated", connection.Database);
        await using var query = new SqlCommand("select db_name()", connection);
        AreEqual("simulated", await query.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UseMissingDatabase_RaisesMsg911_SessionSurvives()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand("use [nosuchdb]", connection))
        {
            var ex = await ThrowsExactlyAsync<SqlException>(() => command.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(911, ex.Number);
        }

        AreEqual("simulated", connection.Database);
        await using var query = new SqlCommand("select db_name()", connection);
        AreEqual("simulated", await query.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
