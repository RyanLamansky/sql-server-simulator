using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>PARSE(string AS type [USING culture])</c> and <c>TRY_PARSE(...)</c>:
/// culture-aware string-to-typed conversion. Sibling of <see cref="Cast"/>
/// but routed through .NET <see cref="Convert.ChangeType(object?, Type, IFormatProvider?)"/>
/// with the optional culture so locale-specific decimal/thousands separators
/// and date formats work. <c>TRY_PARSE</c> returns NULL on parse failure
/// instead of raising Msg 9819.
/// </summary>
/// <remarks>
/// Grammar parsed inline rather than via <see cref="Cast"/> because the
/// optional <c>USING 'culture'</c> trailer needs to be consumed before
/// the closing paren. Target types match the SQL Server <c>PARSE</c>
/// surface — integer / decimal / float / date / time / datetime
/// families — and fall back to <c>CoerceTo</c> for anything else.
/// </remarks>
internal sealed class ParseFunction : Expression
{
    private readonly bool tryMode;
    private readonly Expression source;
    private readonly SqlType targetType;
    private readonly string? culture;

    public ParseFunction(ParserContext context, bool tryMode)
    {
        this.tryMode = tryMode;
        this.source = Parse(context);
        if (context.Token is not Tokens.ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var typeName = context.GetNextRequired<Tokens.Name>();
        (this.targetType, _) = Cast.ParseTargetTypeSpec(context, typeName);
        // Optional USING 'culture'
        if (context.Token is Tokens.UnquotedString { ContextualKeyword: ContextualKeyword.Using })
        {
            context.MoveNextRequired();
            if (context.Token is not Tokens.Literal lit)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            this.culture = lit.Value.CoerceTo(SqlType.NVarchar).AsString;
            context.MoveNextRequired();
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var src = this.source.Run(runtime);
        if (src.IsNull)
            return SqlValue.Null(this.targetType);
        var input = src.CoerceTo(SqlType.NVarchar).AsString;
        var cultureInfo = this.culture is null
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(this.culture);
        try
        {
            return ParseInto(input, this.targetType, cultureInfo);
        }
        catch
        {
            // Real names the target's family, and an exact-numeric target is
            // "numeric" however the statement spelled it.
            return this.tryMode
                ? SqlValue.Null(this.targetType)
                : throw SimulatedSqlException.ParseConversionFailed(
                    input,
                    this.targetType is DecimalSqlType ? "numeric" : this.targetType.SqlServerName ?? this.targetType.ToString()!,
                    this.culture ?? "");
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.targetType;

    internal override string DebugDisplay() => $"{(this.tryMode ? "TRY_PARSE" : "PARSE")}({this.source.DebugDisplay()} AS {this.targetType.SqlServerName})";

    private static SqlValue ParseInto(string input, SqlType target, CultureInfo culture)
    {
        if (target == SqlType.Int32) return SqlValue.FromInt32(int.Parse(input, NumberStyles.Number, culture));
        if (target == SqlType.BigInt) return SqlValue.FromInt64(long.Parse(input, NumberStyles.Number, culture));
        if (target == SqlType.SmallInt) return SqlValue.FromInt16(short.Parse(input, NumberStyles.Number, culture));
        if (target == SqlType.TinyInt) return SqlValue.FromByte(byte.Parse(input, NumberStyles.Number, culture));
        if (target == SqlType.Float) return SqlValue.FromDouble(double.Parse(input, NumberStyles.Float | NumberStyles.AllowThousands, culture));
        if (target == SqlType.Real) return SqlValue.FromSingle(float.Parse(input, NumberStyles.Float | NumberStyles.AllowThousands, culture));
        if (target is DecimalSqlType d) return SqlValue.FromDecimal(d, ParseExactNumeric(input, d, culture));
        if (target == SqlType.Money || target == SqlType.SmallMoney) return SqlValue.FromMoney(target, decimal.Parse(input, NumberStyles.Currency | NumberStyles.Number, culture));
        if (target == SqlType.Date) return SqlValue.FromDate(DateOnly.Parse(input, culture));
        if (target == SqlType.DateTime) return SqlValue.FromDateTime(DateTime.Parse(input, culture));
        if (target == SqlType.SmallDateTime) return SqlValue.FromSmallDateTime(DateTime.Parse(input, culture));
        if (target is DateTime2SqlType) return SqlValue.FromDateTime2(target, DateTime.Parse(input, culture));
        if (target is TimeSqlType) return SqlValue.FromTime(target, TimeSpan.Parse(input, culture));
        if (target is DateTimeOffsetSqlType) return SqlValue.FromDateTimeOffset(target, DateTimeOffset.Parse(input, culture));
        // Fall back to CAST's path for types PARSE doesn't add a culture-aware
        // route for (strings, binaries, etc.)
        return SqlValue.FromNVarchar(input).CoerceTo(target);
    }

    /// <summary>
    /// The exact-numeric target, at the full 38 digits real reads there
    /// (<c>PARSE('12345678901234567890123456789012345678' AS decimal(38, 0))</c>
    /// answers). The culture's separators come off and the digits go through
    /// <see cref="Decimal38"/>, with .NET's own parse kept as the grammar gate
    /// for anything a <see cref="decimal"/> could have held — so a rejection
    /// there is the culture refusing the text rather than the value being too
    /// wide. Excess fractional digits round, as they do for <c>CAST</c>, but
    /// text carrying more than 38 digits is refused outright rather than
    /// rounded into range (<c>PARSE('1.0000000000000000000000000000000000000005'
    /// AS decimal(38, 2))</c> raises Msg 9819 where the <c>CAST</c> answers
    /// <c>1.00</c>).
    /// </summary>
    private static Decimal38 ParseExactNumeric(string input, DecimalSqlType target, CultureInfo culture)
    {
        var text = StripGrouping(input, culture.NumberFormat);
        if (NaturalPrecision(text) > Decimal38.MaxPrecision
            || Decimal38.TryParse(text, target.precision, target.scale, out var parsed) != Decimal38ParseOutcome.Success)
        {
            throw new FormatException($"'{input}' is not a {target}.");
        }

        return Decimal38.TryToDotNetDecimal(parsed, out _) && !decimal.TryParse(input, NumberStyles.Number, culture, out _)
            ? throw new FormatException($"'{input}' is not a number in {culture.Name}.")
            : parsed;
    }

    /// <summary>
    /// The digit count the text itself carries — its integer digits past any
    /// leading zeros plus every written fractional digit — which is what real
    /// measures against <c>numeric</c>'s 38-digit domain here.
    /// </summary>
    private static int NaturalPrecision(string text)
    {
        var digits = 0;
        var counting = false;
        foreach (var c in text)
        {
            if (c == '.')
            {
                counting = true;
            }
            else if (char.IsAsciiDigit(c) && (counting || c != '0'))
            {
                counting = true;
                digits++;
            }
        }

        return digits;
    }

    /// <summary>
    /// The culture's number text reduced to the digits-and-point form
    /// <see cref="Decimal38.TryParse"/> reads: group separators dropped, the
    /// decimal separator restated, and a leading or trailing sign moved to the
    /// front — the shape <see cref="NumberStyles.Number"/> accepts.
    /// </summary>
    private static string StripGrouping(string input, NumberFormatInfo info)
    {
        var text = input.Trim();
        var negative = text.StartsWith(info.NegativeSign, StringComparison.Ordinal)
            || text.EndsWith(info.NegativeSign, StringComparison.Ordinal);
        foreach (var token in new[] { info.NegativeSign, info.PositiveSign, info.NumberGroupSeparator })
            text = text.Replace(token, "", StringComparison.Ordinal);
        return (negative ? "-" : "") + text.Replace(info.NumberDecimalSeparator, ".", StringComparison.Ordinal).Trim();
    }
}
