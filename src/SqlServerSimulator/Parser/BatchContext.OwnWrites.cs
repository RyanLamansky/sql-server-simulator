using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// What a statement waiting on its client reads in place of the rows another
// request of its own transaction wrote meanwhile (see OwnWriteImages): the
// read paths ask here for the row an address held when the statement began,
// and merge the rows the writes took away or moved back into the key order
// they read on in.
internal sealed partial class BatchContext
{
    /// <summary>
    /// The rows of <paramref name="table"/> another request of the running
    /// statement's transaction wrote since the statement began, as the
    /// statement found them, or null when none did — the common case, costing
    /// a field read.
    /// </summary>
    internal OwnWriteImages? OwnWritesOf(HeapTable table) =>
        this.Connection.ActiveOwnWriteImages is { } images && images.Covers(table.Heap) ? images : null;

    /// <summary>
    /// Whether another request of the running statement's transaction wrote
    /// the row at <paramref name="address"/> of <paramref name="table"/> since
    /// the statement began, with <paramref name="image"/> the row as the
    /// statement found it — null for a row it didn't find, which it doesn't read.
    /// </summary>
    internal bool TryReadOwnWrite(HeapTable table, (int Page, int Slot) address, out byte[]? image)
    {
        if (this.Connection.ActiveOwnWriteImages is { } images)
            return images.TryGet(table.Heap, address, out image);
        image = null;
        return false;
    }

    /// <summary>
    /// <see cref="TryReadOwnWrite"/> for a read path's per-row use: the images
    /// can only change while the statement waits on its client, so the view
    /// looks them up again only once the statement's suspensions have moved,
    /// a row costing a field compare otherwise.
    /// </summary>
    internal struct OwnWriteView(BatchContext batch, HeapTable table)
    {
        private int suspensions = batch.CurrentStatement.Suspensions;

        private OwnWriteImages? images = batch.OwnWritesOf(table);

        /// <summary>
        /// Whether another request of the statement's transaction wrote the row
        /// at <paramref name="address"/> of <paramref name="table"/> since the
        /// statement of <paramref name="batch"/> began — the two the view was
        /// made for — with <paramref name="image"/> the row as the statement
        /// found it.
        /// </summary>
        public bool TryRead(BatchContext batch, HeapTable table, (int Page, int Slot) address, out byte[]? image)
        {
            if (batch.CurrentStatement.Suspensions != this.suspensions)
            {
                this.suspensions = batch.CurrentStatement.Suspensions;
                this.images = batch.OwnWritesOf(table);
            }
            if (this.images is { } own)
                return own.TryGet(table.Heap, address, out image);
            image = null;
            return false;
        }
    }

