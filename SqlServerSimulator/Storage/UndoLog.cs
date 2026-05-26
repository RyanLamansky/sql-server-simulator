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
internal sealed class UndoLog
{
    private readonly List<UndoEntry> entries = [];

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

    public void RecordTempTableCreation(ConcurrentDictionary<string, HeapTable> owner, string name) =>
        this.entries.Add(new TempTableCreation(owner, name));

    public void RecordTempTableRemoval(ConcurrentDictionary<string, HeapTable> owner, string name, HeapTable table) =>
        this.entries.Add(new TempTableRemoval(owner, name, table));

    public void RecordTruncation(Heap heap, List<HeapPage> oldPages, List<HeapLobPage> oldLobPages, HashSet<(int Page, int Slot)> oldForwardTargets, int[] oldFreeLobPages, (IdentityState State, long? HighWaterMark)[] identitySnapshots) =>
        this.entries.Add(new HeapTruncation(heap, oldPages, oldLobPages, oldForwardTargets, oldFreeLobPages, identitySnapshots));

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
    /// <see cref="Rollback"/>).
    /// </summary>
    public void RollbackTo(int position)
    {
        for (var i = this.entries.Count - 1; i >= position; i--)
            this.entries[i].Undo();
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
        for (var i = 0; i < this.entries.Count; i++)
            this.entries[i].Commit();
        this.entries.Clear();
    }

    /// <summary>
    /// Reads the row at <paramref name="page"/>/<paramref name="slot"/>
    /// (dereferencing one level of forwarding) and returns its off-row LOB
    /// chains to the heap's free-list. No-op when the heap carries no
    /// reclaim layout (no off-row-capable column, or a bare scratch heap).
    /// </summary>
    private static void FreeChainsAtSlot(Heap heap, int page, int slot)
    {
        if (heap.ReclaimColumns is null)
            return;
        var bytes = heap.ReadSlotBytes(page, slot);
        if (bytes is not null)
            FreeChainsInBytes(heap, bytes);
    }

    /// <summary>Frees the off-row LOB chains referenced by an already-materialized row image.</summary>
    private static void FreeChainsInBytes(Heap heap, ReadOnlySpan<byte> rowBytes)
    {
        if (heap.ReclaimColumns is not { } columns)
            return;
        var heads = new List<int>(1);
        RowDecoder.CollectLobHeads(columns, rowBytes, heads);
        for (var i = 0; i < heads.Count; i++)
            heap.FreeLobChain(heads[i]);
    }

    private abstract class UndoEntry
    {
        public abstract void Undo();

        /// <summary>
        /// Runs when the enclosing transaction commits. Default no-op; the
        /// slot-mutation entries override it to reclaim superseded LOB chains.
        /// </summary>
        public virtual void Commit()
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

        public override void Undo()
        {
            var page = this.Heap.Pages[this.PageIndex];
            switch (this.Kind)
            {
                case UndoKind.Insert:
                    // The row this INSERT created is being unwound; its off-row
                    // chains were allocated by the rolled-back statement and no
                    // surviving row, undo entry, or version references them.
                    FreeChainsAtSlot(this.Heap, this.PageIndex, this.SlotIndex);
                    page.DeleteSlot(this.SlotIndex);
                    break;
                case UndoKind.Delete:
                    // Un-tombstone: the row (and its chains) become live again,
                    // so nothing is freed here.
                    page.UndeleteSlot(this.SlotIndex);
                    break;
            }
        }

        public override void Commit()
        {
            if (this.Kind != UndoKind.Delete)
                return;
            // A committed DELETE's row is permanently gone. Reclaim its off-row
            // LOB chains when no version owns them (freeOnCommit = versioning was
            // off), and mark its heap-page slot reclaimable so compaction can
            // pack away the row-payload bytes and reuse the space — independent
            // of versioning, since snapshot history reads a version-store copy,
            // not the live (now tombstoned) slot.
            if (this.FreeOnCommit)
                FreeChainsAtSlot(this.Heap, this.PageIndex, this.SlotIndex);
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

        public override void Undo()
        {
            var page = this.Heap.Pages[this.PageIndex];
            switch (this.Kind)
            {
                case SlotRewriteKind.InPlaceRewrite:
                    // At this point the slot holds the (rolled-back) new payload;
                    // free its chains before overwriting with the old image,
                    // whose chains were never freed (Commit didn't run).
                    FreeChainsAtSlot(this.Heap, this.PageIndex, this.SlotIndex);
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

        public override void Commit()
        {
            // InPlaceRewrite / ForwardInstall both supersede the original
            // row, whose pre-update image is OldPayload — reclaim its off-row
            // chains when no version owns them. ForwardRetarget carries no
            // superseded payload of its own (its old target rides a Delete).
            if (this.FreeOnCommit && this.Kind != SlotRewriteKind.ForwardRetarget)
                FreeChainsInBytes(this.Heap, this.OldPayload);
        }
    }

    private sealed class TempTableCreation(ConcurrentDictionary<string, HeapTable> owner, string name) : UndoEntry
    {
        public readonly ConcurrentDictionary<string, HeapTable> Owner = owner;
        public readonly string Name = name;

        public override void Undo() => this.Owner.TryRemove(this.Name, out _);
    }

    private sealed class TempTableRemoval(ConcurrentDictionary<string, HeapTable> owner, string name, HeapTable table) : UndoEntry
    {
        public readonly ConcurrentDictionary<string, HeapTable> Owner = owner;
        public readonly string Name = name;
        public readonly HeapTable Table = table;

        public override void Undo() => this.Owner[this.Name] = this.Table;
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
    private sealed class HeapTruncation(Heap heap, List<HeapPage> oldPages, List<HeapLobPage> oldLobPages, HashSet<(int Page, int Slot)> oldForwardTargets, int[] oldFreeLobPages, (IdentityState State, long? HighWaterMark)[] identitySnapshots) : UndoEntry
    {
        public override void Undo()
        {
            heap.Pages.Clear();
            heap.Pages.AddRange(oldPages);
            heap.LobPages.Clear();
            heap.LobPages.AddRange(oldLobPages);
            heap.ForwardTargets.Clear();
            heap.ForwardTargets.UnionWith(oldForwardTargets);
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
