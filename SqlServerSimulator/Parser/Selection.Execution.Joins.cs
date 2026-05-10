using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Multi-source FROM enumeration: cross-product / join row stream and the
/// per-row column resolver. Lateral plans (CROSS APPLY / OUTER APPLY and
/// always-deferred derived tables) flow through the same driver.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Resolves a column reference against a row tuple of byte[] slots
    /// (one per FROM source; null slots indicate unmatched LEFT-JOIN
    /// rows, which expose NULL of the source's declared column type).
    /// Falls through to the outer-scope resolver when no local source
    /// matches.
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
    /// <c>byte[]?[]</c> tuples, one byte[] per source (null in slots
    /// representing the unmatched right side of a LEFT JOIN). Single-source
    /// FROM produces a one-slot tuple per heap row. The same array
    /// instance is reused across yields for efficiency — consumers must
    /// finish reading each tuple (typically by projecting / encoding the
    /// row) before advancing the enumerator.
    /// </summary>
    internal static IEnumerable<byte[]?[]> EnumerateJoinedRows(
        FromSource[] sources,
        JoinSpec[] joins,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var tuple = new byte[]?[sources.Length];

        if (joins.Length == 0)
        {
            // Single FROM source. A correlated derived table here carries a
            // LateralPlan whose execution depends on the enclosing scope's
            // resolver (no per-row tuple to feed yet — we're at the outermost
            // level of this Selection). Run it once with the outer resolver
            // and stream its rows; eager (non-correlated) sources iterate
            // their pre-evaluated row bytes the same way as before.
            var rows = sources[0].LateralPlan is { } leftmostLateralPlan
                ? leftmostLateralPlan.Execute(batch, outerResolver).RowBytes
                : sources[0].Rows;
            foreach (var row in rows)
            {
                tuple[0] = row;
                yield return tuple;
            }
            yield break;
        }

        SqlValue Resolve(byte[]?[] currentTuple, MultiPartName name) =>
            ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, n => Resolve(currentTuple, n));

        // Leftmost-source lateral plan in a multi-source FROM: same as the
        // joins.Length==0 case — drive it from the outer resolver, then let
        // the join driver chain through the remaining sources. The level==0
        // branch in JoinDriver handles non-lateral leftmost sources; we
        // pre-feed the lateral row stream via a temporary FromSource swap.
        if (sources[0].LateralPlan is { } leadLateralPlan)
        {
            foreach (var row in leadLateralPlan.Execute(batch, outerResolver).RowBytes)
            {
                tuple[0] = row;
                foreach (var t in JoinDriver(sources, joins, tuple, Resolve, level: 1, batch))
                    yield return t;
            }
            yield break;
        }

        foreach (var t in JoinDriver(sources, joins, tuple, Resolve, level: 0, batch))
            yield return t;
    }

    /// <summary>
    /// Recursive join driver. At each level, iterates the source's rows,
    /// places the current row into the tuple at that source's slot, and
    /// recurses to the next level. INNER and CROSS only emit when the
    /// ON predicate (if any) passes; LEFT NULL-fills the right side when
    /// no row at that level matched the predicate against the partial
    /// tuple. The tuple array is reused across yields.
    /// </summary>
    private static IEnumerable<byte[]?[]> JoinDriver(
        FromSource[] sources,
        JoinSpec[] joins,
        byte[]?[] tuple,
        Func<byte[]?[], MultiPartName, SqlValue> resolve,
        int level,
        BatchContext batch)
    {
        if (level == sources.Length)
        {
            yield return tuple;
            yield break;
        }

        // The leftmost source has no incoming join (joins[0] is the join
        // for sources[1], etc.). For levels beyond 0, joins[level - 1]
        // describes how this source attaches.
        if (level == 0)
        {
            foreach (var row in sources[0].Rows)
            {
                tuple[0] = row;
                foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1, batch))
                    yield return t;
            }
            yield break;
        }

        var join = joins[level - 1];
        var matched = false;

        // Lateral source: any source backed by a deferred plan. APPLY brings
        // its own join kind (CrossApply / OuterApply, no ON predicate) and
        // correlates via the inner WHERE; ordinary derived tables in INNER /
        // LEFT / CROSS JOIN slots also flow here since
        // <see cref="ParseSingleFromSource"/> always defers (correlation isn't
        // statically detectable). Apply the ON predicate when the join has
        // one, and null-fill for both LEFT and OUTER APPLY when nothing
        // matched.
        if (sources[level].LateralPlan is { } lateralPlan)
        {
            foreach (var row in lateralPlan.Execute(batch, name => resolve(tuple, name)).RowBytes)
            {
                tuple[level] = row;
                if (join.OnPredicate is not null && join.OnPredicate.Run(new RuntimeContext(name => resolve(tuple, name), batch)) != true)
                    continue;
                matched = true;
                foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1, batch))
                    yield return t;
            }
            tuple[level] = null;
            if (!matched && join.Kind is JoinKind.OuterApply or JoinKind.Left)
            {
                foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1, batch))
                    yield return t;
            }
            yield break;
        }

        foreach (var row in sources[level].Rows)
        {
            tuple[level] = row;
            var passes = join.OnPredicate is null || join.OnPredicate.Run(new RuntimeContext(name => resolve(tuple, name), batch)) == true;
            if (!passes)
                continue;
            matched = true;
            foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1, batch))
                yield return t;
        }
        tuple[level] = null;
        if (!matched && join.Kind == JoinKind.Left)
        {
            foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1, batch))
                yield return t;
        }
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
