using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// DML naming one of its statement's own CTEs as the target writes through it
/// as through a view with that body. Probed against SQL Server 2025 on
/// 2026-09-25.
/// </summary>
[TestClass]
public sealed class CteDmlTests
{
    private const string Setup = "create table t (id int, v int); insert t values (1, 1), (1, 2), (2, 3);";

    [TestMethod]
    [DataRow("with d as (select id, v from t where id = 1) delete from d where v = 2; select count(*) from t", 2)]
    [DataRow("with d as (select id, v from t) update d set v = 9 where id = 2; select sum(v) from t", 12)]
    [DataRow("with d (i, w) as (select id, v from t) update d set w = 0 where i = 1; select sum(v) from t", 3)]
    [DataRow("with d as (select id, v from t) insert into d values (5, 5); select count(*) from t", 4)]
    [DataRow("with d as (select id, v from t where id = 1) delete from d; select count(*) from t", 1)]
    public void WritesPassThroughToTheTable(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(Setup + sql));

    [TestMethod]
    [DataRow("with d as (select id, v * 2 w from t) update d set w = 1; select 5", 4406, "Update or insert of view or function 'd' failed because it contains a derived or constant field.")]
    [DataRow("with d as (select id, count(*) c from t group by id) delete from d; select 5", 4403, "Cannot update the view or function 'd' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.")]
    [DataRow("with d as (select id, count(*) c from t group by id) update d set id = 2 where id = 1; select 5", 4403, null)]
    public void RefusalsEndTheBatch(string sql, int number, string? message)
    {
        var ex = new Simulation().AssertSqlError(Setup + sql, number);
        HasCount(1, ex.Errors);
        if (message is not null)
            AreEqual(message, ex.Errors[0].Message);
    }

    /// <summary>
    /// A row-limited or windowed body writes only to the rows it yields — the
    /// dedupe idiom among them — and reads its derived columns off those rows.
    /// </summary>
    [TestMethod]
    [DataRow("with d as (select *, row_number() over (partition by id order by v) rn from t) delete from d where rn > 1; select string_agg(concat(id, ':', v), ',') within group (order by id) from t", "1:1,2:3")]
    [DataRow("with d as (select *, row_number() over (partition by id order by v desc) rn from t) update d set v = 0 where rn = 1; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:0,1:1,2:0")]
    [DataRow("with d as (select top 1 * from t order by v desc) delete from d; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:1,1:2")]
    [DataRow("create table u (id int, v int); insert u values (1, 1), (2, 2), (3, 3); with d as (select top 2 id, v * 2 w from u order by v desc) delete from d where w > 4; select string_agg(concat(id, ':', v), ',') within group (order by id) from u", "1:1,2:2")]
    [DataRow("with d as (select id, v from t order by v offset 1 rows fetch next 1 rows only) delete from d; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:1,2:3")]
    public void RowSelectingBody_WritesTheRowsItYields(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(Setup + sql));

    /// <summary>
    /// A limit that picks between rows its body's projection can't tell apart
    /// still writes the row it picked, and a MERGE through a row-selecting body
    /// matches only the rows the body yields (probed 2026-09-30 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table u (id int, v int); insert u values (1, 5), (1, 9); with d as (select top 1 id from u order by v desc) delete from d; select string_agg(concat(id, ':', v), ',') from u", "1:5")]
    [DataRow("create table u (id int, v int); insert u values (1, 5), (1, 9), (1, 7); with d as (select top 1 id from u order by v) update d set id = 3; select string_agg(concat(id, ':', v), ',') within group (order by v) from u", "3:5,1:7,1:9")]
    [DataRow("with d as (select top 1 * from t order by v) merge d using (values (1)) s(x) on d.id = s.x when matched then delete; select string_agg(concat(id, ':', v), ',') within group (order by v) from t", "1:2,2:3")]
    [DataRow("with d as (select top 2 * from t order by v desc) merge d using (values (1)) s(x) on d.id = s.x when matched then update set v = -v when not matched then insert (id, v) values (s.x, 0); select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:-2,1:1,2:3")]
    [DataRow("with d as (select *, row_number() over (partition by id order by v) rn from t) merge d using (values (1)) s(x) on d.rn > s.x when matched then delete; select string_agg(concat(id, ':', v), ',') within group (order by v) from t", "1:1,2:3")]
    [DataRow("with d as (select *, row_number() over (partition by id order by v desc) rn from t) merge d using (values (2)) s(x) on d.id = s.x when matched then update set v = d.rn * 10; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:1,1:2,2:10")]
    public void UndecidableRowSelection_WritesTheChosenRow(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(Setup + sql));

