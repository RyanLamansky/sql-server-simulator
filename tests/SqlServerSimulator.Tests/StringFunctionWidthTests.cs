using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Result-type width of the string-producing scalars CONCAT / CONCAT_WS /
/// STR / DATENAME. These project a computed <c>varchar(N)</c> /
/// <c>nvarchar(N)</c> rather than the family container width — probe-confirmed
/// against SQL Server 2025 (2026-07-22) via
/// <c>sys.dm_exec_describe_first_result_set</c>. Widths are observed in-process
/// through <c>SQL_VARIANT_PROPERTY(..., 'MaxLength')</c>, which reports the
/// declared byte width (nvarchar doubles it).
/// </summary>
/// <remarks>
/// One case can't be observed here: an argument of no declared width drives the
/// whole result to the family container, and a <c>sql_variant</c> can't carry
/// that form — it re-sizes the value on the way in. The container widths live in
/// <c>StringLiteralWidthWireTests</c>, where COLMETADATA reports them directly.
/// </remarks>
[TestClass]
public sealed class StringFunctionWidthTests
{
    private static object? MaxLength(string expr) =>
        ExecuteScalar($"select sql_variant_property(cast({expr} as sql_variant), 'MaxLength')");

    private static object? BaseType(string expr) =>
        ExecuteScalar($"select sql_variant_property(cast({expr} as sql_variant), 'BaseType')");

    // --- CONCAT: sum of per-argument widths (int 12, bigint 24, decimal 41,
    // date 40, string = declared length); a bare NULL contributes 0. ---

    [TestMethod]
    public void Concat_SumsArgumentWidths_BareNullContributesZero()
    {
        AreEqual("varchar", BaseType("concat('a', 1, null, 'b')"));
        AreEqual(14, MaxLength("concat('a', 1, null, 'b')"));   // 1 + 12 + 0 + 1
    }

    [TestMethod]
    [DataRow("concat('a', 'b')", 2)]
    [DataRow("concat(1, 2)", 24)]
    [DataRow("concat(cast(1 as bigint), cast(2 as int))", 36)]
    [DataRow("concat(cast(1.5 as decimal(10,2)), 'x')", 42)]
    [DataRow("concat(cast('2020-01-01' as date), 'x')", 41)]
    public void Concat_VarcharWidth(string expr, int width) => AreEqual(width, MaxLength(expr));

    [TestMethod]
    public void Concat_NationalArgument_ProducesNvarcharWidth()
    {
        AreEqual("nvarchar", BaseType("concat(N'a', 1)"));
        AreEqual(26, MaxLength("concat(N'a', 1)"));   // (1 + 12) chars * 2 bytes
    }

    [TestMethod]
    public void Concat_OverCapStaysBounded_NotMax()
    {
        AreEqual("varchar", BaseType("concat(cast('a' as varchar(5000)), cast('b' as varchar(5000)))"));
        AreEqual(8000, MaxLength("concat(cast('a' as varchar(5000)), cast('b' as varchar(5000)))"));
    }

    // --- CONCAT_WS: value widths + one separator between each value pair. ---

    [TestMethod]
    [DataRow("concat_ws('-', 'a', 'b', 'c')", 5)]    // 3 values + 2 separators
    [DataRow("concat_ws('--', 'a', 'b', 'c')", 7)]   // 3 values + 2 * 2-char separators
    public void ConcatWs_VarcharWidth(string expr, int width) => AreEqual(width, MaxLength(expr));

    // --- STR: the length argument (default 10), varchar. ---

    [TestMethod]
    [DataRow("str(3.14159, 6, 2)", 6)]
    [DataRow("str(3.14159)", 10)]
    [DataRow("str(3.14159, 12)", 12)]
    [DataRow("str(3.14159, 0)", 1)]
    public void Str_VarcharWidthFromLengthArgument(string expr, int width)
    {
        AreEqual("varchar", BaseType(expr));
        AreEqual(width, MaxLength(expr));
    }

    // --- DATENAME: fixed nvarchar(30). ---

    [TestMethod]
    public void DateName_FixedNvarchar30()
    {
        AreEqual("nvarchar", BaseType("datename(month, cast('2020-05-06' as date))"));
        AreEqual(60, MaxLength("datename(month, cast('2020-05-06' as date))"));   // 30 chars * 2 bytes
        AreEqual(60, MaxLength("datename(weekday, cast('2020-05-06' as date))"));
    }
}
