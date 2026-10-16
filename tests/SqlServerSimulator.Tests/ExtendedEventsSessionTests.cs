using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE</c> / <c>ALTER</c> / <c>DROP EVENT SESSION</c>, the
/// <c>sys.server_event_session*</c> catalog and the running-session
/// <c>sys.dm_xe_session*</c> views, as SQL Server 2025 answers them (probed
/// 2026-10-16).
/// </summary>
[TestClass]
public sealed class ExtendedEventsSessionTests
{
    [TestMethod]
    public void DefaultSessions_ListRealsTwoWithSystemHealthRunning()
    {
        var sim = new Simulation();
        AreEqual("65536:system_health:1,65537:AlwaysOn_health:0", sim.ExecuteScalar("""
            select string_agg(concat(event_session_id, ':', name, ':', cast(startup_state as int)), ',') within group (order by event_session_id)
            from sys.server_event_sessions
            """));
        AreEqual("hkenginexesession,system_health,sp_server_diagnostics session", sim.ExecuteScalar("select string_agg(name, ',') from sys.dm_xe_sessions"));
        AreEqual("120000:4096", sim.ExecuteScalar("select concat(max_dispatch_latency, ':', max_memory) from sys.server_event_sessions where name = 'system_health'"));
    }

    // Real's own predicate text for system_health, which the simulator reads
    // back through its predicate parser and writes out again unchanged.
    [TestMethod]
    public void DefaultSessions_CarryRealsPredicates()
    {
        var sim = new Simulation();
        AreEqual("([sqlserver].[is_system]=(1) AND [component]<>(4))", sim.ExecuteScalar("select predicate from sys.server_event_session_events where event_session_id = 65536 and event_id = 40"));
        AreEqual(21, sim.ExecuteScalar("select count(*) from sys.server_event_session_events where event_session_id = 65536"));
        AreEqual("system_health.xel", sim.ExecuteScalar("select cast(value as nvarchar(100)) from sys.server_event_session_fields where event_session_id = 65536 and name = 'filename'"));
    }

    [TestMethod]
    public void Create_NumbersEventsActionsAttributesAndTargetsFromOneCounter()
        => AreEqual("1:error_reported,4:sql_batch_completed,7:rpc_completed|8:ring_buffer,11:event_file", new Simulation().ExecuteScalar("""
            create event session s on server
            add event sqlserver.error_reported (action (sqlserver.sql_text, sqlserver.session_id) where severity >= 16),
            add event sqlserver.sql_batch_completed (set collect_batch_text = 1 action (package0.collect_system_time)),
            add event rpc_completed
            add target package0.ring_buffer (set max_events_limit = 100, max_memory = 1024),
            add target package0.event_file (set filename = N's.xel', max_file_size = 5);
            select concat(
                (select string_agg(concat(event_id, ':', e.name), ',') within group (order by event_id) from sys.server_event_session_events e join sys.server_event_sessions s on s.event_session_id = e.event_session_id where s.name = 's'),
                '|',
                (select string_agg(concat(target_id, ':', t.name), ',') within group (order by target_id) from sys.server_event_session_targets t join sys.server_event_sessions s on s.event_session_id = t.event_session_id where s.name = 's'))
            """));

