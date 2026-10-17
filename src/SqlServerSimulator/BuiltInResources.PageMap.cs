using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The page ids every page-level surface numbers a database's pages by: the
// buffer descriptors, sys.system_internals_allocation_units, DBCC IND,
// SHOWCONTIG and CHECKALLOC, and sys.dm_db_database_page_allocations.
internal static partial class BuiltInResources
{
    /// <summary>The object id real's allocation report gives the pages its allocation maps take.</summary>
    internal const int AllocationMapsObjectId = 99;

    /// <summary>
    /// The pages of <paramref name="database"/>'s data file 1, every allocation
    /// unit's laid out in extents of eight pages from page
    /// <see cref="FirstBufferedPageId"/>, as real lays out a database where
    /// mixed extents serve only first pages (probed 2026-10-17 against SQL
    /// Server 2025 with <c>DBCC IND</c>, <c>DBCC CHECKALLOC</c> and
    /// <c>sys.dm_db_database_page_allocations</c>): the allocation maps' pages
    /// first — the 7 used of an extent and a mixed page real reports under
    /// object 99 — then the system tables' (<see cref="MapSystemUnit"/>),
    /// then the user rowsets', each with its IAM page in a mixed extent and
    /// every other page in the extents its storage figures reserve
    /// (<see cref="StorageLayout"/>), at the places the replay gave them. The
    /// ids are the simulator's own; real's follow its allocation history.
    /// Computed when a surface reads it, from figures the layout keeps.
    /// </summary>
    internal static List<MappedUnit> MapPages(Parser.BatchContext? batch, Database database)
    {
        var units = new List<MappedUnit>();
        var allocator = new PageAllocator();

        var maps = new MappedUnit(AllocationMapsObjectId, 0, 1, 0, (long)AllocationMapsObjectId << 16, 0, totalPages: 9, usedPages: 7, dataPages: 0, mixed: true)
        {
            IamPage = allocator.TakeMixed(),
        };
        maps.Extents.Add(allocator.TakeExtent());
        units.Add(maps);

        foreach (var systemTable in SystemTableObjects(database))
        {
            foreach (var rowset in systemTable.Rowsets)
            {
                foreach (var unit in rowset.Units)
                {
                    var mapped = new MappedUnit(systemTable.ObjectId, rowset.IndexId, 1, rowset.PartitionId, unit.Id, unit.Type, unit.TotalPages, unit.UsedPages, unit.DataPages, mixed: !systemTable.IsInternal)
                    {
                        SystemTable = systemTable,
                    };
                    MapSystemUnit(mapped, allocator);
                    units.Add(mapped);
                }
            }
        }

        var census = new PartitionCensus();
        foreach (var (table, identity, placement) in EnumerateStorageIdentities(database, batch))
        {
            var figures = StorageLayout.Of(batch, table, identity, placement);
            foreach (var unit in census.Units(table, identity.IndexId, placement))
            {
                var rowset = figures[unit.Number - 1];
                var id = unit.PartitionId;
                var inRow = new MappedUnit(table.ObjectId, identity.IndexId, unit.Number, id, PartitionCensus.AllocationUnitId(id, 1), 1, rowset.InRowReserved, rowset.InRowUsedPages, rowset.InRowDataPages, mixed: false)
                {
                    Figures = rowset,
                };
                MapInRowUnit(inRow, rowset, allocator);
                units.Add(inRow);
                // Real creates a rowset's row-overflow unit ahead of its LOB unit.
                if (rowset.HasOverflowUnit)
                    units.Add(MapSpillUnit(table, identity, unit, 3, rowset.Overflow, rowset, allocator));
                if (rowset.HasLobUnit)
                    units.Add(MapSpillUnit(table, identity, unit, 2, rowset.Lob, rowset, allocator));
            }
        }
        foreach (var unit in units)
            unit.MixedExtents = allocator.MixedExtents;
        return units;
    }

