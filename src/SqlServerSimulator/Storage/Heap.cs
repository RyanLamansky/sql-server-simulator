using System.Buffers;
using System.Collections.Concurrent;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Callback that consumes the bytes of a LOB chain materialized into a
/// caller-supplied scratch buffer. The <c>state</c> parameter lets callers
/// pass per-call context (e.g. the destination <see cref="SqlType"/>) into
/// a static lambda, avoiding closure allocations on the hot decode path.
/// The span is only valid for the duration of the call — implementations
/// must not store it.
/// </summary>
internal delegate T LobChainReader<TState, T>(ReadOnlySpan<byte> bytes, TState state);

/// <summary>
/// A multi-page heap: an ordered list of <see cref="HeapPage"/>s linked
/// prev/next, into which rows are appended. Real SQL Server tracks page
/// allocations through PFS/GAM/SGAM/IAM pages and a heap object's first-page
/// pointer; we model just the linked list of data pages directly today, which
/// is enough to drive the encoder/decoder through real page-bounded storage
/// while leaving room for IAM/PFS modeling later.
/// </summary>
internal sealed class Heap
{
    /// <summary>
    /// SQL Server's documented in-row record size limit. The encoder pushes
    /// variable-length columns off-row through <see cref="AllocateLobChain"/>
    /// to keep rows under this cap; only when no overflowable column can
    /// help (e.g. the fixed-length section alone exceeds the limit) does
    /// insertion fail.
    /// </summary>
    /// <remarks>
    /// The page's physical capacity (<see cref="HeapPage.MaxRowPayload"/>) is
    /// slightly larger; the gap accounts for SQL Server's per-record overhead
    /// the simulator doesn't byte-for-byte reproduce.
    /// </remarks>
    public const int MaxRowSize = 8060;

    /// <summary>Pages in this heap, in allocation order. Index <c>i</c> is reachable via prev/next links.</summary>
    public readonly List<HeapPage> Pages = [];

    /// <summary>
    /// Slots that are the target of some forwarding pointer. Iteration over
    /// the whole heap skips these (the row will be yielded via the
    /// forwarding slot at the row's stable address); single-slot reads through
    /// <see cref="ReadSlotBytes"/> still resolve them directly so forward-chasing
    /// callers can address them by their physical location. <c>TRUNCATE</c>
    /// clears this alongside <see cref="Pages"/> and <see cref="LobPages"/>;
    /// <see cref="UndoLog"/>'s truncation entry snapshots and restores it.
    /// </summary>
    internal readonly HashSet<(int Page, int Slot)> ForwardTargets = [];

    /// <summary>
    /// Monotonic counter bumped by every <see cref="Insert"/>,
    /// <see cref="DeleteAt"/>, and <see cref="UpdateAt"/>; the forwarding
    /// UPDATE path may bump multiple times (its internal Insert + Delete each
    /// contribute) and that's fine — read-side equality-seek caches (see
    /// <c>Selection.Execution.IndexSeek.cs</c>) only check whether anything
    /// changed, so any-positive delta forces a rebuild or — once the seek
    /// journal is active (see <see cref="seekJournal"/>) — a delta replay.
    /// Not a transactional value: it advances on the physical mutation and
    /// never rolls back. Mutations on a given table are lock-serialized, so
    /// this needs no interlocking.
    /// </summary>
    public long MutationGeneration;

    /// <summary>
    /// Visible-row mutation kind recorded in the <see cref="seekJournal"/>.
    /// <see cref="Insert"/> carries the inserted image; <see cref="Delete"/>
    /// the pre-delete image; <see cref="Update"/> both (so a replay computes the
    /// old and new key without re-reading the live slot, which may have moved on).
    /// </summary>
    internal enum SeekJournalKind : byte
    {
        Insert,
        Delete,
        Update,
    }

    /// <summary>
    /// One visible-row mutation, tagged with the <see cref="MutationGeneration"/>
    /// it produced. Addresses are the row's stable visible Rid — a forwarding
    /// UPDATE's internal target Insert / old-target Delete are deliberately NOT
    /// journaled (they carry <c>journalEvent: false</c>); only the visible slot's
    /// before/after images are. <see cref="OldImage"/> is null for an Insert;
    /// <see cref="NewImage"/> is null for a Delete.
    /// </summary>
    internal readonly struct SeekJournalEvent(long generation, SeekJournalKind kind, int page, int slot, byte[]? oldImage, byte[]? newImage)
    {
        public readonly long Generation = generation;
        public readonly SeekJournalKind Kind = kind;
        public readonly int Page = page;
        public readonly int Slot = slot;
        public readonly byte[]? OldImage = oldImage;
        public readonly byte[]? NewImage = newImage;
    }

    private readonly Lock seekJournalGate = new();

