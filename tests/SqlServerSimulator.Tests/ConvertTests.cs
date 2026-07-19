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
    [DataRow(0, "Mar 15 2024")]
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

    [TestMethod]
    [DataRow(1, "05/13/26")]
    [DataRow(101, "05/13/2026")]
    [DataRow(10, "05-13-26")]
    [DataRow(110, "05-13-2026")]
    [DataRow(12, "260513")]
    [DataRow(112, "20260513")]
    [DataRow(102, "2026.05.13")]
    [DataRow(103, "13/05/2026")]
    [DataRow(23, "2026-05-13")]
    [DataRow(126, "2026-05-13T14:25:36.790")]
    [DataRow(127, "2026-05-13T14:25:36.790")]
    public void Convert_DateTimeStyle(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"declare @d datetime = '2026-05-13T14:25:36.789'; select convert(varchar(40), @d, {style})"));

    [TestMethod]
    [DataRow(1, "05/13/26")]
    [DataRow(101, "05/13/2026")]
    [DataRow(112, "20260513")]
    [DataRow(23, "2026-05-13")]
    [DataRow(126, "2026-05-13")]
    public void Convert_DateStyle(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2026-05-13' as date), {style})"));

    [TestMethod]
    public void Convert_DateTime2Style126_FullPrecision() =>
        AreEqual("2026-05-13T14:25:36.1234567", ExecuteScalar(
            "select convert(varchar(40), cast('2026-05-13T14:25:36.1234567' as datetime2(7)), 126)"));

    [TestMethod]
    public void Convert_DateTimeOffsetStyle126_PreservesOffset() =>
        AreEqual("2026-05-13T14:25:36.1234567+05:30", ExecuteScalar(
            "select convert(varchar(40), cast('2026-05-13T14:25:36.1234567+05:30' as datetimeoffset(7)), 126)"));

    [TestMethod]
    public void Convert_DateTimeOffsetStyle127_ProjectsToUtcWithZ() =>
        AreEqual("2026-05-13T08:55:36.1234567Z", ExecuteScalar(
            "select convert(varchar(40), cast('2026-05-13T14:25:36.1234567+05:30' as datetimeoffset(7)), 127)"));

    [TestMethod]
    [DataRow(101, "05/13/2026")]
    [DataRow(112, "20260513")]
    [DataRow(102, "2026.05.13")]
    [DataRow(103, "13/05/2026")]
    [DataRow(23, "2026-05-13")]
    public void Convert_StringToDateWithStyle(int style, string input) =>
        AreEqual(new DateTime(2026, 5, 13), ExecuteScalar($"select convert(date, '{input}', {style})"));

    // General styles run SQL Server's flexible parser: separators (/ - .) are
    // interchangeable and ISO year-first is accepted, with the trailing pair
    // ordered by the style family. Each input below resolves to 2026-05-13.
    [TestMethod]
    [DataRow(101, "2026-05-13")]   // mdy + ISO year-first (the AdventureWorks shape)
    [DataRow(101, "2026/05/13")]
    [DataRow(101, "05-13-2026")]   // mdy, dash where the style documents slash
    [DataRow(0, "2026-05-13")]     // default style, ISO
    [DataRow(120, "2026-05-13")]   // ODBC canonical
    [DataRow(102, "2026/05/13")]   // ymd, slash where the style documents dot
    [DataRow(110, "05/13/2026")]   // mdy, slash where the style documents dash
    public void Convert_StringToDate_GeneralStyle_SeparatorAndIsoFlexible(int style, string input) =>
        AreEqual(new DateTime(2026, 5, 13), ExecuteScalar($"select convert(date, '{input}', {style})"));

    [TestMethod]
    [DataRow(103, "2003-04-05")]   // dmy + year-first → trailing pair is day-month → May 4
    [DataRow(104, "2003.04.05")]
    public void Convert_StringToDate_DmyYearFirst_OrdersTrailingDayMonth(int style, string input) =>
        AreEqual(new DateTime(2003, 5, 4), ExecuteScalar($"select convert(date, '{input}', {style})"));

    [TestMethod]
    [DataRow("Apr 5 2003")]
    [DataRow("April 5, 2003")]
    [DataRow("5 Apr 2003")]
    public void Convert_StringToDate_MonthNameForms_StyleIndependent(string input) =>
        AreEqual(new DateTime(2003, 4, 5), ExecuteScalar($"select convert(date, '{input}', 101)"));

    [TestMethod]
    public void Convert_StringToDateTime_GeneralStyle_AmPmAndSpaceTime()
    {
        AreEqual(new DateTime(2003, 1, 1, 23, 59, 0), ExecuteScalar("select convert(datetime, 'Jan 1 2003 11:59PM', 101)"));
        AreEqual(new DateTime(2003, 4, 5, 10, 20, 30), ExecuteScalar("select convert(datetime, '2003-04-05 10:20:30', 101)"));
    }

    [TestMethod]
    public void Convert_StringToDateTime_GeneralStyle_BareTimeAnchorsTo1900() =>
        AreEqual(new DateTime(1900, 1, 1, 10, 20, 30), ExecuteScalar("select convert(datetime, '10:20:30', 101)"));

    [TestMethod]
    public void Convert_StringToDateTime2_Style126() =>
        AreEqual(new DateTime(2026, 5, 13, 14, 25, 36, 789), ExecuteScalar(
            "select convert(datetime2(3), '2026-05-13T14:25:36.789', 126)"));

    [TestMethod]
    public void Convert_StringToDateTime2_Style127WithZ() =>
        AreEqual(new DateTime(2026, 5, 13, 14, 25, 36, 789), ExecuteScalar(
            "select convert(datetime2(3), '2026-05-13T14:25:36.789Z', 127)"));

    [TestMethod]
    public void Convert_StringToDate_StyleMismatchRaisesMsg9807()
    {
        var ex = Throws<DbException>(() => ExecuteScalar(
            "select convert(date, '05/13/2026', 112)"));
        AreEqual("9807", ex.Data["HelpLink.EvtID"]);
        Contains("style 112", ex.Message);
    }

    [TestMethod]
    public void Convert_StringToDate_NotADate_RaisesMsg241()
    {
        var ex = Throws<DbException>(() => ExecuteScalar(
            "select convert(date, 'nope', 112)"));
        AreEqual("241", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void TryConvert_StringToDate_StyleMismatchReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select try_convert(date, '05/13/2026', 112)"));

    [TestMethod]
    public void TryConvert_StringToDate_NotADateReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select try_convert(date, 'nope', 112)"));

    [TestMethod]
    [DataRow(0, "1234567.89")]
    [DataRow(1, "1,234,567.89")]
    [DataRow(2, "1234567.8910")]
    public void Convert_MoneyStyle(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast(1234567.891 as money), {style})"));

    [TestMethod]
    public void Convert_MoneyStyle_Negative()
        => AreEqual("-12.50", ExecuteScalar("select convert(varchar(40), cast(-12.5 as money), 0)"));

    [TestMethod]
    public void Convert_SmallMoneyStyle1()
        => AreEqual("1,234.56", ExecuteScalar("select convert(varchar(40), cast(1234.56 as smallmoney), 1)"));

    [TestMethod]
    public void Convert_MoneyStyle_UnknownStyle_RaisesMsg281()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select convert(varchar(40), cast(1.5 as money), 99)"));
        AreEqual("281", ex.Data["HelpLink.EvtID"]);
        Contains("money", ex.Message);
    }

    [TestMethod]
    public void Convert_NullStringSource_WithStyle_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select convert(datetime2, cast(null as nvarchar(50)), 126)"));

    [TestMethod]
    public void Convert_NullDateSource_WithStyle_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select convert(varchar(40), cast(null as datetime2), 121)"));

    // -- Expanded date/time CONVERT-style coverage. Source values match the probe
    // fixtures (2026-05-13 14:25:36.1234567 with timezone +05:30 for offset source)
    // so every DataRow string can be back-traced to the probe output.

    [TestMethod]
    [DataRow(0, "May 13 2026  2:25PM")]
    [DataRow(100, "May 13 2026  2:25PM")]
    [DataRow(1, "05/13/26")]
    [DataRow(101, "05/13/2026")]
    [DataRow(2, "26.05.13")]
    [DataRow(102, "2026.05.13")]
    [DataRow(3, "13/05/26")]
    [DataRow(103, "13/05/2026")]
    [DataRow(4, "13.05.26")]
    [DataRow(104, "13.05.2026")]
    [DataRow(5, "13-05-26")]
    [DataRow(105, "13-05-2026")]
    [DataRow(6, "13 May 26")]
    [DataRow(106, "13 May 2026")]
    [DataRow(7, "May 13, 26")]
    [DataRow(107, "May 13, 2026")]
    [DataRow(8, "14:25:36")]
    [DataRow(24, "14:25:36")]
    [DataRow(108, "14:25:36")]
    [DataRow(9, "May 13 2026  2:25:36:123PM")]
    [DataRow(109, "May 13 2026  2:25:36:123PM")]
    [DataRow(10, "05-13-26")]
    [DataRow(110, "05-13-2026")]
    [DataRow(11, "26/05/13")]
    [DataRow(111, "2026/05/13")]
    [DataRow(12, "260513")]
    [DataRow(112, "20260513")]
    [DataRow(13, "13 May 2026 14:25:36:123")]
    [DataRow(113, "13 May 2026 14:25:36:123")]
    [DataRow(14, "14:25:36:123")]
    [DataRow(114, "14:25:36:123")]
    [DataRow(20, "2026-05-13 14:25:36")]
    [DataRow(120, "2026-05-13 14:25:36")]
    [DataRow(21, "2026-05-13 14:25:36.123")]
    [DataRow(25, "2026-05-13 14:25:36.123")]
    [DataRow(121, "2026-05-13 14:25:36.123")]
    [DataRow(22, "05/13/26  2:25:36 PM")]
    [DataRow(23, "2026-05-13")]
    [DataRow(126, "2026-05-13T14:25:36.123")]
    [DataRow(127, "2026-05-13T14:25:36.123")]
    public void Convert_DateTime_AllStyles(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2026-05-13T14:25:36.123' as datetime), {style})"));

    [TestMethod]
    [DataRow(0, "May 13 2026  2:25PM")]
    [DataRow(9, "May 13 2026  2:25:00:000PM")]
    [DataRow(13, "13 May 2026 14:25:00:000")]
    [DataRow(14, "14:25:00:000")]
    [DataRow(22, "05/13/26  2:25:00 PM")]
    [DataRow(108, "14:25:00")]
    [DataRow(112, "20260513")]
    public void Convert_SmallDateTime_ExpandedStyles(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2026-05-13T14:25:00' as smalldatetime), {style})"));

    [TestMethod]
    [DataRow(0, "Mar 15 2024")]
    [DataRow(9, "Mar 15 2024")]   // milliseconds suppressed because date has no time
    [DataRow(2, "24.03.15")]
    [DataRow(3, "15/03/24")]
    [DataRow(4, "15.03.24")]
    [DataRow(5, "15-03-24")]
    [DataRow(6, "15 Mar 24")]
    [DataRow(7, "Mar 15, 24")]
    [DataRow(11, "24/03/15")]
    [DataRow(13, "15 Mar 2024")]   // 24-hour time suppressed because no time
    [DataRow(22, "03/15/24")]
    [DataRow(104, "15.03.2024")]
    [DataRow(105, "15-03-2024")]
    [DataRow(106, "15 Mar 2024")]
    [DataRow(107, "Mar 15, 2024")]
    [DataRow(111, "2024/03/15")]
    public void Convert_Date_ExpandedStyles(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2024-03-15' as date), {style})"));

    [TestMethod]
    [DataRow(0, "2:25PM")]
    [DataRow(9, "2:25:36.1234567PM")]
    [DataRow(100, "2:25PM")]
    [DataRow(109, "2:25:36.1234567PM")]
    [DataRow(8, "14:25:36")]
    [DataRow(24, "14:25:36")]
    [DataRow(108, "14:25:36")]
    [DataRow(13, "14:25:36.1234567")]
    [DataRow(14, "14:25:36.1234567")]
    [DataRow(113, "14:25:36.1234567")]
    [DataRow(114, "14:25:36.1234567")]
    [DataRow(20, "14:25:36")]
    [DataRow(120, "14:25:36")]
    [DataRow(21, "14:25:36.1234567")]
    [DataRow(25, "14:25:36.1234567")]
    [DataRow(121, "14:25:36.1234567")]
    [DataRow(22, " 2:25:36 PM")]
    [DataRow(126, "14:25:36.1234567")]
    [DataRow(127, "14:25:36.1234567")]
    public void Convert_Time7_ExpandedStyles(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('14:25:36.1234567' as time(7)), {style})"));

    [TestMethod]
    [DataRow(0, "May 13 2026  2:25PM")]
    [DataRow(9, "May 13 2026  2:25:36.1234567PM")]
    [DataRow(13, "13 May 2026 14:25:36.1234567")]
    [DataRow(14, "14:25:36.1234567")]
    [DataRow(22, "05/13/26  2:25:36 PM")]
    [DataRow(21, "2026-05-13 14:25:36.1234567")]
    [DataRow(126, "2026-05-13T14:25:36.1234567")]
    [DataRow(127, "2026-05-13T14:25:36.1234567")]
    [DataRow(112, "20260513")]
    public void Convert_DateTime2P7_ExpandedStyles(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2026-05-13T14:25:36.1234567' as datetime2(7)), {style})"));

    [TestMethod]
    [DataRow(9, "May 13 2026  2:25:36PM")]
    [DataRow(13, "13 May 2026 14:25:36")]
    [DataRow(14, "14:25:36")]
    [DataRow(21, "2026-05-13 14:25:36")]
    [DataRow(121, "2026-05-13 14:25:36")]
    [DataRow(126, "2026-05-13T14:25:36")]
    public void Convert_DateTime2P0_FractionalSuppressed(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2026-05-13T14:25:36' as datetime2(0)), {style})"));

    [TestMethod]
    [DataRow(0, "May 13 2026  2:25PM +05:30")]
    [DataRow(9, "May 13 2026  2:25:36.1234567PM +05:30")]
    [DataRow(13, "13 May 2026 14:25:36.1234567 +05:30")]
    [DataRow(14, "14:25:36.1234567 +05:30")]
    [DataRow(8, "14:25:36 +05:30")]
    [DataRow(20, "2026-05-13 14:25:36 +05:30")]
    [DataRow(21, "2026-05-13 14:25:36.1234567 +05:30")]
    [DataRow(22, "05/13/26  2:25:36 PM +05:30")]
    [DataRow(126, "2026-05-13T14:25:36.1234567+05:30")]
    [DataRow(127, "2026-05-13T08:55:36.1234567Z")]
    [DataRow(112, "20260513")]                            // date-only style strips offset
    public void Convert_DateTimeOffset_ExpandedStyles(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('2026-05-13T14:25:36.1234567+05:30' as datetimeoffset(7)), {style})"));

    [TestMethod]
    [DataRow(8)]
    [DataRow(24)]
    [DataRow(108)]
    public void Convert_Date_TimeOnlyStyles_RaiseMsg8114(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast('2024-03-15' as date), {style})",
            "Error converting data type date to varchar.");

    [TestMethod]
    [DataRow(14)]
    [DataRow(114)]
    public void Convert_Date_FractionalTimeOnlyStyles_RaiseMsg281(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast('2024-03-15' as date), {style})",
            $"{style} is not a valid style number when converting from date to a character string.");

    [TestMethod]
    [DataRow(1)]
    [DataRow(23)]
    [DataRow(101)]
    [DataRow(112)]
    public void Convert_Time_DateBearingStyles_RaiseMsg8114(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast('14:25:36' as time(0)), {style})",
            "Error converting data type time to varchar.");

    [TestMethod]
    [DataRow(50)]
    [DataRow(132)]
    [DataRow(200)]
    public void Convert_Time_UnknownStyle_RaisesMsg281(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast('14:25:36' as time(0)), {style})",
            $"{style} is not a valid style number when converting from time to a character string.");

    [TestMethod]
    public void Convert_Hijri_DateTime2_NvarcharPreservesArabicMonth() =>
        AreEqual("27 ذو القعدة 1447  2:25:36.1234567PM", ExecuteScalar(
            "select convert(nvarchar(64), cast('2026-05-13T14:25:36.1234567' as datetime2(7)), 130)"));

    [TestMethod]
    public void Convert_Hijri_LegacyDateTime_NvarcharColonFractional() =>
        AreEqual("27 ذو القعدة 1447  2:25:36:123PM", ExecuteScalar(
            "select convert(nvarchar(64), cast('2026-05-13T14:25:36.123' as datetime), 130)"));

    [TestMethod]
    public void Convert_Hijri_Date_NumericMonth131() =>
        AreEqual("27/11/1447", ExecuteScalar(
            "select convert(nvarchar(64), cast('2026-05-13' as date), 131)"));

    [TestMethod]
    [DataRow("'2026-04-17'", " 1 ذو القعدة 1447")]    // single-digit Hijri day → space-padded
    [DataRow("'2026-08-15'", " 2 ربيع الاول 1448")]
    [DataRow("'2026-12-15'", " 6 رجب 1448")]
    public void Convert_Hijri_SpacePaddedDay(string source, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(nvarchar(64), cast({source} as date), 130)"));

    [TestMethod]
    [DataRow(0, "1.23457e+006")]
    [DataRow(1, "1.2345679e+006")]
    [DataRow(2, "1.234567890000000e+006")]
    [DataRow(3, "1.2345678899999999e+006")]
    [DataRow(126, "1.234567890000000e+006")]
    public void Convert_Float_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast(1234567.89 as float), {style})"));

    [TestMethod]
    [DataRow("0", "0")]
    [DataRow("0.5", "0.5")]
    [DataRow("100", "100")]
    [DataRow("12345", "12345")]
    [DataRow("999999", "999999")]
    [DataRow("1000000", "1e+006")]
    [DataRow("0.0001", "0.0001")]
    [DataRow("9.99e-5", "9.99e-005")]
    public void Convert_Float_Style0_BoundaryFormatting(string literal, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast({literal} as float), 0)"));

    [TestMethod]
    [DataRow(0, "1.23457e+006")]
    [DataRow(1, "1.2345679e+006")]
    [DataRow(126, "1.2345679e+006")]      // real style 126 = 8 sig digits, NOT 16
    public void Convert_Real_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast(1234567.89 as real), {style})"));

    [TestMethod]
    [DataRow(99)]
    [DataRow(200)]
    public void Convert_Float_UnknownStyle_RaisesMsg281(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast(1.0 as float), {style})",
            $"{style} is not a valid style number when converting from float to a character string.");

    [TestMethod]
    [DataRow(99)]
    public void Convert_Real_UnknownStyle_RaisesMsg281(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast(1.0 as real), {style})",
            $"{style} is not a valid style number when converting from real to a character string.");

    [TestMethod]
    [DataRow(1, "0xAABBCC")]
    [DataRow(2, "AABBCC")]
    public void Convert_Varbinary_ToVarchar_StyleOutputs(int style, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast(0xAABBCC as varbinary(8)), {style})"));

    [TestMethod]
    public void Convert_Varbinary_Style0_ReinterpretsAsCp1252() =>
        AreEqual("AB", ExecuteScalar("select convert(varchar(40), cast(0x4142 as varbinary(8)), 0)"));

    [TestMethod]
    public void Convert_Varbinary_Empty_StylesEmitEmpty()
    {
        AreEqual("0x", ExecuteScalar("select convert(varchar(40), cast(0x as varbinary(8)), 1)"));
        AreEqual("", ExecuteScalar("select convert(varchar(40), cast(0x as varbinary(8)), 2)"));
    }

    [TestMethod]
    [DataRow(99)]
    public void Convert_Varbinary_UnknownStyle_RaisesMsg281(int style) =>
        AssertSqlMessage(
            $"select convert(varchar(40), cast(0xAB as varbinary(8)), {style})",
            $"{style} is not a valid style number when converting from varbinary to a character string.");

    /// <summary>
    /// Each CP1252 byte preserved verbatim; "0xAABBCC"u8 = { 0x30, 0x78, 0x41, 0x41, 0x42, 0x42, 0x43, 0x43 }.
    /// </summary>
    [TestMethod]
    public void Convert_String_ToVarbinary_Style0_CopiesBytes() =>
        CollectionAssert.AreEqual(
            "0xAABBCC"u8.ToArray(),
            (byte[])ExecuteScalar("select convert(varbinary(8), '0xAABBCC', 0)")!);

    [TestMethod]
    public void Convert_String_ToVarbinary_Style1_ParsesHexWithPrefix() =>
        CollectionAssert.AreEqual(
            new byte[] { 0xAA, 0xBB, 0xCC },
            (byte[])ExecuteScalar("select convert(varbinary(8), '0xAABBCC', 1)")!);

    [TestMethod]
    public void Convert_String_ToVarbinary_Style2_ParsesHexWithoutPrefix() =>
        CollectionAssert.AreEqual(
            new byte[] { 0xAA, 0xBB, 0xCC },
            (byte[])ExecuteScalar("select convert(varbinary(8), 'AABBCC', 2)")!);

    [TestMethod]
    public void Convert_String_ToVarbinary_Style1_RejectsMissingPrefix() =>
        AssertSqlMessage(
            "select convert(varbinary(8), 'AABBCC', 1)",
            "Error converting data type varchar to varbinary.");

    [TestMethod]
    public void Convert_String_ToVarbinary_Style2_RejectsPresentPrefix() =>
        AssertSqlMessage(
            "select convert(varbinary(8), '0xAABBCC', 2)",
            "Error converting data type varchar to varbinary.");

    [TestMethod]
    [DataRow("'ABCXYZ'")]   // bad hex char
    [DataRow("'A'")]        // odd-length hex
    public void Convert_String_ToVarbinary_Style2_BadHex_RaisesMsg8114(string literal) =>
        AssertSqlMessage(
            $"select convert(varbinary(8), {literal}, 2)",
            "Error converting data type varchar to varbinary.");

    [TestMethod]
    public void TryConvert_String_ToVarbinary_BadHex_ReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select try_convert(varbinary(8), 'ABCXYZ', 2)"));
}
