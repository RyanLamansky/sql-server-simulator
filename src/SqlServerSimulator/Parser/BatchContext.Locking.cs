using SqlServerSimulator.Storage;
using System.Runtime.CompilerServices;

namespace SqlServerSimulator.Parser;

// The batch's side of the lock manager: the schema and statement locks it
// takes and releases, row and key locks with their waits on writers and
// deleters, escalation, SERIALIZABLE fences, and the snapshot a read is judged
// against.
internal sealed partial class BatchContext
{
    /// <summary>
    /// Schema-stability and schema-modification locks acquired during the
    /// current statement's dispatch. Each successful TryResolve*-side
    /// acquisition (Sch-S on the resolved schema object) and each DDL-side
    /// acquisition (Sch-M on the target before mutation) appends here; the
    /// dispatch loop releases every entry in a <c>finally</c> at statement
    /// end so locks are returned regardless of success / error / TRY-catch
    /// outcome. Re-entrance (same object resolved twice in one statement —
    /// e.g. <c>FROM t a JOIN t b</c>) is handled inside
    /// <see cref="LockResource"/> via per-owner counting; this list just
    /// tracks every acquisition by reference so Release runs the matching
    /// number of times.
    /// </summary>
    public readonly List<(LockResource Resource, LockMode Mode)> StatementSchemaLocks = [];

    /// <summary>
    /// Tables the current statement resolved with a <c>NOWAIT</c> table hint.
    /// Real scopes the hint to the table it sits on, but the per-row lock
    /// acquisitions happen far from the hint list — every DML path reaches
    /// <see cref="AcquireRowLockTxScoped"/> with a table and a RID and nothing
    /// else — so the hinted tables are recorded here at
    /// <see cref="AcquireDataLockIfApplicable"/> and looked up there. Cleared
    /// with the statement's locks, so a hint doesn't leak into the batch's
    /// next statement.
    /// </summary>
    private readonly HashSet<HeapTable> noWaitTables = [];

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/> for
    /// the current connection, honoring the connection's
    /// <see cref="SimulatedDbConnection.LockTimeoutMillis"/>, and records the
    /// acquisition in <see cref="StatementSchemaLocks"/> so the dispatch
    /// loop releases it at statement end. The two-phase split (acquire then
    /// record) is fine because <see cref="LockManager.Acquire"/> can only
    /// fail by throwing — on success the lock IS held, and we always reach
    /// the append. On throw the lock isn't held, no cleanup needed.
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public void AcquireStatementLock(LockResource resource, LockMode mode, bool noWait = false)
    {
        this.ReplayLockLog?.Add(new ReplayedLock(resource, mode, noWait, transactionScoped: false));
        var connection = this.Connection;
        connection.Simulation.LockManager.Acquire(resource, mode, connection.Session, noWait ? 0 : connection.LockTimeoutMillis);
        this.StatementSchemaLocks.Add((resource, mode));
    }

    /// <summary>
    /// The Sch-M a statement that redefines <paramref name="table"/> or
    /// rewrites its rows wholesale — <c>ALTER TABLE</c>, <c>TRUNCATE</c>,
    /// <c>SWITCH</c> — takes: on its schema lock for the statement, and on its
    /// data lock to the transaction's end, which waits out every transaction
    /// still holding the table's intent lock and holds new readers and writers
    /// off until this one settles, as real's object Sch-M does (probed
    /// 2026-10-01 against SQL Server 2025: a <c>TRUNCATE</c> behind an open
    /// insert waits <c>LCK_M_SCH_M</c> on the object until it commits).
    /// Without the second, the statement swapped the rows out from under an
    /// open writer, whose rollback then wrote into pages that were gone.
    /// </summary>
    public void AcquireTableRedefinitionLock(HeapTable table)
    {
        this.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        if (Simulation.IsLockableTable(table))
            this.AcquireTransactionLock(table.TableDataLock, LockMode.SchemaModification);
    }

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/> for
    /// the current connection and records the acquisition against the
    /// active <see cref="SimulatedDbTransaction"/>, so the lock releases at
    /// COMMIT / ROLLBACK instead of statement end. Used for X data locks
    /// (which must span the transaction under READ COMMITTED) and HOLDLOCK-
    /// upgraded S locks (held until tx end matching SERIALIZABLE).
    /// </summary>
    /// <remarks>
    /// When no transaction is active, the lock falls back to statement-end
    /// release (recorded in <see cref="StatementSchemaLocks"/>) — auto-
    /// commit semantics, matching real SQL Server's implicit-commit-after-
    /// statement behavior for DML outside <c>BEGIN TRAN</c>.
    /// </remarks>
    public void AcquireTransactionLock(LockResource resource, LockMode mode, bool noWait = false)
    {
        this.ReplayLockLog?.Add(new ReplayedLock(resource, mode, noWait, transactionScoped: true));
        var connection = this.Connection;
        connection.Simulation.LockManager.Acquire(resource, mode, connection.Session, noWait ? 0 : connection.LockTimeoutMillis);
        if (connection.CurrentTransaction is { } tx)
            tx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
    }

    /// <summary>
    /// Phase-1b entry point: acquire the appropriate table-level data lock
    /// (IS / IX / SIX / S / U / X) on <paramref name="table"/> and return a
    /// <see cref="DataLockPlan"/> describing what per-row lock the caller
    /// should acquire / probe as it enumerates or mutates rows. Routing
    /// depends on direction (<paramref name="isWrite"/>), hints
    /// (<paramref name="hints"/>), and the session's
    /// <see cref="SimulatedDbConnection.SessionIsolationLevel"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Table-level mode selection:
    /// <list type="bullet">
    /// <item><c>TABLOCKX</c> on read or write → table-X (skips row-level).</item>
    /// <item><c>TABLOCK</c> on read → table-S; on write → table-X.</item>
    /// <item>Write (no TABLOCK*) → table-IX.</item>
    /// <item>Read <c>XLOCK</c> / <c>UPDLOCK</c> → table-IX (intent to write).</item>
    /// <item>Read session <c>SERIALIZABLE</c> (no TABLOCK*) → table-IS
    /// tx-scoped, with phantom protection deferred to whoever consumes the
    /// source: key-range locks on the keys the predicate reaches when it is
    /// sargable on an indexed leading column, on every key otherwise, and a
    /// table-S over a heap. See <see cref="EnsureSerializableTableLock"/>.</item>
    /// <item>Read session <c>READ UNCOMMITTED</c> / hint <c>NOLOCK</c> → no
    /// table-level lock acquired (dirty read).</item>
    /// <item>Read default (RC / RR / HOLDLOCK hint) → table-IS, tx-scoped
    /// under RR, whose row locks outlive the statement.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Per-row plan:
    /// <list type="bullet">
    /// <item>NOLOCK / RU isolation → no per-row lock; reader doesn't probe.</item>
    /// <item>RC reader → no row-S acquisition; reader probes each row for
    /// an incompatible row-X holder and waits (or skips with READPAST).</item>
    /// <item>RR reader → row-S tx-scoped per row it returns.</item>
    /// <item>SERIALIZABLE reader → no per-row acquire; the key locks (or a
    /// heap's table-S) cover both the rows read and the gaps between them,
    /// since a writer tests its row's keys against every held key lock
    /// whatever its own isolation level.</item>
    /// <item>UPDLOCK reader → row-U tx-scoped per touched row.</item>
    /// <item>XLOCK reader → row-X tx-scoped per touched row.</item>
    /// <item>Writer (table-IX) → row-X tx-scoped per mutated row.</item>
    /// <item>TABLOCK* → no per-row lock (the table-level lock covers).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Skips data-lock acquisition entirely for tables that aren't shared
    /// across connections (table variables, local temp tables, system
    /// tables) — same set that <see cref="TryResolveTable"/> bypasses for
    /// Sch-S acquisition. Returns <see cref="DataLockPlan.Bypass"/> in
    /// that case so the caller's per-row logic naturally short-circuits.
    /// </para>
    /// </remarks>
    public DataLockPlan AcquireDataLockIfApplicable(HeapTable table, Selection.TableHintInfo hints, bool isWrite, bool readsRows = true)
    {
        var plan = this.AcquireDataLockCore(table, hints, isWrite, readsRows);
        return isWrite || plan.NoLockReader || !hints.LocksRead
            ? plan
            : plan.WithVersioningRule(lockingRead: true, snapshotConflictCheck: hints.UpdLock || hints.XLock || hints.TabLockX);
    }

