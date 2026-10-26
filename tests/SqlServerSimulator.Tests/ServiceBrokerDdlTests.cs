using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Service Broker DDL — message types, contracts, queues, services,
/// routes, remote service bindings and broker priorities — and the catalog
/// views over them, as SQL Server 2025 answers (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class ServiceBrokerDdlTests
{
    private static string Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToInvariantString())));
        return string.Join(";", rows);
    }

    /// <summary>
    /// A system queue the database's first broker read brings into being
    /// answers the object scalars by its id, though an earlier one read the
    /// database's objects before it existed (probed 2026-10-26 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void SystemQueue_AnswersObjectPropertyOnceItsBrokerExists()
    {
        var simulation = new Simulation();
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select objectproperty(1, 'IsTable')"));
        AreEqual("1,1,1", simulation.ExecuteScalar(
            "select string_agg(cast(isnull(objectproperty(object_id, 'IsQueue'), -1) as varchar(5)), ',') from sys.objects where type = 'SQ'"));
    }

    [TestMethod]
    public void EveryDatabase_ShipsTheSystemObjects()
    {
        var simulation = new Simulation();
        AreEqual(14, simulation.ExecuteScalar("select count(*) from sys.service_message_types"));
        AreEqual("DEFAULT|14|N |BINARY", Rows(simulation, "select name, message_type_id, validation, validation_desc from sys.service_message_types where name = 'DEFAULT'"));
        AreEqual(
            "DEFAULT|6;http://schemas.microsoft.com/SQL/Notifications/PostEventNotification|2;http://schemas.microsoft.com/SQL/Notifications/PostQueryNotification|1;"
            + "http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice|3;http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic|5;"
            + "http://schemas.microsoft.com/SQL/ServiceBroker/ServiceEcho|4",
            Rows(simulation, "select name, service_contract_id from sys.service_contracts"));
        AreEqual(
            "QueryNotificationErrorsQueue|1977058079|True;EventNotificationErrorsQueue|2009058193|True;ServiceBrokerQueue|2041058307|True",
            Rows(simulation, "select name, object_id, is_ms_shipped from sys.service_queues"));
        AreEqual("3|4;3|5", Rows(simulation, "select * from sys.service_contract_usages"));
        AreEqual("AutoCreatedLocal|65536|LOCAL", Rows(simulation, "select name, route_id, address from sys.routes"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from ServiceBrokerQueue"));

        // The shipped objects can't be changed, but the route can go.
        _ = simulation.AssertSqlError("drop message type [DEFAULT]", 9663);
        _ = simulation.AssertSqlError("alter message type [DEFAULT] validation = empty", 9663);
        _ = simulation.AssertSqlError("drop contract [DEFAULT]", 9663);
        _ = simulation.AssertSqlError("drop service [http://schemas.microsoft.com/SQL/ServiceBroker/ServiceBroker]", 9663);
        _ = simulation.AssertSqlError("alter queue ServiceBrokerQueue with status = off", 9663);
        _ = simulation.AssertSqlError("drop queue ServiceBrokerQueue", 3716);
        AreEqual(0, simulation.ExecuteScalar("drop route AutoCreatedLocal; select count(*) from sys.routes"));
    }

    [TestMethod]
    public void Msdb_ShipsDatabaseMailsServices()
    {
        var simulation = new Simulation();
        AreEqual(
            "ExternalMailService|65537|1035150733;InternalMailService|65536|1003150619",
            Rows(simulation, "select name, service_id, service_queue_id from msdb.sys.services where name like '%Mail%' order by name"));
        AreEqual("[dbo].[sp_ExternalMailQueueListener]|1", Rows(simulation, "select activation_procedure, max_readers from msdb.sys.service_queues where name = 'InternalMailQueue'"));
    }

    [TestMethod]
    public void MessageTypesAndContracts_ProjectAndRefuse()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create xml schema collection xsc as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="xs:int"/></xs:schema>';
            """,
            """
            create message type m1;
            create message type m2 validation = empty;
            create message type m3 validation = well_formed_xml;
            create message type [//x/m4] authorization dbo validation = none;
            create message type m5 validation = valid_xml with schema collection xsc;
            create contract c1 (m1 sent by initiator, m2 sent by target, m3 sent by any);
            """);
        AreEqual(
            "m1|65536|N |BINARY|NULL;m2|65537|E |EMPTY|NULL;m3|65538|X |XML|NULL;//x/m4|65539|N |BINARY|NULL;m5|65540|X |XML|65536",
            Rows(simulation, "select name, message_type_id, validation, validation_desc, xml_collection_id from sys.service_message_types where message_type_id > 14"));
        AreEqual("65540|65536", Rows(simulation, "select * from sys.message_type_xml_schema_collection_usages"));
        AreEqual("65536|65536|True|False;65536|65537|False|True;65536|65538|True|True", Rows(simulation, "select * from sys.service_contract_message_usages where service_contract_id > 6"));

        // Broker names compare as written, whatever the database's collation.
        _ = simulation.ExecuteNonQuery("create message type M1");
        _ = simulation.AssertSqlError("create message type m1", 2714);
        _ = simulation.AssertSqlError("create message type m6 validation = valid_xml with schema collection nope", 15151);
        _ = simulation.AssertSqlError("create message type DEFAULT", 156);
        _ = simulation.AssertSqlError("create contract c2 (nope sent by any)", 15151);
        _ = simulation.AssertSqlError("create contract c2 (m1 sent by initiator, m1 sent by target)", 8403);
        _ = simulation.AssertSqlError("drop message type m1", 3716);
        _ = simulation.AssertSqlError("drop message type nope", 15151);
        _ = simulation.AssertSqlError("drop message type if exists nope", 156);

        _ = simulation.ExecuteNonQuery("alter message type m1 validation = empty");
        AreEqual("E ", simulation.ExecuteScalar("select validation from sys.service_message_types where name = 'm1'"));
        _ = simulation.AssertSqlError("alter message type nope validation = empty", 15151);
    }

    [TestMethod]
    public void Queues_ProjectTheirSettings()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create procedure act as set nocount on",
            """
            create queue q1;
            create queue dbo.q2 with status = off, retention = on;
            create queue q3 with activation (status = on, procedure_name = act, max_queue_readers = 2, execute as self), poison_message_handling (status = off);
            create queue q4 with activation (procedure_name = dbo.act, max_queue_readers = 0, execute as owner);
            """);
        AreEqual(
            "q1|0|NULL|NULL|False|True|True|False|True;"
            + "q2|0|NULL|NULL|False|False|False|True|True;"
            + "q3|2|[dbo].[act]|1|True|True|True|False|False;"
            + "q4|0|[dbo].[act]|-2|True|True|True|False|True",
            Rows(simulation, """
                select name, max_readers, activation_procedure, execute_as_principal_id, is_activation_enabled,
                    is_receive_enabled, is_enqueue_enabled, is_retention_enabled, is_poison_message_handling_enabled
                from sys.service_queues where is_ms_shipped = 0
                """));
        AreEqual("SQ|SERVICE_QUEUE", Rows(simulation, "select type, type_desc from sys.objects where name = 'q1'"));
        AreEqual("1|SQ|0", Rows(simulation, "select objectproperty(object_id('q1'), 'IsQueue'), objectpropertyex(object_id('q1'), 'BaseType'), objectproperty(object_id('q1'), 'IsTable')"));

        _ = simulation.ExecuteNonQuery("alter queue q1 with activation (procedure_name = act, max_queue_readers = 3, execute as owner, status = off), status = off");
        AreEqual("3|[dbo].[act]|False|False", Rows(simulation, "select max_readers, activation_procedure, is_activation_enabled, is_receive_enabled from sys.service_queues where name = 'q1'"));
        _ = simulation.ExecuteNonQuery("alter queue q1 with activation (drop)");
        AreEqual("0|NULL|NULL|False", Rows(simulation, "select max_readers, activation_procedure, execute_as_principal_id, is_activation_enabled from sys.service_queues where name = 'q1'"));
        _ = simulation.ExecuteNonQuery("alter queue q1 rebuild; alter queue q1 reorganize; alter queue q1 move to [primary]");

        _ = simulation.AssertSqlError("create queue q1", 2714);
        _ = simulation.AssertSqlError("create table q1 (a int)", 2714);
        _ = simulation.AssertSqlError("create queue q5 with activation (status = on, max_queue_readers = 1)", 102);
        _ = simulation.AssertSqlError("create queue q5 with activation (procedure_name = nope, max_queue_readers = 1, execute as self)", 2812);
        _ = simulation.AssertSqlError("create queue q5 with activation (procedure_name = act, max_queue_readers = 32768, execute as self)", 187);
        _ = simulation.AssertSqlError("create queue q5 on nope", 1921);
        _ = simulation.AssertSqlError("drop queue nope", 15151);
    }

    [TestMethod]
    public void Queue_IsReadOnlyToDml()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create queue q");
        AreEqual(15, simulation.ExecuteReader("select * from q").FieldCount);
        _ = simulation.AssertSqlError("insert q (status) values (1)", 416);
        _ = simulation.AssertSqlError("update q set status = 0", 416);
        _ = simulation.AssertSqlError("delete q", 416);
        _ = simulation.AssertSqlError("truncate table q", 4708);
        _ = simulation.AssertSqlError("drop table q", 3705);
        _ = simulation.AssertSqlError("alter table q add c int", 4909);
    }

    [TestMethod]
    public void Services_BindQueuesAndContracts()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create message type m; create contract c (m sent by any);
            create contract c2 ([DEFAULT] sent by any);
            create queue q1; create queue q2;
            create service s1 on queue q1 (c, [DEFAULT]);
            create service [//x/s2] authorization dbo on queue dbo.q1;
            """);
        var q1 = simulation.ExecuteScalar("select object_id('q1')");
        AreEqual($"s1|65536|{q1};//x/s2|65537|{q1}", Rows(simulation, "select name, service_id, service_queue_id from sys.services where service_id > 3"));
        AreEqual("65536|6;65536|65536", Rows(simulation, "select * from sys.service_contract_usages where service_id > 3"));

        _ = simulation.ExecuteNonQuery("alter service s1 (add contract c2, drop contract c)");
        AreEqual("65536|6;65536|65537", Rows(simulation, "select * from sys.service_contract_usages where service_id > 3"));
        _ = simulation.ExecuteNonQuery("alter service s1 on queue q2");
        AreEqual(simulation.ExecuteScalar("select object_id('q2')"), simulation.ExecuteScalar("select service_queue_id from sys.service_queue_usages where service_id = 65536"));

        _ = simulation.AssertSqlError("create service s1 on queue q1", 2714);
        _ = simulation.AssertSqlError("create service s3 on queue nope", 15151);
        _ = simulation.AssertSqlError("create service s3 on queue q1 (nope)", 15151);
        _ = simulation.AssertSqlError("create service s3 on queue q1 (c, c)", 8404);
        _ = simulation.AssertSqlError("alter service s1 (drop contract c)", 15151);
        _ = simulation.AssertSqlError("alter service s1 (add contract c2)", 8404);
        _ = simulation.AssertSqlError("drop queue q2", 3716);
        _ = simulation.AssertSqlError("drop contract c2", 3716);
        _ = simulation.AssertSqlError("drop service nope", 15151);

        _ = simulation.ExecuteNonQuery("drop service s1; drop service [//x/s2]; drop queue q1; drop queue q2; drop contract c; drop contract c2; drop message type m");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.services where service_id > 3"));
    }

    [TestMethod]
    public void RoutesBindingsAndPriorities_Project()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create queue q; create service s on queue q;
            create route r1 with service_name = 's', broker_instance = 'D8D4D268-02A3-4C62-8F91-634B89C1E315', address = 'TCP://h:4022', mirror_address = 'TCP://m:4022';
            create route r2 authorization dbo with address = 'TRANSPORT';
            create remote service binding b to service 's' with user = dbo, anonymous = on;
            create broker priority p1 for conversation set (contract_name = [DEFAULT], local_service_name = s, remote_service_name = N's', priority_level = 8);
            create broker priority p2 for conversation;
            """);
        AreEqual(
            "AutoCreatedLocal|NULL|LOCAL|NULL;r1|s|TCP://h:4022|TCP://m:4022;r2|NULL|TRANSPORT|NULL",
            Rows(simulation, "select name, remote_service_name, address, mirror_address from sys.routes"));
        AreEqual("b|s|1|True", Rows(simulation, "select name, remote_service_name, remote_principal_id, is_anonymous_on from sys.remote_service_bindings"));
        AreEqual("p1|6|65536|s|8;p2|NULL|NULL|NULL|5", Rows(simulation, "select name, service_contract_id, local_service_id, remote_service_name, priority from sys.conversation_priorities"));

        _ = simulation.AssertSqlError("create route r3 with service_name = 'x'", 102);
        _ = simulation.AssertSqlError("create route r3 with address = 'garbage'", 9744);
        _ = simulation.AssertSqlError("create route r1 with address = 'LOCAL'", 2714);
        _ = simulation.AssertSqlError("create broker priority p3 for conversation set (priority_level = 11)", 33302);
        _ = simulation.AssertSqlError("create broker priority p3 for conversation set (priority_level = default)", 33303);

        _ = simulation.ExecuteNonQuery("alter route r2 with address = 'LOCAL'; alter broker priority p1 for conversation set (priority_level = 3)");
        AreEqual("LOCAL", simulation.ExecuteScalar("select address from sys.routes where name = 'r2'"));
        AreEqual((byte)3, simulation.ExecuteScalar("select priority from sys.conversation_priorities where name = 'p1'"));
        _ = simulation.ExecuteNonQuery("drop broker priority p1; drop broker priority p2; drop remote service binding b; drop route r1; drop route r2");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.routes"));
    }

    [TestMethod]
    public void Ddl_RollsBackWithItsTransaction()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            begin tran;
            create message type m; create contract c (m sent by any); create queue q; create service s on queue q (c);
            rollback;
            """);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.service_message_types where name = 'm'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.services where name = 's'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.service_queues where name = 'q'"));
    }

    [TestMethod]
    public void Ddl_RaisesItsDdlEvents()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table ev (t sysname, s sysname null, o sysname, k sysname)",
            """
            create trigger dt on database for DDL_SSB_EVENTS as
            insert ev select eventdata().value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), eventdata().value('(/EVENT_INSTANCE/SchemaName)[1]', 'sysname'),
                eventdata().value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'), eventdata().value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname')
            """,
            "create message type m; create queue q; create service s on queue q; drop service s; drop queue q; drop message type m");
        AreEqual(
            "CREATE_MESSAGE_TYPE|NULL|m|MESSAGE TYPE;CREATE_QUEUE|dbo|q|QUEUE;CREATE_SERVICE|NULL|s|SERVICE;DROP_SERVICE|NULL|s|SERVICE;DROP_QUEUE|dbo|q|QUEUE;DROP_MESSAGE_TYPE|NULL|m|MESSAGE TYPE",
            Rows(simulation, "select * from ev"));
    }

    [TestMethod]
    public void Permissions_GrantOnBrokerClasses()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create user u without login;
            create message type m; create contract c (m sent by any); create queue q; create service s on queue q;
            create route r with address = 'LOCAL'; create remote service binding b to service 's' with user = dbo;
            grant send on service::s to u;
            grant control on contract::c to u;
            grant references on message type::m to u;
            grant receive on q to u;
            grant control on route::r to u;
            grant control on remote service binding::b to u;
            """);
        AreEqual(
            "1|OBJECT_OR_COLUMN|RECEIVE;15|MESSAGE_TYPE|REFERENCES;16|SERVICE_CONTRACT|CONTROL;17|SERVICE|SEND;18|REMOTE_SERVICE_BINDING|CONTROL;19|ROUTE|CONTROL",
            Rows(simulation, "select class, class_desc, permission_name from sys.database_permissions where grantee_principal_id = user_id('u') and class > 0 order by class"));
        _ = simulation.AssertSqlError("grant insert on q to u", 4606);
        _ = simulation.AssertSqlError("execute as user = 'u'; create queue q2", 262);
    }
    [TestMethod]
    public void Queue_TransfersAndRenames_KeepingItsIdAndService()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create schema s2", "create queue rq; create service rs on queue rq");
        var id = simulation.ExecuteScalar("select object_id('dbo.rq')");
        _ = simulation.ExecuteNonQuery("alter schema s2 transfer dbo.rq");
        AreEqual("s2|rq|rs", Rows(simulation, "select schema_name(q.schema_id), q.name, s.name from sys.services s join sys.service_queues q on q.object_id = s.service_queue_id where s.name = 'rs'"));
        AreEqual(id, simulation.ExecuteScalar("select object_id('s2.rq')"));
        _ = simulation.ExecuteNonQuery("alter schema dbo transfer object::s2.rq; exec sp_rename 'dbo.rq', 'rq2'");
        AreEqual(id, simulation.ExecuteScalar("select object_id from sys.service_queues where name = 'rq2'"));
        _ = simulation.ExecuteNonQuery("create queue rq9; create queue s2.rq9");
        simulation.AssertSqlError("alter schema s2 transfer dbo.rq9", 15530, "The object with name \"rq9\" already exists.");
        simulation.AssertSqlError("alter schema s2 transfer dbo.EventNotificationErrorsQueue", 15349, "Cannot transfer an MS Shipped object.");
        simulation.AssertSqlError("exec sp_rename 'dbo.EventNotificationErrorsQueue', 'x'", 15001,
            "Object 'dbo.EventNotificationErrorsQueue' does not exist or is not a valid object for this operation.");
    }
}
