using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>DATE_BUCKET(part, width, date [, origin])</c>: bucket
/// alignment using 1900-01-01 as the default origin. Probe-confirmed
/// against SQL Server 2025 (2026-05-22): the 3rd argument must be a
/// typed date/datetime (string literals raise Msg 8116).
/// </summary>
[TestClass]
public sealed class DateBucketTests
{
    [TestMethod]
    public void DateBucket_Day3_Works()
        => AreEqual(new DateTime(2024, 5, 13), new Simulation().ExecuteScalar("select date_bucket(day, 3, cast('2024-05-15' as date))"));

    [TestMethod]
    public void DateBucket_Hour6_FloorsToBucketStart()
        => AreEqual(new DateTime(2024, 5, 15, 12, 0, 0), new Simulation().ExecuteScalar("select date_bucket(hour, 6, cast('2024-05-15T13:45:30' as datetime2))"));

    [TestMethod]
    public void DateBucket_Month3_QuarterBoundary()
        => AreEqual(new DateTime(2024, 4, 1), new Simulation().ExecuteScalar("select date_bucket(month, 3, cast('2024-05-15' as date))"));

    [TestMethod]
    public void DateBucket_Year1_StaysOnYear()
        => AreEqual(new DateTime(2024, 1, 1), new Simulation().ExecuteScalar("select date_bucket(year, 1, cast('2024-05-15' as date))"));

    [TestMethod]
    public void DateBucket_NullDate_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select date_bucket(day, 1, cast(null as date))"));

    [TestMethod]
    public void DateBucket_NullWidth_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select date_bucket(day, cast(null as int), cast('2024-05-15' as date))"));

    [TestMethod]
    public void DateBucket_WithOrigin_RespectsOrigin()
        => AreEqual(new DateTime(2024, 5, 15), new Simulation().ExecuteScalar("select date_bucket(day, 7, cast('2024-05-15' as date), cast('2024-05-15' as date))"));
}
