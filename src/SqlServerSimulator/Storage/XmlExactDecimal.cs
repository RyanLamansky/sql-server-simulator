using System.Globalization;
using System.Numerics;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Real's exact XQuery numbers — <c>xs:decimal</c>, <c>xs:integer</c> and the
/// types derived from them — behave as SQL <c>numeric(38, 10)</c> (probed
/// 2026-10-06 against SQL Server 2025): a literal keeps ten fractional digits,
/// rounded half away from zero (<c>0.00000000005</c> is
/// <c>0.0000000001</c>), and at most 28 integer digits, past which it is Msg
/// 2342; a sum past that range is the empty sequence; and an integer keeps
/// every digit, so <c>9007199254740993 + 1</c> is exact where a
/// <see cref="double"/> isn't. A value is carried scaled by 10¹⁰ while it
/// computes.
/// </summary>
/// <remarks>
/// The evaluator's items stay <see cref="double"/> wherever that is exact; a
/// value a double can't carry digit for digit travels as an
/// <see cref="XmlTypedAtomic"/> whose text holds the digits, which every
/// consumer already reads as a number and serializes as its text.
/// </remarks>
internal static class XmlExactDecimal
{
    private const int Scale = 10;
    private static readonly BigInteger Unit = BigInteger.Pow(10, Scale);
    private static readonly BigInteger Limit = BigInteger.Pow(10, 38);

    /// <summary>
    /// Reads a decimal or integer literal's digits, rounded to ten fractional
    /// digits; false when its integer part passes 28 digits.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out BigInteger scaled)
    {
        scaled = BigInteger.Zero;
        var negative = text.Length > 0 && text[0] == '-';
        if (text.Length > 0 && text[0] is '-' or '+')
            text = text[1..];
        var point = text.IndexOf('.');
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? [] : text[(point + 1)..];
        whole = whole.TrimStart('0');
        if (whole.Length > 28)
            return false;
        var digits = new StringBuilder(whole.Length + Scale);
        _ = digits.Append(whole);
        for (var i = 0; i < Scale; i++)
            _ = digits.Append(i < fraction.Length ? fraction[i] : '0');
        scaled = digits.Length == 0 ? BigInteger.Zero : BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (fraction.Length > Scale && fraction[Scale] >= '5')
            scaled += 1;
        if (negative)
            scaled = -scaled;
        return scaled > -Limit && scaled < Limit;
    }

    /// <summary>The exact value of <paramref name="item"/>, when it is an exact number.</summary>
    public static bool TryOf(object item, out BigInteger scaled)
    {
        scaled = BigInteger.Zero;
        switch (item)
        {
            // A whole double below 2^53 is exact as it stands.
            case double whole when Math.Abs(whole) < 9007199254740992 && Math.Floor(whole) == whole:
                scaled = new BigInteger(whole) * Unit;
                return true;
        }
        return item switch
        {
            double number => !double.IsNaN(number) && !double.IsInfinity(number) && TryParse(XmlAtomicTypes.RenderExact(number), out scaled),
            XmlTypedAtomic { Kind: XmlStaticKind.Number } typed => IsExactType(typed.TypeName) && TryParse(typed.Text, out scaled),
            _ => false,
        };
    }

    /// <summary>Whether an <c>xs:</c> type name is <c>xs:decimal</c> or derived from it.</summary>
    public static bool IsExactType(string typeName) =>
        typeName.StartsWith("xs:", StringComparison.Ordinal)
        && XmlAtomicTypes.Resolve(typeName[3..]) is { Datatype.TypeCode: var code }
        && XmlAtomicTypes.NumericRank(code) is 1 or 2;

    /// <summary>The item carrying <paramref name="scaled"/>, or null past the type's range.</summary>
    public static object? Item(BigInteger scaled, string typeName)
    {
        if (scaled <= -Limit || scaled >= Limit)
            return null;
        var text = Render(scaled);
        var number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return XmlAtomicTypes.RenderExact(number) == text ? number : new XmlTypedAtomic(typeName, text, number, XmlStaticKind.Number);
    }

    /// <summary>
    /// <paramref name="left"/> <paramref name="op"/> <paramref name="right"/>
    /// — <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> (<c>div</c>) or <c>m</c>
    /// (<c>mod</c>) — or null for a zero divisor. A decimal product keeps six
    /// fractional digits, rounded, and a quotient six, cut, as real's
    /// <c>numeric</c> scale reduction leaves them.
    /// </summary>
    public static BigInteger? Compute(BigInteger left, char op, BigInteger right, bool decimalResult)
    {
        switch (op)
        {
            case '+':
                return left + right;
            case '-':
                return left - right;
            case '*':
                var product = left * right / Unit;
                return decimalResult ? RoundTo(left * right, Unit * 10_000) * 10_000 : product;
            case 'm':
                return right.IsZero ? null : BigInteger.Remainder(left, right);
            default:
                if (right.IsZero)
                    return null;
                var quotient = left * Unit / right;
                return quotient / 10_000 * 10_000;
        }
    }

    /// <summary>
    /// <paramref name="scaled"/> through a whole-number rounding —
    /// <c>Math.Floor</c>, <c>Math.Ceiling</c>, or <c>fn:round</c>'s half up —
    /// applied to its fraction alone, so the integer digits stay exact.
    /// </summary>
    public static BigInteger Whole(BigInteger scaled, Func<double, double> rounding)
    {
        var whole = BigInteger.DivRem(scaled, Unit, out var remainder);
        if (remainder.Sign < 0)
        {
            whole -= 1;
            remainder += Unit;
        }
        // The fraction, now in [0, 1), rounds as the whole number's own would.
        var fraction = (double)remainder / (double)Unit;
        return (whole + new BigInteger(rounding(fraction))) * Unit;
    }

    /// <summary><paramref name="value"/> divided by <paramref name="divisor"/>, rounded half away from zero.</summary>
    private static BigInteger RoundTo(BigInteger value, BigInteger divisor)
    {
        var quotient = BigInteger.DivRem(value, divisor, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= divisor)
            quotient += value.Sign;
        return quotient;
    }

    /// <summary>Plain digits, no exponent, no trailing fractional zeros.</summary>
    public static string Render(BigInteger scaled)
    {
        var negative = scaled.Sign < 0;
        var digits = BigInteger.Abs(scaled).ToString(CultureInfo.InvariantCulture).PadLeft(Scale + 1, '0');
        var whole = digits[..^Scale];
        var fraction = digits[^Scale..].TrimEnd('0');
        var text = fraction.Length == 0 ? whole : whole + "." + fraction;
        return negative && text != "0" ? "-" + text : text;
    }
}
