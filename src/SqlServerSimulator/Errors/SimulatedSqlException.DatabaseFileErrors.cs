namespace SqlServerSimulator;

// The database file and scoped-configuration family: ALTER DATABASE … ADD /
// MODIFY / REMOVE FILE, MODIFY FILEGROUP, CREATE DATABASE's file list, and
// ALTER DATABASE SCOPED CONFIGURATION (all probed 2026-09-27 against SQL
// Server 2025).
partial class SimulatedSqlException
{
    /// <summary>Mimics SQL Server error 153: a file option the grammar lacks, repeated, or given a unit it doesn't take — named as written (<c>percent</c> for <c>%</c>).</summary>
    internal static SimulatedSqlException InvalidFileOptionUsage(string option) =>
        new($"Invalid usage of the option {option} in the CREATE/ALTER DATABASE statement.", 153, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1036: a file specification without <c>NAME</c>
    /// (class 16, state 3 in <c>ALTER DATABASE</c>, 2 in <c>CREATE DATABASE</c>)
    /// or without <c>FILENAME</c> where a file is created (class 15, state 1).
    /// Both are raised compiling the batch.
    /// </summary>
    internal static SimulatedSqlException FileOptionRequired(string option, byte @class, byte state) =>
        new($"File option {option} is required in this CREATE/ALTER DATABASE statement.", 1036, @class, state);

    /// <summary>Mimics SQL Server error 1842: a size or growth past <c>int</c> pages, raised compiling the batch.</summary>
    internal static SimulatedSqlException FileSizeTooLarge() =>
        new("The file size, max size cannot be greater than 2147483647 in units of a page size. The file growth cannot be greater than 2147483647 in units of both page size and percentage.", 1842, 16, 1);

    /// <summary>Mimics SQL Server error 188: <c>CREATE DATABASE … LOG ON</c> with no data file.</summary>
    internal static SimulatedSqlException LogFileWithoutDataFile() =>
        new("Cannot specify a log file in a CREATE DATABASE statement without also specifying at least one data file.", 188, 15, 1);

    /// <summary>Mimics SQL Server error 1828: a logical file name the database already holds — state 3 renaming, 4 adding, 5 in <c>CREATE DATABASE</c>, 9 repeated within one <c>ADD FILE</c>.</summary>
    internal static SimulatedSqlException LogicalFileNameInUse(string name, byte state) =>
        new($"The logical file name \"{name}\" is already in use. Choose a different name.", 1828, 16, state);

    /// <summary>Mimics SQL Server error 1921: <c>ADD FILE … TO FILEGROUP</c> naming no filegroup.</summary>
    internal static SimulatedSqlException InvalidFilegroup(string name) =>
        new($"Invalid filegroup '{name}' specified.", 1921, 16, 4);

    /// <summary>Mimics SQL Server error 5004: a file change on a read-only database — state 1 adding, 3 removing, 4 modifying.</summary>
    internal static SimulatedSqlException DatabaseNotWritableForFileChange(byte state) =>
        new("To use ALTER DATABASE, the database must be in a writable state in which a checkpoint can be executed.", 5004, 16, state);

    /// <summary>
    /// Mimics SQL Server error 5009 — an <c>ADD FILE</c> that couldn't create a
    /// file (state 8 after the error naming why, state 1 before Msg 5170) or a
    /// <c>REMOVE FILE</c> naming none (state 9).
    /// </summary>
    internal static SimulatedSqlException FilesNotInitialized(byte state) =>
        new("One or more files listed in the statement could not be found or could not be initialized.", 5009, 16, state);

    /// <summary>Mimics SQL Server error 5009 (state 8) following <paramref name="error"/>, an <c>ADD FILE</c> specification's refusal.</summary>
    internal static SimulatedSqlException FollowedByFilesNotInitialized(SimulatedSqlException error) =>
        FollowedBy(error, FilesNotInitialized(8));

    /// <summary>Mimics SQL Server error 1802 following <paramref name="error"/>, a <c>CREATE DATABASE</c> file specification's refusal.</summary>
    internal static SimulatedSqlException FollowedByCreateDatabaseFailed(SimulatedSqlException error, byte state) =>
        FollowedBy(error, new("CREATE DATABASE failed. Some file names listed could not be created. Check related errors.", 1802, 16, state));

    /// <summary>Mimics SQL Server error 5170: a physical path another file holds — state 1 adding (after Msg 5009), 4 in <c>CREATE DATABASE</c> (before Msg 1802).</summary>
    internal static SimulatedSqlException PhysicalFileExists(string path, byte state) =>
        new($"Cannot create file '{path}' because it already exists. Change the file path or the file name, and retry the operation.", 5170, 16, state);

    /// <summary>Msg 5009 (state 1), then Msg 5170 for <paramref name="path"/>: <c>ADD FILE</c> onto a path another file holds.</summary>
    internal static SimulatedSqlException AddFilePathExists(string path) =>
        FollowedBy(FilesNotInitialized(1), PhysicalFileExists(path, 1));

    /// <summary>Mimics SQL Server error 5174: a created file smaller than 512 KB.</summary>
    internal static SimulatedSqlException FileTooSmall() =>
        new("Each file size must be greater than or equal to 512 KB.", 5174, 16, 1);

    /// <summary>Mimics SQL Server error 5103: a created file whose <c>MAXSIZE</c> is below its <c>SIZE</c>.</summary>
    internal static SimulatedSqlException MaxSizeBelowSize(string name) =>
        new($"MAXSIZE cannot be less than SIZE for file '{name}'.", 5103, 16, 1);

    /// <summary>Mimics SQL Server error 5169: a growth past the ceiling — state 1 setting the growth, 2 creating the file, 3 setting the ceiling.</summary>
    internal static SimulatedSqlException GrowthAboveMaxSize(string name, byte state) =>
        new($"FILEGROWTH cannot be greater than MAXSIZE for file '{name}'.", 5169, 16, state);

    /// <summary>Mimics SQL Server error 5087: <c>ADD LOG FILE … TO FILEGROUP</c>.</summary>
    internal static SimulatedSqlException FileContentTypeMismatch() =>
        new("The file content type mismatches with the content type of the filegroup.", 5087, 16, 1);

    /// <summary>Mimics SQL Server error 5020: removing the primary data or log file.</summary>
    internal static SimulatedSqlException CannotRemovePrimaryFile() =>
        new("The primary data or log file cannot be removed from a database.", 5020, 16, 1);

    /// <summary>Mimics SQL Server error 5031: removing the one file the default filegroup has.</summary>
    internal static SimulatedSqlException CannotRemoveOnlyDefaultFile(string name) =>
        new($"Cannot remove the file '{name}' because it is the only file in the DEFAULT filegroup.", 5031, 16, 1);

    /// <summary>Mimics SQL Server error 5055: removing a file of a read-only filegroup.</summary>
    internal static SimulatedSqlException FileIsReadOnly(string name) =>
        new($"Cannot add, remove, or modify file '{name}'. The file is read-only.", 5055, 16, 3);

    /// <summary>Mimics SQL Server error 5048: adding (state 1) or modifying (state 3) a file of a read-only filegroup.</summary>
    internal static SimulatedSqlException FilegroupIsReadOnly(string name, byte state) =>
        new($"Cannot add, remove, or modify files in filegroup '{name}'. The filegroup is read-only.", 5048, 16, state);

    /// <summary>Mimics SQL Server error 5038: a <c>MODIFY FILE</c> naming nothing to change.</summary>
    internal static SimulatedSqlException ModifyFileNeedsProperty(string name) =>
        new($"MODIFY FILE failed for file \"{name}\". At least one property per file must be specified.", 5038, 16, 1);

    /// <summary>Mimics SQL Server error 5039: a <c>MODIFY FILE</c> size no larger than the file's.</summary>
    internal static SimulatedSqlException ModifyFileSizeNotLarger() =>
        new("MODIFY FILE failed. Specified size is less than or equal to current size.", 5039, 16, 1);

    /// <summary>Mimics SQL Server error 5040: a <c>MAXSIZE</c> below the file's size, both in KB.</summary>
    internal static SimulatedSqlException ModifyFileMaxSizeBelowSize(string databaseName, int fileId, long sizeKilobytes, long maxSizeKilobytes) =>
        new($"MODIFY FILE failed for database '{databaseName}', file id {fileId}. Size of file ({sizeKilobytes} KB) is greater than MAXSIZE ({maxSizeKilobytes} KB).", 5040, 16, 1);

    /// <summary>Mimics SQL Server error 5041: a <c>MODIFY FILE</c> naming no file.</summary>
    internal static SimulatedSqlException ModifyFileDoesNotExist(string name) =>
        new($"MODIFY FILE failed. File '{name}' does not exist.", 5041, 16, 1);

    /// <summary>Mimics SQL Server error 5077: <c>MODIFY FILE … OFFLINE</c> on a log file or a file of <c>PRIMARY</c>.</summary>
    internal static SimulatedSqlException CannotTakeFileOffline() =>
        new("Cannot change the state of non-data files or files in the primary filegroup.", 5077, 16, 2);

    /// <summary>Mimics SQL Server error 12106: a <c>FILENAME</c> another database file holds.</summary>
    internal static SimulatedSqlException PathInUse(string path) =>
        new($"The path name '{path}' is already used by another database file. Change to another valid and UNUSED name.", 12106, 16, 1);

    /// <summary>Mimics SQL Server error 5012: renaming <c>PRIMARY</c>.</summary>
    internal static SimulatedSqlException CannotRenamePrimaryFilegroup() =>
        new("The name of the primary filegroup cannot be changed.", 5012, 16, 1);

    /// <summary>Mimics SQL Server error 5014 (state 2): <c>MODIFY FILEGROUP</c> naming no filegroup.</summary>
    internal static SimulatedSqlException ModifyFilegroupDoesNotExist(string name, string database) =>
        new($"The filegroup '{name}' does not exist in database '{database}'.", 5014, 16, 2);

    /// <summary>Mimics SQL Server error 5035 (state 2): renaming a filegroup onto another's name.</summary>
    internal static SimulatedSqlException RenamedFilegroupExists(string name) =>
        new($"Filegroup '{name}' already exists in this database. Specify a different name or remove the conflicting filegroup if it is empty.", 5035, 16, 2);

    /// <summary>Mimics SQL Server error 5035 (state 1) in <c>CREATE DATABASE</c>: a file list declaring a filegroup twice, or <c>PRIMARY</c>.</summary>
    internal static SimulatedSqlException DeclaredFilegroupExists(string name) =>
        new($"Filegroup '{name}' already exists in this database. Specify a different name or remove the conflicting filegroup if it is empty.", 5035, 16, 1);

    /// <summary>Mimics SQL Server error 5042 (state 7): removing a filegroup that still has files.</summary>
    internal static SimulatedSqlException FilegroupHasFiles(string name) =>
        new($"The filegroup '{name}' cannot be removed because it is not empty.", 5042, 16, 7);

    /// <summary>Mimics SQL Server error 5045: a filegroup property it already has — state 1 READ_ONLY, 2 READ_WRITE, 3 DEFAULT and AUTOGROW_ALL_FILES, 4 AUTOGROW_SINGLE_FILE.</summary>
    internal static SimulatedSqlException FilegroupPropertyAlreadySet(string property, byte state) =>
        new($"The filegroup already has the '{property}' property set.", 5045, 16, state);

    /// <summary>Mimics SQL Server error 5047: marking <c>PRIMARY</c> read-only or read-write.</summary>
    internal static SimulatedSqlException PrimaryFilegroupReadOnlyChange() =>
        new("Cannot change the READONLY property of the PRIMARY filegroup.", 5047, 16, 1);

    /// <summary>Mimics SQL Server error 5050: a property change on a filegroup with no file.</summary>
    internal static SimulatedSqlException EmptyFilegroupProperty(string name) =>
        new($"Cannot change the properties of empty filegroup '{name}'. The filegroup must contain at least one file.", 5050, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 12108: a <c>MAXDOP</c> outside 0–32767, raised
    /// compiling the batch.
    /// </summary>
    internal static SimulatedSqlException ScopedConfigurationOutOfRange(long value, string option) =>
        new($"'{value}' is out of range for the database scoped configuration option '{option}'. See sp_configure option 'max degree of parallelism' for valid values.", 12108, 16, 1);

    /// <summary>Mimics SQL Server error 12109: <c>PRIMARY</c> as a primary replica's value, raised compiling the batch.</summary>
    internal static SimulatedSqlException ScopedConfigurationPrimaryOnPrimary() =>
        new("Statement 'ALTER DATABASE SCOPED CONFIGURATION' failed, because it attempted to set the value to 'PRIMARY' for the primary replica. A settings can only be set to 'PRIMARY' when the setting is applied to the secondary.", 12109, 16, 1);

    /// <summary>Mimics SQL Server error 12110: <c>FOR SECONDARY</c> on an option only the primary carries, raised compiling the batch.</summary>
    internal static SimulatedSqlException ScopedConfigurationPrimaryOnly(string option, byte state) =>
        new($"Statement 'ALTER DATABASE SCOPED CONFIGURATION' failed, because it attempted to set the '{option}' option for the secondaries replica while this option is only allowed to be set for the primary.", 12110, 16, state);

    /// <summary>Mimics SQL Server error 12121: a <c>PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES</c> outside 0–71582, raised compiling the batch.</summary>
    internal static SimulatedSqlException PausedIndexAbortDurationOutOfRange(long value) =>
        new($"Time value {value} used with PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES is not a valid value; PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES wait time must be greater or equal to 0 and less or equal to 71582.", 12121, 16, 1);

    /// <summary>Mimics SQL Server error 31207: a <c>FULLTEXT_INDEX_VERSION</c> other than 1 or 2, raised compiling the batch.</summary>
    internal static SimulatedSqlException InvalidFullTextIndexVersion() =>
        new("Invalid value for Full-Text index version is specified. Valid values are 1 or 2.", 31207, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 12117: <c>CLEAR PROCEDURE_CACHE</c> with a plan
    /// handle, none of which the simulator's plan cache hands out. Ends the batch.
    /// </summary>
    internal static SimulatedSqlException PlanHandleNotFound() =>
        new("Cannot free the plan because a plan was not found in the database plan cache that corresponds to the specified plan handle. Specify a cached plan handle for the database. For a list of cached plan handles, query the sys.dm_exec_query_stats dynamic management view.", 12117, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 12136: a ledger digest endpoint that isn't an
    /// Azure blob storage URL — state 1 when it isn't <c>https://</c>, 3 when
    /// its host names blob storage in another case, 2 otherwise. Ends the batch.
    /// </summary>
    internal static SimulatedSqlException InvalidLedgerDigestEndpoint(byte state) =>
        new("The specified digest storage endpoint is invalid. It must be an Azure blob storage endpoint.", 12136, 16, state) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 37531: a blob-storage ledger digest endpoint,
    /// which needs a credential the simulator has no model of. Ends the batch.
    /// </summary>
    internal static SimulatedSqlException LedgerDigestEndpointUnreachable(string endpoint) =>
        new($"Failed to set the ledger digest storage endpoint to '{endpoint}'. Verify that you have created a credential object to provide SQL Server access to the 'sqldbledgerdigests' container in this Azure Storage account.", 37531, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 15247 (state 13): <c>ALTER DATABASE SCOPED CONFIGURATION</c> without permission. Ends the batch.</summary>
    internal static SimulatedSqlException ScopedConfigurationPermissionDenied() =>
        new("User does not have permission to perform this action.", 15247, 16, 13) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 3906 for <c>ALTER DATABASE SCOPED CONFIGURATION … SET</c> on a read-only database, which ends the batch.</summary>
    internal static SimulatedSqlException ScopedConfigurationReadOnly(string databaseName) =>
        new($"Failed to update database \"{databaseName}\" because the database is read-only.", 3906, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 102 at class 16, which real raises running a
    /// <c>SET DW_COMPATIBILITY_LEVEL</c> — a Synapse-only scoped configuration
    /// its grammar knows — ending the batch (probed 2026-09-27).
    /// </summary>
    internal static SimulatedSqlException DataWarehouseCompatibilityLevel() =>
        new("Incorrect syntax near 'DW_COMPATIBILITY_LEVEL'.", 102, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 102 at state 6: a <c>MODIFY FILEGROUP</c> property real doesn't know.</summary>
    internal static SimulatedSqlException UnknownFilegroupProperty(string word) =>
        new($"Incorrect syntax near '{word}'.", 102, 15, 6);

    /// <summary>Msg 5018, after a <c>MODIFY FILE … FILENAME</c>, naming the file by its name before any rename.</summary>
    internal static string FileModifiedInCatalogMessage(string name) =>
        $"The file \"{name}\" has been modified in the system catalog. The new path will be used the next time the database is started.";
}
