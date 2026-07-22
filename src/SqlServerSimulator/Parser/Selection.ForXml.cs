using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// The fixed single-column name SQL Server assigns a top-level FOR XML
    /// result set (a GUID-shaped sentinel; consumers concatenate the chunks).
    /// </summary>
    private const string ForXmlColumnName = "XML_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>The xsi namespace declared when <c>ELEMENTS XSINIL</c> emits nil elements.</summary>
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Parses the trailing <c>FOR XML { RAW[('elem')] | AUTO | PATH[('row')] }
    /// [, ELEMENTS [XSINIL|ABSENT]] [, ROOT[('name')]]</c> clause when the
    /// cursor sits on <c>FOR</c>, wrapping <paramref name="inner"/> in a
    /// serializer that projects the single xml-string column. A <c>FOR</c>
    /// that isn't <c>XML</c> (<c>FOR BROWSE</c> / leftover) restores the cursor
    /// and returns <paramref name="inner"/> unchanged for the downstream Msg
    /// 102. Leaves the cursor on the first token past the clause.
    /// </summary>
    internal static Selection ParseOptionalForXml(ParserContext context, Selection inner)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.For })
            return inner;

        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not Name xmlKeyword || !Collation.Baseline.Equals(xmlKeyword.Value, "XML"))
        {
            context.RestoreCheckpoint(checkpoint);
            return inner;
        }

        if (context.GetNextRequired() is not Name modeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        ForXmlMode mode;
        string? rowElement;
        Span<char> upper = stackalloc char[modeName.Value.Length];
        _ = modeName.Value.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "AUTO":
                mode = ForXmlMode.Auto;
                rowElement = null;
                context.MoveNextOptional();
                break;
            case "EXPLICIT":
                throw new NotSupportedException("FOR XML EXPLICIT (the universal-table format) isn't modeled; use FOR XML PATH.");
            case "PATH":
                mode = ForXmlMode.Path;
                rowElement = ParseOptionalForXmlElementName(context, "row");
                break;
            case "RAW":
                mode = ForXmlMode.Raw;
                rowElement = ParseOptionalForXmlElementName(context, "row");
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        var elements = false;
        var xsinil = false;
        var rootSpecified = false;
        var rootName = "root";

        while (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not Name optionName)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (Collation.Baseline.Equals(optionName.Value, "ELEMENTS"))
            {
                elements = true;
                context.MoveNextOptional();
                if (context.Token is Name modifier && (Collation.Baseline.Equals(modifier.Value, "XSINIL") || Collation.Baseline.Equals(modifier.Value, "ABSENT")))
                {
                    xsinil = Collation.Baseline.Equals(modifier.Value, "XSINIL");
                    context.MoveNextOptional();
                }
            }
            else if (Collation.Baseline.Equals(optionName.Value, "ROOT"))
            {
                rootSpecified = true;
                context.MoveNextOptional();
                if (context.Token is Operator { Character: '(' })
                {
                    if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } rootLiteral)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    rootName = rootLiteral.Value.AsString;
                    if (rootName.Length == 0)
                        throw SimulatedSqlException.ForXmlEmptyRootTag();
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                }
            }
            else if (Collation.Baseline.Equals(optionName.Value, "BINARY"))
            {
                throw new NotSupportedException("FOR XML BINARY BASE64/HEX isn't modeled; FOR XML PATH base64-encodes binary directly.");
            }
            else if (Collation.Baseline.Equals(optionName.Value, "TYPE"))
            {
                throw new NotSupportedException("FOR XML TYPE (typed-xml node embedding) isn't modeled; the untyped escaped-text nesting is the default.");
            }
            else if (Collation.Baseline.Equals(optionName.Value, "XMLSCHEMA"))
            {
                throw new NotSupportedException("FOR XML XMLSCHEMA (inline XSD emission) isn't modeled.");
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        return WrapForXml(inner, new ForXmlOptions(mode, rowElement, elements, xsinil, rootSpecified ? rootName : null));
    }

    /// <summary>
    /// Reads the optional <c>('name')</c> argument of RAW / PATH, returning the
    /// literal (empty string allowed for PATH) or <paramref name="fallback"/>
    /// when absent. Advances to the first token past the mode / its argument.
    /// </summary>
    private static string ParseOptionalForXmlElementName(ParserContext context, string fallback)
    {
        context.MoveNextOptional();
        if (context.Token is not Operator { Character: '(' })
            return fallback;
        if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } literal)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = literal.Value.AsString;
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return name;
    }

    private static Selection WrapForXml(Selection inner, ForXmlOptions options)
    {
        // AUTO nests secondary tables under the first; that row-collapsing is
        // deferred (PATH covers the same cases), matching FOR JSON AUTO.
        if (options.Mode == ForXmlMode.Auto && (inner.MultipleFromSources || inner.AutoElementName is null))
            throw new NotSupportedException("FOR XML AUTO over a join (nesting secondary tables) isn't modeled; use FOR XML PATH.");

        var rowElement = BuildForXmlRowElement(inner, options);
        var innerSchema = inner.Schema;

        return new Selection([SqlType.Xml], [ForXmlColumnName],
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => SerializeForXml(inner, innerSchema, rowElement, options, batch, outerResolver));
    }

    /// <summary>
    /// Compiles the projection into a single per-row element template (shared
    /// across rows; leaves reference result-column indices). RAW / AUTO build a
    /// flat attribute- or element-centric wrapper; PATH parses each column
    /// alias into an XPath-like node placement. A PATH('') wrapper has an empty
    /// <see cref="ForXmlElement.Name"/> — its content is emitted with no row tag.
    /// </summary>
    private static ForXmlElement BuildForXmlRowElement(Selection inner, ForXmlOptions options)
    {
        if (options.Mode == ForXmlMode.Path)
        {
            var pathRoot = new ForXmlElement(options.RowElement!);
            for (var i = 0; i < inner.ColumnNames.Length; i++)
                InsertForXmlPath(pathRoot, inner.ColumnNames[i], i, options.RowElement!.Length == 0);
            return pathRoot;
        }

        // RAW / AUTO: every column must be named, and binary needs the
        // (unmodeled) BINARY BASE64 option.
        var wrapperName = options.Mode == ForXmlMode.Auto ? inner.AutoElementName! : options.RowElement!;
        var wrapper = new ForXmlElement(wrapperName);
        for (var i = 0; i < inner.ColumnNames.Length; i++)
        {
            var name = inner.ColumnNames[i];
            if (name.Length == 0)
                throw SimulatedSqlException.ForXmlUnnamedColumn();
            if (inner.Schema[i] is BinarySqlType or VarbinarySqlType)
                throw options.Mode == ForXmlMode.Auto ? SimulatedSqlException.ForXmlBinaryAuto(name) : SimulatedSqlException.ForXmlBinaryRaw(name);

            if (options.Elements)
            {
                var element = new ForXmlElement(name);
                element.Content.Add(new ForXmlLeaf(i, atomic: false));
                wrapper.Content.Add(element);
            }
            else
            {
                wrapper.Attributes.Add(new ForXmlAttribute(name, i));
            }
        }
        return wrapper;
    }

    /// <summary>
    /// Places one PATH column into the row-element template by its alias:
    /// <c>@a</c> → attribute, <c>a/b</c> → nested elements, <c>text()</c> /
    /// <c>data()</c> / an unnamed column → text content, a plain name → a leaf
    /// element holding the value. Adjacent same-name element steps merge (so
    /// <c>[x],[x]</c> concatenates and <c>[a/b],[a/c]</c> shares the <c>a</c>
    /// parent). Enforces Msg 6852 (attribute after non-attribute) and Msg 6864
    /// (attribute under a suppressed row tag).
    /// </summary>
    private static void InsertForXmlPath(ForXmlElement root, string alias, int column, bool rowTagOmitted)
    {
        var segments = alias.Length == 0 ? [] : alias.Split('/');
        var atomic = false;
        var attributeName = (string?)null;
        var descendCount = segments.Length;

        if (segments.Length > 0)
        {
            var leaf = segments[^1];
            if (leaf.StartsWith('@'))
            {
                attributeName = leaf[1..];
                descendCount = segments.Length - 1;
            }
            else if (Collation.Baseline.Equals(leaf, "text()"))
            {
                descendCount = segments.Length - 1;
            }
            else if (Collation.Baseline.Equals(leaf, "data()"))
            {
                atomic = true;
                descendCount = segments.Length - 1;
            }
        }

        if (attributeName is not null && rowTagOmitted && descendCount == 0)
            throw SimulatedSqlException.ForXmlAttributeWithoutRowTag();

        // Descend/merge the element steps that precede the leaf.
        var node = root;
        for (var s = 0; s < descendCount; s++)
            node = DescendForXml(node, segments[s]);

        if (attributeName is not null)
        {
            if (node.HasContent)
                throw SimulatedSqlException.ForXmlAttributeAfterNonAttribute("@" + attributeName);
            node.Attributes.Add(new ForXmlAttribute(attributeName, column));
        }
        else
        {
            node.Content.Add(new ForXmlLeaf(column, atomic));
        }
    }

    /// <summary>
    /// Returns the child element named <paramref name="name"/>, reusing the
    /// last content item when it is an element of that name (so contiguous
    /// same-name steps merge) and otherwise appending a fresh child.
    /// </summary>
    private static ForXmlElement DescendForXml(ForXmlElement node, string name)
    {
        if (node.Content.Count > 0 && node.Content[^1] is ForXmlElement last && last.Name == name)
            return last;
        var child = new ForXmlElement(name);
        node.Content.Add(child);
        return child;
    }

    private static IEnumerable<byte[]> SerializeForXml(
        Selection inner, SqlType[] innerSchema, ForXmlElement rowElement,
        ForXmlOptions options, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var sb = new StringBuilder();
        var xsiOnRoot = options.Xsinil && options.RootName is not null;
        var xsiOnTopLevel = options.Xsinil && options.RootName is null;

        if (options.RootName is { } rootName)
        {
            _ = sb.Append('<').Append(rootName);
            if (options.Xsinil)
                _ = sb.Append(" xmlns:xsi=\"").Append(XsiNamespace).Append('"');
            _ = sb.Append('>');
        }

        var any = false;
        var prevAtomic = false;
        foreach (var rowBytes in inner.Execute(batch, outerResolver).RowBytes)
        {
            any = true;
            if (rowElement.Name.Length == 0)
            {
                // PATH('') — no row wrapper; emit the row's content directly.
                prevAtomic = SerializeForXmlContent(sb, rowElement.Content, rowBytes, innerSchema, options, xsiOnTopLevel, prevAtomic);
            }
            else
            {
                SerializeForXmlElement(sb, rowElement, rowBytes, innerSchema, options, xsiOnTopLevel);
                prevAtomic = false;
            }
        }

        if (!any)
            yield break;

        if (options.RootName is { } closeName)
            _ = sb.Append("</").Append(closeName).Append('>');

        yield return RowEncoder.EncodeRow([SqlType.Xml], [SqlValue.FromXml(sb.ToString())]);
    }

    /// <summary>
    /// Serializes one element (open tag, attributes, content, close/self-close)
    /// onto <paramref name="sb"/>. A single-leaf element whose value is NULL is
    /// omitted under the default ABSENT semantics, or rendered as
    /// <c>&lt;name xsi:nil="true"/&gt;</c> under XSINIL.
    /// </summary>
    private static void SerializeForXmlElement(
        StringBuilder sb, ForXmlElement element, byte[] rowBytes, SqlType[] innerSchema, ForXmlOptions options, bool declareXsi)
    {
        if (element.Content.Count == 1 && element.Content[0] is ForXmlLeaf onlyLeaf
            && RowDecoder.DecodeColumn(innerSchema, rowBytes, onlyLeaf.Column).IsNull)
        {
            if (!options.Xsinil)
                return;
            _ = sb.Append('<').Append(element.Name);
            if (declareXsi)
                _ = sb.Append(" xmlns:xsi=\"").Append(XsiNamespace).Append('"');
            _ = sb.Append(" xsi:nil=\"true\"/>");
            return;
        }

        _ = sb.Append('<').Append(element.Name);
        if (declareXsi)
            _ = sb.Append(" xmlns:xsi=\"").Append(XsiNamespace).Append('"');
        foreach (var attribute in element.Attributes)
        {
            var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, attribute.Column);
            if (value.IsNull)
                continue;
            _ = sb.Append(' ').Append(attribute.Name).Append("=\"");
            AppendForXmlText(sb, ScalarForXmlText(value), isAttribute: true);
            _ = sb.Append('"');
        }

        var body = new StringBuilder();
        _ = SerializeForXmlContent(body, element.Content, rowBytes, innerSchema, options, declareXsiOnElements: false, prevAtomic: false);
        if (body.Length == 0)
            _ = sb.Append("/>");
        else
            _ = sb.Append('>').Append(body).Append("</").Append(element.Name).Append('>');
    }

    /// <summary>
    /// Serializes an ordered content list (child elements and text leaves),
    /// threading the <paramref name="prevAtomic"/> flag so adjacent
    /// <c>data()</c> atomic values are space-separated while <c>text()</c>
    /// values concatenate. Returns the trailing atomic state.
    /// </summary>
    private static bool SerializeForXmlContent(
        StringBuilder sb, List<object> content, byte[] rowBytes, SqlType[] innerSchema, ForXmlOptions options, bool declareXsiOnElements, bool prevAtomic)
    {
        foreach (var item in content)
        {
            switch (item)
            {
                case ForXmlElement element:
                    SerializeForXmlElement(sb, element, rowBytes, innerSchema, options, declareXsiOnElements);
                    prevAtomic = false;
                    break;
                case ForXmlLeaf leaf:
                    var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, leaf.Column);
                    if (value.IsNull)
                        break;
                    if (leaf.Atomic && prevAtomic)
                        _ = sb.Append(' ');
                    AppendForXmlText(sb, ScalarForXmlText(value), isAttribute: false);
                    prevAtomic = leaf.Atomic;
                    break;
            }
        }
        return prevAtomic;
    }

    /// <summary>
    /// Appends <paramref name="text"/> with position-dependent XML escaping.
    /// Element content escapes <c>&amp;</c> / <c>&lt;</c> / <c>&gt;</c> and the
    /// carriage return (preserved through parsing); an attribute value also
    /// escapes the double quote, tab, and line feed (attribute-value
    /// normalization). Probe-confirmed against SQL Server 2025.
    /// </summary>
    private static void AppendForXmlText(StringBuilder sb, string text, bool isAttribute)
    {
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => sb.Append("&amp;"),
                '<' => sb.Append("&lt;"),
                '>' => sb.Append("&gt;"),
                '"' when isAttribute => sb.Append("&quot;"),
                '\t' when isAttribute => sb.Append("&#x09;"),
                '\n' when isAttribute => sb.Append("&#x0A;"),
                '\r' => sb.Append("&#x0D;"),
                _ => sb.Append(c),
            };
        }
    }

    /// <summary>
    /// The unescaped XML text form of a non-NULL value. Numeric / date
    /// formatting matches FOR JSON (scientific float, fraction-drop dates)
    /// except <c>bit</c> renders <c>1</c>/<c>0</c>; binary base64-encodes and
    /// <c>uniqueidentifier</c> uppercases. Callers apply the position-dependent
    /// escaping.
    /// </summary>
    private static string ScalarForXmlText(SqlValue value)
    {
        var type = value.Type;
        switch (type)
        {
            case var _ when type == SqlType.Bit:
                return value.AsBoolean ? "1" : "0";
            case SqlVariantSqlType:
                return ScalarForXmlText(value.AsVariantInner);
            case var _ when type == SqlType.Float:
                return value.AsDouble.ToString("0.000000000000000e+000", CultureInfo.InvariantCulture);
            case var _ when type == SqlType.Real:
                return value.AsSingle.ToString("0.0000000e+000", CultureInfo.InvariantCulture);
            case var _ when type == SqlType.Money || type == SqlType.SmallMoney:
                return value.AsMoney.ToString("0.0000", CultureInfo.InvariantCulture);
            case BinarySqlType or VarbinarySqlType:
                return Convert.ToBase64String(value.AsBytes);
            case DateTime2SqlType dt2:
                return ForXmlDateTime(value.AsDateTime2, dt2.precision);
            case var _ when type == SqlType.DateTime:
                return ForXmlDateTime(value.AsDateTime, 3);
            case var _ when type == SqlType.SmallDateTime:
                return ForXmlDateTime(value.AsSmallDateTime, 0);
            case TimeSqlType time:
                {
                    var sb = new StringBuilder();
                    _ = sb.Append(value.AsTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                    AppendForJsonFraction(sb, value.AsTime.Ticks % TimeSpan.TicksPerSecond, time.precision);
                    return sb.ToString();
                }
            case DateTimeOffsetSqlType dto:
                {
                    var offset = value.AsDateTimeOffset;
                    var sb = new StringBuilder();
                    _ = sb.Append(offset.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                    AppendForJsonFraction(sb, offset.Ticks % TimeSpan.TicksPerSecond, dto.precision);
                    _ = sb.Append(offset.ToString("zzz", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            default:
                // int / decimal / date / char / nchar / varchar / nvarchar /
                // uniqueidentifier / everything else: the default string form.
                return value.CoerceTo(SqlType.NVarchar).AsString;
        }
    }

    private static string ForXmlDateTime(DateTime value, int precision)
    {
        var sb = new StringBuilder();
        _ = sb.Append(value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        AppendForJsonFraction(sb, value.Ticks % TimeSpan.TicksPerSecond, precision);
        return sb.ToString();
    }
}

/// <summary>The three modeled FOR XML modes (EXPLICIT is deferred).</summary>
internal enum ForXmlMode
{
    /// <summary>Attribute-centric (default) rows named <c>row</c> / <c>('elem')</c>.</summary>
    Raw,
    /// <summary>Rows named after the source table/alias.</summary>
    Auto,
    /// <summary>Column aliases drive XPath-like node placement (the workhorse).</summary>
    Path,
}

/// <summary>Parsed FOR XML clause options. Immutable, so it rides the cached plan.</summary>
internal sealed class ForXmlOptions(ForXmlMode mode, string? rowElement, bool elements, bool xsinil, string? rootName)
{
    public readonly ForXmlMode Mode = mode;

    /// <summary>The row element name for RAW / PATH ('' suppresses the row tag); null for AUTO.</summary>
    public readonly string? RowElement = rowElement;

    /// <summary>Element-centric serialization (RAW / AUTO); PATH is always element-centric.</summary>
    public readonly bool Elements = elements;

    /// <summary>Emit <c>xsi:nil="true"</c> elements for NULL columns instead of omitting them.</summary>
    public readonly bool Xsinil = xsinil;

    /// <summary>The ROOT wrapper name, or null when no ROOT option was given.</summary>
    public readonly string? RootName = rootName;
}

/// <summary>An attribute placement on a FOR XML element, bound to a result column.</summary>
internal sealed class ForXmlAttribute(string name, int column)
{
    public readonly string Name = name;
    public readonly int Column = column;
}

/// <summary>A text leaf in FOR XML content; <see cref="Atomic"/> marks a <c>data()</c> value (space-joined).</summary>
internal sealed class ForXmlLeaf(int column, bool atomic)
{
    public readonly int Column = column;
    public readonly bool Atomic = atomic;
}

/// <summary>
/// One element in the FOR XML row template: a name, ordered attributes, and
/// ordered content (nested <see cref="ForXmlElement"/> children and
/// <see cref="ForXmlLeaf"/> text leaves). Shared across rows; leaves bind to
/// result-column indices resolved per row at serialization.
/// </summary>
internal sealed class ForXmlElement(string name)
{
    public readonly string Name = name;
    public readonly List<ForXmlAttribute> Attributes = [];
    public readonly List<object> Content = [];

    public bool HasContent => this.Content.Count > 0;
}
