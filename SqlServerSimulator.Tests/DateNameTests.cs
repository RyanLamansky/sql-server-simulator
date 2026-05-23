using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>DATENAME(part, date)</c>: localized string form of a
/// date/time component. Two parts surface as English names — <c>month</c>
/// → "January"...; <c>weekday</c> → "Sunday".... Everything else is the
/// integer DATEPART would return, formatted via invariant culture.
/// Probe-confirmed against SQL Server 2025 (2026-05-22).
/// </summary>
[TestClass]
public sealed class DateNameTests
{
    [TestMethod]
    public void DateName_Year_ReturnsYearString()
        => AreEqual("2024", new Simulation().ExecuteScalar("select datename(year, '2024-01-15')"));

    [TestMethod]
    public void DateName_Month_ReturnsMonthName()
        => AreEqual("January", new Simulation().ExecuteScalar("select datename(month, '2024-01-15')"));

    [TestMethod]
    public void DateName_MonthDecember_ReturnsDecember()
        => AreEqual("December", new Simulation().ExecuteScalar("select datename(month, '2024-12-31')"));

    [TestMethod]
    public void DateName_Day_ReturnsDayString()
        => AreEqual("15", new Simulation().ExecuteScalar("select datename(day, '2024-01-15')"));

    [TestMethod]
    public void DateName_Weekday_ReturnsDayName()
        => AreEqual("Monday", new Simulation().ExecuteScalar("select datename(weekday, '2024-01-15')"));

    [TestMethod]
    public void DateName_WeekdaySunday_ReturnsSunday()
        => AreEqual("Sunday", new Simulation().ExecuteScalar("select datename(weekday, '2024-01-14')"));

    [TestMethod]
    public void DateName_Quarter_ReturnsNumericString()
        => AreEqual("2", new Simulation().ExecuteScalar("select datename(quarter, '2024-04-15')"));

    [TestMethod]
    public void DateName_Hour_ReturnsHourString()
        => AreEqual("13", new Simulation().ExecuteScalar("select datename(hour, '2024-01-15T13:45:30')"));

    [TestMethod]
    public void DateName_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select datename(month, cast(null as date))"));

    [TestMethod]
    public void DateName_AliasKeyword_Works()
        => AreEqual("January", new Simulation().ExecuteScalar("select datename(mm, '2024-01-15')"));

    [TestMethod]
    public void DateName_UnknownKeyword_RaisesMsg155()
        => new Simulation().AssertSqlError("select datename(potato, '2024-01-15')", 155);

    [TestMethod]
    public void DateName_ResultType_IsNvarchar()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select datename(month, '2024-01-15')";
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("nvarchar", reader.GetDataTypeName(0));
    }
}
