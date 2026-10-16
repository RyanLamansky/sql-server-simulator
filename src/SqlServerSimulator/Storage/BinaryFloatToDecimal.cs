namespace SqlServerSimulator.Storage;

/// <summary>
/// The <see cref="double"/> → <see cref="decimal"/> and <see cref="float"/> →
/// <see cref="decimal"/> conversions .NET 10's casts perform, written out so
/// the engine's answer doesn't move with the runtime: the casts round to 15
/// (<see cref="double"/>) or 7 (<see cref="float"/>) significant digits
/// through double arithmetic on .NET 10, and convert exactly on .NET 11
/// (<c>(decimal)12.34</c> is <c>12.339999999999999857891452848</c> there).
/// </summary>
/// <remarks>
/// The rounding is not a correctly rounded 15-digit reading of the value —
/// scaling by a power of ten in <see cref="double"/> moves the last digit for
/// about one value in seventy, and the scale keeps a digit a rounding carry
/// adds — so this is the runtime's algorithm step for step rather than a
/// <c>G15</c> round trip, and it matches the .NET 10 cast bit for bit
/// (value and scale).
/// An exponent past 2^96 is an <see cref="OverflowException"/>, as the cast's
/// is, and NaN and the infinities overflow too.
/// </remarks>
internal static class BinaryFloatToDecimal
{
    /// <summary>The highest scale a <see cref="decimal"/> carries.</summary>
    private const int MaxScale = 28;

    /// <summary>10^0 through 10^28 as <see cref="double"/> literals, the scaling table the cast reads.</summary>
    private static readonly double[] DoublePowersOfTen =
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9,
        1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
        1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28,
    ];

    /// <summary><paramref name="value"/> rounded to 15 significant digits, as .NET 10's <c>(decimal)double</c> is.</summary>
    public static decimal FromDouble(double value)
    {
        // The most a decimal scales by is 10^28, just over 2^93, so an
        // exponent below -94 always rounds to zero.
        var exponent = (int)((BitConverter.DoubleToUInt64Bits(value) >> 52) & 0x7FF) - 1022;
        if (exponent < -94)
            return 0m;
        if (exponent > 96)
            throw new OverflowException("Value was either too large or too small for a Decimal.");

        var negative = value < 0;
        var scaled = Math.Abs(value);
        // log10(2) * 2^16 ≈ 19728.3 estimates the value's power of ten.
        var power = 14 - ((exponent * 19728) >> 16);
        if (power >= 0)
        {
            power = Math.Min(power, MaxScale);
            scaled *= DoublePowersOfTen[power];
        }
        else if (power != -1 || scaled >= 1e15)
        {
            scaled /= DoublePowersOfTen[-power];
        }
        else
        {
            power = 0;
        }

        if (scaled < 1e14 && power < MaxScale)
        {
            scaled *= 10;
            power++;
        }

        var mantissa = (ulong)(long)Math.Round(scaled);
        if (mantissa == 0)
            return 0m;
        if (power < 0)
            return Signed(mantissa * PowerOfTen(-power), negative);

        // Factor out the trailing zeros the scale allows, at most 14 of the
        // 15 digits.
        var removable = Math.Min(power, 14);
        foreach (var (digits, divisor) in (ReadOnlySpan<(int, ulong)>)[(8, 100_000_000UL), (4, 10_000UL), (2, 100UL), (1, 10UL)])
        {
            if (removable >= digits && mantissa % divisor == 0)
            {
                mantissa /= divisor;
                power -= digits;
                removable -= digits;
            }
        }
        return new decimal((int)(uint)mantissa, (int)(uint)(mantissa >> 32), 0, negative, (byte)power);
    }

    /// <summary><paramref name="value"/> rounded to 7 significant digits, as .NET 10's <c>(decimal)float</c> is.</summary>
    public static decimal FromSingle(float value)
    {
        var exponent = (int)((BitConverter.SingleToUInt32Bits(value) >> 23) & 0xFF) - 126;
        if (exponent < -94)
            return 0m;
        if (exponent > 96)
            throw new OverflowException("Value was either too large or too small for a Decimal.");

        var negative = value < 0;
        double scaled = Math.Abs(value);
        var power = 6 - ((exponent * 19728) >> 16);
        if (power >= 0)
        {
            power = Math.Min(power, MaxScale);
            scaled *= DoublePowersOfTen[power];
        }
        else if (power != -1 || scaled >= 1e7)
        {
            scaled /= DoublePowersOfTen[-power];
        }
        else
        {
            power = 0;
        }

        if (scaled < 1e6 && power < MaxScale)
        {
            scaled *= 10;
            power++;
        }

        var mantissa = (uint)(int)Math.Round(scaled);
        if (mantissa == 0)
            return 0m;
        if (power < 0)
            return Signed(mantissa * PowerOfTen(-power), negative);

        var removable = Math.Min(power, 6);
        foreach (var (digits, divisor) in (ReadOnlySpan<(int, uint)>)[(4, 10_000U), (2, 100U), (1, 10U)])
        {
            if (removable >= digits && mantissa % divisor == 0)
            {
                mantissa /= divisor;
                power -= digits;
                removable -= digits;
            }
        }
        return new decimal((int)mantissa, 0, 0, negative, (byte)power);
    }

    /// <summary>
    /// A parameter value as a <see cref="decimal"/>: a <see cref="double"/> or
    /// <see cref="float"/> through <see cref="FromDouble"/> / <see cref="FromSingle"/>,
    /// anything else through <see cref="Convert.ToDecimal(object, IFormatProvider)"/>.
    /// </summary>
    public static decimal FromParameter(object raw) => raw switch
    {
        double value => FromDouble(value),
        float value => FromSingle(value),
        _ => Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>10^<paramref name="power"/> as an integral <see cref="decimal"/>, for the scale-down products (exact, scale 0).</summary>
    private static decimal PowerOfTen(int power)
    {
        var result = 1m;
        for (var i = 0; i < power; i++)
            result *= 10;
        return result;
    }

    private static decimal Signed(decimal magnitude, bool negative) => negative ? -magnitude : magnitude;
}
