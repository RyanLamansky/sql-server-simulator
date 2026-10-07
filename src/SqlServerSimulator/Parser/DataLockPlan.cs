using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-row lock strategy returned by
/// <see cref="BatchContext.AcquireDataLockIfApplicable(HeapTable, Selection.TableHintInfo, bool, bool)"/>
/// after acquiring the appropriate table-level data lock. Encodes
/// what the per-row enumeration / mutation code should do at each row
/// touch — acquire a particular mode tx-scoped, probe-only with optional
/// READPAST-skip on conflict, or skip the row check entirely (dirty-read).
/// </summary>
internal readonly struct DataLockPlan(
    LockMode? rowMode,
    bool rowTxScoped,
    bool skipBlockedRows,
    bool noLockReader,
    LockMode? serializableRangeMode = null,
    PhantomFenceState? fence = null,
    bool lockingRead = false,
    bool snapshotConflictCheck = false,
    RowAddressMap? writeTargetAddresses = null)
{
    /// <summary>
    /// Lock mode to acquire per touched row, or <c>null</c> when no row-
    /// level acquire happens. Null + <see cref="NoLockReader"/>=false means
    /// "probe only" (RC reader fast path); null + NoLockReader=true means
    /// "skip everything" (NOLOCK / READ UNCOMMITTED).
    /// </summary>
    public readonly LockMode? RowMode = rowMode;

    /// <summary>
    /// True when per-row acquires hold to COMMIT / ROLLBACK; false (in
    /// practice unused — every populated <see cref="RowMode"/> in phase 1b
    /// is tx-scoped) marks the lock as statement-scoped.
    /// </summary>
    public readonly bool RowTxScoped = rowTxScoped;

    /// <summary>
    /// <c>READPAST</c> hint: instead of waiting on a row-X holder, skip the
    /// blocked row entirely. Used by the reader's per-row touch.
    /// </summary>
    public readonly bool SkipBlockedRows = skipBlockedRows;

    /// <summary>
    /// True when the reader should bypass even the probe-and-wait path —
    /// dirty-read semantics. Set by <c>WITH (NOLOCK)</c> hint and by the
    /// <c>READ UNCOMMITTED</c> session isolation level.
    /// </summary>
    public readonly bool NoLockReader = noLockReader;

    /// <summary>
    /// The range mode a SERIALIZABLE / <c>HOLDLOCK</c> reader still owes
    /// phantom protection in, or <c>null</c> for every other reader. The
    /// table-level acquisition made so far doesn't cover it, so whoever
    /// consumes the source settles it — the index-seek path by locking the
    /// keys the predicate's interval reaches in this mode, every other path by
    /// falling back to the whole key space a scan reaches, or the table S a
    /// heap scan takes (<c>BatchContext.EnsureSerializableTableLock</c>).
    /// <para>
    /// <c>RangeS-S</c> for a plain SERIALIZABLE read, <c>RangeS-U</c> when it
    /// carries <c>UPDLOCK</c> and <c>RangeX-X</c> when it carries
    /// <c>XLOCK</c> — the modes real reports for the same three reads.
    /// </para>
    /// </summary>
    public readonly LockMode? SerializableRangeMode = serializableRangeMode;

    /// <summary>
    /// Shared cell recording whether this source's fence has been settled,
    /// non-null exactly when <see cref="SerializableRangeMode"/> is. The plan
    /// is a struct copied into <c>FromSource.HeapPlan</c> and into the row
    /// wrapper, so the reference is what makes the two see one another's work:
    /// a source that locked the keys its predicate reaches must not then have
    /// the whole key space added on top by the scan wrapper, which would
    /// re-block the keys the seek deliberately left free.
    /// </summary>
    public readonly PhantomFenceState? Fence = fence;

    /// <summary>
    /// The read carries a hint that makes it a locking read
    /// (<c>Selection.TableHintInfo.LocksRead</c>), so row versioning —
    /// <c>READ_COMMITTED_SNAPSHOT</c>'s statement snapshot or a
    /// <c>SNAPSHOT</c> transaction's — doesn't answer it: it waits out
    /// writers and reads the latest committed row under the locks it takes.
    /// </summary>
    public readonly bool LockingRead = lockingRead;

    /// <summary>
    /// The read carries <c>UPDLOCK</c>, <c>XLOCK</c> or <c>TABLOCKX</c>, which
    /// under <c>SNAPSHOT</c> isolation raise Msg 3960 on a row another
    /// transaction committed a change to after the snapshot, as the update
    /// they announce would (probed 2026-10-03 against SQL Server 2025).
    /// </summary>
    public readonly bool SnapshotConflictCheck = snapshotConflictCheck;

    /// <summary>
    /// Set on the plan of a joined <c>UPDATE</c> / <c>DELETE</c>'s target
    /// (<see cref="ForWriteTarget"/>): the map each row the target's scan or
    /// seek yields is recorded in against its heap address, which is how the
    /// write finds the row behind a join tuple. Scoped to the target's reads,
    /// unlike the statement's <see cref="StatementContext.RowAddresses"/>, so
    /// a partner source or a subquery reading the same table records nothing.
    /// </summary>
    public readonly RowAddressMap? WriteTargetAddresses = writeTargetAddresses;

    /// <summary>This plan with <see cref="LockingRead"/> and <see cref="SnapshotConflictCheck"/> set as given.</summary>
    public DataLockPlan WithVersioningRule(bool lockingRead, bool snapshotConflictCheck) =>
        new(this.RowMode, this.RowTxScoped, this.SkipBlockedRows, this.NoLockReader, this.SerializableRangeMode, this.Fence, lockingRead, snapshotConflictCheck, this.WriteTargetAddresses);

    /// <summary>
    /// Plan for a joined write's target read: the heap's live rows, every one
    /// recorded in <paramref name="addresses"/>, with no lock taken and no
    /// snapshot consulted — the write waits in U on each row it judges
    /// qualifying and X-locks the rows it keeps, as the single-table walk
    /// does, so a seek narrowing the read never changes its lock footprint.
    /// </summary>
    public static DataLockPlan ForWriteTarget(RowAddressMap addresses) =>
        new(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: true, writeTargetAddresses: addresses);

    /// <summary>
    /// Plan for sources where data locks don't apply (table variables,
    /// local temp tables, system tables). Acquires nothing; the reader /
    /// writer iterator's per-row touch is a no-op.
    /// </summary>
    public static readonly DataLockPlan Bypass = new(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: true);

    /// <summary>
    /// Plan for <c>WITH (NOLOCK)</c> / <c>READ UNCOMMITTED</c> isolation —
    /// reader skips conflict checks entirely.
    /// </summary>
    public static readonly DataLockPlan NoLock = new(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: true);
}

