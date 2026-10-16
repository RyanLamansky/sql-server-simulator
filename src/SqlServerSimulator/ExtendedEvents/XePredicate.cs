using System.Globalization;
using System.Numerics;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// An event's <c>WHERE</c> clause as written: <c>AND</c> / <c>OR</c> /
/// <c>NOT</c> over leaves, each a source compared with a constant by an
/// operator or a named comparator. Parsing settles only the grammar, which
/// real checks as the batch compiles; <see cref="Bind"/> resolves the names
/// and converts the constants against the event, which real does as the
/// statement runs (probed 2026-10-16 against SQL Server 2025).
/// </summary>
internal abstract class XePredicateSyntax
{
    /// <summary>
    /// The predicate's text as <c>sys.server_event_session_events.predicate</c>
    /// stores it: every name bracketed as written, every number parenthesized,
    /// the operators respelled (<c>!=</c> as <c>&lt;&gt;</c>, <c>LIKE</c> as
    /// <c>like</c>), parentheses kept only where precedence needs them, and the
    /// whole wrapped in one pair.
    /// </summary>
    public string Text => "(" + this.Render() + ")";

    /// <summary>The text without the outer parentheses.</summary>
    public abstract string Render();

    /// <summary>Resolves the names against <paramref name="event"/> and converts each constant, raising real's error for the first leaf that fails.</summary>
    public abstract XePredicate Bind(XeObject @event);

    /// <summary>
    /// Parses a predicate expression, the cursor on its first token on entry
    /// and on the first token past it on return.
    /// </summary>
    public static XePredicateSyntax Parse(ParserContext context)
    {
        var left = ParseTerm(context);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Or })
        {
            context.MoveNextRequired();
            left = new XePredicateOrSyntax(left, ParseTerm(context));
        }
        return left;
    }

    private static XePredicateSyntax ParseTerm(ParserContext context)
    {
        var left = ParseFactor(context);
        while (context.Token is ReservedKeyword { Keyword: Keyword.And })
        {
            context.MoveNextRequired();
            left = new XePredicateAndSyntax(left, ParseFactor(context));
        }
        return left;
    }

    private static XePredicateSyntax ParseFactor(ParserContext context)
    {
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Not }:
                context.MoveNextRequired();
                return new XePredicateNotSyntax(ParseFactor(context));
            case Operator { Character: '(' }:
                context.MoveNextRequired();
                var inner = Parse(context);
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
                return inner is XePredicateLeafSyntax or XePredicateNotSyntax ? inner : new XePredicateGroupSyntax(inner);
            case Name:
                return XePredicateLeafSyntax.ParseLeaf(context);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>A dotted name's parts as written, the cursor on its first part on entry and past its last on return.</summary>
    internal static List<string> ParseDottedName(ParserContext context)
    {
        var parts = new List<string>(3);
        while (true)
        {
            if (context.Token is not Name name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            parts.Add(name.Value);
            if (context.GetNextOptional() is not Operator { Character: '.' })
                return parts;
            context.MoveNextRequired();
        }
    }

    internal static string Bracketed(List<string> parts) =>
        string.Join('.', parts.Select(part => "[" + part.Replace("]", "]]", StringComparison.Ordinal) + "]"));
}

/// <summary>A parenthesized <c>AND</c> or <c>OR</c>, kept so a right-nested one renders with its parentheses as real's text does.</summary>
internal sealed class XePredicateGroupSyntax(XePredicateSyntax inner) : XePredicateSyntax
{
    public readonly XePredicateSyntax Inner = inner;

    public override string Render() => this.Inner.Render();

    public override XePredicate Bind(XeObject @event) => this.Inner.Bind(@event);
}

internal sealed class XePredicateAndSyntax(XePredicateSyntax left, XePredicateSyntax right) : XePredicateSyntax
{
    public override string Render() => Operand(left, rightSide: false) + " AND " + Operand(right, rightSide: true);

    // A left operand of the same operator reads without parentheses; a
    // right one keeps them, as does an OR on either side.
    private static string Operand(XePredicateSyntax operand, bool rightSide) => operand switch
    {
        XePredicateGroupSyntax { Inner: XePredicateOrSyntax } group => "(" + group.Render() + ")",
        XePredicateOrSyntax => "(" + operand.Render() + ")",
        XePredicateGroupSyntax { Inner: XePredicateAndSyntax } group when rightSide => "(" + group.Render() + ")",
        _ => operand.Render(),
    };

