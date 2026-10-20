using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One full-text index attached to a heap table — at most one per table
/// (real SQL Server's invariant; the simulator stores it directly on
/// <see cref="HeapTable.FullTextIndex"/> as a single nullable slot rather
/// than a list). Created via
/// <c>CREATE FULLTEXT INDEX ON table (col [LANGUAGE n][, ...]) KEY INDEX
/// unique_index_name [ON catalog]</c>.
/// </summary>
internal sealed class FullTextIndex(
    int catalogId,
    string keyIndexName,
    int uniqueIndexId,
    List<FullTextIndexColumn> columns)
{
    public readonly int CatalogId = catalogId;

    /// <summary>Name of the unique-key constraint (or unique index) that
    /// serves as the row key for the full-text index. Matches the
    /// <c>KEY INDEX</c> clause in the CREATE statement.</summary>
    public readonly string KeyIndexName = keyIndexName;

    /// <summary>
    /// <c>sys.fulltext_indexes.unique_index_id</c> — the index_id of the
    /// resolved key index. Real SQL Server's PK index_id is conventionally 1
    /// (the clustered index); the simulator looks up the actual index in the
    /// parent table's <see cref="HeapTable.KeyConstraints"/> /
    /// <see cref="HeapTable.Indexes"/> at CREATE time.
    /// </summary>
    public readonly int UniqueIndexId = uniqueIndexId;

    public readonly List<FullTextIndexColumn> Columns = columns;

    /// <summary>
    /// Whether a key of <paramref name="keyType"/> serves as its row's document
    /// id — an <c>int</c>, <c>bigint</c> or <c>smallint</c> — where any other,
    /// a <c>tinyint</c> included, is mapped to ids the index assigns, which
    /// real keeps in a <c>fulltext_index_docidmap</c> table (probed 2026-10-20
    /// against SQL Server 2025).
    /// </summary>
    public static bool KeysItsDocuments(SqlType keyType) => keyType is Int32SqlType or BigIntSqlType or SmallIntSqlType;

    /// <summary>
    /// The object ids of the index's internal tables — its average document
    /// lengths, document-id filter, document-id map, document status and
    /// indexed document ids — the map's unused when the key serves as the
    /// document ids.
    /// </summary>
    public int[] InternalTableObjectIds = [];

    /// <summary>When the index was created, which its internal tables report as their dates.</summary>
    public DateTime CreateDate;

    /// <summary>
    /// <c>sys.fulltext_indexes.is_enabled</c>: <c>ALTER FULLTEXT INDEX …
    /// DISABLE</c> clears it, and so does dropping the last column. A disabled
    /// index still answers searches, as real's keeps answering from what it
    /// crawled (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public bool IsEnabled = true;

    /// <summary>The <c>CHANGE_TRACKING</c> mode, <c>AUTO</c> unless the DDL says otherwise.</summary>
    public FullTextChangeTracking ChangeTracking = FullTextChangeTracking.Auto;

    /// <summary>
    /// <c>STOPLIST OFF</c>: a search treats the system stoplist's words as
    /// ordinary terms rather than dropping them, and
    /// <c>sys.fulltext_indexes.stoplist_id</c> is NULL.
    /// </summary>
    public bool StoplistOff;

    /// <summary>
    /// The document id each key of a table whose key isn't an integer holds,
    /// which real keeps for the key once assigned — through an update, and
    /// for a key deleted and written again (probed 2026-10-17 against SQL
    /// Server 2025). Guarded by its own lock, as sessions insert concurrently.
    /// </summary>
    private readonly Dictionary<SqlValue, long> documentIds = [];

    /// <summary>The last document id <see cref="documentIds"/> handed out.</summary>
    private long lastDocumentId;

    /// <summary>
    /// The document id of <paramref name="key"/>, assigning the next one the
    /// first time a key is met. Real numbers ids in blocks of a thousand with
    /// one id left between them (1–1000, 1002–2001, 2003–…): its initial crawl
    /// numbers the rows in key order from 1, and a row written afterwards
    /// starts the next block, its later ones following in write order — so
    /// three rows crawled leave the next written at 1002, and 1,500 leave it at
    /// 2003 (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    public long DocumentIdFor(SqlValue key)
    {
        lock (this.documentIds)
        {
            if (this.documentIds.TryGetValue(key, out var id))
                return id;
            id = this.lastDocumentId + 1;
            if (id % DocumentIdBlock == 0)
                id++;
            this.lastDocumentId = id;
            this.documentIds.Add(key, id);
            return id;
        }
    }

    /// <summary>
    /// Ends the initial crawl: the next id handed out starts the block after
    /// the last one it used.
    /// </summary>
    public void EndInitialCrawl()
    {
        lock (this.documentIds)
            this.lastDocumentId = ((this.lastDocumentId / DocumentIdBlock) + 1) * DocumentIdBlock;
    }

    /// <summary>The stride of real's document id blocks, the skipped id included.</summary>
    private const long DocumentIdBlock = 1001;
}

/// <summary>A full-text index's <c>CHANGE_TRACKING</c> mode.</summary>
internal enum FullTextChangeTracking
{
    Auto,
    Manual,
    Off,
}

/// <summary>
/// One column entry inside a <see cref="FullTextIndex"/>. Carries the column
/// ordinal (1-based, matching real SQL Server's <c>column_id</c>), the
/// language LCID for tokenizer/stemmer selection, and an optional
/// <c>TYPE COLUMN</c> id for varbinary/image columns that pair with a
/// separate column carrying the document extension (e.g. <c>'.docx'</c>).
/// </summary>
internal readonly struct FullTextIndexColumn(int columnId, int languageId, int? typeColumnId)
{
    public readonly int ColumnId = columnId;
    public readonly int LanguageId = languageId;
    public readonly int? TypeColumnId = typeColumnId;
}
