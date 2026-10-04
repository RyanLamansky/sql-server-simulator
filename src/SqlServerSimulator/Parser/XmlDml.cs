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

/// <summary>
/// A <c>sql:variable("@v")</c> or <c>sql:column("c")</c> accessor written as
/// an insert's bare content — the one position an <c>xml</c> value may take.
/// </summary>
internal readonly struct XmlDmlTerm
{
    /// <summary>True for <c>sql:column</c>, false for <c>sql:variable</c>.</summary>
    public readonly bool IsColumn;

    /// <summary>The variable (no <c>@</c>) or column name the term reads.</summary>
    public readonly string Name;

    /// <summary>
    /// The term's compile-time type, or null when only execution can say
    /// (<c>sql:column</c> outside an UPDATE's SET list, where no column-type
    /// resolver is in scope while the modify text parses).
    /// </summary>
    public readonly SqlType? StaticType;

    private XmlDmlTerm(bool isColumn, string name, SqlType? staticType)
    {
        this.IsColumn = isColumn;
        this.Name = name;
        this.StaticType = staticType;
    }

    /// <summary>Builds a <c>sql:variable</c> term over an already-declared variable.</summary>
    public static XmlDmlTerm FromVariable(string name, SqlType declaredType) => new(isColumn: false, name, declaredType);

    /// <summary>Builds a <c>sql:column</c> term; <paramref name="staticType"/> is null when unresolvable at parse.</summary>
    public static XmlDmlTerm FromColumn(string name, SqlType? staticType) => new(isColumn: true, name, staticType);

    /// <summary>Reads the term's value for the row being mutated.</summary>
    public SqlValue Evaluate(RuntimeContext runtime) =>
        this.IsColumn ? runtime.ResolveColumn(XmlDml.ColumnNameOf(this.Name)) : runtime.Batch.Variables[this.Name].Value;
}

/// <summary>
/// One item of an <c>insert</c> whose content is a bare <c>sql:variable</c> /
/// <c>sql:column</c> accessor — the one content form that may carry a whole
/// <c>xml</c> instance. Every other content is an XQuery expression.
/// </summary>
internal sealed class XmlDmlItem(XmlDmlTerm term)
{
    /// <summary>The accessor.</summary>
    public readonly XmlDmlTerm Term = term;

    /// <summary>Wraps one accessor.</summary>
    public static XmlDmlItem Value(XmlDmlTerm term) => new(term);
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

    /// <summary>The bare-accessor content of an <c>insert</c>; empty otherwise.</summary>
    public readonly XmlDmlItem[] Content;

    /// <summary>
    /// The content expression of an <c>insert</c> that isn't a bare accessor,
    /// evaluated against the instance before the edit — so a path copies nodes
    /// out of it — with <see cref="contentAccessors"/> filled per row.
    /// </summary>
    private readonly XmlQueryExpr? contentExpression;

