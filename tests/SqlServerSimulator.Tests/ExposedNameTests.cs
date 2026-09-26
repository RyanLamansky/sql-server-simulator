using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Two FROM sources may not share an exposed name: an unaliased table exposes
/// its name's last part, an alias or a CTE's own name is a correlation name.
/// Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ExposedNameTests
{
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create schema s", "create table t (a int); create table u (a int); create table s.t (a int)");
        return simulation;
    }

    [TestMethod]
    [DataRow("select 1 from t x, u X", 1011, "The correlation name 'X' is specified multiple times in a FROM clause.")]
    [DataRow("select 1 from t x left join (u x cross join t y) on 1 = 1", 1011, "The correlation name 'x' is specified multiple times in a FROM clause.")]
    [DataRow("select 1 from (select 1 a) d cross apply (select 2 b) d", 1011, "The correlation name 'd' is specified multiple times in a FROM clause.")]
    [DataRow("with c as (select 1 a) select 1 from c, t c", 1011, "The correlation name 'c' is specified multiple times in a FROM clause.")]
    [DataRow("delete t from t x, u x", 1011, "The correlation name 'x' is specified multiple times in a FROM clause.")]
    [DataRow("select 1 from T, u t", 1012, "The correlation name 't' has the same exposed name as table 'T'.")]
    [DataRow("select 1 from u t, s.t", 1012, "The correlation name 't' has the same exposed name as table 's.t'.")]
    [DataRow("select 1 from t, (select 1 a) t", 1012, "The correlation name 't' has the same exposed name as table 't'.")]
    [DataRow("select 1 from t, s.t, dbo.t", 1013, "The objects \"s.t\" and \"t\" in the FROM clause have the same exposed names. Use correlation names to distinguish them.")]
    [DataRow("select 1 from sys.objects, sys.objects", 1013, "The objects \"sys.objects\" and \"sys.objects\" in the FROM clause have the same exposed names. Use correlation names to distinguish them.")]
    [DataRow("select nosuch from t, t", 1013, "The objects \"t\" and \"t\" in the FROM clause have the same exposed names. Use correlation names to distinguish them.")]
    [DataRow("select 1 from t where exists (select 1 from u, u)", 1013, "The objects \"u\" and \"u\" in the FROM clause have the same exposed names. Use correlation names to distinguish them.")]
    public void RepeatedExposedName_IsRefused(string query, int number, string message)
        => Seeded().AssertSqlError(query, number, message);

    [TestMethod]
    public void UnaliasedRowsetFunctions_ExposeNothing()
        => AreEqual(1, new Simulation().ExecuteScalar("select count(*) from openjson('[1]'), openjson('[2]')"));

    [TestMethod]
    [DataRow("merge t using t on 1 = 1 when matched then delete;")]
    [DataRow("merge t x using u x on 1 = 1 when matched then delete;")]
    [DataRow("merge t using (select 1 a) t on 1 = 1 when matched then delete;")]
    public void MergeSourceNamedAsItsTarget_RaisesMsg5318(string merge)
        => new Simulation().AssertSqlError($"create table t (a int); create table u (a int); {merge}", 5318);

    [TestMethod]
    public void InADeferredStatement_TheErrorEndsTheBatchPastItsOwnTry()
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError("create table t (a int); begin try select 1 from t, t end try begin catch print 'caught' end catch; create table u (a int)", 1013);
        AreEqual(1, ex.Errors.Count);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('u')"));
    }
}
