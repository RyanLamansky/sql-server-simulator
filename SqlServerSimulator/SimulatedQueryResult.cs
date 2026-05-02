namespace SqlServerSimulator;

/// <summary>
/// Base for tabular query results. A result exposes its column names and
/// produces a fresh <see cref="RowCursor"/> for each consumer.
/// </summary>
internal abstract class SimulatedQueryResult : SimulatedStatementOutcome
{
    private protected SimulatedQueryResult()
        : base(-1)
    {
    }

    /// <summary>Column names in result order; empty string for anonymous columns.</summary>
    public abstract string[] ColumnNames { get; }

    /// <summary>Creates a fresh cursor that iterates this result's rows.</summary>
    public abstract RowCursor CreateCursor();
}
