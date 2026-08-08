using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>The three XML-DML statements <c>.modify()</c> accepts.</summary>
internal enum XmlDmlKind
{
    /// <summary><c>insert &lt;content&gt; {as first|as last} into|before|after &lt;target&gt;</c>.</summary>
    Insert,

    /// <summary><c>delete &lt;target&gt;</c>.</summary>
    Delete,

    /// <summary><c>replace value of &lt;target&gt; with &lt;value&gt;</c>.</summary>
    ReplaceValueOf,
}

/// <summary>Where an <c>insert</c> places its content relative to the target.</summary>
internal enum XmlDmlPosition
{
    /// <summary><c>into</c> with no <c>as first</c> / <c>as last</c> — appends, like <c>as last</c>.</summary>
    Into,

    /// <summary><c>as first into</c>.</summary>
    AsFirst,

    /// <summary><c>as last into</c>.</summary>
    AsLast,

    /// <summary><c>before</c>.</summary>
    Before,

    /// <summary><c>after</c>.</summary>
    After,
}

/// <summary>The node kind an XML-DML path statically selects.</summary>
internal enum XmlDmlNodeKind
{
    /// <summary>A named element step (<c>/r/a</c>).</summary>
    Element,

    /// <summary>An attribute step (<c>@n</c>).</summary>
    Attribute,

    /// <summary>A <c>text()</c> node test.</summary>
    Text,

    /// <summary>A <c>comment()</c> node test.</summary>
    Comment,

    /// <summary>A <c>processing-instruction()</c> node test.</summary>
    ProcessingInstruction,

    /// <summary>The context (document) node, written <c>.</c>.</summary>
    Document,
}

/// <summary>How an <c>insert</c> content item produces its nodes.</summary>
internal enum XmlDmlItemKind
{
    /// <summary>A direct constructor — element, comment or processing instruction markup.</summary>
    Markup,

    /// <summary>A computed <c>attribute name {…}</c> constructor.</summary>
    Attribute,

    /// <summary>A computed <c>element name {…}</c> constructor.</summary>
    Element,

    /// <summary>A computed <c>text {…}</c> constructor.</summary>
    Text,

    /// <summary>A bare expression — legal only when it evaluates to <c>xml</c>.</summary>
    Value,
}

/// <summary>Where a single XML-DML term reads its value from.</summary>
internal enum XmlDmlTermKind
{
    /// <summary>A string or numeric literal written in the XQuery text.</summary>
    Literal,

    /// <summary><c>sql:variable("@v")</c>.</summary>
    Variable,

    /// <summary><c>sql:column("c")</c>.</summary>
    Column,
}

/// <summary>
/// One value-producing term of an XML-DML expression: a literal, a
/// <c>sql:variable("@v")</c> or a <c>sql:column("c")</c>. A sequence of them
/// atomizes to their string values joined by a single space, which is what
/// real does for <c>with ("a","b")</c> and for a multi-term enclosed
/// expression.
/// </summary>
internal readonly struct XmlDmlTerm
{
    /// <summary>Which of the three forms this term is.</summary>
    public readonly XmlDmlTermKind Kind;

    /// <summary>The literal's value, for <see cref="XmlDmlTermKind.Literal"/>.</summary>
    public readonly SqlValue Literal;

    /// <summary>The variable (no <c>@</c>) or column name the term reads.</summary>
    public readonly string Name;

    /// <summary>
    /// The term's compile-time type, or null when only execution can say
    /// (<c>sql:column</c> outside an UPDATE's SET list, where no column-type
    /// resolver is in scope while the modify text parses).
    /// </summary>
    public readonly SqlType? StaticType;

    private XmlDmlTerm(XmlDmlTermKind kind, SqlValue literal, string name, SqlType? staticType)
    {
        this.Kind = kind;
        this.Literal = literal;
        this.Name = name;
        this.StaticType = staticType;
    }

    /// <summary>Builds a literal term.</summary>
    public static XmlDmlTerm FromLiteral(SqlValue value) => new(XmlDmlTermKind.Literal, value, string.Empty, value.Type);

    /// <summary>Builds a <c>sql:variable</c> term over an already-declared variable.</summary>
    public static XmlDmlTerm FromVariable(string name, SqlType declaredType) => new(XmlDmlTermKind.Variable, default, name, declaredType);

    /// <summary>Builds a <c>sql:column</c> term; <paramref name="staticType"/> is null when unresolvable at parse.</summary>
    public static XmlDmlTerm FromColumn(string name, SqlType? staticType) => new(XmlDmlTermKind.Column, default, name, staticType);

    /// <summary>Reads the term's value for the row being mutated.</summary>
    public SqlValue Evaluate(RuntimeContext runtime) => this.Kind switch
    {
        XmlDmlTermKind.Literal => this.Literal,
        XmlDmlTermKind.Variable => runtime.Batch.Variables[this.Name].Value,
        _ => runtime.ResolveColumn(XmlDml.ColumnNameOf(this.Name)),
    };

    /// <summary>
    /// Whether a literal written directly in the XQuery text produced this
    /// term. Real reports a literal's static type without an occurrence
    /// indicator (<c>xs:string</c>) and a <c>sql:</c> accessor's with one
    /// (<c>xs:string ?</c>).
    /// </summary>
    public bool IsLiteral => this.Kind == XmlDmlTermKind.Literal;
}

