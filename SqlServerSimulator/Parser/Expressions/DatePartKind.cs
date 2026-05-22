using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Canonical date-part identities accepted by <c>DATEPART</c> / <c>DATEADD</c>
/// (and, eventually, <c>DATEDIFF</c>). The enum collapses SQL Server's many
/// keyword aliases (<c>yy</c>/<c>yyyy</c>/<c>year</c>) to one value per
/// behavior; the per-keyword string is preserved separately by the function
/// node for diagnostic rendering.
/// </summary>
internal enum DatePartKind
{
    Year,
    Quarter,
    Month,
    DayOfYear,
    Day,
    Week,
    IsoWeek,
    Weekday,
    Hour,
    Minute,
    Second,
    Millisecond,
    Microsecond,
    Nanosecond,
    TzOffset,
}

internal static class DatePartKinds
{
    /// <summary>
    /// Maps a SQL Server datepart keyword (canonical or alias) to its
    /// <see cref="DatePartKind"/>. Throws Msg 155 for an unknown keyword,
    /// embedding <paramref name="functionLowerName"/> in the message
    /// ("... is not a recognized datepart/dateadd/datediff/datediff_big
    /// option.") to match SQL Server's per-caller wording.
    /// </summary>
    public static DatePartKind ResolveOrThrow(string keyword, string functionLowerName) =>
        Resolve(keyword) ?? throw SimulatedSqlException.NotARecognizedDatepartOption(keyword, functionLowerName);

    /// <summary>
    /// Span-based keyword dispatch — matches the pattern used in
    /// <c>Parser/Expression.cs:ResolveBuiltIn</c> and
    /// <c>Storage/SqlType.cs:GetByName</c> so the parser stays
    /// allocation-free in its keyword-resolution hot paths.
    /// </summary>
    private static DatePartKind? Resolve(string keyword)
    {
        Span<char> upper = stackalloc char[keyword.Length];
        return keyword.AsSpan().ToUpperInvariant(upper) switch
        {
            1 => upper switch
            {
                "Q" => DatePartKind.Quarter,
                "M" => DatePartKind.Month,
                "Y" => DatePartKind.DayOfYear,
                "D" => DatePartKind.Day,
                "N" => DatePartKind.Minute,
                "S" => DatePartKind.Second,
                _ => null,
            },
            2 => upper switch
            {
                "YY" => DatePartKind.Year,
                "QQ" => DatePartKind.Quarter,
                "MM" => DatePartKind.Month,
                "DY" => DatePartKind.DayOfYear,
                "DD" => DatePartKind.Day,
                "WK" or "WW" => DatePartKind.Week,
                "DW" => DatePartKind.Weekday,
                "HH" => DatePartKind.Hour,
                "MI" => DatePartKind.Minute,
                "SS" => DatePartKind.Second,
                "MS" => DatePartKind.Millisecond,
                "NS" => DatePartKind.Nanosecond,
                "TZ" => DatePartKind.TzOffset,
                _ => null,
            },
            3 => upper switch
            {
                "DAY" => DatePartKind.Day,
                "MCS" => DatePartKind.Microsecond,
                _ => null,
            },
            4 => upper switch
            {
                "YEAR" => DatePartKind.Year,
                "YYYY" => DatePartKind.Year,
                "WEEK" => DatePartKind.Week,
                "HOUR" => DatePartKind.Hour,
                _ => null,
            },
            5 => upper switch
            {
                "MONTH" => DatePartKind.Month,
                "ISOWK" or "ISOWW" => DatePartKind.IsoWeek,
                _ => null,
            },
            6 => upper switch
            {
                "MINUTE" => DatePartKind.Minute,
                "SECOND" => DatePartKind.Second,
                _ => null,
            },
            7 => upper switch
            {
                "QUARTER" => DatePartKind.Quarter,
                "WEEKDAY" => DatePartKind.Weekday,
                _ => null,
            },
            8 => upper switch
            {
                "ISO_WEEK" => DatePartKind.IsoWeek,
                "TZOFFSET" => DatePartKind.TzOffset,
                _ => null,
            },
            9 => upper switch
            {
                "DAYOFYEAR" => DatePartKind.DayOfYear,
                _ => null,
            },
            10 => upper switch
            {
                "NANOSECOND" => DatePartKind.Nanosecond,
                _ => null,
            },
            11 => upper switch
            {
                "MILLISECOND" => DatePartKind.Millisecond,
                "MICROSECOND" => DatePartKind.Microsecond,
                _ => null,
            },
            _ => null,
        };
    }

