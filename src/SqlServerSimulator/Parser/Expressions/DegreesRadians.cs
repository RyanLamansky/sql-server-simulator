using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared math helpers for <see cref="Degrees"/> and <see cref="Radians"/>.
/// Both functions multiply the input by a constant ratio of 180 to pi,
/// just with the numerator and denominator swapped. The result-type rule
/// is type-preserving across most categories with the decimal arm using
/// <c>decimal(38, max(s, 18))</c> instead of preserving — that decision
/// lives in each call site (per-function), but the actual integer-arm and
/// exact-numeric-arm computations are factored here.
/// </summary>
/// <remarks>
/// Every arm computes in <see cref="double"/> against a single pre-multiplied
/// ratio, which is what real does whatever the argument's family — the
/// exact-numeric result is the double's own exact expansion rounded at the
/// result's scale, so <c>DEGREES(CAST(1.5 AS decimal(10, 2)))</c> is
/// <c>85.943669269623484297</c> and the same call at
/// <c>decimal(38, 30)</c> is <c>85.943669269623484296971582807600</c>, the
/// binary fraction running out before the digits do. Multiplying by the
/// ratio as one constant rather than by 180 and then dividing is load-bearing:
/// <c>DEGREES(CAST(33644737241.2066 AS decimal(18, 4)))</c> separates the two.
/// </remarks>
internal static class DegreesRadians
{
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
    /// Computes the exact-numeric arm — <c>decimal</c> / <c>numeric</c> and
    /// <c>money</c> / <c>smallmoney</c> alike: the operand crosses to
    /// <see cref="double"/>, meets <paramref name="multiplier"/> there, and the
    /// product's exact binary value comes back rounded half away from zero at
    /// <paramref name="resultType"/>'s declared scale. A magnitude the result
    /// type can't hold is real's Msg 8115 at state 2.
    /// </summary>
    public static SqlValue ExactNumericArm(in Decimal38 input, double multiplier, SqlType resultType) =>
        MathScalars.FromDoubleAsDecimalOrMoney(resultType, input.ToDouble() * multiplier);
}