/// <summary>
/// One item of an <c>insert</c> content sequence. A direct constructor keeps
/// its markup as alternating literal segments and enclosed expressions
/// (<c>&lt;n&gt;{sql:variable("@v")}&lt;/n&gt;</c>), spliced together and
/// parsed at execution; the computed constructors keep only their value terms.
/// </summary>
internal sealed class XmlDmlItem
{
    /// <summary>Which constructor form produced this item.</summary>
    public readonly XmlDmlItemKind Kind;

    /// <summary>The constructor's name (attribute / processing-instruction target); empty otherwise.</summary>
    public readonly string Name;

    /// <summary>
    /// Literal markup segments of a <see cref="XmlDmlItemKind.Markup"/> item,
    /// one more than <see cref="Enclosed"/> has entries.
    /// </summary>
    public readonly string[] Literals;

    /// <summary>
    /// Each enclosed expression of a markup item, or the single value
    /// expression of a computed constructor / bare value.
    /// </summary>
    public readonly XmlDmlTerm[][] Enclosed;

    /// <summary>
    /// True for the enclosed expressions written inside an attribute value —
    /// their substituted text takes attribute-value escaping rather than
    /// element-content escaping.
    /// </summary>
    public readonly bool[] EnclosedInAttribute;

    /// <summary>
    /// A computed <c>element</c> constructor's own content items, which nest to
    /// any depth (<c>element n {element m {1}}</c>); empty for every other kind.
    /// </summary>
    public readonly XmlDmlItem[] Children;

    private XmlDmlItem(XmlDmlItemKind kind, string name, string[] literals, XmlDmlTerm[][] enclosed, bool[] enclosedInAttribute, XmlDmlItem[] children)
    {
        this.Kind = kind;
        this.Name = name;
        this.Literals = literals;
        this.Enclosed = enclosed;
        this.EnclosedInAttribute = enclosedInAttribute;
        this.Children = children;
    }

    /// <summary>A direct constructor's markup template.</summary>
    public static XmlDmlItem Markup(string[] literals, XmlDmlTerm[][] enclosed, bool[] enclosedInAttribute) =>
        new(XmlDmlItemKind.Markup, string.Empty, literals, enclosed, enclosedInAttribute, []);

    /// <summary>A computed <c>attribute</c> / <c>text</c> constructor.</summary>
    public static XmlDmlItem Computed(XmlDmlItemKind kind, string name, XmlDmlTerm[] value) =>
        new(kind, name, [], [value], [], []);

    /// <summary>A computed <c>element name {…}</c> constructor over its content items.</summary>
    public static XmlDmlItem Element(string name, XmlDmlItem[] children) =>
        new(XmlDmlItemKind.Element, name, [], [], [], children);

    /// <summary>A bare expression item, legal only when it evaluates to <c>xml</c>.</summary>
    public static XmlDmlItem Value(XmlDmlTerm[] value) =>
        new(XmlDmlItemKind.Value, string.Empty, [], [value], [], []);
}

