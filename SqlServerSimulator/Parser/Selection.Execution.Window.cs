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

            if (win.Kind == WindowKind.RowNumber)
            {
                var orderByList = new List<OrderBySpec>(win.OrderBy);
                foreach (var (_, indices) in partitions)
                {
                    indices.Sort((a, b) =>
                        CompareOrderKeys(perWindowKeys[a][w].OrderKeys, perWindowKeys[b][w].OrderKeys, orderByList));
                    for (var rank = 0; rank < indices.Count; rank++)
                        results[indices[rank]] = SqlValue.FromInt64(rank + 1);
                }
            }
            else
            {
                // Aggregate window: build one Aggregator per partition,
                // accumulate across all rows in the partition (insertion
                // order — no ORDER BY in OVER for aggregates per Decision
                // A), and broadcast the Result to every row. Operand and
                // result types were pre-resolved by BuildSqlProjection.
                var aggregate = win.AggregateInfo!;
                var operandType = windowOperandTypes[w];
                var resultType = windowResultTypes[w];
                foreach (var (_, indices) in partitions)
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
                    var result = aggregator.Result();
                    foreach (var i in indices)
                        results[i] = result;
                }
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
}