    /// <summary>
    /// Bounded log of visible-row mutations since the seek cache went live,
    /// enabling the per-<see cref="Heap"/> equality-seek cache to apply a delta
    /// rather than rebuild from a full scan on every mutation — the "no warm-up"
    /// path. Null until <see cref="ActivateSeekJournal"/> runs on the first seek
    /// against this heap, so a never-queried (write-only) table pays nothing.
    /// Trimmed to <see cref="MaxSeekJournalEvents"/>; older events fall off and
    /// advance <see cref="seekJournalDroppedThroughGen"/>, which forces a full
    /// rebuild for any cache that fell too far behind (a large bulk mutation, or
    /// a heap that wasn't seeked for a long time). A rollback or ALTER clears it
    /// via <see cref="InvalidateSeekJournal"/>.
    /// </summary>
    private Queue<SeekJournalEvent>? seekJournal;

    /// <summary>
    /// Highest <see cref="MutationGeneration"/> whose journal event has been
    /// dropped (trimmed or invalidated). A cache whose last-seen generation is
    /// below this can't replay the delta — it's missing dropped events — so it
    /// rebuilds from a scan.
    /// </summary>
    private long seekJournalDroppedThroughGen;

    /// <summary>
    /// True once the first seek activated journaling. Read on the hot write path
    /// to decide whether to capture before/after images; <c>volatile</c> so the
    /// activation by a reader thread is visible to writer threads. When false,
    /// <see cref="Insert"/> / <see cref="DeleteAt"/> / <see cref="UpdateAt"/>
    /// skip all journal work.
    /// </summary>
    private volatile bool seekJournalActive;

    private const int MaxSeekJournalEvents = 512;

    /// <summary>
    /// Turns on the seek journal (idempotent) and returns the current
    /// <see cref="MutationGeneration"/> for the caller to stamp on the cache
    /// entry it's about to build. Activation happens-before the returned
    /// generation, so any mutation that lands after this call is journaled at a
    /// later generation and replayed into the cache on a later seek — the cache
    /// never silently misses a write. Called by the seek cache the first time it
    /// builds an entry for this heap.
    /// </summary>
    internal long ActivateSeekJournal()
    {
        lock (this.seekJournalGate)
        {
            this.seekJournal ??= new Queue<SeekJournalEvent>();
            this.seekJournalActive = true;
            return this.MutationGeneration;
        }
    }

    /// <summary>
    /// Returns the journal events with <see cref="SeekJournalEvent.Generation"/>
    /// greater than <paramref name="sinceGen"/> (in mutation order), or null when
    /// the cache can't safely replay — either journaling isn't active or
    /// <paramref name="sinceGen"/> predates a dropped event. A null result tells
    /// the caller to rebuild from a full scan. <paramref name="currentGen"/> is
    /// the generation the events bring the cache up to.
    /// </summary>
    internal SeekJournalEvent[]? SnapshotSeekJournalSince(long sinceGen, out long currentGen)
    {
        lock (this.seekJournalGate)
        {
            currentGen = this.MutationGeneration;
            if (this.seekJournal is null || sinceGen < this.seekJournalDroppedThroughGen)
                return null;
            var result = new List<SeekJournalEvent>();
            foreach (var e in this.seekJournal)
            {
                if (e.Generation > sinceGen)
                    result.Add(e);
            }
            return [.. result];
        }
    }

    /// <summary>
    /// Drops the entire journal and advances <see cref="seekJournalDroppedThroughGen"/>
    /// to the current generation, so every existing cache rebuilds on its next
    /// seek. Called when a rollback rewinds heap state without producing
    /// reversing journal events (<see cref="UndoLog"/> mutates pages directly),
    /// and by ALTER paths that rewrite the heap's columns underneath the cache.
    /// Journaling stays active — the rebuild re-bases the cache cleanly.
    /// </summary>
    internal void InvalidateSeekJournal()
    {
        if (!this.seekJournalActive)
            return;
        lock (this.seekJournalGate)
        {
            this.MutationGeneration++;
            this.seekJournalDroppedThroughGen = this.MutationGeneration;
            this.seekJournal?.Clear();
        }
    }

    private void RecordSeekJournalEvent(SeekJournalKind kind, int page, int slot, byte[]? oldImage, byte[]? newImage)
    {
        lock (this.seekJournalGate)
        {
            if (this.seekJournal is not { } journal)
                return;
            journal.Enqueue(new SeekJournalEvent(this.MutationGeneration, kind, page, slot, oldImage, newImage));
            while (journal.Count > MaxSeekJournalEvents)
                this.seekJournalDroppedThroughGen = Math.Max(this.seekJournalDroppedThroughGen, journal.Dequeue().Generation);
        }
    }

    /// <summary>
    /// Appends a row's encoded bytes to the heap. The active (last) page is
    /// tried first; on no-fit, a new page is allocated and linked, and the
    /// row goes there. Callers are responsible for sizing the row to
    /// <see cref="MaxRowSize"/> — the row encoder pushes variable-length
    /// columns off-row to honor that cap; this method only enforces it as
    /// a defensive guard against bypassed callers.
    /// </summary>
    public (int PageIndex, int SlotIndex) Insert(ReadOnlySpan<byte> row, UndoLog? undoLog = null) =>
        this.InsertCore(row, undoLog, journalEvent: true);

