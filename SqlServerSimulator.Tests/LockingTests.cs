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
