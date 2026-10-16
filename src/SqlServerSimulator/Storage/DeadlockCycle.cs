namespace SqlServerSimulator.Storage;

/// <summary>
/// A deadlock as <see cref="LockManager"/> found it, captured under its gate
/// while every session in the cycle waits: each process with the lock it waits
/// for and the session that holds it, in the order real's report lists them —
/// the victim first, then each session waiting on the one before it, so the
/// last holds what the victim waits for (probed 2026-10-16 against SQL Server
/// 2025 over two- and three-session cycles). The victim reports it
/// (<c>Simulation.ReportDeadlock</c>) before its error rolls anything back.
/// </summary>
internal sealed class DeadlockCycle(DeadlockCycle.Edge[] edges, int deadlockId)
{
    /// <summary>One process of the cycle and the lock it waits for.</summary>
    internal readonly struct Edge(SessionToken waiter, LockResource resource, LockMode waitMode, SessionToken holder, LockMode heldMode, long waitStartedTicks)
    {
        public readonly SessionToken Waiter = waiter;
        public readonly LockResource Resource = resource;
        public readonly LockMode WaitMode = waitMode;

        /// <summary>The session holding <see cref="Resource"/> in a mode the wait conflicts with — the next process of the cycle's wait-for order.</summary>
        public readonly SessionToken Holder = holder;

        public readonly LockMode HeldMode = heldMode;

        /// <summary>When the wait began (<see cref="Environment.TickCount64"/>), the requester's own when it hadn't begun waiting.</summary>
        public readonly long WaitStartedTicks = waitStartedTicks;
    }

    /// <summary>The processes in report order, the victim's first.</summary>
    public readonly Edge[] Edges = edges;

    /// <summary>The server's count of deadlocks, which <c>lock_deadlock</c>'s <c>deadlock_id</c> reports.</summary>
    public readonly int DeadlockId = deadlockId;

    /// <summary>When the cycle was found.</summary>
    public readonly DateTime FoundUtc = DateTime.UtcNow;
}
