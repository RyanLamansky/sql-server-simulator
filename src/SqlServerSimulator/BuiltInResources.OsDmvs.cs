using System.Buffers.Binary;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The SQLOS views monitoring scripts read — schedulers, tasks, workers,
// threads, NUMA nodes, memory, the buffer pool and the wait statistics — each
// in SQL Server 2025's column shape (probed 2026-10-16). Rows follow the
// simulator's own state where it has that state (its sessions' requests, its
// tables' pages, its plan cache, the waits its statements finish); the rest
// are documented stand-ins, see docs/claude/catalog-views.md#sqlos-views.
internal static partial class BuiltInResources
{
    private static void RegisterOsDmvs(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        HeapColumn[] schedulers =
        [
            new("scheduler_address", SqlType.Varbinary, 8, false),
            new("parent_node_id", SqlType.Int32, null, false),
            new("scheduler_id", SqlType.Int32, null, false),
            new("cpu_id", SqlType.Int32, null, false),
            new("status", SqlType.NVarchar, 60, false),
            new("is_online", SqlType.Bit, null, false),
            new("is_idle", SqlType.Bit, null, false),
            new("preemptive_switches_count", SqlType.Int32, null, false),
            new("context_switches_count", SqlType.Int32, null, false),
            new("idle_switches_count", SqlType.Int32, null, false),
            new("current_tasks_count", SqlType.Int32, null, false),
            new("runnable_tasks_count", SqlType.Int32, null, false),
            new("current_workers_count", SqlType.Int32, null, false),
            new("active_workers_count", SqlType.Int32, null, false),
            new("work_queue_count", SqlType.BigInt, null, false),
            new("pending_disk_io_count", SqlType.Int32, null, false),
            new("queued_disk_io_count", SqlType.Int32, null, false),
            new("load_factor", SqlType.Int32, null, false),
            new("yield_count", SqlType.Int32, null, false),
            new("last_timer_activity", SqlType.BigInt, null, false),
            new("failed_to_create_worker", SqlType.Bit, null, false),
            new("active_worker_address", SqlType.Varbinary, 8, true),
            new("memory_object_address", SqlType.Varbinary, 8, false),
            new("task_memory_object_address", SqlType.Varbinary, 8, false),
            new("quantum_length_us", SqlType.BigInt, null, false),
            new("total_cpu_usage_ms", SqlType.BigInt, null, false),
            new("total_cpu_idle_capped_ms", SqlType.BigInt, null, true),
            new("total_scheduler_delay_ms", SqlType.BigInt, null, false),
            new("ideal_workers_limit", SqlType.Int32, null, false),
            new("total_signal_time", SqlType.BigInt, null, false),
            new("total_waits_completed", SqlType.BigInt, null, false),
            new("total_enqueued_tasks", SqlType.Int32, null, false),
            new("total_completed_tasks", SqlType.Int32, null, false),
            new("spinlock_wait_time_ms", SqlType.BigInt, null, false),
            new("spinlock_max_wait_time_ms", SqlType.BigInt, null, false),
            new("spinlock_wait_count", SqlType.BigInt, null, false),
        ];
        Sys("dm_os_schedulers", schedulers, (batch, database) => SchedulerRows(batch, database, schedulers));

        HeapColumn[] tasks =
        [
            new("task_address", SqlType.Varbinary, 8, false),
            new("task_state", SqlType.NVarchar, 60, true),
            new("context_switches_count", SqlType.Int32, null, true),
            new("pending_io_count", SqlType.Int32, null, true),
            new("pending_io_byte_count", SqlType.BigInt, null, true),
            new("pending_io_byte_average", SqlType.Int32, null, true),
            new("scheduler_id", SqlType.Int32, null, false),
            new("session_id", SqlType.SmallInt, null, true),
            new("exec_context_id", SqlType.Int32, null, true),
            new("request_id", SqlType.Int32, null, true),
            new("worker_address", SqlType.Varbinary, 8, true),
            new("host_address", SqlType.Varbinary, 8, false),
            new("parent_task_address", SqlType.Varbinary, 8, true),
            new("task_local_storage", SqlType.NVarchar, 3072, true),
        ];
        Sys("dm_os_tasks", tasks, (batch, database) => RequestTasks(batch, database).Select(task => StandInRow(tasks,
        [
            ("task_address", SqlValue.FromVarbinary(TaskAddress(task.Spid, task.RequestId))),
            ("task_state", SqlValue.FromNVarchar(task.State)),
            ("scheduler_id", SqlValue.FromInt32(SchedulerOf(task.Spid))),
            ("session_id", SqlValue.FromInt16(task.Spid)),
            ("request_id", SqlValue.FromInt32(task.RequestId)),
            ("worker_address", SqlValue.FromVarbinary(WorkerAddress(task.Spid, task.RequestId))),
        ])));

        // One row per request waiting — on a lock, in a WAITFOR, or on its
        // client to read what it sent — as dm_exec_requests reports it (the
        // simulator has no system tasks, whose waits real lists beside them).
        Sys("dm_os_waiting_tasks",
        [
            new("waiting_task_address", SqlType.Varbinary, 8, false),
            new("session_id", SqlType.SmallInt, null, true),
            new("exec_context_id", SqlType.Int32, null, true),
            new("wait_duration_ms", SqlType.BigInt, null, true),
            new("wait_type", SqlType.NVarchar, 60, true),
            new("resource_address", SqlType.Varbinary, 8, true),
            new("blocking_task_address", SqlType.Varbinary, 8, true),
            new("blocking_session_id", SqlType.SmallInt, null, true),
            new("blocking_exec_context_id", SqlType.Int32, null, true),
            new("resource_description", SqlType.NVarchar, 3072, true),
        ], WaitingTaskRows);

        HeapColumn[] workers =
        [
            new("worker_address", SqlType.Varbinary, 8, false),
            new("status", SqlType.Int32, null, false),
            new("is_preemptive", SqlType.Bit, null, true),
            new("is_fiber", SqlType.Bit, null, true),
            new("is_sick", SqlType.Bit, null, true),
            new("is_in_cc_exception", SqlType.Bit, null, true),
            new("is_fatal_exception", SqlType.Bit, null, true),
            new("is_inside_catch", SqlType.Bit, null, true),
            new("is_in_polling_io_completion_routine", SqlType.Bit, null, true),
            new("context_switch_count", SqlType.Int32, null, false),
            new("pending_io_count", SqlType.Int32, null, false),
            new("pending_io_byte_count", SqlType.BigInt, null, false),
            new("pending_io_byte_average", SqlType.Int32, null, false),
            new("wait_started_ms_ticks", SqlType.BigInt, null, false),
            new("wait_resumed_ms_ticks", SqlType.BigInt, null, false),
            new("task_bound_ms_ticks", SqlType.BigInt, null, false),
            new("worker_created_ms_ticks", SqlType.BigInt, null, false),
            new("exception_num", SqlType.Int32, null, false),
            new("exception_severity", SqlType.Int32, null, false),
            new("exception_address", SqlType.Varbinary, 8, true),
            new("affinity", SqlType.BigInt, null, false),
            new("state", SqlType.NVarchar, 60, true),
            new("start_quantum", SqlType.BigInt, null, false),
            new("end_quantum", SqlType.BigInt, null, false),
            new("last_wait_type", SqlType.NVarchar, 60, true),
            new("return_code", SqlType.Int32, null, false),
            new("quantum_used", SqlType.BigInt, null, false),
            new("max_quantum", SqlType.BigInt, null, false),
            new("boost_count", SqlType.Int32, null, false),
            new("tasks_processed_count", SqlType.Int32, null, false),
            new("fiber_address", SqlType.Varbinary, 8, true),
            new("task_address", SqlType.Varbinary, 8, true),
            new("memory_object_address", SqlType.Varbinary, 8, false),
            new("thread_address", SqlType.Varbinary, 8, true),
            new("signal_worker_address", SqlType.Varbinary, 8, true),
            new("scheduler_address", SqlType.Varbinary, 8, true),
            new("processor_group", SqlType.SmallInt, null, false),
            new("worker_migration_count", SqlType.Int32, null, false),
            new("spinlock_wait_time_ms", SqlType.BigInt, null, false),
            new("spinlock_max_wait_time_ms", SqlType.BigInt, null, false),
            new("spinlock_wait_count", SqlType.BigInt, null, false),
            new("cpu_used", SqlType.BigInt, null, false),
        ];
        Sys("dm_os_workers", workers, (batch, database) => RequestTasks(batch, database).Select(task => StandInRow(workers,
        [
            ("worker_address", SqlValue.FromVarbinary(WorkerAddress(task.Spid, task.RequestId))),
            ("is_preemptive", SqlValue.FromBoolean(false)),
            ("is_fiber", SqlValue.FromBoolean(false)),
            ("is_sick", SqlValue.FromBoolean(false)),
            ("is_in_cc_exception", SqlValue.FromBoolean(false)),
            ("is_fatal_exception", SqlValue.FromBoolean(false)),
            ("is_inside_catch", SqlValue.FromBoolean(false)),
            ("is_in_polling_io_completion_routine", SqlValue.FromBoolean(false)),
            ("affinity", SqlValue.FromInt64(SchedulerMask)),
            ("state", SqlValue.FromNVarchar(task.State)),
            ("last_wait_type", SqlValue.FromNVarchar(task.WaitType ?? "MISCELLANEOUS")),
            ("task_address", SqlValue.FromVarbinary(TaskAddress(task.Spid, task.RequestId))),
            ("memory_object_address", SqlValue.FromVarbinary(StandInAddress(0x07, task.Spid, task.RequestId))),
            ("thread_address", SqlValue.FromVarbinary(ThreadAddress(task.Spid, task.RequestId))),
            ("scheduler_address", SqlValue.FromVarbinary(SchedulerAddress(SchedulerOf(task.Spid)))),
        ])));

        HeapColumn[] threads =
        [
            new("thread_address", SqlType.Varbinary, 8, false),
            new("started_by_sqlservr", SqlType.Bit, null, false),
            new("os_thread_id", SqlType.Int32, null, false),
            new("status", SqlType.Int32, null, false),
            new("instruction_address", SqlType.Varbinary, 8, true),
            new("creation_time", SqlType.DateTime, null, true),
            new("kernel_time", SqlType.BigInt, null, true),
            new("usermode_time", SqlType.BigInt, null, true),
            new("stack_base_address", SqlType.Varbinary, 8, false),
            new("stack_end_address", SqlType.Varbinary, 8, true),
            new("stack_bytes_committed", SqlType.Int32, null, false),
            new("stack_bytes_used", SqlType.Int32, null, true),
            new("affinity", SqlType.BigInt, null, false),
            new("priority", SqlType.Int32, null, true),
            new("locale", SqlType.Int32, null, true),
            new("token", SqlType.Varbinary, 8, true),
            new("is_impersonating", SqlType.Int32, null, true),
            new("is_waiting_on_loader_lock", SqlType.Int32, null, true),
            new("fiber_data", SqlType.Varbinary, 8, true),
            new("thread_handle", SqlType.Varbinary, 8, true),
            new("event_handle", SqlType.Varbinary, 8, true),
            new("scheduler_address", SqlType.Varbinary, 8, true),
            new("worker_address", SqlType.Varbinary, 8, true),
            new("fiber_context_address", SqlType.Varbinary, 8, true),
            new("self_address", SqlType.Varbinary, 8, true),
            new("processor_group", SqlType.SmallInt, null, false),
            new("description", SqlType.NVarchar, 256, true),
        ];
        Sys("dm_os_threads", threads, (batch, database) => RequestTasks(batch, database).Select(task => StandInRow(threads,
        [
            ("thread_address", SqlValue.FromVarbinary(ThreadAddress(task.Spid, task.RequestId))),
            ("started_by_sqlservr", SqlValue.FromBoolean(true)),
            ("os_thread_id", SqlValue.FromInt32(task.ThreadId)),
            ("creation_time", SqlValue.FromDateTime(task.LoginTimeUtc)),
            ("stack_base_address", SqlValue.FromVarbinary(StandInAddress(0x08, task.Spid, task.RequestId))),
            ("affinity", SqlValue.FromInt64(SchedulerMask)),
            ("scheduler_address", SqlValue.FromVarbinary(SchedulerAddress(SchedulerOf(task.Spid)))),
            ("worker_address", SqlValue.FromVarbinary(WorkerAddress(task.Spid, task.RequestId))),
            ("self_address", SqlValue.FromVarbinary(ThreadAddress(task.Spid, task.RequestId))),
        ])));

        HeapColumn[] nodes =
        [
            new("node_id", SqlType.SmallInt, null, false),
            new("node_state_desc", SqlType.NVarchar, 256, false),
            new("memory_object_address", SqlType.Varbinary, 8, false),
            new("memory_clerk_address", SqlType.Varbinary, 8, false),
            new("io_completion_worker_address", SqlType.Varbinary, 8, true),
            new("memory_node_id", SqlType.SmallInt, null, false),
            new("cpu_affinity_mask", SqlType.BigInt, null, false),
            new("online_scheduler_count", SqlType.SmallInt, null, false),
            new("idle_scheduler_count", SqlType.SmallInt, null, false),
            new("active_worker_count", SqlType.Int32, null, false),
            new("avg_load_balance", SqlType.Int32, null, false),
            new("timer_task_affinity_mask", SqlType.BigInt, null, false),
            new("permanent_task_affinity_mask", SqlType.BigInt, null, false),
            new("resource_monitor_state", SqlType.Bit, null, false),
            new("online_scheduler_mask", SqlType.BigInt, null, false),
            new("processor_group", SqlType.SmallInt, null, false),
            new("cpu_count", SqlType.Int32, null, false),
            new("cached_tasks", SqlType.BigInt, null, false),
            new("cached_tasks_reused", SqlType.BigInt, null, false),
            new("cached_tasks_removed", SqlType.BigInt, null, false),
        ];
        Sys("dm_os_nodes", nodes, (batch, database) => NodeRows(batch, database, nodes));

        HeapColumn[] memoryNodes =
        [
            new("memory_node_id", SqlType.SmallInt, null, false),
            new("virtual_address_space_reserved_kb", SqlType.BigInt, null, false),
            new("virtual_address_space_committed_kb", SqlType.BigInt, null, false),
            new("locked_page_allocations_kb", SqlType.BigInt, null, false),
            new("pages_kb", SqlType.BigInt, null, false),
            new("shared_memory_reserved_kb", SqlType.BigInt, null, false),
            new("shared_memory_committed_kb", SqlType.BigInt, null, false),
            new("cpu_affinity_mask", SqlType.BigInt, null, false),
            new("online_scheduler_mask", SqlType.BigInt, null, false),
            new("processor_group", SqlType.SmallInt, null, false),
            new("foreign_committed_kb", SqlType.BigInt, null, false),
            new("target_kb", SqlType.BigInt, null, false),
        ];
        Sys("dm_os_memory_nodes", memoryNodes, (_, _) => MemoryNodeRows(memoryNodes));

        Sys("dm_os_sys_memory",
        [
            new("total_physical_memory_kb", SqlType.BigInt, null, false),
            new("available_physical_memory_kb", SqlType.BigInt, null, false),
            new("total_page_file_kb", SqlType.BigInt, null, false),
            new("available_page_file_kb", SqlType.BigInt, null, false),
            new("system_cache_kb", SqlType.BigInt, null, false),
            new("kernel_paged_pool_kb", SqlType.BigInt, null, false),
            new("kernel_nonpaged_pool_kb", SqlType.BigInt, null, false),
            new("system_high_memory_signal_state", SqlType.Bit, null, false),
            new("system_low_memory_signal_state", SqlType.Bit, null, false),
            new("system_memory_state_desc", SqlType.NVarchar, 256, false),
        ], static (_, _) => SysMemoryRows());

        HeapColumn[] clerks =
        [
            new("memory_clerk_address", SqlType.Varbinary, 8, false),
            new("type", SqlType.NVarchar, 60, false),
            new("name", SqlType.NVarchar, 256, false),
            new("memory_node_id", SqlType.SmallInt, null, false),
            new("pages_kb", SqlType.BigInt, null, false),
            new("virtual_memory_reserved_kb", SqlType.BigInt, null, false),
            new("virtual_memory_committed_kb", SqlType.BigInt, null, false),
            new("awe_allocated_kb", SqlType.BigInt, null, false),
            new("shared_memory_reserved_kb", SqlType.BigInt, null, false),
            new("shared_memory_committed_kb", SqlType.BigInt, null, false),
            new("page_size_in_bytes", SqlType.BigInt, null, false),
            new("page_allocator_address", SqlType.Varbinary, 8, false),
            new("host_address", SqlType.Varbinary, 8, false),
            new("parent_memory_broker_type", SqlType.NVarchar, 60, true),
        ];
        Sys("dm_os_memory_clerks", clerks, (batch, _) => MemoryClerkRows(batch, clerks));

        // Real keeps its ring buffers' records of scheduler health, memory
        // brokers, connectivity and exceptions; the simulator samples none.
        Sys("dm_os_ring_buffers",
        [
            new("ring_buffer_address", SqlType.Varbinary, 8, false),
            new("ring_buffer_type", SqlType.NVarchar, 60, false),
            new("timestamp", SqlType.BigInt, null, false),
            new("record", SqlType.NVarcharMax, null, true),
            new("ring_buffer_group", SqlType.NVarchar, 60, false),
            new("create_time", SqlType.GetDateTime2(7), null, false),
        ], static (_, _) => EmptyCatalogRows);

        Sys("dm_os_buffer_descriptors",
        [
            new("database_id", SqlType.Int32, null, true),
            new("file_id", SqlType.Int32, null, true),
            new("page_id", SqlType.Int32, null, true),
            new("page_level", SqlType.Int32, null, true),
            new("allocation_unit_id", SqlType.BigInt, null, true),
            new("page_type", SqlType.NVarchar, 60, true),
            new("row_count", SqlType.Int32, null, true),
            new("free_space_in_bytes", SqlType.Int32, null, true),
            new("is_modified", SqlType.Bit, null, true),
            new("numa_node", SqlType.Int32, null, true),
            new("read_microsec", SqlType.BigInt, null, true),
            new("is_in_bpool_extension", SqlType.Bit, null, true),
            new("error_code", SqlType.Int32, null, true),
            new("op_history", SqlType.Varbinary, 8, true),
            new("buffer_address", SqlType.Varbinary, 8, true),
            new("latch_address", SqlType.Varbinary, 8, true),
            new("latch_desc", SqlType.NVarchar, 256, true),
        ], static (batch, _) => BufferDescriptorRows(batch));

        HeapColumn[] waitColumns =
        [
            new("wait_type", SqlType.NVarchar, 60, false),
            new("waiting_tasks_count", SqlType.BigInt, null, false),
            new("wait_time_ms", SqlType.BigInt, null, false),
            new("max_wait_time_ms", SqlType.BigInt, null, false),
            new("signal_wait_time_ms", SqlType.BigInt, null, false),
        ];
        Sys("dm_os_wait_stats", waitColumns, static (batch, _) => OsWaitStatsRows(batch.Connection.Simulation.WaitStats));
        Sys("dm_exec_session_wait_stats",
        [
            new("session_id", SqlType.SmallInt, null, false),
            .. waitColumns,
        ], static (batch, _) => SessionWaitStatsRows(batch));

        // No latch the simulator takes is one of real's classes, so every
        // class reports no waits.
        Sys("dm_os_latch_stats",
        [
            new("latch_class", SqlType.NVarchar, 60, false),
            new("waiting_requests_count", SqlType.BigInt, null, true),
            new("wait_time_ms", SqlType.BigInt, null, true),
            new("max_wait_time_ms", SqlType.BigInt, null, true),
        ], static (_, _) => WaitStatistics.LatchClasses.Select(static latchClass => new[]
        {
            SqlValue.FromNVarchar(latchClass), SqlValue.FromInt64(0), SqlValue.FromInt64(0), SqlValue.FromInt64(0),
        }));
    }