    // journalEvent is false for the forwarding-UPDATE path's internal target
    // insert — that target is a relocated payload, not a new visible row, so it
    // must not produce a seek-journal Insert; the visible slot's key change rides
    // the Update event UpdateAt records instead.
    private (int PageIndex, int SlotIndex) InsertCore(ReadOnlySpan<byte> row, UndoLog? undoLog, bool journalEvent)
    {
        if (row.Length > MaxRowSize)
            throw new NotSupportedException($"Row of {row.Length} bytes exceeds SQL Server's per-row maximum of {MaxRowSize}; the encoder should have pushed variable-length columns off-row.");

        int pageIndex;
        if (this.Pages.Count > 0 && this.Pages[^1].TryInsert(row))
        {
            pageIndex = this.Pages.Count - 1;
        }
        else if (TryReuseReclaimablePage(row, out pageIndex))
        {
            // Inserted into a page whose committed-dead space was reused
            // (compacted if needed) — bounds Pages.Count by the working set.
        }
        else
        {
            var newPage = new HeapPage();
            if (this.Pages.Count > 0)
            {
                var prevIndex = this.Pages.Count - 1;
                this.Pages[prevIndex].NextPageIndex = prevIndex + 1;
                newPage.PrevPageIndex = prevIndex;
            }
            this.Pages.Add(newPage);
            if (!newPage.TryInsert(row))
                throw new InvalidOperationException($"Row of {row.Length} bytes failed to insert into a fresh page; this should be impossible because the size was validated.");
            pageIndex = this.Pages.Count - 1;
        }

        // The new row went into the slot at SlotCount-1 of the chosen page —
        // TryInsert appends a new directory entry as the highest-index slot.
        var slotIndex = this.Pages[pageIndex].SlotCount - 1;
        this.MutationGeneration++;
        undoLog?.RecordInsert(this, pageIndex, slotIndex);
        if (journalEvent && this.seekJournalActive)
            this.RecordSeekJournalEvent(SeekJournalKind.Insert, pageIndex, slotIndex, oldImage: null, newImage: row.ToArray());
        return (pageIndex, slotIndex);
    }

    /// <summary>
    /// Page indices known to hold committed-dead (reclaimable) row space —
    /// populated by <see cref="MarkPageReclaimable"/> when a DELETE / forwarding
    /// UPDATE commits. The Insert no-fit path draws from this set (compacting a
    /// page to consolidate the dead space) before appending a fresh page, which
    /// is what bounds <see cref="Pages"/>.Count by the working set instead of the
    /// churn count. A concurrent set because commit (which marks) and Insert
    /// (which drains) on a table are lock-serialized but reached from different
    /// call paths; weakly-consistent iteration is fine — a missed candidate just
    /// defers reuse to the next insert.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> reclaimablePages = new();

    /// <summary>
    /// Records that page <paramref name="pageIndex"/> holds reclaimable space.
    /// Called by the undo log when a DELETE (or forwarding-UPDATE supersede)
    /// commits the tombstone on a slot there.
    /// </summary>
    internal void MarkPageReclaimable(int pageIndex) => this.reclaimablePages[pageIndex] = 0;

    /// <summary>
    /// Tries to place <paramref name="row"/> into an existing page's reclaimable
    /// space, compacting that page if the room is fragmented behind dead slots.
    /// Returns false (and the caller appends a new page) when no candidate can
    /// hold the row. New rows always take a fresh, higher slot index, so reuse
    /// never aliases a <c>(page, slot)</c> any holder still references.
    /// </summary>
    private bool TryReuseReclaimablePage(ReadOnlySpan<byte> row, out int pageIndex)
    {
        var need = row.Length + 2;
        foreach (var candidate in this.reclaimablePages.Keys)
        {
            if (candidate < 0 || candidate >= this.Pages.Count)
            {
                _ = this.reclaimablePages.TryRemove(candidate, out _);
                continue;
            }
            var page = this.Pages[candidate];
            if (page.FreeSpace >= need)
            {
                // Trailing room already; no compaction needed.
                _ = page.TryInsert(row);
                if (page.ReclaimableBytes == 0)
                    _ = this.reclaimablePages.TryRemove(candidate, out _);
                pageIndex = candidate;
                return true;
            }
            if (page.FreeSpace + page.ReclaimableBytes >= need)
            {
                page.Compact();
                _ = page.TryInsert(row);
                // Compaction consumed all reclaimable space on this page.
                _ = this.reclaimablePages.TryRemove(candidate, out _);
                pageIndex = candidate;
                return true;
            }
            // Can't fit even compacted — leave it a candidate for a smaller row.
        }
        pageIndex = -1;
        return false;
    }

    /// <summary>Drops all reclaimable-page candidates — paired with clearing <see cref="Pages"/> on <c>TRUNCATE</c>.</summary>
    internal void ClearReclaimablePages() => this.reclaimablePages.Clear();

    /// <summary>
    /// Rebuilds the reclaimable-page candidate set by scanning every page for
    /// committed-dead slots. Used by <c>TRUNCATE</c>'s undo entry after it
    /// restores the pre-truncate <see cref="Pages"/> (the reclaimable bits ride
    /// the restored slot directories, so a scan reconstructs the set exactly).
    /// </summary>
    internal void RebuildReclaimablePages()
    {
        this.reclaimablePages.Clear();
        for (var p = 0; p < this.Pages.Count; p++)
        {
            if (this.Pages[p].ReclaimableBytes > 0)
                this.reclaimablePages[p] = 0;
        }
    }

