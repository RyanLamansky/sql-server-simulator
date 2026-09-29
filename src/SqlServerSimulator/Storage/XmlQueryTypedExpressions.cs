using System.Text;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>What a sequence type's item type tests.</summary>
internal enum XmlSequenceKind
{
    /// <summary>An atomic type — <c>xs:integer</c>, <c>xdt:untypedAtomic</c>.</summary>
    Atomic,

    /// <summary><c>empty()</c>.</summary>
    Empty,

    /// <summary><c>item()</c>.</summary>
    Item,

    /// <summary><c>node()</c>.</summary>
    Node,

    /// <summary><c>element()</c> / <c>element(name)</c>.</summary>
    Element,

    /// <summary><c>attribute()</c> / <c>attribute(name)</c>.</summary>
    Attribute,

    /// <summary><c>text()</c>.</summary>
    Text,

    /// <summary><c>comment()</c>.</summary>
    Comment,

    /// <summary><c>processing-instruction()</c>.</summary>
    ProcessingInstruction,
}

/// <summary>The sequence type an <c>instance of</c> tests against.</summary>
internal sealed class XmlSequenceType
{
    /// <summary>The item type's kind.</summary>
    public readonly XmlSequenceKind Kind;

    /// <summary>The element / attribute local name a kind test names, or empty for any; the atomic type's written name.</summary>
    public readonly string Name;

    /// <summary>The atomic type, or null for <c>xdt:untypedAtomic</c> and every kind test.</summary>
    public readonly XmlSchemaSimpleType? AtomicType;

    /// <summary>The occurrence indicator as written, <c>'\0'</c> for none.</summary>
    public readonly char Occurrence;

    private XmlSequenceType(XmlSequenceKind kind, string name, XmlSchemaSimpleType? atomicType, char occurrence)
    {
        this.Kind = kind;
        this.Name = name;
        this.AtomicType = atomicType;
        this.Occurrence = occurrence;
    }

    /// <summary>A kind test.</summary>
    public static XmlSequenceType KindTest(XmlSequenceKind kind, string name) => new(kind, name, null, '\0');

    /// <summary>An atomic type; a null <paramref name="type"/> is <c>xdt:untypedAtomic</c>.</summary>
    public static XmlSequenceType Atomic(string name, XmlSchemaSimpleType? type) => new(XmlSequenceKind.Atomic, name, type, '\0');

    /// <summary>The same item type with an occurrence indicator.</summary>
    public XmlSequenceType WithOccurrence(char occurrence) => new(this.Kind, this.Name, this.AtomicType, occurrence);

    /// <summary>Whether the empty sequence is an instance.</summary>
    public bool AdmitsEmpty => this.Kind == XmlSequenceKind.Empty || this.Occurrence is '?' or '*';
}

/// <summary>
/// <c>expr instance of SequenceType</c>. An atomic item's type is the one the
/// compiled tree gave it — a typed atomic carries its own, and a plain number
/// takes the operand's static type — so <c>1 instance of xs:decimal</c> is
/// true (<c>xs:integer</c> derives from it) and <c>1.5 instance of
/// xs:integer</c> false, both probe-confirmed.
/// </summary>
internal sealed class XmlInstanceOfExpr(XmlQueryExpr operand, XmlSequenceType type)
    : XmlQueryExpr(XmlStaticKind.Boolean, XmlOccurrence.ExactlyOne, "xs:boolean")
{
    private readonly XmlQueryExpr operand = operand;
    private readonly XmlSequenceType type = type;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var items = this.operand.Evaluate(frame);
        results.Add(items.Count switch
        {
            0 => this.type.AdmitsEmpty,
            _ => this.type.Kind != XmlSequenceKind.Empty && this.Matches(items[0]),
        });
    }

    private bool Matches(object item)
    {
        if (this.type.Kind == XmlSequenceKind.Item)
            return true;
        if (item is not XPathNavigator node)
        {
            if (this.type.Kind != XmlSequenceKind.Atomic)
                return false;
            var itemType = item switch
            {
                XmlTypedAtomic typed => typed.TypeName,
                // An atomized node carries its schema type statically: the
                // operand's, which is xdt:untypedAtomic over untyped XML.
                XmlUntypedAtomic => this.operand.TypeName,
                bool => "xs:boolean",
                string => "xs:string",
                _ => this.operand.TypeName,
            };
            return XmlAtomicTypes.IsSubtype(itemType, this.type.AtomicType, targetIsUntyped: this.type.AtomicType is null);
        }

        return this.type.Kind switch
        {
            XmlSequenceKind.Node => true,
            XmlSequenceKind.Element => node.NodeType == XPathNodeType.Element && (this.type.Name.Length == 0 || node.LocalName == this.type.Name),
            XmlSequenceKind.Attribute => node.NodeType == XPathNodeType.Attribute && (this.type.Name.Length == 0 || node.LocalName == this.type.Name),
            XmlSequenceKind.Text => node.NodeType is XPathNodeType.Text or XPathNodeType.Whitespace or XPathNodeType.SignificantWhitespace,
            XmlSequenceKind.Comment => node.NodeType == XPathNodeType.Comment,
            XmlSequenceKind.ProcessingInstruction => node.NodeType == XPathNodeType.ProcessingInstruction,
            _ => false,
        };
    }
}

