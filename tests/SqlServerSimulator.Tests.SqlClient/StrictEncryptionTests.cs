using System.Security.Cryptography;
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
/// server certificate, so these tests pin a certificate through the
/// connection string's <c>ServerCertificate</c> keyword. One certificate is
/// created for the whole class and supplied to every listener via
/// <see cref="SimulatedNetworkListenerOptions"/>; its public part is exported
/// once to a fixed-name file in the OS temp directory, so runs overwrite one
/// file instead of accumulating per-run MSTest deployment directories, and a
/// PKCS#12 sibling lets re-runs reuse the certificate instead of generating
/// a fresh RSA key.
/// </summary>
[TestClass]
public sealed class StrictEncryptionTests
{
    public TestContext TestContext { get; set; } = null!;

    private static X509Certificate2 certificate = null!;
    private static string certificatePath = null!;

    [ClassInitialize]
    public static void CreateSharedCertificate(TestContext _)
    {
        var temp = Path.GetTempPath();
        var pfxPath = Path.Combine(temp, "SqlServerSimulator.Tests.SqlClient.strict.pfx");
        certificatePath = Path.Combine(temp, "SqlServerSimulator.Tests.SqlClient.strict.cer");
        certificate = TryLoadPreviousRun(pfxPath) ?? CreateAndPersist(pfxPath);
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Cert));
    }

    /// <summary>
    /// Reuses the certificate a previous run persisted beside the pin file,
    /// skipping RSA key generation on re-runs. Anything unusable — missing,
    /// unreadable, corrupt, lacking its private key, or outside a
    /// one-day-margin validity window — falls back to creating fresh. The
    /// private key sitting in the user temp directory is acceptable for a
    /// throwaway test certificate that authenticates nothing.
    /// </summary>
    private static X509Certificate2? TryLoadPreviousRun(string pfxPath)
    {
        try
        {
            var loaded = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), password: null);
            if (loaded.HasPrivateKey
                && loaded.NotBefore <= DateTime.Now
                && loaded.NotAfter >= DateTime.Now.AddDays(1))
            {
                return loaded;
            }

            loaded.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
        }

        return null;
    }

    private static X509Certificate2 CreateAndPersist(string pfxPath)
    {
        // The raw PKCS#12 bytes are the persistence format: a certificate
        // loaded from them cannot be re-exported with its key on Windows
        // (store-loaded private keys are non-exportable there).
        var pkcs12 = TdsServerCertificate.CreatePkcs12();
        try
        {
            File.WriteAllBytes(pfxPath, pkcs12);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting is a re-run optimization only; a concurrent test
            // process or a foreign-owned file must not fail the run.
        }

        return X509CertificateLoader.LoadPkcs12(pkcs12, password: null);
    }

    [ClassCleanup]
    public static void DisposeSharedCertificate() => certificate.Dispose();

    private static Task<SimulatedNetworkListener> ListenAsync(Simulation simulation, CancellationToken cancellationToken)
        => simulation.ListenLocalAsync(new SimulatedNetworkListenerOptions { Port = 0, ServerCertificate = certificate }, cancellationToken);

    private static string StrictConnectionString(SimulatedNetworkListener listener, string extra = "")
        => $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;Encrypt=Strict;ServerCertificate={certificatePath};Pooling=False;Connect Timeout=15{extra}";

    [TestMethod]
    public async Task Strict_SelectOne_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await ListenAsync(simulation, TestContext.CancellationToken);
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
        await using var listener = await ListenAsync(simulation, TestContext.CancellationToken);
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
        await using var listener = await ListenAsync(simulation, TestContext.CancellationToken);
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
        await using var listener = await ListenAsync(simulation, TestContext.CancellationToken);
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
        await using var listener = await ListenAsync(simulation, TestContext.CancellationToken);
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

    // The ownership contract that makes certificate sharing possible: a
    // supplied certificate survives listener disposal and serves the next
    // listener, where a generated one dies with its listener.
    [TestMethod]
    public async Task SuppliedCertificate_SurvivesListenerDispose()
    {
        var simulation = new Simulation();
        var first = await ListenAsync(simulation, TestContext.CancellationToken);
        await first.DisposeAsync();

        IsTrue(certificate.HasPrivateKey);
        await using var second = await ListenAsync(simulation, TestContext.CancellationToken);
        await using var connection = new SqlConnection(StrictConnectionString(second));
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("select 2", connection);
        AreEqual(2, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SuppliedCertificate_WithoutPrivateKey_Rejected()
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var ex = await ThrowsExactlyAsync<ArgumentException>(() => new Simulation().ListenLocalAsync(
            new SimulatedNetworkListenerOptions { Port = 0, ServerCertificate = publicOnly },
            TestContext.CancellationToken));
        Assert.Contains("private key", ex.Message);
    }
}
