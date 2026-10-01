using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the culture-aware <c>PARSE</c> / <c>TRY_PARSE</c> functions
/// (Conversion category) and the datetime-adjustment trio
/// <c>DATETRUNC</c> / <c>SWITCHOFFSET</c> / <c>TODATETIMEOFFSET</c>.
/// Probe-confirmed against SQL Server 2025 (2026-05-22).
/// </summary>
[TestClass]
public sealed class ParseAndDateAdjustmentTests
{
    [TestMethod]
    public void Parse_DecimalDefault_Works()
        => AreEqual(1234.56m, new Simulation().ExecuteScalar("select parse('1234.56' as decimal(10, 2))"));

    [TestMethod]
    public void Parse_DecimalGermanCulture_UsesCommaAsDecimal()
        => AreEqual(1234.56m, new Simulation().ExecuteScalar("select parse('1.234,56' as decimal(10, 2) using 'de-DE')"));

    [TestMethod]
    public void Parse_Date_Works()
        => AreEqual(new DateTime(2024, 1, 15), new Simulation().ExecuteScalar("select parse('2024-01-15' as date)"));

    [TestMethod]
    public void TryParse_InvalidInt_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select try_parse('abc' as int)"));

    [TestMethod]
    public void Parse_InvalidInt_RaisesError()
        => new Simulation().AssertSqlError("select parse('abc' as int)", 9819);

    [TestMethod]
    public void DateTrunc_Day_Works()
        => AreEqual(new DateTime(2024, 5, 15), new Simulation().ExecuteScalar("select datetrunc(day, cast('2024-05-15T13:45:30' as datetime2))"));

    [TestMethod]
    public void DateTrunc_Month_Works()
        => AreEqual(new DateTime(2024, 5, 1), new Simulation().ExecuteScalar("select datetrunc(month, cast('2024-05-15' as date))"));

    [TestMethod]
    public void DateTrunc_Year_Works()
        => AreEqual(new DateTime(2024, 1, 1), new Simulation().ExecuteScalar("select datetrunc(year, cast('2024-05-15T13:45:30' as datetime))"));

    [TestMethod]
    public void DateTrunc_Hour_Works()
        => AreEqual(new DateTime(2024, 5, 15, 13, 0, 0), new Simulation().ExecuteScalar("select datetrunc(hour, cast('2024-05-15T13:45:30' as datetime2))"));

    [TestMethod]
    public void DateTrunc_Quarter_Q2()
        => AreEqual(new DateTime(2024, 4, 1), new Simulation().ExecuteScalar("select datetrunc(quarter, cast('2024-05-15' as date))"));

    [TestMethod]
    public void DateTrunc_Week_FloorsToSunday()
        => AreEqual(new DateTime(2024, 5, 12), new Simulation().ExecuteScalar("select datetrunc(week, cast('2024-05-15' as date))"));

    [TestMethod]
    public void SwitchOffset_PreservesUtcInstant()
    {
        var result = (DateTimeOffset)new Simulation().ExecuteScalar("select switchoffset(cast('2024-01-15T12:00:00+00:00' as datetimeoffset), '-05:00')")!;
        AreEqual(new DateTimeOffset(2024, 1, 15, 7, 0, 0, TimeSpan.FromHours(-5)), result);
    }

