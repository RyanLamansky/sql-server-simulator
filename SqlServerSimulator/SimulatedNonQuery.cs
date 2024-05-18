namespace SqlServerSimulator;

sealed class SimulatedNonQuery(int recordsAffected) : SimulatedStatementOutcome(recordsAffected)
{
    public SimulatedNonQuery()
        : this(-1)
    {
    }
}
