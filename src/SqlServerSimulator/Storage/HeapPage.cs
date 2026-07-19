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
    /// Offsets fit in 13 bits (max 8191) so the top three bits are always
    /// available; DELETE and a relocated-UPDATE's original slot both set this
    /// bit, <see cref="EnumerateRowsWithSlots"/> skips tombstoned slots. A
    /// tombstone whose DELETE has committed also gets <see cref="SlotReclaimableBit"/>,
    /// and <see cref="Compact"/> reclaims its row-data bytes for reuse; the slot
    /// directory still grows monotonically (a reclaimed slot keeps a zero-extent
    /// directory entry — see CLAUDE.md for the residual slot-directory growth).
    /// </summary>
    private const ushort SlotTombstoneBit = 0x8000;

    /// <summary>
    /// Second-highest bit flags the slot as a forwarding pointer. The 6 bytes
    /// at the slot's payload offset encode <c>(int32 pageIndex, int16 slotIndex)</c>
    /// — the live target. A forwarded slot's <em>visible</em> identity stays at
    /// the original (page, slot) so callers track stable row addresses across
    /// UPDATEs that don't fit in place. See <see cref="Heap.UpdateAt"/>.
    /// </summary>
    private const ushort SlotForwardBit = 0x4000;

    /// <summary>
    /// Bit 13 flags a tombstoned slot as <em>committed-dead and reclaimable</em>:
    /// its row's DELETE (or the supersede half of a forwarding UPDATE) has
    /// committed, so no rollback can resurrect it and no snapshot reads its live
    /// bytes (snapshot history is a version-store copy, not the live slot). Set
    /// at commit by the undo log; consumed by <see cref="Compact"/>, which packs
    /// such slots to zero extent and reclaims their bytes. An uncommitted
    /// tombstone (DELETE still in an open tx) lacks this bit, so compaction
    /// preserves its bytes for a possible un-delete. The slot directory entry
    /// itself is never removed (that would renumber slots), so it stays as a
    /// zero-extent tombstone — the residual slot-directory growth noted in
    /// CLAUDE.md.
    /// </summary>
    private const ushort SlotReclaimableBit = 0x2000;

    /// <summary>Low-13-bits mask: the actual byte offset within the page (always &lt; <see cref="PageSize"/>).</summary>
    private const ushort SlotOffsetMask = 0x1FFF;

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
    /// skipped; forwarded slots yield the forward pointer's 6-byte payload
    /// (callers that need the target row's bytes resolve through
    /// <see cref="ReadForwardTarget"/> against the owning heap).
    /// </summary>
    public IEnumerable<(int SlotIndex, byte[] Bytes)> EnumerateRowsWithSlots()
    {
        var count = this.SlotCount;
        for (var i = 0; i < count; i++)
        {
            if (this.IsSlotDeleted(i))
                continue;

            yield return (i, this.SlotPayload(i));
        }
    }

    /// <summary>
    /// Returns the byte extent for the slot at <paramref name="slotIndex"/>
    /// (the bytes between this slot's offset and the next live-or-tombstoned
    /// slot's offset, or <see cref="FreeSpacePointer"/> when this is the last
    /// slot). The extent is fixed at the row's original encoded length —
    /// in-place rewrites must fit inside it (<see cref="RewriteSlotInPlace"/>
    /// validates).
    /// </summary>
    public int SlotExtent(int slotIndex)
    {
        var rowStart = this.ReadSlotOffset(slotIndex);
        var rowEnd = slotIndex + 1 < this.SlotCount
            ? this.ReadSlotOffset(slotIndex + 1)
            : this.FreeSpacePointer;
        return rowEnd - rowStart;
    }

    /// <summary>Fresh copy of the slot's payload bytes — see <see cref="SlotExtent"/> for the extent rule.</summary>
    private byte[] SlotPayload(int slotIndex)
    {
        var rowStart = this.ReadSlotOffset(slotIndex);
        return this.Bytes.AsSpan(rowStart, this.SlotExtent(slotIndex)).ToArray();
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
    /// Marks a tombstoned slot as committed-dead and reclaimable (see
    /// <see cref="SlotReclaimableBit"/>). Called by the undo log when a DELETE
    /// (or a forwarding UPDATE's supersede) commits. Idempotent.
    /// </summary>
    public void MarkSlotReclaimable(int slotIndex)
    {
        var slotByteOffset = PageSize - (2 * (slotIndex + 1));
        var slotValue = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(slotValue | SlotReclaimableBit));
    }

    /// <summary>True iff the slot carries the committed-dead reclaimable flag.</summary>
    public bool IsSlotReclaimable(int slotIndex) =>
        slotIndex >= 0 && slotIndex < this.SlotCount
        && (BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (slotIndex + 1)), 2)) & SlotReclaimableBit) != 0;

    /// <summary>
    /// Total bytes occupied by reclaimable (committed-dead) slots — the space
    /// <see cref="Compact"/> would return to free space. The Insert no-fit path
    /// consults this to decide whether compacting a page is worth it.
    /// </summary>
    public int ReclaimableBytes
    {
        get
        {
            var count = this.SlotCount;
            var sum = 0;
            for (var i = 0; i < count; i++)
            {
                if (this.IsSlotReclaimable(i))
                    sum += this.SlotExtent(i);
            }
            return sum;
        }
    }

    /// <summary>
    /// True when no slot holds a reachable row — every slot is a committed-dead
    /// (reclaimable) entry, or the page has none at all. Live, forwarded,
    /// forward-target, and <em>uncommitted</em>-tombstoned slots are all
    /// non-reclaimable, so any of them makes the page not dead. A page that
    /// reads true can be dropped from the tail of <see cref="Heap.Pages"/>
    /// without losing reachable data (a non-tail page still can't, because that
    /// would renumber the stable indices later pages depend on).
    /// </summary>
    public bool IsFullyDead
    {
        get
        {
            var count = this.SlotCount;
            for (var i = 0; i < count; i++)
            {
                if (!this.IsSlotReclaimable(i))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Packs the page's live row data, reclaiming the bytes of committed-dead
    /// (reclaimable) slots. Slot indices are preserved — only byte offsets move
    /// — so every <c>(page, slot)</c> holder (cursors, version-chain Rids,
    /// forward pointers, the seek cache) stays valid; reclaimable slots collapse
    /// to zero-extent tombstones at their packed position. Live, forwarded, and
    /// <em>uncommitted</em>-tombstoned slots keep their bytes and extents (the
    /// last so a rolled-back DELETE can still be un-deleted). After this,
    /// <see cref="FreeSpace"/> grows by <see cref="ReclaimableBytes"/> (which
    /// then reads zero), and new rows append into the freed room with fresh
    /// (higher) slot indices.
    /// </summary>
    public void Compact()
    {
        var count = this.SlotCount;
        var writePos = HeaderSize;
        for (var i = 0; i < count; i++)
        {
            var slotByteOffset = PageSize - (2 * (i + 1));
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2));
            var flags = (ushort)(raw & ~SlotOffsetMask);
            if ((raw & SlotReclaimableBit) != 0)
            {
                // Committed-dead: keep the directory entry as a zero-extent
                // tombstone at the current write position, drop its bytes.
                BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(writePos | flags));
                continue;
            }
            // SlotExtent reads the original (not-yet-rewritten) offsets of this
            // slot and its successor, and FreeSpacePointer for the last slot —
            // all still pristine here since we set FreeSpacePointer last and
            // only rewrite already-processed slots.
            var extent = this.SlotExtent(i);
            var srcOffset = (ushort)(raw & SlotOffsetMask);
            if (srcOffset != writePos)
                this.Bytes.AsSpan(srcOffset, extent).CopyTo(this.Bytes.AsSpan(writePos, extent));
            BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(writePos | flags));
            writePos += extent;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(3, 2), (ushort)writePos);
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

    /// <summary>
    /// Returns true when the slot is past the slot directory's high-water
    /// mark or has been tombstoned. The version store's snapshot-aware
    /// readers consult this to decide whether to substitute a chain's
    /// historical version for a slot the live heap iteration skipped. A
    /// <em>forwarded</em> slot is NOT tombstoned — the row is alive at the
    /// forward target while its visible identity remains here.
    /// </summary>
    public bool IsSlotTombstoned(int slotIndex) =>
        slotIndex < 0 || slotIndex >= this.SlotCount || this.IsSlotDeleted(slotIndex);

    /// <summary>True iff this slot is a forwarding pointer (see <see cref="SlotForwardBit"/>).</summary>
    public bool IsSlotForwarded(int slotIndex) =>
        slotIndex >= 0 && slotIndex < this.SlotCount && this.ReadSlotRaw(slotIndex).Forwarded;

    /// <summary>
    /// Decodes the 6-byte forward-target reference stored at a forwarded slot's
    /// payload offset. Caller must have checked <see cref="IsSlotForwarded"/>.
    /// </summary>
    public (int PageIndex, int SlotIndex) ReadForwardTarget(int slotIndex)
    {
        var offset = this.ReadSlotOffset(slotIndex);
        var page = BinaryPrimitives.ReadInt32LittleEndian(this.Bytes.AsSpan(offset, 4));
        var slot = BinaryPrimitives.ReadInt16LittleEndian(this.Bytes.AsSpan(offset + 4, 2));
        return (page, slot);
    }

    /// <summary>
    /// Converts a live slot into a forwarded slot pointing at
    /// <paramref name="target"/>. Caller guarantees the slot's existing
    /// extent is at least 6 bytes (always true: the row encoder emits no
    /// payload shorter than that). Sets <see cref="SlotForwardBit"/> and
    /// overwrites the slot's first 6 payload bytes with the target reference;
    /// any trailing bytes inside the extent are left as dead space.
    /// </summary>
    public void InstallForward(int slotIndex, (int PageIndex, int SlotIndex) target)
    {
        var (offset, _, _) = this.ReadSlotRaw(slotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(this.Bytes.AsSpan(offset, 4), target.PageIndex);
        BinaryPrimitives.WriteInt16LittleEndian(this.Bytes.AsSpan(offset + 4, 2), (short)target.SlotIndex);
        var slotByteOffset = PageSize - (2 * (slotIndex + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(offset | SlotForwardBit));
    }

    /// <summary>
    /// Updates an already-forwarded slot's target. The 6-byte forward payload
    /// is rewritten in place — used when a doubly-relocating UPDATE re-points
    /// the original slot at a fresh target (single-level forwarding, matching
    /// SQL Server's heap behavior).
    /// </summary>
    public void RewriteForward(int slotIndex, (int PageIndex, int SlotIndex) target)
    {
        var offset = this.ReadSlotOffset(slotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(this.Bytes.AsSpan(offset, 4), target.PageIndex);
        BinaryPrimitives.WriteInt16LittleEndian(this.Bytes.AsSpan(offset + 4, 2), (short)target.SlotIndex);
    }

    /// <summary>
    /// Clears the forward bit, restoring a non-forwarded live slot. Used by
    /// the undo log when rolling back a forwarding UPDATE — paired with a
    /// payload restore via <see cref="RewriteSlotInPlace"/>.
    /// </summary>
    public void ClearForward(int slotIndex)
    {
        var slotByteOffset = PageSize - (2 * (slotIndex + 1));
        var slotValue = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Bytes.AsSpan(slotByteOffset, 2), (ushort)(slotValue & ~SlotForwardBit));
    }

    /// <summary>
    /// Overwrites the slot's payload bytes in place. The new payload must fit
    /// inside the slot's existing extent (validated); shorter rewrites
    /// zero-pad the trailing bytes so the page's byte image stays
    /// deterministic. Used by <see cref="Heap.UpdateAt"/> for the fits-in-place
    /// fast path and by <see cref="UndoLog"/> to restore an updated slot's
    /// pre-update bytes.
    /// </summary>
    public void RewriteSlotInPlace(int slotIndex, ReadOnlySpan<byte> newPayload)
    {
        var extent = this.SlotExtent(slotIndex);
        if (newPayload.Length > extent)
            throw new InvalidOperationException($"In-place rewrite payload of {newPayload.Length} bytes does not fit slot extent {extent}; caller should have forwarded instead.");
        var offset = this.ReadSlotOffset(slotIndex);
        newPayload.CopyTo(this.Bytes.AsSpan(offset, newPayload.Length));
        if (newPayload.Length < extent)
            this.Bytes.AsSpan(offset + newPayload.Length, extent - newPayload.Length).Clear();
    }

    private bool IsSlotDeleted(int slotIndex) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (slotIndex + 1)), 2)) & SlotTombstoneBit) != 0;

    private ushort ReadSlotOffset(int slotIndex) =>
        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (slotIndex + 1)), 2)) & SlotOffsetMask);

    private (ushort Offset, bool Tombstoned, bool Forwarded) ReadSlotRaw(int slotIndex)
    {
        var raw = BinaryPrimitives.ReadUInt16LittleEndian(this.Bytes.AsSpan(PageSize - (2 * (slotIndex + 1)), 2));
        return ((ushort)(raw & SlotOffsetMask), (raw & SlotTombstoneBit) != 0, (raw & SlotForwardBit) != 0);
    }

    internal string DebugDisplay() => $"HeapPage(type=0x{this.PageType:X2}, slots={this.SlotCount}, freePtr={this.FreeSpacePointer}, prev={this.PrevPageIndex}, next={this.NextPageIndex})";
}
