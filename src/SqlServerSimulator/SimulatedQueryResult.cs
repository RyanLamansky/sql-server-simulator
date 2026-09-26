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
    /// Per column, the user alias type the column carries (see
    /// <c>Selection.ColumnAliasTypes</c>), which
    /// <c>sp_describe_first_result_set</c> reports as its user type.
    /// </summary>
    internal Schemas.AliasType?[]? ColumnAliasTypes;

    /// <summary>The identity each column passes straight through (<c>Selection.ColumnIdentitySources</c>), which describing the result reports.</summary>
    internal Storage.IdentityState?[]? ColumnIdentitySources;

    /// <summary>
    /// Per column, the COLMETADATA flag bits a column carries besides
    /// fNullable, as SQL Server 2025 sets them (captured 2026-09-26): a column
    /// read from a table — through views, joins and derived tables alike —
    /// keeps its table's character, <c>0x10</c> (fIdentity, read-only) for an
    /// identity column, <c>0x20</c> (fComputed, read-only) for a computed one,
    /// <c>0x00</c> (read-only) for a rowversion and <c>0x08</c> (updatability
    /// unknown) for the rest; a scalar expression is <c>0x20</c>; an aggregate,
    /// a window function or a set operation's column is <c>0x00</c>. Null
    /// means every column is <c>0x08</c>.
    /// </summary>
    public byte[]? ColumnWireFlags;

    /// <summary>
    /// How many of the trailing columns are hidden: counted by <c>FieldCount</c>
    /// but not by <c>VisibleFieldCount</c> or <c>GetValues</c>, and flagged
    /// <c>fHidden</c> in TDS COLMETADATA. Only a cursor fetch's trailing
    /// <c>ROWSTAT</c> column is hidden.
    /// </summary>
    public int HiddenColumnCount;

    /// <summary>
    /// Browse-mode metadata — set on a statement's result while
    /// <c>SET NO_BROWSETABLE</c> is on — which the TDS endpoint sends as the
    /// TABNAME and COLINFO tokens after COLMETADATA; null otherwise.
    /// </summary>
    internal BrowseInfo? Browse;

    /// <summary>
    /// Per column, the base-table column it reads directly, or null for an
    /// expression — set only by the <c>SET FMTONLY</c> metadata path, which
    /// is what <c>sp_describe_first_result_set</c> describes through.
    /// </summary>
    internal HeapColumn?[]? ColumnOrigins;

    /// <summary>
    /// Per column, whether it is a scalar expression rather than a column, an
    /// aggregate or a window function — <c>sp_describe_first_result_set</c>'s
    /// <c>is_computed_column</c>. Set alongside <see cref="ColumnOrigins"/>.
    /// </summary>
    internal bool[]? ColumnIsComputed;

    /// <summary>Whether the query grouped its rows, so no column is updatable through it.</summary>
    internal bool IsGrouped;

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

/// <summary>
/// What a browse-mode result tells a client about each column's origin, the
/// TDS TABNAME and COLINFO tokens' content: the base tables as the query
/// spelled them, and per column its 1-based table number (0 for none), its
/// status — EXPRESSION <c>0x04</c>, KEY <c>0x08</c>, HIDDEN <c>0x10</c>,
/// DIFFERENT_NAME <c>0x20</c> — and, for a renamed column, the base name.
/// </summary>
internal sealed class BrowseInfo(string[][] tables, (byte Table, byte Status, string? BaseName)[] columns)
{
    public readonly string[][] Tables = tables;

    public readonly (byte Table, byte Status, string? BaseName)[] Columns = columns;
}
