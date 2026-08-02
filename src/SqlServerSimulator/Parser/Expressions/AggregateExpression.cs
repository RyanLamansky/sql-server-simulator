using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator for the aggregate functions the simulator models.
/// </summary>
internal enum AggregateKind
{
    Count,
    CountBig,
    Sum,
    Avg,
    Max,
    Min,
    Stdev,
    StdevP,
    Var,
    VarP,
    StringAgg,
    ChecksumAgg,
    ApproxCountDistinct,
    JsonArrayAgg,
    JsonObjectAgg,
}

/// <summary>
/// SQL aggregate function call (<c>COUNT</c>, <c>SUM</c>, <c>AVG</c>,
/// <c>MAX</c>, <c>MIN</c>, the statistical family, <c>STRING_AGG</c>,
/// <c>CHECKSUM_AGG</c>, <c>APPROX_COUNT_DISTINCT</c>). Aggregates can't be
/// evaluated row-by-row; the Selection executor detects them in the
/// projection list, creates per-group <see cref="Aggregator"/>
/// state, streams input rows through it, and binds the materialized
/// <see cref="SqlValue"/> back here via <see cref="BindResult"/> before
/// projecting the output row. <see cref="Run"/> just returns the bound
/// value; calling it before binding is a usage error.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Operand"/> is null only for <c>COUNT(*)</c> and
/// <c>COUNT_BIG(*)</c>. <see cref="Separator"/> is non-null only for
/// <c>STRING_AGG</c>. The bound result lives in
/// <c>BatchContext.BoundProjectionResults</c>, never on this instance — a
/// plan-cached <c>Selection</c> shares its expression tree across
/// concurrently-executing commands, so instance-held results would
/// cross-contaminate them.
/// </para>
/// </remarks>
internal sealed class AggregateExpression : Expression
{
    public readonly AggregateKind Kind;

    public readonly Expression? Operand;

    public readonly Expression? Separator;

    public readonly bool Distinct;

    /// <summary>
    /// Items from a postfix <c>WITHIN GROUP (ORDER BY ...)</c> clause; null
    /// when no ORDER BY was supplied. Set exactly once during parse, after the
    /// aggregate is constructed and registered (the postfix follows the
    /// closing <c>)</c> of the function call). Only <c>STRING_AGG</c> accepts
    /// this clause; <see cref="Expression.Parse"/>'s outer loop raises
    /// Msg 10757 for any other aggregate kind before reaching the setter.
    /// </summary>
    public IReadOnlyList<OrderBySpec>? OrderBy;

    /// <summary>
    /// For <see cref="AggregateKind.JsonObjectAgg"/>, the property-name
    /// expression (the left side of <c>key : value</c>); the value side is
    /// carried in <see cref="Operand"/>. Null for every other kind. Set once
    /// during parse, alongside <see cref="JsonNulls"/>.
    /// </summary>
    public Expression? KeyExpression;

    /// <summary>
    /// For <see cref="AggregateKind.JsonArrayAgg"/> /
    /// <see cref="AggregateKind.JsonObjectAgg"/>, whether SQL NULL value
    /// expressions appear as JSON <c>null</c> or are omitted. Defaults match
    /// the corresponding scalar builders (probe-confirmed against SQL Server
    /// 2025): <c>JSON_ARRAYAGG</c> → <see cref="JsonNullClause.AbsentOnNull"/>
    /// (like <c>JSON_ARRAY</c>), <c>JSON_OBJECTAGG</c> →
    /// <see cref="JsonNullClause.NullOnNull"/> (like <c>JSON_OBJECT</c>). Set
    /// once during parse; ignored by every non-JSON aggregate kind.
    /// </summary>
    public JsonNullClause JsonNulls;

    private AggregateExpression(AggregateKind kind, Expression? operand, bool distinct, Expression? separator)
    {
        this.Kind = kind;
        this.Operand = operand;
        this.Distinct = distinct;
        this.Separator = separator;
    }

