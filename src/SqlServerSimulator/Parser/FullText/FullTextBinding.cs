using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// What a <c>CONTAINS</c> / <c>FREETEXT</c> / <c>CONTAINSTABLE</c> /
/// <c>FREETEXTTABLE</c> call resolved to at parse time: the full-text-indexed
/// table, the columns the search reads, and the accent fold its catalog
/// imposes.
/// </summary>
internal sealed class FullTextBinding(HeapTable table, int[] columnOrdinals, MultiPartName[] columnNames, MultiPartName?[] typeColumnNames, FullTextLanguage[] columnLanguages, bool accentSensitive)
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
    /// Per searched column, its <c>TYPE COLUMN</c> — the column naming a
    /// document's extension — under the same qualifier, or null.
    /// </summary>
    public readonly MultiPartName?[] TypeColumnNames = typeColumnNames;

    /// <summary>
    /// Each searched column's full-text language, from its <c>LANGUAGE</c>
    /// in the index: the stoplist its content is indexed under.
    /// </summary>
    public readonly FullTextLanguage[] ColumnLanguages = columnLanguages;

    /// <summary>
    /// The zero-based ordinal of searched column <paramref name="c"/>'s
    /// <c>TYPE COLUMN</c>, or -1 when it has none.
    /// </summary>
    public int TypeColumnOrdinal(int c)
    {
        if (this.Table.FullTextIndex is { } index)
        {
            foreach (var column in index.Columns)
            {
                if (column.ColumnId == this.ColumnOrdinals[c] + 1)
                    return column.TypeColumnId is int typeColumnId ? typeColumnId - 1 : -1;
            }
        }
        return -1;
    }

    /// <summary>
    /// The language a condition is read in when the call names none: the
    /// first searched column's.
    /// </summary>
    public FullTextLanguage DefaultQueryLanguage => this.ColumnLanguages.Length == 0 ? FullTextLanguage.English : this.ColumnLanguages[0];

    /// <summary>
    /// From the backing catalog's <c>ACCENT_SENSITIVITY</c> option (default
    /// ON). Controls whether the word breaker folds diacritics on both the
    /// indexed content and the condition's own terms.
    /// </summary>
    public readonly bool AccentSensitive = accentSensitive;

    /// <summary>
    /// Whether the index drops the system stoplist's words, read when the
    /// search runs so an <c>ALTER FULLTEXT INDEX … SET STOPLIST</c> reaches a
    /// cached plan.
    /// </summary>
    public bool UsesStoplist => this.Table.FullTextIndex is not { StoplistOff: true };

    /// <summary>
    /// Builds a document from one row by word-breaking each searched column in
    /// index order.
    /// </summary>
    public FullTextDocument BuildDocument(Func<MultiPartName, SqlValue> resolveColumn)
    {
        var document = new FullTextDocument();
        var stoplist = this.UsesStoplist;
        for (var i = 0; i < this.ColumnNames.Length; i++)
            document.AddColumn(TextOf(resolveColumn(this.ColumnNames[i]), this.TypeColumnNames[i] is { } typeColumn ? resolveColumn(typeColumn) : null), this.AccentSensitive, stoplist ? this.ColumnLanguages[i] : null);
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
    /// A <c>TYPE COLUMN</c> pairing indexes a binary document through the
    /// filter its extension names; see <see cref="FilteredText"/>.
    /// </para>
    /// </remarks>
    public static string? TextOf(SqlValue value, SqlValue? extension = null)
    {
        return value.IsNull ? null
            : value.Type is XmlSqlType ? XmlContentText(value.AsString)
            : SqlType.IsStringCategory(value.Type) ? value.AsString
            : value.Type is BinarySqlType or VarbinarySqlType or ImageSqlType && extension is { IsNull: false } ext && SqlType.IsStringCategory(ext.Type)
                ? FilteredText(value.AsBytes, ext.AsString.Trim())
            : null;
    }

    /// <summary>
    /// The text real's filters extract from a binary document by extension,
    /// fitted to probes of SQL Server 2025 on Linux (2026-10-02), which ships
    /// the <c>.txt</c> / <c>.c</c> / <c>.csv</c>, <c>.htm</c> / <c>.html</c>
    /// and <c>.xml</c> filters: the plain ones index the decoded text as is,
    /// markup included; HTML indexes element text with entities decoded, but
    /// neither tag names, attribute values, comments nor <c>script</c> /
    /// <c>style</c> bodies; XML indexes text and attribute values. The bytes
    /// decode by their byte-order mark (UTF-8, UTF-16) or else as code page
    /// 1252 — a UTF-16 document without a mark yields nothing findable. Any
    /// other extension, or none, contributes nothing.
    /// </summary>
    private static string? FilteredText(byte[] bytes, string extension)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var plain = comparer.Equals(extension, ".txt") || comparer.Equals(extension, ".c") || comparer.Equals(extension, ".csv");
        var html = comparer.Equals(extension, ".htm") || comparer.Equals(extension, ".html");
        var xml = comparer.Equals(extension, ".xml");
        if (!plain && !html && !xml)
            return null;
        var text = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
            [0xFF, 0xFE, ..] => System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
            [0xFE, 0xFF, ..] => System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
            _ => CharSqlType.Cp1252Encoder.GetString(bytes),
        };
        return plain ? text : xml ? XmlContentText(text) : HtmlContentText(text);
    }

    /// <summary>
    /// The element text of an HTML document: tags, comments and the bodies of
    /// <c>script</c> / <c>style</c> elements dropped, entities decoded, each
    /// dropped span leaving a separator so neighboring words don't fuse.
    /// </summary>
    private static string HtmlContentText(string document)
    {
        var builder = new System.Text.StringBuilder(document.Length);
        var i = 0;
        while (i < document.Length)
        {
            if (document[i] != '<')
            {
                var next = document.IndexOf('<', i);
                var end = next < 0 ? document.Length : next;
                _ = builder.Append(System.Net.WebUtility.HtmlDecode(document[i..end]));
                i = end;
                continue;
            }
            if (string.CompareOrdinal(document, i, "<!--", 0, 4) == 0)
            {
                var close = document.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? document.Length : close + 3;
            }
            else
            {
                var close = document.IndexOf('>', i + 1);
                var tagEnd = close < 0 ? document.Length : close + 1;
                var raw = SkippedElementBody(document, i, tagEnd);
                i = raw < 0 ? tagEnd : raw;
            }
            _ = builder.Append(' ');
        }
        return builder.ToString();
    }

    /// <summary>
    /// Where the matching close tag of a <c>script</c> / <c>style</c> start tag
    /// spanning <paramref name="start"/> to <paramref name="tagEnd"/> ends, or
    /// -1 for any other tag.
    /// </summary>
    private static int SkippedElementBody(string document, int start, int tagEnd)
    {
        foreach (var name in (ReadOnlySpan<string>)["script", "style"])
        {
            if (start + 1 + name.Length > document.Length
                || string.Compare(document, start + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }
            var after = start + 1 + name.Length;
            if (after < document.Length && char.IsLetterOrDigit(document[after]))
                continue;
            var close = document.IndexOf("</" + name, tagEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                return document.Length;
            var closeEnd = document.IndexOf('>', close);
            return closeEnd < 0 ? document.Length : closeEnd + 1;
        }
        return -1;
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
        // `PROPERTY(column, 'name')` searches one document property, which
        // needs a search property list no index here carries.
        if (context.Token is UnquotedString propertyToken && propertyToken.Value.Equals("PROPERTY", StringComparison.OrdinalIgnoreCase))
        {
            var beforeProperty = context.SaveCheckpoint();
            if (context.MoveNext() && context.Token is Operator { Character: '(' })
            {
                context.MoveNextRequired();
                _ = BatchContext.ParseObjectName(context);
                if (context.GetNextRequired() is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Literal)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                throw SimulatedSqlException.FullTextPropertySearchUnsupported();
            }
            context.RestoreCheckpoint(beforeProperty);
        }

        switch (context.Token)
        {
            case Operator { Character: '*' }:
                context.MoveNextRequired();
                return new Spec(allColumns: true, [], starQualifier: null);

            case Operator { Character: '(' }:
                List<MultiPartName> columns = [];
                context.MoveNextRequired();
                // `(*)` is the star form too.
                if (context.Token is Operator { Character: '*' })
                {
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    return new Spec(allColumns: true, [], starQualifier: null);
                }
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
    public static FullTextBinding Bind(Spec spec, HeapTable table, string reportedTableName, Database database, Collation collation, string? qualifier, byte notIndexedState = 2)
    {
        if (table.FullTextIndex is not { } index)
            throw SimulatedSqlException.FullTextTableNotIndexed(reportedTableName, notIndexedState);

        var accentSensitive = true;
        foreach (var (_, catalog) in database.FullTextCatalogs)
        {
            if (catalog.Id == index.CatalogId)
            {
                accentSensitive = catalog.IsAccentSensitive;
                break;
            }
        }

        List<int> ordinals = [];
        List<MultiPartName> names = [];
        List<MultiPartName?> typeNames = [];
        List<FullTextLanguage> languages = [];
        MultiPartName? TypeColumnName(Schemas.FullTextIndexColumn column, string? columnQualifier) =>
            column.TypeColumnId is int typeColumnId && typeColumnId - 1 < table.Columns.Length
                ? Qualify(columnQualifier, table.Columns[typeColumnId - 1].Name)
                : null;
        if (spec.AllColumns)
        {
            foreach (var column in index.Columns)
            {
                var ordinal = column.ColumnId - 1;
                if (ordinal < 0 || ordinal >= table.Columns.Length)
                    continue;
                ordinals.Add(ordinal);
                names.Add(Qualify(qualifier, table.Columns[ordinal].Name));
                typeNames.Add(TypeColumnName(column, qualifier));
                languages.Add(FullTextLanguage.For(column.LanguageId));
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
                Schemas.FullTextIndexColumn? indexed = null;
                foreach (var column in index.Columns)
                {
                    if (column.ColumnId == ordinal + 1)
                    {
                        indexed = column;
                        break;
                    }
                }
                if (indexed is not { } entry)
                    throw SimulatedSqlException.FullTextColumnNotIndexed(written.Leaf);
                languages.Add(FullTextLanguage.For(entry.LanguageId));
                ordinals.Add(ordinal);
                names.Add(written.Count > 1 ? written : Qualify(qualifier, table.Columns[ordinal].Name));
                typeNames.Add(TypeColumnName(entry, written.Count > 1 ? written.ImmediateQualifier : qualifier));
            }
        }
        return new FullTextBinding(table, [.. ordinals], [.. names], [.. typeNames], [.. languages], accentSensitive);
    }

    private static MultiPartName Qualify(string? qualifier, string columnName) =>
        qualifier is null ? new MultiPartName(columnName) : new MultiPartName(qualifier).WithAddedPart(columnName);
}
