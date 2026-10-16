using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Backup;

namespace SqlServerSimulator;

// BACKUP DATABASE and RESTORE DATABASE over DISK devices, in the simulator's own
// backup format (Storage/Backup). Behavior probed 2026-10-11 against SQL Server
// 2022; see docs/claude/backup-restore.md.
partial class Simulation
{
    /// <summary>
    /// Opens the files that <c>BACKUP … TO DISK</c> writes and
    /// <c>RESTORE … FROM DISK</c> reads, standing in for the server's file
    /// system. Defaults to <see langword="null"/>, under which backups live in
    /// memory for the life of the <see cref="Simulation"/>, keyed by the path
    /// as the statement spells it; set it in an object initializer to reach
    /// real files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate receives the path exactly as the statement spells it.
    /// Given <see cref="FileAccess.Read"/>, it returns a readable
    /// <see cref="Stream"/>, which the simulator reads to its end and
    /// disposes, or <see langword="null"/> for a file that doesn't exist.
    /// Given <see cref="FileAccess.Write"/>, it returns a writable stream the
    /// simulator writes the device's whole new content to and disposes, or
    /// <see langword="null"/> for a path that can't be written. A host wanting
    /// real disk access supplies something like
    /// <c>(path, access) =&gt; access == FileAccess.Read ? (File.Exists(path) ? File.OpenRead(path) : null) : File.Create(path)</c>.
    /// </para>
    /// <para>
    /// The file holds the simulator's own backup format, not Microsoft's,
    /// and only the simulator build that wrote it restores it.
    /// <strong>Anything the delegate reaches is readable and writable by every
    /// session allowed to back up or restore</strong>, as real SQL Server's
    /// file access is; keep the mapping narrow, above all when a
    /// <see cref="Simulation"/> is exposed over the network endpoint.
    /// </para>
    /// </remarks>
    public Func<string, FileAccess, Stream?>? OpenBackupFile { get; init; }

    /// <summary>
    /// How long a restore waits for the sessions using its database to leave
    /// before Msg 3101: about twenty seconds (probed 2026-10-11 against SQL
    /// Server 2022, where a session leaving during the wait lets the restore
    /// go ahead). Internal so a test can shorten it.
    /// </summary>
    internal int RestoreExclusiveAccessWaitMillis = 20_000;

    /// <summary>
    /// The backup devices written while <see cref="OpenBackupFile"/> is unset,
    /// keyed by the canonical path real reaches (<see cref="BackupDeviceKey"/>):
    /// a relative path under the default backup directory, case aside.
    /// </summary>
    internal readonly ConcurrentDictionary<string, byte[]> BackupDevices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A device's bytes, or null with the operating-system error real reports for it.</summary>
    private byte[]? ReadBackupDevice(string path, out int osError)
    {
        // A directory in the device's place can't be opened as one (probed
        // 2026-10-16 against SQL Server 2025: operating system error 5).
        var key = BackupDeviceKey(path);
        osError = this.IsServerDirectory(key) ? 5 : 2;
        if (this.OpenBackupFile is not { } open)
            return this.BackupDevices.GetValueOrDefault(key);
        try
        {
            using var stream = open(path, FileAccess.Read);
            if (stream is null)
                return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            osError = OperatingSystemError(ex);
            return null;
        }
    }

    /// <summary>
    /// Replaces a device's content, creating the directories above it as
    /// real's Linux server does; 0, or the operating-system error real reports.
    /// </summary>
    private int WriteBackupDevice(string path, byte[] content)
    {
        var key = BackupDeviceKey(path);
        if (this.OpenBackupFile is not { } open)
        {
            this.BackupDevices[key] = content;
            if (ServerParentDirectory(key) is { } parent)
                this.CreateServerDirectory(parent);
            return 0;
        }
        try
        {
            using var stream = open(path, FileAccess.Write);
            if (stream is null)
                return 3;
            stream.Write(content);
            if (ServerParentDirectory(key) is { } parent)
                this.CreateServerDirectory(parent);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperatingSystemError(ex);
        }
    }

    private static int OperatingSystemError(Exception ex) => ex switch
    {
        FileNotFoundException => 2,
        DirectoryNotFoundException => 3,
        _ => 5,
    };

    /// <summary>What a <c>BACKUP</c> or <c>RESTORE</c> statement's <c>WITH</c> list said.</summary>
    private sealed class BackupOptions
    {
        public string? Name;
        public string? Description;
        public string? MediaName;
        public string? MediaDescription;
        public int? Stats;
        public int File = 1;
        public bool FileWritten;
        public bool Init;
        public bool Format;
        public bool Replace;
        public bool CopyOnly;
        public bool Differential;
        public bool Checksum;
        public bool RestrictedUser;
        public bool LoadHistory;
        public bool? Broker;
        public readonly List<(string LogicalName, string Path)> Moves = [];

        /// <summary>Each flag option named, upper-cased, for the conflict check.</summary>
        public readonly HashSet<string> Flags = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// <c>BACKUP { DATABASE | LOG } name TO DISK = path [WITH …]</c>, entered
    /// on <c>BACKUP</c>.
    /// </summary>
    private bool ParseBackup(ParserContext context, BatchContext batch)
    {
        RefuseUnbuiltStatement(context, verb: null);
        var kind = context.GetNextRequired();
        var isLog = kind is Name { Value: var word } && BuiltInToken.Equals(word, "LOG");
        if (!isLog && kind is not ReservedKeyword { Keyword: Keyword.Database })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var verb = isLog ? "BACKUP LOG" : "BACKUP DATABASE";
        context.MoveNextRequired();
        var databaseName = ParseBackupOperand(context, batch);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var devices = ParseBackupDevices(context, batch, verb);
        var options = ParseBackupOptions(context, batch, "BACKUP", verb);
        context.RejectTrailingToken();
        if (batch.IsSkipping)
            return true;

        var connection = batch.Connection;
        if (connection.CurrentTransaction is not null)
            throw SimulatedSqlException.BackupInTransaction(verb);
        if (BuiltInToken.Equals(databaseName, TempdbDatabaseName))
            throw SimulatedSqlException.BackupOfTempdb(verb);
        var type = isLog ? BackupSet.LogType : options.Differential ? BackupSet.DifferentialType : BackupSet.FullType;
        // A differential names itself in its summary and its failure line, though Msg 3013 says BACKUP DATABASE.
        var commandVerb = type == BackupSet.DifferentialType ? "BACKUP DATABASE WITH DIFFERENTIAL" : verb;
        try
        {
            this.RunBackup(batch, databaseName, type, verb, commandVerb, devices, options);
        }
        catch (SimulatedSqlException)
        {
            // Past those two checks every refusal is real's backup failure (probed 2026-10-16 against SQL Server 2025).
            this.ErrorLog.WriteError(batch.CurrentStatement.UtcNow, BackupProcess, 3041, 16, 1,
                $"BACKUP failed to complete the command {(isLog ? "BACKUP LOG" : "BACKUP DATABASE")} {databaseName}{(type == BackupSet.DifferentialType ? " WITH DIFFERENTIAL" : "")}. Check the backup application log for detailed messages.");
            throw;
        }
        return true;
    }

