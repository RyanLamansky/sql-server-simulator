using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// DBCC SHOWCONTIG and DBCC IND, which read the pages BuiltInResources.MapPages
// lays each rowset out on. Probed 2026-10-17 against SQL Server 2025.
partial class Simulation
{
    private static readonly string[] ShowContigColumnNames =
    [
        "ObjectName", "ObjectId", "IndexName", "IndexId", "Level", "Pages", "Rows", "MinimumRecordSize", "MaximumRecordSize", "AverageRecordSize",
        "ForwardedRecords", "Extents", "ExtentSwitches", "AverageFreeBytes", "AveragePageDensity", "ScanDensity", "BestCount", "ActualCount",
        "LogicalFragmentation", "ExtentFragmentation",
    ];

    private static readonly string[] IndColumnNames =
    [
        "PageFID", "PagePID", "IAMFID", "IAMPID", "ObjectID", "IndexID", "PartitionNumber", "PartitionID", "iam_chain_type", "PageType", "IndexLevel",
        "NextPageFID", "NextPagePID", "PrevPageFID", "PrevPagePID",
    ];

    /// <summary>
    /// <c>DBCC SHOWCONTIG [( table [, index] )] [WITH ALL_INDEXES, FAST,
    /// TABLERESULTS, ALL_LEVELS, NO_INFOMSGS]</c>: each index's first
    /// partition, its leaf pages as the storage figures lay them out —
    /// pages, extents and extent switches read off the page ids, the scan
    /// density their best and actual extent counts, the logical fragmentation
    /// the leaf's out-of-order pages, the extent fragmentation the extents a
    /// scan leaves out of order — reported as Msg 7942 through 7954 or, under
    /// <c>TABLERESULTS</c>, as rows, every level under <c>ALL_LEVELS</c>.
    /// Without a table every user table and queue of the current database,
    /// in object-id order, takes <c>db_owner</c> (Msg 7983 state 10); one
    /// table takes <c>ALTER</c> on it (Msg 229). <c>ALL_INDEXES</c> reports
    /// every index, whatever index is named; <c>FAST</c> leaves the record
    /// and page-fill figures out; <c>NO_INFOMSGS</c> silences Msg 2528
    /// alone. A <c>sys</c> system table reports nothing, a <c>#temp</c> table
    /// goes by <c>tempdb..</c>, and an index the table lacks is Msg 7999
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccShowContig(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.TableResults | DbccOptions.AllIndexes | DbccOptions.AllLevels | DbccOptions.Fast);
        dbcc.RequireArgumentCount(0, 2);
        var allIndexes = dbcc.Has(DbccOptions.AllIndexes);
        var targets = new List<ContigTarget>();
        if (dbcc.Arguments.Count == 0)
        {
            var database = batch.CurrentDatabase;
            if (!IsSysadminSession(batch) && !PermissionEnforcement.IsOwner(batch, database))
                throw SimulatedSqlException.DbccDatabasePermissionDenied(batch.Connection.Security.Effective.DatabasePrincipalName, "SHOWCONTIG", database.Name, 10);
            foreach (var (table, _) in UserTablesByObjectId(database, filegroupId: null))
                AddContigTargets(targets, database, table, table.Name, allIndexes ? null : BaseIndexId(table));
            foreach (var queue in database.ServiceBroker.AllQueues(database))
                targets.Add(new ContigTarget(database, queue.Name, queue.ObjectId, "queue_clustered_index", 1, MessageTableOf(database, queue)));
            targets.Sort(static (x, y) => x.ObjectId != y.ObjectId ? x.ObjectId.CompareTo(y.ObjectId) : x.IndexId.CompareTo(y.IndexId));
        }
        else
        {
            var resolved = ResolveDbccPagedObject(batch, dbcc, 0, batch.CurrentDatabase, temporaryNeedsDatabase: true);
            if (resolved.Table is not { } table)
                return DbccCompleted(batch, dbcc, []);
            RequireDbccTableAlter(batch, resolved.Database, table);
            int? only = BaseIndexId(table);
            if (allIndexes)
                only = null;
            else if (dbcc.Arguments.Count == 2)
                only = ResolveDbccIndexArgument(batch, dbcc, 1, table);
            AddContigTargets(targets, resolved.Database, table, resolved.Display, only);
        }

