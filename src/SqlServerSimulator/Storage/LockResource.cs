using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Lock modes recognized by <see cref="LockManager"/>. Four orthogonal
/// families: schema-stability locks (Sch-S / Sch-M) protect against
/// concurrent DDL on an object; data locks (Shared / Update / Exclusive)
/// protect against concurrent DML reads / writes at the row level (and at
/// the table level when explicit TABLOCK / TABLOCKX is in play); intent
/// locks (IS / IX / SIX) sit at the table level to signal "some children
/// of this object are S- / U- / X-locked" so a TABLOCK / TABLOCKX
/// requester at the parent can quickly check for child conflicts without
/// scanning the row-lock dict; key-range locks (the four Range* modes)
/// sit on a <see cref="KeyLockGroup"/> anchor and fence the gap below it
/// against the inserts and key-changing updates a SERIALIZABLE reader must
/// not see. The compatibility matrix in
/// <see cref="LockManager.IsCompatible"/> spells out the relationships.
/// </summary>
internal enum LockMode
{
    /// <summary>Schema stability — multiple holders allowed; blocks Sch-M.</summary>
    SchemaStability,
    /// <summary>Schema modification — exclusive against every other mode.</summary>
    SchemaModification,
    /// <summary>Intent-shared (IS) — table-level signal that some row-S is held by this owner.</summary>
    IntentShared,
    /// <summary>Intent-exclusive (IX) — table-level signal that some row-X is held by this owner.</summary>
    IntentExclusive,
    /// <summary>Shared-with-intent-exclusive (SIX) — full table read + intent to write some rows.</summary>
    SharedIntentExclusive,
    /// <summary>Data shared (S) — non-exclusive read. Multiple S holders allowed; coexists with U.</summary>
    Shared,
    /// <summary>Data update (U) — "I'm reading but plan to convert to X". One U at a time per resource; coexists with S and IS.</summary>
    Update,
    /// <summary>Data exclusive (X) — exclusive against every other data-family mode.</summary>
    Exclusive,

    /// <summary>
    /// Key-range shared (RangeS-S) — a SERIALIZABLE / HOLDLOCK reader's hold
    /// on a key and the gap below it. Coexists with another reader's RangeS-S
    /// and with RangeS-U; blocks a writer inserting into the gap below the key.
    /// </summary>
    RangeSharedShared,

    /// <summary>
    /// Key-range update (RangeS-U) — a SERIALIZABLE / HOLDLOCK reader's hold
    /// taken with intent to write, which is what <c>UPDLOCK</c> alongside
    /// either fences its keys in. Shares with RangeS-S, conflicts with a
    /// second RangeS-U.
    /// </summary>
    RangeSharedUpdate,

    /// <summary>
    /// Key-range exclusive (RangeX-X) — exclusive against every other range
    /// mode. What <c>XLOCK</c> under SERIALIZABLE / HOLDLOCK, and a
    /// SERIALIZABLE UPDATE / DELETE, fences its keys in.
    /// </summary>
    RangeExclusiveExclusive,

    /// <summary>
    /// Key-range insert (RangeI-N) — the instant-duration mode a writer takes
    /// on the next key above the one it inserts, testing whether the gap its
    /// row lands in is range-locked. Two writers probing the same gap don't
    /// block each other; a held RangeS-S / RangeS-U / RangeX-X does block
    /// them.
    /// </summary>
    RangeInsertNull,
}

/// <summary>
/// Passive per-object lock state. Holds the current set of acquisitions
/// (each a <see cref="Hold"/> entry with owner / mode / re-entrance
/// count). Every <see cref="SchemaObject"/> carries one via the inherited
/// <see cref="SchemaObject.SchemaLock"/>; row-level locks live in
/// <see cref="HeapTable.RowLocks"/>, lazily-interned per
/// <c>(pageIndex, slotIndex)</c>, and key locks in
/// <see cref="HeapTable.KeyLockGroups"/>, lazily-interned per anchor. All mutations to <see cref="Holders"/>
/// happen under <see cref="LockManager"/>'s gate; the class itself has no
/// logic.
/// </summary>
internal sealed class LockResource
{
    /// <summary>
    /// Current holders. One entry per distinct (owner, mode) combination;
    /// re-acquisition by the same owner / same mode bumps
    /// <see cref="Hold.Count"/> instead of appending a duplicate. Mutated
    /// only under <see cref="LockManager"/>'s gate.
    /// </summary>
    public readonly List<Hold> Holders = [];