    /// <summary>
    /// SQL Server's lowercase function name for this aggregate kind, used in
    /// error messages that quote the offending function (Msg 10757, etc.).
    /// </summary>
    internal string LowerName => LowerNameOf(this.Kind);

    /// <inheritdoc cref="LowerName"/>
    internal static string LowerNameOf(AggregateKind kind) => kind switch
    {
        AggregateKind.Count => "count",
        AggregateKind.CountBig => "count_big",
        AggregateKind.Sum => "sum",
        AggregateKind.Avg => "avg",
        AggregateKind.Max => "max",
        AggregateKind.Min => "min",
        AggregateKind.Stdev => "stdev",
        AggregateKind.StdevP => "stdevp",
        AggregateKind.Var => "var",
        AggregateKind.VarP => "varp",
        AggregateKind.StringAgg => "string_agg",
        AggregateKind.ChecksumAgg => "checksum_agg",
        AggregateKind.ApproxCountDistinct => "approx_count_distinct",
        AggregateKind.JsonArrayAgg => "json_arrayagg",
        AggregateKind.JsonObjectAgg => "json_objectagg",
        _ => throw new InvalidOperationException($"Unknown aggregate kind {kind}."),
    };

    /// <summary>
    /// The <c>nvarchar(max)</c> store type both JSON aggregates project (the
    /// scalar <c>JSON_OBJECT</c> / <c>JSON_ARRAY</c> builders return plain
    /// <c>nvarchar</c>, but the aggregate forms widen to MAX — probe-confirmed
    /// against SQL Server 2025).
    /// </summary>
    internal static readonly NVarcharSqlType NVarcharMax =
        NVarcharSqlType.Get(SqlType.MaxLengthSentinel, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <summary>
    /// Builds a single-operand aggregate programmatically (used by PIVOT
    /// desugaring, where each pivot column becomes
    /// <c>&lt;kind&gt;(CASE forCol WHEN value THEN argCol END)</c>). Bypasses
    /// the token parser and the <c>AggregateCollector</c> registration — the
    /// PIVOT planner hands the built list straight to
    /// <c>Selection.BuildSqlProjection</c>.
    /// </summary>
    internal static AggregateExpression CreatePivotAggregate(AggregateKind kind, Expression operand) =>
        new(kind, operand, distinct: false, separator: null);

    /// <summary>
    /// Convenience overload that auto-registers the new instance with the
    /// parser context's aggregate collector (when one is in scope, e.g.
    /// during a Selection projection / HAVING parse). Lets the executor
    /// learn what aggregates appear without re-walking the expression
    /// trees.
    /// </summary>
    private static AggregateExpression Register(ParserContext context, AggregateExpression expression)
    {
        context.RecursiveBranchConstructs.GroupingOrAggregate = true;
        context.AggregatesParsed++;
        context.AggregateCollector?.Add(expression);
        return expression;
    }

    /// <summary>
    /// Enforces the two operand rules real SQL Server binds at parse time,
    /// given the counter snapshot taken immediately before the operand parse:
    /// <list type="bullet">
    /// <item><b>Msg 130</b> when the operand contained an aggregate or a
    /// subquery at any depth. Detected from the parse-time counters rather than
    /// a tree walk — see <see cref="ParserContext.AggregatesParsed"/>.</item>
    /// <item><b>Msg 8117</b> when the operand is the bare untyped <c>NULL</c>
    /// keyword (<c>COUNT_BIG(NULL)</c>, which is what mssql-django's empty
    /// <c>filter=</c> aggregate degrades to). A <em>typed</em> NULL is fine:
    /// <c>COUNT_BIG(CAST(NULL AS int))</c> returns 0 on real.</item>
    /// </list>
    /// Probe-confirmed 2026-07-24. STRING_AGG's untyped-NULL rejection is a
    /// different message (Msg 8116, the argument form) and isn't covered here.
    /// <para>The Msg 130 rule has one carve-out: an aggregate-bearing operand
    /// is legal when <em>this</em> aggregate carries an <c>OVER</c> clause, so
    /// <c>SUM(SUM(b)) OVER ()</c> binds where the bare <c>SUM(SUM(b))</c>
    /// doesn't (probe-confirmed — it returns the grand total repeated per
    /// group). The OVER keyword sits two tokens ahead at this point (the
    /// aggregate's closing paren is still unconsumed), so the carve-out is a
    /// bounded lookahead rather than deferred state. It also reproduces real's
    /// depth rule for free: in <c>SUM(SUM(SUM(x))) OVER ()</c> the middle
    /// aggregate is followed by a paren rather than OVER, so it still raises
    /// Msg 130.</para>
    /// </summary>
    private static void ValidateOperand(ParserContext context, AggregateKind kind, Expression operand, int aggregatesBefore, int subqueriesBefore)
    {
        if ((context.AggregatesParsed > aggregatesBefore || context.SubqueriesParsed > subqueriesBefore)
            && !OverFollowsCall(context))
        {
            throw SimulatedSqlException.AggregateOnAggregateOrSubquery();
        }
        if (kind != AggregateKind.StringAgg && IsUntypedNullLiteral(operand))
            throw SimulatedSqlException.OperandDataTypeNullInvalid(LowerNameOf(kind));
    }

    /// <summary>
    /// Non-consuming lookahead for the <c>) OVER</c> pair that turns this
    /// aggregate into a window function. Called with the cursor parked on the
    /// aggregate's closing paren; restores that position either way, so the
    /// caller's normal paren + OVER consumption is unaffected.
    /// </summary>
    private static bool OverFollowsCall(ParserContext context)
    {
        if (context.Token is not Operator { Character: ')' })
            return false;

        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next is ReservedKeyword { Keyword: Keyword.Over };
    }

    /// <summary>
    /// Binds the aggregator's final result into <paramref name="batch"/> so
    /// that the next <see cref="Run"/> call under that batch returns it. The
    /// Selection executor calls this once per group after streaming all input
    /// rows through the matching <see cref="Aggregator"/>.
    /// </summary>
    internal void BindResult(BatchContext batch, SqlValue value) => batch.BindProjectionResult(this, value);

    public override SqlValue Run(RuntimeContext runtime) =>
        runtime.Batch.BoundProjectionResults is { } bound && bound.TryGetValue(this, out var result)
            ? result
            : throw new InvalidOperationException("AggregateExpression.Run was called before its result was bound; this indicates the Selection executor didn't recognize it as an aggregate.");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // An aggregate's own DISTINCT dedups its operand, which needs a
        // definite collation — real reports that as the same Msg 446 State 11
        // the projection-level DISTINCT takes, naming the producing operator
        // and DISTINCT together.
        return this.Distinct && this.Operand is { } distinctOperand
            && UnresolvedCollation.On(distinctOperand.GetSqlType(batch, resolveColumnType)) is { } conflict
            ? throw SimulatedSqlException.UnresolvedCollationInOperation(
                conflict.RightName, conflict.LeftName, conflict.OperatorName, "DISTINCT", 11)
            : this.ResultType(batch, resolveColumnType);
    }

