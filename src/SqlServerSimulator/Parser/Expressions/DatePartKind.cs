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
    /// Reads the datepart a date function takes as its first argument, the
    /// cursor on its first token and left on its last. It is a word, not an
    /// expression: a string, a number, a variable, a function call or
    /// <c>NULL</c> there is Msg 1023, a dotted name Msg 155 naming it whole,
    /// while a parenthesized word
    /// is read through its parentheses (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    public static DatePartKind Read(ParserContext context, string functionName, out string keywordText)
    {
        switch (context.Token)
        {
            case Tokens.Operator { Character: '(' }:
                context.MoveNextRequired();
                var inner = Read(context, functionName, out keywordText);
                return context.GetNextRequired() is Tokens.Operator { Character: ')' }
                    ? inner
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            case Tokens.Name name:
                keywordText = name.Value;
                var checkpoint = context.SaveCheckpoint();
                if (context.MoveNext() && context.Token is Tokens.Operator { Character: '(' })
                    throw SimulatedSqlException.InvalidParameterSpecifiedFor(1, functionName);
                if (context.Token is Tokens.Operator { Character: '.' })
                {
                    var dotted = name.Value;
                    while (context.Token is Tokens.Operator { Character: '.' } && context.MoveNext() && context.Token is Tokens.Name part)
                    {
                        dotted += "." + part.Value;
                        if (!context.MoveNext())
                            break;
                    }
                    throw SimulatedSqlException.NotARecognizedDatepartOption(dotted, functionName);
                }
                context.RestoreCheckpoint(checkpoint);
                return ResolveOrThrow(keywordText, functionName);
            default:
                throw SimulatedSqlException.InvalidParameterSpecifiedFor(1, functionName);
        }
    }

    /// <summary>
    /// Span-based keyword dispatch — matches the pattern used in
    /// <c>Parser/Expression.cs:ResolveBuiltIn</c> and
    /// <c>Storage/SqlType.cs:GetByName</c> so the parser stays
    /// allocation-free in its keyword-resolution hot paths.
    /// </summary>
    public static DatePartKind? Resolve(string keyword)
    {
        Span<char> upper = stackalloc char[keyword.Length];
        // SSS005 disabled: inner-length arms run by descending time-unit magnitude
        // (quarter → month → … → second), matching how the units are conceptualized,
        // not alphabetically.
#pragma warning disable SSS005
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
                "W" => DatePartKind.Weekday,
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
#pragma warning restore SSS005
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
    /// <c>1900-01-02</c>. Every other number and a binary read as
    /// <c>datetime</c> too (probed 2026-09-25: <c>DAY(1.5)</c> is 2,
    /// <c>DATEADD(day, 1, 0x01)</c> 1900-01-02); the types that can't reach
    /// <c>datetime</c> at all were refused while compiling.
    /// </summary>
    public static SqlValue CoerceDateArgumentImplicit(SqlValue value) =>
        SqlType.IsStringCategory(value.Type) ? value.CoerceTo(SqlType.GetDateTime2(7))
        : ReadsAsDateTime(value.Type) ? value.CoerceTo(SqlType.DateTime)
        : value;

    /// <summary>
    /// <see cref="CoerceDateArgumentImplicit"/> for one of <c>DATEDIFF</c>'s
    /// two dates: a string beside a number, a binary or a bare <c>NULL</c> —
    /// a partner that reads as <c>datetime</c> — reads as <c>datetime</c> too,
    /// so it rounds to that type's 1/300 s and raises its Msg 242 where the
    /// <c>datetime2</c> it reads as otherwise would not (probed 2026-09-25
    /// against SQL Server 2025: <c>DATEDIFF(ms, 0, '1900-01-01 00:00:00.001')</c>
    /// is 0).
    /// </summary>
    public static SqlValue CoerceDiffArgument(SqlValue value, SqlValue partner) =>
        SqlType.IsStringCategory(value.Type) && ReadsAsDateTime(partner.Type) ? value.CoerceTo(SqlType.DateTime)
        : CoerceDateArgumentImplicit(value);

    private static bool ReadsAsDateTime(SqlType type) =>
        type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate
        || type is BinarySqlType or VarbinarySqlType;

    /// <summary>
    /// Parallel of <see cref="CoerceDateArgumentImplicit"/> for the static
    /// projection path: maps string types to <c>datetime2(7)</c> and
    /// numeric and binary types to legacy <c>datetime</c>; everything else passes
    /// through. Used so a date function's schema matches the runtime type
    /// for the implicit-cast cases (a string-typed source projects as
    /// datetime2, not the input's varchar — <c>DATEADD</c> is the exception,
    /// reading a string as <c>datetime</c>).
    /// </summary>
    public static SqlType ResolveImplicitDateType(SqlType source) =>
        SqlType.IsStringCategory(source) ? SqlType.GetDateTime2(7)
        : ReadsAsDateTime(source) ? SqlType.DateTime
        : source;

    /// <summary>
    /// <c>DATETRUNC</c>, <c>DATE_BUCKET</c> and <c>EOMONTH</c> take a date
    /// and nothing a number or a binary would convert to: any other type —
    /// a typed <c>NULL</c> or an empty rowset's column included — is Msg 8116
    /// while compiling (probed 2026-09-25 against SQL Server 2025). A string
    /// passes where <paramref name="acceptsString"/> says so, and a
    /// <c>time</c> where <paramref name="acceptsTime"/> does.
    /// </summary>
    public static SqlType RequireDateArgument(Expression argument, SqlType type, int argumentIndex, string functionName, bool acceptsString, bool acceptsTime)
    {
        if (Expression.IsUntypedNullLiteral(argument)
            || (SqlType.IsDateTimeCategory(type) && (acceptsTime || type is not TimeSqlType))
            || (acceptsString && SqlType.IsStringCategory(type) && type is not XmlSqlType))
        {
            return type;
        }
        throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, argument), argumentIndex, functionName);
    }

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
    public static void RequireCompatible(DatePartKind kind, SqlType type, string functionLowerName)
    {
        var legacy = type == SqlType.DateTime || type == SqlType.SmallDateTime;
        var ok = type switch
        {
            _ when type == SqlType.Date => IsDatePart(kind),
            TimeSqlType => IsTimePart(kind),
            _ when legacy => IsDatePart(kind) || IsTimePart(kind),
            // DATEPART / DATENAME read a datetime2's offset as zero.
            DateTime2SqlType => IsDatePart(kind) || IsTimePart(kind) || (IsTzPart(kind) && functionLowerName is "datepart" or "datename"),
            DateTimeOffsetSqlType => IsDatePart(kind) || IsTimePart(kind) || IsTzPart(kind),
            _ => throw new NotSupportedException($"DATEPART/DATEADD doesn't accept operand type {type}."),
        };
        // DATEPART / DATENAME read a smalldatetime as a datetime, and name it
        // so (probed 2026-09-26 against SQL Server 2025).
        if (!ok)
        {
            var typeName = type == SqlType.SmallDateTime && functionLowerName is "datepart" or "datename" ? "datetime" : FamilyRootName(type);
            throw SimulatedSqlException.DatepartNotSupportedForType(CanonicalName(kind), functionLowerName, typeName, IncompatibleDatepartState(functionLowerName, type, kind));
        }

        // What a type holds, some functions still refuse (probed 2026-09-26
        // against SQL Server 2025): DATEADD moves no ISO week or offset, nor a
        // legacy type's sub-millisecond parts; DATETRUNC truncates to no
        // weekday, offset or nanosecond, nor a legacy type's microsecond —
        // or a smalldatetime's millisecond.
        var refused = functionLowerName switch
        {
            "dateadd" => kind is DatePartKind.IsoWeek or DatePartKind.TzOffset
                || (legacy && kind is DatePartKind.Microsecond or DatePartKind.Nanosecond),
            "datetrunc" => kind is DatePartKind.Weekday or DatePartKind.TzOffset or DatePartKind.Nanosecond
                || (legacy && kind == DatePartKind.Microsecond)
                || (type == SqlType.SmallDateTime && kind == DatePartKind.Millisecond),
            _ => false,
        };
        if (refused)
            throw SimulatedSqlException.DatepartNotSupportedForType(CanonicalName(kind), functionLowerName, FamilyRootName(type), FunctionRefusalState(functionLowerName, type));
    }

    /// <summary>
    /// The state of a function-level Msg 9810, which names the operand type:
    /// DATEADD's 0 / 3 / 2 for datetime / smalldatetime / the rest, DATETRUNC's
    /// 9 / 8 / 11 likewise (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static byte FunctionRefusalState(string functionName, SqlType type) => functionName switch
    {
        "dateadd" => type == SqlType.DateTime ? (byte)0 : type == SqlType.SmallDateTime ? (byte)3 : (byte)2,
        _ => type == SqlType.DateTime ? (byte)9 : type == SqlType.SmallDateTime ? (byte)8 : (byte)11,
    };

    /// <summary>
    /// The datepart's own name, which real's Msg 9810 reports whatever
    /// abbreviation the statement wrote (probed 2026-09-25 against SQL Server
    /// 2025: <c>DATEPART(dy, …)</c> names dayofyear).
    /// </summary>
    private static string CanonicalName(DatePartKind kind) => kind switch
    {
        DatePartKind.Year => "year",
        DatePartKind.Quarter => "quarter",
        DatePartKind.Month => "month",
        DatePartKind.DayOfYear => "dayofyear",
        DatePartKind.Day => "day",
        DatePartKind.Week => "week",
        DatePartKind.IsoWeek => "iso_week",
        DatePartKind.Weekday => "weekday",
        DatePartKind.Hour => "hour",
        DatePartKind.Minute => "minute",
        DatePartKind.Second => "second",
        DatePartKind.Millisecond => "millisecond",
        DatePartKind.Microsecond => "microsecond",
        DatePartKind.Nanosecond => "nanosecond",
        _ => "tzoffset",
    };

    /// <summary>
    /// The state real's Msg 9810 carries, which names the function and the
    /// operand type between them (probed 2026-09-24 against SQL Server 2025).
    /// </summary>
    private static byte IncompatibleDatepartState(string functionName, SqlType type, DatePartKind kind) => functionName switch
    {
        // A date / time operand's own mismatch is 1 and 10; a type that could
        // hold the part but not an offset takes the function-level state.
        "dateadd" => type == SqlType.Date || type is TimeSqlType ? (byte)1 : FunctionRefusalState(functionName, type),
        "datename" => type == SqlType.Date ? (byte)4 : type is TimeSqlType ? (byte)5 : (byte)7,
        "datepart" => type == SqlType.Date ? (byte)2 : type is TimeSqlType ? (byte)3 : (byte)6,
        "datetrunc" => IsTzPart(kind) && type != SqlType.Date && type is not TimeSqlType ? FunctionRefusalState(functionName, type) : (byte)10,
        _ => 1,
    };

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

    private static int LegacyNanoseconds(TimeSpan time)
    {
        var fraction = time.Ticks % TimeSpan.TicksPerSecond;
        var units300 = DateTimeSqlType.UnitsFromTicks(fraction);
        return (int)(units300 * 1_000_000_000L / 300);
    }

    /// <summary>
    /// Returns the integer extraction of <paramref name="kind"/> from the
    /// non-NULL value <paramref name="value"/>. Caller must have already
    /// validated compatibility via <see cref="RequireCompatible"/>.
    /// </summary>
    public static int Extract(DatePartKind kind, SqlValue value, int dateFirst = 7)
    {
        var (date, time, offsetMinutes) = SplitDateTime(value);
        return kind switch
        {
            DatePartKind.Year => date.Year,
            DatePartKind.Quarter => ((date.Month - 1) / 3) + 1,
            DatePartKind.Month => date.Month,
            DatePartKind.DayOfYear => date.DayOfYear,
            DatePartKind.Day => date.Day,
            DatePartKind.Week => SqlServerWeek(date, dateFirst),
            DatePartKind.IsoWeek => System.Globalization.ISOWeek.GetWeekOfYear(date),
            DatePartKind.Weekday => WeekdayNumber(date, dateFirst),
            DatePartKind.Hour => time.Hours,
            DatePartKind.Minute => time.Minutes,
            DatePartKind.Second => time.Seconds,
            // Higher-precision parts: derive from sub-second tick remainder.
            DatePartKind.Millisecond => (int)(time.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMillisecond),
            DatePartKind.Microsecond => (int)(time.Ticks % TimeSpan.TicksPerSecond / 10),
            // A datetime's fraction is a count of 1/300 seconds, and real
            // scales that count rather than the rounded 100 ns ticks
            // (probe-confirmed 2026-09-23: .123 is 37/300 s, 123333333 ns).
            DatePartKind.Nanosecond => value.Type == SqlType.DateTime
                ? LegacyNanoseconds(time)
                : (int)(time.Ticks % TimeSpan.TicksPerSecond * 100),
            DatePartKind.TzOffset => offsetMinutes,
            _ => throw new NotSupportedException($"DATEPART({kind}) isn't implemented."),
        };
    }

    /// <summary>
    /// Coerces a DATEADD count to <c>int</c>. A value outside int range can
    /// only push the result past the date type's representable range, which
    /// SQL Server reports as the same Msg 517 date overflow <see cref="Add"/>
    /// raises — not an int-conversion error (probe-confirmed across second /
    /// day / year). Without this, the bigint-to-int narrowing would leak a
    /// raw <see cref="OverflowException"/>.
    /// </summary>
    public static long CoerceCount(SqlValue number, SqlType targetType)
    {
        try
        {
            return number.CoerceTo(SqlType.BigInt).AsInt64;
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.DateAddOverflow(FamilyRootName(targetType));
        }
    }

    /// <summary>
    /// Returns the result of <paramref name="value"/> + <paramref name="n"/>
    /// units of <paramref name="kind"/>, preserving the input's SQL type.
    /// Out-of-range output raises Msg 517. Caller must have already validated
    /// compatibility via <see cref="RequireCompatible"/>.
    /// </summary>
    public static SqlValue Add(DatePartKind kind, SqlValue value, long n)
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

    private static SqlValue AddToDate(SqlValue value, DatePartKind kind, long n)
    {
        var date = value.AsDate;
        // Year / month / quarter feed DateOnly's int-typed adders; day / week
        // feed DateOnly.AddDays(int). An interval too large for int can only
        // overflow the date's range, so the checked int-narrowing surfaces the
        // same Msg 517 as an out-of-range DateOnly result (Add re-wraps both).
        var added = kind switch
        {
            DatePartKind.Year => date.AddYears(checked((int)n)),
            DatePartKind.Quarter => date.AddMonths(checked((int)(n * 3))),
            DatePartKind.Month => date.AddMonths(checked((int)n)),
            DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => date.AddDays(checked((int)n)),
            DatePartKind.Week or DatePartKind.IsoWeek => date.AddDays(checked((int)(n * 7))),
            _ => throw new NotSupportedException($"DATEADD({kind}) on date isn't implemented."),
        };
        return SqlValue.FromDate(added);
    }

    private static SqlValue AddToTime(SqlValue value, DatePartKind kind, long n)
    {
        // checked so an interval overflowing the 100-ns tick range raises
        // Msg 517 via Add's catch rather than silently wrapping.
        var ticks = checked(value.AsTime.Ticks + (kind switch
        {
            DatePartKind.Hour => n * TimeSpan.TicksPerHour,
            DatePartKind.Minute => n * TimeSpan.TicksPerMinute,
            DatePartKind.Second => n * TimeSpan.TicksPerSecond,
            DatePartKind.Millisecond => n * TimeSpan.TicksPerMillisecond,
            DatePartKind.Microsecond => n * 10L,
            DatePartKind.Nanosecond => n / 100L,
            _ => throw new NotSupportedException($"DATEADD({kind}) on time isn't implemented."),
        }));
        return ticks is < 0 or >= TimeSpan.TicksPerDay
            ? throw SimulatedSqlException.DateAddOverflow("time")
            : SqlValue.FromTime(value.Type, new TimeSpan(ticks));
    }

    private static DateTime AddToDateTime(DateTime input, DatePartKind kind, long n) => kind switch
    {
        DatePartKind.Year => input.AddYears(checked((int)n)),
        DatePartKind.Quarter => input.AddMonths(checked((int)(n * 3))),
        DatePartKind.Month => input.AddMonths(checked((int)n)),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => input.AddDays(n),
        DatePartKind.Week or DatePartKind.IsoWeek => input.AddDays(checked(n * 7)),
        DatePartKind.Hour => input.AddHours(n),
        DatePartKind.Minute => input.AddMinutes(n),
        DatePartKind.Second => input.AddSeconds(n),
        DatePartKind.Millisecond => input.AddMilliseconds(n),
        DatePartKind.Microsecond => input.AddTicks(checked(n * 10L)),
        DatePartKind.Nanosecond => input.AddTicks(n / 100L),
        _ => throw new NotSupportedException($"DATEADD({kind}) on datetime isn't implemented."),
    };

    private static DateTimeOffset AddToDateTimeOffset(DateTimeOffset input, DatePartKind kind, long n) => kind switch
    {
        DatePartKind.Year => input.AddYears(checked((int)n)),
        DatePartKind.Quarter => input.AddMonths(checked((int)(n * 3))),
        DatePartKind.Month => input.AddMonths(checked((int)n)),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => input.AddDays(n),
        DatePartKind.Week or DatePartKind.IsoWeek => input.AddDays(checked(n * 7)),
        DatePartKind.Hour => input.AddHours(n),
        DatePartKind.Minute => input.AddMinutes(n),
        DatePartKind.Second => input.AddSeconds(n),
        DatePartKind.Millisecond => input.AddMilliseconds(n),
        DatePartKind.Microsecond => input.AddTicks(checked(n * 10L)),
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
    /// SQL Server's <c>DATEPART(weekday, …)</c>: 1..7 counting from the weekday
    /// <c>SET DATEFIRST</c> names as the week's first. With the default 7
    /// (Sunday) that is Sunday=1 … Saturday=7; with <c>DATEFIRST 3</c>
    /// (Wednesday) Sunday reads 5 and Monday 6 (probe-confirmed across
    /// DATEFIRST 1 / 3 / 5 / 7).
    /// </summary>
    public static int WeekdayNumber(DateOnly date, int dateFirst) =>
        (((int)date.DayOfWeek + 7 - dateFirst) % 7) + 1;

    /// <summary>
    /// SQL Server's <c>DATEPART(week, …)</c>: January 1 is in week 1, and the
    /// number advances at each weekday <c>SET DATEFIRST</c> names, so the value
    /// moves with the option (probe-confirmed: 2026-01-04, a Sunday, is week 2
    /// under DATEFIRST 7 and week 1 under DATEFIRST 1). The ISO variant, which
    /// is fixed to Monday, has its own kind.
    /// </summary>
    private static int SqlServerWeek(DateOnly date, int dateFirst)
    {
        var jan1 = new DateOnly(date.Year, 1, 1);
        return ((date.DayNumber - jan1.DayNumber + WeekdayNumber(jan1, dateFirst) - 1) / 7) + 1;
    }
}
