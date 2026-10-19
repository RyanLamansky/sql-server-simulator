using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// SQL <c>AT TIME ZONE</c>: converts datetime/datetime2/datetimeoffset to
/// datetimeoffset in the supplied zone. Probe-anchored against SQL Server
/// 2025 (2026-05-09).
/// </summary>
[TestClass]
public sealed class AtTimeZoneTests
{
    [TestMethod]
    public void DateTime2_AtUtc_AttachesZeroOffset()
    {
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-05-09T12:00:00' as datetime2) at time zone 'UTC'")!;
        AreEqual(new DateTime(2026, 5, 9, 12, 0, 0), result.DateTime);
        AreEqual(TimeSpan.Zero, result.Offset);
    }

    [TestMethod]
    public void DateTime2_AtPacific_StaysAtSameWallClockWithDaylightOffset()
    {
        // May is daylight time in Pacific (-07:00). datetime2 → datetimeoffset
        // attaches the zone's offset for that wall-clock; the wall-clock value
        // doesn't change.
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-05-09T12:00:00' as datetime2) at time zone 'Pacific Standard Time'")!;
        AreEqual(new DateTime(2026, 5, 9, 12, 0, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-7), result.Offset);
    }

    [TestMethod]
    public void DateTimeOffset_AtUtc_PreservesUtcInstant()
    {
        // datetimeoffset → datetimeoffset converts: same UTC instant,
        // re-expressed in target zone. 12:00+05:00 = 07:00+00:00 UTC.
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-05-09T12:00:00+05:00' as datetimeoffset) at time zone 'UTC'")!;
        AreEqual(new DateTime(2026, 5, 9, 7, 0, 0), result.DateTime);
        AreEqual(TimeSpan.Zero, result.Offset);
    }

    [TestMethod]
    public void DateTimeOffset_AtPacific_ConvertsAndAdjustsOffset()
    {
        // UTC noon in May → Pacific 5am (-07:00, DST).
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-07-15T12:00:00+00:00' as datetimeoffset) at time zone 'Pacific Standard Time'")!;
        AreEqual(new DateTime(2026, 7, 15, 5, 0, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-7), result.Offset);
    }

    [TestMethod]
    public void DateTimeOffset_AtPacific_NoDST_UsesStandardOffset()
    {
        // UTC noon in January (no DST in Pacific) → Pacific 4am (-08:00).
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-01-15T12:00:00+00:00' as datetimeoffset) at time zone 'Pacific Standard Time'")!;
        AreEqual(new DateTime(2026, 1, 15, 4, 0, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-8), result.Offset);
    }

    [TestMethod]
    public void DateTime_LegacyType_ResultPrecision3()
    {
        // datetime AT TIME ZONE 'UTC' → datetimeoffset(3) (precision matches
        // legacy datetime's 1/300s tick rounding floor).
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-05-09T12:00:00' as datetime) at time zone 'UTC'")!;
        AreEqual(2026, result.Year);
        AreEqual(TimeSpan.Zero, result.Offset);
    }

    /// <summary>
    /// Spring-forward gap (Pacific 2026-03-08 02:30 doesn't exist —
    /// the clock jumps 02:00 → 03:00). Real SQL Server shifts the
    /// wall-clock forward by the DST delta (1h) and stamps the post-transition
    /// daylight offset (-07:00).
    /// </summary>
    [TestMethod]
    public void DateTime2_AtPacific_DstSpringForward_ShiftsWallClock()
    {
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-03-08T02:30:00' as datetime2) at time zone 'Pacific Standard Time'")!;
        AreEqual(new DateTime(2026, 3, 8, 3, 30, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-7), result.Offset);
    }

    /// <summary>
    /// Fall-back ambiguous wall-clock (Pacific 2026-11-01 01:30 happens
    /// twice). Real SQL Server picks the first occurrence — the daylight
    /// (-07:00) interpretation, NOT the standard (-08:00) one.
    /// </summary>
    [TestMethod]
    public void DateTime2_AtPacific_DstFallBack_PicksDaylightInterpretation()
    {
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-11-01T01:30:00' as datetime2) at time zone 'Pacific Standard Time'")!;
        AreEqual(new DateTime(2026, 11, 1, 1, 30, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-7), result.Offset);
    }

