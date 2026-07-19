using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>&lt;date-time-expr&gt; AT TIME ZONE &lt;tz-expr&gt;</c>: converts
/// the LHS to <c>datetimeoffset</c> in the supplied time zone. Result type is
/// <c>datetimeoffset</c> with the LHS's fractional precision preserved
/// (<c>datetime2(N)</c> / <c>datetimeoffset(N)</c> stay at precision <c>N</c>;
/// <c>datetime</c> lands at <c>datetimeoffset(3)</c>, matching the legacy
/// type's milliseconds resolution). <c>date</c> and <c>time</c> LHS raise
/// <c>Msg 8116</c>; unrecognized zone names raise <c>Msg 9820</c>. NULL on
/// either side propagates to NULL of the result type.
/// </summary>
/// <remarks>
/// <para>Two zone-conversion semantics, distinguished by LHS type:</para>
/// <list type="bullet">
/// <item><c>datetime2 / datetime / smalldatetime AT TIME ZONE 'X'</c>: treats
/// the LHS wall-clock as already in zone X, returning that wall-clock with
/// X's offset attached. Skipped (spring-forward) wall-clocks shift forward by
/// the DST delta; ambiguous (fall-back) wall-clocks pick the daylight
/// (pre-fall-back) interpretation — probe-confirmed against SQL Server 2025.</item>
/// <item><c>datetimeoffset AT TIME ZONE 'X'</c>: preserves the UTC instant
/// and re-expresses it in zone X (offset and wall-clock both change to match).</item>
/// </list>
/// <para>Time-zone names route through .NET 6+'s
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>, which accepts both
/// Windows-style identifiers (<c>"Pacific Standard Time"</c>) and IANA names
/// (<c>"America/Los_Angeles"</c>) cross-platform via ICU. The lookup result is
/// cached per zone-name string to keep per-row overhead at a hashtable lookup.</para>
/// <para>Zone-name binding is tighter than <c>+</c> (probe-confirmed: <c>expr AT
/// TIME ZONE 'UT' + 'C'</c> raises Msg 402 because real SQL Server parses it as
/// <c>(expr AT TIME ZONE 'UT') + 'C'</c>, not <c>expr AT TIME ZONE 'UTC'</c>).
/// To match, the zone-name slot accepts only a primary expression: a literal,
/// a numeric literal, a <c>NULL</c>, an <c>@variable</c>, an unqualified
/// column reference, or a parenthesized full expression. Multi-part dotted
/// column refs and binary-operator chains in the zone-name slot aren't
/// modeled — wrap in parens if needed.</para>
/// </remarks>
internal sealed class AtTimeZone(Expression source, Expression zoneNameExpression) : Expression
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo?> ZoneCache = new();

    private readonly Expression source = source;
    private readonly Expression zoneNameExpression = zoneNameExpression;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(this.source.GetSqlType(batch, resolveColumnType));

    public override SqlValue Run(RuntimeContext runtime)
    {
        var input = this.source.Run(runtime);
        var resultType = ResolveResultType(input.Type);
        var zoneName = this.zoneNameExpression.Run(runtime);

        // NULL on either side → NULL datetimeoffset (probe-confirmed).
        if (input.IsNull || zoneName.IsNull)
            return SqlValue.Null(resultType);

        // Reject date / time at runtime — real SQL Server raises Msg 8116
        // post-parse, even when the LHS is a column reference whose type is
        // statically known.
        if (input.Type == SqlType.Date)
            throw SimulatedSqlException.AtTimeZoneInvalidArgument("date");
        if (input.Type is TimeSqlType)
            throw SimulatedSqlException.AtTimeZoneInvalidArgument("time");

        var zone = ResolveZone(zoneName.AsString);

        if (input.Type is DateTimeOffsetSqlType)
        {
            // datetimeoffset → datetimeoffset: preserve the UTC instant,
            // re-express it in the target zone.
            var converted = TimeZoneInfo.ConvertTime(input.AsDateTimeOffset, zone);
            return SqlValue.FromDateTimeOffset(resultType, converted);
        }

        // datetime2 / datetime / smalldatetime: take the wall-clock and
        // attach the target zone's offset for that wall-clock.
        var wall = input.Type == SqlType.DateTime ? input.AsDateTime
            : input.Type == SqlType.SmallDateTime ? input.AsSmallDateTime
            : input.Type is DateTime2SqlType ? input.AsDateTime2
            : throw SimulatedSqlException.AtTimeZoneInvalidArgument(input.Type.ToString()!);

        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        DateTimeOffset zoned;
        if (zone.IsInvalidTime(wall))
        {
            // Spring-forward gap (e.g. Pacific 2026-03-08 02:30): SQL Server
            // shifts the wall-clock forward by the DST delta and stamps the
            // post-transition (daylight) offset. .NET's GetUtcOffset returns
            // the standard offset for invalid times, so we manually adjust.
            var rule = zone.GetAdjustmentRules()
                .FirstOrDefault(r => r.DateStart <= wall && wall <= r.DateEnd);
            var delta = rule?.DaylightDelta ?? TimeSpan.FromHours(1);
            var shifted = wall.Add(delta);
            zoned = new DateTimeOffset(shifted, zone.GetUtcOffset(shifted));
        }
        else if (zone.IsAmbiguousTime(wall))
        {
            // Fall-back ambiguous wall-clock: SQL Server picks the daylight
            // (pre-fall-back) interpretation. The daylight interpretation has
            // the offset that produces the EARLIER UTC instant (because it's
            // the "first occurrence" of that wall-clock).
            var offsets = zone.GetAmbiguousTimeOffsets(wall);
            var pick = offsets[0];
            for (var i = 1; i < offsets.Length; i++)
            {
                if (wall - offsets[i] < wall - pick)
                    pick = offsets[i];
            }
            zoned = new DateTimeOffset(wall, pick);
        }
        else
        {
            zoned = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        return SqlValue.FromDateTimeOffset(resultType, zoned);
    }

    private static SqlType ResolveResultType(SqlType lhs) => lhs switch
    {
        DateTime2SqlType dt2 => SqlType.GetDateTimeOffset(dt2.precision),
        DateTimeOffsetSqlType dto => SqlType.GetDateTimeOffset(dto.precision),
        _ when lhs == SqlType.DateTime || lhs == SqlType.SmallDateTime => SqlType.GetDateTimeOffset(3),
        // Fallback for date / time (which raise Msg 8116 at runtime) and
        // unknown LHS types — GetSqlType requires a non-throwing answer for
        // projection-schema resolution that may not be reached at runtime.
        _ => SqlType.GetDateTimeOffset(7),
    };

    private static TimeZoneInfo ResolveZone(string name)
    {
        var resolved = ZoneCache.GetOrAdd(name, static n =>
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(n); }
            catch (TimeZoneNotFoundException) { return null; }
            catch (InvalidTimeZoneException) { return null; }
        });
        return resolved ?? throw SimulatedSqlException.InvalidTimeZoneParameter(name);
    }

    /// <summary>
    /// Parses the <c>AT TIME ZONE &lt;tz-expr&gt;</c> postfix when invoked
    /// from <see cref="Expression.Parse"/>'s binary-operator loop. The current
    /// token is the <c>AT</c> contextual keyword; this method consumes
    /// <c>TIME</c>, <c>ZONE</c>, and a primary zone-name expression, then
    /// leaves the cursor on the last consumed token of the zone-name (so the
    /// caller's surrounding loop can advance via <c>GetNextOptional</c>).
    /// </summary>
    public static AtTimeZone ParsePostfix(Expression source, ParserContext context)
    {
        // Cursor is on AT.
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.At })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Time })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Zone })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var zoneNameExpression = ParseTimeZonePrimary(context);
        return new AtTimeZone(source, zoneNameExpression);
    }

    /// <summary>
    /// Parses the zone-name slot. Limited to primary expressions because
    /// SQL Server's AT TIME ZONE precedence is tighter than binary <c>+</c>;
    /// supporting full expressions here would mis-bind <c>expr AT TIME ZONE
    /// 'X' + 'Y'</c>. For full expressions, callers wrap in parentheses.
    /// </summary>
    private static Expression ParseTimeZonePrimary(ParserContext context) => context.Token switch
    {
        Literal lit => new Value(lit.Value),
        Numeric num => new Value(num.Value),
        ReservedKeyword { Keyword: Keyword.Null } => new Value(),
        AtPrefixedString atVar => new VariableReference(atVar, context),
        Name name => new Reference(name),
        Operator { Character: '(' } => ParseParenthesizedZone(context),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    private static Expression ParseParenthesizedZone(ParserContext context)
    {
        context.MoveNextRequired();
        var inner = Expression.Parse(context);
        return context.Token is not Operator { Character: ')' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : inner;
    }

    internal override string DebugDisplay() =>
        $"{this.source.DebugDisplay()} AT TIME ZONE {this.zoneNameExpression.DebugDisplay()}";
}
