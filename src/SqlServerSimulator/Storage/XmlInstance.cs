using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Parses an <c>xml</c> payload into the tree the evaluator walks, and carries
/// the node references a <c>.nodes()</c> row holds.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server's <c>xml</c> is CONTENT-typed: an instance may hold several
/// top-level elements and top-level text, so <c>CAST('&lt;a/&gt;&lt;b/&gt;' AS
/// xml)</c> and <c>CAST('abc' AS xml)</c> are both legal and a
/// <c>FOR XML …, TYPE</c> result routinely carries more than one root. Every
/// instance parses as a fragment whose root node — real's document node — is
/// the context item, so a relative path written against an instance starts
/// above its top-level element: <c>@x.query('a')</c> over
/// <c>&lt;r&gt;&lt;a/&gt;&lt;/r&gt;</c> selects nothing (probed 2026-09-28
/// against SQL Server 2025).
/// </para>
/// <para>
/// A <c>.nodes()</c> row is a <b>node reference</b> rather than a copy: the
/// whole document plus the node's position in it, so a downstream method's
/// context item is that node while <c>..</c> and an absolute path still reach
/// the rest of the document — <c>c.query('/r')</c> answers the whole instance
/// and <c>c.value('count(../a)', …)</c> counts the node's siblings, both as on
/// real. The reference travels through the row as text, a marker character no
/// well-formed instance can begin with, and is only ever read back by the four
/// methods (anything else is refused before it can see the text — Msg 493 /
/// 525).
/// </para>
/// <para>
/// Whitespace-only text between top-level nodes is insignificant and dropped
/// (real's own answer for <c>CAST('  &lt;a/&gt;  &lt;b/&gt;  ' AS xml)</c>),
/// while text carrying anything else keeps its surrounding spaces.
/// </para>
/// </remarks>
internal static class XmlInstance
{
    /// <summary>
    /// The synthetic container a mutable instance hangs from. It is parentless,
    /// so an absolute path's <c>MoveToRoot</c> lands on it and an edit may add
    /// top-level siblings — which is how an <c>insert … before | after</c> on
    /// the outermost element produces the multi-root fragment real answers.
    /// </summary>
    private const string ContainerName = "xml";

    /// <summary>
    /// Opens and separates a node reference's two halves. U+0001 can't appear
    /// in a well-formed instance, so no stored value is mistaken for one.
    /// </summary>
    private const char ReferenceMarker = '\u0001';

    /// <summary>
    /// <see cref="XmlReader"/> settings for a fragment read. Whitespace-only
    /// text is dropped.
    /// </summary>
    private static readonly XmlReaderSettings FragmentSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// The last document this thread parsed. A <c>.nodes()</c> rowset hands
    /// every row the same document, and each row's methods read it again, so
    /// one entry turns a per-row parse into one parse per rowset.
    /// </summary>
    [ThreadStatic]
    private static string? cachedText;

    [ThreadStatic]
    private static XPathDocument? cachedDocument;

