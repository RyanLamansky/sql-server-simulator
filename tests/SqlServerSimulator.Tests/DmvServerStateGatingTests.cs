using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.Extensions;

namespace SqlServerSimulator;

/// <summary>
/// Server-state permission gating on the modeled DMVs (probe-confirmed against
/// SQL Server 2025, 2026-07-21). A restricted session reading a server-scope DMV
/// without <c>VIEW SERVER PERFORMANCE STATE</c> (covered by <c>VIEW SERVER
/// STATE</c>) raises Msg 300; a database-scope DMV without <c>VIEW DATABASE
/// PERFORMANCE STATE</c> (covered by <c>VIEW DATABASE STATE</c> at database scope
/// or a server permission cross-scope) raises Msg 262;
/// <c>sys.dm_exec_sessions</c> self-filters to the own session without <c>VIEW
/// SERVER STATE</c>; <c>sys.dm_os_host_info</c> / <c>sys.fn_helpcollations</c> /
/// <c>sys.dm_db_xtp_table_memory_stats</c> stay public. dbo / sysadmin bypass.
/// A restricted principal is established via <c>EXECUTE AS LOGIN</c> (login
/// identity, for server-scope grants) or <c>EXECUTE AS USER</c> (database user,
/// for database-scope grants and the login-less denial path).
/// </summary>
[TestClass]
public sealed class DmvServerStateGatingTests
{
    public TestContext TestContext { get; set; } = null!;

