namespace SqlServerSimulator;

/// <summary>
/// <c>INDEXPROPERTY</c> over every index kind, the locking options it and
/// <c>sys.indexes</c> report, and the XML and spatial rows <c>sys.indexes</c>
/// carries. Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class IndexPropertyMatrixTests
{
    private static readonly Simulation Objects = Build();

    private static Simulation Build()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null constraint pk_t primary key with (allow_page_locks = off), u int constraint uq_t unique, a int, b nvarchar(50), x xml, gg geometry)",
            "create index ix_fill on t (a) with (fillfactor = 70, pad_index = on)",
            "create index ix_locks on t (a, id) with (allow_row_locks = off, allow_page_locks = off, optimize_for_sequential_key = on)",
            "create index ix_set on t (u)",
            "alter index ix_set on t set (allow_row_locks = off, optimize_for_sequential_key = on)",
            "create index ix_dis on t (b)",
            "alter index ix_dis on t disable",
            "create nonclustered columnstore index ncci on t (a, u)",
            "create statistics st on t (a, b)",
            "create primary xml index px on t (x)",
            "create spatial index sx on t (gg) with (bounding_box = (0, 0, 10, 10))",
            "create fulltext catalog cat as default",
            "create fulltext index on t (b) key index pk_t",
            "create view v with schemabinding as select id, a from dbo.t",
            "create unique clustered index vx on v (id)");
        return sim;
    }

    [TestMethod]
    [DataRow("t", "pk_t", "IndexID", "1")]
    [DataRow("t", "pk_t", "IsFulltextKey", "1")]
    [DataRow("t", "pk_t", "IsPageLockDisallowed", "1")]
    [DataRow("t", "uq_t", "IsFulltextKey", "0")]
    [DataRow("t", "ix_fill", "IndexFillFactor", "70")]
    [DataRow("t", "ix_fill", "IsPadIndex", "1")]
    [DataRow("t", "ix_locks", "IsRowLockDisallowed", "1")]
    [DataRow("t", "ix_locks", "IsOptimizedForSequentialKey", "1")]
    [DataRow("t", "ix_set", "IsRowLockDisallowed", "1")]
    [DataRow("t", "ix_set", "IsPageLockDisallowed", "0")]
    [DataRow("t", "ix_dis", "IsDisabled", "1")]
    [DataRow("t", "ncci", "IsColumnstore", "1")]
    [DataRow("t", "ncci", "IsRowLockDisallowed", "1")]
    [DataRow("t", "st", "IsStatistics", "1")]
    [DataRow("t", "st", "IndexID", "0")]
    [DataRow("t", "px", "IndexID", "256000")]
    [DataRow("t", "px", "IndexDepth", "NULL")]
    [DataRow("t", "sx", "IndexID", "384000")]
    [DataRow("t", "sx", "IsUnique", "0")]
    [DataRow("v", "vx", "IsClustered", "1")]
    [DataRow("v", "vx", "IsUnique", "1")]
    [DataRow("v", "vx", "IndexDepth", "0")]
    [DataRow("t", "nope", "IsUnique", "NULL")]
    public void AProperty_AnswersForTheIndex(string objectName, string index, string property, string expected)
        => Assert.AreEqual(expected, Objects.ExecuteScalar(
            $"select isnull(cast(indexproperty(object_id('{objectName}'), '{index}', '{property}') as varchar), 'NULL')"));

    [TestMethod]
    public void SysIndexes_ReportsTheLockingOptionsAndTheXmlAndSpatialIndexes()
        => Assert.AreEqual("pk_t:1:1:0:0|ix_locks:2:0:0:1|ix_set:2:0:1:1|ncci:6:0:0:0|px:3:1:1:0|sx:4:1:1:0", Objects.ExecuteScalar("""
            select string_agg(concat(name, ':', type, ':', cast(allow_row_locks as int), ':', cast(allow_page_locks as int), ':', cast(optimize_for_sequential_key as int)), '|')
                within group (order by index_id)
            from sys.indexes where object_id = object_id('t') and name in ('pk_t', 'ix_locks', 'ix_set', 'ncci', 'px', 'sx')
            """));

    [TestMethod]
    public void SysIndexColumns_ListsTheSpatialIndexColumn()
        => Assert.AreEqual(6, Objects.ExecuteScalar("select column_id from sys.index_columns where object_id = object_id('t') and index_id = 384000"));
}