    public override XePredicate Bind(XeObject @event) => new XePredicateAnd(left.Bind(@event), right.Bind(@event));
}

internal sealed class XePredicateOrSyntax(XePredicateSyntax left, XePredicateSyntax right) : XePredicateSyntax
{
    public override string Render() => Operand(left, rightSide: false) + " OR " + Operand(right, rightSide: true);

    private static string Operand(XePredicateSyntax operand, bool rightSide) => operand switch
    {
        XePredicateGroupSyntax { Inner: XePredicateOrSyntax } group when rightSide => "(" + group.Render() + ")",
        _ => operand.Render(),
    };

    public override XePredicate Bind(XeObject @event) => new XePredicateOr(left.Bind(@event), right.Bind(@event));
}

internal sealed class XePredicateNotSyntax(XePredicateSyntax operand) : XePredicateSyntax
{
    public override string Render() => operand switch
    {
        XePredicateGroupSyntax or XePredicateAndSyntax or XePredicateOrSyntax => "NOT (" + operand.Render() + ")",
        _ => "NOT " + operand.Render(),
    };

    public override XePredicate Bind(XeObject @event) => new XePredicateNot(operand.Bind(@event));
}

/// <summary>
/// One comparison: a source (an event field, or a package's predicate source)
/// against a constant, by an operator or by a comparator named as a function.
/// </summary>
internal sealed class XePredicateLeafSyntax : XePredicateSyntax
{
    /// <summary>The source's name parts as written.</summary>
    public readonly List<string> Source;

    /// <summary><c>=</c>, <c>&lt;&gt;</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c> or <c>like</c>; null for the comparator form.</summary>
    public readonly string? Operator;

    /// <summary>The comparator's name parts as written, for the function form.</summary>
    public readonly List<string>? Comparator;

    public readonly XeConstant Value;

    private XePredicateLeafSyntax(List<string> source, string? @operator, List<string>? comparator, XeConstant value)
    {
        this.Source = source;
        this.Operator = @operator;
        this.Comparator = comparator;
        this.Value = value;
    }