/// <summary>
/// <c>expr cast as xs:type?</c>, and the <c>xs:type(expr)</c> constructor
/// function real reads as the same cast. A value from the instance that
/// doesn't convert answers the empty sequence — real's dynamic-error rule —
/// while a literal that doesn't is refused while compiling (Msg 9319).
/// </summary>
internal sealed class XmlCastExpr(XmlQueryExpr operand, XmlSchemaSimpleType? target, string targetName)
    : XmlQueryExpr(
        target is null ? XmlStaticKind.Untyped : XmlAtomicTypes.KindOf(target),
        XmlOccurrence.ZeroOrOne,
        targetName)
{
    private readonly XmlQueryExpr operand = operand;
    private readonly XmlSchemaSimpleType? target = target;
    private readonly string targetName = targetName;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var items = this.operand.Evaluate(frame);
        if (items.Count == 0)
            return;
        if (Convert(XmlQueryValues.Atomize(items[0]), this.target, this.targetName) is { } converted)
            results.Add(converted);
    }

    /// <summary>
    /// Converts one atomized item, answering null when the value isn't in the
    /// target's lexical space. A null <paramref name="target"/> is
    /// <c>xdt:untypedAtomic</c>, which takes any value's string form.
    /// </summary>
    public static object? Convert(object atomized, XmlSchemaSimpleType? target, string targetName) =>
        target is null
            ? new XmlUntypedAtomic(XmlQueryValues.StringValue(atomized))
            : XmlAtomicTypes.TryCast(atomized, target, targetName);
}

/// <summary>
/// The node comparisons <c>is</c>, <c>&lt;&lt;</c> and <c>&gt;&gt;</c>: node
/// identity and document order between two singletons. An empty operand
/// answers the empty sequence.
/// </summary>
internal sealed class XmlNodeComparisonExpr(XmlQueryExpr left, XmlQueryExpr right, string op)
    : XmlQueryExpr(XmlStaticKind.Boolean, XmlOccurrence.ZeroOrOne, "xs:boolean")
{
    private readonly XmlQueryExpr left = left;
    private readonly XmlQueryExpr right = right;
    private readonly string op = op;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var leftItems = this.left.Evaluate(frame);
        var rightItems = this.right.Evaluate(frame);
        if (leftItems.Count == 0 || rightItems.Count == 0 || leftItems[0] is not XPathNavigator leftNode || rightItems[0] is not XPathNavigator rightNode)
            return;
        results.Add(this.op switch
        {
            "<<" => leftNode.ComparePosition(rightNode) == System.Xml.XmlNodeOrder.Before,
            ">>" => leftNode.ComparePosition(rightNode) == System.Xml.XmlNodeOrder.After,
            _ => leftNode.IsSamePosition(rightNode),
        });
    }
}

/// <summary>
/// The empty text node <c>text {""}</c> builds: a node, so <c>.exist()</c>
/// sees it, but one that contributes nothing wherever it lands. .NET's
/// navigators have no way to stand on an empty text node, hence the stand-in.
/// </summary>
internal sealed class XmlEmptyTextNode
{
    /// <summary>The one instance.</summary>
    public static readonly XmlEmptyTextNode Instance = new();

    private XmlEmptyTextNode()
    {
    }
}

/// <summary>
/// A computed <c>attribute name {…}</c> constructor: the content's atomized
/// items joined by a single space, on a throwaway element so the node can be
/// navigated to. It is only ever hoisted into an enclosing constructor, tested
/// for existence, or refused at the top level of a <c>.query()</c> (Msg 2396).
/// </summary>
internal sealed class XmlComputedAttributeExpr(string prefix, string localName, string namespaceUri, XmlQueryExpr? content)
    : XmlQueryExpr(XmlStaticKind.Node, XmlOccurrence.ExactlyOne, "xdt:untypedAtomic", constructed: true)
{
    private readonly string prefix = prefix;
    private readonly string localName = localName;
    private readonly string namespaceUri = namespaceUri;
    private readonly XmlQueryExpr? content = content;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var value = this.content is null ? string.Empty : XmlQueryValues.JoinAtomized(this.content.Evaluate(frame));
        var holder = new XElement("a");
        if (this.prefix.Length > 0)
            holder.Add(new XAttribute(XNamespace.Xmlns + this.prefix, this.namespaceUri));
        holder.Add(new XAttribute(XName.Get(this.localName, this.namespaceUri), value));
        var navigator = holder.CreateNavigator();
        _ = navigator.MoveToFirstAttribute();
        while (navigator.LocalName != this.localName || navigator.NamespaceURI != this.namespaceUri)
            _ = navigator.MoveToNextAttribute();
        results.Add(navigator);
    }

    public override string NodeTypeBase() => $"attribute({this.localName},xdt:untypedAtomic)";
}

