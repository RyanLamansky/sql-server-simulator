using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The log space, database transaction, live query profile and server service
// views, each in SQL Server 2025's column shape (probed 2026-10-16); see
// docs/claude/catalog-views.md#sqlos-and-monitoring-views.
internal static partial class BuiltInResources
{
    private static void RegisterServerStateDmvs(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        // The current database's log, as DBCC SQLPERF(LOGSPACE) reports it:
        // its size less the header page, and the fixed share in use a fresh
        // database's log shows, which is also the share written since the
        // last backup the database never had.
        Sys("dm_db_log_space_usage",
        [
            new("database_id", SqlType.Int32, null, true),
            new("total_log_size_in_bytes", SqlType.BigInt, null, true),
            new("used_log_space_in_bytes", SqlType.BigInt, null, true),
            new("used_log_space_in_percent", SqlType.Real, null, true),
            new("log_space_in_bytes_since_last_backup", SqlType.BigInt, null, true),
        ], static (batch, database) =>
        {
            var total = Simulation.LogFileBytes(database) - 8192;
            var used = Simulation.ReportedLogBytesUsed;
            return [[
                SqlValue.FromInt32(DatabaseIdOf(batch.Connection.Simulation, database)),
                SqlValue.FromInt64(total),
                SqlValue.FromInt64(used),
                SqlValue.FromSingle((float)(100.0 * used / total)),
                SqlValue.FromInt64(used),
            ]];
        });

        Sys("dm_tran_database_transactions",
        [
            new("transaction_id", SqlType.BigInt, null, false),
            new("database_id", SqlType.Int32, null, false),
            new("database_transaction_begin_time", SqlType.DateTime, null, true),
            new("database_transaction_type", SqlType.Int32, null, false),
            new("database_transaction_state", SqlType.Int32, null, false),
            new("database_transaction_status", SqlType.Int32, null, false),
            new("database_transaction_status2", SqlType.Int32, null, false),
            new("database_transaction_log_record_count", SqlType.BigInt, null, false),
            new("database_transaction_replicate_record_count", SqlType.Int32, null, false),
            new("database_transaction_log_bytes_used", SqlType.BigInt, null, false),
            new("database_transaction_log_bytes_reserved", SqlType.BigInt, null, false),
            new("database_transaction_log_bytes_used_system", SqlType.Int32, null, false),
            new("database_transaction_log_bytes_reserved_system", SqlType.Int32, null, false),
            new("database_transaction_begin_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("database_transaction_last_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("database_transaction_most_recent_savepoint_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("database_transaction_commit_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("database_transaction_last_rollback_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("database_transaction_next_undo_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("database_transaction_first_repl_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
        ], static (batch, _) => DatabaseTransactionRows(batch));

        // A query's live per-operator profile is kept only under
        // lightweight profiling of a running plan, which the simulator's
        // execution has no operators to report.
        Sys("dm_exec_query_profiles",
        [
            new("session_id", SqlType.SmallInt, null, true),
            new("request_id", SqlType.Int32, null, true),
            new("sql_handle", SqlType.Varbinary, 64, true),
            new("plan_handle", SqlType.Varbinary, 64, true),
            new("physical_operator_name", SqlType.NVarchar, 256, true),
            new("node_id", SqlType.Int32, null, true),
            new("thread_id", SqlType.Int32, null, true),
            new("task_address", SqlType.Varbinary, 8, false),
            new("row_count", SqlType.BigInt, null, true),
            new("rewind_count", SqlType.BigInt, null, true),
            new("rebind_count", SqlType.BigInt, null, true),
            new("end_of_scan_count", SqlType.BigInt, null, true),
            new("estimate_row_count", SqlType.BigInt, null, true),
            new("first_active_time", SqlType.BigInt, null, true),
            new("last_active_time", SqlType.BigInt, null, true),
            new("open_time", SqlType.BigInt, null, true),
            new("first_row_time", SqlType.BigInt, null, true),
            new("last_row_time", SqlType.BigInt, null, true),
            new("close_time", SqlType.BigInt, null, true),
            new("elapsed_time_ms", SqlType.BigInt, null, true),
            new("cpu_time_ms", SqlType.BigInt, null, true),
            new("database_id", SqlType.SmallInt, null, true),
            new("object_id", SqlType.Int32, null, true),
            new("index_id", SqlType.Int32, null, true),
            new("scan_count", SqlType.BigInt, null, true),
            new("logical_read_count", SqlType.BigInt, null, true),
            new("physical_read_count", SqlType.BigInt, null, true),
            new("read_ahead_count", SqlType.BigInt, null, true),
            new("write_page_count", SqlType.BigInt, null, true),
            new("lob_logical_read_count", SqlType.BigInt, null, true),
            new("lob_physical_read_count", SqlType.BigInt, null, true),
            new("lob_read_ahead_count", SqlType.BigInt, null, true),
            new("segment_read_count", SqlType.Int32, null, true),
            new("segment_skip_count", SqlType.Int32, null, true),
            new("actual_read_row_count", SqlType.BigInt, null, true),
            new("estimated_read_row_count", SqlType.BigInt, null, true),
            new("page_server_read_count", SqlType.BigInt, null, true),
            new("page_server_read_ahead_count", SqlType.BigInt, null, true),
            new("lob_page_server_read_count", SqlType.BigInt, null, true),
            new("lob_page_server_read_ahead_count", SqlType.BigInt, null, true),
            new("row_requalification_count", SqlType.BigInt, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // The three services a Linux server reports, in the Windows-style
        // names and paths it reports them under (probed 2026-10-16 against
        // SQL Server 2025): the engine, running since the simulation was
        // made, the Agent stopped, and the full-text launcher running beside
        // the engine — the process the simulator is.
        Sys("dm_server_services",
        [
            new("servicename", SqlType.NVarchar, 256, false),
            new("startup_type", SqlType.Int32, null, true),
            new("startup_type_desc", SqlType.NVarchar, 256, false),
            new("status", SqlType.Int32, null, true),
            new("status_desc", SqlType.NVarchar, 256, false),
            new("process_id", SqlType.Int32, null, true),
            new("last_startup_time", SqlType.GetDateTimeOffset(7), null, true),
            new("service_account", SqlType.NVarchar, 256, false),
            new("filename", SqlType.NVarchar, 256, false),
            new("is_clustered", SqlType.NVarchar, 1, false),
            new("cluster_nodename", SqlType.NVarchar, 256, true),
            new("instant_file_initialization_enabled", SqlType.NVarchar, 1, false),
        ], static (batch, _) => ServerServiceRows(batch.Connection.Simulation));

        // A Linux server keeps no registry to read (probed 2026-10-16 against
        // SQL Server 2025).
        Sys("dm_server_registry",
        [
            new("registry_key", SqlType.NVarchar, 256, true),
            new("value_name", SqlType.NVarchar, 256, true),
            new("value_data", SqlType.SqlVariant, null, true),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>The <c>database_id</c> of <paramref name="database"/>.</summary>
    private static int DatabaseIdOf(Simulation simulation, Database database)
    {
        foreach (var (candidate, id) in DbId.DatabasesWithIds(simulation))
        {
            if (ReferenceEquals(candidate, database))
                return id;
        }
        return database.Id;
    }

    /// <summary>
    /// <c>sys.dm_tran_database_transactions</c>: for each user transaction, a
    /// row for each database it has written to — begun there, with its
    /// changed rows as its log records — and for each it has only locked a
    /// table in, not yet begun (probed 2026-10-16 against SQL Server 2025: a
    /// transaction that has done nothing in a database has no row there, and
    /// neither does a statement's own autocommit transaction). LSNs are the
    /// ones <c>DBCC OPENTRAN</c> prints, and the log bytes a stand-in of none.
    /// </summary>
    private static IEnumerable<SqlValue[]> DatabaseTransactionRows(Parser.BatchContext batch)
    {
        var simulation = batch.Connection.Simulation;
        var connections = simulation.SnapshotConnections();
        var transactions = new List<(SimulatedDbConnection Connection, SimulatedDbTransaction Transaction)>();
        var listed = new HashSet<long>();
        foreach (var connection in connections)
        {
            if (connection.CurrentTransaction is { } transaction && listed.Add(transaction.TransactionId))
                transactions.Add((connection, transaction));
        }
        if (transactions.Count == 0)
            yield break;
        transactions.Sort(static (a, b) =>
        {
            long left = a.Transaction.TransactionId, right = b.Transaction.TransactionId;
            return left.CompareTo(right);
        });
        var zero = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        var lsnType = SqlType.GetDecimal(25, 0);
        var noLsn = SqlValue.Null(lsnType);
        foreach (var (database, id) in DbId.DatabasesWithIds(simulation))
        {
            var tables = new Simulation.DatabaseTables(simulation, database, connections);
            foreach (var (connection, transaction) in transactions)
            {
                var changes = tables.WrittenBy(connection, transaction);
                if (changes == 0 && !tables.Holds(connection.LockOwner, writeOnly: false))
                    continue;
                var begun = changes != 0;
                var lsn = begun
                    ? SqlValue.FromDecimal(lsnType, (Simulation.ActiveLogSequence * 1_000_000_000_000_000m) + (transaction.TransactionId * 100_000m) + 1)
                    : noLsn;
                yield return
                [
                    SqlValue.FromInt64(transaction.TransactionId),
                    SqlValue.FromInt32(id),
                    begun ? SqlValue.FromDateTime(transaction.BeginTimeUtc) : SqlValue.Null(SqlType.DateTime),
                    SqlValue.FromInt32(1),
                    SqlValue.FromInt32(begun ? 4 : 3),
                    SqlValue.FromInt32(begun ? 1 : 256),
                    SqlValue.FromInt32(begun ? 256 : 0),
                    SqlValue.FromInt64(changes),
                    zero,
                    zeroBig,
                    zeroBig,
                    zero,
                    zero,
                    lsn,
                    lsn,
                    noLsn,
                    noLsn,
                    noLsn,
                    noLsn,
                    noLsn,
                ];
            }
        }
    }

    private static IEnumerable<SqlValue[]> ServerServiceRows(Simulation simulation)
    {
        var started = SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(7), new DateTimeOffset(simulation.SeedDate, TimeSpan.Zero));
        var processId = SqlValue.FromInt32(Environment.ProcessId);
        var no = SqlValue.FromNVarchar("N");
        var noNode = SqlValue.Null(SqlType.NVarchar);
        SqlValue[] Service(string name, int startup, string startupDesc, int status, string statusDesc, bool running, string? account, string file, string fileInit) =>
        [
            SqlValue.FromNVarchar(name),
            SqlValue.FromInt32(startup),
            SqlValue.FromNVarchar(startupDesc),
            SqlValue.FromInt32(status),
            SqlValue.FromNVarchar(statusDesc),
            running ? processId : SqlValue.Null(SqlType.Int32),
            running ? started : SqlValue.Null(SqlType.GetDateTimeOffset(7)),
            account is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(account),
            SqlValue.FromNVarchar(file),
            no,
            noNode,
            SqlValue.FromNVarchar(fileInit),
        ];
        yield return Service("MSSQLSERVER", 2, "Automatic", 4, "Running", running: true, account: null, @"C:\binn\sqlservr.exe", "Y");
        yield return Service("SQL Server Agent (MSSQLSERVER)", 2, "Automatic", 1, "Stopped", running: false, "LocalSystem", "\"C:\\Binn\\sqlagent.exe\"", "N");
        yield return Service("SQL Full-text Filter Daemon Launcher (MSSQL)", 3, "Manual", 4, "Running", running: true, "LocalSystem", "\"c:\\binn\\fdlauncher.exe\" -s MSSQL -p c:\\binn\\", "N");
    }
}
