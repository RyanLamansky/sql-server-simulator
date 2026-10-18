using SqlServerSimulator.Parser.FullText;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>The four index keyword TVFs' shared output shape.</summary>
    private enum FullTextKeywordView
    {
        /// <summary><c>dm_fts_index_keywords</c>: one row per keyword and column, with its document count.</summary>
        Keywords,

        /// <summary><c>dm_fts_index_keywords_by_document</c>: one row per keyword, column and document, with its occurrence count.</summary>
        ByDocument,

        /// <summary><c>dm_fts_index_keywords_by_property</c>: one row per keyword, column, document and search property — none, as no property list is modeled.</summary>
        ByProperty,

        /// <summary><c>dm_fts_index_keywords_position_by_document</c>: one row per occurrence.</summary>
        PositionByDocument,
    }

    private static readonly NVarcharSqlType FullTextDisplayTermType = NVarcharSqlType.Get(4000, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly (string Name, SqlType Type)[] FullTextKeywordsColumns =
        [("keyword", VarbinarySqlType.Get(128)), ("display_term", FullTextDisplayTermType), ("column_id", SqlType.Int32), ("document_count", SqlType.BigInt)];

    private static readonly (string Name, SqlType Type)[] FullTextKeywordsByDocumentColumns =
        [("keyword", VarbinarySqlType.Get(128)), ("display_term", FullTextDisplayTermType), ("column_id", SqlType.Int32), ("document_id", SqlType.BigInt), ("occurrence_count", SqlType.Int32)];

    private static readonly (string Name, SqlType Type)[] FullTextKeywordsByPropertyColumns =
        [("keyword", VarbinarySqlType.Get(128)), ("display_term", FullTextDisplayTermType), ("column_id", SqlType.Int32), ("document_id", SqlType.BigInt), ("property_id", SqlType.Int32)];

    private static readonly (string Name, SqlType Type)[] FullTextKeywordsPositionColumns =
        [("keyword", VarbinarySqlType.Get(128)), ("display_term", FullTextDisplayTermType), ("column_id", SqlType.Int32), ("document_id", SqlType.BigInt), ("position", SqlType.Int32)];

    /// <summary>
    /// <c>sys.dm_fts_index_keywords(database_id, object_id)</c> and its three
    /// siblings by name, or null for another: the keywords a table's
    /// full-text index holds, read off its live rows the way a search reads
    /// them (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static Selection? ParseFullTextKeywordDmv(ParserContext context, string leaf, string functionName, Database? database)
    {
        Span<char> upper = stackalloc char[leaf.Length];
        _ = leaf.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "DM_FTS_INDEX_KEYWORDS" => ParseSystemTvf(context, functionName, database, 2, FullTextKeywordsColumns,
                static (batch, args) => FullTextKeywordRows(batch, args, FullTextKeywordView.Keywords), notNullColumns: 3, arityState: 3),
            "DM_FTS_INDEX_KEYWORDS_BY_DOCUMENT" => ParseSystemTvf(context, functionName, database, 2, FullTextKeywordsByDocumentColumns,
                static (batch, args) => FullTextKeywordRows(batch, args, FullTextKeywordView.ByDocument), notNullColumns: 3, arityState: 3),
            "DM_FTS_INDEX_KEYWORDS_BY_PROPERTY" => ParseSystemTvf(context, functionName, database, 2, FullTextKeywordsByPropertyColumns,
                static (batch, args) => FullTextKeywordRows(batch, args, FullTextKeywordView.ByProperty), notNullColumns: 3, arityState: 3),
            "DM_FTS_INDEX_KEYWORDS_POSITION_BY_DOCUMENT" => ParseSystemTvf(context, functionName, database, 2, FullTextKeywordsPositionColumns,
                static (batch, args) => FullTextKeywordRows(batch, args, FullTextKeywordView.PositionByDocument), notNullColumns: 3, arityState: 3),
            _ => null,
        };
    }

    /// <summary>
    /// One occurrence of a keyword in a full-text-indexed row: the entries a
    /// crawl of the row would write, ordered as real's index returns them —
    /// by keyword bytes, column, document, position.
    /// </summary>
    private readonly struct FullTextKeywordOccurrence(byte[] keyword, string display, int columnId, long documentId, int position)
    {
        public readonly byte[] Keyword = keyword;
        public readonly string Display = display;
        public readonly int ColumnId = columnId;
        public readonly long DocumentId = documentId;
        public readonly int Position = position;

        public static int Compare(FullTextKeywordOccurrence a, FullTextKeywordOccurrence b)
        {
            var order = a.Keyword.AsSpan().SequenceCompareTo(b.Keyword);
            if (order == 0)
                order = a.ColumnId.CompareTo(b.ColumnId);
            if (order == 0)
                order = a.DocumentId.CompareTo(b.DocumentId);
            return order == 0 ? a.Position.CompareTo(b.Position) : order;
        }

        public bool SameEntry(FullTextKeywordOccurrence other, bool byDocument) =>
            this.ColumnId == other.ColumnId && (!byDocument || this.DocumentId == other.DocumentId) && this.Keyword.AsSpan().SequenceEqual(other.Keyword);
    }

    /// <summary>
    /// The initial crawl of a just-created full-text index over a table whose
    /// key isn't an integer: its rows take document ids in key order.
    /// </summary>
    internal static void CrawlFullTextDocuments(HeapTable table)
    {
        var index = table.FullTextIndex!;
        var keyStorageOrdinal = ResolveFullTextKeyOrdinal(table);
        if (SqlType.IsIntegerCategory(table.StoredColumns[keyStorageOrdinal].Type))
            return;
        List<SqlValue> keys = [];
        foreach (var bytes in table.Rows)
            keys.Add(RowDecoder.DecodeColumn(table.StoredColumns, bytes, keyStorageOrdinal, table.Heap));
        keys.Sort(static (a, b) => a.CompareTo(b));
        foreach (var key in keys)
            _ = index.DocumentIdFor(key);
        index.EndInitialCrawl();
    }

    /// <summary>
    /// Gives a row just written to a table with a full-text index over a
    /// non-integer key its document id, in write order as real's crawl does.
    /// </summary>
    internal static void NoteFullTextDocument(HeapTable table, ReadOnlySpan<byte> image)
    {
        var index = table.FullTextIndex!;
        var keyStorageOrdinal = ResolveFullTextKeyOrdinal(table);
        if (!SqlType.IsIntegerCategory(table.StoredColumns[keyStorageOrdinal].Type))
            _ = index.DocumentIdFor(RowDecoder.DecodeColumn(table.StoredColumns.AsSpan(), image, keyStorageOrdinal, table.Heap));
    }

    /// <summary>
    /// The rows of one keyword view over the table <paramref name="args"/>
    /// names, after real's argument checks: a NULL argument is Msg 30007, an
    /// unknown database Msg 30003, an object that is no user table there Msg
    /// 30004 and a table without a full-text index Msg 30006.
    /// </summary>
    /// <remarks>
    /// Each indexed column of each row breaks into terms at their positions,
    /// noise words keeping theirs unlisted, and a non-empty value ends with an
    /// <c>END OF FILE</c> entry one past its last word (at 1 when it has none);
    /// a row whose every column is NULL or empty holds one at position 1 of its
    /// highest-numbered indexed column. The document id is the key's value for
    /// an integer key and otherwise the id the index holds for the key
    /// (<see cref="Schemas.FullTextIndex.DocumentIdFor"/>).
    /// </remarks>
    private static List<SqlValue[]> FullTextKeywordRows(BatchContext batch, SqlValue[] args, FullTextKeywordView view)
    {
        if (args[0].IsNull || args[1].IsNull)
            throw SimulatedSqlException.FullTextKeywordViewNullArgument();
        var databaseId = Expressions.ScalarArguments.CoerceToInt(args[0]);
        var objectId = Expressions.ScalarArguments.CoerceToInt(args[1]);
        Database? database = null;
        foreach (var (candidate, id) in Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            if (id == databaseId)
                database = candidate;
        }
        if (database is null)
            throw SimulatedSqlException.FullTextViewDatabaseNotFound(databaseId);
        HeapTable? table = null;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, candidate) in schema.HeapTables)
            {
                if (candidate.ObjectId == objectId)
                    table = candidate;
            }
        }
        if (table is null)
            throw SimulatedSqlException.FullTextViewTableNotFound(objectId);
        if (table.FullTextIndex is not { } index)
            throw SimulatedSqlException.FullTextViewIndexNotFound(objectId);
        if (view == FullTextKeywordView.ByProperty)
            return [];

        var accentSensitive = true;
        foreach (var (_, catalog) in database.FullTextCatalogs)
        {
            if (catalog.Id == index.CatalogId)
                accentSensitive = catalog.IsAccentSensitive;
        }
        var keyStorageOrdinal = ResolveFullTextKeyOrdinal(table);
        var keyType = table.StoredColumns[keyStorageOrdinal].Type;
        var integerKey = SqlType.IsIntegerCategory(keyType);
        List<(SqlValue Key, byte[] Row)> rows = [];
        foreach (var bytes in table.Rows)
            rows.Add((RowDecoder.DecodeColumn(table.StoredColumns, bytes, keyStorageOrdinal, table.Heap), bytes));
        if (!integerKey)
            rows.Sort(static (a, b) => a.Key.CompareTo(b.Key));

        var columns = index.Columns.OrderBy(static column => column.ColumnId).ToArray();
        var lastColumnId = columns.Length == 0 ? 0 : columns[^1].ColumnId;
        var encoded = new Dictionary<string, (byte[] Keyword, string Display)>(StringComparer.Ordinal);
        List<FullTextKeywordOccurrence> occurrences = [];
        for (var r = 0; r < rows.Count; r++)
        {
            var (key, bytes) = rows[r];
            var documentId = integerKey ? key.CoerceTo(SqlType.BigInt).AsInt64 : index.DocumentIdFor(key);
            var endedAny = false;
            foreach (var column in columns)
            {
                var ordinal = column.ColumnId - 1;
                if (ordinal < 0 || ordinal >= table.Columns.Length || !table.Columns[ordinal].IsStored)
                    continue;
                var value = RowDecoder.DecodeColumn(table.StoredColumns, bytes, table.StorageOrdinals[ordinal], table.Heap);
                var extension = column.TypeColumnId is int typeColumnId && typeColumnId - 1 < table.Columns.Length && table.Columns[typeColumnId - 1].IsStored
                    ? RowDecoder.DecodeColumn(table.StoredColumns, bytes, table.StorageOrdinals[typeColumnId - 1], table.Heap)
                    : (SqlValue?)null;
                if (FullTextBinding.TextOf(value, extension) is not { Length: > 0 } text)
                    continue;
                var language = FullTextLanguage.For(column.LanguageId);
                var last = 0;
                foreach (var term in FullTextWordBreaker.Break(text, accentSensitive))
                {
                    if (term.Kind != FullTextTermKind.Word)
                        continue;
                    last = Math.Max(last, term.Position);
                    if (!index.StoplistOff && language.IsNoise(term.Text))
                        continue;
                    if (!encoded.TryGetValue(term.Text, out var entry))
                    {
                        entry = FullTextKeyword.Encode(term.Text, accentSensitive);
                        encoded[term.Text] = entry;
                    }
                    occurrences.Add(new FullTextKeywordOccurrence(entry.Keyword, entry.Display, column.ColumnId, documentId, term.Position));
                }
                occurrences.Add(new FullTextKeywordOccurrence(FullTextKeyword.EndOfFile, "END OF FILE", column.ColumnId, documentId, last + 1));
                endedAny = true;
            }
            if (!endedAny && lastColumnId > 0)
                occurrences.Add(new FullTextKeywordOccurrence(FullTextKeyword.EndOfFile, "END OF FILE", lastColumnId, documentId, 1));
        }
        occurrences.Sort(FullTextKeywordOccurrence.Compare);

        List<SqlValue[]> result = [];
        if (view == FullTextKeywordView.PositionByDocument)
        {
            FullTextKeywordOccurrence? previous = null;
            foreach (var occurrence in occurrences)
            {
                // A term the breaker emits twice at one position, as a
                // composite and its part can, is one entry.
                if (previous is { } p && p.SameEntry(occurrence, byDocument: true) && p.Position == occurrence.Position)
                    continue;
                previous = occurrence;
                result.Add([SqlValue.FromVarbinary(VarbinarySqlType.Get(128), occurrence.Keyword), SqlValue.FromNVarchar(occurrence.Display), SqlValue.FromInt32(occurrence.ColumnId),
                    SqlValue.FromInt64(occurrence.DocumentId), SqlValue.FromInt32(occurrence.Position)]);
            }
            return result;
        }

        var byDocument = view == FullTextKeywordView.ByDocument;
        for (var start = 0; start < occurrences.Count;)
        {
            var first = occurrences[start];
            var end = start + 1;
            var count = 1L;
            var lastDocument = first.DocumentId;
            var lastPosition = first.Position;
            while (end < occurrences.Count && occurrences[end].SameEntry(first, byDocument))
            {
                var next = occurrences[end];
                // A keyword view counts documents and a document view
                // occurrences, a position the term holds twice once.
                if (byDocument ? next.Position != lastPosition : next.DocumentId != lastDocument)
                    count++;
                lastDocument = next.DocumentId;
                lastPosition = next.Position;
                end++;
            }
            result.Add(byDocument
                ? [SqlValue.FromVarbinary(VarbinarySqlType.Get(128), first.Keyword), SqlValue.FromNVarchar(first.Display), SqlValue.FromInt32(first.ColumnId),
                    SqlValue.FromInt64(first.DocumentId), SqlValue.FromInt32((int)count)]
                : [SqlValue.FromVarbinary(VarbinarySqlType.Get(128), first.Keyword), SqlValue.FromNVarchar(first.Display), SqlValue.FromInt32(first.ColumnId),
                    SqlValue.FromInt64(count)]);
            start = end;
        }
        return result;
    }
}
