using System.Globalization;
using SqlServerSimulator.Parser;

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
    /// Msg 6800: <c>FOR XML AUTO</c> on a SELECT with no FROM clause — AUTO
    /// names every element after a table, so it has nothing to name.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlAutoRequiresTable() =>
        new("FOR XML AUTO requires at least one table for generating XML tags. Use FOR XML RAW or add a FROM clause with a table name.", 6800, 16, 1);

    /// <summary>
    /// Msg 6851: a <c>FOR XML PATH</c> projection maps an <c>xml</c>-typed
    /// column to an attribute (<c>[@name]</c>) — an xml value serializes as
    /// nodes, which an attribute can't hold. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlAttributeInvalidType(string column) =>
        new($"Column '{column}' has invalid data type for attribute-centric XML serialization in FOR XML PATH.", 6851, 16, 1);

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
    /// Msg 6850: a <c>FOR XML PATH</c> column alias, or any mode's explicit row
    /// tag / <c>ROOT</c> name, isn't a legal XML name — RAW and AUTO escape
    /// such a name as <c>_xHHHH_</c> instead, but these positions reject it.
    /// The message names the first offending character and its code point.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlInvalidName(ForXmlNameKind kind, string name, char offender) =>
        new($"{NameKindWord(kind)} name '{name}' contains an invalid XML identifier as required by FOR XML; '{offender}'(0x{((int)offender).ToString("X4", CultureInfo.InvariantCulture)}) is the first character at fault.", 6850, 16, 1);

    /// <summary>
    /// Msg 6846: a <c>FOR XML</c> name carries a namespace prefix other than
    /// the predefined <c>xml</c>, which only the unmodeled
    /// <c>WITH XMLNAMESPACES</c> clause could declare. Probe-confirmed wording
    /// (and state 4) against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlUndeclaredPrefix(string prefix, string name, ForXmlNameKind kind) =>
        new($"XML name space prefix '{prefix}' declaration is missing for FOR XML {NameKindPhrase(kind)} name '{name}'.", 6846, 16, 4);

    /// <summary>
    /// Msg 6867: a <c>FOR XML</c> name is <c>xmlns</c> or carries it as a
    /// prefix — the namespace-declaration name, which no column alias, row tag
    /// or ROOT name may claim. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlXmlnsName() =>
        new("'xmlns' is invalid in XML tag name in FOR XML PATH, or when WITH XMLNAMESPACES is used with FOR XML.", 6867, 16, 1);

    /// <summary>
    /// Msg 6849: a <c>FOR XML PATH</c> column alias has an empty path step — a
    /// leading or trailing <c>/</c>, or a <c>//</c>. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlPathSlashPlacement(string column) =>
        new($"FOR XML PATH error in column '{column}' - '//' and leading and trailing '/' are not allowed in simple path expressions.", 6849, 16, 1);

    /// <summary>
    /// Msg 6819: a <c>FOR XML</c> clause sits on the SELECT an
    /// <c>INSERT … SELECT</c> or <c>SELECT … INTO</c> writes from
    /// (<paramref name="statementKind"/> is real's own word for the statement).
    /// Probe-confirmed wording against SQL Server 2025, including the missing
    /// article agreement.
    /// </summary>
    internal static SimulatedSqlException ForXmlNotAllowedIn(string statementKind) =>
        new($"The FOR XML clause is not allowed in a {statementKind} statement.", 6819, 16, 1);

    /// <summary>
    /// Msg 6819 state 3: a <c>FOR XML</c> — or, quirk-faithfully, a
    /// <c>FOR JSON</c> — clause sits on a variable-assigning
    /// <c>SELECT @v = …</c>. Real reports the FOR XML wording for both clauses
    /// here (probe-confirmed against SQL Server 2025), where the INSERT and
    /// SELECT INTO paths give FOR JSON its own Msg 13602.
    /// </summary>
    internal static SimulatedSqlException ForXmlNotAllowedInAssignment() =>
        new("The FOR XML clause is not allowed in a ASSIGNMENT statement.", 6819, 16, 3);

    /// <summary>The Msg 6850 leading word for each name position.</summary>
    private static string NameKindWord(ForXmlNameKind kind) => kind switch
    {
        ForXmlNameKind.Column => "Column",
        ForXmlNameKind.Root => "ROOT",
        _ => "Row",
    };

    /// <summary>The Msg 6846 mid-sentence form for each name position.</summary>
    private static string NameKindPhrase(ForXmlNameKind kind) => kind switch
    {
        ForXmlNameKind.Column => "column",
        ForXmlNameKind.Root => "ROOT",
        _ => "row",
    };

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
