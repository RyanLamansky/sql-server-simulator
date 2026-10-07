using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>FORMAT</c> as SQL Server's .NET Framework formatter on Windows culture
/// data answers it, where .NET on ICU answers otherwise — probed 2026-09-29
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class FormatCultureTests
{
    [TestMethod]
    [DataRow("format(cast(-0.5 as decimal(5, 2)), 'C')", "($0.50)")]
    [DataRow("format(cast(123.456 as decimal(10, 3)), 'P')", "12,345.60%")]
    [DataRow("format(cast(1234 as int), 'N0', 'fr-FR')", "1 234")]
    [DataRow("format(cast(-1234567.891 as decimal(12, 3)), 'C', 'fr-FR')", "-1 234 567,89 €")]
    [DataRow("format(cast(1234567.891 as decimal(12, 3)), 'N', 'de-CH')", "1’234’567.89")]
    [DataRow("format(cast(1234567890 as bigint), 'N0', 'hi-IN')", "1,23,45,67,890")]
    [DataRow("format(cast(-1234567 as int), 'C', 'hi-IN')", "₹ -12,34,567.00")]
    [DataRow("format(cast(-1234.5 as decimal(6, 1)), 'N1', 'fa-IR')", "1,234/5-")]
    [DataRow("format(cast(1234 as int), 'C', 'ja-JP')", "¥1,234")]
    [DataRow("format(cast(1234 as int), 'P0', 'ar-SA')", "123,400 %")]
    public void Numbers_TakeWindowsCultureData(string expression, string expected) => AssertFormats(expression, expected);

    [TestMethod]
    [DataRow("format(cast('2009-11-23 13:45:56' as datetime2), 'g')", "11/23/2009 1:45 PM")]
    [DataRow("format(cast('2009-11-23' as date), 'gg')", "A.D.")]
    [DataRow("format(cast('2009-11-23 13:45:56' as datetime2), 'hh:mm tt', 'fr-FR')", "01:45 ")]
    [DataRow("format(cast('2009-11-23' as date), 'd', 'ar-SA')", "06/12/30")]
    [DataRow("format(cast('2009-11-23' as date), 'D', 'th-TH')", "23 พฤศจิกายน 2552")]
    [DataRow("format(cast('2009-11-23' as date), 'd', 'fa-IR')", "02/09/1388")]
    [DataRow("format(cast('2009-11-23 13:45:56' as datetime2), 'f', 'zh-TW')", "2009年11月23日 下午 01:45")]
    [DataRow("format(cast('2009-11-23' as date), 'MMMM', 'zh-TW')", "十一月")]
    [DataRow("format(cast('2009-11-23 13:45:56' as datetime2), 'U', 'ar-SA')", "23 نوفمبر, 2009 01:45:56 م")]
    [DataRow("format(cast('2024-12-31' as date), 'f')", "Tuesday, December 31, 2024 12:00 AM")]
    [DataRow("format(cast('1988-10-31 11:11:11.997' as datetime), 'O')", "1988-10-31T11:11:11.9970000")]
    [DataRow("format(cast('13:45:56.789' as time), 'g', 'fr-FR')", "13:45:56,789")]
    public void Dates_TakeWindowsCultureData(string expression, string expected) => AssertFormats(expression, expected);

    // 'U' of a culture on a non-Gregorian calendar switches to a Gregorian pattern with
    // the day names real gives it (probed 2026-09-29 against SQL Server 2025).
    [TestMethod]
    [DataRow("format(cast('2024-03-04 02:03:04' as datetime2), 'U', 'ckb-IR')", "2024 ئازار 4, دووشەممە 02:03:04")]
    [DataRow("format(cast('2024-03-08 14:07:09' as datetime2), 'U', 'ckb-IR')", "2024 ئازار 8, ھەینی 14:07:09")]
    [DataRow("format(cast('2024-01-04 02:03:04' as datetime2), 'U', 'lrc')", "2024 جانڤیە 4, Thu 02:03:04")]
    [DataRow("format(cast('2024-12-04 02:03:04' as datetime2), 'U', 'lrc-IR')", "2024 دئسامر 4, Wed 02:03:04")]
    [DataRow("format(cast('2024-03-10 14:07:09' as datetime2), 'U', 'mzn')", "2024 مارس 10, Sun 14:07:09")]
    [DataRow("format(cast('2024-06-04 02:03:04' as datetime2), 'U', 'mzn-IR')", "2024 ژوئن 4, Tue 02:03:04")]
    [DataRow("format(cast('2024-03-10 14:07:09' as datetime2), 'U', 'ps')", "يونۍ د 2024 د مارچ 10 14:07:09")]
    [DataRow("format(cast('2024-03-04 00:07:09' as datetime2), 'U', 'ps-AF')", "دونۍ د 2024 د مارچ 4 0:07:09")]
    [DataRow("format(cast('2024-09-04 02:03:04' as datetime2), 'U', 'ps')", "څلرنۍ د 2024 د سېپتمبر 4 2:03:04")]
    public void UniversalPattern_OfThePersianAndPashtoFamilies_IsGregorianWithItsOwnDayNames(string expression, string expected) => AssertFormats(expression, expected);

    [TestMethod]
    [DataRow("format(cast(1234.5 as money), 'C', 'qq-QQ')", "¤1,234.50")]
    [DataRow("format(cast(1234.5 as money), 'C', 'de-US')", "1.234,50 $")]
    [DataRow("format(cast(1234.5 as money), 'C', 'fr-QQ')", "1 234,50 ¤")]
    [DataRow("format(cast(1234.5 as money), 'C', 'en-DE-US')", "1.234,50 €")]
    [DataRow("format(cast(1234.5 as money), 'C', 'zh-CHS')", "¥1,234.50")]
    [DataRow("format(cast(1234.5 as money), 'C', 'en-US-POSIX')", "$1,234.50")]
    [DataRow("format(cast(1234.5 as money), 'C', 'en-Latn')", "¤1,234.50")]
    public void UnknownNames_TakeWhatWindowsSynthesizes(string expression, string expected) => AssertFormats(expression, expected);

    [TestMethod]
    [DataRow("en-POSIX")]
    [DataRow("en-aaa")]
    [DataRow("en-A")]
    [DataRow("en-US-ABCDEFGHI")]
    public void MalformedName_IsMsg9818(string culture)
        => _ = new Simulation().AssertSqlError($"select format(1, 'N', '{culture}')", 9818);

    [TestMethod]
    [DataRow("format(cast(0 as decimal(5, 0)), 'P')", "000.00%")]
    [DataRow("format(cast(0 as decimal(5, 2)), 'P')", "0.00%")]
    [DataRow("format(cast(0 as decimal(5, 0)), '#')", "0")]
    [DataRow("format(cast(0 as decimal(5, 2)), '#')", "")]
    [DataRow("format(cast(0 as decimal(5, 2)), '#.##%')", "0%")]
    [DataRow("format(cast(0 as decimal(5, 0)), '0.00‰')", "0000.00‰")]
    [DataRow("format(cast(0 as decimal(12, 5)), 'G5')", "0")]
    [DataRow("format(cast(123.456 as decimal(9, 3)), '0''x''0.00')", "12x0.00")]
    [DataRow("format(cast(123.456 as decimal(9, 3)), '''x''#,##0.00')", "x#,##0.00")]
    [DataRow("format(cast(-123.456 as decimal(9, 3)), '0.00;''y''0.00')", "y0.00")]
    [DataRow("format(cast(123 as bigint), '''x''0.00')", "x123.00")]
    [DataRow("format(cast(1 as int), 'R')", null)]
    [DataRow("format(cast(-1234567 as int), 'X')", "FFED2979")]
    [DataRow("format(cast(-1 as smallint), 'X')", "FFFF")]
    public void Decimals_TakeTheFrameworksLayout(string expression, string? expected) => AssertFormats(expression, expected);

    [TestMethod]
    [DataRow("format(cast(123456789012345678 as float), 'N0')", "123,456,789,012,346,000")]
    [DataRow("format(cast(0.125 as float), 'N2')", "0.13")]
    [DataRow("format(cast(2.5 as float), 'F0')", "3")]
    [DataRow("format(cast(-0.000123456 as float), 'N2')", "0.00")]
    [DataRow("format(cast(0.1 as float) + cast(0.2 as float), 'G')", "0.3")]
    [DataRow("format(cast(0.1 as float) + cast(0.2 as float), 'R')", "0.30000000000000004")]
    [DataRow("format(cast(1e15 as float), 'R')", "1E+15")]
    [DataRow("format(cast(16777217 as real), 'G')", "1.677722E+07")]
    [DataRow("format(cast(123456.789 as real), 'N3')", "123,456.800")]
    [DataRow("format(1.5e30, 'N0')", "1,500,000,000,000,000,000,000,000,000,000")]
    [DataRow("format(-7e36, 'N2')", "-7,000,000,000,000,000,000,000,000,000,000,000,000.00")]
    public void Floats_RoundAtFifteenDigitsFirst(string expression, string expected) => AssertFormats(expression, expected);

    [TestMethod]
    [DataRow("format(cast(12345678901234567890123456789012345678 as decimal(38, 0)), '0.0E+0')", "1.2E+37")]
    [DataRow("format(cast(12345678901234567890123456789012345678 as decimal(38, 0)), '#,##0.00E+00')", "1,234.57E+34")]
    [DataRow("format(cast(-98765432109876543210987654321.5 as decimal(38, 1)), '0.000E-0', 'de-DE')", "-9,877E28")]
    [DataRow("format(cast(0.00000000000000000000000000000000012345 as decimal(38, 38)), '0.00e-00')", "1.23e-34")]
    public void WideDecimal_TakesAScientificPattern(string expression, string expected) => AssertFormats(expression, expected);

    [TestMethod]
    public void OutOfCalendarRange_IsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select format(cast('0001-01-01' as datetime2), 'd', 'ar-SA')"));

    private static void AssertFormats(string expression, string? expected)
    {
        var result = new Simulation().ExecuteScalar($"select {expression}");
        AreEqual(expected, result is DBNull ? null : result);
    }
}
