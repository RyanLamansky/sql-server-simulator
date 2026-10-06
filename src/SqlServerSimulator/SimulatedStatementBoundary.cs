namespace SqlServerSimulator;

/// <summary>
/// A marker in a TDS MARS request's outcome stream ahead of each of its
/// batch's statements but the first: the TDS endpoint sends what the
/// statements before it produced, stepping out of the connection's execution
/// gate while the client has yet to read it, so the session's other requests
/// run between this request's statements as real interleaves them (probed
/// 2026-10-06 against SQL Server 2025). Only a command the endpoint marks
/// (<see cref="SimulatedDbCommand.YieldsBetweenStatements"/>) produces one.
/// </summary>
sealed class SimulatedStatementBoundary() : SimulatedStatementOutcome(-1);
