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
    /// themselves are restored by the undo log. For INSERT entries the
    /// chain itself is dropped (the row never existed from any
    /// snapshot's perspective).
    /// </summary>
    internal static void DiscardPendingEntries(List<PendingVersionEntry> entries)
    {
        foreach (var entry in entries)
        {
            // INSERT / UPDATE both created the chain at NewRid in this tx
            // (slot indices never reuse — every new write goes to a fresh
            // slot). Roll back by removing the entire chain at NewRid; the
            // heap row itself is restored by the undo log. DELETE leaves
            // the chain entry intact — its WriterTx mark gets cleared so
            // future SI readers consult the live (un-tombstoned) row.
            if (entry.Kind is VersionWriteKind.Insert or VersionWriteKind.Update)
            {
                _ = entry.Table.RowVersions.TryRemove(entry.NewRid, out _);
                continue;
            }
            if (GetExistingChain(entry.Table, entry.NewRid) is { } chain)
                chain.WriterTx = null;
        }
        entries.Clear();
    }

    private static RowVersionChain? GetExistingChain(HeapTable table, (int Page, int Slot) rid) =>
        table.RowVersions.TryGetValue(rid, out var chain) ? chain : null;

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
