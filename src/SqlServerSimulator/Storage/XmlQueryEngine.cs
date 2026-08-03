using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>
/// XQuery-subset evaluator backing the <c>xml</c> type's <c>.value()</c> /
/// <c>.nodes()</c> / <c>.query()</c> / <c>.exist()</c> methods, and supplying
/// the prolog parsing and compilation <c>.modify()</c>'s target paths run
/// through (<see cref="Parser.XmlDml"/>). An expression is compiled once, while
/// the SQL statement parses, into the tree
/// <see cref="XmlQueryParser"/> builds — so the diagnostics SQL Server settles
/// statically (Msg 2203 / 2209 / 2229 / 2234 / 2389 / 2395 / 9335) fire there
/// too, and only the per-instance walk happens per row.
/// </summary>
/// <remarks>
/// <para>
/// The subset covers an optional prolog of
/// <c>declare default element namespace "uri";</c> and
/// <c>declare namespace prefix="uri";</c> declarations followed by an
/// expression built from location steps (child / attribute / parent /
/// descendant, prefixed or unprefixed names, <c>text()</c> / <c>node()</c> /
/// <c>comment()</c> / <c>processing-instruction()</c> node tests, wildcards),
/// predicates (positional, existence and value), the general
/// (<c>=</c> <c>!=</c> <c>&lt;</c> …) and value (<c>eq</c> <c>ne</c> <c>lt</c> …)
/// comparison operators, <c>and</c> / <c>or</c>, arithmetic, parenthesized
/// sequences, and the built-in function library.
/// </para>
/// <para>
/// The navigator is positioned on the document element of the parsed input, so
/// a relative path (<c>Edu.Level</c>) resolves against that element while an
/// absolute path (<c>/Resume/…</c>) resolves from the document root — the dual
/// behavior <c>.nodes()</c>-produced node references rely on, since each row's
/// value is the serialized outer XML of one matched node.
/// </para>
/// </remarks>
internal static class XmlQueryEngine
{
    /// <summary>
    /// Compiles an XQuery argument — prolog included — for the named method
    /// (<c>value</c> / <c>nodes</c> / <c>query</c> / <c>exist</c>), which the
    /// diagnostics quote.
    /// </summary>
    public static XmlQueryExpr Compile(string xquery, string method)
    {
        var (defaultNamespace, prefixes, body) = ParsePrologAndBody(xquery);
        if (body.Length == 0)
            throw SimulatedSqlException.XQueryExpressionMissing();
        var parser = new XmlQueryParser(body, defaultNamespace, prefixes, method);
        var compiled = parser.ParseBody();

        // A node constructor is legal in query() and exist(), which hand the
        // node on, and refused by the two that would have to look inside it —
        // value() atomizes and nodes() addresses (probe-confirmed, each with
        // its own wording).
        if (parser.ConstructsXml)
        {
            if (method.Equals("value", StringComparison.Ordinal))
                throw SimulatedSqlException.XQueryConstructedXmlNotSupported(method, "data()");
            if (method.Equals("nodes", StringComparison.Ordinal))
                throw SimulatedSqlException.XQueryConstructedXmlNotSupported(method, "'nodes()'");
        }

        // value() takes the first item of its result, but only where real types
        // the expression as at most one item — `(…)[1]` or an attribute step,
        // never a bare `/r/a` (probe-confirmed: Msg 2389 even when the instance
        // holds exactly one match).
        if (method.Equals("value", StringComparison.Ordinal))
            XmlQueryParser.RequireSingleton(compiled, "value()", method);
        return compiled;
    }

    /// <summary>
    /// Compiles a prolog-stripped body against an already-resolved namespace
    /// scope — the entry <c>.modify()</c>'s target paths take.
    /// </summary>
    public static XmlQueryExpr CompileBody(string body, string? defaultNamespace, Dictionary<string, string> prefixes, string method) =>
        new XmlQueryParser(body, defaultNamespace, prefixes, method).ParseBody();

