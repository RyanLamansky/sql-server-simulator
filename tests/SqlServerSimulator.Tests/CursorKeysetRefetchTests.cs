using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A KEYSET fetch reads its member's base rows by their identity and the
/// select list over them, judging again none of the predicates that chose the
/// member — the WHERE, a join's ON, a derived table's, a CTE's or an APPLY
/// body's — but a view body's, and leaves NULL the side an outer join
/// NULL-extended, whatever it would join now; a member whose row is gone, or
/// whose view no longer returns it, is a hole (probed 2026-10-26 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class CursorKeysetRefetchTests
{
    [TestMethod]
    [DataRow("select id, p_id, amt from o where amt > 15", "update o set amt = 5 where id = 3", null, "2/2/20 3/3/5 4/4/40")]
    [DataRow("select p.id, o.id, o.amt from p join o on o.p_id = p.id", "update o set p_id = 1 where id = 2; update o set p_id = 99 where id = 3", null, "1/1/10 2/2/20 3/3/30 4/4/40")]
    [DataRow("select p.id, o.id, o.amt from p join o on o.p_id = p.id where o.amt > 15", "update o set amt = 1 where id = 3", null, "2/2/20 3/3/1 4/4/40")]
    [DataRow("select x.id, x.oid, x.amt from (select p.id, o.id as oid, o.amt from p join o on o.p_id = p.id where o.amt > 15) x", "update o set amt = 1 where id = 3", null, "2/2/20 3/3/1 4/4/40")]
    [DataRow("with x as (select p.id, o.id as oid, o.amt from p join o on o.p_id = p.id where o.amt > 15) select id, oid, amt from x", "update o set amt = 1 where id = 3", null, "2/2/20 3/3/1 4/4/40")]
    [DataRow("select p.id, x.id, x.amt from p cross apply (select o.id, o.amt from o where o.p_id = p.id) x", "update o set p_id = 1 where id = 3", null, "1/1/10 2/2/20 3/3/30 4/4/40")]
    [DataRow("select p.id, o.id, o.amt from p left join o on o.p_id = p.id", "insert o values (9, 5, 90); update o set p_id = 1 where id = 3", null, "1/1/10 2/2/20 3/3/30 4/4/40 5/-/-")]
    [DataRow("select p.id, x.id, x.amt from p outer apply (select o.id, o.amt from o where o.p_id = p.id) x", "insert o values (9, 5, 90)", null, "1/1/10 2/2/20 3/3/30 4/4/40 5/-/-")]
    [DataRow("select p.id, o.id, o.amt from p join o on o.p_id = p.id", "delete o where id = 3; delete o where id = 2; insert o values (2, 2, 222); update o set id = 40 where id = 4", null, "1/1/10 2/2/222 gone gone")]
    [DataRow("select id, oid, amt from v", "update o set amt = 1 where id = 3; update o set p_id = 1 where id = 4", "create view v as select p.id, o.id as oid, o.amt from p join o on o.p_id = p.id where o.amt > 15", "2/2/20 gone gone")]
    [DataRow("select p.id, v.id, v.amt from p join v on v.p_id = p.id", "update o set p_id = 1 where id = 3; update o set amt = 1 where id = 2", "create view v as select id, p_id, amt from o where amt > 15", "gone 3/3/30 4/4/40")]
    public void Refetch_ReadsMembersByIdentity(string query, string writes, string? view, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table p (id int primary key, name varchar(10)); insert p select value, concat('p', value) from generate_series(1, 5);
            create table o (id int primary key, p_id int, amt int); insert o select value, value, value * 10 from generate_series(1, 4);
            """);
        if (view is not null)
            _ = simulation.ExecuteNonQuery(view);
        AreEqual(expected, simulation.ExecuteScalar($"""
            declare @a int, @b int, @m int, @seq varchar(200) = '';
            declare c cursor local keyset for {query};
            open c;
            {writes};
            fetch first from c into @a, @b, @m;
            while @@fetch_status <> -1
            begin
                set @seq += case @@fetch_status when 0 then concat(@a, '/', isnull(cast(@b as varchar(10)), '-'), '/', isnull(cast(@m as varchar(10)), '-')) else 'gone' end + ' ';
                fetch next from c into @a, @b, @m;
            end;
            close c; deallocate c;
            select rtrim(@seq)
            """));
    }

    /// <summary>
    /// Each fetch reads its member's rows where they stand, where each joined
    /// every pair of rows to find one.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    public void JoinFetchLoop_RunsInLinearTime() =>
        AreEqual(767862, new Simulation().ExecuteScalar("""
            create table p (id int primary key, name varchar(20)); insert p select value, concat('p', value) from generate_series(1, 16000);
            create table o (id int primary key, p_id int, amt int); insert o select value, value, value % 97 from generate_series(1, 16000);
            declare @n varchar(20), @a int, @s int = 0;
            declare c cursor local scroll for select p.name, o.amt from p join o on o.p_id = p.id;
            open c; fetch c into @n, @a;
            while @@fetch_status = 0 begin set @s += @a; fetch c into @n, @a; end;
            close c; deallocate c;
            select @s
            """));
}
