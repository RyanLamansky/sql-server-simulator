using System.Numerics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
/// <remarks>
/// A differential sweep of <see cref="Decimal38"/> against a
/// <see cref="BigInteger"/> reference model implementing the same stated rules
/// — division truncating toward zero at the result scale, every other operator
/// rounding half away from zero there — over randomized operands across the
/// whole 1-to-38-digit range at a fixed seed.
/// </remarks>
[TestClass]
public sealed class Decimal38DifferentialTests
{
    private const int CasesPerOperator = 4000;

    [TestMethod]
    public void Add_AgreesWithReferenceModel() => Sweep(Operator.Add);

    [TestMethod]
    public void Subtract_AgreesWithReferenceModel() => Sweep(Operator.Subtract);

    [TestMethod]
    public void Multiply_AgreesWithReferenceModel() => Sweep(Operator.Multiply);

    [TestMethod]
    public void Divide_AgreesWithReferenceModel() => Sweep(Operator.Divide);

    [TestMethod]
    public void Modulo_AgreesWithReferenceModel() => Sweep(Operator.Modulo);

    [TestMethod]
    public void Rescale_AgreesWithReferenceModel()
    {
        var random = new Random(20260805);
        for (var i = 0; i < CasesPerOperator; i++)
        {
            var value = NextValue(random, out var unscaled);
            var scale = random.Next(0, 39);
            var precision = random.Next(scale == 0 ? 1 : scale, 39);

            var expectedFits = TryReferenceRescale(unscaled, value.Scale, scale, precision, rounded: true, out var expected);
            var fits = Decimal38.TryRescale(value, precision, scale, out var actual);
            AreEqual(expectedFits, fits, $"rescale {value} to ({precision}, {scale})");
            if (fits)
                AreEqual(expected, Unscaled(actual), $"rescale {value} to ({precision}, {scale})");

            var truncatedFits = TryReferenceRescale(unscaled, value.Scale, scale, precision, rounded: false, out var truncated);
            var actualTruncatedFits = Decimal38.TryTruncate(value, precision, scale, out var actualTruncated);
            AreEqual(truncatedFits, actualTruncatedFits, $"truncate {value} to ({precision}, {scale})");
            if (truncatedFits)
                AreEqual(truncated, Unscaled(actualTruncated), $"truncate {value} to ({precision}, {scale})");
        }
    }

    [TestMethod]
    public void CompareTo_AgreesWithReferenceModel()
    {
        var random = new Random(7);
        for (var i = 0; i < CasesPerOperator; i++)
        {
            var left = NextValue(random, out var leftUnscaled);
            var right = NextValue(random, out var rightUnscaled);
            var common = Math.Max(left.Scale, right.Scale);
            var expected = (leftUnscaled * BigInteger.Pow(10, common - left.Scale))
                .CompareTo(rightUnscaled * BigInteger.Pow(10, common - right.Scale));

            AreEqual(Math.Sign(expected), Math.Sign(left.CompareTo(right)), $"{left} vs {right}");
            AreEqual(expected == 0, left.Equals(right), $"{left} vs {right}");
            if (expected == 0)
                AreEqual(left.GetHashCode(), right.GetHashCode(), $"{left} vs {right}");
        }
    }

    [TestMethod]
    public void ToStringAndTryParse_RoundTrip()
    {
        var random = new Random(99);
        for (var i = 0; i < CasesPerOperator; i++)
        {
            var value = NextValue(random, out var unscaled);
            var text = value.ToString();
            AreEqual(Decimal38ParseOutcome.Success, Decimal38.TryParse(text, 38, value.Scale, out var parsed), text);
            AreEqual(unscaled, Unscaled(parsed), text);
            AreEqual(value.Scale, parsed.Scale, text);
            AreEqual(text, parsed.ToString());
        }
    }

