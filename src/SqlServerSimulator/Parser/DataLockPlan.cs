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
    bool serializableRangeReader = false)
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
    /// True for a SERIALIZABLE / <c>HOLDLOCK</c> reader, whose phantom
    /// protection is still owed when the plan is handed back: the table-level
    /// acquisition so far is only IS. Whoever consumes the source settles it —
    /// the index-seek path by claiming a <see cref="Storage.KeyRange"/> over
    /// the predicate's interval, every other path by falling back to the
    /// table-S the whole-scan case needs
    /// (<c>BatchContext.EnsureSerializableTableLock</c>).
    /// </summary>
    public readonly bool SerializableRangeReader = serializableRangeReader;

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
