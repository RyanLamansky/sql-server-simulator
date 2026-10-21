using System.Xml;
using System.Xml.Schema;

namespace SqlServerSimulator.Schemas;

// The third pass: each component's definition.
internal sealed partial class XmlSchemaCompiler
{
    /// <summary>The definitions compiled so far, by the node declaring them; a definition that failed silently maps to null.</summary>
    private readonly Dictionary<XsdNode, XsdType?> compiledTypes = [];

    /// <summary>The names of the added simple types Msg 6966 warns of, in compile order; see <see cref="Reject"/>.</summary>
    private List<string> roundTripWarnings = [];

    /// <summary>The definitions being compiled, which a reference back to closes a loop.</summary>
    private readonly HashSet<XsdNode> compilingTypes = [];

    /// <summary>The types being compiled, as far as they are, for a declaration inside one that names it.</summary>
    private readonly Dictionary<XsdNode, XsdType> partialTypes = [];

    /// <summary>The element and attribute declarations compiled, and the type each settled on.</summary>
    private readonly Dictionary<XsdNode, XsdType?> declarationTypes = [];

    /// <summary>The groups and attribute groups compiled, with a group's particle.</summary>
    private readonly Dictionary<XsdNode, Particle?> compiledGroups = [];

    /// <summary>The element declarations being compiled, which a substitution group reaching back to closes a loop.</summary>
    private readonly HashSet<XsdNode> compilingElements = [];

    private readonly HashSet<XsdNode> compilingGroups = [];

    private readonly Dictionary<string, SimpleXsdType> builtIns = new(StringComparer.Ordinal);

    /// <summary>How a type reference closing a loop is reported.</summary>
    private enum CycleReport
    {
        /// <summary>Msg 2366, naming the type referenced: a simple restriction's base, a list's item type, a simple content's base.</summary>
        Circular,

        /// <summary>Msg 6953, naming the type referencing it: a complex content's base, a union's member.</summary>
        Recursive,

        /// <summary>No loop at all: an element or attribute naming its type, which takes the type as far as it is compiled.</summary>
        None,
    }

