using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The msdb tables of the backup and restore history, which BACKUP and RESTORE
// write, and log shipping's, which stays empty, seeded at construction in SQL
// Server 2025's column shapes (probed 2026-10-16); the Agent tables beside
// them are SeedMsdbHistoryAndAgentTables'.
partial class Simulation
{
    /// <summary>
    /// The seeded msdb tables, each column as <c>name type</c> with an
    /// optional trailing <c>null</c> (nullable) or <c>identity</c>
    /// (<c>IDENTITY(1, 1)</c>, not nullable).
    /// </summary>
    private static readonly (string Name, string[] Columns)[] MsdbTables =
    [
        ("backupset", [
            "backup_set_id int identity", "backup_set_uuid uniqueidentifier", "media_set_id int",
            "first_family_number tinyint null", "first_media_number smallint null", "last_family_number tinyint null",
            "last_media_number smallint null", "catalog_family_number tinyint null", "catalog_media_number smallint null",
            "position int null", "expiration_date datetime null", "software_vendor_id int null", "name nvarchar(128) null",
            "description nvarchar(255) null", "user_name nvarchar(128) null", "software_major_version tinyint null",
            "software_minor_version tinyint null", "software_build_version smallint null", "time_zone smallint null",
            "mtf_minor_version tinyint null", "first_lsn numeric(25,0) null", "last_lsn numeric(25,0) null",
            "checkpoint_lsn numeric(25,0) null", "database_backup_lsn numeric(25,0) null", "database_creation_date datetime null",
            "backup_start_date datetime null", "backup_finish_date datetime null", "type char(1) null", "sort_order smallint null",
            "code_page smallint null", "compatibility_level tinyint null", "database_version int null",
            "backup_size numeric(20,0) null", "database_name nvarchar(128) null", "server_name nvarchar(128) null",
            "machine_name nvarchar(128) null", "flags int null", "unicode_locale int null", "unicode_compare_style int null",
            "collation_name nvarchar(128) null", "is_password_protected bit null", "recovery_model nvarchar(60) null",
            "has_bulk_logged_data bit null", "is_snapshot bit null", "is_readonly bit null", "is_single_user bit null",
            "has_backup_checksums bit null", "is_damaged bit null", "begins_log_chain bit null",
            "has_incomplete_metadata bit null", "is_force_offline bit null", "is_copy_only bit null",
            "first_recovery_fork_guid uniqueidentifier null", "last_recovery_fork_guid uniqueidentifier null",
            "fork_point_lsn numeric(25,0) null", "database_guid uniqueidentifier null", "family_guid uniqueidentifier null",
            "differential_base_lsn numeric(25,0) null", "differential_base_guid uniqueidentifier null",
            "compressed_backup_size numeric(20,0) null", "key_algorithm nvarchar(32) null", "encryptor_thumbprint varbinary(20) null",
            "encryptor_type nvarchar(32) null", "last_valid_restore_time datetime null", "compression_algorithm nvarchar(32) null",
        ]),
        ("backupfile", [
            "backup_set_id int", "first_family_number tinyint null", "first_media_number smallint null",
            "filegroup_name nvarchar(128) null", "page_size int null", "file_number numeric(10,0)",
            "backed_up_page_count numeric(10,0) null", "file_type char(1) null", "source_file_block_size numeric(10,0) null",
            "file_size numeric(20,0) null", "logical_name nvarchar(128) null", "physical_drive nvarchar(260) null",
            "physical_name nvarchar(260) null", "state tinyint null", "state_desc nvarchar(64) null", "create_lsn numeric(25,0) null",
            "drop_lsn numeric(25,0) null", "file_guid uniqueidentifier null", "read_only_lsn numeric(25,0) null",
            "read_write_lsn numeric(25,0) null", "differential_base_lsn numeric(25,0) null",
            "differential_base_guid uniqueidentifier null", "backup_size numeric(20,0) null", "filegroup_guid uniqueidentifier null",
            "is_readonly bit null", "is_present bit null",
        ]),
        ("backupfilegroup", [
            "backup_set_id int", "name nvarchar(128)", "filegroup_id int", "filegroup_guid uniqueidentifier null", "type char(2)",
            "type_desc nvarchar(60)", "is_default bit", "is_readonly bit", "log_filegroup_guid uniqueidentifier null",
        ]),
        ("backupmediafamily", [
            "media_set_id int", "family_sequence_number tinyint", "media_family_id uniqueidentifier null", "media_count int null",
            "logical_device_name nvarchar(128) null", "physical_device_name nvarchar(260) null", "device_type tinyint null",
            "physical_block_size int null", "mirror tinyint",
        ]),
        ("backupmediaset", [
            "media_set_id int identity", "media_uuid uniqueidentifier null", "media_family_count tinyint null",
            "name nvarchar(128) null", "description nvarchar(255) null", "software_name nvarchar(128) null",
            "software_vendor_id int null", "MTF_major_version tinyint null", "mirror_count tinyint null",
            "is_password_protected bit null", "is_compressed bit null", "is_encrypted bit null",
        ]),
        ("logmarkhistory", [
            "database_name nvarchar(128)", "mark_name nvarchar(128)", "description nvarchar(255) null", "user_name nvarchar(128)",
            "lsn numeric(25,0)", "mark_time datetime",
        ]),
        ("log_shipping_primary_databases", [
            "primary_id uniqueidentifier", "primary_database sysname", "backup_directory nvarchar(500)",
            "backup_share nvarchar(500)", "backup_retention_period int", "backup_job_id uniqueidentifier", "monitor_server sysname",
            "user_specified_monitor bit null", "monitor_server_security_mode bit", "last_backup_file nvarchar(500) null",
            "last_backup_date datetime null", "backup_compression tinyint", "primary_connection_options nvarchar(4000) null",
            "monitor_connection_options nvarchar(4000) null",
        ]),
        ("log_shipping_secondary_databases", [
            "secondary_database sysname", "secondary_id uniqueidentifier", "restore_delay int", "restore_all bit",
            "restore_mode bit", "disconnect_users bit", "block_size int null", "buffer_count int null",
            "max_transfer_size int null", "last_restored_file nvarchar(500) null", "last_restored_date datetime null",
        ]),
        ("restorefile", [
            "restore_history_id int", "file_number numeric(10,0) null", "destination_phys_drive nvarchar(260) null",
            "destination_phys_name nvarchar(260) null",
        ]),
        ("restorefilegroup", ["restore_history_id int", "filegroup_name nvarchar(128) null"]),
        ("restorehistory", [
            "restore_history_id int identity", "restore_date datetime null", "destination_database_name nvarchar(128) null",
            "user_name nvarchar(128) null", "backup_set_id int", "restore_type char(1) null", "replace bit null",
            "recovery bit null", "restart bit null", "stop_at datetime null", "device_count tinyint null",
            "stop_at_mark_name nvarchar(128) null", "stop_before bit null",
        ]),
    ];

