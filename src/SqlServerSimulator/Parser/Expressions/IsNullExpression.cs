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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var primary = this.check.Run(runtime);
        if (!primary.IsNull)
            return primary;
        var fallback = this.replacement.Run(runtime);
        return this.cachedResultType is { } target && fallback.Type != target ? fallback.CoerceTo(target) : fallback;
    }

    // ISNULL fixes the result to the FIRST argument's type — but an untyped
    // NULL first argument yields to the replacement's type (`ISNULL(NULL, 'z')`
    // is varchar, matching real, not the int a bare NULL's placeholder would
    // force). ISNULL does not joint-promote, so no digit-count sizing applies
    // (`ISNULL(1, 2.5)` stays int).
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = IsUntypedNullLiteral(this.check)
            ? this.replacement.GetSqlType(batch, resolveColumnType)
            : this.check.GetSqlType(batch, resolveColumnType);
        this.cachedResultType = t;
        return t;
    }

    internal override string DebugDisplay() => $"ISNULL({this.check.DebugDisplay()}, {this.replacement.DebugDisplay()})";

    // ISNULL(x, y) is non-null iff EITHER operand is non-null: a non-null x
    // short-circuits, otherwise the result is the (possibly-non-null) y.
    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) =>
        this.check.ResultIsNullable(resolveColumnNullable)
        && this.replacement.ResultIsNullable(resolveColumnNullable);
}
