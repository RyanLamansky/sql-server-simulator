using System.Data;
using System.Data.Common;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="DbTransaction"/> for the simulator's command pipeline. Adds a
/// strongly-typed <see cref="Connection"/> shadow so consumers who downcast
/// a base-typed <see cref="DbTransaction"/> stay in <c>Simulated*</c> shapes
/// — same pattern <c>SqlTransaction</c> follows against <c>DbTransaction</c>.
/// Instances are created via
/// <see cref="SimulatedDbConnection.BeginTransaction()"/>.
/// </summary>
public sealed class SimulatedDbTransaction : DbTransaction
{
    internal SimulatedDbTransaction(Simulation simulation, SimulatedDbConnection connection, IsolationLevel isolationLevel)
    {
        this.simulation = simulation;
        this.Owner = connection;
        this.IsolationLevel = isolationLevel;
        this.TransactionId = simulation.AllocateTransactionId();
        this.target = this;
        connection.RecordTransactionEvent(TransactionEvent.Begin, this);
    }

    /// <summary>
    /// The object <c>BeginTransaction</c> returns while a transaction begun by
    /// SQL text — <c>BEGIN TRANSACTION</c> or <c>IMPLICIT_TRANSACTIONS</c> — is
    /// open: it nests one level inside <paramref name="enlisted"/>, as
    /// SqlClient's transaction-manager begin does.
    /// </summary>
    private SimulatedDbTransaction(SimulatedDbTransaction enlisted, IsolationLevel isolationLevel)
    {
        this.simulation = enlisted.simulation;
        this.Owner = enlisted.Owner;
        this.IsolationLevel = isolationLevel;
        this.TransactionId = enlisted.TransactionId;
        this.target = enlisted;
    }

    internal readonly Simulation simulation;

    /// <summary>The session the transaction belongs to, whatever state it is in.</summary>
    internal readonly SimulatedDbConnection Owner;

    /// <summary>
    /// The server transaction the API's commit and rollback act on: this one,
    /// or for a transaction <c>BeginTransaction</c> nested inside one SQL text
    /// began, that one.
    /// </summary>
    private readonly SimulatedDbTransaction target;

    /// <summary>
    /// The nested API transaction (see <see cref="Nest"/>) opened inside this
    /// one and not yet committed or rolled back through the API, which a
    /// second <c>BeginTransaction</c> refuses to run beside.
    /// </summary>
#pragma warning disable CA2213 // An alias of an object the caller of BeginTransaction owns and disposes.
    private SimulatedDbTransaction? nestedApiTransaction;
#pragma warning restore CA2213

    /// <summary>
    /// Set when <c>BeginTransaction</c> began this transaction, as opposed to
    /// SQL text.
    /// </summary>
    internal bool BegunByApi;

    /// <summary>
    /// Set when <c>SET IMPLICIT_TRANSACTIONS ON</c> opened this transaction,
    /// which <c>DBCC OPENTRAN</c> names <c>implicit_transaction</c>.
    /// </summary>
    internal bool BegunImplicitly;

    /// <summary>
    /// The databases whose catalog this transaction has changed — a
    /// <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c> run inside it — which
    /// <c>DBCC OPENTRAN</c> counts as written to. Locked on itself, since
    /// another session reads it.
    /// </summary>
    internal readonly HashSet<Database> CatalogChanges = [];

    /// <summary>
    /// The heap modification clock as this transaction began: a heap whose
    /// <c>LastModifiedEpoch</c> is at or past it changed while the transaction
    /// was open.
    /// </summary>
    internal readonly long BeginEpoch = Interlocked.Increment(ref Heap.ModificationEpoch);

    /// <summary>
    /// The id <c>CURRENT_TRANSACTION_ID()</c> and the transaction DMVs report,
    /// drawn at BEGIN; the TDS endpoint's transaction ENVCHANGE descriptor too.
    /// </summary>
    internal readonly long TransactionId;