    /// <summary>
    /// The table this resource locks (a row lock or the
    /// <see cref="HeapTable.TableDataLock"/>), or <c>null</c> for resources
    /// not tied to a heap table (e.g. <see cref="SchemaObject.SchemaLock"/>).
    /// Set at interning time so <see cref="LockManager"/> can maintain the
    /// owning table's <see cref="HeapTable.ActiveDataWriters"/> and
    /// <see cref="HeapTable.ActiveKeyRangeLocks"/> counts without re-deriving
    /// the table on every grant / release.
    /// </summary>
    public HeapTable? OwningTable;

    /// <summary>
    /// The row this resource locks, for a row lock; <c>null</c> otherwise.
    /// Lets the final release of a row X retire that row's entry in
    /// <see cref="HeapTable.SupersededKeyImages"/>.
    /// </summary>
    public (int PageIndex, int SlotIndex)? RowAddress;

    /// <summary>
    /// The key or index this resource is a key lock of, for a
    /// <see cref="KeyLockGroup"/> anchor; <c>null</c> otherwise. Lets
    /// <see cref="LockManager"/> keep <see cref="KeyLockGroup.Holds"/> and
    /// <see cref="HeapTable.ActiveKeyRangeLocks"/> without a lookup.
    /// </summary>
    public KeyLockGroup? KeyGroup;

    /// <summary>
    /// The key tuple a key lock anchors on; <c>null</c> for the infinity anchor
    /// and for every resource that isn't a key lock.
    /// </summary>
    public Parser.SqlValueKey? AnchorKey;

    /// <summary>
    /// One owner's hold on this resource, with re-entrance count. Stored
    /// as a struct in <see cref="Holders"/>; same-owner / same-mode re-
    /// acquires bump <see cref="Count"/> instead of appending a second
    /// entry.
    /// </summary>
    public struct Hold(SessionToken owner, LockMode mode, int count)
    {
        public readonly SessionToken Owner = owner;
        public readonly LockMode Mode = mode;
        public int Count = count;
    }
}

