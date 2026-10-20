using System.Xml;
using System.Xml.Schema;

namespace SqlServerSimulator.Storage;

/// <summary>
/// What a path over an instance bound to an XML schema collection can reach,
/// settled from the collection's declarations as real's static typing does:
/// the document node, or the elements a step's nodes may be — each a name and
/// the type it carries. A declaration's children are its type's content
/// model's, a type derived from it included — an instance may name one with
/// <c>xsi:type</c> — along with each child's substitution group; a name only
/// a content wildcard admits is an <c>xs:anyType</c> element, and so is any
/// child of one, a parent step's included. A step this model doesn't follow
/// leaves the path untyped, which only ever skips a check real makes (probed
/// 2026-10-20 against SQL Server 2025).
/// </summary>
internal sealed class XmlSchemaPathType
{
    private readonly XmlSchemaSet schemas;

    /// <summary>Whether this is the document node, whose children are the collection's global elements.</summary>
    private readonly bool document;

    /// <summary>Whether this is an element of any name typed <c>xs:anyType</c>, as a parent step's node is.</summary>
    private readonly bool anyElement;

    /// <summary>The elements, in real's union order; empty for the document node and an any element.</summary>
    private readonly Entry[] elements;

    /// <summary>Whether the elements are the document's children, which real writes as an interleave of optional items.</summary>
    private readonly bool documentChildren;

    /// <summary>
    /// Whether these are what <c>//</c> reaches from the document node, whose
    /// union real writes with the document in it — a notation not modeled —
    /// so a step finding nothing here goes unrefused.
    /// </summary>
    private readonly bool fromDocument;

    private XmlSchemaPathType(XmlSchemaSet schemas, bool document, bool anyElement, Entry[] elements, bool documentChildren, bool fromDocument = false)
    {
        this.schemas = schemas;
        this.document = document;
        this.anyElement = anyElement;
        this.elements = elements;
        this.documentChildren = documentChildren;
        this.fromDocument = fromDocument;
    }

    /// <summary>
    /// One node a step may reach: an element's name and the type it carries,
    /// or — in a <c>//</c> step's union — a comment, processing instruction or
    /// text node, named by <see cref="NodeKind"/>.
    /// </summary>
    private readonly struct Entry(XmlQualifiedName name, XmlSchemaType? type, string? nodeKind = null)
    {
        public readonly XmlQualifiedName Name = name;
        public readonly XmlSchemaType? Type = type;
        public readonly string? NodeKind = nodeKind;

        /// <summary>
        /// Whether real's union lists the two once: the same name typed the
        /// same way, as a derived type's content model repeats its base's.
        /// </summary>
        public bool Same(Entry other) =>
            this.NodeKind is { } kind ? kind == other.NodeKind
            : other.NodeKind is null
            && this.Name == other.Name
            && (ReferenceEquals(this.Type, other.Type)
                || (this.Type is { QualifiedName.IsEmpty: false } mine && other.Type is { } theirs && mine.QualifiedName == theirs.QualifiedName));
    }

    /// <summary>The document node of an instance bound to <paramref name="schemas"/>.</summary>
    public static XmlSchemaPathType Document(XmlSchemaSet schemas) => new(schemas, document: true, anyElement: false, [], documentChildren: false);

    /// <summary>
    /// What <c>//</c>'s <c>descendant-or-self::node()</c> reaches from these
    /// elements, in real's union order — each element, then for each child
    /// element in content order that child and what it holds in turn, then the
    /// element's own comments, processing instructions and, under simple or
    /// mixed content, text — or null where a wildcard or <c>xs:anyType</c>
    /// lies below.
    /// </summary>
    public XmlSchemaPathType? DescendantOrSelf()
    {
        if (this.anyElement)
            return null;
        var reached = new List<Entry>();
        var expanded = new HashSet<XmlSchemaType>();
        if (this.document)
        {
            foreach (XmlSchemaElement global in this.schemas.GlobalElements.Values)
            {
                Add(reached, new(global.QualifiedName, global.ElementSchemaType));
                if (!this.Descend(global.ElementSchemaType, reached, expanded))
                    return null;
            }
            return new(this.schemas, document: false, anyElement: false, [.. reached], documentChildren: false, fromDocument: true);
        }
        foreach (var element in this.elements)
        {
            if (element.NodeKind is not null)
                continue;
            Add(reached, element);
            if (!this.Descend(element.Type, reached, expanded))
                return null;
        }
        return new(this.schemas, document: false, anyElement: false, [.. reached], documentChildren: false);
    }

