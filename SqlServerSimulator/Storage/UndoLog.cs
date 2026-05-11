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
/// consumed their values are rolled back. LOB chains allocated for
/// rolled-back inserts are also outside the log; they leak the same way
/// committed deletes leak (the existing CLAUDE.md quirk). Regular
/// <c>CREATE TABLE</c> / <c>DROP TABLE</c> (non-<c>#</c>) doesn't append
/// entries either — it's a known asymmetry with temp DDL that's
/// transactional; document it where the temp behavior is described.
/// </remarks>
internal sealed class UndoLog
{
    private readonly List<UndoEntry> entries = [];

    public void RecordInsert(Heap heap, int pageIndex, int slotIndex) =>
        this.entries.Add(new SlotChange(heap, UndoKind.Insert, pageIndex, slotIndex));

    public void RecordDelete(Heap heap, int pageIndex, int slotIndex) =>
        this.entries.Add(new SlotChange(heap, UndoKind.Delete, pageIndex, slotIndex));

    public void RecordTempTableCreation(ConcurrentDictionary<string, HeapTable> owner, string name) =>
        this.entries.Add(new TempTableCreation(owner, name));

    public void RecordTempTableRemoval(ConcurrentDictionary<string, HeapTable> owner, string name, HeapTable table) =>
        this.entries.Add(new TempTableRemoval(owner, name, table));

    public void RecordTruncation(Heap heap, List<HeapPage> oldPages, List<HeapLobPage> oldLobPages, (IdentityState State, long? HighWaterMark)[] identitySnapshots) =>
        this.entries.Add(new HeapTruncation(heap, oldPages, oldLobPages, identitySnapshots));

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
    /// Discards all entries without undoing them — the writes stay in the
    /// heap. Called on transaction commit: the log's purpose was to enable
    /// rollback, and a successful commit means rollback won't be needed.
    /// </summary>
    public void Clear() => this.entries.Clear();

    private abstract class UndoEntry
    {
        public abstract void Undo();
    }

    private sealed class SlotChange(Heap heap, UndoKind kind, int pageIndex, int slotIndex) : UndoEntry
    {
        public readonly Heap Heap = heap;
        public readonly UndoKind Kind = kind;
        public readonly int PageIndex = pageIndex;
        public readonly int SlotIndex = slotIndex;

        public override void Undo()
        {
            var page = this.Heap.Pages[this.PageIndex];
            switch (this.Kind)
            {
                case UndoKind.Insert:
                    page.DeleteSlot(this.SlotIndex);
                    break;
                case UndoKind.Delete:
                    page.UndeleteSlot(this.SlotIndex);
                    break;
            }
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
    private sealed class HeapTruncation(Heap heap, List<HeapPage> oldPages, List<HeapLobPage> oldLobPages, (IdentityState State, long? HighWaterMark)[] identitySnapshots) : UndoEntry
    {
        public override void Undo()
        {
            heap.Pages.Clear();
            heap.Pages.AddRange(oldPages);
            heap.LobPages.Clear();
            heap.LobPages.AddRange(oldLobPages);
            for (var i = 0; i < identitySnapshots.Length; i++)
                identitySnapshots[i].State.Restore(identitySnapshots[i].HighWaterMark);
        }
    }
}
