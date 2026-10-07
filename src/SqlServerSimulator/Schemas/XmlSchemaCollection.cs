using System.Collections.Frozen;
using System.Xml;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A registered XML schema collection: name + raw XSD source text. Created
/// via <c>CREATE XML SCHEMA COLLECTION schema.name AS '&lt;xsd:schema&gt;…&lt;/xsd:schema&gt;'</c>;
/// stored on <see cref="Schema.XmlSchemaCollections"/> (shares the type
/// namespace with table types and alias types — Msg 219 on duplicate).
/// </summary>
/// <remarks>
/// The source text is stored verbatim — <c>ALTER … ADD</c> appends to it —
/// and everything the simulator reads out of it is derived and cached against
/// that text: the compiled schema set a typed write validates against, and
/// the static typing an XQuery expression over a bound value compiles with.
/// </remarks>
internal sealed class XmlSchemaCollection(
    int id,
    string name,
    int schemaId,
    int? principalId,
    string xsdText,
    DateTime createDate)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public int SchemaId = schemaId;

    /// <summary>
    /// Owning principal id. Probe-confirmed against SQL Server 2025: the
    /// column is nullable and CREATE without AUTHORIZATION leaves it
    /// NULL, which means the owning schema's owner; <c>ALTER AUTHORIZATION</c>
    /// sets it, and <c>TO SCHEMA OWNER</c> puts it back to NULL.
    /// </summary>
    public int? PrincipalId = principalId;

    /// <summary>
    /// Raw XSD source text passed to <c>AS '…'</c>, with each
    /// <c>ALTER … ADD</c>'s documents appended. Every view derived from it is
    /// cached against this reference, so reassigning it re-reads them.
    /// </summary>
    public string XsdText = xsdText;

    public readonly DateTime CreateDate = createDate;

    public DateTime ModifyDate = createDate;

    /// <summary>
    /// <see cref="Simulation.XmlSchemaCollectionAlterations"/> as of this
    /// collection's last <c>ALTER … ADD</c>, 0 before one; see
    /// <see cref="Parser.BatchContext.XmlSchemaAlterationsAtStart"/>.
    /// </summary>
    public long AlteredAt;

    private string? namesReadFrom;
    private FrozenSet<string>? singletonElementNames;
    private string? simpleContentNamesReadFrom;
    private FrozenSet<string>? simpleContentElementNames;
    private string? compiledReadFrom;
    private System.Xml.Schema.XmlSchemaSet? compiledSchemas;
    private string? typingReadFrom;
    private Storage.XmlStaticTyping? staticTyping;

    /// <summary>
    /// What an XQuery expression over a value bound to this collection is
    /// typed by: the singleton element names (see
    /// <see cref="GetSingletonElementNames"/>) plus the declared simple type of
    /// each element and attribute name the compiled schemas type
    /// consistently, which is what makes <c>/r/d</c> over an
    /// <c>xs:decimal</c> element compare, add and report as
    /// <c>xs:decimal</c> rather than <c>xdt:untypedAtomic</c>.
    /// </summary>
    public Storage.XmlStaticTyping GetStaticTyping()
    {
        if (!ReferenceEquals(this.typingReadFrom, this.XsdText))
        {
            this.typingReadFrom = this.XsdText;
            this.staticTyping = Storage.XmlStaticTyping.From(this.GetSingletonElementNames(), this.GetCompiledSchemas());
        }

        return this.staticTyping!;
    }

    /// <summary>
    /// The element names this collection declares at most once wherever they
    /// appear in a content model — the slice of the schema an XQuery path's
    /// <em>static cardinality</em> depends on. A step naming one of these is
    /// a singleton to real's type checker, which is what lets
    /// <c>.value()</c> read <c>(act:telephoneNumber)[1]/act:number</c> off a
    /// typed column where the same path over untyped <c>xml</c> is Msg 2389.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <em>local</em> declarations (an <c>xsd:element</c> inside a
    /// content model) carry an occurrence; a global one — a direct child of
    /// <c>xsd:schema</c> — says nothing, because its cardinality comes from
    /// wherever it is referenced, which for AdventureWorks' contact schema is
    /// an unbounded <c>xsd:any</c> wildcard. So a global-only name stays
    /// plural, matching real: <c>/ci:AdditionalContactInfo/act:telephoneNumber</c>
    /// is a sequence there, and the view that reads it writes the <c>[1]</c>.
    /// </para>
    /// <para>
    /// A name declared plural <em>anywhere</em> in the collection loses its
    /// singleton status everywhere, since the narrowing is keyed on the name
    /// alone rather than on the declaring type — the narrower-than-real
    /// direction, which keeps a path real accepts from being refused without
    /// letting one real refuses through.
    /// </para>
    /// </remarks>
    public FrozenSet<string> GetSingletonElementNames()
    {
        if (!ReferenceEquals(this.namesReadFrom, this.XsdText))
        {
            this.namesReadFrom = this.XsdText;
            this.singletonElementNames = ReadSingletonElementNames(this.XsdText);
        }

        return this.singletonElementNames!;
    }

    /// <summary>
    /// The element names this collection declares with <em>simple typed
    /// content</em> — the slice of the schema <c>replace value of</c> reads.
    /// Real refuses to write an element's value unless the element's type says
    /// the element holds a value, so <c>replace value of (/r/b)[1] with …</c>
    /// is Msg 2356 over untyped <c>xml</c> and legal over an
    /// <c>xml(&lt;collection&gt;)</c> binding that types <c>b</c> as (say)
    /// <c>xsd:decimal</c> — which is what lets AdventureWorks'
    /// <c>Sales.iduSalesOrderDetail</c> trigger write
    /// <c>(/IndividualSurvey/TotalPurchaseYTD)[1]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An element counts as simply typed when its <c>type</c> attribute names a
    /// built-in XSD type or a named <c>xsd:simpleType</c> the same text
    /// declares, or when its own first child is an inline <c>xsd:simpleType</c>.
    /// A declaration this reader can't place that way — an inline
    /// <c>xsd:complexType</c> wrapping <c>xsd:simpleContent</c>, a type from a
    /// schema the collection doesn't carry, an <c>xsd:annotation</c> sitting
    /// between the element and its inline type — stays complex, which keeps
    /// real's Msg 2356 rather than admitting a write real refuses.
    /// </para>
    /// <para>
    /// Keyed on the element name alone, like
    /// <see cref="GetSingletonElementNames"/>: a name declared complex anywhere
    /// in the collection is complex everywhere.
    /// </para>
    /// </remarks>
    public FrozenSet<string> GetSimpleContentElementNames()
    {
        if (!ReferenceEquals(this.simpleContentNamesReadFrom, this.XsdText))
        {
            this.simpleContentNamesReadFrom = this.XsdText;
            this.simpleContentElementNames = ReadSimpleContentElementNames(this.XsdText);
        }

        return this.simpleContentElementNames!;
    }

    /// <summary>
    /// The collection's XSD compiled into a schema set, or null when it won't
    /// compile. Post-compilation the set carries the resolved infoset — every
    /// element's own <c>ElementSchemaType</c>, every complex type's
    /// <c>ContentTypeParticle</c> and <c>AttributeUses</c> — which is what the
    /// typed-write walk reads to validate and canonicalize an instance.
    /// </summary>
    /// <remarks>
    /// A text that doesn't compile answers null and leaves the write untyped
    /// rather than refusing it: the simulator accepted every instance before it
    /// read the XSD at all, so a compiler gap can only cost fidelity, never
    /// reject something real allows. Cached against <see cref="XsdText"/> by
    /// reference like the two name sets, so an <c>ALTER</c> that reassigns the
    /// text recompiles.
    /// </remarks>
    public System.Xml.Schema.XmlSchemaSet? GetCompiledSchemas()
    {
        if (!ReferenceEquals(this.compiledReadFrom, this.XsdText))
        {
            this.compiledReadFrom = this.XsdText;
            this.compiledSchemas = CompileSchemas(this.XsdText);
        }

        return this.compiledSchemas;
    }

    private static System.Xml.Schema.XmlSchemaSet? CompileSchemas(string xsdText)
    {
        try
        {
            var set = new System.Xml.Schema.XmlSchemaSet();
            // One CREATE may carry several schema documents back to back, so
            // the text is read as a fragment and each root added in turn.
            var settings = new System.Xml.XmlReaderSettings { ConformanceLevel = System.Xml.ConformanceLevel.Fragment };
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xsdText), settings);
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element)
                    continue;
                if (System.Xml.Schema.XmlSchema.Read(reader.ReadSubtree(), null) is { } schema)
                    _ = set.Add(schema);
            }

            if (set.Count == 0)
                return null;
            set.Compile();
            return set;
        }
        catch (Exception e) when (e is System.Xml.XmlException or System.Xml.Schema.XmlSchemaException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    /// <summary>
    /// Msg 9336 for the XSD constructs SQL Server's schema collections refuse
    /// outright — the identity constraints <c>unique</c>, <c>key</c> and
    /// <c>keyref</c>, <c>include</c> and <c>notation</c> — and Msg 2391 for
    /// <c>redefine</c>, naming the first one in document order (probed
    /// 2026-09-28 and 2026-10-07 against SQL Server 2025). Text the reader
    /// can't get through is left to whatever reads it next.
    /// </summary>
    public static void RejectUnsupportedSyntax(string xsdText)
    {
        try
        {
            var settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment };
            using var reader = XmlReader.Create(new System.IO.StringReader(xsdText), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != XsdNamespace)
                    continue;
                if (reader.LocalName is "key" or "keyref" or "unique")
                    throw SimulatedSqlException.XmlSchemaSyntaxNotSupported(reader.LocalName);
                // The two below are named as written, prefix and brackets
                // included (probed 2026-10-07 against SQL Server 2025).
                if (reader.LocalName is "include" or "notation")
                    throw SimulatedSqlException.XmlSchemaSyntaxNotSupported($"<{reader.Name}>");
                if (reader.LocalName == "redefine")
                    throw SimulatedSqlException.XmlSchemaRedefineNotSupported();
            }
        }
        catch (XmlException)
        {
            // Not readable as XML: the parse that follows reports it.
        }
    }

    private const string SqlTypesNamespace = "http://schemas.microsoft.com/sqlserver/2004/sqltypes";

    /// <summary>
    /// The compile errors real raises over a schema collection's text, in its
    /// order: a global name declared twice (Msg 2302), then the first
    /// reference to a name nothing defines (Msg 2307 / 2308) in document
    /// order, then a facet whose value isn't a number (Msg 2309) or that its
    /// base type doesn't take (Msg 2319). <paramref name="existing"/> is what
    /// an <c>ALTER … ADD</c> adds to. Real predefines no <c>xml:</c>
    /// attributes and resolves the <c>sqltypes</c> namespace an import names;
    /// everything else .NET's compiler refuses is left accepted and untyped.
    /// </summary>
    public static void RejectUncompilableSchema(string xsdText, System.Xml.Schema.XmlSchemaSet? existing)
    {
        var declared = new HashSet<(string Kind, string Namespace, string Name)>();
        if (existing is not null)
        {
            foreach (XmlQualifiedName name in existing.GlobalElements.Names)
                _ = declared.Add(("element", name.Namespace, name.Name));
            foreach (XmlQualifiedName name in existing.GlobalAttributes.Names)
                _ = declared.Add(("attribute", name.Namespace, name.Name));
            foreach (XmlQualifiedName name in existing.GlobalTypes.Names)
                _ = declared.Add(("type", name.Namespace, name.Name));
            foreach (System.Xml.Schema.XmlSchema schema in existing.Schemas())
            {
                foreach (var item in schema.Items)
                {
                    _ = item switch
                    {
                        System.Xml.Schema.XmlSchemaGroup group => declared.Add(("group", schema.TargetNamespace ?? string.Empty, group.Name!)),
                        System.Xml.Schema.XmlSchemaAttributeGroup group => declared.Add(("attributeGroup", schema.TargetNamespace ?? string.Empty, group.Name!)),
                        _ => false,
                    };
                }
            }
        }

        var references = new List<(string Kind, XmlQualifiedName Name)>();
        var facets = new List<(string Facet, string Value, string Location, int Line, int Position)>();
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xsdText), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            var lineInfo = (IXmlLineInfo)reader;
            var path = new List<string>();
            var siblingCounts = new List<Dictionary<string, int>> { new(StringComparer.Ordinal) };
            var targetNamespace = string.Empty;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    path.RemoveAt(path.Count - 1);
                    siblingCounts.RemoveAt(siblingCounts.Count - 1);
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var counts = siblingCounts[^1];
                counts[reader.LocalName] = counts.GetValueOrDefault(reader.LocalName) + 1;
                var step = $"/*:{reader.LocalName}[{counts[reader.LocalName]}]";
                var depth = reader.Depth;
                if (reader.NamespaceURI == XsdNamespace)
                    InspectXsdElement(reader, depth, ref targetNamespace, declared, references, facets, string.Concat(path) + step, lineInfo);
                if (!reader.IsEmptyElement)
                {
                    path.Add(step);
                    siblingCounts.Add(new(StringComparer.Ordinal));
                }
            }
        }
        catch (XmlException)
        {
            return;
        }

        foreach (var (kind, name) in references)
        {
            // A reference that isn't an XML name at all is Msg 2379 (probed
            // 2026-10-07 against SQL Server 2025).
            if (!IsNcName(name.Name))
                throw SimulatedSqlException.XmlSchemaNameNotValid(name.Name);
            if (name.Namespace == XsdNamespace
                ? kind == "type" && (System.Xml.Schema.XmlSchemaType.GetBuiltInSimpleType(name) is not null || name.Name == "anyType")
                : name.Namespace == SqlTypesNamespace || declared.Contains((kind, name.Namespace, name.Name)))
            {
                continue;
            }
            throw SimulatedSqlException.XmlSchemaUndefinedName(name.Name, name.Namespace);
        }

        if (FirstStructuralError(xsdText) is { } structural)
            throw structural;

        if (facets.Count != 0)
            RejectFacetsTheBaseRefuses(xsdText, facets);

        if (FirstTypeDefinitionError(xsdText) is { } definition)
            throw definition;
    }

    /// <summary>
    /// Msg 2319 for the first facet .NET's compiler finds its base type
    /// doesn't take, located as the walk recorded it.
    /// </summary>
    private static void RejectFacetsTheBaseRefuses(string xsdText, List<(string Facet, string Value, string Location, int Line, int Position)> facets)
    {
        var set = new System.Xml.Schema.XmlSchemaSet();
        var refused = new List<System.Xml.Schema.XmlSchemaObject?>();
        set.ValidationEventHandler += (_, e) => refused.Add(e.Exception.SourceSchemaObject);
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xsdText), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && System.Xml.Schema.XmlSchema.Read(reader.ReadSubtree(), null) is { } schema)
                    _ = set.Add(schema);
            }
            set.Compile();
        }
        catch (Exception e) when (e is XmlException or System.Xml.Schema.XmlSchemaException)
        {
            refused.Add((e as System.Xml.Schema.XmlSchemaException)?.SourceSchemaObject);
        }
        if (refused.OfType<System.Xml.Schema.XmlSchemaFacet>().FirstOrDefault() is not { } refusedFacet)
            return;
        foreach (var (facet, _, location, line, position) in facets)
        {
            if (line == refusedFacet.LineNumber && position == refusedFacet.LinePosition)
                throw SimulatedSqlException.XmlSchemaFacetNotAllowed(facet, location);
        }
    }

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
    /// global and a local <c>element</c> or <c>attribute</c> differ — as real
    /// reads them (probed 2026-10-07 against SQL Server 2025: real takes
    /// <c>abstract</c> and <c>mixed</c> on a <c>simpleType</c> and <c>form</c>
    /// on a global element, beyond the XSD recommendation). An element missing
    /// here goes unchecked.
    /// </summary>
    private static readonly FrozenDictionary<string, FrozenSet<string>> XsdAttributes = new Dictionary<string, string[]>
    {
        ["all"] = ["id", "maxOccurs", "minOccurs"],
        ["annotation"] = ["id"],
        ["any"] = ["id", "maxOccurs", "minOccurs", "namespace", "processContents"],
        ["anyAttribute"] = ["id", "namespace", "processContents"],
        ["appinfo"] = ["source"],
        ["attribute"] = ["default", "fixed", "form", "id", "name", "ref", "type", "use"],
        ["attributeGroup"] = ["id", "ref"],
        ["choice"] = ["id", "maxOccurs", "minOccurs"],
        ["complexContent"] = ["id", "mixed"],
        ["complexType"] = ["abstract", "block", "final", "id", "mixed", "name"],
        ["documentation"] = ["source"],
        ["element"] = ["abstract", "block", "default", "final", "fixed", "form", "id", "maxOccurs", "minOccurs", "name", "nillable", "ref", "substitutionGroup", "type"],
        ["enumeration"] = ["id", "value"],
        ["extension"] = ["base", "id"],
        ["fractionDigits"] = ["fixed", "id", "value"],
        ["global attribute"] = ["default", "fixed", "form", "id", "name", "ref", "type"],
        ["global element"] = ["abstract", "block", "default", "final", "fixed", "form", "id", "name", "nillable", "ref", "substitutionGroup", "type"],
        ["global attributeGroup"] = ["id", "name"],
        ["global group"] = ["abstract", "id", "mixed", "name"],
        ["import"] = ["id", "namespace", "schemaLocation"],
        ["length"] = ["fixed", "id", "value"],
        ["group"] = ["id", "maxOccurs", "minOccurs", "ref"],
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
    /// The first refusal real's reading of the schema documents raises once
    /// every name resolves, in document order (probed 2026-10-07 against SQL
    /// Server 2025): an element the XSD namespace lacks (Msg 2297), an
    /// attribute its element doesn't take (2298), an occurrence or a length
    /// facet's value that isn't a number (2309), a <c>totalDigits</c> of 0
    /// (2386), a boolean or an enumerated
    /// attribute given another value (2312 / 2313), a global declaration
    /// without its name (2299), a name beside a <c>ref</c> (2360),
    /// <c>minOccurs</c> above <c>maxOccurs</c> (2382), a type named and
    /// declared inline at once (2305), and an attribute declared twice in one
    /// complex type or attribute group (2310). Null when there is none.
    /// </summary>
    private static SimulatedSqlException? FirstStructuralError(string xsdText)
    {
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xsdText), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            var path = new List<string>();
            var siblingCounts = new List<Dictionary<string, int>> { new(StringComparer.Ordinal) };
            // Per open element: whether it names a type, and the attribute
            // names a complex type or attribute group has declared.
            var namesType = new List<bool>();
            var declaredAttributes = new List<HashSet<string>?>();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    path.RemoveAt(path.Count - 1);
                    siblingCounts.RemoveAt(siblingCounts.Count - 1);
                    namesType.RemoveAt(namesType.Count - 1);
                    declaredAttributes.RemoveAt(declaredAttributes.Count - 1);
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var counts = siblingCounts[^1];
                counts[reader.LocalName] = counts.GetValueOrDefault(reader.LocalName) + 1;
                var step = $"/*:{reader.LocalName}[{counts[reader.LocalName]}]";
                var location = string.Concat(path) + step;
                var local = reader.LocalName;
                var isXsd = reader.NamespaceURI == XsdNamespace;
                // An attribute of a complex type's content element is located
                // at the type itself (probed 2026-10-07 against SQL Server 2025).
                var attributeLocation = isXsd && local is "complexContent" or "simpleContent" ? string.Concat(path) : location;
                var error = isXsd ? StructuralErrorAt(reader, local, location, attributeLocation, namesType, declaredAttributes) : null;
                if (error is not null)
                    return error;

                if (!reader.IsEmptyElement)
                {
                    path.Add(step);
                    siblingCounts.Add(new(StringComparer.Ordinal));
                    namesType.Add(isXsd && local is "element" or "attribute" && reader.GetAttribute("type") is not null);
                    declaredAttributes.Add(isXsd && local is "complexType" or "attributeGroup" ? new(StringComparer.Ordinal) : null);
                }
            }
        }
        catch (XmlException)
        {
        }
        return null;
    }

    private static SimulatedSqlException? StructuralErrorAt(XmlReader reader, string local, string location, string attributeLocation, List<bool> namesType, List<HashSet<string>?> declaredAttributes)
    {
        if (!XsdElementNames.Contains(local))
            return SimulatedSqlException.XmlSchemaElementNotValid(local, location);

        var depth = reader.Depth;
        var listKey = local switch
        {
            "attribute" or "attributeGroup" or "element" or "group" when depth == 1 => "global " + local,
            "element" => "local element",
            _ => local,
        };
        if (XsdAttributes.TryGetValue(listKey, out var allowed) && reader.MoveToFirstAttribute())
        {
            do
            {
                // Namespace declarations and attributes of another namespace
                // ride along, and xml:lang is taken everywhere but appinfo.
                var attribute = reader.LocalName;
                if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && attribute == "xmlns"))
                    continue;
                var refused = reader.NamespaceURI.Length == 0
                    ? allowed.Contains(attribute) ? null : attribute
                    : reader.Prefix == "xml" && attribute == "lang" && local is "appinfo" or "import" ? attribute : null;
                var error = refused is not null
                    ? SimulatedSqlException.XmlSchemaAttributeNotValid(refused, attributeLocation)
                    : reader.NamespaceURI.Length == 0 ? AttributeValueError(local, attribute, reader.Value)
                    : reader.Prefix == "xml" && attribute == "lang" && local == "documentation" && !IsLanguageTag(reader.Value) ? SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, reader.Value)
                    : null;
                if (error is not null)
                {
                    _ = reader.MoveToElement();
                    return error;
                }
            }
            while (reader.MoveToNextAttribute());
            _ = reader.MoveToElement();
        }

        if (local == "element" && reader.GetAttribute("fixed") is not null && reader.GetAttribute("default") is not null)
            return SimulatedSqlException.XmlSchemaAttributeNotValid("fixed", location);
        if (local == "choice" && reader.IsEmptyElement && (reader.GetAttribute("minOccurs")?.Trim() ?? "1") != "0")
            return SimulatedSqlException.XmlSchemaEmptyChoice(location);
        var name = reader.GetAttribute("name");
        var reference = reader.GetAttribute("ref");
        if (depth == 1 && name is null && local is "attribute" or "attributeGroup" or "complexType" or "element" or "group" or "simpleType")
            return SimulatedSqlException.XmlSchemaRequiredAttributeMissing("name", local);
        if (name is not null && reference is not null && local is "attribute" or "element")
            return SimulatedSqlException.XmlSchemaNameAndRef(location);
        if (reader.GetAttribute("minOccurs") is { } minText && reader.GetAttribute("maxOccurs") is { } maxText && maxText.Trim() != "unbounded"
            && ulong.TryParse(minText.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minOccurs)
            && ulong.TryParse(maxText.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maxOccurs)
            && minOccurs > maxOccurs)
        {
            return SimulatedSqlException.XmlSchemaOccursOutOfOrder(location);
        }
        if (local is "complexType" or "simpleType" && namesType.Count > 0 && namesType[^1])
            return SimulatedSqlException.XmlSchemaTypeSpecifiedTwice(location);
        if (local == "attribute" && declaredAttributes.Count > 0 && declaredAttributes[^1] is { } declared
            && (name ?? LocalPart(reference)) is { } attributeName && !declared.Add(attributeName))
        {
            return SimulatedSqlException.XmlSchemaAttributeDeclaredTwice(attributeName);
        }
        return null;
    }

    /// <summary>The value refusals of <see cref="FirstStructuralError"/>'s attributes: Msg 2309, 2312 and 2313.</summary>
    private static SimulatedSqlException? AttributeValueError(string element, string attribute, string value)
    {
        var trimmed = value.Trim();
        switch (attribute)
        {
            case "abstract" or "mixed" or "nillable":
            case "fixed" when element is not ("attribute" or "element"):
                return trimmed is "0" or "1" or "false" or "true" ? null : SimulatedSqlException.XmlSchemaAttributeNotBoolean(attribute, value);
            case "block" or "blockDefault" or "final" or "finalDefault":
                // #all, or a list of the derivations the attribute names.
                return trimmed == "#all" || Array.TrueForAll(
                    trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    static token => token is "extension" or "list" or "restriction" or "substitution" or "union")
                    ? null
                    : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "attributeFormDefault" or "elementFormDefault" or "form":
                return trimmed is "qualified" or "unqualified" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "maxOccurs" when trimmed == "unbounded":
                return null;
            case "maxOccurs" or "minOccurs":
                return ulong.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
                    ? null
                    : SimulatedSqlException.XmlSchemaFacetValueNotNumber(attribute);
            case "processContents":
                return trimmed is "lax" or "skip" or "strict" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "use":
                return trimmed is "optional" or "prohibited" or "required" ? null : SimulatedSqlException.XmlSchemaAttributeValueNotAllowed(attribute, value);
            case "value" when element is "fractionDigits" or "length" or "maxLength" or "minLength" or "totalDigits":
                return !ulong.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                    ? SimulatedSqlException.XmlSchemaFacetValueNotNumber()
                    : count == 0 && element == "totalDigits" ? SimulatedSqlException.XmlSchemaTotalDigitsOutOfRange() : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The first contradiction in a named simple type's own restriction, in
    /// document order, once the facets each base takes have been judged
    /// (probed 2026-10-07 against SQL Server 2025): a restriction chain
    /// returning to the type (Msg 2366),
    /// <c>fractionDigits</c> over <c>totalDigits</c> (6950), <c>minLength</c>
    /// over <c>maxLength</c> (6946), and a numeric lower bound over its upper
    /// one (6951 / 6952). Null when there is none; an anonymous type, whose
    /// name real spells as a path, is left unjudged.
    /// </summary>
    private static SimulatedSqlException? FirstTypeDefinitionError(string xsdText)
    {
        var bases = new Dictionary<string, string?>(StringComparer.Ordinal);
        var restrictions = new List<(string Type, Dictionary<string, string> Facets, string? Base)>();
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xsdText), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            string? type = null;
            Dictionary<string, string>? facets = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != XsdNamespace)
                    continue;
                switch (reader.LocalName)
                {
                    case "simpleType" when reader.Depth == 1:
                        type = reader.GetAttribute("name");
                        facets = null;
                        break;
                    case "complexType" or "element" or "attribute" when reader.Depth == 1:
                        type = null;
                        facets = null;
                        break;
                    case "restriction" when reader.Depth == 2 && type is not null:
                        var restrictionBase = reader.GetAttribute("base");
                        _ = bases.TryAdd(type, restrictionBase);
                        facets = new(StringComparer.Ordinal);
                        restrictions.Add((type, facets, restrictionBase));
                        break;
                    case "fractionDigits" or "maxExclusive" or "maxInclusive" or "maxLength" or "minExclusive" or "minInclusive" or "minLength" or "totalDigits"
                        when reader.Depth == 3 && facets is not null:
                        _ = facets.TryAdd(reader.LocalName, (reader.GetAttribute("value") ?? string.Empty).Trim());
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }

        foreach (var (type, facets, restrictionBase) in restrictions)
        {
            // A chain of bases in no namespace that leads back to the type.
            var seen = new HashSet<string>(StringComparer.Ordinal) { type };
            for (var next = restrictionBase; next is not null && !next.Contains(':', StringComparison.Ordinal); next = bases.GetValueOrDefault(next))
            {
                if (!seen.Add(next))
                {
                    if (next == type)
                        return SimulatedSqlException.XmlSchemaCircularDefinition(type);
                    break;
                }
            }

            static bool Count(Dictionary<string, string> facets, string facet, out ulong value) =>
                ulong.TryParse(facets.GetValueOrDefault(facet), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
            static bool Bound(Dictionary<string, string> facets, string facet, out decimal value) =>
                decimal.TryParse(facets.GetValueOrDefault(facet), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

            if (Count(facets, "fractionDigits", out var fractionDigits) && Count(facets, "totalDigits", out var totalDigits) && fractionDigits > totalDigits)
                return SimulatedSqlException.XmlSchemaFacetsContradict(type, 6950);
            if (Count(facets, "minLength", out var minLength) && Count(facets, "maxLength", out var maxLength) && minLength > maxLength)
                return SimulatedSqlException.XmlSchemaFacetsContradict(type, 6946);
            if (!IsNumericBuiltIn(restrictionBase))
                continue;
            if (Bound(facets, "minInclusive", out var minInclusive)
                && ((Bound(facets, "maxInclusive", out var maxInclusive) && minInclusive > maxInclusive)
                    || (Bound(facets, "maxExclusive", out var maxExclusiveOfInclusive) && minInclusive >= maxExclusiveOfInclusive)))
            {
                return SimulatedSqlException.XmlSchemaFacetsContradict(type, 6951);
            }
            if (Bound(facets, "minExclusive", out var minExclusive)
                && ((Bound(facets, "maxExclusive", out var maxExclusive) && minExclusive > maxExclusive)
                    || (Bound(facets, "maxInclusive", out var maxInclusiveOfExclusive) && minExclusive >= maxInclusiveOfExclusive)))
            {
                return SimulatedSqlException.XmlSchemaFacetsContradict(type, 6952);
            }
        }
        return null;
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

    /// <summary>Whether <paramref name="qualifiedName"/> names one of the XSD namespace's numeric types, by its usual prefix's local part.</summary>
    private static bool IsNumericBuiltIn(string? qualifiedName) =>
        LocalPart(qualifiedName) is "byte" or "decimal" or "int" or "integer" or "long" or "negativeInteger" or "nonNegativeInteger"
            or "nonPositiveInteger" or "positiveInteger" or "short" or "unsignedByte" or "unsignedInt" or "unsignedLong" or "unsignedShort"
        && qualifiedName!.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// One <c>xsd:</c> element of <see cref="RejectUncompilableSchema"/>'s walk:
    /// a global declaration joins <paramref name="declared"/> (Msg 2302 when
    /// already there), each name it references joins
    /// <paramref name="references"/>, and a facet joins <paramref name="facets"/>.
    /// </summary>
    private static void InspectXsdElement(
        XmlReader reader,
        int depth,
        ref string targetNamespace,
        HashSet<(string Kind, string Namespace, string Name)> declared,
        List<(string Kind, XmlQualifiedName Name)> references,
        List<(string Facet, string Value, string Location, int Line, int Position)> facets,
        string location,
        IXmlLineInfo lineInfo)
    {
        var local = reader.LocalName;
        if (depth == 0 && local == "schema")
        {
            targetNamespace = reader.GetAttribute("targetNamespace") ?? string.Empty;
            return;
        }

        if (depth == 1 && reader.GetAttribute("name") is { } declaredName)
        {
            var kind = local switch
            {
                "attribute" => "attribute",
                "attributeGroup" => "attributeGroup",
                "complexType" or "simpleType" => "type",
                "element" => "element",
                "group" => "group",
                _ => null,
            };
            if (kind is not null && !declared.Add((kind, targetNamespace, declaredName)))
                throw SimulatedSqlException.XmlSchemaNameAlreadyDefined(declaredName);
        }

        void Reference(string kind, string? qualifiedName)
        {
            if (qualifiedName is null || qualifiedName.Trim() is not { Length: > 0 } trimmed)
                return;
            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            var prefix = colon < 0 ? string.Empty : trimmed[..colon];
            var name = colon < 0 ? trimmed : trimmed[(colon + 1)..];
            var ns = prefix == "xml" ? "http://www.w3.org/XML/1998/namespace" : reader.LookupNamespace(prefix) ?? string.Empty;
            references.Add((kind, new XmlQualifiedName(name, ns)));
        }

        switch (local)
        {
            case "attribute":
                Reference("attribute", reader.GetAttribute("ref"));
                Reference("type", reader.GetAttribute("type"));
                break;
            case "attributeGroup":
                Reference("attributeGroup", reader.GetAttribute("ref"));
                break;
            case "element":
                Reference("element", reader.GetAttribute("ref"));
                Reference("type", reader.GetAttribute("type"));
                Reference("element", reader.GetAttribute("substitutionGroup"));
                break;
            case "extension" or "restriction":
                Reference("type", reader.GetAttribute("base"));
                break;
            case "group":
                Reference("group", reader.GetAttribute("ref"));
                break;
            case "list":
                Reference("type", reader.GetAttribute("itemType"));
                break;
            case "union":
                foreach (var member in (reader.GetAttribute("memberTypes") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    Reference("type", member);
                break;
            case "enumeration" or "fractionDigits" or "length" or "maxExclusive" or "maxInclusive" or "maxLength"
                or "minExclusive" or "minInclusive" or "minLength" or "pattern" or "totalDigits" or "whiteSpace":
                facets.Add((local, reader.GetAttribute("value") ?? string.Empty, location, lineInfo.LineNumber, lineInfo.LinePosition));
                break;
        }
    }

    /// <summary>
    /// Checks the schema documents an <c>ALTER … ADD</c> brings: text that
    /// holds no <c>xsd:schema</c> document is Msg 2378, and a global element,
    /// type or attribute this collection already declares in the same
    /// namespace is Msg 6310.
    /// </summary>
    public void RejectRedeclaredComponents(string addedText)
    {
        var added = new List<System.Xml.Schema.XmlSchema>();
        try
        {
            var settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment };
            using var reader = XmlReader.Create(new System.IO.StringReader(addedText), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.NamespaceURI != XsdNamespace || reader.LocalName != "schema")
                    throw SimulatedSqlException.XmlSchemaDocumentExpected();
                if (System.Xml.Schema.XmlSchema.Read(reader.ReadSubtree(), null) is { } schema)
                    added.Add(schema);
            }
        }
        catch (XmlException)
        {
            throw SimulatedSqlException.XmlSchemaDocumentExpected();
        }
        catch (System.Xml.Schema.XmlSchemaException)
        {
            // A schema document the object model can't read is left to the
            // compile checks, whose refusals real raises for it.
            return;
        }
        if (added.Count == 0)
            throw SimulatedSqlException.XmlSchemaDocumentExpected();

        var existing = this.GetCompiledSchemas();
        if (existing is null)
            return;
        foreach (var schema in added)
        {
            var targetNamespace = schema.TargetNamespace ?? string.Empty;
            foreach (var item in schema.Items)
            {
                var (componentName, kind, table) = item switch
                {
                    System.Xml.Schema.XmlSchemaElement element => (element.Name, "ELEMENT", existing.GlobalElements),
                    System.Xml.Schema.XmlSchemaAttribute attribute => (attribute.Name, "ATTRIBUTE", existing.GlobalAttributes),
                    System.Xml.Schema.XmlSchemaType type => (type.Name, "TYPE", existing.GlobalTypes),
                    _ => (null, string.Empty, null),
                };
                if (componentName is not null && table!.Contains(new XmlQualifiedName(componentName, targetNamespace)))
                    throw SimulatedSqlException.XmlSchemaComponentExists(targetNamespace, componentName, kind);
            }
        }
    }

    /// <summary>
    /// Scans the (possibly multi-document) XSD text for element declarations
    /// and returns the names none of them declares more than once. A text the
    /// reader can't get through yields an empty set — the untyped behavior,
    /// which is what the simulator did before it read the XSD at all.
    /// </summary>
    private static FrozenSet<string> ReadSingletonElementNames(string xsdText)
    {
        var singleton = new HashSet<string>(StringComparer.Ordinal);
        var plural = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = CreateFragmentReader(xsdText);
            while (reader.Read())
            {
                // Depth 0 is an `xsd:schema`, depth 1 its global declarations;
                // an occurrence constraint only exists from depth 2 down.
                if (reader.NodeType != XmlNodeType.Element || reader.Depth < 2
                    || reader.LocalName != "element" || reader.NamespaceURI != XsdNamespace)
                {
                    continue;
                }

                var name = reader.GetAttribute("name") ?? LocalPart(reader.GetAttribute("ref"));
                if (name is null)
                    continue;
                if (reader.GetAttribute("maxOccurs") is { } maxOccurs && maxOccurs != "1")
                    _ = plural.Add(name);
                else
                    _ = singleton.Add(name);
            }
        }
        catch (XmlException)
        {
            return [];
        }

        singleton.ExceptWith(plural);
        return singleton.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Scans the (possibly multi-document) XSD text and returns the element
    /// names every declaration gives simply-typed content. Two passes over the
    /// same text: the first collects the named <c>xsd:simpleType</c> names a
    /// <c>type</c> attribute can point at, the second classifies each element
    /// declaration. A text the reader can't get through yields an empty set —
    /// the untyped behavior.
    /// </summary>
    private static FrozenSet<string> ReadSimpleContentElementNames(string xsdText)
    {
        var simple = new HashSet<string>(StringComparer.Ordinal);
        var complex = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var simpleTypeNames = ReadNamedSimpleTypeNames(xsdText);
            using var reader = CreateFragmentReader(xsdText);

            // Set while the reader sits on an element declaration that named no
            // type: its content model is whatever its own first child says, so
            // the classification waits one step.
            string? awaitingInlineType = null;
            var awaitingDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != XsdNamespace)
                    continue;

                if (awaitingInlineType is { } pending)
                {
                    _ = (reader.Depth == awaitingDepth + 1 && reader.LocalName == "simpleType" ? simple : complex).Add(pending);
                    awaitingInlineType = null;
                }

                if (reader.LocalName != "element")
                    continue;
                var name = reader.GetAttribute("name") ?? LocalPart(reader.GetAttribute("ref"));
                if (name is null)
                    continue;

                if (reader.GetAttribute("type") is { } declaredType)
                {
                    var prefix = declaredType.Contains(':', StringComparison.Ordinal)
                        ? declaredType[..declaredType.IndexOf(':', StringComparison.Ordinal)]
                        : string.Empty;
                    var isSimple = reader.LookupNamespace(prefix) == XsdNamespace
                        || simpleTypeNames.Contains(LocalPart(declaredType)!);
                    _ = (isSimple ? simple : complex).Add(name);
                }
                else if (reader.IsEmptyElement)
                {
                    // No type and no content model is xsd:anyType — complex.
                    _ = complex.Add(name);
                }
                else
                {
                    awaitingInlineType = name;
                    awaitingDepth = reader.Depth;
                }
            }

            if (awaitingInlineType is { } trailing)
                _ = complex.Add(trailing);
        }
        catch (XmlException)
        {
            return [];
        }

        simple.ExceptWith(complex);
        return simple.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The names of the <c>xsd:simpleType</c> declarations in
    /// <paramref name="xsdText"/> — the types an element's <c>type</c>
    /// attribute can name and still hold a value.
    /// </summary>
    private static HashSet<string> ReadNamedSimpleTypeNames(string xsdText)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = CreateFragmentReader(xsdText);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.NamespaceURI == XsdNamespace
                && reader.LocalName == "simpleType"
                && reader.GetAttribute("name") is { } name)
            {
                _ = names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// A reader over the collection's stored text, which holds one schema
    /// document per target namespace concatenated — a fragment, not a document.
    /// </summary>
    private static XmlReader CreateFragmentReader(string xsdText) =>
        XmlReader.Create(
            new StringReader(xsdText),
            new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });

    private static string? LocalPart(string? qualifiedName) =>
        qualifiedName?[(qualifiedName.IndexOf(':', StringComparison.Ordinal) + 1)..];
}
