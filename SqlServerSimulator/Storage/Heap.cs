using System.Buffers;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Callback that consumes the bytes of a LOB chain materialized into a
/// caller-supplied scratch buffer. The <c>state</c> parameter lets callers
/// pass per-call context (e.g. the destination <see cref="SqlType"/>) into
/// a static lambda, avoiding closure allocations on the hot decode path.
/// The span is only valid for the duration of the call — implementations
/// must not store it.
/// </summary>
internal delegate T LobChainReader<TState, T>(ReadOnlySpan<byte> bytes, TState state);

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
    /// <summary>
    /// SQL Server's documented in-row record size limit. Rows whose encoded
    /// length exceeds this fail at insert time; real SQL Server would push
    /// variable-length columns to ROW_OVERFLOW pages, which the simulator
    /// doesn't model yet.
    /// </summary>
    /// <remarks>
    /// The page's physical capacity (<see cref="HeapPage.MaxRowPayload"/>) is
    /// slightly larger; the gap accounts for SQL Server's per-record overhead
    /// the simulator doesn't byte-for-byte reproduce.
    /// </remarks>
    public const int MaxRowSize = 8060;

    /// <summary>Pages in this heap, in allocation order. Index <c>i</c> is reachable via prev/next links.</summary>
    public readonly List<HeapPage> Pages = [];

    /// <summary>
    /// Appends a row's encoded bytes to the heap. The active (last) page is
    /// tried first; on no-fit, a new page is allocated and linked, and the row
    /// goes there. Throws if the row exceeds <see cref="MaxRowSize"/>
    /// (no overflow-page modeling yet).
    /// </summary>
    public void Insert(ReadOnlySpan<byte> row)
    {
        if (row.Length > MaxRowSize)
            throw new NotSupportedException($"Row of {row.Length} bytes exceeds SQL Server's per-row maximum of {MaxRowSize}; row-overflow pages aren't modeled yet.");

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

    /// <summary>
    /// LOB-chain pages. Each <c>varchar(MAX)</c>/<c>nvarchar(MAX)</c>/
    /// <c>varbinary(MAX)</c>/<c>text</c>/<c>ntext</c>/<c>image</c> value that
    /// the row encoder pushed off-row owns its own forward-linked sub-chain
    /// of pages here; pages from different chains are interleaved in
    /// allocation order (one chain doesn't reserve a contiguous run).
    /// </summary>
    public readonly List<HeapLobPage> LobPages = [];

    /// <summary>
    /// Splits <paramref name="data"/> into <see cref="HeapLobPage.MaxPayload"/>-sized
    /// chunks, allocates a page chain in <see cref="LobPages"/>, and returns
    /// the index of the chain's head page. Empty inputs allocate a single
    /// zero-payload page so the row's pointer is always valid; callers that
    /// want NULL semantics should not call this method at all.
    /// </summary>
    public int AllocateLobChain(ReadOnlySpan<byte> data)
    {
        var head = this.LobPages.Count;
        var remaining = data;
        while (true)
        {
            var chunkSize = Math.Min(HeapLobPage.MaxPayload, remaining.Length);
            var page = new HeapLobPage();
            page.WritePayload(remaining[..chunkSize]);
            this.LobPages.Add(page);
            remaining = remaining[chunkSize..];
            if (remaining.Length == 0)
                return head;
            // The just-added page's next pointer references the page we're
            // about to allocate.
            page.NextPageIndex = this.LobPages.Count;
        }
    }

    /// <summary>
    /// Walks the LOB chain starting at <paramref name="headIndex"/> into a
    /// scratch buffer (stack-allocated for small payloads, pooled for
    /// larger ones) and hands the bytes to <paramref name="reader"/>. The
    /// callback's return value is the method's result; the buffer is
    /// released as soon as the callback completes, so the span must not
    /// escape.
    /// </summary>
    public T ReadLobChain<TState, T>(int headIndex, int totalLength, TState state, LobChainReader<TState, T> reader)
    {
        if (totalLength <= LobScratchStackThreshold)
        {
            Span<byte> stack = stackalloc byte[totalLength];
            FillLobChain(stack, headIndex);
            return reader(stack, state);
        }

        var rented = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var slice = rented.AsSpan(0, totalLength);
            FillLobChain(slice, headIndex);
            return reader(slice, state);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Convenience overload that copies the chain into a fresh
    /// <see cref="byte"/>[] — used by storage-internals tests where a
    /// concrete array is the natural shape. Hot decode paths should use
    /// the callback overload to avoid the per-call allocation.
    /// </summary>
    public byte[] ReadLobChain(int headIndex, int totalLength) =>
        ReadLobChain(headIndex, totalLength, default(byte), static (span, _) => span.ToArray());

    /// <summary>
    /// Threshold below which <see cref="ReadLobChain{TState, T}"/>'s scratch
    /// buffer lives on the call stack. 256 bytes covers most "small"
    /// LOB-eligible values (short strings, default-mapped <c>nvarchar(MAX)</c>
    /// columns) without inflating the frame; values above the threshold flow
    /// through <see cref="ArrayPool{T}.Shared"/>. The same constant gates
    /// <see cref="RowEncoder"/>'s encode-side scratch buffer.
    /// </summary>
    internal const int LobScratchStackThreshold = 256;

    private void FillLobChain(Span<byte> destination, int headIndex)
    {
        var totalLength = destination.Length;
        var dest = destination;
        var current = headIndex;
        while (current >= 0 && dest.Length > 0)
        {
            var page = this.LobPages[current];
            var payload = page.Payload;
            if (payload.Length > dest.Length)
                throw new InvalidDataException($"LOB chain at head {headIndex} produced more bytes than the row's declared total length {totalLength}.");
            payload.CopyTo(dest);
            dest = dest[payload.Length..];
            current = page.NextPageIndex;
        }
        if (dest.Length != 0)
            throw new InvalidDataException($"LOB chain at head {headIndex} produced fewer bytes than the row's declared total length {totalLength} (short by {dest.Length}).");
    }
}
