using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SOUNDEX(s)</c>, <c>DIFFERENCE(a, b)</c>, and
/// <c>STR(num, [length, [decimals]])</c> — the remaining String-category
/// scalars. SOUNDEX is the standard 4-character English phonetic code;
/// DIFFERENCE returns 0-4 comparing two SOUNDEX codes; STR formats a
/// float right-aligned in a fixed-width string.
/// </summary>
[TestClass]
public sealed class SoundexStrTests
{
    [TestMethod]
    public void Soundex_Smith_S530()
        => AreEqual("S530", new Simulation().ExecuteScalar("select soundex('Smith')"));

    [TestMethod]
    public void Soundex_Smyth_S530()
        => AreEqual("S530", new Simulation().ExecuteScalar("select soundex('Smyth')"));

    [TestMethod]
    public void Soundex_Williams_W452()
        => AreEqual("W452", new Simulation().ExecuteScalar("select soundex('Williams')"));

    [TestMethod]
    public void Soundex_Empty_AllZeros()
        => AreEqual("0000", new Simulation().ExecuteScalar("select soundex('')"));

    [TestMethod]
    public void Soundex_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select soundex(cast(null as varchar(10)))"));

    [TestMethod]
    public void Difference_SmithSmyth_Returns4()
        => AreEqual(4, new Simulation().ExecuteScalar("select difference('Smith', 'Smyth')"));

    [TestMethod]
    public void Difference_NullSide_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select difference(cast(null as varchar(10)), 'foo')"));

    [TestMethod]
    public void Str_DefaultArgs_Width10NoDecimals()
        => AreEqual("       123", new Simulation().ExecuteScalar("select str(123.456)"));

    [TestMethod]
    public void Str_WithDecimals_RoundsHalfUp()
        => AreEqual("123.46", new Simulation().ExecuteScalar("select str(123.456, 6, 2)"));

    [TestMethod]
    public void Str_WithZeroDecimals_RoundsToInt()
        => AreEqual("   123", new Simulation().ExecuteScalar("select str(123.456, 6, 0)"));

    [TestMethod]
    public void Str_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select str(cast(null as float))"));

    [TestMethod]
    [DataRow("str(1, 0)")]
    [DataRow("str(1, -1)")]
    [DataRow("str(1, 8001)")]
    [DataRow("str(1, 5, -1)")]
    [DataRow("str(1, null)")]
    public void Str_LengthOutOfRangeOrNegativeDecimals_ReturnsNull(string call)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select {call}"));

    [TestMethod]
    public void Str_NullDecimals_ReadsAsZero()
        => AreEqual("    1", new Simulation().ExecuteScalar("select str(1.4, 5, null)"));

    /// <summary>
    /// The decimals shrink to fit the room the <em>unrounded</em> integer part
    /// leaves, and are capped at 16; so a value that rounds up a digit
    /// overflows rather than dropping its decimal.
    /// </summary>
    [TestMethod]
    [DataRow("str(0.1, 20, 17)", "  0.1000000000000000")]
    [DataRow("str(123.456, 10, 16)", "123.456000")]
    [DataRow("str(123.456, 5, 3)", "123.5")]
    [DataRow("str(123.456, 4, 3)", " 123")]
    [DataRow("str(-123.456, 5, 3)", " -123")]
    [DataRow("str(0.4, 2, 1)", " 0")]
    [DataRow("str(99.99, 4, 1)", "****")]
    [DataRow("str(9.5, 1)", "*")]
    public void Str_DecimalsFitTheWidth(string call, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {call}"));

    /// <summary>
    /// The double's exact value is truncated to 17 significant digits and then
    /// rounded half away from zero, so <c>2.675</c> (really 2.67499…) rounds
    /// down, an exact half rounds up, and a tie at the 17th digit truncates.
    /// </summary>
    [TestMethod]
    [DataRow("str(2.675, 5, 2)", " 2.67")]
    [DataRow("str(0.45, 4, 1)", " 0.5")]
    [DataRow("str(2.5, 1)", "3")]
    [DataRow("str(-2.5, 4)", "  -3")]
    [DataRow("str(-0.4, 2)", "-0")]
    [DataRow("str(-cast(0 as float), 4)", "  -0")]
    [DataRow("str(-cast(0 as float), 1)", "*")]
    [DataRow("str(0.123456789012345678, 20, 16)", "  0.1234567890123457")]
    [DataRow("str(12345678901234567890.0, 25, 5)", "12345678901234567000.0000")]
    [DataRow("str(1234567890123456.75e0, 25, 5)", "   1234567890123456.70000")]
    [DataRow("str(1e308, 5)", "*****")]
    public void Str_RoundsTheExactValueToSeventeenDigits(string call, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {call}"));

    /// <summary>
    /// H and W separate a run of equal codes as a vowel does, and a non-letter
    /// ends the code.
    /// </summary>
    [TestMethod]
    [DataRow("Ashcraft", "A226")]
    [DataRow("Burroughs", "B622")]
    [DataRow("Schwartz", "S632")]
    [DataRow("A-hc", "A000")]
    [DataRow("Awwc", "A200")]
    public void Soundex_SqlServerRules(string input, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select soundex('{input}')"));
}
