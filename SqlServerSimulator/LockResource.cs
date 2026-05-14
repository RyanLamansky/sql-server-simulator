namespace SqlServerSimulator;

/// <summary>
/// Lock modes recognized by <see cref="LockResource"/>. Phase 0 ships
/// schema-stability locks only — Sch-S (held during read / use of a
/// schema-bound object) and Sch-M (held during DDL on the object).
/// Phase 1+ adds the data-locking modes (S / X / U / IS / IX / SIX);
/// the enum is open-ended by design.
/// </summary>
internal enum LockMode
{
    /// <summary>Schema stability — multiple holders allowed; blocks Sch-M.</summary>
    SchemaStability,
    /// <summary>Schema modification — exclusive; blocks every other mode.</summary>
    SchemaModification,
}

/// <summary>
/// Per-schema-object reader/writer lock with re-entrant per-owner counting,
/// blocking wait + LOCK_TIMEOUT (Msg 1222), and same-thread-deadlock
/// detection (Msg 1205). One instance lives on each <see cref="SchemaObject"/>
/// (<see cref="SchemaObject.SchemaLock"/>) and on each
/// <see cref="Storage.HeapTable"/> instance (HeapTable inherits from
/// <see cref="SchemaObject"/>). Owners are <see cref="SimulatedDbConnection"/>
/// instances — same-connection re-entrance always succeeds; cross-connection
/// conflicts honor the standard SQL Server compatibility matrix.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency primitive: a single <see cref="object"/> gate per resource,
/// guarding the holder list and waiter signal. Acquirers enter the gate,
/// check compatibility, either record their hold or <see cref="Monitor.Wait(object, int)"/>
/// on the gate. Releasers enter the gate, pop their hold, and
/// <see cref="Monitor.PulseAll(object)"/> so every waiter re-checks. This is
/// coarse — every waiter wakes on every release — but works at the scale of
/// "a handful of connections per Simulation" the simulator targets. A
/// per-mode signal could refine it later if contention becomes a real
/// problem.
/// </para>
/// <para>
/// Same-thread deadlock detection: when an acquire would block, the
/// holder list is scanned — if any conflicting holder's
/// <see cref="SimulatedDbConnection.CurrentExecutingThreadId"/> equals the
/// caller's current managed thread id, the caller raises Msg 1205
/// immediately. The reasoning: one OS thread can only execute one command
/// at a time, so a conflicting holder on this thread cannot release without
/// the caller releasing the thread first — that's a liveness deadlock,
/// distinct from a textbook waiter-graph cycle.
/// </para>
/// <para>
/// Cross-thread cycle detection isn't modeled in phase 0 — Sch-S / Sch-M
/// alone, given the simulator's per-statement acquisition pattern, can't
/// form a cycle (Sch-S is released at statement end; a connection can't
/// hold Sch-S across statements and then need Sch-M on another resource
/// the symmetric peer holds). Cross-thread cycles emerge with data X
/// locks held across transactions (phase 1a+); the detector will land
/// there.
/// </para>
/// </remarks>
internal sealed class LockResource
{
    private readonly object gate = new();
    private readonly List<Hold> holders = [];

    /// <summary>
    /// Acquires <paramref name="mode"/> for <paramref name="owner"/>,
    /// blocking up to <paramref name="timeoutMillis"/> if the request
    /// conflicts with existing holders. <c>timeoutMillis</c> follows the
    /// SQL Server convention: <c>-1</c> = wait indefinitely (the default),
    /// <c>0</c> = fail-fast on first conflict (no wait), positive N = wait
    /// up to N milliseconds. Same-connection re-acquire is always compatible
    /// (the existing hold's count increments). When a conflicting holder
    /// shares the caller's current managed thread, the wait is short-circuited
    /// to an immediate Msg 1205 — that thread can't release without the
    /// caller releasing it first.
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 1205 (deadlock) if a conflicting holder is on the same thread;
    /// Msg 1222 (lock timeout) if the wait elapses without grant.
    /// </exception>
    public void Acquire(LockMode mode, SimulatedDbConnection owner, int timeoutMillis)
    {
        lock (this.gate)
        {
            // Same-owner re-entrance: matching mode bumps the existing hold's
            // count; otherwise append a fresh hold (Sch-S → Sch-M upgrade by
            // the same connection is allowed since the owner already has
            // unique access to the resource, no other holders to wait for).
            for (var i = 0; i < this.holders.Count; i++)
            {
                if (ReferenceEquals(this.holders[i].Owner, owner) && this.holders[i].Mode == mode)
                {
                    var hold = this.holders[i];
                    hold.Count++;
                    this.holders[i] = hold;
                    return;
                }
            }

            // Deadline = now + timeout (TickCount64 for monotonic ms);
            // <0 timeout means "no deadline / wait forever".
            var deadline = timeoutMillis < 0 ? -1L : Environment.TickCount64 + timeoutMillis;

            while (true)
            {
                if (TryGrant(mode, owner))
                    return;

                // Same-thread conflict → immediate Msg 1205; this thread is
                // the executor for both the caller AND a conflicting holder,
                // so the holder can't release until the caller does.
                if (IsConflictingHolderOnSameThread(mode, owner))
                    throw SimulatedSqlException.TransactionDeadlocked(owner.Spid);

                // Timeout==0 = fail-fast on first conflict (probe-confirmed
                // against SQL Server 2025: SET LOCK_TIMEOUT 0 raises Msg 1222
                // within milliseconds of the first conflict, no grace period).
                if (timeoutMillis == 0)
                    throw SimulatedSqlException.LockRequestTimeOutExceeded();

                var remaining = deadline < 0 ? Timeout.Infinite : (int)Math.Max(0, deadline - Environment.TickCount64);
                if (timeoutMillis > 0 && remaining == 0)
                    throw SimulatedSqlException.LockRequestTimeOutExceeded();
                if (!Monitor.Wait(this.gate, remaining))
                    throw SimulatedSqlException.LockRequestTimeOutExceeded();
                // Pulse received — loop and retry compatibility.
            }
        }
    }

