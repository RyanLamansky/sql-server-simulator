using System.Globalization;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// The system TVFs over a database's log and its files' volumes —
// sys.dm_db_log_info, sys.dm_db_log_stats and sys.dm_os_volume_stats — each
// gated by VIEW SERVER PERFORMANCE STATE (Msg 300), shapes probed 2026-10-16
// against SQL Server 2025. The log they describe is the one DBCC LOGINFO and
// DBCC SQLPERF(LOGSPACE) report: the simulator keeps none, so the figures are
// those a freshly created database's log shows.
partial class Selection
{
    private static readonly NVarcharSqlType LogLsnType = NVarcharSqlType.Get(24, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly NVarcharSqlType LogWordType = NVarcharSqlType.Get(60, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly NVarcharSqlType VolumeTextType = NVarcharSqlType.Get(256, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly SqlType[] LogInfoSchema =
    [
        SqlType.Int32, SqlType.SmallInt, SqlType.BigInt, SqlType.Float, SqlType.BigInt, SqlType.Bit, SqlType.Int32,
        SqlType.TinyInt, LogLsnType, LogLsnType, VarbinarySqlType.Get(20),
    ];

    private static readonly string[] LogInfoColumnNames =
    [
        "database_id", "file_id", "vlf_begin_offset", "vlf_size_mb", "vlf_sequence_number", "vlf_active", "vlf_status",
        "vlf_parity", "vlf_first_lsn", "vlf_create_lsn", "vlf_encryptor_thumbprint",
    ];

    private static readonly SqlType[] LogStatsSchema =
    [
        SqlType.Int32, LogWordType, LogLsnType, LogLsnType, SqlType.BigInt, SqlType.Float, SqlType.BigInt, SqlType.Float,
        SqlType.BigInt, SqlType.Float, LogWordType, SqlType.DateTime, LogLsnType, SqlType.Float, LogLsnType, SqlType.Float,
        LogLsnType, SqlType.Float, SqlType.BigInt, LogWordType, LogLsnType, SqlType.Float,
    ];

    private static readonly string[] LogStatsColumnNames =
    [
        "database_id", "recovery_model", "log_min_lsn", "log_end_lsn", "current_vlf_sequence_number", "current_vlf_size_mb",
        "total_vlf_count", "total_log_size_mb", "active_vlf_count", "active_log_size_mb", "log_truncation_holdup_reason",
        "log_backup_time", "log_backup_lsn", "log_since_last_log_backup_mb", "log_checkpoint_lsn", "log_since_last_checkpoint_mb",
        "log_recovery_lsn", "log_recovery_size_mb", "recovery_vlf_count", "log_state", "oldest_lsn_pending_replication",
        "log_size_pending_replication_mb",
    ];

    private static readonly SqlType[] VolumeStatsSchema =
    [
        SqlType.Int32, SqlType.Int32, VolumeTextType, VolumeTextType, VolumeTextType, VolumeTextType, SqlType.BigInt, SqlType.BigInt,
        SqlType.TinyInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.TinyInt,
    ];

    private static readonly string[] VolumeStatsColumnNames =
    [
        "database_id", "file_id", "volume_mount_point", "volume_id", "logical_volume_name", "file_system_type", "total_bytes",
        "available_bytes", "supports_compression", "supports_alternate_streams", "supports_sparse_files", "is_read_only",
        "is_compressed", "incurs_seek_penalty",
    ];

    /// <summary>
    /// <c>sys.dm_db_log_info(database_id)</c>: the virtual log files
    /// <c>DBCC LOGINFO</c> cuts the database's log into, the first active. A
    /// NULL or <c>DEFAULT</c> id reads the current database, and an id no
    /// database has none (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static Selection ParseLogInfoDmv(ParserContext context, string functionName)
    {
        var arguments = ParseIntegerTvfArguments(context, functionName, 1);
        return new Selection(LogInfoSchema, LogInfoColumnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateLogInfoDmv(arguments[0], batch, outerResolver));
    }

    /// <summary>
    /// <c>sys.dm_db_log_stats(database_id)</c>: one row summing up the log
    /// <see cref="ParseLogInfoDmv"/> lists, as a database that has never
    /// backed its log up reports it — nothing holds its truncation up. A NULL
    /// or <c>DEFAULT</c> id reads the current database.
    /// </summary>
    public static Selection ParseLogStatsDmv(ParserContext context, string functionName)
    {
        var arguments = ParseIntegerTvfArguments(context, functionName, 1);
        return new Selection(LogStatsSchema, LogStatsColumnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateLogStatsDmv(arguments[0], batch, outerResolver));
    }

    /// <summary>
    /// <c>sys.dm_os_volume_stats(database_id, file_id)</c>: the volume a
    /// database file lives on, which for the simulator's files is the volume
    /// of the host's temporary directory. A Linux server leaves every column
    /// but the sizes NULL, and a NULL argument matches no file (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static Selection ParseVolumeStatsDmv(ParserContext context, string functionName)
    {
        var arguments = ParseIntegerTvfArguments(context, functionName, 2);
        return new Selection(VolumeStatsSchema, VolumeStatsColumnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateVolumeStatsDmv(arguments[0], arguments[1], batch, outerResolver));
    }

    /// <summary>
    /// A system TVF's parenthesized argument list of <paramref name="count"/>
    /// expressions, each of which may be <c>DEFAULT</c> (read as NULL); too
    /// few is Msg 313, too many Msg 8144.
    /// </summary>
    private static Expression?[] ParseIntegerTvfArguments(ParserContext context, string functionName, int count, byte insufficientState = 2)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var arguments = new Expression?[count];
        if (context.GetNextRequired() is Operator { Character: ')' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, insufficientState);
        for (var i = 0; ; i++)
        {
            Expression? argument = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
                context.MoveNextRequired();
            else
                argument = Expression.Parse(context);
            if (i < count)
                arguments[i] = argument;
            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (i < count - 1)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, insufficientState);
            if (i >= count)
                throw SimulatedSqlException.TooManyArgumentsToFunction(functionName);
            break;
        }
        context.MoveNextOptional();
        return arguments;
    }

    /// <summary>The database a log TVF's argument names: the current one for NULL, none for an id no database has.</summary>
    private static (Database Database, short Id)? LogDmvDatabase(Expression? databaseExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var wanted = databaseExpr is null ? null : EvalNullableInt(databaseExpr, runtime);
        foreach (var (database, id) in DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            if (wanted is { } want ? want == id : ReferenceEquals(database, batch.CurrentDatabase))
                return (database, id);
        }
        return null;
    }

    /// <summary>
    /// The virtual log files of <paramref name="database"/>'s log as
    /// <c>DBCC LOGINFO</c> cuts it: four, each a whole number of 64 KB units,
    /// the last taking the remainder.
    /// </summary>
    private static (long Offset, long Size)[] VirtualLogFiles(Database database)
    {
        var usable = Simulation.LogFileBytes(database) - 8192;
        var unit = usable / 4 / 65536 * 65536;
        var files = new (long, long)[4];
        for (var i = 0; i < 4; i++)
            files[i] = (8192 + (i * unit), i < 3 ? unit : usable - (3 * unit));
        return files;
    }

    /// <summary>An LSN as the log DMVs spell one: the virtual log file's sequence, the block offset and the slot, in hex.</summary>
    private static string LogLsn(int sequence, int block, int slot) =>
        string.Create(CultureInfo.InvariantCulture, $"{sequence:X8}:{block:X8}:{slot:X4}");

    /// <summary>Megabytes cut to <paramref name="digits"/> decimals, as real reports a log size.</summary>
    private static double LogMegabytes(long bytes, int digits)
    {
        var scale = Math.Pow(10, digits);
        return Math.Floor(bytes / 1048576.0 * scale) / scale;
    }

    private static List<byte[]> EnumerateLogInfoDmv(Expression? databaseExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var rows = new List<byte[]>();
        if (LogDmvDatabase(databaseExpr, batch, outerResolver) is not var (database, id))
            return rows;
        var logFile = database.Files.Find(file => file.IsLog);
        var unused = LogLsn(0, 0, 0);
        var files = VirtualLogFiles(database);
        for (var i = 0; i < files.Length; i++)
        {
            var active = i == 0;
            rows.Add(RowEncoder.EncodeRow(LogInfoSchema,
            [
                SqlValue.FromInt32(id),
                SqlValue.FromInt16((short)(logFile?.FileId ?? 2)),
                SqlValue.FromInt64(files[i].Offset),
                SqlValue.FromDouble(LogMegabytes(files[i].Size, 2)),
                SqlValue.FromInt64(active ? Simulation.ActiveLogSequence : 0),
                SqlValue.FromBoolean(active),
                SqlValue.FromInt32(active ? 2 : 0),
                SqlValue.FromByte(active ? (byte)64 : (byte)0),
                SqlValue.FromNVarchar(LogLsnType, active ? LogLsn(Simulation.ActiveLogSequence, 16, 1) : unused),
                SqlValue.FromNVarchar(LogLsnType, unused),
                SqlValue.Null(VarbinarySqlType.Get(20)),
            ]));
        }
        return rows;
    }

    private static List<byte[]> EnumerateLogStatsDmv(Expression? databaseExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var rows = new List<byte[]>();
        if (LogDmvDatabase(databaseExpr, batch, outerResolver) is not var (database, id))
            return rows;
        var files = VirtualLogFiles(database);
        var total = Simulation.LogFileBytes(database) - 8192;
        var used = Simulation.ReportedLogBytesUsed;
        var sequence = Simulation.ActiveLogSequence;
        var start = LogLsn(sequence, 24, 1);
        var usedMegabytes = SqlValue.FromDouble(LogMegabytes(used, 6));
        rows.Add(RowEncoder.EncodeRow(LogStatsSchema,
        [
            SqlValue.FromInt32(id),
            SqlValue.FromNVarchar(LogWordType, database.RecoveryModel switch
            {
                RecoveryModel.BulkLogged => "BULK_LOGGED",
                RecoveryModel.Simple => "SIMPLE",
                _ => "FULL",
            }),
            SqlValue.FromNVarchar(LogLsnType, start),
            SqlValue.FromNVarchar(LogLsnType, LogLsn(sequence, (int)(24 + (used / 512)), 1)),
            SqlValue.FromInt64(sequence),
            SqlValue.FromDouble(LogMegabytes(files[0].Size, 6)),
            SqlValue.FromInt64(files.Length),
            SqlValue.FromDouble(LogMegabytes(total, 6)),
            SqlValue.FromInt64(1),
            usedMegabytes,
            SqlValue.FromNVarchar(LogWordType, "NOTHING"),
            SqlValue.FromDateTime(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            SqlValue.FromNVarchar(LogLsnType, LogLsn(0, 0, 0)),
            SqlValue.Null(SqlType.Float),
            SqlValue.FromNVarchar(LogLsnType, start),
            usedMegabytes,
            SqlValue.FromNVarchar(LogLsnType, start),
            usedMegabytes,
            SqlValue.FromInt64(1),
            SqlValue.FromNVarchar(LogWordType, "LOG_NORMAL"),
            SqlValue.Null(LogLsnType),
            SqlValue.Null(SqlType.Float),
        ]));
        return rows;
    }

    private static List<byte[]> EnumerateVolumeStatsDmv(Expression? databaseExpr, Expression? fileExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var rows = new List<byte[]>();
        if ((databaseExpr is null ? null : EvalNullableInt(databaseExpr, runtime)) is not { } wantDatabase
            || (fileExpr is null ? null : EvalNullableInt(fileExpr, runtime)) is not { } wantFile)
        {
            return rows;
        }
        foreach (var (database, id) in DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            if (id != wantDatabase || !database.Files.Exists(file => file.FileId == wantFile))
                continue;
            var (total, available) = HostVolume();
            var noText = SqlValue.Null(VolumeTextType);
            var noFlag = SqlValue.Null(SqlType.TinyInt);
            rows.Add(RowEncoder.EncodeRow(VolumeStatsSchema,
            [
                SqlValue.FromInt32(id), SqlValue.FromInt32(wantFile), noText, noText, noText, noText,
                SqlValue.FromInt64(total), SqlValue.FromInt64(available),
                noFlag, noFlag, noFlag, noFlag, noFlag, noFlag,
            ]));
        }
        return rows;
    }

    /// <summary>The size and free space of the host volume the simulator's temporary directory is on, zero where the host won't say.</summary>
    private static (long Total, long Available) HostVolume()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath()) ?? "/");
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }
}
