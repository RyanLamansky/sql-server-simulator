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

    /// <summary>Raw XSD source text passed to <c>AS '…'</c>. Not parsed.</summary>
    public string XsdText = xsdText;

    public readonly DateTime CreateDate = createDate;

    public DateTime ModifyDate = createDate;
}