    /// <summary>
    /// The schedulers real gives one to each CPU, the simulator's processor
    /// count — the same <c>cpu_count</c> <c>sys.dm_os_sys_info</c> reports.
    /// </summary>
    private static int VisibleSchedulerCount => Environment.ProcessorCount;

    /// <summary>The affinity mask of the visible schedulers' CPUs.</summary>
    private static long SchedulerMask => VisibleSchedulerCount >= 64 ? -1L : (1L << VisibleSchedulerCount) - 1;

    /// <summary>
    /// The visible scheduler a session's tasks run on. The simulator runs a
    /// request on its caller's thread rather than on a scheduler, so the
    /// session id spreads them as real's round-robin placement would.
    /// </summary>
    internal static int SchedulerOf(int spid) => spid % VisibleSchedulerCount;

    /// <summary>
    /// The address of a request's task, which <c>sys.dm_exec_requests</c>,
    /// <c>sys.dm_os_tasks</c>, <c>sys.dm_os_workers</c> and
    /// <c>sys.dm_db_task_space_usage</c> share as their join key. A stand-in:
    /// the session and request numbered into a region no other stand-in uses.
    /// </summary>
    internal static byte[] TaskAddress(int spid, int requestId) => StandInAddress(0x02, spid, requestId);

    private static byte[] WorkerAddress(int spid, int requestId) => StandInAddress(0x03, spid, requestId);

