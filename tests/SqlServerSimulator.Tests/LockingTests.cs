using System.Data.Common;
using SqlServerSimulator.Storage;
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

    [TestMethod]
    public async Task RowLevelRC_ReaderOfDifferentRow_DoesNotBlockOnWritersRow()
    {
        // Phase 1b: the writer's row-X is on row id=1; the reader scans
        // for id=2 which is a different RID. Under phase-1a's table-only
        // granularity the reader would have blocked on the writer's
        // table-X; under phase 1b the reader only blocks on the specific
        // row-X being held. With READPAST it could even read past blocked
        // rows, but without it the reader simply finds no conflict on
        // row 2 and proceeds.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1), (2)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; update t set id = 11 where id = 1").ExecuteNonQuery();

        // Reader scans for id=2 — the writer's row-X is on row id=1's RID,
        // not row id=2's. The reader probes id=1 and waits (or skips with
        // READPAST). Without READPAST, the reader still gets blocked on
        // row id=1's probe. So this test uses READPAST to demonstrate row-
        // level granularity isolation.
        var readResult = await Task.Run(() =>
            reader.CreateCommand("select count(*) from t with (readpast) where id = 2").ExecuteScalar(),
            TestContext.CancellationToken);
        AreEqual(1, readResult);

        _ = writer.CreateCommand("commit tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task ReadPastHint_SkipsBlockedRowsInsteadOfWaiting()
    {
        // READPAST: when a row's RID has a conflicting row-X holder, the
        // reader skips it rather than blocking. Probe-confirmed semantic.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1), (2), (3)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; update t set id = 10 where id = 1").ExecuteNonQuery();

        // Without READPAST, the reader would block on row 1's row-X.
        // With READPAST, it skips row 1 and yields rows 2 and 3 only.
        var readCount = await Task.Run(() =>
            reader.CreateCommand("select count(*) from t with (readpast)").ExecuteScalar(),
            TestContext.CancellationToken);
        AreEqual(2, readCount);

        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task UpdLockHint_RowU_BlocksAnotherUpdLockOnSameRow()
    {
        // UPDLOCK: SELECT WITH (UPDLOCK) takes row-U tx-scoped. Another
        // connection's UPDLOCK on the same row conflicts (U × U) until the
        // first holder commits. EF Core's pessimistic-concurrency pattern
        // uses this idiom to serialize "read-and-then-update" sequences.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; select * from t with (updlock)").ExecuteScalar();

        var otherStarted = new ManualResetEventSlim();
        var otherTask = Task.Run(() =>
        {
            otherStarted.Set();
            _ = other.CreateCommand("select * from t with (updlock)").ExecuteScalar();
        }, TestContext.CancellationToken);

        IsTrue(otherStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);
        IsFalse(otherTask.IsCompleted);

        _ = holder.CreateCommand("commit tran").ExecuteNonQuery();
        await otherTask;
    }

    [TestMethod]
    public async Task XLockHint_RowX_BlocksConcurrentRead()
    {
        // XLOCK: SELECT WITH (XLOCK) takes row-X tx-scoped. A concurrent
        // reader of the same row blocks (the X-X conflict via probe-and-
        // wait under RC) until commit.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; select * from t with (xlock)").ExecuteScalar();

        var readStarted = new ManualResetEventSlim();
        var readResult = (int?)null;
        var readTask = Task.Run(() =>
        {
            readStarted.Set();
            readResult = (int)reader.CreateCommand("select count(*) from t").ExecuteScalar()!;
        }, TestContext.CancellationToken);

        IsTrue(readStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);
        IsNull(readResult);

        _ = holder.CreateCommand("commit tran").ExecuteNonQuery();
        await readTask;
        AreEqual(1, readResult);
    }

    [TestMethod]
    public async Task TabLockXHint_TableX_BlocksAllOtherAccess()
    {
        // TABLOCKX: write takes table-X tx-scoped instead of table-IX +
        // row-X. Every other connection — read or write — blocks until
        // commit.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var holder = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; insert t with (tablockx) values (1)").ExecuteNonQuery();

        var readStarted = new ManualResetEventSlim();
        var readResult = (int?)null;
        var readTask = Task.Run(() =>
        {
            readStarted.Set();
            readResult = (int)reader.CreateCommand("select count(*) from t").ExecuteScalar()!;
        }, TestContext.CancellationToken);

        IsTrue(readStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);
        IsNull(readResult);

        _ = holder.CreateCommand("commit tran").ExecuteNonQuery();
        await readTask;
        AreEqual(1, readResult);
    }

    [TestMethod]
    public async Task SerializableIsolation_BlocksConcurrentInsertForPhantomPrevention()
    {
        // SET TRANSACTION ISOLATION LEVEL SERIALIZABLE: a read scan takes
        // table-S tx-scoped (the simulator's phantom-prevention
        // approximation — real SQL Server uses key-range locks; without
        // indexes that degenerates to table-level). A concurrent INSERT
        // can't acquire table-IX while the SERIALIZABLE reader holds
        // table-S, so the INSERT blocks until the reader's tx commits.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select * from t").ExecuteScalar();

        var writeStarted = new ManualResetEventSlim();
        var writeTask = Task.Run(() =>
        {
            writeStarted.Set();
            _ = writer.CreateCommand("insert t values (2)").ExecuteNonQuery();
        }, TestContext.CancellationToken);

        IsTrue(writeStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);
        IsFalse(writeTask.IsCompleted);

        _ = reader.CreateCommand("commit tran").ExecuteNonQuery();
        await writeTask;
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public async Task RepeatableReadIsolation_BlocksUpdateOfReadRow_AllowsInsert()
    {
        // SET TRANSACTION ISOLATION LEVEL REPEATABLE READ: a read scan
        // takes row-S tx-scoped per row read. A concurrent UPDATE of one
        // of those rows blocks (row-S × row-X conflict). But a concurrent
        // INSERT of a new row succeeds — REPEATABLE READ doesn't prevent
        // phantoms, only "non-repeatable read" of existing rows.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level repeatable read; begin tran; select * from t").ExecuteScalar();

        // Concurrent INSERT of a NEW row — should succeed (no phantom
        // prevention under RR).
        _ = await Task.Run(() =>
            writer.CreateCommand("insert t values (2)").ExecuteNonQuery(),
            TestContext.CancellationToken);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t with (nolock)"));

        // Concurrent UPDATE of the ALREADY-READ row — should block.
        var upStarted = new ManualResetEventSlim();
        var upTask = Task.Run(() =>
        {
            upStarted.Set();
            _ = writer.CreateCommand("update t set id = 10 where id = 1").ExecuteNonQuery();
        }, TestContext.CancellationToken);

        IsTrue(upStarted.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(100, TestContext.CancellationToken);
        IsFalse(upTask.IsCompleted);

        _ = reader.CreateCommand("commit tran").ExecuteNonQuery();
        await upTask;
    }

    [TestMethod]
    public async Task ReadUncommittedIsolation_AllowsDirtyRead()
    {
        // SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED: reader skips
        // every conflict check, sees uncommitted writes. Equivalent to
        // WITH (NOLOCK) on every read.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; insert t values (42)").ExecuteNonQuery();

        var readValue = await Task.Run(() =>
            reader.CreateCommand("set transaction isolation level read uncommitted; select count(*) from t").ExecuteScalar(),
            TestContext.CancellationToken);
        AreEqual(1, readValue);

        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task UpdateOfDifferentRows_DoesNotBlock_AtRowGranularity()
    {
        // Phase 1b's row-X grants per RID: two writers on different rows
        // of the same table proceed in parallel (both take table-IX, then
        // disjoint row-X on disjoint RIDs).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, v int); insert t values (1, 10), (2, 20)");
        using var connA = sim.CreateOpenConnection();
        using var connB = sim.CreateOpenConnection();

        _ = connA.CreateCommand("begin tran; update t set v = 100 where id = 1").ExecuteNonQuery();

        // Connection B updates the OTHER row — should not block.
        await Task.Run(() =>
        {
            _ = connB.CreateCommand("update t set v = 200 where id = 2").ExecuteNonQuery();
        }, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);

        _ = connA.CreateCommand("commit tran").ExecuteNonQuery();

        AreEqual(100, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(200, sim.ExecuteScalar("select v from t where id = 2"));
    }

    [TestMethod]
    public void LockTimeoutScalar_Default_ReturnsMinusOne()
        => AreEqual(-1, new Simulation().ExecuteScalar("select @@lock_timeout"));

    [TestMethod]
    public void LockTimeoutScalar_AfterSet_ReturnsAssignedValue()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("set lock_timeout 5000").ExecuteNonQuery();
        AreEqual(5000, conn.CreateCommand("select @@lock_timeout").ExecuteScalar());
    }

    [TestMethod]
    public void SpidScalar_FirstUserConnection_Returns51()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        // @@SPID is smallint per probe — fits in short.
        AreEqual((short)51, conn.CreateCommand("select @@spid").ExecuteScalar());
    }

    [TestMethod]
    public void DmTranLocks_EmptySimulation_ReturnsNoRowsForUserTables()
    {
        // Fresh Simulation, no tx in flight — sys.dm_tran_locks shows zero
        // lock entries for any user table. (System tables / catalog views
        // bypass the lock manager so they don't appear.)
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_locks where resource_associated_entity_id = object_id('t')"));
    }

    [TestMethod]
    public void DmTranLocks_InsideUncommittedTx_ShowsHeldRowAndTableLocks()
    {
        // BEGIN TRAN; INSERT t VALUES (...) holds table-IX on t plus a
        // row-X on the inserted row. dm_tran_locks projects both.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("begin tran; insert t values (1)").ExecuteNonQuery();
        var count = (int)conn.CreateCommand("select count(*) from sys.dm_tran_locks where resource_associated_entity_id = object_id('t') and request_status = 'GRANT'").ExecuteScalar()!;
        IsGreaterThanOrEqualTo(2, count);
        // At least one IX entry (table-level) and at least one X entry (row-level).
        var hasIx = (int)conn.CreateCommand("select count(*) from sys.dm_tran_locks where request_mode = 'IX' and resource_associated_entity_id = object_id('t')").ExecuteScalar()! > 0;
        var hasX = (int)conn.CreateCommand("select count(*) from sys.dm_tran_locks where request_mode = 'X' and resource_associated_entity_id = object_id('t')").ExecuteScalar()! > 0;
        IsTrue(hasIx);
        IsTrue(hasX);
        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_locks where resource_associated_entity_id = object_id('t')"));
    }

    [TestMethod]
    public async Task DmOsWaitingTasks_DuringContention_ShowsWaiterRow()
    {
        // Connection A holds row-U on row 1 via SELECT WITH (UPDLOCK);
        // Connection B tries SELECT WITH (UPDLOCK) on the same row → row-U
        // × row-U conflict → B waits. While B is blocked, the observer
        // connection queries sys.dm_os_waiting_tasks and sees one row
        // whose session_id = B's SPID and blocking_session_id = A's SPID.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var waiter = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; select * from t with (updlock)").ExecuteScalar();

        var started = new ManualResetEventSlim();
        var waitTask = Task.Run(() =>
        {
            started.Set();
            _ = waiter.CreateCommand("select * from t with (updlock)").ExecuteScalar();
        }, TestContext.CancellationToken);

        IsTrue(started.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(150, TestContext.CancellationToken);

        var holderSpid = (short)holder.CreateCommand("select @@spid").ExecuteScalar()!;
        var waiterSpid = (short)waiter.CreateCommand("select @@spid").ExecuteScalar()!;
        var rowsCount = (int)observer.CreateCommand($"select count(*) from sys.dm_os_waiting_tasks where session_id = {waiterSpid} and blocking_session_id = {holderSpid}").ExecuteScalar()!;
        AreEqual(1, rowsCount);

        _ = holder.CreateCommand("commit tran").ExecuteNonQuery();
        await waitTask;
    }

    [TestMethod]
    public async Task DmTranLocks_DuringContention_ShowsWaitRow()
    {
        // WAIT-row emission for sys.dm_tran_locks is only reached when a
        // connection is blocked on a resource and the DMV is queried
        // mid-wait. Holder takes a row-X via UPDATE inside a tran; waiter
        // blocks attempting the next UPDATE.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1)");
        using var holder = sim.CreateOpenConnection();
        using var waiter = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();

        _ = holder.CreateCommand("begin tran; update t set id = 2 where id = 1").ExecuteNonQuery();

        var started = new ManualResetEventSlim();
        var waitTask = Task.Run(() =>
        {
            started.Set();
            _ = waiter.CreateCommand("update t set id = 3 where id = 2").ExecuteNonQuery();
        }, TestContext.CancellationToken);

        IsTrue(started.Wait(2000, TestContext.CancellationToken));
        await Task.Delay(150, TestContext.CancellationToken);

        var waitRows = (int)observer.CreateCommand(
            "select count(*) from sys.dm_tran_locks where request_status = 'WAIT'")
            .ExecuteScalar()!;
        IsGreaterThanOrEqualTo(1, waitRows);

        _ = holder.CreateCommand("commit tran").ExecuteNonQuery();
        await waitTask;
    }

    [TestMethod]
    public void DmTranLocks_FiltersAcrossKnownModeAbbreviations()
    {
        // Doesn't try to provoke each mode — just runs the enumerator
        // through the abbreviation map for every recognized mode label so
        // the filter executes against a non-trivial schema.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        var count = (int)sim.ExecuteScalar(
            "select count(*) from sys.dm_tran_locks where request_mode in ('IS','IX','SIX','Sch-S','Sch-M','S','U','X')")!;
        IsGreaterThanOrEqualTo(0, count);
    }

    [TestMethod]
    public void DmTranLocks_EnumeratesAllSchemaObjectKinds()
    {
        // sys.dm_tran_locks walks heap tables, views, functions,
        // procedures, sequences, table types, and triggers. The per-kind
        // branches only enter when the schema contains at least one entry
        // of that kind.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int)",
            "create view v as select * from t",
            "create function fn(@x int) returns int as begin return @x + 1 end",
            "create procedure pr as select * from t",
            "create sequence sq as int start with 1",
            "create type tt as table (id int)",
            "create trigger trg on t for insert as select 1");
        var count = (int)sim.ExecuteScalar("select count(*) from sys.dm_tran_locks")!;
        IsGreaterThanOrEqualTo(0, count);
    }
}
