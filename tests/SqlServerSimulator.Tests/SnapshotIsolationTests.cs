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
    public TestContext TestContext { get; set; } = null!;

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
        var ex = Throws<SimulatedSqlException>(() =>
            siConn.CreateCommand("update t set v = 300 where id = 1").ExecuteNonQuery());
        AreEqual(3960, ex.Number);
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

        var ex = Throws<SimulatedSqlException>(() =>
            siConn.CreateCommand("delete from t where id = 1").ExecuteNonQuery());
        AreEqual(3960, ex.Number);
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
        var ex = Throws<SimulatedSqlException>(() =>
            siConn.CreateCommand("update t set v = 999 where id = 1").ExecuteNonQuery());
        AreEqual(3960, ex.Number);
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

        var ex = Throws<SimulatedSqlException>(() =>
            siConn.CreateCommand("delete from t where id = 1").ExecuteNonQuery());
        AreEqual(3960, ex.Number);
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

    // ---- one version per row per transaction (probed 2026-09-28 against SQL Server 2025) ----

    private static Simulation SnapshotFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100), (2, 100)
            """);
        return sim;
    }

    [TestMethod]
    public void TwoUpdatesInOneTransaction_OlderSnapshotSeesThePreTransactionRow()
    {
        var sim = SnapshotFixture();
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();
        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("begin tran; update t set v = 200 where id = 1; update t set v = 300 where id = 1; commit").ExecuteNonQuery();
        AreEqual(100, siConn.CreateCommand("select v from t where id = 1").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(300, sim.ExecuteScalar("select v from t where id = 1"));
    }

    [TestMethod]
    public void TwoUpdatesInFlight_ConcurrentSnapshotSeesThePreTransactionRow()
    {
        var sim = SnapshotFixture();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update t set v = 200 where id = 1; update t set v = 300 where id = 1").ExecuteNonQuery();
        using var siConn = sim.CreateOpenConnection();
        AreEqual(100, siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(100, siConn.CreateCommand("select v from t where id = 1").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void DeleteInFlight_ConcurrentSnapshotSeesTheRow()
    {
        var sim = SnapshotFixture();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; delete t where id = 1").ExecuteNonQuery();
        using var siConn = sim.CreateOpenConnection();
        AreEqual(2, siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar());
        AreEqual(100, siConn.CreateCommand("select v from t where id = 1").ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(2, siConn.CreateCommand("select count(*) from t").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void MergeInFlight_ConcurrentSnapshotSeesThePreMergeRows()
    {
        const string rows = "select string_agg(concat(id, ':', v), ',') within group (order by id) from t";
        var sim = SnapshotFixture();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("""
            begin tran;
            merge t using (values (1, 300), (3, 300)) s (id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched then insert values (s.id, s.v)
            when not matched by source then delete;
            """).ExecuteNonQuery();
        using var siConn = sim.CreateOpenConnection();
        AreEqual("1:100,2:100", siConn.CreateCommand("set transaction isolation level snapshot; begin tran; " + rows).ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual("1:100,2:100", siConn.CreateCommand(rows).ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
        AreEqual("1:300,3:300", sim.ExecuteScalar(rows));
    }

    [TestMethod]
    public void InsertThenUpdateInOneTransaction_OlderSnapshotSeesNoRow()
    {
        var sim = SnapshotFixture();
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();
        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("begin tran; insert t values (3, 1); update t set v = 2 where id = 3; commit").ExecuteNonQuery();
        AreEqual(2, siConn.CreateCommand("select count(*) from t").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void TwoUpdatesInOneTransaction_KeepOneVersionPerRow()
    {
        var sim = SnapshotFixture();
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();
        using (var writer = sim.CreateOpenConnection())
        {
            _ = writer.CreateCommand("""
                begin tran;
                update t set v = 200 where id = 1;
                update t set v = 300 where id = 1;
                update t set v = 400 where id = 1;
                update t set v = 500 where id = 2;
                commit
                """).ExecuteNonQuery();
        }
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.dm_tran_version_store"));
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void FailedLaterUpdate_KeepsTheFirstUpdatesHistory()
    {
        var sim = SnapshotFixture();
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();
        using (var writer = sim.CreateOpenConnection())
        {
            _ = writer.CreateCommand("begin tran; update t set v = 200 where id = 1").ExecuteNonQuery();
            _ = Throws<SimulatedSqlException>(() => writer.CreateCommand("update t set v = v / 0 where id = 1").ExecuteNonQuery());
            _ = writer.CreateCommand("commit").ExecuteNonQuery();
        }
        AreEqual(100, siConn.CreateCommand("select v from t where id = 1").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(200, sim.ExecuteScalar("select v from t where id = 1"));
    }

    [TestMethod]
    public void TwoUpdatesRolledBack_SnapshotAndLiveReadTheOriginal()
    {
        var sim = SnapshotFixture();
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select v from t where id = 1").ExecuteScalar();
        using (var writer = sim.CreateOpenConnection())
            _ = writer.CreateCommand("begin tran; update t set v = 200 where id = 1; update t set v = 300 where id = 1; rollback").ExecuteNonQuery();
        AreEqual(100, siConn.CreateCommand("select v from t where id = 1").ExecuteScalar());
        _ = siConn.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(100, sim.ExecuteScalar("select v from t where id = 1"));
    }

    [TestMethod]
    [DataRow("update t set v = 300 where id = 1", "update t set id = 10 where id = 1", 2, DisplayName = "key moved")]
    [DataRow("delete t where v = 100", "update t set v = 5 where id = 1", 2, DisplayName = "row left the predicate")]
    public void Msg3960_WhenAnotherTransactionTookTheRowOutOfTheWhere(string snapshotWrite, string otherWrite, int state)
    {
        // The row the snapshot sees matches, though the live row no longer
        // does (probed 2026-09-28 against SQL Server 2025).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100), (2, 200)
            """);
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();
        using (var rcConn = sim.CreateOpenConnection())
            _ = rcConn.CreateCommand(otherWrite).ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => siConn.CreateCommand(snapshotWrite).ExecuteNonQuery());
        AreEqual((3960, (byte)state), (ex.Number, ex.State));
        AreEqual(0, siConn.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Msg3960_OnAHeap_IsState6()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table h (id int, v int);
            insert h values (1, 100)
            """);
        using var siConn = sim.CreateOpenConnection();
        _ = siConn.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from h").ExecuteScalar();
        using (var rcConn = sim.CreateOpenConnection())
            _ = rcConn.CreateCommand("update h set v = 5 where id = 1").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => siConn.CreateCommand("update h set v = 3 where id = 1").ExecuteNonQuery());
        AreEqual((3960, (byte)6), (ex.Number, ex.State));
    }

    private static Simulation TwoAccounts(string options = "")
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"alter database simulated set allow_snapshot_isolation on; {options} create table acc (id int primary key, bal int not null); insert acc values (1, 100), (2, 100);");
        return sim;
    }

    /// <summary>
    /// A locking hint makes a READ_COMMITTED_SNAPSHOT read a locking one: it
    /// waits out the writer and reads the row it committed, where the other
    /// hints leave it reading the version (probed 2026-10-03 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("updlock")]
    [DataRow("holdlock")]
    [DataRow("repeatableread")]
    [DataRow("readcommittedlock")]
    [DataRow("xlock")]
    public async Task Rcsi_LockingHintRead_WaitsForTheWriter(string hint)
    {
        var sim = TwoAccounts("alter database simulated set read_committed_snapshot on;");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; update acc set bal = bal + 1 where id = 1").ExecuteNonQuery();
        var read = await sim.StartBlocked(reader, $"select bal from acc with ({hint}) where id = 1", TestContext.CancellationToken);
        _ = writer.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(101, (await read).Single());
    }

    [TestMethod]
    [DataRow("tablock")]
    [DataRow("readcommitted")]
    [DataRow("rowlock")]
    [DataRow("paglock")]
    [DataRow("readpast")]
    [DataRow("nowait")]
    public void Rcsi_NonLockingHintRead_ReadsTheVersion(string hint)
    {
        var sim = TwoAccounts("alter database simulated set read_committed_snapshot on;");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; update acc set bal = bal + 1 where id = 1").ExecuteNonQuery();

        AreEqual(100, reader.CreateCommand($"select bal from acc with ({hint}) where id = 1").ExecuteScalar());
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// <c>READ UNCOMMITTED</c> reads dirty only where it takes no lock: an
    /// <c>UPDLOCK</c> read under it waits for the writer (probed 2026-10-03
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("updlock")]
    [DataRow("xlock")]
    public async Task ReadUncommitted_LockingHintRead_WaitsForTheWriter(string hint)
    {
        var sim = TwoAccounts();
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; update acc set bal = bal + 1 where id = 1").ExecuteNonQuery();
        var read = await sim.StartBlocked(reader, $"set transaction isolation level read uncommitted; select bal from acc with ({hint}) where id = 1", TestContext.CancellationToken);
        _ = writer.CreateCommand("commit").ExecuteNonQuery();

        AreEqual(101, (await read).Single());
    }

    /// <summary>
    /// Under SNAPSHOT, a read announcing an update — <c>UPDLOCK</c>,
    /// <c>XLOCK</c>, <c>TABLOCKX</c> — of a row another transaction changed
    /// since the snapshot meets the update conflict an update would (Msg
    /// 3960, ending the transaction), while a <c>SERIALIZABLE</c> one reads
    /// the latest committed row (probed 2026-10-03 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("updlock")]
    [DataRow("xlock")]
    [DataRow("tablockx")]
    public void Snapshot_UpdatingHintRead_OfARowChangedSinceTheSnapshot_IsAnUpdateConflict(string hint)
    {
        var sim = TwoAccounts();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from acc where id = 2").ExecuteScalar();
        _ = sim.ExecuteNonQuery("update acc set bal = bal + 1 where id = 1");

        AreEqual(3960, Throws<SimulatedSqlException>(() => reader.CreateCommand($"select bal from acc with ({hint}) where id = 1").ExecuteScalar()).Number);
        AreEqual(0, reader.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Snapshot_SerializableHintRead_ReadsTheLatestCommittedRow()
    {
        var sim = TwoAccounts();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from acc where id = 2").ExecuteScalar();
        _ = sim.ExecuteNonQuery("update acc set bal = bal + 1 where id = 1");

        AreEqual(101, reader.CreateCommand("select bal from acc with (serializable) where id = 1").ExecuteScalar());
        _ = reader.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// A rollback to a savepoint keeps the transaction's earlier writes in
    /// flight: a snapshot reader still reads past them to the committed rows,
    /// where the rollback once cleared their marks with the savepoint's own.
    /// </summary>
    [TestMethod]
    [DataRow("update acc set bal = bal - 10 where id = 1; save tran s; update acc set bal = bal + 1000 where id = 1; rollback tran s")]
    [DataRow("update acc set bal = bal - 10 where id = 1; save tran s; delete acc where id = 1; rollback tran s")]
    [DataRow("insert acc values (3, 100); save tran s; update acc set bal = bal + 1000 where id = 3; rollback tran s")]
    public void SnapshotRead_AfterARollbackToASavepoint_StillReadsTheCommittedRows(string writes)
    {
        var sim = TwoAccounts();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; " + writes).ExecuteNonQuery();

        AreEqual("1:100,2:100", sim.ExecuteScalar("set transaction isolation level snapshot; begin tran; select string_agg(concat(id, ':', bal), ',') within group (order by id) from acc; commit"));
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }
}