    private SqlType ResultType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Kind switch
    {
        AggregateKind.Count => SqlType.Int32,
        AggregateKind.CountBig or AggregateKind.ApproxCountDistinct => SqlType.BigInt,
        AggregateKind.ChecksumAgg => SqlType.Int32,
        AggregateKind.Stdev or AggregateKind.StdevP or AggregateKind.Var or AggregateKind.VarP => SqlType.Float,
        // MAX / MIN order their input, so an unresolved collation reports here
        // (Msg 4191 naming `max` / `min`) rather than travelling on.
        AggregateKind.Max or AggregateKind.Min => BindOrderedOperand(batch, resolveColumnType),
        // STRING_AGG refuses a legacy LOB in either slot, and real binds that
        // while compiling — so the gate runs here as well as per value.
        AggregateKind.StringAgg => BindStringAggArguments(batch, resolveColumnType),
        AggregateKind.JsonArrayAgg or AggregateKind.JsonObjectAgg => NVarcharMax,
        AggregateKind.Sum => DeriveSumResultType(this.Operand!.GetSqlType(batch, resolveColumnType)),
        AggregateKind.Avg => DeriveAvgResultType(this.Operand!.GetSqlType(batch, resolveColumnType)),
        _ => throw new InvalidOperationException($"Unknown aggregate kind {this.Kind}."),
    };

