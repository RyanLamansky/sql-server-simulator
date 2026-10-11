using System.Globalization;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// BACKUP / RESTORE refusals and messages, probed 2026-10-11 against SQL Server
// 2022 and the common ones re-checked against SQL Server 2025. Every refusal but an option conflict is followed by Msg 3013 naming the
// statement, which is what ERROR_NUMBER() reads in a CATCH block.
partial class SimulatedSqlException
{
    /// <summary>Msg 3013 after <paramref name="error"/>: "<paramref name="verb"/> is terminating abnormally."</summary>
    internal static SimulatedSqlException FollowedByBackupTerminating(SimulatedSqlException error, string verb) =>
        FollowedBy(error, new($"{verb} is terminating abnormally.", 3013, 16, 1));

    /// <summary>Mimics SQL Server error 3021: a backup or restore inside a transaction, which stays open and committable.</summary>
    internal static SimulatedSqlException BackupInTransaction(string verb) =>
        FollowedByBackupTerminating(new("Cannot perform a backup or restore operation within a transaction.", 3021, 16, 0), verb);

    /// <summary>
    /// Mimics SQL Server error 3201: a device that can't be opened — operating
    /// system error 2 for a missing file, 3 for a missing path, 5 for access
    /// denied; state 1 for <c>BACKUP</c>, 2 for <c>RESTORE</c>.
    /// </summary>
    internal static SimulatedSqlException CannotOpenBackupDevice(string path, int osError, string verb) =>
        FollowedByBackupTerminating(new(string.Create(CultureInfo.InvariantCulture, $"Cannot open backup device '{path}'. Operating system error {osError}({osError switch
        {
            2 => "The system cannot find the file specified.",
            3 => "The system cannot find the path specified.",
            _ => "Access is denied.",
        }})."), 3201, 16, verb.StartsWith("BACKUP", StringComparison.Ordinal) ? (byte)1 : (byte)2), verb);

    /// <summary>Mimics SQL Server error 3241: a device of 4 KB or more holding no backup media.</summary>
    internal static SimulatedSqlException MediaFamilyIncorrectlyFormed(string path, string verb) =>
        FollowedByBackupTerminating(new($"The media family on device '{path}' is incorrectly formed. SQL Server cannot process this media family.", 3241, 16, 0), verb);

    /// <summary>Mimics SQL Server error 3254: a device shorter than a media header.</summary>
    internal static SimulatedSqlException BackupVolumeEmpty(string path, string verb) =>
        FollowedByBackupTerminating(new($"The volume on device '{path}' is empty.", 3254, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3287: a <c>FILE</c> naming no backup set on the device.</summary>
    internal static SimulatedSqlException BackupFileIdIncorrectlyFormed(int file, string path, string verb) =>
        FollowedByBackupTerminating(new(string.Create(CultureInfo.InvariantCulture, $"The file ID {file} on device '{path}' is incorrectly formed and can not be read."), 3287, 16, 1), verb);

    /// <summary>
    /// Mimics SQL Server error 3169: a backup written by another version — for
    /// the simulator, by any other build, since the backup holds its storage
    /// encodings.
    /// </summary>
    internal static SimulatedSqlException BackupFromIncompatibleVersion(string writtenBy, string running, string verb) =>
        FollowedByBackupTerminating(new($"The database was backed up on a server running version {writtenBy}. That version is incompatible with this server, which is running version {running}. Either restore the database on a server that supports the backup, or use a backup that is compatible with this server.", 3169, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3101: another session is using the database a restore would replace.</summary>
    internal static SimulatedSqlException RestoreExclusiveAccess(string verb) =>
        FollowedByBackupTerminating(new("Exclusive access could not be obtained because the database is in use.", 3101, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3102: the restoring session's own database is the target.</summary>
    internal static SimulatedSqlException RestoreDatabaseInUseBySession(string databaseName, string verb) =>
        FollowedByBackupTerminating(new($"RESTORE cannot process database '{databaseName}' because it is in use by this session. It is recommended that the master database be used when performing this operation.", 3102, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3159: a restore without <c>REPLACE</c> over a database outside the simple recovery model.</summary>
    internal static SimulatedSqlException RestoreTailOfLogNotBackedUp(string databaseName, string verb) =>
        FollowedByBackupTerminating(new($"The tail of the log for the database \"{databaseName}\" has not been backed up. Use BACKUP LOG WITH NORECOVERY to backup the log if it contains work you do not want to lose. Use the WITH REPLACE or WITH STOPAT clause of the RESTORE statement to just overwrite the contents of the log.", 3159, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3154: a restore without <c>REPLACE</c> over another database.</summary>
    internal static SimulatedSqlException RestoreOverOtherDatabase(string databaseName, string verb) =>
        FollowedByBackupTerminating(new($"The backup set holds a backup of a database other than the existing '{databaseName}' database.", 3154, 16, 4), verb);

    /// <summary>
    /// Mimics SQL Server's Msg 1834 then 3156 for each file another database
    /// holds, then Msg 3119 and 3013.
    /// </summary>
    internal static SimulatedSqlException RestoreFilesInUse(List<(string LogicalName, string Path, string HolderName)> files, string verb)
    {
        SimulatedSqlException? errors = null;
        foreach (var (logicalName, path, holder) in files)
        {
            var pair = FollowedBy(
                new($"The file '{path}' cannot be overwritten.  It is being used by database '{holder}'.", 1834, 16, 1),
                new($"File '{logicalName}' cannot be restored to '{path}'. Use WITH MOVE to identify a valid location for the file.", 3156, 16, 4));
            errors = errors is null ? pair : FollowedBy(errors, pair);
        }
        return FollowedByBackupTerminating(
            FollowedBy(errors!, new("Problems were identified while planning for the RESTORE statement. Previous messages provide details.", 3119, 16, 1)), verb);
    }

    /// <summary>Mimics SQL Server error 3234: a <c>MOVE</c> naming no logical file of the backup set.</summary>
    internal static SimulatedSqlException RestoreLogicalFileNotInSet(string logicalName, string databaseName, string verb) =>
        FollowedByBackupTerminating(new($"Logical file '{logicalName}' is not part of database '{databaseName}'. Use RESTORE FILELISTONLY to list the logical file names.", 3234, 16, 2), verb);

    /// <summary>Mimics SQL Server error 3147: <c>tempdb</c>.</summary>
    internal static SimulatedSqlException BackupOfTempdb(string verb) =>
        FollowedByBackupTerminating(new("Backup and restore operations are not allowed on database tempdb.", 3147, 16, 3), verb);

    /// <summary>Mimics SQL Server error 3108: <c>master</c> restored outside single-user server mode.</summary>
    internal static SimulatedSqlException RestoreMasterOutsideSingleUserMode(string verb) =>
        FollowedByBackupTerminating(new("To restore the master database, the server must be running in single-user mode. For information on starting in single-user mode, see \"How to: Start an Instance of SQL Server (sqlservr.exe)\" in Books Online.", 3108, 16, 1), verb);

    /// <summary>Mimics SQL Server error 911 (state 11): a backup of a database that doesn't exist.</summary>
    internal static SimulatedSqlException BackupDatabaseDoesNotExist(string databaseName, string verb) =>
        FollowedByBackupTerminating(new($"Database '{databaseName}' does not exist. Make sure that the name is entered correctly.", 911, 16, 11), verb);

    /// <summary>Mimics SQL Server error 3206: a logical backup device name, none of which the simulator registers.</summary>
    internal static SimulatedSqlException BackupDeviceDoesNotExist(string name, string verb) =>
        FollowedByBackupTerminating(new($"Backup device '{name}' does not exist. To view existing backup devices, use the sys.backup_devices catalog view. To create a new backup device use either sp_addumpdevice or SQL Server Management Studio.", 3206, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3044: <c>DISK = N''</c>.</summary>
    internal static SimulatedSqlException ZeroLengthBackupDevice(string verb) =>
        FollowedByBackupTerminating(new("Invalid zero-length device name. Reissue the BACKUP statement with a valid device name.", 3044, 16, 1), verb);

    /// <summary>Msg 262 for <paramref name="permission"/> in <paramref name="databaseName"/>, then Msg 3013.</summary>
    internal static SimulatedSqlException BackupPermissionDenied(string permission, string databaseName, string verb) =>
        FollowedByBackupTerminating(DatabasePermissionDenied(permission, databaseName, aborts: false), verb);

    /// <summary>Mimics SQL Server error 3110: a restore over an existing database by a login that may not replace it.</summary>
    internal static SimulatedSqlException RestorePermissionDenied(string databaseName, string verb) =>
        FollowedByBackupTerminating(new($"User does not have permission to RESTORE database '{databaseName}'.", 3110, 14, 1), verb);

    /// <summary>Mimics SQL Server error 3250: a <c>STATS</c> or <c>FILE</c> value out of range, reported as written.</summary>
    internal static SimulatedSqlException BackupParameterOutOfRange(string value, string parameter, string verb) =>
        FollowedByBackupTerminating(new($"The value '{value}' is not within range for the {parameter} parameter.", 3250, 16, 0), verb);

    /// <summary>Mimics SQL Server error 3031: two options that contradict each other; no Msg 3013 follows.</summary>
    internal static SimulatedSqlException BackupOptionConflict(string option, string conflicting) =>
        new($"Option '{option}' conflicts with option(s) '{conflicting}'. Remove the conflicting option and reissue the statement.", 3031, 16, 1);

    /// <summary>Mimics SQL Server error 155: a <c>WITH</c> word that is no option of the statement.</summary>
    internal static SimulatedSqlException UnrecognizedBackupOption(string option, string statement) =>
        new($"'{option}' is not a recognized {statement} option.", 155, 15, 1);

    /// <summary>Mimics SQL Server error 4208: <c>BACKUP LOG</c> in the simple recovery model.</summary>
    internal static SimulatedSqlException BackupLogUnderSimpleRecovery() =>
        FollowedByBackupTerminating(new("The statement BACKUP LOG is not allowed while the recovery model is SIMPLE. Use BACKUP DATABASE or change the recovery model using ALTER DATABASE.", 4208, 16, 1), "BACKUP LOG");

    /// <summary>Mimics SQL Server error 4037: <c>MEDIANAME</c> naming other media than the device holds.</summary>
    internal static SimulatedSqlException BackupMediaNameMismatch(string requested, string actual, string path, string verb) =>
        FollowedByBackupTerminating(new($"The user-specified MEDIANAME \"{requested}\" does not match the MEDIANAME \"{actual}\" of the device \"{path}\".", 4037, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3187: <c>RESTORE WITH CHECKSUM</c> from a backup set written without checksums.</summary>
    internal static SimulatedSqlException RestoreChecksumUnavailable(string verb) =>
        FollowedByBackupTerminating(new("RESTORE WITH CHECKSUM cannot be specified because the backup set does not contain checksum information.", 3187, 16, 1), verb);

    /// <summary>Mimics SQL Server error 3153: <c>RESTORE … WITH RECOVERY</c> of a database that is online.</summary>
    internal static SimulatedSqlException RestoreAlreadyRecovered(string verb) =>
        FollowedByBackupTerminating(new("The database is already fully recovered.", 3153, 16, 2), verb);

    /// <summary>Mimics SQL Server error 924: a single-user database another session holds.</summary>
    internal static SimulatedSqlException DatabaseInSingleUserUse(string databaseName) =>
        new($"Database '{databaseName}' is already open and can only have one user at a time.", 924, 14, 1);

    /// <summary>Mimics SQL Server error 5064, then Msg 5069: a single-user database's mode changed by a session other than its single user.</summary>
    internal static SimulatedSqlException SingleUserDatabaseInUse(string databaseName) =>
        FollowedByAlterDatabaseFailed(new($"Changes to the state or options of database '{databaseName}' cannot be made at this time. The database is in single-user mode, and a user is currently connected to it.", 5064, 16, 1));

    /// <summary>Mimics SQL Server error 927: a database another session is restoring.</summary>
    internal static SimulatedSqlException DatabaseIsRestoring(string databaseName) =>
        new($"Database '{databaseName}' cannot be opened. It is in the middle of a restore.", 927, 14, 2);

    /// <summary>Msg 3211: one <c>STATS</c> progress step.</summary>
    internal static SimulatedError BackupPercentProcessedMessage(BatchContext batch, int percent) =>
        batch.InfoMessage(@class: 0, state: 1, number: 3211, string.Create(CultureInfo.InvariantCulture, $"{percent} percent processed."));

    /// <summary>Msg 4035: the pages one file contributed.</summary>
    internal static SimulatedError BackupFileProcessedMessage(BatchContext batch, long pages, string databaseName, string logicalName, int position) =>
        batch.InfoMessage(@class: 0, state: 1, number: 4035, string.Create(CultureInfo.InvariantCulture, $"Processed {pages} pages for database '{databaseName}', file '{logicalName}' on file {position}."));

    /// <summary>Msg 3014: the closing summary, with the throughput real computes from 8 KB pages.</summary>
    internal static SimulatedError BackupCompletedMessage(BatchContext batch, string verb, long pages, double seconds)
    {
        var rate = seconds > 0 ? pages * 8192 / 1048576.0 / seconds : 0;
        return batch.InfoMessage(@class: 0, state: 1, number: 3014, string.Create(CultureInfo.InvariantCulture, $"{verb} successfully processed {pages} pages in {seconds:0.000} seconds ({rate:0.000} MB/sec)."));
    }
}