    /// <summary>
    /// Releases one acquisition of <paramref name="mode"/> by
    /// <paramref name="owner"/>. Re-entrant acquires must match release
    /// 1-for-1; the final release of a mode pops the entry and wakes
    /// every waiter on this resource (each re-checks compatibility under
    /// the gate). Calling release without a matching acquire is a
    /// programming error — the helper throws to surface the bug rather
    /// than silently no-op.
    /// </summary>
    public void Release(LockMode mode, SimulatedDbConnection owner)
    {
        lock (this.gate)
        {
            for (var i = 0; i < this.holders.Count; i++)
            {
                if (ReferenceEquals(this.holders[i].Owner, owner) && this.holders[i].Mode == mode)
                {
                    var hold = this.holders[i];
                    hold.Count--;
                    if (hold.Count == 0)
                    {
                        this.holders.RemoveAt(i);
                        Monitor.PulseAll(this.gate);
                    }
                    else
                    {
                        this.holders[i] = hold;
                    }
                    return;
                }
            }
            throw new InvalidOperationException(
                $"LockResource.Release called without a matching Acquire (owner SPID {owner.Spid}, mode {mode}).");
        }
    }

    /// <summary>
    /// True when <paramref name="mode"/> is compatible with every current
    /// holder (excluding any same-owner self-holds, which are trivially
    /// compatible and handled separately on the acquire path). Inlines the
    /// phase-0 compatibility matrix (Sch-S × Sch-S = ok, everything else
    /// involving Sch-M = conflict).
    /// </summary>
    private bool TryGrant(LockMode mode, SimulatedDbConnection owner)
    {
        foreach (var hold in this.holders)
        {
            if (ReferenceEquals(hold.Owner, owner))
                continue; // same owner — already handled re-entrance above
            if (!IsCompatible(hold.Mode, mode))
                return false;
        }
        this.holders.Add(new Hold(owner, mode, 1));
        return true;
    }

    /// <summary>
    /// True when at least one conflicting holder is currently executing on
    /// the caller's managed thread (same SPID as the caller is impossible —
    /// the caller would have hit the re-entrance branch). Used to short-
    /// circuit the wait when no progress is possible: the caller's thread
    /// can't release while it's the one trying to acquire.
    /// </summary>
    private bool IsConflictingHolderOnSameThread(LockMode mode, SimulatedDbConnection owner)
    {
        var myThread = Environment.CurrentManagedThreadId;
        foreach (var hold in this.holders)
        {
            if (ReferenceEquals(hold.Owner, owner))
                continue;
            if (IsCompatible(hold.Mode, mode))
                continue;
            if (hold.Owner.CurrentExecutingThreadId == myThread)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Static compatibility matrix for the phase-0 mode set. Sch-S × Sch-S
    /// is the only compatible pair; anything touching Sch-M conflicts.
    /// </summary>
    private static bool IsCompatible(LockMode held, LockMode requested) =>
        held == LockMode.SchemaStability && requested == LockMode.SchemaStability;

    /// <summary>
    /// One owner's hold on this resource, with re-entrance count. Stored as
    /// a struct in the holders list; same-owner / same-mode re-acquires
    /// bump <see cref="Count"/> instead of appending a second entry, keeping
    /// the holders list compact.
    /// </summary>
    private struct Hold(SimulatedDbConnection owner, LockMode mode, int count)
    {
        public readonly SimulatedDbConnection Owner = owner;
        public readonly LockMode Mode = mode;
        public int Count = count;
    }
}
