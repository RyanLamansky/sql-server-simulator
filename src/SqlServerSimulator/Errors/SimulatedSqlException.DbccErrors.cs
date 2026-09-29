using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The DBCC family's errors and the informational messages its commands send,
// each probed 2026-09-28 against SQL Server 2025. The factories shared with
// SHOW_STATISTICS / CHECKIDENT / INPUTBUFFER (2501, 2520, 2560, 2571, 2583,
// 2532) live in SimulatedSqlException.SchemaErrors.cs.
partial class SimulatedSqlException
{
    /// <summary>Msg 2526: a <c>DBCC</c> subcommand real doesn't know, or an unknown keyword argument (state 12 / 15 for <c>SQLPERF</c>'s).</summary>
    internal static SimulatedSqlException DbccStatementIncorrect(byte state = 3) =>
        new("Incorrect DBCC statement. Check the documentation for the correct DBCC syntax and options.", 2526, 16, state);

    /// <summary>
    /// Msg 916 state 2: <c>DBCC CHECKTABLE</c> under a database-scoped identity
    /// (<c>EXECUTE AS USER</c>, <c>dbo</c> included), which reads an internal
    /// snapshot of the database — another database to that identity — unless
    /// <c>WITH TABLOCK</c> takes locks instead. Names the server principal as
    /// the <c>USE</c> refusal does, and ends the batch unless caught.
    /// </summary>
    internal static SimulatedSqlException DbccSnapshotInaccessible(string principalName, string databaseName) =>
        new($"The server principal \"{principalName}\" is not able to access the database \"{databaseName}\" under the current security context.", 916, 14, 2) { TerminatesBatch = true };

    /// <summary>Msg 195 state 4: a <c>DBCC … WITH</c> list naming a word that is no DBCC option at all.</summary>
    internal static SimulatedSqlException DbccOptionNotRecognized(string option) =>
        new($"'{option}' is not a recognized option.", 195, 15, 4);

    /// <summary>Msg 7983: a database-scope <c>DBCC</c> check run by a principal that isn't <c>db_owner</c>.</summary>
    internal static SimulatedSqlException DbccDatabasePermissionDenied(string userName, string command, string databaseName) =>
        new($"User '{userName}' does not have permission to run DBCC {command} for database '{databaseName}'.", 7983, 14, 36);

    /// <summary>Msg 2557: an object-scope <c>DBCC</c> (<c>CHECKTABLE</c> state 3, <c>DBREINDEX</c> state 1) run by a principal that doesn't own the table.</summary>
    internal static SimulatedSqlException DbccObjectPermissionDenied(string userName, string command, string objectName, byte state) =>
        new($"User '{userName}' does not have permission to run DBCC {command} for object '{objectName}'.", 2557, 14, state);

    /// <summary>Msg 229 state 1: <c>DBCC CLEANTABLE</c> / <c>INDEXDEFRAG</c> without <c>ALTER</c> on the table.</summary>
    internal static SimulatedSqlException DbccPermissionDeniedOnObject(string objectName, string databaseName, string schemaName) =>
        new($"The DBCC permission was denied on the object '{objectName}', database '{databaseName}', schema '{schemaName}'.", 229, 14, 1);

    /// <summary>Msg 5239: a <c>DBCC</c> table check pointed at a view or another non-table object.</summary>
    internal static SimulatedSqlException DbccObjectTypeNotSupported(int objectId, string objectName) =>
        new($"Unable to process object ID {objectId} (object '{objectName}') because this DBCC command does not support objects of this type.", 5239, 16, 1);

    /// <summary>Msg 3505: <c>CHECKPOINT</c> run by a principal outside <c>db_owner</c> and <c>db_backupoperator</c>.</summary>
    internal static SimulatedSqlException CheckpointPermissionDenied(string databaseName) =>
        new($"Only the owner of database \"{databaseName}\" or someone with relevant permissions can run the CHECKPOINT statement.", 3505, 14, 4);

    /// <summary>
    /// Msg 297: <c>DBCC SQLPERF</c> without the server permission its form
    /// takes. Uncaught it ends the batch and rolls the transaction back, and a
    /// <c>TRY</c> catches it (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException UserLacksPermissionForAction() =>
        new("The user does not have permission to perform this action.", 297, 16, 10) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 8987: <c>DBCC HELP</c> naming a command with no help text — state 1 for an undocumented command real knows, 2 for a word it doesn't.</summary>
    internal static SimulatedSqlException DbccNoHelpAvailable(string statement, byte state) =>
        new($"No help available for DBCC statement '{statement}'.", 8987, 16, state);

