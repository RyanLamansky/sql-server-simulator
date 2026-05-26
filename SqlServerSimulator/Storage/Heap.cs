using System.Buffers;

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
    /// changed, so any-positive delta forces a rebuild. Not a transactional
    /// value: it advances on the physical mutation and never rolls back (a
    /// rolled-back insert/delete/update still bumped it, which only forces a
    /// harmless cache rebuild). Mutations on a given table are lock-serialized,
    /// so this needs no interlocking.
    /// </summary>
    public long MutationGeneration;

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
        if (row.Length > MaxRowSize)
            throw new NotSupportedException($"Row of {row.Length} bytes exceeds SQL Server's per-row maximum of {MaxRowSize}; the encoder should have pushed variable-length columns off-row.");

        int pageIndex;
        if (this.Pages.Count > 0 && this.Pages[^1].TryInsert(row))
        {
            pageIndex = this.Pages.Count - 1;
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
        return (pageIndex, slotIndex);
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
    /// as deleted. The slot is tombstoned at the page level; row payload
    /// bytes are not reclaimed and any LOB chain the row referenced is
    /// orphaned (left in <see cref="LobPages"/>) — see CLAUDE.md for the
    /// LOB-leak quirk on UPDATE / DELETE.
    /// </summary>
    public void DeleteAt(int pageIndex, int slotIndex, UndoLog? undoLog = null)
    {
        this.MutationGeneration++;
        undoLog?.RecordDelete(this, pageIndex, slotIndex);
        this.Pages[pageIndex].DeleteSlot(slotIndex);
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
    /// The orphaned-LOB-chain quirk still applies — both the in-place and
    /// forwarding paths allocate fresh LOB chains for the new payload without
    /// tombstoning the old row's LOB references.
    /// </remarks>
    public void UpdateAt(int pageIndex, int slotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog = null)
    {
        var page = this.Pages[pageIndex];
        if (page.IsSlotForwarded(slotIndex))
            this.UpdateForwarded(page, pageIndex, slotIndex, newRow, undoLog);
        else
            this.UpdateDirect(page, pageIndex, slotIndex, newRow, undoLog);
        this.MutationGeneration++;
    }

    private void UpdateDirect(HeapPage page, int pageIndex, int slotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog)
    {
        var oldExtent = page.SlotExtent(slotIndex);
        if (newRow.Length <= oldExtent)
        {
            var oldBytes = page.ReadSlotBytes(slotIndex)!;
            undoLog?.RecordInPlaceRewrite(this, pageIndex, slotIndex, oldBytes);
            page.RewriteSlotInPlace(slotIndex, newRow);
        }
        else
        {
            var oldBytes = page.ReadSlotBytes(slotIndex)!;
            var target = this.Insert(newRow, undoLog);
            undoLog?.RecordForwardInstall(this, pageIndex, slotIndex, oldBytes, target);
            this.Pages[pageIndex].InstallForward(slotIndex, target);
            _ = this.ForwardTargets.Add(target);
        }
    }

    private void UpdateForwarded(HeapPage originalPage, int originalPageIndex, int originalSlotIndex, ReadOnlySpan<byte> newRow, UndoLog? undoLog)
    {
        var oldTarget = originalPage.ReadForwardTarget(originalSlotIndex);
        var targetPage = this.Pages[oldTarget.PageIndex];
        var targetExtent = targetPage.SlotExtent(oldTarget.SlotIndex);
        if (newRow.Length <= targetExtent)
        {
            // Fits at the existing forward target — rewrite there, forward pointer untouched.
            var oldTargetBytes = targetPage.ReadSlotBytes(oldTarget.SlotIndex)!;
            undoLog?.RecordInPlaceRewrite(this, oldTarget.PageIndex, oldTarget.SlotIndex, oldTargetBytes);
            targetPage.RewriteSlotInPlace(oldTarget.SlotIndex, newRow);
        }
        else
        {
            // Doesn't fit. Insert at a fresh target, tombstone the old one, and
            // re-point the original slot's forward. Forward bit at the original
            // never clears; the row keeps its visible identity.
            var newTarget = this.Insert(newRow, undoLog);
            this.DeleteAt(oldTarget.PageIndex, oldTarget.SlotIndex, undoLog);
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
    /// Splits <paramref name="data"/> into <see cref="HeapLobPage.MaxPayload"/>-sized
    /// chunks, allocates a page chain in <see cref="LobPages"/>, and returns
    /// the index of the chain's head page. Empty inputs allocate a single
    /// zero-payload page so the row's pointer is always valid; callers that
    /// want NULL semantics should not call this method at all.
    /// </summary>
    public int AllocateLobChain(ReadOnlySpan<byte> data)
    {
        var head = this.LobPages.Count;
        var remaining = data;
        while (true)
        {
            var chunkSize = Math.Min(HeapLobPage.MaxPayload, remaining.Length);
            var page = new HeapLobPage();
            page.WritePayload(remaining[..chunkSize]);
            this.LobPages.Add(page);
            remaining = remaining[chunkSize..];
            if (remaining.Length == 0)
                return head;
            // The just-added page's next pointer references the page we're
            // about to allocate.
            page.NextPageIndex = this.LobPages.Count;
        }
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
