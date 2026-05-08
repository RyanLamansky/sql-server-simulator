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
/// Per-statement (Bundle 1) / per-connection-transaction (Bundle 2) record
/// of heap mutations, walked in reverse on rollback. Insert entries are
/// undone by tombstoning the slot they created; delete entries by clearing
/// the tombstone bit on the slot they cleared. UPDATE decomposes to a
/// delete-of-old plus an insert-of-new pair, so its rollback is naturally
/// the inverse pair walked LIFO.
/// </summary>
/// <remarks>
/// Identity counters and the database-scoped rowversion counter are
/// intentionally outside the log — probe-confirmed against SQL Server
/// 2025 (2026-05-08): both keep advancing even when the writes that
/// consumed their values are rolled back. LOB chains allocated for
/// rolled-back inserts are also outside the log; they leak the same way
/// committed deletes leak (the existing CLAUDE.md quirk).
/// </remarks>
internal sealed class UndoLog
{
    private readonly List<(Heap Heap, UndoKind Kind, int PageIndex, int SlotIndex)> entries = [];

    public void RecordInsert(Heap heap, int pageIndex, int slotIndex) =>
        this.entries.Add((heap, UndoKind.Insert, pageIndex, slotIndex));

    public void RecordDelete(Heap heap, int pageIndex, int slotIndex) =>
        this.entries.Add((heap, UndoKind.Delete, pageIndex, slotIndex));

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
        {
            var (heap, kind, pageIndex, slotIndex) = this.entries[i];
            var page = heap.Pages[pageIndex];
            switch (kind)
            {
                case UndoKind.Insert:
                    page.DeleteSlot(slotIndex);
                    break;
                case UndoKind.Delete:
                    page.UndeleteSlot(slotIndex);
                    break;
            }
        }
        this.entries.RemoveRange(position, this.entries.Count - position);
    }

    /// <summary>
    /// Convenience — full rollback to position 0. Equivalent to
    /// <c>RollbackTo(0)</c>.
    /// </summary>
    public void Rollback() => RollbackTo(0);

    /// <summary>
    /// Discards all entries without undoing them — the writes stay in the
    /// heap. Called on transaction commit: the log's purpose was to enable
    /// rollback, and a successful commit means rollback won't be needed.
    /// </summary>
    public void Clear() => this.entries.Clear();
}
