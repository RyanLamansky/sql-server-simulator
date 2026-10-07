using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The catalog views keep their rows across statements until something changes
/// what they project, so each test first reads the view (filling the cache),
/// then changes the metadata by a route other than the ordinary DDL arm, then
/// reads again and expects the change.
/// </summary>
[TestClass]
public sealed class CatalogRowCacheTests
{
    [TestMethod]
    public void CreateTable_AfterARead_Appears()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table a (id int)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables"));
        _ = sim.ExecuteNonQuery("create table b (id int)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.tables"));
    }

    [TestMethod]
    public void TempTableDdl_ThenAPermanentTable_AfterARead_Appears()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table a (id int)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables"));
        _ = sim.ExecuteNonQuery("create table #t (id int); drop table #t; select 1 as c into #u; drop table #u; create table b (id int)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.tables"));
        _ = sim.ExecuteNonQuery("create table #t (id int); drop table #t, b");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables"));
    }

    [TestMethod]
    public void SelectInto_AfterARead_Appears()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("select 1 as a, N'x' as b into t");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
    }

    [TestMethod]
    public void ExtendedProperty_AddUpdateDrop_EachAppear()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.extended_properties where major_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("exec sp_addextendedproperty N'd', N'one', N'SCHEMA', N'dbo', N'TABLE', N't'");
        AreEqual("one", sim.ExecuteScalar("select cast(value as nvarchar(10)) from sys.extended_properties where major_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("exec sp_updateextendedproperty N'd', N'two', N'SCHEMA', N'dbo', N'TABLE', N't'");
        AreEqual("two", sim.ExecuteScalar("select cast(value as nvarchar(10)) from sys.extended_properties where major_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("exec sp_dropextendedproperty N'd', N'SCHEMA', N'dbo', N'TABLE', N't'");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.extended_properties where major_id = object_id('t')"));
    }

    [TestMethod]
    public void BindDefault_AfterARead_SetsTheColumnsDefault()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int)", "create default d as 5");
        AreEqual(0, sim.ExecuteScalar("select default_object_id from sys.columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd', 't.a'");
        IsTrue((bool)sim.ExecuteScalar("select cast(case when default_object_id = object_id('d') then 1 else 0 end as bit) from sys.columns where object_id = object_id('t')")!);
    }

    [TestMethod]
    public void DisableTrigger_AfterARead_SetsIsDisabled()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int)", "create trigger tr on t after insert as return");
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.triggers where name = 'tr'")!);
        _ = sim.ExecuteNonQuery("disable trigger tr on t");
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.triggers where name = 'tr'")!);
        _ = sim.ExecuteNonQuery("enable trigger tr on t");
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.triggers where name = 'tr'")!);
    }

    [TestMethod]
    public void UpdateStatisticsNoRecompute_AfterARead_Shows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); create statistics s on t (a)");
        IsFalse((bool)sim.ExecuteScalar("select no_recompute from sys.stats where name = 's'")!);
        _ = sim.ExecuteNonQuery("update statistics t s with norecompute");
        IsTrue((bool)sim.ExecuteScalar("select no_recompute from sys.stats where name = 's'")!);
    }

    [TestMethod]
    public void RolledBackCreateTable_AfterARead_Disappears()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("begin tran; create table t (a int)").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("select count(*) from sys.tables where name = 't'").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("select count(*) from sys.tables where name = 't'").ExecuteScalar());
    }

    [TestMethod]
    public void AnotherSessionsDdl_AfterARead_Appears()
    {
        var sim = new Simulation();
        var reader = sim.CreateOpenConnection();
        var writer = sim.CreateOpenConnection();
        AreEqual(0, reader.CreateCommand("select count(*) from sys.objects where name = 't'").ExecuteScalar());
        _ = writer.CreateCommand("create table t (a int primary key)").ExecuteNonQuery();
        AreEqual(2, reader.CreateCommand("select count(*) from sys.objects where name = 't' or parent_object_id = object_id('t')").ExecuteScalar());
    }

    [TestMethod]
    public void IdentityLastValue_FollowsInserts()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity(10, 5), a int)");
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select last_value from sys.identity_columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("insert t (a) values (1), (2)");
        AreEqual(15, sim.ExecuteScalar("select last_value from sys.identity_columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("insert t (a) values (3)");
        AreEqual(20, sim.ExecuteScalar("select last_value from sys.identity_columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("truncate table t");
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select last_value from sys.identity_columns where object_id = object_id('t')"));
    }

    [TestMethod]
    public void CrossDatabaseRead_FollowsThatDatabasesDdl()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database other");
        AreEqual(0, sim.ExecuteScalar("select count(*) from other.sys.tables"));
        _ = sim.ExecuteNonQuery("create table other.dbo.t (a int)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from other.sys.tables"));
    }

    [TestMethod]
    public void RestrictedPrincipal_AfterADboRead_StillSeesItsFilteredView()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table visible (a int); create table hidden (a int); create user u without login; grant select on visible to u");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.tables"));
        AreEqual("visible", sim.ExecuteScalar("execute as user = 'u'; select string_agg(name, ',') from sys.tables; revert"));
    }

    [TestMethod]
    public void TempdbRead_ListsEverySessionsTempTables()
    {
        var sim = new Simulation();
        var first = sim.CreateOpenConnection();
        var second = sim.CreateOpenConnection();
        _ = first.CreateCommand("create table #mine (a int)").ExecuteNonQuery();
        AreEqual(1, first.CreateCommand("select count(*) from tempdb.sys.tables where name like '#mine[_]%'").ExecuteScalar());
        AreEqual(1, second.CreateCommand("select count(*) from tempdb.sys.tables where name like '#mine[_]%'").ExecuteScalar());
        AreEqual(1, first.CreateCommand("select count(*) from tempdb.sys.tables where object_id = object_id('tempdb..#mine')").ExecuteScalar());
        AreEqual(0, second.CreateCommand("select count(*) from tempdb.sys.tables where object_id = object_id('tempdb..#mine')").ExecuteScalar());
    }

    [TestMethod]
    public void InListSeek_KeepsTheViewsOrder()
        => AreEqual("a,b,c,d", new Simulation().ExecuteScalar("""
            create table t (a int, b int, c int, d int);
            select string_agg(name, ',') from sys.columns
            where object_id = object_id('t') and name in ('d', 'b', 'a', 'c', 'b')
            """));

    [TestMethod]
    public void NameSeek_FollowsTheComparisonsCollation()
        => AreEqual("1|0", new Simulation().ExecuteScalar("""
            create table Mixed (a int);
            select concat(
                (select count(*) from sys.tables where name = 'MIXED'),
                '|',
                (select count(*) from sys.tables where name = 'MIXED' collate Latin1_General_BIN))
            """));

    [TestMethod]
    public void IntegerSeek_TakesAnyIntegerComparand()
        => AreEqual("1|0|1", new Simulation().ExecuteScalar("""
            create table t (a int);
            declare @big bigint = object_id('t'), @huge bigint = 4294967296;
            select concat(
                (select count(*) from sys.tables where object_id = @big),
                '|',
                (select count(*) from sys.tables where object_id = @huge),
                '|',
                (select count(*) from sys.tables where object_id = cast(object_id('t') as decimal(20, 0))))
            """));

    [TestMethod]
    public void JoinedViews_AfterDdl_JoinTheNewRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table a (x int, y int)");
        const string Query = "select count(*) from sys.tables t join sys.columns c on c.object_id = t.object_id";
        AreEqual(2, sim.ExecuteScalar(Query));
        _ = sim.ExecuteNonQuery("alter table a add z int");
        AreEqual(3, sim.ExecuteScalar(Query));
    }
}
