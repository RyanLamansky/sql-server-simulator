using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>decimal</c> / <c>numeric</c> past a .NET <see cref="decimal"/>'s 28-29
/// significant digits. SQL Server carries 38 and so does the simulator's
/// storage type, so every value here computes, stores and renders whatever the
/// real server answers; the one lossy edge is the client boundary, which is
/// real SqlClient's own.
/// <para>
/// Every expectation below was probed against SQL Server 2025.
/// </para>
/// </summary>
[TestClass]
public sealed class WideDecimalTests
{
    /// <summary>
    /// Renders through <c>varchar</c>, which is how a value past a .NET
    /// <see cref="decimal"/> is read: the client accessors shed or raise, and
    /// that is the behavior <see cref="ReaderShedsTrailingZerosToFit"/> covers.
    /// </summary>
    private static string Text(string expression) =>
        (string)new Simulation().ExecuteScalar($"select cast({expression} as varchar(60))")!;

    // --- Literals ---

    [TestMethod]
    public void ThirtyEightDigitLiteral_KeepsEveryDigit() =>
        AreEqual("12345678901234567890123456789012345678", Text("12345678901234567890123456789012345678"));

    /// <summary>Wider than <c>numeric</c>'s own 38 digits is real's Msg 1007, at parse.</summary>
    [TestMethod]
    public void LiteralPastNumericsOwnMaximum_Msg1007() =>
        new Simulation().AssertSqlError("select 1234567890123456789012345678901234567890", 1007);

    [TestMethod]
    public void FractionalLiteralPastNumericsOwnMaximum_Msg1007() =>
        new Simulation().AssertSqlError("select 1.000000000000000000000000000000000000005", 1007);

    // --- Declared scale ---

    [TestMethod]
    public void DeclaredScaleThirty_CarriesAllThirtyZeros() =>
        AreEqual("1.000000000000000000000000000000", Text("cast(1 as numeric(38, 30))"));

    [TestMethod]
    public void DeclaredScaleThirtyEight_CarriesAllThirtyEightDigits() =>
        AreEqual("0.50000000000000000000000000000000000000", Text("cast(0.5 as decimal(38, 38))"));

