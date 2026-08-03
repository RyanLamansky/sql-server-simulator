using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-row lock strategy returned by
/// <see cref="BatchContext.AcquireDataLockIfApplicable(HeapTable, Selection.TableHintInfo, bool)"/>
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
    PhantomFenceState? fence = null)
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
    /// consumes the source settles it — the index-seek path by claiming a
    /// <see cref="Storage.KeyRange"/> over the predicate's interval in this
    /// mode, every other path by falling back to the table-S the whole-scan
    /// case needs (<c>BatchContext.EnsureSerializableTableLock</c>).
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
    /// a source that claimed a key range must not then have the whole-table S
    /// added on top by the scan wrapper, which would re-block the key space the
    /// range deliberately left free.
    /// </summary>
    public readonly PhantomFenceState? Fence = fence;

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
/// copy of it. One flag: whether the fence this source owes has been taken,
/// whichever form it took.
/// </summary>
internal sealed class PhantomFenceState
{
    /// <summary>
    /// Set once this source's fence — a key range or the whole-table S — is
    /// held. Read by <c>BatchContext.EnsureSerializableTableLock</c>, whose
    /// fallback is otherwise reached from the scan wrapper even for a source
    /// the index-seek path already fenced with a range.
    /// </summary>
    public bool Settled;
}
