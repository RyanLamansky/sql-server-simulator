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
/// projection / FROM (with JOINs) / WHERE / GROUP BY / HAVING / ORDER BY
/// into a frozen plan and returns it; <see cref="Execute"/> materializes
/// one <see cref="SimulatedSqlResultSet"/> per call. The split lets
/// correlated subqueries (EXISTS / IN(SELECT) / scalar) re-execute the
/// inner SELECT per outer row by passing a different <c>outerResolver</c>
/// each time. For the non-correlated and top-level cases,
/// <see cref="Execute"/> is called once with no outer resolver — the
/// deferred shape is invisible to those callers.
/// </para>
/// <para>
/// Multi-source FROM clauses (one or more JOINs) are represented as a
/// <see cref="FromSource"/>[] plus a parallel <see cref="JoinSpec"/>[]
/// (one shorter — the leftmost source has no join). The row stream
/// consumed by the projector is a sequence of <c>byte[]?[]</c> tuples
/// (one byte[] per source, null for an unmatched LEFT-JOIN slot).
/// Column resolution walks all sources via a qualifier-aware lookup;
/// unqualified collisions raise Msg 209.
/// </para>
/// <para>
/// Correlated lookup chains via the <c>outerResolver</c> argument: a
/// column reference that doesn't resolve in any local FROM source falls
/// through to the outer scope, which itself falls through to its outer,
/// and so on. Type resolution at parse time follows the same chain
/// through <see cref="ParserContext.OuterTypeResolver"/>.
/// </para>
/// <para>
/// This file holds the public surface and the parser-side logic
/// (Parse / ParseInner / FROM-source + JOIN parsing / WHERE / GROUP BY /
/// HAVING / ORDER BY / tableless-SELECT shortcut). The execution-side
/// helpers (row pipeline, projection paths, column resolution at
/// runtime) live in <c>Selection.Execution.cs</c> as the other half of
/// the same partial class.
/// </para>
/// </remarks>
internal sealed partial class Selection
{
    public readonly SqlType[] Schema;
    public readonly string[] ColumnNames;

    /// <summary>
    /// True when this plan internally bakes an ORDER BY clause into its
    /// row pipeline. Set-op chaining inspects this on the first branch:
    /// per SQL Server, a per-branch ORDER BY is illegal when a set
    /// operator follows (Msg 156), and the simulator rejects via
    /// <see cref="CombineSetOps"/>. Top-level ORDER BY (after a set-op
    /// chain) is applied by <see cref="ApplyTopLevelOrderBy"/> and also
    /// sets this flag on the wrapper.
    /// </summary>
    public readonly bool HasOrderBy;

    private readonly Func<Func<MultiPartName, SqlValue>?, IEnumerable<byte[]>> rowSource;

    private Selection(SqlType[] schema, string[] columnNames, bool hasOrderBy, Func<Func<MultiPartName, SqlValue>?, IEnumerable<byte[]>> rowSource)
    {
        this.Schema = schema;
        this.ColumnNames = columnNames;
        this.HasOrderBy = hasOrderBy;
        this.rowSource = rowSource;
    }

    /// <summary>
    /// Materializes the SELECT against the given outer-row resolver
    /// (null for top-level / non-correlated scopes). Each call produces a
    /// fresh <see cref="SimulatedSqlResultSet"/>; the underlying row sequence
    /// is itself lazy or eager depending on whether DISTINCT / ORDER BY /
    /// aggregation force buffering.
    /// </summary>
    public SimulatedSqlResultSet Execute(Func<MultiPartName, SqlValue>? outerResolver = null) =>
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
    public static Selection Parse(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver = null) =>
        ParseQueryExpression(context, depth, outerTypeResolver);

