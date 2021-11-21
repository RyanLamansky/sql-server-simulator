namespace SqlServerSimulator;

abstract class SimulatedStatementOutcome
{
    protected SimulatedStatementOutcome(int recordsAffected)
    {
        this.RecordsAffected = recordsAffected;
    }

    public int RecordsAffected { get; set; }
}
