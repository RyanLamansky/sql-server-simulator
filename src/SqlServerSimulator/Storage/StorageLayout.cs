using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The storage figures real reports for a table's rowsets — the pages each
/// index level takes, their records, fill and fragmentation, and the LOB and
/// row-overflow pages — computed from the rows when a view reads them and kept
/// until the table changes. Each rowset's B-tree is replayed as real grows it:
/// the rows an index was built over (<see cref="HeapTable.LayoutBuilds"/>)
/// packed in key order, then every later row inserted in the order the heap
/// holds them, a statement's rows sorted by the index's key as real's plan
/// sorts them (<see cref="HeapTable.AddInsertGroup"/>), into pages that split as
/// real's do. Records are sized by <see cref="RealRecordFormat"/>.
/// </summary>
/// <remarks>
/// <para>
/// Real's page splits were probed 2026-10-16 against SQL Server 2025 with
/// <c>%%physloc%%</c> over single splits of 5- and 38-row pages and through
/// <c>sys.dm_db_index_physical_stats</c> over ascending, descending,
/// interleaved, strided and MD5-random insert orders of up to 20,000 rows,
/// each reproducible run to run: a leaf page whose last insert went to its end
/// takes a row past its end on a new page alone; one whose last insert went to
/// its front takes a row at its front on a new page alone ahead of it; a row
/// just past the last insert splits the page there, the row ending its left
/// half; a row just ahead of it splits it one row earlier, the row starting
/// the right; anything else splits at half the page's bytes, the rows whose
/// running total stays within 4,048 bytes staying, the new row with them when
/// it lands on that boundary. A non-leaf page always splits at half. Every
/// page count and fill those orders gave is reproduced exactly but one, off by
/// one page in 67.
/// </para>
/// <para>
/// An index build packs every level full — to its fill factor at the leaf,
/// and above it too under <c>PAD_INDEX</c> — as a serial build does (real's
/// parallel build leaves partial pages where its threads meet). Pages are
/// numbered within each level in the order they were allocated, the first
/// root taking the leaf numbering's second number as real's does, which is
/// what the fragmentation figures read; real's allocation interleaves the
/// table's other rowsets and other tables' too, so its fragment counts run
/// higher than these.
/// </para>
/// <para>
/// A deleted row is replayed as though never inserted, where real's page
/// keeps the space until it empties; a row an open transaction deleted is
/// kept in its place as real's ghost is, and leaves once the transaction ends
/// where real's ghost cleanup takes some seconds; an update changes a row in
/// place, where real moves a row whose key changed.
/// </para>
/// </remarks>
internal sealed class StorageLayout
{
    private static readonly ConditionalWeakTable<HeapTable, StorageLayout> kept = [];

    private readonly long generation;

    private readonly List<Index> indexes;

    private readonly int keyConstraints;

    private readonly int layoutVersion;

    private readonly bool versioned;

    private readonly ConcurrentDictionary<int, (PartitionPlacement? Placement, SqlValue[]? Boundaries, RowsetFigures[] Figures)> rowsets = new();

    private StorageLayout(HeapTable table)
    {
        this.generation = Volatile.Read(ref table.Heap.MutationGeneration);
        this.indexes = table.Indexes;
        this.keyConstraints = table.KeyConstraints.Count;
        this.layoutVersion = Volatile.Read(ref table.LayoutVersion);
        this.versioned = Versioning(table);
    }

    private bool Describes(HeapTable table) =>
        Volatile.Read(ref table.Heap.MutationGeneration) == this.generation && ReferenceEquals(table.Indexes, this.indexes)
            && table.KeyConstraints.Count == this.keyConstraints && Volatile.Read(ref table.LayoutVersion) == this.layoutVersion
            && Versioning(table) == this.versioned;

    private static bool Versioning(HeapTable table) => table.OwningDatabase is { } database && (database.AllowSnapshotIsolation || database.ReadCommittedSnapshot);

    /// <summary>
    /// The figures of <paramref name="identity"/>'s rowset, one per partition
    /// of <paramref name="placement"/> — a single one for an index on a
    /// filegroup. A filtered index's filter runs in <paramref name="batch"/>;
    /// without one every row counts.
    /// </summary>
    public static RowsetFigures[] Of(BatchContext? batch, HeapTable table, IndexIdentity identity, PartitionPlacement? placement)
    {
        if (!table.SupersededKeyImages.IsEmptyLockFree() || (batch is null && identity.Index?.Filter is not null))
            return Compute(batch, table, identity, placement);
        if (!kept.TryGetValue(table, out var layout) || !layout.Describes(table))
        {
            layout = new StorageLayout(table);
            kept.AddOrUpdate(table, layout);
        }
        // A split or merge of the partition function moves the rows between
        // partitions without changing the table.
        var boundaries = placement?.Scheme.Function.Boundaries;
        if (layout.rowsets.TryGetValue(identity.IndexId, out var found) && ReferenceEquals(found.Placement, placement) && ReferenceEquals(found.Boundaries, boundaries))
            return found.Figures;
        var figures = Compute(batch, table, identity, placement);
        layout.rowsets[identity.IndexId] = (placement, boundaries, figures);
        return figures;
    }

    private static RowsetFigures[] Compute(BatchContext? batch, HeapTable table, IndexIdentity identity, PartitionPlacement? placement)
    {
        var fanout = placement?.Fanout ?? 1;
        var result = new RowsetFigures[fanout];
        if (table.IsMemoryOptimized || identity.Index is { IsColumnstore: true } || identity.Type == 7)
        {
            for (var i = 0; i < fanout; i++)
                result[i] = new RowsetFigures(isHeap: false);
            return result;
        }
        var replay = new Replay(batch, table, identity, placement);
        for (var i = 0; i < fanout; i++)
            result[i] = replay.Partition(i);
        return result;
    }

    /// <summary>One rowset's rows, split by partition, replayed into its pages.</summary>
    private sealed class Replay
    {
        private readonly BatchContext? batch;

        private readonly HeapTable table;

        private readonly HeapColumn[] columns;

        private readonly StorageOptions options;

        private readonly bool versioned;

        private readonly bool isHeap;

        private readonly bool clustered;

        /// <summary>The stored columns a record carries, as <see cref="RealRecordFormat"/> sizes them.</summary>
        private readonly int[] recordColumns;

