namespace SqlServerSimulator;

/// <summary>
/// A marker in the outcome stream bracketing an <c>EXEC('…')</c> /
/// <c>sp_executesql</c> dynamic-SQL scope. Real SQL Server renders statements
/// executing inside such a scope with DONEINPROC (0xFF) tokens and closes the
/// scope with RETURNSTATUS + DONEPROC — the shape a batch-level statement's
/// plain DONE (0xFD) does not carry. The TDS endpoint consumes these markers to
/// reproduce that discipline; every other outcome consumer (the in-process
/// reader, <c>ExecuteNonQuery</c> / <c>ExecuteScalar</c>) ignores them, since a
/// boundary is neither a <see cref="SimulatedQueryResult"/> nor a
/// <see cref="SimulatedNonQuery"/> and carries <c>RecordsAffected == -1</c>.
/// </summary>
sealed class SimulatedProcScopeBoundary(bool isEnter) : SimulatedStatementOutcome(-1)
{
    /// <summary>True at scope entry (switch to DONEINPROC), false at exit (emit RETURNSTATUS + DONEPROC).</summary>
    public readonly bool IsEnter = isEnter;
}
