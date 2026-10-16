using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SQLOS views monitoring scripts read: the wait statistics, the
/// schedulers and the tasks, workers and threads behind each request, the
/// NUMA nodes, the memory views and the buffer pool, in SQL Server 2025's
/// shapes and object ids (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class SqlOsDmvTests
{
    public TestContext TestContext { get; set; } = null!;

    // The session views read the session a command runs on, so these tests
    // keep theirs.
    private static object? Scalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return command.ExecuteScalar();
    }

    [TestMethod]
    public void WaitStats_ListsEveryOneOfRealsWaitTypes()
    {
        var sim = new Simulation();
        AreEqual(1547, sim.ExecuteScalar("select count(*) from sys.dm_os_wait_stats"));
        AreEqual("MISCELLANEOUS", sim.ExecuteScalar("select top 1 wait_type from sys.dm_os_wait_stats"));
        // Four of real's names end in a blank.
        AreEqual(4, sim.ExecuteScalar("select count(*) from sys.dm_os_wait_stats where datalength(wait_type) > 2 * len(wait_type)"));
        AreEqual(-783733354, sim.ExecuteScalar("select object_id('sys.dm_os_wait_stats')"));
    }

    [TestMethod]
    public void WaitFor_CountsAWaitForTheServerAndTheSession()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = Scalar(connection, "waitfor delay '00:00:00.050'");
        AreEqual("1,True", sim.ExecuteScalar("select concat(waiting_tasks_count, ',', iif(wait_time_ms >= 40 and max_wait_time_ms = wait_time_ms and signal_wait_time_ms = 0, 'True', 'False')) from sys.dm_os_wait_stats where wait_type = 'WAITFOR'"));
        AreEqual(1L, Scalar(connection, "select waiting_tasks_count from sys.dm_exec_session_wait_stats where session_id = @@spid and wait_type = 'WAITFOR'"));
    }

    // Clearing the server's wait statistics leaves each session's own
    // (probed 2026-10-16 against SQL Server 2025).
    [TestMethod]
    public void DbccSqlPerfClear_ForgetsTheServersWaitsNotTheSessions()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = Scalar(connection, "waitfor delay '00:00:00.010'; dbcc sqlperf('sys.dm_os_wait_stats', clear) with no_infomsgs");
        AreEqual(0L, Scalar(connection, "select waiting_tasks_count from sys.dm_os_wait_stats where wait_type = 'WAITFOR'"));
        AreEqual(1L, Scalar(connection, "select waiting_tasks_count from sys.dm_exec_session_wait_stats where session_id = @@spid and wait_type = 'WAITFOR'"));
    }

    [TestMethod]
    public void SessionWaitStats_ListsOnlyTheTypesASessionWaitedOn()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_session_wait_stats"));
    }

    [TestMethod]
    public void LockWait_CountsUnderItsLockMode()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var hold = holder.CreateCommand("begin tran; update t set a = 2");
        _ = hold.ExecuteNonQuery();
        using var waiter = sim.CreateOpenConnection();
        using var wait = waiter.CreateCommand("set lock_timeout 50; select * from t");
        _ = ThrowsExactly<SimulatedSqlException>(wait.ExecuteScalar);
        using var read = waiter.CreateCommand("select waiting_tasks_count from sys.dm_exec_session_wait_stats where session_id = @@spid and wait_type = 'LCK_M_S'");
        AreEqual(1L, read.ExecuteScalar());
        AreEqual(1L, sim.ExecuteScalar("select waiting_tasks_count from sys.dm_os_wait_stats where wait_type = 'LCK_M_S'"));
    }

    // A restricted session reads its own session's waits; VIEW SERVER
    // PERFORMANCE STATE opens the rest (probed 2026-10-16 against SQL Server
    // 2025).
    [TestMethod]
    public void SessionWaitStats_SelfFiltersForARestrictedSession()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo; create login perf with password = 'x', check_policy = off; create user perf for login perf; use master; grant view server performance state to perf");
        using var other = sim.CreateOpenConnection();
        using var otherWait = other.CreateCommand("waitfor delay '00:00:00.010'");
        _ = otherWait.ExecuteNonQuery();
        AreEqual(0, sim.ExecuteScalar("execute as login = 'lo'; select count(*) from sys.dm_exec_session_wait_stats"));
        AreEqual(1, sim.ExecuteScalar("execute as login = 'perf'; select count(*) from sys.dm_exec_session_wait_stats"));
    }

    [DataRow("sys.dm_os_schedulers")]
    [DataRow("sys.dm_os_tasks")]
    [DataRow("sys.dm_os_workers")]
    [DataRow("sys.dm_os_threads")]
    [DataRow("sys.dm_os_nodes")]
    [DataRow("sys.dm_os_memory_nodes")]
    [DataRow("sys.dm_os_sys_memory")]
    [DataRow("sys.dm_os_memory_clerks")]
    [DataRow("sys.dm_os_ring_buffers")]
    [DataRow("sys.dm_os_buffer_descriptors")]
    [DataRow("sys.dm_os_wait_stats")]
    [DataRow("sys.dm_os_latch_stats")]
    [TestMethod]
    public void ServerDmv_RestrictedWithoutPermission_Raises300(string view)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        var ex = sim.AssertSqlError($"execute as login = 'lo'; select count(*) from {view}", 300);
        AreEqual(297, ex.Errors[1].Number);
    }

    [TestMethod]
    public void Schedulers_OneVisiblePerCpuThenTheHiddenAndTheAdminConnections()
    {
        var sim = new Simulation();
        AreEqual(Environment.ProcessorCount, sim.ExecuteScalar("select count(*) from sys.dm_os_schedulers where status = 'VISIBLE ONLINE'"));
        AreEqual(sim.ExecuteScalar("select scheduler_total_count from sys.dm_os_sys_info"), sim.ExecuteScalar("select count(*) from sys.dm_os_schedulers"));
        AreEqual(64, sim.ExecuteScalar("select parent_node_id from sys.dm_os_schedulers where scheduler_id = 1048576 and status = 'VISIBLE ONLINE (DAC)'"));
        AreEqual(1, sim.ExecuteScalar("select sum(current_tasks_count) from sys.dm_os_schedulers"));
    }

    // A request's task, worker and thread join as real's do, and the task
    // sits on the scheduler sys.dm_exec_requests reports.
    [TestMethod]
    public void Tasks_JoinTheirRequestsWorkersAndThreads()
        => AreEqual("RUNNING,RUNNING", new Simulation().ExecuteScalar("""
            select concat(t.task_state, ',', w.state)
            from sys.dm_exec_requests r
            join sys.dm_os_tasks t on t.task_address = r.task_address and t.scheduler_id = r.scheduler_id
            join sys.dm_os_workers w on w.worker_address = t.worker_address and w.task_address = t.task_address
            join sys.dm_os_threads th on th.thread_address = w.thread_address and th.worker_address = w.worker_address
            join sys.dm_os_schedulers s on s.scheduler_address = w.scheduler_address and s.scheduler_id = t.scheduler_id
            where r.session_id = @@spid
            """));

    [TestMethod]
    public void Nodes_TheSchedulersNodeAndTheAdminConnections()
        => AreEqual("0:ONLINE,64:ONLINE DAC", new Simulation().ExecuteScalar(
            "select string_agg(concat(node_id, ':', node_state_desc), ',') within group (order by node_id) from sys.dm_os_nodes"));

    [TestMethod]
    public void SysMemory_ReportsThePhysicalMemorySysInfoDoes()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select count(*) from sys.dm_os_sys_memory m cross join sys.dm_os_sys_info i
            where m.total_physical_memory_kb = i.physical_memory_kb and m.available_physical_memory_kb <= m.total_physical_memory_kb
            and m.system_high_memory_signal_state ^ m.system_low_memory_signal_state = 1
            """));

    // The buffer pool's clerk and descriptors hold every page in use of the
    // database's tables, under the allocation units that report them.
    [TestMethod]
    public void BufferPool_HoldsEveryTablePage()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b char(2000)); insert t select top 40 1, 'x' from sys.all_objects");
        var pages = sim.ExecuteScalar<int>("select cast(sum(used_pages) as int) from sys.allocation_units");
        AreEqual("IAM_PAGE:1 DATA_PAGE:10", sim.ExecuteScalar("""
            select string_agg(concat(page_type, ':', n), ' ') within group (order by page_type desc)
            from (select page_type, count(*) n from sys.dm_os_buffer_descriptors where database_id = db_id() group by page_type) d
            """));
        AreEqual(pages, sim.ExecuteScalar<int>("select count(*) from sys.dm_os_buffer_descriptors where database_id = db_id()"));
        AreEqual(sim.ExecuteScalar<int>("select count(*) from sys.dm_os_buffer_descriptors where database_id = db_id()"), sim.ExecuteScalar<int>("""
            select count(*) from sys.dm_os_buffer_descriptors b
            join sys.allocation_units a on a.allocation_unit_id = b.allocation_unit_id
            join sys.partitions p on p.hobt_id = a.container_id
            where b.database_id = db_id() and p.object_id = object_id('t')
            """));
        AreEqual(sim.ExecuteScalar<int>("select count(*) * 8 from sys.dm_os_buffer_descriptors"),
            (int)sim.ExecuteScalar<long>("select pages_kb from sys.dm_os_memory_clerks where type = 'MEMORYCLERK_SQLBUFFERPOOL'"));
    }

    [TestMethod]
    public void MemoryClerks_PlanCacheStoresSumTheCachedPlans()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); select a from t");
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.dm_os_memory_clerks
            where type = 'CACHESTORE_SQLCP' and pages_kb > 0
            and pages_kb = (select sum(size_in_bytes) / 1024 from sys.dm_exec_cached_plans where objtype in ('Adhoc', 'Prepared'))
            """));
        AreEqual(118, sim.ExecuteScalar("select count(distinct type) from sys.dm_os_memory_clerks"));
    }

    [TestMethod]
    public void LatchStats_ListsRealsClassesWithoutWaits()
        => AreEqual("183,0", new Simulation().ExecuteScalar("select concat(count(*), ',', sum(waiting_requests_count)) from sys.dm_os_latch_stats"));

    [TestMethod]
    public void RingBuffers_AreEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from sys.dm_os_ring_buffers"));
}