    private bool Descend(XmlSchemaType? type, List<Entry> reached, HashSet<XmlSchemaType> expanded)
    {
        if (type is null || !expanded.Add(type))
            return true;
        var children = new List<Entry>();
        var wildcards = new List<XmlSchemaAny>();
        if (this.CollectChildren(type, children, wildcards) || wildcards.Count > 0)
            return false;
        foreach (var child in children)
        {
            Add(reached, child);
            if (!this.Descend(child.Type, reached, expanded))
                return false;
        }
        Add(reached, new(XmlQualifiedName.Empty, null, "comment"));
        Add(reached, new(XmlQualifiedName.Empty, null, "processing-instruction"));
        if (type is not XmlSchemaComplexType { ContentType: XmlSchemaContentType.ElementOnly or XmlSchemaContentType.Empty })
            Add(reached, new(XmlQualifiedName.Empty, null, "text"));
        return true;
    }

    /// <summary>What a parent step reaches: an element of any name, typed <c>xs:anyType</c>.</summary>
    public XmlSchemaPathType Parent() => new(this.schemas, document: false, anyElement: true, [], documentChildren: false);

    /// <summary>
    /// The type a child step's matches have, or null when the step can reach
    /// something this model doesn't type. A name nothing declares here is
    /// real's refusal, quoting <paramref name="occurrence"/>'s suffix on the
    /// context's type.
    /// </summary>
    public XmlSchemaPathType? Child(XmlNodeTestKind test, string localName, string namespaceUri, XmlOccurrence occurrence, XmlSchemaNotation notation, string method)
    {
        var candidates = new List<Entry>();
        var wildcards = new List<XmlSchemaAny>();
        var anyName = this.anyElement;
        if (this.document)
        {
            foreach (XmlSchemaElement global in this.schemas.GlobalElements.Values)
                Add(candidates, new(global.QualifiedName, global.ElementSchemaType));
        }
        else
        {
            foreach (var entry in this.elements)
            {
                if (entry.NodeKind is null)
                    anyName |= this.CollectChildren(entry.Type, candidates, wildcards);
            }
        }

        var matched = new List<Entry>();
        foreach (var candidate in candidates)
        {
            var name = candidate.Name;
            var matches = test switch
            {
                XmlNodeTestKind.Name => name.Name == localName && name.Namespace == namespaceUri,
                XmlNodeTestKind.AnyNamespace => name.Name == localName,
                XmlNodeTestKind.AnyLocalName => name.Namespace == namespaceUri,
                XmlNodeTestKind.Wildcard => true,
                _ => false,
            };
            if (matches)
                Add(matched, candidate);
        }

        // A name the declarations don't reach may still be one a wildcard
        // admits: a skip wildcard's element is untyped, a lax one's takes a
        // global declaration's type or else xs:anyType, and a strict one
        // admits only a globally declared name. A test naming no single name
        // over a wildcard reaches names this model can't list.
        if (anyName || wildcards.Count > 0)
        {
            if (test != XmlNodeTestKind.Name)
                return null;
            var name = new XmlQualifiedName(localName, namespaceUri);
            var global = this.schemas.GlobalElements[name] as XmlSchemaElement;
            var admitted = anyName;
            foreach (var wildcard in wildcards)
            {
                if (!Admits(wildcard, namespaceUri))
                    continue;
                if (wildcard.ProcessContents == XmlSchemaContentProcessing.Skip)
                    return null;
                admitted |= !IsStrict(wildcard.ProcessContents) || global is not null;
            }
            if (admitted && !matched.Exists(entry => entry.Name == name))
                Add(matched, new(name, global?.ElementSchemaType ?? AnyType));
        }

        if (matched.Count > 0)
            return new(this.schemas, document: false, anyElement: false, [.. matched], documentChildren: this.document);
        if (test is not (XmlNodeTestKind.Name or XmlNodeTestKind.AnyNamespace) || this.fromDocument)
            return null;

        var uri = test == XmlNodeTestKind.Name ? namespaceUri : string.Empty;
        if (this.document)
        {
            throw uri.Length == 0
                ? SimulatedSqlException.XQueryNoSuchTopLevelElement(method, localName)
                : SimulatedSqlException.XQueryNoSuchTopLevelNamespacedElement(method, $"{{{uri}}}:{localName}");
        }
        var type = this.Render(notation, occurrence);
        throw uri.Length == 0
            ? SimulatedSqlException.XQueryNoSuchElementInType(method, localName, type)
            : SimulatedSqlException.XQueryNoSuchNamespacedElementInType(method, $"{{{uri}}}:{localName}", type);
    }

