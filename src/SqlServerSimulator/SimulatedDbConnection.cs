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
    /// The session's security identity: original login, base database
    /// principal, and impersonation stack. Defaults to the dbo-everywhere
    /// identity (<see cref="SessionSecurityContext.CreateDefault"/>), so an
    /// unauthenticated in-process connection is indistinguishable from today.
    /// Restamped at <see cref="Open"/> when the connection string carries a
    /// <c>User ID</c>, and by the TDS endpoint from the validated login;
    /// mutated by <c>EXECUTE AS</c> / <c>REVERT</c> and module
    /// <c>WITH EXECUTE AS</c>. Read by the identity scalars.
    /// </summary>
    internal SessionSecurityContext Security = SessionSecurityContext.CreateDefault();

    /// <summary>
    /// Picks the database a fresh connection points its
    /// <see cref="CurrentDatabase"/> at. Three-tier resolution:
    /// <list type="number">
    /// <item>The conventional default (<see cref="Simulation.DefaultDatabaseName"/>)
    /// if present — preserves the all-T-SQL "fresh Simulation just works"
    /// path.</item>
    /// <item>Otherwise pick the alphabetically-first user database — predictable
    /// fallback for the multi-import scenario, matching the ordering
    /// <c>sys.databases</c> uses. The always-present system databases
    /// (master / tempdb / model / msdb) are excluded from this pick so a fresh
    /// connection never lands on one by default. Pending real
    /// <c>USE &lt;db&gt;</c> support, the user can still inspect any database
    /// via catalog views regardless of which one a connection's CurrentDatabase
    /// happens to be pointed at.</item>
    /// <item>When no user database exists (only the seeded system databases),
    /// lazily seed the default — the first connection pays the cost of
    /// materializing the default when no import preceded it.</item>
    /// </list>
    /// </summary>
    private static Database ResolveInitialDatabase(Simulation simulation)
    {
        lock (simulation.Databases)
        {
            if (simulation.Databases.TryGetValue(Simulation.DefaultDatabaseName, out var existing))
                return existing;
            foreach (var kvp in simulation.Databases.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!Simulation.SystemDatabaseNames.Contains(kvp.Key))
                    return kvp.Value;
            }
            var seeded = new Database(Simulation.DefaultDatabaseName, simulation.ServerCollation);
            // Already inside lock (simulation.Databases) — use the
            // lock-held registration so the seed gets a stored database_id
            // (the smallest free id ≥ 5, normally 5 on a fresh simulation).
            simulation.RegisterUserDatabaseLocked(seeded);
            return seeded;
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
    /// Session-scoped <c>SET TEXTSIZE</c> state: the byte cap applied to
    /// MAX-typed and legacy-LOB values (<c>varchar/nvarchar/varbinary(max)</c>,
    /// <c>text</c>/<c>ntext</c>/<c>image</c>) as they leave the server for the
    /// client — result columns and output parameters; never server-side
    /// computation, variable assignment, or stored data. <c>-1</c> (the
    /// fresh-session default a SqlClient login establishes, and the only
    /// negative preserved verbatim) means unlimited; <c>SET TEXTSIZE 0</c> and
    /// every other negative collapse to 4096. All probe-confirmed against SQL
    /// Server 2025 (2026-07-19). Read by <c>@@TEXTSIZE</c> and stamped onto
    /// each result set at statement production.
    /// </summary>
    internal int TextSize = -1;

    /// <summary>
    /// Session-scoped <c>SET FMTONLY</c> state. While <see langword="true"/>,
    /// a SELECT returns its result-set metadata (COLMETADATA) with zero rows
    /// and every data-modifying statement is suppressed (no side effects) —
    /// the deprecated metadata-discovery mode SqlClient's <c>SqlBulkCopy</c>
    /// still uses to shape the destination-table metadata batch
    /// (<c>SET FMTONLY ON select * from dest SET FMTONLY OFF</c>).
    /// Probe-confirmed against SQL Server 2025 (2026-07-18): a FMTONLY-wrapped
    /// INSERT persists no rows. Toggled by top-level <c>SET FMTONLY ON|OFF</c>.
    /// </summary>
    internal bool FmtOnly;

    /// <summary>
    /// Session-scoped <c>NOCOUNT</c> setting: while <see langword="true"/>
    /// (toggled by top-level <c>SET NOCOUNT ON|OFF</c>, default off) a
    /// statement's rows-affected count is suppressed — the TDS DONE token omits
    /// the <c>DONE_COUNT</c> flag. Load-bearing for the ubiquitous
    /// <c>SET NOCOUNT ON; INSERT …; SELECT SCOPE_IDENTITY()</c> identity-retrieval
    /// pattern (mssql-django, and any ODBC/pyodbc data layer): without the count
    /// suppressed the driver stalls on the INSERT's rowcount result instead of
    /// advancing to the trailing SELECT (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal bool NoCount;

    /// <summary>
    /// Session-scoped <c>QUOTED_IDENTIFIER</c> setting: <see langword="true"/>
    /// (the default, matching SqlClient connections — probe-confirmed
    /// <c>@@OPTIONS &amp; 256</c> is set on a fresh session) tokenizes
    /// <c>"…"</c> as a delimited identifier; <see langword="false"/> as a
    /// varchar string literal. Mutated by top-level
    /// <c>SET QUOTED_IDENTIFIER</c> / <c>SET ANSI_DEFAULTS</c> — even from a
    /// never-taken conditional branch, because SQL Server applies the option
    /// at parse time (probe-confirmed). SETs inside dynamic SQL or a
    /// procedure body do NOT write here (dynamic SQL scopes the change to
    /// its own batch; procedure bodies ignore it entirely).
    /// </summary>
    internal bool QuotedIdentifiers = true;

    /// <summary>
    /// Session-scoped <c>ANSI_NULLS</c> setting, surfaced by
    /// <c>SESSIONPROPERTY('ANSI_NULLS')</c>. Defaults to <see langword="true"/>
    /// (a fresh SqlClient session reports 1 — probe-confirmed). The simulator
    /// doesn't model the <c>= NULL</c>-comparison semantic this option governs;
    /// the field exists so the option's recorded state reads back consistently.
    /// Mutated by top-level <c>SET ANSI_NULLS ON|OFF</c> (including the comma-list
    /// form); like <c>QUOTED_IDENTIFIER</c>, SETs inside a procedure / function /
    /// trigger body or dynamic SQL don't write here.
    /// </summary>
    internal bool AnsiNulls = true;

    /// <summary>
    /// Session-scoped <c>ANSI_PADDING</c> setting (default <see langword="true"/>),
    /// surfaced by <c>SESSIONPROPERTY('ANSI_PADDING')</c>. Parse-and-discard for
    /// storage semantics; recorded so the option reads back. Scoping mirrors
    /// <see cref="AnsiNulls"/>.
    /// </summary>
    internal bool AnsiPadding = true;

    /// <summary>
    /// Session-scoped <c>ANSI_WARNINGS</c> setting (default <see langword="true"/>),
    /// surfaced by <c>SESSIONPROPERTY('ANSI_WARNINGS')</c>. Recorded only.
    /// Scoping mirrors <see cref="AnsiNulls"/>.
    /// </summary>
    internal bool AnsiWarnings = true;

    /// <summary>
    /// Session-scoped <c>ARITHABORT</c> setting. Defaults to
    /// <see langword="false"/> — a fresh SqlClient session reports 0
    /// (probe-confirmed), the one option of this family that defaults off.
    /// Surfaced by <c>SESSIONPROPERTY('ARITHABORT')</c>; recorded only.
    /// Scoping mirrors <see cref="AnsiNulls"/>.
    /// </summary>
    internal bool Arithabort;

    /// <summary>
    /// Session-scoped <c>CONCAT_NULL_YIELDS_NULL</c> setting (default
    /// <see langword="true"/>), surfaced by
    /// <c>SESSIONPROPERTY('CONCAT_NULL_YIELDS_NULL')</c>. Recorded only.
    /// Scoping mirrors <see cref="AnsiNulls"/>.
    /// </summary>
    internal bool ConcatNullYieldsNull = true;

    /// <summary>
    /// Session-scoped <c>NUMERIC_ROUNDABORT</c> setting (default
    /// <see langword="false"/>), surfaced by
    /// <c>SESSIONPROPERTY('NUMERIC_ROUNDABORT')</c>. Recorded only.
    /// Scoping mirrors <see cref="AnsiNulls"/>.
    /// </summary>
    internal bool NumericRoundabort;

    /// <summary>
    /// Session-scoped <c>XACT_ABORT</c> setting (default
    /// <see langword="false"/>). Load-bearing for one behavior: when a
    /// running command is cancelled by a client attention (or an
    /// <c>ExecuteReader</c> caller's <c>Cancel()</c>) while a transaction is
    /// open, <c>XACT_ABORT ON</c> rolls that transaction back, whereas
    /// <c>OFF</c> leaves it open and usable — probe-confirmed against SQL
    /// Server 2025. Otherwise recorded only (the broader XACT_ABORT
    /// error-abort semantics remain parse-and-discard). Scoping mirrors
    /// <see cref="AnsiNulls"/>.
    /// </summary>
    internal bool XactAbort;

    /// <summary>
    /// The cancellation source for the command currently executing on this
    /// connection, replaced at the start of each top-level command execution
    /// (<see cref="Simulation.CreateResultSetsForCommand"/>). The engine polls
    /// its token at statement boundaries and inside <c>WAITFOR DELAY</c>; the
    /// TDS endpoint's attention watcher and <see cref="SimulatedDbCommand.Cancel"/>
    /// trigger it. Connection-scoped rather than command-scoped so a proc /
    /// UDF / dynamic-SQL body (which shares the connection but wraps a fresh
    /// body command) inherits the same cancellation signal. Only one command
    /// runs at a time per connection (the simulator has no MARS), so a single
    /// source suffices.
    /// </summary>
    private CancellationTokenSource executionCancellation = new();

    /// <summary>
    /// Begins a fresh cancellation scope for one top-level command execution,
    /// discarding any prior (possibly already-cancelled) scope. Called at the
    /// top of <see cref="Simulation.CreateResultSetsForCommand"/> so both the
    /// in-process ADO surface and the TDS wire path get a clean token per
    /// execution — a cancel that fired against a previous command doesn't
    /// bleed into the next one on the same connection.
    /// </summary>
    internal void BeginExecutionScope(TimeSpan? timeout = null)
    {
        var fresh = new CancellationTokenSource();
        // A finite CommandTimeout arms the same source the engine already
        // polls, so a timeout aborts through exactly the path a Cancel() or a
        // TDS attention does — no second signal to coordinate. The cause is
        // recovered from executionCancelledByUser rather than a linked source:
        // whoever cancelled sets the flag, so an unflagged cancellation is the
        // timer's. (A cancel racing the deadline reports as a cancel; real has
        // the same inherent ambiguity.)
        if (timeout is { } span)
            fresh.CancelAfter(span);
        this.executionCancelledByUser = false;
        var previous = Interlocked.Exchange(ref this.executionCancellation, fresh);
        previous.Dispose();
    }

    /// <summary>
    /// Set by <see cref="CancelExecution"/> so a cancelled execution can tell a
    /// user / attention cancel (Msg 0) from a <c>CommandTimeout</c> expiry
    /// (Msg -2). Cleared when each execution scope opens.
    /// </summary>
    private volatile bool executionCancelledByUser;

    /// <summary>
    /// True when the current execution was aborted by its <c>CommandTimeout</c>
    /// rather than by a <see cref="CancelExecution"/> caller — the discriminator
    /// between the Msg -2 and Msg 0 surfaces.
    /// </summary>
    internal bool ExecutionTimedOut =>
        Volatile.Read(ref this.executionCancellation).IsCancellationRequested && !this.executionCancelledByUser;

    /// <summary>The current execution's cancellation token; the engine's safe-point poll target.</summary>
    internal CancellationToken ExecutionCancellationToken => Volatile.Read(ref this.executionCancellation).Token;

    /// <summary>
    /// Requests cancellation of the command currently executing on this
    /// connection. Safe to call from any thread (the TDS attention watcher, an
    /// <c>ExecuteReader</c> caller's <c>Cancel()</c>): the engine observes the
    /// cancellation at its next safe point and aborts the batch.
    /// </summary>
    internal void CancelExecution()
    {
        try
        {
            this.executionCancelledByUser = true;
            Volatile.Read(ref this.executionCancellation).Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with BeginExecutionScope disposing the prior source; the
            // cancel targeted a command that already finished, so dropping it
            // is correct.
        }
    }

    /// <summary>
    /// UTC instant this connection object was constructed, surfaced as
    /// <c>sys.dm_exec_sessions.login_time</c> (and its
    /// <c>last_request_start_time</c> / <c>last_request_end_time</c>
    /// placeholders). Construction is the closest simulator analogue to a
    /// login handshake — the TDS endpoint constructs one per authenticated
    /// session, and in-process consumers construct via
    /// <c>CreateDbConnection()</c>.
    /// </summary>
    internal readonly DateTime LoginTimeUtc = DateTime.UtcNow;

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
    /// close. Global temp tables (<c>##foo</c>) are modeled separately on
    /// <see cref="Simulation.GlobalTempTables"/> (instance-wide).
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
    /// True while an <c>INSERT … EXEC</c> is draining its source EXEC's
    /// result sets on this connection. An <c>INSERT … EXEC</c> encountered
    /// while this is set (i.e. inside the executed procedure / dynamic
    /// batch) raises <strong>Msg 8164</strong> "An INSERT EXEC statement
    /// cannot be nested." (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal bool InsertExecActive;

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
    /// The undo log of the statement that fired the trigger currently
    /// running on this connection, or <c>null</c> outside any trigger.
    /// A trigger body doesn't get an atomic scope of its own: real SQL Server
    /// rolls back the firing statement and everything its triggers wrote as a
    /// single unit, so mutations underneath a trigger join this log instead of
    /// taking a throwaway one they'd commit on their own success
    /// (probe-confirmed — an audit-log INSERT in a body whose later statement
    /// throws does not survive, and neither does one written by a stored
    /// procedure the body called).
    /// Session-scoped rather than per-<see cref="Parser.BatchContext"/>
    /// precisely because it has to reach those nested modules, each of which
    /// runs in a child batch of its own.
    /// Only consulted in auto-commit: under an explicit transaction every
    /// statement already shares <c>SimulatedDbTransaction.UndoLog</c>, and the
    /// firing statement's marker covers the trigger's writes.
    /// </summary>
    internal Storage.UndoLog? TriggerStatementUndoLog;

    /// <summary>
    /// The pending-version list of the statement that fired the currently
    /// running trigger, paired with <see cref="TriggerStatementUndoLog"/> so
    /// MVCC row versions created underneath a trigger are finalized or
    /// discarded with the firing statement rather than on their own.
    /// </summary>
    internal List<Storage.PendingVersionEntry>? TriggerStatementVersionEntries;

    /// <summary>
    /// Set when an error of severity 11 or higher was raised while the
    /// currently running trigger body executed and a <c>TRY</c> / <c>CATCH</c>
    /// swallowed it. The trigger dispatcher reads it when the body returns and
    /// raises Msg 3616, because real aborts the batch and rolls the unit back
    /// regardless of the body having handled the error itself
    /// (probe-confirmed). Saved and cleared per body so a handled error in one
    /// trigger doesn't condemn the next.
    /// Connection-scoped so an error caught inside a stored procedure the body
    /// called still counts, matching real.
    /// </summary>
    internal bool TriggerBodyErrorRaised;

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
    /// T-SQL cursors declared on this session, keyed case-insensitively by
    /// name (cursor names are identifiers, not <c>@</c>-prefixed). Populated
    /// by <c>DECLARE … CURSOR</c>, removed by <c>DEALLOCATE</c>; cleared on
    /// <see cref="Dispose"/> (cursors are session-scoped). The simulator
    /// collapses SQL Server's GLOBAL/LOCAL cursor-scope distinction into this
    /// single per-connection map.
    /// </summary>
    internal readonly Dictionary<string, Cursor> Cursors = new(BuiltInToken.Comparer);

    /// <summary>
    /// Backs <c>@@FETCH_STATUS</c>: the status of the most recent <c>FETCH</c>
    /// on this connection (0 success, -1 past end / no row, -2 keyset member
    /// deleted). Session-global across all cursors, matching SQL Server.
    /// </summary>
    internal int LastFetchStatus;

    /// <summary>
    /// Backs <c>@@CURSOR_ROWS</c>: row count of the most recently OPENed cursor
    /// (count for STATIC / KEYSET, <c>-1</c> for DYNAMIC). Real SQL Server may
    /// transiently report a positive count for a freshly-opened dynamic cursor
    /// (asynchronous population); the simulator reports <c>-1</c> throughout.
    /// </summary>
    internal int LastCursorRows;

    /// <summary>
    /// Name of the table currently under <c>SET IDENTITY_INSERT ... ON</c>
    /// for this connection, or <c>null</c> when no table is in that mode.
    /// SQL Server allows only one table at a time per session; the simulator
    /// enforces the same per connection.
    /// </summary>
    internal string? IdentityInsertTable;

    /// <summary>
    /// Per-session key/value store backing <c>SESSION_CONTEXT(key)</c> and
    /// <c>sp_set_session_context</c>. Keys are case-sensitive (<see cref="StringComparer.Ordinal"/>),
    /// matching SQL Server's binary key comparison regardless of database
    /// collation (probe-confirmed: a key set as <c>TenantId</c> isn't readable
    /// as <c>tenantid</c>). Each entry carries the stored value (type-preserved,
    /// though <c>SESSION_CONTEXT</c> surfaces it as nvarchar since the simulator
    /// has no <c>sql_variant</c>) and whether it was set <c>@read_only = 1</c>
    /// — a read-only key rejects further writes with Msg 15664. Session-scoped:
    /// lives for the connection's lifetime, persisting across batches.
    /// </summary>
    internal readonly Dictionary<string, (SqlValue Value, bool ReadOnly)> SessionContext = new(StringComparer.Ordinal);

    /// <summary>
    /// Backs <c>CONTEXT_INFO()</c> / <c>SET CONTEXT_INFO</c>. Null until the
    /// first <c>SET CONTEXT_INFO</c>; once set, a 128-byte buffer (SQL Server
    /// right-pads or truncates the supplied binary to exactly 128 bytes, so
    /// <c>DATALENGTH(CONTEXT_INFO())</c> is always 128 after a set).
    /// Session-scoped.
    /// </summary>
    internal byte[]? ContextInfo;

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

    private string connectionString = "";
    private string? pendingUserId;
    private string? pendingPassword;
    private string? pendingInitialCatalog;

    /// <inheritdoc/>
    [AllowNull]
    public override string ConnectionString
    {
        get => this.connectionString;
        set
        {
            // SqlConnection forbids mutating the connection string on an open
            // connection; mirror that. The connection is bound to its
            // Simulation at creation, so Data Source / Server are ignored — the
            // parser recognizes only the credential + database keywords that
            // carry meaning in-process.
            if (this.state == ConnectionState.Open)
                throw new InvalidOperationException("Not allowed to change the 'ConnectionString' property. The connection's current state is open.");
            this.ParseConnectionString(value);
        }
    }

    /// <summary>
    /// Minimal <c>SqlConnectionStringBuilder</c>-style parser: splits on
    /// <c>;</c>, matches keys case-insensitively, and captures the credential +
    /// initial-catalog keywords that carry in-process meaning
    /// (<c>User ID</c>/<c>UID</c>, <c>Password</c>/<c>PWD</c>,
    /// <c>Initial Catalog</c>/<c>Database</c>). Every other keyword (Server,
    /// Encrypt, Pooling, …) is accepted and ignored — the connection is already
    /// bound to its Simulation. Values may be wrapped in matching single or
    /// double quotes.
    /// </summary>
    private void ParseConnectionString(string? value)
    {
        this.connectionString = value ?? "";
        this.pendingUserId = null;
        this.pendingPassword = null;
        this.pendingInitialCatalog = null;
        if (string.IsNullOrEmpty(value))
            return;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
                continue;
            var key = part[..equals].Trim();
            var raw = part[(equals + 1)..].Trim();
            var unquoted = raw.Length >= 2 && ((raw[0] == '\'' && raw[^1] == '\'') || (raw[0] == '"' && raw[^1] == '"'))
                ? raw[1..^1]
                : raw;
            if (KeyMatches(key, "User ID", "UID"))
                this.pendingUserId = unquoted;
            else if (KeyMatches(key, "Password", "PWD"))
                this.pendingPassword = unquoted;
            else if (KeyMatches(key, "Initial Catalog", "Database"))
                this.pendingInitialCatalog = unquoted;
        }
    }

    private static bool KeyMatches(string key, string canonical, string alias) =>
        key.Equals(canonical, StringComparison.OrdinalIgnoreCase)
        || key.Equals(alias, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string Database => this.CurrentDatabase.Name;

    /// <summary>
    /// The fixed data-source name reported by <see cref="DataSource"/>.
    /// Real <c>SqlException.Server</c> / <c>SqlError.Server</c> carry the
    /// connection's data source (probe-confirmed against SQL Server 2025 —
    /// <c>ex.Server</c> reports <c>localhost,1433</c>, the connect target, not
    /// the server's <c>@@SERVERNAME</c>), so simulated errors stamp this value
    /// as their <see cref="SimulatedError.Server"/>. Distinct from the TDS
    /// ERROR-token server field, which carries the server's own name
    /// (<c>SIMULATED</c>, matching <c>SERVERPROPERTY('ServerName')</c>).
    /// </summary>
    internal const string DataSourceName = "simulator";

    /// <inheritdoc/>
    public override string DataSource => DataSourceName;

    /// <inheritdoc/>
    public override string ServerVersion => ReferenceBuild.MajorMinorBuild;

    private ConnectionState state;

    /// <inheritdoc/>
    public override ConnectionState State => this.state;

    /// <summary>
    /// Switches <see cref="CurrentDatabase"/> to the named database, the
    /// ADO.NET equivalent of issuing <c>USE &lt;db&gt;</c> on this connection.
    /// A missing database raises Msg 911 (the same error the <c>USE</c> path
    /// reports); a null/empty/whitespace name raises <see cref="ArgumentException"/>,
    /// matching SqlClient. Like <c>USE</c>, the switch is not transactional.
    /// </summary>
    public override void ChangeDatabase(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Database cannot be null, the empty string, or string of only whitespace.", nameof(databaseName));

        // A restricted principal (an impersonated non-dbo user, or an
        // authenticated login mapped to a non-dbo user) can't cross databases —
        // Msg 916, session stays put. A dbo / sa session (the in-process
        // default) keeps today's unrestricted switch.
        if (!this.Security.EffectiveIsDbo)
            throw SimulatedSqlException.CannotAccessDatabaseUnderSecurityContext(this.Security.Effective.LoginName, databaseName);

        if (!this.Simulation.Databases.TryGetValue(databaseName, out var target))
            throw SimulatedSqlException.DatabaseDoesNotExist(databaseName);

        this.CurrentDatabase = target;
    }

    /// <summary>
    /// Session-owned application locks (<c>sp_getapplock @LockOwner =
    /// 'Session'</c>), one entry per successful acquire (probe-confirmed
    /// reference counting: N acquires need N releases). Released in bulk at
    /// <see cref="Close"/> / <see cref="Dispose(bool)"/> — probe-confirmed
    /// that session-owned locks release when the session ends, surviving any
    /// number of intervening transactions. Transaction-owned locks live on
    /// <c>SimulatedDbTransaction.TransactionAppLocks</c> instead.
    /// </summary>
    internal readonly List<AppLockHold> SessionAppLocks = [];

    /// <summary>
    /// Releases every session-owned application lock. Idempotent — the list
    /// clears — so the <see cref="Close"/>-then-<see cref="Dispose(bool)"/>
    /// sequence releases once.
    /// </summary>
    private void ReleaseSessionAppLocks()
    {
        var manager = this.Simulation.LockManager;
        for (var i = this.SessionAppLocks.Count - 1; i >= 0; i--)
            manager.Release(this.SessionAppLocks[i].LockResource, this.SessionAppLocks[i].Mode, this);
        this.SessionAppLocks.Clear();
    }

    /// <inheritdoc/>
    public override void Close()
    {
        // SqlClient auto-rolls-back any active transaction when its
        // connection closes. The transaction's own dispose handles the
        // explicit using-pattern; this branch covers raw Close() without
        // disposing the transaction first.
        this.CurrentTransaction?.Rollback();
        this.ReleaseSessionAppLocks();
        this.state = ConnectionState.Closed;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.CurrentTransaction?.Dispose();
            this.executionCancellation.Dispose();
            this.ReleaseSessionAppLocks();
            // Local temp tables auto-drop at session close. Clearing the dict
            // releases each table's Heap and LOB pages for GC; nothing else
            // holds long-lived references to them after the connection ends.
            this.TempTables.Clear();
            // Cursors are session-scoped and auto-deallocate at close. Release
            // any SCROLL_LOCKS locks the open GLOBAL cursors still hold before
            // dropping them.
            foreach (var cursor in this.Cursors.Values)
                cursor.ReleaseScrollLocks(this);
            this.Cursors.Clear();
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
        // Connection-string authentication. A User ID validates against the
        // CREATE LOGIN registry (empty registry accepts anything, mirroring the
        // TDS endpoint) and stamps the session principal to the login's mapped
        // database user in the target database; failures raise the Msg 18456 /
        // 4060 shapes. No User ID keeps exactly today's behavior — the default
        // dbo identity — with an optional Initial Catalog switch.
        if (this.pendingUserId is { } userId)
            this.AuthenticateConnectionStringLogin(userId, this.pendingPassword ?? "", this.pendingInitialCatalog);
        else if (this.pendingInitialCatalog is { Length: > 0 } catalog)
            this.ChangeDatabase(catalog);
        this.state = ConnectionState.Open;
    }

    /// <summary>
    /// Validates a connection-string login against <see cref="Simulation.Logins"/>,
    /// resolves the requested (or default) database, maps the login to its
    /// database user there, and stamps <see cref="Security"/>. Mirrors the TDS
    /// endpoint's connect flow for the in-process front door.
    /// </summary>
    private void AuthenticateConnectionStringLogin(string userId, string password, string? initialCatalog)
    {
        if (!this.Simulation.ValidateLoginCredentials(userId, password))
            throw SimulatedSqlException.LoginFailed(userId);

        var target = this.CurrentDatabase;
        if (initialCatalog is { Length: > 0 })
        {
            if (!this.Simulation.Databases.TryGetValue(initialCatalog, out var requested))
                throw SimulatedSqlException.CannotOpenDatabaseRequestedByLogin(initialCatalog);
            target = requested;
        }

        if (!Simulation.TryMapLoginToDatabaseUser(this.Simulation, target, userId, out var principal))
            throw SimulatedSqlException.CannotOpenDatabaseRequestedByLogin(target.Name);

        this.CurrentDatabase = target;
        this.Security = Simulation.BuildAuthenticatedSecurityContext(principal, userId);
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
