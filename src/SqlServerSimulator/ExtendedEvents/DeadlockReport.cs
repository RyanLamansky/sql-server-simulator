using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// A deadlock as real reports it: the <c>lock_deadlock_chain</c> event for
/// each process, <c>lock_deadlock</c> for the victim and
/// <c>xml_deadlock_report</c>'s graph, then, under trace flags 1204 and 1222,
/// the error log's two reports (probed 2026-10-16 against SQL Server 2025).
/// Built by the victim while every session in the cycle still waits and
/// holds what it held, so the lock views read the cycle as it was found.
/// </summary>
internal static class DeadlockReport
{
    /// <summary>One lock resource of the graph: the element real names it with and its attributes, the lock row it was read from.</summary>
    private sealed class ReportedResource(string element, List<(string Name, string Value)> attributes, LockDmvs.LockRow? row, string waitResource)
    {
        public readonly string Element = element;
        public readonly List<(string Name, string Value)> Attributes = attributes;
        public readonly LockDmvs.LockRow? Row = row;
        public readonly string WaitResource = waitResource;
    }

    public static void Report(Simulation simulation, DeadlockCycle cycle)
    {
        var connections = new SimulatedDbConnection[cycle.Edges.Length];
        for (var i = 0; i < connections.Length; i++)
        {
            if (cycle.Edges[i].Waiter.TryResolveActing() is not { } connection)
                return;
            connections[i] = connection;
        }
        if (connections[0].RunningBatch is not { } batch)
            return;

        var rows = new List<LockDmvs.LockRow>();
        foreach (var row in LockDmvs.EnumerateDmTranLocks(batch, batch.CurrentDatabase))
        {
            if (!row.Waiting && row.Resource is not null)
                rows.Add(row);
        }

        var resources = new ReportedResource[cycle.Edges.Length];
        var processes = new ReportedProcess[cycle.Edges.Length];
        for (var i = 0; i < resources.Length; i++)
        {
            var edge = cycle.Edges[i];
            resources[i] = Describe(simulation, edge, rows);
            processes[i] = new ReportedProcess(connections[i], edge.Waiter, resources[i].WaitResource, edge.WaitMode, edge.WaitStartedTicks);
        }

        var victim = processes[0];
        for (var i = 0; i < resources.Length; i++)
        {
            var edge = cycle.Edges[i];
            XeEvents.Fire(connections[i], XeEventKind.LockDeadlockChain, @event => LockEventData(@event, edge, resources[i], processes[i], cycle.DeadlockId, durationMicroseconds: null), systemTask: true);
        }
        var victimEdge = cycle.Edges[0];
        XeEvents.Fire(connections[0], XeEventKind.LockDeadlock, @event => LockEventData(@event, victimEdge, resources[0], victim, cycle.DeadlockId, victim.WaitTime * 1000));
        XeEvents.Fire(connections[0], XeEventKind.XmlDeadlockReport, @event => XeEvents.Data(@event, ("xml_report", XeValue.FromXml(Xml(processes, resources)))), systemTask: true);
        simulation.NoteTraceDeadlockGraph(() => Xml(processes, resources), victim.Id, cycle.FoundUtc);

        var flag1204 = simulation.IsGlobalTraceFlagOn(1204);
        var flag1222 = simulation.IsGlobalTraceFlagOn(1222);
        if (flag1204)
            WriteLog(simulation, cycle.FoundUtc, "spid44s", TraceFlag1204(processes, resources));
        if (flag1222)
            WriteLog(simulation, cycle.FoundUtc, "spid3s", TraceFlag1222(processes, resources));
    }

