using System.Text;
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
/// The context item is the instance's document node, or the referenced node
/// of a <c>.nodes()</c> row — see <see cref="XmlInstance"/>.
/// </para>
/// </remarks>
internal static class XmlQueryEngine
{
    /// <summary>
    /// Compiles an XQuery argument — prolog included — for the named method
    /// (<c>value</c> / <c>nodes</c> / <c>query</c> / <c>exist</c>), which the
    /// diagnostics quote. <paramref name="typing"/> carries the receiver's XML
    /// schema collection typing when the receiver is typed, and is null for
    /// untyped <c>xml</c>: it narrows a declared-once step to a singleton and
    /// gives a simply typed element or attribute its schema type.
    /// </summary>
    public static XmlQueryExpr Compile(
        string xquery,
        string method,
        XmlStaticTyping? typing = null,
        string? displayMethod = null,
        XmlSqlAccessorScope? sqlAccessors = null,
        Parser.ForXmlNamespaces? statementNamespaces = null,
        string? contextNodeType = null)
    {
        var (defaultNamespace, prefixes, body) = ParsePrologAndBody(xquery, displayMethod ?? method);

        // The statement's WITH XMLNAMESPACES binds what the prolog didn't.
        if (statementNamespaces is not null)
        {
            defaultNamespace ??= statementNamespaces.DefaultUri;
            foreach (var (prefix, uri) in statementNamespaces.Bindings)
                _ = prefixes.TryAdd(prefix, uri);
        }
        if (body.Length == 0)
            throw SimulatedSqlException.XQueryExpressionMissing();

        // The two names differ once a receiver contributes a prefix: `method`
        // stays the bare discriminator the rules below switch on, while
        // `display` is what real writes between the diagnostics' brackets.
        var display = displayMethod ?? method;
        var parser = new XmlQueryParser(body, defaultNamespace, prefixes, display, typing, sqlAccessors, contextNodeType);
        var compiled = parser.ParseBody();

        // A node constructor is legal in query() and exist(), which hand the
        // node on, and refused by the two that would have to look inside it —
        // value() atomizes and nodes() addresses (probe-confirmed, each with
        // its own wording).
        if (compiled.Constructed)
        {
            if (method.Equals("value", StringComparison.Ordinal))
                throw SimulatedSqlException.XQueryConstructedXmlNotSupported(display, "data()");
            if (method.Equals("nodes", StringComparison.Ordinal))
                throw SimulatedSqlException.XQueryConstructedXmlNotSupported(display, "'nodes()'");
        }

        // nodes() addresses what it returns, so an expression real types as
        // atomic values is refused while it compiles (probed 2026-10-02
        // against SQL Server 2025: `nodes('1')`, `nodes('(1, 2)')`).
        if (method.Equals("nodes", StringComparison.Ordinal) && compiled.Kind != XmlStaticKind.Node)
            throw SimulatedSqlException.XQueryNodeRequired(display, "'nodes()'");

        // query() serializes its result, and an attribute has no serialization
        // of its own outside an element.
        if (method.Equals("query", StringComparison.Ordinal) && compiled.IsAttributeOnly)
            throw SimulatedSqlException.XQueryAttributeOutsideElement(display);

        // value() takes the first item of its result, but only where real types
        // the expression as at most one item — `(…)[1]` or an attribute step,
        // never a bare `/r/a` (probe-confirmed: Msg 2389 even when the instance
        // holds exactly one match).
        if (method.Equals("value", StringComparison.Ordinal))
            XmlQueryParser.RequireSingleton(compiled, "value()", display);
        compiled.Prolog = new XmlProlog(defaultNamespace, prefixes, typing is not null);
        return compiled;
    }

    /// <summary>
    /// Compiles a prolog-stripped body against an already-resolved namespace
    /// scope — the entry <c>.modify()</c>'s target paths take.
    /// </summary>
    public static XmlQueryExpr CompileBody(string body, string? defaultNamespace, Dictionary<string, string> prefixes, string method, XmlStaticTyping? typing = null) =>
        new XmlQueryParser(body, defaultNamespace, prefixes, method, typing).ParseBody();

    /// <summary>
    /// Compiles a prolog-stripped body that may name <c>sql:variable</c> /
    /// <c>sql:column</c> — the value expression of an XML-DML statement, whose
    /// accessors <paramref name="sqlAccessors"/> collects for the SQL side to
    /// fill before each evaluation.
    /// </summary>
    public static XmlQueryExpr CompileBody(
        string body,
        string? defaultNamespace,
        Dictionary<string, string> prefixes,
        string method,
        XmlSqlAccessorScope sqlAccessors,
        XmlStaticTyping? typing = null) =>
        new XmlQueryParser(body, defaultNamespace, prefixes, method, typing, sqlAccessors).ParseBody();

