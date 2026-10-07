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
        this.UndoLog = new(simulation.LobReclamation);
        this.TransactionId = simulation.AllocateTransactionId();
        this.target = this;
        this.LockOwner = connection.Session;
        connection.LastBegunTransactionId = this.TransactionId;
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
        this.UndoLog = enlisted.UndoLog;
        this.TransactionId = enlisted.TransactionId;
        this.target = enlisted;
        this.LockOwner = enlisted.LockOwner;
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
    internal readonly UndoLog UndoLog;

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

    /// <summary>Whether a savepoint is set, which keeps the transaction from being promoted to a distributed one.</summary>
    internal bool HasSavepoint => this.savepoints.Count > 0;

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
            VersionStore.DiscardPendingEntries(undone, kept: this.PendingVersionEntries);
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
    internal readonly List<(LockResource Resource, LockMode Mode, SessionToken Owner)> HeldLocks = [];

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
    /// the per-table <see cref="Heap.RowVersions"/>;
    /// <see cref="Rollback()"/> hands the list to
    /// <see cref="VersionStore.DiscardPendingEntries"/> which clears
    /// the in-flight writer marks without touching the heap (the undo log
    /// has already restored it).
    /// </summary>
    internal readonly List<PendingVersionEntry> PendingVersionEntries = [];

    /// <summary>
    /// The tables this transaction created or redefined, which its commit
    /// stamps (<see cref="HeapTable.DefinitionXid"/>) so an older snapshot
    /// can't reach them; a rollback forgets them. Null until one is.
    /// </summary>
    internal List<HeapTable>? DefinitionChanges;

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
    /// Set on the transaction a request carries on in after another ended its
    /// own, whose beginning and end the TDS endpoint doesn't announce (see
    /// <see cref="SimulatedDbConnection.StartUnannouncedTransaction"/>).
    /// </summary>
    internal bool Unannounced;

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
        using var culture = CultureScope.Engine();
        this.ZombieCheck();
        this.Owner.RefuseApiRequest();
        this.apiCompleted = true;
        if (this.target.TranCount > 1)
        {
            this.target.TranCount--;
            return;
        }
        this.Owner.RefuseApiTransactionOperation(this.target, state: 1);
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
        using var culture = CultureScope.Engine();
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
        using var culture = CultureScope.Engine();
        this.ZombieCheck();
        RejectEmptyName(savepointName);
        this.Owner.RefuseApiRequest();
        this.Owner.RefuseApiTransactionOperation(this.target, state: 2);
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
        using var culture = CultureScope.Engine();
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
        Storage.VersionStore.FinalizePendingEntries(this.PendingVersionEntries, this.simulation, this.DefinitionChanges);
        this.DefinitionChanges = null;
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
        this.ReleaseMembers();
        this.Owner.RecordTransactionEvent(TransactionEvent.Commit, this);
        this.CloseEnlistedSessions();
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
        // A loopback server's call rolling back the caller's transaction it
        // runs in — a ROLLBACK, an error XACT_ABORT promotes, a refused COMMIT
        // — leaves it, the transaction doomed for the caller with its work in
        // place until the caller's batch ends it (probed 2026-10-07 against
        // SQL Server 2025).
        if (this.RunningEnlistedMember() is { } enlisted)
        {
            this.Doomed = true;
            _ = this.Detach(enlisted);
            return;
        }
        var db = this.Owner.CurrentDatabase;
        // The heap rewinds first: until the pending versions go, a snapshot
        // reads past the rolled-back rows to the versions they superseded.
        this.UndoLog.Rollback();
        Storage.VersionStore.DiscardPendingEntries(this.PendingVersionEntries);
        this.DefinitionChanges = null;
        this.TranCount = 0;
        this.CloseCursorsOnEnd();
        ReleaseAllLocks();
        UnregisterActiveSnapshot();
        Storage.VersionStore.RunGarbageCollection(this.simulation, db);
        this.ReleaseMembers();
        this.Owner.RecordTransactionEvent(cause, this);
        this.Owner.NoteTransactionRolledBack(this);
        this.CloseEnlistedSessions();
    }

    /// <summary>
    /// A deadlock victim's rollback when a <c>TRY</c> will catch its Msg
    /// 1205: the work undone and every lock released, so the deadlock the
    /// victim was chosen to break breaks, while the transaction stays open
    /// and doomed — <c>@@TRANCOUNT</c> unchanged, <c>XACT_STATE()</c> -1 —
    /// until a <c>ROLLBACK</c> ends it, or the batch's end with Msg 3998
    /// (probed 2026-10-03 against SQL Server 2025). Ended outright, as it
    /// once was, the <c>CATCH</c> read <c>@@TRANCOUNT</c> 0.
    /// </summary>
    internal void UndoAsDeadlockVictim()
    {
        var db = this.Owner.CurrentDatabase;
        this.UndoLog.Rollback();
        Storage.VersionStore.DiscardPendingEntries(this.PendingVersionEntries);
        this.DefinitionChanges = null;
        ReleaseAllLocks();
        UnregisterActiveSnapshot();
        this.SnapshotXid = null;
        Storage.VersionStore.RunGarbageCollection(this.simulation, db);
        this.Doomed = true;
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
            using var culture = CultureScope.Engine();
            this.apiCompleted = true;
            this.target.EndRollback();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The owner this transaction's locks and uncommitted writes are held
    /// under: the session that began it, which every session bound to it, or
    /// enlisted in it as a loopback server's, shares (see
    /// <see cref="SimulatedDbConnection.LockOwner"/>) — so none of them blocks
    /// on another's locks, and each reads the others' writes as its own, as
    /// real's bound sessions share one lock space (probed 2026-10-07 against
    /// SQL Server 2025). Replaced by a token of its own when that session
    /// leaves while others stay, its holds moving with it.
    /// </summary>
    internal SessionToken LockOwner;

    /// <summary>
    /// The sessions taking part in the transaction once a second one has
    /// joined it — the one that began it first while it stays — and null
    /// before; back to null once only that one is left.
    /// </summary>
    internal List<SimulatedDbConnection>? Members;

    /// <summary>The transaction, while sessions share it; null otherwise.</summary>
    internal SimulatedDbTransaction? WhenShared => this.Members is null ? null : this;

    /// <summary>
    /// The member whose <c>@@TRANCOUNT</c> <see cref="TranCount"/> holds: the
    /// one running, or the one that ran last. Each other keeps its own in
    /// <see cref="SimulatedDbConnection.BoundTranCount"/>.
    /// </summary>
    private SimulatedDbConnection? runner;

    /// <summary>
    /// The token <c>sp_getbindtoken</c> hands out for the transaction, the same
    /// for every call while it lasts (probed 2026-10-07 against SQL Server
    /// 2025); null until one is asked for.
    /// </summary>
    internal string? BindToken;

    /// <summary>
    /// The databases whose tables the transaction has locked anything in,
    /// whose shared database lock real holds to the transaction's end — a
    /// read under READ COMMITTED's included, whose own locks are gone with its
    /// statement (probed 2026-10-07 against SQL Server 2025). Replaced whole on
    /// each change, since another session's lock DMV reads it.
    /// </summary>
    internal Database[] TouchedDatabases = [];

    /// <summary>Notes the database of the table <paramref name="resource"/> locks, if it locks one.</summary>
    internal void NoteDatabase(LockResource resource)
    {
        if (resource.OwningTable?.OwningDatabase is { } database && Array.IndexOf(this.TouchedDatabases, database) < 0)
            this.TouchedDatabases = [.. this.TouchedDatabases, database];
    }

    /// <summary>Serializes the members' runs and every change to <see cref="Members"/>.</summary>
    private readonly object membersGate = new();

    /// <summary>
    /// The member running now when it is a loopback server's session a remote
    /// call enlisted; null otherwise.
    /// </summary>
    private SimulatedDbConnection? RunningEnlistedMember()
    {
        lock (this.membersGate)
            return this.Members is not null && this.runner is { Membership: TransactionMembership.Enlisted } enlisted ? enlisted : null;
    }

    /// <summary>
    /// The loopback servers' sessions the transaction's remote calls ran in,
    /// each kept between calls: real's provider runs every call of one
    /// transaction to a server in one session, whose temporary tables and
    /// <c>CONTEXT_INFO</c> carry from call to call (probed 2026-10-07 against
    /// SQL Server 2025). Closed as the transaction ends; null until a call
    /// ran. A session out on a call isn't listed.
    /// </summary>
    private Dictionary<LinkedServer, SimulatedDbConnection>? enlistedSessions;

    /// <summary>
    /// Takes the session an earlier call of the transaction ran in on
    /// <paramref name="server"/>, for the next call to run in; null when none
    /// ran.
    /// </summary>
    internal SimulatedDbConnection? TakeEnlistedSession(LinkedServer server)
    {
        lock (this.membersGate)
            return this.enlistedSessions is { } sessions && sessions.Remove(server, out var session) ? session : null;
    }

    /// <summary>
    /// Keeps <paramref name="session"/> for the transaction's next call to
    /// <paramref name="server"/>; false once the transaction has ended, when
    /// the caller closes it instead.
    /// </summary>
    internal bool KeepEnlistedSession(LinkedServer server, SimulatedDbConnection session)
    {
        lock (this.membersGate)
        {
            if (this.Ended)
                return false;
            (this.enlistedSessions ??= [])[server] = session;
            return true;
        }
    }

    /// <summary>
    /// The session a remote call of the transaction ran in on
    /// <paramref name="server"/>, which the provider's read of that server
    /// meets and can't resume the transaction in (see
    /// <see cref="SimulatedSqlException.CannotResumeTransaction"/>); null when
    /// no call ran.
    /// </summary>
    internal SimulatedDbConnection? EnlistedSessionOn(LinkedServer server)
    {
        lock (this.membersGate)
            return this.enlistedSessions is { } sessions && sessions.TryGetValue(server, out var session) ? session : null;
    }

    private void CloseEnlistedSessions()
    {
        Dictionary<LinkedServer, SimulatedDbConnection>? sessions;
        lock (this.membersGate)
        {
            sessions = this.enlistedSessions;
            this.enlistedSessions = null;
        }
        if (sessions is null)
            return;
        foreach (var (_, session) in sessions)
            session.Dispose();
    }

    /// <summary>Whether <paramref name="connection"/> is the member whose count <see cref="TranCount"/> holds.</summary>
    internal bool IsRunBy(SimulatedDbConnection connection) => ReferenceEquals(this.runner ?? this.Owner, connection);

    /// <summary>
    /// The first session still in the transaction when <paramref name="connection"/>
    /// shares it with another, whose lock workspace real lists the database
    /// locks of all of them under; null for a transaction no one shares.
    /// </summary>
    internal SimulatedDbConnection? FirstMember(SimulatedDbConnection connection)
    {
        var members = this.Members;
        if (members is null)
            return null;
        lock (this.membersGate)
            return members.Contains(connection) && members.Count > 0 ? members[0] : null;
    }

    /// <summary>
    /// Brings <paramref name="connection"/> into the transaction as
    /// <paramref name="membership"/> says, on a <c>@@TRANCOUNT</c> of its own
    /// starting at 1. Waits until no other member is running, unless the
    /// caller runs inside one — a loopback server's session enlisting in its
    /// caller's transaction — and makes the newcomer the running member when
    /// <paramref name="running"/>, as <c>sp_bindsession</c>'s caller is.
    /// </summary>
    internal void Attach(SimulatedDbConnection connection, TransactionMembership membership, bool running)
    {
        lock (this.membersGate)
        {
            if (this.Ended)
                return;
            if (this.Members is null)
            {
                this.Members = [this.Owner];
                this.runner = this.Owner;
            }
            this.Members.Add(connection);
            connection.CurrentTransaction = this;
            connection.SharedLockOwner = this.LockOwner;
            connection.Membership = membership;
            connection.BoundTranCount = 1;
            if (!running)
                return;
            this.WaitForOtherMembers(connection, running: true);
            // Another member may have ended it meanwhile, taking this one out.
            if (!this.Ended)
                this.RunAs(connection);
        }
    }

    /// <summary>
    /// Takes <paramref name="connection"/> out of the transaction, which goes
    /// on for the members left: <c>sp_bindsession</c> with no token, a bound
    /// session closing, a loopback server's call returning (probed 2026-10-07
    /// against SQL Server 2025: the transaction outlives the session that began
    /// it). When that session leaves, its holds and uncommitted writes pass to
    /// a token of the transaction's own, since the session goes on to work of
    /// its own under its token. Ending the last member's part ends nothing:
    /// the caller rolls back a transaction no one is left in, which this
    /// answers false for.
    /// </summary>
    internal bool Detach(SimulatedDbConnection connection)
    {
        lock (this.membersGate)
        {
            if (this.Members is not { } members || !members.Contains(connection))
                return true;
            // The holds move only between other members' statements, whose
            // locks are being taken under the owner they leave.
            if (ReferenceEquals(connection.Session, this.LockOwner))
                this.WaitForOtherMembers(connection, running: false);
            _ = members.Remove(connection);
            if (ReferenceEquals(this.runner, connection))
            {
                connection.BoundTranCount = this.TranCount;
                this.runner = null;
            }
            ReleaseMember(connection);
            if (members.Count == 0)
                return false;
            if (ReferenceEquals(connection.Session, this.LockOwner))
                this.PassLockOwnership();
            else if (ReferenceEquals(this.LockOwner.RunningMember, connection.Session))
                this.LockOwner.RunningMember = members[0].Session;
            if (members.Count == 1 && ReferenceEquals(members[0], this.Owner) && ReferenceEquals(this.LockOwner, this.Owner.Session))
            {
                // Only the session that began it is left: no one shares it.
                if (this.runner is null)
                    this.TranCount = this.Owner.BoundTranCount;
                this.runner = null;
                this.Members = null;
                this.LockOwner.RunningMember = null;
            }
            return true;
        }
    }

    /// <summary>
    /// The session that began the transaction leaves while others stay: its
    /// holds, the rows it deleted or rewrote, its uncommitted row versions and
    /// its snapshot registration move to a token of the transaction's own,
    /// acting as the first member left. That session is between requests —
    /// its own <c>sp_bindsession</c>, or its session ending — so none of its
    /// statement's locks is among them.
    /// </summary>
    private void PassLockOwnership()
    {
        var from = this.LockOwner;
        var to = new SessionToken(from.Spid) { RunningMember = this.Members![0].Session };
        this.simulation.LockManager.TransferHolds(this.HeldLocks, from, to);
        HashSet<HeapTable> tables = new(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < this.HeldLocks.Count; i++)
        {
            var (resource, mode, owner) = this.HeldLocks[i];
            if (ReferenceEquals(owner, from))
                this.HeldLocks[i] = (resource, mode, to);
            if (resource.OwningTable is { } table)
                _ = tables.Add(table);
        }
        foreach (var table in tables)
        {
            if (table.SupersededKeyImages.TryRemove(from, out var images))
                table.SupersededKeyImages[to] = images;
        }
        foreach (var entry in this.PendingVersionEntries)
        {
            lock (entry.Table.RowVersionsGate)
            {
                if (entry.Heap.RowVersions.TryGetValue(entry.NewRid, out var chain) && ReferenceEquals(chain.WriterSession, from))
                    chain.WriterSession = to;
            }
        }
        if (this.SnapshotXid is not null && this.simulation.ActiveSnapshotTxs.TryRemove(from, out var registration))
            this.simulation.ActiveSnapshotTxs[to] = registration;
        this.LockOwner = to;
        foreach (var member in this.Members!)
            member.SharedLockOwner = to;
        from.RunningMember = null;
    }

    /// <summary>
    /// Takes <paramref name="connection"/>'s part out of the transaction, its
    /// own count kept for nothing: the transaction no longer names it.
    /// </summary>
    private static void ReleaseMember(SimulatedDbConnection connection)
    {
        connection.CurrentTransaction = null;
        connection.SharedLockOwner = null;
        connection.Membership = TransactionMembership.None;
    }

    /// <summary>
    /// The transaction ends: every session in it leaves it, and each but
    /// the one that ended it reports so with Msg 3926 at its next batch, as
    /// real's does (probed 2026-10-07 against SQL Server 2025). A loopback
    /// server's enlisted session hears nothing — its call returns into the
    /// caller.
    /// </summary>
    private void ReleaseMembers()
    {
        lock (this.membersGate)
        {
            // Under the gate, so no session attaches to it once it has ended.
            this.Ended = true;
            if (this.Members is not { } members)
            {
                if (ReferenceEquals(this.Owner.CurrentTransaction, this))
                    this.Owner.CurrentTransaction = null;
                return;
            }
            var ender = this.runner ?? this.Owner;
            foreach (var member in members)
            {
                if (!ReferenceEquals(member.CurrentTransaction, this))
                    continue;
                if (!ReferenceEquals(member, ender) && member.Membership != TransactionMembership.Enlisted)
                    member.EndedUnderBinding = this;
                ReleaseMember(member);
            }
            this.LockOwner.RunningMember = null;
        }
    }

    /// <summary>
    /// Begins a stretch of <paramref name="connection"/>'s execution in the
    /// shared transaction: waits until no other member is running — real runs
    /// them side by side, but they share one undo log and lock owner here —
    /// and swaps its <c>@@TRANCOUNT</c> in. Returns the member that was
    /// running, for <see cref="LeaveExecution"/> to hand back to when this
    /// stretch ran inside it.
    /// </summary>
    internal SimulatedDbConnection? EnterExecution(SimulatedDbConnection connection)
    {
        lock (this.membersGate)
        {
            if (this.Members is not { } members || !members.Contains(connection))
                return null;
            var previous = this.runner;
            this.WaitForOtherMembers(connection, running: true);
            this.RunAs(connection);
            return previous;
        }
    }

    /// <summary>
    /// Ends a stretch <see cref="EnterExecution"/> began: keeps the member's
    /// count, and hands the transaction back to <paramref name="previous"/>
    /// when the stretch ran inside it — a loopback server's call returning
    /// into its caller.
    /// </summary>
    internal void LeaveExecution(SimulatedDbConnection connection, SimulatedDbConnection? previous)
    {
        lock (this.membersGate)
        {
            if (this.Members is { } members && previous is not null && !ReferenceEquals(previous, connection)
                && Volatile.Read(ref previous.Session.ExecutingStretches) > 0 && members.Contains(previous))
            {
                this.RunAs(previous);
            }
            Monitor.PulseAll(this.membersGate);
        }
    }

    /// <summary>
    /// Makes <paramref name="connection"/> the running member: its count into
    /// <see cref="TranCount"/>, the last runner's back to its own, and the
    /// shared owner acting as it.
    /// </summary>
    private void RunAs(SimulatedDbConnection connection)
    {
        if (!ReferenceEquals(this.runner, connection))
        {
            if (this.runner is { } last)
                last.BoundTranCount = this.TranCount;
            this.TranCount = connection.BoundTranCount;
            this.runner = connection;
        }
        this.LockOwner.RunningMember = connection.Session;
    }

    /// <summary>
    /// Waits, holding <see cref="membersGate"/> between slices, until no member
    /// but <paramref name="connection"/> is running — a member running on this
    /// thread is the one this execution is nested in, and doesn't count. A
    /// <paramref name="running"/> connection, waiting to run, observes its
    /// cancellation as a lock wait does and doesn't count as running while it
    /// waits, so a member waiting on it can go first.
    /// </summary>
    private void WaitForOtherMembers(SimulatedDbConnection connection, bool running)
    {
        var thread = Environment.CurrentManagedThreadId;
        while (true)
        {
            var busy = false;
            foreach (var member in this.Members!)
            {
                if (!ReferenceEquals(member, connection) && Volatile.Read(ref member.Session.ExecutingStretches) > 0
                    && member.Session.CurrentExecutingThreadId != thread)
                {
                    busy = true;
                    break;
                }
            }
            if (!busy)
                return;
            if (!running)
            {
                _ = Monitor.Wait(this.membersGate, 25);
                continue;
            }
            _ = Interlocked.Decrement(ref connection.Session.ExecutingStretches);
            try
            {
                _ = Monitor.Wait(this.membersGate, 25);
            }
            finally
            {
                _ = Interlocked.Increment(ref connection.Session.ExecutingStretches);
            }
            if (connection.ExecutionCancellationToken.IsCancellationRequested)
            {
                throw connection.ExecutionTimedOut
                    ? SimulatedSqlException.ExecutionTimeoutExpired()
                    : SimulatedSqlException.CommandCancelled();
            }
        }
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
            _ = this.simulation.ActiveSnapshotTxs.TryRemove(this.LockOwner, out _);
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
            var (resource, mode, owner) = this.HeldLocks[i];
            manager.Release(resource, mode, owner);
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

/// <summary>
/// How a session takes part in a transaction another session began, as
/// <c>sys.dm_tran_session_transactions</c> reports it (probed 2026-10-07
/// against SQL Server 2025).
/// </summary>
internal enum TransactionMembership : byte
{
    /// <summary>The session's own transaction, or none: <c>is_local</c> 1, <c>is_bound</c> 0.</summary>
    None,

    /// <summary>Bound through <c>sp_bindsession</c>: <c>is_local</c> 0, <c>is_bound</c> 1.</summary>
    Bound,

    /// <summary>A loopback server's session a remote call enlisted: <c>is_local</c> 0, <c>is_bound</c> 0.</summary>
    Enlisted,
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

/// <summary>
/// A token <c>sp_getbindtoken</c> handed out: the transaction it names, held
/// weakly, and the session that began that transaction, whose presence decides
/// how <c>sp_bindsession</c> answers once the transaction has ended.
/// </summary>
internal sealed class BindTokenIssue(WeakReference<SimulatedDbTransaction> transaction, SessionToken issuer)
{
    public readonly WeakReference<SimulatedDbTransaction> Transaction = transaction;

    public readonly SessionToken Issuer = issuer;
}
