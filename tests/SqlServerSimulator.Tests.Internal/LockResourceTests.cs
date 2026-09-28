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
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, conn.Session, timeoutMillis: 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, conn.Session);
    }

    [TestMethod]
    public void SchS_TwoDifferentOwners_BothGrant()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a.Session, 0);
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, b.Session, 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, b.Session);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a.Session);
    }

    [TestMethod]
    public void SchM_BlocksConflictingSchS_TimeoutZeroRaises1222()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        sim.LockManager.Acquire(resource, LockMode.SchemaModification, a.Session, 0);
        var ex = Throws<SimulatedSqlException>(() =>
            sim.LockManager.Acquire(resource, LockMode.SchemaStability, b.Session, 0));
        AreEqual(1222, ex.Number);
        AreEqual("Lock request time out period exceeded.", ex.Message);
        sim.LockManager.Release(resource, LockMode.SchemaModification, a.Session);
    }

    [TestMethod]
    public void X_BlocksConflictingX_TimeoutZeroRaises1222()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        sim.LockManager.Acquire(resource, LockMode.Exclusive, a.Session, 0);
        var ex = Throws<SimulatedSqlException>(() =>
            sim.LockManager.Acquire(resource, LockMode.Exclusive, b.Session, 0));
        AreEqual(1222, ex.Number);
        sim.LockManager.Release(resource, LockMode.Exclusive, a.Session);
    }

    [TestMethod]
    public void X_BlocksConflictingS_TimeoutZeroRaises1222()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        sim.LockManager.Acquire(resource, LockMode.Exclusive, a.Session, 0);
        _ = Throws<SimulatedSqlException>(() =>
            sim.LockManager.Acquire(resource, LockMode.Shared, b.Session, 0));
        sim.LockManager.Release(resource, LockMode.Exclusive, a.Session);
    }

    [TestMethod]
    public void SS_Compatible_BothGrant()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.Shared, a.Session, 0);
        sim.LockManager.Acquire(resource, LockMode.Shared, b.Session, 0);
        sim.LockManager.Release(resource, LockMode.Shared, b.Session);
        sim.LockManager.Release(resource, LockMode.Shared, a.Session);
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
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a.Session, 0);
        sim.LockManager.Acquire(resource, LockMode.Exclusive, b.Session, 0);
        sim.LockManager.Release(resource, LockMode.Exclusive, b.Session);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a.Session);
    }

    [TestMethod]
    public void Reentrance_SameOwnerSameMode_BumpsCount()
    {
        var sim = new Simulation();
        var resource = new LockResource();
        var a = sim.CreateDbConnection();
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a.Session, 0);
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a.Session, 0);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a.Session);
        var b = sim.CreateDbConnection();
        a.CurrentExecutingThreadId = -1;
        _ = Throws<SimulatedSqlException>(() => sim.LockManager.Acquire(resource, LockMode.SchemaModification, b.Session, 0));
        sim.LockManager.Release(resource, LockMode.SchemaStability, a.Session);
        sim.LockManager.Acquire(resource, LockMode.SchemaModification, b.Session, 0);
        sim.LockManager.Release(resource, LockMode.SchemaModification, b.Session);
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
            sim.LockManager.Acquire(resource, LockMode.SchemaStability, holder.Session, 0);
            Thread.Sleep(100);
            sim.LockManager.Release(resource, LockMode.SchemaStability, holder.Session);
            holder.CurrentExecutingThreadId = null;
        }, TestContext.CancellationToken);
        await Task.Delay(20, TestContext.CancellationToken);
        sim.LockManager.Acquire(resource, LockMode.SchemaModification, waiter.Session, 1000);
        sim.LockManager.Release(resource, LockMode.SchemaModification, waiter.Session);
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
        sim.LockManager.Acquire(resource, LockMode.SchemaStability, a.Session, 0);
        var ex = Throws<SimulatedSqlException>(() =>
            sim.LockManager.Acquire(resource, LockMode.SchemaModification, b.Session, 10000));
        AreEqual(1205, ex.Number);
        Contains($"Process ID {b.Spid}", ex.Message);
        Contains("deadlocked on lock resources", ex.Message);
        sim.LockManager.Release(resource, LockMode.SchemaStability, a.Session);
    }

    [TestMethod]
    public void CycleDetection_DetectorPicksRequester()
    {
        // Set up a 2-cycle without real threading: this thread holds X on
        // r1; a second "impersonated" connection holds X on r2 and is
        // marked as waiting on r1 (WaitingOnResource = r1). When this
        // thread asks for X on r2, the detector walks r2's holders → b →
        // b.WaitingOnResource = r1 → r1's holders → us, cycle closed.
        // Caller (us) is the victim, both sessions at the same deadlock priority.
        var sim = new Simulation();
        var r1 = new LockResource();
        var r2 = new LockResource();
        var caller = sim.CreateDbConnection();
        var b = sim.CreateDbConnection();
        caller.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        b.CurrentExecutingThreadId = -1; // foreign thread — avoids the same-thread short-circuit
        sim.LockManager.Acquire(r1, LockMode.Exclusive, caller.Session, 0);
        sim.LockManager.Acquire(r2, LockMode.Exclusive, b.Session, 0);
        b.WaitingOnResource = r1;
        try
        {
            var ex = Throws<SimulatedSqlException>(() =>
                sim.LockManager.Acquire(r2, LockMode.Exclusive, caller.Session, timeoutMillis: 10000));
            AreEqual(1205, ex.Number);
            Contains($"Process ID {caller.Spid}", ex.Message);
        }
        finally
        {
            b.WaitingOnResource = null;
            sim.LockManager.Release(r2, LockMode.Exclusive, b.Session);
            sim.LockManager.Release(r1, LockMode.Exclusive, caller.Session);
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
    public void RangeModes_CompatibilityMatrix_IsRealsKeyRangeMatrix()
    {
        // Rows requested, columns held: S U X RangeS-S RangeS-U RangeI-N RangeX-X.
        LockMode[] modes = [LockMode.Shared, LockMode.Update, LockMode.Exclusive, LockMode.RangeSharedShared, LockMode.RangeSharedUpdate, LockMode.RangeInsertNull, LockMode.RangeExclusiveExclusive];
        string[] grid =
        [
            "YYNYYYN",
            "YNNYNYN",
            "NNNNNYN",
            "YYNYYNN",
            "YNNYNNN",
            "YYYNNYN",
            "NNNNNNN",
        ];
        for (var requested = 0; requested < modes.Length; requested++)
        {
            for (var held = 0; held < modes.Length; held++)
                AreEqual(grid[requested][held] == 'Y', LockManager.IsCompatible(modes[held], modes[requested]), $"{modes[requested]} requested over {modes[held]} held");
        }
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
    public void ActiveKeyRangeLocks_TracksHeldKeyLocks_ResetsAtCommit()
    {
        // The writer's per-row key-lock test keys off this per-table count the
        // way the reader's fast path keys off ActiveDataWriters: at zero the
        // writer skips decoding its row and never touches the gate. A leak
        // here costs every writer a decode per mutation forever after.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key, v int); insert t values (3, 0), (9, 0)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        AreEqual(0, table.ActiveKeyRangeLocks);
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select count(*) from t where k between 1 and 5");
        // Key 3 inside the interval, key 9 past it.
        AreEqual(2, table.ActiveKeyRangeLocks);
        ExecuteNonQuery(conn, "commit tran");
        AreEqual(0, table.ActiveKeyRangeLocks);
        IsEmpty(table.KeyLockGroups.Values.SelectMany(static g => g.Anchors.Values.Append(g.Infinity)).SelectMany(static r => r.Holders));
    }

    [TestMethod]
    public void ActiveKeyRangeLocks_StaysZero_OverAHeapScan()
    {
        // A heap has no key to walk, so its SERIALIZABLE scan takes the table
        // S real takes — no key lock is interned.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int, v int); insert t values (1, 3)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select count(*) from t where v = 3");
        AreEqual(0, table.ActiveKeyRangeLocks);
        IsEmpty(table.KeyLockGroups);
        Contains(LockMode.Shared, table.TableDataLock.Holders.Select(static h => h.Mode));
        DoesNotContain(LockMode.IntentShared, table.TableDataLock.Holders.Select(static h => h.Mode));
    }

    [TestMethod]
    public void SerializableUpdLockRead_TakesRangeSU_AndKeepsItsRowU()
    {
        // Real folds the two into one key lock; the row-U here stays on top of
        // the RangeS-U, since the readers and writers that take a row lock meet
        // it there.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key, v int); insert t values (2, 20)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 1 and 5");

        // Key 2 and the infinity anchor past it.
        AreEqual(2, table.ActiveKeyRangeLocks);
        Contains(
            LockMode.RangeSharedUpdate,
            table.KeyLockGroups.Values.SelectMany(static g => g.Anchors.Values).SelectMany(static r => r.Holders).Select(static h => h.Mode));
        Contains(LockMode.Update, table.RowLocks.Values.SelectMany(static r => r.Holders).Select(static h => h.Mode));
        Contains(LockMode.IntentExclusive, table.TableDataLock.Holders.Select(static h => h.Mode));
        DoesNotContain(LockMode.Shared, table.TableDataLock.Holders.Select(static h => h.Mode));

        ExecuteNonQuery(conn, "rollback tran");
        AreEqual(0, table.ActiveKeyRangeLocks);
    }

    [TestMethod]
    public void KeyLockAnchor_InternsInTheColumnType_WhateverTheProbeWasPromotedTo()
    {
        // A bigint probe over an int key reads the seek cache in bigint; the
        // anchor it locks is restated in int, so a writer's test — which reads
        // its row in the column type — finds the same resource.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key); insert t values (20), (30)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "set transaction isolation level serializable; begin tran; select count(*) from t where k > cast(25 as bigint); select count(*) from t where k between 21 and 29");
        var group = table.KeyLockGroups.Values.Single();
        HasCount(1, group.Anchors);
        AreSame(SqlType.Int32, group.Anchors.Keys.Single().ComponentAt(0).Type);
        ExecuteNonQuery(conn, "rollback tran");
    }

    [TestMethod]
    public void KeyLockGroup_OfANonUniqueIndex_AppendsTheClusteredKey()
    {
        // Real's nonclustered entry carries the row locator, so each row of a
        // duplicated value is an anchor of its own.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (k int not null primary key, c int, index ic (c)); create unique index uc on t (c) where c > 0");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        var nonUnique = KeyLockGroup.For(table, table.Indexes.Single(static i => i.Name == "ic"))!;
        CollectionAssert.AreEqual(new[] { 1, 0 }, nonUnique.Ordinals);
        AreEqual(1, nonUnique.KeyLength);
        IsFalse(nonUnique.IsRowGroup);
        var unique = KeyLockGroup.For(table, table.Indexes.Single(static i => i.Name == "uc"))!;
        CollectionAssert.AreEqual(new[] { 1 }, unique.Ordinals);
        IsTrue(KeyLockGroup.RowGroupOf(table)!.IsRowGroup);
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
        sim.LockManager.Acquire(resource, LockMode.Shared, a.Session, 0);
        sim.LockManager.Acquire(resource, LockMode.Update, b.Session, 0);
        sim.LockManager.Release(resource, LockMode.Update, b.Session);
        sim.LockManager.Release(resource, LockMode.Shared, a.Session);
        sim.LockManager.Acquire(resource, LockMode.IntentShared, a.Session, 0);
        sim.LockManager.Acquire(resource, LockMode.Update, b.Session, 0);
        sim.LockManager.Release(resource, LockMode.Update, b.Session);
        sim.LockManager.Release(resource, LockMode.IntentShared, a.Session);
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
            sim.LockManager.Acquire(resource, LockMode.Update, a.Session, 0);
            _ = Throws<SimulatedSqlException>(() => sim.LockManager.Acquire(resource, conflict, b.Session, 0));
            sim.LockManager.Release(resource, LockMode.Update, a.Session);
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
            sim.LockManager.Acquire(resource, LockMode.IntentShared, a.Session, 0);
            sim.LockManager.Acquire(resource, ok, b.Session, 0);
            sim.LockManager.Release(resource, ok, b.Session);
            sim.LockManager.Release(resource, LockMode.IntentShared, a.Session);
        }
        // IS × X: conflict.
        var rx = new LockResource();
        sim.LockManager.Acquire(rx, LockMode.IntentShared, a.Session, 0);
        _ = Throws<SimulatedSqlException>(() => sim.LockManager.Acquire(rx, LockMode.Exclusive, b.Session, 0));
        sim.LockManager.Release(rx, LockMode.IntentShared, a.Session);
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
            sim.LockManager.Acquire(resource, LockMode.IntentExclusive, a.Session, 0);
            _ = Throws<SimulatedSqlException>(() => sim.LockManager.Acquire(resource, conflict, b.Session, 0));
            sim.LockManager.Release(resource, LockMode.IntentExclusive, a.Session);
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
        sim.LockManager.Acquire(r1, LockMode.SharedIntentExclusive, a.Session, 0);
        sim.LockManager.Acquire(r1, LockMode.IntentShared, b.Session, 0);
        sim.LockManager.Release(r1, LockMode.IntentShared, b.Session);
        sim.LockManager.Release(r1, LockMode.SharedIntentExclusive, a.Session);
        // SIX × everything else: conflict.
        foreach (var conflict in new[] { LockMode.IntentExclusive, LockMode.SharedIntentExclusive, LockMode.Shared, LockMode.Update, LockMode.Exclusive })
        {
            var resource = new LockResource();
            sim.LockManager.Acquire(resource, LockMode.SharedIntentExclusive, a.Session, 0);
            _ = Throws<SimulatedSqlException>(() => sim.LockManager.Acquire(resource, conflict, b.Session, 0));
            sim.LockManager.Release(resource, LockMode.SharedIntentExclusive, a.Session);
        }
    }

    [TestMethod]
    public void RowLockEscalation_OneStatementPastTheThreshold_PromotesToTableX()
    {
        // One statement taking past real's first escalation attempt (about
        // 6 250 locks with the pages counted) trades its row X locks for a
        // single table X; after commit nothing is held.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran; insert t select value from generate_series(1, 6500)");
        var tx = conn.CurrentTransaction;
        IsNotNull(tx);
        Contains(table, tx.EscalatedTables);
        IsEmpty(table.RowLocks.Values.SelectMany(static r => r.Holders));
        ExecuteNonQuery(conn, "commit tran");
        IsEmpty(table.TableDataLock.Holders);
    }

    [TestMethod]
    public void RowLockEscalation_CountsPerStatement_NotPerTransaction()
    {
        // Two statements of 4 000 rows each stay under the threshold one at a
        // time, as real's escalation counts a statement's locks.
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran; insert t select value from generate_series(1, 4000); insert t select value from generate_series(1, 4000)");
        var tx = conn.CurrentTransaction;
        IsNotNull(tx);
        DoesNotContain(table, tx.EscalatedTables);
        ExecuteNonQuery(conn, "commit tran");
    }

    [TestMethod]
    public void RowLockEscalation_UnderLockEscalationDisable_KeepsTheRowLocks()
    {
        var sim = new Simulation();
        ExecuteNonQuery(sim, "create table t (id int); alter table t set (lock_escalation = disable)");
        using var conn = sim.CreateDbConnection();
        conn.Open();
        var table = conn.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        ExecuteNonQuery(conn, "begin tran; insert t select value from generate_series(1, 6500)");
        var tx = conn.CurrentTransaction;
        IsNotNull(tx);
        DoesNotContain(table, tx.EscalatedTables);
        ExecuteNonQuery(conn, "commit tran");
    }
}
