using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Logon triggers over the wire: a TDS login and a pooled connection's reset
/// both fire them, and a refusal reaches SqlClient in real's shape (probed
/// 2026-09-28 against SQL Server 2025 with SqlClient 7.0.2).
/// </summary>
[TestClass]
public sealed class LogonTriggerWireTests
{
    public TestContext TestContext { get; set; } = null!;

    // The login registry stays empty, so any credentials connect; the trigger
    // is gated to the probe login so the in-process seeding (which runs as sa)
    // never meets it.
    private static string ProbeConnectionString(SimulatedNetworkListener listener, bool pooled) =>
        $"Server=127.0.0.1,{listener.Port};User ID=probe_x;Password=anything;TrustServerCertificate=True;Connect Timeout=15;"
        + (pooled ? "Max Pool Size=1" : "Pooling=False");

    private static Simulation WithTrigger(string body)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table dbo.logon_log (id int identity, s nvarchar(max))");
        Wire.ExecInProc(simulation, $"create trigger tr_logon on all server for logon as if original_login() = 'probe_x' begin {body} end");
        return simulation;
    }

    private static string Log(Simulation simulation) =>
        string.Join(";", Wire.ReadAllInProc(simulation, "select s from simulated.dbo.logon_log order by id").Select(row => (string)row[0]!));

    [TestMethod]
    public async Task Refusal_IsMsg17892_BesideTheLoginsNotices()
    {
        var simulation = WithTrigger("rollback;");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var connection = new SqlConnection(ProbeConnectionString(listener, pooled: false));
            await connection.OpenAsync(TestContext.CancellationToken);
        });
        AreEqual(17892, ex.Number);
        AreEqual(14, ex.Class);
        AreEqual(1, ex.State);
        AreEqual(
            "17892:14:Logon failed for login 'probe_x' due to trigger execution.|5701:0|5703:0",
            string.Join("|", ex.Errors.Cast<SqlError>().Select(error => error.Number == 17892 ? $"{error.Number}:{error.Class}:{error.Message}" : $"{error.Number}:{error.Class}")));
    }

    [TestMethod]
    public async Task Login_ReportsThePeerAddress_AndAPooledResetFiresAgain()
    {
        var simulation = WithTrigger("insert simulated.dbo.logon_log (s) select concat(@@spid, '|', eventdata().value('(/EVENT_INSTANCE/ClientHost)[1]', 'nvarchar(100)'), '|', eventdata().value('(/EVENT_INSTANCE/IsPooled)[1]', 'int'));");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = ProbeConnectionString(listener, pooled: true);
        for (var i = 0; i < 2; i++)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.CancellationToken);
            await using var command = new SqlCommand("select 1", connection);
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
        }
        SqlConnection.ClearPool(new SqlConnection(connectionString));

        var fires = Log(simulation).Split(';');
        HasCount(2, fires);
        var spid = fires[0].Split('|')[0];
        AreEqual($"{spid}|127.0.0.1|0;{spid}|127.0.0.1|1", string.Join(";", fires));
    }

    [TestMethod]
    public async Task RefusalAtAPooledReset_KillsTheSession()
    {
        var simulation = WithTrigger("insert simulated.dbo.logon_log (s) values ('fire'); if (select count(*) from simulated.dbo.logon_log) >= 2 rollback;");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = ProbeConnectionString(listener, pooled: true);
        await using (var first = new SqlConnection(connectionString))
        {
            await first.OpenAsync(TestContext.CancellationToken);
            await using var command = new SqlCommand("select 1", first);
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
        }

        await using var second = new SqlConnection(connectionString);
        await second.OpenAsync(TestContext.CancellationToken);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select 1", second);
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
        });
        SqlConnection.ClearPool(second);
        AreEqual("17892:14|596:21|0:20", string.Join("|", ex.Errors.Cast<SqlError>().Select(error => $"{error.Number}:{error.Class}")));
        AreEqual("fire", Log(simulation));
    }
}
