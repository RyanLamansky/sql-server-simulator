using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>NULLIF(a, b)</c>: returns NULL when the two arguments are equal,
/// otherwise the first argument. Equivalent to
/// <c>CASE WHEN a = b THEN NULL ELSE a END</c>. Result type is fixed to
/// the first argument's type (probe-confirmed: <c>NULLIF(int, decimal)</c>
/// returns int regardless of which arm wins). Equality uses the same
/// promote-and-compare rule as simple-form CASE / <c>=</c>: NULL on either
/// side yields UNKNOWN, falling through to the ELSE arm and returning the
/// first argument (which is itself NULL when the NULL is on the left).
/// </summary>
internal sealed class NullIf : Expression
{
    private readonly Expression a;
    private readonly Expression b;
    private SqlType? cachedResultType;

    public NullIf(ParserContext context)
    {
        this.a = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.b = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var av = this.a.Run(runtime);
        var bv = this.b.Run(runtime);
        var equal = BooleanExpression.CompareValuesPromoted(av, bv, "equal to", static (l, r) => l.Equals(r));
        return equal == true ? SqlValue.Null(this.cachedResultType ?? av.Type) : av;
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.a.GetSqlType(resolveColumnType);
        this.cachedResultType = t;
        return t;
    }

    internal override string DebugDisplay() => $"NULLIF({this.a.DebugDisplay()}, {this.b.DebugDisplay()})";
}