    /// <summary>
    /// Parses a full query expression: a chain of set-op-combined SELECT
    /// branches optionally followed by a top-level ORDER BY. Set-op
    /// precedence: <c>INTERSECT</c> binds tighter than <c>UNION</c> /
    /// <c>EXCEPT</c> (which are at the same level, left-to-right).
    /// </summary>
    private static Selection ParseQueryExpression(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var combined = ParseUnionExceptChain(context, depth, outerTypeResolver);

        // Top-level ORDER BY: applies to the combined result (post-set-op).
        // ORDER BY references within set-op chains use the first branch's
        // column names. Top-level OFFSET/FETCH (post-chain) attaches here
        // too; FETCH-without-OFFSET on a single SELECT is also caught here
        // when the cursor sits on FETCH after no ORDER BY was consumed.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var orderBy = new List<OrderBySpec>();
            ParseOrderByItems(context, orderBy);
            var topLevelTail = new FromClause();
            ConsumeOffsetFetch(context, topLevelTail);
            combined = ApplyTopLevelOrderBy(combined, orderBy, topLevelTail.OffsetCount, topLevelTail.FetchCount);
        }

        return combined;
    }

    /// <summary>
    /// Lower-precedence set-op level: parses a chain of UNION /
    /// UNION ALL / EXCEPT operators left-to-right, with each operand
    /// parsed via <see cref="ParseIntersectChain"/> (which handles the
    /// higher-precedence INTERSECT operator). The first branch gets
    /// <c>allowOrderBy=true</c> so single-SELECT queries with ORDER BY
    /// retain the existing inside-the-projection behavior (which can
    /// reference non-projected source columns); subsequent branches use
    /// <c>allowOrderBy=false</c> and any post-chain ORDER BY is applied
    /// at the top level.
    /// </summary>
    private static Selection ParseUnionExceptChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var left = ParseIntersectChain(context, depth, outerTypeResolver, isFirstBranch: true);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Union or Keyword.Except } op)
        {
            SetOpKind kind;
            if (op.Keyword == Keyword.Union)
            {
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.All })
                {
                    kind = SetOpKind.UnionAll;
                    context.MoveNextRequired();
                }
                else
                {
                    kind = SetOpKind.Union;
                }
            }
            else
            {
                kind = SetOpKind.Except;
                context.MoveNextRequired();
            }

            var right = ParseIntersectChain(context, depth, outerTypeResolver, isFirstBranch: false);
            left = CombineSetOps(left, right, kind);
        }
        return left;
    }

    /// <summary>
    /// Higher-precedence set-op level: parses a chain of INTERSECT
    /// operators left-to-right.
    /// </summary>
    private static Selection ParseIntersectChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool isFirstBranch)
    {
        var left = ParseSingleSelectStatement(context, depth, outerTypeResolver, allowOrderBy: isFirstBranch);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Intersect })
        {
            context.MoveNextRequired();
            var right = ParseSingleSelectStatement(context, depth, outerTypeResolver, allowOrderBy: false);
            left = CombineSetOps(left, right, SetOpKind.Intersect);
        }
        return left;
    }

    /// <summary>
    /// Parses a single SELECT statement (the leaf of a set-op chain).
    /// Each branch gets its own aggregate-collector scope so aggregates
    /// inside one branch don't leak into another.
    /// <paramref name="allowOrderBy"/> is true only for the very first
    /// branch parsed (or the entire query if no set-op follows) so that
    /// non-set-op queries like <c>SELECT name FROM t ORDER BY id</c>
    /// keep the existing branch-internal sort that can reference
    /// non-projected source columns; subsequent branches must defer
    /// ORDER BY to the top level.
    /// </summary>
    private static Selection ParseSingleSelectStatement(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy)
    {
        // Save / restore the parser's aggregate and window collectors so
        // each branch gets its own scope. Aggregates and window functions
        // parsed inside the projection / HAVING register into the
        // respective lists; the executor uses the populated lists to
        // switch into aggregate or windowed-projection mode.
        var savedAggregateCollector = context.AggregateCollector;
        var savedWindowCollector = context.WindowCollector;
        var aggregates = new List<AggregateExpression>();
        var windows = new List<WindowExpression>();
        context.AggregateCollector = aggregates;
        context.WindowCollector = windows;
        try
        {
            return ParseInner(context, depth, aggregates, windows, outerTypeResolver, allowOrderBy);
        }
        finally
        {
            context.AggregateCollector = savedAggregateCollector;
            context.WindowCollector = savedWindowCollector;
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

        /// <summary>
        /// Resolved <c>OFFSET</c> count. Null when no OFFSET clause was
        /// present. The value is parse-time-resolved (constants, parameters,
        /// arithmetic) and pre-validated for non-negativity (Msg 10742).
        /// </summary>
        public int? OffsetCount;

        /// <summary>
        /// Resolved <c>FETCH NEXT</c> / <c>FETCH FIRST</c> count. Null when
        /// no FETCH clause was present (OFFSET-only is valid; FETCH-only is
        /// rejected at parse time via Msg 153). Pre-validated for &gt; 0
        /// (Msg 10744).
        /// </summary>
        public int? FetchCount;
    }

    private static Selection ParseInner(ParserContext context, uint depth, List<AggregateExpression> aggregates, List<WindowExpression> windows, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy)
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

                // Set-op keywords at the outer-switch position (i.e. after
                // an `AS alias` continued the loop) terminate this branch
                // so the set-op driver can chain.
                case ReservedKeyword { Keyword: Keyword.Union or Keyword.Intersect or Keyword.Except }:
                    goto ExitWhileTokenLoop;

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
                    var sources = new List<FromSource>();
                    var joins = new List<JoinSpec>();
                    ParseFromSourceAndJoins(context, depth, sources, joins, fromClause, outerTypeResolver, allowOrderBy);
                    if (topCount is not null && fromClause.OffsetCount is not null)
                        throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
                    return BuildSqlProjection([.. sources], [.. joins], expressions, fromClause, distinct, topCount, aggregates, windows, outerTypeResolver);

                case ReservedKeyword { Keyword: Keyword.Where }:
                    ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy);
                    goto ExitWhileTokenLoop;

                case ReservedKeyword { Keyword: Keyword.Order }:
                    if (allowOrderBy)
                        ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy);
                    // When this branch is part of a set-op chain, leave
                    // the cursor on ORDER for the top-level driver to
                    // consume (or for the outer caller to error on, per
                    // SQL Server's per-branch-ORDER-BY rejection).
                    goto ExitWhileTokenLoop;

                // Set-op keywords terminate a branch parse so the outer
                // driver (ParseQueryExpression) can chain branches.
                case ReservedKeyword { Keyword: Keyword.Union or Keyword.Intersect or Keyword.Except }:
                    goto ExitWhileTokenLoop;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        if (topCount is not null && fromClause.OffsetCount is not null)
            throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
        return BuildSynthesizedSqlRow(expressions, fromClause.Excluders, fromClause.OrderBy, topCount, fromClause.OffsetCount, fromClause.FetchCount);
    }

    /// <summary>
    /// Parses the FROM clause: the leftmost source plus zero or more JOIN
    /// clauses, followed by the optional WHERE / GROUP BY / HAVING /
    /// ORDER BY tail. Builds the <see cref="FromSource"/>[] /
    /// <see cref="JoinSpec"/>[] pair the projector consumes, and registers
    /// the multi-source type resolver in
    /// <see cref="ParserContext.OuterTypeResolver"/> so any subqueries
    /// inside WHERE / HAVING / ON predicates see the chained scope stack.
    /// </summary>
    /// <remarks>
    /// On entry, <see cref="ParserContext.Token"/> is the FROM keyword.
    /// On return, the cursor is positioned past the WHERE / GROUP BY /
    /// HAVING / ORDER BY tail, ready for the outer dispatch loop to
    /// observe the next un-consumed token.
    /// </remarks>
    private static void ParseFromSourceAndJoins(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        FromClause fromClause,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool allowOrderBy)
    {
        sources.Add(ParseSingleFromSource(context, depth));

        // Parse JOIN clauses. ParseSingleFromSource ends with the cursor at
        // the lookahead-after-source token (e.g. WHERE, ORDER, JOIN, INNER,
        // LEFT, CROSS, etc.). Loop while we see a JOIN-introducing keyword.
        while (TryParseJoinKeyword(context, out var kind))
        {
            if (kind is JoinKind.CrossApply or JoinKind.OuterApply)
            {
                sources.Add(ParseLateralFromSource(context, depth, sources, outerTypeResolver));
                if (context.Token is ReservedKeyword { Keyword: Keyword.On } onToken)
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(onToken);
                joins.Add(new JoinSpec(kind, onPredicate: null));
                continue;
            }

            sources.Add(ParseSingleFromSource(context, depth));
            BooleanExpression? on = null;
            if (kind == JoinKind.Cross)
            {
                if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                    throw SimulatedSqlException.SyntaxErrorNearKeyword((ReservedKeyword)context.Token);
            }
            else
            {
                if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                on = BooleanExpression.Parse(context);
            }
            joins.Add(new JoinSpec(kind, on));
        }

        // Now register the multi-source type resolver and parse WHERE / etc.
        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], outerTypeResolver, allowOrderBy);
    }

    /// <summary>
    /// Parses the right side of <c>CROSS APPLY</c> / <c>OUTER APPLY</c>:
    /// <c>(SELECT ...) [AS alias]</c>. The inner SELECT is parsed with a
    /// chained outer-type resolver that includes <paramref name="leftSources"/>
    /// (already collected by the surrounding FROM parse) so its body's
    /// references to the left side resolve at parse time. Unlike
    /// <see cref="ParseSingleFromSource"/>, the inner is left as a deferred
    /// <see cref="Selection"/> plan on the returned <see cref="FromSource"/>;
    /// the join driver re-executes it per outer row.
    /// </summary>
    private static FromSource ParseLateralFromSource(
        ParserContext context,
        uint depth,
        List<FromSource> leftSources,
        Func<MultiPartName, SqlType>? surroundingOuter)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var leftSnapshot = leftSources.ToArray();
        SqlType ChainedResolver(MultiPartName name) =>
            ResolveColumnTypeAcrossSources(leftSnapshot, name, surroundingOuter);

        var lateralPlan = Selection.Parse(context, depth + 1, outerTypeResolver: ChainedResolver);

        var schema = lateralPlan.Schema;
        var columnNames = lateralPlan.ColumnNames;
        var lateralColumns = new HeapColumn[schema.Length];
        for (var ci = 0; ci < lateralColumns.Length; ci++)
            lateralColumns[ci] = new HeapColumn(string.Empty, schema[ci], maxLength: null, nullable: true);

        var alias = ConsumeOptionalAlias(context);

        return new FromSource(
            qualifier: alias,
            columnNames: columnNames,
            columns: lateralColumns,
            storedSchema: lateralColumns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: lateralPlan);
    }

    /// <summary>
    /// Parses one FROM source: a table name (with optional alias) or a
    /// derived-table <c>(SELECT ...)</c> (with optional alias). On entry
    /// the cursor is on the FROM or JOIN keyword (caller advances past it
    /// internally via <see cref="ParserContext.GetNextRequired"/>); on
    /// return, the cursor is at the first un-consumed token after the
    /// source — typically WHERE / ORDER / a JOIN keyword / ON / etc.
    /// </summary>
    private static FromSource ParseSingleFromSource(ParserContext context, uint depth)
    {
        var token = context.GetNextRequired();
        switch (token)
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

                return new FromSource(
                    qualifier: heapQualifier,
                    columnNames: heapColumnNames,
                    columns: heapTable.Columns,
                    storedSchema: heapTable.StoredColumns,
                    storageOrdinals: heapTable.StorageOrdinals,
                    lobStore: heapTable.Heap,
                    rows: heapTable.Rows);

            case Operator { Character: '(' }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                // Derived tables don't see the outer scope of the
                // enclosing SELECT — pass null for the inner Parse so
                // projection-time references can't reach out.
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

                // Derived tables have no native name; the alias is the
                // qualifier when present, otherwise null disables the
                // qualified-reference check (the existing simulator
                // accepts derived tables without alias, unlike real SQL).
                var derivedQualifier = ConsumeOptionalAlias(context);

                return new FromSource(
                    qualifier: derivedQualifier,
                    columnNames: derived.ColumnNames,
                    columns: derivedColumns,
                    storedSchema: derivedColumns,
                    storageOrdinals: null,
                    lobStore: null,
                    rows: derived.RowBytes);

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// If <see cref="ParserContext.Token"/> is one of the JOIN-introducing
    /// keywords (<c>INNER</c> / <c>LEFT</c> / <c>CROSS</c> / bare
    /// <c>JOIN</c>), consumes it (plus an optional <c>OUTER</c> after LEFT
    /// and the required <c>JOIN</c> after the inner/left/cross keyword)
    /// and returns the join kind. Returns false otherwise (no
    /// advancement). <c>RIGHT</c> and <c>FULL</c> raise
    /// <see cref="NotSupportedException"/> — RIGHT can be rewritten as
    /// LEFT with the source order swapped, and FULL OUTER isn't modeled.
    /// </summary>
    private static bool TryParseJoinKeyword(ParserContext context, out JoinKind kind)
    {
        kind = JoinKind.Inner;
        if (context.Token is not ReservedKeyword keyword)
            return false;

        switch (keyword.Keyword)
        {
            case Keyword.Inner:
                context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.Inner;
                return true;

            case Keyword.Join:
                kind = JoinKind.Inner;
                return true;

            case Keyword.Left:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.Left;
                return true;

            case Keyword.Right:
                throw new NotSupportedException("RIGHT JOIN isn't modeled yet; rewrite as LEFT JOIN with the source order swapped.");

            case Keyword.Full:
                throw new NotSupportedException("FULL OUTER JOIN isn't modeled yet.");

            case Keyword.Cross:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Join })
                {
                    kind = JoinKind.Cross;
                    return true;
                }
                if (context.MatchContextual(ContextualKeyword.Apply))
                {
                    kind = JoinKind.CrossApply;
                    return true;
                }
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // OUTER as a leading keyword introduces OUTER APPLY (the
            // LEFT/RIGHT/FULL OUTER forms consume OUTER inside their own
            // cases above). The cursor is on OUTER; advance and require APPLY.
            case Keyword.Outer:
                context.MoveNextRequired();
                if (!context.MatchContextual(ContextualKeyword.Apply))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.OuterApply;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// After all FROM sources are parsed, sets
    /// <see cref="ParserContext.OuterTypeResolver"/> to a chained resolver
    /// (this scope's sources, falling through to the prior outer) for the
    /// duration of the WHERE / GROUP BY / HAVING / ORDER BY parse.
    /// Subqueries that appear inside those clauses pick up the chained
    /// resolver and pass it as their own outer resolver to
    /// <see cref="Parse"/>.
    /// </summary>
    private static void ConsumeWhereOrderByWithOuterScope(
        ParserContext context,
        FromClause fromClause,
        FromSource[] sources,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool allowOrderBy)
    {
        SqlType MyResolver(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        var saved = context.OuterTypeResolver;
        context.OuterTypeResolver = MyResolver;
        try
        {
            ConsumeWhereAndOrderBy(context, fromClause, allowOrderBy);
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
    /// this advances past it, optionally past <c>AS alias</c> or a bare
    /// <c>alias</c> (the implicit alias form), and leaves the cursor at
    /// the next un-consumed lookahead position (typically WHERE / GROUP /
    /// HAVING / ORDER / JOIN keywords / ; / null).
    /// </summary>
    private static string? ConsumeOptionalAlias(ParserContext context)
    {
        var nextToken = context.GetNextOptional();
        if (nextToken is ReservedKeyword { Keyword: Keyword.As })
        {
            var alias = context.GetNextRequired<Name>().Value;
            context.MoveNextOptional();
            return alias;
        }
        // Bare-Name alias form (without the AS keyword): "FROM t a JOIN ..."
        // SQL Server accepts this as an alias.
        if (nextToken is Name aliasName)
        {
            context.MoveNextOptional();
            return aliasName.Value;
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
    private static void ConsumeWhereAndOrderBy(ParserContext context, FromClause fromClause, bool allowOrderBy)
    {
        // WHERE / GROUP BY / HAVING reject windowed functions (Msg 4108).
        // Toggle the parser-context flag for the duration of those parses;
        // ORDER BY (which DOES allow windows) is parsed below outside the
        // toggle.
        var savedAllowsWindows = context.AllowsWindowExpressions;
        context.AllowsWindowExpressions = false;
        try
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
        }
        finally
        {
            context.AllowsWindowExpressions = savedAllowsWindows;
        }

        // Skip ORDER BY when this branch is part of a set-op chain — the
        // top-level driver consumes it after combining branches and applies
        // the sort to the combined result. Per SQL Server, per-branch
        // ORDER BY is rejected (Msg 156).
        if (allowOrderBy && context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ParseOrderByItems(context, fromClause.OrderBy);
            ConsumeOffsetFetch(context, fromClause);
        }
    }

    /// <summary>
    /// Consumes the optional <c>OFFSET n ROWS [FETCH NEXT|FIRST k ROW|ROWS ONLY]</c>
    /// tail. Must be called immediately after <see cref="ParseOrderByItems"/>
    /// — SQL Server requires OFFSET/FETCH to follow ORDER BY (no ORDER BY → the
    /// OFFSET keyword is just an unexpected identifier and falls through to a
    /// generic Msg 102 syntax error). FETCH alone (without preceding OFFSET) is
    /// rejected with Msg 153 here. <c>ROW</c> and <c>ROWS</c> are interchangeable;
    /// <c>NEXT</c> and <c>FIRST</c> are interchangeable. Both counts resolve at
    /// parse time and are validated for non-negativity (Msg 10742) and &gt; 0
    /// (Msg 10744).
    /// </summary>
    private static void ConsumeOffsetFetch(ParserContext context, FromClause fromClause)
    {
        // FETCH at this position with no preceding OFFSET → Msg 153.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Fetch })
            throw SimulatedSqlException.FetchInvalidUsageWithoutOffset();

        if (!context.MatchContextual(ContextualKeyword.Offset))
            return;

        context.MoveNextRequired();
        var offsetValue = Expression
            .Parse(context)
            .Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
        var offsetCount = !offsetValue.IsNull && offsetValue.Type == SqlType.Int32
            ? offsetValue.AsInt32
            : throw SimulatedSqlException.TopFetchRequiresInteger();
        if (offsetCount < 0)
            throw SimulatedSqlException.OffsetMustNotBeNegative();
        fromClause.OffsetCount = offsetCount;

        if (!context.MatchContextual(ContextualKeyword.Row) && !context.MatchContextual(ContextualKeyword.Rows))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Fetch })
            return;

        context.MoveNextRequired();
        if (!context.MatchContextual(ContextualKeyword.Next) && !context.MatchContextual(ContextualKeyword.First))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var fetchValue = Expression
            .Parse(context)
            .Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
        var fetchCount = !fetchValue.IsNull && fetchValue.Type == SqlType.Int32
            ? fetchValue.AsInt32
            : throw SimulatedSqlException.TopFetchRequiresInteger();
        if (fetchCount < 1)
            throw SimulatedSqlException.FetchMustBeGreaterThanZero();
        fromClause.FetchCount = fetchCount;

        if (!context.MatchContextual(ContextualKeyword.Row) && !context.MatchContextual(ContextualKeyword.Rows))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (!context.MatchContextual(ContextualKeyword.Only))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
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
    /// zero, suppresses the row. DISTINCT is a no-op for a single-row
    /// result and isn't represented; <paramref name="orderBy"/> is also a
    /// no-op for sort but its presence flips <see cref="HasOrderBy"/>
    /// so the set-op chain rejects per-branch ORDER BY (Msg 156).
    /// </summary>
    private static Selection BuildSynthesizedSqlRow(List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, int? topCount, int? offsetCount, int? fetchCount)
    {
        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        for (var i = 0; i < expressions.Count; i++)
        {
            values[i] = expressions[i].Run(column => throw SimulatedSqlException.InvalidColumnName(column));
            schema[i] = values[i].Type;
            columnNames[i] = expressions[i].Name;
        }

        return new Selection(schema, columnNames, hasOrderBy: orderBy.Count > 0, outerResolver =>
        {
            if (topCount == 0)
                return [];
            if (offsetCount is { } offset && offset > 0)
                return [];
            if (fetchCount is { } fetch && fetch < 1)
                return [];

            SqlValue Resolve(MultiPartName name) =>
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