    public static XePredicateSyntax ParseLeaf(ParserContext context)
    {
        var name = ParseDottedName(context);
        if (context.Token is Operator { Character: '(' })
        {
            // A comparator is named with its package; a bare name followed by
            // a parenthesis is Msg 102 near it.
            if (name.Count < 2)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var source = ParseDottedName(context);
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var argument = XeConstant.Parse(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return new XePredicateLeafSyntax(source, null, name, argument);
        }

        var negated = false;
        string op;
        switch (context.Token)
        {
            case Operator { Character: '=' }:
                op = "=";
                context.MoveNextRequired();
                break;
            case Operator { Character: '>' }:
                context.MoveNextRequired();
                op = ">";
                if (context.Token is Operator { Character: '=' })
                {
                    op = ">=";
                    context.MoveNextRequired();
                }
                break;
            case Operator { Character: '<' }:
                context.MoveNextRequired();
                op = "<";
                if (context.Token is Operator { Character: '=' or '>' } second)
                {
                    op = second.Character == '=' ? "<=" : "<>";
                    context.MoveNextRequired();
                }
                break;
            case Operator { Character: '!' }:
                context.MoveNextRequired();
                op = context.Token switch
                {
                    Operator { Character: '=' } => "<>",
                    Operator { Character: '<' } => ">=",
                    Operator { Character: '>' } => "<=",
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.Like }:
                op = "like";
                context.MoveNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.Not }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Like })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                negated = true;
                op = "like";
                context.MoveNextRequired();
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var leaf = new XePredicateLeafSyntax(name, op, null, XeConstant.Parse(context));
        return negated ? new XePredicateNotSyntax(leaf) : leaf;
    }

    public override string Render() => this.Comparator is { } comparator
        ? Bracketed(comparator) + "(" + Bracketed(this.Source) + "," + this.Value.Text + ")"
        : Bracketed(this.Source) + (this.Operator == "like" ? " like " : this.Operator) + this.Value.Text;

    public override XePredicate Bind(XeObject @event)
    {
        // The source resolves first. An operator then picks its comparator —
        // LIKE over a number is Msg 25712, = over xml Msg 25715 — before the
        // constant converts; a comparator named as a function meets the type
        // only after the constant converts, so a string field compared by
        // equal_int64 with a number is the constant's Msg 25713, not the
        // comparator's Msg 25636 (probed 2026-10-16 against SQL Server 2025).
        var (column, global, type, sourceName) = this.ResolveSource(@event);
        var comparator = this.Comparator is null ? DefaultComparator(this.Operator!, type, @event, sourceName) : null;
        var value = this.Value.ConvertTo(type, forPredicate: true) is { } converted
            ? converted
            : throw SimulatedSqlException.XeValueInvalid("event attribute or predicate source", sourceName, "event", @event.Name, this.Value.FailState(type, forPredicate: true));
        if (this.Comparator is { } comparatorName)
        {
            comparator = XeMetadata.Instance.Find(comparatorName[^2], comparatorName[^1], "pred_compare")
                ?? throw SimulatedSqlException.XeObjectNotFound("event predicate comparator", string.Join('.', comparatorName), state: 2);
            if (!type.TakesComparator(XeType.ComparatorFamily(comparator)))
                throw SimulatedSqlException.XePredicateTypesMismatch();
        }
        return new XePredicateLeaf(comparator!, column, global, type, value, @event);
    }

    private (XeObjectColumn? Column, XeObject? Global, XeType Type, string Name) ResolveSource(XeObject @event)
    {
        var written = string.Join('.', this.Source);
        var parts = this.Source;
        var fieldName = parts.Count switch
        {
            1 => parts[0],
            2 when parts[0].Equals(@event.Name, StringComparison.OrdinalIgnoreCase) => parts[1],
            3 when parts[0].Equals(@event.Package.Name, StringComparison.OrdinalIgnoreCase) && parts[1].Equals(@event.Name, StringComparison.OrdinalIgnoreCase) => parts[2],
            _ => null,
        };
        if (fieldName is not null)
        {
            foreach (var column in @event.Columns)
            {
                if (column.ColumnType == "data" && column.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    return (column, null, XeType.Of(column.TypeName, column.TypePackage), parts[^1]);
            }
            throw SimulatedSqlException.XeObjectNotFound("event attribute or predicate source", written, state: 8);
        }
        if (parts.Count == 2 && XeMetadata.Instance.Find(parts[0], parts[1], "pred_source") is { IsPrivate: false } source)
            return (null, source, XeType.Of(source.TypeName!, source.TypePackage), parts[^1]);
        throw SimulatedSqlException.XeObjectNotFound("event attribute or predicate source", written, state: 8);
    }

    /// <summary>
    /// The comparator an operator stands for over a source of
    /// <paramref name="type"/>: the <c>*_int64</c> family for a signed
    /// integer, <c>*_uint64</c> for an unsigned one or a map, the
    /// case-insensitive <c>*_i_sql_*_string</c> pair in <c>sqlserver</c> for
    /// a string, and only <c>=</c> / <c>&lt;&gt;</c> for a boolean, a GUID or
    /// bytes (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static XeObject DefaultComparator(string op, XeType type, XeObject @event, string sourceName)
    {
        var stem = op switch
        {
            "<" => "less_than",
            "<=" => "less_than_equal",
            "<>" => "not_equal",
            "=" => "equal",
            ">" => "greater_than",
            ">=" => "greater_than_equal",
            _ => "like",
        };
        if (stem == "like" && type.Family is not (XeTypeFamily.UnicodeString or XeTypeFamily.AnsiString))
            throw SimulatedSqlException.XeInvalidComparisonOperator();
        var (package, name) = type.Family switch
        {
            XeTypeFamily.Signed => ("package0", stem + "_int64"),
            XeTypeFamily.Unsigned or XeTypeFamily.Map => ("package0", stem + "_uint64"),
            XeTypeFamily.Float => ("package0", stem + "_float64"),
            XeTypeFamily.Pointer => ("package0", stem + "_ptr"),
            XeTypeFamily.UnicodeString => ("sqlserver", stem == "like" ? "like_i_sql_unicode_string" : stem + "_i_sql_unicode_string"),
            XeTypeFamily.AnsiString => ("sqlserver", stem == "like" ? "like_i_sql_ansi_string" : stem + "_i_sql_ansi_string"),
            XeTypeFamily.Boolean when stem is "equal" or "not_equal" => ("package0", stem + "_boolean"),
            XeTypeFamily.Guid when stem is "equal" or "not_equal" => ("package0", stem + "_guid"),
            XeTypeFamily.BinaryData when stem is "equal" or "not_equal" => ("package0", stem + "_binary_data"),
            _ => (null, null),
        };
        return (package is null ? null : XeMetadata.Instance.Find(package, name!, "pred_compare"))
            ?? throw SimulatedSqlException.XeOperatorNotDefined(@event.Name, op, type.Name, sourceName);
    }
}