    /// <summary>The process column of the lines real's backup and restore machinery writes to the error log.</summary>
    private const string BackupProcess = "Backup";

    /// <summary>
    /// A database's creation time as the error log's backup lines spell it,
    /// <c>2025/01/01(00:00:00)</c> — the <c>create_date</c>
    /// <c>sys.databases</c> reports.
    /// </summary>
    private static string BackupCreationDate() =>
        BuiltInResources.SysDatabasesCreateDate.ToString("yyyy/MM/dd(HH:mm:ss)", CultureInfo.InvariantCulture);

    /// <summary>A backup set's LSN as the error log spells it, in the active virtual log file <c>DBCC LOGINFO</c> reports.</summary>
    private static string BackupLsn(long block) => string.Create(CultureInfo.InvariantCulture, $"{ActiveLogSequence}:{block}:1");

    /// <summary>Writes real's Msg 18204 for a backup device that couldn't be opened or created, under the session.</summary>
    private void LogBackupMediaFailure(BatchContext batch, string path, int osError, bool create) =>
        this.ErrorLog.WriteError(batch.CurrentStatement.UtcNow, ErrorLog.SessionProcess(batch.Connection.Spid), 18204, 16, 1, create
            ? $"BackupDiskFile::CreateMedia: Backup device '{path}' failed to create. Operating system error {SimulatedSqlException.OperatingSystemErrorText(osError)}."
            : $"BackupDiskFile::OpenMedia: Backup device '{path}' failed to open. Operating system error {SimulatedSqlException.OperatingSystemErrorText(osError)}.");

    /// <summary>
    /// The backup itself, once the statement may run: every refusal from here
    /// is logged as a failed backup. <paramref name="type"/> is the
    /// <see cref="BackupSet"/> kind; <paramref name="commandVerb"/> is how the
    /// summary names the statement.
    /// </summary>
    private void RunBackup(BatchContext batch, string databaseName, byte type, string verb, string commandVerb, List<BackupDeviceTarget> devices, BackupOptions options)
    {
        var connection = batch.Connection;
        if (!this.Databases.TryGetValue(databaseName, out var database))
            throw SimulatedSqlException.BackupDatabaseDoesNotExist(databaseName, verb);
        if (!database.WaitForSingleUserSlot(connection))
            throw SimulatedSqlException.FollowedByBackupTerminating(SimulatedSqlException.DatabaseInSingleUserUse(database.Name), verb);
        database.RejectWhenOffline(connection);
        var chain = database.BackupChain;
        if (type == BackupSet.LogType)
        {
            if (database.RecoveryModel == RecoveryModel.Simple)
                throw SimulatedSqlException.BackupLogUnderSimpleRecovery();
            if (!PermissionEnforcement.IsOwnerOrBackupOperator(batch, database) && !PermissionEnforcement.HasDatabasePermission(batch, database, "BACKUP LOG"))
                throw SimulatedSqlException.BackupPermissionDenied("BACKUP LOG", database.Name, verb);
            if (chain.LastLogBackupBlock is null)
                throw SimulatedSqlException.BackupLogWithoutDatabaseBackup();
        }
        else
        {
            if (!PermissionEnforcement.IsOwnerOrBackupOperator(batch, database) && !PermissionEnforcement.HasDatabasePermission(batch, database, "BACKUP DATABASE"))
                throw SimulatedSqlException.BackupPermissionDenied("BACKUP DATABASE", database.Name, verb);
            if (type == BackupSet.DifferentialType)
            {
                if (BuiltInToken.Equals(database.Name, MasterDatabaseName))
                    throw SimulatedSqlException.DifferentialBackupOfMaster();
                if (chain.DifferentialBaseBlock is null)
                    throw SimulatedSqlException.DifferentialWithoutDatabaseBackup(database.Name);
            }
        }
        var device = SingleDevice(devices, verb);
        var path = device.Path;
        var start = batch.CurrentStatement.UtcNow;

        BackupMedia media;
        if (options.Init || options.Format)
        {
            // INIT overwrites the sets and keeps the media header; FORMAT writes a new one.
            media = new BackupMedia { MediaDateUtc = start };
            if (!options.Format && this.ReadBackupDevice(path, out _) is { } existing && BackupMedia.TryRead(existing, out var kept, out _) == BackupMedia.ReadOutcome.Read)
            {
                media.MediaName = kept!.MediaName;
                media.MediaDescription = kept.MediaDescription;
                media.MediaSetGuid = kept.MediaSetGuid;
                media.MediaFamilyGuid = kept.MediaFamilyGuid;
                media.MediaDateUtc = kept.MediaDateUtc;
            }
        }
        else
        {
            media = this.ReadBackupDevice(path, out _) is { } existing
                ? BackupMedia.TryRead(existing, out var read, out var writtenBy) switch
                {
                    BackupMedia.ReadOutcome.Read => read!,
                    BackupMedia.ReadOutcome.Empty => new BackupMedia { MediaDateUtc = start },
                    BackupMedia.ReadOutcome.OtherVersion => throw SimulatedSqlException.BackupFromIncompatibleVersion(writtenBy!, BackupMedia.SimulatorVersion, verb),
                    _ => throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(device.Display, verb),
                }
                : new BackupMedia { MediaDateUtc = start };
        }
        if (options.MediaName is { } mediaName)
        {
            if (!options.Format && media.Sets.Count != 0 && media.MediaName is { } actual && actual != mediaName)
                throw SimulatedSqlException.BackupMediaNameMismatch(mediaName, actual, device.Display, verb);
            media.MediaName = mediaName;
        }
        if (options.MediaDescription is { } mediaDescription)
            media.MediaDescription = mediaDescription;

        var stopwatch = Stopwatch.StartNew();
        var set = NewBackupSet(batch, database, type, options, start);
        DatabaseImage image;
        var tables = UserTables(database);
        var held = this.LockTablesForBackup(batch, tables);
        try
        {
            // Every kind of set carries the database as it stands: a
            // differential or log backup restores to that state just as a
            // full one does.
            image = DatabaseImage.Capture(database);
            this.DrawBackupLsns(set, chain);
            AddFilePages(set, database, tables);
        }
        finally
        {
            foreach (var resource in held)
                this.LockManager.Release(resource, LockMode.Shared, connection.LockOwner);
        }
        set.Payload = image.ToPayload();
        set.FinishUtc = start + stopwatch.Elapsed;
        media.Sets.Add(set);
        if (options.Init || options.Format)
            media.Sets.RemoveRange(0, media.Sets.Count - 1);
        if (this.WriteBackupDevice(path, media.ToBytes()) is var osError and not 0)
        {
            this.LogBackupMediaFailure(batch, path, osError, create: true);
            throw SimulatedSqlException.CannotOpenBackupDevice(device.Display, osError, verb);
        }

        AdvanceBackupChain(database, set, options.CopyOnly);
        this.RecordBackupHistory(batch, media, set, device.Display, media.Sets.Count);
        this.CatalogRows.Invalidate();

        var completed = ReportBackupMessages(batch, options.Stats, database.Name, set, media.Sets.Count, commandVerb, stopwatch.Elapsed.TotalSeconds);
        // Real's own count adds the nine pages of the set's header and trailer.
        var deviceInformation = string.Create(CultureInfo.InvariantCulture, $"number of dump devices: 1, device information: (FILE={media.Sets.Count}, TYPE=DISK: {{'{device.Logged}'}})");
        this.ErrorLog.Write(batch.CurrentStatement.UtcNow, BackupProcess, type switch
        {
            BackupSet.LogType => string.Create(CultureInfo.InvariantCulture,
                $"Log was backed up. Database: {database.Name}, creation date(time): {BackupCreationDate()}, first LSN: {BackupLsn(set.FirstLsnBlock)}, last LSN: {BackupLsn(set.LastLsnBlock)}, {deviceInformation}. This is an informational message only. No user action is required."),
            BackupSet.DifferentialType => string.Create(CultureInfo.InvariantCulture,
                $"Database differential changes were backed up. Database: {database.Name}, creation date(time): {BackupCreationDate()}, pages dumped: {TotalPages(set) + 9}, first LSN: {BackupLsn(set.FirstLsnBlock)}, last LSN: {BackupLsn(set.LastLsnBlock)}, full backup LSN: {BackupLsn(set.DifferentialBaseBlock!.Value)}, {deviceInformation}. This is an informational message. No user action is required."),
            _ => string.Create(CultureInfo.InvariantCulture,
                $"Database backed up. Database: {database.Name}, creation date(time): {BackupCreationDate()}, pages dumped: {TotalPages(set) + 9}, first LSN: {BackupLsn(set.FirstLsnBlock)}, last LSN: {BackupLsn(set.LastLsnBlock)}, {deviceInformation}. This is an informational message only. No user action is required."),
        });
        this.ErrorLog.Write(batch.CurrentStatement.UtcNow, BackupProcess, completed);
        connection.LastStatementRowCount = 0;
    }

