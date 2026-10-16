using Microsoft.Data.Sql;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Query notifications over the wire, the server side of <c>SqlDependency</c>:
/// a query sent with a notification request subscribes, and a change to its
/// table sends the QueryNotification message, as SQL Server 2025 does
/// (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class QueryNotificationWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static object? Scalar(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    public async Task SqlDependency_FiresOnAChange()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table dbo.t (id int primary key, v nvarchar(20)); insert dbo.t values (1, N'a')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = Wire.ConnectionString(listener);
        IsTrue(SqlDependency.Start(connectionString));
        try
        {
            var fired = new TaskCompletionSource<SqlNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken))
            {
                await using var command = new SqlCommand("select id, v from dbo.t", connection);
                var dependency = new SqlDependency(command);
                dependency.OnChange += (_, e) => fired.TrySetResult(e);
                await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
                {
                    while (await reader.ReadAsync(TestContext.CancellationToken))
                    {
                    }
                }
                AreEqual(1, Scalar(simulation, "select count(*) from sys.dm_qn_subscriptions"));
            }
            Wire.ExecInProc(simulation, "update dbo.t set v = N'b' where id = 1");
            var change = await fired.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
            AreEqual(SqlNotificationType.Change, change.Type);
            AreEqual(SqlNotificationInfo.Update, change.Info);
            AreEqual(SqlNotificationSource.Data, change.Source);
            AreEqual(0, Scalar(simulation, "select count(*) from sys.dm_qn_subscriptions"));
        }
        finally
        {
            _ = SqlDependency.Stop(connectionString);
        }
    }

    [TestMethod]
    public async Task NotificationRequest_SendsRealsMessages()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table dbo.t (id int primary key, v nvarchar(20));
            create queue q; create service s on queue q ([http://schemas.microsoft.com/SQL/Notifications/PostQueryNotification]);
            """);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        foreach (var query in new[] { "select id, v from dbo.t", "select * from dbo.t" })
        {
            await using var command = new SqlCommand(query, connection) { Notification = new SqlNotificationRequest("note", "service=s;local database=simulated", 60) };
            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            while (await reader.ReadAsync(TestContext.CancellationToken))
            {
            }
        }
        Wire.ExecInProc(simulation, "insert dbo.t values (2, N'x')");
        AreEqual(
            "subscribe|statement|invalid|0|note;change|data|insert|1|note",
            Scalar(simulation, """
                select string_agg(concat(b.value('(/*:QueryNotification/@type)[1]', 'sysname'), '|', b.value('(/*:QueryNotification/@source)[1]', 'sysname'), '|',
                    b.value('(/*:QueryNotification/@info)[1]', 'sysname'), '|', b.value('(/*:QueryNotification/@id)[1]', 'int'), '|', b.value('(/*:QueryNotification/*:Message)[1]', 'sysname')), ';')
                    within group (order by queuing_order)
                from (select queuing_order, cast(cast(message_body as nvarchar(max)) as xml) b from q) m
                """));
    }
}
