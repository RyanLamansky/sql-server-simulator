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
    IndexOptions options,
    bool isColumnstore = false,
    int[]? columnstoreOrder = null)
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
    /// The same key columns as positions in <c>HeapTable.Columns</c>, parallel
    /// to <see cref="KeyStorageOrdinals"/> — what names a non-persisted
    /// computed key column, whose storage entry is <c>-1</c>. Materialized here
    /// so the enforcement paths don't rebuild it out of
    /// <see cref="KeyColumns"/> per row.
    /// </summary>
    public readonly int[] KeyFullOrdinals = BuildKeyFullOrdinals(keyColumns);

    /// <summary>
    /// Whether every key column has a storage slot, which is what the <b>seek</b>
    /// path reads keys through. False only for an index keyed on a
    /// <em>non-persisted</em> computed column — the shape backing
    /// AdventureWorks' <c>AK_SalesOrderHeader_SalesOrderNumber</c> — whose
    /// uniqueness is enforced instead by evaluating the expression per row
    /// against a statement-scoped key set.
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
    /// The <c>index_id</c> this index keeps for life, or 0 while none is
    /// assigned; see <see cref="HeapTable.SettleIndexIds"/>.
    /// </summary>
    public int IndexId;

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
    public bool IgnoreDupKey = options.IgnoreDupKey;

    /// <summary>
    /// <c>sys.indexes.fill_factor</c> / <c>is_padded</c>: the declaration's
    /// <c>FILLFACTOR</c> / <c>PAD_INDEX</c>, which an <c>ALTER INDEX …
    /// REBUILD WITH</c> may change.
    /// </summary>
    public byte FillFactor = options.FillFactor ?? 0;

    /// <summary>
    /// A memory-optimized hash index's bucket count — <c>BUCKET_COUNT</c>
    /// rounded up to a power of two, as <c>sys.hash_indexes</c> reports it —
    /// or 0 for a range index. Mutable for <c>ALTER TABLE … ALTER INDEX …
    /// REBUILD WITH (BUCKET_COUNT = n)</c>.
    /// </summary>
    public int BucketCount = IndexOptions.BucketCountFor(options);

    /// <summary>Whether this is a hash index (<see cref="BucketCount"/> above 0).</summary>
    public bool IsHash => this.BucketCount > 0;

    /// <inheritdoc cref="FillFactor"/>
    public bool IsPadded = options.PadIndex ?? false;

    /// <summary>
    /// <c>sys.indexes.allow_row_locks</c> / <c>allow_page_locks</c> /
    /// <c>optimize_for_sequential_key</c>: the declaration's locking options,
    /// which an <c>ALTER INDEX … REBUILD WITH</c> or <c>SET</c> may change. The
    /// simulator's lock manager doesn't consult them; the catalog and
    /// <c>INDEXPROPERTY</c> report them.
    /// </summary>
    public bool AllowRowLocks = options.AllowRowLocks ?? true;

    /// <inheritdoc cref="AllowRowLocks"/>
    public bool AllowPageLocks = options.AllowPageLocks ?? true;

    /// <inheritdoc cref="AllowRowLocks"/>
    public bool OptimizeForSequentialKey = options.OptimizeForSequentialKey ?? false;

    /// <summary>
    /// <c>sys.stats.no_recompute</c> of the index's statistic: <c>STATISTICS_NORECOMPUTE</c>
    /// declared, or set since by <c>ALTER INDEX</c>, <c>UPDATE STATISTICS</c> or <c>sp_autostats</c>.
    /// </summary>
    public bool StatisticsNoRecompute = options.StatisticsNoRecompute ?? false;

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

    /// <summary>
    /// A hypothetical index (<c>WITH STATISTICS_ONLY = n</c>), kept in
    /// <see cref="HeapTable.HypotheticalIndexes"/> rather than
    /// <see cref="HeapTable.Indexes"/>: the catalog lists it and its statistic,
    /// but no access path, uniqueness check or index hint ever reaches it.
    /// </summary>
    public readonly bool IsHypothetical = options.StatisticsOnly;

    /// <summary>
    /// The partition scheme and column a nonclustered one is placed on, or
    /// null when it sits on a filegroup; a clustered one follows its table's
    /// <see cref="HeapTable.Partitioning"/> instead.
    /// </summary>
    public Schemas.PartitionPlacement? Partitioning;

    /// <summary>
    /// A nonclustered index's filegroup when <see cref="Partitioning"/> is null:
    /// its own <c>ON</c> clause's, else the table's rows' at creation. A
    /// clustered index's placement is the table's (<c>HeapTable.FilegroupId</c>).
    /// </summary>
    public int FilegroupId = Database.PrimaryFilegroupId;

    /// <summary>
    /// The <c>ON</c> clause the declaration wrote, kept until the owning
    /// statement resolves it into <see cref="Partitioning"/>.
    /// </summary>
    public readonly Schemas.DataSpaceClause? WrittenDataSpace = options.DataSpace;

    /// <summary>
    /// A columnstore index: <c>sys.indexes</c> type 5 / 6. It has no key; a
    /// nonclustered one's columns ride in <see cref="IncludedColumns"/>, which
    /// is how <c>sys.index_columns</c> lists them, and a clustered one covers
    /// every column the table has or later gains. Neither seeks nor enforces
    /// anything. See <c>docs/claude/indexes.md</c>.
    /// </summary>
    public readonly bool IsColumnstore = isColumnstore;

    /// <summary>
    /// A columnstore index's <c>ORDER</c> columns as <c>HeapTable.Columns</c>
    /// ordinals, in order — <c>sys.index_columns.column_store_order_ordinal</c>.
    /// </summary>
    public readonly int[] ColumnstoreOrder = columnstoreOrder ?? [];

    /// <summary>
    /// A columnstore index's <c>COMPRESSION_DELAY</c> in minutes, which
    /// <c>ALTER INDEX … SET</c> may change; <c>sys.indexes</c> reads NULL for
    /// a rowstore index.
    /// </summary>
    public int CompressionDelay = options.CompressionDelay ?? 0;

    /// <summary>
    /// Whether a columnstore index is <c>COLUMNSTORE_ARCHIVE</c>-compressed,
    /// which a rebuild may change — <c>sys.partitions.data_compression</c>.
    /// </summary>
    public bool ColumnstoreArchive = options.ColumnstoreArchive ?? false;

    private static int[] BuildKeyFullOrdinals(IndexKeyColumn[] keyColumns)
    {
        var ordinals = new int[keyColumns.Length];
        for (var i = 0; i < keyColumns.Length; i++)
            ordinals[i] = keyColumns[i].ColumnOrdinal;
        return ordinals;
    }

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