    /// <summary>A backup set's header as the backup begins, before its LSNs are drawn.</summary>
    private static BackupSet NewBackupSet(BatchContext batch, Database database, byte type, BackupOptions options, DateTime start)
    {
        var set = new BackupSet
        {
            Name = options.Name,
            Description = options.Description,
            DatabaseName = database.Name,
            BackupSetGuid = Guid.NewGuid(),
            StartUtc = start,
            Type = type,
            IsCopyOnly = options.CopyOnly,
            HasChecksums = options.Checksum,
            UserName = batch.Connection.Security.Effective.LoginName,
            RecoveryModel = database.RecoveryModel switch
            {
                RecoveryModel.Simple => "SIMPLE",
                RecoveryModel.BulkLogged => "BULK_LOGGED",
                _ => "FULL",
            },
            Collation = database.CollationName,
            CompatibilityLevel = (byte)database.CompatibilityLevel,
            DatabaseGuid = database.DatabaseGuid,
            RecoveryForkGuid = database.RecoveryForkGuid,
            IsReadOnly = database.IsReadOnly,
            IsSingleUser = database.UserAccess == 1,
        };
        foreach (var file in database.FilesInOrder())
        {
            if (file.IsContainer)
                continue;
            set.Files.Add(new BackupSetFile
            {
                LogicalName = file.Name,
                PhysicalName = file.PhysicalName,
                IsLog = file.IsLog,
                FileGroupName = file.IsLog ? null : database.FilegroupName(file.DataSpaceId),
                FileGroupId = file.IsLog ? 0 : file.DataSpaceId,
                FileId = file.FileId,
                SizeBytes = BuiltInResources.FileSizePages(database, file) * 8192L,
                // An unlimited data file reports 32 TB, a log file's ceiling 2 TB (probed 2026-10-16 against SQL Server 2025).
                MaxSizeBytes = file.MaxSizePages < 0 ? (file.IsLog ? 2_199_023_255_552L : 35_184_372_080_640L) : file.MaxSizePages * 8192L,
                UniqueId = FileUniqueId(database.DatabaseGuid, file.FileId),
            });
        }
        return set;
    }

    /// <summary>A file's identity in a backup set: the database's GUID with its file id folded into the last bytes.</summary>
    private static Guid FileUniqueId(Guid databaseGuid, int fileId)
    {
        var bytes = databaseGuid.ToByteArray();
        bytes[12] ^= (byte)(fileId >> 24);
        bytes[13] ^= (byte)(fileId >> 16);
        bytes[14] ^= (byte)(fileId >> 8);
        bytes[15] ^= (byte)fileId;
        return new Guid(bytes);
    }