    private static bool IsTimePart(DatePartKind k) => k is DatePartKind.Hour
        or DatePartKind.Minute or DatePartKind.Second or DatePartKind.Millisecond
        or DatePartKind.Microsecond or DatePartKind.Nanosecond;

    private static bool IsDatePart(DatePartKind k) => k is DatePartKind.Year
        or DatePartKind.Quarter or DatePartKind.Month or DatePartKind.DayOfYear
        or DatePartKind.Day or DatePartKind.Week or DatePartKind.IsoWeek
        or DatePartKind.Weekday;

    private static bool IsTzPart(DatePartKind k) => k == DatePartKind.TzOffset;

    /// <summary>
    /// Applies SQL Server's implicit-cast rule for the date argument of
    /// DATEPART / DATEADD / DATEDIFF: string operands parse as
    /// <c>datetime2(7)</c>; integer operands parse as legacy <c>datetime</c>
    /// (days-since-1900-01-01). Both behaviors probe-confirmed against
    /// SQL Server 2025 (2026-05-22): <c>DATEPART(year, '2024-01-15')</c>,
    /// <c>DATEPART(year, 0)</c> → 1900, <c>DATEADD(day, 1, 0)</c> →
    /// <c>1900-01-02</c>. Non-date / non-string / non-integer operands
    /// pass through unchanged so the downstream
    /// <see cref="RequireCompatible"/> check raises the same Msg 9810 the
    /// real server would.
    /// </summary>
    public static SqlValue CoerceDateArgumentImplicit(SqlValue value) =>
        SqlType.IsStringCategory(value.Type) ? value.CoerceTo(SqlType.GetDateTime2(7))
        : SqlType.IsIntegerCategory(value.Type) ? value.CoerceTo(SqlType.DateTime)
        : value;

    /// <summary>
    /// Parallel of <see cref="CoerceDateArgumentImplicit"/> for the static
    /// projection path: maps string types to <c>datetime2(7)</c> and
    /// integer types to legacy <c>datetime</c>; everything else passes
    /// through. Used so <c>DATEADD</c>'s schema matches the runtime type
    /// for the implicit-cast cases (a string-typed source projects as
    /// datetime2 in real SQL Server, not the input's varchar).
    /// </summary>
    public static SqlType ResolveImplicitDateType(SqlType source) =>
        SqlType.IsStringCategory(source) ? SqlType.GetDateTime2(7)
        : SqlType.IsIntegerCategory(source) ? SqlType.DateTime
        : source;

    /// <summary>
    /// Enforces SQL Server's per-type compatibility rules and raises Msg 9810
    /// for the disallowed combinations. The rules:
    /// <list type="bullet">
    /// <item><description><c>date</c>: date parts only.</description></item>
    /// <item><description><c>time(N)</c>: time parts only.</description></item>
    /// <item><description><c>datetime</c> / <c>smalldatetime</c> / <c>datetime2(N)</c>: date and time parts.</description></item>
    /// <item><description><c>datetimeoffset(N)</c>: date, time, and tzoffset.</description></item>
    /// </list>
    /// </summary>
    public static void RequireCompatible(DatePartKind kind, string keywordText, SqlType type, string functionLowerName)
    {
        var ok = type switch
        {
            _ when type == SqlType.Date => IsDatePart(kind),
            TimeSqlType => IsTimePart(kind),
            _ when type == SqlType.DateTime => IsDatePart(kind) || IsTimePart(kind),
            _ when type == SqlType.SmallDateTime => IsDatePart(kind) || IsTimePart(kind),
            DateTime2SqlType => IsDatePart(kind) || IsTimePart(kind),
            DateTimeOffsetSqlType => IsDatePart(kind) || IsTimePart(kind) || IsTzPart(kind),
            _ => throw new NotSupportedException($"DATEPART/DATEADD doesn't accept operand type {type}."),
        };
        if (!ok)
            throw SimulatedSqlException.DatepartNotSupportedForType(keywordText, functionLowerName, FamilyRootName(type));
    }

