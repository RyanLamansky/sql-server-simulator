using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

sealed class SimulatedDbConnection(Simulation simulation) : DbConnection
{
    internal readonly Simulation Simulation = simulation;

    /// <summary>
    /// The single active explicit transaction on this connection, or null if
    /// none. SqlClient rejects parallel transactions on the same connection
    /// (probe-confirmed: <c>InvalidOperationException: SqlConnection does
    /// not support parallel transactions.</c>); the simulator mirrors that.
    /// Statements executed via this connection consult this field through
    /// <see cref="Simulation.RunMutation"/> — when set, mutations append to
    /// the transaction's <see cref="Storage.UndoLog"/> so an eventual
    /// <see cref="SimulatedDbTransaction.Rollback"/> can unwind them.
    /// </summary>
    internal SimulatedDbTransaction? CurrentTransaction;

    /// <summary>
    /// UTC timestamp captured at the top of each top-level statement (in
    /// <see cref="Simulation.CreateResultSetsForCommand"/>'s loop body) and
    /// consumed by the current-time scalar functions (<c>GETDATE</c>,
    /// <c>GETUTCDATE</c>, <c>SYSDATETIME</c>, <c>SYSUTCDATETIME</c>,
    /// <c>SYSDATETIMEOFFSET</c>, <c>CURRENT_TIMESTAMP</c>). Real SQL Server
    /// freezes these within a statement (probe-confirmed 2026-05-09 — two
    /// <c>SYSDATETIME()</c> calls in one SELECT return identical values to
    /// the 7th decimal digit; an UPDATE that stamps every row with
    /// <c>SYSDATETIME()</c> writes the same value into all rows). The
    /// simulator follows by capturing once per statement and serving every
    /// call within that statement from the same snapshot. The simulator does
    /// no local-time conversion: per the Azure SQL Database default,
    /// local-time-returning variants (<c>GETDATE</c> / <c>SYSDATETIME</c> /
    /// <c>CURRENT_TIMESTAMP</c>) and UTC-returning variants share this single
    /// UTC instant, and <c>SYSDATETIMEOFFSET</c> reports a <c>+00:00</c>
    /// offset.
    /// </summary>
    /// <remarks>
    /// Lives on the connection (not the <see cref="Simulation"/>) because
    /// <see cref="DbConnection"/> isn't thread-safe and statements execute
    /// serially through one connection — the field's safe-by-construction
    /// against the multi-connection-on-shared-Simulation race that broader
    /// session state (<see cref="LastStatementRowCount"/> etc.) faces.
    /// </remarks>
    internal DateTime CurrentStatementUtcNow;

    /// <summary>
    /// Backs <c>@@ROWCOUNT</c>. Updated after each statement in
    /// <see cref="Simulation.CreateResultSetsForCommand"/>: DML mutations
    /// write their affected-row count; SELECT writes its produced-row count
    /// after the reader fully iterates; SELECT-assign writes the rows-scanned
    /// count; <c>SET</c> and <c>DECLARE @v T = init</c> write 1; bare
    /// <c>DECLARE @v T</c> (no initializer) leaves it unchanged; most other
    /// statement kinds reset to 0. Probe-confirmed against SQL Server 2025
    /// (2026-05-12).
    /// </summary>
    internal int LastStatementRowCount;

    /// <summary>
    /// Last identity value produced by an INSERT on this connection — the
    /// source for both <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c>. SQL
    /// Server scopes these per session/scope; the simulator collapses both
    /// to a single per-connection slot.
    /// </summary>
    /// <remarks>
    /// Cleared (set to <c>null</c>) by every INSERT that doesn't generate
    /// or accept an identity value — matching SQL Server's behavior of
    /// resetting <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c> when the
    /// most recent statement didn't touch an identity column.
    /// </remarks>
    internal decimal? LastIdentity;

    /// <summary>
    /// Name of the table currently under <c>SET IDENTITY_INSERT ... ON</c>
    /// for this connection, or <c>null</c> when no table is in that mode.
    /// SQL Server allows only one table at a time per session; the simulator
    /// enforces the same per connection.
    /// </summary>
    internal string? IdentityInsertTable;

    /// <summary>
    /// Active session-scoped trace flags toggled via <c>DBCC TRACEON(N)</c>
    /// / <c>DBCC TRACEOFF(N)</c>. The simulator doesn't model the separate
    /// global scope; <c>WITH -1</c> isn't honored. Lives per connection so
    /// concurrent connections don't trample each other's flags.
    /// </summary>
    internal readonly HashSet<int> TraceFlags = [];

    /// <summary>
    /// Decides whether string truncation should raise the verbose Msg 2628
    /// (with table, column, and truncated value) or the legacy Msg 8152
    /// (single line, no detail). Precedence: an explicit
    /// <see cref="Simulation.VerboseTruncationWarnings"/> setting wins;
    /// otherwise this connection's trace flag 460 forces verbose; otherwise
    /// the database compatibility level decides (verbose iff &gt;=
    /// <see cref="CompatibilityLevel.Sql160"/>, the level at which it became
    /// default in SQL Server 2022).
    /// </summary>
    internal bool IsVerboseTruncationActive() =>
        this.Simulation.VerboseTruncationWarnings
        ?? (this.TraceFlags.Contains(460)
            || this.Simulation.CompatibilityLevel >= CompatibilityLevel.Sql160);

    [AllowNull]
    public override string ConnectionString { get => ""; set => throw new NotImplementedException(); }

    public override string Database => "master";

    public override string DataSource => "simulator";

    public override string ServerVersion => "16.0.0";

    private ConnectionState state;

    public override ConnectionState State => this.state;

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotImplementedException();
    }

    public override void Close()
    {
        // SqlClient auto-rolls-back any active transaction when its
        // connection closes. The transaction's own dispose handles the
        // explicit using-pattern; this branch covers raw Close() without
        // disposing the transaction first.
        this.CurrentTransaction?.Rollback();
        this.state = ConnectionState.Closed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.CurrentTransaction?.Dispose();
        base.Dispose(disposing);
    }

    public override void Open()
    {
        this.state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (this.CurrentTransaction is not null)
            throw new InvalidOperationException("SqlConnection does not support parallel transactions.");
        var tx = new SimulatedDbTransaction(this.Simulation, this, isolationLevel);
        this.CurrentTransaction = tx;
        return tx;
    }

    protected override DbCommand CreateDbCommand()
    {
        return new SimulatedDbCommand(this.Simulation, this);
    }
}
