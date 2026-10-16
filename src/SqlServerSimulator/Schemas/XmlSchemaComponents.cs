using System.Xml;
using System.Xml.Schema;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One row of <c>sys.xml_schema_components</c> and of whichever of its
/// per-kind views (<c>sys.xml_schema_types</c>, <c>_elements</c>,
/// <c>_attributes</c>, <c>_model_groups</c>, <c>_wildcards</c>) carries its
/// kind, with the facets, wildcard namespaces and placements hung off it.
/// </summary>
/// <remarks>
/// The one-letter codes are real's own: <see cref="SymbolSpace"/> is
/// N(one) / T(ype) / E(lement) / M(odel group) / A(ttribute) / G (attribute
/// group); <see cref="Kind"/> is N (any type) / Z (any simple type) /
/// P(rimitive) / S(imple) / L(ist) / U(nion) / C (complex type with simple
/// content) / K (complex) / E / M / W (element wildcard) / A / G / V
/// (attribute wildcard); <see cref="Derivation"/> is N(one) / X (extension) /
/// R(estriction) / S(ubstitution).
/// </remarks>
internal sealed class XmlSchemaComponent(int id, int namespaceId, bool isQualified, string? name, char symbolSpace, char kind, int? scopingId)
{
    public readonly int Id = id;
    public readonly int NamespaceId = namespaceId;
    public readonly bool IsQualified = isQualified;
    public readonly string? Name = name;
    public readonly char SymbolSpace = symbolSpace;

    /// <summary>Settled after the base for a simple type's restriction, which takes its base's variety.</summary>
    public char Kind = kind;
    public char Derivation = 'N';
    public int? BaseId;
    public readonly int? ScopingId = scopingId;

    // Elements and attributes.
    public bool IsDefaultFixed;
    public bool MustBeQualified;
    public string? DefaultValue;

    // Elements and types.
    public bool IsAbstract;
    public bool IsNillable;
    public bool AllowsMixedContent;
    public bool IsExtensionBlocked;
    public bool IsRestrictionBlocked;
    public bool IsSubstitutionBlocked;
    public bool IsFinalExtension;
    public bool IsFinalRestriction;
    public bool IsFinalListMember;
    public bool IsFinalUnionMember;

    /// <summary>A model group's S(equence) / C(hoice) / A(ll).</summary>
    public char Compositor;

    /// <summary>A wildcard's S(trict) / L(ax) / P (skip).</summary>
    public char ProcessContent;
    public bool DisallowNamespaces;
    public string[] WildcardNamespaces = [];

    /// <summary>
    /// A simple type's whitespace handling, P(reserve) / R(eplace) /
    /// C(ollapse), which its restrictions' value facets are normalized by.
    /// </summary>
    public char WhiteSpace = 'C';

    /// <summary>An element's type, which a substitution member without one of its own takes.</summary>
    public int? TypeId;

    public readonly List<XmlSchemaFacetRow> Facets = [];
    public readonly List<XmlSchemaPlacement> Placements = [];
}

/// <summary>One row of <c>sys.xml_schema_facets</c>; <see cref="Kind"/> is real's two-letter code.</summary>
internal readonly struct XmlSchemaFacetRow(string kind, bool isFixed, string value)
{
    public readonly string Kind = kind;
    public readonly bool IsFixed = isFixed;
    public readonly string Value = value;
}

/// <summary>One row of <c>sys.xml_schema_component_placements</c>, numbered by its position.</summary>
internal readonly struct XmlSchemaPlacement(int placedId, bool isDefaultFixed, int minOccurs, int maxOccurs, string? defaultValue)
{
    public readonly int PlacedId = placedId;
    public readonly bool IsDefaultFixed = isDefaultFixed;
    public readonly int MinOccurs = minOccurs;
    public readonly int MaxOccurs = maxOccurs;
    public readonly string? DefaultValue = defaultValue;
}

/// <summary>
/// The schema documents one <c>CREATE</c> or <c>ALTER … ADD</c> gave a
/// collection, and the first <c>xml_component_id</c> the database handed
/// their components.
/// </summary>
internal readonly struct XmlSchemaSegment(int documents, int firstComponentId)
{
    public readonly int Documents = documents;
    public readonly int FirstComponentId = firstComponentId;
}

