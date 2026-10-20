using System.Text;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The XSD <c>XML_SCHEMA_NAMESPACE</c> answers, which real regenerates from a
/// collection's components rather than returning the text it was given
/// (probed 2026-10-20 against SQL Server 2025): one <c>xsd:schema</c> per
/// namespace in namespace order, its globals sorted by kind code then by
/// name, each written with real's attribute order and none of a default's
/// value — annotations dropped, an underived complex type a restriction of
/// <c>xsd:anyType</c>, <c>blockDefault</c> and <c>finalDefault</c> spelled out
/// on each component, and the form defaults whichever form most of the
/// namespace's local declarations take.
/// </summary>
internal sealed class XmlSchemaNamespaceWriter
{
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string SqlTypesNamespace = "http://schemas.microsoft.com/sqlserver/2004/sqltypes";
    private const int Unbounded = int.MaxValue;

    private readonly XmlSchemaComponentSet set;
    private readonly Dictionary<int, XmlSchemaComponent> byId = [];

    /// <summary>The namespace being written, which its <c>t</c> prefix names.</summary>
    private string target = string.Empty;

    /// <summary>The prefix each other namespace a schema references is written with, in the order they were met.</summary>
    private readonly Dictionary<string, string> foreign = new(StringComparer.Ordinal);

    private bool elementFormQualified;
    private bool attributeFormQualified;

    private XmlSchemaNamespaceWriter(XmlSchemaComponentSet set)
    {
        this.set = set;
        foreach (var component in XmlSchemaSystemCollection.Components.Components)
            this.byId[component.Id] = component;
        foreach (var component in set.Components)
            this.byId[component.Id] = component;
    }

    /// <summary>
    /// The regenerated XSD of <paramref name="set"/>'s namespaces, or of the
    /// one named <paramref name="onlyNamespace"/>; null when that namespace
    /// holds no components.
    /// </summary>
    public static string? Write(XmlSchemaComponentSet set, string? onlyNamespace)
    {
        var writer = new XmlSchemaNamespaceWriter(set);
        var text = new StringBuilder();
        for (var i = 0; i < set.Namespaces.Count; i++)
        {
            var namespaceName = set.Namespaces[i];
            if (onlyNamespace is not null && !string.Equals(namespaceName, onlyNamespace, StringComparison.Ordinal))
                continue;
            _ = writer.WriteSchema(text, i + 1, namespaceName);
        }
        return onlyNamespace is not null && text.Length == 0 ? null : text.ToString();
    }

    private bool WriteSchema(StringBuilder text, int namespaceId, string namespaceName)
    {
        var globals = this.set.Components
            .Where(component => component.NamespaceId == namespaceId && component.ScopingId is null && component.Name is not null)
            .OrderBy(static component => component.Kind)
            .ThenBy(static component => component.Name, StringComparer.Ordinal)
            .ToList();
        if (globals.Count == 0)
            return false;

        this.target = namespaceName;
        this.foreign.Clear();
        this.elementFormQualified = MostlyQualified(namespaceId, 'E');
        this.attributeFormQualified = MostlyQualified(namespaceId, 'A');

        // The body is written first, so the namespaces it references are
        // known for the root's declarations and imports.
        var body = new StringBuilder();
        foreach (var global in globals)
            this.WriteGlobal(body, global);

        _ = text.Append("<xsd:schema xmlns:xsd=\"").Append(XsdNamespace).Append('"');
        // The XML namespace's prefix is predeclared; every other one is bound,
        // the absent namespace too, as real binds it (xmlns:ns1="").
        foreach (var (uri, prefix) in this.foreign)
        {
            if (uri != XmlNamespace)
                AppendAttribute(text, "xmlns:" + prefix, uri);
        }
        if (namespaceName.Length > 0)
        {
            AppendAttribute(text, "xmlns:t", namespaceName);
            AppendAttribute(text, "targetNamespace", namespaceName);
        }
        if (this.elementFormQualified)
            _ = text.Append(" elementFormDefault=\"qualified\"");
        if (this.attributeFormQualified)
            _ = text.Append(" attributeFormDefault=\"qualified\"");
        _ = text.Append('>');
        foreach (var (uri, _) in this.foreign)
        {
            _ = text.Append("<xsd:import");
            if (uri.Length > 0)
                AppendAttribute(text, "namespace", uri);
            _ = text.Append("/>");
        }
        _ = text.Append(body).Append("</xsd:schema>");
        return true;

        bool MostlyQualified(int id, char kind)
        {
            var qualified = 0;
            var unqualified = 0;
            foreach (var component in this.set.Components)
            {
                if (component.NamespaceId != id || component.ScopingId is null || component.Kind != kind || component.Name is null)
                    continue;
                if (component.MustBeQualified)
                    qualified++;
                else
                    unqualified++;
            }
            return qualified > unqualified;
        }
    }

