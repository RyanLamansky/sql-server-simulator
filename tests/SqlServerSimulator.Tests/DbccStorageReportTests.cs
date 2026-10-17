using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>DBCC SHOWCONTIG</c>, <c>DBCC IND</c>, <c>DBCC CHECKALLOC</c>'s
/// allocation report and <c>sys.dm_db_database_page_allocations</c>, all read
/// off one numbering of the pages the storage figures lay out. Figures and
/// shapes probed 2026-10-17 against SQL Server 2025; the page ids are the
/// simulator's own, so the tests hold them to each other rather than to real's.
/// </summary>
[TestClass]
public sealed class DbccStorageReportTests
{
    private const string Completed = "DBCC execution completed. If DBCC printed error messages, contact your system administrator.";

    /// <summary>A heap and a clustered table of 300 200-byte rows, the second with an index built over its rows.</summary>
    private const string Tables = """
        create table h (a int, b char(200));
        insert h select top 300 row_number() over (order by (select 1)), 'x' from sys.all_columns;
        create table c (a int primary key, b char(200));
        insert c select top 300 row_number() over (order by (select 1)), 'x' from sys.all_columns;
        create index ix on c(b);
        """;

    /// <summary>What a batch sent: its messages as <c>number: text</c> and its result sets' rows as <c>a | b</c>, each set headed by its column names.</summary>
    private static (List<string> Messages, List<string> Rows) Run(Simulation simulation, string sql)
    {
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add($"{error.Number}: {error.Message}");
        };
        var rows = new List<string>();
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        do
        {
            if (reader.FieldCount == 0)
                continue;
            rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
            while (reader.Read())
                rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }
        while (reader.NextResult());
        return (messages, rows);
    }

    private static Simulation WithTables()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Tables);
        return simulation;
    }

    [TestMethod]
    public void ShowContig_ReportsAHeapsPages()
    {
        var simulation = WithTables();
        var objectId = simulation.ExecuteScalar<int>("select object_id('h')");
        var databaseId = simulation.ExecuteScalar<short>("select db_id()");
        CollectionAssert.AreEqual(
            new[]
            {
                "7942: DBCC SHOWCONTIG scanning 'h' table...",
                $"7943: Table: 'h' ({objectId}); index ID: 0, database ID: {databaseId}",
                "7944: TABLE level scan performed.",
                "7945: - Pages Scanned................................: 8",
                "7946: - Extents Scanned..............................: 1",
                "7947: - Extent Switches..............................: 0",
                "7948: - Avg. Pages per Extent........................: 8.0",
                "7949: - Scan Density [Best Count:Actual Count].......: 100.00% [1:1]",
                "7952: - Extent Scan Fragmentation ...................: 0.00%",
                "7953: - Avg. Bytes Free per Page.....................: 108.5",
                "7954: - Avg. Page Density (full).....................: 98.66%",
                "2528: " + Completed,
            },
            Run(simulation, "dbcc showcontig('h')").Messages);
    }

    [TestMethod]
    public void ShowContig_TableResults_EveryLevel()
    {
        var simulation = WithTables();
        var objectId = simulation.ExecuteScalar<int>("select object_id('c')");
        var (messages, rows) = Run(simulation, "dbcc showcontig('c') with tableresults, all_levels; select @@rowcount");
        AreEqual(
            "ObjectName | ObjectId | IndexName | IndexId | Level | Pages | Rows | MinimumRecordSize | MaximumRecordSize | AverageRecordSize | ForwardedRecords | Extents | ExtentSwitches"
            + " | AverageFreeBytes | AveragePageDensity | ScanDensity | BestCount | ActualCount | LogicalFragmentation | ExtentFragmentation",
            rows[0]);
        // The leaf's first page, its root and its other pages each in an
        // extent of their own, as an INSERT … SELECT into an empty table lays
        // them out; the root's density reads its records less two bytes over
        // 8,094, as real's does.
        StartsWith($"c | {objectId} | PK__c__", rows[1]);
        Assert.EndsWith(" | 1 | 0 | 8 | 300 | 211 | 211 | 211 | 0 | 2 | 1 | 108.5 | 98.65950086483815 | 50 | 1 | 2 | 12.5 | 50", rows[1]);
        Assert.EndsWith(" | 1 | 1 | 1 | 8 | 11 | 11 | 11 | 0 | 1 | 0 | 7992 | 1.2601927353595255 | 0 | 0 | 0 | 0 | 0", rows[2]);
        AreEqual("2", rows[^1]);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);
    }

    [TestMethod]
    public void ShowContig_AnIndexByNameOrId()
    {
        var simulation = WithTables();
        var byName = Run(simulation, "dbcc showcontig('c', 'ix') with tableresults").Rows[1];
        Assert.EndsWith(" | ix | 2 | 0 | 8 | 300 | 208 | 208 | 208 | 0 | 1 | 0 | 221 | 97.26958240672103 | 100 | 1 | 1 | 0 | 0", byName);
        AreEqual(byName, Run(simulation, "dbcc showcontig('c', 2) with tableresults").Rows[1]);
        AreEqual("7944: LEAF level scan performed.", Run(simulation, "dbcc showcontig('c', 2)").Messages[2]);
        // ALL_INDEXES reports every index whatever index is named.
        HasCount(3, Run(simulation, "dbcc showcontig('c', 'nope') with tableresults, all_indexes").Rows);
        AreEqual(8, simulation.AssertSqlError("dbcc showcontig('c', 'nope')", 7999).State);
        AreEqual("Could not find any index named '5' for table 'c'.", simulation.AssertSqlError("dbcc showcontig('c', 5)", 7999).Errors[0].Message);
    }

    [TestMethod]
    public void ShowContig_Fast_LeavesThePageFiguresOut()
    {
        var simulation = WithTables();
        CollectionAssert.AreEqual(
            new[]
            {
                "7945: - Pages Scanned................................: 8",
                "7947: - Extent Switches..............................: 1",
                "7949: - Scan Density [Best Count:Actual Count].......: 50.00% [1:2]",
                "7950: - Logical Scan Fragmentation ..................: 12.50%",
                "2528: " + Completed,
            },
            Run(simulation, "dbcc showcontig('c') with fast").Messages[3..]);
        CollectionAssert.AreEqual(
            new[]
            {
                "7945: - Pages Scanned................................: 8",
                "7946: - Extents Scanned..............................: 1",
                "7947: - Extent Switches..............................: 0",
                "7949: - Scan Density [Best Count:Actual Count].......: 100.00% [1:1]",
                "7952: - Extent Scan Fragmentation ...................: 0.00%",
                "2528: " + Completed,
            },
            Run(simulation, "dbcc showcontig('h') with fast").Messages[3..]);
        var rows = Run(simulation, "dbcc showcontig('c') with tableresults, fast, all_levels").Rows;
        HasCount(2, rows);
        Assert.EndsWith(" | 1 | 0 | 8 | NULL | NULL | NULL | NULL | NULL | NULL | 1 | NULL | NULL | 50 | 1 | 2 | 12.5 | NULL", rows[1]);
        Assert.EndsWith(" | 0 | 0 | 8 | NULL | NULL | NULL | NULL | NULL | 1 | 0 | NULL | NULL | 100 | 1 | 1 | 0 | 0", Run(simulation, "dbcc showcontig('h') with tableresults, fast").Rows[1]);
    }

    [TestMethod]
    public void ShowContig_WithoutATable_ReportsEveryTableAndQueue()
    {
        var simulation = WithTables();
        var rows = Run(simulation, "dbcc showcontig with tableresults").Rows;
        CollectionAssert.AreEqual(
            new[] { "h", "c", "QueryNotificationErrorsQueue", "EventNotificationErrorsQueue", "ServiceBrokerQueue" },
            rows.Skip(1).Select(static row => row.Split(" | ")[0]).ToArray());
        Assert.EndsWith(" | queue_clustered_index | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 100 | 0 | 0 | 0 | 0", rows[^1]);
        HasCount(3, Run(simulation, "dbcc showcontig with tableresults, all_indexes").Rows.Skip(1).Where(static row => !row.Contains("Queue", StringComparison.Ordinal)).ToArray());
        _ = simulation.ExecuteNonQuery("create user u without login");
        var denied = simulation.AssertSqlError("execute as user = 'u'; dbcc showcontig", 7983);
        AreEqual(10, denied.State);
        AreEqual("User 'u' does not have permission to run DBCC SHOWCONTIG for database 'simulated'.", denied.Errors[0].Message);
    }

    [TestMethod]
    public void ShowContig_Arguments()
    {
        var simulation = WithTables();
        simulation.ExecuteBatches("create view v as select a from h");
        _ = simulation.AssertSqlError("dbcc showcontig('nope')", 2501);
        _ = simulation.AssertSqlError("dbcc showcontig('v')", 5239);
        _ = simulation.AssertSqlError("dbcc showcontig(0)", 2573);
        _ = simulation.AssertSqlError("dbcc showcontig('h', 0, 1)", 2583);
        _ = simulation.AssertSqlError("dbcc showcontig('h') with estimateonly", 2532);
        _ = simulation.AssertSqlError("dbcc showcontig('sysrowsets')", 2501);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run(simulation, "dbcc showcontig('sys.sysrowsets')").Messages);
        // NO_INFOMSGS silences Msg 2528 alone.
        var (quietMessages, quietRows) = Run(simulation, "dbcc showcontig('dbo.h') with no_infomsgs; select @@rowcount");
        HasCount(11, quietMessages);
        AreEqual("7954: - Avg. Page Density (full).....................: 98.66%", quietMessages[^1]);
        AreEqual("0", quietRows[^1]);
    }

    [TestMethod]
    public void ShowContig_ATempTable_GoesThroughTempdb()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table #tt (a int); insert #tt values (1)").ExecuteNonQuery();
        AreEqual(2501, Throws<SimulatedSqlException>(() => connection.CreateCommand("dbcc showcontig('#tt')").ExecuteNonQuery()).Number);
        var rows = new List<string>();
        using (var reader = connection.CreateCommand("dbcc showcontig('tempdb..#tt') with tableresults").ExecuteReader())
        {
            while (reader.Read())
                rows.Add($"{reader.GetString(0)} | {reader.GetInt64(5)}");
        }
        StartsWith("#tt___", rows[0]);
        Assert.EndsWith(" | 1", rows[0]);
    }

    [TestMethod]
    public void ShowContig_APartitionedTable_ReportsItsFirstPartition()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (100, 200);
            create partition scheme ps as partition pf all to ([primary]);
            create table p (a int primary key, b char(100)) on ps(a);
            insert p select top 300 row_number() over (order by (select 1)), 'x' from sys.all_columns;
            """);
        Assert.EndsWith(" | 1 | 0 | 2 | 100 | 111 | 111 | 111 | 0 | 2 | 1 | 2446 | 69.78008401284903 | 50 | 1 | 2 | 50 | 50",
            Run(simulation, "dbcc showcontig('p') with tableresults").Rows[1]);
    }

    [TestMethod]
    public void ShowContig_AForwardingHeap_CountsItsStubs()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table f (a int, b varchar(400));
            insert f select top 500 row_number() over (order by (select 1)), 'x' from sys.all_columns;
            update f set b = replicate('y', 300);
            """);
        var row = Run(simulation, "dbcc showcontig('f') with tableresults").Rows[1].Split(" | ");
        var stats = Run(simulation, "select record_count, forwarded_record_count, page_count from sys.dm_db_index_physical_stats(db_id(), object_id('f'), 0, null, 'DETAILED')").Rows[1].Split(" | ");
        CollectionAssert.AreEqual(stats, new[] { row[6], row[10], row[5] });
        AreEqual("9", row[7]);
    }

    [TestMethod]
    public void Ind_ListsEachUnitsPages()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table c (a int primary key, b varchar(max), c varchar(5000), d varchar(5000));
            insert c select top 3 row_number() over (order by (select 1)), replicate(cast('x' as varchar(max)), 9000), replicate('y', 5000), replicate('z', 5000) from sys.all_columns;
            """);
        var (messages, rows) = Run(simulation, "dbcc ind(0, 'c', 1); select @@rowcount");
        AreEqual("PageFID | PagePID | IAMFID | IAMPID | ObjectID | IndexID | PartitionNumber | PartitionID | iam_chain_type | PageType | IndexLevel | NextPageFID | NextPagePID | PrevPageFID | PrevPagePID", rows[0]);
        var pages = rows.Skip(1).Take(14).Select(static row => row.Split(" | ")).ToArray();
        // In-row: the IAM page, the first leaf page, the root, the other two
        // leaf pages chained to it; then the row-overflow unit's three pages
        // and the LOB unit's four, each unit's IAM page first.
        CollectionAssert.AreEqual(
            new[] { "In-row data:10:NULL", "In-row data:1:0", "In-row data:2:1", "In-row data:1:0", "In-row data:1:0", "Row-overflow data:10:NULL", "Row-overflow data:3:0", "Row-overflow data:3:0", "Row-overflow data:3:0", "LOB data:10:NULL", "LOB data:3:0", "LOB data:3:0", "LOB data:3:0", "LOB data:3:0" },
            pages.Select(static page => $"{page[8]}:{page[9]}:{page[10]}").ToArray());
        var id = pages.Select(static page => int.Parse(page[1], CultureInfo.InvariantCulture)).ToArray();
        CollectionAssert.AreEqual(new[] { "NULL", "NULL" }, pages[0][2..4]);
        CollectionAssert.AreEqual(new[] { "1", id[0].ToInvariantString() }, pages[1][2..4]);
        CollectionAssert.AreEqual(new[] { "1", id[3].ToInvariantString(), "0", "0" }, pages[1][11..]);
        CollectionAssert.AreEqual(new[] { "1", id[4].ToInvariantString(), "1", id[1].ToInvariantString() }, pages[3][11..]);
        CollectionAssert.AreEqual(new[] { "0", "0", "1", id[3].ToInvariantString() }, pages[4][11..]);
        AreEqual("14", rows[^1]);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);

        // The page surfaces agree on every id.
        AreEqual(string.Join(",", id.Order()), string.Join(",", Run(simulation, """
            select b.page_id from sys.dm_os_buffer_descriptors b join sys.allocation_units a on a.allocation_unit_id = b.allocation_unit_id
            join sys.partitions p on p.partition_id = a.container_id where b.database_id = db_id() and p.object_id = object_id('c') order by b.page_id
            """).Rows.Skip(1)));
        AreEqual(string.Join(",", id.Order()), string.Join(",", Run(simulation, """
            select allocated_page_page_id from sys.dm_db_database_page_allocations(db_id(), object_id('c'), null, null, 'LIMITED') where is_allocated = 1 order by 1
            """).Rows.Skip(1)));
        AreEqual($"{id[1]}:{id[2]}:{id[0]}", (string)simulation.ExecuteScalar("""
            select concat(cast(cast(reverse(substring(first_page, 1, 4)) as binary(4)) as int), ':', cast(cast(reverse(substring(root_page, 1, 4)) as binary(4)) as int), ':',
                cast(cast(reverse(substring(first_iam_page, 1, 4)) as binary(4)) as int))
            from sys.system_internals_allocation_units s join sys.partitions p on p.partition_id = s.container_id where p.object_id = object_id('c') and s.type = 1
            """)!);
    }

    [TestMethod]
    public void Ind_Arguments()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table c (a int primary key, b int); insert c values (1, 1); create index ix on c(b); create table h (a int)");
        simulation.ExecuteBatches("create view v as select a from c", "create user u without login");
        var iams = Run(simulation, "dbcc ind(0, 'c', -2)").Rows;
        HasCount(3, iams);
        IsTrue(iams.Skip(1).All(static row => row.Split(" | ")[9] == "10"));
        HasCount(5, Run(simulation, "dbcc ind(0, 'c', -1)").Rows);
        AreEqual(Run(simulation, "dbcc ind(0, 'c', 1)").Rows[1], Run(simulation, "dbcc ind(0, 'c', 0)").Rows[1]);
        AreEqual(Run(simulation, "dbcc ind(0, 'c', 1)").Rows[1], Run(simulation, "dbcc ind('simulated', 'dbo.c', 1, 0) with estimateonly").Rows[1]);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run(simulation, "dbcc ind(0, 'h', 0)").Messages);
        IsEmpty(Run(simulation, "dbcc ind(0, 'c', 2) with no_infomsgs").Messages);
        IsGreaterThan(2, Run(simulation, "dbcc ind(0, 'sys.sysschobjs', 1)").Rows.Count);
        _ = simulation.AssertSqlError("dbcc ind(0, 'c')", 2583);
        AreEqual("Parameter 3 is incorrect for this DBCC statement.", simulation.AssertSqlError("dbcc ind(0, 'c', 'x')", 2560).Errors[0].Message);
        _ = simulation.AssertSqlError("dbcc ind(0, 'c', null)", 2560);
        _ = simulation.AssertSqlError("dbcc ind(0, 'c', -3)", 2560);
        AreEqual(7, simulation.AssertSqlError("dbcc ind(0, 'c', 7)", 7999).State);
        AreEqual(5, simulation.AssertSqlError("dbcc ind(0, 'c', 1, 2)", 2586).State);
        AreEqual("The invalid partition number -1 was specified.", simulation.AssertSqlError("dbcc ind(0, 'c', 1, -1)", 2587).Errors[0].Message);
        _ = simulation.AssertSqlError("dbcc ind(0, 'c', 1, null)", 2560);
        _ = simulation.AssertSqlError("dbcc ind('nodb', 'c', 1)", 2520);
        _ = simulation.AssertSqlError("dbcc ind(0, 99999, 1)", 2573);
        _ = simulation.AssertSqlError("dbcc ind(0, 'v', 1)", 5239);
        _ = simulation.AssertSqlError("dbcc ind(0, 'nope', 1)", 2501);
        AreEqual("User 'u' does not have permission to run DBCC ind.", simulation.AssertSqlError("execute as user = 'u'; dbcc ind(0, 'c', 1)", 2571).Errors[0].Message);
    }

    [TestMethod]
    public void PageAllocations_ListEveryPageOfTheUnitsExtents()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table h (a int); insert h values (1); create table c (a int primary key)");
        var rows = Run(simulation, """
            select allocation_unit_type_desc, clone_state_desc, is_allocated, is_iam_page, is_mixed_page_allocation, page_free_space_percent, page_type_desc, page_level, is_page_compressed, has_ghost_records
            from sys.dm_db_database_page_allocations(db_id(), object_id('h'), null, null, 'DETAILED') order by is_iam_page desc, allocated_page_page_id
            """).Rows;
        CollectionAssert.AreEqual(
            new[]
            {
                "IN_ROW_DATA | SECONDARY | 1 | 1 | 1 | 100 | IAM_PAGE | 0 | 0 | 0",
                "IN_ROW_DATA | SECONDARY | 1 | 0 | 0 | 50 | DATA_PAGE | 0 | 0 | 0",
                "IN_ROW_DATA | SECONDARY | 0 | 0 | 0 | 100 | NULL | NULL | 0 | 0",
            },
            rows.Skip(1).Take(3).ToArray());
        HasCount(10, rows);
        AreEqual("IN_ROW_DATA | SECONDARY | 1 | 0 | 0 | 50 | NULL | NULL | NULL | 0", Run(simulation, """
            select allocation_unit_type_desc, clone_state_desc, is_allocated, is_iam_page, is_mixed_page_allocation, page_free_space_percent, page_type_desc, page_level, is_page_compressed, has_ghost_records
            from sys.dm_db_database_page_allocations(db_id(), object_id('h'), 0, 1, null) where is_iam_page = 0 and is_allocated = 1
            """).Rows[1]);
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from sys.dm_db_database_page_allocations(db_id(), object_id('c'), null, null, 'LIMITED')"));
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from sys.dm_db_database_page_allocations(db_id(), 12345, null, null, 'limited')"));
        IsGreaterThan(300, simulation.ExecuteScalar<int>("select count(*) from sys.dm_db_database_page_allocations(db_id(), null, null, null, 'LIMITED')"));
        AreEqual(110, simulation.AssertSqlError("select * from sys.dm_db_database_page_allocations(null, null, null, null, 'LIMITED')", 2561).State);
        AreEqual(110, simulation.AssertSqlError("select * from sys.dm_db_database_page_allocations(-1, null, null, null, 'LIMITED')", 2561).State);
        AreEqual(111, simulation.AssertSqlError("select * from sys.dm_db_database_page_allocations(db_id(), null, null, null, 'FOO')", 2561).State);
        _ = simulation.AssertSqlError("select * from sys.dm_db_database_page_allocations(999, null, null, null, 'LIMITED')", 913);
        AreEqual(3, simulation.AssertSqlError("select * from sys.dm_db_database_page_allocations(db_id(), null, null, null)", 313).State);
        _ = simulation.ExecuteNonQuery("create user u without login");
        _ = simulation.AssertSqlError("execute as user = 'u'; select * from sys.dm_db_database_page_allocations(db_id(), null, null, null, 'LIMITED')", 15562);
    }

    [TestMethod]
    public void BufferDescriptors_CarryEachPagesLevelRecordsAndFreeSpace()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table c (a int primary key, b char(500)); insert c select top 100 row_number() over (order by (select 1)), 'x' from sys.all_columns");
        CollectionAssert.AreEqual(
            new[] { "IAM_PAGE | 2 | 6 | 0", "DATA_PAGE | 15 | 401 | 0", "INDEX_PAGE | 7 | 8005 | 1", "DATA_PAGE | 15 | 401 | 0", "DATA_PAGE | 15 | 401 | 0", "DATA_PAGE | 15 | 401 | 0", "DATA_PAGE | 15 | 401 | 0", "DATA_PAGE | 15 | 401 | 0", "DATA_PAGE | 10 | 2966 | 0" },
            Run(simulation, """
                select page_type, row_count, free_space_in_bytes, page_level from sys.dm_os_buffer_descriptors b join sys.allocation_units a on a.allocation_unit_id = b.allocation_unit_id
                join sys.partitions p on p.partition_id = a.container_id where b.database_id = db_id() and p.object_id = object_id('c') order by b.page_id
                """).Rows.Skip(1).ToArray());
    }

    [TestMethod]
    public void CheckAlloc_ReportsEachAllocationUnit()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create schema s", "create table s.t (a int primary key, b varchar(max)); insert s.t values (1, replicate(cast('x' as varchar(max)), 9000)); create index ix on s.t(a)");
        var objectId = simulation.ExecuteScalar<int>("select object_id('s.t')");
        var messages = Run(simulation, "dbcc checkalloc").Messages;
        var at = messages.IndexOf($"7923: Table s.t                Object ID {objectId}.");
        AreEqual("7922: ***************************************************************", messages[at - 1]);
        var section = messages[(at + 1)..(at + 8)];
        StartsWith("7924: Index ID 1, partition ID ", section[0]);
        Assert.Contains("(type In-row data). FirstIAM (1:", section[0]);
        Assert.EndsWith("Dpages 0.", section[0]);
        Assert.EndsWith("(type In-row data). 2 pages used in 1 dedicated extents.", section[1]);
        Assert.Contains("(type LOB data). FirstIAM (1:", section[2]);
        Assert.EndsWith("Dpages 3.", section[2]);
        Assert.EndsWith("(type LOB data). 3 pages used in 1 dedicated extents.", section[3]);
        StartsWith("7924: Index ID 2, ", section[4]);
        Assert.EndsWith("Dpages 1.", section[4]);
        AreEqual("7927: Total number of extents is 3.", section[6]);

        // The file's totals add up the units': each extent a unit holds whole
        // and every mixed extent, the pages in use, and the pages reserved.
        long extents = 0, pages = 0, mixed = 0;
        foreach (var line in messages.Where(static m => m.StartsWith("8916:", StringComparison.Ordinal) || m.StartsWith("8917:", StringComparison.Ordinal)))
        {
            var figures = line[(line.LastIndexOf("extents ", StringComparison.Ordinal) + 8)..^1].Split(", ");
            extents += long.Parse(figures[0], CultureInfo.InvariantCulture);
            pages += long.Parse(figures[1]["pages ".Length..], CultureInfo.InvariantCulture);
            mixed += long.Parse(figures[2]["mixed extent pages ".Length..], CultureInfo.InvariantCulture);
        }
        var mixedExtents = (mixed + 7) / 8;
        Assert.Contains($"2538: File 1. The number of extents = {extents + mixedExtents}, used pages = {pages}, and reserved pages = {(8 * extents) + mixed}.", messages);
        Assert.Contains($"8915:            File 1 (number of mixed extents = {mixedExtents}, mixed pages = {mixed}).", messages);
        Assert.Contains("8917:     Object ID 99, index ID 0, partition ID 0, alloc unit ID 6488064 (type Unknown), index extents 1, pages 7, mixed extent pages 1.", messages);
        AreEqual(pages, simulation.ExecuteScalar<long>("select sum(used_pages) + 7 from sys.allocation_units"));
    }

    [TestMethod]
    public void CheckAlloc_TableResults()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int); insert t values (1)");
        var objectId = simulation.ExecuteScalar<int>("select object_id('t')");
        var databaseId = simulation.ExecuteScalar<short>("select db_id()");
        var (messages, rows) = Run(simulation, "dbcc checkalloc with tableresults");
        var heading = rows.Find(row => row.StartsWith("7923 | ", StringComparison.Ordinal) && row.Contains($"Object ID {objectId}.", StringComparison.Ordinal))!.Split(" | ");
        CollectionAssert.AreEqual(new[] { "NULL", "0", databaseId.ToInvariantString(), "1", objectId.ToInvariantString(), "0" }, heading[4..10]);
        CollectionAssert.AreEqual(new[] { databaseId.ToInvariantString(), "0", "0", "0", "0", databaseId.ToInvariantString(), "0", "0", "0", "0", "2" }, heading[12..]);
        var iam = rows[rows.IndexOf(string.Join(" | ", heading)) + 1].Split(" | ");
        CollectionAssert.AreEqual(new[] { "1", simulation.ExecuteScalar<int>("""
            select cast(cast(reverse(substring(first_iam_page, 1, 4)) as binary(4)) as int) from sys.system_internals_allocation_units s
            join sys.partitions p on p.partition_id = s.container_id where p.object_id = object_id('t')
            """).ToInvariantString() }, iam[14..16]);
        DoesNotContain(static row => row.StartsWith("7922 | ", StringComparison.Ordinal) || row.StartsWith("2536 | ", StringComparison.Ordinal) || row.StartsWith("2538 | ", StringComparison.Ordinal), rows);
        var file = rows.Find(static row => row.StartsWith("8983 | ", StringComparison.Ordinal))!.Split(" | ");
        StartsWith("File 1. Extents ", file[3]);
        CollectionAssert.AreEqual(new[] { "-2147483648", "-2147483648", "0", "0", databaseId.ToInvariantString(), "0", "1" }, file[8..15]);
        CollectionAssert.AreEqual(new[] { "8918", "8989" }, rows[^2..].Select(static row => row.Split(" | ")[0]).ToArray());
        CollectionAssert.AreEqual(new[] { "32767", "32767", "2147483647", "1" }, rows[^2].Split(" | ")[13..17]);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);
        IsEmpty(Run(simulation, "dbcc checkalloc with tableresults, no_infomsgs").Rows);
    }
}
