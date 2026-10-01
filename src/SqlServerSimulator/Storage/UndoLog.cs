using System.Collections.Concurrent;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Discriminator for the heap-mutation entries an <see cref="UndoLog"/>
/// records. Each entry's reverse operation is the symmetric one:
/// <see cref="Insert"/> rolls back by tombstoning the slot;
/// <see cref="Delete"/> rolls back by clearing the tombstone bit.
/// </summary>
internal enum UndoKind
{
    Insert,
    Delete,
}

/// <summary>
/// Per-slot UPDATE flavor recorded by <see cref="UndoLog"/>. Each variant's
/// undo is the inverse of <see cref="Heap.UpdateAt"/>'s mutating action: an
/// <see cref="InPlaceRewrite"/> overwrites the slot back to its pre-UPDATE
/// payload; a <see cref="ForwardInstall"/> additionally clears the forward
/// bit; a <see cref="ForwardRetarget"/> restores the pre-UPDATE forward
/// target. The paired insert at the new target (and the paired tombstone of
/// the old target, in the retarget case) ride their own
/// <see cref="UndoKind.Insert"/> / <see cref="UndoKind.Delete"/> entries.
/// </summary>
internal enum SlotRewriteKind
{
    InPlaceRewrite,
    ForwardInstall,
    ForwardRetarget,
}

/// <summary>
/// Per-statement (Bundle 1) / per-connection-transaction (Bundle 2) record
/// of heap mutations and temp-table DDL, walked in reverse on rollback.
/// Insert entries are undone by tombstoning the slot they created; delete
/// entries by clearing the tombstone bit on the slot they cleared.
/// <c>CREATE TABLE #foo</c> entries undo by removing the table from the
/// connection's <see cref="SimulatedDbConnection.TempTables"/> dict;
/// <c>DROP TABLE #foo</c> entries undo by restoring the table. UPDATE
/// decomposes to a delete-of-old plus an insert-of-new pair, so its
/// rollback is naturally the inverse pair walked LIFO.
/// </summary>
/// <remarks>
/// Identity counters and the database-scoped rowversion counter are
/// intentionally outside the log — probe-confirmed against SQL Server
/// 2025 (2026-05-08): both keep advancing even when the writes that
/// consumed their values are rolled back. Off-row LOB chains, by contrast,
/// are reclaimed: a slot entry's <see cref="UndoEntry.Commit"/> returns the
/// chains a committed UPDATE / DELETE superseded to the heap's free-list, and
/// an Insert / InPlaceRewrite entry's <see cref="UndoEntry.Undo"/> frees the
/// chain a rolled-back write allocated (the dead heap-page row-payload bytes
/// still leak — that's a separate CLAUDE.md quirk). Regular
/// <c>CREATE TABLE</c> / <c>DROP TABLE</c> (non-<c>#</c>) doesn't append
/// entries either — it's a known asymmetry with temp DDL that's
/// transactional; document it where the temp behavior is described.
/// </remarks>
/// <param name="reclamation">
/// Defers the reuse of the off-row chains the log frees until no running
/// statement can hold an image naming them; null for a table variable's log,
/// whose rows only its own session reads.
/// </param>
internal sealed class UndoLog(LobReclamation? reclamation)
{
    private readonly List<UndoEntry> entries = [];

    // Set by the first change tracking entry, so a commit that recorded none
    // skips the versioning pass.
    private bool recordsChangeTracking;

    /// <summary>
    /// Whether an entry changed a row of one of <paramref name="heaps"/>, or —
    /// with <paramref name="temporaryTables"/> — created or dropped a temporary
    /// table: what <c>DBCC OPENTRAN</c> asks of another session's transaction.
    /// Walked by index, since that session may be appending meanwhile.
    /// </summary>
    public bool Changes(HashSet<Heap> heaps, bool temporaryTables)
    {
        for (var i = 0; i < this.entries.Count; i++)
        {
            var entry = this.entries[i];
            if (entry.AffectedHeap is { } heap
                ? heaps.Contains(heap)
                : temporaryTables && entry is TempTableCreation or TempTableRemoval or LocalTempTableCreation or LocalTempTableRemoval)
            {
                return true;
            }
        }
        return false;
    }