    /// <summary>
    /// The resource the process of <paramref name="edge"/> waits for, read off
    /// the lock row its holder holds it under: <c>keylock</c>, <c>ridlock</c>,
    /// <c>pagelock</c>, <c>objectlock</c> or <c>applicationlock</c> with real's
    /// attributes in real's order, <c>mode</c> the holder's.
    /// </summary>
    private static ReportedResource Describe(Simulation simulation, DeadlockCycle.Edge edge, List<LockDmvs.LockRow> rows)
    {
        LockDmvs.LockRow? found = null;
        foreach (var row in rows)
        {
            if (ReferenceEquals(row.Resource, edge.Resource) && (ReferenceEquals(row.Owner, edge.Holder) || found is null))
            {
                found = row;
                if (ReferenceEquals(row.Owner, edge.Holder))
                    break;
            }
        }
        var invariant = CultureInfo.InvariantCulture;
        var mode = LockDmvs.ModeAbbreviation(found?.Mode ?? edge.HeldMode);
        var id = LockDmvs.LockBlockId(edge.Resource);
        if (found is not { } lockRow)
            return new ReportedResource("lock", [("id", id), ("mode", mode)], null, "");
        var dbid = lockRow.DatabaseId.ToString(invariant);
        var objectName = ObjectName(simulation, lockRow.DatabaseId, lockRow.ObjectId);
        List<(string, string)> attributes;
        string element;
        switch (lockRow.Type)
        {
            case "APPLICATION":
                element = "applicationlock";
                var colon = lockRow.Description.IndexOf(':', StringComparison.Ordinal);
                attributes = [("databasePrincipalId", lockRow.Description[..colon]), ("dbid", dbid), ("id", id), ("mode", mode)];
                break;
            case "KEY":
                element = "keylock";
                attributes = [("hobtid", lockRow.EntityId.ToString(invariant)), ("dbid", dbid), ("objectname", objectName), ("indexname", IndexName(edge.Resource, lockRow.IndexId)), ("id", id), ("mode", mode), ("associatedObjectId", lockRow.EntityId.ToString(invariant))];
                break;
            case "OBJECT":
                element = "objectlock";
                attributes = [("lockPartition", "0"), ("objid", lockRow.EntityId.ToString(invariant)), ("subresource", "FULL"), ("dbid", dbid), ("objectname", objectName), ("id", id), ("mode", mode), ("associatedObjectId", lockRow.EntityId.ToString(invariant))];
                break;
            case "PAGE":
                element = "pagelock";
                var page = lockRow.Description.Split(':');
                attributes = [("fileid", page[0]), ("pageid", page[1]), ("dbid", dbid), ("subresource", "FULL"), ("objectname", objectName), ("id", id), ("mode", mode), ("associatedObjectId", lockRow.EntityId.ToString(invariant))];
                break;
            case "RID":
                element = "ridlock";
                var rid = lockRow.Description.Split(':');
                attributes = [("fileid", rid[0]), ("pageid", rid[1]), ("dbid", dbid), ("objectname", objectName), ("id", id), ("mode", mode), ("associatedObjectId", lockRow.EntityId.ToString(invariant))];
                break;
            default:
                element = lockRow.Type switch
                {
                    "DATABASE" => "databaselock",
                    "METADATA" => "metadatalock",
                    _ => "lock",
                };
                attributes = [("dbid", dbid), ("id", id), ("mode", mode)];
                break;
        }
        return new ReportedResource(element, attributes, lockRow, LockDmvs.WaitResourceOf(lockRow));
    }

    private static string ObjectName(Simulation simulation, int databaseId, int objectId)
    {
        if (Parser.Expressions.DbId.DatabaseWithId(simulation, databaseId) is not { } database
            || Parser.Expressions.ObjectProperty.FindObject(database, objectId) is not { } found)
        {
            return "";
        }
        return $"{database.Name}.{PermissionEnforcement.SchemaNameFor(database, found.SchemaId)}.{found.Name}";
    }

    private static string IndexName(LockResource resource, int indexId)
    {
        var table = resource.OwningTable ?? resource.PageOfTable ?? resource.KeyGroup?.Table;
        if (table is null)
            return "";
        foreach (var constraint in table.KeyConstraints)
        {
            if (constraint.IndexId == indexId)
                return constraint.Name;
        }
        foreach (var index in table.Indexes)
        {
            if (index.IndexId == indexId)
                return index.Name;
        }
        return "";
    }

