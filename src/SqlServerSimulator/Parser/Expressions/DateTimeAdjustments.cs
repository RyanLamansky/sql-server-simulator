using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATETRUNC(&lt;datepart&gt;, &lt;date-expr&gt;)</c>: floor the
/// date/time value to the start of the given part (start-of-year,
/// start-of-month, start-of-day, etc.). Returns the same type as the
/// input. Week truncation uses Sunday-anchored weeks (matching the
/// simulator's @@DATEFIRST default of 7). Probe-confirmed against
/// SQL Server 2025 (2026-05-22).
/// </summary>
internal sealed class DateTrunc : Expression
{
    private readonly DatePartKind kind;
    private readonly string keywordText;
    private readonly Expression source;

    public DateTrunc(ParserContext context)
    {
        this.keywordText = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        this.kind = DatePartKinds.ResolveOrThrow(this.keywordText, "datetrunc");
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.source = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = this.source.Run(runtime);
        if (raw.IsNull)
            return SqlValue.Null(raw.Type);
        var value = DatePartKinds.CoerceDateArgumentImplicit(raw);
        var t = value.Type;
        if (t == SqlType.Date)
            return SqlValue.FromDate(TruncateDate(value.AsDate, this.kind));
        if (t == SqlType.DateTime)
            return SqlValue.FromDateTime(TruncateDateTime(value.AsDateTime, this.kind));
        if (t == SqlType.SmallDateTime)
            return SqlValue.FromSmallDateTime(TruncateDateTime(value.AsSmallDateTime, this.kind));
        if (t is DateTime2SqlType)
            return SqlValue.FromDateTime2(t, TruncateDateTime(value.AsDateTime2, this.kind));
        if (t is DateTimeOffsetSqlType)
        {
            var dto = value.AsDateTimeOffset;
            var truncated = TruncateDateTime(dto.DateTime, this.kind);
            return SqlValue.FromDateTimeOffset(t, new DateTimeOffset(truncated, dto.Offset));
        }
        throw new NotSupportedException($"DATETRUNC on {t} not supported.");
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        DatePartKinds.ResolveImplicitDateType(this.source.GetSqlType(batch, resolveColumnType));

    internal override string DebugDisplay() => $"DATETRUNC({this.keywordText}, {this.source.DebugDisplay()})";

    private static DateOnly TruncateDate(DateOnly d, DatePartKind k) => k switch
    {
        DatePartKind.Year => new DateOnly(d.Year, 1, 1),
        DatePartKind.Quarter => new DateOnly(d.Year, ((d.Month - 1) / 3 * 3) + 1, 1),
        DatePartKind.Month => new DateOnly(d.Year, d.Month, 1),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => d,
        DatePartKind.Week => d.AddDays(-(int)d.DayOfWeek),
        DatePartKind.IsoWeek => d.AddDays(-(d.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)d.DayOfWeek - 1)),
        _ => throw SimulatedSqlException.DatepartNotSupportedForType("datetrunc", "datetrunc", "date"),
    };

    private static DateTime TruncateDateTime(DateTime dt, DatePartKind k) => k switch
    {
        DatePartKind.Year => new DateTime(dt.Year, 1, 1),
        DatePartKind.Quarter => new DateTime(dt.Year, ((dt.Month - 1) / 3 * 3) + 1, 1),
        DatePartKind.Month => new DateTime(dt.Year, dt.Month, 1),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => dt.Date,
        DatePartKind.Week => dt.Date.AddDays(-(int)dt.DayOfWeek),
        DatePartKind.IsoWeek => dt.Date.AddDays(-(dt.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)dt.DayOfWeek - 1)),
        DatePartKind.Hour => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0),
        DatePartKind.Minute => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0),
        DatePartKind.Second => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second),
        DatePartKind.Millisecond => new DateTime(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerMillisecond)),
        DatePartKind.Microsecond => new DateTime(dt.Ticks - (dt.Ticks % 10)),
        DatePartKind.Nanosecond => dt,
        _ => throw new NotSupportedException($"DATETRUNC({k}) not supported."),
    };
}

/// <summary>
/// SQL <c>SWITCHOFFSET(dto, offset)</c>: returns the input
/// <c>datetimeoffset</c> adjusted to the new offset, preserving the
/// UTC instant. Offset is accepted as a string ('-05:00') or integer
/// minutes. NULL on either argument returns NULL of the input type.
/// </summary>
internal sealed class SwitchOffset : Expression
{
    private readonly Expression dtoArg;
    private readonly Expression offsetArg;

