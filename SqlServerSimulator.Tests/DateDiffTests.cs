using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Direct simulator coverage for the <c>DATEDIFF</c> / <c>DATEDIFF_BIG</c>
/// scalar functions. Probe-confirmed against SQL Server 2025 (2026-05-08):
/// boundary-crossing semantics (year boundary <c>2023-12-31 → 2024-01-01 = 1</c>,
/// not elapsed time); accept-everything type compatibility (only
/// <c>tzoffset</c> and <c>iso_week</c> rejected, function-level Msg 9806);
/// <c>datetimeoffset</c> compared via UTC instant; result widths (int vs
/// bigint) and Msg 535 overflow on result-width violation. The two
/// functions share an implementation; only the result type / overflow
/// threshold differ.
/// </summary>
[TestClass]
public sealed class DateDiffTests
{
    [TestMethod]
    [DataRow("year", "2020-01-01", "2024-01-01", 4)]
    [DataRow("year", "2023-12-31", "2024-01-01", 1)]
    [DataRow("quarter", "2024-03-31", "2024-04-01", 1)]
    [DataRow("quarter", "2023-10-15", "2024-02-15", 1)]
    [DataRow("month", "2024-01-31", "2024-02-01", 1)]
    [DataRow("month", "2024-06-01", "2024-06-30", 0)]
    [DataRow("day", "2024-01-01 23:59:59", "2024-01-02 00:00:01", 1)]
    [DataRow("hour", "2024-01-01 12:59:59", "2024-01-01 13:00:01", 1)]
    public void DateDiff_BoundaryCrossing(string part, string startStr, string endStr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select datediff({part}, '{startStr}', '{endStr}')"));

    [TestMethod]
    public void DateDiff_NegativeWhenStartAfterEnd() =>
        AreEqual(-4, ExecuteScalar("select datediff(year, '2024-01-01', '2020-01-01')"));

    [TestMethod]
    public void DateDiff_WeekIsSundayAnchored()
    {
        // 2024-06-15 is Saturday; 2024-06-16 is Sunday — boundary crossed.
        AreEqual(1, ExecuteScalar("select datediff(week, '2024-06-15', '2024-06-16')"));
        // 2024-06-16 (Sun) → 2024-06-22 (Sat): same week, no boundary.
        AreEqual(0, ExecuteScalar("select datediff(week, '2024-06-16', '2024-06-22')"));
        // Weekday is treated as plain day count (probe: 7 for 2024-06-15→2024-06-22).
        AreEqual(7, ExecuteScalar("select datediff(weekday, '2024-06-15', '2024-06-22')"));
    }

    [TestMethod]
    [DataRow("yy", 4)]
    [DataRow("yyyy", 4)]
    [DataRow("ww", 1)]
    [DataRow("wk", 1)]
    [DataRow("dw", 7)]
    [DataRow("dy", 7)]
    [DataRow("hh", 0)]
    public void DateDiff_AcceptsKeywordAliases(string alias, int expected)
    {
        var sql = alias is "ww" or "wk"
            ? $"select datediff({alias}, '2024-06-15', '2024-06-22')"
            : alias is "dw" or "dy"
                ? $"select datediff({alias}, '2024-06-15', '2024-06-22')"
                : alias is "hh"
                    ? $"select datediff({alias}, '2024-01-01 13:00', '2024-01-01 13:30')"
                    : $"select datediff({alias}, '2020-01-01', '2024-01-01')";
        AreEqual(expected, ExecuteScalar(sql));
    }

    [TestMethod]
    public void DateDiff_NullStartOrEnd_ReturnsTypedNullInt()
    {
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select datediff(day, cast(null as date), '2024-01-01')"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select datediff(day, '2024-01-01', cast(null as date))"));
    }

    [TestMethod]
    public void DateDiffBig_ReturnsBigint()
    {
        AreEqual(86400000000000L, ExecuteScalar("select datediff_big(nanosecond, '2024-01-01', '2024-01-02')"));
        AreEqual(4L, ExecuteScalar("select datediff_big(year, '2020-01-01', '2024-01-01')"));
    }

    [TestMethod]
    public void DateDiffBig_NullStartOrEnd_ReturnsTypedNullBigint() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select datediff_big(day, cast(null as date), '2024-01-01')"));

    [TestMethod]
    public void DateDiff_AcceptsAllTypeCombinations()
    {
        // date - time mix: time anchored to 1900-01-01 (probe-confirmed).
        AreEqual(5, ExecuteScalar("select datediff(hour, cast('1900-01-01' as date), cast('05:00' as time))"));
        // year/day on time-only inputs returns 0 (no error like DATEPART would raise Msg 9810).
        AreEqual(0, ExecuteScalar("select datediff(year, cast('10:00' as time), cast('11:00' as time))"));
        AreEqual(0, ExecuteScalar("select datediff(day, cast('10:00' as time), cast('11:00' as time))"));
        // hour on date-only: always 24 × day diff.
        AreEqual(3984, ExecuteScalar("select datediff(hour, cast('2024-01-01' as date), cast('2024-06-15' as date))"));
    }

    [TestMethod]
    public void DateDiff_DateTimeOffset_UsesUtcInstant()
    {
        // Same wall-clock, different offsets → identical UTC instants → 0.
        AreEqual(0, ExecuteScalar("select datediff(hour, cast('2024-01-01 12:00:00 +00:00' as datetimeoffset), cast('2024-01-01 17:00:00 +05:00' as datetimeoffset))"));
        // Same wall-clock literal but +5 offset shifts UTC backward by 5h.
        AreEqual(-5, ExecuteScalar("select datediff(hour, cast('2024-01-01 12:00:00 +00:00' as datetimeoffset), cast('2024-01-01 12:00:00 +05:00' as datetimeoffset))"));
    }

    [TestMethod]
    public void DateDiff_TzOffsetPart_RaisesMsg9806()
    {
        // tzoffset is rejected at the function level, regardless of operand type.
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff(tzoffset, cast('2024-01-01 00:00:00 +00:00' as datetimeoffset), cast('2024-06-15 00:00:00 +00:00' as datetimeoffset))"));
        AreEqual("The datepart tzoffset is not supported by date function datediff.", ex.Message);
        AreEqual("9806", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DateDiff_IsoWeekPart_RaisesMsg9806()
    {
        // iso_week works in DATEPART/DATEADD but is unconditionally rejected by DATEDIFF.
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff(iso_week, '2024-06-15', '2024-06-22')"));
        AreEqual("The datepart iso_week is not supported by date function datediff.", ex.Message);
        AreEqual("9806", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DateDiffBig_TzOffset_MessageEmbedsFunctionName()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff_big(iso_week, '2024-06-15', '2024-06-22')"));
        AreEqual("The datepart iso_week is not supported by date function datediff_big.", ex.Message);
    }

    [TestMethod]
    public void DateDiff_UnknownDatepart_RaisesMsg155()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff(badpart, '2024-01-01', '2024-06-15')"));
        AreEqual("'badpart' is not a recognized datediff option.", ex.Message);
        AreEqual("155", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DateDiffBig_UnknownDatepart_MessageSaysDatediffBig()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff_big(badpart, '2024-01-01', '2024-06-15')"));
        AreEqual("'badpart' is not a recognized datediff_big option.", ex.Message);
    }

    [TestMethod]
    public void DateAdd_UnknownDatepart_MessageSaysDateadd()
    {
        // Pre-DATEDIFF, this said "datepart option" — Msg 155 wording is per-caller.
        var ex = Throws<DbException>(() => ExecuteScalar("select dateadd(badpart, 1, '2024-01-01')"));
        AreEqual("'badpart' is not a recognized dateadd option.", ex.Message);
    }

    [TestMethod]
    public void DateDiff_MillisecondOverflow_RaisesMsg535()
    {
        // ~25 days of ms is > int.MaxValue (probe boundary).
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff(millisecond, '2024-01-01', '2024-01-26')"));
        AreEqual("The datediff function resulted in an overflow. The number of dateparts separating two date/time instances is too large. Try to use datediff with a less precise datepart.", ex.Message);
        AreEqual("535", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DateDiff_MillisecondJustUnderInt_DoesNotOverflow()
    {
        // 24 days × 86400000 ms = 2,073,600,000 — fits in int.MaxValue (2,147,483,647).
        AreEqual(2073600000, ExecuteScalar("select datediff(millisecond, '2024-01-01', '2024-01-25')"));
    }

    [TestMethod]
    public void DateDiffBig_MillisecondLargeRange_StaysInBigint() =>
        AreEqual(3913056000000L, ExecuteScalar("select datediff_big(millisecond, '1900-01-01', '2024-01-01')"));

    [TestMethod]
    public void DateDiffBig_NanosecondCenturies_OverflowsToMsg535()
    {
        // 0001 → 9999 in nanoseconds overflows even bigint; probe-confirmed Msg 535.
        var ex = Throws<DbException>(() => ExecuteScalar("select datediff_big(nanosecond, cast('0001-01-01' as datetime2), cast('9999-12-31' as datetime2))"));
        AreEqual("535", ex.Data["HelpLink.EvtID"]);
        AreEqual("The datediff_big function resulted in an overflow. The number of dateparts separating two date/time instances is too large. Try to use datediff_big with a less precise datepart.", ex.Message);
    }

    [TestMethod]
    public void DateDiff_SubsecondParts()
    {
        // 100ms → 200ms = 100ms diff.
        AreEqual(100, ExecuteScalar("select datediff(millisecond, '2024-01-01 00:00:00.100', '2024-01-01 00:00:00.200')"));
        // datetime2(7) ticks: 0.0000001s = 1 tick; 0.0000005s = 5 ticks. nanosecond diff = (5-1)*100 = 400.
        AreEqual(400, ExecuteScalar("select datediff(nanosecond, cast('2024-01-01 00:00:00.0000001' as datetime2(7)), cast('2024-01-01 00:00:00.0000005' as datetime2(7)))"));
        // Same tick range in microseconds: both quantize to 0 μs → diff 0.
        AreEqual(0, ExecuteScalar("select datediff(microsecond, cast('2024-01-01 00:00:00.0000001' as datetime2(7)), cast('2024-01-01 00:00:00.0000005' as datetime2(7)))"));
    }

    [TestMethod]
    public void DateDiff_OnColumn_PreservesIntType()
    {
        AreEqual(166, new Simulation().ExecuteScalar("""
            create table t (a datetime2, b datetime2);
            insert t values ('2024-01-01', '2024-06-15');
            select datediff(day, a, b) from t
            """));
    }
}
