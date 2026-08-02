using System.Globalization;
using System.Text;
using System.Xml;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Serializes a <c>FOR XML EXPLICIT</c> universal table: every row opens
    /// exactly one element, named by its <c>Tag</c> value, under whichever
    /// still-open element its <c>Parent</c> value names (NULL / 0 = document
    /// level). Rows therefore have to arrive in tree order — nothing is
    /// reordered, and a parent that isn't open is real's Msg 6833 rather than a
    /// rearrangement. Everything below the named parent closes first, so a row
    /// for an outer tag ends the inner elements the preceding rows opened.
    /// </summary>
    private static IEnumerable<byte[]> SerializeForXmlExplicit(
        Selection inner, SqlType[] innerSchema, ForXmlExplicitPlan plan,
        ForXmlOptions options, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var sb = new StringBuilder();
        var topLevelDeclarations = options.RootName is null ? options.Declarations : "";

        if (options.RootName is { } rootName)
            _ = sb.Append('<').Append(rootName).Append(options.Declarations).Append('>');

        // Outermost first. A frame's body opens lazily so an element with
        // neither content nor children still self-closes.
        var open = new List<ForXmlExplicitFrame>();
        var any = false;

        foreach (var rowBytes in inner.Execute(batch, outerResolver).RowBytes)
        {
            any = true;
            var tagValue = RowDecoder.DecodeColumn(innerSchema, rowBytes, 0);
            if (tagValue.IsNull || tagValue.AsInt32 <= 0)
                throw SimulatedSqlException.ForXmlExplicitTagColumn(2);
            var parentValue = RowDecoder.DecodeColumn(innerSchema, rowBytes, 1);
            if (!parentValue.IsNull && parentValue.AsInt32 < 0)
                throw SimulatedSqlException.ForXmlExplicitParentColumn(2);

            var tagId = tagValue.AsInt32;
            if (!plan.Tags.TryGetValue(tagId, out var tag))
                throw SimulatedSqlException.ForXmlExplicitUndeclaredTag(tagId);

            var keep = 0;
            // A NULL or zero parent is the document level; anything else has to
            // name a tag some enclosing element still holds open.
            if (!parentValue.IsNull && parentValue.AsInt32 != 0)
            {
                var parentId = parentValue.AsInt32;
                if (!plan.Tags.ContainsKey(parentId))
                    throw SimulatedSqlException.ForXmlExplicitUndeclaredParentTag(parentId);
                keep = OpenFrameDepth(open, parentId);
                if (keep == 0)
                    throw SimulatedSqlException.ForXmlExplicitParentNotOpen(parentId);
            }

            for (var i = open.Count - 1; i >= keep; i--)
            {
                CloseForXmlExplicitFrame(sb, open[i]);
                open.RemoveAt(i);
            }

            if (OpenFrameDepth(open, tagId) > 0)
                throw SimulatedSqlException.ForXmlExplicitCircularTags();

            // The overflow column contributes attributes to the open tag and
            // content to its body, so it is read once and used twice.
            var overflow = tag.XmlText is null ? null : ReadForXmlExplicitOverflow(tag.XmlText, rowBytes, innerSchema);

            if (open.Count > 0)
                StartForXmlExplicitBody(sb, open[^1]);
            _ = sb.Append('<').Append(tag.Name);
            if (open.Count == 0)
                _ = sb.Append(topLevelDeclarations);
            AppendForXmlExplicitAttributes(sb, tag, rowBytes, innerSchema, overflow);

            var frame = new ForXmlExplicitFrame(tagId, tag.Name);
            open.Add(frame);

            var body = new StringBuilder();
            AppendForXmlExplicitContent(body, tag, rowBytes, innerSchema, options, overflow);
            // A materialized overflow keeps the element open even when it
            // contributed nothing (probe-confirmed: <e a="1"></e>).
            if (body.Length > 0 || overflow is not null)
            {
                StartForXmlExplicitBody(sb, frame);
                _ = sb.Append(body);
            }
        }

        if (!any)
        {
            if (options.Typed)
                yield return EmptyForXmlRow();
            yield break;
        }

        for (var i = open.Count - 1; i >= 0; i--)
            CloseForXmlExplicitFrame(sb, open[i]);
        if (options.RootName is { } closeName)
            _ = sb.Append("</").Append(closeName).Append('>');

        yield return ForXmlRow(sb, options);
    }

    /// <summary>
    /// How many frames stand at and above the open element for
    /// <paramref name="tagId"/> — 0 when no open element carries that tag, so
    /// the answer doubles as "is it open" and as the depth to keep.
    /// </summary>
    private static int OpenFrameDepth(List<ForXmlExplicitFrame> open, int tagId)
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            if (open[i].TagId == tagId)
                return i + 1;
        }
        return 0;
    }

    /// <summary>Writes the <c>&gt;</c> that ends an open element's start tag, once.</summary>
    private static void StartForXmlExplicitBody(StringBuilder sb, ForXmlExplicitFrame frame)
    {
        if (frame.BodyStarted)
            return;
        _ = sb.Append('>');
        frame.BodyStarted = true;
    }

    private static void CloseForXmlExplicitFrame(StringBuilder sb, ForXmlExplicitFrame frame) =>
        _ = frame.BodyStarted ? sb.Append("</").Append(frame.Name).Append('>') : sb.Append("/>");

    /// <summary>
    /// Appends the tag's attribute columns for one row (NULLs omitted, as
    /// everywhere in FOR XML), then whatever attributes an unnamed
    /// <c>xmltext</c> overflow element carried — real merges those onto the
    /// parent, skipping any whose name the row already wrote.
    /// </summary>
    private static void AppendForXmlExplicitAttributes(
        StringBuilder sb, ForXmlExplicitTag tag, byte[] rowBytes, SqlType[] innerSchema, ForXmlExplicitOverflow? overflow)
    {
        var written = (List<string>?)null;
        foreach (var column in tag.Attributes)
        {
            var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, column.Column);
            if (value.IsNull)
                continue;
            _ = sb.Append(' ').Append(column.Name).Append("=\"");
            AppendForXmlText(sb, ScalarForXmlText(value), isAttribute: true);
            _ = sb.Append('"');
            if (overflow is { Merged: true })
                (written ??= []).Add(column.Name);
        }

        if (overflow is not { Merged: true })
            return;
        foreach (var (name, value) in overflow.Attributes)
        {
            if (written?.Contains(name) != true)
                _ = sb.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
        }
    }

    /// <summary>
    /// Appends the tag's content columns for one row in select-list order —
    /// child elements, bare text, CDATA sections, raw <c>xml</c> passthrough
    /// and the <c>xmltext</c> overflow each render per their directive.
    /// </summary>
    private static void AppendForXmlExplicitContent(
        StringBuilder sb, ForXmlExplicitTag tag, byte[] rowBytes, SqlType[] innerSchema,
        ForXmlOptions options, ForXmlExplicitOverflow? overflow)
    {
        foreach (var column in tag.Content)
        {
            if (column.Content == ForXmlExplicitContent.XmlText)
            {
                if (overflow is null)
                    continue;
                if (overflow.Merged)
                    _ = sb.Append(overflow.Content);
                else
                    AppendForXmlExplicitOverflowElement(sb, column.Name, overflow);
                continue;
            }

            var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, column.Column);
            if (value.IsNull)
            {
                // Only the xsinil form marks a NULL; every other shape omits it.
                if (column.Content == ForXmlExplicitContent.ElementXsinil)
                    _ = sb.Append('<').Append(column.Name).Append(" xsi:nil=\"true\"/>");
                continue;
            }

            var named = column.Name.Length > 0 && column.Content != ForXmlExplicitContent.Text;
            if (named)
                _ = sb.Append('<').Append(column.Name).Append('>');
            switch (column.Content)
            {
                case ForXmlExplicitContent.Cdata:
                    AppendForXmlCdata(sb, ScalarForXmlText(value));
                    break;
                case ForXmlExplicitContent.Xml:
                    // The xml directive is a passthrough: no escaping, no
                    // well-formedness check (probe-confirmed).
                    _ = sb.Append(ScalarForXmlText(value));
                    break;
                default:
                    if (innerSchema[column.Column] is XmlSqlType)
                        _ = sb.Append(ScalarForXmlText(value));
                    else
                        AppendForXmlText(sb, ForXmlColumnText(value, column.Column, rowBytes, innerSchema, options), isAttribute: false);
                    break;
            }
            if (named)
                _ = sb.Append("</").Append(column.Name).Append('>');
        }
    }

    /// <summary>
    /// Appends <paramref name="text"/> as CDATA sections. Real can't escape
    /// inside one, so it breaks the section apart at every <c>]]&gt;</c>,
    /// splitting after the first <c>]</c> — <c>a]]&gt;b</c> comes back as
    /// <c>&lt;![CDATA[a]]]&gt;&lt;![CDATA[]&gt;b]]&gt;</c> (probe-confirmed).
    /// </summary>
    private static void AppendForXmlCdata(StringBuilder sb, string text)
    {
        _ = sb.Append("<![CDATA[");
        var start = 0;
        for (var split = text.IndexOf("]]>", StringComparison.Ordinal); split >= 0; split = text.IndexOf("]]>", start, StringComparison.Ordinal))
        {
            _ = sb.Append(text, start, split + 1 - start).Append("]]><![CDATA[");
            start = split + 1;
        }
        _ = sb.Append(text, start, text.Length - start).Append("]]>");
    }

    /// <summary>
    /// Renders a <em>named</em> <c>xmltext</c> overflow: the overflow element
    /// keeps its attributes but takes the column's own name, so
    /// <c>[e!1!ov!xmltext]</c> over <c>&lt;over x="1"&gt;t&lt;/over&gt;</c>
    /// answers <c>&lt;ov x="1"&gt;t&lt;/ov&gt;</c>.
    /// </summary>
    private static void AppendForXmlExplicitOverflowElement(StringBuilder sb, string name, ForXmlExplicitOverflow overflow)
    {
        _ = sb.Append('<').Append(name);
        foreach (var (attributeName, value) in overflow.Attributes)
            _ = sb.Append(' ').Append(attributeName).Append("=\"").Append(value).Append('"');
        _ = overflow.Content.Length == 0
            ? sb.Append("/>")
            : sb.Append('>').Append(overflow.Content).Append("</").Append(name).Append('>');
    }

    /// <summary>
    /// Reads an <c>xmltext</c> column's value into its root element's
    /// attributes and the text of that element's content. Real reproduces both
    /// as they were written — the content byte for byte, insignificant
    /// whitespace and all, and each attribute value's source text with only the
    /// delimiter normalized to <c>"</c> (so a <c>&gt;</c> stays literal, an
    /// entity stays an entity, and a <c>"</c> from a single-quoted value comes
    /// back unescaped) — so the whole thing is sliced out of the written value
    /// rather than parsed and re-serialized. A NULL value contributes nothing;
    /// anything that isn't a document with a root element is Msg 6834.
    /// </summary>
    private static ForXmlExplicitOverflow? ReadForXmlExplicitOverflow(ForXmlExplicitColumn column, byte[] rowBytes, SqlType[] innerSchema)
    {
        var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, column.Column);
        if (value.IsNull)
            return null;

        // The reader answers only the two questions the slicing can't: whether
        // the value is well formed, and whether it holds an element at all.
        var text = ScalarForXmlText(value);
        bool hasElement;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            hasElement = reader.MoveToContent() == XmlNodeType.Element;
        }
        catch (XmlException)
        {
            throw SimulatedSqlException.ForXmlExplicitXmlTextInvalid(column.Name, 2);
        }

        return hasElement
            ? SliceForXmlExplicitOverflow(text, column.Name.Length == 0)
            : throw SimulatedSqlException.ForXmlExplicitXmlTextInvalid(column.Name, 1);
    }

    /// <summary>
    /// Slices a validated overflow value into its root element's attributes
    /// (name plus the raw text between the quotes) and the raw text of its
    /// content. The element being well formed, its end tag is the last one
    /// bearing its name; a self-closing root has no content.
    /// </summary>
    private static ForXmlExplicitOverflow SliceForXmlExplicitOverflow(string text, bool merged)
    {
        var i = text.IndexOf('<', StringComparison.Ordinal) + 1;
        var nameEnd = i;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('/' or '>'))
            nameEnd++;
        var name = text[i..nameEnd];

        var attributes = new List<(string Name, string Value)>();
        var at = nameEnd;
        while (true)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            if (at >= text.Length || text[at] is '/' or '>')
                break;

            var attributeStart = at;
            while (text[at] is not '=' && !char.IsWhiteSpace(text[at]))
                at++;
            var attributeName = text[attributeStart..at];
            while (text[at] is not ('"' or '\''))
                at++;
            var quote = text[at];
            var valueStart = ++at;
            while (text[at] != quote)
                at++;
            attributes.Add((attributeName, text[valueStart..at++]));
        }

        if (at >= text.Length || text[at] == '/')
            return new ForXmlExplicitOverflow(attributes, "", merged);
        var end = text.LastIndexOf("</" + name, StringComparison.Ordinal);
        return new ForXmlExplicitOverflow(attributes, end > at ? text[(at + 1)..end] : "", merged);
    }
}