    /// <summary>When the transaction began, <c>sys.dm_tran_active_transactions.transaction_begin_time</c>.</summary>
    internal readonly DateTime BeginTimeUtc = DateTime.UtcNow;

    /// <summary>
    /// Cross-statement undo log for this transaction. Statements executed
    /// while this is the connection's active transaction append entries
    /// here; <see cref="Rollback()"/> walks the log backwards. <see cref="Commit"/>
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
    /// Set once a <c>BEGIN TRAN &lt;name&gt; WITH MARK</c> has placed a mark on
    /// this transaction. The mark itself is a log artifact with no home here —
    /// nothing reads it back — but a second <c>WITH MARK</c> under the same
    /// transaction earns real's severity-10 Msg 3920, and this is what makes
    /// that reachable.
    /// </summary>
    internal bool IsMarked;

    /// <summary>
    /// Set by <c>BEGIN DISTRIBUTED TRANSACTION</c>, at any nesting level: a
    /// linked server's read then enlists in the transaction as a write always
    /// does, which is what reaches the coordinator's refusal (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal bool IsDistributed;

    /// <summary>
    /// The name the outermost <c>BEGIN TRAN</c> gave the transaction, or null.
    /// <c>ROLLBACK TRAN</c> naming it rolls the whole transaction back; a
    /// nested BEGIN's name is not recorded, so naming that one is Msg 6401.
    /// Matched ordinally — real refuses <c>outer1</c> for <c>Outer1</c>
    /// under a case-insensitive collation (probe-confirmed against SQL
    /// Server 2025).
    /// </summary>
    internal string? Name;

    /// <summary>
    /// SQL Server's uncommittable ("doomed") transaction state: set when an
    /// error raised under <c>SET XACT_ABORT ON</c> was caught by a
    /// <c>TRY</c> frame instead of ending the batch. The transaction stays
    /// open — <c>@@TRANCOUNT</c> is unchanged — but <c>XACT_STATE()</c> reads
    /// <c>-1</c>, any statement that would write to the log raises Msg 3930,
    /// and reaching the end of the batch with the flag still set raises
    /// Msg 3998 and rolls back. Only <c>ROLLBACK</c> clears it, by ending the
    /// transaction. Probe-confirmed against SQL Server 2025.
    /// </summary>
    internal bool Doomed;

    /// <summary>
    /// The savepoints <c>SAVE TRANSACTION</c> or <see cref="Save"/> set, oldest
    /// first. EF Core 10's <c>RelationalTransaction.CreateSavepoint</c> emits
    /// <c>SAVE TRANSACTION &lt;name&gt;</c> per SaveChanges call inside an
    /// active <c>Database.BeginTransaction</c>, then on a failed save emits
    /// <c>ROLLBACK TRANSACTION &lt;name&gt;</c> to undo just that SaveChanges'
    /// writes. A stack, as real's: saving a name again stacks a second
    /// savepoint beside the first rather than moving it, and a rollback to a
    /// name returns to its newest savepoint and consumes it along with every
    /// later one (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private readonly List<Savepoint> savepoints = [];

    /// <summary>One savepoint: the undo-log and pending-version positions it returns to.</summary>
    private readonly struct Savepoint(string name, int undoPosition, int versionEntryCount)
    {
        public readonly string Name = name;

        public readonly int UndoPosition = undoPosition;

        public readonly int VersionEntryCount = versionEntryCount;
    }

    /// <summary>Sets a savepoint named <paramref name="name"/> at the transaction's current position.</summary>
    internal void SetSavepoint(string name) =>
        this.savepoints.Add(new Savepoint(name, this.UndoLog.Position, this.PendingVersionEntries.Count));