    /// <summary>Msg 7999: <c>DBCC DBREINDEX</c> (state 4), <c>INDEXDEFRAG</c> or <c>UPDATEUSAGE</c> (state 8) naming an index the table doesn't have.</summary>
    internal static SimulatedSqlException DbccIndexNotFound(string indexName, string tableName, byte state) =>
        new($"Could not find any index named '{indexName}' for table '{tableName}'.", 7999, 16, state);

    /// <summary>Msg 8920: <c>DBCC INDEXDEFRAG</c> inside a user transaction.</summary>
    internal static SimulatedSqlException IndexDefragInsideTransaction() =>
        new("Cannot perform a indexdefrag operation inside a user transaction. Terminate the transaction and reissue the statement.", 8920, 16, 2);

    /// <summary>Msg 2521: a DBCC database argument naming an id no database has.</summary>
    internal static SimulatedSqlException CouldNotFindDatabaseId(long databaseId) =>
        new($"Could not find database ID {databaseId}. The database ID either does not exist, or the database was dropped before a statement tried to use it. Verify if the database ID exists by querying the sys.databases catalog view.", 2521, 16, 10);

    /// <summary>Msg 2573: a DBCC table argument naming an object id no table has.</summary>
    internal static SimulatedSqlException CouldNotFindObjectId(int objectId) =>
        new($"Could not find table or object ID {objectId}. Check system catalog.", 2573, 16, 1);

    /// <summary>Msg 3027: <c>DBCC CHECKFILEGROUP</c> naming a filegroup the database doesn't have.</summary>
    internal static SimulatedSqlException FilegroupNotInDatabase(string filegroupName, string databaseName) =>
        new($"The filegroup \"{filegroupName}\" is not part of database \"{databaseName}\".", 3027, 16, 3);

    /// <summary>The text of Msg 5201, <c>DBCC SHRINKDATABASE</c> passing over a file with no free space to give back.</summary>
    internal static string ShrinkSkippedFileText(int fileId, short databaseId) =>
        $"DBCC SHRINKDATABASE: File ID {fileId} of database ID {databaseId} was skipped because the file does not have enough free space to reclaim.";

