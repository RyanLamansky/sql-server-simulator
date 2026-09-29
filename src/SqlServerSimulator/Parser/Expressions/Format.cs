using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FORMAT(value, format [, culture])</c>: CLR-backed string formatter.
/// Returns <c>nvarchar(4000)</c>. The implementation routes through
/// <see cref="IFormattable"/>'s <c>ToString(format, culture)</c> on the
/// underlying CLR value, matching SQL Server's documented CLR-passthrough
/// shape, over real's culture data (<see cref="FormatCulture"/>) and with the
/// .NET Framework's rules where they differ (<c>Format.Framework.cs</c>).
/// </summary>
/// <remarks>
/// Probe-confirmed behavior (SQL Server 2025):
/// <list type="bullet">
/// <item><description>Accepted value types: numeric (<c>int</c>, <c>bigint</c>, <c>decimal</c>, <c>float</c>, <c>real</c>, <c>money</c>, <c>smallmoney</c>) and date/time (<c>date</c>, <c>datetime</c>, <c>smalldatetime</c>, <c>datetime2</c>, <c>datetimeoffset</c>, <c>time</c>).</description></item>
/// <item><description>Rejected types raise <strong>Msg 8116</strong>: <c>varchar</c>, <c>nvarchar</c>, <c>char</c>, <c>nchar</c>, <c>bit</c>, <c>binary</c>, etc.</description></item>
/// <item><description>NULL value → NULL output. A bare NULL format → Msg 8116; a typed NULL one formats as no format string would.</description></item>
/// <item><description>The format string and culture take a string and nothing else (Msg 8116, xml and the legacy LOB types included).</description></item>
/// <item><description>Culture defaults to <c>en-US</c>. A culture name Windows can't parse — NULL included, and whatever the value — is Msg 9818; one it parses but doesn't know (<c>'qq-QQ'</c>) takes what Windows synthesizes for it (probed 2026-09-29 against SQL Server 2025).</description></item>
/// <item><description>Unrecognized .NET format token: passthrough (probe: <c>FORMAT(1234, 'qq qq')</c> → <c>'qq qq'</c>); .NET <see cref="FormatException"/> (e.g. <c>FORMAT(decimal, 'D5')</c>) → NULL.</description></item>
/// </list>
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/format-transact-sql</remarks>
internal sealed partial class Format : Expression
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
        var culture = this.culture is null ? FormatCulture.Default : ResolveCulture(this.culture.Run(runtime));
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
        catch (ArgumentOutOfRangeException)
        {
            // A date outside the culture calendar's range (0001-01-01 under
            // ar-SA's Umm al-Qura) answers NULL as a FormatException does.
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
    /// Picks the culture data for the formatter. A name Windows can't parse —
    /// NULL included — is Msg 9818; any other resolves through
    /// <see cref="FormatCulture.Resolve"/>, a known name to its own data and an
    /// unknown one to what Windows synthesizes for it.
    /// </summary>
    private static FormatCulture ResolveCulture(SqlValue cultureValue)
    {
        var name = cultureValue.IsNull ? null : cultureValue.AsString;
        return name is null || !(IsWellFormedCultureName(name) || FormatCulture.IsKnown(name))
            ? throw SimulatedSqlException.CultureNotSupported(name ?? "NULL")
            : FormatCulture.Resolve(name);
    }

    /// <summary>
    /// Whether Windows parses <paramref name="name"/> as a culture name at
    /// all, known or not: a two- or three-letter language, an optional
    /// four-letter script, then either nothing, a region (two letters or three
    /// digits) followed by any subtags of one to eight letters or digits, or a
    /// singleton followed by at least one more; or a private-use <c>x-</c> /
    /// <c>i-</c> tag; <c>-</c> and <c>_</c> both separate. Read off real's
    /// answers (probed 2026-09-29 against SQL Server 2025: <c>en-US-POSIX</c>,
    /// <c>en_US</c>, <c>zh-Hans</c>, <c>en-u-nu-thai</c>, <c>ab-cd-ef-gh</c>,
    /// <c>x-y</c> pass; <c>en-POSIX</c>, <c>en-aaa</c>, <c>en-A</c>,
    /// <c>en-US-ABCDEFGHI</c>, <c>abc-DEFGH</c>, <c>x</c>, the empty string and
    /// <c>' en-US'</c> are Msg 9818). A name Windows knows outright passes
    /// whatever its shape (<c>zh-CHS</c>, <c>qps-plocm</c>).
    /// </summary>
    private static bool IsWellFormedCultureName(string name)
    {
        var parts = name.Split('-', '_');
        if (Array.Exists(parts, static part => part.Length is 0 or > 8 || !part.All(char.IsAsciiLetterOrDigit)))
            return false;
        if (parts[0].Length == 1)
            return parts[0][0] is 'x' or 'X' or 'i' or 'I' && parts.Length > 1;
        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter))
            return false;
        var i = 1;
        if (i < parts.Length && parts[i].Length == 4 && parts[i].All(char.IsAsciiLetter))
            i++;
        if (i == parts.Length)
            return true;
        var next = parts[i];
        // A region opens the tail to any subtag; a singleton needs one after it.
        return (next.Length == 2 && next.All(char.IsAsciiLetter)) || (next.Length == 3 && next.All(char.IsAsciiDigit))
            || (next.Length == 1 && i + 1 < parts.Length);
    }

    /// <summary>
    /// Bridges <see cref="SqlValue"/> to the CLR value SQL Server's .NET
    /// Framework formatter would have seen, then formats it the way that
    /// runtime did where it differs from the .NET this simulator runs on (see
    /// <c>Format.Framework.cs</c>). Each integer type keeps its own width, so
    /// <c>'X'</c> of a negative <c>int</c> writes eight digits and of a
    /// <c>smallint</c> four; a <c>date</c> is a midnight <see cref="DateTime"/>,
    /// which is what lets time specifiers answer for it.
    /// </summary>
    private static string FormatValue(SqlValue v, string? format, FormatCulture formatCulture)
    {
        var culture = formatCulture.Culture;
        return v.Type switch
        {
            TinyIntSqlType => FormatInteger((byte)v.CoerceTo(SqlType.BigInt).AsInt64, format, culture),
            SmallIntSqlType => FormatInteger((short)v.CoerceTo(SqlType.BigInt).AsInt64, format, culture),
            Int32SqlType => FormatInteger((int)v.CoerceTo(SqlType.BigInt).AsInt64, format, culture),
            BigIntSqlType => FormatInteger(v.AsInt64, format, culture),
            // A value a .NET decimal holds formats through .NET's own engine; a
            // wider one lays its digits out directly, which is what lets real's
            // full-38-digit rendering come back.
            DecimalSqlType when Decimal38.TryToDotNetDecimal(v.AsDecimal38, out var narrow) => FormatDecimal(narrow, format, culture),
            DecimalSqlType => WideNumericFormat.Render(v.AsDecimal38, format ?? "G", culture),
            MoneySqlType or SmallMoneySqlType => FormatDecimal(v.AsMoney, format, culture),
            FloatSqlType => FormatFloating(WithoutNegativeZero(v.AsDouble), single: false, format, culture),
            RealSqlType => FormatFloating(WithoutNegativeZero(v.AsSingle), single: true, format, culture),
            DateSqlType => FormatDateTime(v.AsDate.ToDateTime(TimeOnly.MinValue), format, formatCulture),
            // A datetime crosses to the CLR as SqlDateTime does, its 1/300-second
            // ticks rounded to the millisecond (.997 reads .9970000 under 'O').
            DateTimeSqlType => FormatDateTime(new DateTime((v.AsDateTime.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond), format, formatCulture),
            SmallDateTimeSqlType => FormatDateTime(v.AsSmallDateTime, format, formatCulture),
            DateTime2SqlType => FormatDateTime(v.AsDateTime2, format, formatCulture),
            DateTimeOffsetSqlType => v.AsDateTimeOffset.ToString(WithEra(format, formatCulture, v.AsDateTimeOffset.DateTime), culture),
            TimeSqlType => FormatTime(v.AsTime, format, culture),
            _ => throw new NotSupportedException($"FORMAT for value type {v.Type} not modeled."),
        };
    }

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
            || type is SqlVariantSqlType or VectorSqlType)
        {
            throw SimulatedSqlException.InvalidArgumentDataType(type.SqlServerName, argumentIndex: 1, "format");
        }
    }

    internal override string DebugDisplay() => this.culture is null
        ? $"FORMAT({this.value.DebugDisplay()}, {this.format.DebugDisplay()})"
        : $"FORMAT({this.value.DebugDisplay()}, {this.format.DebugDisplay()}, {this.culture.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.value).Child(this.format).Child(this.culture);
}