    /// <summary>
    /// Offsets follow Windows' own per-year rules as real embeds them, never
    /// the host's time-zone history: the first rule reaches back unchanged
    /// (summer time in 1800 Pacific, no local mean time in 1880 Tokyo), the
    /// last reaches forward, and the rule-change years keep the artefacts
    /// real shows at a year boundary (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cast('1800-07-01 12:00' as datetime2(0)) at time zone 'Pacific Standard Time'", "1800-07-01 12:00:00 -07:00")]
    [DataRow("cast('1753-06-01 12:00' as datetime) at time zone 'Pacific Standard Time'", "1753-06-01 12:00:00.000 -07:00")]
    [DataRow("cast('1880-01-01 12:00' as datetime2(0)) at time zone 'Tokyo Standard Time'", "1880-01-01 12:00:00 +09:00")]
    [DataRow("cast('1900-07-01 12:00' as datetime2(0)) at time zone 'GMT Standard Time'", "1900-07-01 12:00:00 +01:00")]
    [DataRow("cast('1752-09-10 12:00' as datetime2(0)) at time zone 'W. Europe Standard Time'", "1752-09-10 12:00:00 +02:00")]
    [DataRow("cast('0001-01-15 12:00' as datetime2(0)) at time zone 'AUS Eastern Standard Time'", "0001-01-15 12:00:00 +11:00")]
    [DataRow("cast('2500-07-01 12:00' as datetime2(0)) at time zone 'Eastern Standard Time'", "2500-07-01 12:00:00 -04:00")]
    [DataRow("cast('3000-01-15 12:00' as datetime2(0)) at time zone 'New Zealand Standard Time'", "3000-01-15 12:00:00 +13:00")]
    // Ramadan suspends Morocco's +01:00; half-hour and quarter-hour zones.
    [DataRow("cast('2026-02-20 12:00' as datetime2(0)) at time zone 'Morocco Standard Time'", "2026-02-20 12:00:00 +00:00")]
    [DataRow("cast('2026-04-01 12:00' as datetime2(0)) at time zone 'Morocco Standard Time'", "2026-04-01 12:00:00 +01:00")]
    [DataRow("cast('2026-07-15 12:00' as datetime2(0)) at time zone 'Lord Howe Standard Time'", "2026-07-15 12:00:00 +10:30")]
    [DataRow("cast('2026-01-15 12:00' as datetime2(0)) at time zone 'Lord Howe Standard Time'", "2026-01-15 12:00:00 +11:00")]
    [DataRow("cast('2026-01-15 12:00' as datetime2(0)) at time zone 'Chatham Islands Standard Time'", "2026-01-15 12:00:00 +13:45")]
    // Zones with no IANA counterpart keep their Windows rules.
    [DataRow("cast('2026-07-01 00:00 +00:00' as datetimeoffset(0)) at time zone 'Kamchatka Standard Time'", "2026-07-01 13:00:00 +13:00")]
    [DataRow("cast('2026-07-01 00:00 +00:00' as datetimeoffset(0)) at time zone 'Mid-Atlantic Standard Time'", "2026-06-30 23:00:00 -01:00")]
    // Each side of a transition instant.
    [DataRow("cast('2026-03-08 09:59:59.9999999 +00:00' as datetimeoffset) at time zone 'Pacific Standard Time'", "2026-03-08 01:59:59.9999999 -08:00")]
    [DataRow("cast('2026-03-08 10:00 +00:00' as datetimeoffset) at time zone 'Pacific Standard Time'", "2026-03-08 03:00:00.0000000 -07:00")]
    [DataRow("cast('2026-04-05 02:30 +00:00' as datetimeoffset(0)) at time zone 'Lord Howe Standard Time'", "2026-04-05 13:00:00 +10:30")]
    // Samoa crosses the date line a day after the change it made.
    [DataRow("cast('2011-12-31 10:59:59 +00:00' as datetimeoffset(0)) at time zone 'Samoa Standard Time'", "2011-12-31 00:59:59 -10:00")]
    [DataRow("cast('2011-12-31 11:00:00 +00:00' as datetimeoffset(0)) at time zone 'Samoa Standard Time'", "2012-01-01 01:00:00 +14:00")]
    // Summer ending on January 1 the year before leaves the first hour of
    // the new year in daylight time on standard time; a rule change's
    // year boundary blips for an hour.
    [DataRow("cast('1904-01-01 03:30 +00:00' as datetimeoffset(0)) at time zone 'Central Brazilian Standard Time'", "1903-12-31 23:30:00 -04:00")]
    [DataRow("cast('1904-01-01 04:00 +00:00' as datetimeoffset(0)) at time zone 'Central Brazilian Standard Time'", "1904-01-01 01:00:00 -03:00")]
    [DataRow("cast('2013-12-31 17:30 +00:00' as datetimeoffset(0)) at time zone 'Altai Standard Time'", "2013-12-31 23:30:00 +06:00")]
    [DataRow("cast('2013-12-31 18:30 +00:00' as datetimeoffset(0)) at time zone 'Altai Standard Time'", "2014-01-01 01:30:00 +07:00")]
    public void Offsets_FollowWindowsRules(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), {expression}, 121)"));

    /// <summary>
    /// A wall clock a transition skips reads with the offset in force before
    /// it and lands past the gap; one a transition repeats reads as the
    /// earlier instant, in both hemispheres — and where Windows writes a
    /// change of standard offset as daylight time ending, the skipped hour
    /// reads with the new offset and lands before the gap (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("2026-03-29 01:30", "GMT Standard Time", "2026-03-29 02:30:00 +01:00")]
    [DataRow("2026-10-25 01:30", "GMT Standard Time", "2026-10-25 01:30:00 +01:00")]
    [DataRow("2026-11-01 01:30", "Pacific Standard Time", "2026-11-01 01:30:00 -07:00")]
    [DataRow("2026-10-04 02:30", "AUS Eastern Standard Time", "2026-10-04 03:30:00 +11:00")]
    [DataRow("2026-04-05 02:30", "AUS Eastern Standard Time", "2026-04-05 02:30:00 +11:00")]
    [DataRow("2026-10-04 02:15", "Lord Howe Standard Time", "2026-10-04 02:45:00 +11:00")]
    [DataRow("2007-12-09 02:45", "Venezuela Standard Time", "2007-12-09 02:45:00 -04:00")]
    [DataRow("2016-03-27 02:30", "Altai Standard Time", "2016-03-27 01:30:00 +06:00")]
    [DataRow("1903-01-01 00:30", "Central Brazilian Standard Time", "1903-01-01 00:30:00 -04:00")]
    public void WallClocks_SkippedAndRepeated_ResolveAsReal(string wall, string zone, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), cast('{wall}' as datetime2(0)) at time zone '{zone}', 121)"));

    /// <summary>
    /// A datetimeoffset whose local time leaves the range clamps to that end
    /// at +00:00, while a wall clock naming an instant outside the UTC range
    /// is Msg 9813 state 1 (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cast('0001-01-01 07:59 +00:00' as datetimeoffset) at time zone 'Pacific Standard Time'", "0001-01-01 00:00:00.0000000 +00:00")]
    [DataRow("cast('9999-12-31 15:00:00.1234 +00:00' as datetimeoffset(4)) at time zone 'Tokyo Standard Time'", "9999-12-31 23:59:59.9999 +00:00")]
    [DataRow("cast('0001-01-01 12:00' as datetime2(0)) at time zone 'Pacific Standard Time'", "0001-01-01 12:00:00 -08:00")]
    public void RangeEdges_LocalOverflowClamps(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select convert(varchar(40), {expression}, 121)"));

    [TestMethod]
    [DataRow("0001-01-01 05:00", "Tokyo Standard Time")]
    [DataRow("9999-12-31 20:00", "Pacific Standard Time")]
    public void RangeEdges_WallClockOutsideUtcRange_RaisesMsg9813(string wall, string zone) =>
        AssertSqlError($"select cast('{wall}' as datetime2) at time zone '{zone}'", 9813, 1,
            "The timezone provided to builtin function AT TIME ZONE would cause the datetimeoffset to overflow the range of valid date range in either UTC or local time.");

    [TestMethod]
    [DataRow("cast(null as datetime2) at time zone 'UTC'")]
    [DataRow("cast('2026-05-09' as datetime2) at time zone null")]
    [DataRow("cast(null as datetime2) at time zone null")]
    public void NullPropagates(string expression) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void DateInput_RaisesMsg8116() =>
        AssertSqlError("select cast('2026-05-09' as date) at time zone 'UTC'", 8116,
            "Argument data type date is invalid for argument 1 of AT TIME ZONE function.");

    [TestMethod]
    public void TimeInput_RaisesMsg8116() =>
        AssertSqlError("select cast('12:00:00' as time) at time zone 'UTC'", 8116,
            "Argument data type time is invalid for argument 1 of AT TIME ZONE function.");

    [TestMethod]
    public void InvalidZoneName_RaisesMsg9820() =>
        AssertSqlError("select cast('2026-05-09T12:00:00' as datetime2) at time zone 'NotARealZone'", 9820,
            "The time zone parameter 'NotARealZone' provided to AT TIME ZONE clause is invalid.");

    [TestMethod]
    public void EmptyZoneName_RaisesMsg9820() =>
        AssertSqlError("select cast('2026-05-09T12:00:00' as datetime2) at time zone ''", 9820,
            "The time zone parameter '' provided to AT TIME ZONE clause is invalid.");

    /// <summary>
    /// Only the Windows ids <c>sys.time_zone_info</c> lists are zone names, in
    /// any case — an IANA name is refused, and so is a padded one.
    /// </summary>
    [TestMethod]
    [DataRow("America/Los_Angeles")]
    [DataRow("Etc/UTC")]
    [DataRow(" UTC")]
    public void NonWindowsZoneName_RaisesMsg9820(string zone) =>
        AssertSqlError($"select cast('2026-07-15T12:00:00+00:00' as datetimeoffset) at time zone '{zone}'", 9820,
            $"The time zone parameter '{zone}' provided to AT TIME ZONE clause is invalid.");

    [TestMethod]
    [DataRow("pacific standard time", -420)]
    [DataRow("India Standard Time", 330)]
    [DataRow("Nepal Standard Time", 345)]
    [DataRow("Myanmar Standard Time", 390)]
    public void WindowsZoneName_ResolvesAnyCase(string zone, int offsetMinutes) =>
        AreEqual(TimeSpan.FromMinutes(offsetMinutes),
            ((DateTimeOffset)ExecuteScalar($"select cast('2026-07-15T12:00:00+00:00' as datetimeoffset) at time zone '{zone}'")!).Offset);

    [TestMethod]
    [DataRow("cast('2024-07-01 12:00' as smalldatetime)", 0)]
    [DataRow("cast('2024-07-01 12:00' as datetime)", 3)]
    [DataRow("cast('2024-07-01 12:00' as datetime2(2))", 2)]
    public void ResultPrecision_FollowsSource(string source, int scale) =>
        AreEqual(scale, ExecuteScalar($"select sql_variant_property(cast({source} at time zone 'UTC' as sql_variant), 'Scale')"));

    [TestMethod]
    public void ParenthesizedZoneNameExpression_Accepted()
    {
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-05-09T12:00:00' as datetime2) at time zone (case when 1=1 then 'UTC' else 'X' end)")!;
        AreEqual(TimeSpan.Zero, result.Offset);
    }

    /// <summary>
    /// <c>expr AT TIME ZONE 'UT' + 'C'</c> parses as <c>(expr AT TIME ZONE 'UT') + 'C'</c>,
    /// which is <c>datetimeoffset + varchar</c> — refused while compiling, so
    /// real's Msg 402 comes ahead of the unknown zone name's runtime Msg 9820.
    /// </summary>
    [TestMethod]
    public void Precedence_BindsTighterThanPlus_AdditionAfterAtTimeZoneRaisesMsg402() =>
        AssertSqlError("select cast('2026-05-09' as datetime2) at time zone 'UT' + 'C'", 402);

    [TestMethod]
    public void Chained_AtTimeZoneAfterAtTimeZone_ReConverts()
    {
        // (utc-instant AT TIME ZONE 'UTC') AT TIME ZONE 'Pacific' should
        // convert the UTC instant to Pacific.
        var result = (DateTimeOffset)ExecuteScalar(
            "select (cast('2026-07-15T12:00:00' as datetime2) at time zone 'UTC') at time zone 'Pacific Standard Time'")!;
        AreEqual(new DateTime(2026, 7, 15, 5, 0, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-7), result.Offset);
    }

    [TestMethod]
    public void OfColumns_FromTable() =>
        AreEqual(TimeSpan.Zero, ((DateTimeOffset)new Simulation().ExecuteScalar("""
            create table t (d datetime2(7));
            insert t values ('2026-05-09T12:00:00');
            select d at time zone 'UTC' from t
            """)!).Offset);

    [TestMethod]
    public void DateTime2Precision_PropagatesToResult()
    {
        // datetime2(3) AT TIME ZONE → datetimeoffset(3); verify by checking
        // that fractional precision is preserved through the round-trip.
        var result = (DateTimeOffset)ExecuteScalar(
            "select cast('2026-05-09T12:00:00.1234567' as datetime2(3)) at time zone 'UTC'")!;
        // datetime2(3) only stores 3 fractional digits, so .1234567 → .123.
        AreEqual(123L * TimeSpan.TicksPerMillisecond, result.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// <c>AT</c> without <c>TIME</c> after it is an alias, a column's or a
    /// table's (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AtWithoutTime_IsAnAlias()
        => AreEqual("1|2|3", new Simulation().ExecuteScalar("""
            create table at (at int);
            insert at values (3);
            select concat_ws('|', x.at, x.b, (select at.at from at at)) from (select 1 at, 2 b) x
            """));

    /// <summary>An operand type AT TIME ZONE doesn't take refuses the batch as it compiles, so nothing ahead of it runs (probed 2026-10-19 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("select d at time zone 'UTC' from t", "date")]
    [DataRow("select s at time zone 'UTC' from t", "varchar")]
    [DataRow("declare @d date = '2021-01-01'; select @d at time zone 'UTC'", "date")]
    public void InvalidOperand_RefusesTheBatch(string query, string typeName)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (d date, s varchar(20))");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand($"select 'first'; {query}");
        var error = Throws<SimulatedSqlException>(() =>
        {
            using var reader = command.ExecuteReader();
            _ = reader.Read();
        });
        AreEqual($"Argument data type {typeName} is invalid for argument 1 of AT TIME ZONE function.", error.Errors[0].Message);
    }
}
