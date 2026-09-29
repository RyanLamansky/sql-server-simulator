using System.Collections.Frozen;
using System.Xml.Schema;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The slice of an XML schema collection the XQuery compiler reads: which
/// element names are singletons, and the simple type each element and
/// attribute name is declared with. Keyed on the local name alone, like the
/// singleton rule: a name the collection types two different ways — or as
/// complex content anywhere — is left untyped, which errs toward accepting
/// what real accepts rather than refusing it.
/// </summary>
internal sealed class XmlStaticTyping(
    FrozenSet<string> singletonElements,
    FrozenDictionary<string, string> elementTypes,
    FrozenDictionary<string, string> attributeTypes,
    FrozenSet<string> nillableElements)
{
    /// <summary>The element names declared at most once wherever they appear.</summary>
    public readonly FrozenSet<string> SingletonElements = singletonElements;

    /// <summary>Each simply typed element name and its <c>xs:</c> type.</summary>
    public readonly FrozenDictionary<string, string> ElementTypes = elementTypes;

    /// <summary>Each attribute name and its <c>xs:</c> type.</summary>
    public readonly FrozenDictionary<string, string> AttributeTypes = attributeTypes;

    /// <summary>The element names every declaration marks <c>nillable</c>.</summary>
    public readonly FrozenSet<string> NillableElements = nillableElements;

    /// <summary>
    /// Builds the typing from the collection's compiled schemas, or just the
    /// singleton names when the XSD doesn't compile.
    /// </summary>
    public static XmlStaticTyping From(FrozenSet<string> singletonElements, XmlSchemaSet? schemas)
    {
        var elementTypes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var attributeTypes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var nillable = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (schemas is not null)
        {
            var visited = new HashSet<XmlSchemaElement>();
            foreach (XmlSchemaElement element in schemas.GlobalElements.Values)
                Visit(element, visited, elementTypes, attributeTypes, nillable);
        }

        return new XmlStaticTyping(
            singletonElements,
            Consistent(elementTypes),
            Consistent(attributeTypes),
            nillable.Where(pair => pair.Value).Select(pair => pair.Key).ToFrozenSet(StringComparer.Ordinal));
    }

    private static FrozenDictionary<string, string> Consistent(Dictionary<string, string?> types) =>
        types.Where(pair => pair.Value is not null).ToFrozenDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

    /// <summary>
    /// Records one element declaration — its simple type, or a conflict when
    /// the name was typed differently before — and walks into its content.
    /// </summary>
    private static void Visit(
        XmlSchemaElement element,
        HashSet<XmlSchemaElement> visited,
        Dictionary<string, string?> elementTypes,
        Dictionary<string, string?> attributeTypes,
        Dictionary<string, bool> nillable)
    {
        if (!visited.Add(element))
            return;
        var name = element.QualifiedName.Name;
        Record(elementTypes, name, element.ElementSchemaType is XmlSchemaSimpleType simple ? TypeNameOf(simple) : null);
        nillable[name] = element.IsNillable && (!nillable.TryGetValue(name, out var before) || before);

        if (element.ElementSchemaType is not XmlSchemaComplexType complex)
            return;
        foreach (XmlSchemaAttribute attribute in complex.AttributeUses.Values)
        {
            if (attribute.AttributeSchemaType is { } attributeType)
                Record(attributeTypes, attribute.QualifiedName.Name, TypeNameOf(attributeType));
        }
        VisitParticle(complex.ContentTypeParticle, visited, elementTypes, attributeTypes, nillable);
    }

    private static void VisitParticle(
        XmlSchemaParticle? particle,
        HashSet<XmlSchemaElement> visited,
        Dictionary<string, string?> elementTypes,
        Dictionary<string, string?> attributeTypes,
        Dictionary<string, bool> nillable)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
                Visit(element, visited, elementTypes, attributeTypes, nillable);
                break;
            case XmlSchemaGroupBase group:
                foreach (var item in group.Items)
                {
                    if (item is XmlSchemaParticle child)
                        VisitParticle(child, visited, elementTypes, attributeTypes, nillable);
                }
                break;
        }
    }

    /// <summary>Keeps a name's type only while every declaration agrees on it.</summary>
    private static void Record(Dictionary<string, string?> types, string name, string? type)
    {
        if (!types.TryGetValue(name, out var existing))
            types[name] = type;
        else if (existing != type)
            types[name] = null;
    }

    /// <summary>
    /// The <c>xs:</c> name of a simple type: a built-in's own, or the built-in
    /// primitive a user-derived atomic type restricts. A list or union has no
    /// single atomic type and answers null.
    /// </summary>
    private static string? TypeNameOf(XmlSchemaSimpleType type)
    {
        if (type.Datatype is not { Variety: XmlSchemaDatatypeVariety.Atomic } datatype)
            return null;
        for (XmlSchemaType? current = type; current is not null; current = current.BaseXmlSchemaType)
        {
            if (current.QualifiedName.Namespace == XmlAtomicTypes.SchemaNamespace && current.QualifiedName.Name.Length > 0)
                return "xs:" + current.QualifiedName.Name;
        }
        return XmlSchemaType.GetBuiltInSimpleType(datatype.TypeCode) is { } builtIn ? "xs:" + builtIn.QualifiedName.Name : null;
    }
}