    /// <summary>
    /// Checks a named attribute step against the attributes each element's
    /// type carries — a derived type's included — which real refuses when none
    /// does. An attribute wildcard admitting the name's namespace passes it,
    /// a strict one — <c>anyAttribute</c>'s default — only for an attribute
    /// the collection declares globally.
    /// </summary>
    public void RequireAttribute(string localName, string namespaceUri, XmlOccurrence occurrence, XmlSchemaNotation notation, string method)
    {
        if (this.document || this.anyElement)
            return;
        foreach (var element in this.elements)
        {
            if (element.NodeKind is not null)
                continue;
            foreach (var type in this.TypeAndDerived(element.Type))
            {
                if (type is not XmlSchemaComplexType complex)
                    continue;
                if (IsAnyType(complex))
                    return;
                if (complex.AttributeWildcard is { } wildcard && Admits(wildcard, namespaceUri)
                    && (!IsStrict(wildcard.ProcessContents) || this.schemas.GlobalAttributes.Contains(new XmlQualifiedName(localName, namespaceUri))))
                {
                    return;
                }
                foreach (XmlSchemaAttribute attribute in complex.AttributeUses.Values)
                {
                    if (attribute.QualifiedName.Name == localName && attribute.QualifiedName.Namespace == namespaceUri)
                        return;
                }
            }
        }

        var contextType = this.Render(notation, occurrence);
        throw namespaceUri.Length == 0
            ? SimulatedSqlException.XQueryNoSuchAttributeInType(method, "@" + localName, contextType)
            : SimulatedSqlException.XQueryNoSuchNamespacedAttributeInType(method, $"@{{{namespaceUri}}}:{localName}", contextType);
    }

    /// <summary>
    /// How these elements atomize and are written, when every one carries the
    /// same type: the primitive their simple type — or simple content's —
    /// restricts, which their comparisons and arithmetic follow; the atomized
    /// type's own name, a user-derived type's or <c>&lt;anonymous&gt;</c>; and
    /// the element's type in node notation. Nulls where the types differ or
    /// the content isn't simple (probed 2026-10-20 against SQL Server 2025).
    /// A list type answers its item type, with <c>List</c> set: such an element
    /// atomizes into a sequence of its items.
    /// </summary>
    public (string? Primitive, string? Atomized, string? Node, bool List) SettledType(XmlSchemaNotation notation)
    {
        if (this.elements.Length == 0 || this.elements[0].NodeKind is not null)
            return default;
        var type = this.elements[0].Type;
        foreach (var element in this.elements)
        {
            if (element.NodeKind is not null || !ReferenceEquals(element.Type, type))
                return default;
        }
        var node = TypeNotation(type, notation);
        var simple = SimpleTypeOf(type);
        if (simple is { Datatype.Variety: XmlSchemaDatatypeVariety.List } && ListItemType(simple) is { } item && XmlStaticTyping.TypeNameOf(item) is { } itemPrimitive)
            return (itemPrimitive, AtomizedNotation(item, notation), node, true);
        return simple is not null && XmlStaticTyping.TypeNameOf(simple) is { } primitive
            ? (primitive, AtomizedNotation(simple, notation), node, false)
            : (null, null, node, false);
    }