    /// <summary>
    /// Drops fully-dead pages from the tail of <see cref="Pages"/>, lowering the
    /// list below its high-water mark — the page-data half of a
    /// <c>DBCC SHRINKDATABASE</c>. A trailing page is removed only when it holds
    /// no reachable row (<see cref="HeapPage.IsFullyDead"/>) and
    /// <paramref name="pageIsPinned"/> reports no historical-version entry or
    /// held lock keyed on it; either keeps a <c>(page, slot)</c> address live.
    /// Removal stops at the first page that fails — only the trailing run goes,
    /// so surviving pages keep their indices and no cursor / version Rid /
    /// forward pointer is invalidated. Returns the number of pages dropped.
    /// </summary>
    internal int TrimTrailingDeadPages(Func<int, bool> pageIsPinned)
    {
        var removed = 0;
        while (this.Pages.Count > 0)
        {
            var last = this.Pages.Count - 1;
            if (!this.Pages[last].IsFullyDead || pageIsPinned(last))
                break;
            this.Pages.RemoveAt(last);
            _ = this.reclaimablePages.TryRemove(last, out _);
            if (this.Pages.Count > 0)
                this.Pages[^1].NextPageIndex = -1;
            removed++;
        }
        if (removed > 0)
            this.MutationGeneration++;
        return removed;
    }

    /// <summary>
    /// Drops reclaimed pages from the tail of <see cref="LobPages"/> — the
    /// off-row half of a <c>DBCC SHRINKDATABASE</c>. A trailing page is removable
    /// exactly when its index sits on <see cref="freeLobPages"/>: free-list
    /// membership is the reclamation contract that no live row, surviving
    /// <c>NextPageIndex</c> link, or historical version still references it.
    /// Only the trailing run of free pages is removed, so surviving indices —
    /// which back live chain links and row head-indices — stay valid. Returns
    /// the number of pages dropped.
    /// </summary>
    internal int TrimTrailingFreeLobPages()
    {
        var free = new HashSet<int>(this.freeLobPages);
        var removed = 0;
        while (this.LobPages.Count > 0 && free.Remove(this.LobPages.Count - 1))
        {
            this.LobPages.RemoveAt(this.LobPages.Count - 1);
            removed++;
        }
        if (removed > 0)
        {
            // Rebuild the free-list (and its debug mirror) from the survivors —
            // the dropped indices no longer exist to be reused.
            this.freeLobPages.Clear();
            if (free.Count > 0)
                this.freeLobPages.PushRange([.. free]);
#if DEBUG
            lock (this.debugFreedLobPages)
            {
                this.debugFreedLobPages.Clear();
                foreach (var idx in free)
                    _ = this.debugFreedLobPages.Add(idx);
            }
#endif
        }
        return removed;
    }

    /// <summary>
    /// Yields every live row in the heap, dereferencing forward pointers so
    /// each row appears exactly once at its stable address. Tombstoned slots
    /// and slots that are the target of a forwarding pointer (still physically
    /// present, surfaced via the forwarder) are skipped.
    /// </summary>
    public IEnumerable<byte[]> EnumerateRows()
    {
        foreach (var (_, _, bytes) in this.EnumerateRowsWithAddress())
            yield return bytes;
    }

    /// <summary>
    /// Like <see cref="EnumerateRows"/> but yields a stable address for each
    /// row alongside its resolved bytes — UPDATE and DELETE need this to call
    /// <see cref="UpdateAt"/> / <see cref="DeleteAt"/> through the visible
    /// row identity, which survives a forwarding UPDATE.
    /// </summary>
    public IEnumerable<(int PageIndex, int SlotIndex, byte[] Bytes)> EnumerateRowsWithAddress()
    {
        for (var p = 0; p < this.Pages.Count; p++)
        {
            var page = this.Pages[p];
            foreach (var (slotIndex, raw) in page.EnumerateRowsWithSlots())
            {
                if (this.ForwardTargets.Contains((p, slotIndex)))
                    continue;
                if (page.IsSlotForwarded(slotIndex))
                {
                    var (tp, ts) = page.ReadForwardTarget(slotIndex);
                    yield return (p, slotIndex, this.Pages[tp].ReadSlotBytes(ts)!);
                }
                else
                {
                    yield return (p, slotIndex, raw);
                }
            }
        }
    }