    private static byte[] ThreadAddress(int spid, int requestId) => StandInAddress(0x04, spid, requestId);

    private static byte[] SchedulerAddress(int schedulerId) => StandInAddress(0x01, schedulerId, 0);

    /// <summary>An eight-byte stand-in address: <paramref name="region"/> above the session's and request's numbers.</summary>
    private static byte[] StandInAddress(byte region, long number, int requestId)
    {
        var address = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(address, ((long)region << 40) + (number * 0x1000L) + (requestId * 0x40L));
        return address;
    }

    /// <summary>One request <c>sys.dm_exec_requests</c> lists, as the task views see it.</summary>
    private readonly struct RequestTask(short spid, int requestId, string state, string? waitType, int waitMillis, int threadId, DateTime loginTimeUtc)
    {
        public readonly short Spid = spid;
        public readonly int RequestId = requestId;

        /// <summary>The task's state: <c>RUNNING</c>, <c>RUNNABLE</c> or <c>SUSPENDED</c>, the request's status.</summary>
        public readonly string State = state;

        public readonly string? WaitType = waitType;

        /// <summary>How long the request has waited, its <c>wait_time</c>.</summary>
        public readonly int WaitMillis = waitMillis;
        public readonly int ThreadId = threadId;
        public readonly DateTime LoginTimeUtc = loginTimeUtc;
    }

