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
    /// The capture views read the database's <see cref="QueryStoreData"/>,
    /// which statements feed as they complete (see
    /// <c>Simulation.QueryStore.cs</c>); each read snapshots it under the
    /// store's lock. None of them is in the cross-statement catalog row cache:
    /// every execution a store records changes them.
    /// </para>
    /// <para>
    /// <c>sys.query_store_plan_feedback</c> and
    /// <c>sys.query_store_query_variant</c> stay empty — the simulator has no
    /// feedback loop and no parameter-sensitive plans — and
    /// <c>sys.query_store_wait_stats</c> carries only lock waits. The two views real populates without capturing
    /// anything are populated here too, because their contents are fixed
    /// metadata rather than captured data: <c>sys.query_store_replicas</c>'
    /// four replica roles and <c>sys.database_query_store_internal_state</c>'
    /// single counter row.
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
            new("actual_state_additional_info", NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit), 4000, true),
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
        Sys("query_store_runtime_stats", BuildQueryStoreRuntimeStatsColumns(nvarchar60Catalog), EnumerateQueryStoreRuntimeStats);

        Sys("query_store_runtime_stats_interval",
        [
            new("runtime_stats_interval_id", SqlType.BigInt, null, false),
            new("start_time", dateTimeOffset7, null, false),
            new("end_time", dateTimeOffset7, null, false),
            new("comment", SqlType.NVarcharMax, null, true),
        ], EnumerateQueryStoreIntervals);

        Sys("query_store_query",
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
        ], EnumerateQueryStoreQueries);

        Sys("query_store_query_text",
        [
            new("query_text_id", SqlType.BigInt, null, false),
            new("query_sql_text", SqlType.NVarcharMax, null, true),
            new("statement_sql_handle", SqlType.Varbinary, 44, true),
            new("is_part_of_encrypted_module", SqlType.Bit, null, false),
            new("has_restricted_text", SqlType.Bit, null, false),
        ], EnumerateQueryStoreTexts);

        SysEmpty("query_store_query_variant",
        [
            new("query_variant_query_id", SqlType.BigInt, null, false),
            new("parent_query_id", SqlType.BigInt, null, false),
            new("dispatcher_plan_id", SqlType.BigInt, null, false),
        ]);

        Sys("query_store_plan",
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
        ], EnumerateQueryStorePlans);

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

        Sys("query_store_plan_forcing_locations",
        [
            new("plan_forcing_location_id", SqlType.BigInt, null, false),
            new("query_id", SqlType.BigInt, null, false),
            new("plan_id", SqlType.BigInt, null, false),
            new("replica_group_id", SqlType.BigInt, null, false),
            new("timestamp", SqlType.DateTime, null, false),
            new("plan_forcing_type", SqlType.Int32, null, false),
            new("plan_forcing_type_desc", nvarchar60Catalog, 60, true),
        ], EnumerateQueryStoreForcingLocations);

        Sys("query_store_query_hints",
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
        ], EnumerateQueryStoreHints);

        Sys("query_store_wait_stats",
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
        ], EnumerateQueryStoreWaitStats);

        Sys("query_context_settings",
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
        ], EnumerateQueryContextSettings);
    }

    /// <summary>
    /// Column shape for <c>sys.query_store_runtime_stats</c>. The nine
    /// "core" metrics (duration, cpu_time, logical/physical IO, clr_time,
    /// dop, query_max_used_memory, rowcount) expose NOT NULL last/min/max
    /// columns; the four "extended" metrics (num_physical_io_reads,
    /// log_bytes_used, tempdb_space_used, page_server_io_reads) expose them
    /// NULL — matching the probed SQL Server 2025 catalog shape.
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
    /// reports for a healthy store. <c>current_storage_size_mb</c> rounds what
    /// the store holds up to whole megabytes. The four <c>capture_policy_*</c> columns project NULL
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
            SqlValue.FromInt64(StorageSizeMb(database.QueryStoreData)),
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

    private static long StorageSizeMb(QueryStoreData data)
    {
        lock (data.Gate)
            return data.StorageSizeMb();
    }

    private static readonly SqlType QueryStoreTimeType = SqlType.GetDateTimeOffset(7);
    private static readonly SqlType QueryStoreHashType = SqlType.GetBinary(8);

    /// <summary>A Query Store timestamp, which real reports in UTC.</summary>
    private static SqlValue QueryStoreTime(DateTime utc) =>
        SqlValue.FromDateTimeOffset(QueryStoreTimeType, new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), TimeSpan.Zero));

    private static SqlValue QueryStoreHash(byte[] hash) => SqlValue.FromBinary(QueryStoreHashType, hash);

    /// <summary>Rows for <c>sys.query_store_query_text</c>, one per distinct stored text.</summary>
    private static List<SqlValue[]> EnumerateQueryStoreTexts(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var text in data.Texts)
            {
                rows.Add([
                    SqlValue.FromInt64(text.Id),
                    SqlValue.FromNVarchar(text.Text),
                    SqlValue.FromVarbinary(text.StatementSqlHandle),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromBoolean(false),
                ]);
            }
        }
        return rows;
    }

    /// <summary>
    /// Rows for <c>sys.query_store_query</c>. The compile, bind and optimize
    /// figures read 0 — the simulator compiles a statement as it runs it, so
    /// there is no compile phase of its own to time — and each query compiles
    /// once, as a plan real keeps cached does.
    /// </summary>
    private static List<SqlValue[]> EnumerateQueryStoreQueries(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        var zeroFloat = SqlValue.FromDouble(0);
        var zero = SqlValue.FromInt64(0);
        lock (data.Gate)
        {
            foreach (var query in data.Queries)
            {
                var type = query.Key.ParameterizationType;
                rows.Add([
                    SqlValue.FromInt64(query.QueryId),
                    SqlValue.FromInt64(query.Text.Id),
                    SqlValue.FromInt64(query.Context.Id),
                    SqlValue.FromInt64(query.Key.ObjectId),
                    query.BatchSqlHandle is { } handle ? SqlValue.FromVarbinary(handle) : SqlValue.Null(SqlType.Varbinary),
                    QueryStoreHash(query.QueryHash),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromByte(type),
                    SqlValue.FromNVarchar(type switch
                    {
                        1 => "User",
                        2 => "Simple",
                        3 => "Forced",
                        _ => "None",
                    }),
                    QueryStoreTime(query.InitialCompileStartTime),
                    QueryStoreTime(query.LastCompileStartTime),
                    QueryStoreTime(query.LastExecutionTime),
                    SqlValue.FromVarbinary(query.LastCompileBatchSqlHandle),
                    SqlValue.FromInt64(query.LastCompileBatchOffsetStart),
                    SqlValue.FromInt64(query.LastCompileBatchOffsetEnd),
                    SqlValue.FromInt64(query.CountCompiles),
                    zeroFloat, zero,    // compile duration
                    zeroFloat, zero,    // bind duration
                    zeroFloat, zero,    // bind CPU
                    zeroFloat, zero,    // optimize duration
                    zeroFloat, zero,    // optimize CPU
                    zeroFloat, zero, zero, // compile memory
                    SqlValue.FromBoolean(false),
                ]);
            }
        }
        return rows;
    }

    /// <summary>
    /// Rows for <c>sys.query_store_plan</c>: one plan per query, whose
    /// <c>query_plan</c> is a ShowPlan skeleton without an operator tree.
    /// </summary>
    private static List<SqlValue[]> EnumerateQueryStorePlans(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        var engineVersion = SqlValue.FromNVarchar(ReferenceBuild.ProductVersion);
        lock (data.Gate)
        {
            foreach (var plan in data.Plans)
            {
                rows.Add([
                    SqlValue.FromInt64(plan.PlanId),
                    SqlValue.FromInt64(plan.Query.QueryId),
                    SqlValue.FromInt64(0),
                    engineVersion,
                    SqlValue.FromInt16(plan.CompatibilityLevel),
                    QueryStoreHash(plan.QueryPlanHash),
                    SqlValue.FromNVarchar(plan.PlanXml),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromBoolean(plan.IsTrivial),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromBoolean(plan.IsForced),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromInt64(0),
                    SqlValue.FromInt32(0),
                    SqlValue.FromNVarchar("NONE"),
                    SqlValue.FromInt64(1),
                    QueryStoreTime(plan.InitialCompileStartTime),
                    QueryStoreTime(plan.InitialCompileStartTime),
                    QueryStoreTime(plan.LastExecutionTime),
                    SqlValue.FromDouble(0),
                    SqlValue.FromInt64(0),
                    SqlValue.FromInt32(plan.IsForced ? 1 : 0),
                    SqlValue.FromNVarchar(plan.IsForced ? "MANUAL" : "NONE"),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromBoolean(plan.OptimizedPlanForcingDisabled),
                    SqlValue.FromInt32(0),
                    SqlValue.FromNVarchar("Compiled Plan"),
                ]);
            }
        }
        return rows;
    }

    /// <summary>
    /// Rows for <c>sys.query_store_runtime_stats</c>, one per plan, interval
    /// and execution type. Duration and CPU are measured, in microseconds;
    /// logical reads are the pages the statement entered, as
    /// <c>STATISTICS IO</c> counts them; every execution runs at DOP 1 with
    /// no memory grant, and the physical, CLR, log and tempdb figures read 0.
    /// </summary>
    private static List<SqlValue[]> EnumerateQueryStoreRuntimeStats(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var stats in data.RuntimeStats)
            {
                var row = new List<SqlValue>(81)
                {
                    SqlValue.FromInt64(stats.Id),
                    SqlValue.FromInt64(stats.PlanId),
                    SqlValue.FromInt64(stats.IntervalId),
                    SqlValue.FromByte(stats.ExecutionType),
                    SqlValue.FromNVarchar(stats.ExecutionType switch
                    {
                        3 => "Aborted",
                        4 => "Exception",
                        _ => "Regular",
                    }),
                    QueryStoreTime(stats.FirstExecutionTime),
                    QueryStoreTime(stats.LastExecutionTime),
                    SqlValue.FromInt64(stats.Count),
                };
                void Measured(List<SqlValue> row, QueryStoreMetric metric, long count)
                {
                    row.Add(SqlValue.FromDouble(metric.Average(count)));
                    row.Add(SqlValue.FromInt64(metric.Last));
                    row.Add(SqlValue.FromInt64(metric.Min));
                    row.Add(SqlValue.FromInt64(metric.Max));
                    row.Add(SqlValue.FromDouble(metric.StandardDeviation(count)));
                }
                void Constant(List<SqlValue> row, long value)
                {
                    row.Add(SqlValue.FromDouble(value));
                    row.Add(SqlValue.FromInt64(value));
                    row.Add(SqlValue.FromInt64(value));
                    row.Add(SqlValue.FromInt64(value));
                    row.Add(SqlValue.FromDouble(0));
                }
                Measured(row, stats.Duration, stats.Count);
                Measured(row, stats.CpuTime, stats.Count);
                Measured(row, stats.LogicalIoReads, stats.Count);
                Measured(row, stats.LogicalIoWrites, stats.Count);
                Constant(row, 0);   // physical_io_reads
                Constant(row, 0);   // clr_time
                Constant(row, 1);   // dop
                Constant(row, 0);   // query_max_used_memory
                Measured(row, stats.RowCount, stats.Count);
                Constant(row, 0);   // num_physical_io_reads
                Constant(row, 0);   // log_bytes_used
                Constant(row, 0);   // tempdb_space_used
                Constant(row, 0);   // page_server_io_reads
                row.Add(SqlValue.FromInt64(1));
                rows.Add([.. row]);
            }
        }
        return rows;
    }

    /// <summary>Rows for <c>sys.query_store_runtime_stats_interval</c>, opened by the first execution in each.</summary>
    private static List<SqlValue[]> EnumerateQueryStoreIntervals(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var interval in data.Intervals)
                rows.Add([SqlValue.FromInt64(interval.Id), QueryStoreTime(interval.Start), QueryStoreTime(interval.End), SqlValue.Null(SqlType.NVarcharMax)]);
        }
        return rows;
    }

    /// <summary>
    /// Rows for <c>sys.query_context_settings</c>: <c>set_options</c> as real's
    /// four big-endian bytes, the <c>status</c> real reports for an ordinary
    /// session (0x0400), no cursor options.
    /// </summary>
    private static List<SqlValue[]> EnumerateQueryContextSettings(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var context in data.ContextSettings)
            {
                var key = context.Key;
                var setOptions = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(setOptions, key.SetOptions);
                rows.Add([
                    SqlValue.FromInt64(context.Id),
                    SqlValue.FromVarbinary(setOptions),
                    SqlValue.FromInt16(key.LanguageId),
                    SqlValue.FromInt16(key.DateFormat),
                    SqlValue.FromByte(key.DateFirst),
                    SqlValue.FromVarbinary([0x04, 0x00]),
                    SqlValue.FromInt32(0),
                    SqlValue.FromInt32(0),
                    SqlValue.FromInt16(0),
                    SqlValue.FromInt32(key.DefaultSchemaId),
                    SqlValue.FromBoolean(false),
                    SqlValue.FromVarbinary([0x00]),
                ]);
            }
        }
        return rows;
    }

    /// <summary>Rows for <c>sys.query_store_query_hints</c>, set by <c>sp_query_store_set_hints</c>.</summary>
    private static List<SqlValue[]> EnumerateQueryStoreHints(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var hint in data.Hints)
            {
                rows.Add([
                    SqlValue.FromInt64(hint.Id),
                    SqlValue.FromInt64(hint.QueryId),
                    SqlValue.FromInt64(1),
                    SqlValue.FromNVarchar(hint.Text),
                    SqlValue.FromInt32(0),
                    SqlValue.FromNVarchar("NONE"),
                    SqlValue.FromInt64(0),
                    SqlValue.FromInt32(0),
                    SqlValue.FromNVarchar("User"),
                    SqlValue.Null(SqlType.NVarcharMax),
                ]);
            }
        }
        return rows;
    }

    /// <summary>Rows for <c>sys.query_store_plan_forcing_locations</c>, one per plan <c>sp_query_store_force_plan</c> forced.</summary>
    private static List<SqlValue[]> EnumerateQueryStoreForcingLocations(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var location in data.ForcingLocations)
            {
                rows.Add([
                    SqlValue.FromInt64(location.Id),
                    SqlValue.FromInt64(location.QueryId),
                    SqlValue.FromInt64(location.PlanId),
                    SqlValue.FromInt64(1),
                    SqlValue.FromDateTime(location.Timestamp.ToLocalTime()),
                    SqlValue.FromInt32(1),
                    SqlValue.FromNVarchar("MANUAL"),
                ]);
            }
        }
        return rows;
    }

    /// <summary>
    /// Rows for <c>sys.query_store_wait_stats</c>: a plan's lock waits per
    /// interval and execution type, category 3 (<c>Lock</c>), the only waits
    /// the simulator has.
    /// </summary>
    private static List<SqlValue[]> EnumerateQueryStoreWaitStats(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var data = database.QueryStoreData;
        var rows = new List<SqlValue[]>();
        lock (data.Gate)
        {
            foreach (var waits in data.WaitStats)
            {
                rows.Add([
                    SqlValue.FromInt64(waits.Id),
                    SqlValue.FromInt64(waits.PlanId),
                    SqlValue.FromInt64(waits.IntervalId),
                    SqlValue.FromInt16(3),
                    SqlValue.FromNVarchar("Lock"),
                    SqlValue.FromByte(waits.ExecutionType),
                    SqlValue.FromNVarchar(waits.ExecutionType switch
                    {
                        3 => "Aborted",
                        4 => "Exception",
                        _ => "Regular",
                    }),
                    SqlValue.FromInt64((long)waits.WaitTime.Sum),
                    SqlValue.FromDouble(waits.WaitTime.Average(waits.Count)),
                    SqlValue.FromInt64(waits.WaitTime.Last),
                    SqlValue.FromInt64(waits.WaitTime.Min),
                    SqlValue.FromInt64(waits.WaitTime.Max),
                    SqlValue.FromDouble(waits.WaitTime.StandardDeviation(waits.Count)),
                    SqlValue.FromInt64(1),
                ]);
            }
        }
        return rows;
    }
}
