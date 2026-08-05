using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>IIF(condition, true_value, false_value)</c>: shorthand for the
/// equivalent searched <c>CASE WHEN condition THEN true_value ELSE false_value END</c>.
/// Result type is the joint promotion of the two value arms (matching
/// CASE's branch-type-unification rule); UNKNOWN condition routes to the
/// false arm (probe-confirmed against SQL Server 2025). EF Core 10 emits
/// <c>IIF</c> for ternary <c>?:</c> in projection expressions.
/// </summary>
internal sealed class Iif : Expression
{
    // Assigned in ParseBody (invoked from the ctor inside the CASE-depth
    // try/finally), so these can't be readonly.
    private BooleanExpression condition = null!;
    private Expression trueValue = null!;
    private Expression falseValue = null!;
    private SqlType? cachedResultType;

    public Iif(ParserContext context)
    {
        // IIF desugars to a searched CASE and shares its ten-level nesting cap
        // (Msg 125), but reports State 2 rather than CASE's State 4 — the state
        // identifies the construct being entered at the eleventh level
        // (probe-confirmed 2026-07-18). The counter is shared with CASE via
        // ParserContext.CaseDepth, so mixed CASE/IIF nesting accumulates.
        if (++context.CaseDepth > ParserContext.MaxCaseNestingDepth)
            throw SimulatedSqlException.CaseExpressionsNestedTooDeeply(2);
        try
        {
            ParseBody(context);
        }
        finally
        {
            context.CaseDepth--;
        }
    }

    private void ParseBody(ParserContext context)
    {
        this.condition = BooleanExpression.Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.trueValue = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.falseValue = Parse(context.MoveNextRequiredReturnSelf());

        // IIF desugars to a searched CASE in SQL Server, so Msg 8133 fires
        // when both value arms are bare NULL literals — probe-confirmed
        // against SQL Server 2025 (verbatim CASE wording, not an IIF-specific
        // message). A single typed NULL (`CAST(NULL AS int)`) on either arm
        // satisfies the rule.
        if (IsBareNullLiteral(this.trueValue) && IsBareNullLiteral(this.falseValue))
            throw SimulatedSqlException.AllResultsInCaseAreNull();
    }

    internal override bool ParallelSafe => this.condition.ParallelSafe && this.trueValue.ParallelSafe && this.falseValue.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var picked = this.condition.Run(runtime) == true
            ? this.trueValue.Run(runtime)
            : this.falseValue.Run(runtime);
        return this.cachedResultType is { } target && !picked.IsNull && picked.Type != target ? picked.CoerceTo(target) : picked;
    }

    // Shares CASE's branch-type unification: an untyped-NULL arm yields to the
    // typed arm (`IIF(c, NULL, 'x')` → varchar) and an integer-literal arm
    // sizes by digit count against a decimal sibling — via PromoteValueArms.
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        this.cachedResultType = PromoteValueArms([this.trueValue, this.falseValue], batch, resolveColumnType);
        return this.cachedResultType;
    }

    // IIF desugars to a searched CASE, so it inherits CASE's metadata rule: the
    // result is NOT NULL only when both value arms are non-null (probe-confirmed
    // against SQL Server 2025; exposed by go-mssqldb / tedious COLMETADATA
    // fNullable) — unless the condition is one real folds, which leaves only
    // the arm it selects (`IIF(1 = 1, 5, NULL)` is NOT NULL, `IIF(1 = 2, 5, NULL)`
    // nullable).
    // A surviving arm also answers for the conversion the arm unification put
    // on it (Expression.ArmConversionIsNullable).
    internal override bool ResultIsNullable(NullabilityContext context)
    {
        var promoted = context.TypeOf(this);
        if (context.TryFoldCondition(this.condition, out var branchTaken))
        {
            var taken = branchTaken ? this.trueValue : this.falseValue;
            return taken.ResultIsNullable(context) || ArmConversionIsNullable(taken, promoted, context);
        }

        return this.trueValue.ResultIsNullable(context)
            || this.falseValue.ResultIsNullable(context)
            || ArmConversionIsNullable(this.trueValue, promoted, context)
            || ArmConversionIsNullable(this.falseValue, promoted, context);
    }

    internal override bool ResultReportsNumeric =>
        this.trueValue.ResultReportsNumeric || this.falseValue.ResultReportsNumeric;

    internal override string DebugDisplay() => $"IIF(..., {this.trueValue.DebugDisplay()}, {this.falseValue.DebugDisplay()})";
}
