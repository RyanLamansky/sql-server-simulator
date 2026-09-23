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

    /// <summary>
    /// Buckets are whole spans from the origin: weeks count 7 days from the
    /// origin's own weekday, and an origin off a boundary shifts every bucket.
    /// </summary>
    [TestMethod]
    [DataRow("date_bucket(week, 2, cast('2024-05-05' as date))", "2024-04-22")]
    [DataRow("date_bucket(week, 1, cast('2024-05-05' as date))", "2024-04-29")]
    [DataRow("date_bucket(week, 1, cast('2024-05-05' as date), cast('2024-05-01' as date))", "2024-05-01")]
    [DataRow("date_bucket(month, 1, cast('2024-05-05' as date), cast('2024-01-15' as date))", "2024-04-15")]
    [DataRow("date_bucket(hour, 1, cast('2024-01-01 01:10' as datetime2), cast('2024-01-01 00:30' as datetime2))", "2024-01-01 00:30")]
    [DataRow("date_bucket(week, 1, cast('1899-12-31' as date))", "1899-12-25")]
    public void Bucket_CountsWholeSpansFromTheOrigin(string expression, string expected)
        => AreEqual(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), new Simulation().ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void StringDate_RaisesMsg8116()
        => new Simulation().AssertSqlError(
            "select date_bucket(day, 1, '2024-01-01 10:00')",
            8116,
            "Argument data type varchar is invalid for argument 3 of Date_Bucket function.");
}