    /// <summary>
    /// What an attribute step from these elements is declared with, when every
    /// element's type declares it the same way: its primitive and its
    /// atomized type's own name; nulls otherwise.
    /// </summary>
    public (string? Primitive, string? Atomized) AttributeType(string localName, string namespaceUri, XmlSchemaNotation notation)
    {
        XmlSchemaSimpleType? found = null;
        if (this.document || this.anyElement || this.elements.Length == 0)
            return default;
        foreach (var element in this.elements)
        {
            if (element.Type is not XmlSchemaComplexType complex
                || complex.AttributeUses[new XmlQualifiedName(localName, namespaceUri)] is not XmlSchemaAttribute { AttributeSchemaType: { } attributeType }
                || (found is not null && !ReferenceEquals(found, attributeType)))
            {
                return default;
            }
            found = attributeType;
        }
        return found is not null && XmlStaticTyping.TypeNameOf(found) is { } primitive ? (primitive, AtomizedNotation(found, notation)) : default;
    }

    /// <summary>A list type's item type, through restrictions of the list.</summary>
    private static XmlSchemaSimpleType? ListItemType(XmlSchemaSimpleType list)
    {
        for (XmlSchemaType? current = list; current is XmlSchemaSimpleType simple; current = current.BaseXmlSchemaType)
        {
            if (simple.Content is XmlSchemaSimpleTypeList { BaseItemType: { } item })
                return item;
        }
        return null;
    }

    /// <summary>The simple type an element typed <paramref name="type"/> atomizes as: its own, or its simple content's.</summary>
    private static XmlSchemaSimpleType? SimpleTypeOf(XmlSchemaType? type)
    {
        for (var current = type; current is not null; current = current.BaseXmlSchemaType)
        {
            switch (current)
            {
                case XmlSchemaSimpleType simple:
                    return simple;
                case XmlSchemaComplexType complex when complex.ContentType != XmlSchemaContentType.TextOnly || IsAnyType(complex):
                    return null;
            }
        }
        return null;
    }

    /// <summary>An atomized simple type's name: a built-in's <c>xs:</c> one, a user-derived one's own, or <c>&lt;anonymous&gt;</c>.</summary>
    private static string AtomizedNotation(XmlSchemaSimpleType simple, XmlSchemaNotation notation) =>
        simple.QualifiedName.IsEmpty ? "<anonymous>" : TypeNotation(simple, notation);

