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
/// Msg 245 at runtime when the parse fails, and an out-of-range fallback
/// raises CAST's own Msg 220. Wrong arity
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
        if (context.Token is not Tokens.Operator { Character: ',' })
            return;

        // Real reads the whole argument list before counting it, so a
        // malformed surplus argument is its syntax error rather than Msg 174.
        while (context.Token is Tokens.Operator { Character: ',' })
            _ = Parse(context.MoveNextRequiredReturnSelf());
        throw SimulatedSqlException.FunctionRequiresNArguments("isnull", 2);
    }

    internal override bool ParallelSafe => this.check.ParallelSafe && this.replacement.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var primary = this.check.Run(runtime);
        if (!primary.IsNull)
            return primary;
        var fallback = this.replacement.Run(runtime);
        // A FROM-less SELECT runs its projection before typing it, so the
        // cached type may not be set yet; the check's own NULL carries it.
        var target = this.cachedResultType ?? (IsUntypedNullLiteral(this.check) ? null : primary.Type);
        return target is not null && fallback.Type != target ? Cast.CoerceToDeclared(fallback, target) : fallback;
    }

    // ISNULL fixes the result to the FIRST argument's type — but an untyped
    // NULL first argument yields to the replacement's type (`ISNULL(NULL, 'z')`
    // is varchar, matching real, not the int a bare NULL's placeholder would
    // force). ISNULL does not joint-promote, so no digit-count sizing applies
    // (`ISNULL(1, 2.5)` stays int).
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (IsUntypedNullLiteral(this.check))
            return this.cachedResultType = this.replacement.GetSqlType(batch, resolveColumnType);
        var t = this.check.GetSqlType(batch, resolveColumnType);
        // The replacement converts to the check's type the way an assignment
        // does, so it's the one-way assignment rule that refuses a pair rather
        // than the unification CASE and COALESCE apply (probed 2026-09-24:
        // ISNULL(<decimal>, <datetime>) is Msg 257 where COALESCE answers).
        AssignmentRules.RequireAssignable(this.replacement, this.replacement.GetSqlType(batch, resolveColumnType), t);
        return this.cachedResultType = t;
    }

    internal override string DebugDisplay() => $"ISNULL({this.check.DebugDisplay()}, {this.replacement.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.check).Child(this.replacement);

    // ISNULL(x, y) is non-null iff EITHER operand is non-null: a non-null x
    // short-circuits, otherwise the result is the (possibly-non-null) y.
    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.check.ResultIsNullable(context)
        && this.replacement.ResultIsNullable(context);

    internal override bool ResultReportsNumeric =>
        this.check.ResultReportsNumeric || this.replacement.ResultReportsNumeric;
}
