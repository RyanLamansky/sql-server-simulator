using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One session's identity, as every <em>shared</em> structure refers to it.
/// A lock hold, a <c>##global</c> temp table's ownership stamp, an active
/// SNAPSHOT registration and the <see cref="Simulation"/>'s own session
/// registry all name the session through this object rather than through the
/// <see cref="SimulatedDbConnection"/> that opened it.
/// </summary>
/// <remarks>
/// <para>
/// The indirection is one-way on purpose. A connection references its token
/// strongly; the token's only path back is <see cref="Owner"/>, a
/// <em>weak</em> reference. That is what lets a connection an application
/// opened and then dropped without disposing become unreachable while the
/// state it left behind — a lock, an open transaction, a <c>##temp</c> — is
/// still recorded: the recording structures pin the token, which is a handful
/// of fields, instead of pinning the connection and everything it owns.
/// Real SqlClient's own finalizer eventually closes such a connection and the
/// server resets the session, so the alternative (holding the connection alive
/// forever, its locks with it) is the divergence worth removing.
/// </para>
/// <para>
/// It also carries the per-session state the lock manager reads about
/// <em>other</em> sessions — the wait edge and the executing thread — because
/// those are read while walking a resource's holder list, where only the token
/// is in hand.
/// </para>
/// <para>
/// <see cref="Owner"/> tracks resurrection so a connection sitting in
/// <see cref="Simulation"/>'s abandoned-session queue (already finalized,
/// revived by the queue's reference, not yet torn down) still resolves — which
/// is what keeps the <c>sp_who</c> family and <c>sys.dm_tran_locks</c>
/// reporting a session for exactly as long as its locks are held.
/// </para>
/// </remarks>
internal sealed class SessionToken(int spid)
{
    /// <inheritdoc cref="SimulatedDbConnection.Spid"/>
    public readonly int Spid = spid;

    /// <summary>
    /// Weak, resurrection-tracking reference to the connection this token
    /// belongs to. Assigned once, immediately after construction (the
    /// connection can't hand out <c>this</c> from a field initializer).
    /// Resolves for a live session; stops resolving once the connection has
    /// been collected, which is what the abandoned-session sweep reads as
    /// "this session's owner is gone".
    /// </summary>
    public WeakReference<SimulatedDbConnection>? Owner;

    /// <summary>
    /// Managed thread id currently executing a statement for this session, or
    /// <c>null</c> between statements. Written by the statement dispatcher on
    /// the session's own thread and read by
    /// <c>LockManager</c>'s same-thread deadlock short-circuit and by the
    /// <c>sp_who</c> status column.
    /// </summary>
    /// <remarks>
    /// A parallel-aggregate worker thread is <em>not</em> a session thread and
    /// never writes here: the fan-out happens inside one dispatched statement,
    /// so the field still names the thread that dispatched it. The
    /// abandoned-session sweep relies on that — it reads this field to tell a
    /// session that is mid-statement from one that is idle.
    /// </remarks>
    public int? CurrentExecutingThreadId;

    /// <summary>
    /// This owner's hold entries in a mode that can keep a reader waiting,
    /// against the lock manager's count of everyone's
    /// (<see cref="Storage.LockManager.BlockingHolds"/>). Kept under the lock
    /// manager's gate.
    /// </summary>
    public int BlockingHolds;

    /// <summary>
    /// The <see cref="Storage.LobReclamation"/> epoch the session's running
    /// outermost statement began at, or <see cref="long.MaxValue"/> between
    /// statements: no LOB chain retired after it is reused until it ends.
    /// </summary>
    public long StatementEpoch = long.MaxValue;

    /// <summary>
    /// The oldest snapshot a statement of the session's running outermost
    /// statement reads at — <c>READ_COMMITTED_SNAPSHOT</c>'s, an autocommit
    /// SNAPSHOT statement's, a memory-optimized table's outside a transaction
    /// — or <see cref="long.MaxValue"/> while none does. The version sweep
    /// keeps every version it may read.
    /// </summary>
    public long StatementSnapshotXid = long.MaxValue;

    /// <summary>
    /// The earliest <see cref="StatementEpoch"/> among the session's MARS
    /// requests parked mid-statement while another runs, each statement's
    /// own kept with its request (<see cref="ParkedStatement.Epoch"/>);
    /// <see cref="long.MaxValue"/> for none. Read beside
    /// <see cref="StatementEpoch"/> wherever that is.
    /// </summary>
    public long ParkedStatementEpoch = long.MaxValue;

    /// <summary>
    /// The oldest <see cref="StatementSnapshotXid"/> among the session's
    /// requests parked mid-statement (see <see cref="ParkedStatementEpoch"/>).
    /// </summary>
    public long ParkedStatementSnapshotXid = long.MaxValue;

    /// <summary>
    /// For a token a MARS request holds its locks under, set while the
    /// request last running under it is parked for another of its session's
    /// requests (<see cref="SimulatedDbConnection.ResumeRequest"/>): a lock
    /// it holds is then no lock of the session's running statement, so the
    /// same-thread deadlock check passes over it and a wait on it goes on
    /// until the parked request lets it go or a timeout ends it.
    /// </summary>
    public volatile bool RequestParked;

    /// <summary>
    /// Set on a token a MARS request holds its own statements' locks under
    /// beside its session's other requests (see
    /// <see cref="SessionRequest.LockOwner"/>): what the session's own token
    /// holds while no request is parked on it — a cursor's scroll locks, the
    /// session's application locks — is the request's own as well
    /// (<c>LockManager.SharesSessionScope</c>), so a positioned update meets
    /// its cursor's lock as before.
    /// </summary>
    public bool IsRequestOwner;

