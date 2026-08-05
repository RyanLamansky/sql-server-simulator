using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator for the window functions the simulator models.
/// <see cref="RowNumber"/> / <see cref="Rank"/> / <see cref="DenseRank"/> /
/// <see cref="NTile"/> are ranking functions (require ORDER BY inside OVER,
/// emit one value per row based on the sorted partition; reject explicit
/// frames). <see cref="Lag"/> / <see cref="Lead"/> are offset functions
/// (also reject explicit frames). <see cref="FirstValue"/> /
/// <see cref="LastValue"/> read a specific row's value within the current
/// frame; their default-frame semantic (<c>RANGE UNBOUNDED PRECEDING TO
/// CURRENT ROW</c>) means LAST_VALUE returns the current row's value /
/// peer-tie last unless an explicit frame widens the window.
/// <see cref="Aggregate"/> wraps an <see cref="AggregateExpression"/>
/// (<c>SUM/AVG/COUNT/...</c> with <c>OVER</c>) and applies the same per-row
/// frame extent for running totals + sliding aggregations.
/// <see cref="CumeDist"/> / <see cref="PercentRank"/> are distribution
/// ranking functions (require ORDER BY inside OVER like the other ranking
/// functions, reject explicit frames). <see cref="PercentileCont"/> /
/// <see cref="PercentileDisc"/> are ordered-set analytic functions: their
/// ordering comes from a mandatory <c>WITHIN GROUP (ORDER BY ...)</c> clause,
/// their OVER clause carries only PARTITION BY (ORDER BY there is rejected),
/// and the per-partition percentile is broadcast to every row.
/// </summary>
internal enum WindowKind
{
    RowNumber,
    Aggregate,
    Rank,
    DenseRank,
    NTile,
    Lag,
    Lead,
    FirstValue,
    LastValue,
    CumeDist,
    PercentRank,
    PercentileCont,
    PercentileDisc,
}

/// <summary>
/// SQL window function call (<c>ROW_NUMBER() OVER(...)</c> or
/// <c>SUM/AVG/COUNT/MIN/MAX/STDEV/STDEVP/VAR/VARP/COUNT_BIG/CHECKSUM_AGG/APPROX_COUNT_DISTINCT
/// (...) OVER (PARTITION BY ...)</c>). Like <see cref="AggregateExpression"/>,
/// the value can't be computed row-by-row in isolation — the executor
/// buffers the post-WHERE tuple stream, partitions rows by
/// <see cref="PartitionBy"/>, and either sorts each partition by
/// <see cref="OrderBy"/> + assigns ROW_NUMBER ranks, or runs an
/// <see cref="Aggregator"/> through the partition + broadcasts the
/// per-partition result, before binding the per-tuple result via
/// <see cref="BindResult"/>.
/// </summary>
/// <remarks>
/// EF Core 10's emission shape: ROW_NUMBER lives inside an inner SELECT
/// that's wrapped as a derived table; the outer query filters via
/// <c>WHERE row &lt;= N</c> (Take) or <c>WHERE 1 &lt; row AND row &lt;= K</c>
/// (Skip+Take). Aggregate-OVER broadcasts a per-group result; with
/// <c>ORDER BY</c> inside OVER the default frame is
/// <c>RANGE UNBOUNDED PRECEDING TO CURRENT ROW</c> (running total, peer-tie
/// groups share extents). Explicit <c>ROWS BETWEEN</c> / <c>RANGE BETWEEN</c>
/// frames are accepted for the value family (FIRST_VALUE / LAST_VALUE) and
/// for aggregate-OVER; ranking (ROW_NUMBER / RANK / DENSE_RANK / NTILE)
/// and offset (LAG / LEAD) functions reject explicit frames with Msg 10752.
/// <c>RANGE</c> is restricted to <c>UNBOUNDED</c> / <c>CURRENT ROW</c>
/// bounds (Msg 4194 on <c>N PRECEDING</c> / <c>N FOLLOWING</c>) — matches
/// real SQL Server.
/// </remarks>
internal sealed class WindowExpression : Expression
{
    public readonly WindowKind Kind;

    // Settable so a bare `OVER w` reference can be patched with its named-window
    // definition once the trailing WINDOW clause is parsed (see ApplyNamedWindow).
    public Expression[] PartitionBy;

    public OrderBySpec[] OrderBy;

    /// <summary>
    /// For <see cref="WindowKind.Aggregate"/>, the wrapped aggregate
    /// expression (kind, operand, etc.). Null for every other kind.
    /// The aggregate's own <see cref="AggregateExpression.BindResult"/> is
    /// never called when wrapped — only the surrounding window's
    /// <see cref="BindResult"/> is, since the aggregate's runtime path goes
    /// through the window's bound value.
    /// </summary>
    public readonly AggregateExpression? AggregateInfo;

    /// <summary>
    /// For <see cref="WindowKind.Lag"/> / <see cref="WindowKind.Lead"/> /
    /// <see cref="WindowKind.FirstValue"/>, the value expression evaluated
    /// against the referenced row (lag-offset row / lead-offset row / first
    /// row in partition). Null for ranking functions and aggregates.
    /// </summary>
    public readonly Expression? Operand;

    /// <summary>
    /// For <see cref="WindowKind.Lag"/> / <see cref="WindowKind.Lead"/>,
    /// the optional offset expression (defaults to 1). Evaluated once per
    /// query with no row context — column references trigger a runtime
    /// resolver error rather than a parser-side restriction. Null otherwise.
    /// </summary>
    public readonly Expression? OffsetArg;