    /// <summary>
    /// Marks the row at <paramref name="pageIndex"/> / <paramref name="slotIndex"/>
    /// as deleted. The slot is tombstoned at the page level; on commit its undo
    /// entry marks the slot reclaimable so <see cref="HeapPage.Compact"/> can
    /// pack the bytes away. The row's off-row LOB chains are reclaimed too: when
    /// <paramref name="reclaimSuperseded"/> is set (no <c>HistoricalVersion</c>
    /// will pin them — see <see cref="VersionStore.WillCaptureVersions"/>) the
    /// recorded undo entry frees them on commit via
    /// <see cref="FreeLobChain"/>; otherwise version-store GC frees them once
    /// no snapshot needs the deleted row.
    /// </summary>
    /// <remarks>
    /// When the visible slot is a forwarding pointer, the row's payload (and any
    /// off-row chains) lives at the relocated target, not the pointer slot — so
    /// both are deleted, and the target is unregistered from
    /// <see cref="ForwardTargets"/>. The target's Delete entry carries the
    /// <paramref name="reclaimSuperseded"/> gate (it owns the row + chains); the
    /// pointer's entry only reclaims its directory slot — its bytes are a
    /// forward pointer, not a row, so they must never be decoded for LOB heads.
    /// Rollback resurrects both slots and re-registers the target.
    /// </remarks>
    public void DeleteAt(int pageIndex, int slotIndex, UndoLog? undoLog = null, bool reclaimSuperseded = false) =>
        this.DeleteAtCore(pageIndex, slotIndex, undoLog, reclaimSuperseded, journalEvent: true);

    // journalEvent is false for the forwarding-UPDATE path's internal old-target
    // delete — the old target is a superseded relocated payload, not the removal
    // of a visible row, so it must not produce a seek-journal Delete.
    private void DeleteAtCore(int pageIndex, int slotIndex, UndoLog? undoLog, bool reclaimSuperseded, bool journalEvent)
    {
        var oldImage = journalEvent && this.seekJournalActive ? this.ReadSlotBytes(pageIndex, slotIndex) : null;
        this.MutationGeneration++;
        var page = this.Pages[pageIndex];
        if (page.IsSlotForwarded(slotIndex))
        {
            var target = page.ReadForwardTarget(slotIndex);
            undoLog?.RecordDelete(this, target.PageIndex, target.SlotIndex, reclaimSuperseded);
            this.Pages[target.PageIndex].DeleteSlot(target.SlotIndex);
            undoLog?.RecordForwardedPointerDelete(this, pageIndex, slotIndex, target);
            page.DeleteSlot(slotIndex);
            _ = this.ForwardTargets.Remove(target);
            if (oldImage is not null)
                this.RecordSeekJournalEvent(SeekJournalKind.Delete, pageIndex, slotIndex, oldImage, newImage: null);
            return;
        }
        undoLog?.RecordDelete(this, pageIndex, slotIndex, reclaimSuperseded);
        page.DeleteSlot(slotIndex);
        if (oldImage is not null)
            this.RecordSeekJournalEvent(SeekJournalKind.Delete, pageIndex, slotIndex, oldImage, newImage: null);
    }

    /// <summary>
    /// Rewrites the row at <paramref name="pageIndex"/> / <paramref name="slotIndex"/>
    /// with <paramref name="newRow"/>, keeping the caller-visible address
    /// stable: if the new payload fits within the slot's existing extent it's
    /// rewritten in place; otherwise the new row is appended elsewhere and the
    /// original slot becomes a single-level forwarding pointer to that target.
    /// When the original slot is already forwarded the same fits-or-forwards
    /// decision applies to the current target — if the new row needs more
    /// room than the target offers, a fresh target is allocated and the
    /// original slot's forward pointer is re-pointed (the now-dead target is
    /// tombstoned); the original slot's forward bit is never cleared by an
    /// UPDATE, so chains never form. Matches SQL Server's heap-update
    /// behavior (probe-confirmed 2026-05-26): same physloc reported through
    /// any number of growth / shrink UPDATEs.
    /// </summary>
    /// <remarks>
    /// Both the in-place and forwarding paths allocate a fresh LOB chain for
    /// the new payload; the superseded old chain is reclaimed (returned to the
    /// free-list) either when the recorded undo entry commits (unversioned,
    /// gated by <paramref name="reclaimSuperseded"/>) or by version-store GC
    /// (versioned). A rolled-back UPDATE frees the new chain and keeps the old.
    /// </remarks>
    public void UpdateAt(int pageIndex, int slotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog = null, bool reclaimSuperseded = false)
    {
        // The visible Rid (pageIndex, slotIndex) is stable across an UPDATE even
        // when the payload relocates (the original slot keeps its forward bit),
        // so the seek journal records one Update at that address. Capture the
        // pre-UPDATE visible image before the mutation; the internal target
        // Insert / old-target Delete the relocating paths run are NOT journaled.
        var oldImage = this.seekJournalActive ? this.ReadSlotBytes(pageIndex, slotIndex) : null;
        var page = this.Pages[pageIndex];
        if (page.IsSlotForwarded(slotIndex))
            this.UpdateForwarded(page, pageIndex, slotIndex, newRow, undoLog, reclaimSuperseded);
        else
            this.UpdateDirect(page, pageIndex, slotIndex, newRow, undoLog, reclaimSuperseded);
        this.MutationGeneration++;
        if (oldImage is not null)
            this.RecordSeekJournalEvent(SeekJournalKind.Update, pageIndex, slotIndex, oldImage, newRow.ToArray());
    }

