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
    /// Aggregate-mode executor: streams every input tuple through each
    /// projection aggregate's accumulator (per group when GROUP BY is in
    /// play), then projects one output row per group. WHERE excluders run
    /// per source row before aggregation; HAVING runs per group after
    /// finalization; ORDER BY runs across groups at the end. Without
    /// GROUP BY the output is exactly one row even for empty input (SQL
    /// Server's implicit-empty-GROUP-BY rule); per-aggregate empty-input
    /// behavior is each aggregator's responsibility (COUNT returns 0;
    /// everything else NULL). <paramref name="outerResolver"/> chains
    /// unresolved column references to the enclosing scope.
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
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (topCount == 0)
            return [];

        var groupByExpressions = fromClause.GroupBy;
        var groupByCount = groupByExpressions.Count;
        var groups = new Dictionary<SqlValueKey, GroupState>();

        var aggregateOperandTypes = new SqlType[aggregates.Count];
        var aggregateResultTypes = new SqlType[aggregates.Count];
        for (var i = 0; i < aggregates.Count; i++)
        {
            aggregateOperandTypes[i] = aggregates[i].Operand?.GetSqlType(resolveColumnType) ?? SqlType.Int32;
            aggregateResultTypes[i] = aggregates[i].GetSqlType(resolveColumnType);
        }

        GroupState NewGroup()
        {
            var freshAggregators = new Aggregator[aggregates.Count];
            for (var i = 0; i < aggregates.Count; i++)
                freshAggregators[i] = Aggregator.Create(aggregates[i], aggregateOperandTypes[i], aggregateResultTypes[i]);
            return new(keyValues: new SqlValue[groupByCount], aggregators: freshAggregators);
        }

        if (groupByCount == 0)
            groups[SqlValueKey.Empty] = NewGroup();

        foreach (var tuple in EnumerateJoinedRows(sources, joins, outerResolver))
        {
            var localTuple = tuple;
            SqlValue ResolveColumn(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, outerResolver, ResolveColumn);

            var include = true;
            foreach (var excluder in fromClause.Excluders)
            {
                if (excluder.Run(ResolveColumn) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            GroupState state;
            if (groupByCount == 0)
            {
                state = groups[SqlValueKey.Empty];
            }
            else
            {
                var keyValues = new SqlValue[groupByCount];
                for (var i = 0; i < groupByCount; i++)
                    keyValues[i] = groupByExpressions[i].Run(ResolveColumn);
                var key = new SqlValueKey(keyValues);
                if (!groups.TryGetValue(key, out state!))
                {
                    state = NewGroup();
                    Array.Copy(keyValues, state.KeyValues, groupByCount);
                    groups[key] = state;
                }
            }

            for (var i = 0; i < aggregates.Count; i++)
            {
                var aggregate = aggregates[i];
                if (aggregate.Kind == AggregateKind.StringAgg && state.Aggregators[i] is Aggregators.StringAggAggregator stringAgg)
                {
                    var separatorValue = aggregate.Separator!.Run(ResolveColumn);
                    stringAgg.SetSeparator(separatorValue.IsNull ? string.Empty : separatorValue.AsString);

                    if (aggregate.OrderBy is { } orderBy)
                    {
                        var orderKeys = new SqlValue[orderBy.Count];
                        for (var k = 0; k < orderBy.Count; k++)
                            orderKeys[k] = orderBy[k].Expr!.Run(ResolveColumn);
                        stringAgg.AddOrdered(aggregate.Operand!.Run(ResolveColumn), orderKeys);
                        continue;
                    }
                }
                var operand = aggregate.Operand;
                state.Aggregators[i].Add(operand is null ? SqlValue.Null(SqlType.Int32) : operand.Run(ResolveColumn));
            }
        }

        var output = new List<byte[]>();
        foreach (var (_, state) in groups)
        {
            for (var i = 0; i < aggregates.Count; i++)
                aggregates[i].BindResult(state.Aggregators[i].Result());

            SqlValue ResolveByGroupKey(MultiPartName name)
            {
                for (var i = 0; i < groupByCount; i++)
                {
                    if (groupByExpressions[i] is Reference r
                        && Collation.Default.Equals(r.Name, name.Leaf))
                    {
                        return state.KeyValues[i];
                    }
                }
                return outerResolver is not null
                    ? outerResolver(name)
                    : throw SimulatedSqlException.InvalidColumnName(name);
            }

            if (fromClause.Having is { } having && having.Run(ResolveByGroupKey) != true)
                continue;

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(ResolveByGroupKey);

            output.Add(RowEncoder.EncodeRow(outputSchema, projected));
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