    /// <summary>
    /// For <see cref="WindowKind.Lag"/> / <see cref="WindowKind.Lead"/>,
    /// the optional default expression returned when the offset crosses
    /// the partition boundary (defaults to typed NULL). Evaluated once per
    /// query in the same context as <see cref="OffsetArg"/>. Null otherwise.
    /// </summary>
    public readonly Expression? DefaultArg;

    /// <summary>
    /// For <see cref="WindowKind.NTile"/>, the bucket-count expression.
    /// Evaluated once per query — real SQL Server requires a positive
    /// bigint constant/parameter. Null for every other kind.
    /// </summary>
    public readonly Expression? BucketCount;

    /// <summary>
    /// For <see cref="WindowKind.PercentileCont"/> / <see cref="WindowKind.PercentileDisc"/>,
    /// the percentile fraction argument (a value in <c>[0, 1]</c>). Evaluated
    /// once per query — real SQL Server allows a constant, variable, or
    /// parameter; out-of-range / NULL surfaces Msg 8727 at runtime. The
    /// <c>WITHIN GROUP</c> ordering is stored in <see cref="OrderBy"/> (exactly
    /// one entry). Null for every other kind.
    /// </summary>
    public readonly Expression? PercentileArg;

    /// <summary>
    /// Explicit window frame for the value family
    /// (<see cref="WindowKind.FirstValue"/> / <see cref="WindowKind.LastValue"/>)
    /// and aggregate-OVER (<see cref="WindowKind.Aggregate"/>). Null = use the
    /// default frame: whole partition when no ORDER BY is present;
    /// <c>RANGE UNBOUNDED PRECEDING TO CURRENT ROW</c> when ORDER BY is
    /// present (running-total semantic with peer-tie grouping). Ranking
    /// functions and LAG/LEAD reject explicit frames at parse via Msg 10752,
    /// so this field stays null for those kinds and the executor short-
    /// circuits on Kind alone.
    /// </summary>
    public FrameSpec? Frame;


    private WindowExpression(
        WindowKind kind,
        Expression[] partitionBy,
        OrderBySpec[] orderBy,
        AggregateExpression? aggregateInfo,
        Expression? operand = null,
        Expression? offsetArg = null,
        Expression? defaultArg = null,
        Expression? bucketCount = null,
        Expression? percentileArg = null,
        FrameSpec? frame = null)
    {
        this.Kind = kind;
        this.PartitionBy = partitionBy;
        this.OrderBy = orderBy;
        this.AggregateInfo = aggregateInfo;
        this.Operand = operand;
        this.OffsetArg = offsetArg;
        this.DefaultArg = defaultArg;
        this.BucketCount = bucketCount;
        this.PercentileArg = percentileArg;
        this.Frame = frame;
    }

    private static WindowExpression Register(ParserContext context, WindowExpression expression)
    {
        if (!context.AllowsWindowExpressions)
            throw SimulatedSqlException.WindowedFunctionInWrongClause();
        context.WindowCollector?.Add(expression);
        return expression;
    }

    /// <summary>
    /// Binds this expression's value for the row currently being projected
    /// into <paramref name="batch"/> (not onto this instance — a plan-cached
    /// <c>Selection</c> shares its tree across concurrent commands). The
    /// Selection executor calls this once per buffered tuple, just before
    /// running the projection expressions, with either the precomputed row
    /// number (ROW_NUMBER) or the per-partition aggregate result (aggregate
    /// windows).
    /// </summary>
    internal void BindResult(BatchContext batch, SqlValue value) => batch.BindProjectionResult(this, value);

    public override SqlValue Run(RuntimeContext runtime) =>
        runtime.Batch.BoundProjectionResults is { } bound && bound.TryGetValue(this, out var result)
            ? result
            : throw new InvalidOperationException("WindowExpression.Run was called before its result was bound; this indicates the Selection executor didn't recognize it as a window function.");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Kind switch
    {
        WindowKind.RowNumber or WindowKind.Rank or WindowKind.DenseRank => SqlType.BigInt,
        WindowKind.NTile => SqlType.Int32,
        WindowKind.CumeDist or WindowKind.PercentRank or WindowKind.PercentileCont => SqlType.Float,
        WindowKind.PercentileDisc => this.OrderBy[0].Expr!.GetSqlType(batch, resolveColumnType),
        WindowKind.Aggregate => this.AggregateInfo!.GetSqlType(batch, resolveColumnType),
        WindowKind.Lag or WindowKind.Lead or WindowKind.FirstValue or WindowKind.LastValue => this.Operand!.GetSqlType(batch, resolveColumnType),
        _ => throw new InvalidOperationException($"Unknown window kind {this.Kind}."),
    };

    /// <summary>
    /// Parses <c>ROW_NUMBER() OVER(... )</c> — entered with the cursor on
    /// the <c>)</c> closing the empty argument list. Consumes the
    /// <c>OVER ( [PARTITION BY ...] ORDER BY ... )</c> clause and leaves
    /// the cursor on the OVER's closing <c>)</c>, matching the
    /// <see cref="Expression.Parse"/> dispatch's lookahead contract.
    /// </summary>
    public static WindowExpression ParseRowNumber(ParserContext context) =>
        ParseNoArgRankingFunction(context, WindowKind.RowNumber);

    /// <summary>Parses <c>RANK() OVER (... ORDER BY ...)</c>.</summary>
    public static WindowExpression ParseRank(ParserContext context) =>
        ParseNoArgRankingFunction(context, WindowKind.Rank);

