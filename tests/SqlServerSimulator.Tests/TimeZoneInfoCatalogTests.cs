using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sys.time_zone_info</c>: the Windows time-zone catalog. mssql-django
/// probes it as its zoneinfo-capability check, and the capability is genuine
/// here — the rows read the same Windows rules <c>AT TIME ZONE</c> does.
/// </summary>
[TestClass]
public sealed class TimeZoneInfoCatalogTests
{
    /// <summary>
    /// The row count and names match the reference server's 141 Windows ids.
    /// </summary>
    [TestMethod]
    public void TimeZoneInfo_ListsTheWindowsZones()
    {
        var sim = new Simulation();
        AreEqual(141, sim.ExecuteScalar<int>("select count(*) from sys.time_zone_info"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from sys.time_zone_info where name = 'Pacific Standard Time'"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from sys.time_zone_info where name = 'UTC'"));
        // The capability probe an ORM runs.
        AreEqual(1, sim.ExecuteScalar<int>("select top 1 1 from sys.time_zone_info"));
    }

    /// <summary>
    /// The offset column is the signed <c>±HH:MM</c> form, and UTC is the one
    /// zone whose value is fixed regardless of when the test runs.
    /// </summary>
    [TestMethod]
    public void TimeZoneInfo_ReportsSignedOffsets()
    {
        var sim = new Simulation();
        AreEqual("+00:00", (string)sim.ExecuteScalar("select current_utc_offset from sys.time_zone_info where name = 'UTC'")!);
        IsFalse(sim.ExecuteScalar<bool>("select is_currently_dst from sys.time_zone_info where name = 'UTC'"));
        // Every row carries a well-formed offset.
        AreEqual(141, sim.ExecuteScalar<int>(
            "select count(*) from sys.time_zone_info where len(current_utc_offset) = 6 and substring(current_utc_offset, 4, 1) = ':'"));
    }

    /// <summary>
    /// Rows come in real's order, by standard offset rather than by id
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void TimeZoneInfo_ListsInRealsOrder() =>
        AreEqual("Dateline Standard Time|UTC-11|Aleutian Standard Time|Line Islands Standard Time", new Simulation().ExecuteScalar("""
            select string_agg(name, '|') within group (order by n)
            from (select name, row_number() over (order by (select null)) n from sys.time_zone_info) z
            where n in (1, 2, 3, 141)
            """));

    /// <summary>
    /// Each row's offset is the zone's at the statement's instant, the one
    /// <c>AT TIME ZONE</c> gives the same instant.
    /// </summary>
    [TestMethod]
    public void TimeZoneInfo_OffsetsAgreeWithAtTimeZone() =>
        AreEqual(141, new Simulation().ExecuteScalar(
            "select count(*) from sys.time_zone_info where current_utc_offset = right(convert(varchar(40), sysdatetimeoffset() at time zone name, 121), 6)"));
}
