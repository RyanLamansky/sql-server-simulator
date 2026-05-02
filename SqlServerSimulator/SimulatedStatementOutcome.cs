namespace SqlServerSimulator;

abstract class SimulatedStatementOutcome
{
    private protected SimulatedStatementOutcome(int recordsAffected)
    {
        this.RecordsAffected = recordsAffected;
    }

    public readonly int RecordsAffected;
}
