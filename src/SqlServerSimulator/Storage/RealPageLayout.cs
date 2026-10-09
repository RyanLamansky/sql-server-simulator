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
    /// <summary>The bytes a data page holds records and slots in: 8,192 less its 96-byte header.</summary>
    private const int PageBytes = 8096;

    /// <summary>The row-versioning tag a row written while snapshot isolation or <c>READ_COMMITTED_SNAPSHOT</c> is on carries.</summary>
    private const int VersioningTagBytes = 14;

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
            if (used + cost > PageBytes && used > 0)
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
    /// <paramref name="values"/> takes: a four-byte header, the fixed-length
    /// columns — every eight bits sharing a byte — the column count and NULL
    /// bitmap, and, when a variable-length column holds a value, the count and
    /// end offset of each up to the last that does and their data, a LOB value
    /// kept in row while it fits and a 24-byte pointer past that, <c>text</c>,
    /// <c>ntext</c> and <c>image</c> a 16-byte one; then the uniquifier of a
    /// non-unique clustered key's repeated value and the versioning tag.
    /// </summary>
    internal static int RecordBytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<SqlValue> values, bool versioned, bool uniquified)
    {
        var fixedBytes = 0;
        var bits = 0;
        var variable = 0;
        var offsets = 0;
        var data = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            var type = columns[i].Type;
            if (type == SqlType.Bit)
            {
                bits++;
                continue;
            }
            if (type.IsFixedLength)
            {
                fixedBytes += type.FixedLength;
                continue;
            }
            variable++;
            if (values[i].IsNull)
                continue;
            offsets = variable;
            data += type is TextSqlType or NTextSqlType or ImageSqlType ? 16
                : type.GetVariableByteCount(values[i]) is var length && columns[i].IsLob && length > 8000 ? 24
                : length;
        }
        if (uniquified)
        {
            offsets++;
            data += 4;
        }
        var bytes = 4 + fixedBytes + ((bits + 7) / 8) + 2 + ((columns.Length + 7) / 8);
        if (offsets > 0)
            bytes += 2 + (2 * offsets) + data;
        return versioned ? bytes + VersioningTagBytes : bytes;
    }
}