    /// <summary>
    /// Draws the set's LSNs from <paramref name="chain"/>: a full or
    /// differential backup checkpoints past the log's position and spans 24
    /// blocks from there; a log backup runs from where the chain left off to
    /// the log's end, its checkpoint the last backup's.
    /// </summary>
    private void DrawBackupLsns(BackupSet set, DatabaseBackupChain chain)
    {
        set.DatabaseBackupLsnBlock = chain.DifferentialBaseBlock ?? 0;
        set.FileDifferentialBaseBlock = chain.DifferentialBaseBlock;
        set.FileDifferentialBaseGuid = chain.DifferentialBaseGuid;
        if (set.Type == BackupSet.LogType)
        {
            set.FirstLsnBlock = chain.LastLogBackupBlock!.Value;
            set.LastLsnBlock = Math.Max(chain.Draw(this.LastAllocatedTransactionId, gap: 0), set.FirstLsnBlock + 24);
            chain.LogBlock = set.LastLsnBlock;
            set.CheckpointLsnBlock = chain.LastCheckpointBlock;
            set.BeginsLogChain = !chain.LogChainContinued;
            return;
        }
        set.FirstLsnBlock = chain.Draw(this.LastAllocatedTransactionId, gap: 80);
        set.CheckpointLsnBlock = set.FirstLsnBlock;
        set.LastLsnBlock = set.FirstLsnBlock + 24;
        chain.LogBlock = set.LastLsnBlock;
        chain.LastCheckpointBlock = set.CheckpointLsnBlock;
        if (set.Type == BackupSet.DifferentialType)
        {
            set.DifferentialBaseBlock = chain.DifferentialBaseBlock;
            set.DifferentialBaseGuid = chain.DifferentialBaseGuid;
        }
    }

    /// <summary>Moves the database's differential base and log chain past a written set (see <see cref="DatabaseBackupChain"/>).</summary>
    private static void AdvanceBackupChain(Database database, BackupSet set, bool copyOnly)
    {
        if (copyOnly)
            return;
        var chain = database.BackupChain;
        if (set.Type == BackupSet.LogType)
        {
            chain.LastLogBackupBlock = set.LastLsnBlock;
            chain.LastLogBackupUtc = set.FinishUtc;
            chain.LogChainContinued = true;
            return;
        }
        if (set.Type == BackupSet.FullType)
        {
            chain.DifferentialBaseBlock = set.CheckpointLsnBlock;
            chain.DifferentialBaseGuid = set.BackupSetGuid;
            chain.DifferentialBaseUtc = set.StartUtc;
            chain.TakeBaseGenerations(UserTables(database));
        }
        if (database.RecoveryModel != RecoveryModel.Simple && chain.LastLogBackupBlock is null)
        {
            chain.LastLogBackupBlock = set.FirstLsnBlock;
            chain.LastLogBackupUtc = set.StartUtc;
            chain.LogChainContinued = false;
        }
    }

    /// <summary>Every page a backup set's files report.</summary>
    private static long TotalPages(BackupSet set)
    {
        long pages = 0;
        foreach (var (_, filePages) in set.FilePages)
            pages += filePages;
        return pages;
    }

    /// <summary>Every user table of <paramref name="database"/>, in object-id order.</summary>
    private static List<HeapTable> UserTables(Database database) =>
        [.. database.Schemas.SelectMany(static pair => pair.Value.HeapTables.Select(static table => table.Value)).OrderBy(static table => table.ObjectId)];

    /// <summary>
    /// Takes a shared lock on every table, so the capture reads committed rows
    /// that no writer changes under it. A table another transaction holds is
    /// waited for with nothing held, then the sweep starts over, so a backup
    /// never joins a deadlock.
    /// </summary>
    /// <remarks>
    /// Real's backup takes no such locks — it copies pages and the log beside
    /// running writers and rolls their uncommitted work back on restore — so
    /// here a backup waits for open write transactions and holds writers off
    /// while it reads.
    /// </remarks>
    private List<LockResource> LockTablesForBackup(BatchContext batch, List<HeapTable> tables)
    {
        var owner = batch.Connection.LockOwner;
        var held = new List<LockResource>(tables.Count);
        while (true)
        {
            LockResource? blocked = null;
            foreach (var table in tables)
            {
                if (this.LockManager.TryAcquire(table.TableDataLock, LockMode.Shared, owner, 0) is LockAcquireOutcome.Granted or LockAcquireOutcome.GrantedAfterWait)
                {
                    held.Add(table.TableDataLock);
                    continue;
                }
                blocked = table.TableDataLock;
                break;
            }
            if (blocked is null)
                return held;
            foreach (var resource in held)
                this.LockManager.Release(resource, LockMode.Shared, owner);
            held.Clear();
            batch.PollCancellation();
            this.LockManager.Acquire(blocked, LockMode.Shared, owner, batch.Connection.LockTimeoutMillis);
            this.LockManager.Release(blocked, LockMode.Shared, owner);
        }
    }

    /// <summary>
    /// The pages each file reports. A full backup: the primary data file a new
    /// database's 384 plus every page the tables' heaps and LOB chains hold,
    /// any other data file 8, each log file 2. A differential: the primary
    /// data file 96, the changed extents of a new database's, any other 8 and
    /// each log file 2. A log backup: the log file alone, a page for every 16
    /// blocks of log it spans.
    /// </summary>
    private static void AddFilePages(BackupSet set, Database database, List<HeapTable> tables)
    {
        long heapPages = 0;
        foreach (var table in tables)
            heapPages += table.Heap.Pages.Count + table.Heap.LobPages.Count;
        foreach (var file in database.FilesInOrder())
        {
            if (file.IsContainer)
                continue;
            if (set.Type == BackupSet.LogType)
            {
                if (file.IsLog)
                    set.FilePages.Add((file.Name, ((set.LastLsnBlock - set.FirstLsnBlock) / 16) + 1));
                continue;
            }
            set.FilePages.Add((file.Name, file.IsLog ? 2 : file.FileId != 1 ? 8 : set.Type == BackupSet.DifferentialType ? 96 : 384 + heapPages));
        }
    }

