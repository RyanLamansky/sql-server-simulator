namespace SqlServerSimulator;

abstract class SimulatedStatementOutcome
{
    private protected SimulatedStatementOutcome(int recordsAffected, bool countsRowsReturned = false)
    {
        this.RecordsAffected = recordsAffected;
        this.CountsRowsReturned = countsRowsReturned;
    }

    /// <summary>
    /// The rows the statement changed, or returned when
    /// <see cref="CountsRowsReturned"/> says so; settled as its last row goes
    /// out for a DML statement that writes its rows as it sends them
    /// (<see cref="SimulatedSqlResultSet.CountPending"/>).
    /// </summary>
    public int RecordsAffected;

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
    /// The kind of statement real SQL Server names in the DONE token that
    /// closes this outcome (a <see cref="StatementDoneKind"/> value), stamped by
    /// the statement that produced it; <see cref="StatementDoneKind.NoDone"/>
    /// until then. Only the TDS endpoint reads it.
    /// </summary>
    public ushort DoneKind;

    /// <summary>
    /// Whether the producing statement ran in a trigger body, whose DONE
    /// tokens are DONEINPROC as a procedure's are, and whose count-less DONEs
    /// <c>NOCOUNT</c> drops as it does a procedure's (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    public bool InModule;

    /// <summary>
    /// How many transaction events the session had recorded when the producing
    /// statement finished (<see cref="SimulatedDbConnection.TransactionEventsRecorded"/>),
    /// or -1 when unstamped: the TDS endpoint announces the events before that
    /// mark ahead of this outcome's DONE, which keeps a procedure body's
    /// transaction ENVCHANGEs beside the statements that caused them though
    /// the body ran whole before its first outcome went out.
    /// </summary>
    public int TransactionEventMark = -1;

    /// <summary>
    /// What this outcome contributes to <c>ExecuteNonQuery</c> and to
    /// <c>DbDataReader.RecordsAffected</c>, or <c>-1</c> when it contributes
    /// nothing: its count, unless <c>NOCOUNT</c> suppressed it or the count is
    /// a returned-row count.
    /// </summary>
    public int ClientRecordsAffected =>
        this.CountSuppressed == true || this.CountsRowsReturned ? -1 : this.RecordsAffected;
}
