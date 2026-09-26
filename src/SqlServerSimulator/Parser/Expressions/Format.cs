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
/// <item><description>NULL value → NULL output. A bare NULL format → Msg 8116; a typed NULL one formats as no format string would.</description></item>
/// <item><description>The format string and culture take a string and nothing else (Msg 8116, xml and the legacy LOB types included).</description></item>
/// <item><description>Culture defaults to <c>en-US</c>. A culture name Windows can't parse — NULL included, and whatever the value — is Msg 9818; one it parses but doesn't know (<c>'qq-QQ'</c>) formats as <c>en-US</c> (probed 2026-09-26 against SQL Server 2025).</description></item>
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
        // A bare NULL value has no type to format (Msg 8116, probed 2026-09-24).
        if (IsUntypedNullLiteral(this.value))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex: 1, "format");
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.format = Parse(context.MoveNextRequiredReturnSelf());
        if (IsUntypedNullLiteral(this.format))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex: 2, "format");
        if (context.Token is Tokens.Operator { Character: ',' })
            this.culture = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // The culture is judged before a NULL value answers NULL.
        var culture = WithWindowsDecimalDigits(this.culture is null ? CultureInfo.GetCultureInfo("en-US") : ResolveCulture(this.culture.Run(runtime)));
        var formatValue = this.format.Run(runtime);
        var valueValue = this.value.Run(runtime);
        RejectUnsupportedValueType(valueValue.Type);
        if (valueValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);

        var formatString = formatValue.IsNull ? null : formatValue.AsString;

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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The value's type is settled while compiling, so a refused one raises
        // even where the call is never evaluated (probed 2026-09-25 against
        // SQL Server 2025: COALESCE(1, FORMAT('x', 'N2')) is Msg 8116).
        RejectUnsupportedValueType(this.value.GetSqlType(batch, resolveColumnType));
        _ = StringScalars.RequireStringArgument(this.format, this.format.GetSqlType(batch, resolveColumnType), "format", 2, acceptsLegacyLob: false);
        if (this.culture is not null)
            _ = StringScalars.RequireStringArgument(this.culture, this.culture.GetSqlType(batch, resolveColumnType), "format", 3, acceptsLegacyLob: false);
        return SqlType.NVarchar;
    }

    /// <summary>
    /// SQL Server formats through Windows' culture data, which differs from
    /// ICU's in a few number-format cells: two default number and percent
    /// digits where ICU has three (probe-confirmed 2026-09-23 for en-US,
    /// de-DE, fr-FR, ja-JP and ar-SA: <c>FORMAT(1234.5, 'N')</c> is
    /// <c>1,234.50</c>), a no-break space (U+00A0) as the group separator
    /// where ICU has the narrow one (U+202F, fr-FR / fr-CH), the yen sign as
    /// U+00A5 where ICU has the fullwidth U+FFE5, and en-US's negative
    /// currency in parentheses (<c>($1,234.50)</c>) — the last three probed
    /// 2026-09-24 against SQL Server 2025.
    /// </summary>
    private static CultureInfo WithWindowsDecimalDigits(CultureInfo culture)
    {
        var format = culture.NumberFormat;
        var parenthesizedCurrency = culture.Name == "en-US" && format.CurrencyNegativePattern != 0;
        if (format.NumberDecimalDigits == 2 && format.PercentDecimalDigits == 2
            && format.NumberGroupSeparator != NarrowNoBreakSpace && format.CurrencyGroupSeparator != NarrowNoBreakSpace
            && format.PercentGroupSeparator != NarrowNoBreakSpace && format.CurrencySymbol != FullwidthYen
            && !parenthesizedCurrency)
        {
            return culture;
        }

        var adjusted = (CultureInfo)culture.Clone();
        var adjustedFormat = adjusted.NumberFormat;
        adjustedFormat.NumberDecimalDigits = 2;
        adjustedFormat.PercentDecimalDigits = 2;
        adjustedFormat.NumberGroupSeparator = adjustedFormat.NumberGroupSeparator.Replace(NarrowNoBreakSpace, "\u00A0", StringComparison.Ordinal);
        adjustedFormat.CurrencyGroupSeparator = adjustedFormat.CurrencyGroupSeparator.Replace(NarrowNoBreakSpace, "\u00A0", StringComparison.Ordinal);
        adjustedFormat.PercentGroupSeparator = adjustedFormat.PercentGroupSeparator.Replace(NarrowNoBreakSpace, "\u00A0", StringComparison.Ordinal);
        adjustedFormat.CurrencySymbol = adjustedFormat.CurrencySymbol.Replace(FullwidthYen, "\u00A5", StringComparison.Ordinal);
        if (parenthesizedCurrency)
            adjustedFormat.CurrencyNegativePattern = 0;
        return adjusted;
    }

    private const string NarrowNoBreakSpace = "\u202F";

    private const string FullwidthYen = "\uFFE5";

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
        var name = cultureValue.IsNull ? null : cultureValue.AsString;
        if (name is null || !IsWellFormedCultureName(name))
            throw SimulatedSqlException.CultureNotSupported(name ?? "NULL");
        // A private-use tag names no culture ICU knows, and asking for one
        // yields a culture whose number format is incomplete.
        if (name[1] is '-' or '_')
            return CultureInfo.GetCultureInfo("en-US");
        try
        {
            return CultureInfo.GetCultureInfo(name.Replace('_', '-'), predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US");
        }
    }

    /// <summary>
    /// Whether Windows parses <paramref name="name"/> as a culture name at
    /// all, known or not: a two- or three-letter language, then subtags of two
    /// letters (a region), three digits or four letters (a script), joined by
    /// <c>-</c> or <c>_</c>; or a private-use <c>x-</c> / <c>i-</c> tag. Read off
    /// real's answers (probed 2026-09-26 against SQL Server 2025: <c>qq-QQ</c>,
    /// <c>en_US</c>, <c>zh-Hans</c>, <c>x-y</c> pass; <c>x</c>, the empty string,
    /// <c>' en-US'</c>, <c>abc-DEFGH</c> are Msg 9818) rather than from a
    /// specification, so a tag outside those shapes may be judged otherwise.
    /// </summary>
    private static bool IsWellFormedCultureName(string name)
    {
        var parts = name.Split('-', '_');
        if (parts[0].Length == 1 && parts[0][0] is 'x' or 'X' or 'i' or 'I')
            return parts.Length > 1 && Array.TrueForAll(parts[1..], static part => part.Length is >= 1 and <= 8 && part.All(char.IsAsciiLetterOrDigit));
        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter))
            return false;
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var wellFormed = part.Length switch
            {
                2 or 4 => part.All(char.IsAsciiLetter),
                3 => part.All(char.IsAsciiDigit),
                _ => false,
            };
            if (!wellFormed)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Bridges <see cref="SqlValue"/> to the underlying CLR
    /// <see cref="IFormattable"/> so <c>ToString(format, culture)</c> drives
    /// the actual output. Integer types widen to <see cref="long"/> so a
    /// single switch arm handles all of tinyint/smallint/int/bigint, money
    /// flattens to <see cref="decimal"/>, and the various date/time families
    /// route to their CLR counterparts.
    /// </summary>
    private static string FormatValue(SqlValue v, string? format, CultureInfo culture) => v.Type switch
    {
        TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType => v.CoerceTo(SqlType.BigInt).AsInt64.ToString(format, culture),
        // A value a .NET decimal holds formats through .NET's own engine; a
        // wider one lays its digits out directly, which is what lets real's
        // full-38-digit rendering come back.
        DecimalSqlType when Decimal38.TryToDotNetDecimal(v.AsDecimal38, out var narrow) => narrow.ToString(format, culture),
        DecimalSqlType => WideNumericFormat.Render(v.AsDecimal38, format ?? "G", culture),
        MoneySqlType or SmallMoneySqlType => v.AsMoney.ToString(format, culture),
        FloatSqlType => WithoutNegativeZero(v.AsDouble).ToString(format, culture),
        RealSqlType => WithoutNegativeZero(v.AsSingle).ToString(format, culture),
        DateSqlType => v.AsDate.ToString(format, culture),
        DateTimeSqlType or SmallDateTimeSqlType => v.AsDateTime.ToString(format, culture),
        DateTime2SqlType => v.AsDateTime2.ToString(format, culture),
        DateTimeOffsetSqlType => v.AsDateTimeOffset.ToString(format, culture),
        TimeSqlType => v.AsTime.ToString(format, culture),
        _ => throw new NotSupportedException($"FORMAT for value type {v.Type} not modeled."),
    };

    /// <summary>
    /// Drops the sign of an IEEE 754 negative zero. FORMAT is the one string
    /// surface that doesn't report it: real renders <c>-CAST(0 AS float)</c> as
    /// <c>-0</c> through CAST / CONCAT / STR / CONVERT / PRINT, yet
    /// <c>FORMAT(…, 'G')</c> — and every other format string probed, including
    /// <c>N2</c> / <c>F3</c> / <c>E2</c> / <c>C</c> / <c>P</c> — gives an
    /// unsigned zero, the .NET Framework formatting behavior its CLR
    /// implementation carries. .NET Core renders the sign, so it's folded here.
    /// </summary>
    private static double WithoutNegativeZero(double value) => value == 0 ? 0d : value;

    /// <inheritdoc cref="WithoutNegativeZero(double)"/>
    private static float WithoutNegativeZero(float value) => value == 0 ? 0f : value;

    /// <summary>
    /// Eagerly raises Msg 8116 for value types SQL Server's FORMAT rejects.
    /// Strings and binaries reject; bit and sql_variant also reject (probe-confirmed).
    /// Datetime, time, all numerics accept.
    /// </summary>
    private static void RejectUnsupportedValueType(SqlType type)
    {
        if (SqlType.IsStringCategory(type) || type == SqlType.Bit
            || type is BinarySqlType or VarbinarySqlType
            || type == SqlType.UniqueIdentifier
            || type == SqlType.RowVersion
            || type is SqlVariantSqlType)
        {
            throw SimulatedSqlException.InvalidArgumentDataType(type.SqlServerName, argumentIndex: 1, "format");
        }
    }

    internal override string DebugDisplay() => this.culture is null
        ? $"FORMAT({this.value.DebugDisplay()}, {this.format.DebugDisplay()})"
        : $"FORMAT({this.value.DebugDisplay()}, {this.format.DebugDisplay()}, {this.culture.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.value).Child(this.format).Child(this.culture);
}
