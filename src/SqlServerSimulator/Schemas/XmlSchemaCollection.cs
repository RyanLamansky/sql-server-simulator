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
/// The simulator doesn't parse the XSD or validate xml values against it —
/// the source text is stored verbatim for catalog-view round-trip via
/// <c>sys.xml_schema_collections</c> and for the per-column <c>xml(name)</c>
/// binding (which itself only records the reference; no shape validation
/// is performed). AW's 6 schema collections load end-to-end through this
/// path; apps that exercise xml-method validation hit the
/// <see cref="NotSupportedException"/> raised by <see cref="Storage.XmlSqlType"/>'s
/// method dispatch.
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
    public readonly int SchemaId = schemaId;

    /// <summary>
    /// Owning principal id. Probe-confirmed against SQL Server 2025: the
    /// column is nullable and CREATE without AUTHORIZATION leaves it
    /// NULL. The simulator preserves that semantic.
    /// </summary>
    public readonly int? PrincipalId = principalId;

    /// <summary>
    /// Raw XSD source text passed to <c>AS '…'</c>. Kept verbatim; the only
    /// thing read out of it is <see cref="GetSingletonElementNames"/>, whose
    /// cache re-reads when this is reassigned.
    /// </summary>
    public string XsdText = xsdText;

    public readonly DateTime CreateDate = createDate;

    public DateTime ModifyDate = createDate;

    private string? namesReadFrom;
    private FrozenSet<string>? singletonElementNames;

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

    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

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
            // A collection holds one schema document per target namespace,
            // concatenated — so the text is a fragment, not a document.
            using var reader = XmlReader.Create(
                new StringReader(xsdText),
                new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
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

    private static string? LocalPart(string? qualifiedName) =>
        qualifiedName?[(qualifiedName.IndexOf(':', StringComparison.Ordinal) + 1)..];
}
