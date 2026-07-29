using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class BuiltInResources
{
    /// <summary>
    /// The Windows time-zone identifiers <c>sys.time_zone_info</c> lists, in the
    /// order real SQL Server returns them (probed from SQL Server 2025 — 141
    /// rows). The names are baked rather than projected from
    /// <c>TimeZoneInfo.GetSystemTimeZones()</c> because real always reports
    /// Windows ids while that call yields IANA names on Linux; the offsets are
    /// still computed live, so only the naming is fixed.
    /// </summary>
    internal static readonly string[] WindowsTimeZoneIds =
    [
        "Afghanistan Standard Time",
        "Alaskan Standard Time",
        "Aleutian Standard Time",
        "Altai Standard Time",
        "Arab Standard Time",
        "Arabian Standard Time",
        "Arabic Standard Time",
        "Argentina Standard Time",
        "Astrakhan Standard Time",
        "Atlantic Standard Time",
        "AUS Central Standard Time",
        "Aus Central W. Standard Time",
        "AUS Eastern Standard Time",
        "Azerbaijan Standard Time",
        "Azores Standard Time",
        "Bahia Standard Time",
        "Bangladesh Standard Time",
        "Belarus Standard Time",
        "Bougainville Standard Time",
        "Canada Central Standard Time",
        "Cape Verde Standard Time",
        "Caucasus Standard Time",
        "Cen. Australia Standard Time",
        "Central America Standard Time",
        "Central Asia Standard Time",
        "Central Brazilian Standard Time",
        "Central Europe Standard Time",
        "Central European Standard Time",
        "Central Pacific Standard Time",
        "Central Standard Time",
        "Central Standard Time (Mexico)",
        "Chatham Islands Standard Time",
        "China Standard Time",
        "Cuba Standard Time",
        "Dateline Standard Time",
        "E. Africa Standard Time",
        "E. Australia Standard Time",
        "E. Europe Standard Time",
        "E. South America Standard Time",
        "Easter Island Standard Time",
        "Eastern Standard Time",
        "Eastern Standard Time (Mexico)",
        "Egypt Standard Time",
        "Ekaterinburg Standard Time",
        "Fiji Standard Time",
        "FLE Standard Time",
        "Georgian Standard Time",
        "GMT Standard Time",
        "Greenland Standard Time",
        "Greenwich Standard Time",
        "GTB Standard Time",
        "Haiti Standard Time",
        "Hawaiian Standard Time",
        "India Standard Time",
        "Iran Standard Time",
        "Israel Standard Time",
        "Jordan Standard Time",
        "Kaliningrad Standard Time",
        "Kamchatka Standard Time",
        "Korea Standard Time",
        "Libya Standard Time",
        "Line Islands Standard Time",
        "Lord Howe Standard Time",
        "Magadan Standard Time",
        "Magallanes Standard Time",
        "Marquesas Standard Time",
        "Mauritius Standard Time",
        "Mid-Atlantic Standard Time",
        "Middle East Standard Time",
        "Montevideo Standard Time",
        "Morocco Standard Time",
        "Mountain Standard Time",
        "Mountain Standard Time (Mexico)",
        "Myanmar Standard Time",
        "N. Central Asia Standard Time",
        "Namibia Standard Time",
        "Nepal Standard Time",
        "New Zealand Standard Time",
        "Newfoundland Standard Time",
        "Norfolk Standard Time",
        "North Asia East Standard Time",
        "North Asia Standard Time",
        "North Korea Standard Time",
        "Omsk Standard Time",
        "Pacific SA Standard Time",
        "Pacific Standard Time",
        "Pacific Standard Time (Mexico)",
        "Pakistan Standard Time",
        "Paraguay Standard Time",
        "Qyzylorda Standard Time",
        "Romance Standard Time",
        "Russia Time Zone 10",
        "Russia Time Zone 11",
        "Russia Time Zone 3",
        "Russian Standard Time",
        "SA Eastern Standard Time",
        "SA Pacific Standard Time",
        "SA Western Standard Time",
        "Saint Pierre Standard Time",
        "Sakhalin Standard Time",
        "Samoa Standard Time",
        "Sao Tome Standard Time",
        "Saratov Standard Time",
        "SE Asia Standard Time",
        "Singapore Standard Time",
        "South Africa Standard Time",
        "South Sudan Standard Time",
        "Sri Lanka Standard Time",
        "Sudan Standard Time",
        "Syria Standard Time",
        "Taipei Standard Time",
        "Tasmania Standard Time",
        "Tocantins Standard Time",
        "Tokyo Standard Time",
        "Tomsk Standard Time",
        "Tonga Standard Time",
        "Transbaikal Standard Time",
        "Turkey Standard Time",
        "Turks And Caicos Standard Time",
        "Ulaanbaatar Standard Time",
        "US Eastern Standard Time",
        "US Mountain Standard Time",
        "UTC",
        "UTC+12",
        "UTC+13",
        "UTC-02",
        "UTC-08",
        "UTC-09",
        "UTC-11",
        "Venezuela Standard Time",
        "Vladivostok Standard Time",
        "Volgograd Standard Time",
        "W. Australia Standard Time",
        "W. Central Africa Standard Time",
        "W. Europe Standard Time",
        "W. Mongolia Standard Time",
        "West Asia Standard Time",
        "West Bank Standard Time",
        "West Pacific Standard Time",
        "Yakutsk Standard Time",
        "Yukon Standard Time",
    ];

    /// <summary>
    /// IANA fallbacks for the Windows ids .NET can't resolve on this host — the
    /// ICU mapping covers 132 of the 141 directly. Each alias was checked
    /// against the reference server's reported offset and DST flag.
    /// </summary>
    /// <remarks>
    /// Seven of the nine match real exactly. <c>Kamchatka Standard Time</c> and
    /// <c>Mid-Atlantic Standard Time</c> are deprecated Windows zones that
    /// Microsoft retains with obsolete DST rules and that have no IANA
    /// equivalent, so they report their standard offset with
    /// <c>is_currently_dst = 0</c> where real reports the DST-shifted offset.
    /// </remarks>
    internal static readonly FrozenDictionary<string, string> WindowsTimeZoneIanaFallbacks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Argentina Standard Time"] = "America/Argentina/Buenos_Aires",
            ["FLE Standard Time"] = "Europe/Kyiv",
            ["Greenland Standard Time"] = "America/Nuuk",
            ["India Standard Time"] = "Asia/Kolkata",
            ["Kamchatka Standard Time"] = "Asia/Kamchatka",
            ["Mid-Atlantic Standard Time"] = "Atlantic/South_Georgia",
            ["Myanmar Standard Time"] = "Asia/Yangon",
            ["Nepal Standard Time"] = "Asia/Kathmandu",
            ["US Eastern Standard Time"] = "America/Indiana/Indianapolis",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One row per <see cref="WindowsTimeZoneIds"/> entry, with the offset and
    /// DST flag resolved at the current instant — the shape
    /// <c>sys.time_zone_info</c> exposes. A name that resolves neither directly
    /// nor through <see cref="WindowsTimeZoneIanaFallbacks"/> is skipped rather
    /// than reported with a wrong offset.
    /// </summary>
    private static IEnumerable<SqlValue[]> TimeZoneInfoRows()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in WindowsTimeZoneIds)
        {
            if (ResolveWindowsTimeZone(id) is not { } zone)
                continue;
            var offset = zone.GetUtcOffset(now);
            var sign = offset < TimeSpan.Zero ? '-' : '+';
            yield return
            [
                SqlValue.FromString(NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), id),
                SqlValue.FromString(
                    NVarcharSqlType.Get(6, Collation.Catalog, Coercibility.Implicit),
                    $"{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}"),
                SqlValue.FromBoolean(zone.IsDaylightSavingTime(now)),
            ];
        }
    }

    /// <summary>
    /// Resolves a Windows time-zone id, falling back to the IANA alias table
    /// for the ids this host's ICU mapping doesn't cover.
    /// </summary>
    private static TimeZoneInfo? ResolveWindowsTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall through to the alias table.
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }

        if (!WindowsTimeZoneIanaFallbacks.TryGetValue(id, out var iana))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
