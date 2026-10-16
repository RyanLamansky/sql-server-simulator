using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The pages the lock DMVs place a table's row and key locks on, built when a
/// view reads them: the base rowset's data pages as <see cref="RealPageLayout"/>
/// fills them and each nonclustered index's leaf pages filled the same way —
/// its entries in key order, each record as real's leaf row stores it
/// (<see cref="IndexRecordBytes"/>) — with the rows an open transaction deleted
/// or rewrote kept where they stood, as real's ghost records keep their place
/// on the page until the transaction ends.
/// </summary>
/// <remarks>
/// Real takes an intent lock on the page a row or key lock's row sits on
/// (probed 2026-10-16 against SQL Server 2025: <c>PAGE IS</c> beside each
/// <c>KEY S</c> a <c>REPEATABLE READ</c> read keeps, <c>PAGE IX</c> beside a
/// write's <c>KEY X</c> and <c>RID X</c>, on the index's own leaf page for an
/// index key); the simulator takes none while no session locks the table's
/// pages whole, and the view reports the intents from the locks themselves.
/// A page is numbered from <see cref="FirstPageId"/>: the base rowset's pages
/// first, then each nonclustered index's in index order, so no two of the
/// table's pages share a number.
/// </remarks>
internal sealed class LockPageMap(BatchContext batch, HeapTable table)
{
    /// <summary>The page id the first page of a table's base rowset is described by.</summary>
    public const int FirstPageId = 8;

    private readonly BatchContext batch = batch;

    private readonly HeapTable table = table;

    private Rowset? baseRowset;

    private Dictionary<int, Rowset>? indexes;

    /// <summary>The page id of the page the row at <paramref name="address"/> is on, live or deleted by an open transaction.</summary>
    public int PageOfRow((int PageIndex, int SlotIndex) address)
    {
        var rowset = this.Base();
        return rowset.FirstPage + (rowset.PageOfAddress.TryGetValue(address, out var page) ? page : rowset.LastPage);
    }

    /// <summary>
    /// The page id of the page holding <paramref name="group"/>'s key
    /// <paramref name="key"/>, its last page for the infinity anchor; -1 for an
    /// index with no page, as an empty table's is (probed 2026-10-16 against
    /// SQL Server 2025: a SERIALIZABLE read of an empty table holds its
    /// infinity <c>RangeS-S</c> with no page lock beside it).
    /// </summary>
    public int PageOfAnchor(KeyLockGroup group, SqlValueKey? key)
    {
        var rowset = this.RowsetOf(group);
        return rowset.PageCount == 0 ? -1 : rowset.FirstPage + (key is { } anchor ? rowset.PageOfKey(anchor) : rowset.LastPage);
    }

    /// <summary>The page id of the page holding <paramref name="group"/>'s entry for the row <paramref name="image"/>.</summary>
    public int PageOfImage(KeyLockGroup group, byte[] image)
    {
        var rowset = this.RowsetOf(group);
        return rowset.FirstPage + rowset.PageOfKey(SortKey(this.table, group.Ordinals, image));
    }

    /// <summary>
    /// The page id of the page before the one the row at
    /// <paramref name="address"/> begins, which real's insert of the row
    /// tested first and found full; null for a row beginning no page or the
    /// first (probed 2026-10-16 against SQL Server 2025: an insert past a full
    /// last page held <c>PAGE IX</c> on it beside the new page's).
    /// </summary>
    public int? PageInsertPassed((int PageIndex, int SlotIndex) address)
    {
        var rowset = this.Base();
        return rowset.PageOfAddress.TryGetValue(address, out var page) && page > 0 && rowset.Begins(address) ? rowset.FirstPage + page - 1 : null;
    }

    /// <summary>The page id of the page before the one <paramref name="group"/>'s entry for the row <paramref name="image"/> begins, as for a base row; null for an entry beginning no page or the first.</summary>
    public int? PageInsertPassed(KeyLockGroup group, byte[] image)
    {
        var rowset = this.RowsetOf(group);
        return rowset.PageBeganBy(SortKey(this.table, group.Ordinals, image)) is { } page && page > 0 ? rowset.FirstPage + page - 1 : null;
    }

