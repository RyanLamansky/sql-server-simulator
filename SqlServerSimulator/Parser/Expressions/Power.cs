using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>POWER(base, exponent)</c>: result type is the (post-widen) type
/// of <c>base</c> regardless of <c>exponent</c>'s type — probe-confirmed
/// against SQL Server 2025 (2026-05-09): <c>POWER(int, float) → int</c>
/// (with truncation toward zero) and <c>POWER(decimal, int) → decimal</c>.
/// Negative <c>base</c> with fractional <c>exponent</c> raises Msg 3623;
/// <c>POWER(0, negative)</c> raises Msg 8134 (divide by zero); int-result
/// overflow raises Msg 232.
/// </summary>
internal sealed class Power : Expression
{
    private readonly Expression baseExpr;
    private readonly Expression exponent;

    public Power(ParserContext context)
    {
        this.baseExpr = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.exponent = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var b = this.baseExpr.Run(runtime);
        var resultType = MathScalars.WidenForResult(b.Type);
        if (b.IsNull) return SqlValue.Null(resultType);
        var e = this.exponent.Run(runtime);
        if (e.IsNull) return SqlValue.Null(resultType);

        var bd = MathScalars.AsDouble(b);
        var ed = MathScalars.AsDouble(e);

        if (bd == 0 && ed < 0)
            throw SimulatedSqlException.DivideByZero();
        if (bd < 0 && ed != Math.Truncate(ed))
            throw SimulatedSqlException.InvalidFloatingPointOperation();

        var raw = Math.Pow(bd, ed);
        return resultType.Category switch
        {
            SqlTypeCategory.Approximate => double.IsInfinity(raw)
                ? throw SimulatedSqlException.ArithmeticOverflow("float")
                : SqlValue.FromDouble(raw),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, (decimal)raw),
            _ => CoerceIntegerResult(raw, resultType),
        };
    }

    private static SqlValue CoerceIntegerResult(double raw, SqlType resultType) => resultType == SqlType.BigInt
        ? raw is < long.MinValue or > long.MaxValue
            ? throw SimulatedSqlException.ArithmeticOverflowForType("bigint", raw.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
            : SqlValue.FromInt64((long)raw)
        : raw is < int.MinValue or > int.MaxValue
            ? throw SimulatedSqlException.ArithmeticOverflowForType("int", raw.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
            : SqlValue.FromInt32((int)raw);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.WidenForResult(this.baseExpr.GetSqlType(batch, resolveColumnType));

    internal override string DebugDisplay() => $"POWER({this.baseExpr.DebugDisplay()}, {this.exponent.DebugDisplay()})";
}