        var tableResults = dbcc.Has(DbccOptions.TableResults);
        var fast = dbcc.Has(DbccOptions.Fast);
        var allLevels = dbcc.Has(DbccOptions.AllLevels) && !fast;
        var simulation = batch.Connection.Simulation;
        var maps = new Dictionary<Database, Dictionary<(int, int), MappedUnit>>();
        var rows = new List<SqlValue[]>();
        foreach (var target in targets)
        {
            if (!maps.TryGetValue(target.Database, out var map))
            {
                map = [];
                foreach (var unit in BuiltInResources.MapPages(batch, target.Database))
                {
                    if (unit.Type == 1 && unit.PartitionNumber == 1)
                        map[(unit.ObjectId, unit.IndexId)] = unit;
                }
                maps[target.Database] = map;
            }
            var unitKey = target.MessageTable is { } messages ? (messages.ObjectId, 1) : (target.ObjectId, target.IndexId);
            var mapped = map.GetValueOrDefault(unitKey);
            var levels = ContigLevels(mapped, target.IsHeap, allLevels && tableResults);
            if (tableResults)
            {
                foreach (var level in levels)
                    rows.Add(ShowContigRow(target, level, fast));
                continue;
            }
            var leaf = levels[0];
            Info(batch, SimulatedSqlException.ShowContigScanningMessage(batch, target.Name));
            Info(batch, SimulatedSqlException.ShowContigTableMessage(batch, target.Name, target.ObjectId, target.IndexId, DatabaseIdOf(simulation, target.Database)));
            Info(batch, SimulatedSqlException.ShowContigLevelMessage(batch, target.IndexId <= 1 ? "TABLE" : "LEAF"));
            Figure(7945, leaf.Pages.ToString(CultureInfo.InvariantCulture));
            if (!fast || target.IsHeap)
                Figure(7946, leaf.Extents.ToString(CultureInfo.InvariantCulture));
            Figure(7947, leaf.Switches.ToString(CultureInfo.InvariantCulture));
            if (!fast)
                Figure(7948, (leaf.Extents == 0 ? 0 : (double)leaf.Pages / leaf.Extents).ToString("F1", CultureInfo.InvariantCulture));
            Figure(7949, string.Create(CultureInfo.InvariantCulture, $"{leaf.ScanDensity:F2}% [{leaf.BestCount}:{leaf.ActualCount}]"));
            if (!target.IsHeap)
                Figure(7950, string.Create(CultureInfo.InvariantCulture, $"{leaf.LogicalFragmentation:F2}%"));
            if (!fast || target.IsHeap)
                Figure(7952, string.Create(CultureInfo.InvariantCulture, $"{leaf.ExtentFragmentation:F2}%"));
            if (!fast)
            {
                Figure(7953, leaf.AverageFreeBytes.ToString("F1", CultureInfo.InvariantCulture));
                Figure(7954, string.Create(CultureInfo.InvariantCulture, $"{leaf.PageDensity:F2}%"));
            }
        }
        if (tableResults)
            return DbccCompleted(batch, dbcc, DbccRows(batch, ShowContigSchema(batch), ShowContigColumnNames, rows));
        batch.Connection.LastStatementRowCount = 0;
        return DbccCompleted(batch, dbcc, []);

