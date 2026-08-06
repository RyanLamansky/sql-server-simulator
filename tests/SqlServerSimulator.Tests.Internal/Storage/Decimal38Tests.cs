using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
/// <remarks>
/// Every expected value here was read off SQL Server 2025 (2026-08-05); the
/// statement that produced it is quoted beside it. The result <c>(precision,
/// scale)</c> each case passes is the one <c>PromoteForArithmetic</c>'s rules
/// give for the operand types the statement wrote.
/// </remarks>
[TestClass]
public sealed class Decimal38Tests
{
    private const string Digits38 = "12345678901234567890123456789012345678";
    private const string OtherDigits38 = "98765432109876543210987654321098765432";
    private const string Max38 = "99999999999999999999999999999999999999";

    // ---- division truncates toward zero at the result scale ----

    [TestMethod]
    public void Divide_Uncapped_TruncatesAtTheResultScale() =>
        // SELECT CAST(4.00 AS decimal(5, 2)) / 7  ->  decimal(9, 6)  ->  0.571428
        AssertDivide("4.00", "7", 9, 6, "0.571428");

    [TestMethod]
    public void Divide_Capped_TruncatesAtTheSameDigit() =>
        // SELECT CAST(4.00 AS decimal(38, 2)) / 7  ->  decimal(38, 6)  ->  0.571428
        AssertDivide("4.00", "7", 38, 6, "0.571428");

    [TestMethod]
    public void Divide_ExactHalfAtTheCut_DropsIt() =>
        // SELECT CAST(1 AS decimal(5, 0)) / 1600000  ->  decimal(13, 8)  ->  0.00000062
        AssertDivide("1", "1600000", 13, 8, "0.00000062");

    [TestMethod]
    public void Divide_ExactHalfAtTheCappedCut_DropsIt() =>
        // SELECT CAST(3.00 AS decimal(38, 2)) / 2000000  ->  decimal(38, 6)  ->  0.000001
        AssertDivide("3.00", "2000000", 38, 6, "0.000001");

    [TestMethod]
    [DataRow("4.00", "7", "0.571428")]
    [DataRow("-4.00", "7", "-0.571428")]
    [DataRow("4.00", "-7", "-0.571428")]
    [DataRow("-4.00", "-7", "0.571428")]
    public void Divide_TheSignMovesOnlyTheSign(string left, string right, string expected) =>
        AssertDivide(left, right, 9, 6, expected);

    [TestMethod]
    public void Divide_AtScale28_KeepsTheExactQuotientsDigits() =>
        // SELECT CAST(2 AS decimal(38, 28)) / 3  ->  decimal(38, 28)
        AssertDivide("2.0000000000000000000000000000", "3", 38, 28, "0.6666666666666666666666666666");

    [TestMethod]
    public void Divide_WideOperands_TruncatesTheExactQuotient()
    {
        // SELECT CAST(<38 digits> AS decimal(38, 0)) / CAST(<38 digits> AS decimal(38, 0))
        AssertDivide(Digits38, OtherDigits38, 38, 6, "0.124999");
        AssertDivide(OtherDigits38, Digits38, 38, 6, "8.000000");
    }

    [TestMethod]
    public void Divide_AtScale37_RunsThroughTheWideIntermediate()
    {
        // DECLARE @p decimal(38, 37) = 0.1;  SELECT @p / CAST(3 AS decimal(2, 0))
        AssertDivide("0.1000000000000000000000000000000000000", "3", 38, 37, "0.0333333333333333333333333333333333333");
        AssertDivide("0.9999999999999999999999999999999999999", "3", 38, 37, "0.3333333333333333333333333333333333333");
        AssertDivide("0.9999999999999999999999999999999999999", "7", 38, 37, "0.1428571428571428571428571428571428571");
        AssertDivide("-0.9999999999999999999999999999999999999", "7", 38, 37, "-0.1428571428571428571428571428571428571");
    }

    [TestMethod]
    public void Divide_ByZero_IsTheCallersToRefuse() =>
        _ = ThrowsExactly<DivideByZeroException>(() => Decimal38.TryDivide(Literal("1"), Literal("0"), 38, 6, out _));

    // ---- every other operator rounds half away from zero ----

