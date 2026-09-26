using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Direct simulator coverage for the <c>DATEPART</c> and <c>DATEADD</c>
/// scalar functions: keyword resolution (canonical + aliases), per-input-type
/// extraction / addition semantics, NULL propagation, the cross-type
/// compatibility rejection (Msg 9810), and the overflow path on DATEADD
/// (Msg 517). The EF Core wire path is exercised in <c>EFCoreDateTime.cs</c>;
/// these tests cover SQL-only edges EF Core doesn't reach.
/// </summary>
[TestClass]
public sealed class DatePartTests
{
    [TestMethod]
    [DataRow("year", 2024)]
    [DataRow("month", 6)]
    [DataRow("day", 15)]
    [DataRow("dayofyear", 167)]
    [DataRow("quarter", 2)]
    [DataRow("hour", 13)]
    [DataRow("minute", 45)]
    [DataRow("second", 30)]
    [DataRow("millisecond", 500)]
    public void DatePart_OnDateTime2_ReturnsExpectedComponent(string part, int expected) =>
        AreEqual(expected, ExecuteScalar($"select datepart({part}, cast('2024-06-15 13:45:30.5' as datetime2(7)))"));

    [TestMethod]
    [DataRow("yy", 2024)]
    [DataRow("yyyy", 2024)]
    [DataRow("mm", 6)]
    [DataRow("dd", 15)]
    [DataRow("hh", 13)]
    [DataRow("mi", 45)]
    [DataRow("ss", 30)]
    public void DatePart_AcceptsCommonKeywordAliases(string alias, int expected) =>
        AreEqual(expected, ExecuteScalar($"select datepart({alias}, cast('2024-06-15 13:45:30' as datetime2(0)))"));

    [TestMethod]
    public void DatePart_OnDate_AcceptsDateParts() =>
        AreEqual(2024, ExecuteScalar("select datepart(year, cast('2024-06-15' as date))"));

    [TestMethod]
    public void DatePart_OnTime_AcceptsTimeParts() =>
        AreEqual(13, ExecuteScalar("select datepart(hour, cast('13:45:30' as time))"));

    [TestMethod]
    public void DatePart_OnDateTimeOffset_AcceptsTzOffset() =>
        AreEqual(-420, ExecuteScalar("select datepart(tzoffset, cast('2024-06-15 13:45:30 -07:00' as datetimeoffset))"));

    [TestMethod]
    [DataRow("2024-01-01", 1)]   // Jan 1 is always week 1.
    [DataRow("2024-01-06", 1)]   // Saturday before first Sunday roll → still week 1.
    [DataRow("2024-01-07", 2)]   // Sunday → week 2 begins.
    [DataRow("2024-06-15", 24)]  // Mid-year, default us_english Sunday-anchored.
    [DataRow("2024-12-31", 53)]  // Last day of year — straddles into week 53.
    public void DatePart_Week_DefaultUsEnglishSundayAnchored(string dateStr, int expectedWeek) =>
        AreEqual(expectedWeek, ExecuteScalar($"select datepart(week, cast('{dateStr}' as date))"));

    [TestMethod]
    public void DatePart_NullInput_ReturnsNullInt() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select datepart(year, cast(null as datetime2))"));

