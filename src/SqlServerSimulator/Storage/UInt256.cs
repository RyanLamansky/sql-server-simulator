using System.Numerics;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A 256-bit unsigned integer, carrying the intermediates
/// <see cref="Decimal38"/>'s arithmetic reaches: the exact product of two
/// 38-digit magnitudes and the pre-scaled dividend a truncating division needs
/// are both about 10^76, which is past <see cref="UInt128"/>'s 3.4×10^38 and
/// inside this type's 1.1×10^77.
/// </summary>
/// <remarks>
/// Only the operations that intermediate demands are here — widening multiply,
/// add / subtract, shifts, division by a 128-bit divisor, and comparison. The
/// division is Knuth's algorithm D over 64-bit limbs when the divisor needs
/// more than 64 bits and a limb-at-a-time long division when it doesn't;
/// reductions by a power of ten route through the latter in ≤ 19-digit chunks,
/// so a power of ten wider than a <see cref="ulong"/> never has to be formed.
/// </remarks>
internal readonly struct UInt256(UInt128 high, UInt128 low) : IEquatable<UInt256>
{
    /// <summary>Bits 128-255.</summary>
    public readonly UInt128 High = high;

    /// <summary>Bits 0-127.</summary>
    public readonly UInt128 Low = low;

    public static UInt256 Zero => default;

    public static readonly UInt256 One = new(UInt128.Zero, UInt128.One);

    public bool IsZero => this.High == UInt128.Zero && this.Low == UInt128.Zero;

    /// <summary>True when the value is inside <see cref="UInt128"/>'s range.</summary>
    public bool FitsUInt128 => this.High == UInt128.Zero;

    public static implicit operator UInt256(UInt128 value) => new(UInt128.Zero, value);

    /// <summary>The exact 256-bit product of two 128-bit factors.</summary>
    public static UInt256 Multiply(UInt128 left, UInt128 right)
    {
        UInt128 leftLow = (ulong)left;
        var leftHigh = (UInt128)(ulong)(left >> 64);
        UInt128 rightLow = (ulong)right;
        var rightHigh = (UInt128)(ulong)(right >> 64);

        var lowLow = leftLow * rightLow;
        var crossA = leftLow * rightHigh;
        var crossB = leftHigh * rightLow;
        var highHigh = leftHigh * rightHigh;

        var cross = crossA + crossB;
        var crossCarry = cross < crossA ? UInt128.One : UInt128.Zero;

        var low = lowLow + (cross << 64);
        var lowCarry = low < lowLow ? UInt128.One : UInt128.Zero;
        var high = highHigh + (cross >> 64) + (crossCarry << 64) + lowCarry;
        return new(high, low);
    }

    public static UInt256 Add(UInt256 left, UInt256 right)
    {
        var low = left.Low + right.Low;
        var carry = low < left.Low ? UInt128.One : UInt128.Zero;
        return new(left.High + right.High + carry, low);
    }

    /// <summary><paramref name="left"/> − <paramref name="right"/>, which the callers only take where the difference is non-negative.</summary>
    public static UInt256 Subtract(UInt256 left, UInt256 right)
    {
        var low = left.Low - right.Low;
        var borrow = left.Low < right.Low ? UInt128.One : UInt128.Zero;
        return new(left.High - right.High - borrow, low);
    }

    /// <summary>
    /// <paramref name="value"/> × <paramref name="multiplier"/>, false when the
    /// product needs more than 256 bits.
    /// </summary>
    public static bool TryMultiply(UInt256 value, ulong multiplier, out UInt256 result)
    {
        Span<ulong> limbs = stackalloc ulong[4];
        value.WriteLimbs(limbs);
        ulong carry = 0;
        for (var i = 0; i < 4; i++)
        {
            var product = ((UInt128)limbs[i] * multiplier) + carry;
            limbs[i] = (ulong)product;
            carry = (ulong)(product >> 64);
        }

        result = FromLimbs(limbs);
        return carry == 0;
    }

    /// <summary>
    /// <paramref name="value"/> ÷ <paramref name="divisor"/>, truncated, with
    /// the remainder. A divisor of zero is the caller's to exclude.
    /// </summary>
    public static UInt256 DivRem(UInt256 value, UInt128 divisor, out UInt128 remainder)
    {
        if (divisor <= ulong.MaxValue)
        {
            var quotient = DivRem(value, (ulong)divisor, out var narrow);
            remainder = narrow;
            return quotient;
        }

        return DivRemWide(value, divisor, out remainder);
    }

    /// <summary>
    /// <paramref name="value"/> ÷ <paramref name="divisor"/>, truncated, with
    /// the remainder — the limb-at-a-time long division a divisor inside 64
    /// bits admits.
    /// </summary>
    public static UInt256 DivRem(UInt256 value, ulong divisor, out ulong remainder)
    {
        Span<ulong> limbs = stackalloc ulong[4];
        value.WriteLimbs(limbs);
        UInt128 running = 0;
        for (var i = 3; i >= 0; i--)
        {
            var current = (running << 64) | limbs[i];
            limbs[i] = (ulong)(current / divisor);
            running = current % divisor;
        }

        remainder = (ulong)running;
        return FromLimbs(limbs);
    }

    /// <summary>
    /// Knuth's algorithm D for a two-limb divisor: normalize both operands so
    /// the divisor's top limb has its high bit set, estimate each quotient limb
    /// from a 128-by-64 division, then correct the at-most-two-off estimate.
    /// </summary>
    private static UInt256 DivRemWide(UInt256 value, UInt128 divisor, out UInt128 remainder)
    {
        var shift = BitOperations.LeadingZeroCount((ulong)(divisor >> 64));
        Span<ulong> v = [ShiftLeftLimb((ulong)divisor, 0, shift), ShiftLeftLimb((ulong)(divisor >> 64), (ulong)divisor, shift)];

        Span<ulong> u = stackalloc ulong[5];
        value.WriteLimbs(u[..4]);
        u[4] = shift == 0 ? 0 : u[3] >> (64 - shift);
        for (var i = 3; i >= 1; i--)
            u[i] = ShiftLeftLimb(u[i], u[i - 1], shift);
        u[0] <<= shift;

        Span<ulong> q = stackalloc ulong[4];
        for (var j = 2; j >= 0; j--)
        {
            var numerator = ((UInt128)u[j + 2] << 64) | u[j + 1];
            var estimate = numerator / v[1];
            var rest = numerator - (estimate * v[1]);
            while (estimate > ulong.MaxValue || (UInt128)(ulong)estimate * v[0] > ((rest << 64) | u[j]))
            {
                estimate--;
                rest += v[1];
                if (rest > ulong.MaxValue)
                    break;
            }

            var digit = (ulong)estimate;
            ulong mulCarry = 0;
            ulong borrow = 0;
            for (var i = 0; i < 2; i++)
            {
                var product = ((UInt128)digit * v[i]) + mulCarry;
                mulCarry = (ulong)(product >> 64);
                var difference = (UInt128)u[i + j] - (ulong)product - borrow;
                u[i + j] = (ulong)difference;
                borrow = (ulong)((difference >> 64) & 1);
            }

            var top = (UInt128)u[j + 2] - mulCarry - borrow;
            u[j + 2] = (ulong)top;
            if (((top >> 64) & 1) != 0)
            {
                // The estimate was one too high: put the divisor back.
                digit--;
                ulong carry = 0;
                for (var i = 0; i < 2; i++)
                {
                    var sum = (UInt128)u[i + j] + v[i] + carry;
                    u[i + j] = (ulong)sum;
                    carry = (ulong)(sum >> 64);
                }

                u[j + 2] = unchecked(u[j + 2] + carry);
            }

            q[j] = digit;
        }

        var remainderLow = shift == 0 ? u[0] : (u[0] >> shift) | (u[1] << (64 - shift));
        remainder = ((UInt128)(u[1] >> shift) << 64) | remainderLow;
        return FromLimbs(q);
    }

    private static ulong ShiftLeftLimb(ulong limb, ulong lower, int shift) =>
        shift == 0 ? limb : (limb << shift) | (lower >> (64 - shift));

    public UInt256 ShiftRight(int bits)
    {
        if (bits >= 256)
            return Zero;
        if (bits >= 128)
            return new(UInt128.Zero, this.High >> (bits - 128));
        if (bits == 0)
            return this;
        return new(this.High >> bits, (this.Low >> bits) | (this.High << (128 - bits)));
    }

    /// <summary>True when bit <paramref name="index"/> (counting from zero) is set.</summary>
    public bool IsBitSet(int index)
    {
        if (index >= 256)
            return false;
        var word = index >= 128 ? this.High >> (index - 128) : this.Low >> index;
        return (word & UInt128.One) != UInt128.Zero;
    }

    public int CompareTo(UInt256 other) =>
        this.High != other.High ? this.High.CompareTo(other.High) : this.Low.CompareTo(other.Low);

    public bool Equals(UInt256 other) => this.High == other.High && this.Low == other.Low;

    public override bool Equals(object? obj) => obj is UInt256 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.High, this.Low);

    public static bool operator ==(UInt256 left, UInt256 right) => left.Equals(right);

    public static bool operator !=(UInt256 left, UInt256 right) => !left.Equals(right);

    public static bool operator <(UInt256 left, UInt256 right) => left.CompareTo(right) < 0;

    public static bool operator >(UInt256 left, UInt256 right) => left.CompareTo(right) > 0;

    public static bool operator <=(UInt256 left, UInt256 right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UInt256 left, UInt256 right) => left.CompareTo(right) >= 0;

    /// <summary>Decimal rendering, for diagnostics and test failure messages.</summary>
    public override string ToString()
    {
        if (IsZero)
            return "0";

        Span<char> digits = stackalloc char[78];
        var next = digits.Length;
        var running = this;
        while (!running.IsZero)
        {
            running = DivRem(running, 10UL, out var digit);
            digits[--next] = (char)('0' + digit);
        }

        return new(digits[next..]);
    }

    private void WriteLimbs(Span<ulong> limbs)
    {
        limbs[0] = (ulong)this.Low;
        limbs[1] = (ulong)(this.Low >> 64);
        limbs[2] = (ulong)this.High;
        limbs[3] = (ulong)(this.High >> 64);
    }

    private static UInt256 FromLimbs(ReadOnlySpan<ulong> limbs) =>
        new(((UInt128)limbs[3] << 64) | limbs[2], ((UInt128)limbs[1] << 64) | limbs[0]);
}
