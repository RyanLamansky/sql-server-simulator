using System.Data.Common;
using System.Runtime.CompilerServices;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Reclamation of a session whose <see cref="SimulatedDbConnection"/> was
/// abandoned without a <c>Dispose</c>. Each kind of state a session can leave
/// behind is exercised separately — an open transaction's locks and writes,
/// the MVCC version-store pin, <c>##global</c> temp tables, session
/// application locks, and the session's own registry row — plus the
/// mid-statement immunity rule, the <c>sp_who</c> reporting window, and the
/// weak-reference plumbing that makes the whole thing possible.
/// </summary>
/// <remarks>
/// Most tests drive the sweep directly (enqueue, then drain) so they don't
/// depend on when the GC decides to finalize; <see cref="GarbageCollected_Connection_IsEnqueuedAndReclaimed"/>
/// is the one that goes through the real finalizer, and is what pins the claim
/// that nothing global still holds the connection alive.
/// </remarks>
[TestClass]
public sealed class AbandonedSessionReclamationTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens a connection, runs <paramref name="sql"/> on it, and abandons it —
    /// no Dispose, no returned reference. The session lands in the
    /// abandoned-session queue exactly as the finalizer would leave it, without
    /// waiting on the GC.
    /// </summary>
    private static SessionToken Abandon(Simulation simulation, string sql)
    {
#pragma warning disable CA2000 // Abandoning the connection undisposed is the scenario under test.
        var connection = simulation.CreateDbConnection();
#pragma warning restore CA2000
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            _ = command.ExecuteNonQuery();
        }
        var session = connection.Session;
        simulation.EnqueueAbandonedSession(connection);
        return session;
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Exec(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    [TestMethod]
    public void OpenTransaction_RollsBackAndReleasesItsLocks()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();
        Exec(observer, "CREATE TABLE dbo.t (id int PRIMARY KEY, v int); INSERT dbo.t VALUES (1, 1), (2, 2)");

        _ = Abandon(simulation, "BEGIN TRANSACTION; UPDATE dbo.t SET v = 99 WHERE id = 1; INSERT dbo.t VALUES (3, 3)");
        AreNotEqual(0, Convert.ToInt32(Scalar(observer, "SELECT COUNT(*) FROM sys.dm_tran_locks"), null));

        AreEqual(1, simulation.ReclaimAbandonedSessions());

        // The transaction rolled back through its own machinery: the update is
        // undone, the insert is gone, and every lock it held is released.
        AreEqual(1, Convert.ToInt32(Scalar(observer, "SELECT v FROM dbo.t WHERE id = 1"), null));
        AreEqual(2, Convert.ToInt32(Scalar(observer, "SELECT COUNT(*) FROM dbo.t"), null));
        AreEqual(0, Convert.ToInt32(Scalar(observer, "SELECT COUNT(*) FROM sys.dm_tran_locks"), null));
    }

    [TestMethod]
    public void BlockedReader_ProceedsOnceTheLeakedSessionIsReclaimed()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();
        Exec(observer, "CREATE TABLE dbo.t (id int PRIMARY KEY, v int); INSERT dbo.t VALUES (1, 1)");
        _ = Abandon(simulation, "BEGIN TRANSACTION; UPDATE dbo.t SET v = 99 WHERE id = 1");

        // The read acquires a lock, and the acquisition path sweeps first — so
        // the leaked session's X lock is gone by the time compatibility is
        // tested and the reader never blocks. Fail fast rather than wait, so a
        // regression here is a Msg 1222 rather than a hung test.
        Exec(observer, "SET LOCK_TIMEOUT 0");
        AreEqual(1, Convert.ToInt32(Scalar(observer, "SELECT v FROM dbo.t WHERE id = 1"), null));
    }

    [TestMethod]
    public void SnapshotTransaction_StopsPinningTheVersionStore()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();
        Exec(observer, "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON");
        Exec(observer, "CREATE TABLE dbo.t (id int PRIMARY KEY, v int); INSERT dbo.t VALUES (1, 1)");

        _ = Abandon(
            simulation,
            "SET TRANSACTION ISOLATION LEVEL SNAPSHOT; BEGIN TRANSACTION; SELECT v FROM dbo.t WHERE id = 1");
        HasCount(1, simulation.ActiveSnapshotTxs);

        AreEqual(1, simulation.ReclaimAbandonedSessions());
        IsEmpty(simulation.ActiveSnapshotTxs);
    }

    [TestMethod]
    public void GlobalTempTable_IsDroppedWithItsOwningSession()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();

        _ = Abandon(simulation, "CREATE TABLE ##leaked (id int)");
        IsNotNull(Scalar(observer, "SELECT OBJECT_ID('tempdb..##leaked')"));

        AreEqual(1, simulation.ReclaimAbandonedSessions());
        AreEqual(DBNull.Value, Scalar(observer, "SELECT OBJECT_ID('tempdb..##leaked')"));
    }

    [TestMethod]
    public void SessionApplicationLock_IsReleased()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();

        _ = Abandon(
            simulation,
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'leaked', @LockMode = 'Exclusive', @LockOwner = 'Session'");
        // APPLOCK_MODE answers for the *calling* session, so the observer asks
        // the cross-session question instead: could it take the lock?
        AreEqual(0, Convert.ToInt32(Scalar(observer, "SELECT APPLOCK_TEST('public', 'leaked', 'Exclusive', 'Session')"), null));

        AreEqual(1, simulation.ReclaimAbandonedSessions());
        AreEqual(1, Convert.ToInt32(Scalar(observer, "SELECT APPLOCK_TEST('public', 'leaked', 'Exclusive', 'Session')"), null));
    }

    [TestMethod]
    public void ReclaimedSession_RetiresFromTheSessionRegistry()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();

        var session = Abandon(simulation, "SELECT 1");
        lock (simulation.Sessions)
            Contains(session, simulation.Sessions);

        AreEqual(1, simulation.ReclaimAbandonedSessions());

        lock (simulation.Sessions)
            DoesNotContain(session, simulation.Sessions);
        IsTrue(session.Reclaimed);
    }

    [TestMethod]
    public void SpWho_ReportsTheLeakedSessionUntilItIsReclaimed()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();

        var session = Abandon(simulation, "BEGIN TRANSACTION; SELECT 1");

        // Still queued: the session holds state, so it must still be reportable
        // — a row that vanished before its locks did would be a lie.
        Contains(session.Spid, SpidsFromSpWho(observer));
        AreEqual(1, simulation.ReclaimAbandonedSessions());
        DoesNotContain(session.Spid, SpidsFromSpWho(observer));
    }

    private static List<int> SpidsFromSpWho(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_who";
        using var reader = command.ExecuteReader();
        var spids = new List<int>();
        while (reader.Read())
            spids.Add(Convert.ToInt32(reader["spid"], null));
        return spids;
    }

    [TestMethod]
    public void MidStatementSession_IsSkippedAndRequeued()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();

        var session = Abandon(simulation, "BEGIN TRANSACTION; SELECT 1");
        // Stand in for a statement in flight on this session's own thread. The
        // sweep reads exactly this marker, which a parallel-aggregate worker
        // thread never writes — only the dispatcher on the session's thread
        // does — so a fan-out mid-statement is covered by the same check.
        session.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;

        AreEqual(0, simulation.ReclaimAbandonedSessions());
        IsFalse(session.Reclaimed);
        AreEqual(1, Convert.ToInt32(Scalar(observer, "SELECT @@TRANCOUNT + 1"), null));

        // Statement over — the next sweep takes it.
        session.CurrentExecutingThreadId = null;
        AreEqual(1, simulation.ReclaimAbandonedSessions());
        IsTrue(session.Reclaimed);
    }

    [TestMethod]
    public void DisposedConnection_IsNeverEnqueued()
    {
        var simulation = new Simulation();
        using (var connection = simulation.CreateDbConnection())
        {
            connection.Open();
            Exec(connection, "SELECT 1");
        }
        // Dispose suppresses finalization, so a well-behaved consumer never
        // pays for the queue at all.
        AreEqual(0, simulation.ReclaimAbandonedSessions());
        AreEqual(0, simulation.SessionsReclaimed);
    }

    [TestMethod]
    public void ReclaimingTwice_IsANoOp()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();
        Exec(observer, "CREATE TABLE dbo.t (id int PRIMARY KEY, v int); INSERT dbo.t VALUES (1, 1)");

        var session = Abandon(simulation, "BEGIN TRANSACTION; UPDATE dbo.t SET v = 99 WHERE id = 1");
        AreEqual(1, simulation.ReclaimAbandonedSessions());
        IsTrue(session.Reclaimed);

        // A late finalizer on an already-reclaimed session must not re-run the
        // teardown (a second lock release would be an unmatched one).
        AreEqual(0, simulation.ReclaimAbandonedSessions());
        AreEqual(0, Convert.ToInt32(Scalar(observer, "SELECT COUNT(*) FROM sys.dm_tran_locks"), null));
    }

    [TestMethod]
    public void GarbageCollected_Connection_IsEnqueuedAndReclaimed()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();
        Exec(observer, "CREATE TABLE dbo.t (id int PRIMARY KEY, v int); INSERT dbo.t VALUES (1, 1)");

        var session = LeakConnection(simulation);
        AreNotEqual(0, Convert.ToInt32(Scalar(observer, "SELECT COUNT(*) FROM sys.dm_tran_locks"), null));

        // The whole point of the SessionToken indirection: nothing global holds
        // the connection any more, so the GC can finalize it. Before it, the
        // lock hold, the ##temp entry and the snapshot registration each pinned
        // it for the life of the process and this collection found nothing.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AreEqual(1, simulation.ReclaimAbandonedSessions());
        IsTrue(session.Reclaimed);
        AreEqual(1, Convert.ToInt32(Scalar(observer, "SELECT v FROM dbo.t WHERE id = 1"), null));
        AreEqual(0, Convert.ToInt32(Scalar(observer, "SELECT COUNT(*) FROM sys.dm_tran_locks"), null));
        AreEqual(DBNull.Value, Scalar(observer, "SELECT OBJECT_ID('tempdb..##gcleaked')"));
    }

    /// <summary>
    /// Opens a connection, leaves state behind on it, and returns only its
    /// token — the connection itself is unreachable on return. Not inlined, so
    /// the JIT can't keep the local alive in a caller frame the collection
    /// below would then see.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SessionToken LeakConnection(Simulation simulation)
    {
#pragma warning disable CA2000 // Abandoning the connection undisposed is the scenario under test.
        var connection = simulation.CreateDbConnection();
#pragma warning restore CA2000
        connection.Open();
        Exec(connection, "CREATE TABLE ##gcleaked (id int); BEGIN TRANSACTION; UPDATE dbo.t SET v = 99 WHERE id = 1");
        return connection.Session;
    }
}