    /// <summary>
    /// Enforces the function-level subset rule for <c>DATEDIFF</c> /
    /// <c>DATEDIFF_BIG</c>: probe-confirmed against SQL Server 2025
    /// (2026-05-08), <c>tzoffset</c> and <c>iso_week</c> are rejected with
    /// Msg 9806 regardless of operand type — <i>any</i> other datepart is
    /// accepted for any combination of date/time-family operands. (The
    /// per-type filter that DATEPART/DATEADD enforce via
    /// <see cref="RequireCompatible"/> doesn't apply.)
    /// </summary>
    public static void RequireCompatibleForDiff(DatePartKind kind, string keywordText, string functionLowerName)
    {
        if (kind is DatePartKind.TzOffset or DatePartKind.IsoWeek)
            throw SimulatedSqlException.DatepartNotSupportedForFunction(keywordText, functionLowerName);
    }

    /// <summary>
    /// Returns the count of <paramref name="kind"/> boundaries crossed
    /// going from <paramref name="start"/> to <paramref name="end"/> — the
    /// SQL Server <c>DATEDIFF</c> / <c>DATEDIFF_BIG</c> semantic. Both inputs
    /// must be non-NULL date/time-family values; mixed types are anchored
    /// (<c>date</c> at midnight, <c>time</c> on 1900-01-01,
    /// <c>datetimeoffset</c> via UTC instant).
    /// </summary>
    public static long Diff(DatePartKind kind, SqlValue start, SqlValue end)
    {
        var (startTicks, startYear, startMonth) = ToDiffAnchor(start);
        var (endTicks, endYear, endMonth) = ToDiffAnchor(end);
        return kind switch
        {
            DatePartKind.Year => endYear - startYear,
            DatePartKind.Quarter => QuarterIndex(endYear, endMonth) - QuarterIndex(startYear, startMonth),
            DatePartKind.Month => MonthIndex(endYear, endMonth) - MonthIndex(startYear, startMonth),
            DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => DayIndex(endTicks) - DayIndex(startTicks),
            DatePartKind.Week => SundayWeekIndex(endTicks) - SundayWeekIndex(startTicks),
            DatePartKind.Hour => (endTicks / TimeSpan.TicksPerHour) - (startTicks / TimeSpan.TicksPerHour),
            DatePartKind.Minute => (endTicks / TimeSpan.TicksPerMinute) - (startTicks / TimeSpan.TicksPerMinute),
            DatePartKind.Second => (endTicks / TimeSpan.TicksPerSecond) - (startTicks / TimeSpan.TicksPerSecond),
            DatePartKind.Millisecond => (endTicks / TimeSpan.TicksPerMillisecond) - (startTicks / TimeSpan.TicksPerMillisecond),
            DatePartKind.Microsecond => (endTicks / 10) - (startTicks / 10),
            DatePartKind.Nanosecond => checked((endTicks - startTicks) * 100),
            _ => throw new NotSupportedException($"DATEDIFF({kind}) isn't implemented."),
        };
    }

    private static long MonthIndex(int year, int month) => ((long)year * 12) + (month - 1);

    private static long QuarterIndex(int year, int month) => ((long)year * 4) + ((month - 1) / 3);

    private static long DayIndex(long ticks) => ticks / TimeSpan.TicksPerDay;