    private readonly XmlSqlAccessorRef[] contentAccessors;

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
        XmlQueryExpr? contentExpression,
        XmlSqlAccessorRef[] contentAccessors,
        XmlDmlPosition position,
        XmlQueryExpr? value,
        XmlSqlAccessorRef[] valueAccessors,
        string method)
    {
        this.Kind = kind;
        this.Target = target;
        this.Content = content;
        this.contentExpression = contentExpression;
        this.contentAccessors = contentAccessors;
        this.Position = position;
        this.Value = value;
        this.ValueAccessors = valueAccessors;
        this.method = method;
    }

    /// <summary>Builds a parsed <c>delete</c>.</summary>
    public static XmlDml CreateDelete(XmlDmlPath target, string method) =>
        new(XmlDmlKind.Delete, target, [], null, [], XmlDmlPosition.Into, null, [], method);

    /// <summary>Builds a parsed <c>insert</c>.</summary>
    public static XmlDml CreateInsert(
        XmlDmlPath target,
        XmlDmlItem[] content,
        XmlQueryExpr? contentExpression,
        XmlSqlAccessorRef[] contentAccessors,
        XmlDmlPosition position,
        string method) =>
        new(XmlDmlKind.Insert, target, content, contentExpression, contentAccessors, position, null, [], method);

    /// <summary>Builds a parsed <c>replace value of</c>.</summary>
    public static XmlDml CreateReplaceValueOf(
        XmlDmlPath target,
        XmlQueryExpr value,
        XmlSqlAccessorRef[] valueAccessors,
        bool targetNillable,
        string method) =>
        new(XmlDmlKind.ReplaceValueOf, target, [], null, [], XmlDmlPosition.Into, value, valueAccessors, method) { targetNillable = targetNillable };

    /// <summary>
    /// Whether a <c>replace value of</c> target is an element the receiver's
    /// schema declares <c>nillable</c>, which an empty value sets
    /// <c>xsi:nil</c> on rather than refusing.
    /// </summary>
    private bool targetNillable;

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
        var (defaultNamespace, prefixes, body) = XmlQueryEngine.ParsePrologAndBody(xquery, method);
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
        var navigator = container.CreateNavigator();
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
                if (this.EvaluateReplacement(navigator, selected[0], runtime) is { } replacement)
                    ReplaceValue(selected[0], replacement);
                break;
            case XmlDmlKind.Insert when selected.Count > 0:
                this.InsertContent(selected[0], navigator, runtime);
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
    /// the compiled expression reads as the XQuery item real maps it to — a
    /// <c>money</c> 1.5 is <c>1.5</c>, a <c>float</c> 1e10 <c>1.0E10</c>, a
    /// <c>bit</c> <c>true</c> (probe-confirmed). A NULL accessor binds the
    /// empty sequence.
    /// </summary>
    /// <remarks>
    /// An empty result replaces nothing: real refuses it for an attribute or
    /// element target (Msg 6320), and for a text node unless the expression
    /// was written <c>()</c>, which removes the text (Msg 6325 otherwise).
    /// </remarks>
    private string? EvaluateReplacement(XPathNavigator instance, XObject target, RuntimeContext runtime)
    {
        var scope = BuildAccessorScope(this.ValueAccessors, runtime);
        var items = scope is null
            ? XmlQueryEngine.Select(instance, this.Value!)
            : XmlQueryEngine.Select(instance, this.Value!, scope);
        if (items.Count == 0)
        {
            if (target is XElement element && this.targetNillable)
            {
                SetNil(element);
                return null;
            }
            if (target is not XText)
                throw SimulatedSqlException.XmlDmlReplaceWithEmptyNotNillable();
            if (this.Value is not XmlSequenceExpr { IsEmpty: true })
                throw SimulatedSqlException.XmlDmlReplaceWithEmptySequence();
            return string.Empty;
        }
        return XmlQueryValues.JoinAtomized(items);
    }

    /// <summary>
    /// Reads each accessor's value for the row into a fresh scope, or null
    /// when there are none. A value typed <c>xml</c> — possible only where the
    /// accessor's type wasn't known while compiling — passes through as its
    /// text.
    /// </summary>
    private static XmlVariableScope? BuildAccessorScope(XmlSqlAccessorRef[] accessors, RuntimeContext runtime)
    {
        if (accessors.Length == 0)
            return null;
        var scope = new XmlVariableScope();
        foreach (var accessor in accessors)
        {
            var value = accessor.IsColumn
                ? runtime.ResolveColumn(ColumnNameOf(accessor.Name))
                : runtime.Batch.Variables[accessor.Name.StartsWith('@') ? accessor.Name[1..] : accessor.Name].Value;
            scope.Write(accessor.Slot, value.IsNull ? [] : [value.Type is XmlSqlType ? value.AsString : XmlAtomicTypes.FromSql(value)]);
        }
        return scope;
    }

    /// <summary>The XML Schema instance namespace <c>xsi:nil</c> lives in.</summary>
    private static readonly XNamespace SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Empties a nillable element and marks it <c>xsi:nil="true"</c>, declaring
    /// the <c>xsi</c> prefix on it when nothing in scope does — real's answer
    /// to <c>replace value of</c> a nillable element <c>with ()</c>.
    /// </summary>
    private static void SetNil(XElement element)
    {
        element.RemoveNodes();
        if (element.GetPrefixOfNamespace(SchemaInstance) is null)
            element.Add(new XAttribute(XNamespace.Xmlns + "xsi", SchemaInstance.NamespaceName));
        element.SetAttributeValue(SchemaInstance + "nil", "true");
    }

    /// <summary>
    /// Writes the replacement string into the target. Emptying a text node
    /// removes it, so the owning element comes back self-closing — real's
    /// answer to <c>replace value of (…/text())[1] with ""</c>.
    /// </summary>
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
                // A value replaces a nil marker along with the content.
                element.Attribute(SchemaInstance + "nil")?.Remove();
                element.ReplaceNodes(new XText(replacement));
                break;
        }
    }

    private void InsertContent(XObject target, XPathNavigator instance, RuntimeContext runtime)
    {
        var attributes = new List<XAttribute>();
        var nodes = new List<XNode>();
        if (this.contentExpression is not null)
            this.MaterializeExpression(instance, runtime, attributes, nodes);
        foreach (var item in this.Content)
            this.MaterializeAccessor(item.Term, runtime, nodes);

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

        foreach (var node in nodes)
        {
            if (node is XElement element)
                DropInheritedDeclarations(element);
        }
    }

    /// <summary>
    /// Removes the namespace declarations a constructed element took from the
    /// prolog that its insertion point already makes: real writes such a
    /// binding only where the new position doesn't have it, so <c>declare default element namespace
    /// "urn:d"; insert &lt;b/&gt;</c> under a <c>urn:d</c> parent comes back as
    /// a plain <c>&lt;b/&gt;</c> (probe-confirmed).
    /// </summary>
    private static void DropInheritedDeclarations(XElement element)
    {
        if (element.Parent is not { } parent)
            return;
        foreach (var declaration in element.Attributes().Where(a => a.Annotation<XmlPrologDeclaration>() is not null).ToList())
        {
            var inherited = declaration.Name.Namespace == XNamespace.None
                ? parent.GetDefaultNamespace().NamespaceName
                : parent.GetNamespaceOfPrefix(declaration.Name.LocalName)?.NamespaceName;
            if (inherited == declaration.Value)
                declaration.Remove();
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
    /// Evaluates the content expression against the instance as it stands
    /// before the edit and copies what it answers: a node selected out of the
    /// instance is inserted as a copy (<c>insert /r/b into (/r)[1]</c> doubles
    /// the <c>b</c>), a constructed one as built, an attribute onto the target.
    /// An atomic item is Msg 2207, which the compile already raised for every
    /// content whose type it knew.
    /// </summary>
    private void MaterializeExpression(XPathNavigator instance, RuntimeContext runtime, List<XAttribute> attributes, List<XNode> nodes)
    {
        var scope = BuildAccessorScope(this.contentAccessors, runtime);
        var items = scope is null
            ? XmlQueryEngine.Select(instance, this.contentExpression!)
            : XmlQueryEngine.Select(instance, this.contentExpression!, scope);
        foreach (var item in items)
        {
            switch (item)
            {
                case XPathNavigator { UnderlyingObject: XAttribute attribute }:
                    attributes.Add(new XAttribute(attribute));
                    break;
                case XPathNavigator { UnderlyingObject: XElement element }:
                    var copy = new XElement(element);
                    foreach (var declaration in element.Attributes())
                    {
                        if (declaration.Annotation<XmlPrologDeclaration>() is not null)
                            copy.Attribute(declaration.Name)?.AddAnnotation(XmlPrologDeclaration.Instance);
                    }
                    nodes.Add(copy);
                    break;
                case XPathNavigator { UnderlyingObject: XContainer container }:
                    foreach (var child in container.Nodes())
                        nodes.Add(CopyNode(child));
                    break;
                case XPathNavigator { UnderlyingObject: XNode node }:
                    nodes.Add(CopyNode(node));
                    break;
                case XPathNavigator text:
                    nodes.Add(new XText(text.Value));
                    break;
                case XmlEmptyTextNode:
                    break;
                default:
                    throw SimulatedSqlException.XmlDmlOnlyNodesInsertable(this.method, this.contentExpression!.AtomizedTypeName());
            }
        }
    }

    private static XNode CopyNode(XNode node) => node switch
    {
        XElement element => new XElement(element),
        XComment comment => new XComment(comment),
        XProcessingInstruction instruction => new XProcessingInstruction(instruction),
        XText text => new XText(text.Value),
        _ => node,
    };

    /// <summary>
    /// A bare accessor item: legal only when it carries <c>xml</c>; anything
    /// else is an atomic value, which real refuses with Msg 2207 — statically
    /// when the type is known at parse, here when only the row can say.
    /// </summary>
    private void MaterializeAccessor(XmlDmlTerm term, RuntimeContext runtime, List<XNode> nodes)
    {
        var value = term.Evaluate(runtime);
        if (value.IsNull)
            return;
        if (value.Type is not XmlSqlType)
            throw SimulatedSqlException.XmlDmlOnlyNodesInsertable(this.method, XQueryTypeName(value.Type));

        // A stored value is already-serialized XML, so it brings its own
        // namespace scope.
        var wrapper = XElement.Parse($"<x>{value.AsString}</x>");
        foreach (var node in wrapper.Nodes())
            nodes.Add(node);
    }

    /// <summary>
    /// The XQuery type name real names in Msg 2207 for a bare accessor, with
    /// the occurrence indicator an accessor carries (<c>xs:int ?</c>).
    /// </summary>
    internal static string XQueryTypeName(SqlType type) => $"{XmlAtomicTypes.SqlTypeName(type) ?? "xs:string"} ?";
}