    [TestMethod]
    public void Create_StoresThePredicateAsRealRewritesIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create event session s on server add event sqlserver.sql_statement_completed
            (where (duration > 1 AND (cpu_time > 2 or logical_reads > 3)) OR NOT physical_reads = 4 or sqlserver.database_name like 'x%' or statement != N'it''s')
            """);
        AreEqual("([duration]>(1) AND ([cpu_time]>(2) OR [logical_reads]>(3)) OR NOT [physical_reads]=(4) OR [sqlserver].[database_name] like 'x%' OR [statement]<>N'it''s')",
            sim.ExecuteScalar("select predicate from sys.server_event_session_events where event_session_id > 65537"));
        var xml = (string)sim.ExecuteScalar("select predicate_xml from sys.server_event_session_events where event_session_id > 65537")!;
        StartsWith("<or><or><or><and><leaf><comparator name=\"greater_than_int64\" package=\"package0\"></comparator><event name=\"sql_statement_completed\" package=\"sqlserver\" field=\"duration\"></event><value>1</value></leaf>", xml);
        Contains("<leaf><comparator name=\"like_i_sql_unicode_string\" package=\"sqlserver\"></comparator><global name=\"database_name\" package=\"sqlserver\"></global><value><![CDATA[x%]]></value></leaf>", xml);
        Contains("<value><![CDATA[it's]]></value>", xml);
    }

    // A map field compares on its key, and a string names the key by its map value.
    [TestMethod]
    public void Predicate_MapValueBecomesItsKey()
        => AreEqual("<leaf><comparator name=\"equal_uint64\" package=\"package0\"></comparator><event name=\"error_reported\" package=\"sqlserver\" field=\"category\"></event><value>2</value></leaf>",
            new Simulation().ExecuteScalar("create event session s on server add event sqlserver.error_reported (where category = 'SERVER'); select predicate_xml from sys.server_event_session_events where event_session_id > 65537"));

    [TestMethod]
    public void Fields_KeepTheLiteralsType()
        => AreEqual("max_memory:int,filename:varchar", new Simulation().ExecuteScalar("""
            create event session s on server add event sqlserver.error_reported
            add target package0.ring_buffer (set max_memory = 1024), add target package0.event_file (set filename = 'a.xel');
            select string_agg(concat(name, ':', cast(sql_variant_property(value, 'BaseType') as sysname)), ',') within group (order by object_id) from sys.server_event_session_fields where event_session_id > 65537
            """));

    [TestMethod]
    public void Options_SettleToRealsUnits()
        => AreEqual("M:0:8192:N:1:7200", new Simulation().ExecuteScalar("""
            create event session s on server add event sqlserver.sql_batch_completed
            with (event_retention_mode = allow_multiple_event_loss, max_dispatch_latency = infinite, max_memory = 8 mb, memory_partition_mode = per_node, track_causality = on, max_duration = 2 hours);
            select concat(event_retention_mode, ':', max_dispatch_latency, ':', max_memory, ':', memory_partition_mode, ':', track_causality, ':', max_duration) from sys.server_event_sessions where name = 's'
            """));

    // Every object added after a drop takes a fresh id: the counter never goes back.
    [TestMethod]
    public void Alter_NumbersOnFromTheCounter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create event session s on server add event sqlserver.error_reported, add event sqlserver.rpc_completed");
        _ = sim.ExecuteNonQuery("alter event session s on server drop event sqlserver.rpc_completed");
        _ = sim.ExecuteNonQuery("alter event session s on server add event sqlserver.rpc_starting (action (sqlserver.username))");
        _ = sim.ExecuteNonQuery("alter event session s on server add target package0.event_counter");
        AreEqual("1:error_reported,3:rpc_starting", sim.ExecuteScalar("select string_agg(concat(event_id, ':', name), ',') within group (order by event_id) from sys.server_event_session_events where event_session_id > 65537"));
        AreEqual(5, sim.ExecuteScalar("select target_id from sys.server_event_session_targets where event_session_id > 65537"));
    }

    // A failing ALTER leaves the session as it was.
    [TestMethod]
    public void Alter_FailingPartwayChangesNothing()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create event session s on server add event sqlserver.error_reported");
        _ = sim.AssertSqlError("alter event session s on server drop event sqlserver.error_reported, drop event sqlserver.error_reported", 25706);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_event_session_events where event_session_id > 65537"));
    }

    // A session id is spent by every CREATE past its permission check, a
    // refused one included.
    [TestMethod]
    public void Create_SpendsASessionIdEvenWhenRefused()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("create event session s on server add event sqlserver.nosuch", 25623);
        _ = sim.ExecuteNonQuery("create event session s on server add event sqlserver.error_reported");
        AreEqual(65539, sim.ExecuteScalar("select event_session_id from sys.server_event_sessions where name = 's'"));
    }

    [DataRow("create event session s on server add event sqlserver.nosuch", 25623, 1)]
    [DataRow("create event session s on server add event error_reported", 25658, 1)]
    [DataRow("create event session s on server add event package0.xevents_heartbeat", 25623, 1)]
    [DataRow("create event session s on server add event sqlserver.error_reported (action (sqlserver.nosuch))", 25623, 3)]
    [DataRow("create event session s on server add event sqlserver.error_reported, add event sqlserver.error_reported", 25626, 21)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.nosuch", 25623, 2)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.ring_buffer, add target package0.ring_buffer", 25626, 20)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.ring_buffer (set nosuch = 1)", 25629, 1)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.ring_buffer (set max_memory = 'abc')", 25713, 23)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.ring_buffer (set max_memory = -1)", 25713, 8)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.ring_buffer (set max_memory = 1.5)", 25713, 15)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.ring_buffer (set max_memory = 1, max_memory = 2)", 25650, 23)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.event_file", 25642, 23)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.event_file (set filename = 5)", 25713, 13)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.event_file (set filename = N'')", 25641, 0)]
    [DataRow("create event session s on server add event sqlserver.error_reported add target package0.histogram (set source = 'x')", 25641, 0)]
    [DataRow("create event session s on server add event sqlserver.sql_batch_completed (set collect_batch_text = 2)", 25713, 14)]
    [DataRow("create event session s on server add event sqlserver.sql_batch_completed (set duration = 1)", 25629, 1)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where nosuch = 1)", 25706, 8)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where sqlserver.nosuch = 1)", 25706, 8)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where package0.nosuch(severity, 1))", 25706, 2)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where package0.equal_uint64(severity, 1))", 25636, 30)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where package0.equal_int64(message, 1))", 25713, 13)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where severity = 2147483648)", 25713, 4)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where severity = 1.0)", 25713, 15)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where severity = '5')", 25713, 23)]
    [DataRow("create event session s on server add event sqlserver.sql_statement_completed (where row_count > -1)", 25713, 9)]
    [DataRow("create event session s on server add event sqlos.wait_info (where wait_type = 'NO_SUCH_WAIT')", 25713, 23)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where is_intercepted = 2)", 25713, 14)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where is_intercepted > 0)", 25715, 2)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where category like 'S%')", 25712, 1)]
    [DataRow("create event session s on server add event sqlserver.rpc_completed (where output_parameters = 'x')", 25715, 2)]
    [DataRow("create event session s on server add event sqlserver.rpc_completed (where data_stream = 'x')", 25713, 26)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_memory = 191 kb)", 25632, 29)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_memory = 2048 mb)", 25708, 2)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_dispatch_latency = 2147484 seconds)", 25708, 1)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_event_size = 1400 kb)", 25640, 19)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_duration = 0 seconds)", 25703, 6)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (startup_state = on, startup_state = off)", 25702, 1)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (event_retention_mode = no_event_loss)", 25643, 21)]
    [DataRow("create event session s on server add event sqlserver.sql_batch_completed add target package0.ring_buffer with (max_memory = 0 kb)", 25635, 20)]
    [DataRow("create event session s on database add event sqlserver.error_reported", 40133, 13)]
    [DataRow("create event session system_health on server add event sqlserver.error_reported", 25631, 1)]
    [DataRow("alter event session nosuch on server state = start", 15151, 28)]
    [DataRow("drop event session nosuch on server", 15151, 26)]
    [DataRow("alter event session AlwaysOn_health on server state = stop", 25704, 1)]
    [DataRow("alter event session system_health on server state = start", 25705, 1)]
    [DataRow("alter event session system_health on server with (max_memory = 8 mb)", 25707, 1)]
    [DataRow("alter event session system_health on server drop event sqlserver.rpc_completed", 25653, 22)]
    [DataRow("alter event session AlwaysOn_health on server drop event sqlserver.rpc_completed", 25706, 4)]
    [DataRow("alter event session AlwaysOn_health on server drop target package0.ring_buffer", 25706, 5)]
    [DataRow("alter event session AlwaysOn_health on server add event sqlserver.error_reported", 25625, 21)]
    [TestMethod]
    public void Refusal_RaisesRealsErrorAndState(string statement, int number, int state)
        => AreEqual(state, new Simulation().AssertSqlError(statement, number).State);

    // These refusals come from real's grammar, so the batch never starts.
    [DataRow("create event session #s on server add event sqlserver.error_reported", 25701)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_dispatch_latency = 5)", 25703)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_memory = 300)", 25703)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (event_retention_mode = bogus)", 25703)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (bogus = 1)", 102)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (track_causality = 1)", 102)]
    [DataRow("create event session s on server add event sqlserver.error_reported with (max_memory = 1 gb)", 102)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where 1 = severity)", 102)]
    [DataRow("create event session s on server add event sqlserver.error_reported (where severity = @v)", 102)]
    [DataRow("create event session s on server add event sqlserver.error_reported (action (sql_text) set collect_message = 1)", 156)]
    [DataRow("create event session s on server add target package0.ring_buffer", 102)]
    [DataRow("create event session s on server", 102)]
    [DataRow("drop event session if exists s on server", 156)]
    [DataRow("alter event session system_health on server add event sqlserver.rpc_completed, drop event sqlserver.sql_batch_completed", 156)]
    [TestMethod]
    public void Refusal_AtCompile_StopsTheBatch(string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("create table t (a int); " + statement, number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void Refusal_AsItRuns_LetsTheBatchGoOn()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("create table t (a int); create event session s on server add event sqlserver.error_reported add target package0.nosuch", 25623);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [DataRow("create event session s on server add event sqlserver.error_reported", "CREATE")]
    [DataRow("alter event session system_health on server state = stop", "ALTER")]
    [DataRow("drop event session AlwaysOn_health on server", "DROP")]
    [TestMethod]
    public void InsideATransaction_IsMsg574(string statement, string verb)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("begin tran; " + statement);
        var ex = ThrowsExactly<SimulatedSqlException>(() => command.ExecuteNonQuery());
        AreEqual(574, ex.Number);
        AreEqual($"{verb} EVENT SESSION statement cannot be used inside a user transaction.", ex.Errors[0].Message);
    }

    [TestMethod]
    public void StartAndStop_ListTheSessionWhileItRuns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create event session s on server
            add event sqlserver.error_reported (action (sqlserver.sql_text, sqlserver.session_id) where severity >= 11),
            add event sqlserver.sql_batch_completed
            add target package0.ring_buffer (set max_events_limit = 10)
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_xe_sessions where name = 's'"));
        _ = sim.ExecuteNonQuery("alter event session s on server state = start");
        AreEqual("3:1441587:4324761:drop_event:2049:flush_on_close:server", sim.ExecuteScalar("""
            select concat(total_regular_buffers, ':', regular_buffer_size, ':', total_buffer_size, ':', buffer_policy_desc, ':', flags, ':', flag_desc, ':', session_source)
            from sys.dm_xe_sessions where name = 's'
            """));
        AreEqual("sql_batch_completed,error_reported", sim.ExecuteScalar("select string_agg(e.event_name, ',') from sys.dm_xe_session_events e join sys.dm_xe_sessions s on s.address = e.event_session_address where s.name = 's'"));
        AreEqual("session_id,sql_text", sim.ExecuteScalar("select string_agg(a.action_name, ',') from sys.dm_xe_session_event_actions a join sys.dm_xe_sessions s on s.address = a.event_session_address where s.name = 's'"));
        AreEqual("max_memory=0,occurrence_number=0,max_events_limit=10,collect_batch_text=true", sim.ExecuteScalar("""
            select string_agg(concat(c.column_name, '=', c.column_value), ',') from sys.dm_xe_session_object_columns c join sys.dm_xe_sessions s on s.address = c.event_session_address where s.name = 's'
            """));
        StartsWith("<RingBufferTarget truncated=\"0\" processingTime=\"0\" totalEventsProcessed=\"", (string)sim.ExecuteScalar("""
            select t.target_data from sys.dm_xe_session_targets t join sys.dm_xe_sessions s on s.address = t.event_session_address where s.name = 's'
            """)!);
        _ = sim.ExecuteNonQuery("alter event session s on server state = stop");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_xe_sessions where name = 's'"));
    }

    // A running session takes event and target changes; its options other
    // than STARTUP_STATE wait for it to stop.
    [TestMethod]
    public void RunningSession_TakesEventChangesAndStartupState()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create event session s on server add event sqlserver.error_reported");
        _ = sim.ExecuteNonQuery("alter event session s on server state = start");
        _ = sim.ExecuteNonQuery("alter event session s on server add event sqlserver.rpc_completed");
        _ = sim.ExecuteNonQuery("alter event session s on server with (startup_state = on)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.dm_xe_session_events e join sys.dm_xe_sessions s on s.address = e.event_session_address where s.name = 's'"));
        IsTrue((bool)sim.ExecuteScalar("select startup_state from sys.server_event_sessions where name = 's'")!);
        _ = sim.ExecuteNonQuery("drop event session s on server");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_xe_sessions where name = 's'"));
    }

    [TestMethod]
    public void EventFileTarget_NamesItsFileUnderTheLogDirectory()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create event session s on server add event sqlserver.error_reported add target package0.event_file (set filename = N's.xel')");
        _ = sim.ExecuteNonQuery("alter event session s on server state = start");
        var data = (string)sim.ExecuteScalar("select t.target_data from sys.dm_xe_session_targets t join sys.dm_xe_sessions s on s.address = t.event_session_address where s.name = 's'")!;
        StartsWith("<EventFileTarget truncated=\"0\"><Buffers logged=\"0\" dropped=\"0\"/><File name=\"/var/opt/mssql/log/s_0_", data);
    }

    // The granular permissions of SQL Server 2022 and later each open one
    // clause; ALTER ANY EVENT SESSION opens them all, and none opens the views.
    [DataRow("CREATE ANY EVENT SESSION", "create event session v on server add event sqlserver.error_reported", 0, 0)]
    [DataRow("ALTER ANY EVENT SESSION ENABLE", "create event session v on server add event sqlserver.error_reported", 15247, 1)]
    [DataRow("ALTER ANY EVENT SESSION ENABLE", "alter event session u on server state = start", 0, 0)]
    [DataRow("ALTER ANY EVENT SESSION DISABLE", "alter event session u on server state = start", 15151, 20)]
    [DataRow("ALTER ANY EVENT SESSION ADD EVENT", "alter event session u on server add event sqlserver.rpc_completed", 0, 0)]
    [DataRow("ALTER ANY EVENT SESSION ADD EVENT", "alter event session u on server drop event sqlserver.error_reported", 15151, 23)]
    [DataRow("ALTER ANY EVENT SESSION ADD TARGET", "alter event session u on server add target package0.ring_buffer", 0, 0)]
    [DataRow("ALTER ANY EVENT SESSION OPTION", "alter event session u on server with (max_memory = 8 mb)", 0, 0)]
    [DataRow("ALTER ANY EVENT SESSION OPTION", "alter event session u on server add target package0.ring_buffer", 15151, 24)]
    [DataRow("CREATE ANY EVENT SESSION", "drop event session u on server", 15151, 27)]
    [DataRow("DROP ANY EVENT SESSION", "drop event session u on server", 0, 0)]
    [DataRow("ALTER ANY EVENT SESSION", "drop event session u on server", 0, 0)]
    [DataRow("VIEW SERVER STATE", "create event session v on server add event sqlserver.error_reported", 15247, 1)]
    [TestMethod]
    public void Permission_OpensItsClause(string permission, string statement, int number, int state)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            use master; create login lo with password = 'x', check_policy = off;
            grant {permission} to lo;
            create event session u on server add event sqlserver.error_reported
            """);
        var outcome = sim.ExecuteScalar($"""
            use master; execute as login = 'lo';
            begin try {statement}; select '0:0' end try begin catch select concat(error_number(), ':', error_state()) end catch
            """);
        AreEqual($"{number}:{state}", outcome);
    }

    [TestMethod]
    public void RunningSessionViews_RestrictedWithoutViewServerPerformanceState_Raise297Alone()
        => AreEqual(13, new Simulation().AssertSqlError("""
            use master; create login lo with password = 'x', check_policy = off; grant alter any event session to lo;
            execute as login = 'lo';
            select count(*) from sys.dm_xe_sessions
            """, 297).State);

    [TestMethod]
    public void Catalog_RestrictedWithoutViewServerPerformanceState_ListsNothing()
        => AreEqual("0,0", new Simulation().ExecuteScalar("""
            use master; create login lo with password = 'x', check_policy = off; grant alter any event session to lo;
            execute as login = 'lo';
            select concat((select count(*) from sys.server_event_sessions), ',', (select count(*) from sys.server_event_session_targets))
            """));

    [TestMethod]
    public void DdlTrigger_SeesTheEventSessionEvents()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table master.dbo.log (e xml)");
        _ = sim.ExecuteNonQuery("create trigger tr on all server for ddl_event_session_events as insert master.dbo.log values (eventdata())");
        _ = sim.ExecuteNonQuery("create event session s on server add event sqlserver.error_reported");
        _ = sim.ExecuteNonQuery("drop event session s on server");
        AreEqual("CREATE_EVENT_SESSION:s:EVENT SESSION,DROP_EVENT_SESSION:s:EVENT SESSION", sim.ExecuteScalar("""
            select string_agg(concat(e.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), ':', e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'), ':', e.value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname')), ',')
            from master.dbo.log
            """));
    }

    [TestMethod]
    public void UntakenBranch_ResolvesNothing()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            if 1 = 0 create event session s on server add event sqlserver.nosuch;
            select count(*) from sys.server_event_sessions
            """));
}
