namespace SqlServerSimulator;

/// <summary>
/// A statement that produced no result set. <paramref name="countsRowsReturned"/>
/// marks the one shape whose count is a returned-row count rather than a
/// rows-affected one: the assignment-only <c>SELECT @x = col FROM t</c>, which
/// reports how many rows it read but contributes nothing to a client's
/// <c>RecordsAffected</c> (probe-confirmed against SQL Server 2025).
/// </summary>
sealed class SimulatedNonQuery(int recordsAffected, bool countsRowsReturned = false)
    : SimulatedStatementOutcome(recordsAffected, countsRowsReturned);
