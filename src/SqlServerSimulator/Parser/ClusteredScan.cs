using System.Runtime.CompilerServices;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The order a scan of a table reads its rows in. Real stores a table with a
/// clustered index in that index's key order and scans it so, so a query with
/// no ORDER BY — and the rows a <c>TOP</c> without one keeps — follows the
/// clustered key rather than the order the rows were written in (probed
/// 2026-09-26 against SQL Server 2025). The heap here keeps write order, so a
/// scan of a clustered table walks the key's ordered view instead; a heap
/// (no clustered index) keeps its own order, as real's allocation-order scan
/// does.
/// </summary>
/// <remarks>
/// A key whose columns are all NOT NULL and ascending — every default PRIMARY
/// KEY — walks the seek cache's ordered view, which holds no NULL keys; any
/// other (a nullable or a descending column) sorts every row's key once per
/// heap generation, NULLs first under an ascending column and last under a
/// descending one, as real's key order puts them.
/// </remarks>
internal static class ClusteredScan
{
    /// <summary>The sorted order of a heap under a clustered key the ordered view can't serve, as of one generation.</summary>
    private sealed class SortedOrder
    {
        public long Generation = -1;
        public int[] Ordinals = [];
        public bool[] Descending = [];
        public List<(int Page, int Slot)>? Order;
    }

    private static readonly ConditionalWeakTable<Heap, SortedOrder> sortedOrders = [];

