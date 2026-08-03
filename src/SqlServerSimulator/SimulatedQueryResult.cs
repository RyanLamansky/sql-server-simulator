using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Base for tabular query results. A result exposes its column names and
/// produces a fresh <see cref="RowCursor"/> for each consumer.
/// </summary>
internal abstract class SimulatedQueryResult : SimulatedStatementOutcome
{
    private protected SimulatedQueryResult()
        : this(-1)
    {
    }

    /// <summary>
    /// Carries a rows-affected count on a tabular result — the DML statement
    /// whose <c>OUTPUT</c> clause returns its touched rows to the client. Such
    /// a statement still reports the rows it changed, so the count is a
    /// rows-affected one; every other result set passes <c>-1</c> and its row
    /// count stays a returned-row count.
    /// </summary>
    private protected SimulatedQueryResult(int recordsAffected)
        : base(recordsAffected, countsRowsReturned: recordsAffected < 0)
    {
    }

    /// <summary>Column names in result order; empty string for anonymous columns.</summary>
    public abstract string[] ColumnNames { get; }

    /// <summary>
    /// SQL types in result order. Carried alongside <see cref="ColumnNames"/>
    /// so the data reader can answer <c>GetDataTypeName</c> / <c>GetFieldType</c>
    /// without holding (or having to navigate) any current row.
    /// </summary>
    public abstract SqlType[] Schema { get; }

    /// <summary>
    /// Per-column nullability parallel to <see cref="Schema"/>; true =
    /// nullable. Null means unknown, which metadata consumers (the TDS
    /// COLMETADATA fNullable flag) treat as all-nullable. Populated only by
    /// the single-source no-join SELECT projection — see
    /// <c>Selection.ColumnNullability</c> for the inference contract and
    /// the DacFx bacpac-export dependency.
    /// </summary>
    public bool[]? ColumnNullability;

    /// <summary>
    /// Per-column decimal-family name parallel to <see cref="Schema"/>;
    /// <see langword="true"/> = report the <c>numeric</c> type name rather than
    /// <c>decimal</c> (JDBC <c>getColumnTypeName</c> / the TDS COLMETADATA
    /// NUMERICN token / the in-process <c>GetDataTypeName</c>). Null means every
    /// decimal column reports <c>decimal</c> — the common case, so most plans
    /// carry no extra array. Meaningful only where <see cref="Schema"/> is
    /// <c>decimal</c>; the two names share one <see cref="SqlType"/>, so
    /// this stays metadata-only and never reaches storage / type identity.
    /// Populated by the SELECT projection via <c>Expression.ResultReportsNumeric</c>.
    /// </summary>
    public bool[]? ColumnReportsNumeric;

    /// <summary>
    /// The session's <c>SET TEXTSIZE</c> byte cap in effect when this result
    /// was produced; <c>-1</c> = unlimited. Stamped by the dispatch loop at
    /// statement materialization so a later-read result truncates under the
    /// value that governed its statement (a proc body's <c>SET TEXTSIZE</c>
    /// reverts at proc exit, but the result sets it produced keep its cap —
    /// probe-confirmed against SQL Server 2025, 2026-07-19).
    /// </summary>
    public int ClientTextSize = -1;

    /// <summary>
    /// 1-based line of the statement that produced this result, already
    /// adjusted by its batch's <c>LineOffset</c>; <c>0</c> = unstamped.
    /// Written once by the dispatch loop at statement materialization, so the
    /// innermost frame — a procedure body, a dynamic-SQL batch — wins as the
    /// result propagates outward. Paired with <see cref="OriginProcedure"/>,
    /// it lets a consumer that fails while projecting an already-produced
    /// result attribute the error to where the rows came from rather than to
    /// itself; <c>EXEC … WITH RESULT SETS</c> is the consumer that needs it
    /// (real reports the module's own SELECT for Msg 11535 / 11537 / 11538 /
    /// 11553, not the EXECUTE statement).
    /// </summary>
    public int OriginLine;

    /// <summary>
    /// Schema-qualified name of the procedure / trigger whose body produced
    /// this result, or empty for a top-level or dynamic-SQL batch. Stamped
    /// alongside <see cref="OriginLine"/>.
    /// </summary>
    public string OriginProcedure = "";

    /// <summary>Creates a fresh cursor that iterates this result's rows.</summary>
    public abstract RowCursor CreateCursor();

    /// <summary>
    /// A cursor for a client-boundary consumer (the in-process data reader /
    /// <c>ExecuteScalar</c>, the TDS row writer): applies the
    /// <see cref="ClientTextSize"/> truncation real SQL Server performs at
    /// wire egress. Engine-internal consumers use <see cref="CreateCursor"/>.
    /// </summary>
    public RowCursor CreateClientCursor()
    {
        var cursor = this.CreateCursor();
        return this.ClientTextSize < 0 ? cursor : new TextSizeCursor(cursor, this.Schema, this.ClientTextSize);
    }
}
