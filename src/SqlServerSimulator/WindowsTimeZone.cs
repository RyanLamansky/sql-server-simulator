using System.Collections.Frozen;
using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// One of the Windows time zones <c>sys.time_zone_info</c> lists, carrying
/// the rules real SQL Server converts with: Windows' own per-year rule set,
/// which reaches back unchanged before its first year and forward past its
/// last, never the host's IANA history. <c>'1800-07-01 12:00' AT TIME ZONE
/// 'Pacific Standard Time'</c> is <c>-07:00</c> because the zone's first
/// rule (first Sunday of April to last Sunday of October) applies in 1800.
/// </summary>
/// <remarks>
/// <para>The rules were reconstructed black-box from SQL Server 2025's own
/// answers (probed 2026-10-16): every offset change of every zone between
/// 1900 and 2104 found to the millisecond, local wall-clock times on both
/// sides of each change, and samples across years 1 to 9999 in both
/// directions. The evaluation below is the algorithm those answers fix,
/// including its year-boundary artefacts — a southern zone whose summer ends
/// on January 1 stays on standard time for the hour after the year turns in
/// daylight time, and several zones blip for an hour at the turn of the year
/// their rules changed — so it is not simplified to the instants a rule
/// names.</para>
/// <para>Each rule gives the standard offset, the daylight delta (negative
/// where Windows encodes a mid-year change of standard offset as daylight
/// time ending), and the two transition dates as Windows writes them: the
/// nth (or last) weekday of a month at a time of day, the start date in
/// standard time and the end date in daylight time.</para>
/// </remarks>
internal sealed class WindowsTimeZone
{
    /// <summary>The Windows id, as <c>sys.time_zone_info</c> spells it.</summary>
    public readonly string Name;

    // Ordered by first year; the first rule also covers every earlier year.
    private readonly Rule[] rules;

    private WindowsTimeZone(string name, Rule[] rules)
    {
        this.Name = name;
        this.rules = rules;
    }

    /// <summary>Every zone, in the order <c>sys.time_zone_info</c> returns them.</summary>
    internal static readonly WindowsTimeZone[] All = Load();

