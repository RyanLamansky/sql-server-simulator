using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A uniqueness check against a key another open transaction is writing waits
/// for that transaction instead of deciding on its uncommitted state: a second
/// insert of a key waits on the first, and a key an uncommitted delete (or
/// key-changing update) took away can't be reused until that write settles —
/// otherwise a rollback would restore the old row beside the new one. Probed
/// 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
// Same scheduling caveat as LockingTests: the blocking assertions hand work to
// a threadpool thread and assert on a deadline that it is seen waiting.
public sealed class UncommittedKeyTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Keyed()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (k int not null primary key, v int not null);
            create table u (id int not null, code int not null);
            create unique index ux on u (code);
            create table n (k int null, constraint uqn unique (k));
            create table g (k int not null, constraint pkg primary key (k) with (ignore_dup_key = on));
            insert t values (10, 1), (20, 2), (30, 3);
            insert u values (1, 100)
            """);
        return sim;
    }

    // Runs `sql` on `conn` from a threadpool thread, asserts it waits, then
    // runs `release` on the holder and returns the blocked statement's outcome.
    private async Task<Exception?> BlockedUntil(Simulation sim, DbConnection holder, DbConnection conn, string sql, string release)
    {
        var task = await sim.StartBlocked(conn, sql, TestContext.CancellationToken);
        _ = holder.CreateCommand(release).ExecuteNonQuery();
        try
        {
            _ = await task;
            return null;
        }
        catch (SimulatedSqlException error)
        {
            return error;
        }
    }

    [TestMethod]
    [DataRow("insert t values (22, 1)", "insert t values (22, 9)")]
    [DataRow("delete t where k = 10", "insert t values (10, 9)")]
    [DataRow("update t set k = 15 where k = 10", "insert t values (10, 9)")]
    [DataRow("update t set k = 15 where k = 10", "insert t values (15, 9)")]
    [DataRow("insert u values (2, 200)", "insert u values (3, 200)")]
    [DataRow("delete u where code = 100", "insert u values (3, 100)")]
    [DataRow("delete t where k = 10", "update t set k = 10 where k = 20")]
    [DataRow("insert n values (null)", "insert n values (null)")]
    [DataRow("insert g values (1)", "insert g values (1)")]
    public void KeyUnderAnotherTransactionsWrite_Waits(string write, string contender)
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; " + write).ExecuteNonQuery();
        _ = other.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        AreEqual(1222, Throws<SimulatedSqlException>(() => other.CreateCommand(contender).ExecuteNonQuery()).Number);

        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task KeyFreedByADelete_IsNotReusedWhenTheDeleteRollsBack()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete t where k = 10").ExecuteNonQuery();
        var outcome = await BlockedUntil(sim, holder, other, "insert t values (10, 9)", "rollback");

        AreEqual(2627, IsInstanceOfType<SimulatedSqlException>(outcome).Number);
        AreEqual("10:1", sim.ExecuteScalar("select string_agg(concat(k, ':', v), ',') from t where k = 10"));
    }

    [TestMethod]
    public async Task KeyFreedByADelete_IsReusedOnceTheDeleteCommits()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete t where k = 10").ExecuteNonQuery();
        IsNull(await BlockedUntil(sim, holder, other, "insert t values (10, 9)", "commit"));

        AreEqual("10:9", sim.ExecuteScalar("select string_agg(concat(k, ':', v), ',') from t where k = 10"));
    }

    [TestMethod]
    public async Task SecondInsertOfAKey_FailsOnlyOnceTheFirstCommits()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert t values (22, 1)").ExecuteNonQuery();
        var outcome = await BlockedUntil(sim, holder, other, "insert t values (22, 9)", "commit");

        AreEqual(2627, IsInstanceOfType<SimulatedSqlException>(outcome).Number);
    }

    [TestMethod]
    public async Task SecondInsertOfAKey_SucceedsWhenTheFirstRollsBack()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert t values (22, 1)").ExecuteNonQuery();
        IsNull(await BlockedUntil(sim, holder, other, "insert t values (22, 9)", "rollback"));

        AreEqual(9, sim.ExecuteScalar("select v from t where k = 22"));
    }

    /// <summary>
    /// The second writer of a key requests X on it, as real's does (probed
    /// 2026-10-01 against SQL Server 2025: <c>LCK_M_X</c> on the first
    /// writer's key).
    /// </summary>
    [TestMethod]
    public async Task SecondInsertOfAKey_WaitsForX()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var monitor = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert t values (22, 1)").ExecuteNonQuery();
        var contender = Task.Run(() => Throws<SimulatedSqlException>(() => other.CreateCommand("insert t values (22, 9)").ExecuteNonQuery()), TestContext.CancellationToken);
        string? waiting = null;
        for (var attempt = 0; attempt < 1000 && waiting is null; attempt++)
        {
            waiting = (string?)monitor.CreateCommand("select resource_type + ' ' + request_mode from sys.dm_tran_locks where request_status = 'WAIT'").ExecuteScalar();
            if (waiting is null)
                await Task.Delay(5, TestContext.CancellationToken);
        }
        AreEqual("KEY X", waiting);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(2627, (await contender).Number);
    }

    /// <summary>
    /// A held key under <c>IGNORE_DUP_KEY</c> is waited on like any other,
    /// and the duplicate is ignored once its writer commits.
    /// </summary>
    [TestMethod]
    public async Task SecondInsertUnderIgnoreDupKey_IsIgnoredOnceTheFirstCommits()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert g values (1)").ExecuteNonQuery();
        IsNull(await BlockedUntil(sim, holder, other, "insert g values (1)", "commit"));

        AreEqual(1, sim.ExecuteScalar("select count(*) from g"));
    }

    /// <summary>
    /// Two transactions each inserting the key the other holds deadlock, and
    /// the one closing the cycle is the victim, as on real (probed 2026-10-01
    /// against SQL Server 2025). Which one closes it is the one that asks
    /// second, so the first is seen waiting before the second asks: a fixed
    /// delay let a slow runner start the first's wait only after the second's,
    /// closing the cycle from the other side.
    /// </summary>
    [TestMethod]
    public async Task CrossedInsertsOfEachOthersKey_Deadlock()
    {
        var sim = Keyed();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();

        _ = first.CreateCommand("begin tran; insert t values (41, 1)").ExecuteNonQuery();
        _ = second.CreateCommand("begin tran; insert t values (42, 2)").ExecuteNonQuery();
        var blocked = await sim.StartBlocked(first, "insert t values (42, 1)", TestContext.CancellationToken);

        AreEqual(1205, Throws<SimulatedSqlException>(() => second.CreateCommand("insert t values (41, 2)").ExecuteNonQuery()).Number);
        _ = await blocked;
        _ = first.CreateCommand("commit").ExecuteNonQuery();
        AreEqual("41:1,42:1", sim.ExecuteScalar("select string_agg(concat(k, ':', v), ',') within group (order by k) from t where k > 40"));
    }

    [TestMethod]
    public void OwnUncommittedWrites_DontBlockTheirOwnSession()
    {
        var sim = Keyed();
        using var conn = sim.CreateOpenConnection();

        _ = conn.CreateCommand("begin tran; delete t where k = 10; insert t values (10, 5); update t set k = 11 where k = 20; insert t values (20, 6)").ExecuteNonQuery();
        AreEqual(4, conn.CreateCommand("select count(*) from t").ExecuteScalar());
        _ = conn.CreateCommand("commit").ExecuteNonQuery();
    }

    /// <summary>
    /// A locking read that would have met a row another open transaction
    /// deleted waits for that transaction, where the heap walk used to skip
    /// the tombstone and report the delete before it committed; a seek to a
    /// different key, a NOLOCK read and a READPAST read don't wait.
    /// </summary>
    [TestMethod]
    [DataRow("select count(*) from t", true)]
    [DataRow("select v from t where k = 10", true)]
    [DataRow("select count(*) from h", true)]
    [DataRow("select v from h where k = 20", true)]
    [DataRow("select v from t where k = 20", false)]
    [DataRow("select count(*) from t where k between 5 and 15", true)]
    [DataRow("select count(*) from t where k between 15 and 25", false)]
    [DataRow("select count(*) from t where k > 15", false)]
    [DataRow("select count(*) from t with (nolock)", false)]
    [DataRow("select count(*) from t with (readpast)", false)]
    public void ReadOverAnUncommittedDelete_Waits(string read, bool waits)
    {
        var sim = Keyed();
        _ = sim.ExecuteNonQuery("create table h (k int, v int); insert h values (10, 1), (20, 2)");
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete t where k = 10; delete h where k = 10").ExecuteNonQuery();
        _ = other.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        if (waits)
            AreEqual(1222, Throws<SimulatedSqlException>(() => other.CreateCommand(read).ExecuteScalar()).Number);
        else
            IsNotNull(other.CreateCommand(read).ExecuteScalar());

        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// A seek's probe meets the deleted row's stored key as its own type — a
    /// literal shorter than the column, here — and still matches it and waits,
    /// where comparing the two unconverted raised NotSupportedException in the
    /// reader.
    /// </summary>
    [TestMethod]
    [DataRow("select id from a where d = N'Eagle'", true)]
    [DataRow("select id from a where d in (N'Eagle', N'Kiwi')", true)]
    [DataRow("select id from a where d = 'Eagle'", true)]
    [DataRow("select id from a where d = N'Rose'", false)]
    public void SeekOverAnUncommittedDelete_ByAShorterLiteral_Waits(string read, bool waits)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table a (id int primary key, d nvarchar(8) not null); create index ix on a(d); insert a values (1, N'Eagle'), (2, N'Kiwi'), (3, N'Rose')");
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete a where id = 1").ExecuteNonQuery();
        _ = other.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        if (waits)
            AreEqual(1222, Throws<SimulatedSqlException>(() => other.CreateCommand(read).ExecuteScalar()).Number);
        else
            AreEqual(3, other.CreateCommand(read).ExecuteScalar());

        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task ScanOverAnUncommittedDelete_SeesTheRowAgainAfterRollback()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete t where k = 10").ExecuteNonQuery();
        IsNull(await BlockedUntil(sim, holder, other, "select count(*) from t", "rollback"));
        AreEqual(3, other.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    private static Simulation ParentChild()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int primary key);
            create table c (id int primary key, pid int references p (id));
            insert p values (1), (7);
            insert c values (1, 7)
            """);
        return sim;
    }

    /// <summary>
    /// A foreign-key check against a parent or child row another open
    /// transaction is writing waits for it — an uncommitted parent insert
    /// would otherwise let a child in that its rollback orphans, and an
    /// uncommitted child delete would let the parent go that its rollback
    /// leaves the child pointing at.
    /// </summary>
    [TestMethod]
    [DataRow("insert p values (2)", "insert c values (10, 2)")]
    [DataRow("delete p where id = 1", "insert c values (10, 1)")]
    [DataRow("update p set id = 5 where id = 1", "insert c values (10, 1)")]
    [DataRow("insert c values (10, 1)", "delete p where id = 1")]
    [DataRow("update c set pid = 1 where id = 1", "delete p where id = 1")]
    [DataRow("delete c where id = 1", "delete p where id = 7")]
    public void ForeignKeyCheckOverAnotherTransactionsWrite_Waits(string write, string contender)
    {
        var sim = ParentChild();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; " + write).ExecuteNonQuery();
        _ = other.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        AreEqual(1222, Throws<SimulatedSqlException>(() => other.CreateCommand(contender).ExecuteNonQuery()).Number);

        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task ChildOfAnUncommittedParent_FailsWhenTheParentRollsBack()
    {
        var sim = ParentChild();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert p values (2)").ExecuteNonQuery();
        var outcome = await BlockedUntil(sim, holder, other, "insert c values (10, 2)", "rollback");

        AreEqual(547, IsInstanceOfType<SimulatedSqlException>(outcome).Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from c where pid = 2"));
    }
}