    /// <summary>
    /// Set on the token a session's cursors hold their scroll locks under
    /// (<see cref="SimulatedDbConnection.SessionScope"/>), which every request
    /// of the session shares, whichever token its statements lock under.
    /// </summary>
    public bool IsSessionScope;

    /// <summary>
    /// The <see cref="LockResource"/> this session is currently blocked on, or
    /// <c>null</c> when it isn't waiting. Set and cleared inside
    /// <c>LockManager</c>'s gate so the cycle detector reads a consistent
    /// wait-for graph.
    /// </summary>
    public LockResource? WaitingOnResource;

    /// <summary>
    /// The mode being waited for on <see cref="WaitingOnResource"/>, or
    /// <c>null</c> when not waiting.
    /// </summary>
    public LockMode? WaitingForMode;

    /// <summary>
    /// The key a uniqueness or foreign-key check waits on while it waits on
    /// <see cref="WaitingOnResource"/>, the lock of the row that carries the
    /// key: real waits on the key's own lock in that index, which is where
    /// the lock DMVs report the wait — on a heap, and for a unique
    /// nonclustered index, a resource other than the row's. Null otherwise.
    /// </summary>
    public string? WaitingOnKey;

    /// <summary>
    /// <c>Environment.TickCount64</c> when the session's current wait began —
    /// a blocked lock acquisition or a <c>WAITFOR</c> — read as
    /// <c>sys.dm_exec_requests.wait_time</c>. Meaningful only while waiting.
    /// </summary>
    public long WaitStartedTicks;

    /// <summary>
    /// The milliseconds the session has spent blocked on locks, all told —
    /// what Query Store takes as a statement's <c>Lock</c> wait and keeps out
    /// of its CPU time.
    /// </summary>
    public long LockWaitedMilliseconds;

    /// <summary>
    /// Set under <c>LockManager</c>'s gate when another session's request
    /// closed a deadlock cycle and this blocked session carries the lower
    /// <c>SET DEADLOCK_PRIORITY</c>: its wait ends with Msg 1205 instead of the
    /// requester's.
    /// </summary>
    public bool ChosenAsDeadlockVictim;

    /// <summary>
    /// How many stretches of execution of the session's requests are under
    /// way (<c>Simulation.CreateResultSetsForCommand</c>), which a session
    /// bound to the same transaction waits out before running.
    /// </summary>
    public int ExecutingStretches;

    /// <summary>Set while the session sleeps in a <c>WAITFOR</c>, which <c>sys.dm_exec_requests</c> reports as its wait.</summary>
    public bool InWaitFor;

    /// <summary>
    /// The id of the request whose <c>SELECT</c> waits on its client to read
    /// the rows it sent (see <see cref="ResultStream"/>), or -1 while none
    /// does: <c>sys.dm_exec_requests</c> reports it <c>suspended</c> on
    /// <c>ASYNC_NETWORK_IO</c> (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    public int AwaitingClientRequest = -1;

    /// <summary>
    /// The kind of the statement the session is running, as
    /// <c>sys.dm_exec_requests.command</c> reports it; written as each
    /// statement dispatches, so a nested body's statement names itself.
    /// </summary>
    public string CurrentCommand = "SELECT";

    /// <summary>When the session's in-flight command began, which <c>sys.dm_exec_requests.start_time</c> reports.</summary>
    public DateTime RequestStartUtc;

    /// <summary>
    /// The text of the session's current or most recent command, which its
    /// SQL handle hashes and <c>sys.dm_exec_sql_text</c> / the input buffer
    /// return; null before the session's first command.
    /// </summary>
    public string? BatchText;

    /// <summary>Where in <see cref="BatchText"/> the running top-level statement starts, in characters.</summary>
    public int StatementStartIndex;

    /// <summary>
    /// Set once the abandoned-session sweep has torn this session down, so a
    /// second pass (or a late <see cref="SimulatedDbConnection.Dispose(bool)"/>
    /// on a resurrected connection) doesn't repeat the work.
    /// </summary>
    public bool Reclaimed;

    /// <summary>
    /// For the lock owner the sessions bound to one transaction share
    /// (<see cref="SimulatedDbTransaction.LockOwner"/>), the member session
    /// running under it, or the one that ran last; null for a session's own
    /// token. Only one member runs at a time, so the wait this token records
    /// is that member's, and real reports the transaction's locks under the
    /// member that last ran (probed 2026-10-07 against SQL Server 2025). For
    /// a token one of a MARS session's requests holds its locks under, the
    /// session itself (see <see cref="SimulatedDbConnection.LockOwner"/>).
    /// </summary>
    public SessionToken? RunningMember;

    /// <summary>
    /// The session acting for this token: itself, or for a shared lock owner
    /// the member running under it — whose <c>@@SPID</c> the lock DMVs report,
    /// whose command a wait observes, and whose thread is executing.
    /// </summary>
    public SessionToken Acting => this.RunningMember ?? this;

    /// <summary>
    /// The connection this token belongs to, or <c>null</c> once it has been
    /// collected.
    /// </summary>
    public SimulatedDbConnection? TryResolveOwner() =>
        this.Owner is { } weak && weak.TryGetTarget(out var connection) ? connection : null;

    /// <summary>
    /// The connection of the session <see cref="Acting"/> for this token —
    /// whose command a lock wait observes and whose transaction a hold
    /// belongs to — or <c>null</c> once it has been collected.
    /// </summary>
    public SimulatedDbConnection? TryResolveActing() => this.Acting.TryResolveOwner();
}
