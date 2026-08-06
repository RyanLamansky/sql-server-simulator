namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's exact-numeric value: an unsigned scaled integer of up to 38
/// decimal digits, a sign, and the declared scale the value carries with it, so
/// <c>CAST(1 AS numeric(10, 2))</c> is <c>1.00</c> and <c>numeric(38, 30)</c>
/// holds its thirty fractional zeros.
/// </summary>
/// <remarks>
/// <para>
/// The type is rule-free about which <c>(precision, scale)</c> a result lands
/// on — <c>SqlType.PromoteForArithmetic</c> settles that — and rule-bound about
/// the digits: every operation takes the target precision and scale and reports
/// overflow by returning <see langword="false"/> rather than throwing, so the
/// caller raises the error its context is due.
/// </para>
/// <para>
/// The rounding split is SQL Server's, probe-confirmed against SQL Server 2025:
/// division truncates toward zero at the result scale, and every other operator
/// rounds half away from zero there — including addition and subtraction where
/// the 38-precision cap reduced the scale, and multiplication at every cap
/// depth.
/// </para>
/// <para>
/// 10^38 − 1 needs 127 bits, so every legal value fits a <see cref="UInt128"/>;
/// the intermediates don't, and reach <see cref="UInt256"/>. A zero is
/// normalized to non-negative at construction, matching real, which has no
/// signed zero in the exact numerics.
/// </para>
/// </remarks>
internal readonly partial struct Decimal38 : IEquatable<Decimal38>, IComparable<Decimal38>
{
    /// <summary>The absolute value, scaled by 10^<see cref="Scale"/>.</summary>
    public readonly UInt128 Magnitude;

    /// <summary>Fractional digits the value carries, 0 to <see cref="MaxPrecision"/>.</summary>
    public readonly byte Scale;

    /// <summary>The sign, always <see langword="false"/> for a zero.</summary>
    public readonly bool IsNegative;

    /// <summary>Significant digits SQL Server's <c>decimal</c> / <c>numeric</c> carries.</summary>
    public const int MaxPrecision = 38;

    /// <summary>Characters the widest rendering needs: a sign, 38 digits and a point.</summary>
    public const int MaxFormattedLength = 41;

    public static Decimal38 Zero => default;

    public static readonly Decimal38 One = new(UInt128.One, isNegative: false, scale: 0);

    private Decimal38(UInt128 magnitude, bool isNegative, int scale)
    {
        this.Magnitude = magnitude;
        this.Scale = (byte)scale;
        this.IsNegative = isNegative && magnitude != UInt128.Zero;
    }

    /// <summary>
    /// The value <paramref name="magnitude"/> / 10^<paramref name="scale"/>,
    /// signed by <paramref name="isNegative"/>. The magnitude is the caller's to
    /// keep inside the declared precision — <see cref="TryRescale"/> is the
    /// checked path.
    /// </summary>
    public static Decimal38 FromParts(UInt128 magnitude, bool isNegative, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, MaxPrecision);
        return new(magnitude, isNegative, scale);
    }

    public bool IsZero => this.Magnitude == UInt128.Zero;

    /// <summary>−1, 0 or 1.</summary>
    public int Sign => this.Magnitude == UInt128.Zero ? 0 : this.IsNegative ? -1 : 1;

    /// <summary>The same value with its sign cleared.</summary>
    public Decimal38 Abs() => new(this.Magnitude, isNegative: false, this.Scale);

    /// <summary>The same value with its sign flipped; a zero stays non-negative.</summary>
    public Decimal38 Negate() => new(this.Magnitude, !this.IsNegative, this.Scale);

    /// <summary>
    /// The same number written with no trailing fractional zeros — the form
    /// equality and hashing settle on, so <c>1.00</c> and <c>1</c> share one
    /// hash code.
    /// </summary>
    public Decimal38 Canonicalize()
    {
        var magnitude = this.Magnitude;
        var scale = this.Scale;
        if (magnitude == UInt128.Zero)
            return new(UInt128.Zero, isNegative: false, 0);

        while (scale > 0 && magnitude % 10 == UInt128.Zero)
        {
            magnitude /= 10;
            scale--;
        }

        return new(magnitude, this.IsNegative, scale);
    }

    /// <summary>
    /// Decimal digits the magnitude occupies — the precision the value needs,
    /// with a zero needing one.
    /// </summary>
    public int SignificantDigits()
    {
        var magnitude = this.Magnitude;
        var digits = 1;
        while (digits < Pow10.Length && magnitude >= Pow10[digits])
            digits++;
        return digits;
    }

    public int CompareTo(Decimal38 other)
    {
        if (this.IsNegative != other.IsNegative)
            return this.IsNegative ? -1 : 1;

        var comparison = CompareMagnitudes(this, other);
        return this.IsNegative ? -comparison : comparison;
    }

    private static int CompareMagnitudes(in Decimal38 left, in Decimal38 right)
    {
        if (left.Scale == right.Scale)
            return left.Magnitude.CompareTo(right.Magnitude);

        // Aligning two 38-digit values can need 76 digits, so the comparison
        // runs at 256 bits rather than truncating one side to fit.
        var common = Math.Max(left.Scale, right.Scale);
        var aligned = UInt256.Multiply(left.Magnitude, Pow10[common - left.Scale]);
        var other = UInt256.Multiply(right.Magnitude, Pow10[common - right.Scale]);
        return aligned.CompareTo(other);
    }

    /// <summary>
    /// Numeric equality, so the scale is invisible to it: <c>1.00</c> equals
    /// <c>1</c>, as SQL Server's own comparison, <c>GROUP BY</c> keys and
    /// <c>DISTINCT</c> treat them.
    /// </summary>
    public bool Equals(Decimal38 other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is Decimal38 other && Equals(other);

    /// <summary>
    /// Keyed on the canonical form, so numerically equal values of different
    /// declared scales agree.
    /// </summary>
    public override int GetHashCode()
    {
        var canonical = Canonicalize();
        return HashCode.Combine(canonical.Magnitude, canonical.IsNegative);
    }

    public static bool operator ==(Decimal38 left, Decimal38 right) => left.Equals(right);

    public static bool operator !=(Decimal38 left, Decimal38 right) => !left.Equals(right);

    public static bool operator <(Decimal38 left, Decimal38 right) => left.CompareTo(right) < 0;

    public static bool operator >(Decimal38 left, Decimal38 right) => left.CompareTo(right) > 0;

    public static bool operator <=(Decimal38 left, Decimal38 right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Decimal38 left, Decimal38 right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// The digits and the point, culture-free, carrying every fractional zero
    /// the declared scale calls for — real's own rendering, where
    /// <c>CAST(1 AS decimal(38, 30))</c> writes thirty of them.
    /// </summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        _ = TryFormat(buffer, out var written);
        return new(buffer[..written]);
    }

    /// <summary>
    /// Writes <see cref="ToString"/>'s rendering into
    /// <paramref name="destination"/>, false when it holds fewer than the
    /// needed characters.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        Span<char> digits = stackalloc char[MaxPrecision + 1];
        var next = digits.Length;
        var magnitude = this.Magnitude;
        do
        {
            digits[--next] = (char)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        while (magnitude != UInt128.Zero);

        // A value below one still writes its leading zero, and the point always
        // has the scale's worth of digits after it.
        var written = digits.Length - next;
        var integerDigits = written - this.Scale;
        var fractionPadding = integerDigits < 0 ? -integerDigits : 0;
        var needed = (this.IsNegative ? 1 : 0)
            + (integerDigits > 0 ? integerDigits : 1)
            + (this.Scale > 0 ? this.Scale + 1 : 0);
        if (destination.Length < needed)
        {
            charsWritten = 0;
            return false;
        }

        var at = 0;
        if (this.IsNegative)
            destination[at++] = '-';

        if (integerDigits > 0)
        {
            digits.Slice(next, integerDigits).CopyTo(destination[at..]);
            at += integerDigits;
        }
        else
        {
            destination[at++] = '0';
        }

        if (this.Scale > 0)
        {
            destination[at++] = '.';
            for (var i = 0; i < fractionPadding; i++)
                destination[at++] = '0';
            var fractionStart = next + Math.Max(0, integerDigits);
            var fractionCount = digits.Length - fractionStart;
            digits.Slice(fractionStart, fractionCount).CopyTo(destination[at..]);
            at += fractionCount;
        }

        charsWritten = at;
        return true;
    }
}
