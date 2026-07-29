using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sys.time_zone_info</c>: the Windows time-zone catalog. mssql-django
/// probes it as its zoneinfo-capability check, and the capability is genuine
/// here — <c>AT TIME ZONE</c> already matches real including DST.
/// </summary>
[TestClass]
public sealed class TimeZoneInfoCatalogTests
{
    /// <summary>
    /// The row count and names match the reference server's 141 Windows ids.
    /// Names are baked because real always reports Windows ids while .NET
    /// yields IANA names on Linux; the offsets stay computed.
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
}
