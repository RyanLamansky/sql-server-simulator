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
    public static List<(int Page, int Slot)>? Order(HeapTable table)
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
        return HeapSeekCache.For(table.Heap).KeyOrderUnlessHeapOrdered(table.Heap, schema, table.Heap, ordinals, commons);
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
    public static IEnumerable<byte[]> Rows(HeapTable table)
    {
        if (Order(table) is not { } order)
        {
            foreach (var bytes in table.Rows)
                yield return bytes;
            yield break;
        }
        var heap = table.Heap;
        var seen = new HashSet<(int, int)>();
        foreach (var address in order)
        {
            if (seen.Add(address) && !heap.IsSlotTombstoned(address.Page, address.Slot) && heap.ReadSlotBytes(address.Page, address.Slot) is { } bytes)
                yield return bytes;
        }
    }

    /// <summary>
    /// <paramref name="table"/>'s rows with their addresses in scan order, for
    /// an UPDATE / DELETE target walk. Lazy, as <see cref="Rows"/> is.
    /// </summary>
    public static IEnumerable<(int PageIndex, int SlotIndex, byte[] Bytes)> RowsWithAddress(HeapTable table)
    {
        if (Order(table) is not { } order)
        {
            foreach (var row in table.Heap.EnumerateRowsWithAddress())
                yield return row;
            yield break;
        }
        var heap = table.Heap;
        var seen = new HashSet<(int, int)>();
        foreach (var (page, slot) in order)
        {
            if (seen.Add((page, slot)) && !heap.IsSlotTombstoned(page, slot) && heap.ReadSlotBytes(page, slot) is { } bytes)
                yield return (page, slot, bytes);
        }
    }

    // The clustered key's storage ordinals and column directions, or null for
    // a heap (or a disabled / filtered clustered index, which leaves one).
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
            if (index.IsDisabled || index.Filter is not null)
                return null;
            var ordinals = new int[index.KeyColumns.Length];
            var descending = new bool[ordinals.Length];
            for (var i = 0; i < ordinals.Length; i++)
            {
                ordinals[i] = index.KeyColumns[i].StorageOrdinal;
                descending[i] = index.KeyColumns[i].IsDescending;
            }
            return (ordinals, descending);
        }
        return null;
    }
}
