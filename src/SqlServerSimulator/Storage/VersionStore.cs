using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Pending row-version capture for one in-flight INSERT / UPDATE / DELETE.
/// Buffered on the active <see cref="SimulatedDbTransaction"/> (or on the
/// <see cref="BatchContext"/> for auto-commit statements) until the writer
/// commits — at which point <see cref="VersionStore.FinalizePendingEntries"/>
/// stamps the entries with the commit Xid and pushes historical payloads
/// into <see cref="Heap.RowVersions"/>. Rollback (statement-atomic or
/// explicit <c>ROLLBACK</c>) calls <see cref="VersionStore.DiscardPendingEntries"/>
/// which clears the in-flight <see cref="RowVersionChain.WriterSession"/> markers
/// without disturbing the heap (the undo log already restored it).
/// </summary>
internal sealed class PendingVersionEntry
{
    internal HeapTable Table = null!;

    /// <summary>The heap <see cref="NewRid"/> and <see cref="OldRid"/> address, whose chains the entry settles.</summary>
    internal Heap Heap = null!;

    internal (int Page, int Slot) NewRid;
    internal (int Page, int Slot)? OldRid;
    internal byte[]? OldPayload;
    internal VersionWriteKind Kind;

    /// <summary>
    /// Whether this UPDATE's or DELETE's capture prepended the pending
    /// <see cref="HistoricalVersion"/> that a rollback pops — false when an
    /// earlier write of the same unit already recorded the row's pre-write
    /// state.
    /// </summary>
    internal bool PushedHistory;
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
/// Helper layer over <see cref="Heap.RowVersions"/> that the mutation
/// dispatch path (INSERT / UPDATE / DELETE / MERGE / OUTPUT-INTO /
/// SELECT-INTO / FK cascade) calls into to record the pre-write state of
/// each affected row. Visibility lookups for SNAPSHOT and
/// READ_COMMITTED_SNAPSHOT readers walk the same data structures via
/// <see cref="ResolveVisibleVersion"/>. Capture is a no-op when neither
/// flag is on for the current database — the version-chain dict stays
/// empty and the read path's <see cref="Heap.RowVersions"/> lookup
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
    /// Whether a mutation of <paramref name="table"/> in
    /// <paramref name="database"/> will record pre-write history. The single
    /// source of truth for <see cref="CaptureWrite"/>'s capture guard, and the
    /// inverse of "the superseding undo entry may reclaim the old row's off-row
    /// LOB chains at commit": when this is <c>false</c> no
    /// <see cref="HistoricalVersion"/> ever pins those chains, so the committing
    /// undo entry owns reclamation; when <c>true</c> the history entry owns them
    /// until <see cref="RunGarbageCollection"/> trims it. A memory-optimized
    /// table versions every write whatever the database's flags, since every
    /// read of it is a snapshot read.
    /// </summary>
    internal static bool WillCaptureVersions(Database database, HeapTable table) =>
        (IsVersioningEnabled(database) || table.IsMemoryOptimized)
        && !table.IsTableVariable
        && !BatchContext.IsLocalTempName(table.Name)
        && !Simulation.SystemHeapTables.Values.Contains(table);

    /// <summary>
    /// Captures a pre-write snapshot for the row at
    /// <paramref name="newRid"/> (post-mutation slot). For UPDATE /
    /// DELETE, <paramref name="oldPayload"/> and <paramref name="oldRid"/>
    /// carry the pre-mutation state (for UPDATE these may differ when the
    /// row moves slots); for INSERT both are null and the chain entry is
    /// created in-flight with no history. Marks the chain's
    /// <see cref="RowVersionChain.WriterSession"/> so concurrent SI readers walk
    /// past the live (uncommitted) heap row.
    /// </summary>
    /// <remarks>
    /// Runs before the heap write becomes visible to another session — an
    /// UPDATE or DELETE captures ahead of its heap mutation, an INSERT from
    /// the hook <see cref="Heap.Insert{TState}"/> runs before the slot is
    /// published — so a snapshot never meets a changed row whose chain
    /// doesn't say so yet.
    /// </remarks>
    internal static void CaptureWrite(BatchContext batch, HeapTable table, (int Page, int Slot) newRid, (int Page, int Slot)? oldRid, byte[]? oldPayload, VersionWriteKind kind)
    {
        if (!WillCaptureVersions(batch.DatabaseFor(table), table))
            return;

        lock (table.RowVersionsGate)
            Capture(batch, table, newRid, oldRid, oldPayload, kind);
    }

