namespace SqlServerSimulator;

/// <summary>
/// A procedure's return status sent on its own, ahead of an error that then
/// closes the procedure's scope — the order real gives a procedure that
/// returns with a mismatched transaction count: RETURNSTATUS, Msg 266, then
/// the DONEPROC carrying the error (probed 2026-09-28 against SQL Server
/// 2025). Only the TDS endpoint renders it; every other consumer ignores it.
/// </summary>
sealed class SimulatedReturnStatus(int status) : SimulatedStatementOutcome(-1)
{
    public readonly int Status = status;
}
