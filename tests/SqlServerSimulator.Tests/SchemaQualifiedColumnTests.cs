using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A column prefixed by its object's schema (<c>dbo.t.a</c>) or database too
/// binds only to an unaliased source whose name resolved in that schema and
/// database; anything else is Msg 4104 on the whole name, and a star is
/// Msg 107. Empty parts (<c>..t.a</c>) match anything. Every expectation
/// probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SchemaQualifiedColumnTests
{
    private static Simulation Fixture()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create schema s",
            "create table t (a int); insert t values (1); create table s.u (b int); insert s.u values (2); create synonym s.sy for t",
            "create view v as select a from t");
        return simulation;
    }

    [TestMethod]
    [DataRow("select dbo.t.a from t")]
    [DataRow("select dbo.T.a from dbo.t")]
    [DataRow("select simulated.dbo.t.a from t")]
    [DataRow("select ..t.a from t")]
    [DataRow("select s.u.b - 1 from s.u")]
    [DataRow("select dbo.v.a from v")]
    [DataRow("select s.sy.a from s.sy")]
    [DataRow("select count(*) from t where exists (select 1 from s.u where s.u.b > dbo.t.a)")]
    [DataRow("select dbo.t.* from t")]
    public void TheResolvedSchemaBinds(string query)
        => AreEqual(1, Fixture().ExecuteScalar(query));

    [TestMethod]
    [DataRow("select s.t.a from t", "s.t.a")]
    [DataRow("select dbo.t.a from t as t", "dbo.t.a")]
    [DataRow("select dbo.x.a from t x", "dbo.x.a")]
    [DataRow("select dbo.u.b from s.u", "dbo.u.b")]
    [DataRow("select master.dbo.t.a from t", "master.dbo.t.a")]
    [DataRow("select dbo..a from t", "dbo..a")]
    [DataRow("select dbo.sy.a from s.sy", "dbo.sy.a")]
    [DataRow("with c as (select 1 a) select dbo.c.a from c", "dbo.c.a")]
    [DataRow("select a from t order by s.t.a", "s.t.a")]
    [DataRow("select 1 from t where exists (select 1 from s.u where s.u.b = s.t.a)", "s.t.a")]
    [DataRow("update t set a = 2 where s.t.a = 1", "s.t.a")]
    [DataRow("delete dbo.t where s.t.a = 1", "s.t.a")]
    public void AnotherSchemaOrAnAlias_IsUnbound(string query, string name)
        => Fixture().AssertSqlError(query, 4104, $"The multi-part identifier \"{name}\" could not be bound.");

    [TestMethod]
    [DataRow("select s.t.* from t", "s.t")]
    [DataRow("select dbo.t.* from t as t", "dbo.t")]
    public void AStarPrefix_IsMsg107(string query, string prefix)
        => Fixture().AssertSqlError(query, 107, $"The column prefix '{prefix}' does not match with a table name or alias name used in the query.");
}
