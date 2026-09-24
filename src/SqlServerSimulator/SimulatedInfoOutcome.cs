namespace SqlServerSimulator;

/// <summary>
/// One informational message — a <c>PRINT</c>, a severity-0-10
/// <c>RAISERROR</c>, a warning such as Msg 8153, or the Msg 3621 that follows
/// a terminated statement — in its place in the outcome stream, the way real
/// SQL Server sends it as an INFO token among the batch's results. The TDS
/// wire writes the token; the in-process ADO surface either fires
/// <see cref="SimulatedDbConnection.InfoMessage"/> or, where SqlClient would,
/// folds the message into the exception an error in the same stretch of the
/// stream raises.
/// </summary>
sealed class SimulatedInfoOutcome(SimulatedError message, bool followsRows = false) : SimulatedStatementOutcome(-1)
{
    public readonly SimulatedError Message = message;

    /// <summary>
    /// Whether the message closes the statement whose outcome precedes it —
    /// Msg 8153, which real sends after the rows and before that statement's
    /// DONE — rather than standing on its own.
    /// </summary>
    public readonly bool FollowsRows = followsRows;
}
