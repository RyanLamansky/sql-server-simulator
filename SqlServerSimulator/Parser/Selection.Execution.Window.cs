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

                case WindowKind.CumeDist:
                    foreach (var (_, indices) in partitions)
                    {
                        indices.Sort((a, b) =>
                            CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                        var n = indices.Count;
                        var i = 0;
                        while (i < n)
                        {
                            // Walk the peer group sharing this ORDER BY key; every
                            // peer gets (rows with key <= this) / N — i.e. the
                            // group's last 1-based position over N.
                            var j = i;
                            while (j + 1 < n && CompareOrderKeys(
                                    perWindowKeys[indices[j + 1]][w].OrderKeys,
                                    perWindowKeys[indices[j]][w].OrderKeys,
                                    orderByList) == 0)
                            {
                                j++;
                            }
                            var cumeDist = SqlValue.FromDouble((double)(j + 1) / n);
                            for (var k = i; k <= j; k++)
                                results[indices[k]] = cumeDist;
                            i = j + 1;
                        }
                    }
                    break;

                case WindowKind.PercentRank:
                    foreach (var (_, indices) in partitions)
                    {
                        indices.Sort((a, b) =>
                            CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                        var n = indices.Count;
                        long rankValue = 1;
                        for (var i = 0; i < n; i++)
                        {
                            if (i > 0 && CompareOrderKeys(
                                    perWindowKeys[indices[i]][w].OrderKeys,
                                    perWindowKeys[indices[i - 1]][w].OrderKeys,
                                    orderByList) != 0)
                            {
                                rankValue = i + 1;
                            }
                            // PERCENT_RANK = (RANK - 1) / (N - 1); a single-row
                            // partition is defined as 0 (avoids divide-by-zero).
                            var percentRank = n == 1 ? 0.0 : (double)(rankValue - 1) / (n - 1);
                            results[indices[i]] = SqlValue.FromDouble(percentRank);
                        }
                    }
                    break;

                case WindowKind.PercentileCont:
                case WindowKind.PercentileDisc:
                    {
                        var isDisc = win.Kind == WindowKind.PercentileDisc;
                        var sortType = win.OrderBy[0].Expr!.GetSqlType(batch, name => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver: null));
                        var descending = win.OrderBy[0].Descending;

                        // The percentile fraction is evaluated once per query.
                        // NULL or a value outside [0, 1] surfaces Msg 8727. With
                        // no buffered rows there's no partition to emit into, so
                        // the check is skipped (matches "no rows → no error").
                        var p = 0.0;
                        if (buffered.Count > 0)
                        {
                            var pValue = EvaluateScalarArg(win.PercentileArg!, buffered, sources, batch, outerResolver);
                            if (pValue.IsNull)
                                throw SimulatedSqlException.PercentileInputOutOfRange();
                            p = pValue.CoerceTo(SqlType.Float).AsDouble;
                            if (p is < 0.0 or > 1.0)
                                throw SimulatedSqlException.PercentileInputOutOfRange();
                        }

                        foreach (var (_, indices) in partitions)
                        {
                            // Collect non-NULL WITHIN GROUP sort-key values; NULLs
                            // are excluded from the percentile computation.
                            var values = new List<SqlValue>(indices.Count);
                            foreach (var idx in indices)
                            {
                                var key = perWindowKeys[idx][w].OrderKeys[0];
                                if (!key.IsNull)
                                    values.Add(key);
                            }
                            values.Sort((a, b) =>
                            {
                                var c = CompareScalarValues(a, b);
                                return descending ? -c : c;
                            });

                            SqlValue result;
                            if (values.Count == 0)
                            {
                                result = SqlValue.Null(isDisc ? sortType : SqlType.Float);
                            }
                            else if (isDisc)
                            {
                                // Smallest value whose cumulative distribution >= p:
                                // index ceil(p*n) - 1, clamped. Returned in the sort
                                // expression's own type.
                                var k = Math.Max(0, (int)Math.Ceiling(p * values.Count) - 1);
                                result = values[k].CoerceTo(sortType);
                            }
                            else
                            {
                                // Continuous: linear interpolation at rank p*(n-1).
                                var count = values.Count;
                                var rank = p * (count - 1);
                                var lo = (int)Math.Floor(rank);
                                var hi = (int)Math.Ceiling(rank);
                                var loValue = values[lo].CoerceTo(SqlType.Float).AsDouble;
                                var hiValue = values[hi].CoerceTo(SqlType.Float).AsDouble;
                                result = SqlValue.FromDouble(loValue + ((rank - lo) * (hiValue - loValue)));
                            }

                            foreach (var idx in indices)
                                results[idx] = result;
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

                        // JSON_OBJECTAGG carries a per-row key that the generic
                        // value-only Add path can't thread, so it gets a
                        // dedicated walk. JSON_ARRAYAGG has no second operand and
                        // rides the generic path below unchanged.
                        if (aggregate.Kind == AggregateKind.JsonObjectAgg)
                        {
                            ComputeJsonObjectAggWindow(win, sources, buffered, perWindowKeys, w, orderByList, operandType, resultType, partitions, results, batch, outerResolver);
                            break;
                        }

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

                            // Framed path. Evaluate each row's operand exactly
                            // once (in sorted order), then maintain the frame
                            // incrementally. Both frame bounds move
                            // monotonically forward as i advances, so a
                            // two-pointer slide — Add as the end advances, Remove
                            // as the start advances — touches each row O(1)
                            // amortized, collapsing the former per-row
                            // re-aggregation from O(n²) to O(n) per partition.
                            var count = indices.Count;
                            var operandByPos = new SqlValue[count];
                            for (var p = 0; p < count; p++)
                            {
                                var localTuple = buffered[indices[p]];
                                SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSource);
                                operandByPos[p] = aggregate.Operand is null
                                    ? SqlValue.Null(SqlType.Int32)
                                    : aggregate.Operand.Run(new RuntimeContext(ResolveSource, batch));
                            }

                            // The start advances (so rows leave the frame and the
                            // aggregator must support removal) for any explicit
                            // start bound other than UNBOUNDED PRECEDING. The
                            // default frame (no explicit frame, with ORDER BY) is
                            // UNBOUNDED PRECEDING TO CURRENT ROW — start pinned at
                            // row 0, so it's a pure forward accumulation.
                            var startAdvances = win.Frame is { } frame && frame.Start.Kind != FrameBoundKind.UnboundedPreceding;
                            var slider = Aggregator.Create(aggregate, operandType, resultType, removable: startAdvances);

                            // DISTINCT forms can't undo an Add (illegal with OVER
                            // anyway): re-aggregate each frame, but off the
                            // once-evaluated operands.
                            if (startAdvances && !slider.CanRemove)
                            {
                                for (var i = 0; i < count; i++)
                                {
                                    var (rebuildStart, rebuildEnd) = ComputeFrameExtent(win, indices, perWindowKeys, w, orderByList, i);
                                    var aggregator = Aggregator.Create(aggregate, operandType, resultType);
                                    for (var j = rebuildStart; j <= rebuildEnd; j++)
                                        aggregator.Add(operandByPos[j]);
                                    results[indices[i]] = aggregator.Result();
                                }
                                continue;
                            }

                            var emptyResult = Aggregator.Create(aggregate, operandType, resultType).Result();
                            var lo = 0;
                            var hi = -1;
                            for (var i = 0; i < count; i++)
                            {
                                var (frameStart, frameEnd) = ComputeFrameExtent(win, indices, perWindowKeys, w, orderByList, i);
                                if (frameStart > frameEnd)
                                {
                                    results[indices[i]] = emptyResult;
                                    continue;
                                }
                                while (hi < frameEnd)
                                    slider.Add(operandByPos[++hi]);
                                while (lo < frameStart)
                                    slider.Remove(operandByPos[lo++]);
                                results[indices[i]] = slider.Result();
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
    /// Window walk for <c>JSON_OBJECTAGG(key : value) OVER (...)</c>. Unlike
    /// the generic aggregate path, each row contributes two evaluated
    /// expressions (key + value), so the key is set on the aggregator
    /// immediately before its value is added. Whole-partition windows (no
    /// ORDER BY, no explicit frame) compute one object and broadcast it;
    /// running / framed windows rebuild per row over the frame extent (JSON
    /// aggregators are non-removable, matching the generic path's rebuild
    /// fallback). Results are written into <paramref name="results"/> by
    /// buffer index.
    /// </summary>
    private static void ComputeJsonObjectAggWindow(
        WindowExpression win,
        FromSource[] sources,
        List<byte[]?[]> buffered,
        List<(SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[]> perWindowKeys,
        int w,
        List<OrderBySpec> orderByList,
        SqlType operandType,
        SqlType resultType,
        Dictionary<SqlValue[], List<int>> partitions,
        SqlValue[] results,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var aggregate = win.AggregateInfo!;
        foreach (var (_, indices) in partitions)
        {
            if (orderByList.Count > 0)
            {
                indices.Sort((a, b) =>
                    CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
            }

            var count = indices.Count;
            var keys = new SqlValue[count];
            var values = new SqlValue[count];
            for (var p = 0; p < count; p++)
            {
                var localTuple = buffered[indices[p]];
                SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveSource);
                var runtime = new RuntimeContext(ResolveSource, batch);
                keys[p] = aggregate.KeyExpression!.Run(runtime);
                values[p] = aggregate.Operand!.Run(runtime);
            }

            if (orderByList.Count == 0 && win.Frame is null)
            {
                var aggregator = (Aggregators.JsonObjectAggAggregator)Aggregator.Create(aggregate, operandType, resultType);
                for (var p = 0; p < count; p++)
                {
                    aggregator.SetKey(keys[p]);
                    aggregator.Add(values[p]);
                }
                var partitionResult = aggregator.Result();
                foreach (var idx in indices)
                    results[idx] = partitionResult;
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var (frameStart, frameEnd) = ComputeFrameExtent(win, indices, perWindowKeys, w, orderByList, i);
                var aggregator = (Aggregators.JsonObjectAggAggregator)Aggregator.Create(aggregate, operandType, resultType);
                for (var j = frameStart; j <= frameEnd; j++)
                {
                    aggregator.SetKey(keys[j]);
                    aggregator.Add(values[j]);
                }
                results[indices[i]] = aggregator.Result();
            }
        }
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
