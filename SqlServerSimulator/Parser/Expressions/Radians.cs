using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>RADIANS(numeric)</c>: converts degrees to radians by multiplying
/// by <c>pi/180</c>. Result-type rule is identical to <see cref="Degrees"/>:
/// <c>decimal(p, s)</c> widens to <c>decimal(38, max(s, 18))</c>; everything
/// else follows <see cref="MathScalars.WidenForResult"/>. Probe-confirmed
/// against SQL Server 2025 (2026-05-10).
/// </summary>
/// <remarks>
/// The integer arm truncates toward zero — <c>RADIANS(360)</c> → <c>6</c>
/// from <c>6.28...</c>, not <c>6.28</c> rounded. Most integer-domain calls
/// produce the same handful of small results; degenerate inputs aren't
/// common since one radian covers ~57 degrees.
/// </remarks>
internal sealed class Radians(ParserContext context) : Expression
{
    private readonly Expression source = ParseSingleArgument(context, "radians");

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        var v = this.source.Run(getColumnValue);
        var resultType = ResolveResultType(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => DegreesRadians.IntegerArm(MathScalars.AsLong(v), DegreesRadians.DegreesToRadiansDouble, resultType),
            SqlTypeCategory.Decimal => DegreesRadians.DecimalArm(MathScalars.AsDecimalOrMoney(v), numerator: DegreesRadians.DecimalPi, denominator: 180m, resultType),
            SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, MathScalars.AsDecimalOrMoney(v) * DegreesRadians.DecimalPi / 180m),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(MathScalars.AsDouble(v) * DegreesRadians.DegreesToRadiansDouble),
            _ => throw new NotSupportedException($"RADIANS doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
        => ResolveResultType(this.source.GetSqlType(resolveColumnType));

    private static SqlType ResolveResultType(SqlType input) =>
        input is DecimalSqlType d
            ? SqlType.GetDecimal(38, d.scale > 18 ? d.scale : 18)
            : MathScalars.WidenForResult(input);

    private static Expression ParseSingleArgument(ParserContext context, string lowerName)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments(lowerName, 1);
        var arg = Parse(context);
        return context.Token is Tokens.Operator { Character: ')' }
            ? arg
            : throw SimulatedSqlException.FunctionRequiresNArguments(lowerName, 1);
    }

    internal override string DebugDisplay() => $"RADIANS({this.source.DebugDisplay()})";
}
