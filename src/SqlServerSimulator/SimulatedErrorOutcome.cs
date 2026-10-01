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
sealed class SimulatedErrorOutcome(SimulatedSqlException exception, bool raisedWhileCompiling = false) : SimulatedStatementOutcome(-1)
{
    public readonly SimulatedSqlException Exception = exception;

    /// <summary>
    /// Whether the error is one a compile sent without stopping anything — a
    /// scalar function call real couldn't inline — rather than a failed
    /// statement's. It carries no DONE of its own: the next DONE carries
    /// <c>DONE_ERROR</c> and drops its count flag, so SqlClient neither raises
    /// <c>StatementCompleted</c> for that statement nor counts its rows in
    /// <c>RecordsAffected</c> (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    public readonly bool RaisedWhileCompiling = raisedWhileCompiling;
}

/// <summary>
/// Follows an outcome stream for the count a statement loses to a compile's
/// non-aborting error sent ahead of it (see
/// <see cref="SimulatedErrorOutcome.RaisedWhileCompiling"/>): the first outcome
/// after one that closes with a DONE reports no count to the client.
/// </summary>
internal struct CompileErrorCount
{
    private bool pending;

    /// <summary>
    /// Whether <paramref name="outcome"/>'s count reaches the client's
    /// <c>RecordsAffected</c> — false for the compile's error itself and the
    /// outcome whose DONE carries its error bit.
    /// </summary>
    public bool Admits(SimulatedStatementOutcome outcome)
    {
        switch (outcome)
        {
            case SimulatedErrorOutcome { RaisedWhileCompiling: true }:
                this.pending = true;
                return false;
            case SimulatedInfoOutcome:
            case SimulatedProcScopeBoundary { IsEnter: true }:
            case SimulatedReturnStatus:
                return true;
        }
        if (!this.pending)
            return true;
        this.pending = false;
        return false;
    }
}