    [TestMethod]
    public void WideScaleSurvivesStorage()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table w (v decimal(38, 30));
            insert w values (1), (cast('0.123456789012345678901234567890' as decimal(38, 30)))
            """);
        AreEqual(
            "1.000000000000000000000000000000|0.123456789012345678901234567890",
            (string)sim.ExecuteScalar("select string_agg(cast(v as varchar(60)), '|') from w")!);
    }

    // --- Conversion ---

    [TestMethod]
    public void ThirtyDigitStringConverts() =>
        AreEqual("123456789012345678901234567890", Text("cast('123456789012345678901234567890' as decimal(38, 0))"));

    [TestMethod]
    public void TwentyNineDigitStringScalesUp() =>
        AreEqual(
            "1234567890123456789012345678.9000000000",
            Text("cast('1234567890123456789012345678.9' as decimal(38, 10))"));

    /// <summary>A string carrying more than 38 digits is real's Msg 8115 at state 6.</summary>
    [TestMethod]
    public void StringPastNumericsOwnMaximum_Msg8115() =>
        new Simulation().AssertSqlError(
            "select cast('1234567890123456789012345678901234567890' as decimal(38, 0))",
            8115,
            "Arithmetic overflow error converting varchar to data type numeric.");

    [TestMethod]
    public void StringWiderThanTheDeclaredTarget_Msg8115() =>
        new Simulation().AssertSqlError(
            "select cast('123456789012345678901234567890' as decimal(20, 0))",
            8115,
            "Arithmetic overflow error converting varchar to data type numeric.");

    /// <summary>
    /// Real's <c>varchar</c> → <c>numeric</c> grammar takes no exponent, so
    /// scientific notation is Msg 8114 whatever its magnitude.
    /// </summary>
    [TestMethod]
    [DataRow("'1e5'")]
    [DataRow("'1.5e2'")]
    [DataRow("'1e40'")]
    [DataRow("'abc'")]
    public void ScientificNotationAndNonNumbers_Msg8114(string literal) =>
        new Simulation().AssertSqlError($"select cast({literal} as decimal(38, 0))", 8114);

    /// <summary>
    /// A 40-significant-digit string still converts when the rounded value
    /// fits — real judges the digit count after the rounding.
    /// </summary>
    [TestMethod]
    public void FortyDigitStringRoundingIntoRange_Converts() =>
        AreEqual("1.00", Text("cast('1.0000000000000000000000000000000000000005' as decimal(38, 2))"));

    [TestMethod]
    public void TryCastOfAWideString_ReturnsTheValueRatherThanNull() =>
        AreEqual("123456789012345678901234567890", Text("try_cast('123456789012345678901234567890' as decimal(38, 0))"));

    /// <summary>
    /// <c>float</c> → <c>numeric</c> reads the operand's exact binary value,
    /// which is what puts real's <c>…19884624838656</c> tail on <c>1e30</c>.
    /// </summary>
    [TestMethod]
    public void FloatConvertsFromItsExactBinaryValue() =>
        AreEqual("1000000000000000019884624838656", Text("cast(cast(1e30 as float) as decimal(38, 0))"));

    // --- Arithmetic ---

    [TestMethod]
    public void WideAddition() =>
        AreEqual(
            "100000000000000000000000000000",
            Text("cast(50000000000000000000000000000 as decimal(38, 0)) + cast(50000000000000000000000000000 as decimal(38, 0))"));

    [TestMethod]
    public void WideSubtraction() =>
        AreEqual(
            "99999999999999999999999999999999999998",
            Text("cast(99999999999999999999999999999999999999 as decimal(38, 0)) - 1"));

    [TestMethod]
    public void WideMultiplication() =>
        AreEqual(
            "1524157875323883675019051998750190521",
            Text("cast(1234567890123456789 as decimal(19, 0)) * cast(1234567890123456789 as decimal(19, 0))"));

    /// <summary>
    /// Division truncates toward zero at the result scale, so the exact
    /// <c>0.12499999887…</c> keeps its six digits rather than rounding to
    /// <c>0.125000</c>.
    /// </summary>
    [TestMethod]
    public void WideDivisionTruncatesRatherThanRounding() =>
        AreEqual("0.124999", Text("""
            cast(12345678901234567890123456789012345678 as decimal(38, 0))
                / cast(98765432109876543210987654321098765432 as decimal(38, 0))
            """));

    [TestMethod]
    public void WideDivisionTheOtherWay() =>
        AreEqual("8.000000", Text("""
            cast(98765432109876543210987654321098765432 as decimal(38, 0))
                / cast(12345678901234567890123456789012345678 as decimal(38, 0))
            """));

    [TestMethod]
    public void WideModulo() =>
        AreEqual("900000000090000000009000000008", Text("""
            cast(98765432109876543210987654321098765432 as decimal(38, 0))
                % cast(12345678901234567890123456789012345678 as decimal(38, 0))
            """));

    /// <summary>Past 38 digits is real's own arithmetic overflow — Msg 8115 at state 2.</summary>
    [TestMethod]
    public void ArithmeticPastNumericsOwnMaximum_Msg8115() =>
        new Simulation().AssertSqlError(
            "select cast(99999999999999999999999999999999999999 as decimal(38, 0)) * 10",
            8115,
            "Arithmetic overflow error converting expression to data type numeric.");

    // --- Aggregates ---

    [TestMethod]
    public void SumPastNinetySixBits()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table s (v decimal(38, 0));
            insert s values (50000000000000000000000000000), (50000000000000000000000000000)
            """);
        AreEqual("100000000000000000000000000000", (string)sim.ExecuteScalar("select cast(sum(v) as varchar(60)) from s")!);
        AreEqual("50000000000000000000000000000.000000", (string)sim.ExecuteScalar("select cast(avg(v) as varchar(60)) from s")!);
    }

    /// <summary>
    /// A total real itself can't carry is real's own Msg 8115 at state 2,
    /// naming <c>numeric</c> the way a bare <c>+</c> of the same values does.
    /// </summary>
    [TestMethod]
    public void SumPastNumericsOwnMaximum_Msg8115() =>
        new Simulation().AssertSqlError(
            """
            create table s (v decimal(38, 0));
            insert s values (99999999999999999999999999999999999999), (99999999999999999999999999999999999999);
            select sum(v) from s
            """,
            8115,
            "Arithmetic overflow error converting expression to data type numeric.");

    // --- Client boundary ---

    /// <summary>
    /// SqlClient's own reader sheds trailing fractional zeros to make a value
    /// fit a .NET <see cref="decimal"/>, silently — so
    /// <c>decimal(38, 30)</c> holding 1 reaches <c>GetDecimal</c> at scale 28.
    /// </summary>
    [TestMethod]
    public void ReaderShedsTrailingZerosToFit()
    {
        var value = (decimal)new Simulation().ExecuteScalar("select cast(1 as decimal(38, 30))")!;
        AreEqual(1m, value);
        AreEqual(28, value.Scale);
    }

    [TestMethod]
    public void ReaderKeepsAValueThatFitsWithNothingShed() =>
        AreEqual(
            79228162514264337593543950335m,
            new Simulation().ExecuteScalar("select cast('79228162514264337593543950335' as decimal(38, 0))"));

    /// <summary>
    /// One past <see cref="decimal.MaxValue"/> with no trailing zeros to give
    /// up is where SqlClient raises, and so does the simulator's reader.
    /// </summary>
    [TestMethod]
    [DataRow("cast('79228162514264337593543950336' as decimal(38, 0))")]
    [DataRow("cast('12345678901234567890123456789.5' as decimal(38, 1))")]
    [DataRow("cast('0.123456789012345678901234567890123456789' as decimal(38, 38))")]
    public void ReaderRaisesWhenNothingCanBeShed(string expression)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand($"select {expression}");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        var ex = Throws<OverflowException>(() => reader.GetDecimal(0));
        AreEqual("Conversion overflows.", ex.Message);
    }

    [TestMethod]
    public void ReaderReportsSystemDecimalWhateverTheWidth()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select cast(1 as decimal(38, 30))");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(decimal), reader.GetFieldType(0));
    }

    // --- FORMAT ---

    private const string Wide = "cast('12345678901234567890123456789012345678' as decimal(38, 0))";
    private const string WideFraction = "cast('1234567890123456789012345678.9' as decimal(38, 1))";
    private const string WideUnit = "cast('0.99999999999999999999999999999999999999' as decimal(38, 38))";

    /// <summary>
    /// <c>FORMAT</c> lays out all 38 digits, grouping, rounding and decorating
    /// them the way the culture asks — the value never crosses to a narrower
    /// type on its way to the string.
    /// </summary>
    [TestMethod]
    [DataRow(Wide, "N0", "12,345,678,901,234,567,890,123,456,789,012,345,678")]
    [DataRow(Wide, "N2", "12,345,678,901,234,567,890,123,456,789,012,345,678.00")]
    [DataRow(Wide, "F0", "12345678901234567890123456789012345678")]
    [DataRow(Wide, "G", "12345678901234567890123456789012345678")]
    [DataRow(Wide, "G40", "12345678901234567890123456789012345678")]
    [DataRow(Wide, "G1", "1E+37")]
    [DataRow(Wide, "G5", "1.2346E+37")]
    [DataRow(Wide, "G20", "1.234567890123456789E+37")]
    [DataRow(Wide, "g5", "1.2346e+37")]
    [DataRow(Wide, "0000", "12345678901234567890123456789012345678")]
    [DataRow(Wide, "#,#", "12,345,678,901,234,567,890,123,456,789,012,345,678")]
    [DataRow(WideUnit, "G5", "1")]
    [DataRow(WideUnit, "G1", "1")]
    [DataRow(Wide, "C", "$12,345,678,901,234,567,890,123,456,789,012,345,678.00")]
    [DataRow(Wide, "E", "1.234568E+037")]
    [DataRow(Wide, "E4", "1.2346E+037")]
    [DataRow(Wide, "#,##0.00", "12,345,678,901,234,567,890,123,456,789,012,345,678.00")]
    [DataRow(Wide, "0.###", "12345678901234567890123456789012345678")]
    [DataRow(Wide, "#", "12345678901234567890123456789012345678")]
    [DataRow(WideFraction, "N2", "1,234,567,890,123,456,789,012,345,678.90")]
    [DataRow(WideFraction, "G", "1234567890123456789012345678.9")]
    [DataRow(WideFraction, "#,##0.00", "1,234,567,890,123,456,789,012,345,678.90")]
    [DataRow(WideFraction, "0.###", "1234567890123456789012345678.9")]
    [DataRow(WideFraction, "E2", "1.23E+027")]
    [DataRow(WideUnit, "G", "0.99999999999999999999999999999999999999")]
    [DataRow(WideUnit, "N40", "0.9999999999999999999999999999999999999900")]
    [DataRow(WideUnit, "0.00", "1.00")]
    [DataRow(WideUnit, "#,##0.00", "1.00")]
    public void FormatOfAWideValue(string expression, string format, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select format({expression}, '{format}')"));

    [TestMethod]
    [DataRow(Wide, "N0", "12.345.678.901.234.567.890.123.456.789.012.345.678")]
    [DataRow(Wide, "N2", "12.345.678.901.234.567.890.123.456.789.012.345.678,00")]
    [DataRow(Wide, "C", "12.345.678.901.234.567.890.123.456.789.012.345.678,00 €")]
    [DataRow(Wide, "E", "1,234568E+037")]
    public void FormatOfAWideValueUnderAnotherCulture(string expression, string format, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select format({expression}, '{format}', 'de-DE')"));

    /// <summary>The specifiers .NET refuses on a fractional type answer NULL.</summary>
    [TestMethod]
    [DataRow("D")]
    [DataRow("X")]
    [DataRow("R")]
    public void FormatOfAWideValueUnderAnIntegerSpecifier_IsNull(string format)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select format({Wide}, '{format}')"));

    [TestMethod]
    public void FormatOfAWideValuePassesUnrecognizedTextThrough()
        => AreEqual("qq qq", new Simulation().ExecuteScalar($"select format({Wide}, 'qq qq')"));

    // --- PARSE ---

    [TestMethod]
    public void ParseReadsAllThirtyEightDigits()
        => AreEqual(
            "12345678901234567890123456789012345678",
            Text("parse('12345678901234567890123456789012345678' as decimal(38, 0))"));

    [TestMethod]
    public void ParseReadsAWideFraction()
        => AreEqual(
            "0.99999999999999999999999999999999999999",
            Text("parse('0.99999999999999999999999999999999999999' as decimal(38, 38))"));

    [TestMethod]
    public void ParseReadsTheCulturesGroupingAtFullWidth()
        => AreEqual(
            "99999999999999999999999999999999999999",
            Text("parse('99.999.999.999.999.999.999.999.999.999.999.999.999' as decimal(38, 0) using 'de-DE')"));

    /// <summary>
    /// Excess fractional digits round, but text carrying more than 38 digits is
    /// refused outright rather than rounded into range — which is where PARSE
    /// parts company with CAST.
    /// </summary>
    [TestMethod]
    public void ParseRoundsInsideThirtyEightDigits()
        => AreEqual(
            "12345678901234567890123456790",
            Text("parse('12345678901234567890123456789.5' as decimal(38, 0))"));

    [TestMethod]
    [DataRow("parse('12345678901234567890123456789012345678.5' as decimal(38, 0))")]
    [DataRow("parse('1.0000000000000000000000000000000000000005' as decimal(38, 2))")]
    [DataRow("parse('123456' as decimal(3, 0))")]
    public void ParseOfTextPastTheTarget_RaisesMsg9819(string expression)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}", 9819);
        Assert.Contains("into data type numeric using culture ''", ex.Message);
    }

    [TestMethod]
    public void TryParseOfTextPastTheTarget_IsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select try_parse('12345678901234567890123456789012345678.5' as decimal(38, 0))"));

    [TestMethod]
    public void TryParseReadsAllThirtyEightDigits()
        => AreEqual(
            "12345678901234567890123456789012345678",
            Text("try_parse('12345678901234567890123456789012345678' as decimal(38, 0))"));
}
