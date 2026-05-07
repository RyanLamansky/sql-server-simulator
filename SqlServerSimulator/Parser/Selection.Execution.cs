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
    private static (int SourceIndex, int ColumnIndex) FindSourceColumn(FromSource[] sources, List<string> name)
    {
        if (name.Count >= 2)
        {
            var qualifier = name[^2];
            for (var s = 0; s < sources.Length; s++)
            {
                if (sources[s].Qualifier is null || !Collation.Default.Equals(sources[s].Qualifier, qualifier))
                    continue;
                var lastPart = name[^1];
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (Collation.Default.Equals(sources[s].ColumnNames[c], lastPart))
                        return (s, c);
                }
                // Qualifier matched but the column doesn't exist in that
                // source; fall through to outer (caller handles).
                return (-1, -1);
            }
            // No source's qualifier matches the prefix → outer fallthrough.
            return (-1, -1);
        }

        var unqualified = name[^1];
        var foundSource = -1;
        var foundColumn = -1;
        var matches = 0;
        for (var s = 0; s < sources.Length; s++)
        {
            for (var c = 0; c < sources[s].ColumnNames.Length; c++)
            {
                if (Collation.Default.Equals(sources[s].ColumnNames[c], unqualified))
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
            ? throw SimulatedSqlException.AmbiguousColumnName(unqualified)
            : matches == 1 ? (foundSource, foundColumn) : (-1, -1);
    }

    /// <summary>
    /// Static type-resolution counterpart to <see cref="FindSourceColumn"/>:
    /// returns the column's declared type if it resolves locally across
    /// sources; falls through to <paramref name="outerTypeResolver"/> if
    /// nothing matches; raises Msg 209 on unqualified ambiguity.
    /// </summary>
    private static SqlType ResolveColumnTypeAcrossSources(FromSource[] sources, List<string> name, Func<List<string>, SqlType>? outerTypeResolver)
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
        Func<List<string>, SqlType>? outerTypeResolver)
    {
        var orderBy = fromClause.OrderBy;
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        SqlType ResolveColumnType(List<string> name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

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

        return new Selection(outputSchema, outputColumnNames, outerResolver =>
            aggregates.Count > 0 || fromClause.GroupBy.Count > 0 || fromClause.Having is not null
                ? BuildAggregateProjectionRows(sources, joins, ResolveColumnType, expressions, fromClause, outputSchema, aggregates, topCount, outerResolver)
                : ProjectSqlRows(sources, joins, expressions, fromClause.Excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, outerResolver));
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
        Func<List<string>, SqlType> resolveColumnType,
        List<Expression> expressions,
        FromClause fromClause,
        SqlType[] outputSchema,
        List<AggregateExpression> aggregates,
        int? topCount,
        Func<List<string>, SqlValue>? outerResolver)
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
            SqlValue ResolveColumn(List<string> name) => ResolveAcrossTuple(sources, localTuple, name, outerResolver, ResolveColumn);

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

            SqlValue ResolveByGroupKey(List<string> name)
            {
                for (var i = 0; i < groupByCount; i++)
                {
                    if (groupByExpressions[i] is Reference r
                        && Collation.Default.Equals(r.Name, name[^1]))
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

        if (topCount is { } limit && output.Count > limit)
            output = [.. output.Take(limit)];

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
        Func<List<string>, SqlValue>? outerResolver) =>
        !distinct && orderBy.Count == 0
            ? ProjectStreaming(sources, joins, expressions, excluders, outputSchema, topCount, outerResolver)
            : ProjectBuffered(sources, joins, expressions, excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, outerResolver);

    private static IEnumerable<byte[]> ProjectStreaming(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        int? topCount,
        Func<List<string>, SqlValue>? outerResolver)
    {
        var remaining = topCount;
        foreach (var tuple in EnumerateJoinedRows(sources, joins, outerResolver))
        {
            if (remaining == 0)
                yield break;

            var localTuple = tuple;
            SqlValue ResolveColumn(List<string> name) => ResolveAcrossTuple(sources, localTuple, name, outerResolver, ResolveColumn);

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

            if (remaining is not null)
                remaining--;
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
        Func<List<string>, SqlValue>? outerResolver)
    {
        var buffer = new List<(SqlValue[] Projected, SqlValue[] Keys)>();

        foreach (var tuple in EnumerateJoinedRows(sources, joins, outerResolver))
        {
            var localTuple = tuple;
            SqlValue ResolveSource(List<string> name) => ResolveAcrossTuple(sources, localTuple, name, outerResolver, ResolveSource);

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

        var taken = topCount is { } limit ? materialized.Take(limit) : materialized;
        foreach (var (projected, _) in taken)
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
        List<string> name,
        Func<List<string>, SqlValue>? outerResolver,
        Func<List<string>, SqlValue> selfRecursive)
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
        Func<List<string>, SqlValue>? outerResolver)
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

        SqlValue Resolve(byte[]?[] currentTuple, List<string> name) =>
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
        Func<byte[]?[], List<string>, SqlValue> resolve,
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
        Func<List<string>, SqlValue> resolveByName) =>
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
        Func<List<string>, SqlValue> resolveSource)
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
                var lastPart = name[^1];
                for (var j = 0; j < outputColumnNames.Length; j++)
                {
                    if (Collation.Default.Equals(outputColumnNames[j], lastPart))
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
