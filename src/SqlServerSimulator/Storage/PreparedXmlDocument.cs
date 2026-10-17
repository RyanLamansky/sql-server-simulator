using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// One document prepared by <c>sp_xml_preparedocument</c> and addressed by an
/// integer handle for the rest of the session. Holds the parsed DOM, the
/// namespace bindings the optional third argument declared, and the edge-table
/// node numbering <c>OPENXML</c> projects when no <c>WITH</c> clause narrows
/// the shape.
/// </summary>
/// <remarks>
/// The rowpattern and every colpattern are XPath 1.0 — the dialect MSXML gives
/// OPENXML — so both run straight through <see cref="XmlNode.SelectNodes(string, XmlNamespaceManager)"/>
/// rather than the XQuery-subset translation <see cref="XmlQueryEngine"/> does
/// for the <c>xml</c> type's own methods.
/// </remarks>
internal sealed class PreparedXmlDocument
{
    /// <summary>The parsed document. A DOM (not an <c>XPathDocument</c>) because the edge table addresses attribute value text nodes, which only the DOM materializes.</summary>
    public readonly XmlDocument Document;

    /// <summary>Prefix bindings from the <c>@xpath_namespaces</c> argument.</summary>
    public readonly XmlNamespaceManager Namespaces;

    /// <summary>Edge-table id per node. <see cref="XmlNode"/> doesn't override equality, so the default comparer is node identity.</summary>
    private readonly Dictionary<XmlNode, long> nodeIds = [];

    private PreparedXmlDocument(XmlDocument document, XmlNamespaceManager namespaces)
    {
        this.Document = document;
        this.Namespaces = namespaces;
        this.AssignNodeIds();
    }

    /// <summary>
    /// Parses <paramref name="xmlText"/> and the optional
    /// <paramref name="namespaceDeclarations"/> wrapper element, whose
    /// <c>xmlns</c> attributes become the prefixes patterns may use. A
    /// malformed document sends <strong>Msg 6601</strong> naming MSXML's error
    /// code and the line it stopped on, then raises <strong>Msg 6602</strong>
    /// with MSXML's description, both attributed to
    /// <c>sp_xml_preparedocument</c>, and no handle is allocated.
    /// </summary>
    public static PreparedXmlDocument Parse(BatchContext batch, string? xmlText, string? namespaceDeclarations)
    {
        // An omitted, NULL or empty document still gets a handle, over a
        // document with no nodes at all — probe-confirmed that every
        // rowpattern then answers zero rows.
        var document = string.IsNullOrEmpty(xmlText) ? new XmlDocument() : LoadOrRaise(batch, xmlText);
        var namespaces = new XmlNamespaceManager(document.NameTable);
        if (namespaceDeclarations is not null && LoadOrRaise(batch, namespaceDeclarations).DocumentElement is { } wrapper)
        {
            foreach (XmlAttribute attribute in wrapper.Attributes)
            {
                if (attribute.Prefix.Equals("xmlns", StringComparison.Ordinal))
                    namespaces.AddNamespace(attribute.LocalName, attribute.Value);
                else if (attribute.LocalName.Equals("xmlns", StringComparison.Ordinal))
                    namespaces.AddNamespace(string.Empty, attribute.Value);
            }
        }

        return new PreparedXmlDocument(document, namespaces);
    }

