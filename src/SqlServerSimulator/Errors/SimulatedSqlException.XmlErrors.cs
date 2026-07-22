namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 6809: a <c>FOR XML RAW</c> / <c>AUTO</c> projection contains a
    /// column with no name or alias (attribute-centric and element-centric
    /// both require every column to be named). Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlUnnamedColumn() =>
        new("Unnamed tables cannot be used as XML identifiers as well as unnamed columns cannot be used for attribute names. Name unnamed columns/tables using AS in the SELECT statement.", 6809, 16, 1);

    /// <summary>
    /// Msg 6864: a <c>FOR XML PATH('')</c> (row-tag omission) projection maps
    /// a column to an attribute — attributes have no element to attach to when
    /// the row wrapper is suppressed. Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlAttributeWithoutRowTag() =>
        new("Row tag omission (empty row tag name) cannot be used with attribute-centric FOR XML serialization.", 6864, 16, 1);

    /// <summary>
    /// Msg 6852: a <c>FOR XML PATH</c> attribute-centric column
    /// (<c>[@name]</c>) appears after a non-attribute sibling at the same
    /// element level. SQL Server requires all attributes to precede element
    /// content on an element. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlAttributeAfterNonAttribute(string column) =>
        new($"Attribute-centric column '{column}' must not come after a non-attribute-centric sibling in XML hierarchy in FOR XML PATH.", 6852, 16, 1);

    /// <summary>
    /// Msg 6861: <c>FOR XML … ROOT('')</c> — the ROOT tag name is empty.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlEmptyRootTag() =>
        new("Empty root tag name can't be specified with FOR XML.", 6861, 16, 1);

    /// <summary>
    /// Msg 6829: a <c>FOR XML RAW</c> projection includes a binary column,
    /// which real SQL Server can only serialize under the (unmodeled) BINARY
    /// BASE64 option. Probe-confirmed wording against SQL Server 2025 (FOR XML
    /// PATH base64-encodes binary directly and is modeled).
    /// </summary>
    internal static SimulatedSqlException ForXmlBinaryRaw(string column) =>
        new($"FOR XML EXPLICIT and RAW modes currently do not support addressing binary data as URLs in column '{column}'. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.", 6829, 16, 1);

    /// <summary>
    /// Msg 6830: the <c>FOR XML AUTO</c> counterpart of
    /// <see cref="ForXmlBinaryRaw"/> for a binary column. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlBinaryAuto(string column) =>
        new($"FOR XML AUTO could not find the table owning the following column '{column}' to create a URL address for it. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.", 6830, 16, 1);
}
