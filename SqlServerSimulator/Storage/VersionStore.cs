using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Pending row-version capture for one in-flight INSERT / UPDATE / DELETE.
/// Buffered on the active <see cref="SimulatedDbTransaction"/> (or on the
/// <see cref="BatchContext"/> for auto-commit statements) until the writer
/// commits — at which point <see cref="VersionStore.FinalizePendingEntries"/>
/// stamps the entries with the commit Xid and pushes historical payloads
/// into <see cref="HeapTable.RowVersions"/>. Rollback (statement-atomic or
/// explicit <c>ROLLBACK</c>) calls <see cref="VersionStore.DiscardPendingEntries"/>
/// which clears the in-flight <see cref="RowVersionChain.WriterTx"/> markers
/// without disturbing the heap (the undo log already restored it).
/// </summary>
internal sealed class PendingVersionEntry
{
    internal HeapTable Table = null!;
    internal (int Page, int Slot) NewRid;
    internal (int Page, int Slot)? OldRid;
    internal byte[]? OldPayload;
    internal VersionWriteKind Kind;
}

/// <summary>
/// Differentiates the three mutation kinds whose visibility / commit
/// finalization rules differ. INSERT creates a new chain entry, UPDATE
/// migrates the chain from the old slot to the new slot and prepends the
/// historical payload, DELETE marks the slot as tombstoned-after-commit.
/// </summary>
internal enum VersionWriteKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// Helper layer over <see cref="HeapTable.RowVersions"/> that the mutation
/// dispatch path (INSERT / UPDATE / DELETE / MERGE / OUTPUT-INTO /
/// SELECT-INTO / FK cascade) calls into to record the pre-write state of
/// each affected row. Visibility lookups for SNAPSHOT and
/// READ_COMMITTED_SNAPSHOT readers walk the same data structures via
/// <see cref="ResolveVisibleVersion"/>. Capture is a no-op when neither
/// flag is on for the current database — the version-chain dict stays
/// empty and the read path's <see cref="HeapTable.RowVersions"/> lookup
/// short-circuits.
/// </summary>
internal static class VersionStore
{
    /// <summary>
    /// Returns <c>true</c> iff the current database has either
    /// <see cref="Database.AllowSnapshotIsolation"/> or
    /// <see cref="Database.ReadCommittedSnapshot"/> turned on — only then
    /// does writer-side capture need to record pre-write payloads.
    /// </summary>
    internal static bool IsVersioningEnabled(Database database) =>
        database.AllowSnapshotIsolation || database.ReadCommittedSnapshot;

    /// <summary>
    /// Captures a pre-write snapshot for the row at
    /// <paramref name="newRid"/> (post-mutation slot). For UPDATE /
    /// DELETE, <paramref name="oldPayload"/> and <paramref name="oldRid"/>
    /// carry the pre-mutation state (for UPDATE these may differ when the
    /// row moves slots); for INSERT both are null and the chain entry is
    /// created in-flight with no history. Marks the chain's
    /// <see cref="RowVersionChain.WriterTx"/> so concurrent SI readers walk
    /// past the live (uncommitted) heap row.
    /// </summary>
    internal static void CaptureWrite(BatchContext batch, HeapTable table, (int Page, int Slot) newRid, (int Page, int Slot)? oldRid, byte[]? oldPayload, VersionWriteKind kind)
    {
        if (!IsVersioningEnabled(batch.CurrentDatabase))
            return;
        if (table.IsTableVariable || BatchContext.IsLocalTempName(table.Name))
            return;
        if (Simulation.SystemHeapTables.ContainsValue(table))
            return;

        var tx = batch.Connection.CurrentTransaction;
        var chain = table.RowVersions.GetOrAdd(newRid, static _ => new RowVersionChain());
        chain.WriterTx = tx;

        // For UPDATE, the chain at NewRid gets a fresh history entry
        // carrying the pre-mutation payload — eagerly attached so SI
        // readers (which walk history when WriterTx is set) can see the
        // pre-write value. Xmax = PendingXmax sentinel; the writer's
        // commit step (FinalizePendingEntries) rewrites it to the actual
        // commit Xid. Carrying the old slot's existing history forward
        // matches the chain semantics — multi-update timelines stay
        // walkable for older snapshots.
        if (kind == VersionWriteKind.Update && oldPayload is not null && oldRid is { } oldRidValue)
        {
            var oldChain = GetExistingChain(table, oldRidValue);
            var hv = new HistoricalVersion
            {
                Payload = oldPayload,
                Xmin = oldChain?.LiveXmin ?? 0,
                Xmax = PendingXmax,
                Next = oldChain?.Head,
            };
            chain.Head = hv;
            // Old slot's chain stays until commit — Rollback removes the
            // entire new-slot chain (the slot didn't exist pre-tx) and the
            // old slot retains its prior visibility. Commit drops the old
            // slot since its data has already been carried forward.
        }

        batch.AppendPendingVersionEntry(new PendingVersionEntry
        {
            Table = table,
            NewRid = newRid,
            OldRid = oldRid,
            OldPayload = oldPayload,
            Kind = kind,
        });
    }