/// <summary>
/// One SERIALIZABLE / <c>HOLDLOCK</c> heap source's phantom-fence bookkeeping,
/// allocated with the source's <see cref="DataLockPlan"/> and shared by every
/// copy of it: whether the fence this source owes has been taken, whichever
/// form it took, and over which keys.
/// </summary>
internal sealed class PhantomFenceState
{
    /// <summary>
    /// Set once this source's fence — its keys, the whole key space or a table S — is
    /// held. Read by <c>BatchContext.EnsureSerializableTableLock</c>, whose
    /// fallback is otherwise reached from the scan wrapper even for a source
    /// the index-seek path already fenced with a range.
    /// </summary>
    public bool Settled;

    /// <summary>
    /// The lock group whose keys the fence locked — the clustered key's, or a
    /// heap's unique index read through row lookups — else <c>null</c>. The
    /// fence locks the keys the table held as it was taken, and a key a write
    /// in flight deleted then is in none of them — though the read, waiting
    /// that write out, reads the row its transaction put back under the key —
    /// so a row read after the table changed is locked as it is read
    /// (<c>BatchContext.TouchRowForRead</c>), as real's read locks each key
    /// it reaches.
    /// </summary>
    public KeyLockGroup? FencedGroup;

    /// <summary>The heap's <see cref="Heap.MutationGeneration"/> just before <see cref="FencedGroup"/>'s keys were read for the fence.</summary>
    public long FencedGeneration;

    /// <summary>
    /// Whether the fence locks the rows it finds rather than the keys — one of
    /// unique points alone, as real takes it, or a heap's index read through
    /// lookups — so a row read after the table changed takes its row S
    /// rather than its key's range lock.
    /// </summary>
    public bool LocksRows;

    /// <summary>
    /// Records that the fence about to be taken locks <paramref name="group"/>'s
    /// keys over <paramref name="intervals"/> as <paramref name="table"/> holds
    /// them now, looking each key's rows up when <paramref name="lookupRows"/>.
    /// </summary>
    public void NoteKeysFenced(HeapTable table, KeyLockGroup group, List<KeyFenceInterval> intervals, bool lookupRows)
    {
        if (!group.IsRowGroup && !lookupRows)
            return;
        this.FencedGroup = group;
        this.LocksRows = !group.IsRowGroup || intervals.TrueForAll(interval => interval.UniquePoint);
        this.FencedGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
    }
}

