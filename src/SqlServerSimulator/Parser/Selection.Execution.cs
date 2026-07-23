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
        // Ambiguity across real sources is a compile error — unless a
        // placeholder source is in scope, in which case real SQL Server defers
        // the whole statement's binding (the missing object could own the name),
        // so bind to the first match and let the discarded statement carry on.
        return matches > 1
            ? AnyPlaceholderSource(sources) ? (foundSource, foundColumn) : throw SimulatedSqlException.AmbiguousColumnName(name.Leaf)
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

        // A placeholder source (skip-mode stand-in for an unresolvable table)
        // means real SQL Server would defer this whole statement's binding, so
        // an unresolved column can't be a compile error — it just belongs to
        // the missing object. Return a placeholder type; the statement is
        // discarded before execution. Without a placeholder in scope, a genuine
        // missing column on a resolvable table stays a Msg 207 even in skip mode
        // (probe-confirmed: real SQL Server errors at compile time here).
        return AnyPlaceholderSource(sources)
            ? SqlType.Int32
            : outerTypeResolver is not null
                ? outerTypeResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
    }

    /// <summary>
    /// True when any source in the set is a skip-mode placeholder (a stand-in
    /// for an unresolvable table). See <see cref="FromSource.IsPlaceholder"/>.
    /// </summary>
    internal static bool AnyPlaceholderSource(FromSource[] sources)
    {
        foreach (var source in sources)
        {
            if (source.IsPlaceholder)
                return true;
        }
        return false;
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
    /// <summary>
    /// Finds a WHERE equality that can be pushed into the leftmost catalog-view
    /// source's row generator. Eligible when <c>sources[0]</c> is a
    /// pushdown-aware catalog view and some top-level AND-conjunct is
    /// <c>&lt;key&gt; = &lt;comparand&gt;</c> (either operand order) where the key
    /// is one of the view's <see cref="Schemas.CatalogView.PushdownColumns"/>
    /// qualified to this source, and the comparand is row-independent (reads no
    /// column, so it evaluates to one value for the whole scan). Returns the
    /// source, the canonical key-column name, and the comparand; null when
    /// nothing qualifies. Only the leftmost source is considered — a catalog view
    /// deeper in a JOIN keeps its full scan.
    /// </summary>
    private static (FromSource Source, string Column, Expression Comparand)? DetectCatalogPushdown(
        FromSource[] sources,
        List<BooleanExpression> excluders)
    {
        if (sources.Length == 0)
            return null;
        var source = sources[0];
        if (source.BackingCatalogView is not { PushdownColumns: { } pushColumns, FilteredRowGenerator: not null })
            return null;

        var singleSource = sources.Length == 1;
        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        foreach (var conjunct in conjuncts)
        {
            if (!conjunct.TryGetEqualityOperands(out var left, out var right))
                continue;
            if (MatchPushdownKey(left, right, source.Qualifier, singleSource, pushColumns) is { } forward)
                return (source, forward.Column, forward.Comparand);
            if (MatchPushdownKey(right, left, source.Qualifier, singleSource, pushColumns) is { } reversed)
                return (source, reversed.Column, reversed.Comparand);
        }
        return null;
    }

    // When `keySide` is a column reference to the catalog source naming one of
    // `pushColumns`, and `valueSide` is row-independent, returns the canonical
    // column name (from `pushColumns`) paired with the comparand; else null.
    private static (string Column, Expression Comparand)? MatchPushdownKey(
        Expression keySide,
        Expression valueSide,
        string? sourceQualifier,
        bool singleSource,
        string[] pushColumns)
    {
        if (keySide is not Reference reference || !valueSide.IsRowIndependent)
            return null;

        var name = reference.ReferencedName;
        // Qualified (`c.object_id`) must match this source's alias / view name;
        // unqualified (`object_id`) is only unambiguous when it's the sole source.
        var qualifier = name.ImmediateQualifier;
        var qualifierMatches = qualifier is null ? singleSource : BuiltInToken.Equals(qualifier, sourceQualifier);
        if (!qualifierMatches)
            return null;

        foreach (var pushColumn in pushColumns)
        {
            if (BuiltInToken.Equals(pushColumn, name.Leaf))
                return (pushColumn, valueSide);
        }
        return null;
    }

    private static Selection BuildSqlProjection(
        BatchContext parseBatch,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        Expression? topExpression,
        bool topPercent,
        bool topWithTies,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool isAssignmentOnly,
        MultiPartName? intoTarget,
        Dictionary<int, (Storage.HeapTable Table, HashSet<int> Columns)>? readColumnSink)
    {
        if (windows.Count > 0 && (aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null))
            throw new NotSupportedException("Combining window functions with GROUP BY / HAVING / aggregates in the same SELECT isn't modeled. EF Core 10 doesn't emit this shape.");

        // Convert a comma-join / CROSS JOIN carrying an equi-join predicate in
        // WHERE into an INNER JOIN, so it rides the equi-join seek / hash path
        // instead of the O(L×R) nested loop. Value-independent, so it's done
        // once here and the rewritten array is captured in the cached plan.
        // A parenthesized join group's connecting join spans multiple slots;
        // the comma→equi rewrite assumes a single-source right operand, so skip
        // it when a group is present (the group folds via the nested-loop path).
        if (!ContainsJoinGroup(joins))
            joins = RewriteCommaJoinsToEquiJoins(sources, joins, fromClause.Excluders);

        // Catalog-view predicate pushdown: when the leftmost source is a
        // pushdown-aware catalog view (sys.columns etc.) and WHERE carries a
        // top-level `<key> = <row-independent comparand>` conjunct, rebuild the
        // source's generator plan so it enumerates only matching objects instead
        // of materializing every row. Value-independent decision (compiled into
        // the shared plan); the comparand's value is resolved per execution. The
        // full WHERE still runs as a residual filter, so this can only narrow the
        // generator output, never change the result.
        if (DetectCatalogPushdown(sources, fromClause.Excluders) is (var pushSource, var pushColumn, var pushComparand))
        {
            sources[0] = new FromSource(
                qualifier: pushSource.Qualifier,
                columnNames: pushSource.ColumnNames,
                columns: pushSource.Columns,
                storedSchema: pushSource.StoredSchema,
                storageOrdinals: pushSource.StorageOrdinals,
                lobStore: pushSource.LobStore,
                rows: pushSource.Rows,
                lateralPlan: ForCatalogView(pushSource.BackingCatalogView!, pushSource.BackingCatalogDatabase!, pushColumn, pushComparand),
                materializeOnce: true,
                backingCatalogView: pushSource.BackingCatalogView,
                backingCatalogDatabase: pushSource.BackingCatalogDatabase);
        }

        var orderBy = fromClause.OrderBy;
        if (topWithTies && orderBy.Count == 0)
            throw SimulatedSqlException.TopWithTiesRequiresOrderBy();
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        SqlType ResolveColumnType(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        // Column-level read tracking (parse-time, principal-independent): record
        // every base-table column this query reads into the shared sink so the
        // execution-time column-level SELECT check (Msg 230 / 229) can run
        // against the current principal. Pre-seed each base-table source with an
        // empty ordinal set — a table read that names no column (COUNT(*) /
        // SELECT 1) then routes through the column path as "all columns". The
        // projection funnels through RecordingResolver (recording is free — the
        // schema resolution already visits these references); WHERE / JOIN ON /
        // GROUP BY / HAVING / ORDER BY / aggregate operands are walked
        // structurally below. The runtime row closure keeps the non-recording
        // ResolveColumnType, so this adds nothing to execution.
        void RecordReadColumn(MultiPartName name)
        {
            if (readColumnSink is null)
                return;
            // Best-effort, non-throwing resolution: unlike FindSourceColumn this
            // silently skips an unresolved (correlated / outer) or ambiguous name
            // rather than raising Msg 207 / 209 — recording must never alter query
            // semantics. A qualified name binds to its one qualifier-matching
            // source; an unqualified name binds only on a single match.
            var matchSource = -1;
            var matchColumn = -1;
            var matches = 0;
            var qualifier = name.ImmediateQualifier;
            for (var s = 0; s < sources.Length; s++)
            {
                if (qualifier is not null && (sources[s].Qualifier is null || !BuiltInToken.Equals(sources[s].Qualifier, qualifier)))
                    continue;
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
                    {
                        matchSource = s;
                        matchColumn = c;
                        matches++;
                    }
                }
                if (qualifier is not null)
                    break;
            }
            if (matches != 1 || sources[matchSource].BackingTable is not { } table || table.Name.StartsWith('#'))
                return;
            if (!readColumnSink.TryGetValue(table.ObjectId, out var entry))
                readColumnSink[table.ObjectId] = entry = (table, []);
            _ = entry.Columns.Add(matchColumn + 1);
        }
        SqlType RecordingResolver(MultiPartName name)
        {
            RecordReadColumn(name);
            return ResolveColumnType(name);
        }
        if (readColumnSink is not null)
        {
            foreach (var source in sources)
            {
                if (source.BackingTable is { } table && !table.Name.StartsWith('#'))
                    _ = readColumnSink.TryAdd(table.ObjectId, (table, []));
            }
        }

        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].GetSqlType(parseBatch, readColumnSink is null ? ResolveColumnType : RecordingResolver);
            outputColumnNames[i] = expressions[i].Name;
        }

        if (readColumnSink is not null)
        {
            foreach (var aggregate in aggregates)
                aggregate.Operand?.VisitColumnReferences(RecordReadColumn);
            foreach (var window in windows)
                window.AggregateInfo?.Operand?.VisitColumnReferences(RecordReadColumn);
            foreach (var excluder in fromClause.Excluders)
                excluder.VisitOperandExpressions(op => op.VisitColumnReferences(RecordReadColumn));
            fromClause.Having?.VisitOperandExpressions(op => op.VisitColumnReferences(RecordReadColumn));
            foreach (var grouping in fromClause.AllGroupingExpressions)
                grouping.VisitColumnReferences(RecordReadColumn);
            foreach (var orderItem in orderBy)
                orderItem.Expr?.VisitColumnReferences(RecordReadColumn);
            foreach (var join in joins)
                join.OnPredicate?.VisitOperandExpressions(op => op.VisitColumnReferences(RecordReadColumn));
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

        var columnNullability = ComputeColumnNullability(expressions, sources, joins);

        var selection = new Selection(outputSchema, outputColumnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topExpression is not null || offsetExpression is not null || fetchExpression is not null,
            (batch, outerResolver) =>
            {
                // Per-execution count resolution: the expressions may carry
                // parameters, and this closure replays across executions of a
                // plan-cached SELECT (EF's Skip/Take shape), so the values
                // must come from the EXECUTING batch, not the parse.
                var top = topExpression is null
                    ? default
                    : topPercent
                        ? new TopSpec(null, ResolveTopPercentValue(topExpression, batch), topWithTies)
                        : new TopSpec(ResolveRowCountLimit(topExpression, RowLimitKind.Top, batch), null, topWithTies);
                var offsetCount = ResolveRowCountLimit(offsetExpression, RowLimitKind.Offset, batch);
                var fetchCount = ResolveRowCountLimit(fetchExpression, RowLimitKind.Fetch, batch);
                // Materialize provably-uncorrelated catalog-view sources once
                // per execution (before the projection paths build their
                // resolver closures over the array), so a nested-loop join
                // stops re-generating them per outer row and the equi-join
                // hash path can key them. Correlated sources are left untouched.
                var execSources = MaterializeUncorrelatedDeferredSources(sources, batch);
                return aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null
                    ? BuildAggregateProjectionRows(execSources, joins, ResolveColumnType, expressions, fromClause, outputColumnNames, orderBy, aggregates, top, offsetCount, fetchCount, batch, outerResolver)
                    : windows.Count > 0
                        ? ProjectWindowedRows(execSources, joins, expressions, fromClause.Excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, windows, windowOperandTypes, windowResultTypes, batch, outerResolver)
                        : ProjectSqlRows(execSources, joins, expressions, fromClause.Excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, batch, outerResolver);
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
        selection.ColumnNullability = columnNullability;
        selection.ProjectionExpressions = [.. expressions];
        selection.ColumnIntegerLiteralDigits = LiteralDigitsOf(expressions);
        selection.ColumnReportsNumeric = ColumnReportsNumericOf(expressions, outputSchema);
        selection.MultipleFromSources = sources.Length > 1;
        selection.AutoElementName = sources.Length == 1 ? sources[0].Qualifier : null;
        return selection;
    }

    /// <summary>
    /// Per-projection-column nullability for result-set metadata (the TDS
    /// COLMETADATA fNullable flag). Computed for the no-join shape with at
    /// most one source, where <see cref="Expression.ResultIsNullable"/>'s
    /// rules (direct refs preserve base-column nullability, literals NOT NULL,
    /// other expressions nullable) match SQL Server's result metadata; joined
    /// or multi-source shapes return null and the wire falls back to claiming
    /// every column nullable — outer joins NULL-fill the inner side, so
    /// base-column nullability alone would over-claim NOT NULL there. The
    /// zero-source (FROM-less) case is included so a bare literal projection
    /// reports NOT NULL like real (<c>select 1</c> → <c>Int</c>, not
    /// <c>IntN</c>); a column reference can't appear without a source, so the
    /// resolver is never consulted there. Load-bearing for DacFx bacpac
    /// export: its BCP data-file layout drops the per-value length prefix
    /// on fixed-width columns whose wire metadata says NOT NULL, and the
    /// bacpac loader reads the file per the model.xml declaration — the two
    /// must agree.
    /// </summary>
    private static bool[]? ComputeColumnNullability(List<Expression> expressions, FromSource[] sources, JoinSpec[] joins)
    {
        if (sources.Length > 1 || joins.Length != 0)
            return null;

        bool ResolveNullable(MultiPartName name)
        {
            var (s, c) = FindSourceColumn(sources, name);
            return s == -1 || sources[s].Columns[c].Nullable;
        }

        var nullability = new bool[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            nullability[i] = expressions[i].ResultIsNullable(ResolveNullable);
        return nullability;
    }

    /// <summary>
    /// Per-projection-column decimal-vs-numeric reported type name for
    /// result-set metadata. A column reports <c>numeric</c> only when its
    /// result is <c>decimal</c>-family AND the expression carries a
    /// numeric-named source (see <see cref="Expression.ResultReportsNumeric"/>);
    /// returns null when no column qualifies (the common case), so most plans
    /// carry no extra array. The two names share one <see cref="SqlType"/>, so
    /// this stays projection-time metadata and never influences storage.
    /// </summary>
    private static bool[]? ColumnReportsNumericOf(List<Expression> expressions, SqlType[] schema)
    {
        bool[]? reportsNumeric = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (schema[i] is DecimalSqlType && expressions[i].ResultReportsNumeric)
                (reportsNumeric ??= new bool[expressions.Count])[i] = true;
        }
        return reportsNumeric;
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
        TopSpec top,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        // ORDER BY elimination: when the sort matches a NOT-NULL leading-key
        // column, enumerate the source in key order and stream (no buffer + sort).
        // Residual WHERE and projection preserve order; OFFSET / FETCH / TOP then
        // read only the rows they need. TOP PERCENT / WITH TIES need the full
        // buffered rowcount / ORDER BY keys, so they skip the streaming paths.
        var hasJoinGroup = ContainsJoinGroup(joins);
        if (!hasJoinGroup && !distinct && !top.RequiresBuffering && orderBy.Count > 0
            && TryApplyOrderedScan(sources, joins, orderBy, excluders, batch, outerResolver, out var orderedSources))
        {
            return ProjectStreaming(orderedSources, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver);
        }

        if (!hasJoinGroup)
            sources = MaybeApplyIndexSeek(sources, joins, excluders, batch, outerResolver);
        sources = NarrowLeftmostJoinSource(sources, excluders, batch, outerResolver);
        return !distinct && orderBy.Count == 0 && !top.RequiresBuffering
            ? ProjectStreaming(sources, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver)
            : ProjectBuffered(sources, joins, expressions, excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, batch, outerResolver);
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
        TopSpec top,
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

        var cap = ComputeTopCap(materialized, item => item.Keys, orderBy, top, fetchCount);

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> windowed = materialized;
        if (offsetCount is { } offset && offset > 0)
            windowed = windowed.Skip(offset);
        if (cap is { } limit)
            windowed = windowed.Take(limit);

        foreach (var (projected, _) in windowed)
            yield return projected;
    }
}
