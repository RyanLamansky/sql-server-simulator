using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A Dynamic Data Masking function as a column declares it
/// (<c>MASKED WITH (FUNCTION = '…')</c>): which of the five functions, its
/// arguments already validated against the column's type, and the canonical
/// text <c>sys.masked_columns.masking_function</c> reports. <see cref="Apply"/>
/// computes what a principal without <c>UNMASK</c> reads in place of a value.
/// </summary>
/// <remarks>
/// The grammar and every refusal below were probed 2026-09-27 against SQL
/// Server 2025. The function text is read from its first <c>(</c> and its last
/// <c>)</c>: no <c>(</c> at all is Msg 16006 naming the whole text (an empty
/// text is Msg 16002 instead), no <c>)</c> after it Msg 16006 naming what
/// precedes it, and anything after the last <c>)</c> Msg 16004 — whatever the
/// name, so <c>'  Default ( ) '</c> reports <c>'  Default '</c>. Only then is
/// the name looked up, exactly and case-insensitively, so a space inside it
/// (<c>' default()'</c>, <c>'default ()'</c>) is Msg 16002.
/// </remarks>
internal sealed class MaskingFunction
{
    public readonly MaskingFunctionKind Kind;

    /// <summary>The text <c>sys.masked_columns.masking_function</c> reports: the lowercase name and each argument re-rendered, joined by <c>", "</c>.</summary>
    public readonly string Definition;

    private readonly int prefix;
    private readonly int suffix;
    private readonly string padding = string.Empty;
    private readonly SqlValue low;
    private readonly SqlValue high;
    private readonly char datePart;

    /// <summary>
    /// The <c>default()</c> function, which is also what any expression over a
    /// masked column reads as: a principal without <c>UNMASK</c> sees
    /// <c>c + 'x'</c>, <c>LEN(c)</c> or <c>MAX(c)</c> as the default mask of
    /// the expression's own type.
    /// </summary>
    public static readonly MaskingFunction Default = new(MaskingFunctionKind.Default, "default()");

    private MaskingFunction(MaskingFunctionKind kind, string definition)
    {
        this.Kind = kind;
        this.Definition = definition;
    }

