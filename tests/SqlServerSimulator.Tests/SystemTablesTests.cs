using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The system base tables and internal tables every database lists in its
/// catalog — their objects, columns, indexes, rowsets and pages, and the
/// functions, procedures and checks that read them — as a fresh database of
/// each kind holds them on SQL Server 2025 (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class SystemTablesTests
{
    private static List<string> Messages(Simulation simulation, string sql)
    {
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Message));
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        return messages;
    }

    private const string TypeCounts = """
        select string_agg(concat(rtrim(type), ':', n), ' ') within group (order by type)
        from (select type, count(*) n from sys.objects where type in ('S', 'IT', 'SQ') group by type) t
        """;

    [TestMethod]
    public void EachDatabaseKind_ListsRealsSystemTables()
    {
        var sim = new Simulation();
        AreEqual("IT:43 S:72 SQ:3", sim.ExecuteScalar(TypeCounts));
        AreEqual("IT:42 S:72 SQ:3", sim.ExecuteScalar("use model; " + TypeCounts));
        AreEqual("IT:43 S:72 SQ:3", sim.ExecuteScalar("use tempdb; " + TypeCounts));
        AreEqual("IT:46 S:72 SQ:6", sim.ExecuteScalar("use msdb; " + TypeCounts));
        AreEqual("IT:48 S:79 SQ:3", sim.ExecuteScalar("use master; " + TypeCounts));
        AreEqual("wpr_bucket_table", sim.ExecuteScalar("select name from sys.objects where object_id = 1205579333"));
        AreEqual("service_broker_map", sim.ExecuteScalar("select name from tempdb.sys.objects where object_id = 1205579333"));
        AreEqual("sysmatrixages", sim.ExecuteScalar("select name from master.sys.objects where object_id = 16"));
    }

    [TestMethod]
    public void SystemTables_AreShippedSysObjectsOutsideSysTablesAndSystemObjects()
    {
        var sim = new Simulation();
        AreEqual("sysschobjs|4|1|0", sim.ExecuteScalar("select concat_ws('|', name, schema_id, cast(is_ms_shipped as int), parent_object_id) from sys.objects where object_id = 34"));
        AreEqual("SYSTEM_TABLE", sim.ExecuteScalar("select type_desc from sys.objects where object_id = 34"));
        AreEqual("queue_messages_1977058079|4|1977058079", sim.ExecuteScalar("select concat_ws('|', name, schema_id, parent_object_id) from sys.objects where object_id = 1993058136"));
        AreEqual("INTERNAL_TABLE", sim.ExecuteScalar("select type_desc from sys.objects where object_id = 1993058136"));
        AreEqual("QueryNotificationErrorsQueue|1", sim.ExecuteScalar("select concat_ws('|', name, schema_id) from sys.objects where object_id = 1977058079"));
        AreEqual("SERVICE_QUEUE", sim.ExecuteScalar("select type_desc from sys.objects where object_id = 1977058079"));
        AreEqual(118, sim.ExecuteScalar("select count(*) from sys.all_objects where type in ('S', 'IT', 'SQ')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.system_objects where type in ('S', 'IT')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables"));
        AreEqual("1|1|2", sim.ExecuteScalar("select concat_ws('|', userstat, sysstat, category) from sys.sysobjects where id = 34"));
        AreEqual("1977058079|0", sim.ExecuteScalar("select concat_ws('|', parent_obj, sysstat) from sys.sysobjects where id = 1993058136 and xtype = 'IT'"));
    }

    [TestMethod]
    public void InternalTables_ReportTheirTypeAndParent()
    {
        var sim = new Simulation();
        AreEqual(43, sim.ExecuteScalar("select count(*) from sys.internal_tables"));
        AreEqual("0|1|241|QUERY_DISK_STORE_QUERY|0|0|0", sim.ExecuteScalar("""
            select concat_ws('|', cast(is_ms_shipped as int), (select cast(is_ms_shipped as int) from sys.objects o where o.object_id = it.object_id),
                internal_type, internal_type_desc, parent_id, parent_minor_id, lob_data_space_id)
            from sys.internal_tables it where name = 'plan_persist_query'
            """));
        AreEqual("201|QUEUE_MESSAGES|2009058193|1", sim.ExecuteScalar("select concat_ws('|', internal_type, internal_type_desc, parent_id, lob_data_space_id) from sys.internal_tables where object_id = 2025058250"));
    }

    [TestMethod]
    public void SystemTableColumns_ListInSysColumnsWithRealsTypesAndCollations()
    {
        var sim = new Simulation();
        AreEqual(1269, sim.ExecuteScalar("select count(*) from sys.columns c join sys.objects o on o.object_id = c.object_id where o.type in ('S', 'IT')"));
        AreEqual(1269, sim.ExecuteScalar("select count(*) from sys.all_columns c join sys.objects o on o.object_id = c.object_id where o.type in ('S', 'IT')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.system_columns c where c.object_id = 34"));
        AreEqual(
            "id:int:4:0, name:sysname:256:0, nsid:int:4:0, nsclass:tinyint:1:0, status:int:4:0, type:char:2:0, pid:int:4:0, pclass:tinyint:1:0, intprop:int:4:0, created:datetime:8:0, modified:datetime:8:0, status2:int:4:0",
            sim.ExecuteScalar("select string_agg(concat(name, ':', type_name(user_type_id), ':', max_length, ':', cast(is_nullable as int)), ', ') within group (order by column_id) from sys.columns where object_id = 34"));
        AreEqual("Latin1_General_CI_AS_KS_WS", sim.ExecuteScalar("select collation_name from sys.columns where object_id = 34 and name = 'type'"));
        // A system base table's names follow the database's collation, an
        // internal table's the server's.
        sim.ExecuteBatches("create database cs collate Latin1_General_100_CS_AS");
        AreEqual("Latin1_General_100_CS_AS", sim.ExecuteScalar("select collation_name from cs.sys.columns where object_id = 34 and name = 'name'"));
        AreEqual("SQL_Latin1_General_CP1_CI_AS", sim.ExecuteScalar("select collation_name from cs.sys.columns where object_id = 165575628 and name = 'query_sql_text'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.syscolumns where id = 34 and name = 'status2'"));
    }

    [TestMethod]
    public void SystemTableIdentityColumns_ListInIdentityColumns()
    {
        var sim = new Simulation();
        AreEqual(8, sim.ExecuteScalar("select count(*) from sys.identity_columns"));
        AreEqual("queuing_order|0|1|bigint", sim.ExecuteScalar("""
            select concat_ws('|', name, cast(seed_value as bigint), cast(increment_value as bigint), cast(sql_variant_property(seed_value, 'BaseType') as sysname))
            from sys.identity_columns where object_id = 1993058136
            """));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select last_value from sys.identity_columns where object_id = 133575514"));
        AreEqual(81L, sim.ExecuteScalar("select cast(last_value as bigint) from msdb.sys.identity_columns where object_id = 1019150676"));
    }

    [TestMethod]
    public void SystemTableIndexes_ListWithRealsKeys()
    {
        var sim = new Simulation();
        AreEqual(199, sim.ExecuteScalar("select count(*) from sys.indexes i join sys.objects o on o.object_id = i.object_id where o.type in ('S', 'IT')"));
        AreEqual(436, sim.ExecuteScalar("select count(*) from sys.index_columns i join sys.objects o on o.object_id = i.object_id where o.type in ('S', 'IT')"));
        AreEqual("clst:1:1, nc1:2:1, nc2:2:1, nc3:2:0", sim.ExecuteScalar("select string_agg(concat(name, ':', type, ':', cast(is_unique as int)), ', ') within group (order by index_id) from sys.indexes where object_id = 34"));
        // An index's key ordinals can run out of its column order.
        AreEqual("1:2, 2:1, 3:3", sim.ExecuteScalar("select string_agg(concat(column_id, ':', key_ordinal), ', ') within group (order by index_column_id) from sys.index_columns where object_id = 74 and index_id = 1"));
        AreEqual("1:2, 2:1, 3:3", sim.ExecuteScalar("select string_agg(concat(colid, ':', keyno), ', ') within group (order by colid) from sys.sysindexkeys where id = 74 and indid = 1"));
        AreEqual("1:1:1, 1:2:3", sim.ExecuteScalar("select string_agg(concat(stats_id, ':', stats_column_id, ':', column_id), ', ') within group (order by stats_column_id) from sys.stats_columns where object_id = 3"));
        AreEqual(0, sim.ExecuteScalar("select cast(allow_page_locks as int) from sys.indexes where object_id = 1993058136 and index_id = 2"));
        // sysindexes counts a nonclustered index's clustered keys, a column in
        // both twice.
        AreEqual("4|2|11|2", sim.ExecuteScalar("select concat_ws('|', (select keycnt from sys.sysindexes where id = 34 and indid = 2), (select status from sys.sysindexes where id = 34 and indid = 2), (select keycnt from sys.sysindexes where id = 1993058136 and indid = 2), (select lockflags from sys.sysindexes where id = 1993058136 and indid = 2))"));
    }

    [TestMethod]
    public void SystemTableRowsets_FillThePartitionAndAllocationViews()
    {
        var sim = new Simulation();
        AreEqual(199, sim.ExecuteScalar("select count(*) from sys.partitions"));
        AreEqual(233, sim.ExecuteScalar("select count(*) from sys.allocation_units"));
        AreEqual(465L, sim.ExecuteScalar("select sum(total_pages) from sys.allocation_units"));
        AreEqual("196608|196608|1563", sim.ExecuteScalar("select concat_ws('|', partition_id, hobt_id, rows) from sys.partitions where object_id = 3"));
        AreEqual("PAGE", sim.ExecuteScalar("select top 1 data_compression_desc from sys.partitions where object_id = 437576597"));
        // The partition stats leave an internal table's heap out.
        AreEqual(195, sim.ExecuteScalar("select count(*) from sys.dm_db_partition_stats"));
        AreEqual(sim.ExecuteScalar("select sum(used_pages) from sys.allocation_units"), sim.ExecuteScalar("select sum(used_page_count) from sys.dm_db_partition_stats"));
        AreEqual(199, sim.ExecuteScalar("select count(*) from sys.system_internals_partitions"));
        AreEqual(233, sim.ExecuteScalar("select count(*) from sys.system_internals_allocation_units"));
        AreEqual("14|82|58|19|19", sim.ExecuteScalar("select concat_ws('|', max_null_bit_used, max_leaf_length, min_leaf_length, max_internal_length, min_internal_length) from sys.system_internals_partitions where partition_id = 196608"));
        // A database with no table of its own still sums to pages, which SMO's
        // DataSpaceUsage and SpaceAvailable read.
        IsNotNull(sim.ExecuteScalar("select sum(a.total_pages) from model.sys.allocation_units a"));
    }

    [TestMethod]
    public void SpaceUsed_CountsTheSystemTablesPages()
    {
        var sim = new Simulation();
        using var reader = sim.ExecuteReader("exec sp_spaceused");
        IsTrue(reader.Read());
        AreEqual("16.00 MB|4.37 MB", $"{reader.GetString(1)}|{reader.GetString(2)}");
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("3720 KB|1256 KB|1504 KB|960 KB", $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}");
    }

    [TestMethod]
    public void ObjectFunctions_AnswerForSystemTables()
    {
        var sim = new Simulation();
        AreEqual("34|NULL|NULL|34|NULL|181575685|3", sim.ExecuteScalar("""
            select concat_ws('|', object_id('sys.sysschobjs'), isnull(cast(object_id('sysschobjs') as varchar), 'NULL'), isnull(cast(object_id('dbo.sysschobjs') as varchar), 'NULL'),
                object_id('sys.sysschobjs', 'S'), isnull(cast(object_id('sys.sysschobjs', 'U') as varchar), 'NULL'), object_id('sys.plan_persist_query', 'IT'), object_id('simulated.sys.sysrscols'))
            """));
        AreEqual("1977058079|1977058079", sim.ExecuteScalar("select concat_ws('|', object_id('dbo.QueryNotificationErrorsQueue'), object_id('QueryNotificationErrorsQueue'))"));
        AreEqual("sysrscols|queue_messages_1977058079|sys|sys|QueryNotificationErrorsQueue|dbo|sysmatrixages", sim.ExecuteScalar("""
            select concat_ws('|', object_name(3), object_name(1993058136), object_schema_name(3), object_schema_name(1993058136), object_name(1977058079), object_schema_name(1977058079), object_name(16, db_id('master')))
            """));
        AreEqual("1|0|1|0|4|1|1|0", sim.ExecuteScalar("""
            select concat_ws('|', objectproperty(34, 'IsSystemTable'), objectproperty(181575685, 'IsSystemTable'), objectproperty(181575685, 'IsTable'), objectproperty(34, 'IsUserTable'),
                objectproperty(181575685, 'OwnerId'), objectproperty(1993058136, 'OwnerId'), objectproperty(34, 'TableHasNonclustIndex'), objectproperty(3, 'TableHasNonclustIndex'))
            """));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(181575685, 'TableHasClustIndex')"));
        AreEqual("S |IT|1563", sim.ExecuteScalar("select concat_ws('|', cast(objectpropertyex(3, 'BaseType') as char(2)), cast(objectpropertyex(181575685, 'BaseType') as char(2)), cast(objectpropertyex(3, 'Cardinality') as bigint))"));
    }

    [TestMethod]
    public void SystemTables_HideFromAUserWithoutPermissions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user plain without login");
        AreEqual("0|0|0|0|0|NULL|NULL|199", sim.ExecuteScalar("""
            execute as user = 'plain';
            select concat_ws('|', (select count(*) from sys.objects where type in ('S', 'IT', 'SQ')), (select count(*) from sys.internal_tables),
                (select count(*) from sys.columns where object_id = 34), (select count(*) from sys.indexes where object_id = 34),
                (select count(*) from sys.sysobjects where xtype in ('S', 'IT', 'SQ')),
                isnull(cast(object_id('sys.sysschobjs') as varchar), 'NULL'), isnull(object_name(34), 'NULL'), (select count(*) from sys.partitions));
            revert;
            """));
    }

    [TestMethod]
    public void CreateQueue_AddsItsMessageTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create queue dbo.q");
        AreEqual("1|201|QUEUE_MESSAGES|2", sim.ExecuteScalar("""
            select concat_ws('|', count(*), max(it.internal_type), max(it.internal_type_desc), (select count(*) from sys.indexes i where i.object_id = max(it.object_id)))
            from sys.internal_tables it where it.parent_id = object_id('dbo.q') and it.name = concat('queue_messages_', object_id('dbo.q'))
            """));
        AreEqual(16, sim.ExecuteScalar("select count(*) from sys.columns where object_id = (select object_id from sys.internal_tables where parent_id = object_id('dbo.q'))"));
        AreEqual(0L, sim.ExecuteScalar("select sum(rows) from sys.partitions where object_id = (select object_id from sys.internal_tables where parent_id = object_id('dbo.q'))"));
        _ = sim.ExecuteNonQuery("drop queue dbo.q");
        AreEqual(43, sim.ExecuteScalar("select count(*) from sys.internal_tables"));
    }

    [TestMethod]
    public void ChangeTracking_AddsTheTablesChangeTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set change_tracking = on;
            create table dbo.ct2 (a int not null, b nvarchar(10) not null, c int, constraint pk_ct2 primary key (b, a));
            alter table dbo.ct2 enable change_tracking with (track_columns_updated = on);
            insert dbo.ct2 values (1, N'x', 1), (2, N'y', 2);
            update dbo.ct2 set c = 5 where a = 1;
            """);
        AreEqual("209|CHANGE_TRACKING|0", sim.ExecuteScalar("select concat_ws('|', internal_type, internal_type_desc, lob_data_space_id) from sys.internal_tables where parent_id = object_id('dbo.ct2') and name = concat('change_tracking_', object_id('dbo.ct2'))"));
        AreEqual(
            "sys_change_xdes_id:bigint:8, sys_change_xdes_id_seq:bigint:8, sys_change_operation:nchar:2, sys_change_columns:varbinary:4100, sys_change_context:varbinary:128, k_b_00000002:nvarchar:20, k_a_00000001:int:4",
            sim.ExecuteScalar("select string_agg(concat(name, ':', type_name(user_type_id), ':', max_length), ', ') within group (order by column_id) from sys.columns where object_id = (select object_id from sys.internal_tables where parent_id = object_id('dbo.ct2'))"));
        AreEqual("sys_change_cidx|3|9|2|1", sim.ExecuteScalar("""
            select concat_ws('|', i.name, p.rows, a.total_pages, a.used_pages, a.data_pages)
            from sys.internal_tables it join sys.indexes i on i.object_id = it.object_id join sys.partitions p on p.object_id = it.object_id
            join sys.allocation_units a on a.container_id = p.partition_id where it.parent_id = object_id('dbo.ct2')
            """));
        AreEqual("7|4285|26|26|23", sim.ExecuteScalar("""
            select concat_ws('|', max_null_bit_used, max_leaf_length, min_leaf_length, max_internal_length, min_internal_length) from sys.system_internals_partitions
            where object_id = (select object_id from sys.internal_tables where parent_id = object_id('dbo.ct2'))
            """));
        AreEqual(1, sim.ExecuteScalar("select objectproperty((select object_id from sys.internal_tables where parent_id = object_id('dbo.ct2')), 'OwnerId')"));
        _ = sim.ExecuteNonQuery("alter table dbo.ct2 disable change_tracking");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.internal_tables where parent_id = object_id('dbo.ct2')"));
    }

    [TestMethod]
    public void CatalogBaseTables_CountWhatTheUserMade()
    {
        var sim = new Simulation();
        const string Rows = """
            select string_agg(concat(o.name, ':', p.rows), ' ') within group (order by o.name)
            from sys.partitions p join sys.objects o on o.object_id = p.object_id
            where o.name in ('sysallocunits', 'syscolpars', 'sysidxstats', 'sysiscols', 'sysrowsets', 'sysrscols', 'sysscalartypes', 'sysschobjs') and p.index_id = 1
            """;
        AreEqual("sysallocunits:233 syscolpars:1269 sysidxstats:307 sysiscols:544 sysrowsets:199 sysrscols:1563 sysscalartypes:36 sysschobjs:2894", sim.ExecuteScalar(Rows));
        sim.ExecuteBatches(
            "create table dbo.p (id int primary key, n varchar(max))",
            "create table dbo.c (id int identity primary key, pid int references dbo.p(id), v int default 1 check (v > 0), w nvarchar(20) unique)",
            "create index ix on dbo.c (v) include (w)",
            "create statistics st on dbo.c (v, w)",
            "create table dbo.h (a int, b int)",
            "create view dbo.vw as select id, v from dbo.c",
            "create procedure dbo.pr @a int, @b varchar(10) output as select 1",
            "create function dbo.fn (@x int) returns int as begin return @x end",
            "create type dbo.tt as table (a int primary key, b int)");
        AreEqual("sysallocunits:239 syscolpars:1285 sysidxstats:314 sysiscols:552 sysrowsets:204 sysrscols:1576 sysscalartypes:37 sysschobjs:2908", sim.ExecuteScalar(Rows));
        // The system databases keep a fresh database's.
        AreEqual(2893L, sim.ExecuteScalar("select rows from model.sys.partitions where object_id = 34 and index_id = 1"));
    }

    [TestMethod]
    public void QueryStoreTables_HoldWhatTheStoreRecords()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); select a from t where a = 1");
        AreEqual(sim.ExecuteScalar("select cast(count(*) as bigint) from sys.query_store_query"), sim.ExecuteScalar("select rows from sys.partitions where object_id = object_id('sys.plan_persist_query') and index_id = 1"));
        AreEqual(0L, sim.ExecuteScalar("select rows from sys.partitions where object_id = object_id('sys.plan_persist_runtime_stats') and index_id = 1"));
    }

    [TestMethod]
    public void InternalsPartitions_ComputeRealsRecordLengthsForUserRowsets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.c1 (id int primary key, n nvarchar(50) not null, m varchar(max), d datetime2 null);
            create index ix_n on dbo.c1(n);
            create index ix_d on dbo.c1(d) include (m);
            create table dbo.c3 (a int not null, b int);
            create clustered index cx on dbo.c3(b);
            create table dbo.h3 (a int not null, b int, c varchar(20));
            create index ix_h3 on dbo.h3(b);
            create table dbo.k4 (id bigint primary key, b1 bit, b2 bit, b3 bit, c nchar(5));
            """);
        AreEqual("c1:1:4:8125:16:15:11, c1:2:2:112:5:118:11, c1:3:3:8020:13:22:19, c3:1:3:23:12:22:11, h3:0:3:39:12:0:0, h3:2:2:16:13:22:19, k4:1:5:26:23:18:15", sim.ExecuteScalar("""
            select string_agg(concat(o.name, ':', s.index_id, ':', s.max_null_bit_used, ':', s.max_leaf_length, ':', s.min_leaf_length, ':', s.max_internal_length, ':', s.min_internal_length), ', ')
                within group (order by o.name, s.index_id)
            from sys.system_internals_partitions s join sys.objects o on o.object_id = s.object_id where o.type = 'U'
            """));
        AreEqual("2|1|1", sim.ExecuteScalar("select concat_ws('|', max(dropped_lob_column_state), min(ownertype), max(cast(is_data_row_format as int))) from sys.system_internals_partitions s where object_id = object_id('dbo.c1')"));
    }

    [TestMethod]
    public void InternalsAllocationUnits_NumberTheBufferPoolsPages()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b char(2000)); insert t select top 40 1, 'x' from sys.all_objects");
        var iam = (byte[])sim.ExecuteScalar("""
            select s.first_iam_page from sys.system_internals_allocation_units s join sys.partitions p on p.partition_id = s.container_id where p.object_id = object_id('t')
            """)!;
        var pageId = BitConverter.ToInt32(iam, 0);
        AreEqual(1, BitConverter.ToInt16(iam, 4));
        AreEqual("IAM_PAGE", sim.ExecuteScalar($"select page_type from sys.dm_os_buffer_descriptors where database_id = db_id() and page_id = {pageId}"));
        CollectionAssert.AreEqual(new byte[6], (byte[])sim.ExecuteScalar("select first_iam_page from sys.system_internals_allocation_units where container_id = 281474977103872")!);
    }

    [TestMethod]
    public void CheckDb_ListsTheSystemTablesByObjectId()
    {
        var sim = new Simulation();
        var messages = Messages(sim, "create table t (id int); insert t values (1); dbcc checkdb");
        var objects = messages.Where(static m => m.StartsWith("DBCC results for '", StringComparison.Ordinal)).Skip(1).ToList();
        HasCount(116, objects);
        AreEqual("DBCC results for 'sys.sysrscols'.", objects[0]);
        AreEqual("DBCC results for 't'.", objects[72]);
        AreEqual("DBCC results for 'sys.sqlagent_jobs'.", objects[73]);
        AreEqual("DBCC results for 'sys.filetable_updates_2105058535'.", objects[^1]);
        Contains("There are 0 rows in 0 pages for object \"sys.sysclones\".", messages);
        Contains("There are 2 rows in 1 pages for object \"sys.sysfiles1\".", messages);
        Contains("There are 0 rows in 0 pages for object \"sys.queue_messages_1977058079\".", messages);
        // Another filegroup holds none of them.
        _ = sim.ExecuteNonQuery("alter database current add filegroup fg2");
        IsFalse(Messages(sim, "dbcc checkfilegroup('fg2')").Exists(static m => m.Contains("'sys.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UpdateStats_WalksTheInternalTables()
    {
        var sim = new Simulation();
        var messages = Messages(sim, "exec sp_updatestats");
        AreEqual("Updating [sys].[sqlagent_jobs]", messages[0]);
        AreEqual("    [sqlagent_jobs_clust], update is not necessary...", messages[1]);
        Contains("Updating [sys].[plan_persist_ce_feedback_for_expressions]", messages);
        IsFalse(messages.Exists(static m => m.Contains("persistent_version_store", StringComparison.Ordinal)));
        IsFalse(messages.Exists(static m => m.Contains("[sysschobjs]", StringComparison.Ordinal)));
        HasCount(200, messages);
        AreEqual("Statistics for all tables have been updated.", messages[^1]);
    }
}
