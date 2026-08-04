namespace SqlServerSimulator.Storage;

/// <summary>
/// The decimal-digit rules SQL Server's exact-numeric arithmetic settles a
/// value with. Every operator but division rounds half away from zero at the
/// result scale; <b>division truncates toward zero</b> there, and does so at
/// every cap depth rather than only where the 38-precision cap reduced the
/// scale — probe-confirmed against SQL Server 2025 (2026-08-04):
/// <c>CAST(4.00 AS decimal(5, 2)) / 7</c> (uncapped) and
/// <c>CAST(4.00 AS decimal(38, 2)) / 7</c> (capped) are both
/// <c>0.571428</c>, an exact half drops the same way
/// (<c>CAST(1 AS decimal(5, 0)) / 1600000</c> is <c>0.00000062</c>), and the
/// sign doesn't move the cut (<c>-4.00 / 7</c> is <c>-0.571428</c>).
/// <c>money</c> follows the same split — <c>$1.00 / 7</c> is <c>0.1428</c>
/// while <c>$1.0001 * $0.5555</c> rounds to <c>0.5556</c> — and
/// <c>AVG</c> inherits it, real computing it as <c>SUM</c> / <c>COUNT</c>.
/// </summary>
internal static class DecimalMath
{
    /// <summary>
    /// <paramref name="dividend"/> ÷ <paramref name="divisor"/>, truncated
    /// toward zero at <paramref name="scale"/> fractional digits — a decimal
    /// or money result type's own scale, so 0 to 38.
    /// </summary>
    /// <remarks>
    /// Scaling the dividend up front — rather than dividing first and dropping
    /// digits after — keeps the digits real keeps: .NET's own division rounds
    /// at its 28-significant-digit ceiling, which lands inside the kept digits
    /// once the result scale approaches 28 (<c>CAST(2 AS decimal(38, 28)) /
    /// 3</c> is real's <c>0.6666…6666</c>, where the rounded quotient would
    /// truncate to <c>…6667</c>). The pre-scaling runs only where it can't
    /// overflow: a divisor of magnitude 1 or more can't grow the quotient past
    /// the scaled dividend.
    /// </remarks>
    public static decimal Truncating(decimal dividend, decimal divisor, int scale)
    {
        if (scale < Pow10Table.Length
            && decimal.Abs(divisor) >= 1m
            && decimal.Abs(dividend) <= MaxScaledDividend[scale])
        {
            var pow = Pow10Table[scale];
            return decimal.Truncate(dividend * pow / divisor) / pow;
        }

        return TruncateToScale(dividend / divisor, scale);
    }

    /// <summary>
    /// Drops <paramref name="value"/>'s fractional digits past
    /// <paramref name="scale"/>, toward zero. A value already inside the scale
    /// comes back untouched — which covers every scale at or past .NET
    /// <see cref="decimal"/>'s own 28-fractional-digit ceiling, since no value
    /// can carry more than that.
    /// </summary>
    public static decimal TruncateToScale(decimal value, int scale)
    {
        if (value.Scale <= scale)
            return value;

        // Splitting the integral part off first keeps the shift inside
        // decimal's range whatever the magnitude: the fraction is below 1, so
        // scaling it by up to 10^28 can't overflow.
        var integral = decimal.Truncate(value);
        var pow = Pow10Table[scale];
        return integral + (decimal.Truncate((value - integral) * pow) / pow);
    }

    /// <summary>10^<paramref name="n"/> for 0 ≤ n ≤ 28 — .NET decimal's whole exponent range.</summary>
    public static decimal Pow10(int n) => Pow10Table[n];

    private static readonly decimal[] Pow10Table = BuildPow10();

    /// <summary>
    /// Per scale, the largest magnitude that survives being multiplied by
    /// 10^scale — the pre-scaling guard in <see cref="Truncating"/>.
    /// </summary>
    private static readonly decimal[] MaxScaledDividend = BuildMaxScaledDividend();

    private static decimal[] BuildPow10()
    {
        var table = new decimal[29];
        table[0] = 1m;
        for (var i = 1; i < table.Length; i++)
            table[i] = table[i - 1] * 10m;
        return table;
    }

    private static decimal[] BuildMaxScaledDividend()
    {
        var table = new decimal[Pow10Table.Length];
        for (var i = 0; i < table.Length; i++)
            table[i] = decimal.MaxValue / Pow10Table[i];
        return table;
    }
}