    /// <summary>The <c>STATS</c> progress, each file's pages, then the summary, whose text it returns for the error log.</summary>
    private static string ReportBackupMessages(BatchContext batch, int? stats, string databaseName, BackupSet set, int position, string verb, double seconds)
    {
        var messages = batch.Connection.PendingMessages;
        if (stats is { } step)
        {
            for (var percent = step; percent < 100; percent += step)
                messages.Enqueue(SimulatedSqlException.BackupPercentProcessedMessage(batch, percent));
            messages.Enqueue(SimulatedSqlException.BackupPercentProcessedMessage(batch, 100));
        }
        long pages = 0;
        foreach (var (logicalName, filePages) in set.FilePages)
        {
            messages.Enqueue(SimulatedSqlException.BackupFileProcessedMessage(batch, filePages, databaseName, logicalName, position));
            pages += filePages;
        }
        var completed = SimulatedSqlException.BackupCompletedMessage(batch, verb, pages, seconds);
        messages.Enqueue(completed);
        return completed.Message;
    }

    /// <summary>
    /// <c>RESTORE DATABASE name FROM DISK = path [WITH …]</c>, entered on
    /// <c>RESTORE</c>.
    /// </summary>
    private bool ParseRestore(ParserContext context, BatchContext batch)
    {
        RefuseUnbuiltStatement(context, verb: null);
        var kind = context.GetNextRequired();
        var isLog = kind is Name { Value: var log } && BuiltInToken.Equals(log, "LOG");
        if (kind is not ReservedKeyword { Keyword: Keyword.Database } && !isLog)
        {
            if (kind is Name { Value: var form } && BuiltInToken.Equals(form, "REWINDONLY"))
                throw new NotSupportedException($"RESTORE {form.ToUpperInvariant()} isn't modeled by the simulator.");
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var verb = isLog ? "RESTORE LOG" : "RESTORE DATABASE";
        context.MoveNextRequired();
        var databaseName = ParseBackupOperand(context, batch);
        List<BackupDeviceTarget>? devices = null;
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.From })
        {
            devices = ParseBackupDevices(context, batch, verb);
        }
        else if (context.Token is not null && context.Token is not ReservedKeyword { Keyword: Keyword.With } && !IsStatementBoundary(context.Token))
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var options = ParseBackupOptions(context, batch, "RESTORE", verb);
        context.RejectTrailingToken();
        if (batch.IsSkipping)
            return true;

        var connection = batch.Connection;
        if (connection.CurrentTransaction is not null)
            throw SimulatedSqlException.BackupInTransaction(verb);
        if (BuiltInToken.Equals(databaseName, TempdbDatabaseName))
            throw SimulatedSqlException.BackupOfTempdb(verb);
        if (BuiltInToken.Equals(databaseName, MasterDatabaseName))
            throw SimulatedSqlException.RestoreMasterOutsideSingleUserMode(verb);
        if (BuiltInToken.EqualsAny(databaseName, ModelDatabaseName, MsdbDatabaseName))
            throw new NotSupportedException($"RESTORE of the system database '{databaseName}' isn't modeled by the simulator.");
        _ = this.Databases.TryGetValue(databaseName, out var existing);
        if (devices is null)
        {
            if (existing is { State: not DatabaseState.Restoring })
                throw SimulatedSqlException.RestoreAlreadyRecovered(verb);
            throw new NotSupportedException("RESTORE DATABASE without FROM isn't modeled by the simulator, which restores no database WITH NORECOVERY.");
        }
        if (existing is not null)
        {
            if (!PermissionEnforcement.HasDatabaseDdlAuthority(batch, Permission.CreateAnyDatabase)
                && !(this.Logins.TryGetValue(connection.Security.Effective.LoginName, out var login) && BuiltInToken.Equals(login.Name, existing.OwnerLoginName)))
            {
                throw SimulatedSqlException.RestorePermissionDenied(existing.Name, verb);
            }
        }
        else if (!PermissionEnforcement.HasDatabaseDdlAuthority(batch, Permission.CreateAnyDatabase))
        {
            throw SimulatedSqlException.BackupPermissionDenied("CREATE DATABASE", MasterDatabaseName, verb);
        }

        var device = SingleDevice(devices, verb);
        var media = this.ReadMediaForRestore(batch, device, verb);
        if (options.File > media.Sets.Count)
            throw SimulatedSqlException.BackupFileIdIncorrectlyFormed(options.File, device.Display, verb);
        var set = media.Sets[options.File - 1];
        if (options.Checksum && !set.HasChecksums)
            throw SimulatedSqlException.RestoreChecksumUnavailable(verb);
        // Only a full backup creates a database; applying a differential or
        // log backup to a restoring one isn't built.
        if (isLog || set.Type != BackupSet.FullType)
        {
            if (existing is null)
                throw SimulatedSqlException.RestoreCannotCreateDatabase(databaseName, verb);
            throw new NotSupportedException($"{verb} of a {(set.Type == BackupSet.LogType ? "log" : set.Type == BackupSet.DifferentialType ? "differential" : "full")} backup set onto an existing database isn't modeled by the simulator; only a full backup restores.");
        }
        DatabaseImage image;
        try
        {
            image = DatabaseImage.FromPayload(set.Payload);
        }
        catch (InvalidDataException)
        {
            throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(device.Display, verb);
        }

        if (existing is not null)
        {
            if (ReferenceEquals(connection.CurrentDatabase, existing))
                throw SimulatedSqlException.RestoreDatabaseInUseBySession(existing.Name, verb);
            if (!WaitForOtherSessionsToLeave(batch, existing, TimeSpan.FromMilliseconds(this.RestoreExclusiveAccessWaitMillis), singleUserToo: true))
                throw SimulatedSqlException.RestoreExclusiveAccess(verb);
            if (!options.Replace && existing.State != DatabaseState.Restoring)
            {
                if (existing.DatabaseGuid != image.DatabaseGuid)
                    throw SimulatedSqlException.RestoreOverOtherDatabase(existing.Name, verb);
                if (existing.RecoveryModel != RecoveryModel.Simple)
                    throw SimulatedSqlException.RestoreTailOfLogNotBackedUp(existing.Name, verb);
            }
        }
        var targetName = existing?.Name ?? databaseName;
        var physicalNames = this.PlanRestoreFiles(image, options, existing, targetName, verb);

