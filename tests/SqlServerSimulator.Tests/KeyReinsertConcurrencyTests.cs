using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A transaction that deletes a key and inserts it again leaves the row at a
/// new address here, where real's index keeps the key in place. Every reader
/// and writer that meets the key while that transaction is open waits on it
/// and then reads or writes the row the key holds once the transaction
/// settles, as real does (probed 2026-10-03 against SQL Server 2025) — none of
/// them may pass the key by, read the deleted row, or leave the row it read
/// unlocked.
/// </summary>
[TestClass]
public sealed class KeyReinsertConcurrencyTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Accounts()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table acc (id int not null primary key, bal int not null); insert acc values (1, 10), (2, 20), (3, 30)");
        return sim;
    }

    [TestMethod]
    [DataRow("update t set bal = t.bal + d.delta from acc t join (values (2, 100)) d (id, delta) on t.id = d.id; select @@rowcount")]
    [DataRow("update t set bal = t.bal + d.delta from (values (2, 100)) d (id, delta) join acc t on t.id = d.id; select @@rowcount")]
    [DataRow("update t set bal = t.bal + d.delta from acc t join (values (2, 100)) d (id, delta) on t.id = d.id where t.id = 2; select @@rowcount")]
    [DataRow("update t set bal = t.bal + 100 from acc t join acc p on p.id = t.id where p.id = 2; select @@rowcount")]
    [DataRow("merge acc as t using (values (2, 100)) as d (id, delta) on t.id = d.id when matched then update set bal = t.bal + d.delta when not matched then insert values (d.id, d.delta); select @@rowcount")]
    [DataRow("update acc set bal = bal + 100 where id = 2; select @@rowcount")]
    public async Task WriterMeetingTheKey_WritesTheReinsertedRow(string write)
    {
        var sim = Accounts();
        using var holder = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete acc where id = 2; insert acc values (2, 25)").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(writer, write, TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(1, (await blocked).Single());
        AreEqual("1:10,2:125,3:30", sim.ExecuteScalar("select string_agg(concat(id, ':', bal), ',') within group (order by id) from acc"));
    }

    [TestMethod]
    [DataRow("select bal from acc with (updlock) where id = 2")]
    [DataRow("select bal from acc with (updlock, holdlock) where id = 2")]
    [DataRow("set transaction isolation level repeatable read; select bal from acc where id = 2")]
    [DataRow("set transaction isolation level serializable; select bal from acc where id = 2")]
    public async Task LockingSeekMeetingTheKey_ReadsTheReinsertedRow(string read)
    {
        var sim = Accounts();
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete acc where id = 2; insert acc values (2, 25)").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(reader, "begin tran; " + read, TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(25, (await blocked).Single());
        _ = reader.CreateCommand("commit; set transaction isolation level read committed").ExecuteNonQuery();
    }

    /// <summary>
    /// A SERIALIZABLE scan's key fence is taken over the keys the table holds
    /// as the scan starts, which a key deleted in flight is missing from: the
    /// scan still locks it as it reads the row put back under it, so a later
    /// write of the key waits for the reader.
    /// </summary>
    [TestMethod]
    [DataRow("serializable")]
    [DataRow("repeatable read")]
    public async Task LockingScanMeetingTheKey_ReadsAndHoldsTheReinsertedRow(string isolation)
    {
        var sim = Accounts();
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete acc where id = 3; insert acc values (3, 33)").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(reader, $"set transaction isolation level {isolation}; begin tran; select sum(bal) from acc", TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(63, (await blocked).Single());
        _ = holder.CreateCommand("set lock_timeout 0").ExecuteNonQuery();
        AreEqual(1222, Throws<SimulatedSqlException>(() => holder.CreateCommand("update acc set bal = bal + 1 where id = 3").ExecuteNonQuery()).Number);
        _ = reader.CreateCommand("commit; set transaction isolation level read committed").ExecuteNonQuery();
    }

    /// <summary>
    /// A heap has no index order to keep a key's place, so a repeatable-read
    /// scan waiting on one row passes a key another transaction deletes and
    /// inserts again into a slot the scan has already read, and counts one row
    /// short; the next scan meets it. Real's heap scan does the same (probed
    /// 2026-10-07 against SQL Server 2025: 8 rows summing 45, then 9).
    /// </summary>
    [TestMethod]
    public async Task RepeatableReadHeapScan_PassesAKeyReinsertedBehindIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table hp (k int constraint uq_hp unique, v int); insert hp select value, value from generate_series(1, 10); delete hp where k = 2");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; update hp set v = v where k = 5").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(reader, "set transaction isolation level repeatable read; begin tran; select sum(v) from hp; select count(*) from hp", TestContext.CancellationToken);
        _ = writer.CreateCommand("delete hp where k = 8; insert hp values (8, 8); commit").ExecuteNonQuery();

        CollectionAssert.AreEqual(new object[] { 45, 9 }, await blocked);
        _ = reader.CreateCommand("commit; set transaction isolation level read committed").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task ScanMeetingADeleteThatRollsBack_ReadsTheRestoredRow()
    {
        var sim = Accounts();
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete acc where id = 2").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(reader, "select count(*) from acc with (updlock)", TestContext.CancellationToken);
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();

        AreEqual(3, (await blocked).Single());
    }

    /// <summary>
    /// Transfers that each delete and reinsert one account and credit
    /// another, run from several sessions at once, keep the total: no
    /// session reads an account's balance off a row another has already
    /// replaced, and no credit misses the row its account moved to.
    /// </summary>
    [TestMethod]
    public void ConcurrentDeleteReinsertTransfers_ConserveTheTotal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table acc (id int not null primary key, bal int not null); insert acc select value, 1000 from generate_series(1, 4)");
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            using var conn = sim.CreateOpenConnection();
            var random = new Random(t);
            for (var i = 0; i < 60; i++)
            {
                var a = random.Next(1, 5);
                var b = (a % 4) + 1;
                try
                {
                    _ = conn.CreateCommand($"""
                        begin try begin tran;
                        declare @va int = (select bal from acc with (updlock, holdlock) where id = {a});
                        delete acc where id = {a};
                        insert acc values ({a}, @va - 7);
                        update acc set bal = bal + 7 where id = {b};
                        if @@rowcount <> 1 raiserror('credit missed', 16, 1);
                        commit;
                        end try begin catch if @@trancount > 0 rollback; throw; end catch
                        """).ExecuteNonQuery();
                }
                catch (SimulatedSqlException e) when (e.Number == 1205)
                {
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                    return;
                }
            }
        })).ToArray();
        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            IsTrue(thread.Join(60_000));

        IsEmpty(failures);
        AreEqual("4:4000", sim.ExecuteScalar("select concat(count(*), ':', sum(bal)) from acc"));
    }

    /// <summary>
    /// A keyset cursor's fetch of a member another transaction is deleting
    /// and putting back waits on it, then reads the row the key holds — the
    /// reinserted one after a commit, the restored one after a rollback —
    /// whatever its concurrency (probed 2026-10-03 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("optimistic", "commit", 25)]
    [DataRow("optimistic", "rollback", 20)]
    [DataRow("scroll_locks", "commit", 25)]
    [DataRow("scroll_locks", "rollback", 20)]
    [DataRow("read_only", "commit", 25)]
    public async Task KeysetFetchOfAMovingMember_ReadsTheSettledRow(string concurrency, string settle, int expected)
    {
        var sim = Accounts();
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand($"begin tran; declare c cursor global keyset {concurrency} for select bal, id from acc where id in (1, 2) order by id; open c; fetch next from c").ExecuteScalar();

        _ = holder.CreateCommand("begin tran; delete acc where id = 2; insert acc values (2, 25)").ExecuteNonQuery();
        var fetch = await sim.StartBlocked(reader, "fetch next from c; select @@fetch_status", TestContext.CancellationToken);
        _ = holder.CreateCommand(settle).ExecuteNonQuery();

        CollectionAssert.AreEqual(new object?[] { expected, 0 }, await fetch);
        _ = reader.CreateCommand("close c; deallocate c; commit").ExecuteNonQuery();
    }

    /// <summary>
    /// An OPTIMISTIC cursor's positioned UPDATE of a row another transaction
    /// is deleting and putting back waits on it, then finds the row it
    /// fetched gone: the optimistic conflict (Msg 16934, then Msg 16947),
    /// where it once wrote nothing and reported success (probed 2026-10-03
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task OptimisticPositionedUpdate_OfAMovingRow_IsAConflict()
    {
        var sim = Accounts();
        using var holder = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; declare c cursor global keyset optimistic for select id, bal from acc where id = 2 for update of bal; open c; fetch next from c").ExecuteScalar();

        _ = holder.CreateCommand("begin tran; delete acc where id = 2; insert acc values (2, 25)").ExecuteNonQuery();
        var update = await sim.StartBlocked(writer, "update acc set bal = bal + 1 where current of c", TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(16947, (await ThrowsAsync<SimulatedSqlException>(() => update)).Number);
        _ = writer.CreateCommand("rollback; deallocate c").ExecuteNonQuery();
        AreEqual(25, sim.ExecuteScalar("select bal from acc where id = 2"));
    }

    /// <summary>
    /// A SERIALIZABLE seek through a heap's unique nonclustered key locks the
    /// row it finds, which a key deleted and put back elsewhere moves: the
    /// row read after the wait is locked as it is read, so a later write of
    /// it waits for the reader.
    /// </summary>
    [TestMethod]
    public async Task SerializableSeekThroughAHeapsUniqueKey_HoldsTheReinsertedRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table acc (id int not null primary key nonclustered, bal int not null); insert acc values (1, 10), (2, 20), (3, 30)");
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete acc where id = 2; insert acc values (2, 25)").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(reader, "set transaction isolation level serializable; begin tran; select bal from acc where id = 2", TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(25, (await blocked).Single());
        _ = holder.CreateCommand("set lock_timeout 0").ExecuteNonQuery();
        AreEqual(1222, Throws<SimulatedSqlException>(() => holder.CreateCommand("update acc set bal = bal + 1 where id = 2").ExecuteNonQuery()).Number);
        _ = reader.CreateCommand("commit; set transaction isolation level read committed").ExecuteNonQuery();
    }
}
