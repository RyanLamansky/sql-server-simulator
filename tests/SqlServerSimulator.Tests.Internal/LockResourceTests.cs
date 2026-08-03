using System.Data.Common;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Direct exercises of the <see cref="LockManager"/> + <see cref="LockResource"/>
/// pair: per-owner re-entrant counting, compatibility matrix across
/// Sch-S / Sch-M / S / X, cross-thread blocking wait, LOCK_TIMEOUT path
/// (Msg 1222), same-thread-deadlock short-circuit (Msg 1205), and
/// wait-for-graph cycle detection (Msg 1205 on the requester).
/// </summary>
[TestClass]
public sealed class LockResourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SchS_OnEmptyResource_GrantsImmediately()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var conn = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, conn, timeoutMillis: 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, conn);
    }

    [TestMethod]
    public void SchS_TwoDifferentOwners_BothGrant()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, b, 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, b);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void SchM_BlocksConflictingSchS_TimeoutZeroRaises1222()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        sim.LockManager.Acquire(resource, LockMode.SchemaModification, a, 0);
        var ex = Throws<DbException>(() =>
            sim.LockManager.Acquire(resource, LockMode.SchemaStability, b, 0));
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
        AreEqual("Lock request time out period exceeded.", ex.Message);
        sim.LockManager.Release(resource, LockMode.SchemaModification, a);
    }

    [TestMethod]
    public void X_BlocksConflictingX_TimeoutZeroRaises1222()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        sim.LockManager.Acquire(resource, LockMode.Exclusive, a, 0);
        var ex = Throws<DbException>(() =>
            sim.LockManager.Acquire(resource, LockMode.Exclusive, b, 0));
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
        sim.LockManager.Release(resource, LockMode.Exclusive, a);
    }

    [TestMethod]
    public void X_BlocksConflictingS_TimeoutZeroRaises1222()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        sim.LockManager.Acquire(resource, LockMode.Exclusive, a, 0);
        _ = Throws<DbException>(() =>
            sim.LockManager.Acquire(resource, LockMode.Shared, b, 0));
        sim.LockManager.Release(resource, LockMode.Exclusive, a);
    }

    [TestMethod]
    public void SS_Compatible_BothGrant()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.Shared, a, 0);
        sim.LockManager.Acquire(resource, LockMode.Shared, b, 0);
        sim.LockManager.Release(resource, LockMode.Shared, b);
        sim.LockManager.Release(resource, LockMode.Shared, a);
    }

    [TestMethod]
    public void SchS_AndS_AreOrthogonal_BothGrant()
    {
        // The schema family (Sch-S/Sch-M) and the data family (S/X) are
        // independent — a Sch-S holder doesn't block an X requester and
        // vice versa.
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        sim.LockManager.Acquire(resource, LockMode.Exclusive, b, 0);
        sim.LockManager.Release(resource, LockMode.Exclusive, b);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void Reentrance_SameOwnerSameMode_BumpsCount()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a);
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        _ = Throws<DbException>(() => sim.LockManager.Acquire(resource, LockMode.SchemaModification, b, 0));
        sim.LockManager.Release(resource, LockMode.SchemaStability, a);
        sim.LockManager.Acquire(resource, LockMode.SchemaModification, b, 0);
        sim.LockManager.Release(resource, LockMode.SchemaModification, b);
    }

    [TestMethod]
    public async Task CrossThread_SchSDrainsAfterRelease_QueuedSchMSucceeds()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var holder = sim.CreateDbConnection();
        var waiter = sim.CreateDbConnection();
        var holderTask = Task.Run(() =>
        {
            holder.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
            sim.LockManager.Acquire(resource, LockMode.SchemaStability, holder, 0);
            Thread.Sleep(100);
            sim.LockManager.Release(resource, LockMode.SchemaStability, holder);
            holder.CurrentExecutingThreadId = null;
        }, TestContext.CancellationToken);
        await Task.Delay(20, TestContext.CancellationToken);
        sim.LockManager.Acquire(resource, LockMode.SchemaModification, waiter, 1000);
        sim.LockManager.Release(resource, LockMode.SchemaModification, waiter);
        await holderTask;
    }

    [TestMethod]
    public void SameThreadConflict_RaisesMsg1205Immediately()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        var ex = Throws<DbException>(() =>
            sim.LockManager.Acquire(resource, LockMode.SchemaModification, b, 10000));
        AreEqual("1205", ex.Data["HelpLink.EvtID"]);
        Contains($"Process ID {b.Spid}", ex.Message);
        Contains("deadlocked on lock resources", ex.Message);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void CycleDetection_DetectorPicksRequester()
    {
        // Set up a 2-cycle without real threading: this thread holds X on
        // r1; a second "impersonated" connection holds X on r2 and is
        // marked as waiting on r1 (WaitingOnResource = r1). When this
        // thread asks for X on r2, the detector walks r2's holders → b →
        // b.WaitingOnResource = r1 → r1's holders → us, cycle closed.
        // Caller (us) is the victim per the always-the-requester policy.
        var sim = new Simulation();
        var r1 = new LockResource();
        var r2 = new LockResource();
        var caller = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        caller.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        b.CurrentExecutingThreadId = -1; // foreign thread — avoids the same-thread short-circuit
        sim.LockManager.Acquire(r1, LockMode.Exclusive, caller, 0);
        sim.LockManager.Acquire(r2, LockMode.Exclusive, b, 0);
        b.WaitingOnResource = r1;
        try
        {
            var ex = Throws<DbException>(() =>
                sim.LockManager.Acquire(r2, LockMode.Exclusive, caller, timeoutMillis: 10000));
            AreEqual("1205", ex.Data["HelpLink.EvtID"]);
            Contains($"Process ID {caller.Spid}", ex.Message);
        }
        finally
        {
            b.WaitingOnResource = null;
            sim.LockManager.Release(r2, LockMode.Exclusive, b);
            sim.LockManager.Release(r1, LockMode.Exclusive, caller);
        }
    }

    [TestMethod]
    public void TransactionScopedX_ReleasedAtCommit()
    {
        // Once the simulator runs a tx-scoped IX-then-row-X acquire (via
        // BatchContext.AcquireTransactionLock + AcquireRowLockTxScoped),
        // Commit releases every entry in the tx's HeldLocks list — both
        // the table-IX and every row-X.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran; insert t values (1)");
        IsNotEmpty(table.TableDataLock.Holders);
        ExecuteNonQuery(conn, "commit tran");
        IsEmpty(table.TableDataLock.Holders);
        foreach (var (_, resource) in table.RowLocks)
            IsEmpty(resource.Holders);
    }

    [TestMethod]
    public void TransactionScopedX_ReleasedAtRollback()
    {
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran; insert t values (1); rollback tran");
        IsEmpty(table.TableDataLock.Holders);
        foreach (var (_, resource) in table.RowLocks)
            IsEmpty(resource.Holders);
    }

    [TestMethod]
    public void ActiveDataWriters_TracksUncommittedRowX_ResetsAtCommit()
    {
        // The READ COMMITTED reader's lock-free fast path keys off this
        // per-table count: an uncommitted INSERT's row-X must lift it to 1,
        // and COMMIT must return it to 0 so subsequent readers skip the gate
        // again. A leak here is a silent throughput regression (readers stay
        // on the slow per-row probe), invisible to the behavioral suite.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        AreEqual(0, table.ActiveDataWriters);
        ExecuteNonQuery(conn, "begin tran; insert t values (1)");
        AreEqual(1, table.ActiveDataWriters);
        ExecuteNonQuery(conn, "commit tran");
        AreEqual(0, table.ActiveDataWriters);
    }

    [TestMethod]
    public void ActiveDataWriters_ResetsAtRollback()
    {
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran; insert t values (1)");
        AreEqual(1, table.ActiveDataWriters);
        ExecuteNonQuery(conn, "rollback tran");
        AreEqual(0, table.ActiveDataWriters);
    }

    [TestMethod]
    public void RangeModes_CompatibilityMatrix_MatchesTheProbedCells()
    {
        // RangeS-S coexists with a second reader's RangeS-S and with RangeS-U
        // (probed: a SERIALIZABLE reader and an overlapping UPDLOCK reader both
        // proceed). Everything else in the family conflicts, except two writers
        // probing the same interval with RangeI-N.
        IsTrue(LockManager.IsCompatible(LockMode.RangeSharedShared, LockMode.RangeSharedShared));
        IsTrue(LockManager.IsCompatible(LockMode.RangeSharedShared, LockMode.RangeSharedUpdate));
        IsTrue(LockManager.IsCompatible(LockMode.RangeSharedUpdate, LockMode.RangeSharedShared));
        IsTrue(LockManager.IsCompatible(LockMode.RangeInsertNull, LockMode.RangeInsertNull));

        IsFalse(LockManager.IsCompatible(LockMode.RangeSharedShared, LockMode.RangeInsertNull));
        IsFalse(LockManager.IsCompatible(LockMode.RangeInsertNull, LockMode.RangeSharedShared));
        IsFalse(LockManager.IsCompatible(LockMode.RangeSharedUpdate, LockMode.RangeSharedUpdate));
        IsFalse(LockManager.IsCompatible(LockMode.RangeSharedUpdate, LockMode.RangeInsertNull));
        IsFalse(LockManager.IsCompatible(LockMode.RangeExclusiveExclusive, LockMode.RangeSharedShared));
        IsFalse(LockManager.IsCompatible(LockMode.RangeSharedShared, LockMode.RangeExclusiveExclusive));
        IsFalse(LockManager.IsCompatible(LockMode.RangeExclusiveExclusive, LockMode.RangeExclusiveExclusive));
    }

    [TestMethod]
    public void RangeModes_DoNotDisturbTheRowAndTableFamilies()
    {
        // The range arms sit at the top of the matrix, so this pins that they
        // settle only range pairs — the eight-mode table below them is
        // unchanged.
        IsTrue(LockManager.IsCompatible(LockMode.SchemaStability, LockMode.Exclusive));
        IsTrue(LockManager.IsCompatible(LockMode.IntentShared, LockMode.IntentExclusive));
        IsTrue(LockManager.IsCompatible(LockMode.Shared, LockMode.Update));
        IsFalse(LockManager.IsCompatible(LockMode.Update, LockMode.Update));
        IsFalse(LockManager.IsCompatible(LockMode.Shared, LockMode.IntentExclusive));
        IsFalse(LockManager.IsCompatible(LockMode.Exclusive, LockMode.IntentShared));
    }

    [TestMethod]
    public void ActiveKeyRangeLocks_TracksAHeldRange_ResetsAtCommit()
    {
        // The writer's per-row range probe keys off this per-table count the
        // way the reader's fast path keys off ActiveDataWriters: at zero the
        // writer skips decoding its row and never touches the gate. A leak
        // here costs every writer a decode per mutation forever after.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key, v int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        AreEqual(0, table.ActiveKeyRangeLocks);
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select count(*) from t where k between 1 and 5");
        AreEqual(1, table.ActiveKeyRangeLocks);
        ExecuteNonQuery(conn, "commit tran");
        AreEqual(0, table.ActiveKeyRangeLocks);
        IsEmpty(table.KeyRangeLocks.Values.SelectMany(static r => r.Holders));
    }

    [TestMethod]
    public void ActiveKeyRangeLocks_StaysZero_WhenTheReaderFallsBackToTheTableLock()
    {
        // No index leads `v`, so there is no key space to fence and the reader
        // takes the whole-table S instead — no range resource is interned.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key, v int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select count(*) from t where v = 3");
        AreEqual(0, table.ActiveKeyRangeLocks);
        IsEmpty(table.KeyRangeLocks);
        Contains(LockMode.Shared, table.TableDataLock.Holders.Select(static h => h.Mode));
        ExecuteNonQuery(conn, "rollback tran");
    }

    [TestMethod]
    public void SerializableUpdLockRead_TakesRangeSU_AndKeepsItsRowU()
    {
        // Real folds the two into one key lock; range modes live on resources
        // of their own here, so the row-U stays on top of the RangeS-U — which
        // is what keeps blocking the readers and writers that take a row lock
        // without ever probing a range.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key, v int); insert t values (2, 20)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 1 and 5");

        AreEqual(1, table.ActiveKeyRangeLocks);
        Contains(
            LockMode.RangeSharedUpdate,
            table.KeyRangeLocks.Values.SelectMany(static r => r.Holders).Select(static h => h.Mode));
        Contains(LockMode.Update, table.RowLocks.Values.SelectMany(static r => r.Holders).Select(static h => h.Mode));
        Contains(LockMode.IntentExclusive, table.TableDataLock.Holders.Select(static h => h.Mode));
        DoesNotContain(LockMode.Shared, table.TableDataLock.Holders.Select(static h => h.Mode));

        ExecuteNonQuery(conn, "rollback tran");
        AreEqual(0, table.ActiveKeyRangeLocks);
    }

    [TestMethod]
    public void KeyRange_Contains_HonorsBoundInclusivityAndRejectsNull()
    {
        var open = SingleColumn(
            hasLower: true, SqlValue.FromInt32(10), lowerInclusive: false,
            hasUpper: true, SqlValue.FromInt32(20), upperInclusive: false);
        IsFalse(Covers(open, SqlValue.FromInt32(10)));
        IsTrue(Covers(open, SqlValue.FromInt32(15)));
        IsFalse(Covers(open, SqlValue.FromInt32(20)));
        IsFalse(Covers(open, SqlValue.Null(SqlType.Int32)));

        var closed = SingleColumn(
            hasLower: true, SqlValue.FromInt32(10), lowerInclusive: true,
            hasUpper: true, SqlValue.FromInt32(20), upperInclusive: true);
        IsTrue(Covers(closed, SqlValue.FromInt32(10)));
        IsTrue(Covers(closed, SqlValue.FromInt32(20)));
        IsFalse(Covers(closed, SqlValue.FromInt32(21)));

        // An open-ended upper is the infinity range past the last key.
        var tail = SingleColumn(
            hasLower: true, SqlValue.FromInt32(10), lowerInclusive: false,
            hasUpper: false, default, upperInclusive: false);
        IsTrue(Covers(tail, SqlValue.FromInt32(int.MaxValue)));
        IsFalse(Covers(tail, SqlValue.FromInt32(10)));
    }

    [TestMethod]
    public void KeyRange_Contains_ComparesTheTupleLexicographically()
    {
        // `a = 1 AND b between 2 and 5` over a key on (a, b): the interval runs
        // from (1,2) to (1,5), so a second-column value inside the interval but
        // under a different leading value is outside it.
        var closed = Tuple(
            [SqlValue.FromInt32(1), SqlValue.FromInt32(2)], lowerInclusive: true,
            [SqlValue.FromInt32(1), SqlValue.FromInt32(5)], upperInclusive: true);
        IsTrue(Covers(closed, SqlValue.FromInt32(1), SqlValue.FromInt32(3)));
        IsTrue(Covers(closed, SqlValue.FromInt32(1), SqlValue.FromInt32(2)));
        IsFalse(Covers(closed, SqlValue.FromInt32(1), SqlValue.FromInt32(6)));
        IsFalse(Covers(closed, SqlValue.FromInt32(2), SqlValue.FromInt32(3)));
        IsFalse(Covers(closed, SqlValue.FromInt32(0), SqlValue.FromInt32(3)));
        IsFalse(Covers(closed, SqlValue.Null(SqlType.Int32), SqlValue.FromInt32(3)));
        AreEqual("0,1:[(1,2),(1,5)]", closed.ToString());
    }

    [TestMethod]
    public void KeyRange_Contains_TreatsAShorterBoundTupleAsOpenBelowIt()
    {
        // `a = 1 AND b > 2`: the lower bound names both columns, the upper only
        // the first, so every b above 2 under a = 1 is inside and no other a is.
        var halfOpen = Tuple(
            [SqlValue.FromInt32(1), SqlValue.FromInt32(2)], lowerInclusive: false,
            [SqlValue.FromInt32(1)], upperInclusive: true);
        IsTrue(Covers(halfOpen, SqlValue.FromInt32(1), SqlValue.FromInt32(int.MaxValue)));
        IsFalse(Covers(halfOpen, SqlValue.FromInt32(1), SqlValue.FromInt32(2)));
        IsFalse(Covers(halfOpen, SqlValue.FromInt32(2), SqlValue.FromInt32(3)));
        AreEqual("0,1:((1,2),(1,*)]", halfOpen.ToString());
    }

    [TestMethod]
    public void KeyRange_InternsByIntervalNotByType()
    {
        // Two ranges whose bounds compare equal are one resource, so the same
        // predicate parsed twice reuses the interned LockResource instead of
        // minting one per parse.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        var a = new KeyRange([0], [SqlType.Int32], [SqlValue.FromInt32(1)], true, [SqlValue.FromInt32(5)], true);
        var b = new KeyRange([0], [SqlType.BigInt], [SqlValue.FromInt32(1)], true, [SqlValue.FromInt32(5)], true);
        AreSame(table.GetOrCreateKeyRangeLock(a), table.GetOrCreateKeyRangeLock(b));
        AreEqual("0:[1,5]", a.ToString());
    }

    private static KeyRange SingleColumn(
        bool hasLower, SqlValue lower, bool lowerInclusive, bool hasUpper, SqlValue upper, bool upperInclusive) =>
        new([0], [SqlType.Int32], hasLower ? [lower] : [], lowerInclusive, hasUpper ? [upper] : [], upperInclusive);

    private static KeyRange Tuple(SqlValue[] lower, bool lowerInclusive, SqlValue[] upper, bool upperInclusive) =>
        new([0, 1], [SqlType.Int32, SqlType.Int32], lower, lowerInclusive, upper, upperInclusive);

    private static bool Covers(KeyRange range, params SqlValue[] probe) => range.Contains(probe);

    private static void ExecuteNonQuery(Simulation sim, string sql)
    {
        using var conn = sim.CreateDbConnection();
        conn.Open();
        ExecuteNonQuery(conn, sql);
    }

    private static void ExecuteNonQuery(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void Spid_FirstUserConnection_Is51()
    {
        var sim = new Simulation();
        var conn1 = sim.CreateDbConnection();
        var conn2 = sim.CreateDbConnection();
        AreEqual(51, conn1.Spid);
        AreEqual(52, conn2.Spid);
    }

    [TestMethod]
    public void LockTimeoutMillis_DefaultIsMinusOne()
    {
        var sim = new Simulation();
        var conn = sim.CreateDbConnection();
        AreEqual(-1, conn.LockTimeoutMillis);
    }

    [TestMethod]
    public void SetLockTimeout_UpdatesConnectionState()
    {
        var sim = new Simulation();
        using var conn = sim.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "set lock_timeout 5000";
        _ = cmd.ExecuteNonQuery();
        AreEqual(5000, conn.LockTimeoutMillis);
    }

    [TestMethod]
    public void SetLockTimeout_Zero_UpdatesConnectionState()
    {
        var sim = new Simulation();
        using var conn = sim.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "set lock_timeout 0";
        _ = cmd.ExecuteNonQuery();
        AreEqual(0, conn.LockTimeoutMillis);
    }

    [TestMethod]
    public void U_CompatibleWith_S_IS()
    {
        // U × S: compatible (read-with-intent-to-upgrade coexists with
        // plain readers). U × IS: compatible (the IS holder might be
        // a different child of the same parent).
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.Shared, a, 0);
        sim.LockManager.Acquire(resource, LockMode.Update, b, 0);
        sim.LockManager.Release(resource, LockMode.Update, b);
        sim.LockManager.Release(resource, LockMode.Shared, a);
        sim.LockManager.Acquire(resource, LockMode.IntentShared, a, 0);
        sim.LockManager.Acquire(resource, LockMode.Update, b, 0);
        sim.LockManager.Release(resource, LockMode.Update, b);
        sim.LockManager.Release(resource, LockMode.IntentShared, a);
    }

    [TestMethod]
    public void U_ConflictsWith_U_X_IX_SIX()
    {
        var sim = new Simulation();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        foreach (var conflict in new[] { LockMode.Update, LockMode.Exclusive, LockMode.IntentExclusive, LockMode.SharedIntentExclusive })
        {
            var resource = new LockResource();
            sim.LockManager.Acquire(resource, LockMode.Update, a, 0);
            _ = Throws<DbException>(() => sim.LockManager.Acquire(resource, conflict, b, 0));
            sim.LockManager.Release(resource, LockMode.Update, a);
        }
    }

    [TestMethod]
    public void IS_CompatibleWithEverythingExceptX()
    {
        var sim = new Simulation();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        foreach (var ok in new[] { LockMode.IntentShared, LockMode.IntentExclusive, LockMode.SharedIntentExclusive, LockMode.Shared, LockMode.Update })
        {
            var resource = new LockResource();
            sim.LockManager.Acquire(resource, LockMode.IntentShared, a, 0);
            sim.LockManager.Acquire(resource, ok, b, 0);
            sim.LockManager.Release(resource, ok, b);
            sim.LockManager.Release(resource, LockMode.IntentShared, a);
        }
        // IS × X: conflict.
        var rx = new LockResource();
        sim.LockManager.Acquire(rx, LockMode.IntentShared, a, 0);
        _ = Throws<DbException>(() => sim.LockManager.Acquire(rx, LockMode.Exclusive, b, 0));
        sim.LockManager.Release(rx, LockMode.IntentShared, a);
    }

    [TestMethod]
    public void IX_ConflictsWith_S_U_SIX_X()
    {
        var sim = new Simulation();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        foreach (var conflict in new[] { LockMode.Shared, LockMode.Update, LockMode.SharedIntentExclusive, LockMode.Exclusive })
        {
            var resource = new LockResource();
            sim.LockManager.Acquire(resource, LockMode.IntentExclusive, a, 0);
            _ = Throws<DbException>(() => sim.LockManager.Acquire(resource, conflict, b, 0));
            sim.LockManager.Release(resource, LockMode.IntentExclusive, a);
        }
    }

    [TestMethod]
    public void SIX_OnlyCompatibleWith_IS()
    {
        var sim = new Simulation();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        // SIX × IS: compatible.
        var r1 = new LockResource();
        sim.LockManager.Acquire(r1, LockMode.SharedIntentExclusive, a, 0);
        sim.LockManager.Acquire(r1, LockMode.IntentShared, b, 0);
        sim.LockManager.Release(r1, LockMode.IntentShared, b);
        sim.LockManager.Release(r1, LockMode.SharedIntentExclusive, a);
        // SIX × everything else: conflict.
        foreach (var conflict in new[] { LockMode.IntentExclusive, LockMode.SharedIntentExclusive, LockMode.Shared, LockMode.Update, LockMode.Exclusive })
        {
            var resource = new LockResource();
            sim.LockManager.Acquire(resource, LockMode.SharedIntentExclusive, a, 0);
            _ = Throws<DbException>(() => sim.LockManager.Acquire(resource, conflict, b, 0));
            sim.LockManager.Release(resource, LockMode.SharedIntentExclusive, a);
        }
    }

    [TestMethod]
    public void RowLockEscalation_Above5000_PromotesToTableX()
    {
        // Insert >5000 rows in one transaction; the per-row X acquisitions
        // bump the per-tx per-table count past the threshold, triggering
        // promotion to a single table-X. After commit, the table-X is
        // released and the per-row dict entries are no longer tracked
        // against any tx.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran");
        for (var i = 0; i < SimulatedDbTransaction.RowLockEscalationThreshold + 2; i++)
            ExecuteNonQuery(conn, $"insert t values ({i})");
        // After escalation, the active transaction holds table-X on this
        // table; row-lock count is zeroed.
        var tx = conn.CurrentTransaction;
        IsNotNull(tx);
        Contains(table, tx.EscalatedTables);
        ExecuteNonQuery(conn, "commit tran");
        IsEmpty(table.TableDataLock.Holders);
    }
}
