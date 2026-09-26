using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's string additions: two-argument <c>SUBSTRING</c>,
/// <c>UNISTR</c> and the <c>BASE64_ENCODE</c> / <c>BASE64_DECODE</c> pair.
/// Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class Sql2025StringFunctionTests
{
    private static string Text(string expression) =>
        Convert.ToString(new Simulation().ExecuteScalar($"select {expression}"), System.Globalization.CultureInfo.InvariantCulture)!;

    [TestMethod]
    [DataRow("substring('abcdef', 3)", "cdef")]
    [DataRow("substring('abcdef', 0)", "abcdef")]
    [DataRow("substring('abcdef', -2)", "abcdef")]
    [DataRow("substring('abcdef', 7)", "")]
    [DataRow("sql_variant_property(substring(cast('abcdef' as varchar(10)), 3), 'MaxLength')", "10")]
    [DataRow("isnull(substring('abc', null), 'N')", "N")]
    public void Substring_WithoutALength_ReadsToTheEnd(string expression, string expected)
        => AreEqual(expected, Text(expression));

    [TestMethod]
    [DataRow(@"unistr(N'a\\b')", @"a\b")]
    [DataRow(@"unistr(N'\0041\+01F600')", "A😀")]
    [DataRow("unistr(N'x#0041', N'#')", "xA")]
    [DataRow("sql_variant_property(unistr(N'ab'), 'BaseType')", "nvarchar")]
    [DataRow(@"sql_variant_property(unistr(cast('\0041' as varchar(10)) collate Latin1_General_100_CI_AS_SC_UTF8), 'BaseType')", "varchar")]
    public void Unistr_ReplacesItsEscapes(string expression, string expected)
        => AreEqual(expected, Text(expression));

    [TestMethod]
    [DataRow(@"unistr(N'\zz')", 9841, 1)]
    [DataRow(@"unistr(N'a\')", 9841, 3)]
    [DataRow("unistr(N'ab', N'a')", 9842, 0)]
    [DataRow("unistr(N'ab', N'##')", 9843, 4)]
    [DataRow(@"unistr('\0041')", 9844, 4)]
    public void Unistr_RefusesAsRealDoes(string expression, int number, int state)
        => AreEqual((byte)state, new Simulation().AssertSqlError($"select {expression}", number).State);

    [TestMethod]
    [DataRow("base64_encode(0xfbff)", "+/8=")]
    [DataRow("base64_encode(0xfbff, 1)", "-_8")]
    [DataRow("isnull(base64_encode(null), 'N')", "N")]
    [DataRow("convert(varchar(10), base64_decode('+/8='), 1)", "0xFBFF")]
    [DataRow("convert(varchar(10), base64_decode('-_8'), 1)", "0xFBFF")]
    public void Base64_RoundTrips(string expression, string expected)
        => AreEqual(expected, Text(expression));

    [TestMethod]
    [DataRow("base64_decode('###')", 9803)]
    [DataRow("base64_encode('abc')", 8116)]
    [DataRow("base64_decode(N'AQI=')", 8116)]
    public void Base64_RefusesAsRealDoes(string expression, int number)
        => new Simulation().AssertSqlError($"select {expression}", number);
}
