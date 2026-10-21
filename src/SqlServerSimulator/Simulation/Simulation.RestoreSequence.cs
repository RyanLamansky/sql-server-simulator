using System.Diagnostics;
using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Backup;

namespace SqlServerSimulator;

// RESTORE DATABASE and RESTORE LOG: a full set, then differential and log sets
// applied to a database the sequence leaves restoring, a point-in-time stop
// inside a log set, STANDBY, and the recovery that ends the sequence. Probed
// 2026-10-16 against SQL Server 2025; see docs/claude/backup-restore.md.
partial class Simulation
{
    /// <summary>How a restore leaves its database.</summary>
    private enum RestoreEnding
    {
        /// <summary>Recovered and online: the sequence ends.</summary>
        Recovered,

        /// <summary><c>NORECOVERY</c>, or a stop point past the set: restoring, ready for the next set.</summary>
        Restoring,

        /// <summary><c>STANDBY</c>: online and read-only, ready for the next set.</summary>
        Standby,
    }

    /// <summary>A restore's stop point once settled: a time, or a mark with the time it must fall after.</summary>
    private readonly struct StopPoint(DateTime? at, string? mark, bool beforeMark, DateTime? markAfter)
    {
        public readonly DateTime? At = at;
        public readonly string? Mark = mark;
        public readonly bool BeforeMark = beforeMark;
        public readonly DateTime? MarkAfter = markAfter;

        public bool IsSet => this.At is not null || this.Mark is not null;
    }