    /// <summary>
    /// Sentinel value stamped on a <see cref="HistoricalVersion.Xmax"/>
    /// while the superseding writer's transaction is still in flight.
    /// Replaced at commit time with the actual commit Xid by
    /// <see cref="FinalizePendingEntries"/>; treated as "infinity" (always
    /// after every snapshot) during the visibility check so SI readers
    /// see pre-write versions while the writer is uncommitted.
    /// </summary>
    internal const long PendingXmax = long.MaxValue;

    /// <summary>
    /// Called from <see cref="SimulatedDbTransaction.Commit"/> (or at
    /// statement end for auto-commit). Allocates one commit Xid for the
    /// transaction's whole pending list, then walks each entry and
    /// finalizes the chain at the live slot:
    /// <list type="bullet">
    /// <item>INSERT: stamp <see cref="RowVersionChain.LiveXmin"/> with the
    /// commit Xid and clear <see cref="RowVersionChain.WriterTx"/>.</item>
    /// <item>UPDATE: prepend a <see cref="HistoricalVersion"/> to the new
    /// slot's chain carrying the pre-mutation payload, with <c>Xmin</c>
    /// from the OLD slot's chain (or 0 if absent) and <c>Xmax</c> set to
    /// the commit Xid; stamp the new chain's LiveXmin and drop the OLD
    /// slot's chain entry (its slot is tombstoned).</item>
    /// <item>DELETE: mark the chain as
    /// <see cref="RowVersionChain.IsDeletedLive"/>; prepend the pre-delete
    /// payload to <see cref="RowVersionChain.Head"/> with Xmax = commit Xid.</item>
    /// </list>
    /// </summary>
    internal static void FinalizePendingEntries(List<PendingVersionEntry> entries, Database database)
    {
        if (entries.Count == 0)
            return;
        var commitXid = database.AllocateTransactionCommitId();
        foreach (var entry in entries)
        {
            var newChain = entry.Table.RowVersions.GetOrAdd(entry.NewRid, static _ => new RowVersionChain());
            switch (entry.Kind)
            {
                case VersionWriteKind.Insert:
                    newChain.LiveXmin = commitXid;
                    newChain.WriterTx = null;
                    break;
                case VersionWriteKind.Update:
                    {
                        // The pre-write history entry was already attached
                        // at capture time; commit stamps its Xmax with the
                        // real commit Xid, finalizes the chain's LiveXmin,
                        // and drops the now-abandoned old-slot chain.
                        if (newChain.Head is { Xmax: PendingXmax } pendingHead)
                            pendingHead.Xmax = commitXid;
                        newChain.LiveXmin = commitXid;
                        newChain.WriterTx = null;
                        if (entry.OldRid is { } abandonedRid && !abandonedRid.Equals(entry.NewRid))
                            _ = entry.Table.RowVersions.TryRemove(abandonedRid, out _);
                        break;
                    }
                case VersionWriteKind.Delete:
                    {
                        var hv = new HistoricalVersion
                        {
                            Payload = entry.OldPayload ?? [],
                            Xmin = newChain.LiveXmin,
                            Xmax = commitXid,
                            Next = newChain.Head,
                        };
                        newChain.Head = hv;
                        newChain.LiveXmin = commitXid;
                        newChain.IsDeletedLive = true;
                        newChain.WriterTx = null;
                        break;
                    }
            }
        }
        entries.Clear();
    }

