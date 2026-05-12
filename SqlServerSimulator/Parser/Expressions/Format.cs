using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FORMAT(value, format [, culture])</c>: CLR-backed string formatter.
/// Returns <c>nvarchar(4000)</c>. The implementation routes through
/// <see cref="IFormattable"/>'s <c>ToString(format, culture)</c> on the
/// underlying CLR value, matching SQL Server's documented CLR-passthrough
/// shape.
/// </summary>
/// <remarks>
/// Probe-confirmed behavior (SQL Server 2025):
/// <list type="bullet">
/// <item><description>Accepted value types: numeric (<c>int</c>, <c>bigint</c>, <c>decimal</c>, <c>float</c>, <c>real</c>, <c>money</c>, <c>smallmoney</c>) and date/time (<c>date</c>, <c>datetime</c>, <c>smalldatetime</c>, <c>datetime2</c>, <c>datetimeoffset</c>, <c>time</c>).</description></item>
/// <item><description>Rejected types raise <strong>Msg 8116</strong>: <c>varchar</c>, <c>nvarchar</c>, <c>char</c>, <c>nchar</c>, <c>bit</c>, <c>binary</c>, etc.</description></item>
/// <item><description>NULL value → NULL output. NULL format → Msg 8116.</description></item>
/// <item><description>Culture defaults to <c>en-US</c>; an invalid culture also falls back to <c>en-US</c> (probe: <c>'qq-QQ'</c> didn't error).</description></item>
/// <item><description>Unrecognized .NET format token: passthrough (probe: <c>FORMAT(1234, 'qq qq')</c> → <c>'qq qq'</c>); .NET <see cref="FormatException"/> (e.g. <c>FORMAT(decimal, 'D5')</c>) → NULL.</description></item>
/// </list>
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/format-transact-sql</remarks>
internal sealed class Format : Expression
{
    private readonly Expression value;
    private readonly Expression format;
    private readonly Expression? culture;

    public Format(ParserContext context)
    {
        this.value = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.format = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.culture = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Argument-type validation runs eagerly on the format slot — probe
        // shows that even a NULL value paired with an invalid format type
        // still surfaces the value-side Msg 8116 first (probed in earlier
        // bundles via the CONVERT style validator's analogous gate). For
        // FORMAT specifically, NULL format → Msg 8116 fires regardless of
        // the value side.
        var formatValue = this.format.Run(runtime);
        if (formatValue.IsNull)
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex: 2, "format");

        var valueValue = this.value.Run(runtime);
        RejectUnsupportedValueType(valueValue.Type);
        if (valueValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);

        var culture = this.culture is null ? CultureInfo.GetCultureInfo("en-US") : ResolveCulture(this.culture.Run(runtime));
        var formatString = formatValue.AsString;

        try
        {
            var formatted = FormatValue(valueValue, formatString, culture);
            return SqlValue.FromNVarchar(formatted);
        }
        catch (FormatException)
        {
            return SqlValue.Null(SqlType.NVarchar);
        }
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    /// <summary>
    /// Picks the CLR culture for the formatter. A non-string argument
    /// or an unrecognized culture name silently falls back to <c>en-US</c>
    /// — probe-confirmed (<c>FORMAT(1234, 'N0', 'qq-QQ')</c> formatted as
    /// en-US-style <c>"1,234"</c> rather than raising). The <c>predefinedOnly:
    /// true</c> overload is load-bearing for cross-platform determinism: on
    /// some ICU builds (notably GitHub Actions Linux runners), the default
    /// <see cref="CultureInfo.GetCultureInfo(string)"/> silently synthesizes
    /// a culture from any well-formed BCP-47 tag rather than throwing,
    /// producing an invariant-like formatter (no thousands separator) instead
    /// of the expected fallback. <c>predefinedOnly</c> rejects synthesized
    /// cultures and forces the catch block to fire.
    /// </summary>
    private static CultureInfo ResolveCulture(SqlValue cultureValue)
    {
        if (cultureValue.IsNull || !SqlType.IsStringCategory(cultureValue.Type))
            return CultureInfo.GetCultureInfo("en-US");
        try
        {
            return CultureInfo.GetCultureInfo(cultureValue.AsString, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US");
        }
    }

    /// <summary>
    /// Bridges <see cref="SqlValue"/> to the underlying CLR
    /// <see cref="IFormattable"/> so <c>ToString(format, culture)</c> drives
    /// the actual output. Integer types widen to <see cref="long"/> so a
    /// single switch arm handles all of tinyint/smallint/int/bigint, money
    /// flattens to <see cref="decimal"/>, and the various date/time families
    /// route to their CLR counterparts.
    /// </summary>
    private static string FormatValue(SqlValue v, string format, CultureInfo culture) => v.Type switch
    {
        TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType => v.CoerceTo(SqlType.BigInt).AsInt64.ToString(format, culture),
        DecimalSqlType => v.AsDecimal.ToString(format, culture),
        MoneySqlType or SmallMoneySqlType => v.AsDecimal.ToString(format, culture),
        FloatSqlType => v.AsDouble.ToString(format, culture),
        RealSqlType => v.AsSingle.ToString(format, culture),
        DateSqlType => v.AsDate.ToString(format, culture),
        DateTimeSqlType or SmallDateTimeSqlType => v.AsDateTime.ToString(format, culture),
        DateTime2SqlType => v.AsDateTime2.ToString(format, culture),
        DateTimeOffsetSqlType => v.AsDateTimeOffset.ToString(format, culture),
        TimeSqlType => v.AsTime.ToString(format, culture),
        _ => throw new NotSupportedException($"FORMAT for value type {v.Type} not modeled."),
    };

    /// <summary>
    /// Eagerly raises Msg 8116 for value types SQL Server's FORMAT rejects.
    /// Strings and binaries reject; bit also rejects (probe-confirmed).
    /// Datetime, time, all numerics accept.
    /// </summary>
    private static void RejectUnsupportedValueType(SqlType type)
    {
        if (SqlType.IsStringCategory(type) || type == SqlType.Bit
            || type is BinarySqlType or VarbinarySqlType
            || type == SqlType.UniqueIdentifier
            || type == SqlType.RowVersion)
        {
            throw SimulatedSqlException.InvalidArgumentDataType(type.SqlServerName, argumentIndex: 1, "format");
        }
    }

    internal override string DebugDisplay() => this.culture is null
        ? $"FORMAT({this.value.DebugDisplay()}, {this.format.DebugDisplay()})"
        : $"FORMAT({this.value.DebugDisplay()}, {this.format.DebugDisplay()}, {this.culture.DebugDisplay()})";
}
