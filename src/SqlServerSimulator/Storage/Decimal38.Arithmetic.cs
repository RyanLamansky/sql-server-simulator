namespace SqlServerSimulator.Storage;

/// <summary>
/// The five operators, the two rescaling forms they and <c>CAST</c> share, and
/// the power-of-ten plumbing under both.
/// </summary>
internal readonly partial struct Decimal38
{
    /// <summary>10^n for 0 ≤ n ≤ 38 — every power a legal scale can call for.</summary>
    internal static readonly UInt128[] Pow10 = BuildPow10();

    /// <summary>
    /// 10^n for 0 ≤ n ≤ 19, the widest powers a <see cref="ulong"/> holds. A
    /// reduction by a larger power runs as a chain of these, which is what keeps
    /// every division by a power of ten on the cheap 64-bit divisor path.
    /// </summary>
    private static readonly ulong[] Pow10UInt64 = BuildPow10UInt64();

    private const int Pow10UInt64Max = 19;

    /// <summary>
    /// <paramref name="left"/> + <paramref name="right"/> at
    /// <paramref name="scale"/>, rounded half away from zero where the result
    /// scale is narrower than the operands' — the shape a 38-precision cap
    /// produces, and probe-confirmed to round rather than truncate. False when
    /// the result needs more than <paramref name="precision"/> digits.
    /// </summary>
    public static bool TryAdd(in Decimal38 left, in Decimal38 right, int precision, int scale, out Decimal38 result) =>
        TryAddSigned(left, right, right.IsNegative, precision, scale, out result);

    /// <summary><paramref name="left"/> − <paramref name="right"/>, otherwise <see cref="TryAdd"/>.</summary>
    public static bool TrySubtract(in Decimal38 left, in Decimal38 right, int precision, int scale, out Decimal38 result) =>
        TryAddSigned(left, right, !right.IsNegative, precision, scale, out result);

    private static bool TryAddSigned(in Decimal38 left, in Decimal38 right, bool rightNegative, int precision, int scale, out Decimal38 result)
    {
        var common = Math.Max(left.Scale, right.Scale);
        if (left.Scale == right.Scale && TryAddNarrow(left, right, rightNegative, out var narrow, out var narrowNegative))
            return TryFinishNarrow(narrow, narrowNegative, common, precision, scale, rounded: true, out result);

        var leftAligned = UInt256.Multiply(left.Magnitude, Pow10[common - left.Scale]);
        var rightAligned = UInt256.Multiply(right.Magnitude, Pow10[common - right.Scale]);

        UInt256 magnitude;
        bool negative;
        if (left.IsNegative == rightNegative)
        {
            magnitude = UInt256.Add(leftAligned, rightAligned);
            negative = left.IsNegative;
        }
        else if (leftAligned >= rightAligned)
        {
            magnitude = UInt256.Subtract(leftAligned, rightAligned);
            negative = left.IsNegative;
        }
        else
        {
            magnitude = UInt256.Subtract(rightAligned, leftAligned);
            negative = rightNegative;
        }

        return TryFinish(magnitude, negative, common, precision, scale, rounded: true, out result);
    }

    /// <summary>
    /// The equal-scale sum inside 128 bits — two magnitudes below 10^38 add to
    /// below 2×10^38, which a <see cref="UInt128"/> holds, so the common case
    /// never forms a 256-bit intermediate.
    /// </summary>
    private static bool TryAddNarrow(in Decimal38 left, in Decimal38 right, bool rightNegative, out UInt128 magnitude, out bool negative)
    {
        if (left.IsNegative == rightNegative)
        {
            magnitude = left.Magnitude + right.Magnitude;
            negative = left.IsNegative;
            return magnitude >= left.Magnitude;
        }

        if (left.Magnitude >= right.Magnitude)
        {
            magnitude = left.Magnitude - right.Magnitude;
            negative = left.IsNegative;
        }
        else
        {
            magnitude = right.Magnitude - left.Magnitude;
            negative = rightNegative;
        }

        return true;
    }

    /// <summary>
    /// <paramref name="left"/> × <paramref name="right"/> at
    /// <paramref name="scale"/>, rounding half away from zero at the cut.
    /// </summary>
    public static bool TryMultiply(in Decimal38 left, in Decimal38 right, int precision, int scale, out Decimal38 result)
    {
        var productScale = left.Scale + right.Scale;
        if (left.Magnitude <= ulong.MaxValue && right.Magnitude <= ulong.MaxValue)
        {
            // Two 19-digit factors multiply inside 128 bits, which covers every
            // value narrower than a bigint and so the overwhelming majority.
            var narrow = left.Magnitude * right.Magnitude;
            return TryFinishNarrow(narrow, left.IsNegative ^ right.IsNegative, productScale, precision, scale, rounded: true, out result);
        }

        var magnitude = UInt256.Multiply(left.Magnitude, right.Magnitude);
        return TryFinish(magnitude, left.IsNegative ^ right.IsNegative, productScale, precision, scale, rounded: true, out result);
    }

    /// <summary>
    /// <paramref name="left"/> ÷ <paramref name="right"/> at
    /// <paramref name="scale"/>, <b>truncated toward zero</b> — the one operator
    /// that drops the digits past the result scale rather than rounding them,
    /// at every cap depth and for either sign.
    /// </summary>
    /// <remarks>
    /// The dividend is scaled up before the division rather than digits being
    /// dropped after it, so the kept digits are the exact quotient's:
    /// <c>CAST(2 AS decimal(38, 28)) / 3</c> is real's twenty-eight sixes, where
    /// a rounded intermediate would truncate to a seven.
    /// <paramref name="right"/> must be non-zero; the caller raises real's
    /// Msg 8134.
    /// </remarks>
    public static bool TryDivide(in Decimal38 left, in Decimal38 right, int precision, int scale, out Decimal38 result)
    {
        if (right.IsZero)
            throw new DivideByZeroException("A Decimal38 division by zero reached the arithmetic core; the caller raises Msg 8134.");

        var negative = left.IsNegative ^ right.IsNegative;
        var shift = scale + right.Scale - left.Scale;
        if (shift < 0)
        {
            // The divisor grows instead of the dividend. Past 128 bits it
            // exceeds every legal dividend, so the quotient is zero.
            if (!TryScaleUpWide(right.Magnitude, -shift, out var widened) || !widened.FitsUInt128)
            {
                result = new(UInt128.Zero, isNegative: false, scale);
                return true;
            }

            var narrowQuotient = left.Magnitude / widened.Low;
            return TryPackNarrow(narrowQuotient, negative, precision, scale, out result);
        }

        if (shift <= Pow10UInt64Max && left.Magnitude <= ulong.MaxValue)
        {
            // A 19-digit dividend scaled by up to 10^19 stays inside 128 bits.
            var narrow = left.Magnitude * Pow10[shift];
            return TryPackNarrow(narrow / right.Magnitude, negative, precision, scale, out result);
        }

        if (!TryScaleUpWide(left.Magnitude, shift, out var dividend))
        {
            // Past 256 bits the quotient exceeds 10^38 whatever the divisor,
            // since no divisor reaches 10^38.
            result = default;
            return false;
        }

        var quotient = UInt256.DivRem(dividend, right.Magnitude, out _);
        return TryPack(quotient, negative, precision, scale, out result);
    }

    /// <summary>
    /// <paramref name="left"/> % <paramref name="right"/> at
    /// <paramref name="scale"/> — the remainder, whose sign follows the
    /// dividend for either operand's sign.
    /// </summary>
    /// <remarks><paramref name="right"/> must be non-zero; the caller raises real's Msg 8134.</remarks>
    public static bool TryModulo(in Decimal38 left, in Decimal38 right, int precision, int scale, out Decimal38 result)
    {
        if (right.IsZero)
            throw new DivideByZeroException("A Decimal38 modulo by zero reached the arithmetic core; the caller raises Msg 8134.");

        var common = Math.Max(left.Scale, right.Scale);
        if (left.Scale == right.Scale)
        {
            var narrow = left.Magnitude % right.Magnitude;
            return TryFinishNarrow(narrow, left.IsNegative, common, precision, scale, rounded: true, out result);
        }

        var dividend = UInt256.Multiply(left.Magnitude, Pow10[common - left.Scale]);
        var divisor = UInt256.Multiply(right.Magnitude, Pow10[common - right.Scale]);

        // Only one side can need aligning, so whenever the divisor is the
        // smaller of the two it is also inside 128 bits.
        UInt256 remainder;
        if (divisor > dividend)
        {
            remainder = dividend;
        }
        else
        {
            _ = UInt256.DivRem(dividend, divisor.Low, out var value);
            remainder = value;
        }

        return TryFinish(remainder, left.IsNegative, common, precision, scale, rounded: true, out result);
    }

    /// <summary>
    /// <paramref name="value"/> restated at <paramref name="scale"/> — rounding
    /// half away from zero where that narrows the scale, padding with zeros
    /// where it widens it. The <c>CAST</c> / coercion workhorse.
    /// </summary>
    public static bool TryRescale(in Decimal38 value, int precision, int scale, out Decimal38 result) =>
        TryFinishNarrow(value.Magnitude, value.IsNegative, value.Scale, precision, scale, rounded: true, out result);

    /// <summary>
    /// <paramref name="value"/> restated at <paramref name="scale"/>, dropping
    /// the digits past it toward zero — the form <c>ROUND(x, n, 1)</c> and
    /// division's own cut take.
    /// </summary>
    public static bool TryTruncate(in Decimal38 value, int precision, int scale, out Decimal38 result) =>
        TryFinishNarrow(value.Magnitude, value.IsNegative, value.Scale, precision, scale, rounded: false, out result);

    private static bool TryFinish(UInt256 magnitude, bool negative, int currentScale, int precision, int scale, bool rounded, out Decimal38 result)
    {
        if (scale < currentScale)
        {
            magnitude = rounded
                ? ReduceRounded(magnitude, currentScale - scale)
                : DivideByPow10(magnitude, currentScale - scale);
        }
        else if (scale > currentScale && !TryScaleUpWide(magnitude, scale - currentScale, out magnitude))
        {
            result = default;
            return false;
        }

        return TryPack(magnitude, negative, precision, scale, out result);
    }

    private static bool TryFinishNarrow(UInt128 magnitude, bool negative, int currentScale, int precision, int scale, bool rounded, out Decimal38 result)
    {
        if (scale < currentScale)
        {
            magnitude = rounded
                ? ReduceRounded(magnitude, currentScale - scale)
                : DivideByPow10(magnitude, currentScale - scale);
        }
        else if (scale > currentScale)
        {
            if (!TryScaleUpWide(magnitude, scale - currentScale, out var widened))
            {
                result = default;
                return false;
            }

            return TryPack(widened, negative, precision, scale, out result);
        }

        return TryPackNarrow(magnitude, negative, precision, scale, out result);
    }

    private static bool TryPack(UInt256 magnitude, bool negative, int precision, int scale, out Decimal38 result)
    {
        if (!magnitude.FitsUInt128)
        {
            result = default;
            return false;
        }

        return TryPackNarrow(magnitude.Low, negative, precision, scale, out result);
    }

    private static bool TryPackNarrow(UInt128 magnitude, bool negative, int precision, int scale, out Decimal38 result)
    {
        if (magnitude >= Pow10[precision])
        {
            result = default;
            return false;
        }

        result = new(magnitude, negative, scale);
        return true;
    }

    /// <summary>
    /// <paramref name="value"/> ÷ 10^<paramref name="exponent"/>, rounded half
    /// away from zero. The decision is the first dropped digit alone — a five
    /// there rounds away whatever follows it, and anything smaller rounds toward
    /// zero — so no remainder wider than a digit ever has to be formed.
    /// </summary>
    private static UInt256 ReduceRounded(UInt256 value, int exponent)
    {
        if (exponent <= 0)
            return value;

        var quotient = UInt256.DivRem(DivideByPow10(value, exponent - 1), 10UL, out var digit);
        return digit >= 5 ? UInt256.Add(quotient, UInt256.One) : quotient;
    }

    private static UInt128 ReduceRounded(UInt128 value, int exponent)
    {
        if (exponent <= 0)
            return value;

        var quotient = DivideByPow10(value, exponent - 1);
        var digit = (uint)(quotient % 10);
        quotient /= 10;
        return digit >= 5 ? quotient + UInt128.One : quotient;
    }

    private static UInt256 DivideByPow10(UInt256 value, int exponent)
    {
        while (exponent > 0)
        {
            var step = Math.Min(exponent, Pow10UInt64Max);
            value = UInt256.DivRem(value, Pow10UInt64[step], out _);
            exponent -= step;
        }

        return value;
    }

    private static UInt128 DivideByPow10(UInt128 value, int exponent)
    {
        while (exponent > 0)
        {
            var step = Math.Min(exponent, Pow10UInt64Max);
            value /= Pow10UInt64[step];
            exponent -= step;
        }

        return value;
    }

    /// <summary>
    /// <paramref name="value"/> × 10^<paramref name="exponent"/>, false when the
    /// product needs more than 256 bits.
    /// </summary>
    private static bool TryScaleUpWide(UInt128 value, int exponent, out UInt256 result)
    {
        if (exponent <= MaxPrecision)
        {
            result = UInt256.Multiply(value, Pow10[exponent]);
            return true;
        }

        result = UInt256.Multiply(value, Pow10[MaxPrecision]);
        return TryScaleUpWide(result, exponent - MaxPrecision, out result);
    }

    private static bool TryScaleUpWide(UInt256 value, int exponent, out UInt256 result)
    {
        result = value;
        while (exponent > 0)
        {
            var step = Math.Min(exponent, Pow10UInt64Max);
            if (!UInt256.TryMultiply(result, Pow10UInt64[step], out result))
                return false;
            exponent -= step;
        }

        return true;
    }

    private static bool TryScaleUpNarrow(UInt128 value, int exponent, out UInt128 result)
    {
        if (exponent > MaxPrecision)
        {
            result = default;
            return value == UInt128.Zero;
        }

        var widened = UInt256.Multiply(value, Pow10[exponent]);
        result = widened.Low;
        return widened.FitsUInt128;
    }

    private static UInt128[] BuildPow10()
    {
        var table = new UInt128[MaxPrecision + 1];
        table[0] = UInt128.One;
        for (var i = 1; i < table.Length; i++)
            table[i] = table[i - 1] * 10;
        return table;
    }

    private static ulong[] BuildPow10UInt64()
    {
        var table = new ulong[Pow10UInt64Max + 1];
        table[0] = 1;
        for (var i = 1; i < table.Length; i++)
            table[i] = table[i - 1] * 10;
        return table;
    }
}
