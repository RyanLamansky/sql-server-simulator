using System.Buffers.Binary;

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
internal sealed class HeapPage
{
    public const int PageSize = 8192;

    public const int HeaderSize = 96;

    /// <summary>Largest row payload (in bytes) that can fit on an empty page (one row plus one 2-byte slot).</summary>
    public const int MaxRowPayload = PageSize - HeaderSize - 2;

    private const byte HeapDataPageType = 0x01;

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
    /// Yields each row's bytes in slot-directory order. Rows are copied out of
    /// the page; mutating the returned arrays does not affect the page.
    /// </summary>
    public IEnumerable<byte[]> EnumerateRows()
    {
        var count = this.SlotCount;
        for (var i = 0; i < count; i++)
        {
            var rowStart = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (i + 1)), 2));
            var rowEnd = i + 1 < count
                ? BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (i + 2)), 2))
                : this.FreeSpacePointer;

            yield return this.Bytes.AsSpan(rowStart, rowEnd - rowStart).ToArray();
        }
    }

#if DEBUG
    public override string ToString() => $"HeapPage(type=0x{this.PageType:X2}, slots={this.SlotCount}, freePtr={this.FreeSpacePointer}, prev={this.PrevPageIndex}, next={this.NextPageIndex})";
#endif
}