    // srvl: a login granted VIEW SERVER STATE (server scope, in master).
    // srvl2: a login with no server-state grant. u_dbstate: a database user
    // granted VIEW DATABASE STATE. u_none: a database user with no grant.
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create login srvl with password = 'P@ss1word'",
            "create login srvl2 with password = 'P@ss1word'",
            "use master",
            "grant view server state to srvl",
            "use simulated",
            "create user u_dbstate without login",
            "grant view database state to u_dbstate",
            "create user u_none without login");
        return sim;
    }

    // ---- Server-scope DMVs → Msg 300 ----

    // Msg 300 names master, where server permissions live, and Msg 297
    // follows it (probed 2026-10-15 against SQL Server 2025).
    [TestMethod]
    public void ServerDmv_RestrictedWithoutPermission_Raises300NamingMasterThen297()
    {
        var ex = Seeded().AssertSqlError(
            "use master; execute as login = 'srvl2'; select count(*) from sys.dm_os_waiting_tasks", 300);
        AreEqual("VIEW SERVER PERFORMANCE STATE permission was denied on object 'server', database 'master'.", ex.Errors[0].Message);
        AreEqual(297, ex.Errors[1].Number);
    }

    // sys.dm_exec_connections takes the same permission but refuses with Msg
    // 371 (probed 2026-09-25 against SQL Server 2025).
    [TestMethod]
    public void Connections_RestrictedWithoutPermission_Raises371()
        => Seeded().AssertSqlError(
            "use master; execute as login = 'srvl2'; select count(*) from sys.dm_exec_connections", 371,
            "The user does not have the external policy action 'Microsoft.Sql/Sqlservers/SystemViewsAndFunctions/ServerPerformanceState/Rows/Select' or permission 'VIEW SERVER PERFORMANCE STATE' to perform this action.");

    [TestMethod]
    public void SqlText_RestrictedWithoutPermission_Raises371()
        => _ = Seeded().AssertSqlError("execute as user = 'u_none'; select count(*) from sys.dm_exec_sql_text(null)", 371);

    [TestMethod]
    public void InputBuffer_RestrictedWithoutPermission_Raises300EvenForItsOwnSession()
        => _ = Seeded().AssertSqlError("use master; execute as login = 'srvl2'; select count(*) from sys.dm_exec_input_buffer(@@spid, 0)", 300);

    [TestMethod]
    public void InputBuffer_UnderExecuteAsUser_IsNotTrusted()
        => _ = Seeded().AssertSqlError("execute as user = 'u_none'; select count(*) from sys.dm_exec_input_buffer(@@spid, 0)", 15562);

    [TestMethod]
    public void DbccInputBuffer_RestrictedReadsItsOwnSessionOnly()
    {
        var sim = Seeded();
        using var other = sim.CreateOpenConnection();
        AreEqual("Language Event", sim.ExecuteScalar("execute as user = 'u_none'; dbcc inputbuffer(@@spid) with no_infomsgs"));
        sim.AssertSqlError("execute as user = 'u_none'; dbcc inputbuffer(52)", 2571, "User 'u_none' does not have permission to run DBCC inputbuffer.");
    }

    [TestMethod]
    public void Connections_ServerStateGrant_Reads()
        => AreEqual(1, Seeded().ExecuteScalar("use master; execute as login = 'srvl'; select count(*) from sys.dm_exec_connections"));

    [DataRow("sys.dm_tran_locks")]
    [DataRow("sys.dm_os_waiting_tasks")]
    [DataRow("sys.dm_os_sys_info")]
    [DataRow("sys.dm_tran_active_transactions")]
    [DataRow("sys.dm_tran_session_transactions")]
    [DataRow("sys.dm_tran_current_transaction")]
    [DataRow("sys.dm_tran_version_store")]
    [DataRow("sys.dm_tran_version_store_space_usage")]
    [DataRow("sys.dm_tran_active_snapshot_database_transactions")]
    [DataRow("sys.dm_hadr_cluster")]
    [TestMethod]
    public void ServerDmv_RestrictedWithoutPermission_Raises300(string view)
        => _ = Seeded().AssertSqlError($"use master; execute as login = 'srvl2'; select count(*) from {view}", 300);

    // An EXECUTE AS USER context reaching for server state is refused as
    // untrusted before any permission is asked, unless the database is
    // TRUSTWORTHY (probed 2026-10-15 against SQL Server 2025).
    [DataRow("sys.dm_tran_locks")]
    [DataRow("sys.dm_db_session_space_usage")]
    [DataRow("sys.dm_exec_query_memory_grants")]
    [TestMethod]
    public void ServerDmv_UnderExecuteAsUser_IsNotTrusted(string view)
        => Seeded().AssertSqlError($"execute as user = 'u_none'; select count(*) from {view}", 15562,
            "The module being executed is not trusted. Either the owner of the database of the module needs to be granted authenticate permission, or the module needs to be digitally signed.");

    [TestMethod]
    public void ServerDmv_UnderExecuteAsUserInATrustworthyDatabase_AsksThePermission()
        => _ = Seeded().AssertSqlError("alter database simulated set trustworthy on; execute as user = 'u_none'; select count(*) from sys.dm_tran_locks", 300);

    [TestMethod]
    public void ServerDmv_WithViewServerState_Opens()
        => AreEqual(0, Seeded().ExecuteScalar(
            "use master; execute as login = 'srvl'; select count(*) from sys.dm_tran_locks"));

    [TestMethod]
    public void ServerDmv_WithGranularViewServerPerformanceState_Opens()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("use master; grant view server performance state to srvl2");
        AreEqual(0, sim.ExecuteScalar(
            "use master; execute as login = 'srvl2'; select count(*) from sys.dm_os_waiting_tasks"));
    }

    [TestMethod]
    public void ServerDmv_ServerDeny_OverridesGrant_Raises300()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("use master; deny view server state to srvl");
        _ = sim.AssertSqlError("use master; execute as login = 'srvl'; select count(*) from sys.dm_tran_locks", 300);
    }

    // ---- Database-scope DMVs → Msg 262 ----

    [TestMethod]
    public void DatabaseDmv_RestrictedWithoutPermission_Raises262Verbatim()
    {
        var ex = Seeded().AssertSqlError(
            "execute as user = 'u_none'; select count(*) from sys.dm_db_partition_stats", 262);
        AreEqual("VIEW DATABASE PERFORMANCE STATE permission denied in database 'simulated'.", ex.Errors[0].Message);
        AreEqual(297, ex.Errors[1].Number);
    }

    [TestMethod]
    public void DatabaseDmv_HadrReplicaStates_RestrictedWithoutPermission_Raises262()
        => _ = Seeded().AssertSqlError(
            "execute as user = 'u_none'; select count(*) from sys.dm_hadr_database_replica_states", 262);

    [TestMethod]
    public void DatabaseDmv_WithViewDatabaseState_Opens()
        => IsGreaterThanOrEqualTo(0, Convert.ToInt32(Seeded().ExecuteScalar(
            "execute as user = 'u_dbstate'; select count(*) from sys.dm_db_partition_stats")));

    [TestMethod]
    public void DatabaseDmv_WithServerViewServerState_OpensCrossScope()
        => IsGreaterThanOrEqualTo(0, Convert.ToInt32(Seeded().ExecuteScalar(
            "use master; execute as login = 'srvl'; select count(*) from sys.dm_db_partition_stats")));

    // ---- sys.dm_exec_sessions self-filter ----

    [TestMethod]
    public void ExecSessions_RestrictedWithoutPermission_SelfFiltersToOwnSession()
    {
        var sim = Seeded();
        using var idle = sim.CreateOpenConnection();
        // Two live connections (idle + the querying one); a restricted session
        // sees only its own row.
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u_none'; select count(*) from sys.dm_exec_sessions"));
    }

    [TestMethod]
    public void ExecSessions_WithViewServerState_SeesAllSessions()
    {
        var sim = Seeded();
        using var idle = sim.CreateOpenConnection();
        IsGreaterThanOrEqualTo(2, Convert.ToInt32(sim.ExecuteScalar(
            "use master; execute as login = 'srvl'; select count(*) from sys.dm_exec_sessions")));
    }

    // sys.dm_exec_requests filters the same way (probed 2026-09-25): a
    // sleeping session's request is visible only with the permission.
    [TestMethod]
    public async Task ExecRequests_SelfFilterLikeExecSessions()
    {
        var sim = Seeded();
        using var sleeper = sim.CreateOpenConnection();
        // Long enough to outlast a starved poll, and cancelled once the
        // assertions have seen it: a short delay could finish before the poll
        // first ran, leaving it to wait for a request that was already gone.
        using var sleep = sleeper.CreateCommand("waitfor delay '00:00:30'");
        var sleepTask = Task.Run(sleep.ExecuteNonQuery, TestContext.CancellationToken);
        _ = await PollUntil(
            () => (int)sim.ExecuteScalar("select count(*) from sys.dm_exec_requests where command = 'WAITFOR'")!,
            count => count == 1,
            TestContext.CancellationToken);
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u_none'; select count(*) from sys.dm_exec_requests"));
        AreEqual(2, sim.ExecuteScalar("use master; execute as login = 'srvl'; select count(*) from sys.dm_exec_requests"));
        sleep.Cancel();
        _ = await ThrowsAsync<SimulatedSqlException>(() => sleepTask);
    }

    // ---- dbo / sysadmin bypass + ungated views ----

    [TestMethod]
    public void Dbo_ReadsGatedDmvsUnfiltered()
    {
        var sim = Seeded();
        using var idle = sim.CreateOpenConnection();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_locks where resource_type <> 'DATABASE'"));
        IsGreaterThanOrEqualTo(2, Convert.ToInt32(sim.ExecuteScalar("select count(*) from sys.dm_exec_sessions")));
    }

    [TestMethod]
    public void UngatedDmvs_ReadableByRestrictedPrincipal()
    {
        var sim = Seeded();
        IsGreaterThanOrEqualTo(1, Convert.ToInt32(sim.ExecuteScalar(
            "execute as user = 'u_none'; select count(*) from sys.dm_os_host_info")));
        IsGreaterThan(0, Convert.ToInt32(sim.ExecuteScalar(
            "execute as user = 'u_none'; select count(*) from sys.fn_helpcollations()")));
        AreEqual(0, sim.ExecuteScalar(
            "execute as user = 'u_none'; select count(*) from sys.dm_db_xtp_table_memory_stats"));
    }

    [TestMethod]
    public void ServerExtras_ReadAsRealDoes()
    {
        var sim = Seeded();
        AreEqual("35|DISABLE_OPTIMIZED_NESTED_LOOP|ASSUME_FIXED_MAX_SELECTIVITY_FOR_REGEXP", sim.ExecuteScalar("""
            select concat_ws('|', count(*), min(case when n = 1 then name end), min(case when n = 35 then name end))
            from (select name, row_number() over (order by (select 1)) n from sys.dm_exec_valid_use_hints) h
            """));
        AreEqual("2|0|0|0|0", sim.ExecuteScalar("""
            select concat_ws('|', database_id, user_objects_alloc_page_count, (select count(*) from sys.dm_exec_query_memory_grants),
                (select count(*) from sys.master_key_passwords) + (select count(*) from sys.backup_devices) + (select count(*) from sys.extended_procedures),
                (select request_id from sys.dm_db_task_space_usage where session_id = @@spid))
            from sys.dm_db_session_space_usage where session_id = @@spid
            """));
        _ = sim.ExecuteNonQuery("create credential c with identity = 'i'; alter login srvl with credential = c");
        AreEqual("srvl", sim.ExecuteScalar("select p.name from sys.server_principal_credentials spc join sys.server_principals p on p.principal_id = spc.principal_id"));
    }
}
