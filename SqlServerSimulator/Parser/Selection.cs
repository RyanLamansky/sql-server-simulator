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

    /// <summary>
    /// True when this plan baked in a <c>TOP</c> count, an <c>OFFSET</c>,
    /// or a <c>FETCH</c> at any layer. The CTE body parser pairs this with
    /// <see cref="HasOrderBy"/> to enforce SQL Server's Msg 1033 — a CTE
    /// body's <c>ORDER BY</c> requires a companion <c>TOP</c> / <c>OFFSET</c>.
    /// </summary>
    public readonly bool HasTopOrOffsetOrFetch;

    /// <summary>
    /// True when every projection element is an <see cref="AssignmentExpression"/>
    /// — i.e. <c>SELECT @v = expr [, @w = expr2 ...] [FROM ...]</c>. The
    /// dispatch in <c>Simulation.CreateResultSetsForCommand</c> drains the
    /// row sequence (running the per-row side effects of writing to slots)
    /// but yields a <see cref="SimulatedNonQuery"/> rather than a result
    /// set — matches SQL Server's behavior of suppressing the result-set
    /// envelope for SELECT-assign. Set-op / recursive-CTE / etc. paths
    /// default to false.
    /// </summary>
    public readonly bool IsAssignmentOnly;

    /// <summary>
    /// Target table name for a <c>SELECT … INTO target …</c> statement; null
    /// for a regular SELECT. Captured at parse time when an <c>INTO</c>
    /// clause appears between the projection list and FROM. Set-op chains
    /// propagate the first-branch INTO through <c>CombineSetOps</c>;
    /// a subsequent branch carrying its own INTO is rejected as a syntax
    /// error (real SQL Server allows INTO only on the first branch). The
    /// dispatch routes Selections with this set to the SELECT INTO handler
    /// rather than the regular execute path.
    /// </summary>
    public readonly MultiPartName? IntoTarget;

    /// <summary>
    /// Pre-computed destination schema (column names + types + nullability
    /// + identity flags) for a <c>SELECT INTO</c> statement; null when
    /// <see cref="IntoTarget"/> is null. Built during projection planning
    /// from the projection expressions and FROM sources, applying SQL
    /// Server's documented schema-inference rules (direct refs preserve
    /// source nullability + identity; expressions / aggregates / casts /
    /// COALESCE always nullable; ISNULL non-null when either arg is
    /// non-null; CASE non-null when every branch is non-null; string `+`
    /// non-null when both operands non-null; integer arithmetic always
    /// nullable due to overflow). The SELECT INTO handler reads this
    /// directly to create the destination heap table.
    /// </summary>
    public readonly HeapColumn[]? DestColumnSchema;

    /// <summary>
    /// Non-null when this Selection is shape-eligible to back an updatable
    /// view: exactly one FROM source, no JOINs, no DISTINCT, no aggregates,
    /// no windows, no GROUP BY, no HAVING, no set-op chain. The
    /// <see cref="ViewUpdatabilityProfile"/> exposes the single source, the
    /// projection expressions, and the WHERE excluders — enough for
    /// <see cref="View"/> to derive its base-column map and re-evaluate
    /// the body's WHERE against a base-table row at DML time. Null for any
    /// other shape; the DML-through-view path inspects the null+
    /// <see cref="ViewUpdatabilityRejection"/> to surface
    /// <strong>Msg 4403</strong> / <strong>Msg 4406</strong> / <strong>Msg
    /// 4405</strong>.
    /// </summary>
    internal readonly ViewUpdatabilityProfile? UpdatabilityProfile;

    /// <summary>
    /// When <see cref="UpdatabilityProfile"/> is null, the reason — drives
    /// Msg 4403 (aggregates / DISTINCT / GROUP BY) vs Msg 4406 (derived
    /// projection) vs Msg 4405 (multi-base-table) at DML time. Always
    /// <see cref="ViewUpdatabilityRejection.None"/> when the profile is set.
    /// </summary>
    internal readonly ViewUpdatabilityRejection UpdatabilityRejection;

    private readonly Func<BatchContext, Func<MultiPartName, SqlValue>?, IEnumerable<byte[]>> rowSource;

    private Selection(SqlType[] schema, string[] columnNames, bool hasOrderBy, bool hasTopOrOffsetOrFetch, Func<BatchContext, Func<MultiPartName, SqlValue>?, IEnumerable<byte[]>> rowSource, bool isAssignmentOnly = false, MultiPartName? intoTarget = null, HeapColumn[]? destColumnSchema = null, ViewUpdatabilityProfile? updatabilityProfile = null, ViewUpdatabilityRejection updatabilityRejection = ViewUpdatabilityRejection.UnsupportedShape)
    {
        this.Schema = schema;
        this.ColumnNames = columnNames;
        this.HasOrderBy = hasOrderBy;
        this.HasTopOrOffsetOrFetch = hasTopOrOffsetOrFetch;
        this.IsAssignmentOnly = isAssignmentOnly;
        this.rowSource = rowSource;
        this.IntoTarget = intoTarget;
        this.DestColumnSchema = destColumnSchema;
        this.UpdatabilityProfile = updatabilityProfile;
        this.UpdatabilityRejection = updatabilityProfile is null ? updatabilityRejection : ViewUpdatabilityRejection.None;
    }

    /// <summary>
    /// Wraps a <see cref="CatalogView"/>'s row generator + column schema as a
    /// <see cref="Selection"/> suitable for use as a <see cref="FromSource.LateralPlan"/>.
    /// Executing the resulting plan invokes the view's generator with the
    /// live <see cref="BatchContext"/>, encodes each row's
    /// <see cref="SqlValue"/> array via <c>RowEncoder.EncodeRow</c>, and
    /// streams the bytes. Re-executes on each call so changes made earlier
    /// in the same batch (CREATE TABLE, CREATE SCHEMA, DROP TABLE) appear
    /// immediately.
    /// </summary>
    internal static Selection ForCatalogView(CatalogView view)
    {
        var schema = new SqlType[view.Columns.Length];
        var columnNames = new string[view.Columns.Length];
        for (var i = 0; i < view.Columns.Length; i++)
        {
            schema[i] = view.Columns[i].Type;
            columnNames[i] = view.Columns[i].Name;
        }
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, _) => view.RowGenerator(batch).Select(values => RowEncoder.EncodeRow(view.Columns, values)));
    }

    /// <summary>
    /// Materializes the SELECT against the given outer-row resolver
    /// (null for top-level / non-correlated scopes). Each call produces a
    /// fresh <see cref="SimulatedSqlResultSet"/>; the underlying row sequence
    /// is itself lazy or eager depending on whether DISTINCT / ORDER BY /
    /// aggregation force buffering. <paramref name="batch"/> is the
    /// executing <see cref="BatchContext"/> — threaded through so
    /// <see cref="Expression.Run(RuntimeContext)"/> calls inside the row
    /// generation can build a <see cref="RuntimeContext"/> with explicit
    /// per-batch / per-session / per-database access.
    /// </summary>
    public SimulatedSqlResultSet Execute(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver = null) =>
        new(this.Schema, this.ColumnNames, this.rowSource(batch, outerResolver));

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

        // OPTION (MAXRECURSION N) — statement-level hint applied to all
        // recursive CTEs in the surrounding statement. Only MAXRECURSION
        // is modeled; other hints (OPTIMIZE FOR / RECOMPILE / etc.) are
        // valid in real SQL Server but raise NotSupportedException here.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Option })
            ParseOptionClause(context);

        return combined;
    }

    /// <summary>
    /// Parses the trailing <c>OPTION (MAXRECURSION N)</c> hint clause and
    /// applies the value to every <see cref="CteBinding"/> in scope. Only
    /// MAXRECURSION is modeled; reaching any other hint raises
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    private static void ParseOptionClause(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.MaxRecursion })
                throw new NotSupportedException("Only OPTION (MAXRECURSION N) is modeled in the OPTION clause.");

            if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } limitValue })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var limit = limitValue.AsInt32;
            if (limit is < 0 or > 32_767)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (context.CteBindings is { } bindings)
            {
                foreach (var binding in bindings.Values)
                    binding.MaxRecursion = limit;
            }

            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
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
    internal static Selection ParseIntersectChain(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool isFirstBranch)
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
    internal static Selection ParseSingleSelectStatement(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver, bool allowOrderBy)
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
                .Run(new RuntimeContext(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), context.Batch));
            topCount = !resolved.IsNull && resolved.Type == SqlType.Int32
                ? resolved.AsInt32
                : throw SimulatedSqlException.TopFetchRequiresInteger();
        }

        List<Expression> expressions = [];
        var fromClause = new FromClause();
        MultiPartName? intoTarget = null;

        do
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.From }:
                case ReservedKeyword { Keyword: Keyword.Into }:
                    break;

                case ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce or Keyword.NullIf or Keyword.Case or Keyword.Current_Timestamp }:
                    // LEFT, RIGHT, CONVERT, TRY_CONVERT, COALESCE, NULLIF are
                    // reserved keywords but valid as function-call heads
                    // inside a SELECT projection. CASE introduces an inline
                    // expression (see CaseExpression.ParseCase). CURRENT_TIMESTAMP
                    // is uniquely a parens-less reserved-keyword expression
                    // (see CurrentTimeFunction).
                    expressions.Add(Expression.Parse(context));
                    break;

                // Set-op keywords at the outer-switch position (i.e. after
                // an `AS alias` continued the loop) terminate this branch
                // so the set-op driver can chain.
                case ReservedKeyword { Keyword: Keyword.Union or Keyword.Intersect or Keyword.Except }:
                    goto ExitWhileTokenLoop;

                // At the top level (depth 0), the start of another statement
                // terminates this SELECT and lets the dispatch loop pick up
                // where it left off. Real SQL Server allows back-to-back
                // statements without `;` between them; we mirror by stopping
                // the projection-list parse here. Inside a subquery (depth > 0)
                // these keywords are still invalid — fall through to the
                // generic Msg 156 catch-all below.
                case Operator { Character: ';' } when depth == 0:
                case ReservedKeyword
                {
                    Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete
                        or Keyword.Merge or Keyword.Begin or Keyword.Commit or Keyword.Rollback
                        or Keyword.Save or Keyword.Create or Keyword.Drop or Keyword.Alter or Keyword.Dbcc
                        or Keyword.Set or Keyword.Declare or Keyword.If or Keyword.Else or Keyword.End
                        or Keyword.While or Keyword.Break or Keyword.Continue or Keyword.Return
                        or Keyword.Print or Keyword.WaitFor or Keyword.Truncate
                } when depth == 0:
                    goto ExitWhileTokenLoop;

                // WITH at the start of a projection element is unambiguous:
                // it can only mean a CTE-prefixed follow-up statement. Real
                // SQL Server raises Msg 319 here rather than the generic
                // Msg 156 from the catch-all below — telling the user to
                // separate statements with `;`.
                case ReservedKeyword { Keyword: Keyword.With } when depth == 0:
                    throw SimulatedSqlException.CteRequiresPrecedingSemicolon();

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

                // Bare `*` as a projection element (the first thing after
                // SELECT, or the first thing after a comma). Within a
                // projected expression, `*` is the multiplication operator and
                // is handled by Expression.Parse's binary loop instead.
                case Operator { Character: '*' }:
                    expressions.Add(new StarProjection(null));
                    context.MoveNextOptional();
                    break;

                // SELECT-assign disambiguation: `@v = expr` at projection-
                // element-start position is variable assignment;
                // `@v` followed by anything else (`+`, `,`, AS, etc.) is just
                // a variable read. Peek past the @v token to decide.
                case AtPrefixedString atPrefixed:
                    {
                        var checkpoint = context.SaveCheckpoint();
                        _ = context.MoveNext();
                        if (context.Token is Operator { Character: '=' })
                        {
                            var slot = context.Batch.GetVariableSlot(atPrefixed.Value);
                            context.MoveNextRequired();
                            var rhs = Expression.Parse(context);
                            expressions.Add(new AssignmentExpression(slot, rhs));
                        }
                        else
                        {
                            context.RestoreCheckpoint(checkpoint);
                            expressions.Add(Expression.Parse(context));
                        }
                    }
                    break;

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
                    ExpandStars(expressions, sources);
                    return BuildSqlProjection([.. sources], [.. joins], expressions, fromClause, distinct, topCount, aggregates, windows, outerTypeResolver, ResolveAssignmentMode(expressions), intoTarget);

                // SELECT projection INTO target [FROM ...] — captures the
                // destination table name. Real SQL Server requires every
                // projection to have a name (Msg 1038) and rejects duplicate
                // names (Msg 2705); both validations happen at build time
                // alongside the schema-inference walk, so we can flag the
                // offending column with the target table name in the message.
                case ReservedKeyword { Keyword: Keyword.Into }:
                    if (intoTarget is not null)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    intoTarget = BatchContext.ParseObjectName(context);
                    continue;

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

                // At the top level (depth 0), the start of another statement
                // terminates this SELECT — the dispatch loop picks up there.
                // Inside a subquery these keywords stay invalid (fall through
                // to the generic Msg 102 below).
                case ReservedKeyword
                {
                    Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete
                        or Keyword.Merge or Keyword.Begin or Keyword.Commit or Keyword.Rollback
                        or Keyword.Save or Keyword.Create or Keyword.Drop or Keyword.Alter or Keyword.Dbcc
                        or Keyword.Set or Keyword.Declare or Keyword.If or Keyword.Else or Keyword.End
                        or Keyword.While or Keyword.Break or Keyword.Continue or Keyword.Return
                        or Keyword.Print or Keyword.WaitFor or Keyword.Truncate
                } when depth == 0:
                    goto ExitWhileTokenLoop;

                // WITH at the projection-element-end position can only mean a
                // CTE-prefixed follow-up statement; raise Msg 319 to mirror
                // SQL Server's specific error here.
                case ReservedKeyword { Keyword: Keyword.With } when depth == 0:
                    throw SimulatedSqlException.CteRequiresPrecedingSemicolon();
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        if (topCount is not null && fromClause.OffsetCount is not null)
            throw SimulatedSqlException.TopAndOffsetMutuallyExclusive();
        return BuildSynthesizedSqlRow(context.Batch, expressions, fromClause.Excluders, fromClause.OrderBy, topCount, fromClause.OffsetCount, fromClause.FetchCount, ResolveAssignmentMode(expressions), intoTarget);
    }

    /// <summary>
    /// Walks the parsed projection list to detect <c>SELECT @v = expr</c>
    /// mode. Returns true when every projection element is an
    /// <see cref="AssignmentExpression"/>; false when none are; raises Msg
    /// 141 when the projection mixes assignment and retrieval elements
    /// (probe-confirmed real SQL Server behavior).
    /// </summary>
    private static bool ResolveAssignmentMode(List<Expression> expressions)
    {
        if (expressions.Count == 0) return false;
        var assignCount = 0;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (expressions[i] is AssignmentExpression)
                assignCount++;
        }
        return assignCount switch
        {
            0 => false,
            var n when n == expressions.Count => true,
            _ => throw SimulatedSqlException.SelectAssignmentMixedWithRetrieval(),
        };
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
        ParseSourcesAndJoins(context, depth, sources, joins, outerTypeResolver);

        // Now register the multi-source type resolver and parse WHERE / etc.
        ConsumeWhereOrderByWithOuterScope(context, fromClause, [.. sources], outerTypeResolver, allowOrderBy);
    }

    /// <summary>
    /// Pure source-and-joins parser, separable from WHERE / ORDER BY
    /// consumption. Used by both <see cref="ParseFromSourceAndJoins"/> (which
    /// adds WHERE consumption on top) and the UPDATE / DELETE mutation paths
    /// (which handle WHERE separately because the leading-identifier target
    /// binding has to happen first). Enters with the cursor on the
    /// <c>FROM</c> keyword (or, in mutation context, on the FROM keyword
    /// position); leaves the cursor at the lookahead-after-last-source token
    /// (typically WHERE, end-of-statement, or set-op chain).
    /// </summary>
    internal static void ParseSourcesAndJoins(
        ParserContext context,
        uint depth,
        List<FromSource> sources,
        List<JoinSpec> joins,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        sources.Add(ParseSingleFromSource(context, depth, outerTypeResolver));

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

            // Joined-source derived tables can also correlate, but the JoinDriver
            // path for non-leftmost LateralPlan sources doesn't apply ON
            // predicates or LEFT-fill. Keep the chained outer-type-resolver in
            // play so a correlated derived table here is at least diagnosed
            // (NotSupportedException at execute time) rather than silently
            // resolving against a wrong scope.
            sources.Add(ParseSingleFromSource(context, depth, outerTypeResolver));
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
        // Peek next token. A parenthesized derived table `(SELECT ...)`
        // stays on the dedicated path so the chained outer-type resolver
        // can be wired into the inner Selection's parse. A leading Name
        // routes to ParseSingleFromSource ONLY when it resolves to an
        // inline TVF — APPLY requires a derived table or a TVF; a bare
        // table after APPLY is invalid (probe-confirmed via real SQL
        // Server, guarded by ApplyTests).
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextRequired();
        if (next is Name)
        {
            // Peek the resolved object name to decide between TVF route
            // and reject-as-syntax-error.
            var afterNameCheckpoint = context.SaveCheckpoint();
            var resolvedName = BatchContext.ParseObjectName(context);
            var resolvedIsTvf = context.Batch.TryResolveFunction(resolvedName, out var resolvedFn)
                && resolvedFn is InlineTableValuedFunction;
            context.RestoreCheckpoint(afterNameCheckpoint);
            if (resolvedIsTvf)
            {
                context.RestoreCheckpoint(checkpoint);
                return ParseSingleFromSource(context, depth, surroundingOuter);
            }
            // Restore + fall through to the generic syntax-error throw.
            context.RestoreCheckpoint(checkpoint);
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (next is not Operator { Character: '(' })
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
    private static FromSource ParseSingleFromSource(ParserContext context, uint depth, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var token = context.GetNextRequired();
        switch (token)
        {
            case Name tableName:
                // OPENJSON dispatch wins over CTE / table lookup. Case-
                // insensitive match on the function name; ParseOpenJson
                // enforces the trailing `(`. SQL Server reserves OPENJSON
                // as a built-in rowset function, so unconditional name-
                // dispatch matches real-server behavior — a CTE / table
                // named OPENJSON would already conflict on a real server.
                // OPENJSON never carries a schema qualifier, so this fires
                // before ParseObjectName / cursor advance.
                if (string.Equals(tableName.Value, "OPENJSON", StringComparison.OrdinalIgnoreCase))
                {
                    var openJsonPlan = ParseOpenJson(context, outerTypeResolver);
                    var openJsonColumns = new HeapColumn[openJsonPlan.Schema.Length];
                    for (var ci = 0; ci < openJsonColumns.Length; ci++)
                        openJsonColumns[ci] = new HeapColumn(string.Empty, openJsonPlan.Schema[ci], maxLength: null, nullable: true);
                    var openJsonAlias = ConsumeOptionalAliasInPlace(context);
                    return new FromSource(
                        qualifier: openJsonAlias,
                        columnNames: openJsonPlan.ColumnNames,
                        columns: openJsonColumns,
                        storedSchema: openJsonColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: openJsonPlan);
                }

                // Multi-part name parse: advances the cursor past the last
                // dotted segment, leaving Token on the first non-name token
                // (alias / AS / WHERE / JOIN / etc.). CTE binding only fires
                // for a single-segment leaf (CTE names can't be schema-
                // qualified — they're aliases, not real tables).
                var objectName = BatchContext.ParseObjectName(context);

                if (objectName.Count == 1
                    && context.CteBindings is { } cteBindings
                    && cteBindings.TryGetValue(objectName.Leaf, out var cteBinding))
                {
                    // Recursive-part self-reference: the body parser has
                    // captured the anchor's schema and toggled
                    // IsRecursivePartParse. The FromSource pulls rows from
                    // the binding's per-iteration rowset slot, which the
                    // recursive Selection rebinds between iterations.
                    if (cteBinding.IsRecursivePartParse && cteBinding.Schema is { } recursiveSchema)
                    {
                        cteBinding.SelfReferenceCountInCurrentBranch++;
                        var recursiveColumns = new HeapColumn[recursiveSchema.Length];
                        for (var ci = 0; ci < recursiveColumns.Length; ci++)
                            recursiveColumns[ci] = new HeapColumn(string.Empty, recursiveSchema[ci], maxLength: null, nullable: true);
                        var recursiveAlias = ConsumeOptionalAlias(context);
                        return new FromSource(
                            qualifier: recursiveAlias ?? cteBinding.Name,
                            columnNames: cteBinding.ColumnNames,
                            columns: recursiveColumns,
                            storedSchema: recursiveColumns,
                            storageOrdinals: null,
                            lobStore: null,
                            rows: SelfReferenceRows(cteBinding));
                    }

                    if (cteBinding.Plan is null)
                        throw SimulatedSqlException.RecursiveCteMissingUnionAll(cteBinding.Name);

                    var cteColumns = new HeapColumn[cteBinding.Plan.Schema.Length];
                    for (var ci = 0; ci < cteColumns.Length; ci++)
                        cteColumns[ci] = new HeapColumn(string.Empty, cteBinding.Plan.Schema[ci], maxLength: null, nullable: true);

                    var cteAlias = ConsumeOptionalAlias(context);

                    return new FromSource(
                        qualifier: cteAlias ?? cteBinding.Name,
                        columnNames: cteBinding.ColumnNames,
                        columns: cteColumns,
                        storedSchema: cteColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: cteBinding.Plan);
                }

                // Catalog views (sys.tables / sys.objects / sys.schemas)
                // route to a virtual FromSource whose rows project from live
                // metadata at execution time — wrapped as a LateralPlan so
                // each Execute re-runs the generator and picks up CREATE /
                // DROP changes from earlier in the same batch.
                if (context.Batch.TryResolveCatalogView(objectName, out var catalogView))
                {
                    var catalogColumnNames = new string[catalogView.Columns.Length];
                    for (var ci = 0; ci < catalogColumnNames.Length; ci++)
                        catalogColumnNames[ci] = catalogView.Columns[ci].Name;
                    var catalogAlias = ConsumeOptionalAlias(context);
                    return new FromSource(
                        qualifier: catalogAlias ?? catalogView.Name,
                        columnNames: catalogColumnNames,
                        columns: catalogView.Columns,
                        storedSchema: catalogView.Columns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForCatalogView(catalogView));
                }

                // View resolution: `FROM schema.view [alias]` or
                // `FROM view [alias]` (unqualified). Routes before table
                // lookup so a view with the same name as a table (rare —
                // collisions raise Msg 2714 at CREATE) wins; in practice
                // the name namespace is shared, so either resolver finds
                // the right object. Views are re-parsed and executed per
                // call via Selection.ForView (a lateral plan); the body's
                // own FROM sources resolve in a child batch isolated from
                // the caller's parser cursor.
                if (context.Batch.TryResolveView(objectName, out var resolvedView))
                {
                    var viewColumnNames = new string[resolvedView.OutputColumns.Length];
                    for (var ci = 0; ci < viewColumnNames.Length; ci++)
                        viewColumnNames[ci] = resolvedView.OutputColumns[ci].Name;
                    var viewAlias = ConsumeOptionalAlias(context);
                    return new FromSource(
                        qualifier: viewAlias ?? resolvedView.Name,
                        columnNames: viewColumnNames,
                        columns: resolvedView.OutputColumns,
                        storedSchema: resolvedView.OutputColumns,
                        storageOrdinals: null,
                        lobStore: null,
                        rows: [],
                        lateralPlan: Selection.ForView(resolvedView),
                        backingView: resolvedView);
                }

                // Inline TVF call: `FROM schema.fn(args) [alias]`. Detected
                // when the resolved function is a TVF AND `(` follows the
                // name (cursor is on the name leaf post-ParseObjectName; peek
                // the next token via a checkpoint). A ScalarFunction here
                // falls through to the table-lookup branch and surfaces
                // Msg 208 (probe-confirmed: real SQL Server treats
                // `FROM dbo.scalar_fn(...)` as a missing-object error, not
                // a kind-mismatch).
                if (context.Batch.TryResolveFunction(objectName, out var function)
                    && function is InlineTableValuedFunction tvf)
                {
                    var checkpoint = context.SaveCheckpoint();
                    context.MoveNextOptional();
                    if (context.Token is Operator { Character: '(' })
                    {
                        context.MoveNextRequired();
                        var tvfArgs = Expressions.UserFunctionCall.ParseFunctionArguments(tvf, context);
                        // ParseFunctionArguments leaves the cursor on the closing `)`.
                        var tvfAlias = ConsumeOptionalAlias(context);
                        return new FromSource(
                            qualifier: tvfAlias ?? tvf.Name,
                            columnNames: [.. tvf.OutputColumns.Select(c => c.Name)],
                            columns: tvf.OutputColumns,
                            storedSchema: tvf.OutputColumns,
                            storageOrdinals: null,
                            lobStore: null,
                            rows: [],
                            lateralPlan: Selection.ForInlineTvf(tvf, tvfArgs));
                    }
                    context.RestoreCheckpoint(checkpoint);
                }

                if (!context.Batch.TryResolveTable(objectName, out var heapTable))
                    throw SimulatedSqlException.InvalidObjectName(objectName);

                var heapColumnNames = new string[heapTable.Columns.Length];
                for (var ci = 0; ci < heapColumnNames.Length; ci++)
                    heapColumnNames[ci] = heapTable.Columns[ci].Name;

                var heapAlias = ConsumeOptionalAlias(context);
                var heapQualifier = heapAlias ?? objectName.Leaf;

                return new FromSource(
                    qualifier: heapQualifier,
                    columnNames: heapColumnNames,
                    columns: heapTable.Columns,
                    storedSchema: heapTable.StoredColumns,
                    storageOrdinals: heapTable.StorageOrdinals,
                    lobStore: heapTable.Heap,
                    rows: heapTable.Rows,
                    backingTable: heapTable);

            // Table-variable source: <c>FROM @t [alias]</c>. Routes through
            // BatchContext.TableVariables instead of the regular schema dict;
            // missing @t raises Msg 1087 (distinct from regular tables'
            // Msg 208) since the user's spelling tells us they meant a
            // table variable, not a missing table.
            case AtPrefixedString:
                var tvName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
                if (!context.Batch.TryResolveTable(tvName, out var tvTable))
                    throw SimulatedSqlException.MustDeclareTableVariable(tvName.Leaf);
                var tvColumnNames = new string[tvTable.Columns.Length];
                for (var ci = 0; ci < tvColumnNames.Length; ci++)
                    tvColumnNames[ci] = tvTable.Columns[ci].Name;
                var tvAlias = ConsumeOptionalAlias(context);
                return new FromSource(
                    qualifier: tvAlias ?? tvName.Leaf,
                    columnNames: tvColumnNames,
                    columns: tvTable.Columns,
                    storedSchema: tvTable.StoredColumns,
                    storageOrdinals: tvTable.StorageOrdinals,
                    lobStore: tvTable.Heap,
                    rows: tvTable.Rows,
                    backingTable: tvTable);

            case Operator { Character: '(' }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                // Derived tables can correlate to outer scope (SQL Server
                // allows any FROM derived table to reference outer columns,
                // not just APPLY). Static parse-time correlation detection
                // misses runtime-only references (WHERE / ON predicates use
                // Run, not GetSqlType), so the safe path is to always defer
                // execution into FromSource.LateralPlan and re-run per outer
                // resolver invocation. Non-correlated derived tables pay the
                // same per-Execute cost as before (the inner plan still runs
                // once per outer Execute call, just routed through
                // lateralPlan.Execute).
                //
                // Pass through the chained outer-type-resolver so the inner
                // Parse can statically type-resolve any projection / GROUP
                // BY references that point at outer columns. Both
                // <see cref="ParserContext.OuterTypeResolver"/> (set inside
                // the WHERE / GROUP BY / HAVING parse of the enclosing
                // Selection) and the explicit <paramref name="outerTypeResolver"/>
                // chain (set when this FROM source is itself nested inside
                // a subquery) are honored.
                var derivedSelection = Selection.Parse(context, depth + 1,
                    outerTypeResolver: context.OuterTypeResolver ?? outerTypeResolver);

                // Inner SELECT result rows are LOB-inline (projections never
                // emit LOB pointers because they have no destination Heap),
                // so build a HeapColumn[] schema from the SqlType[] so the
                // decoder still strips marker bytes for text/ntext/image
                // columns; lobStore is null because no chain to follow.
                var derivedColumns = new HeapColumn[derivedSelection.Schema.Length];
                for (var ci = 0; ci < derivedColumns.Length; ci++)
                    derivedColumns[ci] = new HeapColumn(string.Empty, derivedSelection.Schema[ci], maxLength: null, nullable: true);

                // Derived tables have no native name; the alias is the
                // qualifier when present, otherwise null disables the
                // qualified-reference check (the existing simulator
                // accepts derived tables without alias, unlike real SQL).
                var derivedQualifier = ConsumeOptionalAlias(context);

                return new FromSource(
                    qualifier: derivedQualifier,
                    columnNames: derivedSelection.ColumnNames,
                    columns: derivedColumns,
                    storedSchema: derivedColumns,
                    storageOrdinals: null,
                    lobStore: null,
                    rows: [],
                    lateralPlan: derivedSelection);

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// If <see cref="ParserContext.Token"/> is one of the JOIN-introducing
    /// keywords (<c>INNER</c> / <c>LEFT</c> / <c>RIGHT</c> / <c>FULL</c> /
    /// <c>CROSS</c> / bare <c>JOIN</c>), consumes it (plus an optional
    /// <c>OUTER</c> after LEFT/RIGHT/FULL and the required <c>JOIN</c>
    /// keyword) and returns the join kind. Returns false otherwise (no
    /// advancement).
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
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.Right;
                return true;

            case Keyword.Full:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Outer })
                    context.MoveNextRequired();
                if (context.Token is not ReservedKeyword { Keyword: Keyword.Join })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                kind = JoinKind.Full;
                return true;

            case Keyword.Cross:
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Join })
                {
                    kind = JoinKind.Cross;
                    return true;
                }
                if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Apply })
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
                if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Apply })
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
    /// Yields the CTE binding's current iteration rowset to a recursive
    /// branch's self-reference FromSource. The runtime
    /// <see cref="CteBinding.CurrentIterationRows"/> slot is rebound by
    /// <see cref="FromRecursiveCte"/> between iterations, so each
    /// enumerator created here pulls the per-iteration rowset captured at
    /// iterator-start time.
    /// </summary>
    private static IEnumerable<byte[]> SelfReferenceRows(CteBinding binding)
    {
        var rows = binding.CurrentIterationRows;
        if (rows is null)
            yield break;
        foreach (var row in rows)
            yield return row;
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

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Offset })
            return;

        context.MoveNextRequired();
        var offsetValue = Expression
            .Parse(context)
            .Run(new RuntimeContext(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), context.Batch));
        var offsetCount = !offsetValue.IsNull && offsetValue.Type == SqlType.Int32
            ? offsetValue.AsInt32
            : throw SimulatedSqlException.TopFetchRequiresInteger();
        if (offsetCount < 0)
            throw SimulatedSqlException.OffsetMustNotBeNegative();
        fromClause.OffsetCount = offsetCount;

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Row or ContextualKeyword.Rows })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Fetch })
            return;

        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Next or ContextualKeyword.First })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var fetchValue = Expression
            .Parse(context)
            .Run(new RuntimeContext(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), context.Batch));
        var fetchCount = !fetchValue.IsNull && fetchValue.Type == SqlType.Int32
            ? fetchValue.AsInt32
            : throw SimulatedSqlException.TopFetchRequiresInteger();
        if (fetchCount < 1)
            throw SimulatedSqlException.FetchMustBeGreaterThanZero();
        fromClause.FetchCount = fetchCount;

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Row or ContextualKeyword.Rows })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Only })
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
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Asc }:
                    context.MoveNextOptional();
                    break;
                case ReservedKeyword { Keyword: Keyword.Desc }:
                    descending = true;
                    context.MoveNextOptional();
                    break;
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
    private static Selection BuildSynthesizedSqlRow(BatchContext parseBatch, List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, int? topCount, int? offsetCount, int? fetchCount, bool isAssignmentOnly, MultiPartName? intoTarget)
    {
        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        // Run-then-GetSqlType: any expression whose runtime path raises a
        // type-error message with operator-name wording (e.g. <c>dt + time</c>
        // → "add operator") emits that error from Run before GetSqlType has
        // a chance to throw a Promote-side message with comparison-only
        // wording. For successful runs, GetSqlType then bridges the matched
        // branch's runtime type to the joint-promoted schema (CASE / Coalesce
        // with mixed-type branches in a FROM-less SELECT).
        var parseRuntime = new RuntimeContext(column => throw SimulatedSqlException.InvalidColumnName(column), parseBatch);
        for (var i = 0; i < expressions.Count; i++)
        {
            var raw = expressions[i].Run(parseRuntime);
            schema[i] = expressions[i].GetSqlType(column => throw SimulatedSqlException.InvalidColumnName(column));
            columnNames[i] = expressions[i].Name;
            values[i] = raw.IsNull || raw.Type == schema[i] ? raw : raw.CoerceTo(schema[i]);
        }

        return new Selection(schema, columnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topCount.HasValue || offsetCount.HasValue || fetchCount.HasValue,
            (batch, outerResolver) =>
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
                if (excluder.Run(new RuntimeContext(Resolve, batch)) != true)
                    return [];
            }

            return [RowEncoder.EncodeRow(schema, values)];
        }, isAssignmentOnly,
        intoTarget,
        // FROM-less SELECT INTO: no source sources/joins to inspect, so the
        // analyzer routes through the empty-FROM branch and produces
        // dest columns with literal-derived nullability and no identity.
        destColumnSchema: intoTarget is { } target
            ? ComputeIntoDestSchema(target, expressions, schema, columnNames, [], [])
            : null);
    }

    /// <summary>
    /// Expands any <see cref="StarProjection"/> markers in the projection
    /// list into per-column <see cref="Reference"/> expressions, using each
    /// FROM source's <see cref="FromSource.Qualifier"/> to disambiguate
    /// same-named columns across sources (so multi-source <c>SELECT *</c>
    /// doesn't trip Msg 209). Bare <c>*</c> emits every column from every
    /// source in source order; <c>&lt;qualifier&gt;.*</c> filters to the
    /// named source. An unbound qualifier raises Msg 4104.
    /// </summary>
    private static void ExpandStars(List<Expression> expressions, List<FromSource> sources)
    {
        for (var i = expressions.Count - 1; i >= 0; i--)
        {
            if (expressions[i] is not StarProjection star)
                continue;

            var expanded = new List<Expression>();
            if (star.Qualifier is null)
            {
                foreach (var source in sources)
                    AppendSourceColumns(expanded, source);
            }
            else
            {
                FromSource? matched = null;
                foreach (var source in sources)
                {
                    if (source.Qualifier is { } q && Collation.Default.Equals(q, star.Qualifier))
                    {
                        matched = source;
                        break;
                    }
                }
                if (matched is null)
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{star.Qualifier}.*");
                AppendSourceColumns(expanded, matched);
            }

            expressions.RemoveAt(i);
            expressions.InsertRange(i, expanded);
        }

        static void AppendSourceColumns(List<Expression> destination, FromSource source)
        {
            foreach (var col in source.ColumnNames)
            {
                destination.Add(source.Qualifier is { } q
                    ? new Reference(q, col)
                    : new Reference(col));
            }
        }
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
