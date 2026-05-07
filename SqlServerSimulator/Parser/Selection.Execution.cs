using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Execution-side helpers for <see cref="Selection"/>: row pipeline (single
/// or joined sources), projection paths (streaming, buffered, aggregate),
/// and per-row column resolution. The parser-side counterpart lives in
/// <c>Selection.cs</c>; the two halves share private members through C#'s
/// partial-class mechanism.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Locates a column reference across all FROM sources. A qualified
    /// reference (<c>alias.col</c> / <c>tableName.col</c>) restricts the
    /// search to the source whose <see cref="FromSource.Qualifier"/>
    /// matches; an unqualified reference searches all sources and raises
    /// <see cref="SimulatedSqlException.AmbiguousColumnName"/> (Msg 209)
    /// if the column name appears in more than one. Returns
    /// <c>(-1, -1)</c> when no source resolves the name — the caller then
    /// falls through to the outer scope.
    /// </summary>
    private static (int SourceIndex, int ColumnIndex) FindSourceColumn(FromSource[] sources, MultiPartName name)
    {
        if (name.ImmediateQualifier is { } qualifier)
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (sources[s].Qualifier is null || !Collation.Default.Equals(sources[s].Qualifier, qualifier))
                    continue;
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (Collation.Default.Equals(sources[s].ColumnNames[c], name.Leaf))
                        return (s, c);
                }
                // Qualifier matched but the column doesn't exist in that
                // source; fall through to outer (caller handles).
                return (-1, -1);
            }
            // No source's qualifier matches the prefix → outer fallthrough.
            return (-1, -1);
        }

        var foundSource = -1;
        var foundColumn = -1;
        var matches = 0;
        for (var s = 0; s < sources.Length; s++)
        {
            for (var c = 0; c < sources[s].ColumnNames.Length; c++)
            {
                if (Collation.Default.Equals(sources[s].ColumnNames[c], name.Leaf))
                {
                    if (matches == 0)
                    {
                        foundSource = s;
                        foundColumn = c;
                    }
                    matches++;
                }
            }
        }
        return matches > 1
            ? throw SimulatedSqlException.AmbiguousColumnName(name.Leaf)
            : matches == 1 ? (foundSource, foundColumn) : (-1, -1);
    }

    /// <summary>
    /// Static type-resolution counterpart to <see cref="FindSourceColumn"/>:
    /// returns the column's declared type if it resolves locally across
    /// sources; falls through to <paramref name="outerTypeResolver"/> if
    /// nothing matches; raises Msg 209 on unqualified ambiguity.
    /// </summary>
    private static SqlType ResolveColumnTypeAcrossSources(FromSource[] sources, MultiPartName name, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var (s, c) = FindSourceColumn(sources, name);
        return s != -1
            ? sources[s].Columns[c].Type
            : outerTypeResolver is not null
                ? outerTypeResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
    }

    /// <summary>
    /// Builds the plan for a SELECT whose FROM clause has at least one
    /// source (and possibly JOINs). Static work — output schema, validation
    /// of ordinal ORDER BY items, LOB-in-DISTINCT/ORDER-BY checks — happens
    /// here. The deferred closure runs per <see cref="Execute"/> call,
    /// accepting the outer-row resolver and dispatching to the aggregate or
    /// simple projection path; each row tuple (one byte[] per source, null
    /// in unmatched LEFT-JOIN slots) is decoded column-by-column on demand
    /// and projected through <see cref="Expression.Run"/>.
    /// </summary>
    private static Selection BuildSqlProjection(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        int? topCount,
        List<AggregateExpression> aggregates,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var orderBy = fromClause.OrderBy;
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        SqlType ResolveColumnType(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].GetSqlType(ResolveColumnType);
            outputColumnNames[i] = expressions[i].Name;
        }

        // Validate ordinal ORDER BY items now that the projection count is
        // known. SQL Server fires Msg 108 at parse time, before any rows are
        // touched, so do the same.
        for (var i = 0; i < orderBy.Count; i++)
        {
            if (orderBy[i].IsOrdinal && (orderBy[i].Ordinal < 1 || orderBy[i].Ordinal > expressions.Count))
                throw SimulatedSqlException.OrderByPositionOutOfRange(orderBy[i].Ordinal);
        }

        // Msg 306: text/ntext/image can't appear in a sort or distinct slot.
        if (distinct)
        {
            for (var i = 0; i < outputSchema.Length; i++)
            {
                if (outputSchema[i].IsLob)
                    throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
            }
        }
        for (var i = 0; i < orderBy.Count; i++)
        {
            var keyType = orderBy[i].IsOrdinal
                ? outputSchema[orderBy[i].Ordinal - 1]
                : orderBy[i].Expr!.GetSqlType(ResolveColumnType);
            if (keyType.IsLob)
                throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
        }

        var offsetCount = fromClause.OffsetCount;
        var fetchCount = fromClause.FetchCount;

        return new Selection(outputSchema, outputColumnNames, hasOrderBy: orderBy.Count > 0, outerResolver =>
            aggregates.Count > 0 || fromClause.GroupBy.Count > 0 || fromClause.Having is not null
                ? BuildAggregateProjectionRows(sources, joins, ResolveColumnType, expressions, fromClause, outputSchema, aggregates, topCount, offsetCount, fetchCount, outerResolver)
                : ProjectSqlRows(sources, joins, expressions, fromClause.Excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, outerResolver));
    }

    /// <summary>
    /// Combines two SELECT plans via a set operator (UNION / UNION ALL /
    /// INTERSECT / EXCEPT). Validates that the branches have the same
    /// column count (Msg 205), promotes per-column types via
    /// <see cref="SqlType.Promote"/>, and rejects per-branch ORDER BY
    /// (which the parser tolerates greedily for the first branch via
    /// <see cref="HasOrderBy"/>; if it stuck around when a set operator
    /// follows, that's a syntax error).
    /// </summary>
    private static Selection CombineSetOps(Selection left, Selection right, SetOpKind kind)
    {
        if (left.HasOrderBy)
        {
            var setOpKeyword = kind switch
            {
                SetOpKind.Union or SetOpKind.UnionAll => "union",
                SetOpKind.Intersect => "intersect",
                SetOpKind.Except => "except",
                _ => throw new InvalidOperationException($"Unknown SetOpKind {kind}."),
            };
            throw SimulatedSqlException.PerBranchOrderByRejected(setOpKeyword);
        }

        if (left.Schema.Length != right.Schema.Length)
            throw SimulatedSqlException.SetOpUnequalColumnCount();

        var combinedSchema = new SqlType[left.Schema.Length];
        for (var i = 0; i < combinedSchema.Length; i++)
            combinedSchema[i] = SqlType.Promote(left.Schema[i], right.Schema[i]);

        // Result column names come from the first (leftmost) branch.
        var combinedNames = left.ColumnNames;

        return new Selection(combinedSchema, combinedNames, hasOrderBy: false, outerResolver => kind switch
        {
            SetOpKind.UnionAll => ConcatBranchRows(left, right, combinedSchema, outerResolver),
            SetOpKind.Union => DedupeUnionRows(left, right, combinedSchema, outerResolver),
            SetOpKind.Intersect => IntersectRows(left, right, combinedSchema, outerResolver),
            SetOpKind.Except => ExceptRows(left, right, combinedSchema, outerResolver),
            _ => throw new InvalidOperationException($"Unknown SetOpKind {kind}."),
        });
    }

    /// <summary>
    /// Materializes a branch's rows and coerces each value to the
    /// combined schema's per-column type. Pass-through fast path when
    /// the branch's schema already matches; otherwise decode, coerce,
    /// re-encode each row.
    /// </summary>
    private static IEnumerable<byte[]> CoerceBranchRows(Selection branch, SqlType[] targetSchema, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resultSet = branch.Execute(outerResolver);
        var sourceSchema = resultSet.Schema;

        var sameTypes = true;
        for (var i = 0; i < sourceSchema.Length; i++)
        {
            if (sourceSchema[i] != targetSchema[i]) { sameTypes = false; break; }
        }

        if (sameTypes)
        {
            foreach (var rowBytes in resultSet.RowBytes)
                yield return rowBytes;
            yield break;
        }

        foreach (var rowBytes in resultSet.RowBytes)
        {
            var values = new SqlValue[targetSchema.Length];
            for (var i = 0; i < sourceSchema.Length; i++)
            {
                var v = RowDecoder.DecodeColumn(sourceSchema, rowBytes, i);
                values[i] = v.IsNull ? SqlValue.Null(targetSchema[i]) : v.CoerceTo(targetSchema[i]);
            }
            yield return RowEncoder.EncodeRow(targetSchema, values);
        }
    }

    private static IEnumerable<byte[]> ConcatBranchRows(Selection left, Selection right, SqlType[] schema, Func<MultiPartName, SqlValue>? outer)
    {
        foreach (var r in CoerceBranchRows(left, schema, outer)) yield return r;
        foreach (var r in CoerceBranchRows(right, schema, outer)) yield return r;
    }

    /// <summary>
    /// Decodes a row's bytes into a <see cref="SqlValue"/> array using
    /// the combined schema. Used by the dedup-aware set ops (UNION /
    /// INTERSECT / EXCEPT) for HashSet keying via
    /// <see cref="RowEqualityComparer"/>.
    /// </summary>
    private static SqlValue[] DecodeRowToValues(byte[] bytes, SqlType[] schema)
    {
        var values = new SqlValue[schema.Length];
        for (var i = 0; i < schema.Length; i++)
            values[i] = RowDecoder.DecodeColumn(schema, bytes, i);
        return values;
    }

    private static IEnumerable<byte[]> DedupeUnionRows(Selection left, Selection right, SqlType[] schema, Func<MultiPartName, SqlValue>? outer)
    {
        var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rowBytes in CoerceBranchRows(left, schema, outer).Concat(CoerceBranchRows(right, schema, outer)))
        {
            if (seen.Add(DecodeRowToValues(rowBytes, schema)))
                yield return rowBytes;
        }
    }

    private static IEnumerable<byte[]> IntersectRows(Selection left, Selection right, SqlType[] schema, Func<MultiPartName, SqlValue>? outer)
    {
        var rightSet = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rb in CoerceBranchRows(right, schema, outer))
            _ = rightSet.Add(DecodeRowToValues(rb, schema));

        var emitted = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rowBytes in CoerceBranchRows(left, schema, outer))
        {
            var values = DecodeRowToValues(rowBytes, schema);
            if (rightSet.Contains(values) && emitted.Add(values))
                yield return rowBytes;
        }
    }

    private static IEnumerable<byte[]> ExceptRows(Selection left, Selection right, SqlType[] schema, Func<MultiPartName, SqlValue>? outer)
    {
        var rightSet = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rb in CoerceBranchRows(right, schema, outer))
            _ = rightSet.Add(DecodeRowToValues(rb, schema));

        var emitted = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rowBytes in CoerceBranchRows(left, schema, outer))
        {
            var values = DecodeRowToValues(rowBytes, schema);
            if (!rightSet.Contains(values) && emitted.Add(values))
                yield return rowBytes;
        }
    }

    /// <summary>
    /// Wraps a Selection (typically the combined result of a set-op
    /// chain) with a top-level ORDER BY pass. References resolve against
    /// the inner plan's projected column names and ordinals only — there
    /// are no source columns to fall back to. (Single-SELECT queries
    /// with ORDER BY take a different path: ORDER BY stays inside the
    /// branch's projection so it can reference non-projected source
    /// columns, matching SQL Server's documented behavior.)
    /// </summary>
    private static Selection ApplyTopLevelOrderBy(Selection inner, List<OrderBySpec> orderBy, int? offsetCount, int? fetchCount)
    {
        var schema = inner.Schema;
        var columnNames = inner.ColumnNames;

        return new Selection(schema, columnNames, hasOrderBy: true, outerResolver =>
        {
            var allRows = inner.Execute(outerResolver).RowBytes.ToList();

            IEnumerable<byte[]> ordered;
            if (orderBy.Count == 0 || allRows.Count <= 1)
            {
                ordered = allRows;
            }
            else
            {
                var keyed = new List<(byte[] Row, SqlValue[] Keys)>(allRows.Count);
                foreach (var rowBytes in allRows)
                {
                    var values = DecodeRowToValues(rowBytes, schema);
                    SqlValue ResolveByOutputName(MultiPartName name)
                    {
                        for (var j = 0; j < columnNames.Length; j++)
                        {
                            if (Collation.Default.Equals(columnNames[j], name.Leaf))
                                return values[j];
                        }
                        throw SimulatedSqlException.InvalidColumnName(name);
                    }

                    var keys = ComputeOrderKeys(orderBy, values, columnNames, distinct: false, ResolveByOutputName);
                    keyed.Add((rowBytes, keys));
                }

                keyed.Sort((a, b) => CompareOrderKeys(a.Keys, b.Keys, orderBy));
                ordered = keyed.Select(r => r.Row);
            }

            return ApplyOffsetTake(ordered, offsetCount, fetchCount);
        });
    }

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

    private static IEnumerable<byte[]> ProjectSqlRows(
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
        Func<MultiPartName, SqlValue>? outerResolver) =>
        !distinct && orderBy.Count == 0
            ? ProjectStreaming(sources, joins, expressions, excluders, outputSchema, topCount, offsetCount, fetchCount, outerResolver)
            : ProjectBuffered(sources, joins, expressions, excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, outerResolver);

    /// <summary>
    /// Applies OFFSET (skip) and the row cap (take) to a row sequence in
    /// that order. The cap is whichever of TOP / FETCH is in play —
    /// they're mutually exclusive at parse time (Msg 10741), so callers
    /// pass <c>topCount ?? fetchCount</c> here.
    /// </summary>
    private static IEnumerable<byte[]> ApplyOffsetTake(IEnumerable<byte[]> rows, int? offsetCount, int? topOrFetch)
    {
        if (offsetCount is { } offset && offset > 0)
            rows = rows.Skip(offset);
        if (topOrFetch is { } limit)
            rows = rows.Take(limit);
        return rows;
    }

    private static IEnumerable<byte[]> ProjectStreaming(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        return ApplyOffsetTake(InnerStream(), offsetCount, topCount ?? fetchCount);

        IEnumerable<byte[]> InnerStream()
        {
            foreach (var tuple in EnumerateJoinedRows(sources, joins, outerResolver))
            {
                var localTuple = tuple;
                SqlValue ResolveColumn(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, outerResolver, ResolveColumn);

                var include = true;
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(ResolveColumn) != true)
                    {
                        include = false;
                        break;
                    }
                }
                if (!include)
                    continue;

                var projected = new SqlValue[expressions.Count];
                for (var i = 0; i < expressions.Count; i++)
                    projected[i] = expressions[i].Run(ResolveColumn);

                yield return RowEncoder.EncodeRow(outputSchema, projected);
            }
        }
    }

    private static IEnumerable<byte[]> ProjectBuffered(
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
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var buffer = new List<(SqlValue[] Projected, SqlValue[] Keys)>();

        foreach (var tuple in EnumerateJoinedRows(sources, joins, outerResolver))
        {
            var localTuple = tuple;
            SqlValue ResolveSource(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, outerResolver, ResolveSource);

            var include = true;
            foreach (var excluder in excluders)
            {
                if (excluder.Run(ResolveSource) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(ResolveSource);

            var keys = orderBy.Count == 0 ? [] : ComputeOrderKeys(orderBy, projected, outputColumnNames, distinct, ResolveSource);
            buffer.Add((projected, keys));
        }

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> filtered = buffer;
        if (distinct)
        {
            var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
            filtered = buffer.Where(item => seen.Add(item.Projected));
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
            : DecodeOrCompute(sources[s], c, bytes, selfRecursive);
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
    private static IEnumerable<byte[]?[]> EnumerateJoinedRows(
        FromSource[] sources,
        JoinSpec[] joins,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var tuple = new byte[]?[sources.Length];

        if (joins.Length == 0)
        {
            foreach (var row in sources[0].Rows)
            {
                tuple[0] = row;
                yield return tuple;
            }
            yield break;
        }

        SqlValue Resolve(byte[]?[] currentTuple, MultiPartName name) =>
            ResolveAcrossTuple(sources, currentTuple, name, outerResolver, n => Resolve(currentTuple, n));

        foreach (var t in JoinDriver(sources, joins, tuple, Resolve, level: 0))
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
        int level)
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
                foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1))
                    yield return t;
            }
            yield break;
        }

        var join = joins[level - 1];
        var matched = false;

        // Lateral source (right side of CROSS APPLY / OUTER APPLY): the
        // plan re-executes per outer tuple, and its result rows are this
        // level's contribution to the join. No ON predicate — correlation
        // lives inside the lateral plan's own WHERE clause.
        if (sources[level].LateralPlan is { } lateralPlan)
        {
            foreach (var row in lateralPlan.Execute(name => resolve(tuple, name)).RowBytes)
            {
                tuple[level] = row;
                matched = true;
                foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1))
                    yield return t;
            }
            tuple[level] = null;
            if (!matched && join.Kind == JoinKind.OuterApply)
            {
                foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1))
                    yield return t;
            }
            yield break;
        }

        foreach (var row in sources[level].Rows)
        {
            tuple[level] = row;
            var passes = join.OnPredicate is null || join.OnPredicate.Run(name => resolve(tuple, name)) == true;
            if (!passes)
                continue;
            matched = true;
            foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1))
                yield return t;
        }
        tuple[level] = null;
        if (!matched && join.Kind == JoinKind.Left)
        {
            foreach (var t in JoinDriver(sources, joins, tuple, resolve, level + 1))
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
        Func<MultiPartName, SqlValue> resolveByName) =>
        source.StorageOrdinals is null
            ? RowDecoder.DecodeColumn(source.StoredSchema, bytes, columnIndex, source.LobStore)
            : source.Columns[columnIndex].Computed is { } computedExpr && !source.Columns[columnIndex].IsPersisted
                ? computedExpr.Run(resolveByName)
                : RowDecoder.DecodeColumn(source.StoredSchema, bytes, source.StorageOrdinals[columnIndex], source.LobStore);

    /// <summary>
    /// Evaluates each ORDER BY item against the current row. Ordinal items
    /// index directly into the projected row. Expression items resolve column
    /// references through an output-first resolver; without DISTINCT, names
    /// not in the output fall back to source columns (matching SQL Server's
    /// rule that ORDER BY can reference non-selected source columns). With
    /// DISTINCT, source fallback would be ambiguous post-dedup so a missing
    /// output match raises Msg 145.
    /// </summary>
    private static SqlValue[] ComputeOrderKeys(
        List<OrderBySpec> orderBy,
        SqlValue[] projected,
        string[] outputColumnNames,
        bool distinct,
        Func<MultiPartName, SqlValue> resolveSource)
    {
        var keys = new SqlValue[orderBy.Count];
        for (var i = 0; i < orderBy.Count; i++)
        {
            var spec = orderBy[i];
            if (spec.IsOrdinal)
            {
                keys[i] = projected[spec.Ordinal - 1];
                continue;
            }

            keys[i] = spec.Expr!.Run(name =>
            {
                for (var j = 0; j < outputColumnNames.Length; j++)
                {
                    if (Collation.Default.Equals(outputColumnNames[j], name.Leaf))
                        return projected[j];
                }
                return distinct
                    ? throw SimulatedSqlException.OrderByItemNotInSelectListWithDistinct()
                    : resolveSource(name);
            });
        }
        return keys;
    }

    /// <summary>
    /// Lexicographic compare of two key tuples per the per-key descending
    /// flags. NULL is treated as the smallest value (NULL first under ASC,
    /// NULL last under DESC), matching SQL Server. Cross-type keys are
    /// promoted via <see cref="SqlType.Promote"/> before comparison.
    /// </summary>
    private static int CompareOrderKeys(SqlValue[] a, SqlValue[] b, List<OrderBySpec> orderBy)
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

            if (orderBy[i].Descending)
                c = -c;
            if (c != 0)
                return c;
        }
        return 0;
    }
}

/// <summary>
/// Equality comparer for projected rows (<see cref="SqlValue"/> tuples). Used
/// by DISTINCT to dedupe based on the same equality semantics as the
/// <c>=</c> operator: collation-aware string comparison, ANSI trailing-space
/// padding, two NULLs of the same type compare equal, and
/// <c>datetimeoffset</c> compares by UTC instant.
/// </summary>
internal sealed class RowEqualityComparer : IEqualityComparer<SqlValue[]>
{
    public static readonly RowEqualityComparer Instance = new();

    public bool Equals(SqlValue[]? x, SqlValue[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (!x[i].Equals(y[i])) return false;
        return true;
    }

    public int GetHashCode(SqlValue[] obj)
    {
        var hash = new HashCode();
        foreach (var v in obj)
            hash.Add(v);
        return hash.ToHashCode();
    }
}
