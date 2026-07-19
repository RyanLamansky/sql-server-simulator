using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ATN2(y, x)</c>: returns the angle (in radians) whose tangent is
/// <c>y / x</c>, taking the quadrant of <c>(x, y)</c> into account. Result
/// is always <see cref="SqlType.Float"/> regardless of input categories —
/// probe-confirmed against SQL Server 2025 (2026-05-10). NULL on either
/// operand propagates as typed-NULL float.
/// </summary>
/// <remarks>
/// SQL Server's <c>ATN2(0, 0)</c> raises Msg 3623 ("An invalid floating
/// point operation occurred.") rather than returning 0 like .NET's
/// <c>Math.Atan2(0, 0)</c> does — probe-confirmed.
/// </remarks>
internal sealed class Atn2 : Expression
{
    private readonly Expression first;
    private readonly Expression second;

    public Atn2(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("atn2", 2);
        this.first = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("atn2", 2);
        context.MoveNextRequired();
        this.second = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("atn2", 2);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var y = MathScalars.CoerceImplicit(this.first.Run(runtime));
        if (y.IsNull) return SqlValue.Null(SqlType.Float);
        var x = MathScalars.CoerceImplicit(this.second.Run(runtime));
        if (x.IsNull) return SqlValue.Null(SqlType.Float);
        var yd = MathScalars.AsDouble(y);
        var xd = MathScalars.AsDouble(x);
        return yd == 0 && xd == 0
            ? throw SimulatedSqlException.InvalidFloatingPointOperation()
            : SqlValue.FromDouble(Math.Atan2(yd, xd));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => $"ATN2({this.first.DebugDisplay()}, {this.second.DebugDisplay()})";
}
