namespace SqlServerSimulator;

/// <summary>
/// Lock modes recognized by <see cref="LockManager"/>. Two orthogonal
/// families: schema-stability locks (Sch-S / Sch-M) protect against
/// concurrent DDL on an object; data locks (Shared / Exclusive) protect
/// against concurrent DML reads / writes. The compatibility matrix in
/// <see cref="LockManager.IsCompatible"/> spells out the relationships.
/// </summary>
internal enum LockMode
{
    /// <summary>Schema stability — multiple holders allowed; blocks Sch-M.</summary>
    SchemaStability,
    /// <summary>Schema modification — exclusive against every other mode.</summary>
    SchemaModification,
    /// <summary>Data shared (S) — multiple holders allowed; blocks Exclusive.</summary>
    Shared,
    /// <summary>Data exclusive (X) — exclusive against Shared and Exclusive.</summary>
    Exclusive,
}

/// <summary>
/// Passive per-object lock state. Holds the current set of acquisitions
/// (each a <see cref="Hold"/> entry with owner / mode / re-entrance
/// count). Every <see cref="SchemaObject"/> carries one via the inherited
/// <see cref="SchemaObject.SchemaLock"/>. All mutations to
/// <see cref="Holders"/> happen under <see cref="LockManager"/>'s gate;
/// the class itself has no logic.
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
    /// One owner's hold on this resource, with re-entrance count. Stored
    /// as a struct in <see cref="Holders"/>; same-owner / same-mode re-
    /// acquires bump <see cref="Count"/> instead of appending a second
    /// entry.
    /// </summary>
    public struct Hold(SimulatedDbConnection owner, LockMode mode, int count)
    {
        public readonly SimulatedDbConnection Owner = owner;
        public readonly LockMode Mode = mode;
        public int Count = count;
    }
}