    /// <summary>
    /// Compile-time mirror of the two <c>RejectLegacyLob</c> calls STRING_AGG's
    /// execution makes — the value slot in <c>StringAggAggregator</c> and the
    /// separator in the aggregate executor. Returns the operand's type, which
    /// is the aggregate's result type.
    /// </summary>
    private SqlType BindOrderedOperand(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.Operand!.GetSqlType(batch, resolveColumnType);
        StringScalars.RequireSettledCollation(operandType, this.Kind == AggregateKind.Max ? "max" : "min");
        return operandType;
    }

    private SqlType BindStringAggArguments(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = StringScalars.BindArgument(this.Operand!, batch, resolveColumnType, "string_agg");
        _ = StringScalars.BindArgument(this.Separator!, batch, resolveColumnType, "string_agg", argumentIndex: 2);
        return operandType;
    }

    // SUM / AVG / MIN / MAX preserve the operand's decimal-vs-numeric name; the other
    // kinds have non-decimal results the projection-time gate filters out.
    internal override bool ResultReportsNumeric => this.Operand?.ResultReportsNumeric ?? false;

    /// <summary>
    /// Maps <c>SUM</c>'s operand type to its result type per SQL Server's
    /// rules: integer family widens to <see cref="SqlType.Int32"/> for
    /// tinyint/smallint and stays at the operand type for int/bigint;
    /// decimal becomes <c>decimal(38, s)</c> preserving scale; float and
    /// money pass through. Probed against SQL Server 2025 — int does NOT
    /// auto-widen to bigint, so an overflowing sum raises Msg 8115.
    /// </summary>
    private static SqlType DeriveSumResultType(SqlType operandType) => operandType switch
    {
        var t when t == SqlType.TinyInt || t == SqlType.SmallInt => SqlType.Int32,
        var t when t == SqlType.Int32 => SqlType.Int32,
        var t when t == SqlType.BigInt => SqlType.BigInt,
        var t when t == SqlType.Float => SqlType.Float,
        var t when t == SqlType.Real => SqlType.Real,
        var t when t == SqlType.Money => SqlType.Money,
        var t when t == SqlType.SmallMoney => SqlType.Money,
        DecimalSqlType d => SqlType.GetDecimal(38, d.scale),
        _ => throw SimulatedSqlException.OperandDataTypeInvalid(operandType, "sum"),
    };

    /// <summary>
    /// Maps <c>AVG</c>'s operand type to its result type per SQL Server's
    /// rules: integer family rounds-toward-zero in the operand's own type
    /// (<c>AVG(int)</c> → int, truncating); decimal widens to
    /// <c>decimal(38, max(s, 6))</c>; float / real / money pass through.
    /// </summary>
    private static SqlType DeriveAvgResultType(SqlType operandType) => operandType switch
    {
        var t when t == SqlType.TinyInt || t == SqlType.SmallInt => SqlType.Int32,
        var t when t == SqlType.Int32 => SqlType.Int32,
        var t when t == SqlType.BigInt => SqlType.BigInt,
        var t when t == SqlType.Float => SqlType.Float,
        var t when t == SqlType.Real => SqlType.Real,
        var t when t == SqlType.Money => SqlType.Money,
        var t when t == SqlType.SmallMoney => SqlType.SmallMoney,
        DecimalSqlType d => SqlType.GetDecimal(38, (byte)Math.Max((int)d.scale, 6)),
        _ => throw SimulatedSqlException.OperandDataTypeInvalid(operandType, "avg"),
    };