    [TestMethod]
    public void Multiply_ExactHalfAtTheCut_RoundsAway()
    {
        // DECLARE @x decimal(20, 10) = 0.0000000005, @y decimal(18, 9) = 0.000000001;
        // SELECT @x * @y  ->  decimal(38, 18)
        AssertMultiply("0.0000000005", "0.000000001", 38, 18, "0.000000000000000001");
        AssertMultiply("-0.0000000005", "0.000000001", 38, 18, "-0.000000000000000001");
        AssertMultiply("0.0000000004", "0.000000001", 38, 18, "0.000000000000000000");
        AssertMultiply("0.0000000015", "0.000000001", 38, 18, "0.000000000000000002");
    }

    [TestMethod]
    public void Multiply_AtTheCappedScale_RoundsAway()
    {
        // SELECT CAST(0.0000005 AS decimal(38, 7)) * CAST(1 AS decimal(38, 0))  ->  decimal(38, 6)
        AssertMultiply("0.0000005", "1", 38, 6, "0.000001");
        AssertMultiply("-0.0000005", "1", 38, 6, "-0.000001");
    }

    [TestMethod]
    public void Add_AtACappedScale_RoundsAwayRatherThanTruncating()
    {
        // DECLARE @a decimal(38, 20) = 0, @b decimal(38, 30) = 0.000000000000000000005;
        // SELECT @a + @b  ->  decimal(38, 20)  ->  0.00000000000000000001
        AssertAdd("0.00000000000000000000", "0.000000000000000000005000000000", 38, 20, "0.00000000000000000001");
        AssertAdd("0.00000000000000000000", "0.000000000000000000004999999999", 38, 20, "0.00000000000000000000");
        AssertAdd("0.00000000000000000000", "-0.000000000000000000005000000000", 38, 20, "-0.00000000000000000001");
        AssertSubtract("0.00000000000000000000", "0.000000000000000000005000000000", 38, 20, "-0.00000000000000000001");
    }

    [TestMethod]
    public void Subtract_WideOperands_IsExact() =>
        // SELECT CAST(<b> AS decimal(38, 0)) - CAST(<a> AS decimal(38, 0))
        AssertSubtract(OtherDigits38, Digits38, 38, 0, "86419753208641975320864197532086419754");

    // ---- modulo ----

    [TestMethod]
    [DataRow("10.1234", "3.00", "1.1234")]
    [DataRow("-10.1234", "3.00", "-1.1234")]
    [DataRow("10.1234", "-3.00", "1.1234")]
    [DataRow("-10.1234", "-3.00", "-1.1234")]
    public void Modulo_SignFollowsTheDividend(string left, string right, string expected) =>
        // DECLARE @m1 decimal(10, 4) = 10.1234, @m2 decimal(8, 2) = 3.00;  ->  decimal(10, 4)
        AssertModulo(left, right, 10, 4, expected);

    [TestMethod]
    public void Modulo_WideOperands_FollowsTheSameRule()
    {
        AssertModulo(OtherDigits38, Digits38, 38, 0, "900000000090000000009000000008");
        AssertModulo("-" + OtherDigits38, Digits38, 38, 0, "-900000000090000000009000000008");
        AssertModulo(OtherDigits38, "-" + Digits38, 38, 0, "900000000090000000009000000008");
        AssertModulo(Digits38, OtherDigits38, 38, 0, Digits38);
    }

    [TestMethod]
    public void Modulo_ByZero_IsTheCallersToRefuse() =>
        _ = ThrowsExactly<DivideByZeroException>(() => Decimal38.TryModulo(Literal("1"), Literal("0"), 38, 6, out _));

    // ---- overflow ----

    [TestMethod]
    public void Add_PastTheDeclaredPrecision_Overflows() =>
        // DECLARE @a decimal(38, 0) = <38 nines>;  SELECT @a + @a  ->  Msg 8115 state 2
        IsFalse(Decimal38.TryAdd(Literal(Max38), Literal(Max38), 38, 0, out _));

    [TestMethod]
    public void Multiply_PastTheDeclaredPrecision_Overflows()
    {
        IsFalse(Decimal38.TryMultiply(Literal(Max38), Literal("10"), 38, 0, out _));
        IsFalse(Decimal38.TryMultiply(Literal(Max38), Literal(Max38), 38, 0, out _));
    }

