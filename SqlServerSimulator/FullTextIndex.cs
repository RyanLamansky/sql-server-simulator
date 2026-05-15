using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One full-text index attached to a heap table — at most one per table
/// (real SQL Server's invariant; the simulator stores it directly on
/// <see cref="HeapTable.FullTextIndex"/> as a single nullable slot rather
/// than a list). Created via
/// <c>CREATE FULLTEXT INDEX ON table (col [LANGUAGE n][, ...]) KEY INDEX
/// unique_index_name [ON catalog]</c>.
/// </summary>
/// <remarks>
/// Like <see cref="FullTextCatalog"/>, this exists for catalog-view
/// round-trip + AW model.xml load. The simulator never indexes column
/// values for text search; CONTAINS / FREETEXT / CONTAINSTABLE /
/// FREETEXTTABLE all raise <see cref="NotSupportedException"/>.
/// </remarks>
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
