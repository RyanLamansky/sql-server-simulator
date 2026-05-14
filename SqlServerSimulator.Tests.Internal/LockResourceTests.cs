using System.Data.Common;
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
        var conn = (SimulatedDbConnection)sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, conn, timeoutMillis: 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, conn);
    }

    [TestMethod]
    public void SchS_TwoDifferentOwners_BothGrant()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a, 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a);
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var holder = (SimulatedDbConnection)sim.CreateDbConnection();
        var waiter = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var a = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        var caller = (SimulatedDbConnection)sim.CreateDbConnection();
        var b = (SimulatedDbConnection)sim.CreateDbConnection();
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
        // Once the simulator runs a tx-scoped X acquire (via
        // BatchContext.AcquireTransactionLock), Commit releases every
        // entry in the tx's HeldLocks list.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var schemaLock = ((SimulatedDbConnection)conn).CurrentDatabase.Schemas["dbo"].HeapTables["t"].SchemaLock;
        ExecuteNonQuery(conn, "begin tran; insert t values (1)");
        IsNotEmpty(schemaLock.Holders);
        ExecuteNonQuery(conn, "commit tran");
        IsEmpty(schemaLock.Holders);
    }

    [TestMethod]
    public void TransactionScopedX_ReleasedAtRollback()
    {
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var schemaLock = ((SimulatedDbConnection)conn).CurrentDatabase.Schemas["dbo"].HeapTables["t"].SchemaLock;
        ExecuteNonQuery(conn, "begin tran; insert t values (1); rollback tran");
        IsEmpty(schemaLock.Holders);
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
        var conn1 = (SimulatedDbConnection)sim.CreateDbConnection();
        var conn2 = (SimulatedDbConnection)sim.CreateDbConnection();
        AreEqual(51, conn1.Spid);
        AreEqual(52, conn2.Spid);
    }

    [TestMethod]
    public void LockTimeoutMillis_DefaultIsMinusOne()
    {
        var sim = new Simulation();
        var conn = (SimulatedDbConnection)sim.CreateDbConnection();
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
        AreEqual(5000, ((SimulatedDbConnection)conn).LockTimeoutMillis);
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
        AreEqual(0, ((SimulatedDbConnection)conn).LockTimeoutMillis);
    }
}
