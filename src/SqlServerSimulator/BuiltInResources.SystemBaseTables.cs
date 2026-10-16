using SqlServerSimulator.Storage;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

// The system base tables and internal tables every database lists: the
// fresh-database set SystemTables carries, and the internal tables CREATE
// QUEUE and ENABLE CHANGE_TRACKING add.
internal static partial class BuiltInResources
{
    /// <summary>
    /// Every system base table and internal table of <paramref name="database"/>,
    /// in object-id order within each part: the set a fresh database of its kind
    /// holds on real (<see cref="SystemTables.Of"/>), then the
    /// <c>queue_messages_&lt;id&gt;</c> table of each queue <c>CREATE QUEUE</c>
    /// made and the <c>change_tracking_&lt;id&gt;</c> table of each tracked
    /// table, which real adds as it creates the queue or enables tracking
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static IEnumerable<SystemTableObject> SystemTableObjects(Database database)
    {
        foreach (var table in SystemTables.Of(database.Name))
            yield return table;
        var queues = new List<ServiceQueue>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, queue) in schema.Queues)
                queues.Add(queue);
        }
        queues.Sort(static (x, y) => x.ObjectId.CompareTo(y.ObjectId));
        foreach (var queue in queues)
            yield return QueueMessagesTable(queue);
        var tracked = new List<HeapTable>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.ChangeTracking is not null)
                    tracked.Add(table);
            }
        }
        tracked.Sort(static (x, y) => x.ObjectId.CompareTo(y.ObjectId));
        foreach (var table in tracked)
            yield return ChangeTrackingTable(table, table.ChangeTracking!);
    }

    /// <summary>
    /// The internal change table <c>ALTER TABLE … ENABLE CHANGE_TRACKING</c>
    /// adds: the transaction, operation, columns-mask and context columns, then
    /// a copy of each primary-key column in key order named
    /// <c>k_&lt;column&gt;_&lt;column id as 8 hex digits&gt;</c>, clustered on the
    /// transaction pair, one row per committed change to a row (probed
    /// 2026-10-16 against SQL Server 2025). Its pages are an estimate: one data
    /// page per 200 changes and an IAM page, in whole extents; a change table
    /// holding none has none.
    /// </summary>
    private static SystemTableObject ChangeTrackingTable(HeapTable table, Storage.TableChangeTracking tracking)
    {
        var definition = tracking.InternalTableDefinition ??= ChangeTrackingDefinition(table);
        var objectId = tracking.InternalTableId;
        var partitionId = ((long)(uint)objectId << 16) | 1;
        var rows = tracking.ChangeCount;
        var dataPages = (rows + 199) / 200;
        var usedPages = dataPages == 0 ? 0 : dataPages + 1;
        var totalPages = usedPages == 0 ? 0 : 1 + ((usedPages - 1 + 7) / 8 * 8);
        var (leafMax, leafMin, leafColumns) = RecordLength(definition.HeapColumns(""), [.. Enumerable.Range(0, definition.Columns.Length)], uniquifier: false, locator: false, header: 4);
        var (upperMax, upperMin, _) = RecordLength(definition.HeapColumns(""), [0, 1], uniquifier: false, locator: false, header: 7);
        return new SystemTableObject("change_tracking_" + table.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture), objectId, isInternal: true,
            Database.SysSchemaId, table.ObjectId, tracking.EnabledAt, tracking.EnabledAt, internalType: 209, lobDataSpaceId: 0, definition,
            [
                new SystemRowset(1, partitionId, rows, 0, 0, (short)leafColumns, leafMax, (short)leafMin, (short)Math.Max(15, upperMax), (short)upperMin,
                    allowsNullableKeys: true, isDataRowFormat: true, isNotVersioned: false,
                    [new SystemAllocationUnit(PartitionCensus.AllocationUnitId(partitionId, 1), 1, totalPages, usedPages, dataPages)]),
            ],
            []);
    }

    private static SystemTableDefinition ChangeTrackingDefinition(HeapTable table)
    {
        var columns = new List<SystemTableColumn>
        {
            new(1, "sys_change_xdes_id", 127, 8, 19, 0, nullable: false, hidden: false, null, 0, null),
            new(2, "sys_change_xdes_id_seq", 127, 8, 19, 0, nullable: false, hidden: false, null, 0, null),
            new(3, "sys_change_operation", 239, 2, 0, 0, nullable: true, hidden: false, null, 0, "Latin1_General_BIN"),
            new(4, "sys_change_columns", 165, 4100, 0, 0, nullable: true, hidden: false, null, 0, null),
            new(5, "sys_change_context", 165, 128, 0, 0, nullable: true, hidden: false, null, 0, null),
        };
        var primaryKey = table.KeyConstraints.Find(static key => key.Kind == KeyConstraintKind.PrimaryKey);
        foreach (var ordinal in primaryKey?.FullOrdinals ?? [])
        {
            var key = table.Columns[ordinal];
            var (maxLength, precision, scale) = GetSysColumnMetadata(key);
            columns.Add(new SystemTableColumn(columns.Count + 1, $"k_{key.Name}_{key.ColumnId:x8}", key.UserTypeId, maxLength, precision, scale,
                nullable: false, hidden: false, null, 0, key.Collation, key));
        }
        return new SystemTableDefinition("change_tracking", [.. columns],
            [new SystemTableIndex(1, "sys_change_cidx", 1, unique: true, allowPageLocks: true, [new SystemIndexKey(1, 1, false, false), new SystemIndexKey(2, 2, false, false)])]);
    }

    /// <summary>
    /// The internal message table of a queue <c>CREATE QUEUE</c> made: real's
    /// shape and record lengths for every queue's, its clustered index carrying
    /// the in-row, LOB and row-overflow units and its secondary index one
    /// in-row unit, each empty, with the queue's dates (probed 2026-10-16
    /// against SQL Server 2025). The partition and allocation-unit ids are
    /// synthetic, as a user table's are.
    /// </summary>
    private static SystemTableObject QueueMessagesTable(ServiceQueue queue)
    {
        var objectId = queue.InternalTableId;
        static SystemAllocationUnit Unit(long partitionId, byte type) =>
            new(PartitionCensus.AllocationUnitId(partitionId, type), type, 0, 0, 0);
        var clustered = ((long)(uint)objectId << 16) | 1;
        var secondary = ((long)(uint)objectId << 16) | 2;
        return new SystemTableObject(
            "queue_messages_" + queue.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture), objectId, isInternal: true, Database.SysSchemaId, queue.ObjectId,
            queue.CreateDate, queue.ModifyDate, internalType: 201, lobDataSpaceId: Database.PrimaryFilegroupId, SystemTables.Definition("queue_messages"),
            [
                new SystemRowset(1, clustered, 0, 0, 2, 16, 8116, 108, 52, 49, allowsNullableKeys: true, isDataRowFormat: true, isNotVersioned: true,
                    [Unit(clustered, 1), Unit(clustered, 2), Unit(clustered, 3)]),
                new SystemRowset(2, secondary, 0, 0, 2, 6, 50, 47, 56, 53, allowsNullableKeys: true, isDataRowFormat: false, isNotVersioned: true,
                    [Unit(secondary, 1)]),
            ],
            []);
    }

    /// <summary>
    /// The rows <c>sys.partitions</c> reports for one of a system table's
    /// rowsets: a queue's message table holds the queue's messages, a Query
    /// Store table the rows its catalog view lists — the version-2 runtime and
    /// wait statistics tables holding what SQL Server 2025 records, its
    /// version-1 pair nothing (probed 2026-10-16 against SQL Server 2025) — and
    /// every other table the rows a fresh database's holds on real. Without a
    /// <paramref name="batch"/> a Query Store table reports its fresh rows.
    /// </summary>
    internal static long SystemRowsetRows(Parser.BatchContext? batch, Database database, SystemTableObject table, SystemRowset rowset)
    {
        if (table.InternalType == 201)
            return QueueOf(database, table.ParentObjectId) is { } queue ? queue.Messages.Count : rowset.Rows;
        if (batch is null)
            return rowset.Rows;
        if (UserCatalogRows(batch, database, table) is { } added)
            return rowset.Rows + added;
        if (table.InternalType is 6 or 243)
            return 0;
        var view = table.InternalType switch
        {
            3 => "sys.query_store_query_hints",
            8 => "sys.query_store_query_variant",
            9 => "sys.query_store_replicas",
            10 => "sys.query_store_plan_forcing_locations",
            11 => "sys.query_store_runtime_stats",
            12 => "sys.query_store_wait_stats",
            240 => "sys.query_store_query_text",
            241 => "sys.query_store_query",
            242 => "sys.query_store_plan",
            244 => "sys.query_store_runtime_stats_interval",
            245 => "sys.query_context_settings",
            250 => "sys.query_store_plan_feedback",
            _ => null,
        };
        if (view is null)
            return rowset.Rows;
        long rows = 0;
        foreach (var _ in Simulation.CatalogViews[view].RowGenerator(batch, database))
            rows++;
        return rows;
    }

    /// <summary>
    /// The rows a user database's catalog base table holds past a fresh
    /// database's for what its user made, or null for a table that isn't
    /// counted: <c>sysschobjs</c> a row per object <c>sys.objects</c> lists,
    /// <c>syscolpars</c> per column and parameter, <c>sysidxstats</c> per index
    /// (a heap's included) and statistic, <c>sysiscols</c> per index and
    /// statistic column, <c>sysrowsets</c> per rowset partition,
    /// <c>sysallocunits</c> per allocation unit, <c>sysrscols</c> per rowset
    /// column and <c>sysscalartypes</c> per user type (probed 2026-10-16 against
    /// SQL Server 2025, over tables, constraints, indexes, a statistic, a view,
    /// a procedure, a function and a table type). The system databases keep
    /// their fresh rows.
    /// </summary>
    private static long? UserCatalogRows(Parser.BatchContext batch, Database database, SystemTableObject table)
    {
        var slot = table.IsInternal || !SystemTables.IsUserDatabase(database.Name) ? -1 : table.ObjectId switch
        {
            3 => 6,
            5 => 4,
            7 => 5,
            34 => 0,
            41 => 1,
            50 => 7,
            54 => 2,
            55 => 3,
            _ => -1,
        };
        if (slot < 0)
            return null;
        var frame = batch.CurrentStatement;
        if (!(frame.UserCatalogRowCounts ??= []).TryGetValue(database, out var counts))
            frame.UserCatalogRowCounts[database] = counts = CountUserCatalogRows(batch, database);
        return counts[slot];
    }

    private static long[] CountUserCatalogRows(Parser.BatchContext batch, Database database)
    {
        // A catalog view's rows come from the cross-statement cache where it
        // serves them, so a read of sys.partitions doesn't project the whole
        // catalog again.
        long Rows(string view)
        {
            var catalogView = Simulation.CatalogViews[view];
            if (Parser.Selection.CachedCatalogRows(catalogView, batch, database) is { } cached)
                return cached.Rows.Count;
            long rows = 0;
            foreach (var _ in catalogView.RowGenerator(batch, database))
                rows++;
            return rows;
        }
        var fresh = SystemTables.Of(database.Name);
        long freshColumns = 0, freshIndexes = 0, freshIndexColumns = 0, freshUnits = 0;
        foreach (var table in fresh)
        {
            freshColumns += table.Definition.Columns.Length;
            freshIndexes += table.Definition.Indexes.Length;
            foreach (var index in table.Definition.Indexes)
                freshIndexColumns += index.Keys.Length;
            foreach (var rowset in table.Rowsets)
                freshUnits += rowset.Units.Length;
        }
        long statistics = 0, statisticColumns = 0, types = 0;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var table in CatalogTables(schema, batch))
            {
                statistics += table.UserStatistics.Count;
                foreach (var statistic in table.UserStatistics)
                    statisticColumns += statistic.ColumnFullOrdinals.Length;
            }
            foreach (var _ in schema.AliasTypes)
                types++;
            foreach (var _ in schema.TableTypes)
                types++;
        }
        // The rowsets are counted here rather than off sys.partitions, whose
        // system rows ask for these very counts.
        long rowsets = 0, rowsetColumns = 0;
        var census = new PartitionCensus();
        foreach (var (table, identity, placement) in EnumerateStorageIdentities(database, batch))
        {
            var columns = RealRowsetLengths(table, identity).NullBits;
            foreach (var _ in census.Units(table, identity.IndexId, placement))
            {
                rowsets++;
                rowsetColumns += columns;
            }
        }
        foreach (var table in SystemTableObjects(database))
        {
            if (Array.IndexOf(fresh, table) >= 0)
                continue;
            foreach (var rowset in table.Rowsets)
            {
                rowsets++;
                rowsetColumns += rowset.MaxNullBitUsed;
            }
        }
        long units = 0;
        foreach (var _ in EnumerateAllocationUnitData(batch, database))
            units++;
        return
        [
            Rows("sys.objects") - fresh.Length - database.ServiceBroker.SystemQueues.Length,
            Rows("sys.columns") - freshColumns + Rows("sys.parameters"),
            Rows("sys.indexes") - freshIndexes + statistics,
            Rows("sys.index_columns") - freshIndexColumns + statisticColumns,
            rowsets,
            units - freshUnits,
            rowsetColumns,
            types,
        ];
    }

    /// <summary>The queue of <paramref name="database"/> with <paramref name="objectId"/>, shipped or created, if any.</summary>
    internal static ServiceQueue? QueueOf(Database database, int objectId)
    {
        foreach (var queue in database.ServiceBroker.AllQueues(database))
        {
            if (queue.ObjectId == objectId)
                return queue;
        }
        return null;
    }

    /// <summary>The queue <paramref name="database"/> ships with <paramref name="objectId"/>, if any.</summary>
    internal static ServiceQueue? ShippedQueue(Database database, int objectId)
    {
        foreach (var queue in database.ServiceBroker.SystemQueues)
        {
            if (queue.ObjectId == objectId)
                return queue;
        }
        return null;
    }

    /// <summary><c>OBJECTPROPERTYEX(…, 'Cardinality')</c> of a system table: the rows of its heap or clustered index.</summary>
    internal static long SystemTableCardinality(Parser.BatchContext batch, Database database, SystemTableObject table)
    {
        foreach (var rowset in table.Rowsets)
        {
            if (rowset.IndexId <= 1)
                return SystemRowsetRows(batch, database, table, rowset);
        }
        return 0;
    }

    /// <summary>The <c>last_value</c> a system table's identity column reports: null before it hands one out.</summary>
    private static long? SystemIdentityLastValue(Database database, int objectId, int columnId)
    {
        if (!TryResolveSystemTable(database, objectId, out var table))
            return null;
        foreach (var (column, value) in table.IdentityLastValues)
        {
            if (column == columnId)
                return value;
        }
        return null;
    }

    /// <summary>The system table of <paramref name="database"/> with <paramref name="objectId"/>, if any.</summary>
    internal static bool TryResolveSystemTable(Database database, int objectId, out SystemTableObject table)
    {
        foreach (var candidate in SystemTableObjects(database))
        {
            if (candidate.ObjectId == objectId)
            {
                table = candidate;
                return true;
            }
        }
        table = null!;
        return false;
    }

    /// <summary>The system table of <paramref name="database"/> named <paramref name="name"/> (the <c>sys</c> schema's), if any.</summary>
    internal static bool TryResolveSystemTable(Database database, string name, out SystemTableObject table)
    {
        foreach (var candidate in SystemTableObjects(database))
        {
            if (Collation.Catalog.Equals(candidate.Name, name))
            {
                table = candidate;
                return true;
            }
        }
        table = null!;
        return false;
    }

    /// <summary>
    /// The <c>sys.objects</c> rows of <paramref name="database"/>'s system
    /// tables, then its shipped queues', which real lists in <c>dbo</c> beside
    /// the queues <c>CREATE QUEUE</c> makes (probed 2026-10-16 against SQL
    /// Server 2025). A table made with its database dates from it.
    /// </summary>
    private static IEnumerable<SqlValue[]> SystemTableObjectRows(Database database, SqlValue msShipped, SqlValue notPublished)
    {
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var sysSchema = SqlValue.FromInt32(Database.SysSchemaId);
        var databaseDate = SqlValue.FromDateTime(SysDatabasesCreateDate);
        foreach (var table in SystemTableObjects(database))
        {
            yield return [
                SqlValue.FromInt32(table.ObjectId),
                SqlValue.FromSystemName(table.Name),
                sysSchema,
                SqlValue.FromInt32(table.ParentObjectId),
                nullPrincipal,
                SqlValue.FromChar(charTwo, table.TypeCode),
                SqlValue.FromNVarchar(table.TypeDescription),
                table.CreateDate is { } created ? SqlValue.FromDateTime(created) : databaseDate,
                table.ModifyDate is { } modified ? SqlValue.FromDateTime(modified) : databaseDate,
                msShipped,
                notPublished,
                notPublished,
            ];
        }
        var queueType = SqlValue.FromChar(charTwo, "SQ");
        var queueTypeDesc = SqlValue.FromNVarchar("SERVICE_QUEUE");
        var zero = SqlValue.FromInt32(0);
        foreach (var queue in database.ServiceBroker.SystemQueues)
        {
            yield return [
                SqlValue.FromInt32(queue.ObjectId),
                SqlValue.FromSystemName(queue.Name),
                SqlValue.FromInt32(queue.SchemaId),
                zero,
                nullPrincipal,
                queueType,
                queueTypeDesc,
                SqlValue.FromDateTime(queue.CreateDate),
                SqlValue.FromDateTime(queue.ModifyDate),
                msShipped,
                notPublished,
                notPublished,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.internal_tables</c>: each internal table, which the view
    /// reports as not shipped though <c>sys.objects</c> says it is, its parent
    /// in <c>parent_id</c> too (probed 2026-10-16 against SQL Server 2025);
    /// then each primary XML index's node table.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInternalTables(Parser.BatchContext batch, Database database)
    {
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var sysSchema = SqlValue.FromInt32(Database.SysSchemaId);
        var typeCode = SqlValue.FromChar(charTwo, "IT");
        var typeDesc = SqlValue.FromNVarchar("INTERNAL_TABLE");
        var falseBit = SqlValue.FromBoolean(false);
        var zero = SqlValue.FromInt32(0);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var databaseDate = SqlValue.FromDateTime(SysDatabasesCreateDate);
        SqlValue[] Row(int objectId, string name, int parentId, SqlValue created, SqlValue modified, byte internalType, int parentMinorId, int lobDataSpaceId) =>
        [
            SqlValue.FromSystemName(name),
            SqlValue.FromInt32(objectId),
            nullPrincipal,
            sysSchema,
            SqlValue.FromInt32(parentId),
            typeCode,
            typeDesc,
            created,
            modified,
            falseBit,
            falseBit,
            falseBit,
            SqlValue.FromByte(internalType),
            SqlValue.FromString(nvarchar60Catalog, SystemTables.InternalTypeDescription(internalType)),
            SqlValue.FromInt32(parentId),
            parentMinorId == 0 ? zero : SqlValue.FromInt32(parentMinorId),
            SqlValue.FromInt32(lobDataSpaceId),
            nullInt,
        ];
        foreach (var table in SystemTableObjects(database))
        {
            if (!table.IsInternal)
                continue;
            yield return Row(table.ObjectId, table.Name, table.ParentObjectId,
                table.CreateDate is { } created ? SqlValue.FromDateTime(created) : databaseDate,
                table.ModifyDate is { } modified ? SqlValue.FromDateTime(modified) : databaseDate,
                table.InternalType, 0, table.LobDataSpaceId);
        }
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var t in CatalogTables(schema, batch).OrderBy(static t => t.ObjectId))
            {
                foreach (var xmlIndex in t.XmlIndexes)
                {
                    if (xmlIndex.IsPrimary)
                    {
                        yield return Row(xmlIndex.InternalTableObjectId, $"xml_index_nodes_{t.ObjectId}_{xmlIndex.IndexId}", t.ObjectId,
                            SqlValue.FromDateTime(t.CreateDate), SqlValue.FromDateTime(t.ModifyDate), 202, xmlIndex.IndexId, Database.PrimaryFilegroupId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.system_internals_partitions</c>: each <c>sys.partitions</c>
    /// row with real's row-format figures. A system table's are real's own; a
    /// user rowset's are computed from its columns as real lays its records out
    /// (probed 2026-10-16 against SQL Server 2025): a data record's 4-byte
    /// header, an index record's 1-byte one, the fixed-width columns (bits packed
    /// eight to a byte), the column count and null bitmap, and the
    /// variable-width columns' count, offsets and widest values, a
    /// nonclustered index's row locator and a non-unique clustered index's
    /// 4-byte uniquifier among them.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSystemInternalsPartitions(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroSmall = SqlValue.FromInt16(0);
        var zeroTiny = SqlValue.FromByte(0);
        var ownerType = SqlValue.FromByte(1);
        var noGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        SqlValue Bit(bool value) => value ? trueBit : falseBit;
        SqlValue[] Row(long partitionId, int objectId, int indexId, int partitionNumber, long rows, byte droppedLob, bool unique, RowsetLengths lengths,
            bool nullableKeys, bool rowLocks, bool pageLocks, bool dataRowFormat, bool notVersioned, bool columnstore, bool sequentialKey) =>
        [
            SqlValue.FromInt64(partitionId),
            SqlValue.FromInt32(objectId),
            SqlValue.FromInt32(indexId),
            SqlValue.FromInt32(partitionNumber),
            SqlValue.FromInt64(rows),
            zeroSmall,
            falseBit,
            SqlValue.FromByte(droppedLob),
            Bit(unique),
            falseBit,
            falseBit,
            falseBit,
            SqlValue.FromInt16(lengths.NullBits),
            SqlValue.FromInt32(lengths.MaxLeaf),
            SqlValue.FromInt16(lengths.MinLeaf),
            SqlValue.FromInt16(lengths.MaxInternal),
            SqlValue.FromInt16(lengths.MinInternal),
            Bit(nullableKeys),
            Bit(rowLocks),
            Bit(pageLocks),
            Bit(dataRowFormat),
            Bit(notVersioned),
            noGuid,
            ownerType,
            Bit(columnstore),
            Bit(sequentialKey),
        ];

        var census = new PartitionCensus();
        foreach (var (table, identity, placement) in EnumerateStorageIdentities(database, batch))
        {
            var lengths = RealRowsetLengths(table, identity);
            var unique = identity.Constraint is not null || identity.Index is { IsUnique: true };
            var (rowLocks, pageLocks, sequentialKey) = identity switch
            {
                { Constraint: { } key } => (key.AllowRowLocks, key.AllowPageLocks, key.OptimizeForSequentialKey),
                { Index.IsColumnstore: true } => (false, false, false),
                { Index: { } index } => (index.AllowRowLocks, index.AllowPageLocks, index.OptimizeForSequentialKey),
                _ => (table.HeapAllowRowLocks, table.HeapAllowPageLocks, false),
            };
            foreach (var unit in census.Units(table, identity.IndexId, placement))
            {
                var rows = identity.Index is { Filter: not null } ? FilteredRows(batch, table, identity.IndexId, placement, unit.Number) : unit.Rows;
                yield return Row(unit.PartitionId, table.ObjectId, identity.IndexId, unit.Number, rows, 2, unique, lengths,
                    !identity.IsHeap, rowLocks, pageLocks, identity.Type is 0 or 1, false, identity.Index is { IsColumnstore: true }, sequentialKey);
            }
        }
        foreach (var systemTable in SystemTableObjects(database))
        {
            foreach (var rowset in systemTable.Rowsets)
            {
                var index = Array.Find(systemTable.Definition.Indexes, candidate => candidate.Id == rowset.IndexId);
                yield return Row(rowset.PartitionId, systemTable.ObjectId, rowset.IndexId, 1, SystemRowsetRows(batch, database, systemTable, rowset), rowset.DroppedLobColumnState,
                    index is { Unique: true }, new RowsetLengths(rowset.MaxNullBitUsed, rowset.MaxLeafLength, rowset.MinLeafLength, rowset.MaxInternalLength, rowset.MinInternalLength),
                    rowset.AllowsNullableKeys, true, index is not { AllowPageLocks: false }, rowset.IsDataRowFormat, rowset.IsNotVersioned, false, false);
            }
        }
    }

    /// <summary>The row-format figures of one rowset, as <c>sys.system_internals_partitions</c> reports them.</summary>
    private readonly struct RowsetLengths(short nullBits, int maxLeaf, short minLeaf, short maxInternal, short minInternal)
    {
        public readonly short NullBits = nullBits;
        public readonly int MaxLeaf = maxLeaf;
        public readonly short MinLeaf = minLeaf;
        public readonly short MaxInternal = maxInternal;
        public readonly short MinInternal = minInternal;
    }

    /// <summary>
    /// The record lengths real computes for one of <paramref name="table"/>'s
    /// rowsets: a heap or clustered index's leaf holds every stored column, a
    /// nonclustered index's its keys, included columns and row locator (the
    /// clustered keys, or an 8-byte RID over a heap), and an index's upper
    /// levels its keys — a non-unique nonclustered index's with the locator —
    /// and a 6-byte child page pointer; a non-unique clustered key carries a
    /// 4-byte variable uniquifier. An upper-level record is never reported
    /// shorter than 15 bytes at its longest (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private static RowsetLengths RealRowsetLengths(HeapTable table, IndexIdentity identity)
    {
        IndexIdentity? clustered = null;
        foreach (var candidate in table.IndexIdentities())
        {
            if (candidate.Type == 1)
                clustered = candidate;
        }
        int[] KeyOrdinals(IndexIdentity rowset) => rowset.Constraint is { } key ? key.FullOrdinals : rowset.Index?.KeyFullOrdinals ?? [];
        bool Unique(IndexIdentity rowset) => rowset.Constraint is not null || rowset.Index is { IsUnique: true };
        var leaf = new List<int>();
        var leafUniquifier = false;
        var leafLocator = false;
        var upper = new List<int>();
        var upperUniquifier = false;
        var upperLocator = false;
        void AddDistinct(List<int> into, int[] ordinals)
        {
            foreach (var ordinal in ordinals)
            {
                if (!into.Contains(ordinal))
                    into.Add(ordinal);
            }
        }
        if (identity.Type is 0 or 1 or 5)
        {
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (table.Columns[i].IsStored)
                    leaf.Add(i);
            }
            leafUniquifier = identity.Type == 1 && !Unique(identity);
            if (identity.Type == 1)
            {
                AddDistinct(upper, KeyOrdinals(identity));
                upperUniquifier = leafUniquifier;
            }
        }
        else
        {
            AddDistinct(leaf, KeyOrdinals(identity));
            AddDistinct(upper, KeyOrdinals(identity));
            if (identity.Index is { } index)
                AddDistinct(leaf, index.IncludedColumnOrdinals);
            if (clustered is { } cluster)
            {
                AddDistinct(leaf, KeyOrdinals(cluster));
                leafUniquifier = !Unique(cluster);
                if (!Unique(identity))
                {
                    AddDistinct(upper, KeyOrdinals(cluster));
                    upperUniquifier = leafUniquifier;
                }
            }
            else
            {
                leafLocator = true;
                upperLocator = !Unique(identity);
            }
        }
        var (leafMax, leafMin, leafBits) = RecordLength(table.Columns, [.. leaf], leafUniquifier, leafLocator, identity.Type is 0 or 1 or 5 ? 4 : 1);
        if (identity.IsHeap)
            return new RowsetLengths((short)leafBits, leafMax, (short)leafMin, 0, 0);
        var (upperMax, upperMin, _) = RecordLength(table.Columns, [.. upper], upperUniquifier, upperLocator, 7);
        return new RowsetLengths((short)leafBits, leafMax, (short)leafMin, (short)Math.Max(15, upperMax), (short)upperMin);
    }

    /// <summary>
    /// One record's longest and shortest lengths and its column count:
    /// <paramref name="header"/> bytes, then the fixed-width part, then the
    /// column count, the null bitmap and — when any column varies — the
    /// variable-width count, offsets and widest values.
    /// </summary>
    private static (int Max, int Min, int Columns) RecordLength(HeapColumn[] tableColumns, int[] ordinals, bool uniquifier, bool locator, int header)
    {
        var fixedWidth = 0;
        var bits = 0;
        var variableCount = 0;
        var variableWidth = 0;
        foreach (var ordinal in ordinals)
        {
            var column = tableColumns[ordinal];
            if (column.Type == SqlType.Bit)
            {
                bits++;
                continue;
            }
            var (maxLength, _, _) = GetSysColumnMetadata(column);
            if (column.Type.IsFixedLength)
            {
                fixedWidth += maxLength;
            }
            else
            {
                variableCount++;
                variableWidth += maxLength < 0 ? 8000 : maxLength;
            }
        }
        fixedWidth += ((bits + 7) / 8) + (locator ? 8 : 0);
        if (uniquifier)
        {
            variableCount++;
            variableWidth += 4;
        }
        var columns = ordinals.Length + (uniquifier ? 1 : 0) + (locator ? 1 : 0);
        var min = header + fixedWidth;
        var max = min + 2 + ((columns + 7) / 8) + (variableCount > 0 ? 2 + (2 * variableCount) + variableWidth : 0);
        return (max, min, columns);
    }

    /// <summary>
    /// Rows for <c>sys.system_internals_allocation_units</c>: each
    /// <c>sys.allocation_units</c> row with its filegroup and the addresses of
    /// its first page, root page and first IAM page — file 1's pages numbered
    /// as <c>sys.dm_os_buffer_descriptors</c> numbers them, the IAM page first,
    /// and zero for a unit that holds none.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSystemInternalsAllocationUnits(Parser.BatchContext batch, Database database)
    {
        var inRowDesc = SqlValue.FromNVarchar("IN_ROW_DATA");
        var lobDesc = SqlValue.FromNVarchar("LOB_DATA");
        var overflowDesc = SqlValue.FromNVarchar("ROW_OVERFLOW_DATA");
        var none = SqlValue.FromBinary(BinarySqlType.Get(6), new byte[6]);
        static SqlValue Address(long page)
        {
            var bytes = new byte[6];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)page);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), 1);
            return SqlValue.FromBinary(BinarySqlType.Get(6), bytes);
        }
        long pageId = FirstBufferedPageId;
        foreach (var (containerId, allocationUnitId, indexId, type, totalPages, usedPages, dataPages, dataSpaceId) in EnumerateAllocationUnitData(batch, database))
        {
            var first = usedPages > 1 ? Address(pageId + 1) : none;
            var root = usedPages <= 1 || type != 1 || indexId == 0 ? none : Address(pageId + usedPages - 1);
            yield return
            [
                SqlValue.FromInt64(allocationUnitId),
                SqlValue.FromByte(type),
                type switch { 2 => lobDesc, 3 => overflowDesc, _ => inRowDesc },
                SqlValue.FromInt64(containerId),
                SqlValue.FromInt16((short)dataSpaceId),
                SqlValue.FromInt64(totalPages),
                SqlValue.FromInt64(usedPages),
                SqlValue.FromInt64(dataPages),
                first,
                root,
                usedPages > 0 ? Address(pageId) : none,
            ];
            pageId += usedPages;
        }
    }
}