        var stopwatch = Stopwatch.StartNew();
        if (existing is null || !this.TryRestoreInPlace(image, existing, physicalNames))
            this.RestoreAsNewDatabase(image, existing, targetName, physicalNames);
        if (this.Databases.TryGetValue(targetName, out var restored))
        {
            if (options.RestrictedUser)
                restored.UserAccess = 2;
            if (options.Broker is { } broker)
                restored.BrokerEnabled = broker;
            ContinueBackupChainFrom(restored, set, batch.CurrentStatement.UtcNow);
        }
        this.RecordRestoreHistory(batch, media, set, device.Display, options.File, targetName, 'D', options.Replace, physicalNames);
        this.CatalogRows.Invalidate();
        // The restore brings back the backed-up access mode.
        this.NoteDatabaseAdmissionChanged();
        var completed = ReportBackupMessages(batch, options.Stats, targetName, set, options.File, verb, stopwatch.Elapsed.TotalSeconds);
        // The database starts up restoring, then again once recovered (probed 2026-10-16 against SQL Server 2025).
        LogSessionLine(batch, $"Starting up database '{targetName}'.");
        LogDatabaseNotRecovered(batch, restored!, "RESTORING");
        LogDatabaseStartup(batch, restored!);
        var at = batch.CurrentStatement.UtcNow;
        this.ErrorLog.Write(at, BackupProcess, $"Restore is complete on database '{targetName}'.  The database is now available.");
        this.ErrorLog.Write(at, BackupProcess, string.Create(CultureInfo.InvariantCulture,
            $"Database was restored: Database: {targetName}, creation date(time): {BackupCreationDate()}, first LSN: {BackupLsn(set.FirstLsnBlock)}, last LSN: {BackupLsn(set.LastLsnBlock)}, number of dump devices: 1, device information: (FILE={options.File}, TYPE=DISK: {{'{device.Logged}'}}). Informational message. No user action required."));
        this.ErrorLog.Write(at, BackupProcess, completed);
        connection.LastStatementRowCount = 0;
        return true;
    }

    /// <summary>
    /// The backup chain a database restored from <paramref name="set"/> goes
    /// on with: the set is its differential base, and outside <c>SIMPLE</c>
    /// recovery the next log backup continues from the set's last LSN, as if
    /// one ran as the restore finished (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static void ContinueBackupChainFrom(Database database, BackupSet set, DateTime restoredUtc)
    {
        var chain = database.BackupChain;
        chain.DifferentialBaseBlock = set.CheckpointLsnBlock;
        chain.DifferentialBaseGuid = set.BackupSetGuid;
        chain.DifferentialBaseUtc = set.StartUtc;
        chain.LogBlock = Math.Max(chain.LogBlock, set.LastLsnBlock);
        chain.LastCheckpointBlock = set.CheckpointLsnBlock;
        chain.TakeBaseGenerations(UserTables(database));
        if (database.RecoveryModel == RecoveryModel.Simple)
        {
            chain.BreakLogChain();
            return;
        }
        chain.LastLogBackupBlock = set.LastLsnBlock;
        chain.LastLogBackupUtc = restoredUtc;
        chain.LogChainContinued = true;
    }

    /// <summary>
    /// Each file's path once <c>MOVE</c> applies, by logical name, refusing a
    /// logical name the set lacks (Msg 3234) and a path another database's file
    /// holds (Msgs 1834 / 3156, then 3119).
    /// </summary>
    private Dictionary<string, string> PlanRestoreFiles(DatabaseImage image, BackupOptions options, Database? existing, string targetName, string verb)
    {
        var physicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in image.Files)
            physicalNames[file.Name] = file.PhysicalName;
        foreach (var (logicalName, path) in options.Moves)
        {
            if (!physicalNames.ContainsKey(logicalName))
                throw SimulatedSqlException.RestoreLogicalFileNotInSet(logicalName, targetName, verb);
            physicalNames[logicalName] = path;
        }
        var conflicts = new List<(string LogicalName, string Path, string HolderName)>();
        foreach (var file in image.Files)
        {
            if (file.IsContainer)
                continue;
            var path = physicalNames[file.Name];
            foreach (var (_, other) in this.Databases)
            {
                if (ReferenceEquals(other, existing))
                    continue;
                if (other.FilesInOrder().Any(held => held.PhysicalName.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    conflicts.Add((file.Name, path, other.Name));
                    break;
                }
            }
        }
        return conflicts.Count == 0 ? physicalNames : throw SimulatedSqlException.RestoreFilesInUse(conflicts, verb);
    }

    /// <summary>
    /// Builds the restored database from <paramref name="image"/>, registered
    /// in <paramref name="existing"/>'s place (and under its <c>database_id</c>)
    /// in the RESTORING state for the length of the rebuild.
    /// </summary>
    private void RestoreAsNewDatabase(DatabaseImage image, Database? existing, string targetName, Dictionary<string, string> physicalNames)
    {
        var database = image.CreateShell(targetName, physicalNames);
        database.State = DatabaseState.Restoring;
        lock (this.Databases)
        {
            if (existing is not null)
            {
                _ = this.Databases.TryRemove(KeyValuePair.Create(existing.Name, existing));
                existing.State = DatabaseState.Offline;
                if (this.Databases.TryGetValue(ModelDatabaseName, out var model))
                    database.ScopedConfiguration.CopyFrom(model.ScopedConfiguration);
                database.Id = existing.Id;
                this.Databases[targetName] = database;
            }
            else
            {
                this.RegisterUserDatabaseLocked(database);
            }
        }
        this.BumpSchemaVersion();
        this.CatalogRows.Invalidate();
        try
        {
            image.ApplySettings(database);
            image.Populate(this, database);
        }
        catch (InvalidOperationException ex)
        {
            // Left RESTORING, as real leaves a database whose restore failed.
            throw new NotSupportedException($"RESTORE DATABASE couldn't rebuild '{targetName}' from its backup set: {ex.Message}", ex);
        }
        database.State = DatabaseState.Online;
        this.BumpSchemaVersion();
        this.CatalogRows.Invalidate();
    }

