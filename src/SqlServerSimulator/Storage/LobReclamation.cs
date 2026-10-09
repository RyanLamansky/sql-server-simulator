namespace SqlServerSimulator.Storage;

/// <summary>
/// When an off-row LOB chain a write gave up may be handed to another row:
/// once every statement that might still hold a row image naming it has
/// ended. A statement reads a row's bytes and decodes its LOB columns later —
/// a column is decoded when first referenced, a sort or a MERGE's target pass
/// holds images for the statement's length — and a READ COMMITTED or
/// <c>NOLOCK</c> reader holds no lock that keeps the row's writer out
/// meanwhile, where real's row lock (or its deferred ghost cleanup) would.
/// A superseded chain freed at commit could be in another row's hands by the
/// time such a reader follows its image into it.
/// </summary>
/// <remarks>
/// One per <see cref="Simulation"/>: a session announces its outermost
/// statement's start (<see cref="Enter"/>) on its <see cref="SessionToken"/>,
/// a chain retired by a commit, a rollback or the version sweep is tagged by
/// advancing the clock (<see cref="Heap.RetireLobChain"/>), and the heap
/// reuses it only once no announced statement began before that tag. A
/// statement announcing after the tag began after the write it superseded
/// had left the heap, so no image it reads names the chain.
/// </remarks>
internal sealed class LobReclamation(Simulation simulation)
{
    private long epoch = 1;

    /// <summary>
    /// Announces <paramref name="session"/>'s statement as a reader holding
    /// row images until <see cref="Leave"/>; false, announcing nothing, when an
    /// enclosing statement of the session already did, since that one's
    /// images outlive this one.
    /// </summary>
    public bool Enter(SessionToken session)
    {
        if (Volatile.Read(ref session.StatementEpoch) != long.MaxValue)
            return false;
        // A full fence: the announcement is visible before the statement reads
        // a row, so a reclaimer that misses it advanced the clock first.
        _ = Interlocked.Exchange(ref session.StatementEpoch, Volatile.Read(ref this.epoch));
        return true;
    }

    /// <summary>Withdraws what <see cref="Enter"/> announced.</summary>
    public static void Leave(SessionToken session) => Volatile.Write(ref session.StatementEpoch, long.MaxValue);

    /// <summary>The tag of a chain retiring now: later than every statement announced so far.</summary>
    public long Retire() => Interlocked.Increment(ref this.epoch);

    /// <summary>
    /// The earliest epoch a running statement announced, or
    /// <see cref="long.MaxValue"/> when none is running: a chain whose tag is
    /// at or below it is free to reuse.
    /// </summary>
    public long OldestReader()
    {
        var oldest = long.MaxValue;
        lock (simulation.Sessions)
        {
            foreach (var session in simulation.Sessions)
                oldest = Math.Min(oldest, Math.Min(Volatile.Read(ref session.StatementEpoch), Volatile.Read(ref session.ParkedStatementEpoch)));
        }
        return oldest;
    }
}
