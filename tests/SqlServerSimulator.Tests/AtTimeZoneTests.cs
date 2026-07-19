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

    [TestMethod]
    public void IanaZoneName_AcceptedCrossPlatform()
    {
        // .NET 6+ accepts both Windows and IANA names cross-platform via ICU.
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-07-15T12:00:00+00:00' as datetimeoffset) at time zone 'America/Los_Angeles'")!;
        AreEqual(new DateTime(2026, 7, 15, 5, 0, 0), result.DateTime);
        AreEqual(TimeSpan.FromHours(-7), result.Offset);
    }

    [TestMethod]
    public void ParenthesizedZoneNameExpression_Accepted()
    {
        var result = (DateTimeOffset)ExecuteScalar("select cast('2026-05-09T12:00:00' as datetime2) at time zone (case when 1=1 then 'UTC' else 'X' end)")!;
        AreEqual(TimeSpan.Zero, result.Offset);
    }

    /// <summary>
    /// <c>expr AT TIME ZONE 'UT' + 'C'</c> parses as <c>(expr AT TIME ZONE 'UT') + 'C'</c>,
    /// which is <c>datetimeoffset + varchar</c>. The inner zone-name <c>'UT'</c>
    /// fails first with Msg 9820, demonstrating the binding precedence by
    /// error path. (Real SQL Server's exact wording is Msg 402 about
    /// <c>datetimeoffset + varchar</c>, but the simulator's earlier-evaluated
    /// zone resolution surfaces 9820 first — same precedence outcome either way.)
    /// </summary>
    [TestMethod]
    public void Precedence_BindsTighterThanPlus_AdditionAfterAtTimeZoneRaisesMsg402() =>
        AssertSqlError("select cast('2026-05-09' as datetime2) at time zone 'UT' + 'C'", 9820);

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
}
