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
        IsEmpty(heap.Pages);
        AreEqual(0, heap.RowCount);
        IsFalse(heap.EnumerateRows().Any());
    }

    [TestMethod]
    public void Insert_SingleRow_AllocatesFirstPage()
    {
        var heap = new Heap();
        _ = heap.Insert([1, 2, 3]);
        HasCount(1, heap.Pages);
        AreEqual(1, heap.RowCount);
    }

    [TestMethod]
    public void Insert_RowsExceedingPageCapacity_AllocatesSecondPageAndLinks()
    {
        var heap = new Heap();
        var big = new byte[Heap.MaxRowSize];
        _ = heap.Insert(big);                 // page 0 holds one max-sized row
        _ = heap.Insert(big);                 // forces a new page

        HasCount(2, heap.Pages);
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
            _ = heap.Insert(row);
        }

        IsGreaterThanOrEqualTo(2, heap.Pages.Count, "Expected the heap to span at least two pages.");

        var rowsRead = heap.EnumerateRows().ToList();
        HasCount(rowsInserted.Count, rowsRead);
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
        IsEmpty(heap.Pages);
    }

    [TestMethod]
    public void Insert_RowAtMaxRowSize_Succeeds()
    {
        var heap = new Heap();
        var row = new byte[Heap.MaxRowSize];
        _ = heap.Insert(row);
        AreEqual(1, heap.RowCount);
    }

    [TestMethod]
    public void RowCount_AggregatesAcrossPages()
    {
        var heap = new Heap();
        var halfish = new byte[HeapPage.MaxRowPayload / 3];

        _ = heap.Insert(halfish);     // fits in page 0
        _ = heap.Insert(halfish);     // fits in page 0
        _ = heap.Insert(halfish);     // page 0 may or may not fit a third copy; either way row count is correct

        AreEqual(3, heap.RowCount);

        // Across however many pages the heap chose, EnumerateRows returns all 3 rows.
        AreEqual(3, heap.EnumerateRows().Count());
    }

    /// <summary>
    /// The scan reads each slot's directory entry once and walks slots inline
    /// rather than through a per-page enumerator. These pin the three things
    /// that walk has to keep straight: a tombstoned slot is skipped, a
    /// forwarded row is surfaced once at its <em>original</em> address carrying
    /// the relocated payload, and the relocation target is not surfaced a
    /// second time where it physically sits.
    /// </summary>
    [TestMethod]
    public void EnumerateRowsWithAddress_SkipsTombstonesAndFollowsForwardingOnce()
    {
        // Rows are at least as wide as the 6-byte forward pointer that
        // replaces one in place; the row encoder never emits a narrower row.
        var heap = new Heap();
        var (firstPage, firstSlot) = heap.Insert([1, 1, 1, 1, 1, 1, 1, 1]);
        var (secondPage, secondSlot) = heap.Insert([2, 2, 2, 2, 2, 2, 2, 2]);
        var (thirdPage, thirdSlot) = heap.Insert([3, 3, 3, 3, 3, 3, 3, 3]);

        heap.DeleteAt(secondPage, secondSlot);

        // Growing the row past its slot extent relocates it and leaves a
        // forwarding pointer at the original address.
        var grown = new byte[HeapPage.MaxRowPayload / 2];
        grown[0] = 9;
        heap.UpdateAt(thirdPage, thirdSlot, grown);

        var scanned = heap.EnumerateRowsWithAddress().ToList();
        HasCount(2, scanned);
        AreEqual((firstPage, firstSlot), (scanned[0].PageIndex, scanned[0].SlotIndex));
        CollectionAssert.AreEqual(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1 }, scanned[0].Bytes);
        AreEqual((thirdPage, thirdSlot), (scanned[1].PageIndex, scanned[1].SlotIndex));
        HasCount(grown.Length, scanned[1].Bytes);
        AreEqual((byte)9, scanned[1].Bytes[0]);
    }

    /// <summary>
    /// Delete-then-insert churn draws from the reclaimable-page candidates
    /// instead of appending pages, which is what bounds <c>Pages.Count</c> by
    /// the working set. The insert path consults that candidate set on every
    /// row the tail page can't hold, so the walk has to stay correct as well as
    /// cheap: it enumerates the concurrent set directly rather than snapshotting
    /// its keys.
    /// </summary>
    [TestMethod]
    public void DeleteInsertChurn_ReusesPagesInsteadOfAppending()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, "create table t (id int not null primary key, pad char(400) not null)");
        for (var i = 0; i < 200; i++)
            Exec(connection, $"insert t values ({i}, 'x')");
        var pagesWhenFull = HeapFor(connection, "t").Pages.Count;
        IsGreaterThanOrEqualTo(2, pagesWhenFull, "Expected 200 padded rows to span several pages.");

        for (var round = 0; round < 5; round++)
        {
            Exec(connection, "delete t");
            for (var i = 0; i < 200; i++)
                Exec(connection, $"insert t values ({i}, 'x')");
        }

        AreEqual(200, (int)Scalar(connection, "select count(*) from t")!);
        IsLessThanOrEqualTo(
            2 * pagesWhenFull,
            HeapFor(connection, "t").Pages.Count,
            $"Five rounds of full delete-and-refill should reuse the reclaimed pages, not append about 6x the {pagesWhenFull} a single fill needs.");
    }

    private static Heap HeapFor(SimulatedDbConnection connection, string table) =>
        connection.CurrentDatabase.Schemas[Database.DefaultSchemaName].HeapTables[table].Heap;

    private static void Exec(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static object? Scalar(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// The payload each scanned row carries is a copy: writing through it must
    /// not reach the page, since every consumer of a scan holds the array.
    /// </summary>
    [TestMethod]
    public void EnumerateRowsWithAddress_YieldsACopyOfThePagePayload()
    {
        var heap = new Heap();
        _ = heap.Insert([7, 7, 7, 7, 7, 7, 7, 7]);
        var (_, _, bytes) = heap.EnumerateRowsWithAddress().Single();
        bytes[0] = 42;
        CollectionAssert.AreEqual(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 }, heap.EnumerateRows().Single());
    }
}
