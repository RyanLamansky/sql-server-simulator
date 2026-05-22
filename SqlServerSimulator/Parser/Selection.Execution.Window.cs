using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Window-projection executor for SELECTs whose projection contains window
/// functions (<c>ROW_NUMBER() OVER(...)</c> or aggregate-OVER). Buffers all
/// post-WHERE tuples, partitions per window's PARTITION BY, computes per-row
/// window values, then runs the projection plus the same DISTINCT / ORDER BY
/// / OFFSET / TAKE post-processing as the non-window buffered path.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Projection path for SELECTs whose projection contains window
    /// functions (<c>ROW_NUMBER() OVER(...)</c> or aggregate-OVER). Buffers
    /// all post-WHERE tuples, partitions by each window's PARTITION BY keys,
    /// sorts each ROW_NUMBER partition by the window's ORDER BY keys,
    /// assigns per-partition rank or per-partition aggregate result, then
    /// walks the buffer in original order binding each window expression's
    /// per-tuple value before running the projection. Falls through to the
    /// same OFFSET / TAKE / DISTINCT / ORDER BY post-processing as
    /// <see cref="ProjectBuffered"/>.
    /// </summary>
    private static IEnumerable<byte[]> ProjectWindowedRows(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        List<WindowExpression> windows,
        SqlType[] windowOperandTypes,
        SqlType[] windowResultTypes,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        // Step 1: buffer post-WHERE tuples. The same byte[]?[] instance is
        // reused across yields by EnumerateJoinedRows, so each entry is
        // cloned. For each buffered tuple, also pre-compute every window's
        // partition + order keys so the per-row resolver doesn't have to
        // be re-bound during window evaluation.
        var buffered = new List<byte[]?[]>();
        var perWindowKeys = new List<(SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[]>();
        foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
        {
            var localTuple = tuple;
            SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSource);

            var include = true;
            foreach (var excluder in excluders)
            {
                if (excluder.Run(new RuntimeContext(ResolveSource, batch)) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            var keys = new (SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[windows.Count];
            for (var w = 0; w < windows.Count; w++)
            {
                var win = windows[w];
                var partitionKeys = new SqlValue[win.PartitionBy.Length];
                for (var p = 0; p < win.PartitionBy.Length; p++)
                    partitionKeys[p] = win.PartitionBy[p].Run(new RuntimeContext(ResolveSource, batch));
                var orderKeys = new SqlValue[win.OrderBy.Length];
                for (var o = 0; o < win.OrderBy.Length; o++)
                    orderKeys[o] = win.OrderBy[o].Expr!.Run(new RuntimeContext(ResolveSource, batch));
                keys[w] = (partitionKeys, orderKeys);
            }

            buffered.Add((byte[]?[])tuple.Clone());
            perWindowKeys.Add(keys);
        }

        // Step 2: for each window, group buffered tuples by partition key
        // and compute the per-tuple result. ROW_NUMBER sorts each partition
        // and assigns 1..N ranks; aggregate windows run an Aggregator over
        // every tuple in the partition (no sort) and broadcast the
        // per-partition Result to every tuple in the same partition. The
        // result array is kept as SqlValue[] so the two kinds share storage.
        var perWindowResults = new SqlValue[windows.Count][];
        for (var w = 0; w < windows.Count; w++)
        {
            var win = windows[w];
            var results = new SqlValue[buffered.Count];
            var partitions = new Dictionary<SqlValue[], List<int>>(RowEqualityComparer.Instance);
            for (var i = 0; i < buffered.Count; i++)
            {
                var pk = perWindowKeys[i][w].PartitionKeys;
                if (!partitions.TryGetValue(pk, out var list))
                {
                    list = [];
                    partitions[pk] = list;
                }
                list.Add(i);
            }

            var orderByList = new List<OrderBySpec>(win.OrderBy);

            switch (win.Kind)
            {
                case WindowKind.RowNumber:
                    foreach (var (_, indices) in partitions)
                    {
                        indices.Sort((a, b) =>
                            CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                        for (var rank = 0; rank < indices.Count; rank++)
                            results[indices[rank]] = SqlValue.FromInt64(rank + 1);
                    }
                    break;

                case WindowKind.Rank:
                    foreach (var (_, indices) in partitions)
                    {
                        indices.Sort((a, b) =>
                            CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                        long rankValue = 1;
                        for (var i = 0; i < indices.Count; i++)
                        {
                            // Same ORDER BY key as previous → keep prior rank;
                            // otherwise the rank "catches up" to (i+1), so
                            // ties consume rank numbers that the next distinct
                            // group skips.
                            if (i > 0 && CompareOrderKeys(
                                    perWindowKeys[indices[i]][w].OrderKeys,
                                    perWindowKeys[indices[i - 1]][w].OrderKeys,
                                    orderByList) != 0)
                            {
                                rankValue = i + 1;
                            }
                            results[indices[i]] = SqlValue.FromInt64(rankValue);
                        }
                    }
                    break;

                case WindowKind.DenseRank:
                    foreach (var (_, indices) in partitions)
                    {
                        indices.Sort((a, b) =>
                            CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                        long denseRank = 1;
                        for (var i = 0; i < indices.Count; i++)
                        {
                            if (i > 0 && CompareOrderKeys(
                                    perWindowKeys[indices[i]][w].OrderKeys,
                                    perWindowKeys[indices[i - 1]][w].OrderKeys,
                                    orderByList) != 0)
                            {
                                denseRank++;
                            }
                            results[indices[i]] = SqlValue.FromInt64(denseRank);
                        }
                    }
                    break;

                case WindowKind.NTile:
                    {
                        // NTILE's bucket count: evaluated once per query with
                        // the first buffered row's resolver (matches real SQL
                        // Server's constant-only restriction for the common
                        // case while letting non-constant expressions surface
                        // naturally if a value can't be produced).
                        var bucketCount = (int)EvaluateScalarArg(win.BucketCount!, buffered, sources, batch, outerResolver).CoerceTo(SqlType.BigInt).AsInt64;
                        if (bucketCount <= 0)
                            throw SimulatedSqlException.NTileBucketCountMustBePositive();
                        foreach (var (_, indices) in partitions)
                        {
                            indices.Sort((a, b) =>
                                CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                            var count = indices.Count;
                            var smallerSize = count / bucketCount;
                            var firstFewBuckets = count % bucketCount;
                            for (var i = 0; i < count; i++)
                            {
                                // First firstFewBuckets buckets carry (smallerSize+1)
                                // rows; the remainder carry smallerSize. Two-piece
                                // formula keeps integer arithmetic tight.
                                var p = i + 1;
                                var bucket = p <= firstFewBuckets * (smallerSize + 1)
                                    ? ((p - 1) / (smallerSize + 1)) + 1
                                    : smallerSize == 0
                                        ? bucketCount
                                        : firstFewBuckets + ((p - (firstFewBuckets * (smallerSize + 1)) - 1) / smallerSize) + 1;
                                results[indices[i]] = SqlValue.FromInt32(bucket);
                            }
                        }
                    }
                    break;

                case WindowKind.Lag:
                case WindowKind.Lead:
                    {
                        var sign = win.Kind == WindowKind.Lag ? -1 : 1;
                        var lagOffset = win.OffsetArg is null
                            ? 1
                            : (int)EvaluateScalarArg(win.OffsetArg, buffered, sources, batch, outerResolver).CoerceTo(SqlType.BigInt).AsInt64;
                        var operandType = win.Operand!.GetSqlType(batch, name => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver: null));
                        foreach (var (_, indices) in partitions)
                        {
                            indices.Sort((a, b) =>
                                CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                            for (var i = 0; i < indices.Count; i++)
                            {
                                var targetIdx = i + (sign * lagOffset);
                                if (targetIdx < 0 || targetIdx >= indices.Count)
                                {
                                    // Out of partition bounds → DEFAULT expression
                                    // (or typed NULL). Default is evaluated in the
                                    // current row's resolver context, matching real
                                    // SQL Server (default expressions can reference
                                    // the row's columns).
                                    if (win.DefaultArg is null)
                                    {
                                        results[indices[i]] = SqlValue.Null(operandType);
                                    }
                                    else
                                    {
                                        var localTuple = buffered[indices[i]];
                                        SqlValue ResolveSelf(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSelf);
                                        results[indices[i]] = win.DefaultArg.Run(new RuntimeContext(ResolveSelf, batch));
                                    }
                                }
                                else
                                {
                                    var targetTuple = buffered[indices[targetIdx]];
                                    SqlValue ResolveTarget(MultiPartName name) => ResolveAcrossTuple(sources, targetTuple, name, batch, outerResolver, ResolveTarget);
                                    results[indices[i]] = win.Operand.Run(new RuntimeContext(ResolveTarget, batch));
                                }
                            }
                        }
                    }
                    break;

                case WindowKind.FirstValue:
                case WindowKind.LastValue:
                    {
                        // Both walk a per-row frame extent. FIRST_VALUE returns
                        // the operand evaluated at the frame start; LAST_VALUE
                        // at the frame end. Empty-frame extent (start > end)
                        // emits typed NULL — probe-confirmed against SQL Server
                        // for both with explicit frames outside partition.
                        var isLast = win.Kind == WindowKind.LastValue;
                        var operandType = win.Operand!.GetSqlType(batch, name => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver: null));
                        foreach (var (_, indices) in partitions)
                        {
                            indices.Sort((a, b) =>
                                CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                            for (var i = 0; i < indices.Count; i++)
                            {
                                var (frameStart, frameEnd) = ComputeFrameExtent(win, indices, perWindowKeys, w, orderByList, i);
                                if (frameStart > frameEnd)
                                {
                                    results[indices[i]] = SqlValue.Null(operandType);
                                    continue;
                                }
                                var refIdx = isLast ? frameEnd : frameStart;
                                var refTuple = buffered[indices[refIdx]];
                                SqlValue ResolveRef(MultiPartName name) => ResolveAcrossTuple(sources, refTuple, name, batch, outerResolver, ResolveRef);
                                results[indices[i]] = win.Operand.Run(new RuntimeContext(ResolveRef, batch));
                            }
                        }
                    }
                    break;

                case WindowKind.Aggregate:
                    {
                        // Aggregate window: per-row Aggregator over the row's
                        // frame extent. Without ORDER BY (and no explicit frame)
                        // every row's frame is the whole partition, so the
                        // result is broadcast — same shape as the pre-frame
                        // implementation. With ORDER BY, default frame is
                        // RANGE UNBOUNDED PRECEDING TO CURRENT ROW (running
                        // total with peer-tie grouping). Operand and result
                        // types were pre-resolved by BuildSqlProjection.
                        var aggregate = win.AggregateInfo!;
                        var operandType = windowOperandTypes[w];
                        var resultType = windowResultTypes[w];
                        foreach (var (_, indices) in partitions)
                        {
                            if (orderByList.Count > 0)
                            {
                                indices.Sort((a, b) =>
                                    CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                            }

                            // Whole-partition fast path: no ORDER BY and no
                            // explicit frame → compute once + broadcast.
                            if (orderByList.Count == 0 && win.Frame is null)
                            {
                                var aggregator = Aggregator.Create(aggregate, operandType, resultType);
                                foreach (var i in indices)
                                {
                                    var localTuple = buffered[i];
                                    SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSource);
                                    var operandValue = aggregate.Operand is null
                                        ? SqlValue.Null(SqlType.Int32)
                                        : aggregate.Operand.Run(new RuntimeContext(ResolveSource, batch));
                                    aggregator.Add(operandValue);
                                }
                                var partitionResult = aggregator.Result();
                                foreach (var i in indices)
                                    results[i] = partitionResult;
                                continue;
                            }

                            // Per-row frame path.
                            for (var i = 0; i < indices.Count; i++)
                            {
                                var (frameStart, frameEnd) = ComputeFrameExtent(win, indices, perWindowKeys, w, orderByList, i);
                                var aggregator = Aggregator.Create(aggregate, operandType, resultType);
                                for (var j = frameStart; j <= frameEnd; j++)
                                {
                                    var localTuple = buffered[indices[j]];
                                    SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSource);
                                    var operandValue = aggregate.Operand is null
                                        ? SqlValue.Null(SqlType.Int32)
                                        : aggregate.Operand.Run(new RuntimeContext(ResolveSource, batch));
                                    aggregator.Add(operandValue);
                                }
                                results[indices[i]] = aggregator.Result();
                            }
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unknown window kind {win.Kind}.");
            }

            perWindowResults[w] = results;
        }

        // Step 3: walk buffered tuples in original order, bind each
        // window's per-tuple result, then project. From here on,
        // mirror ProjectBuffered's DISTINCT / ORDER BY / OFFSET / TAKE
        // post-processing.
        var projectedBuffer = new List<(SqlValue[] Projected, SqlValue[] Keys)>(buffered.Count);
        for (var i = 0; i < buffered.Count; i++)
        {
            for (var w = 0; w < windows.Count; w++)
                windows[w].BindResult(perWindowResults[w][i]);

            var localTuple = buffered[i];
            SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSource);

            var projected = new SqlValue[expressions.Count];
            for (var j = 0; j < expressions.Count; j++)
                projected[j] = expressions[j].Run(new RuntimeContext(ResolveSource, batch));

            var keys = orderBy.Count == 0 ? [] : ComputeOrderKeys(orderBy, projected, outputColumnNames, distinct, batch, ResolveSource);
            projectedBuffer.Add((projected, keys));
        }

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> filtered = projectedBuffer;
        if (distinct)
        {
            var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
            filtered = projectedBuffer.Where(item => seen.Add(item.Projected));
        }

        var materialized = filtered.ToList();
        if (orderBy.Count > 0)
            materialized.Sort((a, b) => CompareOrderKeys(a.Keys, b.Keys, orderBy));

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> windowed = materialized;
        if (offsetCount is { } offset && offset > 0)
            windowed = windowed.Skip(offset);
        if ((topCount ?? fetchCount) is { } limit)
            windowed = windowed.Take(limit);

        foreach (var (projected, _) in windowed)
            yield return RowEncoder.EncodeRow(outputSchema, projected);
    }

    /// <summary>
    /// Computes the per-row frame extent (inclusive <c>[Start, End]</c>
    /// indices into the partition's sorted <paramref name="indices"/> list)
    /// for the window function at position <paramref name="i"/>. Returns
    /// <c>(count, count - 1)</c> — i.e. <c>Start &gt; End</c> — when the
    /// frame is empty (theoretical bounds outside the partition, or both
    /// bounds clamped to the same side and inverted). Default-frame logic:
    /// no <see cref="WindowExpression.Frame"/> + no ORDER BY → whole
    /// partition; no frame + ORDER BY → <c>RANGE UNBOUNDED PRECEDING TO
    /// CURRENT ROW</c> (running-total semantic with peer-tie grouping).
    /// <c>RANGE CURRENT ROW</c> peer extents are computed by scanning
    /// outward from <paramref name="i"/> while ORDER BY keys compare equal.
    /// </summary>
    private static (int Start, int End) ComputeFrameExtent(
        WindowExpression win,
        List<int> indices,
        List<(SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[]> perWindowKeys,
        int w,
        List<OrderBySpec> orderByList,
        int i)
    {
        var count = indices.Count;
        var hasOrderBy = orderByList.Count > 0;
        var frame = win.Frame;

        if (frame is null && !hasOrderBy)
            return (0, count - 1);

        // Default with ORDER BY: RANGE UNBOUNDED PRECEDING TO CURRENT ROW.
        var isRange = frame?.IsRange ?? true;
        var startBound = frame?.Start ?? FrameBound.UnboundedPreceding;
        var endBound = frame?.End ?? FrameBound.CurrentRow;

        var startPos = ResolveBoundPosition(startBound, isRange, indices, perWindowKeys, w, orderByList, i, count, isStart: true);
        var endPos = ResolveBoundPosition(endBound, isRange, indices, perWindowKeys, w, orderByList, i, count, isStart: false);

        var actualStart = Math.Max(0, startPos);
        var actualEnd = Math.Min(count - 1, endPos);
        // Empty frame: either start landed past the partition end (frame's
        // first row doesn't exist) or end landed before partition start (frame's
        // last row doesn't exist), or the two crossed after clamping.
        return startPos > count - 1 || endPos < 0 || actualStart > actualEnd
            ? (count, count - 1)
            : (actualStart, actualEnd);
    }

    /// <summary>
    /// Maps one <see cref="FrameBound"/> to its row position in the sorted
    /// partition. <c>UNBOUNDED</c> bounds saturate to safe sentinels
    /// (<c>int.MinValue / 2</c> / <c>int.MaxValue / 2</c>) so the caller's
    /// <c>Math.Max(0, ...)</c> / <c>Math.Min(count - 1, ...)</c> clamps
    /// resolve correctly without overflow.
    /// <c>RANGE CURRENT ROW</c> walks outward from <paramref name="i"/>
    /// while ORDER BY keys compare equal — that's the peer extent.
    /// <c>ROWS CURRENT ROW</c> simply returns <paramref name="i"/>.
    /// </summary>
    private static int ResolveBoundPosition(
        FrameBound bound,
        bool isRange,
        List<int> indices,
        List<(SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[]> perWindowKeys,
        int w,
        List<OrderBySpec> orderByList,
        int i,
        int count,
        bool isStart)
    {
        switch (bound.Kind)
        {
            case FrameBoundKind.UnboundedPreceding:
                return int.MinValue / 2;
            case FrameBoundKind.UnboundedFollowing:
                return int.MaxValue / 2;
            case FrameBoundKind.NPreceding:
                return i - (int)bound.Offset;
            case FrameBoundKind.NFollowing:
                return i + (int)bound.Offset;
            case FrameBoundKind.CurrentRow:
                if (!isRange)
                    return i;
                if (isStart)
                {
                    var j = i;
                    while (j > 0 && CompareOrderKeys(
                        perWindowKeys[indices[j]][w].OrderKeys,
                        perWindowKeys[indices[j - 1]][w].OrderKeys,
                        orderByList) == 0)
                    {
                        j--;
                    }
                    return j;
                }
                else
                {
                    var j = i;
                    while (j < count - 1 && CompareOrderKeys(
                        perWindowKeys[indices[j]][w].OrderKeys,
                        perWindowKeys[indices[j + 1]][w].OrderKeys,
                        orderByList) == 0)
                    {
                        j++;
                    }
                    return j;
                }
            default:
                throw new InvalidOperationException($"Unknown frame bound kind {bound.Kind}.");
        }
    }

    /// <summary>
    /// Evaluates a window-function scalar argument (NTILE's bucket count,
    /// LAG / LEAD offset) once per query against the first buffered row's
    /// resolver. Real SQL Server requires these to be constants / parameters
    /// — for literals this is equivalent to a no-context evaluation, and for
    /// stray column references it surfaces a normal column-not-found error
    /// rather than imposing a parser-side restriction. Buffered must be
    /// non-empty (no-op partitions short-circuit before this call).
    /// </summary>
    private static SqlValue EvaluateScalarArg(
        Expression arg,
        List<byte[]?[]> buffered,
        FromSource[] sources,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (buffered.Count == 0)
            return SqlValue.Null(SqlType.Int32);
        var firstTuple = buffered[0];
        SqlValue Resolve(MultiPartName name) => ResolveAcrossTuple(sources, firstTuple, name, batch, outerResolver, Resolve);
        return arg.Run(new RuntimeContext(Resolve, batch));
    }
}
