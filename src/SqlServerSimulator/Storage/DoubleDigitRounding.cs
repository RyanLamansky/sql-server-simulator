namespace SqlServerSimulator.Storage;

/// <summary>
/// <see cref="Math.Round(double, int, MidpointRounding)"/> as .NET 10 computes
/// it, written out so the engine's answer doesn't move with the runtime: .NET
/// 10 scales by a power of ten, rounds to an integer and scales back, each
/// step rounding in <see cref="double"/>, while .NET 11 rounds the value
/// itself and so differs in the last place for about half of all inputs.
/// </summary>
internal static class DoubleDigitRounding
{
    /// <summary>10^0 through 10^15, the scales the rounding takes.</summary>
    private static readonly double[] PowersOfTen =
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
    ];

    /// <summary>
    /// <paramref name="value"/> rounded to <paramref name="digits"/> (0 through
    /// 15) fractional digits; a magnitude of 10^16 or more has none to round and
    /// comes back unchanged.
    /// </summary>
    public static double Round(double value, int digits, MidpointRounding mode)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)digits, (uint)(PowersOfTen.Length - 1), nameof(digits));
        if (!(Math.Abs(value) < 1e16))
            return value;
        var scale = PowersOfTen[digits];
        return Math.Round(value * scale, mode) / scale;
    }
}
