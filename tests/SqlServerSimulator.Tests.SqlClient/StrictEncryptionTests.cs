using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// TDS 8.0 (<c>Encrypt=Strict</c>): the client opens with a bare TLS
/// handshake negotiating ALPN <c>tds/8.0</c> and every TDS packet — prelogin
/// included — flows inside the TLS channel, versus TDS 7.x's cleartext
/// prelogin followed by a prelogin-wrapped handshake. SqlClient ignores
/// <c>TrustServerCertificate</c> in strict mode and always validates the
/// server certificate, so these tests pin the listener's
/// <see cref="SimulatedNetworkListener.ServerCertificate"/> through the
/// connection string's <c>ServerCertificate</c> keyword.
/// </summary>
[TestClass]
public sealed class StrictEncryptionTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Exports the listener's certificate beside the test results and builds
    /// a strict connection string pinning it.
    /// </summary>
    private string StrictConnectionString(SimulatedNetworkListener listener, string extra = "")
    {
        var path = Path.Combine(this.TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), $"strict-{listener.Port}.cer");
        File.WriteAllBytes(path, listener.ServerCertificate.Export(X509ContentType.Cert));
        return $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;Encrypt=Strict;ServerCertificate={path};Pooling=False;Connect Timeout=15{extra}";
    }

    [TestMethod]
    public async Task Strict_SelectOne_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = new SqlConnection(this.StrictConnectionString(listener));
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
        await using var connection = new SqlConnection(this.StrictConnectionString(listener));
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
        await using var connection = new SqlConnection(this.StrictConnectionString(listener, ";MultipleActiveResultSets=True"));
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
        await using var connection = new SqlConnection(this.StrictConnectionString(listener));
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
        await using var connection = new SqlConnection(this.StrictConnectionString(listener));
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
}
