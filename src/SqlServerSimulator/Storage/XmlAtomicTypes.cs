using System.Globalization;
using System.Xml;
using System.Xml.Schema;

namespace SqlServerSimulator.Storage;

/// <summary>
/// An atomic item whose XQuery type is known and whose text is that type's
/// canonical form — what a constructor function (<c>xs:integer("-007")</c>),
/// a <c>cast as</c>, a <c>sql:variable</c> / <c>sql:column</c> accessor or an
/// <c>xs:double</c> computation produces. The evaluator's untyped numbers stay
/// plain <see cref="double"/> values; this carries the ones whose rendering
/// real derives from the type (<c>1.234568E6</c> for a double, the exact
/// digits of an <c>xs:long</c>).
/// </summary>
internal sealed class XmlTypedAtomic(string typeName, string text, double number, XmlStaticKind kind)
{
    /// <summary>The <c>xs:</c>-prefixed type name real reports for the value.</summary>
    public readonly string TypeName = typeName;

    /// <summary>The canonical text, which is what serialization and <c>.value()</c> read.</summary>
    public readonly string Text = text;

    /// <summary>The numeric value, NaN for a type that isn't numeric.</summary>
    public readonly double Number = number;

    /// <summary>How the item compares and what a predicate over it means.</summary>
    public readonly XmlStaticKind Kind = kind;

    public override string ToString() => this.Text;
}

/// <summary>
/// The XQuery atomic type system the evaluator needs past plain numbers and
/// strings: resolving <c>xs:</c> type names, casting between them, rendering
/// numbers in the type real gives them, and mapping a SQL value onto the
/// XQuery type <c>sql:variable</c> / <c>sql:column</c> report it as.
/// </summary>
/// <remarks>
/// Validation and the canonical rendering ride .NET's built-in XSD datatypes
/// and <see cref="XsdCanonical"/>, the same pair a typed <c>xml</c> write
/// uses, so <c>xs:decimal("1.50")</c> renders <c>1.5</c> exactly as a typed
/// column stores it (probed 2026-09-28 against SQL Server 2025).
/// </remarks>
internal static class XmlAtomicTypes
{
    /// <summary>The namespace the predeclared <c>xs</c> prefix binds.</summary>
    public const string SchemaNamespace = "http://www.w3.org/2001/XMLSchema";

    /// <summary>The namespace the predeclared <c>xdt</c> prefix binds.</summary>
    public const string DataTypesNamespace = "http://www.w3.org/2004/07/xpath-datatypes";

    /// <summary>The namespace the predeclared <c>sql</c> prefix binds.</summary>
    public const string SqlNamespace = "urn:schemas-microsoft-com:xml-sql";

    /// <summary>
    /// The built-in simple type <c>xs:<paramref name="local"/></c> names, or null
    /// when XSD has none by that name. <c>anyType</c> / <c>anySimpleType</c> and
    /// the list types aren't atomic, so they resolve to null too.
    /// </summary>
    public static XmlSchemaSimpleType? Resolve(string local)
    {
        if (local is "anySimpleType" or "anyAtomicType" or "IDREFS" or "NMTOKENS" or "ENTITIES")
            return null;
        return XmlSchemaType.GetBuiltInSimpleType(new XmlQualifiedName(local, SchemaNamespace));
    }

    /// <summary>The static kind an item of <paramref name="type"/> carries.</summary>
    public static XmlStaticKind KindOf(XmlSchemaSimpleType type) =>
        type.Datatype?.TypeCode switch
        {
            XmlTypeCode.Boolean => XmlStaticKind.Boolean,
            _ when IsNumeric(type) => XmlStaticKind.Number,
            _ => XmlStaticKind.String,
        };

    /// <summary>Whether <paramref name="type"/> is one of the numeric primitives or derives from one.</summary>
    public static bool IsNumeric(XmlSchemaSimpleType type) => NumericRank(type.Datatype?.TypeCode ?? XmlTypeCode.String) > 0;

