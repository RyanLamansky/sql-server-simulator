using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A DYNAMIC cursor follows its live rows in its order across the writes made
/// while it is open — a key moved later, a row deleted ahead, rows inserted, a
/// value changing what its WHERE passes — and a fetch or positioned update
/// loop over it runs in linear time (probed 2026-10-25 against SQL Server
/// 2025).
/// </summary>
[TestClass]
public sealed class CursorDynamicOrderTests
{
    [TestMethod]
    [DataRow("id int primary key", "select id from k", "update k set id = 10 where id = 2", "1,3,4,5,10")]
    [DataRow("id int primary key", "select id from k", "delete k where id = 3; insert k values (6, 6), (0, 0)", "1,2,4,5,6")]
    [DataRow("id int primary key", "select id from k where v > 0", "update k set v = 0 where id = 4; update k set v = 9 where id = 2", "1,2,3,5")]
    [DataRow("id int", "select id from k", "update k set id = id * 10 where id >= 3; delete k where id = 2", "1,30,40,50")]
    public void WritesWhileOpen_AreFollowed(string key, string query, string writes, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table k ({key}, v int); insert k values (1, 1), (2, 2), (3, 3), (4, 4), (5, 5);
            declare @x int, @seq varchar(100);
            declare c cursor local dynamic for {query};
            open c; fetch c into @x; set @seq = cast(@x as varchar(10));
            {writes};
            while 1 = 1 begin fetch c into @x; if @@fetch_status <> 0 break; set @seq += ',' + cast(@x as varchar(10)); end;
            close c; deallocate c;
            select @seq
            """));

    /// <summary>
    /// A cursor over a join follows its joined rows the same way: a row
    /// joining another once its join column changes, an outer join's row
    /// losing its match, a derived table's WHERE passing a row it didn't.
    /// </summary>
    [TestMethod]
    [DataRow("select p.id, o.id, o.amt from p join o on o.p_id = p.id", "delete o where id = 3; update o set p_id = 5 where id = 4; insert o values (7, 6, 70); update o set amt = 99 where id = 5", "1/1/10 2/2/20 5/4/40 5/5/99 6/6/60 6/7/70")]
    [DataRow("select p.id, o.id, o.amt from p left join o on o.p_id = p.id", "delete o where id = 4; update o set amt = amt + 1 where id = 5; delete p where id = 3", "1/1/10 2/2/20 4/-/- 5/5/51 6/6/60")]
    [DataRow("select q.id, q.oid, q.amt from (select p.id, o.id as oid, o.amt from p join o on o.p_id = p.id where o.amt > 15) q", "update o set amt = 5 where id = 4; update o set amt = 77 where id = 5", "2/2/20 3/3/30 5/5/77 6/6/60")]
    public void JoinWritesWhileOpen_AreFollowed(string query, string writes, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table p (id int primary key, name varchar(10)); insert p select value, concat('p', value) from generate_series(1, 6);
            create table o (id int primary key, p_id int, amt int); insert o select value, value, value * 10 from generate_series(1, 6);
            declare @a int, @b int, @m int, @seq varchar(200);
            declare c cursor local for {query};
            open c; fetch c into @a, @b, @m;
            set @seq = concat(@a, '/', isnull(cast(@b as varchar(10)), '-'), '/', isnull(cast(@m as varchar(10)), '-'));
            {writes};
            while 1 = 1
            begin
                fetch c into @a, @b, @m;
                if @@fetch_status <> 0 break;
                set @seq += concat(' ', @a, '/', isnull(cast(@b as varchar(10)), '-'), '/', isnull(cast(@m as varchar(10)), '-'));
            end;
            close c; deallocate c;
            select @seq
            """));

    /// <summary>
    /// A fetch over a join reads its row at its addresses from the joined
    /// rows' order, where each joined every pair of rows to find one.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    public void JoinFetchLoop_RunsInLinearTime() =>
        AreEqual(767862, new Simulation().ExecuteScalar("""
            create table p (id int primary key, name varchar(20)); insert p select value, concat('p', value) from generate_series(1, 16000);
            create table o (id int primary key, p_id int, amt int); insert o select value, value, value % 97 from generate_series(1, 16000);
            declare @n varchar(20), @a int, @s int = 0;
            declare c cursor local for select p.name, o.amt from p join o on o.p_id = p.id;
            open c; fetch c into @n, @a;
            while @@fetch_status = 0 begin set @s += @a; fetch c into @n, @a; end;
            close c; deallocate c;
            select @s
            """));

    /// <summary>
    /// An update leaving a join's columns alone keeps the joined rows' order,
    /// so a positioned update loop over a join reads it once.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    public void JoinPositionedUpdateLoop_RunsInLinearTime() =>
        AreEqual(783862, new Simulation().ExecuteScalar("""
            create table p (id int primary key, name varchar(20)); insert p select value, concat('p', value) from generate_series(1, 16000);
            create table o (id int primary key, p_id int, amt int); insert o select value, value, value % 97 from generate_series(1, 16000);
            declare @n varchar(20), @a int;
            declare c cursor local for select p.name, o.amt from p join o on o.p_id = p.id;
            open c; fetch c into @n, @a;
            while @@fetch_status = 0 begin update o set amt = amt + 1 where current of c; fetch c into @n, @a; end;
            close c; deallocate c;
            select sum(amt) from o
            """));

    /// <summary>
    /// Each positioned update reads its cursor's row where it stands, and the
    /// fetch after it keeps the order the update left in place, where each
    /// walked every row.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    [DataRow("id int primary key")]
    [DataRow("id int")]
    public void PositionedUpdateLoop_RunsInLinearTime(string key) =>
        AreEqual(783862, new Simulation().ExecuteScalar($"""
            create table t ({key}, x int); insert t select value, value % 97 from generate_series(1, 16000);
            declare @x int;
            declare c cursor local for select x from t;
            open c; fetch c into @x;
            while @@fetch_status = 0 begin update t set x = x + 1 where current of c; fetch c into @x; end;
            close c; deallocate c;
            select sum(x) from t
            """));
}
