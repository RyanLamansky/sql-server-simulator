using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Cursor-side enumeration for updatable cursors (KEYSET / DYNAMIC and
/// positioned <c>WHERE CURRENT OF</c> DML). A cursor whose SELECT maps to a
/// single base table with a unique key re-reads live rows here instead of
/// snapshotting bytes through <see cref="Execute"/>, so column changes (and,
/// for DYNAMIC, membership changes) made between <c>FETCH</c>es are visible —
/// matching SQL Server's sensitivity model. Rows carry their projected output
/// values, their ORDER BY key, and their unique-key tuple (the stable identity
/// the cursor tracks instead of a physical RID, since the simulator's UPDATE
/// relocates rows).
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// One row produced by <see cref="EnumerateForCursor"/>: the projected
    /// output values, the ORDER BY key (empty when the cursor's SELECT has no
    /// ORDER BY), and the base table's unique-key column values used as the
    /// cursor's stable row identity.
    /// </summary>
    internal sealed class CursorRow(SqlValue[] values, SqlValue[] orderKey, SqlValue[] uniqueKey)
    {
        public readonly SqlValue[] Values = values;
        public readonly SqlValue[] OrderKey = orderKey;
        public readonly SqlValue[] UniqueKey = uniqueKey;
    }

    /// <summary>
    /// The single base <see cref="HeapTable"/> this cursor's SELECT reads, or
    /// null when the shape isn't updatable for cursor purposes (multi-source,
    /// aggregate / DISTINCT / set-op, derived/view source, or TOP/OFFSET/FETCH
    /// — all of which SQL Server forces to a STATIC cursor).
    /// </summary>
    internal HeapTable? CursorBaseTable =>
        this.UpdatabilityProfile is { Source.BackingTable: { } table } && !this.HasTopOrOffsetOrFetch
            ? table
            : null;

    /// <summary>
    /// Storage ordinals of the base table's chosen unique key (PRIMARY KEY
    /// preferred, else the first UNIQUE constraint), or null when the table
    /// has neither. KEYSET / DYNAMIC cursors and positioned DML require a
    /// unique key; without one SQL Server converts the cursor to STATIC.
    /// </summary>
    internal static int[]? CursorUniqueKeyOrdinals(HeapTable table)
    {
        KeyConstraint? chosen = null;
        foreach (var key in table.KeyConstraints)
        {
            if (key.Kind == KeyConstraintKind.PrimaryKey)
                return key.StorageOrdinals;
            chosen ??= key;
        }
        return chosen?.StorageOrdinals;
    }

    /// <summary>
    /// Enumerates the cursor's source rows live from the base heap, applying
    /// the SELECT's WHERE and projection and ordering by its ORDER BY (with
    /// the unique key as a final tiebreak for a total order). Re-invoked per
    /// KEYSET / DYNAMIC <c>FETCH</c> so the latest committed values and (for
    /// DYNAMIC) membership are observed. Only valid when
    /// <see cref="CursorBaseTable"/> is non-null and the table has a unique
    /// key.
    /// </summary>
    internal List<CursorRow> EnumerateForCursor(BatchContext batch)
    {
        var profile = this.UpdatabilityProfile!;
        var source = profile.Source;
        var table = source.BackingTable!;
        var keyOrdinals = CursorUniqueKeyOrdinals(table)!;
        var sources = new[] { source };
        var orderBy = this.CursorOrderBy ?? [];

        var rows = new List<CursorRow>();
        foreach (var (_, _, bytes) in table.Heap.EnumerateRowsWithAddress())
        {
            var tuple = new byte[]?[] { bytes };
            SqlValue Resolve(MultiPartName name) => ResolveAcrossTuple(sources, tuple, name, batch, null, Resolve);

            var keep = true;
            foreach (var excluder in profile.Excluders)
            {
                if (excluder.Run(new RuntimeContext(Resolve, batch)) != true)
                {
                    keep = false;
                    break;
                }
            }
            if (!keep)
                continue;

            var values = new SqlValue[profile.Projections.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = profile.Projections[i].Run(new RuntimeContext(Resolve, batch));

            var orderKey = orderBy.Count == 0
                ? []
                : ComputeOrderKeys(orderBy, values, this.ColumnNames, distinct: false, batch, Resolve);

            var uniqueKey = new SqlValue[keyOrdinals.Length];
            for (var i = 0; i < keyOrdinals.Length; i++)
                uniqueKey[i] = RowDecoder.DecodeColumn(table.StoredColumns, bytes, keyOrdinals[i], table.Heap);

            rows.Add(new CursorRow(values, orderKey, uniqueKey));
        }

        rows.Sort(this.CompareCursorRows);
        return rows;
    }

    /// <summary>
    /// Total-order comparison between two cursor rows: ORDER BY key first (per
    /// the SELECT's ASC/DESC flags), then the unique key ascending as a
    /// deterministic tiebreak. Drives both the stable sort in
    /// <see cref="EnumerateForCursor"/> and DYNAMIC next/prior navigation.
    /// </summary>
    internal int CompareCursorRows(CursorRow a, CursorRow b)
    {
        var orderBy = this.CursorOrderBy ?? [];
        var c = orderBy.Count == 0 ? 0 : CompareOrderKeys(a.OrderKey, b.OrderKey, orderBy);
        return c != 0 ? c : CompareKeyTuples(a.UniqueKey, b.UniqueKey);
    }

    /// <summary>
    /// Ascending lexicographic compare of two key tuples (NULL smallest,
    /// cross-type promoted) — the unique-key tiebreak and the keyset identity
    /// match.
    /// </summary>
    internal static int CompareKeyTuples(SqlValue[] a, SqlValue[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var lk = a[i];
            var rk = b[i];
            int c;
            if (lk.IsNull && rk.IsNull)
            {
                c = 0;
            }
            else if (lk.IsNull)
            {
                c = -1;
            }
            else if (rk.IsNull)
            {
                c = 1;
            }
            else if (lk.Type == rk.Type)
            {
                c = lk.CompareTo(rk);
            }
            else
            {
                var common = SqlType.Promote(lk.Type, rk.Type);
                c = lk.CoerceTo(common).CompareTo(rk.CoerceTo(common));
            }
            if (c != 0)
                return c;
        }
        return 0;
    }
}