    public void RecordInsert(Heap heap, int pageIndex, int slotIndex) =>
        this.entries.Add(new SlotChange(heap, UndoKind.Insert, pageIndex, slotIndex, freeOnCommit: false));

    public void RecordDelete(Heap heap, int pageIndex, int slotIndex, bool freeOnCommit = false) =>
        this.entries.Add(new SlotChange(heap, UndoKind.Delete, pageIndex, slotIndex, freeOnCommit));

    public void RecordInPlaceRewrite(Heap heap, int pageIndex, int slotIndex, byte[] oldPayload, bool freeOnCommit = false) =>
        this.entries.Add(new SlotRewrite(heap, SlotRewriteKind.InPlaceRewrite, pageIndex, slotIndex, oldPayload, default, default, freeOnCommit));

    public void RecordForwardInstall(Heap heap, int pageIndex, int slotIndex, byte[] oldPayload, (int Page, int Slot) installedTarget, bool freeOnCommit = false) =>
        this.entries.Add(new SlotRewrite(heap, SlotRewriteKind.ForwardInstall, pageIndex, slotIndex, oldPayload, default, installedTarget, freeOnCommit));

    public void RecordForwardRetarget(Heap heap, int pageIndex, int slotIndex, (int Page, int Slot) oldTarget, (int Page, int Slot) newTarget) =>
        this.entries.Add(new SlotRewrite(heap, SlotRewriteKind.ForwardRetarget, pageIndex, slotIndex, [], oldTarget, newTarget, freeOnCommit: false));

    public void RecordForwardedPointerDelete(Heap heap, int pageIndex, int slotIndex, (int Page, int Slot) target) =>
        this.entries.Add(new ForwardedPointerDelete(heap, pageIndex, slotIndex, target));

    public void RecordTempTableCreation(ConcurrentDictionary<string, HeapTable> owner, string name) =>
        this.entries.Add(new TempTableCreation(owner, name));

    public void RecordTempTableRemoval(ConcurrentDictionary<string, HeapTable> owner, string name, HeapTable table) =>
        this.entries.Add(new TempTableRemoval(owner, name, table));

    /// <summary>
    /// A local temp table's create and drop go through the session's registry
    /// rather than a plain dictionary, so undoing one also restores which of
    /// the same-named tables a nested scope hides is visible.
    /// </summary>
    public void RecordLocalTempTableCreation(SimulatedDbConnection connection, HeapTable table) =>
        this.entries.Add(new LocalTempTableCreation(connection, table));

    /// <inheritdoc cref="RecordLocalTempTableCreation"/>
    public void RecordLocalTempTableRemoval(SimulatedDbConnection connection, HeapTable table) =>
        this.entries.Add(new LocalTempTableRemoval(connection, table));

    /// <summary>
    /// Records a catalog change to a permanent object — created, altered or
    /// dropped — whose rollback runs <paramref name="undo"/> and then
    /// invalidates every cached plan, since a plan compiled since may name the
    /// object as it stood.
    /// </summary>
    public void RecordSchemaChange(Simulation simulation, Action undo) =>
        this.entries.Add(new SchemaChange(simulation, undo));

    /// <summary>
    /// Records a <c>DBCC CHECKIDENT</c> reseed, which a rollback undoes
    /// (probed 2026-09-24 against SQL Server 2025) though a generated value
    /// never is.
    /// </summary>
    public void RecordIdentityReseed(IdentityState state, (Int128? HighWaterMark, Int128? ReseededStart) snapshot) =>
        this.entries.Add(new IdentityReseed(state, snapshot));

    /// <summary>
    /// Records a row's prior <see cref="Heap.Uniquifiers"/> entry (0 for none)
    /// before a clustered-key update redraws it, so a rollback restores the
    /// row's cursor identity.
    /// </summary>
    public void RecordUniquifier(Heap heap, (int Page, int Slot) address, long previous) =>
        this.entries.Add(new UniquifierChange(heap, address, previous));