    /// <summary>
    /// Restores <paramref name="existing"/> in place when its definitions are
    /// still exactly the backup set's — the common case of a database restored
    /// again and again from one backup — replacing each table's rows and every
    /// value the image carries without rebuilding a single object. False when
    /// anything differs, which the full rebuild then handles.
    /// </summary>
    private bool TryRestoreInPlace(DatabaseImage image, Database existing, Dictionary<string, string> physicalNames)
    {
        if (existing.State != DatabaseState.Online)
            return false;
        existing.State = DatabaseState.Restoring;
        bool restored;
        try
        {
            restored = image.TryRestoreInPlace(existing, physicalNames);
        }
        catch (InvalidOperationException ex)
        {
            // Left RESTORING, as real leaves a database whose restore failed.
            throw new NotSupportedException($"RESTORE DATABASE couldn't restore '{existing.Name}' from its backup set: {ex.Message}", ex);
        }
        existing.State = DatabaseState.Online;
        if (restored)
        {
            this.BumpSchemaVersion();
            this.CatalogRows.Invalidate();
        }
        return restored;
    }

    /// <summary>The one disk device a statement names; more is striped media, which isn't modeled.</summary>
    private static BackupDeviceTarget SingleDevice(List<BackupDeviceTarget> devices, string verb)
    {
        if (devices.Count != 1)
            throw new NotSupportedException($"{verb} to or from striped media (more than one device) isn't modeled by the simulator.");
        return devices[0].Path.Length == 0 ? throw SimulatedSqlException.ZeroLengthBackupDevice(verb) : devices[0];
    }

