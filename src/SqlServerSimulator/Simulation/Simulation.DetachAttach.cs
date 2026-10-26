using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Backup;

namespace SqlServerSimulator;

// Detaching a database to its files and attaching it from them: DBCC DETACHDB,
// sp_detach_db, CREATE DATABASE … FOR ATTACH / FOR ATTACH_REBUILD_LOG,
// sp_attach_db and sp_attach_single_file_db. A detached database is a
// DatabaseImage written into its primary data file on the server's disk (the
// in-memory disk or Simulation.OpenServerFile), each other file holding its
// identity alone. Behavior probed 2026-10-16 against SQL Server 2025 by
// calling each and reading the messages, return codes, catalog and files;
// see docs/claude/backup-restore.md.
partial class Simulation
{
    /// <summary>
    /// <c>sp_detach_db [@dbname] [, @skipchecks] [, @keepfulltextindexfile]</c>:
    /// a NULL name, or an option other than <c>true</c> / <c>false</c>, is the
    /// usage message from line 17, 25 or 33, a missing database Msg 15010 from
    /// line 40 and a transaction Msg 15002 from line 47, each returning 1.
    /// <c>@skipchecks = 'false'</c> alone runs <c>sp_updatestats</c> in the
    /// database first; then <c>DBCC DETACHDB</c> runs as a dynamic batch,
    /// whose refusals name no procedure and leave the return code 0.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDetachDb(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_detach_db", calledAs, arguments,
        [
            new("dbname", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
            new("skipchecks", SqlType.NVarchar, 10, SqlValue.Null(SqlType.NVarchar)),
            new("keepfulltextindexfile", SqlType.NVarchar, 10, SqlValue.FromNVarchar("true")),
        ]);
        static bool IsSwitch(SqlValue value) => value.IsNull || value.AsString.Equals("true", StringComparison.OrdinalIgnoreCase) || value.AsString.Equals("false", StringComparison.OrdinalIgnoreCase);
        var usageLine = values[0].IsNull ? 17 : !IsSwitch(values[1]) ? 25 : !IsSwitch(values[2]) ? 33 : 0;
        if (usageLine != 0)
        {
            SetReturnCode(batch, returnCodeVar, 1);
            yield return ProcedureMessage(batch, calledAs, usageLine, 15354, "Usage: sp_detach_db <dbname>, [TRUE|FALSE], [TRUE|FALSE]");
            yield break;
        }
        var name = values[0].AsString;
        // The procedure answers its own refusals' return code.
        if (!this.Databases.TryGetValue(name, out var database))
        {
            SetReturnCode(batch, returnCodeVar, 1);
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpDatabaseDoesNotExist(name), 40);
        }
        if (batch.Connection.CurrentTransaction is not null)
        {
            SetReturnCode(batch, returnCodeVar, 1);
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction("sp_detach_db"), 47);
        }

        if (values[1] is { IsNull: false } skip && skip.AsString.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, "exec sys.sp_updatestats", preDeclaredVariables: null, viaSystemProcedure: true, runsIn: database))
                yield return outcome;
        }
        var keep = values[2].IsNull || values[2].AsString.Equals("true", StringComparison.OrdinalIgnoreCase);
        foreach (var outcome in this.ExecuteDynamicBatch(batch, $"dbcc detachdb({QuoteName(database.Name)}, {(keep ? 1 : 0)})", preDeclaredVariables: null, viaSystemProcedure: true))
            yield return outcome;
        SetReturnCode(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// <c>DBCC DETACHDB ( database [, keep_fulltext] ) [WITH NO_INFOMSGS]</c>:
    /// writes the database to its files and takes it off the server, silently.
    /// An empty name is the current database and a NULL one Msg 2560; then a
    /// database that doesn't exist is Msg 7939, a system database Msg 7940, a
    /// principal neither sysadmin nor in its <c>db_owner</c> Msg 7983, and the
    /// session's own current database Msg 3703, which ends the batch. A
    /// restoring database is detached with Msg 947, its files left
    /// unattachable. Other sessions using the database don't hold it back.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccDetachDb(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.RequireArgumentCount(1, 2);
        var simulation = batch.Connection.Simulation;
        var argument = dbcc.Arguments[0];
        var name = argument.Kind == DbccArgumentKind.Name
            ? argument.Name.Leaf
            : dbcc.StringArgument(batch, 0) ?? throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        if (dbcc.Arguments.Count > 1)
            _ = dbcc.IntegerArgument(batch, 1);
        var database = name.Length == 0
            ? batch.CurrentDatabase
            : simulation.Databases.TryGetValue(name, out var named) ? named : throw SimulatedSqlException.DetachDatabaseDoesNotExist(name);
        // An option the command doesn't take is refused once the database is found.
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        if (SystemDatabaseNames.Contains(database.Name))
            throw SimulatedSqlException.CannotDetachSystemDatabase();
        if (!IsSysadminSession(batch) && !PermissionEnforcement.IsOwner(batch, database))
            throw SimulatedSqlException.DbccDatabasePermissionDenied(batch.Connection.Security.Effective.LoginName, "detachdb", database.Name);
        if (ReferenceEquals(batch.Connection.CurrentDatabase, database))
            throw SimulatedSqlException.DetachDatabaseInUse(database.Name);
        var restoring = database.State == DatabaseState.Restoring;
        simulation.DetachDatabase(batch, database, restoring);
        if (!restoring)
            return [];
        var closing = SimulatedSqlException.ErrorClosingDatabase(database.Name);
        LogRaisedError(batch, closing.Number, closing.Class, closing.State, closing.Message);
        throw closing;
    }

    /// <summary>
    /// Writes <paramref name="database"/> to its files and takes it off the
    /// server: its image into the primary data file (none for a restoring
    /// database), its identity into each other file.
    /// </summary>
    private void DetachDatabase(BatchContext batch, Database database, bool restoring)
    {
        DatabaseImage? image = null;
        if (!restoring)
        {
            var held = this.LockTablesForBackup(batch, UserTables(database));
            try
            {
                image = DatabaseImage.Capture(database, this.BrokerIfUsed);
            }
            finally
            {
                foreach (var resource in held)
                    this.LockManager.Release(resource, LockMode.Shared, batch.Connection.LockOwner);
            }
        }
        this.WriteDetachedFiles(database, image, restoring);
        lock (this.Databases)
            _ = this.Databases.TryRemove(KeyValuePair.Create(database.Name, database));
        this.NoteDatabaseAdmissionChanged();
        this.BumpSchemaVersion();
        this.CatalogRows.Invalidate();
    }

    /// <summary>
    /// Writes each of <paramref name="database"/>'s files to the server's
    /// disk as a detached database's: the primary data file with
    /// <paramref name="image"/>, the others with the database's identity.
    /// </summary>
    private void WriteDetachedFiles(Database database, DatabaseImage? image, bool restoring)
    {
        var payload = image?.ToPayload();
        foreach (var file in database.FilesInOrder())
        {
            if (file.IsContainer)
                continue;
            var content = new DetachedDatabaseFile
            {
                DatabaseGuid = database.DatabaseGuid,
                FileId = file.FileId,
                IsLog = file.IsLog,
                WasRestoring = restoring,
                Payload = file.FileId == 1 ? payload : null,
            };
            if (this.WriteBackupDevice(file.PhysicalName, content.ToBytes()) is not 0)
                throw new NotSupportedException($"The simulator couldn't write the detached database's file '{file.PhysicalName}' through OpenServerFile.");
        }
    }

    /// <summary>
    /// Leaves a dropped OFFLINE database's files on the server's disk, as real
    /// does — attachable, unlike an online database's, which a drop deletes.
    /// A database whose image can't be captured leaves files that attach
    /// refuses.
    /// </summary>
    internal void KeepDroppedOfflineFiles(Database database)
    {
        DatabaseImage? image;
        try
        {
            image = DatabaseImage.Capture(database, this.BrokerIfUsed);
        }
        catch (NotSupportedException)
        {
            image = null;
        }
        this.WriteDetachedFiles(database, image, restoring: false);
    }

    /// <summary>What a <c>FOR ATTACH</c> clause's <c>WITH</c> list said.</summary>
    private sealed class AttachOptions(bool rebuildLog)
    {
        /// <summary><c>FOR ATTACH_REBUILD_LOG</c>, which takes no option.</summary>
        public readonly bool RebuildLog = rebuildLog;

        public BrokerSwitch? Broker;
        public bool RestrictedUser;
    }

    /// <summary>
    /// The <c>WITH</c> list after <c>FOR ATTACH</c>, entered on <c>WITH</c>:
    /// one of <c>ENABLE_BROKER</c> / <c>NEW_BROKER</c> /
    /// <c>ERROR_BROKER_CONVERSATIONS</c>, and <c>RESTRICTED_USER</c>. A second
    /// broker option, or any option after <c>FOR ATTACH_REBUILD_LOG</c>, is the
    /// syntax error at it (state 29, or 45 for <c>RESTRICTED_USER</c>), and an
    /// unknown word state 44.
    /// </summary>
    private static void ParseAttachOptions(ParserContext context, AttachOptions options)
    {
        do
        {
            if (context.GetNextRequired() is not Parser.Tokens.Name { Value: var word } wordToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (AttachBrokerOption(word) is { } broker)
            {
                if (options.RebuildLog || options.Broker is not null)
                    throw SimulatedSqlException.SyntaxErrorNear(wordToken, state: 29);
                options.Broker = broker;
            }
            else if (BuiltInToken.Equals(word, "RESTRICTED_USER"))
            {
                if (options.RebuildLog)
                    throw SimulatedSqlException.SyntaxErrorNear(wordToken, state: 45);
                options.RestrictedUser = true;
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(wordToken, state: 44);
            }
            context.MoveNextOptional();
        }
        while (context.Token is Parser.Tokens.Operator { Character: ',' });
    }

    /// <summary>The broker switch an attach option names, or null for another word.</summary>
    private static BrokerSwitch? AttachBrokerOption(string word)
    {
        Span<char> upper = stackalloc char[word.Length];
        _ = word.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "ENABLE_BROKER" => BrokerSwitch.Enable,
            "ERROR_BROKER_CONVERSATIONS" => BrokerSwitch.ErrorConversations,
            "NEW_BROKER" => BrokerSwitch.New,
            _ => null,
        };
    }

    /// <summary>
    /// Opens the file at <paramref name="path"/> for an attach: null when
    /// nothing is there, refusing a path no file can be at or one an online
    /// database holds (Msg 5120 with operating system error 5 or 32), and a
    /// file that holds no database (Msg 5172).
    /// </summary>
    private DetachedDatabaseFile? OpenAttachFile(BatchContext batch, string path, bool mustExist)
    {
        if (CanonicalServerPath(path) is not { } canonical)
        {
            var activation = SimulatedSqlException.FileActivationError(path);
            LogRaisedError(batch, activation.Number, activation.Class, activation.State, activation.Message);
            throw activation;
        }
        var osError = 0;
        foreach (var (_, database) in this.Databases)
        {
            foreach (var file in database.FilesInOrder())
            {
                if (CanonicalServerPath(file.PhysicalName) is { } held && held.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                    osError = 32;
            }
        }
        byte[]? bytes = null;
        if (osError == 0)
        {
            if (this.IsServerDirectory(canonical))
                osError = 5;
            else if ((bytes = this.ReadBackupDevice(path, out var readError)) is null && !this.IsServerFile(canonical))
                osError = readError;
        }
        if (osError != 0)
        {
            if (osError == 2 && !mustExist)
                return null;
            throw FileOpenFailure(batch, path, osError);
        }
        return (bytes is null ? null : DetachedDatabaseFile.TryRead(bytes)) ?? throw SimulatedSqlException.NotADatabaseFile(path);
    }

    /// <summary>
    /// <c>CREATE DATABASE name ON &lt;files&gt; [LOG ON &lt;files&gt;] FOR ATTACH
    /// | FOR ATTACH_REBUILD_LOG</c>: rebuilds the database its primary data
    /// file holds under <paramref name="name"/>, each file at the path the
    /// list gives it or else the one it was detached from. The first file must
    /// be the primary (Msg 1829), every file the primary's database's (Msg
    /// 5173) and only one primary (Msg 1830); a log file missing from where it
    /// was is rebuilt there, with Msgs 5111 and 5182. The database comes back
    /// online under the smallest free <c>database_id</c>, owned by the
    /// attaching login, its compatibility level and recovery model as they
    /// were, with <c>TRUSTWORTHY</c>, <c>DB_CHAINING</c>, <c>READ_ONLY</c>,
    /// <c>AUTO_CLOSE</c> and its broker off and multi-user — but for the
    /// <c>WITH</c> list's broker switch and <c>RESTRICTED_USER</c>.
    /// </summary>
    private Database AttachDatabase(BatchContext batch, string name, List<FileSpecification> listed, AttachOptions options)
    {
        if (this.Databases.ContainsKey(name))
            throw SimulatedSqlException.DatabaseAlreadyExists(name);

        var primaryPath = listed[0].FileName!;
        var primary = this.OpenAttachFile(batch, primaryPath, mustExist: true)!;
        if (!primary.IsPrimary)
            throw SimulatedSqlException.AttachFirstFileNotPrimary();
        if (primary.WasRestoring)
        {
            LogSessionLine(batch, $"Starting up database '{name}'.");
            throw SimulatedSqlException.CannotAttachRestoringDatabase();
        }
        var image = primary.Payload is { } payload
            ? DatabaseImage.FromPayload(payload)
            : throw new NotSupportedException($"The database in '{primaryPath}' was dropped offline holding something the simulator's database image can't rebuild, so it can't be attached.");

        var found = new Dictionary<int, string> { [1] = primaryPath };
        for (var i = 1; i < listed.Count; i++)
        {
            var path = listed[i].FileName!;
            var content = this.OpenAttachFile(batch, path, mustExist: true)!;
            if (content.IsPrimary)
                throw SimulatedSqlException.AttachTwoPrimaryFiles(primaryPath, path);
            if (content.DatabaseGuid != primary.DatabaseGuid || !image.Files.Exists(file => file.FileId == content.FileId))
                throw FilesDoNotMatch(batch);
            found[content.FileId] = path;
        }

        // Every other file is looked for where the database left it; a log
        // missing there is rebuilt.
        var missingLogs = new List<DatabaseFile>();
        foreach (var file in image.Files)
        {
            if (file.IsContainer || found.ContainsKey(file.FileId))
                continue;
            if (this.OpenAttachFile(batch, file.PhysicalName, mustExist: !file.IsLog) is not { } content)
            {
                missingLogs.Add(file);
                continue;
            }
            if (content.DatabaseGuid != primary.DatabaseGuid || content.FileId != file.FileId)
                throw FilesDoNotMatch(batch);
            found[file.FileId] = file.PhysicalName;
        }
        DatabaseFile? rebuiltLog = null;
        if (missingLogs.Count > 0)
        {
            if (image.Files.Exists(file => file.IsLog && found.ContainsKey(file.FileId)))
            {
                throw FileOpenFailure(batch, missingLogs[0].PhysicalName, 2);
            }
            var first = missingLogs[0];
            rebuiltLog = new DatabaseFile(first.FileId, isLog: true, first.Name, first.PhysicalName, 0, sizePages: 63, maxSizePages: -1, growth: 10, isPercentGrowth: true);
            _ = image.Files.RemoveAll(file => file.IsLog);
            image.Files.Add(rebuiltLog);
        }

        var physicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in image.Files)
            physicalNames[file.Name] = found.GetValueOrDefault(file.FileId, file.PhysicalName);
        this.RestoreAsNewDatabase(image, existing: null, name, physicalNames, DatabaseState.Online);
        var database = this.Databases[name];
        // A rebuilt log reports its unlimited ceiling as -1.
        if (rebuiltLog is not null && database.FindFile(rebuiltLog.FileId) is { } log)
            log.UnlimitedReadsMinusOne = true;

        // The files are the database's own again.
        if (this.OpenServerFile is null)
        {
            foreach (var (_, path) in found)
                _ = this.DiskFiles.TryRemove(BackupDeviceKey(path), out _);
        }
        database.Trustworthy = false;
        database.CrossDatabaseChaining = false;
        database.IsReadOnly = false;
        database.Switches &= ~DatabaseSwitches.AutoClose;
        database.UserAccess = options.RestrictedUser ? (byte)2 : (byte)0;
        database.OwnerLoginName = this.Logins.TryGetValue(batch.Connection.Security.Effective.LoginName, out var attaching) ? attaching.Name : "sa";
        database.BrokerEnabled = false;
        if (this.SwitchRestoredBroker(batch, database, options.Broker) is { } brokerNotice)
            batch.Connection.PendingMessages.Enqueue(brokerNotice);
        this.NoteDatabaseAdmissionChanged();
        this.CatalogRows.Invalidate();

        LogDatabaseStartup(batch, database);
        if (rebuiltLog is not null)
        {
            var path = rebuiltLog.PhysicalName;
            batch.Connection.PendingMessages.Enqueue(batch.InfoMessage(@class: 0, state: 5, number: 5111, $"File activation failure. The physical file name \"{path}\" may be incorrect."));
            batch.Connection.PendingMessages.Enqueue(batch.InfoMessage(@class: 0, state: 1, number: 5182, $"New log file '{path}' was created."));
            LogRaisedError(batch, 17207, 16, 1, $"FileMgr::StartLogFiles: Operating system error {SimulatedSqlException.OperatingSystemErrorText(2)} occurred while creating or opening file '{path}'. Diagnose and correct the operating system error, and retry the operation.");
            LogSessionLine(batch, $"New log file '{path}' was created.");
        }
        return database;
    }

    /// <summary>
    /// Msg 5120 for a file an attach couldn't open, after the Msg 17204 its
    /// failure writes to the error log.
    /// </summary>
    private static SimulatedSqlException FileOpenFailure(BatchContext batch, string path, int osError)
    {
        LogRaisedError(batch, 17204, 16, 1, $"FCB::Open failed: Could not open file {path} for file number 0.  OS error: {SimulatedSqlException.OperatingSystemErrorText(osError)}.");
        return SimulatedSqlException.AttachFileCannotBeOpened(path, osError);
    }

    /// <summary>Msg 5173, which the error log records too.</summary>
    private static SimulatedSqlException FilesDoNotMatch(BatchContext batch)
    {
        var refusal = SimulatedSqlException.FilesDoNotMatchPrimary();
        var first = refusal.Errors[0];
        LogRaisedError(batch, first.Number, first.Class, first.State, first.Message);
        return refusal;
    }

    /// <summary>
    /// <c>sp_attach_db @dbname, @filename1 [, … @filename16]</c> and
    /// <c>sp_attach_single_file_db @dbname, @physname</c>: a NULL name or first
    /// file is Msg 15004 from line 27 or 17; otherwise the procedure runs
    /// <c>CREATE DATABASE … FOR ATTACH</c> over the files as a dynamic batch,
    /// whose refusals name no procedure and return 1.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAttachDb(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var singleFile = procedure == "sp_attach_single_file_db";
        SystemProcedureParameter[] parameters = singleFile
            ? [new("dbname", SqlType.NVarchar, 128), new("physname", SqlType.NVarchar, 260)]
            : [new("dbname", SqlType.NVarchar, 128), new("filename1", SqlType.NVarchar, 260),
                .. Enumerable.Range(2, 15).Select(static i => new SystemProcedureParameter("filename" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), SqlType.NVarchar, 260, SqlValue.Null(SqlType.NVarchar)))];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        if (values[0].IsNull || values[1].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NameCannotBeNull(), singleFile ? 17 : 27);
        var files = string.Join(", ", values.Skip(1).Where(static value => !value.IsNull).Select(static value => $"(FILENAME = N'{value.AsString.Replace("'", "''", StringComparison.Ordinal)}')"));
        foreach (var outcome in this.ExecuteDynamicBatch(batch, $"CREATE DATABASE {QuoteName(values[0].AsString)} ON {files} FOR ATTACH", preDeclaredVariables: null, viaSystemProcedure: true))
            yield return outcome;
    }
}
