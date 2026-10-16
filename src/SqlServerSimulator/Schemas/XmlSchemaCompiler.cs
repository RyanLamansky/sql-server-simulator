using System.Collections.Frozen;
using System.Xml;
using System.Xml.Linq;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The compile checks SQL Server runs over the text of a
/// <c>CREATE XML SCHEMA COLLECTION</c> or <c>ALTER … ADD</c>, in its order
/// (probed 2026-10-07 and 2026-10-16 against SQL Server 2025). Every refusal
/// acts as under <c>XACT_ABORT</c> and draws no component ids.
/// </summary>
/// <remarks>
/// <para>
/// Real reads the documents in three passes, and which error a text with
/// several raises follows from them:
/// </para>
/// <list type="number">
/// <item>The top level of each document, in order (<see cref="ReadTopLevel"/>):
/// an element the schema element doesn't take there, a declaration without its
/// name, <c>include</c> / <c>notation</c> / <c>redefine</c>, and a type name
/// declared twice.</item>
/// <item>Each top-level declaration's subtree in document order
/// (<see cref="ReadDeclarations"/>), beginning with an element, attribute,
/// group or attribute group name another declaration repeats; each element
/// read for its required attributes, the names it references and each
/// attribute in written order, then the place of every child, and only then
/// each child.</item>
/// <item>The components' definitions (<see cref="CompileComponents"/>), each
/// global in document order compiling what it references first.</item>
/// </list>
/// <para>
/// An <c>ALTER … ADD</c>'s documents resolve their references against the
/// collection's own, but only the added documents are judged, and a name the
/// collection already declares is left to the later Msg 6310.
/// </para>
/// </remarks>
internal sealed partial class XmlSchemaCompiler
{
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string SqlTypesNamespace = "http://schemas.microsoft.com/sqlserver/2004/sqltypes";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>The added documents' <c>xsd:schema</c> roots, in order.</summary>
    private readonly List<XsdNode> documents;

    /// <summary>The first declaration of each global, the added documents' ahead of the collection's own.</summary>
    private readonly Dictionary<(string Kind, string Namespace, string Name), XsdNode> globals = [];

    /// <summary>The <c>id</c> values each document's elements carry, which repeat only as Msg 2398.</summary>
    private readonly HashSet<(XsdNode Document, string Id)> ids = [];

    /// <summary>Whether a document imports the <c>xml:</c> namespace, whose attributes real then predefines.</summary>
    private readonly bool importsXmlNamespace;

    /// <summary>How many times the added documents declare each global.</summary>
    private readonly Dictionary<(string Kind, string Namespace, string Name), int> addedDeclarations = [];

    private XmlSchemaCompiler(List<XsdNode> documents, List<XsdNode> existing)
    {
        this.documents = documents;
        foreach (var document in (List<XsdNode>)[.. documents, .. existing])
        {
            foreach (var child in document.Children)
            {
                if (child.IsXsd && child.Local == "import" && child.Element.Attribute("namespace")?.Value == XmlNamespace)
                    this.importsXmlNamespace = true;
                if (child.IsXsd && GlobalKind(child.Local) is { } kind && child.Element.Attribute("name") is { } name)
                {
                    var key = (kind, document.TargetNamespace, name.Value);
                    _ = this.globals.TryAdd(key, child);
                    if (!child.IsExisting)
                        this.addedDeclarations[key] = this.addedDeclarations.GetValueOrDefault(key) + 1;
                }
            }
        }
    }

    /// <summary>
    /// Raises the first refusal real's compile of <paramref name="addedText"/>
    /// meets, its references resolving against <paramref name="existingText"/>
    /// too (an <c>ALTER … ADD</c>'s collection), or returns when there is none.
    /// Text the reader can't get through is left to whatever reads it next.
    /// </summary>
    public static void Reject(string addedText, string? existingText)
    {
        if (ReadDocuments(addedText, existing: false) is not { } added)
            return;
        var existing = existingText is null ? [] : ReadDocuments(existingText, existing: true) ?? [];
        var compiler = new XmlSchemaCompiler(added, existing);
        compiler.ReadTopLevel();
        compiler.ReadDeclarations();
        compiler.CompileComponents();
    }

    private static string? GlobalKind(string local) => local switch
    {
        "attribute" => "attribute",
        "attributeGroup" => "attributeGroup",
        "complexType" or "simpleType" => "type",
        "element" => "element",
        "group" => "group",
        _ => null,
    };

