using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// ADO.NET <see cref="DbConnection"/> against a <see cref="Simulation"/>.
/// Obtain via <see cref="Simulation.CreateDbConnection"/>; constructor is
/// internal (apps shouldn't materialize the connection without a backing
/// simulation). The class is public so consumers can cast a base-typed
/// <see cref="DbConnection"/> down to subscribe to
/// <see cref="InfoMessage"/> the same way they would against a
/// <c>SqlConnection</c>.
/// </summary>
public sealed class SimulatedDbConnection : DbConnection
{
    internal readonly Simulation Simulation;

    internal SimulatedDbConnection(Simulation simulation)
    {
        this.Simulation = simulation;
        this.Spid = simulation.AllocateSpid();
        this.CurrentDatabase = ResolveInitialDatabase(simulation);
        simulation.RegisterConnection(this);
    }

    /// <summary>
    /// Picks the database a fresh connection points its
    /// <see cref="CurrentDatabase"/> at. Three-tier resolution:
    /// <list type="number">
    /// <item>The conventional default (<see cref="Simulation.DefaultDatabaseName"/>)
    /// if present — preserves the all-T-SQL "fresh Simulation just works"
    /// path.</item>
    /// <item>When <see cref="Simulation.Databases"/> is empty, lazily seed
    /// the default — fresh <see cref="Simulation"/>'s ctor starts empty so
    /// no-collision <c>ImportBacpac</c> shapes work; the first connection
    /// pays the cost of materializing the default when no import
    /// preceded it.</item>
    /// <item>Otherwise pick the alphabetically-first database — predictable
    /// fallback for the multi-import scenario, matching the ordering
    /// <c>sys.databases</c> uses. Pending real <c>USE &lt;db&gt;</c>
    /// support, the user can still inspect any database via catalog
    /// views regardless of which one a connection's CurrentDatabase
    /// happens to be pointed at.</item>
    /// </list>
    /// </summary>
    private static Database ResolveInitialDatabase(Simulation simulation)
    {
        lock (simulation.Databases)
        {
            if (simulation.Databases.TryGetValue(Simulation.DefaultDatabaseName, out var existing))
                return existing;
            if (simulation.Databases.Count == 0)
            {
                var seeded = new Database(Simulation.DefaultDatabaseName);
                simulation.Databases.Add(Simulation.DefaultDatabaseName, seeded);
                return seeded;
            }
            return simulation.Databases
                .OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Value;
        }
    }

    /// <summary>
    /// Session id. Allocated once at connection construction via
    /// <see cref="Simulation.AllocateSpid"/>; first user connection on a
    /// fresh <see cref="Simulation"/> gets 51, matching SQL Server's
    /// "user SPIDs start at 51" convention. Surfaced in Msg 1205's
    /// deadlock-victim wording (<c>Process ID &lt;N&gt;</c>) and will surface
    /// in <c>@@SPID</c> / <c>sys.dm_exec_sessions.session_id</c> if those
    /// are ever projected.
    /// </summary>
    internal readonly int Spid;

    /// <summary>
    /// Session-scoped <c>@@LOCK_TIMEOUT</c>. Default is <c>-1</c> (wait
    /// indefinitely — probe-confirmed against SQL Server 2025: a fresh
    /// connection reads <c>@@LOCK_TIMEOUT = -1</c> before any explicit
    /// <c>SET LOCK_TIMEOUT</c>). <c>0</c> = fail-fast on first conflict;
    /// positive N = wait up to N ms before raising Msg 1222. Set via
    /// <c>SET LOCK_TIMEOUT &lt;N&gt;</c>.
    /// </summary>
    internal int LockTimeoutMillis = -1;

    /// <summary>
    /// Session-scoped transaction-isolation level. Default is
    /// <see cref="IsolationLevel.ReadCommitted"/> (matches SQL Server's
    /// connection-default). Mutated by
    /// <c>SET TRANSACTION ISOLATION LEVEL { READ UNCOMMITTED | READ COMMITTED |
    /// REPEATABLE READ | SNAPSHOT | SERIALIZABLE }</c>; carries forward across
    /// statements until the next SET or connection close. Drives:
    /// <list type="bullet">
    /// <item><c>READ UNCOMMITTED</c> — readers behave like <c>WITH (NOLOCK)</c>
    /// (skip row-conflict check, dirty reads).</item>
    /// <item><c>READ COMMITTED</c> (default) — readers conflict-check each
    /// row against tx-scoped row-X holders; no row-S retained.</item>
    /// <item><c>REPEATABLE READ</c> — readers acquire tx-scoped row-S per
    /// row read (so reread sees the same value).</item>
    /// <item><c>SERIALIZABLE</c> — REPEATABLE READ + tx-scoped table-S at
    /// scan start (phantom protection at table granularity since the simulator
    /// has no indexes to range-lock through).</item>
    /// <item><c>SNAPSHOT</c> — parses-and-discards (MVCC is phase 3); behaves
    /// as READ COMMITTED for now.</item>
    /// </list>
    /// </summary>
    internal IsolationLevel SessionIsolationLevel = IsolationLevel.ReadCommitted;

