using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The server-health surfaces sp_Blitz and its siblings read: the mirroring,
/// memory-cache and default-trace views, <c>fn_trace_gettable</c>
/// and the legacy <c>::</c> prefix, <c>DBCC DBINFO</c>, the shipped <c>msdb</c>
/// tables, the Agent and login procedures, and the system functions a
/// three-part name, <c>OBJECT_ID</c> and <c>APPLY</c> reach (probed
/// 2026-10-16 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class HealthCheckSurfaceTests
{
    [DataRow("sys.database_automatic_tuning_options", -598, 1)]
    [DataRow("sys.database_mirroring_endpoints", -235, 0)]
    [DataRow("sys.dm_db_column_store_row_group_physical_stats", -586, 0)]
    [DataRow("sys.dm_hadr_availability_replica_states", -744484871, 0)]
    [DataRow("sys.dm_os_buffer_pool_extension_configuration", -817648642, 1)]
    [DataRow("sys.dm_os_cluster_nodes", -39192848, 0)]
    [DataRow("sys.dm_os_loaded_modules", -1035533931, 1)]
    [DataRow("sys.dm_os_memory_nodes", -46687631, 2)]
    [DataRow("sys.dm_os_windows_info", -39745214, 1)]
    [DataRow("sys.dm_xtp_transaction_stats", -995830047, 1)]
    [DataRow("sys.resource_governor_configuration", -253, 1)]
    [DataRow("sys.traces", -855650752, 1)]
    [TestMethod]
    public void HealthView_CarriesRealsIdAndRows(string view, int objectId, int rows)
    {
        var sim = new Simulation();
        AreEqual(objectId, sim.ExecuteScalar($"select object_id('{view}')"));
        AreEqual(rows, sim.ExecuteScalar($"select count(*) from {view}"));
    }

    [TestMethod]
    public void MemoryCaches_ThePlanStoresAreSizedAsRealsAndHoldThePlans()
    {
        var sim = new Simulation();
        AreEqual(40009, sim.ExecuteScalar("select buckets_count from sys.dm_os_memory_cache_hash_tables where type = 'CACHESTORE_SQLCP'"));
        AreEqual("SQL Plans", sim.ExecuteScalar("select name from sys.dm_os_memory_cache_counters where type = 'CACHESTORE_SQLCP'"));
        IsGreaterThan(0L, sim.ExecuteScalar<long>("select entries_count from sys.dm_os_memory_cache_counters where type = 'CACHESTORE_SQLCP'"));
    }

    [TestMethod]
    public void DefaultTrace_ListsWhileEnabledAndOpensWithItsHeader()
    {
        var sim = new Simulation();
        AreEqual("/var/opt/mssql/log/log_1.trc", sim.ExecuteScalar("select path from sys.traces where is_default = 1"));
        AreEqual("65528,65534", sim.ExecuteScalar("select string_agg(EventClass, ',') within group (order by EventSequence) from sys.fn_trace_gettable('/var/opt/mssql/log/log.trc', default)"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from ::fn_trace_gettable(N'/VAR/OPT/MSSQL/LOG/LOG_1.TRC', 1)"));
        AreEqual("GroupID", sim.ExecuteReader("select top 0 * from sys.fn_trace_gettable('/var/opt/mssql/log/log.trc', default)").GetName(65));
        _ = sim.ExecuteNonQuery("exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'default trace enabled', 0; reconfigure");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.traces"));
    }

    [TestMethod]
    public void TraceGetTable_RefusesAnotherFileAndAnEmptyName()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("select count(*) from sys.fn_trace_gettable('/var/opt/mssql/log/nosuch.trc', default)", 19049);
        _ = sim.AssertSqlError("select count(*) from sys.fn_trace_gettable(null, default)", 19050);
        _ = sim.AssertSqlError("select count(*) from sys.fn_trace_gettable('/var/opt/mssql/log/log.trc')", 313);
    }

    [TestMethod]
    public void Trace_ARestrictedLoginIsRefusedWithMsg8189()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        AreEqual(6, sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.traces", 8189).State);
        AreEqual(15, sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.fn_trace_gettable('/var/opt/mssql/log/log.trc', default)", 8189).State);
    }

    [TestMethod]
    public void LegacyPrefix_TakesOnlyTheOldSystemFunctions()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select count(*) from ::fn_virtualfilestats(1, 1)"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.databases d cross apply :: fn_virtualfilestats(d.database_id, 1) f where d.name = 'master'"));
        AreEqual(15, sim.AssertSqlError("select count(*) from ::fn_my_permissions(null, null)", 195).State);
        _ = sim.AssertSqlError("select count(*) from ::sys.fn_my_permissions(null, null)", 102);
    }

    [TestMethod]
    public void ObjectId_FindsTheModeledSystemFunctions()
    {
        var sim = new Simulation();
        AreEqual(-245973352, sim.ExecuteScalar("select object_id('sys.dm_exec_sql_text', 'IF')"));
        AreEqual(-245973352, sim.ExecuteScalar("select object_id('master.sys.dm_exec_sql_text')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('sys.dm_exec_sql_text', 'FN')"));
        AreEqual(-462384620, sim.ExecuteScalar("select object_id('fn_my_permissions')"));
        AreEqual(-462384620, sim.ExecuteScalar("select object_id('sys.fn_my_permissions', 'TF')"));
        AreEqual(-718311503, sim.ExecuteScalar("select object_id('sys.fn_trace_gettable', 'IF')"));
    }

    [TestMethod]
    public void MyPermissions_TwoNullsNameTheServer_AndAnApplyReachesIt()
    {
        var sim = new Simulation();
        AreEqual(
            sim.ExecuteScalar("select count(*) from fn_my_permissions(null, 'SERVER')"),
            sim.ExecuteScalar("select count(*) from fn_my_permissions(null, null)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from fn_my_permissions('x', null)"));
        AreEqual(
            sim.ExecuteScalar("select count(*) from fn_my_permissions(null, 'DATABASE')"),
            sim.ExecuteScalar("select count(fmp.permission_name) from sys.databases d cross apply fn_my_permissions(d.name, 'DATABASE') as fmp where d.name = 'master'"));
    }

    [TestMethod]
    public void ThreePartSystemFunction_ReadsTheNamedDatabase_ArgumentsTheSessions()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create database zz", "use zz; create table t (a int not null constraint pk primary key); insert t values (1), (2); update statistics t");
        AreEqual(1, sim.ExecuteScalar("use master; select count(*) from zz.sys.dm_db_stats_properties(object_id('zz.dbo.t'), 1)"));
        AreEqual(0, sim.ExecuteScalar("use master; select count(*) from zz.sys.dm_db_stats_properties(object_id('dbo.t'), 1)"));
        AreEqual(2L, sim.ExecuteScalar("""
            use master;
            select p.rows from zz.sys.stats s cross apply zz.sys.dm_db_stats_properties(s.object_id, s.stats_id) p where s.name = 'pk'
            """));
        _ = sim.AssertSqlError("select count(*) from nosuch.sys.dm_db_index_physical_stats(null, null, null, null, null)", 208);
    }

    [TestMethod]
    public void DbInfo_ReturnsTheBootPageOnlyAsTableResults()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database zz");
        const string Read = """
            create table #d (ParentObject nvarchar(255), Object nvarchar(255), Field nvarchar(255), Value nvarchar(255));
            insert #d exec ('dbcc dbinfo(''zz'') with tableresults, no_infomsgs');
            """;
        AreEqual(108, sim.ExecuteScalar(Read + "select count(*) from #d"));
        AreEqual("zz", sim.ExecuteScalar(Read + "select Value from #d where Field = 'dbi_dbname'"));
        AreEqual("1900-01-01 00:00:00.000", sim.ExecuteScalar(Read + "select Value from #d where Field = 'dbi_dbccLastKnownGood'"));
        _ = sim.ExecuteNonQuery("dbcc checkdb('zz') with physical_only, no_infomsgs");
        AreNotEqual("1900-01-01 00:00:00.000", sim.ExecuteScalar(Read + "select Value from #d where Field = 'dbi_dbccLastKnownGood'"));
        AreEqual(0, sim.ExecuteScalar("create table #e (a int); insert #e exec ('dbcc dbinfo(''zz'')'); select count(*) from #e"));
        _ = sim.AssertSqlError("dbcc dbinfo(1) with tableresults", 2560);
    }

    [TestMethod]
    public void Msdb_ShipsTheHistoryAndAgentTablesEmpty()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select count(*) from msdb.sys.objects where is_ms_shipped = 0"));
        AreEqual(0, sim.ExecuteScalar("""
            select count(*) from msdb.dbo.sysjobs j join msdb.dbo.sysjobschedules js on js.job_id = j.job_id
            join msdb.dbo.sysschedules s on s.schedule_id = js.schedule_id
            """));
        AreEqual(1, sim.ExecuteScalar("use msdb; select objectproperty(object_id('dbo.sysalerts'), 'IsMSShipped')"));
    }

    [TestMethod]
    public void AgentAlertInfo_IsAnUnconfiguredInstances()
    {
        var sim = new Simulation();
        using var reader = sim.ExecuteReader("exec master.dbo.sp_MSgetalertinfo @includeaddresses = 1");
        IsTrue(reader.Read());
        AreEqual(131, reader.GetInt32(3));
        IsTrue(reader.NextResult());
        AreEqual("AlertFailSafeEmailAddress", reader.GetName(0));
        _ = sim.AssertSqlError("exec sp_MSgetalertinfo @x = 1", 8145);
    }

    [TestMethod]
    public void ValidateLogins_RefusesArgumentsAndTheUnprivileged()
    {
        var sim = new Simulation();
        using (var reader = sim.ExecuteReader("exec sp_validatelogins"))
            AreEqual("NT Login", reader.GetName(1));
        _ = sim.AssertSqlError("exec sp_validatelogins 1", 8146);
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        _ = sim.AssertSqlError("execute as login = 'lo'; exec sp_validatelogins", 15247);
    }
}
