using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage;

internal readonly partial struct SqlValue
{
    /// <summary>
    /// CONVERT style dispatch for date-like → string conversions. Routes
    /// each per-source-type style switch through a dedicated formatter so
    /// the source-specific rules (legacy datetime's colon fractional
    /// separator, datetime2's source-precision fractional digits, time-only
    /// vs date-only source rejection paths) stay co-located.
    /// </summary>
    /// <remarks>
    /// Style numbers grouped by output shape:
    /// <list type="bullet">
    /// <item>0/100 — legacy default: <c>"Mmm d yyyy h:miAM/PM"</c></item>
    /// <item>1/10/12/23/101/102/103/110/112 + 2/3/4/5/6/7/11/104/105/106/107/111 — date-only emit</item>
    /// <item>8/24/108 — <c>"HH:mm:ss"</c> (24-hour, no fractional)</item>
    /// <item>9/109 — legacy default + ms: <c>"Mmm d yyyy h:mi:ss[sep]frac AM/PM"</c></item>
    /// <item>13/113 — Europe default + ms: <c>"d Mmm yyyy HH:mm:ss[sep]frac"</c></item>
    /// <item>14/114 — <c>"HH:mm:ss[sep]frac"</c> (24-hour, with fractional)</item>
    /// <item>20/120 — ODBC canonical: <c>"yyyy-MM-dd HH:mm:ss"</c></item>
    /// <item>21/25/121 — ODBC canonical + ms: <c>"yyyy-MM-dd HH:mm:ss.fff"</c></item>
    /// <item>22 — <c>"MM/dd/yy h:mm:ss AM/PM"</c> (single space before AM/PM, no fractional)</item>
    /// <item>126/127 — ISO 8601 with T separator; 127 projects datetimeoffset to UTC with Z</item>
    /// <item>130/131 — Hijri (UmAlQura) date with Arabic month name (130) or numeric month (131)</item>
    /// </list>
    /// Fractional-second separator follows the source family: legacy
    /// <c>datetime</c> / <c>smalldatetime</c> use COLON in styles
    /// 9/13/14/109/113/114/130/131 (e.g. <c>"14:25:36:123"</c>) and PERIOD in
    /// 21/25/121/126/127; modern <c>datetime2</c> / <c>datetimeoffset</c> /
    /// <c>time</c> always use PERIOD with source-precision digits (precision
    /// 0 omits the fractional portion entirely). Time-only sources reject
    /// every date-bearing style with Msg 8114; date-only sources reject
    /// time-of-day-only styles 8/24/108 with Msg 8114 and the fractional
    /// time-only styles 14/114 with Msg 281 (probe-confirmed split).
    /// </remarks>
    internal SqlValue CoerceDateTimeToStringWithStyle(SqlType target, int style)
    {
        var formatted = this.Type switch
        {
            _ when this.Type == SqlType.Date => FormatDateSourceWithStyle(this.AsDate, style),
            _ when this.Type == SqlType.DateTime => FormatLegacyDateTimeSourceWithStyle(this.AsDateTime, style, "datetime"),
            _ when this.Type == SqlType.SmallDateTime => FormatLegacyDateTimeSourceWithStyle(this.AsSmallDateTime, style, "smalldatetime"),
            DateTime2SqlType dt2 => FormatDateTime2SourceWithStyle(this.AsDateTime2, dt2.precision, style),
            TimeSqlType t => FormatTimeSourceWithStyle(this.AsTime, t.precision, style),
            DateTimeOffsetSqlType dto => FormatDateTimeOffsetSourceWithStyle(this.AsDateTimeOffset, dto.precision, style),
            _ => throw new NotSupportedException($"CONVERT style codes aren't implemented for {this.Type}."),
        };
        return FromString(target, formatted);
    }

    /// <summary>
    /// Per-style formatter for a <c>date</c> source: only the date-bearing
    /// styles work directly. Time-only styles split between Msg 8114 (the
    /// "valid style but source can't satisfy" set 8/24/108) and Msg 281
    /// (the "this style is never valid for date" set 14/114). Probe-
    /// confirmed split against SQL Server 2025.
    /// </summary>
    private static string FormatDateSourceWithStyle(DateOnly date, int style) => style switch
    {
        0 or 100 or 9 or 109 => FormatLegacyDate(date),
        13 or 113 => $"{date.Day,2} {date:MMM yyyy}",
        20 or 21 or 23 or 25 or 120 or 121 or 126 or 127 => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        22 or 1 => date.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
        101 => date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        2 => date.ToString("yy.MM.dd", CultureInfo.InvariantCulture),
        102 => date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
        3 => date.ToString("dd/MM/yy", CultureInfo.InvariantCulture),
        103 => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        4 => date.ToString("dd.MM.yy", CultureInfo.InvariantCulture),
        104 => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        5 => date.ToString("dd-MM-yy", CultureInfo.InvariantCulture),
        105 => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        6 => $"{date.Day,2} {date:MMM yy}",
        106 => $"{date.Day,2} {date:MMM yyyy}",
        7 => date.ToString("MMM dd, yy", CultureInfo.InvariantCulture),
        107 => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture),
        10 => date.ToString("MM-dd-yy", CultureInfo.InvariantCulture),
        110 => date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
        11 => date.ToString("yy/MM/dd", CultureInfo.InvariantCulture),
        111 => date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
        12 => date.ToString("yyMMdd", CultureInfo.InvariantCulture),
        112 => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        130 => FormatHijriDateOnly(date.ToDateTime(TimeOnly.MinValue), withMonthName: true),
        131 => FormatHijriDateOnly(date.ToDateTime(TimeOnly.MinValue), withMonthName: false),
        8 or 24 or 108 => throw SimulatedSqlException.ConvertingDataTypeError(SqlType.Date, "varchar"),
        _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, "date"),
    };

    /// <summary>
    /// Per-style formatter for legacy <c>datetime</c> / <c>smalldatetime</c>
    /// sources. Fractional separator is always COLON in styles
    /// 9/13/14/109/113/114/130/131 and PERIOD in 21/25/121/126/127 (probe-
    /// confirmed). Date-only styles fall through to the shared date
    /// formatter and discard the time portion, matching real-server
    /// behavior. <paramref name="sourceTypeWord"/> threads the source
    /// family name into the Msg 281 wording on an unrecognized style.
    /// </summary>
    private static string FormatLegacyDateTimeSourceWithStyle(DateTime dt, int style, string sourceTypeWord)
    {
        var date = DateOnly.FromDateTime(dt);
        var time = TimeOnly.FromDateTime(dt);
        var frac = LegacyMilliseconds(dt);
        return style switch
        {
            0 or 100 => $"{FormatLegacyDate(date)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: false, ':', "", spaceBeforeAmPm: false)}",
            9 or 109 => $"{FormatLegacyDate(date)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, ':', frac, spaceBeforeAmPm: false)}",
            13 or 113 => $"{date.Day,2} {date:MMM yyyy} {Format24HourTime(time, ':', frac)}",
            20 or 120 => $"{date:yyyy-MM-dd} {Format24HourTime(time, '.', "")}",
            21 or 25 or 121 => $"{date:yyyy-MM-dd} {Format24HourTime(time, '.', frac)}",
            22 => $"{date:MM/dd/yy} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', "", spaceBeforeAmPm: true)}",
            126 or 127 => $"{date:yyyy-MM-dd}T{Format24HourTime(time, '.', frac)}",
            8 or 24 or 108 => Format24HourTime(time, '.', ""),
            14 or 114 => Format24HourTime(time, ':', frac),
            130 => $"{FormatHijriDateOnly(dt, withMonthName: true)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, ':', frac, spaceBeforeAmPm: false)}",
            131 => $"{FormatHijriDateOnly(dt, withMonthName: false)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, ':', frac, spaceBeforeAmPm: false)}",
            23 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 11 or 12 or 101 or 102 or 103 or 104 or 105 or 106 or 107 or 110 or 111 or 112 => FormatDateSourceWithStyle(date, style),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, sourceTypeWord),
        };
    }

    /// <summary>
    /// Per-style formatter for <c>datetime2(N)</c>. Fractional-second
    /// separator is always PERIOD; fractional width follows the source's
    /// declared precision (precision 0 omits the fractional portion).
    /// </summary>
    private static string FormatDateTime2SourceWithStyle(DateTime dt, int precision, int style)
    {
        var date = DateOnly.FromDateTime(dt);
        var time = TimeOnly.FromDateTime(dt);
        var frac = ModernFractional(dt, precision);
        return style switch
        {
            0 or 100 => $"{FormatLegacyDate(date)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: false, '.', "", spaceBeforeAmPm: false)}",
            9 or 109 => $"{FormatLegacyDate(date)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', frac, spaceBeforeAmPm: false)}",
            13 or 113 => $"{date.Day,2} {date:MMM yyyy} {Format24HourTime(time, '.', frac)}",
            20 or 120 => $"{date:yyyy-MM-dd} {Format24HourTime(time, '.', "")}",
            21 or 25 or 121 => $"{date:yyyy-MM-dd} {Format24HourTime(time, '.', frac)}",
            22 => $"{date:MM/dd/yy} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', "", spaceBeforeAmPm: true)}",
            126 or 127 => $"{date:yyyy-MM-dd}T{Format24HourTime(time, '.', frac)}",
            8 or 24 or 108 => Format24HourTime(time, '.', ""),
            14 or 114 => Format24HourTime(time, '.', frac),
            130 => $"{FormatHijriDateOnly(dt, withMonthName: true)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', frac, spaceBeforeAmPm: false)}",
            131 => $"{FormatHijriDateOnly(dt, withMonthName: false)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', frac, spaceBeforeAmPm: false)}",
            23 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 11 or 12 or 101 or 102 or 103 or 104 or 105 or 106 or 107 or 110 or 111 or 112 => FormatDateSourceWithStyle(date, style),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, "datetime2"),
        };
    }

    /// <summary>
    /// Per-style formatter for a <c>time(N)</c> source: only time-of-day-
    /// bearing styles are valid. Date-bearing styles all raise Msg 8114
    /// ("Error converting data type time to varchar") — note this is a
    /// uniform error path, unlike date sources which split between 8114
    /// and 281. Probe-confirmed against SQL Server 2025. Styles 0/9/100/109
    /// emit the hour WITHOUT leading-space padding (no date prefix to
    /// align against); styles 22/130/131 DO pad the hour (probe-confirmed
    /// quirk).
    /// </summary>
    private static string FormatTimeSourceWithStyle(TimeSpan ts, int precision, int style)
    {
        var time = TimeOnly.FromTimeSpan(ts);
        var frac = ModernFractional(DateTime.MinValue.Add(ts), precision);
        return style switch
        {
            0 or 100 => FormatAmPm12HourTime(time, paddedHour: false, includeSeconds: false, '.', "", spaceBeforeAmPm: false),
            9 or 109 => FormatAmPm12HourTime(time, paddedHour: false, includeSeconds: true, '.', frac, spaceBeforeAmPm: false),
            22 => " " + FormatAmPm12HourTime(time, paddedHour: false, includeSeconds: true, '.', "", spaceBeforeAmPm: true),
            130 or 131 => " " + FormatAmPm12HourTime(time, paddedHour: false, includeSeconds: true, '.', frac, spaceBeforeAmPm: false),
            8 or 24 or 108 or 20 or 120 => Format24HourTime(time, '.', ""),
            13 or 113 or 14 or 114 or 21 or 25 or 121 or 126 or 127 => Format24HourTime(time, '.', frac),
            // Date-bearing styles fail with Msg 8114 — source can't supply the date portion (probe-confirmed).
            1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 11 or 12 or 23 or 101 or 102 or 103 or 104 or 105 or 106 or 107 or 110 or 111 or 112 => throw SimulatedSqlException.ConvertingDataTypeError("time", "varchar"),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, "time"),
        };
    }

    /// <summary>
    /// Per-style formatter for <c>datetimeoffset(N)</c>. Like
    /// <c>datetime2(N)</c> but every time-of-day-bearing style appends
    /// <c>" ±HH:mm"</c> after the time portion (style 126 keeps the
    /// offset; style 127 projects to UTC and emits a trailing <c>Z</c>).
    /// </summary>
    private static string FormatDateTimeOffsetSourceWithStyle(DateTimeOffset dto, int precision, int style)
    {
        var date = DateOnly.FromDateTime(dto.DateTime);
        var time = TimeOnly.FromDateTime(dto.DateTime);
        var frac = ModernFractional(dto.DateTime, precision);
        var offset = dto.ToString("zzz", CultureInfo.InvariantCulture);
        return style switch
        {
            0 or 100 => $"{FormatLegacyDate(date)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: false, '.', "", spaceBeforeAmPm: false)} {offset}",
            9 or 109 => $"{FormatLegacyDate(date)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', frac, spaceBeforeAmPm: false)} {offset}",
            22 => $"{date:MM/dd/yy} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', "", spaceBeforeAmPm: true)} {offset}",
            130 => $"{FormatHijriDateOnly(dto.DateTime, withMonthName: true)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', frac, spaceBeforeAmPm: false)} {offset}",
            131 => $"{FormatHijriDateOnly(dto.DateTime, withMonthName: false)} {FormatAmPm12HourTime(time, paddedHour: true, includeSeconds: true, '.', frac, spaceBeforeAmPm: false)} {offset}",
            13 or 113 => $"{date.Day,2} {date:MMM yyyy} {Format24HourTime(time, '.', frac)} {offset}",
            14 or 114 => $"{Format24HourTime(time, '.', frac)} {offset}",
            8 or 24 or 108 => $"{Format24HourTime(time, '.', "")} {offset}",
            20 or 120 => $"{date:yyyy-MM-dd} {Format24HourTime(time, '.', "")} {offset}",
            21 or 25 or 121 => $"{date:yyyy-MM-dd} {Format24HourTime(time, '.', frac)} {offset}",
            126 => FormatIsoDateTimeOffset(dto, precision, withOffset: true),
            127 => FormatIsoDateTimeOffset(dto.ToUniversalTime(), precision, withOffset: false) + "Z",
            23 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 11 or 12 or 101 or 102 or 103 or 104 or 105 or 106 or 107 or 110 or 111 or 112 => FormatDateSourceWithStyle(date, style),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, "datetimeoffset"),
        };
    }

    private static string FormatIsoDateTimeOffset(DateTimeOffset dto, int precision, bool withOffset) =>
        dto.ToString(
            (precision == 0 ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-ddTHH:mm:ss." + new string('f', precision))
            + (withOffset ? "zzz" : ""),
            CultureInfo.InvariantCulture);

    /// <summary>
    /// Legacy <c>"Mmm d yyyy"</c> with the day right-aligned in 2 chars
    /// (single-digit days get a leading space).
    /// </summary>
    private static string FormatLegacyDate(DateOnly date) =>
        string.Format(CultureInfo.InvariantCulture, "{0:MMM} {1,2} {0:yyyy}", date.ToDateTime(TimeOnly.MinValue), date.Day);

    /// <summary>
    /// 12-hour time-of-day formatter for AM/PM styles. <paramref name="paddedHour"/>
    /// right-aligns the hour in 2 chars (single-digit gets a leading space) —
    /// always true when emitted alongside a date prefix, false on time-only
    /// sources for styles 0/9/100/109. <paramref name="spaceBeforeAmPm"/>
    /// adds a literal space before <c>AM</c>/<c>PM</c> (style 22 only).
    /// </summary>
    private static string FormatAmPm12HourTime(TimeOnly time, bool paddedHour, bool includeSeconds, char fracSep, string fractional, bool spaceBeforeAmPm)
    {
        var hour12 = ((time.Hour + 11) % 12) + 1;
        var ampm = time.Hour < 12 ? "AM" : "PM";
        var hourPart = paddedHour
            ? string.Format(CultureInfo.InvariantCulture, "{0,2}", hour12)
            : hour12.ToString(CultureInfo.InvariantCulture);
        var secondsPart = includeSeconds ? $":{time.Second:00}" : "";
        var fracPart = fractional.Length > 0 ? $"{fracSep}{fractional}" : "";
        var sep = spaceBeforeAmPm ? " " : "";
        return $"{hourPart}:{time.Minute:00}{secondsPart}{fracPart}{sep}{ampm}";
    }

    /// <summary>
    /// 24-hour <c>HH:mm:ss</c> with optional fractional suffix. Empty
    /// <paramref name="fractional"/> omits the separator entirely.
    /// </summary>
    private static string Format24HourTime(TimeOnly time, char fracSep, string fractional) =>
        fractional.Length > 0
            ? $"{time:HH:mm:ss}{fracSep}{fractional}"
            : time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Legacy <c>datetime</c>/<c>smalldatetime</c> always carries 3-digit
    /// milliseconds in style 9/13/14/109/113/114/21/25/121/130/131; the
    /// helper centralizes the <c>"fff"</c> rendering.
    /// </summary>
    private static string LegacyMilliseconds(DateTime dt) =>
        dt.Millisecond.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Modern <c>datetime2</c> / <c>datetimeoffset</c> / <c>time</c> emit
    /// source-precision fractional digits (no rounding, no trim). Precision
    /// 0 returns the empty string so the caller can skip the separator.
    /// </summary>
    private static string ModernFractional(DateTime dt, int precision) =>
        precision == 0 ? "" : dt.ToString(new string('f', precision), CultureInfo.InvariantCulture);

    // SQL Server's CONVERT styles 130/131 use the tabular (Kuwaiti)
    // algorithm, which corresponds to .NET's HijriCalendar with the
    // default HijriAdjustment = 0. UmAlQuraCalendar (the modern Saudi
    // tabular variant) consistently differs by ±1 day for some months
    // because the underlying month-start tables don't agree — verified
    // by probe against SQL Server 2025 (2026-05-19).
    private static readonly HijriCalendar HijriCal = new();

    /// <summary>
    /// Hijri date emit for CONVERT style 130/131. Day is space-padded to
    /// 2 chars in both styles. Style 130 emits the Arabic month name (e.g.
    /// <c>"ذو القعدة"</c>); style 131 emits the zero-padded month number
    /// with <c>/</c> separators. Probe-confirmed against SQL Server 2025
    /// (2026-05-19).
    /// </summary>
    private static string FormatHijriDateOnly(DateTime dt, bool withMonthName)
    {
        var y = HijriCal.GetYear(dt);
        var m = HijriCal.GetMonth(dt);
        var d = HijriCal.GetDayOfMonth(dt);
        return withMonthName
            ? string.Format(CultureInfo.InvariantCulture, "{0,2} {1} {2}", d, HijriMonthName(m), y)
            : string.Format(CultureInfo.InvariantCulture, "{0,2}/{1:00}/{2}", d, m, y);
    }

    private static string HijriMonthName(int month) => month switch
    {
        1 => "محرم",
        2 => "صفر",
        3 => "ربيع الاول",
        4 => "ربيع الثاني",
        5 => "جمادى الاولى",
        6 => "جمادى الثانية",
        7 => "رجب",
        8 => "شعبان",
        9 => "رمضان",
        10 => "شوال",
        11 => "ذو القعدة",
        12 => "ذو الحجة",
        _ => throw new ArgumentOutOfRangeException(nameof(month), month, "Hijri month must be 1-12."),
    };

    /// <summary>
    /// CONVERT-style string formatting for a <c>float</c>/<c>real</c>
    /// source. Style 0 emits a compact form (6 significant digits, decimal
    /// or 3-digit scientific) — values in <c>[1e-4, 1e6)</c> render
    /// fixed-point with trailing-zero trim, the rest scientific. Styles
    /// 1/2/3/126 always render scientific with 3-digit exponent: 1 → 8 sig
    /// digits, 2 → 16 sig digits (real source promotes to float precision),
    /// 3 → 17 sig digits, 126 → source-precision (16 for float, 8 for
    /// real). Probe-confirmed against SQL Server 2025 (2026-05-19); unknown
    /// styles raise Msg 281 with the source family name.
    /// </summary>
    internal SqlValue CoerceFloatToStringWithStyle(SqlType target, int style)
    {
        var isReal = this.Type == SqlType.Real;
        var value = isReal ? (double)this.AsSingle : this.AsDouble;
        var formatted = style switch
        {
            0 => FormatFloatStyle0(value),
            1 => FormatFloatScientific(value, totalSignificantDigits: 8),
            2 => FormatFloatScientific(value, totalSignificantDigits: 16),
            3 => FormatFloatScientific(value, totalSignificantDigits: 17),
            126 => FormatFloatScientific(value, totalSignificantDigits: isReal ? 8 : 16),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, isReal ? "real" : "float"),
        };
        return FromString(target, formatted);
    }

    /// <summary>
    /// CONVERT style 0 for <c>float</c>: 6 significant digits, fixed-point
    /// when the rounded magnitude is in <c>[1e-4, 1e6)</c>, else scientific
    /// with <c>e±NNN</c> 3-digit exponent. Trailing zeros stripped in both
    /// forms. Negative-zero sign preserved.
    /// </summary>
    private static string FormatFloatStyle0(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value.ToString("R", CultureInfo.InvariantCulture);
        if (value == 0.0)
            return double.IsNegative(value) ? "-0" : "0";

        var rounded = RoundToSignificantDigits(value, 6);
        var absRounded = Math.Abs(rounded);
        if (absRounded is >= 0.0001 and < 1_000_000.0)
        {
            var s = rounded.ToString("G6", CultureInfo.InvariantCulture);
            if (!s.Contains('E', StringComparison.Ordinal))
                return s;
        }
        return FormatFloatScientific(rounded, totalSignificantDigits: 6, trimTrailingZeros: true);
    }

    /// <summary>
    /// Scientific form <c>[-]d.ddd…e±NNN</c> with a 3-digit exponent and
    /// lowercase <c>e</c>. <paramref name="totalSignificantDigits"/> counts
    /// the digits before AND after the decimal point (so 8 = one leading
    /// + seven fractional). When <paramref name="trimTrailingZeros"/> is
    /// true, fractional trailing zeros are removed (and the decimal point
    /// drops with them) — used only by style 0's scientific fallback.
    /// </summary>
    private static string FormatFloatScientific(double value, int totalSignificantDigits, bool trimTrailingZeros = false)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value.ToString("R", CultureInfo.InvariantCulture);

        var fractionalDigits = Math.Max(0, totalSignificantDigits - 1);
        var raw = value.ToString("E" + fractionalDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var eIndex = raw.IndexOf('E', StringComparison.Ordinal);
        var mantissa = raw[..eIndex];
        var exponent = raw[(eIndex + 1)..];
        if (trimTrailingZeros && mantissa.Contains('.', StringComparison.Ordinal))
        {
            mantissa = mantissa.TrimEnd('0');
            if (mantissa.EndsWith('.'))
                mantissa = mantissa[..^1];
        }
        var sign = exponent[0];
        var magnitude = exponent[1..].TrimStart('0');
        if (magnitude.Length == 0)
            magnitude = "0";
        var paddedExponent = magnitude.PadLeft(3, '0');
        return $"{mantissa}e{sign}{paddedExponent}";
    }

    /// <summary>
    /// Rounds <paramref name="value"/> to <paramref name="digits"/>
    /// significant digits using round-half-away-from-zero, matching SQL
    /// Server's float formatting rounding.
    /// </summary>
    private static double RoundToSignificantDigits(double value, int digits)
    {
        if (value == 0.0)
            return value;
        var magnitude = Math.Pow(10, digits - Math.Ceiling(Math.Log10(Math.Abs(value))));
        return Math.Round(value * magnitude, MidpointRounding.AwayFromZero) / magnitude;
    }

    /// <summary>
    /// CONVERT-style string formatting for a <c>varbinary</c>/<c>binary</c>/<c>image</c>
    /// source. Style 0 reinterprets bytes character-by-character through the
    /// target's encoding (the collation's ANSI code page for <c>varchar</c>,
    /// UTF-16 LE for <c>nvarchar</c>). Style 1 emits <c>"0xHHHH…"</c> with uppercase hex
    /// digits; style 2 emits bare <c>"HHHH…"</c>. Any other style raises
    /// Msg 281 with <c>"varbinary"</c> as the source family name.
    /// </summary>
    internal SqlValue CoerceBinaryToStringWithStyle(SqlType target, int style)
    {
        var bytes = this.AsBytes;
        var formatted = style switch
        {
            0 => target is NVarcharSqlType or NCharSqlType or SystemNameSqlType
                ? Encoding.Unicode.GetString(bytes)
                : (target.Collation ?? Collation.Baseline).StorageEncoding.GetString(bytes),
            1 => "0x" + Convert.ToHexString(bytes),
            2 => Convert.ToHexString(bytes),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, "varbinary"),
        };
        return FromString(target, formatted);
    }

    /// <summary>
    /// CONVERT-style hex / character → <c>varbinary</c>/<c>binary</c>
    /// coercion. Style 0 copies the input's bytes verbatim (the source
    /// collation's ANSI code page for varchar-family, UTF-16 LE for
    /// nvarchar-family). Style 1 parses
    /// <c>"0xHHHH…"</c> with the prefix required (missing prefix → Msg
    /// 8114). Style 2 parses bare <c>"HHHH…"</c> with the prefix
    /// explicitly disallowed (presence → Msg 8114). Both hex paths require
    /// an even number of hex digits and reject any non-hex character.
    /// Truncation to the target's declared length happens in the
    /// CAST-level path after this method returns.
    /// </summary>
    internal SqlValue CoerceStringToBinaryWithStyle(SqlType target, int style)
    {
        var s = this.AsString;
        var sourceIsUnicode = this.Type is NVarcharSqlType or NCharSqlType or SystemNameSqlType;
        var bytes = style switch
        {
            0 => sourceIsUnicode ? Encoding.Unicode.GetBytes(s) : (this.Type.Collation ?? Collation.Baseline).StorageEncoding.GetBytes(s),
            1 => ParseHexWithPrefix(s, requirePrefix: true),
            2 => ParseHexWithPrefix(s, requirePrefix: false),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, sourceIsUnicode ? "nvarchar" : "varchar"),
        };
        return target is BinarySqlType bin
            ? FromBinaryPadded(bin, bytes)
            : FromVarbinary(bytes);
    }

    /// <summary>
    /// Parses a hex string into a byte array for CONVERT styles 1 / 2.
    /// Style 1 requires a leading <c>"0x"</c>; style 2 requires its
    /// absence. Both demand an even number of hex digits and reject any
    /// non-hex character — both failure paths raise Msg 8114, matching
    /// real SQL Server.
    /// </summary>
    private static byte[] ParseHexWithPrefix(string s, bool requirePrefix)
    {
        var hasPrefix = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (requirePrefix != hasPrefix)
            throw SimulatedSqlException.ConvertingDataTypeError(VarcharSqlType.Get(0, Collation.Baseline, Coercibility.CoercibleDefault), "varbinary");
        var hex = hasPrefix ? s[2..] : s;
        if (hex.Length % 2 != 0)
            throw SimulatedSqlException.ConvertingDataTypeError(VarcharSqlType.Get(0, Collation.Baseline, Coercibility.CoercibleDefault), "varbinary");
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw SimulatedSqlException.ConvertingDataTypeError(VarcharSqlType.Get(0, Collation.Baseline, Coercibility.CoercibleDefault), "varbinary");
        }
    }

    /// <summary>
    /// Coerces a freshly-decoded byte sequence into a fixed-length
    /// <c>binary(N)</c> target: right-pads with zero bytes when the source
    /// is shorter than <c>N</c>; truncation past <c>N</c> is left to the
    /// CAST-level length-enforcement path.
    /// </summary>
    private static SqlValue FromBinaryPadded(BinarySqlType target, byte[] bytes)
    {
        if (bytes.Length >= target.length)
            return FromVarbinary(bytes);
        var padded = new byte[target.length];
        Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
        return FromVarbinary(padded);
    }
}