    /// <summary>
    /// <c>lock_deadlock_chain</c>'s and <c>lock_deadlock</c>'s fields: the
    /// waited resource as the lock manager keys it — a key's hobt and hash, a
    /// row's page, file and slot, a page's number and file, an object's id —
    /// the mode the process asked for and its transaction.
    /// </summary>
    private static XeValue[] LockEventData(XeObject @event, DeadlockCycle.Edge edge, ReportedResource resource, ReportedProcess process, int deadlockId, long? durationMicroseconds)
    {
        var (type, r0, r1, r2) = ResourceKey(resource.Row);
        var row = resource.Row;
        return XeEvents.Data(@event,
            ("resource_type", XeValue.FromMap(type, null)),
            ("mode", XeValue.FromMap(ModeKey(edge.WaitMode), null)),
            ("owner_type", XeValue.FromMap(1, null)),
            ("transaction_id", XeValue.FromSigned(process.TransactionId)),
            ("database_id", XeValue.FromUnsigned((ulong)(row?.DatabaseId ?? process.Connection.CurrentDatabase.Id))),
            ("lockspace_workspace_id", XeValue.FromPointer(0x0000000C39644530UL + ((ulong)process.Spid * 0x100000))),
            ("lockspace_sub_id", XeValue.FromUnsigned(1)),
            ("lockspace_nest_id", XeValue.FromUnsigned(1)),
            ("resource_0", XeValue.FromUnsigned(r0)),
            ("resource_1", XeValue.FromUnsigned(r1)),
            ("resource_2", XeValue.FromUnsigned(r2)),
            ("deadlock_id", XeValue.FromUnsigned((ulong)deadlockId)),
            ("object_id", XeValue.FromSigned(row is { Type: "OBJECT" } objectRow ? objectRow.EntityId : 0)),
            ("associated_object_id", XeValue.FromUnsigned((ulong)(row?.EntityId ?? 0))),
            ("session_id", XeValue.FromSigned(process.Spid)),
            ("resource_owner_type", XeValue.FromMap(1, null)),
            ("duration", XeValue.FromUnsigned((ulong)(durationMicroseconds ?? 0))));
    }

    /// <summary>The lock manager's own key of a resource: its <c>lock_resource_type</c> and the three words real reports beside it.</summary>
    internal static (ulong Type, ulong R0, ulong R1, ulong R2) ResourceKey(LockDmvs.LockRow? row)
    {
        if (row is not { } found)
            return (0, 0, 0, 0);
        var invariant = CultureInfo.InvariantCulture;
        switch (found.Type)
        {
            case "APPLICATION":
                return (10, 0, 0, 0);
            case "KEY":
                // (8194443284a0): the hash's first two bytes in resource_1's
                // high half, the other four in resource_2, both little-endian.
                var hash = Convert.FromHexString(found.Description.AsSpan().Trim("()"));
                var r1 = hash.Length >= 2 ? (ulong)(hash[0] | (hash[1] << 8)) << 16 : 0;
                var r2 = hash.Length >= 6 ? (ulong)(uint)(hash[2] | (hash[3] << 8) | (hash[4] << 16) | (hash[5] << 24)) : 0;
                return (7, (ulong)((found.EntityId >> 16) & 0xFFFFFFFF), r1, r2);
            case "OBJECT":
                return (5, (ulong)found.EntityId, 0, 0);
            case "PAGE":
                var page = found.Description.Split(':');
                return (6, ulong.Parse(page[1], invariant), ulong.Parse(page[0], invariant), 0);
            case "RID":
                var rid = found.Description.Split(':');
                return (9, ulong.Parse(rid[1], invariant), ulong.Parse(rid[0], invariant) | (ulong.Parse(rid[2], invariant) << 16), 0);
            default:
                return (0, 0, 0, 0);
        }
    }

    /// <summary>A lock mode's key in the <c>lock_mode</c> map.</summary>
    internal static ulong ModeKey(LockMode mode) => mode switch
    {
        LockMode.SchemaStability => 1,
        LockMode.SchemaModification => 2,
        LockMode.Shared => 3,
        LockMode.Update => 4,
        LockMode.Exclusive => 5,
        LockMode.IntentShared => 6,
        LockMode.IntentUpdate => 7,
        LockMode.IntentExclusive => 8,
        LockMode.SharedIntentUpdate => 9,
        LockMode.SharedIntentExclusive => 10,
        LockMode.UpdateIntentExclusive => 11,
        LockMode.RangeSharedShared => 13,
        LockMode.RangeSharedUpdate => 14,
        LockMode.RangeInsertNull => 15,
        LockMode.RangeExclusiveExclusive => 21,
        _ => 0,
    };

