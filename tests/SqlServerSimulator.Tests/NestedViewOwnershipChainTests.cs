using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A write through a view over a view checks the ownership chain one link at a
/// time, each object's owner compared with the next one's (probed 2026-09-29
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class NestedViewOwnershipChainTests
{
    // dbo.td is dbo-owned, s1.v2 sits in u1's schema, dbo.v1 reads s1.v2 and
    // the caller c is granted on v1 (plus whatever else the case adds).
    private static Simulation Nested(string v2Source, string grants)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user u1 without login",
            "create schema s1 authorization u1",
            "create table dbo.td (a int, b int); insert dbo.td values (1, 2); create table s1.t1 (a int, b int); insert s1.t1 values (1, 2)",
            $"create view s1.v2 as select a, b from {v2Source}",
            "create view dbo.v1 as select a, b from s1.v2");
        _ = sim.ExecuteNonQuery(grants);
        return sim;
    }

    [TestMethod]
    [DataRow("dbo.td", "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("dbo.td", "grant select, update on dbo.v1 to c; grant select, update on s1.v2 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'td'")]
    [DataRow("dbo.td", "grant insert on dbo.v1 to c", "insert dbo.v1 values (7, 8)", "The INSERT permission was denied on the object 'v2'")]
    [DataRow("dbo.td", "grant delete, select on dbo.v1 to c", "delete dbo.v1 where a = 1", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("dbo.td", "grant select, update, insert, delete on dbo.v1 to c", "merge dbo.v1 t using (select 1 a, 9 b) s on t.a = s.a when matched then update set b = s.b;", "The SELECT permission was denied on the object 'v2'")]
    [DataRow("s1.t1", "grant select, update on dbo.v1 to c", "update dbo.v1 set b = 5 where a = 1", "The SELECT permission was denied on the object 'v2'")]
    public void BrokenLink_NamesTheObjectWhoseOwnerDiffersFromItsPredecessor(string v2Source, string grants, string statement, string message)
    {
        var ex = Nested(v2Source, grants).AssertSqlError($"execute as user = 'c'; {statement}", 229);
        Contains(message, ex.Errors[0].Message);
    }

    [TestMethod]
    public void IntactLinkBelowABrokenOne_NeedsOnlyTheGrantOnTheMiddleView()
        => AreEqual(1, Nested("s1.t1", "grant select, update on dbo.v1 to c; grant select, update on s1.v2 to c")
            .ExecuteNonQuery("execute as user = 'c'; update dbo.v1 set b = 5 where a = 1"));
}
