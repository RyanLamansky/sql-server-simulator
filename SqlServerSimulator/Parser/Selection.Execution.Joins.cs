using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Multi-source FROM enumeration: cross-product / join row stream and the
/// per-row column resolver. INNER / LEFT / CROSS / CROSS APPLY / OUTER
/// APPLY stream through the same operator pipeline; RIGHT / FULL
/// materialize the right source and track a matched bitmap to emit
/// unmatched-right rows after upstream is exhausted.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Resolves a column reference against a row tuple of byte[] slots
    /// (one per FROM source). A null slot indicates a NULL-filled side
    /// of an outer join — LEFT-fill at <c>tuple[s]==null</c> when
    /// <c>s</c> is the right of a LEFT JOIN, RIGHT-fill / FULL-fill at
    /// any prior slot when emitting an unmatched-right row — and the
    /// reference reads as a typed NULL.
    /// Falls through to the outer-scope resolver when no local source matches.
    /// </summary>
    private static SqlValue ResolveAcrossTuple(
        FromSource[] sources,
        byte[]?[] tuple,
        MultiPartName name,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Func<MultiPartName, SqlValue> selfRecursive)
    {
        var (s, c) = FindSourceColumn(sources, name);
        if (s == -1)
        {
            return outerResolver is not null
                ? outerResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
        }

        var bytes = tuple[s];
        return bytes is null
            ? SqlValue.Null(sources[s].Columns[c].Type)
            : DecodeOrCompute(sources[s], c, bytes, batch, selfRecursive);
    }

    /// <summary>
    /// Yields the cross-product / join row stream as a sequence of
    /// <c>byte[]?[]</c> tuples, one byte[] per source (null slots
    /// indicate NULL-filled outer-join sides). The same array instance
    /// is reused across yields for efficiency — consumers must finish
    /// reading each tuple (typically by projecting / encoding the row)
    /// before advancing the enumerator.
    /// </summary>
    /// <remarks>
    /// The driver is a fold over <paramref name="joins"/>: a leftmost
    /// rowset enumerator is constructed for <c>sources[0]</c>, then each
    /// join wraps the rowset in an operator that fills its slot per
    /// upstream tuple. INNER / LEFT / CROSS / CROSS APPLY / OUTER APPLY
    /// stream one upstream tuple at a time; RIGHT / FULL materialize
    /// <c>sources[level].Rows</c> and track a matched bitmap across the
    /// entire upstream iteration so unmatched right rows can be emitted
    /// (with NULL-filled left slots) after upstream is exhausted.
    /// </remarks>
    internal static IEnumerable<byte[]?[]> EnumerateJoinedRows(
        FromSource[] sources,
        JoinSpec[] joins,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var tuple = new byte[]?[sources.Length];

        SqlValue Resolve(MultiPartName name) =>
            ResolveAcrossTuple(sources, tuple, name, batch, outerResolver, Resolve);

        var rowset = EnumerateLeftmost(sources[0], tuple, batch, outerResolver);
        for (var level = 1; level < sources.Length; level++)
        {
            rowset = ApplyJoin(rowset, sources[level], joins[level - 1], tuple, level, batch, Resolve);
        }
        return rowset;
    }

    /// <summary>
    /// Drives <c>sources[0]</c>: its rows directly for a base / view /
    /// system table, or its <see cref="FromSource.LateralPlan"/> executed
    /// once with the enclosing-scope <paramref name="outerResolver"/>
    /// (the only correlation available at the outermost FROM level).
    /// Writes slot 0 of the shared buffer on each yield.
    /// </summary>
    private static IEnumerable<byte[]?[]> EnumerateLeftmost(
        FromSource source,
        byte[]?[] tuple,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var rows = source.LateralPlan is { } plan
            ? plan.Execute(batch, outerResolver).RowBytes
            : source.Rows;
        foreach (var row in rows)
        {
            tuple[0] = row;
            yield return tuple;
        }
        tuple[0] = null;
    }

    private static IEnumerable<byte[]?[]> ApplyJoin(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve) => join.Kind switch
        {
            JoinKind.Inner or JoinKind.Cross or JoinKind.CrossApply
                => InnerOrCross(left, right, join, tuple, level, batch, resolve),
            JoinKind.Left or JoinKind.OuterApply
                => LeftOrOuterApply(left, right, join, tuple, level, batch, resolve),
            JoinKind.Right
                => RightOuterJoin(left, right, join, tuple, level, batch, resolve),
            JoinKind.Full
                => FullOuterJoin(left, right, join, tuple, level, batch, resolve),
            _ => throw new NotSupportedException($"Unknown JoinKind {join.Kind}"),
        };

    /// <summary>
    /// INNER JOIN (with ON), CROSS JOIN (no ON), and CROSS APPLY (no ON,
    /// lateral right): for each upstream tuple iterate right rows (or
    /// re-execute the lateral plan), check ON when present, yield matches.
    /// Upstream tuples with no match are silently dropped.
    /// </summary>
    private static IEnumerable<byte[]?[]> InnerOrCross(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve)
    {
        foreach (var _ in left)
        {
            var rows = right.LateralPlan is { } plan
                ? plan.Execute(batch, resolve).RowBytes
                : right.Rows;
            foreach (var row in rows)
            {
                tuple[level] = row;
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                    yield return tuple;
            }
            tuple[level] = null;
        }
    }

    /// <summary>
    /// LEFT JOIN (with ON; right side may be a base table or a deferred
    /// derived table) and OUTER APPLY (no ON, lateral right): for each
    /// upstream tuple iterate right rows, check ON when present, yield
    /// matches; if no row matched, NULL-fill the level slot and yield
    /// once with the unmatched upstream tuple.
    /// </summary>
    private static IEnumerable<byte[]?[]> LeftOrOuterApply(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve)
    {
        foreach (var _ in left)
        {
            var rows = right.LateralPlan is { } plan
                ? plan.Execute(batch, resolve).RowBytes
                : right.Rows;
            var matched = false;
            foreach (var row in rows)
            {
                tuple[level] = row;
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                {
                    matched = true;
                    yield return tuple;
                }
            }
            tuple[level] = null;
            if (!matched)
                yield return tuple;
        }
    }

    /// <summary>
    /// RIGHT [OUTER] JOIN: materializes the right source's rows up front
    /// and tracks a matched bitmap across the whole upstream iteration.
    /// For each upstream tuple, sweeps the right list, marks matches,
    /// yields matched pairs (upstream tuples without any matching right
    /// are silently dropped — that's the asymmetric flip of LEFT JOIN).
    /// After upstream is exhausted, walks the bitmap and emits each
    /// unmatched right row with all left-side slots NULL-filled. The
    /// lateral / derived-table right side is rejected — real SQL Server
    /// raises Msg 4104 on correlated subqueries to the right of
    /// RIGHT / FULL, and the simulator defers every derived table
    /// regardless of actual correlation, so the safe rule is to reject
    /// every <see cref="FromSource.LateralPlan"/>-bearing right.
    /// </summary>
    private static IEnumerable<byte[]?[]> RightOuterJoin(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve)
    {
        if (right.LateralPlan is not null)
            throw new NotSupportedException("RIGHT JOIN with a derived-table right side isn't modeled.");

        var rightRows = right.Rows.ToList();
        var matched = new bool[rightRows.Count];

        foreach (var _ in left)
        {
            for (var i = 0; i < rightRows.Count; i++)
            {
                tuple[level] = rightRows[i];
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                {
                    matched[i] = true;
                    yield return tuple;
                }
            }
            tuple[level] = null;
        }

        for (var j = 0; j < level; j++)
            tuple[j] = null;
        for (var i = 0; i < rightRows.Count; i++)
        {
            if (matched[i])
                continue;
            tuple[level] = rightRows[i];
            yield return tuple;
        }
        tuple[level] = null;
    }

    /// <summary>
    /// FULL [OUTER] JOIN: matched pairs emit normally; unmatched upstream
    /// tuples emit with the level slot NULL-filled; unmatched right rows
    /// (tracked across the whole upstream iteration) emit at the end with
    /// all left-side slots NULL-filled. Same lateral-right restriction as
    /// <see cref="RightOuterJoin"/>.
    /// </summary>
    private static IEnumerable<byte[]?[]> FullOuterJoin(
        IEnumerable<byte[]?[]> left,
        FromSource right,
        JoinSpec join,
        byte[]?[] tuple,
        int level,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve)
    {
        if (right.LateralPlan is not null)
            throw new NotSupportedException("FULL OUTER JOIN with a derived-table right side isn't modeled.");

        var rightRows = right.Rows.ToList();
        var matched = new bool[rightRows.Count];

        foreach (var _ in left)
        {
            var leftMatched = false;
            for (var i = 0; i < rightRows.Count; i++)
            {
                tuple[level] = rightRows[i];
                if (join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(resolve, batch)) == true)
                {
                    matched[i] = true;
                    leftMatched = true;
                    yield return tuple;
                }
            }
            tuple[level] = null;
            if (!leftMatched)
                yield return tuple;
        }

        for (var j = 0; j < level; j++)
            tuple[j] = null;
        for (var i = 0; i < rightRows.Count; i++)
        {
            if (matched[i])
                continue;
            tuple[level] = rightRows[i];
            yield return tuple;
        }
        tuple[level] = null;
    }

    /// <summary>
    /// Resolves a single column reference at <paramref name="columnIndex"/>
    /// in <paramref name="source"/> for the row at <paramref name="bytes"/>.
    /// Stored columns (regular plus persisted-computed) decode directly via
    /// <see cref="RowDecoder.DecodeColumn(ReadOnlySpan{HeapColumn}, ReadOnlySpan{byte}, int, Heap?)"/>
    /// at their storage ordinal. Non-persisted computed columns evaluate
    /// their expression through <paramref name="resolveByName"/> — the
    /// recursive references inside the expression bind back through the same
    /// caller's resolver, but are guaranteed by Msg 1759 to land only on
    /// stored columns.
    /// </summary>
    private static SqlValue DecodeOrCompute(
        FromSource source,
        int columnIndex,
        byte[] bytes,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolveByName) =>
        source.StorageOrdinals is null
            ? RowDecoder.DecodeColumn(source.StoredSchema, bytes, columnIndex, source.LobStore)
            : source.Columns[columnIndex].Computed is { } computedExpr && !source.Columns[columnIndex].IsPersisted
                ? computedExpr.Run(new RuntimeContext(resolveByName, batch))
                : RowDecoder.DecodeColumn(source.StoredSchema, bytes, source.StorageOrdinals[columnIndex], source.LobStore);
}
