using System.Globalization;

namespace SqlServerSimulator.Storage;

internal readonly partial struct SqlValue
{
    /// <summary>Date-time format string with N fractional digits, matching SQL Server's default datetime2(N) ToString.</summary>
    private static string DateTime2Format(int precision) =>
        precision == 0 ? "yyyy-MM-dd HH:mm:ss"
        : "yyyy-MM-dd HH:mm:ss." + new string('f', precision);

    /// <summary>Time-of-day format with N fractional digits, matching SQL Server's default time(N) ToString.</summary>
    private static string FormatTime(TimeSpan value, int precision)
    {
        // TimeSpan formatting needs the colons quoted; using DateTime indirection
        // keeps the format string identical in spirit to DateTime2Format.
        var asDt = DateTime.MinValue.Add(value);
        return precision == 0
            ? asDt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : asDt.ToString("HH:mm:ss." + new string('f', precision), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads <paramref name="value"/> as one of the four newer date-time types
    /// does (<see cref="DateTimeText.TryParse"/>), every failure Msg 241.
    /// </summary>
    private static DateTimeText ReadModernDateTimeText(string value) =>
        DateTimeText.TryParse(value, legacy: false, out var text) == DateTimeTextError.None
            ? text
            : throw SimulatedSqlException.ConversionFailedDateTimeFromString();

    private static DateOnly ParseDate(string value) => ReadModernDateTimeText(value).DateOrBase;

    private static DateTime ParseDateTime2(string value)
    {
        var text = ReadModernDateTimeText(value);
        // A fraction rounded up past 9999-12-31's last tick has nowhere to go.
        return text.DateOrBase == DateOnly.MaxValue && text.TimeTicks >= TimeSpan.TicksPerDay
            ? throw SimulatedSqlException.ConversionFailedDateTimeFromString()
            : text.DateTime;
    }

    /// <summary>
    /// A time-of-day that rounded up to midnight stays on the day's last tick,
    /// where the date-bearing types carry it into the next day (probed
    /// 2026-09-24 against SQL Server 2025).
    /// </summary>
    private static TimeSpan ParseTime(string value) =>
        new(Math.Min(ReadModernDateTimeText(value).TimeTicks, TimeSpan.TicksPerDay - 1));

    /// <summary>
    /// With no offset written the value is <c>+00:00</c>; a <c>Z</c> is the
    /// same. An offset carrying the UTC instant outside years 1–9999 is
    /// Msg 8114 naming <paramref name="sourceType"/>.
    /// </summary>
    private static DateTimeOffset ParseDateTimeOffset(string value, SqlType sourceType)
    {
        var instant = ParseDateTime2(value);
        var offset = ReadModernDateTimeText(value).Offset ?? TimeSpan.Zero;
        var utc = instant.Ticks - offset.Ticks;
        return utc < DateTime.MinValue.Ticks || utc > DateTime.MaxValue.Ticks
            ? throw SimulatedSqlException.DateTimeOffsetUtcOutOfRange(sourceType)
            : new DateTimeOffset(instant, offset);
    }

    /// <summary>
    /// Reads <paramref name="value"/> as <c>datetime</c> does: Msg 241 for a
    /// string it can't read, Msg 242 for one naming a value that doesn't
    /// exist, naming a Unicode source <c>nvarchar</c>. The 1/300-second
    /// rounding and the 1753–9999 range are <see cref="FromDateTime(DateTime)"/>'s.
    /// </summary>
    private static DateTime ParseLegacyDateTime(string value, SqlType sourceType) =>
        DateTimeText.TryParse(value, legacy: true, out var text) switch
        {
            DateTimeTextError.None => text.DateTime,
            DateTimeTextError.Range => throw SimulatedSqlException.OutOfRangeDateTimeConversion(SqlType.DateTime, RangeErrorSource(sourceType)),
            _ => throw SimulatedSqlException.ConversionFailedDateTimeFromString(),
        };

    /// <summary>
    /// As <see cref="ParseLegacyDateTime"/>, but a string it can't read is
    /// <c>smalldatetime</c>'s own Msg 295.
    /// </summary>
    private static DateTime ParseSmallDateTime(string value, SqlType sourceType) =>
        DateTimeText.TryParse(value, legacy: true, out var text) switch
        {
            DateTimeTextError.None => text.DateTime,
            DateTimeTextError.Range => throw SimulatedSqlException.OutOfRangeDateTimeConversion(SqlType.SmallDateTime, RangeErrorSource(sourceType)),
            _ => throw SimulatedSqlException.ConversionFailedSmallDateTimeFromString(),
        };

    private static NVarcharSqlType? RangeErrorSource(SqlType sourceType) =>
        SqlType.IsNationalStringCategory(sourceType) ? SqlType.NVarchar : null;

    /// <summary>
    /// Whether <paramref name="value"/> reads as a <c>datetime</c> string, for
    /// <c>ISDATE</c>, which applies the type's year range itself.
    /// </summary>
    internal static bool TryParseLegacyDateTime(string value, out DateTime result)
    {
        var read = DateTimeText.TryParse(value, legacy: true, out var text) == DateTimeTextError.None;
        result = read ? text.DateTime : default;
        return read;
    }

    /// <summary>
    /// Format SQL Server's legacy <c>datetime</c> emits when CAST to a string:
    /// <c>"MMM d yyyy h:mmtt"</c> with the day right-aligned in 2 chars
    /// (single-digit days get a leading space) and the 12-hour hour likewise
    /// right-aligned. Length is always 19 chars (e.g. <c>"Jan  5 2024  1:00AM"</c>).
    /// Seconds and fractional seconds aren't included in the default format.
    /// </summary>
    private static string FormatLegacyDateTime(DateTime value)
    {
        var hour12 = ((value.Hour + 11) % 12) + 1;
        var ampm = value.Hour < 12 ? "AM" : "PM";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:MMM} {1,2} {0:yyyy} {2,2}:{0:mm}{3}",
            value, value.Day, hour12, ampm);
    }

    /// <summary>Date-time-with-offset format string with N fractional digits, matching SQL Server's default datetimeoffset(N) ToString.</summary>
    private static string FormatDateTimeOffset(DateTimeOffset value, int precision) =>
        value.ToString(precision == 0 ? "yyyy-MM-dd HH:mm:ss zzz" : "yyyy-MM-dd HH:mm:ss." + new string('f', precision) + " zzz", CultureInfo.InvariantCulture);

}