/// <summary>
/// An XML-DML path expression plus the static node type real derives from it.
/// The type drives the target checks (<c>Msg 2226</c> / <c>2240</c> /
/// <c>2249</c> / <c>2337</c> / <c>2356</c> / <c>2264</c>), which real settles
/// at compile time off the path's shape alone — so <c>/r/a/text()</c> is
/// <c>text *</c> and rejected as a <c>replace value of</c> target even when
/// the instance holds exactly one matching node, while
/// <c>(/r/a/text())[1]</c> is <c>text ?</c> and accepted.
/// </summary>
internal readonly struct XmlDmlPath(string body, XmlQueryExpr compiled, XmlDmlNodeKind kind, string name, bool singleton)
{
    /// <summary>The path body as written, with the prolog already stripped.</summary>
    public readonly string Body = body;

    /// <summary>The compiled path, evaluated against the instance.</summary>
    public readonly XmlQueryExpr Compiled = compiled;

    /// <summary>The node kind the path's last step selects.</summary>
    public readonly XmlDmlNodeKind Kind = kind;

    /// <summary>The selected element / attribute's local name; empty for the node tests.</summary>
    public readonly string Name = name;

    /// <summary>
    /// True when the whole path is wrapped in a positional predicate
    /// (<c>(…)[n]</c>) — the only shape real types as at-most-one.
    /// </summary>
    public readonly bool Singleton = singleton;

    /// <summary>
    /// Real's static-type notation for this path, as it appears inside the
    /// target-check messages.
    /// </summary>
    public string Describe()
    {
        if (this.Kind == XmlDmlNodeKind.Document)
            return "document { (element(*,xdt:untyped) ? & text ? & comment ? & processing-instruction ?) * }";
        var occurrence = this.Singleton ? " ?" : " *";
        return this.Kind switch
        {
            XmlDmlNodeKind.Attribute => $"attribute({this.Name},xdt:untypedAtomic){occurrence}",
            XmlDmlNodeKind.Comment => $"comment{occurrence}",
            XmlDmlNodeKind.Element => $"element({this.Name},xdt:untyped){occurrence}",
            XmlDmlNodeKind.ProcessingInstruction => $"processing-instruction{occurrence}",
            _ => $"text{occurrence}",
        };
    }
}

/// <summary>
/// The XML-DML sublanguage behind the <c>xml</c> type's <c>.modify()</c>
/// mutator: <c>insert</c>, <c>delete</c> and <c>replace value of</c>. The text
/// is a compile-time literal, so the whole statement — path, content
/// constructors, static target checks — is parsed once at
/// <see cref="Parse"/> and only the value terms are read per row.
/// </summary>
/// <remarks>
/// <para>
/// Path selection reuses <see cref="XmlQueryEngine"/>'s prolog parsing and
/// XPath 1.0 translation, so <c>.modify()</c> reaches exactly the path subset
/// the read methods do. Mutation runs over a LINQ-to-XML tree recovered from
/// the matched <see cref="XPathNavigator"/>s, and the result is re-serialized
/// by <see cref="Serialize"/> in SQL Server's own output shape — which is why
/// a modified instance comes back normalized (insignificant whitespace and
/// any XML declaration dropped, CDATA folded into escaped text, empty elements
/// self-closing) exactly as real returns it.
/// </para>
/// </remarks>
internal sealed class XmlDml
{
    /// <summary>Which of the three statements this is.</summary>
    public readonly XmlDmlKind Kind;

    /// <summary>The path naming the node the statement acts on.</summary>
    public readonly XmlDmlPath Target;

    /// <summary>The content sequence of an <c>insert</c>; empty otherwise.</summary>
    public readonly XmlDmlItem[] Content;

    /// <summary>The placement of an <c>insert</c>'s content.</summary>
    public readonly XmlDmlPosition Position;

    /// <summary>
    /// The compiled <c>with</c> expression of a <c>replace value of</c>, or
    /// null for the other two statements. Real takes a whole XQuery expression
    /// here — AdventureWorks' <c>Sales.iduSalesOrderDetail</c> writes
    /// <c>data(…)[1] + sql:column("inserted.LineTotal")</c> — so it compiles
    /// through the same engine a read method's path does, with
    /// <see cref="ValueAccessors"/> naming the slots the row fills.
    /// </summary>
    public readonly XmlQueryExpr? Value;

    /// <summary>
    /// The <c>sql:variable</c> / <c>sql:column</c> accessors <see cref="Value"/>
    /// reads, each with the slot its value is written to before evaluation.
    /// </summary>
    public readonly XmlSqlAccessorRef[] ValueAccessors;