    /// <summary>
    /// A task for each request <c>sys.dm_exec_requests</c> lists, read off its
    /// rows so the views agree on which requests run. Real lists its system
    /// tasks too, and the idle ones its worker pool keeps; the simulator has
    /// neither.
    /// </summary>
    private static List<RequestTask> RequestTasks(Parser.BatchContext batch, Database database)
    {
        var bySpid = new Dictionary<int, SimulatedDbConnection>();
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
            bySpid[connection.Spid] = connection;
        var tasks = new List<RequestTask>();
        foreach (var row in EnumerateSysDmExecRequests(batch, database))
        {
            var spid = row[0].AsInt16;
            _ = bySpid.TryGetValue(spid, out var connection);
            var status = row[3].AsString;
            tasks.Add(new(
                spid,
                row[1].AsInt32,
                status switch
                {
                    "runnable" => "RUNNABLE",
                    "running" => "RUNNING",
                    _ => "SUSPENDED",
                },
                row[13].IsNull ? null : row[13].AsString,
                row[14].AsInt32,
                connection?.Session.CurrentExecutingThreadId ?? 0,
                connection?.LoginTimeUtc ?? batch.Connection.Simulation.SeedDate));
        }
        return tasks;
    }

    /// <summary>
    /// Rows for <c>sys.dm_os_waiting_tasks</c>: each suspended request's task
    /// and what it waits on. A lock wait names its waiting request's
    /// <c>lock_owner_address</c> as <c>resource_address</c>, its blocker's task
    /// while the blocker runs a request (none for an idle session holding the
    /// lock in an open transaction) and real's description of the lock; a
    /// <c>WAITFOR</c> or a client wait names nothing (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> WaitingTaskRows(Parser.BatchContext batch, Database database)
    {
        var bySpid = new Dictionary<int, SimulatedDbConnection>();
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
            bySpid[connection.Spid] = connection;
        var tasks = RequestTasks(batch, database);
        var nullAddress = SqlValue.Null(SqlType.Varbinary);
        var zero = SqlValue.FromInt32(0);
        var nullContext = SqlValue.Null(SqlType.Int32);
        foreach (var task in tasks)
        {
            if (task.State != "SUSPENDED" || task.WaitType is not { } waitType)
                continue;
            _ = bySpid.TryGetValue(task.Spid, out var connection);
            var lockWait = connection is not null && connection.WaitingOnResource is not null && waitType.StartsWith("LCK_M_", StringComparison.Ordinal);
            var blocker = lockWait && LockDmvs.FindFirstBlocker(connection!.WaitingOnResource!, connection) is { } spid ? spid : (int?)null;
            byte[]? blockingTask = null;
            if (blocker is { } blockingSpid)
            {
                foreach (var other in tasks)
                {
                    if (other.Spid == blockingSpid)
                    {
                        blockingTask = TaskAddress(other.Spid, other.RequestId);
                        break;
                    }
                }
            }
            var resourceAddress = lockWait ? LockDmvs.WaitingTaskResourceAddress(batch, connection!) : null;
            var description = lockWait ? LockDmvs.WaitingTaskDescription(batch, connection!) : null;
            yield return
            [
                SqlValue.FromVarbinary(TaskAddress(task.Spid, task.RequestId)),
                SqlValue.FromInt16(task.Spid),
                zero,
                SqlValue.FromInt64(task.WaitMillis),
                SqlValue.FromNVarchar(waitType),
                resourceAddress is null ? nullAddress : SqlValue.FromVarbinary(resourceAddress),
                blockingTask is null ? nullAddress : SqlValue.FromVarbinary(blockingTask),
                blocker is { } b ? SqlValue.FromInt16((short)b) : SqlValue.Null(SqlType.SmallInt),
                nullContext,
                description is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(description),
            ];
        }
    }