/// <summary>One element a <c>FOR XML EXPLICIT</c> row opened and hasn't closed.</summary>
internal sealed class ForXmlExplicitFrame(int tagId, string name)
{
    public readonly int TagId = tagId;
    public readonly string Name = name;

    /// <summary>Whether the start tag's <c>&gt;</c> has been written — until it is, the element can still self-close.</summary>
    public bool BodyStarted;
}

/// <summary>
/// An <c>xmltext</c> column's value, split into the overflow root element's
/// attributes and its content — both as written, ready to append unescaped.
/// <see cref="Merged"/> marks the unnamed form, which folds both onto the tag's
/// own element instead of building a child.
/// </summary>
internal sealed class ForXmlExplicitOverflow(List<(string Name, string Value)> attributes, string content, bool merged)
{
    public readonly List<(string Name, string Value)> Attributes = attributes;
    public readonly string Content = content;
    public readonly bool Merged = merged;
}

/// <summary>The directives a <c>FOR XML EXPLICIT</c> column name may carry past its attribute name.</summary>
internal enum ForXmlExplicitDirective
{
    /// <summary>Wrap the value in a CDATA section.</summary>
    Cdata,

    /// <summary>Emit a child element instead of an attribute.</summary>
    Element,

    /// <summary>As <see cref="Element"/>, marking a NULL with <c>xsi:nil</c>.</summary>
    ElementXsinil,

