using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which error an exact-numeric value's conversion reports, and at which state.
/// SQL Server splits this surface finely — the source family picks the message
/// and the target picks the state — and every row below was probed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ExactNumericConversionErrorTests
{
    private static void AssertError(string expression, int number, byte state, string message)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}", number);
        AreEqual(state, ex.State);
        AreEqual(message, ex.Message);
    }

    // --- Character source: the state splits on the text's own width ---

    /// <summary>
    /// Text carrying more than 38 digits reports state 6 whatever the target,
    /// since it outran <c>numeric</c>'s own domain rather than the declared
    /// precision.
    /// </summary>
    [TestMethod]
    [DataRow("cast('123456789012345678901234567890123456789' as decimal(38, 0))")]
    [DataRow("cast('123456789012345678901234567890123456789' as decimal(10, 0))")]
    [DataRow("cast('0.999999999999999999999999999999999999995' as decimal(38, 38))")]
    [DataRow("cast('12345678901234567890123456789012345678.9' as decimal(38, 1))")]
    [DataRow("cast('1.00500000000000000000000000000000000000' as decimal(38, 38))")]
    public void StringWiderThanNumericsDomain_State6(string expression)
        => AssertError(expression, 8115, 6, "Arithmetic overflow error converting varchar to data type numeric.");

    /// <summary>
    /// Text inside 38 digits reports state 8 even where restating it at the
    /// target's scale would need 39 — the text, not the rescaled value, is
    /// what the state reads.
    /// </summary>
    [TestMethod]
    [DataRow("cast('1.005' as decimal(38, 38))")]
    [DataRow("cast('12.5' as decimal(38, 38))")]
    [DataRow("cast('99.5' as decimal(2, 0))")]
    [DataRow("cast('1234.5' as decimal(5, 2))")]
    [DataRow("cast('000000000000000000000000000000000000000001' as decimal(38, 38))")]
    [DataRow("cast('99999999999999999999999999999999999999' as decimal(38, 1))")]
    public void StringInsideNumericsDomain_State8(string expression)
        => AssertError(expression, 8115, 8, "Arithmetic overflow error converting varchar to data type numeric.");

    // --- Exact-numeric source ---

    /// <summary>
    /// A <c>numeric</c> source runs the split on the rescaled value instead:
    /// state 6 past 38 digits, state 8 inside them.
    /// </summary>
    [TestMethod]
    public void DecimalNeedingMoreThanThirtyEightDigits_State6()
        => AssertError(
            "cast(cast(370.93049074045129 as decimal(18, 14)) as decimal(38, 37))",
            8115, 6, "Arithmetic overflow error converting numeric to data type numeric.");

    [TestMethod]
    public void DecimalPastTheDeclaredPrecisionOnly_State8()
        => AssertError(
            "cast(cast(123456 as decimal(9, 0)) as decimal(5, 0))",
            8115, 8, "Arithmetic overflow error converting numeric to data type numeric.");

    [TestMethod]
    public void DecimalIntoATooShortAnsiString_State5()
        => AssertError(
            "cast(cast(123456789 as decimal(9, 0)) as varchar(5))",
            8115, 5, "Arithmetic overflow error converting numeric to data type varchar.");

    /// <summary>The Unicode targets take the generic "expression" wording.</summary>
    [TestMethod]
    public void DecimalIntoATooShortUnicodeString_State2()
        => AssertError(
            "cast(cast(123456789 as decimal(9, 0)) as nvarchar(5))",
            8115, 2, "Arithmetic overflow error converting expression to data type nvarchar.");

    [TestMethod]
    [DataRow("money", "cast('123456789012345678901234' as decimal(38, 0))")]
    [DataRow("smallmoney", "cast(1000000.5 as decimal(10, 1))")]
    public void DecimalIntoANarrowerMoney_State4(string target, string source)
        => AssertError(
            $"cast({source} as {target})",
            8115, 4, $"Arithmetic overflow error converting numeric to data type {target}.");

    // --- The other source families reaching money ---

    [TestMethod]
    [DataRow("money")]
    [DataRow("smallmoney")]
    public void BigintIntoMoney_NamesTheExpression(string target)
        => AssertError(
            $"cast(cast(9223372036854775807 as bigint) as {target})",
            8115, 2, $"Arithmetic overflow error converting expression to data type {target}.");

    [TestMethod]
    public void StringIntoMoney_NamesTheExpression()
        => AssertError(
            "cast('999999999999999999' as money)",
            8115, 2, "Arithmetic overflow error converting expression to data type money.");

    [TestMethod]
    public void IntIntoSmallMoney_CarriesTheValue()
        => AssertError(
            "cast(cast(1000000 as int) as smallmoney)",
            220, 3, "Arithmetic overflow error for data type smallmoney, value = 1000000.");

    [TestMethod]
    public void MoneyIntoSmallMoney_ReportsInsufficientSpace()
        => AssertError(
            "cast(cast(1000000 as money) as smallmoney)",
            237, 3, "There is insufficient result space to convert a money value to smallmoney.");

    /// <summary>
    /// <c>float</c> / <c>real</c> reach <c>money</c> like any other numeric
    /// source, reading the operand's exact binary value at scale 4.
    /// </summary>
    [TestMethod]
    [DataRow("cast(cast(1.5e0 as float) as money)")]
    [DataRow("cast(cast(1.5e0 as real) as money)")]
    [DataRow("cast(cast(1.5e0 as float) as smallmoney)")]
    public void FloatIntoMoney_Converts(string expression)
        => AreEqual(1.5m, new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>
    /// An approximate source's overflow carries the value at seventeen
    /// significant digits, so a magnitude past that shows zeros rather than the
    /// double's own binary tail.
    /// </summary>
    [TestMethod]
    [DataRow("cast(cast(1e30 as float) as money)", "money")]
    [DataRow("cast(cast(1e30 as float) as smallmoney)", "smallmoney")]
    [DataRow("cast(cast(1e30 as float) as int)", "int")]
    public void FloatOverflow_CarriesSeventeenSignificantDigits(string expression, string target)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}", 232);
        AreEqual(
            $"Arithmetic overflow error for type {target}, value = 1000000000000000000000000000000.000000.",
            ex.Message);
    }

    [TestMethod]
    public void RealOverflowIntoMoney_CarriesSeventeenSignificantDigits()
        => AssertError(
            "cast(cast(1e20 as real) as money)",
            232, 2, "Arithmetic overflow error for type money, value = 100000002004087730000.000000.");

    // --- Arithmetic ---

    /// <summary>
    /// Both modulo operands are aligned at the result's scale before the
    /// remainder is taken, so an operand needing more than 38 digits there
    /// overflows however small the remainder itself would have been.
    /// </summary>
    [TestMethod]
    [DataRow("cast('99' as decimal(38, 0)) % cast('0.1' as decimal(38, 37))")]
    [DataRow("cast('1' as decimal(38, 0)) % cast('0.1' as decimal(38, 38))")]
    [DataRow("cast('-62455.937562995123860566485271343918' as decimal(38, 30)) % cast('2928715362.6' as decimal(17, 1))")]
    public void ModuloOperandPastTheAlignedWidth_RaisesMsg8115(string expression)
        => AssertError(expression, 8115, 2, "Arithmetic overflow error converting expression to data type numeric.");

    [TestMethod]
    [DataRow("cast('1' as decimal(38, 0)) % cast('0.1' as decimal(38, 37))", "0.0000000000000000000000000000000000000")]
    [DataRow("cast('0.9' as decimal(38, 38)) % cast('0.1' as decimal(2, 1))", "0.00000000000000000000000000000000000000")]
    [DataRow("cast('100000.0' as decimal(10, 1)) % cast('3.00000' as decimal(10, 5))", "1.00000")]
    public void ModuloInsideTheAlignedWidth_Answers(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast({expression} as varchar(60))"));

    /// <summary>
    /// <c>ROUND</c> settles back into the argument's own declared precision, so
    /// a carry out of it overflows.
    /// </summary>
    [TestMethod]
    [DataRow("round(cast(7.2 as decimal(2, 1)), -1)")]
    [DataRow("round(cast(94.5 as decimal(3, 1)), -2)")]
    [DataRow("round(cast(7.21452767131 as decimal(12, 11)), -1)")]
    public void RoundCarryingPastTheArgumentsPrecision_RaisesMsg8115(string expression)
        => AssertError(expression, 8115, 2, "Arithmetic overflow error converting expression to data type numeric.");

    [TestMethod]
    [DataRow("round(cast(7.2 as decimal(3, 1)), -1)", "10.0")]
    [DataRow("round(cast(4.2 as decimal(2, 1)), -1)", "0.0")]
    public void RoundInsideTheArgumentsPrecision_Answers(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast({expression} as varchar(60))"));
}
