using System.Runtime.CompilerServices;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The data pages real stores a table's rows on, as a table loaded in its
/// clustered key's order — a heap in its insertion order — fills them, which is
/// what real's page locks (<c>PAGLOCK</c>) name. Each page takes rows in that
/// order while their records and two-byte slots fit its 8,096 bytes, a full one
/// starting the next, as real's insert at the end of an index does (probed
/// 2026-10-09 against SQL Server 2025 with <c>%%physloc%%</c> over twenty row
/// shapes — fixed and variable columns, NULLs, bits, a composite key, a
/// <c>varchar(max)</c> value kept in row, a heap and a non-unique clustered
/// index — each table's pages holding the rows <see cref="RecordBytes"/> says).
/// </summary>
/// <remarks>
/// A table loaded out of key order, or changed since, has the pages its page
/// splits left, which follow its history rather than its contents; the layout
/// is then the one a rebuild in key order would leave. It is recomputed as the
/// heap changes, and asked for only while a page lock is held on the table.
/// </remarks>
internal sealed class RealPageLayout
{
    private static readonly ConditionalWeakTable<Heap, RealPageLayout> layouts = [];

    private readonly long generation;

    private readonly Dictionary<(int Page, int Slot), int> pageOf;

    /// <summary>The row each page begins with.</summary>
    private readonly HashSet<(int Page, int Slot)> pageStarts;

    /// <summary>How many pages the rows take.</summary>
    public readonly int PageCount;

    private RealPageLayout(long generation, Dictionary<(int Page, int Slot), int> pageOf, HashSet<(int Page, int Slot)> pageStarts, int pageCount)
    {
        this.generation = generation;
        this.pageOf = pageOf;
        this.pageStarts = pageStarts;
        this.PageCount = pageCount;
    }

    /// <summary>
    /// The page an insert of the row at <paramref name="address"/> lands on
    /// first: its key's predecessor's, which a row beginning a page only
    /// reaches past — real's insert at the end of a full page tests that page
    /// before it allocates the next.
    /// </summary>
    public int InsertPageOf((int Page, int Slot) address)
    {
        var page = this.PageOf(address);
        return page > 0 && this.pageStarts.Contains(address) ? page - 1 : page;
    }

    /// <summary>The page, counted from 0, the row at <paramref name="address"/> is on; the last page for an address no live row holds.</summary>
    public int PageOf((int Page, int Slot) address) =>
        this.pageOf.TryGetValue(address, out var page) ? page : Math.Max(this.PageCount - 1, 0);

    /// <summary>The rows on each page, by address, built when first asked for.</summary>
    private List<(int Page, int Slot)>[]? rowsByPage;

    /// <summary>The rows on <paramref name="page"/>, by address.</summary>
    public List<(int Page, int Slot)> RowsOn(int page)
    {
        var byPage = Volatile.Read(ref this.rowsByPage);
        if (byPage is null)
        {
            byPage = new List<(int, int)>[this.PageCount];
            for (var i = 0; i < byPage.Length; i++)
                byPage[i] = [];
            foreach (var (address, on) in this.pageOf)
                byPage[on].Add(address);
            Volatile.Write(ref this.rowsByPage, byPage);
        }
        return (uint)page < (uint)byPage.Length ? byPage[page] : [];
    }

    /// <summary><paramref name="table"/>'s layout as its rows stand, computed again once the heap has changed.</summary>
    public static RealPageLayout For(HeapTable table)
    {
        var heap = table.Heap;
        var generation = Volatile.Read(ref heap.MutationGeneration);
        if (layouts.TryGetValue(heap, out var cached) && cached.generation == generation)
            return cached;
        var versioned = table.OwningDatabase is { } database && (database.AllowSnapshotIsolation || database.ReadCommittedSnapshot);
        var columns = table.StoredColumns;
        var uniquifies = ClusteredScan.Key(table) is { Unique: false } nonUnique ? nonUnique.Ordinals : null;
        Dictionary<(int, int), int> pageOf = [];
        HashSet<(int, int)> pageStarts = [];
        var page = 0;
        var used = 0;
        SqlValueKey? previousKey = null;
        foreach (var (pageIndex, slotIndex, bytes) in ClusteredScan.RowsWithAddress(table))
        {
            var values = RowDecoder.DecodeRow(columns, bytes, heap);
            var duplicate = false;
            if (uniquifies is not null)
            {
                var components = new SqlValue[uniquifies.Length];
                for (var i = 0; i < components.Length; i++)
                    components[i] = values[uniquifies[i]];
                var key = new SqlValueKey(components);
                duplicate = previousKey is { } previous && previous.Equals(key);
                previousKey = key;
            }
            var cost = RecordBytes(columns, values, versioned, duplicate) + 2;
            if (used + cost > RealRecordFormat.PageBytes && used > 0)
            {
                page++;
                used = 0;
            }
            if (used == 0)
                _ = pageStarts.Add((pageIndex, slotIndex));
            used += cost;
            pageOf[(pageIndex, slotIndex)] = page;
        }
        var layout = new RealPageLayout(generation, pageOf, pageStarts, pageOf.Count == 0 ? 0 : page + 1);
        layouts.AddOrUpdate(heap, layout);
        return layout;
    }

    /// <summary>
    /// The bytes real's record of a row of <paramref name="columns"/> holding
    /// <paramref name="values"/> takes, its long values placed as real places
    /// them (<see cref="RealRecordFormat.Place"/>), with the uniquifier of a
    /// non-unique clustered key's repeated value and the versioning tag.
    /// </summary>
    internal static int RecordBytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<SqlValue> values, bool versioned, bool uniquified, StorageOptions options = default)
    {
        var natural = columns.Length <= 64 ? stackalloc int[columns.Length] : new int[columns.Length];
        var ordinals = columns.Length <= 64 ? stackalloc int[columns.Length] : new int[columns.Length];
        var inRow = columns.Length <= 64 ? stackalloc int[columns.Length] : new int[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            ordinals[i] = i;
            var type = columns[i].Type;
            natural[i] = values[i].IsNull ? -1 : type == SqlType.Bit ? 0 : type.IsFixedLength ? type.FixedLength : type.GetVariableByteCount(values[i]);
        }
        return RealRecordFormat.Place(columns, ordinals, natural, inRow, options, new RecordShape(indexLeaf: false, heapLocator: false, versioned, uniquified), spill: null);
    }
}
