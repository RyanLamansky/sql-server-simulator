using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
/// <param name="sessionPrivate">
/// Sizes the heap's concurrent maps for the session-private table it backs —
/// a table variable, a trigger's pseudo-table, a function's return table —
/// which one session writes, so each holds one lock where a shared table's
/// holds one per processor (about a kilobyte apiece, which every such table
/// once paid).
/// </param>
internal sealed class Heap(bool sessionPrivate = false)
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
    public readonly HeapPageList Pages = [];

    /// <summary>
    /// The heap's write latch — real's page latch, at heap granularity. A
    /// table-level IX lock admits every inserting session at once, as real's
    /// does, so the lock manager serializes nothing here: whatever changes
    /// <see cref="Pages"/>, a page's bytes, <see cref="LobPages"/> and its
    /// free-list, the forward-target set, the reuse candidates and the
    /// row-count bookkeeping runs under this latch, entered through
    /// <see cref="EnterLatch"/>. That is <see cref="Insert"/> (with what its
    /// caller publishes alongside the row, and the last look a uniqueness
    /// check's <see cref="UniqueKeyWriteGuard"/> takes before it), <see cref="UpdateAt"/>,
    /// <see cref="DeleteAt"/>, the LOB chain allocator and free, the tail
    /// trims, and the undo log's page writes on rollback and commit.
    /// <para>
    /// Held for the page mutation only. Nothing under it waits on another
    /// session — no lock wait, trigger, constraint check or read of another
    /// table — because a reader spinning on <see cref="latchSequence"/> may
    /// be the session the holder would wait for. The order is: this latch,
    /// then the lock manager's gate (the insert hook takes a row X that no
    /// session can hold, skipping the abandoned-session sweep), the table's
    /// version-store gate, and the simulation's session registry (the LOB
    /// reclamation's oldest-reader check), each a leaf. No path takes this
    /// latch while holding one of those, nor while holding another heap's
    /// latch; the seek cache and the clustered-order cache read the heap
    /// under their own locks, which therefore come before it.
    /// </para>
    /// <para>
    /// Readers take no latch: they read optimistically against
    /// <see cref="latchSequence"/> (see <see cref="BeginRead"/>), so a scan
    /// pays a volatile read per row and a reader never sees a torn page, a
    /// half-forwarded row or a half-published insert.
    /// </para>
    /// </summary>
    private readonly Lock latch = new();

    /// <summary>
    /// Even at rest, odd while a latch holder is between its first change and
    /// its exit. A reader notes it before reading and re-reads it after: the
    /// same even value both times means no writer touched the heap meanwhile.
    /// </summary>
    private int latchSequence;

    // How deeply the holder has re-entered the latch (an UPDATE's forwarding
    // insert, an undo entry freeing chains); touched only by the holder, and
    // only the outermost enter and exit move the sequence.
    private int latchDepth;

    /// <summary>Enters <see cref="latch"/> for a mutation; dispose the scope to leave it.</summary>
    internal LatchScope EnterLatch()
    {
        this.latch.Enter();
        if (this.latchDepth++ == 0)
            _ = Interlocked.Increment(ref this.latchSequence);
        return new(this);
    }

    private void ExitLatch()
    {
        if (--this.latchDepth == 0)
            Volatile.Write(ref this.latchSequence, this.latchSequence + 1);
        this.latch.Exit();
    }

    /// <summary>A held <see cref="latch"/>, released on dispose.</summary>
    internal readonly ref struct LatchScope(Heap heap)
    {
        public void Dispose() => heap.ExitLatch();
    }

    /// <summary>
    /// Starts an optimistic read: returns the even <see cref="latchSequence"/>
    /// to hand <see cref="EndRead"/>, waiting out a writer mid-mutation first.
    /// The latch holder's own reads (an undo entry reading the row whose chains
    /// it frees) see its writes in progress and return at once.
    /// </summary>
    private int BeginRead()
    {
        var sequence = Volatile.Read(ref this.latchSequence);
        return (sequence & 1) == 0 || this.latch.IsHeldByCurrentThread ? sequence : this.AwaitLatchHolder();
    }

    private int AwaitLatchHolder()
    {
        var spin = new SpinWait();
        int sequence;
        while (((sequence = Volatile.Read(ref this.latchSequence)) & 1) != 0)
            spin.SpinOnce();
        return sequence;
    }

    /// <summary>
    /// Whether no writer touched the heap since <see cref="BeginRead"/>
    /// returned <paramref name="sequence"/>; when false, what was read may be
    /// torn and the caller reads again. The reads it validates must not throw
    /// on torn input — the page accessors on this path bound-check instead.
    /// </summary>
    private bool EndRead(int sequence)
    {
        Volatile.ReadBarrier();
        return Volatile.Read(ref this.latchSequence) == sequence;
    }

    /// <summary>
    /// Slots that are the target of some forwarding pointer. Iteration over
    /// the whole heap skips these (the row will be yielded via the
    /// forwarding slot at the row's stable address); single-slot reads through
    /// <see cref="ReadSlotBytes"/> still resolve them directly so forward-chasing
    /// callers can address them by their physical location. <c>TRUNCATE</c>
    /// clears this alongside <see cref="Pages"/> and <see cref="LobPages"/>;
    /// <see cref="UndoLog"/>'s truncation entry snapshots and restores it.
    /// Concurrent because a scan probes it without the latch, and the probe
    /// runs only while <see cref="forwardTargetCount"/>, which the latch
    /// holder keeps, says it holds something.
    /// </summary>
    private readonly ConcurrentDictionary<(int Page, int Slot), byte> forwardTargets = new();

    private int forwardTargetCount;

    private void AddForwardTarget((int Page, int Slot) target)
    {
        if (this.forwardTargets.TryAdd(target, 0))
            this.forwardTargetCount++;
    }

    private void RemoveForwardTarget((int Page, int Slot) target)
    {
        if (this.forwardTargets.TryRemove(target, out _))
            this.forwardTargetCount--;
    }

    /// <summary>The forward-target set as it stands, for <c>TRUNCATE</c>'s undo entry.</summary>
    internal HashSet<(int Page, int Slot)> SnapshotForwardTargets()
    {
        var snapshot = new HashSet<(int Page, int Slot)>();
        foreach (var (target, _) in this.forwardTargets)
            _ = snapshot.Add(target);
        return snapshot;
    }

    /// <summary>Replaces the forward-target set with <paramref name="targets"/> — empty on <c>TRUNCATE</c>, the snapshot when one rolls back.</summary>
    internal void RestoreForwardTargets(HashSet<(int Page, int Slot)> targets)
    {
        this.forwardTargets.Clear();
        foreach (var target in targets)
            _ = this.forwardTargets.TryAdd(target, 0);
        this.forwardTargetCount = targets.Count;
    }

    /// <summary>
    /// The stand-in for real's uniquifier under a non-unique clustered index:
    /// a row's entry is drawn from <see cref="uniquifierCounter"/> each time an
    /// UPDATE assigns one of the clustered key's columns, which real performs
    /// as a delete and re-insert that gives the row a fresh, higher uniquifier
    /// even when the value stands still (probed 2026-09-29 against SQL Server
    /// 2025: a KEYSET member so updated fetches as <c>@@FETCH_STATUS = -2</c>,
    /// and a DYNAMIC cursor meets it again after the key's other duplicates).
    /// A row never so updated has no entry and reads 0. Null until the first
    /// such update; read by cursor identity and order and by the order a seek
    /// lists equal clustered keys in.
    /// </summary>
    internal ConcurrentDictionary<(int Page, int Slot), long>? Uniquifiers;

    private long uniquifierCounter;

    /// <summary>The row's <see cref="Uniquifiers"/> entry, or 0 when it has none.</summary>
    internal long UniquifierOf((int Page, int Slot) address) =>
        this.Uniquifiers is { } map && map.TryGetValue(address, out var value) ? value : 0;

    /// <summary>
    /// Draws a fresh <see cref="Uniquifiers"/> entry for the row at
    /// <paramref name="address"/>, recording the prior one so a rollback
    /// restores it — real's rolled-back key update leaves the row's identity
    /// as it was (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    internal void Reuniquify((int Page, int Slot) address, UndoLog? undoLog)
    {
        var map = this.Uniquifiers ?? Interlocked.CompareExchange(ref this.Uniquifiers, new(), null) ?? this.Uniquifiers;
        undoLog?.RecordUniquifier(this, address, this.UniquifierOf(address));
        map[address] = Interlocked.Increment(ref this.uniquifierCounter);
    }

    /// <summary>
    /// Per-row version chains used by SNAPSHOT and READ_COMMITTED_SNAPSHOT
    /// readers. Each entry maps a slot's <c>(PageIndex, SlotIndex)</c> tuple
    /// to a <see cref="RowVersionChain"/> that records the slot's commit
    /// timeline (live-row Xmin + history of superseded payloads, oldest
    /// first walked newest-first by visibility logic). Populated lazily on
    /// the first INSERT / UPDATE / DELETE the slot participates in; pre-
    /// existing rows that have never been touched have no entry and are
    /// implicitly committed at Xmin = 0 (visible to every snapshot). Skipped
    /// for table variables / local temp tables / system tables — same set
    /// that bypasses <see cref="HeapTable.RowLocks"/>. Concurrent dict for
    /// the same reason: visibility lookups must run without the lock-manager
    /// gate so SNAPSHOT readers don't serialize behind writers.
    /// <para>
    /// Held by the heap whose addresses key it, not by the table: a
    /// statement rewriting the table into a new heap (an <c>ALTER TABLE</c>
    /// adding or dropping a column) leaves them behind with the old heap, and
    /// a rollback restoring that heap brings them back. On the table, a
    /// snapshot read after the rewrite resolved the old heap's chains against
    /// the new heap's rows at the same addresses, hiding some and showing
    /// others twice.
    /// </para>
    /// </summary>
    public readonly ConcurrentDictionary<(int PageIndex, int SlotIndex), RowVersionChain> RowVersions = sessionPrivate ? new(concurrencyLevel: 1, capacity: 0) : new();

    /// <summary>
    /// Monotonic counter bumped by every <see cref="Insert"/>,
    /// <see cref="DeleteAt"/>, and <see cref="UpdateAt"/>, and by the rollback
    /// of each (<see cref="JournalUndoneRowWrite"/>); the forwarding
    /// UPDATE path may bump multiple times (its internal Insert + Delete each
    /// contribute) and that's fine — read-side equality-seek caches (see
    /// <c>Selection.Execution.IndexSeek.cs</c>) only check whether anything
    /// changed, so any-positive delta forces a rebuild or — once the seek
    /// journal is active (see <see cref="seekJournal"/>) — a delta replay.
    /// Not a transactional value: it advances on the physical mutation and
    /// never rolls back. Advanced only under <see cref="latch"/>, so it needs
    /// no interlocking.
    /// </summary>
    public long MutationGeneration;

    /// <summary>
    /// The <see cref="MutationGeneration"/> at the last visible row inserted or deleted —
    /// a change every column of the table feels, which the statistics freshness of
    /// <see cref="HeapTable"/> reads apart from the per-column updates.
    /// </summary>
    public long LastRowCountChangeGeneration;

    /// <summary>
    /// Every visible row inserted or deleted, the half of a statistic's
    /// modification count every column shares (see
    /// <see cref="HeapTable.ModificationCount"/>). Advanced under the latch;
    /// like <see cref="MutationGeneration"/>, never rolled back.
    /// </summary>
    public long RowModifications;

    /// <summary>
    /// A process-wide clock that advances once per transaction begun (see
    /// <c>SimulatedDbTransaction.BeginEpoch</c>), so a heap's
    /// <see cref="LastModifiedEpoch"/> orders against a transaction's start.
    /// </summary>
    internal static long ModificationEpoch;

    /// <summary>
    /// The <see cref="ModificationEpoch"/> as of this heap's latest row write:
    /// at or past a transaction's begin epoch when the heap changed since that
    /// transaction began. Real's READ COMMITTED read takes its row S only on a
    /// page changed since the oldest open transaction began, which is what the
    /// key-lock test asks of it (see <c>BatchContext.TestRowKeyLock</c>); the
    /// simulator answers per heap rather than per page.
    /// </summary>
    public long LastModifiedEpoch;

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

    /// <summary>
    /// Bounded log of visible-row mutations since the seek cache went live,
    /// enabling the per-<see cref="Heap"/> equality-seek cache to apply a delta
    /// rather than rebuild from a full scan on every mutation — the "no warm-up"
    /// path. Null until <see cref="ActivateSeekJournal"/> runs on the first seek
    /// against this heap, so a never-queried (write-only) table pays nothing.
    /// Trimmed to <see cref="SeekJournalCapacity"/>; older events fall off and
    /// advance <see cref="seekJournalDroppedThroughGen"/>, which forces a full
    /// rebuild for any cache that fell too far behind (a large bulk mutation, or
    /// a heap that wasn't seeked for a long time). A rolled-back row write
    /// journals its reversal (<see cref="JournalUndoneRowWrite"/>); a TRUNCATE
    /// or a restored ALTER clears it via <see cref="InvalidateSeekJournal"/>.
    /// <para>
    /// Guarded by <see cref="latch"/>, under which every writer advances
    /// <see cref="MutationGeneration"/> and records its event, so a snapshot
    /// can't read a generation whose event is missing from the journal: that
    /// would step a seek cache past an insert it never replays, and a
    /// clustered scan following the cache's key order would lose the row.
    /// </para>
    /// </summary>
    private List<SeekJournalEvent>? seekJournal;

    /// <summary>
    /// Index of the oldest live event in <see cref="seekJournal"/>: trimming
    /// advances it rather than shifting the list, and the dead prefix is
    /// dropped once it reaches <see cref="SeekJournalCapacity"/>. Events are
    /// in generation order, so a reader binary-searches its starting point
    /// (<see cref="FirstSeekJournalEventAfter"/>) instead of walking the whole
    /// journal for the few events past its generation.
    /// </summary>
    private int seekJournalHead;

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
    internal volatile bool SeekJournalActive;

    /// <summary>
    /// How many events <see cref="seekJournal"/> keeps: a sixty-fourth of the
    /// rows, and never fewer than 512. A cache replays a delta up to that
    /// length rather than rebuilding from a scan of every row, which is the
    /// cheaper way for any delta well short of the table — a multi-row write
    /// rolled back journals twice its row count. The bound keeps what the
    /// journal's row images hold small beside the table, since every event
    /// it keeps holds its images alive.
    /// </summary>
    internal int SeekJournalCapacity => Math.Max(512, this.RowCount >> 6);

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
        using (this.EnterLatch())
        {
            this.seekJournal ??= [];
            this.SeekJournalActive = true;
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
        using (this.EnterLatch())
        {
            currentGen = this.MutationGeneration;
            if (this.seekJournal is not { } journal || sinceGen < this.seekJournalDroppedThroughGen)
                return null;
            var start = this.FirstSeekJournalEventAfter(sinceGen);
            return [.. CollectionsMarshal.AsSpan(journal)[start..]];
        }
    }

    /// <summary>
    /// The index of the first live journal event past <paramref name="generation"/>,
    /// or the journal's count when there is none.
    /// </summary>
    private int FirstSeekJournalEventAfter(long generation)
    {
        var journal = this.seekJournal!;
        int low = this.seekJournalHead, high = journal.Count;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (journal[mid].Generation > generation)
                high = mid;
            else
                low = mid + 1;
        }
        return low;
    }

    /// <summary>
    /// Adds to <paramref name="images"/> the row image of every insert and
    /// update since <paramref name="generation"/>, read off the seek journal;
    /// false when the journal can't account for them all — not active, or
    /// trimmed past that point. Runs under the latch, for a uniqueness
    /// check's last look (<see cref="UniqueKeyWriteGuard"/>).
    /// </summary>
    internal bool CollectWrittenImagesSince(long generation, ref List<byte[]>? images)
    {
        Debug.Assert(this.latch.IsHeldByCurrentThread, "The seek journal is read under the latch.");
        if (this.seekJournal is not { } journal || generation < this.seekJournalDroppedThroughGen)
            return false;
        for (var i = this.FirstSeekJournalEventAfter(generation); i < journal.Count; i++)
        {
            if (journal[i].NewImage is { } image)
                (images ??= []).Add(image);
        }
        return true;
    }

    /// <summary>
    /// Drops the entire journal and advances <see cref="seekJournalDroppedThroughGen"/>
    /// to the current generation, so every existing cache rebuilds on its next
    /// seek. Called where heap state changes wholesale rather than row by row:
    /// a <c>TRUNCATE</c> and its rollback, which swap the page list, and a
    /// rollback restoring a heap an <c>ALTER</c> replaced. A rolled-back row
    /// write journals its reversal instead (<see cref="JournalUndoneRowWrite"/>).
    /// Journaling stays active — the rebuild re-bases the cache cleanly.
    /// </summary>
    internal void InvalidateSeekJournal()
    {
        if (!this.SeekJournalActive)
            return;
        using var latch = this.EnterLatch();
        this.MutationGeneration++;
        this.seekJournalDroppedThroughGen = this.MutationGeneration;
        this.seekJournal?.Clear();
        this.seekJournalHead = 0;
    }

    /// <summary>
    /// The row a visible address reads as, following a forwarding pointer, or
    /// null when the address holds no live row — the before and after image of
    /// a rolled-back row write (<see cref="UndoLog.RollbackTo"/>). Unlike
    /// <see cref="ReadSlotBytes"/>, a tombstoned slot reads as null. Under the
    /// latch.
    /// </summary>
    internal byte[]? VisibleRowImage((int Page, int Slot) address)
    {
        Debug.Assert(this.latch.IsHeldByCurrentThread, "A rollback reads the row it rewinds under the latch.");
        var page = this.Pages[address.Page];
        if (page.IsSlotTombstoned(address.Slot))
            return null;
        if (!page.IsSlotForwarded(address.Slot))
            return page.ReadSlotBytes(address.Slot);
        var (targetPage, targetSlot) = page.ReadForwardTarget(address.Slot);
        return this.Pages[targetPage].IsSlotTombstoned(targetSlot) ? null : this.Pages[targetPage].ReadSlotBytes(targetSlot);
    }

    /// <summary>
    /// Advances <see cref="MutationGeneration"/> past a rolled-back row write and
    /// journals what the rollback did to the row at <paramref name="address"/>:
    /// a restored row is an insert, a removed one a delete, a rewound one an
    /// update — so a seek cache replays the rollback as it replays a write,
    /// rather than rebuilding from a scan. Runs under the latch hold that
    /// undid the write, so no reader sees the rewound row at a generation whose
    /// journal lacks it. <paramref name="before"/> and <paramref name="after"/>
    /// are the <see cref="VisibleRowImage"/> either side of the undo, read only
    /// while the journal is active.
    /// </summary>
    internal void JournalUndoneRowWrite((int Page, int Slot) address, byte[]? before, byte[]? after)
    {
        Debug.Assert(this.latch.IsHeldByCurrentThread, "A rollback journals the row it rewinds under the latch.");
        this.MutationGeneration++;
        if (!this.SeekJournalActive)
            return;
        switch ((before, after))
        {
            case (null, { }):
                this.RecordSeekJournalEvent(SeekJournalKind.Insert, address.Page, address.Slot, oldImage: null, after);
                break;
            case ({ }, null):
                this.RecordSeekJournalEvent(SeekJournalKind.Delete, address.Page, address.Slot, before, newImage: null);
                break;
            case ({ }, { }):
                this.RecordSeekJournalEvent(SeekJournalKind.Update, address.Page, address.Slot, before, after);
                break;
            default:
                break;
        }
    }

    private void RecordSeekJournalEvent(SeekJournalKind kind, int page, int slot, byte[]? oldImage, byte[]? newImage)
    {
        if (this.seekJournal is not { } journal)
            return;
        journal.Add(new SeekJournalEvent(this.MutationGeneration, kind, page, slot, oldImage, newImage));
        var capacity = this.SeekJournalCapacity;
        while (journal.Count - this.seekJournalHead > capacity)
        {
            this.seekJournalDroppedThroughGen = Math.Max(this.seekJournalDroppedThroughGen, journal[this.seekJournalHead].Generation);
            journal[this.seekJournalHead++] = default;
        }
        if (this.seekJournalHead >= capacity)
        {
            journal.RemoveRange(0, this.seekJournalHead);
            this.seekJournalHead = 0;
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
    public (int PageIndex, int SlotIndex) Insert(ReadOnlySpan<byte> row, UndoLog? undoLog = null)
    {
        using var latch = this.EnterLatch();
        return this.InsertCore(row, undoLog, journalEvent: true);
    }

    /// <summary>
    /// <see cref="Insert"/> that runs <paramref name="placed"/> with the new
    /// row's address before releasing <see cref="latch"/>, so what the caller
    /// publishes with the row — its row X lock, its version-store entry — is
    /// in place before any latch-free reader can see the slot; published after
    /// it, an uncommitted insert would read as unlocked to a READ COMMITTED
    /// reader and as committed long ago to a snapshot. <paramref name="placed"/> runs
    /// under the latch, so it must not wait on another session.
    /// <para>
    /// With a <paramref name="guard"/>, the uniqueness check that cleared
    /// <paramref name="storedValues"/> takes its last look under the same latch
    /// hold, before the row is written: refused, nothing is written and the
    /// address is <c>(-1, -1)</c>, for the caller to check the row again.
    /// </para>
    /// </summary>
    public (int PageIndex, int SlotIndex) Insert<TState>(
        ReadOnlySpan<byte> row, UndoLog? undoLog, TState state, Action<TState, (int PageIndex, int SlotIndex)> placed, UniqueKeyWriteGuard? guard = null, SqlValue[]? storedValues = null)
    {
        using var latch = this.EnterLatch();
        if (guard is not null && !guard.Admits(storedValues!))
            return (-1, -1);
        var address = this.InsertCore(row, undoLog, journalEvent: true);
        placed(state, address);
        guard?.NoteOwnWrite();
        return address;
    }

    // journalEvent is false for the forwarding-UPDATE path's internal target
    // insert — that target is a relocated payload, not a new visible row, so it
    // must not produce a seek-journal Insert; the visible slot's key change rides
    // the Update event UpdateAt records instead.
    private (int PageIndex, int SlotIndex) InsertCore(ReadOnlySpan<byte> row, UndoLog? undoLog, bool journalEvent)
    {
        Debug.Assert(this.latch.IsHeldByCurrentThread, "The heap's latch covers every page mutation.");
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
        this.RowCount++;
        this.MutationGeneration++;
        if (journalEvent)
        {
            this.LastRowCountChangeGeneration = this.MutationGeneration;
            this.RowModifications++;
        }
        this.LastModifiedEpoch = Volatile.Read(ref ModificationEpoch);
        if (undoLog is not null)
        {
            if (journalEvent)
                undoLog.NoteVisibleWrite(this, (pageIndex, slotIndex), before: null, undoLog.Position);
            undoLog.RecordInsert(this, pageIndex, slotIndex);
            if (journalEvent)
                undoLog.MarkRowWrite(undoLog.Position - 1, (pageIndex, slotIndex));
        }
        if (journalEvent && this.SeekJournalActive)
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
    private readonly ConcurrentDictionary<int, byte> reclaimablePages = sessionPrivate ? new(concurrencyLevel: 1, capacity: 0) : new();

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
    /// <remarks>
    /// The walk enumerates the dictionary directly rather than reading its
    /// <c>Keys</c> property: that property takes <em>every</em> one of the
    /// dictionary's locks and copies the keys into a fresh collection, and this
    /// runs on the insert path — once for each row that doesn't fit the tail
    /// page, which on a bulk load is once per page. The direct enumerator is the
    /// lock-free weakly-consistent one the set was chosen for, and the empty
    /// test short-circuits the overwhelmingly common case of a heap nothing has
    /// deleted from. Removing candidates while enumerating is what
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/>'s enumerator supports.
    /// </remarks>
    private bool TryReuseReclaimablePage(ReadOnlySpan<byte> row, out int pageIndex)
    {
        if (this.reclaimablePages.IsEmptyLockFree())
        {
            pageIndex = -1;
            return false;
        }

        var need = row.Length + 2;
        foreach (var (candidate, _) in this.reclaimablePages)
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
            // Even emptied, a page holds no more than what its slot directory
            // leaves, and the directory only grows. Delete-and-insert churn
            // leaves pages whose directory fills them — each still a candidate
            // for the few reclaimable bytes it can't use — and reading
            // ReclaimableBytes walks that whole directory, so checking the bound
            // first is what keeps an insert from walking thousands of slots per
            // such page.
            if (HeapPage.PageSize - HeapPage.HeaderSize - (2 * page.SlotCount) < need)
                continue;
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
        using var latch = this.EnterLatch();
        var removed = 0;
        while (this.Pages.Count > 0)
        {
            var last = this.Pages.Count - 1;
            if (!this.Pages[last].IsFullyDead || pageIsPinned(last))
                break;
            this.RowCount -= this.Pages[last].SlotCount;
            this.Pages.RemoveLast();
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
        using var latch = this.EnterLatch();
        _ = this.RecycleRetiredLobChains();
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
    /// <remarks>
    /// The slot walk is inline rather than delegated to
    /// <see cref="HeapPage.EnumerateRowsWithSlots"/>: this is the path every
    /// table scan in the engine runs, and the nested iterator's per-row
    /// <c>MoveNext</c> plus the repeated slot-directory reads were together a
    /// fifth of a scan's CPU. The forward-target set is probed only when it
    /// holds something — it is empty for any heap no <c>UPDATE</c> has
    /// relocated a row in, and its key is a tuple, so the <c>Count</c> test
    /// keeps a hash probe off every scanned row. Both reads stay per row so a
    /// heap mutated mid-enumeration is seen the same way it was before.
    /// </remarks>
    public IEnumerable<(int PageIndex, int SlotIndex, byte[] Bytes)> EnumerateRowsWithAddress()
    {
        var sequence = Volatile.Read(ref this.latchSequence);
        for (var p = 0; p < this.Pages.Count; p++)
        {
            var page = this.Pages[p];
            var slotCount = page.SlotCount;
            for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                if (this.ReadScanSlotFast(page, p, slotIndex, ref sequence, out var bytes) == SlotRead.Live)
                    yield return (p, slotIndex, bytes);
            }
        }
    }

    /// <summary>
    /// The slots a snapshot reader walks: every live row as
    /// <see cref="EnumerateRowsWithAddress"/> yields it, and every deleted
    /// slot with null bytes, since the snapshot may predate the delete — each
    /// with the <see cref="WriteSequence"/> it was read at. The reader pairs
    /// the slot with the row's version chain, read after it; when the
    /// sequence has moved by then, it reads the slot again
    /// (<see cref="TryReadSlot"/>).
    /// </summary>
    public IEnumerable<(int PageIndex, int SlotIndex, byte[]? Bytes, int Sequence)> EnumerateSlots()
    {
        var sequence = Volatile.Read(ref this.latchSequence);
        for (var p = 0; p < this.Pages.Count; p++)
        {
            var page = this.Pages[p];
            var slotCount = page.SlotCount;
            for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                switch (this.ReadScanSlotFast(page, p, slotIndex, ref sequence, out var bytes))
                {
                    case SlotRead.Live:
                        yield return (p, slotIndex, bytes, sequence);
                        break;
                    case SlotRead.Tombstoned:
                        yield return (p, slotIndex, null, sequence);
                        break;
                    default:
                        break;
                }
            }
        }
    }

    /// <summary>
    /// One slot as <see cref="EnumerateSlots"/> reads it, again: false when
    /// the address lies past the heap's slots; otherwise
    /// <paramref name="bytes"/> is the row, followed through a forward
    /// pointer, or null for a deleted slot.
    /// </summary>
    public bool TryReadSlot(int pageIndex, int slotIndex, out byte[]? bytes, out int sequence)
    {
        bytes = null;
        sequence = this.WriteSequence;
        if ((uint)pageIndex >= (uint)this.Pages.Count)
            return false;
        var page = this.Pages[pageIndex];
        if ((uint)slotIndex >= page.SlotCount)
            return false;
        var read = this.ReadScanSlot(page, pageIndex, slotIndex, out var live, out sequence);
        bytes = read == SlotRead.Tombstoned ? null : live;
        return true;
    }

    /// <summary>
    /// The live row at the address, read at one moment and followed through a
    /// forward pointer, or null when the slot is deleted or past the heap's
    /// slots: what a reader testing <see cref="IsSlotTombstoned"/> and then
    /// reading <see cref="ReadSlotBytes"/> means, without another session's
    /// delete — and the compaction reclaiming its bytes — landing between the
    /// two.
    /// </summary>
    public byte[]? ReadLiveRow(int pageIndex, int slotIndex) =>
        this.TryReadSlot(pageIndex, slotIndex, out var bytes, out _) ? bytes : null;

    /// <summary>
    /// Advances by two with every latched mutation of the heap, a rollback's
    /// page writes included: a reader that notes it before reading a row and
    /// finds it moved afterwards knows the row may have changed since.
    /// </summary>
    public int WriteSequence => Volatile.Read(ref this.latchSequence);

    private enum SlotRead : byte
    {
        Live,
        Tombstoned,
        ForwardTarget,
    }

    /// <summary>
    /// <see cref="ReadScanSlot"/> with its common case inlined into the scan:
    /// no writer mid-mutation, no forwarding anywhere in the heap, a live slot.
    /// <paramref name="sequence"/> is a <see cref="latchSequence"/> the scan
    /// read before this slot — the one the previous slot validated against —
    /// so one read per row both closes this row's validation and opens the
    /// next's; a write in between, the consumer's own included, fails it and
    /// sends the row to the retrying reader. The scan is the path every table
    /// read runs, and the call into that reader plus a second read per row
    /// cost several percent of a whole-table count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SlotRead ReadScanSlotFast(HeapPage page, int pageIndex, int slotIndex, ref int sequence, out byte[] bytes)
    {
        if ((sequence & 1) == 0 && this.forwardTargetCount == 0 && page.TryReadLiveSlot(slotIndex, out bytes, out var forwarded) && !forwarded)
        {
            Volatile.ReadBarrier();
            if (Volatile.Read(ref this.latchSequence) == sequence)
                return SlotRead.Live;
        }
        return this.ReadScanSlot(page, pageIndex, slotIndex, out bytes, out sequence);
    }

    /// <summary>
    /// One slot of a scan, read optimistically (see <see cref="BeginRead"/>):
    /// live, deleted, or a forward target the scan leaves to its forwarder;
    /// for a live slot, its row bytes, followed through a forward pointer.
    /// Retries until no writer moved meanwhile, so a forwarding UPDATE's
    /// target insert, pointer write and target registration are seen together
    /// or not at all.
    /// </summary>
    private SlotRead ReadScanSlot(HeapPage page, int pageIndex, int slotIndex, out byte[] bytes, out int sequence)
    {
        while (true)
        {
            sequence = this.BeginRead();
            var read = !page.TryReadLiveSlot(slotIndex, out bytes, out var forwarded)
                ? SlotRead.Tombstoned
                : this.forwardTargetCount != 0 && this.forwardTargets.ContainsKey((pageIndex, slotIndex))
                    ? SlotRead.ForwardTarget
                    : SlotRead.Live;
            if (read == SlotRead.Live && forwarded)
                bytes = this.ReadForwardedRow(page, slotIndex) ?? [];
            if (this.EndRead(sequence))
                return read;
        }
    }

    // The row a forwarding slot points at; null where a torn read named no page.
    private byte[]? ReadForwardedRow(HeapPage page, int slotIndex)
    {
        var (tp, ts) = page.ReadForwardTarget(slotIndex);
        return (uint)tp < (uint)this.Pages.Count ? this.Pages[tp].ReadSlotBytes(ts) : null;
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
    /// <see cref="forwardTargets"/>. The target's Delete entry carries the
    /// <paramref name="reclaimSuperseded"/> gate (it owns the row + chains); the
    /// pointer's entry only reclaims its directory slot — its bytes are a
    /// forward pointer, not a row, so they must never be decoded for LOB heads.
    /// Rollback resurrects both slots and re-registers the target.
    /// </remarks>
    public void DeleteAt(int pageIndex, int slotIndex, UndoLog? undoLog = null, bool reclaimSuperseded = false)
    {
        using var latch = this.EnterLatch();
        _ = this.TouchedSlots?.Add((pageIndex, slotIndex));
        _ = this.RootedNullLobCells?.TryRemove((pageIndex, slotIndex), out _);
        var firstUndoEntry = undoLog?.Position ?? 0;
        if (undoLog?.ImageWatchers is not null)
            undoLog.NoteVisibleWrite(this, (pageIndex, slotIndex), this.ReadSlotBytes(pageIndex, slotIndex), firstUndoEntry);
        this.DeleteAtCore(pageIndex, slotIndex, undoLog, reclaimSuperseded, journalEvent: true);
        undoLog?.MarkRowWrite(firstUndoEntry, (pageIndex, slotIndex));
    }

    /// <summary>
    /// The <c>text</c> / <c>ntext</c> / <c>image</c> cells a write set NULL
    /// after they had held a value, keyed by row address with a bit per
    /// stored ordinal below 64. Real keeps such a cell's LOB root allocated,
    /// so <c>TEXTPTR</c> still hands out a pointer to it and the pointer stays
    /// valid, where a cell that was never given a value has none (probed
    /// 2026-09-30 against SQL Server 2025). A deleted row's entry goes with it,
    /// so a later row at a reused address starts clear.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<(int PageIndex, int SlotIndex), ulong>? RootedNullLobCells;

    /// <summary>Records that the cell at <paramref name="storedOrdinal"/> of the row at the address keeps its LOB root while NULL.</summary>
    public void MarkRootedNullLob(int pageIndex, int slotIndex, int storedOrdinal)
    {
        if (storedOrdinal >= 64)
            return;
        var cells = this.RootedNullLobCells ?? Interlocked.CompareExchange(ref this.RootedNullLobCells, new(), null) ?? this.RootedNullLobCells;
        _ = cells.AddOrUpdate((pageIndex, slotIndex), 1UL << storedOrdinal, (_, bits) => bits | (1UL << storedOrdinal));
    }

    /// <summary>Whether <see cref="MarkRootedNullLob"/> recorded the cell.</summary>
    public bool IsRootedNullLob((int PageIndex, int SlotIndex) address, int storedOrdinal) =>
        storedOrdinal < 64 && this.RootedNullLobCells is { } cells && cells.TryGetValue(address, out var bits) && (bits & (1UL << storedOrdinal)) != 0;

    /// <summary>
    /// When set, every visible address an <see cref="UpdateAt"/> or
    /// <see cref="DeleteAt"/> writes — the stand-in heap a write to a linked
    /// server's table runs against records which rows its statement reached,
    /// an UPDATE setting a column to the value it already held included.
    /// </summary>
    public HashSet<(int PageIndex, int SlotIndex)>? TouchedSlots;

    // journalEvent is false for the forwarding-UPDATE path's internal old-target
    // delete — the old target is a superseded relocated payload, not the removal
    // of a visible row, so it must not produce a seek-journal Delete.
    private void DeleteAtCore(int pageIndex, int slotIndex, UndoLog? undoLog, bool reclaimSuperseded, bool journalEvent)
    {
        var oldImage = journalEvent && this.SeekJournalActive ? this.ReadSlotBytes(pageIndex, slotIndex) : null;
        this.MutationGeneration++;
        if (journalEvent)
        {
            this.LastRowCountChangeGeneration = this.MutationGeneration;
            this.RowModifications++;
        }
        this.LastModifiedEpoch = Volatile.Read(ref ModificationEpoch);
        var page = this.Pages[pageIndex];
        if (page.IsSlotForwarded(slotIndex))
        {
            var target = page.ReadForwardTarget(slotIndex);
            undoLog?.RecordDelete(this, target.PageIndex, target.SlotIndex, reclaimSuperseded);
            this.Pages[target.PageIndex].DeleteSlot(target.SlotIndex);
            undoLog?.RecordForwardedPointerDelete(this, pageIndex, slotIndex, target);
            page.DeleteSlot(slotIndex);
            this.RemoveForwardTarget(target);
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
        using var latch = this.EnterLatch();
        this.UpdateAtCore(pageIndex, slotIndex, newRow, undoLog, reclaimSuperseded);
    }

    /// <summary>
    /// <see cref="UpdateAt"/> whose uniqueness check takes its last look under
    /// the latch, as a guarded <see cref="Insert{TState}"/>'s does: false, with
    /// nothing written, when <paramref name="guard"/> refuses
    /// <paramref name="storedValues"/>.
    /// </summary>
    public bool TryUpdateAt(int pageIndex, int slotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog, bool reclaimSuperseded, UniqueKeyWriteGuard guard, SqlValue[] storedValues)
    {
        using var latch = this.EnterLatch();
        if (!guard.Admits(storedValues))
            return false;
        this.UpdateAtCore(pageIndex, slotIndex, newRow, undoLog, reclaimSuperseded);
        guard.NoteOwnWrite();
        return true;
    }

    private void UpdateAtCore(int pageIndex, int slotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog, bool reclaimSuperseded)
    {
        // The visible Rid (pageIndex, slotIndex) is stable across an UPDATE even
        // when the payload relocates (the original slot keeps its forward bit),
        // so the seek journal records one Update at that address. Capture the
        // pre-UPDATE visible image before the mutation; the internal target
        // Insert / old-target Delete the relocating paths run are NOT journaled.
        _ = this.TouchedSlots?.Add((pageIndex, slotIndex));
        var oldImage = this.SeekJournalActive ? this.ReadSlotBytes(pageIndex, slotIndex) : null;
        var firstUndoEntry = undoLog?.Position ?? 0;
        if (undoLog?.ImageWatchers is not null)
            undoLog.NoteVisibleWrite(this, (pageIndex, slotIndex), oldImage ?? this.ReadSlotBytes(pageIndex, slotIndex), firstUndoEntry);
        var page = this.Pages[pageIndex];
        if (page.IsSlotForwarded(slotIndex))
            this.UpdateForwarded(page, pageIndex, slotIndex, newRow, undoLog, reclaimSuperseded);
        else
            this.UpdateDirect(page, pageIndex, slotIndex, newRow, undoLog, reclaimSuperseded);
        this.MutationGeneration++;
        this.LastModifiedEpoch = Volatile.Read(ref ModificationEpoch);
        if (oldImage is not null)
            this.RecordSeekJournalEvent(SeekJournalKind.Update, pageIndex, slotIndex, oldImage, newRow.ToArray());
        undoLog?.MarkRowWrite(firstUndoEntry, (pageIndex, slotIndex));
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
            this.AddForwardTarget(target);
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
            this.RemoveForwardTarget(oldTarget);
            this.AddForwardTarget(newTarget);
        }
    }

    /// <summary>
    /// Undo callback for <see cref="UndoLog.RecordForwardInstall"/> — removes
    /// the target from <see cref="forwardTargets"/> after the page-level
    /// forward bit is cleared. Called as part of the rollback walk; the
    /// target slot itself is tombstoned by the paired <see cref="UndoKind.Insert"/>
    /// entry.
    /// </summary>
    internal void UnregisterForwardTargetForUndo((int Page, int Slot) target) =>
        this.RemoveForwardTarget(target);

    /// <summary>
    /// Undo callback for <see cref="UndoLog.RecordForwardRetarget"/> — restores
    /// the forwarding-target tracking to its pre-UPDATE shape (old target back
    /// in, new target removed). The old target slot's tombstone bit is cleared
    /// by the paired <see cref="UndoKind.Delete"/> entry.
    /// </summary>
    internal void SwapForwardTargetForUndo((int Page, int Slot) oldTarget, (int Page, int Slot) newTarget)
    {
        this.RemoveForwardTarget(newTarget);
        this.AddForwardTarget(oldTarget);
    }

    /// <summary>
    /// Undo callback for <see cref="UndoLog.RecordForwardedPointerDelete"/> —
    /// re-registers a target whose forwarding row was deleted, so the
    /// resurrected pointer surfaces the row once (via the forwarder) rather than
    /// the target also appearing as a standalone live row.
    /// </summary>
    internal void ReinstateForwardTargetForUndo((int Page, int Slot) target) =>
        this.AddForwardTarget(target);

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
        while (true)
        {
            var sequence = this.BeginRead();
            var bytes = page.IsSlotForwarded(slotIndex) ? this.ReadForwardedRow(page, slotIndex) : page.ReadSlotBytes(slotIndex);
            if (this.EndRead(sequence))
                return bytes;
        }
    }

    /// <summary>
    /// Returns true when the slot at the given Rid is past the page's high-
    /// water mark or has been tombstoned. Snapshot-aware iteration uses this
    /// to identify chain entries whose live slot is no longer in the heap's
    /// live row stream and surface a historical version instead.
    /// </summary>
    public bool IsSlotTombstoned(int pageIndex, int slotIndex) =>
        pageIndex < 0 || pageIndex >= this.Pages.Count || this.Pages[pageIndex].IsSlotTombstoned(slotIndex);

    /// <summary>
    /// Total slot count across all pages — maintained rather than walked,
    /// because the join planner reads it once per join level per execution to
    /// size the seek-vs-hash choice and the hash build's row lists, which made
    /// an O(pages) walk a per-query cost that grows with the table.
    /// <para>
    /// Every seam that moves it is one of four: <see cref="InsertCore"/> (each
    /// insert appends exactly one slot, whether to the tail page, a reused
    /// reclaimable page, or a fresh one), <see cref="TrimTrailingDeadPages"/>
    /// (drops whole pages from the tail), <c>TRUNCATE</c> and the undo log's
    /// truncation restore — the last two clearing / re-attaching
    /// <see cref="Pages"/> wholesale and re-deriving the count through
    /// <see cref="RecomputeRowCount"/>. Nothing else changes a page's slot
    /// count: a DELETE tombstones its slot in place, an UNDO un-tombstones it,
    /// and <see cref="HeapPage.Compact"/> preserves slot indices by design.
    /// </para>
    /// Counts slots rather than live rows (a tombstone keeps its directory
    /// entry), which is what the walk it replaces counted.
    /// </summary>
    public int RowCount;

    /// <summary>
    /// The rows a scan yields — the slots less tombstones and forward targets —
    /// which is what <c>sys.partitions</c> and the other catalog row counts
    /// report, where <see cref="RowCount"/> keeps counting a deleted row's
    /// slot. Walks the slot directories, so it is for catalog readers, not
    /// per-query planning.
    /// </summary>
    public long CountLiveRows()
    {
        long count = 0;
        foreach (var page in this.Pages)
            count += page.UntombstonedSlotCount;
        return count - this.forwardTargetCount;
    }

    /// <summary>
    /// Re-derives <see cref="RowCount"/> from the pages. The two seams that
    /// replace <see cref="Pages"/> wholesale call it; the storage-internals
    /// tests call it to assert the maintained count against the walk.
    /// </summary>
    internal int RecomputeRowCount()
    {
        var count = 0;
        foreach (var page in this.Pages)
            count += page.SlotCount;
        this.RowCount = count;
        return count;
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
        using var latch = this.EnterLatch();
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
        if (this.freeLobPages.TryPop(out var recycled) || (this.RecycleRetiredLobChains() && this.freeLobPages.TryPop(out recycled)))
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
        using var latch = this.EnterLatch();
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
    /// Chains a commit, a rollback or the version sweep gave up, each tagged
    /// with the <see cref="LobReclamation"/> epoch it retired at, oldest
    /// first; <see cref="RecycleRetiredLobChains"/> frees them once no running
    /// statement predates the tag. Touched only under <see cref="latch"/>.
    /// </summary>
    private readonly Queue<(int Head, long Epoch, LobReclamation Reclamation)> retiredLobChains = new();

    /// <summary>
    /// Gives up the chain rooted at <paramref name="headIndex"/>, as
    /// <see cref="FreeLobChain"/> does, but leaves its pages unused until
    /// every statement running as it retires — any of which may hold a row
    /// image naming them — has ended (see <see cref="LobReclamation"/>).
    /// Without <paramref name="reclamation"/> the chain frees at once.
    /// </summary>
    public void RetireLobChain(int headIndex, LobReclamation? reclamation)
    {
        using var latch = this.EnterLatch();
        if (reclamation is null)
            this.FreeLobChain(headIndex);
        else
            this.retiredLobChains.Enqueue((headIndex, reclamation.Retire(), reclamation));
    }

    /// <summary>Frees the retired chains no running statement predates; true when it freed any.</summary>
    private bool RecycleRetiredLobChains()
    {
        if (this.retiredLobChains.Count == 0)
            return false;
        var oldestReader = this.retiredLobChains.Peek().Reclamation.OldestReader();
        var freed = false;
        while (this.retiredLobChains.TryPeek(out var retired) && retired.Epoch <= oldestReader)
        {
            this.FreeLobChain(this.retiredLobChains.Dequeue().Head);
            freed = true;
        }
        return freed;
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
        this.retiredLobChains.Clear();
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

    /// <summary>
    /// How many LOB pages have been read off this heap, ever: what a
    /// statement's <c>STATISTICS IO</c> reports as its LOB reads is this
    /// counter's growth while it ran (<see cref="Parser.IoTableCounts"/>).
    /// </summary>
    public long LobPagesRead;

    // Read optimistically (see BeginRead), re-checking the sequence per page
    // so a walk a writer tears — a page it frees or reuses midway — restarts
    // rather than following a link the writer is halfway through. A chain
    // that reads wrong with no writer in the way was freed and reused after
    // the reader read the row naming it, which LobReclamation rules out for
    // a reader inside an announced statement.
    private void FillLobChain(Span<byte> destination, int headIndex)
    {
        while (true)
        {
            var sequence = this.BeginRead();
            var dest = destination;
            var current = headIndex;
            var overrun = false;
            while (current >= 0 && dest.Length > 0 && current < this.LobPages.Count && this.EndRead(sequence))
            {
                var page = this.LobPages[current];
                this.LobPagesRead++;
                var payload = page.Payload;
                if (payload.Length > dest.Length)
                {
                    overrun = true;
                    break;
                }
                payload.CopyTo(dest);
                dest = dest[payload.Length..];
                current = page.NextPageIndex;
            }
            if (!this.EndRead(sequence))
                continue;
            if (overrun || dest.Length != 0)
                throw SimulatedSqlException.NoLockScanDataMovement();
            return;
        }
    }
}
