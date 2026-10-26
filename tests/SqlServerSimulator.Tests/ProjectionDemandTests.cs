using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A derived table's, view's, CTE's, inline function's, <c>APPLY</c> body's
/// or <c>VALUES</c> list's column the enclosing query never reads is never
/// computed, as real's optimizer removes it — so an error only that column
/// would raise never appears — while a column the query reads anywhere, or
/// one the body itself needs (a <c>DISTINCT</c> or <c>UNION</c> comparing whole
/// rows, a <c>TOP</c> ordering by it), is computed and raises. Probed
/// 2026-10-17 against SQL Server 2025, where row <c>k = 3</c> of every
/// <c>10 / (k - 3)</c> below divides by zero.
/// </summary>
[TestClass]
public sealed class ProjectionDemandTests
{
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table src (k int primary key, v int, s varchar(10));
            insert src values (1, 1, '1'), (2, 2, 'x'), (3, 3, '3'), (4, 4, '4');
            create table src2 (k int, v int);
            insert src2 values (1, 1), (2, 1), (3, 2), (4, 2);
            """,
            "create view v1 as select k, 10 / (k - 3) as x, cast(s as int) as c from src",
            "create view v2 as select k, x from v1",
            "create view v3 as select k, 10 / (k - 3) as x from src union all select k, 1 from src",
            "create function tvf1(@d int) returns table as return select k, 10 / (k - @d) as x from src",
            "create function f_err(@k int) returns int as begin declare @t table (a int); insert @t values (1); return 10 / (@k - 3) end");
        return simulation;
    }

    [TestMethod]
    [DataRow(4, "select count(*) from (select k, 10 / (k - 3) as x from src) d")]
    [DataRow(4, "select count(*) from (select k from (select k, 10 / (k - 3) as x from src) d) e")]
    [DataRow(4, "select count(*) from (select * from (select k, 10 / (k - 3) as x from src) d) e where k > 0")]
    [DataRow(4, "select count(*) from v1")]
    [DataRow(4, "select count(k) from v2")]
    [DataRow(8, "select count(k) from v3")]
    [DataRow(4, "select count(*) from tvf1(3)")]
    [DataRow(4, "with c as (select k, 10 / (k - 3) as x from src) select count(*) from c")]
    [DataRow(4, "with c as (select k, 10 / (k - 3) as x from src) select count(*) from c a join c b on a.k = b.k")]
    [DataRow(4, "with c1 as (select k, 10 / (k - 3) as x from src), c2 as (select k from c1) select count(*) from c2")]
    [DataRow(8, "select count(k) from (select k, 10 / (k - 3) as x from src union all select k, 1 from src) d")]
    [DataRow(4, "select count(k) from (select distinct k, v, 10 / (k - 3) as x from src) d")]
    [DataRow(4, "select count(k) from (select top 10 k, 10 / (k - 3) as x from src order by k) d")]
    [DataRow(4, "select count(*) from (select k, (select v from src) as x from src) d")]
    [DataRow(4, "select count(*) from (select k, dbo.f_err(k) as x from src) d")]
    [DataRow(4, "select count(*) from (select k, 1 / 0 as x from src) d")]
    [DataRow(4, "select count(*) from src s cross apply (select 10 / (s.k - 3) as x) a")]
    [DataRow(4, "select count(*) from src s outer apply (select 10 / (s.k - 3) as x) a")]
    [DataRow(4, "select count(*) from src s cross apply (values (10 / (s.k - 3))) a(x)")]
    [DataRow(4, "select count(a) from (values (1, 1 / 0), (2, 3), (3, 4), (4, 5)) d(a, b)")]
    [DataRow(4, "select count(*) from src a left join (select k, 10 / (k - 3) as x from src) d on a.k = d.k")]
    [DataRow(4, "select count(*) from src s where exists (select 1 from (select k, 10 / (k - 3) as x from src) d where d.k = s.k)")]
    [DataRow(4, "select count(*) from src s where s.k in (select k from (select k, 10 / (k - 3) as x from src) d)")]
    [DataRow(4, "select count(*) from (select (select d.k) as kk from (select k, 10 / (k - 3) as x from src) d) e")]
    [DataRow(2, "select count(*) from (select v, sum(10 / (k - 3)) as x from src2 group by v) d")]
    [DataRow(2, "select count(*) from (select v, max(10 / (k - 3)) as x, string_agg(cast(k as varchar), ',') within group (order by 10 / (k - 3)) as y from src2 group by v) d")]
    [DataRow(3, "select count(*) from (select v, sum(10 / (k - 3)) as x from src2 group by grouping sets ((v), ())) d")]
    [DataRow(1, "select count(*) from (select 1 as one, sum(10 / (k - 3)) as x from src) d")]
    [DataRow(4, "select count(*) from (select k, sum(10 / (k - 3)) over (partition by v) as x, row_number() over (order by 10 / (k - 3)) as rn from src2) d")]
    [DataRow(2, "select count(*) from (select v, sum(sum(10 / (k - 3))) over () as x from src2 group by v) d")]
    public void UnreadColumn_IsNeverComputed(int expected, string query) => AreEqual(expected, Seeded().ExecuteScalar(query));

    [TestMethod]
    [DataRow("select x from (select k, 10 / (k - 3) as x from src) d")]
    [DataRow("select k from (select k, 10 / (k - 3) as x from src) d order by x")]
    [DataRow("select k from (select k, 10 / (k - 3) as x from src) d where x > 0")]
    [DataRow("select count(x) from (select k, 10 / (k - 3) as x from src) d")]
    [DataRow("select a.k from (select k, 10 / (k - 3) as x from src) a join src b on a.x = b.k")]
    [DataRow("select v from (select distinct v, 10 / (k - 3) as x from src2) d")]
    [DataRow("select k from (select k, 10 / (k - 3) as x from src union select k, 1 from src) d")]
    [DataRow("select k from (select k, 10 / (k - 3) as x from src intersect select k, 1 from src) d")]
    [DataRow("select k from (select top 10 k, 10 / (k - 3) as x from src order by x) d")]
    [DataRow("select v from (select v, 10 / (k - 3) as x from src2 group by v, 10 / (k - 3)) d")]
    [DataRow("select v from (select v, sum(10 / (k - 3)) as x from src2 group by v having sum(10 / (k - 3)) > 0) d")]
    [DataRow("select v from (select v, k, 10 / (k - 3) as x from src2) d group by v having max(x) > 0")]
    [DataRow("select k from (select k, row_number() over (order by 10 / (k - 3)) as rn from src) d where rn > 0")]
    [DataRow("select [1] from (select v, k, 10 / (k - 3) as x from src2) d pivot (max(k) for v in ([1], [2])) p")]
    [DataRow("with r as (select 1 as n, 1 / 0 as z union all select n + 1, z from r where n < 3) select n from r")]
    [DataRow("with c as (select k, v, 10 / (k - 3) as x from src) select a.k, b.x from c a join c b on a.k = b.k")]
    [DataRow("select k from (select k, 10 / (k - 3) as x from src) d where exists (select 1 from src2 where src2.k = d.x)")]
    [DataRow("select x from v1 where k <> 2")]
    public void ReadColumn_IsComputed(string query) => _ = Seeded().AssertSqlError(query, 8134);

    /// <summary>
    /// A read column is evaluated where its reader uses it, as real merges the
    /// body's projection into the reading query: a <c>CASE</c> / <c>IIF</c>
    /// arm the guard skips, a filter beneath the projection or an <c>APPLY</c>
    /// body's guarded column never divides by zero for <c>k = 3</c> (probed
    /// 2026-10-17 against SQL Server 2025, each shape answering alike under
    /// <c>RECOMPILE</c>, <c>MAXDOP 1</c>, a forced parallel plan, hash, merge
    /// and loop hints, and heap, clustered and nonclustered layouts).
    /// </summary>
    [TestMethod]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(iif(k = 3, 0, x) as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from v1")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from v2")]
    [DataRow("-5,-10,0,10", "with c as (select k, 10 / (k - 3) as x from src) select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from c")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select top 10 k, 10 / (k - 3) as x from src order by k) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src group by k) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select distinct k, 10 / (k - 3) as x from src) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x, row_number() over (order by k) as rn from src) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when s.k = 3 then 0 else a.x end as varchar), ',') within group (order by s.k) from src s cross apply (select 10 / (s.k - 3) as x) a")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when v.kk = 3 then 0 else v.x end as varchar), ',') within group (order by s.k) from src s cross apply (values (s.k, 10 / (s.k - 3))) v(kk, x)")]
    [DataRow("-5,-10,10", "select string_agg(cast(v.x as varchar), ',') within group (order by s.k) from src s cross apply (values (s.k, 10 / (s.k - 3))) v(kk, x) where v.kk <> 3")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from v1 where k <> 3")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from v2 where k <> 3")]
    [DataRow("-5,-10,10", "with c as (select k, 10 / (k - 3) as x from src) select string_agg(cast(x as varchar), ',') within group (order by k) from c where k <> 3")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select * from v1) e where k <> 3")]
    [DataRow("-4,-9,11", "select string_agg(cast(y as varchar), ',') within group (order by k) from (select k, x + 1 as y from v1) e where k <> 3")]
    [DataRow("-5,-10,10", "select string_agg(cast(d.x as varchar), ',') within group (order by a.k) from src a left join v1 d on a.k = d.k and d.k <> 3")]
    [DataRow("-10,-5,10", "select string_agg(cast(x as varchar), ',') within group (order by x) from v1 where k <> 3")]
    [DataRow("1,0,3,4", "select string_agg(cast(case when s = 'x' then 0 else c end as varchar), ',') within group (order by k) from (select k, s, cast(s as int) as c from src) d")]
    [DataRow("1,2,3,4", "select string_agg(cast(isnull(k, x) as varchar), ',') within group (order by k) from v1")]
    [DataRow("3", "select cast(count(*) as varchar) from v1 where k <> 3 and x > -100")]
    [DataRow("4", "select cast(count(*) as varchar) from v1 where k = 3 or x > -100")]
    [DataRow("4", "select cast(count(*) as varchar) from v1 where case when k = 3 then 1 else x end > -100")]
    [DataRow("3", "select cast(count(*) as varchar) from (select x from v1 where k <> 3 group by x) g")]
    [DataRow("3", "select cast(count(*) as varchar) from src s where exists (select 1 from v1 d where d.k = s.k and d.k <> 3 and d.x > -100)")]
    [DataRow("-5", "select cast(sum(case when k = 3 then 0 else x end) as varchar) from v1")]
    [DataRow("1,0,3,4", "select string_agg(cast(case when k = 2 then 0 else c end as varchar), ',') within group (order by k) from (select k, cast(s as int) as c from src) d")]
    [DataRow("1,0,3,4", "select string_agg(cast(case when k = 2 then 0 else c end as varchar), ',') within group (order by k) from (select k, c from (select k, cast(s as int) as c from src) i) d")]
    [DataRow("1,0,3,4", "select string_agg(cast(case when k = 2 then 0 else c end as varchar), ',') within group (order by k) from v1")]
    [DataRow("1,0,3,4", "select string_agg(cast(case when k = 2 then 0 else c end as varchar), ',') within group (order by k) from (select * from v1) e")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, (select 10 / (s.k - 3)) as x from src s) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, (select 10 / (s.v - 3)) as x from src s) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from tvf1(3)")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from tvf1(3) where k <> 3")]
    [DataRow("-10", "select cast(sum(case when k = 3 then 0 else x end) as varchar) from (select k, 10 / (k - 3) as x from src union all select k, 10 / (k - 3) from src) d")]
    [DataRow("-5,-10,0,10", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, sum(10 / (k - 3)) as x from src group by k) d")]
    [DataRow("-4,-9,0,11", "select string_agg(cast(case when k = 3 then 0 else x end as varchar), ',') within group (order by k) from (select k, max(10 / (k - 3)) + count(*) as x from src group by k) d")]
    public void ReadColumn_IsEvaluatedWhereItsReaderUsesIt(string expected, string query) => AreEqual(expected, Seeded().ExecuteScalar(query));

    /// <summary>
    /// A filter on a body's plain column reaches beneath a body that computes
    /// its columns itself — a <c>DISTINCT</c>, each branch of a set operation —
    /// and beneath one whose failing column reads what the body doesn't
    /// project, <c>&lt;&gt;</c> included, so the row it turns away never
    /// computes its columns (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select distinct k, v, 10 / (k - 3) as x from src2) d where k <> 3")]
    [DataRow("-5,-10,10,1", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src union all select 5, 1) d where k <> 3")]
    [DataRow("-5,-10,10,1", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src2 union select 5, 1) d where k <> 3")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src2 intersect select k, 10 / (k - 3) from src2 where k <> 3) d where k <> 3")]
    [DataRow("3", "select cast(count(*) as varchar) from (select k, 10 / (k - 3) as x from src2 except select k, 0 from src2 where k = 1) d where k <> 3")]
    [DataRow("1,3,4", "select string_agg(cast(c as varchar), ',') within group (order by c) from (select k, cast(s as int) as c from src) d where k <> 2")]
    [DataRow("3", "select cast(count(*) as varchar) from v1 where x > -100 and k <> 3")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select distinct k, v, 10 / (k - 3) as x from src2) d where k % 4 <> 3")]
    [DataRow("-5,-10,10,1", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src2 union select 5, 1) d where k + 0 <> 3")]
    [DataRow("-5,-10,10,1", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select k, 10 / (k - 3) as x from src2 union all select 5, 1) d where -k <> -3")]
    [DataRow("-5,-10,10", "select string_agg(cast(x as varchar), ',') within group (order by k) from (select k, sum(10 / (k - 3)) as x from src2 group by k) d where k * 2 <> 6")]
    [DataRow("1,3,4", "select string_agg(cast(c as varchar), ',') within group (order by c) from (select distinct k, cast(s as int) as c from src) d where cast(k as varchar) <> '2'")]
    public void Filter_ReachesBeneathTheBody(string expected, string query) => AreEqual(expected, Seeded().ExecuteScalar(query));

    /// <summary>
    /// What the body reads of a column itself, or a column only it can
    /// compute, is computed for every body row, and the guard above it comes
    /// too late (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select case when k = 3 then 0 else x end from (select distinct v, k, 10 / (k - 3) as x from src2) d")]
    [DataRow("select case when k = 3 then 0 else x end from (select k, 10 / (k - 3) as x from src2 union select 5, 1) d")]
    [DataRow("select case when k = 3 then 0 else x end from (select k, sum(10 / (k - 3)) as x from src2 group by k) d")]
    [DataRow("select case when k = 3 then 0 else x end from (select k, x from (select k, 10 / (k - 3) as x from src) i where i.x > -100) d")]
    [DataRow("select x from (select top 10 k, 10 / (k - 3) as x from src order by x) d where k <> 3")]
    [DataRow("select d.k from (select k, 10 / (k - 3) as x from src) d join src s on s.k = d.x")]
    public void ReadColumn_TheBodyNeedsItself_RaisesUnderAGuard(string query) => _ = Seeded().AssertSqlError(query, 8134);

    /// <summary>
    /// Every reader of a deferred column reads it as the body would have
    /// computed it: a hash join's key, a grouping, a window's order, a cursor's
    /// fetch, a joined write's source and a correlated subquery.
    /// </summary>
    [TestMethod]
    public void DeferredColumn_ReadsAsTheBodyComputesIt()
    {
        var simulation = Seeded();
        AreEqual("1:2,2:4", simulation.ExecuteScalar("select string_agg(concat(d.k, ':', s.k), ',') within group (order by d.k) from (select k, k * 2 as x from src) d join src s on s.k = d.x"));
        AreEqual("0:2,1:2", simulation.ExecuteScalar("select string_agg(concat(x, ':', n), ',') within group (order by x) from (select x, count(*) as n from (select k, k % 2 as x from src) d group by x) g"));
        AreEqual("100,90,70,40", simulation.ExecuteScalar("select string_agg(cast(t as varchar), ',') within group (order by k) from (select k, sum(x) over (order by x desc) as t from (select k, k * 10 as x from src) d) w"));
        AreEqual("11,22,33,44", simulation.ExecuteScalar("select string_agg(cast(a.y as varchar), ',') within group (order by s.k) from src s cross apply (select d.x + s.k as y from (select k, k * 10 as x from src) d where d.k = s.k) a"));
        AreEqual("3,5,7,9", simulation.ExecuteScalar("select string_agg(cast(b.z as varchar), ',') within group (order by s.k) from src s cross apply (select s.k * 2 as y) a cross apply (select a.y + 1 as z) b"));
        AreEqual(-10, simulation.ExecuteBatchesScalar(
            """
            declare @x int;
            declare c cursor keyset for select x from (select k, 10 / (k - 3) as x from src) d where k <> 3 order by k;
            open c;
            fetch next from c into @x;
            fetch next from c into @x;
            close c;
            deallocate c;
            select @x;
            """));
        AreEqual("-5,-10,0,10", simulation.ExecuteBatchesScalar(
            """
            declare @u table (k int, v int);
            insert @u select k, 0 from src;
            update u set v = d.x from @u u join (select k, 10 / (k - 3) as x from src) d on d.k = u.k where u.k <> 3;
            select string_agg(cast(v as varchar), ',') within group (order by k) from @u;
            """));
    }

    /// <summary>
    /// A view's conversion error in a column the query never reads doesn't
    /// surface, where the column it does read still raises its own.
    /// </summary>
    [TestMethod]
    public void ViewColumn_ReadOnlyWhereNamed()
    {
        var simulation = Seeded();
        AreEqual("1,2,3,4", simulation.ExecuteScalar("select string_agg(cast(k as varchar), ',') within group (order by k) from v1"));
        _ = simulation.AssertSqlError("select c from v1", 245);
    }

    /// <summary>
    /// A seeded <c>RAND</c> in a column nobody reads never runs, so it never
    /// reseeds the session's generator (probed 2026-10-17 against SQL Server
    /// 2025: the next <c>RAND()</c> continues the unseeded sequence).
    /// </summary>
    [TestMethod]
    public void UnreadSeededRand_LeavesTheGeneratorAlone() => AreEqual(3, Seeded().ExecuteScalar("""
        declare @seeded float, @read float, @unread float, @n int;
        select @seeded = rand(5);
        select @seeded = rand();
        select @read = max(r) from (select k, rand(5) as r from src) d;
        select @read = rand();
        select @n = count(*) from (select k, rand(5) as r from src) d;
        select @unread = rand();
        select iif(@read = @seeded, 1, 0) + iif(@unread <> @seeded, 2, 0)
        """));

    [TestMethod]
    public void MergeSource_ComputesOnlyTheColumnsItsClausesRead()
    {
        var simulation = Seeded();
        AreEqual(4, simulation.ExecuteBatchesScalar(
            """
            declare @t table (k int primary key);
            merge @t t using (select k, 10 / (k - 3) as x from src) s on t.k = s.k
            when not matched then insert (k) values (s.k);
            select count(*) from @t;
            """));
        _ = simulation.AssertSqlError(
            """
            declare @t table (k int primary key);
            merge @t t using (select k, 10 / (k - 3) as x from src) s on t.k = s.k
            when not matched and s.x > -100 then insert (k) values (s.k);
            """,
            8134);
        _ = simulation.AssertSqlError(
            """
            declare @t table (k int primary key);
            merge @t t using (select k, 10 / (k - 3) as x from src) s on t.k = s.k
            when not matched then insert (k) values (s.k) output s.x;
            """,
            8134);
    }

    /// <summary>
    /// Every kind of <c>USING</c> source leaves the columns no clause reads
    /// uncomputed — a query, a view, a CTE, a <c>VALUES</c> list, a table's
    /// computed column, a joined source — and a column only a <c>WHEN</c>
    /// clause reads is evaluated where the clause reads it, so a guard the
    /// failing row never passes keeps it from raising (probed 2026-10-17
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("-5,-10,0,10", "merge @t t using (select k, 10 / (k - 3) as x from src) s on t.k = s.k when matched then update set j = case when s.k = 3 then 0 else s.x end;")]
    [DataRow("-5,-10,0,10", "merge @t t using (select k, 10 / (k - 3) as x from src) s on t.k = s.k when matched and s.k <> 3 then update set j = s.x;")]
    [DataRow("-5,-10,0,10", "merge @t t using v1 s on t.k = s.k when matched and s.k <> 3 then update set j = s.x;")]
    [DataRow("1,2,3,4", "merge @t t using v1 s on t.k = s.k when matched then update set j = s.k;")]
    [DataRow("-5,-10,0,10", "with c as (select k, 10 / (k - 3) as x from src) merge @t t using c on t.k = c.k when matched and c.k <> 3 then update set j = c.x;")]
    [DataRow("1,0,3,0", "merge @t t using (values (1, 10 / 1), (3, 10 / 0)) s(k, x) on t.k = s.k when matched then update set j = s.k;")]
    [DataRow("1,2,3,4", "merge @t t using tc on t.k = tc.k when matched then update set j = tc.k;")]
    [DataRow("-5,-10,0,10", "merge @t t using tc on t.k = tc.k and tc.k <> 3 when matched then update set j = tc.c;")]
    [DataRow("1,2,3,4", "merge @t t using src z cross apply (values (z.k, 10 / (z.k - 3))) s(kk, x) on t.k = s.kk when matched then update set j = s.kk;")]
    [DataRow("-5,-10,0,10", "merge @t t using src z cross apply (select z.k as kk, 10 / (z.k - 3) as x) s on t.k = s.kk when matched and s.kk <> 3 then update set j = s.x;")]
    public void MergeSource_EvaluatesWhatOnlyAWhenClauseReadsThere(string expected, string merge)
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("create table tc (k int, c as 10 / (k - 3))");
        _ = simulation.ExecuteNonQuery("insert tc (k) values (1), (2), (3), (4)");
        AreEqual(expected, simulation.ExecuteBatchesScalar($"""
            declare @t table (k int primary key, j int);
            insert @t values (1, 0), (2, 0), (3, 0), (4, 0);
            {merge}
            select string_agg(cast(j as varchar), ',') within group (order by k) from @t;
            """));
        _ = simulation.AssertSqlError(
            """
            declare @t table (k int primary key, j int);
            insert @t values (1, 0), (2, 0), (3, 0), (4, 0);
            merge @t t using tc on t.k = tc.k when matched then update set j = tc.c;
            """,
            8134);
    }

    /// <summary>
    /// A joined write's target read through a view, CTE or derived table
    /// computes only the columns the statement reads of it, and evaluates a
    /// computed one where the statement reads it (probed 2026-10-17 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("5,5,5,5", "update vm set j = 5 from vm join src s on vm.k = s.k")]
    [DataRow("9,9,0,9", "update vm set j = 9 from vm join src s on vm.k = s.k where vm.k <> 3 and vm.x > -100")]
    [DataRow("7,7,7,7", "update d set j = 7 from (select k, j, 10 / (k - 3) as x from tm) d join src s on d.k = s.k")]
    [DataRow("8,8,8,8", "with c as (select k, j, 10 / (k - 3) as x from tm) update c set j = 8 from c join src s on c.k = s.k")]
    [DataRow("0,0,0", "delete vm from vm join src s on vm.k = s.k where s.k = 1")]
    public void JoinedWriteThroughABody_ComputesOnlyWhatItReads(string expected, string write)
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create table tm (k int primary key, j int); insert tm values (1, 0), (2, 0), (3, 0), (4, 0);",
            "create view vm as select k, j, 10 / (k - 3) as x from tm");
        _ = simulation.ExecuteNonQuery(write);
        AreEqual(expected, simulation.ExecuteScalar("select string_agg(cast(j as varchar), ',') within group (order by k) from tm"));
        _ = simulation.AssertSqlError("update vm set j = vm.x from vm join src s on vm.k = s.k", 8134);
    }

    [TestMethod]
    public void JoinedUpdate_ComputesOnlyTheColumnsItReads()
    {
        var simulation = Seeded();
        AreEqual(10, simulation.ExecuteBatchesScalar(
            """
            declare @u table (k int, v int);
            insert @u select k, 0 from src;
            update u set v = d.k from @u u join (select k, 10 / (k - 3) as x from src) d on d.k = u.k;
            select sum(v) from @u;
            """));
        _ = simulation.AssertSqlError(
            """
            declare @u table (k int, v int);
            insert @u select k, 0 from src;
            update u set v = d.x from @u u join (select k, 10 / (k - 3) as x from src) d on d.k = u.k;
            """,
            8134);
    }

    /// <summary>
    /// A write through a join view computes only the view's columns it reads:
    /// a <c>MERGE</c> into one never computes a column no clause reads, and a
    /// joined <c>UPDATE</c> naming one in its <c>FROM</c> clause evaluates the
    /// column where it reads it (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void JoinViewWrite_ComputesOnlyWhatItReads()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table a (k int primary key, j int);
            create table b (k int primary key, bk int);
            insert a values (1, 0), (2, 0), (3, 0), (4, 0);
            insert b values (1, 1), (2, 2), (3, 3), (4, 4);
            """,
            "create view jv as select a.k, a.j, b.bk, 10 / (a.k - 3) as x from a join b on a.k = b.k");
        AreEqual(1, simulation.ExecuteScalar("merge jv using (select 1 as k) s on jv.k = s.k when matched then update set j = 1; select j from a where k = 1"));
        AreEqual(4, simulation.ExecuteScalar("merge jv using (select k from a) s on jv.k = s.k when matched then update set j = 1; select sum(j) from a"));
        AreEqual(27, simulation.ExecuteScalar("update jv set j = 9 from jv join a s on jv.k = s.k where jv.k <> 3 and jv.x > -100; update a set j = 0 where k = 3; select sum(j) from a"));
        _ = simulation.AssertSqlError("update jv set j = jv.x from jv join a s on jv.k = s.k", 8134);
    }

    [TestMethod]
    public void CursorOverDerivedTable_FetchesWithoutTheUnreadColumn()
    {
        var simulation = Seeded();
        AreEqual(1, simulation.ExecuteBatchesScalar(
            """
            declare @k int;
            declare c cursor for select k from (select k, 10 / (k - 3) as x from src) d order by k;
            open c;
            fetch next from c into @k;
            close c;
            deallocate c;
            select @k;
            """));
    }

    [TestMethod]
    public void InsertAndSelectInto_ReadOnlyTheirColumns()
    {
        var simulation = Seeded();
        AreEqual(4, simulation.ExecuteBatchesScalar(
            """
            select k into #t from (select k, 10 / (k - 3) as x from src) d;
            insert #t select k from v1;
            select count(*) / 2 from #t;
            """));
    }

    /// <summary>
    /// Under DISTINCT an ORDER BY term naming the source column behind a
    /// projected one reads that projected column, so a reader naming only the
    /// others leaves it computed and the TOP keeps the rows it orders first
    /// (probed 2026-10-26 against SQL Server 2025: Django's sliced distinct
    /// count).
    /// </summary>
    [TestMethod]
    public void DistinctTop_OrderedByAProjectedSourceColumn_KeepsIt()
        => AreEqual("1,2,4", new Simulation().ExecuteScalar("""
            create table c (id int primary key, name nvarchar(10));
            create table t (id int primary key, name nvarchar(10), cid int);
            insert c values (1, N'b'), (2, N'a'), (3, N'c');
            insert t values (1, N't1', 1), (2, N't2', 2), (3, N't3', 3), (4, N't4', 2), (5, N't5', 1);
            select string_agg(cast(col1 as varchar(9)), ',') within group (order by col1)
            from (select d.col1 from (select distinct top 3 t.id as col1, t.name as col2, c.name as col5 from t join c on c.id = t.cid order by c.name, t.id) d) q
            """));
}