    /// <summary>The database a statement names: an identifier or a variable, the cursor left on it.</summary>
    private static string ParseBackupOperand(ParserContext context, BatchContext batch) => context.Token switch
    {
        AtPrefixedString variable => batch.IsSkipping ? "" : batch.GetVariableSlot(variable.Value).Value.CoerceTo(SqlType.NVarchar).AsString,
        Name name => name.Value,
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// The device list after <c>TO</c> / <c>FROM</c>, entered on that keyword
    /// and leaving the cursor past it: <c>DISK = path</c> entries and logical
    /// device names, written or held by a variable, which resolve as the
    /// statement runs; a tape or URL device isn't modeled.
    /// </summary>
    private static List<BackupDeviceTarget> ParseBackupDevices(ParserContext context, BatchContext batch, string verb)
    {
        var devices = new List<BackupDeviceTarget>();
        while (true)
        {
            var token = context.GetNextRequired();
            if (token is ReservedKeyword { Keyword: Keyword.Disk })
            {
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                var path = ParseBackupStringValue(context, batch);
                var resolved = ResolveBackupDevicePath(path);
                devices.Add(new(path, resolved, resolved));
            }
            else if (token is Name { Value: var deviceKind } && BuiltInToken.EqualsAny(deviceKind, "TAPE", "URL"))
            {
                throw new NotSupportedException($"{verb} {deviceKind.ToUpperInvariant()} devices aren't modeled by the simulator; only DISK is.");
            }
            else if (token is Name or AtPrefixedString)
            {
                var logicalName = token is Name logical ? logical.Value : ParseBackupStringValue(context, batch);
                if (!batch.IsSkipping)
                    devices.Add(ResolveLogicalBackupDevice(batch, logicalName, verb));
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.GetNextOptional() is not Operator { Character: ',' })
                return devices;
        }
    }

    /// <summary>A string literal or variable, the cursor on it.</summary>
    private static string ParseBackupStringValue(ParserContext context, BatchContext batch) => context.Token switch
    {
        Literal { Value: { IsNull: false } value } when value.Type.Category == SqlTypeCategory.String => value.AsString,
        AtPrefixedString variable => batch.IsSkipping ? "" : batch.GetVariableSlot(variable.Value).Value is { IsNull: false } held ? held.CoerceTo(SqlType.NVarchar).AsString : "",
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>An integer literal or variable, the cursor on it; its written text alongside for Msg 3250.</summary>
    private static (long Value, string Written) ParseBackupIntegerValue(ParserContext context, BatchContext batch)
    {
        var negative = false;
        if (context.Token is Operator { Character: '-' })
        {
            negative = true;
            context.MoveNextRequired();
        }
        switch (context.Token)
        {
            case Numeric { Value: { IsNull: false } value } numeric when SqlType.IsIntegerCategory(value.Type) || value.Type is DecimalSqlType { scale: 0 }:
                {
                    var written = numeric.Source.ToString();
                    if (!long.TryParse(written, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var magnitude) || magnitude > int.MaxValue)
                        throw SimulatedSqlException.IntegerValueOutOfRange((negative ? "-" : "") + written);
                    return (negative ? -magnitude : magnitude, (negative ? "-" : "") + written);
                }
            case AtPrefixedString variable when !negative:
                {
                    if (batch.IsSkipping)
                        return (1, "1");
                    var held = batch.GetVariableSlot(variable.Value).Value;
                    var number = held.IsNull ? 0 : held.CoerceTo(SqlType.BigInt).AsInt64;
                    return (number, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// The <c>WITH</c> list, entered on the token after the devices (or the
    /// name) and leaving the cursor past it. Options that change nothing the
    /// simulator models — tape handling, buffer sizes, compression — are
    /// accepted; those that would change what is written or restored and
    /// aren't modeled raise <see cref="NotSupportedException"/>. Given
    /// <paramref name="refused"/>, the statement is one of the forms that only
    /// read a device, which collects the options that would change a database
    /// as real names them in Msg 3032 — <c>STATS</c> too unless
    /// <paramref name="takesStats"/>.
    /// </summary>
    private static BackupOptions ParseBackupOptions(ParserContext context, BatchContext batch, string statement, string verb, List<string>? refused = null, bool takesStats = true)
    {
        var options = new BackupOptions();
        var restore = statement == "RESTORE";
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return options;
        (long Value, string Written)? stats = null;
        (long Value, string Written)? file = null;
        while (true)
        {
            var token = context.GetNextRequired();
            var name = token switch
            {
                ReservedKeyword { Keyword: Keyword.File } => "FILE",
                Name word => word.Value.ToUpperInvariant(),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            var advanced = false;
            if (refused is not null && name is "REPLACE" or "RECOVERY" or "NORECOVERY" or "RESTRICTED_USER" or "KEEP_REPLICATION" or "KEEP_CDC"
                or "ENABLE_BROKER" or "NEW_BROKER" or "ERROR_BROKER_CONVERSATIONS")
            {
                refused.Add(name switch
                {
                    "KEEP_CDC" => "keep_cdc",
                    "KEEP_REPLICATION" => "keep_replication",
                    "NORECOVERY" => "norecovery",
                    "RECOVERY" => "recovery",
                    "REPLACE" => "replace",
                    "RESTRICTED_USER" => "restricted_user",
                    _ => "broker_option",
                });
                name = "";
            }
            switch (name)
            {
                case "":
                    break;
                case "BLOCKSIZE" or "BUFFERCOUNT" or "MAXTRANSFERSIZE":
                    ExpectEquals(context);
                    _ = ParseBackupIntegerValue(context, batch);
                    break;
                case "CHECKSUM":
                    options.Checksum = true;
                    _ = options.Flags.Add("CHECKSUM");
                    break;
                case "COMPRESSION" or "NO_COMPRESSION" or "SKIP" or "NOSKIP" when !restore:
                case "CONTINUE_AFTER_ERROR" or "STOP_ON_ERROR" or "NO_CHECKSUM" or "REWIND" or "NOREWIND" or "UNLOAD" or "NOUNLOAD":
                    _ = options.Flags.Add(name);
                    break;
                case "COPY_ONLY" when !restore:
                    options.CopyOnly = true;
                    break;
                case "DESCRIPTION" when !restore:
                    ExpectEquals(context);
                    options.Description = ParseBackupStringValue(context, batch);
                    break;
                case "DIFFERENTIAL" when !restore:
                    options.Differential = true;
                    break;
                case "ENABLE_BROKER" or "NEW_BROKER" when restore:
                    options.Broker = true;
                    break;
                case "ERROR_BROKER_CONVERSATIONS" when restore:
                    options.Broker = false;
                    break;
                case "FILE" when restore:
                    ExpectEquals(context);
                    file = ParseBackupIntegerValue(context, batch);
                    break;
                case "FORMAT" or "NOFORMAT" when !restore:
                    options.Format = name == "FORMAT";
                    _ = options.Flags.Add(name);
                    break;
                case "INIT" or "NOINIT" when !restore:
                    options.Init = name == "INIT";
                    _ = options.Flags.Add(name);
                    break;
                case "KEEP_CDC" or "KEEP_REPLICATION" or "RECOVERY" when restore:
                    break;
                case "LOADHISTORY" when restore:
                    options.LoadHistory = true;
                    break;
                case "MEDIADESCRIPTION" when !restore:
                    ExpectEquals(context);
                    options.MediaDescription = ParseBackupStringValue(context, batch);
                    break;
                case "MEDIANAME" when !restore:
                    ExpectEquals(context);
                    options.MediaName = ParseBackupNameValue(context, batch);
                    break;
                case "MOVE" when restore:
                    context.MoveNextRequired();
                    var logicalName = ParseBackupStringValue(context, batch);
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.To })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    options.Moves.Add((logicalName, ParseBackupStringValue(context, batch)));
                    break;
                case "NAME" when !restore:
                    ExpectEquals(context);
                    options.Name = ParseBackupNameValue(context, batch);
                    break;
                case "REPLACE" when restore:
                    options.Replace = true;
                    break;
                case "RESTRICTED_USER" when restore:
                    options.RestrictedUser = true;
                    break;
                case "STATS":
                    // A bare STATS reports every 10 percent.
                    if (context.GetNextOptional() is Operator { Character: '=' })
                    {
                        context.MoveNextRequired();
                        stats = ParseBackupIntegerValue(context, batch);
                    }
                    else
                    {
                        stats = (10, "10");
                        advanced = true;
                    }
                    if (refused is not null && !takesStats)
                    {
                        refused.Add(advanced ? "stats" : "stats, stats=");
                        stats = null;
                    }
                    break;
                case "ENCRYPTION" or "CREDENTIAL" or "EXPIREDATE" or "RETAINDAYS" or "METADATA_ONLY" or "FILE_SNAPSHOT" or "NORECOVERY" or "STANDBY"
                    or "NO_TRUNCATE" or "PARTIAL" or "PASSWORD" or "MEDIAPASSWORD" or "STOPAT" or "STOPATMARK" or "STOPBEFOREMARK" or "RESTART" or "FILESTREAM":
                    throw new NotSupportedException($"{statement} … WITH {name} isn't modeled by the simulator.");
                default:
                    throw SimulatedSqlException.UnrecognizedBackupOption(token.Source.ToString(), statement);
            }
            if (!advanced)
                context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
        }

        foreach (var (one, other, oneWritten, otherWritten) in ConflictingBackupOptions)
        {
            if (options.Flags.Contains(one) && options.Flags.Contains(other))
                throw SimulatedSqlException.BackupOptionConflict(oneWritten, otherWritten);
        }
        if (batch.IsSkipping)
            return options;
        if (stats is { } statsValue)
        {
            if (statsValue.Value is < 1 or > 100)
                throw SimulatedSqlException.BackupParameterOutOfRange(statsValue.Written, "STATS", verb);
            options.Stats = (int)statsValue.Value;
        }
        if (file is { } fileValue)
        {
            if (fileValue.Value < 1)
                throw SimulatedSqlException.BackupParameterOutOfRange(fileValue.Written, "FILE", verb);
            options.File = (int)fileValue.Value;
            options.FileWritten = true;
        }
        return options;

        static void ExpectEquals(ParserContext context)
        {
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>The option pairs one statement may not name both of, and how Msg 3031 names them.</summary>
    private static readonly (string One, string Other, string OneWritten, string OtherWritten)[] ConflictingBackupOptions =
    [
        ("INIT", "NOINIT", "init", "noinit"), ("FORMAT", "NOFORMAT", "format", "noformat"), ("SKIP", "NOSKIP", "skip", "noskip"),
        ("REWIND", "NOREWIND", "rewind", "norewind"), ("UNLOAD", "NOUNLOAD", "unload", "nounload"), ("CHECKSUM", "NO_CHECKSUM", "checksum", "no_checksum"),
        ("COMPRESSION", "NO_COMPRESSION", "compression", "no_compression"), ("CONTINUE_AFTER_ERROR", "STOP_ON_ERROR", "continue_after_error", "stop_on_error"),
    ];

    /// <summary>A <c>NAME</c> / <c>MEDIANAME</c> value: a string, a variable or a bare identifier, the cursor on it.</summary>
    private static string ParseBackupNameValue(ParserContext context, BatchContext batch) =>
        context.Token is Name name ? name.Value : ParseBackupStringValue(context, batch);
}
