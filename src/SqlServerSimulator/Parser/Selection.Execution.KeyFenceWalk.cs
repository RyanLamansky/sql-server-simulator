using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// A SERIALIZABLE read of intervals of the clustered key — a range or point
    /// seek, an <c>IN</c> list, a read in key order either way — producing its
    /// rows by walking the keys in <paramref name="intervals"/> and locking each
    /// in the plan's range mode as it reaches it, as real's seek does, so a
    /// reader suspended mid-result holds the ranges behind its position alone
    /// and a key inserted ahead of it goes in and is read (probed 2026-10-09
    /// against SQL Server 2025: twenty keys held two rows into a result of
    /// 2,007-byte rows, ascending or descending, through a range, an
    /// <c>IN</c> list, an equality prefix of a composite key and under
    /// <c>UPDLOCK</c> / <c>XLOCK</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which anchors outside the rows real locks follows the direction. An
    /// ascending walk locks the key past each interval's upper bound — the
    /// infinity anchor when none is — once it has read the interval, even when
    /// the bound is a key it read. A descending walk locks that same anchor
    /// before its first row, unless the interval's top is a key equal to an
    /// inclusive upper bound, and at its end the last key below the interval,
    /// which has no infinity counterpart. A full key of a unique index
    /// (<see cref="KeyFenceInterval.UniquePoint"/>) takes the plain key lock on
    /// a hit and the next key's range on a miss, each as the walk reaches it.
    /// </para>
    /// <para>
    /// The keys come from the seek cache as the walk begins each interval;
    /// one inserted since into a gap the walk has fenced is read in its place —
    /// below the key just locked for an ascending walk, above the next one for
    /// a descending walk — once the heap has changed, which is what keeps the
    /// fence free of the phantoms it exists against.
    /// </para>
    /// </remarks>
    private static IEnumerable<byte[]> WalkKeyFence(
        HeapTable table, BatchContext batch, DataLockPlan plan, KeyLockGroup group, SqlType[] commons,
        List<KeyFenceInterval> intervals, bool descending, RowLockQualifier? qualifier)
    {
        var mode = plan.SerializableRangeMode!.Value;
        var heap = table.Heap;
        var schema = table.StoredColumns;
        var ordinals = group.Ordinals;
        var cache = HeapSeekCache.For(heap);
        var lookups = !group.IsRowGroup && mode == LockMode.RangeSharedShared;
        // The rows are read under the walk's own key locks: the per-row touch
        // takes the row lock a hint asks for and waits out a writer, nothing
        // more, and keeps no READ COMMITTED position.
        var walked = new DataLockPlan(plan.RowMode, plan.RowTxScoped, plan.SkipBlockedRows, plan.NoLockReader, plan.SerializableRangeMode,
            new PhantomFenceState { Settled = true }, plan.LockingRead, plan.SnapshotConflictCheck, plan.WriteTargetAddresses);
        var io = batch.Connection.StatementIo?.Touch(table);
        _ = io?.ScanCount += intervals.Count == 1 && intervals[0].UniquePoint ? 0 : intervals.Count;
        var lastPage = -1;
        var addresses = batch.CurrentStatement.RowAddresses;
        var tuple = new byte[]?[1];
        var qualifying = qualifier is not null && plan.RowMode is not null;
        RuntimeContext runtime = default;
        if (qualifying)
        {
            FromSource[] one = [qualifier!.Source];
            var memo = new SourceColumnMemo();
            var outer = qualifier!.OuterResolver;
            SqlValue resolve(MultiPartName name) => ResolveAcrossTuple(one, tuple, name, batch, outer, memo);
            runtime = new RuntimeContext(resolve, batch);
        }

        var seen = new HashSet<(int, int)>();
        var pending = new Stack<((int Page, int Slot) Address, SqlValueKey? Key)>();
        var ownWrites = new BatchContext.OwnWriteView(batch, table);
        List<KeyFenceInterval> ordered = [.. intervals];
        ordered.Sort(static (x, y) => CompareLower(x, y));
        if (descending)
            ordered.Reverse();

        foreach (var interval in ordered)
        {
            var point = interval.UniquePoint;
            // A key another session's delete in flight took away is waited out
            // before the walk reads its keys, as real's seek waits on the
            // deleted key's lock: the delete may put the key back.
            long generation;
            List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> keys;
            for (var reads = 1; ; reads++)
            {
                var putBack = Volatile.Read(ref table.KeysPutBack);
                generation = Volatile.Read(ref heap.MutationGeneration);
                keys = cache.KeyLockAnchors(heap, schema, heap, ordinals, commons,
                    interval.Lower, interval.LowerInclusive, interval.Upper, interval.UpperInclusive, int.MaxValue)!;
                if (reads == MaxKeyRereads || (!AwaitDeletesInside(interval) && Volatile.Read(ref table.KeysPutBack) == putBack))
                    break;
            }
            // The rows another request of the statement's transaction wrote
            // since the statement began stand where the statement found them.
            if (batch.OwnWritesOf(table) is { } own)
            {
                var beyond = keys[^1];
                keys.RemoveAt(keys.Count - 1);
                keys = BatchContext.WithOwnWrites(table, own, ordinals, commons, keys, interval.Lower, interval.LowerInclusive, interval.Upper, interval.UpperInclusive);
                keys.Add(beyond);
            }
            if (point)
            {
                // The one key a unique equality can find: its lock — or, on a
                // miss, the next key's range — is had before its row is read.
                batch.AcquireKeyFence(table, group, commons, [interval], mode, KeyFenceKind.Read, lookupRows: false);
            }
            var past = keys[^1].Key;
            keys.RemoveAt(keys.Count - 1);
            if (descending && !point)
            {
                keys.Reverse();
                // The key above the interval fences the gap below it, which the
                // read's top reaches into — save where that top is the bound.
                if (!(interval.Upper is { } upper && interval.UpperInclusive && upper.ComponentCount == group.KeyLength
                    && keys.Count != 0 && keys[0].Key is { } top && HeapSeekCache.CompareKeys(top, upper) == 0))
                {
                    batch.LockFenceAnchor(table, group, past, mode);
                }
            }

            // Where the walk stands: the last key read, or the interval's bound
            // it starts from (`atBound`), which counts inside when inclusive.
            SqlValueKey? last = null;
            var atBound = true;
            for (var position = 0; ; position++)
            {
                if (position < keys.Count)
                {
                    var (key, rids) = keys[position];
                    for (var r = rids.Length - 1; r >= 0; r--)
                        pending.Push((rids[r], key));
                }
                else if (point)
                {
                    break;
                }
                else
                {
                    // The interval's end: an ascending walk locks the key past
                    // it, then reads what arrived in the gap the lock fences; a
                    // descending walk reads what arrived below its last key,
                    // then locks the key it stops at.
                    if (!descending)
                        batch.LockFenceAnchor(table, group, PastAnchor(interval, last, atBound), mode);
                    if (!PushArrivals(Arrivals(interval, last, atBound, below: null)))
                    {
                        if (descending && interval.Lower is { } lower
                            && cache.PreviousKeyBelow(heap, schema, heap, ordinals, commons, lower, interval.LowerInclusive) is { } stop)
                        {
                            batch.LockFenceAnchor(table, group, stop, mode);
                        }
                        break;
                    }
                }

                while (pending.TryPop(out var next))
                {
                    var ((page, slot), key) = next;
                    if (!seen.Add((page, slot)))
                        continue;
                    // The gap the walk is about to pass is fenced: below the key
                    // once it is locked for an ascending walk, above it already —
                    // under the key read last — for a descending one. A key
                    // inserted there since the walk read its keys is read first.
                    if (!point && descending && key is { } reaching && Volatile.Read(ref heap.MutationGeneration) != generation)
                    {
                        if (PushBefore(next, Arrivals(interval, last, atBound, below: reaching)))
                            continue;
                    }
                    // A row another request of the statement's transaction
                    // wrote since the statement began reads as the statement
                    // found it.
                    var noted = ownWrites.TryRead(batch, table, (page, slot), out var image);
                    var prior = noted ? image : heap.ReadLiveRow(page, slot);
                    if (prior is not null && !point)
                        batch.LockWalkedKey(table, group, key, page, slot, mode, generation);
                    if (!point && !descending && key is { } reached && Volatile.Read(ref heap.MutationGeneration) != generation)
                    {
                        if (PushBefore(next, Arrivals(interval, last, atBound, below: reached)))
                            continue;
                    }
                    byte[]? bytes = null;
                    if (prior is not null)
                    {
                        // Read through a nonclustered index, the row is looked
                        // up under the S real's lookup takes.
                        if (lookups)
                            batch.AcquireRowLockTxScoped(table, page, slot, LockMode.Shared);
                        var sequence = heap.WriteSequence;
                        if (!batch.TouchRowForRead(table, page, slot, walked))
                            continue;
                        if (noted)
                            bytes = prior;
                        else if (heap.ReadLiveRow(page, slot) is { } read)
                            bytes = batch.SettleReadCommitted(table, page, slot, walked, read, sequence);
                    }
                    if (bytes is null)
                    {
                        if (noted)
                            continue;
                        // The key the walk found the row by names the rows that
                        // carry it now, should a write since have moved it.
                        var known = key is { } found && group.IsRowGroup ? BatchContext.Normalize(group, found) : (SqlValueKey?)null;
                        if (batch.RowsOfDeletedKey(table, page, slot, walked, known, prior, out var movedKey) is { } moved)
                        {
                            _ = seen.Remove((page, slot));
                            for (var m = moved.Count - 1; m >= 0; m--)
                                pending.Push((moved[m], key ?? movedKey));
                        }
                        continue;
                    }
                    if (key is not null)
                    {
                        last = key;
                        atBound = false;
                    }
                    io?.Enter(page, ref lastPage);
                    addresses?.Record(bytes, page, slot);
                    if (qualifying)
                    {
                        tuple[0] = bytes;
                        if (!Qualifies(qualifier!.Conjuncts, runtime))
                        {
                            batch.ReleaseRowLockAcquisition(table, page, slot, plan.RowMode!.Value);
                            continue;
                        }
                    }
                    yield return bytes;
                }
            }
        }
        tuple[0] = null;

        // Waits out each delete another session has in flight of a key inside
        // `interval`; whether there was one.
        bool AwaitDeletesInside(KeyFenceInterval interval)
        {
            if (OtherSessionsDeletedRows(table, batch) is not { } deleted)
                return false;
            var waited = false;
            foreach (var (image, resource) in deleted)
            {
                if (HeapSeekCache.TryComputeKey(image, ordinals, commons, schema, heap, out var key) && Inside(interval, key))
                {
                    _ = batch.AwaitRowWritersOf(table, resource);
                    waited = true;
                }
            }
            return waited;
        }

        // The keys inside `interval` between where the walk stands and
        // `below` (the interval's far end when null), exclusive of both, in
        // ascending order: those that arrived in a gap the walk has fenced.
        List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> Arrivals(KeyFenceInterval interval, SqlValueKey? last, bool atBound, SqlValueKey? below)
        {
            SqlValueKey? lower, upper;
            bool lowerInclusive, upperInclusive;
            if (descending)
            {
                (upper, upperInclusive) = atBound ? (interval.Upper, interval.UpperInclusive) : (last, false);
                (lower, lowerInclusive) = below is { } b ? (b, false) : (interval.Lower, interval.LowerInclusive);
            }
            else
            {
                (lower, lowerInclusive) = atBound ? (interval.Lower, interval.LowerInclusive) : (last, false);
                (upper, upperInclusive) = below is { } b ? (b, false) : (interval.Upper, interval.UpperInclusive);
            }
            var found = cache.KeyLockAnchors(heap, schema, heap, ordinals, commons, lower, lowerInclusive, upper, upperInclusive, int.MaxValue)!;
            found.RemoveAt(found.Count - 1);
            return batch.WithoutOwnWrites(table, found);
        }

        // The key past `interval`'s upper bound as the table stands now — the
        // infinity anchor when null — where an ascending walk stops.
        SqlValueKey? PastAnchor(KeyFenceInterval interval, SqlValueKey? last, bool atBound)
        {
            var (lower, lowerInclusive) = atBound ? (interval.Lower, interval.LowerInclusive) : (last, false);
            return cache.KeyLockAnchors(heap, schema, heap, ordinals, commons, lower, lowerInclusive, interval.Upper, interval.UpperInclusive, int.MaxValue)![^1].Key;
        }

        // Pushes arrivals so the one the walk reaches first is on top.
        bool PushArrivals(List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> arrivals)
        {
            for (var a = 0; a < arrivals.Count; a++)
            {
                var (key, rids) = arrivals[descending ? a : arrivals.Count - 1 - a];
                for (var r = rids.Length - 1; r >= 0; r--)
                {
                    if (!seen.Contains(rids[r]))
                        pending.Push((rids[r], key));
                }
            }
            return arrivals.Count != 0 && pending.Count != 0;
        }

        // Puts `current` back under the arrivals read before it.
        bool PushBefore(((int Page, int Slot) Address, SqlValueKey? Key) current, List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> arrivals)
        {
            var depth = pending.Count;
            pending.Push(current);
            _ = seen.Remove(current.Address);
            if (PushArrivals(arrivals) && pending.Count > depth + 1)
                return true;
            _ = pending.Pop();
            _ = seen.Add(current.Address);
            return false;
        }
    }

    // Whether `key` lies inside `interval`.
    private static bool Inside(KeyFenceInterval interval, SqlValueKey key)
    {
        if (interval.Lower is { } lower && HeapSeekCache.CompareKeys(key, lower) is var low && (low < 0 || (low == 0 && !interval.LowerInclusive)))
            return false;
        return interval.Upper is not { } upper || (HeapSeekCache.CompareKeys(key, upper) is var high && (high < 0 || (high == 0 && interval.UpperInclusive)));
    }

    // Orders intervals by their lower bound, an open one first.
    private static int CompareLower(KeyFenceInterval x, KeyFenceInterval y) => (x.Lower, y.Lower) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        ({ } a, { } b) => HeapSeekCache.CompareKeys(a, b),
    };
}
