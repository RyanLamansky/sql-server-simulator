using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The server-health views sp_Blitz and its siblings read beside the SQLOS
// ones — mirroring and availability groups, the buffer pool extension, the
// loaded modules, the memory caches, Extended Events' running sessions and the
// default trace — each in SQL Server 2025's column shape and under its object
// id (probed 2026-10-16). What they list follows the simulator's state where
// it keeps one; see docs/claude/catalog-views.md#server-health-views.
internal static partial class BuiltInResources
{
    private static void RegisterHealthCheckViews(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        // Per database, the one option real lists, at its default.
        Sys("database_automatic_tuning_options",
        [
            new("name", SqlType.NVarchar, 128, true),
            new("desired_state", SqlType.SmallInt, null, true),
            new("desired_state_desc", nvarchar60Catalog, 60, true),
            new("actual_state", SqlType.SmallInt, null, true),
            new("actual_state_desc", nvarchar60Catalog, 60, true),
            new("reason", SqlType.SmallInt, null, true),
            new("reason_desc", nvarchar60Catalog, 60, true),
        ], static (_, _) => AutomaticTuningRows);

        // No endpoint is defined.
        Sys("database_mirroring_endpoints",
        [
            new("name", SqlType.SystemName, 128, false),
            new("endpoint_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("protocol", SqlType.TinyInt, null, false),
            new("protocol_desc", nvarchar60Catalog, 60, true),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("is_admin_endpoint", SqlType.Bit, null, false),
            new("role", SqlType.TinyInt, null, true),
            new("role_desc", nvarchar60Catalog, 60, true),
            new("is_encryption_enabled", SqlType.Bit, null, false),
            new("connection_auth", SqlType.TinyInt, null, false),
            new("connection_auth_desc", nvarchar60Catalog, 60, true),
            new("certificate_id", SqlType.Int32, null, false),
            new("encryption_algorithm", SqlType.TinyInt, null, false),
            new("encryption_algorithm_desc", nvarchar60Catalog, 60, true),
        ], static (_, _) => EmptyCatalogRows);

        // A columnstore index compresses nothing here, as sys.column_store_row_groups lists nothing.
        Sys("dm_db_column_store_row_group_physical_stats",
        [
            new("object_id", SqlType.Int32, null, true),
            new("index_id", SqlType.Int32, null, true),
            new("partition_number", SqlType.Int32, null, true),
            new("row_group_id", SqlType.Int32, null, true),
            new("delta_store_hobt_id", SqlType.BigInt, null, true),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, false),
            new("total_rows", SqlType.BigInt, null, true),
            new("deleted_rows", SqlType.BigInt, null, true),
            new("size_in_bytes", SqlType.BigInt, null, true),
            new("trim_reason", SqlType.TinyInt, null, true),
            new("trim_reason_desc", nvarchar60Catalog, 60, true),
            new("transition_to_compressed_state", SqlType.TinyInt, null, true),
            new("transition_to_compressed_state_desc", nvarchar60Catalog, 60, true),
            new("has_vertipaq_optimization", SqlType.Bit, null, true),
            new("generation", SqlType.BigInt, null, true),
            new("created_time", SqlType.DateTime, null, true),
            new("closed_time", SqlType.DateTime, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // No database is mirrored.
        Sys("dm_db_mirroring_auto_page_repair",
        [
            new("database_id", SqlType.Int32, null, false),
            new("file_id", SqlType.Int32, null, false),
            new("page_id", SqlType.BigInt, null, false),
            new("error_type", SqlType.SmallInt, null, false),
            new("page_status", SqlType.TinyInt, null, false),
            new("modification_time", SqlType.DateTime, null, false),
        ], static (_, _) => EmptyCatalogRows);

        // No feature ties a database to an edition, as on SQL Server 2016 SP1 and later.
        Sys("dm_db_persisted_sku_features",
        [
            new("feature_name", SqlType.NVarchar, 4000, true),
            new("feature_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);

        // No availability group exists.
        Sys("dm_hadr_auto_page_repair",
        [
            new("database_id", SqlType.Int32, null, false),
            new("file_id", SqlType.Int32, null, false),
            new("page_id", SqlType.BigInt, null, false),
            new("error_type", SqlType.SmallInt, null, false),
            new("page_status", SqlType.TinyInt, null, false),
            new("modification_time", SqlType.DateTime, null, false),
        ], static (_, _) => EmptyCatalogRows);

        // No availability group exists.
        Sys("dm_hadr_availability_group_states",
        [
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("primary_replica", SqlType.NVarchar, 128, true),
            new("primary_recovery_health", SqlType.TinyInt, null, true),
            new("primary_recovery_health_desc", SqlType.NVarchar, 60, true),
            new("secondary_recovery_health", SqlType.TinyInt, null, true),
            new("secondary_recovery_health_desc", SqlType.NVarchar, 60, true),
            new("synchronization_health", SqlType.TinyInt, null, true),
            new("synchronization_health_desc", SqlType.NVarchar, 60, true),
        ], static (_, _) => EmptyCatalogRows);

        // No availability group exists.
        Sys("dm_hadr_availability_replica_cluster_states",
        [
            new("replica_id", SqlType.UniqueIdentifier, null, false),
            new("replica_server_name", SqlType.NVarchar, 256, false),
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("join_state", SqlType.TinyInt, null, false),
            new("join_state_desc", SqlType.NVarchar, 60, true),
        ], static (_, _) => EmptyCatalogRows);

        // No availability group exists.
        Sys("dm_hadr_availability_replica_states",
        [
            new("replica_id", SqlType.UniqueIdentifier, null, false),
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("is_local", SqlType.Bit, null, false),
            new("role", SqlType.TinyInt, null, true),
            new("role_desc", SqlType.NVarchar, 60, true),
            new("operational_state", SqlType.TinyInt, null, true),
            new("operational_state_desc", SqlType.NVarchar, 60, true),
            new("connected_state", SqlType.TinyInt, null, true),
            new("connected_state_desc", SqlType.NVarchar, 60, true),
            new("recovery_health", SqlType.TinyInt, null, true),
            new("recovery_health_desc", SqlType.NVarchar, 60, true),
            new("synchronization_health", SqlType.TinyInt, null, true),
            new("synchronization_health_desc", SqlType.NVarchar, 60, true),
            new("last_connect_error_number", SqlType.Int32, null, true),
            new("last_connect_error_description", SqlType.NVarchar, 1024, true),
            new("last_connect_error_timestamp", SqlType.DateTime, null, true),
            new("write_lease_remaining_ticks", SqlType.BigInt, null, true),
            new("current_configuration_commit_start_time_utc", SqlType.DateTime, null, true),
            new("is_internal", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // The buffer pool extension is off.
        Sys("dm_os_buffer_pool_extension_configuration",
        [
            new("path", SqlType.NVarchar, 256, true),
            new("file_id", SqlType.Int32, null, true),
            new("state", SqlType.Int32, null, true),
            new("state_description", SqlType.NVarchar, 60, false),
            new("current_size_in_kb", SqlType.BigInt, null, true),
        ], static (_, _) => BufferPoolExtensionRows);

        // The instance is not clustered.
        Sys("dm_os_cluster_nodes",
        [
            new("NodeName", SqlType.NVarchar, 128, true),
            new("status", SqlType.Int32, null, true),
            new("status_description", SqlType.Varchar, 7, false),
            new("is_current_owner", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // A Linux server runs in no job object.
        Sys("dm_os_job_object",
        [
            new("cpu_rate", SqlType.Int32, null, true),
            new("cpu_affinity_mask", SqlType.BigInt, null, true),
            new("cpu_affinity_group", SqlType.Int32, null, true),
            new("memory_limit_mb", SqlType.BigInt, null, true),
            new("process_memory_limit_mb", SqlType.BigInt, null, true),
            new("workingset_limit_mb", SqlType.BigInt, null, true),
            new("non_sos_mem_gap_mb", SqlType.BigInt, null, true),
            new("low_mem_signal_threshold_mb", SqlType.BigInt, null, true),
            new("total_user_time", SqlType.BigInt, null, true),
            new("total_kernel_time", SqlType.BigInt, null, true),
            new("write_operation_count", SqlType.BigInt, null, true),
            new("read_operation_count", SqlType.BigInt, null, true),
            new("peak_process_memory_used_mb", SqlType.BigInt, null, true),
            new("peak_job_memory_used_mb", SqlType.BigInt, null, true),
            new("process_physical_affinity", SqlType.NVarchar, 3072, false),
        ], static (_, _) => EmptyCatalogRows);

        // The engine module alone; real lists every library its process maps.
        Sys("dm_os_loaded_modules",
        [
            new("base_address", SqlType.Varbinary, 8, false),
            new("file_version", SqlType.Varchar, 256, true),
            new("product_version", SqlType.Varchar, 256, true),
            new("debug", SqlType.Bit, null, true),
            new("patched", SqlType.Bit, null, true),
            new("prerelease", SqlType.Bit, null, true),
            new("private_build", SqlType.Bit, null, true),
            new("special_build", SqlType.Bit, null, true),
            new("language", SqlType.Int32, null, true),
            new("company", SqlType.NVarchar, 256, true),
            new("description", SqlType.NVarchar, 256, true),
            new("name", SqlType.NVarchar, 512, true),
            new("target", SqlType.NVarchar, 256, true),
        ], static (_, _) => LoadedModuleRows);

        // One cache store per cache clerk, its entries the plans the plan cache holds.
        HeapColumn[] cacheCounters =
        [
            new("cache_address", SqlType.Varbinary, 8, false),
            new("name", SqlType.NVarchar, 256, false),
            new("type", SqlType.NVarchar, 60, false),
            new("pages_kb", SqlType.BigInt, null, false),
            new("pages_in_use_kb", SqlType.BigInt, null, true),
            new("entries_count", SqlType.BigInt, null, false),
            new("entries_in_use_count", SqlType.BigInt, null, false),
            new("extended_properties", SqlType.NVarchar, 256, true),
        ];
        Sys("dm_os_memory_cache_counters", cacheCounters, (batch, _) => MemoryCacheCounterRows(batch, cacheCounters));

        // The hash table of each cache store.
        HeapColumn[] cacheHashTables =
        [
            new("cache_address", SqlType.Varbinary, 8, false),
            new("name", SqlType.NVarchar, 256, false),
            new("type", SqlType.NVarchar, 60, false),
            new("table_level", SqlType.Int32, null, false),
            new("buckets_count", SqlType.Int32, null, false),
            new("buckets_in_use_count", SqlType.Int32, null, false),
            new("buckets_min_length", SqlType.Int32, null, false),
            new("buckets_max_length", SqlType.Int32, null, false),
            new("buckets_avg_length", SqlType.Int32, null, false),
            new("buckets_max_length_ever", SqlType.Int32, null, false),
            new("hits_count", SqlType.BigInt, null, false),
            new("misses_count", SqlType.BigInt, null, false),
            new("buckets_avg_scan_hit_length", SqlType.Int32, null, false),
            new("buckets_avg_scan_miss_length", SqlType.Int32, null, false),
        ];
        Sys("dm_os_memory_cache_hash_tables", cacheHashTables, (batch, _) => MemoryCacheHashTableRows(batch, cacheHashTables));

        // The simulator samples no memory health.
        Sys("dm_os_memory_health_history",
        [
            new("snapshot_time", SqlType.GetDateTime2(7), null, false),
            new("severity_level", SqlType.TinyInt, null, false),
            new("severity_level_desc", SqlType.NVarchar, 60, false),
            new("allocation_potential_memory_mb", SqlType.Int32, null, false),
            new("reclaimable_cache_memory_mb", SqlType.Int32, null, false),
            new("top_memory_clerks", SqlType.NVarchar, 4000, true),
            new("out_of_memory_event_count", SqlType.Int32, null, false),
            new("memgrant_timeout_count", SqlType.Int32, null, false),
            new("memgrant_waiter_count", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);

        // The empty row a Linux server reports.
        Sys("dm_os_windows_info",
        [
            new("windows_release", SqlType.NVarchar, 256, false),
            new("windows_service_pack_level", SqlType.NVarchar, 256, false),
            new("windows_sku", SqlType.Int32, null, true),
            new("os_language_version", SqlType.Int32, null, false),
        ], static (_, _) => WindowsInfoRows);

        // No memory-optimized transaction statistics are kept.
        HeapColumn[] xtpStats =
        [
            new("total_count", SqlType.BigInt, null, false),
            new("read_only_count", SqlType.BigInt, null, false),
            new("total_aborts", SqlType.BigInt, null, false),
            new("system_aborts", SqlType.BigInt, null, false),
            new("validation_failures", SqlType.BigInt, null, false),
            new("dependencies_taken", SqlType.BigInt, null, false),
            new("dependencies_failed", SqlType.BigInt, null, false),
            new("savepoint_create", SqlType.BigInt, null, false),
            new("savepoint_rollbacks", SqlType.BigInt, null, false),
            new("savepoint_refreshes", SqlType.BigInt, null, false),
            new("log_bytes_written", SqlType.BigInt, null, false),
            new("log_IO_count", SqlType.BigInt, null, false),
            new("phantom_scans_started", SqlType.BigInt, null, false),
            new("phantom_scans_retries", SqlType.BigInt, null, false),
            new("phantom_rows_touched", SqlType.BigInt, null, false),
            new("phantom_rows_expiring", SqlType.BigInt, null, false),
            new("phantom_rows_expired", SqlType.BigInt, null, false),
            new("phantom_rows_expired_removed", SqlType.BigInt, null, false),
            new("scans_started", SqlType.BigInt, null, false),
            new("scans_retried", SqlType.BigInt, null, false),
            new("rows_returned", SqlType.BigInt, null, false),
            new("rows_touched", SqlType.BigInt, null, false),
            new("rows_expiring", SqlType.BigInt, null, false),
            new("rows_expired", SqlType.BigInt, null, false),
            new("rows_expired_removed", SqlType.BigInt, null, false),
            new("row_insert_attempts", SqlType.BigInt, null, false),
            new("row_update_attempts", SqlType.BigInt, null, false),
            new("row_delete_attempts", SqlType.BigInt, null, false),
            new("write_conflicts", SqlType.BigInt, null, false),
            new("unique_constraint_violations", SqlType.BigInt, null, false),
            new("drop_table_memory_attempts", SqlType.BigInt, null, false),
            new("drop_table_memory_failures", SqlType.BigInt, null, false),
        ];
        Sys("dm_xtp_transaction_stats", xtpStats, (_, _) => [StandInRow(xtpStats, [])]);

        // An unconfigured Resource Governor, to a login that may view any definition.
        Sys("resource_governor_configuration",
        [
            new("classifier_function_id", SqlType.Int32, null, false),
            new("is_enabled", SqlType.Bit, null, false),
            new("max_outstanding_io_per_volume", SqlType.Int32, null, false),
        ], static (batch, _) => ResourceGovernorRows(batch, ResourceGovernorConfigurationRows));

        // The default trace, while the default trace enabled option is on.
        HeapColumn[] traces =
        [
            new("id", SqlType.Int32, null, false),
            new("status", SqlType.Int32, null, false),
            new("path", SqlType.NVarchar, 260, true),
            new("max_size", SqlType.BigInt, null, true),
            new("stop_time", SqlType.DateTime, null, true),
            new("max_files", SqlType.Int32, null, true),
            new("is_rowset", SqlType.Bit, null, true),
            new("is_rollover", SqlType.Bit, null, true),
            new("is_shutdown", SqlType.Bit, null, true),
            new("is_default", SqlType.Bit, null, true),
            new("buffer_count", SqlType.Int32, null, true),
            new("buffer_size", SqlType.Int32, null, true),
            new("file_position", SqlType.BigInt, null, true),
            new("reader_spid", SqlType.Int32, null, true),
            new("start_time", SqlType.DateTime, null, true),
            new("last_event_time", SqlType.DateTime, null, true),
            new("event_count", SqlType.BigInt, null, true),
            new("dropped_event_count", SqlType.Int32, null, true),
        ];
        Sys("traces", traces, (batch, _) => DefaultTraceRows(batch.Connection.Simulation, traces).Concat(UserTraceRows(batch.Connection.Simulation)));
    }

    /// <summary>
    /// The one automatic tuning option real lists, <c>FORCE_LAST_GOOD_PLAN</c>,
    /// at its default (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static readonly SqlValue[][] AutomaticTuningRows =
    [[
        SqlValue.FromNVarchar("FORCE_LAST_GOOD_PLAN"), SqlValue.FromInt16(2), SqlValue.FromNVarchar("DEFAULT"),
        SqlValue.FromInt16(0), SqlValue.FromNVarchar("OFF"), SqlValue.FromInt16(2), SqlValue.FromNVarchar("AUTO_CONFIGURED"),
    ]];

    /// <summary>The disabled buffer pool extension's row (probed 2026-10-16 against SQL Server 2025).</summary>
    private static readonly SqlValue[][] BufferPoolExtensionRows =
    [[
        SqlValue.Null(SqlType.NVarchar), SqlValue.FromInt32(-1), SqlValue.FromInt32(0),
        SqlValue.FromNVarchar("BUFFER POOL EXTENSION DISABLED"), SqlValue.Null(SqlType.BigInt),
    ]];

    /// <summary>
    /// The engine's own module, with the versions real's carries; real also
    /// lists every library its process maps (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private static readonly SqlValue[][] LoadedModuleRows =
    [[
        SqlValue.FromVarbinary(StandInAddress(0x20, 0, 0)), SqlValue.FromVarchar("2025.170:4065.4"), SqlValue.FromVarchar("17.0:4065.4"),
        SqlValue.FromBoolean(false), SqlValue.FromBoolean(false), SqlValue.FromBoolean(false), SqlValue.FromBoolean(false), SqlValue.FromBoolean(false),
        SqlValue.FromInt32(67699888), SqlValue.FromNVarchar("Microsoft Corporation"), SqlValue.FromNVarchar("SQL Server Windows NT - 64 Bit"),
        SqlValue.FromNVarchar("sqlservr.exe"), SqlValue.FromNVarchar("AMD64"),
    ]];

    /// <summary>The row a Linux server reports, its Windows fields empty (probed 2026-10-16 against SQL Server 2025).</summary>
    private static readonly SqlValue[][] WindowsInfoRows =
    [[
        SqlValue.FromNVarchar(""), SqlValue.FromNVarchar(""), SqlValue.Null(SqlType.Int32), SqlValue.FromInt32(0),
    ]];

    /// <summary>An unconfigured Resource Governor: no classifier, disabled (probed 2026-10-16 against SQL Server 2025).</summary>
    private static readonly SqlValue[][] ResourceGovernorConfigurationRows =
    [[
        SqlValue.FromInt32(0), SqlValue.FromBoolean(false), SqlValue.FromInt32(0),
    ]];

    /// <summary>
    /// Each cache store's hash-table size, by clerk type, as real sizes it
    /// (probed 2026-10-16 against SQL Server 2025); a user store real keeps
    /// no hash table for is absent.
    /// </summary>
    private static readonly (string Type, int Buckets)[] CacheStoreBuckets =
    [
        ("CACHESTORE_ADHOC_CE_FEEDBACK_MV", 7645), ("CACHESTORE_BROKERDSH", 256), ("CACHESTORE_BROKERKEK", 256),
        ("CACHESTORE_BROKERREADONLY", 997), ("CACHESTORE_BROKERRSB", 256), ("CACHESTORE_BROKERTBLACS", 997),
        ("CACHESTORE_BROKERTO", 20011), ("CACHESTORE_BROKERUSERCERTLOOKUP", 256), ("CACHESTORE_COLUMNSTOREOBJECTPOOL", 40000),
        ("CACHESTORE_CONVPRI", 113), ("CACHESTORE_EVENTS", 458), ("CACHESTORE_FULLTEXTSTOPLIST", 127), ("CACHESTORE_NOTIF", 16384),
        ("CACHESTORE_OBJCP", 40009), ("CACHESTORE_PHDR", 4001), ("CACHESTORE_QDSCONTEXTSETTINGS", 100), ("CACHESTORE_QDSRUNTIMESTATS", 512),
        ("CACHESTORE_SEARCHPROPERTYLIST", 127), ("CACHESTORE_SEHOBTCOLUMNATTRIBUTE", 256), ("CACHESTORE_SQLCP", 40009),
        ("CACHESTORE_STACKFRAMES", 65536), ("CACHESTORE_SYSTEMROWSET", 356), ("CACHESTORE_TEMPTABLES", 4001),
        ("CACHESTORE_VIEWDEFINITIONS", 128), ("CACHESTORE_XML_SELECTIVE_DG", 401), ("CACHESTORE_XMLDBATTRIBUTE", 401),
        ("CACHESTORE_XMLDBELEMENT", 401), ("CACHESTORE_XMLDBTYPE", 401), ("CACHESTORE_XPROC", 127), ("CACHESTORE_XSTORE_AADTOKEN", 16),
        ("USERSTORE_TOKENPERM", 128),
    ];

    /// <summary>
    /// The cache and user stores among real's memory clerk types, each with
    /// its clerk's index (which numbers its stand-in address) and name.
    /// </summary>
    private static IEnumerable<(int Index, string Type, string Name)> CacheStores()
    {
        for (var i = 0; i < MemoryClerkTypes.Length; i++)
        {
            var (type, name, _) = MemoryClerkTypes[i];
            if (type.StartsWith("CACHESTORE_", StringComparison.Ordinal) || type.StartsWith("USERSTORE_", StringComparison.Ordinal))
                yield return (i, type, name);
        }
    }

    /// <summary>
    /// The plan stores' figures — the batch plans under <c>SQL Plans</c>, the
    /// module plans under <c>Object Plans</c> — as
    /// <c>sys.dm_exec_cached_plans</c> lists them.
    /// </summary>
    private static (long SqlPlans, long SqlPlanKb, long ObjectPlans, long ObjectPlanKb) PlanStoreFigures(Simulation simulation)
    {
        long sqlPlans = 0, sqlBytes = 0, objectPlans = 0, objectBytes = 0;
        foreach (var plan in simulation.LiveCachedPlans())
        {
            if (plan.IsBatch)
            {
                sqlPlans++;
                sqlBytes += CachedPlanSizeBytes(plan);
            }
            else
            {
                objectPlans++;
                objectBytes += CachedPlanSizeBytes(plan);
            }
        }
        return (sqlPlans, sqlBytes / 1024, objectPlans, objectBytes / 1024);
    }

    /// <summary>
    /// <c>sys.dm_os_memory_cache_counters</c>: a store for each cache and
    /// user store clerk, the plan stores holding the cached plans and every
    /// other empty; real keeps several instances of some types.
    /// </summary>
    private static IEnumerable<SqlValue[]> MemoryCacheCounterRows(Parser.BatchContext batch, HeapColumn[] columns)
    {
        var (sqlPlans, sqlKb, objectPlans, objectKb) = PlanStoreFigures(batch.Connection.Simulation);
        foreach (var (index, type, name) in CacheStores())
        {
            var (entries, pagesKb) = type switch
            {
                "CACHESTORE_OBJCP" => (objectPlans, objectKb),
                "CACHESTORE_SQLCP" => (sqlPlans, sqlKb),
                _ => (0L, 0L),
            };
            yield return StandInRow(columns,
            [
                ("cache_address", SqlValue.FromVarbinary(StandInAddress(0x21, index, 0))),
                ("name", SqlValue.FromNVarchar(name)),
                ("type", SqlValue.FromNVarchar(type)),
                ("pages_kb", SqlValue.FromInt64(pagesKb)),
                ("pages_in_use_kb", SqlValue.FromInt64(0)),
                ("entries_count", SqlValue.FromInt64(entries)),
            ]);
        }
    }

    /// <summary>
    /// <c>sys.dm_os_memory_cache_hash_tables</c>: each cache store's table,
    /// sized as real sizes it, the plan stores' buckets in use their plans.
    /// </summary>
    private static IEnumerable<SqlValue[]> MemoryCacheHashTableRows(Parser.BatchContext batch, HeapColumn[] columns)
    {
        var (sqlPlans, _, objectPlans, _) = PlanStoreFigures(batch.Connection.Simulation);
        foreach (var (index, type, name) in CacheStores())
        {
            var buckets = Array.Find(CacheStoreBuckets, entry => entry.Type == type).Buckets;
            if (buckets == 0)
                continue;
            var entries = type switch
            {
                "CACHESTORE_OBJCP" => objectPlans,
                "CACHESTORE_SQLCP" => sqlPlans,
                _ => 0L,
            };
            var inUse = (int)Math.Min(entries, buckets);
            yield return StandInRow(columns,
            [
                ("cache_address", SqlValue.FromVarbinary(StandInAddress(0x21, index, 0))),
                ("name", SqlValue.FromNVarchar(name)),
                ("type", SqlValue.FromNVarchar(type)),
                ("buckets_count", SqlValue.FromInt32(buckets)),
                ("buckets_in_use_count", SqlValue.FromInt32(inUse)),
                ("buckets_max_length", SqlValue.FromInt32(inUse > 0 ? 1 : 0)),
                ("buckets_avg_length", SqlValue.FromInt32(inUse > 0 ? 1 : 0)),
                ("buckets_max_length_ever", SqlValue.FromInt32(inUse > 0 ? 1 : 0)),
            ]);
        }
    }

    /// <summary>
    /// <c>sys.traces</c>' default trace row while <c>default trace
    /// enabled</c> is on, with real's file sizing and rollover; it records no
    /// events here (see <c>fn_trace_gettable</c>).
    /// </summary>
    private static IEnumerable<SqlValue[]> DefaultTraceRows(Simulation simulation, HeapColumn[] columns)
    {
        if (!simulation.DefaultTraceEnabled)
            yield break;
        var log = simulation.TracesIfMade?.DefaultLog;
        yield return StandInRow(columns,
        [
            ("id", SqlValue.FromInt32(1)),
            ("status", SqlValue.FromInt32(1)),
            ("path", SqlValue.FromNVarchar(log?.CurrentPath ?? DefaultTracePath)),
            ("max_size", SqlValue.FromInt64(20)),
            ("max_files", SqlValue.FromInt32(5)),
            ("is_rowset", SqlValue.FromBoolean(false)),
            ("is_rollover", SqlValue.FromBoolean(true)),
            ("is_shutdown", SqlValue.FromBoolean(false)),
            ("is_default", SqlValue.FromBoolean(true)),
            ("buffer_count", SqlValue.FromInt32(4)),
            ("buffer_size", SqlValue.FromInt32(1024)),
            ("file_position", SqlValue.FromInt64(0)),
            ("start_time", SqlValue.FromDateTime(simulation.SeedDate)),
            ("event_count", SqlValue.FromInt64(log?.EventCount ?? 0)),
        ]);
    }

    /// <summary>The default trace's file, in the error log's directory as on real's Linux server.</summary>
    internal const string DefaultTracePath = ErrorLog.Directory + "log_1.trc";
}