    /// <summary>
    /// <paramref name="anchors"/> — keys of <paramref name="table"/> over
    /// <paramref name="ordinals"/> between the bounds, ascending, each with the
    /// rows carrying it now — as the running statement found them: the rows
    /// another request of its transaction wrote since it began leave the keys
    /// they carry now, and the ones it found return under the keys they
    /// carried then, inside the bounds.
    /// </summary>
    internal static List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> WithOwnWrites(
        HeapTable table, OwnWriteImages own, int[] ordinals, SqlType[] commons, List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> anchors,
        SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive)
    {
        var heap = table.Heap;
        var found = new List<(SqlValueKey Key, (int Page, int Slot) Address)>();
        foreach (var (address, image) in own.Found(heap))
        {
            if (HeapSeekCache.TryComputeKey(image, ordinals, commons, table.StoredColumns, heap, out var key) && Within(key, lower, lowerInclusive, upper, upperInclusive))
                found.Add((key, address));
        }
        found.Sort(static (x, y) => HeapSeekCache.CompareKeys(x.Key, y.Key) is var c and not 0 ? c : x.Address.CompareTo(y.Address));
        var merged = new List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)>(anchors.Count + found.Count);
        var next = 0;
        foreach (var (key, rids) in anchors)
        {
            List<(int Page, int Slot)> kept = [];
            if (key is { } at)
            {
                for (; next < found.Count && HeapSeekCache.CompareKeys(found[next].Key, at) <= 0; next++)
                {
                    if (HeapSeekCache.CompareKeys(found[next].Key, at) == 0)
                        kept.Add(found[next].Address);
                    else
                        merged.Add((found[next].Key, [found[next].Address]));
                }
            }
            foreach (var rid in rids)
            {
                if (!own.TryGet(heap, rid, out _))
                    kept.Add(rid);
            }
            if (kept.Count != 0)
                merged.Add((key, [.. kept]));
        }
        for (; next < found.Count; next++)
            merged.Add((found[next].Key, [found[next].Address]));
        return merged;
    }

    /// <summary>
    /// <paramref name="anchors"/> without the rows another request of the
    /// running statement's transaction wrote since it began: what a scan
    /// reading its own order finds arriving ahead of it, the rows it found
    /// keeping their places in that order.
    /// </summary>
    internal static List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> WithoutOwnWrites(HeapTable table, OwnWriteImages own, List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> anchors)
    {
        var heap = table.Heap;
        var kept = new List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)>(anchors.Count);
        foreach (var (key, rids) in anchors)
        {
            var left = Array.FindAll(rids, rid => !own.TryGet(heap, rid, out _));
            if (left.Length != 0)
                kept.Add((key, left));
        }
        return kept;
    }

    /// <summary>
    /// <see cref="WithoutOwnWrites(HeapTable, OwnWriteImages, List{ValueTuple{SqlValueKey?, ValueTuple{int, int}[]}})"/>
    /// for the running statement, <paramref name="anchors"/> unchanged when no
    /// such write reached <paramref name="table"/>.
    /// </summary>
    internal List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> WithoutOwnWrites(HeapTable table, List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)> anchors) =>
        this.OwnWritesOf(table) is { } own ? WithoutOwnWrites(table, own, anchors) : anchors;

    /// <summary>
    /// <paramref name="order"/> — addresses of <paramref name="table"/> in
    /// ascending order of the key over <paramref name="ordinals"/>, between the
    /// bounds — as the running statement found them (see
    /// <see cref="WithOwnWrites(HeapTable, OwnWriteImages, int[], SqlType[], List{ValueTuple{SqlValueKey?, ValueTuple{int, int}[]}}, SqlValueKey?, bool, SqlValueKey?, bool)"/>).
    /// </summary>
    internal static (int Page, int Slot)[] WithOwnWrites(
        HeapTable table, OwnWriteImages own, int[] ordinals, SqlType[] commons, (int Page, int Slot)[] order,
        SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive)
    {
        var heap = table.Heap;
        var anchors = new List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)>(order.Length);
        foreach (var address in order)
        {
            if (heap.ReadLiveRow(address.Page, address.Slot) is { } bytes && HeapSeekCache.TryComputeKey(bytes, ordinals, commons, table.StoredColumns, heap, out var key))
                anchors.Add((key, [address]));
        }
        var merged = WithOwnWrites(table, own, ordinals, commons, anchors, lower, lowerInclusive, upper, upperInclusive);
        var result = new List<(int Page, int Slot)>(merged.Count);
        foreach (var (_, rids) in merged)
            result.AddRange(rids);
        return [.. result];
    }

    /// <summary>
    /// The rows of <paramref name="table"/>'s heap the running statement found
    /// that another request of its transaction deleted since, past
    /// <paramref name="after"/> in address order: what a walk of the heap reads
    /// where it passes their addresses. Null when there are none.
    /// </summary>
    internal List<((int Page, int Slot) Address, byte[] Image)>? OwnDeletesPast(HeapTable table, (int Page, int Slot) after)
    {
        if (this.OwnWritesOf(table) is not { } own)
            return null;
        var heap = table.Heap;
        var deleted = own.Found(heap).FindAll(row => row.Address.CompareTo(after) > 0 && heap.ReadLiveRow(row.Address.Page, row.Address.Slot) is null);
        if (deleted.Count == 0)
            return null;
        deleted.Sort(static (x, y) => x.Address.CompareTo(y.Address));
        return deleted;
    }

    // Whether `key` lies between the bounds, an open side null.
    private static bool Within(SqlValueKey key, SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive)
    {
        if (lower is { } low && HeapSeekCache.CompareKeys(key, low) is var below && (below < 0 || (below == 0 && !lowerInclusive)))
            return false;
        return upper is not { } high || (HeapSeekCache.CompareKeys(key, high) is var above && (above < 0 || (above == 0 && upperInclusive)));
    }
}
