using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>FORMAT(value, format [, culture])</c>. The
/// implementation routes through .NET's <see cref="IFormattable"/> on the
/// underlying CLR value — so .NET's format-token rules (and quirks)
/// determine whether the result is the format string echoed, a properly
/// formatted value, or NULL via <see cref="FormatException"/>.
/// </summary>
[TestClass]
public sealed class FormatTests
{
    [TestMethod]
    public void IntN0_ThousandsSeparated()
        => AreEqual("1,234,567", ExecuteScalar("select FORMAT(1234567, 'N0')"));

    [TestMethod]
    public void IntN2_TwoDecimalPlaces()
        => AreEqual("1,234,567.00", ExecuteScalar("select FORMAT(1234567, 'N2')"));

    [TestMethod]
    public void DecimalCurrency_EnUS()
        => AreEqual("$1,234.56", ExecuteScalar("select FORMAT(cast(1234.56 as decimal(10,2)), 'C', 'en-US')"));

    [TestMethod]
    public void DateCustomFormat()
        => AreEqual("2026-05-12", ExecuteScalar("select FORMAT(cast('2026-05-12' as date), 'yyyy-MM-dd')"));

    [TestMethod]
    public void NumberDeDE_DecimalCommaThousandsDot()
        => AreEqual("1.234,50", ExecuteScalar("select FORMAT(cast(1234.5 as decimal(10,2)), 'N2', 'de-DE')"));

    [TestMethod]
    public void NullValue_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select FORMAT(cast(NULL as int), 'N0')"));

    /// <summary>Probe-confirmed: NULL format raises Msg 8116 even with a NULL value side.</summary>
    [TestMethod]
    public void NullFormat_RaisesMsg8116()
        => AssertSqlError("select FORMAT(1234, NULL)", 8116);

    [TestMethod]
    public void StringInput_RaisesMsg8116()
        => AssertSqlError("select FORMAT('abc', 'N0')", 8116);

    [TestMethod]
    public void BitInput_RaisesMsg8116()
        => AssertSqlError("select FORMAT(cast(1 as bit), 'N0')", 8116);

    /// <summary>
    /// .NET's int.ToString accepts unrecognized custom-format strings by
    /// echoing them — matching SQL Server's documented passthrough.
    /// </summary>
    [TestMethod]
    public void UnrecognizedFormat_OnInt_Passthrough()
        => AreEqual("qq qq", ExecuteScalar("select FORMAT(1234, 'qq qq')"));

    /// <summary>
    /// .NET's decimal.ToString throws FormatException for the D specifier
    /// (decimals don't support D). SQL Server catches and returns NULL.
    /// </summary>
    [TestMethod]
    public void IncompatibleFormat_OnDecimal_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select FORMAT(cast(42 as decimal(10,0)), 'D5')"));

    [TestMethod]
    public void InvalidCulture_FallsBackToEnUS()
        => AreEqual("1,234", ExecuteScalar("select FORMAT(1234, 'N0', 'qq-QQ')"));

    [TestMethod]
    public void DateTimeCustomFormat()
        => AreEqual("12/05/2026 13:45", ExecuteScalar("select FORMAT(cast('2026-05-12T13:45:00' as datetime2), 'dd/MM/yyyy HH:mm')"));
}
