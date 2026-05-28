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
