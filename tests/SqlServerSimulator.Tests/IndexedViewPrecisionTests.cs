using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A view's precision — what <c>OBJECTPROPERTY(…, 'IsPrecise')</c> answers and
/// what an index on it refuses — and the statistics and columnstore indexes a
/// view without a unique clustered index can't take (probed 2026-10-06
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class IndexedViewPrecisionTests
{
    private static Simulation Seeded(string view)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int not null, b float not null, c real, d decimal(5, 2))", view);
        return simulation;
    }

    [TestMethod]
    [DataRow("create view v with schemabinding as select b, count_big(*) c from dbo.t group by b", 0)]
    [DataRow("create view v with schemabinding as select a from dbo.t where b > 0", 0)]
    [DataRow("create view v with schemabinding as select x.a from dbo.t x join dbo.t y on x.b = y.b", 0)]
    [DataRow("create view v with schemabinding as select cast(b as int) bi, count_big(*) n from dbo.t group by cast(b as int)", 0)]
    [DataRow("create view v with schemabinding as select a from dbo.t where d > 1.5e0", 0)]
    [DataRow("create view v with schemabinding as select a, sum(b) s, count_big(*) c from dbo.t group by a", 1)]
    [DataRow("create view v with schemabinding as select a, b from dbo.t where a > 0", 1)]
    [DataRow("create view v with schemabinding as select b * 2 x, a from dbo.t", 1)]
    [DataRow("create view v as select a from dbo.t where a > 0", 0)]
    public void IsPrecise(string view, int precise)
    {
        var simulation = Seeded(view);
        AreEqual(precise, simulation.ExecuteScalar("select objectproperty(object_id('v'), 'IsPrecise')"));
        AreEqual(precise, simulation.ExecuteScalar("select cast(objectpropertyex(object_id('v'), 'IsPrecise') as int)"));
    }

    [TestMethod]
    public void FloatConstantInAWhere_IsMsg1964()
    {
        var simulation = Seeded("create view v with schemabinding as select a from dbo.t where d > 1.5e0");
        simulation.AssertSqlError("create unique clustered index ix on v (a)", 1964, "Cannot create index on view \"simulated.dbo.v\". The view contains an imprecise constant.");
        AreEqual(0, simulation.ExecuteScalar("select objectproperty(object_id('v'), 'IsIndexable')"));
    }

    [TestMethod]
    public void FloatConstantInTheSelectList_IndexesFine()
    {
        var simulation = Seeded("create view v with schemabinding as select a, 1.5e0 x from dbo.t");
        simulation.ExecuteBatches("create unique clustered index ix on v (a)");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('v')"));
    }

    [TestMethod]
    public void WithoutAUniqueClusteredIndex_StatisticsAndColumnstoreAreMsg1940()
    {
        var simulation = Seeded("create view v with schemabinding as select a from dbo.t");
        simulation.ExecuteBatches("create view nb as select a from dbo.t");
        var statistics = simulation.AssertSqlError("create statistics st on dbo.v (a)", 1940);
        AreEqual("Cannot create statistics on view 'dbo.v'. It does not have a unique clustered index.", statistics.Errors[0].Message);
        AreEqual<byte>(1, statistics.Errors[0].State);
        var columnstore = simulation.AssertSqlError("create nonclustered columnstore index cs on nb (a)", 1940);
        AreEqual("Cannot create index on view 'nb'. It does not have a unique clustered index.", columnstore.Errors[0].Message);
        AreEqual<byte>(2, columnstore.Errors[0].State);
        _ = simulation.AssertSqlError("create clustered columnstore index cs on v", 35305);
    }
}