    private void WriteGlobal(StringBuilder text, XmlSchemaComponent component)
    {
        switch (component.Kind)
        {
            case 'E':
                this.WriteElement(text, component, 1, 1);
                break;
            case 'A':
                this.WriteAttribute(text, component, null);
                break;
            case 'C' or 'K':
                this.WriteComplexType(text, component);
                break;
            case 'S' or 'L' or 'U':
                this.WriteSimpleType(text, component);
                break;
            case 'M':
                _ = text.Append("<xsd:group");
                AppendAttribute(text, "name", component.Name!);
                _ = text.Append('>');
                this.WriteCompositor(text, component, 1, 1);
                _ = text.Append("</xsd:group>");
                break;
            case 'G':
                _ = text.Append("<xsd:attributeGroup");
                AppendAttribute(text, "name", component.Name!);
                if (component.Placements.Count == 0)
                {
                    _ = text.Append("/>");
                    break;
                }
                _ = text.Append('>');
                this.WriteAttributeUses(text, component, 0);
                _ = text.Append("</xsd:attributeGroup>");
                break;
        }
    }

    private void WriteElement(StringBuilder text, XmlSchemaComponent element, int minimum, int maximum)
    {
        var isLocal = element.ScopingId is not null;
        var type = element.TypeId is int typeId && this.byId.TryGetValue(typeId, out var found) ? found : null;
        var anonymous = type is not null && type.ScopingId == element.Id;
        _ = text.Append("<xsd:element");
        AppendAttribute(text, "name", element.Name!);
        if (type is not null && !anonymous)
            AppendAttribute(text, "type", this.QName(type));
        if (element.Derivation == 'S' && element.BaseId is int headId && this.byId.TryGetValue(headId, out var head))
            AppendAttribute(text, "substitutionGroup", this.QName(head));
        if (isLocal && element.MustBeQualified != this.elementFormQualified)
            AppendAttribute(text, "form", element.MustBeQualified ? "qualified" : "unqualified");
        AppendOccurrence(text, minimum, maximum);
        if (element.IsAbstract)
            _ = text.Append(" abstract=\"true\"");
        if (Flags(("extension", element.IsExtensionBlocked), ("restriction", element.IsRestrictionBlocked), ("substitution", element.IsSubstitutionBlocked)) is { } block)
            AppendAttribute(text, "block", block);
        if (!isLocal && Flags(("extension", element.IsFinalExtension), ("restriction", element.IsFinalRestriction)) is { } final)
            AppendAttribute(text, "final", final);
        if (element.IsNillable)
            _ = text.Append(" nillable=\"true\"");
        if (element.DefaultValue is { } value)
            AppendAttribute(text, element.IsDefaultFixed ? "fixed" : "default", value);
        if (!anonymous)
        {
            _ = text.Append("/>");
            return;
        }
        _ = text.Append('>');
        this.WriteAnonymousType(text, type!);
        _ = text.Append("</xsd:element>");
    }

    private void WriteAnonymousType(StringBuilder text, XmlSchemaComponent type)
    {
        if (type.Kind is 'C' or 'K')
            this.WriteComplexType(text, type);
        else
            this.WriteSimpleType(text, type);
    }

