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
            _ = batch;
            return EnumerateFilegroupRows(database, filegroupType, filegroupTypeDesc);
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
            _ = batch;
            var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
            var nullLogId = SqlValue.Null(SqlType.Int32);
            var falseBit = SqlValue.FromBoolean(false);
            return EnumerateFilegroupRows(database, filegroupType, filegroupTypeDesc)
                .Select(row => new[] { row[0], row[1], row[2], row[3], row[4], row[5], nullGuid, nullLogId, falseBit, falseBit });
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

        // sys.allocation_units: probe-confirmed 8-column shape against SQL
        // Server 2025 (2026-07-15). One IN_ROW_DATA row per sys.partitions row
        // (container_id = that partition's synthetic partition_id — the join
        // key SSMS's space query uses), plus one LOB_DATA row per table that
        // has off-row LOB pages (attached to the base heap/clustered partition,
        // since the simulator's LOB-page chain is per-table, not per-index).
        // ROW_OVERFLOW_DATA (type 3) isn't surfaced — the row encoder pushes
        // oversize columns into the LOB chain, so there is no separate
        // row-overflow allocation to report. total_pages / used_pages /
        // data_pages all read the table's live heap page count
        // (Heap.Pages.Count for IN_ROW, Heap.LobPages.Count for LOB); LOB rows
        // report data_pages = 0, matching real. Because separate nonclustered-
        // index storage isn't modeled, every index partition's IN_ROW unit
        // reports the base heap's page count — an over-count for multi-index
        // tables, kept self-consistent with sys.database_files.size (both
        // derive from SumDataFilePages) so SSMS never computes negative
        // SpaceAvailable. allocation_unit_id is synthetic-deterministic
        // (distinct per partition/type; not SQL Server's real id). See
        // docs/claude/catalog-views.md.
        Sys("allocation_units",
        [
            new("allocation_unit_id", SqlType.BigInt, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("container_id", SqlType.BigInt, null, false),
            new("data_space_id", SqlType.Int32, null, true),
            new("total_pages", SqlType.BigInt, null, false),
            new("used_pages", SqlType.BigInt, null, false),
            new("data_pages", SqlType.BigInt, null, false),
        ], EnumerateSysAllocationUnits);

        // sys.dm_db_partition_stats: probe-confirmed 14-column shape against SQL
        // Server 2025 (2026-07-16). One row per (object_id, index_id) that
        // sys.partitions / sys.allocation_units report — partition_number = 1,
        // partition_id = the same synthetic id those views use (the join key).
        // Page counts derive from the table's live heap page count, kept
        // consistent with sys.allocation_units: in_row_* = Heap.Pages.Count on
        // every partition; lob_* = Heap.LobPages.Count only on the base
        // heap/clustered partition (index_id 0/1), matching allocation_units'
        // per-table LOB attachment; row_overflow_* = 0 (the row encoder pushes
        // oversize columns into the LOB chain, so no separate row-overflow
        // allocation — same as allocation_units omitting type 3). used_page_count
        // / reserved_page_count are the row-level sums (in_row + lob + overflow),
        // so SUM(used_page_count) across a table's partitions equals its
        // allocation_units total and never exceeds SumDataFilePages — the
        // cross-view consistency contract SSMS's Table IndexSpaceUsed math relies
        // on. row_count = live Heap.RowCount. SMO's DataSpaceUsed reads
        // allocation_units; IndexSpaceUsed reads this view. See
        // docs/claude/catalog-views.md.
        Sys("dm_db_partition_stats",
        [
            new("partition_id", SqlType.BigInt, null, true),
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("partition_number", SqlType.Int32, null, false),
            new("in_row_data_page_count", SqlType.BigInt, null, true),
            new("in_row_used_page_count", SqlType.BigInt, null, true),
            new("in_row_reserved_page_count", SqlType.BigInt, null, true),
            new("lob_used_page_count", SqlType.BigInt, null, true),
            new("lob_reserved_page_count", SqlType.BigInt, null, true),
            new("row_overflow_used_page_count", SqlType.BigInt, null, true),
            new("row_overflow_reserved_page_count", SqlType.BigInt, null, true),
            new("used_page_count", SqlType.BigInt, null, true),
            new("reserved_page_count", SqlType.BigInt, null, true),
            new("row_count", SqlType.BigInt, null, true),
        ], EnumerateSysDmDbPartitionStats);

        // sys.dm_db_xtp_table_memory_stats: in-memory-OLTP per-table memory DMV.
        // Memory-optimized tables aren't modeled, so this is an empty view (full
        // probe-confirmed 5-column shape, SQL Server 2025, 2026-07-16). Load-
        // bearing for parse binding, not data: SMO's Table DataSpaceUsed /
        // IndexSpaceUsed queries branch on is_memory_optimized and reference this
        // view in the (never-taken, but compile-time-bound) memory-optimized arm
        // — without the view the whole statement failed Msg 208 and the property
        // errored. The is_memory_optimized = 0 arm (every simulator table) reads
        // allocation_units / dm_db_partition_stats instead.
        Sys("dm_db_xtp_table_memory_stats",
        [
            new("object_id", SqlType.Int32, null, true),
            new("memory_allocated_for_table_kb", SqlType.BigInt, null, true),
            new("memory_used_by_table_kb", SqlType.BigInt, null, true),
            new("memory_allocated_for_indexes_kb", SqlType.BigInt, null, true),
            new("memory_used_by_indexes_kb", SqlType.BigInt, null, true),
        ], static (_, _) => EmptyCatalogRows);

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

        // sys.stats_columns: one row per KEY column of each index-backed
        // statistic sys.stats reports (stats_id = index_id). Mirrors
        // sys.index_columns' key-column rows exactly — stats_column_id =
        // the key ordinal (1..N), column_id = the sys.columns id — but
        // omits INCLUDE columns (a statistic covers only key columns;
        // probe-confirmed against SQL Server 2025 WWI: stats_columns count
        // per index-backed stat equals the index's is_included_column = 0
        // count). Auto-created column statistics (_WA_Sys_*) aren't modeled,
        // so no column-only stats_columns rows appear — same divergence as
        // sys.stats. Probe-confirmed 4-column shape (SQL Server 2025).
        Sys("stats_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("stats_id", SqlType.Int32, null, false),
            new("stats_column_id", SqlType.Int32, null, true),
            new("column_id", SqlType.Int32, null, true),
        ], EnumerateSysStatsColumns);

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

        // sys.json_index_paths / sys.selective_xml_index_namespaces /
        // sys.vector_indexes: index-feature views for capabilities the
        // simulator doesn't model (JSON indexes, selective XML index
        // namespaces, DiskANN vector indexes). Each ships the full
        // probe-confirmed shape (SQL Server 2025, 2026-07-16) with zero
        // rows via the shared EmptyCatalogRows — DacFx's bacpac-export
        // reverse-engineering references them and must resolve to an empty
        // (not Msg 208) result. See docs/claude/catalog-views.md.
        Sys("json_index_paths",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("path", VarcharSqlType.Get(8000, Collation.Baseline, Coercibility.CoercibleDefault), 8000, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("selective_xml_index_namespaces",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("is_default_uri", SqlType.Bit, null, true),
            new("uri", SqlType.NVarchar, 4000, true),
            new("prefix", SqlType.SystemName, 128, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("vector_indexes",
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
            new("vector_index_type", nvarchar60Catalog, 60, true),
            new("distance_metric", nvarchar60Catalog, 60, true),
            new("build_parameters", SqlType.NVarchar, 4000, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.partition_functions / sys.partition_schemes /
        // sys.partition_range_values: table/index partitioning isn't modeled
        // (every table reads as a single unpartitioned partition — see
        // sys.data_spaces / sys.partitions), so all three ship empty with the
        // full probe-confirmed shape (SQL Server 2025). partition_range_values'
        // value column is sql_variant on real SQL Server; substituted here as
        // nvarchar since the view is always empty (the same sql_variant→nvarchar
        // substitution sys.asymmetric_keys uses). See docs/claude/catalog-views.md.
        Sys("partition_functions",
        [
            new("name", SqlType.SystemName, 128, false),
            new("function_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("fanout", SqlType.Int32, null, false),
            new("boundary_value_on_right", SqlType.Bit, null, false),
            new("is_system", SqlType.Bit, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
        ], static (_, _) => EmptyCatalogRows);
        Sys("partition_schemes",
        [
            new("name", SqlType.SystemName, 128, false),
            new("data_space_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_default", SqlType.Bit, null, true),
            new("is_system", SqlType.Bit, null, true),
            new("function_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);
        Sys("partition_range_values",
        [
            new("function_id", SqlType.Int32, null, false),
            new("boundary_id", SqlType.Int32, null, false),
            new("parameter_id", SqlType.Int32, null, false),
            new("value", SqlType.NVarchar, 4000, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.partition_parameters / sys.destination_data_spaces: the
        // remaining partitioning-catalog surface DacFx's SqlPartitionFunction /
        // SqlPartitionScheme populators read; partitioning isn't modeled, so
        // both ship empty with the probe-confirmed shape (SQL Server 2025).
        Sys("partition_parameters",
        [
            new("function_id", SqlType.Int32, null, false),
            new("parameter_id", SqlType.Int32, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("user_type_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);
        Sys("destination_data_spaces",
        [
            new("partition_scheme_id", SqlType.Int32, null, false),
            new("destination_id", SqlType.Int32, null, false),
            new("data_space_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>
    /// Shared row producer for <c>sys.data_spaces</c> + <c>sys.filegroups</c>
    /// (the latter widens each row with four filegroup-only trailing columns).
    /// One row per <see cref="Database.Filegroups"/> entry ordered by
    /// <c>data_space_id</c>: <c>PRIMARY</c> (id 1) reports
    /// <c>is_default = 1</c>, every registered filegroup <c>is_default = 0</c>;
    /// <c>is_system</c> is always 0. Partition schemes ('PS') aren't modeled, so
    /// every row is a 'FG' ROWS_FILEGROUP.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateFilegroupRows(Database database, SqlValue filegroupType, SqlValue filegroupTypeDesc)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var (name, id) in database.Filegroups.OrderBy(kvp => kvp.Value))
        {
            yield return
            [
                SqlValue.FromSystemName(name),
                SqlValue.FromInt32(id),
                filegroupType,
                filegroupTypeDesc,
                id == Database.PrimaryFilegroupId ? trueBit : falseBit,
                falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.indexes</c>: one row per identity yielded by
    /// <see cref="HeapTable.IndexIdentities"/> — the single index-id
    /// allocation authority. The clustered entry (clustered PK / UNIQUE
    /// constraint or <c>CREATE CLUSTERED INDEX</c>) lands at index_id = 1
    /// type_desc = CLUSTERED and suppresses the HEAP row; a table with no
    /// clustered index emits a HEAP row (index_id = 0, name = NULL); every
    /// other index / constraint (incl. a NONCLUSTERED PK) lands at index_id
    /// 2..N type_desc = NONCLUSTERED in object-id (declaration) order.
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
                foreach (var identity in table.IndexIdentities())
                    yield return RowForIdentity(tableObjectId, identity);
            }
            // Indexed views: one row per index the view carries (no HEAP row —
            // an ordinary view contributes nothing, probe-confirmed). The
            // clustered unique index lands at index_id = 1 / CLUSTERED.
            foreach (var view in schema.Views.Values)
            {
                if (view.Indexes.Count == 0)
                    continue;
                var viewObjectId = SqlValue.FromInt32(view.ObjectId);
                foreach (var identity in view.IndexIdentities())
                    yield return RowForIdentity(viewObjectId, identity);
            }
        }

        SqlValue[] RowForIdentity(SqlValue objectId, IndexIdentity identity)
        {
            var typeDesc = identity.Type switch { 0 => heapDesc, 1 => clusteredDesc, _ => nonClusteredDesc };
            SqlValue name, isUnique, isPrimaryKey, isUniqueConstraint, hasFilter, filterDefinition;
            if (identity.Constraint is { } key)
            {
                var isPk = key.Kind == KeyConstraintKind.PrimaryKey;
                name = SqlValue.FromSystemName(key.Name);
                isUnique = trueBit;
                isPrimaryKey = isPk ? trueBit : falseBit;
                isUniqueConstraint = isPk ? falseBit : trueBit;
                hasFilter = falseBit;
                filterDefinition = nullFilter;
            }
            else if (identity.Index is { } index)
            {
                name = SqlValue.FromSystemName(index.Name);
                isUnique = index.IsUnique ? trueBit : falseBit;
                isPrimaryKey = falseBit;
                isUniqueConstraint = falseBit;
                hasFilter = index.Filter is not null ? trueBit : falseBit;
                filterDefinition = index.FilterDefinition is { } def ? SqlValue.FromNVarchar(def) : nullFilter;
            }
            else
            {
                name = nullName;
                isUnique = falseBit;
                isPrimaryKey = falseBit;
                isUniqueConstraint = falseBit;
                hasFilter = falseBit;
                filterDefinition = nullFilter;
            }
            return BuildIndexRow(
                name: name,
                objectId: objectId,
                indexId: SqlValue.FromInt32(identity.IndexId),
                type: SqlValue.FromByte(identity.Type),
                typeDesc: typeDesc,
                isUnique: isUnique,
                dataSpaceId: primaryDataSpace,
                isPrimaryKey: isPrimaryKey,
                isUniqueConstraint: isUniqueConstraint,
                hasFilter: hasFilter,
                filterDefinition: filterDefinition,
                falseBit, trueBit, zeroByte);
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
    /// Shared (table, index_id) identity stream backing <c>sys.partitions</c>,
    /// <c>sys.allocation_units</c>, <c>sys.dm_db_partition_stats</c>, and
    /// <c>sys.stats</c>. A thin per-database flattening of
    /// <see cref="HeapTable.IndexIdentities"/> — the same allocation authority
    /// <see cref="EnumerateSysIndexes"/> reads — so every id these views report
    /// agrees with <c>sys.indexes</c>. Name is the constraint/index name (null
    /// only for the heap).
    /// </summary>
    private static IEnumerable<(HeapTable Table, int IndexId, string? Name, bool IsHeap)> EnumerateTableIndexIdentities(Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var identity in table.IndexIdentities())
                    yield return (table, identity.IndexId, identity.Name, identity.IsHeap);
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
    /// The raw allocation-unit stream shared by <see cref="EnumerateSysAllocationUnits"/>
    /// and <see cref="SumDataFilePages"/>. Yields one IN_ROW_DATA tuple per
    /// (table, index_id) that <see cref="EnumerateSysPartitions"/> reports —
    /// container_id = the same synthetic partition_id — plus one LOB_DATA tuple
    /// per table with off-row LOB pages, attached to the base heap/clustered
    /// partition (the first identity yielded per table). Page counts read the
    /// live <see cref="Storage.Heap"/> state (Pages.Count for IN_ROW,
    /// LobPages.Count for LOB), so they track same-batch INSERT/DELETE. LOB
    /// tuples report data_pages = 0, matching real SQL Server.
    /// </summary>
    private static IEnumerable<(long ContainerId, byte Type, long TotalPages, long UsedPages, long DataPages)> EnumerateAllocationUnitData(Database database)
    {
        HeapTable? lastTable = null;
        foreach (var (table, indexId, _, _) in EnumerateTableIndexIdentities(database))
        {
            var partitionId = ((long)(uint)table.ObjectId << 16) | (uint)indexId;
            long dataPages = table.Heap.Pages.Count;
            yield return (partitionId, 1, dataPages, dataPages, dataPages);
            if (!ReferenceEquals(table, lastTable))
            {
                lastTable = table;
                long lobPages = table.Heap.LobPages.Count;
                if (lobPages > 0)
                    yield return (partitionId, 2, lobPages, lobPages, 0);
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.allocation_units</c>: one per tuple from
    /// <see cref="EnumerateAllocationUnitData"/>. allocation_unit_id is
    /// synthetic-deterministic (partition_id shifted, low bits carrying the
    /// type — distinct per partition/type, not SQL Server's real id);
    /// data_space_id is always 1 (the single modeled PRIMARY filegroup).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysAllocationUnits(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var inRowDesc = SqlValue.FromNVarchar("IN_ROW_DATA");
        var lobDesc = SqlValue.FromNVarchar("LOB_DATA");
        var primaryDataSpace = SqlValue.FromInt32(1);
        foreach (var (containerId, type, totalPages, usedPages, dataPages) in EnumerateAllocationUnitData(database))
        {
            yield return
            [
                SqlValue.FromInt64((containerId << 8) | type),
                SqlValue.FromByte(type),
                type == 2 ? lobDesc : inRowDesc,
                SqlValue.FromInt64(containerId),
                primaryDataSpace,
                SqlValue.FromInt64(totalPages),
                SqlValue.FromInt64(usedPages),
                SqlValue.FromInt64(dataPages),
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.dm_db_partition_stats</c>: one per (object_id, index_id)
    /// that <see cref="EnumerateSysPartitions"/> reports, partition_number = 1,
    /// partition_id = the same synthetic id (the <c>sys.partitions</c> /
    /// <c>sys.allocation_units</c> join key). Page counts derive from the live
    /// <see cref="Storage.Heap"/> the same way <see cref="EnumerateAllocationUnitData"/>
    /// does — in_row_* = Pages.Count on every partition, lob_* = LobPages.Count
    /// only on the base heap/clustered partition (the first identity per table),
    /// row_overflow_* = 0. used_page_count / reserved_page_count are the
    /// in_row + lob + overflow row-level sums, so a table's SUM(used_page_count)
    /// equals its allocation-unit total (the cross-view consistency contract).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDmDbPartitionStats(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var partitionNumber = SqlValue.FromInt32(1);
        var zeroPages = SqlValue.FromInt64(0);
        HeapTable? lastTable = null;
        foreach (var (table, indexId, _, _) in EnumerateTableIndexIdentities(database))
        {
            var isBase = !ReferenceEquals(table, lastTable);
            lastTable = table;
            var partitionId = ((long)(uint)table.ObjectId << 16) | (uint)indexId;
            long inRow = table.Heap.Pages.Count;
            long lob = isBase ? table.Heap.LobPages.Count : 0;
            var inRowValue = SqlValue.FromInt64(inRow);
            var lobValue = SqlValue.FromInt64(lob);
            var usedReserved = SqlValue.FromInt64(inRow + lob);
            yield return
            [
                SqlValue.FromInt64(partitionId),
                SqlValue.FromInt32(table.ObjectId),
                SqlValue.FromInt32(indexId),
                partitionNumber,
                inRowValue, // in_row_data_page_count
                inRowValue, // in_row_used_page_count
                inRowValue, // in_row_reserved_page_count
                lobValue,   // lob_used_page_count
                lobValue,   // lob_reserved_page_count
                zeroPages,  // row_overflow_used_page_count
                zeroPages,  // row_overflow_reserved_page_count
                usedReserved, // used_page_count
                usedReserved, // reserved_page_count
                SqlValue.FromInt64(table.Heap.RowCount),
            ];
        }
    }

    /// <summary>
    /// Live page total across every modeled allocation unit of
    /// <paramref name="database"/> — the sum of <c>total_pages</c> over
    /// <see cref="EnumerateAllocationUnitData"/>. Backs the data-file
    /// <c>size</c> reported by <c>sys.database_files</c> / <c>sys.master_files</c>
    /// (via <see cref="ComputeDataFileSizePages"/>) and FILEPROPERTY's
    /// data-file <c>SpaceUsed</c>, keeping SSMS's
    /// SpaceAvailable = size − SUM(total_pages) non-negative.
    /// </summary>
    internal static long SumDataFilePages(Database database)
    {
        long total = 0;
        foreach (var (_, _, totalPages, _, _) in EnumerateAllocationUnitData(database))
            total += totalPages;
        return total;
    }

    /// <summary>Synthetic per-database log-file size, in 8 KB pages.</summary>
    internal const int LogFileSizePages = 128;

    /// <summary>Synthetic log-file <c>SpaceUsed</c> (pages) reported by FILEPROPERTY — a small fraction of <see cref="LogFileSizePages"/>.</summary>
    internal const int LogFileUsedPages = 24;

    /// <summary>
    /// Synthetic data-file <c>size</c> (pages) for <paramref name="database"/>:
    /// the live allocated-page total (<see cref="SumDataFilePages"/>) plus
    /// generous headroom, floored at 640 pages so an empty database still
    /// reports a plausible file. Guarantees size &gt; SUM(total_pages).
    /// </summary>
    internal static int ComputeDataFileSizePages(Database database)
    {
        var used = SumDataFilePages(database);
        var size = used + Math.Max(512L, used / 2);
        return (int)Math.Min(int.MaxValue, Math.Max(640L, size));
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
        // Indexed-view statistics: one index-backed stat per view index
        // (stats_id = index_id, name = index name), matching real SQL Server.
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var view in schema.Views.Values)
            {
                if (view.Indexes.Count == 0)
                    continue;
                var viewObjectId = SqlValue.FromInt32(view.ObjectId);
                foreach (var identity in view.IndexIdentities())
                {
                    yield return
                    [
                        viewObjectId,
                        SqlValue.FromSystemName(identity.Name!),
                        SqlValue.FromInt32(identity.IndexId),
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
        }
        // XML-index statistics live on the owning primary's internal node
        // table (sys.objects type IT), one per index, stats_id sequential
        // within that node table (probe-confirmed). DacFx's XML-index export
        // joins sys.stats to the node table by (object_id, name = index name).
        foreach (var (internalTableObjectId, statsId, indexName) in EnumerateXmlIndexStats(database))
        {
            yield return
            [
                SqlValue.FromInt32(internalTableObjectId),
                SqlValue.FromSystemName(indexName),
                SqlValue.FromInt32(statsId),
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
    /// Walks every table's XML indexes, resolving each index to the
    /// <c>object_id</c> of the internal node table it belongs to (a primary
    /// owns one; a secondary shares its primary's) and assigning a
    /// per-node-table sequential <c>stats_id</c>. Used by
    /// <see cref="EnumerateSysStats"/> and the internal-table + stats surface
    /// DacFx's XML-index reverse-engineering joins through.
    /// </summary>
    private static IEnumerable<(int InternalTableObjectId, int StatsId, string IndexName)> EnumerateXmlIndexStats(Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.XmlIndexes.Count == 0)
                    continue;
                var primaryNodeTable = new Dictionary<string, int>(database.Collation);
                foreach (var ix in table.XmlIndexes)
                {
                    if (ix.IsPrimary)
                        primaryNodeTable[ix.Name] = ix.InternalTableObjectId;
                }
                var nextStatsId = new Dictionary<int, int>();
                foreach (var ix in table.XmlIndexes)
                {
                    var nodeTableId = ix.IsPrimary
                        ? ix.InternalTableObjectId
                        : ix.UsingPrimaryIndexName is { } u && primaryNodeTable.TryGetValue(u, out var v) ? v : 0;
                    if (nodeTableId == 0)
                        continue;
                    var statsId = nextStatsId.TryGetValue(nodeTableId, out var cur) ? cur + 1 : 1;
                    nextStatsId[nodeTableId] = statsId;
                    yield return (nodeTableId, statsId, ix.Name);
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.stats_columns</c>: one row per KEY column of each
    /// index-backed statistic (stats_id = index_id), mirroring
    /// <see cref="EnumerateSysIndexColumns"/>'s index-id assignment and
    /// key-column ordering but omitting INCLUDE columns (a statistic covers
    /// only the index key). stats_column_id = the 1-based key ordinal;
    /// column_id = the <c>sys.columns</c> id via
    /// <see cref="StorageOrdinalToColumnId"/>. HEAP rows (index_id = 0) carry
    /// no statistic and are skipped, matching <see cref="EnumerateSysStats"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysStatsColumns(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                var tableObjectId = SqlValue.FromInt32(table.ObjectId);
                foreach (var identity in table.IndexIdentities())
                {
                    if (identity.IsHeap)
                        continue;
                    var columnIds = identity.Constraint is { } key
                        ? ResolveConstraintColumnIds(key, table)
                        : IndexKeyColumnIds(identity.Index!);
                    foreach (var row in EmitStatsColumns(tableObjectId, SqlValue.FromInt32(identity.IndexId), columnIds))
                        yield return row;
                }
            }
            // Indexed views: one stats_columns row per view-index key column
            // (column_id = view OUTPUT ordinal + 1). No INCLUDE columns.
            foreach (var view in schema.Views.Values)
            {
                if (view.Indexes.Count == 0)
                    continue;
                var viewObjectId = SqlValue.FromInt32(view.ObjectId);
                foreach (var identity in view.IndexIdentities())
                {
                    foreach (var row in EmitStatsColumns(viewObjectId, SqlValue.FromInt32(identity.IndexId), IndexKeyColumnIds(identity.Index!)))
                        yield return row;
                }
            }
        }

        static int[] ResolveConstraintColumnIds(KeyConstraint key, HeapTable table) =>
            [.. key.StorageOrdinals.Select(o => StorageOrdinalToColumnId(table, o))];

        static int[] IndexKeyColumnIds(Storage.Index index) =>
            [.. index.KeyColumns.Select(static c => c.ColumnOrdinal + 1)];
    }

    /// <summary>
    /// Materializes one <c>sys.stats_columns</c> row per key column of an
    /// index-backed statistic: stats_column_id = the 1-based key ordinal,
    /// column_id = the resolved <c>sys.columns</c> id.
    /// </summary>
    private static IEnumerable<SqlValue[]> EmitStatsColumns(SqlValue tableObjectId, SqlValue statsIdValue, int[] columnIds)
    {
        for (var i = 0; i < columnIds.Length; i++)
        {
            yield return
            [
                tableObjectId,
                statsIdValue,
                SqlValue.FromInt32(i + 1),
                SqlValue.FromInt32(columnIds[i]),
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
                foreach (var identity in table.IndexIdentities())
                {
                    if (identity.IsHeap)
                        continue;
                    var indexIdValue = SqlValue.FromInt32(identity.IndexId);
                    if (identity.Constraint is { } key)
                    {
                        foreach (var row in EmitKeyConstraintColumns(tableObjectId, indexIdValue, key, table, falseBit, zeroByte, nullByte))
                            yield return row;
                    }
                    else
                    {
                        foreach (var row in IndexColumnRows(tableObjectId, indexIdValue, identity.Index!))
                            yield return row;
                    }
                }
                // XML indexes: one index_column row per index (the indexed xml
                // column), key_ordinal 0 / index_column_id 1 (probe-confirmed
                // against SQL Server 2025). DacFx's XML-index export INNER JOINs
                // sys.index_columns on the xml index's index_id.
                foreach (var xmlIndex in table.XmlIndexes)
                {
                    yield return [
                        tableObjectId,
                        SqlValue.FromInt32(xmlIndex.ObjectId),
                        SqlValue.FromInt32(1),
                        SqlValue.FromInt32(xmlIndex.ColumnOrdinal + 1),
                        zeroByte,
                        zeroByte,
                        falseBit,
                        falseBit,
                        nullByte,
                        nullByte,
                    ];
                }
            }
            // Indexed views: KEY / INCLUDE columns keyed on the view OUTPUT
            // ordinal (column_id = ordinal + 1, matching sys.columns of views).
            foreach (var view in schema.Views.Values)
            {
                if (view.Indexes.Count == 0)
                    continue;
                var viewObjectId = SqlValue.FromInt32(view.ObjectId);
                foreach (var identity in view.IndexIdentities())
                {
                    foreach (var row in IndexColumnRows(viewObjectId, SqlValue.FromInt32(identity.IndexId), identity.Index!))
                        yield return row;
                }
            }
        }

        IEnumerable<SqlValue[]> IndexColumnRows(SqlValue objectId, SqlValue indexIdValue, Storage.Index index)
        {
            for (var i = 0; i < index.KeyColumns.Length; i++)
            {
                var keyCol = index.KeyColumns[i];
                yield return [
                    objectId,
                    indexIdValue,
                    SqlValue.FromInt32(i + 1),
                    SqlValue.FromInt32(keyCol.ColumnOrdinal + 1),
                    SqlValue.FromByte((byte)(i + 1)),
                    zeroByte,
                    keyCol.IsDescending ? trueBit : falseBit,
                    falseBit,
                    nullByte,
                    nullByte,
                ];
            }
            for (var i = 0; i < index.IncludedColumnOrdinals.Length; i++)
            {
                yield return [
                    objectId,
                    indexIdValue,
                    SqlValue.FromInt32(index.KeyColumns.Length + i + 1),
                    SqlValue.FromInt32(index.IncludedColumnOrdinals[i] + 1),
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

    /// <summary>
    /// Materializes one row per key column of a PRIMARY KEY / UNIQUE
    /// <see cref="KeyConstraint"/> for <c>sys.index_columns</c>, at the
    /// index_id the shared allocation authority assigned the constraint.
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
