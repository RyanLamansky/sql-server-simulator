using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>WINDOW</c> clause over every kind of FROM source, with no FROM at all,
/// and inside a query nested in another block's clause — each query block owns
/// its named windows. Probed 2026-09-29 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class NamedWindowScopeTests
{
    /// <summary>
    /// Rowset functions, <c>VALUES</c>, derived tables, CTEs, TVFs and views
    /// all take a WINDOW clause, with refinement and chaining; a rowset
    /// function's alias parse leaves <c>WINDOW w AS (</c> to the clause.
    /// </summary>
    [TestMethod]
    [DataRow("select value v, sum(value) over w s from generate_series(1, 4) window w as (order by value)", "1:1,2:3,3:6,4:10")]
    [DataRow("select value v, row_number() over w s from generate_series(1, 6) window w as (partition by value % 2 order by value)", "1:1,2:1,3:2,4:2,5:3,6:3")]
    [DataRow("select value v, sum(value) over (w rows between 1 preceding and current row) s from generate_series(1, 4) window w as (order by value)", "1:1,2:3,3:5,4:7")]
    [DataRow("select value v, rank() over w2 s from generate_series(1, 4) window w as (partition by value % 2), w2 as (w order by value desc)", "1:2,2:2,3:1,4:1")]
    [DataRow("select g.value v, sum(g.value) over w s from generate_series(1, 3) g window w as (order by g.value)", "1:1,2:3,3:6")]
    [DataRow("select x v, sum(x) over w s from (values (1), (2), (3)) v(x) window w as (order by x)", "1:1,2:3,3:6")]
    [DataRow("select x v, lag(x) over w s from (select 1 x union all select 2 union all select 3) d window w as (order by x)", "1:,2:1,3:2")]
    [DataRow("select cast([key] as int) v, row_number() over w s from openjson('[10,20,30]') window w as (order by [key] desc)", "0:3,1:2,2:1")]
    [DataRow("select ascii(value) v, dense_rank() over w s from string_split('b,a,c', ',') window w as (order by value)", "97:1,98:2,99:3")]
    [DataRow("select x v, sum(x) over w s from dbo.f() window w as (order by x)", "1:1,2:3,3:6")]
    [DataRow("select x v, sum(x) over w s from dbo.g() window w as (order by x)", "1:1,2:3,3:6")]
    [DataRow("select x v, sum(x) over w s from vw window w as (order by x)", "1:1,2:3,3:6")]
    [DataRow("select a * 10 + value v, sum(value) over w s from t cross join generate_series(1, 2) window w as (partition by a order by value)", "11:1,12:3,21:1,22:3")]
    [DataRow("select a * 10 + value v, count(*) over w s from t cross apply generate_series(1, t.a) window w as (partition by a)", "11:1,21:2,22:2")]
    public void WindowClause_OverEverySource(string query, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int); insert t values (1), (2);",
            "create function dbo.f() returns table as return select 1 x union all select 2 union all select 3",
            "create function dbo.g() returns @t table (x int) as begin insert @t values (1), (2), (3); return end",
            "create view vw as select 1 x union all select 2 union all select 3");
        AreEqual(expected, simulation.ExecuteScalar($"select string_agg(concat(v, ':', s), ',') within group (order by v) from ({query}) q"));
    }

    /// <summary>A WINDOW clause needs no FROM ahead of it, even straight after
    /// an element where an alias could stand, and the ORDER BY after it reads
    /// the window.</summary>
    [TestMethod]
    [DataRow("select 1 window w as (order by (select 1))", 1)]
    [DataRow("select 1 x window w as (order by (select 1))", 1)]
    [DataRow("select sum(1) over w s window w as (order by (select 1)) order by s", 1)]
    [DataRow("select 1 x where 1 = 1 window w as (order by (select 1))", 1)]
    public void WindowClause_WithoutFrom(string query, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(query));

    /// <summary>
    /// A subquery's windows are its own: it neither answers its enclosing
    /// block's references nor collides with its names.
    /// </summary>
    [TestMethod]
    [DataRow("select x, sum(x) over w s, (select max(y) from t2) q from t window w as (order by x)", "1:1:6,2:3:6")]
    [DataRow("select x, sum(x) over w s, (select 1) q from t window w as (order by x)", "1:1:1,2:3:1")]
    [DataRow("select x, sum(x) over w s, (select top 1 count(*) over w from t2 window w as (partition by y)) q from t window w as (order by x)", "1:1:1,2:3:1")]
    [DataRow("select x, 0 s, (select top 1 sum(y) over w from t2 window w as (order by y) order by y desc) q from t", "1:0:11,2:0:11")]
    public void WindowClause_NestedBlocksKeepTheirOwn(string query, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            "create table t (x int); insert t values (1), (2); create table t2 (y int); insert t2 values (5), (6);" +
            $"select string_agg(concat(x, ':', s, ':', q), ',') within group (order by x) from ({query}) d"));

    /// <summary>A windowed function in a subquery under a WHERE belongs to
    /// the subquery's select list, where it is legal.</summary>
    [TestMethod]
    public void WindowInSubqueryUnderWhere_IsLegal()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table t (x int); insert t values (1), (2); select count(*) from t where exists (select sum(x) over w from t t2 window w as (order by x))"));

    /// <summary>
    /// Msg 5362's state says which miss it was: 3 when the block has no WINDOW
    /// clause (a subquery's clause doesn't count), 4 when an OVER names none
    /// of the clause's windows, 7 when a definition names one — itself
    /// included.
    /// </summary>
    [TestMethod]
    [DataRow("select sum(x) over w2 from t", 3)]
    [DataRow("select sum(1) over w2", 3)]
    [DataRow("select sum(x) over w, (select top 1 x from t window w as (order by x)) from t", 3)]
    [DataRow("select sum(x) over w2 from t window w as (order by x)", 4)]
    [DataRow("select value from generate_series(1, 2) window w as (order by value) order by sum(value) over w2", 4)]
    [DataRow("select sum(x) over w from t window w as (w order by x)", 7)]
    [DataRow("select sum(x) over w from t window w as (w9 order by x)", 7)]
    public void UndefinedWindow_StateNamesTheMiss(string query, int state)
    {
        var ex = new Simulation().AssertSqlError("create table t (x int); " + query, 5362);
        AreEqual((byte)state, ex.State);
    }
}
