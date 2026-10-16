using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The log, volume, database-transaction and server-service views monitoring
/// scripts read, in SQL Server 2025's shapes and object ids (probed
/// 2026-10-16). The log they describe is the one <c>DBCC LOGINFO</c> and
/// <c>DBCC SQLPERF(LOGSPACE)</c> report.
/// </summary>
[TestClass]
public sealed class LogAndServerDmvTests
{
    [TestMethod]
    public void LogSpaceUsage_IsTheCurrentDatabasesLogLessItsHeader()
        => AreEqual("1,8380416,442368", new Simulation().ExecuteScalar("""
            select concat(count(*), ',', min(total_log_size_in_bytes), ',', min(used_log_space_in_bytes))
            from sys.dm_db_log_space_usage
            where database_id = db_id() and abs(used_log_space_in_percent - 100.0 * used_log_space_in_bytes / total_log_size_in_bytes) < 0.001
            """));

    [TestMethod]
    public void LogInfo_CutsTheLogAsDbccLogInfo()
        => AreEqual("8192:1.93:1,2039808:1.93:0,4071424:1.93:0,6103040:2.17:0", new Simulation().ExecuteScalar("""
            select string_agg(concat(vlf_begin_offset, ':', vlf_size_mb, ':', cast(vlf_active as int)), ',') within group (order by vlf_begin_offset)
            from sys.dm_db_log_info(db_id())
            """));

    // A NULL or DEFAULT id reads the current database, an unknown one
    // nothing (probed 2026-10-16 against SQL Server 2025).
    [TestMethod]
    public void LogInfo_NullReadsTheCurrentDatabaseAndAnUnknownIdNothing()
        => AreEqual("4,4,0,1,0", new Simulation().ExecuteScalar("""
            select concat((select count(*) from sys.dm_db_log_info(null)), ',', (select count(*) from sys.dm_db_log_info(default)), ',',
                (select count(*) from sys.dm_db_log_info(999)), ',', (select count(*) from sys.dm_db_log_stats(null)), ',',
                (select count(*) from sys.dm_db_log_stats(999)))
            """));

    [TestMethod]
    public void LogInfo_WithoutAnArgument_Raises313()
        => new Simulation().AssertSqlError("select * from sys.dm_db_log_info()", 313,
            "An insufficient number of arguments were supplied for the procedure or function sys.dm_db_log_info.");

    [TestMethod]
    public void LogStats_SumsUpTheLogPerDatabase()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select count(*) from sys.databases d cross apply sys.dm_db_log_stats(d.database_id) s
            where d.database_id = db_id() and s.recovery_model = d.recovery_model_desc collate database_default
            and s.total_vlf_count = 4 and s.active_vlf_count = 1 and s.current_vlf_size_mb = 1.9375 and s.total_log_size_mb = 7.992187
            and s.log_truncation_holdup_reason = 'NOTHING' and s.log_state = 'LOG_NORMAL'
            """));

    // Glenn Berry's volume query applies the function to every file; a NULL
    // argument matches no file (probed 2026-10-16 against SQL Server 2025).
    [TestMethod]
    public void VolumeStats_AppliesPerFile()
        => AreEqual("2,0,0", new Simulation().ExecuteScalar("""
            select concat(
                (select count(*) from sys.master_files f cross apply sys.dm_os_volume_stats(f.database_id, f.file_id) v
                    where f.database_id = db_id() and v.total_bytes >= v.available_bytes and v.volume_mount_point is null),
                ',', (select count(*) from sys.dm_os_volume_stats(db_id(), null)), ',', (select count(*) from sys.dm_os_volume_stats(db_id(), 9)))
            """));

    // A transaction that has only begun lists nothing; one holding a lock in
    // a database lists it not yet begun, and one that has written there,
    // begun with its changes counted (probed 2026-10-16 against SQL Server
    // 2025).
    [TestMethod]
    public void DatabaseTransactions_FollowWhatTheTransactionDid()
    {
        using var connection = new Simulation().CreateOpenConnection();
        string Read(string sql)
        {
            using var command = connection.CreateCommand(sql);
            return command.ExecuteScalar().ToInvariantString();
        }
        _ = Read("create table t (a int); insert t values (1); begin tran");
        const string State = """
            select concat(count(*), ':', min(database_transaction_state), ':', min(database_transaction_log_record_count), ':', min(iif(database_transaction_begin_time is null, 0, 1)))
            from sys.dm_tran_database_transactions where transaction_id = current_transaction_id() and database_id = db_id()
            """;
        AreEqual("0:::", Read(State));
        _ = Read("select * from t with (holdlock)");
        AreEqual("1:3:0:0", Read(State));
        // The log records are the transaction's begin and one per row written
        // (probed 2026-10-16 against SQL Server 2025).
        _ = Read("update t set a = 2; insert t values (3)");
        AreEqual("1:4:3:1", Read(State));
        _ = Read("rollback");
        AreEqual("0:::", Read(State));
    }

    [TestMethod]
    public void ServerServices_ListsTheThreeServicesALinuxServerReports()
        => AreEqual("MSSQLSERVER:Running,SQL Full-text Filter Daemon Launcher (MSSQL):Running,SQL Server Agent (MSSQLSERVER):Stopped", new Simulation().ExecuteScalar(
            "select string_agg(concat(servicename, ':', status_desc), ',') within group (order by servicename) from sys.dm_server_services"));

    [TestMethod]
    public void EmptyViews_Resolve()
        => AreEqual(0, new Simulation().ExecuteScalar("select (select count(*) from sys.dm_server_registry) + (select count(*) from sys.dm_exec_query_profiles)"));

    // Either VIEW SERVER PERFORMANCE STATE or VIEW SERVER SECURITY STATE
    // opens the services; refused, real names each permission where the
    // database goes (probed 2026-10-16 against SQL Server 2025).
    [TestMethod]
    public void ServerServices_RefusedNamesBothPermissions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create login lo with password = 'x', check_policy = off; create user lo for login lo;
            create login sec with password = 'x', check_policy = off; create user sec for login sec;
            use master; grant view server security state to sec
            """);
        var ex = sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.dm_server_services", 300);
        AreEqual("server permission was denied on object 'server', database 'VIEW SERVER PERFORMANCE STATE'.", ex.Errors[0].Message);
        AreEqual("server permission was denied on object 'server', database 'VIEW SERVER SECURITY STATE'.", ex.Errors[1].Message);
        AreEqual(297, ex.Errors[2].Number);
        AreEqual(3, sim.ExecuteScalar("execute as login = 'sec'; select count(*) from sys.dm_server_services"));
    }

    [DataRow("sys.dm_db_log_space_usage")]
    [DataRow("sys.dm_tran_database_transactions")]
    [DataRow("sys.dm_exec_query_profiles")]
    [DataRow("sys.dm_server_registry")]
    [DataRow("sys.dm_db_log_info(db_id())")]
    [DataRow("sys.dm_db_log_stats(db_id())")]
    [DataRow("sys.dm_os_volume_stats(db_id(), 1)")]
    [TestMethod]
    public void ServerDmv_RestrictedWithoutPermission_Raises300(string source)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        var ex = sim.AssertSqlError($"execute as login = 'lo'; select count(*) from {source}", 300);
        AreEqual(297, ex.Errors[1].Number);
    }

    [TestMethod]
    public void ObjectIds_AreReals()
        => AreEqual("-879924562,-528723827,-305100784", new Simulation().ExecuteScalar(
            "select concat_ws(',', object_id('sys.dm_db_log_space_usage'), object_id('sys.dm_tran_database_transactions'), object_id('sys.dm_server_services'))"));
}