    [TestMethod]
    public void Divide_PastTheDeclaredPrecision_Overflows() =>
        // SELECT <38 nines> / CAST(0.0001 AS decimal(38, 10))  ->  Msg 8115 state 2
        IsFalse(Decimal38.TryDivide(Literal(Max38), Literal("0.0001000000"), 38, 6, out _));

    [TestMethod]
    public void Rescale_PastTheDeclaredPrecision_Overflows() =>
        // SELECT CAST(<38 nines> AS decimal(20, 0))  ->  Msg 8115 state 8
        IsFalse(Decimal38.TryRescale(Literal(Max38), 20, 0, out _));

    // ---- string conversion ----

    [TestMethod]
    [DataRow("1.005", 5, 2, "1.01")]
    [DataRow("1.004999", 5, 2, "1.00")]
    [DataRow("-1.005", 5, 2, "-1.01")]
    [DataRow("1.015", 5, 2, "1.02")]
    [DataRow("1.0050000000000000000000000000000000001", 5, 2, "1.01")]
    [DataRow("  1.5  ", 5, 2, "1.50")]
    [DataRow("+1.5", 5, 2, "1.50")]
    [DataRow(".5", 5, 2, "0.50")]
    [DataRow("5.", 5, 2, "5.00")]
    [DataRow("9.5", 2, 0, "10")]
    [DataRow("-2.5", 2, 0, "-3")]
    [DataRow("1.5", 38, 0, "2")]
    [DataRow("000000000000000000000000000000000000000001", 38, 0, "1")]
    [DataRow("0.00000000000000000000000000000000000000001", 38, 10, "0.0000000000")]
    [DataRow("1.0000000000000000000000000000000000000005", 38, 2, "1.00")]
    [DataRow("12345678901234567890123456789012345.678", 38, 3, "12345678901234567890123456789012345.678")]
    [DataRow("0.999999999999999999999999999999999999994", 38, 38, "0.99999999999999999999999999999999999999")]
    public void TryParse_RoundsExcessDigitsHalfAwayFromZero(string text, int precision, int scale, string expected)
    {
        AreEqual(Decimal38ParseOutcome.Success, Decimal38.TryParse(text, precision, scale, out var value), text);
        AreEqual(expected, value.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(".")]
    [DataRow("abc")]
    [DataRow("1e5")]
    [DataRow("1E5")]
    [DataRow("1.5e2")]
    [DataRow("1,000")]
    [DataRow("$1.00")]
    [DataRow("(1)")]
    [DataRow("++1")]
    [DataRow("1+")]
    [DataRow("1.2.3")]
    [DataRow("-")]
    public void TryParse_NonNumericText_IsMalformed(string text) =>
        // Real reports Msg 8114 state 5 for each of these.
        AreEqual(Decimal38ParseOutcome.Malformed, Decimal38.TryParse(text, 38, 0, out _), text);

    [TestMethod]
    [DataRow("123456789012345678901234567890123456789", 38, 0)]
    [DataRow("1234567890123456789012345678901234567890", 38, 0)]
    [DataRow("0.999999999999999999999999999999999999995", 38, 38)]
    public void TryParse_PastNumericsOwnDomain_IsThirtyEightDigitOverflow(string text, int precision, int scale) =>
        // Real reports Msg 8115 state 6 for each of these.
        AreEqual(Decimal38ParseOutcome.ExceedsNumericDomain, Decimal38.TryParse(text, precision, scale, out _), text);

    [TestMethod]
    [DataRow("99.5", 2, 0)]
    [DataRow("1234.5", 5, 2)]
    [DataRow("123456789012345678901234567890", 20, 0)]
    public void TryParse_PastTheDeclaredPrecision_IsTargetOverflow(string text, int precision, int scale) =>
        // Real reports Msg 8115 state 8 for each of these.
        AreEqual(Decimal38ParseOutcome.ExceedsDeclaredPrecision, Decimal38.TryParse(text, precision, scale, out _), text);

    // ---- rendering ----

    [TestMethod]
    [DataRow("1", 10, 2, "1.00")]
    [DataRow("-1", 10, 2, "-1.00")]
    [DataRow("0", 10, 4, "0.0000")]
    [DataRow("0.5", 10, 0, "1")]
    [DataRow("-0.0", 10, 2, "0.00")]
    [DataRow("1", 38, 30, "1.000000000000000000000000000000")]
    [DataRow("0.05", 10, 2, "0.05")]
    public void ToString_WritesEveryDigitTheDeclaredScaleCallsFor(string text, int precision, int scale, string expected)
    {
        // SELECT CAST(CAST(<text> AS decimal(38, *)) AS varchar(60))
        var point = text.IndexOf('.');
        var sourceScale = point < 0 ? 0 : text.Length - point - 1;
        AreEqual(Decimal38ParseOutcome.Success, Decimal38.TryParse(text, 38, sourceScale, out var source), text);
        IsTrue(Decimal38.TryRescale(source, precision, scale, out var value), text);
        AreEqual(expected, value.ToString());
    }

    [TestMethod]
    public void TryFormat_TooSmallADestination_ReportsFalse()
    {
        Span<char> buffer = stackalloc char[3];
        IsFalse(Literal("1.500").TryFormat(buffer, out var written));
        AreEqual(0, written);
    }

    // ---- float ----

    [TestMethod]
    [DataRow(1.5, 10, 0, "2")]
    [DataRow(2.5, 10, 0, "3")]
    [DataRow(-1.5, 10, 0, "-2")]
    [DataRow(0.5, 10, 0, "1")]
    [DataRow(0.125, 10, 2, "0.13")]
    [DataRow(-0.125, 10, 2, "-0.13")]
    [DataRow(0.375, 10, 2, "0.38")]
    [DataRow(0.1, 38, 20, "0.10000000000000000555")]
    [DataRow(0.333333, 38, 30, "0.333332999999999990414778494596")]
    [DataRow(1e30, 38, 0, "1000000000000000019884624838656")]
    [DataRow(123456789012345678d, 38, 0, "123456789012345680")]
    [DataRow(1e38, 38, 0, "99999999999999997748809823456034029568")]
    public void TryFromDouble_ReadsTheExactBinaryValueAndRoundsHalfAway(double value, int precision, int scale, string expected)
    {
        // SELECT CAST(CAST(<value> AS float) AS decimal(precision, scale))
        IsTrue(Decimal38.TryFromDouble(value, precision, scale, out var result), expected);
        AreEqual(expected, result.ToString());
    }

    [TestMethod]
    [DataRow(1e39)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void TryFromDouble_PastTheDomain_ReportsFalse(double value) =>
        // SELECT CAST(CAST(1e39 AS float) AS decimal(38, 0))  ->  Msg 8115 state 6
        IsFalse(Decimal38.TryFromDouble(value, 38, 0, out _));

    [TestMethod]
    public void ToDouble_IsCorrectlyRounded()
    {
        AreEqual(0.5d, Literal("0.50000").ToDouble());
        AreEqual(1d / 3d, Literal("0.33333333333333333333333333333333333333").ToDouble(), 1e-17);
        AreEqual(-2.5d, Literal("-2.5").ToDouble());
    }

    // ---- the .NET decimal boundary SqlClient enforces ----

    [TestMethod]
    [DataRow("79228162514264337593543950335", 0, "79228162514264337593543950335")]
    [DataRow("1", 30, "1.0000000000000000000000000000")]
    [DataRow("1", 29, "1.0000000000000000000000000000")]
    [DataRow("1", 28, "1.0000000000000000000000000000")]
    [DataRow("1.5", 30, "1.5000000000000000000000000000")]
    [DataRow("0.1", 38, "0.1000000000000000000000000000")]
    [DataRow("0.55", 30, "0.5500000000000000000000000000")]
    [DataRow("8000000000000000000000000000", 1, "8000000000000000000000000000")]
    [DataRow("12345678901234567890123456789", 1, "12345678901234567890123456789")]
    public void TryToDotNetDecimal_ShedsTrailingZerosToFit(string text, int scale, string expected)
    {
        // Read back through SqlClient's GetDecimal, which sheds trailing zeros
        // rather than raising: CAST(1 AS decimal(38, 30)) arrives at scale 28.
        var value = Rescaled(text, scale);
        IsTrue(Decimal38.TryToDotNetDecimal(value, out var converted), text);
        AreEqual(expected, converted.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow("79228162514264337593543950336", 0)]
    [DataRow("12345678901234567890123456789012345678", 0)]
    [DataRow("123456789012345678901234567890", 0)]
    [DataRow("0.12345678901234567890123456789012345679", 38)]
    [DataRow("12345678901234567890123456789.5", 1)]
    [DataRow("111111111111111111111111111111.0", 1)]
    public void TryToDotNetDecimal_PastTheNinetySixBitMantissa_ReportsFalse(string text, int scale)
    {
        // SqlClient raises System.OverflowException("Conversion overflows.")
        // for each of these.
        IsFalse(Decimal38.TryToDotNetDecimal(Rescaled(text, scale), out _), text);
    }

    // ---- identity ----

    [TestMethod]
    public void Equality_IgnoresTheDeclaredScale()
    {
        var one = Literal("1");
        var padded = Rescaled("1", 6);
        IsTrue(one == padded);
        AreEqual(one.GetHashCode(), padded.GetHashCode());
        AreEqual(0, one.CompareTo(padded));
        AreEqual("1.000000", padded.ToString());
    }

    [TestMethod]
    public void NegativeZero_NormalizesToPositive()
    {
        AreEqual(Decimal38ParseOutcome.Success, Decimal38.TryParse("-0.00", 10, 2, out var value));
        IsFalse(value.IsNegative);
        AreEqual(0, value.Sign);
        AreEqual("0.00", value.ToString());
        AreEqual("0.00", Literal("0.00").Negate().ToString());
    }

    [TestMethod]
    public void Comparison_AcrossScales_AlignsWithoutLosingDigits()
    {
        var wide = Literal("0.99999999999999999999999999999999999999");
        var one = Literal("1");
        IsTrue(wide < one);
        IsTrue(one > wide);
        IsTrue(Literal("-" + Max38) < Literal("0"));
        IsTrue(Literal(Max38) > Literal("0.00000000000000000000000000000000000001"));
    }

    [TestMethod]
    public void Int64_RoundTripsIncludingTheAsymmetricEnd()
    {
        IsTrue(Decimal38.TryToInt64(Decimal38.FromInt64(long.MinValue), out var min));
        AreEqual(long.MinValue, min);
        IsTrue(Decimal38.TryToInt64(Decimal38.FromInt64(long.MaxValue), out var max));
        AreEqual(long.MaxValue, max);
        IsTrue(Decimal38.TryToInt64(Literal("-1.99"), out var truncated));
        AreEqual(-1L, truncated);
        IsFalse(Decimal38.TryToInt64(Literal(Max38), out _));
    }

    private static Decimal38 Literal(string text)
    {
        var point = text.IndexOf('.');
        var scale = point < 0 ? 0 : text.Length - point - 1;
        AreEqual(Decimal38ParseOutcome.Success, Decimal38.TryParse(text, 38, scale, out var value), text);
        return value;
    }

    private static Decimal38 Rescaled(string text, int scale)
    {
        AreEqual(Decimal38ParseOutcome.Success, Decimal38.TryParse(text, 38, scale, out var value), text);
        return value;
    }

    private static void AssertAdd(string left, string right, int precision, int scale, string expected)
    {
        IsTrue(Decimal38.TryAdd(Literal(left), Literal(right), precision, scale, out var result), expected);
        AreEqual(expected, result.ToString());
    }

    private static void AssertSubtract(string left, string right, int precision, int scale, string expected)
    {
        IsTrue(Decimal38.TrySubtract(Literal(left), Literal(right), precision, scale, out var result), expected);
        AreEqual(expected, result.ToString());
    }

    private static void AssertMultiply(string left, string right, int precision, int scale, string expected)
    {
        IsTrue(Decimal38.TryMultiply(Literal(left), Literal(right), precision, scale, out var result), expected);
        AreEqual(expected, result.ToString());
    }

    private static void AssertDivide(string left, string right, int precision, int scale, string expected)
    {
        IsTrue(Decimal38.TryDivide(Literal(left), Literal(right), precision, scale, out var result), expected);
        AreEqual(expected, result.ToString());
    }

    private static void AssertModulo(string left, string right, int precision, int scale, string expected)
    {
        IsTrue(Decimal38.TryModulo(Literal(left), Literal(right), precision, scale, out var result), expected);
        AreEqual(expected, result.ToString());
    }
}
