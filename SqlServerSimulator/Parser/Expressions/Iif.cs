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
    private readonly BooleanExpression condition;
    private readonly Expression trueValue;
    private readonly Expression falseValue;
    private SqlType? cachedResultType;

    public Iif(ParserContext context)
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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var picked = this.condition.Run(runtime) == true
            ? this.trueValue.Run(runtime)
            : this.falseValue.Run(runtime);
        return this.cachedResultType is { } target && !picked.IsNull && picked.Type != target ? picked.CoerceTo(target) : picked;
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = SqlType.Promote(
            this.trueValue.GetSqlType(resolveColumnType),
            this.falseValue.GetSqlType(resolveColumnType));
        this.cachedResultType = t;
        return t;
    }

    internal override string DebugDisplay() => $"IIF(..., {this.trueValue.DebugDisplay()}, {this.falseValue.DebugDisplay()})";
}
