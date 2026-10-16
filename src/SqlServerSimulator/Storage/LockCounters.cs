namespace SqlServerSimulator.Storage;

/// <summary>
/// The lock traffic the <c>SQLServer:Locks</c> performance counters report,
/// per kind of resource: the requests <see cref="LockManager"/> takes, the
/// waits among them and their milliseconds, and the waits that ended in a
/// timeout or a deadlock. Written under the manager's gate, read without it.
/// </summary>
internal sealed class LockCounters
{
    /// <summary>The resource kinds, as real's counter instances name them.</summary>
    public enum Kind
    {
        Application,
        Key,
        Metadata,
        Object,
        Page,
        Rid,
    }

    /// <summary>One slot per <see cref="Kind"/>.</summary>
    public const int KindCount = 6;

    public readonly long[] Requests = new long[KindCount];
    public readonly long[] Waits = new long[KindCount];
    public readonly long[] WaitMilliseconds = new long[KindCount];
    public readonly long[] Timeouts = new long[KindCount];

    /// <summary>The timeouts of a request that waited first — a <c>LOCK_TIMEOUT</c> above 0.</summary>
    public readonly long[] TimeoutsAfterWaiting = new long[KindCount];

    public readonly long[] Deadlocks = new long[KindCount];

    /// <summary>
    /// The kind of <paramref name="resource"/>: a key lock, a heap row's
    /// RID, a page, a name's metadata lock, an application lock, or an
    /// object's.
    /// </summary>
    public static Kind Of(LockResource resource) => resource switch
    {
        { KeyGroup: not null } => Kind.Key,
        { Definition: not null } => Kind.Metadata,
        { PageOfTable: not null } or { Visit: not null } => Kind.Page,
        { RowAddress: not null, OwningTable: { } table } => table.HasClusteredIndex() ? Kind.Key : Kind.Rid,
        { IsApplicationLock: true } => Kind.Application,
        _ => Kind.Object,
    };
}
