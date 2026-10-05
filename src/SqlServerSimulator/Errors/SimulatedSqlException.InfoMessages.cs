using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The informational messages the engine sends on its own account, each
// attributed to the statement being dispatched (BatchContext.InfoMessage). All
// class 0 — severity 10 as real delivers it — and probed 2026-09-23 against
// SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 337, class 0: a float literal below the smallest normal value,
    /// which reads as 0; <paramref name="literal"/> is its written text.
    /// </summary>
    internal static SimulatedError FloatLiteralTooSmallMessage(BatchContext batch, string literal) =>
        batch.InfoMessage(@class: 0, state: 1, number: 337, $"Warning: the floating point value '{literal}' is too small. It will be interpreted as 0.");

    /// <summary>
    /// Msg 2007, class 0: a module created over a call of a procedure that
    /// doesn't exist, attributed to the module.
    /// </summary>
    internal static SimulatedError ModuleDependsOnMissingObjectMessage(BatchContext batch, string moduleName, string missingName)
    {
        var note = batch.InfoMessage(0, 1, 2007, $"The module '{moduleName}' depends on the missing object '{missingName}'. The module will still be created; however, it cannot run successfully until the object exists.");
        note.Procedure = moduleName;
        return note;
    }

    /// <summary>
    /// Msg 282, when a procedure's <c>RETURN</c> value is NULL; names the
    /// procedure as it is stored, whatever the <c>EXEC</c> spelled. SqlClient
    /// raises no <c>InfoMessage</c> for it when it trails a result set under
    /// <c>ExecuteReader</c> or <c>ExecuteScalar</c>, though real sends it.
    /// </summary>
    internal static SimulatedError NullReturnStatusMessage(BatchContext batch, string procedureName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 282, $"The '{procedureName}' procedure attempted to return a status of NULL, which is not allowed. A status of 0 will be returned instead.");

    /// <summary>
    /// Msg 1945, class 0: an index whose key columns can together exceed what
    /// its kind holds.
    /// </summary>
    internal static SimulatedError WideIndexKeyMessage(BatchContext batch, bool clustered, int limit, string indexName, int length) =>
        batch.InfoMessage(@class: 0, state: 1, number: 1945, $"Warning! The maximum key length for a {(clustered ? "clustered" : "nonclustered")} index is {limit} bytes. The index '{indexName}' has maximum length of {length} bytes. For some combination of large values, the insert/update operation will fail.");

    /// <summary>Msg 4430, an index hint on a view read without <c>NOEXPAND</c>.</summary>
    internal static SimulatedError ViewIndexHintsIgnoredMessage(BatchContext batch, string viewName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 4430, $"Warning: Index hints supplied for view '{viewName}' will be ignored.");

    /// <summary>Msg 3621, after an execution error ends a statement that writes rows.</summary>
    internal static SimulatedError StatementTerminatedMessage(BatchContext batch, SimulatedSqlException? error = null)
    {
        var message = batch.InfoMessage(@class: 0, state: 0, number: 3621, "The statement has been terminated.");
        // After a unique index's build finds a duplicate (Msg 1505), or a
        // column rewrite a value it can't convert, the notice reports line 1,
        // whatever line the statement was on (probed 2026-09-26 against SQL
        // Server 2025).
        // So does any error that ended a trigger body, T-SQL or CLR, and its
        // context connection ending the firing statement's transaction (probed
        // 2026-10-01 and 2026-10-04), and a partitioned view's write a row
        // fits no member of (probed 2026-10-01).
        if (error is { Number: 1505 or 4457 } or { EndedColumnRewrite: true } or { EndedTriggerBody: true })
            message.LineNumber = 1;
        return message;
    }

    /// <summary>
    /// Msg 3606, in place of Msg 3621 after an identity overflow ends a write,
    /// and after a statement whose overflow the session answered with NULL.
    /// </summary>
    internal static SimulatedError ArithmeticOverflowOccurredMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3606, "Arithmetic overflow occurred.");

    /// <summary>Msg 3607, after a statement whose divide by zero the session answered with NULL.</summary>
    internal static SimulatedError DivisionByZeroOccurredMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3607, "Division by zero occurred.");

    /// <summary>Msg 15070, <c>sp_recompile</c>'s confirmation, naming the object as passed.</summary>
    internal static SimulatedError MarkedForRecompilationMessage(BatchContext batch, string procedure, string objectName) =>
        SystemProcedureMessage(batch, procedure, 61, 15070, $"Object '{objectName}' was successfully marked for recompilation.");

    /// <summary>Msg 5021, after an <c>ALTER DATABASE … MODIFY NAME</c> (probed 2026-09-25).</summary>
    internal static SimulatedError DatabaseNameSetMessage(BatchContext batch, string databaseName) =>
        batch.InfoMessage(@class: 0, state: 2, number: 5021, $"The database name '{databaseName}' has been set.");

    /// <summary>Msg 5701, after every <c>USE</c> and after renaming the session's own database.</summary>
    internal static SimulatedError DatabaseContextChangedMessage(BatchContext batch, string databaseName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 5701, $"Changed database context to '{databaseName}'.");

    /// <summary>
    /// Msg 5703, after every <c>SET LANGUAGE</c> outside a module, in the words
    /// of the language being switched to (<see cref="Language.ChangedMessage"/>).
    /// </summary>
    internal static SimulatedError LanguageChangedMessage(BatchContext batch, Language language) =>
        batch.InfoMessage(@class: 0, state: 1, number: 5703, language.ChangedMessage);

    /// <summary>
    /// Msg 3613, <c>SET STATISTICS TIME</c>'s report on a compile — a batch's,
    /// a module's on each call, a dynamic batch's, a simply parameterized
    /// statement's (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError ParseAndCompileTimeMessage(BatchContext batch, long cpuMilliseconds, long elapsedMilliseconds) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3613, $"SQL Server parse and compile time: \n   CPU time = {cpuMilliseconds} ms, elapsed time = {elapsedMilliseconds} ms.");

    /// <summary>Msg 3612, <c>SET STATISTICS TIME</c>'s report after each statement that closes with a DONE.</summary>
    internal static SimulatedError ExecutionTimesMessage(BatchContext batch, long cpuMilliseconds, long elapsedMilliseconds) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3612, $"\n SQL Server Execution Times:\n   CPU time = {cpuMilliseconds} ms,  elapsed time = {elapsedMilliseconds} ms.");

    /// <summary>Msg 3615, <c>SET STATISTICS IO</c>'s line for one table a statement read (<see cref="IoStatistics.Lines"/>).</summary>
    internal static SimulatedError TableIoMessage(BatchContext batch, string line) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3615, line);

    /// <summary>Msg 8153, once per statement whose aggregate skipped a NULL.</summary>
    internal static SimulatedError NullEliminatedMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 8153, "Warning: Null value is eliminated by an aggregate or other SET operation.");

    /// <summary>
    /// Msg 3750: a nonclustered index disabled along with its table's clustered
    /// one, the table named bare (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError IndexDisabledWithClusteredMessage(BatchContext batch, string index, string table) =>
        batch.InfoMessage(@class: 0, state: 1, number: 3750, $"Warning: Index '{index}' on table '{table}' was disabled as a result of disabling the clustered index on the table.");

    /// <summary>
    /// Msg 1992, once per FOREIGN KEY that disabling the unique index it rests
    /// on took out of service (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError ForeignKeyDisabledWithIndexMessage(BatchContext batch, string foreignKey, string childTable, string referencedTable, string index) =>
        batch.InfoMessage(@class: 0, state: 1, number: 1992, $"Warning: Foreign key '{foreignKey}' on table '{childTable}' referencing table '{referencedTable}' was disabled as a result of disabling the index '{index}'.");

    /// <summary>
    /// Msg 11729, when a sequence's declared cache is longer than the values it
    /// has left; names the sequence without its schema.
    /// </summary>
    internal static SimulatedError SequenceCacheExceedsRangeMessage(BatchContext batch, string sequenceName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 11729, $"The sequence object '{sequenceName}' cache size is greater than the number of available values.");

    /// <summary>
    /// Msg 11707, which a <c>CREATE</c> or <c>ALTER SEQUENCE</c> naming
    /// <c>CACHE 1</c> sends; <c>sys.sequences</c> still reports the cache as 1
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError SequenceCacheSetToNoCacheMessage(BatchContext batch, string sequenceName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 11707, $"The cache size for sequence object '{sequenceName}' has been set to NO CACHE.");

    /// <summary>
    /// A message a system procedure prints from its own body, attributed to
    /// it by the name it was called by and to the line of real's source that
    /// prints it (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError SystemProcedureMessage(BatchContext batch, string procedure, int line, int number, string text, byte @class = 0) =>
        new(@class: @class, lineNumber: line, message: text, number: number, procedure: procedure, server: batch.Connection.DataSource, source: "SqlServerSimulator", state: 1);

    /// <summary>The blank line (<c>PRINT ''</c>, one space) the help procedures print between their sections.</summary>
    internal static SimulatedError HelpBlankLineMessage(BatchContext batch, string procedure, int line) =>
        SystemProcedureMessage(batch, procedure, line, 0, " ");

    /// <summary>Msg 15471, <c>sp_helptext</c>'s answer for a module created <c>WITH ENCRYPTION</c>.</summary>
    internal static SimulatedError TextIsEncryptedMessage(BatchContext batch, string procedure, int line, string objectName) =>
        SystemProcedureMessage(batch, procedure, line, 15471, $"The text for object '{objectName}' is encrypted.");

    /// <summary>Msg 15469, in place of an empty constraint set.</summary>
    internal static SimulatedError NoConstraintsMessage(BatchContext batch, string procedure, int line, string objectName) =>
        SystemProcedureMessage(batch, procedure, line, 15469, $"No constraints are defined on object '{objectName}', or you do not have permissions.");

    /// <summary>Msg 15470, in place of an empty referencing-foreign-key set.</summary>
    internal static SimulatedError NoReferencingForeignKeysMessage(BatchContext batch, string procedure, int line, string objectName) =>
        SystemProcedureMessage(batch, procedure, line, 15470, $"No foreign keys reference table '{objectName}', or you do not have permissions on referencing tables.");

    /// <summary>Msg 15472, in place of an empty index set.</summary>
    internal static SimulatedError NoIndexesMessage(BatchContext batch, string procedure, string objectName) =>
        SystemProcedureMessage(batch, procedure, 64, 15472, $"The object '{objectName}' does not have any indexes, or you do not have permissions.");

    /// <summary>Msg 15647, in place of an empty referencing-view set.</summary>
    internal static SimulatedError NoReferencingViewsMessage(BatchContext batch, string procedure, string objectName) =>
        SystemProcedureMessage(batch, procedure, 211, 15647, $"No views with schema binding reference table '{objectName}'.");

    /// <summary>Msg 15625, an application-lock procedure's unrecognized <c>@LockMode</c> / <c>@LockOwner</c> string, ahead of its -999.</summary>
    internal static SimulatedError AppLockOptionNotRecognizedMessage(BatchContext batch, string procedure, int line, string written, string parameter) =>
        SystemProcedureMessage(batch, procedure, line, 15625, $"Option '{written}' not recognized for '@{parameter}' parameter.");

    /// <summary>Msg 15626, <c>sp_getapplock</c>'s Transaction owner without a transaction, ahead of its -999.</summary>
    internal static SimulatedError TransactionalAppLockWithoutTransactionMessage(BatchContext batch) =>
        SystemProcedureMessage(batch, "sp_getapplock", 52, 15626, "You attempted to acquire a transactional application lock without an active transaction.");

    // ALTER FULLTEXT INDEX's warnings, probed 2026-09-26 against SQL Server
    // 2025 on an index whose population had completed.

    /// <summary>Msg 7638, turning change tracking off.</summary>
    internal static SimulatedError FullTextChangesDeletedMessage(BatchContext batch, string tableName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 7638, $"Warning: Request to stop change tracking has deleted all changes tracked on table or indexed view '{tableName}'.");

    /// <summary>Msg 7673, turning off change tracking that is already off.</summary>
    internal static SimulatedError FullTextChangeTrackingAlreadyOffMessage(BatchContext batch, string tableName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 7673, $"Warning: Full-text change tracking is currently disabled for table or indexed view '{tableName}'.");

    /// <summary>Msg 7661, setting manual change tracking already in force.</summary>
    internal static SimulatedError FullTextChangeTrackingAlreadyOnMessage(BatchContext batch, string tableName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 7661, $"Warning: Full-text change tracking is currently enabled for table or indexed view '{tableName}'.");

    /// <summary>Msg 7662, setting automatic change tracking already in force.</summary>
    internal static SimulatedError FullTextAutoPropagationAlreadyOnMessage(BatchContext batch, string tableName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 7662, $"Warning: Full-text auto propagation is currently enabled for table or indexed view '{tableName}'.");

    /// <summary>Msg 7636, a full or incremental population started under automatic change tracking.</summary>
    internal static SimulatedError FullTextPopulationActiveMessage(BatchContext batch, string tableName) =>
        batch.InfoMessage(@class: 0, state: 2, number: 7636, $"Warning: Request to start a full-text index population on table or indexed view '{tableName}' is ignored because a population is currently active for this table or indexed view.");

    /// <summary>Msg 7676, <c>STOP POPULATION</c> under automatic change tracking.</summary>
    internal static SimulatedError FullTextStopIgnoredMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 7676, "Warning: Full-text auto propagation is on. Stop crawl request is ignored.");

    /// <summary>Msg 9974, <c>PAUSE POPULATION</c> with no full population running — state 1 under automatic change tracking, 2 otherwise.</summary>
    internal static SimulatedError FullTextPauseIgnoredMessage(BatchContext batch, byte state) =>
        batch.InfoMessage(@class: 0, state: state, number: 9974, "Warning: Only running full population can be paused. The command is ignored. Other type of population can just be stopped and it will continue when your start the same type of crawl again.");

    /// <summary>Msg 9975, <c>RESUME POPULATION</c> with nothing paused, outside automatic change tracking.</summary>
    internal static SimulatedError FullTextResumeIgnoredMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 9975, "Warning: Only paused full population can be resumed. The command is ignored.");

    /// <summary>Msg 30022, changing the stoplist <c>WITH NO POPULATION</c>.</summary>
    internal static SimulatedError FullTextStoplistNoPopulationMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 30022, "Warning: The configuration of a full-text stoplist was modified using the WITH NO POPULATION clause. This put the full-text index into an inconsistent state. To bring the full-text index into a consistent state, start a full population. The basic Transact-SQL syntax for this is: ALTER FULLTEXT INDEX ON table_name START FULL POPULATION.");
}