    /// <summary>
    /// The promotion rank of a numeric type code — integer below decimal below
    /// float below double — and 0 for anything that isn't numeric.
    /// </summary>
    public static int NumericRank(XmlTypeCode code) => code switch
    {
        XmlTypeCode.Double => 4,
        XmlTypeCode.Float => 3,
        XmlTypeCode.Decimal => 2,
        XmlTypeCode.Integer
            or XmlTypeCode.NonPositiveInteger
            or XmlTypeCode.NegativeInteger
            or XmlTypeCode.Long
            or XmlTypeCode.Int
            or XmlTypeCode.Short
            or XmlTypeCode.Byte
            or XmlTypeCode.NonNegativeInteger
            or XmlTypeCode.UnsignedLong
            or XmlTypeCode.UnsignedInt
            or XmlTypeCode.UnsignedShort
            or XmlTypeCode.UnsignedByte
            or XmlTypeCode.PositiveInteger => 1,
        _ => 0,
    };

    /// <summary>
    /// The promotion rank of a static type name as the compiled tree carries it:
    /// an untyped operand is promoted to <c>xs:double</c> by arithmetic, and a
    /// name that isn't numeric answers 0.
    /// </summary>
    public static int NumericRank(string typeName) => typeName switch
    {
        "xdt:untypedAtomic" => 4,
        _ when typeName.StartsWith("xs:", StringComparison.Ordinal) && Resolve(typeName[3..]) is { Datatype: { } datatype } => NumericRank(datatype.TypeCode),
        _ => 0,
    };

    /// <summary>The type name a promotion rank computes in.</summary>
    public static string RankTypeName(int rank) => rank switch
    {
        2 => "xs:decimal",
        3 => "xs:float",
        4 => "xs:double",
        _ => "xs:integer",
    };

    /// <summary>
    /// A computed number carried in <paramref name="typeName"/>: an approximate
    /// type keeps its value in a <see cref="XmlTypedAtomic"/> rendered by real's
    /// <c>fn:string</c> rule, while an exact one stays a plain
    /// <see cref="double"/>.
    /// </summary>
    public static object Number(double value, string typeName) => typeName switch
    {
        "xs:double" => new XmlTypedAtomic(typeName, RenderApproximate(value, 15), value, XmlStaticKind.Number),
        "xs:float" => new XmlTypedAtomic(typeName, RenderApproximate(value, 7), value, XmlStaticKind.Number),
        _ => value,
    };

