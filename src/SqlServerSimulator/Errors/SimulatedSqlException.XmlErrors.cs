using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 493: a <c>.nodes()</c> row column read other
    /// than by one of the four xml methods or an <c>IS [NOT] NULL</c> test
    /// (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException NodesColumnUsedDirectly(string columnName) =>
        new($"The column '{columnName}' that was returned from the nodes() method cannot be used directly. It can only be used with one of the four XML data type methods, exist(), nodes(), query(), and value(), or in IS NULL and IS NOT NULL checks.", 493, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 525: a <c>CAST</c> / <c>CONVERT</c> of a
    /// <c>.nodes()</c> row column, naming the target by its base type — kept
    /// <c>(max)</c>, dropped length or precision otherwise (probed 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException NodesColumnCannotConvert(SqlType target) =>
        new($"The column that was returned from the nodes() method cannot be converted to the data type {target.SqlServerName}{(target is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel } ? "(max)" : "")}. It can only be used with one of the four XML data type methods, exist(), nodes(), query(), and value(), or in IS NULL and IS NOT NULL checks.", 525, 16, 2);


    /// <summary>
    /// The 9400 family: a value converted to <c>xml</c> isn't well-formed.
    /// Probe-confirmed against SQL Server 2025 (2026-09-23): Class 16, State
    /// 1, <c>XML parsing: line L, character C, &lt;detail&gt;</c>, where the
    /// position is the character the parser stopped on (the last one when the
    /// input ran out), counted in characters rather than UTF-16 units and
    /// restarting after each line break. An uncaught one ends the batch and
    /// rolls the transaction back, and a caught one dooms it — the
    /// <c>SET XACT_ABORT ON</c> shape whatever the option says.
    /// </summary>
    internal static SimulatedSqlException XmlParsingFailed(XmlParseError error, int line, int character)
    {
        var (number, detail) = error switch
        {
            XmlParseError.UnexpectedEndOfInput => (9400, "unexpected end of input"),
            XmlParseError.UnrecognizedEncoding => (9401, "unrecognized encoding"),
            XmlParseError.UnableToSwitchEncoding => (9402, "unable to switch the encoding"),
            XmlParseError.UnrecognizedInputSignature => (9403, "unrecognized input signature"),
            XmlParseError.WhitespaceExpected => (9410, "whitespace expected"),
            XmlParseError.SemicolonExpected => (9411, "semicolon expected"),
            XmlParseError.GreaterThanExpected => (9412, "'>' expected"),
            XmlParseError.StringLiteralExpected => (9413, "A string literal was expected"),
            XmlParseError.EqualExpected => (9414, "equal expected"),
            XmlParseError.LessThanInAttributeValue => (9415, "well formed check: no '<' in attribute value"),
            XmlParseError.HexadecimalDigitExpected => (9416, "hexadecimal digit expected"),
            XmlParseError.DecimalDigitExpected => (9417, "decimal digit expected"),
            XmlParseError.IllegalXmlCharacter => (9420, "illegal xml character"),
            XmlParseError.IllegalNameCharacter => (9421, "illegal name character"),
            XmlParseError.IncorrectDocumentSyntax => (9422, "incorrect document syntax"),
            XmlParseError.IncorrectCDataSyntax => (9423, "incorrect CDATA section syntax"),
            XmlParseError.IncorrectCommentSyntax => (9424, "incorrect comment syntax"),
            XmlParseError.EndTagMismatch => (9436, "end tag does not match start tag"),
            XmlParseError.DuplicateAttribute => (9437, "duplicate attribute"),
            XmlParseError.XmlDeclarationNotAtBeginning => (9438, "text/xmldecl not at the beginning of input"),
            XmlParseError.ReservedXmlName => (9439, "namespaces beginning with \"xml\" are reserved"),
            XmlParseError.IncorrectXmlDeclarationSyntax => (9441, "incorrect xml declaration syntax"),
            XmlParseError.UndeclaredEntity => (9448, "well formed check: undeclared entity"),
            XmlParseError.IncorrectProcessingInstructionSyntax => (9451, "incorrect processing instruction syntax"),
            XmlParseError.CDataEndInContent => (9454, "no ']]>' in element content"),
            XmlParseError.IllegalQualifiedNameCharacter => (9455, "illegal qualified name character"),
            XmlParseError.MultipleColons => (9456, "multiple colons in qualified name"),
            XmlParseError.RedeclaredPrefix => (9458, "redeclared prefix"),
            XmlParseError.UndeclaredPrefix => (9459, "undeclared prefix"),
            XmlParseError.EmptyNamespaceUri => (9460, "non default namespace with empty uri"),
            XmlParseError.XmlPrefixRebound => (9464, "XML namespace prefix 'xml' can only be associated with the URI http://www.w3.org/XML/1998/namespace. This URI cannot be used with other prefixes."),
            _ => (9465, "XML namespace prefix 'xmlns' is reserved for use by XML."),
        };
        return new($"XML parsing: line {line.ToString(CultureInfo.InvariantCulture)}, character {character.ToString(CultureInfo.InvariantCulture)}, {detail}", number, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };
    }

    /// <summary>
    /// Msg 6359: a value converted to <c>xml</c> carries a <c>DOCTYPE</c>,
    /// which only <c>CONVERT</c> style 2 admits. Probe-confirmed against SQL
    /// Server 2025 (2026-09-23), with or without an internal subset.
    /// </summary>
    internal static SimulatedSqlException XmlInternalSubsetDtdNotAllowed() =>
        new("Parsing XML with internal subset DTDs not allowed. Use CONVERT with style option 2 to enable limited internal subset DTD support.", 6359, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>Msg 6865: a <c>FOR XML</c> column of a spatial or CLR user-defined type.</summary>
    internal static SimulatedSqlException ForXmlClrType() =>
        new("FOR XML does not support CLR types - cast CLR types explicitly into one of the supported types in FOR XML queries.", 6865, 16, 1);

    /// <summary>
    /// Msg 6810: two columns would write the same attribute on one tag,
    /// named by the second column's alias as written.
    /// </summary>
    internal static SimulatedSqlException ForXmlRepeatedAttribute(string column) =>
        new($"Column name '{column}' is repeated. The same attribute cannot be generated more than once on the same XML tag.", 6810, 16, 1);

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
    /// Msg 6816: a column carries one of the identity directives beside
    /// <c>cdata</c>, <c>xml</c> or <c>xmltext</c>. Real checks it after Msg
    /// 6815 and ahead of Msg 6817 (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitIdCannotBeRaw(string column) =>
        new($"In the FOR XML EXPLICIT clause, ID, IDREF, IDREFS, NMTOKEN, and NMTOKENS attributes cannot be generated as CDATA, XML, or XMLTEXT in '{column}'.", 6816, 16, 1);

    /// <summary>
    /// Msg 6839: a row of a tag declaring an <c>idrefs</c> / <c>nmtokens</c>
    /// column carries a value for the tag's <c>xmltext</c> column — raised as
    /// the row is written, so an empty rowset or NULL overflow values pass
    /// (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForXmlExplicitXmlTextBesideIdrefs(string tag) =>
        new($"FOR XML EXPLICIT does not support XMLTEXT field on tag '{tag}' that has IDREFS or NMTOKENS fields.", 6839, 16, 1);

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
    /// Msg 6826: a row carries an <c>idrefs</c> / <c>nmtokens</c> value with no
    /// element of its tag current to take it, or for a list column ahead of the
    /// one the element's earlier rows moved on to (probed 2026-10-08 against
    /// SQL Server 2025).
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
    /// Msg 2722: an xml method in a <c>PRINT</c> operand, refused while the
    /// batch compiles (probed 2026-09-28 against SQL Server 2025). The
    /// statements that take an expression without a query around it otherwise
    /// admit one (<c>SET</c>, <c>IF</c>, <c>RETURN</c>, <c>TOP</c>), or refuse
    /// the method call's syntax outright (<c>RAISERROR</c>, <c>THROW</c>,
    /// <c>EXEC</c> arguments, <c>WAITFOR</c>).
    /// </summary>
    internal static SimulatedSqlException XmlMethodNotAllowedInContext() =>
        new("Xml data type methods are not allowed in expressions in this context.", 2722, 16, 1);

    /// <summary>
    /// Msg 423: an xml method in a <c>CHECK</c> constraint, followed by Msg
    /// 1750 (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlMethodInCheckConstraint(string tableName) =>
        FollowedByConstraintNotCreated(new($"Xml data type methods are not supported in check constraints. Create a scalar user-defined function to wrap the method invocation. The error occurred at table \"{tableName}\".", 423, 16, 16), state: 0);

    /// <summary>
    /// Msg 435 — or Msg 424 for a table variable's column — an xml method in a
    /// computed column's definition; <paramref name="statement"/> is
    /// <c>CREATE TABLE</c> for every declaring form and <c>ALTER TABLE</c> for
    /// an added column (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlMethodInComputedColumn(string columnName, string tableName, string statement, bool tableVariable) =>
        tableVariable
            ? new($"Xml data type methods are not supported in computed column definitions of table variables and return tables of table-valued functions. The error occurred at column \"{columnName}\", table \"{tableName}\", in the {statement} statement.", 424, 16, 16)
            : new($"Xml data type methods are not supported in computed column definitions. Create a scalar user-defined function to wrap the method invocation. The error occurred at column \"{columnName}\", table \"{tableName}\", in the {statement} statement.", 435, 16, 16);

    /// <summary>
    /// Msg 8172: an XML method argument that must be a string literal — the
    /// XQuery expression, or <c>value()</c>'s target type — is a variable, an
    /// expression or <c>NULL</c>. A parenthesized literal passes. Raised while
    /// the batch compiles (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlMethodArgumentNotStringLiteral(int position, string methodName) =>
        new($"The argument {position} of the XML data type method \"{methodName}\" must be a string literal.", 8172, 16, 1);

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
    /// Msg 344: a method called on a receiver named in three or more parts,
    /// which real reads as a remote function.
    /// </summary>
    internal static SimulatedSqlException RemoteFunctionReference(string written, string firstPart) =>
        new($"Remote function reference '{written}' is not allowed, and the column name '{firstPart}' could not be found or is ambiguous.", 344, 16, 1);

    /// <summary>
    /// Msg 227: a method an <c>xml</c> receiver doesn't have — the five names
    /// match case-sensitively — or <c>.nodes()</c> read as a scalar.
    /// </summary>
    internal static SimulatedSqlException NotAValidFunctionPropertyOrField(string name) =>
        new($"\"{name}\" is not a valid function, property, or field.", 227, 15, 1);

    /// <summary>
    /// Msg 9500: an <c>xml</c> <c>.value()</c> target type the method can't
    /// produce, named as written.
    /// </summary>
    internal static SimulatedSqlException XmlValueTypeInvalid(string written) =>
        new($"The data type '{written}' used in the VALUE method is invalid.", 9500, 16, 1);

    /// <summary>
    /// Msg 6335: a converted xml instance nests past 128 levels — state 102
    /// for an element there, 101 for an attribute or text node.
    /// </summary>
    internal static SimulatedSqlException XmlTooDeep(byte state) =>
        new("XML datatype instance has too many levels of nested nodes. Maximum allowed depth is 128 levels.", 6335, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 6354: an xml instance converted to a string or binary type too short for it.</summary>
    internal static SimulatedSqlException XmlTooLongForTarget() =>
        new("Target string size is too small to represent the XML instance", 6354, 16, 10);

    /// <summary>Msg 6355: an xml instance converted to an ANSI type holds a character its code page can't map.</summary>
    internal static SimulatedSqlException XmlCharacterNotInTargetCodePage() =>
        new("Conversion of one or more characters from XML to target collation impossible", 6355, 16, 1);

    /// <summary>
    /// Msg 6358: a <c>CONVERT</c> to <c>xml</c> with a style other than 0
    /// to 3, checked once the value converting is non-NULL.
    /// </summary>
    internal static SimulatedSqlException XmlStyleInvalid(int style) =>
        new($"{style.ToString(CultureInfo.InvariantCulture)} is not a valid style number when converting to XML.", 6358, 16, 1);

    /// <summary>
    /// Msg 318: a <c>.nodes()</c> rowset without its <c>alias(column)</c>.
    /// </summary>
    internal static SimulatedSqlException TableValuedMethodNeedsAlias() =>
        new("The table (and its columns) returned by a table-valued method need to be aliased.", 318, 15, 0);

    /// <summary>
    /// Msg 5302: <c>.modify()</c> is called on a NULL <c>xml</c> instance.
    /// <paramref name="name"/> is the variable (with its <c>@</c>) or column
    /// as written. Probe-confirmed wording against SQL Server 2025. Like the
    /// json type's, it ends the batch and rolls the transaction back as under
    /// <c>SET XACT_ABORT ON</c>, and dooms it inside <c>TRY</c> (probed
    /// 2026-10-02).
    /// </summary>
    internal static SimulatedSqlException XmlMutatorOnNullValue(string name) =>
        new($"Mutator 'modify()' on '{name}' cannot be called on a null value.", 5302, 16, 1) { AbortsAsUnderXactAbort = true };

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
        new($"XML well-formedness check: Duplicate attribute '{attributeName}'. Rewrite your XQuery so it returns well-formed XML.", 6308, 16, 1) { AbortsAsUnderXactAbort = true };

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
        new($"XML Validation: Invalid simple type value: '{value}'. Location: {location}", 6926, 16, 1) { AbortsAsUnderXactAbort = true };

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
            1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>
    /// Msg 6923: an element the content model allows, but more times than its
    /// <c>maxOccurs</c> admits — reported against the offending occurrence's
    /// own ordinal.
    /// </summary>
    internal static SimulatedSqlException XmlValidationTooManyOccurrences(string name, string location) =>
        new($"XML Validation: Unexpected element(s): {name}. Location: {location}", 6923, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 6911: a child an <c>xsd:all</c> group's member already took,
    /// again — each member takes one. Named against the repeated occurrence.
    /// Probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException XmlValidationDuplicateInAll(string name, string location) =>
        new($"XML Validation: Found duplicate element '{name}' in all content model. Location: {location}", 6911, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 6908: the content model still required an element when the parent
    /// ended. Named against the parent, not the missing child.
    /// </summary>
    internal static SimulatedSqlException XmlValidationIncompleteContent(string expected, string location) =>
        new($"XML Validation: Invalid content. Expected element(s): '{expected}'. Location: {location}", 6908, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 6905 state 3: an attribute the element's type doesn't declare.</summary>
    internal static SimulatedSqlException XmlValidationAttributeNotPermitted(string name, string location) =>
        new($"XML Validation: Attribute '{name}' is not permitted in this context. Location: {location}", 6905, 16, 3) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 6906: an attribute declared <c>use="required"</c> that the element didn't write.</summary>
    internal static SimulatedSqlException XmlValidationRequiredAttributeMissing(string name, string location) =>
        new($"XML Validation: Required attribute '{name}' is missing. Location: {location}", 6906, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 6913: no global element declaration matches the instance's own
    /// element — the error a document whose root the collection never declared
    /// takes, including one written in no namespace against a qualified schema.
    /// A no-namespace name is reported bare, a qualified one as
    /// <c>{uri}local</c>.
    /// </summary>
    internal static SimulatedSqlException XmlValidationDeclarationNotFound(string name, string location) =>
        new($"XML Validation: Declaration not found for element '{name}'. Location: {location}", 6913, 16, 1) { AbortsAsUnderXactAbort = true };

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
            1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>
    /// Msg 2396: a <c>.query()</c> whose result real types as attribute nodes
    /// alone — <c>/r/@a</c>, a computed <c>attribute</c> constructor, a FLWOR
    /// returning attributes. Settled statically, so it fires whatever the
    /// instance holds.
    /// </summary>
    internal static SimulatedSqlException XQueryAttributeOutsideElement(string method) =>
        new($"XQuery [{method}()]: Attribute may not appear outside of an element", 2396, 16, 1);

    /// <summary>
    /// Msg 6307: an attribute node lands where no element can take it — after an
    /// element, comment or processing-instruction child of a constructed
    /// element, or at the top level of a <c>.query()</c> result that also holds
    /// other nodes.
    /// </summary>
    internal static SimulatedSqlException XmlAttributeAfterContent() =>
        new("XML well-formedness check: Attribute cannot appear outside of element declaration. Rewrite your XQuery so it returns well-formed XML.", 6307, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 9322 state 1: a direct comment constructor whose text carries
    /// <c>--</c> or ends in <c>-</c>. The XQuery form of the message carries
    /// the method prefix the <c>FOR XML</c> one doesn't.
    /// </summary>
    internal static SimulatedSqlException XQueryCommentDoubleHyphen(string method) =>
        new($"XQuery [{method}()]: Two consecutive '-' can only appear in a comment constructor if they are used to close the comment ('-->').", 9322, 16, 1);

    /// <summary>
    /// Msg 2294: a direct processing-instruction constructor whose target is
    /// <c>xml</c> in any case.
    /// </summary>
    internal static SimulatedSqlException XQueryProcessingInstructionTargetXml(string method) =>
        new($"XQuery [{method}()]: 'xml' is not allowed as a processing instruction target.", 2294, 16, 1);

    /// <summary>
    /// Msg 2278: a direct constructor's name starts with a character a name
    /// can't — the space in <c>&lt;? p?&gt;</c>.
    /// </summary>
    internal static SimulatedSqlException XQueryTagNameInvalidStart(string method, char character) =>
        new($"XQuery [{method}()]: A tag name may not start with the character '{character}'", 2278, 16, 1);

    /// <summary>
    /// Msg 9313: a direct constructor's attribute value mixing an enclosed
    /// expression with literal text, or holding more than one.
    /// </summary>
    internal static SimulatedSqlException XQueryAttributeValueMixed(string method) =>
        new($"XQuery [{method}()]: This version of the server does not support multiple expressions or expressions mixed with strings in an attribute constructor.", 9313, 16, 1);

    /// <summary>Msg 9316: a computed attribute constructor named <c>xmlns</c>.</summary>
    internal static SimulatedSqlException XQueryComputedAttributeXmlns(string method) =>
        new($"XQuery [{method}()]: Cannot use 'xmlns' in the name expression of computed attribute constructor.", 9316, 16, 1);

    /// <summary>
    /// Msg 2282: an <c>&amp;</c> in a string literal that doesn't open one of
    /// the five predefined entity references or a character reference.
    /// </summary>
    internal static SimulatedSqlException XQueryInvalidEntityReference(string method) =>
        new($"XQuery [{method}()]: Invalid entity reference", 2282, 16, 1);

    /// <summary>
    /// Msg 2283: an entity reference's name ran into a character a name
    /// can't hold before its <c>;</c>.
    /// </summary>
    internal static SimulatedSqlException XQueryEntityReferenceCharacter(string method, char character) =>
        new($"XQuery [{method}()]: The character '{character}' may not be part of an entity reference", 2283, 16, 1);

    /// <summary>Msg 2285: a numeric character reference that names no character.</summary>
    internal static SimulatedSqlException XQueryInvalidNumericEntityReference(string method) =>
        new($"XQuery [{method}()]: Invalid numeric entity reference", 2285, 16, 1);

    /// <summary>
    /// Msg 9301: <c>cast as</c> without the <c>?</c> occurrence indicator, the
    /// only form real accepts.
    /// </summary>
    internal static SimulatedSqlException XQueryCastRequiresOptional(string method) =>
        new($"XQuery [{method}()]: In this version of the server, 'cast as <type>' is not available. Please use the 'cast as <type> ?' syntax.", 9301, 16, 1);

    /// <summary>Msg 2364: a function argument whose static type the parameter takes no implicit conversion from.</summary>
    internal static SimulatedSqlException XQueryCannotImplicitlyConvert(string method, string sourceType, string targetType) =>
        new($"XQuery [{method}()]: Cannot implicitly convert from '{sourceType}' to '{targetType}'", 2364, 16, 1);

    /// <summary>
    /// Msg 2365: a <c>cast as</c> or constructor function whose operand real
    /// types as more than one item — or as the empty sequence, which it names
    /// <c>empty</c> — or whose source type has no conversion to the target.
    /// </summary>
    internal static SimulatedSqlException XQueryCannotConvert(string method, string sourceType, string targetType) =>
        new($"XQuery [{method}()]: Cannot explicitly convert from '{sourceType}' to '{targetType}'", 2365, 16, 1);

    /// <summary>
    /// Msg 9319: a literal cast or constructor argument the target type
    /// doesn't admit, settled while compiling. A value read from the instance
    /// that won't convert answers the empty sequence instead.
    /// </summary>
    internal static SimulatedSqlException XQueryStaticInvalidValue(string method, string value) =>
        new($"XQuery [{method}()]: Static simple type validation: Invalid simple type value '{value}'.", 9319, 16, 1);

    /// <summary>Msg 2232: a sequence type naming an atomic type that doesn't exist.</summary>
    internal static SimulatedSqlException XQueryUndefinedType(string method, string name) =>
        new($"XQuery [{method}()]: The name \"{name}\" does not denote a defined type.", 2232, 16, 1);

    /// <summary>Msg 2392: a named axis step whose axis XQuery doesn't have (<c>namespace::</c> included).</summary>
    internal static SimulatedSqlException XQueryInvalidAxis(string method, string axis) =>
        new($"XQuery [{method}()]: '{axis}::' is not a valid axis", 2392, 16, 1);

    /// <summary>
    /// Msg 2261: a <c>self::</c> step naming an element the preceding step's
    /// static type says can't be there.
    /// </summary>
    internal static SimulatedSqlException XQueryNoSuchElementInType(string method, string name, string staticType) =>
        new($"XQuery [{method}()]: There is no element named '{name}' in the type '{staticType}'.", 2261, 16, 1);

    /// <summary>
    /// Msg 2342: a decimal or integer literal past the 28 integer digits real's
    /// <c>xs:decimal</c> holds (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XQueryInvalidNumericConstant(string method) =>
        new($"XQuery [{method}()]: Invalid numeric constant.", 2342, 16, 1);

    /// <summary>
    /// Msg 2377: an expression whose result the static types settle as empty —
    /// a <c>text()</c> step from an attribute (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException XQueryStaticallyEmpty(string method, string expression) =>
        new($"XQuery [{method}()]: Result of '{expression}' expression is statically 'empty'", 2377, 16, 1);

    /// <summary>
    /// Msg 2219: an attribute step from a node the static type says can't
    /// carry one — the attribute a <c>.nodes()</c> row over attributes stands
    /// on (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XQueryNoSuchAttributeInType(string method, string name, string staticType) =>
        new($"XQuery [{method}()]: There is no attribute named '{name}' in the type '{staticType}'.", 2219, 16, 1);

    /// <summary>
    /// Msg 9308: an arithmetic operand real types as neither numeric nor
    /// untyped — a string or boolean, a <c>sql:variable</c> over a character
    /// type included.
    /// </summary>
    internal static SimulatedSqlException XQueryArithmeticOperandType(string method, string op, string staticType) =>
        new($"XQuery [{method}()]: The argument of '{op}' must be of a single numeric primitive type or 'http://www.w3.org/2004/07/xpath-datatypes#untypedAtomic'. Found argument of type '{staticType}'.", 9308, 16, 1);

    /// <summary>
    /// Msg 9342: a <c>sql:variable</c> / <c>sql:column</c> of type <c>xml</c>
    /// anywhere but an <c>insert</c>'s direct content.
    /// </summary>
    internal static SimulatedSqlException XQuerySqlAccessorXmlNotAllowed(string method) =>
        new($"XQuery [{method}()]: An XML instance is only supported as the direct source of an insert using sql:column/sql:variable.", 9342, 16, 1);

    /// <summary>
    /// Msg 9344: a <c>sql:variable</c> / <c>sql:column</c> over a type with no
    /// XQuery mapping — <c>sql_variant</c>, <c>hierarchyid</c>, the CLR and
    /// spatial types.
    /// </summary>
    internal static SimulatedSqlException XQuerySqlAccessorTypeNotSupported(string method, string sqlTypeName) =>
        new($"XQuery [{method}()]: The SQL type '{sqlTypeName}' is not supported with sql:column() and sql:variable().", 9344, 16, 1);

    /// <summary>
    /// Msg 9501 state 2: a <c>sql:variable</c> naming a variable the batch
    /// never declared. Real writes no method bracket on this one.
    /// </summary>
    internal static SimulatedSqlException XQuerySqlVariableNotFound(string name) =>
        new($"XQuery: Unable to resolve sql:variable('{name}'). The variable must be declared as a scalar TSQL variable.", 9501, 16, 2);

    /// <summary>Msg 9519: a <c>sql:variable</c> argument that doesn't start with <c>@</c>.</summary>
    internal static SimulatedSqlException XQuerySqlVariableNameInvalid(string name) =>
        new($"XQuery: The name supplied to sql:variable('{name}') is not a valid SQL variable name. Variable names must start with the '@' symbol followed by at least one character.", 9519, 16, 1);

    /// <summary>Msg 2225: a <c>sql:</c> accessor whose argument isn't a string literal.</summary>
    internal static SimulatedSqlException XQueryStringLiteralExpected(string method) =>
        new($"XQuery [{method}()]: A string literal was expected", 2225, 16, 1);

    /// <summary>
    /// Msg 2236 for a constructor function or <c>sql:</c> accessor, which real
    /// names without the parentheses a library function's message carries.
    /// </summary>
    internal static SimulatedSqlException XQueryTooFewArgumentsBare(string method, string name) =>
        new($"XQuery [{method}()]: There are not enough actual arguments in the call to function \"{name}\".", 2236, 16, 1);

    /// <summary>
    /// Msg 2238 for a constructor function or <c>sql:</c> accessor, named
    /// without parentheses.
    /// </summary>
    internal static SimulatedSqlException XQueryTooManyArgumentsBare(string method, string name) =>
        new($"XQuery [{method}()]: Too many arguments in call to function '{name}'", 2238, 16, 1);

    /// <summary>
    /// Msg 6325: a <c>replace value of</c> whose <c>with</c> expression
    /// evaluated to the empty sequence without being written <c>()</c>.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceWithEmptySequence() =>
        new("XQuery: Replacing the value of a node with an empty sequence is allowed only if '()' is used as the new value expression. The new value expression evaluated to an empty sequence but it is not '()'.", 6325, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 2374: a function whose parameter real types as nodes given an
    /// atomic argument — <c>number("12")</c>.
    /// </summary>
    internal static SimulatedSqlException XQueryNodeRequired(string method, string function) =>
        new($"XQuery [{method}()]: A node or set of nodes is required for {function}", 2374, 16, 1);

    /// <summary>
    /// Msg 6320: a <c>replace value of</c> over an attribute or element whose
    /// <c>with</c> expression evaluated to the empty sequence — only a text
    /// node (or a nillable element) can take one.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceWithEmptyNotNillable() =>
        new("XQuery: Only nillable elements or text nodes can be updated with empty sequence", 6320, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 9312: a <c>text()</c> step under an element a schema collection
    /// types with simple content, whose value is typed rather than a text node.
    /// </summary>
    internal static SimulatedSqlException XQueryTextOnSimpleTypedElement(string method, string staticType) =>
        new($"XQuery [{method}()]: 'text()' is not supported on simple typed or 'http://www.w3.org/2001/XMLSchema#anyType' elements, found '{staticType}'.", 9312, 16, 1);

    /// <summary>
    /// Msg 2247: the <c>with</c> value of a <c>replace value of</c> over a
    /// typed node isn't of the node's declared type or one derived from it —
    /// <c>xs:integer</c> into an <c>xs:int</c> attribute included, since the
    /// derivation runs the other way.
    /// </summary>
    internal static SimulatedSqlException XmlDmlReplaceValueTypeMismatch(string method, string valueType, string expectedType) =>
        new($"XQuery [{method}()]: The value is of type \"{valueType}\", which is not a subtype of the expected type \"{expectedType}\".", 2247, 16, 1);

    /// <summary>Msg 6901: an <c>xml(DOCUMENT …)</c> target given something other than one top-level element; the state is <c>XmlSchemaValidation.DocumentViolation</c>'s.</summary>
    internal static SimulatedSqlException XmlValidationNotADocument(byte state) =>
        new("XML Validation: XML instance must be a document.", 6901, 16, state)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>
    /// Msg 9336: an XSD construct SQL Server's schema collections refuse — the
    /// identity constraints <c>key</c>, <c>keyref</c> and <c>unique</c>.
    /// </summary>
    internal static SimulatedSqlException XmlSchemaSyntaxNotSupported(string construct) =>
        new($"The XML Schema syntax '{construct}' is not supported.", 9336, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2379: a <c>type</c>, <c>ref</c> or <c>base</c> that isn't an XML name (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaNameNotValid(string name) =>
        new($"The name specified is not a valid XML name :'{name}'", 2379, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2293: an empty <c>xsd:choice</c> whose <c>minOccurs</c> isn't 0 (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaEmptyChoice(string location) =>
        new($"Choice cannot be empty unless minOccurs is 0. Location: '{location}'.", 2293, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2391: an <c>xsd:redefine</c> (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaRedefineNotSupported() =>
        new("Redefining XSD schemas is not supported", 2391, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 2302: a schema collection's text declares one global element,
    /// attribute, type, group or attribute group name twice in a namespace
    /// (probed 2026-10-06 against SQL Server 2025). This and the rest of the
    /// collection's compile errors act as under <c>XACT_ABORT</c>, ending the
    /// batch and rolling the transaction back.
    /// </summary>
    internal static SimulatedSqlException XmlSchemaNameAlreadyDefined(string name) =>
        new($"The name \"{name}\" has already been defined in this scope.", 2302, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 2307 / 2308: a <c>type</c>, <c>base</c>, <c>ref</c>, <c>itemType</c>,
    /// <c>memberTypes</c> or <c>substitutionGroup</c> naming a component the
    /// collection doesn't define — 2308 when the name is in a namespace, the
    /// <c>xml:</c> one included (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlSchemaUndefinedName(string name, string targetNamespace) =>
        targetNamespace.Length == 0
            ? new($"Reference to an undefined name '{name}'", 2307, 16, 1) { AbortsAsUnderXactAbort = true }
            : new($"Reference to an undefined name '{name}' within namespace '{targetNamespace}'", 2308, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2309: a length or digits facet whose <c>value</c> isn't a number (probed 2026-10-06 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaFacetValueNotNumber(string attribute = "value") =>
        new($"The value of \"{attribute}\" is not a valid number.", 2309, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 2297: an element of the XSD namespace the schema language doesn't
    /// have, located as <c>/*:schema[1]/…/*:name[n]</c> (probed 2026-10-07
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlSchemaElementNotValid(string name, string location) =>
        new($"Element <{name}> is not valid at location '{location}'.", 2297, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2298: an attribute an XSD element doesn't take (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaAttributeNotValid(string name, string location) =>
        new($"Attribute '{name}' is not valid at location '{location}'.", 2298, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2299: a global declaration without its <c>name</c> (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaRequiredAttributeMissing(string attribute, string element) =>
        new($"Required attribute \"{attribute}\" of XSD element \"{element}\" is missing.", 2299, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 2305: an element or attribute naming a <c>type</c> and declaring
    /// one inline too, located at the inline one (probed 2026-10-07 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlSchemaTypeSpecifiedTwice(string location) =>
        new($"Element or attribute type specified more than once. Location: '{location}'.", 2305, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2310: one attribute declared twice in a complex type or attribute group (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaAttributeDeclaredTwice(string name) =>
        new($"The attribute \"{name}\" is declared more than once.", 2310, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2312: a boolean XSD attribute whose value isn't one (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaAttributeNotBoolean(string attribute, string value) =>
        new($"The value of attribute '{attribute}' does not conform to the type definition 'http://www.w3.org/2001/XMLSchema#boolean': '{value}'.", 2312, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2313: an enumerated XSD attribute — <c>use</c>, <c>form</c>, <c>processContents</c> … — given another value (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaAttributeValueNotAllowed(string attribute, string value) =>
        new($"The attribute \"{attribute}\" cannot have a value of \"{value}\".", 2313, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2360: a declaration with both a <c>name</c> and a <c>ref</c> (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaNameAndRef(string location) =>
        new($"Cannot have both a 'name' and 'ref' attribute. Location: '{location}'.", 2360, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2366: a simple type whose restriction chain returns to itself (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaCircularDefinition(string name) =>
        new($"\"{name}\" has a circular definition.", 2366, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2382: a particle whose <c>minOccurs</c> exceeds its <c>maxOccurs</c> (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaOccursOutOfOrder(string location) =>
        new($"Invalid combination of minOccurs and maxOccurs values, minOccurs has to be less than or equal to maxOccurs. Location: '{location}'.", 2382, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2386: a <c>totalDigits</c> facet of 0 (probed 2026-10-07 against SQL Server 2025).</summary>
    internal static SimulatedSqlException XmlSchemaTotalDigitsOutOfRange() =>
        new("The value of 'totalDigits' facet is outside of the allowed range", 2386, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 6946 / 6950 / 6951 / 6952: a named simple type's facets that
    /// contradict each other within its own restriction (probed 2026-10-07
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlSchemaFacetsContradict(string typeName, int number)
    {
        var rule = number switch
        {
            6946 => "'minLength' can not be greater than 'maxLength'",
            6950 => "'fractionDigits' can not be greater than 'totalDigits'",
            6951 => "'minInclusive' must be less than or equal to 'maxInclusive' and less than 'maxExclusive'",
            _ => "'minExclusive' must be less than or equal to 'maxExclusive' and less than 'maxInclusive'",
        };
        return new($"Invalid type definition for type '{typeName}', {rule}", number, 16, 1) { AbortsAsUnderXactAbort = true };
    }

    /// <summary>
    /// Msg 2319: a restriction applies a facet its base type doesn't take,
    /// located as <c>/*:schema[1]/*:simpleType[1]/…</c> (probed 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlSchemaFacetNotAllowed(string facet, string location) =>
        new($"This type may not have a '{facet}' facet. Location: '{location}'.", 2319, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 6323: an assignment to a variable typed by a collection altered
    /// since the batch began. Transaction-aborting: real rolls the transaction
    /// back, a <c>TRY</c> never reaches its <c>CATCH</c> and the batch ends
    /// (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlSchemaCollectionAlteredDuringBatch(string variableName) =>
        new($"The xml schema collection for variable '@{variableName}' has been altered while the batch was being executed. Remove all XML schema collection DDL operations it is dependent on from the batch, and re-run the batch.", 6323, 16, 1)
        {
            AbortsTransaction = true,
            TerminatesBatch = true,
        };

    /// <summary>Msg 6347: <c>ALTER XML SCHEMA COLLECTION</c> naming a collection that doesn't resolve or can't be altered.</summary>
    internal static SimulatedSqlException XmlSchemaCollectionCannotBeAltered(string name) =>
        new($"Specified collection '{name}' cannot be altered because it does not exist or you do not have permission.", 6347, 16, 1);

    /// <summary>Msg 2378: an <c>ALTER XML SCHEMA COLLECTION … ADD</c> whose text isn't a schema document.</summary>
    internal static SimulatedSqlException XmlSchemaDocumentExpected() =>
        new("Expected XML schema document", 2378, 16, 1);

    /// <summary>
    /// Msg 6310: an <c>ALTER XML SCHEMA COLLECTION … ADD</c> redeclaring a
    /// global component the collection already has. Real's text carries two
    /// spaces after the first sentence.
    /// </summary>
    internal static SimulatedSqlException XmlSchemaComponentExists(string componentNamespace, string name, string kind) =>
        new($"Altering existing schema components is not allowed.  There was an attempt to modify an existing XML Schema component, component namespace: '{componentNamespace}' component name: '{name}' component kind:{kind}", 6310, 16, 1);

    /// <summary>Msg 6917: <c>xsi:nil="true"</c> on an element not declared <c>nillable</c> (or carrying a fixed value).</summary>
    internal static SimulatedSqlException XmlValidationNilNotAllowed(string name, string location) =>
        new($"XML Validation: Element '{name}' may not have xsi:nil=\"true\" because it was not defined as nillable or because it has a fixed value constraint. Location: {location}", 6917, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>Msg 6918: a nil element that still has content.</summary>
    internal static SimulatedSqlException XmlValidationNilWithContent(string name, string location) =>
        new($"XML Validation: Element '{name}' must not have character or element children, because xsi:nil was set to true. Location: {location}", 6918, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>Msg 6914: an <c>xsi:type</c> naming a type the collection doesn't define.</summary>
    internal static SimulatedSqlException XmlValidationTypeNotFound(string type, string location) =>
        new($"XML Validation: Type definition for type '{type}' was not found, type definition is required before use in a type cast. Location: {location}", 6914, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>Msg 6936: an <c>xsi:type</c> naming a type that doesn't derive from the declared one.</summary>
    internal static SimulatedSqlException XmlValidationInvalidTypeCast(string name, string fromType, string toType, string location) =>
        new($"XML Validation: Invalid cast for element '{name}' from type '{fromType}' to type '{toType}'. Location: {location}", 6936, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };
}
