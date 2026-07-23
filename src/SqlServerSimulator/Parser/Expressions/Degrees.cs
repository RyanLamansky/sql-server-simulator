using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DEGREES(numeric)</c>: converts radians to degrees by multiplying
/// by <c>180/pi</c>. Result type is type-preserving with one tweak relative
/// to the shared math-scalar widening: <c>decimal(p, s)</c> widens to
/// <c>decimal(38, max(s, 18))</c> rather than preserving — probe-confirmed
/// against SQL Server 2025 (2026-05-10). All other categories follow
/// <see cref="MathScalars.WidenForResult"/>: tinyint / smallint / int → int,
/// bigint → bigint, real / bit → float, smallmoney → money, money / float
/// preserved.
/// </summary>
/// <remarks>
/// The integer arm truncates toward zero after the float multiplication
/// (<c>DEGREES(360)</c> → <c>20626</c> from <c>20626.48...</c>). Out-of-range
/// integer results raise Msg 8115 with the result type's family name —
/// e.g. <c>DEGREES(2147483646)</c> overflows the int target.
/// </remarks>
internal sealed class Degrees(ParserContext context) : Expression
{
    private readonly Expression source = ParseSingleArgument(context, "degrees");

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.source.Run(runtime));
        var resultType = ResolveResultType(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => DegreesRadians.IntegerArm(MathScalars.AsLong(v), DegreesRadians.RadiansToDegreesDouble, resultType),
            SqlTypeCategory.Decimal => DegreesRadians.DecimalArm(MathScalars.AsDecimalOrMoney(v), numerator: 180m, denominator: DegreesRadians.DecimalPi, resultType),
            SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, MathScalars.AsDecimalOrMoney(v) * 180m / DegreesRadians.DecimalPi),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(MathScalars.AsDouble(v) * DegreesRadians.RadiansToDegreesDouble),
            _ => throw new NotSupportedException($"DEGREES doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => ResolveResultType(this.source.GetSqlType(batch, resolveColumnType));

    internal override bool ResultReportsNumeric => this.source.ResultReportsNumeric;

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

    internal override string DebugDisplay() => $"DEGREES({this.source.DebugDisplay()})";
}
