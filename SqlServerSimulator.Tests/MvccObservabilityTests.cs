using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Phase-3 MVCC observability surface: <c>sys.dm_tran_version_store</c>,
/// <c>sys.dm_tran_version_store_space_usage</c>, and
/// <c>sys.dm_tran_active_snapshot_database_transactions</c>, plus the
/// version-store garbage-collector that runs at every Commit / Rollback /
/// Dispose of an explicit transaction. Each DMV's column shape was
/// probe-confirmed against SQL Server 2025 (2026-05-14) after
/// <c>GRANT VIEW SERVER STATE</c>; the simulator surfaces matching column
/// names + ordering + types so existing diagnostic queries port unchanged.
/// </summary>
[TestClass]
public sealed class MvccObservabilityTests
{
    private static Simulation NewWithVersioning()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set allow_snapshot_isolation on");
        _ = sim.ExecuteNonQuery("alter database simulated set read_committed_snapshot on");
        return sim;
    }

    [TestMethod]
    public void VersionStore_NoVersioning_ReturnsEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t set v = 200 where id = 1;
            select count(*) from sys.dm_tran_version_store
            """));

    [TestMethod]
    public void VersionStore_PopulatedAfterUpdateUnderSi_WhileReaderHoldsSnapshot()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300)
            """);
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("update t set v = v + 1").ExecuteNonQuery();

        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void VersionStore_AfterAllReadersCommit_GcEmptiesStore()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300)
            """);
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update t set v = v + 1").ExecuteNonQuery();

        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        // After the reader's SI tx commits there are no active snapshots
        // anchoring HVs <= the writer's commit Xid; the GC at commit time
        // collapses the store.
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
    }

    [TestMethod]
    public void VersionStore_DeleteUnderSi_AppearsInVersionStore()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200)
            """);
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("delete from t where id = 1").ExecuteNonQuery();

        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void VersionStore_TransactionSequenceNumIsCommitXid()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100)
            """);
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update t set v = 200").ExecuteNonQuery();

        // The HV's transaction_sequence_num is the writer's commit Xid.
        // We can't predict the absolute value but it should be > 0 (the
        // implicit Xid 0 marks pre-versioning state).
        var seq = (long)sim.ExecuteScalar("select transaction_sequence_num from sys.dm_tran_version_store")!;
        IsGreaterThan(0L, seq);
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void SpaceUsage_AlwaysYieldsOneRow_PerDatabase()
    {
        var sim = NewWithVersioning();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store_space_usage"));
    }

    [TestMethod]
    public void SpaceUsage_ReservedKbScalesWithStoreSize()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300)
            """);
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();
        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update t set v = v + 1").ExecuteNonQuery();

        var kb = (long)sim.ExecuteScalar("select reserved_space_kb from sys.dm_tran_version_store_space_usage")!;
        IsGreaterThan(0L, kb);
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void ActiveSnapshotDbTx_ListsHoldingSiSession()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("create table t (id int primary key); insert t values (1)");
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_tran_active_snapshot_database_transactions where is_snapshot = 1"));
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_active_snapshot_database_transactions"));
    }

    [TestMethod]
    public void ActiveSnapshotDbTx_SessionIdMatchesSpid()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("create table t (id int primary key); insert t values (1)");
        using var reader = sim.CreateOpenConnection();
        var spid = (short)reader.CreateCommand("select @@spid").ExecuteScalar()!;
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        AreEqual(spid, (int)sim.ExecuteScalar($"select session_id from sys.dm_tran_active_snapshot_database_transactions where session_id = {spid}")!);
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void ActiveSnapshotDbTx_RcsiStatementSnapshot_NotTracked()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("create table t (id int primary key); insert t values (1)");
        using var reader = sim.CreateOpenConnection();
        // Default RC + RCSI; the per-statement snapshot allocated by the
        // SELECT is ephemeral and shouldn't appear in the DMV after the
        // statement returns.
        AreEqual(1, reader.CreateCommand("select count(*) from t").ExecuteScalar());
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_active_snapshot_database_transactions"));
    }

    [TestMethod]
    public void Gc_DoesNotDropHvsStillNeededByOlderSnapshot()
    {
        var sim = NewWithVersioning();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100)
            """);
        using var olderReader = sim.CreateOpenConnection();
        _ = olderReader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        // First writer mutation
        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("update t set v = 200").ExecuteNonQuery();

        // A newer SI tx commits without ever needing the HV
        using (var newerReader = sim.CreateOpenConnection())
        {
            _ = newerReader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();
            _ = newerReader.CreateCommand("commit").ExecuteNonQuery();
        }

        // GC at the newer reader's commit must not drop the HV — older
        // reader still needs it.
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
        _ = olderReader.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
    }
}
