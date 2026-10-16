using System.Data.Common;
using System.Text.RegularExpressions;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a deadlock reports: <c>xml_deadlock_report</c>'s graph, which
/// <c>system_health</c> keeps, <c>lock_deadlock</c> and
/// <c>lock_deadlock_chain</c>, and trace flags 1204 and 1222's error-log
/// reports, in SQL Server 2025's shapes (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed partial class ExtendedEventsDeadlockTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string RingBuffer(Simulation sim, string session) => (string)sim.ExecuteScalar($"""
        select t.target_data from sys.dm_xe_session_targets t join sys.dm_xe_sessions s on s.address = t.event_session_address
        where s.name = '{session}' and t.target_name = 'ring_buffer'
        """)!;

    private static int? Run(DbConnection connection, string sql)
    {
        try
        {
            _ = connection.CreateCommand(sql).ExecuteNonQuery();
            return null;
        }
        catch (SimulatedSqlException ex)
        {
            return ex.Number;
        }
    }

    /// <summary>
    /// Deadlocks the sessions of <paramref name="steps"/>: each runs its hold,
    /// then its wait — the waits in order, each blocked before the next is
    /// sent — so the last session, the requester closing the cycle, is the
    /// victim. Returns each session's error, null for those that went on.
    /// </summary>
    private async Task<int?[]> Deadlock(Simulation sim, params (string Hold, string Wait)[] steps)
    {
        var connections = steps.Select(_ => sim.CreateOpenConnection()).ToArray();
        try
        {
            for (var i = 0; i < steps.Length; i++)
                _ = connections[i].CreateCommand(steps[i].Hold).ExecuteNonQuery();
            var tasks = new List<Task<int?>>();
            for (var i = 0; i < steps.Length; i++)
            {
                var (connection, wait, blocked) = (connections[i], steps[i].Wait, i + 1);
                tasks.Add(Task.Run(() => Run(connection, wait), this.TestContext.CancellationToken));
                if (blocked < steps.Length)
                {
                    _ = await Extensions.PollUntil(
                        () => sim.ExecuteScalar("select count(*) from sys.dm_exec_requests where blocking_session_id <> 0"),
                        count => count is int waiting && waiting == blocked,
                        this.TestContext.CancellationToken);
                }
            }
            return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), this.TestContext.CancellationToken);
        }
        finally
        {
            foreach (var connection in connections)
                connection.Dispose();
        }
    }

    [GeneratedRegex("<process id=\"(process[0-9a-f]+)\"")]
    private static partial Regex ProcessIdPattern();

    [GeneratedRegex("<owner id=\"(process[0-9a-f]+)\"")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("<waiter id=\"(process[0-9a-f]+)\"")]
    private static partial Regex WaiterPattern();

    [GeneratedRegex("<event name=\"(\\w+)\"")]
    private static partial Regex EventNamePattern();

    [GeneratedRegex("<keylock hobtid=\"(\\d+)\" dbid=\"5\" objectname=\"simulated.dbo.k\" indexname=\"PK__k__[0-9A-F]+\" id=\"lock[0-9a-f]+\" mode=\"X\" associatedObjectId=\"\\1\">")]
    private static partial Regex KeyLockPattern();

    private static string[] Matches(Regex pattern, string data) => [.. pattern.Matches(data).Select(match => match.Groups[1].Value)];

    private static string Spid(string data, string processId) =>
        Regex.Match(data, $"<process id=\"{processId}\"[^>]* spid=\"(\\d+)\"").Groups[1].Value;

    // The victim leads the process list; each resource is the one its
    // process waits on, owned by the process before it — the victim's by the
    // last; and the running batch statement is "unknown", behind the
    // prepared text simple parameterization runs it as.
    [TestMethod]
    public async Task SystemHealth_KeepsTheDeadlockGraph()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table k (id int primary key, v int); insert k values (1, 0), (2, 0)");
        var errors = await this.Deadlock(sim,
            ("begin tran; update k set v = 1 where id = 1", "update k set v = 1 where id = 2"),
            ("begin tran; update k set v = 2 where id = 2", "update k set v = 2 where id = 1"));
        CollectionAssert.AreEqual(new int?[] { null, 1205 }, errors);
        var data = RingBuffer(sim, "system_health");
        var ids = Matches(ProcessIdPattern(), data);
        HasCount(2, ids);
        Contains($"<data name=\"xml_report\"><type name=\"xml\" package=\"package0\"></type><value><deadlock>\n <victim-list>\n  <victimProcess id=\"{ids[0]}\"/>\n </victim-list>\n <process-list>\n  <process id=\"{ids[0]}\" taskpriority=\"0\"", data);
        AreEqual("53", Spid(data, ids[0]));
        Contains("transactionname=\"user_transaction\"", data);
        Contains("lockMode=\"X\" schedulerid=\"1\"", data);
        Contains("status=\"suspended\" spid=\"53\" sbid=\"0\" ecid=\"0\" priority=\"0\" trancount=\"2\"", data);
        Contains("loginname=\"sa\" isolationlevel=\"read committed (2)\"", data);
        Contains("currentdbname=\"simulated\" lockTimeout=\"4294967295\"", data);
        Contains("<frame procname=\"adhoc\" line=\"1\" stmtstart=\"38\" stmtend=\"112\" sqlhandle=\"0x02", data);
        Contains("\">\nunknown    </frame>\n    <frame procname=\"adhoc\" line=\"1\" stmtend=\"60\" sqlhandle=\"0x02", data);
        Contains("   </executionStack>\n   <stackFrames>\n      \n      </stackFrames>\n   <inputbuf>\nupdate k set v = 2 where id = 1   </inputbuf>\n  </process>", data);
        Contains($"<owner-list>\n    <owner id=\"{ids[1]}\" mode=\"X\"/>\n   </owner-list>\n   <waiter-list>\n    <waiter id=\"{ids[0]}\" mode=\"X\" requestType=\"wait\"/>\n   </waiter-list>\n  </keylock>\n  <keylock", data);
        Contains($"<owner-list>\n    <owner id=\"{ids[0]}\" mode=\"X\"/>\n   </owner-list>\n   <waiter-list>\n    <waiter id=\"{ids[1]}\" mode=\"X\" requestType=\"wait\"/>\n   </waiter-list>\n  </keylock>\n </resource-list>\n</deadlock>\n</value></data></event>", data);
        Contains("waitresource=\"KEY: 5:", data);
        HasCount(2, KeyLockPattern().Matches(data));
        // The file target keeps it too, as monitoring tools read it.
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fn_xe_file_target_read_file('system_health*.xel', null, null, null) where object_name = 'xml_deadlock_report'"));
    }

    // A server event notification for DEADLOCK_GRAPH gets the graph in the
    // trace's <deadlock-list> form, the victim named on <deadlock>, from the
    // deadlock monitor's session.
    [TestMethod]
    public async Task DeadlockGraph_ReachesAnEventNotification()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table k (id int primary key, v int); insert k values (1, 0), (2, 0)");
        var instance = sim.ExecuteScalar("select cast(service_broker_guid as nvarchar(36)) from sys.databases where name = db_name()").ToInvariantString();
        _ = sim.ExecuteNonQuery($"""
            create queue enq;
            create service ens on queue enq ([http://schemas.microsoft.com/SQL/Notifications/PostEventNotification]);
            create event notification dl on server for deadlock_graph to service 'ens', '{instance}';
            """);
        _ = await this.Deadlock(sim,
            ("begin tran; update k set v = 1 where id = 1", "update k set v = 1 where id = 2; commit"),
            ("begin tran; update k set v = 2 where id = 2", "update k set v = 2 where id = 1"));
        var body = (string)sim.ExecuteScalar("select cast(message_body as nvarchar(max)) from enq")!;
        StartsWith("\uFEFF<EVENT_INSTANCE><EventType>DEADLOCK_GRAPH</EventType>", body);
        Contains("<SPID>3</SPID><TextData><deadlock-list>\n <deadlock victim=\"process", body);
        Contains("\n  <process-list>\n   <process id=", body);
        Contains("</resource-list>\n </deadlock>\n</deadlock-list>\n</TextData><TransactionID />", body);
        DoesNotContain("victim-list", body);
    }

    // Three sessions: the victim, then the session waiting on it, then the
    // one that session waits on.
    [TestMethod]
    public async Task ThreeSessionCycle_ListsTheProcessesAgainstTheWaits()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table k (id int primary key, v int); insert k values (1, 0), (2, 0), (3, 0)");
        var errors = await this.Deadlock(sim,
            ("begin tran; update k set v = 1 where id = 1", "update k set v = 1 where id = 2; commit"),
            ("begin tran; update k set v = 2 where id = 2", "update k set v = 2 where id = 3; commit"),
            ("begin tran; update k set v = 3 where id = 3", "update k set v = 3 where id = 1"));
        CollectionAssert.AreEqual(new int?[] { null, null, 1205 }, errors);
        var data = RingBuffer(sim, "system_health");
        var ids = Matches(ProcessIdPattern(), data);
        CollectionAssert.AreEqual(new[] { "54", "53", "52" }, ids.Select(id => Spid(data, id)).ToArray());
        CollectionAssert.AreEqual(ids, Matches(WaiterPattern(), data));
        CollectionAssert.AreEqual(new[] { ids[2], ids[0], ids[1] }, Matches(OwnerPattern(), data));
    }

    // A procedure's frame names it, its statement's line in the CREATE and
    // offsets into it, and its text one character short; the batch calling
    // it gives its own text under its legacy handle.
    [TestMethod]
    public async Task ProcedureFrames_NameTheModuleAndItsStatement()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table h (id int, v int); create table h2 (id int, v int); insert h values (1, 0); insert h2 values (1, 0)",
            """
            create procedure p @first int as
            begin
              if @first = 1 update h set v = 1 where id = 1; else update h2 set v = 1 where id = 1;
            end
            """);
        var errors = await this.Deadlock(sim,
            ("begin tran; exec p 1", "update h2 set v = 1 where id = 1"),
            ("begin tran; exec p 2", "declare @x int = 1\nexec p @x"));
        CollectionAssert.AreEqual(new int?[] { null, 1205 }, errors);
        var data = RingBuffer(sim, "system_health");
        Contains("<frame procname=\"simulated.dbo.p\" line=\"3\" stmtstart=\"110\" stmtend=\"170\" sqlhandle=\"0x03000500", data);
        Contains("\">\nupdate h set v = 1 where id =     </frame>\n    <frame procname=\"adhoc\" line=\"2\" stmtstart=\"38\" stmtend=\"54\" sqlhandle=\"0x01000500", data);
        Contains("\">\nexec p @    </frame>\n   </executionStack>", data);
        Contains("<inputbuf>\ndeclare @x int = 1\nexec p @x   </inputbuf>", data);
        Contains("waitresource=\"RID: 5:1:", data);
        Contains("lockMode=\"U\"", data);
        Contains("<ridlock fileid=\"1\" pageid=\"", data);
        Contains("objectname=\"simulated.dbo.h\" id=\"lock", data);
        Contains("<waiter id=\"" + Matches(ProcessIdPattern(), data)[0] + "\" mode=\"U\" requestType=\"wait\"/>", data);
    }

    // lock_deadlock_chain for each process in the graph's order, then
    // lock_deadlock for the victim, its wait's duration with it.
    [TestMethod]
    public async Task LockDeadlockEvents_FollowTheCycle()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table k (id int primary key, v int); insert k values (1, 0), (2, 0);
            create event session s on server add event sqlserver.lock_deadlock (action (sqlserver.session_id)), add event sqlserver.lock_deadlock_chain (action (sqlserver.session_id))
            add target package0.ring_buffer;
            alter event session s on server state = start
            """);
        _ = await this.Deadlock(sim,
            ("begin tran; update k set v = 1 where id = 1", "update k set v = 1 where id = 2"),
            ("begin tran; update k set v = 2 where id = 2", "update k set v = 2 where id = 1"));
        var data = RingBuffer(sim, "s");
        CollectionAssert.AreEqual(new[] { "lock_deadlock_chain", "lock_deadlock_chain", "lock_deadlock" }, Matches(EventNamePattern(), data));
        Contains("<data name=\"resource_type\"><type name=\"lock_resource_type\" package=\"sqlserver\"></type><value>7</value><text><![CDATA[KEY]]></text></data><data name=\"mode\"><type name=\"lock_mode\" package=\"sqlserver\"></type><value>5</value><text><![CDATA[X]]></text></data><data name=\"owner_type\"><type name=\"lock_owner_type\" package=\"sqlserver\"></type><value>1</value><text><![CDATA[Transaction]]></text></data>", data);
        Contains("<data name=\"deadlock_id\"><type name=\"uint32\" package=\"package0\"></type><value>1</value></data>", data);
        Contains("<data name=\"session_id\"><type name=\"int16\" package=\"package0\"></type><value>53</value></data><data name=\"resource_owner_type\"", data);
        Contains("<data name=\"session_id\"><type name=\"int16\" package=\"package0\"></type><value>52</value></data><data name=\"resource_owner_type\"", data);
        // The chain reports the lock monitor's session, lock_deadlock the victim's.
        Contains("<action name=\"session_id\" package=\"sqlserver\"><type name=\"uint16\" package=\"package0\"></type><value>44</value></action>", data);
        Contains("<data name=\"duration\"><type name=\"uint64\" package=\"package0\"></type><value>", data);
        EndsWith("<action name=\"session_id\" package=\"sqlserver\"><type name=\"uint16\" package=\"package0\"></type><value>53</value></action></event></RingBufferTarget>", data);
    }

    // A wait past the blocked process threshold reports itself and the
    // session holding what it waits for, once per threshold passed.
    [TestMethod]
    public async Task BlockedProcessReport_FiresPastTheThreshold()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table k (id int primary key, v int); insert k values (1, 0);
            exec sp_configure 'show advanced options', 1; reconfigure;
            exec sp_configure 'blocked process threshold', 1; reconfigure;
            create event session s on server add event sqlserver.blocked_process_report add target package0.ring_buffer;
            alter event session s on server state = start
            """);
        using var holder = sim.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; update k set v = 1 where id = 1").ExecuteNonQuery();
        using var reader = sim.CreateOpenConnection();
        var read = Task.Run(() => Run(reader, "set lock_timeout 2300; select v from k where id = 1"), this.TestContext.CancellationToken);
        AreEqual(1222, await read.WaitAsync(TimeSpan.FromSeconds(10), this.TestContext.CancellationToken));
        var data = RingBuffer(sim, "s");
        HasCount(2, Matches(EventNamePattern(), data));
        Contains("<data name=\"lock_mode\"><type name=\"lock_mode\" package=\"sqlserver\"></type><value>3</value><text><![CDATA[S]]></text></data>", data);
        Contains("<data name=\"blocked_process\"><type name=\"xml\" package=\"package0\"></type><value><blocked-process-report monitorLoop=\"1\">\n <blocked-process>\n  <process id=\"process", data);
        Contains("transactionname=\"SELECT\"", data);
        Contains("lockMode=\"S\"", data);
        Contains("status=\"suspended\" spid=\"53\" sbid=\"0\" ecid=\"0\" priority=\"0\" trancount=\"0\"", data);
        Contains("   <executionStack>\n    <frame line=\"1\" stmtstart=\"", data);
        Contains("<inputbuf>\nset lock_timeout 2300; select v from k where id = 1   </inputbuf>\n  </process>\n </blocked-process>\n <blocking-process>\n  <process status=\"sleeping\" waittime=\"0\" spid=\"52\" sbid=\"0\" ecid=\"0\" priority=\"0\" trancount=\"1\" lastbatchstarted=\"", data);
        Contains("   <executionStack>\n   </executionStack>\n   <stackFrames>\n      \n      </stackFrames>\n   <inputbuf>\nbegin tran; update k set v = 1 where id = 1   </inputbuf>\n  </process>\n </blocking-process>\n</blocked-process-report>\n</value></data><data name=\"database_name\"", data);
        Contains("monitorLoop=\"2\"", data);
    }

    private static List<(string Process, string Text)> ErrorLog(Simulation sim)
    {
        var lines = new List<(string, string)>();
        using var reader = sim.CreateCommand("exec xp_readerrorlog").ExecuteReader();
        while (reader.Read())
            lines.Add((reader.GetString(1), reader.GetString(2)));
        return lines;
    }

    // Trace flags 1204 and 1222, on server-wide, write their reports under
    // the lock monitor's sessions; on for a session alone, nothing.
    [TestMethod]
    [DataRow("dbcc traceon (1204, 1222, -1)", true)]
    [DataRow("dbcc traceon (1204, 1222)", false)]
    public async Task TraceFlags_WriteTheReportsToTheErrorLog(string traceOn, bool written)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table k (id int primary key, v int); insert k values (1, 0), (2, 0)");
        _ = await this.Deadlock(sim,
            ("begin tran; update k set v = 1 where id = 1", "update k set v = 1 where id = 2"),
            (traceOn + "; begin tran; update k set v = 2 where id = 2", "update k set v = 2 where id = 1"));
        var log = ErrorLog(sim);
        var report1204 = log.Where(line => line.Process == "spid44s").Select(line => line.Text).ToArray();
        var report1222 = log.Where(line => line.Process == "spid3s").Select(line => line.Text.TrimEnd('\r', '\n')).ToArray();
        if (!written)
        {
            IsEmpty(report1204);
            IsEmpty(report1222);
            return;
        }
        CollectionAssert.AreEqual(new[] { "Deadlock encountered .... Printing deadlock information\n", "Wait-for graph\n", "\n", "Node:1\n" }, report1204[..4]);
        StartsWith("KEY: 5:", report1204[4]);
        EndsWith(" CleanCnt:2 Mode:X Flags: 0x1", report1204[4]);
        AreEqual(" Grant List 0:", report1204[5]);
        Contains(" Mode: X        Flg:0x40 Ref:0 Life:02000000 SPID:52 ECID:0 XactLockInfo: 0x", report1204[6]);
        AreEqual("   SPID: 52 ECID: 0 Statement Type: UPDATE Line #: 1", report1204[7]);
        AreEqual("   Input Buf: Language Event: update k set v = 1 where id = 2", report1204[8]);
        AreEqual("Requested by: \n", report1204[9]);
        StartsWith("  ResType:LockOwner Stype:'OR'Xdes:0x", report1204[10]);
        Contains(" Mode: X SPID:53 BatchID:0 ECID:0 TaskProxy:(0x", report1204[10]);
        AreEqual("Victim Resource Owner:\n", report1204[^2]);
        StartsWith(" ResType:LockOwner", report1204[^1]);

        AreEqual("deadlock-list", report1222[0]);
        StartsWith(" deadlock victim=process", report1222[1]);
        AreEqual("  process-list", report1222[2]);
        StartsWith("   process id=process", report1222[3]);
        Contains(" spid=53 sbid=0 ecid=0 priority=0 trancount=2 ", report1222[3]);
        Contains(" isolationlevel=read committed (2) ", report1222[3]);
        AreEqual("    executionStack", report1222[4]);
        StartsWith("     frame procname=adhoc line=1 stmtstart=38 stmtend=112 sqlhandle=0x02", report1222[5]);
        AreEqual("unknown     ", report1222[6]);
        CollectionAssert.Contains(report1222, "update k set v = 2 where id = 1    ");
        CollectionAssert.Contains(report1222, "  resource-list");
        CollectionAssert.Contains(report1222, "    owner-list");
        StartsWith("     waiter id=process", report1222[^1]);
        EndsWith(" mode=X requestType=wait", report1222[^1]);
    }
}