    /// <summary>
    /// The prolog's namespace scope. A direct element constructor resolves its
    /// name through it just like a path step does, so
    /// <c>declare default element namespace "urn:d"; insert &lt;b/&gt;</c>
    /// builds a <c>urn:d</c> element (probe-confirmed).
    /// </summary>
    private readonly string? defaultElementNamespace;
    private readonly Dictionary<string, string> prefixes;

    /// <summary>
    /// What real writes between the brackets of this statement's own
    /// diagnostics — the method name behind the receiver that named it, so a
    /// column receiver's errors carry its <c>schema.table.column.</c> prefix.
    /// Carried onto the instance because the insert path types its content at
    /// <em>run</em> time, where the parser is long gone.
    /// </summary>
    private readonly string method;

    private XmlDml(
        XmlDmlKind kind,
        XmlDmlPath target,
        XmlDmlItem[] content,
        XmlDmlPosition position,
        XmlQueryExpr? value,
        XmlSqlAccessorRef[] valueAccessors,
        string? defaultElementNamespace,
        Dictionary<string, string> prefixes,
        string method)
    {
        this.Kind = kind;
        this.Target = target;
        this.Content = content;
        this.Position = position;
        this.Value = value;
        this.ValueAccessors = valueAccessors;
        this.defaultElementNamespace = defaultElementNamespace;
        this.prefixes = prefixes;
        this.method = method;
    }

    /// <summary>Builds a parsed <c>delete</c>.</summary>
    public static XmlDml CreateDelete(XmlDmlPath target, string? defaultElementNamespace, Dictionary<string, string> prefixes, string method) =>
        new(XmlDmlKind.Delete, target, [], XmlDmlPosition.Into, null, [], defaultElementNamespace, prefixes, method);

    /// <summary>Builds a parsed <c>insert</c>.</summary>
    public static XmlDml CreateInsert(XmlDmlPath target, XmlDmlItem[] content, XmlDmlPosition position, string? defaultElementNamespace, Dictionary<string, string> prefixes, string method) =>
        new(XmlDmlKind.Insert, target, content, position, null, [], defaultElementNamespace, prefixes, method);

    /// <summary>Builds a parsed <c>replace value of</c>.</summary>
    public static XmlDml CreateReplaceValueOf(
        XmlDmlPath target,
        XmlQueryExpr value,
        XmlSqlAccessorRef[] valueAccessors,
        string? defaultElementNamespace,
        Dictionary<string, string> prefixes,
        string method) =>
        new(XmlDmlKind.ReplaceValueOf, target, [], XmlDmlPosition.Into, value, valueAccessors, defaultElementNamespace, prefixes, method);

    /// <summary>
    /// Parses one XML-DML statement, applying every check real settles at
    /// compile time. <paramref name="resolveColumnType"/> supplies the type of
    /// a <c>sql:column</c> reference when the caller has a column scope (the
    /// UPDATE SET list); null leaves such a term's atomicity to execution.
    /// </summary>
    public static XmlDml Parse(
        string xquery,
        ParserContext context,
        Func<string, SqlType>? resolveColumnType,
        Schemas.XmlSchemaCollection? schemaCollection,
        string method)
    {
        var (defaultNamespace, prefixes, body) = XmlQueryEngine.ParsePrologAndBody(xquery);
        return new XmlDmlParser(body, defaultNamespace, prefixes, context, resolveColumnType, schemaCollection, method).ParseStatement();
    }

    /// <summary>
    /// Applies the statement to <paramref name="xmlText"/> and returns the
    /// mutated instance's serialization. A path that selects nothing is a
    /// no-op, matching real.
    /// </summary>
    public string Apply(string xmlText, RuntimeContext runtime)
    {
        if (xmlText.AsSpan().Trim().IsEmpty)
            return xmlText;

        var container = XmlInstance.CreateMutableContainer(xmlText);
        var navigator = XmlInstance.CreateMutableNavigator(container);
        var selected = new List<XObject>();
        foreach (var item in XmlQueryEngine.Select(navigator, this.Target.Compiled))
        {
            if (item is XPathNavigator node && node.UnderlyingObject is XObject matched)
                selected.Add(matched);
        }

        switch (this.Kind)
        {
            case XmlDmlKind.Delete:
                foreach (var node in selected)
                    Remove(node);
                break;
            case XmlDmlKind.ReplaceValueOf when selected.Count > 0:
                ReplaceValue(selected[0], this.EvaluateReplacement(navigator, runtime));
                break;
            case XmlDmlKind.Insert when selected.Count > 0:
                this.InsertContent(selected[0], runtime);
                break;
        }
        return Serialize(container);
    }