    private MaskingFunction(int prefix, string padding, int suffix)
        : this(MaskingFunctionKind.Partial, $"partial({prefix}, \"{padding.Replace("\"", "\"\"", StringComparison.Ordinal)}\", {suffix})")
    {
        this.prefix = prefix;
        this.padding = padding;
        this.suffix = suffix;
    }

    private MaskingFunction(SqlValue low, SqlValue high, string lowText, string highText)
        : this(MaskingFunctionKind.Random, $"random({lowText}, {highText})")
    {
        this.low = low;
        this.high = high;
    }

    private MaskingFunction(char datePart)
        : this(MaskingFunctionKind.Datetime, $"datetime(\"{datePart}\")")
    {
        this.datePart = datePart;
    }

    /// <summary>
    /// Parses and validates <paramref name="text"/> for a column of
    /// <paramref name="type"/>, raising real's Msg 16002 – 16006 at state 0.
    /// </summary>
    public static MaskingFunction Parse(string text, string columnName, SqlType type)
    {
        if (text.Length == 0)
            throw SimulatedSqlException.InvalidMaskingFunction(columnName);
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
            throw SimulatedSqlException.InvalidMaskingFormat(text, columnName);
        var name = text[..open];
        var close = text.LastIndexOf(')');
        if (close < open)
            throw SimulatedSqlException.InvalidMaskingFormat(name, columnName);
        if (close != text.Length - 1)
            throw SimulatedSqlException.MaskingParameterCount(name, columnName);

        Span<char> upper = stackalloc char[Math.Min(name.Length, 8)];
        var length = name.Length > 8 ? 0 : name.AsSpan().ToUpperInvariant(upper);
        MaskingFunctionKind? kind = upper[..length] switch
        {
            "DATETIME" => MaskingFunctionKind.Datetime,
            "DEFAULT" => MaskingFunctionKind.Default,
            "EMAIL" => MaskingFunctionKind.Email,
            "PARTIAL" => MaskingFunctionKind.Partial,
            "RANDOM" => MaskingFunctionKind.Random,
            _ => null,
        };
        if (kind is not { } recognized)
            throw SimulatedSqlException.InvalidMaskingFunction(columnName);
        if (!Supports(recognized, type))
        {
            throw SimulatedSqlException.MaskingFunctionUnsupportedType(columnName, recognized switch
            {
                MaskingFunctionKind.Datetime => "datetime",
                MaskingFunctionKind.Email => "email",
                MaskingFunctionKind.Partial => "partial",
                _ => "random",
            });
        }

        var arguments = SplitArguments(text.AsSpan(open + 1, close - open - 1));
        var expected = recognized switch
        {
            MaskingFunctionKind.Partial => 3,
            MaskingFunctionKind.Random => 2,
            MaskingFunctionKind.Datetime => 1,
            _ => 0,
        };
        if (arguments is not null && arguments.Count != expected)
            throw SimulatedSqlException.MaskingParameterCount(name, columnName);
        if (arguments is null)
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);

        return recognized switch
        {
            MaskingFunctionKind.Default => Default,
            MaskingFunctionKind.Email => Email,
            MaskingFunctionKind.Partial => ParsePartial(arguments, name, columnName),
            MaskingFunctionKind.Random => ParseRandom(arguments, name, columnName, type),
            _ => ParseDatetime(arguments[0], name, columnName, type),
        };
    }

    private static readonly MaskingFunction Email = new(MaskingFunctionKind.Email, "email()");

    /// <summary>
    /// Whether <paramref name="kind"/> applies to a column of
    /// <paramref name="type"/>: <c>default()</c> to any, <c>email()</c> and
    /// <c>partial()</c> to the character types, <c>random()</c> to the numeric
    /// ones, <c>datetime()</c> to the date and time ones (Msg 16003 otherwise).
    /// </summary>
    private static bool Supports(MaskingFunctionKind kind, SqlType type) => kind switch
    {
        MaskingFunctionKind.Email or MaskingFunctionKind.Partial =>
            type is VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType or TextSqlType or NTextSqlType or SystemNameSqlType,
        MaskingFunctionKind.Random => type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate,
        MaskingFunctionKind.Datetime => type.Category == SqlTypeCategory.DateTime,
        _ => true,
    };

    /// <summary>
    /// The comma-separated arguments between the parentheses, each trimmed; a
    /// comma inside a double-quoted string (<c>""</c> escapes a quote) doesn't
    /// split. Whitespace alone is no argument at all. Null when a quote never
    /// closes.
    /// </summary>
    private static List<string>? SplitArguments(ReadOnlySpan<char> inner)
    {
        List<string> arguments = [];
        if (inner.IsWhiteSpace())
            return arguments;
        var start = 0;
        var quoted = false;
        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ',' when !quoted:
                    arguments.Add(inner[start..i].Trim().ToString());
                    start = i + 1;
                    break;
            }
        }
        if (quoted)
            return null;
        arguments.Add(inner[start..].Trim().ToString());
        return arguments;
    }

    private static MaskingFunction ParsePartial(List<string> arguments, string name, string columnName)
    {
        if (!TryParseCount(arguments[0], out var prefix) || !TryParseCount(arguments[2], out var suffix))
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        var padding = arguments[1];
        if (padding.Length < 2 || padding[0] != '"' || padding[^1] != '"')
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        var body = padding[1..^1];
        // A lone quote inside the padding would have closed it early.
        var unescaped = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '"')
            {
                if (i + 1 >= body.Length || body[i + 1] != '"')
                    throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
                i++;
            }
            _ = unescaped.Append(body[i]);
        }
        return new MaskingFunction(prefix, unescaped.ToString(), suffix);
    }

    /// <summary>A prefix / suffix length: decimal digits only, leading zeros allowed, no sign.</summary>
    private static bool TryParseCount(string text, out int value)
    {
        value = 0;
        if (text.Length == 0)
            return false;
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A <c>random(low, high)</c> bound pair: plain decimal literals (an
    /// optional leading minus, no exponent, no plus, no quotes) converted to
    /// the column's type as an assignment would — so a decimal column rounds
    /// them to its scale and a bound the type can't hold is refused — with
    /// <c>low &lt;= high</c>. A <c>bit</c> takes 0 and 1 alone.
    /// </summary>
    private static MaskingFunction ParseRandom(List<string> arguments, string name, string columnName, SqlType type)
    {
        var low = RandomBound(arguments[0], name, columnName, type);
        var high = RandomBound(arguments[1], name, columnName, type);
        if (low.CompareTo(high) > 0)
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        return new MaskingFunction(low, high, RenderBound(low), RenderBound(high));
    }

    private static SqlValue RandomBound(string text, string name, string columnName, SqlType type)
    {
        var digits = 0;
        var dots = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
                digits++;
            else if (c == '.')
                dots++;
            else if (c != '-' || i != 0)
                throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        }
        if (digits == 0 || dots > 1 || (type == SqlType.Bit && text is not ("0" or "1")))
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        try
        {
            return SqlValue.FromVarchar(text).CoerceTo(type);
        }
        catch (Exception error) when (error is SimulatedSqlException or OverflowException or FormatException)
        {
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        }
    }

    /// <summary>
    /// A bound as <c>sys.masked_columns</c> reports it: an integer plainly, a
    /// decimal at its column's scale, money rounded to four places with the
    /// trailing zeros dropped, a float in its round-trip form (<c>-0</c> kept).
    /// </summary>
    private static string RenderBound(SqlValue value) => value.Type.Category switch
    {
        SqlTypeCategory.Integer => value.Type == SqlType.Bit
            ? (value.AsBoolean ? "1" : "0")
            : value.CoerceTo(SqlType.BigInt).AsInt64.ToString(CultureInfo.InvariantCulture),
        SqlTypeCategory.Decimal => value.AsDecimal38.ToString(),
        SqlTypeCategory.Money => value.AsMoney.ToString("0.####", CultureInfo.InvariantCulture),
        _ => value.Type == SqlType.Real
            ? value.AsSingle.ToString("R", CultureInfo.InvariantCulture)
            : value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// <c>datetime("X")</c>: one of <c>Y M D h m s</c>, case-sensitive and
    /// double-quoted; a <c>date</c> column takes only the date parts and a
    /// <c>time</c> column only the time parts.
    /// </summary>
    private static MaskingFunction ParseDatetime(string argument, string name, string columnName, SqlType type)
    {
        if (argument.Length != 3 || argument[0] != '"' || argument[2] != '"')
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        var part = argument[1];
        var isDatePart = part is 'Y' or 'M' or 'D';
        var isTimePart = part is 'h' or 'm' or 's';
        if ((!isDatePart && !isTimePart) || (type is DateSqlType && !isDatePart) || (type is TimeSqlType && !isTimePart))
            throw SimulatedSqlException.InvalidMaskingArgument(name, columnName);
        return new MaskingFunction(part);
    }

    /// <summary>
    /// What a principal without <c>UNMASK</c> reads in place of
    /// <paramref name="value"/>, typed <paramref name="type"/> (the column's,
    /// or the expression's). NULL stays NULL.
    /// </summary>
    public SqlValue Apply(SqlValue value, SqlType type)
    {
        if (value.IsNull)
            return value;
        return this.Kind switch
        {
            MaskingFunctionKind.Email => value.AsString is { Length: > 0 } email
                ? MaskedString(type, email[..1] + "XXX@XXXX.com")
                : DefaultMask(value, type),
            MaskingFunctionKind.Partial => MaskedString(type, value.AsString is { } text && text.Length > this.prefix + this.suffix
                ? string.Concat(text.AsSpan(0, this.prefix), this.padding, text.AsSpan(text.Length - this.suffix))
                : this.padding),
            MaskingFunctionKind.Random => this.RandomValue(type),
            MaskingFunctionKind.Datetime => this.MaskDatePart(value),
            _ => DefaultMask(value, type),
        };
    }

    /// <summary>
    /// <c>default()</c>'s value for <paramref name="type"/>: <c>xxxx</c> cut to
    /// a shorter string type's length, the single byte <c>0x30</c> for the
    /// binary types, zero, <c>1900-01-01 00:00:00</c> (at <c>+00:00</c>), the
    /// empty GUID, eight zero bytes for <c>rowversion</c>,
    /// <c>&lt;masked /&gt;</c>, <c>{"masked":true}</c>; a <c>sql_variant</c>
    /// masks as its base type does, but for a string's length. A fixed-length
    /// string or binary value is sent unpadded.
    /// </summary>
    private static SqlValue DefaultMask(SqlValue value, SqlType type)
    {
        switch (type)
        {
            case SqlVariantSqlType:
                // A character payload masks to the whole xxxx, whatever its
                // own length (probed 2026-09-27: a variant holding 'ab' reads
                // xxxx).
                var inner = value.AsVariantInner;
                return SqlValue.FromVariant(inner.Type switch
                {
                    NVarcharSqlType or NCharSqlType or NTextSqlType => SqlValue.FromNVarchar("xxxx"),
                    VarcharSqlType or CharSqlType or TextSqlType => SqlValue.FromVarchar("xxxx"),
                    _ => DefaultMask(inner, inner.Type),
                });
            case VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType or TextSqlType or NTextSqlType or SystemNameSqlType:
                return MaskedString(type, "xxxx");
            case XmlSqlType:
                return SqlValue.FromXml("<masked />");
            case JsonSqlType:
                return SqlValue.FromJson("{\"masked\":true}");
            case BinarySqlType or VarbinarySqlType or ImageSqlType:
                return SqlValue.MaskedFixedLength(type, (byte[])[0x30]);
            case RowVersionSqlType:
                return SqlValue.FromRowVersion(0);
            case UniqueIdentifierSqlType:
                return SqlValue.FromGuid(Guid.Empty);
            case HierarchyIdSqlType:
                // Real sends the single byte 0x00, which no hierarchyid method
                // can decode (probed 2026-09-27: CAST to varbinary reads 0x00,
                // ToString() raises Msg 6522).
                return SqlValue.FromHierarchyIdBytes([0x00]);
            case SpatialSqlType or VectorSqlType:
                // Real sends a single 0x00 byte its own client can't read back
                // into the type (probed 2026-09-27: SqlClient raises on the
                // geography / geometry / vector value, and CAST to varbinary
                // reads 0x00); the simulator's values of these types are
                // parsed, so there is no such value to send.
                throw new NotSupportedException($"Dynamic Data Masking of a {type.SqlServerName} value isn't modeled.");
        }
        return type.Category switch
        {
            SqlTypeCategory.DateTime => SqlValue.FromDateTime(new DateTime(1900, 1, 1)).CoerceTo(type),
            _ => SqlValue.FromInt32(0).CoerceTo(type),
        };
    }

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="type"/>'s length and
    /// typed as it, a fixed-length type left unpadded as real sends it.
    /// </summary>
    private static SqlValue MaskedString(SqlType type, string text)
    {
        var length = type switch
        {
            VarcharSqlType v => v.length,
            NVarcharSqlType n => n.length,
            CharSqlType c => c.length,
            NCharSqlType nc => nc.length,
            _ => SqlType.MaxLengthSentinel,
        };
        if (length > 0 && text.Length > length)
            text = text[..length];
        return type is CharSqlType or NCharSqlType
            ? SqlValue.MaskedFixedLength(type, text)
            : SqlValue.FromString(type, text);
    }

    /// <summary>
    /// A value drawn uniformly from the bounds: an integer, a decimal at the
    /// column's scale, money at four places, a float anywhere between.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A masking function's random() is a non-cryptographic display value.")]
    private SqlValue RandomValue(SqlType type)
    {
        var random = Random.Shared;
        switch (type.Category)
        {
            case SqlTypeCategory.Integer:
                var lowInteger = this.low.CoerceTo(SqlType.BigInt).AsInt64;
                var highInteger = this.high.CoerceTo(SqlType.BigInt).AsInt64;
                var drawn = highInteger == long.MaxValue
                    ? random.NextInt64(lowInteger, highInteger)
                    : random.NextInt64(lowInteger, highInteger + 1);
                return SqlValue.FromInt64(drawn).CoerceTo(type);
            case SqlTypeCategory.Decimal or SqlTypeCategory.Money:
                var scale = type is DecimalSqlType decimalType ? decimalType.scale : 4;
                var lowDouble = this.low.CoerceTo(SqlType.Float).AsDouble;
                var highDouble = this.high.CoerceTo(SqlType.Float).AsDouble;
                var step = Math.Pow(10, -scale);
                var steps = (long)Math.Floor((highDouble - lowDouble) / step);
                var value = lowDouble + (random.NextInt64(0, steps + 1) * step);
                return SqlValue.FromDouble(Math.Round(Math.Min(value, highDouble), scale)).CoerceTo(type);
            default:
                var floor = this.low.CoerceTo(SqlType.Float).AsDouble;
                var ceiling = this.high.CoerceTo(SqlType.Float).AsDouble;
                return SqlValue.FromDouble(floor + (random.NextDouble() * (ceiling - floor))).CoerceTo(type);
        }
    }

    /// <summary>
    /// <c>datetime()</c>: the named part reset — the year to 2000, the month or
    /// day to 1, an hour, minute or second to 0 — with the fractional seconds
    /// always dropped and a <c>datetimeoffset</c> keeping its clock time at
    /// <c>+00:00</c>.
    /// </summary>
    private SqlValue MaskDatePart(SqlValue value)
    {
        var type = value.Type;
        var moment = type switch
        {
            DateSqlType => value.AsDate.ToDateTime(TimeOnly.MinValue),
            TimeSqlType => DateTime.MinValue + value.AsTime,
            DateTimeSqlType => value.AsDateTime,
            SmallDateTimeSqlType => value.AsSmallDateTime,
            DateTimeOffsetSqlType => value.AsDateTimeOffset.DateTime,
            _ => value.AsDateTime2,
        };
        moment = new DateTime(
            this.datePart == 'Y' ? 2000 : moment.Year,
            this.datePart == 'M' ? 1 : moment.Month,
            this.datePart == 'D' ? 1 : moment.Day,
            this.datePart == 'h' ? 0 : moment.Hour,
            this.datePart == 'm' ? 0 : moment.Minute,
            this.datePart == 's' ? 0 : moment.Second,
            DateTimeKind.Unspecified);
        return type switch
        {
            DateSqlType => SqlValue.FromDate(DateOnly.FromDateTime(moment)),
            TimeSqlType => SqlValue.FromTime(type, moment.TimeOfDay),
            DateTimeSqlType => SqlValue.FromDateTime(moment),
            SmallDateTimeSqlType => SqlValue.FromSmallDateTime(moment),
            DateTimeOffsetSqlType => SqlValue.FromDateTimeOffset(type, new DateTimeOffset(moment, TimeSpan.Zero)),
            _ => SqlValue.FromDateTime2(type, moment),
        };
    }
}

/// <summary>The five Dynamic Data Masking functions.</summary>
internal enum MaskingFunctionKind : byte
{
    Default,
    Email,
    Partial,
    Random,
    Datetime,
}