/// <summary>
/// A collection's namespaces (<c>xml_namespace_id</c> is the position plus
/// one) and its components in <c>xml_component_id</c> order.
/// </summary>
internal sealed class XmlSchemaComponentSet(List<string> namespaces, List<XmlSchemaComponent> components)
{
    public readonly List<string> Namespaces = namespaces;
    public readonly List<XmlSchemaComponent> Components = components;

    /// <summary>The empty set a text the object model can't read answers.</summary>
    public static readonly XmlSchemaComponentSet Empty = new([], []);
}

/// <summary>
/// Numbers the components of a collection's schema documents as real does
/// (probed 2026-10-16 against SQL Server 2025): the documents' global
/// declarations in document order, each component taking the next id as it
/// is reached, its own parts after it depth first — an element's
/// substitution head, then its type; a complex type's base, then its content
/// model, then its attribute uses in order and its attribute wildcard — and a
/// reference to a global not yet numbered numbering it, whole, right there.
/// </summary>
/// <remarks>
/// Each <see cref="XmlSchemaSegment"/> restarts the count at the ids its
/// statement drew, since a later <c>ALTER … ADD</c> draws from the
/// database's counter after whatever else was created in between. A
/// reference only ever reaches back to a segment already numbered, so one
/// walk over every document gives each segment the numbering its own
/// statement did. A reference group, attribute group or element ref is no
/// component of its own: it places the global it names.
/// </remarks>
internal sealed class XmlSchemaComponentBuilder
{
    private const int Unbounded = int.MaxValue;

    private readonly List<string> namespaces = [];
    private readonly List<XmlSchemaComponent> components = [];
    private readonly Dictionary<(char Space, string Namespace, string Name), (XmlSchemaObject Declaration, XmlSchema Document, int Segment)> globals = [];
    private readonly Dictionary<XmlSchemaObject, XmlSchemaComponent> numbered = new(ReferenceEqualityComparer.Instance);
    private int nextId;

    /// <summary>
    /// Whether the walk stopped at a global declaration an earlier segment
    /// already holds — the <c>ALTER … ADD</c> real refuses with Msg 6310,
    /// having numbered the components ahead of it.
    /// </summary>
    public bool StoppedAtRedeclaration;

    private XmlSchemaComponentBuilder()
    {
    }

    /// <summary>
    /// The components of <paramref name="xsdText"/>, its documents split into
    /// <paramref name="segments"/>; a text the object model can't read
    /// answers <see cref="XmlSchemaComponentSet.Empty"/>.
    /// </summary>
    public static XmlSchemaComponentSet Build(string xsdText, XmlSchemaSegment[] segments) =>
        Build(xsdText, segments, out _);

    /// <summary>
    /// <see cref="Build(string, XmlSchemaSegment[])"/>, also saying whether the
    /// last segment redeclares a global an earlier one holds.
    /// </summary>
    public static XmlSchemaComponentSet Build(string xsdText, XmlSchemaSegment[] segments, out bool stoppedAtRedeclaration)
    {
        stoppedAtRedeclaration = false;
        if (ReadDocuments(xsdText) is not { } documents)
            return XmlSchemaComponentSet.Empty;
        var builder = new XmlSchemaComponentBuilder();
        try
        {
            builder.Walk(documents, segments);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return XmlSchemaComponentSet.Empty;
        }
        stoppedAtRedeclaration = builder.StoppedAtRedeclaration;
        return new XmlSchemaComponentSet(builder.namespaces, builder.components);
    }

    /// <summary>The number of schema documents <paramref name="xsdText"/> holds.</summary>
    public static int CountDocuments(string xsdText) => ReadDocuments(xsdText)?.Count ?? 0;