    /// <summary>
    /// <c>sys.dm_db_database_page_allocations(database_id, object_id,
    /// index_id, partition_number, mode)</c>: for each allocation unit
    /// holding pages, its IAM page, then every page of its mixed and
    /// dedicated extents in page order, an extent's unallocated pages among
    /// them, as <see cref="MapPages"/> lays them out. A NULL object, index or
    /// partition reads them all, an unknown one nothing; a NULL or negative
    /// database is Msg 2561 state 110, an unknown one Msg 913, and a mode
    /// other than <c>LIMITED</c> or <c>DETAILED</c> (NULL reading as the
    /// first) Msg 2561 state 111. <c>LIMITED</c> leaves a page's type, level
    /// and neighbors NULL; a heap's or LOB unit's pages report their free
    /// space as the page free space map buckets it — 100 empty, 50, 20, 5
    /// and 0 full — an index's NULL (probed 2026-10-17 against SQL Server
    /// 2025). A LOB page's fill isn't kept, so it reads 0.
    /// </summary>
    internal static List<SqlValue[]> DatabasePageAllocations(Parser.BatchContext batch, int? databaseId, int? objectId, int? indexId, int? partitionNumber, string? mode)
    {
        // A caller without VIEW SERVER PERFORMANCE STATE is refused, db_owner
        // or not (probed 2026-10-17 against SQL Server 2025).
        DemandServerPerformanceState(batch, policyWording: false);
        if (databaseId is null or < 0)
            throw SimulatedSqlException.ParameterIncorrectForStatement(1, 110);
        var detailed = mode is not null && BuiltInToken.Equals(mode, "DETAILED");
        if (mode is not null && !detailed && !BuiltInToken.Equals(mode, "LIMITED"))
            throw SimulatedSqlException.ParameterIncorrectForStatement(5, 111);
        var simulation = batch.Connection.Simulation;
        Database? database = null;
        short id = 0;
        foreach (var (candidate, candidateId) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
        {
            if (databaseId == 0 ? ReferenceEquals(candidate, batch.CurrentDatabase) : candidateId == databaseId)
                (database, id) = (candidate, candidateId);
        }
        if (database is null)
            throw SimulatedSqlException.DatabaseIdNotActivated(databaseId.Value);

        var rows = new List<SqlValue[]>();
        var nullSmall = SqlValue.Null(SqlType.SmallInt);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullTiny = SqlValue.Null(SqlType.TinyInt);
        var nullText = SqlValue.Null(SqlType.NVarchar);
        var file = SqlValue.FromInt16(1);
        var yes = SqlValue.FromByte(1);
        var no = SqlValue.FromByte(0);
        var secondary = SqlValue.FromNVarchar("SECONDARY");
        var databaseValue = SqlValue.FromInt32(id);
        foreach (var unit in MapPages(batch, database))
        {
            if (unit.IamPage == 0 || unit.ObjectId == AllocationMapsObjectId
                || (objectId is { } wantedObject && wantedObject != unit.ObjectId)
                || (indexId is { } wantedIndex && wantedIndex != unit.IndexId)
                || (partitionNumber is { } wantedPartition && wantedPartition != unit.PartitionNumber))
            {
                continue;
            }
            // A heap's and a LOB or row-overflow unit's pages are tracked in
            // the page free space map; an index's aren't.
            var tracked = unit.Type != 1 || unit.IndexId == 0;
            var head = new SqlValue[]
            {
                databaseValue,
                SqlValue.FromInt32(unit.ObjectId),
                SqlValue.FromInt32(unit.IndexId),
                SqlValue.FromInt32(unit.PartitionNumber),
                SqlValue.FromInt64(unit.PartitionId),
                SqlValue.FromInt64(unit.AllocationUnitId),
                SqlValue.FromInt32(unit.Type),
                SqlValue.FromNVarchar(unit.Type switch { 2 => "LOB_DATA", 3 => "ROW_OVERFLOW_DATA", _ => "IN_ROW_DATA" }),
                nullInt,
                nullInt,
                secondary,
            };
            var iam = SqlValue.FromInt32(unit.IamPage);
            rows.Add([
                .. head, file, SqlValue.FromInt32(unit.IamPage & ~7), nullSmall, nullInt, file, iam, yes, yes, yes,
                tracked ? SqlValue.FromInt32(100) : nullInt,
                detailed ? SqlValue.FromInt32(MappedPage.IamPage) : nullInt, detailed ? SqlValue.FromNVarchar("IAM_PAGE") : nullText, detailed ? no : nullTiny,
                nullSmall, nullInt, nullSmall, nullInt, detailed ? no : nullTiny, no,
            ]);
            var pages = new Dictionary<int, MappedPage>(unit.Pages.Count);
            foreach (var page in unit.Pages)
                pages[page.Id] = page;
            var slots = new List<(int Page, bool Mixed)>();
            foreach (var page in unit.MixedPages)
                slots.Add((page, true));
            foreach (var extent in unit.Extents)
            {
                for (var i = 0; i < 8; i++)
                    slots.Add((extent + i, false));
            }
            slots.Sort(static (x, y) => x.Page.CompareTo(y.Page));
            foreach (var (slot, mixed) in slots)
            {
                var allocated = pages.TryGetValue(slot, out var page);
                var freeSpace = !tracked ? nullInt : SqlValue.FromInt32(!allocated ? 100 : FreeSpaceBucket(page.FreeBytes));
                var known = detailed && allocated;
                rows.Add([
                    .. head, file, SqlValue.FromInt32(slot & ~7), file, iam, file, SqlValue.FromInt32(slot), allocated ? yes : no, no, mixed ? yes : no,
                    freeSpace,
                    known ? SqlValue.FromInt32(page.PageType) : nullInt,
                    known ? SqlValue.FromNVarchar(page.PageType switch { MappedPage.DataPage => "DATA_PAGE", MappedPage.IndexPage => "INDEX_PAGE", _ => "TEXT_MIX_PAGE" }) : nullText,
                    known ? SqlValue.FromByte((byte)page.Level) : nullTiny,
                    known && page.Next != 0 ? file : nullSmall, known && page.Next != 0 ? SqlValue.FromInt32(page.Next) : nullInt,
                    known && page.Previous != 0 ? file : nullSmall, known && page.Previous != 0 ? SqlValue.FromInt32(page.Previous) : nullInt,
                    detailed ? no : nullTiny,
                    known && page.Ghosts > 0 ? yes : no,
                ]);
            }
        }
        return rows;
    }

    /// <summary>The page free space map's bucket for a page with <paramref name="freeBytes"/> free, as the percentage it reports: 0 for a full page.</summary>
    private static int FreeSpaceBucket(int freeBytes)
    {
        if (freeBytes < 0)
            return 0;
        var full = 100.0 * (RealRecordFormat.PageBytes - freeBytes) / RealRecordFormat.PageBytes;
        return full switch
        {
            <= 0 => 100,
            <= 50 => 50,
            <= 80 => 20,
            <= 95 => 5,
            _ => 0,
        };
    }

    /// <summary>
    /// A system table's unit: its IAM page in a mixed extent, and a system
    /// base table's first eight pages too, the rest in extents of its own
    /// (probed 2026-10-17 against SQL Server 2025: <c>sysschobjs</c>' 43
    /// pages, 9 of them mixed, in five dedicated extents, where an internal
    /// table's pages but its IAM page sit in dedicated extents as a user
    /// table's do). Its leaf pages come first, chained in page order, and the
    /// pages above them last, the root at the end.
    /// </summary>
    private static void MapSystemUnit(MappedUnit unit, PageAllocator allocator)
    {
        if (unit.UsedPages == 0)
            return;
        unit.IamPage = allocator.TakeMixed();
        var pages = (int)unit.UsedPages - 1;
        var mixedPages = unit.MixedAllocation ? Math.Min(pages, 8) : 0;
        var dedicated = Math.Max(0, (int)((unit.TotalPages - mixedPages - 1 + 7) / 8));
        var ids = new int[pages];
        for (var i = 0; i < mixedPages; i++)
        {
            ids[i] = allocator.TakeMixed();
            unit.MixedPages.Add(ids[i]);
        }
        for (var i = 0; i < dedicated; i++)
            unit.Extents.Add(allocator.TakeExtent());
        for (var i = mixedPages; i < pages; i++)
        {
            var slot = i - mixedPages;
            while (slot / 8 >= unit.Extents.Count)
                unit.Extents.Add(allocator.TakeExtent());
            ids[i] = unit.Extents[slot / 8] + (slot % 8);
        }
        if (unit.Type != 1)
        {
            foreach (var id in ids)
                unit.Pages.Add(new MappedPage(id, MappedPage.TextPage, 0, 0, 0, -1, -1, 0));
            unit.FirstPage = ids.Length == 0 ? 0 : ids[0];
            unit.RootPage = unit.FirstPage;
            return;
        }
        var leaves = unit.IndexId == 0 || pages <= 1 ? pages : unit.DataPages > 0 ? (int)Math.Min(unit.DataPages, pages) : pages - 1;
        var leafType = unit.IndexId <= 1 ? MappedPage.DataPage : MappedPage.IndexPage;
        var chained = unit.IndexId != 0;
        // Two pages or more above the leaf: the last is the root, a level
        // above the rest.
        var topped = pages - leaves > 1;
        for (var i = 0; i < pages; i++)
        {
            var leaf = i < leaves;
            var level = leaf ? 0 : topped && i == pages - 1 ? 2 : 1;
            int previous = 0, next = 0;
            if (chained && leaf)
                (previous, next) = (i > 0 ? ids[i - 1] : 0, i + 1 < leaves ? ids[i + 1] : 0);
            else if (chained && level == 1)
                (previous, next) = (i > leaves ? ids[i - 1] : 0, i + 1 < pages - (topped ? 1 : 0) ? ids[i + 1] : 0);
            unit.Pages.Add(new MappedPage(ids[i], leaf ? leafType : MappedPage.IndexPage, level, previous, next, -1, -1, 0));
        }
        unit.FirstPage = ids.Length == 0 ? 0 : ids[0];
        unit.RootPage = unit.IndexId == 0 || ids.Length == 0 ? 0 : ids[^1];
        unit.LevelPages = pages == leaves ? [ids] : topped ? [ids[..leaves], ids[leaves..^1], ids[^1..]] : [ids[..leaves], ids[leaves..]];
        unit.SortPages();
    }

    /// <summary>
    /// A user rowset's in-row unit: its IAM page in a mixed extent, then the
    /// extents its figures reserve, each page at the place the replay gave
    /// it, chained to its neighbors in key order on its level — a heap's
    /// pages unchained.
    /// </summary>
    private static void MapInRowUnit(MappedUnit unit, RowsetFigures figures, PageAllocator allocator)
    {
        if (figures.Levels.Length == 0)
            return;
        unit.IamPage = allocator.TakeMixed();
        var dedicated = (int)((figures.InRowReserved - 1) / 8);
        for (var i = 0; i < dedicated; i++)
            unit.Extents.Add(allocator.TakeExtent());
        unit.LevelPages = new int[figures.Levels.Length][];
        for (var l = 0; l < figures.Levels.Length; l++)
        {
            var list = figures.Levels[l].PageList;
            var ids = new int[list.Length];
            for (var p = 0; p < list.Length; p++)
            {
                var slot = list[p].Slot;
                while (slot / 8 >= unit.Extents.Count)
                    unit.Extents.Add(allocator.TakeExtent());
                ids[p] = unit.Extents[slot / 8] + (slot % 8);
            }
            unit.LevelPages[l] = ids;
            var type = l > 0 ? MappedPage.IndexPage : unit.IndexId <= 1 ? MappedPage.DataPage : MappedPage.IndexPage;
            for (var p = 0; p < list.Length; p++)
            {
                var previous = figures.IsHeap || p == 0 ? 0 : ids[p - 1];
                var next = figures.IsHeap || p == list.Length - 1 ? 0 : ids[p + 1];
                unit.Pages.Add(new MappedPage(ids[p], type, l, previous, next, list[p].Records + list[p].Ghosts, RealRecordFormat.PageBytes - list[p].Bytes, list[p].Ghosts));
            }
        }
        unit.FirstPage = unit.LevelPages[0].Length == 0 ? 0 : unit.LevelPages[0][0];
        unit.RootPage = figures.IsHeap ? 0 : unit.LevelPages[^1][0];
        unit.SortPages();
    }

    /// <summary>A rowset's LOB or row-overflow unit: its IAM page in a mixed extent and its text pages in extents of their own.</summary>
    private static MappedUnit MapSpillUnit(HeapTable table, IndexIdentity identity, PartitionCensus.Unit unit, byte type, RealRecordFormat.SpillUnit spill, RowsetFigures figures, PageAllocator allocator)
    {
        var mapped = new MappedUnit(table.ObjectId, identity.IndexId, unit.Number, unit.PartitionId, PartitionCensus.AllocationUnitId(unit.PartitionId, type), type,
            RowsetFigures.ReservedPages(spill), RowsetFigures.UsedPages(spill), 0, mixed: false)
        {
            Figures = figures,
        };
        if (spill.Pages == 0)
            return mapped;
        mapped.IamPage = allocator.TakeMixed();
        for (long i = 0; i < spill.Pages; i++)
        {
            if (i % 8 == 0)
                mapped.Extents.Add(allocator.TakeExtent());
            mapped.Pages.Add(new MappedPage(mapped.Extents[^1] + (int)(i % 8), MappedPage.TextPage, 0, 0, 0, -1, -1, 0));
        }
        mapped.FirstPage = mapped.Pages[0].Id;
        mapped.RootPage = mapped.FirstPage;
        return mapped;
    }

    /// <summary>Hands out file 1's pages: whole extents in turn, and single pages of the mixed extent being filled.</summary>
    private sealed class PageAllocator
    {
        private int next = FirstBufferedPageId;

        private int mixed = -1;

        private int mixedUsed = 8;

        public int MixedExtents;

        public int TakeExtent()
        {
            var first = this.next;
            this.next += 8;
            return first;
        }

        public int TakeMixed()
        {
            if (this.mixedUsed == 8)
            {
                this.mixed = this.TakeExtent();
                this.mixedUsed = 0;
                this.MixedExtents++;
            }
            return this.mixed + this.mixedUsed++;
        }
    }
}

/// <summary>
/// One allocation unit as <see cref="BuiltInResources.MapPages"/> lays it out
/// on file 1: its IAM page, its pages and the extents it holds.
/// </summary>
internal sealed class MappedUnit(int objectId, int indexId, int partitionNumber, long partitionId, long allocationUnitId, byte type, long totalPages, long usedPages, long dataPages, bool mixed)
{
    public readonly int ObjectId = objectId;
    public readonly int IndexId = indexId;
    public readonly int PartitionNumber = partitionNumber;
    public readonly long PartitionId = partitionId;
    public readonly long AllocationUnitId = allocationUnitId;

