using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// The fixed single-column name SQL Server assigns a top-level FOR XML
    /// result set (a GUID-shaped sentinel; consumers concatenate the chunks).
    /// </summary>
    private const string ForXmlColumnName = "XML_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>The xsi namespace declared when <c>ELEMENTS XSINIL</c> emits nil elements.</summary>
    internal const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Parses the trailing <c>FOR XML { RAW[('elem')] | AUTO | PATH[('row')] }
    /// [, ELEMENTS [XSINIL|ABSENT]] [, TYPE] [, ROOT[('name')]]</c> clause when
    /// the cursor sits on <c>FOR</c>, wrapping <paramref name="inner"/> in a
    /// serializer that projects the single result column — <c>xml</c> under
    /// <c>TYPE</c>, else the <c>nvarchar(max)</c> string form. A <c>FOR</c>
    /// that isn't <c>XML</c> (<c>FOR BROWSE</c> / leftover) restores the cursor
    /// and returns <paramref name="inner"/> unchanged for the downstream Msg
    /// 102. Leaves the cursor on the first token past the clause.
    /// <paramref name="depth"/> is the enclosing query's nesting depth: only a
    /// statement's own SELECT (depth 0) can be the one an INSERT / SELECT INTO
    /// writes from, so only there does the clause raise Msg 6819.
    /// </summary>
    internal static Selection ParseOptionalForXml(ParserContext context, Selection inner, uint depth)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.For })
            return inner;

        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not Name xmlKeyword || !Collation.Baseline.Equals(xmlKeyword.Value, "XML"))
        {
            context.RestoreCheckpoint(checkpoint);
            return inner;
        }

        if (context.GetNextRequired() is not Name modeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // The statement's WITH XMLNAMESPACES declarations, if any: they scope
        // to every FOR XML clause in the statement, nested subqueries included.
        var namespaces = context.XmlNamespaces;

        ForXmlMode mode;
        string? rowElement;
        Span<char> upper = stackalloc char[modeName.Value.Length];
        _ = modeName.Value.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "AUTO":
                mode = ForXmlMode.Auto;
                rowElement = null;
                RejectForXmlRowTagArgument(context);
                break;
            case "EXPLICIT":
                if (namespaces is not null)
                    throw SimulatedSqlException.ForXmlNamespacesUnsupportedFeature();
                mode = ForXmlMode.Explicit;
                rowElement = null;
                RejectForXmlRowTagArgument(context);
                break;
            case "PATH":
                mode = ForXmlMode.Path;
                rowElement = ParseOptionalForXmlElementName(context, "row");
                break;
            case "RAW":
                mode = ForXmlMode.Raw;
                rowElement = ParseOptionalForXmlElementName(context, "row");
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        var elements = false;
        var xsinil = false;
        var typed = false;
        var binaryBase64 = false;
        var rootSpecified = false;
        var rootName = "root";

        // Real's option grammar admits each option once: a repeated TYPE / ROOT
        // / ELEMENTS / BINARY BASE64 is Msg 102 reported against the clause's
        // own XML keyword (probe-confirmed).
        var seen = ForXmlOptionSeen.None;

        while (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not Name optionName)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (Collation.Baseline.Equals(optionName.Value, "ELEMENTS"))
            {
                RejectRepeatedForXmlOption(ref seen, ForXmlOptionSeen.Elements);
                if (mode == ForXmlMode.Explicit)
                    throw SimulatedSqlException.ForXmlElementsNotAllowedInMode();
                elements = true;
                context.MoveNextOptional();
                if (context.Token is Name modifier && (Collation.Baseline.Equals(modifier.Value, "XSINIL") || Collation.Baseline.Equals(modifier.Value, "ABSENT")))
                {
                    xsinil = Collation.Baseline.Equals(modifier.Value, "XSINIL");
                    context.MoveNextOptional();
                }
            }
            else if (Collation.Baseline.Equals(optionName.Value, "ROOT"))
            {
                RejectRepeatedForXmlOption(ref seen, ForXmlOptionSeen.Root);
                rootSpecified = true;
                context.MoveNextOptional();
                if (context.Token is Operator { Character: '(' })
                {
                    if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } rootLiteral)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    rootName = rootLiteral.Value.AsString;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                }
            }
            else if (Collation.Baseline.Equals(optionName.Value, "BINARY"))
            {
                // BASE64 is the only spelling the grammar admits here — real
                // reports Msg 102 near the offending word for BINARY HEX and
                // near BINARY itself when nothing follows.
                if (context.GetNextRequired() is not Name encoding || !Collation.Baseline.Equals(encoding.Value, "BASE64"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                RejectRepeatedForXmlOption(ref seen, ForXmlOptionSeen.BinaryBase64);
                binaryBase64 = true;
                context.MoveNextOptional();
            }
            else if (Collation.Baseline.Equals(optionName.Value, "TYPE"))
            {
                RejectRepeatedForXmlOption(ref seen, ForXmlOptionSeen.Type);
                typed = true;
                context.MoveNextOptional();
            }
            else if (Collation.Baseline.Equals(optionName.Value, "XMLSCHEMA"))
            {
                if (namespaces is not null)
                    throw SimulatedSqlException.ForXmlNamespacesUnsupportedFeature();
                if (mode == ForXmlMode.Explicit)
                    throw SimulatedSqlException.ForXmlExplicitInlineSchemaNotImplemented();
                throw new NotSupportedException("FOR XML XMLSCHEMA (inline XSD emission) isn't modeled.");
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        // Real settles the statement shape before any name: an INSERT source
        // SELECT raises Msg 6819 even when the projection also carries an
        // unusable name (probe-confirmed), while a syntax error still wins.
        RejectSerializationInWriteStatement(context, inner, depth, forJson: false);

        // XSINIL owns the xsi prefix for its nil markers, so a clause that
        // rebinds the prefix can't be honored (real's Msg 6873).
        if (xsinil && namespaces?.IsDeclared("xsi") == true)
            throw SimulatedSqlException.XmlNamespaceXsiRedefinedWithXsinil();

        // The names written into the clause are validated rather than escaped —
        // the row tag first, then ROOT (real's order).
        if (rowElement is { Length: > 0 })
            ForXmlName.ValidateSimpleName(rowElement, ForXmlNameKind.Row, namespaces);
        if (rootSpecified)
        {
            if (rootName.Length == 0)
                throw SimulatedSqlException.ForXmlEmptyRootTag();
            ForXmlName.ValidateSimpleName(rootName, ForXmlNameKind.Root, namespaces);
        }

        // EXPLICIT's shape lives entirely in the projection: the universal
        // table's column names compile into the tag templates, and whether any
        // of them carries the elementxsinil directive is what decides the xsi
        // declaration the options object precomputes.
        var explicitPlan = mode == ForXmlMode.Explicit ? ForXmlExplicitPlan.Build(inner, binaryBase64) : null;
        if (explicitPlan is not null)
            xsinil = explicitPlan.Xsinil;

        return WrapForXml(inner, new ForXmlOptions(mode, rowElement, elements, xsinil, typed, binaryBase64, rootSpecified ? rootName : null, namespaces), explicitPlan);
    }

    /// <summary>
    /// Raises Msg 6859 when a <c>('name')</c> row-tag argument follows AUTO or
    /// EXPLICIT — both name their elements from the query rather than the
    /// clause. Otherwise advances past the mode keyword.
    /// </summary>
    private static void RejectForXmlRowTagArgument(ParserContext context)
    {
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '(' })
            throw SimulatedSqlException.ForXmlRowTagNotAllowedInMode();
    }

    /// <summary>
    /// Records <paramref name="option"/> as written, raising real's Msg 102
    /// against the <c>XML</c> keyword when the clause already carried it.
    /// </summary>
    private static void RejectRepeatedForXmlOption(ref ForXmlOptionSeen seen, ForXmlOptionSeen option)
    {
        if ((seen & option) != 0)
            throw SimulatedSqlException.ForXmlDuplicateOption();
        seen |= option;
    }

    /// <summary>
    /// Raises real's rejection when a <c>FOR XML</c> / <c>FOR JSON</c> clause
    /// lands on a SELECT whose rows go somewhere other than the client: the
    /// source of an <c>INSERT … SELECT</c> or a <c>SELECT … INTO</c>
    /// (Msg 6819 / Msg 13602), or a variable-assigning <c>SELECT @v = …</c>
    /// (Msg 6819 for both clauses — real reports the FOR XML wording even for
    /// FOR JSON there). Nested scopes are unaffected: the clause is legal in a
    /// derived table, a scalar subquery and a <c>SET @v = (SELECT … FOR XML)</c>
    /// alike, so only <paramref name="depth"/> 0 is checked.
    /// </summary>
    private static void RejectSerializationInWriteStatement(ParserContext context, Selection inner, uint depth, bool forJson)
    {
        if (depth != 0)
            return;
        if (inner.IsAssignmentOnly)
            throw SimulatedSqlException.ForXmlNotAllowedInAssignment();

        var statementKind = context.InInsertSourceSelect ? "INSERT" : inner.IntoTarget is not null ? "SELECT INTO" : null;
        if (statementKind is null)
            return;
        throw forJson
            ? SimulatedSqlException.ForJsonNotAllowedIn(statementKind)
            : SimulatedSqlException.ForXmlNotAllowedIn(statementKind);
    }

    /// <summary>
    /// Reads the optional <c>('name')</c> argument of RAW / PATH, returning the
    /// literal (empty string allowed for PATH) or <paramref name="fallback"/>
    /// when absent. Advances to the first token past the mode / its argument.
    /// </summary>
    private static string ParseOptionalForXmlElementName(ParserContext context, string fallback)
    {
        context.MoveNextOptional();
        if (context.Token is not Operator { Character: '(' })
            return fallback;
        if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } literal)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = literal.Value.AsString;
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return name;
    }

    private static Selection WrapForXml(Selection inner, ForXmlOptions options, ForXmlExplicitPlan? explicitPlan = null)
    {
        var innerSchema = inner.Schema;
        // The TYPE option makes the result a typed xml value instead of the
        // string form: one unnamed xml column (probe-confirmed on the wire),
        // which an enclosing FOR XML then embeds as nodes rather than escaped
        // text. Without it the column carries SQL Server's GUID-shaped
        // sentinel name.
        SqlType[] schema = [options.Typed ? SqlType.Xml : SqlType.NVarcharMax];
        string[] columnNames = [options.Typed ? "" : ForXmlColumnName];

        if (explicitPlan is not null)
        {
            return new Selection(schema, columnNames,
                hasOrderBy: false,
                hasTopOrOffsetOrFetch: false,
                (batch, outerResolver) => SerializeForXmlExplicit(inner, innerSchema, explicitPlan, options, batch, outerResolver));
        }

        if (options.Mode == ForXmlMode.Auto)
        {
            var levels = BuildAutoLevels(inner, forJson: false);
            // Without BINARY BASE64, an AUTO binary column addresses a
            // dbobject URL instead of carrying its bytes; the builder fills a
            // slot per such column (and raises where no URL can be addressed).
            var binaryUrls = options.BinaryBase64 ? null : new ForXmlBinaryUrl?[inner.ColumnNames.Length];
            var levelElements = new ForXmlElement[levels.Length];
            for (var i = 0; i < levels.Length; i++)
            {
                // A level is named after a table or alias as written, so it
                // takes the same escaping a column name does: FROM #tmp emits
                // <_x0023_tmp>.
                levelElements[i] = BuildForXmlFlatElement(ForXmlName.Encode(levels[i].Name), levels[i].Columns, inner, options, binaryUrls);
            }

            var autoOptions = binaryUrls is not null && Array.Exists(binaryUrls, static url => url is not null)
                ? options.WithBinaryUrls(binaryUrls)
                : options;
            return new Selection(schema, columnNames,
                hasOrderBy: false,
                hasTopOrOffsetOrFetch: false,
                (batch, outerResolver) => SerializeForXmlAuto(inner, innerSchema, levels, levelElements, autoOptions, batch, outerResolver));
        }

        var rowElement = BuildForXmlRowElement(inner, options);

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => SerializeForXml(inner, innerSchema, rowElement, options, batch, outerResolver));
    }

    /// <summary>
    /// Compiles the projection into a single per-row element template (shared
    /// across rows; leaves reference result-column indices). RAW builds a flat
    /// attribute- or element-centric wrapper; PATH parses each column alias
    /// into an XPath-like node placement. A PATH('') wrapper has an empty
    /// <see cref="ForXmlElement.Name"/> — its content is emitted with no row
    /// tag. (AUTO builds one flat wrapper per nesting level instead.)
    /// </summary>
    private static ForXmlElement BuildForXmlRowElement(Selection inner, ForXmlOptions options)
    {
        if (options.Mode == ForXmlMode.Path)
        {
            var pathRoot = new ForXmlElement(options.RowElement!);
            for (var i = 0; i < inner.ColumnNames.Length; i++)
                InsertForXmlPath(pathRoot, inner.ColumnNames[i], i, inner.Schema[i], options.RowElement!.Length == 0, options.Namespaces);
            return pathRoot;
        }

        // RAW('') is row-tag omission, which only element-centric
        // serialization can carry (probe-confirmed: RAW(''), ELEMENTS emits
        // <a>1</a> while the attribute-centric default raises).
        if (options.RowElement!.Length == 0 && !options.Elements)
            throw SimulatedSqlException.ForXmlAttributeWithoutRowTag();

        var columns = new int[inner.ColumnNames.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = i;
        return BuildForXmlFlatElement(options.RowElement!, columns, inner, options, binaryUrls: null);
    }

    /// <summary>
    /// Builds one RAW row wrapper / AUTO nesting level: every column must be
    /// named (Msg 6809), a binary column without <c>BINARY BASE64</c> is
    /// refused by RAW (Msg 6829) and addressed as a <c>dbobject</c> URL by AUTO
    /// (filling <paramref name="binaryUrls"/>, or Msg 6830 / 6831 when no URL
    /// can be addressed), and an <c>xml</c>-typed column becomes a child
    /// element holding its nodes even in the default attribute-centric shape
    /// (probe-confirmed — an attribute can't hold nodes).
    /// </summary>
    private static ForXmlElement BuildForXmlFlatElement(
        string name, IReadOnlyList<int> columns, Selection inner, ForXmlOptions options, ForXmlBinaryUrl?[]? binaryUrls)
    {
        var wrapper = new ForXmlElement(name);
        foreach (var i in columns)
        {
            var columnName = inner.ColumnNames[i];
            if (columnName.Length == 0)
                throw SimulatedSqlException.ForXmlUnnamedColumn();
            if (!options.BinaryBase64 && inner.Schema[i] is BinarySqlType or VarbinarySqlType or ImageSqlType)
            {
                if (binaryUrls is null)
                    throw SimulatedSqlException.ForXmlBinaryRaw(columnName);
                binaryUrls[i] = BuildForXmlBinaryUrl(inner, i, columnName);
            }

            // RAW / AUTO escape a name XML can't carry instead of rejecting it,
            // so [a b] becomes a_x0020_b (the errors above still quote the name
            // as written).
            var xmlName = ForXmlName.Encode(columnName);
            if (options.Elements || inner.Schema[i] is XmlSqlType)
            {
                var element = new ForXmlElement(xmlName);
                element.Content.Add(new ForXmlLeaf(i, ForXmlName.ForXmlPathLeaf.Node, null));
                wrapper.Content.Add(element);
            }
            else
            {
                wrapper.Attributes.Add(new ForXmlAttribute(xmlName, i));
            }
        }
        return wrapper;
    }

    /// <summary>
    /// Assembles the <c>dbobject</c> reference <c>FOR XML AUTO</c> addresses a
    /// binary column with when <c>BINARY BASE64</c> is absent. Real needs both
    /// halves of the addressing: an owning base table (else Msg 6830 — an
    /// expression, a derived table's column, a set-operation result) and that
    /// table's whole primary key present in the projection (else Msg 6831).
    /// The reference is written from base names, so a select-list alias on
    /// either the binary column or a key column doesn't show through.
    /// </summary>
    private static ForXmlBinaryUrl BuildForXmlBinaryUrl(Selection inner, int column, string columnName)
    {
        if (inner.AutoColumnSource is not { } columnSource || inner.AutoColumnOrdinal is not { } columnOrdinal
            || inner.BranchFromSources is not { } sources || columnSource[column] < 0)
        {
            throw SimulatedSqlException.ForXmlBinaryAuto(columnName);
        }

        var source = sources[columnSource[column]];
        if (source.BackingTable is not { } table)
            throw SimulatedSqlException.ForXmlBinaryAuto(columnName);

        var key = table.KeyConstraints.Find(static k => k.Kind == KeyConstraintKind.PrimaryKey)
            ?? throw SimulatedSqlException.ForXmlBinaryAutoNeedsPrimaryKey(columnName);

        // Each key column has to be readable off a projected result column of
        // the same source; the storage-ordinal indirection is what a source
        // whose exposed columns are reordered / projected needs.
        var keys = new ForXmlUrlKey[key.StorageOrdinals.Length];
        for (var k = 0; k < keys.Length; k++)
        {
            var keyColumn = -1;
            for (var i = 0; i < columnSource.Length && keyColumn < 0; i++)
            {
                if (columnSource[i] == columnSource[column] && columnOrdinal[i] >= 0
                    && SourceStorageOrdinal(source, columnOrdinal[i]) == key.StorageOrdinals[k])
                {
                    keyColumn = i;
                }
            }
            if (keyColumn < 0)
                throw SimulatedSqlException.ForXmlBinaryAutoNeedsPrimaryKey(columnName);
            keys[k] = new ForXmlUrlKey(ForXmlName.Encode(source.ColumnNames[columnOrdinal[keyColumn]]), keyColumn);
        }

        return new ForXmlBinaryUrl(
            $"dbobject/{ForXmlName.Encode(table.Name)}[",
            keys,
            $"]/@{ForXmlName.Encode(source.ColumnNames[columnOrdinal[column]])}");
    }

    /// <summary>
    /// The storage ordinal one of <paramref name="source"/>'s exposed columns
    /// occupies in its base table's rows — the identity mapping unless the
    /// source projects a subset or a different order.
    /// </summary>
    private static int SourceStorageOrdinal(FromSource source, int columnIndex) =>
        source.StorageOrdinals is { } ordinals ? ordinals[columnIndex] : columnIndex;

    /// <summary>
    /// Renders one <c>dbobject</c> reference for a row: the key predicate's
    /// terms joined by real's URL-escaped <c>%20and%20</c>, each value in its
    /// plain text form (the reference as a whole then takes the position's
    /// ordinary XML escaping).
    /// </summary>
    private static string FormatForXmlBinaryUrl(ForXmlBinaryUrl url, byte[] rowBytes, SqlType[] innerSchema)
    {
        var sb = new StringBuilder(url.Prefix);
        for (var k = 0; k < url.Keys.Length; k++)
        {
            if (k > 0)
                _ = sb.Append("%20and%20");
            _ = sb.Append('@').Append(url.Keys[k].Name).Append("='")
                .Append(ScalarForXmlText(RowDecoder.DecodeColumn(innerSchema, rowBytes, url.Keys[k].Column)))
                .Append('\'');
        }
        return sb.Append(url.Suffix).ToString();
    }

    /// <summary>
    /// Places one PATH column into the row-element template by its alias:
    /// <c>@a</c> → attribute, <c>a/b</c> → nested elements, a node function or
    /// an unnamed column → content of that node kind, a plain name → a leaf
    /// element holding the value. Adjacent same-name element steps merge (so
    /// <c>[x],[x]</c> concatenates and <c>[a/b],[a/c]</c> shares the <c>a</c>
    /// parent). Enforces Msg 6852 (attribute after non-attribute — a comment or
    /// processing instruction counts as content for it), Msg 6864 (attribute
    /// under a suppressed row tag), Msg 6851 (an xml-typed column mapped to an
    /// attribute — an attribute can't hold nodes) and Msg 6853 (an xml-typed
    /// column under a node function with no text form for it).
    /// </summary>
    private static void InsertForXmlPath(
        ForXmlElement root, string alias, int column, SqlType columnType, bool rowTagOmitted, ForXmlNamespaces? namespaces)
    {
        // PATH rejects a name RAW / AUTO would escape (Msg 6850), along with the
        // path-shape, node-function and namespace-prefix rules on it.
        ForXmlName.ValidatePathColumn(alias, namespaces);

        var segments = alias.Length == 0 ? [] : alias.Split('/');
        var leafKind = ForXmlName.ForXmlPathLeaf.Node;
        var processingInstructionTarget = (string?)null;
        var attributeName = (string?)null;
        var descendCount = segments.Length;

        if (segments.Length > 0)
        {
            var leaf = segments[^1];
            if (leaf.StartsWith('@'))
            {
                attributeName = leaf[1..];
                descendCount = segments.Length - 1;
            }
            else
            {
                leafKind = ForXmlName.ClassifyPathLeaf(leaf, out processingInstructionTarget);
                if (leafKind != ForXmlName.ForXmlPathLeaf.Element)
                    descendCount = segments.Length - 1;
            }
        }

        if (attributeName is not null && rowTagOmitted && descendCount == 0)
            throw SimulatedSqlException.ForXmlAttributeWithoutRowTag();
        if (attributeName is not null && columnType is XmlSqlType)
            throw SimulatedSqlException.ForXmlAttributeInvalidType(alias);
        // node() / * take an xml value as nodes; the others have only a text
        // form to write, which an xml instance doesn't have.
        if (columnType is XmlSqlType
            && leafKind is ForXmlName.ForXmlPathLeaf.Text or ForXmlName.ForXmlPathLeaf.Data
                or ForXmlName.ForXmlPathLeaf.Comment or ForXmlName.ForXmlPathLeaf.ProcessingInstruction)
        {
            throw SimulatedSqlException.ForXmlPathLastStepNotApplicable(alias);
        }

        // Descend/merge the element steps that precede the leaf.
        var node = root;
        for (var s = 0; s < descendCount; s++)
            node = DescendForXml(node, segments[s]);

        if (attributeName is not null)
        {
            if (node.HasContent)
                throw SimulatedSqlException.ForXmlAttributeAfterNonAttribute("@" + attributeName);
            node.Attributes.Add(new ForXmlAttribute(attributeName, column));
        }
        else
        {
            // A plain name step descended into its own element above, so the
            // value lands there as ordinary content.
            node.Content.Add(leafKind == ForXmlName.ForXmlPathLeaf.Element
                ? new ForXmlLeaf(column, ForXmlName.ForXmlPathLeaf.Node, null)
                : new ForXmlLeaf(column, leafKind, processingInstructionTarget));
        }
    }

    /// <summary>
    /// Returns the child element named <paramref name="name"/>, reusing the
    /// last content item when it is an element of that name (so contiguous
    /// same-name steps merge) and otherwise appending a fresh child.
    /// </summary>
    private static ForXmlElement DescendForXml(ForXmlElement node, string name)
    {
        if (node.Content.Count > 0 && node.Content[^1] is ForXmlElement last && last.Name == name)
            return last;
        var child = new ForXmlElement(name);
        node.Content.Add(child);
        return child;
    }

    private static IEnumerable<byte[]> SerializeForXml(
        Selection inner, SqlType[] innerSchema, ForXmlElement rowElement,
        ForXmlOptions options, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var sb = new StringBuilder();
        // The xsi and WITH XMLNAMESPACES declarations land on whatever element
        // is outermost: the ROOT wrapper when there is one, else every
        // top-level element the rows produce (probe-confirmed, PATH('')'s
        // per-column elements included — its bare text carries none).
        var topLevelDeclarations = options.RootName is null ? options.Declarations : "";

        if (options.RootName is { } rootName)
            _ = sb.Append('<').Append(rootName).Append(options.Declarations).Append('>');

        var any = false;
        var prevAtomic = false;
        foreach (var rowBytes in inner.Execute(batch, outerResolver).RowBytes)
        {
            any = true;
            if (rowElement.Name.Length == 0)
            {
                // PATH('') — no row wrapper; emit the row's content directly.
                prevAtomic = SerializeForXmlContent(sb, rowElement.Content, rowBytes, innerSchema, options, topLevelDeclarations, prevAtomic);
            }
            else
            {
                SerializeForXmlElement(sb, rowElement, rowBytes, innerSchema, options, topLevelDeclarations, isRowElement: true);
                prevAtomic = false;
            }
        }

        if (!any)
        {
            if (options.Typed)
                yield return EmptyForXmlRow();
            yield break;
        }

        if (options.RootName is { } closeName)
            _ = sb.Append("</").Append(closeName).Append('>');

        yield return ForXmlRow(sb, options);
    }

    /// <summary>
    /// Serializes a <c>FOR XML AUTO</c> projection whose sources nest: one
    /// element per level, opened when a row's values for that level differ
    /// from the previous row's and closed when they change again, so
    /// consecutive rows sharing an outer level's values collapse into one
    /// element with several children. The innermost level restarts on every
    /// row (SQL Server emits one element per row there even for two identical
    /// rows). A single-level projection — the flat AUTO shape — falls out as
    /// the degenerate case.
    /// </summary>
    private static IEnumerable<byte[]> SerializeForXmlAuto(
        Selection inner, SqlType[] innerSchema, AutoLevel[] levels, ForXmlElement[] levelElements,
        ForXmlOptions options, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var sb = new StringBuilder();
        var topLevelDeclarations = options.RootName is null ? options.Declarations : "";

        if (options.RootName is { } rootName)
            _ = sb.Append('<').Append(rootName).Append(options.Declarations).Append('>');

        // The names of the elements left open, outermost first; the innermost
        // level is absent whenever it self-closed.
        var open = new List<string>();
        byte[]? previous = null;
        foreach (var rowBytes in inner.Execute(batch, outerResolver).RowBytes)
        {
            var depth = previous is null ? 0 : AutoRestartDepth(levels, innerSchema, previous, rowBytes);
            for (var i = open.Count - 1; i >= depth; i--)
            {
                _ = sb.Append("</").Append(open[i]).Append('>');
                open.RemoveAt(i);
            }

            for (var i = depth; i < levels.Length; i++)
            {
                var element = levelElements[i];
                _ = sb.Append('<').Append(element.Name);
                if (i == 0)
                    _ = sb.Append(topLevelDeclarations);
                AppendForXmlAttributes(sb, element, rowBytes, innerSchema, options);

                var body = new StringBuilder();
                _ = SerializeForXmlContent(body, element.Content, rowBytes, innerSchema, options, declarationsOnElements: "", prevAtomic: false);
                // Only the innermost level can self-close: every outer level
                // has the next level's element as content.
                if (body.Length == 0 && i == levels.Length - 1)
                {
                    _ = sb.Append("/>");
                    continue;
                }
                _ = sb.Append('>').Append(body);
                open.Add(element.Name);
            }
            previous = rowBytes;
        }

        if (previous is null)
        {
            if (options.Typed)
                yield return EmptyForXmlRow();
            yield break;
        }

        for (var i = open.Count - 1; i >= 0; i--)
            _ = sb.Append("</").Append(open[i]).Append('>');
        if (options.RootName is { } closeName)
            _ = sb.Append("</").Append(closeName).Append('>');

        yield return ForXmlRow(sb, options);
    }

    /// <summary>
    /// The serialized fragment as one result row — a typed <c>xml</c> value
    /// under the TYPE option, else the <c>nvarchar(max)</c> string form.
    /// </summary>
    private static byte[] ForXmlRow(StringBuilder document, ForXmlOptions options) => options.Typed
        ? RowEncoder.EncodeRow([SqlType.Xml], [SqlValue.FromXml(document.ToString())])
        : RowEncoder.EncodeRow([SqlType.NVarcharMax], [SqlValue.FromNVarchar(SqlType.NVarcharMax, document.ToString())]);

    /// <summary>
    /// The row an empty input rowset produces under the TYPE option: one NULL
    /// xml value (probe-confirmed — without TYPE the statement returns no rows
    /// at all, so a scalar subquery reads NULL either way).
    /// </summary>
    private static byte[] EmptyForXmlRow() => RowEncoder.EncodeRow([SqlType.Xml], [SqlValue.Null(SqlType.Xml)]);

    /// <summary>
    /// Appends an element's attributes for one row, skipping the NULL ones
    /// (attributes are always absent for NULL, whatever the ELEMENTS setting).
    /// </summary>
    private static void AppendForXmlAttributes(StringBuilder sb, ForXmlElement element, byte[] rowBytes, SqlType[] innerSchema, ForXmlOptions options)
    {
        foreach (var attribute in element.Attributes)
        {
            var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, attribute.Column);
            if (value.IsNull)
                continue;
            _ = sb.Append(' ').Append(attribute.Name).Append("=\"");
            AppendForXmlText(sb, ForXmlColumnText(value, attribute.Column, rowBytes, innerSchema, options), isAttribute: true);
            _ = sb.Append('"');
        }
    }

    /// <summary>
    /// The text one non-NULL result column serializes as: its own value, or —
    /// for an AUTO binary column without <c>BINARY BASE64</c> — the
    /// <c>dbobject</c> reference standing in for it. The caller applies the
    /// position's escaping.
    /// </summary>
    private static string ForXmlColumnText(SqlValue value, int column, byte[] rowBytes, SqlType[] innerSchema, ForXmlOptions options) =>
        options.BinaryUrls?[column] is { } url
            ? FormatForXmlBinaryUrl(url, rowBytes, innerSchema)
            : ScalarForXmlText(value);

    /// <summary>
    /// Serializes one element (open tag, attributes, content, close/self-close)
    /// onto <paramref name="sb"/>. A single-leaf element whose value is NULL is
    /// omitted under the default ABSENT semantics, or rendered as
    /// <c>&lt;name xsi:nil="true"/&gt;</c> under XSINIL — but the <b>row</b>
    /// element always stands (a row whose only content is NULL is
    /// <c>&lt;row/&gt;</c> on real, not a missing row), which is what
    /// <paramref name="isRowElement"/> distinguishes.
    /// </summary>
    private static void SerializeForXmlElement(
        StringBuilder sb, ForXmlElement element, byte[] rowBytes, SqlType[] innerSchema, ForXmlOptions options, string declarations, bool isRowElement = false)
    {
        if (!isRowElement && element.Content.Count == 1 && element.Content[0] is ForXmlLeaf onlyLeaf
            && RowDecoder.DecodeColumn(innerSchema, rowBytes, onlyLeaf.Column).IsNull)
        {
            // A NULL under a comment / processing-instruction step writes
            // nothing at all, XSINIL included — the nil marker is for an
            // element that would have held a value (probe-confirmed).
            if (!options.Xsinil || onlyLeaf.Kind is ForXmlName.ForXmlPathLeaf.Comment or ForXmlName.ForXmlPathLeaf.ProcessingInstruction)
                return;
            _ = sb.Append('<').Append(element.Name).Append(declarations).Append(" xsi:nil=\"true\"/>");
            return;
        }

        _ = sb.Append('<').Append(element.Name).Append(declarations);
        AppendForXmlAttributes(sb, element, rowBytes, innerSchema, options);

        var body = new StringBuilder();
        _ = SerializeForXmlContent(body, element.Content, rowBytes, innerSchema, options, declarationsOnElements: "", prevAtomic: false);
        if (body.Length == 0)
            _ = sb.Append("/>");
        else
            _ = sb.Append('>').Append(body).Append("</").Append(element.Name).Append('>');
    }

    /// <summary>
    /// Serializes an ordered content list (child elements and text leaves),
    /// threading the <paramref name="prevAtomic"/> flag so adjacent
    /// <c>data()</c> atomic values are space-separated while <c>text()</c>
    /// values concatenate. Returns the trailing atomic state.
    /// </summary>
    private static bool SerializeForXmlContent(
        StringBuilder sb, List<object> content, byte[] rowBytes, SqlType[] innerSchema, ForXmlOptions options, string declarationsOnElements, bool prevAtomic)
    {
        foreach (var item in content)
        {
            switch (item)
            {
                case ForXmlElement element:
                    SerializeForXmlElement(sb, element, rowBytes, innerSchema, options, declarationsOnElements);
                    prevAtomic = false;
                    break;
                case ForXmlLeaf leaf:
                    var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, leaf.Column);
                    if (value.IsNull)
                        break;
                    switch (leaf.Kind)
                    {
                        // A comment / processing instruction writes its value
                        // raw — real escapes nothing inside either constructor,
                        // so a `?>` in a PI value closes it early and produces
                        // XML that won't re-parse. The one thing it does check
                        // is the pair of dashes a comment can't carry (Msg 9322).
                        case ForXmlName.ForXmlPathLeaf.Comment:
                            var comment = ForXmlColumnText(value, leaf.Column, rowBytes, innerSchema, options);
                            if (comment.Contains("--", StringComparison.Ordinal))
                                throw SimulatedSqlException.ForXmlCommentDashes(trailing: false);
                            if (comment.EndsWith('-'))
                                throw SimulatedSqlException.ForXmlCommentDashes(trailing: true);
                            _ = sb.Append("<!--").Append(comment).Append("-->");
                            break;
                        case ForXmlName.ForXmlPathLeaf.ProcessingInstruction:
                            _ = sb.Append("<?").Append(leaf.ProcessingInstructionTarget).Append(' ')
                                .Append(ForXmlColumnText(value, leaf.Column, rowBytes, innerSchema, options)).Append("?>");
                            break;
                        default:
                            if (leaf.Atomic && prevAtomic)
                                _ = sb.Append(' ');
                            // An xml-typed value is already markup: it embeds as
                            // nodes, not as escaped text. That covers a stored xml
                            // column, a CAST(… AS xml), and a nested FOR XML … TYPE
                            // subquery alike — the type is what decides, matching real.
                            if (innerSchema[leaf.Column] is XmlSqlType)
                                _ = sb.Append(ScalarForXmlText(value));
                            else
                                AppendForXmlText(sb, ForXmlColumnText(value, leaf.Column, rowBytes, innerSchema, options), isAttribute: false);
                            break;
                    }
                    // A comment or processing instruction breaks a run of
                    // data() atoms, so the values either side of one aren't
                    // space-joined (probe-confirmed).
                    prevAtomic = leaf.Atomic;
                    break;
            }
        }
        return prevAtomic;
    }

    /// <summary>
    /// Appends <paramref name="text"/> with position-dependent XML escaping.
    /// Element content escapes <c>&amp;</c> / <c>&lt;</c> / <c>&gt;</c> and the
    /// carriage return (preserved through parsing); an attribute value also
    /// escapes the double quote, tab, and line feed (attribute-value
    /// normalization). Probe-confirmed against SQL Server 2025.
    /// Shared with <see cref="XmlDml"/>, which re-serializes a
    /// <c>.modify()</c>-edited instance under the same rules.
    /// </summary>
    internal static void AppendForXmlText(StringBuilder sb, string text, bool isAttribute)
    {
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => sb.Append("&amp;"),
                '<' => sb.Append("&lt;"),
                '>' => sb.Append("&gt;"),
                '"' when isAttribute => sb.Append("&quot;"),
                '\t' when isAttribute => sb.Append("&#x09;"),
                '\n' when isAttribute => sb.Append("&#x0A;"),
                '\r' => sb.Append("&#x0D;"),
                _ => sb.Append(c),
            };
        }
    }

    /// <summary>
    /// The unescaped XML text form of a non-NULL value. Numeric / date
    /// formatting matches FOR JSON (scientific float, fraction-drop dates)
    /// except <c>bit</c> renders <c>1</c>/<c>0</c>; binary base64-encodes and
    /// <c>uniqueidentifier</c> uppercases. Callers apply the position-dependent
    /// escaping. Shared with <see cref="XmlDml"/>, which atomizes a
    /// <c>.modify()</c> value term through it.
    /// </summary>
    internal static string ScalarForXmlText(SqlValue value)
    {
        var type = value.Type;
        switch (type)
        {
            case var _ when type == SqlType.Bit:
                return value.AsBoolean ? "1" : "0";
            case SqlVariantSqlType:
                return ScalarForXmlText(value.AsVariantInner);
            case var _ when type == SqlType.Float:
                return value.AsDouble.ToString("0.000000000000000e+000", CultureInfo.InvariantCulture);
            case var _ when type == SqlType.Real:
                return value.AsSingle.ToString("0.0000000e+000", CultureInfo.InvariantCulture);
            case var _ when type == SqlType.Money || type == SqlType.SmallMoney:
                return value.AsMoneyDecimal38.ToString();
            case BinarySqlType or VarbinarySqlType or ImageSqlType:
                return Convert.ToBase64String(value.AsBytes);
            case DateTime2SqlType dt2:
                return ForXmlDateTime(value.AsDateTime2, dt2.precision);
            case var _ when type == SqlType.DateTime:
                return ForXmlDateTime(value.AsDateTime, 3);
            case var _ when type == SqlType.SmallDateTime:
                return ForXmlDateTime(value.AsSmallDateTime, 0);
            case TimeSqlType time:
                {
                    var sb = new StringBuilder();
                    _ = sb.Append(value.AsTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                    AppendForJsonFraction(sb, value.AsTime.Ticks % TimeSpan.TicksPerSecond, time.precision);
                    return sb.ToString();
                }
            case DateTimeOffsetSqlType dto:
                {
                    var offset = value.AsDateTimeOffset;
                    var sb = new StringBuilder();
                    _ = sb.Append(offset.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                    AppendForJsonFraction(sb, offset.Ticks % TimeSpan.TicksPerSecond, dto.precision);
                    _ = sb.Append(offset.ToString("zzz", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            default:
                // int / decimal / date / char / nchar / varchar / nvarchar /
                // uniqueidentifier / everything else: the default string form.
                return value.CoerceTo(SqlType.NVarchar).AsString;
        }
    }

    private static string ForXmlDateTime(DateTime value, int precision)
    {
        var sb = new StringBuilder();
        _ = sb.Append(value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        AppendForJsonFraction(sb, value.Ticks % TimeSpan.TicksPerSecond, precision);
        return sb.ToString();
    }
}

/// <summary>The four FOR XML modes.</summary>
internal enum ForXmlMode
{
    /// <summary>Attribute-centric (default) rows named <c>row</c> / <c>('elem')</c>.</summary>
    Raw,
    /// <summary>One element per FROM source, named after its table/alias and nested.</summary>
    Auto,
    /// <summary>Column aliases drive XPath-like node placement (the workhorse).</summary>
    Path,
    /// <summary>The universal table: <c>Tag</c> / <c>Parent</c> plus <c>Element!Tag!Attribute[!Directive]</c> columns.</summary>
    Explicit,
}

/// <summary>
/// Which FOR XML options a clause has already written — real admits each once,
/// so a repeat is Msg 102. <c>ELEMENTS XSINIL</c> / <c>ABSENT</c> is one option
/// with its modifier, not two.
/// </summary>
[Flags]
internal enum ForXmlOptionSeen
{
    None = 0,
    Elements = 1,
    Root = 2,
    BinaryBase64 = 4,
    Type = 8,
}

/// <summary>Parsed FOR XML clause options. Immutable, so it rides the cached plan.</summary>
internal sealed class ForXmlOptions(
    ForXmlMode mode, string? rowElement, bool elements, bool xsinil, bool typed, bool binaryBase64,
    string? rootName, ForXmlNamespaces? namespaces, ForXmlBinaryUrl?[]? binaryUrls = null)
{
    public readonly ForXmlMode Mode = mode;

    /// <summary>The row element name for RAW / PATH ('' suppresses the row tag); null for AUTO.</summary>
    public readonly string? RowElement = rowElement;

    /// <summary>Element-centric serialization (RAW / AUTO); PATH is always element-centric.</summary>
    public readonly bool Elements = elements;

    /// <summary>Emit <c>xsi:nil="true"</c> elements for NULL columns instead of omitting them.</summary>
    public readonly bool Xsinil = xsinil;

    /// <summary>
    /// The <c>TYPE</c> option: the result is one unnamed <c>xml</c> column
    /// rather than the string form, so an enclosing FOR XML embeds it as
    /// nodes, and an empty input rowset still yields a row (NULL).
    /// </summary>
    public readonly bool Typed = typed;

    /// <summary>
    /// The <c>BINARY BASE64</c> option: RAW and AUTO base64-encode a binary
    /// column instead of raising / addressing it as a <c>dbobject</c> URL.
    /// PATH base64-encodes either way.
    /// </summary>
    public readonly bool BinaryBase64 = binaryBase64;

    /// <summary>The ROOT wrapper name, or null when no ROOT option was given.</summary>
    public readonly string? RootName = rootName;

    /// <summary>The statement's <c>WITH XMLNAMESPACES</c> bindings, or null.</summary>
    public readonly ForXmlNamespaces? Namespaces = namespaces;

    /// <summary>
    /// Per result column, the <c>dbobject</c> URL an AUTO binary column
    /// serializes as without <c>BINARY BASE64</c>; null entries (and a null
    /// array) mean the column serializes as its own value.
    /// </summary>
    public readonly ForXmlBinaryUrl?[]? BinaryUrls = binaryUrls;

    /// <summary>
    /// The declaration text the outermost element carries: the <c>xsi</c>
    /// binding XSINIL needs, then the statement's own, in real's reverse
    /// declaration order. Empty when neither applies.
    /// </summary>
    public readonly string Declarations = ForXmlNamespaces.TopLevelDeclarations(xsinil, namespaces);

    /// <summary>This clause's options with <paramref name="urls"/> attached.</summary>
    public ForXmlOptions WithBinaryUrls(ForXmlBinaryUrl?[] urls) =>
        new(this.Mode, this.RowElement, this.Elements, this.Xsinil, this.Typed, this.BinaryBase64, this.RootName, this.Namespaces, urls);
}

/// <summary>
/// The <c>dbobject/TABLE[@PK='V']/@COLUMN</c> reference <c>FOR XML AUTO</c>
/// writes for a binary column when <c>BINARY BASE64</c> is absent — SQL Server's
/// legacy SQLXML addressing form. Assembled once per plan: the fixed text either
/// side of the key predicate, plus the result columns each key value reads from.
/// </summary>
/// <remarks>
/// Real builds the reference from the <em>base</em> names — the owning table's
/// object name and the base column names, not the select-list aliases — and
/// joins a composite key's terms with the URL-escaped <c>%20and%20</c>. The
/// finished reference then takes ordinary attribute / element escaping, so a key
/// value containing <c>&amp;</c> comes back escaped (probe-confirmed).
/// </remarks>
internal sealed class ForXmlBinaryUrl(string prefix, ForXmlUrlKey[] keys, string suffix)
{
    /// <summary><c>dbobject/&lt;table&gt;[</c>.</summary>
    public readonly string Prefix = prefix;

    public readonly ForXmlUrlKey[] Keys = keys;

    /// <summary><c>]/@&lt;column&gt;</c>.</summary>
    public readonly string Suffix = suffix;
}

/// <summary>One key term of a <see cref="ForXmlBinaryUrl"/>: the base column's name and where its value lives.</summary>
internal sealed class ForXmlUrlKey(string name, int column)
{
    public readonly string Name = name;
    public readonly int Column = column;
}

/// <summary>An attribute placement on a FOR XML element, bound to a result column.</summary>
internal sealed class ForXmlAttribute(string name, int column)
{
    public readonly string Name = name;
    public readonly int Column = column;
}

/// <summary>
/// A value leaf in FOR XML content, bound to a result column.
/// <see cref="Kind"/> is what the alias's last step selected — text content,
/// a space-joined <c>data()</c> atom, a comment or a processing instruction —
/// and never <see cref="ForXmlName.ForXmlPathLeaf.Element"/>, which descends
/// into an element of its own and places a text leaf inside it.
/// </summary>
internal sealed class ForXmlLeaf(int column, ForXmlName.ForXmlPathLeaf kind, string? processingInstructionTarget)
{
    public readonly int Column = column;
    public readonly ForXmlName.ForXmlPathLeaf Kind = kind;

    /// <summary>The PI target; non-null exactly for a processing-instruction leaf.</summary>
    public readonly string? ProcessingInstructionTarget = processingInstructionTarget;

    /// <summary>A <c>data()</c> atom, which a space separates from an adjacent one.</summary>
    public bool Atomic => this.Kind == ForXmlName.ForXmlPathLeaf.Data;
}

/// <summary>
/// One element in the FOR XML row template: a name, ordered attributes, and
/// ordered content (nested <see cref="ForXmlElement"/> children and
/// <see cref="ForXmlLeaf"/> text leaves). Shared across rows; leaves bind to
/// result-column indices resolved per row at serialization.
/// </summary>
internal sealed class ForXmlElement(string name)
{
    public readonly string Name = name;
    public readonly List<ForXmlAttribute> Attributes = [];
    public readonly List<object> Content = [];

    public bool HasContent => this.Content.Count > 0;
}
