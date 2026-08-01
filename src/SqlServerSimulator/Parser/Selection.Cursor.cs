using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The FROM-clause shape an updatable cursor navigates: the participating
/// sources and their joins, the projection expressions, and the WHERE
/// excluders. Captured at parse time by <c>BuildSqlProjection</c> when every
/// source is a direct base-table scan — the only shape that carries the
/// stable <c>(page, slot)</c> address KEYSET membership, DYNAMIC navigation
/// and positioned <c>WHERE CURRENT OF</c> DML ride on. A single source is
/// just the one-slot case; a JOIN adds slots, and the cursor's identity
/// becomes the tuple of per-source addresses.
/// </summary>
internal sealed class CursorSourcePlan(
    FromSource[] sources,
    JoinSpec[] joins,
    HeapTable[] tables,
    Expression[] projections,
    BooleanExpression[] excluders)
{
    public readonly FromSource[] Sources = sources;

    /// <summary>The join between slot <c>i</c> and slot <c>i + 1</c>, so
    /// <c>Joins.Length == Sources.Length - 1</c>.</summary>
    public readonly JoinSpec[] Joins = joins;

    /// <summary>The base table behind each slot of <see cref="Sources"/>.</summary>
    public readonly HeapTable[] Tables = tables;

    public readonly Expression[] Projections = projections;
    public readonly BooleanExpression[] Excluders = excluders;
}