    /// <summary>
    /// Sunday-anchored week-bucket index for a tick value. <c>DateTime</c>'s
    /// epoch (0001-01-01) was a Monday, so the running day index is offset by
    /// +1 before dividing by 7 to align bucket boundaries on Sundays.
    /// Probe-confirmed: Sat→Sun crosses, Sun→Sat doesn't.
    /// </summary>
    private static long SundayWeekIndex(long ticks) => (DayIndex(ticks) + 1) / 7;

    /// <summary>
    /// Reduces a date/time-family value to (ticks-since-DateTime.MinValue,
    /// year, month) — the inputs the per-part bucket-index subtraction in
    /// <see cref="Diff"/> needs. Anchoring rules verified against SQL Server
    /// 2025 (2026-05-08): bare <c>date</c> → midnight; bare <c>time</c> →
    /// 1900-01-01; <c>datetimeoffset</c> → UTC instant (year/month read off
    /// the UTC clock too).
    /// </summary>
    private static (long ticks, int year, int month) ToDiffAnchor(SqlValue value)
    {
        if (value.Type == SqlType.Date)
        {
            var d = value.AsDate;
            return (d.ToDateTime(TimeOnly.MinValue).Ticks, d.Year, d.Month);
        }
        if (value.Type is TimeSqlType)
        {
            var anchor = new DateTime(1900, 1, 1).Add(value.AsTime);
            return (anchor.Ticks, 1900, 1);
        }
        if (value.Type == SqlType.DateTime)
        {
            var dt = value.AsDateTime;
            return (dt.Ticks, dt.Year, dt.Month);
        }
        if (value.Type == SqlType.SmallDateTime)
        {
            var dt = value.AsSmallDateTime;
            return (dt.Ticks, dt.Year, dt.Month);
        }
        if (value.Type is DateTime2SqlType)
        {
            var dt = value.AsDateTime2;
            return (dt.Ticks, dt.Year, dt.Month);
        }
        if (value.Type is DateTimeOffsetSqlType)
        {
            var utc = value.AsDateTimeOffset.UtcDateTime;
            return (utc.Ticks, utc.Year, utc.Month);
        }
        throw new NotSupportedException($"DATEDIFF: unhandled type {value.Type}.");
    }

    private static string FamilyRootName(SqlType type) => type switch
    {
        TimeSqlType => "time",
        DateTime2SqlType => "datetime2",
        DateTimeOffsetSqlType => "datetimeoffset",
        _ => type.ToString()!,
    };

    /// <summary>
    /// Returns the integer extraction of <paramref name="kind"/> from the
    /// non-NULL value <paramref name="value"/>. Caller must have already
    /// validated compatibility via <see cref="RequireCompatible"/>.
    /// </summary>
    public static int Extract(DatePartKind kind, SqlValue value)
    {
        var (date, time, offsetMinutes) = SplitDateTime(value);
        return kind switch
        {
            DatePartKind.Year => date.Year,
            DatePartKind.Quarter => ((date.Month - 1) / 3) + 1,
            DatePartKind.Month => date.Month,
            DatePartKind.DayOfYear => date.DayOfYear,
            DatePartKind.Day => date.Day,
            DatePartKind.Week => SqlServerWeek(date),
            DatePartKind.IsoWeek => System.Globalization.ISOWeek.GetWeekOfYear(date),
            // SQL Server's DATEPART(weekday, ...) returns 1-7 with the start
            // governed by SET DATEFIRST (default 7 = Sunday). Default mapping:
            // Sunday=1, Monday=2, ..., Saturday=7.
            DatePartKind.Weekday => (int)date.DayOfWeek + 1,
            DatePartKind.Hour => time.Hours,
            DatePartKind.Minute => time.Minutes,
            DatePartKind.Second => time.Seconds,
            // Higher-precision parts: derive from sub-second tick remainder.
            DatePartKind.Millisecond => (int)(time.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMillisecond),
            DatePartKind.Microsecond => (int)(time.Ticks % TimeSpan.TicksPerSecond / 10),
            DatePartKind.Nanosecond => (int)(time.Ticks % TimeSpan.TicksPerSecond * 100),
            DatePartKind.TzOffset => offsetMinutes,
            _ => throw new NotSupportedException($"DATEPART({kind}) isn't implemented."),
        };
    }

