using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// The row order a query without ORDER BY returns where real's plan sorts on its
// own: a window's Sequence Project, a sort-fed Stream Aggregate or Distinct Sort,
// and a set operation's dedup. See docs/claude/query.md#row-order-without-order-by
// for the probes each rule rests on and where real's order stops being predictable.
partial class Selection
{
    /// <summary>
    /// The most groups, distinct rows or set-operation rows the implicit order
    /// sorts. Real hashes a large input, whose bucket order nothing reproduces,
    /// so past this count arrival order stands and costs nothing; below it the
    /// sort is cheap next to the read that produced the rows.
    /// </summary>
    private const int ImplicitOrderSortCap = 4096;

    /// <summary>
    /// The key positions, and each one's direction, a sort-based grouping or
    /// DISTINCT over <paramref name="keys"/> leaves its output in, or null where
    /// arrival order already is real's.
    /// <list type="bullet">
    /// <item><description>Over one base table whose keys are all its bare columns,
    /// a set of them that covers a unique key is not aggregated at all, so the
    /// scan's own order stands (null); and a set equal to an index's leading
    /// columns — the clustered one first — is read in that index's order.</description></item>
    /// <item><description>Otherwise real sorts in the written order, except that a
    /// GROUP BY of exactly two keys that computes an aggregate sorts them the
    /// other way round (probed 2026-09-29 against SQL Server 2025:
    /// <c>SELECT COUNT(*) … GROUP BY a, b</c> sorts by <c>b, a</c>, while three,
    /// four and five keys, a GROUP BY with no aggregate — which real runs as a
    /// DISTINCT — and every DISTINCT keep the written order).</description></item>
    /// </list>
    /// </summary>
    private static (int[] Positions, bool[] Descending)? ImplicitKeyOrder(FromSource[] sources, JoinSpec[] joins, Expression[] keys, bool aggregates)
    {
        var count = keys.Length;
        if (count == 0)
            return null;

        if (sources.Length == 1 && joins.Length == 0 && sources[0] is { BackingTable: { } table, LateralPlan: null } source)
        {
            var ordinals = new int[count];
            var allColumns = true;
            for (var i = 0; i < count && allColumns; i++)
                allColumns = TryIdentifyIndexableColumn(source, Peel(keys[i]), out ordinals[i]);

            if (allColumns)
            {
                var set = new HashSet<int>(ordinals);
                foreach (var key in table.KeyConstraints)
                {
                    if (set.IsSupersetOf(key.StorageOrdinals))
                        return null;
                }
                foreach (var index in table.Indexes)
                {
                    if (index is { IsUnique: true, Filter: null, IsDisabled: false, IsColumnstore: false } && set.IsSupersetOf(index.KeyStorageOrdinals))
                        return null;
                }

                if (IndexOrderOver(table, set, ordinals) is { } indexOrder)
                    return indexOrder;
            }
        }

        var positions = new int[count];
        for (var i = 0; i < count; i++)
            positions[i] = aggregates && count == 2 ? count - 1 - i : i;
        return (positions, new bool[count]);
    }