    private static List<XmlSchema>? ReadDocuments(string xsdText)
    {
        var documents = new List<XmlSchema>();
        try
        {
            var settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment };
            using var reader = XmlReader.Create(new StringReader(xsdText), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (XmlSchema.Read(reader.ReadSubtree(), null) is { } schema)
                    documents.Add(schema);
            }
        }
        catch (Exception e) when (e is XmlException or XmlSchemaException)
        {
            return null;
        }
        return documents;
    }

    private void Walk(List<XmlSchema> documents, XmlSchemaSegment[] segments)
    {
        // Which segment each document belongs to; documents past the
        // segments' count (a text the segments undercount) join the last.
        var segmentOf = new int[documents.Count];
        for (int d = 0, s = 0, left = segments.Length == 0 ? int.MaxValue : segments[0].Documents; d < documents.Count; d++)
        {
            while (left == 0 && s + 1 < segments.Length)
                left = segments[++s].Documents;
            segmentOf[d] = s;
            left--;
        }

        for (var d = 0; d < documents.Count; d++)
        {
            var document = documents[d];
            var targetNamespace = document.TargetNamespace ?? string.Empty;
            if (!this.namespaces.Contains(targetNamespace))
                this.namespaces.Add(targetNamespace);
            foreach (var item in document.Items)
            {
                if (GlobalKey(item, targetNamespace) is { } key)
                    _ = this.globals.TryAdd(key, (item, document, segmentOf[d]));
            }
        }

        var segment = -1;
        for (var d = 0; d < documents.Count; d++)
        {
            if (segmentOf[d] != segment)
            {
                segment = segmentOf[d];
                this.nextId = segments.Length == 0 ? 0 : segments[segment].FirstComponentId;
            }
            var document = documents[d];
            var targetNamespace = document.TargetNamespace ?? string.Empty;
            foreach (var item in document.Items)
            {
                if (GlobalKey(item, targetNamespace) is not { } key)
                    continue;
                if (!ReferenceEquals(this.globals[key].Declaration, item))
                {
                    this.StoppedAtRedeclaration = true;
                    return;
                }
                _ = this.VisitGlobal(item, document);
            }
        }
    }

    private static (char, string, string)? GlobalKey(XmlSchemaObject item, string targetNamespace) => item switch
    {
        XmlSchemaElement { Name: { } name } => ('E', targetNamespace, name),
        XmlSchemaAttribute { Name: { } name } => ('A', targetNamespace, name),
        XmlSchemaType { Name: { } name } => ('T', targetNamespace, name),
        XmlSchemaGroup { Name: { } name } => ('M', targetNamespace, name),
        XmlSchemaAttributeGroup { Name: { } name } => ('G', targetNamespace, name),
        _ => null,
    };

    private int NamespaceIdOf(XmlSchema document) => this.namespaces.IndexOf(document.TargetNamespace ?? string.Empty) + 1;

    private XmlSchemaComponent Add(XmlSchema document, bool isQualified, string? name, char symbolSpace, char kind, int? scopingId, XmlSchemaObject declaration)
    {
        var component = new XmlSchemaComponent(this.nextId++, this.NamespaceIdOf(document), isQualified, name, symbolSpace, kind, scopingId);
        this.components.Add(component);
        this.numbered[declaration] = component;
        return component;
    }

    private XmlSchemaComponent? VisitGlobal(XmlSchemaObject item, XmlSchema document)
    {
        if (this.numbered.TryGetValue(item, out var existing))
            return existing;
        return item switch
        {
            XmlSchemaElement element => this.VisitElement(element, document, null),
            XmlSchemaAttribute attribute => this.VisitAttribute(attribute, document, null),
            XmlSchemaSimpleType simple => this.VisitSimpleType(simple, document, null),
            XmlSchemaComplexType complex => this.VisitComplexType(complex, document, null),
            XmlSchemaGroup group => this.VisitNamedGroup(group, document),
            XmlSchemaAttributeGroup group => this.VisitAttributeGroup(group, document),
            _ => null,
        };
    }

    /// <summary>
    /// The component a reference names: a built-in from the <c>sys</c>
    /// collection, or a global of this collection, numbered here when the walk
    /// hasn't reached it yet.
    /// </summary>
    private XmlSchemaComponent? Resolve(char space, XmlQualifiedName name)
    {
        if (name.IsEmpty)
            return null;
        if (XmlSchemaSystemCollection.Find(space, name.Namespace, name.Name) is { } builtIn)
            return builtIn;
        return this.globals.TryGetValue((space, name.Namespace, name.Name), out var global)
            ? this.VisitGlobal(global.Declaration, global.Document)
            : null;
    }

    private static bool Has(XmlSchemaDerivationMethod method, XmlSchemaDerivationMethod flag) =>
        method != XmlSchemaDerivationMethod.None && (method & flag) != 0;

    private static XmlSchemaDerivationMethod OrDefault(XmlSchemaDerivationMethod method, XmlSchemaDerivationMethod schemaDefault) =>
        method == XmlSchemaDerivationMethod.None ? schemaDefault : method;

    private static int Occurs(decimal value) => value >= Unbounded ? Unbounded : (int)value;

    private XmlSchemaComponent VisitElement(XmlSchemaElement element, XmlSchema document, int? scopingId)
    {
        var isGlobal = scopingId is null;
        var qualified = isGlobal || (element.Form == XmlSchemaForm.None ? document.ElementFormDefault == XmlSchemaForm.Qualified : element.Form == XmlSchemaForm.Qualified);
        var component = this.Add(document, qualified, element.Name, 'E', 'E', scopingId, element);
        component.MustBeQualified = qualified;
        component.IsDefaultFixed = element.FixedValue is not null;
        component.DefaultValue = element.DefaultValue ?? element.FixedValue;
        component.IsAbstract = element.IsAbstract;
        component.IsNillable = element.IsNillable;
        var block = OrDefault(element.Block, document.BlockDefault);
        component.IsExtensionBlocked = Has(block, XmlSchemaDerivationMethod.Extension);
        component.IsRestrictionBlocked = Has(block, XmlSchemaDerivationMethod.Restriction);
        component.IsSubstitutionBlocked = Has(block, XmlSchemaDerivationMethod.Substitution);
        if (isGlobal)
        {
            var final = OrDefault(element.Final, document.FinalDefault);
            component.IsFinalExtension = Has(final, XmlSchemaDerivationMethod.Extension);
            component.IsFinalRestriction = Has(final, XmlSchemaDerivationMethod.Restriction);
        }

        XmlSchemaComponent? head = null;
        if (!element.SubstitutionGroup.IsEmpty && this.Resolve('E', element.SubstitutionGroup) is { } resolvedHead)
        {
            head = resolvedHead;
            component.Derivation = 'S';
            component.BaseId = head.Id;
        }

        var typeId = element.SchemaType is { } inline
            ? this.VisitAnonymousType(inline, document, component.Id).Id
            : !element.SchemaTypeName.IsEmpty
                ? this.Resolve('T', element.SchemaTypeName)?.Id
                : head is not null ? head.TypeId : XmlSchemaSystemCollection.AnyTypeId;
        component.TypeId = typeId;
        if (typeId is int placed)
            component.Placements.Add(new(placed, false, 1, 1, null));
        return component;
    }

    private XmlSchemaComponent VisitAnonymousType(XmlSchemaType type, XmlSchema document, int scopingId) => type switch
    {
        XmlSchemaComplexType complex => this.VisitComplexType(complex, document, scopingId),
        _ => this.VisitSimpleType((XmlSchemaSimpleType)type, document, scopingId),
    };

    private XmlSchemaComponent VisitAttribute(XmlSchemaAttribute attribute, XmlSchema document, int? scopingId)
    {
        var isGlobal = scopingId is null;
        var mustBeQualified = isGlobal || (attribute.Form == XmlSchemaForm.None ? document.AttributeFormDefault == XmlSchemaForm.Qualified : attribute.Form == XmlSchemaForm.Qualified);
        // A local attribute counts as qualified only where its schema's
        // attributeFormDefault qualifies it too, unlike an element.
        var component = this.Add(document, isGlobal || (mustBeQualified && document.AttributeFormDefault == XmlSchemaForm.Qualified), attribute.Name, 'A', 'A', scopingId, attribute);
        component.MustBeQualified = mustBeQualified;
        component.IsDefaultFixed = attribute.FixedValue is not null;
        component.DefaultValue = attribute.DefaultValue ?? attribute.FixedValue;
        var typeId = attribute.SchemaType is { } inline
            ? this.VisitSimpleType(inline, document, component.Id).Id
            : !attribute.SchemaTypeName.IsEmpty
                ? this.Resolve('T', attribute.SchemaTypeName)?.Id
                : XmlSchemaSystemCollection.AnySimpleTypeId;
        if (typeId is int placed)
            component.Placements.Add(new(placed, false, 1, 1, null));
        return component;
    }

    /// <summary>
    /// The placements a complex type's or attribute group's attribute list
    /// gives its owner: each attribute use (a local declaration's fixed flag,
    /// or a reference's own value constraint), each attribute group it
    /// references, then the attribute wildcard.
    /// </summary>
    private void VisitAttributeUses(XmlSchemaComponent owner, XmlSchemaObjectCollection attributes, XmlSchemaAnyAttribute? anyAttribute, XmlSchema document)
    {
        foreach (var item in attributes)
        {
            switch (item)
            {
                case XmlSchemaAttribute { RefName.IsEmpty: false } reference:
                    if (this.Resolve('A', reference.RefName) is { } referenced)
                    {
                        owner.Placements.Add(new(referenced.Id, reference.FixedValue is not null, UseMinimum(reference.Use), UseMaximum(reference.Use),
                            reference.DefaultValue ?? reference.FixedValue));
                    }
                    break;
                case XmlSchemaAttribute local:
                    var declared = this.VisitAttribute(local, document, owner.Id);
                    owner.Placements.Add(new(declared.Id, local.FixedValue is not null, UseMinimum(local.Use), UseMaximum(local.Use), null));
                    break;
                case XmlSchemaAttributeGroupRef groupReference:
                    if (this.Resolve('G', groupReference.RefName) is { } group)
                        owner.Placements.Add(new(group.Id, false, 1, 1, null));
                    break;
            }
        }
        if (anyAttribute is not null)
        {
            var wildcard = this.VisitWildcard(anyAttribute, anyAttribute.Namespace, anyAttribute.ProcessContents, 'V', document, owner.Id);
            owner.Placements.Add(new(wildcard.Id, false, 1, 1, null));
        }
    }

    private static int UseMinimum(XmlSchemaUse use) => use == XmlSchemaUse.Required ? 1 : 0;

    private static int UseMaximum(XmlSchemaUse use) => use == XmlSchemaUse.Prohibited ? 0 : 1;

    private XmlSchemaComponent VisitAttributeGroup(XmlSchemaAttributeGroup group, XmlSchema document)
    {
        var component = this.Add(document, true, group.Name, 'G', 'G', null, group);
        this.VisitAttributeUses(component, group.Attributes, group.AnyAttribute, document);
        return component;
    }

    private XmlSchemaComponent VisitWildcard(XmlSchemaObject declaration, string? namespaceList, XmlSchemaContentProcessing processing, char kind, XmlSchema document, int scopingId)
    {
        var component = this.Add(document, false, null, 'N', kind, scopingId, declaration);
        component.ProcessContent = processing switch
        {
            XmlSchemaContentProcessing.Lax => 'L',
            XmlSchemaContentProcessing.Skip => 'P',
            _ => 'S',
        };
        var targetNamespace = document.TargetNamespace ?? string.Empty;
        var tokens = (namespaceList ?? "##any").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens is ["##any"])
        {
            component.DisallowNamespaces = true;
        }
        else if (tokens is ["##other"])
        {
            component.DisallowNamespaces = true;
            component.WildcardNamespaces = [targetNamespace];
        }
        else
        {
            // Real lists the named namespaces in ordinal order, the absent
            // namespace (##local, or ##targetNamespace without one) last.
            var named = new SortedSet<string>(StringComparer.Ordinal);
            var absent = false;
            foreach (var token in tokens)
            {
                var resolved = token switch
                {
                    "##local" => string.Empty,
                    "##targetNamespace" => targetNamespace,
                    _ => token,
                };
                if (resolved.Length == 0)
                    absent = true;
                else
                    _ = named.Add(resolved);
            }
            component.WildcardNamespaces = absent ? [.. named, string.Empty] : [.. named];
        }
        return component;
    }

    private XmlSchemaComponent VisitNamedGroup(XmlSchemaGroup group, XmlSchema document)
    {
        var component = this.Add(document, true, group.Name, 'M', 'M', null, group);
        if (group.Particle is { } particle)
        {
            component.Compositor = CompositorOf(particle);
            this.VisitGroupItems(component, particle, document);
        }
        return component;
    }

    private static char CompositorOf(XmlSchemaGroupBase particle) => particle switch
    {
        XmlSchemaChoice => 'C',
        XmlSchemaAll => 'A',
        _ => 'S',
    };

    /// <summary>
    /// A content model particle placed in <paramref name="owner"/> with the
    /// particle's own occurrence: a compositor as an anonymous model group, a
    /// local element, a wildcard, or the global an element or group
    /// reference names.
    /// </summary>
    private void VisitParticle(XmlSchemaComponent owner, XmlSchemaParticle particle, XmlSchema document)
    {
        var minimum = Occurs(particle.MinOccurs);
        var maximum = Occurs(particle.MaxOccurs);
        var placed = particle switch
        {
            XmlSchemaElement { RefName.IsEmpty: false } reference => this.Resolve('E', reference.RefName),
            XmlSchemaElement local => this.VisitElement(local, document, owner.Id),
            XmlSchemaGroupRef reference => this.Resolve('M', reference.RefName),
            XmlSchemaAny any => this.VisitWildcard(any, any.Namespace, any.ProcessContents, 'W', document, owner.Id),
            XmlSchemaGroupBase compositor => this.VisitModelGroup(compositor, document, owner.Id),
            _ => null,
        };
        if (placed is not null)
            owner.Placements.Add(new(placed.Id, false, minimum, maximum, null));
    }

    private XmlSchemaComponent VisitModelGroup(XmlSchemaGroupBase compositor, XmlSchema document, int scopingId)
    {
        var component = this.Add(document, false, null, 'M', 'M', scopingId, compositor);
        component.Compositor = CompositorOf(compositor);
        this.VisitGroupItems(component, compositor, document);
        return component;
    }

    private void VisitGroupItems(XmlSchemaComponent owner, XmlSchemaGroupBase compositor, XmlSchema document)
    {
        foreach (var item in compositor.Items)
        {
            if (item is XmlSchemaParticle particle)
                this.VisitParticle(owner, particle, document);
        }
    }

    private XmlSchemaComponent VisitComplexType(XmlSchemaComplexType type, XmlSchema document, int? scopingId)
    {
        var simpleContent = type.ContentModel is XmlSchemaSimpleContent;
        var component = this.Add(document, scopingId is null, type.Name, 'T', simpleContent ? 'C' : 'K', scopingId, type);
        component.IsAbstract = type.IsAbstract;
        component.AllowsMixedContent = type.IsMixed || type.ContentModel is XmlSchemaComplexContent { IsMixed: true };
        if (scopingId is null)
        {
            var block = OrDefault(type.Block, document.BlockDefault);
            component.IsExtensionBlocked = Has(block, XmlSchemaDerivationMethod.Extension);
            component.IsRestrictionBlocked = Has(block, XmlSchemaDerivationMethod.Restriction);
            var final = OrDefault(type.Final, document.FinalDefault);
            component.IsFinalExtension = Has(final, XmlSchemaDerivationMethod.Extension);
            component.IsFinalRestriction = Has(final, XmlSchemaDerivationMethod.Restriction);
        }

        switch (type.ContentModel?.Content)
        {
            case XmlSchemaComplexContentExtension extension:
                component.Derivation = 'X';
                component.BaseId = this.Resolve('T', extension.BaseTypeName)?.Id;
                if (extension.Particle is { } extended)
                    this.VisitParticle(component, extended, document);
                this.VisitAttributeUses(component, extension.Attributes, extension.AnyAttribute, document);
                break;
            case XmlSchemaComplexContentRestriction restriction:
                component.Derivation = 'R';
                component.BaseId = this.Resolve('T', restriction.BaseTypeName)?.Id;
                if (restriction.Particle is { } restricted)
                    this.VisitParticle(component, restricted, document);
                this.VisitAttributeUses(component, restriction.Attributes, restriction.AnyAttribute, document);
                break;
            case XmlSchemaSimpleContentExtension extension:
                component.Derivation = 'X';
                component.BaseId = this.Resolve('T', extension.BaseTypeName)?.Id;
                this.VisitAttributeUses(component, extension.Attributes, extension.AnyAttribute, document);
                break;
            case XmlSchemaSimpleContentRestriction restriction:
                component.Derivation = 'R';
                var simpleBase = restriction.BaseType is { } inlineBase
                    ? this.VisitSimpleType(inlineBase, document, component.Id)
                    : this.Resolve('T', restriction.BaseTypeName);
                component.BaseId = simpleBase?.Id;
                AddFacets(component, restriction.Facets);
                this.VisitAttributeUses(component, restriction.Attributes, restriction.AnyAttribute, document);
                break;
            default:
                component.Derivation = 'R';
                component.BaseId = XmlSchemaSystemCollection.AnyTypeId;
                if (type.Particle is { } particle)
                    this.VisitParticle(component, particle, document);
                this.VisitAttributeUses(component, type.Attributes, type.AnyAttribute, document);
                break;
        }
        return component;
    }

    private XmlSchemaComponent VisitSimpleType(XmlSchemaSimpleType type, XmlSchema document, int? scopingId)
    {
        var component = this.Add(document, scopingId is null, type.Name, 'T', 'S', scopingId, type);
        if (scopingId is null)
        {
            var final = OrDefault(type.Final, document.FinalDefault);
            component.IsFinalRestriction = Has(final, XmlSchemaDerivationMethod.Restriction);
            component.IsFinalListMember = Has(final, XmlSchemaDerivationMethod.List);
            component.IsFinalUnionMember = Has(final, XmlSchemaDerivationMethod.Union);
        }

        switch (type.Content)
        {
            case XmlSchemaSimpleTypeRestriction restriction:
                component.Derivation = 'R';
                var baseType = restriction.BaseType is { } inlineBase
                    ? this.VisitSimpleType(inlineBase, document, component.Id)
                    : this.Resolve('T', restriction.BaseTypeName);
                component.BaseId = baseType?.Id;
                component.Kind = baseType?.Kind is 'L' or 'U' ? baseType.Kind : 'S';
                component.WhiteSpace = baseType?.WhiteSpace ?? 'C';
                AddFacets(component, restriction.Facets);
                break;
            case XmlSchemaSimpleTypeList list:
                component.Kind = 'L';
                var item = list.ItemType is { } inlineItem
                    ? this.VisitSimpleType(inlineItem, document, component.Id)
                    : this.Resolve('T', list.ItemTypeName);
                if (item is not null)
                    component.Placements.Add(new(item.Id, false, 1, 1, null));
                break;
            case XmlSchemaSimpleTypeUnion union:
                component.Kind = 'U';
                foreach (var member in union.MemberTypes ?? [])
                {
                    if (this.Resolve('T', member) is { } resolved)
                        component.Placements.Add(new(resolved.Id, false, 1, 1, null));
                }
                foreach (var inlineMember in union.BaseTypes)
                {
                    if (inlineMember is XmlSchemaSimpleType memberType)
                        component.Placements.Add(new(this.VisitSimpleType(memberType, document, component.Id).Id, false, 1, 1, null));
                }
                break;
        }
        return component;
    }

    /// <summary>
    /// A restriction's facets in document order. A value facet reads as its
    /// type's whitespace handling leaves it — <c>' a  b '</c> enumerated over
    /// <c>xs:token</c> is <c>'a b'</c> — and a length or digits count is
    /// collapsed, while a pattern stays as written (probed 2026-10-16 against
    /// SQL Server 2025).
    /// </summary>
    private static void AddFacets(XmlSchemaComponent component, XmlSchemaObjectCollection facets)
    {
        foreach (var item in facets)
        {
            if (item is XmlSchemaWhiteSpaceFacet { Value: { } whiteSpace })
            {
                component.WhiteSpace = whiteSpace switch
                {
                    "preserve" => 'P',
                    "replace" => 'R',
                    _ => 'C',
                };
            }
        }
        foreach (var item in facets)
        {
            if (item is not XmlSchemaFacet facet)
                continue;
            var (code, normalization) = facet switch
            {
                XmlSchemaLengthFacet => ("LG", 'C'),
                XmlSchemaMinLengthFacet => ("LN", 'C'),
                XmlSchemaMaxLengthFacet => ("LX", 'C'),
                XmlSchemaPatternFacet => ("PT", 'P'),
                XmlSchemaEnumerationFacet => ("EU", component.WhiteSpace),
                XmlSchemaWhiteSpaceFacet => ("WS", 'P'),
                XmlSchemaMaxInclusiveFacet => ("IX", component.WhiteSpace),
                XmlSchemaMaxExclusiveFacet => ("EX", component.WhiteSpace),
                XmlSchemaMinInclusiveFacet => ("IN", component.WhiteSpace),
                XmlSchemaMinExclusiveFacet => ("EN", component.WhiteSpace),
                XmlSchemaTotalDigitsFacet => ("DT", 'C'),
                XmlSchemaFractionDigitsFacet => ("DF", 'C'),
                _ => (null, 'P'),
            };
            if (code is not null)
                component.Facets.Add(new(code, facet.IsFixed, Normalize(facet.Value ?? string.Empty, normalization)));
        }
    }

    private static string Normalize(string value, char whiteSpace)
    {
        if (whiteSpace == 'P')
            return value;
        var replaced = value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return whiteSpace == 'R'
            ? replaced
            : string.Join(' ', replaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
