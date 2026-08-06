using System.Globalization;

namespace SqlServerSimulator.Storage;

/// <summary>
/// How a value crosses in and out of the type: the .NET numeric primitives at
/// the edges, and the string grammar SQL Server's own <c>varchar</c> →
/// <c>numeric</c> conversion accepts.
/// </summary>
internal readonly partial struct Decimal38
{
    /// <summary>2^96 − 1, the widest mantissa a .NET <see cref="decimal"/> holds.</summary>
    private static readonly UInt128 MaxDotNetDecimalMantissa = ((UInt128)uint.MaxValue << 64) | ulong.MaxValue;

    /// <summary>Fractional digits a .NET <see cref="decimal"/> carries.</summary>
    private const int MaxDotNetDecimalScale = 28;

    public static Decimal38 FromInt32(int value) => FromInt64(value);

    public static Decimal38 FromInt64(long value)
    {
        if (value >= 0)
            return new((ulong)value, isNegative: false, 0);

        // long.MinValue has no positive counterpart, so the magnitude comes
        // from the complement rather than from negating.
        var magnitude = (UInt128)(ulong)~value + UInt128.One;
        return new(magnitude, isNegative: true, 0);
    }

    public static Decimal38 FromUInt64(ulong value) => new(value, isNegative: false, 0);

    /// <summary>
    /// A .NET <see cref="decimal"/>, exactly — a 96-bit mantissa at up to 28
    /// fractional digits is inside this type's range whatever its value.
    /// </summary>
    public static Decimal38 FromDotNetDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        var magnitude = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        var flags = bits[3];
        return new(magnitude, (flags & int.MinValue) != 0, (flags >> 16) & 0xFF);
    }

    /// <summary>
    /// The value as a .NET <see cref="decimal"/>, shedding trailing fractional
    /// zeros where that is what it takes to fit — the conversion SqlClient
    /// performs at the client boundary, where
    /// <c>CAST(1 AS decimal(38, 30))</c> reaches <c>GetDecimal</c> as
    /// <c>1.0000000000000000000000000000</c> at scale 28 rather than raising.
    /// False when the significant digits alone don't fit, which is where
    /// SqlClient raises <see cref="OverflowException"/>.
    /// </summary>
    public static bool TryToDotNetDecimal(in Decimal38 value, out decimal result)
    {
        var magnitude = value.Magnitude;
        var scale = (int)value.Scale;
        while (scale > 0
            && (scale > MaxDotNetDecimalScale || magnitude > MaxDotNetDecimalMantissa)
            && magnitude % 10 == UInt128.Zero)
        {
            magnitude /= 10;
            scale--;
        }

        if (scale > MaxDotNetDecimalScale || magnitude > MaxDotNetDecimalMantissa)
        {
            result = 0m;
            return false;
        }

        result = new((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), value.IsNegative, (byte)scale);
        return true;
    }

    /// <summary>
    /// The integral part, truncated toward zero. False when it doesn't fit an
    /// <see cref="long"/>.
    /// </summary>
    public static bool TryToInt64(in Decimal38 value, out long result)
    {
        var integral = DivideByPow10(value.Magnitude, value.Scale);
        if (integral > (value.IsNegative ? (UInt128)long.MaxValue + 1 : (UInt128)long.MaxValue))
        {
            result = 0;
            return false;
        }

        result = value.IsNegative ? unchecked(-(long)integral) : (long)integral;
        return true;
    }

    /// <summary>
    /// A <see cref="double"/> converted the way SQL Server's <c>float</c> →
    /// <c>decimal</c> conversion does: from the operand's <b>exact</b> binary
    /// value, rounded half away from zero at <paramref name="scale"/>. So
    /// <c>CAST(CAST(1e30 AS float) AS decimal(38, 0))</c> is real's
    /// <c>1000000000000000019884624838656</c> rather than a 17-digit
    /// approximation, and <c>CAST(2.5 AS float)</c> at scale 0 is 3 rather than
    /// the even 2. False for NaN, an infinity, or a magnitude that doesn't fit.
    /// </summary>
    public static bool TryFromDouble(double value, int precision, int scale, out Decimal38 result)
    {
        result = default;
        if (double.IsNaN(value) || double.IsInfinity(value))
            return false;

        var negative = double.IsNegative(value);
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponentField = (int)((bits >> 52) & 0x7FF);
        var mantissaField = bits & 0xF_FFFF_FFFF_FFFF;
        var mantissa = exponentField == 0 ? (ulong)mantissaField : (ulong)mantissaField | (1UL << 52);
        var exponent = (exponentField == 0 ? 1 : exponentField) - 1075;

        UInt256 scaled;
        if (exponent >= 0)
        {
            // An integral double: the scaled value is exact, no rounding due.
            // A 53-bit mantissa shifted past 75 already exceeds 10^38, so the
            // shift itself never has to leave 128 bits.
            if (exponent > 75)
                return false;

            if (!TryScaleUpWide((UInt128)mantissa << exponent, scale, out scaled))
                return false;
        }
        else
        {
            // value = mantissa / 2^-exponent, so the scaled numerator divides by
            // a power of two — a shift, with the bit below the cut deciding the
            // half-away rounding.
            var numerator = UInt256.Multiply(mantissa, Pow10[scale]);
            var shift = -exponent;
            scaled = numerator.ShiftRight(shift);
            if (numerator.IsBitSet(shift - 1))
                scaled = UInt256.Add(scaled, UInt256.One);
        }

        return TryPack(scaled, negative, precision, scale, out result);
    }

    /// <summary>
    /// The value as a <see cref="double"/>, correctly rounded — the digits are
    /// rendered and re-read rather than divided by a power of ten in binary,
    /// which would round twice.
    /// </summary>
    public double ToDouble()
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        _ = TryFormat(buffer, out var written);
        return double.Parse(buffer[..written], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// SQL Server's <c>varchar</c> → <c>numeric</c> grammar: optional
    /// surrounding whitespace, an optional single sign, digits with at most one
    /// decimal point, and nothing else — no exponent, no grouping separator, no
    /// currency symbol, all of which real refuses with Msg 8114
    /// (probe-confirmed, <c>'1e5'</c> included).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excess fractional digits round half away from zero at
    /// <paramref name="scale"/> — <c>CAST('1.005' AS decimal(5, 2))</c> is
    /// <c>1.01</c> and <c>CAST('1.004999' AS decimal(5, 2))</c> is <c>1.00</c> —
    /// and the digit count is judged after that rounding, so a string carrying
    /// more than 38 significant digits still converts when the rounded value
    /// fits (<c>CAST('1.0000000000000000000000000000000000000005' AS
    /// decimal(38, 2))</c> is <c>1.00</c>).
    /// </para>
    /// <para>
    /// Which of the two overflow outcomes a failure carries is settled by the
    /// <b>text's own</b> natural precision — its integer digits past any leading
    /// zeros plus every fractional digit written — rather than by the rounded
    /// value's width. Text wider than 38 digits reports
    /// <see cref="Decimal38ParseOutcome.ExceedsNumericDomain"/> whatever the
    /// target (<c>CAST('123456789012345678901234567890123456789' AS
    /// decimal(10, 0))</c>), and text inside 38 digits reports
    /// <see cref="Decimal38ParseOutcome.ExceedsDeclaredPrecision"/> even where
    /// the rescaled value needs 39 (<c>CAST('1.005' AS decimal(38, 38))</c>) —
    /// both probe-confirmed.
    /// </para>
    /// </remarks>
    public static Decimal38ParseOutcome TryParse(ReadOnlySpan<char> text, int precision, int scale, out Decimal38 result)
    {
        result = default;
        var trimmed = text.Trim();
        if (trimmed.IsEmpty)
            return Decimal38ParseOutcome.Malformed;

        var negative = false;
        if (trimmed[0] is '+' or '-')
        {
            negative = trimmed[0] == '-';
            trimmed = trimmed[1..];
        }

        var point = trimmed.IndexOf('.');
        var integerText = point < 0 ? trimmed : trimmed[..point];
        var fractionText = point < 0 ? trimmed[trimmed.Length..] : trimmed[(point + 1)..];
        if (integerText.Length + fractionText.Length == 0 || !IsAllDigits(integerText) || !IsAllDigits(fractionText))
            return Decimal38ParseOutcome.Malformed;

        // Accumulate the integer digits and the scale's worth of fractional
        // ones, then let the first dropped digit settle the rounding. Anything
        // past 39 significant digits can only overflow, so accumulation stops
        // there rather than needing an unbounded intermediate.
        UInt128 magnitude = 0;
        var significant = 0;
        var overflowed = false;
        foreach (var digit in integerText)
            Accumulate(digit, ref magnitude, ref significant, ref overflowed);
        for (var i = 0; i < scale; i++)
            Accumulate(i < fractionText.Length ? fractionText[i] : '0', ref magnitude, ref significant, ref overflowed);

        if (scale < fractionText.Length && fractionText[scale] >= '5')
        {
            magnitude++;
            if (magnitude >= Pow10[MaxPrecision])
                overflowed = true;
        }

        if (overflowed || magnitude >= Pow10[precision])
        {
            var leadingZeros = 0;
            while (leadingZeros < integerText.Length && integerText[leadingZeros] == '0')
                leadingZeros++;
            var naturalPrecision = integerText.Length - leadingZeros + fractionText.Length;
            return naturalPrecision > MaxPrecision
                ? Decimal38ParseOutcome.ExceedsNumericDomain
                : Decimal38ParseOutcome.ExceedsDeclaredPrecision;
        }

        result = new(magnitude, negative, scale);
        return Decimal38ParseOutcome.Success;
    }

    private static void Accumulate(char digit, ref UInt128 magnitude, ref int significant, ref bool overflowed)
    {
        var value = (uint)(digit - '0');
        if (significant == 0 && value == 0)
            return;

        if (significant >= MaxPrecision)
        {
            overflowed = true;
            return;
        }

        magnitude = (magnitude * 10) + value;
        significant++;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }
}

/// <summary>
/// Why a string didn't convert, keyed to the error SQL Server reports for it —
/// probe-confirmed against SQL Server 2025.
/// </summary>
internal enum Decimal38ParseOutcome : byte
{
    /// <summary>The text converted.</summary>
    Success,

    /// <summary>The text isn't a number at all — real's Msg 8114 at state 5.</summary>
    Malformed,

    /// <summary>
    /// The value doesn't fit, and the text that carried it was itself wider
    /// than <c>numeric</c>'s own 38-digit domain — real's Msg 8115 at state 6.
    /// </summary>
    ExceedsNumericDomain,

    /// <summary>
    /// The value doesn't fit, and the text that carried it was inside 38
    /// digits — real's Msg 8115 at state 8.
    /// </summary>
    ExceedsDeclaredPrecision,
}
