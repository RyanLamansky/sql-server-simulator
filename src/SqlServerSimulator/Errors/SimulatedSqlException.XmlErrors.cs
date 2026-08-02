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
    /// Msg 6846: a <c>FOR XML</c> name carries a namespace prefix that is
    /// neither the predefined <c>xml</c> nor one a <c>WITH XMLNAMESPACES</c>
    /// prefix declared. Probe-confirmed wording (and state 4) against SQL
    /// Server 2025.
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
    /// Msg 6829: a <c>FOR XML RAW</c> projection includes a binary column and
    /// the <c>BINARY BASE64</c> option is absent — RAW has no <c>dbobject</c>
    /// URL form to fall back on the way AUTO does. Probe-confirmed wording
    /// against SQL Server 2025 (FOR XML PATH base64-encodes binary whatever the
    /// option says).
    /// </summary>
    internal static SimulatedSqlException ForXmlBinaryRaw(string column) =>
        new($"FOR XML EXPLICIT and RAW modes currently do not support addressing binary data as URLs in column '{column}'. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.", 6829, 16, 1);

    /// <summary>
    /// Msg 6830: a <c>FOR XML AUTO</c> binary column without <c>BINARY
    /// BASE64</c> has no owning table to address a <c>dbobject</c> URL against
    /// — an expression, a derived table's column, or a set-operation result.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlBinaryAuto(string column) =>
        new($"FOR XML AUTO could not find the table owning the following column '{column}' to create a URL address for it. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.", 6830, 16, 1);

    /// <summary>
    /// Msg 6831: a <c>FOR XML AUTO</c> binary column without <c>BINARY
    /// BASE64</c> does have an owning table, but the <c>dbobject</c> URL can't
    /// be addressed — the table has no primary key, or the projection doesn't
    /// carry every key column. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlBinaryAutoNeedsPrimaryKey(string column) =>
        new($"FOR XML AUTO requires primary keys to create references for '{column}'. Select primary keys, or use BINARY BASE64 to obtain binary data in encoded form if no primary keys exist.", 6831, 16, 1);

    /// <summary>
    /// Msg 6868: a <c>WITH XMLNAMESPACES</c> prefix scopes a <c>FOR XML</c>
    /// clause using one of the features the declarations can't reach — EXPLICIT
    /// mode, or the XMLSCHEMA / XMLDATA directives. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlNamespacesUnsupportedFeature() =>
        new("The following FOR XML features are not supported with WITH XMLNAMESPACES list: EXPLICIT mode, XMLSCHEMA and XMLDATA directives.", 6868, 16, 1);

    /// <summary>
    /// Msg 6869: a <c>WITH XMLNAMESPACES</c> clause binds the same prefix twice
    /// — <paramref name="prefix"/> is the written prefix, or the literal
    /// <c>default</c> for a repeated <c>DEFAULT</c>. Probe-confirmed wording,
    /// sentence-final period included (there isn't one), against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException XmlNamespaceRedefined(string prefix) =>
        new($"Attempt to redefine namespace prefix '{prefix}'", 6869, 16, 1);

    /// <summary>
    /// Msg 6870: a <c>WITH XMLNAMESPACES</c> prefix isn't a legal XML name.
    /// The message names the first offending character and its code point.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlNamespacePrefixNotAName(string prefix, char offender) =>
        new($"Prefix '{prefix}' used in WITH XMLNAMESPACES clause contains an invalid XML identifier. '{offender}'(0x{((int)offender).ToString("X4", CultureInfo.InvariantCulture)}) is the first character at fault.", 6870, 16, 1);

    /// <summary>
    /// Msg 6871: a <c>WITH XMLNAMESPACES</c> clause tries to bind <c>xmlns</c>,
    /// the namespace-declaration name itself. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlNamespacePrefixReserved() =>
        new("Prefix 'xmlns' used in WITH XMLNAMESPACES is reserved and cannot be used as a user-defined prefix.", 6871, 16, 1);

    /// <summary>
    /// Msg 6872: the predefined <c>xml</c> prefix and its URI must be bound to
    /// each other. Real splits the two directions by state (probe-confirmed):
    /// state 1 binds <c>xml</c> to some other URI, state 2 binds that URI to
    /// some other prefix; both carry the same text.
    /// </summary>
    internal static SimulatedSqlException XmlNamespaceXmlPrefixMisbound(byte state) =>
        new("XML namespace prefix 'xml' can only be associated with the URI http://www.w3.org/XML/1998/namespace. This URI cannot be used with other prefixes.", 6872, 16, state);

    /// <summary>
    /// Msg 6873: a <c>WITH XMLNAMESPACES</c> clause rebinds <c>xsi</c> while
    /// <c>ELEMENTS XSINIL</c> needs that prefix for its own nil markers.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlNamespaceXsiRedefinedWithXsinil() =>
        new("Redefinition of 'xsi' XML namespace prefix is not supported with ELEMENTS XSINIL option of FOR XML.", 6873, 16, 1);

    /// <summary>
    /// Msg 6874: a <c>WITH XMLNAMESPACES</c> binding carries an empty URI.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlNamespaceEmptyUri() =>
        new("Empty URI is not allowed in WITH XMLNAMESPACES clause.", 6874, 16, 1);

    /// <summary>
    /// Msg 8137: a mutator XML method (<c>.modify()</c>) appears where a value
    /// is expected — a select list, a predicate, the right-hand side of an
    /// assignment. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlMutatorInValuePosition() =>
        new("Incorrect use of the XML data type method 'modify'. A non-mutator method is expected in this context.", 8137, 16, 1);

    /// <summary>
    /// Msg 8113: a non-mutator XML method sits in a mutator position — the
    /// whole right-hand side of <c>SET @x.&lt;method&gt;(…)</c> or of an
    /// UPDATE's <c>SET col.&lt;method&gt;(…)</c> clause. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlNonMutatorInMutatorPosition(string method) =>
        new($"Incorrect use of the XML data type method '{method}'. A mutator method is expected in this context.", 8113, 16, 1);

    /// <summary>
    /// Msg 258: an instance method is called on a value whose type has none.
    /// Severity 15 — real reports this as a syntax-class error.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotCallMethodsOn(string typeName) =>
        new($"Cannot call methods on {typeName}.", 258, 15, 1);

    /// <summary>
    /// Msg 5302: <c>.modify()</c> is called on a NULL <c>xml</c> instance.
    /// <paramref name="name"/> is the variable (with its <c>@</c>) or column
    /// as written. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlMutatorOnNullValue(string name) =>
        new($"Mutator 'modify()' on '{name}' cannot be called on a null value.", 5302, 16, 1);

    /// <summary>
    /// Msg 6305: the <c>.modify()</c> argument parses as an XQuery expression
    /// but isn't one of the three XML-DML statements. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlExpressionRequired() =>
        new("XQuery data manipulation expression required in XML data type method.", 6305, 16, 1);

    /// <summary>
    /// Msg 2209: the XML-DML text fails to parse, naming the token at fault
    /// (<c>&lt;eof&gt;</c> when the text ran out). Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlSyntaxError(string token) =>
        new($"XQuery [modify()]: Syntax error near '{token}'", 2209, 16, 1);

    /// <summary>
    /// Msg 2205: a <c>replace value of</c> has no <c>with</c> clause.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlWithExpected() =>
        new("XQuery [modify()]: \"with\" was expected.", 2205, 16, 1);

    /// <summary>
    /// Msg 2337: the <c>replace value of</c> target isn't statically at most
    /// one node. Real types the path off its shape alone, so only a
    /// <c>(…)[n]</c> wrapper makes a step singular. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceTargetNotSingleton(string staticType) =>
        new($"XQuery [modify()]: The target of 'replace' must be at most one node, found '{staticType}'", 2337, 16, 1);

    /// <summary>
    /// Msg 2356: the <c>replace value of</c> target is a node whose value can't
    /// be written — an untyped element rather than an attribute or a
    /// <c>text()</c> node. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceTargetNotSimpleContent(string staticType) =>
        new($"XQuery [modify()]: The target of 'replace value of' must be a non-metadata attribute or an element with simple typed content, found '{staticType}'", 2356, 16, 1);

    /// <summary>
    /// Msg 9310: the <c>with</c> clause of a <c>replace value of</c> holds an
    /// XML constructor rather than a value. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceWithConstructedXml() =>
        new("XQuery [modify()]: The 'with' clause of 'replace value of' cannot contain constructed XML.", 9310, 16, 1);

    /// <summary>
    /// Msg 2226: the <c>insert</c> target isn't statically a single node.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlInsertTargetNotSingleton(string staticType) =>
        new($"XQuery [modify()]: The target of 'insert' must be a single node, found '{staticType}'", 2226, 16, 1);

    /// <summary>
    /// Msg 2240: an <c>insert … into</c> names something other than an element
    /// or the document node. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlInsertIntoTargetKind(string staticType) =>
        new($"XQuery [modify()]: The target of 'insert into' must be an element/document node, found '{staticType}'", 2240, 16, 1);

    /// <summary>
    /// Msg 2249: an <c>insert … before</c> / <c>after</c> names a node kind
    /// that has no siblings to sit among. Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlInsertBeforeAfterTargetKind(string staticType) =>
        new($"XQuery [modify()]: The target of 'insert before/after' must be an element/PI/comment/text node, found '{staticType}'", 2249, 16, 1);

    /// <summary>
    /// Msg 2258: an attribute constructor is inserted with a positional
    /// keyword — attributes have no document order to sit in. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlAttributeInsertHasPosition(string staticType) =>
        new($"XQuery [modify()]: The position may not be specified when inserting an attribute node, found '{staticType}'", 2258, 16, 1);

    /// <summary>
    /// Msg 2207: an <c>insert</c>'s content is an atomic value rather than a
    /// node. Probe-confirmed wording — including the sentence-final period and
    /// the double-quoted type — against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlOnlyNodesInsertable(string staticType) =>
        new($"XQuery [modify()]: Only non-document nodes can be inserted. Found \"{staticType}\".", 2207, 16, 1);

    /// <summary>
    /// Msg 2264: a <c>delete</c> names the document node or an atomic value.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlOnlyNodesDeletable(string staticType) =>
        new($"XQuery [modify()]: Only non-document nodes may be deleted, found '{staticType}'", 2264, 16, 1);

    /// <summary>
    /// Msg 6308: an <c>insert</c> would give an element two attributes of the
    /// same name. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDuplicateAttribute(string attributeName) =>
        new($"XML well-formedness check: Duplicate attribute '{attributeName}'. Rewrite your XQuery so it returns well-formed XML.", 6308, 16, 1);

    /// <summary>
    /// Msg 6602 state 2: <c>sp_xml_preparedocument</c> couldn't parse its
    /// document (or its <c>@xpath_namespaces</c> wrapper). Real attributes the
    /// error to the procedure and quotes the XML parser's own complaint;
    /// <paramref name="detail"/> is the reader's message, which the simulator
    /// takes from .NET rather than MSXML. Probe-confirmed shape against SQL
    /// Server 2025 — real emits the "The XML parse error 0x… occurred …" line
    /// as a separate info message, so it is not part of this text.
    /// </summary>
    internal static SimulatedSqlException XmlDocumentParseFailed(string detail)
    {
        var error = new SimulatedSqlException($"The error description is '{detail}'.", 6602, 16, 2);
        error.Errors[0].Procedure = "sp_xml_preparedocument";
        return error;
    }

    /// <summary>
    /// Msg 6603 state 2: an <c>OPENXML</c> rowpattern or colpattern isn't a
    /// pattern the XPath engine accepts. Real's text is the parser's complaint
    /// followed by a blank line and the pattern with a <c>--&gt;x&lt;--</c>
    /// marker at the offending token; the simulator keeps that shape, with
    /// .NET's message and the marker at the pattern's end.
    /// </summary>
    internal static SimulatedSqlException XmlPatternParseFailed(string detail, string pattern) =>
        new($"XML parsing error: {detail}\r\n\n{pattern}--><--", 6603, 16, 2);

    /// <summary>
    /// Msg 8179 state 5: a document handle that <c>sp_xml_preparedocument</c>
    /// never issued on this session, or that <c>sp_xml_removedocument</c>
    /// already released. A NULL handle reports <c>0</c>. Probe-confirmed
    /// wording against SQL Server 2025 (the message says "prepared statement",
    /// which is real's own shared text with the cursor-handle family).
    /// </summary>
    internal static SimulatedSqlException CouldNotFindPreparedStatement(int handle) =>
        new($"Could not find prepared statement with handle {handle}.", 8179, 16, 5);
}
