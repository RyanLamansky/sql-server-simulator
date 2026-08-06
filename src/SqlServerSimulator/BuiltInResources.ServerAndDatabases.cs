using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterServerAndDatabases(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // sys.databases: the full 98-column projection SQL Server 2025 emits,
        // so SSMS's SMO Object-Explorer enumeration (which references
        // owner_sid / create_date / state_desc / recovery_model_desc /
        // containment / the is_* option flags) resolves every column. One row
        // per Database via DatabasesWithIds. Modeled columns read live Database
        // state (name / database_id / compatibility_level / collation_name /
        // snapshot-isolation trio / recovery_model / state); the remaining
        // option-flag columns carry a stock freshly-created-user-database
        // profile as constant defaults (see EnumerateSysDatabases).
        Sys("databases",
        [
            new("name", SqlType.SystemName, 128, false),
            new("database_id", SqlType.Int32, null, false),
            new("source_database_id", SqlType.Int32, null, true),
            new("owner_sid", SqlType.Varbinary, 85, true),
            new("create_date", SqlType.DateTime, null, false),
            new("compatibility_level", SqlType.TinyInt, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("user_access", SqlType.TinyInt, null, true),
            new("user_access_desc", nvarchar60Catalog, 60, true),
            new("is_read_only", SqlType.Bit, null, true),
            new("is_auto_close_on", SqlType.Bit, null, false),
            new("is_auto_shrink_on", SqlType.Bit, null, true),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("is_in_standby", SqlType.Bit, null, true),
            new("is_cleanly_shutdown", SqlType.Bit, null, true),
            new("is_supplemental_logging_enabled", SqlType.Bit, null, true),
            new("snapshot_isolation_state", SqlType.TinyInt, null, true),
            new("snapshot_isolation_state_desc", nvarchar60Catalog, 60, true),
            new("is_read_committed_snapshot_on", SqlType.Bit, null, true),
            new("recovery_model", SqlType.TinyInt, null, true),
            new("recovery_model_desc", nvarchar60Catalog, 60, true),
            new("page_verify_option", SqlType.TinyInt, null, true),
            new("page_verify_option_desc", nvarchar60Catalog, 60, true),
            new("is_auto_create_stats_on", SqlType.Bit, null, true),
            new("is_auto_create_stats_incremental_on", SqlType.Bit, null, true),
            new("is_auto_update_stats_on", SqlType.Bit, null, true),
            new("is_auto_update_stats_async_on", SqlType.Bit, null, true),
            new("is_ansi_null_default_on", SqlType.Bit, null, true),
            new("is_ansi_nulls_on", SqlType.Bit, null, true),
            new("is_ansi_padding_on", SqlType.Bit, null, true),
            new("is_ansi_warnings_on", SqlType.Bit, null, true),
            new("is_arithabort_on", SqlType.Bit, null, true),
            new("is_concat_null_yields_null_on", SqlType.Bit, null, true),
            new("is_numeric_roundabort_on", SqlType.Bit, null, true),
            new("is_quoted_identifier_on", SqlType.Bit, null, true),
            new("is_recursive_triggers_on", SqlType.Bit, null, true),
            new("is_cursor_close_on_commit_on", SqlType.Bit, null, true),
            new("is_local_cursor_default", SqlType.Bit, null, true),
            new("is_fulltext_enabled", SqlType.Bit, null, true),
            new("is_trustworthy_on", SqlType.Bit, null, true),
            new("is_db_chaining_on", SqlType.Bit, null, true),
            new("is_parameterization_forced", SqlType.Bit, null, true),
            new("is_master_key_encrypted_by_server", SqlType.Bit, null, false),
            new("is_query_store_on", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, false),
            new("is_subscribed", SqlType.Bit, null, false),
            new("is_merge_published", SqlType.Bit, null, false),
            new("is_distributor", SqlType.Bit, null, false),
            new("is_sync_with_backup", SqlType.Bit, null, false),
            new("service_broker_guid", SqlType.UniqueIdentifier, null, false),
            new("is_broker_enabled", SqlType.Bit, null, false),
            new("log_reuse_wait", SqlType.TinyInt, null, true),
            new("log_reuse_wait_desc", nvarchar60Catalog, 60, true),
            new("is_date_correlation_on", SqlType.Bit, null, false),
            new("is_cdc_enabled", SqlType.Bit, null, false),
            new("is_encrypted", SqlType.Bit, null, true),
            new("is_honor_broker_priority_on", SqlType.Bit, null, true),
            new("replica_id", SqlType.UniqueIdentifier, null, true),
            new("group_database_id", SqlType.UniqueIdentifier, null, true),
            new("resource_pool_id", SqlType.Int32, null, true),
            new("default_language_lcid", SqlType.SmallInt, null, true),
            new("default_language_name", nvarchar128Catalog, 128, true),
            new("default_fulltext_language_lcid", SqlType.Int32, null, true),
            new("default_fulltext_language_name", nvarchar128Catalog, 128, true),
            new("is_nested_triggers_on", SqlType.Bit, null, true),
            new("is_transform_noise_words_on", SqlType.Bit, null, true),
            new("two_digit_year_cutoff", SqlType.SmallInt, null, true),
            new("containment", SqlType.TinyInt, null, true),
            new("containment_desc", nvarchar60Catalog, 60, true),
            new("target_recovery_time_in_seconds", SqlType.Int32, null, true),
            new("delayed_durability", SqlType.Int32, null, true),
            new("delayed_durability_desc", nvarchar60Catalog, 60, true),
            new("is_memory_optimized_elevate_to_snapshot_on", SqlType.Bit, null, true),
            new("is_federation_member", SqlType.Bit, null, true),
            new("is_remote_data_archive_enabled", SqlType.Bit, null, true),
            new("is_mixed_page_allocation_on", SqlType.Bit, null, true),
            new("is_temporal_history_retention_enabled", SqlType.Bit, null, true),
            new("catalog_collation_type", SqlType.Int32, null, false),
            new("catalog_collation_type_desc", nvarchar60Catalog, 60, true),
            new("physical_database_name", nvarchar128Catalog, 128, true),
            new("is_result_set_caching_on", SqlType.Bit, null, true),
            new("is_accelerated_database_recovery_on", SqlType.Bit, null, true),
            new("is_tempdb_spill_to_remote_store", SqlType.Bit, null, true),
            new("is_stale_page_detection_on", SqlType.Bit, null, true),
            new("is_memory_optimized_enabled", SqlType.Bit, null, true),
            new("is_data_retention_enabled", SqlType.Bit, null, true),
            new("is_ledger_on", SqlType.Bit, null, true),
            new("is_change_feed_enabled", SqlType.Bit, null, true),
            new("is_data_lake_replication_enabled", SqlType.Bit, null, true),
            new("is_event_stream_enabled", SqlType.Bit, null, true),
            new("data_compaction", SqlType.TinyInt, null, true),
            new("data_compaction_desc", nvarchar60Catalog, 60, true),
            new("data_lake_log_publishing", SqlType.TinyInt, null, true),
            new("data_lake_log_publishing_desc", nvarchar60Catalog, 60, true),
            new("is_vorder_enabled", SqlType.Bit, null, true),
            new("is_proactive_statistics_refresh_on", SqlType.Bit, null, true),
            new("is_optimized_locking_on", SqlType.Bit, null, true),
        ], EnumerateSysDatabases);

        // sys.fn_helpcollations() — table-valued metadata function listing the
        // collations the simulator recognizes. Real SQL Server emits ~5540
        // rows; the simulator emits the whitelist defined in Collation.Recognized
        // (currently 2). Each row carries the canonical name + a human
        // description, matching real SQL Server's column shape.
        Sys("fn_helpcollations",
        [
            new("name", SqlType.SystemName, 128, true),
            new("description", SqlType.NVarchar, 1000, true),
        ], EnumerateFnHelpCollations);

        // sys.servers: the local instance projects as row 0 (is_linked = 0);
        // each entry in <see cref="Simulation.ActiveLinkedServers"/> follows
        // with a stable monotonic server_id keyed by name-sort. Real SQL
        // Server exposes ~26 columns; the simulator surfaces the
        // load-bearing subset that BACPAC scripts + diagnostic queries
        // touch (server_id / name / product / provider / data_source /
        // is_linked). modify_date is always epoch-zero since linked-server
        // registration doesn't carry a creation timestamp.
        Sys("servers",
        [
            new("server_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("product", SqlType.NVarchar, 128, true),
            new("provider", SqlType.NVarchar, 128, true),
            new("data_source", SqlType.NVarchar, 4000, true),
            new("is_linked", SqlType.Bit, null, false),
        ], EnumerateSysServers);

        // sys.dm_os_host_info: single-row, server-scope DMV describing the
        // host operating system. SSMS selects host_platform from it on every
        // connect. The row reflects the actual .NET host process rather than a
        // canned Windows row: host_platform via OperatingSystem.Is*,
        // host_architecture via RuntimeInformation.OSArchitecture (uppercased),
        // and on Linux host_distribution / host_release parsed from
        // /etc/os-release. host_sku is 48 on Windows and NULL elsewhere
        // (matching real SQL Server on Linux); os_language_version is 1033;
        // host_service_pack_level is the empty string. Computed once into
        // DmOsHostInfoRows since host identity is fixed for the process lifetime.
        Sys("dm_os_host_info",
        [
            new("host_platform", SqlType.NVarchar, 256, false),
            new("host_distribution", SqlType.NVarchar, 256, false),
            new("host_release", SqlType.NVarchar, 256, false),
            new("host_service_pack_level", SqlType.NVarchar, 256, false),
            new("host_sku", SqlType.Int32, null, true),
            new("os_language_version", SqlType.Int32, null, false),
            new("host_architecture", SqlType.NVarchar, 256, false),
        ], (batch, database) => DmOsHostInfoRows);

        // sys.time_zone_info: the Windows time-zone catalog, server-scope.
        // mssql-django probes it as its `has_zoneinfo_database` capability
        // check (`SELECT TOP 1 1 FROM sys.time_zone_info`), and the capability
        // is genuine here — AT TIME ZONE already matches real including DST.
        // Names are baked (real reports Windows ids; the ICU mapping behind
        // TimeZoneInfo yields IANA names on Linux) while the offset and DST
        // flag are computed live per query, so the row reflects the current
        // instant the way real's does.
        Sys("time_zone_info",
        [
            new("name", SqlType.NVarchar, 128, false),
            new("current_utc_offset", SqlType.NVarchar, 6, false),
            new("is_currently_dst", SqlType.Bit, null, false),
        ], (batch, database) => TimeZoneInfoRows());

        // sys.dm_exec_sessions: one row per live connection, server-scope.
        // SMO's contained-authentication check reads
        // `authenticating_database_id ... WHERE session_id = @@SPID` (always 1
        // here — SQL-auth against master), and monitoring-flavored tooling
        // reads the session-option columns. Where the simulator genuinely
        // tracks session state the row reflects it live (quoted_identifier,
        // arithabort, the ANSI bits, text_size, lock_timeout,
        // transaction_isolation_level, context_info, row_count = @@ROWCOUNT,
        // prev_error = @@ERROR, open_transaction_count, database_id,
        // host_name / program_name off the connection string or LOGIN7,
        // login_name / original_login_name and their derived SIDs); the
        // remainder are probe-confirmed fresh-session defaults from SQL Server
        // 2025 (endpoint_id 4, group_id 2, client_version 7,
        // ansi_null_dflt_on 1). status is 'running' for the querying session,
        // 'sleeping' for the rest.
        Sys("dm_exec_sessions",
        [
            new("session_id", SqlType.SmallInt, null, false),
            new("login_time", SqlType.DateTime, null, false),
            new("host_name", SqlType.NVarchar, 128, true),
            new("program_name", SqlType.NVarchar, 128, true),
            new("host_process_id", SqlType.Int32, null, true),
            new("client_version", SqlType.Int32, null, true),
            new("client_interface_name", SqlType.NVarchar, 32, true),
            new("security_id", SqlType.Varbinary, 85, false),
            new("login_name", SqlType.NVarchar, 128, false),
            new("nt_domain", SqlType.NVarchar, 128, true),
            new("nt_user_name", SqlType.NVarchar, 128, true),
            new("status", nvarchar60Catalog, 30, false),
            new("context_info", SqlType.Varbinary, 128, true),
            new("cpu_time", SqlType.Int32, null, false),
            new("memory_usage", SqlType.Int32, null, false),
            new("total_scheduled_time", SqlType.Int32, null, false),
            new("total_elapsed_time", SqlType.Int32, null, false),
            new("endpoint_id", SqlType.Int32, null, false),
            new("last_request_start_time", SqlType.DateTime, null, false),
            new("last_request_end_time", SqlType.DateTime, null, true),
            new("reads", SqlType.BigInt, null, false),
            new("writes", SqlType.BigInt, null, false),
            new("logical_reads", SqlType.BigInt, null, false),
            new("is_user_process", SqlType.Bit, null, false),
            new("text_size", SqlType.Int32, null, false),
            new("language", SqlType.NVarchar, 128, true),
            new("date_format", SqlType.NVarchar, 3, true),
            new("date_first", SqlType.SmallInt, null, false),
            new("quoted_identifier", SqlType.Bit, null, false),
            new("arithabort", SqlType.Bit, null, false),
            new("ansi_null_dflt_on", SqlType.Bit, null, false),
            new("ansi_defaults", SqlType.Bit, null, false),
            new("ansi_warnings", SqlType.Bit, null, false),
            new("ansi_padding", SqlType.Bit, null, false),
            new("ansi_nulls", SqlType.Bit, null, false),
            new("concat_null_yields_null", SqlType.Bit, null, false),
            new("transaction_isolation_level", SqlType.SmallInt, null, false),
            new("lock_timeout", SqlType.Int32, null, false),
            new("deadlock_priority", SqlType.Int32, null, false),
            new("row_count", SqlType.BigInt, null, false),
            new("prev_error", SqlType.Int32, null, false),
            new("original_security_id", SqlType.Varbinary, 85, false),
            new("original_login_name", SqlType.NVarchar, 128, false),
            new("last_successful_logon", SqlType.DateTime, null, true),
            new("last_unsuccessful_logon", SqlType.DateTime, null, true),
            new("unsuccessful_logons", SqlType.BigInt, null, true),
            new("group_id", SqlType.Int32, null, false),
            new("database_id", SqlType.SmallInt, null, false),
            new("authenticating_database_id", SqlType.Int32, null, true),
            new("open_transaction_count", SqlType.Int32, null, false),
            new("page_server_reads", SqlType.BigInt, null, false),
            new("contained_availability_group_id", SqlType.UniqueIdentifier, null, true),
        ], EnumerateSysDmExecSessions);

        // sys.configurations: server-scoped static server-configuration
        // catalog. value / minimum / maximum / value_in_use are sql_variant,
        // matching real SQL Server — every option carries an inner base type of
        // int (probe-confirmed against SQL Server 2025, even 'max server memory
        // (MB)'). The 106 rows are a stock instance's defaults —
        // configuration_id and name are stable across instances, and value
        // mirrors value_in_use on a fresh server. This is static catalog data,
        // not a live settings model: SET / sp_configure changes are not
        // reflected. SMO reads value_in_use for configuration_id 16384 (Agent
        // XPs) during SSMS's Object-Explorer database-node preamble, so the row
        // set must resolve for that folder to populate. Row set is independent
        // of the database argument.
        Sys("configurations",
        [
            new("configuration_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 35, false),
            new("value", SqlType.SqlVariant, null, true),
            new("minimum", SqlType.SqlVariant, null, true),
            new("maximum", SqlType.SqlVariant, null, true),
            new("value_in_use", SqlType.SqlVariant, null, true),
            new("description", SqlType.NVarchar, 255, false),
            new("is_dynamic", SqlType.Bit, null, false),
            new("is_advanced", SqlType.Bit, null, false),
        ], (batch, database) => ConfigurationRowsFor(batch));

        // sys.database_scoped_configurations: per-database configuration knobs.
        // value / value_for_secondary are sql_variant, matching real SQL Server:
        // each row carries its own inner base type (MAXDOP int, the bit-valued
        // knobs bit), so a bit knob reads back as bool (SSMS's ON/OFF) and
        // DacFx's (bool)reader[value] unbox on LEGACY_CARDINALITY_ESTIMATION
        // succeeds. SSMS's ISNULL(value_for_secondary, 'PRIMARY') /
        // ISNULL(value, 'NULL') also work — the variant NULL falls through to
        // the string fallback, and the ISNULL result stays sql_variant.
        // Static defaults for a fresh database — the simulator doesn't track
        // ALTER DATABASE SCOPED CONFIGURATION changes. The row set is
        // independent of the database.
        Sys("database_scoped_configurations",
        [
            new("configuration_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("value", SqlType.SqlVariant, null, true),
            new("value_for_secondary", SqlType.SqlVariant, null, true),
            new("is_value_default", SqlType.Bit, null, true),
        ], (batch, database) => DatabaseScopedConfigurationRows);

        // sys.database_mirroring: one row per database (join key database_id),
        // surfaced so SSMS's Object-Explorer enumeration
        // (master.sys.databases LEFT JOIN sys.database_mirroring) populates the
        // Databases folder. The simulator never mirrors a database, so every
        // mirroring_* column is NULL on every row — the exact non-mirrored
        // shape a live SQL Server 2025 returns (probe-confirmed: only
        // database_id populated). mirroring_failover_lsn / _end_of_log_lsn /
        // _replication_lsn are numeric(25, 0) on the server; surfaced NULL.
        Sys("database_mirroring",
        [
            new("database_id", SqlType.Int32, null, false),
            new("mirroring_guid", SqlType.UniqueIdentifier, null, true),
            new("mirroring_state", SqlType.TinyInt, null, true),
            new("mirroring_state_desc", nvarchar60Catalog, 60, true),
            new("mirroring_role", SqlType.TinyInt, null, true),
            new("mirroring_role_desc", nvarchar60Catalog, 60, true),
            new("mirroring_role_sequence", SqlType.Int32, null, true),
            new("mirroring_safety_level", SqlType.TinyInt, null, true),
            new("mirroring_safety_level_desc", nvarchar60Catalog, 60, true),
            new("mirroring_safety_sequence", SqlType.Int32, null, true),
            new("mirroring_partner_name", SqlType.NVarchar, 128, true),
            new("mirroring_partner_instance", SqlType.NVarchar, 128, true),
            new("mirroring_witness_name", SqlType.NVarchar, 128, true),
            new("mirroring_witness_state", SqlType.TinyInt, null, true),
            new("mirroring_witness_state_desc", nvarchar60Catalog, 60, true),
            new("mirroring_failover_lsn", lsnNumeric, null, true),
            new("mirroring_connection_timeout", SqlType.Int32, null, true),
            new("mirroring_redo_queue", SqlType.Int32, null, true),
            new("mirroring_redo_queue_type", nvarchar60Catalog, 60, true),
            new("mirroring_end_of_log_lsn", lsnNumeric, null, true),
            new("mirroring_replication_lsn", lsnNumeric, null, true),
        ], EnumerateSysDatabaseMirroring);

        // sys.endpoints: server-scope endpoint catalog. The simulator's TDS
        // listener isn't surfaced as a configured endpoint object, so the view
        // is always empty — SMO's Server.Endpoints enumeration does
        // `SELECT e.name FROM sys.endpoints AS e ORDER BY [Name]`, which must
        // resolve and return zero rows (the real server's built-in system
        // endpoints aren't modeled). Probe-confirmed column shape (SQL Server
        // 2025).
        Sys("endpoints",
        [
            new("name", SqlType.SystemName, 128, false),
            new("endpoint_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("protocol", SqlType.TinyInt, null, false),
            new("protocol_desc", nvarchar60Catalog, 60, true),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("state", SqlType.TinyInt, null, false),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("is_admin_endpoint", SqlType.Bit, null, false),
        ], static (_, _) => EmptyCatalogRows);

        // sys.availability_replicas: server-scope AlwaysOn Availability-Group
        // catalog. No AGs are configured in the simulator, so the view is
        // always empty — SSMS's enumeration does
        // `insert into #tmp select replica_id, group_id, replica_server_name
        // from master.sys.availability_replicas`, which must resolve and
        // return zero rows. Full column shape modeled so future tooling
        // selecting other columns doesn't hit Msg 207.
        Sys("availability_replicas",
        [
            new("replica_id", SqlType.UniqueIdentifier, null, true),
            new("group_id", SqlType.UniqueIdentifier, null, true),
            new("replica_metadata_id", SqlType.Int32, null, true),
            new("replica_server_name", SqlType.NVarchar, 256, true),
            new("owner_sid", SqlType.Varbinary, 85, true),
            new("endpoint_url", SqlType.NVarchar, 256, true),
            new("availability_mode", SqlType.TinyInt, null, true),
            new("availability_mode_desc", nvarchar60Catalog, 60, true),
            new("failover_mode", SqlType.TinyInt, null, true),
            new("failover_mode_desc", nvarchar60Catalog, 60, true),
            new("session_timeout", SqlType.Int32, null, true),
            new("primary_role_allow_connections", SqlType.TinyInt, null, true),
            new("primary_role_allow_connections_desc", nvarchar60Catalog, 60, true),
            new("secondary_role_allow_connections", SqlType.TinyInt, null, true),
            new("secondary_role_allow_connections_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, true),
            new("modify_date", SqlType.DateTime, null, true),
            new("backup_priority", SqlType.Int32, null, true),
            new("read_only_routing_url", SqlType.NVarchar, 256, true),
            new("seeding_mode", SqlType.TinyInt, null, true),
            new("seeding_mode_desc", nvarchar60Catalog, 60, true),
            new("read_write_routing_url", SqlType.NVarchar, 256, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.availability_groups: server-scope AlwaysOn catalog, always empty
        // (no AGs configured). SSMS's enumeration does
        // `insert into #tmp select group_id, name from
        // master.sys.availability_groups`.
        Sys("availability_groups",
        [
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("name", SqlType.NVarchar, 128, true),
            new("resource_id", SqlType.NVarchar, 40, true),
            new("resource_group_id", SqlType.NVarchar, 40, true),
            new("failure_condition_level", SqlType.Int32, null, true),
            new("health_check_timeout", SqlType.Int32, null, true),
            new("automated_backup_preference", SqlType.TinyInt, null, true),
            new("automated_backup_preference_desc", nvarchar60Catalog, 60, true),
            new("version", SqlType.SmallInt, null, true),
            new("basic_features", SqlType.Bit, null, true),
            new("dtc_support", SqlType.Bit, null, true),
            new("db_failover", SqlType.Bit, null, true),
            new("is_distributed", SqlType.Bit, null, true),
            new("cluster_type", SqlType.TinyInt, null, true),
            new("cluster_type_desc", nvarchar60Catalog, 60, true),
            new("required_synchronized_secondaries_to_commit", SqlType.Int32, null, true),
            new("sequence_number", SqlType.BigInt, null, true),
            new("is_contained", SqlType.Bit, null, true),
            new("cluster_connection_options", SqlType.NVarchar, 4000, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.dm_hadr_cluster: single-row failover-clustering DMV. Probe-
        // confirmed against a non-clustered SQL Server 2025: even with no
        // cluster the view returns ONE row — empty cluster_name,
        // quorum_type 0 / NODE_MAJORITY, quorum_state 1 / NORMAL_QUORUM —
        // and SSMS's Select-Top-1000 server-properties batch reads it inside
        // a TRY/CATCH that tolerates only permission errors, so an empty
        // view (or Msg 208) escapes as a THROW.
        Sys("dm_hadr_cluster",
        [
            new("cluster_name", SqlType.NVarchar, 256, false),
            new("quorum_type", SqlType.TinyInt, null, false),
            new("quorum_type_desc", nvarchar60Catalog, 60, false),
            new("quorum_state", SqlType.TinyInt, null, false),
            new("quorum_state_desc", nvarchar60Catalog, 60, false),
        ], static (_, _) => DmHadrClusterRows);

        // sys.dm_hadr_database_replica_states: server-scope AlwaysOn DMV,
        // always empty (no AGs). SSMS's enumeration does
        // `insert into #tmp select group_database_id, synchronization_state,
        // is_local, group_id, database_id from
        // master.sys.dm_hadr_database_replica_states`. LSN columns are
        // numeric(25, 0).
        Sys("dm_hadr_database_replica_states",
        [
            new("database_id", SqlType.Int32, null, false),
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("replica_id", SqlType.UniqueIdentifier, null, false),
            new("group_database_id", SqlType.UniqueIdentifier, null, false),
            new("is_local", SqlType.Bit, null, true),
            new("is_primary_replica", SqlType.Bit, null, true),
            new("synchronization_state", SqlType.TinyInt, null, true),
            new("synchronization_state_desc", nvarchar60Catalog, 60, true),
            new("is_commit_participant", SqlType.Bit, null, true),
            new("synchronization_health", SqlType.TinyInt, null, true),
            new("synchronization_health_desc", nvarchar60Catalog, 60, true),
            new("database_state", SqlType.TinyInt, null, true),
            new("database_state_desc", nvarchar60Catalog, 60, true),
            new("is_suspended", SqlType.Bit, null, true),
            new("suspend_reason", SqlType.TinyInt, null, true),
            new("suspend_reason_desc", nvarchar60Catalog, 60, true),
            new("recovery_lsn", lsnNumeric, null, true),
            new("truncation_lsn", lsnNumeric, null, true),
            new("last_sent_lsn", lsnNumeric, null, true),
            new("last_sent_time", SqlType.DateTime, null, true),
            new("last_received_lsn", lsnNumeric, null, true),
            new("last_received_time", SqlType.DateTime, null, true),
            new("last_hardened_lsn", lsnNumeric, null, true),
            new("last_hardened_time", SqlType.DateTime, null, true),
            new("last_redone_lsn", lsnNumeric, null, true),
            new("last_redone_time", SqlType.DateTime, null, true),
            new("log_send_queue_size", SqlType.BigInt, null, true),
            new("log_send_rate", SqlType.BigInt, null, true),
            new("redo_queue_size", SqlType.BigInt, null, true),
            new("redo_rate", SqlType.BigInt, null, true),
            new("filestream_send_rate", SqlType.BigInt, null, true),
            new("end_of_log_lsn", lsnNumeric, null, true),
            new("last_commit_lsn", lsnNumeric, null, true),
            new("last_commit_time", SqlType.DateTime, null, true),
            new("low_water_mark_for_ghosts", SqlType.BigInt, null, true),
            new("secondary_lag_seconds", SqlType.BigInt, null, true),
            new("quorum_commit_lsn", lsnNumeric, null, true),
            new("quorum_commit_time", SqlType.DateTime, null, true),
            new("is_internal", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.master_files: one data file (type 0, ROWS) + one log file
        // (type 1, LOG) per database, join key database_id. SSMS probes for
        // in-memory-OLTP filegroups via
        // `... from master.sys.master_files mf ... where mf.[type] = 2`, which
        // must return nothing — the simulator emits no type-2 (FILESTREAM /
        // memory-optimized) files. File contents are synthetic: logical name
        // `<db>_Data` / `<db>_Log`, a plausible physical path, a small page
        // count, unlimited max_size, 64 MB growth. All LSN columns numeric(25, 0),
        // surfaced NULL (no physical log).
        Sys("master_files",
        [
            new("database_id", SqlType.Int32, null, false),
            new("file_id", SqlType.Int32, null, false),
            new("file_guid", SqlType.UniqueIdentifier, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 128, true),
            new("physical_name", SqlType.NVarchar, 260, false),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("size", SqlType.Int32, null, false),
            new("max_size", SqlType.Int32, null, false),
            new("growth", SqlType.Int32, null, false),
            new("is_media_read_only", SqlType.Bit, null, false),
            new("is_read_only", SqlType.Bit, null, false),
            new("is_sparse", SqlType.Bit, null, false),
            new("is_percent_growth", SqlType.Bit, null, false),
            new("is_name_reserved", SqlType.Bit, null, false),
            new("is_persistent_log_buffer", SqlType.Bit, null, false),
            new("create_lsn", lsnNumeric, null, true),
            new("drop_lsn", lsnNumeric, null, true),
            new("read_only_lsn", lsnNumeric, null, true),
            new("read_write_lsn", lsnNumeric, null, true),
            new("differential_base_lsn", lsnNumeric, null, true),
            new("differential_base_guid", SqlType.UniqueIdentifier, null, true),
            new("differential_base_time", SqlType.DateTime, null, true),
            new("redo_start_lsn", lsnNumeric, null, true),
            new("redo_start_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("redo_target_lsn", lsnNumeric, null, true),
            new("redo_target_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("backup_lsn", lsnNumeric, null, true),
            new("credential_id", SqlType.Int32, null, true),
        ], EnumerateSysMasterFiles);

        // sys.database_files: the current-database view over master_files — one
        // data file (file_id 1, type 0 ROWS) + one log file (file_id 2, type 1
        // LOG). The join key is the database context (the resolved
        // `database`), so a three-part `master.sys.database_files` read (SSMS
        // reads it to derive the master data/log directory) returns master's
        // two files. Names / file_ids / types agree with sys.master_files
        // (`<db>_Data` / `<db>_Log`); real SQL Server has no database_id column
        // here (implicitly the current database), so it is omitted.
        Sys("database_files",
        [
            new("file_id", SqlType.Int32, null, false),
            new("file_guid", SqlType.UniqueIdentifier, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 128, true),
            new("physical_name", SqlType.NVarchar, 260, false),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("size", SqlType.Int32, null, false),
            new("max_size", SqlType.Int32, null, false),
            new("growth", SqlType.Int32, null, false),
            new("is_media_read_only", SqlType.Bit, null, false),
            new("is_read_only", SqlType.Bit, null, false),
            new("is_sparse", SqlType.Bit, null, false),
            new("is_percent_growth", SqlType.Bit, null, false),
            new("is_name_reserved", SqlType.Bit, null, false),
            // drop_lsn: NULL for every live file (the simulator never drops
            // one). SSMS's FileGroup→Files enumeration filters on
            // `df.drop_lsn is null`, so the column must resolve — sys.master_files
            // already carries it; database_files was the missing sibling.
            new("drop_lsn", lsnNumeric, null, true),
        ], EnumerateSysDatabaseFiles);

        // sys.database_query_store_options: per-database view (join key is the
        // current database context, not a database_id column). Query Store is
        // never enabled in the simulator, so a user database returns exactly
        // one OFF row and a system database (master/tempdb/model/msdb) returns
        // zero rows — the exact split a live SQL Server 2025 returns
        // (probe-confirmed 2026-07-15). SSMS's Query Store probe gates on
        // OBJECT_ID(N'[sys].[database_query_store_options]') resolving and then
        // reads actual_state. nvarchar(60) _desc columns carry the probed
        // max_length=120 bytes; actual_state_additional_info is nvarchar(4000)
        // (probed max_length=8000 bytes), surfaced as the empty string.
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

        // sys.query_store_runtime_stats: per-plan runtime-statistics capture.
        // The simulator never runs Query Store, so no runtime stats are ever
        // captured and the view is always empty. SSMS's Query Store probe does
        // IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats), which
        // must resolve and return zero rows. Column shape probe-confirmed
        // against SQL Server 2025 (2026-07-15): the first nine metric groups
        // carry NOT NULL last/min/max columns, the last four NULL.
        Sys("query_store_runtime_stats",
            BuildQueryStoreRuntimeStatsColumns(nvarchar60Catalog),
            static (_, _) => EmptyCatalogRows);
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
    /// The single row projected by <c>sys.dm_os_host_info</c>. Materialized
    /// once at first access — the host operating system, architecture, and
    /// distribution can't change during the process lifetime, so the row is
    /// shared across every read (matching how the constant catalog-view cells
    /// elsewhere are reused).
    /// </summary>
    private static readonly SqlValue[][] DmOsHostInfoRows = [BuildDmOsHostInfoRow()];

    /// <summary>
    /// The single <c>sys.dm_hadr_cluster</c> row — a non-clustered
    /// instance's values, probe-confirmed against SQL Server 2025.
    /// </summary>
    private static readonly SqlValue[][] DmHadrClusterRows =
    [
        [
            SqlValue.FromNVarchar(string.Empty),
            SqlValue.FromByte(0),
            SqlValue.FromString(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit), "NODE_MAJORITY"),
            SqlValue.FromByte(1),
            SqlValue.FromString(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit), "NORMAL_QUORUM"),
        ],
    ];

    private static SqlValue[] BuildDmOsHostInfoRow()
    {
        string platform, distribution, release;
        SqlValue sku;
        if (OperatingSystem.IsWindows())
        {
            platform = "Windows";
            distribution = "Windows";
            var version = Environment.OSVersion.Version;
            release = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");
            sku = SqlValue.FromInt32(48);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Real SQL Server never runs on macOS; report the OS honestly
            // rather than mislabeling it 'Linux'. host_sku is NULL as on Linux.
            platform = "macOS";
            distribution = "macOS";
            release = "";
            sku = SqlValue.Null(SqlType.Int32);
        }
        else
        {
            platform = "Linux";
            var osRelease = ReadOsRelease();
            distribution = osRelease.TryGetValue("NAME", out var name) && name.Length > 0 ? name : "Linux";
            release = osRelease.TryGetValue("VERSION_ID", out var versionId) ? versionId : "";
            sku = SqlValue.Null(SqlType.Int32);
        }

        var architecture = RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant();
        return
        [
            SqlValue.FromNVarchar(platform),
            SqlValue.FromNVarchar(distribution),
            SqlValue.FromNVarchar(release),
            SqlValue.FromNVarchar(""),
            sku,
            SqlValue.FromInt32(1033),
            SqlValue.FromNVarchar(architecture),
        ];
    }

    /// <summary>
    /// Parses the <c>KEY=value</c> pairs of <c>/etc/os-release</c>, stripping a
    /// single layer of surrounding double quotes from each value. Any file-
    /// access failure yields an empty map so callers fall back to defaults —
    /// this must never throw, since it runs during static initialization.
    /// </summary>
    private static Dictionary<string, string> ReadOsRelease()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                    continue;
                var key = line[..separator];
                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                result[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>
    /// Raw stock-instance rows for <c>sys.configurations</c> (probe-confirmed
    /// against SQL Server 2025). <c>configuration_id</c> and <c>name</c> are
    /// stable across instances; <c>value</c> mirrors <c>value_in_use</c> on a
    /// fresh server. The four sql_variant columns wrap an <c>int</c> inner for
    /// every option. These are the defaults an option reports until
    /// <c>sp_configure</c> writes it — see <c>Simulation.ServerConfiguration</c>
    /// for the per-simulation overrides layered on top.
    /// </summary>
    internal static readonly (int Id, string Name, int Value, int Minimum, int Maximum, int ValueInUse, string Description, bool IsDynamic, bool IsAdvanced)[] ConfigurationData =
    [
        (101, "recovery interval (min)", 0, 0, 32767, 0, "Maximum recovery interval in minutes", true, true),
        (102, "allow updates", 0, 0, 1, 0, "Allow updates to system tables", true, false),
        (103, "user connections", 0, 0, 32767, 0, "Number of user connections allowed", false, true),
        (106, "locks", 0, 5000, 2147483647, 0, "Number of locks for all users", false, true),
        (107, "open objects", 0, 0, 2147483647, 0, "Number of open database objects", false, true),
        (109, "fill factor (%)", 0, 0, 100, 0, "Default fill factor percentage", false, true),
        (114, "disallow results from triggers", 0, 0, 1, 0, "Disallow returning results from triggers", true, true),
        (115, "nested triggers", 1, 0, 1, 1, "Allow triggers to be invoked within triggers", true, false),
        (116, "server trigger recursion", 1, 0, 1, 1, "Allow recursion for server level triggers", true, false),
        (117, "remote access", 1, 0, 1, 1, "Allow remote access", false, false),
        (124, "default language", 0, 0, 9999, 0, "default language", true, false),
        (400, "cross db ownership chaining", 0, 0, 1, 0, "Allow cross db ownership chaining", true, false),
        (503, "max worker threads", 0, 128, 65535, 0, "Maximum worker threads", true, true),
        (505, "network packet size (B)", 4096, 512, 32767, 4096, "Network packet size", true, true),
        (518, "show advanced options", 0, 0, 1, 0, "show advanced options", true, false),
        (542, "remote proc trans", 0, 0, 1, 0, "Create DTC transaction for remote procedures", true, false),
        (544, "c2 audit mode", 0, 0, 1, 0, "c2 audit mode", false, true),
        (1126, "default full-text language", 1033, 0, 2147483647, 1033, "default full-text language", true, true),
        (1127, "two digit year cutoff", 2049, 1753, 9999, 2049, "two digit year cutoff", true, true),
        (1505, "index create memory (KB)", 0, 704, 2147483647, 0, "Memory for index create sorts (kBytes)", true, true),
        (1517, "priority boost", 0, 0, 1, 0, "Priority boost", false, true),
        (1519, "remote login timeout (s)", 10, 0, 2147483647, 10, "remote login timeout", true, false),
        (1520, "remote query timeout (s)", 600, 0, 2147483647, 600, "remote query timeout", true, false),
        (1531, "cursor threshold", -1, -1, 2147483647, -1, "cursor threshold", true, true),
        (1532, "set working set size", 0, 0, 1, 0, "set working set size", false, true),
        (1534, "user options", 0, 0, 32767, 0, "user options", true, false),
        (1535, "affinity mask", 0, -2147483648, 2147483647, 0, "affinity mask", true, true),
        (1536, "max text repl size (B)", 65536, -1, 2147483647, 65536, "Maximum size of a text field in replication.", true, false),
        (1537, "media retention", 0, 0, 365, 0, "Tape retention period in days", true, true),
        (1538, "cost threshold for parallelism", 5, 0, 32767, 5, "cost threshold for parallelism", true, true),
        (1539, "max degree of parallelism", 8, 0, 32767, 8, "maximum degree of parallelism", true, true),
        (1540, "min memory per query (KB)", 1024, 512, 2147483647, 1024, "minimum memory per query (kBytes)", true, true),
        (1541, "query wait (s)", -1, -1, 2147483647, -1, "maximum time to wait for query memory (s)", true, true),
        (1543, "min server memory (MB)", 0, 0, 2147483647, 16, "Minimum size of server memory (MB)", true, true),
        (1544, "max server memory (MB)", 4096, 128, 2147483647, 4096, "Maximum size of server memory (MB)", true, true),
        (1545, "query governor cost limit", 0, 0, 2147483647, 0, "Maximum estimated cost allowed by query governor", true, true),
        (1546, "lightweight pooling", 0, 0, 1, 0, "User mode scheduler uses lightweight pooling", false, true),
        (1547, "scan for startup procs", 0, 0, 1, 0, "scan for startup stored procedures", false, true),
        (1549, "affinity64 mask", 0, -2147483648, 2147483647, 0, "affinity64 mask", true, true),
        (1550, "affinity I/O mask", 0, -2147483648, 2147483647, 0, "affinity I/O mask", false, true),
        (1551, "affinity64 I/O mask", 0, -2147483648, 2147483647, 0, "affinity64 I/O mask", false, true),
        (1555, "transform noise words", 0, 0, 1, 0, "Transform noise words for full-text query", true, true),
        (1556, "precompute rank", 0, 0, 1, 0, "Use precomputed rank for full-text query", true, true),
        (1557, "PH timeout (s)", 60, 1, 3600, 60, "DB connection timeout for full-text protocol handler (s)", true, true),
        (1562, "clr enabled", 0, 0, 1, 0, "CLR user code execution enabled in the server", true, false),
        (1563, "max full-text crawl range", 4, 0, 256, 4, "Maximum  crawl ranges allowed in full-text indexing", true, true),
        (1564, "ft notify bandwidth (min)", 0, 0, 32767, 0, "Number of reserved full-text notifications buffers", true, true),
        (1565, "ft notify bandwidth (max)", 100, 0, 32767, 100, "Max number of full-text notifications buffers", true, true),
        (1566, "ft crawl bandwidth (min)", 0, 0, 32767, 0, "Number of reserved full-text crawl buffers", true, true),
        (1567, "ft crawl bandwidth (max)", 100, 0, 32767, 100, "Max number of full-text crawl buffers", true, true),
        (1568, "default trace enabled", 1, 0, 1, 1, "Enable or disable the default trace", true, true),
        (1569, "blocked process threshold (s)", 0, 0, 86400, 0, "Blocked process reporting threshold", true, true),
        (1570, "in-doubt xact resolution", 0, 0, 2, 0, "Recovery policy for DTC transactions with unknown outcome", true, true),
        (1576, "remote admin connections", 0, 0, 1, 0, "Dedicated Admin Connections are allowed from remote clients", true, false),
        (1577, "common criteria compliance enabled", 0, 0, 1, 0, "Common Criteria compliance mode enabled", false, true),
        (1578, "EKM provider enabled", 0, 0, 1, 0, "Enable or disable EKM provider", true, true),
        (1579, "backup compression default", 0, 0, 1, 0, "Enable compression of backups by default", true, false),
        (1580, "filestream access level", 0, 0, 2, 0, "Sets the FILESTREAM access level", true, false),
        (1581, "optimize for ad hoc workloads", 0, 0, 1, 0, "When this option is set, plan cache size is further reduced for single-use adhoc OLTP workload.", true, true),
        (1582, "access check cache bucket count", 0, 0, 65536, 0, "Default hash bucket count for the access check result security cache", true, true),
        (1583, "access check cache quota", 0, 0, 2147483647, 0, "Default quota for the access check result security cache", true, true),
        (1584, "backup checksum default", 0, 0, 1, 0, "Enable checksum of backups by default", true, false),
        (1585, "automatic soft-NUMA disabled", 0, 0, 1, 0, "Automatic soft-NUMA is enabled by default", false, true),
        (1586, "external scripts enabled", 0, 0, 1, 0, "Allows execution of external scripts", true, false),
        (1587, "clr strict security", 1, 0, 1, 1, "CLR strict security enabled in the server", true, true),
        (1588, "column encryption enclave type", 0, 0, 2, 0, "Type of enclave used for computations on encrypted columns", false, false),
        (1589, "tempdb metadata memory-optimized", 0, 0, 1, 0, "Tempdb metadata memory-optimized is disabled by default.", false, true),
        (1591, "ADR cleaner retry timeout (min)", 15, 0, 32767, 15, "ADR cleaner retry timeout.", true, true),
        (1592, "ADR Preallocation Factor", 4, 0, 32767, 4, "ADR Preallocation Factor.", true, true),
        (1593, "version high part of SQL Server", 1114112, -2147483648, 2147483647, 1114112, "version high part of SQL Server that model database copied for", true, true),
        (1594, "version low part of SQL Server", 73072641, -2147483648, 2147483647, 73072641, "version low part of SQL Server that model database copied for", true, true),
        (1595, "Data processed daily limit in TB", 2147483647, 0, 2147483647, 2147483647, "SQL On-demand data processed daily limit in TB", true, false),
        (1596, "Data processed weekly limit in TB", 2147483647, 0, 2147483647, 2147483647, "SQL On-demand data processed weekly limit in TB", true, false),
        (1597, "Data processed monthly limit in TB", 2147483647, 0, 2147483647, 2147483647, "SQL On-demand data processed monthly limit in TB", true, false),
        (1598, "ADR Cleaner Thread Count", 1, 1, 32767, 1, "Max number of threads ADR cleaner can assign.", true, true),
        (1599, "hardware offload enabled", 0, 0, 1, 0, "Enable hardware offloading on the server", false, true),
        (1600, "hardware offload config", 0, 0, 255, 0, "Configure hardware offload accelerator", false, true),
        (1601, "hardware offload mode", 0, 0, 255, 0, "Configure hardware offload accelerator mode", false, true),
        (1602, "backup compression algorithm", 0, 0, 3, 0, "Configure default backup compression algorithm", true, false),
        (1603, "ADR cleaner lock timeout (s)", 5, 1, 32767, 5, "ADR cleaner lock timeout", true, true),
        (1606, "SLOG memory quota (%)", 75, 1, 100, 75, "SLOG memory quota percentage", true, true),
        (1609, "max RPC request params (KB)", 0, 0, 2147483647, 0, "Maximum memory for RPC request parameters (kBytes)", true, true),
        (1610, "max UCS send boxcars", 256, 256, 2048, 256, "Maximum number of UCS boxcars for sending messages.", false, true),
        (1611, "availability group commit time (ms)", 0, 0, 10, 0, "Configure availability group commit time in milliseconds for SQL Server only.", true, true),
        (1612, "tiered memory enabled", 0, 0, 1, 0, "tiered memory memory-optimized is disabled by default.", false, true),
        (1613, "max server tiered memory (MB)", 2147483647, 0, 2147483647, 2147483647, "Maximum size of server tiered memory (MB)", false, true),
        (16384, "Agent XPs", 0, 0, 1, 0, "Enable or disable Agent XPs", true, true),
        (16386, "Database Mail XPs", 0, 0, 1, 0, "Enable or disable Database Mail XPs", true, true),
        (16387, "SMO and DMO XPs", 1, 0, 1, 1, "Enable or disable SMO and DMO XPs", true, true),
        (16388, "Ole Automation Procedures", 0, 0, 1, 0, "Enable or disable Ole Automation Procedures", true, true),
        (16390, "xp_cmdshell", 0, 0, 1, 0, "Enable or disable command shell", true, true),
        (16391, "Ad Hoc Distributed Queries", 0, 0, 1, 0, "Enable or disable Ad Hoc Distributed Queries", true, true),
        (16392, "Replication XPs", 0, 0, 1, 0, "Enable or disable Replication XPs", true, true),
        (16393, "contained database authentication", 0, 0, 1, 0, "Enables contained databases and contained authentication", true, false),
        (16394, "hadoop connectivity", 0, 0, 8, 0, "Configure SQL Server to connect to external Hadoop or Microsoft Azure storage blob data sources through PolyBase", true, false),
        (16395, "polybase network encryption", 1, 0, 1, 1, "Configure SQL Server to encrypt control and data channels when using PolyBase", true, false),
        (16396, "remote data archive", 0, 0, 1, 0, "Allow the use of the REMOTE_DATA_ARCHIVE data access for databases", true, false),
        (16397, "allow polybase export", 0, 0, 1, 0, "Allows writing into an external table using PolyBase", true, false),
        (16398, "allow filesystem enumeration", 1, 0, 1, 1, "Allow enumeration of filesystem", true, true),
        (16399, "polybase enabled", 0, 0, 1, 0, "Configure SQL Server to connect to external data sources through PolyBase", true, false),
        (16400, "suppress recovery model errors", 0, 0, 1, 0, "Return warning instead of error for unsupported ALTER DATABASE SET RECOVERY command", true, true),
        (16401, "openrowset auto_create_statistics", 1, 0, 1, 1, "Enable or disable auto create statistics for openrowset sources.", true, true),
        (16402, "external rest endpoint enabled", 0, 0, 1, 0, "Enable or disable invocations of external REST endpoints", true, false),
        (16403, "external xtp dll gen util enabled", 0, 0, 1, 0, "Enable or disable using external xtp dll generation via HkDllGen.exe", true, false),
        (16404, "external AI runtimes enabled", 0, 0, 1, 0, "Enable or disable using external AI runtimes", true, false),
        (16405, "allow server scoped db credentials", 0, 0, 1, 0, "Enable or disable use of server managed identity in database scoped credentials", true, false),
    ];

    /// <summary>
    /// The 106 rows projected by <c>sys.configurations</c>, materialized once
    /// from <see cref="ConfigurationData"/> since server-configuration
    /// metadata is fixed static catalog data (matching how the other
    /// constant-row catalog views reuse a shared array). Independent of the
    /// database argument — <c>sys.configurations</c> is server-scoped.
    /// </summary>
    private static readonly SqlValue[][] ConfigurationsRows = BuildConfigurationsRows();

    /// <summary>
    /// <c>sys.configurations</c> for one batch: the stock defaults with the
    /// simulation's <c>sp_configure</c> writes layered on top.
    /// </summary>
    /// <remarks>
    /// mssql-django's <c>enable_clr()</c> reads <c>clr enabled</c> here and only
    /// falls through to <c>sp_configure</c> when it is 0, so tracking the opt-in
    /// keeps that path from needing a configuration-write model.
    /// </remarks>
    private static SqlValue[][] ConfigurationRowsFor(Parser.BatchContext batch)
    {
        var simulation = batch.Connection.Simulation;
        if (!simulation.EnableClr && simulation.ServerConfiguration.IsEmpty)
            return ConfigurationsRows;

        var rows = (SqlValue[][])ConfigurationsRows.Clone();
        for (var i = 0; i < rows.Length; i++)
        {
            var (configured, inUse) = EffectiveConfigurationValues(simulation, i);
            if (configured == ConfigurationData[i].Value && inUse == ConfigurationData[i].ValueInUse)
                continue;

            var row = (SqlValue[])rows[i].Clone();
            row[2] = SqlValue.FromVariant(SqlValue.FromInt32(configured));
            row[5] = SqlValue.FromVariant(SqlValue.FromInt32(inUse));
            rows[i] = row;
        }

        return rows;
    }

    /// <summary>
    /// The <c>config_value</c> / <c>run_value</c> pair one configuration option
    /// currently reports: whatever <c>sp_configure</c> staged and
    /// <c>RECONFIGURE</c> installed, else the stock default.
    /// <para>
    /// The two CLR rows are the exception, and report the simulation's
    /// <see cref="Simulation.EnableClr"/> opt-in whatever <c>sp_configure</c>
    /// wrote: <c>clr enabled</c> mirrors the opt-in and <c>clr strict
    /// security</c> drops to 0 once CLR is on, because the simulator gates
    /// assembly registration on the host opt-in rather than on assembly
    /// signing — reporting real's default of 1 would describe an enforcement it
    /// does not perform.
    /// </para>
    /// </summary>
    internal static (int Configured, int InUse) EffectiveConfigurationValues(Simulation simulation, int index)
    {
        var (id, name, value, _, _, valueInUse, _, _, _) = ConfigurationData[index];
        if (name is "clr enabled" or "clr strict security")
        {
            var clr = !simulation.EnableClr ? value
                : name == "clr enabled" ? 1
                : 0;
            return (clr, clr);
        }

        return simulation.ServerConfiguration.TryGetValue(id, out var setting)
            ? setting
            : (value, valueInUse);
    }

    private static SqlValue[][] BuildConfigurationsRows()
    {
        var rows = new SqlValue[ConfigurationData.Length][];
        for (var i = 0; i < ConfigurationData.Length; i++)
        {
            var (id, name, value, minimum, maximum, valueInUse, description, isDynamic, isAdvanced) = ConfigurationData[i];
            rows[i] =
            [
                SqlValue.FromInt32(id),
                SqlValue.FromNVarchar(name),
                SqlValue.FromVariant(SqlValue.FromInt32(value)),
                SqlValue.FromVariant(SqlValue.FromInt32(minimum)),
                SqlValue.FromVariant(SqlValue.FromInt32(maximum)),
                SqlValue.FromVariant(SqlValue.FromInt32(valueInUse)),
                SqlValue.FromNVarchar(description),
                SqlValue.FromBoolean(isDynamic),
                SqlValue.FromBoolean(isAdvanced),
            ];
        }

        return rows;
    }

    /// <summary>
    /// Static <c>sys.database_scoped_configurations</c> rows — the fresh-database
    /// defaults for the knobs SMO's Script-As preamble reads. The simulator
    /// doesn't track <c>ALTER DATABASE SCOPED CONFIGURATION</c> changes, so the
    /// set is fixed. <c>value</c> is <c>sql_variant</c> carrying each knob's
    /// real inner base type (MAXDOP <c>int</c>; the remaining knobs <c>bit</c>,
    /// probe-confirmed against SQL Server 2025); <c>value_for_secondary</c> is a
    /// variant NULL (no secondary replica). configuration_id values match SQL
    /// Server 2025's assignment.
    /// </summary>
    private static readonly SqlValue[][] DatabaseScopedConfigurationRows = BuildDatabaseScopedConfigurationRows();

    private static SqlValue[][] BuildDatabaseScopedConfigurationRows()
    {
        (int Id, string Name, SqlValue Value)[] data =
        [
            (1, "MAXDOP", SqlValue.FromVariant(SqlValue.FromInt32(0))),
            (2, "LEGACY_CARDINALITY_ESTIMATION", SqlValue.FromVariant(SqlValue.FromBoolean(false))),
            (3, "PARAMETER_SNIFFING", SqlValue.FromVariant(SqlValue.FromBoolean(true))),
            (4, "QUERY_OPTIMIZER_HOTFIXES", SqlValue.FromVariant(SqlValue.FromBoolean(false))),
        ];
        var nullValue = SqlValue.Null(SqlType.SqlVariant);
        var isDefault = SqlValue.FromBoolean(true);
        var rows = new SqlValue[data.Length][];
        for (var i = 0; i < data.Length; i++)
        {
            rows[i] =
            [
                SqlValue.FromInt32(data[i].Id),
                SqlValue.FromSystemName(data[i].Name),
                data[i].Value,
                nullValue,
                isDefault,
            ];
        }

        return rows;
    }

    /// <summary>
    /// Fixed <c>create_date</c> seed for <c>sys.databases</c> rows — the
    /// simulator doesn't track per-database creation timestamps, so every
    /// row reports this constant (matching real SQL Server's non-null,
    /// datetime-typed column).
    /// </summary>
    internal static readonly DateTime SysDatabasesCreateDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// Fixed <c>service_broker_guid</c> for every <c>sys.databases</c> row.
    /// Service Broker isn't modeled; the column is non-null in real SQL
    /// Server, so a stable constant stands in.
    /// </summary>
    private static readonly Guid SysDatabasesBrokerGuid = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Rows for <c>sys.databases</c>. One row per <see cref="Database"/>
    /// hosted by the connected <see cref="Simulation"/>; matches real SQL
    /// Server's "instance-scoped catalog view" semantic. Full 98-column
    /// projection: modeled columns read live <see cref="Database"/> state
    /// (name / database_id / compatibility_level / collation_name /
    /// snapshot-isolation trio / recovery_model / physical_database_name),
    /// state is always <c>0 / ONLINE</c>, and the remaining option-flag
    /// columns carry the stock defaults a freshly created user database
    /// reports on SQL Server 2025 (user_access MULTI_USER, page_verify
    /// CHECKSUM, containment NONE, log_reuse_wait NOTHING, delayed_durability
    /// DISABLED, catalog_collation DATABASE_DEFAULT). recovery_model is
    /// SIMPLE for <c>master</c> / <c>tempdb</c> / <c>msdb</c> and FULL for
    /// <c>model</c> and every user database, which inherits the template's,
    /// and <c>is_broker_enabled</c> is 1 everywhere but <c>master</c> and
    /// <c>model</c> (both probe-confirmed). Code↔desc pairs are always
    /// internally consistent.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabases(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var zeroByte = SqlValue.FromByte(0);
        var ownerSid = SqlValue.FromVarbinary([0x01]);
        var createDate = SqlValue.FromDateTime(SysDatabasesCreateDate);
        var brokerGuid = SqlValue.FromGuid(SysDatabasesBrokerGuid);
        var multiUser = SqlValue.FromNVarchar("MULTI_USER");
        var online = SqlValue.FromNVarchar("ONLINE");
        var checksum = SqlValue.FromNVarchar("CHECKSUM");
        var nothing = SqlValue.FromNVarchar("NOTHING");
        var none = SqlValue.FromNVarchar("NONE");
        var disabled = SqlValue.FromNVarchar("DISABLED");
        var databaseDefault = SqlValue.FromNVarchar("DATABASE_DEFAULT");
        var unsupported = SqlValue.FromNVarchar("UNSUPPORTED");
        var recoveryTime = SqlValue.FromInt32(60);
        var zeroInt = SqlValue.FromInt32(0);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullSmallInt = SqlValue.Null(SqlType.SmallInt);
        var nullBit = SqlValue.Null(SqlType.Bit);
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullName = SqlValue.Null(SqlType.NVarchar);

        // Ordered by database_id via DatabasesWithIds (master = 1, system
        // databases 2-4, user databases from 5) — matching real SQL Server's
        // sys.databases ordering by database_id.
        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            var snapshotOn = db.AllowSnapshotIsolation;
            // Service Broker is enabled everywhere but master and model
            // (probe-confirmed: tempdb, msdb and a freshly created user
            // database all read 1). Broker itself isn't modeled — this is the
            // flag alone.
            var isBrokerEnabled = !Collation.Baseline.Equals(db.Name, "master")
                && !Collation.Baseline.Equals(db.Name, "model");
            yield return [
                SqlValue.FromSystemName(db.Name),
                SqlValue.FromInt32(id),
                nullInt,
                ownerSid,
                createDate,
                SqlValue.FromByte((byte)db.CompatibilityLevel),
                SqlValue.FromSystemName(db.CollationName),
                zeroByte,
                multiUser,
                SqlValue.FromBoolean(db.IsReadOnly), // is_read_only
                falseBit,
                falseBit,
                zeroByte,
                online,
                falseBit,
                falseBit,
                falseBit,
                SqlValue.FromByte((byte)(snapshotOn ? 1 : 0)),
                SqlValue.FromNVarchar(snapshotOn ? "ON" : "OFF"),
                SqlValue.FromBoolean(db.ReadCommittedSnapshot),
                SqlValue.FromByte((byte)db.RecoveryModel),
                SqlValue.FromNVarchar(db.RecoveryModel switch
                {
                    RecoveryModel.Simple => "SIMPLE",
                    RecoveryModel.BulkLogged => "BULK_LOGGED",
                    _ => "FULL",
                }),
                SqlValue.FromByte(2),
                checksum,
                trueBit,  // is_auto_create_stats_on
                falseBit, // is_auto_create_stats_incremental_on
                trueBit,  // is_auto_update_stats_on
                falseBit, // is_auto_update_stats_async_on
                falseBit, // is_ansi_null_default_on
                falseBit, // is_ansi_nulls_on
                falseBit, // is_ansi_padding_on
                falseBit, // is_ansi_warnings_on
                falseBit, // is_arithabort_on
                falseBit, // is_concat_null_yields_null_on
                falseBit, // is_numeric_roundabort_on
                falseBit, // is_quoted_identifier_on
                SqlValue.FromBoolean(db.RecursiveTriggers),
                falseBit, // is_cursor_close_on_commit_on
                falseBit, // is_local_cursor_default
                trueBit,  // is_fulltext_enabled
                SqlValue.FromBoolean(db.Trustworthy),
                SqlValue.FromBoolean(db.CrossDatabaseChaining),
                falseBit, // is_parameterization_forced
                falseBit, // is_master_key_encrypted_by_server
                // is_query_store_on stays 0 so it agrees with the OFF row
                // sys.database_query_store_options projects; see that
                // generator for why Query Store reads disabled.
                falseBit, // is_query_store_on
                falseBit, // is_published
                falseBit, // is_subscribed
                falseBit, // is_merge_published
                falseBit, // is_distributor
                falseBit, // is_sync_with_backup
                brokerGuid,
                SqlValue.FromBoolean(isBrokerEnabled),
                zeroByte,
                nothing,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                nullGuid,
                nullGuid,
                nullInt,
                nullSmallInt,
                nullName,
                nullInt,
                nullName,
                nullBit,
                nullBit,
                nullSmallInt,
                zeroByte,
                none,
                recoveryTime,
                zeroInt,
                disabled,
                falseBit, // is_memory_optimized_elevate_to_snapshot_on
                falseBit, // is_federation_member
                falseBit, // is_remote_data_archive_enabled
                falseBit, // is_mixed_page_allocation_on
                trueBit,  // is_temporal_history_retention_enabled
                zeroInt,
                databaseDefault,
                SqlValue.FromNVarchar(db.Name),
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                trueBit,
                trueBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                zeroByte,
                unsupported,
                zeroByte,
                unsupported,
                falseBit,
                falseBit,
                falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.database_mirroring</c> — one per database (join key
    /// <c>database_id</c>, ordered via <see cref="Parser.Expressions.DbId.DatabasesWithIds"/>).
    /// The simulator never mirrors a database, so every <c>mirroring_*</c>
    /// column is NULL on every row, matching a live SQL Server 2025's
    /// non-mirrored shape. SSMS's Object-Explorer enumeration LEFT JOINs this
    /// to <c>sys.databases</c> on <c>database_id</c> and reads
    /// <c>ISNULL(mirroring_role, 0)</c> / <c>ISNULL(mirroring_state + 1, 0)</c>.
    /// </summary>
    /// <summary>
    /// Rows for <c>sys.dm_exec_sessions</c> — one per live connection on the
    /// simulation, snapshotted under the registry lock. Session-backed
    /// columns read the connection's real state; the rest are the
    /// probe-confirmed fresh-session defaults documented at the
    /// registration site.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDmExecSessions(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var simulation = batch.Connection.Simulation;
        var connections = simulation.SnapshotConnections();

        var emptyName = SqlValue.FromNVarchar(string.Empty);
        var nullName = SqlValue.Null(SqlType.NVarchar);
        var zero = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        var bitOn = SqlValue.FromBoolean(true);
        var bitOff = SqlValue.FromBoolean(false);
        var nullDateTime = SqlValue.Null(SqlType.DateTime);

        foreach (var connection in connections)
        {
            var loginTime = SqlValue.FromDateTime(connection.LoginTimeUtc);
            short databaseId = 1;
            foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
            {
                if (ReferenceEquals(db, connection.CurrentDatabase))
                {
                    databaseId = id;
                    break;
                }
            }
            var isolation = connection.SessionIsolationLevel switch
            {
                System.Data.IsolationLevel.ReadUncommitted => (short)1,
                System.Data.IsolationLevel.RepeatableRead => (short)3,
                System.Data.IsolationLevel.Serializable => (short)4,
                System.Data.IsolationLevel.Snapshot => (short)5,
                _ => (short)2,
            };
            var effectiveLogin = connection.Security.Effective.LoginName;
            var originalLogin = connection.Security.OriginalLoginName;
            yield return [
                SqlValue.FromInt16((short)connection.Spid),
                loginTime,
                connection.ClientHostName.Length == 0 ? emptyName : SqlValue.FromNVarchar(connection.ClientHostName),
                connection.ClientApplicationName.Length == 0 ? emptyName : SqlValue.FromNVarchar(connection.ClientApplicationName),
                SqlValue.FromInt32(Environment.ProcessId),
                SqlValue.FromInt32(7),
                SqlValue.FromNVarchar("SqlServerSimulator"),
                SqlValue.FromVarbinary(DeriveLoginSid(effectiveLogin)),
                SqlValue.FromNVarchar(effectiveLogin),
                nullName,
                nullName,
                SqlValue.FromString(NVarcharSqlType.Get(30, Collation.Catalog, Coercibility.Implicit), ReferenceEquals(connection, batch.Connection) ? "running" : "sleeping"),
                connection.ContextInfo is { } contextInfo ? SqlValue.FromVarbinary(contextInfo) : SqlValue.Null(SqlType.Varbinary),
                zero,
                zero,
                zero,
                zero,
                SqlValue.FromInt32(4),
                loginTime,
                loginTime,
                zeroBig,
                zeroBig,
                zeroBig,
                bitOn, // is_user_process
                SqlValue.FromInt32(connection.TextSize),
                SqlValue.FromNVarchar("us_english"),
                SqlValue.FromNVarchar("mdy"),
                SqlValue.FromInt16(7),
                connection.QuotedIdentifiers ? bitOn : bitOff,
                connection.Arithabort ? bitOn : bitOff,
                bitOn,  // ansi_null_dflt_on — SET ANSI_NULL_DFLT_ON/OFF is
                        // parse-and-discard, so no session field backs it
                bitOff, // ansi_defaults
                connection.AnsiWarnings ? bitOn : bitOff,
                connection.AnsiPadding ? bitOn : bitOff,
                connection.AnsiNulls ? bitOn : bitOff,
                connection.ConcatNullYieldsNull ? bitOn : bitOff,
                SqlValue.FromInt16(isolation),
                SqlValue.FromInt32(connection.LockTimeoutMillis),
                zero,
                SqlValue.FromInt64(connection.LastStatementRowCount),
                SqlValue.FromInt32(connection.LastErrorNumber),
                SqlValue.FromVarbinary(DeriveLoginSid(originalLogin)),
                SqlValue.FromNVarchar(originalLogin),
                nullDateTime,
                nullDateTime,
                SqlValue.Null(SqlType.BigInt),
                SqlValue.FromInt32(2),
                SqlValue.FromInt16(databaseId),
                SqlValue.FromInt32(1),
                SqlValue.FromInt32(connection.CurrentTransaction?.TranCount ?? 0),
                zeroBig,
                SqlValue.Null(SqlType.UniqueIdentifier),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseMirroring(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullTinyInt = SqlValue.Null(SqlType.TinyInt);
        var nullDesc = SqlValue.Null(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit));
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullName = SqlValue.Null(SqlType.NVarchar);
        var nullLsn = SqlValue.Null(SqlType.GetDecimal(25, 0));

        foreach (var (_, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            yield return [
                SqlValue.FromInt32(id),
                nullGuid,
                nullTinyInt,
                nullDesc,
                nullTinyInt,
                nullDesc,
                nullInt,
                nullTinyInt,
                nullDesc,
                nullInt,
                nullName,
                nullName,
                nullName,
                nullTinyInt,
                nullDesc,
                nullLsn,
                nullInt,
                nullInt,
                nullDesc,
                nullLsn,
                nullLsn,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.database_query_store_options</c> — one OFF row per user
    /// database, zero rows for a system database (master/tempdb/model/msdb per
    /// <see cref="Simulation.SystemDatabaseNames"/>). The simulator never
    /// enables Query Store, so the single row is a fixed "disabled" shape:
    /// desired/actual OFF with <c>query_capture_mode</c> AUTO and NULL
    /// capture-policy columns, the shape live pre-CUSTOM databases report
    /// (probe-confirmed against the reference AW/WWI). A fresh SQL Server
    /// 2025 database defaults to CUSTOM (4) with policy defaults, but DacFx's
    /// bacpac model schema reads only <c>query_capture_mode</c> and cannot
    /// express CUSTOM — exporting 4 produces a model.xml that real DacFx
    /// rejects at import ("The option 4 for querystore query_capture_mode is
    /// not supported"), so AUTO is the round-trippable choice. The join key
    /// is the database context (<paramref name="database"/>), so a
    /// three-part <c>master.sys.database_query_store_options</c> read
    /// returns nothing.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseQueryStoreOptions(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        if (Simulation.SystemDatabaseNames.Contains(database.Name))
            yield break;

        var off = SqlValue.FromNVarchar("OFF");
        yield return [
            SqlValue.FromInt16(0),                  // desired_state
            off,                                    // desired_state_desc
            SqlValue.FromInt16(0),                  // actual_state
            off,                                    // actual_state_desc
            SqlValue.FromInt32(0),                  // readonly_reason
            SqlValue.FromInt64(0),                  // current_storage_size_mb
            SqlValue.FromInt64(900),                // flush_interval_seconds
            SqlValue.FromInt64(60),                 // interval_length_minutes
            SqlValue.FromInt64(1000),               // max_storage_size_mb
            SqlValue.FromInt64(30),                 // stale_query_threshold_days
            SqlValue.FromInt64(200),                // max_plans_per_query
            SqlValue.FromInt16(2),                  // query_capture_mode
            SqlValue.FromNVarchar("AUTO"),          // query_capture_mode_desc
            SqlValue.Null(SqlType.Int32),           // capture_policy_execution_count
            SqlValue.Null(SqlType.BigInt),          // capture_policy_total_compile_cpu_time_ms
            SqlValue.Null(SqlType.BigInt),          // capture_policy_total_execution_cpu_time_ms
            SqlValue.Null(SqlType.Int32),           // capture_policy_stale_threshold_hours
            SqlValue.FromInt16(0),                  // size_based_cleanup_mode
            off,                                    // size_based_cleanup_mode_desc
            SqlValue.FromInt16(0),                  // wait_stats_capture_mode
            off,                                    // wait_stats_capture_mode_desc
            SqlValue.FromNVarchar(string.Empty),    // actual_state_additional_info
        ];
    }

    /// <summary>
    /// Rows for <c>sys.master_files</c> — one data file (<c>type</c> 0, ROWS)
    /// and one log file (<c>type</c> 1, LOG) per database, join key
    /// <c>database_id</c>. The simulator emits no <c>type</c>-2
    /// (FILESTREAM / memory-optimized) files, so SSMS's in-memory-OLTP probe
    /// (<c>where mf.[type] = 2</c>) returns nothing. Contents are synthetic:
    /// logical name <c>&lt;db&gt;_Data</c> / <c>&lt;db&gt;_Log</c>, a plausible
    /// physical path, a small page count, and the 64 MB default autogrowth.
    /// <c>max_size</c> / <c>growth</c> are both in 8 KB pages here (the unit
    /// real uses whenever <c>is_percent_growth</c> is 0): the data file
    /// reports -1 (unlimited) and the log file the 2 TB ceiling. All LSN
    /// columns surface NULL (no physical log).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysMasterFiles(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        var onlineState = SqlValue.FromNVarchar("ONLINE");
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullTime = SqlValue.Null(SqlType.DateTime);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullLsn = SqlValue.Null(SqlType.GetDecimal(25, 0));
        var rowsDesc = SqlValue.FromNVarchar("ROWS");
        var logDesc = SqlValue.FromNVarchar("LOG");
        var unlimited = SqlValue.FromInt32(-1);
        var logMaxSize = SqlValue.FromInt32(LogFileMaxSizePages);
        var growthPages = SqlValue.FromInt32(FileGrowthPages);

        SqlValue[] BuildFile(short id, int fileId, byte type, SqlValue typeDesc, int dataSpaceId, string logicalName, string physicalName, int sizePages) =>
        [
            SqlValue.FromInt32(id),
            SqlValue.FromInt32(fileId),
            nullGuid,
            SqlValue.FromByte(type),
            typeDesc,
            SqlValue.FromInt32(dataSpaceId),
            SqlValue.FromNVarchar(logicalName),
            SqlValue.FromNVarchar(physicalName),
            zeroByte,
            onlineState,
            SqlValue.FromInt32(sizePages),
            type == 1 ? logMaxSize : unlimited,
            growthPages,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            nullLsn,
            nullLsn,
            nullLsn,
            nullLsn,
            nullLsn,
            nullGuid,
            nullTime,
            nullLsn,
            nullGuid,
            nullLsn,
            nullGuid,
            nullLsn,
            nullInt,
        ];

        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            yield return BuildFile(id, 1, 0, rowsDesc, 1, db.Name + "_Data", DataFilePath(db.Name), ComputeDataFileSizePages(db));
            yield return BuildFile(id, 2, 1, logDesc, 0, db.Name + "_Log", LogFilePath(db.Name), LogFileSizePages);
        }
    }

    /// <summary>
    /// Rows for <c>sys.database_files</c> — the current-database projection of
    /// <see cref="EnumerateSysMasterFiles"/>: one data file (<c>file_id</c> 1,
    /// <c>type</c> 0 ROWS) and one log file (<c>file_id</c> 2, <c>type</c> 1
    /// LOG) for the resolved <paramref name="database"/>. Names / file_ids /
    /// types agree with <c>sys.master_files</c>; there is no
    /// <c>database_id</c> column (the view is implicitly current-database), so
    /// a three-part <c>master.sys.database_files</c> read returns master's two
    /// files. Synthetic contents mirror master_files: logical name
    /// <c>&lt;db&gt;_Data</c> / <c>&lt;db&gt;_Log</c>, a plausible physical
    /// path, a small page count, and the page-denominated <c>max_size</c> /
    /// <c>growth</c> pair (-1 unlimited on the data file, the 2 TB ceiling on
    /// the log, 8192 pages of growth on both).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseFiles(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        var onlineState = SqlValue.FromNVarchar("ONLINE");
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var rowsDesc = SqlValue.FromNVarchar("ROWS");
        var logDesc = SqlValue.FromNVarchar("LOG");
        var unlimited = SqlValue.FromInt32(-1);
        var logMaxSize = SqlValue.FromInt32(LogFileMaxSizePages);
        var growthPages = SqlValue.FromInt32(FileGrowthPages);
        var nullLsn = SqlValue.Null(lsnNumeric);

        SqlValue[] BuildFile(int fileId, byte type, SqlValue typeDesc, int dataSpaceId, string logicalName, string physicalName, int sizePages) =>
        [
            SqlValue.FromInt32(fileId),
            nullGuid,
            SqlValue.FromByte(type),
            typeDesc,
            SqlValue.FromInt32(dataSpaceId),
            SqlValue.FromNVarchar(logicalName),
            SqlValue.FromNVarchar(physicalName),
            zeroByte,
            onlineState,
            SqlValue.FromInt32(sizePages),
            type == 1 ? logMaxSize : unlimited,
            growthPages,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            nullLsn,
        ];

        yield return BuildFile(1, 0, rowsDesc, 1, database.Name + "_Data", DataFilePath(database.Name), ComputeDataFileSizePages(database));
        yield return BuildFile(2, 1, logDesc, 0, database.Name + "_Log", LogFilePath(database.Name), LogFileSizePages);
    }

    /// <summary>
    /// Rows for <c>sys.servers</c>. Row 0 is the local instance
    /// (<c>is_linked = 0</c>, name <c>"SIMULATED"</c>, product
    /// <c>"SQL Server"</c>); each subsequent row is one entry from
    /// <see cref="Simulation.ActiveLinkedServers"/> in name-sort order
    /// (stable across runs, distinct from real SQL Server's
    /// <c>object_id</c>-derived ordering — see the quirks list).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServers(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var notLinked = SqlValue.FromBoolean(false);
        var isLinked = SqlValue.FromBoolean(true);
        var localProduct = SqlValue.FromNVarchar("SQL Server");
        var nullProvider = SqlValue.Null(SqlType.NVarchar);
        var nullDataSource = SqlValue.Null(SqlType.NVarchar);
        yield return [
            SqlValue.FromInt32(0),
            SqlValue.FromSystemName("SIMULATED"),
            localProduct,
            nullProvider,
            nullDataSource,
            notLinked,
        ];

        var serverId = 1;
        foreach (var ls in batch.Connection.Simulation.ActiveLinkedServers.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return [
                SqlValue.FromInt32(serverId++),
                SqlValue.FromSystemName(ls.Name),
                SqlValue.FromNVarchar(ls.SrvProduct),
                SqlValue.FromNVarchar(ls.Provider),
                ls.DataSource is null ? nullDataSource : SqlValue.FromNVarchar(ls.DataSource),
                isLinked,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.fn_helpcollations()</c>. Emits one row per entry in
    /// <see cref="Collation.IsRecognized"/> — the simulator's whitelist of
    /// metadata-accepted collation names. Real SQL Server returns ~5540
    /// rows here; the simulator's shorter list is honest about which
    /// collation names round-trip through <see cref="Database.CollationName"/>
    /// / <see cref="HeapColumn.Collation"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateFnHelpCollations(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        foreach (var (entryName, entryDesc) in Collation.EnumerateRecognized().OrderBy(e => e.Name, StringComparer.Ordinal))
            yield return [SqlValue.FromSystemName(entryName), SqlValue.FromNVarchar(entryDesc)];
    }
}
