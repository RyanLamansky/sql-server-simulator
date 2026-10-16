using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A database's Service Broker state through <c>BACKUP</c> and <c>RESTORE</c>,
/// and the broker switches of <c>RESTORE … WITH</c> and <c>ALTER DATABASE …
/// SET</c>, as SQL Server 2025 runs them (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class ServiceBrokerBackupTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Database <c>b1</c>, broker enabled, holding one dialog with a message on the target's queue, backed up to <c>b1.bak</c>.</summary>
    private static Simulation Backed()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database b1",
            """
            use b1;
            create message type m validation = well_formed_xml;
            create contract c (m sent by any);
            create queue q with status = on, retention = off;
            create queue q2;
            create service s on queue q (c);
            create service s2 on queue q2 (c);
            create route r with address = 'LOCAL';
            declare @h uniqueidentifier;
            begin dialog @h from service s to service 's2' on contract c with encryption = off;
            send on conversation @h message type m (N'<a/>');
            """,
            "backup database b1 to disk = N'b1.bak' with init");
        return simulation;
    }

    private const string CopyB2 = "restore database b2 from disk = N'b1.bak' with move N'b1' to N'/var/opt/mssql/data/b2.mdf', move N'b1_log' to N'/var/opt/mssql/data/b2_log.ldf'";

    private static string Rows(DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        var rows = new List<string>();
        do
        {
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToInvariantString())));
        }
        while (reader.NextResult());
        return string.Join(";", rows);
    }

    private static string Rows(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        return Rows(connection, sql);
    }

    private const string Shape = """
        select (select string_agg(concat(message_type_id, ':', name collate database_default), ',') from sys.service_message_types where message_type_id > 65535),
            (select string_agg(concat(service_contract_id, ':', name collate database_default), ',') from sys.service_contracts where service_contract_id > 65535),
            (select string_agg(concat(object_id, ':', name, ':', is_receive_enabled), ',') within group (order by name) from sys.service_queues where is_ms_shipped = 0),
            (select string_agg(concat(service_id, ':', name collate database_default), ',') within group (order by name) from sys.services where service_id > 65535),
            (select string_agg(concat(route_id, ':', name), ',') within group (order by name) from sys.routes),
            (select count(*) from q2),
            (select string_agg(concat(state, ':', far_service collate database_default), ',') within group (order by far_service) from sys.conversation_endpoints)
        """;

    [TestMethod]
    public void Restore_OverItself_BringsBackObjectsMessagesAndConversations_BrokerDisabled()
    {
        var simulation = Backed();
        var before = Rows(simulation, "use b1; " + Shape);
        var guid = simulation.ExecuteScalar("select service_broker_guid from sys.databases where name = 'b1'");
        _ = simulation.ExecuteNonQuery("""
            use b1; drop service s2; drop queue q2;
            use master; restore database b1 from disk = N'b1.bak' with replace
            """);
        AreEqual(before, Rows(simulation, "use b1; " + Shape));
        AreEqual($"False|{guid}", Rows(simulation, "select is_broker_enabled, service_broker_guid from sys.databases where name = 'b1'"));
        // The dialog goes on: its target receives the message and answers.
        AreEqual("m|<a/>", Rows(simulation, """
            use b1; alter database b1 set enable_broker;
            declare @h uniqueidentifier, @t sysname, @b varbinary(max);
            receive top (1) @h = conversation_handle, @t = message_type_name, @b = message_body from q2;
            send on conversation @h message type m (N'<b/>');
            select @t, cast(cast(@b as xml) as nvarchar(max))
            """));
        AreEqual(1, simulation.ExecuteScalar("use b1; select count(*) from q"));
    }

    [TestMethod]
    public void Restore_AsCopy_SharesTheBrokerId_AndCanNotEnableBesideTheOriginal()
    {
        var simulation = Backed();
        _ = simulation.ExecuteNonQuery(CopyB2);
        AreEqual("b1|True|1;b2|False|1", Rows(simulation,
            "select name, is_broker_enabled, (select count(distinct service_broker_guid) from sys.databases where name like 'b_') from sys.databases where name like 'b_' order by name"));
        AreEqual(Rows(simulation, "use b1; select conversation_handle from sys.conversation_endpoints order by conversation_handle"),
            Rows(simulation, "use b2; select conversation_handle from sys.conversation_endpoints order by conversation_handle"));
        var ex = simulation.AssertSqlError("alter database b2 set enable_broker", 9772);
        AreEqual("The Service Broker in database \"b2\" cannot be enabled because there is already an enabled Service Broker with the same ID.", ex.Errors[0].Message);
        AreEqual(5069, ex.Errors[1].Number);
        _ = simulation.AssertSqlError("alter database b2 set error_broker_conversations", 9772);

        using var connection = simulation.CreateOpenConnection();
        var notices = new List<(int Number, string Message)>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => notices.AddRange(e.Errors.Select(static error => (error.Number, error.Message)));
        _ = connection.CreateCommand(CopyB2.Replace("move", "replace, enable_broker, move", StringComparison.Ordinal)).ExecuteNonQuery();
        var id = simulation.ExecuteScalar("select db_id('b2')").ToInvariantString();
        CollectionAssert.AreEqual(new[] { 4035, 4035, 9773, 3014 }, notices.Select(static notice => notice.Number).ToArray());
        AreEqual($"The Service Broker in database \"{id}\" is disabled because there is already an enabled Service Broker with the same ID.", notices[2].Message);
        IsFalse((bool)simulation.ExecuteScalar("select is_broker_enabled from sys.databases where name = 'b2'")!);
    }

    [TestMethod]
    public void Restore_NewBroker_DrawsAnId_EnablesIt_AndDropsTheConversations()
    {
        var simulation = Backed();
        _ = simulation.ExecuteNonQuery(CopyB2.Replace("move", "new_broker, move", StringComparison.Ordinal));
        AreEqual("True|2|0|0", Rows(simulation, """
            select is_broker_enabled, (select count(distinct service_broker_guid) from sys.databases where name like 'b_'),
                (select count(*) from b2.sys.conversation_endpoints), (select count(*) from b2.dbo.q2)
            from sys.databases where name = 'b2'
            """));
    }

    [TestMethod]
    public void Restore_ErrorBrokerConversations_ErrorsEveryEndpoint()
    {
        var simulation = Backed();
        _ = simulation.ExecuteNonQuery("restore database b1 from disk = N'b1.bak' with replace, error_broker_conversations");
        AreEqual("True", Rows(simulation, "select is_broker_enabled from sys.databases where name = 'b1'"));
        AreEqual("ER|s;ER|s2", Rows(simulation, "use b1; select state, far_service from sys.conversation_endpoints order by far_service"));
        AreEqual("http://schemas.microsoft.com/SQL/ServiceBroker/Error|-9719;m|NULL;http://schemas.microsoft.com/SQL/ServiceBroker/Error|-9719", Rows(simulation, """
            use b1;
            select message_type_name, cast(message_body as xml).value('declare namespace e="http://schemas.microsoft.com/SQL/ServiceBroker/Error"; (/e:Error/e:Code)[1]', 'int') from q
            union all
            select message_type_name, case when message_type_name = 'm' then null else cast(message_body as xml).value('declare namespace e="http://schemas.microsoft.com/SQL/ServiceBroker/Error"; (/e:Error/e:Code)[1]', 'int') end from q2
            """));
    }

    [TestMethod]
    [DataRow("new_broker, norecovery", 3031, "Option 'norecovery' conflicts with option(s) 'broker_option'. Remove the conflicting option and reissue the statement.")]
    [DataRow("norecovery, enable_broker", 3031, "Option 'norecovery' conflicts with option(s) 'broker_option'. Remove the conflicting option and reissue the statement.")]
    [DataRow("new_broker, enable_broker", 102, "Incorrect syntax near 'enable_broker'.")]
    public void Restore_BrokerOptionGrammar(string options, int number, string message) =>
        Backed().AssertSqlError($"restore database b1 from disk = N'b1.bak' with replace, {options}", number, message);

    [TestMethod]
    public void Restore_EnableBrokerTwice_IsOneSwitch()
    {
        var simulation = Backed();
        _ = simulation.ExecuteNonQuery("restore database b1 from disk = N'b1.bak' with replace, enable_broker, enable_broker");
        IsTrue((bool)simulation.ExecuteScalar("select is_broker_enabled from sys.databases where name = 'b1'")!);
    }

    [TestMethod]
    public void Restore_RecoveryAlone_TakesTheSwitch()
    {
        var simulation = Backed();
        var guid = simulation.ExecuteScalar("select service_broker_guid from sys.databases where name = 'b1'");
        _ = simulation.ExecuteNonQuery("restore database b1 from disk = N'b1.bak' with replace, norecovery");
        _ = simulation.ExecuteNonQuery("restore database b1 with recovery, new_broker");
        IsTrue((bool)simulation.ExecuteScalar("select is_broker_enabled from sys.databases where name = 'b1'")!);
        AreNotEqual(guid, simulation.ExecuteScalar("select service_broker_guid from sys.databases where name = 'b1'"));
    }

    [TestMethod]
    public void AlterDatabase_NewBroker_DrawsAnId_AndDropsTheConversations()
    {
        var simulation = Backed();
        var guid = simulation.ExecuteScalar("select service_broker_guid from sys.databases where name = 'b1'");
        _ = simulation.ExecuteNonQuery("alter database b1 set new_broker");
        AreNotEqual(guid, simulation.ExecuteScalar("select service_broker_guid from sys.databases where name = 'b1'"));
        AreEqual("0|0", Rows(simulation, "use b1; select (select count(*) from sys.conversation_endpoints), (select count(*) from q2)"));
    }

    [TestMethod]
    public void BrokerIds_EachDatabaseItsOwn_MasterAndModelNone()
        => AreEqual("00000000-0000-0000-0000-000000000000|00000000-0000-0000-0000-000000000000|2", Rows(Backed(), """
            select (select service_broker_guid from sys.databases where name = 'master'), (select service_broker_guid from sys.databases where name = 'model'),
                (select count(distinct service_broker_guid) from sys.databases where name in ('b1', 'msdb'))
            """));
}
