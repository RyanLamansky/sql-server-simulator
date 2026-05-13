using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Aggregate-mode executor for SELECTs that carry aggregates, GROUP BY, or
/// HAVING. Streams every input tuple through each projection aggregate's
/// accumulator (per group when GROUP BY is in play), then projects one
/// output row per group.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Aggregate-mode executor. Buffers WHERE-filtered input rows once, then
    /// iterates each grouping set (or a single synthesized empty set when
    /// GROUP BY is absent) and partitions the buffer by that set's column
    /// values, accumulating aggregates per partition. Projection runs once
    /// per group with a grouped-away resolver: references to GROUP BY
    /// columns not in the current set return typed NULL (matching SQL
    /// Server's subtotal/total-row semantics), references to other columns
    /// in the parent scope chain through <paramref name="outerResolver"/>.
    /// HAVING runs per group after finalization; TOP / OFFSET / FETCH apply
    /// to the final concatenated stream across all grouping sets. Without
    /// GROUP BY the output is exactly one row even for empty input (SQL
    /// Server's implicit-empty-GROUP-BY rule).
    /// </summary>
    private static List<byte[]> BuildAggregateProjectionRows(
        FromSource[] sources,
        JoinSpec[] joins,
        Func<MultiPartName, SqlType> resolveColumnType,
        List<Expression> expressions,
        FromClause fromClause,
        SqlType[] outputSchema,
        List<AggregateExpression> aggregates,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (topCount == 0)
            return [];

        var aggregateOperandTypes = new SqlType[aggregates.Count];
        var aggregateResultTypes = new SqlType[aggregates.Count];
        for (var i = 0; i < aggregates.Count; i++)
        {
            aggregateOperandTypes[i] = aggregates[i].Operand?.GetSqlType(resolveColumnType) ?? SqlType.Int32;
            aggregateResultTypes[i] = aggregates[i].GetSqlType(resolveColumnType);
        }

        GroupState NewGroup(int keyArity)
        {
            var freshAggregators = new Aggregator[aggregates.Count];
            for (var i = 0; i < aggregates.Count; i++)
                freshAggregators[i] = Aggregator.Create(aggregates[i], aggregateOperandTypes[i], aggregateResultTypes[i]);
            return new(keyValues: new SqlValue[keyArity], aggregators: freshAggregators);
        }

        // Buffer WHERE-passing rows once. `EnumerateJoinedRows` mutates a
        // single shared tuple array in place across iterations, so each
        // accepted row gets snapshotted (the inner byte[] references are
        // immutable, only the outer array slots get rewritten by the join
        // driver). Captured snapshots are then iterated per grouping set.
        var buffered = new List<byte[]?[]>();
        foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
        {
            var localTuple = tuple;
            SqlValue ResolveColumn(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveColumn);

            var include = true;
            foreach (var excluder in fromClause.Excluders)
            {
                if (excluder.Run(new RuntimeContext(ResolveColumn, batch)) != true)
                {
                    include = false;
                    break;
                }
            }
            if (include)
            {
                var snapshot = new byte[]?[tuple.Length];
                Array.Copy(tuple, snapshot, tuple.Length);
                buffered.Add(snapshot);
            }
        }

        // Effective grouping sets: parser-built list, or a single synthesized
        // empty set when GROUP BY is absent (the implicit "all rows are one
        // group" case for queries that have aggregates but no GROUP BY).
        var effectiveSets = fromClause.GroupingSets.Count > 0
            ? (IReadOnlyList<Expression[]>)fromClause.GroupingSets
            : [[]];

        var output = new List<byte[]>();
        foreach (var groupingSet in effectiveSets)
        {
            var groups = new Dictionary<SqlValueKey, GroupState>();
            if (groupingSet.Length == 0)
                groups[SqlValueKey.Empty] = NewGroup(0);

            foreach (var tuple in buffered)
            {
                var localTuple = tuple;
                SqlValue ResolveColumn(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, ResolveColumn);

                GroupState state;
                if (groupingSet.Length == 0)
                {
                    state = groups[SqlValueKey.Empty];
                }
                else
                {
                    var keyValues = new SqlValue[groupingSet.Length];
                    for (var i = 0; i < groupingSet.Length; i++)
                        keyValues[i] = groupingSet[i].Run(new RuntimeContext(ResolveColumn, batch));
                    var key = new SqlValueKey(keyValues);
                    if (!groups.TryGetValue(key, out state!))
                    {
                        state = NewGroup(groupingSet.Length);
                        Array.Copy(keyValues, state.KeyValues, groupingSet.Length);
                        groups[key] = state;
                    }
                }

                for (var i = 0; i < aggregates.Count; i++)
                {
                    var aggregate = aggregates[i];
                    if (aggregate.Kind == AggregateKind.StringAgg && state.Aggregators[i] is Aggregators.StringAggAggregator stringAgg)
                    {
                        var separatorValue = aggregate.Separator!.Run(new RuntimeContext(ResolveColumn, batch));
                        stringAgg.SetSeparator(separatorValue.IsNull ? string.Empty : separatorValue.AsString);

                        if (aggregate.OrderBy is { } orderBy)
                        {
                            var orderKeys = new SqlValue[orderBy.Count];
                            for (var k = 0; k < orderBy.Count; k++)
                                orderKeys[k] = orderBy[k].Expr!.Run(new RuntimeContext(ResolveColumn, batch));
                            stringAgg.AddOrdered(aggregate.Operand!.Run(new RuntimeContext(ResolveColumn, batch)), orderKeys);
                            continue;
                        }
                    }
                    var operand = aggregate.Operand;
                    state.Aggregators[i].Add(operand is null ? SqlValue.Null(SqlType.Int32) : operand.Run(new RuntimeContext(ResolveColumn, batch)));
                }
            }

            foreach (var (_, state) in groups)
            {
                for (var i = 0; i < aggregates.Count; i++)
                    aggregates[i].BindResult(state.Aggregators[i].Result());

                // Publish current grouping-set context so GROUPING() /
                // GROUPING_ID() in projection / HAVING expressions can read
                // it. Save & restore around this group's evaluation to keep
                // nested aggregate queries (correlated subqueries) clean.
                var savedSet = batch.GroupingSetExpressions;
                var savedAll = batch.AllGroupingExpressions;
                batch.GroupingSetExpressions = groupingSet;
                batch.AllGroupingExpressions = fromClause.AllGroupingExpressions;

                var capturedSet = groupingSet;
                SqlValue ResolveByGroupKey(MultiPartName name)
                {
                    for (var i = 0; i < capturedSet.Length; i++)
                    {
                        if (capturedSet[i] is Reference r
                            && Collation.Default.Equals(r.Name, name.Leaf))
                        {
                            return state.KeyValues[i];
                        }
                    }
                    // Column appears in another grouping set but not this one
                    // — return typed NULL to surface the subtotal/total-row
                    // semantic. The type comes from the column-type resolver.
                    foreach (var expr in fromClause.AllGroupingExpressions)
                    {
                        if (expr is Reference r
                            && Collation.Default.Equals(r.Name, name.Leaf))
                        {
                            return SqlValue.Null(expr.GetSqlType(resolveColumnType));
                        }
                    }
                    return outerResolver is not null
                        ? outerResolver(name)
                        : throw SimulatedSqlException.InvalidColumnName(name);
                }

                try
                {
                    if (fromClause.Having is { } having && having.Run(new RuntimeContext(ResolveByGroupKey, batch)) != true)
                        continue;

                    var projected = new SqlValue[expressions.Count];
                    for (var i = 0; i < expressions.Count; i++)
                        projected[i] = expressions[i].Run(new RuntimeContext(ResolveByGroupKey, batch));

                    output.Add(RowEncoder.EncodeRow(outputSchema, projected));
                }
                finally
                {
                    batch.GroupingSetExpressions = savedSet;
                    batch.AllGroupingExpressions = savedAll;
                }
            }
        }

        if (topCount is { } topLimit && output.Count > topLimit)
            output = [.. output.Take(topLimit)];

        if (offsetCount is { } offset && offset > 0)
            output = [.. output.Skip(offset)];
        if (fetchCount is { } fetchLimit && output.Count > fetchLimit)
            output = [.. output.Take(fetchLimit)];

        return output;
    }

    /// <summary>
    /// Per-group state inside <see cref="BuildAggregateProjectionRows"/>: the
    /// resolved key tuple (used to populate non-aggregate projection slots
    /// from the GROUP BY's column references) plus one aggregator per
    /// <see cref="AggregateExpression"/> in the projection.
    /// </summary>
    private sealed class GroupState(SqlValue[] keyValues, Aggregator[] aggregators)
    {
        public readonly SqlValue[] KeyValues = keyValues;
        public readonly Aggregator[] Aggregators = aggregators;
    }

    /// <summary>
    /// Hash-key wrapper around a <see cref="SqlValue"/> tuple used as a
    /// dictionary key for GROUP BY buckets. Two NULL slots compare equal
    /// (matching SQL Server: NULL is a valid group key with one bucket).
    /// </summary>
    private readonly struct SqlValueKey(SqlValue[] values) : IEquatable<SqlValueKey>
    {
        public static readonly SqlValueKey Empty = new([]);

        private readonly SqlValue[] values = values;

        public bool Equals(SqlValueKey other)
        {
            if (this.values.Length != other.values.Length)
                return false;
            for (var i = 0; i < this.values.Length; i++)
            {
                var a = this.values[i];
                var b = other.values[i];
                if (a.IsNull != b.IsNull)
                    return false;
                if (a.IsNull)
                    continue;
                if (!a.Equals(b))
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is SqlValueKey other && Equals(other);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var v in this.values)
                h.Add(v.IsNull ? 0 : v.GetHashCode());
            return h.ToHashCode();
        }
    }
}