/// <summary>
/// Per-<see cref="Simulation"/> lock coordinator. Owns the single gate
/// every Acquire / Release operation serializes through, plus the
/// cycle-detection walker. The single-gate model trades raw concurrency
/// for simplicity: centralizing the synchronization makes cross-resource
/// cycle detection straightforward (every connection's wait state is
/// readable consistently under the same lock). Because every grant /
/// release / probe funnels through one monitor, it does serialize under
/// heavy concurrent contention — so the hot read path stays off it: a
/// READ COMMITTED row read consults the lock-free
/// <see cref="HeapTable.ActiveDataWriters"/> count first and touches the
/// gate only when a data-X is actually held somewhere on the table
/// (snapshot / RCSI reads bypass it entirely via the version store).
/// </summary>
/// <remarks>
/// <para>
/// Compatibility matrix (the 8-mode SQL Server matrix plus the range family):
/// <list type="bullet">
/// <item>Schema family (Sch-S / Sch-M) is orthogonal to data + intent
/// families. Sch-S × anything-else compatible; Sch-M × anything =
/// conflict.</item>
/// <item>Intent family (IS / IX / SIX): IS × {IS, IX, SIX, S, U} OK; IX
/// × {IS, IX} OK; SIX × {IS} OK only.</item>
/// <item>Data family (S / U / X): S × {S, U, IS} OK; U × {S, IS} OK
/// (note: U × U conflicts — only one upgrader); X × nothing.</item>
/// <item>Cross-family (intent vs data, taken at different granularities
/// but on the same resource — happens when a TABLOCK requester sees row-
/// IX, etc.): S × IX conflict; S × SIX conflict; U × IX conflict; U × SIX
/// conflict; X × any-intent conflict.</item>
/// <item>Range family (RangeS-S / RangeS-U / RangeX-X / RangeI-N): lives on
/// key-lock anchors (<see cref="HeapTable.KeyLockGroups"/>), which can also
/// carry a plain S / U / X — a unique index's point lock, or the instant X a
/// writer tests an anchor it rewrites with — so the two families meet there.
/// Each range mode's key part behaves as the S / U / X it names and RangeI-N
/// tests only the gap: real's key-range compatibility matrix.</item>
/// <item>Same-owner re-entrance is always compatible — the conflict check
/// skips holders whose owner matches the requester. This handles
/// ALTER-with-Sch-S-then-Sch-M, table-IS-then-row-S coexisting on the
/// same owner, repeated row-touches in one statement, etc.</item>
/// </list>
/// </para>
/// <para>
/// Same-thread immediate-deadlock detection (Msg 1205): when an acquire
/// finds a conflict, the holder list is scanned for any holder whose
/// <see cref="SessionToken.CurrentExecutingThreadId"/> equals
/// the caller's managed thread id. If found, the wait is short-circuited
/// since that thread can't release the conflicting hold while it's also
/// the requester. The caller is the victim.
/// </para>
/// <para>
/// Cross-thread cycle detection: when an acquire would block, the
/// detector walks the wait-for graph starting at each conflicting
/// holder's <see cref="SessionToken.WaitingOnResource"/>. If any
/// walk reaches the caller's connection, a cycle exists, and the session in
/// it with the lowest <c>SET DEADLOCK_PRIORITY</c> becomes the victim, the
/// caller on a tie. The walker reads <c>WaitingOnResource</c> + resource
/// holders under the gate, so the snapshot is consistent.
/// </para>
/// </remarks>
/// <summary>
/// Terminal condition of a <see cref="LockManager.TryAcquire"/> call. The
/// throwing <see cref="LockManager.Acquire"/> maps <see cref="TimedOut"/> to
/// Msg 1222 and <see cref="Deadlocked"/> to Msg 1205; the application-lock
/// path maps all four to <c>sp_getapplock</c> return codes.
/// </summary>
internal enum LockAcquireOutcome
{
    /// <summary>Granted without blocking (includes same-owner re-entrance).</summary>
    Granted,

    /// <summary>Granted after at least one wait on the gate.</summary>
    GrantedAfterWait,

    /// <summary>The timeout elapsed while conflicting holders remained.</summary>
    TimedOut,

    /// <summary>The caller was chosen as the deadlock victim (same-thread conflict or wait-for cycle).</summary>
    Deadlocked,

    /// <summary>
    /// The command was cancelled while waiting — its <c>CommandTimeout</c>
    /// elapsed, a client sent an attention, or an in-process caller called
    /// <c>Cancel()</c>. Distinct from <see cref="TimedOut"/>, which is the
    /// session's own <c>SET LOCK_TIMEOUT</c> and reports Msg 1222; a
    /// cancellation reports whatever the command surface reports for an
    /// aborted execution.
    /// </summary>
    Cancelled,
}

internal sealed class LockManager
{
    /// <summary>
    /// The serialization gate. Every Acquire / Release / cycle-check
    /// reads and writes lock state inside <c>lock (this.gate)</c>.
    /// </summary>
    internal readonly object gate = new();

    /// <summary>
    /// How long a blocked waiter sleeps before re-checking its command's
    /// cancellation. Only an upper bound on noticing a cancel — a lock
    /// release Pulses the gate and wakes every waiter immediately — so it
    /// trades a little idle wake-up work for a bounded response to a
    /// CommandTimeout.
    /// </summary>
    private const int CancellationPollMillis = 25;

    /// <summary>
    /// The simulation this manager coordinates, assigned right after
    /// construction. Present so an acquisition can sweep the abandoned
    /// sessions before it decides whether it conflicts — a leaked session's
    /// locks are exactly what a live one would otherwise block on forever.
    /// </summary>
    internal Simulation? OwningSimulation;

