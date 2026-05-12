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
    /// Parses a string into a <see cref="DateOnly"/> using SQL Server's
    /// invariant ISO-8601 forms: <c>yyyy-MM-dd</c> and <c>yyyyMMdd</c>, plus
    /// <c>yyyy-MM-ddTHH:mm:ss[.fffffff]</c> (time portion discarded). SQL Server
    /// accepts many additional locale-sensitive formats; the simulator handles
    /// only the language-neutral ones for now and raises Msg 241 otherwise.
    /// </summary>
    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : DateTime.TryParseExact(value, dateAsDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateOnly.FromDateTime(dt)
        : throw SimulatedSqlException.ConversionFailedDateTimeFromString();

    private static readonly string[] dateFormats =
    [
        "yyyy-MM-dd",
        "yyyyMMdd",
    ];

    private static readonly string[] dateAsDateTimeFormats =
    [
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.f",
        "yyyy-MM-ddTHH:mm:ss.ff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.ffff",
        "yyyy-MM-ddTHH:mm:ss.fffff",
        "yyyy-MM-ddTHH:mm:ss.ffffff",
        "yyyy-MM-ddTHH:mm:ss.fffffff",
    ];

    /// <summary>
    /// Parses a string into a <see cref="DateTime"/> for datetime2 storage,
    /// accepting ISO-8601 forms with either <c>T</c> or space separator and
    /// optional fractional seconds (1-7 digits). Date-only inputs are also
    /// accepted (time defaults to midnight). Locale-sensitive forms aren't
    /// modeled; out-of-range or unparseable inputs raise Msg 241.
    /// </summary>
    private static DateTime ParseDateTime2(string value) =>
        DateTime.TryParseExact(value, dateTime2Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt
        : DateOnly.TryParseExact(value, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToDateTime(TimeOnly.MinValue)
        : throw SimulatedSqlException.ConversionFailedDateTimeFromString();

    /// <summary>
    /// Parses a string into a <see cref="DateTime"/> for legacy <c>datetime</c>
    /// storage. Accepts the same forms as <see cref="ParseDateTime2"/>, plus
    /// the legacy round-trip format <c>"MMM d yyyy h:mmtt"</c> (case-
    /// insensitive — matches what cast(datetime → varchar) emits) and the
    /// US slash forms <c>"M/d/yyyy"</c> / <c>"M/d/yyyy HH:mm:ss"</c>. Empty
    /// strings convert to <c>1900-01-01 00:00</c> (matching SQL Server). The
    /// year-only and date-with-time-and-no-seconds short-hands are also
    /// accepted. Out-of-range or unparseable inputs raise Msg 241.
    /// </summary>
    private static DateTime ParseLegacyDateTime(string value) =>
        TryParseLegacyDateTime(value, out var result)
            ? result
            : throw SimulatedSqlException.ConversionFailedDateTimeFromString();

    /// <summary>
    /// Parses a string into a <see cref="DateTime"/> for <c>smalldatetime</c>
    /// storage. Accepts the same forms as <see cref="ParseLegacyDateTime"/>;
    /// the only divergence is the error path — SQL Server raises a distinct
    /// <c>Msg 295</c> for <c>smalldatetime</c> instead of the <c>Msg 241</c>
    /// used by every other date/time target.
    /// </summary>
    private static DateTime ParseSmallDateTime(string value) =>
        TryParseLegacyDateTime(value, out var result)
            ? result
            : throw SimulatedSqlException.ConversionFailedSmallDateTimeFromString();

    /// <summary>
    /// Shared body of <see cref="ParseLegacyDateTime"/> and
    /// <see cref="ParseSmallDateTime"/>. Returns whether the string parsed;
    /// the caller throws the appropriate Msg-241/Msg-295 factory on failure.
    /// Also reachable from <c>ISDATE</c>, which wraps with an additional
    /// 1753-9999 year-range gate (the shared parser accepts pre-1753
    /// values via the datetime2 paths).
    /// </summary>
    internal static bool TryParseLegacyDateTime(string value, out DateTime result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = DateTimeSqlType.BaseDate;
            return true;
        }
        if (DateTime.TryParseExact(value, dateTime2Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;
        if (DateOnly.TryParseExact(value, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            result = d.ToDateTime(TimeOnly.MinValue);
            return true;
        }
        if (DateTime.TryParseExact(value, legacyDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowInnerWhite | DateTimeStyles.AllowWhiteSpaces, out result))
            return true;
        if (TimeSpan.TryParseExact(value, timeFormats, CultureInfo.InvariantCulture, out var ts) && ts.Ticks is >= 0 and < TimeSpan.TicksPerDay)
        {
            result = DateTimeSqlType.BaseDate.Add(ts);
            return true;
        }
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1753 and <= 9999)
        {
            result = new DateTime(year, 1, 1);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Time-of-day format for CONVERT style 0: <c>"h:mmtt"</c> with a
    /// single-digit hour (no leading space) and no seconds. Distinct from
    /// the time portion of <see cref="FormatLegacyDateTime"/>, which
    /// right-aligns the hour in two characters because it sits inside a
    /// fixed-width datetime string.
    /// </summary>
    private static string FormatLegacyTimeOfDay(TimeSpan value)
    {
        var hour12 = ((value.Hours + 11) % 12) + 1;
        var ampm = value.Hours < 12 ? "AM" : "PM";
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}{2}", hour12, value.Minutes, ampm);
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

    private static readonly string[] legacyDateTimeFormats =
    [
        "MMM d yyyy h:mmtt",
        "MMM d yyyy h:mm:sstt",
        "MMM d yyyy h:mm:ss.ffftt",
        "M/d/yyyy",
        "M/d/yyyy H:mm",
        "M/d/yyyy H:mm:ss",
        "M/d/yyyy H:mm:ss.fff",
    ];

    private static readonly string[] dateTime2Formats =
    [
        // No-seconds variant: SQL Server accepts both space-separated and
        // T-separated date-and-time strings without the seconds component.
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.f",
        "yyyy-MM-dd HH:mm:ss.ff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fffff",
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss.fffffff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.f",
        "yyyy-MM-ddTHH:mm:ss.ff",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.ffff",
        "yyyy-MM-ddTHH:mm:ss.fffff",
        "yyyy-MM-ddTHH:mm:ss.ffffff",
        "yyyy-MM-ddTHH:mm:ss.fffffff",
    ];

    /// <summary>
    /// Parses a string into a <see cref="TimeSpan"/> for time storage. Accepts
    /// <c>HH:mm[:ss[.fffffff]]</c>; locale-sensitive forms aren't modeled.
    /// Out-of-range or unparseable inputs raise Msg 241.
    /// </summary>
    private static TimeSpan ParseTime(string value) =>
        TimeSpan.TryParseExact(value, timeFormats, CultureInfo.InvariantCulture, out var ts) && ts.Ticks is >= 0 and < TimeSpan.TicksPerDay
            ? ts
            : throw SimulatedSqlException.ConversionFailedDateTimeFromString();

    private static readonly string[] timeFormats =
    [
        @"hh\:mm\:ss",
        @"hh\:mm\:ss\.f",
        @"hh\:mm\:ss\.ff",
        @"hh\:mm\:ss\.fff",
        @"hh\:mm\:ss\.ffff",
        @"hh\:mm\:ss\.fffff",
        @"hh\:mm\:ss\.ffffff",
        @"hh\:mm\:ss\.fffffff",
        @"hh\:mm",
    ];

    /// <summary>Date-time-with-offset format string with N fractional digits, matching SQL Server's default datetimeoffset(N) ToString.</summary>
    private static string FormatDateTimeOffset(DateTimeOffset value, int precision) =>
        value.ToString(precision == 0 ? "yyyy-MM-dd HH:mm:ss zzz" : "yyyy-MM-dd HH:mm:ss." + new string('f', precision) + " zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a string into a <see cref="DateTimeOffset"/> for datetimeoffset
    /// storage. Accepts the same date-and-time forms as
    /// <see cref="ParseDateTime2"/>, optionally followed by a space and a
    /// signed <c>±HH:mm</c> offset (the SQL Server textual default). When the
    /// offset is absent, SQL Server treats the value as <c>+00:00</c>.
    /// </summary>
    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.TryParseExact(value, dateTimeOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto
        : DateTime.TryParseExact(value, dateTime2Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? new DateTimeOffset(dt, TimeSpan.Zero)
        : DateOnly.TryParseExact(value, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
        : throw SimulatedSqlException.ConversionFailedDateTimeFromString();

    private static readonly string[] dateTimeOffsetFormats =
    [
        "yyyy-MM-dd HH:mm:ss zzz",
        "yyyy-MM-dd HH:mm:ss.f zzz",
        "yyyy-MM-dd HH:mm:ss.ff zzz",
        "yyyy-MM-dd HH:mm:ss.fff zzz",
        "yyyy-MM-dd HH:mm:ss.ffff zzz",
        "yyyy-MM-dd HH:mm:ss.fffff zzz",
        "yyyy-MM-dd HH:mm:ss.ffffff zzz",
        "yyyy-MM-dd HH:mm:ss.fffffff zzz",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ss.fzzz",
        "yyyy-MM-ddTHH:mm:ss.ffzzz",
        "yyyy-MM-ddTHH:mm:ss.fffzzz",
        "yyyy-MM-ddTHH:mm:ss.ffffzzz",
        "yyyy-MM-ddTHH:mm:ss.fffffzzz",
        "yyyy-MM-ddTHH:mm:ss.ffffffzzz",
        "yyyy-MM-ddTHH:mm:ss.fffffffzzz",
    ];
}
