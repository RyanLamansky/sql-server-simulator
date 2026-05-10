using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared math helpers for <see cref="Degrees"/> and <see cref="Radians"/>.
/// Both functions multiply the input by a constant ratio of 180 to pi,
/// just with the numerator and denominator swapped. The result-type rule
/// is type-preserving across most categories with the decimal arm using
/// <c>decimal(38, max(s, 18))</c> instead of preserving — that decision
/// lives in each call site (per-function), but the actual integer-arm and
/// decimal-arm computations are factored here.
/// </summary>
internal static class DegreesRadians
{
    /// <summary>
    /// 28-digit decimal pi constant, the maximum precision .NET's
    /// <see cref="decimal"/> can represent. Used so <c>(input * 180m) / pi</c>
    /// rounds to scale-18 with the same trailing digits as SQL Server emits
    /// (probe-confirmed: <c>degrees(cast(1.5 as decimal(10,2))) =
    /// 85.943669269623484297</c>).
    /// </summary>
    public const decimal DecimalPi = 3.1415926535897932384626433833m;

    public const double RadiansToDegreesDouble = 180.0 / Math.PI;

    public const double DegreesToRadiansDouble = Math.PI / 180.0;

    /// <summary>
    /// Computes the integer-arm result: <c>input * multiplier</c> truncated
    /// toward zero, range-checked against <paramref name="resultType"/>'s
    /// integer family. Produces Msg 8115 with the family name on overflow.
    /// </summary>
    public static SqlValue IntegerArm(long input, double multiplier, SqlType resultType)
    {
        var raw = input * multiplier;
        return resultType == SqlType.BigInt
            ? raw is < long.MinValue or > long.MaxValue
                ? throw SimulatedSqlException.ArithmeticOverflow("bigint")
                : SqlValue.FromInt64((long)raw)
            : raw is < int.MinValue or > int.MaxValue
                ? throw SimulatedSqlException.ArithmeticOverflow("int")
                : SqlValue.FromInt32((int)raw);
    }

    /// <summary>
    /// Computes the decimal-arm result: <c>(input * numerator) / denominator</c>
    /// rounded to <paramref name="resultType"/>'s declared scale using
    /// half-away-from-zero (matches SQL Server's decimal arithmetic
    /// rounding convention). Operation order matters for trailing-digit
    /// fidelity — <c>(input * 180m) / pi</c> gives a different last digit
    /// than <c>input * (180m / pi)</c>.
    /// </summary>
    public static SqlValue DecimalArm(decimal input, decimal numerator, decimal denominator, SqlType resultType)
    {
        var raw = input * numerator / denominator;
        // .NET decimal max scale is 28; declared SQL scale can run up to 38.
        // Cap the round target so the .NET runtime doesn't throw on scale > 28
        // (the result decimal's natural precision tops out at 28 anyway, so
        // capping doesn't lose any value information).
        var declaredScale = ((DecimalSqlType)resultType).scale;
        var roundScale = declaredScale > 28 ? 28 : declaredScale;
        var rounded = Math.Round(raw, roundScale, MidpointRounding.AwayFromZero);
        return SqlValue.FromDecimal(resultType, rounded);
    }
}
