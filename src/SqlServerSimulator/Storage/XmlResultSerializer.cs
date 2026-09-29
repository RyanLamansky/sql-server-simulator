using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Serializes the nodes a <c>.query()</c> returns the way SQL Server writes
/// them: empty elements self-close with no space before the slash, nothing is
/// indented, and every namespace a name needs is declared where it is first
/// used — which <see cref="XPathNavigator.OuterXml"/> gets wrong on all three
/// counts.
/// </summary>
/// <remarks>
/// <para>
/// A selected node is written out of its document, so a namespace declared on
/// one of its ancestors is no longer in scope. Real re-binds each such
/// namespace at the element or attribute that uses it, choosing the prefix in
/// this order (probed 2026-09-28 against SQL Server 2025):
/// </para>
/// <list type="number">
/// <item>a declaration inside the written subtree, kept as written;</item>
/// <item>the query's own prolog — <c>declare namespace p = "…"</c>, or the
/// default element namespace for an element name;</item>
/// <item>a generated <c>p1</c>, <c>p2</c>, … counted across the whole result,
/// skipping any prefix already bound. Once <c>p</c> itself is bound the
/// generator moves to <c>p10</c>, <c>p11</c>, ….</item>
/// </list>
/// <para>
/// So <c>/r/a</c> over <c>&lt;r xmlns:p="urn:p"&gt;&lt;a&gt;&lt;p:b/&gt;&lt;/a&gt;&lt;/r&gt;</c>
/// is <c>&lt;a&gt;&lt;p1:b xmlns:p1="urn:p"/&gt;&lt;/a&gt;</c>, and a namespace
/// declared but never used on the way down isn't written at all. An element's
/// own binding is written before its attributes; an attribute's lands just
/// ahead of the attribute.
/// </para>
/// </remarks>
internal sealed class XmlResultSerializer(string? defaultElementNamespace, Dictionary<string, string>? prologPrefixes)
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private readonly string? defaultElementNamespace = defaultElementNamespace;
    private readonly Dictionary<string, string>? prologPrefixes = prologPrefixes;

    /// <summary>The next generated prefix's number, or -1 before the first.</summary>
    private int counter = -1;

    /// <summary>Writes one node and its subtree.</summary>
    public void Append(StringBuilder text, XPathNavigator node) => this.AppendNode(text, node, []);

    private void AppendNode(StringBuilder text, XPathNavigator node, Dictionary<string, string> scope)
    {
        switch (node.NodeType)
        {
            case XPathNodeType.Comment:
                _ = text.Append("<!--").Append(node.Value).Append("-->");
                return;
            case XPathNodeType.ProcessingInstruction:
                _ = text.Append("<?").Append(node.Name);
                if (node.Value.Length > 0)
                    _ = text.Append(' ').Append(node.Value);
                _ = text.Append("?>");
                return;
            case XPathNodeType.Attribute:
                // Only reached for an attribute spliced into an attribute-free
                // position, which the callers refuse first; written plainly.
                _ = text.Append(node.LocalName).Append("=\"");
                Parser.Selection.AppendForXmlText(text, node.Value, isAttribute: true);
                _ = text.Append('"');
                return;
            case XPathNodeType.Element:
                this.AppendElement(text, node, scope);
                return;
            case XPathNodeType.Root:
                // The instance's own root, which `/` and a `..` off a top-level
                // node reach: real serializes its content, so a fragment comes
                // back as the several top-level nodes it holds.
                var top = node.Clone();
                if (!top.MoveToFirstChild())
                    return;
                do
                {
                    this.AppendNode(text, top, scope);
                }
                while (top.MoveToNext());
                return;
            default:
                Parser.Selection.AppendForXmlText(text, node.Value, isAttribute: false);
                return;
        }
    }

    private void AppendElement(StringBuilder text, XPathNavigator node, Dictionary<string, string> inherited)
    {
        var scope = new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        var declarations = new StringBuilder();

        // The element's own declarations are part of the subtree and stay as
        // written.
        var own = node.GetNamespacesInScope(XmlNamespaceScope.Local);
        foreach (var (prefix, uri) in own)
        {
            AppendDeclaration(declarations, prefix, uri);
            scope[prefix] = uri;
        }

        var name = node.LocalName;
        var elementUri = node.NamespaceURI;
        if (elementUri.Length == 0)
        {
            if (scope.TryGetValue(string.Empty, out var inScopeDefault) && inScopeDefault.Length > 0)
            {
                AppendDeclaration(declarations, string.Empty, string.Empty);
                scope[string.Empty] = string.Empty;
            }
        }
        else if (this.Bind(scope, declarations, elementUri, node.Prefix, isElement: true) is var prefix && prefix.Length > 0)
        {
            name = $"{prefix}:{name}";
        }

        _ = text.Append('<').Append(name).Append(declarations);

        var attribute = node.Clone();
        if (attribute.MoveToFirstAttribute())
        {
            do
            {
                var attributeName = attribute.LocalName;
                var attributeUri = attribute.NamespaceURI;
                if (attributeUri == XmlNamespace)
                {
                    attributeName = "xml:" + attributeName;
                }
                else if (attributeUri.Length > 0)
                {
                    var bindings = new StringBuilder();
                    var attributePrefix = this.Bind(scope, bindings, attributeUri, attribute.Prefix, isElement: false);
                    _ = text.Append(bindings);
                    attributeName = $"{attributePrefix}:{attributeName}";
                }
                _ = text.Append(' ').Append(attributeName).Append("=\"");
                Parser.Selection.AppendForXmlText(text, attribute.Value, isAttribute: true);
                _ = text.Append('"');
            }
            while (attribute.MoveToNextAttribute());
        }

        var child = node.Clone();
        if (!child.MoveToFirstChild())
        {
            _ = text.Append("/>");
            return;
        }
        _ = text.Append('>');
        do
        {
            this.AppendNode(text, child, scope);
        }
        while (child.MoveToNext());
        _ = text.Append("</").Append(name).Append('>');
    }

    /// <summary>
    /// The prefix <paramref name="uri"/> is written with, declaring it into
    /// <paramref name="declarations"/> when nothing in scope binds it yet. An
    /// empty answer is the default namespace, which only an element name may
    /// use.
    /// </summary>
    private string Bind(Dictionary<string, string> scope, StringBuilder declarations, string uri, string writtenPrefix, bool isElement)
    {
        if (scope.TryGetValue(writtenPrefix, out var bound) && bound == uri && (isElement || writtenPrefix.Length > 0))
            return writtenPrefix;
        foreach (var (prefix, candidateUri) in scope)
        {
            if (candidateUri == uri && (isElement || prefix.Length > 0))
                return prefix;
        }

        string chosen;
        if (isElement && this.defaultElementNamespace == uri && !scope.ContainsKey(string.Empty))
        {
            chosen = string.Empty;
        }
        else if (this.PrologPrefixFor(uri) is { } prologPrefix && !scope.ContainsKey(prologPrefix))
        {
            chosen = prologPrefix;
        }
        else
        {
            chosen = this.Generate(scope);
        }

        AppendDeclaration(declarations, chosen, uri);
        scope[chosen] = uri;
        return chosen;
    }

    private string? PrologPrefixFor(string uri)
    {
        if (this.prologPrefixes is not null)
        {
            foreach (var (prefix, candidateUri) in this.prologPrefixes)
            {
                if (candidateUri == uri)
                    return prefix;
            }
        }

        // The prefixes every XQuery static context predeclares bind their own
        // namespaces too: an ancestor's xsi binding is re-declared as xsi.
        return uri switch
        {
            XmlAtomicTypes.SchemaNamespace => "xs",
            SchemaInstanceNamespace => "xsi",
            XmlAtomicTypes.DataTypesNamespace => "xdt",
            XmlAtomicTypes.SqlNamespace => "sql",
            _ => null,
        };
    }

    private const string SchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// The next generated prefix: <c>p</c> and a number, where the base grows
    /// a <c>1</c> for as long as it is itself bound (so <c>p1</c> once
    /// <c>p</c> is taken) and the numbering starts at 1 on the plain base and
    /// at 0 on a grown one — which is what gives real's <c>p10</c> beside a
    /// kept <c>p</c>.
    /// </summary>
    private string Generate(Dictionary<string, string> scope)
    {
        var basePrefix = "p";
        while (this.IsBound(scope, basePrefix))
            basePrefix += "1";
        if (this.counter < 0)
            this.counter = basePrefix.Length == 1 ? 1 : 0;
        while (true)
        {
            var candidate = basePrefix + this.counter.ToString(CultureInfo.InvariantCulture);
            this.counter++;
            if (!this.IsBound(scope, candidate))
                return candidate;
        }
    }

    private bool IsBound(Dictionary<string, string> scope, string prefix) =>
        scope.ContainsKey(prefix) || this.prologPrefixes?.ContainsKey(prefix) == true;

    private static void AppendDeclaration(StringBuilder text, string prefix, string uri)
    {
        _ = prefix.Length == 0 ? text.Append(" xmlns=\"") : text.Append(" xmlns:").Append(prefix).Append("=\"");
        Parser.Selection.AppendForXmlText(text, uri, isAttribute: true);
        _ = text.Append('"');
    }
}