    /// <summary>
    /// Drains the simulation's abandoned-session queue, if there is anything
    /// in it, <em>before</em> the gate is taken. Gate-free on purpose: a
    /// teardown rolls a transaction back and releases that session's locks,
    /// which re-enters this manager, and running it under a caller's gate
    /// frame would nest that work inside an unrelated acquisition.
    /// </summary>
    private void SweepAbandonedSessions() => _ = this.OwningSimulation?.ReclaimAbandonedSessions();

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/>
    /// for <paramref name="owner"/>, blocking up to
    /// <paramref name="timeoutMillis"/> if the request conflicts with
    /// existing holders. Same-connection re-acquire of the same mode
    /// increments the existing hold's count; same-connection acquire of a
    /// different mode appends a separate hold (no upgrade — the two
    /// modes are tracked independently and release individually).
    /// <paramref name="sweepAbandoned"/> is false for a lock no abandoned
    /// session can hold — a just-inserted row's — taken under a heap's latch,
    /// where a teardown, which rolls back other tables, must not run.
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 1205 (deadlock) on same-thread conflict or detected
    /// waiter-graph cycle; Msg 1222 (lock timeout) if the wait elapses.
    /// </exception>
    public void Acquire(LockResource resource, LockMode mode, SessionToken owner, int timeoutMillis, bool sweepAbandoned = true)
    {
        switch (this.TryAcquire(resource, mode, owner, timeoutMillis, sweepAbandoned))
        {
            case LockAcquireOutcome.TimedOut:
                throw SimulatedSqlException.LockRequestTimeOutExceeded(TimeoutState(resource));
            case LockAcquireOutcome.Deadlocked:
                throw SimulatedSqlException.TransactionDeadlocked(owner.Spid);
            case LockAcquireOutcome.Cancelled:
                // The same -2 / 0 split the command surface makes: a
                // CommandTimeout is Msg -2, a caller's Cancel() is Msg 0.
                throw owner.TryResolveOwner()?.ExecutionTimedOut == true
                    ? SimulatedSqlException.ExecutionTimeoutExpired()
                    : SimulatedSqlException.CommandCancelled();
        }
    }

    /// <summary>
    /// Msg 1222's state, which names the kind of lock that timed out: 51 for
    /// a key lock — a row of a table with a clustered index is one — 45 for a
    /// heap's row, 56 for a table or schema lock (probed 2026-09-28 against
    /// SQL Server 2025).
    /// </summary>
    private static byte TimeoutState(LockResource resource) => resource switch
    {
        { KeyGroup: not null } => 51,
        { RowAddress: not null, OwningTable: { } table } => table.HasClusteredIndex() ? (byte)51 : (byte)45,
        _ => 56,
    };