    /// <summary>
    /// The first index whose leading key columns are exactly <paramref name="set"/>
    /// — the clustered one ahead of the rest — as positions into
    /// <paramref name="ordinals"/> in that index's key order, with its column
    /// directions; null when no index leads with the set.
    /// </summary>
    private static (int[] Positions, bool[] Descending)? IndexOrderOver(HeapTable table, HashSet<int> set, int[] ordinals)
    {
        (int[] Positions, bool[] Descending)? Match(int[] keyOrdinals, Func<int, bool> descending)
        {
            if (keyOrdinals.Length < set.Count)
                return null;
            var positions = new int[set.Count];
            var directions = new bool[set.Count];
            for (var i = 0; i < set.Count; i++)
            {
                if (!set.Contains(keyOrdinals[i]))
                    return null;
                positions[i] = Array.IndexOf(ordinals, keyOrdinals[i]);
                directions[i] = descending(i);
            }
            return (positions, directions);
        }

        for (var pass = 0; pass < 2; pass++)
        {
            var clustered = pass == 0;
            foreach (var key in table.KeyConstraints)
            {
                if (key.IsClustered == clustered && Match(key.StorageOrdinals, key.IsDescending) is { } found)
                    return found;
            }
            foreach (var index in table.Indexes)
            {
                if (index.IsClustered == clustered && index is { Filter: null, IsDisabled: false, IsColumnstore: false }
                    && Match(index.KeyStorageOrdinals, i => index.KeyColumns[i].IsDescending) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="sets"/> is a ROLLUP's chain — each set the one
    /// before it less its last key, the first carrying every key — which real
    /// computes as one Stream Aggregate over the first set's sort, emitting each
    /// subtotal after the groups it totals and the grand total last (probed
    /// 2026-09-29 against SQL Server 2025, for <c>ROLLUP(a, b)</c>,
    /// <c>a, ROLLUP(b)</c> and the equivalent <c>GROUPING SETS</c> alike).
    /// </summary>
    private static bool IsRollupChain(FromSource[] sources, List<Expression[]> sets)
    {
        if (sets.Count < 2)
            return false;
        var full = sets[0];
        for (var s = 1; s < sets.Count; s++)
        {
            var set = sets[s];
            if (set.Length != full.Length - s)
                return false;
            for (var i = 0; i < set.Length; i++)
            {
                if (!ReferenceEquals(set[i], full[i]) && !GroupingKey(sources, Peel(set[i])).Equals(GroupingKey(sources, Peel(full[i]))))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Compares two groups' key tuples at <paramref name="positions"/>. A tuple
    /// shorter than a position — a ROLLUP subtotal that grouped that key away —
    /// sorts after every tuple that kept it.
    /// </summary>
    private static int CompareGroupKeys(SqlValue[] a, SqlValue[] b, int[] positions, bool[] descending)
    {
        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            int c;
            if (p < a.Length && p < b.Length)
            {
                c = CompareSortValues(a[p], b[p]);
                if (descending[i])
                    c = -c;
            }
            else
            {
                c = (p < a.Length ? 0 : 1) - (p < b.Length ? 0 : 1);
            }
            if (c != 0)
                return c;
        }
        return 0;
    }

    /// <summary>
    /// Sorts distinct projected rows into the order real's Distinct Sort leaves
    /// them in, when they are few enough to be worth it (see
    /// <see cref="ImplicitOrderSortCap"/>) and arrival order isn't already
    /// real's.
    /// </summary>
    private static void SortDistinctRows<T>(List<T> rows, Func<T, SqlValue[]> projected, FromSource[] sources, JoinSpec[] joins, List<Expression> expressions)
    {
        if (rows.Count < 2 || rows.Count > ImplicitOrderSortCap
            || ImplicitKeyOrder(sources, joins, [.. expressions], aggregates: false) is not var (positions, descending))
        {
            return;
        }
        rows.Sort((a, b) => CompareGroupKeys(projected(a), projected(b), positions, descending));
    }

    /// <summary>
    /// One sort a window evaluation pass puts its rows in: the partition keys
    /// (any order of them partitions alike) followed by the ordering keys.
    /// Each entry names where its per-row value lives in the window key buffer.
    /// </summary>
    private readonly struct WindowSortEntry(ShapeKey key, int window, int slot, bool isPartition, bool descending)
    {
        public readonly ShapeKey Key = key;
        public readonly int Window = window;
        public readonly int Slot = slot;
        public readonly bool IsPartition = isPartition;
        public readonly bool Descending = descending;
    }

    /// <summary>
    /// The order a windowed query without ORDER BY emits its rows in, as a
    /// permutation of the row indices — or null where arrival order stands.
    /// <para>
    /// Real evaluates windows in written order, one Sort + Segment per distinct
    /// requirement: a window whose partition-then-order keys an earlier sort
    /// already satisfies joins that sort, one whose own sort satisfies every
    /// window an earlier sort serves replaces it, and any other starts a new
    /// sort. An <c>OVER ()</c> sorts nothing. The rows leave in the last
    /// sort's order (probed 2026-09-29 against SQL Server 2025:
    /// <c>ROW_NUMBER() OVER (ORDER BY x), RANK() OVER (ORDER BY s DESC),
    /// SUM(x) OVER (ORDER BY x)</c> sorts by <c>x</c>, then by <c>s DESC</c>,
    /// and returns rows by <c>s DESC</c>).
    /// </para>
    /// <para>
    /// Real's sort isn't stable, so its order among rows the last sort ties is
    /// its own; here ties fall back to the earlier sorts and then to arrival,
    /// which is what an index already supplying the order gives on real too.
    /// </para>
    /// </summary>
    private static int[]? WindowEmitOrder(FromSource[] sources, List<WindowExpression> windows, List<(SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[]> perWindowKeys, int rowCount)
    {
        if (rowCount < 2)
            return null;

        var sorts = new List<(List<WindowSortEntry> Sort, List<List<WindowSortEntry>> Served)>();
        for (var w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            var own = new List<WindowSortEntry>(window.PartitionBy.Length + window.OrderBy.Length);
            for (var p = 0; p < window.PartitionBy.Length; p++)
            {
                var key = GroupingKey(sources, Peel(window.PartitionBy[p]));
                if (!own.Exists(entry => entry.Key.Equals(key)))
                    own.Add(new(key, w, p, isPartition: true, descending: false));
            }
            for (var o = 0; o < window.OrderBy.Length; o++)
            {
                if (window.OrderBy[o].Expr is { } expr)
                    own.Add(new(GroupingKey(sources, Peel(expr)), w, o, isPartition: false, window.OrderBy[o].Descending));
            }
            if (own.Count == 0)
                continue;

            var placed = false;
            for (var s = 0; s < sorts.Count && !placed; s++)
            {
                var (sort, served) = sorts[s];
                if (Satisfies(sort, own))
                {
                    served.Add(own);
                    placed = true;
                }
                else if (served.TrueForAll(requirement => Satisfies(own, requirement)))
                {
                    served.Add(own);
                    sorts[s] = (own, served);
                    placed = true;
                }
            }
            if (!placed)
                sorts.Add((own, [own]));
        }

        if (sorts.Count == 0)
            return null;

        SqlValue ValueAt(int row, WindowSortEntry entry)
        {
            var (partitionKeys, orderKeys) = perWindowKeys[row][entry.Window];
            return entry.IsPartition ? partitionKeys[entry.Slot] : orderKeys[entry.Slot];
        }

        int Compare(int a, int b)
        {
            for (var s = sorts.Count - 1; s >= 0; s--)
            {
                foreach (var entry in sorts[s].Sort)
                {
                    var c = CompareSortValues(ValueAt(a, entry), ValueAt(b, entry));
                    if (c != 0)
                        return entry.Descending ? -c : c;
                }
            }
            return a.CompareTo(b);
        }

        // An index or an earlier operator often hands the rows over already in
        // this order; checking costs one pass where the sort would cost log n.
        var ordered = true;
        for (var i = 1; i < rowCount && ordered; i++)
            ordered = Compare(i - 1, i) < 0;
        if (ordered)
            return null;

        var permutation = new int[rowCount];
        for (var i = 0; i < rowCount; i++)
            permutation[i] = i;
        Array.Sort(permutation, Compare);
        return permutation;

        // Whether rows in `sort`'s order are in `requirement`'s: its partition
        // keys, in any order, lead, and its ordering keys follow.
        static bool Satisfies(List<WindowSortEntry> sort, List<WindowSortEntry> requirement)
        {
            if (sort.Count < requirement.Count)
                return false;
            var partitionCount = requirement.FindIndex(static entry => !entry.IsPartition);
            if (partitionCount < 0)
                partitionCount = requirement.Count;
            for (var i = 0; i < requirement.Count; i++)
            {
                var entry = requirement[i];
                if (i < partitionCount)
                {
                    var key = sort[i].Key;
                    if (!requirement.Exists(candidate => candidate.IsPartition && candidate.Key.Equals(key)))
                        return false;
                }
                else if (!sort[i].Key.Equals(entry.Key) || sort[i].Descending != entry.Descending)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