    /// <summary>Msg 2528, closing every <c>DBCC</c> run that <c>WITH NO_INFOMSGS</c> doesn't silence.</summary>
    internal static SimulatedError DbccExecutionCompletedMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 2528, "DBCC execution completed. If DBCC printed error messages, contact your system administrator.");

    /// <summary>Msg 7969: <c>DBCC OPENTRAN</c> found no transaction that has written to the database.</summary>
    internal static SimulatedError NoActiveOpenTransactionsMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 2, number: 7969, "No active open transactions.");

    /// <summary>
    /// The lines <c>DBCC OPENTRAN</c> prints for the oldest active transaction:
    /// Msg 7968, 7970, 7971, 7972, 7974, 7975, 7977 and 7978, in that order.
    /// </summary>
    internal static SimulatedError[] OldestActiveTransactionMessages(BatchContext batch, string databaseName, int spid, string name, string lsn, string startTime, string sid) =>
    [
        batch.InfoMessage(@class: 0, state: 1, number: 7968, $"Transaction information for database '{databaseName}'."),
        batch.InfoMessage(@class: 0, state: 1, number: 7970, "\nOldest active transaction:"),
        batch.InfoMessage(@class: 0, state: 1, number: 7971, $"    SPID (server process ID): {spid}"),
        batch.InfoMessage(@class: 0, state: 1, number: 7972, "    UID (user ID) : -1"),
        batch.InfoMessage(@class: 0, state: 1, number: 7974, $"    Name          : {name}"),
        batch.InfoMessage(@class: 0, state: 1, number: 7975, $"    LSN           : {lsn}"),
        batch.InfoMessage(@class: 0, state: 1, number: 7977, $"    Start time    : {startTime}"),
        batch.InfoMessage(@class: 0, state: 1, number: 7978, $"    SID           : {sid}"),
    ];

    /// <summary>Msg 2536, heading a database's or an object's section of a consistency check.</summary>
    internal static SimulatedError DbccResultsForMessage(BatchContext batch, string name) =>
        batch.InfoMessage(@class: 0, state: 1, number: 2536, $"DBCC results for '{name}'.");

    /// <summary>Msg 7966, after the heading of a <c>DBCC CHECKDB</c> told <c>NOINDEX</c>.</summary>
    internal static SimulatedError NoIndexCheckWarningMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 2, number: 7966, "Warning: NO_INDEX option of checkdb being used. Checks on non-system indexes will be skipped.");

    /// <summary>The text of Msg 2593, which a <c>TABLERESULTS</c> row carries as well.</summary>
    internal static string RowsInPagesText(long rows, long pages, string objectName) =>
        $"There are {rows} rows in {pages} pages for object \"{objectName}\".";

    /// <summary>Msg 2593, one object's row and page count in a consistency check.</summary>
    internal static SimulatedError RowsInPagesMessage(BatchContext batch, long rows, long pages, string objectName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 2593, RowsInPagesText(rows, pages, objectName));

    /// <summary>The text of Msg 8989, the closing count of a consistency check.</summary>
    internal static string CheckFoundErrorsText(string command, string databaseName) =>
        $"{command} found 0 allocation errors and 0 consistency errors in database '{databaseName}'.";

    /// <summary>Msg 8989, closing <c>CHECKDB</c>, <c>CHECKALLOC</c> and <c>CHECKFILEGROUP</c>.</summary>
    internal static SimulatedError CheckFoundErrorsMessage(BatchContext batch, string command, string databaseName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 8989, CheckFoundErrorsText(command, databaseName));

    /// <summary>
    /// The eight Msg 8997 lines <c>DBCC CHECKDB</c> prints for the Service Broker
    /// metadata every database carries: the fourteen message types, six
    /// contracts, three services and three queues it is created with, and none
    /// of the rest.
    /// </summary>
    internal static readonly string[] ServiceBrokerCheckTexts =
    [
        "Service Broker Msg 9675, State 1: Message Types analyzed: 14.",
        "Service Broker Msg 9676, State 1: Service Contracts analyzed: 6.",
        "Service Broker Msg 9667, State 1: Services analyzed: 3.",
        "Service Broker Msg 9668, State 1: Service Queues analyzed: 3.",
        "Service Broker Msg 9669, State 1: Conversation Endpoints analyzed: 0.",
        "Service Broker Msg 9674, State 1: Conversation Groups analyzed: 0.",
        "Service Broker Msg 9670, State 1: Remote Service Bindings analyzed: 0.",
        "Service Broker Msg 9605, State 1: Conversation Priorities analyzed: 0.",
    ];

    /// <summary>Msg 8997, one line of <see cref="ServiceBrokerCheckTexts"/>.</summary>
    internal static SimulatedError ServiceBrokerCheckMessage(BatchContext batch, string text) =>
        batch.InfoMessage(@class: 0, state: 1, number: 8997, text);

    /// <summary>Msg 5281, a check's <c>WITH ESTIMATEONLY</c> answer.</summary>
    internal static SimulatedError EstimatedTempdbSpaceMessage(BatchContext batch, string command, string databaseName, long kilobytes) =>
        batch.InfoMessage(@class: 0, state: 1, number: 5281, $"Estimated TEMPDB space (in KB) needed for {command} on database {databaseName} = {kilobytes}.");

    /// <summary>Msg 2538 and 8915 for the data file, then 2539 and 8918 for the database: <c>DBCC CHECKALLOC</c>'s totals.</summary>
    internal static SimulatedError[] AllocationTotalsMessages(BatchContext batch, long extents, long usedPages, long reservedPages) =>
    [
        batch.InfoMessage(@class: 0, state: 1, number: 2538, $"File 1. The number of extents = {extents}, used pages = {usedPages}, and reserved pages = {reservedPages}."),
        batch.InfoMessage(@class: 0, state: 1, number: 8915, "           File 1 (number of mixed extents = 0, mixed pages = 0)."),
        batch.InfoMessage(@class: 0, state: 1, number: 2539, $"The total number of extents = {extents}, used pages = {usedPages}, and reserved pages = {reservedPages} in this database."),
        batch.InfoMessage(@class: 0, state: 1, number: 8918, "       (number of mixed extents = 0, mixed pages = 0) in this database."),
    ];
}
