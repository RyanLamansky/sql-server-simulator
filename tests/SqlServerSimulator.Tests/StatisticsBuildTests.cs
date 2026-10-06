using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Building a statistic over a computed key, and the automatic statistics a
/// predicate over a view or a computed column creates. Every expected value
/// was probed 2026-10-06 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class StatisticsBuildTests
{
    private const string Overflowing = "create table ts (a int, c as a * 1000000000); insert ts values (1), (5);";

    private static string? Names(Simulation simulation, string table) =>
        simulation.ExecuteScalar($"select string_agg(name, ',') within group (order by name) from sys.stats where object_id = object_id('{table}')") as string;

    [TestMethod]
    public void AKeyThatFailsToEvaluate_LeavesTheStatisticUnbuilt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Overflowing);
        _ = simulation.AssertSqlError("create statistics s on ts(c)", 8115);
        _ = simulation.AssertSqlError("create statistics s2 on ts(a, c)", 8115);
        _ = simulation.AssertSqlError("create statistics s3 on ts(c) with fullscan", 8115);
        AreEqual("s,s2,s3", Names(simulation, "ts"));
        AreEqual(1, simulation.ExecuteScalar("select case when stats_date(object_id, stats_id) is null then 1 else 0 end from sys.stats where object_id = object_id('ts') and name = 's'"));
        _ = simulation.AssertSqlError("update statistics ts s", 8115);
    }

    [TestMethod]
    public void AFilter_LimitsTheRowsTheKeyEvaluatesOver()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Overflowing + " create statistics s4 on ts(c) where a < 2");
        AreEqual("s4", Names(simulation, "ts"));
    }

    [TestMethod]
    public void TheFailure_EndsOnlyItsStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Overflowing);
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; create statistics s5 on ts(c); create table after (a int)").ExecuteNonQuery());
        AreEqual(1, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(1, connection.CreateCommand("select count(*) from sys.tables where name = 'after'").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        IsNull(Names(simulation, "ts"));
    }

    [TestMethod]
    public void APredicateOnAComputedColumn_LoadsTheColumnsItReads()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t3 (a int, b int, c as a * 2); insert t3 values (5, 1)");
        _ = simulation.ExecuteScalar("select * from t3 where c = 1");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.stats s join sys.stats_columns sc on sc.object_id = s.object_id and sc.stats_id = s.stats_id where s.object_id = object_id('t3') and s.auto_created = 1 and sc.column_id = 1"));
    }

    [TestMethod]
    public void APredicateOnAViewsColumn_LoadsItsBaseColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); insert t values (1, 2, 3), (3, 4, 5)");
        _ = simulation.ExecuteNonQuery("create view v as select a, b from t");
        _ = simulation.ExecuteScalar("select * from v where b = 2");
        AreEqual("b", simulation.ExecuteScalar("select c.name from sys.stats s join sys.stats_columns sc on sc.object_id = s.object_id and sc.stats_id = s.stats_id join sys.columns c on c.object_id = sc.object_id and c.column_id = sc.column_id where s.object_id = object_id('t')"));
    }
}
