using System.Buffers.Binary;
using System.Diagnostics;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A single 8KB LOB chain page: holds a slice of an oversize variable-length
/// value (varchar(MAX) / nvarchar(MAX) / varbinary(MAX) / text / ntext /
/// image) that didn't fit inline in a row. Pages of one chain link forward
/// via <see cref="NextPageIndex"/>; the row that owns the chain stores the
/// head page's index plus the value's total length.
/// </summary>
/// <remarks>
/// <para>
/// Layout (offsets into <see cref="bytes"/>):
/// <list type="table">
/// <item><description>[0]      Page type (1 byte). <c>0x02</c> = LOB data page.</description></item>
/// <item><description>[1-2]    Payload length on this page (UInt16 LE).</description></item>
/// <item><description>[3-6]    Next page index in the chain (Int32 LE) — <c>-1</c> if last.</description></item>
/// <item><description>[7-95]   Reserved (zero) for future header fields.</description></item>
/// <item><description>[96 .. 96 + payloadLength) Payload bytes.</description></item>
/// </list>
/// </para>
/// <para>
/// The 96-byte header matches <see cref="HeapPage"/> for layout symmetry,
/// even though LOB pages don't need the slot directory. Real SQL Server uses
/// a tree (TEXT_TREE) for very large LOBs; the simulator simplifies to a
/// linked list — chain walks are O(N) in chain length but the page format
/// stays page-based and matches the <c>"page-format storage"</c> commitment
/// in CLAUDE.md.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebugDisplay(),nq}")]
internal sealed class HeapLobPage
{
    public const int PageSize = 8192;

    public const int HeaderSize = 96;

    /// <summary>Largest payload that can fit on a single LOB page (page minus header).</summary>
    public const int MaxPayload = PageSize - HeaderSize;

    private const byte LobDataPageType = 0x02;

    /// <summary>
    /// The page's header and payload, in an array no longer than the largest
    /// payload the page has held: a short <c>nvarchar(max)</c> value — most of
    /// what EF Core's default string mapping stores, every one of them off-row
    /// here — costs its own length rather than a whole page. A recycled page
    /// taking a longer payload moves to a longer array; a reader takes the
    /// array once per access, so one a writer replaces mid-read leaves the
    /// reader on the old array, as a payload rewritten in place leaves it on
    /// torn bytes, for its sequence check to discard either way.
    /// </summary>
    private byte[] bytes = new byte[HeaderSize];

    public HeapLobPage()
    {
        this.bytes[0] = LobDataPageType;
        BinaryPrimitives.WriteInt32LittleEndian(this.bytes.AsSpan(3, 4), -1);
    }

    public byte PageType => this.bytes[0];

    public ushort PayloadLength
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(this.bytes.AsSpan(1, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(this.bytes.AsSpan(1, 2), value);
    }

    public int NextPageIndex
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(this.bytes.AsSpan(3, 4));
        set => BinaryPrimitives.WriteInt32LittleEndian(this.bytes.AsSpan(3, 4), value);
    }

    public Span<byte> Payload
    {
        get
        {
            var page = this.bytes;
            return page.AsSpan(HeaderSize, BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(1, 2)));
        }
    }

    /// <summary>
    /// Writes <paramref name="data"/> into this page's payload area, sets
    /// <see cref="PayloadLength"/>, and clears <see cref="NextPageIndex"/>.
    /// Caller is responsible for chaining a successor page if the source is
    /// larger than <see cref="MaxPayload"/>.
    /// </summary>
    public void WritePayload(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxPayload)
            throw new ArgumentException($"Payload of {data.Length} bytes exceeds the per-page maximum of {MaxPayload}.", nameof(data));
        var page = this.bytes;
        if (page.Length < HeaderSize + data.Length)
        {
            var longer = new byte[HeaderSize + data.Length];
            page.AsSpan(0, HeaderSize).CopyTo(longer);
            this.bytes = page = longer;
        }
        data.CopyTo(page.AsSpan(HeaderSize));
        this.PayloadLength = (ushort)data.Length;
        this.NextPageIndex = -1;
    }

    internal string DebugDisplay() => $"HeapLobPage(type=0x{this.PageType:X2}, payloadLen={this.PayloadLength}, next={this.NextPageIndex})";
}
