namespace SqlServerSimulator;

// Detaching and attaching databases: DBCC DETACHDB, sp_detach_db and
// CREATE DATABASE … FOR ATTACH (probed 2026-10-16 against SQL Server 2025).
partial class SimulatedSqlException
{
    /// <summary>Msg 7939: <c>DBCC DETACHDB</c> naming no database.</summary>
    internal static SimulatedSqlException DetachDatabaseDoesNotExist(string name) =>
        new($"Cannot detach database '{name}' because it does not exist.", 7939, 16, 1);

    /// <summary>Msg 7940: <c>DBCC DETACHDB</c> naming a system database, an empty name being the current one.</summary>
    internal static SimulatedSqlException CannotDetachSystemDatabase() =>
        new("System databases master, model, msdb, and tempdb cannot be detached.", 7940, 16, 1);

    /// <summary>Msg 3703: <c>DBCC DETACHDB</c> of the session's own current database, which ends the batch.</summary>
    internal static SimulatedSqlException DetachDatabaseInUse(string name) =>
        new SimulatedSqlException($"Cannot detach the database '{name}' because it is currently in use.", 3703, 16, 3).EndingBatch();

    /// <summary>Msg 947: a restoring database detached, which the detach still completes.</summary>
    internal static SimulatedSqlException ErrorClosingDatabase(string name) =>
        new($"Error while closing database '{name}'. Check for previous additional errors and retry the operation.", 947, 16, 1);

    /// <summary>Msg 1824: attaching the files of a database detached while it was restoring.</summary>
    internal static SimulatedSqlException CannotAttachRestoringDatabase() =>
        new("Cannot attach a database that was being restored.", 1824, 16, 1);

    /// <summary>Msg 1829 state 3: the first file an attach lists is a log or secondary data file.</summary>
    internal static SimulatedSqlException AttachFirstFileNotPrimary() =>
        new("The FOR ATTACH option requires that at least the primary file be specified.", 1829, 16, 3);

    /// <summary>Msg 1830, then Msg 1802: an attach listing two primary files.</summary>
    internal static SimulatedSqlException AttachTwoPrimaryFiles(string first, string second) =>
        FollowedByCreateDatabaseFailed(new($"The files '{first}' and '{second}' are both primary files. A database can only have one primary file.", 1830, 16, 1), 7);

    /// <summary>Msg 1841: <c>CONTAINMENT</c> written beside <c>FOR ATTACH</c>.</summary>
    internal static SimulatedSqlException ContainmentWithAttach() =>
        new("CREATE DATABASE failed. CONTAINMENT clause cannot be used with the FOR ATTACH option.", 1841, 16, 1);

    /// <summary>Msg 5105 state 2: an attach naming a file by a relative path, which names no file.</summary>
    internal static SimulatedSqlException FileActivationError(string path) =>
        new($"A file activation error occurred. The physical file name '{path}' may be incorrect. Diagnose and correct additional errors, and retry the operation.", 5105, 16, 2);

    /// <summary>
    /// Msg 5120 state 101, then Msg 1802: an attach's file that can't be
    /// opened — missing (operating system error 2), a directory (5), or held
    /// by an online database (32).
    /// </summary>
    internal static SimulatedSqlException AttachFileCannotBeOpened(string path, int osError) =>
        FollowedByCreateDatabaseFailed(new($"Unable to open the physical file \"{path}\". Operating system error {osError}: \"{OperatingSystemErrorText(osError)}\".", 5120, 16, 101), 7);

    /// <summary>Msg 5172 state 15, then Msg 1802: an attach's file that holds no database.</summary>
    internal static SimulatedSqlException NotADatabaseFile(string path) =>
        FollowedByCreateDatabaseFailed(new($"The header for file '{path}' is not a valid database file header. The FILE SIZE property is incorrect.", 5172, 16, 15), 7);

    /// <summary>Msg 5173 state 6, then Msg 1802: an attach's file of another database than its primary file's.</summary>
    internal static SimulatedSqlException FilesDoNotMatchPrimary() =>
        FollowedByCreateDatabaseFailed(new("One or more files do not match the primary file of the database. If you are attempting to attach a database, retry the operation with the correct files.  If this is an existing database, the file may be corrupted and should be restored from a backup.", 5173, 16, 6), 7);
}
