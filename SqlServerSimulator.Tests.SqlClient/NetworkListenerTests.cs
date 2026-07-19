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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(ConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ListenAsync_PortZero_AssignsEphemeralPort()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        IsGreaterThan(0, listener.Port);
        AreNotEqual(1433, listener.Port);
    }

    // SqlConnection.ServerVersion is parsed from the LOGINACK token's
    // ProgVersion (major.minor.build). The simulator reports the SQL Server
    // 2025 reference build 17.0.4065.4, so SqlClient reads "17.00.4065" — a
    // real build number is what lets SSMS's per-build feature gates proceed.
    [TestMethod]
    public async Task LoginAck_ReportsReferenceBuild()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(ConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        AreEqual("17.00.4065", connection.ServerVersion);
    }
}
