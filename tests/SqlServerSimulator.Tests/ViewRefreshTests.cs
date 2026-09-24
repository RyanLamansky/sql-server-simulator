using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_refreshview</c> / <c>sp_refreshsqlmodule</c> and the binding errors a
/// drifted view reports, probed 2026-09-24 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ViewRefreshTests
{
    private static Simulation DriftedView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table rt (a int, b int); insert rt values (1, 2)",
            "create view rvv as select * from rt",
            "alter table rt add c int");
        return sim;
    }

    [TestMethod]
    public void SelectStarView_KeepsItsNamesUntilRefreshed()
        => AreEqual("a,b", DriftedView().ExecuteScalar("select string_agg(name, ',') within group (order by column_id) from sys.columns where object_id = object_id('rvv')"));

    [TestMethod]
    [DataRow("exec sp_refreshview 'rvv'")]
    [DataRow("exec sp_refreshview @viewname = N'dbo.rvv'")]
    [DataRow("exec sp_refreshsqlmodule 'rvv'")]
    public void Refresh_ReRecordsTheColumns(string refresh)
    {
        var sim = DriftedView();
        _ = sim.ExecuteNonQuery(refresh);
        AreEqual("a,b,c", sim.ExecuteScalar("select string_agg(name, ',') within group (order by column_id) from sys.columns where object_id = object_id('rvv')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select c from rvv"));
    }

    [TestMethod]
    public void Refresh_KeepsTheObjectId()
    {
        var sim = DriftedView();
        var before = sim.ExecuteScalar("select object_id('rvv')");
        _ = sim.ExecuteNonQuery("exec sp_refreshview 'rvv'");
        AreEqual(before, sim.ExecuteScalar("select object_id('rvv')"));
    }

    [TestMethod]
    [DataRow("exec sp_refreshview 'nosuch'", "nosuch")]
    [DataRow("exec sp_refreshview 'rt'", "rt")]
    [DataRow("exec sp_refreshsqlmodule 'nosuch'", "nosuch")]
    public void Refresh_UnresolvableName_RaisesMsg15165(string sql, string name)
        => DriftedView().AssertSqlError(sql, 15165, $"Could not find object '{name}' or you do not have permission.");

    [TestMethod]
    public void Refresh_WithoutAName_RaisesMsg201()
        => new Simulation().AssertSqlError("exec sp_refreshview", 201, "Procedure or function 'sp_refreshview' expects parameter '@viewname', which was not supplied.");

    [TestMethod]
    public void RefreshView_RefusesAProcedure()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure rvp as select 1");
        _ = sim.AssertSqlError("exec sp_refreshview 'rvp'", 15165);
        _ = sim.ExecuteNonQuery("exec sp_refreshsqlmodule 'rvp'");
    }

    [TestMethod]
    public void Refresh_SchemaBoundView_WarnsAndLeavesItAlone()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table rt (a int)", "create view rvs with schemabinding as select a from dbo.rt");
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        _ = connection.CreateCommand("exec sp_refreshview 'rvs'").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "Metadata was not updated for the schema-bound object 'rvs'." }, messages);
    }

    [TestMethod]
    public void Refresh_BrokenBody_ReportsItsBinderError()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table rt (a int)", "create view rv3 as select a as x from rt", "exec sp_rename 'rt.a', 'aa', 'COLUMN'");
        var ex = sim.AssertSqlError("exec sp_refreshview 'rv3'", 207);
        AreEqual(1, ex.Errors.Count);
    }

    [TestMethod]
    [DataRow("drop table vbt", "select * from vbv", 208, "vbv")]
    [DataRow("drop table vbt", "select * from vbv2", 208, "vbv2")]
    [DataRow("drop table vbt; create table vbt (b int)", "select 1; select * from vbv", 207, "vbv")]
    public void BrokenView_RaisesItsBinderErrorThenMsg4413(string change, string query, int number, string view)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table vbt (a int)", "create view vbv as select a from vbt", "create view vbv2 as select * from vbv", change);
        var ex = sim.AssertSqlError(query, number);
        AreEqual(2, ex.Errors.Count);
        AreEqual($"Could not use view or function '{view}' because of binding errors.", ex.Errors[1].Message);
    }
}