    /// <summary>
    /// Non-throwing acquire core. Identical semantics to
    /// <see cref="Acquire"/>, but reports the terminal condition as a
    /// <see cref="LockAcquireOutcome"/> instead of raising Msg 1222 / 1205 —
    /// the application-lock path (<c>sp_getapplock</c>) maps outcomes to
    /// return codes (0 / 1 / -1 / -3) rather than exceptions, matching the
    /// probe-confirmed behavior that an app-lock timeout and even a
    /// deadlock-victim selection surface as return codes with no error.
    /// Distinguishes <see cref="LockAcquireOutcome.GrantedAfterWait"/> from
    /// an immediate grant for sp_getapplock's return-code 1.
    /// </summary>
    public LockAcquireOutcome TryAcquire(LockResource resource, LockMode mode, SessionToken owner, int timeoutMillis, bool sweepAbandoned = true)
    {
        if (sweepAbandoned)
            this.SweepAbandonedSessions();
        lock (this.gate)
        {
            // Same-owner / same-mode re-entrance: bump the existing hold's
            // count and return. Same-owner / different-mode falls through
            // to the compatibility check, where same-owner holders are
            // skipped (treated as trivially compatible).
            for (var i = 0; i < resource.Holders.Count; i++)
            {
                if (ReferenceEquals(resource.Holders[i].Owner, owner) && resource.Holders[i].Mode == mode)
                {
                    var hold = resource.Holders[i];
                    hold.Count++;
                    resource.Holders[i] = hold;
                    return LockAcquireOutcome.Granted;
                }
            }

            var deadline = timeoutMillis < 0 ? -1L : Environment.TickCount64 + timeoutMillis;
            var waited = false;
            // The waiting session's own execution token — the one a
            // CommandTimeout, a TDS attention and an in-process Cancel() all
            // signal. Taken from the session rather than threaded through
            // every caller: a lock wait is the session's, so the session is
            // where the answer already lives.
            var cancellation = owner.TryResolveOwner()?.ExecutionCancellationToken ?? CancellationToken.None;

            try
            {
                while (true)
                {
                    // A requester elsewhere closed a cycle and chose this
                    // waiting session over itself.
                    if (owner.ChosenAsDeadlockVictim)
                        return LockAcquireOutcome.Deadlocked;

                    if (TryGrant(resource, mode, owner))
                        return waited ? LockAcquireOutcome.GrantedAfterWait : LockAcquireOutcome.Granted;

                    // Same-thread conflict → immediate Msg 1205. This thread
                    // is the executor for both the caller and a conflicting
                    // holder; no progress possible.
                    if (IsConflictingHolderOnSameThread(resource, mode, owner))
                        return LockAcquireOutcome.Deadlocked;

                    // Cross-thread cycle detection. Walk the wait-for graph
                    // from each conflicting holder; if any walk reaches the
                    // caller, a cycle exists, and its victim is the caller
                    // unless a session in the cycle runs at a lower
                    // DEADLOCK_PRIORITY — then that one's wait ends in
                    // Msg 1205 and the caller waits on for its rollback.
                    if (FindDeadlockVictim(resource, mode, owner) is { } victim)
                    {
                        if (ReferenceEquals(victim, owner))
                            return LockAcquireOutcome.Deadlocked;
                        if (!victim.ChosenAsDeadlockVictim)
                        {
                            victim.ChosenAsDeadlockVictim = true;
                            Monitor.PulseAll(this.gate);
                        }
                    }

                    // Timeout==0 = fail-fast.
                    if (timeoutMillis == 0)
                        return LockAcquireOutcome.TimedOut;

                    var remaining = deadline < 0 ? Timeout.Infinite : (int)Math.Max(0, deadline - Environment.TickCount64);
                    if (timeoutMillis > 0 && remaining == 0)
                        return LockAcquireOutcome.TimedOut;

                    // Mark the caller as waiting on this resource so other
                    // connections' cycle walks can see the edge. Written once
                    // and left set for the whole wait — a re-check between
                    // slices must not drop the edge, because the DMVs read it
                    // without the gate (see LockDmvs) and would report a
                    // blocked session as idle for as long as this thread stays
                    // descheduled mid-check. Cleared once on the way out, so
                    // an exception path (Msg 1222 / 1205) leaves no stale
                    // wait state either.
                    if (!waited)
                        owner.WaitStartedTicks = Environment.TickCount64;
                    owner.WaitingOnResource = resource;
                    owner.WaitingForMode = mode;
                    waited = true;
                    // Monitor.Wait can't observe a token, so the wait is
                    // sliced: a blocked statement has to notice its own
                    // CommandTimeout, and SET LOCK_TIMEOUT's default of
                    // "wait forever" would otherwise make a block permanent.
                    // The slice only bounds how long cancellation goes
                    // unnoticed — a release still Pulses and wakes it at once,
                    // so this costs nothing on the granted path.
                    var slice = remaining == Timeout.Infinite
                        ? CancellationPollMillis
                        : Math.Min(remaining, CancellationPollMillis);
                    if (!Monitor.Wait(this.gate, slice))
                    {
                        if (cancellation.IsCancellationRequested)
                            return LockAcquireOutcome.Cancelled;
                        if (timeoutMillis > 0 && Environment.TickCount64 >= deadline)
                            return LockAcquireOutcome.TimedOut;
                    }
                }
            }
            finally
            {
                // Guarded so an uncontended acquire — the overwhelming
                // majority — doesn't write the session's wait fields at all.
                if (waited)
                {
                    owner.LockWaitedMilliseconds += Environment.TickCount64 - owner.WaitStartedTicks;
                    owner.WaitingOnResource = null;
                    owner.WaitingForMode = null;
                    owner.ChosenAsDeadlockVictim = false;
                }
            }
        }
    }

