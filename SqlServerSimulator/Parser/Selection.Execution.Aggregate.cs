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
        string[] outputColumnNames,
        List<OrderBySpec> orderByItems,
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
            aggregateOperandTypes[i] = aggregates[i].Operand?.GetSqlType(batch, resolveColumnType) ?? SqlType.Int32;
            aggregateResultTypes[i] = aggregates[i].GetSqlType(batch, resolveColumnType);
        }

        GroupState NewGroup(int keyArity)
        {
            var freshAggregators = new Aggregator[aggregates.Count];
            for (var i = 0; i < aggregates.Count; i++)
                freshAggregators[i] = Aggregator.Create(aggregates[i], aggregateOperandTypes[i], aggregateResultTypes[i]);
            return new(keyValues: new SqlValue[keyArity], aggregators: freshAggregators);
        }

        // Narrow a single-base-table source by an equality seek when WHERE
        // carries an indexable conjunct — the same acceleration the
        // non-aggregate projector applies. The matched conjuncts stay in
        // Excluders as residual filters below, so the aggregate sees exactly
        // the WHERE-passing rows (e.g. SELECT SUM(x) ... WHERE indexedcol = @v
        // seeks instead of scanning the whole heap).
        sources = MaybeApplyIndexSeek(sources, joins, fromClause.Excluders, batch, outerResolver);

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

        var output = new List<(SqlValue[] OrderKeys, byte[] Row)>();
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

                // Keep the first row that lands in each group as a
                // representative. Within a group every grouping expression is
                // constant, so any projection / HAVING / ORDER BY column buried
                // inside a grouping expression (e.g. OrderDate under
                // GROUP BY MONTH(OrderDate)) resolves correctly against it.
                state.Representative ??= tuple;

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

                    // JSON_ARRAYAGG with an in-parens ORDER BY buffers each
                    // value + ORDER BY tuple; the aggregator sorts at Result.
                    if (aggregate.Kind == AggregateKind.JsonArrayAgg && aggregate.OrderBy is { } jsonOrderBy
                        && state.Aggregators[i] is Aggregators.JsonArrayAggAggregator arrayAgg)
                    {
                        var orderKeys = new SqlValue[jsonOrderBy.Count];
                        for (var k = 0; k < jsonOrderBy.Count; k++)
                            orderKeys[k] = jsonOrderBy[k].Expr!.Run(new RuntimeContext(ResolveColumn, batch));
                        arrayAgg.AddOrdered(aggregate.Operand!.Run(new RuntimeContext(ResolveColumn, batch)), orderKeys);
                        continue;
                    }

                    // JSON_OBJECTAGG needs the per-row key set before the value
                    // is streamed; the value flows through the generic Add below.
                    if (aggregate.Kind == AggregateKind.JsonObjectAgg
                        && state.Aggregators[i] is Aggregators.JsonObjectAggAggregator objectAgg)
                    {
                        objectAgg.SetKey(aggregate.KeyExpression!.Run(new RuntimeContext(ResolveColumn, batch)));
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
                            && BuiltInToken.Equals(r.Name, name.Leaf))
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
                            && BuiltInToken.Equals(r.Name, name.Leaf))
                        {
                            return SqlValue.Null(expr.GetSqlType(batch, resolveColumnType));
                        }
                    }

                    // Column referenced inside one of this (non-empty) set's
                    // grouping expressions: resolve against the group's
                    // representative row. ResolveAcrossTuple itself falls back
                    // to the outer resolver / Msg 207 when the name isn't a
                    // source column, so this subsumes the outer-or-throw tail.
                    return capturedSet.Length > 0 && state.Representative is { } rep
                        ? ResolveAcrossTuple(sources, rep, name, batch, outerResolver, ResolveByGroupKey)
                        : outerResolver is not null
                            ? outerResolver(name)
                            : throw SimulatedSqlException.InvalidColumnName(name);
                }

                // Resolves an ORDER BY item's column references against this
                // group's output (alias / select-list name first), then through
                // the grouped-key resolver — so ORDER BY can reference a select
                // alias, a grouped column, or a grouping expression.
                SqlValue ResolveOrderName(SqlValue[] projectedRow, MultiPartName name)
                {
                    for (var j = 0; j < outputColumnNames.Length; j++)
                    {
                        if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                            return projectedRow[j];
                    }

                    return ResolveByGroupKey(name);
                }

                try
                {
                    if (fromClause.Having is { } having && having.Run(new RuntimeContext(ResolveByGroupKey, batch)) != true)
                        continue;

                    var projected = new SqlValue[expressions.Count];
                    for (var i = 0; i < expressions.Count; i++)
                        projected[i] = expressions[i].Run(new RuntimeContext(ResolveByGroupKey, batch));

                    // Aggregate-query ORDER BY: keys are computed here, where
                    // each aggregate is bound and the grouping context is
                    // published, then the whole stream is sorted before TOP /
                    // OFFSET / FETCH apply. Ordinal items index the output row;
                    // expression items (aggregates, grouped columns, aliases,
                    // grouping expressions) resolve through ResolveOrderName.
                    var orderKeys = new SqlValue[orderByItems.Count];
                    for (var k = 0; k < orderByItems.Count; k++)
                    {
                        orderKeys[k] = orderByItems[k].IsOrdinal
                            ? projected[orderByItems[k].Ordinal - 1]
                            : orderByItems[k].Expr!.Run(new RuntimeContext(name => ResolveOrderName(projected, name), batch));
                    }

                    output.Add((orderKeys, RowEncoder.EncodeRow(outputSchema, projected)));
                }
                finally
                {
                    batch.GroupingSetExpressions = savedSet;
                    batch.AllGroupingExpressions = savedAll;
                }
            }
        }

        // ORDER BY sorts the full grouped stream (across all grouping sets)
        // before any row-count limiting — so TOP / FETCH select the correct
        // rows rather than an arbitrary prefix.
        if (orderByItems.Count > 0)
            output.Sort((a, b) => CompareOrderKeys(a.OrderKeys, b.OrderKeys, orderByItems));

        IEnumerable<(SqlValue[] OrderKeys, byte[] Row)> limited = output;
        if (topCount is { } topLimit)
            limited = limited.Take(topLimit);
        if (offsetCount is { } offset && offset > 0)
            limited = limited.Skip(offset);
        if (fetchCount is { } fetchLimit)
            limited = limited.Take(fetchLimit);

        return [.. limited.Select(o => o.Row)];
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

        /// <summary>
        /// First input row that landed in this group, used to resolve columns
        /// buried inside a grouping expression (constant within the group).
        /// </summary>
        public byte[]?[]? Representative;
    }

    /// <summary>
    /// Hash-key wrapper around a <see cref="SqlValue"/> tuple used as a
    /// dictionary key for GROUP BY buckets. Two NULL slots compare equal
    /// (matching SQL Server: NULL is a valid group key with one bucket).
    /// </summary>
    internal readonly struct SqlValueKey(SqlValue[] values) : IEquatable<SqlValueKey>
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