    public SwitchOffset(ParserContext context)
    {
        this.dtoArg = Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.offsetArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.dtoArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(v.Type is DateTimeOffsetSqlType t ? t : SqlType.GetDateTimeOffset(7));
        if (v.Type is not DateTimeOffsetSqlType)
            v = v.CoerceTo(SqlType.GetDateTimeOffset(7));
        var off = this.offsetArg.Run(runtime);
        if (off.IsNull)
            return SqlValue.Null(v.Type);
        var offsetMinutes = ParseOffsetMinutes(off, "switchoffset");
        var adjusted = v.AsDateTimeOffset.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        return SqlValue.FromDateTimeOffset(v.Type, adjusted);
    }

    internal static int ParseOffsetMinutes(SqlValue v)
    {
        if (SqlType.IsStringCategory(v.Type))
        {
            var s = v.CoerceTo(SqlType.NVarchar).AsString.Trim();
            var sign = 1;
            if (s.StartsWith('+'))
            {
                s = s[1..];
            }
            else if (s.StartsWith('-'))
            {
                sign = -1;
                s = s[1..];
            }
            var colonIdx = s.IndexOf(':', StringComparison.Ordinal);
            return colonIdx < 0
                ? sign * int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
                : sign * ((int.Parse(s[..colonIdx], System.Globalization.CultureInfo.InvariantCulture) * 60)
                    + int.Parse(s[(colonIdx + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }
        // The minute offset is declared smallint, so an out-of-range one
        // reports that narrowing rather than an int one — Msg 8115 naming
        // smallint for a bigint argument, the value-bearing Msg 220 for an
        // int argument (probe-confirmed 2026-07-31).
        return ScalarArguments.CoerceToSmallInt(v);
    }

    /// <summary>
    /// Parses the offset and enforces SQL Server's legal ±14:00 range,
    /// raising Msg 9812 (named for <paramref name="functionName"/>) when it
    /// is exceeded — instead of letting <see cref="DateTimeOffset"/> throw an
    /// internal <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal static int ParseOffsetMinutes(SqlValue v, string functionName)
    {
        var minutes = ParseOffsetMinutes(v);
        return minutes is < -840 or > 840
            ? throw SimulatedSqlException.InvalidTimeZone(functionName)
            : minutes;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.dtoArg.GetSqlType(batch, resolveColumnType) is DateTimeOffsetSqlType t ? t : SqlType.GetDateTimeOffset(7);

    internal override string DebugDisplay() => $"SWITCHOFFSET({this.dtoArg.DebugDisplay()}, {this.offsetArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>TODATETIMEOFFSET(dt, offset)</c>: attaches the given offset to
/// the input datetime / datetime2 / string, producing a
/// <c>datetimeoffset</c> at the same wall-clock time. Distinct from
/// <see cref="SwitchOffset"/>: this one assumes the input wall-clock is
/// in the target offset (no UTC conversion).
/// </summary>
internal sealed class ToDateTimeOffset : Expression
{
    private static readonly SqlType ResultType = SqlType.GetDateTimeOffset(7);

    private readonly Expression dtArg;
    private readonly Expression offsetArg;

    public ToDateTimeOffset(ParserContext context)
    {
        this.dtArg = Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.offsetArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.dtArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(ResultType);
        var off = this.offsetArg.Run(runtime);
        if (off.IsNull)
            return SqlValue.Null(ResultType);
        var offsetMinutes = SwitchOffset.ParseOffsetMinutes(off, "todatetimeoffset");
        var dt = v.Type == SqlType.DateTime ? v.AsDateTime
            : v.Type == SqlType.SmallDateTime ? v.AsSmallDateTime
            : v.Type is DateTime2SqlType ? v.AsDateTime2
            : v.Type == SqlType.Date ? v.AsDate.ToDateTime(TimeOnly.MinValue)
            : v.CoerceTo(SqlType.GetDateTime2(7)).AsDateTime2;
        return SqlValue.FromDateTimeOffset(ResultType, new DateTimeOffset(dt, TimeSpan.FromMinutes(offsetMinutes)));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => $"TODATETIMEOFFSET({this.dtArg.DebugDisplay()}, {this.offsetArg.DebugDisplay()})";
}
