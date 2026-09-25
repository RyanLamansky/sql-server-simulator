using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The informational messages the engine sends on its own account, each
// attributed to the statement being dispatched (BatchContext.InfoMessage). All
// class 0 — severity 10 as real delivers it — and probed 2026-09-23 against
// SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 282, when a procedure's <c>RETURN</c> value is NULL; names the
    /// procedure as it is stored, whatever the <c>EXEC</c> spelled. SqlClient
    /// raises no <c>InfoMessage</c> for it when it trails a result set under
    /// <c>ExecuteReader</c> or <c>ExecuteScalar</c>, though real sends it.
    /// </summary>
    internal static SimulatedError NullReturnStatusMessage(BatchContext batch, string procedureName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 282, $"The '{procedureName}' procedure attempted to return a status of NULL, which is not allowed. A status of 0 will be returned instead.");

    /// <summary>Msg 3621, after an execution error ends a statement that writes rows.</summary>
    internal static SimulatedError StatementTerminatedMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3621, "The statement has been terminated.");

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
    internal static SimulatedError MarkedForRecompilationMessage(BatchContext batch, string objectName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 15070, $"Object '{objectName}' was successfully marked for recompilation.");

    /// <summary>Msg 5021, after an <c>ALTER DATABASE … MODIFY NAME</c> (probed 2026-09-25).</summary>
    internal static SimulatedError DatabaseNameSetMessage(BatchContext batch, string databaseName) =>
        batch.InfoMessage(@class: 0, state: 2, number: 5021, $"The database name '{databaseName}' has been set.");

    /// <summary>Msg 5701, after every <c>USE</c> and after renaming the session's own database.</summary>
    internal static SimulatedError DatabaseContextChangedMessage(BatchContext batch, string databaseName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 5701, $"Changed database context to '{databaseName}'.");

    /// <summary>
    /// Msg 5703, after every <c>SET LANGUAGE</c>. Real words it in the language
    /// being switched to; this is the English wording.
    /// </summary>
    internal static SimulatedError LanguageChangedMessage(BatchContext batch, string languageName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 5703, $"Changed language setting to {languageName}.");

    /// <summary>Msg 8153, once per statement whose aggregate skipped a NULL.</summary>
    internal static SimulatedError NullEliminatedMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 8153, "Warning: Null value is eliminated by an aggregate or other SET operation.");

    /// <summary>
    /// Msg 11729, when a sequence's first cache block is longer than the values
    /// it has left; names the sequence without its schema.
    /// </summary>
    internal static SimulatedError SequenceCacheExceedsRangeMessage(BatchContext batch, string sequenceName) =>
        batch.InfoMessage(@class: 0, state: 1, number: 11729, $"The sequence object '{sequenceName}' cache size is greater than the number of available values.");
}
