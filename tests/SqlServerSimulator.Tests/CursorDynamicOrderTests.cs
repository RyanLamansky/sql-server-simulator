using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A DYNAMIC cursor over one table follows the table's live rows in its order
/// across the writes made while it is open — a key moved later, a row deleted
/// ahead, rows inserted, a value changing what its WHERE passes — and a
/// positioned update loop over it runs in linear time (probed 2026-10-25
/// against SQL Server 2025).
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
