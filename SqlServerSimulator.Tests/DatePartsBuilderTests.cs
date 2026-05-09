using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// The six SQL Server <c>*FROMPARTS</c> date/time builders plus
/// <c>EOMONTH</c>. Probe-anchored against SQL Server 2025 (2026-05-09).
/// </summary>
[TestClass]
public sealed class DatePartsBuilderTests
{
    [TestMethod]
    [DataRow("datefromparts(2026, 5, 9)", "2026-05-09")]
    [DataRow("datefromparts(2024, 2, 29)", "2024-02-29")]
    [DataRow("datefromparts(1, 1, 1)", "0001-01-01")]
    [DataRow("datefromparts(9999, 12, 31)", "9999-12-31")]
    public void DateFromParts_Valid(string expression, string expectedIso) =>
        AreEqual(DateTime.Parse(expectedIso), ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("datefromparts(null, 5, 9)")]
    [DataRow("datefromparts(2026, null, 9)")]
    [DataRow("datefromparts(2026, 5, null)")]
    public void DateFromParts_NullArgPropagates(string expression) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("datefromparts(2026, 13, 1)")]
    [DataRow("datefromparts(2026, 0, 1)")]
    [DataRow("datefromparts(2026, 2, 30)")]
    [DataRow("datefromparts(2025, 2, 29)")]
    [DataRow("datefromparts(0, 1, 1)")]
    [DataRow("datefromparts(10000, 1, 1)")]
    [DataRow("datefromparts(2026, 1, -1)")]
    public void DateFromParts_Invalid_RaisesMsg289(string expression) =>
        AssertSqlError($"select {expression}", 289, "Cannot construct data type date, some of the arguments have values which are not valid.");

    [TestMethod]
    [DataRow("datefromparts(2026.0, 5, 9)")]
    [DataRow("datefromparts('2026', '5', '9')")]
    [DataRow("datefromparts(cast(2026 as bigint), 5, 9)")]
    public void DateFromParts_ImplicitlyCoercesArgs(string expression) =>
        AreEqual(new DateTime(2026, 5, 9), ExecuteScalar($"select {expression}"));

    /// <summary>ms=0 round-trips cleanly through datetime's 1/300s tick rounding.</summary>
    [TestMethod]
    public void DateTimeFromParts_Valid() =>
        AreEqual(new DateTime(2026, 5, 9, 12, 34, 56),
            ExecuteScalar("select datetimefromparts(2026, 5, 9, 12, 34, 56, 0)"));

    [TestMethod]
    public void DateTimeFromParts_FractionalMs_RoundsTo1_300sTick()
    {
        // 789 ms → rounds to the nearest 1/300s tick. Test that the result
        // is within ~1 tick of the requested value to confirm the rounding
        // path is used (vs. straight-passthrough).
        var result = (DateTime)ExecuteScalar("select datetimefromparts(2026, 5, 9, 12, 34, 56, 789)")!;
        var diff = Math.Abs((result - new DateTime(2026, 5, 9, 12, 34, 56, 789)).Ticks);
        IsLessThan(TimeSpan.TicksPerMillisecond * 4, diff, $"datetime should round to 1/300s tick (within ~3.33 ms); got diff {diff} ticks");
    }

    /// <summary>datetime's 1/300s rounding pushes ms 999 at 23:59:59 into the next day.</summary>
    [TestMethod]
    public void DateTimeFromParts_Ms999Rolls() =>
        AreEqual(new DateTime(2026, 5, 10), ExecuteScalar("select datetimefromparts(2026, 5, 9, 23, 59, 59, 999)"));

    [TestMethod]
    [DataRow("datetimefromparts(2026, 5, 9, 24, 0, 0, 0)")]
    [DataRow("datetimefromparts(2026, 5, 9, 0, 60, 0, 0)")]
    [DataRow("datetimefromparts(2026, 5, 9, 0, 0, 0, 1000)")]
    public void DateTimeFromParts_Invalid_RaisesMsg289(string expression) =>
        AssertSqlError($"select {expression}", 289, "Cannot construct data type datetime, some of the arguments have values which are not valid.");

    [TestMethod]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 1234567, 7)", 7)]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 123, 3)", 3)]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 0, 0)", 0)]
    public void DateTime2FromParts_PrecisionRespected(string expression, int precision)
    {
        var result = (DateTime)ExecuteScalar($"select {expression}")!;
        AreEqual(2026, result.Year);
        AreEqual(5, result.Month);
        AreEqual(9, result.Day);
        AreEqual(12, result.Hour);
        AreEqual(34, result.Minute);
        AreEqual(56, result.Second);
        // Precision 7 means full 0.1234567 seconds; lower precisions store
        // proportionally fewer fractional digits (the value is the
        // precision-N integer multiplied to ticks).
        var expectedTicks = precision == 0 ? 0L
            : precision == 3 ? 123 * TimeSpan.TicksPerMillisecond
            : 1234567L;
        AreEqual(expectedTicks, result.Ticks % TimeSpan.TicksPerSecond);
    }

    [TestMethod]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 99999999, 7)")]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 1000, 3)")]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 1, 0)")]
    public void DateTime2FromParts_FractionsOverflow_RaisesMsg289(string expression) =>
        AssertSqlError($"select {expression}", 289, "Cannot construct data type datetime2, some of the arguments have values which are not valid.");

    [TestMethod]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 0, 8)")]
    [DataRow("datetime2fromparts(2026, 5, 9, 12, 34, 56, 0, -1)")]
    public void DateTime2FromParts_PrecisionOutOfRange_RaisesMsg1002(string expression) =>
        AssertSqlError($"select {expression}", 1002);

    [TestMethod]
    public void DateTime2FromParts_NullPrecision_RaisesMsg10760() =>
        AssertSqlError("select datetime2fromparts(2026, 5, 9, 12, 34, 56, 0, null)", 10760,
            "Scale argument is not valid. Valid expressions for data type datetime2 scale argument are integer constants and integer constant expressions.");

    [TestMethod]
    public void DateTime2FromParts_NullValueArg_PropagatesNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select datetime2fromparts(null, 5, 9, 12, 34, 56, 0, 7)"));

    [TestMethod]
    public void DateTimeOffsetFromParts_Standard()
    {
        var result = (DateTimeOffset)ExecuteScalar("select datetimeoffsetfromparts(2026, 5, 9, 12, 34, 56, 1234567, 5, 30, 7)")!;
        AreEqual(new DateTime(2026, 5, 9, 12, 34, 56).AddTicks(1234567), result.DateTime);
        AreEqual(TimeSpan.FromMinutes((5 * 60) + 30), result.Offset);
    }

    [TestMethod]
    public void DateTimeOffsetFromParts_NegativeOffset_PreservesSign()
    {
        var result = (DateTimeOffset)ExecuteScalar("select datetimeoffsetfromparts(2026, 5, 9, 12, 34, 56, 0, -8, 0, 7)")!;
        AreEqual(TimeSpan.FromHours(-8), result.Offset);
    }

    [TestMethod]
    [DataRow("datetimeoffsetfromparts(2026, 5, 9, 12, 34, 56, 0, -5, 30, 7)")]
    [DataRow("datetimeoffsetfromparts(2026, 5, 9, 12, 34, 56, 0, 15, 0, 7)")]
    [DataRow("datetimeoffsetfromparts(2026, 5, 9, 12, 34, 56, 0, 14, 1, 7)")]
    public void DateTimeOffsetFromParts_OffsetInvalid_RaisesMsg289(string expression) =>
        AssertSqlError($"select {expression}", 289, "Cannot construct data type datetimeoffset, some of the arguments have values which are not valid.");

    [TestMethod]
    public void DateTimeOffsetFromParts_NullPrecision_RaisesMsg10760() =>
        AssertSqlError("select datetimeoffsetfromparts(2026, 5, 9, 12, 34, 56, 0, 0, 0, null)", 10760,
            "Scale argument is not valid. Valid expressions for data type datetimeoffset scale argument are integer constants and integer constant expressions.");

    [TestMethod]
    public void SmallDateTimeFromParts_Valid() =>
        AreEqual(new DateTime(2026, 5, 9, 12, 34, 0),
            ExecuteScalar("select smalldatetimefromparts(2026, 5, 9, 12, 34)"));

    [TestMethod]
    public void SmallDateTimeFromParts_NullArgPropagates() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select smalldatetimefromparts(null, 5, 9, 12, 34)"));

    [TestMethod]
    public void TimeFromParts_FullPrecision()
    {
        var span = (TimeSpan)ExecuteScalar("select timefromparts(12, 34, 56, 1234567, 7)")!;
        AreEqual(new TimeSpan(0, 12, 34, 56) + TimeSpan.FromTicks(1234567), span);
    }

    [TestMethod]
    public void TimeFromParts_Hour24_RaisesMsg289() =>
        AssertSqlError("select timefromparts(24, 0, 0, 0, 0)", 289,
            "Cannot construct data type time, some of the arguments have values which are not valid.");

    [TestMethod]
    public void TimeFromParts_NullPrecision_RaisesMsg10760() =>
        AssertSqlError("select timefromparts(12, 34, 56, 0, null)", 10760,
            "Scale argument is not valid. Valid expressions for data type time scale argument are integer constants and integer constant expressions.");

    [TestMethod]
    [DataRow("eomonth(cast('2026-02-15' as date))", "2026-02-28")]
    [DataRow("eomonth(cast('2024-02-15' as date))", "2024-02-29")]
    [DataRow("eomonth(cast('2025-02-15' as date))", "2025-02-28")]
    [DataRow("eomonth(cast('2026-02-15' as datetime2))", "2026-02-28")]
    [DataRow("eomonth(cast('2026-02-15T12:00:00' as datetime))", "2026-02-28")]
    [DataRow("eomonth('2026-02-15')", "2026-02-28")]
    [DataRow("eomonth(cast('2026-02-15' as date), 1)", "2026-03-31")]
    [DataRow("eomonth(cast('2026-02-15' as date), -1)", "2026-01-31")]
    [DataRow("eomonth('2026-02-15', 1)", "2026-03-31")]
    public void EOMonth_Valid(string expression, string expectedIso) =>
        AreEqual(DateTime.Parse(expectedIso), ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void EOMonth_NullStartDate_PropagatesNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select eomonth(null)"));

    /// <summary>Probe-confirmed quirk: NULL <c>month_offset</c> is silently treated as 0.</summary>
    [TestMethod]
    public void EOMonth_NullOffset_TreatedAsZero() =>
        AreEqual(new DateTime(2026, 2, 28),
            ExecuteScalar("select eomonth(cast('2026-02-15' as date), null)"));

    [TestMethod]
    public void DatePartsBuilder_OfColumns_FromTable() =>
        AreEqual(new DateTime(2026, 5, 9), new Simulation().ExecuteScalar("""
            create table t (y int, m int, d int);
            insert t values (2026, 5, 9);
            select datefromparts(y, m, d) from t
            """));

    [TestMethod]
    public void EOMonth_OfColumn_FromTable() =>
        AreEqual(new DateTime(2026, 2, 28), new Simulation().ExecuteScalar("""
            create table t (d date);
            insert t values ('2026-02-15');
            select eomonth(d) from t
            """));
}
