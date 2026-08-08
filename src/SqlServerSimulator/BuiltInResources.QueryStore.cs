using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    /// <summary>
    /// Registers the Query Store catalog views. Column shapes are
    /// probe-confirmed against SQL Server 2025 (2026-08-08); an
    /// <c>nvarchar</c>'s declared length is half the <c>max_length</c> the
    /// probe reports, since that column counts bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Query Store's <em>configuration</em> is live — see
    /// <see cref="QueryStoreOptions"/> — but nothing is ever captured, so the
    /// eleven capture views are permanently empty whatever the state says.
    /// That is the one place this surface knowingly parts company with real: a
    /// real store in READ_WRITE fills <c>sys.query_store_query</c> within an
    /// interval, while here a database can report itself on and still show
    /// nothing.
    /// </para>
    /// <para>
    /// The two views real populates without capturing anything are populated
    /// here too, because their contents are fixed metadata rather than
    /// captured data: <c>sys.query_store_replicas</c>' four replica roles and
    /// <c>sys.database_query_store_internal_state</c>' single counter row.
    /// <c>sys.query_context_settings</c> and
    /// <c>sys.query_store_runtime_stats_interval</c> stay empty — both probed
    /// zero on a freshly-enabled store, so their rows are capture artifacts.
    /// </para>
    /// </remarks>
    private static void RegisterQueryStore(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        void SysEmpty(string name, HeapColumn[] columns) =>
            views["sys." + name] = new CatalogView(name, columns, static (_, _) => EmptyCatalogRows);

        var dateTimeOffset7 = SqlType.GetDateTimeOffset(7);
        var binary8 = SqlType.GetBinary(8);
        var nvarchar128Desc = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

        // The join key is the database context rather than a database_id
        // column, so a three-part master.sys.database_query_store_options read
        // reports master's state. SSMS's Query Store probe gates on
        // OBJECT_ID(N'[sys].[database_query_store_options]') resolving and then
        // reads actual_state.
        Sys("database_query_store_options",
        [
            new("desired_state", SqlType.SmallInt, null, false),
            new("desired_state_desc", nvarchar60Catalog, 60, true),
            new("actual_state", SqlType.SmallInt, null, false),
            new("actual_state_desc", nvarchar60Catalog, 60, true),
            new("readonly_reason", SqlType.Int32, null, true),
            new("current_storage_size_mb", SqlType.BigInt, null, true),
            new("flush_interval_seconds", SqlType.BigInt, null, true),
            new("interval_length_minutes", SqlType.BigInt, null, true),
            new("max_storage_size_mb", SqlType.BigInt, null, true),
            new("stale_query_threshold_days", SqlType.BigInt, null, true),
            new("max_plans_per_query", SqlType.BigInt, null, true),
            new("query_capture_mode", SqlType.SmallInt, null, false),
            new("query_capture_mode_desc", nvarchar60Catalog, 60, true),
            new("capture_policy_execution_count", SqlType.Int32, null, true),
            new("capture_policy_total_compile_cpu_time_ms", SqlType.BigInt, null, true),
            new("capture_policy_total_execution_cpu_time_ms", SqlType.BigInt, null, true),
            new("capture_policy_stale_threshold_hours", SqlType.Int32, null, true),
            new("size_based_cleanup_mode", SqlType.SmallInt, null, false),
            new("size_based_cleanup_mode_desc", nvarchar60Catalog, 60, true),
            new("wait_stats_capture_mode", SqlType.SmallInt, null, false),
            new("wait_stats_capture_mode_desc", nvarchar60Catalog, 60, true),
            new("actual_state_additional_info", SqlType.NVarchar, 4000, true),
        ], EnumerateSysDatabaseQueryStoreOptions);

        Sys("database_query_store_internal_state",
        [
            new("pending_message_count", SqlType.BigInt, null, false),
            new("messaging_memory_used_mb", SqlType.BigInt, null, false),
        ], static (_, _) => DatabaseQueryStoreInternalStateRows);

        Sys("query_store_replicas",
        [
            new("replica_group_id", SqlType.BigInt, null, false),
            new("role_type", SqlType.SmallInt, null, false),
            new("replica_name", SqlType.NVarchar, 644, true),
        ], EnumerateSysQueryStoreReplicas);

        // SSMS's Query Store probe does
        // IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats),
        // which must resolve and return zero rows.
        SysEmpty("query_store_runtime_stats", BuildQueryStoreRuntimeStatsColumns(nvarchar60Catalog));

        SysEmpty("query_store_runtime_stats_interval",
        [
            new("runtime_stats_interval_id", SqlType.BigInt, null, false),
            new("start_time", dateTimeOffset7, null, false),
            new("end_time", dateTimeOffset7, null, false),
            new("comment", SqlType.NVarcharMax, null, true),
        ]);

        SysEmpty("query_store_query",
        [
            new("query_id", SqlType.BigInt, null, false),
            new("query_text_id", SqlType.BigInt, null, false),
            new("context_settings_id", SqlType.BigInt, null, false),
            new("object_id", SqlType.BigInt, null, true),
            new("batch_sql_handle", SqlType.Varbinary, 44, true),
            new("query_hash", binary8, null, false),
            new("is_internal_query", SqlType.Bit, null, false),
            new("query_parameterization_type", SqlType.TinyInt, null, false),
            new("query_parameterization_type_desc", nvarchar60Catalog, 60, true),
            new("initial_compile_start_time", dateTimeOffset7, null, false),
            new("last_compile_start_time", dateTimeOffset7, null, true),
            new("last_execution_time", dateTimeOffset7, null, true),
            new("last_compile_batch_sql_handle", SqlType.Varbinary, 44, true),
            new("last_compile_batch_offset_start", SqlType.BigInt, null, true),
            new("last_compile_batch_offset_end", SqlType.BigInt, null, true),
            new("count_compiles", SqlType.BigInt, null, true),
            new("avg_compile_duration", SqlType.Float, null, true),
            new("last_compile_duration", SqlType.BigInt, null, true),
            new("avg_bind_duration", SqlType.Float, null, true),
            new("last_bind_duration", SqlType.BigInt, null, true),
            new("avg_bind_cpu_time", SqlType.Float, null, true),
            new("last_bind_cpu_time", SqlType.BigInt, null, true),
            new("avg_optimize_duration", SqlType.Float, null, true),
            new("last_optimize_duration", SqlType.BigInt, null, true),
            new("avg_optimize_cpu_time", SqlType.Float, null, true),
            new("last_optimize_cpu_time", SqlType.BigInt, null, true),
            new("avg_compile_memory_kb", SqlType.Float, null, true),
            new("last_compile_memory_kb", SqlType.BigInt, null, true),
            new("max_compile_memory_kb", SqlType.BigInt, null, true),
            new("is_clouddb_internal_query", SqlType.Bit, null, true),
        ]);

        SysEmpty("query_store_query_text",
        [
            new("query_text_id", SqlType.BigInt, null, false),
            new("query_sql_text", SqlType.NVarcharMax, null, true),
            new("statement_sql_handle", SqlType.Varbinary, 44, true),
            new("is_part_of_encrypted_module", SqlType.Bit, null, false),
            new("has_restricted_text", SqlType.Bit, null, false),
        ]);

        SysEmpty("query_store_query_variant",
        [
            new("query_variant_query_id", SqlType.BigInt, null, false),
            new("parent_query_id", SqlType.BigInt, null, false),
            new("dispatcher_plan_id", SqlType.BigInt, null, false),
        ]);

        SysEmpty("query_store_plan",
        [
            new("plan_id", SqlType.BigInt, null, false),
            new("query_id", SqlType.BigInt, null, false),
            new("plan_group_id", SqlType.BigInt, null, true),
            new("engine_version", SqlType.NVarchar, 32, true),
            new("compatibility_level", SqlType.SmallInt, null, false),
            new("query_plan_hash", binary8, null, false),
            new("query_plan", SqlType.NVarcharMax, null, true),
            new("is_online_index_plan", SqlType.Bit, null, false),
            new("is_trivial_plan", SqlType.Bit, null, false),
            new("is_parallel_plan", SqlType.Bit, null, false),
            new("is_forced_plan", SqlType.Bit, null, false),
            new("is_natively_compiled", SqlType.Bit, null, false),
            new("force_failure_count", SqlType.BigInt, null, false),
            new("last_force_failure_reason", SqlType.Int32, null, false),
            new("last_force_failure_reason_desc", nvarchar128Desc, 128, true),
            new("count_compiles", SqlType.BigInt, null, true),
            new("initial_compile_start_time", dateTimeOffset7, null, false),
            new("last_compile_start_time", dateTimeOffset7, null, true),
            new("last_execution_time", dateTimeOffset7, null, true),
            new("avg_compile_duration", SqlType.Float, null, true),
            new("last_compile_duration", SqlType.BigInt, null, true),
            new("plan_forcing_type", SqlType.Int32, null, false),
            new("plan_forcing_type_desc", nvarchar60Catalog, 60, true),
            new("has_compile_replay_script", SqlType.Bit, null, false),
            new("is_optimized_plan_forcing_disabled", SqlType.Bit, null, false),
            new("plan_type", SqlType.Int32, null, false),
            new("plan_type_desc", nvarchar60Catalog, 60, true),
        ]);

        SysEmpty("query_store_plan_feedback",
        [
            new("plan_feedback_id", SqlType.BigInt, null, false),
            new("plan_id", SqlType.BigInt, null, false),
            new("feature_id", SqlType.TinyInt, null, false),
            new("feature_desc", nvarchar60Catalog, 60, true),
            new("feedback_data", SqlType.NVarcharMax, null, true),
            new("state", SqlType.Int32, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("create_time", dateTimeOffset7, null, false),
            new("last_updated_time", dateTimeOffset7, null, true),
            new("replica_group_id", SqlType.BigInt, null, false),
        ]);

        SysEmpty("query_store_plan_forcing_locations",
        [
            new("plan_forcing_location_id", SqlType.BigInt, null, false),
            new("query_id", SqlType.BigInt, null, false),
            new("plan_id", SqlType.BigInt, null, false),
            new("replica_group_id", SqlType.BigInt, null, false),
            new("timestamp", SqlType.DateTime, null, false),
            new("plan_forcing_type", SqlType.Int32, null, false),
            new("plan_forcing_type_desc", nvarchar60Catalog, 60, true),
        ]);

        SysEmpty("query_store_query_hints",
        [
            new("query_hint_id", SqlType.BigInt, null, false),
            new("query_id", SqlType.BigInt, null, false),
            new("replica_group_id", SqlType.BigInt, null, false),
            new("query_hint_text", SqlType.NVarcharMax, null, true),
            new("last_query_hint_failure_reason", SqlType.Int32, null, false),
            new("last_query_hint_failure_reason_desc", nvarchar128Desc, 128, true),
            new("query_hint_failure_count", SqlType.BigInt, null, false),
            new("source", SqlType.Int32, null, true),
            new("source_desc", nvarchar128Desc, 128, true),
            new("comment", SqlType.NVarcharMax, null, true),
        ]);

        SysEmpty("query_store_wait_stats",
        [
            new("wait_stats_id", SqlType.BigInt, null, false),
            new("plan_id", SqlType.BigInt, null, false),
            new("runtime_stats_interval_id", SqlType.BigInt, null, false),
            new("wait_category", SqlType.SmallInt, null, false),
            new("wait_category_desc", nvarchar60Catalog, 60, true),
            new("execution_type", SqlType.TinyInt, null, false),
            new("execution_type_desc", nvarchar60Catalog, 60, true),
            new("total_query_wait_time_ms", SqlType.BigInt, null, false),
            new("avg_query_wait_time_ms", SqlType.Float, null, true),
            new("last_query_wait_time_ms", SqlType.BigInt, null, false),
            new("min_query_wait_time_ms", SqlType.BigInt, null, false),
            new("max_query_wait_time_ms", SqlType.BigInt, null, false),
            new("stdev_query_wait_time_ms", SqlType.Float, null, true),
            new("replica_group_id", SqlType.BigInt, null, false),
        ]);

        SysEmpty("query_context_settings",
        [
            new("context_settings_id", SqlType.BigInt, null, false),
            new("set_options", SqlType.Varbinary, 8, true),
            new("language_id", SqlType.SmallInt, null, false),
            new("date_format", SqlType.SmallInt, null, false),
            new("date_first", SqlType.TinyInt, null, false),
            new("status", SqlType.Varbinary, 2, true),
            new("required_cursor_options", SqlType.Int32, null, false),
            new("acceptable_cursor_options", SqlType.Int32, null, false),
            new("merge_action_type", SqlType.SmallInt, null, false),
            new("default_schema_id", SqlType.Int32, null, false),
            new("is_replication_specific", SqlType.Bit, null, false),
            new("is_contained", SqlType.Varbinary, 1, true),
        ]);
    }

    /// <summary>
    /// Column shape for <c>sys.query_store_runtime_stats</c>. The nine
    /// "core" metrics (duration, cpu_time, logical/physical IO, clr_time,
    /// dop, query_max_used_memory, rowcount) expose NOT NULL last/min/max
    /// columns; the four "extended" metrics (num_physical_io_reads,
    /// log_bytes_used, tempdb_space_used, page_server_io_reads) expose them
    /// NULL — matching the probed SQL Server 2025 catalog shape. The view is
    /// always empty, so the column set only ever backs metadata reads.
    /// </summary>
    private static HeapColumn[] BuildQueryStoreRuntimeStatsColumns(NVarcharSqlType nvarchar60Catalog)
    {
        var columns = new List<HeapColumn>
        {
            new("runtime_stats_id", SqlType.BigInt, null, false),
            new("plan_id", SqlType.BigInt, null, false),
            new("runtime_stats_interval_id", SqlType.BigInt, null, false),
            new("execution_type", SqlType.TinyInt, null, false),
            new("execution_type_desc", nvarchar60Catalog, 60, true),
            new("first_execution_time", SqlType.GetDateTimeOffset(7), null, false),
            new("last_execution_time", SqlType.GetDateTimeOffset(7), null, false),
            new("count_executions", SqlType.BigInt, null, false),
        };

        void Metric(string metric, bool aggregatesNullable)
        {
            columns.Add(new("avg_" + metric, SqlType.Float, null, true));
            columns.Add(new("last_" + metric, SqlType.BigInt, null, aggregatesNullable));
            columns.Add(new("min_" + metric, SqlType.BigInt, null, aggregatesNullable));
            columns.Add(new("max_" + metric, SqlType.BigInt, null, aggregatesNullable));
            columns.Add(new("stdev_" + metric, SqlType.Float, null, true));
        }

        foreach (var metric in new[]
        {
            "duration", "cpu_time", "logical_io_reads", "logical_io_writes",
            "physical_io_reads", "clr_time", "dop", "query_max_used_memory", "rowcount",
        })
        {
            Metric(metric, aggregatesNullable: false);
        }

        foreach (var metric in new[]
        {
            "num_physical_io_reads", "log_bytes_used", "tempdb_space_used", "page_server_io_reads",
        })
        {
            Metric(metric, aggregatesNullable: true);
        }

        columns.Add(new("replica_group_id", SqlType.BigInt, null, false));
        return [.. columns];
    }

    /// <summary>
    /// The single <c>sys.database_query_store_internal_state</c> row. Real
    /// projects one for every database, <c>master</c> included, and the
    /// simulator queues no Query Store messages, so both counters read zero.
    /// </summary>
    private static readonly SqlValue[][] DatabaseQueryStoreInternalStateRows =
        [[SqlValue.FromInt64(0), SqlValue.FromInt64(0)]];

    /// <summary>
    /// The four fixed <c>sys.query_store_replicas</c> rows — the replica roles
    /// a store recognizes, not captured data, and present on real however the
    /// store is configured.
    /// </summary>
    private static readonly SqlValue[][] QueryStoreReplicaRows =
    [
        [SqlValue.FromInt64(1), SqlValue.FromInt16(1), SqlValue.FromNVarchar("Primary")],
        [SqlValue.FromInt64(2), SqlValue.FromInt16(2), SqlValue.FromNVarchar("Secondary")],
        [SqlValue.FromInt64(3), SqlValue.FromInt16(3), SqlValue.FromNVarchar("Geo Secondary")],
        [SqlValue.FromInt64(4), SqlValue.FromInt16(4), SqlValue.FromNVarchar("Geo HA Secondary")],
    ];

    /// <summary>
    /// Rows for <c>sys.query_store_replicas</c> — the four fixed roles for a
    /// user database and nothing for any of the four system databases, which
    /// is the split real reports (probe-confirmed 2026-08-08: <c>model</c>
    /// projects none even though its own store is on).
    /// </summary>
    private static SqlValue[][] EnumerateSysQueryStoreReplicas(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        return Simulation.SystemDatabaseNames.Contains(database.Name) ? EmptyCatalogRows : QueryStoreReplicaRows;
    }

    /// <summary>
    /// Rows for <c>sys.database_query_store_options</c> — one row projecting
    /// the database's retained <see cref="QueryStoreOptions"/>, and no row at
    /// all for <c>master</c> / <c>tempdb</c>, the two databases real refuses to
    /// host a store on. <c>model</c> and <c>msdb</c> each get their row like a
    /// user database (probe-confirmed 2026-08-08).
    /// </summary>
    /// <remarks>
    /// <c>actual_state</c> tracks <c>desired_state</c> exactly: real's two
    /// diverge only while a store is transitioning or has forced itself
    /// read-only, so <c>readonly_reason</c> stays 0 and
    /// <c>actual_state_additional_info</c> the empty string, both of which real
    /// reports for a healthy store. <c>current_storage_size_mb</c> is 0 —
    /// nothing is stored. The four <c>capture_policy_*</c> columns project NULL
    /// unless the capture mode is CUSTOM, which is real's own masking; the
    /// values behind them survive a trip through another mode.
    /// </remarks>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseQueryStoreOptions(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        if (BuiltInToken.EqualsAny(database.Name, Simulation.MasterDatabaseName, Simulation.TempdbDatabaseName))
            yield break;

        var options = database.QueryStore;
        var state = SqlValue.FromInt16((short)options.DesiredState);
        var stateDesc = SqlValue.FromNVarchar(options.DesiredState switch
        {
            QueryStoreState.Off => "OFF",
            QueryStoreState.ReadOnly => "READ_ONLY",
            QueryStoreState.ReadWrite => "READ_WRITE",
            _ => "ERROR",
        });
        var isCustom = options.CaptureMode == QueryStoreCaptureMode.Custom;
        yield return [
            state,                                  // desired_state
            stateDesc,                              // desired_state_desc
            state,                                  // actual_state
            stateDesc,                              // actual_state_desc
            SqlValue.FromInt32(0),                  // readonly_reason
            SqlValue.FromInt64(0),                  // current_storage_size_mb
            SqlValue.FromInt64(options.FlushIntervalSeconds),
            SqlValue.FromInt64(options.IntervalLengthMinutes),
            SqlValue.FromInt64(options.MaxStorageSizeMb),
            SqlValue.FromInt64(options.StaleQueryThresholdDays),
            SqlValue.FromInt64(options.MaxPlansPerQuery),
            SqlValue.FromInt16((short)options.CaptureMode),
            SqlValue.FromNVarchar(options.CaptureMode switch
            {
                QueryStoreCaptureMode.All => "ALL",
                QueryStoreCaptureMode.None => "NONE",
                QueryStoreCaptureMode.Custom => "CUSTOM",
                _ => "AUTO",
            }),
            isCustom ? SqlValue.FromInt32(options.CapturePolicyExecutionCount) : SqlValue.Null(SqlType.Int32),
            isCustom ? SqlValue.FromInt64(options.CapturePolicyTotalCompileCpuTimeMs) : SqlValue.Null(SqlType.BigInt),
            isCustom ? SqlValue.FromInt64(options.CapturePolicyTotalExecutionCpuTimeMs) : SqlValue.Null(SqlType.BigInt),
            isCustom ? SqlValue.FromInt32(options.CapturePolicyStaleThresholdHours) : SqlValue.Null(SqlType.Int32),
            SqlValue.FromInt16(options.SizeBasedCleanupAuto ? (short)1 : (short)0),
            SqlValue.FromNVarchar(options.SizeBasedCleanupAuto ? "AUTO" : "OFF"),
            SqlValue.FromInt16(options.WaitStatsCaptureOn ? (short)1 : (short)0),
            SqlValue.FromNVarchar(options.WaitStatsCaptureOn ? "ON" : "OFF"),
            SqlValue.FromNVarchar(string.Empty),    // actual_state_additional_info
        ];
    }
}
