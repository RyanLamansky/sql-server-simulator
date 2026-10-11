using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>The backup devices written while <see cref="OpenBackupFile"/> is unset, keyed by path as spelled.</summary>
    internal readonly ConcurrentDictionary<string, byte[]> BackupDevices = new(StringComparer.Ordinal);

    /// <summary>A device's bytes, or null with the operating-system error real reports for it.</summary>
    private byte[]? ReadBackupDevice(string path, out int osError)
    {
        osError = 2;
        if (this.OpenBackupFile is not { } open)
            return this.BackupDevices.GetValueOrDefault(path);
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

    /// <summary>Replaces a device's content; 0, or the operating-system error real reports.</summary>
    private int WriteBackupDevice(string path, byte[] content)
    {
        if (this.OpenBackupFile is not { } open)
        {
            this.BackupDevices[path] = content;
            return 0;
        }
        try
        {
            using var stream = open(path, FileAccess.Write);
            if (stream is null)
                return 3;
            stream.Write(content);
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
        public bool Init;
        public bool Format;
        public bool Replace;
        public bool CopyOnly;
        public bool Checksum;
        public bool RestrictedUser;
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
        if (!this.Databases.TryGetValue(databaseName, out var database))
            throw SimulatedSqlException.BackupDatabaseDoesNotExist(databaseName, verb);
        if (!database.WaitForSingleUserSlot(connection))
            throw SimulatedSqlException.FollowedByBackupTerminating(SimulatedSqlException.DatabaseInSingleUserUse(database.Name), verb);
        database.RejectWhenOffline(connection);
        if (isLog)
        {
            if (database.RecoveryModel == RecoveryModel.Simple)
                throw SimulatedSqlException.BackupLogUnderSimpleRecovery();
            throw new NotSupportedException("BACKUP LOG isn't modeled by the simulator; only full database backups are.");
        }
        if (!PermissionEnforcement.IsOwnerOrBackupOperator(batch, database) && !PermissionEnforcement.HasDatabasePermission(batch, database, "BACKUP DATABASE"))
            throw SimulatedSqlException.BackupPermissionDenied("BACKUP DATABASE", database.Name, verb);
        var path = SingleDevice(devices, verb);

        BackupMedia media;
        if (options.Init || options.Format)
        {
            media = new BackupMedia();
            if (!options.Format && this.ReadBackupDevice(path, out _) is { } existing && BackupMedia.TryRead(existing, out var kept, out _) == BackupMedia.ReadOutcome.Read)
            {
                media.MediaName = kept!.MediaName;
                media.MediaDescription = kept.MediaDescription;
            }
        }
        else
        {
            media = this.ReadBackupDevice(path, out _) is { } existing
                ? BackupMedia.TryRead(existing, out var read, out var writtenBy) switch
                {
                    BackupMedia.ReadOutcome.Read => read!,
                    BackupMedia.ReadOutcome.Empty => new BackupMedia(),
                    BackupMedia.ReadOutcome.OtherVersion => throw SimulatedSqlException.BackupFromIncompatibleVersion(writtenBy!, BackupMedia.SimulatorVersion, verb),
                    _ => throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(path, verb),
                }
                : new BackupMedia();
        }
        if (options.MediaName is { } mediaName)
        {
            if (!options.Format && media.Sets.Count != 0 && media.MediaName is { } actual && actual != mediaName)
                throw SimulatedSqlException.BackupMediaNameMismatch(mediaName, actual, path, verb);
            media.MediaName = mediaName;
        }
        if (options.MediaDescription is { } mediaDescription)
            media.MediaDescription = mediaDescription;

        var stopwatch = Stopwatch.StartNew();
        var start = batch.CurrentStatement.UtcNow;
        var set = new BackupSet
        {
            Name = options.Name,
            Description = options.Description,
            DatabaseName = database.Name,
            BackupSetGuid = Guid.NewGuid(),
            StartUtc = start,
            IsCopyOnly = options.CopyOnly,
            HasChecksums = options.Checksum,
        };
        DatabaseImage image;
        var tables = UserTables(database);
        var held = this.LockTablesForBackup(batch, tables);
        try
        {
            image = DatabaseImage.Capture(database);
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
            throw SimulatedSqlException.CannotOpenBackupDevice(path, osError, verb);

        ReportBackupMessages(batch, options.Stats, database.Name, set, media.Sets.Count, verb, stopwatch.Elapsed.TotalSeconds);
        connection.LastStatementRowCount = 0;
        return true;
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
    /// The pages each file reports: the primary data file a new database's
    /// 384 plus every page the tables' heaps and LOB chains hold, any other
    /// data file 8, each log file 2.
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
            set.FilePages.Add((file.Name, file.IsLog ? 2 : file.FileId == 1 ? 384 + heapPages : 8));
        }
    }

    /// <summary>The <c>STATS</c> progress, each file's pages, then the summary.</summary>
    private static void ReportBackupMessages(BatchContext batch, int? stats, string databaseName, BackupSet set, int position, string verb, double seconds)
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
        messages.Enqueue(SimulatedSqlException.BackupCompletedMessage(batch, verb, pages, seconds));
    }

    /// <summary>
    /// <c>RESTORE DATABASE name FROM DISK = path [WITH …]</c>, entered on
    /// <c>RESTORE</c>.
    /// </summary>
    private bool ParseRestore(ParserContext context, BatchContext batch)
    {
        var kind = context.GetNextRequired();
        if (kind is not ReservedKeyword { Keyword: Keyword.Database })
        {
            if (kind is Name { Value: var form } && (BuiltInToken.EqualsAny(form, "LOG", "HEADERONLY", "FILELISTONLY", "LABELONLY", "VERIFYONLY") || BuiltInToken.Equals(form, "REWINDONLY")))
                throw new NotSupportedException($"RESTORE {form.ToUpperInvariant()} isn't modeled by the simulator; only RESTORE DATABASE is.");
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        const string verb = "RESTORE DATABASE";
        context.MoveNextRequired();
        var databaseName = ParseBackupOperand(context, batch);
        List<string>? devices = null;
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

        var path = SingleDevice(devices, verb);
        var bytes = this.ReadBackupDevice(path, out var osError)
            ?? throw SimulatedSqlException.CannotOpenBackupDevice(path, osError, verb);
        var media = BackupMedia.TryRead(bytes, out var read, out var writtenBy) switch
        {
            BackupMedia.ReadOutcome.Read => read!,
            BackupMedia.ReadOutcome.Empty => throw SimulatedSqlException.BackupVolumeEmpty(path, verb),
            BackupMedia.ReadOutcome.OtherVersion => throw SimulatedSqlException.BackupFromIncompatibleVersion(writtenBy!, BackupMedia.SimulatorVersion, verb),
            _ => throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(path, verb),
        };
        if (options.File > media.Sets.Count)
            throw SimulatedSqlException.BackupFileIdIncorrectlyFormed(options.File, path, verb);
        var set = media.Sets[options.File - 1];
        if (options.Checksum && !set.HasChecksums)
            throw SimulatedSqlException.RestoreChecksumUnavailable(verb);
        DatabaseImage image;
        try
        {
            image = DatabaseImage.FromPayload(set.Payload);
        }
        catch (InvalidDataException)
        {
            throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(path, verb);
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
        }
        ReportBackupMessages(batch, options.Stats, targetName, set, options.File, verb, stopwatch.Elapsed.TotalSeconds);
        connection.LastStatementRowCount = 0;
        return true;
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
    private static string SingleDevice(List<string> devices, string verb)
    {
        if (devices.Count != 1)
            throw new NotSupportedException($"{verb} to or from striped media (more than one device) isn't modeled by the simulator.");
        return devices[0].Length == 0 ? throw SimulatedSqlException.ZeroLengthBackupDevice(verb) : devices[0];
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
    /// and leaving the cursor past it: <c>DISK = path</c> entries; a logical
    /// device name is Msg 3206, a tape or URL device isn't modeled.
    /// </summary>
    private static List<string> ParseBackupDevices(ParserContext context, BatchContext batch, string verb)
    {
        var devices = new List<string>();
        while (true)
        {
            var token = context.GetNextRequired();
            if (token is ReservedKeyword { Keyword: Keyword.Disk })
            {
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                devices.Add(ParseBackupStringValue(context, batch));
            }
            else if (token is Name { Value: var deviceKind } && BuiltInToken.EqualsAny(deviceKind, "TAPE", "URL"))
            {
                throw new NotSupportedException($"{verb} {deviceKind.ToUpperInvariant()} devices aren't modeled by the simulator; only DISK is.");
            }
            else if (token is Name logicalDevice)
            {
                if (!batch.IsSkipping)
                    throw SimulatedSqlException.BackupDeviceDoesNotExist(logicalDevice.Value, verb);
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
    /// aren't modeled raise <see cref="NotSupportedException"/>.
    /// </summary>
    private static BackupOptions ParseBackupOptions(ParserContext context, BatchContext batch, string statement, string verb)
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
            switch (name)
            {
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
                    throw new NotSupportedException("BACKUP DATABASE WITH DIFFERENTIAL isn't modeled by the simulator; only full backups are.");
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
                    break;
                case "ENCRYPTION" or "CREDENTIAL" or "EXPIREDATE" or "RETAINDAYS" or "METADATA_ONLY" or "FILE_SNAPSHOT" or "NORECOVERY" or "STANDBY"
                    or "NO_TRUNCATE" or "PARTIAL" or "PASSWORD" or "MEDIAPASSWORD" or "STOPAT" or "STOPATMARK" or "STOPBEFOREMARK" or "RESTART" or "FILESTREAM" or "LOADHISTORY":
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
