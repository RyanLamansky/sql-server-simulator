using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// What a <c>CONTAINS</c> / <c>FREETEXT</c> / <c>CONTAINSTABLE</c> /
/// <c>FREETEXTTABLE</c> call resolved to at parse time: the full-text-indexed
/// table, the columns the search reads, and the accent fold its catalog
/// imposes.
/// </summary>
internal sealed class FullTextBinding(HeapTable table, int[] columnOrdinals, MultiPartName[] columnNames, bool accentSensitive)
{
    public readonly HeapTable Table = table;

    /// <summary>Zero-based indexes into <see cref="HeapTable.Columns"/>.</summary>
    public readonly int[] ColumnOrdinals = columnOrdinals;

    /// <summary>
    /// The same columns as names the per-row resolver understands, carrying
    /// whatever qualifier the call was written with so a self-join's two
    /// instances stay distinguishable.
    /// </summary>
    public readonly MultiPartName[] ColumnNames = columnNames;

    /// <summary>
    /// From the backing catalog's <c>ACCENT_SENSITIVITY</c> option (default
    /// ON). Controls whether the word breaker folds diacritics on both the
    /// indexed content and the condition's own terms.
    /// </summary>
    public readonly bool AccentSensitive = accentSensitive;

    /// <summary>
    /// Builds a document from one row by word-breaking each searched column in
    /// index order.
    /// </summary>
    public FullTextDocument BuildDocument(Func<MultiPartName, SqlValue> resolveColumn)
    {
        var document = new FullTextDocument();
        foreach (var name in this.ColumnNames)
            document.AddColumn(TextOf(resolveColumn(name)), this.AccentSensitive);
        return document;
    }

    /// <summary>
    /// The searchable text of one indexed column value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>xml</c> column contributes its <b>content</b> — text nodes and
    /// attribute values — and not its markup, which is what real indexes:
    /// probing <c>&lt;r kind="cv"&gt;&lt;skill&gt;Engineer&lt;/skill&gt;&lt;/r&gt;</c>
    /// found <c>Engineer</c> and <c>cv</c> but neither the element name
    /// <c>skill</c> nor the attribute name <c>kind</c>. A document that won't
    /// parse falls back to its raw text.
    /// </para>
    /// <para>
    /// A <c>TYPE COLUMN</c> pairing indexes a <c>varbinary</c> document that
    /// real runs through a filter to extract text; the simulator has no filter,
    /// so such a column contributes nothing rather than word-breaking its bytes.
    /// </para>
    /// </remarks>
    public static string? TextOf(SqlValue value)
    {
        return value.IsNull ? null
            : value.Type is XmlSqlType ? XmlContentText(value.AsString)
            : SqlType.IsStringCategory(value.Type) ? value.AsString
            : null;
    }

