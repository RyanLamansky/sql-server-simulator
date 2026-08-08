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

    /// <summary>
    /// Msg 6853: a <c>FOR XML PATH</c> alias whose last step is a node function
    /// (<c>text()</c> / <c>data()</c> / <c>comment()</c> /
    /// <c>processing-instruction(…)</c>) maps an <c>xml</c>-typed column, which
    /// has no text form to place there. <c>node()</c> / <c>*</c> and a plain
    /// element step take one instead. Probe-confirmed wording against SQL
    /// Server 2025; the message quotes the whole alias.
    /// </summary>
    internal static SimulatedSqlException ForXmlPathLastStepNotApplicable(string column) =>
        new($"Column '{column}': the last step in the path can't be applied to XML data type or CLR type in FOR XML PATH.", 6853, 16, 1);

    /// <summary>
    /// Msg 6854: a <c>processing-instruction()</c> step names no target.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlProcessingInstructionForm(string column) =>
        new($"Invalid column alias '{column}' for formatting column as XML processing instruction in FOR XML PATH - it must be in 'processing-instruction(target)' format.", 6854, 16, 1);

    /// <summary>
    /// Msg 6879: a <c>processing-instruction(xml)</c> step would construct an
    /// XML declaration. The check is ordinal, so <c>XML</c> and <c>XmL</c> pass
    /// (probe-confirmed against SQL Server 2025, wording included).
    /// </summary>
    internal static SimulatedSqlException ForXmlProcessingInstructionXmlTarget() =>
        new("'xml' is an invalid XML processing instruction target. Possible attempt to construct XML declaration using XML processing instruction constructor. XML declaration construction with FOR XML is not supported.", 6879, 16, 1);

    /// <summary>
    /// Msg 9322: a value placed in a <c>comment()</c> step carries a <c>--</c>
    /// (state 2) or ends in a <c>-</c> (state 3), either of which would close
    /// or corrupt the comment constructor. Real raises this while serializing —
    /// per row, on the value — and leaves the rest of the comment content
    /// unescaped. Probe-confirmed wording and both states against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlCommentDashes(bool trailing) =>
        new("Two consecutive '-' can only appear in a comment constructor if they are used to close the comment ('-->').", 9322, 16, trailing ? (byte)3 : (byte)2);

    /// <summary>The Msg 6850 leading word for each name position.</summary>
    private static string NameKindWord(ForXmlNameKind kind) => kind switch
    {
        ForXmlNameKind.Column => "Column",
        // Real leaves this one empty, so the message leads with a space.
        ForXmlNameKind.ProcessingInstructionTarget => "",
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
    /// Msg 102 reported against the <c>XML</c> keyword: a <c>FOR XML</c> clause
    /// writes the same option twice. Real re-parses the option list with a
    /// grammar that admits each option once, so the position it names is the
    /// clause's own keyword rather than the repeated word (probe-confirmed
    /// against SQL Server 2025 for <c>TYPE</c>, <c>ROOT</c>, <c>ELEMENTS</c>
    /// and <c>BINARY BASE64</c> alike).
    /// </summary>
    internal static SimulatedSqlException ForXmlDuplicateOption() =>
        new("Incorrect syntax near 'XML'.", 102, 15, 1);

    /// <summary>
    /// Msg 6859 (severity 15): a row tag argument — <c>AUTO('x')</c> /
    /// <c>EXPLICIT('x')</c> — on a mode that names its elements itself.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlRowTagNotAllowedInMode() =>
        new("Row tag name is only allowed with RAW or PATH mode of FOR XML.", 6859, 15, 1);

    /// <summary>
    /// Msg 6825: the <c>ELEMENTS</c> option on <c>FOR XML EXPLICIT</c>, whose
    /// element-versus-attribute placement comes from the column names instead.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlElementsNotAllowedInMode() =>
        new("ELEMENTS option is only allowed in RAW, AUTO, and PATH modes of FOR XML.", 6825, 16, 1);

    /// <summary>
    /// Msg 3625 state 17: <c>FOR XML EXPLICIT, XMLSCHEMA</c> — real's own
    /// "not yet implemented" rejection of inline XSD for the universal-table
    /// format. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitInlineSchemaNotImplemented() =>
        new("'Inline XSD for FOR XML EXPLICIT' is not yet implemented.", 3625, 16, 17);

    /// <summary>
    /// Msg 6801: a <c>FOR XML EXPLICIT</c> projection is shorter than the
    /// universal table's minimum — <c>Tag</c>, <c>Parent</c> and one data
    /// column. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitNeedsThreeColumns() =>
        new("FOR XML EXPLICIT requires at least three columns, including the tag column, the parent column, and at least one data column.", 6801, 16, 1);

    /// <summary>
    /// Msg 6802: a <c>FOR XML EXPLICIT</c> data column's name doesn't follow
    /// the <c>ElementName!TagNumber[!AttributeName[!Directive…]]</c> convention
    /// — no <c>!</c> at all, an unnamed column, an empty element name, or a tag
    /// number that isn't a positive integer. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitInvalidColumnName(string name) =>
        new($"FOR XML EXPLICIT query contains the invalid column name '{name}'. Use the TAGNAME!TAGID!ATTRIBUTENAME[!..] format where TAGID is a positive integer.", 6802, 16, 1);

    /// <summary>
    /// Msg 6803: the <c>Tag</c> column isn't usable. State 1 is the compile-time
    /// type check (it must be <c>int</c>, so <c>bigint</c> / <c>smallint</c> /
    /// a string all fail), state 2 the per-row value check (NULL or not
    /// positive). Probe-confirmed wording and state split against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitTagColumn(byte state) =>
        new("FOR XML EXPLICIT requires the first column to hold positive integers that represent XML tag IDs.", 6803, 16, state);

    /// <summary>
    /// Msg 6804: the <c>Parent</c> column isn't usable — state 1 for the
    /// compile-time <c>int</c> type check, state 2 for a negative value in a
    /// row. Probe-confirmed wording and state split against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitParentColumn(byte state) =>
        new("FOR XML EXPLICIT requires the second column to hold NULL or nonnegative integers that represent XML parent tag IDs.", 6804, 16, state);

    /// <summary>
    /// Msg 6805 state 2: a row would open a tag that is already an ancestor of
    /// itself. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitCircularTags() =>
        new("FOR XML EXPLICIT stack overflow occurred. Circular parent tag relationships are not allowed.", 6805, 16, 2);

    /// <summary>
    /// Msg 6806 state 2: a row's <c>Tag</c> value names a tag number no column
    /// declared. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitUndeclaredTag(int tagId) =>
        new($"Undeclared tag ID {tagId.ToString(CultureInfo.InvariantCulture)} is used in a FOR XML EXPLICIT query.", 6806, 16, 2);

    /// <summary>
    /// Msg 6807 state 2: a row's <c>Parent</c> value names a tag number no
    /// column declared. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitUndeclaredParentTag(int tagId) =>
        new($"Undeclared parent tag ID {tagId.ToString(CultureInfo.InvariantCulture)} is used in a FOR XML EXPLICIT query.", 6807, 16, 2);

    /// <summary>
    /// Msg 6812: two columns give one tag number different element names. The
    /// comparison is ordinal, so a case difference collides too (probe-confirmed
    /// against SQL Server 2025, wording included).
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitTagRedeclared(int tagId, string declared, string redeclared) =>
        new($"XML tag ID {tagId.ToString(CultureInfo.InvariantCulture)} that was originally declared as '{declared}' is being redeclared as '{redeclared}'.", 6812, 16, 1);

    /// <summary>
    /// Msg 6813: a column carries two of the identity directives. Probe-confirmed
    /// wording — the stray "and/or" included — against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitConflictingIdDirectives(string column) =>
        new($"FOR XML EXPLICIT cannot combine multiple occurrences of ID, IDREF, IDREFS, NMTOKEN, and/or NMTOKENS in column name '{column}'.", 6813, 16, 1);

    /// <summary>
    /// Msg 6835: a column writes <c>hide</c> twice. Real checks this ahead of
    /// every other directive-combination rule (probe-confirmed), and words it
    /// as a "field" where its siblings say "column name".
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitDuplicateHide(string column) =>
        new($"FOR XML EXPLICIT field '{column}' can specify the directive HIDE only once.", 6835, 16, 1);

    /// <summary>
    /// Msg 6815: a column carries both <c>hide</c> and one of the identity
    /// directives. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitIdCannotHide(string column) =>
        new($"In the FOR XML EXPLICIT clause, ID, IDREF, IDREFS, NMTOKEN, and NMTOKENS attributes cannot be hidden in '{column}'.", 6815, 16, 1);

    /// <summary>
    /// Msg 6817: a column carries two of the mutually exclusive content
    /// directives. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitConflictingDirectives(string column) =>
        new($"FOR XML EXPLICIT cannot combine multiple occurrences of ELEMENT, XML, XMLTEXT, and CDATA in column name '{column}'.", 6817, 16, 1);

    /// <summary>
    /// Msg 6820: the universal table's first two columns must be named
    /// <c>Tag</c> and <c>Parent</c> (the comparison is case-insensitive; the
    /// message spells the expected name in upper case).
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitColumnMisnamed(int position, string expected, string actual) =>
        new($"FOR XML EXPLICIT requires column {position.ToString(CultureInfo.InvariantCulture)} to be named '{expected}' instead of '{actual}'.", 6820, 16, 1);

    /// <summary>
    /// Msg 6824: a column name's fourth-or-later segment isn't a directive the
    /// mode knows (the empty string included). Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitInvalidDirective(string directive) =>
        new($"In the FOR XML EXPLICIT clause, mode '{directive}' in a column name is invalid.", 6824, 16, 1);

    /// <summary>
    /// Msg 6826: an <c>idrefs</c> / <c>nmtokens</c> column. Real admits one
    /// only where its expression is statically nullable — the shape that feeds
    /// the values in from a separate <c>SELECT</c> of the union — and reports
    /// this otherwise. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitIdrefsNeedsSeparateSelect() =>
        new("Every IDREFS or NMTOKENS column in a FOR XML EXPLICIT query must appear in a separate SELECT clause, and the instances must be ordered directly after the element to which they belong.", 6826, 16, 1);

    /// <summary>
    /// Msg 6827: a second <c>xmltext</c> column on one tag. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitDuplicateXmlText(string column) =>
        new($"FOR XML EXPLICIT queries allow only one XMLTEXT column per tag. Column '{column}' declares another XMLTEXT column that is not permitted.", 6827, 16, 1);

    /// <summary>
    /// Msg 6833: a row's <c>Parent</c> names a declared tag that isn't among
    /// the elements the preceding rows left open — the universal table is out
    /// of tree order. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitParentNotOpen(int tagId) =>
        new($"Parent tag ID {tagId.ToString(CultureInfo.InvariantCulture)} is not among the open tags. FOR XML EXPLICIT requires parent tags to be opened first. Check the ordering of the result set.", 6833, 16, 1);

    /// <summary>
    /// Msg 6834: an <c>xmltext</c> column's value isn't a document with a root
    /// element. State 1 is text that parses but has no element, state 2 markup
    /// that doesn't parse; <paramref name="field"/> is the column's attribute
    /// name (empty for the unnamed overflow form). Probe-confirmed wording and
    /// state split against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitXmlTextInvalid(string field, byte state) =>
        new($"XMLTEXT field '{field}' contains an invalid XML document. Check the root tag and its attributes.", 6834, 16, state);

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
    /// Msg 6306: the argument carries no expression at all — an empty or
    /// whitespace-only string, which every XML method reports the same way.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryExpressionMissing() =>
        new("Invalid XQuery expression passed to XML data type method.", 6306, 16, 1);

    /// <summary>
    /// Msg 2209: the XML-DML text fails to parse, naming the token at fault
    /// (<c>&lt;eof&gt;</c> when the text ran out). Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlSyntaxError(string token) =>
        XQuerySyntaxError("modify", token);

    /// <summary>
    /// Msg 9315: a computed <c>element</c> / <c>attribute</c> constructor whose
    /// name is written as a <c>{…}</c> expression. Real takes only the constant
    /// QName form (<c>element n {…}</c>) and reports this for every braced name,
    /// a string literal included. Probe-confirmed wording against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryComputedNameNotConstant(string method) =>
        new(
            $"XQuery [{method}()]: Only constant expressions are supported for the name expression of computed element and attribute constructors.",
            9315,
            16,
            1);

    /// <summary>
    /// Msg 9325 / 9326: the computed processing-instruction and comment
    /// constructors, which real parses and refuses in every XML method,
    /// <c>.modify()</c>'s insert content included. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryComputedConstructorNotSupported(string method, bool isComment) =>
        isComment
            ? new($"XQuery [{method}()]: Computed comment constructors are not supported.", 9326, 16, 1)
            : new($"XQuery [{method}()]: Computed processing instruction constructors are not supported.", 9325, 16, 1);

    /// <summary>
    /// Msg 2209: an XQuery expression fails to parse. Every XML method's
    /// diagnostics name the method that carried the expression
    /// (<c>XQuery [value()]:</c> …), probe-confirmed across all five.
    /// </summary>
    internal static SimulatedSqlException XQuerySyntaxError(string method, string token) =>
        new($"XQuery [{method}()]: Syntax error near '{token}'", 2209, 16, 1);

    /// <summary>
    /// Msg 9303: a construct whose grammar names the one word that belongs at
    /// the cursor — <c>if</c>'s <c>then</c> / <c>else</c>, a quantified
    /// expression's <c>in</c> / <c>satisfies</c>, a FLWOR's <c>return</c>, a
    /// predicate's <c>]</c>. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQuerySyntaxErrorExpecting(string method, string token, string expected) =>
        new($"XQuery [{method}()]: Syntax error near '{token}', expected '{expected}'.", 9303, 16, 1);

    /// <summary>
    /// Msg 9332: what follows a FLWOR's <c>for</c> / <c>let</c> clauses is none
    /// of the three that may. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryFlworClauseExpected(string method, string token) =>
        new(
            $"XQuery [{method}()]: Syntax error near '{token}', expected 'where', '(stable) order by' or 'return'.",
            9332,
            16,
            1);

    /// <summary>
    /// Msg 2205: a word the grammar requires is missing outright — a
    /// <c>replace value of</c> without its <c>with</c>, a <c>for</c> binding
    /// without its <c>in</c>, a <c>let</c> binding without its <c>:=</c>.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryTokenExpected(string method, string token) =>
        new($"XQuery [{method}()]: \"{token}\" was expected.", 2205, 16, 1);

    /// <summary>
    /// Msg 2227: a <c>$</c>-variable reference no enclosing <c>for</c> /
    /// <c>let</c> / quantified binding introduced. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryVariableNotFound(string method, string name) =>
        new($"XQuery [{method}()]: The variable '${name}' was not found in the scope in which it was referenced.", 2227, 16, 1);

    /// <summary>
    /// Msg 2204: a condition — an <c>if</c> test, a <c>where</c>, a
    /// <c>satisfies</c> body, an <c>and</c> / <c>or</c> operand, a
    /// <c>not()</c> argument — whose static type is neither boolean nor a node
    /// sequence. Unlike a predicate (Msg 2203) a numeric one is refused too.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryConditionNotBoolean(string method, string staticType) =>
        new(
            $"XQuery [{method}()]: Only 'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' "
            + $"expressions allowed in conditions and with logical operators, found '{staticType}'",
            2204,
            16,
            1);

    /// <summary>
    /// Msg 2210: a sequence — a comma list or an <c>if</c>'s two branches —
    /// mixing nodes with atomic values. The message names the atomic type
    /// first whichever side wrote it (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XQueryHeterogeneousSequence(string method, string atomicType, string nodeType) =>
        new($"XQuery [{method}()]: Heterogeneous sequences are not allowed: found '{atomicType}' and '{nodeType}'", 2210, 16, 1);

    /// <summary>
    /// Msg 2371: <c>position()</c> or <c>last()</c> outside a predicate, where
    /// there is no sequence for it to read. Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryPositionOutsidePredicate(string method, string localName) =>
        new($"XQuery [{method}()]: '{localName}()' can only be used within a predicate or XPath selector", 2371, 16, 1);

    /// <summary>
    /// Msg 2373: a node constructor in a method that can't take one —
    /// <c>value()</c>, which would have to atomize it, and <c>nodes()</c>,
    /// which would have to address it. Real words the two differently
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XQueryConstructedXmlNotSupported(string method, string detail) =>
        new($"XQuery [{method}()]: {detail} is not supported with constructed XML", 2373, 16, 1);

    /// <summary>
    /// Msg 2203: a predicate's static type is neither numeric (positional),
    /// boolean (a filter) nor a node sequence (an existence test).
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryPredicateNotBooleanOrNumeric(string method, string staticType) =>
        new(
            $"XQuery [{method}()]: Only 'http://www.w3.org/2001/XMLSchema#decimal?', "
            + $"'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*' expressions allowed as predicates, found '{staticType}'",
            2203,
            16,
            1);

    /// <summary>
    /// Msg 2234: a comparison's two operands have known, incompatible static
    /// types (<c>"a" = 1</c>). Untyped operands take their type from the other
    /// side, so only a typed pair can mismatch. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryOperatorTypeMismatch(string method, string op, string leftType, string rightType) =>
        new($"XQuery [{method}()]: The operator \"{op}\" cannot be applied to \"{leftType}\" and \"{rightType}\" operands.", 2234, 16, 1);

    /// <summary>
    /// Msg 2229: a name test or function name carries a prefix the prolog never
    /// declared. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryUndeclaredNamespace(string method, string prefix) =>
        new($"XQuery [{method}()]: The name \"{prefix}\" does not denote a namespace.", 2229, 16, 1);

    /// <summary>
    /// Msg 2389: a construct that admits at most one item — a value comparison,
    /// a singleton-parameter function, or <c>value()</c> itself — got an
    /// operand real types as a sequence. Real settles this from the path's
    /// shape, so it fires whatever the instance holds. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryNotSingleton(string method, string construct, string staticType) =>
        new($"XQuery [{method}()]: '{construct}' requires a singleton (or empty sequence), found operand of type '{staticType}'", 2389, 16, 1);

    /// <summary>
    /// Msg 2236: a function call short of its declared arity. Probe-confirmed
    /// wording — double-quoted name, sentence-final period — against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryTooFewArguments(string method, string localName) =>
        new($"XQuery [{method}()]: There are not enough actual arguments in the call to function \"{localName}()\".", 2236, 16, 1);

    /// <summary>
    /// Msg 2238: a function call past its declared arity. Real writes this one
    /// with a single-quoted name and no period. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryTooManyArguments(string method, string localName) =>
        new($"XQuery [{method}()]: Too many arguments in call to function '{localName}()'", 2238, 16, 1);

    /// <summary>
    /// Msg 2395: a function name the XQuery library doesn't carry. The message
    /// spells the resolved namespace before the local name. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQueryNoSuchFunction(string method, string namespaceUri, string localName) =>
        new($"XQuery [{method}()]: There is no function '{{{namespaceUri}}}:{localName}()'", 2395, 16, 1);

    /// <summary>
    /// Msg 9335: an XQuery operator real parses but refuses to evaluate
    /// (<c>to</c>, <c>union</c>, <c>intersect</c>, <c>except</c>,
    /// <c>treat as</c>, <c>castable as</c>). Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XQuerySyntaxNotSupported(string method, string construct) =>
        new($"XQuery [{method}()]: The XQuery syntax '{construct}' is not supported.", 9335, 16, 1);

    /// <summary>
    /// Msg 2205: a <c>replace value of</c> has no <c>with</c> clause.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlWithExpected() => XQueryTokenExpected("modify", "with");

    /// <summary>
    /// Msg 2337: the <c>replace value of</c> target isn't statically at most
    /// one node. Real types the path off its shape alone, so only a
    /// <c>(…)[n]</c> wrapper makes a step singular. Probe-confirmed wording
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceTargetNotSingleton(string method, string staticType) =>
        new($"XQuery [{method}()]: The target of 'replace' must be at most one node, found '{staticType}'", 2337, 16, 1);

    /// <summary>
    /// Msg 2356: the <c>replace value of</c> target is a node whose value can't
    /// be written — an untyped element rather than an attribute or a
    /// <c>text()</c> node. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceTargetNotSimpleContent(string method, string staticType) =>
        new($"XQuery [{method}()]: The target of 'replace value of' must be a non-metadata attribute or an element with simple typed content, found '{staticType}'", 2356, 16, 1);

    /// <summary>
    /// Msg 9310: the <c>with</c> clause of a <c>replace value of</c> holds an
    /// XML constructor rather than a value. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceWithConstructedXml(string method) =>
        new($"XQuery [{method}()]: The 'with' clause of 'replace value of' cannot contain constructed XML.", 9310, 16, 1);

    /// <summary>
    /// Msg 2226: the <c>insert</c> target isn't statically a single node.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlInsertTargetNotSingleton(string method, string staticType) =>
        new($"XQuery [{method}()]: The target of 'insert' must be a single node, found '{staticType}'", 2226, 16, 1);

    /// <summary>
    /// Msg 2240: an <c>insert … into</c> names something other than an element
    /// or the document node. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlInsertIntoTargetKind(string method, string staticType) =>
        new($"XQuery [{method}()]: The target of 'insert into' must be an element/document node, found '{staticType}'", 2240, 16, 1);

    /// <summary>
    /// Msg 2249: an <c>insert … before</c> / <c>after</c> names a node kind
    /// that has no siblings to sit among. Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlInsertBeforeAfterTargetKind(string method, string staticType) =>
        new($"XQuery [{method}()]: The target of 'insert before/after' must be an element/PI/comment/text node, found '{staticType}'", 2249, 16, 1);

    /// <summary>
    /// Msg 2258: an attribute constructor is inserted with a positional
    /// keyword — attributes have no document order to sit in. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlAttributeInsertHasPosition(string method, string staticType) =>
        new($"XQuery [{method}()]: The position may not be specified when inserting an attribute node, found '{staticType}'", 2258, 16, 1);

    /// <summary>
    /// Msg 2207: an <c>insert</c>'s content is an atomic value rather than a
    /// node. Probe-confirmed wording — including the sentence-final period and
    /// the double-quoted type — against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlOnlyNodesInsertable(string method, string staticType) =>
        new($"XQuery [{method}()]: Only non-document nodes can be inserted. Found \"{staticType}\".", 2207, 16, 1);

    /// <summary>
    /// Msg 2264: a <c>delete</c> names the document node or an atomic value.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlDmlOnlyNodesDeletable(string method, string staticType) =>
        new($"XQuery [{method}()]: Only non-document nodes may be deleted, found '{staticType}'", 2264, 16, 1);

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

    /// <summary>
    /// Msg 6926: an element or attribute's text isn't a valid value of the
    /// type the schema declares for it — a facet violation and an out-of-range
    /// value included. Real's <paramref name="location"/> is its own XPath-ish
    /// trail: <c>/*:r[1]/*:a[1]</c> for an element, <c>/*:r[1]/@*:k</c> for an
    /// attribute (probed against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlValidationInvalidSimpleTypeValue(string value, string location) =>
        new($"XML Validation: Invalid simple type value: '{value}'. Location: {location}", 6926, 16, 1);

    /// <summary>
    /// Msg 6965: an element appeared where the content model expected a
    /// different one — an undeclared child, or a declared one out of order.
    /// Real's wording ends with a period after the location, which the rest of
    /// the family doesn't.
    /// </summary>
    internal static SimulatedSqlException XmlValidationUnexpectedElement(string expected, string found, string location) =>
        new(
            $"XML Validation: Invalid content. Expected element(s): '{expected}'. Found: element '{found}' instead. Location: {location}.",
            6965,
            16,
            1);

    /// <summary>
    /// Msg 6923: an element the content model allows, but more times than its
    /// <c>maxOccurs</c> admits — reported against the offending occurrence's
    /// own ordinal.
    /// </summary>
    internal static SimulatedSqlException XmlValidationTooManyOccurrences(string name, string location) =>
        new($"XML Validation: Unexpected element(s): {name}. Location: {location}", 6923, 16, 1);

    /// <summary>
    /// Msg 6908: the content model still required an element when the parent
    /// ended. Named against the parent, not the missing child.
    /// </summary>
    internal static SimulatedSqlException XmlValidationIncompleteContent(string expected, string location) =>
        new($"XML Validation: Invalid content. Expected element(s): '{expected}'. Location: {location}", 6908, 16, 1);

    /// <summary>Msg 6905 state 3: an attribute the element's type doesn't declare.</summary>
    internal static SimulatedSqlException XmlValidationAttributeNotPermitted(string name, string location) =>
        new($"XML Validation: Attribute '{name}' is not permitted in this context. Location: {location}", 6905, 16, 3);

    /// <summary>Msg 6906: an attribute declared <c>use="required"</c> that the element didn't write.</summary>
    internal static SimulatedSqlException XmlValidationRequiredAttributeMissing(string name, string location) =>
        new($"XML Validation: Required attribute '{name}' is missing. Location: {location}", 6906, 16, 1);

    /// <summary>
    /// Msg 6913: no global element declaration matches the instance's own
    /// element — the error a document whose root the collection never declared
    /// takes, including one written in no namespace against a qualified schema.
    /// A no-namespace name is reported bare, a qualified one as
    /// <c>{uri}local</c>.
    /// </summary>
    internal static SimulatedSqlException XmlValidationDeclarationNotFound(string name, string location) =>
        new($"XML Validation: Declaration not found for element '{name}'. Location: {location}", 6913, 16, 1);

    /// <summary>
    /// Msg 6909: character data inside an element whose type declares element-only
    /// content. Real names the containing element, and the text's own position
    /// within it doesn't change the message.
    /// </summary>
    internal static SimulatedSqlException XmlValidationTextNotAllowed(string location) =>
        new(
            "XML Validation: Text node is not allowed at this location, the type was defined with element only content or with simple content. Location: "
                + location,
            6909,
            16,
            1);
}
