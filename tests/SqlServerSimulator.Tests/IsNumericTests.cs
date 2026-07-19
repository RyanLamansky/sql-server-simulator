using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>ISNUMERIC(expression)</c>. Famously lossy on real
/// SQL Server — bare <c>-</c> / <c>.</c> / <c>,</c> / <c>$</c> all return 1,
/// hex strings return 0, and internal whitespace breaks the match. These
/// cases are all probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class IsNumericTests
{
    [TestMethod] public void Integer() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('123')"));
    [TestMethod] public void NegativeInteger() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('-123')"));
    [TestMethod] public void PositiveInteger() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('+123')"));
    [TestMethod] public void Decimal() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('1.5')"));
    [TestMethod] public void ScientificNotation_e() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('1e10')"));
    [TestMethod] public void ScientificNotation_e_LargeExp() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('1e308')"));
    [TestMethod] public void ScientificNotation_d() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('1d10')"));
    [TestMethod] public void ScientificNotation_SignedExponent() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('1e+10')"));
    [TestMethod] public void Currency() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('$1')"));
    [TestMethod] public void CurrencyDecimal() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('$1.50')"));
    [TestMethod] public void NonDollarCurrency() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC(N'£1')"));
    [TestMethod] public void SignThenCurrency() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('-$1')"));
    [TestMethod] public void CurrencyThenSign() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('$-1')"));

    /// <summary>Quirky probe-confirmed acceptance: a bare sign returns 1.</summary>
    [TestMethod] public void SignAlone() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('-')"));

    /// <summary>Quirky probe-confirmed acceptance: a bare decimal point returns 1.</summary>
    [TestMethod] public void DecimalPointAlone() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('.')"));

    [TestMethod] public void CommaAlone() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC(',')"));
    [TestMethod] public void CurrencyAlone() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('$')"));

    [TestMethod] public void SpaceAlone_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC(' ')"));
    [TestMethod] public void EmptyString_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC('')"));
    [TestMethod] public void Null_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC(NULL)"));

    [TestMethod] public void ExponentWithoutLeadingDigit_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC('e10')"));
    [TestMethod] public void ExponentWithoutTrailingDigit_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC('1e')"));
    [TestMethod] public void HexPrefix_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC('0x10')"));
    [TestMethod] public void InternalWhitespace_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC(' 1 2 ')"));
    [TestMethod] public void TrailingNonDigit_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC('1L')"));
    [TestMethod] public void FExponentRejected() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC('1f10')"));

    [TestMethod] public void IntegerInput_ReturnsOne() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC(123)"));
    [TestMethod] public void FloatInput_ReturnsOne() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC(cast(1.5 as float))"));

    /// <summary>Probe-confirmed: bit is the one numeric-category type that returns 0.</summary>
    [TestMethod] public void BitInput_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISNUMERIC(cast(1 as bit))"));

    [TestMethod] public void LeadingAndTrailingWhitespace_TrimmedAccepted() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('   123   ')"));
    [TestMethod] public void ThousandsSeparated_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISNUMERIC('1,000')"));
}