    private static readonly FrozenDictionary<string, WindowsTimeZone> ByName =
        All.ToFrozenDictionary(zone => zone.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The zone <c>AT TIME ZONE</c> names: a Windows id matched without regard
    /// to case and with no surrounding spaces, or <see langword="null"/>. An
    /// IANA name such as <c>America/New_York</c> is refused as real refuses it
    /// (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    public static WindowsTimeZone? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The zone's offset from UTC, in minutes, at a UTC instant given in ticks.</summary>
    public int OffsetMinutesAt(long utcTicks) => this.Evaluate(utcTicks, out _);

    /// <summary>Whether the zone is in daylight time at a UTC instant, as <c>sys.time_zone_info.is_currently_dst</c> reports it.</summary>
    public bool IsDaylightAt(long utcTicks)
    {
        _ = this.Evaluate(utcTicks, out var daylight);
        return daylight;
    }

    /// <summary>
    /// The UTC instant a wall-clock time in this zone names. A time a
    /// transition skips reads as the offset in force before it, so it lands
    /// past the gap once re-expressed; a time a transition repeats reads as
    /// the earlier instant — except at a transition a negative delta writes,
    /// where a skipped time reads with the new offset and lands before the
    /// gap (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public long LocalToUtc(long localTicks)
    {
        var year = YearOf(localTicks);
        var rule = this.RuleFor(year);
        if (rule.Delta == 0)
            return localTicks - (rule.Bias * TimeSpan.TicksPerMinute);
        var daylight = InDaylight(localTicks, year, rule, resolvingLocal: true);
        return localTicks - ((rule.Bias + (daylight ? rule.Delta : 0)) * TimeSpan.TicksPerMinute);
    }

    private int Evaluate(long utcTicks, out bool reportedDaylight)
    {
        var year = this.RuleYear(utcTicks);
        var rule = this.RuleFor(year);
        reportedDaylight = false;
        if (rule.Delta == 0)
            return rule.Bias;
        if (!InDaylight(utcTicks + ((rule.Bias + rule.Delta) * TimeSpan.TicksPerMinute), year, rule, resolvingLocal: false))
            return rule.Bias;
        // A whole-year rule with a negative delta is how a later standard
        // offset carries its predecessor's year boundary; real reports it
        // as standard time.
        reportedDaylight = !(rule.AllYear && rule.Delta < 0);
        return rule.Bias + rule.Delta;
    }

    /// <summary>
    /// The year whose rule a UTC instant reads: the next year once the
    /// instant reaches that year in standard time under either year's
    /// offset, otherwise the year the instant falls in under its own UTC
    /// year's standard offset.
    /// </summary>
    private int RuleYear(long utcTicks)
    {
        var year = YearOf(utcTicks);
        var bias = this.RuleFor(year).Bias;
        var nextBias = this.RuleFor(year + 1).Bias;
        if (utcTicks + (Math.Max(bias, nextBias) * TimeSpan.TicksPerMinute) >= DaysFromCivil(year + 1, 1, 1) * TimeSpan.TicksPerDay)
            return year + 1;
        return YearOf(utcTicks + (bias * TimeSpan.TicksPerMinute));
    }

    private Rule RuleFor(int year)
    {
        var rules = this.rules;
        var i = rules.Length - 1;
        while (i > 0 && rules[i].FromYear > year)
            i--;
        return rules[i];
    }

    /// <summary>
    /// Whether a daylight-time wall clock falls in the rule's daylight period
    /// for the given year. The comparison reads month, day and time of day
    /// only, never the year, which is what makes the hour after midnight on
    /// January 1 compare as early January even while the rule year is still
    /// the old one.
    /// </summary>
    private static bool InDaylight(long daylightTicks, int year, Rule rule, bool resolvingLocal)
    {
        if (rule.AllYear)
            return true;
        // A wall clock being resolved moves to daylight only past the later
        // of the start's two readings, so a repeated hour reads as standard.
        var startDelta = resolvingLocal ? Math.Max(rule.Delta, 0) : rule.Delta;
        var start = MonthDayKey(rule.Start.TicksIn(year) + (startDelta * TimeSpan.TicksPerMinute));
        var end = MonthDayKey(rule.End.TicksIn(year));
        var key = MonthDayKey(daylightTicks);
        return start < end ? start <= key && key < end : key >= start || key < end;
    }

    private static long MonthDayKey(long ticks)
    {
        var days = FloorDivide(ticks, TimeSpan.TicksPerDay);
        CivilFromDays(days, out _, out var month, out var day);
        return (((month * 32) + day) * TimeSpan.TicksPerDay) + (ticks - (days * TimeSpan.TicksPerDay));
    }

    private static int YearOf(long ticks)
    {
        CivilFromDays(FloorDivide(ticks, TimeSpan.TicksPerDay), out var year, out _, out _);
        return year;
    }

    private static long FloorDivide(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    // Proleptic Gregorian day numbers counted from 0001-01-01, valid a year
    // either side of the datetime range, which a local reading of an instant
    // near either end reaches.
    private static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - (era * 400);
        var dayOfYear = (((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5) + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146097) + dayOfEra - 306;
    }

    private static void CivilFromDays(long days, out int year, out int month, out int day)
    {
        var z = days + 306;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var dayOfEra = z - (era * 146097);
        var yearOfEra = (dayOfEra - (dayOfEra / 1460) + (dayOfEra / 36524) - (dayOfEra / 146096)) / 365;
        var dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
        var mp = ((5 * dayOfYear) + 2) / 153;
        day = (int)(dayOfYear - (((153 * mp) + 2) / 5) + 1);
        month = (int)(mp < 10 ? mp + 3 : mp - 9);
        year = (int)(yearOfEra + (era * 400) + (month <= 2 ? 1 : 0));
    }

    private static int DaysInMonth(int year, int month) => month switch
    {
        2 => (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>
    /// Reads the embedded rule table, one zone per line in catalog order: the
    /// id, a tab, then space-separated rules, each but the first prefixed by
    /// its first year and a colon. A rule is the standard offset in minutes
    /// east of UTC alone, or followed by the daylight delta and either
    /// <c>A</c> (daylight all year) or the start and end dates as
    /// <c>month.week.weekday.milliseconds</c>.
    /// </summary>
    private static WindowsTimeZone[] Load()
    {
        using var stream = typeof(WindowsTimeZone).Assembly.GetManifestResourceStream("SqlServerSimulator.WindowsTimeZones.tsv")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.WindowsTimeZones.tsv is missing.");
        using var reader = new StreamReader(stream);
        var zones = new List<WindowsTimeZone>();
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
                continue;
            var parts = line[(tab + 1)..].Split(' ');
            var rules = new Rule[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var text = parts[i];
                var fromYear = int.MinValue;
                var colon = text.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0)
                {
                    fromYear = int.Parse(text.AsSpan(0, colon), CultureInfo.InvariantCulture);
                    text = text[(colon + 1)..];
                }
                var fields = text.Split(',');
                var bias = int.Parse(fields[0], CultureInfo.InvariantCulture);
                rules[i] = fields.Length switch
                {
                    1 => new Rule(fromYear, bias, 0, false, default, default),
                    3 => new Rule(fromYear, bias, int.Parse(fields[1], CultureInfo.InvariantCulture), true, default, default),
                    _ => new Rule(fromYear, bias, int.Parse(fields[1], CultureInfo.InvariantCulture), false, TransitionDate.Parse(fields[2]), TransitionDate.Parse(fields[3])),
                };
            }
            zones.Add(new WindowsTimeZone(line[..tab], rules));
        }
        return [.. zones];
    }

    /// <summary>One year range's rule; <see cref="Delta"/> zero means no daylight time.</summary>
    private readonly struct Rule(int fromYear, int bias, int delta, bool allYear, TransitionDate start, TransitionDate end)
    {
        public readonly int FromYear = fromYear;
        public readonly int Bias = bias;
        public readonly int Delta = delta;
        public readonly bool AllYear = allYear;
        public readonly TransitionDate Start = start;
        public readonly TransitionDate End = end;
    }

    /// <summary>
    /// A transition date as Windows writes it: <see cref="Week"/> 1 to 4 is
    /// the nth <see cref="DayOfWeek"/> (Sunday 0) of the month and 5 the
    /// last; 0 is the month's first day and 6 its last, whatever the weekday.
    /// </summary>
    private readonly struct TransitionDate(int month, int week, int dayOfWeek, int milliseconds)
    {
        public readonly int Month = month;
        public readonly int Week = week;
        public readonly int DayOfWeek = dayOfWeek;
        public readonly int Milliseconds = milliseconds;

        public static TransitionDate Parse(string text)
        {
            var fields = text.Split('.');
            return new(
                int.Parse(fields[0], CultureInfo.InvariantCulture),
                int.Parse(fields[1], CultureInfo.InvariantCulture),
                int.Parse(fields[2], CultureInfo.InvariantCulture),
                int.Parse(fields[3], CultureInfo.InvariantCulture));
        }

        public long TicksIn(int year)
        {
            var first = DaysFromCivil(year, this.Month, 1);
            var length = DaysInMonth(year, this.Month);
            int day;
            switch (this.Week)
            {
                case 0:
                    day = 1;
                    break;
                case 6:
                    day = length;
                    break;
                default:
                    // 0001-01-01 is a Monday; year 0 counts back past it.
                    var firstWeekday = (int)((((first + 1) % 7) + 7) % 7);
                    day = 1 + ((this.DayOfWeek - firstWeekday + 7) % 7) + ((this.Week - 1) * 7);
                    while (day > length)
                        day -= 7;
                    break;
            }
            return ((first + day - 1) * TimeSpan.TicksPerDay) + (this.Milliseconds * TimeSpan.TicksPerMillisecond);
        }
    }
}
