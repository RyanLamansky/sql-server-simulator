using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Cursor-side enumeration for updatable cursors (KEYSET / DYNAMIC and
/// positioned <c>WHERE CURRENT OF</c> DML). A cursor whose SELECT maps to a
/// single base table re-reads live rows here instead of snapshotting bytes
/// through <see cref="Execute"/>, so column changes (and, for DYNAMIC,
/// membership changes) made between <c>FETCH</c>es are visible — matching
/// SQL Server's sensitivity model. Each row carries its projected output
/// values, its ORDER BY key, the chosen unique-key tuple (when the table has
/// a PK or UNIQUE constraint — matches SQL Server's KEYSET identity), and the
/// row's stable <c>(page, slot)</c> address (always — used as the cursor
/// identity when no unique key exists and as the deterministic tiebreak for
/// the ORDER BY total order).
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// One row produced by <see cref="EnumerateForCursor"/>: the projected
    /// output values, the ORDER BY key, the optional unique-key tuple (null
    /// when the base table has no PK/UNIQUE — falling back to <see cref="Rid"/>
    /// for cursor identity), and the row's stable address.
    /// </summary>
    internal sealed class CursorRow(SqlValue[] values, SqlValue[] orderKey, SqlValue[]? uniqueKey, (int Page, int Slot) rid)
    {
        public readonly SqlValue[] Values = values;
        public readonly SqlValue[] OrderKey = orderKey;
        public readonly SqlValue[]? UniqueKey = uniqueKey;
        public readonly (int Page, int Slot) Rid = rid;
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
    /// has neither. KEYSET cursors track by these columns when present —
    /// matching SQL Server's "keyset is identified by the unique index"
    /// behavior (probe-confirmed: an UPDATE to a unique-key column makes the
    /// next fetch return <c>@@FETCH_STATUS = -2</c>). When null, the cursor
    /// falls back to the row's stable <c>(page, slot)</c> address — a
    /// simulator extension over real SQL Server's no-unique-key heap
    /// behavior, which is documented as undefined.
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
    /// the row's stable address as a final tiebreak for a total order).
    /// Re-invoked per KEYSET / DYNAMIC <c>FETCH</c> so the latest committed
    /// values and (for DYNAMIC) membership are observed. Only valid when
    /// <see cref="CursorBaseTable"/> is non-null.
    /// </summary>
    internal List<CursorRow> EnumerateForCursor(BatchContext batch)
    {
        var profile = this.UpdatabilityProfile!;
        var source = profile.Source;
        var table = source.BackingTable!;
        var keyOrdinals = CursorUniqueKeyOrdinals(table);
        var sources = new[] { source };
        var orderBy = this.CursorOrderBy ?? [];

        var rows = new List<CursorRow>();
        foreach (var (pageIndex, slotIndex, bytes) in table.Heap.EnumerateRowsWithAddress())
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

            SqlValue[]? uniqueKey = null;
            if (keyOrdinals is not null)
            {
                uniqueKey = new SqlValue[keyOrdinals.Length];
                for (var i = 0; i < keyOrdinals.Length; i++)
                    uniqueKey[i] = RowDecoder.DecodeColumn(table.StoredColumns, bytes, keyOrdinals[i], table.Heap);
            }

            rows.Add(new CursorRow(values, orderKey, uniqueKey, (pageIndex, slotIndex)));
        }

        rows.Sort(this.CompareCursorRows);
        return rows;
    }

    /// <summary>
    /// Total-order comparison between two cursor rows: ORDER BY key first (per
    /// the SELECT's ASC/DESC flags), then the row's stable address ascending
    /// as a deterministic tiebreak (rids are guaranteed unique across the
    /// heap). Drives both the stable sort in <see cref="EnumerateForCursor"/>
    /// and DYNAMIC next/prior navigation.
    /// </summary>
    internal int CompareCursorRows(CursorRow a, CursorRow b)
    {
        var orderBy = this.CursorOrderBy ?? [];
        var c = orderBy.Count == 0 ? 0 : CompareOrderKeys(a.OrderKey, b.OrderKey, orderBy);
        if (c != 0)
            return c;
        c = a.Rid.Page.CompareTo(b.Rid.Page);
        return c != 0 ? c : a.Rid.Slot.CompareTo(b.Rid.Slot);
    }

    /// <summary>
    /// Ascending lexicographic compare of two key tuples (NULL smallest,
    /// cross-type promoted). Used by the keyset's identity-match step when
    /// the base table has a unique key.
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
