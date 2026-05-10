using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Execution-side trunk for <see cref="Selection"/>: the static planner
/// (<see cref="BuildSqlProjection"/>), per-row column resolution, and the
/// non-aggregate / non-window projection paths. Sibling partials own the
/// other phases — set ops (<c>Selection.Execution.SetOps.cs</c>), aggregates
/// (<c>Selection.Execution.Aggregate.cs</c>), windows
/// (<c>Selection.Execution.Window.cs</c>), join enumeration
/// (<c>Selection.Execution.Joins.cs</c>), and ORDER BY key handling
/// (<c>Selection.Execution.OrderBy.cs</c>). The parser-side counterpart lives
/// in <c>Selection.cs</c>.
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
    internal static (int SourceIndex, int ColumnIndex) FindSourceColumn(FromSource[] sources, MultiPartName name)
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
        List<WindowExpression> windows,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool isAssignmentOnly)
    {
        if (windows.Count > 0 && (aggregates.Count > 0 || fromClause.GroupBy.Count > 0 || fromClause.Having is not null))
            throw new NotSupportedException("Combining window functions with GROUP BY / HAVING / aggregates in the same SELECT isn't modeled. EF Core 10 doesn't emit this shape.");
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

        // Pre-resolve operand and result types for any aggregate windows so
        // the runtime path doesn't need a column-type resolver. ROW_NUMBER
        // windows leave both null — they don't carry an operand and have a
        // fixed bigint result.
        var windowOperandTypes = new SqlType[windows.Count];
        var windowResultTypes = new SqlType[windows.Count];
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Kind == WindowKind.Aggregate)
            {
                var aggregate = windows[i].AggregateInfo!;
                windowOperandTypes[i] = aggregate.Operand?.GetSqlType(ResolveColumnType) ?? SqlType.Int32;
                windowResultTypes[i] = windows[i].GetSqlType(ResolveColumnType);
            }
        }

        return new Selection(outputSchema, outputColumnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topCount.HasValue || offsetCount.HasValue || fetchCount.HasValue,
            outerResolver =>
            aggregates.Count > 0 || fromClause.GroupBy.Count > 0 || fromClause.Having is not null
                ? BuildAggregateProjectionRows(sources, joins, ResolveColumnType, expressions, fromClause, outputSchema, aggregates, topCount, offsetCount, fetchCount, outerResolver)
                : windows.Count > 0
                    ? ProjectWindowedRows(sources, joins, expressions, fromClause.Excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, windows, windowOperandTypes, windowResultTypes, outerResolver)
                    : ProjectSqlRows(sources, joins, expressions, fromClause.Excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, outerResolver),
            isAssignmentOnly);
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
}
