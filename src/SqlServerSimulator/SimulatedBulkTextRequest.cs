namespace SqlServerSimulator;

/// <summary>
/// A <c>WRITETEXT BULK</c> or <c>UPDATETEXT BULK</c> statement waiting for its
/// data, which real reads from the session's next message rather than from
/// the statement (probed 2026-10-07 against SQL Server 2025 with a raw TDS
/// client). Its batch's response ends where the statement begins, as though
/// the batch had finished, and the batch stays suspended on its connection
/// (<see cref="SimulatedDbConnection.ParkedBulkText"/>) until that message
/// arrives: a bulk-load packet carrying a 4-byte length and the bytes, which
/// the statement writes, or anything else, which is not its data — Msg 4022
/// ends the batch, and the message itself is discarded, the batch's remaining
/// output answering it instead. Only a TDS session can send a bulk-load
/// packet, so in process the next command always meets Msg 4022.
/// </summary>
sealed class SimulatedBulkTextRequest() : SimulatedStatementOutcome(-1)
{
    /// <summary>
    /// The suspended batch's outcomes, from the outcome after this one; set
    /// when the batch parks.
    /// </summary>
    public IEnumerator<SimulatedStatementOutcome>? Batch;

    /// <summary>The text of the command the suspended batch belongs to.</summary>
    public string CommandText = string.Empty;

    /// <summary>The bytes the client streamed; null when the next message wasn't a bulk-load packet.</summary>
    public byte[]? Data;

    /// <summary>
    /// The bulk-load packet ended before the length it announced, which real
    /// answers with Msg 4002.
    /// </summary>
    public bool StreamEndedEarly;

    /// <summary>
    /// Whether the waiting statement sits in a <c>TRY</c> block, which changes
    /// how a transaction-manager request's end of the session closes it.
    /// </summary>
    public bool InTry;

    /// <summary>The kind the waiting statement's DONE names.</summary>
    public ushort StatementKind = StatementDoneKind.WriteText;

    /// <summary>
    /// How many procedure or dynamic-SQL scopes the TDS response that ended
    /// here was inside, which the response resuming it renders at batch level
    /// until they close.
    /// </summary>
    public int OpenScopes;
}
