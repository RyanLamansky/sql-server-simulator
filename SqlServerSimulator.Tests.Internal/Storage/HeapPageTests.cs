using System.Buffers.Binary;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
[TestClass]
public class HeapPageTests
{
    [TestMethod]
    public void NewPage_HasHeapTypeAndEmptySlotDir()
    {
        var page = new HeapPage();
        AreEqual((byte)0x01, page.PageType);
        AreEqual((ushort)0, page.SlotCount);
        AreEqual((ushort)HeapPage.HeaderSize, page.FreeSpacePointer);
        AreEqual(-1, page.NextPageIndex);
        AreEqual(-1, page.PrevPageIndex);
        HasCount(HeapPage.PageSize, page.Bytes);
    }

    [TestMethod]
    public void TryInsert_SingleRow_AppendsRowAndSlot()
    {
        var page = new HeapPage();
        IsTrue(page.TryInsert([0x80, 0x81, 0x82]));

        AreEqual((ushort)1, page.SlotCount);
        AreEqual((ushort)(HeapPage.HeaderSize + 3), page.FreeSpacePointer);

        // Slot 0 sits at the last 2 bytes of the page and points to HeaderSize.
        var slot0 = BinaryPrimitives.ReadUInt16LittleEndian(page.Bytes[(HeapPage.PageSize - 2)..HeapPage.PageSize]);
        AreEqual((ushort)HeapPage.HeaderSize, slot0);

        var rows = page.EnumerateRows().ToList();
        HasCount(1, rows);
        CollectionAssert.AreEqual(new byte[] { 0x80, 0x81, 0x82 }, rows[0]);
    }

    [TestMethod]
    public void TryInsert_MultipleRows_PreservesOrderAndPacksContiguously()
    {
        var page = new HeapPage();
        IsTrue(page.TryInsert([0x80]));
        IsTrue(page.TryInsert([0x90, 0x91]));
        IsTrue(page.TryInsert([0xA0, 0xA1, 0xA2]));

        AreEqual((ushort)3, page.SlotCount);

        var rows = page.EnumerateRows().ToList();
        HasCount(3, rows);
        CollectionAssert.AreEqual(new byte[] { 0x80 }, rows[0]);
        CollectionAssert.AreEqual(new byte[] { 0x90, 0x91 }, rows[1]);
        CollectionAssert.AreEqual(new byte[] { 0xA0, 0xA1, 0xA2 }, rows[2]);
    }

    [TestMethod]
    public void TryInsert_ReturnsFalseWhenRowPlusSlotWontFit()
    {
        var page = new HeapPage();

        // Fill with one large row that consumes almost the whole page.
        var big = new byte[HeapPage.MaxRowPayload];
        IsTrue(page.TryInsert(big));

        // No room for another row + slot.
        IsFalse(page.TryInsert([1]));
    }

    [TestMethod]
    public void TryInsert_RowPayloadAtMaximum_FitsExactly()
    {
        var page = new HeapPage();
        var big = new byte[HeapPage.MaxRowPayload];
        for (var i = 0; i < big.Length; i++)
            big[i] = (byte)(i & 0xFF);

        IsTrue(page.TryInsert(big));
        AreEqual(0, page.FreeSpace);

        var rows = page.EnumerateRows().ToList();
        HasCount(1, rows);
        CollectionAssert.AreEqual(big, rows[0]);
    }

    [TestMethod]
    public void TryInsert_RowExceedsMaximum_Throws()
    {
        var page = new HeapPage();
        var oversize = new byte[HeapPage.MaxRowPayload + 1];
        _ = Throws<NotSupportedException>(() => page.TryInsert(oversize));
    }

    [TestMethod]
    public void NextPrevPageIndex_RoundTrip()
    {
        var page = new HeapPage
        {
            NextPageIndex = 7,
            PrevPageIndex = 3,
        };
        AreEqual(7, page.NextPageIndex);
        AreEqual(3, page.PrevPageIndex);
    }

    [TestMethod]
    public void SlotDirectory_GrowsBackwardFromTail()
    {
        // After 3 inserts, the slot directory occupies the last 6 bytes.
        var page = new HeapPage();
        IsTrue(page.TryInsert([0xAA]));
        IsTrue(page.TryInsert([0xBB]));
        IsTrue(page.TryInsert([0xCC]));

        var slot0 = BinaryPrimitives.ReadUInt16LittleEndian(page.Bytes[(HeapPage.PageSize - 2)..HeapPage.PageSize]);
        var slot1 = BinaryPrimitives.ReadUInt16LittleEndian(page.Bytes[(HeapPage.PageSize - 4)..(HeapPage.PageSize - 2)]);
        var slot2 = BinaryPrimitives.ReadUInt16LittleEndian(page.Bytes[(HeapPage.PageSize - 6)..(HeapPage.PageSize - 4)]);

        AreEqual((ushort)HeapPage.HeaderSize, slot0);
        AreEqual((ushort)(HeapPage.HeaderSize + 1), slot1);
        AreEqual((ushort)(HeapPage.HeaderSize + 2), slot2);
    }

    [TestMethod]
    public void EnumerateRows_CopiesOutOfPage()
    {
        var page = new HeapPage();
        IsTrue(page.TryInsert([42]));

        var first = page.EnumerateRows().Single();
        first[0] = 99;

        // Mutating the returned array doesn't affect the next enumeration.
        var second = page.EnumerateRows().Single();
        AreEqual((byte)42, second[0]);
    }
}
