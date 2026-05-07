using System.Data.Common;
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
    public void DatePart_NullInput_ReturnsNullInt() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select datepart(year, cast(null as datetime2))"));

    [TestMethod]
    public void DatePart_UnknownKeyword_RaisesMsg155()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select datepart(badpart, getdate())"));
        AreEqual("'badpart' is not a recognized datepart option.", ex.Message);
        AreEqual("155", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DatePart_HourOnDate_RaisesMsg9810()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select datepart(hour, cast('2024-06-15' as date))"));
        AreEqual("The datepart hour is not supported by date function datepart for data type date.", ex.Message);
        AreEqual("9810", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DatePart_YearOnTime_RaisesMsg9810()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select datepart(year, cast('13:45:30' as time))"));
        AreEqual("The datepart year is not supported by date function datepart for data type time.", ex.Message);
    }

    [TestMethod]
    public void DateAdd_DayOnDate_PreservesDateType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (d date)");
        _ = simulation.ExecuteNonQuery("insert t values ('2024-06-15')");
        AreEqual(new DateTime(2024, 6, 22), simulation.ExecuteScalar("select dateadd(day, 7, d) from t"));
    }

    [TestMethod]
    public void DateAdd_HourOnTime_PreservesTimeType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (h time(0))");
        _ = simulation.ExecuteNonQuery("insert t values ('13:45')");
        AreEqual(new TimeSpan(16, 45, 0), simulation.ExecuteScalar("select dateadd(hour, 3, h) from t"));
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
        var ex = Throws<DbException>(() => ExecuteScalar("select dateadd(hour, 1, cast('2024-06-15' as date))"));
        AreEqual("The datepart hour is not supported by date function dateadd for data type date.", ex.Message);
        AreEqual("9810", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DateAdd_DayOnTime_RaisesMsg9810()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select dateadd(day, 1, cast('13:45' as time))"));
        AreEqual("The datepart day is not supported by date function dateadd for data type time.", ex.Message);
    }

    [TestMethod]
    public void DateAdd_YearOverflowOnDate_RaisesMsg517()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select dateadd(year, 100000, cast('2024-06-15' as date))"));
        AreEqual("Adding a value to a 'date' column caused an overflow.", ex.Message);
        AreEqual("517", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DateAdd_TzOffsetOnDateTimeOffset_ShiftsWallClock()
    {
        // DATEADD(tzoffset, +60, ...) preserves the UTC instant but shifts
        // the rendered offset by 60 minutes.
        var v = (DateTimeOffset)ExecuteScalar("select dateadd(tzoffset, 60, cast('2024-06-15 13:00:00 +00:00' as datetimeoffset(0)))")!;
        AreEqual(TimeSpan.FromHours(1), v.Offset);
        AreEqual(new DateTime(2024, 6, 15, 14, 0, 0), v.DateTime);
    }
}
