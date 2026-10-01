namespace SqlServerSimulator.Storage;

/// <summary>
/// Closes the gap between a uniqueness check and the write it cleared. The
/// check (seek, wait on the uncommitted writers of the key, raise Msg 2627 /
/// 2601) runs outside the heap's latch, because it can wait on another
/// session; the write publishes the row with its row X under the latch. Two
/// sessions writing one new key could each pass the check before either row
/// existed and both write it. Real takes the new key's X as the row enters
/// the index, so the second writer waits on the first's key and then finds
/// it.
/// <para>
/// The guard is that key lock folded into the row's own: the write takes one
/// last look under the latch at what other sessions wrote since the check
/// began — the inserts' and updates' images, off the seek journal — and
/// refuses when one carries a key this row would duplicate. The caller then
/// checks the row again, and that check meets the other row, published with
/// its X, and waits on it as on any uncommitted key (X mode, as real's
/// second writer waits; visible to deadlock detection and
/// <c>SET LOCK_TIMEOUT</c>), then raises or proceeds. Nothing waits under
/// the latch, and the seek cache, whose lock comes before the latch, isn't
/// consulted there.
/// </para>
/// <para>
/// Uncontended it costs a generation read before the check and a compare
/// under the latch: <see cref="Heap.MutationGeneration"/> hasn't moved, or
/// moved only by this guard's own writes, which advance
/// <see cref="examinedThrough"/> past themselves. A lock-manager lock per
/// unique key would have taken the global gate and interned a lock resource
/// per key for every insert. A statement's own writes never carry a key one
/// of its later rows duplicates — its check compared its rows with each
/// other — so an image the guard didn't write itself can be judged as
/// another session's without asking whose it is. When the journal can't
/// account for every write since <see cref="examinedThrough"/> the guard
/// refuses every write until restarted, and each is checked again.
/// </para>
/// <para>
/// Conservative where an exact answer would need more under the latch: a
/// filtered index's key is compared without its filter, and a key over a
/// non-persisted computed column, which only an expression can produce,
/// refuses on any other session's write. A refusal costs only the second
/// check.
/// </para>
/// </summary>
internal sealed class UniqueKeyWriteGuard
{
    private readonly HeapTable table;

    // Every change to the heap through this generation is one the check saw,
    // one of this guard's own writes, or one collected below.
    private long examinedThrough;

    // Images other sessions wrote since the check began; cleared on restart.
    private List<byte[]>? foreignImages;

    // The journal lost events since examinedThrough: refuse until restarted.
    private bool lost;

    private UniqueKeyWriteGuard(HeapTable table)
    {
        this.table = table;
        this.examinedThrough = Volatile.Read(ref table.Heap.MutationGeneration);
    }

    /// <summary>
    /// A guard begun now, for writes of <paramref name="table"/> whose keys a
    /// check about to start clears; null when the table enforces no unique
    /// key. The caller passes only a table other sessions can write.
    /// </summary>
    public static UniqueKeyWriteGuard? Begin(HeapTable table)
    {
        if (!EnforcesUniqueKey(table))
            return null;
        if (!table.Heap.SeekJournalActive)
            _ = table.Heap.ActivateSeekJournal();
        return new(table);
    }

    private static bool EnforcesUniqueKey(HeapTable table)
    {
        foreach (var constraint in table.KeyConstraints)
        {
            if (!constraint.IsDisabled)
                return true;
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsUnique && !index.IsDisabled)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Starts the check of the next row: the check sees everything written
    /// before now. True when the heap changed since the guard last looked
    /// other than by its own writes, so a key set the statement built from
    /// the heap before then may be missing rows.
    /// </summary>
    public bool Restart()
    {
        var now = Volatile.Read(ref this.table.Heap.MutationGeneration);
        var moved = now != this.examinedThrough;
        this.examinedThrough = now;
        this.foreignImages?.Clear();
        this.lost = false;
        return moved;
    }

    /// <summary>
    /// Under the heap's latch, before the write: whether no other session's
    /// write since the check began carries a key <paramref name="storedValues"/>
    /// would duplicate.
    /// </summary>
    public bool Admits(SqlValue[] storedValues)
    {
        var heap = this.table.Heap;
        var now = heap.MutationGeneration;
        if (now != this.examinedThrough)
        {
            if (!heap.CollectWrittenImagesSince(this.examinedThrough, ref this.foreignImages))
                this.lost = true;
            this.examinedThrough = now;
        }
        if (this.lost)
            return false;
        if (this.foreignImages is not { Count: > 0 } images)
            return true;
        foreach (var image in images)
        {
            if (this.SharesUniqueKey(image, storedValues))
                return false;
        }
        return true;
    }

    /// <summary>Under the heap's latch, after the guarded write.</summary>
    public void NoteOwnWrite() => this.examinedThrough = this.table.Heap.MutationGeneration;

    private bool SharesUniqueKey(byte[] image, SqlValue[] storedValues)
    {
        foreach (var constraint in this.table.KeyConstraints)
        {
            if (!constraint.IsDisabled && (!constraint.KeysAreStored || this.KeyEquals(image, constraint.StorageOrdinals, storedValues)))
                return true;
        }
        foreach (var index in this.table.Indexes)
        {
            if (index.IsUnique && !index.IsDisabled && (!index.KeysAreStored || this.KeyEquals(image, index.KeyStorageOrdinals, storedValues)))
                return true;
        }
        return false;
    }

    // SqlValue.Equals is the comparison the check itself makes: collation-
    // aware, ANSI-padded, and two NULLs equal, as UNIQUE treats them.
    private bool KeyEquals(byte[] image, int[] storageOrdinals, SqlValue[] storedValues)
    {
        foreach (var ordinal in storageOrdinals)
        {
            if (!RowDecoder.DecodeColumn(this.table.StoredColumns, image, ordinal, this.table.Heap).Equals(storedValues[ordinal]))
                return false;
        }
        return true;
    }
}
