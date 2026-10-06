using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Writes through a view, CTE or inline function that read a column the
/// level below derives, or reach a triggered view further down (probed
/// 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ViewWriteDerivedReadTests
{
    private static string Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        return string.Join(",", reader.EnumerateRecords().Select(record => $"{record.GetValue(0)}:{record.GetValue(1)}"));
    }

    [TestMethod]
    public void DerivedColumn_ReadsInSetAndWhere()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, s varchar(10), o varchar(10)); insert t values (1, 'a', 'b'), (2, 'c', 'd')",
            "create view v as select k, s, o, s + 'x' s2, k * 10 k10 from t",
            "update v set o = s2 + cast(k10 as varchar) where k10 = 20",
            "delete v where s2 = 'ax'");
        AreEqual("2:cx20", Rows(simulation, "select k, o from t"));
    }

    [TestMethod]
    public void DerivedColumnOfALowerView_ReadsThroughTheChain()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, s varchar(10), o varchar(10)); insert t values (1, 'a', 'b')",
            "create view v as select k, s, o, s + 'x' s2 from t",
            "create view w as select k, o, s2, s2 + 'y' s3 from v",
            "update w set o = s3");
        AreEqual("1:axy", Rows(simulation, "select k, o from t"));
    }

    [TestMethod]
    public void DerivedColumnError_EndsTheWrite()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (k int primary key, s int, o int); insert t values (1, 0, 5)", "create view v as select k, s, o, 10 / s q from t");
        _ = simulation.AssertSqlError("update v set o = q", 8134);
        AreEqual(5, simulation.ExecuteScalar("select o from t"));
    }

    [TestMethod]
    public void ChainedWhere_OnALowerViewsDerivedColumn_FiltersTheWrite()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, v int); insert t values (1, 1), (2, 2), (3, 3)",
            "create view w as select k, v, v * 2 c from t",
            "create view ww as select k, v, c from w where c > 2",
            "update ww set v = 10");
        AreEqual("1:1,2:10,3:10", Rows(simulation, "select k, v from t order by k"));
    }

    [TestMethod]
    public void ChainedWhere_OnARowLimitedViewsDerivedColumn_FiltersItsRows()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, v int); insert t values (1, 1), (2, 2), (3, 3)",
            "create view w as select top 2 k, v, v * 2 c from t order by k desc",
            "create view ww as select k, v, c from w where c > 4",
            "update ww set v = 10");
        AreEqual("1:1,2:2,3:10", Rows(simulation, "select k, v from t order by k"));
    }

    [TestMethod]
    public void ChainedWhere_UnderCheckOption_RefusesARowLeavingIt()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, v int); insert t values (1, 1), (2, 2)",
            "create view w as select k, v, v * 2 c from t",
            "create view ww as select k, v, c from w where c > 2 with check option");
        _ = simulation.AssertSqlError("update ww set v = 0", 550);
    }

    [TestMethod]
    public void InsteadOfTriggerTwoLevelsDown_TakesTheWrite()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, v int); create table log (m varchar(50)); insert t values (1, 1), (2, 2), (3, 3)",
            "create view v1 as select k, v from t",
            "create trigger tr on v1 instead of delete as insert log select 'del ' + cast(k as varchar) from deleted",
            "create view v2 as select k, v, v * 10 w from v1 where k > 1",
            "create view v3 as select k, w from v2 where w < 30",
            "delete v3");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual("del 2", simulation.ExecuteScalar("select string_agg(m, ',') from log"));
        _ = simulation.AssertSqlError("update v3 set w = 1", 4406);
    }

    [TestMethod]
    public void JoinedWrite_ThroughAnInlineFunctionsAlias()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, v int); insert t values (1, 1), (2, 2), (3, 3); create table u (k int); insert u values (2), (3)",
            "create function f(@m int) returns table as return select k, v, v * 2 d from t where k >= @m",
            "declare @m int = 3; update x set v = 9 from dbo.f(@m) x join u on u.k = x.k",
            "delete f from dbo.f(2) f join u on u.k = f.k and u.k = 2");
        AreEqual("1:1,3:9", Rows(simulation, "select k, v from t order by k"));
        _ = simulation.AssertSqlError("update f set d = 1 from dbo.f(1) f join u on u.k = f.k", 4406);
    }

    [TestMethod]
    public void JoinedWrite_ThroughAMultiStatementFunction_IsMsg270()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (k int primary key, v int); create table u (k int)",
            "create function g(@m int) returns @r table (k int, v int) as begin insert @r select k, v from t where k >= @m; return; end");
        simulation.AssertSqlError("delete x from dbo.g(1) x join u on u.k = x.k", 270, "Object 'dbo.g' cannot be modified.");
    }
}
