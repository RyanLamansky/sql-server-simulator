using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATETRUNC(&lt;datepart&gt;, &lt;date-expr&gt;)</c>: floor the
/// date/time value to the start of the given part (start-of-year,
/// start-of-month, start-of-day, etc.). Returns the same type as the
/// input. Week truncation anchors on the weekday <c>SET DATEFIRST</c> names,
/// so it moves with the option (probe-confirmed against SQL Server 2025:
/// a Wednesday truncates to the preceding Sunday under DATEFIRST 7, to the
/// preceding Monday under DATEFIRST 1 and to itself under DATEFIRST 3); the
/// ISO variant stays Monday-anchored.
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
        DatePartKinds.RequireCompatible(this.kind, this.keywordText, t, "datetrunc");
        var dateFirst = runtime.Batch.Connection.DateFirst;
        if (t is TimeSqlType)
            return SqlValue.FromTime(t, TruncateDateTime(new DateTime(1900, 1, 1).Add(value.AsTime), this.kind, dateFirst).TimeOfDay);
        if (t == SqlType.Date)
            return SqlValue.FromDate(TruncateDate(value.AsDate, this.kind, dateFirst));
        if (t == SqlType.DateTime)
            return SqlValue.FromDateTime(TruncateDateTime(value.AsDateTime, this.kind, dateFirst));
        if (t == SqlType.SmallDateTime)
            return SqlValue.FromSmallDateTime(TruncateDateTime(value.AsSmallDateTime, this.kind, dateFirst));
        if (t is DateTime2SqlType)
            return SqlValue.FromDateTime2(t, TruncateDateTime(value.AsDateTime2, this.kind, dateFirst));
        if (t is DateTimeOffsetSqlType)
        {
            var dto = value.AsDateTimeOffset;
            var truncated = TruncateDateTime(dto.DateTime, this.kind, dateFirst);
            return SqlValue.FromDateTimeOffset(t, new DateTimeOffset(truncated, dto.Offset));
        }
        throw new NotSupportedException($"DATETRUNC on {t} not supported.");
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        DatePartKinds.ResolveImplicitDateType(this.source.GetSqlType(batch, resolveColumnType));

    internal override string DebugDisplay() => $"DATETRUNC({this.keywordText}, {this.source.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.source);

    private static DateOnly TruncateDate(DateOnly d, DatePartKind k, int dateFirst) => k switch
    {
        DatePartKind.Year => new DateOnly(d.Year, 1, 1),
        DatePartKind.Quarter => new DateOnly(d.Year, ((d.Month - 1) / 3 * 3) + 1, 1),
        DatePartKind.Month => new DateOnly(d.Year, d.Month, 1),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => d,
        DatePartKind.Week => d.AddDays(1 - DatePartKinds.WeekdayNumber(d, dateFirst)),
        DatePartKind.IsoWeek => d.AddDays(-(d.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)d.DayOfWeek - 1)),
        _ => throw new InvalidOperationException($"RequireCompatible admits no {k} for a date."),
    };

    private static DateTime TruncateDateTime(DateTime dt, DatePartKind k, int dateFirst) => k switch
    {
        DatePartKind.Year => new DateTime(dt.Year, 1, 1),
        DatePartKind.Quarter => new DateTime(dt.Year, ((dt.Month - 1) / 3 * 3) + 1, 1),
        DatePartKind.Month => new DateTime(dt.Year, dt.Month, 1),
        DatePartKind.DayOfYear or DatePartKind.Day or DatePartKind.Weekday => dt.Date,
        DatePartKind.Week => dt.Date.AddDays(1 - DatePartKinds.WeekdayNumber(DateOnly.FromDateTime(dt), dateFirst)),
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

    /// <summary>
    /// Reads the offset argument of <paramref name="functionName"/>
    /// (<c>switchoffset</c> or <c>todatetimeoffset</c>) as minutes. A string
    /// must be exactly <c>[+|-]hh:mm</c> — no padding, no other width, minutes
    /// under 60 — and within ±14:00; anything else is Msg 9812, whose state
    /// tells the function and a string from a number apart (probed 2026-09-24
    /// against SQL Server 2025).
    /// </summary>
    internal static int ParseOffsetMinutes(SqlValue v, string functionName)
    {
        var isSwitch = functionName == "switchoffset";
        if (SqlType.IsStringCategory(v.Type))
        {
            var s = v.CoerceTo(SqlType.NVarchar).AsString;
            var invalid = SimulatedSqlException.InvalidTimeZone(functionName, isSwitch ? (byte)0 : (byte)2);
            if (s.Length != 6 || s[0] is not ('+' or '-') || s[3] != ':'
                || !char.IsAsciiDigit(s[1]) || !char.IsAsciiDigit(s[2]) || !char.IsAsciiDigit(s[4]) || !char.IsAsciiDigit(s[5]))
            {
                throw invalid;
            }
            var hours = ((s[1] - '0') * 10) + (s[2] - '0');
            var minutes = ((s[4] - '0') * 10) + (s[5] - '0');
            var total = (hours * 60) + minutes;
            return minutes >= 60 || total > 840 ? throw invalid : s[0] == '-' ? -total : total;
        }
        // The minute offset is declared smallint, so an out-of-range one
        // reports that narrowing rather than an int one — Msg 8115 naming
        // smallint for a bigint argument, the value-bearing Msg 220 for an
        // int argument (probe-confirmed 2026-07-31).
        var count = ScalarArguments.CoerceToSmallInt(v);
        return count is < -840 or > 840
            ? throw SimulatedSqlException.InvalidTimeZone(functionName, isSwitch ? (byte)1 : (byte)3)
            : count;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.dtoArg.GetSqlType(batch, resolveColumnType) is DateTimeOffsetSqlType t ? t : SqlType.GetDateTimeOffset(7);

    internal override string DebugDisplay() => $"SWITCHOFFSET({this.dtoArg.DebugDisplay()}, {this.offsetArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.dtoArg).Child(this.offsetArg);
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

    internal override void Describe(NodeShape shape) => shape.Child(this.dtArg).Child(this.offsetArg);
}