    /// <summary>1 in-row, 2 LOB, 3 row-overflow; 0 for the allocation maps' pages.</summary>
    public readonly byte Type = type;

    public readonly long TotalPages = totalPages;
    public readonly long UsedPages = usedPages;
    public readonly long DataPages = dataPages;

    /// <summary>Whether the unit's first pages come from mixed extents, as a system table's do.</summary>
    public readonly bool MixedAllocation = mixed;

    /// <summary>The unit's IAM page, 0 when it holds no page.</summary>
    public int IamPage;

    /// <summary>The first page of each extent the unit holds whole, in order.</summary>
    public readonly List<int> Extents = [];

    /// <summary>The unit's pages in mixed extents, its IAM page apart.</summary>
    public readonly List<int> MixedPages = [];

    /// <summary>Every page the unit uses but its IAM page, in page-id order.</summary>
    public readonly List<MappedPage> Pages = [];

    /// <summary>Each in-row level's page ids, the leaf's first, in key order — a heap's in the order it filled them.</summary>
    public int[][] LevelPages = [];

    /// <summary>The first page of the leaf in key order, 0 for none.</summary>
    public int FirstPage;

    /// <summary>An index's root page, a LOB or row-overflow unit's first page; 0 for a heap or no page.</summary>
    public int RootPage;

