using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

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

    [TestMethod]
    public void ServerDmv_RestrictedWithoutPermission_Raises300VerbatimInMaster()
    {
        var ex = Seeded().AssertSqlError(
            "use master; execute as login = 'srvl2'; select count(*) from sys.dm_os_waiting_tasks", 300);
        AreEqual("VIEW SERVER PERFORMANCE STATE permission was denied on object 'server', database 'master'.", ex.Message);
    }

    [DataRow("sys.dm_tran_locks")]
    [DataRow("sys.dm_os_waiting_tasks")]
    [DataRow("sys.dm_tran_version_store")]
    [DataRow("sys.dm_tran_version_store_space_usage")]
    [DataRow("sys.dm_tran_active_snapshot_database_transactions")]
    [DataRow("sys.dm_hadr_cluster")]
    [TestMethod]
    public void ServerDmv_RestrictedWithoutPermission_Raises300(string view)
        => _ = Seeded().AssertSqlError($"execute as user = 'u_none'; select count(*) from {view}", 300);

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
        AreEqual("VIEW DATABASE PERFORMANCE STATE permission denied in database 'simulated'.", ex.Message);
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

    // ---- dbo / sysadmin bypass + ungated views ----

    [TestMethod]
    public void Dbo_ReadsGatedDmvsUnfiltered()
    {
        var sim = Seeded();
        using var idle = sim.CreateOpenConnection();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_locks"));
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
}
