using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A join view whose source is a single-table view passes a write down through
/// that view to its table, the way it does for a join view it reads (probed
/// 2026-09-30 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class JoinViewSingleTableViewSourceTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table a (id int primary key, n varchar(10)); create table b (id int primary key, aid int, q int);
            insert a values (1, 'one'), (2, 'two'); insert b values (10, 1, 5), (11, 2, 6)
            """,
            "create view vt as select id, n as nn from a",
            "create view vt2 as select id, upper(n) as nn from a where id < 5",
            "create view jv as select v.id as vid, v.nn, b.id as bid, b.q from vt v join b on v.id = b.aid",
            "create view jv2 as select v.id as vid, v.nn, b.id as bid, b.q from vt2 v join b on v.id = b.aid");
        return sim;
    }

    [TestMethod]
    public void Update_WritesTheViewsTable()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("update jv set nn = 'x' where vid = 1"));
        AreEqual("x", sim.ExecuteScalar("select n from a where id = 1"));
        AreEqual("two", sim.ExecuteScalar("select n from a where id = 2"));
    }

    [TestMethod]
    public void Update_TheOtherSide_StillWorks()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("update jv set q = 9 where vid = 1"));
        AreEqual(9, sim.ExecuteScalar("select q from b where id = 10"));
    }

    [TestMethod]
    public void Update_SpanningTheViewAndTheOtherTable_IsMsg4405()
        => _ = Seeded().AssertSqlError("update jv set nn = 'x', q = 1", 4405);

    [TestMethod]
    public void Update_ADerivedColumnOfTheViewSource_IsMsg4406NamingTheViewAsWritten()
    {
        var ex = Seeded().AssertSqlError("update jv2 set nn = 'x' where vid = 1", 4406);
        Contains("'jv2'", ex.Errors[0].Message);
    }

    [TestMethod]
    public void Update_ThroughAViewsWhere_ReachesOnlyItsRows()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("insert a values (9, 'nine'); insert b values (12, 9, 7)");
        AreEqual(2, sim.ExecuteNonQuery("update jv2 set q = 0 where q > 0 and vid < 100"));
        AreEqual(7, sim.ExecuteScalar("select q from b where id = 12"));
    }

    [TestMethod]
    public void Insert_WritesTheViewsTable()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("insert jv (vid, nn) values (7, 'seven')"));
        AreEqual("seven", sim.ExecuteScalar("select n from a where id = 7"));
    }

    [TestMethod]
    public void Insert_ADerivedColumnOfTheViewSource_IsMsg4406()
        => _ = Seeded().AssertSqlError("insert jv2 (vid, nn) values (7, 'seven')", 4406);

    [TestMethod]
    public void Delete_IsMsg4405()
        => _ = Seeded().AssertSqlError("delete jv where vid = 1", 4405);

    [TestMethod]
    public void Merge_UpdatesAndInsertsThroughTheView()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("merge jv using (select 1 k) s on jv.vid = s.k when matched then update set nn = 'm';"));
        AreEqual("m", sim.ExecuteScalar("select n from a where id = 1"));
        AreEqual(1, sim.ExecuteNonQuery("merge jv using (select 9 k) s on jv.vid = s.k when not matched then insert (vid, nn) values (9, 'nine');"));
        AreEqual("nine", sim.ExecuteScalar("select n from a where id = 9"));
    }

    // dbo.v1 joins u1's view s1.v2 over dbo's t1 to t2.
    private static Simulation OwnedView(string grants)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user u1 without login",
            "create schema s1 authorization u1",
            "create table dbo.t1 (a int, b int); insert dbo.t1 values (1, 2); create table dbo.t2 (a int, c int); insert dbo.t2 values (1, 3)",
            "create view s1.v2 as select a, b from dbo.t1",
            "create view dbo.v1 as select v2.a, v2.b, t2.c from s1.v2 join dbo.t2 on v2.a = t2.a");
        _ = sim.ExecuteNonQuery("grant select, insert, update, delete on dbo.v1 to c");
        if (grants.Length > 0)
            _ = sim.ExecuteNonQuery(grants);
        return sim;
    }

    private const string ViewGrant = "grant select, insert, update, delete on s1.v2 to c";
    private const string TableGrant = "grant select, insert, update, delete on dbo.t1 to c";

    [TestMethod]
    [DataRow("", "update dbo.v1 set b = 5", "The SELECT permission was denied on the object 'v2'")]
    [DataRow(ViewGrant, "update dbo.v1 set b = 5", "The SELECT permission was denied on the object 't1'")]
    [DataRow(ViewGrant, "insert dbo.v1 (a, b) values (7, 7)", "The INSERT permission was denied on the object 't1'")]
    [DataRow(TableGrant, "insert dbo.v1 (a, b) values (7, 7)", "The INSERT permission was denied on the object 'v2'")]
    [DataRow(ViewGrant, "merge dbo.v1 using (select 1 k) s on v1.a = s.k when matched then update set b = 5;", "The SELECT permission was denied on the object 't1'")]
    public void OwnershipChain_ThroughAnotherOwnersViewSource_IsRefusedAtTheFirstBrokenLink(string grants, string statement, string message)
    {
        var ex = OwnedView(grants).AssertSqlError($"execute as user = 'c'; {statement}", 229);
        Contains(message, ex.Errors[0].Message);
    }

    [TestMethod]
    public void OwnershipChain_WithEveryLinkGranted_Writes()
        => AreEqual(1, OwnedView(ViewGrant + ";" + TableGrant + "; grant select on dbo.t2 to c")
            .ExecuteNonQuery("execute as user = 'c'; update dbo.v1 set b = 5"));
}
