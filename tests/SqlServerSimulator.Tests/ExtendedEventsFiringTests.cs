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
            add event sqlserver.error_reported (action (sqlserver.session_id, sqlserver.sql_text, sqlserver.database_name) where sqlserver.client_app_name = N'app')
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
        var sim = WithSession("add event sqlserver.error_reported (where sqlserver.client_app_name = N'app') add target package0.ring_buffer");
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
        var sim = WithSession("add event sqlserver.error_reported (where sqlserver.client_app_name = N'app') add target package0.ring_buffer (set max_events_limit = 2)");
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
            add event sqlserver.error_reported (where sqlserver.client_app_name = N'app')
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
        var sim = WithSession("add event sqlserver.error_reported (where sqlserver.client_app_name = N'app') add target package0.event_file (set filename = N'f.xel')");
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