    private void UpdateDirect(HeapPage page, int pageIndex, int slotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog, bool reclaimSuperseded)
    {
        var oldExtent = page.SlotExtent(slotIndex);
        if (newRow.Length <= oldExtent)
        {
            var oldBytes = page.ReadSlotBytes(slotIndex)!;
            undoLog?.RecordInPlaceRewrite(this, pageIndex, slotIndex, oldBytes, reclaimSuperseded);
            page.RewriteSlotInPlace(slotIndex, newRow);
        }
        else
        {
            var oldBytes = page.ReadSlotBytes(slotIndex)!;
            var target = this.InsertCore(newRow, undoLog, journalEvent: false);
            undoLog?.RecordForwardInstall(this, pageIndex, slotIndex, oldBytes, target, reclaimSuperseded);
            this.Pages[pageIndex].InstallForward(slotIndex, target);
            _ = this.ForwardTargets.Add(target);
        }
    }

    private void UpdateForwarded(HeapPage originalPage, int originalPageIndex, int originalSlotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog, bool reclaimSuperseded)
    {
        var oldTarget = originalPage.ReadForwardTarget(originalSlotIndex);
        var targetPage = this.Pages[oldTarget.PageIndex];
        var targetExtent = targetPage.SlotExtent(oldTarget.SlotIndex);
        if (newRow.Length <= targetExtent)
        {
            // Fits at the existing forward target — rewrite there, forward pointer untouched.
            var oldTargetBytes = targetPage.ReadSlotBytes(oldTarget.SlotIndex)!;
            undoLog?.RecordInPlaceRewrite(this, oldTarget.PageIndex, oldTarget.SlotIndex, oldTargetBytes, reclaimSuperseded);
            targetPage.RewriteSlotInPlace(oldTarget.SlotIndex, newRow);
        }
        else
        {
            // Doesn't fit. Insert at a fresh target, tombstone the old one, and
            // re-point the original slot's forward. Forward bit at the original
            // never clears; the row keeps its visible identity. The old target's
            // superseded chains ride its Delete entry (reclaimSuperseded passed
            // through); the new target's ride its Insert entry.
            var newTarget = this.InsertCore(newRow, undoLog, journalEvent: false);
            this.DeleteAtCore(oldTarget.PageIndex, oldTarget.SlotIndex, undoLog, reclaimSuperseded, journalEvent: false);
            undoLog?.RecordForwardRetarget(this, originalPageIndex, originalSlotIndex, oldTarget, newTarget);
            originalPage.RewriteForward(originalSlotIndex, newTarget);
            _ = this.ForwardTargets.Remove(oldTarget);
            _ = this.ForwardTargets.Add(newTarget);
        }
    }

    /// <summary>
    /// Undo callback for <see cref="UndoLog.RecordForwardInstall"/> — removes
    /// the target from <see cref="ForwardTargets"/> after the page-level
    /// forward bit is cleared. Called as part of the rollback walk; the
    /// target slot itself is tombstoned by the paired <see cref="UndoKind.Insert"/>
    /// entry.
    /// </summary>
    internal void UnregisterForwardTargetForUndo((int Page, int Slot) target)
    {
        _ = this.ForwardTargets.Remove(target);
    }

    /// <summary>
    /// Undo callback for <see cref="UndoLog.RecordForwardRetarget"/> — restores
    /// the forwarding-target tracking to its pre-UPDATE shape (old target back
    /// in, new target removed). The old target slot's tombstone bit is cleared
    /// by the paired <see cref="UndoKind.Delete"/> entry.
    /// </summary>
    internal void SwapForwardTargetForUndo((int Page, int Slot) oldTarget, (int Page, int Slot) newTarget)
    {
        _ = this.ForwardTargets.Remove(newTarget);
        _ = this.ForwardTargets.Add(oldTarget);
    }

    /// <summary>
    /// Undo callback for <see cref="UndoLog.RecordForwardedPointerDelete"/> —
    /// re-registers a target whose forwarding row was deleted, so the
    /// resurrected pointer surfaces the row once (via the forwarder) rather than
    /// the target also appearing as a standalone live row.
    /// </summary>
    internal void ReinstateForwardTargetForUndo((int Page, int Slot) target) =>
        _ = this.ForwardTargets.Add(target);

    /// <summary>
    /// Returns a fresh copy of the row bytes at the given Rid, dereferencing
    /// one level of forwarding so callers see the live row's payload even
    /// when the slot is a forwarding pointer. Reads through tombstoned slots
    /// (the bytes are still resident pre-finalization). Used by the version
    /// store to snapshot the pre-mutation payload before
    /// <see cref="DeleteAt"/> / <see cref="UpdateAt"/>, and by the index-seek
    /// materializer to read seeked candidates through their stable address.
    /// </summary>
    public byte[]? ReadSlotBytes(int pageIndex, int slotIndex)
    {
        if (pageIndex < 0 || pageIndex >= this.Pages.Count)
            return null;
        var page = this.Pages[pageIndex];
        if (page.IsSlotForwarded(slotIndex))
        {
            var (tp, ts) = page.ReadForwardTarget(slotIndex);
            return this.Pages[tp].ReadSlotBytes(ts);
        }
        return page.ReadSlotBytes(slotIndex);
    }

