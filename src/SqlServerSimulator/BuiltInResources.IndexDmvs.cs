using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The index and statistics DMVs and the sysindexes / sysindexkeys
// compatibility views — shapes probed 2026-10-05 against SQL Server 2025.
internal static partial class BuiltInResources
{
    private static readonly NVarcharSqlType Nvarchar4000Catalog = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);

    /// <summary>
    /// Registers the index DMVs that are views — usage and the missing-index
    /// family, which describe the optimizer's plans and run-time counters the
    /// simulator doesn't keep, so they list nothing — and the two legacy
    /// compatibility views over the indexes and statistics.
    /// </summary>
    private static void RegisterIndexDmvs(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        static IEnumerable<SqlValue[]> None(Parser.BatchContext batch, Database database) => [];

        Sys("dm_db_index_usage_stats",
        [
            new("database_id", SqlType.SmallInt, null, false),
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("user_seeks", SqlType.BigInt, null, false),
            new("user_scans", SqlType.BigInt, null, false),
            new("user_lookups", SqlType.BigInt, null, false),
            new("user_updates", SqlType.BigInt, null, false),
            new("last_user_seek", SqlType.DateTime, null, true),
            new("last_user_scan", SqlType.DateTime, null, true),
            new("last_user_lookup", SqlType.DateTime, null, true),
            new("last_user_update", SqlType.DateTime, null, true),
            new("system_seeks", SqlType.BigInt, null, false),
            new("system_scans", SqlType.BigInt, null, false),
            new("system_lookups", SqlType.BigInt, null, false),
            new("system_updates", SqlType.BigInt, null, false),
            new("last_system_seek", SqlType.DateTime, null, true),
            new("last_system_scan", SqlType.DateTime, null, true),
            new("last_system_lookup", SqlType.DateTime, null, true),
            new("last_system_update", SqlType.DateTime, null, true),
        ], None);

        Sys("dm_db_missing_index_details",
        [
            new("index_handle", SqlType.Int32, null, false),
            new("database_id", SqlType.SmallInt, null, false),
            new("object_id", SqlType.Int32, null, false),
            new("equality_columns", Nvarchar4000Catalog, 4000, true),
            new("inequality_columns", Nvarchar4000Catalog, 4000, true),
            new("included_columns", Nvarchar4000Catalog, 4000, true),
            new("statement", Nvarchar4000Catalog, 4000, true),
        ], None);

        Sys("dm_db_missing_index_groups",
        [
            new("index_group_handle", SqlType.Int32, null, false),
            new("index_handle", SqlType.Int32, null, false),
        ], None);

        Sys("dm_db_missing_index_group_stats",
        [
            new("group_handle", SqlType.Int32, null, false),
            new("unique_compiles", SqlType.BigInt, null, true),
            new("user_seeks", SqlType.BigInt, null, true),
            new("user_scans", SqlType.BigInt, null, true),
            new("last_user_seek", SqlType.DateTime, null, true),
            new("last_user_scan", SqlType.DateTime, null, true),
            new("avg_total_user_cost", SqlType.Float, null, true),
            new("avg_user_impact", SqlType.Float, null, true),
            new("system_seeks", SqlType.BigInt, null, true),
            new("system_scans", SqlType.BigInt, null, true),
            new("last_system_seek", SqlType.DateTime, null, true),
            new("last_system_scan", SqlType.DateTime, null, true),
            new("avg_total_system_cost", SqlType.Float, null, true),
            new("avg_system_impact", SqlType.Float, null, true),
        ], None);

        Sys("dm_db_missing_index_group_stats_query",
        [
            new("group_handle", SqlType.Int32, null, false),
            new("query_hash", BinarySqlType.Get(8), 8, false),
            new("query_plan_hash", BinarySqlType.Get(8), 8, false),
            new("last_sql_handle", VarbinarySqlType.Get(64), 64, false),
            new("last_statement_start_offset", SqlType.Int32, null, false),
            new("last_statement_end_offset", SqlType.Int32, null, false),
            new("last_statement_sql_handle", VarbinarySqlType.Get(64), 64, true),
            new("user_seeks", SqlType.BigInt, null, false),
            new("user_scans", SqlType.BigInt, null, false),
            new("last_user_seek", SqlType.DateTime, null, true),
            new("last_user_scan", SqlType.DateTime, null, true),
            new("avg_total_user_cost", SqlType.Float, null, true),
            new("avg_user_impact", SqlType.Float, null, false),
            new("system_seeks", SqlType.BigInt, null, false),
            new("system_scans", SqlType.BigInt, null, false),
            new("last_system_seek", SqlType.DateTime, null, true),
            new("last_system_scan", SqlType.DateTime, null, true),
            new("avg_total_system_cost", SqlType.Float, null, true),
            new("avg_system_impact", SqlType.Float, null, false),
        ], None);

        HeapColumn[] sysindexes =
        [
            new("id", SqlType.Int32, null, false),
            new("status", SqlType.Int32, null, true),
            new("first", BinarySqlType.Get(6), 6, true),
            new("indid", SqlType.SmallInt, null, true),
            new("root", BinarySqlType.Get(6), 6, true),
            new("minlen", SqlType.SmallInt, null, true),
            new("keycnt", SqlType.SmallInt, null, true),
            new("groupid", SqlType.SmallInt, null, true),
            new("dpages", SqlType.Int32, null, true),
            new("reserved", SqlType.Int32, null, true),
            new("used", SqlType.Int32, null, true),
            new("rowcnt", SqlType.BigInt, null, true),
            new("rowmodctr", SqlType.Int32, null, true),
            new("reserved3", SqlType.TinyInt, null, true),
            new("reserved4", SqlType.TinyInt, null, true),
            new("xmaxlen", SqlType.SmallInt, null, true),
            new("maxirow", SqlType.SmallInt, null, true),
            new("OrigFillFactor", SqlType.TinyInt, null, true),
            new("StatVersion", SqlType.TinyInt, null, true),
            new("reserved2", SqlType.Int32, null, true),
            new("FirstIAM", BinarySqlType.Get(6), 6, true),
            new("impid", SqlType.SmallInt, null, true),
            new("lockflags", SqlType.SmallInt, null, true),
            new("pgmodctr", SqlType.Int32, null, true),
            new("keys", VarbinarySqlType.Get(1088), 1088, true),
            new("name", SqlType.SystemName, 128, true),
            new("statblob", SqlType.Image, null, true),
            new("maxlen", SqlType.Int32, null, true),
            new("rows", SqlType.Int32, null, true),
        ];
        var sysindexesView = new CatalogView("sysindexes", sysindexes, static (batch, database) => EnumerateSysindexes(batch, database));
        views["sysindexes"] = sysindexesView;
        views["sys.sysindexes"] = sysindexesView;

        HeapColumn[] sysindexkeys =
        [
            new("id", SqlType.Int32, null, false),
            new("indid", SqlType.SmallInt, null, true),
            new("colid", SqlType.SmallInt, null, true),
            new("keyno", SqlType.SmallInt, null, true),
        ];
        var sysindexkeysView = new CatalogView("sysindexkeys", sysindexkeys, static (batch, database) => EnumerateSysindexkeys(batch, database));
        views["sysindexkeys"] = sysindexkeysView;
        views["sys.sysindexkeys"] = sysindexkeysView;
    }

    /// <summary>
    /// Rows for <c>sysindexes</c>: each table's heap or clustered row, its other
    /// indexes and its standalone statistics, in id order. <c>status</c>
    /// carries real's bits — 2 unique, 16 clustered, 2048 primary key, 4096
    /// unique constraint, 64 a standalone statistic, 0x800000 one the optimizer
    /// created; <c>keycnt</c> counts a nonclustered index's clustered-key
    /// columns as its own; <c>rowmodctr</c> is the leading column's
    /// modification count since the statistic was built. The physical
    /// columns read the simulator's own pages.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysindexes(Parser.BatchContext batch, Database database)
    {
        var nullBinary = SqlValue.Null(BinarySqlType.Get(6));
        var nullKeys = SqlValue.Null(VarbinarySqlType.Get(1088));
        var nullBlob = SqlValue.Null(SqlType.Image);
        var zeroTiny = SqlValue.FromByte(0);
        var zeroSmall = SqlValue.FromInt16(0);
        var zero = SqlValue.FromInt32(0);
        var maxlen = SqlValue.FromInt32(8000);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var table in CatalogTables(schema, batch))
            {
                var pages = table.Heap.Pages.Count;
                var rows = table.Heap.CountLiveRows();
                var statistics = Simulation.StatisticsOn(table);
                foreach (var identity in table.IndexIdentities())
                {
                    if (identity.Index is { IsColumnstore: true })
                        continue;
                    var status = identity switch
                    {
                        { Constraint: { } key } => 2 | (key.IsClustered ? 16 : 0) | (key.Kind == KeyConstraintKind.PrimaryKey ? 2048 : 4096),
                        { Index: { } index } => (index.IsUnique ? 2 : 0) | (index.IsClustered ? 16 : 0),
                        _ => 0,
                    };
                    var statistic = statistics.Find(candidate => candidate.StatsId == identity.IndexId && candidate.User is null);
                    var keyCount = identity.IsHeap ? 0 : statistic.DensityOrdinals?.Length ?? 0;
                    var fillFactor = identity.Constraint?.FillFactor ?? identity.Index?.FillFactor ?? 0;
                    yield return SysindexesRow(table, identity.IndexId, status, keyCount, 1, pages, rows,
                        statistic.State is null ? 0 : ModificationsSince(table, statistic), fillFactor, identity.Name);
                }
                foreach (var statistic in statistics)
                {
                    if (statistic.User is not { } user)
                        continue;
                    yield return SysindexesRow(table, statistic.StatsId, user.AutoCreated ? 0x800040 : 64, statistic.KeyOrdinals.Length, 0, 0, 0,
                        ModificationsSince(table, statistic), 0, statistic.Name);
                }
            }
        }

        SqlValue[] SysindexesRow(HeapTable table, int indid, int status, int keyCount, short groupId, int pages, long rows, long modifications, byte fillFactor, string? name) =>
        [
            SqlValue.FromInt32(table.ObjectId),
            SqlValue.FromInt32(status),
            nullBinary,
            SqlValue.FromInt16((short)indid),
            nullBinary,
            zeroSmall,
            SqlValue.FromInt16((short)keyCount),
            SqlValue.FromInt16(groupId),
            SqlValue.FromInt32(pages),
            SqlValue.FromInt32(pages),
            SqlValue.FromInt32(pages),
            SqlValue.FromInt64(rows),
            SqlValue.FromInt32((int)Math.Min(int.MaxValue, modifications)),
            zeroTiny,
            zeroTiny,
            zeroSmall,
            zeroSmall,
            SqlValue.FromByte(fillFactor),
            zeroTiny,
            zero,
            nullBinary,
            zeroSmall,
            zeroSmall,
            zero,
            nullKeys,
            name is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(name),
            nullBlob,
            maxlen,
            SqlValue.FromInt32((int)Math.Min(int.MaxValue, rows)),
        ];
    }

    /// <summary>The modifications of a statistic's leading column since it was built — all of them when it has no snapshot.</summary>
    internal static long ModificationsSince(HeapTable table, Simulation.TableStatistic statistic) =>
        statistic.State.Snapshot is { } snapshot
            ? Math.Max(0, table.ModificationCount(statistic.LeadingOrdinal) - snapshot.ModificationBase)
            : table.ModificationCount(statistic.LeadingOrdinal);

    /// <summary>
    /// Rows for <c>sysindexkeys</c>: each index's key columns numbered from 1
    /// and its included columns at 0, neither its clustered-key columns nor
    /// any statistic's.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysindexkeys(Parser.BatchContext batch, Database database)
    {
        var zero = SqlValue.FromInt16(0);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var table in CatalogTables(schema, batch))
            {
                foreach (var identity in table.IndexIdentities())
                {
                    var (keys, includes) = identity switch
                    {
                        { Constraint: { } key } => (key.FullOrdinals, []),
                        { Index: { IsColumnstore: false } index } => (index.KeyFullOrdinals, index.IncludedColumnOrdinals),
                        _ => ([], []),
                    };
                    var indid = SqlValue.FromInt16((short)identity.IndexId);
                    var objectId = SqlValue.FromInt32(table.ObjectId);
                    foreach (var ordinal in includes)
                        yield return [objectId, indid, SqlValue.FromInt16((short)table.Columns[ordinal].ColumnId), zero];
                    for (var i = 0; i < keys.Length; i++)
                        yield return [objectId, indid, SqlValue.FromInt16((short)table.Columns[keys[i]].ColumnId), SqlValue.FromInt16((short)(i + 1))];
                }
            }
        }
    }

    /// <summary>
    /// <c>sys.dm_db_index_physical_stats(database_id, object_id, index_id,
    /// partition_number, mode)</c>'s rows: one per partition of each index
    /// holding storage, in-row pages only, the leaf level alone (the
    /// simulator keeps no B-tree above it). The mode is <c>LIMITED</c>,
    /// <c>SAMPLED</c>, <c>DETAILED</c>, <c>DEFAULT</c> or NULL, else Msg 2561;
    /// an unknown database is Msg 2521 and an index the table lacks Msg 2591.
    /// <c>LIMITED</c> leaves the record figures NULL.
    /// </summary>
    internal static List<SqlValue[]> IndexPhysicalStats(Parser.BatchContext batch, int? databaseId, int? objectId, int? indexId, int? partitionNumber, string? mode)
    {
        var limited = mode is null || BuiltInToken.Equals(mode, "LIMITED") || BuiltInToken.Equals(mode, "DEFAULT");
        if (!limited && !BuiltInToken.Equals(mode, "SAMPLED") && !BuiltInToken.Equals(mode, "DETAILED"))
            throw SimulatedSqlException.ParameterIncorrectForStatement(5);
        var simulation = batch.Connection.Simulation;
        if (databaseId is { } named && named > 0 && !Parser.Expressions.DbId.DatabasesWithIds(simulation).Any(entry => entry.Id == named))
            throw SimulatedSqlException.CouldNotFindDatabaseId((ushort)named, state: 40);
        if (databaseId is < 0)
            throw SimulatedSqlException.CouldNotFindDatabaseId((ushort)(short)databaseId.Value, state: 40);
        var rows = new List<SqlValue[]>();
        foreach (var (database, id) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
        {
            if (databaseId is { } wanted && wanted > 0 && wanted != id)
                continue;
            if (databaseId is null or 0 && database != batch.CurrentDatabase)
                continue;
            foreach (var (_, schema) in database.Schemas)
            {
                foreach (var table in CatalogTables(schema, batch))
                {
                    if (objectId is { } wantedObject && wantedObject > 0 && wantedObject != table.ObjectId)
                        continue;
                    var identities = table.IndexIdentities();
                    if (indexId is { } wantedIndex && wantedIndex >= 0 && !identities.Exists(identity => identity.IndexId == wantedIndex))
                        throw SimulatedSqlException.IndexIdNotInCatalog(wantedIndex, table.Name);
                    var census = new PartitionCensus();
                    foreach (var identity in identities)
                    {
                        if (!HoldsStorage(identity) || (indexId is { } only && only >= 0 && only != identity.IndexId))
                            continue;
                        var typeDesc = identity.Type switch
                        {
                            0 => "HEAP",
                            1 => "CLUSTERED INDEX",
                            2 => "NONCLUSTERED INDEX",
                            5 => "CLUSTERED COLUMNSTORE INDEX",
                            6 => "NONCLUSTERED COLUMNSTORE INDEX",
                            _ => "NONCLUSTERED INDEX",
                        };
                        foreach (var unit in census.Units(table, identity.IndexId, Simulation.PlacementOf(table, identity)))
                        {
                            if (partitionNumber is { } wantedPartition && wantedPartition > 0 && wantedPartition != unit.Number)
                                continue;
                            var nullFloat = SqlValue.Null(SqlType.Float);
                            var nullBig = SqlValue.Null(SqlType.BigInt);
                            var nullInt = SqlValue.Null(SqlType.Int32);
                            rows.Add(
                            [
                                SqlValue.FromInt16(id),
                                SqlValue.FromInt32(table.ObjectId),
                                SqlValue.FromInt32(identity.IndexId),
                                SqlValue.FromInt32(unit.Number),
                                SqlValue.FromString(nvarchar60Catalog, typeDesc),
                                SqlValue.FromString(nvarchar60Catalog, "IN_ROW_DATA"),
                                SqlValue.FromByte(unit.InRowPages == 0 ? (byte)0 : (byte)1),
                                SqlValue.FromByte(0),
                                SqlValue.FromDouble(0),
                                SqlValue.FromInt64(unit.InRowPages == 0 ? 0 : 1),
                                SqlValue.FromDouble(unit.InRowPages),
                                SqlValue.FromInt64(unit.InRowPages),
                                limited ? nullFloat : SqlValue.FromDouble(0),
                                limited ? nullBig : SqlValue.FromInt64(unit.Rows),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullInt : SqlValue.FromInt32(0),
                                limited ? nullInt : SqlValue.FromInt32(0),
                                limited ? nullFloat : SqlValue.FromDouble(0),
                                limited || identity.Type != 0 ? nullBig : SqlValue.FromInt64(0),
                                SqlValue.FromInt64(0),
                                SqlValue.FromInt64(unit.PartitionId),
                                SqlValue.FromByte(0),
                                SqlValue.FromString(nvarchar60Catalog, "NOT VALID"),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullBig : SqlValue.FromInt64(0),
                                limited ? nullBig : SqlValue.FromInt64(0),
                            ]);
                        }
                    }
                }
            }
        }
        return rows;
    }

    /// <summary>The columns of <see cref="IndexPhysicalStats"/>' rows.</summary>
    internal static readonly (string Name, SqlType Type)[] IndexPhysicalStatsColumns =
    [
        ("database_id", SqlType.SmallInt), ("object_id", SqlType.Int32), ("index_id", SqlType.Int32), ("partition_number", SqlType.Int32),
        ("index_type_desc", nvarchar60Catalog), ("alloc_unit_type_desc", nvarchar60Catalog), ("index_depth", SqlType.TinyInt), ("index_level", SqlType.TinyInt),
        ("avg_fragmentation_in_percent", SqlType.Float), ("fragment_count", SqlType.BigInt), ("avg_fragment_size_in_pages", SqlType.Float),
        ("page_count", SqlType.BigInt), ("avg_page_space_used_in_percent", SqlType.Float), ("record_count", SqlType.BigInt),
        ("ghost_record_count", SqlType.BigInt), ("version_ghost_record_count", SqlType.BigInt), ("min_record_size_in_bytes", SqlType.Int32),
        ("max_record_size_in_bytes", SqlType.Int32), ("avg_record_size_in_bytes", SqlType.Float), ("forwarded_record_count", SqlType.BigInt),
        ("compressed_page_count", SqlType.BigInt), ("hobt_id", SqlType.BigInt), ("columnstore_delete_buffer_state", SqlType.TinyInt),
        ("columnstore_delete_buffer_state_desc", nvarchar60Catalog), ("version_record_count", SqlType.BigInt), ("inrow_version_record_count", SqlType.BigInt),
        ("inrow_diff_version_record_count", SqlType.BigInt), ("total_inrow_version_payload_size_in_bytes", SqlType.BigInt),
        ("offrow_regular_version_record_count", SqlType.BigInt), ("offrow_long_term_version_record_count", SqlType.BigInt),
    ];

    /// <summary>
    /// <c>sys.dm_db_stats_properties(object_id, stats_id)</c>'s one row: what
    /// the statistic was last built over and the modifications of its leading
    /// column since — every figure NULL for one never built over rows; no row
    /// for an unknown object or statistic.
    /// </summary>
    internal static List<SqlValue[]> StatsProperties(Parser.BatchContext batch, int? objectId, int? statsId)
    {
        if (StatisticFor(batch, objectId, statsId) is not var (table, statistic))
            return [];
        var snapshot = statistic.State.Snapshot;
        return
        [
            [
                SqlValue.FromInt32(table.ObjectId),
                SqlValue.FromInt32(statistic.StatsId),
                snapshot is null ? SqlValue.Null(SqlType.GetDateTime2(7)) : SqlValue.FromDateTime(snapshot.Updated).CoerceTo(SqlType.GetDateTime2(7)),
                snapshot is null ? SqlValue.Null(SqlType.BigInt) : SqlValue.FromInt64(snapshot.Rows),
                snapshot is null ? SqlValue.Null(SqlType.BigInt) : SqlValue.FromInt64(snapshot.Rows),
                snapshot is null ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(snapshot.Histogram.Length),
                snapshot is null ? SqlValue.Null(SqlType.BigInt) : SqlValue.FromInt64(snapshot.UnfilteredRows),
                snapshot is null ? SqlValue.Null(SqlType.BigInt) : SqlValue.FromInt64(ModificationsSince(table, statistic)),
                snapshot is null ? SqlValue.Null(SqlType.Float) : SqlValue.FromDouble(0),
            ],
        ];
    }

    /// <summary><c>sys.dm_db_stats_histogram(object_id, stats_id)</c>'s rows: the statistic's histogram steps, numbered from 1.</summary>
    internal static List<SqlValue[]> StatsHistogram(Parser.BatchContext batch, int? objectId, int? statsId)
    {
        if (StatisticFor(batch, objectId, statsId) is not var (table, statistic) || statistic.State.Snapshot is not { } snapshot)
            return [];
        var rows = new List<SqlValue[]>(snapshot.Histogram.Length);
        for (var i = 0; i < snapshot.Histogram.Length; i++)
        {
            var step = snapshot.Histogram[i];
            rows.Add(
            [
                SqlValue.FromInt32(table.ObjectId),
                SqlValue.FromInt32(statistic.StatsId),
                SqlValue.FromInt32(i + 1),
                step.RangeHighKey.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(step.RangeHighKey),
                SqlValue.FromSingle(step.RangeRows),
                SqlValue.FromSingle(step.EqualRows),
                SqlValue.FromInt64(step.DistinctRangeRows),
                SqlValue.FromSingle(step.AverageRangeRows),
            ]);
        }
        return rows;
    }

    private static (HeapTable Table, Simulation.TableStatistic Statistic)? StatisticFor(Parser.BatchContext batch, int? objectId, int? statsId)
    {
        if (objectId is not { } id || statsId is not { } wanted
            || Parser.Expressions.ObjectProperty.FindObject(batch.CurrentDatabase, id) is not HeapTable table)
        {
            return null;
        }
        foreach (var statistic in Simulation.StatisticsOn(table))
        {
            if (statistic.StatsId == wanted)
                return (table, statistic);
        }
        return null;
    }
}