    /// <summary>
    /// Non-blocking compatibility probe: returns true if any holder other
    /// than <paramref name="excludingOwner"/> holds <paramref name="resource"/>
    /// in a mode incompatible with <paramref name="probedMode"/>. Used by the
    /// reader's row-conflict-check path: a SELECT under READ COMMITTED
    /// peeks for "is some other connection's tx-scoped row-X holding this
    /// row?" without actually acquiring — if no, the row reads through;
    /// if yes, the reader can either wait (the default) or skip
    /// (<c>READPAST</c>). <paramref name="counts"/>, when given, narrows the
    /// holders considered.
    /// </summary>
    public bool HasIncompatibleHolderOtherThan(LockResource resource, LockMode probedMode, SessionToken excludingOwner, Func<SessionToken, bool>? counts = null)
    {
        lock (this.gate)
        {
            foreach (var hold in resource.Holders)
            {
                if (ReferenceEquals(hold.Owner, excludingOwner) || (counts is not null && !counts(hold.Owner)))
                    continue;
                if (!IsCompatible(hold.Mode, probedMode))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Releases one acquisition of <paramref name="mode"/> by
    /// <paramref name="owner"/>. Re-entrant acquires must match release
    /// 1-for-1; the final release of a (owner, mode) pair removes the
    /// holder entry and pulses every waiter on this manager's gate so
    /// each waiter re-checks compatibility.
    /// </summary>
    public void Release(LockResource resource, LockMode mode, SessionToken owner)
    {
        lock (this.gate)
        {
            for (var i = 0; i < resource.Holders.Count; i++)
            {
                if (ReferenceEquals(resource.Holders[i].Owner, owner) && resource.Holders[i].Mode == mode)
                {
                    var hold = resource.Holders[i];
                    hold.Count--;
                    if (hold.Count == 0)
                    {
                        resource.Holders.RemoveAt(i);
                        if (resource.OwningTable is { } table)
                        {
                            if (mode is LockMode.Exclusive or LockMode.RangeExclusiveExclusive)
                            {
                                _ = Interlocked.Decrement(ref table.ActiveDataWriters);
                                if (resource.RowAddress is { } address)
                                    table.RetireSupersededKeyImage(owner, address);
                            }
                            if (resource.KeyGroup is { } group)
                            {
                                _ = Interlocked.Decrement(ref group.Holds);
                                _ = Interlocked.Decrement(ref table.ActiveKeyRangeLocks);
                            }
                        }
                        Monitor.PulseAll(this.gate);
                    }
                    else
                    {
                        resource.Holders[i] = hold;
                    }
                    return;
                }
            }
            throw new InvalidOperationException(
                $"LockManager.Release called without a matching Acquire (owner SPID {owner.Spid}, mode {mode}).");
        }
    }

    /// <summary>
    /// True when <paramref name="mode"/> is compatible with every current
    /// holder of <paramref name="resource"/> (same-owner holds skipped —
    /// re-entrance is handled in <see cref="Acquire"/>). Appends a new
    /// hold on success.
    /// </summary>
    private static bool TryGrant(LockResource resource, LockMode mode, SessionToken owner)
    {
        foreach (var hold in resource.Holders)
        {
            if (ReferenceEquals(hold.Owner, owner))
                continue;
            if (!IsCompatible(hold.Mode, mode))
                return false;
        }
        resource.Holders.Add(new LockResource.Hold(owner, mode, 1));
        if (resource.OwningTable is { } table)
        {
            if (mode is LockMode.Exclusive or LockMode.RangeExclusiveExclusive)
                _ = Interlocked.Increment(ref table.ActiveDataWriters);
            if (resource.KeyGroup is { } group)
            {
                _ = Interlocked.Increment(ref group.Holds);
                _ = Interlocked.Increment(ref table.ActiveKeyRangeLocks);
            }
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="owner"/> already holds <paramref name="mode"/>
    /// on <paramref name="resource"/> — lets a caller that re-covers the same
    /// keys once per outer row skip the re-entrant acquisition, which would
    /// otherwise pile up one held-lock entry per pass.
    /// </summary>
    public bool IsHeldBy(LockResource resource, LockMode mode, SessionToken owner)
    {
        lock (this.gate)
        {
            foreach (var hold in resource.Holders)
            {
                if (ReferenceEquals(hold.Owner, owner) && hold.Mode == mode)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="owner"/> holds <paramref name="resource"/> in a
    /// key-range mode that fences the gap below it.
    /// </summary>
    public bool HoldsRangeMode(LockResource resource, SessionToken owner)
    {
        lock (this.gate)
        {
            foreach (var hold in resource.Holders)
            {
                if (ReferenceEquals(hold.Owner, owner) && hold.Mode is LockMode.RangeSharedShared or LockMode.RangeSharedUpdate or LockMode.RangeExclusiveExclusive)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The plain mode a key-range mode's key part behaves as — the S, U or X
    /// another session's row lock on the anchored key is tested against.
    /// </summary>
    internal static LockMode KeyPartOf(LockMode rangeMode) => rangeMode switch
    {
        LockMode.RangeSharedShared => LockMode.Shared,
        LockMode.RangeSharedUpdate => LockMode.Update,
        LockMode.RangeExclusiveExclusive => LockMode.Exclusive,
        _ => rangeMode,
    };

    /// <summary>
    /// True if any conflicting holder's
    /// <see cref="SessionToken.CurrentExecutingThreadId"/>
    /// equals the caller's current managed thread id — those threads
    /// can't make progress while this one is the requester.
    /// </summary>
    private static bool IsConflictingHolderOnSameThread(LockResource resource, LockMode mode, SessionToken owner)
    {
        var myThread = Environment.CurrentManagedThreadId;
        foreach (var hold in resource.Holders)
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
    /// Walks the wait-for graph from each conflicting holder, looking for a
    /// path that leads back to <paramref name="caller"/> — a textbook deadlock
    /// cycle (caller → resource → conflicting holder → … → caller). Returns
    /// <see langword="null"/> when there is none, else the cycle's victim: the
    /// session in it with the lowest <c>SET DEADLOCK_PRIORITY</c>, the caller
    /// on a tie (probed 2026-09-28 against SQL Server 2025). Reads
    /// <see cref="LockResource.Holders"/> and
    /// <see cref="SessionToken.WaitingOnResource"/> under the manager's gate —
    /// consistent snapshot.
    /// </summary>
    private static SessionToken? FindDeadlockVictim(LockResource resource, LockMode mode, SessionToken caller)
    {
        var visited = new HashSet<SessionToken>(ReferenceEqualityComparer.Instance);
        var path = new List<SessionToken>();
        foreach (var hold in resource.Holders)
        {
            if (ReferenceEquals(hold.Owner, caller))
                continue;
            if (IsCompatible(hold.Mode, mode))
                continue;
            if (!WalkBack(hold.Owner, caller, visited, path))
                continue;
            var victim = caller;
            var lowest = PriorityOf(caller);
            foreach (var participant in path)
            {
                // A cycle already resolving through its chosen victim.
                if (participant.ChosenAsDeadlockVictim)
                    return participant;
                if (PriorityOf(participant) is var priority && priority < lowest)
                    (victim, lowest) = (participant, priority);
            }
            return victim;
        }
        return null;
    }

    private static int PriorityOf(SessionToken session) => session.TryResolveOwner()?.DeadlockPriority ?? 0;

    /// <summary>
    /// DFS step: is <paramref name="blocker"/> transitively waiting on a
    /// resource <paramref name="target"/> holds? Skips already-visited
    /// connections to break finite cycles in the walk (degenerate
    /// cycles within the holder set itself). On success
    /// <paramref name="path"/> holds the blockers the cycle runs through.
    /// </summary>
    private static bool WalkBack(SessionToken blocker, SessionToken target, HashSet<SessionToken> visited, List<SessionToken> path)
    {
        if (!visited.Add(blocker))
            return false;
        var waitsOn = blocker.WaitingOnResource;
        if (waitsOn is null)
            return false;
        path.Add(blocker);
        foreach (var hold in waitsOn.Holders)
        {
            if (ReferenceEquals(hold.Owner, blocker))
                continue;
            if (ReferenceEquals(hold.Owner, target))
                return true;
            if (WalkBack(hold.Owner, target, visited, path))
                return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    /// <summary>
    /// Static compatibility matrix. Schema family (Sch-S / Sch-M),
    /// intent family (IS / IX / SIX), and data family (S / U / X) cover
    /// 8 modes. Most cross-family pairs are compatible (Sch-S coexists
    /// with everything; IS coexists with all data modes except X); a few
    /// fail (S × IX, U × IX, X × any-intent). The full table is below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matrix (held → requested):
    /// <code>
    ///         Sch-S Sch-M IS    IX    SIX   S     U     X
    /// Sch-S   ✓     ✗     ✓     ✓     ✓     ✓     ✓     ✓
    /// Sch-M   ✗     ✗     ✗     ✗     ✗     ✗     ✗     ✗
    /// IS      ✓     ✗     ✓     ✓     ✓     ✓     ✓     ✗
    /// IX      ✓     ✗     ✓     ✓     ✗     ✗     ✗     ✗
    /// SIX     ✓     ✗     ✓     ✗     ✗     ✗     ✗     ✗
    /// S       ✓     ✗     ✓     ✗     ✗     ✓     ✓     ✗
    /// U       ✓     ✗     ✓     ✗     ✗     ✓     ✗     ✗
    /// X       ✓     ✗     ✗     ✗     ✗     ✗     ✗     ✗
    /// </code>
    /// </para>
    /// </remarks>
    internal static bool IsCompatible(LockMode held, LockMode requested) =>
        (held, requested) switch
        {
            // Range family first, settled before its pairs can fall through
            // into the row / table arms below. A key-lock anchor also carries
            // plain S / U / X (a unique index's point lock, a writer's instant
            // X), so the mixed cells are real's key-range matrix: a range
            // mode's key part acts as the S / U / X it names, RangeI-N tests
            // only the gap and so passes every plain mode, and RangeX-X passes
            // nothing.
            (LockMode.RangeSharedShared, LockMode.RangeSharedShared) => true,
            (LockMode.RangeSharedShared, LockMode.RangeSharedUpdate) => true,
            (LockMode.RangeSharedUpdate, LockMode.RangeSharedShared) => true,
            (LockMode.RangeInsertNull, LockMode.RangeInsertNull) => true,
            (LockMode.RangeInsertNull, LockMode.Shared or LockMode.Update or LockMode.Exclusive) => true,
            (LockMode.Shared or LockMode.Update or LockMode.Exclusive, LockMode.RangeInsertNull) => true,
            (LockMode.RangeSharedShared, LockMode.Shared or LockMode.Update) => true,
            (LockMode.Shared or LockMode.Update, LockMode.RangeSharedShared) => true,
            (LockMode.RangeSharedUpdate, LockMode.Shared) => true,
            (LockMode.Shared, LockMode.RangeSharedUpdate) => true,
            (LockMode.RangeSharedShared or LockMode.RangeSharedUpdate or LockMode.RangeExclusiveExclusive or LockMode.RangeInsertNull, _) => false,
            (_, LockMode.RangeSharedShared or LockMode.RangeSharedUpdate or LockMode.RangeExclusiveExclusive or LockMode.RangeInsertNull) => false,
            // Sch-M conflicts with everything.
            (LockMode.SchemaModification, _) => false,
            (_, LockMode.SchemaModification) => false,
            // Sch-S is compatible with everything else.
            (LockMode.SchemaStability, _) => true,
            (_, LockMode.SchemaStability) => true,
            // X is exclusive against every other data/intent mode.
            (LockMode.Exclusive, _) => false,
            (_, LockMode.Exclusive) => false,
            // IS coexists with everything that isn't X (already excluded above).
            (LockMode.IntentShared, _) => true,
            (_, LockMode.IntentShared) => true,
            // IX coexists with IX (already-handled above with IS).
            (LockMode.IntentExclusive, LockMode.IntentExclusive) => true,
            // SIX × IX, SIX × SIX, SIX × S/U all conflict.
            (LockMode.SharedIntentExclusive, _) => false,
            (_, LockMode.SharedIntentExclusive) => false,
            // S / U cases. IX × S, IX × U all conflict (caught here).
            (LockMode.IntentExclusive, _) => false,
            (_, LockMode.IntentExclusive) => false,
            // Data family: S × S, S × U, U × S compatible; U × U conflict.
            (LockMode.Shared, LockMode.Shared) => true,
            (LockMode.Shared, LockMode.Update) => true,
            (LockMode.Update, LockMode.Shared) => true,
            _ => false,
        };
}
