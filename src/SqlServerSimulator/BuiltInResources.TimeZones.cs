using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class BuiltInResources
{
    /// <summary>
    /// One row per Windows zone, in real's order, with the offset and DST
    /// flag the zone's rules give at the statement's instant — the shape
    /// <c>sys.time_zone_info</c> exposes.
    /// </summary>
    private static IEnumerable<SqlValue[]> TimeZoneInfoRows(DateTime utcNow)
    {
        var ticks = utcNow.Ticks;
        foreach (var zone in WindowsTimeZone.All)
        {
            var offset = zone.OffsetMinutesAt(ticks);
            var sign = offset < 0 ? '-' : '+';
            offset = Math.Abs(offset);
            yield return
            [
                SqlValue.FromString(NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), zone.Name),
                SqlValue.FromString(
                    NVarcharSqlType.Get(6, Collation.Catalog, Coercibility.Implicit),
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{sign}{offset / 60:00}:{offset % 60:00}")),
                SqlValue.FromBoolean(zone.IsDaylightAt(ticks)),
            ];
        }
    }
}
