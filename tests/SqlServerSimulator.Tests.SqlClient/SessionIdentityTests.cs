using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Over-the-wire session identity: a validated TDS login runs as its mapped
/// database user, runs as <c>guest</c> where guest is accessible (master /
/// tempdb / msdb), and is refused (Msg 4060) on a database it can neither map
/// into nor guest into (a user database). The identity scalars report the
/// mapped user / login accordingly.
/// </summary>
[TestClass]
public sealed class SessionIdentityTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string Connect(SimulatedNetworkListener listener, string user, string password, string extra = "") =>
        $"Server=127.0.0.1,{listener.Port};User ID={user};Password={password};TrustServerCertificate=True;Pooling=False;Connect Timeout=15{extra}";

    [TestMethod]
    public async Task MappedLogin_RunsAsMappedUser()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login app with password = 'P@ss1word'; create user mapped for login app");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(Connect(listener, "app", "P@ss1word"));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select current_user + '|' + system_user + '|' + original_login()", connection);
        AreEqual("mapped|app|app", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MappedLogin_ToMaster_LandsAsGuest()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login app with password = 'P@ss1word'; create user mapped for login app");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(Connect(listener, "app", "P@ss1word", ";Database=master"));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select db_name() + '|' + current_user + '|' + system_user", connection);
        AreEqual("master|guest|app", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UnmappedLogin_ToMsdb_LandsAsGuest()
    {
        // guest is accessible in msdb (as in master), so an unmapped login runs
        // as guest there rather than being refused.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login solo with password = 'P@ss1word'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(Connect(listener, "solo", "P@ss1word", ";Database=msdb"));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select db_name() + '|' + current_user + '|' + system_user", connection);
        AreEqual("msdb|guest|solo", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UnmappedLogin_ToUserDatabase_Fails4060()
    {
        // A login with no FOR LOGIN user, connecting to a user database (where
        // guest is inaccessible), is refused — no dbo fallback.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login solo with password = 'P@ss1word'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            // ConnectRetryInterval=1: SqlClient counts 4060 a transient fault
            // and retries the login once, sleeping the interval first — the
            // 10-second default would set this assembly's wall-clock floor.
            // The minimum keeps the retry exercised for a tenth of the wait.
            await using var connection = new SqlConnection(Connect(listener, "solo", "P@ss1word", ";ConnectRetryInterval=1"));
            await connection.OpenAsync(TestContext.CancellationToken);
        });
        AreEqual(4060, ex.Number);
    }

    /// <summary>
    /// LOGIN7 carries the client's workstation and application names; the
    /// session keeps them for <c>HOST_NAME()</c> / <c>APP_NAME()</c> and
    /// <c>sys.dm_exec_sessions</c>. SqlClient sends both from the connection
    /// string's <c>Workstation ID</c> / <c>Application Name</c> keywords.
    /// </summary>
    [TestMethod]
    public async Task Login7_HostAndApplicationNames_ReachTheSession()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(
            listener, TestContext.CancellationToken, ";Workstation ID=wire-ws;Application Name=WireApp");
        await using var command = new SqlCommand(
            "select host_name() + '|' + app_name() + '|' + host_name + '|' + program_name"
            + " from sys.dm_exec_sessions where session_id = @@spid", connection);
        AreEqual("wire-ws|WireApp|wire-ws|WireApp", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
