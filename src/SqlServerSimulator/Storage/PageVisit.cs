using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A page a row-locking read reached without keeping a row or key lock there,
/// on which real keeps the intent it took on the page before it read the row
/// (probed 2026-10-16 against SQL Server 2025: a <c>REPEATABLE READ</c> scan
/// keeping one row's <c>KEY S</c> held <c>PAGE IS</c> on every page it read, a
/// seek of <c>id &gt; 8</c> on the page holding key 8 as well as the next one's
/// keys, and a seek of <c>id BETWEEN 5 AND 8</c> on the page of key 9, the
/// first key past it, which it read to find its end). The lock DMVs report the
/// page from the hold a read takes on the visit's <see cref="LockResource"/>,
/// in the intent its row lock's mode takes (<see cref="LockDmvs.IntentOf"/>);
/// the intent modes pass one another and the resource counts in none of the
/// table's counts, so nothing ever waits on one. A visit names rows, or a
/// position in an index's key order, rather than a page, since the page a row
/// sits on is only settled when a view lays the table out
/// (<see cref="LockPageMap"/>).
/// </summary>
internal sealed class PageVisit(HeapTable table, KeyLockGroup? group, SqlValueKey key, PageVisitBound bound)
{
    /// <summary>The table whose page it is.</summary>
    public readonly HeapTable Table = table;

    /// <summary>
    /// For the rows one scan let go of, its first and its latest, packed as
    /// <see cref="Pack"/> packs them: the scan reads the table's rows in the
    /// order its pages hold them, so it visited every page from the first's to
    /// the latest's. -1 for a key position.
    /// </summary>
    private long first = -1, last = -1;

    /// <summary>A run of rows one scan let go of, beginning at <paramref name="row"/>.</summary>
    public static PageVisit RowRun(HeapTable table, (int PageIndex, int SlotIndex) row)
    {
        var run = new PageVisit(table, null, SqlValueKey.Empty, PageVisitBound.AtOrBelow);
        run.first = run.last = Pack(row);
        return run;
    }

    /// <summary>Whether the visit is a run of rows rather than a key position.</summary>
    public bool IsRowRun => Volatile.Read(ref this.first) >= 0;

    /// <summary>The run's first and latest rows.</summary>
    public ((int PageIndex, int SlotIndex) First, (int PageIndex, int SlotIndex) Last) Rows =>
        (Unpack(Volatile.Read(ref this.first)), Unpack(Volatile.Read(ref this.last)));

    /// <summary>Takes <paramref name="row"/> into the run as its latest row.</summary>
    public void Extend((int PageIndex, int SlotIndex) row) => Volatile.Write(ref this.last, Pack(row));

    private static long Pack((int PageIndex, int SlotIndex) row) => ((long)row.PageIndex << 32) | (uint)row.SlotIndex;

    private static (int PageIndex, int SlotIndex) Unpack(long packed) => ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));

    /// <summary>The index whose key order <see cref="Key"/> is a position in, for a key position.</summary>
    public readonly KeyLockGroup? Group = group;

    /// <summary>The bound a seek began or ended at.</summary>
    public readonly SqlValueKey Key = key;

    /// <summary>Which entry beside <see cref="Key"/> the page is the page of.</summary>
    public readonly PageVisitBound Bound = bound;
}

/// <summary>
/// Which index entry beside a seek's bound a <see cref="PageVisit"/> names the
/// page of: where an ascending seek lands — the last entry at or below its
/// lower bound, as a B-tree's navigation does, or below it for an inclusive
/// bound on fewer columns than the key, which every entry carrying it sorts
/// above — and the entry past its upper bound it reads to find its end.
/// </summary>
internal enum PageVisitBound
{
    /// <summary>The last entry at or below the key; the first page when none is.</summary>
    AtOrBelow,

    /// <summary>The last entry below the key; the first page when none is.</summary>
    Below,

    /// <summary>The first entry above the key; none when none is.</summary>
    Above,

    /// <summary>The first entry at or above the key; none when none is.</summary>
    AtOrAbove,
}
