namespace SqlServerSimulator.Schemas;

/// <summary>
/// One SQL Server 2025 vector index on a table's <c>vector</c> column,
/// created by <c>CREATE VECTOR INDEX name ON table (col) WITH (METRIC = …)</c>.
/// Catalog metadata plus the metric <c>VECTOR_SEARCH</c> must name to be
/// served — the search itself reads the table exactly, so no graph is kept.
/// Surfaced by <c>sys.indexes</c> (type 8, <c>VECTOR</c>),
/// <c>sys.index_columns</c> and <c>sys.vector_indexes</c>; while one exists
/// the table refuses every write (Msg 42231 / 42232).
/// </summary>
internal sealed class VectorIndex(string name, int columnOrdinal, int indexId, string metric, string startId)
{
    /// <summary>The first <c>index_id</c> of real's vector-index range; a table's next is one past its highest.</summary>
    public const int IndexIdBase = 1152000;

    public string Name = name;

    /// <summary>
    /// 0-based ordinal of the indexed column among the table's columns, kept
    /// current when <c>ALTER TABLE … DROP COLUMN</c> removes an earlier one.
    /// </summary>
    public int ColumnOrdinal = columnOrdinal;

    public readonly int IndexId = indexId;

    /// <summary><c>COSINE</c>, <c>DOT</c> or <c>EUCLIDEAN</c>, as <c>sys.vector_indexes.distance_metric</c> reports it.</summary>
    public readonly string Metric = metric;

    /// <summary>
    /// The clustered key of the row the build started its graph from, as
    /// text — the <c>StartId</c> of <c>build_parameters</c>; <c>0</c> for a
    /// table with no vectors.
    /// </summary>
    public readonly string StartId = startId;

    /// <summary>The <c>build_parameters</c> text real reports, whose search-list and degree bounds are fixed.</summary>
    public string BuildParameters => $$"""{"StartId":"{{this.StartId}}", "L":"48", "M":"8", "R":"48"}""";
}
