namespace SqlServerSimulator;

abstract class SimulatedStatementOutcome
{
    private protected SimulatedStatementOutcome(int recordsAffected, bool countsRowsReturned = false)
    {
        this.RecordsAffected = recordsAffected;
        this.CountsRowsReturned = countsRowsReturned;
    }

    public readonly int RecordsAffected;

    /// <summary>
    /// Whether <see cref="RecordsAffected"/> counts rows the statement
    /// <em>returned</em> rather than rows it <em>changed</em>. Real SQL Server
    /// tags every DONE token with the kind of statement that produced it, and a
    /// client leaves the SELECT kind out when it accumulates
    /// <c>RecordsAffected</c> — a SELECT still reports its row count on the
    /// wire (drivers read it as a row count), but that count is not a
    /// rows-affected count. True for a tabular result and for the
    /// assignment-only <c>SELECT @x = col FROM t</c>; false for DML, including
    /// a DML statement whose <c>OUTPUT</c> clause makes it tabular.
    /// </summary>
    public readonly bool CountsRowsReturned;

    /// <summary>
    /// Whether <c>SET NOCOUNT ON</c> was in effect when the producing statement
    /// finished, which suppresses the count everywhere a client reads one.
    /// Null until the dispatch loop stamps it; the innermost frame wins, so a
    /// statement inside a procedure body records the setting the body ran
    /// under rather than the one that survives the body's exit.
    /// </summary>
    public bool? CountSuppressed;

    /// <summary>
    /// What this outcome contributes to <c>ExecuteNonQuery</c> and to
    /// <c>DbDataReader.RecordsAffected</c>, or <c>-1</c> when it contributes
    /// nothing: its count, unless <c>NOCOUNT</c> suppressed it or the count is
    /// a returned-row count.
    /// </summary>
    public int ClientRecordsAffected =>
        this.CountSuppressed == true || this.CountsRowsReturned ? -1 : this.RecordsAffected;
}
