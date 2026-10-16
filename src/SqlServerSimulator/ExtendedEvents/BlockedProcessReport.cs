using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// <c>blocked_process_report</c>: a lock wait past each multiple of
/// <c>sp_configure 'blocked process threshold'</c> reports the waiting process
/// and the one blocking it (probed 2026-10-16 against SQL Server 2025, whose
/// lock monitor reported a five-second threshold's wait twice in twelve
/// seconds). The waiter reports itself between its wait's slices, where real's
/// lock monitor looks every few seconds.
/// </summary>
internal static class BlockedProcessReport
{
    public static void Report(Simulation simulation, SessionToken waiter, LockResource resource, LockMode mode)
    {
        if (waiter.TryResolveActing() is not { } connection || connection.RunningBatch is not { } batch)
            return;
        var waited = LockDmvs.WaitedLock(batch, connection);
        var blocked = new ReportedProcess(connection, waiter, waited is { } row ? LockDmvs.WaitResourceOf(row) : "", mode, waiter.WaitStartedTicks);
        SimulatedDbConnection? blocker = null;
        if (LockDmvs.FindFirstBlocker(resource, connection) is { } blockingSpid)
        {
            foreach (var candidate in simulation.SnapshotConnections())
            {
                if (candidate.Spid == blockingSpid)
                    blocker = candidate;
            }
        }
        var loop = Interlocked.Increment(ref simulation.BlockedProcessReportCount);
        var (_, r0, _, _) = DeadlockReport.ResourceKey(waited);
        XeEvents.Fire(connection, XeEventKind.BlockedProcessReport, @event => XeEvents.Data(@event,
            ("duration", XeValue.FromUnsigned((ulong)(blocked.WaitTime * 1000))),
            ("database_id", XeValue.FromSigned(waited?.DatabaseId ?? connection.CurrentDatabase.Id)),
            ("object_id", XeValue.FromSigned((long)r0)),
            ("index_id", XeValue.FromUnsigned(waited is { Type: "KEY" or "RID" or "PAGE" } keyed ? (ulong)(keyed.EntityId >> 48) : 0)),
            ("lock_mode", XeValue.FromMap(DeadlockReport.ModeKey(mode), null)),
            ("transaction_id", XeValue.FromSigned(blocked.TransactionId)),
            ("resource_owner_type", XeValue.FromMap(1, null)),
            ("blocked_process", XeValue.FromXml(Xml(loop, blocked, blocker))),
            ("database_name", XeValue.FromUnicode(connection.CurrentDatabase.Name))), systemTask: true);
    }

    /// <summary>
    /// The report's document, in real's layout: the blocked process as a
    /// deadlock graph gives one, the blocking process with its status, wait
    /// and transaction count ahead of its session's attributes, each with its
    /// execution stack as bare frames — line, offsets and handle, no text.
    /// </summary>
    private static string Xml(int loop, ReportedProcess blocked, SimulatedDbConnection? blocker)
    {
        var invariant = CultureInfo.InvariantCulture;
        var builder = new StringBuilder(2048);
        _ = builder.Append("<blocked-process-report monitorLoop=\"").Append(loop.ToString(invariant)).Append("\">\n <blocked-process>\n  <process");
        DeadlockReport.AppendAttributes(builder, LockReports.ProcessAttributes(blocked));
        _ = builder.Append(">\n");
        AppendStack(builder, blocked);
        _ = builder.Append("  </process>\n </blocked-process>\n <blocking-process>\n");
        if (blocker is not null)
        {
            var session = blocker.Session;
            var waiting = blocker.WaitingOnResource is not null || session.InWaitFor;
            var running = session.CurrentExecutingThreadId is not null;
            var holding = new ReportedProcess(blocker, blocker.LockOwner, "", null, 0);
            var attributes = new List<(string, string)>
            {
                ("status", waiting ? "suspended" : running ? "running" : "sleeping"),
                ("waittime", (waiting ? Math.Max(0, Environment.TickCount64 - (blocker.WaitRecord?.WaitStartedTicks ?? session.WaitStartedTicks)) : 0).ToString(invariant)),
                ("spid", holding.Spid.ToString(invariant)),
                ("sbid", "0"),
                ("ecid", "0"),
                ("priority", holding.Priority.ToString(invariant)),
                ("trancount", holding.OpenTransactions.ToString(invariant)),
            };
            attributes.AddRange(LockReports.SessionAttributes(holding));
            _ = builder.Append("  <process");
            DeadlockReport.AppendAttributes(builder, attributes);
            _ = builder.Append(">\n");
            AppendStack(builder, running ? holding : null);
            _ = builder.Append("  </process>\n");
        }
        return builder.Append(" </blocking-process>\n</blocked-process-report>\n").ToString();

        void AppendStack(StringBuilder into, ReportedProcess? process)
        {
            _ = into.Append("   <executionStack>\n");
            foreach (var frame in process?.Frames ?? [])
            {
                _ = into.Append("    <frame line=\"").Append(frame.Line.ToString(invariant)).Append('"');
                DeadlockReport.AppendOffsets(into, frame);
                _ = into.Append(" sqlhandle=\"").Append(LockReports.Hex(frame.SqlHandle)).Append("\"/>\n");
            }
            _ = into.Append("   </executionStack>\n   <stackFrames>\n      \n      </stackFrames>\n   <inputbuf>\n")
                .Append(LockReports.Escape(process?.InputBuffer ?? (blocker is null ? "" : LockReports.InputBufferOf(blocker.Simulation, blocker.Session.BatchText)))).Append("   </inputbuf>\n");
        }
    }
}
