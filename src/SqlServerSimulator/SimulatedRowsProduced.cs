namespace SqlServerSimulator;

/// <summary>
/// A marker in a streaming consumer's outcome stream: the suspended
/// <c>SELECT</c> it reads produced another window of rows, or ended (see
/// <see cref="ResultStream"/>). Only the result's own consumer reads it, from
/// inside its pull; any other loop over the outcomes passes over it.
/// </summary>
sealed class SimulatedRowsProduced() : SimulatedStatementOutcome(-1);