/// <summary>
/// A constant as a predicate or a <c>SET</c> clause writes it: a number
/// (negated or parenthesized), a string with or without <c>N</c>, or a
/// binary literal, kept as written for the stored text and converted to
/// the type it meets on binding.
/// </summary>
internal sealed class XeConstant
{
    /// <summary>The token's value, negated when a minus sign led it.</summary>
    public readonly SqlValue Value;

    /// <summary>The stored text: a number parenthesized, a string quoted, bytes as <c>0x…</c>.</summary>
    public readonly string Text;

    private XeConstant(SqlValue value, string text)
    {
        this.Value = value;
        this.Text = text;
    }

    /// <summary>A constant from a value already in hand, as a default session's definition carries one.</summary>
    public static XeConstant FromValue(SqlValue value) => new(value, TextOf(value));

    /// <summary>
    /// Parses a constant, the cursor on its first token on entry and past it
    /// on return. A leading <c>+</c>, a variable or any other expression is
    /// Msg 102 near it, as real's grammar has no expressions here.
    /// </summary>
    public static XeConstant Parse(ParserContext context)
    {
        if (context.Token is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            var inner = Parse(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return inner;
        }
        var negative = false;
        if (context.Token is Operator { Character: '-' })
        {
            negative = true;
            context.MoveNextRequired();
        }
        XeConstant constant;
        switch (context.Token)
        {
            case Numeric numeric:
                var value = numeric.Value;
                if (negative)
                {
                    value = value.Type switch
                    {
                        Int32SqlType => SqlValue.FromInt32(-value.AsInt32),
                        FloatSqlType => SqlValue.FromDouble(-value.AsDouble),
                        _ => SqlValue.FromDecimal(value.Type, value.AsDecimal38.Negate()),
                    };
                }
                constant = new(value, TextOf(value));
                break;
            case Literal literal when !negative:
                constant = new(literal.Value, TextOf(literal.Value));
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return constant;
    }

    private static string TextOf(SqlValue value) => value.Type switch
    {
        Int32SqlType => "(" + value.AsInt32.ToString(CultureInfo.InvariantCulture) + ")",
        FloatSqlType => "(" + value.AsDouble.ToString("E16", CultureInfo.InvariantCulture).Replace('E', 'e') + ")",
        VarbinarySqlType or BinarySqlType => "0x" + Convert.ToHexString(value.AsBytes),
        NVarcharSqlType or NCharSqlType => "N'" + value.AsString.Replace("'", "''", StringComparison.Ordinal) + "'",
        VarcharSqlType or CharSqlType => "'" + value.AsString.Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => "(" + DecimalText(value) + ")",
    };

    private static string DecimalText(SqlValue value)
    {
        var text = value.AsDecimal38.ToString();
        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".";
    }

    /// <summary>Whether the constant is a string literal.</summary>
    public bool IsString => SqlType.IsStringCategory(this.Value.Type);

    /// <summary>
    /// The constant converted to <paramref name="type"/>, or null when it
    /// doesn't convert — <see cref="FailState"/> then names real's state.
    /// </summary>
    public XeValue? ConvertTo(XeType type, bool forPredicate)
    {
        _ = forPredicate;
        var value = this.Value;
        switch (type.Family)
        {
            case XeTypeFamily.UnicodeString:
                return this.IsString ? XeValue.FromUnicode(value.AsString) : null;
            case XeTypeFamily.AnsiString:
                return this.IsString ? XeValue.FromAnsi(value.AsString) : null;
            case XeTypeFamily.BinaryData:
                return value.Type is VarbinarySqlType or BinarySqlType ? XeValue.FromBinary(value.AsBytes) : null;
            case XeTypeFamily.Guid:
                return this.IsString && Guid.TryParse(value.AsString, out var guid) ? XeValue.FromGuid(guid) : null;
            case XeTypeFamily.Boolean:
                return this.Integer() is { } flag && (flag == 0 || flag == 1) ? XeValue.FromBoolean(flag == 1) : null;
            case XeTypeFamily.Float:
                return value.Type switch
                {
                    Int32SqlType => XeValue.FromFloat(value.AsInt32),
                    FloatSqlType => XeValue.FromFloat(value.AsDouble),
                    DecimalSqlType => XeValue.FromFloat(value.AsDecimal38.ToDouble()),
                    _ => null,
                };
            case XeTypeFamily.Map:
                if (this.IsString)
                {
                    foreach (var mapValue in type.Map!.MapValues)
                    {
                        if (mapValue.Value.Equals(value.AsString, StringComparison.OrdinalIgnoreCase))
                            return XeValue.FromMap((ulong)mapValue.Key, mapValue.Value);
                    }
                    return null;
                }
                return this.Integer() is { } key && key >= 0 && key <= type.UnsignedMax ? XeValue.FromMap((ulong)key, null) : null;
            case XeTypeFamily.Signed:
                return this.Integer() is { } signed && signed >= type.SignedMin && signed <= type.SignedMax ? XeValue.FromSigned((long)signed) : null;
            case XeTypeFamily.Unsigned or XeTypeFamily.Pointer:
                return this.Integer() is { } unsigned && unsigned >= 0 && unsigned <= type.UnsignedMax ? XeValue.FromUnsigned((ulong)unsigned) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The state real's Msg 25713 carries for a constant that doesn't convert
    /// to <paramref name="type"/>: 23 for a string where a number or a flag
    /// belongs, or a map value the map lacks; 13 for a number or bytes where
    /// a string belongs; 26 for a string where bytes belong; 15 for a
    /// fraction where an integer belongs; 14 for a flag other than 0 or 1;
    /// and for an integer out of range 4 when the type is signed, 9 in a
    /// predicate and 8 in a <c>SET</c> clause when it is unsigned (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public byte FailState(XeType type, bool forPredicate)
    {
        var value = this.Value;
        if (type.Family is XeTypeFamily.UnicodeString or XeTypeFamily.AnsiString)
            return 13;
        if (type.Family == XeTypeFamily.BinaryData)
            return this.IsString ? (byte)26 : (byte)13;
        if (this.IsString)
            return 23;
        if (value.Type is DecimalSqlType && value.AsDecimal38.Scale > 0 && type.Family != XeTypeFamily.Float)
            return 15;
        if (type.Family == XeTypeFamily.Boolean)
            return 14;
        if (type.Family == XeTypeFamily.Signed)
            return 4;
        return forPredicate ? (byte)9 : (byte)8;
    }

    /// <summary>
    /// The constant as an integer: a whole number as written, a float rounded
    /// half away from zero (<c>1.5e0</c> compares as 2), bytes read big-endian
    /// from their last eight; null for a fraction or a string.
    /// </summary>
    private BigInteger? Integer()
    {
        var value = this.Value;
        switch (value.Type)
        {
            case Int32SqlType:
                return value.AsInt32;
            case FloatSqlType:
                var rounded = Math.Round(value.AsDouble, MidpointRounding.AwayFromZero);
                return double.IsFinite(rounded) && Math.Abs(rounded) < 1e30 ? new BigInteger(rounded) : null;
            case DecimalSqlType when value.AsDecimal38.Scale == 0:
                return value.AsDecimal38.IsNegative ? -(BigInteger)value.AsDecimal38.Magnitude : (BigInteger)value.AsDecimal38.Magnitude;
            case VarbinarySqlType or BinarySqlType:
                var bytes = value.AsBytes;
                var start = Math.Max(0, bytes.Length - 8);
                BigInteger result = 0;
                for (var i = start; i < bytes.Length; i++)
                    result = (result << 8) | bytes[i];
                return result;
            default:
                return null;
        }
    }
}

/// <summary>An event's predicate resolved against the event: what it stores as XML and how it judges one firing.</summary>
internal abstract class XePredicate
{
    /// <summary>The predicate as <c>predicate_xml</c> and <c>sys.dm_xe_session_events.event_predicate</c> carry it.</summary>
    public string Xml
    {
        get
        {
            var builder = new StringBuilder();
            this.WriteXml(builder);
            return builder.ToString();
        }
    }

    public abstract void WriteXml(StringBuilder builder);

    public abstract bool Evaluate(XeFiring firing);
}

internal sealed class XePredicateAnd(XePredicate left, XePredicate right) : XePredicate
{
    public override void WriteXml(StringBuilder builder)
    {
        _ = builder.Append("<and>");
        left.WriteXml(builder);
        right.WriteXml(builder);
        _ = builder.Append("</and>");
    }

    public override bool Evaluate(XeFiring firing) => left.Evaluate(firing) && right.Evaluate(firing);
}

internal sealed class XePredicateOr(XePredicate left, XePredicate right) : XePredicate
{
    public override void WriteXml(StringBuilder builder)
    {
        _ = builder.Append("<or>");
        left.WriteXml(builder);
        right.WriteXml(builder);
        _ = builder.Append("</or>");
    }

    public override bool Evaluate(XeFiring firing) => left.Evaluate(firing) || right.Evaluate(firing);
}

internal sealed class XePredicateNot(XePredicate operand) : XePredicate
{
    public override void WriteXml(StringBuilder builder)
    {
        _ = builder.Append("<not>");
        operand.WriteXml(builder);
        _ = builder.Append("</not>");
    }

    public override bool Evaluate(XeFiring firing) => !operand.Evaluate(firing);
}

/// <summary>One bound comparison: its comparator, its source and the converted constant.</summary>
internal sealed class XePredicateLeaf(XeObject comparator, XeObjectColumn? column, XeObject? global, XeType type, XeValue value, XeObject @event) : XePredicate
{
    private static readonly Collation LikeCollation = Collation.Get("SQL_Latin1_General_CP1_CI_AS");

    private LikeMatcher? matcher;

    public override void WriteXml(StringBuilder builder)
    {
        _ = builder.Append("<leaf><comparator name=\"").Append(comparator.Name).Append("\" package=\"").Append(comparator.Package.Name).Append("\"></comparator>");
        _ = column is not null
            ? builder.Append("<event name=\"").Append(@event.Name).Append("\" package=\"").Append(@event.Package.Name).Append("\" field=\"").Append(column.Name).Append("\"></event>")
            : builder.Append("<global name=\"").Append(global!.Name).Append("\" package=\"").Append(global.Package.Name).Append("\"></global>");
        _ = value.Family is XeTypeFamily.UnicodeString or XeTypeFamily.AnsiString
            ? builder.Append("<value><![CDATA[").Append(value.Text).Append("]]></value>")
            : builder.Append("<value>").Append(value.XmlText).Append("</value>");
        _ = builder.Append("</leaf>");
    }

    public override bool Evaluate(XeFiring firing)
    {
        var actual = column is not null ? firing.Field(column) : firing.Global(global!, type);
        var name = comparator.Name;
        if (name.StartsWith("like_", StringComparison.Ordinal))
        {
            this.matcher ??= LikeMatcher.Compile(value.Text ?? "", null, LikeCollation, forPatIndex: false);
            return this.matcher.IsMatch(actual.Text ?? "", trailingSpaceSlack: false);
        }
        var order = Compare(actual, value, name.Contains("_i_", StringComparison.Ordinal));
        return StemOf(name) switch
        {
            "equal" => order == 0,
            "greater_than" => order > 0,
            "greater_than_equal" => order >= 0,
            "less_than" => order < 0,
            "less_than_equal" => order <= 0,
            "not_equal" => order != 0,
            _ => false,
        };
    }

    private static string StemOf(string comparatorName)
    {
        foreach (var stem in (ReadOnlySpan<string>)["greater_than_equal", "less_than_equal", "greater_than", "less_than", "not_equal", "equal"])
        {
            if (comparatorName.StartsWith(stem + "_", StringComparison.Ordinal))
                return stem;
        }
        return "";
    }

    private static int Compare(XeValue actual, XeValue constant, bool ignoreCase) => constant.Family switch
    {
        XeTypeFamily.Signed => actual.Signed.CompareTo(constant.Signed),
        XeTypeFamily.Unsigned or XeTypeFamily.Map or XeTypeFamily.Pointer or XeTypeFamily.Boolean => actual.Unsigned.CompareTo(constant.Unsigned),
        XeTypeFamily.Float => actual.Float.CompareTo(constant.Float),
        XeTypeFamily.Guid => actual.Guid.CompareTo(constant.Guid),
        XeTypeFamily.BinaryData => actual.Bytes.AsSpan().SequenceCompareTo(constant.Bytes),
        _ => string.Compare(actual.Text, constant.Text, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
    };
}
