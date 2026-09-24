using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The informational messages the engine sends on its own account, each
// attributed to the statement being dispatched (BatchContext.InfoMessage). All
// class 0 — severity 10 as real delivers it — and probed 2026-09-23 against
// SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>Msg 3621, after an execution error ends a statement that writes rows.</summary>
    internal static SimulatedError StatementTerminatedMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 0, number: 3621, "The statement has been terminated.");

    /// <summary>Msg 5701, after every <c>USE</c>.</summary>
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