    /// <summary>Declare the tag but emit nothing (a sort or join key).</summary>
    Hide,

    /// <summary><c>id</c> / <c>idref</c> / <c>nmtoken</c> — an ordinary attribute outside an inline schema.</summary>
    Identity,

    /// <summary><c>idrefs</c> / <c>nmtokens</c> — the list forms, which carry their own shape rule.</summary>
    IdentityList,

    /// <summary>Emit the value's own markup unescaped.</summary>
    Xml,

    /// <summary>Fold an overflow element's attributes and content into the tag's element.</summary>
    XmlText,
}

/// <summary>How one <c>FOR XML EXPLICIT</c> data column renders.</summary>
internal enum ForXmlExplicitContent
{
    /// <summary>An attribute on the tag's element — the default, and what the identity directives also give.</summary>
    Attribute,

    /// <summary>A child element holding the value (the <c>element</c> directive).</summary>
    Element,

    /// <summary>As <see cref="Element"/>, but a NULL emits <c>&lt;name xsi:nil="true"/&gt;</c>.</summary>
    ElementXsinil,

    /// <summary>Text directly in the tag's element — an absent or empty attribute name.</summary>
    Text,

    /// <summary>A CDATA section (the <c>cdata</c> directive), wrapped in a child element when named.</summary>
    Cdata,