    [TestMethod]
    public void DatePart_UnknownKeyword_RaisesMsg155()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select datepart(badpart, getdate())"));
        AreEqual("'badpart' is not a recognized datepart option.", ex.Message);
        AreEqual(155, ex.Number);
    }

    [TestMethod]
    public void DatePart_HourOnDate_RaisesMsg9810()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select datepart(hour, cast('2024-06-15' as date))"));
        AreEqual("The datepart hour is not supported by date function datepart for data type date.", ex.Message);
        AreEqual(9810, ex.Number);
    }

    [TestMethod]
    public void DatePart_YearOnTime_RaisesMsg9810()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select datepart(year, cast('13:45:30' as time))"));
        AreEqual("The datepart year is not supported by date function datepart for data type time.", ex.Message);
    }

    [TestMethod]
    public void DateAdd_DayOnDate_PreservesDateType()
    {
        AreEqual(new DateTime(2024, 6, 22), new Simulation().ExecuteScalar("""
            create table t (d date);
            insert t values ('2024-06-15');
            select dateadd(day, 7, d) from t
            """));
    }

    [TestMethod]
    public void DateAdd_HourOnTime_PreservesTimeType()
    {
        AreEqual(new TimeSpan(16, 45, 0), new Simulation().ExecuteScalar("""
            create table t (h time(0));
            insert t values ('13:45');
            select dateadd(hour, 3, h) from t
            """));
    }

    [TestMethod]
    public void DateAdd_NegativeN_SubtractsFromValue() =>
        AreEqual(new DateTime(2023, 6, 15), ExecuteScalar("select dateadd(year, -1, cast('2024-06-15' as datetime2(0)))"));

    [TestMethod]
    public void DateAdd_NullValue_ReturnsTypedNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select dateadd(day, 1, cast(null as datetime2))"));

    [TestMethod]
    public void DateAdd_HourOnDate_RaisesMsg9810()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select dateadd(hour, 1, cast('2024-06-15' as date))"));
        AreEqual("The datepart hour is not supported by date function dateadd for data type date.", ex.Message);
        AreEqual(9810, ex.Number);
    }

    [TestMethod]
    public void DateAdd_DayOnTime_RaisesMsg9810()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select dateadd(day, 1, cast('13:45' as time))"));
        AreEqual("The datepart day is not supported by date function dateadd for data type time.", ex.Message);
    }

    [TestMethod]
    public void DateAdd_YearOverflowOnDate_RaisesMsg517()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select dateadd(year, 100000, cast('2024-06-15' as date))"));
        AreEqual("Adding a value to a 'date' column caused an overflow.", ex.Message);
        AreEqual(517, ex.Number);
    }

    /// <summary>
    /// The datepart × function × type refusals, with the state naming the
    /// type (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("dateadd(iso_week, 1, cast('2024-02-29' as datetime2))", 2)]
    [DataRow("dateadd(iso_week, 1, cast('2024-02-29' as date))", 2)]
    [DataRow("dateadd(microsecond, 1, cast('2024-02-29' as datetime))", 0)]
    [DataRow("dateadd(nanosecond, 1, cast('2024-02-29' as smalldatetime))", 3)]
    [DataRow("dateadd(tzoffset, 1, cast('2024-02-29' as date))", 1)]
    [DataRow("dateadd(tzoffset, 1, cast('2024-02-29' as datetimeoffset))", 2)]
    [DataRow("datetrunc(weekday, cast('2024-02-29' as datetime2))", 11)]
    [DataRow("datetrunc(nanosecond, cast('12:00' as time))", 11)]
    [DataRow("datetrunc(microsecond, cast('2024-02-29' as datetime))", 9)]
    [DataRow("datetrunc(millisecond, cast('2024-02-29' as smalldatetime))", 8)]
    [DataRow("datetrunc(tzoffset, cast('2024-02-29' as date))", 10)]
    [DataRow("datetrunc(tzoffset, cast('2024-02-29' as datetimeoffset))", 11)]
    public void AFunctionRefusesTheDatepart(string expression, int state)
        => AreEqual((byte)state, new Simulation().AssertSqlError($"select {expression}", 9810).State);

    [TestMethod]
    public void TzOffset_ReadsZeroFromADateTime2()
        => AreEqual("0:+00:00", new Simulation().ExecuteScalar("declare @d datetime2 = '2024-02-29'; select concat(datepart(tzoffset, @d), ':', datename(tzoffset, @d))"));

    [TestMethod]
    public void ASmallDateTime_IsNamedDatetime_InDatepartsRefusal()
        => StartsWith("The datepart tzoffset is not supported by date function datepart for data type datetime.",
            new Simulation().AssertSqlError("select datepart(tzoffset, cast('2024-02-29' as smalldatetime))", 9810).Errors[0].Message);
}