    /// <summary>
    /// Called from <see cref="SimulatedDbTransaction.Rollback"/> (or on
    /// statement-atomic mid-execution failure). Clears every pending
    /// entry's <see cref="RowVersionChain.WriterTx"/> mark so SI readers
    /// no longer see "uncommitted writer" on those slots; the heap rows
    /// themselves are restored by the undo log. For INSERT the chain is
    /// dropped (the row never existed from any snapshot's perspective);
    /// for UPDATE the pending pre-write <see cref="HistoricalVersion"/> is
    /// popped off the chain head, restoring the pre-tx history shape (with
    /// stable RIDs, a row UPDATEd in this tx still has earlier committed
    /// history at the same chain — preserving that is required for SI
    /// readers whose snapshot pre-dates this tx).
    /// </summary>
    internal static void DiscardPendingEntries(List<PendingVersionEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.Table.RowVersions.TryGetValue(entry.NewRid, out var chain))
                continue;
            chain.WriterTx = null;
            switch (entry.Kind)
            {
                case VersionWriteKind.Insert:
                    // The chain was created by this tx's INSERT and has no
                    // pre-tx history — drop it entirely.
                    _ = entry.Table.RowVersions.TryRemove(entry.NewRid, out _);
                    break;
                case VersionWriteKind.Update:
                    // Pop the pending HV the matching CaptureWrite prepended.
                    if (chain.Head is { Xmax: PendingXmax } pendingHead)
                        chain.Head = pendingHead.Next;
                    // INSERT-then-UPDATE in the same tx leaves the chain with
                    // LiveXmin = 0 (the INSERT hadn't committed); a subsequent
                    // INSERT-entry discard will drop the chain, so here we just
                    // strip the UPDATE's contribution. Pre-existing chains
                    // retain their LiveXmin and any earlier HVs.
                    break;
                case VersionWriteKind.Delete:
                    // chain stays with WriterTx cleared so future SI readers
                    // consult the live (un-tombstoned) row.
                    break;
            }
        }
        entries.Clear();
    }

    private static RowVersionChain? GetExistingChain(HeapTable table, (int Page, int Slot) rid) =>
        table.RowVersions.TryGetValue(rid, out var chain) ? chain : null;

    /// <summary>
    /// Walks every per-table <see cref="HeapTable.RowVersions"/> chain in
    /// the database and drops <see cref="HistoricalVersion"/> nodes whose
    /// <c>Xmax &lt;= oldest_active_snapshot_xid</c> — no active SI
    /// transaction needs that version anymore. When no SI tx is in flight,
    /// the cutoff is <see cref="Database.CurrentTransactionCommitId"/>, so
    /// every finalized HV becomes collectible. Chains that lose their only
    /// HV AND aren't marked deleted-live AND have no in-flight writer are
    /// dropped from the dict entirely; chains that retain at least one
    /// fully-visible-to-no-active-snapshot HV stay (later GC passes may
    /// shorten them further). Skips chains with non-null
    /// <see cref="RowVersionChain.WriterTx"/> — those have an in-flight
    /// writer whose pending HV uses the <c>PendingXmax</c> sentinel and
    /// must not be touched.
    /// </summary>
    internal static void RunGarbageCollection(Database database)
    {
        var cutoff = OldestActiveSnapshotXid(database);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.RowVersions.IsEmpty)
                    continue;
                foreach (var kv in table.RowVersions)
                {
                    var chain = kv.Value;
                    if (chain.WriterTx is not null)
                        continue;
                    chain.Head = TrimHistory(chain.Head, cutoff);
                    if (chain.Head is null && !chain.IsDeletedLive)
                        _ = table.RowVersions.TryRemove(kv.Key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Drops the trailing run of <see cref="HistoricalVersion"/> nodes
    /// whose <c>Xmax &lt;= cutoff</c>. Returns the new chain head (may be
    /// <c>null</c> when every node is collectible). Walks newest-first
    /// (head → tail); SI / RCSI visibility uses <c>Xmin &lt;= SX &lt; Xmax</c>,
    /// so an HV with <c>Xmax &lt;= SX</c> is invisible to that snapshot, and
    /// an HV invisible to every active snapshot (Xmax &lt;= cutoff) is
    /// invisible to all future snapshots too (cutoff only rises).
    /// </summary>
    private static HistoricalVersion? TrimHistory(HistoricalVersion? head, long cutoff)
    {
        var node = head;
        HistoricalVersion? previous = null;
        while (node is not null)
        {
            if (node.Xmax <= cutoff)
            {
                if (previous is null)
                    return null;
                previous.Next = null;
                return head;
            }
            previous = node;
            node = node.Next;
        }
        return head;
    }

    /// <summary>
    /// Smallest <see cref="SimulatedDbTransaction.SnapshotXid"/> across the
    /// database's <see cref="Database.ActiveSnapshotTxs"/> set, or the
    /// current commit-id counter when no SI tx is in flight. The empty-set
    /// case returns the latest stamp so the GC can drop every finalized
    /// HV. RCSI per-statement snapshots aren't tracked in this set — they
    /// have effectively-zero lifetime (allocated at first user-table read,
    /// released at statement end), so the GC's once-per-tx-finalize cadence
    /// won't observe them as load-bearing.
    /// </summary>
    private static long OldestActiveSnapshotXid(Database database)
    {
        if (database.ActiveSnapshotTxs.IsEmpty)
            return database.CurrentTransactionCommitId;
        var min = long.MaxValue;
        foreach (var kv in database.ActiveSnapshotTxs)
        {
            if (kv.Key.SnapshotXid is { } xid && xid < min)
                min = xid;
        }
        return min == long.MaxValue ? database.CurrentTransactionCommitId : min;
    }

    /// <summary>
    /// Pre-write conflict check for SNAPSHOT-isolation writers. Raises
    /// Msg 3960 (auto-rollback semantic — caller is responsible for
    /// calling <see cref="SimulatedDbTransaction.Rollback"/> after the
    /// throw) when the live row at <paramref name="rid"/> was committed
    /// by a different transaction after the SI writer's snapshot. Returns
    /// silently when the row is safe to overwrite. No-op when not under
    /// SI (the caller checks <see cref="SimulatedDbConnection.SessionIsolationLevel"/>
    /// before calling).
    /// </summary>
    internal static void CheckSnapshotUpdateConflict(BatchContext batch, HeapTable table, (int Page, int Slot) rid)
    {
        var connection = batch.Connection;
        if (connection.SessionIsolationLevel != System.Data.IsolationLevel.Snapshot)
            return;
        var snapshotXid = connection.CurrentTransaction?.SnapshotXid;
        if (snapshotXid is not { } sx)
            return;
        if (!table.RowVersions.TryGetValue(rid, out var chain))
            return;
        if (chain.LiveXmin <= sx && (chain.WriterTx is null || ReferenceEquals(chain.WriterTx, connection.CurrentTransaction)))
            return;
        // Row was modified by another tx after my snapshot. Probe-confirmed
        // auto-rollback: the SI tx terminates with @@TRANCOUNT = 0.
        connection.CurrentTransaction?.Rollback();
        throw SimulatedSqlException.SnapshotIsolationUpdateConflict($"{Database.DefaultSchemaName}.{table.Name}", batch.CurrentDatabase.Name);
    }

    /// <summary>
    /// Resolves the version of the slot's row visible at
    /// <paramref name="snapshotXid"/>. Returns <c>null</c> when no version
    /// is visible (row inserted after the snapshot, or deleted before
    /// it). Returns the historical payload when the live row was
    /// committed after the snapshot; returns <paramref name="livePayload"/>
    /// when the live row is visible directly.
    /// </summary>
    internal static byte[]? ResolveVisibleVersion(HeapTable table, (int Page, int Slot) rid, byte[] livePayload, long snapshotXid, SimulatedDbTransaction? readerTx)
    {
        if (!table.RowVersions.TryGetValue(rid, out var chain))
            return livePayload;
        if (chain.WriterTx is { } writer && !ReferenceEquals(writer, readerTx))
        {
            // Live row is uncommitted; walk history.
            return WalkHistory(chain.Head, snapshotXid);
        }
        return chain.IsDeletedLive
            ? chain.LiveXmin <= snapshotXid ? null : WalkHistory(chain.Head, snapshotXid)
            : chain.LiveXmin <= snapshotXid ? livePayload : WalkHistory(chain.Head, snapshotXid);
    }

    /// <summary>
    /// Resolves the historical version visible to <paramref name="snapshotXid"/>
    /// for a slot whose live heap entry is tombstoned. Reached by snapshot-
    /// aware iteration as a second pass over <see cref="HeapTable.RowVersions"/>
    /// — the live-heap pass naturally skips tombstoned slots, so this hook
    /// surfaces deleted rows whose pre-delete state is still visible at the
    /// caller's snapshot. Returns the historical payload or <c>null</c>
    /// (slot's delete is visible — row should not appear at this snapshot).
    /// </summary>
    internal static byte[]? ResolveTombstonedSlotForSnapshot(RowVersionChain chain, long snapshotXid, SimulatedDbTransaction? readerTx)
    {
        // In-flight delete by another writer: the delete commit hasn't
        // landed yet, so my snapshot must walk history (chain.LiveXmin still
        // reflects the pre-delete commit). Committed delete: my snapshot
        // sees the row iff it pre-dates the delete (LiveXmin = delete
        // commit Xid). Walking history finds the entry with Xmax = delete
        // Xid in both cases.
        return chain.WriterTx is { } writer && !ReferenceEquals(writer, readerTx)
            ? WalkHistory(chain.Head, snapshotXid)
            : chain.IsDeletedLive && chain.LiveXmin > snapshotXid
                ? WalkHistory(chain.Head, snapshotXid)
                : null;
    }

    private static byte[]? WalkHistory(HistoricalVersion? head, long snapshotXid)
    {
        for (var hv = head; hv is not null; hv = hv.Next)
        {
            if (hv.Xmin <= snapshotXid && snapshotXid < hv.Xmax)
                return hv.Payload;
        }
        return null;
    }
}
