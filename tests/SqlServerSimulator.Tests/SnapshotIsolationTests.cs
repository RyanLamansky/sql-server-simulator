using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Phase 3 — SNAPSHOT isolation + READ_COMMITTED_SNAPSHOT (MVCC) tests.
/// Covers <c>ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION/READ_COMMITTED_SNAPSHOT</c>
/// state flips, Msg 3952 rejection when SI is used before ALLOW_SNAPSHOT_ISOLATION
/// is turned on, the per-tx snapshot acquired at first user-table read,
/// reader visibility against committed prior versions, Msg 3960
/// update-conflict detection (with auto-rollback semantic), and RCSI's
/// per-statement snapshot for default-RC reads. Each probe-confirmed
/// wording assertion pins the verbatim text matched against the live
/// SQL Server 2025 reference (2026-05-14).
/// </summary>
[TestClass]
public sealed class SnapshotIsolationTests
{
    [TestMethod]
    public void AlterDatabase_SetAllowSnapshotIsolationOn_AcceptsBareName()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            alter database simulated set allow_snapshot_isolation on;
            select 1
            """));

    [TestMethod]
    public void AlterDatabase_SetAllowSnapshotIsolationOn_AcceptsCurrent()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            alter database current set allow_snapshot_isolation on;
            select 1
            """));

    [TestMethod]
    public void AlterDatabase_SetReadCommittedSnapshotOn_AcceptsBareName()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            alter database simulated set read_committed_snapshot on;
            select 1
            """));

    [TestMethod]
    public void Msg3952_SnapshotIsoOnUserTable_WithAllowSnapshotIsolationOff_ThrowsVerbatim()
        => new Simulation().AssertSqlError("""
            create table t (id int not null primary key, v int);
            insert t values (1, 100);
            set transaction isolation level snapshot;
            begin tran;
            select v from t where id = 1;
            commit
            """,
            3952,
            "Snapshot isolation transaction failed accessing database 'simulated' because snapshot isolation is not allowed in this database. Use ALTER DATABASE to allow snapshot isolation.");

    [TestMethod]
    public void Msg3952_SnapshotIsoOnSystemCatalog_DoesNotFire()
    {
        // Reading sys.objects under an SI session with ASI=OFF must NOT
        // raise Msg 3952 — probe-confirmed real SQL Server only gates user-
        // table access by the flag, not system catalogs.
        var count = (int)new Simulation().ExecuteScalar("""
            set transaction isolation level snapshot;
            select count(*) from sys.objects
            """)!;
        Assert.IsGreaterThanOrEqualTo(0, count);
    }

    [TestMethod]
    public void SnapshotIso_AfterAsiOn_ReadsUserTableSuccessfully()
        => AreEqual(100, new Simulation().ExecuteScalar("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100);
            set transaction isolation level snapshot;
            begin tran;
            select v from t where id = 1
            """));

    [TestMethod]
    public void SnapshotReader_SeesPriorCommittedVersion_AfterConcurrentUpdate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);

        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();

        // RC concurrent update commits while the SI tx is open.
        using (var rcConn = sim.CreateOpenConnection())
            _ = rcConn.CreateCommand("update t set v = 200 where id = 1").ExecuteNonQuery();

        // SI tx still sees its snapshot's value (100), not the post-update 200.
        var siRead = siConn.CreateCommand("select v from t where id = 1").ExecuteScalar();
        AreEqual(100, siRead);

        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void Msg3960_SnapshotWriter_OnConcurrentCommittedUpdate_ThrowsVerbatimAndAutoRollsBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);

        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();

        using (var rcConn = sim.CreateOpenConnection())
            _ = rcConn.CreateCommand("update t set v = 200 where id = 1").ExecuteNonQuery();

        // SI writer tries to update the same row — the live version was
        // committed by another tx after our snapshot, so Msg 3960 fires.
        var ex = Throws<System.Data.Common.DbException>(() =>
            siConn.CreateCommand("update t set v = 300 where id = 1").ExecuteNonQuery());
        AreEqual("3960", ex.Data["HelpLink.EvtID"]);
        AreEqual("Snapshot isolation transaction aborted due to update conflict. You cannot use snapshot isolation to access table 'dbo.t' directly or indirectly in database 'simulated' to update, delete, or insert the row that has been modified or deleted by another transaction. Retry the transaction or change the isolation level for the update/delete statement.", ex.Message);

        // Probe-confirmed auto-rollback: @@TRANCOUNT drops to 0.
        AreEqual(0, siConn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Msg3960_SnapshotDelete_OnConcurrentCommittedUpdate_Throws()
    {
        // SI session does a DELETE on a row another tx updated since our
        // snapshot — Msg 3960 fires. The flip side (SI updating a row
        // another tx deleted) needs heap-iteration over tombstoned slots
        // to surface the SI row; deferred. See locking.md for the gap.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);

        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();

        using (var rcConn = sim.CreateOpenConnection())
            _ = rcConn.CreateCommand("update t set v = 200 where id = 1").ExecuteNonQuery();

        var ex = Throws<System.Data.Common.DbException>(() =>
            siConn.CreateCommand("delete from t where id = 1").ExecuteNonQuery());
        AreEqual("3960", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void RcsiReader_SeesCommittedValue_NotBlockedByUncommittedWriter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set read_committed_snapshot on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);

        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update t set v = 999 where id = 1").ExecuteNonQuery();

        // Without RCSI, the reader would block. With RCSI on the reader sees
        // the committed pre-write value (100) without waiting.
        using var reader = sim.CreateOpenConnection();
        var seen = reader.CreateCommand("select v from t where id = 1").ExecuteScalar();
        AreEqual(100, seen);

        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void Rcsi_PerStatementSnapshot_SeesUpdatedValueOnNextStatement()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set read_committed_snapshot on;
            create table t (id int not null primary key, v int);
            insert t values (1, 1)
            """);

        using var rcConn = sim.CreateOpenConnection();
        _ = rcConn.CreateCommand("begin tran").ExecuteNonQuery();
        var first = rcConn.CreateCommand("select v from t where id = 1").ExecuteScalar();
        AreEqual(1, first);

        using (var w = sim.CreateOpenConnection())
            _ = w.CreateCommand("update t set v = 2 where id = 1").ExecuteNonQuery();

        // Per-statement snapshot — the next read in the same tx sees the
        // new committed value (RCSI semantic, distinct from full SI which
        // would still return 1).
        var second = rcConn.CreateCommand("select v from t where id = 1").ExecuteScalar();
        AreEqual(2, second);

        _ = rcConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void AlterDatabase_AllowSnapshotIsolation_OnThenOff_ReturnsToRejection()
        => new Simulation().AssertSqlError("""
            alter database current set allow_snapshot_isolation on;
            alter database current set allow_snapshot_isolation off;
            create table t (id int);
            insert t values (1);
            set transaction isolation level snapshot;
            select id from t
            """,
            3952);

    [TestMethod]
    public void SnapshotReader_BetweenCommitted_SeesCommittedOldValue_AfterMultipleUpdates()
    {
        // Sequential committed UPDATEs build a chain; SI reader takes its
        // snapshot at first read, before further writes; subsequent reads
        // continue to see the snapshot's value.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 10)
            """);

        using var siConn = sim.CreateOpenConnection();
        // First read takes the snapshot at v=10.
        var first = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();
        AreEqual(10, first);

        using (var w = sim.CreateOpenConnection())
            _ = w.CreateCommand("update t set v = 20 where id = 1").ExecuteNonQuery();
        using (var w = sim.CreateOpenConnection())
            _ = w.CreateCommand("update t set v = 30 where id = 1").ExecuteNonQuery();

        // SI snapshot still sees 10.
        var second = siConn.CreateCommand("select v from t where id = 1").ExecuteScalar();
        AreEqual(10, second);

        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void SnapshotReader_SeesDeletedRow_AfterConcurrentCommittedDelete()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300)
            """);

        using var siConn = sim.CreateOpenConnection();
        // First read takes the snapshot at all 3 rows visible.
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();

        using (var rc = sim.CreateOpenConnection())
            _ = rc.CreateCommand("delete from t where id = 2").ExecuteNonQuery();

        // SI snapshot still sees the deleted row's pre-delete payload.
        var v = siConn.CreateCommand("select v from t where id = 2").ExecuteScalar();
        AreEqual(200, v);
        var count = siConn.CreateCommand("select count(*) from t").ExecuteScalar();
        AreEqual(3, count);

        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void Msg3960_SnapshotUpdate_OnRcDeletedRow_ThrowsWithAutoRollback()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);

        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();

        using (var rc = sim.CreateOpenConnection())
            _ = rc.CreateCommand("delete from t where id = 1").ExecuteNonQuery();

        // SI snapshot still sees id=1; UPDATE on it must raise Msg 3960
        // (not silently succeed with 0 affected rows).
        var ex = Throws<System.Data.Common.DbException>(() =>
            siConn.CreateCommand("update t set v = 999 where id = 1").ExecuteNonQuery());
        AreEqual("3960", ex.Data["HelpLink.EvtID"]);
        AreEqual(0, siConn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Msg3960_SnapshotDelete_OnRcDeletedRow_Throws()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);

        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();

        using (var rc = sim.CreateOpenConnection())
            _ = rc.CreateCommand("delete from t where id = 1").ExecuteNonQuery();

        var ex = Throws<System.Data.Common.DbException>(() =>
            siConn.CreateCommand("delete from t where id = 1").ExecuteNonQuery());
        AreEqual("3960", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SnapshotReader_AfterCommit_SeesNewBaseline()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 1)
            """);

        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();

        using (var w = sim.CreateOpenConnection())
            _ = w.CreateCommand("update t set v = 2 where id = 1").ExecuteNonQuery();
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();

        // After committing the SI tx, a fresh SI tx takes a new snapshot
        // and sees v = 2.
        var fresh = siConn.CreateCommand("begin tran; select v from t where id = 1").ExecuteScalar();
        AreEqual(2, fresh);
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }
}