    /// <summary>
    /// Whether a <c>text()</c> child step can only be empty: every element's
    /// content is elements alone or nothing.
    /// </summary>
    public bool HoldsNoText()
    {
        if (this.elements.Length == 0)
            return false;
        foreach (var element in this.elements)
        {
            if (element.NodeKind is not null)
                return false;
            foreach (var type in this.TypeAndDerived(element.Type))
            {
                if (type is not XmlSchemaComplexType { ContentType: XmlSchemaContentType.ElementOnly or XmlSchemaContentType.Empty } complex || IsAnyType(complex))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Refuses atomizing these elements when one's type has complex content
    /// (Msg 9314, <c>xs:anyType</c> and mixed content included) or none at
    /// all (Msg 2213), the first such in union order deciding, quoting
    /// <paramref name="occurrence"/>'s suffix on the type.
    /// </summary>
    public void RequireAtomizable(XmlOccurrence occurrence, XmlSchemaNotation notation, string method)
    {
        foreach (var element in this.elements)
        {
            if (element.Type is not XmlSchemaComplexType complex)
                continue;
            if (IsAnyType(complex) || complex.ContentType is XmlSchemaContentType.ElementOnly or XmlSchemaContentType.Mixed)
                throw SimulatedSqlException.XQueryAtomizesComplexContent(method, FoundTypeOf(element, notation), this.Render(notation, occurrence));
            if (complex.ContentType == XmlSchemaContentType.Empty)
                throw SimulatedSqlException.XQueryAtomizesEmptyContent(method, FoundTypeOf(element, notation), this.Render(notation, occurrence));
        }
    }

    /// <summary>
    /// Adds the elements a child of an element typed <paramref name="type"/>
    /// may be and the content wildcards that admit others, answering whether
    /// the child may have any name at all — the content of <c>xs:anyType</c>.
    /// </summary>
    private bool CollectChildren(XmlSchemaType? type, List<Entry> children, List<XmlSchemaAny> wildcards)
    {
        foreach (var candidate in this.TypeAndDerived(type))
        {
            if (candidate is not XmlSchemaComplexType complex)
                continue;
            if (IsAnyType(complex))
                return true;
            this.CollectParticle(complex.ContentTypeParticle, children, wildcards);
        }
        return false;
    }

    private void CollectParticle(XmlSchemaParticle? particle, List<Entry> children, List<XmlSchemaAny> wildcards)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
                Add(children, new(element.QualifiedName, element.ElementSchemaType));
                foreach (XmlSchemaElement member in this.schemas.GlobalElements.Values)
                {
                    if (!member.IsAbstract && this.SubstitutesFor(member, element.QualifiedName))
                        Add(children, new(member.QualifiedName, member.ElementSchemaType));
                }
                break;
            case XmlSchemaGroupBase group:
                foreach (var item in group.Items)
                {
                    if (item is XmlSchemaParticle child)
                        this.CollectParticle(child, children, wildcards);
                }
                break;
            case XmlSchemaGroupRef reference:
                this.CollectParticle(reference.Particle, children, wildcards);
                break;
            case XmlSchemaAny wildcard:
                wildcards.Add(wildcard);
                break;
        }
    }

    /// <summary>
    /// Whether a wildcard's namespace constraint admits <paramref name="uri"/>:
    /// <c>##any</c>, <c>##other</c> (neither the schema's target namespace nor
    /// none), or a list of URIs and <c>##targetNamespace</c> / <c>##local</c>.
    /// </summary>
    private static bool Admits(XmlSchemaObject wildcard, string uri)
    {
        var constraint = wildcard switch
        {
            XmlSchemaAny any => any.Namespace,
            XmlSchemaAnyAttribute anyAttribute => anyAttribute.Namespace,
            _ => null,
        };
        var target = TargetNamespaceOf(wildcard);
        switch (constraint?.Trim())
        {
            case null or "" or "##any":
                return true;
            case "##other":
                return uri.Length > 0 && uri != target;
        }
        foreach (var item in constraint.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var admitted = item switch
            {
                "##local" => string.Empty,
                "##targetNamespace" => target,
                _ => item,
            };
            if (admitted == uri)
                return true;
        }
        return false;
    }

    private static string TargetNamespaceOf(XmlSchemaObject item)
    {
        for (var current = item; current is not null; current = current.Parent)
        {
            if (current is XmlSchema schema)
                return schema.TargetNamespace ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Whether <paramref name="member"/>'s substitution group chain reaches <paramref name="head"/>.</summary>
    private bool SubstitutesFor(XmlSchemaElement member, XmlQualifiedName head)
    {
        var current = member;
        for (var depth = 0; depth < 64 && current is not null && !current.SubstitutionGroup.IsEmpty; depth++)
        {
            if (current.SubstitutionGroup == head)
                return true;
            current = this.schemas.GlobalElements[current.SubstitutionGroup] as XmlSchemaElement;
        }
        return false;
    }

    private static void Add(List<Entry> entries, Entry entry)
    {
        if (!entries.Exists(seen => seen.Same(entry)))
            entries.Add(entry);
    }

    /// <summary><paramref name="type"/>, then every named type the collection derives from it.</summary>
    private IEnumerable<XmlSchemaType?> TypeAndDerived(XmlSchemaType? type)
    {
        yield return type;
        if (type is not { QualifiedName.IsEmpty: false } || IsAnyType(type))
            yield break;
        foreach (XmlSchemaType candidate in this.schemas.GlobalTypes.Values)
        {
            if (ReferenceEquals(candidate, type))
                continue;
            for (var current = candidate.BaseXmlSchemaType; current is not null && !IsAnyType(current); current = current.BaseXmlSchemaType)
            {
                if (current.QualifiedName == type.QualifiedName)
                {
                    yield return candidate;
                    break;
                }
            }
        }
    }

    /// <summary>Whether a wildcard validates strictly, which an unwritten <c>processContents</c> means.</summary>
    private static bool IsStrict(XmlSchemaContentProcessing processing) =>
        processing is XmlSchemaContentProcessing.Strict or XmlSchemaContentProcessing.None;

    /// <summary>The built-in <c>xs:anyType</c>.</summary>
    private static readonly XmlSchemaComplexType AnyType = XmlSchemaType.GetBuiltInComplexType(XmlTypeCode.Item)!;

    private static bool IsAnyType(XmlSchemaType type) =>
        type.QualifiedName.Name == "anyType" && type.QualifiedName.Namespace == XmlAtomicTypes.SchemaNamespace;

    /// <summary>
    /// Real's notation for <paramref name="occurrence"/> of these elements: a
    /// union is parenthesized only where an occurrence indicator follows it,
    /// as a <c>.nodes()</c> row's single node goes without (probed 2026-10-20
    /// against SQL Server 2025).
    /// </summary>
    private string Render(XmlSchemaNotation notation, XmlOccurrence occurrence)
    {
        var suffix = XmlQueryExpr.OccurrenceSuffix(occurrence);
        if (this.elements.Length == 1)
            return ElementNotation(this.elements[0], notation) + suffix;
        if (this.documentChildren)
            return $"({string.Join(" ? & ", this.elements.Select(element => ElementNotation(element, notation)))} ?){suffix}";
        var union = string.Join(" | ", this.elements.Select(element => ElementNotation(element, notation)));
        return suffix.Length == 0 ? union : $"({union}){suffix}";
    }

    private static string ElementNotation(Entry element, XmlSchemaNotation notation) =>
        element.NodeKind ?? $"element({notation.Name(element.Name)},{TypeNotation(element.Type, notation)})";

    private static string TypeNotation(XmlSchemaType? type, XmlSchemaNotation notation) =>
        type is null || type.QualifiedName.IsEmpty ? "#anonymous"
        : type.QualifiedName.Namespace == XmlAtomicTypes.SchemaNamespace ? "xs:" + type.QualifiedName.Name
        : notation.Name(type.QualifiedName);

    /// <summary>
    /// The type an atomization refusal names: the declared type's name, or for
    /// an anonymous one the element's local name in the declaring schema's
    /// target namespace — an unqualified local element's included.
    /// </summary>
    private static string FoundTypeOf(Entry element, XmlSchemaNotation notation) =>
        element.Type is { QualifiedName.IsEmpty: false } named ? TypeNotation(named, notation)
        : notation.Name(new XmlQualifiedName(element.Name.Name, element.Type is { } anonymous ? TargetNamespaceOf(anonymous) : element.Name.Namespace));
}

/// <summary>
/// How real writes a schema name in a static type: <c>prefix{uri}:local</c>
/// under the prefix the query binds the namespace to, <c>{uri}:local</c>
/// under none (the default element namespace), and the bare local name in no
/// namespace.
/// </summary>
internal readonly struct XmlSchemaNotation(Dictionary<string, string> prefixes)
{
    private readonly Dictionary<string, string> prefixes = prefixes;

    public string Name(XmlQualifiedName name)
    {
        if (name.Namespace.Length == 0)
            return name.Name;
        foreach (var (prefix, uri) in this.prefixes)
        {
            if (uri == name.Namespace)
                return $"{prefix}{{{uri}}}:{name.Name}";
        }
        return $"{{{name.Namespace}}}:{name.Name}";
    }
}
