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
}
