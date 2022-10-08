namespace SqlServerSimulator;

abstract class SimulatedStatementOutcome
{
    private protected SimulatedStatementOutcome(int recordsAffected)
    {
        this.RecordsAffected = recordsAffected;
    }

    public int RecordsAffected { get; }
}
