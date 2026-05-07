using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Manages the higher-level logic to convert a sequence of command tokens into tabular results.
/// </summary>
/// <remarks>
/// <para>
/// Parsing and execution are split: <see cref="Parse"/> captures the
/// projection / WHERE / GROUP BY / HAVING / ORDER BY into a frozen plan and
/// returns it; <see cref="Execute"/> materializes one
/// <see cref="SimulatedSqlResultSet"/> per call. The split lets correlated
/// subqueries (EXISTS / IN(SELECT)) re-execute the inner SELECT per outer
/// row by passing a different <c>outerResolver</c> each time. For the
/// non-correlated and top-level cases, <see cref="Execute"/> is called once
/// with no outer resolver — the deferred shape is invisible to those callers.
/// </para>
/// <para>
/// Correlated lookup chains via the <c>outerResolver</c> argument: a column
/// reference that doesn't resolve in the local FROM source falls through to
/// the outer scope, which itself falls through to its outer, and so on. Type
/// resolution at parse time follows the same chain through
/// <see cref="ParserContext.OuterTypeResolver"/>.
/// </para>
/// </remarks>
internal sealed class Selection
{
    public readonly SqlType[] Schema;
    public readonly string[] ColumnNames;

    private readonly Func<Func<List<string>, SqlValue>?, IEnumerable<byte[]>> rowSource;

    private Selection(SqlType[] schema, string[] columnNames, Func<Func<List<string>, SqlValue>?, IEnumerable<byte[]>> rowSource)
    {
        this.Schema = schema;
        this.ColumnNames = columnNames;
        this.rowSource = rowSource;
    }

    /// <summary>
    /// Materializes the SELECT against the given outer-row resolver
    /// (null for top-level / non-correlated scopes). Each call produces a
    /// fresh <see cref="SimulatedSqlResultSet"/>; the underlying row sequence
    /// is itself lazy or eager depending on whether DISTINCT / ORDER BY /
    /// aggregation force buffering.
    /// </summary>
    public SimulatedSqlResultSet Execute(Func<List<string>, SqlValue>? outerResolver = null) =>
        new(this.Schema, this.ColumnNames, this.rowSource(outerResolver));

    /// <summary>
    /// Creates a <see cref="Selection"/> from a series of tokens. Follows the
    /// lookahead contract documented on <see cref="ParserContext"/>: on
    /// return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the SELECT (typically <c>;</c>, <c>)</c> for a derived
    /// table or subquery, or null at end of command).
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <param name="depth">The current depth of recursed selection, such as with derived tables. 0 for the top-level SELECT.</param>
    /// <param name="outerTypeResolver">Outer-scope column type resolver used during projection planning when this SELECT references an enclosing scope's columns. Null for the top-level / non-correlated case.</param>
    /// <returns>The prepared plan; call <see cref="Execute"/> to materialize results.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Selection Parse(ParserContext context, uint depth, Func<List<string>, SqlType>? outerTypeResolver = null)
    {
        // Save / restore the parser's aggregate collector so each Selection
        // (including nested derived tables) gets its own scope. Aggregates
        // parsed inside the projection / HAVING register here; the executor
        // uses the populated list to switch into aggregate mode.
        var savedCollector = context.AggregateCollector;
        var aggregates = new List<AggregateExpression>();
        context.AggregateCollector = aggregates;
        try
        {
            return ParseInner(context, depth, aggregates, outerTypeResolver);
        }
        finally
        {
            context.AggregateCollector = savedCollector;
        }
    }

    /// <summary>
    /// Bundles the post-FROM clause state — WHERE excluders, GROUP BY keys,
    /// HAVING predicate, ORDER BY — so the recursive parse helpers can
    /// share one growing state record without lengthening every signature.
    /// </summary>
    private sealed class FromClause
    {
        public readonly List<BooleanExpression> Excluders = [];
        public readonly List<Expression> GroupBy = [];
        public BooleanExpression? Having;
        public readonly List<OrderBySpec> OrderBy = [];
    }

    private static Selection ParseInner(ParserContext context, uint depth, List<AggregateExpression> aggregates, Func<List<string>, SqlType>? outerTypeResolver)
    {
        var distinct = false;
        int? topCount = null;

        var firstToken = context.GetNextRequired();

        // DISTINCT/ALL appear before TOP. SQL Server rejects `TOP n DISTINCT`
        // at parse time (Msg 156), and the only other quantifier is ALL which
        // is the implicit default — accept it but treat as no-op. Switch (vs
        // chained ifs) lets the compiler emit a single ReservedKeyword type
        // check for both arms.
        switch (firstToken)
        {
            case ReservedKeyword { Keyword: Keyword.Distinct }:
                distinct = true;
                firstToken = context.GetNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.All }:
                firstToken = context.GetNextRequired();
                break;
        }

