using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterIndexes(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // sys.indexes: probe-confirmed 24-column shape against SQL Server
        // 2025 (2026-05-14). One row per (table, index) — PK / UQ
        // constraints surface alongside CREATE-INDEX rows, and a HEAP row
        // (index_id = 0, type = 0, name = NULL) appears for any table with
        // no PRIMARY KEY (matching SQL Server's "the table itself is the
        // heap" semantic). EF Migrations introspection reads name /
        // is_unique / is_primary_key / is_unique_constraint /
        // has_filter / filter_definition.
        Sys("indexes",
        [
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_unique", SqlType.Bit, null, false),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, false),
            new("is_primary_key", SqlType.Bit, null, false),
            new("is_unique_constraint", SqlType.Bit, null, false),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_hypothetical", SqlType.Bit, null, false),
            new("is_ignored_in_optimization", SqlType.Bit, null, false),
            new("allow_row_locks", SqlType.Bit, null, false),
            new("allow_page_locks", SqlType.Bit, null, false),
            new("has_filter", SqlType.Bit, null, false),
            new("filter_definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("compression_delay", SqlType.Int32, null, true),
            new("suppress_dup_key_messages", SqlType.Bit, null, false),
            new("auto_created", SqlType.Bit, null, false),
            new("optimize_for_sequential_key", SqlType.Bit, null, false),
            new("statistics_incremental", SqlType.Bit, null, true),
        ], EnumerateSysIndexes);

        // sys.data_spaces: the simulator models a single PRIMARY row-filegroup
        // (data_space_id = 1) — the same id every sys.indexes row reports for
        // data_space_id, so SMO's LEFT JOIN idx.data_space_id → dsidx resolves.
        // Probe-confirmed shape (SQL Server 2025): name / data_space_id / type
        // char(2) / type_desc / is_default / is_system. 'FG' = ROWS_FILEGROUP;
        // partition schemes ('PS' — what SMO's IsPartitioned probe compares
        // against) aren't modeled, so type is always 'FG'. See
        // docs/claude/catalog-views.md.
        var filegroupType = SqlValue.FromChar(charTwo, "FG");
        var filegroupTypeDesc = SqlValue.FromNVarchar("ROWS_FILEGROUP");
        Sys("data_spaces",
        [
            new("name", SqlType.SystemName, 128, false),
            new("data_space_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_default", SqlType.Bit, null, true),
            new("is_system", SqlType.Bit, null, true),
        ], (batch, database) =>
        {
            _ = (batch, database);
            return
            [
                [
                    SqlValue.FromSystemName("PRIMARY"),
                    SqlValue.FromInt32(1),
                    filegroupType,
                    filegroupTypeDesc,
                    SqlValue.FromBoolean(true),
                    SqlValue.FromBoolean(false),
                ],
            ];
        });

        // sys.filegroups: the row-filegroup subset of sys.data_spaces — the
        // simulator's single PRIMARY filegroup (data_space_id = 1). Adds the
        // filegroup-specific columns (filegroup_guid / log_filegroup_id /
        // is_read_only / is_autogrow_all_files) SMO's CREATE-scripting index /
        // filegroup queries read. Probe-confirmed PRIMARY row (SQL Server 2025):
        // is_default = 1, is_system = 0, the rest NULL / 0.
        Sys("filegroups",
        [
            new("name", SqlType.SystemName, 128, false),
            new("data_space_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_default", SqlType.Bit, null, true),
            new("is_system", SqlType.Bit, null, true),
            new("filegroup_guid", SqlType.UniqueIdentifier, null, true),
            new("log_filegroup_id", SqlType.Int32, null, true),
            new("is_read_only", SqlType.Bit, null, true),
            new("is_autogrow_all_files", SqlType.Bit, null, true),
        ], (batch, database) =>
        {
            _ = (batch, database);
            return
            [
                [
                    SqlValue.FromSystemName("PRIMARY"),
                    SqlValue.FromInt32(1),
                    filegroupType,
                    filegroupTypeDesc,
                    SqlValue.FromBoolean(true),
                    SqlValue.FromBoolean(false),
                    SqlValue.Null(SqlType.UniqueIdentifier),
                    SqlValue.Null(SqlType.Int32),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromBoolean(false),
                ],
            ];
        });

        // sys.index_columns: probe-confirmed 10-column shape. One row per
        // (index, column) pair — KEY columns get key_ordinal = 1..N and
        // index_column_id = 1..N; INCLUDE columns get key_ordinal = 0 and
        // index_column_id continuing past the key column count.
        Sys("index_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("index_column_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("key_ordinal", SqlType.TinyInt, null, false),
            new("partition_ordinal", SqlType.TinyInt, null, false),
            new("is_descending_key", SqlType.Bit, null, false),
            new("is_included_column", SqlType.Bit, null, false),
            new("column_store_order_ordinal", SqlType.TinyInt, null, true),
            new("data_clustering_ordinal", SqlType.TinyInt, null, true),
        ], EnumerateSysIndexColumns);

        // sys.partitions: probe-confirmed 11-column shape against SQL Server
        // 2025 (2026-07-15). One row per (object_id, index_id) that
        // sys.indexes reports — the heap row (index_id = 0) or clustered
        // (index_id = 1) plus every nonclustered index — all with
        // partition_number = 1 (the simulator models a single, unpartitioned
        // partition per index/heap). rows carries the table's live row count
        // (HeapTable.Heap.RowCount), so it tracks INSERT/DELETE within the
        // same batch. partition_id / hobt_id are synthetic-deterministic
        // (distinct per object_id/index_id, not byte-matching SQL Server's
        // allocation-unit ids). Compression isn't modeled, so
        // data_compression = 0 (NONE) / xml_compression = 0 (OFF) always —
        // a divergence from compression-enabled bacpacs (e.g. WWI-Full's
        // PAGE compression). See docs/claude/catalog-views.md.
        var varchar3Catalog = VarcharSqlType.Get(3, Collation.Catalog, Coercibility.Implicit);
        Sys("partitions",
        [
            new("partition_id", SqlType.BigInt, null, false),
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("partition_number", SqlType.Int32, null, false),
            new("hobt_id", SqlType.BigInt, null, false),
            new("rows", SqlType.BigInt, null, true),
            new("filestream_filegroup_id", SqlType.SmallInt, null, false),
            new("data_compression", SqlType.TinyInt, null, false),
            new("data_compression_desc", nvarchar60Catalog, 60, true),
            new("xml_compression", SqlType.Bit, null, true),
            new("xml_compression_desc", varchar3Catalog, 3, true),
        ], EnumerateSysPartitions);

        // sys.stats: one row per index sys.indexes reports, excluding the
        // heap (index_id = 0, which carries no statistics). stats_id =
        // index_id and name = index name, matching real SQL Server's
        // "an index-backing statistic shares the index's id and name". The
        // simulator does NOT model auto-created column statistics (the
        // _WA_Sys_* rows real SQL Server materializes on first predicate use),
        // so auto_created / user_created are always 0 and no column-only stats
        // appear — a divergence documented in catalog-views.md. Probe-confirmed
        // 17-column shape (SQL Server 2025, 2026-07-15).
        Sys("stats",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("stats_id", SqlType.Int32, null, false),
            new("auto_created", SqlType.Bit, null, true),
            new("user_created", SqlType.Bit, null, true),
            new("no_recompute", SqlType.Bit, null, true),
            new("has_filter", SqlType.Bit, null, true),
            new("filter_definition", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
            new("is_temporary", SqlType.Bit, null, true),
            new("is_incremental", SqlType.Bit, null, true),
            new("has_persisted_sample", SqlType.Bit, null, true),
            new("stats_generation_method", SqlType.Int32, null, false),
            new("stats_generation_method_desc", VarcharSqlType.Get(80, Collation.Catalog, Coercibility.Implicit), 80, false),
            new("auto_drop", SqlType.Bit, null, true),
            new("replica_role_id", SqlType.TinyInt, null, true),
            new("replica_role_desc", nvarchar60Catalog, 60, true),
            new("replica_name", SqlType.SystemName, 128, true),
        ], EnumerateSysStats);

        // sys.internal_tables / sys.hash_indexes / sys.json_indexes /
        // sys.index_resumable_operations / sys.selective_xml_index_paths /
        // sys.filetable_system_defined_objects: features the simulator doesn't
        // model (system internal tables, memory-optimized hash indexes, JSON
        // indexes, resumable index builds, selective XML indexes, FileTables).
        // Each ships as an empty view with the probe-confirmed full column
        // shape (SQL Server 2025, 2026-07-15) so that SMO's index-enumeration
        // mega-query — which LEFT JOINs all six and reads specific columns —
        // resolves every reference without Msg 207 and returns the correct
        // rows. The AlwaysOn-DMV precedent (full shape, zero rows).
        Sys("internal_tables",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, true),
            new("is_schema_published", SqlType.Bit, null, true),
            new("internal_type", SqlType.TinyInt, null, true),
            new("internal_type_desc", nvarchar60Catalog, 60, true),
            new("parent_id", SqlType.Int32, null, true),
            new("parent_minor_id", SqlType.Int32, null, true),
            new("lob_data_space_id", SqlType.Int32, null, false),
            new("filestream_data_space_id", SqlType.Int32, null, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("hash_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_unique", SqlType.Bit, null, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, true),
            new("is_primary_key", SqlType.Bit, null, true),
            new("is_unique_constraint", SqlType.Bit, null, true),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, true),
            new("is_disabled", SqlType.Bit, null, true),
            new("is_hypothetical", SqlType.Bit, null, true),
            new("is_ignored_in_optimization", SqlType.Bit, null, true),
            new("allow_row_locks", SqlType.Bit, null, true),
            new("allow_page_locks", SqlType.Bit, null, true),
            new("has_filter", SqlType.Bit, null, true),
            new("filter_definition", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
            new("bucket_count", SqlType.Int32, null, false),
            new("auto_created", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("json_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_unique", SqlType.Bit, null, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, true),
            new("is_primary_key", SqlType.Bit, null, true),
            new("is_unique_constraint", SqlType.Bit, null, true),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, true),
            new("is_disabled", SqlType.Bit, null, true),
            new("is_hypothetical", SqlType.Bit, null, true),
            new("is_ignored_in_optimization", SqlType.Bit, null, true),
            new("allow_row_locks", SqlType.Bit, null, true),
            new("allow_page_locks", SqlType.Bit, null, true),
            new("has_filter", SqlType.Bit, null, false),
            new("filter_definition", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
            new("auto_created", SqlType.Bit, null, true),
            new("optimize_for_array_search", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("index_resumable_operations",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("sql_text", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
            new("last_max_dop_used", SqlType.SmallInt, null, false),
            new("partition_number", SqlType.Int32, null, true),
            new("state", SqlType.TinyInt, null, false),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("start_time", SqlType.DateTime, null, false),
            new("last_pause_time", SqlType.DateTime, null, true),
            new("total_execution_time", SqlType.Int32, null, false),
            new("percent_complete", SqlType.Float, null, false),
            new("page_count", SqlType.BigInt, null, false),
        ], static (_, _) => EmptyCatalogRows);
        Sys("selective_xml_index_paths",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("path_id", SqlType.Int32, null, true),
            new("path", SqlType.NVarchar, 4000, true),
            new("name", SqlType.SystemName, 128, true),
            new("path_type", SqlType.TinyInt, null, true),
            new("path_type_desc", NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), 128, true),
            new("xml_component_id", SqlType.Int32, null, true),
            new("xquery_type_description", SqlType.NVarchar, 4000, true),
            new("is_xquery_type_inferred", SqlType.Bit, null, true),
            new("xquery_max_length", SqlType.Int32, null, true),
            new("is_xquery_max_length_inferred", SqlType.Bit, null, true),
            new("is_node", SqlType.Bit, null, true),
            new("system_type_id", SqlType.TinyInt, null, true),
            new("user_type_id", SqlType.TinyInt, null, true),
            new("max_length", SqlType.SmallInt, null, true),
            new("precision", SqlType.TinyInt, null, true),
            new("scale", SqlType.TinyInt, null, true),
            new("collation_name", SqlType.NVarchar, 128, true),
            new("is_singleton", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("filetable_system_defined_objects",
        [
            new("object_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>
    /// Rows for <c>sys.indexes</c>: one row per (table, index) — PK / UQ
    /// from <see cref="HeapTable.KeyConstraints"/>, plus
    /// <c>CREATE INDEX</c>-declared entries from <see cref="HeapTable.Indexes"/>.
    /// PK gets index_id = 1 with type_desc = CLUSTERED; tables without a
    /// PK emit a HEAP row (index_id = 0, name = NULL). Remaining UQ /
    /// user indexes get index_id starting at 2 in <c>ObjectId</c> order
    /// (the simulator allocates object ids monotonically, so this matches
    /// declaration order).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysIndexes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        // compression_delay is NULL for every rowstore index (probe-confirmed);
        // it carries a minute-delay only for columnstore, which isn't modeled.
        var nullCompressionDelay = SqlValue.Null(SqlType.Int32);
        var nullName = SqlValue.Null(SqlType.SystemName);
        var nullFilter = SqlValue.Null(SqlType.NVarchar);
        var heapDesc = SqlValue.FromNVarchar("HEAP");
        var clusteredDesc = SqlValue.FromNVarchar("CLUSTERED");
        var nonClusteredDesc = SqlValue.FromNVarchar("NONCLUSTERED");
        var primaryDataSpace = SqlValue.FromInt32(1);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                var tableObjectId = SqlValue.FromInt32(table.ObjectId);
                KeyConstraint? primaryKey = null;
                foreach (var k in table.KeyConstraints)
                {
                    if (k.Kind == KeyConstraintKind.PrimaryKey)
                    {
                        primaryKey = k;
                        break;
                    }
                }
                var hasPk = primaryKey is not null;
                var nextIndexId = hasPk ? 2 : 1;
                yield return BuildIndexRow(
                    name: hasPk ? SqlValue.FromSystemName(primaryKey!.Name) : nullName,
                    objectId: tableObjectId,
                    indexId: SqlValue.FromInt32(hasPk ? 1 : 0),
                    type: hasPk ? SqlValue.FromByte(1) : zeroByte,
                    typeDesc: hasPk ? clusteredDesc : heapDesc,
                    isUnique: hasPk ? trueBit : falseBit,
                    dataSpaceId: primaryDataSpace,
                    isPrimaryKey: hasPk ? trueBit : falseBit,
                    isUniqueConstraint: falseBit,
                    hasFilter: falseBit,
                    filterDefinition: nullFilter,
                    falseBit, trueBit, zeroByte);

                var others = new List<(int ObjectId, KeyConstraint? Key, Storage.Index? Index)>();
                foreach (var k in table.KeyConstraints)
                {
                    if (!ReferenceEquals(k, primaryKey))
                        others.Add((k.ObjectId, k, null));
                }
                foreach (var ix in table.Indexes)
                    others.Add((ix.ObjectId, null, ix));
                others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

                foreach (var (_, key, index) in others)
                {
                    if (key is not null)
                    {
                        yield return BuildIndexRow(
                            name: SqlValue.FromSystemName(key.Name),
                            objectId: tableObjectId,
                            indexId: SqlValue.FromInt32(nextIndexId++),
                            type: SqlValue.FromByte(2),
                            typeDesc: nonClusteredDesc,
                            isUnique: trueBit,
                            dataSpaceId: primaryDataSpace,
                            isPrimaryKey: falseBit,
                            isUniqueConstraint: trueBit,
                            hasFilter: falseBit,
                            filterDefinition: nullFilter,
                            falseBit, trueBit, zeroByte);
                    }
                    else
                    {
                        var hasFilter = index!.Filter is not null;
                        yield return BuildIndexRow(
                            name: SqlValue.FromSystemName(index.Name),
                            objectId: tableObjectId,
                            indexId: SqlValue.FromInt32(nextIndexId++),
                            type: SqlValue.FromByte(2),
                            typeDesc: nonClusteredDesc,
                            isUnique: index.IsUnique ? trueBit : falseBit,
                            dataSpaceId: primaryDataSpace,
                            isPrimaryKey: falseBit,
                            isUniqueConstraint: falseBit,
                            hasFilter: hasFilter ? trueBit : falseBit,
                            filterDefinition: index.FilterDefinition is { } def ? SqlValue.FromNVarchar(def) : nullFilter,
                            falseBit, trueBit, zeroByte);
                    }
                }
            }
        }

        SqlValue[] BuildIndexRow(
            SqlValue name, SqlValue objectId, SqlValue indexId, SqlValue type, SqlValue typeDesc,
            SqlValue isUnique, SqlValue dataSpaceId, SqlValue isPrimaryKey, SqlValue isUniqueConstraint,
            SqlValue hasFilter, SqlValue filterDefinition, SqlValue isPadded, SqlValue allowLocks, SqlValue fillFactor) =>
            [
                name,
                objectId,
                indexId,
                type,
                typeDesc,
                isUnique,
                dataSpaceId,
                falseBit, // ignore_dup_key
                isPrimaryKey,
                isUniqueConstraint,
                fillFactor,
                isPadded,
                falseBit, // is_disabled
                falseBit, // is_hypothetical
                falseBit, // is_ignored_in_optimization
                allowLocks, // allow_row_locks
                allowLocks, // allow_page_locks
                hasFilter,
                filterDefinition,
                nullCompressionDelay,
                falseBit, // suppress_dup_key_messages
                falseBit, // auto_created
                falseBit, // optimize_for_sequential_key
                falseBit, // statistics_incremental
            ];
    }

    /// <summary>
    /// Shared (table, index_id) identity stream backing <c>sys.partitions</c>
    /// and <c>sys.stats</c>. Mirrors <see cref="EnumerateSysIndexes"/>'s
    /// index-id assignment exactly: the heap (index_id = 0, IsHeap = true) or
    /// clustered PRIMARY KEY (index_id = 1) leads, then UNIQUE-constraint and
    /// CREATE-INDEX rows follow in object-id order at index_id = 2..N. Name is
    /// the constraint/index name (null only for the heap).
    /// </summary>
    private static IEnumerable<(HeapTable Table, int IndexId, string? Name, bool IsHeap)> EnumerateTableIndexIdentities(Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                KeyConstraint? primaryKey = null;
                foreach (var k in table.KeyConstraints)
                {
                    if (k.Kind == KeyConstraintKind.PrimaryKey)
                    {
                        primaryKey = k;
                        break;
                    }
                }
                var hasPk = primaryKey is not null;
                var nextIndexId = hasPk ? 2 : 1;
                yield return (table, hasPk ? 1 : 0, hasPk ? primaryKey!.Name : null, !hasPk);

                var others = new List<(int ObjectId, KeyConstraint? Key, Storage.Index? Index)>();
                foreach (var k in table.KeyConstraints)
                {
                    if (!ReferenceEquals(k, primaryKey))
                        others.Add((k.ObjectId, k, null));
                }
                foreach (var ix in table.Indexes)
                    others.Add((ix.ObjectId, null, ix));
                others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

                foreach (var (_, key, index) in others)
                    yield return (table, nextIndexId++, key is not null ? key.Name : index!.Name, false);
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.partitions</c>: one per (object_id, index_id) that
    /// <see cref="EnumerateSysIndexes"/> reports, all with partition_number = 1
    /// (single, unpartitioned partition per index/heap). rows carries the
    /// table's live <see cref="Storage.Heap.RowCount"/>, so it reflects
    /// same-batch INSERT/DELETE. partition_id / hobt_id are synthetic-
    /// deterministic (distinct per object_id/index_id; not SQL Server's
    /// allocation-unit ids). Compression is unmodeled: data_compression = 0
    /// (NONE), xml_compression = 0 (OFF).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysPartitions(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var partitionNumber = SqlValue.FromInt32(1);
        var filestreamFg = SqlValue.FromInt16(0);
        var noneCompression = SqlValue.FromByte(0);
        var noneDesc = SqlValue.FromNVarchar("NONE");
        var xmlOff = SqlValue.FromBoolean(false);
        var xmlOffDesc = SqlValue.FromVarchar(VarcharSqlType.Get(3, Collation.Catalog, Coercibility.Implicit), "OFF");
        foreach (var (table, indexId, _, _) in EnumerateTableIndexIdentities(database))
        {
            var objectId = table.ObjectId;
            var partitionId = ((long)(uint)objectId << 16) | (uint)indexId;
            var partitionIdValue = SqlValue.FromInt64(partitionId);
            yield return
            [
                partitionIdValue,
                SqlValue.FromInt32(objectId),
                SqlValue.FromInt32(indexId),
                partitionNumber,
                partitionIdValue,
                SqlValue.FromInt64(table.Heap.RowCount),
                filestreamFg,
                noneCompression,
                noneDesc,
                xmlOff,
                xmlOffDesc,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.stats</c>: one per index <see cref="EnumerateSysIndexes"/>
    /// reports, excluding the heap (index_id = 0 has no statistic). stats_id =
    /// index_id and name = index name, matching real SQL Server's index-backing
    /// statistic. Auto-created column statistics (_WA_Sys_*) aren't modeled, so
    /// auto_created / user_created are always 0 and no column-only stats appear.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysStats(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
        var nullFilter = SqlValue.Null(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
        var zeroInt = SqlValue.FromInt32(0);
        var methodDesc = SqlValue.FromVarchar(VarcharSqlType.Get(80, Collation.Catalog, Coercibility.Implicit), "Sort based statistics");
        var nullRole = SqlValue.Null(SqlType.TinyInt);
        var nullRoleDesc = SqlValue.Null(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit));
        var nullName = SqlValue.Null(SqlType.SystemName);
        foreach (var (table, indexId, name, isHeap) in EnumerateTableIndexIdentities(database))
        {
            if (isHeap)
                continue;
            yield return
            [
                SqlValue.FromInt32(table.ObjectId),
                name is not null ? SqlValue.FromSystemName(name) : nullName,
                SqlValue.FromInt32(indexId),
                falseBit, // auto_created
                falseBit, // user_created
                falseBit, // no_recompute
                falseBit, // has_filter
                nullFilter,
                falseBit, // is_temporary
                falseBit, // is_incremental
                falseBit, // has_persisted_sample
                zeroInt,  // stats_generation_method
                methodDesc,
                falseBit, // auto_drop
                nullRole,
                nullRoleDesc,
                nullName, // replica_name
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.index_columns</c>: one row per (index, column) for
    /// every index reported by <see cref="EnumerateSysIndexes"/>. KEY
    /// columns get key_ordinal = 1..N and index_column_id = 1..N; INCLUDE
    /// columns get key_ordinal = 0 and index_column_id continuing past
    /// the key column count. HEAP rows (index_id = 0) don't appear here —
    /// real SQL Server's catalog omits them and the simulator matches.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysIndexColumns(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var zeroByte = SqlValue.FromByte(0);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                var tableObjectId = SqlValue.FromInt32(table.ObjectId);
                KeyConstraint? primaryKey = null;
                foreach (var k in table.KeyConstraints)
                {
                    if (k.Kind == KeyConstraintKind.PrimaryKey)
                    {
                        primaryKey = k;
                        break;
                    }
                }
                var nextIndexId = primaryKey is null ? 1 : 1;
                if (primaryKey is not null)
                {
                    foreach (var row in EmitKeyConstraintColumns(tableObjectId, SqlValue.FromInt32(nextIndexId), primaryKey, table, falseBit, zeroByte, nullByte))
                        yield return row;
                    nextIndexId++;
                }

                var others = new List<(int ObjectId, KeyConstraint? Key, Storage.Index? Index)>();
                foreach (var k in table.KeyConstraints)
                {
                    if (!ReferenceEquals(k, primaryKey))
                        others.Add((k.ObjectId, k, null));
                }
                foreach (var ix in table.Indexes)
                    others.Add((ix.ObjectId, null, ix));
                others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

                foreach (var (_, key, index) in others)
                {
                    var indexIdValue = SqlValue.FromInt32(nextIndexId++);
                    if (key is not null)
                    {
                        foreach (var row in EmitKeyConstraintColumns(tableObjectId, indexIdValue, key, table, falseBit, zeroByte, nullByte))
                            yield return row;
                    }
                    else
                    {
                        for (var i = 0; i < index!.KeyColumns.Length; i++)
                        {
                            var keyCol = index.KeyColumns[i];
                            yield return [
                                tableObjectId,
                                indexIdValue,
                                SqlValue.FromInt32(i + 1),
                                SqlValue.FromInt32(StorageOrdinalToColumnId(table, keyCol.StorageOrdinal)),
                                SqlValue.FromByte((byte)(i + 1)),
                                zeroByte,
                                keyCol.IsDescending ? trueBit : falseBit,
                                falseBit,
                                nullByte,
                                nullByte,
                            ];
                        }
                        for (var i = 0; i < index.IncludedColumns.Length; i++)
                        {
                            yield return [
                                tableObjectId,
                                indexIdValue,
                                SqlValue.FromInt32(index.KeyColumns.Length + i + 1),
                                SqlValue.FromInt32(StorageOrdinalToColumnId(table, index.IncludedColumns[i])),
                                zeroByte,
                                zeroByte,
                                falseBit,
                                trueBit,
                                nullByte,
                                nullByte,
                            ];
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Materializes one row per key column of a PRIMARY KEY / UNIQUE
    /// <see cref="KeyConstraint"/> for <c>sys.index_columns</c>. Shared
    /// between the PK-at-index_id-1 emission and the non-PK UQ entries
    /// in the ordered emission pass.
    /// </summary>
    private static IEnumerable<SqlValue[]> EmitKeyConstraintColumns(
        SqlValue tableObjectId, SqlValue indexIdValue, KeyConstraint constraint, HeapTable table,
        SqlValue falseBit, SqlValue zeroByte, SqlValue nullByte)
    {
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            yield return [
                tableObjectId,
                indexIdValue,
                SqlValue.FromInt32(i + 1),
                SqlValue.FromInt32(StorageOrdinalToColumnId(table, constraint.StorageOrdinals[i])),
                SqlValue.FromByte((byte)(i + 1)),
                zeroByte,
                falseBit,
                falseBit,
                nullByte,
                nullByte,
            ];
        }
    }

    /// <summary>
    /// Converts a storage ordinal back to the 1-based <c>column_id</c>
    /// reported by <c>sys.columns</c>. The simulator's column_id is the
    /// 1-based full-column ordinal (matching real SQL Server), so we walk
    /// <see cref="HeapTable.StorageOrdinals"/> looking for the full
    /// ordinal that maps to the given storage ordinal.
    /// </summary>
    private static int StorageOrdinalToColumnId(HeapTable table, int storageOrdinal)
    {
        for (var i = 0; i < table.StorageOrdinals.Length; i++)
        {
            if (table.StorageOrdinals[i] == storageOrdinal)
                return i + 1;
        }
        return storageOrdinal + 1;
    }
}