    /// <summary>The graph <c>xml_deadlock_report</c> carries, in real's layout to the white space.</summary>
    private static string Xml(ReportedProcess[] processes, ReportedResource[] resources)
    {
        var builder = new StringBuilder(4096);
        _ = builder.Append("<deadlock>\n <victim-list>\n  <victimProcess id=\"").Append(processes[0].Id).Append("\"/>\n </victim-list>\n <process-list>\n");
        foreach (var process in processes)
        {
            _ = builder.Append("  <process");
            AppendAttributes(builder, LockReports.ProcessAttributes(process));
            _ = builder.Append(">\n   <executionStack>\n");
            foreach (var frame in process.Frames)
            {
                _ = builder.Append("    <frame procname=\"").Append(LockReports.Escape(frame.ProcName)).Append("\" line=\"").Append(frame.Line.ToString(CultureInfo.InvariantCulture)).Append('"');
                AppendOffsets(builder, frame);
                _ = builder.Append(" sqlhandle=\"").Append(LockReports.Hex(frame.SqlHandle)).Append("\">\n")
                    .Append(LockReports.Escape(frame.Text ?? "unknown")).Append("    </frame>\n");
            }
            _ = builder.Append("   </executionStack>\n   <stackFrames>\n      \n      </stackFrames>\n   <inputbuf>\n")
                .Append(LockReports.Escape(process.InputBuffer)).Append("   </inputbuf>\n  </process>\n");
        }
        _ = builder.Append(" </process-list>\n <resource-list>\n");
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            var holder = processes[i == 0 ? processes.Length - 1 : i - 1];
            _ = builder.Append("  <").Append(resource.Element);
            AppendAttributes(builder, resource.Attributes);
            _ = builder.Append(">\n   <owner-list>\n    <owner id=\"").Append(holder.Id).Append("\" mode=\"").Append(ModeOf(resource))
                .Append("\"/>\n   </owner-list>\n   <waiter-list>\n    <waiter id=\"").Append(processes[i].Id).Append("\" mode=\"").Append(processes[i].LockMode)
                .Append("\" requestType=\"wait\"/>\n   </waiter-list>\n  </").Append(resource.Element).Append(">\n");
        }
        return builder.Append(" </resource-list>\n</deadlock>\n").ToString();
    }

    private static string ModeOf(ReportedResource resource)
    {
        foreach (var (name, value) in resource.Attributes)
        {
            if (name == "mode")
                return value;
        }
        return "";
    }

    /// <summary>A frame's offsets, each left out at its default — a start at 0, an end running to the text's end — as real's writer leaves them.</summary>
    internal static void AppendOffsets(StringBuilder builder, ReportFrame frame)
    {
        if (frame.StatementStart != 0)
            _ = builder.Append(" stmtstart=\"").Append(frame.StatementStart.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (frame.StatementEnd >= 0)
            _ = builder.Append(" stmtend=\"").Append(frame.StatementEnd.ToString(CultureInfo.InvariantCulture)).Append('"');
    }

    internal static void AppendAttributes(StringBuilder builder, List<(string Name, string Value)> attributes)
    {
        foreach (var (name, value) in attributes)
            _ = builder.Append(' ').Append(name).Append("=\"").Append(LockReports.Escape(value)).Append('"');
    }

    /// <summary>
    /// Trace flag 1222's report: the graph as indented <c>name=value</c>
    /// lines, one log entry each (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static List<string> TraceFlag1222(ReportedProcess[] processes, ReportedResource[] resources)
    {
        var lines = new List<string> { "deadlock-list", " deadlock victim=" + processes[0].Id, "  process-list" };
        foreach (var process in processes)
        {
            lines.Add("   process" + Flat(LockReports.ProcessAttributes(process)));
            lines.Add("    executionStack");
            foreach (var frame in process.Frames)
            {
                var offsets = new StringBuilder();
                if (frame.StatementStart != 0)
                    _ = offsets.Append(" stmtstart=").Append(frame.StatementStart.ToString(CultureInfo.InvariantCulture));
                if (frame.StatementEnd >= 0)
                    _ = offsets.Append(" stmtend=").Append(frame.StatementEnd.ToString(CultureInfo.InvariantCulture));
                lines.Add($"     frame procname={frame.ProcName} line={frame.Line.ToString(CultureInfo.InvariantCulture)}{offsets} sqlhandle={LockReports.Hex(frame.SqlHandle)}");
                lines.Add((frame.Text ?? "unknown") + "     ");
            }
            lines.Add("    stackFrames");
            lines.Add("    inputbuf");
            lines.Add(process.InputBuffer + "    ");
        }
        lines.Add("  resource-list");
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            var holder = processes[i == 0 ? processes.Length - 1 : i - 1];
            lines.Add("   " + resource.Element + Flat(resource.Attributes));
            lines.Add("    owner-list");
            lines.Add($"     owner id={holder.Id} mode={ModeOf(resource)}");
            lines.Add("    waiter-list");
            lines.Add($"     waiter id={processes[i].Id} mode={processes[i].LockMode} requestType=wait");
        }
        return lines;
    }

    private static string Flat(List<(string Name, string Value)> attributes)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in attributes)
            _ = builder.Append(' ').Append(name).Append('=').Append(value);
        return builder.ToString();
    }

    /// <summary>
    /// Trace flag 1204's report: each resource of the cycle as a node with
    /// its grant list, the holder's statement and input buffer and the
    /// request waiting on it, then the victim (probed 2026-10-16 against SQL
    /// Server 2025). The owner addresses, the grant list's bucket and the
    /// request's value stand in for real's.
    /// </summary>
    private static List<string> TraceFlag1204(ReportedProcess[] processes, ReportedResource[] resources)
    {
        var invariant = CultureInfo.InvariantCulture;
        var lines = new List<string> { "Deadlock encountered .... Printing deadlock information\n", "Wait-for graph\n", "\n" };
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            var holder = processes[i == 0 ? processes.Length - 1 : i - 1];
            var waiter = processes[i];
            var mode = ModeOf(resource);
            lines.Add(string.Create(invariant, $"Node:{i + 1}\n"));
            lines.Add($"{resource.WaitResource.TrimEnd()} CleanCnt:2 Mode:{mode} Flags: 0x1");
            lines.Add(" Grant List 0:");
            lines.Add(string.Create(invariant, $"   Owner:0x{0x0000000C3B6BF8C0L + ((long)holder.Spid * 0x10000):X16} Mode: {mode,-9}Flg:0x40 Ref:0 Life:02000000 SPID:{holder.Spid} ECID:0 XactLockInfo: 0x{0x0000000C3A87C590L + ((long)holder.Spid * 0x40000):X16}"));
            lines.Add(string.Create(invariant, $"   SPID: {holder.Spid} ECID: 0 Statement Type: {holder.StatementVerb} Line #: {holder.StatementLine}"));
            lines.Add("   Input Buf: Language Event: " + holder.InputBuffer);
            lines.Add("Requested by: \n");
            lines.Add(RequestLine(waiter, "  "));
            lines.Add("\n");
        }
        lines.Add("Victim Resource Owner:\n");
        lines.Add(RequestLine(processes[0], " "));
        return lines;
    }

    private static string RequestLine(ReportedProcess process, string indent) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{indent}ResType:LockOwner Stype:'OR'Xdes:0x{0x0000000C2B964480L + ((long)process.Spid * 0x40000):X16} Mode: {process.LockMode} SPID:{process.Spid} BatchID:0 ECID:0 TaskProxy:(0x{0x0000000C724F6250L + ((long)process.Spid * 0x10000):X16}) Value:0x{(uint)(0x27558840 + (process.Spid * 0x1000)):x} Cost:(0/{process.LogUsed})");

    /// <summary>
    /// Writes a report's lines under the system session real writes it from,
    /// a line longer than an entry holds split as real splits it.
    /// </summary>
    private static void WriteLog(Simulation simulation, DateTime utc, string processInfo, List<string> lines)
    {
        foreach (var line in lines)
        {
            for (var at = 0; at < line.Length || at == 0; at += EntryCharacters)
                simulation.ErrorLog.Write(utc, processInfo, line.Substring(at, Math.Min(EntryCharacters, line.Length - at)));
        }
    }

    /// <summary>How much of a long report line real's log writes per entry (probed 2026-10-16 against SQL Server 2025, whose stack-frame lines split near two thousand characters).</summary>
    private const int EntryCharacters = 2000;
}
