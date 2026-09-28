namespace SqlServerSimulator;

/// <summary>
/// A marker in the outcome stream bracketing a procedure call or an
/// <c>EXEC('…')</c> / <c>sp_executesql</c> dynamic-SQL scope. Real SQL Server
/// renders statements executing inside such a scope with DONEINPROC (0xFF)
/// tokens and closes the scope with RETURNSTATUS + DONEPROC — the shape a
/// batch-level statement's plain DONE (0xFD) does not carry — or, for a scope
/// nested in another, with a DONEINPROC and no RETURNSTATUS. The TDS endpoint
/// consumes these markers to reproduce that discipline; every other outcome
/// consumer (the in-process reader, <c>ExecuteNonQuery</c> /
/// <c>ExecuteScalar</c>) ignores them, since a boundary is neither a
/// <see cref="SimulatedQueryResult"/> nor a <see cref="SimulatedNonQuery"/>
/// and carries <c>RecordsAffected == -1</c>.
/// </summary>
sealed class SimulatedProcScopeBoundary(bool isEnter, int? returnStatus = null, bool endedByError = false) : SimulatedStatementOutcome(-1)
{
    /// <summary>True at scope entry (switch to DONEINPROC), false at exit (emit RETURNSTATUS + DONEPROC).</summary>
    public readonly bool IsEnter = isEnter;

    /// <summary>
    /// At exit, the status the scope returned, which a batch-level call sends
    /// as RETURNSTATUS; null for a scope an error abandoned, which real closes
    /// with no RETURNSTATUS (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public readonly int? ReturnStatus = returnStatus;

    /// <summary>
    /// At exit, whether an error the caller didn't catch abandoned the scope,
    /// which real reports on the closing DONEPROC's error bit.
    /// </summary>
    public readonly bool EndedByError = endedByError;
}
