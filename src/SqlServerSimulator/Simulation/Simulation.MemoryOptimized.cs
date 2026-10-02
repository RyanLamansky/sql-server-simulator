using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// In-Memory OLTP's declaration rules: what a memory-optimized table or table
// type may declare, the hash index's shape, and the DDL real refuses on one
// (see docs/claude/memory-optimized.md).
partial class Simulation
{
    /// <summary>
    /// Refuses what a memory-optimized table declaration can't hold — a
    /// database without a container to put it in, a refused column type or
    /// feature, a clustered rowstore key or index, a filtered or covering
    /// index, a durable table without a primary key or any table without an
    /// index — and a hash index or key on a disk-based one. The order is
    /// real's where a probe showed two together (2026-10-02 against SQL Server
    /// 2025); <paramref name="database"/> is null for a table type, which needs
    /// no container.
    /// </summary>
    internal static void ValidateTableDeclaration(
        Database? database,
        string tableName,
        MemoryOptimizationOptions memoryOptimization,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<PendingInlineIndex> pendingIndexes)
    {
        var memoryOptimized = memoryOptimization.MemoryOptimized;
        foreach (var (_, name, _, _, options, _) in pendingKeys)
        {
            if (options.IsHash && options.BucketCount is null)
                throw SimulatedSqlException.BucketCountRequired(name ?? "", tableName);
        }
        foreach (var index in pendingIndexes)
        {
            if (index.Options.IsHash && index.Options.BucketCount is null)
                throw SimulatedSqlException.BucketCountRequired(index.Name, tableName);
        }
        if (!memoryOptimized)
        {
            if (pendingKeys.Exists(static key => key.Options.IsHash) || pendingIndexes.Exists(static index => index.Options.IsHash))
                throw SimulatedSqlException.HashIndexOnDiskTable();
            return;
        }

        if (database is not null)
            RequireMemoryOptimizedContainer(database);

        foreach (var column in heapColumns)
        {
            if (column is null)
                continue;
            if (MemoryOptimizedRefusedTypeName(column.Type) is { } typeName)
                throw SimulatedSqlException.NotSupportedWithMemoryOptimized($"The type '{typeName}'", 80);
            if (column.IsSparse)
                throw SimulatedSqlException.NotSupportedWithMemoryOptimized("The feature 'SPARSE'", 4);
            if (column.IsRowGuidCol)
                throw SimulatedSqlException.NotSupportedWithMemoryOptimized("The feature 'ROWGUIDCOL'", 5);
            if (column.Identity is { } identity && (identity.Seed != 1 || identity.Increment != 1))
                throw SimulatedSqlException.MemoryOptimizedIdentitySeed();
        }

        for (var i = 0; i < pendingKeys.Count; i++)
            RejectMemoryOptimizedIndexShape(IsClusteredKey(pendingKeys, i), isColumnstore: false, pendingKeys[i].Options, hasFilter: false, hasInclude: false);
        foreach (var index in pendingIndexes)
            RejectMemoryOptimizedIndexShape(index.IsClustered, index.IsColumnstore, index.Options, index.Filter is not null, index.IncludeColumnNames.Count > 0);

        if (memoryOptimization.Durability == 0 && !pendingKeys.Exists(static key => key.Kind == KeyConstraintKind.PrimaryKey))
            throw SimulatedSqlException.MemoryOptimizedRequiresPrimaryKey(tableName);
        if (pendingKeys.Count == 0 && pendingIndexes.Count == 0)
            throw SimulatedSqlException.MemoryOptimizedRequiresIndex(tableName);
    }

    /// <summary>
    /// Msg 41337 unless <paramref name="database"/> has a
    /// <c>MEMORY_OPTIMIZED_DATA</c> filegroup (state 100 when it hasn't) with
    /// at least one container (state 1 when it hasn't).
    /// </summary>
    internal static void RequireMemoryOptimizedContainer(Database database)
    {
        if (database.MemoryOptimizedFilegroupId == 0)
            throw SimulatedSqlException.MemoryOptimizedFilegroupMissing(100);
        foreach (var file in database.FilesInOrder())
        {
            if (file.IsContainer)
                return;
        }
        throw SimulatedSqlException.MemoryOptimizedFilegroupMissing(1);
    }