    /// <summary>Parses <c>DENSE_RANK() OVER (... ORDER BY ...)</c>.</summary>
    public static WindowExpression ParseDenseRank(ParserContext context) =>
        ParseNoArgRankingFunction(context, WindowKind.DenseRank);

    /// <summary>Parses <c>CUME_DIST() OVER (... ORDER BY ...)</c>.</summary>
    public static WindowExpression ParseCumeDist(ParserContext context) =>
        ParseNoArgRankingFunction(context, WindowKind.CumeDist);

    /// <summary>Parses <c>PERCENT_RANK() OVER (... ORDER BY ...)</c>.</summary>
    public static WindowExpression ParsePercentRank(ParserContext context) =>
        ParseNoArgRankingFunction(context, WindowKind.PercentRank);

    /// <summary>
    /// Shared backbone for the no-operand ranking functions
    /// (<see cref="WindowKind.RowNumber"/> / <see cref="WindowKind.Rank"/> /
    /// <see cref="WindowKind.DenseRank"/>): the call's empty <c>()</c>, then
    /// the standard <c>OVER ( [PARTITION BY ...] ORDER BY ... )</c> tail.
    /// ORDER BY is required.
    /// </summary>
    private static WindowExpression ParseNoArgRankingFunction(ParserContext context, WindowKind kind)
    {
        var functionLowerName = LowerNameFor(kind);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Over })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (TryParseWindowReference(context, functionLowerName) is { } reference)
            return RegisterNamedWindowReference(context, new WindowExpression(kind, [], [], aggregateInfo: null), reference);

        var partitionBy = ParseOptionalPartitionBy(context);

        // Ranking functions require ORDER BY inside OVER; SQL Server raises Msg
        // 4112. The simulator surfaces a generic syntax error to keep the
        // error-factory surface lean — EF Core never emits these without ORDER BY.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Order })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var orderBy = ParseOrderByList(context);

        RejectFrameSpec(context, functionLowerName);

        return context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : Register(context, new WindowExpression(kind, partitionBy, orderBy, aggregateInfo: null));
    }

    /// <summary>
    /// Parses <c>NTILE(bucket_count) OVER (... ORDER BY ...)</c>. Cursor
    /// enters on the bucket-count argument (first token after the opening
    /// <c>(</c> the dispatcher consumed); leaves on the OVER's closing
    /// <c>)</c>. ORDER BY required.
    /// </summary>
    public static WindowExpression ParseNTile(ParserContext context)
    {
        var bucketCount = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Over })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (TryParseWindowReference(context, "ntile") is { } reference)
            return RegisterNamedWindowReference(context, new WindowExpression(WindowKind.NTile, [], [], aggregateInfo: null, bucketCount: bucketCount), reference);

        var partitionBy = ParseOptionalPartitionBy(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Order })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var orderBy = ParseOrderByList(context);

        RejectFrameSpec(context, "ntile");

        return context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : Register(context, new WindowExpression(WindowKind.NTile, partitionBy, orderBy, aggregateInfo: null, bucketCount: bucketCount));
    }

    /// <summary>Parses <c>LAG(expr [, offset [, default]]) OVER (... ORDER BY ...)</c>.</summary>
    public static WindowExpression ParseLag(ParserContext context) =>
        ParseLagLead(context, WindowKind.Lag);

    /// <summary>Parses <c>LEAD(expr [, offset [, default]]) OVER (... ORDER BY ...)</c>.</summary>
    public static WindowExpression ParseLead(ParserContext context) =>
        ParseLagLead(context, WindowKind.Lead);

    /// <summary>
    /// Shared backbone for <see cref="WindowKind.Lag"/> / <see cref="WindowKind.Lead"/>:
    /// up to three comma-separated arguments (operand, offset, default), then
    /// the OVER clause with required ORDER BY.
    /// </summary>
    private static WindowExpression ParseLagLead(ParserContext context, WindowKind kind)
    {
        var operand = Expression.Parse(context);
        Expression? offsetArg = null;
        Expression? defaultArg = null;
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            offsetArg = Expression.Parse(context);
            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                defaultArg = Expression.Parse(context);
            }
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Over })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var functionLowerName = LowerNameFor(kind);
        if (TryParseWindowReference(context, functionLowerName) is { } reference)
        {
            return RegisterNamedWindowReference(
                context,
                new WindowExpression(kind, [], [], aggregateInfo: null, operand: operand, offsetArg: offsetArg, defaultArg: defaultArg),
                reference);
        }

        var partitionBy = ParseOptionalPartitionBy(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Order })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var orderBy = ParseOrderByList(context);

        RejectFrameSpec(context, functionLowerName);

        return context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : Register(context, new WindowExpression(kind, partitionBy, orderBy, aggregateInfo: null, operand: operand, offsetArg: offsetArg, defaultArg: defaultArg));
    }

    /// <summary>
    /// Parses <c>FIRST_VALUE(expr) OVER (... ORDER BY ... [frame])</c>.
    /// Default frame (no explicit ROWS/RANGE) — paired with the required
    /// ORDER BY — is <c>RANGE UNBOUNDED PRECEDING TO CURRENT ROW</c>; under
    /// that frame FIRST_VALUE returns the operand evaluated at the
    /// partition's leading row. Explicit frames shift the "first" reference
    /// to the frame's start.
    /// </summary>
    public static WindowExpression ParseFirstValue(ParserContext context) =>
        ParseFirstOrLastValue(context, WindowKind.FirstValue);

    /// <summary>
    /// Parses <c>LAST_VALUE(expr) OVER (... ORDER BY ... [frame])</c>.
    /// Default frame is <c>RANGE UNBOUNDED PRECEDING TO CURRENT ROW</c>;
    /// under that frame LAST_VALUE returns the current row's value (or
    /// the last of the peer-tie group under RANGE), not the partition's
    /// last value — for partition-last the caller must supply
    /// <c>ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING</c>.
    /// Probe-confirmed against SQL Server 2025.
    /// </summary>
    public static WindowExpression ParseLastValue(ParserContext context) =>
        ParseFirstOrLastValue(context, WindowKind.LastValue);

    private static WindowExpression ParseFirstOrLastValue(ParserContext context, WindowKind kind)
    {
        var operand = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Over })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (TryParseWindowReference(context, frameRejectingFunction: null) is { } reference)
            return RegisterNamedWindowReference(context, new WindowExpression(kind, [], [], aggregateInfo: null, operand: operand), reference);

        var partitionBy = ParseOptionalPartitionBy(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Order })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var orderBy = ParseOrderByList(context);

        var frame = ParseOptionalFrameSpec(context, orderByPresent: true);

        return context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : Register(context, new WindowExpression(kind, partitionBy, orderBy, aggregateInfo: null, operand: operand, frame: frame));
    }

    /// <summary>
    /// Parses an ordered-set analytic function
    /// (<c>PERCENTILE_CONT(p) WITHIN GROUP (ORDER BY sort [ASC|DESC]) OVER ([PARTITION BY ...])</c>
    /// or its <c>PERCENTILE_DISC</c> sibling). Entered with the cursor on the
    /// percentile-fraction argument (the first token after the opening <c>(</c>
    /// the dispatcher consumed); leaves the cursor on the OVER's closing
    /// <c>)</c>. The <c>WITHIN GROUP</c> ordering is mandatory and supplies the
    /// single sort key; <c>OVER</c> is mandatory (Msg 10753 when absent) and
    /// may carry only <c>PARTITION BY</c> (an <c>ORDER BY</c> inside OVER is
    /// rejected with Msg 10758).
    /// </summary>
    public static WindowExpression ParsePercentile(ParserContext context, WindowKind kind)
    {
        var functionLowerName = kind == WindowKind.PercentileCont ? "percentile_cont" : "percentile_disc";

        var percentileArg = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // WITHIN GROUP ( ORDER BY <sort> [ASC|DESC] ). WITHIN is contextual.
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Within })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Group })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Order })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var sortExpr = Expression.Parse(context);
        ConstantFolding.RejectConstantWindowOrderByTerm(sortExpr, context);
        var descending = false;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Asc }:
                context.MoveNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.Desc }:
                descending = true;
                context.MoveNextRequired();
                break;
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var orderBy = new[] { OrderBySpec.FromExpression(sortExpr, descending) };

        // OVER is mandatory for the ordered-set analytic functions.
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Over })
            throw SimulatedSqlException.FunctionMustHaveOverClause(functionLowerName);
        if (TryParseWindowReference(context, functionLowerName) is { } reference)
        {
            return RegisterNamedWindowReference(
                context,
                new WindowExpression(kind, [], orderBy, aggregateInfo: null, percentileArg: percentileArg),
                reference);
        }

        var partitionBy = ParseOptionalPartitionBy(context);

        // ORDER BY inside the OVER clause is rejected — the ordering must come
        // from WITHIN GROUP. A frame would also be invalid, but ORDER BY is the
        // only thing that can legally precede the closing ) at this point, so
        // any non-) token after PARTITION BY falls through to a syntax error.
        return context.Token is ReservedKeyword { Keyword: Keyword.Order }
            ? throw SimulatedSqlException.FunctionMayNotHaveOrderByInOver(functionLowerName)
            : context.Token is not Operator { Character: ')' }
                ? throw SimulatedSqlException.SyntaxErrorNear(context)
                : Register(context, new WindowExpression(kind, partitionBy, orderBy, aggregateInfo: null, percentileArg: percentileArg));
    }

    /// <summary>
    /// Wraps an aggregate function that's followed by <c>OVER (...)</c>.
    /// Entered with cursor on the <c>OVER</c> keyword. Pops the just-parsed
    /// aggregate from the surrounding <see cref="ParserContext.AggregateCollector"/>
    /// (since it's evaluated through the window infrastructure rather than
    /// the GROUP BY pass), parses the OVER clause, and registers the
    /// resulting <see cref="WindowExpression"/> with
    /// <see cref="ParserContext.WindowCollector"/>. Leaves the cursor on
    /// the OVER's closing <c>)</c>.
    /// </summary>
    public static WindowExpression WrapAggregate(AggregateExpression aggregate, ParserContext context)
    {
        if (aggregate.Distinct)
            throw SimulatedSqlException.DistinctNotAllowedInOver();
        if (aggregate.Kind == AggregateKind.StringAgg)
            throw SimulatedSqlException.FunctionNotValidForOver("string_agg");
        // JSON_ARRAYAGG's in-parens ORDER BY is mutually exclusive with OVER —
        // real SQL Server raises Msg 156 near the OVER keyword (the cursor is
        // on it here). JSON_OBJECTAGG can't carry an in-parens ORDER BY at all.
        if (aggregate.Kind == AggregateKind.JsonArrayAgg && aggregate.OrderBy is not null)
            throw context.Token is ReservedKeyword rk ? SimulatedSqlException.SyntaxErrorNearKeyword(rk) : SimulatedSqlException.SyntaxErrorNear(context);

        // The aggregate auto-registered with AggregateCollector during its
        // own Parse; remove it now that the window wrapper takes over the
        // evaluation. Aggregates inside windows must NOT also count in the
        // outer aggregate set, otherwise the executor's window+aggregate
        // mutual-exclusion check would fire on every aggregate window.
        var aggCollector = context.AggregateCollector;
        if (aggCollector is not null && aggCollector.Count > 0 && ReferenceEquals(aggCollector[^1], aggregate))
            aggCollector.RemoveAt(aggCollector.Count - 1);

        if (TryParseWindowReference(context, frameRejectingFunction: null) is { } reference)
            return RegisterNamedWindowReference(context, new WindowExpression(WindowKind.Aggregate, [], [], aggregate), reference);

        // COUNT(*) / COUNT_BIG(*) — the two aggregates that carry no operand —
        // may frame an unordered partition; every other aggregate takes
        // Msg 10756. Probe-confirmed against SQL Server 2025:
        // `COUNT(*) OVER (PARTITION BY g ROWS BETWEEN UNBOUNDED PRECEDING AND
        // CURRENT ROW)` runs and the frame applies, while COUNT(v) / SUM(v) /
        // MIN(v) in that shape raise. COUNT(1) is not the star form and raises
        // with the rest.
        var body = ParseWindowBody(context, frameAllowedWithoutOrderBy: aggregate.Operand is null);

        return context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : Register(context, new WindowExpression(WindowKind.Aggregate, body.PartitionBy, body.OrderBy, aggregate, frame: body.Frame));
    }

    /// <summary>
    /// A parsed window body — the <c>PARTITION BY</c> / <c>ORDER BY</c> / frame
    /// triple shared by an inline <c>OVER (…)</c> and a named
    /// <c>WINDOW w AS (…)</c> definition, plus the optional name of a window
    /// the body refines.
    /// </summary>
    internal readonly struct WindowBody(Expression[] partitionBy, OrderBySpec[] orderBy, FrameSpec? frame, string? baseWindowName = null)
    {
        public readonly Expression[] PartitionBy = partitionBy;
        public readonly OrderBySpec[] OrderBy = orderBy;
        public readonly FrameSpec? Frame = frame;

        /// <summary>
        /// The named window this body refines — the <c>w</c> of <c>OVER w</c>,
        /// <c>OVER (w ORDER BY …)</c> or <c>WINDOW w2 AS (w …)</c>. Null when
        /// the body stands alone. The remaining fields hold only the elements
        /// written alongside the reference; the referenced window supplies the
        /// rest at resolution.
        /// </summary>
        public readonly string? BaseWindowName = baseWindowName;

        /// <summary>True when no element was written alongside a reference.</summary>
        public bool IsEmpty => this.PartitionBy.Length == 0 && this.OrderBy.Length == 0 && this.Frame is null;
    }

    /// <summary>
    /// Parses the interior of an <c>OVER ( … )</c> / <c>WINDOW w AS ( … )</c>
    /// clause — <c>[&lt;window-name&gt;] [PARTITION BY …] [ORDER BY …] [frame]</c>.
    /// Entered with the cursor on the first body token; leaves it on the
    /// closing <c>)</c>. <paramref name="frameRejectingFunction"/> names the
    /// function when a frame written here is invalid for it (Msg 10752);
    /// <paramref name="deferFrameOrderByCheck"/> suppresses the Msg 10756
    /// frame-needs-ORDER-BY gate because a referenced window may supply the
    /// ordering; <paramref name="allowWindowReference"/> admits the leading
    /// window name a <c>WINDOW</c>-clause definition may refine;
    /// <paramref name="frameAllowedWithoutOrderBy"/> exempts the calling
    /// function from that gate entirely (<c>COUNT(*)</c> / <c>COUNT_BIG(*)</c>).
    /// </summary>
    internal static WindowBody ParseWindowBody(
        ParserContext context,
        string? frameRejectingFunction = null,
        bool deferFrameOrderByCheck = false,
        bool allowWindowReference = false,
        bool frameAllowedWithoutOrderBy = false)
    {
        string? baseWindowName = null;
        if (allowWindowReference && context.Token is Name nameToken && !IsWindowBodyKeyword(nameToken))
        {
            baseWindowName = nameToken.Value;
            context.MoveNextRequired();
        }
        var partitionBy = ParseOptionalPartitionBy(context);
        var orderBy = Array.Empty<OrderBySpec>();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            orderBy = ParseOrderByList(context);
        }
        if (frameRejectingFunction is not null)
            RejectFrameSpec(context, frameRejectingFunction);
        var frame = ParseOptionalFrameSpec(
            context,
            orderByPresent: orderBy.Length > 0 || deferFrameOrderByCheck,
            partitionByPresent: partitionBy.Length > 0,
            frameAllowedWithoutOrderBy: frameAllowedWithoutOrderBy);
        return new WindowBody(partitionBy, orderBy, frame, baseWindowName);
    }

    /// <summary>
    /// Returns true when an identifier-shaped token opens a window-body element
    /// rather than naming a window — <c>PARTITION</c>, <c>ROWS</c> and
    /// <c>RANGE</c> are contextual keywords, so they tokenize as names.
    /// <c>ORDER</c> is reserved and never reaches here.
    /// </summary>
    private static bool IsWindowBodyKeyword(Name token) =>
        token is UnquotedString { ContextualKeyword: ContextualKeyword.Partition or ContextualKeyword.Rows or ContextualKeyword.Range };

    /// <summary>
    /// Recognizes a named-window reference immediately after the <c>OVER</c>
    /// keyword — the bare <c>OVER w</c> form or the refining
    /// <c>OVER (w &lt;element&gt; …)</c> form (SQL Server 2022+). Entered with
    /// the cursor on <c>OVER</c>. Returns the reference and leaves the cursor
    /// on its last token (the name for the bare form, the closing <c>)</c> for
    /// the refining one); returns null for a self-contained <c>OVER (…)</c>,
    /// leaving the cursor on the first body token for the caller to parse.
    /// <paramref name="frameRejectingFunction"/> names the calling function
    /// when a frame written in the refinement is invalid for it (Msg 10752),
    /// and is null for the kinds that accept one.
    /// </summary>
    private static WindowBody? TryParseWindowReference(ParserContext context, string? frameRejectingFunction)
    {
        var afterOver = context.GetNextRequired();
        if (afterOver is Name bareName && !IsWindowBodyKeyword(bareName))
            return new WindowBody([], [], null, bareName.Value);
        if (afterOver is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name referenceName || IsWindowBodyKeyword(referenceName))
            return null;
        context.MoveNextRequired();

        var body = ParseWindowBody(context, frameRejectingFunction, deferFrameOrderByCheck: true);
        // Real requires at least one refining element after the reference:
        // `OVER (w)` is Msg 102 even though `WINDOW w2 AS (w)` is legal.
        return body.IsEmpty || context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : new WindowBody(body.PartitionBy, body.OrderBy, body.Frame, referenceName.Value);
    }

    /// <summary>
    /// Registers a window function whose <c>OVER</c> clause referenced a named
    /// window. The definition parses later (the <c>WINDOW</c> clause sits after
    /// <c>HAVING</c>), so the expression is registered carrying only whatever
    /// the reference wrote inline and is patched by
    /// <c>Selection.ResolvePendingNamedWindows</c>.
    /// </summary>
    private static WindowExpression RegisterNamedWindowReference(ParserContext context, WindowExpression expression, WindowBody reference)
    {
        context.PendingNamedWindows.Add((expression, reference));
        return Register(context, expression);
    }

    /// <summary>
    /// The lowercase name real SQL Server uses for a kind in its window
    /// diagnostics. <see cref="WindowKind.Aggregate"/> has none: an aggregate
    /// window carries no per-kind restriction, so no diagnostic that names the
    /// function can reach it.
    /// </summary>
    private static string LowerNameFor(WindowKind kind) => kind switch
    {
        WindowKind.RowNumber => "row_number",
        WindowKind.Rank => "rank",
        WindowKind.DenseRank => "dense_rank",
        WindowKind.NTile => "ntile",
        WindowKind.Lag => "lag",
        WindowKind.Lead => "lead",
        WindowKind.FirstValue => "first_value",
        WindowKind.LastValue => "last_value",
        WindowKind.CumeDist => "cume_dist",
        WindowKind.PercentRank => "percent_rank",
        WindowKind.PercentileCont => "percentile_cont",
        WindowKind.PercentileDisc => "percentile_disc",
        _ => throw new InvalidOperationException($"Window kind {kind} has no diagnostic name."),
    };

    private string FunctionLowerName => LowerNameFor(this.Kind);

    /// <summary>
    /// True for the ranking and distribution kinds, which real states
    /// differently from the offset / value / ordered-set kinds in the
    /// named-window diagnostics (Msg 4106 and Msg 5366).
    /// </summary>
    private bool IsRankingFamily => this.Kind
        is WindowKind.RowNumber or WindowKind.Rank or WindowKind.DenseRank
        or WindowKind.NTile or WindowKind.CumeDist or WindowKind.PercentRank;

    /// <summary>
    /// Patches an <c>OVER w</c> / <c>OVER (w …)</c> window with the definition
    /// its reference resolved to, then applies the per-kind rules the inline
    /// <c>OVER (…)</c> parse applies at parse time. Real answers this position
    /// with its own error numbers rather than the inline ones — Msg 4106 for a
    /// frame a ranking / offset / percentile function may not carry, Msg 5366
    /// for a missing ORDER BY, Msg 5363 for an ORDER BY the percentile pair may
    /// not carry, and Msg 5364 for a frame with nothing to order against.
    /// </summary>
    public void ApplyNamedWindow(WindowBody body)
    {
        if (this.Kind is WindowKind.PercentileCont or WindowKind.PercentileDisc)
        {
            // OrderBy already holds the mandatory WITHIN GROUP ordering, so the
            // definition contributes PARTITION BY and nothing else.
            if (body.OrderBy.Length > 0)
                throw SimulatedSqlException.FunctionMayNotHaveOrderByInNamedWindow(this.FunctionLowerName);
            if (body.Frame is not null)
                throw SimulatedSqlException.NamedWindowMayNotHaveWindowFrame(this.FunctionLowerName, this.IsRankingFamily);
            this.PartitionBy = body.PartitionBy;
            return;
        }
        if (this.Kind != WindowKind.Aggregate)
        {
            if (body.Frame is not null && !AllowsFrame(this.Kind))
                throw SimulatedSqlException.NamedWindowMayNotHaveWindowFrame(this.FunctionLowerName, this.IsRankingFamily);
            if (body.OrderBy.Length == 0)
                throw SimulatedSqlException.FunctionMustHaveWindowWithOrderBy(this.FunctionLowerName, this.IsRankingFamily);
        }
        if (body.Frame is not null && body.OrderBy.Length == 0)
            throw SimulatedSqlException.NamedWindowFrameRequiresOrderBy();
        this.PartitionBy = body.PartitionBy;
        this.OrderBy = body.OrderBy;
        this.Frame = body.Frame;
    }

    /// <summary>
    /// True for the kinds that may carry an explicit frame — aggregate windows
    /// and the value pair. Every other kind rejects one.
    /// </summary>
    private static bool AllowsFrame(WindowKind kind) =>
        kind is WindowKind.Aggregate or WindowKind.FirstValue or WindowKind.LastValue;

    /// <summary>
    /// Parses an optional <c>PARTITION BY expr [, expr]*</c> clause inside
    /// an OVER. Entered with cursor on the first token of the OVER body; on
    /// return, cursor is on the next OVER-body lookahead token (typically
    /// <c>ORDER</c>, <c>ROWS</c>, <c>RANGE</c>, or the closing <c>)</c>).
    /// </summary>
    private static Expression[] ParseOptionalPartitionBy(ParserContext context)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
            return [];
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // An OVER body rejects NEXT VALUE FOR (Msg 11720) — real names OVER
        // alongside WHERE / ORDER BY / the rest in that message.
        var saved = context.EnterNextValueForScope(NextValueForScope.Clause);
        try
        {
            return ParseExpressionList(context);
        }
        finally
        {
            context.NextValueForRejection = saved;
        }
    }

    /// <summary>
    /// Rejects an explicit frame specification for kinds that aren't allowed
    /// to carry one (ranking + offset functions). Real SQL Server raises
    /// Msg 10752 ("The function 'X' may not have a window frame.").
    /// </summary>
    private static void RejectFrameSpec(ParserContext context, string functionLowerName)
    {
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Rows or ContextualKeyword.Range })
            throw SimulatedSqlException.FunctionMayNotHaveWindowFrame(functionLowerName);
    }

    /// <summary>
    /// Parses an optional <c>ROWS BETWEEN x AND y</c> / <c>RANGE BETWEEN x AND y</c>
    /// clause, or its single-bound shorthand <c>ROWS x</c> / <c>RANGE x</c>
    /// (equivalent to <c>BETWEEN x AND CURRENT ROW</c>). Entered with cursor
    /// at the lookahead-after-ORDER-BY position; returns null and doesn't
    /// advance if no frame keyword is present.
    /// <para>A frame written as the inline body's only element — no
    /// <c>PARTITION BY</c> and no <c>ORDER BY</c> — is Msg 102 near the frame
    /// keyword, whatever the function (probe-confirmed against SQL Server
    /// 2025; real's grammar refuses the shape before the ordering rule gets a
    /// say). With a partition but no ordering the frame takes Msg 10756,
    /// unless <paramref name="frameAllowedWithoutOrderBy"/> exempts the
    /// function.</para>
    /// </summary>
    private static FrameSpec? ParseOptionalFrameSpec(
        ParserContext context,
        bool orderByPresent,
        bool partitionByPresent = true,
        bool frameAllowedWithoutOrderBy = false)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Rows or ContextualKeyword.Range } frameKw)
            return null;

        if (!orderByPresent)
        {
            if (!partitionByPresent)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (!frameAllowedWithoutOrderBy)
                throw SimulatedSqlException.WindowFrameRequiresOrderBy();
        }

        var isRange = frameKw.ContextualKeyword == ContextualKeyword.Range;
        context.MoveNextRequired();

        FrameBound start;
        FrameBound end;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Between })
        {
            context.MoveNextRequired();
            start = ParseFrameBound(context);
            // Start side can't be UNBOUNDED FOLLOWING — real SQL Server
            // rejects at parse with Msg 102 ("near 'following'"); the
            // simulator surfaces the same Msg 102 at the post-bound cursor
            // position.
            if (start.Kind == FrameBoundKind.UnboundedFollowing)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is not ReservedKeyword { Keyword: Keyword.And })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            end = ParseFrameBound(context);
            // End side can't be UNBOUNDED PRECEDING — probe-confirmed Msg 102.
            if (end.Kind == FrameBoundKind.UnboundedPreceding)
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        else
        {
            // Single-bound shorthand: ROWS x  ≡  ROWS BETWEEN x AND CURRENT ROW.
            start = ParseFrameBound(context);
            if (start.Kind == FrameBoundKind.UnboundedFollowing)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            end = FrameBound.CurrentRow;
        }

        ValidateFrameBounds(isRange, start, end);
        return new FrameSpec(isRange, start, end);
    }

    /// <summary>
    /// Parses a single frame bound. Recognized shapes:
    /// <c>UNBOUNDED PRECEDING</c>, <c>UNBOUNDED FOLLOWING</c>, <c>CURRENT ROW</c>,
    /// <c>N PRECEDING</c>, <c>N FOLLOWING</c>. Bound-pair validation
    /// (start can't be <c>UNBOUNDED FOLLOWING</c>; end can't be
    /// <c>UNBOUNDED PRECEDING</c>) happens later in
    /// <see cref="ValidateFrameBounds"/>. Cursor advances past the bound.
    /// </summary>
    private static FrameBound ParseFrameBound(ParserContext context)
    {
        switch (context.Token)
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Unbounded }:
                {
                    context.MoveNextRequired();
                    switch (context.Token)
                    {
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Preceding }:
                            context.MoveNextOptional();
                            return FrameBound.UnboundedPreceding;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Following }:
                            context.MoveNextOptional();
                            return FrameBound.UnboundedFollowing;
                        default:
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                }
            case ReservedKeyword { Keyword: Keyword.Current }:
                {
                    // CURRENT ROW — ROW here is a contextual keyword (the
                    // identifier "row"). Real SQL Server also accepts the
                    // form via the contextual classifier.
                    if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Row })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                    return FrameBound.CurrentRow;
                }
            case Numeric { Value: { IsNull: false } numericValue }:
                {
                    // Frame offset literals are integers per probe (SQL Server
                    // rejects non-integer offsets at parse). CoerceTo handles
                    // the Int32 → BigInt widening for everyday small literals
                    // and surfaces Msg-equivalent overflow on huge values.
                    var offset = numericValue.CoerceTo(SqlType.BigInt).AsInt64;
                    if (offset < 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    return context.Token switch
                    {
                        UnquotedString { ContextualKeyword: ContextualKeyword.Preceding } => Advance(context, FrameBound.NPreceding(offset)),
                        UnquotedString { ContextualKeyword: ContextualKeyword.Following } => Advance(context, FrameBound.NFollowing(offset)),
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                }
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        static FrameBound Advance(ParserContext context, FrameBound bound)
        {
            context.MoveNextOptional();
            return bound;
        }
    }

    /// <summary>
    /// Cross-validates start + end bounds after both are parsed:
    /// <c>RANGE</c> rejects <see cref="FrameBoundKind.NPreceding"/> /
    /// <see cref="FrameBoundKind.NFollowing"/> on either side (Msg 4194);
    /// <c>BETWEEN N FOLLOWING AND N PRECEDING | CURRENT ROW</c> is
    /// semantically invalid (Msg 4193); single-side mistakes
    /// (<c>BETWEEN UNBOUNDED FOLLOWING AND ...</c>,
    /// <c>BETWEEN ... AND UNBOUNDED PRECEDING</c>) fall through to a generic
    /// syntax error — real SQL Server's parser rejects those at the
    /// tokenization level (Msg 102), so they shouldn't reach this point
    /// with a successful <see cref="ParseFrameBound"/> result, but the
    /// explicit check guards against asymmetry between the two parsers.
    /// </summary>
    private static void ValidateFrameBounds(bool isRange, FrameBound start, FrameBound end)
    {
        if (isRange && (start.Kind is FrameBoundKind.NPreceding or FrameBoundKind.NFollowing
                     || end.Kind is FrameBoundKind.NPreceding or FrameBoundKind.NFollowing))
        {
            throw SimulatedSqlException.RangeFrameOnlySupportsUnboundedAndCurrentRow();
        }

        if (start.Kind == FrameBoundKind.NFollowing
            && end.Kind is FrameBoundKind.NPreceding or FrameBoundKind.CurrentRow)
        {
            throw SimulatedSqlException.FrameBetweenFollowingAndPreceding();
        }
    }

    /// <summary>
    /// Parses a comma-separated list of expressions, used by PARTITION BY.
    /// Cursor is on the first token of the first expression on entry; on
    /// return the cursor is at the lookahead-after-list token (typically
    /// <c>ORDER</c> for the OVER's second clause).
    /// </summary>
    private static Expression[] ParseExpressionList(ParserContext context)
    {
        var items = new List<Expression>();
        while (true)
        {
            context.MoveNextRequired();
            items.Add(Expression.Parse(context));
            if (context.Token is not Operator { Character: ',' })
                break;
        }
        return [.. items];
    }

    /// <summary>
    /// Parses a comma-separated list of <c>expr [ASC|DESC]</c> for the
    /// OVER's ORDER BY. Cursor is on the first token of the first
    /// expression on entry; on return the cursor is at the lookahead-
    /// after-list token (typically the OVER's closing <c>)</c>).
    /// </summary>
    private static OrderBySpec[] ParseOrderByList(ParserContext context)
    {
        // Same Msg 11720 rejection as PARTITION BY — this list is only ever an
        // OVER body's ORDER BY, and real names OVER in that message.
        var saved = context.EnterNextValueForScope(NextValueForScope.Clause);
        try
        {
            return ParseOrderByListCore(context);
        }
        finally
        {
            context.NextValueForRejection = saved;
        }
    }

    /// <summary>Body of <see cref="ParseOrderByList"/>.</summary>
    private static OrderBySpec[] ParseOrderByListCore(ParserContext context)
    {
        var items = new List<OrderBySpec>();
        while (true)
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);
            ConstantFolding.RejectConstantWindowOrderByTerm(expr, context);
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
            items.Add(OrderBySpec.FromExpression(expr, descending));
            if (context.Token is not Operator { Character: ',' })
                break;
        }
        return [.. items];
    }

    internal override string DebugDisplay()
    {
        var name = this.Kind switch
        {
            WindowKind.RowNumber => "ROW_NUMBER()",
            WindowKind.Aggregate => this.AggregateInfo!.DebugDisplay(),
            _ => this.Kind.ToString(),
        };
        var partitionPart = this.PartitionBy.Length == 0
            ? ""
            : "PARTITION BY " + string.Join(", ", this.PartitionBy.Select(p => p.DebugDisplay()));
        // OVER's ORDER BY is always expression-based — ordinals only apply
        // to a SELECT projection's ORDER BY, which is parsed elsewhere.
        var orderPart = this.OrderBy.Length == 0
            ? ""
            : "ORDER BY " + string.Join(", ", this.OrderBy.Select(o => o.Expr!.DebugDisplay() + (o.Descending ? " DESC" : "")));
        var separator = partitionPart.Length > 0 && orderPart.Length > 0 ? " " : "";
        return $"{name} OVER({partitionPart}{separator}{orderPart})";
    }
}