/// <summary>
/// Per-<see cref="Simulation"/> lock coordinator. Owns the single gate
/// every Acquire / Release operation serializes through, plus the
/// cycle-detection walker. The single-gate model trades raw concurrency
/// for simplicity — at the simulator's "tens of connections per Simulation"
/// scale, the gate isn't a bottleneck, and centralizing the synchronization
/// makes cross-resource cycle detection straightforward (every connection's
/// wait state is readable consistently under the same lock).
/// </summary>
/// <remarks>
/// <para>
/// Compatibility matrix (phase 1a):
/// <list type="bullet">
/// <item>Schema family (Sch-S / Sch-M) is orthogonal to the data family
/// (S / X). Sch-S × S, Sch-S × X, Sch-M × Sch-M / S / X all compute
/// independently — a Sch-M waits behind any other holder; a Sch-S
/// coexists with S / X / other Sch-S.</item>
/// <item>Data family: S × S compatible; S × X conflict; X × anything
/// conflict.</item>
/// <item>Same-owner re-entrance is always compatible — the conflict check
/// skips holders whose owner matches the requester. This handles the
/// ALTER TABLE pattern (Sch-S then Sch-M from the same connection) and
/// the multi-DML pattern (same connection updates the same row twice in
/// one statement).</item>
/// </list>
/// </para>
/// <para>
/// Same-thread immediate-deadlock detection (Msg 1205): when an acquire
/// finds a conflict, the holder list is scanned for any holder whose
/// <see cref="SimulatedDbConnection.CurrentExecutingThreadId"/> equals
/// the caller's managed thread id. If found, the wait is short-circuited
/// since that thread can't release the conflicting hold while it's also
/// the requester. The caller is the victim (per user's "always-the-
/// requester" policy).
/// </para>
/// <para>
/// Cross-thread cycle detection: when an acquire would block, the
/// detector walks the wait-for graph starting at each conflicting
/// holder's <see cref="SimulatedDbConnection.WaitingOnResource"/>. If any
/// walk reaches the caller's connection, a cycle exists; caller becomes
/// the victim. The walker reads <c>WaitingOnResource</c> + resource
/// holders under the gate, so the snapshot is consistent.
/// </para>
/// </remarks>
internal sealed class LockManager
{
    /// <summary>
    /// The serialization gate. Every Acquire / Release / cycle-check
    /// reads and writes lock state inside <c>lock (this.gate)</c>.
    /// </summary>
    internal readonly object gate = new();

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/>
    /// for <paramref name="owner"/>, blocking up to
    /// <paramref name="timeoutMillis"/> if the request conflicts with
    /// existing holders. Same-connection re-acquire of the same mode
    /// increments the existing hold's count; same-connection acquire of a
    /// different mode appends a separate hold (no upgrade — the two
    /// modes are tracked independently and release individually).
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 1205 (deadlock) on same-thread conflict or detected
    /// waiter-graph cycle; Msg 1222 (lock timeout) if the wait elapses.
    /// </exception>
    public void Acquire(LockResource resource, LockMode mode, SimulatedDbConnection owner, int timeoutMillis)
    {
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
                    return;
                }
            }

            var deadline = timeoutMillis < 0 ? -1L : Environment.TickCount64 + timeoutMillis;

            while (true)
            {
                if (TryGrant(resource, mode, owner))
                    return;

                // Same-thread conflict → immediate Msg 1205. This thread
                // is the executor for both the caller and a conflicting
                // holder; no progress possible.
                if (IsConflictingHolderOnSameThread(resource, mode, owner))
                    throw SimulatedSqlException.TransactionDeadlocked(owner.Spid);

                // Cross-thread cycle detection. Walk the wait-for graph
                // from each conflicting holder; if any walk reaches the
                // caller, a cycle exists and the caller is the victim.
                if (WouldCreateCycle(resource, mode, owner))
                    throw SimulatedSqlException.TransactionDeadlocked(owner.Spid);

                // Timeout==0 = fail-fast.
                if (timeoutMillis == 0)
                    throw SimulatedSqlException.LockRequestTimeOutExceeded();

                var remaining = deadline < 0 ? Timeout.Infinite : (int)Math.Max(0, deadline - Environment.TickCount64);
                if (timeoutMillis > 0 && remaining == 0)
                    throw SimulatedSqlException.LockRequestTimeOutExceeded();

                // Mark the caller as waiting on this resource so other
                // connections' cycle walks can see the edge. Cleared in
                // finally so an exception path (Msg 1222 / 1205) leaves
                // no stale wait state. Set under the gate, read under
                // the gate — snapshot is consistent.
                owner.WaitingOnResource = resource;
                try
                {
                    if (!Monitor.Wait(this.gate, remaining))
                        throw SimulatedSqlException.LockRequestTimeOutExceeded();
                }
                finally
                {
                    owner.WaitingOnResource = null;
                }
            }
        }
    }

    /// <summary>
    /// Releases one acquisition of <paramref name="mode"/> by
    /// <paramref name="owner"/>. Re-entrant acquires must match release
    /// 1-for-1; the final release of a (owner, mode) pair removes the
    /// holder entry and pulses every waiter on this manager's gate so
    /// each waiter re-checks compatibility.
    /// </summary>
    public void Release(LockResource resource, LockMode mode, SimulatedDbConnection owner)
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
    private static bool TryGrant(LockResource resource, LockMode mode, SimulatedDbConnection owner)
    {
        foreach (var hold in resource.Holders)
        {
            if (ReferenceEquals(hold.Owner, owner))
                continue;
            if (!IsCompatible(hold.Mode, mode))
                return false;
        }
        resource.Holders.Add(new LockResource.Hold(owner, mode, 1));
        return true;
    }

    /// <summary>
    /// True if any conflicting holder's
    /// <see cref="SimulatedDbConnection.CurrentExecutingThreadId"/>
    /// equals the caller's current managed thread id — those threads
    /// can't make progress while this one is the requester.
    /// </summary>
    private static bool IsConflictingHolderOnSameThread(LockResource resource, LockMode mode, SimulatedDbConnection owner)
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
    /// Walks the wait-for graph from each conflicting holder. Returns
    /// true when a path leads back to <paramref name="caller"/>, which
    /// is a textbook deadlock cycle (caller → resource → conflicting
    /// holder → … → caller). Reads <see cref="LockResource.Holders"/>
    /// and <see cref="SimulatedDbConnection.WaitingOnResource"/> under
    /// the manager's gate — consistent snapshot.
    /// </summary>
    private static bool WouldCreateCycle(LockResource resource, LockMode mode, SimulatedDbConnection caller)
    {
        var visited = new HashSet<SimulatedDbConnection>(ReferenceEqualityComparer.Instance);
        foreach (var hold in resource.Holders)
        {
            if (ReferenceEquals(hold.Owner, caller))
                continue;
            if (IsCompatible(hold.Mode, mode))
                continue;
            if (WalkBack(hold.Owner, caller, visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// DFS step: is <paramref name="blocker"/> transitively waiting on a
    /// resource <paramref name="target"/> holds? Skips already-visited
    /// connections to break finite cycles in the walk (degenerate
    /// cycles within the holder set itself).
    /// </summary>
    private static bool WalkBack(SimulatedDbConnection blocker, SimulatedDbConnection target, HashSet<SimulatedDbConnection> visited)
    {
        if (!visited.Add(blocker))
            return false;
        var waitsOn = blocker.WaitingOnResource;
        if (waitsOn is null)
            return false;
        foreach (var hold in waitsOn.Holders)
        {
            if (ReferenceEquals(hold.Owner, blocker))
                continue;
            if (ReferenceEquals(hold.Owner, target))
                return true;
            if (WalkBack(hold.Owner, target, visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Static compatibility matrix. Schema family (Sch-S / Sch-M) and
    /// data family (Shared / Exclusive) are orthogonal. Cross-family
    /// pairs are compatible — a Sch-S holder doesn't block an X
    /// requester and vice versa. Within each family the standard
    /// reader/writer rules apply.
    /// </summary>
    internal static bool IsCompatible(LockMode held, LockMode requested) =>
        (held, requested) switch
        {
            // Sch-M conflicts with everything.
            (LockMode.SchemaModification, _) => false,
            (_, LockMode.SchemaModification) => false,
            // Sch-S is compatible with everything else (data locks orthogonal).
            (LockMode.SchemaStability, _) => true,
            (_, LockMode.SchemaStability) => true,
            // Data family: only Shared × Shared is compatible.
            (LockMode.Shared, LockMode.Shared) => true,
            _ => false,
        };
}