    /// <summary>
    /// <c>RESTORE { DATABASE | LOG } name [FROM devices] [WITH …]</c>,
    /// entered on <c>RESTORE</c>.
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
        if (options.StopMark is { } markName && markName.StartsWith("lsn:", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("RESTORE … WITH STOPATMARK / STOPBEFOREMARK naming an LSN isn't modeled by the simulator; name a mark.");
        // The stop point is settled before the database is consulted (probed 2026-10-16 against SQL Server 2025).
        var stop = new StopPoint(ResolveStopAt(options.StopAt, "STOPAT", verb), options.StopMark, options.StopBeforeMark, ResolveStopAt(options.StopMarkAfter, "STOPAT", verb));
        _ = this.Databases.TryGetValue(databaseName, out var existing);
        if (devices is null)
        {
            if (existing is null)
                throw SimulatedSqlException.RestoreCannotCreateDatabase(databaseName, verb);
            if (existing.PendingRestore is null)
                throw SimulatedSqlException.RestoreAlreadyRecovered(verb);
            if (options.NoRecovery || options.Standby is not null || stop.IsSet)
                throw new NotSupportedException($"{verb} without FROM takes only WITH RECOVERY in the simulator.");
            this.RejectRestoreWithoutPermission(batch, existing, verb);
            this.RecoverAlone(batch, existing, verb, options.Broker);
            return true;
        }
        if (existing is not null)
            this.RejectRestoreWithoutPermission(batch, existing, verb);
        else if (!PermissionEnforcement.HasDatabaseDdlAuthority(batch, Permission.CreateAnyDatabase))
            throw SimulatedSqlException.BackupPermissionDenied("CREATE DATABASE", MasterDatabaseName, verb);
        // A database the restoring session is in is refused before the device
        // is opened (probed 2026-10-19 against SQL Server 2025).
        if (existing is not null && ReferenceEquals(connection.CurrentDatabase, existing))
            throw SimulatedSqlException.RestoreDatabaseInUseBySession(existing.Name, verb);

        var device = SingleDevice(devices, verb);
        var media = this.ReadMediaForRestore(batch, device, verb, options.MediaName);
        if (options.File > media.Sets.Count)
            throw SimulatedSqlException.BackupFileIdIncorrectlyFormed(options.File, device.Display, verb);
        var set = media.Sets[options.File - 1];
        if (options.Checksum && !set.HasChecksums)
            throw SimulatedSqlException.RestoreChecksumUnavailable(verb);
        // RESTORE DATABASE of a log set restores it as RESTORE LOG does, and says so (probed 2026-10-16 against SQL Server 2025).
        if (set.Type == BackupSet.LogType)
            verb = "RESTORE LOG";
        if (set.Type != BackupSet.FullType && existing is null)
            throw SimulatedSqlException.RestoreCannotCreateDatabase(databaseName, verb);
        var image = ReadImage(set, device, verb);
        var ending = options.NoRecovery ? RestoreEnding.Restoring : options.Standby is not null ? RestoreEnding.Standby : RestoreEnding.Recovered;
        var run = new RestoreRun(batch, media, set, device, options, verb, image, stop, ending);
        if (set.Type == BackupSet.FullType && !isLog)
            this.RestoreFullSet(run, existing, databaseName);
        else if (set.Type == BackupSet.DifferentialType && !isLog)
            this.RestoreDifferentialSet(run, existing!);
        else
            this.RestoreLogSet(run, existing!);
        return true;
    }

    /// <summary>What one <c>RESTORE … FROM</c> statement works with.</summary>
    private sealed class RestoreRun(BatchContext batch, BackupMedia media, BackupSet set, BackupDeviceTarget device, BackupOptions options, string verb, DatabaseImage image, StopPoint stop, RestoreEnding ending)
    {
        public readonly BatchContext Batch = batch;
        public readonly BackupMedia Media = media;
        public readonly BackupSet Set = set;
        public readonly BackupDeviceTarget Device = device;
        public readonly BackupOptions Options = options;
        public readonly string Verb = verb;
        public readonly DatabaseImage Image = image;
        public readonly StopPoint Stop = stop;
        public RestoreEnding Ending = ending;
        public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
    }

    /// <summary>The set's image, refusing a payload that doesn't read as one.</summary>
    private static DatabaseImage ReadImage(BackupSet set, BackupDeviceTarget device, string verb)
    {
        try
        {
            return DatabaseImage.FromPayload(set.Payload);
        }
        catch (InvalidDataException)
        {
            throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(device.Display, verb);
        }
    }

    /// <summary>
    /// A <c>STOPAT</c> or <c>AFTER</c> value as the time it names, null when
    /// none was written. NULL or an empty string is Msg 3217 state 2, a string
    /// that isn't a time state 1 (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static DateTime? ResolveStopAt(SqlValue? value, string option, string verb)
    {
        if (value is not { } written)
            return null;
        if (written.IsNull || (written.Type.Category == SqlTypeCategory.String && written.AsString.Trim().Length == 0))
            throw SimulatedSqlException.RestoreInvalidStopAt(option, verb, state: 2);
        try
        {
            return written.CoerceTo(SqlType.DateTime).AsDateTime;
        }
        catch (SimulatedSqlException)
        {
            throw SimulatedSqlException.RestoreInvalidStopAt(option, verb);
        }
    }

    /// <summary>A restore over an existing database takes <c>CREATE ANY DATABASE</c> or the database's ownership.</summary>
    private void RejectRestoreWithoutPermission(BatchContext batch, Database existing, string verb)
    {
        if (!PermissionEnforcement.HasDatabaseDdlAuthority(batch, Permission.CreateAnyDatabase)
            && !(this.Logins.TryGetValue(batch.Connection.Security.Effective.LoginName, out var login) && BuiltInToken.Equals(login.Name, existing.OwnerLoginName)))
        {
            throw SimulatedSqlException.RestorePermissionDenied(existing.Name, verb);
        }
    }

    /// <summary>
    /// Waits for the database to be free of other sessions, as every restore
    /// onto an existing database does: Msg 3102 for the restoring session's
    /// own, Msg 3101 when another stays past the wait.
    /// </summary>
    private void TakeExclusiveAccess(BatchContext batch, Database existing, string verb)
    {
        if (ReferenceEquals(batch.Connection.CurrentDatabase, existing))
            throw SimulatedSqlException.RestoreDatabaseInUseBySession(existing.Name, verb);
        if (!WaitForOtherSessionsToLeave(batch, existing, TimeSpan.FromMilliseconds(this.RestoreExclusiveAccessWaitMillis), singleUserToo: true))
            throw SimulatedSqlException.RestoreExclusiveAccess(verb);
    }

    /// <summary>A full set: creates or replaces the database, starting a restore sequence unless it recovers.</summary>
    private void RestoreFullSet(RestoreRun run, Database? existing, string databaseName)
    {
        var (batch, set, options, verb, image) = (run.Batch, run.Set, run.Options, run.Verb, run.Image);
        if (existing is not null)
        {
            this.TakeExclusiveAccess(batch, existing, verb);
            if (!options.Replace)
            {
                if (existing.DatabaseGuid != image.DatabaseGuid)
                    throw SimulatedSqlException.RestoreOverOtherDatabase(existing.Name, verb);
                // A stop point the log backups reach overwrites the log as REPLACE
                // does; one past the last needs the tail backed up (probed 2026-10-16
                // against SQL Server 2025).
                if (existing.PendingRestore is null && existing.State != DatabaseState.Restoring && existing.RecoveryModel != RecoveryModel.Simple)
                {
                    if (run.Stop.At is not { } stopAt)
                        throw SimulatedSqlException.RestoreTailOfLogNotBackedUp(existing.Name, verb);
                    if (existing.BackupChain.LastLogBackupUtc is not { } lastLogBackup || stopAt > lastLogBackup)
                        throw SimulatedSqlException.RestoreStopAtNeedsTailOfLog(existing.Name, verb);
                }
            }
        }
        if (run.Stop.At is { } at && at < set.StartUtc)
            throw SimulatedSqlException.RestoreStopAtBeforeBackup(verb);
        var targetName = existing?.Name ?? databaseName;
        var physicalNames = this.PlanRestoreFiles(image, options, existing, targetName, verb);
        // A stop point past a full or differential set leaves the database for
        // a log to reach it: a recovery asked for ends in Msg 3013, then 4322.
        var stopsPastSet = run.Stop.IsSet && run.Ending == RestoreEnding.Recovered;
        if (stopsPastSet)
            run.Ending = RestoreEnding.Restoring;
        var restored = this.LoadSetImage(run, existing, targetName, physicalNames);
        restored.PendingRestore = new RestoreSequence { BaseGuid = set.BackupSetGuid };
        this.FinishSetRestore(run, restored, 'D', set.LastLsnBlock, set.LogSequence, set.StartUtc, stoppedInsideLog: false, physicalNames, "Database was restored: ", " Informational message. No user action required.",
            summary: !(stopsPastSet && run.Stop.At is not null));
        if (stopsPastSet && run.Stop.At is not null)
        {
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.RestoreStopAtPastSetMessage(batch, state: 2));
            throw SimulatedSqlException.RestoreStopAtPastDatabaseBackup(verb);
        }
    }

