using System.Buffers.Binary;
using System.Diagnostics;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A single 8KB heap page: structurally aligned with SQL Server's data-page
/// shape (fixed-size header, row data growing forward, slot directory growing
/// backward from the page's tail). Holds a sequence of opaque row payloads;
/// per-row encoding is the encoder's concern (<see cref="RowEncoder"/>),
/// not the page's.
/// </summary>
/// <remarks>
/// <para>
/// Layout (offsets into <see cref="Bytes"/>):
/// <list type="table">
/// <item><description>[0]      Page type (1 byte). <c>0x01</c> = heap data page.</description></item>
/// <item><description>[1-2]    Slot count (UInt16 LE).</description></item>
/// <item><description>[3-4]    Free-space pointer (UInt16 LE) — next free byte after the row data; starts at <see cref="HeaderSize"/>.</description></item>
/// <item><description>[5-8]    Next page index (Int32 LE) — -1 if none. Index into the owning <see cref="Heap"/>.</description></item>
/// <item><description>[9-12]   Prev page index (Int32 LE) — -1 if none.</description></item>
/// <item><description>[13-95]  Reserved (zero) for future header fields.</description></item>
/// <item><description>[96 .. FreeSpacePointer)         Row data, packed in insertion order.</description></item>
/// <item><description>[FreeSpacePointer .. slotDirStart) Free space.</description></item>
/// <item><description>[slotDirStart .. <see cref="PageSize"/>) Slot directory — slot <c>i</c> is a UInt16 LE at byte <c>PageSize - 2*(i+1)</c>, holding the absolute offset of row <c>i</c>'s payload start.</description></item>
/// </list>
/// </para>
/// <para>
/// Real SQL Server's header is 96 bytes containing fields like page ID, file
/// ID, allocation-unit ID, transaction info, torn-bits, etc.; we model only
/// the few fields the simulator needs today and reserve the rest. The high-
/// level shape (header / forward-growing rows / backward-growing slot dir)
/// matches the publicly documented record format.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebugDisplay(),nq}")]
internal sealed class HeapPage
{
    public const int PageSize = 8192;

    public const int HeaderSize = 96;

    /// <summary>Largest row payload (in bytes) that can fit on an empty page (one row plus one 2-byte slot).</summary>
    public const int MaxRowPayload = PageSize - HeaderSize - 2;

    private const byte HeapDataPageType = 0x01;

    /// <summary>
    /// High bit of a slot's 16-bit value flags the slot as deleted (tombstone).
    /// The remaining 15 bits hold the row's byte offset within the page; offsets
    /// fit in 13 bits (max 8190) so the high bit is always available. UPDATE
    /// and DELETE set this bit; <see cref="EnumerateRowsWithSlots"/> skips
    /// tombstoned slots. Row payload bytes are not reclaimed — slot directory
    /// space and row data area both grow monotonically (intentional simplifying
    /// trade-off; see CLAUDE.md).
    /// </summary>
    private const ushort SlotTombstoneBit = 0x8000;

    /// <summary>The page's raw bytes (for tests and future page I/O).</summary>
    public readonly byte[] Bytes = new byte[PageSize];

    public HeapPage()
    {
        this.Bytes[0] = HeapDataPageType;
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(3, 2), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(this.Bytes.AsSpan(5, 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(this.Bytes.AsSpan(9, 4), -1);
    }

    public byte PageType => this.Bytes[0];

    public ushort SlotCount => BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(1, 2));

    public ushort FreeSpacePointer => BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(3, 2));

