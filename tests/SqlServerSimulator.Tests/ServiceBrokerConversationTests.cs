using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Service Broker conversations — BEGIN DIALOG, SEND, RECEIVE, END
/// CONVERSATION and their kin — as SQL Server 2025 runs them within one
/// instance (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class ServiceBrokerConversationTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Setup = """
        create queue iq; create queue tq;
        create service init on queue iq; create service targ on queue tq ([DEFAULT]);
        """;

    private static Simulation Broker()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        return simulation;
    }

    private static string Rows(DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        var rows = new List<string>();
        do
        {
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i) is byte[] bytes ? "0x" + Convert.ToHexString(bytes) : reader.GetValue(i).ToInvariantString())));
        }
        while (reader.NextResult());
        return string.Join(";", rows);
    }

    private static string Rows(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        return Rows(connection, sql);
    }

    [TestMethod]
    public void Dialog_DeliversAndEnds()
    {
        var simulation = Broker();
        using var connection = simulation.CreateOpenConnection();
        AreEqual("1|SO|0", Rows(connection, """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            select count(*), min(state), max(send_sequence) from sys.conversation_endpoints
            """));
        AreEqual("True|CO|1|0|targ|-1;False|CO|0|1|init|1", Rows(connection, """
            declare @h uniqueidentifier = (select conversation_handle from sys.conversation_endpoints);
            send on conversation @h (N'hello');
            select is_initiator, state, send_sequence, receive_sequence, far_service, far_principal_id from sys.conversation_endpoints order by is_initiator desc
            """));
        AreEqual("1|5|0|0|targ|DEFAULT|DEFAULT|14|N|hello", Rows(connection,
            "select status, priority, queuing_order, message_sequence_number, service_name, service_contract_name, message_type_name, message_type_id, validation, cast(message_body as nvarchar(max)) from tq"));

        // The initiator's END reaches the target as an EndDialog, and goes.
        AreEqual("hello;http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog|1;False|DI", Rows(connection, """
            declare @h uniqueidentifier = (select conversation_handle from sys.conversation_endpoints where is_initiator = 1);
            receive cast(message_body as nvarchar(max)) from tq;
            end conversation @h;
            receive message_type_name, message_sequence_number from tq;
            select is_initiator, state from sys.conversation_endpoints
            """));
        // The target's END leaves its endpoint CLOSED.
        AreEqual("False|CD", Rows(connection, """
            declare @h uniqueidentifier = (select conversation_handle from sys.conversation_endpoints);
            end conversation @h;
            select is_initiator, state from sys.conversation_endpoints
            """));
        _ = simulation.AssertSqlError("declare @h uniqueidentifier = (select conversation_handle from sys.conversation_endpoints); end conversation @h", 8426);
    }

    [TestMethod]
    public void SendAndReceive_AreTransactional()
    {
        var simulation = Broker();
        using var connection = simulation.CreateOpenConnection();
        AreEqual("1;0;0", Rows(connection, """
            declare @h uniqueidentifier;
            begin tran;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'x');
            select count(*) from tq;
            rollback;
            select count(*) from tq;
            select count(*) from sys.conversation_endpoints
            """));
        AreEqual("x;1;0", Rows(connection, """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'x');
            begin tran;
            receive cast(message_body as nvarchar(max)) from tq;
            rollback;
            select count(*) from tq;
            receive top (1) @h = conversation_handle from tq;
            select count(*) from tq
            """));
    }

    [TestMethod]
    public void FiveRolledBackReceives_DisableAPoisonHandlingQueue()
    {
        var simulation = Broker();
        _ = simulation.ExecuteNonQuery("""
            declare @h uniqueidentifier, @i int = 0;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'poison');
            while @i < 5
            begin
                begin tran;
                receive top (1) message_body from tq;
                rollback;
                set @i += 1;
            end
            """);
        // The fifth rollback disables the queue as it rolls back (probed 2026-10-16 against SQL Server 2025).
        IsFalse((bool)simulation.ExecuteScalar("select is_receive_enabled from sys.service_queues where name = 'tq'")!);
        _ = simulation.AssertSqlError("receive * from tq", 9617);
        AreEqual("False|False", Rows(simulation, "select is_receive_enabled, is_enqueue_enabled from sys.service_queues where name = 'tq'"));

        // A disabled queue refuses the next message, which waits on the
        // sender's transmission queue until the queue is turned back on.
        AreEqual("One or more messages could not be delivered to the local service targeted by this dialog.", simulation.ExecuteScalar("""
            declare @h uniqueidentifier = (select conversation_handle from sys.conversation_endpoints where is_initiator = 1);
            send on conversation @h (N'later');
            select transmission_status from sys.transmission_queue
            """));
        AreEqual(2, simulation.ExecuteScalar("alter queue tq with status = on; select count(*) from tq"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.transmission_queue"));
    }

    [TestMethod]
    [DataRow("declare @h uniqueidentifier = newid(); send on conversation @h (N'x')", 8426)]
    [DataRow("declare @h uniqueidentifier; send on conversation @h (N'x')", 8418)]
    [DataRow("declare @h int = 1; send on conversation @h (N'x')", 206)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service nope to service 'targ'", 208)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ' on contract nope", 208)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ' with lifetime = 0", 8411)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service null", 156)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ'; send on conversation @h message type nope", 208)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ'; send on conversation @h (null)", 8433)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ'; send on conversation @h message type mt", 8431)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ' on contract ct; send on conversation @h message type mt", 8432)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ'; end conversation @h; send on conversation @h", 8426)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ'; end conversation @h with error = 0 description = 'x'", 8165)]
    [DataRow("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ'; begin conversation timer (@h) timeout = 0", 8165)]
    [DataRow("declare @h uniqueidentifier, @g uniqueidentifier = newid(); begin dialog @h from service init to service 'targ'; move conversation @h to @g", 8439)]
    [DataRow("declare @x int; receive @x = message_type_id, message_body from tq", 8447)]
    [DataRow("receive top (-1) * from tq", 127)]
    [DataRow("receive * from nope", 208)]
    [DataRow("receive * from tq where message_type_name = 'x'", 102)]
    [DataRow("receive c = message_body from tq", 102)]
    public void Statements_RefuseAsRealDoes(string sql, int number)
    {
        var simulation = Broker();
        _ = simulation.ExecuteNonQuery("create message type mt; create contract ct (mt sent by target)");
        _ = simulation.AssertSqlError(sql, number);
    }

    [TestMethod]
    public void UndeliverableMessages_WaitOnTheTransmissionQueue()
    {
        var simulation = Broker();
        AreEqual("CO|nope|The target service name could not be found. Ensure that the service name is specified correctly and/or the routing information has been supplied.", Rows(simulation, """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'nope' with encryption = off;
            send on conversation @h (N'x');
            select e.state, t.to_service_name, t.transmission_status from sys.conversation_endpoints e join sys.transmission_queue t on t.conversation_handle = e.conversation_handle
            """));
        // Encryption is on unless turned off, and a local dialog's needs the
        // database master key.
        AreEqual("The session keys for this conversation could not be created or accessed. The database master key is required for this operation.", simulation.ExecuteScalar("""
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ';
            send on conversation @h (N'x');
            select transmission_status from sys.transmission_queue where conversation_handle = @h
            """));
        AreEqual("No route matches the destination service name for this conversation. Create a route to the destination service name for messages in this conversation to be delivered.", simulation.ExecuteScalar("""
            drop route AutoCreatedLocal;
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'x');
            select transmission_status from sys.transmission_queue where conversation_handle = @h
            """));
    }

    [TestMethod]
    public void DisabledBroker_HoldsMessagesUntilEnabled()
    {
        var simulation = Broker();
        AreEqual("The broker is disabled in the sender's database.|0", Rows(simulation, """
            alter database current set disable_broker;
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'x');
            select transmission_status, (select count(*) from tq) from sys.transmission_queue
            """));
        AreEqual(1, simulation.ExecuteScalar("alter database current set enable_broker; select count(*) from tq"));
    }

    [TestMethod]
    public void FailedValidation_ErrorsTheDialog()
    {
        var simulation = Broker();
        _ = simulation.ExecuteNonQuery("""
            create message type mx validation = well_formed_xml;
            create message type me validation = empty;
            create contract cx (mx sent by any, me sent by any);
            create contract c2 ([DEFAULT] sent by any);
            alter service targ (add contract cx);
            """);
        var error = simulation.ExecuteScalar("""
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' on contract cx with encryption = off;
            send on conversation @h message type mx (N'<a>broken');
            select concat(e.state, '|', t.message_sequence_number, '|', cast(q.message_body as nvarchar(max)))
            from sys.conversation_endpoints e join sys.transmission_queue t on t.conversation_handle = e.conversation_handle join iq q on q.conversation_handle = e.conversation_handle
            """)!.ToInvariantString();
        StartsWith("ER|0|﻿<?xml version=\"1.0\"?><Error xmlns=\"http://schemas.microsoft.com/SQL/ServiceBroker/Error\"><Code>-9615</Code><Description>A message of type &apos;mx&apos; failed XML validation on the target service.  XML parsing: line 1, character 9, unexpected end of input This occurred in the message with Conversation ID &apos;", error);
        Contains("Initiator: 1, and Message sequence number: 0.</Description></Error>", error);

        AreEqual("ER|<Code>-8408</Code><Description>Target service &apos;targ&apos; does not support contract &apos;c2&apos;.</Description>", Rows(simulation, """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' on contract c2 with encryption = off;
            send on conversation @h (N'x');
            select e.state, substring(cast(q.message_body as nvarchar(max)), 91, 200) from sys.conversation_endpoints e join iq q on q.conversation_handle = e.conversation_handle where e.conversation_handle = @h
            """).Replace("</Error>", ""));

        // A later message failing reaches the target as -8499 and the sender as -8462.
        AreEqual("ER|-8462;ER|-8499", Rows(simulation, """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' on contract cx with encryption = off;
            send on conversation @h message type mx (N'<a/>');
            send on conversation @h message type me (0x01);
            select e.state, cast(cast(q.message_body as nvarchar(max)) as xml).value('(/*:Error/*:Code)[1]', 'int')
            from sys.conversation_endpoints e
            join (select conversation_handle, message_body from iq union all select conversation_handle, message_body from tq) q on q.conversation_handle = e.conversation_handle
            where e.conversation_id = (select conversation_id from sys.conversation_endpoints where conversation_handle = @h)
                and q.message_body is not null and cast(q.message_body as nvarchar(max)) like '%<Error%'
            order by e.is_initiator desc
            """));
    }

    [TestMethod]
    public void EndConversation_WithErrorAndCleanup()
    {
        var simulation = Broker();
        using var connection = simulation.CreateOpenConnection();
        AreEqual("True|ER;False|CD;http://schemas.microsoft.com/SQL/ServiceBroker/Error|-1|<Code>50001</Code><Description>bad &apos;thing&apos;</Description>", Rows(connection, """
            declare @h uniqueidentifier, @t uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'a');
            receive top (1) @t = conversation_handle from tq;
            end conversation @t with error = 50001 description = N'bad ''thing''';
            select is_initiator, state from sys.conversation_endpoints order by is_initiator desc;
            select message_type_name, message_sequence_number, replace(substring(cast(message_body as nvarchar(max)), 91, 200), '</Error>', '') from iq
            """));
        AreEqual(0, simulation.ExecuteScalar("""
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (N'a');
            end conversation @h with cleanup;
            select count(*) from sys.conversation_endpoints where conversation_handle = @h
            """));
    }

    [TestMethod]
    public void Groups_RelateMoveAndLock()
    {
        var simulation = Broker();
        using var connection = simulation.CreateOpenConnection();
        AreEqual("1;same;2|2;got;1|1;0", Rows(connection, """
            declare @h1 uniqueidentifier, @h2 uniqueidentifier, @x uniqueidentifier, @g uniqueidentifier = newid();
            begin dialog @h1 from service init to service 'targ' with encryption = off;
            begin dialog @h2 from service init to service 'targ' with related_conversation = @h1, encryption = off;
            select count(distinct conversation_group_id) from sys.conversation_endpoints where conversation_handle in (@h1, @h2);
            begin dialog @x from service init to service 'targ' with related_conversation_group = @g, encryption = off;
            select case when conversation_group_id = @g then 'same' end from sys.conversation_endpoints where conversation_handle = @x;
            send on conversation (@h1, @h2) (N'multi');
            select count(*), count(distinct conversation_group_id) from tq;
            declare @tg uniqueidentifier;
            get conversation group @tg from tq;
            select case when @tg is not null then 'got' end;
            declare @rt table (h uniqueidentifier, b varbinary(max), qo bigint);
            receive conversation_handle, message_body, queuing_order from tq into @rt where conversation_group_id = @tg;
            select @@rowcount, count(*) from @rt;
            receive top (1) * from tq where conversation_handle = @h1;
            select @@rowcount
            """));
        AreEqual(1, simulation.ExecuteScalar("""
            declare @h1 uniqueidentifier, @h2 uniqueidentifier, @g uniqueidentifier;
            begin dialog @h1 from service init to service 'targ' with encryption = off;
            begin dialog @h2 from service init to service 'targ' with encryption = off;
            select @g = conversation_group_id from sys.conversation_endpoints where conversation_handle = @h2;
            move conversation @h1 to @g;
            select count(distinct conversation_group_id) from sys.conversation_endpoints where conversation_handle in (@h1, @h2)
            """));
    }

    [TestMethod]
    public async Task Receive_SkipsAGroupAnotherTransactionHolds()
    {
        var simulation = Broker();
        _ = simulation.ExecuteNonQuery("""
            declare @h1 uniqueidentifier, @h2 uniqueidentifier;
            begin dialog @h1 from service init to service 'targ' with encryption = off;
            send on conversation @h1 (N'one');
            begin dialog @h2 from service init to service 'targ' with encryption = off;
            send on conversation @h2 (N'two');
            """);
        using var holder = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran").ExecuteNonQuery();
        AreEqual("one", Rows(holder, "receive top (1) cast(message_body as nvarchar(10)) from tq"));
        AreEqual("two", Rows(simulation, "receive top (1) cast(message_body as nvarchar(10)) from tq"));
        AreEqual("", Rows(simulation, "receive top (1) cast(message_body as nvarchar(10)) from tq"));
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual("one", Rows(simulation, "receive top (1) cast(message_body as nvarchar(10)) from tq"));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task WaitForReceive_WaitsForAMessage()
    {
        var simulation = Broker();
        _ = simulation.ExecuteNonQuery("declare @h uniqueidentifier; begin dialog @h from service init to service 'targ' with encryption = off");
        // A timeout returns the empty set.
        AreEqual("0", Rows(simulation, "waitfor (receive * from tq), timeout 50; select @@rowcount"));

        using var waiter = simulation.CreateOpenConnection();
        var received = Task.Run(() => Rows(waiter, "waitfor (receive cast(message_body as nvarchar(10)) from tq)"), TestContext.CancellationToken);
        await simulation.AwaitRequest(received, "wait_type = 'BROKER_RECEIVE_WAITFOR'", TestContext.CancellationToken);
        _ = simulation.ExecuteNonQuery("declare @h uniqueidentifier = (select conversation_handle from sys.conversation_endpoints); send on conversation @h (N'late')");
        AreEqual("late", await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
    }

    [TestMethod]
    public void ConversationTimer_SendsADialogTimerMessage()
    {
        var simulation = Broker();
        AreEqual("set|SO;http://schemas.microsoft.com/SQL/ServiceBroker/DialogTimer|-2|E|NULL;1900-01-01 00:00:00", Rows(simulation, """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            begin conversation timer (@h) timeout = 1;
            select case when dialog_timer > '2000-01-01' then 'set' end, state from sys.conversation_endpoints where conversation_handle = @h;
            waitfor delay '00:00:01.100';
            select message_type_name, message_sequence_number, validation, message_body from iq where conversation_handle = @h;
            select format(dialog_timer, 'yyyy-MM-dd HH:mm:ss') from sys.conversation_endpoints where conversation_handle = @h
            """));
    }

    [TestMethod]
    public void Bodies_ConvertToVarbinary()
        => AreEqual("0x0000002A;0xFFFE3C0061002F003E00;NULL;NULL;0x6173636969", Rows(Broker(), """
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'targ' with encryption = off;
            send on conversation @h (42);
            send on conversation @h (cast(N'<a/>' as xml));
            send on conversation @h (N'');
            send on conversation @h;
            send on conversation @h ('ascii');
            select message_body from tq order by queuing_order
            """));

    [TestMethod]
    public void Retention_KeepsSentAndReceivedMessages()
    {
        var simulation = new Simulation();
        AreEqual("3|0|0;3|1|1;0|0|2;3|0|0;3|1|1;0", Rows(simulation, """
            create queue rq with retention = on; create queue rq2;
            create service rs on queue rq ([DEFAULT]); create service rs2 on queue rq2 ([DEFAULT]);
            declare @h uniqueidentifier, @t uniqueidentifier;
            begin dialog @h from service rs to service 'rs2' with encryption = off;
            send on conversation @h (N'r1');
            send on conversation @h (N'r2');
            select status, message_sequence_number, queuing_order from rq;
            receive top (1) @t = conversation_handle from rq2;
            send on conversation @t (N'reply');
            receive top (1) @t = conversation_handle from rq;
            select status, message_sequence_number, queuing_order from rq;
            end conversation @h;
            select count(*) from rq
            """));
    }

    [TestMethod]
    public void EndConversation_InsideABlock_StaysInTheBlock()
        => AreEqual(1, Broker().ExecuteScalar("""
            declare @h uniqueidentifier;
            if 1 = 1
            begin
                begin dialog conversation @h from service init to service 'targ' with encryption = off;
                end conversation @h;
            end
            select 1
            """));

    [TestMethod]
    public void DropService_ErrorsTheFarSide()
        => AreEqual("ER|-8470", Rows(Broker(), """
            create queue dq; create service ds on queue dq ([DEFAULT]);
            declare @h uniqueidentifier;
            begin dialog @h from service init to service 'ds' with encryption = off;
            send on conversation @h (N'x');
            drop service ds;
            select e.state, cast(cast(q.message_body as nvarchar(max)) as xml).value('(/*:Error/*:Code)[1]', 'int')
            from sys.conversation_endpoints e join iq q on q.conversation_handle = e.conversation_handle where e.conversation_handle = @h
            """));
    [TestMethod]
    public void AnotherDatabasesService_TakesMessagesOnlyFromATrustworthyDatabase()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database b; create database c",
            "use b; create queue bq; create service bs on queue bq ([DEFAULT])",
            "use c; create queue cq; create service cs on queue cq",
            "use c; declare @h uniqueidentifier; begin dialog @h from service cs to service 'bs' with encryption = off; send on conversation @h (N'x')");
        AreEqual("An exception occurred while enqueueing a message in the target queue. Error: 916, State: 3. The server principal \"sa\" is not able to access the database \"b\" under the current security context.|0",
            Rows(simulation, "use c; select transmission_status, (select count(*) from b.dbo.bq) from sys.transmission_queue"));
        _ = simulation.ExecuteNonQuery("alter database c set trustworthy on");
        AreEqual("0|1", Rows(simulation, "use c; select (select count(*) from sys.transmission_queue), (select count(*) from b.dbo.bq)"));
    }

    /// <summary>A dialog's initiator joins a conversation group of its own, which <c>sys.conversation_groups</c> lists.</summary>
    [TestMethod]
    public void BeginDialog_ListsItsConversationGroup()
        => AreEqual("0|1|1", Rows(Broker(), """
            select count(*) from sys.conversation_groups;
            declare @d uniqueidentifier; begin dialog @d from service init to service 'targ' with encryption = off;
            select count(*) from sys.conversation_groups;
            select count(*) from sys.conversation_groups g join sys.services s on s.service_id = g.service_id where s.name = 'init' and g.is_system = 0
            """).Replace(";", "|", StringComparison.Ordinal));
}