/// <summary>
/// Why a row lock is being taken, which decides what a held key lock on the
/// row's keys means to it: a read tests only the clustered key's lock in its
/// own mode; an insert tests the gap its keys land in (real's RangeI-N on the
/// next key); a delete tests the lock on each of its keys; an update's
/// pre-image tests the clustered key's lock alone, leaving a nonclustered
/// index to the rewrite site, which knows whether the update touches it.
/// </summary>
internal enum RowLockPurpose
{
    /// <summary>A reader's S / U / X on a row it reads.</summary>
    Read,

    /// <summary>A new row's key-range test, taken before it lands.</summary>
    Insert,

    /// <summary>The X on a row about to be deleted.</summary>
    Delete,

    /// <summary>The X on a row about to be rewritten, taken on its old image.</summary>
    UpdatePreImage,
}

/// <summary>
/// What a writer's target read holds on the row it is judging
/// (<see cref="BatchContext.AwaitTargetRow"/>,
/// <see cref="BatchContext.HoldQualifyingTargetRow"/>).
/// </summary>
[Flags]
internal enum TargetRowHold : byte
{
    /// <summary>Nothing yet: no other session's lock stood in the way.</summary>
    None = 0,

    /// <summary>The U the read waited in, held until the row's X or its rejection.</summary>
    Update = 1,

    /// <summary>The X the row's write takes, held to the transaction's end.</summary>
    Exclusive = 2,

    /// <summary>The row was deleted while the read waited on its writer.</summary>
    Gone = 4,
}

/// <summary>
/// Who takes a key fence, which decides how a hit on a unique key is locked:
/// a reader takes the plain key lock real takes there, a writer — an UPDATE,
/// a DELETE, a MERGE's probe — nothing past the X its write takes (probed
/// 2026-09-28 against SQL Server 2025).
/// </summary>
internal enum KeyFenceKind
{
    /// <summary>A SERIALIZABLE / HOLDLOCK read.</summary>
    Read,

    /// <summary>A SERIALIZABLE UPDATE / DELETE / MERGE.</summary>
    Write,
}

/// <summary>
/// One interval of an index's key space a fence reads: the bounds (tuples
/// possibly shorter than the key, compared in the types the predicate
/// promoted the key columns to), and
/// whether it pins every column of a unique key by equality — the shape real
/// locks with a plain key lock on a hit.
/// </summary>
internal readonly struct KeyFenceInterval(SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive, bool uniquePoint)
{
    /// <summary>Lower bound, or null for an open side.</summary>
    public readonly SqlValueKey? Lower = lower;

    /// <summary>Whether <see cref="Lower"/> is inside the interval.</summary>
    public readonly bool LowerInclusive = lowerInclusive;

    /// <summary>Upper bound, or null for an open side.</summary>
    public readonly SqlValueKey? Upper = upper;

    /// <summary>Whether <see cref="Upper"/> is inside the interval.</summary>
    public readonly bool UpperInclusive = upperInclusive;

    /// <summary>Whether the interval is one full key of a unique index.</summary>
    public readonly bool UniquePoint = uniquePoint;

    /// <summary>The whole key space — what a scan reaches.</summary>
    public static readonly KeyFenceInterval Everything = new(null, false, null, false, false);
}

/// <summary>
/// One step a cached statement's parse took that a replay takes again — a
/// lock on <see cref="Resource"/>, the <c>NOWAIT</c> hint on
/// <see cref="NoWaitTable"/>, or the permission check <see cref="Check"/> —
/// recorded so a plan-cache replay retakes it, in the parse's order, as the
/// replaying session (<c>BatchContext.TakeReplayedLocks</c>).
/// </summary>
internal readonly struct ReplayedLock
{
    public ReplayedLock(LockResource resource, LockMode mode, bool noWait, bool transactionScoped)
    {
        this.Resource = resource;
        this.Mode = mode;
        this.NoWait = noWait;
        this.TransactionScoped = transactionScoped;
    }

    public ReplayedLock(HeapTable noWaitTable) => this.NoWaitTable = noWaitTable;

    public ReplayedLock(CompiledPermissionCheck check) => this.Check = check;

    /// <summary>The locked resource; null for a <see cref="NoWaitTable"/> or <see cref="Check"/> step.</summary>
    public readonly LockResource? Resource;

    /// <summary>The mode taken on <see cref="Resource"/>.</summary>
    public readonly LockMode Mode;

    /// <summary>Whether the acquisition refused to wait.</summary>
    public readonly bool NoWait;

    /// <summary>Held to the transaction's end rather than the statement's.</summary>
    public readonly bool TransactionScoped;

    /// <summary>A table the statement named with a <c>NOWAIT</c> hint.</summary>
    public readonly HeapTable? NoWaitTable;

    /// <summary>A permission check the parse made (<c>PermissionEnforcement.CheckWhileParsing</c>).</summary>
    public readonly CompiledPermissionCheck? Check;
}
