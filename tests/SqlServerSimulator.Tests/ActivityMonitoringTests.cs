using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.Extensions;

namespace SqlServerSimulator;

/// <summary>
/// The activity surface a monitoring procedure such as sp_WhoIsActive reads
/// while one session blocks another — the lock, wait, request, session and
/// transaction views, <c>sysprocesses</c>, the SQL text and live plan of a
/// running statement — and the engine pieces such a procedure leans on.
/// Probed 2026-10-16 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ActivityMonitoringTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// One session holding a key X in an open transaction, idle, and another
    /// blocked reading that key, observed from a third.
    /// </summary>
    private sealed class Blocking : IDisposable
    {
        public readonly Simulation Sim = new();
        public readonly System.Data.Common.DbConnection Holder;
        public readonly System.Data.Common.DbConnection Waiter;
        public readonly System.Data.Common.DbConnection Observer;
        public readonly short HolderSpid;
        public readonly short WaiterSpid;
        private readonly Task waiting;

        public Blocking(CancellationToken cancellationToken)
        {
            _ = this.Sim.ExecuteNonQuery("create table t (id int primary key, v varchar(20)); insert t values (1, 'a'), (2, 'b'), (3, 'c')");
            this.Holder = this.Sim.CreateOpenConnection();
            this.Waiter = this.Sim.CreateOpenConnection();
            this.Observer = this.Sim.CreateOpenConnection();
            this.HolderSpid = (short)this.Holder.CreateCommand("select @@spid").ExecuteScalar()!;
            this.WaiterSpid = (short)this.Waiter.CreateCommand("select @@spid").ExecuteScalar()!;
            _ = this.Holder.CreateCommand("begin tran; update t set v = 'x' where id = 2").ExecuteNonQuery();
            this.waiting = Task.Run(() => this.Waiter.CreateCommand("select * from t where id = 2").ExecuteScalar(), cancellationToken);
            var spid = this.WaiterSpid.ToInvariantString();
            _ = PollUntil(
                () => (int)this.Observer.CreateCommand($"select count(*) from sys.dm_os_waiting_tasks where session_id = {spid}").ExecuteScalar()!,
                count => count == 1,
                cancellationToken).GetAwaiter().GetResult();
        }

        public object? Scalar(string sql) => this.Observer.CreateCommand(sql).ExecuteScalar();

        public void Dispose()
        {
            _ = this.Holder.CreateCommand("rollback").ExecuteNonQuery();
            this.waiting.GetAwaiter().GetResult();
            this.Holder.Dispose();
            this.Waiter.Dispose();
            this.Observer.Dispose();
        }
    }

    [TestMethod]
    public void DmTranLocks_ReportsRealsColumns()
    {
        using var blocking = new Blocking(TestContext.CancellationToken);
        var hobt = blocking.Scalar("select hobt_id from sys.partitions where object_id = object_id('t')");
        AreEqual(
            $"KEY|(61a06abd401c)|{hobt}|null|S|LOCK|WAIT|1|0|TRANSACTION;KEY|(61a06abd401c)|{hobt}|null|X|LOCK|GRANT|1|33554432|TRANSACTION",
            blocking.Scalar($"""
                select string_agg(concat(resource_type, '|', rtrim(resource_description), '|', resource_associated_entity_id, '|', isnull(cast(resource_lock_partition as varchar), 'null'), '|',
                    request_mode, '|', request_type, '|', request_status, '|', request_reference_count, '|', request_lifetime, '|', request_owner_type), ';')
                    within group (order by request_status desc)
                from sys.dm_tran_locks where resource_type = 'KEY'
                """));
        AreEqual("0|IX|33554432", blocking.Scalar($"select concat(resource_lock_partition, '|', request_mode, '|', request_lifetime) from sys.dm_tran_locks where resource_type = 'OBJECT' and request_session_id = {blocking.HolderSpid}"));
        AreEqual("SHARED_TRANSACTION_WORKSPACE|0|0", blocking.Scalar($"select concat(request_owner_type, '|', request_owner_id, '|', request_lifetime) from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id = {blocking.HolderSpid}"));
        AreEqual(1, blocking.Scalar($"select count(*) from sys.dm_tran_locks l join sys.dm_tran_session_transactions s on s.transaction_id = l.request_owner_id where l.resource_type = 'KEY' and l.request_session_id = {blocking.HolderSpid} and s.session_id = {blocking.HolderSpid}"));
    }

    [TestMethod]
    public void DmOsWaitingTasks_DescribesTheLockAsRealDoes()
    {
        using var blocking = new Blocking(TestContext.CancellationToken);
        var hobt = blocking.Scalar("select hobt_id from sys.partitions where object_id = object_id('t')");
        var row = (string)blocking.Scalar($"""
            select concat(w.wait_type, '|', w.blocking_session_id, '|', w.exec_context_id, '|', isnull(cast(w.blocking_task_address as varchar), 'null'), '|',
                iif(w.resource_address = l.lock_owner_address, 'own', 'other'), '|', iif(w.waiting_task_address = t.task_address, 'task', 'x'), '|',
                replace(w.resource_description, substring(w.resource_description, charindex('id=lock', w.resource_description), charindex(' mode', w.resource_description) - charindex('id=lock', w.resource_description)), 'id=…'))
            from sys.dm_os_waiting_tasks w
            join sys.dm_tran_locks l on l.request_session_id = w.session_id and l.request_status = 'WAIT'
            join sys.dm_os_tasks t on t.session_id = w.session_id
            where w.session_id = {blocking.WaiterSpid}
            """)!;
        AreEqual($"LCK_M_S|{blocking.HolderSpid}|0|null|own|task|keylock hobtid={hobt} dbid={blocking.Scalar("select db_id()")} id=… mode=X associatedObjectId={hobt}", row);
    }

    [TestMethod]
    public void RequestsAndSysprocesses_NameTheWaitAndTheBlocker()
    {
        using var blocking = new Blocking(TestContext.CancellationToken);
        var hobt = blocking.Scalar("select hobt_id from sys.partitions where object_id = object_id('t')");
        var key = $"KEY: {blocking.Scalar("select db_id()")}:{hobt} (61a06abd401c)";
        AreEqual($"suspended|LCK_M_S|{blocking.HolderSpid}|{key}", blocking.Scalar($"select concat(status, '|', wait_type, '|', blocking_session_id, '|', wait_resource) from sys.dm_exec_requests where session_id = {blocking.WaiterSpid}"));
        AreEqual($"suspended|SELECT|{blocking.HolderSpid}|0x0003|LCK_M_S|{key}", blocking.Scalar($"select concat(rtrim(status), '|', rtrim(cmd), '|', blocked, '|', convert(varchar, waittype, 1), '|', rtrim(lastwaittype), '|', rtrim(waitresource)) from sys.sysprocesses where spid = {blocking.WaiterSpid}"));
        AreEqual("sleeping|AWAITING COMMAND|0|-1|-1|0x0000", blocking.Scalar($"select concat(rtrim(status), '|', rtrim(cmd), '|', blocked, '|', stmt_start, '|', stmt_end, '|', convert(varchar, waittype, 1)) from sys.sysprocesses where spid = {blocking.HolderSpid}"));
        AreEqual("running|sleeping", blocking.Scalar($"select concat((select status from sys.dm_exec_sessions where session_id = {blocking.WaiterSpid}), '|', (select status from sys.dm_exec_sessions where session_id = {blocking.HolderSpid}))"));
        AreEqual(1, blocking.Scalar($"select count(*) from sys.dm_exec_requests r join sys.dm_exec_sessions s on s.session_id = r.session_id and r.start_time = s.last_request_start_time where r.session_id = {blocking.WaiterSpid}"));
    }

    [TestMethod]
    public void RunningStatement_ReportsItsSimpleParameterizedText()
    {
        using var blocking = new Blocking(TestContext.CancellationToken);
        const string Prepared = "(@1 tinyint)SELECT * FROM [t] WHERE [id]=@1";
        AreEqual(
            $"{Prepared}|24|{(Prepared.Length - 1) * 2}",
            blocking.Scalar($"select concat(t.text, '|', r.statement_start_offset, '|', r.statement_end_offset) from sys.dm_exec_requests r cross apply sys.dm_exec_sql_text(r.sql_handle) t where r.session_id = {blocking.WaiterSpid}"));
        AreEqual(Prepared, blocking.Scalar($"select t.text from sys.sysprocesses p cross apply sys.dm_exec_sql_text(p.sql_handle) t where p.spid = {blocking.WaiterSpid}"));
        AreEqual(1, blocking.Scalar($"select count(*) from sys.dm_exec_requests where session_id = {blocking.WaiterSpid} and plan_handle is not null"));
        AreEqual(
            """1|1|0""",
            blocking.Scalar($"""
                select concat(
                    (select count(*) from sys.dm_exec_requests r cross apply sys.dm_exec_query_statistics_xml(r.session_id) x
                        where r.session_id = {blocking.WaiterSpid} and x.plan_handle = r.plan_handle and cast(x.query_plan as nvarchar(max)) like '%StatementParameterizationType="2"%'), '|',
                    (select count(*) from sys.dm_exec_query_statistics_xml({blocking.WaiterSpid})), '|',
                    (select count(*) from sys.dm_exec_query_statistics_xml({blocking.HolderSpid})))
                """));
    }

    [TestMethod]
    public void AutocommitStatement_ListsItsTransaction()
    {
        using var blocking = new Blocking(TestContext.CancellationToken);
        AreEqual(
            "SELECT|2|2|3|0",
            blocking.Scalar($"""
                select concat(a.name, '|', a.transaction_type, '|', d.database_transaction_type, '|', d.database_transaction_state, '|', d.database_transaction_log_record_count)
                from sys.dm_exec_requests r
                join sys.dm_tran_active_transactions a on a.transaction_id = r.transaction_id
                join sys.dm_tran_database_transactions d on d.transaction_id = r.transaction_id and d.database_id = db_id()
                where r.session_id = {blocking.WaiterSpid} and r.transaction_id <> 0
                """));
        AreEqual("4|2", blocking.Scalar($"select concat(d.database_transaction_state, '|', d.database_transaction_log_record_count) from sys.dm_tran_session_transactions s join sys.dm_tran_database_transactions d on d.transaction_id = s.transaction_id and d.database_id = db_id() where s.session_id = {blocking.HolderSpid}"));
    }

    [TestMethod]
    public void SpLock_NamesAKeysIndex()
    {
        using var blocking = new Blocking(TestContext.CancellationToken);
        var table = blocking.Scalar("select object_id('t')");
        using var command = blocking.Observer.CreateCommand("exec sp_lock");
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            if (reader.GetString(4) == "KEY")
                keys.Add(string.Join('|', reader.GetValue(2).ToInvariantString(), reader.GetInt16(3).ToInvariantString(), reader.GetString(6), reader.GetString(7)));
        }
        keys.Sort(StringComparer.Ordinal);
        AreEqual($"{table}|1|S|WAIT;{table}|1|X|GRANT", string.Join(';', keys));
    }

    [TestMethod]
    public async Task StatementEndOffset_StopsBeforeTheNextStatement()
    {
        var sim = new Simulation();
        using var runner = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        var spid = runner.CreateCommand("select @@spid").ExecuteScalar().ToInvariantString();
        // Long enough to outlast a starved poll, and cancelled once the
        // assertion has seen it: a short delay could end while the poll's
        // continuation waited for a pool thread, leaving no request to find.
        using var command = runner.CreateCommand("select 1; waitfor delay '00:00:30'; select 2");
        var running = Task.Run(command.ExecuteNonQuery, TestContext.CancellationToken);
        var offsets = await PollUntil(
            () => observer.CreateCommand($"select concat(statement_start_offset, '|', statement_end_offset, '|', wait_type) from sys.dm_exec_requests where session_id = {spid}").ExecuteScalar() as string,
            value => value is not null && value.EndsWith("WAITFOR", StringComparison.Ordinal),
            TestContext.CancellationToken);
        AreEqual("20|70|WAITFOR", offsets);
        command.Cancel();
        _ = await ThrowsAsync<SimulatedSqlException>(() => running);
    }

    [TestMethod]
    public void SystemObjects_ListTheModeledSystemFunctions()
        => AreEqual(
            "dm_exec_query_statistics_xml|IF|-523604714;dm_exec_sql_text|IF|-245973352;fn_listextendedproperty|TF|-572068538;fn_varbintohexstr|FN|-565035985;resource_governor_workload_groups|V|-254;sysprocesses|V|-210",
            new Simulation().ExecuteScalar("""
                select string_agg(concat(name, '|', rtrim(type), '|', object_id) collate database_default, ';') within group (order by name)
                from sys.all_objects where name in ('dm_exec_sql_text', 'dm_exec_query_statistics_xml', 'fn_listextendedproperty', 'fn_varbintohexstr', 'resource_governor_workload_groups', 'sysprocesses')
                """));

    [TestMethod]
    public void ResourceGovernorViews_ListTheDefaultGroupsAndPools()
        => AreEqual(
            "internal:1:Medium:25,default:2:Medium:25|internal:0:100,default:0:100|4|0",
            new Simulation().ExecuteScalar("""
                select concat(
                    (select string_agg(concat(name, ':', pool_id, ':', importance, ':', request_max_memory_grant_percent), ',') within group (order by group_id) from sys.resource_governor_workload_groups), '|',
                    (select string_agg(concat(name, ':', min_cpu_percent, ':', max_cpu_percent), ',') within group (order by pool_id) from sys.resource_governor_resource_pools), '|',
                    (select count(*) from sys.dm_exec_query_resource_semaphores), '|',
                    (select count(*) from sys.dm_broker_activated_tasks))
                """));

    [TestMethod]
    public void Sysprocesses_CarriesTheServerCollation()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", new Simulation().ExecuteScalar("select collation_name from sys.dm_exec_describe_first_result_set(N'select program_name from sys.sysprocesses', null, 0)"));

    [TestMethod]
    public void SysnameVariable_TakesACatalogColumnsCollation()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            declare @filter sysname = N'%';
            select count(*) from sys.sysprocesses where program_name like @filter and spid = @@spid
            """));

    [TestMethod]
    public void UntakenBranch_DoesNotEvaluateTopOverAVariable()
        => AreEqual("ran", new Simulation().ExecuteScalar("""
            declare @header varchar(max);
            if 1 = 0
                with n as (select top (len(@header) - 1) 1 as number from sys.objects order by number) select number from n;
            select 'ran'
            """));

    [TestMethod]
    public void DeeplyNestedReplace_CompilesInLinearTime()
    {
        var expression = "N'x'";
        for (var i = 0; i < 40; i++)
            expression = $"REPLACE({expression}, NCHAR({(i % 30) + 1}), N'?')";
        AreEqual("x", new Simulation().ExecuteScalar($"select {expression}"));
    }
}
