using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Buffers.Binary;
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
            new("default_language_name", NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, true),
            new("default_fulltext_language_lcid", SqlType.Int32, null, true),
            new("default_fulltext_language_name", NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, true),
            new("is_nested_triggers_on", SqlType.Bit, null, true),
            new("is_transform_noise_words_on", SqlType.Bit, null, true),
            new("two_digit_year_cutoff", SqlType.SmallInt, null, true),
            new("containment", SqlType.TinyInt, null, true),
            new("containment_desc", nvarchar60Catalog, 60, true),
            new("target_recovery_time_in_seconds", SqlType.Int32, null, true),
            new("delayed_durability", SqlType.Int32, null, true),
            new("delayed_durability_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("is_memory_optimized_elevate_to_snapshot_on", SqlType.Bit, null, true),
            new("is_federation_member", SqlType.Bit, null, true),
            new("is_remote_data_archive_enabled", SqlType.Bit, null, true),
            new("is_mixed_page_allocation_on", SqlType.Bit, null, true),
            new("is_temporal_history_retention_enabled", SqlType.Bit, null, true),
            new("catalog_collation_type", SqlType.Int32, null, false),
            new("catalog_collation_type_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("physical_database_name", NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, true),
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
        // collations the simulator recognizes, each row the canonical name and
        // a human description, both NOT NULL as real reports them (probed
        // 2026-10-02 against SQL Server 2025).
        Sys("fn_helpcollations",
        [
            new("name", SqlType.SystemName, 128, false),
            new("description", SqlType.NVarchar, 1000, false),
        ], EnumerateFnHelpCollations);

        // sys.servers: the local instance projects as row 0 (is_linked = 0);
        // each entry in <see cref="Simulation.ActiveLinkedServers"/> follows
        // with a stable monotonic server_id keyed by name-sort.
        Sys("servers",
        [
            new("server_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("product", SqlType.SystemName, 128, false),
            new("provider", SqlType.SystemName, 128, false),
            new("data_source", SqlType.NVarchar, 4000, true),
            new("location", SqlType.NVarchar, 4000, true),
            new("provider_string", SqlType.NVarchar, 4000, true),
            new("catalog", SqlType.SystemName, 128, true),
            new("connect_timeout", SqlType.Int32, null, true),
            new("query_timeout", SqlType.Int32, null, true),
            new("is_linked", SqlType.Bit, null, false),
            new("is_remote_login_enabled", SqlType.Bit, null, false),
            new("is_rpc_out_enabled", SqlType.Bit, null, false),
            new("is_data_access_enabled", SqlType.Bit, null, false),
            new("is_collation_compatible", SqlType.Bit, null, false),
            new("uses_remote_collation", SqlType.Bit, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("lazy_schema_validation", SqlType.Bit, null, false),
            new("is_system", SqlType.Bit, null, false),
            new("is_publisher", SqlType.Bit, null, false),
            new("is_subscriber", SqlType.Bit, null, true),
            new("is_distributor", SqlType.Bit, null, true),
            new("is_nonsql_subscriber", SqlType.Bit, null, true),
            new("is_remote_proc_transaction_promotion_enabled", SqlType.Bit, null, true),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_rda_server", SqlType.Bit, null, true),
        ], EnumerateSysServers);

        // sysservers: the SQL Server 2000 compatibility view over sys.servers,
        // its options packed into srvstatus as sp_serveroption's bits (probed
        // 2026-10-06 against SQL Server 2025).
        var nvarcharName = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var nvarcharText = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);
        var sysservers = new CatalogView("sysservers",
        [
            new("srvid", SqlType.SmallInt, null, true),
            new("srvstatus", SqlType.SmallInt, null, true),
            new("srvname", nvarcharName, 128, false),
            new("srvproduct", nvarcharName, 128, false),
            new("providername", nvarcharName, 128, false),
            new("datasource", nvarcharText, 4000, true),
            new("location", nvarcharText, 4000, true),
            new("providerstring", nvarcharText, 4000, true),
            new("schemadate", SqlType.DateTime, null, false),
            new("topologyx", SqlType.Int32, null, true),
            new("topologyy", SqlType.Int32, null, true),
            new("catalog", nvarcharName, 128, true),
            new("srvcollation", nvarcharName, 128, true),
            new("connecttimeout", SqlType.Int32, null, true),
            new("querytimeout", SqlType.Int32, null, true),
            new("srvnetname", CharSqlType.Get(30, Collation.Catalog, Coercibility.Implicit), 30, true),
            new("isremote", SqlType.Bit, null, true),
            new("rpc", SqlType.Bit, null, false),
            new("pub", SqlType.Bit, null, false),
            new("sub", SqlType.Bit, null, true),
            new("dist", SqlType.Bit, null, true),
            new("dpub", SqlType.Bit, null, true),
            new("rpcout", SqlType.Bit, null, false),
            new("dataaccess", SqlType.Bit, null, false),
            new("collationcompatible", SqlType.Bit, null, false),
            new("system", SqlType.Bit, null, false),
            new("useremotecollation", SqlType.Bit, null, false),
            new("lazyschemavalidation", SqlType.Bit, null, false),
            new("collation", nvarcharName, 128, true),
            new("nonsqlsub", SqlType.Bit, null, true),
        ], EnumerateSysservers);
        views["sysservers"] = sysservers;
        views["sys.sysservers"] = sysservers;

        // sys.linked_logins: each linked server's login mappings, which
        // sp_addlinkedserver, sp_addlinkedsrvlogin and sp_droplinkedsrvlogin
        // maintain (probed 2026-10-05 against SQL Server 2025); and
        // sys.remote_logins, the old remote-server mappings, which nothing here
        // creates.
        Sys("linked_logins",
        [
            new("server_id", SqlType.Int32, null, false),
            new("local_principal_id", SqlType.Int32, null, true),
            new("uses_self_credential", SqlType.Bit, null, false),
            new("remote_name", SqlType.SystemName, 128, true),
            new("modify_date", SqlType.DateTime, null, false),
        ], EnumerateSysLinkedLogins);
        Sys("remote_logins",
        [
            new("server_id", SqlType.Int32, null, false),
            new("remote_name", SqlType.SystemName, 128, true),
            new("local_principal_id", SqlType.Int32, null, true),
            new("modify_date", SqlType.DateTime, null, false),
        ], static (_, _) => []);

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

        // sys.dm_os_sys_info: single-row, server-scope DMV describing the
        // instance's host resources (probed 2026-09-25 against SQL Server
        // 2025). Like dm_os_host_info it reflects the .NET host process: the
        // processor count, the memory the runtime sees, the process's CPU
        // times, and the simulation's construction as the server start; the
        // worker and scheduler counts follow real's formulas from the CPU
        // count, and the configuration columns carry real's defaults.
        Sys("dm_os_sys_info",
        [
            new("cpu_ticks", SqlType.BigInt, null, false),
            new("ms_ticks", SqlType.BigInt, null, false),
            new("cpu_count", SqlType.Int32, null, false),
            new("hyperthread_ratio", SqlType.Int32, null, false),
            new("physical_memory_kb", SqlType.BigInt, null, false),
            new("virtual_memory_kb", SqlType.BigInt, null, false),
            new("committed_kb", SqlType.BigInt, null, false),
            new("committed_target_kb", SqlType.BigInt, null, false),
            new("visible_target_kb", SqlType.BigInt, null, false),
            new("stack_size_in_bytes", SqlType.Int32, null, false),
            new("os_quantum", SqlType.BigInt, null, false),
            new("os_error_mode", SqlType.Int32, null, false),
            new("os_priority_class", SqlType.Int32, null, true),
            new("max_workers_count", SqlType.Int32, null, false),
            new("scheduler_count", SqlType.Int32, null, false),
            new("scheduler_total_count", SqlType.Int32, null, false),
            new("deadlock_monitor_serial_number", SqlType.Int32, null, false),
            new("sqlserver_start_time_ms_ticks", SqlType.BigInt, null, false),
            new("sqlserver_start_time", SqlType.DateTime, null, false),
            new("affinity_type", SqlType.Int32, null, false),
            new("affinity_type_desc", SqlType.NVarchar, 60, false),
            new("process_kernel_time_ms", SqlType.BigInt, null, false),
            new("process_user_time_ms", SqlType.BigInt, null, false),
            new("time_source", SqlType.Int32, null, false),
            new("time_source_desc", SqlType.NVarchar, 60, false),
            new("virtual_machine_type", SqlType.Int32, null, false),
            new("virtual_machine_type_desc", SqlType.NVarchar, 60, false),
            new("softnuma_configuration", SqlType.Int32, null, false),
            new("softnuma_configuration_desc", SqlType.NVarchar, 60, false),
            new("process_physical_affinity", SqlType.NVarchar, 3072, false),
            new("sql_memory_model", SqlType.Int32, null, false),
            new("sql_memory_model_desc", SqlType.NVarchar, 60, false),
            new("socket_count", SqlType.Int32, null, false),
            new("cores_per_socket", SqlType.Int32, null, false),
            new("numa_node_count", SqlType.Int32, null, false),
            new("container_type", SqlType.Int32, null, false),
            new("container_type_desc", SqlType.NVarchar, 60, false),
        ], (batch, database) => DmOsSysInfoRows(batch.Connection.Simulation));

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
        // arithabort, the ANSI bits, ansi_null_dflt_on, ansi_defaults,
        // deadlock_priority, text_size, lock_timeout,
        // transaction_isolation_level, context_info, row_count = @@ROWCOUNT,
        // prev_error = @@ERROR, open_transaction_count, database_id,
        // host_name / program_name off the connection string or LOGIN7,
        // login_name / original_login_name and their derived SIDs); the
        // remainder are probe-confirmed fresh-session defaults from SQL Server
        // 2025 (endpoint_id 4, group_id 2, client_version 7). status is
        // 'running' for the querying session,
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
            new("status", SqlType.NVarchar, 30, false),
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

        // sys.dm_exec_connections: one row per session's physical connection
        // (probed 2026-09-25 against SQL Server 2025). A TDS-endpoint session
        // reports its TCP endpoints, LOGIN7's TDS version, the negotiated
        // packet size and its packet counts, always encrypted since the
        // endpoint requires TLS; an in-process connection reports the
        // shared-memory shape, with no network columns.
        Sys("dm_exec_connections",
        [
            new("session_id", SqlType.Int32, null, true),
            new("most_recent_session_id", SqlType.Int32, null, true),
            new("connect_time", SqlType.DateTime, null, false),
            new("net_transport", SqlType.NVarchar, 40, false),
            new("protocol_type", SqlType.NVarchar, 40, true),
            new("protocol_version", SqlType.Int32, null, true),
            new("endpoint_id", SqlType.Int32, null, true),
            new("encrypt_option", SqlType.NVarchar, 40, false),
            new("auth_scheme", SqlType.NVarchar, 40, false),
            new("node_affinity", SqlType.SmallInt, null, false),
            new("num_reads", SqlType.Int32, null, true),
            new("num_writes", SqlType.Int32, null, true),
            new("last_read", SqlType.DateTime, null, true),
            new("last_write", SqlType.DateTime, null, true),
            new("net_packet_size", SqlType.Int32, null, true),
            new("client_net_address", SqlType.NVarchar, 48, true),
            new("client_tcp_port", SqlType.Int32, null, true),
            new("local_net_address", SqlType.NVarchar, 48, true),
            new("local_tcp_port", SqlType.Int32, null, true),
            new("connection_id", SqlType.UniqueIdentifier, null, false),
            new("parent_connection_id", SqlType.UniqueIdentifier, null, true),
            new("most_recent_sql_handle", SqlType.Varbinary, 64, true),
        ], EnumerateSysDmExecConnections);

        // sys.dm_exec_requests: one row per session with a command in flight —
        // the querying session always, and any other mid-statement or blocked
        // (probed 2026-09-25 against SQL Server 2025). A lock wait reports its
        // LCK_M_<mode>, blocker and resource, a WAITFOR reports WAITFOR, and
        // the session-option columns read as sys.dm_exec_sessions reads them.
        // No sql or plan handles are modeled, so those columns are NULL.
        Sys("dm_exec_requests",
        [
            new("session_id", SqlType.SmallInt, null, false),
            new("request_id", SqlType.Int32, null, false),
            new("start_time", SqlType.DateTime, null, false),
            new("status", SqlType.NVarchar, 30, false),
            new("command", SqlType.NVarchar, 32, false),
            new("sql_handle", SqlType.Varbinary, 64, true),
            new("statement_start_offset", SqlType.Int32, null, true),
            new("statement_end_offset", SqlType.Int32, null, true),
            new("plan_handle", SqlType.Varbinary, 64, true),
            new("database_id", SqlType.SmallInt, null, false),
            new("user_id", SqlType.Int32, null, false),
            new("connection_id", SqlType.UniqueIdentifier, null, true),
            new("blocking_session_id", SqlType.SmallInt, null, true),
            new("wait_type", SqlType.NVarchar, 60, true),
            new("wait_time", SqlType.Int32, null, false),
            new("last_wait_type", SqlType.NVarchar, 60, false),
            new("wait_resource", SqlType.NVarchar, 256, false),
            new("open_transaction_count", SqlType.Int32, null, false),
            new("open_resultset_count", SqlType.Int32, null, false),
            new("transaction_id", SqlType.BigInt, null, false),
            new("context_info", SqlType.Varbinary, 128, true),
            new("percent_complete", SqlType.Real, null, false),
            new("estimated_completion_time", SqlType.BigInt, null, false),
            new("cpu_time", SqlType.Int32, null, false),
            new("total_elapsed_time", SqlType.Int32, null, false),
            new("scheduler_id", SqlType.Int32, null, true),
            new("task_address", SqlType.Varbinary, 8, true),
            new("reads", SqlType.BigInt, null, false),
            new("writes", SqlType.BigInt, null, false),
            new("logical_reads", SqlType.BigInt, null, false),
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
            new("nest_level", SqlType.Int32, null, false),
            new("granted_query_memory", SqlType.Int32, null, false),
            new("executing_managed_code", SqlType.Bit, null, false),
            new("group_id", SqlType.Int32, null, false),
            new("query_hash", SqlType.GetBinary(8), null, true),
            new("query_plan_hash", SqlType.GetBinary(8), null, true),
            new("statement_sql_handle", SqlType.Varbinary, 64, true),
            new("statement_context_id", SqlType.BigInt, null, true),
            new("dop", SqlType.Int32, null, false),
            new("parallel_worker_count", SqlType.Int32, null, true),
            new("external_script_request_id", SqlType.UniqueIdentifier, null, true),
            new("is_resumable", SqlType.Bit, null, false),
            new("page_resource", SqlType.Varbinary, 8, true),
            new("page_server_reads", SqlType.BigInt, null, false),
            new("dist_statement_id", SqlType.UniqueIdentifier, null, true),
            new("label", SqlType.NVarchar, 255, true),
        ], EnumerateSysDmExecRequests);

        // The transaction DMVs (probed 2026-09-25 against SQL Server 2025) list
        // each session's user transaction under the id CURRENT_TRANSACTION_ID()
        // reports, plus the querying statement's own autocommit transaction.
        Sys("dm_tran_active_transactions",
        [
            new("transaction_id", SqlType.BigInt, null, false),
            new("name", SqlType.NVarchar, 32, false),
            new("transaction_begin_time", SqlType.DateTime, null, false),
            new("transaction_type", SqlType.Int32, null, false),
            new("transaction_uow", SqlType.UniqueIdentifier, null, true),
            new("transaction_state", SqlType.Int32, null, false),
            new("transaction_status", SqlType.Int32, null, false),
            new("transaction_status2", SqlType.Int32, null, false),
            new("dtc_state", SqlType.Int32, null, false),
            new("dtc_status", SqlType.Int32, null, false),
            new("dtc_isolation_level", SqlType.Int32, null, false),
            new("filestream_transaction_id", SqlType.Varbinary, 128, true),
        ], EnumerateSysDmTranActiveTransactions);

        Sys("dm_tran_session_transactions",
        [
            new("session_id", SqlType.Int32, null, false),
            new("transaction_id", SqlType.BigInt, null, false),
            new("transaction_descriptor", SqlType.GetBinary(8), null, false),
            new("enlist_count", SqlType.Int32, null, false),
            new("is_user_transaction", SqlType.Bit, null, false),
            new("is_local", SqlType.Bit, null, false),
            new("is_enlisted", SqlType.Bit, null, false),
            new("is_bound", SqlType.Bit, null, false),
            new("open_transaction_count", SqlType.Int32, null, false),
        ], EnumerateSysDmTranSessionTransactions);

        Sys("dm_tran_current_transaction",
        [
            new("transaction_id", SqlType.BigInt, null, true),
            new("transaction_sequence_num", SqlType.BigInt, null, true),
            new("transaction_is_snapshot", SqlType.Bit, null, true),
            new("first_snapshot_sequence_num", SqlType.BigInt, null, true),
            new("last_transaction_sequence_num", SqlType.BigInt, null, true),
            new("first_useful_sequence_num", SqlType.BigInt, null, true),
        ], EnumerateSysDmTranCurrentTransaction);

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
        Sys("database_scoped_configurations",
        [
            new("configuration_id", SqlType.Int32, null, true),
            new("name", SqlType.NVarchar, 60, true),
            new("value", SqlType.SqlVariant, null, true),
            new("value_for_secondary", SqlType.SqlVariant, null, true),
            new("is_value_default", SqlType.Bit, null, true),
        ], (batch, database) => DatabaseScopedConfigurationRows(database));

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
            new("mirroring_failover_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("mirroring_connection_timeout", SqlType.Int32, null, true),
            new("mirroring_redo_queue", SqlType.Int32, null, true),
            new("mirroring_redo_queue_type", nvarchar60Catalog, 60, true),
            new("mirroring_end_of_log_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("mirroring_replication_lsn", lsnNumeric, null, true, spelledNumeric: true),
        ], EnumerateSysDatabaseMirroring);

        // sys.endpoints: the five system endpoints every instance ships, owned
        // by sa, started, the dedicated admin connection the only admin one
        // (probed 2026-09-30 against SQL Server 2025); sys.tcp_endpoints lists
        // the two over TCP, each on a dynamic port 0. The simulator's own TDS
        // listener isn't among them, as a listener's port never is.
        HeapColumn[] endpointColumns =
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
        ];
        Sys("endpoints", endpointColumns, static (_, _) => SystemEndpointRows(tcpOnly: false));
        Sys("tcp_endpoints",
        [
            .. endpointColumns,
            new("port", SqlType.Int32, null, false),
            new("is_dynamic_port", SqlType.Bit, null, false),
            new("ip_address", VarcharSqlType.Get(45, Collation.Catalog, Coercibility.Implicit), 45, true),
        ], static (_, _) => SystemEndpointRows(tcpOnly: true));

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
            new("availability_mode_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("failover_mode", SqlType.TinyInt, null, true),
            new("failover_mode_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("session_timeout", SqlType.Int32, null, true),
            new("primary_role_allow_connections", SqlType.TinyInt, null, true),
            new("primary_role_allow_connections_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("secondary_role_allow_connections", SqlType.TinyInt, null, true),
            new("secondary_role_allow_connections_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("create_date", SqlType.DateTime, null, true),
            new("modify_date", SqlType.DateTime, null, true),
            new("backup_priority", SqlType.Int32, null, true),
            new("read_only_routing_url", SqlType.NVarchar, 256, true),
            new("seeding_mode", SqlType.TinyInt, null, true),
            new("seeding_mode_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("read_write_routing_url", SqlType.NVarchar, 256, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.availability_groups: server-scope AlwaysOn catalog, always empty
        // (no AGs configured). SSMS's enumeration does
        // `insert into #tmp select group_id, name from
        // master.sys.availability_groups`.
        Sys("availability_groups",
        [
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("resource_id", SqlType.NVarchar, 40, true),
            new("resource_group_id", SqlType.NVarchar, 40, true),
            new("failure_condition_level", SqlType.Int32, null, true),
            new("health_check_timeout", SqlType.Int32, null, true),
            new("automated_backup_preference", SqlType.TinyInt, null, true),
            new("automated_backup_preference_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("version", SqlType.SmallInt, null, true),
            new("basic_features", SqlType.Bit, null, true),
            new("dtc_support", SqlType.Bit, null, true),
            new("db_failover", SqlType.Bit, null, true),
            new("is_distributed", SqlType.Bit, null, true),
            new("cluster_type", SqlType.TinyInt, null, true),
            new("cluster_type_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
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
            new("quorum_type_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, false),
            new("quorum_state", SqlType.TinyInt, null, false),
            new("quorum_state_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, false),
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
            new("synchronization_state_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("is_commit_participant", SqlType.Bit, null, true),
            new("synchronization_health", SqlType.TinyInt, null, true),
            new("synchronization_health_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("database_state", SqlType.TinyInt, null, true),
            new("database_state_desc", nvarchar60Catalog, 60, true),
            new("is_suspended", SqlType.Bit, null, true),
            new("suspend_reason", SqlType.TinyInt, null, true),
            new("suspend_reason_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("recovery_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("truncation_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_sent_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_sent_time", SqlType.DateTime, null, true),
            new("last_received_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_received_time", SqlType.DateTime, null, true),
            new("last_hardened_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_hardened_time", SqlType.DateTime, null, true),
            new("last_redone_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_redone_time", SqlType.DateTime, null, true),
            new("log_send_queue_size", SqlType.BigInt, null, true),
            new("log_send_rate", SqlType.BigInt, null, true),
            new("redo_queue_size", SqlType.BigInt, null, true),
            new("redo_rate", SqlType.BigInt, null, true),
            new("filestream_send_rate", SqlType.BigInt, null, true),
            new("end_of_log_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_commit_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("last_commit_time", SqlType.DateTime, null, true),
            new("low_water_mark_for_ghosts", SqlType.BigInt, null, true),
            new("secondary_lag_seconds", SqlType.BigInt, null, true),
            new("quorum_commit_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("quorum_commit_time", SqlType.DateTime, null, true),
            new("is_internal", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.master_files: every database's files, join key database_id.
        // All LSN columns numeric(25, 0).
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
            new("create_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("drop_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("read_only_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("read_write_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("differential_base_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("differential_base_guid", SqlType.UniqueIdentifier, null, true),
            new("differential_base_time", SqlType.DateTime, null, true),
            new("redo_start_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("redo_start_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("redo_target_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("redo_target_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("backup_lsn", lsnNumeric, null, true, spelledNumeric: true),
            new("credential_id", SqlType.Int32, null, true),
        ], EnumerateSysMasterFiles);

        // sys.database_files: the resolved database's slice of master_files,
        // so a three-part `master.sys.database_files` read (SSMS reads it to
        // derive the master data/log directory) returns master's files.
        Sys("database_files",
        [
            new("file_id", SqlType.Int32, null, false),
            new("file_guid", SqlType.UniqueIdentifier, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 128, true),
            new("physical_name", SqlType.NVarchar, 260, true),
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
            new("drop_lsn", lsnNumeric, null, true, spelledNumeric: true),
        ], EnumerateSysDatabaseFiles);
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
        if (!simulation.EnableClr && simulation.ServerConfiguration.IsEmptyLockFree())
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
    /// <c>clr enabled</c> is the exception, and reports the simulation's
    /// <see cref="Simulation.EnableClr"/> opt-in whatever <c>sp_configure</c>
    /// wrote.
    /// </para>
    /// </summary>
    internal static (int Configured, int InUse) EffectiveConfigurationValues(Simulation simulation, int index)
    {
        var (id, name, value, _, _, valueInUse, _, _, _) = ConfigurationData[index];
        if (name == "clr enabled")
        {
            var clr = simulation.EnableClr ? 1 : value;
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
    /// <c>sys.database_scoped_configurations</c> rows for <paramref name="database"/>,
    /// from its <see cref="Database.ScopedConfiguration"/>. <c>value</c> /
    /// <c>value_for_secondary</c> are <c>sql_variant</c> carrying each option's
    /// own base type, and <c>is_value_default</c> compares the primary value
    /// alone with the option's default (probed 2026-09-27 against SQL Server
    /// 2025). A system database omits <c>PREVIEW_FEATURES</c>, as real's do.
    /// </summary>
    private static IEnumerable<SqlValue[]> DatabaseScopedConfigurationRows(Database database)
    {
        var configuration = database.ScopedConfiguration;
        var nullValue = SqlValue.Null(SqlType.SqlVariant);
        var options = DatabaseScopedConfiguration.Options;
        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i];
            if (option.Id == DatabaseScopedConfiguration.UserDatabaseOnlyId && database.Id is >= 1 and <= 4)
                continue;
            var value = configuration.Primary(i);
            yield return
            [
                SqlValue.FromInt32(option.Id),
                SqlValue.FromNVarchar(option.Name),
                SqlValue.FromVariant(DatabaseScopedConfiguration.ToSqlValue(option.Kind, value)),
                configuration.Secondary(i) is { } secondary
                    ? SqlValue.FromVariant(DatabaseScopedConfiguration.ToSqlValue(option.Kind, secondary))
                    : nullValue,
                SqlValue.FromBoolean(value == option.DefaultValue),
            ];
        }
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
    /// <c>is_fulltext_enabled</c> / <c>DATABASEPROPERTYEX(…, 'IsFulltextEnabled')</c>:
    /// on for every database but master, model and tempdb (probed 2026-09-30
    /// against SQL Server 2025) until <c>sp_fulltext_database 'disable'</c> turns it off.
    /// </summary>
    internal static bool ReportsFullTextEnabled(Database database) =>
        !BuiltInToken.EqualsAny(database.Name, "master", "model", "tempdb") && !database.FullTextDisabled;

    /// <summary>
    /// Rows for <c>sys.databases</c>. One row per <see cref="Database"/>
    /// hosted by the connected <see cref="Simulation"/>; matches real SQL
    /// Server's "instance-scoped catalog view" semantic. Full 98-column
    /// projection: modeled columns read live <see cref="Database"/> state
    /// (name / database_id / compatibility_level / collation_name /
    /// snapshot-isolation trio / recovery_model / physical_database_name),
    /// the <c>ALTER DATABASE … SET</c> switches read
    /// <see cref="Database.Switches"/> / <see cref="Database.PageVerify"/> /
    /// <see cref="Database.UserAccess"/>, state is always <c>0 / ONLINE</c>,
    /// and the remaining option-flag columns carry the stock defaults a
    /// freshly created user database reports on SQL Server 2025
    /// (containment NONE, log_reuse_wait NOTHING, delayed_durability
    /// DISABLED, catalog_collation DATABASE_DEFAULT). recovery_model is
    /// SIMPLE for <c>master</c> / <c>tempdb</c> / <c>msdb</c> and FULL for
    /// <c>model</c> and every user database, which inherits the template's,
    /// and <c>is_broker_enabled</c> / <c>target_recovery_time_in_seconds</c>
    /// read <see cref="Database.BrokerEnabled"/> /
    /// <see cref="Database.TargetRecoveryTimeSeconds"/>. Code↔desc pairs are
    /// always internally consistent.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabases(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var zeroByte = SqlValue.FromByte(0);
        var createDate = SqlValue.FromDateTime(SysDatabasesCreateDate);
        var brokerGuid = SqlValue.FromGuid(SysDatabasesBrokerGuid);
        var zeroGuid = SqlValue.FromGuid(Guid.Empty);
        var online = SqlValue.FromNVarchar("ONLINE");
        var nothing = SqlValue.FromNVarchar("NOTHING");
        var none = SqlValue.FromNVarchar("NONE");
        var disabled = SqlValue.FromNVarchar("DISABLED");
        var databaseDefault = SqlValue.FromNVarchar("DATABASE_DEFAULT");
        var unsupported = SqlValue.FromNVarchar("UNSUPPORTED");
        var zeroInt = SqlValue.FromInt32(0);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullSmallInt = SqlValue.Null(SqlType.SmallInt);
        var nullBit = SqlValue.Null(SqlType.Bit);
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullName = SqlValue.Null(SqlType.NVarchar);

        // Ordered by database_id via DatabasesWithIds (master = 1, system
        // databases 2-4, user databases from 5) — matching real SQL Server's
        // sys.databases ordering by database_id.
        var connection = batch.Connection;
        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(connection.Simulation))
        {
            if (!connection.Simulation.CanSeeDatabase(connection, db))
                continue;
            var snapshotOn = db.AllowSnapshotIsolation;
            // master and model carry an all-zero broker GUID (probed
            // 2026-09-30 against SQL Server 2025).
            var isMasterOrModel = BuiltInToken.EqualsAny(db.Name, "master", "model");
            var switches = db.Switches;
            SqlValue Switch(DatabaseSwitches flag) => (switches & flag) != 0 ? trueBit : falseBit;
            yield return [
                SqlValue.FromSystemName(db.Name),
                SqlValue.FromInt32(id),
                nullInt,
                SqlValue.FromVarbinary(Ownership.OwnerSid(db)),
                createDate,
                SqlValue.FromByte((byte)db.CompatibilityLevel),
                SqlValue.FromSystemName(db.CollationName),
                SqlValue.FromByte(db.UserAccess),
                SqlValue.FromNVarchar(db.UserAccess switch
                {
                    1 => "SINGLE_USER",
                    2 => "RESTRICTED_USER",
                    _ => "MULTI_USER",
                }),
                SqlValue.FromBoolean(db.IsReadOnly), // is_read_only
                Switch(DatabaseSwitches.AutoClose),
                Switch(DatabaseSwitches.AutoShrink),
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
                SqlValue.FromByte(db.PageVerify),
                SqlValue.FromNVarchar(db.PageVerify switch
                {
                    0 => "NONE",
                    1 => "TORN_PAGE_DETECTION",
                    _ => "CHECKSUM",
                }),
                Switch(DatabaseSwitches.AutoCreateStatistics),
                Switch(DatabaseSwitches.AutoCreateStatisticsIncremental),
                Switch(DatabaseSwitches.AutoUpdateStatistics),
                Switch(DatabaseSwitches.AutoUpdateStatisticsAsync),
                Switch(DatabaseSwitches.AnsiNullDefault),
                Switch(DatabaseSwitches.AnsiNulls),
                Switch(DatabaseSwitches.AnsiPadding),
                Switch(DatabaseSwitches.AnsiWarnings),
                Switch(DatabaseSwitches.ArithAbort),
                Switch(DatabaseSwitches.ConcatNullYieldsNull),
                Switch(DatabaseSwitches.NumericRoundAbort),
                Switch(DatabaseSwitches.QuotedIdentifier),
                SqlValue.FromBoolean(db.RecursiveTriggers),
                Switch(DatabaseSwitches.CursorCloseOnCommit),
                Switch(DatabaseSwitches.LocalCursorDefault),
                ReportsFullTextEnabled(db) ? trueBit : falseBit,
                SqlValue.FromBoolean(db.Trustworthy),
                SqlValue.FromBoolean(db.CrossDatabaseChaining),
                Switch(DatabaseSwitches.ParameterizationForced),
                falseBit, // is_master_key_encrypted_by_server
                // Any desired state but OFF reads on here, READ_ONLY included —
                // the flag tracks desired_state, not actual_state.
                SqlValue.FromBoolean(db.QueryStore.DesiredState != QueryStoreState.Off),
                falseBit, // is_published
                falseBit, // is_subscribed
                falseBit, // is_merge_published
                falseBit, // is_distributor
                falseBit, // is_sync_with_backup
                isMasterOrModel ? zeroGuid : brokerGuid,
                SqlValue.FromBoolean(db.BrokerEnabled),
                zeroByte,
                nothing,
                Switch(DatabaseSwitches.DateCorrelationOptimization),
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
                SqlValue.FromInt32(db.TargetRecoveryTimeSeconds),
                zeroInt,
                disabled,
                Switch(DatabaseSwitches.MemoryOptimizedElevateToSnapshot),
                falseBit, // is_federation_member
                falseBit, // is_remote_data_archive_enabled
                falseBit, // is_mixed_page_allocation_on
                Switch(DatabaseSwitches.TemporalHistoryRetention),
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

    /// <summary>The one row of <c>sys.dm_os_sys_info</c>, read from the host process at query time.</summary>
    private static SqlValue[][] DmOsSysInfoRows(Simulation simulation)
    {
        var cpus = Environment.ProcessorCount;
        var memoryKb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var committedKb = process.WorkingSet64 / 1024;
        return [[
            SqlValue.FromInt64(System.Diagnostics.Stopwatch.GetTimestamp()),
            SqlValue.FromInt64(Environment.TickCount64),
            SqlValue.FromInt32(cpus),
            SqlValue.FromInt32(cpus),
            SqlValue.FromInt64(memoryKb),
            SqlValue.FromInt64(68719476672),
            SqlValue.FromInt64(committedKb),
            SqlValue.FromInt64(memoryKb),
            SqlValue.FromInt64(memoryKb),
            SqlValue.FromInt32(2093056),
            SqlValue.FromInt64(4),
            SqlValue.FromInt32(5),
            // NORMAL_PRIORITY_CLASS on Windows; real on Linux reports 16384.
            SqlValue.FromInt32(OperatingSystem.IsWindows() ? 32 : 16384),
            // Real's default max worker threads on 64-bit: 512, plus 16 per
            // CPU past the fourth.
            SqlValue.FromInt32(cpus <= 4 ? 512 : 512 + ((cpus - 4) * 16)),
            SqlValue.FromInt32(cpus),
            SqlValue.FromInt32(cpus + 8),
            SqlValue.FromInt32(0),
            SqlValue.FromInt64(simulation.StartTicks),
            SqlValue.FromDateTime(simulation.SeedDate),
            SqlValue.FromInt32(2),
            SqlValue.FromNVarchar("AUTO"),
            SqlValue.FromInt64((long)process.PrivilegedProcessorTime.TotalMilliseconds),
            SqlValue.FromInt64((long)process.UserProcessorTime.TotalMilliseconds),
            SqlValue.FromInt32(0),
            SqlValue.FromNVarchar("QUERY_PERFORMANCE_COUNTER"),
            SqlValue.FromInt32(0),
            SqlValue.FromNVarchar("NONE"),
            SqlValue.FromInt32(0),
            SqlValue.FromNVarchar("OFF"),
            SqlValue.FromNVarchar("{}"),
            SqlValue.FromInt32(1),
            SqlValue.FromNVarchar("CONVENTIONAL"),
            SqlValue.FromInt32(1),
            SqlValue.FromInt32(cpus),
            SqlValue.FromInt32(1),
            SqlValue.FromInt32(0),
            SqlValue.FromNVarchar("NONE"),
        ]];
    }

    /// <summary>
    /// Rows for <c>sys.dm_exec_connections</c> — one per live connection on the
    /// simulation, from the <see cref="Network.ConnectionTransport"/> each
    /// session rides.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDmExecConnections(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullDateTime = SqlValue.Null(SqlType.DateTime);
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
        {
            var transport = connection.Transport;
            var spid = SqlValue.FromInt32(connection.Spid);
            SqlValue Ticks(long ticks) => ticks == 0 ? nullDateTime : SqlValue.FromDateTime(new DateTime(ticks, DateTimeKind.Utc));
            if (transport.Client is not { } client)
            {
                yield return [
                    spid, spid, SqlValue.FromDateTime(transport.ConnectTimeUtc),
                    SqlValue.FromNVarchar("Shared memory"), SqlValue.FromNVarchar("TSQL"), nullInt, SqlValue.FromInt32(2),
                    SqlValue.FromNVarchar("FALSE"), SqlValue.FromNVarchar("SQL"), SqlValue.FromInt16(0),
                    nullInt, nullInt, nullDateTime, nullDateTime, nullInt,
                    SqlValue.FromNVarchar("<local machine>"), nullInt, SqlValue.Null(SqlType.NVarchar), nullInt,
                    SqlValue.FromGuid(transport.ConnectionId), SqlValue.Null(SqlType.UniqueIdentifier), SqlHandleValue(connection.Session.BatchText),
                ];
                continue;
            }
            yield return [
                spid, spid, SqlValue.FromDateTime(transport.ConnectTimeUtc),
                SqlValue.FromNVarchar("TCP"), SqlValue.FromNVarchar("TSQL"), SqlValue.FromInt32(unchecked((int)transport.ProtocolVersion)), SqlValue.FromInt32(4),
                SqlValue.FromNVarchar("TRUE"), SqlValue.FromNVarchar("SQL"), SqlValue.FromInt16(0),
                SqlValue.FromInt32(Volatile.Read(ref transport.PacketsRead)), SqlValue.FromInt32(Volatile.Read(ref transport.PacketsWritten)),
                Ticks(Interlocked.Read(ref transport.LastReadTicks)), Ticks(Interlocked.Read(ref transport.LastWriteTicks)),
                SqlValue.FromInt32(transport.PacketSize),
                SqlValue.FromNVarchar(client.Address.ToString()), SqlValue.FromInt32(client.Port),
                transport.Local is { } local ? SqlValue.FromNVarchar(local.Address.ToString()) : SqlValue.Null(SqlType.NVarchar),
                transport.Local is { } localPort ? SqlValue.FromInt32(localPort.Port) : nullInt,
                SqlValue.FromGuid(transport.ConnectionId), SqlValue.Null(SqlType.UniqueIdentifier), SqlHandleValue(connection.Session.BatchText),
            ];
        }
    }

    /// <summary>
    /// The SQL handle of a command's text, in real's 44-byte shape: the
    /// ad hoc type byte 2, three zero bytes, the batch's object id
    /// (<see cref="AdHocObjectIdOf"/>, little-endian), the MD5 of the text's
    /// UTF-16 bytes and 20 zero bytes (probed 2026-10-06 against SQL Server
    /// 2025). The MD5 bytes match real's; the object id doesn't.
    /// </summary>
    internal static byte[] SqlHandleOf(string text)
    {
        var handle = new byte[SqlHandleLength];
        handle[0] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(handle.AsSpan(4), AdHocObjectIdOf(text));
        // Real's own derivation, reproduced for the matching bytes rather than for security.
#pragma warning disable CA5351
        _ = System.Security.Cryptography.MD5.HashData(MemoryMarshal.AsBytes(text.AsSpan()), handle.AsSpan(8, 16));
#pragma warning restore CA5351
        return handle;
    }

    /// <summary>
    /// The object id real gives an ad hoc batch, which <c>@@PROCID</c> reads
    /// outside a module and its SQL handle carries: a hash of the batch text,
    /// the same for equal texts in any database or session and below 2^30
    /// (probed 2026-10-06 against SQL Server 2025). Real's hash function isn't
    /// recovered, so the value here is a stand-in of the same shape — derived
    /// from the text's SHA-256 — that matches real's in everything but its
    /// digits.
    /// </summary>
    internal static int AdHocObjectIdOf(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        _ = System.Security.Cryptography.SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan()), hash);
        return (BinaryPrimitives.ReadInt32LittleEndian(hash) & 0x3FFFFFFF) is var id and not 0 ? id : 1;
    }

    /// <summary>The length of a SQL handle, below which <c>sys.dm_exec_sql_text</c> refuses one as invalid.</summary>
    internal const int SqlHandleLength = 44;

    private static SqlValue SqlHandleValue(string? text) =>
        text is null ? SqlValue.Null(SqlType.Varbinary) : SqlValue.FromVarbinary(SqlHandleOf(text));

    /// <summary>
    /// Rows for <c>sys.dm_exec_requests</c> — the querying session's, then each
    /// other session mid-statement or blocked, in session order, each with its
    /// MARS requests parked between statements beside it in request order.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDmExecRequests(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var simulation = batch.Connection.Simulation;
        var connections = simulation.SnapshotConnections();
        Array.Sort(connections, static (a, b) => a.Spid.CompareTo(b.Spid));
        var now = DateTime.UtcNow;
        foreach (var connection in connections)
        {
            var session = connection.Session;
            var isSelf = ReferenceEquals(connection, batch.Connection);
            var lockWait = connection.WaitingOnResource is { } resource && connection.WaitingForMode is { } mode ? (resource, mode) : ((LockResource, LockMode)?)null;
            var inWaitFor = session.InWaitFor;
            // A MARS request parked between its statements while another runs
            // waits on its client to read what it sent (probed 2026-10-06
            // against SQL Server 2025).
            var parked = connection.ParkedRequests();
            Array.Sort(parked, static (a, b) => a.RequestId.CompareTo(b.RequestId));
            var awaitingClient = !isSelf && lockWait is null && session.AwaitingClientRequest >= 0 ? session.AwaitingClientRequest : -1;
            var running = isSelf || session.CurrentExecutingThreadId is not null || lockWait is not null || awaitingClient >= 0;
            var runningId = awaitingClient >= 0 ? awaitingClient : connection.ExecutingRequest?.RequestId ?? 0;
            var next = 0;
            foreach (var request in parked)
            {
                if (running && request.RequestId > runningId)
                    break;
                yield return RequestRow(simulation, connection, now, request);
                next++;
            }
            if (running)
            {
                var waitType = lockWait is var (_, waitMode) ? LockDmvs.WaitType(waitMode)
                    : inWaitFor ? "WAITFOR"
                    : awaitingClient >= 0 ? "ASYNC_NETWORK_IO"
                    : null;
                var blocker = lockWait is var (waitResource, _) ? LockDmvs.FindFirstBlocker(waitResource, connection) ?? 0 : 0;
                var waitMillis = waitType is null ? 0 : (int)Math.Min(int.MaxValue, Math.Max(0, Environment.TickCount64 - (lockWait is not null && connection.WaitRecord is { } record ? record.WaitStartedTicks : session.WaitStartedTicks)));
                var start = session.RequestStartUtc == default ? connection.LoginTimeUtc : session.RequestStartUtc;
                yield return RequestRow(
                    simulation, connection, now, runningId, start,
                    isSelf ? "running" : waitType is not null ? "suspended" : "runnable",
                    session.CurrentCommand, session.BatchText, session.StatementStartIndex,
                    waitType, waitMillis, blocker,
                    lockWait is var (describedResource, _) ? LockDmvs.DescribeResource(simulation, describedResource) : "",
                    SessionSettings.Capture(connection), connection.OpenTransactionCount, connection.NestingLevel);
            }
            for (; next < parked.Length; next++)
                yield return RequestRow(simulation, connection, now, parked[next]);
        }
    }

    /// <summary>The <c>sys.dm_exec_requests</c> row of a MARS request parked between its statements.</summary>
    private static SqlValue[] RequestRow(Simulation simulation, SimulatedDbConnection connection, DateTime now, SessionRequest request) =>
        RequestRow(
            simulation, connection, now, request.RequestId, request.RequestStartUtc == default ? connection.LoginTimeUtc : request.RequestStartUtc,
            "suspended", request.CurrentCommand ?? "SELECT", request.BatchText, request.StatementStartIndex,
            "ASYNC_NETWORK_IO", 0, 0, "", request.Settings ?? SessionSettings.Capture(connection), request.Transaction?.TranCount ?? 0, 0);

    /// <summary>One <c>sys.dm_exec_requests</c> row, the request's options read from <paramref name="settings"/>.</summary>
    private static SqlValue[] RequestRow(
        Simulation simulation,
        SimulatedDbConnection connection,
        DateTime now,
        int requestId,
        DateTime start,
        string status,
        string command,
        string? batchText,
        int statementStart,
        string? waitType,
        int waitMillis,
        int blocker,
        string waitResource,
        SessionSettings settings,
        int tranCount,
        int nestingLevel)
    {
        var zero = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        var bitOn = SqlValue.FromBoolean(true);
        var bitOff = SqlValue.FromBoolean(false);
        var nullBinary = SqlValue.Null(SqlType.Varbinary);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var options = settings.Options;
        return [
            SqlValue.FromInt16((short)connection.Spid),
            SqlValue.FromInt32(requestId),
            SqlValue.FromDateTime(start),
            SqlValue.FromNVarchar(status),
            SqlValue.FromNVarchar(command),
            SqlHandleValue(batchText),
            batchText is null ? nullInt : SqlValue.FromInt32(statementStart * 2),
            batchText is null ? nullInt : SqlValue.FromInt32(-1),
            nullBinary,
            SqlValue.FromInt16(SessionDatabaseId(simulation, settings.Database)),
            SqlValue.FromInt32(connection.Security.Effective.DatabasePrincipalId),
            SqlValue.FromGuid(connection.Transport.ConnectionId),
            SqlValue.FromInt16((short)blocker),
            waitType is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(waitType),
            SqlValue.FromInt32(waitMillis),
            SqlValue.FromNVarchar(waitType ?? ""),
            SqlValue.FromNVarchar(waitResource),
            SqlValue.FromInt32(tranCount),
            SqlValue.FromInt32(1),
            zeroBig,
            SqlValue.FromVarbinary(settings.ContextInfo ?? []),
            SqlValue.FromSingle(0),
            zeroBig,
            zero,
            SqlValue.FromInt32((int)Math.Min(int.MaxValue, Math.Max(0, (now - start).TotalMilliseconds))),
            SqlValue.FromInt32(1),
            nullBinary,
            zeroBig, zeroBig, zeroBig,
            SqlValue.FromInt32(settings.TextSize),
            SqlValue.FromNVarchar(options.Language.Name),
            SqlValue.FromNVarchar(options.DateFormat.Name),
            SqlValue.FromInt16(options.DateFirst),
            settings.QuotedIdentifiers ? bitOn : bitOff,
            options.Arithabort ? bitOn : bitOff,
            options.AnsiNullDefaultOn ? bitOn : bitOff,
            AnsiDefaultsAllOn(options, settings.QuotedIdentifiers) ? bitOn : bitOff,
            options.AnsiWarnings ? bitOn : bitOff,
            options.AnsiPadding ? bitOn : bitOff,
            options.AnsiNulls ? bitOn : bitOff,
            options.ConcatNullYieldsNull ? bitOn : bitOff,
            SqlValue.FromInt16(SessionIsolationLevelId(options.IsolationLevel)),
            SqlValue.FromInt32(options.LockTimeoutMillis),
            SqlValue.FromInt32(options.DeadlockPriority),
            SqlValue.FromInt64(settings.RowCount),
            SqlValue.FromInt32(settings.ErrorNumber),
            SqlValue.FromInt32(nestingLevel),
            zero,
            bitOff,
            SqlValue.FromInt32(2),
            SqlValue.Null(SqlType.GetBinary(8)), SqlValue.Null(SqlType.GetBinary(8)), nullBinary, SqlValue.Null(SqlType.BigInt),
            SqlValue.FromInt32(1),
            nullInt,
            SqlValue.Null(SqlType.UniqueIdentifier),
            bitOff,
            nullBinary,
            zeroBig,
            SqlValue.FromGuid(Guid.Empty),
            SqlValue.Null(SqlType.NVarchar),
        ];
    }

    /// <summary>The <c>database_id</c> of the database a session is pointed at.</summary>
    private static short SessionDatabaseId(Simulation simulation, SimulatedDbConnection connection) =>
        SessionDatabaseId(simulation, connection.CurrentDatabase);

    /// <summary>The <c>database_id</c> of <paramref name="database"/>, 1 when it's gone.</summary>
    private static short SessionDatabaseId(Simulation simulation, Database database)
    {
        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
        {
            if (ReferenceEquals(db, database))
                return id;
        }
        return 1;
    }

    /// <summary>A session's isolation level as the DMVs number it: 1–5 for read uncommitted through snapshot.</summary>
    private static short SessionIsolationLevelId(System.Data.IsolationLevel isolationLevel) => isolationLevel switch
    {
        System.Data.IsolationLevel.ReadUncommitted => 1,
        System.Data.IsolationLevel.RepeatableRead => 3,
        System.Data.IsolationLevel.Serializable => 4,
        System.Data.IsolationLevel.Snapshot => 5,
        _ => 2,
    };

    /// <summary>
    /// The <c>ansi_defaults</c> column: whether all seven options
    /// <c>SET ANSI_DEFAULTS</c> sets are on — it reads 0 again once any one of
    /// them is turned off (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private static bool AnsiDefaultsAllOn(SimulatedDbConnection.SessionOptionScope options, bool quotedIdentifiers) =>
        options.AnsiNulls && options.AnsiNullDefaultOn && options.AnsiPadding && options.AnsiWarnings
        && options.CursorCloseOnCommit && options.ImplicitTransactions && quotedIdentifiers;

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
            // A MARS session reports the settings its last finished request
            // left, not those a running one is changing.
            var published = connection.PublishedSettings;
            var options = published?.Options ?? new SimulatedDbConnection.SessionOptionScope(connection);
            var quotedIdentifiers = ReferenceEquals(connection, batch.Connection)
                ? batch.Parser.QuotedIdentifiersAtBatchStart
                : published?.QuotedIdentifiers ?? connection.QuotedIdentifiers;
            var loginTime = SqlValue.FromDateTime(connection.LoginTimeUtc);
            var databaseId = SessionDatabaseId(simulation, published?.Database ?? connection.CurrentDatabase);
            var isolation = SessionIsolationLevelId(options.IsolationLevel);
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
                SqlValue.FromString(NVarcharSqlType.Get(30, Collation.Catalog, Coercibility.Implicit), connection.RunningLogonTriggers ? "preconnect" : ReferenceEquals(connection, batch.Connection) ? "running" : "sleeping"),
                SqlValue.FromVarbinary((published is null ? connection.ReportedContextInfo : published.ContextInfo) ?? []),
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
                SqlValue.FromInt32(published?.TextSize ?? connection.TextSize),
                SqlValue.FromNVarchar(options.Language.Name),
                SqlValue.FromNVarchar(options.DateFormat.Name),
                SqlValue.FromInt16(options.DateFirst),
                quotedIdentifiers ? bitOn : bitOff,
                options.Arithabort ? bitOn : bitOff,
                options.AnsiNullDefaultOn ? bitOn : bitOff,
                AnsiDefaultsAllOn(options, quotedIdentifiers) ? bitOn : bitOff,
                options.AnsiWarnings ? bitOn : bitOff,
                options.AnsiPadding ? bitOn : bitOff,
                options.AnsiNulls ? bitOn : bitOff,
                options.ConcatNullYieldsNull ? bitOn : bitOff,
                SqlValue.FromInt16(isolation),
                SqlValue.FromInt32(options.LockTimeoutMillis),
                SqlValue.FromInt32(options.DeadlockPriority),
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
                SqlValue.FromInt32(connection.OpenTransactionCount),
                zeroBig,
                SqlValue.Null(SqlType.UniqueIdentifier),
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
    /// Rows for <c>sys.master_files</c>: every file of every database, join
    /// key <c>database_id</c>, from <see cref="Database.Files"/>. There are no
    /// <c>type</c>-2 (FILESTREAM / memory-optimized) files, so SSMS's
    /// in-memory-OLTP probe (<c>where mf.[type] = 2</c>) returns nothing.
    /// <c>max_size</c> / <c>growth</c> are 8 KB pages whenever
    /// <c>is_percent_growth</c> is 0, and a log file's unlimited ceiling reads
    /// as its 2 TB cap. All LSN columns surface NULL (no physical log).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysMasterFiles(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullTime = SqlValue.Null(SqlType.DateTime);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullLsn = SqlValue.Null(SqlType.GetDecimal(25, 0));

        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            foreach (var file in db.FilesInOrder())
            {
                var common = FileRowCells(db, file);
                yield return
                [
                    SqlValue.FromInt32(id),
                    .. common,
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
            }
        }
    }

    /// <summary>
    /// The cells <c>sys.master_files</c> reports for a file from
    /// <c>file_id</c> through <c>is_persistent_log_buffer</c>;
    /// <c>sys.database_files</c> takes all but that last one. A file of a <c>READ_ONLY</c> filegroup reports <c>is_read_only</c>
    /// (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static SqlValue[] FileRowCells(Database database, DatabaseFile file)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var readOnly = !file.IsLog && database.IsFilegroupReadOnly(file.DataSpaceId);
        return
        [
            SqlValue.FromInt32(file.FileId),
            SqlValue.Null(SqlType.UniqueIdentifier),
            SqlValue.FromByte(file.IsContainer ? (byte)2 : file.IsLog ? (byte)1 : (byte)0),
            SqlValue.FromNVarchar(file.IsContainer ? "FILESTREAM" : file.IsLog ? "LOG" : "ROWS"),
            SqlValue.FromInt32(file.DataSpaceId),
            SqlValue.FromNVarchar(file.Name),
            SqlValue.FromNVarchar(file.PhysicalName),
            SqlValue.FromByte(0),
            SqlValue.FromNVarchar("ONLINE"),
            SqlValue.FromInt32(FileSizePages(database, file)),
            SqlValue.FromInt32(ReportedMaxSizePages(file)),
            SqlValue.FromInt32(file.Growth),
            falseBit,
            SqlValue.FromBoolean(readOnly),
            falseBit,
            SqlValue.FromBoolean(file.IsPercentGrowth),
            falseBit,
            falseBit,
        ];
    }

    /// <summary>
    /// Rows for <c>sys.database_files</c> — the resolved
    /// <paramref name="database"/>'s slice of <see cref="EnumerateSysMasterFiles"/>.
    /// There is no <c>database_id</c> column (the view is implicitly
    /// current-database), so a three-part <c>master.sys.database_files</c>
    /// read returns master's files.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseFiles(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var nullLsn = SqlValue.Null(lsnNumeric);
        foreach (var file in database.FilesInOrder())
        {
            var common = FileRowCells(database, file);
            yield return [.. common.AsSpan(0, common.Length - 1), nullLsn];
        }
    }

    /// <summary>
    /// Rows for <c>sys.servers</c>. Row 0 is the local instance
    /// (<c>is_linked = 0</c>, name and data source <c>"SIMULATED"</c>,
    /// product <c>"SQL Server"</c>, provider <c>"SQLNCLI"</c>, as real
    /// reports its own row, probed 2026-09-25); each subsequent row is one entry from
    /// <see cref="Simulation.ActiveLinkedServers"/> in name-sort order
    /// (stable across runs, distinct from real SQL Server's
    /// <c>object_id</c>-derived ordering — see the quirks list).
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateSysServers(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var yes = SqlValue.FromBoolean(true);
        var no = SqlValue.FromBoolean(false);
        var zero = SqlValue.FromInt32(0);
        var nullNVarchar = SqlValue.Null(SqlType.NVarchar);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var simulation = batch.Connection.Simulation;

        // Real's flags for its own row and a linked one (probed 2026-09-26
        // against SQL Server 2025); sp_serveroption sets the linked server's
        // rpc out, data access and remote proc transaction promotion.
        SqlValue[] Row(int serverId, string name, string product, string provider, string? dataSource, string? location, string? providerString, string? catalog,
            bool linked, bool remoteLogin, bool rpcOut, bool dataAccess, bool promotion, DateTime modifyDate, LinkedServer? server = null) =>
        [
            SqlValue.FromInt32(serverId),
            SqlValue.FromSystemName(name),
            SqlValue.FromSystemName(product),
            SqlValue.FromSystemName(provider),
            dataSource is null ? nullNVarchar : SqlValue.FromNVarchar(dataSource),
            location is null ? nullNVarchar : SqlValue.FromNVarchar(location),
            providerString is null ? nullNVarchar : SqlValue.FromNVarchar(providerString),
            catalog is null ? nullSysName : SqlValue.FromSystemName(catalog),
            server is null ? zero : SqlValue.FromInt32(server.ConnectTimeout),
            server is null ? zero : SqlValue.FromInt32(server.QueryTimeout),
            linked ? yes : no,
            remoteLogin ? yes : no,
            rpcOut ? yes : no,
            dataAccess ? yes : no,
            server?.CollationCompatible == true ? yes : no,
            server?.UseRemoteCollation == false ? no : yes,
            server?.CollationName is { } collationName ? SqlValue.FromSystemName(collationName) : nullSysName,
            server?.LazySchemaValidation == true ? yes : no,
            server?.IsSystem == true ? yes : no,
            server?.Publisher == true ? yes : no,
            server?.Subscriber == true ? yes : no,
            server?.Distributor == true ? yes : no,
            no,
            promotion ? yes : no,
            SqlValue.FromDateTime(modifyDate),
            no,
        ];

        yield return Row(0, "SIMULATED", "SQL Server", "SQLNCLI", "SIMULATED", null, null, null, linked: false, remoteLogin: true, rpcOut: true, dataAccess: false, promotion: true, simulation.SeedDate);
        var serverId = 1;
        foreach (var ls in simulation.ActiveLinkedServers.EnumerateValues().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return Row(serverId++, ls.Name, ls.SrvProduct, ls.Provider, ls.DataSource, ls.Location, ls.ProviderString, ls.Catalog,
                linked: true, remoteLogin: ls.RemoteLogin, ls.RpcOut, ls.DataAccess, ls.RemoteProcTransactionPromotion, ls.CreateDate, ls);
        }
    }

    /// <summary>
    /// Rows for <c>sysservers</c>, one per <c>sys.servers</c> row: the options
    /// as <c>srvstatus</c> bits — rpc 1, pub 2, sub 4, dist 8, a linked server
    /// 32, rpc out 64, data access 128, collation compatible 256, system 512,
    /// use remote collation 1024, lazy schema validation 2048 — and as one
    /// column each, <c>srvnetname</c> the instance's own name padded, and an
    /// <c>SQLNCLI</c> provider named <c>SQLOLEDB</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysservers(Parser.BatchContext batch, Database database)
    {
        var zero = SqlValue.FromInt32(0);
        var no = SqlValue.FromBoolean(false);
        var nullName = SqlValue.Null(NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit));
        var netNameType = CharSqlType.Get(30, Collation.Catalog, Coercibility.Implicit);
        foreach (var row in EnumerateSysServers(batch, database))
        {
            var linked = row[10].AsBoolean;
            var flags = new[] { row[11], row[19], row[20], row[21], no, row[12], row[13], row[14], row[18], row[15], row[17] };
            var status = (linked ? 32 : 0)
                | (row[11].AsBoolean ? 1 : 0) | (row[19].AsBoolean ? 2 : 0) | (!row[20].IsNull && row[20].AsBoolean ? 4 : 0) | (!row[21].IsNull && row[21].AsBoolean ? 8 : 0)
                | (row[12].AsBoolean ? 64 : 0) | (row[13].AsBoolean ? 128 : 0) | (row[14].AsBoolean ? 256 : 0) | (row[18].AsBoolean ? 512 : 0)
                | (row[15].AsBoolean ? 1024 : 0) | (row[17].AsBoolean ? 2048 : 0);
            var provider = row[3].AsString;
            yield return
            [
                SqlValue.FromInt16((short)row[0].AsInt32),
                SqlValue.FromInt16((short)status),
                row[1],
                row[2],
                provider.StartsWith("SQLNCLI", StringComparison.OrdinalIgnoreCase) ? SqlValue.FromNVarchar("SQLOLEDB") : row[3],
                row[4],
                row[5],
                row[6],
                row[24],
                zero,
                zero,
                row[7],
                row[16],
                row[8],
                row[9],
                linked ? SqlValue.Null(netNameType) : SqlValue.FromString(netNameType, row[1].AsString),
                SqlValue.FromBoolean(!linked),
                .. flags,
                row[16].IsNull ? nullName : row[16],
                no,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.linked_logins</c>: each linked server's mappings, under
    /// the <c>server_id</c> <see cref="EnumerateSysServers"/> gives it.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysLinkedLogins(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var serverId = 1;
        foreach (var server in batch.Connection.Simulation.ActiveLinkedServers.EnumerateValues().OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = SqlValue.FromInt32(serverId++);
            foreach (var login in server.Logins.OrderBy(static login => login.LocalPrincipalId))
            {
                yield return
                [
                    id,
                    SqlValue.FromInt32(login.LocalPrincipalId),
                    SqlValue.FromBoolean(login.UsesSelf),
                    login.RemoteName is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(login.RemoteName),
                    SqlValue.FromDateTime(login.ModifyDate),
                ];
            }
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

    /// <summary>
    /// The system endpoints' rows — every one for <c>sys.endpoints</c>, the TCP
    /// pair with their port columns appended for <c>sys.tcp_endpoints</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> SystemEndpointRows(bool tcpOnly)
    {
        (int Id, string Name, byte Protocol, string ProtocolDesc)[] endpoints =
        [
            (1, "Dedicated Admin Connection", 2, "TCP"),
            (2, "TSQL Local Machine", 4, "SHARED_MEMORY"),
            (3, "TSQL Named Pipes", 3, "NAMED_PIPES"),
            (4, "TSQL Default TCP", 2, "TCP"),
            (5, "TSQL Default VIA", 5, "VIA"),
        ];
        foreach (var (id, name, protocol, protocolDesc) in endpoints)
        {
            if (tcpOnly && protocol != 2)
                continue;
            SqlValue[] row =
            [
                SqlValue.FromSystemName(name),
                SqlValue.FromInt32(id),
                SqlValue.FromInt32(1),
                SqlValue.FromByte(protocol),
                SqlValue.FromNVarchar(protocolDesc),
                SqlValue.FromByte(2),
                SqlValue.FromNVarchar("TSQL"),
                SqlValue.FromByte(0),
                SqlValue.FromNVarchar("STARTED"),
                SqlValue.FromBoolean(id == 1),
            ];
            yield return tcpOnly
                ? [.. row, SqlValue.FromInt32(0), SqlValue.FromBoolean(true), SqlValue.Null(VarcharSqlType.Get(45, Collation.Catalog, Coercibility.Implicit))]
                : row;
        }
    }
}