    /// <summary>
    /// Rolls the transaction back to the newest savepoint named
    /// <paramref name="name"/> — compared case-insensitively — consuming it
    /// and every savepoint set after it, and keeps the transaction and its
    /// locks; false when no savepoint has the name. A doomed transaction can
    /// only be rolled back whole (Msg 3931).
    /// </summary>
    internal bool TryRollbackToSavepoint(string name)
    {
        var index = this.savepoints.FindLastIndex(savepoint => string.Equals(savepoint.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        if (this.Doomed)
            throw SimulatedSqlException.UncommittableTransactionCannotRollBackToSavepoint();
        var savepoint = this.savepoints[index];
        this.savepoints.RemoveRange(index, this.savepoints.Count - index);
        this.UndoLog.RollbackTo(savepoint.UndoPosition);
        if (this.PendingVersionEntries.Count > savepoint.VersionEntryCount)
        {
            var undone = this.PendingVersionEntries.GetRange(savepoint.VersionEntryCount, this.PendingVersionEntries.Count - savepoint.VersionEntryCount);
            this.PendingVersionEntries.RemoveRange(savepoint.VersionEntryCount, undone.Count);
            VersionStore.DiscardPendingEntries(undone);
        }
        return true;
    }

    /// <summary>
    /// Transaction-scoped lock holds: data X locks (acquired by DML
    /// targets while this transaction is active) and HOLDLOCK-upgraded
    /// S locks (acquired by reads that opted into "hold until tx end").
    /// Released at <see cref="Commit"/> / <see cref="Rollback()"/> /
    /// <see cref="Dispose"/> — matching SQL Server's "X locks released
    /// at transaction end under READ COMMITTED" rule, probe-confirmed.
    /// Savepoint partial-rollback does NOT release these (real SQL
    /// Server keeps locks across savepoint rollback — probe-confirmed
    /// via the EF SaveChanges path).
    /// </summary>
    internal readonly List<(LockResource Resource, LockMode Mode)> HeldLocks = [];

    /// <summary>
    /// Transaction-owned application locks (<c>sp_getapplock @LockOwner =
    /// 'Transaction'</c>), one entry per successful acquire. The manager
    /// holds themselves also ride <see cref="HeldLocks"/> (which releases
    /// them at transaction end); this parallel ledger carries the
    /// (principal, resource) identity the owner-scoped views need —
    /// <c>APPLOCK_MODE</c>, <c>sp_releaseapplock</c>'s not-held check, the
    /// <c>sys.dm_tran_locks</c> APPLICATION rows. Probe-confirmed lifecycle:
    /// released on COMMIT and full ROLLBACK, kept across
    /// rollback-to-savepoint.
    /// </summary>
    internal readonly List<AppLockHold> TransactionAppLocks = [];

    /// <summary>
    /// Tables whose row and key locks this transaction escalated to a single
    /// table X; a later row or key lock there short-circuits, the table X
    /// already covering it, until COMMIT / ROLLBACK clears the state.
    /// </summary>
    internal readonly HashSet<HeapTable> EscalatedTables = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Tables whose S-family row and key locks this transaction escalated to
    /// a single table S — a REPEATABLE READ or SERIALIZABLE read past the
    /// threshold. A later S there short-circuits; a U or X still takes its
    /// own lock.
    /// </summary>
    internal readonly HashSet<HeapTable> SharedEscalatedTables = new(ReferenceEqualityComparer.Instance);

    /// <inheritdoc/>
    public override IsolationLevel IsolationLevel { get; }

    /// <inheritdoc/>
    // Null once completed, as the public Connection reads.
    protected override DbConnection DbConnection => this.Connection!;

    /// <summary>
    /// Strongly-typed shadow over <see cref="DbTransaction.Connection"/>: null
    /// once the transaction has been committed or rolled back, through this
    /// object or by SQL text, as SqlClient's reads.
    /// </summary>
    public new SimulatedDbConnection? Connection => this.Zombied ? null : this.Owner;

    /// <summary>
    /// Stable per-transaction snapshot timestamp used by SNAPSHOT-isolation
    /// readers. Allocated lazily at the first user-table access inside the
    /// transaction (via <see cref="Simulation.CurrentTransactionCommitId"/>),
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
    /// <see cref="Rollback()"/> hands the list to
    /// <see cref="VersionStore.DiscardPendingEntries"/> which clears
    /// the in-flight writer marks without touching the heap (the undo log
    /// has already restored it).
    /// </summary>
    internal readonly List<PendingVersionEntry> PendingVersionEntries = [];

    /// <summary>
    /// The cursors opened while this transaction was the session's, which
    /// <c>SET CURSOR_CLOSE_ON_COMMIT ON</c> closes as it ends by commit or
    /// rollback; null until one opens.
    /// </summary>
    internal List<Cursor>? OpenedCursors;

    /// <summary>
    /// True once the transaction has ended, by commit or rollback from SQL
    /// text, the API or the engine.
    /// </summary>
    internal bool Ended;

    /// <summary>
    /// True once the API's <see cref="Commit"/>, <see cref="Rollback()"/> or
    /// dispose has run on this object, after which SqlClient's transaction is
    /// a zombie: <see cref="Connection"/> reads null and a second commit or
    /// rollback is refused.
    /// </summary>
#pragma warning disable IDE0032 // Set by the three API calls; a property would hide that it is plain state.
    private bool apiCompleted;
#pragma warning restore IDE0032

    /// <summary>
    /// Whether this object is past use through the API: completed through it,
    /// or its transaction ended some other way — a <c>COMMIT</c> or
    /// <c>ROLLBACK</c> in SQL text, an error that rolled it back (probed
    /// 2026-09-28 against SQL Server 2025 through SqlClient 7).
    /// </summary>
    private bool Zombied => this.apiCompleted || this.target.Ended;

    /// <summary>
    /// Whether <c>BeginTransaction</c> must refuse to open another transaction
    /// beside this open one: SqlClient refuses while the transaction it began
    /// or nested is still pending on the connection, including one whose
    /// commit through the API ended only an inner level of it, but nests
    /// inside a transaction SQL text began.
    /// </summary>
    internal bool HoldsApiTransaction => this.BegunByApi || this.nestedApiTransaction is { Zombied: false };

    /// <summary>
    /// Opens the API transaction <c>BeginTransaction</c> returns inside this
    /// SQL-text one: <c>@@TRANCOUNT</c> rises by one, the API commit ends only
    /// that level, the API rollback ends the whole transaction, and a requested
    /// isolation level applies to the session from here on (probed 2026-09-28
    /// against SQL Server 2025 through SqlClient 7).
    /// </summary>
    internal SimulatedDbTransaction Nest(IsolationLevel isolationLevel)
    {
        this.TranCount++;
        return this.nestedApiTransaction = new SimulatedDbTransaction(this, isolationLevel);
    }

    /// <summary>
    /// SqlClient's refusal of any use of a transaction past its end, which also
    /// completes the object, so a later <see cref="Rollback()"/> is refused too.
    /// </summary>
    private void ZombieCheck()
    {
        if (!this.Zombied)
            return;
        this.apiCompleted = true;
        throw new InvalidOperationException("This SqlTransaction has completed; it is no longer usable.");
    }

    /// <summary>SqlClient's refusal of a null or empty savepoint or transaction name.</summary>
    private static void RejectEmptyName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Invalid transaction or invalid name for a point at which to save within the transaction.");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Ends one nesting level when the transaction is nested deeper, as
    /// SqlClient's commit request does; either way this object is completed.
    /// </remarks>
    public override void Commit()
    {
        this.ZombieCheck();
        this.apiCompleted = true;
        if (this.target.TranCount > 1)
            this.target.TranCount--;
        else
            this.target.EndCommit();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A transaction SQL text or the engine already ended is completed
    /// silently by the first rollback, as SqlClient's is, where a commit, a
    /// savepoint call or a second rollback is refused (probed 2026-09-28
    /// against SQL Server 2025 through SqlClient 7).
    /// </remarks>
    public override void Rollback()
    {
        if (this.apiCompleted)
            throw new InvalidOperationException("This SqlTransaction has completed; it is no longer usable.");
        this.apiCompleted = true;
        if (!this.target.Ended)
            this.target.EndRollback();
    }

    /// <summary>
    /// Sets a savepoint, as SqlClient's <c>SqlTransaction.Save</c> does: the
    /// transaction-manager counterpart of <c>SAVE TRANSACTION</c>, which a
    /// later <see cref="Rollback(string)"/> returns to.
    /// </summary>
    /// <param name="savepointName">The savepoint's name, at most 32 characters.</param>
    /// <exception cref="ArgumentException"><paramref name="savepointName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The transaction has completed.</exception>
    public override void Save(string savepointName)
    {
        this.ZombieCheck();
        RejectEmptyName(savepointName);
        this.target.SetSavepointByName(savepointName);
    }

    /// <summary>
    /// The server half of a transaction-manager save request, shared by
    /// <see cref="Save"/> and the TDS endpoint: a name past 32 characters is
    /// Msg 103 at the request's own state, and a doomed transaction refuses
    /// the log write with Msg 3930.
    /// </summary>
    internal void SetSavepointByName(string name)
    {
        if (name.Length > MaxNameLength)
            throw SimulatedSqlException.TransactionNameTooLong(name, TransactionManagerNameState);
        if (this.Doomed)
            throw SimulatedSqlException.UncommittableTransactionCannotWrite();
        this.SetSavepoint(name);
    }

    /// <summary>
    /// Rolls back to the newest savepoint of that name, keeping the
    /// transaction open, or — naming the transaction a <c>BEGIN TRANSACTION</c>
    /// began — rolls the whole transaction back, as SqlClient's
    /// <c>SqlTransaction.Rollback(string)</c> does.
    /// </summary>
    /// <param name="savepointName">The savepoint's or the transaction's name.</param>
    /// <exception cref="ArgumentException"><paramref name="savepointName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The transaction has completed.</exception>
    public override void Rollback(string savepointName)
    {
        this.ZombieCheck();
        RejectEmptyName(savepointName);
        this.target.RollbackByName(savepointName);
    }

    /// <summary>
    /// The server half of a transaction-manager rollback request naming a
    /// savepoint or the transaction, shared by <see cref="Rollback(string)"/>
    /// and the TDS endpoint: a savepoint of that name wins over the
    /// transaction's own, which rolls the whole transaction back, and any
    /// other name is Msg 6401.
    /// </summary>
    internal void RollbackByName(string name)
    {
        if (name.Length > MaxNameLength)
            throw SimulatedSqlException.TransactionNameTooLong(name, TransactionManagerNameState);
        if (this.TryRollbackToSavepoint(name))
            return;
        if (!string.Equals(this.Name, name, StringComparison.Ordinal))
            throw SimulatedSqlException.CannotRollBackUnknownSavepoint(name);
        this.EndRollback(TransactionEvent.StatementRollback);
    }

    /// <summary>The longest transaction or savepoint name real accepts.</summary>
    internal const int MaxNameLength = 32;

    /// <summary>The state of Msg 103 for a name a transaction-manager request carries.</summary>
    internal const byte TransactionManagerNameState = 30;

    /// <summary>
    /// Commits the transaction outright, whatever its nesting depth: the
    /// outermost <c>COMMIT</c>, and the engine's own commits.
    /// </summary>
    internal void EndCommit()
    {
        var db = this.Owner.CurrentDatabase;
        Storage.VersionStore.FinalizePendingEntries(this.PendingVersionEntries, this.simulation);
        // Commit() (vs the former discard-only Clear) reclaims the off-row LOB
        // chains superseded by this tx's committed UPDATE/DELETEs in the
        // unversioned case; under SNAPSHOT/RCSI those chains are pinned by the
        // history entries FinalizePendingEntries just stamped and are reclaimed
        // instead by RunGarbageCollection below once no snapshot needs them.
        this.UndoLog.Commit();
        this.TranCount = 0;
        this.CloseCursorsOnEnd();
        ReleaseAllLocks();
        UnregisterActiveSnapshot();
        Storage.VersionStore.RunGarbageCollection(this.simulation, db);
        this.Owner.CurrentTransaction = null;
        this.Ended = true;
        this.Owner.RecordTransactionEvent(TransactionEvent.Commit, this);
    }

    /// <summary>
    /// Rolls the whole transaction back, whatever its nesting depth: a bare
    /// <c>ROLLBACK</c>, and every engine-initiated rollback.
    /// <paramref name="cause"/> tells the TDS endpoint a <c>ROLLBACK</c>
    /// statement's ending, which it reports where the statement ran, from the
    /// engine's, which it reports after the error that caused it.
    /// </summary>
    internal void EndRollback(TransactionEvent cause = TransactionEvent.Rollback)
    {
        var db = this.Owner.CurrentDatabase;
        Storage.VersionStore.DiscardPendingEntries(this.PendingVersionEntries);
        this.UndoLog.Rollback();
        this.TranCount = 0;
        this.CloseCursorsOnEnd();
        ReleaseAllLocks();
        UnregisterActiveSnapshot();
        Storage.VersionStore.RunGarbageCollection(this.simulation, db);
        this.Owner.CurrentTransaction = null;
        this.Ended = true;
        this.Owner.RecordTransactionEvent(cause, this);
    }

    /// <summary>
    /// SqlClient's <c>SqlTransaction</c> auto-rolls-back on dispose if
    /// neither <see cref="Commit"/> nor <see cref="Rollback()"/> ran — the whole
    /// transaction, a nested one's enclosing SQL-text transaction included.
    /// Mirrors the standard <c>using var tx = ...; ... tx.Commit();</c> pattern
    /// where an exception before the commit triggers implicit rollback.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.Zombied)
        {
            this.apiCompleted = true;
            this.target.EndRollback();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Under <c>SET CURSOR_CLOSE_ON_COMMIT ON</c>, closes the cursors this
    /// transaction opened that are still open — static ones included — as a
    /// <c>COMMIT</c> or <c>ROLLBACK</c> ends it; a rollback to a savepoint
    /// leaves them (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private void CloseCursorsOnEnd()
    {
        if (this.OpenedCursors is not { } cursors || !this.Owner.CursorCloseOnCommit)
            return;
        foreach (var cursor in cursors)
        {
            if (cursor.IsOpen)
                cursor.Close(this.Owner);
        }
    }

    private void UnregisterActiveSnapshot()
    {
        if (this.SnapshotXid is not null)
            _ = this.simulation.ActiveSnapshotTxs.TryRemove(this.Owner.Session, out _);
    }

    /// <summary>
    /// Releases every entry in <see cref="HeldLocks"/> in reverse
    /// acquisition order against the manager's gate. Called by
    /// <see cref="Commit"/> / <see cref="Rollback()"/> / dispose-implicit-
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
            manager.Release(resource, mode, this.Owner.Session);
        }
        this.HeldLocks.Clear();
        // Transaction-owned application locks release with the transaction —
        // their manager holds rode the HeldLocks entries above; only the
        // owner-view ledger needs clearing here.
        this.TransactionAppLocks.Clear();
        this.EscalatedTables.Clear();
        this.SharedEscalatedTables.Clear();
    }
}

/// <summary>A server transaction's beginning or ending, as the TDS endpoint reports it.</summary>
internal enum TransactionEvent : byte
{
    Begin,
    Commit,

    /// <summary>A rollback the engine made, reported after the error that caused it.</summary>
    Rollback,

    /// <summary>A rollback a <c>ROLLBACK</c> statement or request made.</summary>
    StatementRollback,
}