    /// <summary>
    /// The context navigator a read method (<c>.value()</c> / <c>.nodes()</c> /
    /// <c>.query()</c> / <c>.exist()</c>) evaluates from: the document node of
    /// an instance, or the referenced node of a <c>.nodes()</c> row, whose
    /// document <paramref name="documents"/> holds.
    /// </summary>
    public static XPathNavigator CreateReadNavigator(string xmlText, XmlDocumentRegistry? documents)
    {
        if (xmlText.Length == 0 || xmlText[0] != ReferenceMarker)
            return Parse(xmlText).CreateNavigator();

        var separator = xmlText.IndexOf(':', StringComparison.Ordinal);
        var document = documents!.Get(int.Parse(xmlText.AsSpan(1, separator - 1), CultureInfo.InvariantCulture));
        var path = xmlText.AsSpan(separator + 1);

        // Consecutive rows of one rowset differ in their last step, so the
        // walk resumes from the previous row's node when it can rather than
        // counting siblings from the first again — which keeps shredding a
        // wide instance linear.
        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash < 0 ? [] : path[..lastSlash];
        var lastStep = path[(lastSlash + 1)..];
        if (!lastStep.IsEmpty && lastStep[0] != '@'
            && ReferenceEquals(lastDocument, document) && lastNavigator is { } previous
            && parentPath.SequenceEqual(lastParentPath) && int.Parse(lastStep, CultureInfo.InvariantCulture) is var ordinal && ordinal >= lastOrdinal)
        {
            var resumed = previous.Clone();
            for (var i = lastOrdinal; i < ordinal; i++)
                _ = resumed.MoveToNext();
            Remember(document, parentPath, ordinal, resumed);
            return resumed.Clone();
        }

        var navigator = Parse(document).CreateNavigator();
        var remaining = path;
        var finalOrdinal = -1;
        while (!remaining.IsEmpty)
        {
            var slash = remaining.IndexOf('/');
            var step = slash < 0 ? remaining : remaining[..slash];
            remaining = slash < 0 ? [] : remaining[(slash + 1)..];
            if (step[0] == '@')
            {
                _ = navigator.MoveToFirstAttribute();
                for (var i = int.Parse(step[1..], CultureInfo.InvariantCulture); i > 0; i--)
                    _ = navigator.MoveToNextAttribute();
                finalOrdinal = -1;
                continue;
            }
            _ = navigator.MoveToFirstChild();
            finalOrdinal = int.Parse(step, CultureInfo.InvariantCulture);
            for (var i = finalOrdinal; i > 0; i--)
                _ = navigator.MoveToNext();
        }
        if (finalOrdinal >= 0)
            Remember(document, parentPath, finalOrdinal, navigator.Clone());
        return navigator;
    }

    [ThreadStatic]
    private static string? lastDocument;

    [ThreadStatic]
    private static string? lastParentPath;

    [ThreadStatic]
    private static int lastOrdinal;

    [ThreadStatic]
    private static XPathNavigator? lastNavigator;

    private static void Remember(string document, ReadOnlySpan<char> parentPath, int ordinal, XPathNavigator navigator)
    {
        lastDocument = document;
        if (!parentPath.SequenceEqual(lastParentPath))
            lastParentPath = parentPath.ToString();
        lastOrdinal = ordinal;
        lastNavigator = navigator;
    }

    /// <summary>The instance text a value carries — the whole document behind a node reference.</summary>
    public static string DocumentTextOf(string xmlText, XmlDocumentRegistry? documents) =>
        xmlText.Length > 0 && xmlText[0] == ReferenceMarker
            ? documents!.Get(int.Parse(xmlText.AsSpan(1, xmlText.IndexOf(':', StringComparison.Ordinal) - 1), CultureInfo.InvariantCulture))
            : xmlText;

