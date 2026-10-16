using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Event notifications — <c>CREATE / DROP EVENT NOTIFICATION</c>, their
/// catalog, and the EventNotification messages DDL, queue and trace events
/// send — as SQL Server 2025 runs them (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class ServiceBrokerEventNotificationTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Target = """
        create queue enq;
        create service ens on queue enq ([http://schemas.microsoft.com/SQL/Notifications/PostEventNotification]);
        create queue q;
        create service s on queue q ([DEFAULT]);
        """;

    private static Simulation WithTarget()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Target);
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

    /// <summary>The event types of the notifications on <c>enq</c>, in order, or the message types where no body is one.</summary>
    private const string Received = """
        select string_agg(isnull(cast(message_body as xml).value('(/EVENT_INSTANCE/EventType)[1]', 'nvarchar(128)'), message_type_name), ',') within group (order by queuing_order) from enq
        """;

    [TestMethod]
    public void DdlEvents_ReachTheService_OnCommitOnly()
    {
        var simulation = WithTarget();
        simulation.ExecuteBatches(
            "create event notification n on database for create_table, ddl_view_events to service 'ens', 'current database'",
            "create table t1 (a int)",
            "create view v1 as select a from t1",
            "begin tran; create table t2 (a int); rollback",
            "alter table t1 add b int");
        AreEqual("CREATE_TABLE,CREATE_VIEW", simulation.ExecuteScalar(Received));
        AreEqual("http://schemas.microsoft.com/SQL/Notifications/EventNotification|0|ens|http://schemas.microsoft.com/SQL/Notifications/PostEventNotification|X;"
            + "http://schemas.microsoft.com/SQL/Notifications/EventNotification|1|ens|http://schemas.microsoft.com/SQL/Notifications/PostEventNotification|X",
            Rows(simulation, "select message_type_name, message_sequence_number, service_name, service_contract_name, validation from enq order by queuing_order"));
        AreEqual("<EVENT_INSTANCE><EventType>CREATE_TABLE</EventType>", ((string)simulation.ExecuteScalar("select top (1) cast(message_body as nvarchar(max)) from enq order by queuing_order")!)[1..52]);
        AreEqual("dbo|t1|TABLE|create table t1 (a int)", Rows(simulation, """
            select top (1) x.value('(/EVENT_INSTANCE/SchemaName)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'),
                x.value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'nvarchar(max)')
            from (select cast(message_body as xml) x, queuing_order from enq) m order by queuing_order
            """));
        // The dialog: the notification's endpoint and the target's, both conversing.
        AreEqual("True|CO|ens;False|CO|http://schemas.microsoft.com/SQL/Notifications/EventNotificationService",
            Rows(simulation, "select is_initiator, state, far_service from sys.conversation_endpoints where far_service <> 's' order by is_initiator desc"));
    }

    [TestMethod]
    public void Catalog_ListsTheNotificationsAndTheirEvents()
    {
        var simulation = WithTarget();
        _ = simulation.ExecuteNonQuery("""
            create event notification n1 on database for create_table, ddl_view_events to service 'ens', 'current database';
            create event notification n2 on queue q for queue_activation to service 'ens', 'current database';
            create event notification n3 on database with fan_in for alter_table to service 'ens', 'current database';
            """);
        AreEqual("n1|0|DATABASE|0|ens|NULL|1;n2|1|OBJECT_OR_COLUMN|1|ens|NULL|1;n3|0|DATABASE|0|ens|0x01|1", Rows(simulation, """
            select name, parent_class, parent_class_desc, case when parent_id = object_id('q') then 1 else parent_id end, service_name, creator_sid, principal_id
            from sys.event_notifications order by object_id
            """));
        IsTrue((bool)simulation.ExecuteScalar("select cast(case when min(broker_instance) = max(broker_instance) and min(broker_instance) = (select cast(service_broker_guid as nvarchar(128)) from sys.databases where name = db_name()) then 1 else 0 end as bit) from sys.event_notifications")!);
        AreEqual("n1|21|CREATE_TABLE|False|NULL;n1|41|CREATE_VIEW|False|DDL_VIEW_EVENTS;n1|42|ALTER_VIEW|False|DDL_VIEW_EVENTS;n1|43|DROP_VIEW|False|DDL_VIEW_EVENTS;n2|173|QUEUE_ACTIVATION|False|NULL;n3|22|ALTER_TABLE|False|NULL",
            Rows(simulation, "select n.name, e.type, e.type_desc, e.is_trigger_event, e.event_group_type_desc from sys.events e join sys.event_notifications n on n.object_id = e.object_id order by e.object_id, e.type"));
        AreEqual("432|BROKER_QUEUE_DISABLED|TRC_ALL_EVENTS|11008", Rows(simulation, """
            select count(*), (select type_name from sys.event_notification_event_types where type = 160),
                (select type_name from sys.event_notification_event_types where type = 11000), (select parent_type from sys.event_notification_event_types where type_name = 'AUDIT_LOGIN')
            from sys.event_notification_event_types
            """));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.objects where name like 'n_'"));
    }

    [TestMethod]
    public void Events_ListsTriggersBesideNotifications()
        => AreEqual("INSERT|True;DELETE|True;DROP_VIEW|True", Rows(WithTarget(), """
            create table t (a int);
            exec('create trigger tr on t after insert, delete as return');
            exec('create trigger dtr on database for drop_view as return');
            select type_desc, is_trigger_event from sys.events order by object_id, type
            """));

    [TestMethod]
    [DataRow("create event notification n on database for create_table to service 'ens', 'current database'", 2714, "There is already an object named 'n' in the database.")]
    [DataRow("create event notification m on database for queue_activation to service 'ens', 'current database'", 1098, "The specified event type(s) is/are not valid on the specified target object.")]
    [DataRow("create event notification m on queue q for create_table to service 'ens', 'current database'", 1098, "The specified event type(s) is/are not valid on the specified target object.")]
    [DataRow("create event notification m on database for deadlock_graph to service 'ens', 'current database'", 1098, "The specified event type(s) is/are not valid on the specified target object.")]
    [DataRow("create event notification m on database for create_login to service 'ens', 'current database'", 1098, "The specified event type(s) is/are not valid on the specified target object.")]
    [DataRow("create event notification m on server for broker_queue_disabled to service 'ens', 'current database'", 1098, "The specified event type(s) is/are not valid on the specified target object.")]
    [DataRow("create event notification m on database for nope_event to service 'ens', 'current database'", 1084, "'nope_event' is an invalid event type.")]
    [DataRow("create event notification m on database for create_table, create_table to service 'ens', 'current database'", 1095, "\"CREATE_TABLE\" has already been specified as an event type.")]
    [DataRow("create event notification m on database for ddl_database_level_events, ddl_table_events to service 'ens', 'current database'", 1095, "\"CREATE_TABLE\" has already been specified as an event type.")]
    [DataRow("create event notification m on queue nosuchq for queue_activation to service 'ens', 'current database'", 1088, "Cannot find the object \"nosuchq\" because it does not exist or you do not have permissions.")]
    [DataRow("ALTER EVENT NOTIFICATION n ON DATABASE FOR CREATE_TABLE TO SERVICE 'ens', 'current database'", 102, "Incorrect syntax near 'NOTIFICATION'.")]
    public void Create_Refusals(string sql, int number, string message)
    {
        var simulation = WithTarget();
        _ = simulation.ExecuteNonQuery("create event notification n on database for create_table to service 'ens', 'current database'");
        simulation.AssertSqlError(sql, number, message);
    }

    [TestMethod]
    public void Names_AreUniquePerParent_AndAnyStringIsAnInstance()
        => AreEqual("n|0;n|1;t|0", Rows(WithTarget(), """
            create table t (a int);
            create event notification n on database for drop_view to service 'ens', 'current database';
            create event notification n on queue q for broker_queue_disabled to service 'ens', 'current database';
            create event notification t on database for drop_table to service 'nosuch', 'garbage';
            select name, parent_class from sys.event_notifications order by name, parent_class
            """));

    [TestMethod]
    public void CurrentDatabase_InMaster_IsNoBrokerDatabase()
        => new Simulation().AssertSqlError("use master; create event notification n on database for drop_table to service 'ens', 'current database'", 2116,
            "Cannot CREATE EVENT NOTIFICATION to database 'master' because it is not a valid broker database.");

    [TestMethod]
    public void Drop_AllOrNone_AndAMissingOneEndsTheBatch()
    {
        var simulation = WithTarget();
        _ = simulation.ExecuteNonQuery("""
            create event notification a on database for drop_view to service 'ens', 'current database';
            create event notification b on database for drop_table to service 'ens', 'current database';
            """);
        var ex = simulation.AssertSqlError("drop event notification a, nosuch on database; select 1", 15151);
        AreEqual("Cannot drop the event notification 'nosuch', because it does not exist or you do not have permission.", ex.Errors[0].Message);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from sys.event_notifications"));
        AreEqual(0, simulation.ExecuteScalar("begin try drop event notification nosuch on database end try begin catch end catch; select count(*) from sys.event_notifications where name = 'zz'"));
        _ = simulation.AssertSqlError("drop event notification a on queue q", 15151);
        _ = simulation.ExecuteNonQuery("drop event notification a, b on database");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.event_notifications"));
    }

    [TestMethod]
    public void Drop_EndsTheDialog_WithAnEndDialog()
    {
        var simulation = WithTarget();
        simulation.ExecuteBatches(
            "create event notification n on database for create_table to service 'ens', 'current database'",
            "create table t (a int)",
            "drop event notification n on database");
        AreEqual("CREATE_TABLE,http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog", simulation.ExecuteScalar(Received));
        AreEqual("False|DI", Rows(simulation, "select is_initiator, state from sys.conversation_endpoints where far_service <> 's'"));
    }

    [TestMethod]
    public void QueueActivation_TellsOnce_UntilAReceive()
    {
        var simulation = WithTarget();
        _ = simulation.ExecuteNonQuery("create event notification n on queue q for queue_activation to service 'ens', 'current database'");
        const string Send = "declare @h uniqueidentifier; begin dialog @h from service s to service 's' with encryption = off; send on conversation @h (N'x');";
        _ = simulation.ExecuteNonQuery(Send);
        _ = simulation.ExecuteNonQuery(Send);
        AreEqual("QUEUE_ACTIVATION", simulation.ExecuteScalar(Received));
        AreEqual("17|q|QUEUE|dbo", Rows(simulation, """
            select x.value('(/EVENT_INSTANCE/SPID)[1]', 'int'), x.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'),
                x.value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/SchemaName)[1]', 'sysname')
            from (select cast(message_body as xml) x from enq) m
            """));
        _ = simulation.ExecuteNonQuery("receive top (1) * from q");
        _ = simulation.ExecuteNonQuery(Send);
        AreEqual("QUEUE_ACTIVATION,QUEUE_ACTIVATION", simulation.ExecuteScalar(Received));
    }

    [TestMethod]
    public void QueueActivation_OnAQueueAlreadyHoldingMessages_TellsAtOnce()
        => AreEqual("QUEUE_ACTIVATION", WithTarget().ExecuteScalar("""
            declare @h uniqueidentifier; begin dialog @h from service s to service 's' with encryption = off; send on conversation @h (N'x');
            create event notification n on queue q for queue_activation to service 'ens', 'current database';
            """ + Received));

    [TestMethod]
    public void BrokerQueueDisabled_TellsAsTheFifthRollbackDisablesTheQueue()
    {
        var simulation = WithTarget();
        _ = simulation.ExecuteNonQuery("""
            create event notification n on queue q for broker_queue_disabled to service 'ens', 'current database';
            declare @h uniqueidentifier; begin dialog @h from service s to service 's' with encryption = off; send on conversation @h (N'x');
            """);
        _ = simulation.ExecuteNonQuery("declare @i int = 0; while @i < 5 begin begin tran; receive top (1) * from q; rollback; set @i += 1; end");
        AreEqual("False|BROKER_QUEUE_DISABLED", Rows(simulation, "select is_receive_enabled, (" + Received + ") from sys.service_queues where name = 'q'"));
        _ = simulation.AssertSqlError("receive top (1) * from q", 9617);
    }

    [TestMethod]
    public void Permissions_DatabaseNeedsItsPermission_QueueItsAlter()
    {
        var simulation = WithTarget();
        _ = simulation.ExecuteNonQuery("create user u without login");
        simulation.AssertSqlError("execute as user = 'u'; create event notification n on database for create_table to service 'ens', 'current database'", 2104,
            "Cannot create the event notification 'n', because you do not have permission.");
        simulation.AssertSqlError("execute as user = 'u'; create event notification n on queue q for queue_activation to service 'ens', 'current database'", 2104,
            "Cannot create the event notification 'n', because you do not have permission.");
        _ = simulation.ExecuteNonQuery("revert; grant create database ddl event notification to u; grant alter on q to u");
        AreEqual("n|5;nq|5", Rows(simulation, """
            execute as user = 'u';
            create event notification n on database for create_table to service 'ens', 'current database';
            create event notification nq on queue q for queue_activation to service 'ens', 'current database';
            select name, principal_id from sys.event_notifications order by name
            """));
    }

    [TestMethod]
    public void DdlTrigger_SeesCreateAndDropEventNotification()
    {
        var simulation = WithTarget();
        simulation.ExecuteBatches(
            "create table log (x xml)",
            "create trigger d on database for ddl_event_notification_events as insert log values (eventdata())",
            "create event notification n on queue q for queue_activation to service 'ens', 'current database';",
            "drop event notification n on queue dbo.q");
        AreEqual("CREATE_EVENT_NOTIFICATION|n|EVENT NOTIFICATION|dbo|q|SERVICE QUEUE;DROP_EVENT_NOTIFICATION|n|EVENT NOTIFICATION|dbo|q|SERVICE QUEUE", Rows(simulation, """
            select x.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname'),
                x.value('(/EVENT_INSTANCE/TargetSchemaName)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/TargetObjectName)[1]', 'sysname'), x.value('(/EVENT_INSTANCE/TargetObjectType)[1]', 'sysname')
            from log
            """));
    }

    [TestMethod]
    public void Server_DdlAndLoginEvents_ReachTheService()
    {
        var simulation = WithTarget();
        using var connection = simulation.CreateOpenConnection();
        var instance = Rows(connection, "select cast(service_broker_guid as nvarchar(36)) from sys.databases where name = db_name()");
        _ = connection.CreateCommand($"create event notification srv on server for create_database, audit_login to service 'ens', '{instance}'").ExecuteNonQuery();
        AreEqual("srv|100|SERVER;srv|201|CREATE_DATABASE;srv|1014|AUDIT_LOGIN", Rows(connection, """
            select name, parent_class, parent_class_desc from sys.server_event_notifications;
            select n.name, e.type, e.type_desc from sys.server_events e join sys.server_event_notifications n on n.object_id = e.object_id order by e.type
            """));
        _ = connection.CreateCommand("create database d2").ExecuteNonQuery();
        using (simulation.CreateOpenConnection())
        {
        }
        AreEqual("CREATE_DATABASE,AUDIT_LOGIN", Rows(connection, Received));
        AreEqual("d2|1|sa", Rows(connection, """
            select top (1) cast(message_body as xml).value('(/EVENT_INSTANCE/DatabaseName)[1]', 'sysname'),
                (select count(*) from enq where cast(message_body as xml).value('(/EVENT_INSTANCE/EventSubClass)[1]', 'int') = 1),
                (select cast(message_body as xml).value('(/EVENT_INSTANCE/LoginName)[1]', 'sysname') from enq where queuing_order = 1)
            from enq order by queuing_order
            """));
        // A server notification's dialog begins in msdb.
        AreEqual("1", Rows(connection, "select count(*) from msdb.sys.conversation_endpoints where far_service = 'ens'"));
        _ = connection.CreateCommand("drop event notification srv on server").ExecuteNonQuery();
        AreEqual("0", Rows(connection, "select count(*) from sys.server_event_notifications"));
    }

    [TestMethod]
    public void Backup_CarriesTheNotificationsAndTheirDialog()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "use d; " + Target + " create event notification n on database for create_table to service 'ens', 'current database'",
            "use d; create table t1 (a int)",
            "backup database d to disk = N'd.bak'",
            "use d; drop event notification n on database",
            "use master; restore database d from disk = N'd.bak' with replace, enable_broker",
            "use d; create table t2 (a int)");
        AreEqual("n|t1,t2|0,1", Rows(simulation, """
            use d;
            select name, (select string_agg(cast(message_body as xml).value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'), ',') within group (order by queuing_order) from enq),
                (select string_agg(message_sequence_number, ',') within group (order by queuing_order) from enq)
            from sys.event_notifications
            """));
    }
}
