namespace SqlServerSimulator.Schemas;

/// <summary>
/// One SQL Server 2025 JSON index on a table's <c>json</c> column, created by
/// <c>CREATE JSON INDEX name ON table (col) [FOR ('path', …)] [WITH (…)]</c>.
/// Catalog metadata only — reads never go through it — surfaced by
/// <c>sys.indexes</c> (type 9, <c>JSON</c>), <c>sys.index_columns</c>,
/// <c>sys.json_indexes</c> and <c>sys.json_index_paths</c>.
/// </summary>
internal sealed class JsonIndex(
    string name,
    int columnOrdinal,
    int indexId,
    string[] paths,
    byte fillFactor,
    bool isPadded,
    bool allowRowLocks,
    bool allowPageLocks,
    bool optimizeForArraySearch)
{
    /// <summary>The first <c>index_id</c> of real's JSON-index range; a table's next is one past its highest.</summary>
    public const int IndexIdBase = 1216000;

    public string Name = name;

    /// <summary>
    /// 0-based ordinal of the indexed column among the table's columns, kept
    /// current when <c>ALTER TABLE … DROP COLUMN</c> removes an earlier one.
    /// </summary>
    public int ColumnOrdinal = columnOrdinal;

    public readonly int IndexId = indexId;

    /// <summary>
    /// The <c>FOR</c> clause's paths exactly as written, or <c>$</c> alone
    /// without one — what <c>sys.json_index_paths</c> reports.
    /// </summary>
    public readonly string[] Paths = paths;

    public readonly byte FillFactor = fillFactor;
    public readonly bool IsPadded = isPadded;
    public readonly bool AllowRowLocks = allowRowLocks;
    public readonly bool AllowPageLocks = allowPageLocks;
    public readonly bool OptimizeForArraySearch = optimizeForArraySearch;

    /// <summary>Set by <c>ALTER INDEX … DISABLE</c>, cleared by <c>REBUILD</c>.</summary>
    public bool IsDisabled;
}