    /// <summary>
    /// A row of <paramref name="columns"/> holding <paramref name="values"/>,
    /// every other column the stand-in for a counter real's engine moves and
    /// the simulator's doesn't: zero for a number, NULL for anything else that
    /// may be NULL, and an empty value for the rest.
    /// </summary>
    private static SqlValue[] StandInRow(HeapColumn[] columns, ReadOnlySpan<(string Column, SqlValue Value)> values)
    {
        var row = new SqlValue[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var type = columns[i].Type;
            row[i] = type switch
            {
                Int32SqlType => SqlValue.FromInt32(0),
                BigIntSqlType => SqlValue.FromInt64(0),
                SmallIntSqlType => SqlValue.FromInt16(0),
                TinyIntSqlType => SqlValue.FromByte(0),
                BitSqlType => SqlValue.FromBoolean(false),
                _ when columns[i].Nullable => SqlValue.Null(type),
                VarbinarySqlType => SqlValue.FromVarbinary(new byte[8]),
                _ => SqlValue.FromNVarchar(string.Empty),
            };
        }
        foreach (var (column, value) in values)
        {
            var index = Array.FindIndex(columns, candidate => candidate.Name == column);
            row[index] = value;
        }
        return row;
    }

    /// <summary>
    /// <c>sys.dm_os_schedulers</c>: a visible scheduler per CPU carrying the
    /// tasks <see cref="SchedulerOf"/> places on it, then the hidden
    /// schedulers and the dedicated admin connection's, in the count
    /// <c>sys.dm_os_sys_info.scheduler_total_count</c> reports.
    /// </summary>
    private static IEnumerable<SqlValue[]> SchedulerRows(Parser.BatchContext batch, Database database, HeapColumn[] columns)
    {
        var cpus = VisibleSchedulerCount;
        var current = new int[cpus];
        var runnable = new int[cpus];
        var firstWorker = new byte[]?[cpus];
        foreach (var task in RequestTasks(batch, database))
        {
            var scheduler = SchedulerOf(task.Spid);
            current[scheduler]++;
            if (task.State == "RUNNABLE")
                runnable[scheduler]++;
            firstWorker[scheduler] ??= WorkerAddress(task.Spid, task.RequestId);
        }
        var idealWorkers = Math.Max(1, (cpus <= 4 ? 512 : 512 + ((cpus - 4) * 16)) / cpus);
        for (var id = 0; id < cpus; id++)
            yield return Scheduler(id, 0, id, "VISIBLE ONLINE", current[id], runnable[id], firstWorker[id], idealWorkers);
        // The hidden schedulers number from 1048578 and the admin
        // connection's is 1048576, listed after the first hidden one (probed
        // 2026-10-16 against SQL Server 2025).
        const int Dac = 1048576;
        // sys.dm_os_sys_info counts eight schedulers past the visible ones.
        const int hidden = 7;
        for (var i = 0; i < hidden; i++)
        {
            yield return Scheduler(Dac + 2 + i, 0, i % cpus, "HIDDEN ONLINE", 0, 0, null, 2);
            if (i == 0)
                yield return Scheduler(Dac, 64, 0, "VISIBLE ONLINE (DAC)", 0, 0, null, 5);
        }

        SqlValue[] Scheduler(int id, int node, int cpu, string status, int tasks, int runnableTasks, byte[]? worker, int ideal) => StandInRow(columns,
        [
            ("scheduler_address", SqlValue.FromVarbinary(SchedulerAddress(id))),
            ("parent_node_id", SqlValue.FromInt32(node)),
            ("scheduler_id", SqlValue.FromInt32(id)),
            ("cpu_id", SqlValue.FromInt32(cpu)),
            ("status", SqlValue.FromNVarchar(status)),
            ("is_online", SqlValue.FromBoolean(true)),
            ("is_idle", SqlValue.FromBoolean(tasks == 0)),
            ("current_tasks_count", SqlValue.FromInt32(tasks)),
            ("runnable_tasks_count", SqlValue.FromInt32(runnableTasks)),
            ("current_workers_count", SqlValue.FromInt32(tasks)),
            ("active_workers_count", SqlValue.FromInt32(tasks)),
            ("load_factor", SqlValue.FromInt32(tasks)),
            ("active_worker_address", worker is null ? SqlValue.Null(SqlType.Varbinary) : SqlValue.FromVarbinary(worker)),
            ("memory_object_address", SqlValue.FromVarbinary(StandInAddress(0x05, id, 0))),
            ("task_memory_object_address", SqlValue.FromVarbinary(StandInAddress(0x06, id, 0))),
            ("quantum_length_us", SqlValue.FromInt64(4000)),
            ("total_cpu_idle_capped_ms", SqlValue.FromInt64(0)),
            ("ideal_workers_limit", SqlValue.FromInt32(ideal)),
        ]);
    }

