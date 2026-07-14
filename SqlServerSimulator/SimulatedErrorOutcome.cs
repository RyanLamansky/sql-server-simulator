namespace SqlServerSimulator;

/// <summary>
/// A statement-terminating error emitted into the outcome stream instead of
/// thrown, so the batch can continue to the next statement (real SQL Server's
/// default severity model). Produced only when the batch opted into
/// continue-on-error — the wire path (SSMS / SMO target) — and never routed
/// through TRY/CATCH state: the carried <see cref="SimulatedSqlException"/> is
/// bound for the client, not a CATCH block. The in-process ADO reader filters
/// on <see cref="SimulatedQueryResult"/> and so never observes this outcome.
/// <see cref="SimulatedStatementOutcome.RecordsAffected"/> is <c>-1</c> (no
/// row count for a failed statement).
/// </summary>
sealed class SimulatedErrorOutcome(SimulatedSqlException exception) : SimulatedStatementOutcome(-1)
{
    public readonly SimulatedSqlException Exception = exception;
}
