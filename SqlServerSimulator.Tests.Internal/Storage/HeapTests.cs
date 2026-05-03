using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
[TestClass]
public class HeapTests
{
    [TestMethod]
    public void NewHeap_HasNoPagesUntilFirstInsert()
    {
        var heap = new Heap();
        AreEqual(0, heap.Pages.Count);
        AreEqual(0, heap.RowCount);
        IsFalse(heap.EnumerateRows().Any());
    }

    [TestMethod]
    public void Insert_SingleRow_AllocatesFirstPage()
    {
        var heap = new Heap();
        heap.Insert([1, 2, 3]);
        AreEqual(1, heap.Pages.Count);
        AreEqual(1, heap.RowCount);
    }

    [TestMethod]
    public void Insert_RowsExceedingPageCapacity_AllocatesSecondPageAndLinks()
    {
        var heap = new Heap();
        var big = new byte[Heap.MaxRowSize];
        heap.Insert(big);                 // page 0 holds one max-sized row
        heap.Insert(big);                 // forces a new page

        AreEqual(2, heap.Pages.Count);
        AreEqual(1, heap.Pages[0].NextPageIndex);
        AreEqual(-1, heap.Pages[0].PrevPageIndex);
        AreEqual(0, heap.Pages[1].PrevPageIndex);
        AreEqual(-1, heap.Pages[1].NextPageIndex);

        AreEqual(2, heap.RowCount);
    }

    [TestMethod]
    public void EnumerateRows_WalksAllPagesInOrder()
    {
        var heap = new Heap();

        // Pack many ~512-byte rows so we span a few pages.
        var rowsInserted = new List<byte[]>();
        for (var i = 0; i < 30; i++)
        {
            var row = new byte[512];
            row[0] = (byte)i;
            rowsInserted.Add(row);
            heap.Insert(row);
        }

        IsTrue(heap.Pages.Count >= 2, "Expected the heap to span at least two pages.");

        var rowsRead = heap.EnumerateRows().ToList();
        AreEqual(rowsInserted.Count, rowsRead.Count);
        for (var i = 0; i < rowsInserted.Count; i++)
            CollectionAssert.AreEqual(rowsInserted[i], rowsRead[i]);
    }

    [TestMethod]
    public void Insert_RowExceedingMaxRowSize_Throws()
    {
        var heap = new Heap();
        var oversize = new byte[Heap.MaxRowSize + 1];
        _ = Throws<NotSupportedException>(() => heap.Insert(oversize));

        // No page should have been allocated.
        AreEqual(0, heap.Pages.Count);
    }

    [TestMethod]
    public void Insert_RowAtMaxRowSize_Succeeds()
    {
        var heap = new Heap();
        var row = new byte[Heap.MaxRowSize];
        heap.Insert(row);
        AreEqual(1, heap.RowCount);
    }

    [TestMethod]
    public void RowCount_AggregatesAcrossPages()
    {
        var heap = new Heap();
        var halfish = new byte[HeapPage.MaxRowPayload / 3];

        heap.Insert(halfish);     // fits in page 0
        heap.Insert(halfish);     // fits in page 0
        heap.Insert(halfish);     // page 0 may or may not fit a third copy; either way row count is correct

        AreEqual(3, heap.RowCount);

        // Across however many pages the heap chose, EnumerateRows returns all 3 rows.
        AreEqual(3, heap.EnumerateRows().Count());
    }
}
