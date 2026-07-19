using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Loopback oracle: real <c>Microsoft.Data.SqlClient</c> connecting to the
/// simulator's TDS endpoint over TCP, validating the wire protocol end to
/// end with the genuine client.
/// </summary>
[TestClass]
public sealed class NetworkListenerTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string ConnectionString(SimulatedNetworkListener listener, string extra = "") =>
        $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;TrustServerCertificate=True;Pooling=False;Connect Timeout=15{extra}";

    [TestMethod]
    public async Task SelectOne_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(ConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ListenLocalAsync_PortZero_AssignsEphemeralPort()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        IsGreaterThan(0, listener.Port);
        AreNotEqual(1433, listener.Port);
    }

    // A network-reachable endpoint must never run the loopback listener's
    // accept-anyone-until-CREATE-LOGIN credential model, so the call itself
    // refuses until a login exists.
    [TestMethod]
    public async Task ListenNetworkAsync_WithoutLogins_Throws()
    {
        var simulation = new Simulation();
        var ex = await ThrowsExactlyAsync<InvalidOperationException>(
            () => simulation.ListenNetworkAsync(0, TestContext.CancellationToken));
        Assert.Contains("CREATE LOGIN", ex.Message);
    }

    // Not run automatically: binding all interfaces triggers a Windows
    // Firewall consent prompt and could trip network policy on CI runners.
    // Re-add [TestMethod] to verify the off-loopback path manually.
    // [TestMethod]
    public async Task ListenNetworkAsync_WithLogin_AcceptsConnectionViaMachineAddress()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN dev WITH PASSWORD = 'S3cure!Pass'");
        await using var listener = await simulation.ListenNetworkAsync(0, TestContext.CancellationToken);

        var address = FindNonLoopbackIPv4();
        if (address is null)
            Assert.Inconclusive("No non-loopback IPv4 interface on this machine.");

        await using var connection = new SqlConnection(
            $"Server={address},{listener.Port};User ID=dev;Password=S3cure!Pass;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A real (non-loopback) IPv4 address of this machine, proving the
    /// all-interfaces bind is reachable beyond 127.0.0.1; null when the
    /// machine has no such interface.
    /// </summary>
    private static IPAddress? FindNonLoopbackIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    return unicast.Address;
            }
        }

        return null;
    }

    // SqlConnection.ServerVersion is parsed from the LOGINACK token's
    // ProgVersion (major.minor.build). The simulator reports the SQL Server
    // 2025 reference build 17.0.4065.4, so SqlClient reads "17.00.4065" — a
    // real build number is what lets SSMS's per-build feature gates proceed.
    [TestMethod]
    public async Task LoginAck_ReportsReferenceBuild()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(ConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        AreEqual("17.00.4065", connection.ServerVersion);
    }
}
