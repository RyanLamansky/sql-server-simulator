using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Over-the-wire session identity: a validated TDS login runs as its mapped
/// database user, falls back to <c>guest</c> in <c>master</c>, and is refused
/// (Msg 4060) on a database it can neither map into nor guest into. The
/// identity scalars report the mapped user / login accordingly.
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
    public async Task MappedLogin_ToInaccessibleDatabase_Fails4060()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login app with password = 'P@ss1word'; create user mapped for login app");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var connection = new SqlConnection(Connect(listener, "app", "P@ss1word", ";Database=msdb"));
            await connection.OpenAsync(TestContext.CancellationToken);
        });
        AreEqual(4060, ex.Number);
    }

    [TestMethod]
    public async Task UnmappedLogin_ToUserDatabase_StaysDbo()
    {
        // A login with no FOR-LOGIN user anywhere keeps the permissive default
        // (dbo) — the back-compat path the endpoint has always taken.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login solo with password = 'P@ss1word'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(Connect(listener, "solo", "P@ss1word"));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select current_user + '|' + system_user", connection);
        AreEqual("dbo|solo", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
