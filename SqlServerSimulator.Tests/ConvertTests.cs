using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class ConvertTests
{
    [TestMethod]
    [DataRow("convert(int, '42')", 42)]
    [DataRow("convert(int, 1)", 1)]
    [DataRow("convert(bigint, 1)", 1L)]
    [DataRow("convert(varchar(10), 42)", "42")]
    [DataRow("convert(varchar, 12345)", "12345")]
    public void Convert_Basics(string expression, object expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Convert_DecimalRoundsToTargetScale() => AreEqual(3.14m, ExecuteScalar("select convert(decimal(10,2), 3.14)"));

    [TestMethod]
    public void Convert_NullSourcePassesThroughTypedNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select convert(int, null)"));

    [TestMethod]
    public void Convert_NullStyleReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select convert(int, '42', null)"));

    [TestMethod]
    [DataRow("'abc'")]
    [DataRow("'42.5'")]
    public void Convert_StringParseFailure_StillThrows(string source)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select convert(int, {source})"));
        Contains("Conversion failed", ex.Message);
    }

    [TestMethod]
    public void Convert_NarrowingOverflow_StillThrows()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select convert(tinyint, 300)"));
        Contains("Arithmetic overflow", ex.Message);
    }

    [TestMethod]
    [DataRow(0, "Mar 15 2024 10:20AM")]
    [DataRow(120, "2024-03-15 10:20:30")]
    [DataRow(121, "2024-03-15 10:20:30.000")]
    public void Convert_DateTime_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(30), cast('2024-03-15 10:20:30' as datetime), {style})"));

    [TestMethod]
    [DataRow(0, "Mar 15 2024 10:21AM")]
    [DataRow(120, "2024-03-15 10:21:00")]
    [DataRow(121, "2024-03-15 10:21:00.000")]
    public void Convert_SmallDateTime_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(30), cast('2024-03-15 10:21:00' as smalldatetime), {style})"));

    [TestMethod]
    [DataRow(0, "2024-03-15")]
    [DataRow(120, "2024-03-15")]
    [DataRow(121, "2024-03-15")]
    public void Convert_Date_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(30), cast('2024-03-15' as date), {style})"));

    [TestMethod]
    [DataRow(0, "Mar 15 2024 10:20AM")]
    [DataRow(120, "2024-03-15 10:20:30")]
    [DataRow(121, "2024-03-15 10:20:30.1234567")]
    public void Convert_DateTime2_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2024-03-15 10:20:30.1234567' as datetime2(7)), {style})"));

    [TestMethod]
    public void Convert_DateTime2_Precision0_Style121_OmitsFractional() =>
        AreEqual("2024-03-15 10:20:30", ExecuteScalar("select convert(varchar(40), cast('2024-03-15 10:20:30' as datetime2(0)), 121)"));

    [TestMethod]
    [DataRow(0, "10:20AM")]
    [DataRow(120, "10:20:30")]
    [DataRow(121, "10:20:30.123")]
    public void Convert_Time_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('10:20:30.123' as time(3)), {style})"));

    [TestMethod]
    [DataRow(0, "Mar 15 2024 10:20AM -05:00")]
    [DataRow(120, "2024-03-15 10:20:30 -05:00")]
    [DataRow(121, "2024-03-15 10:20:30.123 -05:00")]
    public void Convert_DateTimeOffset_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2024-03-15 10:20:30.123 -05:00' as datetimeoffset(3)), {style})"));

    [TestMethod]
    public void Convert_NoStyle_MatchesCastDefault()
    {
        // No-style CONVERT mirrors CAST: datetime2 stays in ISO form even though style 0 would switch it to legacy.
        AreEqual("2024-03-15 10:20:30.1234567", ExecuteScalar("select convert(varchar(40), cast('2024-03-15 10:20:30.1234567' as datetime2(7)))"));
        AreEqual("Mar 15 2024 10:20AM", ExecuteScalar("select convert(varchar(40), cast('2024-03-15 10:20:30' as datetime))"));
    }

    [TestMethod]
    public void Convert_NVarcharTarget_StyleStillApplies() =>
        AreEqual("2024-03-15 10:20:30.000", ExecuteScalar("select convert(nvarchar(30), cast('2024-03-15 10:20:30' as datetime), 121)"));

    [TestMethod]
    [DataRow("datetime", "'2024-03-15 10:00:00'", "999")]
    [DataRow("datetime2", "'2024-03-15 10:00:00'", "999")]
    [DataRow("time", "'10:00:00'", "999")]
    [DataRow("datetimeoffset", "'2024-03-15 10:00:00 +00:00'", "999")]
    [DataRow("date", "'2024-03-15'", "999")]
    [DataRow("smalldatetime", "'2024-03-15 10:00:00'", "999")]
    public void Convert_BadStyle_RaisesMsg281(string typeName, string sourceLiteral, string style) =>
        AssertSqlMessage(
            $"select convert(varchar(30), cast({sourceLiteral} as {typeName}), {style})",
            $"{style} is not a valid style number when converting from {typeName} to a character string.");

    [TestMethod]
    public void Convert_StyleOnNonDateSource_SilentlyIgnored()
    {
        AreEqual("12345", ExecuteScalar("select convert(varchar(30), 12345, 120)"));
        AreEqual(42, ExecuteScalar("select convert(int, '42', 1)"));
    }

    [TestMethod]
    public void Convert_StringStyle_RaisesMsg8116() =>
        AssertSqlMessage(
            "select convert(varchar(30), cast('2024-03-15' as datetime), '120')",
            "Argument data type varchar is invalid for argument 3 of convert function.");

    [TestMethod]
    public void TryConvert_GoodValue_ReturnsValue() => AreEqual(42, ExecuteScalar("select try_convert(int, '42')"));

    [TestMethod]
    public void TryConvert_NullSource_ReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select try_convert(int, null)"));

    [TestMethod]
    [DataRow("try_convert(int, 'abc')")]              // Msg 245
    [DataRow("try_convert(int, '42.5')")]             // Msg 245
    [DataRow("try_convert(tinyint, '300')")]          // Msg 244
    [DataRow("try_convert(int, '99999999999')")]      // Msg 248
    [DataRow("try_convert(uniqueidentifier, 'bad')")] // Msg 8169
    [DataRow("try_convert(tinyint, 300)")]            // Msg 8115 (overflow)
    [DataRow("try_convert(int, 'x', 1)")]             // ignored style + failure → NULL
    public void TryConvert_ConversionFailure_ReturnsNull(string expression) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void TryConvert_TruncatesDecimalToInt() => AreEqual(42, ExecuteScalar("select try_convert(int, 42.7)"));

    [TestMethod]
    public void TryConvert_ExplicitConversionNotAllowed_StillThrows()
    {
        // Msg 529: SQL Server still raises for type pairs where coercion is fundamentally disallowed, even under TRY_CONVERT.
        var ex = Throws<DbException>(() => ExecuteScalar("select try_convert(date, 0)"));
        Contains("Explicit conversion", ex.Message);
    }

    [TestMethod]
    public void TryConvert_NullStyle_ReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select try_convert(int, '42', null)"));

    [TestMethod]
    public void Convert_MissingArgs_RaisesSyntaxError()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select convert(int)"));
        Contains("syntax", ex.Message);
    }

    [TestMethod]
    public void Convert_BadTargetType_RaisesMsg243()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select convert(notatype, 1)"));
        Contains("notatype", ex.Message);
    }
}