    private static List<XsdNode>? ReadDocuments(string text, bool existing)
    {
        var roots = new List<XsdNode>();
        try
        {
            var settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            _ = reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    _ = reader.Read();
                    continue;
                }
                var element = (XElement)XNode.ReadFrom(reader);
                var local = element.Name.LocalName;
                counts[local] = counts.GetValueOrDefault(local) + 1;
                if (element.Name.NamespaceName == XsdNamespace && local == "schema")
                    roots.Add(new XsdNode(element, null, $"/*:{local}[{counts[local]}]", existing, element.Attribute("targetNamespace")?.Value ?? string.Empty));
            }
        }
        catch (XmlException)
        {
            return null;
        }
        return roots;
    }

    /// <summary>
    /// The first pass, over each document's top level: an element the schema
    /// element doesn't take (Msg 2297, an <c>import</c> after a declaration
    /// included), <c>include</c> and <c>notation</c> (Msg 9336, named with
    /// real's own <c>xsd:</c> prefix whatever the text writes) and
    /// <c>redefine</c> (Msg 2391), a declaration without its name (Msg 2299),
    /// and a type name the documents declare twice (Msg 2302) — the other
    /// kinds' duplicates wait for the second pass.
    /// </summary>
    private void ReadTopLevel()
    {
        var types = new HashSet<(string, string)>();
        foreach (var schema in this.documents)
        {
            if (AttributeError(schema, schema.Location) is { } schemaError)
                throw schemaError;
            var declarationSeen = false;
            foreach (var child in schema.Children)
            {
                if (!child.IsXsd)
                    continue;
                switch (child.Local)
                {
                    case "annotation":
                        break;
                    case "attribute" or "attributeGroup" or "complexType" or "element" or "group" or "simpleType":
                        declarationSeen = true;
                        if (child.Element.Attribute("name") is not { } name)
                            throw SimulatedSqlException.XmlSchemaRequiredAttributeMissing("name", child.Local);
                        if (child.Local is "complexType" or "simpleType" && !types.Add((schema.TargetNamespace, name.Value)))
                            throw SimulatedSqlException.XmlSchemaNameAlreadyDefined(name.Value);
                        break;
                    case "import" when !declarationSeen:
                        break;
                    case "include" or "notation":
                        throw SimulatedSqlException.XmlSchemaSyntaxNotSupported($"<xsd:{child.Local}>");
                    case "redefine":
                        throw SimulatedSqlException.XmlSchemaRedefineNotSupported();
                    default:
                        throw SimulatedSqlException.XmlSchemaElementNotValid(child.Local, child.Location);
                }
            }
        }
    }

    /// <summary>
    /// The second pass: each top-level declaration's subtree in document
    /// order, an element, attribute, group or attribute group whose name
    /// another declaration of its kind repeats refused (Msg 2302) before its
    /// subtree is read.
    /// </summary>
    private void ReadDeclarations()
    {
        foreach (var schema in this.documents)
        {
            foreach (var child in schema.Children)
            {
                if (!child.IsXsd)
                    continue;
                if (child.Local is "attribute" or "attributeGroup" or "element" or "group"
                    && child.Element.Attribute("name") is { } name
                    && this.addedDeclarations.GetValueOrDefault((GlobalKind(child.Local)!, schema.TargetNamespace, name.Value)) > 1)
                {
                    throw SimulatedSqlException.XmlSchemaNameAlreadyDefined(name.Value);
                }
                this.Read(child, declaredAttributes: null);
            }
        }
    }

    /// <summary>
    /// Reads one XSD element of a declaration's subtree: its required
    /// attributes, the names it references, its attributes in written order
    /// and the checks that span them, and a facet's base; then the place of
    /// each of its children, a type or content element holding no definition
    /// (Msg 2327), and only then each child in turn.
    /// <paramref name="declaredAttributes"/> holds the attribute names the
    /// enclosing complex type or attribute group has declared.
    /// </summary>
    private void Read(XsdNode node, HashSet<string>? declaredAttributes)
    {
        var local = node.Local;
        if (local is "key" or "keyref" or "unique")
            throw SimulatedSqlException.XmlSchemaSyntaxNotSupported(local);
        var isGlobal = node.Parent!.Parent is null;

        if (RequiredAttributeError(node, isGlobal) is { } required)
            throw required;
        this.ResolveReferences(node);

        var name = node.Element.Attribute("name")?.Value;
        var reference = node.Element.Attribute("ref")?.Value;
        if (local is "attribute" or "element" && reference is not null)
        {
            if (name is not null)
                throw SimulatedSqlException.XmlSchemaNameAndRef(node.Location);
            if (node.Element.Attribute("type") is not null)
                throw SimulatedSqlException.XmlSchemaTypeAndRef(node.Location);
        }

        // An attribute of a complex type's content element is located at the
        // type itself (probed 2026-10-07 against SQL Server 2025).
        if (AttributeError(node, local is "complexContent" or "simpleContent" ? node.Parent.Location : node.Location) is { } attributeError)
            throw attributeError;

        if (local is "attribute" or "element" && node.Element.Attribute("fixed") is not null && node.Element.Attribute("default") is not null)
            throw SimulatedSqlException.XmlSchemaAttributeNotValid("fixed", node.Location);
        if (local == "attribute" && node.Element.Attribute("default") is not null && node.Element.Attribute("use")?.Value.Trim() is "required")
            throw SimulatedSqlException.XmlSchemaAttributeNotValid("default", node.Location);
        if (local == "choice" && (node.Element.Attribute("minOccurs")?.Value.Trim() ?? "1") != "0" && !node.Children.Exists(static child => child.IsXsd && child.Local != "annotation"))
            throw SimulatedSqlException.XmlSchemaEmptyChoice(node.Location);
        if (node.Element.Attribute("id")?.Value.Trim() is { } id && !this.ids.Add((node.Document, id)))
            throw SimulatedSqlException.XmlSchemaDuplicateId(id);
        if (local == "import" && (node.Element.Attribute("namespace")?.Value ?? string.Empty) == node.TargetNamespace)
            throw SimulatedSqlException.XmlSchemaImportOfTargetNamespace();
        // An absent bound is 1 (probed 2026-10-16 against SQL Server 2025:
        // maxOccurs="0" alone is refused).
        if ((node.Element.Attribute("minOccurs") ?? node.Element.Attribute("maxOccurs")) is not null
            && (node.Element.Attribute("maxOccurs")?.Value.Trim() ?? "1") is var maxText && maxText != "unbounded"
            && ulong.TryParse(node.Element.Attribute("minOccurs")?.Value.Trim() ?? "1", System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minOccurs)
            && ulong.TryParse(maxText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maxOccurs)
            && minOccurs > maxOccurs)
        {
            throw SimulatedSqlException.XmlSchemaOccursOutOfOrder(node.Location);
        }
        if (local == "attribute" && declaredAttributes is not null && (name ?? LocalPart(reference)) is { } attributeName)
        {
            // Keyed by the attribute's namespace too: a reference to a global
            // and an unqualified local of one name are two attributes.
            var ns = reference is not null ? ResolveQualifiedName(node, reference.Trim()).Namespace
                : IsQualified(node, "attributeFormDefault") ? node.TargetNamespace : string.Empty;
            if (!declaredAttributes.Add($"{{{ns}}}{attributeName}"))
                throw SimulatedSqlException.XmlSchemaAttributeDeclaredTwice(attributeName);
        }

        if (IsFacet(local))
            this.CheckFacetPlacement(node);

        var attributeScope = local switch
        {
            "attributeGroup" or "complexType" => new HashSet<string>(StringComparer.Ordinal),
            "complexContent" or "extension" or "restriction" or "simpleContent" => declaredAttributes,
            _ => null,
        };
        if (local is "appinfo" or "documentation")
            return;
        // Every child's place is judged before any child is read (probed
        // 2026-10-16 against SQL Server 2025).
        var placement = new Placement(node);
        foreach (var child in node.Children)
        {
            if (!child.IsXsd)
                continue;
            if (!XsdElementNames.Contains(child.Local))
                throw SimulatedSqlException.XmlSchemaElementNotValid(child.Local, child.Location);
            placement.Admit(child);
        }
        if ((local is "complexContent" or "simpleContent" or "simpleType" || (local == "group" && isGlobal)) && !placement.HoldsDefinition)
            throw SimulatedSqlException.XmlSchemaContentMissing(local);

        foreach (var child in node.Children)
        {
            if (child.IsXsd)
                this.Read(child, attributeScope);
        }
    }

    /// <summary>
    /// The attribute an XSD element can't go without, checked ahead of its
    /// attributes (probed 2026-10-16 against SQL Server 2025): a local element
    /// or attribute's name or reference (Msg 2299), a group or attribute group
    /// reference's <c>ref</c> and a facet's <c>value</c> (Msg 2299), a
    /// derivation's base (Msg 2316) and the item or member types of a list or
    /// union (Msg 2393 / 2339).
    /// </summary>
    private static SimulatedSqlException? RequiredAttributeError(XsdNode node, bool isGlobal)
    {
        var element = node.Element;
        bool HasTypeChild() => node.Children.Exists(static child => child.IsXsd && child.Local == "simpleType");
        return node.Local switch
        {
            "attribute" or "element" when !isGlobal && element.Attribute("name") is null && element.Attribute("ref") is null =>
                SimulatedSqlException.XmlSchemaRequiredAttributeMissing("name", node.Local),
            "attributeGroup" or "group" when !isGlobal && element.Attribute("ref") is null =>
                SimulatedSqlException.XmlSchemaRequiredAttributeMissing("ref", node.Local),
            "extension" when element.Attribute("base") is null => SimulatedSqlException.XmlSchemaBaseMissing(node.Location),
            "restriction" when element.Attribute("base") is null && (node.Parent!.Local != "simpleType" || !HasTypeChild()) =>
                SimulatedSqlException.XmlSchemaBaseMissing(node.Location),
            "list" when element.Attribute("itemType") is null && !HasTypeChild() => SimulatedSqlException.XmlSchemaListItemTypeMissing(node.Location),
            "union" when string.IsNullOrWhiteSpace(element.Attribute("memberTypes")?.Value) && !HasTypeChild() => SimulatedSqlException.XmlSchemaUnionMemberTypesMissing(node.Location),
            _ when IsFacet(node.Local) && element.Attribute("value") is null => SimulatedSqlException.XmlSchemaRequiredAttributeMissing("value", node.Local),
            _ => null,
        };
    }

    /// <summary>
    /// Msg 2379 / 2307 / 2308 for the first name this element references that
    /// isn't an XML name or that nothing declares, in written order. Real
    /// predefines no <c>xml:</c> attributes and resolves the <c>sqltypes</c>
    /// namespace an import names.
    /// </summary>
    private void ResolveReferences(XsdNode node)
    {
        foreach (var attribute in node.Element.Attributes())
        {
            if (attribute.Name.NamespaceName.Length != 0)
                continue;
            var kind = (node.Local, attribute.Name.LocalName) switch
            {
                ("attribute", "ref") => "attribute",
                ("attributeGroup", "ref") => "attributeGroup",
                ("element", "ref") or ("element", "substitutionGroup") => "element",
                ("group", "ref") => "group",
                ("attribute" or "element", "type") or ("extension" or "restriction", "base") or ("list", "itemType") or ("union", "memberTypes") => "type",
                _ => null,
            };
            if (kind is null)
                continue;
            foreach (var written in attribute.Value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (written.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon && written[..colon] is var prefix && prefix != "xml"
                    && node.Element.GetNamespaceOfPrefix(prefix) is null)
                {
                    throw SimulatedSqlException.XmlSchemaPrefixNotDeclared(prefix);
                }
                var qualifiedName = ResolveQualifiedName(node, written);
                if (!IsNcName(qualifiedName.Name))
                    throw SimulatedSqlException.XmlSchemaNameNotValid(qualifiedName.Name);
                if (!this.Resolves(kind, qualifiedName))
                    throw SimulatedSqlException.XmlSchemaUndefinedName(qualifiedName.Name, qualifiedName.Namespace);
                if (kind == "type" && qualifiedName.Namespace == XsdNamespace && UnsupportedBuiltInError(node.Local, qualifiedName.Name) is { } unsupported)
                    throw unsupported;
                if (kind == "type" && this.globals.GetValueOrDefault(("type", qualifiedName.Namespace, qualifiedName.Name)) is { Local: "complexType" })
                {
                    if (node.Local == "list")
                        throw SimulatedSqlException.XmlSchemaListItemTypeNotSimple(node.Location);
                    if (node.Local == "restriction" && node.Parent!.Local == "simpleType")
                        throw SimulatedSqlException.XmlSchemaRestrictionBaseNotSimple(qualifiedName.Name);
                }
            }
        }
    }

    /// <summary>
    /// The built-in types real refuses to take as written, as the name
    /// resolves (probed 2026-10-16 against SQL Server 2025): <c>NOTATION</c>
    /// anywhere (Msg 9337), and a restriction of <c>anySimpleType</c>
    /// (Msg 2201) or <c>QName</c> (Msg 2276) and an extension of
    /// <c>anyType</c> (Msg 2237).
    /// </summary>
    private static SimulatedSqlException? UnsupportedBuiltInError(string element, string type) => (element, type) switch
    {
        (_, "NOTATION") => SimulatedSqlException.XmlSchemaTypeNotSupported(type),
        ("extension", "anyType") => SimulatedSqlException.XmlSchemaAnyTypeExtension(),
        ("restriction", "anySimpleType") => SimulatedSqlException.XmlSchemaAnySimpleTypeRestriction(),
        ("restriction", "QName") => SimulatedSqlException.XmlSchemaQNameRestriction(),
        _ => null,
    };

    private bool Resolves(string kind, XmlQualifiedName name) =>
        name.Namespace == XsdNamespace
            ? kind == "type" && (System.Xml.Schema.XmlSchemaType.GetBuiltInSimpleType(name) is not null || name.Name == "anyType")
            : name.Namespace == SqlTypesNamespace || this.globals.ContainsKey((kind, name.Namespace, name.Name))
                || (name.Namespace == XmlNamespace && kind == "attribute" && this.importsXmlNamespace && name.Name is "base" or "lang" or "space");

    /// <summary>The namespace and local name a QName-valued attribute of <paramref name="node"/> names.</summary>
    private static XmlQualifiedName ResolveQualifiedName(XsdNode node, string written)
    {
        var colon = written.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : written[..colon];
        var local = colon < 0 ? written : written[(colon + 1)..];
        var ns = prefix switch
        {
            "" => node.Element.GetDefaultNamespace().NamespaceName,
            "xml" => XmlNamespace,
            _ => node.Element.GetNamespaceOfPrefix(prefix)?.NamespaceName ?? string.Empty,
        };
        return new XmlQualifiedName(local, ns);
    }

    /// <summary>
    /// A facet its base type doesn't take (Msg 2319, located at the facet),
    /// then a facet its restriction repeats (Msg 2320) — <c>enumeration</c> and
    /// <c>pattern</c> repeat freely — then a pattern that isn't a regular
    /// expression (Msg 572).
    /// </summary>
    private void CheckFacetPlacement(XsdNode facet)
    {
        var restriction = facet.Parent!;
        if (restriction.Local == "restriction" && this.FacetFamilyOfRestriction(restriction, []) is { } family && !FacetApplies(family, facet.Local))
            throw SimulatedSqlException.XmlSchemaFacetNotAllowed(facet.Local, facet.Location);
        if (facet.Local is not ("enumeration" or "pattern"))
        {
            foreach (var sibling in restriction.Children)
            {
                if (ReferenceEquals(sibling, facet))
                    break;
                if (sibling.IsXsd && sibling.Local == facet.Local)
                    throw SimulatedSqlException.XmlSchemaDuplicateFacet(facet.Local, facet.Location);
            }
        }
        if (facet.Local == "pattern")
            RejectInvalidPattern(facet);
    }

    private static bool IsFacet(string local) =>
        local is "enumeration" or "fractionDigits" or "length" or "maxExclusive" or "maxInclusive" or "maxLength"
            or "minExclusive" or "minInclusive" or "minLength" or "pattern" or "totalDigits" or "whiteSpace";

    /// <summary>The families of simple types that take the same facets.</summary>
    private enum FacetFamily
    {
        /// <summary>The string-like primitives, the binaries, <c>anyURI</c>, <c>QName</c> and <c>NOTATION</c>.</summary>
        Lengthed,
        Boolean,
        Decimal,

        /// <summary>The floating-point, duration and date-time primitives.</summary>
        Ordered,
        List,
        Union,
    }

    private static bool FacetApplies(FacetFamily family, string facet) => family switch
    {
        FacetFamily.Boolean => facet is "pattern" or "whiteSpace",
        FacetFamily.Decimal => facet is not ("length" or "maxLength" or "minLength"),
        FacetFamily.Lengthed => facet is "enumeration" or "length" or "maxLength" or "minLength" or "pattern" or "whiteSpace",
        FacetFamily.List => facet is "enumeration" or "length" or "maxLength" or "minLength" or "pattern",
        FacetFamily.Ordered => facet is not ("fractionDigits" or "length" or "maxLength" or "minLength" or "totalDigits"),
        _ => facet is "enumeration" or "pattern",
    };

    /// <summary>
    /// The facet family of the type a restriction derives from, or null when
    /// it can't be settled (an unknown base, a cycle, a type that takes every
    /// facet). <paramref name="visiting"/> guards a base chain returning to itself.
    /// </summary>
    private FacetFamily? FacetFamilyOfRestriction(XsdNode restriction, HashSet<XsdNode> visiting)
    {
        if (restriction.Element.Attribute("base")?.Value is { } written)
            return this.FacetFamilyOfType(ResolveQualifiedName(restriction, written.Trim()), visiting);
        return restriction.Children.Find(static child => child.IsXsd && child.Local == "simpleType") is { } inline
            ? this.FacetFamilyOfDefinition(inline, visiting)
            : null;
    }

    private FacetFamily? FacetFamilyOfType(XmlQualifiedName name, HashSet<XsdNode> visiting)
    {
        if (name.Namespace == XsdNamespace)
            return BuiltInFacetFamily(name.Name);
        return this.globals.TryGetValue(("type", name.Namespace, name.Name), out var definition) ? this.FacetFamilyOfDefinition(definition, visiting) : null;
    }

    private FacetFamily? FacetFamilyOfDefinition(XsdNode definition, HashSet<XsdNode> visiting)
    {
        if (!visiting.Add(definition))
            return null;
        foreach (var child in definition.Children)
        {
            if (!child.IsXsd)
                continue;
            switch (child.Local)
            {
                case "complexContent":
                    return null;
                case "extension" or "restriction" when definition.Local == "simpleContent":
                    return child.Element.Attribute("base")?.Value is { } written ? this.FacetFamilyOfType(ResolveQualifiedName(child, written.Trim()), visiting) : null;
                case "list":
                    return FacetFamily.List;
                case "restriction":
                    return this.FacetFamilyOfRestriction(child, visiting);
                case "simpleContent":
                    return this.FacetFamilyOfDefinition(child, visiting);
                case "union":
                    return FacetFamily.Union;
            }
        }
        return null;
    }

    private static FacetFamily? BuiltInFacetFamily(string local) => local switch
    {
        "boolean" => FacetFamily.Boolean,
        "byte" or "decimal" or "int" or "integer" or "long" or "negativeInteger" or "nonNegativeInteger" or "nonPositiveInteger"
            or "positiveInteger" or "short" or "unsignedByte" or "unsignedInt" or "unsignedLong" or "unsignedShort" => FacetFamily.Decimal,
        "date" or "dateTime" or "double" or "duration" or "float" or "gDay" or "gMonth" or "gMonthDay" or "gYear" or "gYearMonth" or "time" => FacetFamily.Ordered,
        "ENTITIES" or "IDREFS" or "NMTOKENS" => FacetFamily.List,
        "anySimpleType" or "anyType" => null,
        _ => FacetFamily.Lengthed,
    };

    /// <summary>The XSD namespace's element names; any other is Msg 2297.</summary>
    private static readonly FrozenSet<string> XsdElementNames = new[]
    {
        "all", "annotation", "any", "anyAttribute", "appinfo", "attribute", "attributeGroup", "choice", "complexContent",
        "complexType", "documentation", "element", "enumeration", "extension", "field", "fractionDigits", "group", "import",
        "include", "key", "keyref", "length", "list", "maxExclusive", "maxInclusive", "maxLength", "minExclusive",
        "minInclusive", "minLength", "notation", "pattern", "redefine", "restriction", "schema", "selector", "sequence",
        "simpleContent", "simpleType", "totalDigits", "union", "unique", "whiteSpace",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The unqualified attributes each XSD element takes, by local name — a
    /// global and a local <c>element</c> or <c>attribute</c> differ, and so
    /// does a reference — as real reads them (probed 2026-10-07 and 2026-10-16
    /// against SQL Server 2025: real takes <c>abstract</c> and <c>mixed</c> on
    /// a <c>simpleType</c> and <c>form</c> on a global element, beyond the XSD
    /// recommendation). An element missing here goes unchecked.
    /// </summary>
    private static readonly FrozenDictionary<string, FrozenSet<string>> XsdAttributes = new Dictionary<string, string[]>
    {
        ["all"] = ["id", "maxOccurs", "minOccurs"],
        ["annotation"] = ["id"],
        ["any"] = ["id", "maxOccurs", "minOccurs", "namespace", "processContents"],
        ["anyAttribute"] = ["id", "namespace", "processContents"],
        ["appinfo"] = ["source"],
        ["attribute"] = ["default", "fixed", "form", "id", "name", "ref", "type", "use"],
        ["attribute ref"] = ["default", "fixed", "id", "ref", "use"],
        ["attributeGroup"] = ["id", "ref"],
        ["choice"] = ["id", "maxOccurs", "minOccurs"],
        ["complexContent"] = ["id", "mixed"],
        ["complexType"] = ["abstract", "block", "final", "id", "mixed", "name"],
        ["documentation"] = ["source"],
        ["element ref"] = ["id", "maxOccurs", "minOccurs", "ref"],
        ["enumeration"] = ["id", "value"],
        ["extension"] = ["base", "id"],
        ["fractionDigits"] = ["fixed", "id", "value"],
        ["global attribute"] = ["default", "fixed", "form", "id", "name", "ref", "type"],
        ["global attributeGroup"] = ["id", "name"],
        ["global element"] = ["abstract", "block", "default", "final", "fixed", "form", "id", "name", "nillable", "ref", "substitutionGroup", "type"],
        ["global group"] = ["abstract", "id", "mixed", "name"],
        ["group"] = ["id", "maxOccurs", "minOccurs", "ref"],
        ["import"] = ["id", "namespace", "schemaLocation"],
        ["length"] = ["fixed", "id", "value"],
        ["list"] = ["id", "itemType"],
        ["local element"] = ["block", "default", "fixed", "form", "id", "maxOccurs", "minOccurs", "name", "nillable", "ref", "type"],
        ["maxExclusive"] = ["fixed", "id", "value"],
        ["maxInclusive"] = ["fixed", "id", "value"],
        ["maxLength"] = ["fixed", "id", "value"],
        ["minExclusive"] = ["fixed", "id", "value"],
        ["minInclusive"] = ["fixed", "id", "value"],
        ["minLength"] = ["fixed", "id", "value"],
        ["pattern"] = ["id", "value"],
        ["restriction"] = ["base", "id"],
        ["schema"] = ["attributeFormDefault", "blockDefault", "elementFormDefault", "finalDefault", "id", "targetNamespace", "version"],
        ["sequence"] = ["id", "maxOccurs", "minOccurs"],
        ["simpleContent"] = ["id"],
        ["simpleType"] = ["abstract", "final", "id", "mixed", "name"],
        ["totalDigits"] = ["fixed", "id", "value"],
        ["union"] = ["id", "memberTypes"],
        ["whiteSpace"] = ["fixed", "id", "value"],
    }.ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// The first of <paramref name="node"/>'s attributes, in written order,
    /// that its element doesn't take (Msg 2298, located at
    /// <paramref name="location"/>) or whose value it refuses (Msg 2309 /
    /// 2312 / 2313 / 2386, and Msg 2313 for a <c>documentation</c>'s
    /// <c>xml:lang</c> that isn't a language tag).
    /// </summary>
    private static SimulatedSqlException? AttributeError(XsdNode node, string location)
    {
        var local = node.Local;
        var isGlobal = node.Parent?.Parent is null;
        var listKey = local switch
        {
            "attribute" or "element" when node.Element.Attribute("ref") is not null && !isGlobal => local + " ref",
            "attribute" or "attributeGroup" or "element" or "group" when isGlobal => "global " + local,
            "element" => "local element",
            _ => local,
        };
        if (!XsdAttributes.TryGetValue(listKey, out var allowed))
            return null;
        foreach (var attribute in node.Element.Attributes())
        {
            // Namespace declarations and attributes of another namespace ride
            // along, and xml:lang is taken everywhere but appinfo and import.
            if (attribute.IsNamespaceDeclaration)
                continue;
            var name = attribute.Name.LocalName;
            var ns = attribute.Name.NamespaceName;
            if (ns.Length == 0 && name == "form" && local is "attribute" or "element" && (isGlobal ? local == "attribute" : node.Element.Attribute("ref") is not null))
                return isGlobal ? SimulatedSqlException.XmlSchemaFormOnGlobal(location) : SimulatedSqlException.XmlSchemaFormOnReference(location);
            if (ns.Length == 0 && name is "maxOccurs" or "minOccurs" && local is "all" or "choice" or "sequence" && node.Parent is { Local: "group", Parent.Parent: null })
                return SimulatedSqlException.XmlSchemaAttributeNotValid(name, location);
            var error = ns.Length == 0
                ? allowed.Contains(name) ? AttributeValueError(local, name, attribute.Value) : SimulatedSqlException.XmlSchemaAttributeNotValid(name, location)
                : ns == XmlNamespace && name == "lang"
                    ? local is "appinfo" or "import" ? SimulatedSqlException.XmlSchemaAttributeNotValid(name, location)
                    : local == "documentation" && !IsLanguageTag(attribute.Value) ? SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(name, attribute.Value)
                    : null
                : null;
            if (error is not null)
                return error;
        }
        return null;
    }

    /// <summary>The value refusals of <see cref="AttributeError"/>: Msg 2309, 2312, 2313 and 2386.</summary>
    private static SimulatedSqlException? AttributeValueError(string element, string attribute, string value)
    {
        var trimmed = value.Trim();
        switch (attribute)
        {
            case "id":
            case "name" when element is not "schema":
                if (!IsNcName(trimmed))
                    return SimulatedSqlException.XmlSchemaAttributeNotOfType(attribute, "NCName", value);
                return attribute == "name" && element == "attribute" && trimmed == "xmlns" ? SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value) : null;
            case "namespace" when element is "any" or "anyAttribute":
                var tokens = trimmed.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                return tokens is ["##any"] or ["##other"] || !Array.Exists(tokens, static token => token is "##any" or "##other")
                    ? null
                    : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "abstract" or "mixed" or "nillable":
            case "fixed" when element is not ("attribute" or "element"):
                return trimmed is "0" or "1" or "false" or "true" ? null : SimulatedSqlException.XmlSchemaAttributeNotBoolean(attribute, value);
            case "block" or "blockDefault" or "final" or "finalDefault":
                // #all, or a list of the derivations the attribute names
                // where it stands (probed 2026-10-16 against SQL Server 2025).
                string[] derivations = (element, attribute) switch
                {
                    ("element", "block") or ("schema", "blockDefault") => ["extension", "restriction", "substitution"],
                    ("schema", "finalDefault") => ["extension", "list", "restriction", "union"],
                    ("simpleType", _) => ["list", "restriction", "union"],
                    _ => ["extension", "restriction"],
                };
                return trimmed == "#all" || Array.TrueForAll(trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries), token => Array.IndexOf(derivations, token) >= 0)
                    ? null
                    : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "attributeFormDefault" or "elementFormDefault" or "form":
                return trimmed is "qualified" or "unqualified" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "maxOccurs" when trimmed == "unbounded":
                return null;
            case "maxOccurs" or "minOccurs":
                return !ulong.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var occurs)
                    ? SimulatedSqlException.XmlSchemaFacetValueNotNumber(attribute)
                    : occurs > int.MaxValue ? SimulatedSqlException.XmlSchemaOccursOutOfRange(value, attribute) : null;
            case "processContents":
                return trimmed is "lax" or "skip" or "strict" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "use":
                return trimmed is "optional" or "prohibited" or "required" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "value" when element is "fractionDigits" or "length" or "maxLength" or "minLength" or "totalDigits":
                // A leading plus sign reads (probed 2026-10-16 against SQL Server 2025).
                return !ulong.TryParse(trimmed.StartsWith('+') ? trimmed[1..] : trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                    ? SimulatedSqlException.XmlSchemaFacetValueNotNumber()
                    : count == 0 && element == "totalDigits" ? SimulatedSqlException.XmlSchemaTotalDigitsOutOfRange() : null;
            case "value" when element == "whiteSpace":
                // Named after the facet rather than the attribute (probed
                // 2026-10-16 against SQL Server 2025).
                return trimmed is "collapse" or "preserve" or "replace" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(element, value);
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="value"/> is an <c>xs:language</c>: letters, then hyphenated letter-or-digit runs, each of one to eight.</summary>
    private static bool IsLanguageTag(string value)
    {
        var parts = value.Trim().Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 8 || !parts[i].All(c => char.IsAsciiLetter(c) || (i > 0 && char.IsAsciiDigit(c))))
                return false;
        }
        return true;
    }

    private static bool IsNcName(string name)
    {
        try
        {
            _ = XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string? LocalPart(string? qualifiedName) =>
        qualifiedName?[(qualifiedName.IndexOf(':', StringComparison.Ordinal) + 1)..];
}

/// <summary>
/// One element of a schema document: its XML, its parent, real's location
/// trail for it (<c>/*:schema[1]/*:element[2]</c>) and its child elements.
/// The content of an <c>appinfo</c>, a <c>documentation</c> or an element
/// outside the XSD namespace isn't read.
/// </summary>
internal sealed class XsdNode
{
    public readonly XElement Element;
    public readonly XsdNode? Parent;
    public readonly string Location;
    public readonly string Local;
    public readonly bool IsXsd;

    /// <summary>Whether the node is one of an <c>ALTER … ADD</c>'s collection's own documents rather than an added one.</summary>
    public readonly bool IsExisting;

    /// <summary>The <c>targetNamespace</c> of the node's document, empty for none.</summary>
    public readonly string TargetNamespace;

    public readonly List<XsdNode> Children = [];

    /// <summary>The <c>xsd:schema</c> root of the node's document.</summary>
    public readonly XsdNode Document;

    public XsdNode(XElement element, XsdNode? parent, string location, bool isExisting, string targetNamespace)
    {
        this.Element = element;
        this.Parent = parent;
        this.Document = parent?.Document ?? this;
        this.Location = location;
        this.Local = element.Name.LocalName;
        this.IsXsd = element.Name.NamespaceName == "http://www.w3.org/2001/XMLSchema";
        this.IsExisting = isExisting;
        this.TargetNamespace = targetNamespace;
        if (!this.IsXsd || this.Local is "appinfo" or "documentation")
            return;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            counts[local] = counts.GetValueOrDefault(local) + 1;
            this.Children.Add(new XsdNode(child, this, $"{location}/*:{local}[{counts[local]}]", isExisting, targetNamespace));
        }
    }
}