    [TestMethod]
    public void ToDateTimeOffset_AttachesOffsetWithoutShifting()
    {
        var result = (DateTimeOffset)new Simulation().ExecuteScalar("select todatetimeoffset(cast('2024-01-15T12:00:00' as datetime2), '-05:00')")!;
        AreEqual(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.FromHours(-5)), result);
    }

    [TestMethod]
    public void ToDateTimeOffset_IntegerOffset_TreatedAsMinutes()
    {
        var result = (DateTimeOffset)new Simulation().ExecuteScalar("select todatetimeoffset(cast('2024-01-15T12:00:00' as datetime2), 0)")!;
        AreEqual(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero), result);
    }

    /// <summary>
    /// SWITCHOFFSET and TODATETIMEOFFSET keep the source's fractional
    /// precision: datetime2(n) and datetimeoffset(n) their own, datetime 3,
    /// smalldatetime 0, a string or date 7.
    /// </summary>
    [TestMethod]
    [DataRow("switchoffset(cast('2024-01-01 10:00' as datetime2(3)), '+01:00')", 3)]
    [DataRow("switchoffset(cast('2024-01-01' as datetime), '+01:00')", 3)]
    [DataRow("switchoffset(cast('2024-01-01' as smalldatetime), '+01:00')", 0)]
    [DataRow("switchoffset('2024-01-01 10:00', '+01:00')", 7)]
    [DataRow("todatetimeoffset(cast('2024-01-01 10:00' as datetime), '-08:00')", 3)]
    [DataRow("todatetimeoffset(cast('2024-01-01 10:00' as datetime2(2)), 60)", 2)]
    [DataRow("todatetimeoffset(cast('2024-01-01' as smalldatetime), 0)", 0)]
    [DataRow("todatetimeoffset(cast('2024-01-01' as date), 0)", 7)]
    public void OffsetFunctions_KeepSourcePrecision(string expression, int scale)
        => AreEqual(scale, new Simulation().ExecuteScalar($"select sql_variant_property(cast({expression} as sql_variant), 'Scale')"));

    /// <summary>An offset that carries the value out of range is Msg 9813.</summary>
    [TestMethod]
    [DataRow("switchoffset(cast('9999-12-31 23:00 +00:00' as datetimeoffset), '+05:00')", "switchoffset", 0)]
    [DataRow("switchoffset(cast('0001-01-01 00:30 +00:00' as datetimeoffset), '-05:00')", "switchoffset", 0)]
    [DataRow("todatetimeoffset(cast('0001-01-01 00:30' as datetime2), '+05:00')", "todatetimeoffset", 2)]
    [DataRow("todatetimeoffset(cast('9999-12-31 23:30' as datetime2), '-05:00')", "todatetimeoffset", 2)]
    public void OffsetOverflow_Raises9813(string expression, string function, int state)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}", 9813);
        AreEqual($"The timezone provided to builtin function {function} would cause the datetimeoffset to overflow the range of valid date range in either UTC or local time.", ex.Message);
        AreEqual(state, ex.State);
    }

    /// <summary>
    /// DATETRUNC refuses a part finer than the operand's precision, types a
    /// bare NULL as datetime2(7), and reports a week starting before
    /// 0001-01-01 as Msg 9837.
    /// </summary>
    [TestMethod]
    [DataRow("millisecond", "datetime2(2)", "datetime2")]
    [DataRow("microsecond", "datetime2(3)", "datetime2")]
    [DataRow("millisecond", "time(2)", "time")]
    [DataRow("millisecond", "datetimeoffset(2)", "datetimeoffset")]
    public void DateTrunc_PartFinerThanPrecision_Raises9810(string part, string type, string typeName)
    {
        var ex = new Simulation().AssertSqlError($"select datetrunc({part}, cast('2024-01-01 10:00:00' as {type}))", 9810);
        AreEqual($"The datepart {part} is not supported by date function datetrunc for data type {typeName}.", ex.Message);
        AreEqual(11, ex.State);
    }

    [TestMethod]
    public void DateTrunc_BareNull_IsDateTime2()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select datetrunc(year, null)").ExecuteReader();
        AreEqual("datetime2", reader.GetDataTypeName(0));
    }

    [TestMethod]
    [DataRow("date", "date")]
    [DataRow("datetime2", "datetime2")]
    public void DateTrunc_WeekBeforeYearOne_Raises9837(string type, string typeName)
        => new Simulation().AssertSqlError(
            $"select datetrunc(week, cast('0001-01-03' as {type}))",
            9837,
            $"An invalid {typeName} value was encountered: The date value is less than the minimum date value allowed for the data type.");

    /// <summary>PARSE converts to the numeric and date / time types alone.</summary>
    [TestMethod]
    [DataRow("parse('1' as varchar(10))", "varchar", "PARSE")]
    [DataRow("parse('1' as nvarchar(5))", "nvarchar", "PARSE")]
    [DataRow("parse('1' as char(5))", "char", "PARSE")]
    [DataRow("parse('true' as bit)", "bit", "PARSE")]
    [DataRow("parse('6F9619FF-8B86-D011-B42D-00C04FC964FF' as uniqueidentifier)", "uniqueidentifier", "PARSE")]
    [DataRow("try_parse('1' as varbinary(5))", "varbinary", "TRY_PARSE")]
    public void Parse_NonNumericNonDateTarget_Raises10761(string expression, string typeName, string function)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}", 10761);
        AreEqual($"Invalid data type {typeName} in function {function}.", ex.Message);
        AreEqual(15, ex.Class);
    }

    [TestMethod]
    [DataRow("parse('1:30 PM' as time)", "13:30:00")]
    [DataRow("parse('1:30:15 AM' as time(0))", "01:30:15")]
    [DataRow("parse('2024-01-01 10:00' as time)", "10:00:00")]
    public void Parse_Time_ReadsLikeADateTime(string expression, string expected)
        => AreEqual(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), new Simulation().ExecuteScalar($"select {expression}"));
}
