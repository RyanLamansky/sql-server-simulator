using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The records running audits write — logins and logouts, object access,
/// schema object and permission changes, DBCC, backups and
/// <c>sp_audit_write</c> — read back through <c>sys.fn_get_audit_file</c>, as
/// SQL Server 2025 writes them (probed 2026-10-17).
/// </summary>
[TestClass]
public sealed class AuditRecordTests
{
    /// <summary>A started, synchronous file audit named <c>a</c> with a server specification taking <paramref name="groups"/>.</summary>
    private static Simulation Audited(params string[] groups)
    {
        var sim = new Simulation();
        var adds = string.Join(", ", groups.Select(static group => "add (" + group + ")"));
        _ = sim.ExecuteNonQuery($"""
            use master;
            create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0);
            create server audit specification s for server audit a {adds} with (state = on);
            alter server audit a with (state = on);
            use simulated
            """);
        return sim;
    }

    private const string Records = "sys.fn_get_audit_file('/var/opt/mssql/log/a_*', default, default)";

    private static string? Read(Simulation sim, string columns, string where = "action_id <> 'AUSC'") =>
        (string?)sim.ExecuteScalar($"select string_agg(concat_ws('|', {columns}, null), ',') from {Records} where {where}");

    [TestMethod]
    public void ObjectAccess_ActionsOnASecurableForAPrincipal()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int, b varchar(10)); insert t values (1, 'x')",
            "create procedure p as select 1",
            "use master; create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0); alter server audit a with (state = on)",
            """
            use simulated;
            create database audit specification d for server audit a
            add (select, insert on object::dbo.t by public), add (execute on dbo.p by dbo), add (delete on schema::dbo by public), add (update on database::simulated by public)
            with (state = on)
            """,
            "select * from t where a = 1; insert t values (2, 'y'); update t set b = 'z' where a = 2; delete t where a = 2; exec p");
        AreEqual("SL|1|1|U |dbo|t|select * from t where a = 1,IN|8|0|U |dbo|t|insert t values (2, 'y'),UP|2|1|U |dbo|t|update t set b = 'z' where a = 2,DL|16|0|U |dbo|t|delete t where a = 2,EX|32|0|P |dbo|p|exec p",
            Read(sim, "rtrim(action_id), cast(substring(permission_bitmask, 16, 1) as int), cast(is_column_permission as int), class_type, schema_name, object_name, statement",
                "action_id in ('SL', 'IN', 'UP', 'DL', 'EX') and statement not like '%where a = 2' or action_id in ('UP', 'DL')"));
        AreEqual("sa|sa|dbo|simulated", Read(sim, "server_principal_name, session_server_principal_name, database_principal_name, database_name", "action_id = 'EX'"));
    }

    /// <summary>
    /// An <c>UPDATE</c> reading its target's columns records <c>SL</c> ahead
    /// of its <c>UP</c>, from its <c>SET</c> list as from a <c>WHERE</c>
    /// (probed 2026-10-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ObjectAccess_AnUpdateReadingItsTarget_RecordsSelect()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int, b int); insert t values (1, 2)",
            "use master; create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0); alter server audit a with (state = on)",
            "use simulated; create database audit specification d for server audit a add (select, update on object::dbo.t by public) with (state = on)",
            "update t set a = b; update t set a = 5; update t set a = 7 where b = 2");
        AreEqual("SL|update t set a = b,UP|update t set a = b,UP|update t set a = 5,SL|update t set a = 7 where b = 2,UP|update t set a = 7 where b = 2",
            Read(sim, "rtrim(action_id), statement", "action_id in ('SL', 'UP')"));
    }

    // Real records the base table a view or procedure reads, the ownership
    // chain sparing its check, and a table joined to itself once.
    [TestMethod]
    public void ObjectAccess_ThroughModulesAndOncePerStatement()
    {
        var sim = Audited("schema_object_access_group");
        sim.ExecuteBatches(
            "create table t (a int); create table u (a int)",
            "create view v as select a from t",
            "create procedure p as select a from t",
            "select t.a from t join t t2 on t.a = t2.a join u on u.a = t.a; select * from v; exec p");
        AreEqual("SL|t|", Read(sim, "rtrim(action_id), object_name, cast(additional_information as nvarchar(max))", "statement like 'select t.a%' and object_name = 't'"));
        AreEqual(1, sim.ExecuteScalar($"select count(*) from {Records} where statement like 'select t.a%' and object_name = 'u'"));
        AreEqual("t,v", sim.ExecuteScalar($"select string_agg(object_name, ',') within group (order by object_name) from {Records} where statement = 'select * from v'"));
        AreEqual("<tsql_stack><frame nest_level = '1' database_name = 'simulated' schema_name = 'dbo' object_name = 'p'/></tsql_stack>",
            Read(sim, "cast(additional_information as nvarchar(max))", "statement = 'select a from t'"));
    }

    [TestMethod]
    public void ObjectAccess_ADeniedWriteRecordsItsFailure()
    {
        var sim = Audited("schema_object_access_group");
        _ = sim.ExecuteNonQuery("create table t (a int); create user u without login; grant select on t to u");
        using var connection = sim.CreateOpenConnection();
        using (var command = connection.CreateCommand("execute as user = 'u'; select a from t"))
            _ = command.ExecuteNonQuery();
        using (var insert = connection.CreateCommand("insert t values (9)"))
            AreEqual(229, Throws<SimulatedSqlException>(() => insert.ExecuteNonQuery()).Number);
        AreEqual("SL|1|u,IN|0|u", Read(sim, "rtrim(action_id), cast(succeeded as int), database_principal_name", "object_name = 't'"));
    }

    [TestMethod]
    public void SchemaObjectChange_RecordsTheSchemaAndTheObject()
    {
        var sim = Audited("schema_object_change_group", "database_object_change_group");
        sim.ExecuteBatches("create table dbo.t (c int)", "alter table dbo.t add d int", "drop table dbo.t");
        AreEqual("AL|SC|dbo|create table dbo.t (c int),CR|U |t|create table dbo.t (c int),AL|U |t|alter table dbo.t add d int,DR|U |t|drop table dbo.t",
            Read(sim, "rtrim(action_id), class_type, object_name, statement"));
    }

    [TestMethod]
    public void PermissionChange_CarriesTheBitAndTheGrantee()
    {
        var sim = Audited("schema_object_permission_change_group", "database_permission_change_group");
        _ = sim.ExecuteNonQuery("create table t (a int); create user u without login; grant select on t to u; revoke alter any database audit from u; deny delete on t to u");
        AreEqual("G|U |t|u|0x00000000000000000000000000000001,R|DB|simulated|u|0x00000000000000000200000000000000,D|U |t|u|0x00000000000000000000000000000010",
            Read(sim, "rtrim(action_id), class_type, object_name, target_database_principal_name, convert(varchar(40), permission_bitmask, 1)"));
    }

    [TestMethod]
    public void DbccBackupAndUserDefinedRecords()
    {
        var sim = Audited("dbcc_group", "backup_restore_group", "user_defined_audit_group");
        _ = sim.ExecuteNonQuery("create table t (a int)");
        _ = sim.AssertSqlError("dbcc checkident('dbo.t')", 7997);
        _ = sim.ExecuteNonQuery("""
            backup database simulated to disk = 'b.bak';
            exec sp_audit_write @user_defined_event_id = 27, @succeeded = 1, @user_defined_information = N'hello'
            """);
        AreEqual("DBCC|1|DB|simulated|dbcc checkident('dbo.t')|0|,BA|1|DB|simulated|backup database simulated to disk = 'b.bak'|0|,UDAU|1|DB|simulated|exec sp_audit_write @user_defined_event_id = 27, @succeeded = 1, @user_defined_information = N'hello'|27|hello",
            Read(sim, "rtrim(action_id), cast(succeeded as int), class_type, object_name, statement, user_defined_event_id, user_defined_information"));
    }

    [TestMethod]
    public void Logins_SucceedFailAndLogOut()
    {
        var sim = Audited("successful_login_group", "failed_login_group", "logout_group");
        _ = sim.ExecuteNonQuery("create login app with password = 'S3cret!Pass'");
        using (var connection = sim.CreateOpenConnection())
            _ = connection.State;
        var refused = sim.CreateDbConnection();
        refused.ConnectionString = "User ID=app;Password=wrong";
        _ = Throws<SimulatedSqlException>(refused.Open);
        var rows = Read(sim, "rtrim(action_id), cast(succeeded as int), class_type, server_principal_name, session_id");
        Contains("LGIS|1|LX|sa|", rows!);
        Contains("LGO|1|LX|sa|", rows!);
        Contains("LGIF|0|LX|app|0", rows!);
        StartsWith("-- network protocol: TCP/IP\nset quoted_identifier on", (string)sim.ExecuteScalar($"select top (1) statement from {Records} where action_id = 'LGIS'")!);
        AreEqual("Login failed for user 'app'. Reason: Password did not match that for the login provided. [CLIENT: <local machine>]",
            sim.ExecuteScalar($"select statement from {Records} where action_id = 'LGIF'"));
    }

    [TestMethod]
    public void Predicate_JudgesEachRecord()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int); create table u (a int);
            use master;
            create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0) where object_name = 'u' or action_id = 1129534785;
            create server audit specification s for server audit a add (schema_object_access_group) with (state = on);
            alter server audit a with (state = on);
            use simulated;
            select * from t; select * from u
            """);
        AreEqual("AUSC:,SL:u", sim.ExecuteScalar($"select string_agg(concat(rtrim(action_id), ':', object_name), ',') from {Records}"));
    }

    // A server whose audits run no specification covering an action writes
    // nothing for it.
    [TestMethod]
    public void NoSpecification_WritesOnlyTheAuditsOwnRecords()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int);
            use master;
            create server audit a to file (filepath = '/var/opt/mssql/log/') with (queue_delay = 0);
            alter server audit a with (state = on);
            use simulated;
            select * from t; grant select on t to public; dbcc freeproccache with no_infomsgs
            """);
        AreEqual("AUSC", sim.ExecuteScalar($"select string_agg(action_id, ',') from {Records}"));
    }
}
