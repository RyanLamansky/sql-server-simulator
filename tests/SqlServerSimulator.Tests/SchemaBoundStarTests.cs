using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A select-list star in a schema-bound view's or function's body is Msg 1054,
/// raised while the body parses, and real's parser recovers past it as past a
/// syntax error (probed 2026-09-30 against SQL Server 2025). Each expectation
/// lists the batch's errors as <c>number/state/line</c>.
/// </summary>
[TestClass]
public sealed class SchemaBoundStarTests
{
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table dbo.t (a int, b int)");
        return simulation;
    }

    private static string Errors(string sql)
    {
        var error = ThrowsExactly<SimulatedSqlException>(() => Seeded().ExecuteScalar(sql));
        return string.Join(", ", error.Errors.Select(entry => $"{entry.Number}/{entry.State}/{entry.LineNumber}"));
    }

    [TestMethod]
    [DataRow("create view v with schemabinding as select * from dbo.t", "1054/6/1")]
    [DataRow("create view v with schemabinding as select t.* from dbo.t", "1054/7/1")]
    [DataRow("create view v with schemabinding as\nselect a,\n * from dbo.t", "1054/6/3")]
    [DataRow("create view v with schemabinding as select a from dbo.t where exists (select * from dbo.t x)", "1054/6/1")]
    [DataRow("create view v with schemabinding as select a from dbo.t where exists (select t.* from dbo.t x)", "1054/7/1")]
    [DataRow("create view v with schemabinding as select a from dbo.t where a in (select * from dbo.t x)", "1054/6/1")]
    [DataRow("create view v with schemabinding as select a from (select * from dbo.t) d", "1054/6/1")]
    [DataRow("create view v with schemabinding as with c as (select * from dbo.t) select a from c", "1054/6/1")]
    [DataRow("create view v with schemabinding as with c as (select a from dbo.t) select * from c", "1054/6/1")]
    [DataRow("create view v with schemabinding as (select * from dbo.t)", "1054/6/1")]
    [DataRow("create view v with schemabinding as select a from dbo.t union all select * from dbo.t", "1054/6/1")]
    [DataRow("create view v with schemabinding as select a from dbo.t union all (select * from dbo.t)", "1054/6/1")]
    [DataRow("create view v with schemabinding as select * from dbo.t where zz = 1", "1054/6/1")]
    [DataRow("create view v with schemabinding as select zz, * from dbo.t", "1054/6/1")]
    [DataRow("create view v with schemabinding as select * from dbo.missing", "1054/6/1")]
    [DataRow("create view v with schemabinding as select * from t", "1054/6/1")]
    [DataRow("create view v with schemabinding as select *, *, * from dbo.t", "1054/6/1")]
    [DataRow("create view v with schemabinding as select * from dbo.t where", "1054/6/1")]
    [DataRow("create view v with schemabinding as select * from dbo.t group by all a", "1054/6/1")]
    [DataRow("create view v with schemabinding as select a b c, * from dbo.t", "102/1/1")]
    [DataRow("create function f() returns table with schemabinding as return select * from dbo.t", "1054/6/1")]
    [DataRow("create function f() returns table with schemabinding as return\n(select *\nfrom dbo.t)", "1054/6/2")]
    [DataRow("create function f() returns table with schemabinding as return with c as (select a from dbo.t) select * from c", "1054/6/1")]
    [DataRow("create function f() returns int with schemabinding as begin return (select top 1 a from (select * from dbo.t) q) end", "1054/6/1")]
    [DataRow("create function f() returns int with schemabinding as begin if exists (select * from dbo.t) return 1; return 0 end", "1054/6/1")]
    [DataRow("create function f() returns int with schemabinding as begin declare c cursor for select * from dbo.t; return 1 end", "1054/1/1")]
    [DataRow("create function f() returns int with schemabinding as begin select * from dbo.t; return 1 end", "1054/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r select * from dbo.t; return end", "1054/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r (a, b) select * from dbo.t; return end", "1054/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r select a, b from dbo.t union all select * from dbo.t; return end", "1054/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin with c as (select a, b from dbo.t) insert @r select * from c; return end", "1054/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r select a, b from (select * from dbo.t) q; return end", "1054/6/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin update @r set a = 1 where exists (select * from dbo.t); return end", "1054/6/1")]
    [DataRow("create function f() returns int with schemabinding as begin declare @x int;\nselect @x = a from dbo.t where 1 = 0;\nselect @x = nosuch from dbo.t where exists (select * from dbo.t);\nreturn 1 end", "1054/6/3")]
    public void Star_Msg1054(string sql, string expected) => AreEqual(expected, Errors(sql));

    /// <summary>
    /// Real's parser recovers past the refusal and reads the rest of the body
    /// as statements, so a later star in a statement's own query is state 1
    /// or 2, a derived table's alias after it is Msg 102 (Msg 156 on its
    /// <c>AS</c>), and a <c>GROUP BY ALL</c> refused first leaves a later star
    /// to be reported too.
    /// </summary>
    [TestMethod]
    [DataRow("create view v with schemabinding as select *\nfrom dbo.t where exists (select *\nfrom dbo.t)", "1054/6/1, 1054/1/2")]
    [DataRow("create view v with schemabinding as select a from dbo.t union all select * from dbo.t union all select * from dbo.t", "1054/6/1, 1054/1/1")]
    [DataRow("create view v with schemabinding as select * from (select a from dbo.t) q", "1054/6/1, 102/1/1")]
    [DataRow("create view v with schemabinding as select * from (select a from dbo.t) as q", "1054/6/1, 156/1/1")]
    [DataRow("create view v with schemabinding as select * from dbo.t cross apply (select 1 x) q", "1054/6/1, 102/1/1")]
    [DataRow("create view v with schemabinding as select * from (values (1)) q(a)", "1054/6/1")]
    [DataRow("create view v with schemabinding as select a from dbo.t group by all a union select * from dbo.t", "1054/8/1, 1054/1/1")]
    [DataRow("create view v with schemabinding as select a from dbo.t where exists (select 1 from dbo.t group by all a) and exists (select * from dbo.t)", "1054/8/1, 1054/1/1")]
    [DataRow("create function f() returns int with schemabinding as begin declare @x int; select @x = a from dbo.t where exists(select * from dbo.t);\nselect @x = a from dbo.t where exists(select * from dbo.t); return 1 end", "1054/6/1, 1054/6/2")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r select * from dbo.t where exists (select * from dbo.t); return end", "1054/1/1, 1054/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin declare @x int; select @x = a from dbo.t where exists(select * from dbo.t); insert @r select t.* from dbo.t; return end", "1054/6/1, 1054/2/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r select * from (select a, b from dbo.t) q; return end", "1054/1/1, 102/1/1")]
    [DataRow("create function f() returns @r table (a int, b int) with schemabinding as begin insert @r (select * from dbo.t); return end", "156/1/1, 1054/1/1")]
    [DataRow("create function f() returns int with schemabinding as begin declare @x int;\nselect @x = a from dbo.t group by all a;\nif exists (select * from dbo.t) set @x = 1;\nreturn 1 end", "1054/8/2, 1054/6/3")]
    [DataRow("create view v with schemabinding as select a, count_big(*) c from dbo.t group by all a with cube", "1054/8/1, 319/1/1")]
    public void Star_RecoveryReportsWhatFollows(string sql, string expected) => AreEqual(expected, Errors(sql));

    /// <summary>
    /// Stars that aren't a select-list element — <c>COUNT(*)</c>,
    /// <c>CHECKSUM(*)</c>, multiplication, an <c>OUTPUT</c> clause's
    /// <c>inserted.*</c> — pass, as does a star in a body that isn't schema bound.
    /// </summary>
    [TestMethod]
    [DataRow("create view v with schemabinding as select count_big(*) c from dbo.t")]
    [DataRow("create view v with schemabinding as select a * b c from dbo.t")]
    [DataRow("create view v with schemabinding as select checksum(*) c from dbo.t")]
    [DataRow("create view v with schemabinding as select binary_checksum(*) c from dbo.t")]
    [DataRow("create view v with schemabinding as select a, (select count(*) from dbo.t x) n from dbo.t")]
    [DataRow("create function v() returns @r table (a int, b int) with schemabinding as begin insert @r output inserted.* into @r select a, b from dbo.t; return end")]
    [DataRow("create view v as select * from dbo.t")]
    [DataRow("create function v() returns table as return select * from dbo.t")]
    public void OtherStars_Pass(string sql)
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery(sql);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.objects where name = 'v'"));
    }

    /// <summary>
    /// The refusal is a parse error, so it outranks an existing object of the
    /// name, and an inline function's body binds ahead of that name check as a
    /// multi-statement function's does (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    public void OverAnExistingObject_TheBodysErrorsComeFirst()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches("create function dbo.fz() returns table as return select a from dbo.t", "create view dbo.vz as select a from dbo.t");
        AreEqual((byte)6, simulation.AssertSqlError("create function dbo.fz() returns table with schemabinding as return select * from dbo.t", 1054).State);
        AreEqual((byte)6, simulation.AssertSqlError("create view dbo.vz with schemabinding as select * from dbo.t", 1054).State);
        _ = simulation.AssertSqlError("create function dbo.fz() returns table with schemabinding as return select nosuch from dbo.t", 207);
        _ = simulation.AssertSqlError("create function dbo.fz() returns table as return select nosuch from dbo.t", 207);
        _ = simulation.AssertSqlError("create function dbo.fz() returns table as return select a from dbo.t where", 102);
    }

    /// <summary>
    /// A <c>CREATE SCHEMA</c> element view after a schema-bound one is no part
    /// of its body, and takes a star (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CreateSchemaElementAfterASchemaBoundView_TakesAStar()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create schema zs create view v1 with schemabinding as select a from dbo.t create view v2 as select * from dbo.t");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from sys.objects where schema_id = schema_id('zs')"));
    }
}
