using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// User-facing phase-0 locking surface: SET LOCK_TIMEOUT acceptance, basic
/// multi-connection sequential DDL/DML, the unchanged single-connection
/// behavior. The detailed Sch-S/Sch-M / Msg 1222 / Msg 1205 / SPID
/// allocation tests live in the Internal test project against the
/// <see cref="LockResource"/> primitive directly — those behaviors are
/// observable through the SchemaObject lock state but not through pure SQL
/// in phase 0 (every Sch-S / Sch-M acquisition is intra-statement and
/// released before the result yields). Phase 1a's X locks will surface
/// blocking end-to-end through SQL.
/// </summary>
[TestClass]
public sealed class LockingTests
{
    /// <summary>
    /// No conflicting holder → SET LOCK_TIMEOUT N has no observable effect
    /// on a single-connection workload. Pin that the statement parses and
    /// executes cleanly across the supported range.
    /// </summary>
    [TestMethod]
    public void SetLockTimeout_PositiveInteger_AcceptedAsNoOpForSingleConnection()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            set lock_timeout 5000;
            create table t (id int);
            insert t values (1);
            select count(*) from t
            """));

    [TestMethod]
    public void SetLockTimeout_Zero_AcceptedAsNoOpForSingleConnection()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            set lock_timeout 0;
            create table t (id int);
            insert t values (42);
            select count(*) from t
            """));

    [TestMethod]
    public void TwoConnections_SequentialDdlDml_NoCorruption()
    {
        // Smoke test: two SimulatedDbConnection instances on the same
        // Simulation, used in alternation from one thread, complete their
        // statements correctly. Exercises the ConcurrentDictionary-backed
        // schema dicts, the per-connection state isolation already in
        // place, and the new lock-acquisition path (which is a no-op when
        // there's no contention).
        var sim = new Simulation();
        using var connA = sim.CreateOpenConnection();
        using var connB = sim.CreateOpenConnection();

        _ = connA.CreateCommand("create table shared (id int, value nvarchar(50))").ExecuteNonQuery();
        _ = connA.CreateCommand("insert shared values (1, 'a')").ExecuteNonQuery();
        _ = connB.CreateCommand("insert shared values (2, 'b')").ExecuteNonQuery();
        _ = connA.CreateCommand("insert shared values (3, 'c')").ExecuteNonQuery();

        AreEqual(3, connB.CreateCommand("select count(*) from shared").ExecuteScalar());
        AreEqual(3, connA.CreateCommand("select count(*) from shared").ExecuteScalar());
    }

    [TestMethod]
    public void TwoThreads_ConcurrentInsertsIntoSeparateTables_BothSucceed()
    {
        // Two threads, two connections, two tables — no contention at all.
        // Validates the connection-state isolation and the lookup-side
        // concurrency (ConcurrentDictionary schema dicts) under real
        // parallelism.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (id int);
            create table b (id int)
            """);

        var threadA = new Thread(() =>
        {
            using var conn = sim.CreateOpenConnection();
            for (var i = 0; i < 50; i++)
                _ = conn.CreateCommand($"insert a values ({i})").ExecuteNonQuery();
        });
        var threadB = new Thread(() =>
        {
            using var conn = sim.CreateOpenConnection();
            for (var i = 0; i < 50; i++)
                _ = conn.CreateCommand($"insert b values ({i})").ExecuteNonQuery();
        });
        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        AreEqual(50, sim.ExecuteScalar("select count(*) from a"));
        AreEqual(50, sim.ExecuteScalar("select count(*) from b"));
    }

    [TestMethod]
    public async Task TxScopedX_BlocksConcurrentRead_UntilCommit()
    {
        // Phase 1a: BEGIN TRAN; INSERT holds X on the table until COMMIT.
        // A concurrent read on a different connection (different thread)
        // acquires S, conflicts with the X → blocks. Once we commit, the
        // S grants and the read completes.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; insert t values (42)").ExecuteNonQuery();

        // Reader on a separate thread tries to count rows — blocks on X.
        var readerStarted = new ManualResetEventSlim();
        var readerResult = (int?)null;
        var readerTask = Task.Run(() =>
        {
            readerStarted.Set();
            readerResult = (int)reader.CreateCommand("select count(*) from t").ExecuteScalar()!;
        }, TestContext.CancellationToken);

        IsTrue(readerStarted.Wait(2000, TestContext.CancellationToken));
        // Give the reader thread time to enter the wait.
        await Task.Delay(100, TestContext.CancellationToken);
        IsNull(readerResult);

        // Commit the writer's tx → reader unblocks and observes the row.
        _ = writer.CreateCommand("commit tran").ExecuteNonQuery();
        await readerTask;
        AreEqual(1, readerResult);
    }

    [TestMethod]
    public async Task TxScopedX_LockTimeoutZero_ReadRaisesMsg1222()
    {
        // Same setup as the blocking test, but the reader has SET
        // LOCK_TIMEOUT 0 → fail-fast on the conflicting S acquire (Msg
        // 1222 within milliseconds, no wait).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; insert t values (1)").ExecuteNonQuery();

        var ex = await Task.Run(() =>
            Throws<DbException>(() =>
                reader.CreateCommand("set lock_timeout 0; select count(*) from t").ExecuteScalar()),
            TestContext.CancellationToken);
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task NoLockHint_ReadsThroughUncommittedX()
    {
        // NOLOCK skips S acquisition entirely → reader sees the
        // uncommitted INSERT immediately. Probe-confirmed dirty-read
        // semantics — the value WAS written to the heap; the lock just
        // gated visibility under READ COMMITTED. NOLOCK bypasses.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; insert t values (42)").ExecuteNonQuery();

        var readValue = await Task.Run(() =>
            reader.CreateCommand("select count(*) from t with (nolock)").ExecuteScalar(),
            TestContext.CancellationToken);
        AreEqual(1, readValue);

        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task HoldLockHint_KeepsSUntilCommit()
    {
        // HOLDLOCK upgrades S to tx-scope. A connection that does
        // BEGIN TRAN; SELECT ... WITH (HOLDLOCK); then sits, holds an
        // X-blocking S until the eventual COMMIT. A concurrent INSERT
        // on another connection can't acquire X until the holder
        // commits.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; select * from t with (holdlock)").ExecuteScalar();

        var writeStarted = new ManualResetEventSlim();
        var writeTask = Task.Run(() =>
        {
            writeStarted.Set();
            _ = writer.CreateCommand("insert t values (2)").ExecuteNonQuery();
        }, TestContext.CancellationToken);

        IsTrue(writeStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);
        IsFalse(writeTask.IsCompleted);

        _ = holder.CreateCommand("commit tran").ExecuteNonQuery();
        await writeTask;
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public async Task CrossThreadDeadlock_OneVictim_RaisesMsg1205()
    {
        // Classic 2-cycle deadlock: A locks t1, B locks t2; then A asks
        // for t2 (blocks on B), B asks for t1 (cycle closes → Msg 1205
        // on the requester per the always-the-requester policy).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t1 (id int); create table t2 (id int); insert t1 values (1); insert t2 values (2)");
        using var connA = sim.CreateOpenConnection();
        using var connB = sim.CreateOpenConnection();

        _ = connA.CreateCommand("begin tran; update t1 set id = 10").ExecuteNonQuery();
        _ = connB.CreateCommand("begin tran; update t2 set id = 20").ExecuteNonQuery();

        Exception? aError = null;
        Exception? bError = null;
        using var aStarted = new ManualResetEventSlim();
        using var bStarted = new ManualResetEventSlim();
        var taskA = Task.Run(() =>
        {
            aStarted.Set();
            try { _ = connA.CreateCommand("update t2 set id = 11").ExecuteNonQuery(); }
            catch (Exception ex) { aError = ex; }
        }, TestContext.CancellationToken);
        // Wait for A to enter the wait, then have B request t1 → cycle.
        IsTrue(aStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);

        var taskB = Task.Run(() =>
        {
            bStarted.Set();
            try { _ = connB.CreateCommand("update t1 set id = 21").ExecuteNonQuery(); }
            catch (Exception ex) { bError = ex; }
        }, TestContext.CancellationToken);

        IsTrue(bStarted.Wait(2000, TestContext.CancellationToken));
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);

        // Exactly one connection was the victim — phase-1a policy is
        // always-the-requester (the connection that closed the cycle).
        IsTrue(aError is null ^ bError is null);
        var victim = aError ?? bError;
        IsNotNull(victim);
        var ex = IsInstanceOfType<DbException>(victim);
        AreEqual("1205", ex.Data["HelpLink.EvtID"]);

        // Clean up — the non-victim's tx is still alive.
        var survivor = aError is null ? connA : connB;
        _ = survivor.CreateCommand("commit").ExecuteNonQuery();
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TwoThreads_ConcurrentDropOnSameTable_OneSucceedsOneRaises3701()
    {
        // Both threads race to DROP the same table. Sch-M acquisition
        // serializes the two — one wins (the dict TryRemove succeeds), the
        // other acquires Sch-M after the winner releases, finds the table
        // already gone via TryRemove returning false, and raises Msg 3701.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table victim (id int)");

        Exception? errorA = null;
        Exception? errorB = null;
        var threadA = new Thread(() =>
        {
            try
            {
                using var conn = sim.CreateOpenConnection();
                _ = conn.CreateCommand("drop table victim").ExecuteNonQuery();
            }
            catch (Exception ex) { errorA = ex; }
        });
        var threadB = new Thread(() =>
        {
            try
            {
                using var conn = sim.CreateOpenConnection();
                _ = conn.CreateCommand("drop table victim").ExecuteNonQuery();
            }
            catch (Exception ex) { errorB = ex; }
        });
        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Exactly one thread should have errored (the loser).
        var loserError = errorA ?? errorB;
        IsNotNull(loserError);
        // The winner threw nothing.
        IsTrue(errorA is null ^ errorB is null);
        var dbException = IsInstanceOfType<DbException>(loserError);
        AreEqual("3701", dbException.Data["HelpLink.EvtID"]);
    }
}