    /// <summary>The mixed extents the database's layout took, for the totals.</summary>
    public int MixedExtents;

    public RowsetFigures? Figures;

    public SystemTableObject? SystemTable;

    /// <summary>The pages the unit takes in mixed extents, its IAM page included.</summary>
    public int MixedPageCount => this.IamPage == 0 ? 0 : this.MixedPages.Count + 1;

    public void SortPages() => this.Pages.Sort(static (x, y) => x.Id.CompareTo(y.Id));
}

/// <summary>One page of a <see cref="MappedUnit"/>.</summary>
internal readonly struct MappedPage(int id, byte pageType, int level, int previous, int next, int records, int freeBytes, int ghosts)
{
    public const byte DataPage = 1, IndexPage = 2, TextPage = 3, IamPage = 10;

    public readonly int Id = id;

    /// <summary>Real's page type: 1 data, 2 index, 3 text, 10 IAM.</summary>
    public readonly byte PageType = pageType;

    public readonly int Level = level;

    /// <summary>The page before it on its level in key order, 0 for none.</summary>
    public readonly int Previous = previous;

    /// <summary>The page after it on its level in key order, 0 for none.</summary>
    public readonly int Next = next;

    /// <summary>The records on the page, ghosts included; -1 when the layout doesn't keep them.</summary>
    public readonly int Records = records;

    /// <summary>The page's free bytes; -1 when the layout doesn't keep them.</summary>
    public readonly int FreeBytes = freeBytes;

    public readonly int Ghosts = ghosts;
}
