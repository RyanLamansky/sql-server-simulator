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
    /// Walks the log in LIFO order and applies the inverse operation for
    /// each entry. Safe to call when the log is empty (no-op). Clears
    /// the log on completion so the same instance can be reused for a
    /// subsequent statement.
    /// </summary>
    public void Rollback()
    {
        for (var i = this.entries.Count - 1; i >= 0; i--)
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
        this.entries.Clear();
    }
}
