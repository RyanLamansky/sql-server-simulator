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
                if (sources[s].Qualifier is null || !BuiltInToken.Equals(sources[s].Qualifier, qualifier))
                    continue;
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
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
                if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
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
        if (s != -1)
        {
            // A HeapColumn can carry its MAX-ness in MaxLength while its .Type
            // stays a length-0 "value-width" variant (catalog-view columns
            // like sys.sql_modules.definition are declared this way). Fold that
            // back in so an expression referencing the column (ISNULL /
            // COALESCE / CASE — SMO reads proc bodies as
            // ISNULL(sql_modules.definition, …)) types as MAX and streams as
            // PLP over the wire, rather than losing MAX to the bounded 2-byte
            // length prefix and overflowing on a large value.
            var column = sources[s].Columns[c];
            return column.MaxLength == SqlType.MaxLengthSentinel
                ? SqlType.AsMaxVariant(column.Type)
                : column.Type;
        }

        return outerTypeResolver is not null
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
    /// and projected through <see cref="Expression.Run(RuntimeContext)"/>.
    /// </summary>
    private static Selection BuildSqlProjection(
        BatchContext parseBatch,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        Expression? topExpression,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool isAssignmentOnly,
        MultiPartName? intoTarget)
    {
        if (windows.Count > 0 && (aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null))
            throw new NotSupportedException("Combining window functions with GROUP BY / HAVING / aggregates in the same SELECT isn't modeled. EF Core 10 doesn't emit this shape.");

        // Convert a comma-join / CROSS JOIN carrying an equi-join predicate in
        // WHERE into an INNER JOIN, so it rides the equi-join seek / hash path
        // instead of the O(L×R) nested loop. Value-independent, so it's done
        // once here and the rewritten array is captured in the cached plan.
        joins = RewriteCommaJoinsToEquiJoins(sources, joins, fromClause.Excluders);

        var orderBy = fromClause.OrderBy;
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        SqlType ResolveColumnType(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].GetSqlType(parseBatch, ResolveColumnType);
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
        // ORDER BY items resolve output-column aliases first (then fall back to
        // source columns), matching SQL Server and the runtime ComputeOrderKeys
        // — so `ORDER BY <select-alias>` and `ORDER BY <aggregate/expression>`
        // type-check here instead of failing as an unknown source column.
        SqlType ResolveOrderByType(MultiPartName name)
        {
            for (var j = 0; j < outputColumnNames.Length; j++)
            {
                if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                    return outputSchema[j];
            }

            return ResolveColumnType(name);
        }

        for (var i = 0; i < orderBy.Count; i++)
        {
            var keyType = orderBy[i].IsOrdinal
                ? outputSchema[orderBy[i].Ordinal - 1]
                : orderBy[i].Expr!.GetSqlType(parseBatch, ResolveOrderByType);
            if (keyType.IsLob)
                throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
        }

        var offsetExpression = fromClause.OffsetExpression;
        var fetchExpression = fromClause.FetchExpression;

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
                windowOperandTypes[i] = aggregate.Operand?.GetSqlType(parseBatch, ResolveColumnType) ?? SqlType.Int32;
                windowResultTypes[i] = windows[i].GetSqlType(parseBatch, ResolveColumnType);
            }
        }

        // SELECT INTO schema inference: when an INTO clause was captured,
        // derive the destination HeapColumn[] now (parse-time) so the
        // dispatch handler can CREATE TABLE before executing. The inference
        // walk also enforces the SELECT-INTO-specific validations
        // (Msg 1038 unnamed projection, Msg 2705 duplicate name).
        var destColumnSchema = intoTarget is { } target
            ? ComputeIntoDestSchema(target, expressions, outputSchema, outputColumnNames, sources, joins)
            : null;

        // Updatable-view shape capture: single source, no JOINs, no DISTINCT,
        // no aggregates / windows / GROUP BY / HAVING. TOP / OFFSET / FETCH are
        // allowed (SQL Server treats TOP views as updatable — probe-confirmed).
        // ORDER BY allowed too (it only affects reads). View.cs consumes this
        // to derive Msg 4403 / 4405 / 4406 metadata at CREATE VIEW.
        var (updatabilityProfile, updatabilityRejection) = ComputeViewUpdatabilityProfile(
            sources, joins, expressions, fromClause, distinct, aggregates, windows);

        var selection = new Selection(outputSchema, outputColumnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topExpression is not null || offsetExpression is not null || fetchExpression is not null,
            (batch, outerResolver) =>
            {
                // Per-execution count resolution: the expressions may carry
                // parameters, and this closure replays across executions of a
                // plan-cached SELECT (EF's Skip/Take shape), so the values
                // must come from the EXECUTING batch, not the parse.
                var topCount = ResolveRowCountLimit(topExpression, RowLimitKind.Top, batch);
                var offsetCount = ResolveRowCountLimit(offsetExpression, RowLimitKind.Offset, batch);
                var fetchCount = ResolveRowCountLimit(fetchExpression, RowLimitKind.Fetch, batch);
                // Materialize provably-uncorrelated catalog-view sources once
                // per execution (before the projection paths build their
                // resolver closures over the array), so a nested-loop join
                // stops re-generating them per outer row and the equi-join
                // hash path can key them. Correlated sources are left untouched.
                var execSources = MaterializeUncorrelatedDeferredSources(sources, batch);
                return aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null
                    ? BuildAggregateProjectionRows(execSources, joins, ResolveColumnType, expressions, fromClause, outputColumnNames, orderBy, aggregates, topCount, offsetCount, fetchCount, batch, outerResolver)
                    : windows.Count > 0
                        ? ProjectWindowedRows(execSources, joins, expressions, fromClause.Excluders, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, windows, windowOperandTypes, windowResultTypes, batch, outerResolver)
                        : ProjectSqlRows(execSources, joins, expressions, fromClause.Excluders, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, batch, outerResolver);
            },
            isAssignmentOnly,
            intoTarget,
            destColumnSchema,
            updatabilityProfile,
            updatabilityRejection);
        // Capture ORDER BY for the updatable-cursor enumeration path; only
        // meaningful when the shape is updatable (single base table).
        if (updatabilityProfile is not null)
            selection.CursorOrderBy = orderBy;
        return selection;
    }

    /// <summary>
    /// Decides whether the FROM-bearing SELECT's shape is eligible to back
    /// view DML and, if so, captures the projection / WHERE state. Eligible
    /// shapes — see <see cref="ViewUpdatabilityProfile"/> — also accept
    /// <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> / <c>ORDER BY</c> (these
    /// only affect reads). Set-op chains are caught one level up in
    /// <see cref="CombineSetOps"/> which discards the profile by
    /// constructing a fresh Selection without one.
    /// </summary>
    private static (ViewUpdatabilityProfile?, ViewUpdatabilityRejection) ComputeViewUpdatabilityProfile(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows)
    {
        if (distinct)
            return (null, ViewUpdatabilityRejection.Distinct);
        if (aggregates.Count > 0)
            return (null, ViewUpdatabilityRejection.Aggregate);
        if (fromClause.GroupingSets.Count > 0 || fromClause.Having is not null)
            return (null, ViewUpdatabilityRejection.GroupBy);
        if (sources.Length != 1 || joins.Length > 0)
            return (null, ViewUpdatabilityRejection.MultipleSources);
        if (windows.Count > 0)
            return (null, ViewUpdatabilityRejection.UnsupportedShape);

        var profile = new ViewUpdatabilityProfile(
            source: sources[0],
            projections: [.. expressions],
            excluders: [.. fromClause.Excluders]);
        return (profile, ViewUpdatabilityRejection.None);
    }

    /// <summary>
    /// Replaces every <see cref="FromSource.MaterializeOnce"/> source — a
    /// provably-uncorrelated catalog view — with a copy whose rows are already
    /// materialized into a re-enumerable list, executing each such plan exactly
    /// once per query execution. Without this, a nested-loop join re-runs the
    /// catalog view's row generator (regenerating every column of every table,
    /// every type, etc.) for each outer row, so an <em>N</em>-column table's
    /// per-column property-bag query costs O(outer × Σ joined-view sizes). After
    /// materialization the source carries a plain <see cref="FromSource.Rows"/>
    /// list, so <c>TryPlanEquiJoin</c> keys it into the O(L + R) hash path and
    /// any residual nested loop re-scans the list instead of re-generating.
    /// Correlated / lateral sources never set the flag, so APPLY, correlated
    /// derived tables, VALUES, TVFs, and views keep their per-outer-row
    /// execution untouched. Returns the input array unchanged (no copy) when no
    /// source qualifies.
    /// </summary>
    private static FromSource[] MaterializeUncorrelatedDeferredSources(FromSource[] sources, BatchContext batch)
    {
        FromSource[]? rewritten = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i] is not { MaterializeOnce: true, LateralPlan: { } plan })
                continue;
            // Uncorrelated by construction: the generator ignores the outer
            // resolver, so a null resolver produces identical rows.
            var materialized = new List<byte[]>(plan.Execute(batch, outerResolver: null).RowBytes);
            rewritten ??= (FromSource[])sources.Clone();
            rewritten[i] = sources[i].WithMaterializedRows(materialized);
        }
        return rewritten ?? sources;
    }

    private static IEnumerable<SqlValue[]> ProjectSqlRows(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        // ORDER BY elimination: when the sort matches a NOT-NULL leading-key
        // column, enumerate the source in key order and stream (no buffer + sort).
        // Residual WHERE and projection preserve order; OFFSET / FETCH / TOP then
        // read only the rows they need.
        if (!distinct && orderBy.Count > 0
            && TryApplyOrderedScan(sources, joins, orderBy, excluders, batch, outerResolver, out var orderedSources))
        {
            return ProjectStreaming(orderedSources, joins, expressions, excluders, topCount, offsetCount, fetchCount, batch, outerResolver);
        }

        sources = MaybeApplyIndexSeek(sources, joins, excluders, batch, outerResolver);
        sources = NarrowLeftmostJoinSource(sources, excluders, batch, outerResolver);
        return !distinct && orderBy.Count == 0
            ? ProjectStreaming(sources, joins, expressions, excluders, topCount, offsetCount, fetchCount, batch, outerResolver)
            : ProjectBuffered(sources, joins, expressions, excluders, outputColumnNames, orderBy, distinct, topCount, offsetCount, fetchCount, batch, outerResolver);
    }

    /// <summary>
    /// Applies OFFSET (skip) and the row cap (take) to a row sequence in
    /// that order. The cap is whichever of TOP / FETCH is in play —
    /// they're mutually exclusive at parse time (Msg 10741), so callers
    /// pass <c>topCount ?? fetchCount</c> here.
    /// </summary>
    private static IEnumerable<T> ApplyOffsetTake<T>(IEnumerable<T> rows, int? offsetCount, int? topOrFetch)
    {
        if (offsetCount is { } offset && offset > 0)
            rows = rows.Skip(offset);
        if (topOrFetch is { } limit)
            rows = rows.Take(limit);
        return rows;
    }

    private static IEnumerable<SqlValue[]> ProjectStreaming(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        return ApplyOffsetTake(InnerStream(), offsetCount, topCount ?? fetchCount);

        IEnumerable<SqlValue[]> InnerStream()
        {
            // Hoisted per-row resolution scaffolding: one mutable-capture
            // tuple slot, one cached self-referencing resolver lambda, one
            // RuntimeContext — instead of a fresh closure + several delegates
            // per row (the allocation profile's dominant entry).
            var memo = new SourceColumnMemo();
            var currentTuple = default(byte[]?[])!;
            Func<MultiPartName, SqlValue> resolveColumn = null!;
            resolveColumn = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, resolveColumn, memo);
            var rowRuntime = new RuntimeContext(resolveColumn, batch);
            foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
            {
                currentTuple = tuple;
                var include = true;
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(rowRuntime) != true)
                    {
                        include = false;
                        break;
                    }
                }
                if (!include)
                    continue;

                // Per-row stamp bump so NEXT VALUE FOR in the projection
                // advances per output row (and dedupes across same-row
                // instances). Bump only on rows that pass WHERE — excluded
                // rows shouldn't burn sequence values.
                batch.BumpRowStamp();
                var projected = new SqlValue[expressions.Count];
                for (var i = 0; i < expressions.Count; i++)
                    projected[i] = expressions[i].Run(rowRuntime);

                yield return projected;
            }
        }
    }

    private static IEnumerable<SqlValue[]> ProjectBuffered(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var buffer = new List<(SqlValue[] Projected, SqlValue[] Keys)>();

        // Hoisted per-row resolution scaffolding — see InnerStream above.
        var memo = new SourceColumnMemo();
        var currentTuple = default(byte[]?[])!;
        Func<MultiPartName, SqlValue> resolveSource = null!;
        resolveSource = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, resolveSource, memo);
        var rowRuntime = new RuntimeContext(resolveSource, batch);
        foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
        {
            currentTuple = tuple;
            var include = true;
            foreach (var excluder in excluders)
            {
                if (excluder.Run(rowRuntime) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            // Per-row stamp bump — same rule as the streaming path: only
            // for rows that pass WHERE.
            batch.BumpRowStamp();
            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(rowRuntime);

            var keys = orderBy.Count == 0 ? [] : ComputeOrderKeys(orderBy, projected, outputColumnNames, distinct, batch, resolveSource);
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
            yield return projected;
    }
}