    /// <summary>
    /// The name Msg 10794 gives a column type a memory-optimized table refuses
    /// — the legacy LOBs, <c>xml</c>, <c>sql_variant</c>, <c>timestamp</c>, the
    /// CLR system types, <c>datetimeoffset</c>, <c>vector</c> and <c>json</c>
    /// (probed 2026-10-02 against SQL Server 2025) — or null for a type it
    /// takes, the MAX types included.
    /// </summary>
    internal static string? MemoryOptimizedRefusedTypeName(SqlType type) => type switch
    {
        XmlSqlType => "xml",
        TextSqlType => "text",
        NTextSqlType => "ntext",
        ImageSqlType => "image",
        SqlVariantSqlType => "sql_variant",
        RowVersionSqlType => "timestamp",
        GeographySqlType => "sys.geography",
        GeometrySqlType => "sys.geometry",
        HierarchyIdSqlType => "sys.hierarchyid",
        DateTimeOffsetSqlType offset => $"datetimeoffset({offset.precision})",
        VectorSqlType vector => $"vector({vector.dimensions})",
        JsonSqlType => "json",
        ClrUdtSqlType udt => udt.Udt.Name,
        _ => null,
    };

    /// <summary>
    /// Refuses a key or index added to an existing table that its kind can't
    /// hold: a missing <c>BUCKET_COUNT</c> (Msg 10789), a hash index on a
    /// disk-based table (Msg 10791), and on a memory-optimized one a clustered
    /// rowstore index (Msg 12317), a filter or an <c>INCLUDE</c> list.
    /// </summary>
    internal static void RejectIndexShapeForTable(HeapTable table, string? indexName, bool isClustered, bool isColumnstore, IndexOptions options, bool hasFilter, bool hasInclude)
    {
        if (options.IsHash && options.BucketCount is null)
            throw SimulatedSqlException.BucketCountRequired(indexName ?? "", table.Name);
        if (!table.IsMemoryOptimized)
        {
            if (options.IsHash)
                throw SimulatedSqlException.HashIndexOnDiskTable();
            return;
        }
        RejectMemoryOptimizedIndexShape(isClustered, isColumnstore, options, hasFilter, hasInclude);
    }

    /// <summary>
    /// The index shapes a memory-optimized table refuses: a clustered
    /// rowstore index (Msg 12317), a filter, an <c>INCLUDE</c> list,
    /// <c>BUCKET_COUNT</c> on a range index (Msg 10790), and the fill-factor
    /// options (Msg 10794 state 81) — probed 2026-10-02 against SQL Server
    /// 2025.
    /// </summary>
    private static void RejectMemoryOptimizedIndexShape(bool isClustered, bool isColumnstore, IndexOptions options, bool hasFilter, bool hasInclude)
    {
        if (isClustered && !isColumnstore)
            throw SimulatedSqlException.ClusteredIndexOnMemoryOptimized();
        if (hasFilter)
            throw SimulatedSqlException.FilteredIndexOnMemoryOptimized();
        if (hasInclude)
            throw SimulatedSqlException.IncludedColumnsOnMemoryOptimized();
        if (!options.IsHash && options.BucketCount is not null)
            throw SimulatedSqlException.BucketCountOnRangeIndex();
        if (options.FillFactor is not null)
            throw SimulatedSqlException.IndexOptionOnMemoryOptimized("fillfactor");
        if (options.PadIndex is not null)
            throw SimulatedSqlException.IndexOptionOnMemoryOptimized("pad_index");
    }

    /// <summary>
    /// Msg 10794 for a DDL operation a memory-optimized table refuses —
    /// <c>CREATE</c> / <c>DROP</c> / <c>ALTER INDEX</c>, <c>TRUNCATE</c>,
    /// <c>SWITCH</c>, <c>REBUILD</c> and the rest — when
    /// <paramref name="table"/> is one; <paramref name="what"/> is the subject
    /// phrase and <paramref name="state"/> real's state for the site.
    /// </summary>
    internal static void RejectOnMemoryOptimized(HeapTable table, string what, byte state)
    {
        if (table.IsMemoryOptimized)
            throw SimulatedSqlException.NotSupportedWithMemoryOptimized(what, state);
    }
}
