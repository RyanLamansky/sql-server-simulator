using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Parses an <c>xml</c> payload into the tree the evaluator walks.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server's <c>xml</c> is CONTENT-typed: an instance may hold several
/// top-level elements and top-level text, so <c>CAST('&lt;a/&gt;&lt;b/&gt;' AS
/// xml)</c> and <c>CAST('abc' AS xml)</c> are both legal and a
/// <c>FOR XML …, TYPE</c> result routinely carries more than one root. A
/// well-formed single-root instance keeps the document-element context that a
/// relative path resolves against — which is what a <c>.nodes()</c> row, itself
/// re-parsed from one serialized node, relies on — while everything else parses
/// as a fragment whose root node is the context, matching the document node
/// real itself uses.
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
    /// <see cref="XmlReader"/> settings for a fragment read. Whitespace-only
    /// text is dropped, matching what <see cref="XDocument.Parse(string)"/>
    /// does for the single-root case.
    /// </summary>
    private static readonly XmlReaderSettings FragmentSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// The context navigator a read method (<c>.value()</c> / <c>.nodes()</c> /
    /// <c>.query()</c> / <c>.exist()</c>) evaluates from.
    /// </summary>
    public static XPathNavigator CreateReadNavigator(string xmlText)
    {
        try
        {
            if (XDocument.Parse(xmlText).Root is { } root)
                return root.CreateNavigator();
        }
        catch (XmlException)
        {
            // Not a document — several top-level elements, top-level text, or
            // nothing at all — so read it as the fragment real admits.
        }

        using var reader = XmlReader.Create(new StringReader(xmlText), FragmentSettings);
        return new XPathDocument(reader).CreateNavigator();
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

    /// <summary>
    /// The context node an XML-DML path resolves relative names against: the
    /// single top-level element of a document-shaped instance, and the container
    /// itself — real's document node — for a fragment.
    /// </summary>
    public static XPathNavigator CreateMutableNavigator(XElement container)
    {
        var elements = container.Elements().Take(2).ToArray();
        return elements.Length == 1 && !container.Nodes().OfType<XText>().Any()
            ? elements[0].CreateNavigator()
            : container.CreateNavigator();
    }
}
