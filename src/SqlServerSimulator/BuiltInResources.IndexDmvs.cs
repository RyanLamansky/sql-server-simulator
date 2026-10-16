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
        ], static (batch, _) => IndexUsageRows(batch));

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
    /// modification count since the statistic was built. <c>dpages</c>,
    /// <c>reserved</c> and <c>used</c> are the rowset's leaf, reserved and
    /// used pages over its partitions (<see cref="StorageLayout"/>).
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
                    long data = 0, reserved = 0, used = 0;
                    if (HoldsStorage(identity))
                    {
                        foreach (var figures in StorageLayout.Of(batch, table, identity, Simulation.PlacementOf(table, identity)))
                        {
                            data += figures.InRowDataPages;
                            reserved += figures.ReservedPageCount;
                            used += figures.UsedPageCount;
                        }
                    }
                    yield return SysindexesRow(table, identity.IndexId, status, keyCount, 1, (data, reserved, used), rows,
                        statistic.State is null ? 0 : ModificationsSince(table, statistic), fillFactor, identity.Name);
                }
                foreach (var statistic in statistics)
                {
                    if (statistic.User is not { } user)
                        continue;
                    yield return SysindexesRow(table, statistic.StatsId, user.AutoCreated ? 0x800040 : 64, statistic.KeyOrdinals.Length, 0, (0, 0, 0), 0,
                        ModificationsSince(table, statistic), 0, statistic.Name);
                }
            }
        }

        SqlValue[] SysindexesRow(HeapTable table, int indid, int status, int keyCount, short groupId, (long Data, long Reserved, long Used) pages, long rows, long modifications, byte fillFactor, string? name) =>
        [
            SqlValue.FromInt32(table.ObjectId),
            SqlValue.FromInt32(status),
            nullBinary,
            SqlValue.FromInt16((short)indid),
            nullBinary,
            zeroSmall,
            SqlValue.FromInt16((short)keyCount),
            SqlValue.FromInt16(groupId),
            SqlValue.FromInt32((int)Math.Min(int.MaxValue, pages.Data)),
            SqlValue.FromInt32((int)Math.Min(int.MaxValue, pages.Reserved)),
            SqlValue.FromInt32((int)Math.Min(int.MaxValue, pages.Used)),
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
    /// partition_number, mode)</c>'s rows, from the pages
    /// <see cref="StorageLayout"/> lays each rowset out on: per index and
    /// partition its in-row unit — every level from the leaf up under
    /// <c>DETAILED</c>, the leaf alone otherwise — then its LOB and
    /// row-overflow units when they hold pages. The mode is <c>LIMITED</c>,
    /// <c>SAMPLED</c>, <c>DETAILED</c>, <c>DEFAULT</c> or NULL, else Msg 2561;
    /// an unknown database is Msg 2521 and an index the table lacks Msg 2591.
    /// <c>LIMITED</c> leaves the record figures NULL, and a heap's
    /// fragmentation reads 0 under <c>SAMPLED</c>, as real's do (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static List<SqlValue[]> IndexPhysicalStats(Parser.BatchContext batch, int? databaseId, int? objectId, int? indexId, int? partitionNumber, string? mode)
    {
        var limited = mode is null || BuiltInToken.Equals(mode, "LIMITED") || BuiltInToken.Equals(mode, "DEFAULT");
        var detailed = !limited && BuiltInToken.Equals(mode, "DETAILED");
        if (!limited && !detailed && !BuiltInToken.Equals(mode, "SAMPLED"))
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
                        var placement = Simulation.PlacementOf(table, identity);
                        var figures = StorageLayout.Of(batch, table, identity, placement);
                        foreach (var unit in census.Units(table, identity.IndexId, placement))
                        {
                            if (partitionNumber is { } wantedPartition && wantedPartition > 0 && wantedPartition != unit.Number)
                                continue;
                            PhysicalStatsRows(rows, id, table, identity, unit, figures[unit.Number - 1], limited, detailed);
                        }
                    }
                }
            }
        }
        return rows;
    }

    /// <summary>
    /// One rowset partition's <see cref="IndexPhysicalStats"/> rows. A heap's
    /// fragment size reads 0 under <c>SAMPLED</c>, a LOB or row-overflow unit
    /// reports no fragments, and <c>forwarded_record_count</c> is a heap's
    /// alone (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static void PhysicalStatsRows(List<SqlValue[]> rows, short databaseId, HeapTable table, IndexIdentity identity, PartitionCensus.Unit unit, RowsetFigures figures, bool limited, bool detailed)
    {
        // A columnstore index reports as the rowstore index of its kind.
        var typeDesc = identity.Type switch
        {
            0 => "HEAP",
            1 or 5 => "CLUSTERED INDEX",
            _ => "NONCLUSTERED INDEX",
        };
        var sampled = !limited && !detailed;
        var nullBig = SqlValue.Null(SqlType.BigInt);
        var depth = (byte)figures.Levels.Length;
        var levels = figures.Levels.Length == 0 ? [new LevelFigures()] : figures.Levels;
        var top = detailed ? levels.Length : 1;
        for (var l = 0; l < top; l++)
        {
            var level = levels[l];
            var heapSampled = figures.IsHeap && sampled;
            var fragmentation = heapSampled || level.Pages == 0 ? 0 : 100.0 * level.OutOfOrder / level.Pages;
            var fragmentSize = heapSampled || level.Fragments == 0 ? 0 : (double)level.Pages / level.Fragments;
            var records = limited ? (RecordFigures?)null : new RecordFigures(level.Bytes, level.Records, l == 0 ? level.Ghosts : 0, level.SizedRecords, level.RecordBytes, level.MinRecord, level.MaxRecord);
            rows.Add(PhysicalStatsRow(databaseId, table, identity, unit, typeDesc, "IN_ROW_DATA", depth, (byte)l,
                SqlValue.FromDouble(fragmentation), SqlValue.FromInt64(level.Fragments), SqlValue.FromDouble(fragmentSize), level.Pages,
                records, limited || !figures.IsHeap ? nullBig : SqlValue.FromInt64(level.Forwarded)));
        }
        AddSpill("LOB_DATA", figures.Lob);
        AddSpill("ROW_OVERFLOW_DATA", figures.Overflow);

        void AddSpill(string unitDesc, RealRecordFormat.SpillUnit spill)
        {
            if (spill.Pages == 0)
                return;
            var records = limited ? (RecordFigures?)null : new RecordFigures(spill.Bytes, spill.Records, 0, spill.Records, spill.Bytes - (2 * spill.Records), spill.MinRecord, spill.MaxRecord);
            rows.Add(PhysicalStatsRow(databaseId, table, identity, unit, typeDesc, unitDesc, 1, 0,
                SqlValue.FromDouble(0), nullBig, SqlValue.Null(SqlType.Float), spill.Pages,
                records, limited || !figures.IsHeap ? nullBig : SqlValue.FromInt64(0)));
        }
    }

    /// <summary>A level's record figures, which <c>LIMITED</c> leaves out.</summary>
    private readonly struct RecordFigures(long bytes, long records, long ghosts, long sized, long recordBytes, int min, int max)
    {
        public readonly long Bytes = bytes;
        public readonly long Records = records;
        public readonly long Ghosts = ghosts;
        public readonly long Sized = sized;
        public readonly long RecordBytes = recordBytes;
        public readonly int Min = min;
        public readonly int Max = max;
    }

    private static SqlValue[] PhysicalStatsRow(short databaseId, HeapTable table, IndexIdentity identity, PartitionCensus.Unit unit, string typeDesc, string unitDesc, byte depth, byte level,
        SqlValue fragmentation, SqlValue fragments, SqlValue fragmentSize, long pages, RecordFigures? records, SqlValue forwarded)
    {
        var nullBig = SqlValue.Null(SqlType.BigInt);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullFloat = SqlValue.Null(SqlType.Float);
        var zero = records is null ? nullBig : SqlValue.FromInt64(0);
        if (records is not { } r)
            r = default;
        var known = records is not null;
        return
        [
            SqlValue.FromInt16(databaseId),
            SqlValue.FromInt32(table.ObjectId),
            SqlValue.FromInt32(identity.IndexId),
            SqlValue.FromInt32(unit.Number),
            SqlValue.FromString(nvarchar60Catalog, typeDesc),
            SqlValue.FromString(nvarchar60Catalog, unitDesc),
            SqlValue.FromByte(depth),
            SqlValue.FromByte(level),
            fragmentation,
            fragments,
            fragmentSize,
            SqlValue.FromInt64(pages),
            known ? SqlValue.FromDouble(pages == 0 ? 0 : 100.0 * r.Bytes / (pages * (double)RealRecordFormat.PageBytes)) : nullFloat,
            known ? SqlValue.FromInt64(r.Records) : nullBig,
            known ? SqlValue.FromInt64(r.Ghosts) : nullBig,
            zero,
            known ? SqlValue.FromInt32(r.Min) : nullInt,
            known ? SqlValue.FromInt32(r.Max) : nullInt,
            known ? SqlValue.FromDouble(r.Sized == 0 ? 0 : (double)r.RecordBytes / r.Sized) : nullFloat,
            forwarded,
            zero,
            SqlValue.FromInt64(unit.PartitionId),
            SqlValue.FromByte(0),
            SqlValue.FromString(nvarchar60Catalog, "NOT VALID"),
            zero,
            zero,
            zero,
            zero,
            zero,
            zero,
        ];
    }

    /// <summary>
    /// <c>sys.dm_db_index_usage_stats</c>: every index any statement has used
    /// since the simulation started, in every database, with its counts and
    /// when each kind last happened (see <see cref="IndexUsage"/>); the
    /// system columns count index builds' scans alone.
    /// </summary>
    private static IEnumerable<SqlValue[]> IndexUsageRows(Parser.BatchContext batch)
    {
        static SqlValue When(long ticks) => ticks == 0 ? SqlValue.Null(SqlType.DateTime) : SqlValue.FromDateTime(new DateTime(ticks, DateTimeKind.Utc));
        var zero = SqlValue.FromInt64(0);
        var never = SqlValue.Null(SqlType.DateTime);
        foreach (var (database, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            foreach (var (_, schema) in database.Schemas)
            {
                foreach (var table in CatalogTables(schema, batch).OrderBy(static table => table.ObjectId))
                {
                    if (table.Usage.IsEmptyLockFree())
                        continue;
                    foreach (var identity in table.IndexIdentities())
                    {
                        if (!table.Usage.TryGetValue(identity.IndexId, out var usage) || !usage.Used)
                            continue;
                        yield return
                        [
                            SqlValue.FromInt16(id), SqlValue.FromInt32(table.ObjectId), SqlValue.FromInt32(identity.IndexId),
                            SqlValue.FromInt64(Interlocked.Read(ref usage.UserSeeks)), SqlValue.FromInt64(Interlocked.Read(ref usage.UserScans)),
                            SqlValue.FromInt64(Interlocked.Read(ref usage.UserLookups)), SqlValue.FromInt64(Interlocked.Read(ref usage.UserUpdates)),
                            When(Interlocked.Read(ref usage.LastUserSeekTicks)), When(Interlocked.Read(ref usage.LastUserScanTicks)),
                            When(Interlocked.Read(ref usage.LastUserLookupTicks)), When(Interlocked.Read(ref usage.LastUserUpdateTicks)),
                            zero, SqlValue.FromInt64(Interlocked.Read(ref usage.SystemScans)), zero, zero,
                            never, When(Interlocked.Read(ref usage.LastSystemScanTicks)), never, never,
                        ];
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>sys.dm_db_index_operational_stats(database_id, object_id, index_id,
    /// partition_number)</c>: a row per index and partition of every table —
    /// NULL or 0 naming every database, object, index or partition — carrying
    /// the row-level counts <see cref="IndexUsage"/> keeps (on the first
    /// partition of a partitioned index) and zero for the lock, latch,
    /// allocation and page counters the simulator's storage doesn't move.
    /// </summary>
    internal static List<SqlValue[]> OperationalStats(Parser.BatchContext batch, int? databaseId, int? objectId, int? indexId, int? partitionNumber)
    {
        var simulation = batch.Connection.Simulation;
        var rows = new List<SqlValue[]>();
        var zero = SqlValue.FromInt64(0);
        foreach (var (database, id) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
        {
            if (databaseId is { } wanted && wanted > 0 && wanted != id)
                continue;
            foreach (var (_, schema) in database.Schemas)
            {
                foreach (var table in CatalogTables(schema, batch).OrderBy(static table => table.ObjectId))
                {
                    if (objectId is { } wantedObject && wantedObject > 0 && wantedObject != table.ObjectId)
                        continue;
                    var census = new PartitionCensus();
                    foreach (var identity in table.IndexIdentities())
                    {
                        if (!HoldsStorage(identity) || (indexId is { } only && only > 0 && only != identity.IndexId))
                            continue;
                        _ = table.Usage.TryGetValue(identity.IndexId, out var usage);
                        foreach (var unit in census.Units(table, identity.IndexId, Simulation.PlacementOf(table, identity)))
                        {
                            if (partitionNumber is { } wantedPartition && wantedPartition > 0 && wantedPartition != unit.Number)
                                continue;
                            var counts = unit.Number == 1 ? usage : null;
                            var row = new SqlValue[OperationalStatsColumnCount];
                            row[0] = SqlValue.FromInt16(id);
                            row[1] = SqlValue.FromInt32(table.ObjectId);
                            row[2] = SqlValue.FromInt32(identity.IndexId);
                            row[3] = SqlValue.FromInt32(unit.Number);
                            row[4] = SqlValue.FromInt64(unit.PartitionId);
                            for (var i = 5; i < row.Length; i++)
                                row[i] = zero;
                            if (counts is not null)
                            {
                                row[5] = SqlValue.FromInt64(Interlocked.Read(ref counts.LeafInserts));
                                row[6] = SqlValue.FromInt64(Interlocked.Read(ref counts.LeafDeletes));
                                row[7] = SqlValue.FromInt64(Interlocked.Read(ref counts.LeafUpdates));
                                row[8] = SqlValue.FromInt64(Interlocked.Read(ref counts.LeafGhosts));
                                row[16] = SqlValue.FromInt64(Interlocked.Read(ref counts.RangeScans));
                                row[17] = SqlValue.FromInt64(Interlocked.Read(ref counts.SingletonLookups));
                            }
                            rows.Add(row);
                        }
                    }
                }
            }
        }
        return rows;
    }

    /// <summary>The width of <see cref="OperationalStats"/>' rows.</summary>
    internal const int OperationalStatsColumnCount = 51;

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