    /// <summary>
    /// Seeds <see cref="MsdbTables"/> into msdb's <c>dbo</c> schema, empty.
    /// Constructed directly so no connection is materialized at construction.
    /// </summary>
    private static void SeedMsdbTables(Database msdb)
    {
        var schema = msdb.Schemas[Database.DefaultSchemaName];
        foreach (var (name, specs) in MsdbTables)
        {
            var columns = new HeapColumn[specs.Length];
            for (var i = 0; i < specs.Length; i++)
                columns[i] = MsdbColumn(msdb, specs[i]);
            schema.HeapTables[name] = new HeapTable(name, columns, msdb.AllocateObjectId()) { OwningDatabase = msdb, IsMsShipped = true };
        }
    }

    /// <summary>One <see cref="MsdbTables"/> column spec as its column.</summary>
    private static HeapColumn MsdbColumn(Database msdb, string spec)
    {
        var parts = spec.Split(' ');
        var nullable = parts is [_, _, "null"];
        var identity = parts is [_, _, "identity"] ? new IdentityState(1, 1) : null;
        var type = parts[1];
        var open = type.IndexOf('(', StringComparison.Ordinal);
        var baseName = open < 0 ? type : type[..open];
        var arguments = open < 0 ? [] : type[(open + 1)..^1].Split(',');
        var length = arguments is [var only] ? only == "max" ? SqlType.MaxLengthSentinel : int.Parse(only, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        var sqlType = baseName switch
        {
            "bit" => SqlType.Bit,
            "char" => CharSqlType.Get(length!.Value, msdb.Collation, Coercibility.Implicit),
            "datetime" => SqlType.DateTime,
            "int" => SqlType.Int32,
            "numeric" => SqlType.GetDecimal(byte.Parse(arguments[0], System.Globalization.CultureInfo.InvariantCulture), byte.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture)),
            "nvarchar" => NVarcharSqlType.Get(length!.Value, msdb.Collation, Coercibility.Implicit),
            "smallint" => SqlType.SmallInt,
            "sysname" => SqlType.SystemName,
            "tinyint" => SqlType.TinyInt,
            "uniqueidentifier" => SqlType.UniqueIdentifier,
            "varbinary" => VarbinarySqlType.Get(length!.Value),
            "varchar" => VarcharSqlType.Get(length!.Value, msdb.Collation, Coercibility.Implicit),
            _ => throw new InvalidOperationException(spec),
        };
        return new HeapColumn(parts[0], sqlType, baseName == "sysname" ? 128 : baseName == "numeric" ? null : length, nullable, identity, spelledNumeric: baseName == "numeric");
    }
}