    private static void Capture(BatchContext batch, HeapTable table, (int Page, int Slot) newRid, (int Page, int Slot)? oldRid, byte[]? oldPayload, VersionWriteKind kind)
    {
        var pendingEntries = batch.ActivePendingVersionEntries();
        var chain = table.Heap.RowVersions.GetOrAdd(newRid, static _ => new RowVersionChain());

        // An UPDATE or DELETE prepends a history entry carrying the
        // pre-mutation payload, with Xmax = PendingXmax until the writer's
        // commit step (FinalizePendingEntries) rewrites it to the actual
        // commit Xid — so a snapshot reader walking history past the
        // in-flight write finds the row as it stood. Carrying the old slot's
        // existing history forward matches the chain semantics — multi-update
        // timelines stay walkable for older snapshots.
        var pushedHistory = false;
        if (kind != VersionWriteKind.Insert && oldPayload is not null && oldRid is { } oldRidValue)
        {
            var oldChain = GetExistingChain(table, oldRidValue);
            if (oldChain is { PendingEntries: { } unit } && ReferenceEquals(unit, pendingEntries))
            {
                // The same unit already wrote this row, so the history it
                // recorded then — or, for a row it inserted, the absence of
                // any — is the row's state before the transaction, the only
                // one a snapshot may see: real keeps one version per row per
                // transaction and never exposes an intermediate one (probed
                // 2026-09-28 against SQL Server 2025).
                if (!ReferenceEquals(oldChain, chain))
                {
                    chain.Head = oldChain.Head;
                    chain.LiveXmin = oldChain.LiveXmin;
                }
            }
            else
            {
                chain.Head = new HistoricalVersion
                {
                    Payload = oldPayload,
                    Xmin = oldChain?.LiveXmin ?? 0,
                    Xmax = PendingXmax,
                    Next = oldChain?.Head,
                    ReclaimColumns = table.Heap.ReclaimColumns,
                };
                pushedHistory = true;
            }
            // Old slot's chain stays until commit — Rollback removes the
            // entire new-slot chain (the slot didn't exist pre-tx) and the
            // old slot retains its prior visibility. Commit drops the old
            // slot since its data has already been carried forward.
        }

        if (pendingEntries is null)
            return;
        // Marked in flight once the pre-write version is in place: a snapshot
        // reading the chain between the two meets a live row the write hasn't
        // changed yet.
        chain.PendingEntries = pendingEntries;
        chain.WriterSession = batch.Connection.Session;
        pendingEntries.Add(new PendingVersionEntry
        {
            Table = table,
            Heap = table.Heap,
            NewRid = newRid,
            OldRid = oldRid,
            OldPayload = oldPayload,
            Kind = kind,
            PushedHistory = pushedHistory,
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
    /// statement end for auto-commit). Draws one commit Xid for the
    /// transaction's whole pending list, walks each entry finalizing the
    /// chain at the live slot, and publishes the Xid last:
    /// <list type="bullet">
    /// <item>INSERT: stamp <see cref="RowVersionChain.LiveXmin"/> with the
    /// commit Xid and clear <see cref="RowVersionChain.WriterSession"/>.</item>
    /// <item>UPDATE: stamp the commit Xid as the <c>Xmax</c> of the pending
    /// <see cref="HistoricalVersion"/> the capture prepended and as the
    /// chain's LiveXmin, and drop the OLD slot's chain entry when the row
    /// moved.</item>
    /// <item>DELETE: the same stamps, and mark the chain as
    /// <see cref="RowVersionChain.IsDeletedLive"/>.</item>
    /// </list>
    /// </summary>
    internal static void FinalizePendingEntries(List<PendingVersionEntry> entries, Simulation simulation, List<HeapTable>? definitionChanges = null)
    {
        if (entries.Count == 0 && definitionChanges is null)
            return;
        // One commit id for the whole transaction, whatever mix of databases it
        // wrote to: the counter is instance-wide, so a cross-database write
        // stamps both sides from the same sequence and a snapshot taken in one
        // database orders correctly against it. Published only once every row
        // carries it (see Simulation.CommitGate).
        lock (simulation.CommitGate)
        {
            var commitXid = simulation.NextTransactionCommitId;
            foreach (var entry in entries)
            {
                lock (entry.Table.RowVersionsGate)
                    Finalize(entry, commitXid);
            }
            if (definitionChanges is not null)
            {
                foreach (var table in definitionChanges)
                    Volatile.Write(ref table.DefinitionXid, commitXid);
            }
            simulation.PublishTransactionCommitId(commitXid);
        }
        entries.Clear();
    }

    /// <summary>
    /// Records that the statement running is creating or redefining
    /// <paramref name="table"/>. Metadata isn't versioned, so a SNAPSHOT
    /// transaction whose snapshot predates the change refuses to reach the
    /// table once it commits — reading or writing, and through a view too —
    /// with Msg 3961, which dooms the transaction (probed 2026-10-01 against
    /// SQL Server 2025: every <c>ALTER TABLE</c> form, <c>CREATE</c> /
    /// <c>DROP</c> / <c>ALTER INDEX</c>, a DML trigger's DDL, a column
    /// rename, <c>TRUNCATE</c> and both sides of a <c>SWITCH</c>, and a table
    /// dropped and created again; not <c>CREATE</c> / <c>UPDATE
    /// STATISTICS</c>, a <c>GRANT</c>, a rolled-back change, nor a snapshot
    /// taken after the change committed). Inside a transaction the stamp
    /// waits for its commit; otherwise the statement draws one now.
    /// </summary>
    internal static void NoteDefinitionChange(BatchContext batch, HeapTable table)
    {
        _ = Interlocked.Increment(ref table.DefinitionVersion);
        if (!Simulation.IsLockableTable(table) || !batch.DatabaseFor(table).AllowSnapshotIsolation)
            return;
        if (batch.Connection.CurrentTransaction is { } transaction)
        {
            (transaction.DefinitionChanges ??= []).Add(table);
            return;
        }
        var simulation = batch.Connection.Simulation;
        lock (simulation.CommitGate)
        {
            var commitXid = simulation.NextTransactionCommitId;
            Volatile.Write(ref table.DefinitionXid, commitXid);
            simulation.PublishTransactionCommitId(commitXid);
        }
    }

    /// <summary>
    /// Sets aside <paramref name="table"/>'s version chains for a statement
    /// replacing its rows wholesale in the same heap — <c>TRUNCATE</c>, either
    /// side of a <c>SWITCH</c> — which no snapshot predating it reaches
    /// (<see cref="NoteDefinitionChange"/>), and whose emptied addresses the
    /// rows written after it reuse: a chain left behind describes another
    /// row there, hiding the new one from a later snapshot read or showing a
    /// row the statement removed. A rollback puts them back.
    /// </summary>
    internal static void SetAsideVersions(BatchContext batch, HeapTable table)
    {
        var heap = table.Heap;
        KeyValuePair<(int PageIndex, int SlotIndex), RowVersionChain>[] chains;
        lock (table.RowVersionsGate)
        {
            if (heap.RowVersions.IsEmptyLockFree())
                return;
            chains = [.. heap.RowVersions];
            heap.RowVersions.Clear();
        }
        Simulation.RecordDdlUndo(batch, () =>
        {
            lock (table.RowVersionsGate)
            {
                foreach (var (address, chain) in chains)
                    heap.RowVersions[address] = chain;
            }
        });
    }

    private static void Finalize(PendingVersionEntry entry, long commitXid)
    {
        var newChain = entry.Heap.RowVersions.GetOrAdd(entry.NewRid, static _ => new RowVersionChain());
        // The pre-write history entry was attached at capture time; commit
        // stamps its Xmax with the real commit Xid and the chain's LiveXmin
        // before the in-flight marks clear.
        if (entry.Kind != VersionWriteKind.Insert && newChain.Head is { Xmax: PendingXmax } pendingHead)
            pendingHead.Xmax = commitXid;
        newChain.LiveXmin = commitXid;
        switch (entry.Kind)
        {
            case VersionWriteKind.Update:
                // Drop the abandoned old-slot chain a moved row left.
                if (entry.OldRid is { } abandonedRid && !abandonedRid.Equals(entry.NewRid))
                    _ = entry.Heap.RowVersions.TryRemove(abandonedRid, out _);
                break;
            case VersionWriteKind.Delete:
                newChain.IsDeletedLive = true;
                break;
            default:
                break;
        }
        newChain.WriterSession = null;
        newChain.PendingEntries = null;
    }

    /// <summary>
    /// Called from <see cref="SimulatedDbTransaction.Rollback()"/> (or on
    /// statement-atomic mid-execution failure). Clears every pending
    /// entry's <see cref="RowVersionChain.WriterSession"/> mark so SI readers
    /// no longer see "uncommitted writer" on those slots; the heap rows
    /// themselves are restored by the undo log. For INSERT the chain is
    /// dropped (the row never existed from any snapshot's perspective);
    /// for UPDATE and DELETE the pending pre-write <see cref="HistoricalVersion"/> is
    /// popped off the chain head, restoring the pre-tx history shape (with
    /// stable RIDs, a row UPDATEd in this tx still has earlier committed
    /// history at the same chain — preserving that is required for SI
    /// readers whose snapshot pre-dates this tx).
    /// </summary>
    internal static void DiscardPendingEntries(List<PendingVersionEntry> entries, List<PendingVersionEntry>? kept = null)
    {
        // A rollback to a savepoint undoes only the writes after it: a row the
        // transaction also wrote before the savepoint stays in flight, its
        // in-flight mark kept, or a snapshot would read the transaction's
        // earlier, still uncommitted write as committed.
        HashSet<(HeapTable, (int, int))>? stillPending = null;
        if (kept is { Count: > 0 })
        {
            stillPending = [];
            foreach (var entry in kept)
                _ = stillPending.Add((entry.Table, entry.NewRid));
        }
        foreach (var entry in entries)
        {
            lock (entry.Table.RowVersionsGate)
                Discard(entry, stillPending);
        }
        entries.Clear();
    }

    private static void Discard(PendingVersionEntry entry, HashSet<(HeapTable, (int, int))>? stillPending)
    {
        if (!entry.Heap.RowVersions.TryGetValue(entry.NewRid, out var chain))
            return;
        if (stillPending is null || !stillPending.Contains((entry.Table, entry.NewRid)))
        {
            chain.WriterSession = null;
            chain.PendingEntries = null;
        }
        switch (entry.Kind)
        {
            case VersionWriteKind.Insert:
                // The chain was created by this tx's INSERT and has no
                // pre-tx history — drop it entirely.
                _ = entry.Heap.RowVersions.TryRemove(entry.NewRid, out _);
                break;
            case VersionWriteKind.Update or VersionWriteKind.Delete when entry.PushedHistory:
                // Pop the pending HV the matching CaptureWrite prepended. An
                // INSERT-then-UPDATE in the same tx leaves the chain with
                // LiveXmin = 0 (the INSERT hadn't committed); a subsequent
                // INSERT-entry discard drops the chain, so here we just strip
                // this write's contribution. Pre-existing chains retain their
                // LiveXmin and any earlier HVs.
                if (chain.Head is { Xmax: PendingXmax } pendingHead)
                    chain.Head = pendingHead.Next;
                break;
            case VersionWriteKind.Update:
                // A later write of a row the unit already wrote pushed
                // nothing; a slot it moved the row into borrowed the row's
                // history and never held it before.
                if (entry.OldRid is { } movedFrom && !movedFrom.Equals(entry.NewRid))
                    _ = entry.Heap.RowVersions.TryRemove(entry.NewRid, out _);
                break;
            default:
                break;
        }
    }

    private static RowVersionChain? GetExistingChain(HeapTable table, (int Page, int Slot) rid) =>
        table.Heap.RowVersions.TryGetValue(rid, out var chain) ? chain : null;

    /// <summary>
    /// Walks every per-table <see cref="Heap.RowVersions"/> chain in
    /// the database and drops <see cref="HistoricalVersion"/> nodes whose
    /// <c>Xmax &lt;= oldest_active_snapshot_xid</c> — no active SI
    /// transaction needs that version anymore. When no SI tx is in flight,
    /// the cutoff is <see cref="Simulation.CurrentTransactionCommitId"/>, so
    /// every finalized HV becomes collectible. Chains that lose their only
    /// HV AND aren't marked deleted-live AND have no in-flight writer AND
    /// whose live row every active snapshot sees are dropped from the dict
    /// entirely; chains that retain at least one
    /// fully-visible-to-no-active-snapshot HV stay (later GC passes may
    /// shorten them further). Skips chains with non-null
    /// <see cref="RowVersionChain.WriterSession"/> — those have an in-flight
    /// writer whose pending HV uses the <c>PendingXmax</c> sentinel and
    /// must not be touched.
    /// </summary>
    internal static void RunGarbageCollection(Simulation simulation, Database database)
    {
        // A session abandoned mid-SNAPSHOT still holds its registration, so its
        // stamp would keep every later version pinned. Reclaiming first is what
        // lets the cutoff below advance past it — the same reason real's
        // version store shrinks once a dead client's session is reset.
        _ = simulation.ReclaimAbandonedSessions();
        var cutoff = OldestActiveSnapshotXid(simulation);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.Heap.RowVersions.IsEmptyLockFree())
                    continue;
                List<HistoricalVersion>? dropped = null;
                lock (table.RowVersionsGate)
                {
                    foreach (var kv in table.Heap.RowVersions)
                    {
                        var chain = kv.Value;
                        if (chain.WriterSession is not null)
                            continue;
                        chain.Head = TrimHistory(chain.Head, cutoff, ref dropped);
                        // A chain with no history left still hides its row from a
                        // snapshot older than the row's commit — a row inserted
                        // since — so it stays until every snapshot sees the row.
                        if (chain.Head is null && !chain.IsDeletedLive && chain.LiveXmin <= cutoff)
                            _ = table.Heap.RowVersions.TryRemove(kv.Key, out _);
                    }
                }
                // Each dropped version was detached under the gate exactly
                // once, so its off-row chains are freed exactly once; freeing
                // takes the heap's latch, which the gate may not hold.
                if (dropped is not null)
                {
                    foreach (var version in dropped)
                        FreeVersionChains(table, version, simulation.LobReclamation);
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
    private static HistoricalVersion? TrimHistory(HistoricalVersion? head, long cutoff, ref List<HistoricalVersion>? dropped)
    {
        var node = head;
        HistoricalVersion? previous = null;
        while (node is not null)
        {
            if (node.Xmax <= cutoff)
            {
                // node and everything after it are invisible to every active
                // (and future) snapshot, so they're being dropped — the caller
                // reclaims each dropped version's off-row LOB chains. Each
                // historical payload owns a chain distinct from the live row's
                // and from newer versions (a write allocates fresh chains), so
                // freeing them can't strand a still-referenced chain.
                for (var version = node; version is not null; version = version.Next)
                    (dropped ??= []).Add(version);
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
    /// Returns the off-row LOB chains referenced by a dropped historical
    /// version's payload to the heap's free-list. No-op when the table carries
    /// no reclaim layout or the payload is empty.
    /// </summary>
    /// <remarks>
    /// A version captured under a layout the table has since replaced — an
    /// <c>ALTER TABLE</c> changed its columns — keeps its chains: its payload
    /// no longer decodes against the table's columns, and the change that
    /// replaced them rewrote the rows it stood for.
    /// </remarks>
    private static void FreeVersionChains(HeapTable table, HistoricalVersion version, LobReclamation reclamation)
    {
        if (table.Heap.ReclaimColumns is not { } columns || version.Payload.Length == 0 || !ReferenceEquals(version.ReclaimColumns, columns))
            return;
        var heads = new List<int>(1);
        RowDecoder.CollectLobHeads(columns, version.Payload, heads);
        for (var i = 0; i < heads.Count; i++)
            table.Heap.RetireLobChain(heads[i], reclamation);
    }

    /// <summary>
    /// Smallest <see cref="SimulatedDbTransaction.SnapshotXid"/> across the
    /// simulation's <see cref="Simulation.ActiveSnapshotTxs"/> set, or the
    /// current commit-id counter when no SI tx is in flight. The set is
    /// instance-wide because a stamp is: a snapshot open in one database can
    /// read another's history, so any open snapshot pins every database's
    /// versions. The empty-set case returns the latest stamp so the GC can
    /// drop every finalized HV. A statement's own snapshot — RCSI's, an
    /// autocommit SNAPSHOT statement's — counts too, through
    /// <see cref="SessionToken.StatementSnapshotXid"/>: a commit landing while
    /// such a read runs collects at its own end, and dropping the versions the
    /// read still needed showed it the commit's rows half written.
    /// </summary>
    /// <remarks>
    /// The counter is read before the registrations, and a SNAPSHOT
    /// transaction registers before it reads its stamp (see
    /// <c>BatchContext.ResolveSnapshotXidForRead</c>): a sweep that misses a
    /// registration therefore read a counter no later than that snapshot's
    /// stamp, and can't drop what the snapshot reads.
    /// </remarks>
    private static long OldestActiveSnapshotXid(Simulation simulation)
    {
        var min = simulation.CurrentTransactionCommitId;
        foreach (var (_, registration) in simulation.ActiveSnapshotTxs)
        {
            if (registration.SnapshotXid < min)
                min = registration.SnapshotXid;
        }
        lock (simulation.Sessions)
        {
            foreach (var session in simulation.Sessions)
                min = Math.Min(min, Volatile.Read(ref session.StatementSnapshotXid));
        }
        return min;
    }

    /// <summary>
    /// Pre-write conflict check for SNAPSHOT-isolation writers. Raises
    /// Msg 3960 (auto-rollback semantic — caller is responsible for
    /// calling <see cref="SimulatedDbTransaction.Rollback()"/> after the
    /// throw) when the live row at <paramref name="rid"/> was committed
    /// by a different transaction after the SI writer's snapshot. Returns
    /// silently when the row is safe to overwrite. No-op when not under
    /// SI (the caller checks <see cref="SimulatedDbConnection.SessionIsolationLevel"/>
    /// before calling).
    /// </summary>
    internal static void CheckSnapshotUpdateConflict(BatchContext batch, HeapTable table, (int Page, int Slot) rid, bool delete = false)
    {
        var connection = batch.Connection;
        if (WriterSnapshotXid(batch, table) is not { } sx)
            return;
        if (!table.Heap.RowVersions.TryGetValue(rid, out var chain))
            return;
        if (chain.LiveXmin <= sx && (chain.WriterSession is null || ReferenceEquals(chain.WriterSession, connection.Session)))
            return;
        // A memory-optimized row's conflict dooms the transaction through the
        // error's own class rather than rolling it back here.
        if (table.IsMemoryOptimized)
            throw SimulatedSqlException.MemoryOptimizedWriteConflict(delete);
        // Row was modified by another tx after my snapshot. Probe-confirmed
        // auto-rollback: the SI tx terminates with @@TRANCOUNT = 0.
        connection.CurrentTransaction?.EndRollback();
        throw SimulatedSqlException.SnapshotIsolationUpdateConflict($"{Database.DefaultSchemaName}.{table.Name}", batch.DatabaseFor(table).Name, table.HasClusteredIndex());
    }

    /// <summary>
    /// The snapshot a write of <paramref name="table"/> is judged against for
    /// an update conflict: a SNAPSHOT transaction's, or — for a
    /// memory-optimized table, whose writes are judged at any isolation level
    /// — the transaction's or else the statement's. Null when the write isn't
    /// judged, or no read has taken the snapshot yet.
    /// </summary>
    internal static long? WriterSnapshotXid(BatchContext batch, HeapTable table) =>
        table.IsMemoryOptimized ? batch.Connection.CurrentTransaction?.SnapshotXid ?? batch.RcsiStatementSnapshotXid
        : batch.Connection.SessionIsolationLevel == System.Data.IsolationLevel.Snapshot ? batch.Connection.CurrentTransaction?.SnapshotXid
        : null;

    /// <summary>
    /// The version a snapshot at <paramref name="snapshotXid"/> reads at
    /// <paramref name="rid"/>, from the slot as a scan read it —
    /// <paramref name="slotBytes"/>, null for a deleted slot — at heap
    /// <paramref name="sequence"/> (<see cref="Heap.EnumerateSlots"/>). The
    /// slot and its chain are read at different moments; a write that moved
    /// the heap between them sends the slot to be read again, which is what
    /// makes the pair consistent: a writer's chain is in place before its heap
    /// write shows, and stays until the heap is rolled back.
    /// </summary>
    internal static byte[]? ReadSnapshotSlot(HeapTable table, (int Page, int Slot) rid, byte[]? slotBytes, int sequence, long snapshotXid, SessionToken reader)
    {
        while (true)
        {
            var resolved = slotBytes is not null
                ? ResolveVisibleVersion(table, rid, slotBytes, snapshotXid, reader)
                : table.Heap.RowVersions.TryGetValue(rid, out var chain) ? ResolveTombstonedSlotForSnapshot(chain, snapshotXid, reader) : null;
            if (table.Heap.WriteSequence == sequence)
                return resolved;
            if (!table.Heap.TryReadSlot(rid.Page, rid.Slot, out slotBytes, out sequence))
                slotBytes = null;
        }
    }

    /// <summary>
    /// Resolves the version of the slot's row visible at
    /// <paramref name="snapshotXid"/>. Returns <c>null</c> when no version
    /// is visible (row inserted after the snapshot, or deleted before
    /// it). Returns the historical payload when the live row was
    /// committed after the snapshot; returns <paramref name="livePayload"/>
    /// when the live row is visible directly.
    /// </summary>
    internal static byte[]? ResolveVisibleVersion(HeapTable table, (int Page, int Slot) rid, byte[] livePayload, long snapshotXid, SessionToken reader)
    {
        if (!table.Heap.RowVersions.TryGetValue(rid, out var chain))
            return livePayload;
        if (chain.WriterSession is { } writer && !ReferenceEquals(writer, reader))
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
    /// aware iteration as a second pass over <see cref="Heap.RowVersions"/>
    /// — the live-heap pass naturally skips tombstoned slots, so this hook
    /// surfaces deleted rows whose pre-delete state is still visible at the
    /// caller's snapshot. Returns the historical payload or <c>null</c>
    /// (slot's delete is visible — row should not appear at this snapshot).
    /// </summary>
    internal static byte[]? ResolveTombstonedSlotForSnapshot(RowVersionChain chain, long snapshotXid, SessionToken reader)
    {
        // In-flight delete by another writer: the delete commit hasn't
        // landed yet, so my snapshot must walk history (chain.LiveXmin still
        // reflects the pre-delete commit). Committed delete: my snapshot
        // sees the row iff it pre-dates the delete (LiveXmin = delete
        // commit Xid). Walking history finds the entry with Xmax = delete
        // Xid in both cases.
        return chain.WriterSession is { } writer && !ReferenceEquals(writer, reader)
            ? WalkHistory(chain.Head, snapshotXid)
            : chain.IsDeletedLive && chain.LiveXmin > snapshotXid
                ? WalkHistory(chain.Head, snapshotXid)
                : null;
    }

    /// <summary>
    /// For a slot whose live row another transaction changed after
    /// <paramref name="snapshotXid"/> — committed since, or still in flight —
    /// the version the snapshot sees instead; <c>null</c> when the live row
    /// is the snapshot's own view, was inserted after it, or is the reader's
    /// own write.
    /// </summary>
    internal static byte[]? ResolveChangedLiveSlotForSnapshot(RowVersionChain chain, long snapshotXid, SessionToken reader)
    {
        if (chain.IsDeletedLive)
            return null;
        if (chain.WriterSession is { } writer)
            return ReferenceEquals(writer, reader) ? null : WalkHistory(chain.Head, snapshotXid);
        return chain.LiveXmin > snapshotXid ? WalkHistory(chain.Head, snapshotXid) : null;
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