    /// <summary>The value's own markup, unescaped and unchecked (the <c>xml</c> directive).</summary>
    Xml,

    /// <summary>The <c>xmltext</c> overflow element, merged onto the tag when unnamed.</summary>
    XmlText,
}

/// <summary>One data column of a <c>FOR XML EXPLICIT</c> universal table.</summary>
internal sealed class ForXmlExplicitColumn(int column, string name, ForXmlExplicitContent content)
{
    /// <summary>The result-column index the value is read from.</summary>
    public readonly int Column = column;

    /// <summary>The attribute / element name as written (empty for the unnamed forms), emitted verbatim.</summary>
    public readonly string Name = name;

    public readonly ForXmlExplicitContent Content = content;
}

/// <summary>
/// One tag number's element template: its name and the columns that write its
/// attributes and its content, each in select-list order. Attributes always
/// precede content in the output whatever the written order, since they belong
/// to the start tag.
/// </summary>
internal sealed class ForXmlExplicitTag(string name)
{
    public readonly string Name = name;
    public readonly List<ForXmlExplicitColumn> Attributes = [];
    public readonly List<ForXmlExplicitColumn> Content = [];

    /// <summary>The tag's single <c>xmltext</c> column, if it declared one (Msg 6827 refuses a second).</summary>
    public ForXmlExplicitColumn? XmlText;
}

