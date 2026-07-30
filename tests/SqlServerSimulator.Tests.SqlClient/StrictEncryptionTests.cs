using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// TDS 8.0 (<c>Encrypt=Strict</c>): the client opens with a bare TLS
/// handshake negotiating ALPN <c>tds/8.0</c> and every TDS packet — prelogin
/// included — flows inside the TLS channel, versus TDS 7.x's cleartext
/// prelogin followed by a prelogin-wrapped handshake. SqlClient ignores
/// <c>TrustServerCertificate</c> in strict mode and always validates the
/// server certificate, so these tests pin instead: the public part of the
/// certificate the endpoint presents is exported once to a fixed-name file in
/// the OS temp directory (so runs overwrite one file rather than accumulating
/// per-run copies) and named in the connection string's
/// <c>ServerCertificate</c> keyword. One file serves every listener because
/// listeners share one certificate per process.
/// </summary>
[TestClass]
public sealed class StrictEncryptionTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string pinPath = null!;

    [ClassInitialize]
    public static void ExportCertificateToPin(TestContext _) =>
        pinPath = Pin(TdsServerCertificate.Shared, "endpoint");

    /// <summary>
    /// Writes a certificate's public part where a strict connection string can
    /// pin it, returning the path. The private key never goes to disk.
    /// </summary>
    private static string Pin(X509Certificate2 certificate, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SqlServerSimulator.Tests.SqlClient.{name}.cer");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }

    private static string StrictConnectionString(SimulatedNetworkListener listener, string extra = "")
        => PinnedConnectionString(listener, pinPath, extra);

    /// <summary>A strict connection string pinning a specific exported certificate rather than the endpoint default.</summary>
    private static string PinnedConnectionString(SimulatedNetworkListener listener, string pin, string extra = "")
        => $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;Encrypt=Strict;ServerCertificate={pin};Pooling=False;Connect Timeout=15{extra}";

    [TestMethod]
    public async Task Strict_SelectOne_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(StrictConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // RPC with parameters exercises the full post-login pipeline inside the
    // strict channel.
    [TestMethod]
    public async Task Strict_ParameterizedQuery_RoundTrips()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int, name nvarchar(50)); insert t values (1, N'alpha'), (2, N'beta')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(StrictConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select name from t where id = @id", connection);
        _ = command.Parameters.AddWithValue("@id", 2);
        AreEqual("beta", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // MARS rides SMP frames over the post-login stream; strict changes which
    // stream that is, so the handoff needs its own proof.
    [TestMethod]
    public async Task Strict_Mars_OverlappingReaders()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int); insert t values (1), (2), (3)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(StrictConnectionString(listener, ";MultipleActiveResultSets=True"));
        await connection.OpenAsync(TestContext.CancellationToken);

        await using var outer = new SqlCommand("select id from t order by id", connection);
        await using var reader = await outer.ExecuteReaderAsync(TestContext.CancellationToken);
        var total = 0;
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            await using var inner = new SqlCommand("select @@spid", connection);
            _ = await inner.ExecuteScalarAsync(TestContext.CancellationToken);
            total += reader.GetInt32(0);
        }

        AreEqual(6, total);
    }

    [TestMethod]
    public async Task Strict_LoginFailure_Raises18456()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login apps with password = 'S3cure!Pass'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(StrictConnectionString(listener));
        var ex = await ThrowsExactlyAsync<SqlException>(() => connection.OpenAsync(TestContext.CancellationToken));
        AreEqual(18456, ex.Number);
        Assert.Contains("Login failed for user 'sa'", ex.Message);
    }

    // SqlBulkCopy's BulkLoadBCP packet is the one non-token client payload;
    // prove it flows through the strict channel too.
    [TestMethod]
    public async Task Strict_BulkCopy_RoundTrips()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table bulked (id int, name nvarchar(50))");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(StrictConnectionString(listener));
        await connection.OpenAsync(TestContext.CancellationToken);

        var source = new System.Data.DataTable();
        _ = source.Columns.Add("id", typeof(int));
        _ = source.Columns.Add("name", typeof(string));
        _ = source.Rows.Add(1, "alpha");
        _ = source.Rows.Add(2, "beta");
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "bulked" })
            await bulk.WriteToServerAsync(source, TestContext.CancellationToken);

        await using var command = new SqlCommand("select count(*) from bulked", connection);
        AreEqual(2, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // The ownership contract: a supplied certificate belongs to the caller,
    // so disposing the listener leaves it usable for the next one. Supplying
    // a freshly generated certificate rather than the process-wide default is
    // what makes the assertion meaningful — the default is never disposed
    // whatever the listener does.
    [TestMethod]
    public async Task SuppliedCertificate_SurvivesListenerDispose()
    {
        var simulation = new Simulation();
        using var supplied = TdsServerCertificate.Create();
        var options = new SimulatedNetworkListenerOptions { Port = 0, ServerCertificate = supplied };
        var first = await simulation.ListenLocalAsync(options, TestContext.CancellationToken);
        await first.DisposeAsync();

        IsTrue(supplied.HasPrivateKey);
        await using var second = await simulation.ListenLocalAsync(options, TestContext.CancellationToken);
        await using var connection = new SqlConnection(PinnedConnectionString(second, Pin(supplied, "supplied")));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 2", connection);
        AreEqual(2, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SuppliedCertificate_WithoutPrivateKey_Rejected()
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(TdsServerCertificate.Shared.Export(X509ContentType.Cert));
        var ex = await ThrowsExactlyAsync<ArgumentException>(() => new Simulation().ListenLocalAsync(
            new SimulatedNetworkListenerOptions { Port = 0, ServerCertificate = publicOnly },
            TestContext.CancellationToken));
        Assert.Contains("private key", ex.Message);
    }
}
