using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The events the simulator fires into running sessions — <c>error_reported</c>,
/// <c>sql_batch_starting</c> / <c>_completed</c>, <c>rpc_starting</c> /
/// <c>_completed</c> — and what the targets keep of them, in SQL Server 2025's
/// shapes (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class ExtendedEventsFiringTests
{
    /// <summary>A session's ring buffer, read on a connection the session's predicate leaves out.</summary>
    private static string RingBuffer(Simulation sim, string session = "s") => (string)sim.ExecuteScalar($"""
        select t.target_data from sys.dm_xe_session_targets t join sys.dm_xe_sessions s on s.address = t.event_session_address
        where s.name = '{session}' and t.target_name = 'ring_buffer'
        """)!;

    /// <summary>A connection that reports <paramref name="application"/> as its application name.</summary>
    private static DbConnection Client(Simulation sim, string application)
    {
        var connection = sim.CreateDbConnection();
        connection.ConnectionString = "Application Name=" + application;
        connection.Open();
        return connection;
    }

    private static Simulation WithSession(string body)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create event session s on server {body}");
        _ = sim.ExecuteNonQuery("alter event session s on server state = start");
        return sim;
    }

    private static void Run(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.NextResult())
            {
            }
        }
        catch (SimulatedSqlException)
        {
        }
    }

    [TestMethod]
    public void ErrorReported_WritesRealsFieldsAndActionsNewestFirst()
    {
        var sim = WithSession("""
            add event sqlserver.error_reported (action (sqlserver.session_id, sqlserver.sql_text, sqlserver.database_name) where sqlserver.client_app_name = N'app' and error_number <> 5701 and error_number <> 5703)
            add target package0.ring_buffer
            """);
        using var client = Client(sim, "app");
        Run(client, "select 1/0");
        var data = RingBuffer(sim);
        Contains("eventCount=\"1\"", data);
        Contains("<data name=\"error_number\"><type name=\"int32\" package=\"package0\"></type><value>8134</value></data><data name=\"severity\"><type name=\"int32\" package=\"package0\"></type><value>16</value></data>", data);
        Contains("<data name=\"category\"><type name=\"error_category\" package=\"sqlserver\"></type><value>2</value><text><![CDATA[SERVER]]></text></data><data name=\"destination\"><type name=\"error_destination\" package=\"sqlserver\"></type><value>0x00000002</value><text><![CDATA[USER]]></text></data>", data);
        Contains("<data name=\"is_intercepted\"><type name=\"boolean\" package=\"package0\"></type><value>false</value></data><data name=\"message\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[Divide by zero error encountered.]]></value></data>", data);
        Contains("<action name=\"database_name\" package=\"sqlserver\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[simulated]]></value></action><action name=\"sql_text\" package=\"sqlserver\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[select 1/0]]></value></action><action name=\"session_id\"", data);
    }

    // A TRY frame anywhere on the session intercepts the error; a severity-10
    // RAISERROR fires at its severity, a PRINT not at all.
    [TestMethod]
    public void ErrorReported_MarksInterceptionAndSkipsPrint()
    {
        var sim = WithSession("add event sqlserver.error_reported (where sqlserver.client_app_name = N'app' and error_number <> 5701 and error_number <> 5703) add target package0.ring_buffer");
        using var client = Client(sim, "app");
        Run(client, "begin try select 1/0 end try begin catch end catch; raiserror('ten', 10, 1); print 'p'");
        var data = RingBuffer(sim);
        Contains("eventCount=\"2\"", data);
        Contains("<value>true</value></data><data name=\"message\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[Divide by zero error encountered.]]>", data);
        Contains("<data name=\"severity\"><type name=\"int32\" package=\"package0\"></type><value>10</value></data>", data);
    }

    [TestMethod]
    public void Predicate_FiltersTheEvents()
    {
        var sim = WithSession("add event sqlserver.error_reported (where severity >= 16 and error_number <> 8134 and sqlserver.client_app_name = N'app') add target package0.ring_buffer");
        using var client = Client(sim, "app");
        Run(client, "select 1/0; raiserror('kept', 16, 1); raiserror('dropped', 11, 1)");
        var data = RingBuffer(sim);
        Contains("eventCount=\"1\"", data);
        Contains("<![CDATA[kept]]>", data);
    }

    // A batch reports Error only when an error ended it.
    [TestMethod]
    public void BatchEvents_CarryTheTextAndTheResult()
    {
        var sim = WithSession("""
            add event sqlserver.sql_batch_starting (where sqlserver.client_app_name = N'app'),
            add event sqlserver.sql_batch_completed (where sqlserver.client_app_name = N'app')
            add target package0.ring_buffer
            """);
        using var client = Client(sim, "app");
        Run(client, "select 1/0");
        Run(client, "select convert(int, 'x'); select 'after'");
        var data = RingBuffer(sim);
        Contains("eventCount=\"4\"", data);
        Contains("<event name=\"sql_batch_starting\" package=\"sqlserver\"", data);
        Contains("<data name=\"result\"><type name=\"rpc_return_result\" package=\"sqlserver\"></type><value>0</value><text><![CDATA[OK]]></text></data><data name=\"batch_text\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[select 1/0]]></value></data>", data);
        Contains("<value>1</value><text><![CDATA[Error]]></text></data><data name=\"batch_text\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[select convert(int, 'x'); select 'after']]></value>", data);
    }

    [TestMethod]
    public void RpcEvents_ReconstructTheCall()
    {
        var sim = WithSession("add event sqlserver.rpc_completed (where sqlserver.client_app_name = N'app') add target package0.ring_buffer");
        _ = sim.ExecuteNonQuery("create procedure dbo.p @a int as select @a");
        using var client = Client(sim, "app");
        using (var command = client.CreateCommand("select @p as v", ("@p", 1)))
            _ = command.ExecuteScalar();
        using (var call = client.CreateCommand("dbo.p", ("@a", 7)))
        {
            call.CommandType = System.Data.CommandType.StoredProcedure;
            _ = call.ExecuteScalar();
        }
        var data = RingBuffer(sim);
        Contains("<value><![CDATA[sp_executesql]]></value></data><data name=\"statement\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[exec sp_executesql N'select @p as v',N'@p int',@p=1]]></value>", data);
        Contains("<value><![CDATA[p]]></value></data><data name=\"statement\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[exec dbo.p @a=7]]></value>", data);
    }

    [TestMethod]
    public void RingBuffer_KeepsTheNewestUpToItsLimit()
    {
        var sim = WithSession("add event sqlserver.error_reported (where sqlserver.client_app_name = N'app' and error_number <> 5701 and error_number <> 5703) add target package0.ring_buffer (set max_events_limit = 2)");
        using var client = Client(sim, "app");
        Run(client, "raiserror('one', 16, 1); raiserror('two', 16, 1); raiserror('three', 16, 1)");
        var data = RingBuffer(sim);
        StartsWith("<RingBufferTarget truncated=\"0\" processingTime=\"0\" totalEventsProcessed=\"3\" eventCount=\"2\"", data);
        DoesNotContain("<![CDATA[one]]>", data);
        Contains("<![CDATA[three]]>", data);
    }

    [TestMethod]
    public void CounterAndHistogram_CountTheEvents()
    {
        var sim = WithSession("""
            add event sqlserver.error_reported (where sqlserver.client_app_name = N'app' and error_number <> 5701 and error_number <> 5703)
            add target package0.event_counter, add target package0.histogram (set source = N'sqlserver.client_app_name')
            """);
        using var client = Client(sim, "app");
        Run(client, "raiserror('one', 16, 1); raiserror('two', 16, 1)");
        Contains("<Package name=\"sqlserver\"><Event name=\"error_reported\" count=\"2\"></Event></Package>", (string)sim.ExecuteScalar("select t.target_data from sys.dm_xe_session_targets t where t.target_name = 'event_counter'")!);
        AreEqual("<HistogramTarget truncated=\"0\" buckets=\"256\"><Slot count=\"2\"><value>app</value></Slot></HistogramTarget>", sim.ExecuteScalar("select t.target_data from sys.dm_xe_session_targets t where t.target_name = 'histogram'"));
    }

    // An event file's events read back without their types, and the file
    // outlives the session.
    [TestMethod]
    public void EventFile_ReadsBackThroughFnXeFileTargetReadFile()
    {
        var sim = WithSession("add event sqlserver.error_reported (where sqlserver.client_app_name = N'app' and error_number <> 5701 and error_number <> 5703) add target package0.event_file (set filename = N'f.xel')");
        using (var client = Client(sim, "app"))
            Run(client, "raiserror('logged', 16, 1)");
        _ = sim.ExecuteNonQuery("drop event session s on server");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fn_xe_file_target_read_file(N'f*.xel', null, null, null)"));
        AreEqual("error_reported:16384", sim.ExecuteScalar("select concat(object_name, ':', file_offset) from sys.fn_xe_file_target_read_file(N'/var/opt/mssql/log/f*.xel', null, null, null)"));
        var xml = (string)sim.ExecuteScalar("select event_data from sys.fn_xe_file_target_read_file(N'f*.xel', null, null, null)")!;
        Contains("<data name=\"error_number\"><value>50000</value></data><data name=\"severity\"><value>16</value></data>", xml);
        AreEqual("ce79811f-1a80-40e1-8f5d-7445a3f375e7", sim.ExecuteScalar("select lower(cast(module_guid as nvarchar(36))) from sys.fn_xe_file_target_read_file(N'f*.xel', null, null, null)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.fn_xe_file_target_read_file(N'nosuch.xel', null, null, null)"));
    }

    [DataRow("select * from sys.fn_xe_file_target_read_file(null, null, null, null)", 25718, 3)]
    [DataRow("select * from sys.fn_xe_file_target_read_file(N'f*.xel', null, N'x', null)", 25719, 4)]
    [DataRow("select * from sys.fn_xe_file_target_read_file(N'f*.xel', null, null, 5)", 25719, 4)]
    [DataRow("select * from sys.fn_xe_file_target_read_file(N'f*.xel')", 313, 3)]
    [TestMethod]
    public void FnXeFileTargetReadFile_RefusesAsRealDoes(string statement, int number, int state)
        => AreEqual(state, new Simulation().AssertSqlError(statement, number).State);

    // system_health takes an error of severity 20 or more into its ring
    // buffer, as real's does from the server's start.
    /// <summary>Each event's name and the values of the named fields in the event's own order, events in firing order.</summary>
    private static List<string> Summaries(string data, params string[] fields)
    {
        var document = System.Xml.Linq.XDocument.Parse(data);
        var summaries = new List<string>();
        foreach (var @event in document.Root!.Elements("event"))
        {
            var parts = new List<string> { (string)@event.Attribute("name")! };
            foreach (var field in @event.Elements("data"))
            {
                if (fields.Contains((string?)field.Attribute("name")))
                    parts.Add((string?)field.Attribute("name") + "=" + (string?)field.Element("value"));
            }
            summaries.Add(string.Join(' ', parts));
        }
        return summaries;
    }

    // The statement events of a batch calling a procedure, every field
    // placed as SQL Server 2025 placed them for the same batch: a query ends
    // at its last character, a DECLARE at the line break after it, the call's
    // row count the sum of its body's.
    [TestMethod]
    public void StatementEvents_PlaceEachStatementAndModule()
    {
        var sim = WithSession("""
            add event sqlserver.sql_statement_starting (where sqlserver.client_app_name = N'app'),
            add event sqlserver.sql_statement_completed (where sqlserver.client_app_name = N'app'),
            add event sqlserver.sp_statement_starting (where sqlserver.client_app_name = N'app'),
            add event sqlserver.sp_statement_completed (where sqlserver.client_app_name = N'app'),
            add event sqlserver.module_start (where sqlserver.client_app_name = N'app'),
            add event sqlserver.module_end (where sqlserver.client_app_name = N'app')
            add target package0.ring_buffer
            """);
        sim.ExecuteBatches(
            "create table k (id int primary key, v int); insert k values (1, 0), (2, 0)",
            "create procedure dbo.p2 @x int = 3 as\nbegin\n  select @x as x;\n  update dbo.k set v = v where id = @x;\nend");
        using var client = Client(sim, "app");
        Run(client, "select 1 as a;\nexec dbo.p2 2;\ndeclare @y int = 5\nselect @y from dbo.k where id = 1");
        AreEqual(string.Join("\n", new[]
        {
            "sql_statement_starting line_number=1 offset=0 offset_end=24 statement=select 1 as a",
            "sql_statement_completed row_count=1 last_row_count=1 line_number=1 offset=0 offset_end=24 statement=select 1 as a",
            "sql_statement_starting line_number=2 offset=30 offset_end=54 statement=exec dbo.p2 2",
            "module_start line_number=2 offset=30 offset_end=54 object_type=P  object_name=p2 statement=",
            "sp_statement_starting object_type=8272 nest_level=1 line_number=3 offset=92 offset_end=118 object_name= statement=select @x as x",
            "sp_statement_completed object_type=8272 row_count=1 last_row_count=1 nest_level=1 line_number=3 offset=92 offset_end=118 object_name= statement=select @x as x",
            "sp_statement_starting object_type=8272 nest_level=1 line_number=4 offset=128 offset_end=198 object_name= statement=update dbo.k set v = v where id = @x",
            "sp_statement_completed object_type=8272 row_count=1 last_row_count=1 nest_level=1 line_number=4 offset=128 offset_end=198 object_name= statement=update dbo.k set v = v where id = @x",
            "module_end row_count=2 line_number=2 offset=30 offset_end=54 object_type=P  object_name=p2 statement=",
            "sql_statement_completed row_count=2 last_row_count=1 line_number=2 offset=30 offset_end=54 statement=exec dbo.p2 2",
            "sql_statement_starting line_number=3 offset=60 offset_end=96 statement=declare @y int = 5\n",
            "sql_statement_completed row_count=1 last_row_count=1 line_number=3 offset=60 offset_end=96 statement=declare @y int = 5\n",
            "sql_statement_starting line_number=4 offset=98 offset_end=162 statement=select @y from dbo.k where id = 1",
            "sql_statement_completed row_count=1 last_row_count=1 line_number=4 offset=98 offset_end=162 statement=select @y from dbo.k where id = 1",
        }), string.Join("\n", Summaries(RingBuffer(sim), "row_count", "last_row_count", "object_type", "nest_level", "line_number", "offset", "offset_end", "object_name", "statement")));
    }

    // A session option and PRINT leave last_row_count standing, a lone
    // non-query statement runs to its batch's end (-1), and a DECLARE
    // initializing nothing fires no event.
    [TestMethod]
    public void StatementEvents_KeepTheLastRowCountAndSkipABareDeclare()
    {
        var sim = WithSession("add event sqlserver.sql_statement_completed (where sqlserver.client_app_name = N'app') add target package0.ring_buffer");
        using var client = Client(sim, "app");
        Run(client, "select 1\nset nocount on\ndeclare @a int\nprint 'x'\nselect 5");
        Run(client, "set textsize 100");
        AreEqual(string.Join("\n", new[]
        {
            "sql_statement_completed row_count=1 last_row_count=1 offset=0 offset_end=14",
            "sql_statement_completed row_count=0 last_row_count=1 offset=18 offset_end=46",
            "sql_statement_completed row_count=0 last_row_count=1 offset=78 offset_end=96",
            "sql_statement_completed row_count=1 last_row_count=1 offset=98 offset_end=112",
            "sql_statement_completed row_count=0 last_row_count=1 offset=0 offset_end=-1",
        }), string.Join("\n", Summaries(RingBuffer(sim), "row_count", "last_row_count", "offset", "offset_end")));
    }

    // A batch the plan cache replays fires its statements' events as its
    // first run did.
    [TestMethod]
    public void StatementEvents_FireForACachedBatch()
    {
        var sim = WithSession("add event sqlserver.sql_statement_completed (where sqlserver.client_app_name = N'app') add target package0.ring_buffer");
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int); insert t values (1, 0)");
        using var client = Client(sim, "app");
        for (var i = 0; i < 3; i++)
            Run(client, "update t set v = v + 1 where id = 1; select v from t where id = 1");
        Run(client, "declare @i int = 0; while @i < 3 begin set @i += 1; update t set v = @i where id = 1 end");
        var summaries = Summaries(RingBuffer(sim), "statement");
        AreEqual(6, summaries.Count(summary => summary.StartsWith("sql_statement_completed statement=update t set v = v + 1", StringComparison.Ordinal) || summary.StartsWith("sql_statement_completed statement=select v", StringComparison.Ordinal)));
        AreEqual(3, summaries.Count(summary => summary == "sql_statement_completed statement=set @i += 1; "));
        AreEqual(3, summaries.Count(summary => summary == "sql_statement_completed statement=update t set v = @i where id = 1"));
    }

    // A login reports its database and language changes as error_reported
    // first, as real's does; the session's end is a logout.
    [TestMethod]
    public void Login_FollowsItsMessagesAndEndsInALogout()
    {
        var sim = WithSession("""
            add event sqlserver.error_reported (where sqlserver.client_app_name = N'app'),
            add event sqlserver.login (set collect_options_text = 1 where sqlserver.client_app_name = N'app'),
            add event sqlserver.logout (where sqlserver.client_app_name = N'app')
            add target package0.ring_buffer
            """);
        using (Client(sim, "app"))
        {
        }
        var summaries = Summaries(RingBuffer(sim), "error_number", "state", "is_cached", "database_id", "packet_size");
        AreEqual(string.Join("\n",
            "error_reported error_number=5701 state=2",
            "error_reported error_number=5703 state=1",
            "login is_cached=false database_id=5 packet_size=4096",
            "logout is_cached=false"), string.Join("\n", summaries));
        Contains("<data name=\"options_text\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[-- network protocol: TCP/IP\nset quoted_identifier on\nset arithabort off\nset numeric_roundabort off\nset ansi_warnings on\nset ansi_padding on\nset ansi_nulls on\nset concat_null_yields_null on\nset cursor_close_on_commit off\nset implicit_transactions off\nset language us_english\nset dateformat mdy\nset datefirst 7\nset transaction isolation level read committed\n]]></value></data>", RingBuffer(sim));
    }

    // Starting a session reports each open session to it alone, by the
    // open session's own fields.
    [TestMethod]
    public void ExistingConnection_ReportsTheOpenSessionsAsTheSessionStarts()
    {
        var sim = new Simulation();
        using var client = Client(sim, "app");
        Run(client, "begin tran");
        _ = sim.ExecuteNonQuery("""
            create event session s on server add event sqlserver.existing_connection (where client_app_name = N'app') add target package0.ring_buffer;
            alter event session s on server state = start
            """);
        var data = RingBuffer(sim);
        HasCount(1, Summaries(data));
        Contains("<data name=\"transaction_count\"><type name=\"uint32\" package=\"package0\"></type><value>1</value></data>", data);
        Contains("<data name=\"started_event_session_name\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[s]]></value></data>", data);
        Contains("<data name=\"client_app_name\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[app]]></value></data>", data);
        Contains("<data name=\"server_principal_name\"><type name=\"unicode_string\" package=\"package0\"></type><value><![CDATA[sa]]></value></data>", data);
    }

    // A WAITFOR's wait ends timed out, a lock wait's granted, each with its
    // wait type and its duration in milliseconds.
    [TestMethod]
    public async Task WaitEvents_ReportWaitForAndLockWaits()
    {
        var sim = WithSession("""
            add event sqlos.wait_info (where sqlserver.client_app_name = N'app'),
            add event sqlos.wait_completed (where sqlserver.client_app_name = N'app')
            add target package0.ring_buffer
            """);
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int); insert t values (1, 0)");
        using var client = Client(sim, "app");
        Run(client, "waitfor delay '00:00:00.100'");
        using var holder = sim.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; update t set v = 1 where id = 1").ExecuteNonQuery();
        var read = Task.Run(() => Run(client, "select v from t where id = 1"), this.TestContext.CancellationToken);
        _ = await Extensions.PollUntil(
            () => sim.ExecuteScalar("select count(*) from sys.dm_exec_requests where blocking_session_id <> 0"),
            count => count is 1,
            this.TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();
        await read.WaitAsync(TimeSpan.FromSeconds(10), this.TestContext.CancellationToken);
        var summaries = Summaries(RingBuffer(sim), "wait_type", "opcode", "wait_result");
        AreEqual(string.Join("\n",
            "wait_info wait_type=308 opcode=1",
            "wait_completed wait_type=308 wait_result=258",
            "wait_info wait_type=3 opcode=1",
            "wait_completed wait_type=3 wait_result=0"), string.Join("\n", summaries));
        Contains("<text><![CDATA[WAITFOR]]></text>", RingBuffer(sim));
        Contains("<text><![CDATA[LCK_M_S]]></text>", RingBuffer(sim));
    }

    // A command timeout ends its request with an attention.
    [TestMethod]
    public void Attention_FiresAsACommandTimeoutEndsTheRequest()
    {
        var sim = WithSession("add event sqlserver.attention (where sqlserver.client_app_name = N'app') add target package0.ring_buffer");
        using var client = Client(sim, "app");
        using var command = client.CreateCommand("waitfor delay '00:00:05'");
        command.CommandTimeout = 1;
        _ = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        AreEqual("attention request_id=0", string.Join("\n", Summaries(RingBuffer(sim), "request_id")));
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SystemHealth_TakesSevereErrors()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("raiserror('severe', 20, 1) with log", 50000);
        _ = sim.AssertSqlError("raiserror('ordinary', 16, 1)", 50000);
        var data = RingBuffer(sim, "system_health");
        Contains("<![CDATA[severe]]>", data);
        DoesNotContain("<![CDATA[ordinary]]>", data);
    }
}
