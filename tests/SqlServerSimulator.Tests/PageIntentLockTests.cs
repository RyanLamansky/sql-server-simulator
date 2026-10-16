using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The intent lock real holds on the page each row or key lock's row is on —
/// IS for an S, IU for a U, IX for an X, in a range mode too — on the
/// nonclustered index's own leaf page for an index key, and on the pages a
/// row-locking read reached without keeping a row there; and how a page lock
/// meets another session's row locks through them. Ten rows of <c>int</c>
/// and <c>char(2000)</c> take three pages, four to a page as real fills them
/// loaded in key order, numbered here from <c>1:8</c> — base pages first,
/// then the index's (probed 2026-10-16 against SQL Server 2025, every
/// expectation, with the pages compared by their order in each rowset).
/// </summary>
[TestClass]
public sealed class PageIntentLockTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Tables = """
        create table c (id int primary key, v int, pad char(2000) not null default '');
        create table h (id int, v int, pad char(2000) not null default '');
        create table n (id int primary key, v int, pad char(2000) not null default '');
        create index ix_v on n (v);
        insert c (id, v) select value, value from generate_series(1, 10);
        insert h (id, v) select value, value from generate_series(1, 10);
        insert n (id, v) select value, value * 10 from generate_series(1, 10);
        """;

    /// <summary>
    /// What <paramref name="holder"/> leaves its session holding: the row, key
    /// and object locks by mode and count, and each page lock by its index id,
    /// description and mode.
    /// </summary>
    private static string Locks(string holder)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables);
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand(holder).ExecuteNonQuery();
        return Held(connection, "@@spid");
    }

    private static string Held(DbConnection connection, string spid) => (string?)connection.CreateCommand($"""
        select string_agg(item, ', ') within group (order by item)
        from (
            select concat(resource_type, ' ', request_mode, ' x', count(*)) item
            from sys.dm_tran_locks where request_session_id = {spid} and resource_type in ('KEY', 'OBJECT', 'RID')
            group by resource_type, request_mode
            union all
            select concat('PAGE ', p.index_id, ' ', rtrim(l.resource_description), ' ', l.request_mode, ' ', l.request_status)
            from sys.dm_tran_locks l join sys.partitions p on p.hobt_id = l.resource_associated_entity_id
            where l.request_session_id = {spid} and l.resource_type = 'PAGE') g
        """).ExecuteScalar() ?? "";

    /// <summary>
    /// A write holds the intent of each page it writes a row or index key of —
    /// a delete's of the page its row stood on — and an insert past a full
    /// page of a clustered index that of the full page too, which it tried
    /// first; a heap's insert goes to a page with room.
    /// </summary>
    [TestMethod]
    [DataRow("begin tran; update c set v = 0 where id = 5", "KEY X x1, OBJECT IX x1, PAGE 1 1:9 IX GRANT")]
    [DataRow("begin tran; update h set v = 0 where id = 5", "OBJECT IX x1, PAGE 0 1:9 IX GRANT, RID X x1")]
    [DataRow("begin tran; update n set v = 55 where id = 5", "KEY X x3, OBJECT IX x1, PAGE 1 1:9 IX GRANT, PAGE 2 1:11 IX GRANT")]
    [DataRow("begin tran; update n set pad = 'y' where id = 5", "KEY X x1, OBJECT IX x1, PAGE 1 1:9 IX GRANT")]
    [DataRow("begin tran; delete c where id between 4 and 6", "KEY X x3, OBJECT IX x1, PAGE 1 1:8 IX GRANT, PAGE 1 1:9 IX GRANT")]
    [DataRow("begin tran; delete h where id between 4 and 6", "OBJECT IX x1, PAGE 0 1:8 IX GRANT, PAGE 0 1:9 IX GRANT, RID X x3")]
    [DataRow("begin tran; insert c (id, v) select value, value from generate_series(11, 20)", "KEY X x10, OBJECT IX x1, PAGE 1 1:10 IX GRANT, PAGE 1 1:11 IX GRANT, PAGE 1 1:12 IX GRANT")]
    [DataRow("begin tran; insert n (id, v) select value, value from generate_series(11, 20)", "KEY X x20, OBJECT IX x1, PAGE 1 1:10 IX GRANT, PAGE 1 1:11 IX GRANT, PAGE 1 1:12 IX GRANT, PAGE 2 1:13 IX GRANT")]
    [DataRow("begin tran; update c set pad = 'x' where v = 5", "KEY X x1, OBJECT IX x1, PAGE 1 1:9 IX GRANT")]
    [DataRow("insert c (id, v) values (11, 11), (12, 12); begin tran; insert c (id, v) values (13, 13)", "KEY X x1, OBJECT IX x1, PAGE 1 1:10 IX GRANT, PAGE 1 1:11 IX GRANT")]
    [DataRow("insert h (id, v) values (11, 11), (12, 12); begin tran; insert h (id, v) values (13, 13)", "OBJECT IX x1, PAGE 0 1:11 IX GRANT, RID X x1")]
    public void Write_HoldsTheIntentOfEachPageItWrites(string holder, string expected) => AreEqual(expected, Locks(holder));

    /// <summary>
    /// A row-locking read holds the intent on every page it read: those of the
    /// rows it keeps, of the rows its predicate rejected, the one its seek
    /// landed on — for <c>id &gt; 8</c> the page holding key 8 — and the one
    /// holding the key past its range, which it read to find its end.
    /// </summary>
    [TestMethod]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where id between 3 and 6", "KEY S x4, OBJECT IS x1, PAGE 1 1:8 IS GRANT, PAGE 1 1:9 IS GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where id between 5 and 8", "KEY S x4, OBJECT IS x1, PAGE 1 1:10 IS GRANT, PAGE 1 1:9 IS GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where id > 8", "KEY S x2, OBJECT IS x1, PAGE 1 1:10 IS GRANT, PAGE 1 1:9 IS GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where id >= 9", "KEY S x2, OBJECT IS x1, PAGE 1 1:10 IS GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where v = 5", "KEY S x1, OBJECT IS x1, PAGE 1 1:10 IS GRANT, PAGE 1 1:8 IS GRANT, PAGE 1 1:9 IS GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select top (2) id from c where v > 0", "KEY S x2, OBJECT IS x1, PAGE 1 1:8 IS GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from h where v = 5", "OBJECT IS x1, PAGE 0 1:10 IS GRANT, PAGE 0 1:8 IS GRANT, PAGE 0 1:9 IS GRANT, RID S x1")]
    [DataRow("begin tran; select count(*) from c with (updlock) where v = 5", "KEY U x1, OBJECT IX x1, PAGE 1 1:10 IU GRANT, PAGE 1 1:8 IU GRANT, PAGE 1 1:9 IU GRANT")]
    [DataRow("begin tran; select count(*) from c with (xlock) where id between 3 and 6", "KEY X x4, OBJECT IX x1, PAGE 1 1:8 IX GRANT, PAGE 1 1:9 IX GRANT")]
    [DataRow("set transaction isolation level serializable; begin tran; select count(*) from c where id > 8", "KEY RangeS-S x3, OBJECT IS x1, PAGE 1 1:10 IS GRANT, PAGE 1 1:9 IS GRANT")]
    [DataRow("set transaction isolation level serializable; begin tran; select count(*) from c where id = 11", "KEY RangeS-S x1, OBJECT IS x1, PAGE 1 1:10 IS GRANT")]
    [DataRow("set transaction isolation level serializable; begin tran; select count(*) from c where v = 5", "KEY RangeS-S x11, OBJECT IS x1, PAGE 1 1:10 IS GRANT, PAGE 1 1:8 IS GRANT, PAGE 1 1:9 IS GRANT")]
    [DataRow("set transaction isolation level serializable; begin tran; select count(*) from h", "OBJECT S x1")]
    public void Read_HoldsTheIntentOfEachPageItRead(string holder, string expected) => AreEqual(expected, Locks(holder));

    /// <summary>An empty table has no page, so a read fencing its infinity key holds no page lock.</summary>
    [TestMethod]
    public void EmptyTable_HoldsNoPage()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table e (k int primary key)");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from e where k = 5").ExecuteNonQuery();
        AreEqual("KEY RangeS-S x1, OBJECT IS x1", Held(connection, "@@spid"));
    }

    /// <summary>
    /// A session's page lock and the intents of its row locks under it are one
    /// lock, in the mode real converts them to: an S beside an IX is SIX,
    /// beside an IU SIU, a U beside an IX UIX, an IU and an IX IX.
    /// </summary>
    [TestMethod]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c with (paglock) where id between 1 and 4; update c set v = 0 where id = 2", "KEY X x1, OBJECT IX x1, PAGE 1 1:8 SIX GRANT, PAGE 1 1:9 S GRANT")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c with (paglock) where id between 1 and 4; select count(*) from c with (updlock) where id = 3", "KEY U x1, OBJECT IX x1, PAGE 1 1:8 SIU GRANT, PAGE 1 1:9 S GRANT")]
    [DataRow("begin tran; select count(*) from c with (paglock, updlock) where id between 1 and 4; update c set v = 0 where id = 2", "KEY X x1, OBJECT IX x1, PAGE 1 1:8 UIX GRANT, PAGE 1 1:9 U GRANT")]
    [DataRow("begin tran; select count(*) from c with (updlock) where id = 1; update c set v = 0 where id = 2", "KEY U x1, KEY X x1, OBJECT IX x1, PAGE 1 1:8 IX GRANT")]
    public void PageLockAndRowIntents_ConvertToOneLock(string holder, string expected) => AreEqual(expected, Locks(holder));

    /// <summary>
    /// A page lock waits on another session's row locks on its page as on
    /// their intents, in the page's mode — an S passes an S or U row lock, a U
    /// an S, an X none — and on the intent a row-locking read kept on a page
    /// whose rows it let go, and times out as a page lock does, state 52, or
    /// 45 on a heap; a row lock waits on another session's page lock through
    /// its own intent.
    /// </summary>
    [TestMethod]
    [DataRow("begin tran; update c set v = 0 where id = 5", "set transaction isolation level repeatable read; select count(*) from c with (paglock) where id = 6", 52)]
    [DataRow("begin tran; update c set v = 0 where id = 5", "update c with (paglock) set v = 0 where id = 6", 52)]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where id = 5", "update c with (paglock) set v = 0 where id = 6", 52)]
    [DataRow("begin tran; select count(*) from c with (updlock) where id = 5", "select count(*) from c with (paglock, updlock) where id = 6", 52)]
    [DataRow("begin tran; select count(*) from c with (updlock) where id = 5", "set transaction isolation level repeatable read; select count(*) from c with (paglock) where id = 6", 0)]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c with (paglock) where id = 6", "update c set v = 0 where id = 5", 52)]
    [DataRow("begin tran; update h set v = 0 where id = 5", "set transaction isolation level repeatable read; select count(*) from h with (paglock) where id = 6", 45)]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where v = 5", "update c with (paglock) set v = 0 where id = 1", 52)]
    [DataRow("set transaction isolation level repeatable read; begin tran; select count(*) from c where v = 5", "set transaction isolation level repeatable read; select count(*) from c with (paglock) where id = 1", 0)]
    [DataRow("begin tran; select count(*) from c with (updlock) where v = 5", "select count(*) from c with (paglock, updlock) where id = 1", 52)]
    public void PageLock_MeetsTheRowLocksUnderIt(string holder, string waiter, int state)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables);
        using var holding = sim.CreateOpenConnection();
        using var waiting = sim.CreateOpenConnection();
        _ = holding.CreateCommand(holder).ExecuteNonQuery();
        try
        {
            _ = waiting.CreateCommand($"set lock_timeout 0; begin tran; {waiter}").ExecuteNonQuery();
            AreEqual(0, state);
        }
        catch (SimulatedSqlException timeout)
        {
            AreEqual(1222, timeout.Number);
            AreEqual(state, timeout.State);
        }
    }

    /// <summary>
    /// A waiting page lock is listed waiting on the page, the wait named on it
    /// with the intent it meets; a row lock's wait holds its page's intent,
    /// granted.
    /// </summary>
    [TestMethod]
    public async Task Waits_ListTheirPages()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables);
        using var holding = sim.CreateOpenConnection();
        using var waiting = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        _ = holding.CreateCommand("begin tran; update c set v = 0 where id = 5").ExecuteNonQuery();
        var spid = waiting.CreateCommand("select @@spid").ExecuteScalar()!.ToInvariantString();

        var read = await sim.StartBlocked(waiting, "select count(*) from c where id = 5", TestContext.CancellationToken);
        AreEqual("KEY S x1, OBJECT IS x1, PAGE 1 1:9 IS GRANT", Held(observer, spid));
        _ = holding.CreateCommand("rollback").ExecuteNonQuery();
        _ = await read;

        _ = holding.CreateCommand("begin tran; update c set v = 0 where id = 5").ExecuteNonQuery();
        var paged = await sim.StartBlocked(waiting, "set transaction isolation level repeatable read; begin tran; select count(*) from c with (paglock) where id = 6; rollback", TestContext.CancellationToken);
        AreEqual("OBJECT IS x1, PAGE 1 1:9 S WAIT", Held(observer, spid));
        AreEqual($"LCK_M_S PAGE: {sim.ExecuteScalar("select db_id()")}:1:9 ", observer.CreateCommand($"select concat(wait_type, ' ', wait_resource) from sys.dm_exec_requests where session_id = {spid}").ExecuteScalar());
        StartsWith("pagelock fileid=1 pageid=9 ", (string)observer.CreateCommand($"select resource_description from sys.dm_os_waiting_tasks where session_id = {spid}").ExecuteScalar()!);
        Contains(" mode=IX ", (string)observer.CreateCommand($"select resource_description from sys.dm_os_waiting_tasks where session_id = {spid}").ExecuteScalar()!);
        _ = holding.CreateCommand("rollback").ExecuteNonQuery();
        _ = await paged;
    }

    /// <summary><c>sp_lock</c> lists a page lock as <c>PAG</c>, its index the page's own.</summary>
    [TestMethod]
    public void SpLock_ListsPagesAsPag()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables);
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran; update n set v = 55 where id = 5").ExecuteNonQuery();
        using var reader = connection.CreateCommand("exec sp_lock @@spid").ExecuteReader();
        var pages = reader.EnumerateRecords().Where(r => r.GetString(4) == "PAG").Select(r => $"{r.GetInt16(3)} {r.GetString(5).TrimEnd()} {r.GetString(6)}").ToList();
        CollectionAssert.AreEqual(new[] { "1 1:9 IX", "2 1:11 IX" }, pages);
    }
}