    /// <summary>
    /// <c>sys.dm_os_nodes</c>: the one NUMA node the visible schedulers share,
    /// and the admin connection's node 64.
    /// </summary>
    private static IEnumerable<SqlValue[]> NodeRows(Parser.BatchContext batch, Database database, HeapColumn[] columns)
    {
        var cpus = VisibleSchedulerCount;
        var tasks = RequestTasks(batch, database);
        var busy = new HashSet<int>();
        foreach (var task in tasks)
            _ = busy.Add(SchedulerOf(task.Spid));
        yield return Node(0, "ONLINE", SchedulerMask, (short)cpus, (short)(cpus - busy.Count), tasks.Count, tasks.Count / cpus, true);
        yield return Node(64, "ONLINE DAC", 0, 1, 1, 0, 0, false);

        SqlValue[] Node(short id, string state, long cpuMask, short online, short idle, int activeWorkers, int loadBalance, bool monitored) => StandInRow(columns,
        [
            ("node_id", SqlValue.FromInt16(id)),
            ("node_state_desc", SqlValue.FromNVarchar(state)),
            ("memory_object_address", SqlValue.FromVarbinary(StandInAddress(0x09, id, 0))),
            ("memory_clerk_address", SqlValue.FromVarbinary(StandInAddress(0x0A, id, 0))),
            ("io_completion_worker_address", SqlValue.FromVarbinary(StandInAddress(0x0B, id, 0))),
            ("memory_node_id", SqlValue.FromInt16(id)),
            ("cpu_affinity_mask", SqlValue.FromInt64(cpuMask)),
            ("online_scheduler_count", SqlValue.FromInt16(online)),
            ("idle_scheduler_count", SqlValue.FromInt16(idle)),
            ("active_worker_count", SqlValue.FromInt32(activeWorkers)),
            ("avg_load_balance", SqlValue.FromInt32(loadBalance)),
            ("resource_monitor_state", SqlValue.FromBoolean(monitored)),
            ("online_scheduler_mask", SqlValue.FromInt64(SchedulerMask)),
            ("cpu_count", SqlValue.FromInt32(cpus)),
        ]);
    }