    /// <summary>
    /// The live row addresses of <paramref name="table"/> in clustered-key
    /// order, or <see langword="null"/> when a scan keeps the heap's order —
    /// no clustered key to follow, or a heap already in its order (a key
    /// assigned in insertion order), which the seek cache tracks so the common
    /// case keeps the sequential scan. The list can name a tombstoned or
    /// repeated address, which the caller skips as
    /// <c>MaterializeWithLockChecks</c> does.
    /// </summary>
    /// <remarks>
    /// <paramref name="keys"/>, when given, receives the key of each address at
    /// the same index where the seek cache's ordered view serves the order, and
    /// stays empty where the order is sorted here. With <paramref name="keyed"/>
    /// the order comes back from the ordered view even for a heap already in
    /// key order, for a locking scan that has to follow a key another session
    /// deletes and reinserts beside it.
    /// </remarks>
    public static List<(int Page, int Slot)>? Order(HeapTable table, List<SqlValueKey>? keys = null, bool keyed = false)
    {
        if (ClusteredKey(table) is not var (ordinals, descending))
            return null;
        var schema = table.StoredColumns;
        var commons = new SqlType[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
        {
            if (schema[ordinals[i]].Nullable || descending[i])
                return SortedKeyOrder(table, ordinals, descending);
            commons[i] = schema[ordinals[i]].Type;
        }
        return HeapSeekCache.For(table.Heap).KeyOrderUnlessHeapOrdered(table.Heap, schema, table.Heap, ordinals, commons, keys, keyed);
    }

    private static List<(int Page, int Slot)>? SortedKeyOrder(HeapTable table, int[] ordinals, bool[] descending)
    {
        var heap = table.Heap;
        var cached = sortedOrders.GetOrCreateValue(heap);
        lock (cached)
        {
            // Read before the walk, so a write landing during it leaves the
            // entry a generation behind and the next scan sorts again.
            // The key is checked too: creating or dropping an index changes it
            // without touching a row.
            var generation = heap.MutationGeneration;
            if (cached.Generation == generation && cached.Ordinals.AsSpan().SequenceEqual(ordinals) && cached.Descending.AsSpan().SequenceEqual(descending))
                return cached.Order;

            var schema = table.StoredColumns;
            var rows = new List<(SqlValue[] Key, int Page, int Slot)>();
            foreach (var (page, slot, bytes) in heap.EnumerateRowsWithAddress())
            {
                var key = new SqlValue[ordinals.Length];
                for (var i = 0; i < ordinals.Length; i++)
                    key[i] = RowDecoder.DecodeColumn(schema, bytes, ordinals[i], heap);
                rows.Add((key, page, slot));
            }

            // OrderBy is stable, so rows with equal keys keep their heap order.
            var sorted = rows.OrderBy(static row => row.Key, new KeyComparer(descending)).ToList();
            var inHeapOrder = true;
            for (var i = 0; i < sorted.Count && inHeapOrder; i++)
                inHeapOrder = sorted[i].Page == rows[i].Page && sorted[i].Slot == rows[i].Slot;
            cached.Order = inHeapOrder ? null : sorted.ConvertAll(static row => (row.Page, row.Slot));
            cached.Generation = generation;
            cached.Ordinals = ordinals;
            cached.Descending = descending;
            return cached.Order;
        }
    }

    // NULL sorts below every value, so it leads an ascending column and
    // trails a descending one.
    private sealed class KeyComparer(bool[] descending) : IComparer<SqlValue[]>
    {
        public int Compare(SqlValue[]? x, SqlValue[]? y)
        {
            for (var i = 0; i < x!.Length; i++)
            {
                var c = (x[i].IsNull, y![i].IsNull) switch
                {
                    (true, true) => 0,
                    (true, false) => -1,
                    (false, true) => 1,
                    _ => x[i].CompareTo(y[i]),
                };
                if (c != 0)
                    return descending[i] ? -c : c;
            }
            return 0;
        }
    }

    /// <summary>
    /// <paramref name="table"/>'s rows in scan order, for a read that takes no
    /// per-row lock (a <c>NOLOCK</c> read, a table variable). Lazy: the order
    /// is taken when an enumeration starts, since a parsed source is
    /// enumerated again by every execution of a cached plan.
    /// </summary>
    /// <remarks>
    /// <paramref name="io"/>, when the executing statement is gathering
    /// <c>STATISTICS IO</c>, counts the scan and the pages it enters, and
    /// <paramref name="addresses"/>, when it reads a row locator, records each
    /// row's address.
    /// <para>
    /// For a statement of <paramref name="batch"/>, when given, that can wait
    /// on its client mid-result: once it has waited while the table changed, a
    /// scan in key order reads on from the key it stopped at as the table
    /// stands then, as the locking scan does (<c>BatchContext.ScanInKeyOrder</c>),
    /// and a row another request of its transaction wrote since it began reads
    /// as the statement found it (probed 2026-10-09 against SQL Server 2025: a
    /// <c>NOLOCK</c> reader suspended mid-result read another session's
    /// update and insert ahead of it, and none of another request's writes in
    /// its own transaction).
    /// </para>
    /// </remarks>
    public static IEnumerable<byte[]> Rows(HeapTable table, IoStatistics? io = null, RowAddressMap? addresses = null, BatchContext? batch = null)
    {
        if (batch is null || !batch.StreamsResultRows)
            return RowsAsOrdered(table, io, addresses);
        return RowsAsTheyStand(table, io, addresses, batch);
    }

    private static IEnumerable<byte[]> RowsAsTheyStand(HeapTable table, IoStatistics? io, RowAddressMap? addresses, BatchContext batch)
    {
        var counts = io?.Touch(table);
        _ = counts?.ScanCount += 1;
        var lastPage = -1;
        var heap = table.Heap;
        var statement = batch.CurrentStatement;
        var suspensions = statement.Suspensions;
        var generation = Volatile.Read(ref heap.MutationGeneration);
        var rowGroup = KeyLockGroup.RowGroupOf(table);
        // Keys come with the order where the ordered view serves it, which a
        // read on from the last key needs.
        var keys = rowGroup is null ? null : new List<SqlValueKey>();
        var order = Order(table, keys);
        if (keys is not null && (order is null || keys.Count != order.Count))
            keys = null;
        List<((int Page, int Slot) Address, byte[] Image)>? ownDeletes = null;
        var nextOwnDelete = 0;
        var lastAddress = (-1, -1);
        byte[]? lastRead = null;
        var seen = new HashSet<(int, int)>();
        var ownWrites = new BatchContext.OwnWriteView(batch, table);
        using var heapRows = order is null ? heap.EnumerateRowsWithAddress().GetEnumerator() : null;
        for (var position = 0; ; position++)
        {
            if (statement.Suspensions != suspensions)
            {
                suspensions = statement.Suspensions;
                if (Volatile.Read(ref heap.MutationGeneration) is var now && now != generation)
                {
                    generation = now;
                    var readKey = default(SqlValueKey);
                    if (rowGroup is not null && (order is null ? ServesKeyedOrder(table) : keys is not null)
                        && (lastRead is null || rowGroup.TryReadKey(lastRead, out readKey)))
                    {
                        var ahead = BatchContext.KeysArrivedBetween(table, rowGroup, lastRead is null ? null : readKey, null);
                        if (batch.OwnWritesOf(table) is { } own)
                            ahead = BatchContext.WithOwnWrites(table, own, rowGroup.Ordinals, rowGroup.Commons, ahead, lastRead is null ? null : readKey, false, null, false);
                        order = [];
                        keys = [];
                        foreach (var (key, rids) in ahead)
                        {
                            foreach (var rid in rids)
                            {
                                order.Add(rid);
                                keys.Add(key!.Value);
                            }
                        }
                        position = 0;
                    }
                }
                if (order is null)
                {
                    ownDeletes = batch.OwnDeletesPast(table, lastAddress);
                    nextOwnDelete = 0;
                }
            }
            (int Page, int Slot) address;
            byte[]? bytes;
            if (order is not null)
            {
                if (position >= order.Count)
                    yield break;
                address = order[position];
                if (!seen.Add(address))
                    continue;
                bytes = ownWrites.TryRead(batch, table, address, out var found) ? found : heap.ReadLiveRow(address.Page, address.Slot);
            }
            else
            {
                var more = heapRows!.MoveNext();
                var (page, slot, live) = more ? heapRows.Current : (int.MaxValue, int.MaxValue, null!);
                if (ownDeletes is not null)
                {
                    for (; nextOwnDelete < ownDeletes.Count && ownDeletes[nextOwnDelete].Address.CompareTo((page, slot)) < 0; nextOwnDelete++)
                    {
                        var (deleted, image) = ownDeletes[nextOwnDelete];
                        counts?.Enter(deleted.Page, ref lastPage);
                        addresses?.Record(image, deleted.Page, deleted.Slot);
                        lastRead = image;
                        yield return image;
                    }
                }
                if (!more)
                    yield break;
                address = lastAddress = (page, slot);
                bytes = ownWrites.TryRead(batch, table, address, out var found) ? found : live;
            }
            if (bytes is null)
                continue;
            counts?.Enter(address.Page, ref lastPage);
            addresses?.Record(bytes, address.Page, address.Slot);
            lastRead = bytes;
            yield return bytes;
        }
    }

    private static IEnumerable<byte[]> RowsAsOrdered(HeapTable table, IoStatistics? io, RowAddressMap? addresses)
    {
        var counts = io?.Touch(table);
        _ = counts?.ScanCount += 1;
        var lastPage = -1;
        if (Order(table) is not { } order)
        {
            if (counts is null && addresses is null)
            {
                foreach (var bytes in table.Rows)
                    yield return bytes;
                yield break;
            }
            foreach (var (page, slot, bytes) in table.Heap.EnumerateRowsWithAddress())
            {
                counts?.Enter(page, ref lastPage);
                addresses?.Record(bytes, page, slot);
                yield return bytes;
            }
            yield break;
        }
        var heap = table.Heap;
        var seen = new HashSet<(int, int)>();
        foreach (var address in order)
        {
            if (seen.Add(address) && heap.ReadLiveRow(address.Page, address.Slot) is { } bytes)
            {
                counts?.Enter(address.Page, ref lastPage);
                addresses?.Record(bytes, address.Page, address.Slot);
                yield return bytes;
            }
        }
    }

    /// <summary>
    /// <paramref name="table"/>'s rows with their addresses in scan order, for
    /// an UPDATE / DELETE target walk. Lazy, as <see cref="Rows"/> is.
    /// </summary>
    public static IEnumerable<(int PageIndex, int SlotIndex, byte[] Bytes)> RowsWithAddress(HeapTable table, IoStatistics? io = null)
    {
        var counts = io?.Touch(table);
        _ = counts?.ScanCount += 1;
        var lastPage = -1;
        if (Order(table) is not { } order)
        {
            foreach (var row in table.Heap.EnumerateRowsWithAddress())
            {
                counts?.Enter(row.PageIndex, ref lastPage);
                yield return row;
            }
            yield break;
        }
        var heap = table.Heap;
        var seen = new HashSet<(int, int)>();
        foreach (var (page, slot) in order)
        {
            if (seen.Add((page, slot)) && heap.ReadLiveRow(page, slot) is { } bytes)
            {
                counts?.Enter(page, ref lastPage);
                yield return (page, slot, bytes);
            }
        }
    }

    /// <summary>
    /// The clustered key's storage ordinals and column directions, and whether
    /// it is unique, or null for a heap (or a disabled / filtered / columnstore
    /// clustered index, which leaves one). A non-unique key is the one real
    /// completes with a uniquifier.
    /// </summary>
    public static (int[] Ordinals, bool[] Descending, bool Unique)? Key(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.IsClustered)
                return ClusteredKey(table) is var (ordinals, descending) ? (ordinals, descending, true) : null;
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsClustered)
                return ClusteredKey(table) is var (ordinals, descending) ? (ordinals, descending, index.IsUnique) : null;
        }
        return null;
    }

    /// <summary>
    /// Redraws the <see cref="Heap.Uniquifiers"/> entry of the row an UPDATE
    /// rewrites at <paramref name="address"/> when the statement assigns a
    /// column of <paramref name="table"/>'s non-unique clustered key —
    /// <paramref name="assignedColumns"/> are column ordinals — however the
    /// value comes out; real moves such a row as a delete and re-insert.
    /// </summary>
    public static void NoteKeyAssignment(HeapTable table, IReadOnlyList<int> assignedColumns, (int Page, int Slot) address, UndoLog? undoLog)
    {
        if (Key(table) is not ({ } ordinals, _, false))
            return;
        foreach (var column in assignedColumns)
        {
            if (Array.IndexOf(ordinals, table.StorageOrdinals[column]) >= 0)
            {
                table.Heap.Reuniquify(address, undoLog);
                return;
            }
        }
    }

    // The clustered key's storage ordinals and column directions, or null for
    // a heap (or a disabled / filtered clustered index, which leaves one).
    /// <summary>The storage ordinals of <paramref name="table"/>'s clustered key, or null for a scan that keeps the heap's order.</summary>
    public static int[]? KeyOrdinals(HeapTable table) => ClusteredKey(table)?.Ordinals;

    /// <summary>
    /// Whether <paramref name="table"/>'s clustered key order comes from the
    /// seek cache's ordered view, its keys in hand (see <see cref="Order"/>):
    /// every key column NOT NULL and ascending.
    /// </summary>
    public static bool ServesKeyedOrder(HeapTable table)
    {
        if (ClusteredKey(table) is not var (ordinals, descending))
            return false;
        var schema = table.StoredColumns;
        for (var i = 0; i < ordinals.Length; i++)
        {
            if (schema[ordinals[i]].Nullable || descending[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a scan of <paramref name="table"/> in its clustered key's order
    /// is already ordered by <paramref name="ordinals"/> ascending: they lead
    /// the key, each of those key columns ascending.
    /// </summary>
    public static bool OrdersAscendingBy(HeapTable table, int[] ordinals)
    {
        if (ClusteredKey(table) is not var (keyOrdinals, descending) || ordinals.Length > keyOrdinals.Length)
            return false;
        for (var i = 0; i < ordinals.Length; i++)
        {
            if (keyOrdinals[i] != ordinals[i] || descending[i])
                return false;
        }
        return true;
    }

    private static (int[] Ordinals, bool[] Descending)? ClusteredKey(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (!key.IsClustered)
                continue;
            if (key.IsDisabled)
                return null;
            var descending = new bool[key.StorageOrdinals.Length];
            for (var i = 0; i < descending.Length; i++)
                descending[i] = key.IsDescending(i);
            return (key.StorageOrdinals, descending);
        }
        foreach (var index in table.Indexes)
        {
            if (!index.IsClustered)
                continue;
            // A clustered columnstore index keeps no key order.
            if (index.IsDisabled || index.Filter is not null || index.IsColumnstore)
                return null;
            var ordinals = new int[index.KeyColumns.Length];
            var descending = new bool[ordinals.Length];
            for (var i = 0; i < ordinals.Length; i++)
            {
                ordinals[i] = index.KeyColumns[i].StorageOrdinal;
                descending[i] = index.KeyColumns[i].IsDescending;
                // A non-persisted computed key column has no stored value to
                // order by, so such a scan keeps the heap's order.
                if (ordinals[i] < 0)
                    return null;
            }
            return (ordinals, descending);
        }
        return null;
    }
}
