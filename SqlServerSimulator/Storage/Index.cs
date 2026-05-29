using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// One entry in <see cref="HeapTable.Indexes"/>: a CREATE INDEX-declared
/// secondary index. Stores key columns (with per-column ASC / DESC order),
/// optional INCLUDE columns, optional WHERE filter (only honored for
/// UNIQUE-with-filter enforcement), and the UNIQUE / CLUSTERED flags.
/// </summary>
/// <remarks>
/// <para>
/// The simulator has no B-tree storage, so an index never constrains inserts
/// (UNIQUE aside) and is pure catalog metadata for <c>sys.indexes</c> /
/// <c>sys.index_columns</c>. It does, however, accelerate <b>equality seeks</b>
/// on its leading key column: a single-base-table scan whose WHERE carries a
/// <c>leadingKeyColumn = &lt;stable value&gt;</c> conjunct narrows to a lazy
/// per-table hash index instead of a full scan (see
/// <c>Selection.Execution.IndexSeek.cs</c>) — the path that collapses
/// correlated <c>EXISTS</c> / <c>IN</c> / scalar subqueries from O(outer ×
/// inner) toward linear. A range bound on the leading key column
/// (<c>col &gt; v</c> / <c>col BETWEEN lo AND hi</c>) likewise narrows to a
/// range seek over an incrementally-maintained ordered view, and a single
/// NOT-NULL leading-key-column <c>ORDER BY</c> streams in key order instead of
/// sorting (ORDER BY elimination). UNIQUE indexes also
/// enforce the same multiset rule the existing UNIQUE constraint path uses
/// (one NULL allowed, second NULL raises Msg 2601), plus the filter-aware
/// extension when <see cref="Filter"/> is non-null: only rows for which the
/// filter evaluates true participate in the uniqueness check.
/// </para>
/// <para>
/// The CLUSTERED keyword on a CREATE INDEX is accepted but doesn't change
/// storage — the simulator has no row-ordered heap, so every index is
/// effectively non-clustered.
/// </para>
/// </remarks>
internal sealed class Index(
    string name,
    int objectId,
    bool isUnique,
    bool isClustered,
    IndexKeyColumn[] keyColumns,
    int[] includedColumns,
    BooleanExpression? filter,
    string? filterDefinition)
{
    public readonly string Name = name;

    public readonly int ObjectId = objectId;

    public readonly bool IsUnique = isUnique;

    public readonly bool IsClustered = isClustered;

    /// <summary>
    /// Key columns in declaration order. Each entry pairs a storage ordinal
    /// with a per-column ASC / DESC flag. Storage ordinals (not declaration
    /// ordinals) so the enforcement loop can decode key columns directly
    /// from row bytes.
    /// </summary>
    public readonly IndexKeyColumn[] KeyColumns = keyColumns;

    /// <summary>
    /// INCLUDE-clause column storage ordinals, in declaration order. Empty
    /// when no INCLUDE was specified. The simulator stores them for
    /// <c>sys.index_columns</c> visibility; they have no runtime effect.
    /// </summary>
    public readonly int[] IncludedColumns = includedColumns;

    /// <summary>
    /// Optional WHERE filter — only honored on UNIQUE indexes (a row is
    /// included in the uniqueness check only when the filter evaluates
    /// true). Null when no WHERE clause was given.
    /// </summary>
    public readonly BooleanExpression? Filter = filter;

    /// <summary>
    /// Original WHERE filter text, recorded at CREATE INDEX time for the
    /// <c>sys.indexes.filter_definition</c> column. Null when no WHERE was
    /// given.
    /// </summary>
    public readonly string? FilterDefinition = filterDefinition;
}

/// <summary>
/// One key column inside an <see cref="Index"/>: a storage ordinal plus
/// the ASC / DESC flag captured at CREATE INDEX time. The DESC flag has
/// no runtime effect (no real index order) but surfaces in
/// <c>sys.index_columns.is_descending_key</c>.
/// </summary>
internal readonly struct IndexKeyColumn(int storageOrdinal, bool isDescending)
{
    public readonly int StorageOrdinal = storageOrdinal;

    public readonly bool IsDescending = isDescending;
}