    /// <summary>
    /// Evaluates a compiled <c>.value()</c> expression against
    /// <paramref name="xmlText"/>. Returns the first selected item's string
    /// value, or null when the expression selects nothing — the caller maps
    /// null to a typed SQL NULL.
    /// </summary>
    public static string? EvaluateScalar(string xmlText, XmlQueryExpr compiled, XmlVariableScope? scope, XmlDocumentRegistry? documents)
    {
        var items = Select(xmlText, compiled, scope, documents);
        if (items.Count == 0)
            return null;

        // A typed element marked nil atomizes to the empty sequence, so its
        // value() is NULL where untyped XML reads the empty string (probed
        // 2026-09-28 against SQL Server 2025).
        if (compiled.Prolog?.Typed == true && items[0] is XPathNavigator { NodeType: XPathNodeType.Element } element
            && element.GetAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance").Trim() is "true" or "1")
        {
            return null;
        }
        return XmlQueryValues.StringValue(items[0]);
    }

    /// <summary>
    /// Evaluates a compiled <c>.nodes()</c> expression against
    /// <paramref name="xmlText"/>, yielding a node reference per matched node
    /// (see <see cref="XmlInstance"/>) — so a downstream method reads the node
    /// in place, its parent and document included.
    /// </summary>
    public static IEnumerable<string> EvaluateNodes(string xmlText, XmlQueryExpr compiled, XmlVariableScope? scope, XmlDocumentRegistry documents)
    {
        var document = documents.Register(XmlInstance.DocumentTextOf(xmlText, documents));
        var cursor = new XmlReferenceCursor();
        foreach (var item in Select(xmlText, compiled, scope, documents))
        {
            if (item is XPathNavigator node)
                yield return XmlInstance.EncodeReference(node, document, cursor);
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
    public static bool EvaluateExists(string xmlText, XmlQueryExpr compiled, XmlVariableScope? scope, XmlDocumentRegistry? documents) =>
        Select(xmlText, compiled, scope, documents).Count > 0;

    /// <summary>
    /// Evaluates a compiled <c>.query()</c> expression against
    /// <paramref name="xmlText"/>, returning the serialized result: matched
    /// nodes concatenated in document order, atomic values separated by a
    /// single space (real's serialization rule), empty string when nothing
    /// matches. The caller maps a NULL instance to a typed SQL NULL before
    /// calling.
    /// </summary>
    public static string EvaluateQuery(string xmlText, XmlQueryExpr compiled, XmlVariableScope? scope, XmlDocumentRegistry? documents)
    {
        var text = new StringBuilder();
        var previousWasAtomic = false;
        var serializer = new XmlResultSerializer(compiled.Prolog?.DefaultElementNamespace, compiled.Prolog?.Prefixes);
        foreach (var item in Select(xmlText, compiled, scope, documents))
        {
            if (item is XPathNavigator node)
            {
                // A statically attribute-only result is Msg 2396 at compile;
                // one that reaches here sits among other nodes, which real
                // refuses as it serializes (probe-confirmed).
                if (node.NodeType == XPathNodeType.Attribute)
                    throw SimulatedSqlException.XmlAttributeAfterContent();
                serializer.Append(text, node);
                previousWasAtomic = false;
                continue;
            }
            if (item is XmlEmptyTextNode)
            {
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
    /// document node, or the referenced node of a <c>.nodes()</c> row — and
    /// <paramref name="scope"/>'s <c>sql:</c> accessor slots filled.
    /// </summary>
    public static List<object> Select(string xmlText, XmlQueryExpr compiled, XmlVariableScope? scope, XmlDocumentRegistry? documents) =>
        compiled.Evaluate(new XmlQueryFrame(XmlInstance.CreateReadNavigator(xmlText, documents), 1, 1, scope));

    /// <summary>Runs <paramref name="compiled"/> from an existing context node.</summary>
    public static List<object> Select(XPathNavigator context, XmlQueryExpr compiled) =>
        compiled.Evaluate(new XmlQueryFrame(context, 1, 1));

    /// <summary>
    /// Runs <paramref name="compiled"/> from an existing context node with
    /// <paramref name="scope"/>'s slots already filled — how an XML-DML value
    /// expression reads the <c>sql:</c> accessors the row supplies.
    /// </summary>
    public static List<object> Select(XPathNavigator context, XmlQueryExpr compiled, XmlVariableScope scope) =>
        compiled.Evaluate(new XmlQueryFrame(context, 1, 1, scope));

    /// <summary>
    /// Splices a sequence into a constructor's markup. In element content a
    /// node contributes its own markup and an atomic value its escaped text,
    /// adjacent atomics separated by a single space, while an attribute node
    /// becomes a marker the constructor hoists onto its element; in an
    /// attribute value everything atomizes, since an attribute can't hold
    /// nodes.
    /// </summary>
    internal static void AppendSequence(StringBuilder text, List<object> items, bool isAttribute, ref List<XPathNavigator>? attributes)
    {
        var previousWasAtomic = false;
        XmlResultSerializer? serializer = null;
        foreach (var item in items)
        {
            if (!isAttribute)
            {
                switch (item)
                {
                    case XPathNavigator { NodeType: XPathNodeType.Attribute } attribute:
                        attributes ??= [];
                        XmlAttributeHoisting.AppendMarker(text, attributes.Count);
                        attributes.Add(attribute);
                        previousWasAtomic = false;
                        continue;
                    case XPathNavigator node:
                        (serializer ??= new XmlResultSerializer(null, null)).Append(text, node);
                        previousWasAtomic = false;
                        continue;
                    case XmlEmptyTextNode:
                        previousWasAtomic = false;
                        continue;
                }
            }
            if (previousWasAtomic)
                _ = text.Append(' ');
            Parser.Selection.AppendForXmlText(text, XmlQueryValues.StringValue(item), isAttribute);
            previousWasAtomic = true;
        }
    }

    /// <summary>
    /// Splits the leading prolog from the expression body and returns the
    /// default element namespace (null when none declared) plus the
    /// prefix→URI map.
    /// </summary>
    /// <remarks>
    /// The prolog real accepts (probed 2026-09-28 against SQL Server 2025): a
    /// leading <c>xquery version "1.0";</c>, then any number of
    /// <c>declare namespace p = "uri";</c>, <c>declare default element
    /// namespace "uri";</c> and <c>declare default function namespace
    /// "uri";</c>. It parses <c>declare function</c> / <c>declare
    /// variable</c> only to refuse them (Msg 9335) and reports any other
    /// declaration as a syntax error near <c>declare</c>.
    /// </remarks>
    internal static (string? DefaultNamespace, Dictionary<string, string> Prefixes, string Body) ParsePrologAndBody(string xquery, string method = "query")
    {
        string? defaultNamespace = null;
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = SkipSpace(xquery, 0);
        if (StartsWithWords(xquery, index, "xquery", "version") && xquery.IndexOf(';', index) is var versionEnd and >= 0)
            index = SkipSpace(xquery, versionEnd + 1);

        while (StartsWithWord(xquery, index, "declare"))
        {
            var after = SkipSpace(xquery, index + "declare".Length);
            if (StartsWithWord(xquery, after, "function") || StartsWithWord(xquery, after, "variable"))
                throw SimulatedSqlException.XQuerySyntaxNotSupported(method, StartsWithWord(xquery, after, "function") ? "declare function" : "declare variable");
            if (!StartsWithWord(xquery, after, "namespace") && !StartsWithWord(xquery, after, "default"))
                throw SimulatedSqlException.XQuerySyntaxError(method, "declare");

            var semicolonOffset = xquery.AsSpan(index).IndexOf(';');
            if (semicolonOffset < 0)
                throw SimulatedSqlException.XQuerySyntaxError(method, "<eof>");
            var declaration = xquery[index..(index + semicolonOffset)];

            var firstQuote = declaration.IndexOfAny(['"', '\'']);
            if (firstQuote < 0)
                throw SimulatedSqlException.XQuerySyntaxError(method, "declare");
            var secondQuote = firstQuote + 1 + declaration.AsSpan(firstQuote + 1).IndexOf(declaration[firstQuote]);
            var uri = declaration[(firstQuote + 1)..secondQuote];
            if (StartsWithWord(xquery, after, "default"))
            {
                // A default *function* namespace changes nothing the subset
                // resolves; the element one binds unprefixed element names.
                if (declaration.Contains("element", StringComparison.Ordinal))
                    defaultNamespace = uri;
            }
            else
            {
                var keywordEnd = declaration.IndexOf("namespace", StringComparison.Ordinal) + "namespace".Length;
                var prefix = declaration[keywordEnd..declaration.IndexOf('=', StringComparison.Ordinal)].Trim();
                prefixes[prefix] = uri;
            }
            index = SkipSpace(xquery, index + semicolonOffset + 1);
        }
        return (defaultNamespace, prefixes, xquery[index..].Trim());
    }

    private static int SkipSpace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    /// <summary>Whether <paramref name="word"/> sits at <paramref name="index"/> as a whole word.</summary>
    private static bool StartsWithWord(string text, int index, string word) =>
        text.AsSpan(index).StartsWith(word, StringComparison.Ordinal)
        && (index + word.Length >= text.Length || !(char.IsLetterOrDigit(text[index + word.Length]) || text[index + word.Length] is '-' or '_' or ':' or '.'));

    private static bool StartsWithWords(string text, int index, string first, string second) =>
        StartsWithWord(text, index, first) && StartsWithWord(text, SkipSpace(text, index + first.Length), second);
}