    /// <summary>
    /// Returns the result of <paramref name="value"/> + <paramref name="n"/>
    /// units of <paramref name="kind"/>, preserving the input's SQL type.
    /// Out-of-range output raises Msg 517. Caller must have already validated
    /// compatibility via <see cref="RequireCompatible"/>.
    /// </summary>
    public static SqlValue Add(DatePartKind kind, SqlValue value, int n)
    {
        try
        {
            return value.Type switch
            {
                _ when value.Type == SqlType.Date => AddToDate(value, kind, n),
                TimeSqlType => AddToTime(value, kind, n),
                _ when value.Type == SqlType.DateTime => SqlValue.FromDateTime(AddToDateTime(value.AsDateTime, kind, n)),
                _ when value.Type == SqlType.SmallDateTime => SqlValue.FromSmallDateTime(AddToDateTime(value.AsSmallDateTime, kind, n)),
                DateTime2SqlType => SqlValue.FromDateTime2(value.Type, AddToDateTime(value.AsDateTime2, kind, n)),
                DateTimeOffsetSqlType => SqlValue.FromDateTimeOffset(value.Type, AddToDateTimeOffset(value.AsDateTimeOffset, kind, n)),
                _ => throw new NotSupportedException($"DATEADD doesn't accept operand type {value.Type}."),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw SimulatedSqlException.DateAddOverflow(FamilyRootName(value.Type));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.DateAddOverflow(FamilyRootName(value.Type));
        }
        catch (SimulatedSqlException ex) when (ex.Number == 242)
        {
            // FromDateTime / FromSmallDateTime raise Msg 242 on out-of-range;
            // DATEADD's message is Msg 517, so re-wrap.
            throw SimulatedSqlException.DateAddOverflow(FamilyRootName(value.Type));
        }
    }

    private static SqlValue AddToDate(SqlValue value, DatePartKind kind, int n)
    {
        var date = value.AsDate;
        var added = kind switch
        {
            DatePartKind.Year => date.AddYears(n),
            DatePartKind.Quarter => date.AddMonths(checked(n * 3)),
            DatePartKind.Month => date.AddMonths(n),
            DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => date.AddDays(n),
            DatePartKind.Week or DatePartKind.IsoWeek => date.AddDays(checked(n * 7)),
            _ => throw new NotSupportedException($"DATEADD({kind}) on date isn't implemented."),
        };
        return SqlValue.FromDate(added);
    }

    private static SqlValue AddToTime(SqlValue value, DatePartKind kind, int n)
    {
        var ticks = value.AsTime.Ticks + (kind switch
        {
            DatePartKind.Hour => n * TimeSpan.TicksPerHour,
            DatePartKind.Minute => n * TimeSpan.TicksPerMinute,
            DatePartKind.Second => n * TimeSpan.TicksPerSecond,
            DatePartKind.Millisecond => n * TimeSpan.TicksPerMillisecond,
            DatePartKind.Microsecond => n * 10L,
            DatePartKind.Nanosecond => n / 100L,
            _ => throw new NotSupportedException($"DATEADD({kind}) on time isn't implemented."),
        });
        return ticks is < 0 or >= TimeSpan.TicksPerDay
            ? throw SimulatedSqlException.DateAddOverflow("time")
            : SqlValue.FromTime(value.Type, new TimeSpan(ticks));
    }

    private static DateTime AddToDateTime(DateTime input, DatePartKind kind, int n) => kind switch
    {
        DatePartKind.Year => input.AddYears(n),
        DatePartKind.Quarter => input.AddMonths(checked(n * 3)),
        DatePartKind.Month => input.AddMonths(n),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => input.AddDays(n),
        DatePartKind.Week or DatePartKind.IsoWeek => input.AddDays(checked(n * 7)),
        DatePartKind.Hour => input.AddHours(n),
        DatePartKind.Minute => input.AddMinutes(n),
        DatePartKind.Second => input.AddSeconds(n),
        DatePartKind.Millisecond => input.AddMilliseconds(n),
        DatePartKind.Microsecond => input.AddTicks(n * 10L),
        DatePartKind.Nanosecond => input.AddTicks(n / 100L),
        _ => throw new NotSupportedException($"DATEADD({kind}) on datetime isn't implemented."),
    };

    private static DateTimeOffset AddToDateTimeOffset(DateTimeOffset input, DatePartKind kind, int n) => kind switch
    {
        DatePartKind.Year => input.AddYears(n),
        DatePartKind.Quarter => input.AddMonths(checked(n * 3)),
        DatePartKind.Month => input.AddMonths(n),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => input.AddDays(n),
        DatePartKind.Week or DatePartKind.IsoWeek => input.AddDays(checked(n * 7)),
        DatePartKind.Hour => input.AddHours(n),
        DatePartKind.Minute => input.AddMinutes(n),
        DatePartKind.Second => input.AddSeconds(n),
        DatePartKind.Millisecond => input.AddMilliseconds(n),
        DatePartKind.Microsecond => input.AddTicks(n * 10L),
        DatePartKind.Nanosecond => input.AddTicks(n / 100L),
        DatePartKind.TzOffset => input.ToOffset(input.Offset + TimeSpan.FromMinutes(n)),
        _ => throw new NotSupportedException($"DATEADD({kind}) on datetimeoffset isn't implemented."),
    };

    /// <summary>
    /// Splits a date/time-family value into the <see cref="DateOnly"/>,
    /// time-of-day, and (for datetimeoffset) offset-in-minutes pieces the
    /// extraction switch needs. <c>date</c> values have no time portion;
    /// <c>time</c> values have no date portion (caller is responsible for
    /// only requesting parts the type carries — already gated by
    /// <see cref="RequireCompatible"/>).
    /// </summary>
    private static (DateOnly date, TimeSpan time, int offsetMinutes) SplitDateTime(SqlValue value)
    {
        if (value.Type == SqlType.Date)
            return (value.AsDate, TimeSpan.Zero, 0);
        if (value.Type is TimeSqlType)
            return (default, value.AsTime, 0);
        if (value.Type == SqlType.DateTime)
        {
            var dt = value.AsDateTime;
            return (DateOnly.FromDateTime(dt), dt.TimeOfDay, 0);
        }
        if (value.Type == SqlType.SmallDateTime)
        {
            var dt = value.AsSmallDateTime;
            return (DateOnly.FromDateTime(dt), dt.TimeOfDay, 0);
        }
        if (value.Type is DateTime2SqlType)
        {
            var dt = value.AsDateTime2;
            return (DateOnly.FromDateTime(dt), dt.TimeOfDay, 0);
        }
        if (value.Type is DateTimeOffsetSqlType)
        {
            var dto = value.AsDateTimeOffset;
            return (DateOnly.FromDateTime(dto.DateTime), dto.TimeOfDay, (int)dto.Offset.TotalMinutes);
        }
        throw new NotSupportedException($"SplitDateTime: unhandled type {value.Type}.");
    }

    /// <summary>
    /// SQL Server's default-week numbering with <c>SET DATEFIRST 7</c>
    /// (Sunday): January 1's week is week 1; subsequent weeks roll over on
    /// Sundays. Approximated here — SQL Server's exact algorithm depends on
    /// <c>DATEFIRST</c> + <c>SET LANGUAGE</c> and the simulator pins the
    /// default us_english behavior.
    /// </summary>
    private static int SqlServerWeek(DateOnly date)
    {
        var jan1 = new DateOnly(date.Year, 1, 1);
        var jan1DayOfWeek = (int)jan1.DayOfWeek;
        var daysFromJan1 = date.DayNumber - jan1.DayNumber;
        return ((daysFromJan1 + jan1DayOfWeek) / 7) + 1;
    }
}
