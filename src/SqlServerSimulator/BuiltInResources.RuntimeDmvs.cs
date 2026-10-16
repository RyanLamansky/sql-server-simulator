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
        // sessions and their requests. Its strings carry the server's
        // collation where the catalog views carry Latin1_General_CI_AS_KS_WS
        // (probed 2026-10-16 against SQL Server 2025), the default server
        // collation here.
        var sysprocesses = new CatalogView("sysprocesses",
        [
            new("spid", SqlType.SmallInt, null, false),
            new("kpid", SqlType.SmallInt, null, false),
            new("blocked", SqlType.SmallInt, null, false),
            new("waittype", BinarySqlType.Get(2), 2, false),
            new("waittime", SqlType.BigInt, null, false),
            new("lastwaittype", NCharSqlType.Get(32, Collation.Baseline, Coercibility.Implicit), 32, false),
            new("waitresource", NCharSqlType.Get(256, Collation.Baseline, Coercibility.Implicit), 256, false),
            new("dbid", SqlType.SmallInt, null, false),
            new("uid", SqlType.SmallInt, null, true),
            new("cpu", SqlType.Int32, null, false),
            new("physical_io", SqlType.BigInt, null, false),
            new("memusage", SqlType.Int32, null, false),
            new("login_time", SqlType.DateTime, null, false),
            new("last_batch", SqlType.DateTime, null, false),
            new("ecid", SqlType.SmallInt, null, false),
            new("open_tran", SqlType.SmallInt, null, false),
            new("status", NCharSqlType.Get(30, Collation.Baseline, Coercibility.Implicit), 30, false),
            new("sid", BinarySqlType.Get(86), 86, false),
            new("hostname", NCharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, false),
            new("program_name", NCharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, false),
            new("hostprocess", NCharSqlType.Get(10, Collation.Baseline, Coercibility.Implicit), 10, false),
            new("cmd", NCharSqlType.Get(26, Collation.Baseline, Coercibility.Implicit), 26, false),
            new("nt_domain", NCharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, false),
            new("nt_username", NCharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, false),
            new("net_address", NCharSqlType.Get(12, Collation.Baseline, Coercibility.Implicit), 12, false),
            new("net_library", NCharSqlType.Get(12, Collation.Baseline, Coercibility.Implicit), 12, false),
            new("loginame", NCharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, false),
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
    /// Rows for <c>sysprocesses</c>: a row per request <c>sys.dm_exec_requests</c>
    /// lists — its status (the reading session's own <c>runnable</c>), command,
    /// blocker, wait and statement offsets, its batch's SQL handle in the
    /// twenty-byte form — and one <c>sleeping</c> row awaiting a command for a
    /// session running none, carrying its last batch's handle and offsets of
    /// -1 (probed 2026-10-16 against SQL Server 2025). <c>last_batch</c> is the
    /// running request's start, or the last request's end. CPU, I/O and memory
    /// counters read zero.
    /// </summary>
    private static IEnumerable<SqlValue[]> SysprocessesRows(Parser.BatchContext batch)
    {
        var simulation = batch.Connection.Simulation;
        var requests = new Dictionary<int, List<SqlValue[]>>();
        foreach (var request in EnumerateSysDmExecRequests(batch, batch.CurrentDatabase))
        {
            int spid = request[0].AsInt16;
            if (!requests.TryGetValue(spid, out var list))
                requests[spid] = list = [];
            list.Add(request);
        }
        var zeroSmall = SqlValue.FromInt16(0);
        var zeroInt = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        var connections = simulation.SnapshotConnections();
        Array.Sort(connections, static (a, b) => a.Spid.CompareTo(b.Spid));
        foreach (var connection in connections)
        {
            var self = ReferenceEquals(connection, batch.Connection);
            var loginTime = SqlValue.FromDateTime(connection.LoginTimeUtc);
            var login = connection.Security.Effective.LoginName;
            var network = connection.Transport.Client is not null;
            var session = connection.Session;
            var databaseId = SessionDatabaseId(simulation, connection);
            var clientProcessId = connection.ClientProcessId;
            var clientProcess = clientProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!requests.TryGetValue(connection.Spid, out var running))
            {
                yield return Row(zeroSmall, zeroSmall, null, 0, "MISCELLANEOUS", "", SqlValue.FromDateTime(LastRequestEndUtc(connection)), "sleeping", "AWAITING COMMAND",
                    session.BatchText is { } last ? LegacySqlHandle(last, databaseId) : new byte[20], -1, -1, 0);
                continue;
            }
            foreach (var request in running)
            {
                var waitType = request[13].IsNull ? null : request[13].AsString;
                var status = request[3].AsString;
                var waitResource = waitType is not null && waitType.StartsWith("LCK_M_", StringComparison.Ordinal) ? Storage.LockDmvs.WaitResource(batch, connection, sysprocesses: true) : "";
                yield return Row(
                    SqlValue.FromInt16((short)(session.CurrentExecutingThreadId ?? 0)),
                    SqlValue.FromInt16(request[12].AsInt16),
                    waitType,
                    request[14].AsInt32,
                    request[15].AsString is { Length: > 0 } lastWait ? lastWait : "MISCELLANEOUS",
                    waitResource,
                    request[2],
                    self ? "runnable" : status,
                    request[4].AsString,
                    session.BatchText is { } text && !request[5].IsNull ? LegacySqlHandle(simulation.ReportedStatement(connection, text, session.StatementStartIndex, connection.CurrentDatabase).Text, databaseId) : new byte[20],
                    request[6].IsNull ? 0 : request[6].AsInt32,
                    request[7].IsNull ? 0 : request[7].AsInt32,
                    request[1].AsInt32);
            }

            SqlValue[] Row(SqlValue kpid, SqlValue blocked, string? waitType, long waitTime, string lastWaitType, string waitResource, SqlValue lastBatch, string status, string command, byte[] sqlHandle, int statementStart, int statementEnd, int requestId) =>
            [
                SqlValue.FromInt16((short)connection.Spid),
                kpid,
                blocked,
                SqlValue.FromBinary(BinarySqlType.Get(2), SysprocessesWaitType(waitType)),
                SqlValue.FromInt64(waitTime),
                nchar(32, lastWaitType),
                nchar(256, waitResource),
                SqlValue.FromInt16(databaseId),
                SqlValue.FromInt16((short)connection.Security.Effective.DatabasePrincipalId),
                zeroInt,
                zeroBig,
                zeroInt,
                loginTime,
                lastBatch,
                zeroSmall,
                SqlValue.FromInt16((short)connection.OpenTransactionCount),
                nchar(30, status),
                SqlValue.FromBinary(BinarySqlType.Get(86), DeriveLoginSid(login)),
                nchar(128, connection.ClientHostName),
                nchar(128, connection.ClientApplicationName),
                nchar(10, clientProcess),
                nchar(26, command),
                nchar(128, string.Empty),
                nchar(128, string.Empty),
                nchar(12, network ? "FFFFFFFFFFFF" : string.Empty),
                nchar(12, network ? "TCP/IP" : string.Empty),
                nchar(128, login),
                SqlValue.FromBinary(BinarySqlType.Get(128), connection.ContextInfo ?? []),
                SqlValue.FromBinary(BinarySqlType.Get(20), sqlHandle),
                SqlValue.FromInt32(statementStart),
                SqlValue.FromInt32(statementEnd),
                SqlValue.FromInt32(requestId),
                SqlValue.Null(SqlType.Varbinary),
            ];
        }

        static SqlValue nchar(int length, string value) => SqlValue.FromNChar(NCharSqlType.Get(length, Collation.Baseline, Coercibility.Implicit), value);
    }

    /// <summary>
    /// When a session's last request ended — the running one's start when it
    /// is its first — which <c>sys.dm_exec_sessions.last_request_end_time</c>
    /// and a sleeping session's <c>sysprocesses.last_batch</c> report.
    /// </summary>
    internal static DateTime LastRequestEndUtc(SimulatedDbConnection connection) =>
        connection.Session.RequestEndUtc != default ? connection.Session.RequestEndUtc : LastRequestStartUtc(connection);

    /// <summary>When a session's running or last request began, its login before any.</summary>
    internal static DateTime LastRequestStartUtc(SimulatedDbConnection connection) =>
        connection.Session.RequestStartUtc != default ? connection.Session.RequestStartUtc : connection.LoginTimeUtc;

    /// <summary>
    /// The twenty-byte SQL handle <c>sysprocesses</c> carries for a batch,
    /// which <c>sys.dm_exec_sql_text</c> accepts too: type 1, the database, and
    /// the batch's object id as its 64-byte handle holds it, then a stand-in
    /// for the plan's address (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static byte[] LegacySqlHandle(string text, short databaseId)
    {
        var modern = SqlHandleOf(text);
        var handle = new byte[20];
        handle[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(handle.AsSpan(2), databaseId);
        modern.AsSpan(4, 4).CopyTo(handle.AsSpan(4));
        modern.AsSpan(8, 5).CopyTo(handle.AsSpan(8));
        return handle;
    }

    /// <summary>
    /// <c>sysprocesses.waittype</c>: the wait's number, big-endian — a lock
    /// mode's from 1 (<c>LCK_M_SCH_S</c>) up, <c>WAITFOR</c>'s 0x0134 and
    /// <c>ASYNC_NETWORK_IO</c>'s 0x00C2 (probed 2026-10-16 against SQL Server
    /// 2025) — and 0x0000 when the session waits on nothing.
    /// </summary>
    private static byte[] SysprocessesWaitType(string? waitType)
    {
        var code = waitType switch
        {
            null => 0,
            "ASYNC_NETWORK_IO" => 0xC2,
            "LCK_M_BU" => 0x0C,
            "LCK_M_IS" => 0x06,
            "LCK_M_IU" => 0x07,
            "LCK_M_IX" => 0x08,
            "LCK_M_RIn_NL" => 0x0F,
            "LCK_M_RIn_S" => 0x10,
            "LCK_M_RIn_U" => 0x11,
            "LCK_M_RIn_X" => 0x12,
            "LCK_M_RS_S" => 0x0D,
            "LCK_M_RS_U" => 0x0E,
            "LCK_M_RX_S" => 0x13,
            "LCK_M_RX_U" => 0x14,
            "LCK_M_RX_X" => 0x15,
            "LCK_M_S" => 0x03,
            "LCK_M_SCH_M" => 0x02,
            "LCK_M_SCH_S" => 0x01,
            "LCK_M_SIU" => 0x09,
            "LCK_M_SIX" => 0x0A,
            "LCK_M_U" => 0x04,
            "LCK_M_UIX" => 0x0B,
            "LCK_M_X" => 0x05,
            "WAITFOR" => 0x134,
            _ => 0,
        };
        return [(byte)(code >> 8), (byte)code];
    }
}
