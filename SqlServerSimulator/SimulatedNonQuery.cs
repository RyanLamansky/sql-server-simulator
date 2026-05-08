namespace SqlServerSimulator;

sealed class SimulatedNonQuery(int recordsAffected) : SimulatedStatementOutcome(recordsAffected);