    /// <summary>
    /// Returns true when the slot at the given Rid is past the page's high-
    /// water mark or has been tombstoned. Snapshot-aware iteration uses this
    /// to identify chain entries whose live slot is no longer in the heap's
    /// live row stream and surface a historical version instead.
    /// </summary>
    public bool IsSlotTombstoned(int pageIndex, int slotIndex) =>
        pageIndex < 0 || pageIndex >= this.Pages.Count || this.Pages[pageIndex].IsSlotTombstoned(slotIndex);

    /// <summary>Total row count across all pages.</summary>
    public int RowCount
    {
        get
        {
            var count = 0;
            foreach (var page in this.Pages)
                count += page.SlotCount;
            return count;
        }
    }

    /// <summary>
    /// LOB-chain pages. Each <c>varchar(MAX)</c>/<c>nvarchar(MAX)</c>/
    /// <c>varbinary(MAX)</c>/<c>text</c>/<c>ntext</c>/<c>image</c> value that
    /// the row encoder pushed off-row owns its own forward-linked sub-chain
    /// of pages here; pages from different chains are interleaved in
    /// allocation order (one chain doesn't reserve a contiguous run).
    /// </summary>
    public readonly List<HeapLobPage> LobPages = [];

    /// <summary>
    /// Indices into <see cref="LobPages"/> whose chains have been reclaimed
    /// (a row that referenced them was superseded by a committed UPDATE /
    /// DELETE, or an INSERT that allocated them rolled back) and may be
    /// reused by a later <see cref="AllocateLobChain"/>. Reuse keeps the
    /// <see cref="LobPages"/> list bounded by the high-water set of
    /// concurrently-live (+ version-pinned) chains rather than total mutation
    /// count — the heap's analog of SQL Server's ghost-record / page
    /// deallocation. Concurrent because version-store GC frees chains without
    /// holding the table's locks (see <c>VersionStore.RunGarbageCollection</c>);
    /// per-index pop/push are individually atomic, which is all the linking
    /// needs (a freed index is never simultaneously live).
    /// </summary>
    private readonly ConcurrentStack<int> freeLobPages = new();

#if DEBUG
    /// <summary>
    /// Debug-only double-free guard: the set of indices held by
    /// <see cref="freeLobPages"/>. Reclamation routes through two disjoint
    /// owners (commit-time undo entries for the unversioned path, version-GC
    /// for the versioned path), so a chain should be freed exactly once;
    /// freeing an already-free index would mean those owners overlapped and
    /// risks handing the same page to two live rows. Locked rather than
    /// concurrent because GC and a committing writer can free in parallel.
    /// </summary>
    private readonly HashSet<int> debugFreedLobPages = [];
#endif

    /// <summary>
    /// The owning table's stored-column layout (set by <see cref="HeapTable"/>
    /// whenever it (re)computes <c>StoredColumns</c>). The undo-log free hooks
    /// and version-store GC use it with <see cref="RowDecoder.CollectLobHeads"/>
    /// to locate a superseded row's off-row chain heads. Null on bare heaps
    /// (ALTER-rebuild scratch, procedure-param clones, tests); those skip
    /// reclamation — they either never carry an undo log or are discarded
    /// wholesale.
    /// </summary>
    internal HeapColumn[]? ReclaimColumns;

    /// <summary>
    /// Splits <paramref name="data"/> into <see cref="HeapLobPage.MaxPayload"/>-sized
    /// chunks, allocates a page chain in <see cref="LobPages"/>, and returns
    /// the index of the chain's head page. Reuses pages from
    /// <see cref="freeLobPages"/> before appending. Empty inputs allocate a
    /// single zero-payload page so the row's pointer is always valid; callers
    /// that want NULL semantics should not call this method at all.
    /// </summary>
    public int AllocateLobChain(ReadOnlySpan<byte> data)
    {
        var head = AllocateLobPage();
        var page = this.LobPages[head];
        var remaining = data;
        while (true)
        {
            var chunkSize = Math.Min(HeapLobPage.MaxPayload, remaining.Length);
            // WritePayload resets the page's length and clears NextPageIndex,
            // so a recycled page starts clean; the tail terminates at -1.
            page.WritePayload(remaining[..chunkSize]);
            remaining = remaining[chunkSize..];
            if (remaining.Length == 0)
                return head;
            var nextIdx = AllocateLobPage();
            page.NextPageIndex = nextIdx;
            page = this.LobPages[nextIdx];
        }
    }

    /// <summary>
    /// Returns a free <see cref="LobPages"/> index — popped from
    /// <see cref="freeLobPages"/> when available, otherwise a freshly appended
    /// page. The returned page's contents are overwritten by the caller via
    /// <see cref="HeapLobPage.WritePayload"/>.
    /// </summary>
    private int AllocateLobPage()
    {
        if (this.freeLobPages.TryPop(out var recycled))
        {
#if DEBUG
            lock (this.debugFreedLobPages)
                _ = this.debugFreedLobPages.Remove(recycled);
#endif
            return recycled;
        }
        this.LobPages.Add(new HeapLobPage());
        return this.LobPages.Count - 1;
    }