    /// <summary>A differential set onto a database its full set's restore sequence holds.</summary>
    private void RestoreDifferentialSet(RestoreRun run, Database existing)
    {
        var (batch, set, verb) = (run.Batch, run.Set, run.Verb);
        var sequence = RejectOutOfSequence(existing, set, verb);
        if (set.DifferentialBaseGuid != sequence.BaseGuid)
            throw SimulatedSqlException.RestoreDifferentialOutOfSequence(1, verb);
        if (set.LastLsnBlock < sequence.RedoBlock)
            throw SimulatedSqlException.RestoreDifferentialOutOfSequence(3, verb);
        this.TakeExclusiveAccess(batch, existing, verb);
        if (run.Stop.At is { } at && at < set.StartUtc)
            throw SimulatedSqlException.RestoreStopAtBeforeBackup(verb);
        var physicalNames = SequenceFilePaths(existing, run.Options);
        // A stop point past a full or differential set leaves the database for
        // a log to reach it: a recovery asked for ends in Msg 3013, then 4322.
        var stopsPastSet = run.Stop.IsSet && run.Ending == RestoreEnding.Recovered;
        if (stopsPastSet)
            run.Ending = RestoreEnding.Restoring;
        var restored = this.LoadSetImage(run, existing, existing.Name, physicalNames);
        restored.PendingRestore = sequence;
        this.FinishSetRestore(run, restored, 'I', set.LastLsnBlock, set.LogSequence, set.StartUtc, stoppedInsideLog: false, physicalNames, "Database changes were restored. ", " This is an informational message. No user action is required.",
            summary: !(stopsPastSet && run.Stop.At is not null));
        if (stopsPastSet && run.Stop.At is not null)
        {
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.RestoreStopAtPastSetMessage(batch, state: 2));
            throw SimulatedSqlException.RestoreStopAtPastDatabaseBackup(verb);
        }
    }

    /// <summary>
    /// The restore sequence a differential or log set applies to, refusing a
    /// database none holds (Msg 3117) and a set of another database (Msg 3154).
    /// </summary>
    private static RestoreSequence RejectOutOfSequence(Database existing, BackupSet set, string verb)
    {
        if (existing.PendingRestore is not { } sequence)
            throw SimulatedSqlException.RestoreNothingToRollForward(verb);
        if (existing.DatabaseGuid != set.DatabaseGuid)
            throw SimulatedSqlException.RestoreOverOtherDatabase(existing.Name, verb);
        return sequence;
    }

    /// <summary>
    /// A log set onto a database a restore sequence holds: whole, its end
    /// image taking the database's place, or stopped at a time or a mark
    /// inside it, its logged commits replayed up to the stop.
    /// </summary>
    private void RestoreLogSet(RestoreRun run, Database existing)
    {
        var (batch, set, verb, stop) = (run.Batch, run.Set, run.Verb, run.Stop);
        var sequence = RejectOutOfSequence(existing, set, verb);
        if (set.FirstLsnBlock > sequence.RedoBlock)
            throw SimulatedSqlException.RestoreLogTooRecent(DatabaseBackupChain.Lsn(set.FirstLsnBlock), DatabaseBackupChain.Lsn(sequence.RedoBlock), verb);
        if (set.LastLsnBlock < sequence.RedoBlock)
            throw SimulatedSqlException.RestoreLogTooEarly(DatabaseBackupChain.Lsn(set.LastLsnBlock), DatabaseBackupChain.Lsn(sequence.RedoBlock), verb);
        if (set.Type != BackupSet.LogType)
            throw new NotSupportedException($"RESTORE LOG of a {(set.Type == BackupSet.DifferentialType ? "differential" : "full")} backup set isn't modeled by the simulator.");
        this.TakeExclusiveAccess(batch, existing, verb);

        var (commits, unrecordedSince) = DatabaseLog.FromPayload(set.LogPayload);
        // The commits this set holds that the database doesn't yet, in log order.
        var pending = commits.FindAll(commit => commit.Sequence > sequence.LogSequence);
        var stopIndex = pending.Count;
        DateTime? stoppedAt = null;
        var pastEnd = false;
        var markMissed = false;
        if (stop.At is { } at)
        {
            if (at < sequence.PointUtc && set.FirstLsnBlock < sequence.RedoBlock)
            {
                _ = ReportRestoreFiles(batch, set, existing.Name, run.Options.File);
                LogSessionLine(batch, "The specified STOPAT time is too early. All or part of the database is already rolled forward beyond that point.");
                throw SimulatedSqlException.RestoreStopAtTooEarly(verb);
            }
            if (at >= set.StartUtc)
            {
                pastEnd = true;
            }
            else
            {
                stopIndex = pending.FindIndex(commit => commit.CommitUtc > at);
                if (stopIndex < 0)
                    stopIndex = pending.Count;
                stoppedAt = at;
            }
        }
        else if (stop.Mark is { } mark)
        {
            var found = pending.FindIndex(commit => string.Equals(commit.MarkName, mark, StringComparison.Ordinal) && (stop.MarkAfter is not { } after || commit.MarkUtc >= after));
            if (found < 0)
            {
                markMissed = true;
            }
            else
            {
                stopIndex = stop.BeforeMark ? found : found + 1;
                stoppedAt = pending[found].MarkUtc;
            }
        }
        if (pastEnd || markMissed)
            run.Ending = RestoreEnding.Restoring;

        var stoppedInside = stoppedAt is not null;
        Database restored;
        long logSequence;
        if (!stoppedInside)
        {
            restored = this.LoadSetImage(run, existing, existing.Name, SequenceFilePaths(existing, run.Options));
            logSequence = set.LogSequence;
        }
        else
        {
            restored = this.StopInsideLog(run, existing, pending, stopIndex, unrecordedSince, stoppedAt!.Value);
            logSequence = stopIndex == 0 ? sequence.LogSequence : pending[stopIndex - 1].Sequence;
        }
        restored.PendingRestore = sequence;
        // A stop inside the set leaves the next set to need it again from its start.
        this.FinishSetRestore(run, restored, 'L', stoppedInside ? set.FirstLsnBlock : set.LastLsnBlock, logSequence, stoppedAt ?? set.StartUtc, stoppedInside, null, "Log was restored. ", " This is an informational message. No user action is required.",
            stopAt: stop.At ?? stoppedAt, markName: stop.Mark, stopBefore: stop.Mark is null ? null : stop.BeforeMark,
            beforeSummary: pastEnd ? SimulatedSqlException.RestoreStopAtPastSetMessage(batch, state: 1) : markMissed ? SimulatedSqlException.RestoreMarkNotReachedMessage(batch) : null);
    }

    /// <summary>
    /// Brings the database to the stop inside a log set: forward from where
    /// it stands through the first <paramref name="stopIndex"/> pending
    /// commits, or — when one of those changed a definition — back from the
    /// set's end image over the commits after the stop. A stop with a
    /// definition change on both sides, or inside a span the log stopped
    /// recording, isn't reproduced.
    /// </summary>
    private Database StopInsideLog(RestoreRun run, Database existing, List<LoggedCommit> pending, int stopIndex, DateTime? unrecordedSince, DateTime stoppedAt)
    {
        var applied = pending.GetRange(0, stopIndex);
        var undone = pending.GetRange(stopIndex, pending.Count - stopIndex);
        var unrecorded = unrecordedSince is { } since && stoppedAt >= since;
        if (!unrecorded && !applied.Exists(static commit => commit.ChangedDefinitions))
        {
            existing.State = DatabaseState.Restoring;
            Replay(existing, applied, inverse: false);
            this.CatalogRows.Invalidate();
            return existing;
        }
        if (unrecordedSince is null && !undone.Exists(static commit => commit.ChangedDefinitions))
        {
            var ending = run.Ending;
            run.Ending = RestoreEnding.Restoring;
            var restored = this.LoadSetImage(run, existing, existing.Name, SequenceFilePaths(existing, run.Options));
            run.Ending = ending;
            Replay(restored, undone, inverse: true);
            this.CatalogRows.Invalidate();
            return restored;
        }
        throw new NotSupportedException(unrecorded || unrecordedSince is not null
            ? $"RESTORE LOG … WITH STOPAT into a span of the log the simulator stopped recording ({DatabaseLog.CapacityBytes / (1024 * 1024)} MB of rows between two log backups) isn't modeled."
            : "RESTORE LOG … WITH STOPAT between two definition changes in one log backup isn't modeled by the simulator.");
    }

    /// <summary>A replay that can't find what the log names is a shape the simulator doesn't reproduce, never a wrong restore.</summary>
    private static void Replay(Database database, List<LoggedCommit> commits, bool inverse)
    {
        try
        {
            LogReplay.Apply(database, commits, inverse);
        }
        catch (InvalidOperationException ex)
        {
            throw new NotSupportedException($"RESTORE LOG … WITH STOPAT couldn't replay the log to its stop point: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The paths a differential or log set restores the database's files to:
    /// where the sequence's full restore put them, <c>MOVE</c> aside.
    /// </summary>
    private static Dictionary<string, string> SequenceFilePaths(Database existing, BackupOptions options)
    {
        var paths = FilePathsOf(existing);
        foreach (var (logicalName, path) in options.Moves)
            paths[logicalName] = path;
        return paths;
    }

    /// <summary>
    /// Restores the set's image into the database — in place when its
    /// definitions are the image's, else rebuilt — leaving it in the state
    /// the restore's ending names, and returns the database that holds the
    /// name afterwards.
    /// </summary>
    private Database LoadSetImage(RestoreRun run, Database? existing, string targetName, Dictionary<string, string> physicalNames)
    {
        var finalState = run.Ending == RestoreEnding.Restoring ? DatabaseState.Restoring : DatabaseState.Online;
        if (existing is null || !this.TryRestoreInPlace(run.Image, existing, physicalNames, finalState))
            this.RestoreAsNewDatabase(run.Image, existing, targetName, physicalNames, finalState);
        var restored = this.Databases[targetName];
        restored.ResetLog(run.Set.LogSequence);
        restored.IsInStandby = false;
        return restored;
    }

    /// <summary>
    /// What every set's restore ends with: the sequence's new position, the
    /// ending's state (recovered, restoring or standby), msdb's history, the
    /// messages and the error log's lines (probed 2026-10-16 against SQL
    /// Server 2025: a full or differential set starts the database up
    /// restoring first, a recovery starts it up again and says it is
    /// available, and only <c>RESTORE DATABASE</c> logs its summary).
    /// </summary>
    private void FinishSetRestore(RestoreRun run, Database restored, char type, long redoBlock, long logSequence, DateTime pointUtc, bool stoppedInsideLog,
        Dictionary<string, string>? physicalNames, string logLead, string logTail, DateTime? stopAt = null, string? markName = null, bool? stopBefore = null, SimulatedError? beforeSummary = null, bool summary = true)
    {
        var (batch, set, options, verb) = (run.Batch, run.Set, run.Options, run.Verb);
        var sequence = restored.PendingRestore!;
        // A standby database restored on leaves standby until the restore's own ending says otherwise.
        if (restored.IsInStandby)
        {
            restored.IsInStandby = false;
            restored.IsReadOnly = set.IsReadOnly;
        }
        sequence.RedoBlock = redoBlock;
        sequence.LogSequence = logSequence;
        sequence.PointUtc = pointUtc;
        sequence.LastSet = set;
        sequence.StoppedInsideLog = stoppedInsideLog;
        if (options.RestrictedUser)
            restored.UserAccess = 2;
        // A restored database's broker comes back disabled unless the restore
        // names a switch, which applies as it recovers (probed 2026-10-16
        // against SQL Server 2025).
        restored.BrokerEnabled = false;
        if (type != 'L')
        {
            LogSessionLine(batch, $"Starting up database '{restored.Name}'.");
            LogDatabaseNotRecovered(batch, restored, "RESTORING");
        }
        SimulatedError? brokerNotice = null;
        switch (run.Ending)
        {
            case RestoreEnding.Recovered:
                this.EndRestoreSequence(batch, restored);
                brokerNotice = this.SwitchRestoredBroker(batch, restored, options.Broker);
                break;
            case RestoreEnding.Standby:
                this.EnterStandby(batch, restored, options.Standby!);
                break;
            default:
                break;
        }
        this.RecordRestoreHistory(batch, run.Media, set, run.Device.Display, options.File, restored.Name, type, options.Replace, physicalNames ?? FilePathsOf(restored),
            recovered: run.Ending == RestoreEnding.Recovered, stopAt, markName, stopBefore);
        this.CatalogRows.Invalidate();
        this.NoteDatabaseAdmissionChanged();
        if (options.Stats is { } step)
        {
            for (var percent = step; percent < 100; percent += step)
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.BackupPercentProcessedMessage(batch, percent));
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.BackupPercentProcessedMessage(batch, 100));
        }
        var completed = ReportRestoreFiles(batch, set, restored.Name, options.File, verb, run.Stopwatch.Elapsed.TotalSeconds, summary, beforeSummary, brokerNotice);
        var at = batch.CurrentStatement.UtcNow;
        this.ErrorLog.Write(at, BackupProcess, string.Create(CultureInfo.InvariantCulture,
            $"{logLead}Database: {restored.Name}, creation date(time): {BackupCreationDate()}, first LSN: {BackupLsn(set.FirstLsnBlock)}, last LSN: {BackupLsn(set.LastLsnBlock)}, number of dump devices: 1, device information: (FILE={options.File}, TYPE=DISK: {{'{run.Device.Logged}'}}).{logTail}"));
        if (type != 'L' && completed is not null)
            this.ErrorLog.Write(at, BackupProcess, completed);
        batch.Connection.LastStatementRowCount = 0;
    }

    /// <summary>Each file's path as a database holds it, by logical name.</summary>
    private static Dictionary<string, string> FilePathsOf(Database database)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in database.FilesInOrder())
            paths[file.Name] = file.PhysicalName;
        return paths;
    }

    /// <summary>
    /// The <c>Processed N pages</c> messages — a log set reports no page of a
    /// data file and its own of the log file — then, with
    /// <paramref name="summary"/>, the closing Msg 3014, whose text it returns.
    /// </summary>
    private static string? ReportRestoreFiles(BatchContext batch, BackupSet set, string databaseName, int position, string? verb = null, double seconds = 0, bool summary = false, SimulatedError? beforeSummary = null, SimulatedError? brokerNotice = null)
    {
        var messages = batch.Connection.PendingMessages;
        long pages = 0;
        if (set.Type == BackupSet.LogType)
        {
            foreach (var file in set.Files)
            {
                if (!file.IsLog)
                    messages.Enqueue(SimulatedSqlException.BackupFileProcessedMessage(batch, 0, databaseName, file.LogicalName, position));
            }
        }
        foreach (var (logicalName, filePages) in set.FilePages)
        {
            messages.Enqueue(SimulatedSqlException.BackupFileProcessedMessage(batch, filePages, databaseName, logicalName, position));
            pages += filePages;
        }
        if (beforeSummary is not null)
            messages.Enqueue(beforeSummary);
        if (brokerNotice is not null)
            messages.Enqueue(brokerNotice);
        if (!summary)
            return null;
        var completed = SimulatedSqlException.BackupCompletedMessage(batch, verb!, pages, seconds);
        messages.Enqueue(completed);
        return completed.Message;
    }

    /// <summary>
    /// <c>RESTORE { DATABASE | LOG } name WITH RECOVERY</c> alone: recovers a
    /// database a restore sequence holds where it stands, with real's Msg 3014
    /// of no pages.
    /// </summary>
    private void RecoverAlone(BatchContext batch, Database database, string verb, BrokerSwitch? broker)
    {
        var stopwatch = Stopwatch.StartNew();
        this.TakeExclusiveAccess(batch, database, verb);
        this.EndRestoreSequence(batch, database);
        if (this.SwitchRestoredBroker(batch, database, broker) is { } brokerNotice)
            batch.Connection.PendingMessages.Enqueue(brokerNotice);
        this.CatalogRows.Invalidate();
        this.NoteDatabaseAdmissionChanged();
        var completed = SimulatedSqlException.BackupCompletedMessage(batch, verb, 0, stopwatch.Elapsed.TotalSeconds);
        batch.Connection.PendingMessages.Enqueue(completed);
        this.ErrorLog.Write(batch.CurrentStatement.UtcNow, BackupProcess, completed.Message);
        batch.Connection.LastStatementRowCount = 0;
    }

    /// <summary>
    /// Applies the broker switch a restore names to the database it recovered:
    /// Msg 9773's notice, the broker left disabled, when another database's
    /// enabled broker carries its broker id.
    /// </summary>
    private SimulatedError? SwitchRestoredBroker(BatchContext batch, Database database, BrokerSwitch? broker) =>
        broker is { } change && !this.TrySwitchBroker(batch, database, change) ? SimulatedSqlException.RestoredBrokerDisabledMessage(batch, database.Id) : null;

    /// <summary>
    /// Recovers a database out of its restore sequence: online, writable as
    /// its last set left it, its backup chain continuing from the last set,
    /// and — after a stop inside a log set — on a recovery fork of its own,
    /// which <c>sys.database_recovery_status</c> reports.
    /// </summary>
    private void EndRestoreSequence(BatchContext batch, Database database)
    {
        var sequence = database.PendingRestore!;
        database.PendingRestore = null;
        if (sequence.StandbyFile is { } standby)
            this.DeleteStandbyFile(standby);
        if (database.IsInStandby)
        {
            database.IsInStandby = false;
            database.IsReadOnly = sequence.LastSet?.IsReadOnly ?? false;
        }
        database.State = DatabaseState.Online;
        if (sequence.StoppedInsideLog)
        {
            database.ForkPoint = (database.ForkPoint?.FirstFork ?? database.RecoveryForkGuid, sequence.RedoBlock + 8);
            database.RecoveryForkGuid = Guid.NewGuid();
        }
        if (sequence.LastSet is { } set)
            ContinueBackupChainFrom(database, set, sequence.RedoBlock, batch.CurrentStatement.UtcNow);
        database.ResetLog(sequence.LogSequence);
        LogDatabaseStartup(batch, database);
        this.ErrorLog.Write(batch.CurrentStatement.UtcNow, BackupProcess, $"Restore is complete on database '{database.Name}'.  The database is now available.");
    }

    /// <summary>
    /// Opens a database a restore sequence holds read-only, as <c>STANDBY</c>
    /// does, writing the undo file the next restore would read — which the
    /// server's disk then holds until recovery deletes it (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private void EnterStandby(BatchContext batch, Database database, string file)
    {
        var path = ResolveBackupDevicePath(file);
        if (this.WriteBackupDevice(path, new byte[BackupMedia.MinimumMediaLength]) is var osError and not 0)
            throw SimulatedSqlException.CannotOpenBackupDevice(path, osError, "RESTORE DATABASE");
        database.PendingRestore!.StandbyFile = path;
        database.IsInStandby = true;
        database.IsReadOnly = true;
        database.State = DatabaseState.Online;
        LogDatabaseStartup(batch, database);
    }

    /// <summary>Deletes a standby undo file the in-memory disk holds; one the host's delegate opened stays, as it has no delete.</summary>
    private void DeleteStandbyFile(string path)
    {
        if (this.OpenBackupFile is null)
            _ = this.BackupDevices.TryRemove(BackupDeviceKey(path), out _);
    }

    /// <summary>
    /// The backup chain a recovered database goes on with: its last set is
    /// its differential base when it was a full one, and outside <c>SIMPLE</c>
    /// recovery the next log backup continues from <paramref name="lastLogBlock"/>,
    /// as if one ran as the restore finished (probed 2026-10-16 against SQL
    /// Server 2025, where a recovery stopped inside a log set reports the
    /// set's first LSN).
    /// </summary>
    private static void ContinueBackupChainFrom(Database database, BackupSet set, long lastLogBlock, DateTime restoredUtc)
    {
        var chain = database.BackupChain;
        if (set.Type == BackupSet.FullType)
        {
            chain.DifferentialBaseBlock = set.CheckpointLsnBlock;
            chain.DifferentialBaseGuid = set.BackupSetGuid;
            chain.DifferentialBaseUtc = set.StartUtc;
        }
        else
        {
            chain.DifferentialBaseBlock = set.Type == BackupSet.DifferentialType ? set.DifferentialBaseBlock : set.DatabaseBackupLsnBlock;
            chain.DifferentialBaseGuid = set.Type == BackupSet.DifferentialType ? set.DifferentialBaseGuid : set.FileDifferentialBaseGuid;
        }
        chain.LogBlock = Math.Max(chain.LogBlock, set.LastLsnBlock);
        chain.LastCheckpointBlock = set.CheckpointLsnBlock;
        chain.TakeBaseGenerations(UserTables(database));
        if (database.RecoveryModel == RecoveryModel.Simple)
        {
            chain.BreakLogChain();
            return;
        }
        chain.LastLogBackupBlock = lastLogBlock;
        chain.LastLogBackupUtc = restoredUtc;
        chain.LogChainContinued = true;
    }
}