    /// <summary>
    /// <c>sys.dm_os_memory_nodes</c>: node 0 holding the host process's memory
    /// — the reading <c>sys.dm_os_process_memory</c> makes — and the admin
    /// connection's node 64, which holds none.
    /// </summary>
    private static IEnumerable<SqlValue[]> MemoryNodeRows(HeapColumn[] columns)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var targetKb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024;
        yield return StandInRow(columns,
        [
            ("virtual_address_space_reserved_kb", SqlValue.FromInt64(process.VirtualMemorySize64 / 1024)),
            ("virtual_address_space_committed_kb", SqlValue.FromInt64(process.PrivateMemorySize64 / 1024)),
            ("pages_kb", SqlValue.FromInt64(process.WorkingSet64 / 1024)),
            ("cpu_affinity_mask", SqlValue.FromInt64(SchedulerMask)),
            ("online_scheduler_mask", SqlValue.FromInt64(SchedulerMask)),
            ("target_kb", SqlValue.FromInt64(targetKb)),
        ]);
        yield return StandInRow(columns,
        [
            ("memory_node_id", SqlValue.FromInt16(64)),
            ("cpu_affinity_mask", SqlValue.FromInt64(SchedulerMask)),
            ("online_scheduler_mask", SqlValue.FromInt64(SchedulerMask)),
        ]);
    }

    /// <summary>
    /// <c>sys.dm_os_sys_memory</c>: the memory the runtime sees on its host,
    /// the total <c>sys.dm_os_sys_info.physical_memory_kb</c> reports, and the
    /// part of it not in use. A Linux server reports its page file as its
    /// physical memory (probed 2026-10-16 against SQL Server 2025), and the
    /// signal states follow the share left: low under a tenth, high
    /// otherwise.
    /// </summary>
    private static IEnumerable<SqlValue[]> SysMemoryRows()
    {
        var info = GC.GetGCMemoryInfo();
        var totalKb = info.TotalAvailableMemoryBytes / 1024;
        var availableKb = Math.Max(0, totalKb - (info.MemoryLoadBytes / 1024));
        var low = availableKb * 10 < totalKb;
        var zero = SqlValue.FromInt64(0);
        yield return
        [
            SqlValue.FromInt64(totalKb),
            SqlValue.FromInt64(availableKb),
            SqlValue.FromInt64(totalKb),
            SqlValue.FromInt64(availableKb),
            zero,
            zero,
            zero,
            SqlValue.FromBoolean(!low),
            SqlValue.FromBoolean(low),
            SqlValue.FromNVarchar(low ? "Available physical memory is low" : "Available physical memory is high"),
        ];
    }

    /// <summary>
    /// Real's memory clerk types, each with the name and parent broker its
    /// first clerk on node 0 carries (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static readonly (string Type, string Name, string? Broker)[] MemoryClerkTypes = ReadMemoryClerkTypes();

    private static (string Type, string Name, string? Broker)[] ReadMemoryClerkTypes()
    {
        using var stream = typeof(BuiltInResources).Assembly.GetManifestResourceStream("SqlServerSimulator.MemoryClerks.tsv")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.MemoryClerks.tsv is missing.");
        using var reader = new StreamReader(stream);
        var types = new List<(string, string, string?)>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length == 3)
                types.Add((fields[0], fields[1], fields[2] == "NULL" ? null : fields[2]));
        }
        return [.. types];
    }

    /// <summary>
    /// <c>sys.dm_os_memory_clerks</c>: a clerk of each of real's types on node
    /// 0. The buffer pool's holds the pages of every database's tables, as
    /// <c>sys.dm_os_buffer_descriptors</c> lists them, and the plan stores the
    /// cached plans' sizes, as <c>sys.dm_exec_cached_plans</c> reports them;
    /// every other clerk holds nothing the simulator accounts.
    /// </summary>
    private static IEnumerable<SqlValue[]> MemoryClerkRows(Parser.BatchContext batch, HeapColumn[] columns)
    {
        var simulation = batch.Connection.Simulation;
        long bufferPoolKb = 0;
        foreach (var (database, _) in DbId.DatabasesWithIds(simulation))
            bufferPoolKb += SumDataFilePages(database) * 8;
        long sqlPlansBytes = 0;
        long objectPlansBytes = 0;
        foreach (var plan in simulation.LiveCachedPlans())
        {
            if (plan.IsBatch)
                sqlPlansBytes += CachedPlanSizeBytes(plan);
            else
                objectPlansBytes += CachedPlanSizeBytes(plan);
        }
        for (var i = 0; i < MemoryClerkTypes.Length; i++)
        {
            var (type, name, broker) = MemoryClerkTypes[i];
            var pagesKb = type switch
            {
                "CACHESTORE_OBJCP" => objectPlansBytes / 1024,
                "CACHESTORE_SQLCP" => sqlPlansBytes / 1024,
                "MEMORYCLERK_SQLBUFFERPOOL" => bufferPoolKb,
                _ => 0,
            };
            yield return StandInRow(columns,
            [
                ("memory_clerk_address", SqlValue.FromVarbinary(StandInAddress(0x0C, i, 0))),
                ("type", SqlValue.FromNVarchar(type)),
                ("name", SqlValue.FromNVarchar(name)),
                ("pages_kb", SqlValue.FromInt64(pagesKb)),
                ("page_size_in_bytes", SqlValue.FromInt64(8192)),
                ("page_allocator_address", SqlValue.FromVarbinary(StandInAddress(0x0D, i, 0))),
                ("parent_memory_broker_type", broker is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(broker)),
            ]);
        }
    }

    /// <summary>
    /// <c>sys.dm_os_buffer_descriptors</c>: every page of every database's
    /// tables is in memory, so the buffer pool lists each, under the
    /// allocation unit <c>sys.allocation_units</c> reports it in — a data page
    /// for a row page, a text page for a LOB page. Page ids are numbered
    /// through each database's primary file; a page's row count and free
    /// space aren't kept, and no page waits to be written.
    /// </summary>
    private static IEnumerable<SqlValue[]> BufferDescriptorRows(Parser.BatchContext batch)
    {
        var fileId = SqlValue.FromInt32(1);
        var zero = SqlValue.FromInt32(0);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var off = SqlValue.FromBoolean(false);
        var noBinary = SqlValue.Null(SqlType.Varbinary);
        var noLatch = SqlValue.Null(SqlType.NVarchar);
        var dataPage = SqlValue.FromNVarchar("DATA_PAGE");
        var textPage = SqlValue.FromNVarchar("TEXT_MIX_PAGE");
        foreach (var (database, id) in DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            var databaseId = SqlValue.FromInt32(id);
            // Past the pages real's file header, allocation maps and boot
            // page take.
            var pageId = 16;
            foreach (var (containerId, type, totalPages, _, _, _) in EnumerateAllocationUnitData(database))
            {
                var unit = SqlValue.FromInt64(PartitionCensus.AllocationUnitId(containerId, type));
                for (long i = 0; i < totalPages; i++)
                {
                    yield return
                    [
                        databaseId, fileId, SqlValue.FromInt32(pageId++), zero, unit, type == 2 ? textPage : dataPage,
                        nullInt, nullInt, off, zero, SqlValue.FromInt64(0), off, zero, noBinary, noBinary, noBinary, noLatch,
                    ];
                }
            }
        }
    }

    /// <summary><c>sys.dm_os_wait_stats</c>: every one of real's wait types, in its order, with what the server's sessions have waited on it.</summary>
    private static IEnumerable<SqlValue[]> OsWaitStatsRows(WaitStatistics waits)
    {
        var zero = SqlValue.FromInt64(0);
        foreach (var waitType in WaitStatistics.WaitTypes)
        {
            var totals = waits.Of(waitType);
            yield return
            [
                SqlValue.FromNVarchar(waitType),
                SqlValue.FromInt64(totals.WaitingTasks),
                SqlValue.FromInt64(totals.WaitMilliseconds),
                SqlValue.FromInt64(totals.MaxWaitMilliseconds),
                zero,
            ];
        }
    }

    /// <summary>
    /// <c>sys.dm_exec_session_wait_stats</c>: a row for each wait type a live
    /// session has waited on since it opened, in session order and real's
    /// order of the types.
    /// </summary>
    private static IEnumerable<SqlValue[]> SessionWaitStatsRows(Parser.BatchContext batch)
    {
        var connections = batch.Connection.Simulation.SnapshotConnections();
        Array.Sort(connections, static (a, b) => a.Spid.CompareTo(b.Spid));
        var zero = SqlValue.FromInt64(0);
        foreach (var connection in connections)
        {
            if (connection.Session.Waits is not { } waits)
                continue;
            var spid = SqlValue.FromInt16((short)connection.Spid);
            var waited = waits.Snapshot();
            Array.Sort(waited, static (a, b) => WaitTypeOrder(a.Key).CompareTo(WaitTypeOrder(b.Key)));
            foreach (var (waitType, totals) in waited)
            {
                yield return
                [
                    spid,
                    SqlValue.FromNVarchar(waitType),
                    SqlValue.FromInt64(totals.WaitingTasks),
                    SqlValue.FromInt64(totals.WaitMilliseconds),
                    SqlValue.FromInt64(totals.MaxWaitMilliseconds),
                    zero,
                ];
            }
        }

        static int WaitTypeOrder(string waitType) => Array.IndexOf(WaitStatistics.WaitTypes, waitType);
    }
}