    /// <summary>
    /// Renders an exact (integer or decimal) number the way real writes one:
    /// plain digits with no exponent and no trailing fractional zeros. The
    /// evaluator computes in <see cref="double"/>, so the value is first rounded
    /// to the 15 significant digits a double carries reliably — which is what
    /// keeps <c>0.1 + 0.2</c> rendering <c>0.3</c>.
    /// </summary>
    public static string RenderExact(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsInfinity(value))
            return value > 0 ? "INF" : "-INF";
        if (Math.Abs(value) < 7.9e27)
        {
            var exact = (decimal)double.Parse(value.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var text = exact.ToString(CultureInfo.InvariantCulture);
            if (text.Contains('.', StringComparison.Ordinal))
                text = text.TrimEnd('0').TrimEnd('.');
            return text == "-0" ? "0" : text;
        }
        return value.ToString("F0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Cuts <paramref name="value"/> to <paramref name="digits"/> fractional
    /// digits, working on the 15 significant digits a double carries reliably
    /// so that a value like 0.3 isn't cut to 0.299999.
    /// </summary>
    public static double TruncateDigits(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) * Math.Pow(10, digits) >= 7.9e27)
            return value;
        var exact = (decimal)double.Parse(value.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var scale = 1m;
        for (var i = 0; i < digits; i++)
            scale *= 10;
        return (double)(decimal.Truncate(exact * scale) / scale);
    }

    /// <summary>Renders an approximate number at <paramref name="significantDigits"/> by real's <c>fn:string</c> rule.</summary>
    public static string RenderApproximate(double value, int significantDigits) =>
        double.IsNaN(value) ? "NaN"
        : double.IsInfinity(value) ? (value > 0 ? "INF" : "-INF")
        : XsdCanonical.RenderApproximate(value.ToString("R", CultureInfo.InvariantCulture), significantDigits);

    /// <summary>
    /// Casts an atomized item to <paramref name="target"/>, answering the typed
    /// result or null when the value doesn't convert — which the caller turns
    /// into Msg 9319 for a literal and the empty sequence for a value read from
    /// the instance, real's own split.
    /// </summary>
    public static object? TryCast(object item, XmlSchemaSimpleType target, string targetName)
    {
        var code = target.Datatype!.TypeCode;
        return item switch
        {
            bool boolean when code == XmlTypeCode.Boolean => boolean,
            bool boolean => TryCastText(NumericRank(code) > 0 ? (boolean ? "1" : "0") : (boolean ? "true" : "false"), target, targetName),
            double number => CastNumber(number, RenderExact(number), target, targetName),
            XmlTypedAtomic { Kind: XmlStaticKind.Number } typed => CastNumber(typed.Number, typed.Text, target, targetName),
            _ => TryCastText(XmlQueryValues.StringValue(item), target, targetName),
        };
    }

    private static object? CastNumber(double number, string text, XmlSchemaSimpleType target, string targetName)
    {
        var code = target.Datatype!.TypeCode;
        if (code == XmlTypeCode.Boolean)
            return number != 0 && !double.IsNaN(number);
        if (code is XmlTypeCode.Double or XmlTypeCode.Float)
            return Number(number, code == XmlTypeCode.Double ? "xs:double" : "xs:float");
        if (NumericRank(code) == 1)
        {
            // A cast to an integer type truncates the fraction rather than
            // refusing it: xs:integer(1.7) is 1 (probe-confirmed).
            var point = text.IndexOf('.', StringComparison.Ordinal);
            if (text.Contains('E', StringComparison.Ordinal))
                text = RenderExact(Math.Truncate(number));
            else if (point >= 0)
                text = text[..point];
            if (text is "-0" or "")
                text = "0";
        }
        else if (code == XmlTypeCode.Decimal && text.Contains('E', StringComparison.Ordinal))
        {
            text = RenderExact(number);
        }
        return TryCastText(text, target, targetName);
    }

    /// <summary>Casts a string value through the target's lexical space.</summary>
    public static object? TryCastText(string text, XmlSchemaSimpleType target, string targetName)
    {
        var datatype = target.Datatype!;
        if (datatype.TypeCode == XmlTypeCode.String)
            return new XmlTypedAtomic(targetName, text, double.NaN, XmlStaticKind.String);

        var normalized = XsdCanonical.PreParse(datatype, XsdCanonical.ApplyWhitespaceFacet(datatype, text));
        object parsed;
        try
        {
            parsed = datatype.ParseValue(normalized, null, null);
        }
        catch (Exception e) when (e is XmlSchemaException or FormatException or OverflowException or ArgumentException or InvalidCastException)
        {
            return null;
        }

        // Real's xs:double and xs:float take INF and -INF as written but
        // refuse NaN and a number past their range (probed 2026-10-02 against
        // SQL Server 2025), where .NET's reader would round to infinity.
        if (datatype.TypeCode is XmlTypeCode.Double or XmlTypeCode.Float
            && Convert.ToDouble(parsed, CultureInfo.InvariantCulture) is var approximate
            && (double.IsNaN(approximate) || (double.IsInfinity(approximate) && normalized is not ("INF" or "-INF"))))
        {
            return null;
        }

        var canonical = XsdCanonical.Render(target, normalized);
        return KindOf(target) switch
        {
            XmlStaticKind.Boolean => (bool)parsed,
            XmlStaticKind.Number => new XmlTypedAtomic(
                targetName,
                canonical,
                double.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
                    : canonical switch { "-INF" => double.NegativeInfinity, "INF" => double.PositiveInfinity, _ => double.NaN },
                XmlStaticKind.Number),
            _ => new XmlTypedAtomic(targetName, canonical, double.NaN, XmlStaticKind.String),
        };
    }

    /// <summary>
    /// Whether an item of static type <paramref name="sourceName"/> is an
    /// instance of the atomic type <paramref name="target"/> — which is the
    /// source type itself or one it derives from. An untyped value is an
    /// instance of <c>xdt:untypedAtomic</c> and nothing else.
    /// </summary>
    public static bool IsSubtype(string sourceName, XmlSchemaSimpleType? target, bool targetIsUntyped)
    {
        if (sourceName == "xdt:untypedAtomic")
            return targetIsUntyped;
        if (targetIsUntyped || target is null || !sourceName.StartsWith("xs:", StringComparison.Ordinal))
            return false;
        return Resolve(sourceName[3..]) is { } source
            && (source == target || XmlSchemaType.IsDerivedFrom(source, target, XmlSchemaDerivationMethod.Empty));
    }

    /// <summary>
    /// The XQuery type a SQL type maps to under <c>sql:variable</c> /
    /// <c>sql:column</c> — <c>int</c> is <c>xs:int</c>, the character types
    /// <c>xs:string</c>, the date-time family <c>xs:dateTime</c> — or null for a
    /// type real has no mapping for.
    /// </summary>
    public static string? SqlTypeName(SqlType type) => type switch
    {
        BigIntSqlType => "xs:long",
        BitSqlType => "xs:boolean",
        DecimalSqlType or MoneySqlType or SmallMoneySqlType => "xs:decimal",
        FloatSqlType => "xs:double",
        Int32SqlType => "xs:int",
        RealSqlType => "xs:float",
        SmallIntSqlType => "xs:short",
        TinyIntSqlType => "xs:unsignedByte",
        DateSqlType => "xs:date",
        TimeSqlType => "xs:time",
        DateTimeSqlType or SmallDateTimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType => "xs:dateTime",
        BinarySqlType or VarbinarySqlType or RowVersionSqlType => "xs:base64Binary",
        VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType or SystemNameSqlType or UniqueIdentifierSqlType => "xs:string",
        _ => null,
    };

    /// <summary>
    /// A non-NULL SQL value as the XQuery item <c>sql:variable</c> /
    /// <c>sql:column</c> hands the expression: the mapped type's canonical text,
    /// with the date-time family written at the SQL type's own fractional
    /// precision (<c>datetime</c> always three digits) as real writes them.
    /// </summary>
    public static object FromSql(SqlValue value)
    {
        var type = value.Type;
        switch (type)
        {
            case BitSqlType:
                return value.AsBoolean;
            case FloatSqlType:
                return Number(value.AsDouble, "xs:double");
            case RealSqlType:
                return Number(value.CoerceTo(SqlType.Float).AsDouble, "xs:float");
            case Int32SqlType or BigIntSqlType or SmallIntSqlType or TinyIntSqlType:
                {
                    var text = value.CoerceTo(SqlType.NVarchar).AsString;
                    return new XmlTypedAtomic(SqlTypeName(type)!, text, double.Parse(text, CultureInfo.InvariantCulture), XmlStaticKind.Number);
                }
            case DecimalSqlType or MoneySqlType or SmallMoneySqlType:
                {
                    var raw = type is DecimalSqlType ? value.CoerceTo(SqlType.NVarchar).AsString : value.AsMoneyDecimal38.ToString();
                    var text = XsdCanonical.Render(Resolve("decimal")!, raw);
                    return new XmlTypedAtomic("xs:decimal", text, double.Parse(text, CultureInfo.InvariantCulture), XmlStaticKind.Number);
                }
            case DateTimeSqlType:
                return DateTimeText(value.AsDateTime, 3);
            case SmallDateTimeSqlType:
                return DateTimeText(value.AsSmallDateTime, 3);
            case DateTime2SqlType dateTime2:
                return DateTimeText(value.AsDateTime2, dateTime2.precision);
            case DateSqlType:
                return new XmlTypedAtomic("xs:date", value.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), double.NaN, XmlStaticKind.String);
            case TimeSqlType time:
                {
                    var text = value.AsTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + Fraction(value.AsTime.Ticks % TimeSpan.TicksPerSecond, time.precision);
                    return new XmlTypedAtomic("xs:time", text, double.NaN, XmlStaticKind.String);
                }
            case DateTimeOffsetSqlType offsetType:
                {
                    var offset = value.AsDateTimeOffset;
                    var text = offset.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                        + Fraction(offset.Ticks % TimeSpan.TicksPerSecond, offsetType.precision)
                        + offset.ToString("zzz", CultureInfo.InvariantCulture);
                    return new XmlTypedAtomic("xs:dateTime", text, double.NaN, XmlStaticKind.String);
                }
            case BinarySqlType or VarbinarySqlType or RowVersionSqlType:
                return new XmlTypedAtomic("xs:base64Binary", Convert.ToBase64String(value.AsBytes), double.NaN, XmlStaticKind.String);
            default:
                return value.CoerceTo(SqlType.NVarchar).AsString;
        }
    }

    private static XmlTypedAtomic DateTimeText(DateTime value, int precision) =>
        new(
            "xs:dateTime",
            value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + Fraction(value.Ticks % TimeSpan.TicksPerSecond, precision),
            double.NaN,
            XmlStaticKind.String);

    /// <summary>A fraction of a second at exactly <paramref name="precision"/> digits, trailing zeros kept.</summary>
    private static string Fraction(long ticksInSecond, int precision)
    {
        if (precision == 0)
            return string.Empty;
        var divisor = 1L;
        for (var p = precision; p < 7; p++)
            divisor *= 10;
        return "." + (ticksInSecond / divisor).ToString("D" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
