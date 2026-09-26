using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Columnstore indexes: what the catalog reports for one, the refusals real
/// raises declaring, rebuilding or setting one, and that a table carrying one
/// reads and writes as before. Every expectation probed 2026-09-26 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class ColumnstoreIndexTests
{
    [TestMethod]
    [DataRow("create nonclustered columnstore index n on t (a, b)", "0:HEAP:,2:NONCLUSTERED COLUMNSTORE:0")]
    [DataRow("create columnstore index n on t (a)", "0:HEAP:,2:NONCLUSTERED COLUMNSTORE:0")]
    [DataRow("create clustered columnstore index c on t", "1:CLUSTERED COLUMNSTORE:0")]
    [DataRow("create clustered columnstore index c on t with (compression_delay = 10 minutes)", "1:CLUSTERED COLUMNSTORE:10")]
    public void SysIndexes_ReportsTheColumnstoreTypes(string create, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b varchar(10), c int); " + create);
        AreEqual(expected, sim.ExecuteScalar("""
            select string_agg(concat(index_id, ':', type_desc, ':', compression_delay), ',') within group (order by index_id)
            from sys.indexes where object_id = object_id('t')
            """));
    }

    [TestMethod]
    [DataRow("create nonclustered columnstore index n on t (b, a) order (a)", "2:2:0:1:0,2:1:0:1:1")]
    [DataRow("create clustered columnstore index c on t order (b)", "1:1:0:1:0,1:2:0:1:1,1:3:0:1:0")]
    public void SysIndexColumns_ListsEveryColumnAsIncluded(string create, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b varchar(10), c int); " + create);
        AreEqual(expected, sim.ExecuteScalar("""
            select string_agg(concat(index_id, ':', column_id, ':', key_ordinal, ':', cast(is_included_column as int), ':', column_store_order_ordinal), ',')
                within group (order by index_column_id)
            from sys.index_columns where object_id = object_id('t')
            """));
    }

    [TestMethod]
    public void AClusteredColumnstoreIndex_CoversAColumnAddedLater()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b int); create clustered columnstore index c on t; alter table t add d varchar(5)");
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.index_columns where object_id = object_id('t')"));
    }

    [TestMethod]
    [DataRow("create table t (a int, b int, index c clustered columnstore)", "CLUSTERED COLUMNSTORE")]
    [DataRow("create table t (a int, b int, index n nonclustered columnstore (a))", "NONCLUSTERED COLUMNSTORE")]
    public void AnInlineColumnstoreIndex_IsDeclared(string create, string typeDesc)
        => AreEqual(typeDesc, new Simulation().ExecuteScalar(create + "; select type_desc from sys.indexes where index_id > 0"));

    [TestMethod]
    [DataRow("", "COLUMNSTORE")]
    [DataRow("with (data_compression = columnstore_archive)", "COLUMNSTORE_ARCHIVE")]
    public void SysPartitions_ReportsTheColumnstoreCompression(string with, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t (a int);
            create clustered columnstore index c on t {with};
            select data_compression_desc from sys.partitions where object_id = object_id('t')
            """));

    [TestMethod]
    [DataRow("alter index c on t rebuild with (data_compression = columnstore_archive)")]
    [DataRow("alter table t rebuild with (data_compression = columnstore_archive)")]
    public void ARebuild_Recompresses(string rebuild)
        => AreEqual("COLUMNSTORE_ARCHIVE", new Simulation().ExecuteScalar($"""
            create table t (a int);
            create clustered columnstore index c on t;
            {rebuild};
            select data_compression_desc from sys.partitions where object_id = object_id('t')
            """));

    [TestMethod]
    public void AlterIndexSet_ChangesTheCompressionDelay()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create table t (a int);
            create clustered columnstore index c on t;
            alter index c on t set (compression_delay = 5);
            select compression_delay from sys.indexes where name = 'c'
            """));

    [TestMethod]
    public void ATableWithAClusteredColumnstoreIndex_ReadsAndWrites()
        => AreEqual("1:0,5:6", new Simulation().ExecuteScalar("""
            create table t (a int, b int);
            insert t values (1, 2), (3, 4);
            create clustered columnstore index c on t;
            insert t values (5, 6);
            update t set b = 0 where a = 1;
            delete t where a = 3;
            select string_agg(concat(a, ':', b), ',') within group (order by a) from t
            """));

    [TestMethod]
    public void DropExisting_ReplacesAClusteredRowstoreIndex()
        => AreEqual("CLUSTERED COLUMNSTORE", new Simulation().ExecuteScalar("""
            create table t (a int, b int);
            create clustered index ci on t (a);
            create clustered columnstore index ci on t with (drop_existing = on);
            select type_desc from sys.indexes where object_id = object_id('t') and name = 'ci'
            """));

    [TestMethod]
    public void SpHelpIndexAndIndexProperty_KnowTheKind()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); create clustered columnstore index c on t");
        AreEqual(1, sim.ExecuteScalar("select indexproperty(object_id('t'), 'c', 'IsColumnstore')"));
        using var reader = sim.ExecuteReader("exec sp_helpindex 't'");
        IsTrue(reader.Read());
        AreEqual("clustered, columnstore located on PRIMARY", reader.GetString(1));
        IsTrue(reader.IsDBNull(2));
    }

    [TestMethod]
    [DataRow("create unique nonclustered columnstore index n on t (a)", 35301)]
    [DataRow("create nonclustered columnstore index n on t (a desc)", 35302)]
    [DataRow("create nonclustered columnstore index n on t (a, c)", 35307)]
    [DataRow("create nonclustered columnstore index n on t (a) include (b)", 35311)]
    [DataRow("create nonclustered columnstore index n on t (a) with (fillfactor = 80)", 35317)]
    [DataRow("create clustered columnstore index cc on t with (allow_row_locks = on)", 35318)]
    [DataRow("create clustered columnstore index cc on t (a)", 35335)]
    [DataRow("create nonclustered columnstore index n on t", 35336)]
    [DataRow("create nonclustered columnstore index n on t (a); create nonclustered columnstore index m on t (b)", 35339)]
    [DataRow("create nonclustered columnstore index n on t (a, x)", 35343)]
    [DataRow("create clustered index ci on t (a); create clustered columnstore index cc on t", 35372)]
    [DataRow("create clustered columnstore index cc on t with (compression_delay = 10081)", 35382)]
    [DataRow("create clustered columnstore index cc on t with (data_compression = page)", 10799)]
    [DataRow("create clustered columnstore index cc on t with (xml_compression = on)", 16210)]
    [DataRow("create clustered columnstore index cc on t with (resumable = on)", 11438)]
    [DataRow("create clustered columnstore index cc on t with (resumable = on, online = on)", 35318)]
    public void Declaring_RefusesAsRealDoes(string statement, int number)
        => new Simulation().AssertSqlError("create table t (a int, b int, c as a + b, x xml); " + statement, number);

    [TestMethod]
    [DataRow("create clustered columnstore index c on t", "alter index c on t rebuild with (fillfactor = 80)", 35327)]
    [DataRow("create clustered columnstore index c on t", "alter index c on t set (allow_page_locks = off)", 35328)]
    [DataRow("create clustered columnstore index c on t", "alter index c on t rebuild with (data_compression = page)", 10799)]
    [DataRow("create index ix on t (a)", "alter index ix on t rebuild with (data_compression = columnstore)", 10798)]
    [DataRow("create index ix on t (a)", "alter index ix on t set (compression_delay = 5)", 35364)]
    public void Altering_RefusesAsRealDoes(string create, string alter, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); " + create);
        _ = sim.AssertSqlError(alter, number);
    }

    [TestMethod]
    public void ATableVariable_RefusesAColumnstoreIndex()
        => new Simulation().AssertSqlError("declare @t table (a int, index c clustered columnstore)", 35310);

    /// <summary>The two option refusals every CREATE INDEX shares.</summary>
    [TestMethod]
    public void CreateIndex_FollowsAnUnknownNumericOptionWithMsg153_AndNeedsOnlineToResume()
    {
        var errors = new Simulation().AssertSqlError("create table t (a int); create index ix on t (a) with (bogus = 1)", 155).Errors;
        AreEqual(153, errors[1].Number);
        _ = new Simulation().AssertSqlError("create table t (a int); create index ix on t (a) with (resumable = on)", 11438);
    }
}
