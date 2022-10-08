namespace SqlServerSimulator;

sealed class SimulatedNonQuery : SimulatedStatementOutcome
{
    public SimulatedNonQuery()
        : this(-1)
    {
    }

    public SimulatedNonQuery(int recordsAffected)
        : base(recordsAffected)
    {
    }
}