        if (firstToken is ReservedKeyword { Keyword: Keyword.Top })
        {
            var resolved = Expression
                .Parse(context.MoveNextRequiredReturnSelf())
                .Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            topCount = !resolved.IsNull && resolved.Type == SqlType.Int32
                ? resolved.AsInt32
                : throw SimulatedSqlException.TopFetchRequiresInteger();
        }

        List<Expression> expressions = [];
        var fromClause = new FromClause();

        do
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.From }:
                    break;

                case ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce or Keyword.Case }:
                    // LEFT, RIGHT, CONVERT, TRY_CONVERT, COALESCE are reserved
                    // keywords but valid as function-call heads inside a
                    // SELECT projection. CASE introduces an inline expression
                    // (see CaseExpression.ParseCase).
                    expressions.Add(Expression.Parse(context));
                    break;

                case ReservedKeyword { Keyword: not Keyword.Null } keyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);

                case Operator { Character: ',' }:
                    if (expressions.Count == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    continue;
                case Operator { Character: ')' }:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                default:
                    expressions.Add(Expression.Parse(context));
                    break;
            }

            switch (context.Token)
            {
                case null:
                    goto ExitWhileTokenLoop;

                // End of statement in a multi-statement batch. Leave the ';'
                // as the current token so the outer dispatch loop sees it on
                // its next iteration (where it's a no-op separator) and
                // continues with whatever statement follows.
                case Operator { Character: ';' }:
                    goto ExitWhileTokenLoop;

                case Operator { Character: ',' }:
                    continue;

                // A `)` at the lookahead-after-expression position closes the
                // enclosing subquery / derived table when this Parse is at
                // depth > 0. The pre-expression switch above also has a `)`
                // case for the empty-projection error path; this one fires
                // when at least one expression has been parsed.
                case Operator { Character: ')' }:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                case Name name:
                    expressions[^1] = Expression.AssignName(expressions[^1], name);
                    continue;

                case ReservedKeyword { Keyword: Keyword.As }:
                    expressions[^1] = Expression.AssignName(expressions[^1], context.GetNextRequired<Name>());
                    continue;

                case ReservedKeyword { Keyword: Keyword.From }:
                    switch (context.GetNextRequired())
                    {
                        case Name tableName:
                            if (!context.Simulation.HeapTables.TryGetValue(tableName.Value, out var heapTable)
                                && !Simulation.SystemHeapTables.TryGetValue(tableName.Value, out heapTable))
                            {
                                throw SimulatedSqlException.InvalidObjectName(tableName);
                            }

                            var heapColumnNames = new string[heapTable.Columns.Length];
                            for (var ci = 0; ci < heapColumnNames.Length; ci++)
                                heapColumnNames[ci] = heapTable.Columns[ci].Name;

                            var heapAlias = ConsumeOptionalAlias(context);
                            var heapQualifier = heapAlias ?? tableName.Value;

                            ConsumeWhereOrderByWithOuterScope(context, fromClause, heapQualifier, heapColumnNames, heapTable.Columns, outerTypeResolver);

                            return BuildSqlProjection(heapQualifier, heapColumnNames, heapTable.Columns, heapTable.StoredColumns, heapTable.StorageOrdinals, heapTable.Heap, heapTable.Rows, expressions, fromClause, distinct, topCount, aggregates, outerTypeResolver);

                        case Operator { Character: '(' }:
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                                throw SimulatedSqlException.SyntaxErrorNear(context);

                            // Derived tables don't see the outer scope of the
                            // enclosing SELECT — pass null for the inner Parse
                            // so projection-time references can't reach out.
                            // (SQL Server's actual rule allows this only with
                            // APPLY / lateral; we don't support those yet.)
                            var derivedSelection = Selection.Parse(context, depth + 1, outerTypeResolver: null);
                            if (derivedSelection.Execute() is not SimulatedSqlResultSet derived)
                                throw new InvalidOperationException("Inner SELECT produced a non-Pages result set; this should be unreachable.");

                            // Inner SELECT result rows are LOB-inline (projections never
                            // emit LOB pointers because they have no destination Heap),
                            // so build a HeapColumn[] schema from the SqlType[] so the
                            // decoder still strips marker bytes for text/ntext/image
                            // columns; lobStore is null because no chain to follow.
                            var derivedColumns = new HeapColumn[derived.Schema.Length];
                            for (var ci = 0; ci < derivedColumns.Length; ci++)
                                derivedColumns[ci] = new HeapColumn(string.Empty, derived.Schema[ci], maxLength: null, nullable: true);

                            // Derived tables have no native name; the alias is
                            // the qualifier when present, otherwise null disables
                            // the qualified-reference check (the existing simulator
                            // accepts derived tables without alias, unlike real SQL).
                            var derivedQualifier = ConsumeOptionalAlias(context);

                            ConsumeWhereOrderByWithOuterScope(context, fromClause, derivedQualifier, derived.ColumnNames, derivedColumns, outerTypeResolver);

                            return BuildSqlProjection(derivedQualifier, derived.ColumnNames, derivedColumns, derivedColumns, storageOrdinals: null, lobStore: null, derived.RowBytes, expressions, fromClause, distinct, topCount, aggregates, outerTypeResolver);
                    }

                    throw SimulatedSqlException.SyntaxErrorNear(context);

                case ReservedKeyword { Keyword: Keyword.Where or Keyword.Order }:
                    ConsumeWhereAndOrderBy(context, fromClause);
                    goto ExitWhileTokenLoop;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        return BuildSynthesizedSqlRow(expressions, fromClause.Excluders, fromClause.OrderBy, distinct, topCount, outerTypeResolver);
    }

    /// <summary>
    /// After a FROM source's columns and qualifier are known, sets
    /// <see cref="ParserContext.OuterTypeResolver"/> to a chained resolver
    /// (this scope's columns, falling through to the prior outer) for the
    /// duration of the WHERE / GROUP BY / HAVING / ORDER BY parse.
    /// Subqueries that appear inside those clauses pick up the chained
    /// resolver and pass it as their own outer resolver to
    /// <see cref="Parse"/>, so a column reference inside a
    /// correlated subquery sees the full enclosing-scope stack.
    /// </summary>
    private static void ConsumeWhereOrderByWithOuterScope(
        ParserContext context,
        FromClause fromClause,
        string? qualifier,
        string[] sourceColumnNames,
        HeapColumn[] sourceSchema,
        Func<List<string>, SqlType>? outerTypeResolver)
    {
        SqlType MyResolver(List<string> name)
        {
            // Qualified reference: only this scope owns the name iff the
            // qualifier matches; otherwise fall through to the outer chain
            // unconditionally — the resolver up the stack will handle it.
            if (name.Count >= 2 && (qualifier is null || !Collation.Default.Equals(name[^2], qualifier)))
            {
                return outerTypeResolver is not null
                    ? outerTypeResolver(name)
                    : throw SimulatedSqlException.InvalidColumnName(name);
            }

            var lastPart = name[^1];
            for (var j = 0; j < sourceColumnNames.Length; j++)
            {
                if (Collation.Default.Equals(sourceColumnNames[j], lastPart))
                    return sourceSchema[j].Type;
            }
            return outerTypeResolver is not null
                ? outerTypeResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
        }

        var saved = context.OuterTypeResolver;
        context.OuterTypeResolver = MyResolver;
        try
        {
            ConsumeWhereAndOrderBy(context, fromClause);
        }
        finally
        {
            context.OuterTypeResolver = saved;
        }
    }

    /// <summary>
    /// Consumes an optional <c>AS alias</c> after a FROM source. Returns the
    /// alias text if present, null otherwise. On entry, the FROM source
    /// (table name or derived-table closing <c>)</c>) is the current token;
    /// this advances past it, optionally past <c>AS alias</c>, and leaves
    /// the cursor at the next un-consumed lookahead position (typically
    /// WHERE / GROUP / HAVING / ORDER / ; / null).
    /// </summary>
    private static string? ConsumeOptionalAlias(ParserContext context)
    {
        var nextToken = context.GetNextOptional();
        if (nextToken is ReservedKeyword { Keyword: Keyword.As })
        {
            var alias = context.GetNextRequired<Name>().Value;
            _ = context.GetNextOptional();
            return alias;
        }
        return null;
    }

    /// <summary>
    /// Reads zero or more WHERE clauses, an optional GROUP BY, an optional
    /// HAVING, and an optional ORDER BY — in that order, matching SQL Server's
    /// grammar. Starts with <see cref="ParserContext.Token"/> already
    /// positioned at the first lookahead token (e.g. WHERE, GROUP, HAVING,
    /// ORDER, ;, or null). On return, <see cref="ParserContext.Token"/> is
    /// the first token after the last consumed clause (typically ;, ), or
    /// null).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ParserContext.Token"/> directly between clauses
    /// instead of advancing with <see cref="ParserContext.GetNextOptional"/>
    /// — sub-Parse helpers leave Token at the first un-consumed token per
    /// the lookahead contract, and an extra advance here would silently swallow
    /// the next clause's opening keyword.
    /// </remarks>
    private static void ConsumeWhereAndOrderBy(ParserContext context, FromClause fromClause)
    {
        while (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            fromClause.Excluders.Add(BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()));
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.Group })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            do
            {
                context.MoveNextRequired();
                fromClause.GroupBy.Add(Expression.Parse(context));
            } while (context.Token is Operator { Character: ',' });
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.Having })
        {
            fromClause.Having = BooleanExpression.Parse(context.MoveNextRequiredReturnSelf());
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ParseOrderByItems(context, fromClause.OrderBy);
        }
    }

    /// <summary>
    /// Reads one or more ORDER BY items (comma separated). Each item is an
    /// <see cref="Expression"/> followed by an optional <c>ASC</c>/<c>DESC</c>
    /// keyword (default ASC). A pure positive-integer literal is recorded as
    /// an ordinal reference into the projection rather than a constant
    /// expression; constant non-integer expressions silently sort by their
    /// constant (SQL Server's Msg 408 rejection isn't modeled).
    /// </summary>
    private static void ParseOrderByItems(ParserContext context, List<OrderBySpec> orderBy)
    {
        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);

            var descending = false;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc })
            {
                context.MoveNextOptional();
            }
            else if (context.Token is ReservedKeyword { Keyword: Keyword.Desc })
            {
                descending = true;
                context.MoveNextOptional();
            }

            // A bare integer literal is the ordinal form (validated against the
            // projection count later in BuildSqlProjection). Anything else —
            // including a constant arithmetic expression like `1+0` — falls
            // through to per-row evaluation; SQL Server's Msg 408 rejection of
            // constant ORDER BY expressions isn't modeled.
            if (expr is Value valExpr
                && valExpr.Constant.Type == SqlType.Int32
                && !valExpr.Constant.IsNull)
            {
                orderBy.Add(OrderBySpec.FromOrdinal(valExpr.Constant.AsInt32, descending));
            }
            else
            {
                orderBy.Add(OrderBySpec.FromExpression(expr, descending));
            }
        }
        while (context.Token is Operator { Character: ',' });
    }

    /// <summary>
    /// Builds the plan for a tableless SELECT (synthesized constant-row
    /// branch). Schema and values are computed at parse time by Running each
    /// projection expression against a throwing column resolver — tableless
    /// projections don't reference any column. WHERE excluders, by contrast,
    /// can reference outer-scope columns when this Selection is the body of a
    /// correlated subquery, so they re-evaluate at <see cref="Execute"/> time
    /// against the supplied outer resolver. <paramref name="topCount"/>, if
    /// zero, suppresses the row. <paramref name="distinct"/> and
    /// <paramref name="orderBy"/> are accepted for syntactic completeness but
    /// are no-ops — the result is at most one row, so dedup and sort have no
    /// effect.
    /// </summary>
    /// <remarks>
    /// <paramref name="outerTypeResolver"/> is unused here because the parse-
    /// time projection Run can't see outer rows (and a tableless projection
    /// referencing an outer column raises Msg 207 at parse time). Kept on the
    /// signature so the dispatch in <see cref="ParseInner"/> doesn't fork.
    /// </remarks>
    private static Selection BuildSynthesizedSqlRow(List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, bool distinct, int? topCount, Func<List<string>, SqlType>? outerTypeResolver)
    {
        _ = distinct;
        _ = orderBy;
        _ = outerTypeResolver;

        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        for (var i = 0; i < expressions.Count; i++)
        {
            values[i] = expressions[i].Run(column => throw SimulatedSqlException.InvalidColumnName(column));
            schema[i] = values[i].Type;
            columnNames[i] = expressions[i].Name;
        }

        return new Selection(schema, columnNames, outerResolver =>
        {
            if (topCount == 0)
                return [];

            SqlValue Resolve(List<string> name) =>
                outerResolver is not null
                    ? outerResolver(name)
                    : throw SimulatedSqlException.InvalidColumnName(name);

            foreach (var excluder in excluders)
            {
                if (excluder.Run(Resolve) != true)
                    return [];
            }

            return [RowEncoder.EncodeRow(schema, values)];
        });
    }

    /// <summary>
    /// Builds the plan for a SELECT-FROM-source query (a heap table or a
    /// derived table). Static work — output schema, validation of ordinal
    /// ORDER BY items, LOB-in-DISTINCT/ORDER-BY checks — happens here.
    /// The deferred closure runs per <see cref="Execute"/> call, accepting
    /// the outer-row resolver and dispatching to the aggregate or simple
    /// projection path; each input row is decoded column-by-column on demand
    /// via <see cref="RowDecoder"/> and projected through
    /// <see cref="Expression.Run"/>.
    /// </summary>
    private static Selection BuildSqlProjection(
        string? qualifier,
        string[] sourceColumnNames,
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
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

        // Qualified-reference scoping: a multi-part name like `t1.id` only
        // resolves locally when its qualifier matches this scope's table /
        // alias. A mismatch returns -1 so the runtime / type resolvers fall
        // through to the outer chain — the rule that lets correlated
        // subqueries reach enclosing scopes even when column names collide.
        int FindSourceColumn(List<string> name)
        {
            if (name.Count >= 2 && (qualifier is null || !Collation.Default.Equals(name[^2], qualifier)))
                return -1;

            for (var j = 0; j < sourceColumnNames.Length; j++)
            {
                if (Collation.Default.Equals(sourceColumnNames[j], name[^1]))
                    return j;
            }
            return -1;
        }

        SqlType ResolveColumnType(List<string> name)
        {
            var idx = FindSourceColumn(name);
            return idx != -1
                ? sourceSchema[idx].Type
                : outerTypeResolver is not null
                    ? outerTypeResolver(name)
                    : throw SimulatedSqlException.InvalidColumnName(name);
        }

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
        // ORDER BY: ordinal items index into the output schema; expression
        // items resolve through the same column type machinery the projection
        // already used. DISTINCT is row-level dedup, so any LOB output column
        // is fatal.
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
                ? BuildAggregateProjectionRows(sourceSchema, storedSchema, storageOrdinals, lobStore, sourceRows, FindSourceColumn, ResolveColumnType, expressions, fromClause, outputSchema, outputColumnNames, aggregates, topCount, outerResolver)
                : ProjectSqlRows(sourceSchema, storedSchema, storageOrdinals, lobStore, sourceRows, FindSourceColumn, expressions, fromClause.Excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, outerResolver));
    }

    /// <summary>
    /// Aggregate-mode executor: streams every input row through each
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
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
        Func<List<string>, int> findSourceColumn,
        Func<List<string>, SqlType> resolveColumnType,
        List<Expression> expressions,
        FromClause fromClause,
        SqlType[] outputSchema,
        string[] outputColumnNames,
        List<AggregateExpression> aggregates,
        int? topCount,
        Func<List<string>, SqlValue>? outerResolver)
    {
        _ = outputColumnNames;

        if (topCount == 0)
            return [];

        var groupByExpressions = fromClause.GroupBy;
        var groupByCount = groupByExpressions.Count;

        // Per-group state: aggregators (one per AggregateExpression) plus the
        // group key tuple (used to populate non-aggregate projection slots).
        // Without GROUP BY, the implicit "single empty group" still flows
        // through this path with an empty key tuple.
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

        // Pre-create the implicit group when there's no GROUP BY so an empty
        // input still produces one output row.
        if (groupByCount == 0)
            groups[SqlValueKey.Empty] = NewGroup();

        foreach (var rowBytes in sourceRows)
        {
            var bytes = rowBytes;
            SqlValue ResolveColumn(List<string> name)
            {
                var columnIndex = findSourceColumn(name);
                return columnIndex != -1
                    ? DecodeOrCompute(sourceSchema, storedSchema, storageOrdinals, columnIndex, bytes, lobStore, ResolveColumn)
                    : outerResolver is not null
                        ? outerResolver(name)
                        : throw SimulatedSqlException.InvalidColumnName(name);
            }

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

            // Locate or create this row's group.
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
                // STRING_AGG's separator is evaluated per row (SQL Server
                // accepts non-constant separators); thread the latest value
                // into the aggregator before each Add. Other aggregates have
                // no per-row auxiliary inputs.
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
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
        Func<List<string>, int> findSourceColumn,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        int? topCount,
        Func<List<string>, SqlValue>? outerResolver)
    {
        // Streaming path: no DISTINCT, no ORDER BY. TOP applies row-by-row.
        if (!distinct && orderBy.Count == 0)
        {
            return ProjectStreaming(sourceSchema, storedSchema, storageOrdinals, lobStore, sourceRows, findSourceColumn, expressions, excluders, outputSchema, topCount, outerResolver);
        }

        // Buffered path: DISTINCT and/or ORDER BY require the full row set
        // before TOP can apply.
        return ProjectBuffered(sourceSchema, storedSchema, storageOrdinals, lobStore, sourceRows, findSourceColumn, expressions, excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount, outerResolver);
    }

    private static IEnumerable<byte[]> ProjectStreaming(
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
        Func<List<string>, int> findSourceColumn,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        int? topCount,
        Func<List<string>, SqlValue>? outerResolver)
    {
        var remaining = topCount;
        foreach (var rowBytes in sourceRows)
        {
            if (remaining == 0)
                yield break;

            var bytes = rowBytes;

            SqlValue ResolveColumn(List<string> name)
            {
                var columnIndex = findSourceColumn(name);
                return columnIndex != -1
                    ? DecodeOrCompute(sourceSchema, storedSchema, storageOrdinals, columnIndex, bytes, lobStore, ResolveColumn)
                    : outerResolver is not null
                        ? outerResolver(name)
                        : throw SimulatedSqlException.InvalidColumnName(name);
            }

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
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
        Func<List<string>, int> findSourceColumn,
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

        foreach (var rowBytes in sourceRows)
        {
            var bytes = rowBytes;

            SqlValue ResolveSource(List<string> name)
            {
                var columnIndex = findSourceColumn(name);
                return columnIndex != -1
                    ? DecodeOrCompute(sourceSchema, storedSchema, storageOrdinals, columnIndex, bytes, lobStore, ResolveSource)
                    : outerResolver is not null
                        ? outerResolver(name)
                        : throw SimulatedSqlException.InvalidColumnName(name);
            }

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
    /// Resolves a single column reference at <paramref name="columnIndex"/>
    /// in <paramref name="sourceSchema"/> for the row at <paramref name="bytes"/>.
    /// Stored columns (regular plus persisted-computed) decode directly via
    /// <see cref="RowDecoder.DecodeColumn(ReadOnlySpan{HeapColumn}, ReadOnlySpan{byte}, int, Heap?)"/>
    /// at their storage ordinal. Non-persisted computed columns evaluate
    /// their expression through <paramref name="resolveByName"/> — the
    /// recursive references inside the expression bind back through the same
    /// caller's resolver, but are guaranteed by Msg 1759 to land only on
    /// stored columns.
    /// </summary>
    private static SqlValue DecodeOrCompute(
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        int columnIndex,
        byte[] bytes,
        Heap? lobStore,
        Func<List<string>, SqlValue> resolveByName) =>
        storageOrdinals is null
            ? RowDecoder.DecodeColumn(storedSchema, bytes, columnIndex, lobStore)
            : sourceSchema[columnIndex].Computed is { } computedExpr && !sourceSchema[columnIndex].IsPersisted
                ? computedExpr.Run(resolveByName)
                : RowDecoder.DecodeColumn(storedSchema, bytes, storageOrdinals[columnIndex], lobStore);

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
/// One entry in an ORDER BY clause: either a positional ordinal (1-based
/// index into the projection) or an arbitrary expression, plus the direction
/// flag.
/// </summary>
internal readonly struct OrderBySpec
{
    public readonly Expression? Expr;
    public readonly int Ordinal;
    public readonly bool Descending;

    public bool IsOrdinal => this.Expr is null;

    private OrderBySpec(Expression? expr, int ordinal, bool descending)
    {
        this.Expr = expr;
        this.Ordinal = ordinal;
        this.Descending = descending;
    }

    public static OrderBySpec FromExpression(Expression expr, bool descending) => new(expr, 0, descending);
    public static OrderBySpec FromOrdinal(int ordinal, bool descending) => new(null, ordinal, descending);
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