        void Figure(int number, string value) => Info(batch, SimulatedSqlException.ShowContigFigureMessage(batch, number, value));
    }

    /// <summary>One index <c>DBCC SHOWCONTIG</c> reports on: its table's name as the report gives it, and the message table a queue's figures come from.</summary>
    private sealed class ContigTarget(Database database, string name, int objectId, string indexName, int indexId, SystemTableObject? messageTable)
    {
        public readonly Database Database = database;
        public readonly string Name = name;
        public readonly int ObjectId = objectId;
        public readonly string IndexName = indexName;
        public readonly int IndexId = indexId;
        public readonly SystemTableObject? MessageTable = messageTable;

        public bool IsHeap => this.IndexId == 0;
    }

    /// <summary>One level's figures in a <c>DBCC SHOWCONTIG</c> report.</summary>
    private readonly struct ContigLevel(int level, LevelFigures figures, long extents, long switches, long outOfOrderExtents, bool scanned, bool heap)
    {
        public readonly int Level = level;
        public readonly LevelFigures Figures = figures;
        public readonly long Extents = extents;
        public readonly long Switches = scanned ? switches : 0;
        public readonly long OutOfOrderExtents = outOfOrderExtents;
        public readonly bool Scanned = scanned;
        public readonly bool Heap = heap;

        public long Pages => this.Figures.Pages;

        public long BestCount => this.Scanned ? (this.Pages + 7) / 8 : 0;

        public long ActualCount => this.Scanned && this.Pages > 0 ? this.Switches + 1 : 0;

        public double ScanDensity => !this.Scanned ? 0 : this.ActualCount == 0 ? 100 : 100.0 * this.BestCount / this.ActualCount;

        public double LogicalFragmentation => !this.Scanned || this.Heap || this.Pages == 0 ? 0 : 100.0 * this.Figures.OutOfOrder / this.Pages;

        public double ExtentFragmentation => !this.Scanned || this.Extents == 0 ? 0 : 100.0 * this.OutOfOrderExtents / this.Extents;

        /// <summary>The page's free bytes on average, cut to three decimals as real's are.</summary>
        public double AverageFreeBytes => this.Pages == 0 ? 0 : Math.Truncate(((this.Pages * (double)RealRecordFormat.PageBytes) - this.Figures.Bytes) / this.Pages * 1000) / 1000;

        public double PageDensity => RowsetFigures.PageSpaceUsedPercent(this.Figures.Bytes, this.Pages);

        /// <summary>The record size on average, cut to three decimals as real's is.</summary>
        public double AverageRecordSize => this.Figures.SizedRecords == 0 ? 0 : Math.Truncate((double)this.Figures.RecordBytes / this.Figures.SizedRecords * 1000) / 1000;
    }

    /// <summary>
    /// The levels of <paramref name="unit"/> a report covers — the leaf, or
    /// every level from the leaf up — each with the extents its pages sit in
    /// read in key order: how many, how often the scan moves to another, and
    /// how often to one that isn't the next. Only the leaf is scanned for the
    /// density and fragmentation figures, which read 0 above it (probed
    /// 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static List<ContigLevel> ContigLevels(MappedUnit? unit, bool heap, bool every)
    {
        var levels = new List<ContigLevel>();
        var figures = unit?.Figures?.Levels ?? [];
        if (figures.Length == 0 || unit is null || unit.LevelPages.Length != figures.Length)
        {
            levels.Add(new ContigLevel(0, new LevelFigures(), 0, 0, 0, scanned: true, heap));
            return levels;
        }
        for (var l = 0; l < (every ? figures.Length : 1); l++)
        {
            var ids = unit.LevelPages[l];
            var seen = new HashSet<int>();
            long switches = 0, outOfOrder = 0;
            for (var i = 0; i < ids.Length; i++)
            {
                var extent = ids[i] >> 3;
                _ = seen.Add(extent);
                if (i == 0 || extent == ids[i - 1] >> 3)
                    continue;
                switches++;
                if (extent != (ids[i - 1] >> 3) + 1)
                    outOfOrder++;
            }
            levels.Add(new ContigLevel(l, figures[l], seen.Count, switches, outOfOrder, scanned: l == 0, heap));
        }
        return levels;
    }

    /// <summary>A <c>DBCC SHOWCONTIG … WITH TABLERESULTS</c> row; <c>FAST</c> leaves the figures it didn't read NULL.</summary>
    private static SqlValue[] ShowContigRow(ContigTarget target, ContigLevel level, bool fast)
    {
        var figures = level.Figures;
        var nullBig = SqlValue.Null(SqlType.BigInt);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullFloat = SqlValue.Null(SqlType.Float);
        return
        [
            SqlValue.FromNVarchar(target.Name),
            SqlValue.FromInt32(target.ObjectId),
            SqlValue.FromNVarchar(target.IndexName),
            SqlValue.FromInt32(target.IndexId),
            SqlValue.FromInt32(level.Level),
            SqlValue.FromInt64(level.Pages),
            fast ? nullBig : SqlValue.FromInt64(figures.Records),
            fast ? nullInt : SqlValue.FromInt32(figures.MinRecord),
            fast ? nullInt : SqlValue.FromInt32(figures.MaxRecord),
            fast ? nullFloat : SqlValue.FromDouble(level.AverageRecordSize),
            fast ? nullBig : SqlValue.FromInt64(figures.Forwarded),
            fast && !target.IsHeap ? nullBig : SqlValue.FromInt64(level.Extents),
            SqlValue.FromInt64(level.Switches),
            fast ? nullFloat : SqlValue.FromDouble(level.AverageFreeBytes),
            fast ? nullFloat : SqlValue.FromDouble(level.PageDensity),
            SqlValue.FromDouble(level.ScanDensity),
            SqlValue.FromInt64(level.BestCount),
            SqlValue.FromInt64(level.ActualCount),
            SqlValue.FromDouble(level.LogicalFragmentation),
            fast && !target.IsHeap ? nullFloat : SqlValue.FromDouble(level.ExtentFragmentation),
        ];
    }

    private static SqlType[] ShowContigSchema(BatchContext batch)
    {
        var name = NVarcharSqlType.Get(128, batch.CurrentDatabase.Collation, Coercibility.Implicit);
        return
        [
            name, SqlType.Int32, name, SqlType.Int32, SqlType.Int32, SqlType.BigInt, SqlType.BigInt, SqlType.Int32, SqlType.Int32, SqlType.Float,
            SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.Float, SqlType.Float, SqlType.Float, SqlType.BigInt, SqlType.BigInt,
            SqlType.Float, SqlType.Float,
        ];
    }

    /// <summary>The indexes of <paramref name="table"/> a report covers: the one <paramref name="only"/> names, or every one holding storage.</summary>
    private static void AddContigTargets(List<ContigTarget> targets, Database database, HeapTable table, string display, int? only)
    {
        foreach (var identity in table.IndexIdentities())
        {
            if (only is { } wanted && wanted != identity.IndexId)
                continue;
            targets.Add(new ContigTarget(database, display, table.ObjectId, identity.Name ?? "", identity.IndexId, null));
        }
    }

    /// <summary>The index a table's base rowset is: its clustered index, else its heap.</summary>
    private static int BaseIndexId(HeapTable table) => table.HasClusteredIndex() ? 1 : 0;

    /// <summary>The internal table holding <paramref name="queue"/>'s messages.</summary>
    private static SystemTableObject? MessageTableOf(Database database, ServiceQueue queue)
    {
        foreach (var systemTable in BuiltInResources.SystemTableObjects(database))
        {
            if (systemTable.ParentObjectId == queue.ObjectId && systemTable.Name.StartsWith("queue_messages_", StringComparison.Ordinal))
                return systemTable;
        }
        return null;
    }

    /// <summary>
    /// A DBCC index argument naming one of <paramref name="table"/>'s indexes,
    /// by name or id — 0 its base rowset — and Msg 7999 for one it lacks:
    /// state 8 naming a name, 7 a number.
    /// </summary>
    private static int ResolveDbccIndexArgument(BatchContext batch, DbccInvocation dbcc, int index, HeapTable table)
    {
        var argument = dbcc.Arguments[index];
        var identities = table.IndexIdentities();
        if (argument.Kind != DbccArgumentKind.Name && argument.Evaluate(batch) is { IsNull: false } value && SqlType.IsIntegerCategory(value.Type))
        {
            var id = value.CoerceTo(SqlType.BigInt).AsInt64;
            if (id == 0)
                return BaseIndexId(table);
            return identities.Exists(identity => identity.IndexId == id)
                ? (int)id
                : throw SimulatedSqlException.DbccIndexNotFound(id.ToString(CultureInfo.InvariantCulture), table.Name, 7);
        }
        var name = dbcc.StringArgument(batch, index) ?? throw SimulatedSqlException.DbccParameterIsIncorrect(index + 1);
        foreach (var identity in identities)
        {
            if (identity.Name is { } indexName && BuiltInToken.Equals(indexName, name))
                return identity.IndexId;
        }
        throw SimulatedSqlException.DbccIndexNotFound(name, table.Name, 8);
    }

    /// <summary>
    /// A DBCC page command's object argument in <paramref name="database"/>:
    /// a table by name or object id, or a <c>sys</c>-qualified system table
    /// (returned with no table). A view is Msg 5239, a miss Msg 2501 or 2573.
    /// Under <paramref name="temporaryNeedsDatabase"/> a <c>#temp</c> table
    /// is found only through a <c>tempdb</c> qualifier, as
    /// <c>SHOWCONTIG</c> finds it. Returns the name its report gives the table
    /// — bare, or a <c>#temp</c> table's padded internal one — and the
    /// database holding it.
    /// </summary>
    private static (HeapTable? Table, SystemTableObject? SystemTable, string Display, Database Database) ResolveDbccPagedObject(
        BatchContext batch, DbccInvocation dbcc, int index, Database database, bool temporaryNeedsDatabase)
    {
        var argument = dbcc.Arguments[index];
        if (argument.Kind != DbccArgumentKind.Name && argument.Evaluate(batch) is { IsNull: false } id && SqlType.IsIntegerCategory(id.Type))
        {
            var objectId = id.CoerceTo(SqlType.Int32).AsInt32;
            foreach (var (table, _) in UserTablesByObjectId(database, filegroupId: null))
            {
                if (table.ObjectId == objectId)
                    return (table, null, table.Name, database);
            }
            foreach (var systemTable in BuiltInResources.SystemTableObjects(database))
            {
                if (systemTable.ObjectId == objectId)
                    return (null, systemTable, systemTable.Name, database);
            }
            throw SimulatedSqlException.CouldNotFindObjectId(objectId);
        }
        var (name, written) = DbccObjectName(batch, argument, index + 1);
        if (name.Leaf.StartsWith('#'))
        {
            if (temporaryNeedsDatabase && (name.Count < 3 || !BuiltInToken.Equals(name[name.Count - 3], TempdbDatabaseName)))
                throw SimulatedSqlException.CannotFindTableOrObject(written);
            if (!batch.TryResolveTable(name, out var temporary))
                throw SimulatedSqlException.CannotFindTableOrObject(written);
            return (temporary, null, temporary.InternalName ?? temporary.Name, batch.Connection.Simulation.Databases[TempdbDatabaseName]);
        }
        if (name.Count >= 2 && BuiltInToken.Equals(name.ImmediateQualifier, "sys"))
        {
            foreach (var systemTable in BuiltInResources.SystemTableObjects(database))
            {
                if (BuiltInToken.Equals(systemTable.Name, name.Leaf))
                    return (null, systemTable, systemTable.Name, database);
            }
        }
        HeapTable? found;
        if (ReferenceEquals(database, batch.CurrentDatabase))
        {
            found = TryResolveDbccObject(batch, name, written);
        }
        else
        {
            found = null;
            var schemaName = name.Count >= 2 ? name.ImmediateQualifier! : Database.DefaultSchemaName;
            if (database.Schemas.TryGetValue(schemaName, out var schema))
            {
                if (schema.HeapTables.TryGetValue(name.Leaf, out var table))
                    found = table;
                else if (schema.Views.TryGetValue(name.Leaf, out var view))
                    throw SimulatedSqlException.DbccObjectTypeNotSupported(view.ObjectId, view.Name);
            }
        }
        if (found is null)
            throw SimulatedSqlException.CannotFindTableOrObject(written);
        return (found, null, found.InternalName ?? found.Name, found.OwningDatabase ?? database);
    }

    /// <summary>
    /// <c>DBCC IND ( database, table, index [, partition] )</c>, <c>sysadmin</c>
    /// only (Msg 2571): a row per page of the index's allocation units, each
    /// unit's IAM page first and then its pages in page-id order — in-row,
    /// row-overflow and LOB units in turn — with its type, level and its
    /// neighbors on its level, as <see cref="BuiltInResources.MapPages"/>
    /// numbers them. The index is an id: 0 the base rowset, -1 every index,
    /// -2 every index's IAM pages alone, another number Msg 7999 when the
    /// table lacks it, anything else Msg 2560 state 9; the partition 0 for
    /// all, below 0 Msg 2587, past the index's Msg 2586 state 5. A system
    /// table may be named; any <c>WITH</c> option is accepted; no page sends
    /// no result set (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccInd(BatchContext batch, DbccInvocation dbcc)
    {
        RequireDbccSysadmin(batch, "ind", 1);
        dbcc.RequireArgumentCount(3, 4);
        var database = ResolveDbccDatabase(batch, dbcc.Arguments[0], 1);
        var (table, systemTable, _, owner) = ResolveDbccPagedObject(batch, dbcc, 1, database, temporaryNeedsDatabase: false);
        var indexValue = dbcc.Arguments[2].Kind == DbccArgumentKind.Name ? SqlValue.Null(SqlType.Int32) : dbcc.Arguments[2].Evaluate(batch);
        if (indexValue.IsNull || !SqlType.IsIntegerCategory(indexValue.Type) || indexValue.CoerceTo(SqlType.BigInt).AsInt64 < -2)
            throw SimulatedSqlException.DbccParameterIsIncorrect(3);
        var indexId = indexValue.CoerceTo(SqlType.BigInt).AsInt64;
        var objectId = table?.ObjectId ?? systemTable!.ObjectId;
        if (table is not null)
        {
            if (indexId == 0)
                indexId = BaseIndexId(table);
            else if (indexId > 0 && !table.IndexIdentities().Exists(identity => identity.IndexId == indexId))
                throw SimulatedSqlException.DbccIndexNotFound(indexId.ToString(CultureInfo.InvariantCulture), table.Name, 7);
        }
        else if (indexId == 0 && !Array.Exists(systemTable!.Rowsets, rowset => rowset.IndexId == 0))
        {
            indexId = 1;
        }
        long partition = 0;
        if (dbcc.Arguments.Count == 4)
        {
            var partitionValue = dbcc.Arguments[3].Kind == DbccArgumentKind.Name ? SqlValue.Null(SqlType.Int32) : dbcc.Arguments[3].Evaluate(batch);
            if (partitionValue.IsNull || !SqlType.IsIntegerCategory(partitionValue.Type))
                throw SimulatedSqlException.DbccParameterIsIncorrect(4);
            partition = partitionValue.CoerceTo(SqlType.BigInt).AsInt64;
            if (partition < 0)
                throw SimulatedSqlException.DbccInvalidPartitionNumber(partition);
        }

        var units = BuiltInResources.MapPages(batch, owner).FindAll(unit => unit.ObjectId == objectId && (indexId < 0 || unit.IndexId == indexId));
        if (partition > 0 && !units.Exists(unit => unit.PartitionNumber == partition))
        {
            var indexName = table?.IndexIdentities().Find(identity => identity.IndexId == Math.Max(indexId, 0)).Name ?? "";
            throw SimulatedSqlException.DbccPartitionNotFound(partition, indexName, table?.Name ?? systemTable!.Name);
        }
        var fileId = SqlValue.FromInt16(1);
        var noFile = SqlValue.Null(SqlType.SmallInt);
        var noPage = SqlValue.Null(SqlType.Int32);
        var zeroFile = SqlValue.FromInt16(0);
        var zeroPage = SqlValue.FromInt32(0);
        var rows = new List<SqlValue[]>();
        foreach (var unit in units)
        {
            if (unit.IamPage == 0 || (partition > 0 && unit.PartitionNumber != partition))
                continue;
            var head = new SqlValue[]
            {
                SqlValue.FromInt32(unit.ObjectId),
                SqlValue.FromInt32(unit.IndexId),
                SqlValue.FromInt32(unit.PartitionNumber),
                SqlValue.FromInt64(unit.PartitionId),
                SqlValue.FromNVarchar(unit.Type switch { 2 => "LOB data", 3 => "Row-overflow data", _ => "In-row data" }),
            };
            var iam = SqlValue.FromInt32(unit.IamPage);
            rows.Add([fileId, iam, noFile, noPage, .. head, SqlValue.FromInt16(MappedPage.IamPage), SqlValue.Null(SqlType.SmallInt), zeroFile, zeroPage, zeroFile, zeroPage]);
            if (indexId == -2)
                continue;
            foreach (var page in unit.Pages)
            {
                rows.Add([
                    fileId, SqlValue.FromInt32(page.Id), fileId, iam, .. head, SqlValue.FromInt16(page.PageType), SqlValue.FromInt16((short)page.Level),
                    page.Next == 0 ? zeroFile : fileId, SqlValue.FromInt32(page.Next), page.Previous == 0 ? zeroFile : fileId, SqlValue.FromInt32(page.Previous),
                ]);
            }
        }
        if (rows.Count == 0)
            return DbccCompleted(batch, dbcc, []);
        SqlType[] schema =
        [
            SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.BigInt,
            NVarcharSqlType.Get(20, batch.CurrentDatabase.Collation, Coercibility.Implicit), SqlType.SmallInt, SqlType.SmallInt,
            SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.Int32,
        ];
        return DbccCompleted(batch, dbcc, DbccRows(batch, schema, IndColumnNames, rows));
    }
}