    public void RecordTruncation(Heap heap, List<HeapPage> oldPages, List<HeapLobPage> oldLobPages, HashSet<(int Page, int Slot)> oldForwardTargets, int[] oldFreeLobPages, (IdentityState State, Int128? HighWaterMark)[] identitySnapshots) =>
        this.entries.Add(new HeapTruncation(heap, oldPages, oldLobPages, oldForwardTargets, oldFreeLobPages, identitySnapshots));

    /// <summary>
    /// Records a write to a change-tracked table, published with its
    /// transaction's version when the log commits and dropped when the write
    /// rolls back.
    /// </summary>
    public void RecordChangeTracking(PendingRowChange change)
    {
        this.recordsChangeTracking = true;
        this.entries.Add(new ChangeTrackingRow(change));
    }

    /// <summary>
    /// Records a truncation of a change-tracked table, which on commit forgets
    /// the table's history and raises its minimum valid version.
    /// </summary>
    public void RecordChangeTrackingTruncation(TableChangeTracking tracking, Database database)
    {
        this.recordsChangeTracking = true;
        this.entries.Add(new ChangeTrackingTruncation(tracking, database));
    }

    /// <summary>
    /// Whether this log holds an uncommitted change to the tracked row
    /// <paramref name="key"/> — which <c>CHANGETABLE(VERSION …)</c> reports as
    /// a NULL version inside the writing transaction.
    /// </summary>
    public bool HasPendingChange(TableChangeTracking tracking, Parser.SqlValueKey key)
    {
        if (!this.recordsChangeTracking)
            return false;
        foreach (var entry in this.entries)
        {
            if (entry is ChangeTrackingRow row && ReferenceEquals(row.Change.Tracking, tracking) && key.Equals(new Parser.SqlValueKey(row.Change.Key)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Current end-of-log position, captured by callers as a marker before a
    /// scope of mutations so a later <see cref="RollbackTo"/> can undo only
    /// that scope. Used both for statement-level atomicity (marker = position
    /// at statement start) and for explicit transactions where a failed
    /// statement undoes its own writes without disturbing prior committed-
    /// to-the-tx writes.
    /// </summary>
    public int Position => this.entries.Count;

    /// <summary>
    /// Walks the log in LIFO order from the current end down to (and not
    /// including) <paramref name="position"/>, applying the inverse
    /// operation for each entry, and trims the log to that length. A
    /// position of 0 unwinds the entire log (equivalent to
    /// <see cref="Rollback"/>). A position past the end is already undone:
    /// an error inside a trigger body can roll the whole transaction back
    /// before the firing statement unwinds to the marker it took.
    /// </summary>
    public void RollbackTo(int position)
    {
        if (position >= this.entries.Count)
            return;

        for (var i = this.entries.Count - 1; i >= position; i--)
            this.entries[i].Undo(reclamation);

        // Undo rewinds heap state by mutating pages directly — it produces no
        // reversing seek-journal events and doesn't advance MutationGeneration,
        // so a seek cache built mid-transaction would otherwise carry the
        // rolled-back rows. Invalidate each touched heap's journal once so the
        // next seek rebuilds from the rewound state (the agreed rollback safety
        // valve for the incrementally-maintained equality-seek cache).
        HashSet<Heap>? touched = null;
        for (var i = position; i < this.entries.Count; i++)
        {
            if (this.entries[i].AffectedHeap is { } heap && (touched ??= []).Add(heap))
                heap.InvalidateSeekJournal();
        }

        this.entries.RemoveRange(position, this.entries.Count - position);
    }

    /// <summary>
    /// Convenience — full rollback to position 0. Equivalent to
    /// <c>RollbackTo(0)</c>.
    /// </summary>
    public void Rollback() => RollbackTo(0);

    /// <summary>
    /// Finalizes the log on transaction commit: each entry's
    /// <see cref="UndoEntry.Commit"/> runs (reclaiming the off-row LOB chains
    /// a committed UPDATE / DELETE superseded, where the entry was recorded
    /// with <c>freeOnCommit</c>), then all entries are discarded. The heap
    /// writes themselves stay — the log's only remaining job at commit is to
    /// hand back the storage the superseded rows no longer need. Replaces the
    /// former discard-only <c>Clear</c>; rollback still goes through
    /// <see cref="Rollback"/> / <see cref="RollbackTo"/>, which never call
    /// <see cref="UndoEntry.Commit"/>.
    /// </summary>
    public void Commit()
    {
        if (this.recordsChangeTracking)
        {
            PublishChangeTracking();
            this.recordsChangeTracking = false;
        }
        for (var i = 0; i < this.entries.Count; i++)
            this.entries[i].Commit(reclamation);
        this.entries.Clear();
    }

    /// <summary>
    /// Gives each database whose tracked tables this transaction changed one
    /// new version, and publishes the changes under it in log order. A
    /// truncation alone draws no version (probed 2026-09-27 against SQL
    /// Server 2025), so its minimum valid version is the one current before
    /// the transaction's own.
    /// </summary>
    private void PublishChangeTracking()
    {
        var databases = new List<Database>(1);
        foreach (var entry in this.entries)
        {
            var database = entry switch
            {
                ChangeTrackingRow row => row.Change.Database,
                ChangeTrackingTruncation truncation => truncation.Database,
                _ => null,
            };
            if (database is not null && !databases.Contains(database))
                databases.Add(database);
        }
        foreach (var database in databases)
            database.CommitChangeTracking(version => this.PublishChangeTracking(database, version));
    }

    // Applies this log's tracked changes to one database under the version
    // its commit draws; returns whether any change drew it.
    private bool PublishChangeTracking(Database database, long version)
    {
        var drawn = false;
        foreach (var entry in this.entries)
        {
            switch (entry)
            {
                case ChangeTrackingRow row when row.Change.Database == database:
                    row.Change.Tracking.Apply(row.Change, version);
                    drawn = true;
                    break;
                case ChangeTrackingTruncation truncation when truncation.Database == database:
                    truncation.Tracking.Reset(version - 1);
                    break;
                default:
                    break;
            }
        }
        return drawn;
    }

    /// <summary>
    /// Reads the row at <paramref name="page"/>/<paramref name="slot"/>
    /// (dereferencing one level of forwarding) and returns its off-row LOB
    /// chains to the heap's free-list. No-op when the heap carries no
    /// reclaim layout (no off-row-capable column, or a bare scratch heap).
    /// </summary>
    private static void FreeChainsAtSlot(Heap heap, int page, int slot, LobReclamation? reclamation)
    {
        if (heap.ReclaimColumns is null)
            return;
        var bytes = heap.ReadSlotBytes(page, slot);
        if (bytes is not null)
            FreeChainsInBytes(heap, bytes, reclamation);
    }

    /// <summary>Frees the off-row LOB chains referenced by an already-materialized row image.</summary>
    private static void FreeChainsInBytes(Heap heap, ReadOnlySpan<byte> rowBytes, LobReclamation? reclamation)
    {
        if (heap.ReclaimColumns is not { } columns)
            return;
        var heads = new List<int>(1);
        RowDecoder.CollectLobHeads(columns, rowBytes, heads);
        for (var i = 0; i < heads.Count; i++)
            heap.RetireLobChain(heads[i], reclamation);
    }

    private abstract class UndoEntry
    {
        public abstract void Undo(LobReclamation? reclamation);

        /// <summary>
        /// The heap this entry mutates, or null for entries that touch no heap
        /// (temp-table DDL). <see cref="RollbackTo"/> uses it to invalidate each
        /// affected heap's seek journal exactly once after a rollback.
        /// </summary>
        public virtual Heap? AffectedHeap => null;

        /// <summary>
        /// Runs when the enclosing transaction commits. Default no-op; the
        /// slot-mutation entries override it to reclaim superseded LOB chains.
        /// </summary>
        public virtual void Commit(LobReclamation? reclamation)
        {
        }
    }

    private sealed class SlotChange(Heap heap, UndoKind kind, int pageIndex, int slotIndex, bool freeOnCommit) : UndoEntry
    {
        public readonly Heap Heap = heap;
        public readonly UndoKind Kind = kind;
        public readonly int PageIndex = pageIndex;
        public readonly int SlotIndex = slotIndex;
        public readonly bool FreeOnCommit = freeOnCommit;

        public override Heap? AffectedHeap => this.Heap;

        public override void Undo(LobReclamation? reclamation)
        {
            using var latch = this.Heap.EnterLatch();
            var page = this.Heap.Pages[this.PageIndex];
            switch (this.Kind)
            {
                case UndoKind.Insert:
                    // The row this INSERT created is being unwound; its off-row
                    // chains were allocated by the rolled-back statement and no
                    // surviving row, undo entry, or version references them.
                    // Rollback is terminal — the row can't come back and an
                    // uncommitted insert is invisible to every snapshot — so the
                    // slot is reclaimable just like a committed DELETE's: mark it
                    // so Compact packs the row-payload bytes away and later
                    // inserts reuse the space, instead of leaking a slot's worth
                    // of bytes per rolled-back insert.
                    FreeChainsAtSlot(this.Heap, this.PageIndex, this.SlotIndex, reclamation);
                    page.DeleteSlot(this.SlotIndex);
                    page.MarkSlotReclaimable(this.SlotIndex);
                    this.Heap.MarkPageReclaimable(this.PageIndex);
                    break;
                case UndoKind.Delete:
                    // Un-tombstone: the row (and its chains) become live again,
                    // so nothing is freed here.
                    page.UndeleteSlot(this.SlotIndex);
                    break;
            }
        }

        public override void Commit(LobReclamation? reclamation)
        {
            if (this.Kind != UndoKind.Delete)
                return;
            // A committed DELETE's row is permanently gone. Reclaim its off-row
            // LOB chains when no version owns them (freeOnCommit = versioning was
            // off), and mark its heap-page slot reclaimable so compaction can
            // pack away the row-payload bytes and reuse the space — independent
            // of versioning, since snapshot history reads a version-store copy,
            // not the live (now tombstoned) slot.
            using var latch = this.Heap.EnterLatch();
            if (this.FreeOnCommit)
                FreeChainsAtSlot(this.Heap, this.PageIndex, this.SlotIndex, reclamation);
            this.Heap.Pages[this.PageIndex].MarkSlotReclaimable(this.SlotIndex);
            this.Heap.MarkPageReclaimable(this.PageIndex);
        }
    }

    /// <summary>
    /// Undo entry for an UPDATE flavor recorded by <see cref="Heap.UpdateAt"/>.
    /// See <see cref="SlotRewriteKind"/> for the per-variant inverse.
    /// <c>SecondaryTarget</c> carries the installed target on ForwardInstall and
    /// the new target on ForwardRetarget; <c>OldTarget</c> carries the
    /// pre-UPDATE forward target on ForwardRetarget.
    /// </summary>
    private sealed class SlotRewrite(Heap heap, SlotRewriteKind kind, int pageIndex, int slotIndex, byte[] oldPayload, (int Page, int Slot) oldTarget, (int Page, int Slot) secondaryTarget, bool freeOnCommit) : UndoEntry
    {
        public readonly Heap Heap = heap;
        public readonly SlotRewriteKind Kind = kind;
        public readonly int PageIndex = pageIndex;
        public readonly int SlotIndex = slotIndex;
        public readonly byte[] OldPayload = oldPayload;
        public readonly (int Page, int Slot) OldTarget = oldTarget;
        public readonly (int Page, int Slot) SecondaryTarget = secondaryTarget;
        public readonly bool FreeOnCommit = freeOnCommit;

        public override Heap? AffectedHeap => this.Heap;

        public override void Undo(LobReclamation? reclamation)
        {
            using var latch = this.Heap.EnterLatch();
            var page = this.Heap.Pages[this.PageIndex];
            switch (this.Kind)
            {
                case SlotRewriteKind.InPlaceRewrite:
                    // At this point the slot holds the (rolled-back) new payload;
                    // free its chains before overwriting with the old image,
                    // whose chains were never freed (Commit didn't run).
                    FreeChainsAtSlot(this.Heap, this.PageIndex, this.SlotIndex, reclamation);
                    page.RewriteSlotInPlace(this.SlotIndex, this.OldPayload);
                    break;
                case SlotRewriteKind.ForwardInstall:
                    // The new payload lives at the installed target; its chains
                    // ride that target's paired Insert entry (freed there on
                    // rollback). Here we only restore the original slot's old
                    // image — its chains stay live, so nothing is freed.
                    page.ClearForward(this.SlotIndex);
                    page.RewriteSlotInPlace(this.SlotIndex, this.OldPayload);
                    this.Heap.UnregisterForwardTargetForUndo(this.SecondaryTarget);
                    break;
                case SlotRewriteKind.ForwardRetarget:
                    // Pointer-only swap; the old target's chains ride its paired
                    // Delete entry and the new target's its paired Insert entry.
                    page.RewriteForward(this.SlotIndex, this.OldTarget);
                    this.Heap.SwapForwardTargetForUndo(this.OldTarget, this.SecondaryTarget);
                    break;
            }
        }

        public override void Commit(LobReclamation? reclamation)
        {
            // InPlaceRewrite / ForwardInstall both supersede the original
            // row, whose pre-update image is OldPayload — reclaim its off-row
            // chains when no version owns them. ForwardRetarget carries no
            // superseded payload of its own (its old target rides a Delete).
            if (this.FreeOnCommit && this.Kind != SlotRewriteKind.ForwardRetarget)
                FreeChainsInBytes(this.Heap, this.OldPayload, reclamation);
        }
    }

    /// <summary>
    /// Undo entry for the forwarding-pointer half of a forwarded-row DELETE
    /// (the relocated target is handled by a paired <see cref="UndoKind.Delete"/>
    /// <see cref="SlotChange"/>). The pointer slot holds a forward pointer, not a
    /// row, so commit reclaims only its directory slot — never decoding it for
    /// LOB chains — and undo resurrects the pointer and re-registers the target.
    /// </summary>
    private sealed class ForwardedPointerDelete(Heap heap, int pageIndex, int slotIndex, (int Page, int Slot) target) : UndoEntry
    {
        public readonly Heap Heap = heap;
        public readonly int PageIndex = pageIndex;
        public readonly int SlotIndex = slotIndex;
        public readonly (int Page, int Slot) Target = target;

        public override Heap? AffectedHeap => this.Heap;

        public override void Undo(LobReclamation? reclamation)
        {
            using var latch = this.Heap.EnterLatch();
            this.Heap.Pages[this.PageIndex].UndeleteSlot(this.SlotIndex);
            this.Heap.ReinstateForwardTargetForUndo(this.Target);
        }

        public override void Commit(LobReclamation? reclamation)
        {
            using var latch = this.Heap.EnterLatch();
            this.Heap.Pages[this.PageIndex].MarkSlotReclaimable(this.SlotIndex);
            this.Heap.MarkPageReclaimable(this.PageIndex);
        }
    }

    private sealed class UniquifierChange(Heap heap, (int Page, int Slot) address, long previous) : UndoEntry
    {
        public override void Undo(LobReclamation? reclamation)
        {
            if (previous == 0)
                _ = heap.Uniquifiers!.TryRemove(address, out _);
            else
                heap.Uniquifiers![address] = previous;
        }
    }

    private sealed class IdentityReseed(IdentityState state, (Int128? HighWaterMark, Int128? ReseededStart) snapshot) : UndoEntry
    {
        public override void Undo(LobReclamation? reclamation) => state.RestoreReseed(snapshot);
    }

    private sealed class TempTableCreation(ConcurrentDictionary<string, HeapTable> owner, string name) : UndoEntry
    {
        public readonly ConcurrentDictionary<string, HeapTable> Owner = owner;
        public readonly string Name = name;

        public override void Undo(LobReclamation? reclamation) => this.Owner.TryRemove(this.Name, out _);
    }

    private sealed class LocalTempTableCreation(SimulatedDbConnection connection, HeapTable table) : UndoEntry
    {
        public readonly SimulatedDbConnection Connection = connection;
        public readonly HeapTable Table = table;

        public override void Undo(LobReclamation? reclamation) => this.Connection.RemoveTempTable(this.Table);
    }

    private sealed class LocalTempTableRemoval(SimulatedDbConnection connection, HeapTable table) : UndoEntry
    {
        public readonly SimulatedDbConnection Connection = connection;
        public readonly HeapTable Table = table;

        public override void Undo(LobReclamation? reclamation) => this.Connection.ReinstateTempTable(this.Table);
    }

    // Neither change tracking entry has anything to undo: dropping the entry
    // is the rollback.
    private sealed class ChangeTrackingRow(PendingRowChange change) : UndoEntry
    {
        public readonly PendingRowChange Change = change;

        public override void Undo(LobReclamation? reclamation)
        {
        }
    }

    private sealed class ChangeTrackingTruncation(TableChangeTracking tracking, Database database) : UndoEntry
    {
        public readonly TableChangeTracking Tracking = tracking;

        public readonly Database Database = database;

        public override void Undo(LobReclamation? reclamation)
        {
        }
    }

    private sealed class SchemaChange(Simulation simulation, Action undo) : UndoEntry
    {
        public readonly Simulation Simulation = simulation;
        public readonly Action Reverse = undo;

        public override void Undo(LobReclamation? reclamation)
        {
            this.Reverse();
            this.Simulation.BumpSchemaVersion();
        }
    }

    private sealed class TempTableRemoval(ConcurrentDictionary<string, HeapTable> owner, string name, HeapTable table) : UndoEntry
    {
        public readonly ConcurrentDictionary<string, HeapTable> Owner = owner;
        public readonly string Name = name;
        public readonly HeapTable Table = table;

        public override void Undo(LobReclamation? reclamation) => this.Owner[this.Name] = this.Table;
    }

    /// <summary>
    /// Records a <c>TRUNCATE TABLE</c> against <paramref name="heap"/>. The
    /// snapshots are the pre-truncate <see cref="Heap.Pages"/> /
    /// <see cref="Heap.LobPages"/> list contents and each identity column's
    /// pre-truncate high-water mark. Undo splices the snapshot lists back
    /// into the live heap and restores each identity state — probe-
    /// confirmed against SQL Server 2025 that a rollback after TRUNCATE
    /// restores both the row data AND the identity counter (distinct from
    /// the simulator's general "identity bypasses the log" rule, which
    /// applies to INSERT only).
    /// </summary>
    private sealed class HeapTruncation(Heap heap, List<HeapPage> oldPages, List<HeapLobPage> oldLobPages, HashSet<(int Page, int Slot)> oldForwardTargets, int[] oldFreeLobPages, (IdentityState State, Int128? HighWaterMark)[] identitySnapshots) : UndoEntry
    {
        public override Heap? AffectedHeap => heap;

        public override void Undo(LobReclamation? reclamation)
        {
            using var latch = heap.EnterLatch();
            heap.Pages.Replace(oldPages);
            _ = heap.RecomputeRowCount();
            heap.LobPages.Clear();
            heap.LobPages.AddRange(oldLobPages);
            heap.RestoreForwardTargets(oldForwardTargets);
            // The restored LobPages are indexed by their original positions, so
            // the pre-truncate free-list indices are valid again.
            heap.RestoreFreeLobPages(oldFreeLobPages);
            // The restored pages carry their slots' reclaimable bits, so rescan
            // to reconstruct the candidate set.
            heap.RebuildReclaimablePages();
            for (var i = 0; i < identitySnapshots.Length; i++)
                identitySnapshots[i].State.Restore(identitySnapshots[i].HighWaterMark);
        }
    }
}