/// <summary>
/// The compiled shape of a <c>FOR XML EXPLICIT</c> projection: one
/// <see cref="ForXmlExplicitTag"/> per declared tag number, built once per plan
/// from the universal table's column names. Immutable after
/// <see cref="Build"/>, so it rides the cached plan.
/// </summary>
internal sealed class ForXmlExplicitPlan(Dictionary<int, ForXmlExplicitTag> tags, bool xsinil)
{
    public readonly Dictionary<int, ForXmlExplicitTag> Tags = tags;

    /// <summary>Whether any column carries <c>elementxsinil</c> — what puts the <c>xsi</c> declaration on the outermost element.</summary>
    public readonly bool Xsinil = xsinil;

    /// <summary>
    /// Compiles <paramref name="inner"/>'s projection, raising real's own
    /// diagnostics in real's own order (probe-confirmed against SQL Server
    /// 2025): the binary-column scan first, then the three-column minimum, the
    /// <c>Tag</c> / <c>Parent</c> types and names, and finally each data
    /// column's name convention.
    /// </summary>
    internal static ForXmlExplicitPlan Build(Selection inner, bool binaryBase64)
    {
        var schema = inner.Schema;
        var names = inner.ColumnNames;

        // Real scans the whole projection for binary before looking at the
        // universal table's shape at all — a binary column reports Msg 6829
        // even where the projection is too short or a name is malformed.
        if (!binaryBase64)
        {
            for (var i = 0; i < schema.Length; i++)
            {
                if (schema[i] is BinarySqlType or VarbinarySqlType or ImageSqlType)
                    throw SimulatedSqlException.ForXmlBinaryRaw(names[i]);
            }
        }

        if (names.Length < 3)
            throw SimulatedSqlException.ForXmlExplicitNeedsThreeColumns();
        if (schema[0] != SqlType.Int32)
            throw SimulatedSqlException.ForXmlExplicitTagColumn(1);
        if (schema[1] != SqlType.Int32)
            throw SimulatedSqlException.ForXmlExplicitParentColumn(1);
        if (!Collation.Baseline.Equals(names[0], "TAG"))
            throw SimulatedSqlException.ForXmlExplicitColumnMisnamed(1, "TAG", names[0]);
        if (!Collation.Baseline.Equals(names[1], "PARENT"))
            throw SimulatedSqlException.ForXmlExplicitColumnMisnamed(2, "PARENT", names[1]);

        var tags = new Dictionary<int, ForXmlExplicitTag>();
        var xsinil = false;
        for (var i = 2; i < names.Length; i++)
            xsinil |= AddColumn(tags, names[i], i, schema[i]);
        return new ForXmlExplicitPlan(tags, xsinil);
    }