/// <summary>
/// A computed <c>text {…}</c> constructor: the content's atomized items joined
/// by a single space. An empty content sequence builds no node at all, while
/// an empty string builds an empty one (probe-confirmed through
/// <c>.exist()</c>).
/// </summary>
internal sealed class XmlComputedTextExpr(XmlQueryExpr content)
    : XmlQueryExpr(XmlStaticKind.Node, XmlOccurrence.ZeroOrOne, "xdt:untypedAtomic", constructed: true)
{
    private readonly XmlQueryExpr content = content;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var items = this.content.Evaluate(frame);
        if (items.Count == 0)
            return;
        var value = XmlQueryValues.JoinAtomized(items);
        if (value.Length == 0)
        {
            results.Add(XmlEmptyTextNode.Instance);
            return;
        }
        var navigator = new XElement("t", value).CreateNavigator();
        _ = navigator.MoveToFirstChild();
        results.Add(navigator);
    }

    public override string NodeTypeBase() => "text";
}

/// <summary>
/// A direct comment (<c>&lt;!-- c --&gt;</c>) or processing-instruction
/// (<c>&lt;?t d?&gt;</c>) constructor at expression level; its content is
/// literal text, braces included.
/// </summary>
internal sealed class XmlDirectLeafExpr(bool isComment, string target, string data)
    : XmlQueryExpr(XmlStaticKind.Node, XmlOccurrence.ExactlyOne, "xdt:untypedAtomic", constructed: true)
{
    private readonly bool isComment = isComment;
    private readonly string target = target;
    private readonly string data = data;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        XNode node = this.isComment ? new XComment(this.data) : new XProcessingInstruction(this.target, this.data);
        var navigator = new XElement("h", node).CreateNavigator();
        _ = navigator.MoveToFirstChild();
        results.Add(navigator);
    }

    public override string NodeTypeBase() => this.isComment ? "comment" : "processing-instruction";
}

/// <summary>
/// Builds a constructed element's attributes from the placeholders its markup
/// carries: an attribute item in element content — a computed constructor, or
/// an attribute a path selected — is spliced in as a marker processing
/// instruction and hoisted onto the element once the markup has parsed.
/// </summary>
internal static class XmlAttributeHoisting
{
    /// <summary>The marker's target, a name reserved for the purpose.</summary>
    public const string MarkerTarget = "sqlsim-attribute";

    /// <summary>Appends the marker for the <paramref name="ordinal"/>-th pending attribute.</summary>
    public static void AppendMarker(StringBuilder text, int ordinal) =>
        _ = text.Append("<?").Append(MarkerTarget).Append(' ').Append(ordinal).Append("?>");

    /// <summary>
    /// Moves each marked attribute onto its element, in document order. One
    /// that follows an element, comment or processing instruction is Msg 6307
    /// (text and atomic values before it are fine, probe-confirmed), and one
    /// the element already carries is Msg 6308.
    /// </summary>
    public static void Hoist(XElement root, List<XPathNavigator> attributes)
    {
        var markers = new List<XProcessingInstruction>();
        foreach (var node in root.DescendantNodes())
        {
            if (node is XProcessingInstruction { Target: MarkerTarget } marker)
                markers.Add(marker);
        }
        foreach (var marker in markers)
        {
            var owner = marker.Parent!;
            foreach (var before in marker.NodesBeforeSelf())
            {
                if (before is XElement or XComment or XProcessingInstruction { Target: not MarkerTarget })
                    throw SimulatedSqlException.XmlAttributeAfterContent();
            }

            var attribute = attributes[int.Parse(marker.Data, System.Globalization.CultureInfo.InvariantCulture)];
            var name = XName.Get(attribute.LocalName, attribute.NamespaceURI);
            if (owner.Attribute(name) is not null)
                throw SimulatedSqlException.XmlDuplicateAttribute(attribute.LocalName);
            if (attribute.NamespaceURI.Length > 0 && owner.GetPrefixOfNamespace(attribute.NamespaceURI) is null)
                owner.Add(new XAttribute(XNamespace.Xmlns + (attribute.Prefix.Length > 0 ? attribute.Prefix : "p1"), attribute.NamespaceURI));
            owner.Add(new XAttribute(name, attribute.Value));
            marker.Remove();
        }
    }
}
