using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sys.dm_db_index_physical_stats</c>, <c>sys.allocation_units</c>,
/// <c>sys.dm_db_partition_stats</c> and <c>sp_spaceused</c> report the pages
/// real lays a table's rowsets out on: every expected figure is what SQL
/// Server 2025 reported for the same script (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class StorageFiguresTests
{
    /// <summary>Each in-row level of the object's indexes as <c>index:level:pages:records:min:max</c>, in index and level order.</summary>
    private static string Levels(Simulation sim, string table, string mode = "DETAILED") =>
        (string)sim.ExecuteScalar($"""
            select string_agg(concat(index_id, ':', index_level, ':', page_count, ':', record_count, ':', min_record_size_in_bytes, ':', max_record_size_in_bytes), ' ')
                within group (order by index_id, index_level)
            from sys.dm_db_index_physical_stats(db_id(), object_id('{table}'), null, null, '{mode}')
            where alloc_unit_type_desc = 'IN_ROW_DATA'
            """)!;

    /// <summary>Each allocation unit of the object as <c>index:type:total:used:data</c>.</summary>
    private static string Units(Simulation sim, string table) =>
        (string)sim.ExecuteScalar($"""
            select string_agg(concat(p.index_id, ':', a.type, ':', a.total_pages, ':', a.used_pages, ':', a.data_pages), ' ')
                within group (order by p.index_id, p.partition_number, a.type)
            from sys.partitions p join sys.allocation_units a on a.container_id = p.partition_id
            where p.object_id = object_id('{table}')
            """)!;

    private static string Leaf(Simulation sim, string table, int indexId) =>
        (string)sim.ExecuteScalar($"""
            select concat(page_count, ':', record_count, ':', cast(avg_page_space_used_in_percent as decimal(5, 2)), ':', cast(avg_fragmentation_in_percent as decimal(5, 2)))
            from sys.dm_db_index_physical_stats(db_id(), object_id('{table}'), {indexId}, null, 'DETAILED') where index_level = 0
            """)!;

    [TestMethod]
    public void AKeyOrderedLoad_AndIndexesBuiltOverIt_FillTheirPages()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table c (id int not null primary key, a int not null, b varchar(20) null, pad char(100) not null default 'x');
            insert c (id, a, b) select value, (value * 7919) % 10007, case when value % 3 = 0 then null else replicate('b', value % 20) end from generate_series(1, 10000);
            create index ix_a on c (a);
            create index ix_b on c (b) include (pad);
            create index ix_f on c (a) where b is not null;
            """);
        AreEqual("1:0:157:10000:115:138 1:1:1:157:11:11 2:0:18:10000:12:12 2:1:1:18:15:15 3:0:148:10000:108:131 3:1:1:148:14:37 4:0:12:6667:12:12 4:1:1:12:15:15", Levels(sim, "c"));
        AreEqual("157:10000:99.02:0.64", Leaf(sim, "c", 1));
        AreEqual(6667L, sim.ExecuteScalar("select rows from sys.partitions where object_id = object_id('c') and index_id = 4"));
    }

    [TestMethod]
    public void AHeap_FillsItsPagesInTheOrderItHoldsItsRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h (id int not null, v varchar(50) null);
            insert h select value, replicate('v', value % 50) from generate_series(1, 5000);
            create index ix_h on h (id);
            create table h1 (id int not null, v varchar(500) null);
            declare @i int = 1;
            while @i <= 3000 begin insert h1 values (@i, replicate('v', @i % 100)); set @i += 1; end
            """);
        AreEqual("0:0:26:5000:11:64 2:0:12:5000:16:16 2:1:1:12:19:19", Levels(sim, "h"));
        // A row inserted alone stops at a page real's free-space map no longer offers.
        AreEqual("26:3000:94.72:0.00", Leaf(sim, "h1", 0));
        AreEqual("0:1:33:27:26", Units(sim, "h1"));
    }

    [TestMethod]
    public void AWidenedHeapRow_IsForwardedWhenItsPageHasNoRoom()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h (id int not null, v varchar(500) null);
            insert h select value, replicate('v', 10) from generate_series(1, 5000);
            update h set v = replicate('w', 300) where id % 10 = 0;
            """);
        var forwarded = (long)sim.ExecuteScalar("select forwarded_record_count from sys.dm_db_index_physical_stats(db_id(), object_id('h'), 0, null, 'DETAILED')")!;
        // Real forwarded 482 of the 500.
        IsTrue(forwarded is > 450 and <= 500, forwarded.ToInvariantString());
        AreEqual(37L, sim.ExecuteScalar("select page_count from sys.dm_db_index_physical_stats(db_id(), object_id('h'), 0, null, 'DETAILED')"));
        AreEqual(5000L + forwarded, sim.ExecuteScalar("select record_count from sys.dm_db_index_physical_stats(db_id(), object_id('h'), 0, null, 'DETAILED')"));
        AreEqual("0:1:41:38:37", Units(sim, "h"));
    }

    [TestMethod]
    [DataRow("@i", "53:2000:99.28:1.89")]
    [DataRow("10000 - @i", "53:2000:99.28:98.11")]
    [DataRow("(@i * 7919) % 10007", "64:2000:82.22:98.44")]
    [DataRow("case when @i % 2 = 0 then @i else 100000 + @i end", "54:2000:97.44:")]
    [DataRow("cast(substring(hashbytes('MD5', cast(@i as varchar(10))), 1, 4) as int)", "77:2000:68.34:")]
    public void RowsInsertedOneByOne_SplitPagesAsRealDoes(string key, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table t (id int not null primary key, pad char(200) not null default 'x');
            declare @i int = 1;
            begin tran;
            while @i <= 2000 begin insert t (id) values ({key}); set @i += 1; end
            commit;
            """);
        var leaf = Leaf(sim, "t", 1);
        // Where real's allocation interleaves another rowset's pages, only the
        // page figures are compared.
        AreEqual(expected, expected.EndsWith(':') ? leaf[..(leaf.LastIndexOf(':') + 1)] : leaf);
    }

    [TestMethod]
    public void AStatementsRows_ReachTheIndexSortedByItsKey()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table src (i int not null, k int not null);
            insert src select value, cast(substring(hashbytes('MD5', cast(value as varchar(10))), 1, 4) as int) from generate_series(1, 5000);
            create table e1 (id int not null primary key, pad char(200) not null default 'x');
            insert e1 (id) select k from src where i <= 2000;
            create table e3 (id int not null primary key, pad char(200) not null default 'x');
            declare @b int = 0;
            while @b < 2000 begin insert e3 (id) select k from src where i > @b and i <= @b + 20; set @b += 20; end
            create table e4 (id int not null primary key, a int not null, pad char(200) not null default 'x');
            create index ix4 on e4 (a);
            insert e4 (id, a) select i, k from src;
            """);
        AreEqual("53:2000:99.28:1.89", Leaf(sim, "e1", 1));
        AreEqual("76:2000:69.23:98.68", Leaf(sim, "e3", 1));
        AreEqual("136:5000:98.54:0.74", Leaf(sim, "e4", 1));
        AreEqual("9:5000:96.07:11.11", Leaf(sim, "e4", 2));
    }

    [TestMethod]
    public void AnIndexBuild_PacksEveryLevel_ToItsFillFactor()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table b (id int not null primary key, c char(400) not null);
            insert b select value, cast(value as char(400)) from generate_series(1, 3000);
            create index full_c on b (c) with (maxdop = 1);
            create index ff_c on b (c) with (maxdop = 1, fillfactor = 70);
            create index pad_c on b (c) with (maxdop = 1, fillfactor = 70, pad_index = on);
            create table g (id int not null primary key, c char(400) not null);
            create index grown_c on g (c);
            insert g select value, cast(value as char(400)) from generate_series(1, 3000);
            """);
        AreEqual(
            "1:0:158:3000:411:411 1:1:1:158:11:11 2:0:158:3000:408:408 2:1:9:158:411:411 2:2:1:9:411:411 "
                + "3:0:215:3000:408:408 3:1:12:215:411:411 3:2:1:12:411:411 4:0:215:3000:408:408 4:1:16:215:411:411 4:2:2:16:411:411 4:3:1:2:411:411",
            Levels(sim, "b"));
        AreEqual("1:1:177:160:158 2:1:177:169:158 3:1:233:229:215 4:1:241:235:215", Units(sim, "b"));
        // An index grown by inserts splits its non-leaf pages in half.
        AreEqual("1:0:158:3000:411:411 1:1:1:158:11:11 2:0:158:3000:408:408 2:1:17:158:411:411 2:2:1:17:411:411", Levels(sim, "g"));
        _ = sim.ExecuteNonQuery("alter index grown_c on g rebuild with (maxdop = 1)");
        AreEqual("1:0:158:3000:411:411 1:1:1:158:11:11 2:0:158:3000:408:408 2:1:9:158:411:411 2:2:1:9:411:411", Levels(sim, "g"));
    }

    [TestMethod]
    public void NonLeafRecords_CarryTheKeyTheLocatorAndTheUniquifier()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table nu (k int not null, pad char(100) not null default 'x');
            create clustered index cx on nu (k);
            insert nu (k) select value / 3 from generate_series(1, 6000);
            create table vk (k varchar(30) not null primary key, pad char(100) not null default 'x');
            insert vk (k) select right('0000000000' + cast(value as varchar(10)), 10) from generate_series(1, 6000);
            create table w (id int not null primary key, a int null, v varchar(20) not null, b bigint not null);
            insert w select value, case when value % 5 = 0 then null else value end, cast(value as varchar(20)), value from generate_series(1, 20000);
            create unique index ux on w (b);
            create index ixa on w (a);
            create index ixv on w (v);
            create unique index uxv on w (v) include (a);
            """);
        AreEqual("1:0:89:6000:111:119 1:1:1:89:11:19", Levels(sim, "nu"));
        AreEqual("1:0:93:6000:121:121 1:1:1:93:7:21", Levels(sim, "vk"));
        AreEqual("1:1:105:91:89", Units(sim, "nu"));
        AreEqual("1:1:113:95:93", Units(sim, "vk"));
        var w = Levels(sim, "w");
        Contains("2:1:1:45:15:15", w);
        Contains("3:1:1:35:18:18", w);
        Contains("4:1:1:46:16:20", w);
        Contains("5:1:1:56:12:16", w);
    }

    [TestMethod]
    public void LongValues_StayInTheRowWhileTheyFit_AndSpillAsRealSpillsThem()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table l (id int not null primary key, len int not null, v nvarchar(max) null);
            insert l values (1, 100, replicate(N'a', 100)), (2, 3000, replicate(N'a', 3000)), (3, 4000, replicate(N'a', 4000)), (4, 4001, replicate(cast(N'a' as nvarchar(max)), 4001)),
                (5, 10000, replicate(cast(N'a' as nvarchar(max)), 10000)), (6, 40000, replicate(cast(N'a' as nvarchar(max)), 40000)), (7, 0, null), (8, 3900, replicate(N'b', 3900));
            create table t2 (id int not null primary key, a nvarchar(max) null, b nvarchar(max) null);
            insert t2 values (1, replicate(N'a', 3000), replicate(N'b', 3000)), (2, replicate(N'a', 2000), replicate(N'b', 1000));
            create table ro (id int not null primary key, a varchar(5000) null, b varchar(5000) null);
            insert ro values (1, replicate('a', 5000), replicate('b', 5000)), (2, replicate('a', 3000), replicate('b', 3000)), (3, replicate('a', 4000), replicate('b', 4500));
            create table oor (id int not null primary key, v nvarchar(max) null);
            exec sp_tableoption 'oor', 'large value types out of row', 1;
            insert oor values (1, N'x'), (2, replicate(N'a', 100)), (3, replicate(N'a', 3000));
            create table tx (id int not null primary key, v text null);
            insert tx values (1, 'x'), (2, replicate('a', 100)), (3, replicate('a', 3000)), (4, null);
            create table c6 (id int not null primary key, v varbinary(max));
            insert c6 values (1, cast(replicate(cast('a' as varchar(max)), 6 * 8040) as varbinary(max)));
            """);
        string Spill(string table) => (string)sim.ExecuteScalar($"""
            select string_agg(concat(alloc_unit_type_desc, ':', index_level, ':', page_count, ':', record_count, ':', min_record_size_in_bytes, ':', max_record_size_in_bytes), ' ')
                within group (order by alloc_unit_type_desc, index_level)
            from sys.dm_db_index_physical_stats(db_id(), object_id('{table}'), null, null, 'DETAILED')
            """)!;
        AreEqual("IN_ROW_DATA:0:3:8:15:8019 IN_ROW_DATA:1:1:3:11:11 LOB_DATA:0:15:15:180:8054", Spill("l"));
        AreEqual("IN_ROW_DATA:0:2:2:6017:6041 IN_ROW_DATA:1:1:2:11:11 LOB_DATA:0:1:1:6014:6014", Spill("t2"));
        AreEqual("IN_ROW_DATA:0:3:3:4041:6017 IN_ROW_DATA:1:1:3:11:11 ROW_OVERFLOW_DATA:0:2:2:4514:5014", Spill("ro"));
        Contains("IN_ROW_DATA:0:1:3:31:31 LOB_DATA:0:", Spill("oor"));
        AreEqual("IN_ROW_DATA:0:1:4:11:31 LOB_DATA:0:1:5:84:3014", Spill("tx"));
        AreEqual("IN_ROW_DATA:0:1:1:39:39 LOB_DATA:0:8:7:116:8054", Spill("c6"));
        AreEqual("1:1:9:5:3 1:2:17:16:0", Units(sim, "l"));
        AreEqual("1:1:9:4:2 1:2:9:2:0 1:3:0:0:0", Units(sim, "t2"));
    }

    [TestMethod]
    public void AllocationUnits_ExistAsTheColumnsCallForThem()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table u1 (id int not null primary key, v nvarchar(max) null, w varchar(100));
            insert u1 values (1, N'small', 'x');
            create index i1 on u1 (w);
            create index i2 on u1 (w) include (v);
            create table u2 (id int not null primary key, a varchar(4000), b varchar(4040));
            create table u4 (id int not null, a varchar(8000), b varchar(100));
            create index i4 on u4 (b) include (a);
            create table u5 (id int not null, x xml, t text);
            """);
        AreEqual("1:1:9:2:1 1:2:0:0:0 1:3:0:0:0 2:1:9:2:1 3:1:9:2:1 3:2:0:0:0 3:3:0:0:0", Units(sim, "u1"));
        AreEqual("1:1:0:0:0", Units(sim, "u2"));
        AreEqual("0:1:0:0:0 0:3:0:0:0 2:1:0:0:0 2:3:0:0:0", Units(sim, "u4"));
        AreEqual("0:1:0:0:0 0:2:0:0:0", Units(sim, "u5"));
    }

    [TestMethod]
    public void ADeletedRow_KeepsItsPlaceAsAGhost_ThenLeavesItsPage()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table g (id int not null primary key, pad char(100) not null default 'x');
            insert g (id) select value from generate_series(1, 5000);
            begin tran;
            delete g where id % 2 = 0;
            """).ExecuteNonQuery();
        const string Figures = "select concat(page_count, ':', record_count, ':', ghost_record_count, ':', cast(avg_page_space_used_in_percent as decimal(5, 2))) from sys.dm_db_index_physical_stats(db_id(), object_id('g'), 1, null, 'DETAILED') where index_level = 0";
        StartsWith("71:2500:2500:", (string)connection.CreateCommand(Figures).ExecuteScalar()!);
        _ = connection.CreateCommand("commit").ExecuteNonQuery();
        // Real's ghost cleanup takes a few seconds; the pages keep their place.
        AreEqual("71:2500:0:49.15", connection.CreateCommand(Figures).ExecuteScalar());
    }

    [TestMethod]
    public void EachPartition_IsARowsetOfItsOwn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range right for values (1000, 4000, 9000);
            create partition scheme ps as partition pf all to ([primary]);
            create table p (id int not null, a int not null, pad char(100) not null default 'x', constraint pkp primary key (id)) on ps (id);
            create index pa on p (a) on ps (id);
            insert p (id, a) select value, value % 97 from generate_series(1, 5000);
            """);
        AreEqual("1:1:33:17:15 1:1:65:46:44 1:1:33:17:15 1:1:0:0:0 2:1:9:4:2 2:1:9:8:6 2:1:9:4:2 2:1:0:0:0", Units(sim, "p"));
        AreEqual("50.00 16.67 50.00 0.00", sim.ExecuteScalar("""
            select string_agg(cast(cast(avg_fragmentation_in_percent as decimal(5, 2)) as varchar(10)), ' ') within group (order by partition_number)
            from sys.dm_db_index_physical_stats(db_id(), object_id('p'), 2, null, 'LIMITED')
            """));
    }

    [TestMethod]
    public void TheModes_ReportWhatRealsDo()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h (id int not null, v varchar(500) null);
            insert h select value, replicate('v', 10) from generate_series(1, 5000);
            create table e (id int not null);
            """);
        AreEqual("1:0:17:NULL:NULL:NULL:0.00:1:17.00", sim.ExecuteScalar("""
            select concat(index_depth, ':', index_level, ':', page_count, ':', isnull(cast(record_count as varchar(10)), 'NULL'), ':', isnull(cast(avg_page_space_used_in_percent as varchar(10)), 'NULL'), ':',
                isnull(cast(forwarded_record_count as varchar(10)), 'NULL'), ':', cast(avg_fragmentation_in_percent as decimal(5, 2)), ':', fragment_count, ':', cast(avg_fragment_size_in_pages as decimal(5, 2)))
            from sys.dm_db_index_physical_stats(db_id(), object_id('h'), null, null, 'LIMITED')
            """));
        // SAMPLED reads a heap's fragmentation as none.
        AreEqual("5000:0:0.00:0.00", sim.ExecuteScalar("""
            select concat(record_count, ':', forwarded_record_count, ':', cast(avg_fragmentation_in_percent as decimal(5, 2)), ':', cast(avg_fragment_size_in_pages as decimal(5, 2)))
            from sys.dm_db_index_physical_stats(db_id(), object_id('h'), null, null, 'SAMPLED')
            """));
        AreEqual("0:0:0:0:0", sim.ExecuteScalar("""
            select concat(index_depth, ':', page_count, ':', record_count, ':', min_record_size_in_bytes, ':', fragment_count)
            from sys.dm_db_index_physical_stats(db_id(), object_id('e'), null, null, 'DETAILED')
            """));
    }

    [TestMethod]
    public void SpSpaceUsed_SumsTheSameUnits()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table nu (k int not null, pad char(100) not null default 'x');
            create clustered index cx on nu (k);
            insert nu (k) select value / 3 from generate_series(1, 6000);
            """);
        using var reader = sim.ExecuteReader("exec sp_spaceused 'nu'");
        IsTrue(reader.Read());
        AreEqual("840 KB|712 KB|16 KB|112 KB", string.Join("|", reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
    }
}
