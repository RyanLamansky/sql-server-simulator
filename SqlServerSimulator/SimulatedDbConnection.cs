using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

sealed class SimulatedDbConnection(Simulation simulation) : DbConnection
{
    internal readonly Simulation Simulation = simulation;

    /// <summary>
    /// The database this session is pointed at. Defaults to the entry named
    /// <see cref="Simulation.DefaultDatabaseName"/> at connection construction;
    /// future <c>USE &lt;db&gt;</c> support will switch the pointer to a
    /// different entry of <see cref="Simulation.Databases"/>. Per-database
    /// state (heap tables, compatibility level, rowversion counter) reads
    /// through this pointer.
    /// </summary>
    internal Database CurrentDatabase = simulation.Databases[Simulation.DefaultDatabaseName];

    /// <summary>
    /// Local temp tables (<c>#foo</c>) created from this session. Real SQL
    /// Server name-mangles internally so two sessions can each hold a
    /// <c>#foo</c> independently; the simulator keeps user-visible names
    /// (the mangling is invisible at the SQL surface) and isolates by giving
    /// each connection its own dictionary. Cleared in <see cref="Dispose"/>,
    /// matching the real-server rule that <c>#foo</c> auto-drops at session
    /// close. <c>##foo</c> globals aren't modeled.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: <c>#foo</c> persists across
    /// batches in the same connection, is invisible to other connections
    /// (Msg 208 on access), and database-qualified references
    /// (<c>tempdb..#foo</c>, <c>tempdb.dbo.#foo</c>, even
    /// <c>claude..#foo</c>) all resolve to the local session's table — the
    /// database qualifier is effectively ignored for <c>#</c> names.
    /// </remarks>
    internal readonly ConcurrentDictionary<string, HeapTable> TempTables = new(Collation.Default);

    /// <summary>
    /// The single active explicit transaction on this connection, or null if
    /// none. SqlClient rejects parallel transactions on the same connection
    /// (probe-confirmed: <c>InvalidOperationException: SqlConnection does
    /// not support parallel transactions.</c>); the simulator mirrors that.
    /// Statements executed via this connection consult this field through
    /// <see cref="Simulation.RunMutation"/> — when set, mutations append to
    /// the transaction's <see cref="UndoLog"/> so an eventual
    /// <see cref="SimulatedDbTransaction.Rollback"/> can unwind them.
    /// </summary>
    internal SimulatedDbTransaction? CurrentTransaction;

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
    /// Backs <c>@@ERROR</c>: error number of the most recently completed
    /// statement on this connection, or <c>0</c> on success. Set by the
    /// <c>TRY/CATCH</c> dispatch wrapper when a statement throws a
    /// <see cref="SimulatedSqlException"/> caught at a TRY boundary; reset to
    /// <c>0</c> after every successful statement (so the value reflects the
    /// previous-statement-only contract, matching SQL Server). Before the
    /// TRY/CATCH bundle this was hardcoded to <c>0</c> because errors
    /// terminated the batch — no caught error path existed.
    /// </summary>
    internal int LastErrorNumber;

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
    /// <see cref="Database.VerboseTruncationWarnings"/> setting on the
    /// current database wins; otherwise this connection's trace flag 460
    /// forces verbose; otherwise the database's compatibility level decides
    /// (verbose iff &gt;= <see cref="CompatibilityLevel.Sql160"/>, the level
    /// at which it became default in SQL Server 2022).
    /// </summary>
    internal bool IsVerboseTruncationActive() =>
        this.CurrentDatabase.VerboseTruncationWarnings
        ?? (this.TraceFlags.Contains(460)
            || this.CurrentDatabase.CompatibilityLevel >= CompatibilityLevel.Sql160);

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
        {
            this.CurrentTransaction?.Dispose();
            // Local temp tables auto-drop at session close. Clearing the dict
            // releases each table's Heap and LOB pages for GC; nothing else
            // holds long-lived references to them after the connection ends.
            this.TempTables.Clear();
        }
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