    /// <summary>
    /// Concatenates an XML document's text nodes and attribute values,
    /// separated so no two adjacent nodes fuse into one term.
    /// </summary>
    private static string XmlContentText(string document)
    {
        var builder = new System.Text.StringBuilder(document.Length);
        try
        {
            using var reader = System.Xml.XmlReader.Create(
                new StringReader(document),
                new System.Xml.XmlReaderSettings { ConformanceLevel = System.Xml.ConformanceLevel.Fragment, DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case System.Xml.XmlNodeType.Element:
                        while (reader.MoveToNextAttribute())
                            _ = builder.Append(reader.Value).Append(' ');
                        _ = reader.MoveToElement();
                        break;
                    case System.Xml.XmlNodeType.Text:
                    case System.Xml.XmlNodeType.CDATA:
                    case System.Xml.XmlNodeType.SignificantWhitespace:
                        _ = builder.Append(reader.Value).Append(' ');
                        break;
                    default:
                        break;
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            return document;
        }
        return builder.ToString();
    }
}

/// <summary>
/// Parses the column specification the four full-text members share —
/// <c>col</c>, <c>(col, col, …)</c>, <c>*</c> or <c>alias.*</c> — and binds it
/// against a full-text-indexed table.
/// </summary>
internal static class FullTextColumnSpec
{
    /// <summary>
    /// One parsed specification, before it is matched to a table: either the
    /// star form or an explicit list of one-or-two-part column names.
    /// </summary>
    internal readonly struct Spec(bool allColumns, MultiPartName[] columns, string? starQualifier)
    {
        public readonly bool AllColumns = allColumns;
        public readonly MultiPartName[] Columns = columns;

        /// <summary>Alias written ahead of the star in <c>alias.*</c>.</summary>
        public readonly string? StarQualifier = starQualifier;
    }

    /// <summary>
    /// Reads the specification with the cursor on its first token; on return
    /// the cursor sits on the comma that follows.
    /// </summary>
    public static Spec Parse(ParserContext context)
    {
        switch (context.Token)
        {
            case Operator { Character: '*' }:
                context.MoveNextRequired();
                return new Spec(allColumns: true, [], starQualifier: null);

            case Operator { Character: '(' }:
                List<MultiPartName> columns = [];
                context.MoveNextRequired();
                while (true)
                {
                    columns.Add(BatchContext.ParseObjectName(context));
                    context.MoveNextRequired();
                    if (context.Token is Operator { Character: ',' })
                    {
                        context.MoveNextRequired();
                        continue;
                    }
                    break;
                }
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                return new Spec(allColumns: false, [.. columns], starQualifier: null);

            // `alias.*` — the star can't be a name segment, so this shape has
            // to be recognized before the general object-name parse sees it.
            case Name aliasToken:
                var checkpoint = context.SaveCheckpoint();
                if (context.MoveNext() && context.Token is Operator { Character: '.' }
                    && context.MoveNext() && context.Token is Operator { Character: '*' })
                {
                    context.MoveNextRequired();
                    return new Spec(allColumns: true, [], aliasToken.Value);
                }
                context.RestoreCheckpoint(checkpoint);
                break;

            default:
                break;
        }

        var name = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();
        return new Spec(allColumns: false, [name], starQualifier: null);
    }

    /// <summary>
    /// Resolves a specification against <paramref name="table"/>, raising real's
    /// Msg 7601 when the table carries no full-text index (state 2) or a named
    /// column isn't one of the indexed ones (state 3).
    /// </summary>
    public static FullTextBinding Bind(Spec spec, HeapTable table, string reportedTableName, Database database, Collation collation, string? qualifier)
    {
        if (table.FullTextIndex is not { } index)
            throw SimulatedSqlException.FullTextTableNotIndexed(reportedTableName);

        var accentSensitive = true;
        foreach (var catalog in database.FullTextCatalogs.Values)
        {
            if (catalog.Id == index.CatalogId)
            {
                accentSensitive = catalog.IsAccentSensitive;
                break;
            }
        }

        List<int> ordinals = [];
        List<MultiPartName> names = [];
        if (spec.AllColumns)
        {
            foreach (var column in index.Columns)
            {
                var ordinal = column.ColumnId - 1;
                if (ordinal < 0 || ordinal >= table.Columns.Length)
                    continue;
                ordinals.Add(ordinal);
                names.Add(Qualify(qualifier, table.Columns[ordinal].Name));
            }
        }
        else
        {
            foreach (var written in spec.Columns)
            {
                var ordinal = -1;
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    if (collation.Equals(table.Columns[i].Name, written.Leaf))
                    {
                        ordinal = i;
                        break;
                    }
                }
                if (ordinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(written.Leaf);
                var indexed = false;
                foreach (var column in index.Columns)
                {
                    if (column.ColumnId == ordinal + 1)
                    {
                        indexed = true;
                        break;
                    }
                }
                if (!indexed)
                    throw SimulatedSqlException.FullTextColumnNotIndexed(written.Leaf);
                ordinals.Add(ordinal);
                names.Add(written.Count > 1 ? written : Qualify(qualifier, table.Columns[ordinal].Name));
            }
        }
        return new FullTextBinding(table, [.. ordinals], [.. names], accentSensitive);
    }

    private static MultiPartName Qualify(string? qualifier, string columnName) =>
        qualifier is null ? new MultiPartName(columnName) : new MultiPartName(qualifier).WithAddedPart(columnName);
}
