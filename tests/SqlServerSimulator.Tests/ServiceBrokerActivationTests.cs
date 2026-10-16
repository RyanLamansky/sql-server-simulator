using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Service Broker activation: a queue's procedure started on a background
/// session as messages arrive, as SQL Server 2025 runs it (probed
/// 2026-10-16).
/// </summary>
[TestClass]
public sealed class ServiceBrokerActivationTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Activated(string procedureBody, string activation)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table log (n int identity, body nvarchar(100), usr sysname, nest int)",
            "create queue iq; create service init on queue iq",
            $"create procedure act as {procedureBody}",
            $"create queue aq with activation ({activation}); create service asvc on queue aq ([DEFAULT])");
        return simulation;
    }

    private const string Drain = """
        begin
            declare @b varbinary(max), @h uniqueidentifier;
            waitfor (receive top (1) @h = conversation_handle, @b = message_body from aq), timeout 200;
            while @h is not null
            begin
                insert log (body, usr, nest) select cast(@b as nvarchar(100)), user_name(), @@nestlevel;
                if cast(@b as nvarchar(100)) = N'boom' raiserror('activation boom', 16, 1);
                set @h = null;
                waitfor (receive top (1) @h = conversation_handle, @b = message_body from aq), timeout 200;
            end
        end
        """;

    private static void Send(Simulation simulation, params string[] bodies)
    {
        var sends = string.Concat(bodies.Select(body => $"send on conversation @h (N'{body}');"));
        _ = simulation.ExecuteNonQuery($"""
            declare @h uniqueidentifier = (select top (1) conversation_handle from sys.conversation_endpoints where is_initiator = 1);
            if @h is null begin dialog @h from service init to service 'asvc' with encryption = off;
            {sends}
            """);
    }

    private async Task<T> Until<T>(Func<T> probe, Func<T, bool> settled) =>
        await Extensions.PollUntil(probe, settled, TestContext.CancellationToken);

    [TestMethod]
    public async Task ArrivingMessages_RunTheActivationProcedure()
    {
        var simulation = Activated(Drain, "status = on, procedure_name = act, max_queue_readers = 1, execute as owner");
        Send(simulation, "one", "two");
        AreEqual(2, await this.Until(() => (int)simulation.ExecuteScalar("select count(*) from log")!, count => count == 2));
        AreEqual("one|dbo|1;two|dbo|1", string.Join(";", Enumerable.Range(1, 2).Select(n => simulation.ExecuteScalar($"select concat(body, '|', usr, '|', nest) from log where n = {n}"))));
        AreEqual(0, await this.Until(() => (int)simulation.ExecuteScalar("select count(*) from sys.dm_broker_activated_tasks")!, count => count == 0));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from aq"));
        AreEqual("INACTIVE", simulation.ExecuteScalar("select state from sys.dm_broker_queue_monitors where queue_id = object_id('aq')"));
    }

    [TestMethod]
    public async Task RunningReader_ShowsInTheDmvs()
    {
        var simulation = Activated("""
            begin
                declare @h uniqueidentifier;
                receive top (1) @h = conversation_handle from aq;
                waitfor delay '00:00:01';
            end
            """, "procedure_name = act, max_queue_readers = 2, execute as self");
        Send(simulation, "one");
        AreEqual("[dbo].[act]|1|RECEIVES_OCCURRING", await this.Until(
            () => simulation.ExecuteScalar("""
                select concat(t.procedure_name, '|', t.execute_as, '|', m.state)
                from sys.dm_broker_activated_tasks t join sys.dm_broker_queue_monitors m on m.queue_id = t.queue_id
                """)?.ToInvariantString() ?? "",
            row => row.Length > 0));
    }

    [TestMethod]
    public async Task ActivationErrors_GoToTheErrorLog()
    {
        var simulation = Activated(Drain, "procedure_name = act, max_queue_readers = 1, execute as owner");
        Send(simulation, "boom");
        var line = await this.Until(
            () => simulation.ExecuteScalar("create table #l (d datetime, p sysname, t nvarchar(4000)); insert #l exec sp_readerrorlog 0, 1, N'activated proc'; select top (1) t from #l")?.ToInvariantString() ?? "",
            text => text.Length > 0);
        AreEqual("The activated proc '[dbo].[act]' running on queue 'simulated.dbo.aq' output the following:  'activation boom'", line);
    }

    [TestMethod]
    public async Task AReaderReceivingNothing_LeavesTheQueueNotified()
    {
        var simulation = Activated("select 1", "procedure_name = act, max_queue_readers = 1, execute as owner");
        Send(simulation, "stays");
        AreEqual("NOTIFIED", await this.Until(() => simulation.ExecuteScalar("select state from sys.dm_broker_queue_monitors where queue_id = object_id('aq')")!.ToInvariantString(), state => state == "NOTIFIED"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from aq"));
    }

    [TestMethod]
    public async Task ActivationTurnedOn_StartsAReaderForWaitingMessages()
    {
        var simulation = Activated(Drain, "status = off, procedure_name = act, max_queue_readers = 1, execute as owner");
        Send(simulation, "waiting");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from aq"));
        _ = simulation.ExecuteNonQuery("alter queue aq with activation (status = on)");
        AreEqual(1, await this.Until(() => (int)simulation.ExecuteScalar("select count(*) from log")!, count => count == 1));
    }
}