    [TestMethod]
    public void RowLimitedView_WritesTheRowsItYields()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            Setup,
            "create view vtop as select top 1 * from t order by v desc",
            "create view vrn as select *, row_number() over (partition by id order by v) rn from t");
        _ = simulation.ExecuteNonQuery("delete from vtop");
        AreEqual(3, simulation.ExecuteScalar("select sum(v) from t"));
        _ = simulation.ExecuteNonQuery("delete from vrn where rn > 1");
        AreEqual(1, simulation.ExecuteScalar("select sum(v) from t"));
        _ = simulation.AssertSqlError("update vrn set rn = 5", 4406);
        AreEqual(1, simulation.ExecuteNonQuery("insert vtop values (4, 4)"));
    }

    /// <summary>
    /// A view over a row-limited or windowed view writes the rows the lower
    /// view yields and its own filter admits (probed 2026-09-30 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void ViewOverRowSelectingView_WritesTheRowsItYields()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table u (id int, v int, w int); insert u values (1, 5, 100), (1, 3, 200), (2, 9, 300); create table x (id int, v int); insert x values (1, 5), (1, 3), (2, 9)",
            "create view vtop as select top 1 id from u order by v",
            "create view vover as select id from vtop",
            "create view vrn as select id, v, row_number() over (partition by id order by v) rn from x",
            "create view vfilter as select id, v, rn from vrn where id = 1",
            "create view vtoo as select id, rn, v from vfilter");
        _ = simulation.ExecuteNonQuery("update vover set id = 7");
        AreEqual("1:100,7:200,2:300", simulation.ExecuteScalar("select string_agg(concat(id, ':', w), ',') within group (order by w) from u"));
        _ = simulation.ExecuteNonQuery("merge vtoo as t using (select 2 k) s on t.rn = s.k when matched then update set v = -t.v;");
        AreEqual("1:-5,1:3,2:9", simulation.ExecuteScalar("select string_agg(concat(id, ':', v), ',') within group (order by id, abs(v) desc) from x"));
        _ = simulation.ExecuteNonQuery("delete vfilter where rn = 2");
        AreEqual("1:-5,2:9", simulation.ExecuteScalar("select string_agg(concat(id, ':', v), ',') within group (order by id) from x"));
    }

    private const string JoinSetup = """
        create table j (id int primary key, v int, k int); create table m (k int primary key, n int);
        insert j values (1, 10, 1), (2, 20, 2), (3, 30, 3), (4, 40, 1); insert m values (1, 100), (2, 200), (5, 500);
        """;

    private const string JRows = "; select string_agg(concat(id, ':', v), ' ') within group (order by id) from j";
    private const string MRows = "; select string_agg(concat(k, ':', n), ' ') within group (order by k) from m";

    /// <summary>
    /// A CTE reading several sources writes through as a join view does: an
    /// UPDATE or INSERT to the one table its columns land in, a MERGE likewise,
    /// a DELETE from the first table of its FROM (probed 2026-10-01 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k) update c set v = n where id < 4" + JRows, "1:100 2:200 3:30 4:40")]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k) update c set n = 7 where id = 1" + MRows, "1:7 2:200 5:500")]
    [DataRow("with c as (select j.id, j.v, m.n from j left join m on m.k = j.k) update c set n = 7" + MRows, "1:7 2:7 5:500")]
    [DataRow("with c as (select j.id, j.v, m.n from j, m where m.k = j.k) update c set v = 0" + JRows, "1:0 2:0 3:30 4:0")]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k where m.n > 150) update c set v = 0" + JRows, "1:10 2:0 3:30 4:40")]
    [DataRow("with c as (select j.id, j.v, j.k, m.n from j join m on m.k = j.k) insert into c (id, v, k) values (9, 90, 1)" + JRows, "1:10 2:20 3:30 4:40 9:90")]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k) merge c using (values (1, 5)) s (id, v) on c.id = s.id when matched then update set v = s.v;" + JRows, "1:5 2:20 3:30 4:40")]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k) merge c using (values (1)) s (id) on c.id = s.id when matched then delete;" + JRows, "2:20 3:30 4:40")]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k), c2 as (select id, v from c where n > 100) update c2 set v = 9" + JRows, "1:10 2:9 3:30 4:40")]
    public void MultiSourceBody_WritesTheTableItsColumnsLandIn(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(JoinSetup + sql));

    [TestMethod]
    public void MultiSourceBody_OverAJoinView()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(JoinSetup, "create view vj as select j.id, j.v, m.n from j join m on m.k = j.k");
        AreEqual("1:7 2:200 5:500", simulation.ExecuteScalar("with c as (select * from vj) update c set n = 7 where id = 1" + MRows));
    }

    [TestMethod]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k) update c set n = 7, v = 1", 4405, "View or function 'c' is not updatable because the modification affects multiple base tables.")]
    [DataRow("with c as (select j.id, j.v, m.n from j join m on m.k = j.k) delete from c", 4405, "View or function 'c' is not updatable because the modification affects multiple base tables.")]
    [DataRow("with c as (select j.id, j.v, j.k, m.n from j join m on m.k = j.k) insert into c (id, n) values (9, 90)", 4405, "View or function 'c' is not updatable because the modification affects multiple base tables.")]
    [DataRow("with c as (select j.id, j.v + m.n as s from j join m on m.k = j.k) update c set s = 1", 4406, "Update or insert of view or function 'c' failed because it contains a derived or constant field.")]
    public void MultiSourceBody_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError(JoinSetup + sql, number, message);

    /// <summary>
    /// A body a <c>UNION</c> tops derives every column, so an UPDATE, INSERT or
    /// MERGE writing one is Msg 4406, and a DELETE is Msg 4426 — the same under
    /// an <c>EXCEPT</c> or <c>INTERSECT</c> above it but for the DELETE, Msg
    /// 4403 as a plain <c>EXCEPT</c> takes, and Msg 4405 when the union's
    /// first branch joins (a <c>MERGE</c>'s delete aside). Through a view over
    /// a stored <c>UNION</c> view, Msg 4406 names the stored one bare, and
    /// otherwise the target as written (probed 2026-10-01 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("with c as (select id, v from j union select k, n from m) update c set v = 1", 4406, "c")]
    [DataRow("with c as (select id, v from j union all select k, n from m) update c set v = 1", 4406, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m) insert c values (1, 1)", 4406, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m) insert c (id) values (1)", 4406, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m) delete c", 4426, "c")]
    [DataRow("with c as (select id, v from j union all select k, n from m) delete c", 4426, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m) merge c using (values (1)) s (x) on c.id = s.x when matched then delete;", 4426, "c")]
    [DataRow("with c as (select id, v from j union all select k, n from m) merge c using (values (1)) s (x) on c.id = s.x when matched then update set v = 1;", 4406, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m except select 1, 1) update c set v = 1", 4406, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m except select 1, 1) delete c", 4403, "c")]
    [DataRow("with c as (select id, v from j except select 1, 1 union select k, n from m) delete c", 4426, "c")]
    [DataRow("with c as (select id, v from j except select k, n from m) update c set v = 1", 4403, "c")]
    [DataRow("with c as (select id, v from j union select k, n from m), c2 as (select * from c) delete c2", 4426, "c2")]
    [DataRow("with c as (select id, v from j union select k, n from m), c2 as (select * from c) update c2 set v = 1", 4406, "c2")]
    [DataRow("with c2 as (select * from (select id, v from j union select k, n from m) d) delete c2", 4426, "c2")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\ndelete vun", 4426, "vun")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\nupdate vun set v = 1", 4406, "vun")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\ncreate view vo as select * from vun;\ndelete vo", 4426, "vo")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\ncreate view vo as select * from vun;\nupdate vo set v = 1", 4406, "vun")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\ncreate view vo as select * from vun;\nwith c as (select * from vo) update c set v = 1", 4406, "vun")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\nupdate dbo.vun set v = 1", 4406, "dbo.vun")]
    [DataRow("create view vun as select id, v from j union select k, n from m;\ncreate view vo as select * from vun;\nupdate dbo.vo set v = 1", 4406, "vun")]
    [DataRow("create view vuj as select j.id, j.v from j join m on m.k = j.k union select k, n from m;\ndelete vuj", 4405, "vuj")]
    [DataRow("create view vuj as select j.id, j.v from j, m union all select k, n from m;\ncreate view vo as select * from vuj;\ndelete dbo.vo", 4405, "dbo.vo")]
    [DataRow("create view vjl as select k, n from m union all select j.id, j.v from j join m on m.k = j.k;\ndelete vjl", 4426, "vjl")]
    [DataRow("with c as (select j.id, j.v from j join m on m.k = j.k union all select k, n from m) delete c", 4405, "c")]
    [DataRow("delete d from (select j.id, j.v from j join m on m.k = j.k union all select k, n from m) d", 4405, "d")]
    [DataRow("create view vuj as select j.id, j.v from j join m on m.k = j.k union select k, n from m;\nmerge vuj using (values (1)) s (x) on vuj.id = s.x when matched then delete;", 4426, "vuj")]
    [DataRow("create view vd as select id, v + 1 d from j;\nupdate vd set d = 1", 4406, "vd")]
    [DataRow("create view vd as select id, v + 1 d from j;\ninsert dbo.vd values (1, 1)", 4406, "dbo.vd")]
    [DataRow("create view vd as select id, v + 1 d from j;\ninsert vd (id, d) values (1, 1)", 4406, "vd")]
    [DataRow("create view vd as select id, v + 1 d from j;\nmerge vd using (values (1)) s (x) on vd.id = s.x when matched then update set d = 1;", 4406, "vd")]
    [DataRow("create view vd as select id, v + 1 d from j;\ncreate view vd2 as select id, d from vd;\nupdate vd2 set d = 1", 4406, "vd2")]
    public void UnionBody_RefusesTheWrite(string sql, int number, string name)
    {
        var simulation = new Simulation();
        var batches = sql.Split("\n");
        simulation.ExecuteBatches(JoinSetup);
        simulation.ExecuteBatches(batches[..^1]);
        var message = number switch
        {
            4403 => $"Cannot update the view or function '{name}' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.",
            4405 => $"View or function '{name}' is not updatable because the modification affects multiple base tables.",
            4406 => $"Update or insert of view or function '{name}' failed because it contains a derived or constant field.",
            _ => $"View '{name}' is not updatable because the definition contains a UNION operator.",
        };
        simulation.AssertSqlError(batches[^1], number, message);
    }

    /// <summary>
    /// A body limiting rows it reads through another view, CTE or derived
    /// table writes the row its limit chose, even between rows its projection
    /// can't tell apart (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("delete vt", "1:5:100 1:9:200 1:7:300")]
    [DataRow("update vt2 set id = 9", "1:5:100 9:9:200 9:7:300 2:1:400")]
    [DataRow("delete vrn where rn > 1", "1:9:200 2:1:400")]
    [DataRow("merge vt2 as t using (values (1)) s (k) on t.id = s.k when matched then update set id = 5;", "1:5:100 5:9:200 5:7:300 2:1:400")]
    [DataRow("with c as (select top 1 id from v1 where id = 1 order by v desc) delete c", "1:5:100 1:7:300 2:1:400")]
    [DataRow("with a as (select id, v from x), c as (select top 1 id from a order by v desc) update c set id = 8", "1:5:100 8:9:200 1:7:300 2:1:400")]
    [DataRow("with c as (select top 1 id from v1w order by v) delete c", "1:5:100 1:9:200 1:7:300")]
    [DataRow("delete vvt", "1:9:200 1:7:300 2:1:400")]
    [DataRow("delete vt2 output deleted.id", "1:5:100 2:1:400")]
    public void LimitOverAnotherView_WritesTheChosenRow(string sql, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table x (id int, v int, w int); insert x values (1, 5, 100), (1, 9, 200), (1, 7, 300), (2, 1, 400)",
            "create view v1 as select id, v from x",
            "create view v1w as select id, v, w from x where w > 100",
            "create view vt as select top 1 id from v1 order by v",
            "create view vt2 as select top 2 id from v1 order by v desc",
            "create view vrn as select id, row_number() over (partition by id order by v desc) rn from v1",
            "create view vv as select id, v from v1 where id = 1",
            "create view vvt as select top 1 id from vv order by v");
        _ = simulation.ExecuteNonQuery(sql);
        AreEqual(expected, simulation.ExecuteScalar("select string_agg(concat(id, ':', v, ':', w), ' ') within group (order by w) from x"));
    }

    /// <summary>
    /// A view whose body is a CTE passes a write through to the table the CTE
    /// reads, as real does (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CteBodiedView_PassesWritesThrough()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(JoinSetup, "create view vcte as with c as (select id, v, k from j where id > 1) select id, v from c");
        AreEqual(1, simulation.ExecuteNonQuery("update vcte set v = 99 where id = 2"));
        AreEqual(1, simulation.ExecuteNonQuery("delete vcte where id = 4"));
        AreEqual(1, simulation.ExecuteNonQuery("insert vcte values (9, 9)"));
        AreEqual("1:10 2:99 3:30 9:9", simulation.ExecuteScalar("select string_agg(concat(id, ':', v), ' ') within group (order by id) from j"));
    }
}
