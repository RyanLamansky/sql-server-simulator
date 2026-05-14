using System.Data;
using System.Data.Common;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

sealed class SimulatedDbTransaction(Simulation simulation, SimulatedDbConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    internal readonly Simulation simulation = simulation;
#pragma warning disable CA2213 // Disposable fields should be disposed
    // This is intended to survive even if the transaction is disposed.
    internal readonly SimulatedDbConnection connection = connection;
#pragma warning restore

    /// <summary>
    /// Cross-statement undo log for this transaction. Statements executed
    /// while this is the connection's active transaction append entries
    /// here; <see cref="Rollback"/> walks the log backwards. <see cref="Commit"/>
    /// just discards it — committed writes are already in the heap.
    /// </summary>
    internal readonly UndoLog UndoLog = new();

    /// <summary>
    /// SQL Server's <c>@@TRANCOUNT</c> nesting depth. Starts at 1 when this
    /// transaction is created (via either SqlClient API or SQL-text
    /// <c>BEGIN TRANSACTION</c>). Each subsequent SQL-text <c>BEGIN</c>
    /// increments it; each SQL-text <c>COMMIT</c> decrements; only when it
    /// reaches 0 does the transaction actually commit. <c>ROLLBACK</c>
    /// (without a savepoint name) zeroes it regardless of depth — matches
    /// SQL Server's documented behavior (probe-confirmed 2026-05-08).
    /// </summary>
    internal int TranCount = 1;

    /// <summary>
    /// Savepoint name → log position at the time of <c>SAVE TRANSACTION</c>.
    /// EF Core 10's <c>RelationalTransaction.CreateSavepoint</c> emits
    /// <c>SAVE TRANSACTION &lt;name&gt;</c> per SaveChanges call inside an
    /// active <c>Database.BeginTransaction</c>, then on a failed save
    /// emits <c>ROLLBACK TRANSACTION &lt;name&gt;</c> to undo just that
    /// SaveChanges' writes. Names are case-insensitive (T-SQL identifiers);
    /// re-saving the same name overwrites the prior marker (matches SQL
    /// Server's documented behavior).
    /// </summary>
    internal readonly Dictionary<string, int> Savepoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transaction-scoped lock holds: data X locks (acquired by DML
    /// targets while this transaction is active) and HOLDLOCK-upgraded
    /// S locks (acquired by reads that opted into "hold until tx end").
    /// Released at <see cref="Commit"/> / <see cref="Rollback"/> /
    /// <see cref="Dispose"/> — matching SQL Server's "X locks released
    /// at transaction end under READ COMMITTED" rule, probe-confirmed.
    /// Savepoint partial-rollback does NOT release these (real SQL
    /// Server keeps locks across savepoint rollback — probe-confirmed
    /// via the EF SaveChanges path).
    /// </summary>
    internal readonly List<(LockResource Resource, LockMode Mode)> HeldLocks = [];

    /// <summary>
    /// Per-table count of currently-held tx-scoped row locks (row-X, row-U,
    /// row-S-tx-scoped). Bumped at every row-lock acquire site; when a
    /// table's count exceeds <see cref="RowLockEscalationThreshold"/>, the
    /// acquire site promotes to a single table-X (releasing every row lock
    /// it had previously held on that table). Matches real SQL Server's
    /// lock-escalation behavior at the same ~5000-locks-per-table threshold
    /// (probe-confirmed: real SQL Server defaults to escalation enabled with
    /// the threshold around 5000; the exact value can vary by version /
    /// memory pressure).
    /// </summary>
    internal readonly Dictionary<HeapTable, int> RowLockCountsByTable = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Set of tables whose per-tx row locks have been escalated to a single
    /// table-X. Once a table is in this set, subsequent row-X acquires on
    /// it short-circuit (the table-X already covers them) until COMMIT /
    /// ROLLBACK clears the state.
    /// </summary>
    internal readonly HashSet<HeapTable> EscalatedTables = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Row-count threshold above which a transaction's per-row X locks on
    /// a single table get promoted to a single table-X. Matches real SQL
    /// Server's ~5000 default; the simulator uses the same constant.
    /// </summary>
    internal const int RowLockEscalationThreshold = 5000;

    public override IsolationLevel IsolationLevel { get; } = isolationLevel;

    protected override DbConnection DbConnection => this.connection;

    /// <summary>
    /// The session's <see cref="SimulatedDbConnection.SessionIsolationLevel"/>
    /// value captured before this transaction overrode it (when the
    /// caller passed an explicit non-<see cref="IsolationLevel.Unspecified"/>
    /// level to <c>BeginTransaction</c>). Restored on
    /// <see cref="Commit"/> / <see cref="Rollback"/> / dispose so the
    /// session-wide setting survives the transaction's lifetime.
    /// </summary>
    internal IsolationLevel PreviousSessionIsolationLevel;

    /// <summary>
    /// Whether <see cref="SimulatedDbConnection"/>'s BeginDbTransaction
    /// actually overrode the session iso (true) or left it untouched
    /// (false — caller passed <see cref="IsolationLevel.Unspecified"/>).
    /// Drives the restore step on <see cref="Commit"/> / <see cref="Rollback"/>
    /// / dispose.
    /// </summary>
    internal bool OverrodeSessionIsolation;

    /// <summary>
    /// Stable per-transaction snapshot timestamp used by SNAPSHOT-isolation
    /// readers. Allocated lazily at the first user-table access inside the
    /// transaction (via <see cref="Database.CurrentTransactionCommitId"/>),
    /// immutable for the transaction's lifetime, and consulted by every
    /// subsequent SI read to walk the row's version chain. Null while the
    /// transaction has not yet read any user table; null for transactions
    /// whose iso is not SNAPSHOT.
    /// </summary>
    internal long? SnapshotXid;

    /// <summary>
    /// Pending version-store entries captured by INSERT / UPDATE / DELETE
    /// during this transaction. <see cref="Commit"/> hands the list to
    /// <see cref="VersionStore.FinalizePendingEntries"/> which
    /// stamps each entry with the commit Xid and propagates payloads into
    /// the per-table <see cref="HeapTable.RowVersions"/>;
    /// <see cref="Rollback"/> hands the list to
    /// <see cref="VersionStore.DiscardPendingEntries"/> which clears
    /// the in-flight writer marks without touching the heap (the undo log
    /// has already restored it).
    /// </summary>
    internal readonly List<PendingVersionEntry> PendingVersionEntries = [];

    /// <summary>
    /// True once <see cref="Commit"/> or <see cref="Rollback"/> has run.
    /// Subsequent calls are no-ops; <see cref="Dispose"/> uses this to skip
    /// the implicit rollback that fires for a transaction left "open" at
    /// disposal time (matches SqlClient's <c>SqlTransaction</c> behavior).
    /// </summary>
    private bool finished;

    public override void Commit()
    {
        if (this.finished)
            throw new InvalidOperationException("This SqlTransaction has completed; it is no longer usable.");
        this.TranCount--;
        if (this.TranCount > 0)
            return;
        var db = this.connection.CurrentDatabase;
        Storage.VersionStore.FinalizePendingEntries(this.PendingVersionEntries, db);
        this.UndoLog.Clear();
        ReleaseAllLocks();
        UnregisterActiveSnapshot(db);
        Storage.VersionStore.RunGarbageCollection(db);
        RestoreSessionIsolation();
        this.connection.CurrentTransaction = null;
        this.finished = true;
    }

    public override void Rollback()
    {
        if (this.finished)
            throw new InvalidOperationException("This SqlTransaction has completed; it is no longer usable.");
        var db = this.connection.CurrentDatabase;
        Storage.VersionStore.DiscardPendingEntries(this.PendingVersionEntries);
        this.UndoLog.Rollback();
        this.TranCount = 0;
        ReleaseAllLocks();
        UnregisterActiveSnapshot(db);
        Storage.VersionStore.RunGarbageCollection(db);
        RestoreSessionIsolation();
        this.connection.CurrentTransaction = null;
        this.finished = true;
    }

    /// <summary>
    /// SqlClient's <c>SqlTransaction</c> auto-rolls-back on dispose if
    /// neither <see cref="Commit"/> nor <see cref="Rollback"/> ran. Mirrors
    /// the standard <c>using var tx = ...; ... tx.Commit();</c> pattern
    /// where an exception before the commit triggers implicit rollback.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.finished)
        {
            var db = this.connection.CurrentDatabase;
            Storage.VersionStore.DiscardPendingEntries(this.PendingVersionEntries);
            this.UndoLog.Rollback();
            ReleaseAllLocks();
            UnregisterActiveSnapshot(db);
            Storage.VersionStore.RunGarbageCollection(db);
            RestoreSessionIsolation();
            this.connection.CurrentTransaction = null;
            this.finished = true;
        }
        base.Dispose(disposing);
    }

    private void UnregisterActiveSnapshot(Database db)
    {
        if (this.SnapshotXid is not null)
            _ = db.ActiveSnapshotTxs.TryRemove(this, out _);
    }

    private void RestoreSessionIsolation()
    {
        if (this.OverrodeSessionIsolation)
            this.connection.SessionIsolationLevel = this.PreviousSessionIsolationLevel;
    }

    /// <summary>
    /// Releases every entry in <see cref="HeldLocks"/> in reverse
    /// acquisition order against the manager's gate. Called by
    /// <see cref="Commit"/> / <see cref="Rollback"/> / dispose-implicit-
    /// rollback. Safe to call multiple times (the list clears between
    /// calls). LIFO discipline matches structured-locking convention; the
    /// manager pulses every waiter on each release so order doesn't affect
    /// correctness, just style.
    /// </summary>
    private void ReleaseAllLocks()
    {
        var manager = this.simulation.LockManager;
        for (var i = this.HeldLocks.Count - 1; i >= 0; i--)
        {
            var (resource, mode) = this.HeldLocks[i];
            manager.Release(resource, mode, this.connection);
        }
        this.HeldLocks.Clear();
        this.RowLockCountsByTable.Clear();
        this.EscalatedTables.Clear();
    }
}