    /// <summary>An attribute declaration, or — when <paramref name="use"/> places a global — a reference to it.</summary>
    private void WriteAttribute(StringBuilder text, XmlSchemaComponent attribute, XmlSchemaPlacement? use)
    {
        var isReference = use is not null && attribute.ScopingId is null;
        var type = attribute.Placements.Count > 0 && this.byId.TryGetValue(attribute.Placements[0].PlacedId, out var found) ? found : null;
        var anonymous = !isReference && type is not null && type.ScopingId == attribute.Id;
        _ = text.Append("<xsd:attribute");
        if (isReference)
        {
            AppendAttribute(text, "ref", this.QName(attribute));
        }
        else
        {
            AppendAttribute(text, "name", attribute.Name!);
            if (type is not null && !anonymous && type.Id != XmlSchemaSystemCollection.AnySimpleTypeId)
                AppendAttribute(text, "type", this.QName(type));
            if (attribute.ScopingId is not null && attribute.MustBeQualified != this.attributeFormQualified)
                AppendAttribute(text, "form", attribute.MustBeQualified ? "qualified" : "unqualified");
        }
        if (use is { } placement)
        {
            if (placement.MaxOccurs == 0)
                _ = text.Append(" use=\"prohibited\"");
            else if (placement.MinOccurs == 1)
                _ = text.Append(" use=\"required\"");
        }
        var (value, isFixed) = isReference && use is { } reference ? (reference.DefaultValue, reference.IsDefaultFixed) : (attribute.DefaultValue, attribute.IsDefaultFixed);
        if (value is not null)
            AppendAttribute(text, isFixed ? "fixed" : "default", value);
        if (!anonymous)
        {
            _ = text.Append("/>");
            return;
        }
        _ = text.Append('>');
        this.WriteSimpleType(text, type!);
        _ = text.Append("</xsd:attribute>");
    }

    private void WriteComplexType(StringBuilder text, XmlSchemaComponent type)
    {
        _ = text.Append("<xsd:complexType");
        if (type.ScopingId is null)
            AppendAttribute(text, "name", type.Name!);
        if (type.IsAbstract)
            _ = text.Append(" abstract=\"true\"");
        if (Flags(("extension", type.IsExtensionBlocked), ("restriction", type.IsRestrictionBlocked)) is { } block)
            AppendAttribute(text, "block", block);
        if (Flags(("extension", type.IsFinalExtension), ("restriction", type.IsFinalRestriction)) is { } final)
            AppendAttribute(text, "final", final);
        if (type.AllowsMixedContent)
            _ = text.Append(" mixed=\"true\"");
        _ = text.Append('>');

        var simple = type.Kind == 'C';
        var derivation = type.Derivation == 'X' ? "extension" : "restriction";
        _ = text.Append(simple ? "<xsd:simpleContent>" : type.AllowsMixedContent ? "<xsd:complexContent mixed=\"true\">" : "<xsd:complexContent>")
            .Append("<xsd:").Append(derivation);
        var baseType = type.BaseId is int baseId && this.byId.TryGetValue(baseId, out var found) ? found : null;
        var inlineBase = baseType is not null && baseType.ScopingId == type.Id;
        if (baseType is not null && !inlineBase)
            AppendAttribute(text, "base", this.QName(baseType));

        var content = new StringBuilder();
        if (inlineBase)
            this.WriteSimpleType(content, baseType!);
        var first = 0;
        if (!simple && type.Placements.Count > 0 && this.byId.TryGetValue(type.Placements[0].PlacedId, out var particle) && particle.Kind == 'M')
        {
            this.WriteParticle(content, type.Placements[0]);
            first = 1;
        }
        if (simple)
            WriteFacets(content, type);
        this.WriteAttributeUses(content, type, first);
        if (content.Length == 0)
            _ = text.Append("/>");
        else
            _ = text.Append('>').Append(content).Append("</xsd:").Append(derivation).Append('>');
        _ = text.Append(simple ? "</xsd:simpleContent>" : "</xsd:complexContent>").Append("</xsd:complexType>");
    }

    /// <summary>The attribute uses, attribute group references and attribute wildcard of <paramref name="owner"/>'s placements from <paramref name="first"/> on.</summary>
    private void WriteAttributeUses(StringBuilder text, XmlSchemaComponent owner, int first)
    {
        for (var i = first; i < owner.Placements.Count; i++)
        {
            var placement = owner.Placements[i];
            if (!this.byId.TryGetValue(placement.PlacedId, out var placed))
                continue;
            switch (placed.Kind)
            {
                case 'A':
                    this.WriteAttribute(text, placed, placement);
                    break;
                case 'G':
                    _ = text.Append("<xsd:attributeGroup");
                    AppendAttribute(text, "ref", this.QName(placed));
                    _ = text.Append("/>");
                    break;
                case 'V':
                    WriteWildcard(text, placed, 1, 1);
                    break;
            }
        }
    }

