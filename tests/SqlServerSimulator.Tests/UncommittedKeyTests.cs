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
// a threadpool thread and assert on a deadline that it started.
public sealed class UncommittedKeyTests
{
    private const int ThreadStartTimeoutMs = 10_000;

    public TestContext TestContext { get; set; } = null!;

    private static Simulation Keyed()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (k int not null primary key, v int not null);
            create table u (id int not null, code int not null);
            create unique index ux on u (code);
            insert t values (10, 1), (20, 2), (30, 3);
            insert u values (1, 100)
            """);
        return sim;
    }

    // Runs `sql` on `conn` from a threadpool thread, asserts it is still
    // blocked once the holder has had time to matter, then runs `release` on
    // the holder and returns the blocked statement's outcome.
    private async Task<Exception?> BlockedUntil(DbConnection holder, DbConnection conn, string sql, string release)
    {
        using var started = new ManualResetEventSlim();
        var task = Task.Run(
            () =>
            {
                started.Set();
                try
                {
                    _ = conn.CreateCommand(sql).ExecuteNonQuery();
                    return null;
                }
                catch (SimulatedSqlException error)
                {
                    return (Exception)error;
                }
            },
            TestContext.CancellationToken);
        IsTrue(started.Wait(ThreadStartTimeoutMs, TestContext.CancellationToken));
        await Task.Delay(150, TestContext.CancellationToken);
        IsFalse(task.IsCompleted, $"expected `{sql}` to block");
        _ = holder.CreateCommand(release).ExecuteNonQuery();
        return await task;
    }

    [TestMethod]
    [DataRow("insert t values (22, 1)", "insert t values (22, 9)")]
    [DataRow("delete t where k = 10", "insert t values (10, 9)")]
    [DataRow("update t set k = 15 where k = 10", "insert t values (10, 9)")]
    [DataRow("update t set k = 15 where k = 10", "insert t values (15, 9)")]
    [DataRow("insert u values (2, 200)", "insert u values (3, 200)")]
    [DataRow("delete u where code = 100", "insert u values (3, 100)")]
    [DataRow("delete t where k = 10", "update t set k = 10 where k = 20")]
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
        var outcome = await BlockedUntil(holder, other, "insert t values (10, 9)", "rollback");

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
        IsNull(await BlockedUntil(holder, other, "insert t values (10, 9)", "commit"));

        AreEqual("10:9", sim.ExecuteScalar("select string_agg(concat(k, ':', v), ',') from t where k = 10"));
    }

    [TestMethod]
    public async Task SecondInsertOfAKey_FailsOnlyOnceTheFirstCommits()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert t values (22, 1)").ExecuteNonQuery();
        var outcome = await BlockedUntil(holder, other, "insert t values (22, 9)", "commit");

        AreEqual(2627, IsInstanceOfType<SimulatedSqlException>(outcome).Number);
    }

    [TestMethod]
    public async Task SecondInsertOfAKey_SucceedsWhenTheFirstRollsBack()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert t values (22, 1)").ExecuteNonQuery();
        IsNull(await BlockedUntil(holder, other, "insert t values (22, 9)", "rollback"));

        AreEqual(9, sim.ExecuteScalar("select v from t where k = 22"));
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

    [TestMethod]
    public async Task ScanOverAnUncommittedDelete_SeesTheRowAgainAfterRollback()
    {
        var sim = Keyed();
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; delete t where k = 10").ExecuteNonQuery();
        IsNull(await BlockedUntil(holder, other, "select count(*) from t", "rollback"));
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
        var outcome = await BlockedUntil(holder, other, "insert c values (10, 2)", "rollback");

        AreEqual(547, IsInstanceOfType<SimulatedSqlException>(outcome).Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from c where pid = 2"));
    }
}