    private DataLockPlan AcquireDataLockCore(HeapTable table, Selection.TableHintInfo hints, bool isWrite, bool readsRows)
    {
        // A vector index makes its table read-only, which real settles
        // optimizing the writing statement — an un-taken branch included
        // when the batch compiles before it runs — and which ends the batch.
        if (isWrite && table.VectorIndexes.Count > 0)
            this.RejectOptimizedWrite(SimulatedSqlException.VectorIndexedTableIsReadOnly(table.Name));

        // The table hints a memory-optimized table refuses, and the SNAPSHOT
        // hint only one takes, are refused compiling the batch (probed
        // 2026-10-02 against SQL Server 2025).
        if (table.IsMemoryOptimized)
        {
            if (hints.RefusedByMemoryOptimized is { } refusedHint)
                throw SimulatedSqlException.NotSupportedWithMemoryOptimized($"The table option '{refusedHint}'", 82);
            if (table.IsTableVariable)
                return DataLockPlan.Bypass;
            if (this.IsSkipping)
                return DataLockPlan.Bypass;
            this.CheckMemoryOptimizedIsolation(table, hints, readsRows);
            // No table lock and no reader lock: every read is a snapshot read
            // (ResolveSnapshotXidForRead), and a write's row lock only detects
            // a conflict, which AcquireOnTable turns into Msg 41302. The read's
            // plan isn't NOLOCK's, whose scan reads the heap's live rows — the
            // in-flight writes of other transactions among them.
            return isWrite
                ? new DataLockPlan(rowMode: LockMode.Exclusive, rowTxScoped: true, skipBlockedRows: false, noLockReader: false)
                : new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }
        if (hints.Snapshot && !table.IsTableVariable)
            throw SimulatedSqlException.SnapshotHintOnDiskTable();

        // A skipped statement — an un-taken branch, or a batch compiling before
        // it runs — touches no rows, and a transaction-scoped lock taken for it
        // would outlive it.
        if (this.IsSkipping)
            return DataLockPlan.Bypass;
        if (table.IsTableVariable || IsLocalTempName(table.Name))
            return DataLockPlan.Bypass;
        if (Simulation.SystemHeapTables.Values.Contains(table))
            return DataLockPlan.Bypass;

        if (hints.NoWait)
        {
            _ = this.noWaitTables.Add(table);
            this.ReplayLockLog?.Add(new ReplayedLock(table));
        }

        var connection = this.Connection;
        var isolation = connection.SessionIsolationLevel;

        // SNAPSHOT isolation reaching a user table in a database where
        // ALLOW_SNAPSHOT_ISOLATION is OFF raises Msg 3952. Probe-confirmed
        // the rejection point is the first user-table access, not the SET
        // statement and not BeginTransaction. The bypass paths above
        // (table-variable / local-temp / system table) are the same ones
        // real SQL Server doesn't apply Msg 3952 to — system catalogs work
        // fine inside an SI session regardless of the database flag. The flag
        // that governs is the *table's* database, not the session's: an SI
        // session in a snapshot-enabled database reading a three-part name in
        // one that isn't raises 3952 naming the target (probe-confirmed).
        if (isolation == System.Data.IsolationLevel.Snapshot && this.DatabaseFor(table) is { AllowSnapshotIsolation: false } snapshotDisabled)
            throw SimulatedSqlException.SnapshotIsolationNotAllowed(snapshotDisabled.Name);

        // Read uncommitted / NOLOCK: skip everything. Dirty-read semantics —
        // save for a READ UNCOMMITTED session's read carrying a hint that
        // names a locking level of its own, which real takes as written
        // (probed 2026-10-03 against SQL Server 2025).
        if (!isWrite && (hints.NoLock || (isolation == System.Data.IsolationLevel.ReadUncommitted && !hints.LocksRead && !hints.ReadCommitted)))
            return DataLockPlan.NoLock;

        // A row-versioned read — READ_COMMITTED_SNAPSHOT's or a SNAPSHOT
        // transaction's — carrying TABLOCK and no locking hint reads its
        // versions as any other does, without the table S (probed 2026-10-03
        // against SQL Server 2025: it reads past an open writer's IX).
        if (!isWrite && hints.TabLock && !hints.LocksRead
            && (isolation == System.Data.IsolationLevel.Snapshot
                || (isolation == System.Data.IsolationLevel.ReadCommitted && this.DatabaseFor(table).ReadCommittedSnapshot)))
        {
            hints.TabLock = false;
        }

        // A snapshot older than the table's definition can't reach it, since
        // metadata isn't versioned (see VersionStore.NoteDefinitionChange); a
        // NOLOCK read reads no snapshot and goes ahead (probed 2026-10-01).
        if (isolation == System.Data.IsolationLevel.Snapshot
            && connection.CurrentTransaction is { SnapshotXid: { } snapshotXid }
            && Volatile.Read(ref table.DefinitionXid) > snapshotXid)
        {
            throw SimulatedSqlException.SnapshotTableDefinitionChanged(this.DatabaseFor(table).Name);
        }

        // TABLOCKX: table-X tx-scoped; no per-row work.
        if (hints.TabLockX)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.Exclusive, hints.NoWait);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }

        if (hints.TabLock)
        {
            if (isWrite)
            {
                this.AcquireTransactionLock(table.TableDataLock, LockMode.Exclusive, hints.NoWait);
                return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
            }
            // Reader TABLOCK: table-S. Tx-scoped iff HOLDLOCK/SER/REPEATABLE or session RR/SER.
            var tabLockTxScoped = hints.Serializable
                || hints.Repeatable
                || isolation is System.Data.IsolationLevel.RepeatableRead or System.Data.IsolationLevel.Serializable;
            if (tabLockTxScoped)
                this.AcquireTransactionLock(table.TableDataLock, LockMode.Shared, hints.NoWait);
            else
                this.AcquireStatementLock(table.TableDataLock, LockMode.Shared, hints.NoWait);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }

        if (isWrite)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive, hints.NoWait);
            return new DataLockPlan(rowMode: LockMode.Exclusive, rowTxScoped: true, skipBlockedRows: false, noLockReader: false);
        }

        // Reader path (no TABLOCK*).
        var serializable = hints.Serializable || isolation == System.Data.IsolationLevel.Serializable;
        if (hints.XLock || hints.UpdLock)
        {
            // Table-IX either way — probed, real reports IX at the object for
            // an UPDLOCK / XLOCK read whatever the isolation level.
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive, hints.NoWait);
            var hintedRowMode = hints.XLock ? LockMode.Exclusive : LockMode.Update;
            if (!serializable)
                return new DataLockPlan(rowMode: hintedRowMode, rowTxScoped: true, skipBlockedRows: hints.ReadPast, noLockReader: false);
            // SERIALIZABLE on top of the hint: real fences the same interval a
            // plain SERIALIZABLE read would, in the mode the hint names —
            // RangeS-U for UPDLOCK, RangeX-X for XLOCK (probe-confirmed, and
            // neither takes a plain key lock beside it). The per-row hold stays
            // on top: range modes live on resources of their own here, so
            // dropping the row-U / row-X would stop blocking the readers and
            // writers that take one without ever probing a range.
            this.HasSessionScopedReference = true;
            return new DataLockPlan(
                rowMode: hintedRowMode, rowTxScoped: true, skipBlockedRows: hints.ReadPast, noLockReader: false,
                serializableRangeMode: hints.XLock ? LockMode.RangeExclusiveExclusive : LockMode.RangeSharedUpdate,
                fence: new PhantomFenceState());
        }
        if (serializable)
        {
            // SERIALIZABLE / HOLDLOCK hint. Only table-IS is settled here —
            // the predicate that decides between key-range locks and the
            // whole-table fallback isn't known at FROM-source resolution, so
            // the plan carries the obligation forward (see
            // DataLockPlan.SerializableRangeMode). The IS is tx-scoped, not
            // statement-scoped, so a concurrent TABLOCKX still conflicts for
            // as long as the ranges are held — that writer takes no per-row
            // lock, so the range probe would never see it.
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentShared, hints.NoWait);
            // Not plan-cacheable: the plan's PhantomFenceState records
            // whether the fence is settled, and a replay sharing it would find
            // the first execution's fence already taken and take none.
            this.HasSessionScopedReference = true;
            return new DataLockPlan(
                rowMode: null, rowTxScoped: false, skipBlockedRows: hints.ReadPast, noLockReader: false,
                serializableRangeMode: LockMode.RangeSharedShared,
                fence: new PhantomFenceState());
        }
        // RC / RR reader. A REPEATABLE READ keeps its IS as long as the row S
        // locks under it, as real reports — it is what a TABLOCKX writer, which
        // takes no row lock, meets.
        var rowTxScoped = hints.Repeatable || isolation == System.Data.IsolationLevel.RepeatableRead;
        if (rowTxScoped)
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentShared, hints.NoWait);
        else
            this.AcquireStatementLock(table.TableDataLock, LockMode.IntentShared, hints.NoWait);
        // RR: acquire row-S tx-scoped per row.
        // RC default: probe-only (no acquire). Encoded as rowMode = null + noLockReader = false;
        // the row-touch helper distinguishes "null + noLockReader=false" (probe) from
        // "null + noLockReader=true" (skip even probe — that's the NoLock path).
        var rowMode = rowTxScoped ? (LockMode?)LockMode.Shared : null;
        return new DataLockPlan(rowMode: rowMode, rowTxScoped: rowTxScoped, skipBlockedRows: hints.ReadPast, noLockReader: false);
    }

    /// <summary>
    /// Acquires <paramref name="mode"/> on the row at
    /// <c>(pageIndex, slotIndex)</c> in <paramref name="table"/>, recording
    /// it against the active transaction (the statement, outside one), and
    /// counts it toward the statement's lock escalation on the table. A row
    /// the transaction or statement has already escalated past takes no lock
    /// of its own. Before the row lock, a held key lock on the row's keys is
    /// tested as <paramref name="purpose"/> says it must be.
    /// </summary>
    public void AcquireRowLockTxScoped(HeapTable table, int pageIndex, int slotIndex, LockMode mode, RowLockPurpose purpose = RowLockPurpose.Read)
    {
        // Every UPDATE / DELETE path passes through here with a RID in hand,
        // so it is where their key-lock tests hang: the lock comes before the
        // write, so the slot holds the row it is about to supersede. An
        // UPDATE's new image is tested separately at the rewrite site — a row
        // moving into a fenced gap is a phantom the old image can't reveal —
        // and an INSERT tests its image before the heap write and takes its
        // X through AcquireInsertedRowLock.
        for (var attempt = 1; ; attempt++)
        {
            if (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0
                && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } liveImage)
            {
                if (purpose == RowLockPurpose.Read)
                    _ = this.TestRowKeyLock(table, liveImage, mode, skipIfBlocked: false);
                else
                    this.TestKeyLocksForWrite(table, liveImage, purpose);
            }
            this.AcquireRowLock(table, pageIndex, slotIndex, mode, conflictIsDelete: purpose == RowLockPurpose.Delete);
            // The key test and the row lock are two acquisitions where real's
            // key lock is one: a SERIALIZABLE reader can lock the key between
            // them, find the row unlocked and read it, and the write then
            // changes the row under the reader's lock. A key lock taken in
            // that gap gives the row back — unless the session held it
            // already, when the reader waited on it — and waits for the key.
            if (purpose == RowLockPurpose.Read || attempt == Simulation.MaxTargetWalks
                || Volatile.Read(ref table.ActiveKeyRangeLocks) == 0
                || this.EscalatedModeOf(table) is not null
                || table.Heap.ReadSlotBytes(pageIndex, slotIndex) is not { } lockedImage
                || !this.KeyLockRefusesWrite(table, lockedImage, purpose)
                || !table.RowLocks.TryGetValue((pageIndex, slotIndex), out var held)
                || this.Connection.Simulation.LockManager.HoldCount(held, mode, this.Connection.Session) > 1)
            {
                return;
            }
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, mode);
        }
    }

    // Whether another session holds a key lock a write of `image` for
    // `purpose` would have to wait out (TestKeyLocksForWrite), without waiting.
    private bool KeyLockRefusesWrite(HeapTable table, byte[] image, RowLockPurpose purpose)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        foreach (var (_, group) in table.KeyLockGroups)
        {
            if (Volatile.Read(ref group.Holds) != 0
                && (purpose != RowLockPurpose.UpdatePreImage || group.IsRowGroup)
                && group.TryReadKey(image, out var key)
                && group.Find(key) is { } resource
                && manager.HasIncompatibleHolderOtherThan(resource, LockMode.Exclusive, connection.Session))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The X on a row an insert has just placed, taken under the heap's latch
    /// before the row is visible (see <c>Simulation.InsertRow</c>), so it
    /// skips the key-range tests, which can wait and which the insert ran
    /// before taking the latch. No session can hold a lock on an address
    /// that didn't exist, so the acquisition never waits.
    /// </summary>
    /// <remarks>
    /// The inserter is recorded before the acquisition, whose escalation count
    /// can release this very lock with the rest of the statement's — the
    /// release clears it with the X — and not at all under a table X already
    /// covering the row, when no row lock is taken for it to describe.
    /// </remarks>
    public void AcquireInsertedRowLock(HeapTable table, int pageIndex, int slotIndex)
    {
        if (this.EscalatedModeOf(table) == LockMode.Exclusive)
            return;
        table.GetOrCreateRowLock(pageIndex, slotIndex).InsertedBy = this.Connection.Session;
        this.AcquireRowLock(table, pageIndex, slotIndex, LockMode.Exclusive, underLatch: true);
    }

    private void AcquireRowLock(HeapTable table, int pageIndex, int slotIndex, LockMode mode, bool underLatch = false, bool countForEscalation = true, bool conflictIsDelete = false)
    {
        if (this.EscalatedModeOf(table) is { } escalated && (escalated == LockMode.Exclusive || mode == LockMode.Shared))
            return;
        var connection = this.Connection;
        var resource = table.GetOrCreateRowLock(pageIndex, slotIndex);
        this.AcquireOnTable(table, resource, mode, connection.Session, sweepAbandoned: !underLatch, conflictIsDelete);
        if (connection.CurrentTransaction is { } activeTx)
            activeTx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
        // A memory-optimized table's row locks only detect write conflicts,
        // so they never escalate.
        if (countForEscalation && !table.IsMemoryOptimized)
            this.CountLocksForEscalation(table, 1, exclusive: mode != LockMode.Shared, rowLock: true);
    }

    /// <summary>
    /// Set when a target row a writer's walk waited on came back deleted while
    /// a row carrying its key (<see cref="RowIdentityKey"/>) stands again — a DELETE and INSERT of
    /// the key in the transaction the walk waited out. Real's read of the key
    /// reads the row the key holds once its writer settles, so the walk reads
    /// its rows again (<c>Simulation.MaxTargetWalks</c>): the reinserted row is
    /// at an address it never read. Cleared by each walk as it starts.
    /// </summary>
    public bool TargetKeyReinserted;

    /// <summary>
    /// Whether the statement running would run again on
    /// <see cref="TargetKeyReinserted"/> — false on its last attempt and in a
    /// trigger's statement — so a walk can stop short of work the run again
    /// replaces, such as a MERGE inserting a source row whose target it missed.
    /// </summary>
    public bool TargetWalkMayRunAgain;

    /// <summary>Notes, for <see cref="TargetKeyReinserted"/>, whether a target row the walk read as <paramref name="priorImage"/> and found deleted has its key (<see cref="RowIdentityKey"/>) back.</summary>
    private void NoteVanishedTargetRow(HeapTable table, byte[] priorImage)
    {
        if (this.TargetKeyReinserted || RowIdentityKey(table) is not var (ordinals, commons))
            return;
        var schema = table.StoredColumns;
        if (!HeapSeekCache.TryComputeKey(priorImage, ordinals, commons, schema, table.Heap, out var key))
            return;
        // Another session may hold the key deleted again by now; its write
        // is waited out, as for a reader (see RowsOfDeletedKey).
        if (this.RowsCarryingKey(table, ordinals, commons, key).Count != 0)
            this.TargetKeyReinserted = true;
    }

    /// <summary>
    /// The addresses of the rows carrying <paramref name="key"/> in
    /// <see cref="RowIdentityKey"/>'s columns, waiting out another session's
    /// delete of the key in flight, as real waits on the key's lock whoever
    /// holds it. Empty only when the rows and the deletes in flight, read one
    /// after the other, both saw the key gone with nothing put back between
    /// the reads (<see cref="HeapTable.KeysPutBack"/>): a delete settling
    /// between them otherwise reads as the key never having come back.
    /// </summary>
    private List<(int Page, int Slot)> RowsCarryingKey(HeapTable table, int[] ordinals, SqlType[] commons, SqlValueKey key)
    {
        var rows = new List<(int Page, int Slot)>();
        var schema = table.StoredColumns;
        for (var attempt = 1; ; attempt++)
        {
            var putBack = Volatile.Read(ref table.KeysPutBack);
            foreach (var (page, slot, _) in HeapSeekCache.For(table.Heap).MatchingRows(table.Heap, schema, ordinals, commons, key))
                rows.Add((page, slot));
            if (rows.Count != 0 || attempt == Simulation.MaxTargetWalks
                || (!this.AwaitKeyDeleters(table, ordinals, commons, key) && Volatile.Read(ref table.KeysPutBack) == putBack))
            {
                return rows;
            }
        }
    }

    /// <summary>
    /// Readies a row a writer's target read is about to judge — an UPDATE's,
    /// a DELETE's or a MERGE's. Real reads its target under U, so a row
    /// another session is writing is waited out in U and judged as that write
    /// left it (probed 2026-10-01 against SQL Server 2025: a MERGE, seeking or
    /// scanning, waits <c>LCK_M_U</c> on the writer's key, and an UPDATE whose
    /// row was being rewritten by a transaction that then rolled back writes
    /// from the restored row). Here the wait happens only when the row holds
    /// a lock U conflicts with; otherwise nothing is taken yet, and the lock
    /// a qualifying row needs comes from <see cref="HoldQualifyingTargetRow"/>.
    /// <paramref name="rowBytes"/> becomes the row as it stands after a wait.
    /// A memory-optimized table's target read waits for nothing: a row
    /// another session writes is a conflict the write itself meets.
    /// </summary>
    public TargetRowHold AwaitTargetRow(HeapTable table, int pageIndex, int slotIndex, ref byte[] rowBytes)
    {
        if (table.IsMemoryOptimized
            || (Volatile.Read(ref table.ActiveDataWriters) == 0 && Volatile.Read(ref table.ActiveUpdateLocks) == 0)
            || !table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource)
            || !Simulation.IsLockableTable(table)
            || !this.Connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(resource, LockMode.Update, this.Connection.Session))
        {
            return TargetRowHold.None;
        }
        this.AcquireRowLock(table, pageIndex, slotIndex, LockMode.Update, countForEscalation: false);
        if (table.Heap.ReadLiveRow(pageIndex, slotIndex) is not { } current)
        {
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Update);
            this.NoteVanishedTargetRow(table, rowBytes);
            return TargetRowHold.Gone;
        }
        rowBytes = current;
        return TargetRowHold.Update;
    }

    /// <summary>
    /// <see cref="AwaitTargetRow"/> for a statement that takes its rows' X
    /// only once its walk is done, in walk order
    /// (<see cref="HoldQualifyingTargetRow"/>): the U a wait took goes as soon
    /// as the row is read, so the walk holds nothing. Holding it, the walk
    /// could keep a later row while its X waits on an earlier one another
    /// session holds, that session's X waiting on the later — a deadlock real
    /// never meets, since its walk takes U on every row in order (probed
    /// 2026-10-01 against SQL Server 2025: eight sessions each updating the
    /// same two rows forty times, single-table or joined, meet none). False
    /// when the row was deleted while the walk waited.
    /// </summary>
    public bool AwaitTargetRowWriters(HeapTable table, int pageIndex, int slotIndex, ref byte[] rowBytes)
    {
        var hold = this.AwaitTargetRow(table, pageIndex, slotIndex, ref rowBytes);
        if (hold == TargetRowHold.Update)
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Update, countedForEscalation: false);
        return hold != TargetRowHold.Gone;
    }

    /// <summary>
    /// Takes and keeps U on a row a <c>MERGE</c> into a join view reads its
    /// written table through, as real's target read does (probed 2026-10-01
    /// against SQL Server 2025: it waits <c>LCK_M_U</c> on the writer's key),
    /// so the row stays as read until the statement's write converts the U to
    /// X; the caller gives it back with <see cref="ReleaseTargetRow"/> once
    /// the statement has written. Answers the row as it stands once taken —
    /// null when another session's write deleted it.
    /// </summary>
    public byte[]? HoldTargetRowForUpdate(HeapTable table, int pageIndex, int slotIndex)
    {
        if (Simulation.IsLockableTable(table))
            this.AcquireRowLock(table, pageIndex, slotIndex, LockMode.Update, countForEscalation: false);
        return table.Heap.ReadLiveRow(pageIndex, slotIndex);
    }

    /// <summary>
    /// The key lock an INSERT's check of a nonclustered PRIMARY KEY / UNIQUE
    /// constraint or unique index with <c>IGNORE_DUP_KEY</c> takes: real reads
    /// that index for the key under a SERIALIZABLE U — U on the key when a row
    /// carries it, <c>RangeS-U</c> on the next key past it when none does —
    /// and keeps it to the transaction's end, the insert then splitting the
    /// range so its own key takes <c>RangeX-X</c> (probed 2026-10-01 against
    /// SQL Server 2025; a clustered key with the option takes none). So a
    /// second writer of the key waits in U, and one writing another key into
    /// the same gap waits in <c>RangeS-U</c>, until the first settles.
    /// </summary>
    public void LockIgnoreDupKeyProbe(HeapTable table, object keyOwner, SqlValueKey probe)
    {
        if (this.IsSkipping
            || !Simulation.IsLockableTable(table)
            || this.EscalatedModeOf(table) == LockMode.Exclusive
            || KeyLockGroup.For(table, keyOwner) is not { } group)
        {
            return;
        }
        var heap = table.Heap;
        var cache = HeapSeekCache.For(heap);
        var (resource, mode) = cache.AnyRowMatches(heap, table.StoredColumns, group.Ordinals, group.Commons, probe)
            ? (group.GetOrCreate(probe), LockMode.Update)
            : (group.GetOrCreate(cache.NextKeyAbove(heap, table.StoredColumns, heap, group.Ordinals, group.Commons, probe)), LockMode.RangeSharedUpdate);
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (manager.IsHeldBy(resource, mode, connection.Session))
            return;
        this.AcquireOnTable(table, resource, mode, connection.Session);
        if (connection.CurrentTransaction is { } tx)
            tx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
    }

    /// <summary>
    /// The rows of <paramref name="table"/> other sessions have rewritten or
    /// deleted and still hold X on, each with the image it carried before
    /// that write and the lock (<see cref="HeapTable.SupersededKeyImages"/>);
    /// null when there are none, which costs one lock-free read.
    /// </summary>
    public List<((int Page, int Slot) Address, byte[] PriorImage, LockResource Lock)>? SupersededTargetRows(HeapTable table)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree() || !Simulation.IsLockableTable(table) || table.IsMemoryOptimized)
            return null;
        var session = this.Connection.Session;
        List<((int Page, int Slot) Address, byte[] PriorImage, LockResource Lock)>? rows = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, session))
                continue;
            foreach (var (address, (image, resource)) in images)
                (rows ??= []).Add((address, image, resource));
        }
        return rows;
    }

    /// <summary>
    /// Waits in U for the session holding <paramref name="resource"/>, the X
    /// on a row it rewrote or deleted, as real's target read meets that row;
    /// true when there was someone to wait for.
    /// </summary>
    public bool AwaitSupersededTargetRow(HeapTable table, LockResource resource) =>
        this.AwaitRowWriters(table, resource, LockMode.Update);

    /// <summary>Gives back what <paramref name="hold"/> took on a row the statement then turned away.</summary>
    public void ReleaseTargetRow(HeapTable table, int pageIndex, int slotIndex, TargetRowHold hold)
    {
        if ((hold & TargetRowHold.Exclusive) != 0)
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Exclusive);
        if ((hold & TargetRowHold.Update) != 0)
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Update, countedForEscalation: false);
    }

    /// <summary>
    /// Takes the X a target row the statement judged qualifying is written
    /// under, as real converts its U to X writing the row (probed 2026-10-01
    /// against SQL Server 2025: an UPDATE scanning past another session's row
    /// already holds X on the rows before it), so no other writer changes the
    /// row between the judgement and the write — two sessions updating one
    /// row from its own value each see the other's write — and the write
    /// itself takes nothing more. <paramref name="purpose"/> is the write's,
    /// for the key-range tests. False when the row changed since
    /// <paramref name="rowBytes"/> was read, the X having waited out the
    /// session that changed it: <paramref name="rowBytes"/> is then the row as
    /// it stands, to judge again, and <paramref name="hold"/> is
    /// <see cref="TargetRowHold.Gone"/> when that write deleted it. A heap
    /// whose <see cref="Heap.MutationGeneration"/> still reads
    /// <paramref name="walkGeneration"/>, noted before the walk read any row,
    /// has had no row written since — a rollback only restores an image some
    /// write moved the generation past — so the row isn't read again.
    /// </summary>
    public bool HoldQualifyingTargetRow(
        HeapTable table, int pageIndex, int slotIndex, ref TargetRowHold hold, ref byte[] rowBytes, long walkGeneration, RowLockPurpose purpose = RowLockPurpose.UpdatePreImage)
    {
        if ((hold & TargetRowHold.Exclusive) != 0 || !Simulation.IsLockableTable(table))
            return true;
        this.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive, purpose);
        hold |= TargetRowHold.Exclusive;
        if (Volatile.Read(ref table.Heap.MutationGeneration) == walkGeneration)
            return true;
        var current = table.Heap.ReadLiveRow(pageIndex, slotIndex);
        if (current is not null && current.AsSpan().SequenceEqual(rowBytes))
            return true;
        if (current is null)
        {
            this.ReleaseTargetRow(table, pageIndex, slotIndex, hold);
            this.NoteVanishedTargetRow(table, rowBytes);
            hold = TargetRowHold.Gone;
            return false;
        }
        rowBytes = current;
        return false;
    }

    /// <summary>
    /// Gives back the most recent acquisition of <paramref name="mode"/> on the
    /// row at <paramref name="pageIndex"/> / <paramref name="slotIndex"/> — the
    /// lock a tx-scoped read took on a row that turned out not to qualify,
    /// which real releases rather than keeping to the transaction's end. A
    /// row the read took no lock on (the table escalated) gives back nothing.
    /// <paramref name="countedForEscalation"/> false for an acquisition that
    /// didn't count toward escalation, as a target read's U doesn't.
    /// </summary>
    public void ReleaseRowLockAcquisition(HeapTable table, int pageIndex, int slotIndex, LockMode mode, bool countedForEscalation = true)
    {
        if (!table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource))
            return;
        var connection = this.Connection;
        var held = connection.CurrentTransaction?.HeldLocks ?? this.StatementSchemaLocks;
        var index = held.LastIndexOf((resource, mode));
        if (index < 0)
            return;
        held.RemoveAt(index);
        connection.Simulation.LockManager.Release(resource, mode, connection.Session);
        if (countedForEscalation && this.CurrentStatement.LockTallies is { } tallies && tallies.TryGetValue(table, out var tally) && !tally.RowsKeyLocked)
            tally.Count--;
    }

    /// <summary>
    /// Records the image of the row at <paramref name="pageIndex"/> /
    /// <paramref name="slotIndex"/> — which this session has just taken X on
    /// and is about to delete or rewrite — in
    /// <see cref="HeapTable.SupersededKeyImages"/>, so another session's
    /// uniqueness check can wait on the key the write takes away, and its
    /// scan on the row a delete hides, until this session's transaction
    /// settles. An escalated table's X covers every row already.
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public void NoteSupersededRow(HeapTable table, int pageIndex, int slotIndex)
    {
        var connection = this.Connection;
        if (this.EscalatedModeOf(table) == LockMode.Exclusive)
            return;
        // Every caller has just taken the row X through AcquireRowLockTxScoped,
        // whose only way out without it is the escalation checked above; the
        // entry retires with that hold's release.
        if (!table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource)
            || table.Heap.ReadSlotBytes(pageIndex, slotIndex) is not { } image)
        {
            return;
        }
        table.SupersededKeyImages.GetOrAdd(connection.Session, static _ => new())[(pageIndex, slotIndex)] = (image, resource);
    }

    /// <summary>
    /// Waits out every other session's uncommitted delete on
    /// <paramref name="table"/> before a locking scan reads it. The scan's
    /// heap walk never reaches a tombstoned slot, where real's scan meets the
    /// deleted row's X-locked key and waits on it (probed 2026-09-26 against
    /// SQL Server 2025) — a read that skipped it would report the delete
    /// before it committed. Waiting up front rather than at the row's turn
    /// only moves the wait earlier within the same statement.
    /// </summary>
    public void AwaitUncommittedDeletes(HeapTable table)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree())
            return;
        var connection = this.Connection;
        List<LockResource>? holders = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, connection.Session))
                continue;
            foreach (var (address, (_, resource)) in images)
            {
                if (table.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex))
                    (holders ??= []).Add(resource);
            }
        }

        if (holders is not null)
        {
            foreach (var resource in holders)
                _ = this.AwaitRowWriters(table, resource);
        }
    }

    /// <summary>
    /// The key that names a row whatever its address — the clustered key, else
    /// a stored PRIMARY KEY or UNIQUE constraint — which a read that finds its
    /// row deleted reads again by, as real's read meets the key under its
    /// writer's lock rather than the address; null when the table has none.
    /// </summary>
    internal static (int[] Ordinals, SqlType[] Commons)? RowIdentityKey(HeapTable table)
    {
        var ordinals = ClusteredScan.KeyOrdinals(table);
        if (ordinals is null)
        {
            foreach (var key in table.KeyConstraints)
            {
                if (!key.IsDisabled && key.KeysAreStored)
                {
                    ordinals = key.StorageOrdinals;
                    break;
                }
            }
        }
        if (ordinals is null)
            return null;
        var schema = table.StoredColumns;
        var commons = new SqlType[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
            commons[i] = schema[ordinals[i]].Type;
        return (ordinals, commons);
    }

    /// <summary>
    /// The order a locking scan reads <paramref name="table"/> in
    /// (<see cref="ClusteredScan.Order"/>), with <paramref name="keys"/>
    /// receiving each address's key. With <paramref name="follow"/>, keys
    /// another session's delete has taken out of the order but not yet
    /// settled stand in it as their deleted rows, as real's index keeps a
    /// deleted key as a ghost under its writer's X until the delete commits:
    /// the scan meets them in key order and waits. The order and the deletes
    /// are two reads, and a delete that settled between them with its key put
    /// back elsewhere left the key in neither, so a key put back meanwhile
    /// (<see cref="HeapTable.KeysPutBack"/>) reads both again.
    /// </summary>
    public List<(int Page, int Slot)>? LockingScanOrder(HeapTable table, List<SqlValueKey> keys, bool follow)
    {
        for (var reads = 1; ; reads++)
        {
            var putBack = Volatile.Read(ref table.KeysPutBack);
            keys.Clear();
            var order = ClusteredScan.Order(table, keys, follow);
            if (order is null || !follow || keys.Count != order.Count)
                return order;
            // An empty order may be the sorted-order cache's own list, shared
            // with every scan of this generation: placing into it would change
            // what they enumerate.
            if (order.Count == 0)
                order = [];
            this.PlaceInFlightDeletes(table, order, keys);
            if (Volatile.Read(ref table.KeysPutBack) == putBack || reads == Simulation.MaxTargetWalks)
                return order;
        }
    }

    /// <summary>
    /// Places in <paramref name="order"/>, a key-order scan's addresses with
    /// their <paramref name="keys"/> at the same indexes, each row another
    /// session has deleted and not yet settled, at its key's position — the
    /// ghost real's index keeps for it — so the scan meets it and waits
    /// (<see cref="RowsOfDeletedKey"/>) rather than passing a key that comes
    /// back elsewhere.
    /// </summary>
    public void PlaceInFlightDeletes(HeapTable table, List<(int Page, int Slot)> order, List<SqlValueKey> keys)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree() || ClusteredScan.KeyOrdinals(table) is not { } ordinals)
            return;
        var schema = table.StoredColumns;
        var commons = new SqlType[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
            commons[i] = schema[ordinals[i]].Type;
        var session = this.Connection.Session;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, session))
                continue;
            foreach (var (address, (image, _)) in images)
            {
                if (!HeapSeekCache.TryComputeKey(image, ordinals, commons, schema, table.Heap, out var key))
                    continue;
                // A delete whose transaction rolled back has restored its row
                // here, perhaps since the order was read without it: placed
                // too, unless the order has it — or the row is an update's
                // that moved it to another key, where the order finds it.
                if (!table.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex)
                    && (table.Heap.ReadSlotBytes(address.PageIndex, address.SlotIndex) is not { } live
                        || !HeapSeekCache.TryComputeKey(live, ordinals, commons, schema, table.Heap, out var liveKey)
                        || !liveKey.Equals(key)))
                {
                    continue;
                }
                var at = 0;
                var placed = false;
                for (int compared; at < keys.Count && (compared = HeapSeekCache.CompareKeys(keys[at], key)) <= 0; at++)
                    placed |= compared == 0 && order[at] == (address.PageIndex, address.SlotIndex);
                if (placed)
                    continue;
                order.Insert(at, (address.PageIndex, address.SlotIndex));
                keys.Insert(at, key);
            }
        }
    }

    /// <summary>
    /// For a read reaching an address it was about to read that has since been
    /// deleted, or whose row a wait on it saw deleted: waits out the delete
    /// when another session still has it in flight, as real's read waits on
    /// the deleted key's X, and returns the addresses of the rows carrying the
    /// row's key (<see cref="RowIdentityKey"/>) now — the row a DELETE and
    /// INSERT of the key in one transaction left, or none. The key is
    /// <paramref name="knownKey"/> when the caller's order carried it, else
    /// <paramref name="priorImage"/>'s or the in-flight delete's pre-image's.
    /// Null when nothing names it, or the read doesn't wait (<c>NOLOCK</c>,
    /// <c>READPAST</c>). <paramref name="resolvedKey"/> is the key, which names
    /// the rows returned should they be deleted in turn before they are read.
    /// </summary>
    public List<(int Page, int Slot)>? RowsOfDeletedKey(
        HeapTable table, int pageIndex, int slotIndex, in DataLockPlan plan, SqlValueKey? knownKey, byte[]? priorImage, out SqlValueKey? resolvedKey)
    {
        resolvedKey = null;
        if (plan.NoLockReader || plan.SkipBlockedRows || RowIdentityKey(table) is not var (ordinals, commons))
            return null;
        var schema = table.StoredColumns;
        var key = knownKey;
        if (key is null && priorImage is not null && HeapSeekCache.TryComputeKey(priorImage, ordinals, commons, schema, table.Heap, out var priorKey))
            key = priorKey;
        if (!table.SupersededKeyImages.IsEmptyLockFree())
        {
            var session = this.Connection.Session;
            foreach (var (owner, images) in table.SupersededKeyImages)
            {
                if (ReferenceEquals(owner, session) || !images.TryGetValue((pageIndex, slotIndex), out var superseded))
                    continue;
                _ = this.AwaitRowWriters(table, superseded.Lock);
                if (key is null && HeapSeekCache.TryComputeKey(superseded.Image, ordinals, commons, schema, table.Heap, out var imageKey))
                    key = imageKey;
                break;
            }
        }
        resolvedKey = key;
        // The key may be gone again by now, deleted by a session that met it
        // after this one's wait began: that delete is waited out too.
        return key is { } known ? this.RowsCarryingKey(table, ordinals, commons, known) : null;
    }

    /// <summary>
    /// Counts in <see cref="HeapTable.KeysPutBack"/> an insert of
    /// <paramref name="image"/> carrying the key (<see cref="RowIdentityKey"/>)
    /// of a row this session deleted and still holds — a DELETE and INSERT of
    /// one key in a transaction. Costs one lock-free read while the session
    /// has no delete in flight on the table.
    /// </summary>
    public void NoteKeyPutBack(HeapTable table, ReadOnlySpan<byte> image)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree()
            || !table.SupersededKeyImages.TryGetValue(this.Connection.Session, out var own)
            || RowIdentityKey(table) is not var (ordinals, commons))
        {
            return;
        }
        var schema = table.StoredColumns;
        if (!HeapSeekCache.TryComputeKey(image, ordinals, commons, schema, table.Heap, out var key))
            return;
        foreach (var (address, (prior, _)) in own)
        {
            if (table.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex)
                && HeapSeekCache.TryComputeKey(prior, ordinals, commons, schema, table.Heap, out var deleted)
                && deleted.Equals(key))
            {
                _ = Interlocked.Increment(ref table.KeysPutBack);
                return;
            }
        }
    }

    /// <summary>
    /// Waits out, in S, every other session's uncommitted delete or rewrite of
    /// a row whose <paramref name="ordinals"/> tuple was <paramref name="key"/>;
    /// true when there was one.
    /// </summary>
    private bool AwaitKeyDeleters(HeapTable table, int[] ordinals, SqlType[] commons, SqlValueKey key)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree())
            return false;
        var session = this.Connection.Session;
        List<LockResource>? holders = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, session))
                continue;
            foreach (var (_, (image, resource)) in images)
            {
                if (HeapSeekCache.TryComputeKey(image, ordinals, commons, table.StoredColumns, table.Heap, out var imageKey) && imageKey.Equals(key))
                    (holders ??= []).Add(resource);
            }
        }
        var waited = false;
        foreach (var resource in holders ?? [])
            waited |= this.AwaitRowWriters(table, resource);
        return waited;
    }

    /// <summary>
    /// Waits out every other session's uncommitted delete or rewrite of a row
    /// whose <paramref name="storageOrdinals"/> tuple was
    /// <paramref name="probe"/> (see <see cref="HeapTable.SupersededKeyImages"/>),
    /// the way real's insert of a key waits on that key's lock — in
    /// <paramref name="mode"/>, X for a uniqueness check and S for a foreign
    /// key's. Cheap when no other session has one pending on the table.
    /// <paramref name="reportedKey"/>, when the tuple is a unique key, is
    /// where the lock DMVs report the wait (<see cref="SessionToken.WaitingOnKey"/>).
    /// </summary>
    public void AwaitSupersededKeyHolders(HeapTable table, int[] storageOrdinals, SqlType[] commons, SqlValueKey probe, LockMode mode, bool reportedKey = false)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree())
            return;
        var connection = this.Connection;
        List<LockResource>? holders = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, connection.Session))
                continue;
            foreach (var (_, (image, resource)) in images)
            {
                if (HeapSeekCache.TryComputeKey(image, storageOrdinals, commons, table.StoredColumns, table.Heap, out var key) && key.Equals(probe))
                    (holders ??= []).Add(resource);
            }
        }

        if (holders is not null)
        {
            foreach (var resource in holders)
                _ = this.AwaitRowWriters(table, resource, mode, reportedKey ? probe : null);
        }
    }

    /// <summary>
    /// Waits out another session's uncommitted write to a live row carrying
    /// <paramref name="probe"/> — the duplicate a uniqueness check just
    /// found, which is only a duplicate once that write commits (real's
    /// second insert of a key waits on the first's lock rather than failing
    /// at once, requesting X on it; probed 2026-10-01 against SQL Server
    /// 2025). <paramref name="mode"/> is
    /// <see cref="AwaitSupersededKeyHolders"/>'s, as is <paramref name="reportedKey"/>.
    /// True when it waited, so the caller checks again.
    /// </summary>
    public bool AwaitLiveKeyHolders(HeapTable table, int[] storageOrdinals, SqlType[] commons, SqlValueKey probe, LockMode mode, bool reportedKey = false)
    {
        if (Volatile.Read(ref table.ActiveDataWriters) == 0)
            return false;
        var waited = false;
        foreach (var (page, slot, _) in HeapSeekCache.For(table.Heap).MatchingRows(table.Heap, table.StoredColumns, storageOrdinals, commons, probe))
        {
            if (table.RowLocks.TryGetValue((page, slot), out var resource))
                waited |= this.AwaitRowWriters(table, resource, mode, reportedKey ? probe : null);
        }
        return waited;
    }

    /// <summary>
    /// <see cref="AwaitLiveKeyHolders"/> for one row a uniqueness check's
    /// scan found carrying the key — the scan a NULL key component sends it
    /// to. True when it waited, so the caller scans again.
    /// </summary>
    public bool AwaitKeyHolderAt(HeapTable table, int pageIndex, int slotIndex) =>
        Volatile.Read(ref table.ActiveDataWriters) != 0
        && table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource)
        && this.AwaitRowWriters(table, resource, LockMode.Exclusive);

    /// <summary>
    /// Blocks until no other session holds the row lock
    /// <paramref name="resource"/> incompatibly with S — the transient
    /// acquire-and-release real's "wait for the committed row" amounts to.
    /// </summary>
    public bool AwaitRowWritersOf(HeapTable table, LockResource resource) => this.AwaitRowWriters(table, resource);

    // True when there was someone to wait for. A wait for a key some row
    // carries names the key, for the lock DMVs.
    private bool AwaitRowWriters(HeapTable table, LockResource resource, LockMode mode = LockMode.Shared, SqlValueKey? waitingOnKey = null)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, mode, connection.Session))
            return false;
        var session = connection.Session;
        session.WaitingOnKey = waitingOnKey is { } key ? KeyLockGroup.Describe(key) : null;
        try
        {
            this.AcquireOnTable(table, resource, mode, session);
        }
        finally
        {
            session.WaitingOnKey = null;
        }
        manager.Release(resource, mode, session);
        return true;
    }

    /// <summary>
    /// The table lock this transaction — or, outside one, this statement —
    /// escalated <paramref name="table"/>'s row and key locks to, or null.
    /// </summary>
    private LockMode? EscalatedModeOf(HeapTable table)
    {
        if (this.Connection.CurrentTransaction is { } tx)
        {
            if (tx.EscalatedTables.Count != 0 && tx.EscalatedTables.Contains(table))
                return LockMode.Exclusive;
            return tx.SharedEscalatedTables.Count != 0 && tx.SharedEscalatedTables.Contains(table) ? LockMode.Shared : null;
        }
        return this.CurrentStatement.EscalatedTables is { } escalated && escalated.TryGetValue(table, out var mode) ? mode : null;
    }

    /// <summary>
    /// Counts <paramref name="added"/> more row or key locks this statement
    /// took on <paramref name="table"/>, and escalates them to one table lock
    /// once the estimated total reaches the next attempt point — S when every
    /// lock counted is S-family, X otherwise, as real's escalation of a
    /// REPEATABLE READ scan, a SERIALIZABLE scan, an <c>UPDLOCK</c> scan and
    /// an UPDATE shows (probed 2026-09-28 against SQL Server 2025). A table
    /// set <c>LOCK_ESCALATION = DISABLE</c> keeps its locks however many.
    /// </summary>
    private void CountLocksForEscalation(HeapTable table, int added, bool exclusive, bool rowLock = false, bool rowsKeyLocked = false)
    {
        if (table.LockEscalation == 1)
            return;
        var tallies = this.CurrentStatement.LockTallies ??= new(ReferenceEqualityComparer.Instance);
        if (!tallies.TryGetValue(table, out var tally))
            tallies[table] = tally = new LockEscalationTally();
        tally.Exclusive |= exclusive;
        tally.RowsKeyLocked |= rowsKeyLocked;
        if (added == 0 || (rowLock && tally.RowsKeyLocked))
            return;
        tally.Count += added;
        var estimated = EstimatedLockTotal(table, tally.Count);
        if (estimated >= tally.NextAttempt && !this.TryEscalate(table, tally.Exclusive))
            tally.NextAttempt = estimated + LockEscalationTally.RetryInterval;
    }

    /// <summary>
    /// Real's escalation counts every lock the statement holds on the table:
    /// its row or key locks, the table's own intent lock, and the intent lock
    /// on each page those rows sit on. Pages aren't locked here, so their
    /// share is estimated from the heap's rows per page — which is what puts
    /// a SERIALIZABLE scan of a narrow table over real's threshold at about
    /// 6 235 keys rather than 6 250 (probed 2026-09-28 against SQL Server
    /// 2025).
    /// </summary>
    private static int EstimatedLockTotal(HeapTable table, int locks)
    {
        var heap = table.Heap;
        var rows = heap.RowCount;
        var pages = heap.Pages.Count;
        var pageLocks = rows <= 0 ? 0 : Math.Min(pages, (((long)locks * pages) + rows - 1) / rows);
        return (int)Math.Min(int.MaxValue, locks + 1 + pageLocks);
    }

    /// <summary>
    /// Replaces the row and key locks this transaction (or statement) holds on
    /// <paramref name="table"/> with one table S or X. Real escalates only when
    /// the table lock is grantable at once, and otherwise keeps the fine-grained
    /// locks and tries again later, so this never waits: false means refused.
    /// The table's intent lock folds into the escalated mode, as real reports
    /// a single OBJECT S / X afterwards.
    /// </summary>
    private bool TryEscalate(HeapTable table, bool exclusive)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        var mode = exclusive ? LockMode.Exclusive : LockMode.Shared;
        if (manager.TryAcquire(table.TableDataLock, mode, connection.Session, 0) is not (LockAcquireOutcome.Granted or LockAcquireOutcome.GrantedAfterWait))
            return false;

        var tx = connection.CurrentTransaction;
        var held = tx?.HeldLocks ?? this.StatementSchemaLocks;
        held.Add((table.TableDataLock, mode));
        ReleaseEscalated(held, table, mode, manager, connection.Session);
        if (tx is not null)
            ReleaseEscalated(this.StatementSchemaLocks, table, mode, manager, connection.Session);

        if (tx is null)
            (this.CurrentStatement.EscalatedTables ??= new(ReferenceEqualityComparer.Instance))[table] = mode;
        else if (exclusive)
            _ = tx.EscalatedTables.Add(table);
        else
            _ = tx.SharedEscalatedTables.Add(table);
        return true;
    }

    // Drops the holds a table lock in `mode` covers from `held`: every row and
    // key lock on the table an X covers, only the S-family ones an S does, and
    // the table's intent lock the escalated mode subsumes.
    private static void ReleaseEscalated(List<(LockResource Resource, LockMode Mode)> held, HeapTable table, LockMode mode, LockManager manager, SessionToken session)
    {
        var exclusive = mode == LockMode.Exclusive;
        for (var i = held.Count - 1; i >= 0; i--)
        {
            var (resource, heldMode) = held[i];
            var covered = ReferenceEquals(resource, table.TableDataLock)
                ? heldMode == LockMode.IntentShared || (exclusive && heldMode == LockMode.IntentExclusive)
                : ReferenceEquals(resource.OwningTable, table)
                    && (exclusive || heldMode is LockMode.Shared or LockMode.RangeSharedShared);
            if (!covered)
                continue;
            manager.Release(resource, heldMode, session);
            held.RemoveAt(i);
        }
    }

    /// <summary>
    /// Tables this batch has already fenced whole for SERIALIZABLE phantom
    /// protection. Purely an idempotency guard: the fallback is decided per
    /// materialization, and a source can be re-enumerated many times (a
    /// correlated subquery's inner side), so without this the whole key space
    /// would be walked and re-covered per pass.
    /// </summary>
    private readonly HashSet<HeapTable> serializableTableFallbacks = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Discharges a SERIALIZABLE / <c>HOLDLOCK</c> reader's outstanding
    /// phantom protection for a read whose shape offers no narrower interval
    /// (a whole-table scan, a non-sargable predicate, a predicate on an
    /// unindexed or non-leading column, a cross-column <c>OR</c>): the whole
    /// key space. Over a clustered table that is every key of the clustered
    /// index plus the infinity anchor in the plan's range mode, as real's scan
    /// takes; over a heap it is a table S, real's OBJECT S for a heap scan
    /// (probed 2026-09-28 against SQL Server 2025).
    /// <para>
    /// A no-op for every other plan, for a source whose fence is already
    /// settled (the keys the seek path locked cover the same obligation more
    /// narrowly), and for a table this batch already fenced whole.
    /// </para>
    /// </summary>
    public void EnsureSerializableTableLock(HeapTable table, in DataLockPlan plan)
    {
        if (plan.SerializableRangeMode is not { } mode || plan.Fence is not { Settled: false } fence)
            return;
        fence.Settled = true;
        if (!this.serializableTableFallbacks.Add(table))
            return;
        if (KeyLockGroup.RowGroupOf(table) is { } group)
        {
            List<KeyFenceInterval> everything = [KeyFenceInterval.Everything];
            fence.NoteKeysFenced(table, group, everything, lookupRows: false);
            this.AcquireKeyFence(table, group, group.Commons, everything, mode, KeyFenceKind.Read, lookupRows: false);
        }
        else
        {
            this.AcquireSerializableTableS(table);
        }
    }

    /// <summary>
    /// A heap's SERIALIZABLE fence: table S, folding in the IS the read took
    /// when its source resolved, which real reports converted to the one
    /// OBJECT S.
    /// </summary>
    private void AcquireSerializableTableS(HeapTable table)
    {
        this.AcquireTransactionLock(table.TableDataLock, LockMode.Shared, this.noWaitTables.Contains(table));
        var connection = this.Connection;
        var held = connection.CurrentTransaction?.HeldLocks ?? this.StatementSchemaLocks;
        var index = held.IndexOf((table.TableDataLock, LockMode.IntentShared));
        if (index >= 0)
        {
            connection.Simulation.LockManager.Release(table.TableDataLock, LockMode.IntentShared, connection.Session);
            held.RemoveAt(index);
        }
    }

    /// <summary>
    /// Takes the key locks a SERIALIZABLE access of <paramref name="group"/>'s
    /// index over <paramref name="intervals"/> takes, in <paramref name="mode"/>:
    /// every key inside each interval and the first key past it, the infinity
    /// anchor when none follows — real's next-key locking, which is what fences
    /// the gap below each key and past the read's end (probed 2026-09-28
    /// against SQL Server 2025). An interval naming one full key of a unique
    /// index is the exception real makes when it finds that key: a reader
    /// takes a plain key lock there (a row S for the clustered key), a writer
    /// nothing past its own row X; a miss locks the next key like any range.
    /// <para>
    /// Bounds are compared in <paramref name="commons"/>, the types the
    /// predicate promoted the key columns to. A clustered key's anchors are
    /// also tested against another session's lock on the anchored row, which
    /// real meets as the key lock itself; <paramref name="lookupRows"/> takes
    /// the row S real's lookup from a nonclustered index takes on each row it
    /// reads. Past the escalation threshold the whole read escalates instead.
    /// </para>
    /// </summary>
    public void AcquireKeyFence(
        HeapTable table, KeyLockGroup group, SqlType[] commons, List<KeyFenceInterval> intervals, LockMode mode, KeyFenceKind kind, bool lookupRows)
    {
        var keyPart = LockManager.KeyPartOf(mode);
        if (this.EscalatedModeOf(table) is { } escalated && (escalated == LockMode.Exclusive || keyPart == LockMode.Shared))
            return;

        var heap = table.Heap;
        var cache = HeapSeekCache.For(heap);
        var requests = new List<(SqlValueKey? Key, LockMode Mode, (int Page, int Slot)[] Rids, bool Lookup)>();
        var cap = LockEscalationTally.FirstAttempt;
        var capped = false;
        foreach (var interval in intervals)
        {
            var anchors = cache.KeyLockAnchors(heap, table.StoredColumns, heap, group.Ordinals, commons,
                interval.Lower, interval.LowerInclusive, interval.Upper, interval.UpperInclusive, capped ? int.MaxValue : cap);
            if (anchors is null)
            {
                // Too many keys to lock one by one: escalate if that can be
                // granted, else lock them all after all.
                if (table.LockEscalation != 1 && this.TryEscalate(table, keyPart != LockMode.Shared))
                    return;
                capped = true;
                anchors = cache.KeyLockAnchors(heap, table.StoredColumns, heap, group.Ordinals, commons,
                    interval.Lower, interval.LowerInclusive, interval.Upper, interval.UpperInclusive, int.MaxValue)!;
            }

            if (interval.UniquePoint && anchors.Count == 2)
            {
                // The one key a unique equality can find.
                var (_, rids) = anchors[0];
                if (kind == KeyFenceKind.Read && mode == LockMode.RangeSharedShared && group.IsRowGroup)
                {
                    foreach (var (page, slot) in rids)
                        this.AcquireRowLockTxScoped(table, page, slot, LockMode.Shared);
                }
                else if (kind == KeyFenceKind.Read && !group.IsRowGroup)
                {
                    requests.Add((anchors[0].Key, keyPart, rids, lookupRows));
                }
                continue;
            }

            for (var i = 0; i < anchors.Count; i++)
                requests.Add((anchors[i].Key, mode, anchors[i].Rids, lookupRows && !group.IsRowGroup && i < anchors.Count - 1));
        }

        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        var session = connection.Session;
        var noWait = this.noWaitTables.Count != 0 && this.noWaitTables.Contains(table);
        var acquired = 0;
        foreach (var (key, requestMode, rids, lookup) in requests)
        {
            var resource = group.GetOrCreate(key is { } k ? Normalize(group, k) : null);
            if (!manager.IsHeldBy(resource, requestMode, session))
            {
                this.AcquireTransactionLock(resource, requestMode, noWait);
                acquired++;
            }

            if (group.IsRowGroup)
            {
                var rowMode = LockManager.KeyPartOf(requestMode);
                foreach (var rid in rids)
                {
                    if (table.RowLocks.TryGetValue(rid, out var rowLock) && manager.HasIncompatibleHolderOtherThan(rowLock, rowMode, session))
                    {
                        this.AcquireOnTable(table, rowLock, rowMode, session);
                        manager.Release(rowLock, rowMode, session);
                    }
                }
            }
            else if (lookup)
            {
                foreach (var (page, slot) in rids)
                    this.AcquireRowLockTxScoped(table, page, slot, LockMode.Shared);
            }
        }

        this.CountLocksForEscalation(table, acquired, exclusive: keyPart != LockMode.Shared, rowsKeyLocked: group.IsRowGroup);
    }

    // An anchor found through a seek-cache entry keyed in the predicate's
    // promoted types, restated in the column types every writer's test reads
    // its row in — a widening, so narrowing back is exact.
    private static SqlValueKey Normalize(KeyLockGroup group, SqlValueKey key)
    {
        var restated = false;
        for (var i = 0; i < key.ComponentCount && !restated; i++)
            restated = !key.ComponentAt(i).Type.Equals(group.Commons[i]);
        if (!restated)
            return key;
        var components = new SqlValue[key.ComponentCount];
        for (var i = 0; i < components.Length; i++)
            components[i] = key.ComponentAt(i).CoerceTo(group.Commons[i]);
        return new SqlValueKey(components);
    }

    /// <summary>
    /// Tests the key lock another session may hold on the clustered key of
    /// the row <paramref name="image"/> is, against a row lock in
    /// <paramref name="mode"/> — a reader's S meeting a writer's
    /// <c>RangeX-X</c>, an <c>UPDLOCK</c> reader's U meeting another's
    /// <c>RangeS-U</c> — waiting it out, or reporting false for a
    /// <c>READPAST</c> reader to skip the row.
    /// </summary>
    private bool TestRowKeyLock(HeapTable table, byte[] image, LockMode mode, bool skipIfBlocked, bool unlockedWhenClean = false)
    {
        if (KeyLockGroup.ClusteredOwner(table) is not { } owner
            || !table.KeyLockGroups.TryGetValue(owner, out var group)
            || Volatile.Read(ref group.Holds) == 0
            || !group.TryReadKey(image, out var key)
            || group.Find(key) is not { } resource)
        {
            return true;
        }
        if (unlockedWhenClean
            && !this.Connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(
                resource, mode, this.Connection.Session, holder => ChangedWhileOpen(table, holder)))
        {
            return true;
        }
        return this.TestKeyLock(table, resource, mode, skipIfBlocked);
    }

    // Whether the table changed since the holder's transaction began — or the
    // holder is a statement running outside one — which is when real's READ
    // COMMITTED read takes the S that meets its lock.
    private static bool ChangedWhileOpen(HeapTable table, SessionToken holder) =>
        holder.TryResolveOwner()?.CurrentTransaction is not { } transaction
        || Volatile.Read(ref table.Heap.LastModifiedEpoch) >= transaction.BeginEpoch;

    // Waits until no other session holds `resource` incompatibly with `mode`
    // — an instant-duration acquire, never held — or, for a READPAST reader,
    // reports that it would have to.
    private bool TestKeyLock(HeapTable table, LockResource resource, LockMode mode, bool skipIfBlocked)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, mode, connection.Session))
            return true;
        if (skipIfBlocked)
            return false;
        this.AcquireOnTable(table, resource, mode, connection.Session);
        manager.Release(resource, mode, connection.Session);
        return true;
    }

    // A write's tests against every index somebody holds a key lock in: an
    // insert tests the gap its key lands in, a delete the lock on its key,
    // and an update's pre-image only the clustered key's (the rewrite site
    // tests a nonclustered index once it knows the update touches it).
    private void TestKeyLocksForWrite(HeapTable table, byte[] image, RowLockPurpose purpose)
    {
        foreach (var (_, group) in table.KeyLockGroups)
        {
            if (Volatile.Read(ref group.Holds) == 0
                || (purpose == RowLockPurpose.UpdatePreImage && !group.IsRowGroup)
                || !group.TryReadKey(image, out var key))
            {
                continue;
            }
            if (purpose == RowLockPurpose.Insert)
                this.TestGapLock(table, group, key);
            else if (group.Find(key) is { } resource)
                _ = this.TestKeyLock(table, resource, LockMode.Exclusive, skipIfBlocked: false);
        }
    }

    // Real's insert-range test: RangeI-N, instant, on the first key above the
    // one being written — the anchor whose range the new key lands in — or on
    // the infinity anchor past the last key. A key written into a gap this
    // session itself range-locks splits that gap, so the new key takes
    // RangeX-X to keep the lower half fenced, as real's does (probed
    // 2026-09-28 against SQL Server 2025: a HOLDLOCK MERGE's insert).
    private void TestGapLock(HeapTable table, KeyLockGroup group, SqlValueKey key)
    {
        var next = HeapSeekCache.For(table.Heap).NextKeyAbove(table.Heap, table.StoredColumns, table.Heap, group.Ordinals, group.Commons, key);
        if (group.Find(next) is not { } resource)
            return;
        _ = this.TestKeyLock(table, resource, LockMode.RangeInsertNull, skipIfBlocked: false);
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (!manager.HoldsRangeMode(resource, connection.Session))
            return;
        var split = group.GetOrCreate(key);
        if (!manager.IsHeldBy(split, LockMode.RangeExclusiveExclusive, connection.Session))
            this.AcquireTransactionLock(split, LockMode.RangeExclusiveExclusive);
    }

    /// <summary>
    /// Blocks the caller until no other session's key lock fences the gap a
    /// new row <paramref name="image"/> lands in, on any index. Runs on every
    /// writer whatever its own isolation level — a range lock's whole purpose
    /// is to fence writers that know nothing about it — and mirrors real's
    /// RangeI-N: an instant-duration mode on the next key, taken only to test
    /// the gap and released the moment it is granted, so it never shows up in
    /// a lock snapshot taken after the write.
    /// <para>
    /// Costs nothing when no key lock is held anywhere on the table (the
    /// <see cref="HeapTable.ActiveKeyRangeLocks"/> read).
    /// </para>
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 1222 on lock timeout, Msg 1205 when waiting would close a cycle.
    /// </exception>
    public void ProbeKeyLocksForInsert(HeapTable table, ReadOnlySpan<byte> image)
    {
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0)
            this.TestKeyLocksForWrite(table, image.ToArray(), RowLockPurpose.Insert);
    }

    /// <summary>
    /// The rewrite site's half of an UPDATE's key-lock tests, called with the
    /// row at <paramref name="pageIndex"/> / <paramref name="slotIndex"/> still
    /// holding its old image, once the new one is known: a nonclustered index whose row the update touches — its key
    /// moves, or a column it carries changes — has its old key's lock tested,
    /// and any index whose key moves has the gap the new key lands in tested,
    /// since a row moving into a fenced gap is a phantom its old image can't
    /// reveal. An update touching no column of an index takes nothing there,
    /// as on real (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public void ProbeKeyLocksForUpdate(HeapTable table, int pageIndex, int slotIndex, byte[] newImage)
    {
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) == 0 || table.Heap.ReadSlotBytes(pageIndex, slotIndex) is not { } oldImage)
            return;
        foreach (var (_, group) in table.KeyLockGroups)
        {
            if (Volatile.Read(ref group.Holds) == 0)
                continue;
            var hadOld = group.TryReadKey(oldImage, out var oldKey);
            var hasNew = group.TryReadKey(newImage, out var newKey);
            var moved = hadOld != hasNew || (hadOld && !oldKey.Equals(newKey));
            if (!group.IsRowGroup && hadOld && (moved || group.RowChanges(oldImage, newImage)) && group.Find(oldKey) is { } held)
                _ = this.TestKeyLock(table, held, LockMode.Exclusive, skipIfBlocked: false);
            if (moved && hasNew)
                this.TestGapLock(table, group, newKey);
        }
    }

    /// <summary>
    /// Wraps <paramref name="table"/>'s row enumeration with per-row
    /// conflict checks driven by <paramref name="plan"/>. Each yielded
    /// row's RID flows through <see cref="TouchRowForRead"/>; READPAST-
    /// blocked rows are silently skipped. <paramref name="batch"/> is the
    /// executing one: a SELECT's FROM source holds a
    /// <see cref="LockCheckedScanRows"/> that calls here per execution, since
    /// a cached plan is replayed by other sessions.
    /// </summary>
    public static IEnumerable<byte[]> WrapWithRowConflictChecks(HeapTable table, BatchContext batch, DataLockPlan plan)
    {
        // Reaching here means nothing narrowed the source to an index seek, so
        // a SERIALIZABLE reader is about to scan the whole table and its
        // phantom fence has to be the whole key space. Deliberately inside the
        // iterator body: the seek decision is made after the FROM source is
        // built, and a seeked source is a different enumerable that never runs
        // this one.
        // A key another session's uncommitted delete took away is waited out
        // first, as a seek waits before fencing: fencing first held the next
        // key's range through the wait, which that session's reinsert of the
        // key then waited on — a deadlock real never meets.
        var snapshotXid = batch.ResolveSnapshotXidForRead(table, plan);
        if (snapshotXid is null && !plan.NoLockReader && !plan.SkipBlockedRows)
            batch.AwaitUncommittedDeletes(table);
        batch.EnsureSerializableTableLock(table, plan);
        var io = batch.Connection.StatementIo?.Touch(table);
        _ = io?.ScanCount += 1;
        var lastPage = -1;
        // Null unless the statement reads a row locator (see RowLocator).
        var addresses = batch.CurrentStatement.RowAddresses;
        // A clustered table scans in its key's order (see ClusteredScan); a
        // snapshot read sweeps the heap and its version chains as before.
        var orderKeys = snapshotXid is null ? new List<SqlValueKey>() : null;
        // A write in flight may delete and reinsert a key the scan has yet to
        // reach, the row landing at an address the heap's walk already passed:
        // the scan follows the key order then, keys in hand, even over a heap
        // whose own order is the key's.
        var followKeys = !plan.NoLockReader && !plan.SkipBlockedRows
            && (Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActiveUpdateLocks) != 0 || !table.SupersededKeyImages.IsEmptyLockFree());
        if (snapshotXid is null && batch.LockingScanOrder(table, orderKeys!, followKeys) is { } clusteredOrder)
        {
            var seen = new HashSet<(int, int)>();
            // Rows read in the key's place when the key's row was deleted
            // under the scan, read before the scan moves on.
            var followed = new Stack<((int Page, int Slot) Address, SqlValueKey? Key)>();
            for (var position = 0; position < clusteredOrder.Count; position++)
            {
                followed.Push((clusteredOrder[position], orderKeys!.Count == clusteredOrder.Count ? orderKeys[position] : null));
                while (followed.TryPop(out var next))
                {
                    var ((pageIndex, slotIndex), key) = next;
                    if (!seen.Add((pageIndex, slotIndex)))
                        continue;
                    // Deleted since the order was taken, or by the writer the
                    // row's lock waited out — a write that may put the key back
                    // at another address, as a DELETE and INSERT of one key in a
                    // transaction does, or here again, as its rollback does.
                    // Real's scan meets the key under its writer's X, waits,
                    // and reads what the key holds then.
                    byte[]? bytes = null;
                    if (!table.Heap.IsSlotTombstoned(pageIndex, slotIndex))
                    {
                        var sequence = table.Heap.WriteSequence;
                        if (!batch.TouchRowForRead(table, pageIndex, slotIndex, plan))
                            continue;
                        if (table.Heap.ReadLiveRow(pageIndex, slotIndex) is { } read)
                            bytes = batch.SettleReadCommitted(table, pageIndex, slotIndex, plan, read, sequence);
                    }
                    if (bytes is null)
                    {
                        if (batch.RowsOfDeletedKey(table, pageIndex, slotIndex, plan, key, null, out var movedKey) is { } moved)
                        {
                            _ = seen.Remove((pageIndex, slotIndex));
                            for (var m = moved.Count - 1; m >= 0; m--)
                                followed.Push((moved[m], movedKey));
                        }
                        continue;
                    }
                    io?.Enter(pageIndex, ref lastPage);
                    addresses?.Record(bytes, pageIndex, slotIndex);
                    yield return bytes;
                }
            }
            yield break;
        }
        var heap = table.Heap;
        if (snapshotXid is { } sx)
        {
            // A deleted slot comes through too: the snapshot may predate the
            // delete. Each slot resolves once, against the chain as it stood
            // with the slot, so a write landing mid-scan neither hides a row
            // nor shows it twice.
            foreach (var (pageIndex, slotIndex, read, sequence) in heap.EnumerateSlots())
            {
                io?.Enter(pageIndex, ref lastPage);
                var resolved = Storage.VersionStore.ReadSnapshotSlot(table, (pageIndex, slotIndex), read, sequence, sx, batch.Connection.Session);
                if (resolved is null)
                    continue;
                addresses?.Record(resolved, pageIndex, slotIndex);
                yield return resolved;
            }
            // A chain whose address the heap no longer has — its page trimmed
            // or truncated away — may still hold a version the snapshot
            // predates.
            foreach (var (address, chain) in table.Heap.RowVersions)
            {
                if (heap.TryReadSlot(address.PageIndex, address.SlotIndex, out _, out _))
                    continue;
                var resolved = Storage.VersionStore.ResolveTombstonedSlotForSnapshot(chain, sx, batch.Connection.Session);
                if (resolved is null)
                    continue;
                addresses?.Record(resolved, address.PageIndex, address.SlotIndex);
                yield return resolved;
            }
        }
        else
        {
            using var rows = heap.EnumerateRowsWithAddress().GetEnumerator();
            // Rows read early by key after the row the scan waited on was
            // deleted under it, which the walk then passes over.
            HashSet<(int, int)>? followed = null;
            while (true)
            {
                // Read just ahead of the row, so a write since — one a wait in
                // the probe below outlasted — shows as a moved sequence.
                var sequence = heap.WriteSequence;
                if (!rows.MoveNext())
                    break;
                var (pageIndex, slotIndex, bytes) = rows.Current;
                if (followed is not null && followed.Contains((pageIndex, slotIndex)))
                    continue;
                io?.Enter(pageIndex, ref lastPage);
                if (!batch.TouchRowForRead(table, pageIndex, slotIndex, plan))
                    continue;
                // A wait on the row's writer can outlast the image read before
                // it: when anything wrote the heap since, read the row as it
                // stands — gone if the write deleted it, when the rows now
                // carrying its key are read in its place — as real's read
                // after the wait does.
                if (heap.WriteSequence != sequence)
                {
                    if (heap.ReadLiveRow(pageIndex, slotIndex) is not { } read
                        || batch.SettleReadCommitted(table, pageIndex, slotIndex, plan, read, sequence) is not { } current)
                    {
                        if (batch.RowsOfDeletedKey(table, pageIndex, slotIndex, plan, null, bytes, out var movedKey) is { } moved)
                        {
                            // A row read in the key's place can be deleted in
                            // turn before its lock is had: the key is followed
                            // on from it.
                            var pending = new Stack<(int Page, int Slot)>(moved.AsEnumerable().Reverse());
                            for (var hops = 0; pending.TryPop(out var at);)
                            {
                                if (!(followed ??= []).Add(at) || !batch.TouchRowForRead(table, at.Page, at.Slot, plan))
                                    continue;
                                if (heap.ReadLiveRow(at.Page, at.Slot) is { } followedRow)
                                {
                                    addresses?.Record(followedRow, at.Page, at.Slot);
                                    yield return followedRow;
                                }
                                else if (++hops < Simulation.MaxTargetWalks
                                    && batch.RowsOfDeletedKey(table, at.Page, at.Slot, plan, movedKey, null, out _) is { } next)
                                {
                                    _ = followed.Remove(at);
                                    for (var m = next.Count - 1; m >= 0; m--)
                                        pending.Push(next[m]);
                                }
                            }
                        }
                        continue;
                    }
                    bytes = current;
                }
                addresses?.Record(bytes, pageIndex, slotIndex);
                yield return bytes;
            }
        }
    }

    /// <summary>
    /// A READ COMMITTED read's image of a row it probed (<see cref="TouchRowForRead"/>),
    /// <paramref name="bytes"/> as read after the probe, with the heap's
    /// <see cref="Heap.WriteSequence"/> at <paramref name="sequence"/> before it. The probe holds nothing once it
    /// returns, so a write that took the row between the probe and the read
    /// — and possibly rolled back since — may be what was read: when the heap
    /// moved meanwhile, the row is probed and read again until two reads
    /// agree. A locking read holds its row lock across the read and is
    /// returned as read; null when the row is gone, or a READPAST read skips
    /// it.
    /// </summary>
    internal byte[]? SettleReadCommitted(HeapTable table, int pageIndex, int slotIndex, in DataLockPlan plan, byte[] bytes, int sequence)
    {
        if (plan.RowMode is not null || plan.NoLockReader || table.Heap.WriteSequence == sequence)
            return bytes;
        for (var attempt = 1; attempt < Simulation.MaxTargetWalks; attempt++)
        {
            if (!this.TouchRowForRead(table, pageIndex, slotIndex, plan) || table.Heap.ReadLiveRow(pageIndex, slotIndex) is not { } again)
                return null;
            if (again.AsSpan().SequenceEqual(bytes))
                return again;
            bytes = again;
        }
        return bytes;
    }

    /// <summary>
    /// Returns the snapshot Xid governing this read, or <c>null</c> when
    /// the read should use the standard lock-based path (default RC without
    /// RCSI, RR, SERIALIZABLE, etc). Allocates the per-transaction SI Xid
    /// lazily on first call; allocates the per-statement RCSI Xid lazily
    /// on first user-table read inside the statement.
    /// </summary>
    internal long? ResolveSnapshotXidForRead(HeapTable table, in DataLockPlan plan)
    {
        // A write's target reads the live rows, whatever the isolation level.
        if (plan.WriteTargetAddresses is not null)
            return null;
        var snapshotXid = this.ResolveTableSnapshotXid(table);
        // A locking read reads the latest committed row under its locks. A
        // SNAPSHOT transaction's snapshot is still fixed by it, as its first
        // data access, which is what a later Msg 3960 judges by.
        return plan.LockingRead && !table.IsMemoryOptimized ? null : snapshotXid;
    }

    /// <summary>The snapshot an unhinted read of <paramref name="table"/> reads at, by the session's level and the table's database.</summary>
    private long? ResolveTableSnapshotXid(HeapTable table)
    {
        if (table.IsTableVariable || IsLocalTempName(table.Name))
            return null;
        if (Simulation.SystemHeapTables.Values.Contains(table))
            return null;

        var connection = this.Connection;
        var isolation = connection.SessionIsolationLevel;
        var simulation = connection.Simulation;

        // A memory-optimized table is always read at a snapshot: the
        // transaction's, taken at its first such read, or the statement's
        // outside one.
        if (table.IsMemoryOptimized && connection.CurrentTransaction is null)
            return this.StatementSnapshotXid(simulation);
        if (isolation == System.Data.IsolationLevel.Snapshot || table.IsMemoryOptimized)
        {
            if (connection.CurrentTransaction is { } tx)
            {
                if (tx.SnapshotXid is null)
                {
                    // Registered before the stamp is read, under one no later
                    // than it: a version sweep running meanwhile either sees
                    // the registration or read its cutoff before this stamp
                    // existed, so it can't drop a version the snapshot reads.
                    var transactionId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tx);
                    simulation.ActiveSnapshotTxs[connection.Session] = new ActiveSnapshotRegistration(transactionId, simulation.CurrentTransactionCommitId, connection.Spid);
                    var snapshotXid = simulation.CurrentTransactionCommitId;
                    tx.SnapshotXid = snapshotXid;
                    simulation.ActiveSnapshotTxs[connection.Session] = new ActiveSnapshotRegistration(transactionId, snapshotXid, connection.Spid);
                }
                return tx.SnapshotXid;
            }
            // An autocommit statement under SNAPSHOT is a transaction of its
            // own, its snapshot the statement's.
            return this.StatementSnapshotXid(simulation);
        }

        // RCSI is the *table's* database's flag, not the session's — a session
        // in a non-RCSI database reading a three-part name into an RCSI one
        // reads versioned, and the reverse blocks (probe-confirmed).
        if (isolation == System.Data.IsolationLevel.ReadCommitted && this.DatabaseFor(table).ReadCommittedSnapshot)
            return this.StatementSnapshotXid(simulation);

        return null;
    }

    /// <summary>
    /// The statement's snapshot (<see cref="RcsiStatementSnapshotXid"/>),
    /// taken at its first read that needs one. The session registers it
    /// first (<see cref="SessionToken.StatementSnapshotXid"/>), under a stamp
    /// no later than the one it reads, as a SNAPSHOT transaction registers
    /// its own: the version sweep a concurrent commit runs either sees the
    /// registration or read its cutoff before this stamp existed, so it can't
    /// drop a version the statement reads. Without it a commit landing
    /// mid-read collected the versions of the rows it wrote, and the read saw
    /// their new images beside the old images of rows it had already passed.
    /// </summary>
    private long StatementSnapshotXid(Simulation simulation)
    {
        if (this.RcsiStatementSnapshotXid is { } taken)
            return taken;
        var session = this.Connection.Session;
        if (Volatile.Read(ref session.StatementSnapshotXid) == long.MaxValue)
            _ = Interlocked.Exchange(ref session.StatementSnapshotXid, simulation.CurrentTransactionCommitId);
        var stamp = simulation.CurrentTransactionCommitId;
        this.RcsiStatementSnapshotXid = stamp;
        return stamp;
    }

    /// <summary>
    /// Reader-side row-touch helper called per row during enumeration.
    /// Based on <paramref name="plan"/>:
    /// <list type="bullet">
    /// <item><see cref="DataLockPlan.NoLockReader"/> — no probe, no acquire (dirty read).</item>
    /// <item><see cref="DataLockPlan.RowMode"/> non-null — acquire that mode
    /// tx-scoped, unless <see cref="DataLockPlan.SkipBlockedRows"/> is set and
    /// another connection already holds the row incompatibly, which is the
    /// <c>UPDLOCK, READPAST</c> / <c>XLOCK, READPAST</c> pair real answers by
    /// leaving the row out of the result (probe-confirmed).</item>
    /// <item>Else (RC probe path) — check for a row-X holder by another
    /// connection. If found and <see cref="DataLockPlan.SkipBlockedRows"/>
    /// is true, return false so the caller skips this row (READPAST).
    /// Otherwise wait for the row by transiently acquiring + releasing
    /// row-S (matches real SQL Server's "wait for committed row" semantic).
    /// A SERIALIZABLE read whose fence may have missed the row's key locks
    /// it first (<see cref="PhantomFenceState.FencedGroup"/>).</item>
    /// </list>
    /// Returns true when the row should be yielded; false on READPAST skip.
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public bool TouchRowForRead(HeapTable table, int pageIndex, int slotIndex, in DataLockPlan plan)
    {
        if (plan.NoLockReader)
            return true;
        if (plan.RowMode is { } mode)
        {
            // READPAST beside UPDLOCK / XLOCK: probe before acquiring, since
            // the acquire would block. The RC path's ActiveDataWriters gate
            // doesn't serve here — it counts row-X grants only, and the holder
            // this pair most often meets is another UPDLOCK reader's row-U.
            if (plan.SkipBlockedRows
                && ((table.RowLocks.TryGetValue((pageIndex, slotIndex), out var held)
                        && this.Connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(held, mode, this.Connection.Session))
                    || (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0
                        && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } image
                        && !this.TestRowKeyLock(table, image, mode, skipIfBlocked: true))))
            {
                return false;
            }
            this.AcquireRowLockTxScoped(table, pageIndex, slotIndex, mode);
            if (plan.SnapshotConflictCheck)
                Storage.VersionStore.CheckSnapshotUpdateConflict(this, table, (pageIndex, slotIndex));
            return true;
        }
        // The key lock comes first: it fences the writers yet to reach the row,
        // and the probe then waits out one that reached it before.
        if (plan.Fence is { FencedGroup: { } fenced } fence && Volatile.Read(ref table.Heap.MutationGeneration) != fence.FencedGeneration)
        {
            if (fence.LocksRows)
            {
                this.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Shared);
                return true;
            }
            this.HoldFencedRowKey(table, fenced, pageIndex, slotIndex, plan.SerializableRangeMode!.Value);
        }
        return this.ProbeRowForRead(table, pageIndex, slotIndex, plan);
    }

    /// <summary>
    /// Takes <paramref name="mode"/> on the clustered key the row at
    /// <paramref name="pageIndex"/> / <paramref name="slotIndex"/> carries,
    /// unless the session holds it already — the key of a row a SERIALIZABLE
    /// scan reads that its fence, taken over the keys the table held before,
    /// may have missed (<see cref="PhantomFenceState.FencedGroup"/>).
    /// </summary>
    private void HoldFencedRowKey(HeapTable table, KeyLockGroup group, int pageIndex, int slotIndex, LockMode mode)
    {
        if (this.EscalatedModeOf(table) is not null)
            return;
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        var session = connection.Session;
        var heap = table.Heap;
        // A key the row is rewritten off while the lock is waited for leaves
        // the lock on a key it no longer carries: lock the one it carries then.
        for (var attempt = 0; attempt < Simulation.MaxTargetWalks; attempt++)
        {
            if (heap.ReadSlotBytes(pageIndex, slotIndex) is not { } image || !group.TryReadKey(image, out var key))
                return;
            var resource = group.GetOrCreate(key);
            if (manager.IsHeldBy(resource, mode, session))
                return;
            this.AcquireTransactionLock(resource, mode, this.noWaitTables.Count != 0 && this.noWaitTables.Contains(table));
            this.CountLocksForEscalation(table, 1, exclusive: false, rowsKeyLocked: true);
            if (heap.ReadSlotBytes(pageIndex, slotIndex) is not { } locked || !group.TryReadKey(locked, out var lockedKey) || lockedKey.Equals(key))
                return;
        }
    }

    // The READ COMMITTED probe: waits out a writer holding the row, or reports
    // false for a READPAST reader to skip it.
    [MethodImpl(Tiering.OptimizeFirstCall)]
    private bool ProbeRowForRead(HeapTable table, int pageIndex, int slotIndex, in DataLockPlan plan)
    {
        var connection = this.Connection;
        // TABLOCKX's table X has waited every writer out already.
        if (plan.SnapshotConflictCheck)
            Storage.VersionStore.CheckSnapshotUpdateConflict(this, table, (pageIndex, slotIndex));
        if (connection.CurrentTransaction is { } tx && tx.EscalatedTables.Contains(table))
            return true;
        // Lock-free table-level gate: with no data-X held anywhere on the
        // table, every row is committed-readable, so skip the per-row lock-
        // resource intern and the manager gate entirely. This is the
        // read-mostly common path — under concurrency it keeps readers off
        // the single LockManager gate, which a per-row probe would otherwise
        // serialize on. A row-X grant increments ActiveDataWriters under the
        // gate before the writer mutates the heap, so a zero read here means
        // no conflicting writer had started.
        if (Volatile.Read(ref table.ActiveDataWriters) == 0)
            return true;
        // A RangeX-X counts as a writer too: a SERIALIZABLE UPDATE's lock on
        // the key past its range refuses this read though no row X is there —
        // when the table changed while the holder's transaction was open,
        // which is when real's READ COMMITTED takes its S at all (probed
        // 2026-09-28 against SQL Server 2025: the same lock behind an XLOCK
        // read, or a DELETE that removed nothing, lets the read through until
        // a write lands).
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0
            && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } keyedImage
            && !this.TestRowKeyLock(table, keyedImage, LockMode.Shared, plan.SkipBlockedRows, unlockedWhenClean: true))
        {
            return false;
        }
        // A writer is somewhere on the table; check this specific row. Use a
        // non-interning lookup — a row with no holder interned can't be in
        // conflict, so reading it through costs no allocation and no gate.
        if (!table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource))
            return true;
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, LockMode.Shared, connection.Session))
            return true;
        if (plan.SkipBlockedRows)
            return false;
        // Wait for the row's writers to drain. Transient acquire-release
        // matches real SQL Server's RC pattern: "block until committed,
        // then release immediately."
        this.AcquireOnTable(table, resource, LockMode.Shared, connection.Session);
        manager.Release(resource, LockMode.Shared, connection.Session);
        return true;
    }

    /// <summary>
    /// Takes over the statement-scoped locks <paramref name="child"/> holds, so
    /// they release with this batch's statement: a child batch that binds a
    /// body for this statement to run is never dispatched, and the locks would
    /// otherwise outlive the session (held by a SPID nobody can find, blocking
    /// every later Sch-M on the objects the body names).
    /// </summary>
    public void AdoptStatementLocks(BatchContext child)
    {
        this.StatementSchemaLocks.AddRange(child.StatementSchemaLocks);
        child.StatementSchemaLocks.Clear();
    }

    /// <summary>
    /// Releases every lock acquired during the current statement. Called by
    /// the dispatch loop in a <c>finally</c> at statement end. Safe to call
    /// even when the list is empty; safe to call multiple times (the list
    /// clears between calls so the second is a no-op).
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public void ReleaseStatementSchemaLocks()
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        // Release in reverse acquisition order — symmetric to a stack of
        // acquires. Phase 0 has no order-dependent semantics in release
        // (every Sch-S / Sch-M release pulses the gate independently), but
        // the LIFO discipline matches structured-locking convention.
        for (var i = this.StatementSchemaLocks.Count - 1; i >= 0; i--)
        {
            var (resource, mode) = this.StatementSchemaLocks[i];
            manager.Release(resource, mode, connection.Session);
        }
        this.StatementSchemaLocks.Clear();
        this.noWaitTables.Clear();
    }

    /// <summary>
    /// The lock timeout to use for <paramref name="table"/>: zero when the
    /// current statement named it with a <c>NOWAIT</c> table hint, else the
    /// session's own <see cref="SimulatedDbConnection.LockTimeoutMillis"/>.
    /// </summary>
    private int LockTimeoutFor(HeapTable table)
        => table.IsMemoryOptimized || (this.noWaitTables.Count != 0 && this.noWaitTables.Contains(table)) ? 0 : this.Connection.LockTimeoutMillis;

    /// <summary>
    /// The isolation rules a memory-optimized table is reached under (probed
    /// 2026-10-02 against SQL Server 2025), each ending the batch and rolling
    /// back as under <c>XACT_ABORT</c>: a SNAPSHOT session is refused outright
    /// (Msg 41332) and a READ UNCOMMITTED one too (Msg 10794); a REPEATABLE
    /// READ or SERIALIZABLE session needs the <c>SNAPSHOT</c> hint (Msg
    /// 41333); and a READ COMMITTED read inside an explicit or implicit
    /// transaction needs an isolation hint or the database's
    /// <c>MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT</c> (Msg 41368) — a write that
    /// reads nothing, an <c>INSERT</c>'s target, needs neither. A natively
    /// compiled module's atomic block sets its own level and is exempt.
    /// </summary>
    private void CheckMemoryOptimizedIsolation(HeapTable table, Selection.TableHintInfo hints, bool readsRows)
    {
        var connection = this.Connection;
        if (connection.AtomicBlockDepth > 0)
            return;
        switch (connection.SessionIsolationLevel)
        {
            case System.Data.IsolationLevel.Snapshot:
                throw SimulatedSqlException.MemoryOptimizedUnderSnapshotSession();
            case System.Data.IsolationLevel.ReadUncommitted:
                throw SimulatedSqlException.IsolationLevelNotSupportedWithMemoryOptimized("READ UNCOMMITTED");
            case System.Data.IsolationLevel.RepeatableRead or System.Data.IsolationLevel.Serializable:
                if (!hints.Snapshot)
                    throw SimulatedSqlException.MemoryOptimizedNeedsSnapshot();
                return;
        }
        if (!readsRows || hints.Snapshot || hints.Repeatable || hints.Serializable || connection.CurrentTransaction is null)
            return;
        if ((this.DatabaseFor(table).Switches & DatabaseSwitches.MemoryOptimizedElevateToSnapshot) == 0)
            throw SimulatedSqlException.MemoryOptimizedReadCommittedInTransaction();
    }

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/>, one of
    /// <paramref name="table"/>'s locks, under the table's timeout. A
    /// memory-optimized table never waits: a lock another session holds is the
    /// write conflict real refuses at once (Msg 41302) rather than the
    /// timeout (Msg 1222) a zero wait would otherwise raise.
    /// </summary>
    private void AcquireOnTable(HeapTable table, LockResource resource, LockMode mode, SessionToken session, bool sweepAbandoned = true, bool conflictIsDelete = false)
    {
        try
        {
            this.Connection.Simulation.LockManager.Acquire(resource, mode, session, this.LockTimeoutFor(table), sweepAbandoned);
        }
        catch (SimulatedSqlException timeout) when (timeout.Number == 1222 && table.IsMemoryOptimized)
        {
            throw SimulatedSqlException.MemoryOptimizedWriteConflict(conflictIsDelete);
        }
    }
}