    /// <summary>The page id of the page a seek's <paramref name="visit"/> names, null for none.</summary>
    public int? PageOfVisit(PageVisit visit)
    {
        var rowset = this.RowsetOf(visit.Group!);
        return rowset.PageCount != 0 && rowset.PageBeside(visit.Key, visit.Bound) is { } page ? rowset.FirstPage + page : null;
    }

    /// <summary>The page id of the base rowset's page <paramref name="ordinal"/>, as <see cref="RealPageLayout"/> counts it.</summary>
    public static int PageIdOf(int ordinal) => FirstPageId + ordinal;

    private Rowset RowsetOf(KeyLockGroup group)
    {
        if (group.IsRowGroup)
            return this.Base();
        var indexes = this.Indexes();
        return indexes.TryGetValue(group.IndexId, out var rowset) ? rowset : this.Base();
    }

    /// <summary>
    /// The layouts a view built of a table with no row deleted or rewritten in
    /// flight, kept until its heap or its indexes change, so a view read again
    /// and again — a session polling for a blocked one — lays the table out once.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HeapTable, Layouts> layouts = [];

    private sealed class Layouts(long generation, List<Index> indexes, int keyConstraints, bool versioned)
    {
        public readonly long Generation = generation;
        public readonly List<Index> IndexList = indexes;
        public readonly int KeyConstraints = keyConstraints;
        public readonly bool Versioned = versioned;
        public Rowset? Base;
        public Dictionary<int, Rowset>? Indexes;

        public bool Describes(HeapTable table) =>
            Volatile.Read(ref table.Heap.MutationGeneration) == this.Generation && ReferenceEquals(table.Indexes, this.IndexList)
                && table.KeyConstraints.Count == this.KeyConstraints && Versioning(table) == this.Versioned;
    }

    private static bool Versioning(HeapTable table) => table.OwningDatabase is { } database && (database.AllowSnapshotIsolation || database.ReadCommittedSnapshot);

    // The kept layouts of the table, or null when an in-flight delete or
    // rewrite leaves a ghost the layout must place.
    private Layouts? Kept()
    {
        if (!this.table.SupersededKeyImages.IsEmptyLockFree())
            return null;
        if (layouts.TryGetValue(this.table, out var kept) && kept.Describes(this.table))
            return kept;
        kept = new Layouts(Volatile.Read(ref this.table.Heap.MutationGeneration), this.table.Indexes, this.table.KeyConstraints.Count, Versioning(this.table));
        layouts.AddOrUpdate(this.table, kept);
        return kept;
    }

    private Rowset Base()
    {
        if (this.baseRowset is { } built)
            return built;
        var kept = this.Kept();
        built = kept?.Base ?? this.BuildBase();
        _ = kept?.Base ??= built;
        return this.baseRowset = built;
    }

    private Dictionary<int, Rowset> Indexes()
    {
        if (this.indexes is { } built)
            return built;
        if (this.Kept() is { Indexes: { } keptIndexes })
            return this.indexes = keptIndexes;
        var next = this.Base().FirstPage + Math.Max(this.Base().PageCount, 1);
        var owners = new List<(int IndexId, KeyLockGroup Group, Index? Index)>();
        foreach (var key in this.table.KeyConstraints)
        {
            if (!key.IsClustered && !key.IsDisabled && KeyLockGroup.For(this.table, key) is { } group)
                owners.Add((key.IndexId, group, null));
        }
        foreach (var index in this.table.Indexes)
        {
            if (!index.IsClustered && !index.IsDisabled && !index.IsColumnstore && KeyLockGroup.For(this.table, index) is { } group)
                owners.Add((index.IndexId, group, index));
        }
        owners.Sort(static (a, b) => a.IndexId.CompareTo(b.IndexId));
        var map = new Dictionary<int, Rowset>();
        foreach (var (indexId, group, index) in owners)
        {
            var rowset = this.BuildIndex(group, index, next);
            map[indexId] = rowset;
            next += Math.Max(rowset.PageCount, 1);
        }
        _ = this.Kept()?.Indexes ??= map;
        return this.indexes = map;
    }

