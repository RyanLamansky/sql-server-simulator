using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Server-scope triggers (<c>CREATE TRIGGER … ON ALL SERVER</c>): their DDL
/// and refusals, the <c>sys.server_*</c> catalog, the server-scope DDL events
/// they fire on, and <c>sp_settriggerorder</c>'s <c>@namespace</c>. Probed
/// 2026-09-28 against SQL Server 2025; logon firing is
/// <see cref="LogonTriggerTests"/>.
/// </summary>
[TestClass]
public sealed class ServerTriggerTests
{
    private const string Harmless = "if original_login() = 'nobody_x' rollback";

    [TestMethod]
    public void Create_SurfacesInTheServerCatalog_NotTheDatabases()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches($"create trigger lt on all server for logon as {Harmless}");
        AreEqual(
            "lt|100|SERVER|0|TR|SQL_TRIGGER|0|0",
            simulation.ExecuteScalar("select concat(name, '|', parent_class, '|', parent_class_desc collate database_default, '|', parent_id, '|', type collate database_default, '|', type_desc collate database_default, '|', cast(is_ms_shipped as int), '|', cast(is_disabled as int)) from sys.server_triggers"));
        AreEqual(
            "147|LOGON|1|0|0|null",
            simulation.ExecuteScalar("select concat(type, '|', type_desc collate database_default, '|', cast(is_trigger_event as int), '|', cast(is_first as int), '|', cast(is_last as int), '|', isnull(event_group_type_desc collate database_default, 'null')) from sys.server_trigger_events"));
        AreEqual(
            $"create trigger lt on all server for logon as {Harmless}|1|1|null",
            simulation.ExecuteScalar("select concat(definition, '|', cast(uses_ansi_nulls as int), '|', cast(uses_quoted_identifier as int), '|', isnull(cast(execute_as_principal_id as varchar), 'null')) from sys.server_sql_modules"));
        AreEqual(
            "null|0|0",
            simulation.ExecuteScalar("select concat(isnull(cast(object_id('lt') as varchar), 'null'), '|', (select count(*) from sys.triggers), '|', (select count(*) from sys.objects where name = 'lt'))"));
    }

    [TestMethod]
    public void ExecuteAs_RecordsTheLoginsServerPrincipal_AndEncryptionHidesTheDefinition()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create trigger lt_a on all server with execute as 'sa', encryption after logon as {Harmless}",
            $"create trigger lt_b on all server with execute as self for logon as {Harmless}",
            $"create trigger lt_c on all server with execute as caller for logon as {Harmless}");
        AreEqual(
            "lt_a:null:1;lt_b:def:1;lt_c:def:null",
            simulation.ExecuteScalar("""
                select string_agg(concat(t.name, ':', iif(m.definition is null, 'null', 'def'), ':', isnull(cast(m.execute_as_principal_id as varchar), 'null')), ';') within group (order by t.name)
                from sys.server_triggers t join sys.server_sql_modules m on m.object_id = t.object_id
                """));
    }

    [TestMethod]
    public void CreateOrAlter_AndAlter_ReplaceInPlace()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create or alter trigger lt on all server for logon as if original_login() = 'x1' rollback",
            "create or alter trigger lt on all server for logon as if original_login() = 'x2' rollback",
            "alter trigger lt on all server for create_login as print 'y'");
        AreEqual("1|CREATE_LOGIN", simulation.ExecuteScalar("select concat((select count(*) from sys.server_triggers), '|', (select type_desc collate database_default from sys.server_trigger_events))"));
        AreEqual("CREATE trigger lt on all server for create_login as print 'y'", simulation.ExecuteScalar("select definition from sys.server_sql_modules"));
    }

    [TestMethod]
    public void DuplicateName_Msg2714_ButATableOrDatabaseTriggerOfTheNameIsNoConflict()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table lt (a int)",
            $"create trigger lt on all server for logon as {Harmless}",
            "create trigger dt on database for create_table as print 'd'",
            "create trigger dt on all server for create_table as print 's'");
        _ = simulation.AssertSqlError($"create trigger lt on all server for logon as {Harmless}", 2714);
    }

    [TestMethod]
    public void AlterMissing_Msg208()
        => _ = new Simulation().AssertSqlError("alter trigger nope on all server for logon as select 1", 208);

    [TestMethod]
    public void DropMissing_Msg3701_UnlessIfExists()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("drop trigger nope on all server", 3701);
        _ = simulation.ExecuteNonQuery("drop trigger if exists nope on all server");
    }

    [TestMethod]
    public void DropList_TakesTheScopeTrailer_ForEveryName()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create trigger lt_a on all server for logon as {Harmless}",
            $"create trigger lt_b on all server for logon as {Harmless}",
            "create trigger dt_a on database for create_table as print 'a'",
            "create trigger dt_b on database for create_table as print 'b'",
            "drop trigger lt_a, lt_b on all server; drop trigger dt_a, dt_b on database");
        AreEqual(0, simulation.ExecuteScalar("select (select count(*) from sys.server_triggers) + (select count(*) from sys.triggers)"));
    }

    [TestMethod]
    public void DropWithoutTheScope_DoesNotReachAServerTrigger()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches($"create trigger lt on all server for logon as {Harmless}");
        _ = simulation.AssertSqlError("drop trigger lt", 3701);
        _ = simulation.AssertSqlError("drop trigger lt on database", 3701);
    }

    [TestMethod]
    public void DisableEnable_ToggleIsDisabled_OneOrAll()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create trigger lt_a on all server for logon as {Harmless}",
            $"create trigger lt_b on all server for logon as {Harmless}",
            "disable trigger lt_a on all server");
        AreEqual("lt_a1lt_b0", simulation.ExecuteScalar("select string_agg(concat(name, cast(is_disabled as int)), '') within group (order by name) from sys.server_triggers"));
        _ = simulation.ExecuteNonQuery("disable trigger all on all server");
        AreEqual(2, simulation.ExecuteScalar("select sum(cast(is_disabled as int)) from sys.server_triggers"));
        _ = simulation.ExecuteNonQuery("enable trigger all on all server");
        AreEqual(0, simulation.ExecuteScalar("select sum(cast(is_disabled as int)) from sys.server_triggers"));
    }

    [TestMethod]
    public void DisableMissing_Msg1088State119_OnEitherScope()
    {
        var simulation = new Simulation();
        AreEqual(119, simulation.AssertSqlError("disable trigger nope on all server", 1088).State);
        AreEqual(119, simulation.AssertSqlError("enable trigger nope on database", 1088).State);
    }

    [TestMethod]
    public void SchemaPrefix_Msg1094()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError($"create trigger dbo.lt on all server for logon as {Harmless}", 1094);
        _ = simulation.AssertSqlError("create trigger dbo.dt on database for create_table as print 'x'", 1094);
        simulation.ExecuteBatches($"create trigger lt on all server for logon as {Harmless}");
        _ = simulation.AssertSqlError("disable trigger dbo.lt on all server", 1094);
        _ = simulation.AssertSqlError("drop trigger dbo.lt on all server", 1094);
    }

    [TestMethod]
    public void ExecuteAsOwner_Msg1083_OnEitherScope()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError($"create trigger lt on all server with execute as owner for logon as {Harmless}", 1083);
        _ = simulation.AssertSqlError("create trigger dt on database with execute as owner for create_table as print 'x'", 1083);
    }

    [TestMethod]
    public void ExecuteAsMissingLogin_Msg15151()
        => new Simulation().AssertSqlError(
            $"create trigger lt on all server with execute as 'no_such_login' for logon as {Harmless}",
            15151,
            "Cannot execute as the login 'no_such_login', because it does not exist or you do not have permission.");

    [TestMethod]
    public void EventTypes_ValidatedPerScope()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create trigger x on all server for insert as select 1", 1098);
        _ = simulation.AssertSqlError("create trigger x on database for logon as select 1", 1098);
        _ = simulation.AssertSqlError("create trigger x on database for create_login as print 'x'", 1098);
        _ = simulation.AssertSqlError("create trigger x on database for insert as print 'x'", 1098);
        simulation.AssertSqlError("create trigger x on all server for no_such_event as select 1", 1084, "'no_such_event' is an invalid event type.");
        simulation.AssertSqlError("create trigger x on all server instead of logon as select 1", 102, "Incorrect syntax near 'logon'.");
    }

    [TestMethod]
    public void LogonBesideDdlEvents_AndServerGroups_ExpandInTheCatalog()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create trigger lt on all server for logon, create_database as {Harmless}",
            "create trigger st on all server for ddl_login_events as print 'x'",
            "create trigger sg on all server for ddl_database_level_events as print 'x'");
        AreEqual(
            "lt:CREATE_DATABASE,LOGON;sg:158;st:ALTER_LOGIN,CREATE_LOGIN,DROP_LOGIN",
            simulation.ExecuteScalar("""
                select string_agg(x, ';') within group (order by x) from (
                    select concat(t.name, ':', iif(count(*) > 10, cast(count(*) as varchar), string_agg(e.type_desc collate database_default, ',') within group (order by e.type_desc))) x
                    from sys.server_triggers t join sys.server_trigger_events e on e.object_id = t.object_id
                    group by t.name) d
                """));
    }

    [TestMethod]
    public void BodyBindsAtCreate_Msg207_WhileAMissingTableDefers()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create trigger lt on all server for logon as if original_login() = 'nobody_x' select nocol from sys.objects", 207);
        simulation.ExecuteBatches("create trigger lt on all server for logon as if original_login() = 'nobody_x' select a from no_such_table");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.server_triggers"));
    }

    [TestMethod]
    public void Create_RollsBackWithItsTransaction()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran").ExecuteNonQuery();
        _ = connection.CreateCommand($"create trigger lt on all server for logon as {Harmless}").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("select count(*) from sys.server_triggers").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("select count(*) from sys.server_triggers").ExecuteScalar());
    }

    [TestMethod]
    public void NonSysadmin_Msg2104()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create login m_login with password = 'Xx!12345678abc'; create user m_login for login m_login; alter role db_owner add member m_login;");
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("execute as login = 'm_login'").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand($"create trigger lt on all server for logon as {Harmless}").ExecuteNonQuery());
        AreEqual(2104, ex.Number);
    }

    [TestMethod]
    public void ServerDdlTrigger_FiresForAnyDatabasesEvent_AheadOfTheDatabasesOwn_RunningInMaster()
    {
        var simulation = new Simulation();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        _ = connection.CreateCommand("create trigger st on all server for create_table as print concat('server ', db_name(), ' ', eventdata().value('(/EVENT_INSTANCE/DatabaseName)[1]', 'sysname'))").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger dt on database for create_table as print 'database'").ExecuteNonQuery();
        _ = connection.CreateCommand("create table t1 (a int)").ExecuteNonQuery();
        AreEqual("server master simulated|database", string.Join("|", messages));
    }

    [TestMethod]
    public void ServerDdlTrigger_ExecuteAs_RunsAsTheLogin()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create login m_login with password = 'Xx!12345678abc'",
            "create trigger st on all server with execute as 'm_login' for create_table as select concat(suser_sname(), '|', user_name(), '|', original_login())");
        AreEqual("m_login|guest|sa", simulation.ExecuteScalar("create table t1 (a int)"));
    }

    [TestMethod]
    public void LoginEvents_ReportTheLogin_WithThePasswordMasked()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table dbo.ev (e nvarchar(max))",
            "create trigger st on all server for ddl_login_events as insert simulated.dbo.ev select cast(eventdata() as nvarchar(max))",
            "create login m_login with password = 'Xx!12345678abc', check_policy = off",
            "alter login m_login with password = 'Yy!12345678abc'",
            "drop login m_login");
        var events = (string)simulation.ExecuteScalar("select string_agg(e, '|') from dbo.ev")!;
        Contains("<EventType>CREATE_LOGIN</EventType>", events);
        Contains("<LoginName>sa</LoginName><ObjectName>m_login</ObjectName><ObjectType>LOGIN</ObjectType><DefaultLanguage>us_english</DefaultLanguage><DefaultDatabase>master</DefaultDatabase><LoginType>SQL Login</LoginType><SID>", events);
        Contains("<CommandText>create login m_login with password = '******', check_policy = off</CommandText>", events);
        Contains("<CommandText>alter login m_login with password = '******'</CommandText>", events);
        Contains("<EventType>DROP_LOGIN</EventType>", events);
        DoesNotContain("12345678abc", events);
        DoesNotContain("<UserName>", events);
    }

    [TestMethod]
    public void DatabaseEvents_ReportTheDatabaseNamed()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table dbo.ev (e nvarchar(max))",
            "create trigger st on all server for create_database, alter_database, drop_database as insert simulated.dbo.ev select eventdata().value('(/EVENT_INSTANCE/EventType)[1]', 'sysname') + ':' + eventdata().value('(/EVENT_INSTANCE/DatabaseName)[1]', 'sysname')",
            "create database d1",
            "alter database d1 set recursive_triggers on",
            "drop database d1");
        AreEqual("CREATE_DATABASE:d1|ALTER_DATABASE:d1|DROP_DATABASE:d1", simulation.ExecuteScalar("select string_agg(e, '|') from dbo.ev"));
    }

    [TestMethod]
    public void SetTriggerOrder_ServerNamespace_PinsAndClears()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create trigger lt on all server for logon as {Harmless}",
            "exec sp_settriggerorder 'lt', 'First', 'LOGON', 'SERVER'");
        AreEqual("10", simulation.ExecuteScalar("select concat(cast(is_first as int), cast(is_last as int)) from sys.server_trigger_events"));
        _ = simulation.ExecuteNonQuery("exec sp_settriggerorder @triggername = 'lt', @order = 'None', @stmttype = 'LOGON', @namespace = 'SERVER'");
        AreEqual("00", simulation.ExecuteScalar("select concat(cast(is_first as int), cast(is_last as int)) from sys.server_trigger_events"));
    }

    [TestMethod]
    public void SetTriggerOrder_ScopedRefusals()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches($"create trigger lt on all server for logon as {Harmless}");
        _ = simulation.AssertSqlError("exec sp_settriggerorder 'lt', 'Last', 'LOGON'", 15600);
        _ = simulation.AssertSqlError("exec sp_settriggerorder 'lt', 'Last', 'LOGON', 'DATABASE'", 15165);
        _ = simulation.AssertSqlError("exec sp_settriggerorder 'nope', 'Last', 'LOGON', 'SERVER'", 15165);
        simulation.AssertSqlError("exec sp_settriggerorder 'lt', 'Last', 'CREATE_TABLE', 'SERVER'", 15125, "Trigger 'lt' is not a trigger for 'create_table'.");
    }

    [TestMethod]
    public void SetTriggerOrder_DatabaseNamespace_OrdersDdlTriggers()
    {
        var simulation = new Simulation();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        _ = connection.CreateCommand("create trigger o1 on database for create_table as print 'o1'").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger o2 on database for create_table as print 'o2'").ExecuteNonQuery();
        _ = connection.CreateCommand("exec sp_settriggerorder 'o2', 'First', 'CREATE_TABLE', 'DATABASE'").ExecuteNonQuery();
        _ = connection.CreateCommand("create table t1 (a int)").ExecuteNonQuery();
        AreEqual("o2|o1", string.Join("|", messages));
        AreEqual(1, connection.CreateCommand("select cast(is_first as int) from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.name = 'o2'").ExecuteScalar());
        var conflict = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_settriggerorder 'o1', 'First', 'CREATE_TABLE', 'DATABASE'").ExecuteNonQuery());
        AreEqual("There already exists a 'first' trigger for 'create_table'.", conflict.Errors[0].Message);
        AreEqual(15600, Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_settriggerorder 'o2', 'First', 'DDL_TABLE_EVENTS', 'DATABASE'").ExecuteNonQuery()).Number);
    }
}
