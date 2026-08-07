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
/// range seek over an incrementally-maintained ordered view, and an <c>ORDER
/// BY</c> matching a NOT-NULL leading prefix (one or several columns, optionally
/// after an equality-pinned prefix) streams in key order instead of sorting
/// (ORDER BY elimination). UNIQUE indexes also
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
    int[] includedColumnOrdinals,
    BooleanExpression? filter,
    string? filterDefinition,
    bool ignoreDupKey)
{
    // Mutable: EXEC sp_rename (INDEX rename) reassigns the name in place; the
    // index keeps its identity and surfaces the new name through sys.indexes.
    public string Name = name;

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
    /// <see cref="KeyColumns"/>' storage ordinals alone, in declaration order —
    /// the prefix shape the per-<c>Heap</c> seek cache keys an entry on.
    /// Materialized once here so the per-row uniqueness seek doesn't rebuild it
    /// out of <see cref="KeyColumns"/> on every inserted or updated row.
    /// Mutable for the same reason <c>KeyConstraint.StorageOrdinals</c> is:
    /// <c>ALTER TABLE … DROP COLUMN</c> shifts every later storage slot down and
    /// remaps both in place, in one loop, so this can't drift from
    /// <see cref="KeyColumns"/>.
    /// </summary>
    public readonly int[] KeyStorageOrdinals = BuildKeyStorageOrdinals(keyColumns);

    /// <summary>
    /// Whether every key column has a storage slot, which the uniqueness
    /// enforcement reads keys through. False only for an index keyed on a
    /// <em>non-persisted</em> computed column — real enforces one (it is what
    /// backs AdventureWorks' <c>AK_SalesOrderHeader_SalesOrderNumber</c>), the
    /// simulator carries it as metadata and leaves it unenforced, since the
    /// existing-row side of the check reads stored bytes rather than
    /// re-evaluating an expression per row.
    /// </summary>
    public bool KeysAreStored
    {
        get
        {
            foreach (var ordinal in this.KeyStorageOrdinals)
            {
                if (ordinal < 0)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// INCLUDE-clause column storage ordinals, in declaration order. Empty
    /// when no INCLUDE was specified. A non-persisted computed column has
    /// no storage slot, so its entry is <c>-1</c> — ambiguous across
    /// computed columns, which is why the catalog reads
    /// <see cref="IncludedColumnOrdinals"/> instead.
    /// </summary>
    public readonly int[] IncludedColumns = includedColumns;

    /// <summary>
    /// INCLUDE-clause full column ordinals (0-based positions in
    /// <c>HeapTable.Columns</c>), parallel to <see cref="IncludedColumns"/>.
    /// The source for <c>sys.index_columns.column_id</c> — unambiguous even
    /// for non-persisted computed columns, whose shared <c>-1</c> storage
    /// ordinal collapsed the catalog mapping onto the wrong column (WWI's
    /// <c>IX_Sales_Invoices_ConfirmedDeliveryTime</c> scripted
    /// <c>INCLUDE</c> of its own key column, which real SQL Server rejects
    /// at import with Msg 1909).
    /// </summary>
    public readonly int[] IncludedColumnOrdinals = includedColumnOrdinals;

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

    /// <summary>
    /// <c>IGNORE_DUP_KEY</c>: an INSERT whose row would duplicate this index's
    /// key skips that row and continues, instead of raising Msg 2601. Only
    /// meaningful on a UNIQUE index — real rejects the option on a non-unique or
    /// filtered one, so this is false for both. Mutable because
    /// <c>ALTER INDEX … SET (IGNORE_DUP_KEY = …)</c> toggles it in place, the
    /// same way <see cref="Name"/> is mutable for <c>sp_rename</c>.
    /// Surfaces as <c>sys.indexes.ignore_dup_key</c>.
    /// See <c>docs/claude/constraints.md</c>.
    /// </summary>
    public bool IgnoreDupKey = ignoreDupKey;

    /// <summary>
    /// Whether <c>ALTER INDEX … DISABLE</c> has taken this index out of service.
    /// A disabled UNIQUE index stops being enforced entirely — duplicates insert
    /// freely — and <c>ALTER INDEX … REBUILD</c> puts it back, re-validating the
    /// rows that accumulated meanwhile (Msg 1505 if any duplicate did).
    /// A disabled <b>clustered</b> index goes further and locks the whole table:
    /// every query and DML against it raises Msg 8655.
    /// Surfaces as <c>sys.indexes.is_disabled</c>.
    /// See <c>docs/claude/indexes.md</c>.
    /// </summary>
    public bool IsDisabled;

    private static int[] BuildKeyStorageOrdinals(IndexKeyColumn[] keyColumns)
    {
        var ordinals = new int[keyColumns.Length];
        for (var i = 0; i < keyColumns.Length; i++)
            ordinals[i] = keyColumns[i].StorageOrdinal;
        return ordinals;
    }
}

/// <summary>
/// One key column inside an <see cref="Index"/>: a storage ordinal (for
/// the enforcement / seek paths that decode row bytes), the full column
/// ordinal (for the catalog — a non-persisted computed key column's
/// storage ordinal is the ambiguous <c>-1</c>), plus the ASC / DESC flag
/// captured at CREATE INDEX time. The DESC flag has no runtime effect (no
/// real index order) but surfaces in
/// <c>sys.index_columns.is_descending_key</c>.
/// </summary>
internal readonly struct IndexKeyColumn(int storageOrdinal, int columnOrdinal, bool isDescending)
{
    public readonly int StorageOrdinal = storageOrdinal;

    /// <summary>0-based position in <c>HeapTable.Columns</c>; the source for <c>sys.index_columns.column_id</c>.</summary>
    public readonly int ColumnOrdinal = columnOrdinal;

    public readonly bool IsDescending = isDescending;
}