    [TestMethod]
    public void DotNetDecimal_RoundTripsExactly()
    {
        var random = new Random(555);
        for (var i = 0; i < CasesPerOperator; i++)
        {
            var source = NextNarrowDecimal(random);
            var value = Decimal38.FromDotNetDecimal(source);
            IsTrue(Decimal38.TryToDotNetDecimal(value, out var back), source.ToString());
            AreEqual(source, back);
            AreEqual(source.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ToString());
        }
    }

    private enum Operator
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
    }

    private static void Sweep(Operator op)
    {
        var random = new Random(1000 + (int)op);
        for (var i = 0; i < CasesPerOperator; i++)
        {
            var left = NextValue(random, out var leftUnscaled);
            var right = NextValue(random, out var rightUnscaled);
            if (op is Operator.Divide or Operator.Modulo && right.IsZero)
                continue;

            var leftPrecision = Math.Max(left.SignificantDigits(), left.Scale);
            var rightPrecision = Math.Max(right.SignificantDigits(), right.Scale);
            var (precision, scale) = ResultType(op, leftPrecision, left.Scale, rightPrecision, right.Scale);

            var expectedFits = TryReference(op, leftUnscaled, left.Scale, rightUnscaled, right.Scale, precision, scale, out var expected);
            var fits = Apply(op, left, right, precision, scale, out var actual);

            var label = $"{left} {op} {right} at ({precision}, {scale})";
            AreEqual(expectedFits, fits, label);
            if (!fits)
                continue;

            AreEqual(expected, Unscaled(actual), label);
            AreEqual(scale, actual.Scale, label);
        }
    }

    private static bool Apply(Operator op, in Decimal38 left, in Decimal38 right, int precision, int scale, out Decimal38 result) => op switch
    {
        Operator.Add => Decimal38.TryAdd(left, right, precision, scale, out result),
        Operator.Subtract => Decimal38.TrySubtract(left, right, precision, scale, out result),
        Operator.Multiply => Decimal38.TryMultiply(left, right, precision, scale, out result),
        Operator.Divide => Decimal38.TryDivide(left, right, precision, scale, out result),
        _ => Decimal38.TryModulo(left, right, precision, scale, out result),
    };

    /// <summary>
    /// The reference model: exact rational arithmetic in
    /// <see cref="BigInteger"/>, with division truncating toward zero at the
    /// result scale and everything else rounding half away from zero there.
    /// </summary>
    private static bool TryReference(Operator op, BigInteger left, int leftScale, BigInteger right, int rightScale, int precision, int scale, out BigInteger result)
    {
        if (op is Operator.Add or Operator.Subtract)
        {
            var common = Math.Max(leftScale, rightScale);
            var sum = (left * BigInteger.Pow(10, common - leftScale))
                + ((op == Operator.Subtract ? -right : right) * BigInteger.Pow(10, common - rightScale));
            return TryReferenceRescale(sum, common, scale, precision, rounded: true, out result);
        }

        if (op == Operator.Multiply)
            return TryReferenceRescale(left * right, leftScale + rightScale, scale, precision, rounded: true, out result);

        if (op == Operator.Divide)
            return TryReferenceDivide(left, leftScale, right, rightScale, precision, scale, out result);

        var alignment = Math.Max(leftScale, rightScale);
        var dividend = BigInteger.Abs(left) * BigInteger.Pow(10, alignment - leftScale);
        var divisor = BigInteger.Abs(right) * BigInteger.Pow(10, alignment - rightScale);
        var remainder = dividend % divisor;
        return TryReferenceRescale(left.Sign < 0 ? -remainder : remainder, alignment, scale, precision, rounded: true, out result);
    }

    private static bool TryReferenceDivide(BigInteger left, int leftScale, BigInteger right, int rightScale, int precision, int scale, out BigInteger result)
    {
        result = BigInteger.Zero;
        var shift = scale + rightScale - leftScale;
        var numerator = BigInteger.Abs(left);
        var denominator = BigInteger.Abs(right);
        if (shift >= 0)
            numerator *= BigInteger.Pow(10, shift);
        else
            denominator *= BigInteger.Pow(10, -shift);

        var quotient = numerator / denominator;
        if (quotient >= BigInteger.Pow(10, precision))
            return false;

        result = left.Sign * right.Sign < 0 ? -quotient : quotient;
        return true;
    }

    private static bool TryReferenceRescale(BigInteger unscaled, int currentScale, int scale, int precision, bool rounded, out BigInteger result)
    {
        var negative = unscaled.Sign < 0;
        var magnitude = BigInteger.Abs(unscaled);
        if (scale < currentScale)
        {
            var divisor = BigInteger.Pow(10, currentScale - scale);
            var quotient = BigInteger.DivRem(magnitude, divisor, out var remainder);
            magnitude = rounded && remainder * 2 >= divisor ? quotient + 1 : quotient;
        }
        else if (scale > currentScale)
        {
            magnitude *= BigInteger.Pow(10, scale - currentScale);
        }

        result = BigInteger.Zero;
        if (magnitude >= BigInteger.Pow(10, precision))
            return false;

        result = negative ? -magnitude : magnitude;
        return true;
    }

    private static (int Precision, int Scale) ResultType(Operator op, int leftPrecision, int leftScale, int rightPrecision, int rightScale)
    {
        var (leftInteger, rightInteger) = (leftPrecision - leftScale, rightPrecision - rightScale);
        int precision;
        int scale;
        if (op is Operator.Add or Operator.Subtract)
        {
            scale = Math.Max(leftScale, rightScale);
            precision = Math.Max(leftInteger, rightInteger) + scale + 1;
            if (precision > 38)
            {
                scale = Math.Min(scale, 38 - Math.Max(leftInteger, rightInteger));
                precision = 38;
            }
        }
        else if (op == Operator.Multiply)
        {
            scale = leftScale + rightScale;
            precision = leftPrecision + rightPrecision + 1;
            (precision, scale) = CapMultiplicative(precision, scale);
        }
        else if (op == Operator.Divide)
        {
            scale = Math.Max(6, leftScale + rightPrecision + 1);
            precision = leftInteger + rightScale + scale;
            (precision, scale) = CapMultiplicative(precision, scale);
        }
        else
        {
            scale = Math.Max(leftScale, rightScale);
            precision = Math.Min(leftInteger, rightInteger) + scale;
        }

        precision = Math.Clamp(precision, 1, 38);
        scale = Math.Clamp(scale, 0, precision);
        return (precision, scale);
    }

    /// <summary>The 38-cap scale reduction the multiplicative family takes, floored at six.</summary>
    private static (int Precision, int Scale) CapMultiplicative(int precision, int scale) =>
        precision <= 38 ? (precision, scale) : (38, Math.Max(Math.Min(scale, 6), scale - (precision - 38)));

    private static Decimal38 NextValue(Random random, out BigInteger unscaled)
    {
        var digits = random.Next(1, 39);
        UInt128 magnitude = 0;
        for (var i = 0; i < digits; i++)
            magnitude = (magnitude * 10) + (UInt128)(uint)random.Next(0, 10);

        var scale = random.Next(0, 39);
        var negative = random.Next(2) == 1;
        var value = Decimal38.FromParts(magnitude, negative, scale);
        unscaled = ToBigInteger(value);
        return value;
    }

    private static decimal NextNarrowDecimal(Random random, int maxDigits = 19, int maxScale = 14)
    {
        var digits = random.Next(1, maxDigits + 1);
        long magnitude = 0;
        for (var i = 0; i < digits; i++)
            magnitude = (magnitude * 10) + random.Next(0, 10);

        var scale = random.Next(0, maxScale + 1);
        var negative = magnitude != 0 && random.Next(2) == 1;
        return new((int)(uint)magnitude, (int)(uint)((ulong)magnitude >> 32), 0, negative, (byte)scale);
    }

    private static BigInteger Unscaled(in Decimal38 value) => ToBigInteger(value);

    private static BigInteger ToBigInteger(in Decimal38 value)
    {
        var magnitude = (new BigInteger((ulong)(value.Magnitude >> 64)) << 64) + (ulong)value.Magnitude;
        return value.IsNegative ? -magnitude : magnitude;
    }
}