    /// <summary>
    /// Managed thread id of the OS thread currently executing a command
    /// against this connection, or <c>null</c> when no command is in flight.
    /// Set at the top of <see cref="Simulation.CreateResultSetsForCommand"/>'s
    /// outer wrapper and cleared in its <c>finally</c>. Drives
    /// <see cref="LockManager"/>'s same-thread-deadlock detection: a
    /// conflicting holder whose <see cref="CurrentExecutingThreadId"/>
    /// matches the caller's thread can't release without the caller
    /// releasing first, so Msg 1205 fires immediately instead of waiting.
    /// </summary>
    internal int? CurrentExecutingThreadId;

    /// <summary>
    /// Resource this connection is currently blocked waiting to acquire,
    /// or <c>null</c> when not in a wait state. Set under the
    /// <see cref="LockManager"/> gate just before <see cref="Monitor.Wait(object, int)"/>
    /// suspends the caller; cleared in the wait's <c>finally</c> so an
    /// exception (Msg 1222 / 1205) leaves no stale edge. Read by
    /// other connections' cycle-detection walks (under the same gate, so
    /// the snapshot is consistent) to spot a wait-for-graph cycle that
    /// includes this connection.
    /// </summary>
    internal LockResource? WaitingOnResource;

    /// <summary>
    /// Mode this connection is currently waiting to acquire on
    /// <see cref="WaitingOnResource"/>, or <c>null</c> when not waiting.
    /// Surfaced through <c>sys.dm_os_waiting_tasks.wait_type</c> as
    /// <c>LCK_M_&lt;mode&gt;</c> (e.g. <c>LCK_M_X</c>, <c>LCK_M_S</c>) and
    /// through <c>sys.dm_tran_locks.request_mode</c> for WAIT-status rows.
    /// </summary>
    internal LockMode? WaitingForMode;

    /// <summary>
    /// The database this session is pointed at. Defaults to the entry named
    /// <see cref="Simulation.DefaultDatabaseName"/> at connection construction;
    /// future <c>USE &lt;db&gt;</c> support will switch the pointer to a
    /// different entry of <see cref="Simulation.Databases"/>. Per-database
    /// state (heap tables, compatibility level, rowversion counter) reads
    /// through this pointer.
    /// </summary>
    internal Database CurrentDatabase;

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
    internal readonly ConcurrentDictionary<string, HeapTable> TempTables = new(BuiltInToken.Comparer);

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
    /// Current nesting depth of in-flight scalar UDF / stored-proc / trigger
    /// / view calls on this connection. Incremented when
    /// <c>Simulation.InvokeScalarFunction</c> enters a body, decremented when
    /// it exits. Real SQL Server caps this combined depth at 32 — exceeding
    /// raises <c>Msg 217</c> (probe-confirmed verbatim against SQL Server
    /// 2025). Today only scalar UDFs contribute; stored procs / triggers /
    /// views will share the same counter when added.
    /// </summary>
    internal int NestingLevel;

    /// <summary>Real SQL Server's combined nesting cap (probe-confirmed).</summary>
    internal const int MaxNestingLevel = 32;

    /// <summary>
    /// ObjectIds of triggers currently mid-fire on this connection. The
    /// DML dispatcher consults this set before firing a trigger and skips
    /// any whose ObjectId is already in flight — matches SQL Server's
    /// default <c>RECURSIVE_TRIGGERS OFF</c> behavior (direct same-trigger
    /// recursion is suppressed; trigger-to-other-trigger chains still fire).
    /// Probe-confirmed: an update inside a trigger doesn't re-fire that
    /// same trigger, even though the table-level update otherwise would.
    /// </summary>
    internal readonly HashSet<int> FiringTriggerIds = [];

    /// <summary>
    /// Current trigger nesting depth — incremented each time a trigger
    /// body begins executing on this connection, decremented on exit.
    /// Surfaced by the <c>TRIGGER_NESTLEVEL()</c> scalar (probe-confirmed
    /// to return 1 at the top-level DML's first trigger and 2+ when
    /// trigger bodies fire further DML that itself triggers).
    /// </summary>
    internal int TriggerNestLevel;

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

