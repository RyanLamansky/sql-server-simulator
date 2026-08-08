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
    /// Set once the abandoned-session sweep has torn this session down, so a
    /// second pass (or a late <see cref="SimulatedDbConnection.Dispose(bool)"/>
    /// on a resurrected connection) doesn't repeat the work.
    /// </summary>
    public bool Reclaimed;

    /// <summary>
    /// The connection this token belongs to, or <c>null</c> once it has been
    /// collected.
    /// </summary>
    public SimulatedDbConnection? TryResolveOwner() =>
        this.Owner is { } weak && weak.TryGetTarget(out var connection) ? connection : null;
}
