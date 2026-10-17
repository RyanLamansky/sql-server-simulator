using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The language a TDS session starts in: the one the client's login names
/// (<c>Current Language</c>), else the login's default, a pooled reset
/// starting it there again (probed 2026-10-17 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class LoginLanguageWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string Connect(SimulatedNetworkListener listener, string extra) =>
        $"Server=127.0.0.1,{listener.Port};User ID=fr;Password=Pa55word!;TrustServerCertificate=True;Connect Timeout=15{extra}";

    private static Simulation WithFrenchLogin()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create login fr with password = 'Pa55word!', default_language = [Français]; create user fr for login fr");
        return simulation;
    }

    private async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(this.TestContext.CancellationToken);
    }

    [TestMethod]
    [DataRow(";Pooling=False", "Français|1")]
    [DataRow(";Pooling=False;Current Language=Deutsch", "Deutsch|1")]
    [DataRow(";Pooling=False;Current Language=us_english", "us_english|7")]
    public async Task Session_StartsInTheLoginsOrTheClientsLanguage(string extra, string expected)
    {
        await using var listener = await WithFrenchLogin().ListenLocalAsync(0, this.TestContext.CancellationToken);
        await using var connection = new SqlConnection(Connect(listener, extra));
        await connection.OpenAsync(this.TestContext.CancellationToken);
        AreEqual(expected, await this.ScalarAsync(connection, "select concat(@@language, '|', @@datefirst)"));
    }

    [TestMethod]
    public async Task PooledReset_StartsInTheLoginsLanguageAgain()
    {
        await using var listener = await WithFrenchLogin().ListenLocalAsync(0, this.TestContext.CancellationToken);
        var connectionString = Connect(listener, ";Max Pool Size=1");
        await using (var first = new SqlConnection(connectionString))
        {
            await first.OpenAsync(this.TestContext.CancellationToken);
            _ = await this.ScalarAsync(first, "set language us_english; select 1");
        }
        await using var second = new SqlConnection(connectionString);
        await second.OpenAsync(this.TestContext.CancellationToken);
        AreEqual("Français", await this.ScalarAsync(second, "select @@language"));
        SqlConnection.ClearPool(second);
    }

    [TestMethod]
    public async Task UnknownClientLanguage_RefusesTheLogin()
    {
        await using var listener = await WithFrenchLogin().ListenLocalAsync(0, this.TestContext.CancellationToken);
        await using var connection = new SqlConnection(Connect(listener, ";Pooling=False;Current Language=Klingon"));
        var refusal = await ThrowsAsync<SqlException>(() => connection.OpenAsync(this.TestContext.CancellationToken));
        AreEqual("4019,18456,5701", string.Join(",", refusal.Errors.Cast<SqlError>().Select(static error => error.Number)));
    }
}