    /// <summary>
    /// Returns every page of the chain rooted at <paramref name="headIndex"/>
    /// to <see cref="freeLobPages"/> for reuse. The pages stay physically in
    /// <see cref="LobPages"/> (their stable indices back the free-list and any
    /// surviving <c>NextPageIndex</c> links elsewhere remain valid); they're
    /// reset to empty so stale payload isn't read if a bug ever revisits a
    /// freed index. Caller must guarantee no live row, undo entry, or
    /// historical version still references the chain — see the reclamation
    /// ownership rules in the undo-log free hooks and
    /// <c>VersionStore.RunGarbageCollection</c>.
    /// </summary>
    public void FreeLobChain(int headIndex)
    {
        var idx = headIndex;
        while (idx >= 0 && idx < this.LobPages.Count)
        {
            var page = this.LobPages[idx];
            var next = page.NextPageIndex;
            page.PayloadLength = 0;
            page.NextPageIndex = -1;
#if DEBUG
            lock (this.debugFreedLobPages)
            {
                if (!this.debugFreedLobPages.Add(idx))
                    throw new InvalidOperationException($"LOB page {idx} double-freed; reclamation owners overlapped.");
            }
#endif
            this.freeLobPages.Push(idx);
            idx = next;
        }
    }

    /// <summary>
    /// Snapshots the current free-list (used by <c>TRUNCATE</c>'s undo entry,
    /// which must restore both <see cref="LobPages"/> and the indices that
    /// were reusable before the truncate).
    /// </summary>
    internal int[] SnapshotFreeLobPages() => [.. this.freeLobPages];

    /// <summary>Clears the free-list — paired with clearing <see cref="LobPages"/> on <c>TRUNCATE</c>.</summary>
    internal void ClearFreeLobPages()
    {
        this.freeLobPages.Clear();
#if DEBUG
        lock (this.debugFreedLobPages)
            this.debugFreedLobPages.Clear();
#endif
    }

    /// <summary>Replaces the free-list contents with <paramref name="indices"/> (TRUNCATE rollback).</summary>
    internal void RestoreFreeLobPages(int[] indices)
    {
        this.freeLobPages.Clear();
        // ConcurrentStack.PushRange preserves order such that ToArray() round-trips.
        if (indices.Length > 0)
            this.freeLobPages.PushRange(indices);
#if DEBUG
        lock (this.debugFreedLobPages)
        {
            this.debugFreedLobPages.Clear();
            foreach (var i in indices)
                _ = this.debugFreedLobPages.Add(i);
        }
#endif
    }

    /// <summary>
    /// Walks the LOB chain starting at <paramref name="headIndex"/> into a
    /// scratch buffer (stack-allocated for small payloads, pooled for
    /// larger ones) and hands the bytes to <paramref name="reader"/>. The
    /// callback's return value is the method's result; the buffer is
    /// released as soon as the callback completes, so the span must not
    /// escape.
    /// </summary>
    public T ReadLobChain<TState, T>(int headIndex, int totalLength, TState state, LobChainReader<TState, T> reader)
    {
        if (totalLength <= LobScratchStackThreshold)
        {
            Span<byte> stack = stackalloc byte[totalLength];
            FillLobChain(stack, headIndex);
            return reader(stack, state);
        }

        var rented = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var slice = rented.AsSpan(0, totalLength);
            FillLobChain(slice, headIndex);
            return reader(slice, state);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Convenience overload that copies the chain into a fresh
    /// <see cref="byte"/>[] — used by storage-internals tests where a
    /// concrete array is the natural shape. Hot decode paths should use
    /// the callback overload to avoid the per-call allocation.
    /// </summary>
    public byte[] ReadLobChain(int headIndex, int totalLength) =>
        ReadLobChain(headIndex, totalLength, default(byte), static (span, _) => span.ToArray());

    /// <summary>
    /// Threshold below which <see cref="ReadLobChain{TState, T}"/>'s scratch
    /// buffer lives on the call stack. 256 bytes covers most "small"
    /// LOB-eligible values (short strings, default-mapped <c>nvarchar(MAX)</c>
    /// columns) without inflating the frame; values above the threshold flow
    /// through <see cref="ArrayPool{T}.Shared"/>. The same constant gates
    /// <see cref="RowEncoder"/>'s encode-side scratch buffer.
    /// </summary>
    internal const int LobScratchStackThreshold = 256;

    private void FillLobChain(Span<byte> destination, int headIndex)
    {
        var totalLength = destination.Length;
        var dest = destination;
        var current = headIndex;
        while (current >= 0 && dest.Length > 0)
        {
            var page = this.LobPages[current];
            var payload = page.Payload;
            if (payload.Length > dest.Length)
                throw new InvalidDataException($"LOB chain at head {headIndex} produced more bytes than the row's declared total length {totalLength}.");
            payload.CopyTo(dest);
            dest = dest[payload.Length..];
            current = page.NextPageIndex;
        }
        if (dest.Length != 0)
            throw new InvalidDataException($"LOB chain at head {headIndex} produced fewer bytes than the row's declared total length {totalLength} (short by {dest.Length}).");
    }
}
