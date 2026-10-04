using SqlServerSimulator.Parser.Aggregators;
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
    Product,
    ApproxPercentileCont,
    ApproxPercentileDisc,

    /// <summary>A CLR user-defined aggregate; see <see cref="AggregateExpression.ClrFunction"/>.</summary>
    ClrAggregate,

    /// <summary>
    /// One of the four aggregates the spatial types expose as static methods —
    /// <c>geometry::UnionAggregate(col)</c> and its siblings; see
    /// <see cref="AggregateExpression.SpatialMethod"/>.
    /// </summary>
    SpatialAggregate,
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
    /// <c>APPROX_COUNT_DISTINCT</c>'s window gate and the name its refusal
    /// quotes. Real reads <c>APPROX_COUNT_DISTINCT(ALL x) OVER (…)</c> as a
    /// windowed distinct count — frames, running totals and partitions all
    /// apply — while the unquantified call with an <c>OVER</c> is Msg 4113
    /// naming the function as written (probed 2026-09-29 against SQL Server
    /// 2025). Null for every other kind, and for the <c>ALL</c> form.
    /// </summary>
    internal string? RefusedWindowName;

    /// <summary>
    /// Set by the projection planner when real reduces this
    /// <c>COUNT</c> / <c>COUNT_BIG</c> to <c>COUNT(*)</c> and never evaluates
    /// the argument — see <c>Selection.ReduceConstantCounts</c> for the rule
    /// and its fences. Value-independent, so it rides the cached plan.
    /// </summary>
    public bool CountsRowsOnly;

    /// <summary>
    /// False for an aggregate real doesn't report a skipped NULL for — a
    /// PIVOT's (see <see cref="CreatePivotAggregate"/>) and one in an
    /// <c>EXISTS</c> body.
    /// </summary>
    internal bool WarnsOnNullInput = true;

    /// <summary>
    /// Set at parse time when this aggregate was written in a nested scope but
    /// reads only an enclosing query's columns, so that query owns it and the
    /// nested scope reads the value bound for the enclosing query's current
    /// group. That value changes from one enclosing row to the next without the
    /// nested plan reading the enclosing row, so <see cref="Run"/> counts itself
    /// among <see cref="SimulatedDbConnection.VolatileEvaluations"/>: a
    /// subquery or deferred source reading it can't replay one execution for
    /// the rest of the statement.
    /// </summary>
    public bool ReadsEnclosingGroup;

    /// <summary>
    /// Passes one operand value through, noting on the executing statement
    /// when it is a NULL this aggregate skips with real's Msg 8153 warning —
    /// every aggregate does but <c>COUNT(*)</c> (and a <c>COUNT</c> reduced to
    /// it), <c>STRING_AGG</c> and the JSON aggregates (probed 2026-09-23).
    /// </summary>
    internal SqlValue ObserveInput(SqlValue value, RuntimeContext runtime)
    {
        if (value.IsNull && this.WarnsOnNullInput && !this.CountsRowsOnly && this.Operand is not null
            && this.Kind is not (AggregateKind.StringAgg or AggregateKind.JsonArrayAgg or AggregateKind.JsonObjectAgg or AggregateKind.ClrAggregate or AggregateKind.SpatialAggregate))
        {
            runtime.Batch.CurrentStatement.NullEliminated = true;
        }
        return value;
    }

    /// <summary>
    /// Set at parse time when this aggregate sits in a <c>CASE</c> arm — or
    /// behind a <c>COALESCE</c> argument — real settled as unreachable while
    /// compiling, so the aggregate pass must not evaluate its operand per row
    /// (<c>SELECT CASE 23 WHEN -38 THEN COUNT(7 / 0) ELSE 2 END</c> answers 2
    /// on real). The aggregate stays <em>registered</em> all the same, because
    /// real keeps the query a vector aggregate and keeps reporting Msg 8120
    /// for an ungrouped column beside it even when the arm holding its only
    /// aggregate is the one it dropped (both probe-confirmed). Its result is
    /// never read — the arm that would read it can't be reached.
    /// </summary>
    public bool OperandUnreachable;

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

    /// <summary>
    /// For the two JSON aggregates, whether a <c>RETURNING json</c> clause
    /// asked for a <c>json</c> result — which a <c>json</c> operand also
    /// yields (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public bool ReturningJson;

    /// <summary>
    /// For <see cref="AggregateKind.ClrAggregate"/>, the aggregate called;
    /// null for every other kind.
    /// </summary>
    public Schemas.ClrAggregateFunction? ClrFunction;

    /// <summary>
    /// For <see cref="AggregateKind.ClrAggregate"/>, every argument in order —
    /// <see cref="Operand"/> is the first — since a CLR aggregate's
    /// <c>Accumulate</c> may take several.
    /// </summary>
    public Expression[]? ClrArguments;

    /// <summary>For <see cref="AggregateKind.SpatialAggregate"/>, which of the four it is.</summary>
    public SpatialAggregateMethod SpatialMethod;

    /// <summary>For <see cref="AggregateKind.SpatialAggregate"/>, the spatial type the aggregate belongs to and returns.</summary>
    public SpatialSqlType? SpatialType;

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
        AggregateKind.Product => "product",
        AggregateKind.ApproxPercentileCont => "approx_percentile_cont",
        AggregateKind.ApproxPercentileDisc => "approx_percentile_disc",
        AggregateKind.ClrAggregate or AggregateKind.SpatialAggregate => "aggregate",
        _ => throw new InvalidOperationException($"Unknown aggregate kind {kind}."),
    };

    /// <summary>
    /// A JSON aggregate's result: <c>json</c> for <c>RETURNING json</c> or a
    /// <c>json</c> operand, else JSON text. A CLR-typed key or value is
    /// Msg 13666 (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private SqlType BindJsonAggregate(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.Operand!.GetSqlType(batch, resolveColumnType);
        var isObject = this.Kind == AggregateKind.JsonObjectAgg;
        if (JsonValueRender.IsClrType(operandType) || (isObject && JsonValueRender.IsClrType(this.KeyExpression!.GetSqlType(batch, resolveColumnType))))
            throw isObject ? SimulatedSqlException.JsonBuilderClrType("json_object and json_objectagg", 2) : SimulatedSqlException.JsonBuilderClrType("json_arrayagg", 1);
        return this.ReturningJson || operandType is JsonSqlType ? SqlType.Json : SqlType.JsonTextMax;
    }

    /// <summary>
    /// Builds a single-operand aggregate programmatically (used by PIVOT
    /// desugaring, where each pivot column becomes
    /// <c>&lt;kind&gt;(CASE forCol WHEN value THEN argCol END)</c>). Bypasses
    /// the token parser and the <c>AggregateCollector</c> registration — the
    /// PIVOT planner hands the built list straight to
    /// <c>Selection.BuildSqlProjection</c>.
    /// These never raise Msg 8153: real's PIVOT doesn't, even over a NULL in
    /// the value column (probed 2026-09-23), and the <c>CASE</c> hands the
    /// aggregate a NULL for every row of another pivot column besides.
    /// </summary>
    internal static AggregateExpression CreatePivotAggregate(AggregateKind kind, Expression operand) =>
        new(kind, operand, distinct: false, separator: null) { WarnsOnNullInput = false };

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
        context.Batch.BindErrors?.NoteAggregate(expression, context.Token);
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
            if (context.Batch.BindErrors is { } report && report.Covers(context.Token))
                report.RecordUnlessPreceded(SimulatedSqlException.AggregateOnAggregateOrSubquery(), context.Token!.StartIndex);
            else
                throw SimulatedSqlException.AggregateOnAggregateOrSubquery();
        }
        if (IsUntypedNullLiteral(operand))
            throw UntypedNullOperand(kind);
    }

    /// <summary>
    /// A windowed function in an aggregate's operand — <c>SUM(ROW_NUMBER()
    /// OVER (…))</c>, windowed or not — is Msg 4109 (probed 2026-10-01 against
    /// SQL Server 2025). A subquery's windows register with its own block, so
    /// only one written directly in the operand counts.
    /// </summary>
    private static void RefuseNestedWindow(ParserContext context, int windowsBefore)
    {
        if ((context.WindowCollector?.Count ?? 0) > windowsBefore)
            throw SimulatedSqlException.WindowedFunctionInAggregate();
    }

    /// <summary>
    /// The refusal of an operand with no type — the bare <c>NULL</c>, or a
    /// derived column filled only with it. STRING_AGG names the argument where
    /// the others name the operator (Msg 8116, probed 2026-09-24).
    /// </summary>
    internal static SimulatedSqlException UntypedNullOperand(AggregateKind kind) =>
        kind == AggregateKind.StringAgg
            ? SimulatedSqlException.InvalidArgumentDataType("NULL", 1, "string_agg")
            : SimulatedSqlException.OperandDataTypeNullInvalid(LowerNameOf(kind));

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

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.ReadsEnclosingGroup)
            runtime.Batch.Connection.VolatileEvaluations++;
        return runtime.Batch.BoundProjectionResults is { } bound && bound.TryGetValue(this, out var result)
            ? result
            : throw new InvalidOperationException("AggregateExpression.Run was called before its result was bound; this indicates the Selection executor didn't recognize it as an aggregate.");
    }

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
        AggregateKind.CountBig => SqlType.BigInt,
        AggregateKind.ApproxCountDistinct => this.RequireOperandType(batch, resolveColumnType, SqlType.BigInt),
        AggregateKind.ChecksumAgg => this.RequireOperandType(batch, resolveColumnType, SqlType.Int32),
        AggregateKind.Stdev or AggregateKind.StdevP or AggregateKind.Var or AggregateKind.VarP => this.RequireOperandType(batch, resolveColumnType, SqlType.Float),
        // MAX / MIN order their input, so an unresolved collation reports here
        // (Msg 4191 naming `max` / `min`) rather than travelling on.
        AggregateKind.Max or AggregateKind.Min => BindOrderedOperand(batch, resolveColumnType),
        // STRING_AGG refuses a legacy LOB in either slot, and real binds that
        // while compiling — so the gate runs here as well as per value.
        AggregateKind.StringAgg => BindStringAggArguments(batch, resolveColumnType),
        AggregateKind.JsonArrayAgg or AggregateKind.JsonObjectAgg => this.BindJsonAggregate(batch, resolveColumnType),
        AggregateKind.Sum => DeriveSumResultType(this.RejectDistinctLob(this.Operand!.GetSqlType(batch, resolveColumnType))),
        AggregateKind.Avg => DeriveAvgResultType(this.RejectDistinctLob(this.Operand!.GetSqlType(batch, resolveColumnType))),
        AggregateKind.Product => DeriveProductResultType(this.RejectDistinctLob(this.Operand!.GetSqlType(batch, resolveColumnType))),
        AggregateKind.ApproxPercentileCont or AggregateKind.ApproxPercentileDisc => this.BindApproxPercentile(batch, resolveColumnType),
        AggregateKind.ClrAggregate => this.BindClrArguments(batch, resolveColumnType),
        AggregateKind.SpatialAggregate => this.BindSpatialOperand(batch, resolveColumnType),
        _ => throw new InvalidOperationException($"Unknown aggregate kind {this.Kind}."),
    };

    /// <summary>
    /// A CLR aggregate's arguments each bind to their declared parameter's
    /// type as an assignment would; the result is the declared
    /// <c>RETURNS</c> type.
    /// </summary>
    /// <summary>
    /// A spatial aggregate's operand binds like a CLR parameter of the
    /// aggregate's own type — a string converts, an <c>int</c> is Msg 206 —
    /// and the result is that type.
    /// </summary>
    private SpatialSqlType BindSpatialOperand(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.Operand!, this.SpatialType!, batch, resolveColumnType);
        return this.SpatialType!;
    }

    private SqlType BindClrArguments(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var function = this.ClrFunction!;
        for (var i = 0; i < this.ClrArguments!.Length; i++)
            _ = AssignmentRules.ArgumentType(this.ClrArguments[i], function.Parameters[i].Type, batch, resolveColumnType);
        return function.ReturnType;
    }

    /// <summary>
    /// Parses a CLR aggregate's call — <c>schema.name([DISTINCT | ALL] arg,
    /// …)</c> — entered with the cursor on the token after <c>(</c> and left on
    /// the closing <c>)</c>. Real reads NULL inputs into <c>Accumulate</c> and
    /// raises no Msg 8153 for them, and a wrong argument count is Msg 174
    /// naming the call as written (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static AggregateExpression ParseClr(Schemas.ClrAggregateFunction function, MultiPartName written, ParserContext context)
    {
        context.SecurableSink?.Add(new ReferencedSecurable(function.Schema.Database, function.ObjectId, function.SchemaId, function.Name, function.Schema.Name, "EXECUTE"));
        using var rejection = context.EnterNextValueForScope(NextValueForScope.Aggregate);
        var distinct = false;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Distinct }:
                distinct = true;
                context.MoveNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.All }:
                context.MoveNextRequired();
                break;
        }

        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        List<Expression> arguments = [];
        if (context.Token is not Operator { Character: ')' })
        {
            while (true)
            {
                arguments.Add(Expression.Parse(context));
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (arguments.Count != function.Parameters.Length)
            throw SimulatedSqlException.FunctionRequiresNArguments(written.ToString(), function.Parameters.Length);
        ValidateOperand(context, AggregateKind.ClrAggregate, arguments[0], aggregatesBefore, subqueriesBefore);
        return Register(context, new AggregateExpression(AggregateKind.ClrAggregate, arguments[0], distinct, separator: null)
        {
            ClrFunction = function,
            ClrArguments = [.. arguments],
        });
    }

    /// <summary>
    /// Parses a spatial aggregate's call — <c>geometry::UnionAggregate(arg)</c>
    /// and its siblings — entered with the cursor on the token after <c>(</c>
    /// and left on the closing <c>)</c>. Like a CLR aggregate it reads NULL
    /// inputs silently (no Msg 8153), and a wrong argument count is Msg 174
    /// naming the method as written.
    /// </summary>
    internal static AggregateExpression ParseSpatial(SpatialSqlType type, SpatialAggregateMethod method, string written, ParserContext context)
    {
        using var rejection = context.EnterNextValueForScope(NextValueForScope.Aggregate);
        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        List<Expression> arguments = [];
        if (context.Token is not Operator { Character: ')' })
        {
            while (true)
            {
                arguments.Add(Expression.Parse(context));
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (arguments.Count != 1)
            throw SimulatedSqlException.FunctionRequiresNArguments(written, 1);
        ValidateOperand(context, AggregateKind.SpatialAggregate, arguments[0], aggregatesBefore, subqueriesBefore);
        return Register(context, new AggregateExpression(AggregateKind.SpatialAggregate, arguments[0], distinct: false, separator: null)
        {
            SpatialMethod = method,
            SpatialType = type,
        });
    }

    /// <summary>
    /// The operand rule of an aggregate that reads only some types, settled
    /// while compiling (probed 2026-09-25 against SQL Server 2025): STDEV /
    /// VAR and their population forms take a number other than <c>bit</c>,
    /// CHECKSUM_AGG an <c>int</c> and nothing wider or narrower, and
    /// APPROX_COUNT_DISTINCT anything comparable save <c>sql_variant</c> and
    /// <c>hierarchyid</c>. Anything else is Msg 8117, at state 2 when the
    /// operand also can't be compared for the distinct count it would need.
    /// </summary>
    private SqlType RequireOperandType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType, SqlType resultType)
    {
        var operandType = this.Operand!.GetSqlType(batch, resolveColumnType);
        var accepted = this.Kind switch
        {
            AggregateKind.ChecksumAgg => operandType == SqlType.Int32,
            AggregateKind.ApproxCountDistinct => !operandType.IsIncomparable && operandType is not (SqlVariantSqlType or HierarchyIdSqlType),
            _ => operandType.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate
                && operandType != SqlType.Bit,
        };
        return accepted
            ? resultType
            : throw SimulatedSqlException.OperandDataTypeInvalid(SqlType.OperandName(operandType, this.Operand), this.LowerName, (this.Distinct || this.Kind == AggregateKind.ApproxCountDistinct) && operandType.IsIncomparable ? (byte)2 : (byte)1);
    }

    /// <summary>
    /// SUM / AVG over a <c>DISTINCT</c> operand that can't be compared: the
    /// same Msg 8117 the type itself earns, at state 2 (probed 2026-09-25).
    /// </summary>
    private SqlType RejectDistinctLob(SqlType operandType) =>
        this.Distinct && operandType.IsIncomparable
            ? throw SimulatedSqlException.OperandDataTypeInvalid(operandType, this.LowerName, 2)
            : operandType;

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
        // Real refuses a vector value twice over, at state 1 and again at 6
        // (probed 2026-09-26 against SQL Server 2025).
        if (operandType is VectorSqlType)
        {
            throw SimulatedSqlException.Aggregate([
                SimulatedSqlException.InvalidArgumentDataType(operandType.SqlServerName, 1, "string_agg"),
                SimulatedSqlException.InvalidArgumentDataType(operandType.SqlServerName, 1, "string_agg", 6)]);
        }
        var separatorType = StringScalars.BindArgument(this.Separator!, batch, resolveColumnType, "string_agg", argumentIndex: 2);
        // The value is judged before the separator (probed 2026-09-26 against
        // SQL Server 2025: a binary in both is argument 1's Msg 8116 first).
        var resultType = Aggregators.StringAggAggregator.ResultType(operandType, batch);
        this.RejectSeparator(this.Separator!, separatorType, operandType, batch);
        return resultType;
    }

    /// <summary>
    /// STRING_AGG's separator is a string — never a Unicode one beside a
    /// non-Unicode string operand — (Msg 8116), and then a variable or a
    /// value real folds to a constant (Msg 8733): <c>','</c>, <c>',' + ','</c>,
    /// <c>CHAR(44)</c> and a bare <c>NULL</c> pass, a column, <c>@s + @s</c>
    /// and <c>UPPER(',')</c> don't. Both while compiling, the type first
    /// (probed 2026-09-24 against SQL Server 2025).
    /// </summary>
    private void RejectSeparator(Expression separator, SqlType separatorType, SqlType operandType, BatchContext batch)
    {
        if (IsUntypedNullLiteral(separator))
            return;
        if (separatorType.Category != SqlTypeCategory.String
            || (SqlType.IsNationalStringCategory(separatorType)
                && operandType.Category == SqlTypeCategory.String
                && !SqlType.IsNationalStringCategory(operandType)))
        {
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(separatorType, separator), 2, "string_agg");
        }

        var bare = separator;
        while (bare is Parenthesized parenthesized)
            bare = parenthesized.Wrapped;
        if (bare is VariableReference)
            return;
        // A CAST or CONVERT of a variable answers as a scalar aggregate's
        // separator alone, which the query block settles once it knows its
        // grouping (probed 2026-10-04 against SQL Server 2025).
        if (bare.PureConversionOperand is { } converted && Peel(converted) is VariableReference)
        {
            this.SeparatorCastsVariable = true;
            return;
        }
        if (!separator.IsWrittenConstant)
            throw SimulatedSqlException.StringAggSeparatorNotLiteralOrVariable();
        // A constant only through a conversion of a literal is one real's
        // simple parameterization turns into a parameter; the statement
        // decides once it has parsed.
        if (ConvertsLiteral(separator) && batch.Parser is { } parser)
            parser.ParameterizedSeparatorRefusal ??= SimulatedSqlException.StringAggSeparatorNotLiteralOrVariable();
    }

    private static Expression Peel(Expression expression)
    {
        while (expression is Parenthesized parenthesized)
            expression = parenthesized.Wrapped;
        return expression;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> converts a literal other than
    /// <c>NULL</c> — a <c>CAST</c>, <c>CONVERT</c> or their <c>TRY_</c> forms
    /// anywhere in it, style or none, which simple parameterization makes a
    /// parameter of.
    /// </summary>
    private static bool ConvertsLiteral(Expression expression)
    {
        var converts = false;
        expression.Walk((node, _) =>
        {
            if (node is Cast or ConvertExpression && ((Expression)node).PureConversionOperand is { } operand)
                converts |= !IsUntypedNullLiteral(operand);
            return !converts;
        });
        return converts;
    }

    /// <summary>
    /// Set while binding when the separator is a <c>CAST</c> or
    /// <c>CONVERT</c> of a variable, which real takes only in a query block
    /// with no grouping (Msg 8733 otherwise).
    /// </summary>
    internal bool SeparatorCastsVariable;

    // SUM / AVG / MIN / MAX preserve the operand's decimal-vs-numeric name; the other
    // kinds have non-decimal results the projection-time gate filters out.
    internal override bool ResultReportsNumeric => this.Operand?.ResultReportsNumeric ?? false;

    internal override Schemas.AliasType? ResultAliasType =>
        this.Kind is AggregateKind.Max or AggregateKind.Min or AggregateKind.Sum ? this.Operand?.ResultAliasType : null;

    /// <summary>
    /// Maps <c>SUM</c>'s operand type to its result type per SQL Server's
    /// rules: integer family widens to <see cref="SqlType.Int32"/> for
    /// tinyint/smallint and stays at the operand type for int/bigint;
    /// decimal becomes <c>decimal(38, s)</c> preserving scale; <c>real</c>
    /// widens to <c>float</c>; float and money pass through. Probed against
    /// SQL Server 2025 — int does NOT auto-widen to bigint, so an overflowing
    /// sum raises Msg 8115.
    /// </summary>
    /// <summary>
    /// <c>APPROX_PERCENTILE_CONT</c> reads any number into <c>float</c>;
    /// <c>APPROX_PERCENTILE_DISC</c> answers in its operand's type but takes
    /// no <c>decimal</c> / <c>numeric</c>; anything else is Msg 402 against
    /// <c>numeric</c>, and a constant fraction outside <c>[0, 1]</c> — NULL
    /// included — is Msg 8727 (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private SqlType BindApproxPercentile(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.Operand!.GetSqlType(batch, resolveColumnType);
        _ = this.Separator!.GetSqlType(batch, resolveColumnType);
        if (this.Separator is Value { } constant)
        {
            var fraction = constant.Run(new RuntimeContext(static name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch));
            if (fraction.IsNull || fraction.CoerceTo(SqlType.Float).AsDouble is < 0 or > 1)
                throw SimulatedSqlException.PercentileInputOutOfRange();
        }
        var continuous = this.Kind == AggregateKind.ApproxPercentileCont;
        var accepted = operandType.Category is SqlTypeCategory.Integer or SqlTypeCategory.Approximate or SqlTypeCategory.Money
            || (continuous && operandType is DecimalSqlType);
        return !accepted
            ? throw SimulatedSqlException.IncompatibleDataTypesInOperator("numeric", operandType.SqlServerName, this.LowerName)
            : continuous ? SqlType.Float : operandType;
    }

    /// <summary>
    /// Parses <c>APPROX_PERCENTILE_CONT | _DISC (fraction) WITHIN GROUP (ORDER BY
    /// value [ASC | DESC])</c>: the value is the operand, the fraction rides in
    /// <see cref="Separator"/>'s slot, and the direction in <see cref="OrderBy"/>.
    /// The ordering is mandatory and takes exactly one expression (Msg 10751).
    /// Leaves the cursor on the WITHIN GROUP's closing <c>)</c>.
    /// </summary>
    internal static AggregateExpression ParseApproxPercentile(ParserContext context, AggregateKind kind)
    {
        var name = LowerNameOf(kind);
        // The fraction is part of what the call aggregates, so an aggregate or
        // subquery there is Msg 130 as in the ordering (probed 2026-09-29).
        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        var fraction = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
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
        var value = Expression.Parse(context);
        ValidateOperand(context, kind, value, aggregatesBefore, subqueriesBefore);
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
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.WithinGroupNeedsOneExpression(name);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return Register(context, new AggregateExpression(kind, value, distinct: false, separator: fraction)
        {
            OrderBy = [OrderBySpec.FromExpression(value, descending)],
        });
    }

    /// <summary>
    /// <c>PRODUCT</c>'s result type: SUM's, except that a decimal with any
    /// fractional digits multiplies at scale 6 whatever its own (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static SqlType DeriveProductResultType(SqlType operandType) => operandType switch
    {
        DecimalSqlType d => SqlType.GetDecimal(38, d.scale == 0 ? 0 : 6),
        _ when operandType.Category is SqlTypeCategory.Integer or SqlTypeCategory.Approximate or SqlTypeCategory.Money && operandType != SqlType.Bit
            => DeriveSumResultType(operandType),
        _ => throw SimulatedSqlException.OperandDataTypeInvalid(operandType, "product"),
    };

    private static SqlType DeriveSumResultType(SqlType operandType) => operandType switch
    {
        var t when t == SqlType.TinyInt || t == SqlType.SmallInt => SqlType.Int32,
        var t when t == SqlType.Int32 => SqlType.Int32,
        var t when t == SqlType.BigInt => SqlType.BigInt,
        var t when t == SqlType.Float || t == SqlType.Real => SqlType.Float,
        var t when t == SqlType.Money || t == SqlType.SmallMoney => SqlType.Money,
        DecimalSqlType d => SqlType.GetDecimal(38, d.scale),
        _ => throw SimulatedSqlException.OperandDataTypeInvalid(operandType, "sum"),
    };

    /// <summary>
    /// Maps <c>AVG</c>'s operand type to its result type per SQL Server's
    /// rules: integer family rounds-toward-zero in the operand's own type
    /// (<c>AVG(int)</c> → int, truncating); decimal widens to
    /// <c>decimal(38, max(s, 6))</c>; float passes through and
    /// <c>real</c> widens to it; both money types report <c>money</c>.
    /// </summary>
    private static SqlType DeriveAvgResultType(SqlType operandType) => operandType switch
    {
        var t when t == SqlType.TinyInt || t == SqlType.SmallInt => SqlType.Int32,
        var t when t == SqlType.Int32 => SqlType.Int32,
        var t when t == SqlType.BigInt => SqlType.BigInt,
        var t when t == SqlType.Float || t == SqlType.Real => SqlType.Float,
        var t when t == SqlType.Money || t == SqlType.SmallMoney => SqlType.Money,
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
    public static AggregateExpression Parse(ParserContext context, AggregateKind kind, string? writtenName = null)
    {
        // Real refuses NEXT VALUE FOR anywhere in an aggregate's arguments
        // with its own message (Msg 11725), ahead of the DISTINCT, TOP and
        // CASE refusals the same statement may also earn — probe-confirmed.
        // A windowed call is the exception: its trailing OVER makes the
        // reference one of the clauses Msg 11720 names (probed 2026-09-29).
        var windowed = IsWindowedCall(context);
        using var rejection = context.EnterNextValueForScope(windowed ? NextValueForScope.Clause : NextValueForScope.Aggregate);
        using var overBody = ParserScope.Enter(ref context.InOverBody, context.InOverBody | windowed);
        var aggregate = ParseArguments(context, kind, out var allWritten);
        if (kind == AggregateKind.ApproxCountDistinct && !allWritten)
            aggregate.RefusedWindowName = writtenName;
        return aggregate;
    }

    /// <summary>
    /// True when the call whose first argument the cursor is on closes with a
    /// <c>)</c> that <c>OVER</c> follows. A token-only scan against a
    /// checkpoint, so nothing the arguments parse is affected.
    /// </summary>
    private static bool IsWindowedCall(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var depth = 1;
            while (depth > 0)
            {
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' }:
                        depth--;
                        break;
                }
                if (!context.MoveNext())
                    return false;
            }
            return context.Token is ReservedKeyword { Keyword: Keyword.Over };
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    private static AggregateExpression ParseArguments(ParserContext context, AggregateKind kind, out bool allWritten)
    {
        allWritten = false;
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
                allWritten = true;
                context.MoveNextRequired();
                break;
        }

        // APPROX_COUNT_DISTINCT is implicitly distinct regardless of keyword.
        if (kind == AggregateKind.ApproxCountDistinct)
            distinct = true;

        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        var windowsBefore = context.WindowCollector?.Count ?? 0;
        var operand = Expression.Parse(context);
        RefuseNestedWindow(context, windowsBefore);
        ValidateOperand(context, kind, operand, aggregatesBefore, subqueriesBefore);
        return Register(context, new AggregateExpression(kind, operand, distinct, separator: null));
    }

    private static AggregateExpression ParseStringAgg(ParserContext context)
    {
        var aggregatesBefore = context.AggregatesParsed;
        var subqueriesBefore = context.SubqueriesParsed;
        var windowsBefore = context.WindowCollector?.Count ?? 0;
        var operand = Expression.Parse(context);
        RefuseNestedWindow(context, windowsBefore);
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
        var returningJson = JsonNullClauseParser.ParseReturning(context);
        return Register(context, new AggregateExpression(AggregateKind.JsonArrayAgg, operand, distinct: false, separator: null)
        {
            OrderBy = orderBy,
            JsonNulls = jsonNulls,
            ReturningJson = returningJson,
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
        Expression key;
        using (ParserScope.Enter(ref context.StopExpressionAtBareColon, true))
        {
            key = Expression.Parse(context);
        }

        if (context.Token is not Operator { Character: ':' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var value = Expression.Parse(context);
        var jsonNulls = JsonNullClauseParser.Parse(context, JsonNullClause.NullOnNull);
        var returningJson = JsonNullClauseParser.ParseReturning(context);
        // JSON_OBJECTAGG has no ordered-set form; ORDER BY here is Msg 156 near
        // the keyword (real SQL Server), not the generic Msg 102 the bare
        // missing-')' fall-through would otherwise raise.
        return context.Token is ReservedKeyword { Keyword: Keyword.Order } orderKeyword
            ? throw SimulatedSqlException.SyntaxErrorNearKeyword(orderKeyword)
            : Register(context, new AggregateExpression(AggregateKind.JsonObjectAgg, value, distinct: false, separator: null)
            {
                KeyExpression = key,
                JsonNulls = jsonNulls,
                ReturningJson = returningJson,
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
            AggregateKind.Product => "PRODUCT",
            AggregateKind.ApproxPercentileCont => "APPROX_PERCENTILE_CONT",
            AggregateKind.ApproxPercentileDisc => "APPROX_PERCENTILE_DISC",
            AggregateKind.ClrAggregate => $"{this.ClrFunction!.Schema.Name}.{this.ClrFunction.Name}",
            _ => this.Kind.ToString(),
        };
        if (this.Kind == AggregateKind.JsonObjectAgg)
            return $"{name}({this.KeyExpression!.DebugDisplay()}: {this.Operand!.DebugDisplay()})";
        var distinct = this.Distinct ? "DISTINCT " : "";
        var operand = this.Operand?.DebugDisplay() ?? "*";
        var separator = this.Separator is null ? "" : $", {this.Separator.DebugDisplay()}";
        if (this.ClrArguments is { } clrArguments)
            separator = string.Concat(clrArguments.Skip(1).Select(argument => ", " + argument.DebugDisplay()));
        return $"{name}({distinct}{operand}{separator})";
    }

    internal override void Describe(NodeShape shape)
    {
        _ = shape.Local(this.Kind).Local(this.Distinct).Local(this.JsonNulls).Local(this.ReturningJson).Child(this.KeyExpression).Child(this.Operand).Child(this.Separator).Local(this.OrderBy?.Count ?? -1);
        foreach (var item in this.OrderBy ?? [])
            _ = shape.Local(item.Descending).Local(item.Ordinal).Child(item.Expr);
        // A CLR aggregate's first argument is its Operand, reported above.
        _ = shape.Local(this.ClrFunction);
        for (var i = 1; i < (this.ClrArguments?.Length ?? 0); i++)
            _ = shape.Child(this.ClrArguments![i]);
    }
}