    /// <summary>
    /// Parses an aggregate function call entered with
    /// <see cref="ParserContext.Token"/> at the first argument (the caller
    /// — <see cref="Expression.ResolveBuiltIn"/> — has already consumed the
    /// opening <c>(</c>). Handles the kind-specific argument shapes:
    /// <c>*</c> for COUNT-family star variants, optional <c>ALL</c> /
    /// <c>DISTINCT</c> qualifier, two-arg <c>STRING_AGG</c>. Leaves the token at the closing
    /// <c>)</c>; the caller advances past it.
    /// </summary>
    public static AggregateExpression Parse(ParserContext context, AggregateKind kind)
    {
        if (kind == AggregateKind.StringAgg)
            return ParseStringAgg(context);
        if (kind == AggregateKind.JsonArrayAgg)
            return ParseJsonArrayAgg(context);
        if (kind == AggregateKind.JsonObjectAgg)
            return ParseJsonObjectAgg(context);

        // COUNT(*), COUNT_BIG(*) — the only aggregates that accept a bare `*`.
        if (kind is AggregateKind.Count or AggregateKind.CountBig
            && context.Token is Operator { Character: '*' })
        {
            context.MoveNextRequired();
            return Register(context, new AggregateExpression(kind, operand: null, distinct: false, separator: null));
        }

        var distinct = false;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Distinct }:
                distinct = true;
                context.MoveNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.All }:
                // ALL is the grammar's explicit spelling of the default
                // (COUNT(ALL x) = COUNT(x)); consumed with no effect.
                context.MoveNextRequired();
                break;
        }

        // APPROX_COUNT_DISTINCT is implicitly distinct regardless of keyword.
        if (kind == AggregateKind.ApproxCountDistinct)
            distinct = true;

        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        var operand = Expression.Parse(context);
        ValidateOperand(context, kind, operand, aggregatesBefore, subqueriesBefore);
        return Register(context, new AggregateExpression(kind, operand, distinct, separator: null));
    }

    private static AggregateExpression ParseStringAgg(ParserContext context)
    {
        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        var operand = Expression.Parse(context);
        ValidateOperand(context, AggregateKind.StringAgg, operand, aggregatesBefore, subqueriesBefore);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var separator = Expression.Parse(context);
        return Register(context, new AggregateExpression(AggregateKind.StringAgg, operand, distinct: false, separator: separator));
    }

    /// <summary>
    /// Parses <c>JSON_ARRAYAGG(value [ORDER BY expr [ASC|DESC] [, ...]] [null_clause])</c>.
    /// The <c>ORDER BY</c> sits inside the function parentheses (not a
    /// <c>WITHIN GROUP</c> postfix) and is mutually exclusive with a following
    /// <c>OVER</c> — that conflict is rejected in
    /// <see cref="WindowExpression.WrapAggregate"/>. Default null clause is
    /// <see cref="JsonNullClause.AbsentOnNull"/> (matching <c>JSON_ARRAY</c>).
    /// Leaves the cursor on the closing <c>)</c>.
    /// </summary>
    private static AggregateExpression ParseJsonArrayAgg(ParserContext context)
    {
        var operand = Expression.Parse(context);
        List<OrderBySpec>? orderBy = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
            orderBy = ParseInParensOrderBy(context);
        var jsonNulls = JsonNullClauseParser.Parse(context, JsonNullClause.AbsentOnNull);
        return Register(context, new AggregateExpression(AggregateKind.JsonArrayAgg, operand, distinct: false, separator: null)
        {
            OrderBy = orderBy,
            JsonNulls = jsonNulls,
        });
    }

    /// <summary>
    /// Parses <c>JSON_OBJECTAGG(key : value [null_clause])</c>. Only the colon
    /// key/value form is accepted (the SQL-standard <c>key VALUE value</c>
    /// raises Msg 102, matching SQL Server). Default null clause is
    /// <see cref="JsonNullClause.NullOnNull"/> (matching the scalar
    /// <c>JSON_OBJECT</c> builder). No <c>ORDER BY</c> is permitted. Leaves the
    /// cursor on the closing <c>)</c>.
    /// </summary>
    private static AggregateExpression ParseJsonObjectAgg(ParserContext context)
    {
        // Key parse: redirect a bare ':' to end-of-expression so the colon is
        // seen by this parser rather than swallowed as a type-cast prefix
        // (mirrors JsonObject's key handling).
        var savedFlag = context.StopExpressionAtBareColon;
        context.StopExpressionAtBareColon = true;
        Expression key;
        try
        {
            key = Expression.Parse(context);
        }
        finally
        {
            context.StopExpressionAtBareColon = savedFlag;
        }

        if (context.Token is not Operator { Character: ':' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var value = Expression.Parse(context);
        var jsonNulls = JsonNullClauseParser.Parse(context, JsonNullClause.NullOnNull);
        // JSON_OBJECTAGG has no ordered-set form; ORDER BY here is Msg 156 near
        // the keyword (real SQL Server), not the generic Msg 102 the bare
        // missing-')' fall-through would otherwise raise.
        return context.Token is ReservedKeyword { Keyword: Keyword.Order } orderKeyword
            ? throw SimulatedSqlException.SyntaxErrorNearKeyword(orderKeyword)
            : Register(context, new AggregateExpression(AggregateKind.JsonObjectAgg, value, distinct: false, separator: null)
            {
                KeyExpression = key,
                JsonNulls = jsonNulls,
            });
    }

    /// <summary>
    /// Parses the in-parentheses <c>ORDER BY expr [ASC|DESC] [, ...]</c> of
    /// <c>JSON_ARRAYAGG</c>. Entered with the cursor on the <c>ORDER</c>
    /// keyword; leaves it on the token after the list (the null clause or the
    /// closing <c>)</c>).
    /// </summary>
    private static List<OrderBySpec> ParseInParensOrderBy(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var items = new List<OrderBySpec>();
        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);
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
            items.Add(OrderBySpec.FromExpression(expr, descending));
        }
        while (context.Token is Operator { Character: ',' });
        return items;
    }

    internal override string DebugDisplay()
    {
        var name = this.Kind switch
        {
            AggregateKind.Count => "COUNT",
            AggregateKind.CountBig => "COUNT_BIG",
            AggregateKind.Sum => "SUM",
            AggregateKind.Avg => "AVG",
            AggregateKind.Max => "MAX",
            AggregateKind.Min => "MIN",
            AggregateKind.Stdev => "STDEV",
            AggregateKind.StdevP => "STDEVP",
            AggregateKind.Var => "VAR",
            AggregateKind.VarP => "VARP",
            AggregateKind.StringAgg => "STRING_AGG",
            AggregateKind.ChecksumAgg => "CHECKSUM_AGG",
            AggregateKind.ApproxCountDistinct => "APPROX_COUNT_DISTINCT",
            AggregateKind.JsonArrayAgg => "JSON_ARRAYAGG",
            AggregateKind.JsonObjectAgg => "JSON_OBJECTAGG",
            _ => this.Kind.ToString(),
        };
        if (this.Kind == AggregateKind.JsonObjectAgg)
            return $"{name}({this.KeyExpression!.DebugDisplay()}: {this.Operand!.DebugDisplay()})";
        var distinct = this.Distinct ? "DISTINCT " : "";
        var operand = this.Operand?.DebugDisplay() ?? "*";
        var separator = this.Separator is null ? "" : $", {this.Separator.DebugDisplay()}";
        return $"{name}({distinct}{operand}{separator})";
    }
}