        /// <summary>The stored columns ordering the rowset, the row locator included for a non-unique nonclustered index.</summary>
        private readonly int[] keyColumns;

        /// <summary>The stored columns a non-leaf record names its child by.</summary>
        private readonly int[] nodeColumns;

        private readonly bool[] descending;

        private readonly bool heapLocator;

        private readonly bool uniquifies;

        private readonly int fillFactor;

        private readonly bool padIndex;

        private readonly (int PageIndex, int SlotIndex) built;

        private readonly InsertGroup[] groups;

        private readonly Index? index;

        private readonly bool hasLobUnit;

        private readonly bool hasOverflowUnit;

        private readonly List<Source>[] partitions;

        public Replay(BatchContext? batch, HeapTable table, IndexIdentity identity, PartitionPlacement? placement)
        {
            this.batch = batch;
            this.table = table;
            this.columns = table.StoredColumns;
            this.options = new StorageOptions(table.LargeValueTypesOutOfRow, table.TextInRowLimit);
            this.versioned = Versioning(table);
            this.index = identity.Index;
            this.built = table.LayoutBuilds.TryGetValue(identity.IndexId, out var mark) ? mark : (0, 0);
            this.groups = table.InsertGroupsInOrder();
            var clusteredKey = ClusteredScan.Key(table);
            object? owner = identity.Constraint is { } constraint ? constraint : identity.Index;
            this.isHeap = identity.IsHeap;
            this.clustered = identity.Type == 1;
            this.fillFactor = identity.Constraint?.FillFactor ?? identity.Index?.FillFactor ?? 0;
            this.padIndex = identity.Constraint?.IsPadded ?? identity.Index?.IsPadded ?? false;
            var all = new int[this.columns.Length];
            for (var i = 0; i < all.Length; i++)
                all[i] = i;
            if (this.isHeap)
            {
                this.recordColumns = all;
                this.keyColumns = [];
                this.nodeColumns = [];
                this.descending = [];
            }
            else if (this.clustered)
            {
                this.recordColumns = all;
                this.keyColumns = clusteredKey?.Ordinals ?? [];
                this.nodeColumns = this.keyColumns;
                this.descending = clusteredKey?.Descending ?? [];
                this.uniquifies = clusteredKey is { Unique: false };
            }
            else
            {
                var group = owner is null ? null : KeyLockGroup.For(table, owner);
                this.keyColumns = group?.Ordinals ?? [];
                this.heapLocator = clusteredKey is null;
                this.uniquifies = clusteredKey is { Unique: false };
                var unique = group is { IsUnique: true };
                this.nodeColumns = unique ? this.keyColumns[..group!.KeyLength] : this.keyColumns;
                this.recordColumns = LeafColumns(table, this.keyColumns, identity.Index, clusteredKey?.Ordinals);
                this.descending = new bool[this.keyColumns.Length];
                if (identity.Index is { } nonclustered)
                {
                    for (var i = 0; i < nonclustered.KeyColumns.Length && i < this.descending.Length; i++)
                        this.descending[i] = nonclustered.KeyColumns[i].IsDescending;
                }
            }
            (this.hasLobUnit, this.hasOverflowUnit) = Units(this.columns, this.recordColumns, this.isHeap || this.clustered, this.heapLocator);

            var fanout = placement?.Fanout ?? 1;
            this.partitions = new List<Source>[fanout];
            for (var i = 0; i < fanout; i++)
                this.partitions[i] = [];
            var partitionOrdinal = placement?.StorageOrdinal(table) ?? -1;
            var heap = table.Heap;

            // The rows an open transaction deleted, which real keeps as ghosts.
            Dictionary<(int PageIndex, int SlotIndex), byte[]>? ghosts = null;
            if (!table.SupersededKeyImages.IsEmptyLockFree())
            {
                ghosts = [];
                foreach (var (_, images) in table.SupersededKeyImages)
                {
                    foreach (var (address, (image, _)) in images)
                        ghosts[address] = image;
                }
            }
            foreach (var (pageIndex, slotIndex, bytes, _) in heap.EnumerateSlots())
            {
                var kind = SourceKind.Live;
                var image = bytes;
                if (image is null)
                {
                    // A deleted slot: an open transaction's ghost, or a row
                    // deleted and committed whose bytes the page still holds,
                    // which real's page kept the place of until it was freed.
                    if (ghosts is not null && ghosts.TryGetValue((pageIndex, slotIndex), out var ghost))
                    {
                        if (this.isHeap)
                            continue;
                        (kind, image) = (SourceKind.Ghost, ghost);
                    }
                    else if (heap.Pages[pageIndex].IsSlotForwarded(slotIndex) || heap.Pages[pageIndex].ReadSlotBytes(slotIndex) is not { Length: > 0 } dead || !IsRowImage(this.columns, dead))
                    {
                        continue;
                    }
                    else
                    {
                        (kind, image) = (SourceKind.Deleted, dead);
                    }
                }
                if (!this.Admits(image))
                    continue;
                var partition = placement is null ? 0 : placement.PartitionOfRow(table, partitionOrdinal, image) - 1;
                var forwarded = kind == SourceKind.Live && this.isHeap && heap.Pages[pageIndex].IsSlotForwarded(slotIndex);
                this.partitions[partition].Add(new Source((pageIndex, slotIndex), image, kind, forwarded));
            }
        }