    /// <summary>One placed particle: a model group, an element, a wildcard or a reference to a global element or group.</summary>
    private void WriteParticle(StringBuilder text, XmlSchemaPlacement placement)
    {
        if (!this.byId.TryGetValue(placement.PlacedId, out var placed))
            return;
        switch (placed.Kind)
        {
            case 'E' when placed.ScopingId is null:
                _ = text.Append("<xsd:element");
                AppendAttribute(text, "ref", this.QName(placed));
                AppendOccurrence(text, placement.MinOccurs, placement.MaxOccurs);
                _ = text.Append("/>");
                break;
            case 'E':
                this.WriteElement(text, placed, placement.MinOccurs, placement.MaxOccurs);
                break;
            case 'M' when placed.ScopingId is null:
                _ = text.Append("<xsd:group");
                AppendAttribute(text, "ref", this.QName(placed));
                AppendOccurrence(text, placement.MinOccurs, placement.MaxOccurs);
                _ = text.Append("/>");
                break;
            case 'M':
                this.WriteCompositor(text, placed, placement.MinOccurs, placement.MaxOccurs);
                break;
            case 'W':
                WriteWildcard(text, placed, placement.MinOccurs, placement.MaxOccurs);
                break;
        }
    }

    private void WriteCompositor(StringBuilder text, XmlSchemaComponent group, int minimum, int maximum)
    {
        var name = group.Compositor switch
        {
            'C' => "choice",
            'A' => "all",
            _ => "sequence",
        };
        _ = text.Append("<xsd:").Append(name);
        AppendOccurrence(text, minimum, maximum);
        if (group.Placements.Count == 0)
        {
            _ = text.Append("/>");
            return;
        }
        _ = text.Append('>');
        foreach (var placement in group.Placements)
            this.WriteParticle(text, placement);
        _ = text.Append("</xsd:").Append(name).Append('>');
    }

    private static void WriteWildcard(StringBuilder text, XmlSchemaComponent wildcard, int minimum, int maximum)
    {
        _ = text.Append(wildcard.Kind == 'V' ? "<xsd:anyAttribute" : "<xsd:any");
        if (!wildcard.DisallowNamespaces)
        {
            // As the declaration wrote them, ##targetNamespace resolved and
            // the absent namespace as ##local.
            AppendAttribute(text, "namespace", string.Join(' ', wildcard.WildcardNamespacesAsWritten.Select(static uri => uri.Length == 0 ? "##local" : uri)));
        }
        else if (wildcard.WildcardNamespaces.Length > 0)
        {
            _ = text.Append(" namespace=\"##other\"");
        }
        if (wildcard.ProcessContent is 'L' or 'P')
            AppendAttribute(text, "processContents", wildcard.ProcessContent == 'L' ? "lax" : "skip");
        if (wildcard.Kind == 'W')
            AppendOccurrence(text, minimum, maximum);
        _ = text.Append("/>");
    }

    private void WriteSimpleType(StringBuilder text, XmlSchemaComponent type)
    {
        _ = text.Append("<xsd:simpleType");
        if (type.ScopingId is null)
            AppendAttribute(text, "name", type.Name!);
        if (Flags(("restriction", type.IsFinalRestriction), ("list", type.IsFinalListMember), ("union", type.IsFinalUnionMember)) is { } final)
            AppendAttribute(text, "final", final);
        _ = text.Append('>');
        if (type.Derivation == 'R')
        {
            _ = text.Append("<xsd:restriction");
            var baseType = type.BaseId is int baseId && this.byId.TryGetValue(baseId, out var found) ? found : null;
            var inlineBase = baseType is not null && baseType.ScopingId == type.Id;
            if (baseType is not null && !inlineBase)
                AppendAttribute(text, "base", this.QName(baseType));
            var content = new StringBuilder();
            if (inlineBase)
                this.WriteSimpleType(content, baseType!);
            WriteFacets(content, type);
            _ = content.Length == 0 ? text.Append("/>") : text.Append('>').Append(content).Append("</xsd:restriction>");
        }
        else if (type.Kind == 'L')
        {
            _ = text.Append("<xsd:list");
            var item = type.Placements.Count > 0 && this.byId.TryGetValue(type.Placements[0].PlacedId, out var found) ? found : null;
            if (item is not null && item.ScopingId != type.Id)
            {
                AppendAttribute(text, "itemType", this.QName(item));
                _ = text.Append("/>");
            }
            else
            {
                _ = text.Append('>');
                if (item is not null)
                    this.WriteSimpleType(text, item);
                _ = text.Append("</xsd:list>");
            }
        }
        else
        {
            _ = text.Append("<xsd:union");
            var members = type.Placements.Select(placement => this.byId.GetValueOrDefault(placement.PlacedId)).Where(static member => member is not null).ToList();
            var named = members.Where(member => member!.ScopingId != type.Id).Select(member => this.QName(member!)).ToList();
            if (named.Count > 0)
                AppendAttribute(text, "memberTypes", string.Join(' ', named));
            var inline = members.Where(member => member!.ScopingId == type.Id).ToList();
            if (inline.Count == 0)
            {
                _ = text.Append("/>");
            }
            else
            {
                _ = text.Append('>');
                foreach (var member in inline)
                    this.WriteSimpleType(text, member!);
                _ = text.Append("</xsd:union>");
            }
        }
        _ = text.Append("</xsd:simpleType>");
    }

