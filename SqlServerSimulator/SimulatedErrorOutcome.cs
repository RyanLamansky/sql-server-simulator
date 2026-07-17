namespace SqlServerSimulator;

/// <summary>
/// A statement-terminating error emitted into the outcome stream instead of
/// thrown, so the batch can continue to the next statement (real SQL Server's
/// default severity model). Produced by every top-level batch (both front
/// doors) and never routed through TRY/CATCH state: the carried
/// <see cref="SimulatedSqlException"/> is bound for the client, not a CATCH
/// block. The two renderers consume it differently — the TDS wire writes its
/// error token(s); the in-process ADO surface converts it to a throw
/// (positionally in a reader, aggregated at completion for
/// ExecuteNonQuery / ExecuteScalar).
/// <see cref="SimulatedStatementOutcome.RecordsAffected"/> is <c>-1</c> (no
/// row count for a failed statement).
/// </summary>
sealed class SimulatedErrorOutcome(SimulatedSqlException exception, bool rowReturning) : SimulatedStatementOutcome(-1)
{
    public readonly SimulatedSqlException Exception = exception;

    /// <summary>
    /// Whether the failed statement was row-returning (a SELECT / VALUES that
    /// real SQL Server frames with COLMETADATA before the error). The
    /// in-process reader uses it to choose between positional surfacing (the
    /// reader advances onto the failed statement and the first <c>Read</c>
    /// throws) and eager surfacing (the error throws on the advance itself, at
    /// <c>ExecuteReader</c> or <c>NextResult</c>) — see
    /// <c>StatementContext.LeadingKeywordReturnsRows</c>. The TDS wire and the
    /// drain-to-completion APIs (ExecuteNonQuery / ExecuteScalar) ignore it.
    /// </summary>
    public readonly bool RowReturning = rowReturning;
}