    public int NextPageIndex
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(this.Bytes.AsSpan(5, 4));
        set => BinaryPrimitives.WriteInt32LittleEndian(this.Bytes.AsSpan(5, 4), value);
    }

    public int PrevPageIndex
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(this.Bytes.AsSpan(9, 4));
        set => BinaryPrimitives.WriteInt32LittleEndian(this.Bytes.AsSpan(9, 4), value);
    }

    private int SlotDirectoryStart => PageSize - (2 * this.SlotCount);

    /// <summary>Free contiguous space available for a new row plus its 2-byte slot.</summary>
    public int FreeSpace => this.SlotDirectoryStart - this.FreeSpacePointer;

    /// <summary>
    /// Appends <paramref name="row"/> to the page if it fits. Returns false
    /// when the row plus its slot entry won't fit in the remaining free space;
    /// the page is unmodified in that case. Throws when the row is larger than
    /// any page can ever hold (<see cref="MaxRowPayload"/>) — that's a
    /// caller-side bug: the row encoder's overflow pass keeps rows under
    /// <see cref="Heap.MaxRowSize"/>.
    /// </summary>
    public bool TryInsert(ReadOnlySpan<byte> row)
    {
        if (row.Length > MaxRowPayload)
            throw new NotSupportedException($"Row of {row.Length} bytes exceeds the per-page maximum of {MaxRowPayload}; the row encoder should have pushed variable-length columns off-row.");

        if (row.Length + 2 > this.FreeSpace)
            return false;

        var freePtr = this.FreeSpacePointer;
        row.CopyTo(this.Bytes.AsSpan(freePtr, row.Length));

        var newSlotIndex = this.SlotCount;
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (newSlotIndex + 1)), 2), freePtr);
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(1, 2), (ushort)(newSlotIndex + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(3, 2), (ushort)(freePtr + row.Length));
        return true;
    }

    /// <summary>
    /// Yields each live row's bytes in slot-directory order. Tombstoned slots
    /// (rows removed by DELETE or relocated by UPDATE) are skipped. Rows are
    /// copied out of the page; mutating the returned arrays does not affect
    /// the page.
    /// </summary>
    public IEnumerable<byte[]> EnumerateRows()
    {
        foreach (var (_, bytes) in this.EnumerateRowsWithSlots())
            yield return bytes;
    }

    /// <summary>
    /// Like <see cref="EnumerateRows"/> but yields the slot index alongside
    /// each row's bytes. Used by UPDATE and DELETE to address rows for
    /// in-place removal via <see cref="DeleteSlot"/>. Tombstoned slots are
    /// skipped — the caller never sees them.
    /// </summary>
    public IEnumerable<(int SlotIndex, byte[] Bytes)> EnumerateRowsWithSlots()
    {
        var count = this.SlotCount;
        for (var i = 0; i < count; i++)
        {
            if (this.IsSlotDeleted(i))
                continue;

            var rowStart = this.ReadSlotOffset(i);
            var rowEnd = i + 1 < count
                ? this.ReadSlotOffset(i + 1)
                : this.FreeSpacePointer;

            yield return (i, this.Bytes.AsSpan(rowStart, rowEnd - rowStart).ToArray());
        }
    }

    /// <summary>
    /// Marks the slot at <paramref name="slotIndex"/> as deleted (tombstone).
    /// The slot directory entry stays in place — slot count is unchanged —
    /// but the high bit is set so subsequent enumerations skip it. Row
    /// payload bytes within the page are not reclaimed.
    /// </summary>
    public void DeleteSlot(int slotIndex)
    {
        var slotByteOffset = PageSize - (2 * (slotIndex + 1));
        var slotValue = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(slotValue | SlotTombstoneBit));
    }

    /// <summary>
    /// Clears the tombstone bit on a slot — the inverse of
    /// <see cref="DeleteSlot"/>. Used by <see cref="UndoLog"/> when
    /// rolling back a delete (or the delete half of an UPDATE).
    /// </summary>
    public void UndeleteSlot(int slotIndex)
    {
        var slotByteOffset = PageSize - (2 * (slotIndex + 1));
        var slotValue = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(slotValue & ~SlotTombstoneBit));
    }

    /// <summary>
    /// Returns a fresh copy of the row bytes at <paramref name="slotIndex"/>,
    /// ignoring the tombstone bit (so callers reading a deleted slot get
    /// the still-resident payload — useful for the version store's
    /// post-DELETE history capture). Returns null when the slot is past
    /// <see cref="SlotCount"/>.
    /// </summary>
    public byte[]? ReadSlotBytes(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= this.SlotCount)
            return null;
        var rowStart = this.ReadSlotOffset(slotIndex);
        var rowEnd = slotIndex + 1 < this.SlotCount
            ? this.ReadSlotOffset(slotIndex + 1)
            : this.FreeSpacePointer;
        return this.Bytes.AsSpan(rowStart, rowEnd - rowStart).ToArray();
    }

    private bool IsSlotDeleted(int slotIndex) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (slotIndex + 1)), 2)) & SlotTombstoneBit) != 0;

    private ushort ReadSlotOffset(int slotIndex) =>
        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (slotIndex + 1)), 2)) & ~SlotTombstoneBit);

    internal string DebugDisplay() => $"HeapPage(type=0x{this.PageType:X2}, slots={this.SlotCount}, freePtr={this.FreeSpacePointer}, prev={this.PrevPageIndex}, next={this.NextPageIndex})";
}