    /// <summary>
    /// The rows of an open transaction's deletes and rewrites whose slot no
    /// longer holds them — real's ghosts — with the image each held.
    /// </summary>
    private List<((int PageIndex, int SlotIndex) Address, byte[] Image)> Ghosts()
    {
        var ghosts = new List<((int, int), byte[])>();
        if (this.table.SupersededKeyImages.IsEmptyLockFree())
            return ghosts;
        var heap = this.table.Heap;
        foreach (var (_, images) in this.table.SupersededKeyImages)
        {
            foreach (var (address, (image, _)) in images)
            {
                if (heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex))
                    ghosts.Add((address, image));
            }
        }
        return ghosts;
    }

    private Rowset BuildBase()
    {
        var table = this.table;
        var heap = table.Heap;
        var columns = table.StoredColumns;
        var clustered = ClusteredScan.Key(table);
        var ordinals = clustered?.Ordinals;
        var entries = new List<Entry>();
        foreach (var (pageIndex, slotIndex, bytes) in ClusteredScan.RowsWithAddress(table))
            entries.Add(new Entry((pageIndex, slotIndex), bytes, ordinals is null ? SqlValueKey.Empty : SortKey(table, ordinals, bytes)));
        var ghosts = this.Ghosts();
        if (ghosts.Count != 0)
        {
            // A unique key deleted and inserted again is one record on real,
            // whose insert reuses the ghost.
            HashSet<SqlValueKey>? live = null;
            if (clustered is { Unique: true })
            {
                live = [];
                foreach (var entry in entries)
                    _ = live.Add(entry.Key);
            }
            foreach (var (address, image) in ghosts)
            {
                var ghostKey = ordinals is null ? SqlValueKey.Empty : SortKey(table, ordinals, image);
                if (live is null || !live.Contains(ghostKey))
                    entries.Add(new Entry(address, image, ghostKey));
            }
            if (clustered is { } key)
            {
                var comparer = new TupleComparer(key.Descending);
                Sort(entries, (x, y) => comparer.Compare(x.Key, y.Key));
            }
            else
            {
                Sort(entries, static (x, y) => x.Address.CompareTo(y.Address));
            }
        }

        var versioned = Versioning(table);
        var uniquifies = clustered is { Unique: false };
        var rowset = new Rowset(FirstPageId, clustered?.Descending);
        SqlValueKey? previous = null;
        foreach (var entry in entries)
        {
            var values = RowDecoder.DecodeRow(columns, entry.Image, heap);
            var duplicate = uniquifies && previous is { } before && before.Equals(entry.Key);
            previous = entry.Key;
            rowset.Add(entry, RealPageLayout.RecordBytes(columns, values, versioned, duplicate) + 2, keyed: ordinals is not null);
        }
        return rowset;
    }

    private Rowset BuildIndex(KeyLockGroup group, Index? index, int firstPage)
    {
        var table = this.table;
        var heap = table.Heap;
        var columns = table.StoredColumns;
        var leaf = LeafColumns(table, group, index);
        var heapLocator = !table.HasClusteredIndex();
        var entries = new List<Entry>();
        foreach (var (pageIndex, slotIndex, bytes) in heap.EnumerateRowsWithAddress())
        {
            if (this.Admits(index, bytes))
                entries.Add(new Entry((pageIndex, slotIndex), bytes, SortKey(table, group.Ordinals, bytes)));
        }
        var ghosts = this.Ghosts();
        if (ghosts.Count != 0)
        {
            var live = new HashSet<SqlValueKey>();
            foreach (var entry in entries)
                _ = live.Add(entry.Key);
            foreach (var (address, image) in ghosts)
            {
                var ghostKey = SortKey(table, group.Ordinals, image);
                if (!live.Contains(ghostKey) && this.Admits(index, image))
                    entries.Add(new Entry(address, image, ghostKey));
            }
        }
        var directions = new bool[group.Ordinals.Length];
        if (index is not null)
        {
            for (var i = 0; i < index.KeyColumns.Length && i < directions.Length; i++)
                directions[i] = index.KeyColumns[i].IsDescending;
        }
        var comparer = new TupleComparer(directions);
        Sort(entries, (x, y) => comparer.Compare(x.Key, y.Key) is var c && c != 0 ? c : x.Address.CompareTo(y.Address));
        var rowset = new Rowset(firstPage, directions);
        var leafValues = new SqlValue[leaf.Length];
        foreach (var entry in entries)
        {
            for (var i = 0; i < leaf.Length; i++)
                leafValues[i] = RowDecoder.DecodeColumn(columns, entry.Image, leaf[i], heap);
            rowset.Add(entry, IndexRecordBytes(columns, leaf, leafValues, heapLocator) + 2, keyed: true);
        }
        return rowset;
    }

    private bool Admits(Index? index, byte[] image) =>
        index?.Filter is not { } filter || Simulation.EvaluateIndexFilter(filter, this.table, Simulation.DecodeFullRow(this.table, image), this.batch) == true;

    /// <summary>
    /// The stored columns a nonclustered index's leaf row carries: its key, the
    /// clustered key it doesn't already name — the row locator, a heap's being
    /// the row's eight-byte RID instead — and its <c>INCLUDE</c> columns.
    /// </summary>
    private static int[] LeafColumns(HeapTable table, KeyLockGroup group, Index? index)
    {
        var leaf = new List<int>(group.Ordinals);
        if (ClusteredScan.KeyOrdinals(table) is { } clustered)
        {
            foreach (var ordinal in clustered)
            {
                if (!leaf.Contains(ordinal))
                    leaf.Add(ordinal);
            }
        }
        if (index is not null)
        {
            foreach (var ordinal in index.IncludedColumns)
            {
                if ((uint)ordinal < (uint)table.StoredColumns.Length && !leaf.Contains(ordinal))
                    leaf.Add(ordinal);
            }
        }
        return [.. leaf];
    }

    /// <summary>
    /// The bytes real's nonclustered leaf record of <paramref name="leaf"/>
    /// holding <paramref name="values"/> takes
    /// (<see cref="RealRecordFormat.IndexLeafRecordBytes"/>).
    /// </summary>
    internal static int IndexRecordBytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> leaf, ReadOnlySpan<SqlValue> values, bool heapLocator)
    {
        var lengths = leaf.Length <= 64 ? stackalloc int[leaf.Length] : new int[leaf.Length];
        for (var i = 0; i < leaf.Length; i++)
        {
            var type = columns[leaf[i]].Type;
            lengths[i] = values[i].IsNull ? -1 : type == SqlType.Bit ? 0 : type.IsFixedLength ? type.FixedLength : type.GetVariableByteCount(values[i]);
        }
        return RealRecordFormat.IndexLeafRecordBytes(columns, leaf, lengths, heapLocator, uniquified: false);
    }

    // An image's components at ordinals, NULLs included, as an index orders them.
    private static SqlValueKey SortKey(HeapTable table, int[] ordinals, byte[] image)
    {
        var components = new SqlValue[ordinals.Length];
        for (var i = 0; i < components.Length; i++)
            components[i] = RowDecoder.DecodeColumn(table.StoredColumns, image, ordinals[i], table.Heap);
        return new SqlValueKey(components);
    }

    // A stable sort: entries already in order keep it, as the live rows do.
    private static void Sort(List<Entry> entries, Comparison<Entry> comparison)
    {
        var indexed = new (Entry Entry, int Index)[entries.Count];
        for (var i = 0; i < indexed.Length; i++)
            indexed[i] = (entries[i], i);
        Array.Sort(indexed, (x, y) => comparison(x.Entry, y.Entry) is var c && c != 0 ? c : x.Index.CompareTo(y.Index));
        for (var i = 0; i < indexed.Length; i++)
            entries[i] = indexed[i].Entry;
    }

    private readonly struct Entry((int PageIndex, int SlotIndex) address, byte[] image, SqlValueKey key)
    {
        public readonly (int PageIndex, int SlotIndex) Address = address;
        public readonly byte[] Image = image;
        public readonly SqlValueKey Key = key;
    }

    /// <summary>
    /// Orders key tuples as an index does — NULL first in an ascending column,
    /// last in a descending one — over the components both carry, so an anchor
    /// shorter than the entries it names sorts equal to each of them.
    /// </summary>
    private sealed class TupleComparer(bool[] descending)
    {
        private readonly bool[] descending = descending;

        public int Compare(SqlValueKey x, SqlValueKey y)
        {
            var n = Math.Min(x.ComponentCount, y.ComponentCount);
            for (var i = 0; i < n; i++)
            {
                var a = x.ComponentAt(i);
                var b = y.ComponentAt(i);
                var c = (a.IsNull, b.IsNull) switch
                {
                    (true, true) => 0,
                    (true, false) => -1,
                    (false, true) => 1,
                    _ => a.CompareTo(b),
                };
                if (c != 0)
                    return i < this.descending.Length && this.descending[i] ? -c : c;
            }
            return 0;
        }
    }

    /// <summary>One rowset's pages: its entries filling 8,096-byte pages in order.</summary>
    private sealed class Rowset(int firstPage, bool[]? descending)
    {
        private const int PageBytes = 8096;

        public readonly int FirstPage = firstPage;

        public readonly Dictionary<(int PageIndex, int SlotIndex), int> PageOfAddress = [];

        private readonly List<(SqlValueKey Key, int Page)> keys = [];

        private readonly Dictionary<SqlValueKey, int> exact = [];

        private readonly TupleComparer comparer = new(descending ?? []);

        private int page;

        private int used;

        public int PageCount;

        /// <summary>The last page, page 0 for an empty rowset.</summary>
        public int LastPage => Math.Max(this.PageCount - 1, 0);

        // The entries each page begins with.
        private readonly HashSet<(int PageIndex, int SlotIndex)> starts = [];

        private readonly Dictionary<SqlValueKey, int> startKeys = [];

        /// <summary>Whether the entry at <paramref name="address"/> begins its page.</summary>
        public bool Begins((int PageIndex, int SlotIndex) address) => this.starts.Contains(address);

        /// <summary>The page the entry carrying <paramref name="key"/> begins, null when it begins none.</summary>
        public int? PageBeganBy(SqlValueKey key) => this.startKeys.TryGetValue(key, out var began) ? began : null;

        public void Add(Entry entry, int cost, bool keyed)
        {
            if (this.used + cost > PageBytes && this.used > 0)
            {
                this.page++;
                this.used = 0;
            }
            if (this.used == 0)
            {
                _ = this.starts.Add(entry.Address);
                if (keyed)
                    _ = this.startKeys.TryAdd(entry.Key, this.page);
            }
            this.used += cost;
            this.PageCount = this.page + 1;
            this.PageOfAddress[entry.Address] = this.page;
            if (keyed)
            {
                this.keys.Add((entry.Key, this.page));
                _ = this.exact.TryAdd(entry.Key, this.page);
            }
        }

        /// <summary>The page of the entry <paramref name="bound"/> names beside <paramref name="key"/>, null for none.</summary>
        public int? PageBeside(SqlValueKey key, PageVisitBound bound)
        {
            var below = bound is PageVisitBound.AtOrBelow or PageVisitBound.Below;
            int? found = below ? 0 : null;
            foreach (var (entryKey, entryPage) in this.keys)
            {
                var c = this.comparer.Compare(entryKey, key);
                switch (bound)
                {
                    case PageVisitBound.AtOrBelow when c <= 0:
                    case PageVisitBound.Below when c < 0:
                        found = entryPage;
                        continue;
                    case PageVisitBound.Above when c > 0:
                    case PageVisitBound.AtOrAbove when c >= 0:
                        return entryPage;
                }
                if (below)
                    break;
            }
            return found;
        }

        /// <summary>
        /// The page holding <paramref name="key"/>, or for a key no entry
        /// carries the page a seek for it lands on: the last entry below it,
        /// the first page when none is.
        /// </summary>
        public int PageOfKey(SqlValueKey key)
        {
            if (this.exact.TryGetValue(key, out var found))
                return found;
            var landed = 0;
            foreach (var (entryKey, entryPage) in this.keys)
            {
                var c = this.comparer.Compare(entryKey, key);
                if (c > 0)
                    break;
                landed = entryPage;
                if (c == 0)
                    return entryPage;
            }
            return landed;
        }
    }
}
