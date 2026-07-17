using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for indexed (materialized) views: <c>CREATE UNIQUE
/// CLUSTERED INDEX ON &lt;view&gt;</c>, the schema-binding / unique-clustered
/// gates (Msg 1939 / 1940 / 1941), create-time duplicate rejection (Msg 1505),
/// live base-DML uniqueness enforcement (Msg 2601), the <c>sys.indexes</c> /
/// <c>sys.index_columns</c> / <c>sys.stats</c> catalog surface, and
/// <c>is_schema_bound</c>. Probe-confirmed against SQL Server 2025
/// (2026-07-17).
/// </summary>
[TestClass]
public sealed class IndexedViewTests
{
    /// <summary>
    /// Base table + a schema-bound projection view + a unique clustered index
    /// on the view's <c>Id</c> column, with three seed rows (Ids 1/2/3).
    /// </summary>
    private static Simulation SeedIndexedView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null, val int not null); insert dbo.b values (1,10,100),(2,20,200),(3,30,300)",
            "create view dbo.v with schemabinding as select id, grp, val from dbo.b",
            "create unique clustered index ix_v on dbo.v(id)");
        return sim;
    }

    // --- Gates ---

    [TestMethod]
    public void CreateIndex_OnNonSchemaBoundView_Msg1939()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v as select id, grp from dbo.b");
        var ex = sim.AssertSqlError("create unique clustered index ix on dbo.v(id)", 1939);
        AreEqual("Cannot create index on view 'v' because the view is not schema bound.", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_NonUniqueClusteredOnView_Msg1941()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        var ex = sim.AssertSqlError("create clustered index ix on dbo.v(id)", 1941);
        Contains("only unique clustered indexes are allowed", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_NonClusteredFirstOnView_Msg1940()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        var ex = sim.AssertSqlError("create nonclustered index ix on dbo.v(id)", 1940);
        AreEqual("Cannot create index on view 'dbo.v'. It does not have a unique clustered index.", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_UniqueNonClusteredFirstOnView_Msg1940()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        _ = sim.AssertSqlError("create unique nonclustered index ix on dbo.v(id)", 1940);
    }

    [TestMethod]
    public void CreateIndex_MissingViewColumn_Msg1911()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        _ = sim.AssertSqlError("create unique clustered index ix on dbo.v(nope)", 1911);
    }

    // --- Create-time validation ---

    [TestMethod]
    public void CreateUniqueClusteredIndex_OverDuplicateViewRows_Msg1505()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null); insert dbo.b values (1,10),(2,10),(3,20)",
            "create view dbo.v with schemabinding as select grp from dbo.b");
        var ex = sim.AssertSqlError("create unique clustered index ix on dbo.v(grp)", 1505);
        Contains("'dbo.v'", ex.Message);
        Contains("(10)", ex.Message);
    }

    [TestMethod]
    public void CreateUniqueClusteredIndex_Succeeds()
        => AreEqual(3L, SeedIndexedView().ExecuteScalar("select count_big(*) from dbo.v"));

    // --- Enforcement ---

    [TestMethod]
    public void Insert_ProducingDuplicateViewKey_Msg2601_NamesViewAndIndex()
    {
        var ex = SeedIndexedView().AssertSqlError("insert dbo.b values (1, 99, 999)", 2601);
        AreEqual("Cannot insert duplicate key row in object 'dbo.v' with unique index 'ix_v'. The duplicate key value is (1).", ex.Message);
    }

    [TestMethod]
    public void Insert_ProducingDuplicateViewKey_RollsBackStatement()
    {
        var sim = SeedIndexedView();
        _ = sim.AssertSqlError("insert dbo.b values (1, 99, 999)", 2601);
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.b"));
    }

    [TestMethod]
    public void Update_ProducingDuplicateViewKey_Msg2601()
    {
        var sim = SeedIndexedView();
        var ex = sim.AssertSqlError("update dbo.b set id = 2 where id = 3", 2601);
        Contains("'dbo.v'", ex.Message);
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.b where id in (1,2,3)"));
    }

    [TestMethod]
    public void Insert_NonDuplicate_Succeeds()
    {
        var sim = SeedIndexedView();
        _ = sim.ExecuteNonQuery("insert dbo.b values (4, 40, 400)");
        AreEqual(4, sim.ExecuteScalar("select count(*) from dbo.b"));
    }

    [TestMethod]
    public void Delete_NeverViolatesViewUniqueness()
    {
        var sim = SeedIndexedView();
        _ = sim.ExecuteNonQuery("delete dbo.b where id = 3");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.b"));
    }

    // --- is_schema_bound ---

    [TestMethod]
    public void SchemaBinding_SurfacesThroughSqlModulesAndObjectProperty()
    {
        var sim = SeedIndexedView();
        IsTrue((bool)sim.ExecuteScalar("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')")!);
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.v'), 'IsSchemaBound')"));
    }

    [TestMethod]
    public void NonSchemaBoundView_ReportsIsSchemaBoundFalse()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null)",
            "create view dbo.v as select id from dbo.b");
        IsFalse((bool)sim.ExecuteScalar("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')")!);
    }

    // --- Catalog surface ---

    [TestMethod]
    public void ViewIndex_SurfacesInSysIndexes_AsClusteredUnique()
    {
        var sim = SeedIndexedView();
        AreEqual(1, sim.ExecuteScalar(
            "select index_id from sys.indexes where object_id = object_id('dbo.v') and name = 'ix_v' and type_desc = 'CLUSTERED' and is_unique = 1 and is_primary_key = 0 and is_unique_constraint = 0"));
        // No HEAP row for a view (no index_id 0).
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v') and index_id = 0"));
    }

    [TestMethod]
    public void PlainView_HasNoSysIndexesRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null)",
            "create view dbo.v as select id from dbo.b");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v')"));
    }

    [TestMethod]
    public void ViewIndex_SurfacesInSysIndexColumns_WithViewColumnId()
    {
        // Two key columns: id (column_id 1) and grp (column_id 2).
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null, val int not null); insert dbo.b values (1,10,100)",
            "create view dbo.v with schemabinding as select id, grp, val from dbo.b",
            "create unique clustered index ix_v on dbo.v(id, grp)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.index_columns where object_id = object_id('dbo.v') and is_included_column = 0"));
        AreEqual(1, sim.ExecuteScalar("select column_id from sys.index_columns where object_id = object_id('dbo.v') and key_ordinal = 1"));
        AreEqual(2, sim.ExecuteScalar("select column_id from sys.index_columns where object_id = object_id('dbo.v') and key_ordinal = 2"));
    }

    [TestMethod]
    public void ViewIndex_SurfacesInSysStats()
    {
        var sim = SeedIndexedView();
        AreEqual(1, sim.ExecuteScalar("select stats_id from sys.stats where object_id = object_id('dbo.v') and name = 'ix_v'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.v') and stats_id = 1"));
    }

    // --- NOEXPAND ---

    [TestMethod]
    public void Select_FromIndexedView_WithNoExpand_Accepted()
        => AreEqual(3L, SeedIndexedView().ExecuteScalar("select count_big(*) from dbo.v with (noexpand)"));
}
