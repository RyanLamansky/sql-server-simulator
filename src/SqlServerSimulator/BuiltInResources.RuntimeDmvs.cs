using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The process, session-compatibility and version-store runtime views SMO's
// Server and Database property bags join, each with SQL Server 2025's column
// shape (probed 2026-09-30).
internal static partial class BuiltInResources
{
    private static void RegisterRuntimeDmvs(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        // sys.dm_os_process_memory: the engine process's memory, which is the
        // host process's — the same reading SERVERPROPERTY('ProcessID') makes.
        Sys("dm_os_process_memory",
        [
            new("physical_memory_in_use_kb", SqlType.BigInt, null, false),
            new("large_page_allocations_kb", SqlType.BigInt, null, false),
            new("locked_page_allocations_kb", SqlType.BigInt, null, false),
            new("total_virtual_address_space_kb", SqlType.BigInt, null, false),
            new("virtual_address_space_reserved_kb", SqlType.BigInt, null, false),
            new("virtual_address_space_committed_kb", SqlType.BigInt, null, false),
            new("virtual_address_space_available_kb", SqlType.BigInt, null, false),
            new("page_fault_count", SqlType.BigInt, null, false),
            new("memory_utilization_percentage", SqlType.Int32, null, false),
            new("available_commit_limit_kb", SqlType.BigInt, null, false),
            new("process_physical_memory_low", SqlType.Bit, null, false),
            new("process_virtual_memory_low", SqlType.Bit, null, false),
        ], static (batch, database) => DmOsProcessMemoryRows());

        // sys.dm_database_encryption_keys: Transparent Data Encryption isn't
        // modeled, so no database has an encryption key.
        Sys("dm_database_encryption_keys",
        [
            new("database_id", SqlType.Int32, null, true),
            new("encryption_state", SqlType.Int32, null, true),
            new("create_date", SqlType.DateTime, null, true),
            new("regenerate_date", SqlType.DateTime, null, true),
            new("modify_date", SqlType.DateTime, null, true),
            new("set_date", SqlType.DateTime, null, true),
            new("opened_date", SqlType.DateTime, null, true),
            new("key_algorithm", SqlType.NVarchar, 128, true),
            new("key_length", SqlType.Int32, null, true),
            new("encryptor_thumbprint", SqlType.Varbinary, 20, true),
            new("encryptor_type", SqlType.NVarchar, 128, true),
            new("percent_complete", SqlType.Real, null, true),
            new("encryption_state_desc", SqlType.NVarchar, 128, true),
            new("encryption_scan_state", SqlType.Int32, null, true),
            new("encryption_scan_state_desc", SqlType.NVarchar, 128, true),
            new("encryption_scan_modify_date", SqlType.DateTime, null, true),
        ], static (batch, database) => []);

        // sys.dm_tran_persistent_version_store_stats: one row per database, its
        // persistent version store on the PRIMARY filegroup and empty — the
        // simulator's row versions live in its own version store, which
        // sys.dm_tran_version_store reports.
        Sys("dm_tran_persistent_version_store_stats",
        [
            new("database_id", SqlType.Int32, null, false),
            new("pvs_filegroup_id", SqlType.SmallInt, null, true),
            new("persistent_version_store_size_kb", SqlType.BigInt, null, false),
            new("online_index_version_store_size_kb", SqlType.BigInt, null, false),
            new("current_aborted_transaction_count", SqlType.BigInt, null, false),
            new("oldest_active_transaction_id", SqlType.BigInt, null, false),
            new("oldest_active_transaction_global_id", SqlType.BigInt, null, false),
            new("oldest_aborted_transaction_id", SqlType.BigInt, null, false),
            new("min_transaction_timestamp", SqlType.BigInt, null, false),
            new("online_index_min_transaction_timestamp", SqlType.BigInt, null, false),
            new("secondary_low_water_mark", SqlType.BigInt, null, false),
            new("offrow_version_cleaner_start_time", SqlType.GetDateTime2(7), null, true),
            new("offrow_version_cleaner_end_time", SqlType.GetDateTime2(7), null, true),
            new("aborted_version_cleaner_start_time", SqlType.GetDateTime2(7), null, true),
            new("aborted_version_cleaner_end_time", SqlType.GetDateTime2(7), null, true),
            new("pvs_off_row_page_skipped_low_water_mark", SqlType.BigInt, null, false),
            new("pvs_off_row_page_skipped_transaction_not_cleaned", SqlType.BigInt, null, false),
            new("pvs_off_row_page_skipped_oldest_active_xdesid", SqlType.BigInt, null, false),
            new("pvs_off_row_page_skipped_min_useful_xts", SqlType.BigInt, null, false),
            new("pvs_off_row_page_skipped_oldest_snapshot", SqlType.BigInt, null, false),
            new("pvs_off_row_page_skipped_oldest_aborted_xdesid", SqlType.BigInt, null, false),
        ], static (batch, database) => PersistentVersionStoreStatsRows(batch.Connection.Simulation));

        // sys.dm_db_file_space_usage: the reading database's data files, each
        // file's used pages allocated in whole extents out of its reported size.
        Sys("dm_db_file_space_usage",
        [
            new("database_id", SqlType.Int32, null, true),
            new("file_id", SqlType.SmallInt, null, true),
            new("filegroup_id", SqlType.SmallInt, null, true),
            new("total_page_count", SqlType.BigInt, null, true),
            new("allocated_extent_page_count", SqlType.BigInt, null, true),
            new("unallocated_extent_page_count", SqlType.BigInt, null, true),
            new("version_store_reserved_page_count", SqlType.BigInt, null, true),
            new("user_object_reserved_page_count", SqlType.BigInt, null, true),
            new("internal_object_reserved_page_count", SqlType.BigInt, null, true),
            new("mixed_extent_page_count", SqlType.BigInt, null, true),
            new("modified_extent_page_count", SqlType.BigInt, null, true),
        ], static (batch, database) => FileSpaceUsageRows(database));

        // sysprocesses: the SQL Server 2000 compatibility view over the
        // sessions, one row per connection as sys.dm_exec_sessions lists them.
        var sysprocesses = new CatalogView("sysprocesses",
        [
            new("spid", SqlType.SmallInt, null, false),
            new("kpid", SqlType.SmallInt, null, false),
            new("blocked", SqlType.SmallInt, null, false),
            new("waittype", BinarySqlType.Get(2), 2, false),
            new("waittime", SqlType.BigInt, null, false),
            new("lastwaittype", NCharSqlType.Get(32, Collation.Catalog, Coercibility.Implicit), 32, false),
            new("waitresource", NCharSqlType.Get(256, Collation.Catalog, Coercibility.Implicit), 256, false),
            new("dbid", SqlType.SmallInt, null, false),
            new("uid", SqlType.SmallInt, null, true),
            new("cpu", SqlType.Int32, null, false),
            new("physical_io", SqlType.BigInt, null, false),
            new("memusage", SqlType.Int32, null, false),
            new("login_time", SqlType.DateTime, null, false),
            new("last_batch", SqlType.DateTime, null, false),
            new("ecid", SqlType.SmallInt, null, false),
            new("open_tran", SqlType.SmallInt, null, false),
            new("status", NCharSqlType.Get(30, Collation.Catalog, Coercibility.Implicit), 30, false),
            new("sid", BinarySqlType.Get(86), 86, false),
            new("hostname", NCharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), 128, false),
            new("program_name", NCharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), 128, false),
            new("hostprocess", NCharSqlType.Get(10, Collation.Catalog, Coercibility.Implicit), 10, false),
            new("cmd", NCharSqlType.Get(26, Collation.Catalog, Coercibility.Implicit), 26, false),
            new("nt_domain", NCharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), 128, false),
            new("nt_username", NCharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), 128, false),
            new("net_address", NCharSqlType.Get(12, Collation.Catalog, Coercibility.Implicit), 12, false),
            new("net_library", NCharSqlType.Get(12, Collation.Catalog, Coercibility.Implicit), 12, false),
            new("loginame", NCharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), 128, false),
            new("context_info", BinarySqlType.Get(128), 128, false),
            new("sql_handle", BinarySqlType.Get(20), 20, false),
            new("stmt_start", SqlType.Int32, null, false),
            new("stmt_end", SqlType.Int32, null, false),
            new("request_id", SqlType.Int32, null, false),
            new("page_resource", SqlType.Varbinary, 8, true),
        ], static (batch, database) => SysprocessesRows(batch));
        views["sysprocesses"] = sysprocesses;
        views["sys.sysprocesses"] = sysprocesses;
    }

    private static IEnumerable<SqlValue[]> DmOsProcessMemoryRows()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var zero = SqlValue.FromInt64(0);
        var off = SqlValue.FromBoolean(false);
        yield return
        [
            SqlValue.FromInt64(process.WorkingSet64 / 1024),
            zero,
            zero,
            SqlValue.FromInt64(process.VirtualMemorySize64 / 1024),
            SqlValue.FromInt64(process.VirtualMemorySize64 / 1024),
            SqlValue.FromInt64(process.PrivateMemorySize64 / 1024),
            zero,
            zero,
            SqlValue.FromInt32(100),
            zero,
            off,
            off,
        ];
    }

    private static IEnumerable<SqlValue[]> FileSpaceUsageRows(Database database)
    {
        var databaseId = SqlValue.FromInt32(database.Id);
        var unreported = SqlValue.Null(SqlType.BigInt);
        var zero = SqlValue.FromInt64(0);
        foreach (var file in database.FilesInOrder())
        {
            if (file.IsLog)
                continue;
            long total = FileSizePages(database, file);
            var used = file.FileId == 1 ? SumDataFilePages(database) : EmptyDataFileUsedPages;
            var allocated = Math.Min(total, (used + 7) / 8 * 8);
            yield return
            [
                databaseId,
                SqlValue.FromInt16((short)file.FileId),
                SqlValue.FromInt16((short)file.DataSpaceId),
                SqlValue.FromInt64(total),
                SqlValue.FromInt64(allocated),
                SqlValue.FromInt64(total - allocated),
                unreported,
                unreported,
                unreported,
                zero,
                zero,
            ];
        }
    }

    private static IEnumerable<SqlValue[]> PersistentVersionStoreStatsRows(Simulation simulation)
    {
        var primary = SqlValue.FromInt16(Database.PrimaryFilegroupId);
        var zero = SqlValue.FromInt64(0);
        var noTime = SqlValue.Null(SqlType.GetDateTime2(7));
        foreach (var (_, id) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
        {
            yield return
            [
                SqlValue.FromInt32(id), primary, zero, zero, zero, zero, zero, zero, zero, zero, zero,
                noTime, noTime, noTime, noTime, zero, zero, zero, zero, zero, zero,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sysprocesses</c>: the reading session <c>runnable</c> on its
    /// <c>SELECT</c>, every other one <c>sleeping</c> and awaiting a command,
    /// with the wait, CPU and I/O counters a session that never waits reads.
    /// </summary>
    private static IEnumerable<SqlValue[]> SysprocessesRows(Parser.BatchContext batch)
    {
        var simulation = batch.Connection.Simulation;
        var zeroSmall = SqlValue.FromInt16(0);
        var zeroInt = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        foreach (var connection in simulation.SnapshotConnections())
        {
            var self = ReferenceEquals(connection, batch.Connection);
            var loginTime = SqlValue.FromDateTime(connection.LoginTimeUtc);
            var login = connection.Security.Effective.LoginName;
            var network = connection.Transport.Client is not null;
            yield return
            [
                SqlValue.FromInt16((short)connection.Spid),
                zeroSmall,
                zeroSmall,
                SqlValue.FromBinary(BinarySqlType.Get(2), []),
                zeroBig,
                nchar(32, "MISCELLANEOUS"),
                nchar(256, string.Empty),
                SqlValue.FromInt16(SessionDatabaseId(simulation, connection)),
                SqlValue.FromInt16((short)connection.Security.Effective.DatabasePrincipalId),
                zeroInt,
                zeroBig,
                zeroInt,
                loginTime,
                loginTime,
                zeroSmall,
                SqlValue.FromInt16((short)connection.OpenTransactionCount),
                nchar(30, self ? "runnable" : "sleeping"),
                SqlValue.FromBinary(BinarySqlType.Get(86), DeriveLoginSid(login)),
                nchar(128, connection.ClientHostName),
                nchar(128, connection.ClientApplicationName),
                nchar(10, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                nchar(26, self ? "SELECT" : "AWAITING COMMAND"),
                nchar(128, string.Empty),
                nchar(128, string.Empty),
                nchar(12, network ? "FFFFFFFFFFFF" : string.Empty),
                nchar(12, network ? "TCP/IP" : string.Empty),
                nchar(128, login),
                SqlValue.FromBinary(BinarySqlType.Get(128), connection.ContextInfo ?? []),
                SqlValue.FromBinary(BinarySqlType.Get(20), []),
                zeroInt,
                zeroInt,
                zeroInt,
                SqlValue.Null(SqlType.Varbinary),
            ];
        }

        static SqlValue nchar(int length, string value) => SqlValue.FromNChar(NCharSqlType.Get(length, Collation.Catalog, Coercibility.Implicit), value);
    }
}