    /// <summary>
    /// Evaluates a compiled <c>.value()</c> expression against
    /// <paramref name="xmlText"/>. Returns the first selected item's string
    /// value, or null when the expression selects nothing — the caller maps
    /// null to a typed SQL NULL.
    /// </summary>
    public static string? EvaluateScalar(string xmlText, XmlQueryExpr compiled)
    {
        var items = Select(xmlText, compiled);
        return items.Count == 0 ? null : XmlQueryValues.StringValue(items[0]);
    }

    /// <summary>
    /// Evaluates a compiled <c>.nodes()</c> expression against
    /// <paramref name="xmlText"/>, yielding the serialized outer XML of each
    /// matched node. In-scope namespace declarations are re-emitted on each
    /// fragment so a subsequent relative <c>.value()</c> / <c>.nodes()</c>
    /// against the fragment resolves the same names.
    /// </summary>
    public static IEnumerable<string> EvaluateNodes(string xmlText, XmlQueryExpr compiled)
    {
        foreach (var item in Select(xmlText, compiled))
        {
            if (item is XPathNavigator node)
                yield return SerializeNode(node);
        }
    }

    /// <summary>
    /// Evaluates a compiled <c>.exist()</c> expression against
    /// <paramref name="xmlText"/>: true when the expression's result sequence
    /// is non-empty. That is real's rule and not an effective-boolean-value
    /// one — <c>exist('false()')</c> and <c>exist('0')</c> both answer 1
    /// (probe-confirmed). The caller maps a NULL instance to a typed SQL NULL
    /// before calling.
    /// </summary>
    public static bool EvaluateExists(string xmlText, XmlQueryExpr compiled) => Select(xmlText, compiled).Count > 0;

    /// <summary>
    /// Evaluates a compiled <c>.query()</c> expression against
    /// <paramref name="xmlText"/>, returning the serialized result: matched
    /// nodes concatenated in document order, atomic values separated by a
    /// single space (real's serialization rule), empty string when nothing
    /// matches. The caller maps a NULL instance to a typed SQL NULL before
    /// calling.
    /// </summary>
    public static string EvaluateQuery(string xmlText, XmlQueryExpr compiled)
    {
        var text = new StringBuilder();
        var previousWasAtomic = false;
        foreach (var item in Select(xmlText, compiled))
        {
            if (item is XPathNavigator node)
            {
                _ = text.Append(SerializeNode(node));
                previousWasAtomic = false;
                continue;
            }
            _ = text.Append(previousWasAtomic ? " " : string.Empty).Append(XmlQueryValues.StringValue(item));
            previousWasAtomic = true;
        }
        return text.ToString();
    }

    /// <summary>
    /// Runs <paramref name="compiled"/> against the parsed instance, with the
    /// context item <see cref="XmlInstance.CreateReadNavigator"/> picks — the
    /// document element of a single-root instance, the fragment's root node
    /// otherwise.
    /// </summary>
    public static List<object> Select(string xmlText, XmlQueryExpr compiled) =>
        Select(XmlInstance.CreateReadNavigator(xmlText), compiled);

    /// <summary>Runs <paramref name="compiled"/> from an existing context node.</summary>
    public static List<object> Select(XPathNavigator context, XmlQueryExpr compiled) =>
        compiled.Evaluate(new XmlQueryFrame(context, 1, 1));

    /// <summary>
    /// Serializes one selected node the way SQL Server returns it: empty
    /// elements self-close with no space before the slash and nothing is
    /// indented — <see cref="XPathNavigator.OuterXml"/> does neither. The
    /// fragment's own root re-declares every namespace in scope, so a relative
    /// read against a <c>.nodes()</c> row resolves the same names it would have
    /// resolved in place.
    /// </summary>
    private static string SerializeNode(XPathNavigator node)
    {
        var text = new StringBuilder();
        AppendNode(text, node, isFragmentRoot: true);
        return text.ToString();
    }