    /// <summary>
    /// Serializes a mutated instance the way SQL Server returns one: no XML
    /// declaration, empty elements self-closing with no space before the
    /// slash, CDATA sections folded into escaped text, and the same
    /// position-dependent escaping <c>FOR XML</c> applies (probe-confirmed
    /// against SQL Server 2025). The container's children are the instance's
    /// top-level nodes, so a fragment writes each of them in turn.
    /// </summary>
    internal static string Serialize(XContainer container)
    {
        var sb = new StringBuilder();
        foreach (var node in container.Nodes())
            AppendNode(sb, node, XNamespace.None);
        return sb.ToString();
    }

    /// <summary>
    /// Writes one node. <paramref name="inScopeDefault"/> is the default
    /// namespace an ancestor declared, so an element the edit moved out of that
    /// scope re-declares its own — the <c>xmlns=""</c> real writes when an
    /// unqualified constructed element lands under a namespaced parent.
    /// </summary>
    private static void AppendNode(StringBuilder sb, XNode node, XNamespace inScopeDefault)
    {
        switch (node)
        {
            case XElement element:
                var name = NameOf(element.Name, element);
                var ownDefault = element.Attributes().FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None);
                // A prefixed name carries its own binding; an unprefixed one
                // reads the default namespace, so it needs a declaration when
                // the scope it sits in doesn't already bind exactly that.
                var needsDefault = ownDefault is null
                    && !name.Contains(':', StringComparison.Ordinal)
                    && element.Name.Namespace != inScopeDefault;
                var childDefault = ownDefault is not null ? XNamespace.Get(ownDefault.Value)
                    : needsDefault ? element.Name.Namespace
                    : inScopeDefault;
                _ = sb.Append('<').Append(name);
                if (needsDefault)
                    _ = sb.Append(" xmlns=\"").Append(element.Name.NamespaceName).Append('"');
                foreach (var attribute in element.Attributes())
                {
                    _ = sb.Append(' ').Append(AttributeNameOf(attribute, element)).Append("=\"");
                    Selection.AppendForXmlText(sb, attribute.Value, isAttribute: true);
                    _ = sb.Append('"');
                }
                if (!element.Nodes().Any())
                {
                    _ = sb.Append("/>");
                    break;
                }
                _ = sb.Append('>');
                foreach (var child in element.Nodes())
                    AppendNode(sb, child, childDefault);
                _ = sb.Append("</").Append(name).Append('>');
                break;
            case XComment comment:
                _ = sb.Append("<!--").Append(comment.Value).Append("-->");
                break;
            case XProcessingInstruction instruction:
                _ = sb.Append("<?").Append(instruction.Target);
                if (instruction.Data.Length > 0)
                    _ = sb.Append(' ').Append(instruction.Data);
                _ = sb.Append("?>");
                break;
            case XText text:
                Selection.AppendForXmlText(sb, text.Value, isAttribute: false);
                break;
        }
    }

    /// <summary>
    /// Renders an element's name with the prefix bound to its namespace in
    /// scope; an unprefixed binding (the default namespace) leaves the local
    /// name bare, since the <c>xmlns</c> declaration itself rides along as an
    /// attribute.
    /// </summary>
    private static string NameOf(XName name, XElement scope)
    {
        if (name.Namespace == XNamespace.None)
            return name.LocalName;
        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : $"{prefix}:{name.LocalName}";
    }

    private static string AttributeNameOf(XAttribute attribute, XElement scope) =>
        attribute.IsNamespaceDeclaration
            ? attribute.Name.Namespace == XNamespace.None ? "xmlns" : $"xmlns:{attribute.Name.LocalName}"
            : NameOf(attribute.Name, scope);

    private static void Remove(XObject node)
    {
        switch (node)
        {
            case XAttribute attribute:
                attribute.Remove();
                break;
            case XNode other when other.Parent is not null || other.Document is not null:
                other.Remove();
                break;
        }
    }

    /// <summary>
    /// Writes the replacement string into the target. Emptying a text node
    /// removes it, so the owning element comes back self-closing — real's
    /// answer to <c>replace value of (…/text())[1] with ""</c>.
    /// </summary>
    /// <summary>
    /// The column reference a <c>sql:column</c> argument writes. Real takes the
    /// multi-part form — AdventureWorks' <c>Sales.iduSalesOrderDetail</c> reads
    /// <c>sql:column("inserted.LineTotal")</c> — so the dots separate qualifiers
    /// rather than belonging to one name, and a bracketed segment unwraps.
    /// </summary>
    internal static MultiPartName ColumnNameOf(string written)
    {
        var dot = written.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
            return new MultiPartName(Unbracket(written));

        var name = new MultiPartName(Unbracket(written[..dot]));
        foreach (var part in written[(dot + 1)..].Split('.'))
            name = name.WithAddedPart(Unbracket(part));
        return name;

        static string Unbracket(string part) =>
            part.Length >= 2 && part[0] == '[' && part[^1] == ']' ? part[1..^1].Replace("]]", "]", StringComparison.Ordinal) : part;
    }

    /// <summary>
    /// Evaluates the <c>with</c> expression against the instance being
    /// mutated, with each <c>sql:</c> accessor's value written into the scope
    /// the compiled expression reads. A NULL accessor binds the empty sequence,
    /// which atomizes to nothing — real's own reading.
    /// </summary>
    private string EvaluateReplacement(XPathNavigator instance, RuntimeContext runtime)
    {
        XmlVariableScope? scope = null;
        if (this.ValueAccessors.Length > 0)
        {
            scope = new XmlVariableScope();
            foreach (var accessor in this.ValueAccessors)
            {
                var value = accessor.IsColumn
                    ? runtime.ResolveColumn(ColumnNameOf(accessor.Name))
                    : runtime.Batch.Variables[accessor.Name.StartsWith('@') ? accessor.Name[1..] : accessor.Name].Value;
                scope.Write(accessor.Slot, value.IsNull ? [] : [Selection.ScalarForXmlText(value)]);
            }
        }

        var items = scope is null
            ? XmlQueryEngine.Select(instance, this.Value!)
            : XmlQueryEngine.Select(instance, this.Value!, scope);
        if (items.Count == 1)
            return XmlQueryValues.StringValue(items[0]);

        var text = new StringBuilder();
        foreach (var item in items)
        {
            if (text.Length > 0)
                _ = text.Append(' ');
            _ = text.Append(XmlQueryValues.StringValue(item));
        }
        return text.ToString();
    }

    private static void ReplaceValue(XObject target, string replacement)
    {
        switch (target)
        {
            case XAttribute attribute:
                attribute.Value = replacement;
                break;
            case XText text when replacement.Length == 0:
                text.Remove();
                break;
            case XText text:
                text.Value = replacement;
                break;

            // An element target is the typed-instance case: the collection
            // says the element holds a value, so the write replaces its content
            // and leaves its attributes standing.
            case XElement element when replacement.Length == 0:
                element.Nodes().Remove();
                break;
            case XElement element:
                element.ReplaceNodes(new XText(replacement));
                break;
        }
    }

    private void InsertContent(XObject target, RuntimeContext runtime)
    {
        var attributes = new List<XAttribute>();
        var nodes = new List<XNode>();
        foreach (var item in this.Content)
            Materialize(item, runtime, attributes, nodes);

        if (target is not XNode targetNode)
            return;
        if (attributes.Count > 0 && targetNode is XElement owner)
        {
            // Only `into` reaches here with attributes — Msg 2258 rejected the
            // positional forms at parse — so the element is the target itself.
            InsertAttributes(owner, attributes);
        }
        if (nodes.Count == 0)
            return;

        switch (this.Position)
        {
            case XmlDmlPosition.Before:
            case XmlDmlPosition.After:
                if (this.Position == XmlDmlPosition.Before)
                    targetNode.AddBeforeSelf(nodes);
                else
                    targetNode.AddAfterSelf(nodes);
                break;
            case XmlDmlPosition.AsFirst:
                ((XContainer)targetNode).AddFirst(nodes);
                break;
            default:
                ((XContainer)targetNode).Add(nodes);
                break;
        }
    }

    /// <summary>
    /// Threads inserted attributes into the element's list the way real's
    /// internal node order does. An instance's own attributes sit at the odd
    /// ordinals 1, 3, 5, … and the <em>i</em>-th attribute one statement adds
    /// takes ordinal 2<em>i</em>, so a single insert lands right after the first
    /// attribute and a sequence of them interleaves one per gap before
    /// spilling to the end — <c>&lt;a m n o p/&gt;</c> plus <c>(z, y)</c> comes
    /// back <c>m z n y o p</c> (probe-confirmed one shape at a time, namespace
    /// declarations counting as attributes like any other).
    /// </summary>
    private static void InsertAttributes(XElement owner, List<XAttribute> added)
    {
        var existing = new List<XAttribute>();
        foreach (var attribute in owner.Attributes())
            existing.Add(attribute);
        foreach (var attribute in added)
        {
            if (owner.Attribute(attribute.Name) is not null)
                throw SimulatedSqlException.XmlDuplicateAttribute(attribute.Name.LocalName);
        }

        owner.RemoveAttributes();
        if (existing.Count > 0)
            owner.Add(existing[0]);
        for (var i = 0; i < Math.Max(added.Count, existing.Count - 1); i++)
        {
            if (i < added.Count)
                owner.Add(added[i]);
            if (i + 1 < existing.Count)
                owner.Add(existing[i + 1]);
        }
    }

    /// <summary>
    /// Turns one content item into the attributes / nodes it contributes. A
    /// bare value term is legal only when it carries <c>xml</c>; anything else
    /// is an atomic value, which real refuses with Msg 2207 — statically when
    /// the type is known at parse, here when only the row can say.
    /// </summary>
    private void Materialize(XmlDmlItem item, RuntimeContext runtime, List<XAttribute> attributes, List<XNode> nodes)
    {
        switch (item.Kind)
        {
            case XmlDmlItemKind.Attribute:
                attributes.Add(new XAttribute(item.Name, Atomize(item.Enclosed[0], runtime)));
                break;
            case XmlDmlItemKind.Element:
                nodes.Add(this.BuildElement(item, runtime));
                break;
            case XmlDmlItemKind.Text:
                nodes.Add(new XText(Atomize(item.Enclosed[0], runtime)));
                break;
            case XmlDmlItemKind.Value:
                var value = item.Enclosed[0][0].Evaluate(runtime);
                if (value.IsNull)
                    break;
                if (value.Type is not XmlSqlType)
                    throw SimulatedSqlException.XmlDmlOnlyNodesInsertable(this.method, XQueryTypeName(value.Type, isLiteral: false));
                // A stored value is already-serialized XML, so it brings its own
                // namespace scope rather than the prolog's.
                AppendFragment(nodes, value.AsString, prologScope: null);
                break;
            default:
                var markup = new StringBuilder(item.Literals[0]);
                for (var i = 0; i < item.Enclosed.Length; i++)
                {
                    Selection.AppendForXmlText(markup, Atomize(item.Enclosed[i], runtime), item.EnclosedInAttribute[i]);
                    _ = markup.Append(item.Literals[i + 1]);
                }
                this.AppendFragment(nodes, markup.ToString(), this.PrologScope());
                break;
        }
    }

    /// <summary>
    /// Builds a computed <c>element name {…}</c> constructor's node. The name
    /// resolves through the prolog exactly as a path step's does, and the
    /// content items nest — attributes among them land on the element, atomic
    /// terms become its text.
    /// </summary>
    private XElement BuildElement(XmlDmlItem item, RuntimeContext runtime)
    {
        var colon = item.Name.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : item.Name[..colon];
        var local = colon < 0 ? item.Name : item.Name[(colon + 1)..];
        var uri = colon < 0 ? this.defaultElementNamespace : this.prefixes[prefix];
        var element = new XElement(uri is null ? XName.Get(local) : XName.Get(local, uri));
        if (prefix.Length > 0)
            element.Add(new XAttribute(XNamespace.Xmlns + prefix, uri!));

        var attributes = new List<XAttribute>();
        var nodes = new List<XNode>();
        var atomics = new List<string>();
        foreach (var child in item.Children)
        {
            // Inside a constructor the content sequence's atomic items are the
            // element's text — the Msg 2207 rule that guards a top-level insert
            // doesn't apply — and adjacent ones join with a single space, real's
            // own atomization (`element n {"a","b"}` is `<n>a b</n>`).
            if (child.Kind == XmlDmlItemKind.Value && child.Enclosed[0][0].StaticType is not XmlSqlType)
            {
                atomics.Add(Atomize(child.Enclosed[0], runtime));
                continue;
            }
            Flush();
            this.Materialize(child, runtime, attributes, nodes);
        }
        Flush();

        foreach (var attribute in attributes)
            element.Add(attribute);
        foreach (var node in nodes)
            element.Add(node);
        return element;

        void Flush()
        {
            if (atomics.Count == 0)
                return;
            nodes.Add(new XText(string.Join(' ', atomics)));
            atomics.Clear();
        }
    }

    /// <summary>
    /// The namespace declarations a direct constructor parses under: the
    /// prolog's default element namespace plus each declared prefix, written as
    /// wrapper attributes so <c>&lt;p:b/&gt;</c> resolves the same way a path
    /// step's <c>p:b</c> does.
    /// </summary>
    private string PrologScope()
    {
        var sb = new StringBuilder();
        if (this.defaultElementNamespace is { } uri)
            _ = sb.Append(" xmlns=\"").Append(uri).Append('"');
        foreach (var (prefix, mapped) in this.prefixes)
            _ = sb.Append(" xmlns:").Append(prefix).Append("=\"").Append(mapped).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Parses a markup fragment through a throwaway wrapper element so a
    /// multi-node result (or a stored <c>xml</c> value holding several
    /// top-level nodes) contributes all of its nodes.
    /// <paramref name="prologScope"/> carries the constructor's in-scope
    /// namespace declarations; each is copied onto the top-level nodes that use
    /// it, since the wrapper doesn't survive the insert.
    /// </summary>
    private void AppendFragment(List<XNode> nodes, string markup, string? prologScope)
    {
        var wrapper = XElement.Parse($"<x{prologScope}>{markup}</x>");
        foreach (var node in wrapper.Nodes())
        {
            if (prologScope is not null && node is XElement element)
                this.CarryPrefixDeclarations(element);
            nodes.Add(node);
        }
    }

    /// <summary>
    /// Re-declares on <paramref name="element"/> every prolog prefix its
    /// subtree actually uses, so the binding outlives the parse wrapper. Real
    /// writes the same declaration when the insertion point has no binding for
    /// the prefix, and omits it when it has one — the simulator always writes
    /// it, so a target that already declares the prefix ends up carrying it
    /// twice.
    /// </summary>
    private void CarryPrefixDeclarations(XElement element)
    {
        foreach (var (prefix, uri) in this.prefixes)
        {
            XNamespace mapped = uri;
            if (element.Attribute(XNamespace.Xmlns + prefix) is not null)
                continue;
            if (!element.DescendantsAndSelf().Any(e => e.Name.Namespace == mapped || e.Attributes().Any(a => a.Name.Namespace == mapped)))
                continue;
            element.Add(new XAttribute(XNamespace.Xmlns + prefix, uri));
        }
    }

    /// <summary>
    /// The string value of a term sequence: each term's XML text form, joined
    /// by a single space (real's atomization of a sequence in a <c>with</c>
    /// clause or an enclosed expression). A NULL term contributes nothing.
    /// </summary>
    private static string Atomize(XmlDmlTerm[] terms, RuntimeContext runtime)
    {
        if (terms.Length == 1)
        {
            var single = terms[0].Evaluate(runtime);
            return single.IsNull ? string.Empty : Selection.ScalarForXmlText(single);
        }
        var sb = new StringBuilder();
        foreach (var term in terms)
        {
            var value = term.Evaluate(runtime);
            if (value.IsNull)
                continue;
            if (sb.Length > 0)
                _ = sb.Append(' ');
            _ = sb.Append(Selection.ScalarForXmlText(value));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The XQuery type name real names in Msg 2207 for a SQL type, with the
    /// occurrence indicator a <c>sql:</c> accessor carries and a written
    /// literal doesn't. An integer literal is <c>xs:integer</c> where an
    /// <c>int</c> variable is <c>xs:int</c> (probe-confirmed).
    /// </summary>
    internal static string XQueryTypeName(SqlType type, bool isLiteral)
    {
        var name = type switch
        {
            BigIntSqlType => "xs:long",
            BitSqlType => "xs:boolean",
            DecimalSqlType => "xs:decimal",
            FloatSqlType => "xs:double",
            Int32SqlType => isLiteral ? "xs:integer" : "xs:int",
            RealSqlType => "xs:float",
            SmallIntSqlType => "xs:short",
            TinyIntSqlType => "xs:unsignedByte",
            _ => "xs:string",
        };
        return isLiteral ? name : $"{name} ?";
    }
}