    /// <summary>
    /// The reference a <c>.nodes()</c> row carries for <paramref name="node"/>:
    /// the registry's number for its document, then the child ordinals from
    /// the document node down, an attribute's ordinal last.
    /// <paramref name="previous"/> carries the last node this rowset encoded,
    /// so a run of siblings counts forward from it rather than from the first.
    /// </summary>
    public static string EncodeReference(XPathNavigator node, int document, XmlReferenceCursor previous)
    {
        var steps = new List<string>();
        var current = node.Clone();
        if (current.NodeType == XPathNodeType.Attribute)
        {
            var attribute = current.Clone();
            _ = attribute.MoveToParent();
            _ = attribute.MoveToFirstAttribute();
            var ordinal = 0;
            while (!attribute.IsSamePosition(current) && attribute.MoveToNextAttribute())
                ordinal++;
            steps.Add("@" + ordinal.ToString(CultureInfo.InvariantCulture));
            _ = current.MoveToParent();
        }
        else if (previous.Node is { } last && previous.ParentPath is { } parentPath && IsLaterSibling(last, current, out var distance))
        {
            var ordinal = previous.Ordinal + distance;
            previous.Remember(current, parentPath, ordinal);
            return new StringBuilder().Append(ReferenceMarker).Append(document).Append(':')
                .Append(parentPath).Append(parentPath.Length > 0 ? "/" : string.Empty).Append(ordinal).ToString();
        }

        var leafOrdinal = -1;
        var leaf = current.Clone();
        while (current.NodeType != XPathNodeType.Root)
        {
            var sibling = current.Clone();
            var ordinal = 0;
            while (sibling.MoveToPrevious())
                ordinal++;
            if (leafOrdinal < 0 && steps.Count == 0)
                leafOrdinal = ordinal;
            steps.Add(ordinal.ToString(CultureInfo.InvariantCulture));
            if (!current.MoveToParent())
                break;
        }

        var text = new StringBuilder().Append(ReferenceMarker).Append(document).Append(':');
        var parent = new StringBuilder();
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            _ = text.Append(steps[i]);
            if (i > 0)
            {
                _ = text.Append('/');
                if (leafOrdinal >= 0)
                    _ = parent.Append(steps[i]).Append(i > 1 ? "/" : string.Empty);
            }
        }
        if (leafOrdinal >= 0)
            previous.Remember(leaf, parent.ToString(), leafOrdinal);
        else
            previous.Remember(null, null, 0);
        return text.ToString();
    }

    /// <summary>
    /// Whether <paramref name="node"/> follows <paramref name="last"/> among
    /// the same parent's children, and how many siblings on.
    /// </summary>
    private static bool IsLaterSibling(XPathNavigator last, XPathNavigator node, out int distance)
    {
        distance = 0;
        var lastParent = last.Clone();
        var nodeParent = node.Clone();
        if (!lastParent.MoveToParent() || !nodeParent.MoveToParent() || !lastParent.IsSamePosition(nodeParent))
            return false;
        var walker = last.Clone();
        while (walker.MoveToNext())
        {
            distance++;
            if (walker.IsSamePosition(node))
                return true;
        }
        return false;
    }

    private static XPathDocument Parse(string xmlText)
    {
        if (cachedDocument is { } document && string.Equals(cachedText, xmlText, StringComparison.Ordinal))
            return document;
        using var reader = XmlReader.Create(new StringReader(xmlText), FragmentSettings);
        document = new XPathDocument(reader);
        cachedText = xmlText;
        cachedDocument = document;
        return document;
    }

    /// <summary>
    /// Reads an instance into the mutable container <c>.modify()</c> edits. The
    /// container's own children are the instance's top-level nodes; an XML
    /// declaration is dropped, as it is on real.
    /// </summary>
    public static XElement CreateMutableContainer(string xmlText)
    {
        var container = new XElement(ContainerName);
        using var reader = XmlReader.Create(new StringReader(xmlText), FragmentSettings);
        _ = reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType is XmlNodeType.XmlDeclaration or XmlNodeType.None)
            {
                _ = reader.Read();
                continue;
            }
            container.Add(XNode.ReadFrom(reader));
        }
        return container;
    }
}

/// <summary>
/// The documents a batch's <c>.nodes()</c> rows reference, numbered in
/// registration order. The same string registered twice — every row of one
/// variable's rowset, a nested <c>.nodes()</c> over a row — takes one number.
/// </summary>
internal sealed class XmlDocumentRegistry
{
    private readonly List<string> documents = [];
    private readonly Dictionary<string, int> numbers = new(ReferenceEqualityComparer.Instance);
    private readonly Lock gate = new();

    /// <summary>The number <paramref name="document"/> is registered under.</summary>
    public int Register(string document)
    {
        lock (this.gate)
        {
            if (this.numbers.TryGetValue(document, out var number))
                return number;
            number = this.documents.Count;
            this.documents.Add(document);
            this.numbers.Add(document, number);
            return number;
        }
    }

    /// <summary>The document registered under <paramref name="number"/>.</summary>
    public string Get(int number)
    {
        lock (this.gate)
            return this.documents[number];
    }
}

/// <summary>
/// The last node a rowset encoded as a reference, and where it sits: what
/// lets the next sibling's reference count on from it.
/// </summary>
internal sealed class XmlReferenceCursor
{
    /// <summary>The last node encoded, or null when it was an attribute or the document.</summary>
    public XPathNavigator? Node;

    /// <summary>The path of <see cref="Node"/>'s parent, as the reference writes it.</summary>
    public string? ParentPath;

    /// <summary><see cref="Node"/>'s ordinal among its parent's children.</summary>
    public int Ordinal;

    /// <summary>Records the node just encoded.</summary>
    public void Remember(XPathNavigator? node, string? parentPath, int ordinal)
    {
        this.Node = node;
        this.ParentPath = parentPath;
        this.Ordinal = ordinal;
    }
}
