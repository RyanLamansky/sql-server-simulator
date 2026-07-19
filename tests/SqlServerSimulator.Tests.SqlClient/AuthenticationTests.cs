using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Credential enforcement at the wire endpoint: an empty
/// <c>CREATE LOGIN</c> registry accepts any credentials (the
/// zero-configuration default every other test in this project relies on);
/// once a login exists, the LOGIN7 username/password must match or the
/// connection fails with the probe-confirmed Msg 18456 severity 14 state 1
/// shape and closes.
/// </summary>
[TestClass]
public sealed class AuthenticationTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string CredentialConnectionString(SimulatedNetworkListener listener, string user, string password) =>
        $"Server=127.0.0.1,{listener.Port};User ID={user};Password='{password.Replace("'", "''", StringComparison.Ordinal)}';TrustServerCertificate=True;Pooling=False;Connect Timeout=15";

    private static async Task<SqlException> AssertLoginFails(SimulatedNetworkListener listener, string user, string password, CancellationToken cancellationToken)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var connection = new SqlConnection(CredentialConnectionString(listener, user, password));
            await connection.OpenAsync(cancellationToken);
        });

        AreEqual(18456, ex.Number);
        AreEqual(14, ex.Class);
        AreEqual(1, ex.State);
        AreEqual($"Login failed for user '{user}'.", ex.Message);
        return ex;
    }

    private static async Task AssertLoginSucceeds(SimulatedNetworkListener listener, string user, string password, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CredentialConnectionString(listener, user, password));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(cancellationToken));
    }

    [TestMethod]
    public async Task EmptyRegistry_AcceptsAnyCredentials()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "whoever", "whatever", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task CorrectPassword_Connects()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'S3cret!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "app", "S3cret!Pass", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WrongPassword_Fails18456()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'S3cret!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        _ = await AssertLoginFails(listener, "app", "wrong", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task UnknownUser_Fails18456()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'S3cret!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        _ = await AssertLoginFails(listener, "nosuchuser", "S3cret!Pass", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task EmptyPassword_Fails18456()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'S3cret!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        _ = await AssertLoginFails(listener, "app", "", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task NonAsciiPassword_RoundTripsObfuscation()
    {
        // Non-ASCII UTF-16 units (including a surrogate pair) exercise the
        // LOGIN7 nibble-swap/XOR de-obfuscation and the char-counted length.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = N'pä£€🙂ß'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "app", "pä£€🙂ß", TestContext.CancellationToken);
        _ = await AssertLoginFails(listener, "app", "pä£€ß", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task UserNameLookup_IsCaseInsensitive()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'S3cret!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "APP", "S3cret!Pass", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task AlterLogin_ChangesEnforcedPassword()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'OldPass1!'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "app", "OldPass1!", TestContext.CancellationToken);

        Wire.ExecInProc(simulation, "ALTER LOGIN app WITH PASSWORD = 'NewPass2!'");
        _ = await AssertLoginFails(listener, "app", "OldPass1!", TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "app", "NewPass2!", TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task DropLastLogin_RevertsToAcceptAnything()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app WITH PASSWORD = 'S3cret!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        _ = await AssertLoginFails(listener, "other", "whatever", TestContext.CancellationToken);

        Wire.ExecInProc(simulation, "DROP LOGIN app");
        await AssertLoginSucceeds(listener, "other", "whatever", TestContext.CancellationToken);
    }

    // Probe-confirmed (2026-07-15): login naming a database that can't be
    // opened fails with a two-error sequence — Msg 4060 severity 11 (database
    // name in double quotes) then Msg 18456 severity 14 — and the connection
    // closes. Distinct from mid-session USE, which stays Msg 911.
    [TestMethod]
    public async Task LoginToMissingDatabase_Fails4060Then18456()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var connection = new SqlConnection(
                $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;Database=no_such_db;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
            await connection.OpenAsync(TestContext.CancellationToken);
        });

        AreEqual(4060, ex.Number);
        AreEqual(2, ex.Errors.Count);
        AreEqual("Cannot open database \"no_such_db\" requested by the login. The login failed.", ex.Errors[0].Message);
        AreEqual((byte)11, ex.Errors[0].Class);
        AreEqual((byte)1, ex.Errors[0].State);
        AreEqual(18456, ex.Errors[1].Number);
        AreEqual("Login failed for user 'sa'.", ex.Errors[1].Message);
        AreEqual((byte)14, ex.Errors[1].Class);
    }

    // The LOGIN7 requested database maps genuinely: Database=master lands in
    // the real master system database (every Simulation seeds one), no longer
    // aliased to the default. An empty requested database still maps to the
    // default user database.
    [TestMethod]
    public async Task LoginToMaster_LandsInMaster()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(
            $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;Database=master;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select db_name()", connection);
        AreEqual("master", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task LoginWithNoDatabase_LandsInDefault()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(Wire.ConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select db_name()", connection);
        AreEqual("simulated", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SecondLogin_BothEnforced()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN app1 WITH PASSWORD = 'Pass!One1'");
        Wire.ExecInProc(simulation, "CREATE LOGIN app2 WITH PASSWORD = 'Pass!Two2'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "app1", "Pass!One1", TestContext.CancellationToken);
        await AssertLoginSucceeds(listener, "app2", "Pass!Two2", TestContext.CancellationToken);
        _ = await AssertLoginFails(listener, "app1", "Pass!Two2", TestContext.CancellationToken);
    }
}