    private static void WriteFacets(StringBuilder text, XmlSchemaComponent type)
    {
        foreach (var facet in type.Facets)
        {
            var name = facet.Kind switch
            {
                "DF" => "fractionDigits",
                "DT" => "totalDigits",
                "EN" => "minExclusive",
                "EU" => "enumeration",
                "EX" => "maxExclusive",
                "IN" => "minInclusive",
                "IX" => "maxInclusive",
                "LG" => "length",
                "LN" => "minLength",
                "LX" => "maxLength",
                "PT" => "pattern",
                _ => "whiteSpace",
            };
            _ = text.Append("<xsd:").Append(name);
            AppendAttribute(text, "value", facet.Value);
            if (facet.IsFixed)
                _ = text.Append(" fixed=\"true\"");
            _ = text.Append("/>");
        }
    }

    /// <summary>
    /// A component's name as the schema being written writes it: <c>xsd:</c>
    /// for XML Schema's own, <c>t:</c> for the target namespace's, bare in no
    /// namespace, and another namespace under the <c>ns</c> prefix its first
    /// reference gave it.
    /// </summary>
    private string QName(XmlSchemaComponent component)
    {
        var namespaces = component.Id <= XmlSchemaSystemCollection.LastComponentId ? XmlSchemaSystemCollection.Components.Namespaces : this.set.Namespaces;
        var uri = component.NamespaceId > 0 && component.NamespaceId <= namespaces.Count ? namespaces[component.NamespaceId - 1] : string.Empty;
        if (uri == XsdNamespace)
            return "xsd:" + component.Name;
        if (uri == this.target)
            return uri.Length == 0 ? component.Name! : "t:" + component.Name;
        if (!this.foreign.TryGetValue(uri, out var prefix))
        {
            // sqltypes and the XML namespace keep prefixes of their own
            // (probed 2026-10-20 against SQL Server 2025).
            prefix = uri switch
            {
                SqlTypesNamespace => "sqlt",
                XmlNamespace => "xml",
                _ => "ns" + (this.foreign.Count(static pair => pair.Key is not (SqlTypesNamespace or XmlNamespace)) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            this.foreign[uri] = prefix;
        }
        return prefix + ":" + component.Name;
    }

    /// <summary>A <c>block</c> or <c>final</c> value: the set members in order, or <c>#all</c> when every one is set; null for none.</summary>
    private static string? Flags(params (string Name, bool Set)[] members)
    {
        if (!Array.Exists(members, static member => member.Set))
            return null;
        return Array.TrueForAll(members, static member => member.Set)
            ? "#all"
            : string.Join(' ', members.Where(static member => member.Set).Select(static member => member.Name));
    }

    private static void AppendOccurrence(StringBuilder text, int minimum, int maximum)
    {
        if (minimum != 1)
            AppendAttribute(text, "minOccurs", minimum.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (maximum != 1)
            AppendAttribute(text, "maxOccurs", maximum == Unbounded ? "unbounded" : maximum.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendAttribute(StringBuilder text, string name, string value)
    {
        _ = text.Append(' ').Append(name).Append("=\"");
        Parser.Selection.AppendForXmlText(text, value, isAttribute: true);
        _ = text.Append('"');
    }
}
