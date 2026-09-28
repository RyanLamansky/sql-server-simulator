using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// Whether a query's sort, grouping or DISTINCT lists a work table under SET
// STATISTICS IO. Real lists one for a Sort or a Hash Match; it needs neither
// where a single table's key or index already delivers the rows in the order
// asked, or already distinct (probed 2026-09-28 against SQL Server 2025) —
// which is what these tests approximate, the simulator's own sort and hash
// running either way.
internal sealed partial class Selection
{
    /// <summary>
    /// Records the work table a full sort by <paramref name="orderBy"/> lists,
    /// unless the one table read is kept in that order by a key or index.
    /// </summary>
    private static void NoteSortWorktable(BatchContext batch, FromSource[] sources, List<OrderBySpec> orderBy, List<Expression> expressions)
    {
        if (batch.Connection.StatementIo is not { } io || orderBy.Count == 0)
            return;
        if (SingleTable(sources) is { } table)
        {
            var ordinals = new int[orderBy.Count];
            var descending = new bool[orderBy.Count];
            var resolved = true;
            for (var i = 0; i < orderBy.Count; i++)
            {
                var term = orderBy[i].IsOrdinal ? expressions[orderBy[i].Ordinal - 1] : orderBy[i].Expr!;
                if (StorageOrdinalOf(sources[0], table, term) is not { } ordinal)
                {
                    resolved = false;
                    break;
                }
                ordinals[i] = ordinal;
                descending[i] = orderBy[i].Descending;
            }
            if (resolved && KeyOrders(table, ordinals, descending))
                return;
        }
        io.UseWorktable();
    }

    /// <summary>
    /// Records the work table a hash aggregate over <paramref name="grouping"/>
    /// lists, unless the one table read delivers the groups in order (a key
    /// or index leads with the grouping columns) or already distinct (they
    /// hold a whole unique key).
    /// </summary>
    private static void NoteGroupingWorktable(BatchContext batch, FromSource[] sources, IReadOnlyList<Expression> grouping)
    {
        if (batch.Connection.StatementIo is not { } io || grouping.Count == 0)
            return;
        if (SingleTable(sources) is { } table)
        {
            var columns = new HashSet<int>();
            foreach (var term in grouping)
            {
                if (StorageOrdinalOf(sources[0], table, term) is not { } ordinal)
                {
                    io.UseWorktable();
                    return;
                }
                _ = columns.Add(ordinal);
            }
            if (KeyLeadsWith(table, columns) || HoldsUniqueKey(table, columns))
                return;
        }
        io.UseWorktable();
    }

    private static HeapTable? SingleTable(FromSource[] sources) =>
        sources is [{ BackingTable: { } table, LateralPlan: null }] ? table : null;

    private static int? StorageOrdinalOf(FromSource source, HeapTable table, Expression term)
    {
        while (term is Expressions.Parenthesized parenthesized)
            term = parenthesized.Wrapped;
        if (term is not Expressions.Reference reference)
            return null;
        var name = reference.ReferencedName;
        if (name.ImmediateQualifier is { } qualifier && !string.Equals(qualifier, source.Qualifier, StringComparison.OrdinalIgnoreCase))
            return null;
        for (var i = 0; i < source.ColumnNames.Length; i++)
        {
            if (string.Equals(source.ColumnNames[i], name.Leaf, StringComparison.OrdinalIgnoreCase))
                return table.StorageOrdinals[i] is >= 0 and var ordinal ? ordinal : null;
        }
        return null;
    }

    // Whether some key or index reads in the order asked, forward or backward.
    private static bool KeyOrders(HeapTable table, int[] ordinals, bool[] descending)
    {
        foreach (var (keyOrdinals, keyDescending) in Keys(table))
        {
            if (keyOrdinals.Length < ordinals.Length)
                continue;
            bool forward = true, backward = true;
            for (var i = 0; i < ordinals.Length; i++)
            {
                if (keyOrdinals[i] != ordinals[i])
                {
                    forward = backward = false;
                    break;
                }
                forward &= keyDescending[i] == descending[i];
                backward &= keyDescending[i] != descending[i];
            }
            if (forward || backward)
                return true;
        }
        return false;
    }

    private static bool KeyLeadsWith(HeapTable table, HashSet<int> columns)
    {
        foreach (var (keyOrdinals, _) in Keys(table))
        {
            if (keyOrdinals.Length < columns.Count)
                continue;
            var leads = true;
            for (var i = 0; i < columns.Count && leads; i++)
                leads = columns.Contains(keyOrdinals[i]);
            if (leads)
                return true;
        }
        return false;
    }

    private static bool HoldsUniqueKey(HeapTable table, HashSet<int> columns)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (Array.TrueForAll(key.StorageOrdinals, columns.Contains))
                return true;
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsUnique && index.Filter is null && Array.TrueForAll(index.KeyColumns, column => columns.Contains(column.StorageOrdinal)))
                return true;
        }
        return false;
    }

    private static IEnumerable<(int[] Ordinals, bool[] Descending)> Keys(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
        {
            var descending = new bool[key.StorageOrdinals.Length];
            for (var i = 0; i < descending.Length; i++)
                descending[i] = key.IsDescending(i);
            yield return (key.StorageOrdinals, descending);
        }
        foreach (var index in table.Indexes)
        {
            if (index.Filter is not null || index.IsColumnstore || index.IsDisabled)
                continue;
            var ordinals = new int[index.KeyColumns.Length];
            var descending = new bool[ordinals.Length];
            for (var i = 0; i < ordinals.Length; i++)
            {
                ordinals[i] = index.KeyColumns[i].StorageOrdinal;
                descending[i] = index.KeyColumns[i].IsDescending;
            }
            yield return (ordinals, descending);
        }
    }
}
