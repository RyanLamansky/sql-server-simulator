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

    [TestMethod]
    public async Task ListenNetworkAsync_Options_WithoutLogins_Throws()
    {
        var simulation = new Simulation();
        var ex = await ThrowsExactlyAsync<InvalidOperationException>(
            () => simulation.ListenNetworkAsync(new SimulatedNetworkListenerOptions { Port = 0 }, TestContext.CancellationToken));
        Assert.Contains("CREATE LOGIN", ex.Message);
    }

    // The options overloads mirror the port-only ones' defaults, so leaving
    // both unset binds 1433 presenting the endpoint's default certificate.
    [TestMethod]
    public void Options_Defaults_MatchPortOnlyOverloads()
    {
        var options = new SimulatedNetworkListenerOptions();
        AreEqual(1433, options.Port);
        IsNull(options.ServerCertificate);
    }

    // The options overload with a generated (null) certificate behaves like
    // the port-only overload: TrustServerCertificate connects.
    [TestMethod]
    public async Task ListenLocalAsync_Options_GeneratedCertificate_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(
            new SimulatedNetworkListenerOptions { Port = 0 }, TestContext.CancellationToken);
        await using var connection = new SqlConnection(ConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 3", connection);
        AreEqual(3, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // The default certificate is created once per process, not per listener or
    // per simulation: creating it means generating an RSA key pair, which costs
    // more than the listener around it, so a host standing up many listeners
    // would otherwise pay that price repeatedly.
    [TestMethod]
    public async Task DefaultCertificate_SharedAcrossListenersAndSimulations()
    {
        await using var first = await new Simulation().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var second = await new Simulation().ListenLocalAsync(0, TestContext.CancellationToken);
        AreEqual(first.ServerCertificate.Thumbprint, second.ServerCertificate.Thumbprint);
    }

    // Sharing only works if no listener disposes the default certificate, so
    // the next listener must still complete a handshake with it — a connection
    // is the assertion, since a disposed private key fails at TLS, not before.
    [TestMethod]
    public async Task DefaultCertificate_SurvivesListenerDispose()
    {
        var simulation = new Simulation();
        var first = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var thumbprint = first.ServerCertificate.Thumbprint;
        await first.DisposeAsync();

        await using var second = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        AreEqual(thumbprint, second.ServerCertificate.Thumbprint);
        await using var connection = new SqlConnection(ConnectionString(second));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 4", connection);
        AreEqual(4, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // BindAddress belongs to ListenNetworkAsync: honoring it on the loopback
    // method would let its accept-anyone-until-CREATE-LOGIN credential model
    // face a network interface.
    [TestMethod]
    public async Task ListenLocalAsync_Options_BindAddress_Rejected()
    {
        var ex = await ThrowsExactlyAsync<ArgumentException>(() => new Simulation().ListenLocalAsync(
            new SimulatedNetworkListenerOptions { Port = 0, BindAddress = IPAddress.Loopback },
            TestContext.CancellationToken));
        Assert.Contains("ListenNetworkAsync", ex.Message);
    }

    // Per-interface selection, provable without a firewall consent prompt:
    // a loopback bind through ListenNetworkAsync accepts via 127.0.0.1 and
    // deliberately leaves the IPv6 side unbound.
    [TestMethod]
    public async Task ListenNetworkAsync_BindAddress_BindsOnlyThatInterface()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN dev WITH PASSWORD = 'S3cure!Pass'");
        await using var listener = await simulation.ListenNetworkAsync(
            new SimulatedNetworkListenerOptions { Port = 0, BindAddress = IPAddress.Loopback },
            TestContext.CancellationToken);

        // Target master (guest-accessible) so the unmapped dev login opens as
        // guest — this test is about interface binding, not authorization.
        await using var connection = new SqlConnection(
            $"Server=127.0.0.1,{listener.Port};Database=master;User ID=dev;Password=S3cure!Pass;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));

        // An explicit bind address binds exactly that interface — no
        // best-effort other-family sibling — so IPv6 loopback refuses
        // immediately. (Only a foreign process listening on this ephemeral
        // port's IPv6 side could connect here.)
        using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        _ = await ThrowsExactlyAsync<SocketException>(async () =>
            await probe.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, listener.Port), TestContext.CancellationToken));
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
            $"Server={address},{listener.Port};Database=master;User ID=dev;Password=S3cure!Pass;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // Not run automatically: binding a machine address triggers a Windows
    // Firewall consent prompt and could trip network policy on CI runners.
    // Re-add [TestMethod] to verify the per-interface bind manually.
    // [TestMethod]
    public async Task ListenNetworkAsync_BindAddress_MachineAddress_AcceptsOnlyThatAddress()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "CREATE LOGIN dev WITH PASSWORD = 'S3cure!Pass'");
        var address = FindNonLoopbackIPv4();
        if (address is null)
            Assert.Inconclusive("No non-loopback IPv4 interface on this machine.");

        await using var listener = await simulation.ListenNetworkAsync(
            new SimulatedNetworkListenerOptions { Port = 0, BindAddress = address },
            TestContext.CancellationToken);

        await using var connection = new SqlConnection(
            $"Server={address},{listener.Port};Database=master;User ID=dev;Password=S3cure!Pass;TrustServerCertificate=True;Pooling=False;Connect Timeout=15");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));

        // Loopback was not bound: the listener serves the selected interface
        // only, unlike the all-interfaces default.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _ = await ThrowsExactlyAsync<SocketException>(async () =>
            await probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.Port), TestContext.CancellationToken));
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