/// <summary>
/// Cursor-side enumeration for updatable cursors (KEYSET / DYNAMIC and
/// positioned <c>WHERE CURRENT OF</c> DML). A cursor whose SELECT reads only
/// base tables re-reads live rows here instead of snapshotting bytes through
/// <see cref="Execute"/>, so column changes (and, for DYNAMIC, membership
/// changes) made between <c>FETCH</c>es are visible — matching SQL Server's
/// sensitivity model. Each row carries its projected output values, its
/// ORDER BY key, the chosen unique-key tuple per source (when that source's
/// table has a PK or UNIQUE constraint — matches SQL Server's KEYSET
/// identity), and each source row's stable <c>(page, slot)</c> address
/// (always — used as the cursor identity when no unique key exists and as the
/// deterministic tiebreak for the ORDER BY total order).
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// One row produced by <see cref="EnumerateForCursor"/>: the projected
    /// output values, the ORDER BY key, and — one slot per FROM source — the
    /// optional unique-key tuple (null when that source's table has no
    /// PK/UNIQUE, falling back to <see cref="Rids"/> for cursor identity) and
    /// the source row's stable address (null on a NULL-extended outer-join
    /// side).
    /// </summary>
    internal sealed class CursorRow(SqlValue[] values, SqlValue[] orderKey, SqlValue[]?[] uniqueKeys, (int Page, int Slot)?[] rids)
    {
        public readonly SqlValue[] Values = values;
        public readonly SqlValue[] OrderKey = orderKey;
        public readonly SqlValue[]?[] UniqueKeys = uniqueKeys;
        public readonly (int Page, int Slot)?[] Rids = rids;
    }

    /// <summary>
    /// Storage ordinals of a base table's chosen unique key (PRIMARY KEY
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
    /// Enumerates the cursor's source rows live from the base heaps, folding
    /// the JOIN chain, applying the SELECT's WHERE and projection, and
    /// ordering by its ORDER BY (with the per-source stable addresses as a
    /// final tiebreak for a total order). Re-invoked per KEYSET / DYNAMIC
    /// <c>FETCH</c> so the latest committed values and (for DYNAMIC)
    /// membership are observed. Only valid when <see cref="CursorPlan"/> is
    /// non-null.
    /// </summary>
    internal List<CursorRow> EnumerateForCursor(BatchContext batch)
    {
        var plan = this.CursorPlan!;
        var sources = plan.Sources;
        var width = sources.Length;
        var orderBy = this.CursorOrderBy ?? [];

        // Live snapshot of each participating base heap: the row bytes plus
        // the stable address cursor identity rides on. Taken once per call so
        // the join fold can revisit the inner sides without re-walking pages.
        var scans = new List<((int Page, int Slot) Rid, byte[] Bytes)>[width];
        for (var i = 0; i < width; i++)
        {
            var scan = new List<((int, int), byte[])>();
            foreach (var (page, slot, bytes) in plan.Tables[i].Heap.EnumerateRowsWithAddress())
                scan.Add(((page, slot), bytes));
            scans[i] = scan;
        }

        // Hoisted per-row scaffolding: one mutable tuple, one cached
        // self-referencing resolver lambda (never a local function passed as
        // its own selfRecursive argument — that allocates a delegate per
        // resolution per row), one RuntimeContext. The join fold and the
        // projection pass below share all three.
        var current = new byte[]?[width];
        var memo = new SourceColumnMemo();
        Func<MultiPartName, SqlValue> resolve = null!;
        resolve = name => ResolveAcrossTuple(sources, current, name, batch, null, resolve, memo);
        var runtime = new RuntimeContext(resolve, batch);

        var tuples = FoldCursorTuples(plan, scans, current, runtime);

        var keyOrdinals = new int[]?[width];
        for (var i = 0; i < width; i++)
            keyOrdinals[i] = CursorUniqueKeyOrdinals(plan.Tables[i]);

        var rows = new List<CursorRow>(tuples.Count);
        foreach (var tuple in tuples)
        {
            var rids = new (int Page, int Slot)?[width];
            for (var i = 0; i < width; i++)
            {
                if (tuple[i] < 0)
                {
                    current[i] = null;
                    rids[i] = null;
                }
                else
                {
                    var (rid, bytes) = scans[i][tuple[i]];
                    current[i] = bytes;
                    rids[i] = rid;
                }
            }

            var keep = true;
            foreach (var excluder in plan.Excluders)
            {
                if (excluder.Run(runtime) != true)
                {
                    keep = false;
                    break;
                }
            }
            if (!keep)
                continue;

            var values = new SqlValue[plan.Projections.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = plan.Projections[i].Run(runtime);

            var orderKey = orderBy.Count == 0
                ? []
                : ComputeOrderKeys(orderBy, values, this.ColumnNames, projectionSources: null, distinct: false, batch, resolve);

            var uniqueKeys = new SqlValue[]?[width];
            for (var i = 0; i < width; i++)
            {
                if (keyOrdinals[i] is not { } ordinals || current[i] is not { } bytes)
                    continue;
                var table = plan.Tables[i];
                var key = new SqlValue[ordinals.Length];
                for (var k = 0; k < ordinals.Length; k++)
                    key[k] = RowDecoder.DecodeColumn(table.StoredColumns, bytes, ordinals[k], table.Heap);
                uniqueKeys[i] = key;
            }

            rows.Add(new CursorRow(values, orderKey, uniqueKeys, rids));
        }

        rows.Sort(this.CompareCursorRows);
        return rows;
    }

    /// <summary>
    /// Folds the cursor's JOIN chain into one row-index tuple per joined row:
    /// slot <c>i</c> holds the index into <c>scans[i]</c>, or <c>-1</c> for a
    /// NULL-extended outer-join side. A left-deep nested loop — the equi-join
    /// hash / seek strategies of the read path don't apply here because every
    /// intermediate row must keep its per-source address, and the cursor
    /// re-folds on every FETCH anyway.
    /// </summary>
    /// <remarks>
    /// The ON predicate evaluates through the shared <paramref name="current"/>
    /// tuple, whose slots past the level being joined are cleared so a
    /// forward reference reads as NULL rather than a stale left-sibling row.
    /// RIGHT / FULL track a matched bitmap across the whole left iteration and
    /// emit the unmatched right rows afterwards with every prior slot
    /// NULL-filled, matching <c>EnumerateJoinedRows</c>'s semantics.
    /// </remarks>
    private static List<int[]> FoldCursorTuples(
        CursorSourcePlan plan,
        List<((int Page, int Slot) Rid, byte[] Bytes)>[] scans,
        byte[]?[] current,
        RuntimeContext runtime)
    {
        var width = scans.Length;
        var accumulated = new List<int[]>(scans[0].Count);
        for (var r = 0; r < scans[0].Count; r++)
        {
            var seed = new int[width];
            Array.Fill(seed, -1);
            seed[0] = r;
            accumulated.Add(seed);
        }

        for (var level = 1; level < width; level++)
        {
            var join = plan.Joins[level - 1];
            var right = scans[level];
            // RIGHT / FULL need the unmatched-right tail, so track which right
            // rows a left row paired with across the whole left iteration.
            var keepUnmatchedRight = join.Kind is JoinKind.Right or JoinKind.Full;
            var matched = new bool[right.Count];
            var next = new List<int[]>();
            foreach (var left in accumulated)
            {
                for (var i = 0; i < width; i++)
                    current[i] = i < level && left[i] >= 0 ? scans[i][left[i]].Bytes : null;

                var any = false;
                for (var r = 0; r < right.Count; r++)
                {
                    current[level] = right[r].Bytes;
                    if (join.OnPredicate is { } on && on.Run(runtime) != true)
                        continue;
                    var row = (int[])left.Clone();
                    row[level] = r;
                    next.Add(row);
                    any = true;
                    matched[r] = true;
                }

                if (!any && join.Kind is JoinKind.Left or JoinKind.Full)
                    next.Add((int[])left.Clone());
            }

            for (var r = 0; keepUnmatchedRight && r < right.Count; r++)
            {
                if (matched[r])
                    continue;
                var row = new int[width];
                Array.Fill(row, -1);
                row[level] = r;
                next.Add(row);
            }

            accumulated = next;
        }

        return accumulated;
    }

    /// <summary>
    /// Total-order comparison between two cursor rows: ORDER BY key first (per
    /// the SELECT's ASC/DESC flags), then the per-source stable addresses
    /// ascending as a deterministic tiebreak (addresses are unique within a
    /// heap, so the tuple of them is unique across the join). Drives both the
    /// stable sort in <see cref="EnumerateForCursor"/> and DYNAMIC next/prior
    /// navigation.
    /// </summary>
    internal int CompareCursorRows(CursorRow a, CursorRow b)
    {
        var orderBy = this.CursorOrderBy ?? [];
        var c = orderBy.Count == 0 ? 0 : CompareOrderKeys(a.OrderKey, b.OrderKey, orderBy);
        for (var i = 0; c == 0 && i < a.Rids.Length; i++)
            c = CompareRids(a.Rids[i], b.Rids[i]);
        return c;
    }

    /// <summary>Ascending compare of two stable addresses, a missing one
    /// (NULL-extended outer-join side) sorting first.</summary>
    private static int CompareRids((int Page, int Slot)? a, (int Page, int Slot)? b)
    {
        if (a is not { } left)
            return b is null ? 0 : -1;
        if (b is not { } right)
            return 1;
        var c = left.Page.CompareTo(right.Page);
        return c != 0 ? c : left.Slot.CompareTo(right.Slot);
    }

    /// <summary>
    /// True when <paramref name="row"/> is the same joined row a KEYSET
    /// member snapshotted at OPEN: per source, the unique-key tuple when that
    /// table has one (so an UPDATE to those columns unmakes the match, as on
    /// real SQL Server), else the stable address.
    /// </summary>
    internal static bool CursorIdentityMatches(CursorRow row, SqlValue[]?[] keys, (int Page, int Slot)?[] rids)
    {
        for (var i = 0; i < rids.Length; i++)
        {
            var same = keys[i] is { } key
                ? row.UniqueKeys[i] is { } live && CompareKeyTuples(live, key) == 0
                : Nullable.Equals(row.Rids[i], rids[i]);
            if (!same)
                return false;
        }
        return true;
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