    /// <summary>
    /// Splices a sequence into a constructor's markup. In element content a
    /// node contributes its own markup and an atomic value its escaped text,
    /// adjacent atomics separated by a single space; in an attribute value
    /// everything atomizes, since an attribute can't hold nodes.
    /// </summary>
    internal static void AppendSequence(StringBuilder text, List<object> items, bool isAttribute)
    {
        var previousWasAtomic = false;
        foreach (var item in items)
        {
            if (!isAttribute && item is XPathNavigator node)
            {
                AppendNode(text, node, isFragmentRoot: true);
                previousWasAtomic = false;
                continue;
            }
            if (previousWasAtomic)
                _ = text.Append(' ');
            Parser.Selection.AppendForXmlText(text, XmlQueryValues.StringValue(item), isAttribute);
            previousWasAtomic = true;
        }
    }

    private static void AppendNode(StringBuilder text, XPathNavigator node, bool isFragmentRoot)
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
                _ = text.Append(QualifiedName(node)).Append("=\"");
                Parser.Selection.AppendForXmlText(text, node.Value, isAttribute: true);
                _ = text.Append('"');
                return;
            case XPathNodeType.Element:
                break;
            case XPathNodeType.Root:
                // The instance's own root, which `/` and a `..` off a top-level
                // node reach: real serializes its content, so a fragment comes
                // back as the several top-level nodes it holds.
                var top = node.Clone();
                if (!top.MoveToFirstChild())
                    return;
                do
                {
                    AppendNode(text, top, isFragmentRoot: true);
                }
                while (top.MoveToNext());
                return;
            default:
                Parser.Selection.AppendForXmlText(text, node.Value, isAttribute: false);
                return;
        }

        var name = QualifiedName(node);
        _ = text.Append('<').Append(name);

        // The fragment's root carries the whole in-scope set; a descendant only
        // needs what it declared itself, since the root already wrote the rest.
        var scope = isFragmentRoot ? XmlNamespaceScope.ExcludeXml : XmlNamespaceScope.Local;
        foreach (var (prefix, uri) in node.GetNamespacesInScope(scope))
            _ = text.Append(prefix.Length == 0 ? " xmlns=\"" : $" xmlns:{prefix}=\"").Append(uri).Append('"');

        var attribute = node.Clone();
        if (attribute.MoveToFirstAttribute())
        {
            do
            {
                _ = text.Append(' ');
                AppendNode(text, attribute, isFragmentRoot: false);
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
            AppendNode(text, child, isFragmentRoot: false);
        }
        while (child.MoveToNext());
        _ = text.Append("</").Append(name).Append('>');
    }

    private static string QualifiedName(XPathNavigator node) =>
        node.Prefix.Length == 0 ? node.LocalName : $"{node.Prefix}:{node.LocalName}";

    /// <summary>
    /// Splits the leading <c>declare … ;</c> prolog from the expression body and
    /// returns the default element namespace (null when none declared) plus
    /// the prefix→URI map.
    /// </summary>
    internal static (string? DefaultNamespace, Dictionary<string, string> Prefixes, string Body) ParsePrologAndBody(string xquery)
    {
        string? defaultNamespace = null;
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        while (true)
        {
            while (index < xquery.Length && char.IsWhiteSpace(xquery[index]))
                index++;
            if (!xquery.AsSpan(index).StartsWith("declare", StringComparison.Ordinal))
                break;

            var semicolonOffset = xquery.AsSpan(index).IndexOf(';');
            if (semicolonOffset < 0)
                throw new NotSupportedException("Unterminated XQuery namespace declaration.");
            var declaration = xquery[index..(index + semicolonOffset)];

            var firstQuote = declaration.IndexOf('"', StringComparison.Ordinal);
            var secondQuote = firstQuote + 1 + declaration.AsSpan(firstQuote + 1).IndexOf('"');
            var uri = declaration[(firstQuote + 1)..secondQuote];
            if (declaration.Contains("default element namespace", StringComparison.Ordinal))
            {
                defaultNamespace = uri;
            }
            else
            {
                var keywordEnd = declaration.IndexOf("namespace", StringComparison.Ordinal) + "namespace".Length;
                var prefix = declaration[keywordEnd..declaration.IndexOf('=', StringComparison.Ordinal)].Trim();
                prefixes[prefix] = uri;
            }
            index += semicolonOffset + 1;
        }
        return (defaultNamespace, prefixes, xquery[index..].Trim());
    }
}
