namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// Where a database stands in a restore sequence it hasn't recovered from:
/// the LSN its next differential or log backup set must reach, the point in
/// its log its rows stand at, and the full backup the sequence began from.
/// </summary>
/// <remarks>
/// Real checks a set against these as SQL Server 2025 was probed checking
/// (2026-10-16): a log set beginning past <see cref="RedoBlock"/> is Msg 4305
/// and one ending before it Msg 4326, a differential of another base Msg 3136
/// state 1 and one ending before it state 3; a log set ending exactly at it
/// applies, to no effect.
/// </remarks>
internal sealed class RestoreSequence
{
    /// <summary>The LSN block the next set must include: the last restored set's last LSN, or its first after a stop inside it.</summary>
    public long RedoBlock;

    /// <summary>The <see cref="DatabaseLog"/> sequence the database's rows stand at, past which a log set's commits apply.</summary>
    public long LogSequence;

    /// <summary>The time the database's rows stand at, which a <c>STOPAT</c> earlier than it can't reach.</summary>
    public DateTime PointUtc;

    /// <summary>The full backup set the sequence began from, the base a differential must have.</summary>
    public Guid? BaseGuid;

    /// <summary>The set restored last, whose LSNs the backup chain continues from at recovery.</summary>
    public BackupSet? LastSet;

    /// <summary>A log restore stopped inside its set, so recovery forks the database's recovery path.</summary>
    public bool StoppedInsideLog;

    /// <summary>The <c>STANDBY</c> file the last restore wrote, deleted at recovery.</summary>
    public string? StandbyFile;
}