    /// <summary>
    /// Fires once per batch when the batch contained at least one
    /// <c>PRINT</c> or severity-0-10 <c>RAISERROR</c> statement that
    /// produced output (the un-taken-IF / skip-mode path doesn't fire).
    /// Multiple contributing statements in the batch coalesce into a single
    /// event with the messages joined by <c>\n</c> — matches SqlClient's
    /// <c>InfoMessage</c> probe behavior. Mirrors the shape of
    /// <c>SqlConnection.InfoMessage</c> so consumers can subscribe
    /// identically after casting a base-typed <see cref="DbConnection"/>
    /// down to <see cref="SimulatedDbConnection"/>.
    /// </summary>
    public event EventHandler<SimulatedInfoMessageEventArgs>? InfoMessage;

    /// <summary>
    /// Delivers a buffered <c>PRINT</c> / informational <c>RAISERROR</c>
    /// batch to <see cref="InfoMessage"/> subscribers. Called from
    /// <see cref="Parser.BatchContext.FlushPrintMessages"/> at the end of
    /// each command's dispatch.
    /// </summary>
    internal void RaiseInfoMessage(SimulatedInfoMessageEventArgs args) => this.InfoMessage?.Invoke(this, args);

    /// <inheritdoc/>
    [AllowNull]
    public override string ConnectionString { get => ""; set => throw new NotImplementedException(); }

    /// <inheritdoc/>
    public override string Database => "master";

    /// <inheritdoc/>
    public override string DataSource => "simulator";

    /// <inheritdoc/>
    public override string ServerVersion => "16.0.0";

    private ConnectionState state;

    /// <inheritdoc/>
    public override ConnectionState State => this.state;

    /// <inheritdoc/>
    public override void ChangeDatabase(string databaseName)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public override void Close()
    {
        // SqlClient auto-rolls-back any active transaction when its
        // connection closes. The transaction's own dispose handles the
        // explicit using-pattern; this branch covers raw Close() without
        // disposing the transaction first.
        this.CurrentTransaction?.Rollback();
        this.state = ConnectionState.Closed;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.CurrentTransaction?.Dispose();
            // Local temp tables auto-drop at session close. Clearing the dict
            // releases each table's Heap and LOB pages for GC; nothing else
            // holds long-lived references to them after the connection ends.
            this.TempTables.Clear();
            // Global temp tables: drop every ##foo owned by this connection.
            // Probe-confirmed against SQL Server 2025 (pooling disabled) that
            // owner-disconnect drops ##foo unconditionally, regardless of
            // other sessions' prior or in-flight references. Walk a snapshot
            // of the keys so concurrent reads / drops by other connections
            // don't trip dictionary mutation.
            foreach (var name in this.Simulation.GlobalTempTables.Keys)
            {
                if (this.Simulation.GlobalTempTables.TryGetValue(name, out var table)
                    && ReferenceEquals(table.OwnerConnection, this))
                {
                    _ = this.Simulation.GlobalTempTables.TryRemove(name, out _);
                }
            }
            this.Simulation.UnregisterConnection(this);
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override void Open()
    {
        this.state = ConnectionState.Open;
    }

    /// <inheritdoc/>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (this.CurrentTransaction is not null)
            throw new InvalidOperationException("SqlConnection does not support parallel transactions.");
        // Explicit iso level on BeginTransaction overrides the session-wide
        // default for the duration of this transaction; restored on
        // Commit/Rollback/Dispose. Unspecified keeps the existing session
        // value. Matches SqlClient's "the transaction inherits the session
        // iso unless an override is specified at BeginTransaction time"
        // behavior.
        var previousIsolation = this.SessionIsolationLevel;
        if (isolationLevel != IsolationLevel.Unspecified)
            this.SessionIsolationLevel = isolationLevel;
        var tx = new SimulatedDbTransaction(this.Simulation, this, isolationLevel)
        {
            PreviousSessionIsolationLevel = previousIsolation,
            OverrodeSessionIsolation = isolationLevel != IsolationLevel.Unspecified,
        };
        this.CurrentTransaction = tx;
        return tx;
    }

    /// <inheritdoc/>
    protected override DbCommand CreateDbCommand()
    {
        return new SimulatedDbCommand(this.Simulation, this);
    }

    /// <summary>Strongly-typed shadow over <see cref="DbConnection.CreateCommand"/>.</summary>
    public new SimulatedDbCommand CreateCommand() => new(this.Simulation, this);

    /// <summary>Strongly-typed shadow over <see cref="DbConnection.BeginTransaction()"/>.</summary>
    public new SimulatedDbTransaction BeginTransaction() => (SimulatedDbTransaction)base.BeginTransaction();

    /// <summary>Strongly-typed shadow over <see cref="DbConnection.BeginTransaction(IsolationLevel)"/>.</summary>
    public new SimulatedDbTransaction BeginTransaction(IsolationLevel isolationLevel) => (SimulatedDbTransaction)base.BeginTransaction(isolationLevel);
}