    /// <summary>
    /// Parses one <c>ElementName!TagNumber[!AttributeName[!Directive…]]</c>
    /// column name into its tag's template, returning whether it carried
    /// <c>elementxsinil</c>. The element name and the attribute name reach the
    /// output verbatim — EXPLICIT neither escapes them the way RAW / AUTO do
    /// nor rejects them the way PATH does.
    /// </summary>
    private static bool AddColumn(Dictionary<int, ForXmlExplicitTag> tags, string name, int column, SqlType columnType)
    {
        var segments = name.Split('!');
        if (segments.Length < 2 || segments[0].Length == 0 || !IsTagNumber(segments[1], out var tagId))
            throw SimulatedSqlException.ForXmlExplicitInvalidColumnName(name);

        var attributeName = segments.Length > 2 ? segments[2] : "";
        var content = attributeName.Length == 0 ? ForXmlExplicitContent.Text : ForXmlExplicitContent.Attribute;
        var contentDirectives = 0;
        var idDirectives = 0;
        var hideDirectives = 0;
        var idrefs = false;

        for (var s = 3; s < segments.Length; s++)
        {
            switch (ParseDirective(segments[s]) ?? throw SimulatedSqlException.ForXmlExplicitInvalidDirective(segments[s]))
            {
                case ForXmlExplicitDirective.Cdata:
                    contentDirectives++;
                    content = ForXmlExplicitContent.Cdata;
                    break;
                case ForXmlExplicitDirective.Element:
                    contentDirectives++;
                    content = attributeName.Length == 0 ? ForXmlExplicitContent.Text : ForXmlExplicitContent.Element;
                    break;
                case ForXmlExplicitDirective.ElementXsinil:
                    contentDirectives++;
                    content = ForXmlExplicitContent.ElementXsinil;
                    break;
                case ForXmlExplicitDirective.Hide:
                    hideDirectives++;
                    break;
                case ForXmlExplicitDirective.Identity:
                    idDirectives++;
                    break;
                case ForXmlExplicitDirective.IdentityList:
                    idDirectives++;
                    idrefs = true;
                    break;
                case ForXmlExplicitDirective.Xml:
                    contentDirectives++;
                    content = ForXmlExplicitContent.Xml;
                    break;
                default:
                    contentDirectives++;
                    content = ForXmlExplicitContent.XmlText;
                    break;
            }
        }

        // Real's own check order, probed one combination at a time.
        if (hideDirectives > 1)
            throw SimulatedSqlException.ForXmlExplicitDuplicateHide(name);
        if (idDirectives > 1)
            throw SimulatedSqlException.ForXmlExplicitConflictingIdDirectives(name);
        if (contentDirectives > 1)
            throw SimulatedSqlException.ForXmlExplicitConflictingDirectives(name);
        if (hideDirectives > 0 && idDirectives > 0)
            throw SimulatedSqlException.ForXmlExplicitIdCannotHide(name);
        // Real admits an idrefs / nmtokens column only where its expression is
        // statically nullable, feeding one value per row into a merged
        // attribute; short of that nullability it reports this.
        if (idrefs)
            throw SimulatedSqlException.ForXmlExplicitIdrefsNeedsSeparateSelect();

        if (!tags.TryGetValue(tagId, out var tag))
        {
            tag = new ForXmlExplicitTag(segments[0]);
            tags.Add(tagId, tag);
        }
        else if (!string.Equals(tag.Name, segments[0], StringComparison.Ordinal))
        {
            throw SimulatedSqlException.ForXmlExplicitTagRedeclared(tagId, tag.Name, segments[0]);
        }

        // A hidden column declares its tag and then contributes nothing.
        if (hideDirectives > 0)
            return false;

        // An xml value serializes as nodes, which an attribute can't hold, so
        // it becomes a child element instead — the same rule RAW and AUTO take.
        if (content == ForXmlExplicitContent.Attribute && columnType is XmlSqlType)
            content = ForXmlExplicitContent.Element;

        var parsed = new ForXmlExplicitColumn(column, attributeName, content);
        switch (content)
        {
            case ForXmlExplicitContent.Attribute:
                tag.Attributes.Add(parsed);
                break;
            case ForXmlExplicitContent.XmlText:
                if (tag.XmlText is not null)
                    throw SimulatedSqlException.ForXmlExplicitDuplicateXmlText(name);
                tag.XmlText = parsed;
                tag.Content.Add(parsed);
                break;
            default:
                tag.Content.Add(parsed);
                break;
        }
        return content == ForXmlExplicitContent.ElementXsinil;
    }

    /// <summary>
    /// Maps one written directive to its kind (case-insensitively, as real
    /// reads them), or null when the word is none of them — Msg 6824. The
    /// identity directives collapse to two kinds because they behave alike:
    /// <c>id</c> / <c>idref</c> / <c>nmtoken</c> serialize as an ordinary
    /// attribute, while <c>idrefs</c> / <c>nmtokens</c> carry the list rule.
    /// </summary>
    private static ForXmlExplicitDirective? ParseDirective(string text)
    {
        Span<char> lower = stackalloc char[text.Length];
        _ = text.AsSpan().ToLowerInvariant(lower);
        return lower switch
        {
            "cdata" => ForXmlExplicitDirective.Cdata,
            "element" => ForXmlExplicitDirective.Element,
            "elementxsinil" => ForXmlExplicitDirective.ElementXsinil,
            "hide" => ForXmlExplicitDirective.Hide,
            "id" => ForXmlExplicitDirective.Identity,
            "idref" => ForXmlExplicitDirective.Identity,
            "idrefs" => ForXmlExplicitDirective.IdentityList,
            "nmtoken" => ForXmlExplicitDirective.Identity,
            "nmtokens" => ForXmlExplicitDirective.IdentityList,
            "xml" => ForXmlExplicitDirective.Xml,
            "xmltext" => ForXmlExplicitDirective.XmlText,
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a tag number: decimal digits denoting
    /// a positive value. A sign, a space or anything else makes the whole
    /// column name invalid rather than the number.
    /// </summary>
    private static bool IsTagNumber(string text, out int tagId)
    {
        tagId = 0;
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out tagId) && tagId > 0;
    }
}
