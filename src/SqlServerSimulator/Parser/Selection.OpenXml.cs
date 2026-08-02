using System.Xml;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>OPENXML(@hdoc, 'rowpattern' [, flags]) [WITH (&lt;schema&gt; | &lt;table&gt;)]</c>
/// — the pre-<c>OPENJSON</c> XML rowset, read over a document
/// <c>sp_xml_preparedocument</c> put in the session's store. Built as a
/// <see cref="Selection"/> factory so the FROM-source machinery (alias,
/// qualifier, lateral re-execution per outer row) reuses the derived-table
/// codepath, exactly as <c>OPENJSON</c> does.
/// </summary>
/// <remarks>
/// The rowpattern selects the row nodes and each column's colpattern selects a
/// value relative to one of them; both are XPath 1.0, so both run straight
/// through the DOM's own engine rather than the XQuery-subset translator the
/// <c>xml</c> type's methods use. The deep-dive is in <c>docs/claude/xml.md</c>.
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// The edge table <c>OPENXML</c> projects with no <c>WITH</c> clause: one
    /// row per node in each matched node's subtree. Column names and types are
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    private static readonly string[] OpenXmlEdgeColumnNames =
        ["id", "parentid", "nodetype", "localname", "prefix", "namespaceuri", "datatype", "prev", "text"];

    private static readonly SqlType[] OpenXmlEdgeSchema = BuildOpenXmlEdgeSchema();

    /// <summary>
    /// The edge schema, laid out once — a fresh array per plan would defeat the
    /// row-layout cache's array-identity key.
    /// </summary>
    private static SqlType[] BuildOpenXmlEdgeSchema()
    {
        var name = NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit);
        return [SqlType.BigInt, SqlType.BigInt, SqlType.Int32, name, name, name, name, SqlType.BigInt, SqlType.NText];
    }

    /// <summary>
    /// Builds the plan for one <c>OPENXML</c> source. <paramref name="columns"/>
    /// null selects the edge table.
    /// </summary>
    public static Selection FromOpenXml(Expression handle, Expression rowPattern, Expression? flags, OpenXmlColumn[]? columns)
    {
        var schema = columns is null ? OpenXmlEdgeSchema : Array.ConvertAll(columns, c => c.Type);
        var columnNames = columns is null ? OpenXmlEdgeColumnNames : Array.ConvertAll(columns, c => c.Name);
        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateOpenXmlRows(handle, rowPattern, flags, columns, schema, batch, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateOpenXmlRows(
        Expression handle, Expression rowPattern, Expression? flags, OpenXmlColumn[]? columns, SqlType[] schema,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);

        // A NULL handle reports handle 0 — real coerces before it looks the
        // document up (probe-confirmed).
        var handleValue = handle.Run(runtime);
        var handleNumber = handleValue.IsNull ? 0 : ScalarArguments.CoerceProcedureParameter(handleValue, SqlType.Int32);
        if (!batch.Connection.PreparedXmlDocuments.TryGetValue(handleNumber, out var document))
            throw SimulatedSqlException.CouldNotFindPreparedStatement(handleNumber);

        var patternValue = rowPattern.Run(runtime);
        var pattern = patternValue.IsNull ? string.Empty : patternValue.CoerceTo(SqlType.NVarchar).AsString;
        var flagBits = 0;
        if (flags is not null)
        {
            var flagsValue = flags.Run(runtime);
            flagBits = flagsValue.IsNull ? 0 : ScalarArguments.CoerceProcedureParameter(flagsValue, SqlType.Int32);
        }

        var rows = document.SelectRows(pattern);
        foreach (XmlNode rowNode in rows)
        {
            if (columns is null)
            {
                foreach (var edgeRow in EnumerateOpenXmlEdgeRows(document, rowNode, schema))
                    yield return edgeRow;
                continue;
            }
            yield return BuildOpenXmlRow(document, rowNode, columns, schema, flagBits);
        }
    }

    /// <summary>
    /// One edge-table row per node of <paramref name="rowNode"/>'s subtree, in
    /// document order: the node itself, then each attribute followed by its
    /// value text node, then each child's subtree. A matched attribute reports
    /// a NULL <c>parentid</c> — real reads the DOM's <c>parentNode</c>, which
    /// an attribute doesn't have — while one reached as a descendant carries
    /// its owner element's id.
    /// </summary>
    private static IEnumerable<byte[]> EnumerateOpenXmlEdgeRows(PreparedXmlDocument document, XmlNode rowNode, SqlType[] schema)
    {
        return Walk(rowNode, document.IdOf(rowNode.ParentNode));

        IEnumerable<byte[]> Walk(XmlNode node, long? parentId)
        {
            // The document node carries no edge row of its own — a rowpattern
            // of `/` reports the document element's subtree, not a wrapper.
            if (node.NodeType != XmlNodeType.Document)
                yield return EncodeOpenXmlEdgeRow(document, node, parentId, schema);
            var id = document.IdOf(node);
            if (node.Attributes is { } attributes)
            {
                foreach (XmlAttribute attribute in attributes)
                {
                    yield return EncodeOpenXmlEdgeRow(document, attribute, id, schema);
                    if (attribute.FirstChild is { } attributeText)
                        yield return EncodeOpenXmlEdgeRow(document, attributeText, document.IdOf(attribute), schema);
                }
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType is XmlNodeType.XmlDeclaration or XmlNodeType.DocumentType)
                    continue;
                foreach (var row in Walk(child, id))
                    yield return row;
            }
        }
    }

    private static byte[] EncodeOpenXmlEdgeRow(PreparedXmlDocument document, XmlNode node, long? parentId, SqlType[] schema)
    {
        var nameType = schema[3];
        // The edge table's nodetype codes are the DOM's own, which
        // XmlNodeType numbers identically for the kinds a document carries;
        // every character-data kind reports 3.
        var nodeType = PreparedXmlDocument.IsTextual(node.NodeType) ? 3 : (int)node.NodeType;
        var localName = node.NodeType switch
        {
            XmlNodeType.Comment => "#comment",
            XmlNodeType.ProcessingInstruction => node.Name,
            _ when PreparedXmlDocument.IsTextual(node.NodeType) => "#text",
            _ => node.LocalName,
        };

        // A namespace declaration reports the prefix `xmlns` and no namespace
        // URI, whichever half of `xmlns:p` / `xmlns` it is (probe-confirmed).
        var isNamespaceDeclaration = node.NodeType == XmlNodeType.Attribute
            && (node.Prefix.Equals("xmlns", StringComparison.Ordinal) || node.LocalName.Equals("xmlns", StringComparison.Ordinal));
        var prefix = isNamespaceDeclaration ? "xmlns" : node.Prefix;
        var namespaceUri = isNamespaceDeclaration ? string.Empty : node.NamespaceURI;

        // Only character data carries text; an element's content and an
        // attribute's value both live on their own child text node.
        var text = PreparedXmlDocument.IsTextual(node.NodeType) || node.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction
            ? node.Value
            : null;

        SqlValue Name(string? value) =>
            string.IsNullOrEmpty(value) ? SqlValue.Null(nameType) : SqlValue.FromString(nameType, value);
        SqlValue Id(long? value) =>
            value is { } present ? SqlValue.FromInt64(present) : SqlValue.Null(SqlType.BigInt);

        return RowEncoder.EncodeRow(schema,
        [
            Id(document.IdOf(node)),
            Id(parentId),
            SqlValue.FromInt32(nodeType),
            Name(localName),
            Name(prefix),
            Name(namespaceUri),
            SqlValue.Null(nameType),
            // An attribute has no previous sibling in the DOM, so the
            // attribute list never chains — matching real.
            Id(document.IdOf(node.PreviousSibling)),
            text is null ? SqlValue.Null(SqlType.NText) : SqlValue.FromNText(text),
        ]);
    }

    /// <summary>
    /// Maps one matched node onto the <c>WITH</c> schema. A column with no
    /// colpattern is name-matched against an attribute (flags bit 1, and the
    /// default flags 0), a child element (bit 2), or the attribute first and
    /// the element as fallback (flags 3). Bit 8 makes <c>@mp:xmltext</c> report
    /// only what no other column consumed.
    /// </summary>
    private static byte[] BuildOpenXmlRow(PreparedXmlDocument document, XmlNode rowNode, OpenXmlColumn[] columns, SqlType[] schema, int flags)
    {
        var attributeCentric = (flags & 3) == 0 || (flags & 1) != 0;
        var elementCentric = (flags & 2) != 0;
        var notConsumed = (flags & 8) != 0;

        var matches = new XmlNode?[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (column.MetaProperty is not null)
                continue;
            matches[i] = column.ColumnPattern is { } colPattern
                ? FirstMatch(document, rowNode, colPattern)
                : DefaultMatch(rowNode, column.Name, attributeCentric, elementCentric);
        }

        // Every column that read a node consumed it, whether or not the
        // overflow column is present — the flag only decides whether the
        // overflow reports the subtraction.
        var consumed = notConsumed
            ? [.. matches.Where(m => m is not null).Select(m => m!)]
            : new HashSet<XmlNode>();

        var values = new SqlValue[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            values[i] = column.MetaProperty is { } meta
                ? MetaPropertyValue(document, rowNode, meta, consumed, column.Type)
                : matches[i] is { } match
                    ? SqlValue.FromNVarchar(NodeText(match)).CoerceTo(column.Type)
                    : SqlValue.Null(column.Type);
        }
        return RowEncoder.EncodeRow(schema, values);
    }

    /// <summary>The first node a colpattern selects — real takes the first match and ignores the rest.</summary>
    private static XmlNode? FirstMatch(PreparedXmlDocument document, XmlNode rowNode, string colPattern)
    {
        var matched = PreparedXmlDocument.Select(rowNode, colPattern, document.Namespaces);
        return matched is { Count: > 0 } ? matched[0] : null;
    }

    private static XmlNode? DefaultMatch(XmlNode rowNode, string name, bool attributeCentric, bool elementCentric)
    {
        if (attributeCentric && rowNode.Attributes?.GetNamedItem(name) is { } attribute)
            return attribute;
        if (!elementCentric)
            return null;
        foreach (XmlNode child in rowNode.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Element && child.LocalName.Equals(name, StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    /// <summary>An element's value is its concatenated descendant text; every other node kind carries its own.</summary>
    private static string NodeText(XmlNode node) =>
        node.NodeType == XmlNodeType.Element ? node.InnerText : node.Value ?? node.InnerText;

    private static SqlValue MetaPropertyValue(PreparedXmlDocument document, XmlNode rowNode, string meta, HashSet<XmlNode> consumed, SqlType type)
    {
        var parent = rowNode.NodeType == XmlNodeType.Attribute ? ((XmlAttribute)rowNode).OwnerElement : rowNode.ParentNode;
        var isNamespaceDeclaration = rowNode.NodeType == XmlNodeType.Attribute
            && (rowNode.Prefix.Equals("xmlns", StringComparison.Ordinal) || rowNode.LocalName.Equals("xmlns", StringComparison.Ordinal));
        // The document node reports no name of its own (real answers NULL
        // where the DOM would say `#document`).
        var isDocument = rowNode.NodeType == XmlNodeType.Document;
        Span<char> folded = stackalloc char[meta.Length];
        _ = meta.AsSpan().ToUpperInvariant(folded);
        var text = folded switch
        {
            "ID" => document.IdOf(rowNode)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "LOCALNAME" => isDocument ? null : rowNode.LocalName,
            "NAMESPACEURI" => isNamespaceDeclaration || isDocument ? null : rowNode.NamespaceURI,
            "PARENTID" => document.IdOf(parent)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "PARENTLOCALNAME" => parent?.LocalName,
            "PARENTNAMESPACEURI" => parent?.NamespaceURI,
            "PARENTPREFIX" => parent?.Prefix,
            "PREFIX" => isNamespaceDeclaration ? "xmlns" : isDocument ? null : rowNode.Prefix,
            "PREV" => document.IdOf(rowNode.PreviousSibling)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "XMLTEXT" => PreparedXmlDocument.Serialize(rowNode, consumed),
            _ => throw new NotSupportedException($"OPENXML metaproperty '@mp:{meta}' is not modeled."),
        };
        return string.IsNullOrEmpty(text) ? SqlValue.Null(type) : SqlValue.FromNVarchar(text).CoerceTo(type);
    }

    /// <summary>
    /// Parses an <c>OPENXML(…) [WITH (…)]</c> source. Enters with
    /// <see cref="ParserContext.Token"/> on the <c>OPENXML</c> keyword; on
    /// return the cursor sits one past the source's last token, ready for the
    /// caller's alias handling — the same contract
    /// <see cref="ParseOpenJson"/> keeps.
    /// </summary>
    public static Selection ParseOpenXml(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Real's grammar admits one token per argument — no expression
        // combiners, and the handle must be a variable (an integer literal
        // there is Msg 102, probe-confirmed).
        var handle = context.GetNextRequired() is AtPrefixedString handleToken
            ? new VariableReference(handleToken, context)
            : throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        Expression rowPattern = context.GetNextRequired() switch
        {
            AtPrefixedString patternVariable => new VariableReference(patternVariable, context),
            Literal { Value: var literal } when SqlType.IsStringCategory(literal.Type) => new Value(literal),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        Expression? flags = null;
        if (context.GetNextRequired() is Operator { Character: ',' })
        {
            flags = context.GetNextRequired() switch
            {
                AtPrefixedString flagsVariable => new VariableReference(flagsVariable, context),
                Numeric { Value: { IsNull: false } flagsLiteral } => new Value(flagsLiteral),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextRequired();
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        OpenXmlColumn[]? columns = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            columns = ParseOpenXmlWithClause(context);

        return Selection.FromOpenXml(handle, rowPattern, flags, columns);
    }

    /// <summary>
    /// Parses the <c>WITH</c> clause in either form real accepts — an inline
    /// <c>(col type ['colpattern'], …)</c> schema, or the name of a table whose
    /// column list supplies the shape (Msg 208 when it doesn't resolve). On
    /// return the cursor sits one past the clause.
    /// </summary>
    private static OpenXmlColumn[] ParseOpenXmlWithClause(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            return ParseOpenXmlWithTable(context);

        var columns = new List<OpenXmlColumn>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var columnName = columnNameToken.Value;

            context.MoveNextRequired();
            var (qualifiedTypeName, typeNameToken) = TypeNameSynonyms.ReadTypeName(context);
            context.MoveNextRequired();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            if (context.Token is Operator { Character: '(' })
            {
                var lengthToken = context.GetNextRequired();
                declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                    ? numericValue.AsInt32
                    : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                        ? SqlType.MaxLengthSentinel
                        : throw SimulatedSqlException.SyntaxErrorNear(context);

                switch (context.GetNextRequired())
                {
                    case Operator { Character: ',' }:
                        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        declaredScale = scaleValue.AsInt32;
                        if (context.GetNextRequired() is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        break;
                    case Operator { Character: ')' }:
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                context.MoveNextRequired();
            }

            var (resolvedType, _, _) = Simulation.ResolveTypeReference(
                context.Batch, qualifiedTypeName, typeNameToken, declaredMaxLength, declaredScale,
                index: columns.Count + 1, columnName: columnName);

            string? colPattern = null;
            if (context.Token is Literal { Value: var patternLiteral } && SqlType.IsStringCategory(patternLiteral.Type))
            {
                colPattern = patternLiteral.AsString;
                context.MoveNextRequired();
            }

            columns.Add(OpenXmlColumn.Create(columnName, resolvedType, colPattern));

            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        context.MoveNextOptional();
        return [.. columns];
    }

    /// <summary>
    /// The <c>WITH &lt;table&gt;</c> form: the named table's columns become the
    /// rowset's, each name-matched against the row node. Entered with the
    /// cursor already on the first token after <c>WITH</c>.
    /// </summary>
    private static OpenXmlColumn[] ParseOpenXmlWithTable(ParserContext context)
    {
        var tableName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        context.MoveNextOptional();
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableName);

        var columns = new OpenXmlColumn[table.Columns.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = OpenXmlColumn.Create(table.Columns[i].Name, table.Columns[i].Type, colPattern: null);
        return columns;
    }
}

/// <summary>
/// One column of an <c>OPENXML … WITH (…)</c> schema (or of the table the
/// <c>WITH &lt;table&gt;</c> form names). A colpattern that reads a
/// <c>@mp:</c> metaproperty is split out at parse time so the per-row path
/// doesn't re-inspect the string.
/// </summary>
internal sealed class OpenXmlColumn
{
    public readonly string Name;
    public readonly SqlType Type;

    /// <summary>The written colpattern, or null when the column name is the pattern (the flags-driven default mapping).</summary>
    public readonly string? ColumnPattern;

    /// <summary>The metaproperty this column reads (<c>id</c>, <c>xmltext</c>, …), or null for an ordinary node read.</summary>
    public readonly string? MetaProperty;

    private OpenXmlColumn(string name, SqlType type, string? colPattern, string? metaProperty)
    {
        this.Name = name;
        this.Type = type;
        this.ColumnPattern = colPattern;
        this.MetaProperty = metaProperty;
    }

    /// <summary>The <c>@mp:</c> prefix marking a metaproperty colpattern.</summary>
    private const string MetaPropertyPrefix = "@mp:";

    public static OpenXmlColumn Create(string name, SqlType type, string? colPattern) =>
        colPattern is not null && colPattern.StartsWith(MetaPropertyPrefix, StringComparison.OrdinalIgnoreCase)
            ? new OpenXmlColumn(name, type, null, colPattern[MetaPropertyPrefix.Length..])
            : new OpenXmlColumn(name, type, colPattern, null);
}
