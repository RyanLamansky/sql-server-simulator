using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An index or statistic keeps the <c>index_id</c> it was given for life, and
/// the next one takes the lowest id from 2 nothing holds (probed 2026-09-30
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class IndexIdReuseTests
{
    private static string Ids(Simulation simulation, string view = "indexes", string idColumn = "index_id")
    {
        using var reader = simulation.ExecuteReader($"select name, {idColumn} from sys.{view} where object_id = object_id('t') order by {idColumn}");
        return string.Join(", ", reader.EnumerateRecords().Select(record => $"{(record.IsDBNull(0) ? "-" : record.GetString(0))}={record.GetInt32(1)}"));
    }

    [TestMethod]
    public void ADrop_LeavesAGap_TheNextIndexFills()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int, d int); create index i1 on t (a); create index i2 on t (b); create index i3 on t (c); drop index i2 on t");
        AreEqual("-=0, i1=2, i3=4", Ids(simulation));
        _ = simulation.ExecuteNonQuery("create index i4 on t (d)");
        AreEqual("-=0, i1=2, i4=3, i3=4", Ids(simulation));
    }

    [TestMethod]
    public void EachNewObjectTakesTheLowestFreeId()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int, d int); create index i1 on t (a); create index i2 on t (b); create index i3 on t (c); drop index i1 on t; drop index i3 on t; create index i4 on t (d); create index i5 on t (a)");
        AreEqual("-=0, i4=2, i2=3, i5=4", Ids(simulation));
    }

    [TestMethod]
    public void Statistics_ShareThePool()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); create index i1 on t (a); create statistics s1 on t (b); create index i2 on t (c)");
        AreEqual("i1=2, s1=3, i2=4", Ids(simulation, "stats", "stats_id"));
        AreEqual("-=0, i1=2, i2=4", Ids(simulation));
    }

    [TestMethod]
    public void AHeaps_FirstStatistic_IsIdTwo()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int); create statistics s1 on t (a); create statistics s2 on t (b)");
        AreEqual("s1=2, s2=3", Ids(simulation, "stats", "stats_id"));
    }

    [TestMethod]
    public void ADroppedStatistic_IsAGapAnIndexFills()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); create statistics s1 on t (a); create statistics s2 on t (b); drop statistics t.s1; create index i1 on t (c); create statistics s3 on t (c)");
        AreEqual("i1=2, s2=3, s3=4", Ids(simulation, "stats", "stats_id"));
    }

    [TestMethod]
    public void TheClusteredIndex_IsAlwaysOne_AndNothingElseTakesTheSlotItLeaves()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); create clustered index cx on t (c); create index i1 on t (a); drop index cx on t; create index i2 on t (b)");
        AreEqual("-=0, i1=2, i2=3", Ids(simulation));
        _ = simulation.ExecuteNonQuery("create clustered index cx2 on t (b)");
        AreEqual("cx2=1, i1=2, i2=3", Ids(simulation));
    }

    /// <summary>A key constraint's index is numbered as any other; one declaration's constraints take ids in reverse declaration order.</summary>
    [TestMethod]
    public void ADroppedConstraint_LeavesAGap_TheNextConstraintFills()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int constraint u1 unique, c int constraint u2 unique, d int); alter table t drop constraint u1; alter table t add constraint u3 unique (d)");
        AreEqual("-=0, u2=2, u3=3", Ids(simulation));
    }

    [TestMethod]
    public void DropExisting_KeepsTheId()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); create index i1 on t (a); create index i2 on t (b); create index i3 on t (c); drop index i1 on t; create index i2 on t (b) with (drop_existing = on)");
        AreEqual("-=0, i2=3, i3=4", Ids(simulation));
    }
}
