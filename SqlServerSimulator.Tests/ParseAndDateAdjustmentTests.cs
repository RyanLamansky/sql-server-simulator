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
}
