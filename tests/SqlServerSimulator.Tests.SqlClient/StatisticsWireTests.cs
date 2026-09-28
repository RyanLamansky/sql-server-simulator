using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Where <c>SET STATISTICS IO</c> / <c>TIME</c>'s messages land against a
/// statement's DONE: after a query's, ahead of a write's, and between the two
/// DONEs a write whose <c>OUTPUT</c> returned rows then sends (probed
/// 2026-09-28 against SQL Server 2025 through SqlClient's
/// <c>StatementCompleted</c> / <c>InfoMessage</c> order).
/// </summary>
[TestClass]
public sealed class StatisticsWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private async Task<string> RunAsync(string sql)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int primary key, v int); insert t values (1, 0)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var events = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                events.Add($"Msg {error.Number}");
        };
        await using var command = new SqlCommand(sql, connection);
        command.StatementCompleted += (_, e) => events.Add($"({e.RecordCount})");
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    events.Add("row");
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }
        return string.Join(",", events);
    }

    [TestMethod]
    [DataRow("set statistics io on; select * from t", "row,(1),Msg 3615")]
    [DataRow("set statistics io on; update t set v = 1", "Msg 3615,(1)")]
    [DataRow("set statistics io on; update t set v = 1 output inserted.id", "row,(1),Msg 3615,(1)")]
    [DataRow("set statistics time on; select 1", "row,(1),Msg 3612")]
    [DataRow("set statistics time on; update t set v = 1", "Msg 3613,Msg 3612,(1)")]
    public async Task Statistics_LandAsRealSendsThem(string sql, string expected) =>
        AreEqual(expected, await RunAsync(sql));
}
