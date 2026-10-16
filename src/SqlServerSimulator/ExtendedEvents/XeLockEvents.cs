using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// <c>lock_timeout</c>, <c>lock_timeout_greater_than_0</c> and
/// <c>lock_escalation</c> — and through them the trace's <c>Lock:Timeout</c>,
/// <c>Lock:Timeout (timeout &gt; 0)</c> and <c>Lock:Escalation</c> — with the
/// fields SQL Server 2025 gives them (probed 2026-10-16).
/// </summary>
internal static class XeLockEvents
{
    /// <summary>
    /// A request for <paramref name="resource"/> that timed out: real fires
    /// <c>lock_timeout</c> twice, the first with no duration and the second
    /// with the wait's, then — when the session's <c>LOCK_TIMEOUT</c> is
    /// above 0 — <c>lock_timeout_greater_than_0</c> once with the wait's
    /// (probed 2026-10-16 against SQL Server 2025: a key lock under
    /// <c>SET LOCK_TIMEOUT 0</c> fires two <c>lock_timeout</c>, under 300 two
    /// and one <c>lock_timeout_greater_than_0</c> of 300,000 µs). The
    /// resource reads as the holder's row of <c>sys.dm_tran_locks</c> names it,
    /// whose description the trace's classes carry as their <c>TextData</c>.
    /// </summary>
    public static void Timeout(SimulatedDbConnection connection, LockResource resource, LockMode mode, long waitedMilliseconds, int timeoutMillis)
    {
        var simulation = connection.Simulation;
        var greater = timeoutMillis > 0 && XeEvents.Takes(simulation, XeEventKind.LockTimeoutGreaterThan0);
        if (!greater && !XeEvents.Takes(simulation, XeEventKind.LockTimeout))
            return;
        var row = RowOf(connection, resource);
        var micros = (ulong)Math.Max(0, waitedMilliseconds) * 1000;
        var transactionId = connection.RunningBatch?.CurrentTransactionId() ?? connection.CurrentTransaction?.TransactionId ?? 0;
        XeValue[] Data(XeObject @event, ulong duration)
        {
            var (type, r0, r1, r2) = DeadlockReport.ResourceKey(row);
            return XeEvents.Data(@event,
                ("resource_type", XeValue.FromMap(type, null)),
                ("mode", XeValue.FromMap(DeadlockReport.ModeKey(mode), null)),
                ("owner_type", XeValue.FromMap(1, null)),
                ("transaction_id", XeValue.FromSigned(transactionId)),
                ("database_id", XeValue.FromUnsigned((ulong)(row?.DatabaseId ?? connection.CurrentDatabase.Id))),
                ("lockspace_workspace_id", XeValue.FromPointer(0x0000000C39644530UL + ((ulong)connection.Spid * 0x100000))),
                ("lockspace_sub_id", XeValue.FromUnsigned(1)),
                ("lockspace_nest_id", XeValue.FromUnsigned(1)),
                ("resource_0", XeValue.FromUnsigned(r0)),
                ("resource_1", XeValue.FromUnsigned(r1)),
                ("resource_2", XeValue.FromUnsigned(r2)),
                ("object_id", XeValue.FromSigned(row is { Type: "OBJECT" } objectRow ? objectRow.EntityId : 0)),
                ("associated_object_id", XeValue.FromUnsigned((ulong)(row?.EntityId ?? 0))),
                ("duration", XeValue.FromUnsigned(duration)),
                ("resource_description", XeValue.FromUnicode("")),
                ("database_name", XeValue.FromUnicode("")));
        }
        var description = row?.Description;
        XeEvents.Fire(connection, XeEventKind.LockTimeout, @event => Data(@event, 0), traceText: description);
        XeEvents.Fire(connection, XeEventKind.LockTimeout, @event => Data(@event, micros), traceText: description);
        if (greater)
            XeEvents.Fire(connection, XeEventKind.LockTimeoutGreaterThan0, @event => Data(@event, micros), traceText: description);
    }

    /// <summary>
    /// The holder's row of <c>sys.dm_tran_locks</c> for
    /// <paramref name="resource"/>, read in the session's database, or null
    /// when it isn't one the view lists there.
    /// </summary>
    private static LockDmvs.LockRow? RowOf(SimulatedDbConnection connection, LockResource resource)
    {
        if (connection.RunningBatch is not { } batch)
            return null;
        foreach (var row in LockDmvs.EnumerateDmTranLocks(batch, batch.CurrentDatabase))
        {
            if (!row.Waiting && ReferenceEquals(row.Resource, resource))
                return row;
        }
        return null;
    }

    /// <summary>
    /// <c>lock_escalation</c> as a statement's row and key locks on
    /// <paramref name="table"/> give way to one table lock: the table's
    /// <c>OBJECT</c> lock in <paramref name="mode"/>, by lock threshold, with
    /// the locks it replaced — every lock the statement held on the table,
    /// and those on its rowset, one fewer (probed 2026-10-16 against SQL
    /// Server 2025: an <c>UPDATE</c> of 7,000 rows escalates holding 6,249
    /// and 6,248). Its <c>statement</c> is empty, as real's is, while the
    /// trace's <c>Lock:Escalation</c> carries the statement's text.
    /// </summary>
    public static void Escalation(Parser.BatchContext batch, HeapTable table, LockMode mode, int heldLocks)
    {
        var connection = batch.Connection;
        if (!XeEvents.Takes(connection.Simulation, XeEventKind.LockEscalation))
            return;
        var database = XeObjectEvents.DatabaseOf(batch, table);
        var hobt = PartitionCensus.UnpartitionedId(table, KeyLockGroup.RowGroupOf(table) is { } rows ? rows.IndexId : 0);
        var transactionId = batch.CurrentTransactionId();
        XeEvents.Fire(connection, XeEventKind.LockEscalation, @event => XeEvents.Data(@event,
            ("resource_type", XeValue.FromMap(5, null)),
            ("mode", XeValue.FromMap(DeadlockReport.ModeKey(mode), null)),
            ("owner_type", XeValue.FromMap(1, null)),
            ("transaction_id", XeValue.FromSigned(transactionId)),
            ("database_id", XeValue.FromUnsigned((ulong)database.Id)),
            ("lockspace_workspace_id", XeValue.FromPointer(0x0000000C39644530UL + ((ulong)connection.Spid * 0x100000))),
            ("lockspace_sub_id", XeValue.FromUnsigned(1)),
            ("lockspace_nest_id", XeValue.FromUnsigned(1)),
            ("resource_0", XeValue.FromUnsigned((uint)table.ObjectId)),
            ("escalation_cause", XeValue.FromMap(0, null)),
            ("object_id", XeValue.FromSigned(table.ObjectId)),
            ("hobt_id", XeValue.FromUnsigned((ulong)hobt)),
            ("escalated_lock_count", XeValue.FromUnsigned((ulong)heldLocks)),
            ("hobt_lock_count", XeValue.FromUnsigned((ulong)Math.Max(0, heldLocks - 1))),
            ("statement", XeValue.FromUnicode("")),
            ("database_name", XeValue.FromUnicode(""))),
            traceText: XeStatementEvents.Place(batch, batch.CurrentStatement.StartIndex) is { EndRecorded: true } placed ? placed.Text : null);
    }
}
