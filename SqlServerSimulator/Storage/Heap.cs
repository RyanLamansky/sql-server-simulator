namespace SqlServerSimulator.Storage;

/// <summary>
/// A multi-page heap: an ordered list of <see cref="HeapPage"/>s linked
/// prev/next, into which rows are appended. Real SQL Server tracks page
/// allocations through PFS/GAM/SGAM/IAM pages and a heap object's first-page
/// pointer; we model just the linked list of data pages directly today, which
/// is enough to drive the encoder/decoder through real page-bounded storage
/// while leaving room for IAM/PFS modeling later.
/// </summary>
internal sealed class Heap
{
    /// <summary>Pages in this heap, in allocation order. Index <c>i</c> is reachable via prev/next links.</summary>
    public readonly List<HeapPage> Pages = [];

    /// <summary>
    /// Appends a row's encoded bytes to the heap. The active (last) page is
    /// tried first; on no-fit, a new page is allocated and linked, and the row
    /// goes there. Throws if the row exceeds <see cref="HeapPage.MaxRowPayload"/>
    /// (no overflow-page modeling yet).
    /// </summary>
    public void Insert(ReadOnlySpan<byte> row)
    {
        if (row.Length > HeapPage.MaxRowPayload)
            throw new NotSupportedException($"Row of {row.Length} bytes exceeds the per-page maximum of {HeapPage.MaxRowPayload}; row-overflow pages aren't modeled yet.");

        if (this.Pages.Count > 0 && this.Pages[^1].TryInsert(row))
            return;

        var newPage = new HeapPage();
        if (this.Pages.Count > 0)
        {
            var prevIndex = this.Pages.Count - 1;
            this.Pages[prevIndex].NextPageIndex = prevIndex + 1;
            newPage.PrevPageIndex = prevIndex;
        }
        this.Pages.Add(newPage);

        if (!newPage.TryInsert(row))
            throw new InvalidOperationException($"Row of {row.Length} bytes failed to insert into a fresh page; this should be impossible because the size was validated.");
    }

    /// <summary>
    /// Yields every row in every page in allocation order. Each yielded array
    /// is a fresh copy of the page bytes for that row.
    /// </summary>
    public IEnumerable<byte[]> EnumerateRows()
    {
        foreach (var page in this.Pages)
        {
            foreach (var row in page.EnumerateRows())
                yield return row;
        }
    }

    /// <summary>Total row count across all pages.</summary>
    public int RowCount
    {
        get
        {
            var count = 0;
            foreach (var page in this.Pages)
                count += page.SlotCount;
            return count;
        }
    }
}
