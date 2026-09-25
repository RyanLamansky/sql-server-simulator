namespace SqlServerSimulator;

/// <summary>
/// A statement-terminating error emitted into the outcome stream instead of
/// thrown, so the batch can continue to the next statement (real SQL Server's
/// default severity model). Produced by every top-level batch (both front
/// doors) and never routed through TRY/CATCH state: the carried
/// <see cref="SimulatedSqlException"/> is bound for the client, not a CATCH
/// block. The two renderers consume it differently — the TDS wire writes its
/// error token(s); the in-process ADO surface converts it to a throw (on the
/// reader's advance onto it, aggregated at completion for
/// ExecuteNonQuery / ExecuteScalar).
/// <see cref="SimulatedStatementOutcome.RecordsAffected"/> is <c>-1</c> (no
/// row count for a failed statement).
/// </summary>
sealed class SimulatedErrorOutcome(SimulatedSqlException exception) : SimulatedStatementOutcome(-1)
{
    public readonly SimulatedSqlException Exception = exception;
}
