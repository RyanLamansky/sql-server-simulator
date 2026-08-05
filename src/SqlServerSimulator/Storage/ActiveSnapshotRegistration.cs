namespace SqlServerSimulator.Storage;

/// <summary>
/// One session's live SNAPSHOT-isolation registration in
/// <see cref="Simulation.ActiveSnapshotTxs"/>: enough of the transaction to
/// answer the version-store collector and
/// <c>sys.dm_tran_active_snapshot_database_transactions</c>, and nothing that
/// would keep the session alive.
/// </summary>
/// <remarks>
/// Copying the three facts is what breaks the last global reference into a
/// session: the transaction object holds its <see cref="SimulatedDbConnection"/>
/// — ADO.NET's <c>DbTransaction.Connection</c> contract requires that — so a
/// simulation-wide registry keyed or valued by transactions would pin every
/// connection that ever took a snapshot, locks and all.
/// </remarks>
internal sealed class ActiveSnapshotRegistration(long transactionId, long snapshotXid, int spid)
{
    /// <summary>
    /// Stand-in for real's <c>transaction_id</c> — the transaction object's
    /// runtime hash, captured while it was registered.
    /// </summary>
    public readonly long TransactionId = transactionId;

    /// <summary>The commit-id stamp this transaction reads as of.</summary>
    public readonly long SnapshotXid = snapshotXid;

    /// <summary>The owning session's SPID.</summary>
    public readonly int Spid = spid;
}