    /// <summary>
    /// The third pass: every global the added documents declare, in document
    /// order, each compiling what it references first, so an error in a type
    /// an earlier element names is the one raised.
    /// </summary>
    private void CompileComponents()
    {
        foreach (var schema in this.documents)
        {
            foreach (var child in schema.Children)
            {
                if (!child.IsXsd)
                    continue;
                switch (child.Local)
                {
                    case "attribute":
                        _ = this.CompileAttribute(child);
                        break;
                    case "attributeGroup":
                        this.CompileAttributeGroup(child);
                        break;
                    case "complexType" or "simpleType":
                        _ = this.CompileDefinition(child);
                        break;
                    case "element":
                        _ = this.CompileElement(child);
                        break;
                    case "group":
                        _ = this.CompileGroup(child);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// A definition of the collection's own documents compiles without
    /// raising: it was accepted when it was added, and a gap here must not
    /// refuse a text that only references it.
    /// </summary>
    private static T? Quietly<T>(XsdNode node, Func<T?> compile)
        where T : class
    {
        if (!node.IsExisting)
            return compile();
        try
        {
            return compile();
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }

    // ---- names --------------------------------------------------------------

    /// <summary>
    /// Real's path for a component in its messages: <c>xs-nun(/r/complexType()/sequence()/a)</c>,
    /// a global named <c>ns:r</c> and the whole led by <c>#xmlns(ns=urn:x) </c>
    /// in a target namespace, and the derivation elements left out.
    /// </summary>
    private static string PathOf(XsdNode node)
    {
        var steps = new List<string>();
        var ns = node.TargetNamespace;
        string Global(string name) => ns.Length == 0 ? name : "ns:" + name;
        for (var current = node; current.Parent is { } parent; current = parent)
        {
            var isGlobal = parent.Parent is null;
            var name = current.Element.Attribute("name")?.Value ?? LocalPart(current.Element.Attribute("ref")?.Value) ?? string.Empty;
            var step = current.Local switch
            {
                "attribute" => isGlobal ? "/@" + Global(name) : "/@" + name,
                "all" or "choice" or "sequence" => $"/{current.Local}()",
                "attributeGroup" or "complexType" or "group" or "simpleType" => isGlobal ? $"/{current.Local}({Global(name)})" : $"/{current.Local}()",
                "element" => isGlobal ? "/" + Global(name) : "/" + name,
                _ => null,
            };
            if (step is not null)
                steps.Add(step);
        }
        steps.Reverse();
        return (ns.Length == 0 ? string.Empty : $"#xmlns(ns={ns}) ") + "xs-nun(" + string.Concat(steps) + ")";
    }

    /// <summary>A type's name in real's messages: <c>t</c>, <c>{urn:x}t</c>, or an anonymous one's path.</summary>
    private static string TypeNameOf(XsdNode definition) =>
        definition.Parent!.Parent is null && definition.Element.Attribute("name")?.Value is { } name
            ? definition.TargetNamespace.Length == 0 ? name : $"{{{definition.TargetNamespace}}}{name}"
            : PathOf(definition);

    private static string DeclaredName(XsdNode node) =>
        node.Element.Attribute("name")?.Value ?? LocalPart(node.Element.Attribute("ref")?.Value) ?? string.Empty;

    // ---- type references ----------------------------------------------------

    /// <summary>
    /// The type <paramref name="written"/> names, compiled, or null for one this
    /// compiler can't read (a <c>sqltypes</c> type, one failing quietly).
    /// <paramref name="referrer"/> is the definition referencing it, which a
    /// <see cref="CycleReport.Recursive"/> loop is reported against.
    /// </summary>
    private XsdType? TypeNamed(XsdNode node, string written, XsdNode? referrer, CycleReport report)
    {
        var name = ResolveQualifiedName(node, written.Trim());
        if (name.Namespace == XsdNamespace)
            return name.Name == "anyType" ? ComplexXsdType.AnyType : this.BuiltIn(name.Name);
        if (!this.globals.TryGetValue(("type", name.Namespace, name.Name), out var definition))
            return null;
        if (this.compilingTypes.Contains(definition))
        {
            if (report == CycleReport.None)
                return this.partialTypes.GetValueOrDefault(definition);
            throw report == CycleReport.Circular || referrer is null
                ? SimulatedSqlException.XmlSchemaCircularDefinition(name.Name)
                : SimulatedSqlException.XmlSchemaRecursiveType(TypeNameOf(referrer));
        }
        return this.CompileDefinition(definition);
    }

    private SimpleXsdType? BuiltIn(string local)
    {
        if (this.builtIns.TryGetValue(local, out var known))
            return known;
        if (XmlSchemaType.GetBuiltInSimpleType(new XmlQualifiedName(local, XsdNamespace)) is not { } builtIn)
            return null;
        var type = new SimpleXsdType("xs:" + local, "xs:" + local, null)
        {
            BuiltIn = builtIn,
            IsId = local == "ID",
            IsIdReference = local is "IDREF" or "IDREFS",
            IsQName = local is "NOTATION" or "QName",
            WhiteSpace = local switch
            {
                "normalizedString" => 1,
                "string" => 0,
                _ => 2,
            },
            IsStringFamily = IsStringFamily(local),
            IntegerFamily = BuiltInFacetFamily(local) == FacetFamily.Decimal && local != "decimal",
        };
        if (local is "ENTITIES" or "IDREFS" or "NMTOKENS")
        {
            type.MinimumItems = true;
            type.Variety = SimpleVariety.List;
            type.ItemType = this.BuiltIn(local[..^1]);
        }
        this.builtIns[local] = type;
        return type;
    }

    private static bool IsStringFamily(string local) =>
        local is "ENTITY" or "ID" or "IDREF" or "NCName" or "NMTOKEN" or "Name" or "language" or "normalizedString" or "string" or "token";

    /// <summary>Compiles a named or anonymous <c>simpleType</c> or <c>complexType</c> once.</summary>
    private XsdType? CompileDefinition(XsdNode definition)
    {
        if (this.compiledTypes.TryGetValue(definition, out var done))
            return done;
        _ = this.compilingTypes.Add(definition);
        XsdType? type;
        try
        {
            type = definition.Local == "simpleType"
                ? Quietly(definition, () => this.CompileSimpleType(definition))
                : Quietly(definition, () => this.CompileComplexType(definition));
        }
        finally
        {
            _ = this.compilingTypes.Remove(definition);
        }
        this.compiledTypes[definition] = type;
        if (!definition.IsExisting && type is SimpleXsdType simple && WarnsOfRoundTripping(simple))
            this.roundTripWarnings.Add(simple.Name);
        return type;
    }

    /// <summary>
    /// Whether real warns of <paramref name="type"/> with Msg 6966: a
    /// restriction of a numeric, boolean, duration or date-time primitive
    /// that a <c>pattern</c> facet restricts, its own or a base's (probed
    /// 2026-10-20 against SQL Server 2025).
    /// </summary>
    private static bool WarnsOfRoundTripping(SimpleXsdType type)
    {
        if (type.Base is null || type.Variety != SimpleVariety.Atomic || type.BuiltIn is not { } builtIn)
            return false;
        var patterned = false;
        for (var current = type; current is not null && !patterned; current = current.Base)
            patterned = current.Facets.Exists(static facet => facet.Name == "pattern");
        return patterned && Storage.XmlAtomicTypes.ComparisonFamily("xs:" + builtIn.QualifiedName.Name) is "number" or "boolean"
            or "Duration" or "DateTime" or "Time" or "Date" or "GYearMonth" or "GYear" or "GMonthDay" or "GDay" or "GMonth";
    }

    private static string FinalOf(XsdNode definition)
    {
        if (definition.Element.Attribute("final")?.Value is { } final)
            return final;
        var root = definition;
        while (root.Parent is { } parent)
            root = parent;
        return definition.Parent!.Parent is null ? root.Element.Attribute("finalDefault")?.Value ?? string.Empty : string.Empty;
    }

    private static bool Forbids(string final, string method) =>
        final.Split(' ', StringSplitOptions.RemoveEmptyEntries) is var tokens && (Array.IndexOf(tokens, "#all") >= 0 || Array.IndexOf(tokens, method) >= 0);

    // ---- simple types ---------------------------------------------------------

    private SimpleXsdType CompileSimpleType(XsdNode definition)
    {
        var type = new SimpleXsdType(TypeNameOf(definition), PathOf(definition), definition) { Final = FinalOf(definition) };
        this.partialTypes[definition] = type;
        foreach (var derivation in definition.Children)
        {
            if (!derivation.IsXsd)
                continue;
            switch (derivation.Local)
            {
                case "list":
                    type.Variety = SimpleVariety.List;
                    type.WhiteSpace = 2;
                    var item = derivation.Element.Attribute("itemType")?.Value is { } itemName
                        ? this.TypeNamed(derivation, itemName, definition, CycleReport.Circular)
                        : FirstChild(derivation, "simpleType") is { } inlineItem ? this.CompileDefinition(inlineItem) : null;
                    if (item is SimpleXsdType simpleItem)
                    {
                        if (Forbids(simpleItem.Final, "list"))
                            throw SimulatedSqlException.XmlSchemaDerivationFinal(type.Name);
                        if (simpleItem.Variety == SimpleVariety.List || simpleItem.IsId)
                            throw SimulatedSqlException.XmlSchemaInvalidListItemType(type.Name);
                        type.ItemType = simpleItem;
                        type.IsIdReference = simpleItem.IsIdReference;
                    }
                    else
                    {
                        type.Unknown = true;
                    }
                    return type;
                case "restriction":
                    var restrictionBase = derivation.Element.Attribute("base")?.Value is { } baseName
                        ? this.TypeNamed(derivation, baseName, definition, CycleReport.Circular)
                        : FirstChild(derivation, "simpleType") is { } inlineBase ? this.CompileDefinition(inlineBase) : null;
                    CompileRestriction(type, derivation, restrictionBase as SimpleXsdType);
                    return type;
                case "union":
                    type.Variety = SimpleVariety.Union;
                    foreach (var memberName in (derivation.Element.Attribute("memberTypes")?.Value ?? string.Empty).Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                        AddMember(type, this.TypeNamed(derivation, memberName, definition, CycleReport.Recursive));
                    foreach (var inline in derivation.Children)
                    {
                        if (inline.IsXsd && inline.Local == "simpleType")
                            AddMember(type, this.CompileDefinition(inline));
                    }
                    return type;
            }
        }
        type.Unknown = true;
        return type;
    }

    private static void AddMember(SimpleXsdType union, XsdType? member)
    {
        if (member is ComplexXsdType complex)
            throw SimulatedSqlException.XmlSchemaUnionMemberNotSimple(complex.Name, union.Name);
        if (member is not SimpleXsdType simple)
        {
            union.Unknown = true;
            return;
        }
        if (Forbids(simple.Final, "union"))
            throw SimulatedSqlException.XmlSchemaDerivationFinal(union.Name);
        union.Members.Add(simple);
    }

    private static XsdNode? FirstChild(XsdNode node, string local) =>
        node.Children.Find(child => child.IsXsd && child.Local == local);

    /// <summary>
    /// A restriction's own facets against its base, in real's order (probed
    /// 2026-10-07 and 2026-10-16 against SQL Server 2025): the base's
    /// <c>final</c> (Msg 6943) and a union base (Msg 6910); then facets that
    /// contradict each other across the type and its bases (Msg 6950, 6946,
    /// 6949, 6951, 6952); a bound or <c>whiteSpace</c> its primitive doesn't
    /// read (Msg 6945, a state per facet); both bounds of one side
    /// (Msg 6996); an enumeration value or bound the base type doesn't admit
    /// (Msg 6926, located at the type); and last, each facet in written order
    /// that loosens its base's (Msg 6944) or changes a fixed one (Msg 6956).
    /// </summary>
    private static void CompileRestriction(SimpleXsdType type, XsdNode restriction, SimpleXsdType? restrictionBase)
    {
        if (restrictionBase is null || restrictionBase.Unknown)
        {
            type.Unknown = true;
            return;
        }
        if (Forbids(restrictionBase.Final, "restriction"))
            throw SimulatedSqlException.XmlSchemaDerivationFinal(type.Name);
        if (restrictionBase.Variety == SimpleVariety.Union)
            throw SimulatedSqlException.XmlSchemaUnionRestriction(type.Name);

        type.Base = restrictionBase;
        type.BuiltIn = restrictionBase.BuiltIn;
        type.Variety = restrictionBase.Variety;
        type.ItemType = restrictionBase.ItemType;
        type.IsId = restrictionBase.IsId;
        type.IsIdReference = restrictionBase.IsIdReference;
        type.MinimumItems = restrictionBase.MinimumItems;
        type.IsQName = restrictionBase.IsQName;
        type.WhiteSpace = restrictionBase.WhiteSpace;
        type.IsStringFamily = restrictionBase.IsStringFamily;
        type.IntegerFamily = restrictionBase.IntegerFamily;
        foreach (var child in restriction.Children)
        {
            if (child.IsXsd && IsFacet(child.Local))
                type.Facets.Add(new OwnFacet(child.Local, child.Element.Attribute("value")?.Value ?? string.Empty, child.Element.Attribute("fixed")?.Value.Trim() is "1" or "true"));
        }

        RejectContradictions(type);
        if (type.Variety == SimpleVariety.Atomic && PrimitiveOf(type.BuiltIn)?.TypeCode == XmlTypeCode.Decimal)
        {
            // Real stores a decimal in 38 digits, 10 of them fractional
            // (probed 2026-10-16 against SQL Server 2025).
            var hasTotal = Count(Effective(type, "totalDigits"), out var total);
            var hasFraction = Count(Effective(type, "fractionDigits"), out var fraction);
            if (hasTotal && total > 38)
                throw SimulatedSqlException.XmlSchemaDigitsOutOfRange(type.Name, 1);
            if (hasFraction && fraction > 10)
                throw SimulatedSqlException.XmlSchemaDigitsOutOfRange(type.Name, 2);
            if (hasTotal && hasFraction && total - fraction > 28)
                throw SimulatedSqlException.XmlSchemaDigitsOutOfRange(type.Name, 3);
        }

        var primitive = type.Variety == SimpleVariety.Atomic ? PrimitiveOf(type.BuiltIn) : null;
        foreach (var facet in type.Facets)
        {
            switch (facet.Name)
            {
                case "maxExclusive" or "maxInclusive" or "minExclusive" or "minInclusive" when primitive?.Datatype is { } datatype && !TryParse(datatype, facet.Value, type.WhiteSpace, out _):
                    throw SimulatedSqlException.XmlSchemaFacetValueInvalid(facet.Name, type.Name, BoundState(facet.Name));
                case "whiteSpace" when !type.IsStringFamily && type.Variety == SimpleVariety.Atomic && facet.Value.Trim() != "collapse":
                    throw SimulatedSqlException.XmlSchemaFacetValueInvalid(facet.Name, type.Name, 13);
            }
        }

        if ((Own(type, "minInclusive") is not null && Own(type, "minExclusive") is not null) || (Own(type, "maxInclusive") is not null && Own(type, "maxExclusive") is not null))
            throw SimulatedSqlException.XmlSchemaBothBoundKinds(type.Name);

        foreach (var facet in type.Facets)
        {
            if (facet.Name is not ("enumeration" or "maxExclusive" or "maxInclusive" or "minExclusive" or "minInclusive"))
                continue;
            if (!IsValid(restrictionBase, facet.Value, out var parsed))
                throw SimulatedSqlException.XmlValidationInvalidSimpleTypeValue(facet.Value, type.Location);
            facet.Parsed = parsed;
        }

        foreach (var facet in type.Facets)
            RejectLoosening(type, restrictionBase, facet);

        if (Own(type, "whiteSpace") is { } whiteSpace)
        {
            type.WhiteSpace = whiteSpace.Value.Trim() switch
            {
                "preserve" => 0,
                "replace" => 1,
                _ => 2,
            };
        }
        foreach (var facet in type.Facets)
        {
            if (facet.Name == "pattern")
                facet.Pattern = XsdPattern(facet.Value, out _);
        }
    }

    private static OwnFacet? Own(SimpleXsdType type, string name) => type.Facets.Find(facet => facet.Name == name);

    /// <summary>The most derived occurrence of <paramref name="name"/> along <paramref name="type"/>'s restriction chain.</summary>
    private static OwnFacet? Effective(SimpleXsdType? type, string name)
    {
        for (var step = type; step is not null; step = step.Base)
        {
            if (Own(step, name) is { } facet)
                return facet;
        }
        return null;
    }

    private static bool Count(OwnFacet? facet, out ulong value)
    {
        value = 0;
        return facet is not null && ulong.TryParse(facet.Value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Msg 6950, 6946, 6949, 6951 and 6952 over the facets the type and its bases give it.</summary>
    private static void RejectContradictions(SimpleXsdType type)
    {
        if (Count(Effective(type, "fractionDigits"), out var fractionDigits) && Count(Effective(type, "totalDigits"), out var totalDigits) && fractionDigits > totalDigits)
            throw SimulatedSqlException.XmlSchemaFacetsContradict(type.Name, 6950);
        if (Count(Effective(type, "minLength"), out var minLength) && Count(Effective(type, "maxLength"), out var maxLength) && minLength > maxLength)
            throw SimulatedSqlException.XmlSchemaFacetsContradict(type.Name, 6946);
        if (Count(Effective(type, "length"), out var length) && Count(Effective(type, "maxLength"), out var lengthCap) && length > lengthCap)
            throw SimulatedSqlException.XmlSchemaLengthOverMaxLength(type.Name);
        if (type.Variety != SimpleVariety.Atomic || PrimitiveOf(type.BuiltIn)?.Datatype is not { } datatype)
            return;
        object? Bound(string name) => Effective(type, name) is { } facet && TryParse(datatype, facet.Value, type.WhiteSpace, out var value) ? value : null;
        var minInclusive = Bound("minInclusive");
        var minExclusive = Bound("minExclusive");
        var maxInclusive = Bound("maxInclusive");
        var maxExclusive = Bound("maxExclusive");
        static bool Above(object? lower, object? upper, bool orEqual) =>
            CompareValues(lower, upper) is var order and not int.MaxValue && (order > 0 || (orEqual && order == 0));
        if (Above(minInclusive, maxInclusive, orEqual: false) || Above(minInclusive, maxExclusive, orEqual: true))
            throw SimulatedSqlException.XmlSchemaFacetsContradict(type.Name, 6951);
        if (Above(minExclusive, maxExclusive, orEqual: false) || Above(minExclusive, maxInclusive, orEqual: true))
            throw SimulatedSqlException.XmlSchemaFacetsContradict(type.Name, 6952);
    }

    /// <summary>Msg 6944 for a facet loosening its base's, else Msg 6956 for one changing a value the base fixes.</summary>
    private static void RejectLoosening(SimpleXsdType type, SimpleXsdType restrictionBase, OwnFacet facet)
    {
        var inherited = Effective(restrictionBase, facet.Name);
        var loosens = facet.Name switch
        {
            "fractionDigits" => Count(facet, out var digits) && (Count(inherited, out var baseDigits) ? digits > baseDigits : restrictionBase.IntegerFamily && digits > 0),
            "length" => inherited is not null && Count(facet, out var length) && Count(inherited, out var baseLength) && length != baseLength,
            "maxLength" => Count(facet, out var maxLength) && Count(inherited, out var baseMaxLength) && maxLength > baseMaxLength,
            "minLength" => Count(facet, out var minLength) && Count(inherited, out var baseMinLength) && minLength < baseMinLength,
            "totalDigits" => Count(facet, out var total) && Count(inherited, out var baseTotal) && total > baseTotal,
            "whiteSpace" => WhiteSpaceRank(facet.Value) < restrictionBase.WhiteSpace,
            _ => false,
        };
        if (loosens)
        {
            byte state = facet.Name switch
            {
                "fractionDigits" => 5,
                "length" => 1,
                "maxLength" => 2,
                "minLength" => 3,
                "totalDigits" => 4,
                _ => 7,
            };
            throw SimulatedSqlException.XmlSchemaFacetNotRestricting(type.Name, facet.Name, state);
        }
        if (inherited is { Fixed: true } && facet.Name is not ("enumeration" or "pattern") && !SameFacetValue(facet, inherited))
            throw SimulatedSqlException.XmlSchemaFixedFacetRedefined(type.Name, facet.Name, FixedState(facet.Name));
    }

    private static bool SameFacetValue(OwnFacet facet, OwnFacet inherited) =>
        facet.Parsed is not null && inherited.Parsed is not null
            ? CompareValues(facet.Parsed, inherited.Parsed) == 0
            : string.Equals(facet.Value.Trim(), inherited.Value.Trim(), StringComparison.Ordinal);

    private static int WhiteSpaceRank(string value) => value.Trim() switch
    {
        "preserve" => 0,
        "replace" => 1,
        _ => 2,
    };

    private static byte BoundState(string facet) => facet switch
    {
        "maxExclusive" => 11,
        "maxInclusive" => 9,
        "minExclusive" => 12,
        _ => 10,
    };

    private static byte FixedState(string facet) => facet switch
    {
        "fractionDigits" => 16,
        "length" => 11,
        "maxLength" => 13,
        "minLength" => 12,
        "totalDigits" => 15,
        "whiteSpace" => 14,
        _ => 10,
    };

    /// <summary>The primitive a built-in type derives from — <c>xs:decimal</c> for <c>xs:int</c> — whose lexical space a bound must be in.</summary>
    private static XmlSchemaSimpleType? PrimitiveOf(XmlSchemaSimpleType? builtIn)
    {
        var current = builtIn;
        while (current?.BaseXmlSchemaType is XmlSchemaSimpleType { TypeCode: not XmlTypeCode.AnyAtomicType and not XmlTypeCode.Item } next && next.QualifiedName.Name != "anySimpleType")
            current = next;
        return current;
    }

    // ---- complex types --------------------------------------------------------

    /// <summary>
    /// A complex type, in real's order (probed 2026-10-16 against SQL Server
    /// 2025): its base (a loop is Msg 2366 through simple content, Msg 6953
    /// through complex content), the base's <c>final</c> (Msg 6943), a simple
    /// content's base (Msg 6942 / 6968) and a <c>mixed</c> differing from the
    /// base's (Msg 6967); then its own particles and attributes, each
    /// declaration compiled in written order; then complex content over a base
    /// without it (Msg 6941), the attribute rules, and the content model's.
    /// </summary>
    private ComplexXsdType CompileComplexType(XsdNode definition)
    {
        var type = new ComplexXsdType(TypeNameOf(definition), PathOf(definition), definition) { Final = FinalOf(definition) };
        this.partialTypes[definition] = type;
        var mixed = IsTrue(definition.Element.Attribute("mixed")?.Value);
        var content = definition.Children.Find(static child => child.IsXsd && child.Local is "complexContent" or "simpleContent");
        var derivation = content?.Children.Find(static child => child.IsXsd && child.Local is "extension" or "restriction");
        var isExtension = derivation?.Local == "extension";
        var holder = derivation ?? definition;
        XsdType? baseType = ComplexXsdType.AnyType;
        if (content is not null)
        {
            if (derivation?.Element.Attribute("base")?.Value is not { } baseName)
                return type;
            baseType = this.TypeNamed(derivation, baseName, definition, content.Local == "simpleContent" ? CycleReport.Circular : CycleReport.Recursive);
            if (content.Local == "complexContent")
                mixed = IsTrue(content.Element.Attribute("mixed")?.Value) || mixed;
        }
        type.Base = baseType;
        type.IsExtension = isExtension;
        type.Mixed = mixed;
        var method = isExtension ? "extension" : "restriction";

        if (baseType is not null && content is not null && Forbids(baseType.Final, method))
            throw SimulatedSqlException.XmlSchemaDerivationFinal(type.Name);

        if (content?.Local == "simpleContent")
        {
            switch (baseType)
            {
                case ComplexXsdType { IsAnyType: false, ContentType: null } when !isExtension || baseType is ComplexXsdType { Particle: not null }:
                    throw SimulatedSqlException.XmlSchemaContentKindMismatch(type.Name, complexContent: false);
                case SimpleXsdType when !isExtension:
                    throw SimulatedSqlException.XmlSchemaComplexRestrictsSimple(type.Name);
                case SimpleXsdType { IsId: true } or SimpleXsdType { IsIdReference: true }:
                    throw SimulatedSqlException.XmlSchemaIdTypeNotSupported(type.Name, 2);
                case SimpleXsdType simpleBase:
                    type.ContentType = simpleBase;
                    break;
                case ComplexXsdType { ContentType: { } baseContent } when isExtension:
                    type.ContentType = baseContent;
                    break;
                case ComplexXsdType { ContentType: { } baseContent }:
                    var restricted = new SimpleXsdType(type.Name, type.Location, definition);
                    CompileRestriction(restricted, derivation!, baseContent);
                    type.ContentType = restricted;
                    break;
            }
        }
        else if (content?.Local == "complexContent" && baseType is ComplexXsdType { IsAnyType: false } complexBase
            && complexBase.Particle is not null && (isExtension ? complexBase.Mixed != mixed : mixed && !complexBase.Mixed))
        {
            throw SimulatedSqlException.XmlSchemaMixedMismatch(type.Name);
        }

        var ownParticle = content?.Local == "simpleContent" ? null : this.ParticleOfContent(holder);
        var ownAttributes = new List<AttributeUse>();
        this.CollectAttributeUses(holder, ownAttributes, definition);

        if (content?.Local == "complexContent" && baseType is SimpleXsdType or ComplexXsdType { ContentType: not null })
            throw SimulatedSqlException.XmlSchemaContentKindMismatch(type.Name, complexContent: true);

        var baseComplex = content is null ? null : baseType as ComplexXsdType;
        type.Particle = baseComplex is { IsAnyType: false } && isExtension
            ? baseComplex.Particle is null ? ownParticle
                : ownParticle is null ? baseComplex.Particle
                : new Particle(ParticleKind.Sequence, 1, 1) { Children = { baseComplex.Particle, ownParticle } }
            : ownParticle;
        var ownWildcard = holder.Children.Find(static child => child.IsXsd && child.Local == "anyAttribute") is { } anyAttribute
            ? this.BuildParticle(anyAttribute)
            : null;
        type.AttributeWildcard = isExtension && baseComplex is { AttributeWildcard: { } baseWildcard }
            ? ownWildcard is null || baseWildcard.Wildcard is null ? baseWildcard : ownWildcard
            : ownWildcard;
        if (content?.Local == "complexContent" && !isExtension && baseComplex is { IsAnyType: false } && ownWildcard is not null
            && (baseComplex.AttributeWildcard is not { } baseAttributeWildcard || !WildcardSubset(ownWildcard, baseAttributeWildcard)))
        {
            throw SimulatedSqlException.XmlSchemaInvalidRestriction(type.Name, 6994);
        }

        RejectAttributeUses(type, baseComplex, isExtension, ownAttributes);
        RejectContentModel(type, type.Particle);
        if (content?.Local == "complexContent" && !isExtension && baseComplex is { IsAnyType: false })
            RejectParticleRestriction(type, ownParticle, baseComplex.Particle);
        return type;
    }

    private static bool IsTrue(string? value) => value?.Trim() is "1" or "true";

    /// <summary>
    /// The attribute rules once every declaration is compiled: an extension
    /// redeclaring a base attribute (Msg 6976) or prohibiting one the base
    /// lacks (Msg 6920), a restriction's attribute of a type not derived from
    /// the base's (Msg 6972), optional where the base's is required
    /// (Msg 6973) or neither declared nor admitted by the base (Msg 6971), a
    /// prohibition on a type deriving from nothing (Msg 6920), and two
    /// attributes of ID types (Msg 6970).
    /// </summary>
    private static void RejectAttributeUses(ComplexXsdType type, ComplexXsdType? baseComplex, bool isExtension, List<AttributeUse> own)
    {
        // A name an attribute group brings a second time (probed 2026-10-16
        // against SQL Server 2025).
        var names = new HashSet<XmlQualifiedName>();
        foreach (var use in own)
        {
            if (!names.Add(use.Name))
                throw SimulatedSqlException.XmlSchemaAttributeDeclaredTwice(use.Name.Name);
        }
        var inherited = baseComplex is { IsAnyType: false } ? baseComplex.Attributes : [];
        foreach (var use in own)
        {
            var matching = inherited.Find(candidate => candidate.Name == use.Name);
            if (isExtension)
            {
                if (matching is not null)
                    throw SimulatedSqlException.XmlSchemaAttributeRedefinition(use.Name.Name, type.Name, 6976);
                if (use.Prohibited)
                    throw SimulatedSqlException.XmlSchemaProhibitedAttributeNotInBase(type.Name, use.Name.Name);
                continue;
            }
            if (baseComplex is null or { IsAnyType: true })
            {
                if (use.Prohibited)
                    throw SimulatedSqlException.XmlSchemaProhibitedAttributeNotInBase(type.Name, use.Name.Name);
                continue;
            }
            if (matching is null)
            {
                if (baseComplex.AttributeWildcard is not { } baseWildcard || !Admits(baseWildcard, use.Name.Namespace))
                    throw SimulatedSqlException.XmlSchemaAttributeNotInBase(type.Name, use.Name.Name);
                continue;
            }
            // A prohibition is judged as a redeclaration would be: of a type
            // deriving from the base's, required where it is, fixed to its
            // value (probed 2026-10-20 against SQL Server 2025).
            if (use.Type is not null && matching.Type is not null && !IsDerived(use.Type, matching.Type))
                throw SimulatedSqlException.XmlSchemaAttributeRedefinition(use.Name.Name, type.Name, 6972);
            if (matching.Required && !use.Required)
                throw SimulatedSqlException.XmlSchemaAttributeRedefinition(use.Name.Name, type.Name, 6973);
            if (matching.Fixed is not null && !SameValue(use.Type ?? matching.Type, use.Fixed, matching.Fixed))
                throw SimulatedSqlException.XmlSchemaAttributeRedefinition(use.Name.Name, type.Name, 6975);
        }

        var effective = new List<AttributeUse>();
        foreach (var use in inherited)
        {
            if (!own.Exists(candidate => candidate.Name == use.Name))
                effective.Add(use);
        }
        foreach (var use in own)
        {
            if (!use.Prohibited)
                effective.Add(use);
        }
        if (effective.Count(static use => use.Type is SimpleXsdType { IsId: true }) > 1)
        {
            // State 2 when an extension adds one to its base's (probed
            // 2026-10-16 against SQL Server 2025).
            var inheritedId = isExtension && inherited.Exists(static use => use.Type is SimpleXsdType { IsId: true });
            throw SimulatedSqlException.XmlSchemaTwoIdAttributes(type.Name, inheritedId ? (byte)2 : (byte)1);
        }
        type.Attributes = effective;
    }

    /// <summary>
    /// The attribute declarations of a complex type, a derivation or an
    /// attribute group, each compiled in written order and an attribute
    /// group's taken in its place.
    /// </summary>
    private void CollectAttributeUses(XsdNode holder, List<AttributeUse> into, XsdNode owner)
    {
        foreach (var child in holder.Children)
        {
            if (!child.IsXsd)
                continue;
            switch (child.Local)
            {
                case "attribute":
                    var attributeType = this.CompileAttribute(child);
                    var declaration = child.Element.Attribute("ref")?.Value is { } reference
                        && this.globals.TryGetValue(("attribute", ResolveQualifiedName(child, reference.Trim()).Namespace, LocalPart(reference.Trim())!), out var global)
                        ? global
                        : child;
                    var ns = ReferenceEquals(declaration, child) && !IsQualified(child, "attributeFormDefault") ? string.Empty : declaration.TargetNamespace;
                    var use = child.Element.Attribute("use")?.Value.Trim();
                    var fixedValue = child.Element.Attribute("fixed")?.Value ?? declaration.Element.Attribute("fixed")?.Value;
                    into.Add(new AttributeUse(new XmlQualifiedName(DeclaredName(child), ns), attributeType, use == "required", use == "prohibited", fixedValue));
                    break;
                case "attributeGroup" when child.Element.Attribute("ref")?.Value is { } groupName:
                    var name = ResolveQualifiedName(child, groupName.Trim());
                    if (!this.globals.TryGetValue(("attributeGroup", name.Namespace, name.Name), out var group))
                        break;
                    if (this.compilingGroups.Contains(group))
                    {
                        if (owner.Local == "attributeGroup")
                            throw SimulatedSqlException.XmlSchemaRecursiveGroup(DeclaredName(owner));
                        break;
                    }
                    this.CompileAttributeGroup(group);
                    var groupUses = new List<AttributeUse>();
                    _ = Quietly(group, () =>
                    {
                        this.CollectAttributeUses(group, groupUses, group);
                        return groupUses;
                    });
                    into.AddRange(groupUses);
                    break;
            }
        }
    }

    /// <summary>Whether a local declaration is qualified, by its <c>form</c> or its schema's default.</summary>
    private static bool IsQualified(XsdNode declaration, string defaultAttribute)
    {
        if (declaration.Element.Attribute("form")?.Value.Trim() is { } form)
            return form == "qualified";
        var root = declaration;
        while (root.Parent is { } parent)
            root = parent;
        return root.Element.Attribute(defaultAttribute)?.Value.Trim() == "qualified";
    }

    // ---- declarations ---------------------------------------------------------

    /// <summary>
    /// An element declaration: a global's substitution head first, then its
    /// type, then a member whose type doesn't derive from the head's
    /// (Msg 6957), then its value constraint, then a simple type derived from
    /// <c>ID</c> or <c>IDREF</c> (Msg 6995 state 1). A reference compiles the
    /// global it names. Answers the element's type.
    /// </summary>
    private XsdType? CompileElement(XsdNode node)
    {
        if (this.declarationTypes.TryGetValue(node, out var known))
            return known;
        this.declarationTypes[node] = null;
        _ = this.compilingElements.Add(node);
        XsdType? type;
        try
        {
            type = Quietly(node, () => this.CompileElementCore(node));
        }
        finally
        {
            _ = this.compilingElements.Remove(node);
        }
        this.declarationTypes[node] = type;
        return type;
    }

    private XsdType? CompileElementCore(XsdNode node)
    {
        if (node.Element.Attribute("ref")?.Value is { } reference)
        {
            var name = ResolveQualifiedName(node, reference.Trim());
            return this.globals.TryGetValue(("element", name.Namespace, name.Name), out var global) ? this.CompileElement(global) : null;
        }

        XsdNode? head = null;
        XsdType? headType = null;
        if (node.Element.Attribute("substitutionGroup")?.Value is { } headName)
        {
            var name = ResolveQualifiedName(node, headName.Trim());
            if (this.globals.TryGetValue(("element", name.Namespace, name.Name), out head))
            {
                if (this.compilingElements.Contains(head))
                    throw SimulatedSqlException.XmlSchemaCircularDefinition(DeclaredName(head));
                headType = this.CompileElement(head);
            }
        }

        var type = node.Element.Attribute("type")?.Value is { } typeName
            ? this.TypeNamed(node, typeName, null, CycleReport.None)
            : node.Children.Find(static child => child.IsXsd && child.Local is "complexType" or "simpleType") is { } inline
                ? this.CompileDefinition(inline)
                : head is not null ? headType : ComplexXsdType.AnyType;

        if (head is not null && type is not null && headType is not null
            && (!IsDerived(type, headType) || Array.Exists(DerivationMethods(type, headType), method => Forbids(FinalOf(head), method))))
        {
            throw SimulatedSqlException.XmlSchemaSubstitutionNotDerived(DeclaredName(node), DeclaredName(head));
        }

        if ((node.Element.Attribute("default") ?? node.Element.Attribute("fixed"))?.Value is { } value)
            RejectValueConstraint(node, type, value);
        if (type is SimpleXsdType { IsId: true } or SimpleXsdType { IsIdReference: true })
            throw SimulatedSqlException.XmlSchemaIdTypeNotSupported(DeclaredName(node), 1);
        return type;
    }

    /// <summary>An attribute declaration: its type, then its value constraint. A reference compiles the global it names and judges its own value against that type.</summary>
    private XsdType? CompileAttribute(XsdNode node)
    {
        if (this.declarationTypes.TryGetValue(node, out var known))
            return known;
        this.declarationTypes[node] = null;
        var type = Quietly(node, () =>
        {
            XsdType? type;
            if (node.Element.Attribute("ref")?.Value is { } reference)
            {
                var name = ResolveQualifiedName(node, reference.Trim());
                type = name.Namespace == XmlNamespace ? this.XmlAttributeType(name.Name)
                    : this.globals.TryGetValue(("attribute", name.Namespace, name.Name), out var global) ? this.CompileAttribute(global) : null;
            }
            else
            {
                type = node.Element.Attribute("type")?.Value is { } typeName
                    ? this.TypeNamed(node, typeName, null, CycleReport.None)
                    : FirstChild(node, "simpleType") is { } inline ? this.CompileDefinition(inline) : this.BuiltIn("anySimpleType");
            }
            if (type is ComplexXsdType)
                throw SimulatedSqlException.XmlSchemaAttributeTypeNotSimple(DeclaredName(node));
            if ((node.Element.Attribute("default") ?? node.Element.Attribute("fixed"))?.Value is { } value)
                RejectValueConstraint(node, type, value);
            return type;
        });
        this.declarationTypes[node] = type;
        return type;
    }

    /// <summary>
    /// The type of an attribute of the <c>xml:</c> namespace a document
    /// imports: <c>lang</c> a language, <c>space</c> one of <c>default</c> and
    /// <c>preserve</c>, <c>base</c> a URI.
    /// </summary>
    private SimpleXsdType? XmlAttributeType(string name)
    {
        switch (name)
        {
            case "base":
                return this.BuiltIn("anyURI");
            case "lang":
                return this.BuiltIn("language");
            case "space":
                var ncName = this.BuiltIn("NCName")!;
                var space = new SimpleXsdType("xml:space", "xml:space", null) { Base = ncName, BuiltIn = ncName.BuiltIn, IsStringFamily = true, Synthetic = true };
                foreach (var value in (string[])["default", "preserve"])
                    space.Facets.Add(new OwnFacet("enumeration", value, isFixed: false) { Parsed = value });
                return space;
            default:
                return null;
        }
    }

    /// <summary>
    /// The derivation methods <paramref name="type"/>'s chain takes to reach
    /// <paramref name="ancestor"/>, which a substitution group head's
    /// <c>final</c> may forbid.
    /// </summary>
    private static string[] DerivationMethods(XsdType type, XsdType ancestor)
    {
        if (ReferenceEquals(type, ancestor))
            return [];
        var methods = new List<string>();
        for (var step = (XsdType?)type; step is not null && !ReferenceEquals(step, ancestor);)
        {
            switch (step)
            {
                case ComplexXsdType complex:
                    methods.Add(complex.IsExtension ? "extension" : "restriction");
                    step = complex.Base;
                    break;
                default:
                    methods.Add("restriction");
                    step = null;
                    break;
            }
        }
        return [.. methods];
    }

    /// <summary>Whether two written values of one simple type are one value, as fixed values must be.</summary>
    private static bool SameValue(XsdType? type, string? left, string? right)
    {
        if (left is null || right is null)
            return left == right;
        if (type is SimpleXsdType simple && IsValid(simple, left, out var leftValue) && IsValid(simple, right, out var rightValue) && leftValue is not null)
            return CompareValues(leftValue, rightValue) == 0;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// A default or fixed value: none on an ID type (Msg 6932) and none on a
    /// QName one (Msg 6999), one the type admits (Msg 6926, located at the
    /// declaration), and on an element only where the type has simple or
    /// emptiable mixed content (Msg 6962).
    /// </summary>
    private static void RejectValueConstraint(XsdNode declaration, XsdType? type, string value)
    {
        var simple = type switch
        {
            SimpleXsdType simpleType => simpleType,
            ComplexXsdType { IsAnyType: true } => null,
            ComplexXsdType { ContentType: { } contentType } => contentType,
            ComplexXsdType complex when !complex.Mixed || !Emptiable(complex.Particle) =>
                throw SimulatedSqlException.XmlSchemaValueConstraintOnComplexType(complex.Name),
            _ => null,
        };
        if (simple is null)
            return;
        if (simple.IsId)
            throw SimulatedSqlException.XmlSchemaIdValueConstraint(DeclaredName(declaration));
        if (simple.IsQName)
            throw SimulatedSqlException.XmlSchemaQNameValueConstraint(DeclaredName(declaration));
        if (!IsValid(simple, value, out _))
            throw SimulatedSqlException.XmlValidationInvalidSimpleTypeValue(value, PathOf(declaration));
    }

    /// <summary>A group's particle, its declarations compiled once; a group reaching itself through its references is Msg 6954, named after the group whose reference closes the loop.</summary>
    private Particle? CompileGroup(XsdNode group)
    {
        if (this.compiledGroups.TryGetValue(group, out var particle))
            return particle;
        _ = this.compilingGroups.Add(group);
        try
        {
            particle = Quietly(group, () =>
            {
                var content = this.ParticleOfContent(group);
                if (content is not null && InvalidAllGroup(content) is { } state)
                    throw SimulatedSqlException.XmlSchemaInvalidAllGroup(TypeNameOf(group), state);
                return content;
            });
        }
        finally
        {
            _ = this.compilingGroups.Remove(group);
        }
        this.compiledGroups[group] = particle;
        return particle;
    }

    private void CompileAttributeGroup(XsdNode group)
    {
        if (this.compiledGroups.ContainsKey(group))
            return;
        _ = this.compilingGroups.Add(group);
        try
        {
            _ = Quietly(group, () =>
            {
                var uses = new List<AttributeUse>();
                this.CollectAttributeUses(group, uses, group);
                return uses;
            });
        }
        finally
        {
            _ = this.compilingGroups.Remove(group);
        }
        this.compiledGroups[group] = null;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is <paramref name="ancestor"/> or
    /// derives from it. A pair this compiler can't follow answers yes, so a
    /// gap here never refuses a text.
    /// </summary>
    private static bool IsDerived(XsdType type, XsdType ancestor)
    {
        if (ReferenceEquals(type, ancestor) || ancestor is ComplexXsdType { IsAnyType: true })
            return true;
        switch (type, ancestor)
        {
            case (ComplexXsdType complex, _):
                for (var step = complex.Base; ;)
                {
                    switch (step)
                    {
                        case null:
                            return true;
                        case var _ when ReferenceEquals(step, ancestor):
                            return true;
                        case ComplexXsdType { IsAnyType: true }:
                            return false;
                        case ComplexXsdType stepComplex:
                            step = stepComplex.Base;
                            continue;
                        case SimpleXsdType stepSimple:
                            return ancestor is SimpleXsdType && IsDerived(stepSimple, ancestor);
                    }
                }
            case (SimpleXsdType simple, SimpleXsdType simpleAncestor):
                if (simple.Unknown || simpleAncestor.Unknown)
                    return true;
                for (var step = simple.Base; step is not null; step = step.Base)
                {
                    if (ReferenceEquals(step, ancestor))
                        return true;
                }
                if (simpleAncestor.Node is not null)
                    return false;
                if (simpleAncestor.BuiltIn is null || simpleAncestor.BuiltIn.QualifiedName.Name == "anySimpleType" || simple.Variety != SimpleVariety.Atomic || simple.BuiltIn is null)
                    return true;
                return ReferenceEquals(simple.BuiltIn, simpleAncestor.BuiltIn) || XmlSchemaType.IsDerivedFrom(simple.BuiltIn, simpleAncestor.BuiltIn, XmlSchemaDerivationMethod.Empty);
            default:
                return false;
        }
    }
}

/// <summary>A type the third pass compiled: a simple one, or a complex one.</summary>
internal abstract class XsdType(string name, string location, XsdNode? node)
{
    /// <summary>The type's name in real's messages.</summary>
    public readonly string Name = name;

    /// <summary>The type's path, where a Msg 6926 over one of its facet values is located.</summary>
    public readonly string Location = location;

    /// <summary>The declaring definition, null for a built-in.</summary>
    public readonly XsdNode? Node = node;

    /// <summary>The <c>final</c> derivations, as written.</summary>
    public string Final = string.Empty;
}

internal enum SimpleVariety
{
    Atomic,
    List,
    Union,
}

/// <summary>A simple type: its variety, the built-in it restricts, and each derivation step's own facets.</summary>
internal sealed class SimpleXsdType(string name, string location, XsdNode? node) : XsdType(name, location, node)
{
    public SimpleVariety Variety;

    /// <summary>The restriction step this one derives from, null for a built-in, a list or a union.</summary>
    public SimpleXsdType? Base;

    /// <summary>The built-in an atomic type is or restricts, whose parse applies its own value space.</summary>
    public XmlSchemaSimpleType? BuiltIn;

    public SimpleXsdType? ItemType;
    public readonly List<SimpleXsdType> Members = [];
    public readonly List<OwnFacet> Facets = [];

    /// <summary>0 preserve, 1 replace, 2 collapse.</summary>
    public int WhiteSpace = 2;

    public bool IsId;

    /// <summary>Whether the type is or derives from <c>IDREF</c>, or lists it.</summary>
    public bool IsIdReference;

    public bool IsQName;
    public bool IsStringFamily;

    /// <summary>Whether the type is or restricts <c>xs:integer</c>, whose <c>fractionDigits</c> is 0.</summary>
    public bool IntegerFamily;

    /// <summary>Whether the type is one this compiler made up rather than read, its facets already parsed.</summary>
    public bool Synthetic;

    /// <summary>Whether the type is or restricts a built-in list, which takes at least one item.</summary>
    public bool MinimumItems;

    /// <summary>Whether something the type rests on couldn't be read, which leaves its values unjudged.</summary>
    public bool Unknown;
}

/// <summary>One facet of a restriction step: its value as written, parsed once the base admits it.</summary>
internal sealed class OwnFacet(string name, string value, bool isFixed)
{
    public readonly string Name = name;
    public readonly string Value = value;
    public readonly bool Fixed = isFixed;
    public object? Parsed;
    public System.Text.RegularExpressions.Regex? Pattern;
}

/// <summary>A complex type: its base, simple content type or particle, and attribute uses.</summary>
internal sealed class ComplexXsdType(string name, string location, XsdNode? node) : XsdType(name, location, node)
{
    /// <summary><c>xs:anyType</c>, which takes anything.</summary>
    public static readonly ComplexXsdType AnyType = new("xs:anyType", "xs:anyType", null) { IsAnyType = true, Mixed = true, AttributeWildcard = new(ParticleKind.Any, 1, 1) };

    public bool IsAnyType;
    public XsdType? Base;
    public bool IsExtension;
    public bool Mixed;
    public SimpleXsdType? ContentType;
    public Particle? Particle;
    public List<AttributeUse> Attributes = [];

    /// <summary>The <c>anyAttribute</c> the type takes, as a wildcard particle; null for none.</summary>
    public Particle? AttributeWildcard;
}

/// <summary>One attribute a complex type takes.</summary>
internal sealed class AttributeUse(XmlQualifiedName name, XsdType? type, bool required, bool prohibited, string? fixedValue)
{
    public readonly XmlQualifiedName Name = name;
    public readonly XsdType? Type = type;
    public readonly bool Required = required;
    public readonly bool Prohibited = prohibited;

    /// <summary>The value the use or its declaration fixes, as written.</summary>
    public readonly string? Fixed = fixedValue;
}
