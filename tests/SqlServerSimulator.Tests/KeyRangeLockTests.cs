using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Key-range locking: what a SERIALIZABLE / <c>HOLDLOCK</c> reader fences when
/// its predicate is sargable on an indexed leading column, and what it falls
/// back to when it isn't. The payoff case is
/// <see cref="SerializableEquality_ConcurrentInsertOutsideRange_DoesNotBlock"/>
/// — two SERIALIZABLE transactions over disjoint key ranges of one table no
/// longer serialize on a whole-table S.
/// </summary>
[TestClass]
// Same scheduling caveat as LockingTests: every blocking assertion here hands
// work to a threadpool thread and asserts on a deadline that it *started*, so
// a test elsewhere that monopolizes the pool surfaces as failures here rather
// than at its own site.
public sealed class KeyRangeLockTests
{
    /// <summary>See <c>LockingTests.ThreadStartTimeoutMs</c> — only ever waited
    /// out on the failure path.</summary>
    private const int ThreadStartTimeoutMs = 10_000;

    /// <summary>Window a "didn't block" assertion gives the background statement
    /// to actually finish before it is called a hang.</summary>
    private static readonly TimeSpan ProceedTimeout = TimeSpan.FromSeconds(5);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Three-row table keyed on <c>k</c> by a clustered PRIMARY KEY, plus a
    /// non-key column so a non-sargable predicate has something to read.
    /// </summary>
    private static Simulation KeyedTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (k int not null primary key, v int not null);
            insert t values (10, 1), (20, 2), (30, 3)
            """);
        return sim;
    }

    /// <summary>
    /// Table keyed on the composite <c>(a, b)</c>, so a predicate bounding a
    /// leading prefix has a tuple interval to fence.
    /// </summary>
    private static Simulation CompositeKeyedTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table ck (a int not null, b int not null, v int not null, primary key (a, b));
            insert ck values (1, 2, 100), (1, 5, 101), (1, 9, 102), (2, 2, 103), (2, 7, 104), (3, 1, 105)
            """);
        return sim;
    }

    // Runs `writeSql` on `writer` from a threadpool thread and asserts it is
    // still blocked after the holder has had time to matter, then releases the
    // holder and drains the write. Returns once the write has completed.
    private async Task AssertBlocksUntil(DbConnection holder, DbConnection writer, string writeSql, string release)
    {
        using var started = new ManualResetEventSlim();
        var task = Task.Run(
            () =>
            {
                started.Set();
                _ = writer.CreateCommand(writeSql).ExecuteNonQuery();
            },
            TestContext.CancellationToken);
        IsTrue(started.Wait(ThreadStartTimeoutMs, TestContext.CancellationToken));
        await Task.Delay(150, TestContext.CancellationToken);
        IsFalse(task.IsCompleted, $"expected `{writeSql}` to block");
        _ = holder.CreateCommand(release).ExecuteNonQuery();
        await task;
    }

    // Runs `sql` on `conn` from a threadpool thread and asserts it completes
    // promptly — the holder's range doesn't cover it.
    private async Task AssertProceeds(DbConnection conn, string sql) =>
        await Task.Run(
            () => conn.CreateCommand(sql).ExecuteNonQuery(),
            TestContext.CancellationToken)
            .WaitAsync(ProceedTimeout, TestContext.CancellationToken);

    [TestMethod]
    public async Task SerializableEquality_ConcurrentInsertOutsideRange_DoesNotBlock()
    {
        // The payoff: a SERIALIZABLE reader whose predicate is `k = 20` fences
        // the single value 20, not the table. A writer landing anywhere else in
        // the key space proceeds while that transaction is still open — under
        // the whole-table S this replaced, it would have waited for the commit.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select v from t where k = 20").ExecuteScalar();
        await AssertProceeds(writer, "insert t values (25, 9)");

        _ = reader.CreateCommand("commit tran").ExecuteNonQuery();
        AreEqual(4, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public async Task SerializableEquality_ConcurrentInsertOfTheFencedValue_Blocks()
    {
        // The mirror of the payoff case. `k = 40` matches nothing, so the
        // reader read no rows and holds no row lock — only the range fences
        // the value, and that is what makes the phantom impossible.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        AreEqual(0, reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k = 40").ExecuteScalar());
        await AssertBlocksUntil(reader, writer, "insert t values (40, 9)", "commit tran");

        AreEqual(4, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public async Task SerializableBetween_InsertInsideTheRange_Blocks()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert t values (22, 9)", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableBetween_InsertOutsideTheRange_DoesNotBlock()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(writer, "insert t values (26, 9)");

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task SerializableOpenEndedRange_InsertPastTheLastKey_Blocks()
    {
        // `k > 25` runs to positive infinity, so it fences the whole key space
        // past 25 — real names that the infinity range and blocks the same
        // insert.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k > 25").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert t values (5000, 9)", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableOpenEndedRange_InsertBelowTheBound_DoesNotBlock()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k > 25").ExecuteScalar();
        await AssertProceeds(writer, "insert t values (1, 9)");

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task SerializableRange_UpdateOfARowInsideTheRange_Blocks()
    {
        // A range covers the rows it spans as well as the gaps: the writer's
        // row-X probes the range its pre-update image falls in.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "update t set v = 99 where k = 20", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableRange_UpdateMovingARowIntoTheRange_Blocks()
    {
        // The row starts outside the fence, so its pre-update image clears the
        // probe; only the post-update image reveals the phantom.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "update t set k = 21 where k = 30", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableRange_DeleteOfARowInsideTheRange_Blocks()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "delete t where k = 20", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableRange_UpdateOutsideTheRange_DoesNotBlock()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(writer, "update t set v = 99 where k = 30");

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task TwoSerializableReaders_OverlappingRanges_DoNotBlockEachOther()
    {
        // RangeS-S × RangeS-S is compatible — probed against real, where the
        // second SERIALIZABLE reader of an overlapping interval proceeds.
        var sim = KeyedTable();
        using var readerA = sim.CreateOpenConnection();
        using var readerB = sim.CreateOpenConnection();

        _ = readerA.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(readerB, "set transaction isolation level serializable; begin tran; select count(*) from t where k between 20 and 30");

        _ = readerB.CreateCommand("rollback tran").ExecuteNonQuery();
        _ = readerA.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task SerializablePredicateOnUnindexedColumn_FallsBackToWholeTable()
    {
        // `v` leads no key or index, so there is no key space to fence along —
        // real takes an object-level S here too (probed on a heap). The
        // fallback blocks a writer anywhere in the table.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where v = 2").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert t values (999, 9)", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableWholeTableScan_FallsBackToWholeTable()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert t values (999, 9)", "rollback tran");
    }

    [TestMethod]
    public async Task RepeatableRead_OnTheSameIndexedTable_AllowsAnInsertIntoTheReadRange()
    {
        // The isolation-level boundary: REPEATABLE READ takes plain key locks
        // and no ranges, so a phantom is allowed in — probed against real,
        // where the same insert proceeds under RR and blocks under SER.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level repeatable read; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(writer, "insert t values (22, 9)");

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
        AreEqual(4, sim.ExecuteScalar("select count(*) from t with (nolock)"));
    }

    [TestMethod]
    public async Task HoldLockHint_UnderReadCommitted_FencesTheSameRange()
    {
        // HOLDLOCK is SERIALIZABLE for the statement it sits on — probed: the
        // hint under a READ COMMITTED session takes the identical range.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("begin tran; select count(*) from t with (holdlock) where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(writer, "insert t values (26, 9)");
        await AssertBlocksUntil(reader, writer, "insert t values (22, 8)", "rollback tran");
    }

    [TestMethod]
    public async Task WriterUnderReadUncommitted_IsStillFencedByAHeldRange()
    {
        // A range locks writers whatever isolation level they run at — the
        // probe is on the write path, not the reader's.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "set transaction isolation level read uncommitted; insert t values (22, 9)", "rollback tran");
    }

    [TestMethod]
    public void DmTranLocks_ProjectsAHeldRange_AsAKeyResourceInRangeSSMode()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();

        AreEqual(1, reader.CreateCommand("""
            select count(*) from sys.dm_tran_locks
            where resource_type = 'KEY' and request_mode = 'RangeS-S' and request_status = 'GRANT'
            """).ExecuteScalar());
        AreEqual("0:[15,25]", reader.CreateCommand("""
            select resource_description from sys.dm_tran_locks where resource_type = 'KEY'
            """).ExecuteScalar());

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
        AreEqual(0, reader.CreateCommand("select count(*) from sys.dm_tran_locks where resource_type = 'KEY'").ExecuteScalar());
    }

    [TestMethod]
    public async Task CrossedKeyRanges_DeadlockOnTheExistingDetector_RaisesMsg1205()
    {
        // Each transaction fences one range and then writes into the other's —
        // the classic crossed-range deadlock, which real reports as Msg 1205.
        // Range waits enter the same wait-for graph every other lock does, so
        // the existing detector sees the cycle unchanged.
        var sim = KeyedTable();
        using var connA = sim.CreateOpenConnection();
        using var connB = sim.CreateOpenConnection();

        _ = connA.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 100 and 200").ExecuteScalar();
        _ = connB.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 300 and 400").ExecuteScalar();

        Exception? aError = null;
        Exception? bError = null;
        using var aStarted = new ManualResetEventSlim();
        using var bStarted = new ManualResetEventSlim();
        var taskA = Task.Run(
            () =>
            {
                aStarted.Set();
                try { _ = connA.CreateCommand("insert t values (350, 1)").ExecuteNonQuery(); }
                catch (Exception ex) { aError = ex; }
            },
            TestContext.CancellationToken);
        IsTrue(aStarted.Wait(ThreadStartTimeoutMs, TestContext.CancellationToken));
        await Task.Delay(150, TestContext.CancellationToken);

        var taskB = Task.Run(
            () =>
            {
                bStarted.Set();
                try { _ = connB.CreateCommand("insert t values (150, 1)").ExecuteNonQuery(); }
                catch (Exception ex) { bError = ex; }
            },
            TestContext.CancellationToken);
        IsTrue(bStarted.Wait(ThreadStartTimeoutMs, TestContext.CancellationToken));
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        IsTrue(aError is null ^ bError is null);
        var victim = aError ?? bError;
        IsNotNull(victim);
        AreEqual("1205", IsInstanceOfType<DbException>(victim).Data["HelpLink.EvtID"]);

        _ = (aError is null ? connA : connB).CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void SerializableRange_LockTimeoutOnAFencedInsert_RaisesMsg1222()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        _ = writer.CreateCommand("set lock_timeout 0").ExecuteNonQuery();

        var ex = Throws<DbException>(() => writer.CreateCommand("insert t values (22, 9)").ExecuteNonQuery());
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task SerializableTransaction_InsertsIntoItsOwnRange_IsNotSelfBlocked()
    {
        // Same-owner holds are skipped by the conflict check, so the reader's
        // own writes into the interval it fenced go through.
        var sim = KeyedTable();
        using var conn = sim.CreateOpenConnection();

        _ = conn.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(conn, "insert t values (22, 9)");

        _ = conn.CreateCommand("commit tran").ExecuteNonQuery();
        AreEqual(4, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public async Task SerializableRange_AfterTheSameTextRanUnderReadCommitted_StillFences()
    {
        // The first run is a cacheable standalone SELECT, so its plan lands in
        // the per-Simulation cache. Replaying that plan would carry the first
        // session's (READ COMMITTED) lock decisions, leaving the SERIALIZABLE
        // run unfenced — which is why a non-default isolation level skips the
        // cache and re-parses.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("select count(*) from t where k between 15 and 25").ExecuteScalar();
        _ = reader.CreateCommand("set transaction isolation level serializable").ExecuteNonQuery();
        _ = reader.CreateCommand("begin tran").ExecuteNonQuery();
        _ = reader.CreateCommand("select count(*) from t where k between 15 and 25").ExecuteScalar();

        await AssertProceeds(writer, "insert t values (26, 9)");
        await AssertBlocksUntil(reader, writer, "insert t values (22, 8)", "rollback tran");
    }

    [TestMethod]
    public async Task CompositePrefixAndRange_InsertInsideTheTupleInterval_Blocks()
    {
        // `a = 1 AND b between 2 and 5` fences the tuple interval
        // [(1,2), (1,5)] on the (a, b) key — probed against real, which range
        // locks the three keys the predicate spans.
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where a = 1 and b between 2 and 5").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert ck values (1, 3, 900)", "rollback tran");
    }

    [TestMethod]
    public async Task CompositePrefixAndRange_InsertUnderADifferentLeadingValue_DoesNotBlock()
    {
        // The payoff of the tuple fence: (2, 3) carries a `b` inside the
        // second column's interval but a different `a`, so it sits outside the
        // lexicographic interval entirely. Probed — real admits it too, while
        // it blocks (1, 3).
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where a = 1 and b between 2 and 5").ExecuteScalar();
        await AssertProceeds(writer, "insert ck values (2, 3, 901)");

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task CompositeFullTupleEquality_FencesExactlyThatTuple()
    {
        // `a = 1 AND b = 3` matches nothing, so only the fence makes the
        // phantom impossible — and it is the single tuple, not the whole `a = 1`
        // group.
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        AreEqual(0, reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where a = 1 and b = 3").ExecuteScalar());
        await AssertProceeds(writer, "insert ck values (1, 4, 902)");
        await AssertBlocksUntil(reader, writer, "insert ck values (1, 3, 903)", "rollback tran");
    }

    [TestMethod]
    public async Task CompositePrefixOnly_FencesTheWholeGroupAndNothingElse()
    {
        // `a = 1` pins only the leading column, so the interval leaves `b`
        // open: every row under `a = 1` is fenced and every other `a` is free.
        // Probed on both sides.
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where a = 1").ExecuteScalar();
        await AssertProceeds(writer, "insert ck values (2, 5, 904)");
        await AssertBlocksUntil(reader, writer, "insert ck values (1, 100, 905)", "rollback tran");
    }

    [TestMethod]
    public async Task CompositePrefix_UpdateMovingARowIntoTheTupleInterval_Blocks()
    {
        // The post-update image is the only one that reveals the phantom, and
        // the tuple probe has to read both key columns to see it.
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where a = 1 and b between 2 and 5").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "update ck set a = 1, b = 4 where a = 3 and b = 1", "rollback tran");
    }

    [TestMethod]
    public async Task PredicateOnTheSecondKeyColumnOnly_FallsBackToWholeTable()
    {
        // No leading bound, so there is no prefix to fence — real degenerates
        // to range-locking every key plus infinity, which is the whole key
        // space the table-S covers here.
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where b = 2").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert ck values (9, 9, 906)", "rollback tran");
    }

    [TestMethod]
    public void DmTranLocks_ProjectsACompositeRange_WithTheTupleInterval()
    {
        var sim = CompositeKeyedTable();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from ck where a = 1 and b between 2 and 5").ExecuteScalar();

        AreEqual("0,1:[(1,2),(1,5)]", reader.CreateCommand("""
            select resource_description from sys.dm_tran_locks
            where resource_type = 'KEY' and request_mode = 'RangeS-S'
            """).ExecuteScalar());

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public void DmTranLocks_ProjectsAnUpdLockSerializableRead_AsRangeSU()
    {
        // Probed: SERIALIZABLE + UPDLOCK reports RangeS-U at the key and IX at
        // the object, where the same read without SERIALIZABLE reports a plain
        // key U.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 15 and 25").ExecuteScalar();

        AreEqual(1, reader.CreateCommand("""
            select count(*) from sys.dm_tran_locks
            where resource_type = 'KEY' and request_mode = 'RangeS-U' and request_status = 'GRANT'
            """).ExecuteScalar());

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
        AreEqual(0, reader.CreateCommand("select count(*) from sys.dm_tran_locks where resource_type = 'KEY'").ExecuteScalar());
    }

    [TestMethod]
    public void DmTranLocks_ProjectsAnXLockSerializableRead_AsRangeXX()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select v from t with (xlock) where k between 15 and 25").ExecuteScalar();

        AreEqual(1, reader.CreateCommand("""
            select count(*) from sys.dm_tran_locks
            where resource_type = 'KEY' and request_mode = 'RangeX-X' and request_status = 'GRANT'
            """).ExecuteScalar());

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public void UpdLockUnderReadCommitted_TakesNoRange()
    {
        // The isolation level is what turns the hint's row mode into a range —
        // UPDLOCK on its own keeps the row-U plan and interns no range.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("begin tran; select v from t with (updlock) where k between 15 and 25").ExecuteScalar();

        AreEqual(0, reader.CreateCommand("select count(*) from sys.dm_tran_locks where resource_type = 'KEY'").ExecuteScalar());

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public void HoldLockWithUpdLock_UnderReadCommitted_TakesRangeSU()
    {
        // HOLDLOCK is SERIALIZABLE for the statement it sits on, so it reaches
        // the same RangeS-U the session-level setting does.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        _ = reader.CreateCommand("begin tran; select v from t with (updlock, holdlock) where k between 15 and 25").ExecuteScalar();

        AreEqual(1, reader.CreateCommand("""
            select count(*) from sys.dm_tran_locks where resource_type = 'KEY' and request_mode = 'RangeS-U'
            """).ExecuteScalar());

        _ = reader.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task RangeSU_FencesAConcurrentInsert_LikeRangeSS()
    {
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(writer, "insert t values (26, 9)");
        await AssertBlocksUntil(reader, writer, "insert t values (22, 8)", "rollback tran");
    }

    [TestMethod]
    public async Task TwoRangeSUReaders_OfTheSameInterval_BlockEachOther()
    {
        // RangeS-U × RangeS-U conflicts where RangeS-S × RangeS-S doesn't —
        // probed, where the second UPDLOCK reader of the same interval waits.
        var sim = KeyedTable();
        using var readerA = sim.CreateOpenConnection();
        using var readerB = sim.CreateOpenConnection();

        _ = readerA.CreateCommand("set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(
            readerA,
            readerB,
            "set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 15 and 25",
            "rollback tran");

        _ = readerB.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task RangeSU_AndRangeSS_OverTheSameInterval_Coexist()
    {
        var sim = KeyedTable();
        using var readerA = sim.CreateOpenConnection();
        using var readerB = sim.CreateOpenConnection();

        _ = readerA.CreateCommand("set transaction isolation level serializable; begin tran; select v from t with (updlock) where k between 15 and 25").ExecuteScalar();
        await AssertProceeds(readerB, "set transaction isolation level serializable; begin tran; select v from t where k between 15 and 25");

        _ = readerB.CreateCommand("rollback tran").ExecuteNonQuery();
        _ = readerA.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task RangeXX_BlocksAPlainSerializableReaderOfTheSameInterval()
    {
        // RangeX-X conflicts with every other range mode, RangeS-S included —
        // probed, where the plain SERIALIZABLE reader waits behind the XLOCK
        // holder.
        var sim = KeyedTable();
        using var readerA = sim.CreateOpenConnection();
        using var readerB = sim.CreateOpenConnection();

        _ = readerA.CreateCommand("set transaction isolation level serializable; begin tran; select v from t with (xlock) where k between 15 and 25").ExecuteScalar();
        await AssertBlocksUntil(
            readerA,
            readerB,
            "set transaction isolation level serializable; begin tran; select v from t where k between 15 and 25",
            "rollback tran");

        _ = readerB.CreateCommand("rollback tran").ExecuteNonQuery();
    }

    [TestMethod]
    public async Task SerializableUpdLockOnAnUnindexedColumn_FallsBackToWholeTable()
    {
        // No key space to fence, so the fence is the whole-table S the plain
        // SERIALIZABLE reader falls back to — real range-locks every key plus
        // infinity here, which covers the same value space.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select k from t with (updlock) where v = 2").ExecuteScalar();
        AreEqual(0, reader.CreateCommand("select count(*) from sys.dm_tran_locks where resource_type = 'KEY'").ExecuteScalar());
        await AssertBlocksUntil(reader, writer, "insert t values (999, 9)", "rollback tran");
    }

    [TestMethod]
    public async Task SerializableRange_ReleasesOnRollback_UnblockingTheWriter()
    {
        // Ranges are transaction-scoped like every other SERIALIZABLE hold:
        // the statement ending doesn't release them, the transaction does.
        var sim = KeyedTable();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = reader.CreateCommand("set transaction isolation level serializable; begin tran; select count(*) from t where k between 15 and 25").ExecuteScalar();
        // A second, unrelated statement on the same transaction proves the
        // range survived the first statement's end.
        _ = reader.CreateCommand("select 1").ExecuteScalar();
        await AssertBlocksUntil(reader, writer, "insert t values (22, 9)", "rollback tran");
    }
}
