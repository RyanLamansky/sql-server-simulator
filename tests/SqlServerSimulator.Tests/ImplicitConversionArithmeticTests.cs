using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server implicitly converts a low-precedence <c>varchar</c>/string
/// operand to the type an expression needs. Three positions where the
/// simulator previously errored instead, each value-matched against
/// SQL Server 2025:
/// <list type="bullet">
/// <item>a string date argument to DATEDIFF / DATEPART / DATENAME / DATEADD
/// (implicit string → datetime2, bare time anchored to 1900-01-01);</item>
/// <item>a string operand in decimal / money / float / int arithmetic
/// (string adopts the numeric partner's type; modulo against a non-integer
/// numeric stays Msg 402, a non-numeric string still raises its conversion
/// error);</item>
/// <item>a DATEADD interval exceeding int32 (handled as bigint — only an
/// out-of-range result raises Msg 517).</item>
/// </list>
/// </summary>
[TestClass]
public sealed class ImplicitConversionArithmeticTests
{
    // Bug A — string date argument to the date functions.

    [TestMethod]
    public void DateDiff_StringTimeArgument_CoercesAndDiffsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("SELECT DATEDIFF(second, '11:15:00', CAST('11:15:00' AS time))"));

    [TestMethod]
    public void DateDiffBig_StringTimeArgument_CoercesAndDiffsZero()
        => AreEqual(0L, new Simulation().ExecuteScalar("SELECT DATEDIFF_BIG(second, '11:15:00', CAST('11:15:00' AS time))"));

    [TestMethod]
    public void DatePart_MicrosecondOfStringTime_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("SELECT DATEPART(microsecond, '11:15:00')"));

    [TestMethod]
    public void DateName_MicrosecondOfStringTime_IsZeroText()
        => AreEqual("0", new Simulation().ExecuteScalar("SELECT DATENAME(microsecond, '11:15:00')"));

    [TestMethod]
    public void DateAdd_SecondToStringTime_AnchorsTo1900()
        => AreEqual(new DateTime(1900, 1, 1, 11, 15, 1), new Simulation().ExecuteScalar("SELECT DATEADD(second, 1, '11:15:00')"));

    [TestMethod]
    public void Cast_BareTimeString_ToDateTime2_AnchorsTo1900()
        => AreEqual(new DateTime(1900, 1, 1, 11, 15, 0), new Simulation().ExecuteScalar("SELECT CAST('11:15:00' AS datetime2)"));

    [TestMethod]
    public void DateDiff_ParameterizedStringTimeArgument_CoercesAndDiffsZero()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand(
            "SELECT DATEDIFF(second, @p, CAST('11:15:00' AS time))", ("@p", "11:15:00"));
        AreEqual(0, command.ExecuteScalar());
    }

    // Bug B — string operand in numeric arithmetic.

    [TestMethod]
    public void Decimal_MinusStringLiteral_ConvertsStringToDecimal()
        => AreEqual(10.10m, new Simulation().ExecuteScalar("SELECT CAST(10.5 AS decimal(10,2)) - '0.4'"));

    [TestMethod]
    public void StringLiteral_PlusDecimal_ConvertsStringToDecimal()
        => AreEqual(1.40m, new Simulation().ExecuteScalar("SELECT '0.4' + CAST(1 AS decimal(10,2))"));

    [TestMethod]
    public void Decimal_MinusStringParameter_ConvertsStringToDecimal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("SELECT CAST(10.5 AS decimal(10,2)) - @p", ("@p", "0.4"));
        AreEqual(10.10m, command.ExecuteScalar());
    }

    [TestMethod]
    public void StringParameter_MinusDecimal_ConvertsStringToDecimal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("SELECT @p - CAST(10.5 AS decimal(10,2))", ("@p", "0.4"));
        AreEqual(-10.10m, command.ExecuteScalar());
    }

    [TestMethod]
    public void StringLiteral_TimesFloat_ConvertsStringToFloat()
        => AreEqual(6.0, new Simulation().ExecuteScalar("SELECT '3' * CAST(2.0 AS float)"));

    [TestMethod]
    public void StringLiteral_DividedByDecimal_MatchesDivisionScale()
        => AreEqual(2.5m, new Simulation().ExecuteScalar("SELECT '10' / CAST(4 AS decimal(10,2))"));

    [TestMethod]
    public void Money_PlusStringLiteral_ConvertsStringToMoney()
        => AreEqual(3.5m, new Simulation().ExecuteScalar("SELECT CAST(1 AS money) + '2.5'"));

    [TestMethod]
    public void Int_PlusStringLiteral_ConvertsStringToInt()
        => AreEqual(10, new Simulation().ExecuteScalar("SELECT CAST(7 AS int) + '3'"));

    [TestMethod]
    public void Modulo_StringAgainstDecimal_RaisesIncompatibleTypes()
        => new Simulation().AssertSqlError("SELECT '5' % CAST(3 AS decimal(10,2))", 402,
            "The data types varchar and decimal are incompatible in the modulo operator.");

    [TestMethod]
    public void NonNumericString_MinusDecimal_RaisesNumericConversionError()
        => new Simulation().AssertSqlError("SELECT 'abc' - CAST(1 AS decimal(10,2))", 8114);

    [TestMethod]
    public void NonNumericString_TimesFloat_RaisesFloatConversionError()
        => new Simulation().AssertSqlError("SELECT 'abc' * CAST(2 AS float)", 8114);

    [TestMethod]
    public void NonNumericString_MinusInt_RaisesIntConversionError()
        => new Simulation().AssertSqlError("SELECT 'abc' - 1", 245);

    // Bug C — DATEADD interval exceeding int32.

    [TestMethod]
    public void DateAdd_SecondIntervalPastInt32_LandsInRange()
        => AreEqual(new DateTime(2092, 1, 19, 3, 14, 8),
            new Simulation().ExecuteScalar("SELECT DATEADD(second, 2147483648, CAST('2024-01-01' AS datetime2))"));

    [TestMethod]
    public void DateAdd_BigintSecondInterval_LandsInRange()
        => AreEqual(new DateTime(2309, 3, 14, 15, 59, 59),
            new Simulation().ExecuteScalar("SELECT DATEADD(second, CAST(8999999999999999 AS bigint) / 1000000, CAST('2024-01-01' AS datetime2))"));

    [TestMethod]
    public void DateAdd_BigintMillisecondInterval_LandsInRange()
        => AreEqual(new DateTime(2340, 11, 20, 17, 46, 39, 999),
            new Simulation().ExecuteScalar("SELECT DATEADD(millisecond, CAST(9999999999999 AS bigint), CAST('2024-01-01' AS datetime2))"));

    [TestMethod]
    public void DateAdd_SecondIntervalPastInt32_LegacyDateTime_LandsInRange()
        => AreEqual(new DateTime(2092, 1, 19, 3, 14, 8),
            new Simulation().ExecuteScalar("SELECT DATEADD(second, 2147483648, CAST('2024-01-01 00:00:00' AS datetime))"));

    [TestMethod]
    public void DateAdd_IntervalOverflowingDate_RaisesMsg517()
        => new Simulation().AssertSqlError("SELECT DATEADD(day, CAST(4000000 AS bigint), CAST('2024-01-01' AS datetime2))", 517);

    [TestMethod]
    public void DateAdd_HugeSecondInterval_RaisesMsg517()
        => new Simulation().AssertSqlError("SELECT DATEADD(second, 9999999999999, CAST('2024-01-01' AS datetime2))", 517);
}
