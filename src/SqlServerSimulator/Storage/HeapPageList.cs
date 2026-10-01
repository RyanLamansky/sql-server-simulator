using System.Collections;

namespace SqlServerSimulator.Storage;

/// <summary>
/// <see cref="Heap.Pages"/>: an append-mostly list a latch-free reader may
/// index while the heap's latch holder appends. <see cref="List{T}"/> can't
/// serve that reader — its <c>Add</c> raises the count before it stores the
/// item, so a reader between the two reads a null page — so this list stores
/// the page first and publishes the count after it, and a reader that read a
/// count finds every page below it.
/// </summary>
/// <remarks>
/// Mutations run under the heap's latch (or, for <see cref="Replace"/> and
/// <see cref="Clear"/>, under the schema-modification lock that excludes every
/// reader). <see cref="RemoveLast"/> leaves the dropped page in the backing
/// array, so a reader that read the count before the trim still finds the
/// (dead) page it indexes.
/// </remarks>
internal sealed class HeapPageList : IReadOnlyList<HeapPage>
{
    private HeapPage[] items = [];

    private int count;

    /// <summary>The published page count; every index below it holds a page.</summary>
    public int Count => Volatile.Read(ref this.count);

    /// <summary>The page at <paramref name="index"/>, which must be below a <see cref="Count"/> the caller read.</summary>
    public HeapPage this[int index] => Volatile.Read(ref this.items)[index];

    /// <summary>Appends <paramref name="page"/>, storing it before the count that publishes it.</summary>
    public void Add(HeapPage page)
    {
        var size = this.count;
        var array = this.items;
        if (size == array.Length)
        {
            var grown = new HeapPage[Math.Max(4, size * 2)];
            Array.Copy(array, grown, size);
            grown[size] = page;
            Volatile.Write(ref this.items, grown);
        }
        else
        {
            array[size] = page;
        }
        Volatile.Write(ref this.count, size + 1);
    }

    /// <summary>Drops the last page from the count.</summary>
    public void RemoveLast() => Volatile.Write(ref this.count, this.count - 1);

    /// <summary>Empties the list (<c>TRUNCATE</c>).</summary>
    public void Clear()
    {
        Volatile.Write(ref this.count, 0);
        this.items = [];
    }

    /// <summary>Replaces the contents with <paramref name="pages"/> (a <c>TRUNCATE</c> rolling back).</summary>
    public void Replace(List<HeapPage> pages)
    {
        Volatile.Write(ref this.count, 0);
        this.items = [.. pages];
        Volatile.Write(ref this.count, pages.Count);
    }

    public IEnumerator<HeapPage> GetEnumerator()
    {
        var size = this.Count;
        var array = Volatile.Read(ref this.items);
        for (var i = 0; i < size; i++)
            yield return array[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