        private static bool IsRowImage(HeapColumn[] columns, byte[] image)
        {
            try
            {
                var lengths = columns.Length <= 256 ? stackalloc int[columns.Length] : new int[columns.Length];
                RowDecoder.ReadValueLengths(columns, image, lengths);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private bool Admits(byte[] image) =>
            this.index?.Filter is not { } filter || this.batch is null
                || Simulation.EvaluateIndexFilter(filter, this.table, Simulation.DecodeFullRow(this.table, image), this.batch) == true;

        /// <summary>
        /// The stored columns a nonclustered index's leaf row carries: its key
        /// and row locator, then its <c>INCLUDE</c> columns.
        /// </summary>
        private static int[] LeafColumns(HeapTable table, int[] keyColumns, Index? index, int[]? clusteredKey)
        {
            var leaf = new List<int>(keyColumns);
            if (clusteredKey is not null)
            {
                foreach (var ordinal in clusteredKey)
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
        /// Whether the rowset has a LOB allocation unit — a column of a LOB
        /// type — and a row-overflow one — a record whose values at their
        /// longest would pass 8,060 bytes (probed 2026-10-16 against SQL Server
        /// 2025: each listed in <c>sys.allocation_units</c> from the table's
        /// creation, empty or not).
        /// </summary>
        private static (bool Lob, bool Overflow) Units(HeapColumn[] columns, int[] recordColumns, bool data, bool heapLocator)
        {
            var lob = false;
            var longest = recordColumns.Length <= 256 ? stackalloc int[recordColumns.Length] : new int[recordColumns.Length];
            for (var i = 0; i < recordColumns.Length; i++)
            {
                var column = columns[recordColumns[i]];
                lob |= column.IsLob;
                var type = column.Type;
                longest[i] = type == SqlType.Bit ? 0
                    : type.IsFixedLength ? type.FixedLength
                    : type is TextSqlType or NTextSqlType or ImageSqlType ? 16
                    : BuiltInResources.GetSysColumnMetadata(column).MaxLength is var max and > 0 ? max
                    : RealRecordFormat.MaxInRowValueBytes;
            }
            var bytes = data
                ? RealRecordFormat.DataRecordBytes(DataColumns(columns, recordColumns), longest, versioned: false, uniquified: false)
                : RealRecordFormat.IndexLeafRecordBytes(columns, recordColumns, longest, heapLocator, uniquified: false);
            return (lob, bytes > RealRecordFormat.MaxRecordBytes);
        }

        // A data record's columns are the table's, in order.
        private static HeapColumn[] DataColumns(HeapColumn[] columns, int[] recordColumns) => recordColumns.Length == columns.Length ? columns : [.. recordColumns.Select(ordinal => columns[ordinal])];

        public RowsetFigures Partition(int partition)
        {
            var sources = this.partitions[partition];
            var spill = new RealRecordFormat.SpillPacker();
            var figures = this.isHeap ? this.Heap(sources, spill) : this.Tree(sources, spill);
            figures.Lob = spill.Lob;
            figures.Overflow = spill.Overflow;
            figures.HasLobUnit = this.hasLobUnit;
            figures.HasOverflowUnit = this.hasOverflowUnit;
            return figures;
        }

        /// <summary>
        /// A heap's pages: its rows in the order it holds them filling pages in
        /// turn, as real's insert into a heap does, a deleted row's space kept
        /// in its page and freed again, the page with it. A row an update
        /// widened grows in place while its page has room, the updates taken
        /// in the heap's order; otherwise it leaves a nine-byte stub in its
        /// place, freeing the rest, and its record, carrying a ten-byte back
        /// pointer as a variable-length column of its own, moves to the pages
        /// after the rest (probed 2026-10-16 against SQL Server 2025: 482 of
        /// 5,000 25-byte rows widened to 315 bytes forwarded, 5,482 records on
        /// 37 pages). The simulator's own heap forwards every widened row and
        /// keeps its first size, which is what the replay starts it from.
        /// A row inserted alone goes to the last page while real's free-space
        /// map still offers it — no more than 95% full — and the rows of an
        /// <c>INSERT … SELECT</c> fill each page to the brim (probed
        /// 2026-10-16 against SQL Server 2025: 3,000 rows of 11 to 114 bytes
        /// inserted one by one on 26 pages 94.7% full, 5,000 of 25 bytes in
        /// one statement on 17, 98.1%).
        /// </summary>
        private RowsetFigures Heap(List<Source> sources, RealRecordFormat.SpillPacker spill)
        {
            var figures = new RowsetFigures(isHeap: true);
            var level = new LevelFigures();
            var natural = new int[this.columns.Length];
            var inRow = new int[this.columns.Length];
            var shape = new RecordShape(indexLeaf: false, heapLocator: false, this.versioned, uniquified: false);
            var pages = new List<int>();
            var pageRecords = new List<int>();
            int Fill(int record, bool bulk = false)
            {
                var cost = record + 2;
                if (pages.Count == 0 || pages[^1] + cost > RealRecordFormat.PageBytes || (!bulk && pages[^1] > RealRecordFormat.PageBytes * 0.95))
                {
                    pages.Add(0);
                    pageRecords.Add(0);
                }
                pages[^1] += cost;
                pageRecords[^1]++;
                return pages.Count - 1;
            }
            var g = 0;
            List<(int Page, int Before, int After, int Moved)>? widened = null;
            foreach (var source in sources)
            {
                RowDecoder.ReadValueLengths(this.columns, source.Image, natural);
                var deleted = source.Kind == SourceKind.Deleted;
                var record = RealRecordFormat.Place(this.columns, this.recordColumns, natural, inRow, this.options, shape, deleted ? null : spill);
                while (g < this.groups.Length && this.groups[g].Last.CompareTo(source.Address) < 0)
                    g++;
                var bulk = source.Address.CompareTo(this.built) < 0
                    || (g < this.groups.Length && this.groups[g] is { Tuples: false } group && group.First.CompareTo(source.Address) <= 0);
                // A heap keeps the page a deleted row leaves, empty or not.
                if (deleted)
                {
                    var page = Fill(record, bulk);
                    pages[page] -= record + 2;
                    pageRecords[page]--;
                    continue;
                }
                figures.Rows++;
                if (source.Forwarded && source.Address.CompareTo(this.built) >= 0)
                {
                    var grown = source.Image.Length - this.table.Heap.Pages[source.Address.PageIndex].SlotExtent(source.Address.SlotIndex);
                    var before = Math.Max(9, record - Math.Max(0, grown));
                    (widened ??= []).Add((Fill(before, bulk), before, record, record + (HasVariableBlock(this.columns, inRow) ? 12 : 14)));
                    continue;
                }
                _ = Fill(record, bulk);
                level.Add(record);
            }
            if (widened is not null)
            {
                List<int>? moved = null;
                foreach (var (page, before, after, movedRecord) in widened)
                {
                    if (pages[page] + after - before <= RealRecordFormat.PageBytes)
                    {
                        pages[page] += after - before;
                        level.Add(after);
                        continue;
                    }
                    pages[page] -= before - 9;
                    level.Add(9);
                    level.Forwarded++;
                    (moved ??= []).Add(movedRecord);
                }
                foreach (var record in moved ?? [])
                {
                    _ = Fill(record);
                    level.Add(record);
                }
            }
            level.Pages = pages.Count;
            if (level.Pages > 0)
                level.Fragments = 1;
            level.PageList = new PageFigures[pages.Count];
            for (var i = 0; i < pages.Count; i++)
                level.PageList[i] = new PageFigures(i, pageRecords[i], 0, pages[i]);
            figures.Levels = level.Pages == 0 ? [] : [level];
            figures.InRowReserved = level.Pages == 0 ? 0 : (8 * Extents(level.Pages)) + 1;
            return figures;
        }

        private static bool HasVariableBlock(HeapColumn[] columns, int[] inRow)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (!columns[i].Type.IsFixedLength && columns[i].Type != SqlType.Bit && inRow[i] >= 0)
                    return true;
            }
            return false;
        }

        private RowsetFigures Tree(List<Source> sources, RealRecordFormat.SpillPacker spill)
        {
            var figures = new RowsetFigures(isHeap: false);
            if (this.keyColumns.Length == 0 && sources.Count != 0)
                return figures;

            // The rows in the order the replay takes them: those the build
            // covered in key order, then the rest as inserted, each multi-row
            // statement's rows in key order.
            var entries = new Entry[sources.Count];
            var builtCount = 0;
            for (var i = 0; i < sources.Count; i++)
            {
                var key = SortKey(sources[i].Image, this.keyColumns);
                entries[i] = new Entry(key, i);
                if (sources[i].Address.CompareTo(this.built) < 0)
                    builtCount++;
            }
            var comparer = new KeyComparer(this.descending);
            int ByKey(Entry x, Entry y) => comparer.Compare(x.Key, y.Key) is var c && c != 0 ? c : x.Order.CompareTo(y.Order);
            var sequence = new Entry[entries.Length];
            Array.Copy(entries, sequence, entries.Length);
            if (builtCount > 0)
                Array.Sort(sequence, 0, builtCount, Comparer<Entry>.Create(ByKey));
            var multiRowStart = false;
            var g = 0;
            for (var i = builtCount; i < sequence.Length;)
            {
                var address = sources[sequence[i].Order].Address;
                while (g < this.groups.Length && this.groups[g].Last.CompareTo(address) < 0)
                    g++;
                var run = 1;
                if (g < this.groups.Length && this.groups[g].First.CompareTo(address) <= 0)
                {
                    var last = this.groups[g].Last;
                    while (i + run < sequence.Length && sources[sequence[i + run].Order].Address.CompareTo(last) <= 0)
                        run++;
                    if (i == builtCount && this.groups[g] is { Tuples: false, Rows: >= 25 })
                        multiRowStart = true;
                    if (run > 1)
                        Array.Sort(sequence, i, run, Comparer<Entry>.Create(ByKey));
                }
                i += run;
            }

            // Each row's records, its uniquifier read from whether an earlier
            // row holds its clustered key.
            var natural = new int[this.columns.Length];
            var inRow = new int[this.recordColumns.Length];
            var recordNatural = new int[this.recordColumns.Length];
            var nodeLengths = new int[this.nodeColumns.Length];
            HashSet<SqlValueKey>? seen = this.uniquifies ? [] : null;
            var clusteredKey = this.uniquifies ? ClusteredScan.KeyOrdinals(this.table) : null;
            var duplicate = new bool[sources.Count];
            if (seen is not null)
            {
                for (var i = 0; i < sources.Count; i++)
                    duplicate[i] = !seen.Add(SortKey(sources[i].Image, clusteredKey!));
            }
            var tree = new TreeBuilder(ByKey);
            var builtEntries = new List<Entry>(builtCount);
            List<Entry>? deleted = null;
            for (var n = 0; n < sequence.Length; n++)
            {
                var at = sequence[n].Order;
                var source = sources[at];
                RowDecoder.ReadValueLengths(this.columns, source.Image, natural);
                for (var i = 0; i < this.recordColumns.Length; i++)
                    recordNatural[i] = natural[this.recordColumns[i]];
                var uniquified = duplicate[at];
                var shape = new RecordShape(indexLeaf: !this.clustered, this.heapLocator, this.versioned && this.clustered, uniquified);
                var record = RealRecordFormat.Place(this.columns, this.recordColumns, recordNatural, inRow, this.options, shape, source.Ghost ? null : spill);
                for (var i = 0; i < this.nodeColumns.Length; i++)
                    nodeLengths[i] = natural[this.nodeColumns[i]];
                // A unique nonclustered index's non-leaf record names its key alone.
                var locates = ReferenceEquals(this.nodeColumns, this.keyColumns);
                var node = RealRecordFormat.IndexNodeRecordBytes(this.columns, this.nodeColumns, nodeLengths, this.heapLocator && locates, uniquified && locates, omitVariable: false);
                var nodeFirst = RealRecordFormat.IndexNodeRecordBytes(this.columns, this.nodeColumns, nodeLengths, this.heapLocator && locates, uniquified && locates, omitVariable: true);
                var entry = new Entry(sequence[n].Key, at, record, node, nodeFirst, source.Ghost);
                if (source.Kind == SourceKind.Live)
                    figures.Rows++;
                else if (source.Kind == SourceKind.Deleted)
                    (deleted ??= []).Add(entry);
                if (n < builtCount)
                {
                    builtEntries.Add(entry);
                }
                else
                {
                    if (n == builtCount && builtEntries.Count != 0)
                        tree.Build(builtEntries, this.fillFactor, this.padIndex);
                    tree.Insert(entry);
                }
            }
            if (builtEntries.Count != 0 && builtEntries.Count == sequence.Length)
                tree.Build(builtEntries, this.fillFactor, this.padIndex);
            if (deleted is not null)
            {
                foreach (var entry in deleted)
                    tree.Delete(entry);
            }
            figures.Levels = tree.Levels();
            var leafPages = figures.Levels.Length == 0 ? 0 : figures.Levels[0].Pages;
            long nodePages = 0;
            for (var i = 1; i < figures.Levels.Length; i++)
                nodePages += figures.Levels[i].Pages;
            long extents;
            List<(long Extents, List<(PageRef Page, int Key)> Members)> regions = [];
            if (leafPages == 0)
            {
                extents = 0;
            }
            else if (builtCount > 0)
            {
                extents = Extents(leafPages) + Extents(nodePages);
                regions.Add((Extents(leafPages), Members(figures.Levels, leaves: true, first: null)));
                regions.Add((Extents(nodePages), Members(figures.Levels, leaves: false, first: null)));
            }
            else if (multiRowStart && this.clustered && !this.hasLobUnit)
            {
                extents = 1 + Extents(leafPages - 1) + (nodePages == 0 ? 0 : 1 + Extents(nodePages - 1));
                var leaves = Members(figures.Levels, leaves: true, first: null);
                var nodes = Members(figures.Levels, leaves: false, first: null);
                regions.Add((1, leaves.Count == 0 ? [] : [leaves[0]]));
                if (nodePages != 0)
                    regions.Add((1, [nodes[0]]));
                regions.Add((Extents(leafPages - 1), leaves.Count == 0 ? [] : leaves[1..]));
                if (nodePages != 0)
                    regions.Add((Extents(nodePages - 1), nodes[1..]));
            }
            else
            {
                extents = Extents(leafPages + nodePages);
                // The first root, made by the split of the index's only page,
                // took the leaf numbering's second number.
                var members = Members(figures.Levels, leaves: true, first: null);
                var lastLeaf = members.Count == 0 ? -1 : members.Max(member => member.Key);
                var nodes = Members(figures.Levels, leaves: false, first: tree.RootTookLeafNumber ? 1 : null);
                foreach (var (page, key) in nodes)
                    members.Add((page, key == -1 ? 1 : lastLeaf + 1 + key));
                regions.Add((extents, members));
            }
            figures.InRowReserved = extents == 0 ? 0 : (8 * extents) + 1;
            PlaceSlots(figures.Levels, regions);
            return figures;
        }

        /// <summary>
        /// The pages of the leaf (or of the levels above it) keyed by their
        /// allocation order: a leaf by its number, a non-leaf page by its rank
        /// over the levels in turn — the root <paramref name="first"/> names,
        /// the first page of level 1, keyed -1 and left out of the ranks.
        /// </summary>
        private static List<(PageRef Page, int Key)> Members(LevelFigures[] levels, bool leaves, int? first)
        {
            var members = new List<(PageRef Page, int Key)>();
            if (leaves)
            {
                if (levels.Length != 0)
                {
                    for (var p = 0; p < levels[0].PageList.Length; p++)
                        members.Add((new PageRef(0, p), levels[0].PageList[p].Slot));
                    members.Sort(static (x, y) => x.Key.CompareTo(y.Key));
                }
                return members;
            }
            var rank = 0;
            for (var l = 1; l < levels.Length; l++)
            {
                var ordered = new List<(PageRef Page, int Allocation)>();
                for (var p = 0; p < levels[l].PageList.Length; p++)
                    ordered.Add((new PageRef(l, p), levels[l].PageList[p].Slot));
                ordered.Sort(static (x, y) => x.Allocation.CompareTo(y.Allocation));
                foreach (var (page, allocation) in ordered)
                    members.Add(first is not null && l == 1 && allocation == 0 ? (page, -1) : (page, rank++));
            }
            members.Sort(static (x, y) => x.Key.CompareTo(y.Key));
            return members;
        }

        /// <summary>
        /// Gives each page its place among the rowset's reserved pages: the
        /// regions follow one another, each a whole number of extents, and a
        /// region's pages sit at their keys' distances from its first, which
        /// keeps a page freed since in its place, or one after another when
        /// those distances outrun the region.
        /// </summary>
        private static void PlaceSlots(LevelFigures[] levels, List<(long Extents, List<(PageRef Page, int Key)> Members)> regions)
        {
            long offset = 0;
            foreach (var (regionExtents, members) in regions)
            {
                var capacity = 8 * regionExtents;
                if (members.Count != 0)
                {
                    var low = members[0].Key;
                    var spread = members[^1].Key - low + 1 <= capacity;
                    for (var i = 0; i < members.Count; i++)
                    {
                        var (page, key) = members[i];
                        var level = levels[page.Level];
                        var figures = level.PageList[page.Index];
                        level.PageList[page.Index] = new PageFigures((int)(offset + (spread ? key - low : i)), figures.Records, figures.Ghosts, figures.Bytes);
                    }
                }
                offset += capacity;
            }
        }

        private SqlValueKey SortKey(byte[] image, int[] ordinals)
        {
            var components = new SqlValue[ordinals.Length];
            for (var i = 0; i < components.Length; i++)
                components[i] = RowDecoder.DecodeColumn(this.columns, image, ordinals[i], this.table.Heap);
            return new SqlValueKey(components);
        }
    }

    private static long Extents(long pages) => (pages + 7) / 8;

    /// <summary>A page of a replayed rowset: its level and its place in that level's key order.</summary>
    private readonly struct PageRef(int level, int index)
    {
        public readonly int Level = level;
        public readonly int Index = index;
    }

    private enum SourceKind : byte
    {
        Live,

        /// <summary>A row an open transaction deleted: real's ghost record, in its place until the transaction ends.</summary>
        Ghost,

        /// <summary>A row deleted since it was inserted: in the replay it is inserted in its turn, then deleted after the rest.</summary>
        Deleted,
    }

    private readonly struct Source((int PageIndex, int SlotIndex) address, byte[] image, SourceKind kind, bool forwarded)
    {
        public readonly (int PageIndex, int SlotIndex) Address = address;
        public readonly byte[] Image = image;
        public readonly SourceKind Kind = kind;
        public readonly bool Forwarded = forwarded;
        public bool Ghost => this.Kind == SourceKind.Ghost;
    }

    /// <summary>One record of a rowset: its key, its place in the heap's order, and the bytes it and a non-leaf record naming it take.</summary>
    private readonly struct Entry(SqlValueKey key, int order, int record = 0, int node = 0, int nodeFirst = 0, bool ghost = false)
    {
        public readonly SqlValueKey Key = key;
        public readonly int Order = order;
        public readonly int Record = record;
        public readonly int Node = node;
        public readonly int NodeFirst = nodeFirst;
        public readonly bool Ghost = ghost;
    }

    /// <summary>
    /// Orders key tuples as an index does — NULL first in an ascending column,
    /// last in a descending one.
    /// </summary>
    private sealed class KeyComparer(bool[] descending)
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

    /// <summary>A B-tree grown as real grows one (see <see cref="StorageLayout"/>).</summary>
    private sealed class TreeBuilder(Comparison<Entry> order)
    {
        private const int HalfPage = RealRecordFormat.PageBytes / 2;

        private readonly Comparison<Entry> order = order;

        private object? root;

        private int leafAllocations;

        private readonly List<int> nodeAllocations = [];

        private bool rooted;

        private sealed class Leaf
        {
            public readonly List<Entry> Rows = [];
            public int Used;
            public int Last = -1;
            public int Allocation;
            public Node? Parent;
        }

        private sealed class Node(int level)
        {
            public readonly int Level = level;
            public readonly List<Child> Children = [];
            public int Used;
            public int Allocation;
            public Node? Parent;
        }

        private readonly struct Child(object page, Entry separator, int cost)
        {
            public readonly object Page = page;
            public readonly Entry Separator = separator;
            public readonly int Cost = cost;
        }

        private int NextNodeAllocation(int level)
        {
            while (this.nodeAllocations.Count < level)
                this.nodeAllocations.Add(0);
            return this.nodeAllocations[level - 1]++;
        }

        private Leaf NewLeaf() => new() { Allocation = this.leafAllocations++ };

        /// <summary>
        /// A page splitting <paramref name="from"/> off: past the root real
        /// allocates first, when the split of the index's only page makes one.
        /// </summary>
        private Leaf NewSibling(Leaf from)
        {
            if (from.Parent is null && !this.rooted)
            {
                this.leafAllocations++;
                this.RootTookLeafNumber = true;
            }
            return this.NewLeaf();
        }

        /// <summary>Whether the first root took a number of the leaf's, as it does when the split of the index's only page makes it.</summary>
        public bool RootTookLeafNumber;

        /// <summary>Lays <paramref name="sorted"/> out as an index build does: every level packed in key order.</summary>
        public void Build(List<Entry> sorted, int fillFactor, bool padIndex)
        {
            var limit = fillFactor is > 0 and < 100 ? RealRecordFormat.PageBytes * fillFactor / 100.0 : RealRecordFormat.PageBytes;
            List<object> pages = [];
            Leaf? leaf = null;
            foreach (var entry in sorted)
            {
                var cost = entry.Record + 2;
                if (leaf is null || leaf.Used + cost > RealRecordFormat.PageBytes || leaf.Used >= limit)
                {
                    leaf = this.NewLeaf();
                    pages.Add(leaf);
                }
                leaf.Rows.Add(entry);
                leaf.Used += cost;
                leaf.Last = leaf.Rows.Count - 1;
            }
            var nodeLimit = padIndex ? limit : RealRecordFormat.PageBytes;
            var level = 1;
            while (pages.Count > 1)
            {
                List<object> parents = [];
                Node? node = null;
                foreach (var page in pages)
                {
                    var separator = First(page);
                    var cost = separator.Node + 2;
                    if (node is null || node.Used + cost > RealRecordFormat.PageBytes || node.Used >= nodeLimit)
                    {
                        node = new Node(level) { Allocation = this.NextNodeAllocation(level) };
                        parents.Add(node);
                    }
                    node.Children.Add(new Child(page, separator, cost));
                    node.Used += cost;
                    SetParent(page, node);
                }
                pages = parents;
                level++;
            }
            this.root = pages.Count == 0 ? null : pages[0];
            this.rooted = pages.Count != 0 && pages[0] is Node;
        }

        private static Entry First(object page) => page is Leaf leaf ? leaf.Rows[0] : ((Node)page).Children[0].Separator;

        private static void SetParent(object page, Node? parent)
        {
            if (page is Leaf leaf)
                leaf.Parent = parent;
            else
                ((Node)page).Parent = parent;
        }

        private static Node? ParentOf(object page) => page is Leaf leaf ? leaf.Parent : ((Node)page).Parent;

        private static int LevelOf(object page) => page is Node node ? node.Level : 0;

        public void Insert(Entry entry)
        {
            if (this.root is null)
            {
                var first = this.NewLeaf();
                first.Rows.Add(entry);
                first.Used = entry.Record + 2;
                first.Last = 0;
                this.root = first;
                return;
            }
            var page = this.root;
            while (page is Node node)
                page = node.Children[this.ChildFor(node, entry)].Page;
            var leaf = (Leaf)page;
            var pos = this.Position(leaf.Rows, entry);
            this.InsertIntoLeaf(leaf, pos, entry);
        }

        // The last child whose separator is at or below the entry, the first when
        // none is: a node's first child stands for every key below the second's,
        // as real's leftmost non-leaf record keeps no key, so its separator —
        // stale once rows below it arrive — is never compared.
        private int ChildFor(Node node, Entry entry)
        {
            int lo = 1, hi = node.Children.Count - 1, found = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (this.order(node.Children[mid].Separator, entry) <= 0)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return found;
        }

        /// <summary>
        /// Takes <paramref name="entry"/> out of its leaf, as real's ghost
        /// cleanup does once the delete commits: the page keeps its place and
        /// its other rows, a page left empty is freed and leaves its parent,
        /// and the last page of the index stays.
        /// </summary>
        public void Delete(Entry entry)
        {
            var page = this.root;
            while (page is Node node)
                page = node.Children[this.ChildFor(node, entry)].Page;
            if (page is not Leaf leaf)
                return;
            var pos = this.Position(leaf.Rows, entry);
            if (pos >= leaf.Rows.Count || leaf.Rows[pos].Order != entry.Order)
            {
                pos = leaf.Rows.FindIndex(row => row.Order == entry.Order);
                if (pos < 0)
                    return;
            }
            leaf.Used -= leaf.Rows[pos].Record + 2;
            leaf.Rows.RemoveAt(pos);
            if (leaf.Rows.Count == 0 && leaf.Parent is { } parent)
                RemoveChild(parent, leaf);
        }

        private static void RemoveChild(Node node, object child)
        {
            var at = node.Children.FindIndex(c => ReferenceEquals(c.Page, child));
            node.Used -= node.Children[at].Cost;
            node.Children.RemoveAt(at);
            if (node.Children.Count == 0 && node.Parent is { } parent)
                RemoveChild(parent, node);
        }

        private int Position(List<Entry> rows, Entry entry)
        {
            int lo = 0, hi = rows.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (this.order(rows[mid], entry) < 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        private void InsertIntoLeaf(Leaf leaf, int pos, Entry entry)
        {
            var cost = entry.Record + 2;
            var n = leaf.Rows.Count;
            if (leaf.Used + cost <= RealRecordFormat.PageBytes)
            {
                leaf.Rows.Insert(pos, entry);
                leaf.Used += cost;
                leaf.Last = pos;
                return;
            }
            if (pos == n && leaf.Last == n - 1)
            {
                var alone = this.NewSibling(leaf);
                alone.Rows.Add(entry);
                alone.Used = cost;
                alone.Last = 0;
                this.AddSibling(leaf, alone, after: true);
                return;
            }
            if (pos == 0 && leaf.Last == 0)
            {
                var alone = this.NewSibling(leaf);
                alone.Rows.Add(entry);
                alone.Used = cost;
                alone.Last = 0;
                this.AddSibling(leaf, alone, after: false);
                return;
            }
            int split;
            bool left;
            if (pos == leaf.Last && pos > 1)
                (split, left) = (pos - 1, false);
            else if (pos == leaf.Last + 1 && pos < n)
                (split, left) = (pos, true);
            else
                (split, left) = Midpoint(leaf.Rows, static row => row.Record + 2, pos, cost);
            var right = this.NewSibling(leaf);
            right.Rows.AddRange(leaf.Rows.GetRange(split, n - split));
            leaf.Rows.RemoveRange(split, n - split);
            if (left)
            {
                leaf.Rows.Insert(pos, entry);
                leaf.Last = pos;
                right.Last = -1;
            }
            else
            {
                right.Rows.Insert(pos - split, entry);
                right.Last = pos - split;
                leaf.Last = -1;
            }
            leaf.Used = Sum(leaf.Rows);
            right.Used = Sum(right.Rows);
            this.AddSibling(leaf, right, after: true);
        }

        private static int Sum(List<Entry> rows)
        {
            var sum = 0;
            foreach (var row in rows)
                sum += row.Record + 2;
            return sum;
        }

        /// <summary>
        /// Where real splits a full page taking a new item at
        /// <paramref name="pos"/>: the items whose running total, the new one
        /// among them, stays within half a page stay, and the new item stays
        /// with them when it lands on that boundary.
        /// </summary>
        private static (int Split, bool Left) Midpoint<T>(List<T> items, Func<T, int> cost, int pos, int newCost)
        {
            var total = 0;
            var half = 0;
            var count = items.Count + 1;
            for (var i = 0; i < count; i++)
            {
                var c = i == pos ? newCost : cost(items[i < pos ? i : i - 1]);
                if (total + c > HalfPage && half > 0)
                    break;
                total += c;
                half++;
            }
            half = Math.Min(half, count - 1);
            if (pos > half)
                return (half, false);
            var split = Math.Max(pos, half - 1);
            // A page left holding everything takes the new item on the next page instead.
            return split >= items.Count ? (items.Count, false) : (split, true);
        }

        private void AddSibling(object old, object sibling, bool after)
        {
            var separator = First(sibling);
            var parent = ParentOf(old);
            if (parent is null)
            {
                var level = LevelOf(old) + 1;
                this.rooted = true;
                var top = new Node(level) { Allocation = this.NextNodeAllocation(level) };
                var (first, second) = after ? (old, sibling) : (sibling, old);
                var leading = First(first);
                top.Children.Add(new Child(first, leading, leading.NodeFirst + 2));
                top.Children.Add(new Child(second, First(second), First(second).Node + 2));
                top.Used = top.Children[0].Cost + top.Children[1].Cost;
                SetParent(first, top);
                SetParent(second, top);
                this.root = top;
                return;
            }
            var at = parent.Children.FindIndex(child => ReferenceEquals(child.Page, old));
            if (after)
            {
                at++;
            }
            else
            {
                // A page taking a sibling ahead of it is named from then on by
                // the rows it holds.
                var named = First(old);
                parent.Used += named.Node + 2 - parent.Children[at].Cost;
                parent.Children[at] = new Child(old, named, named.Node + 2);
            }
            SetParent(sibling, parent);
            this.InsertIntoNode(parent, at, new Child(sibling, separator, separator.Node + 2));
        }

        private void InsertIntoNode(Node node, int pos, Child child)
        {
            if (node.Used + child.Cost <= RealRecordFormat.PageBytes)
            {
                node.Children.Insert(pos, child);
                node.Used += child.Cost;
                return;
            }
            var (split, left) = Midpoint(node.Children, static c => c.Cost, pos, child.Cost);
            var right = new Node(node.Level) { Allocation = this.NextNodeAllocation(node.Level) };
            right.Children.AddRange(node.Children.GetRange(split, node.Children.Count - split));
            node.Children.RemoveRange(split, node.Children.Count - split);
            if (left)
                node.Children.Insert(pos, child);
            else
                right.Children.Insert(pos - split, child);
            node.Used = 0;
            foreach (var c in node.Children)
                node.Used += c.Cost;
            foreach (var c in right.Children)
            {
                right.Used += c.Cost;
                SetParent(c.Page, right);
            }
            foreach (var c in node.Children)
                SetParent(c.Page, node);
            this.AddSibling(node, right, after: true);
        }

        /// <summary>Each level's figures, the leaf's first, read off the pages in key order.</summary>
        public LevelFigures[] Levels()
        {
            if (this.root is null)
                return [];
            var levels = new List<List<object>>();
            List<object> current = [this.root];
            while (current.Count != 0)
            {
                levels.Insert(0, current);
                List<object> below = [];
                foreach (var page in current)
                {
                    if (page is Node node)
                    {
                        foreach (var child in node.Children)
                            below.Add(child.Page);
                    }
                }
                current = below;
            }
            var result = new LevelFigures[levels.Count];
            for (var l = 0; l < levels.Count; l++)
            {
                var figures = new LevelFigures();
                var previous = -2;
                var pages = levels[l];
                figures.PageList = new PageFigures[pages.Count];
                for (var p = 0; p < pages.Count; p++)
                {
                    var page = pages[p];
                    figures.Pages++;
                    int allocation;
                    if (page is Leaf leaf)
                    {
                        allocation = leaf.Allocation;
                        var ghosts = 0;
                        foreach (var row in leaf.Rows)
                        {
                            if (row.Ghost)
                                ghosts++;
                            figures.Add(row.Record, counted: !row.Ghost);
                        }
                        figures.Ghosts += ghosts;
                        figures.PageList[p] = new PageFigures(allocation, leaf.Rows.Count - ghosts, ghosts, leaf.Used);
                    }
                    else
                    {
                        var node = (Node)page;
                        allocation = node.Allocation;
                        foreach (var child in node.Children)
                            figures.Add(child.Cost - 2);
                        figures.PageList[p] = new PageFigures(allocation, node.Children.Count, 0, node.Used);
                    }
                    if (allocation != previous + 1)
                        figures.Fragments++;
                    if (p > 0 && allocation != previous + 1)
                        figures.OutOfOrder++;
                    previous = allocation;
                }
                result[l] = figures;
            }
            return result;
        }
    }
}

/// <summary>A statement's range of inserted rows (see <see cref="HeapTable.AddInsertGroup"/>).</summary>
internal sealed class InsertGroup((int PageIndex, int SlotIndex) first, bool tuples)
{
    public readonly (int PageIndex, int SlotIndex) First = first;

    /// <summary>
    /// Whether the rows came from an <c>INSERT … VALUES</c>. An
    /// <c>INSERT … SELECT</c> of 25 rows or more into an empty clustered index
    /// without LOB columns starts the leaf and the root each in an extent of
    /// its own and leaves the rest of both unused (probed 2026-10-16 against
    /// SQL Server 2025: 25 rows on five pages reserving three extents, 20 or
    /// the same 25 rows of <c>VALUES</c> one) — though real's choice also
    /// follows the database's allocation history, which the figures don't
    /// keep.
    /// </summary>
    public readonly bool Tuples = tuples;

    /// <summary>The rows the statement inserted.</summary>
    public int Rows = 1;

    public (int PageIndex, int SlotIndex) Last = first;

    /// <summary>Two adjacent ranges as one, whose rows then sort together.</summary>
    public static InsertGroup Merge(InsertGroup earlier, InsertGroup later) =>
        new(earlier.First, earlier.Tuples && later.Tuples) { Last = later.Last, Rows = earlier.Rows + later.Rows };
}

/// <summary>One level of a rowset's pages as <c>sys.dm_db_index_physical_stats</c> describes it.</summary>
internal sealed class LevelFigures
{
    public long Pages;

    /// <summary>
    /// The level's pages in key order — a heap's in the order it filled
    /// them — each with its place among the rowset's reserved pages, which
    /// the page ids the page surfaces number are read from.
    /// </summary>
    public PageFigures[] PageList = [];

    /// <summary>The level's records, its ghosts left out.</summary>
    public long Records;

    public long Ghosts;

    public long Forwarded;

    /// <summary>The bytes the level's records and their slots take, ghosts included.</summary>
    public long Bytes;

    /// <summary>The bytes of the records the size figures read, ghosts included.</summary>
    public long RecordBytes;

    public long SizedRecords;

    public int MinRecord;

    public int MaxRecord;

    /// <summary>Pages whose successor in key order isn't the page allocated next.</summary>
    public long OutOfOrder;

    /// <summary>Runs of pages allocated one after another in key order.</summary>
    public long Fragments;

    public void Add(int record, bool counted = true)
    {
        if (counted)
            this.Records++;
        this.SizedRecords++;
        this.Bytes += record + 2;
        this.RecordBytes += record;
        this.MinRecord = this.SizedRecords == 1 ? record : Math.Min(this.MinRecord, record);
        this.MaxRecord = Math.Max(this.MaxRecord, record);
    }
}

/// <summary>
/// One page of a replayed rowset: its place among the rowset's reserved
/// pages, counted from the first page of its first extent, its records
/// (ghosts apart) and the bytes they and their slots take.
/// </summary>
internal readonly struct PageFigures(int slot, int records, int ghosts, int bytes)
{
    public readonly int Slot = slot;
    public readonly int Records = records;
    public readonly int Ghosts = ghosts;
    public readonly int Bytes = bytes;
}

/// <summary>One partition of one index's storage, as the storage views report it.</summary>
internal sealed class RowsetFigures(bool isHeap)
{
    public readonly bool IsHeap = isHeap;

    /// <summary>The rowset's live rows.</summary>
    public long Rows;

    /// <summary>The in-row levels, the leaf's (a heap's data pages) first; empty for no pages.</summary>
    public LevelFigures[] Levels = [];

    public long InRowReserved;

    public RealRecordFormat.SpillUnit Lob = new();

    public RealRecordFormat.SpillUnit Overflow = new();

    public bool HasLobUnit;

    public bool HasOverflowUnit;

    /// <summary>The in-row data (leaf) pages.</summary>
    public long InRowDataPages => this.Levels.Length == 0 ? 0 : this.Levels[0].Pages;

    /// <summary>Every in-row page and the allocation unit's IAM page.</summary>
    public long InRowUsedPages
    {
        get
        {
            long pages = 0;
            foreach (var level in this.Levels)
                pages += level.Pages;
            return pages == 0 ? 0 : pages + 1;
        }
    }

    public static long UsedPages(RealRecordFormat.SpillUnit unit) => unit.Pages == 0 ? 0 : unit.Pages + 1;

    /// <summary>
    /// How full <paramref name="pages"/> pages holding <paramref name="bytes"/>
    /// of records and slots are on average, as a percentage, the way real
    /// reckons it: each page's bytes less two over 8,094 (probed 2026-10-17
    /// against SQL Server 2025, where every <c>avg_page_space_used_in_percent</c>
    /// and <c>DBCC SHOWCONTIG</c> density read is a fraction over 8,094 — 18
    /// 15-byte records on one page read 304 / 8,094, 3.756%, and 8 11-byte
    /// root records 102 / 8,094).
    /// </summary>
    public static double PageSpaceUsedPercent(long bytes, long pages) =>
        pages == 0 ? 0 : Math.Max(0, 100.0 * (bytes - (2.0 * pages)) / (pages * (RealRecordFormat.PageBytes - 2.0)));

    public static long ReservedPages(RealRecordFormat.SpillUnit unit) => unit.Pages == 0 ? 0 : (8 * ((unit.Pages + 7) / 8)) + 1;

    public long UsedPageCount => this.InRowUsedPages + UsedPages(this.Lob) + UsedPages(this.Overflow);

    public long ReservedPageCount => this.InRowReserved + ReservedPages(this.Lob) + ReservedPages(this.Overflow);
}
