using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ISNULL(check, replacement)</c>: returns the first argument if it
/// isn't NULL, otherwise the second argument coerced to the first's type.
/// Distinct from 2-arg <c>COALESCE</c>: <c>COALESCE</c> picks a joint-promoted
/// result type across all operands, while <c>ISNULL</c> fixes the result
/// type (and length / precision) to the first argument's declared type —
/// the second is always coerced to match. Probe-confirmed against
/// SQL Server 2025: <c>ISNULL(varchar(5), 'longerstring')</c> truncates the
/// fallback to 5 characters; <c>ISNULL(int_null, '42')</c> parses the string
/// fallback through int's CAST path; <c>ISNULL(int_null, 'abc')</c> raises
/// Msg 245 at runtime when the parse fails. Wrong arity
/// (1 or 3+ arguments) raises Msg 174.
/// </summary>
internal sealed class IsNullExpression : Expression
{
    private readonly Expression check;
    private readonly Expression replacement;
    private SqlType? cachedResultType;

    public IsNullExpression(ParserContext context)
    {
        this.check = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("isnull", 2);
        this.replacement = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("isnull", 2);
    }

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        var primary = this.check.Run(getColumnValue);
        if (!primary.IsNull)
            return primary;
        var fallback = this.replacement.Run(getColumnValue);
        return this.cachedResultType is { } target && fallback.Type != target ? fallback.CoerceTo(target) : fallback;
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.check.GetSqlType(resolveColumnType);
        this.cachedResultType = t;
        return t;
    }

    internal override string DebugDisplay() => $"ISNULL({this.check.DebugDisplay()}, {this.replacement.DebugDisplay()})";
}
