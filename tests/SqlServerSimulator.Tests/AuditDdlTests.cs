using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE</c> / <c>ALTER</c> / <c>DROP SERVER AUDIT</c>, the server and
/// database audit specifications, their catalog views and
/// <c>sys.fn_get_audit_file</c>'s start and stop records, as SQL Server 2025
/// answers them (probed 2026-10-17).
/// </summary>
[TestClass]
public sealed class AuditDdlTests
{
    private const string LogPath = "/var/opt/mssql/log/";

    private static void Run(System.Data.Common.DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand(commandText);
        _ = command.ExecuteNonQuery();
    }

    private static object? Scalar(System.Data.Common.DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand(commandText);
        return command.ExecuteScalar();
    }

    private static void Refuse(System.Data.Common.DbConnection connection, string commandText, int number)
    {
        using var command = connection.CreateCommand(commandText);
        AreEqual(number, Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Number);
    }

    [TestMethod]
    public void Create_ListsTheAuditAndItsFileOptions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            use master;
            create server audit a to file (filepath = '/var/opt/mssql/log', maxsize = 3 tb, max_rollover_files = unlimited, reserve_disk_space = off)
            with (queue_delay = 0, on_failure = fail_operation, audit_guid = '11111111-2222-3333-4444-555555555555')
            where database_name = 'd' and object_name like 'zz%' or (statement <> N'x' and not session_id > 5)
            """);
        AreEqual("65536|a|11111111-2222-3333-4444-555555555555|FL|FILE|2|FAIL OPERATION|0|0|([database_name]='d' AND [object_name] like 'zz%' OR [statement]<>N'x' AND NOT [session_id]>(5))", sim.ExecuteScalar("""
            select concat_ws('|', audit_id, name, audit_guid, type collate database_default, type_desc collate database_default, on_failure, on_failure_desc collate database_default, cast(is_state_enabled as int), queue_delay, predicate) from sys.server_audits
            """));
        AreEqual("3145728|2147483647|0|0|/var/opt/mssql/log/|a_11111111-2222-3333-4444-555555555555.sqlaudit|0", sim.ExecuteScalar("""
            select concat_ws('|', max_file_size, max_rollover_files, max_files, cast(reserve_disk_space as int), log_file_path, log_file_name, retention_days) from sys.server_file_audits
            """));
    }

    [TestMethod]
    public void Create_EventLogTargetsHaveNoFileRow()
        => AreEqual("AL:APPLICATION LOG:1000,SL:SECURITY LOG:1000|0", new Simulation().ExecuteScalar("""
            use master;
            create server audit a to application_log;
            create server audit b to security_log;
            select concat((select string_agg(concat(type, ':', type_desc, ':', queue_delay) collate database_default, ',') within group (order by audit_id) from sys.server_audits), '|', (select count(*) from sys.server_file_audits))
            """));

    // A refused CREATE past the permission check spends an id.
    [TestMethod]
    public void Create_RefusalSpendsAnAuditId()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("use master; create server audit a to file (filepath = '/nonexist/')", 33072);
        _ = sim.ExecuteNonQuery("use master; create server audit b to file (filepath = '/var/opt/mssql/log/')");
        AreEqual(65537, sim.ExecuteScalar("select audit_id from sys.server_audits"));
    }

    [TestMethod]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/', maxsize = 1 mb)", 33070)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/', max_files = 2, max_rollover_files = 3)", 33238)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/', reserve_disk_space = on)", 33077)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 500)", 33227)]
    [DataRow("create server audit a to file (filepath = '')", 15021)]
    [DataRow("create server audit a to file (filepath = 'relative/')", 33072)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/') with (audit_guid = 'x')", 8169)]
    [DataRow("create server audit a to url (path = 'x')", 40517)]
    [DataRow("create server audit a to external_monitor", 40517)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/') with (operator_audit = on)", 40517)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/') with (state = on)", 102)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/', maxsize = 5 kb)", 102)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/', maxsize = 2.5 gb)", 1080)]
    [DataRow("create server audit a to file (maxsize = 5 mb)", 102)]
    [DataRow("create server audit a", 102)]
    [DataRow("create server audit [#a] to application_log", 1074)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/') where foo = 1", 25706)]
    [DataRow("create server audit a to file (filepath = '/var/opt/mssql/log/') where action_id = 'SL'", 25713)]
    public void Create_RefusesAsRealDoes(string statement, int number)
        => _ = new Simulation().AssertSqlError("use master; " + statement, number);

    [TestMethod]
    public void Create_TakenNameOrGuid()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("use master; create server audit a to application_log with (audit_guid = '11111111-2222-3333-4444-555555555555')");
        sim.AssertSqlError("use master; create server audit a to application_log", 15530, "The audit with name \"a\" already exists.");
        sim.AssertSqlError("use master; create server audit b to application_log with (audit_guid = '11111111-2222-3333-4444-555555555555')", 15530, "The audit with name \"b\" already exists.");
    }

    [TestMethod]
    public void Ddl_OutsideMaster_IsRefused()
        => new Simulation().AssertSqlError("create server audit a to application_log", 33074,
            "Cannot create a server audit from a user database. This operation must be performed in the master database.");

    [TestMethod]
    public void Alter_StartStopsAndRefusesChangesWhileRunning()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "use master; create server audit a to file (filepath = '/var/opt/mssql/log/'); alter server audit a with (state = on)");
        AreEqual("1|STARTED", Scalar(connection, "select concat(status, '|', status_desc) from sys.dm_server_audit_status"));
        StartsWith(LogPath + "a_", (string)Scalar(connection, "select audit_file_path from sys.dm_server_audit_status")!);
        AreEqual(20480L, Scalar(connection, "select audit_file_size from sys.dm_server_audit_status"));
        Run(connection, "alter server audit a with (state = on)");
        Refuse(connection, "alter server audit a with (queue_delay = 0)", 33071);
        Refuse(connection, "alter server audit a modify name = b", 33071);
        Refuse(connection, "drop server audit a", 33071);
        Refuse(connection, "alter server audit a with (state = off) where object_id = 1", 33223);
        Run(connection, "alter server audit a with (state = off); alter server audit a with (state = off)");
        AreEqual(0, Scalar(connection, "select count(*) from sys.dm_server_audit_status"));
    }

    [TestMethod]
    public void Alter_ChangesTargetOptionsPredicateAndName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            use master;
            create server audit a to application_log;
            alter server audit a to file (filepath = '/tmp', maxsize = 20 mb) with (on_failure = fail_operation) where object_id = 1;
            alter server audit a modify name = b
            """);
        AreEqual("b|FL|FAIL OPERATION|([object_id]=(1))|20|/tmp/", sim.ExecuteScalar("""
            select concat_ws('|', a.name, a.type collate database_default, a.on_failure_desc collate database_default, a.predicate, max_file_size, log_file_path) from sys.server_audits a join sys.server_file_audits f on f.audit_id = a.audit_id
            """));
        _ = sim.ExecuteNonQuery("use master; alter server audit b remove where");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_audits where predicate is null"));
        sim.AssertSqlError("use master; alter server audit nope with (state = on)", 15151, "Cannot alter the audit 'nope', because it does not exist or you do not have permission.");
        _ = sim.AssertSqlError("use master; alter server audit b with (queue_delay = 5)", 33227);
    }

    [TestMethod]
    public void Transaction_RollsBackDdlButRefusesState()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "use master; begin tran; create server audit a to application_log; rollback");
        AreEqual(0, Scalar(connection, "select count(*) from sys.server_audits"));
        Run(connection, "create server audit a to application_log; begin tran; drop server audit a; rollback");
        AreEqual(1, Scalar(connection, "select count(*) from sys.server_audits"));
        Refuse(connection, "begin tran; alter server audit a with (state = on)", 574);
        Run(connection, "if @@trancount > 0 rollback");
    }

    [TestMethod]
    public void ServerSpecification_ListsGroupsAndRefusesAsRealDoes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            use master;
            create server audit a to application_log;
            create server audit specification s for server audit a add (successful_login_group), add (failed_login_group), add (failed_login_group) with (state = on);
            """);
        AreEqual("65536|1", sim.ExecuteScalar("select concat(server_specification_id, '|', cast(is_state_enabled as int)) from sys.server_audit_specifications"));
        AreEqual("LGFL:FAILED_LOGIN_GROUP:100:SERVER:2:SUCCESS AND FAILURE:1,LGSD:SUCCESSFUL_LOGIN_GROUP:100:SERVER:2:SUCCESS AND FAILURE:1", sim.ExecuteScalar("""
            select string_agg(concat_ws(':', rtrim(audit_action_id) collate database_default, audit_action_name, class, class_desc collate database_default, audited_principal_id, audited_result, cast(is_group as int)), ',') from sys.server_audit_specification_details
            """));
        _ = sim.AssertSqlError("use master; alter server audit specification s add (logout_group)", 33229);
        _ = sim.AssertSqlError("use master; drop server audit specification s", 33071);
        sim.AssertSqlError("use master; create server audit specification t for server audit a", 33230, "An audit specification for audit 'a' already exists.");
        sim.AssertSqlError("use master; create server audit specification t for server audit nope", 33073, "Cannot find audit 'nope' or you do not have the required permissions.");
        _ = sim.AssertSqlError("use master; create server audit specification t for server audit a add (nope_group)", 102);
        _ = sim.AssertSqlError("use master; create server audit specification t for server audit a add (select)", 102);
        sim.AssertSqlError("use master; drop server audit specification nope", 33073, "Cannot find audit specification 'nope' or you do not have the required permissions.");
        _ = sim.ExecuteNonQuery("use master; alter server audit specification s with (state = off); alter server audit specification s drop (failed_login_group), add (logout_group); drop server audit a");
        AreEqual("LGSD,LO", sim.ExecuteScalar("select string_agg(rtrim(audit_action_id) collate database_default, ',') from sys.server_audit_specification_details"));
    }

    [TestMethod]
    public void DatabaseSpecification_ListsActionsBySecurable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int); create user u without login",
            "create procedure p as select 1",
            "use master; create server audit a to application_log",
            """
            use simulated;
            create database audit specification d for server audit a
            add (select, insert on object::dbo.t by public), add (execute on dbo.p by dbo), add (schema_object_change_group),
            add (delete on schema::dbo by public), add (update on database::simulated by u) with (state = on)
            """);
        AreEqual("MNO:0:0:0:1,UP:0:0:5:0,IN:1:t:0:0,SL:1:t:0:0,EX:1:p:1:0,DL:3:1:0:0", sim.ExecuteScalar("""
            select string_agg(concat_ws(':', rtrim(audit_action_id) collate database_default, class, case class when 1 then object_name(major_id) else cast(major_id as sysname) end, audited_principal_id, cast(is_group as int)), ',')
            from sys.database_audit_specification_details
            """));
        _ = sim.AssertSqlError("create database audit specification e for server audit a add (select (a) on dbo.t by public)", 33211);
        sim.AssertSqlError("create database audit specification e for server audit a add (select on dbo.t by nobody)", 15151, "Cannot find the user 'nobody', because it does not exist or you do not have permission.");
        _ = sim.AssertSqlError("create database audit specification e for server audit a add (failed_login_group)", 102);
        _ = sim.AssertSqlError("create database audit specification e for server audit a", 33230);
        _ = sim.AssertSqlError("drop database audit specification d", 33071);
    }

    [TestMethod]
    public void Permissions_GateDdlAndViews()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create login l with password = 'Zz!passw0rd1';
            create login m with password = 'Zz!passw0rd1';
            use master; grant alter any server audit to m;
            create user l; create user m;
            create server audit a to application_log
            """);
        using var connection = sim.CreateOpenConnection();
        Run(connection, "use master; execute as login = 'l'");
        Refuse(connection, "create server audit b to application_log", 15247);
        Refuse(connection, "drop server audit a", 15151);
        AreEqual(0, Scalar(connection, "select count(*) from sys.server_audits"));
        Refuse(connection, "select count(*) from sys.fn_get_audit_file('/var/opt/mssql/log/*', default, default)", 300);
        Run(connection, "revert; execute as login = 'm'");
        AreEqual(1, Scalar(connection, "select count(*) from sys.server_audits"));
        Refuse(connection, "create server audit b to application_log with (on_failure = shutdown)", 33228);
        Run(connection, "create server audit b to application_log; drop server audit b; revert");
    }

    [TestMethod]
    public void DdlTriggers_SeeTheAuditEvents()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.ev (x xml)",
            "create trigger st on all server for ddl_server_audit_events, ddl_server_audit_specification_events as insert simulated.dbo.ev values (eventdata())");
        AreEqual("CREATE_SERVER_AUDIT:SERVER AUDIT:a,ALTER_SERVER_AUDIT:SERVER AUDIT:a,CREATE_SERVER_AUDIT_SPECIFICATION:SERVER AUDIT SPECIFICATION:s,DROP_SERVER_AUDIT_SPECIFICATION:SERVER AUDIT SPECIFICATION:s,DROP_SERVER_AUDIT:SERVER AUDIT:a",
            sim.ExecuteScalar("""
                use master;
                create server audit a to application_log;
                alter server audit a with (queue_delay = 0);
                create server audit specification s for server audit a;
                drop server audit specification s;
                drop server audit a;
                use simulated;
                select string_agg(concat(x.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), ':', x.value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname'), ':', x.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname')), ',') from dbo.ev
                """));
    }

    [TestMethod]
    public void AuditFile_StartAndStopWriteTheAuditsOwnRecords()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("use master; create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0); alter server audit a with (state = on); alter server audit a with (state = off)");
        AreEqual("AUSC:A :event enabled,AUSC:A :destroyed,AUSC:A :event disabled", sim.ExecuteScalar("""
            select string_agg(concat(action_id, ':', class_type, ':', cast(additional_information as xml).value('declare namespace a="http://schemas.microsoft.com/sqlserver/2008/sqlaudit_data"; (/a:action_info/a:action)[1]', 'sysname')), ',')
            from sys.fn_get_audit_file('/var/opt/mssql/log/a_*', default, default)
            """));
        AreEqual(16384L, sim.ExecuteScalar("select max(audit_file_offset) from sys.fn_get_audit_file('/var/opt/mssql/log/*.sqlaudit', default, default)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.fn_get_audit_file('/var/opt/mssql/log/', default, default)"));
        _ = sim.AssertSqlError("select count(*) from sys.fn_get_audit_file(null, default, default)", 33224);
        _ = sim.AssertSqlError("select count(*) from sys.fn_get_audit_file('/nonexist/*', default, default)", 33224);
        _ = sim.AssertSqlError("select count(*) from sys.fn_get_audit_file('/var/opt/mssql/log/*', default, 16384)", 33225);
        _ = sim.AssertSqlError("select count(*) from sys.fn_get_audit_file_v2('/var/opt/mssql/log/*', default, default, '2026-10-17 10:50:00', default)", 37620);
    }

    // The predicate judges an audit's own records too.
    [TestMethod]
    public void AuditFile_PredicateFiltersTheStartRecord()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            use master;
            create server audit a to file (filepath = '/var/opt/mssql/log/') where object_id = 5;
            alter server audit a with (state = on);
            select count(*) from sys.fn_get_audit_file('a_*', default, default)
            """));

    [TestMethod]
    public void AuditChangeGroup_RecordsAuditDdl()
        => AreEqual("CR:A :master:b,AL:A :master:b,DR:A :master:b", new Simulation().ExecuteScalar("""
            use master;
            create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0);
            create server audit specification s for server audit a add (audit_change_group);
            alter server audit specification s with (state = on);
            alter server audit a with (state = on);
            create server audit b to application_log;
            alter server audit b with (queue_delay = 0);
            drop server audit b;
            select string_agg(concat(rtrim(action_id), ':', class_type, ':', database_name, ':', object_name), ',') from sys.fn_get_audit_file('/var/opt/mssql/log/a_*', default, default) where action_id <> 'AUSC'
            """));

    [TestMethod]
    public void BackupRestore_CarriesDatabaseSpecifications()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database d");
        _ = sim.ExecuteNonQuery("""
            use master; create server audit a to application_log;
            use d; create table t (a int);
            create database audit specification s for server audit a add (select on dbo.t by public) with (state = on);
            backup database d to disk = N'd.bak';
            restore database d2 from disk = N'd.bak' with move N'd' to N'/var/opt/mssql/data/d2.mdf', move N'd_log' to N'/var/opt/mssql/data/d2_log.ldf'
            """);
        AreEqual("65536:s:1:SL", sim.ExecuteScalar("""
            use d2;
            select concat(s.database_specification_id, ':', s.name, ':', cast(s.is_state_enabled as int), ':', rtrim(d.audit_action_id) collate database_default) from sys.database_audit_specifications s join sys.database_audit_specification_details d on d.database_specification_id = s.database_specification_id
            """));
    }

    [TestMethod]
    public void Metadata_ListsRealsActionsAndClasses()
    {
        var sim = new Simulation();
        AreEqual(663, sim.ExecuteScalar("select count(*) from sys.dm_audit_actions"));
        AreEqual(110, sim.ExecuteScalar("select count(*) from sys.dm_audit_class_type_map"));
        AreEqual("SCHEMA_OBJECT_ACCESS_GROUP", sim.ExecuteScalar("select containing_group_name from sys.dm_audit_actions where action_id = 'SL' and class_desc = 'OBJECT'"));
        AreEqual("TABLE", sim.ExecuteScalar("select class_type_desc from sys.dm_audit_class_type_map where class_type = 'U'"));
    }
}
