using System.Text;
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
    /// malformed document raises <strong>Msg 6602</strong> attributed to
    /// <c>sp_xml_preparedocument</c>, and no handle is allocated.
    /// </summary>
    public static PreparedXmlDocument Parse(string? xmlText, string? namespaceDeclarations)
    {
        // An omitted or NULL document still gets a handle, over a document with
        // no nodes at all — probe-confirmed that every rowpattern then answers
        // zero rows.
        var document = xmlText is null ? new XmlDocument() : LoadOrRaise(xmlText);
        var namespaces = new XmlNamespaceManager(document.NameTable);
        if (namespaceDeclarations is not null && LoadOrRaise(namespaceDeclarations).DocumentElement is { } wrapper)
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

    private static XmlDocument LoadOrRaise(string text)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        try
        {
            document.LoadXml(text);
        }
        catch (XmlException ex)
        {
            throw SimulatedSqlException.XmlDocumentParseFailed(ex.Message);
        }
        return document;
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
