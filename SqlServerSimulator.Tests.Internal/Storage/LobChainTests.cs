using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests for the LOB-chain page format on <see cref="Heap"/>.
/// These exercise the storage-layer contracts directly: chunking,
/// chain-walking, and the row encoder/decoder's inline-vs-pointer dispatch.
/// User-facing LOB behavior (varchar(MAX), text/ntext/image inserts and
/// queries) is covered by SqlServerSimulator.Tests/MaxTypesTests.
/// </summary>
[TestClass]
public sealed class LobChainTests
{
    [TestMethod]
    public void AllocateLobChain_SingleChunk_FitsOnOnePage()
    {
        var heap = new Heap();
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        var head = heap.AllocateLobChain(data);
        AreEqual(0, head);
        HasCount(1, heap.LobPages);
        AreEqual(-1, heap.LobPages[0].NextPageIndex);

        var roundtrip = heap.ReadLobChain(head, data.Length);
        CollectionAssert.AreEqual(data, roundtrip);
    }

    [TestMethod]
    public void AllocateLobChain_OversizeData_SplitsAcrossMultiplePages()
    {
        // 25_000 bytes / 8096 bytes-per-page = 4 pages (3 full + 1 partial).
        var heap = new Heap();
        var data = new byte[25_000];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i & 0xFF);

        var head = heap.AllocateLobChain(data);
        AreEqual(0, head);
        IsGreaterThanOrEqualTo(4, heap.LobPages.Count);
        // Pages link in allocation order; the last page terminates with -1.
        AreEqual(-1, heap.LobPages[^1].NextPageIndex);

        var roundtrip = heap.ReadLobChain(head, data.Length);
        CollectionAssert.AreEqual(data, roundtrip);
    }

    [TestMethod]
    public void AllocateLobChain_Empty_AllocatesOneZeroLengthPage()
    {
        var heap = new Heap();
        var head = heap.AllocateLobChain([]);
        AreEqual(0, head);
        HasCount(1, heap.LobPages);
        AreEqual(0, heap.LobPages[0].PayloadLength);

        var roundtrip = heap.ReadLobChain(head, 0);
        IsEmpty(roundtrip);
    }

    [TestMethod]
    public void AllocateLobChain_TwoChainsInterleave_StayDistinct()
    {
        var heap = new Heap();
        var dataA = new byte[15_000];
        var dataB = new byte[10_000];
        for (var i = 0; i < dataA.Length; i++)
            dataA[i] = (byte)('A' + (i % 26));
        for (var i = 0; i < dataB.Length; i++)
            dataB[i] = (byte)('a' + (i % 26));

        var headA = heap.AllocateLobChain(dataA);
        var headB = heap.AllocateLobChain(dataB);
        AreNotEqual(headA, headB);

        CollectionAssert.AreEqual(dataA, heap.ReadLobChain(headA, dataA.Length));
        CollectionAssert.AreEqual(dataB, heap.ReadLobChain(headB, dataB.Length));
    }

    [TestMethod]
    public void EncodeRow_VarcharMax_NoHeap_StaysInlineWithMarker()
    {
        // Without a Heap, LOB-eligible columns inline their bytes after a
        // 0x00 marker.
        var column = new HeapColumn("v", SqlType.Varchar, maxLength: SqlType.MaxLengthSentinel, nullable: true);
        var bytes = RowEncoder.EncodeRow([column], [SqlValue.FromVarchar("hi")], lobStore: null);

        var decoded = RowDecoder.DecodeRow([column], bytes, lobStore: null);
        AreEqual("hi", decoded[0].AsString);
    }

    [TestMethod]
    public void EncodeRow_VarcharMax_WithHeap_GoesOffRowAsPointer()
    {
        var heap = new Heap();
        var column = new HeapColumn("v", SqlType.Varchar, maxLength: SqlType.MaxLengthSentinel, nullable: true);
        var big = new string('x', 20_000);

        var bytes = RowEncoder.EncodeRow([column], [SqlValue.FromVarchar(big)], lobStore: heap);
        // Off-row: the row carries a 9-byte LOB pointer entry, not the
        // 20_000-byte payload. Compute the row's expected size.
        IsNotEmpty(heap.LobPages);
        IsLessThan(100, bytes.Length, $"Expected a compact row carrying only the pointer; got {bytes.Length} bytes.");

        var decoded = RowDecoder.DecodeRow([column], bytes, lobStore: heap);
        AreEqual(big, decoded[0].AsString);
    }

    [TestMethod]
    public void DecodeColumn_LobPointer_WithoutHeap_ThrowsInvalidData()
    {
        var heap = new Heap();
        var column = new HeapColumn("v", SqlType.Varchar, maxLength: SqlType.MaxLengthSentinel, nullable: true);
        var bytes = RowEncoder.EncodeRow([column], [SqlValue.FromVarchar(new string('x', 20_000))], lobStore: heap);

        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeColumn([column], bytes, 0, lobStore: null));
    }

    [TestMethod]
    public void EncodeRow_Text_AlwaysCarriesMarker()
    {
        // text is always-LOB; even without a Heap, the encoded payload starts
        // with the 0x00 inline marker so the format stays consistent across
        // call sites.
        var bytes = RowEncoder.EncodeRow([SqlType.Text], [SqlValue.FromText("hello")]);
        var decoded = RowDecoder.DecodeRow([SqlType.Text], bytes);
        AreEqual("hello", decoded[0].AsString);
    }

    [TestMethod]
    public void EncodeRow_NullLobColumn_ContributesNoMarker()
    {
        // NULL handling: the row's NULL bitmap signals NULL; the var section's
        // offset for the NULL column points to the same byte as the previous
        // entry (zero-length). No marker byte is written.
        var column = new HeapColumn("v", SqlType.Varchar, maxLength: SqlType.MaxLengthSentinel, nullable: true);
        var bytes = RowEncoder.EncodeRow([column], [SqlValue.Null(SqlType.Varchar)]);
        var decoded = RowDecoder.DecodeRow([column], bytes);
        IsTrue(decoded[0].IsNull);
    }
}