    private static XmlDocument LoadOrRaise(BatchContext batch, string text)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        try
        {
            document.LoadXml(text);
        }
        catch (XmlException ex)
        {
            var (code, description, line) = MsxmlError(ex, text);
            var notice = SimulatedSqlException.XmlParseErrorNoticeMessage(batch, code, line, line == 0 ? "" : LineText(text, line));
            batch.Connection.PendingMessages.Enqueue(notice);
            throw SimulatedSqlException.XmlDocumentParseFailed(description);
        }
        return document;
    }

    /// <summary>The text of line <paramref name="line"/> (1-based) of <paramref name="text"/>, without its line break.</summary>
    private static string LineText(string text, int line)
    {
        var start = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0)
                return "";
            start = next + 1;
        }
        var end = text.IndexOf('\n', start);
        return text[start..(end < 0 ? text.Length : end)].TrimEnd('\r');
    }

    /// <summary>
    /// MSXML's error code, description and line for the failure .NET's reader
    /// reported as <paramref name="error"/> — line 0 for an error found at the
    /// end of the document — matching what real's
    /// <c>sp_xml_preparedocument</c> sends for each kind of malformation
    /// (probed 2026-10-17 against SQL Server 2025). A failure no probe
    /// matched keeps .NET's message under <c>E_FAIL</c>.
    /// </summary>
    private static (uint Code, string Description, int Line) MsxmlError(XmlException error, string text)
    {
        var message = error.Message;
        var line = error.LineNumber;
        if (Matched(message, @"^Unexpected end of file has occurred\. The following elements are not closed: (.*?)\. Line") is { } unclosed)
        {
            // .NET lists the innermost element first, MSXML the outermost.
            var names = unclosed[1].Value.Split(", ");
            Array.Reverse(names);
            return (0xC00CE553, $"The following tags were not closed: {string.Join(", ", names)}.", 0);
        }
        if (Matched(message, @"^The '(.+?)' start tag on line \d+ position \d+ does not match the end tag of '(.+?)'\.") is { } mismatch)
            return (0xC00CE56D, $"End tag '{mismatch[2].Value}' does not match the start tag '{mismatch[1].Value}'.", line);
        if (message.StartsWith("Data at the root level is invalid.", StringComparison.Ordinal))
        {
            // A start tag .NET refuses there is one MSXML reads to the end of
            // the document: a tag never finished, or elements never closed.
            if (Offset(text, error.LineNumber, error.LinePosition) is { } offset && offset < text.Length && text[offset] == '<')
            {
                var rest = text[offset..];
                if (!rest.Contains('>', StringComparison.Ordinal))
                    return (0xC00CE55E, "Element was not closed.", line);
                var open = new List<string>();
                foreach (Match tag in Regex.Matches(rest, @"<(/?)([^\s/>!?]+)[^>]*?(/?)>", RegexOptions.CultureInvariant))
                {
                    if (tag.Groups[1].Length != 0)
                    {
                        if (open.Count != 0)
                            open.RemoveAt(open.Count - 1);
                    }
                    else if (tag.Groups[3].Length == 0)
                    {
                        open.Add(tag.Groups[2].Value);
                    }
                }
                if (open.Count != 0)
                    return (0xC00CE553, $"The following tags were not closed: {string.Join(", ", open)}.", 0);
            }
            return (0xC00CE556, "Invalid at the top level of the document.", line);
        }
        return message switch
        {
            _ when message.StartsWith("Root element is missing.", StringComparison.Ordinal) => (0xC00CE558, "XML document must have a top level element.", 0),
            _ when Matched(message, @"^'.*' is an unexpected token\. The expected token is '""' or '\x27'\.") is not null => (0xC00CE502, "A string literal was expected, but no opening quote character was found.", line),
            _ when Matched(message, @"^'.*' is an unexpected token\. The expected token is '='\.") is not null => (0xC00CE505, "A name contained an invalid character.", line),
            _ when Matched(message, @"^'.*' is an unexpected token\. Expecting white ?space\.") is not null => (0xC00CE509, "Required white space was missing.", line),
            _ when Matched(message, @"^'.+' is a duplicate attribute name\.") is not null => (0xC00CE554, "Duplicate attribute.", line),
            _ when message.StartsWith("There are multiple root elements.", StringComparison.Ordinal) => (0xC00CE555, "Only one top level element is allowed in an XML document.", line),
            _ when Matched(message, @"^Reference to undeclared entity '(.+?)'\.") is { } entity => (0xC00CE002, $"Reference to undefined entity '{entity[1].Value}'.", line),
            _ when Matched(message, @"^'(.+?)' is an undeclared prefix\.") is { } prefix => (0xC00CE01D, $"Reference to undeclared namespace prefix: '{prefix[1].Value}'.", line),
            _ when message.StartsWith("An XML comment cannot contain '--'", StringComparison.Ordinal) => (0xC00CE503, "Incorrect syntax was used in a comment.", line),
            _ when message.StartsWith("']]>' is not allowed in character data.", StringComparison.Ordinal) => (0xC00CE55C, "The literal string ']]>' is not allowed in element content.", line),
            _ when message.StartsWith("Name cannot begin with the ", StringComparison.Ordinal) => (0xC00CE504, "A name was started with an invalid character.", line),
            _ when message.StartsWith("'<', hexadecimal value 0x3C, is an invalid attribute character.", StringComparison.Ordinal) => (0xC00CE506, "The character '<' cannot be used in an attribute value.", line),
            _ when Matched(message, @"^Invalid syntax for a (hexadecimal|decimal) numeric entity reference\.") is not null => (0xC00CE50E, "An invalid character was found inside an entity reference.", line),
            _ when Matched(message, @"^Version number '.*' is invalid\.") is not null => (0xC00CE57F, "Invalid version number.", line),
            _ when message.StartsWith("Unexpected end of file while parsing CDATA has occurred.", StringComparison.Ordinal) => (0xC00CE564, "A CDATA section was not closed.", line),
            _ when message.StartsWith("Unexpected end of file while parsing Comment has occurred.", StringComparison.Ordinal) => (0xC00CE561, "A comment was not closed.", line),
            _ when message.StartsWith("Invalid namespace declaration.", StringComparison.Ordinal) => (0xC00CE027, "Only a default namespace can have an empty URI.", line),
            _ when message.StartsWith("Unexpected end tag.", StringComparison.Ordinal) => (0xC00CE552, "End tag was not expected at this location.", line),
            _ when message.StartsWith("An error occurred while parsing EntityName.", StringComparison.Ordinal) => (0xC00CE50D, "A semi colon character was expected.", line),
            _ when message.StartsWith("There is an unclosed literal string.", StringComparison.Ordinal) => (0xC00CE560, "A string literal was not closed.", line),
            _ => (0x80004005, message, line),
        };

        static GroupCollection? Matched(string message, string pattern) =>
            Regex.Match(message, pattern, RegexOptions.CultureInvariant) is { Success: true } match ? match.Groups : null;
    }

    /// <summary>The character offset of a 1-based line and position in <paramref name="text"/>, or null past its end.</summary>
    private static int? Offset(string text, int line, int position)
    {
        var start = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0)
                return null;
            start = next + 1;
        }
        return position >= 1 ? start + position - 1 : null;
    }

    /// <summary>
    /// Selects the rowpattern's matches. An XPath the engine refuses is
    /// <strong>Msg 6603</strong>, the error real reports for a rowpattern and a
    /// colpattern alike.
    /// </summary>
    public XmlNodeList SelectRows(string rowPattern) =>
        Select(this.Document, rowPattern, this.Namespaces) ?? this.Document.ChildNodes;

    /// <summary>
    /// Runs one XPath against <paramref name="context"/>, translating the
    /// engine's refusal into Msg 6603.
    /// </summary>
    public static XmlNodeList? Select(XmlNode context, string pattern, XmlNamespaceManager namespaces)
    {
        try
        {
            return context.SelectNodes(pattern, namespaces);
        }
        catch (Exception ex) when (ex is XPathException or ArgumentException)
        {
            throw SimulatedSqlException.XmlPatternParseFailed(ex.Message, pattern);
        }
    }

    /// <summary>The edge-table id assigned to <paramref name="node"/>, or null for one the numbering pass never reached.</summary>
    public long? IdOf(XmlNode? node) => node is not null && this.nodeIds.TryGetValue(node, out var id) ? id : null;

    /// <summary>
    /// Assigns every node its edge-table id, reproducing real's numbering
    /// (probe-confirmed against SQL Server 2025):
    /// <list type="bullet">
    /// <item>the document element is <c>0</c>, and still consumes the counter slot it would otherwise have taken — which is why a document with no prolog numbers its next node <c>2</c>;</item>
    /// <item>nodes preceding the document element are numbered from <c>1</c>;</item>
    /// <item>an element's attributes are numbered immediately after it, before its children;</item>
    /// <item>a text node that would be numbered immediately after its own parent element swaps places with the node numbered next;</item>
    /// <item>attribute value text nodes are numbered last, in document order.</item>
    /// </list>
    /// </summary>
    private void AssignNodeIds()
    {
        var next = 1L;
        XmlNode? deferredText = null;
        var attributeTexts = new List<XmlNode>();

        void Assign(XmlNode node)
        {
            this.nodeIds[node] = next++;
            if (deferredText is not { } pending)
                return;
            deferredText = null;
            this.nodeIds[pending] = next++;
        }

        void Walk(XmlNode node)
        {
            // The XML declaration and the DTD are not modeled as edge-table
            // nodes; see the divergences in docs/claude/xml.md.
            if (node.NodeType is XmlNodeType.XmlDeclaration or XmlNodeType.DocumentType)
                return;

            if (ReferenceEquals(node, this.Document.DocumentElement))
            {
                this.nodeIds[node] = 0;
                next = Math.Max(next + 1, 2);
            }
            else if (IsTextual(node.NodeType))
            {
                // A text node opening its parent element's content is numbered
                // one slot late, behind whichever node the walk reaches next.
                if (node.PreviousSibling is null && node.ParentNode is XmlElement { HasAttributes: false })
                    deferredText = node;
                else
                    Assign(node);
            }
            else
            {
                Assign(node);
            }

            if (node.Attributes is { } attributes)
            {
                foreach (XmlAttribute attribute in attributes)
                {
                    Assign(attribute);
                    if (attribute.FirstChild is { } attributeText)
                        attributeTexts.Add(attributeText);
                }
            }

            foreach (XmlNode child in node.ChildNodes)
                Walk(child);
        }

        foreach (XmlNode child in this.Document.ChildNodes)
            Walk(child);

        // A deferred text node with nothing after it still lands at the tail of
        // the main pass, ahead of the attribute value text nodes.
        if (deferredText is { } trailing)
            this.nodeIds[trailing] = next++;
        foreach (var attributeText in attributeTexts)
            this.nodeIds[attributeText] = next++;
    }

    /// <summary>Whether the node kind carries character data — every one of these reports edge-table nodetype 3.</summary>
    public static bool IsTextual(XmlNodeType type) =>
        type is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

    /// <summary>
    /// Serializes <paramref name="node"/> the way <c>@mp:xmltext</c> reports it
    /// — empty elements self-close with no space, text and attribute values
    /// take the position-dependent escaping <c>FOR XML</c> applies — omitting
    /// every node in <paramref name="consumed"/> (the flag-8 "not consumed"
    /// overflow; an empty set gives the plain outer XML).
    /// </summary>
    public static string Serialize(XmlNode node, HashSet<XmlNode> consumed)
    {
        var sb = new StringBuilder();
        Write(node);
        return sb.ToString();

        void Write(XmlNode current)
        {
            if (consumed.Contains(current))
                return;
            switch (current.NodeType)
            {
                case XmlNodeType.Element:
                    _ = sb.Append('<').Append(current.Name);
                    foreach (XmlAttribute attribute in current.Attributes!)
                    {
                        if (consumed.Contains(attribute))
                            continue;
                        _ = sb.Append(' ').Append(attribute.Name).Append("=\"");
                        Selection.AppendForXmlText(sb, attribute.Value, isAttribute: true);
                        _ = sb.Append('"');
                    }
                    if (!current.HasChildNodes)
                    {
                        _ = sb.Append("/>");
                        break;
                    }
                    _ = sb.Append('>');
                    foreach (XmlNode child in current.ChildNodes)
                        Write(child);
                    _ = sb.Append("</").Append(current.Name).Append('>');
                    break;
                case XmlNodeType.Attribute:
                    _ = sb.Append(current.Name).Append("=\"");
                    Selection.AppendForXmlText(sb, current.Value!, isAttribute: true);
                    _ = sb.Append('"');
                    break;
                case XmlNodeType.Comment:
                    _ = sb.Append("<!--").Append(current.Value).Append("-->");
                    break;
                case XmlNodeType.ProcessingInstruction:
                    _ = sb.Append("<?").Append(current.Name).Append(' ').Append(current.Value).Append("?>");
                    break;
                case XmlNodeType.XmlDeclaration:
                case XmlNodeType.DocumentType:
                    break;
                default:
                    Selection.AppendForXmlText(sb, current.Value ?? string.Empty, isAttribute: false);
                    break;
            }
        }
    }
}
