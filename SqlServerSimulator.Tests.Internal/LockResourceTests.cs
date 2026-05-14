using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Direct exercises of the phase-0 <see cref="LockResource"/> primitive:
/// per-owner re-entrant counting, Sch-S × Sch-S compatibility,
/// Sch-M-blocks-everything, cross-thread blocking wait, LOCK_TIMEOUT path
/// (Msg 1222), and same-thread-deadlock short-circuit (Msg 1205). These
/// live in the Internal test project because <see cref="LockResource"/>
/// is internal — the user-facing surface for phase 0 is just the SET /
/// connection-state plumbing, and end-to-end blocking tests need X locks
/// (phase 1a) to observe holds that span statements.
/// </summary>
[TestClass]
public sealed class LockResourceTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SimulatedDbConnection NewConnection()
    {
        var sim = new Simulation();
        return (SimulatedDbConnection)sim.CreateDbConnection();
    }

    [TestMethod]
    public void SchS_OnEmptyResource_GrantsImmediately()
    {
        var resource = new LockResource();
        var conn = NewConnection();
        resource.Acquire(LockMode.SchemaStability, conn, timeoutMillis: 0);
        // No throw = grant. Release to leave the resource clean.
        resource.Release(LockMode.SchemaStability, conn);
    }

    [TestMethod]
    public void SchS_TwoDifferentOwners_BothGrant()
    {
        var resource = new LockResource();
        var a = NewConnection();
        var b = NewConnection();
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0);
        resource.Acquire(LockMode.SchemaStability, b, timeoutMillis: 0);
        resource.Release(LockMode.SchemaStability, b);
        resource.Release(LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void SchM_BlocksConflictingSchS_TimeoutZeroRaises1222()
    {
        var resource = new LockResource();
        var a = NewConnection();
        var b = NewConnection();
        // a runs synchronously on this thread but won't be "executing" once
        // we clear it — simulating a holder whose statement has parked.
        a.CurrentExecutingThreadId = -1; // not this thread; cross-thread blocker
        resource.Acquire(LockMode.SchemaModification, a, timeoutMillis: 0);
        var ex = Throws<DbException>(() =>
            resource.Acquire(LockMode.SchemaStability, b, timeoutMillis: 0));
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
        AreEqual("Lock request time out period exceeded.", ex.Message);
        resource.Release(LockMode.SchemaModification, a);
    }

    [TestMethod]
    public void SchS_BlocksConflictingSchM_TimeoutZeroRaises1222()
    {
        var resource = new LockResource();
        var a = NewConnection();
        var b = NewConnection();
        a.CurrentExecutingThreadId = -1; // not this thread
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0);
        var ex = Throws<DbException>(() =>
            resource.Acquire(LockMode.SchemaModification, b, timeoutMillis: 0));
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
        resource.Release(LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void SchS_BlocksConflictingSchM_PositiveTimeoutEventuallyRaises1222()
    {
        var resource = new LockResource();
        var a = NewConnection();
        var b = NewConnection();
        a.CurrentExecutingThreadId = -1;
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0);
        var started = Environment.TickCount64;
        var ex = Throws<DbException>(() =>
            resource.Acquire(LockMode.SchemaModification, b, timeoutMillis: 100));
        var elapsed = Environment.TickCount64 - started;
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);
        IsGreaterThanOrEqualTo(80L, elapsed);
        resource.Release(LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void Reentrance_SameOwnerSameMode_BumpsCount()
    {
        var resource = new LockResource();
        var a = NewConnection();
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0);
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0); // re-entry
        resource.Release(LockMode.SchemaStability, a); // first release just decrements
        // Lock is still held; another owner's Sch-M would conflict.
        var b = NewConnection();
        a.CurrentExecutingThreadId = -1;
        _ = Throws<DbException>(() => resource.Acquire(LockMode.SchemaModification, b, 0));
        resource.Release(LockMode.SchemaStability, a); // final release
        // Now b can acquire.
        resource.Acquire(LockMode.SchemaModification, b, timeoutMillis: 0);
        resource.Release(LockMode.SchemaModification, b);
    }

    [TestMethod]
    public void SameOwner_HoldsSchS_ThenAcquiresSchM_Grants()
    {
        // A single owner that already holds Sch-S can also acquire Sch-M —
        // there are no other holders to conflict with. This is the path the
        // ALTER dispatcher takes (Sch-S via TryResolveTable, then Sch-M
        // explicitly). Both holds release independently.
        var resource = new LockResource();
        var a = NewConnection();
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0);
        resource.Acquire(LockMode.SchemaModification, a, timeoutMillis: 0);
        resource.Release(LockMode.SchemaModification, a);
        resource.Release(LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void CrossThread_SchSDrainsAfterRelease_QueuedSchMSucceeds()
    {
        var resource = new LockResource();
        var holder = NewConnection();
        var waiter = NewConnection();
        // Holder takes Sch-S on a different thread (simulated via thread id).
        var holderThread = new Thread(() =>
        {
            holder.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
            resource.Acquire(LockMode.SchemaStability, holder, timeoutMillis: 0);
            Thread.Sleep(100);
            resource.Release(LockMode.SchemaStability, holder);
            holder.CurrentExecutingThreadId = null;
        });
        holderThread.Start();
        // Give the holder thread time to acquire before the waiter starts.
        Thread.Sleep(20);
        // Waiter requests Sch-M with a generous timeout. Should wait for the
        // holder to release then succeed.
        var started = Environment.TickCount64;
        resource.Acquire(LockMode.SchemaModification, waiter, timeoutMillis: 1000);
        var elapsed = Environment.TickCount64 - started;
        resource.Release(LockMode.SchemaModification, waiter);
        holderThread.Join();
        // Elapsed should be roughly the remaining sleep time on the holder
        // (~80ms after the 20ms head start). Generous bounds for CI noise.
        IsGreaterThanOrEqualTo(20L, elapsed);
    }

    [TestMethod]
    public void SameThreadConflict_RaisesMsg1205Immediately()
    {
        // A conflicting holder whose CurrentExecutingThreadId equals the
        // caller's thread means the holder can't release until the caller
        // does — no progress is possible. The simulator raises Msg 1205
        // immediately rather than letting the wait hang (or rely on the
        // timeout).
        var resource = new LockResource();
        var a = NewConnection();
        var b = NewConnection();
        a.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        resource.Acquire(LockMode.SchemaStability, a, timeoutMillis: 0);
        var ex = Throws<DbException>(() =>
            resource.Acquire(LockMode.SchemaModification, b, timeoutMillis: 10000));
        AreEqual("1205", ex.Data["HelpLink.EvtID"]);
        Contains($"Process ID {b.Spid}", ex.Message);
        Contains("deadlocked on lock resources", ex.Message);
        resource.Release(LockMode.SchemaStability, a);
    }

    [TestMethod]
    public void Spid_FirstUserConnection_Is51()
    {
        // SQL Server's convention reserves SPIDs 1-50 for system/internal
        // use and starts user sessions at 51. The simulator matches.
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
