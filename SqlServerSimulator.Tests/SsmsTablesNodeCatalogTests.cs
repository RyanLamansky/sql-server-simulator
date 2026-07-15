using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the catalog surface SSMS Object Explorer's Tables node reads via
/// SMO: <c>sys.all_columns</c> (user-object parity with <c>sys.columns</c> plus
/// <c>is_sparse</c>), <c>sys.data_spaces</c> (single PRIMARY filegroup), the
/// table-flavor flags on <c>sys.tables</c> (<c>is_memory_optimized</c> /
/// <c>is_filetable</c> / <c>is_external</c> / <c>is_node</c> / <c>is_edge</c> /
/// <c>ledger_type</c>), and the QUOTENAME collation-propagation fix the Urn
/// concatenation exposed. Closes with a trimmed representative of SMO's Tables
/// node query. Shapes/values probed against SQL Server 2025 (2026-07-15).
/// </summary>
[TestClass]
public sealed class SsmsTablesNodeCatalogTests
{
    [TestMethod]
    public void AllColumns_ExposesIsSparse_ForUserColumn()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key, x int null);
            select cast(is_sparse as int) from sys.all_columns
            where object_id = object_id('t') and name = 'x'
            """));

    [TestMethod]
    public void AllColumns_MatchesColumnsRowSet_ForUserTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null primary key, b nvarchar(20) null, c decimal(10, 2) null)");
        AreEqual(
            sim.ExecuteScalar<int>("select count(*) from sys.columns where object_id = object_id('t')"),
            sim.ExecuteScalar<int>("select count(*) from sys.all_columns where object_id = object_id('t')"));
    }

    [TestMethod]
    public void DataSpaces_ReturnsPrimaryFilegroup()
    {
        using var reader = new Simulation().ExecuteReader(
            "select name, data_space_id, type, type_desc, is_default, is_system from sys.data_spaces");

        IsTrue(reader.Read());
        AreEqual("PRIMARY", reader.GetString(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual("FG", reader.GetString(2).TrimEnd());
        AreEqual("ROWS_FILEGROUP", reader.GetString(3));
        IsTrue(reader.GetBoolean(4));
        IsFalse(reader.GetBoolean(5));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// SMO's IsPartitioned probe joins sys.indexes.data_space_id to
    /// sys.data_spaces and compares type to 'PS'. The single modeled row is a
    /// row-filegroup ('FG'), so a PK's index always resolves to a non-partition
    /// data space.
    /// </summary>
    [TestMethod]
    public void DataSpaces_JoinFromIndex_ResolvesToFilegroupNotPartitionScheme()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key);
            select cast(case when 'PS' = dsidx.type then 1 else 0 end as int)
            from sys.tables tbl
            inner join sys.indexes idx on idx.object_id = tbl.object_id and idx.index_id < 2
            left outer join sys.data_spaces dsidx on dsidx.data_space_id = idx.data_space_id
            where tbl.object_id = object_id('t')
            """));

    [TestMethod]
    public void Tables_TableFlavorFlags_AllZeroForUserTable()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int not null primary key);
            select cast(is_memory_optimized as int), is_filetable, is_external, is_node, is_edge, ledger_type
            from sys.tables where object_id = object_id('t')
            """);

        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        IsFalse(reader.GetBoolean(1));
        IsFalse(reader.GetBoolean(2));
        IsFalse(reader.GetBoolean(3));
        IsFalse(reader.GetBoolean(4));
        AreEqual((byte)0, reader.GetByte(5));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// Regression for the QUOTENAME collation fix. Under a non-baseline
    /// database collation a plain string literal carries the database collation
    /// while the sysname catalog column carries the baseline; before the fix
    /// QUOTENAME dropped the input's Implicit coercibility and the Urn's
    /// literal + QUOTENAME(...) concatenation raised Msg 468.
    /// </summary>
    [TestMethod]
    public void QuoteName_UnderNonBaselineCollation_ConcatWithLiteralSucceeds()
    {
        var sim = new Simulation { ServerCollationName = "Latin1_General_100_CI_AS" };
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        AreEqual("x[t]", sim.ExecuteScalar("select 'x' + quotename(name) from sys.tables where name = 't'"));
    }

    /// <summary>
    /// Trimmed representative of SMO's Tables node query: the correlated
    /// nonclustered-index probe, the is_ms_shipped + extended-property CASE
    /// filter, and the sys.all_columns → sys.types join for XML detection. A
    /// table with a clustered PK, a nonclustered index, and an xml column
    /// projects HasClusteredIndex / HasPrimaryClusteredIndex / HasNonClustered /
    /// HasXmlData = 1 and passes the exclusion filter.
    /// </summary>
    [TestMethod]
    public void SmoTablesNodeShape_ProjectsExpectedFlags()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, payload xml null, note nvarchar(50) null);
            create index ix_note on t (note);
            """);

        using var reader = sim.ExecuteReader("""
            select
                tbl.name,
                cast(case idx.index_id when 1 then 1 else 0 end as int) as HasClusteredIndex,
                cast(case idx.index_id when 1 then case when (idx.is_primary_key + 2 * idx.is_unique_constraint = 1) then 1 else 0 end else 0 end as int) as HasPrimaryClusteredIndex,
                cast(isnull((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0), 0) as int) as HasNonClusteredIndex,
                cast(isnull((select top 1 1 from sys.all_columns clmns join sys.types usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) as int) as HasXmlData,
                cast(isnull((select distinct 1 from sys.all_columns where object_id = tbl.object_id and is_sparse = 1), 0) as int) as HasSparse
            from sys.tables tbl
            inner join sys.indexes idx on idx.object_id = tbl.object_id and idx.index_id < 2
            where tbl.object_id = object_id('t')
              and (cast(case
                    when tbl.is_ms_shipped = 1 then 1
                    when exists (select 1 from sys.extended_properties
                        where major_id = tbl.object_id and minor_id = 0 and class = 1
                          and name = N'microsoft_database_tools_support') then 1
                    else 0 end as bit) = 0
                   and tbl.is_filetable = 0 and tbl.temporal_type = 0 and cast(tbl.is_node as bit) = 0)
            """);

        IsTrue(reader.Read());
        AreEqual("t", reader.GetString(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(1, reader.GetInt32(2));
        AreEqual(1, reader.GetInt32(3));
        AreEqual(1, reader.GetInt32(4));
        AreEqual(0, reader.GetInt32(5));
        IsFalse(reader.Read());
    }

    // ---- Columns / Keys / Indexes / Triggers sub-node round (2026-07-15) ----

    /// <summary>
    /// SMO's column-node query projects <c>CAST(clmns.precision AS int)</c>.
    /// PRECISION is a reserved keyword but — probe-confirmed against SQL Server
    /// 2025 — is fully usable as an identifier in every position (dotted member,
    /// bare projection, alias, ORDER BY), unlike genuinely-reserved words such
    /// as FROM / USER which raise Msg 156.
    /// </summary>
    [TestMethod]
    public void AllColumns_PrecisionMemberReference_Projects()
        => AreEqual(10, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key, amount decimal(10, 2) null);
            select cast(clmns.precision as int) from sys.all_columns clmns
            where clmns.object_id = object_id('t') and clmns.name = 'amount'
            """));

    [TestMethod]
    public void Precision_AsColumnName_RoundTrips()
        => AreEqual(7, new Simulation().ExecuteScalar<int>("""
            create table t (precision int not null);
            insert t (precision) values (7);
            select precision from t
            """));

    [TestMethod]
    public void Precision_AsColumnAlias_Projects()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("select 1 as precision"));

    /// <summary>
    /// The Columns node reads the SQL-Server-2025 vector / ledger / column-set /
    /// XML surface off sys.all_columns. None are modeled, so the constants are
    /// 0 (is_xml_document / is_column_set / is_dropped_ledger_column),
    /// xml_collection_id 0, and the vector_* pair NULL.
    /// </summary>
    [TestMethod]
    public void AllColumns_ExposesVectorLedgerColumnSetConstants()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        using var reader = sim.ExecuteReader("""
            select is_xml_document, xml_collection_id, is_column_set,
                   is_dropped_ledger_column, vector_dimensions, vector_base_type_desc
            from sys.all_columns where object_id = object_id('t') and name = 'id'
            """);

        IsTrue(reader.Read());
        IsFalse(reader.GetBoolean(0));
        AreEqual(0, reader.GetInt32(1));
        IsFalse(reader.GetBoolean(2));
        IsFalse(reader.GetBoolean(3));
        IsTrue(reader.IsDBNull(4));
        IsTrue(reader.IsDBNull(5));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// The index / key / FK sub-node queries branch on
    /// <c>sys.table_types.is_memory_optimized</c> (constant 0 — no
    /// memory-optimized types modeled) keyed by <c>type_table_object_id</c>,
    /// which lines up with the table type's OBJECT-model id.
    /// </summary>
    [TestMethod]
    public void TableTypes_ExposesIsMemoryOptimizedAndObjectId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type dbo.OrderLines as table (line_id int not null, qty int null)");
        using var reader = sim.ExecuteReader("""
            select is_memory_optimized,
                   cast(case when type_table_object_id > 0 then 1 else 0 end as int)
            from sys.table_types where name = 'OrderLines'
            """);

        IsTrue(reader.Read());
        IsFalse(reader.GetBoolean(0));
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// The column-node base-type join arm tests <c>baset.is_assembly_type = 1</c>.
    /// It is 0 for ordinary built-ins and 1 only for the CLR-backed system types
    /// (hierarchyid / geometry / geography).
    /// </summary>
    [TestMethod]
    public void SysTypes_IsAssemblyType_ZeroForIntOneForHierarchyId()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar<int>("select cast(is_assembly_type as int) from sys.types where name = 'int'"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(is_assembly_type as int) from sys.types where name = 'hierarchyid'"));
    }

    /// <summary>
    /// The Triggers sub-node query LEFT JOINs sys.system_sql_modules to detect a
    /// WITH ENCRYPTION module. The simulator ships no system-defined modules, so
    /// the view is always empty.
    /// </summary>
    [TestMethod]
    public void SystemSqlModules_IsEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("select count(*) from sys.system_sql_modules"));

    /// <summary>
    /// sys.partitions carries the table's live row count on the rows column
    /// and tracks INSERTs made after the table was populated — the partition
    /// row is projected from HeapTable.Heap.RowCount at read time, not cached.
    /// </summary>
    [TestMethod]
    public void Partitions_ReportsLiveRowCount_AndUpdatesAfterInsert()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, x int null)");
        _ = sim.ExecuteNonQuery("insert into t (id, x) values (1, 10), (2, 20), (3, 30)");
        AreEqual(3L, sim.ExecuteScalar<long>(
            "select rows from sys.partitions where object_id = object_id('t') and index_id = 1"));
        _ = sim.ExecuteNonQuery("insert into t (id, x) values (4, 40), (5, 50)");
        AreEqual(5L, sim.ExecuteScalar<long>(
            "select rows from sys.partitions where object_id = object_id('t') and index_id = 1"));
    }

    /// <summary>
    /// sys.partitions emits one row per (object_id, index_id) that sys.indexes
    /// reports — the clustered PK plus every nonclustered index — with
    /// partition_number = 1 (single, unpartitioned partition).
    /// </summary>
    [TestMethod]
    public void Partitions_OneRowPerIndex_MirrorsSysIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, a int null, b int null)");
        _ = sim.ExecuteNonQuery("create index ix_a on t(a)");
        _ = sim.ExecuteNonQuery("create unique index ux_b on t(b)");
        AreEqual(
            sim.ExecuteScalar<int>("select count(*) from sys.indexes where object_id = object_id('t')"),
            sim.ExecuteScalar<int>("select count(*) from sys.partitions where object_id = object_id('t')"));
        AreEqual(3, sim.ExecuteScalar<int>("select count(*) from sys.partitions where object_id = object_id('t')"));
    }

    /// <summary>
    /// A table with no PRIMARY KEY is a heap: sys.partitions carries an
    /// index_id = 0 row with the heap's live row count, matching sys.indexes'
    /// HEAP row.
    /// </summary>
    [TestMethod]
    public void Partitions_HeapTable_ReportsHeapRowWithRowCount()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table h (a int null)");
        _ = sim.ExecuteNonQuery("insert into h values (1), (2)");
        AreEqual(2L, sim.ExecuteScalar<long>(
            "select rows from sys.partitions where object_id = object_id('h') and index_id = 0"));
    }

    /// <summary>
    /// Compression isn't modeled: every sys.partitions row reports
    /// data_compression = 0 (NONE) and xml_compression = 0 (OFF), so SMO's
    /// HasCompressedPartitions probe filter matches nothing.
    /// </summary>
    [TestMethod]
    public void Partitions_CompressionColumns_ReportNone()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int not null primary key);
            select cast(data_compression as int), cast(xml_compression as int)
            from sys.partitions where object_id = object_id('t') and index_id = 1
            """);
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// sys.allocation_units emits one IN_ROW_DATA row per sys.partitions row,
    /// joined on container_id = partition_id (the key SSMS's space query uses).
    /// data_space_id = 1 (the single PRIMARY filegroup).
    /// </summary>
    [TestMethod]
    public void AllocationUnits_OneInRowUnitPerPartition_JoinsOnContainerId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, a int null, b int null)");
        _ = sim.ExecuteNonQuery("create index ix_a on t(a)");
        _ = sim.ExecuteNonQuery("create unique index ux_b on t(b)");
        // Three partitions (clustered PK + two indexes) → three IN_ROW units,
        // each joining to its partition on container_id = partition_id.
        AreEqual(3, sim.ExecuteScalar<int>("""
            select count(*)
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t') and a.type = 1
            """));
        AreEqual("IN_ROW_DATA", sim.ExecuteScalar("""
            select distinct a.type_desc
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t') and a.type = 1
            """));
        AreEqual(1, sim.ExecuteScalar<int>("""
            select distinct a.data_space_id
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t')
            """));
    }

    /// <summary>
    /// allocation_unit page counts read the live heap page total, so they grow
    /// as rows are inserted — mirroring sys.partitions' live rows column.
    /// </summary>
    [TestMethod]
    public void AllocationUnits_PageCountsMoveAfterInsert()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, filler nvarchar(2000) null)");
        _ = sim.ExecuteNonQuery("insert into t (id, filler) select value, replicate(N'x', 1000) from generate_series(1, 5)");
        var before = sim.ExecuteScalar<long>("""
            select a.total_pages
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t') and a.type = 1
            """);
        _ = sim.ExecuteNonQuery("insert into t (id, filler) select value, replicate(N'x', 1000) from generate_series(6, 200)");
        var after = sim.ExecuteScalar<long>("""
            select a.total_pages
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t') and a.type = 1
            """);
        IsGreaterThan(before, after);
        // IN_ROW total_pages = used_pages = data_pages (no separate index/IAM
        // overhead modeled).
        using var reader = sim.ExecuteReader("""
            select a.total_pages, a.used_pages, a.data_pages
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t') and a.type = 1
            """);
        IsTrue(reader.Read());
        AreEqual(after, reader.GetInt64(0));
        AreEqual(after, reader.GetInt64(1));
        AreEqual(after, reader.GetInt64(2));
    }

    /// <summary>
    /// A table with off-row LOB pages surfaces one LOB_DATA unit (type 2) on
    /// the base partition with data_pages = 0, matching real SQL Server.
    /// </summary>
    [TestMethod]
    public void AllocationUnits_LobColumn_SurfacesLobDataUnit()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, big nvarchar(max) null)");
        _ = sim.ExecuteNonQuery("insert into t (id, big) select value, replicate(cast(N'y' as nvarchar(max)), 10000) from generate_series(1, 50)");
        using var reader = sim.ExecuteReader("""
            select a.type_desc, a.total_pages, a.data_pages
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            where p.object_id = object_id('t') and a.type = 2
            """);
        IsTrue(reader.Read());
        AreEqual("LOB_DATA", reader.GetString(0));
        IsGreaterThan(0L, reader.GetInt64(1));
        AreEqual(0L, reader.GetInt64(2));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// Self-consistency contract: the data file's size (sys.database_files) must
    /// cover the SUM of allocation-unit total_pages, so SSMS's
    /// SpaceAvailable = DbSize − SpaceUsed stays non-negative.
    /// </summary>
    [TestMethod]
    public void AllocationUnits_TotalPages_NeverExceedDataFileSize()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, filler nvarchar(2000) null)");
        _ = sim.ExecuteNonQuery("insert into t (id, filler) select value, replicate(N'x', 1000) from generate_series(1, 300)");
        var dbSize = sim.ExecuteScalar<long>("select cast(size as bigint) from sys.database_files where type = 0");
        var spaceUsed = sim.ExecuteScalar<long>("""
            select sum(a.total_pages)
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            """);
        IsGreaterThanOrEqualTo(spaceUsed, dbSize);
    }

    /// <summary>
    /// sys.stats emits one row per index sys.indexes reports (excluding the
    /// heap), with stats_id = index_id and name = index name. Auto-created
    /// column statistics (_WA_Sys_*) aren't modeled, so no_recompute /
    /// auto_created are 0 and only index-backing statistics appear.
    /// </summary>
    [TestMethod]
    public void Stats_OneRowPerIndex_SharesIndexIdAndName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, a int null)");
        _ = sim.ExecuteNonQuery("create index ix_a on t(a)");
        AreEqual(2, sim.ExecuteScalar<int>("select count(*) from sys.stats where object_id = object_id('t')"));
        AreEqual("ix_a", (string?)sim.ExecuteScalar(
            "select name from sys.stats where object_id = object_id('t') and stats_id = 2"));
        AreEqual(0, sim.ExecuteScalar<int>(
            "select cast(no_recompute as int) from sys.stats where object_id = object_id('t') and stats_id = 1"));
    }

    /// <summary>
    /// A heap with no indexes has no sys.stats rows — the heap itself carries
    /// no statistic and column-only auto-stats aren't modeled.
    /// </summary>
    [TestMethod]
    public void Stats_HeapTableWithNoIndexes_IsEmpty()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table h (a int null)");
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from sys.stats where object_id = object_id('h')"));
    }

    /// <summary>
    /// The index-feature views for capabilities the simulator doesn't model
    /// resolve (no Msg 208) and return zero rows, so SMO's index-scripting
    /// mega-query LEFT JOINs them without incident.
    /// </summary>
    [TestMethod]
    public void UnmodeledIndexFeatureViews_ResolveEmpty()
    {
        var sim = new Simulation();
        foreach (var view in new[]
        {
            "internal_tables", "hash_indexes", "json_indexes", "index_resumable_operations",
            "selective_xml_index_paths", "filetable_system_defined_objects",
        })
        {
            AreEqual(0, sim.ExecuteScalar<int>($"select count(*) from sys.{view}"));
        }
    }

    /// <summary>
    /// sys.indexes.compression_delay is NULL for every rowstore index
    /// (columnstore, which carries a minute delay, isn't modeled) — SMO reads
    /// it as CAST(i.compression_delay AS int) with no ISNULL wrapper.
    /// </summary>
    [TestMethod]
    public void Indexes_CompressionDelay_IsNullForRowstore()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key);
            select cast(case when compression_delay is null then 1 else 0 end as int)
            from sys.indexes where object_id = object_id('t') and index_id = 1
            """));

    /// <summary>
    /// INDEXPROPERTY 'IsFulltextKey' and 'IsOptimizedForSequentialKey' return
    /// 0 (not NULL) for a modeled index — SMO's index-scripting query reads
    /// IsFulltextKey without an ISNULL wrapper, so NULL would surface a wrong
    /// value.
    /// </summary>
    [TestMethod]
    public void IndexProperty_FulltextKeyAndSequentialKey_ReturnZero()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, a int null)");
        _ = sim.ExecuteNonQuery("create index ix_a on t(a)");
        AreEqual(0, sim.ExecuteScalar<int>(
            "select cast(indexproperty(object_id('t'), 'ix_a', 'IsFulltextKey') as int)"));
        AreEqual(0, sim.ExecuteScalar<int>(
            "select cast(indexproperty(object_id('t'), 'ix_a', 'IsOptimizedForSequentialKey') as int)"));
    }
}
