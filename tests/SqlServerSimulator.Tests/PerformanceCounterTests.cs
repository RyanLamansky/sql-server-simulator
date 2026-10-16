using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sys.dm_os_performance_counters</c> and <c>sysperfinfo</c>: the
/// <c>SQLServer:</c> objects monitoring tools read, named, typed and ordered
/// as SQL Server 2025 lists them (probed 2026-10-16), their values following
/// the simulator's own state.
/// </summary>
[TestClass]
public sealed class PerformanceCounterTests
{
    private const string Counter = "select cntr_value from sys.dm_os_performance_counters where object_name = 'SQLServer:{0}' and counter_name = '{1}' and instance_name = '{2}'";

    private static long Read(Simulation sim, string objectName, string counter, string instance = "")
        => sim.ExecuteScalar<long>(string.Format(System.Globalization.CultureInfo.InvariantCulture, Counter, objectName, counter, instance));

    [TestMethod]
    public void Counters_CarryRealsShapeAndObjectIds()
    {
        var sim = new Simulation();
        AreEqual("-169782400,-209,1", sim.ExecuteScalar("""
            select concat_ws(',', object_id('sys.dm_os_performance_counters'), object_id('sys.sysperfinfo'),
                (select count(*) from sys.dm_os_performance_counters where object_name = 'SQLServer:SQL Statistics' and counter_name = 'Batch Requests/sec' and cntr_type = 272696576))
            """));
        // The names are nchar(128), padded as real pads them.
        AreEqual(256, sim.ExecuteScalar("select top 1 datalength(object_name) from sys.dm_os_performance_counters"));
        AreEqual(sim.ExecuteScalar("select count(*) from sys.dm_os_performance_counters"), sim.ExecuteScalar("select count(*) from sysperfinfo"));
    }

    [TestMethod]
    public void Databases_ListEachDatabaseThenTheTotal()
    {
        var sim = new Simulation();
        AreEqual(sim.ExecuteScalar<int>("select count(*) from sys.databases") + 1, sim.ExecuteScalar<int>(
            "select count(distinct instance_name) from sys.dm_os_performance_counters where object_name = 'SQLServer:Databases'"));
        AreEqual(sim.ExecuteScalar<long>("select sum(cast(size as bigint)) * 8 from sys.master_files where type = 1 and database_id = db_id()"), Read(sim, "Databases", "Log File(s) Size (KB)", "simulated"));
        AreEqual(sim.ExecuteScalar<long>("select sum(cast(cntr_value as bigint)) from sys.dm_os_performance_counters where object_name = 'SQLServer:Databases' and counter_name = 'Data File(s) Size (KB)' and instance_name <> '_Total'"),
            Read(sim, "Databases", "Data File(s) Size (KB)", "_Total"));
    }

    [TestMethod]
    public void BatchRequests_CountEachBatch()
    {
        var sim = new Simulation();
        var before = Read(sim, "SQL Statistics", "Batch Requests/sec");
        _ = sim.ExecuteNonQuery("select 1");
        AreEqual(before + 2, Read(sim, "SQL Statistics", "Batch Requests/sec"));
    }

    [TestMethod]
    public void Connections_CountLoginsLogoutsAndTheLiveSessions()
    {
        var sim = new Simulation();
        var logins = Read(sim, "General Statistics", "Logins/sec");
        var logouts = Read(sim, "General Statistics", "Logouts/sec");
        using (var other = sim.CreateOpenConnection())
            AreEqual(2L, Read(sim, "General Statistics", "User Connections"));
        // Each read logs in on a connection of its own, and out once it has read.
        AreEqual(logins + 4, Read(sim, "General Statistics", "Logins/sec"));
        AreEqual(logouts + 4, Read(sim, "General Statistics", "Logouts/sec"));
    }

    [TestMethod]
    public void Locks_CountRequestsWaitsAndTimeoutsPerResource()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var hold = holder.CreateCommand("begin tran; update t set a = 2");
        _ = hold.ExecuteNonQuery();
        using var waiter = sim.CreateOpenConnection();
        using var wait = waiter.CreateCommand("set lock_timeout 50; select * from t");
        _ = ThrowsExactly<SimulatedSqlException>(wait.ExecuteScalar);
        IsGreaterThan(0L, Read(sim, "Locks", "Lock Requests/sec", "_Total"));
        AreEqual(1L, Read(sim, "Locks", "Lock Waits/sec", "RID"));
        AreEqual(1L, Read(sim, "Locks", "Lock Timeouts/sec", "RID"));
        AreEqual(1L, Read(sim, "Locks", "Lock Timeouts (timeout > 0)/sec", "_Total"));
        AreEqual(Read(sim, "Locks", "Lock Wait Time (ms)", "_Total"), Read(sim, "Locks", "Average Wait Time (ms)", "_Total"));
        AreEqual(1L, Read(sim, "Wait Statistics", "Lock waits", "Waits started per second"));
        AreEqual(1L, Read(sim, "Transactions", "Transactions"));
        AreEqual(1L, Read(sim, "Databases", "Active Transactions", "simulated"));
    }

    [TestMethod]
    public void PlanCache_CountsTheCachedPlans()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); select a from t; select a from t");
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.dm_os_performance_counters
            where object_name = 'SQLServer:Plan Cache' and counter_name = 'Cache Object Counts' and instance_name = 'SQL Plans'
            and cntr_value = (select count(*) from sys.dm_exec_cached_plans where objtype in ('Adhoc', 'Prepared'))
            """));
        IsGreaterThan(0L, Read(sim, "SQL Statistics", "SQL Compilations/sec"));
    }

    [TestMethod]
    public void BufferManager_ReportsTheTablePages()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b char(2000)); insert t select top 40 1, 'x' from sys.all_objects");
        AreEqual(sim.ExecuteScalar<int>("select count(*) from sys.dm_os_buffer_descriptors"), (int)Read(sim, "Buffer Manager", "Database pages"));
    }

    [DataRow("sys.dm_os_performance_counters")]
    [DataRow("sysperfinfo")]
    [TestMethod]
    public void Counters_RestrictedWithoutPermission_Raise300(string view)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        _ = sim.AssertSqlError($"execute as login = 'lo'; select count(*) from {view}", 300);
    }
}
