using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The key locks a SERIALIZABLE access takes, anchored on index keys the way
/// real's are, and the blocking matrix they produce against a second session.
/// Each case runs the holder's statement in an open transaction, then the
/// probe under <c>SET LOCK_TIMEOUT 0</c> so a block reads as Msg 1222 rather
/// than a wait. Every expectation was probed 2026-09-28 against SQL Server
/// 2025 with the same two-session shape.
/// </summary>
[TestClass]
public sealed class KeyLockAnchorTests
{
    private const string Keys = "create table t (k int not null primary key, v int not null); insert t values (10, 1), (20, 2), (30, 3), (40, 4);";

    private const string Serializable = "set transaction isolation level serializable; begin tran; ";

    // Runs `holder` on one connection inside an open transaction, then `probe`
    // on a second; the probe's error number, or 0 when it went through.
    private static int Probe(string setup, string holder, string probe)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(setup);
        using var holding = sim.CreateOpenConnection();
        using var probing = sim.CreateOpenConnection();
        _ = holding.CreateCommand(holder.StartsWith("set ", StringComparison.Ordinal) || holder.StartsWith("begin", StringComparison.Ordinal) ? holder : "begin tran; " + holder).ExecuteNonQuery();
        try
        {
            _ = probing.CreateCommand("set lock_timeout 0; begin tran; " + probe).ExecuteNonQuery();
            return 0;
        }
        catch (SimulatedSqlException ex)
        {
            return ex.Number;
        }
        finally
        {
            _ = probing.CreateCommand("if @@trancount > 0 rollback").ExecuteNonQuery();
            _ = holding.CreateCommand("if @@trancount > 0 rollback").ExecuteNonQuery();
        }
    }

    // What sys.dm_tran_locks reports for the holder's session after `holder`:
    // "type mode" pairs with counts, sorted, excluding the database lock.
    private static string Locks(string setup, string holder)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(setup);
        using var holding = sim.CreateOpenConnection();
        _ = holding.CreateCommand(holder).ExecuteNonQuery();
        var locks = (string?)holding.CreateCommand("""
            select string_agg(concat(resource_type, ' ', request_mode, ' x', n), ', ') within group (order by resource_type, request_mode)
            from (select resource_type, request_mode, count(*) n from sys.dm_tran_locks where request_session_id = @@spid and resource_type <> 'DATABASE' group by resource_type, request_mode) g
            """).ExecuteScalar() ?? "";
        _ = holding.CreateCommand("if @@trancount > 0 rollback").ExecuteNonQuery();
        return locks;
    }

    /// <summary>
    /// A range locks the keys inside it and the next key past it, whose lock
    /// covers the gap down to the key below — so an insert anywhere between 10
    /// and 30 waits, as does a write of the next key's own row, while past the
    /// last locked key everything goes through.
    /// </summary>
    [TestMethod]
    [DataRow("insert t values (12, 0)", 1222)]
    [DataRow("insert t values (28, 0)", 1222)]
    [DataRow("insert t values (35, 0)", 0)]
    [DataRow("insert t values (5, 0)", 0)]
    [DataRow("update t set v = 9 where k = 30", 1222)]
    [DataRow("update t set v = 9 where k = 10", 0)]
    [DataRow("select * from t with (updlock) where k = 30", 0)]
    [DataRow("select * from t with (xlock) where k = 30", 1222)]
    public void Between_LocksItsKeysAndTheNextKey(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "select * from t where k between 15 and 25", probe));

    [TestMethod]
    public void Between_ReportsOneKeyLockPerKey()
        => AreEqual("KEY RangeS-S x2, OBJECT IS x1", Locks(Keys, Serializable + "select * from t where k between 15 and 25"));

    /// <summary>An equality miss locks the next key, fencing the whole gap it sits in.</summary>
    [TestMethod]
    [DataRow("insert t values (21, 0)", 1222)]
    [DataRow("insert t values (29, 0)", 1222)]
    [DataRow("insert t values (31, 0)", 0)]
    [DataRow("update t set v = 9 where k = 30", 1222)]
    [DataRow("update t set v = 9 where k = 20", 0)]
    public void UniqueEqualityMiss_LocksTheNextKey(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "select * from t where k = 25", probe));

    /// <summary>An equality hit on a unique key is a plain key S: the neighbors stay insertable.</summary>
    [TestMethod]
    [DataRow("insert t values (15, 0)", 0)]
    [DataRow("insert t values (25, 0)", 0)]
    [DataRow("update t set v = 9 where k = 20", 1222)]
    [DataRow("select * from t with (updlock) where k = 20", 0)]
    public void UniqueEqualityHit_TakesAPlainKeyLock(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "select * from t where k = 20", probe));

    [TestMethod]
    public void UniqueEqualityHit_ReportsKeyS()
        => AreEqual("KEY S x1, OBJECT IS x1", Locks(Keys, Serializable + "select * from t where k = 20"));

    /// <summary>
    /// An IN list locks per value — hits take plain key locks, a miss its next
    /// key — so the gaps between listed hits stay free, where a hull would
    /// have fenced them.
    /// </summary>
    [TestMethod]
    [DataRow("insert t values (15, 0)", 0)]
    [DataRow("insert t values (25, 0)", 0)]
    [DataRow("insert t values (33, 0)", 1222)]
    [DataRow("insert t values (38, 0)", 1222)]
    [DataRow("update t set v = 9 where k = 40", 1222)]
    [DataRow("update t set v = 9 where k = 20", 0)]
    public void InList_LocksPerValue(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "select * from t where k in (10, 30, 35)", probe));

    /// <summary>An open range past the last key locks the infinity anchor, and so every insert above.</summary>
    [TestMethod]
    [DataRow("insert t values (36, 0)", 1222)]
    [DataRow("insert t values (1000, 0)", 1222)]
    [DataRow("insert t values (32, 0)", 1222)]
    [DataRow("update t set v = 9 where k = 30", 0)]
    public void OpenRange_LocksTheInfinityAnchor(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "select * from t where k > 35", probe));

    [TestMethod]
    public void EmptyTable_LocksTheInfinityAnchor()
    {
        AreEqual(1222, Probe("create table t (k int primary key, v int)", Serializable + "select * from t where k = 5", "insert t values (100, 0)"));
        AreEqual("KEY RangeS-S x1, OBJECT IS x1", Locks("create table t (k int primary key, v int)", Serializable + "select * from t where k = 5"));
    }

    /// <summary>
    /// A read with no interval to narrow to — no predicate, a non-sargable
    /// one, one on a non-leading column, a cross-column OR — locks every key
    /// and the infinity anchor rather than the table: an <c>UPDLOCK</c> read
    /// of a row and a write that finds no row still go through.
    /// </summary>
    [TestMethod]
    [DataRow("select * from t")]
    [DataRow("select * from t where v = 2")]
    [DataRow("select * from t where k = 20 or v = 4")]
    public void Scan_LocksEveryKeyNotTheTable(string read)
    {
        AreEqual(1222, Probe(Keys, Serializable + read, "insert t values (50, 0)"));
        AreEqual(1222, Probe(Keys, Serializable + read, "update t set v = 9 where k = 30"));
        AreEqual(0, Probe(Keys, Serializable + read, "update t set v = 9 where k = 35"));
        AreEqual(0, Probe(Keys, Serializable + read, "select * from t with (updlock) where k = 40"));
        AreEqual("KEY RangeS-S x5, OBJECT IS x1", Locks(Keys, Serializable + read));
    }

    /// <summary>A heap has no key to walk, so its scan takes the table S, with no IS beside it.</summary>
    [TestMethod]
    public void HeapScan_TakesTableS()
    {
        const string heap = "create table t (k int, v int); insert t values (10, 1), (20, 2)";
        AreEqual("OBJECT S x1", Locks(heap, Serializable + "select * from t where k = 20"));
        AreEqual(1222, Probe(heap, Serializable + "select * from t where k = 20", "select * from t with (updlock) where k = 10"));
    }

    /// <summary>
    /// Through a nonclustered index: the index keys it reads and the next one,
    /// plus a row S on each row it looks up. An update of a column the index
    /// doesn't carry doesn't touch the index, so the next key's row stays
    /// writable; an update of the indexed column is refused.
    /// </summary>
    [TestMethod]
    [DataRow("insert t values (5000, 205, 0)", 1222)]
    [DataRow("insert t values (5001, 195, 0)", 1222)]
    [DataRow("insert t values (5002, 215, 0)", 0)]
    [DataRow("update t set v = 9 where k = 20", 1222)]
    [DataRow("update t set v = 9 where k = 21", 0)]
    [DataRow("update t set c = 999999 where k = 21", 1222)]
    [DataRow("select * from t with (updlock) where k = 20", 0)]
    public void NonclusteredSeek_LocksIndexKeysAndLookups(string probe, int expected)
        => AreEqual(expected, Probe(
            "create table t (k int primary key, c int, v int, index ic (c)); insert t select value, value * 10, 0 from generate_series(1, 200)",
            Serializable + "select * from t where c = 200",
            probe));

    /// <summary>
    /// A composite prefix locks every key under it and the next key past, so
    /// the gap reaches down to the last key of the group before.
    /// </summary>
    [TestMethod]
    [DataRow("insert t values (1, 10, 0)", 1222)]
    [DataRow("insert t values (2, 3, 0)", 1222)]
    [DataRow("insert t values (3, 0, 0)", 1222)]
    [DataRow("insert t values (3, 2, 0)", 0)]
    [DataRow("update t set v = 1 where a = 3 and b = 1", 1222)]
    [DataRow("update t set v = 1 where a = 1 and b = 9", 0)]
    public void CompositePrefix_LocksTheGroupAndTheNextKey(string probe, int expected)
        => AreEqual(expected, Probe(
            "create table t (a int, b int, v int, primary key (a, b)); insert t values (1,1,0),(1,5,0),(1,9,0),(2,1,0),(2,5,0),(3,1,0)",
            Serializable + "select * from t where a = 2",
            probe));

    /// <summary>
    /// Two <c>UPDLOCK</c> readers whose ranges share only the next key meet
    /// there, as on real: RangeS-U on one key refuses a second.
    /// </summary>
    [TestMethod]
    [DataRow("set transaction isolation level serializable; select * from t with (updlock) where k between 26 and 29", 1222)]
    [DataRow("set transaction isolation level serializable; select * from t with (updlock) where k between 31 and 35", 0)]
    [DataRow("set transaction isolation level serializable; select * from t where k between 26 and 29", 0)]
    [DataRow("select * from t with (updlock) where k = 30", 1222)]
    [DataRow("select * from t where k = 30", 0)]
    public void UpdLockReaders_MeetOnASharedNextKey(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "select * from t with (updlock) where k between 15 and 25", probe));

    /// <summary>
    /// A SERIALIZABLE UPDATE takes RangeX-X on the keys it reaches and the
    /// next one, which refuses a READ COMMITTED read of the next key's row —
    /// though an <c>XLOCK</c> read's identical lock doesn't, nor a DELETE's
    /// that removed nothing, since real's read takes its S only on data
    /// changed while the lock's transaction has been open.
    /// </summary>
    [TestMethod]
    [DataRow("update t set v = v + 1 where k between 15 and 25", 1222)]
    [DataRow("select * from t with (xlock) where k between 15 and 25", 0)]
    [DataRow("delete t where k = 25", 0)]
    public void RangeXX_RefusesAReadOfTheNextKey_OnceTheTableChanged(string holder, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + holder, "select * from t where k = 30"));

    [TestMethod]
    public void SerializableUpdate_ReportsRangeXXOnEachKey_TheRowLocksFolded()
        => AreEqual("KEY RangeX-X x2, OBJECT IX x1", Locks(Keys, Serializable + "update t set v = v + 1 where k between 15 and 25"));

    [TestMethod]
    public void SerializableUpdateThatScans_ReportsRangeSUAndTheUpdatedKeyAsRangeXX()
        => AreEqual("KEY RangeS-U x4, KEY RangeX-X x1, OBJECT IX x1", Locks(Keys, Serializable + "update t set v = v + 1 where v = 3"));

    /// <summary>A SERIALIZABLE write that hits a unique key takes its row X and no range.</summary>
    [TestMethod]
    [DataRow("insert t values (15, 0)", 0)]
    [DataRow("insert t values (25, 0)", 0)]
    [DataRow("select * from t with (updlock) where k = 30", 0)]
    public void SerializableUniqueHitWrite_TakesNoRange(string probe, int expected)
        => AreEqual(expected, Probe(Keys, Serializable + "update t set v = v + 1 where k = 20", probe));

    /// <summary>
    /// An insert into a gap its own transaction range-locks splits the gap,
    /// and the new key takes RangeX-X to keep the lower half fenced — a
    /// HOLDLOCK MERGE's upsert of a missing key.
    /// </summary>
    [TestMethod]
    public void InsertIntoOwnRange_TakesRangeXXOnTheNewKey()
    {
        const string merge = "begin tran; merge t with (holdlock) using (select 25 k) s on t.k = s.k when matched then update set v = 1 when not matched then insert values (s.k, 0);";
        AreEqual(1222, Probe(Keys, merge, "insert t values (22, 0)"));
        AreEqual(0, Probe(Keys, merge, "insert t values (35, 0)"));
        AreEqual("KEY RangeS-U x1, KEY RangeX-X x1, OBJECT IX x1", Locks(Keys, merge));
    }

    /// <summary>A HOLDLOCK MERGE that finds its unique key takes only the row X, as a writer does.</summary>
    [TestMethod]
    public void HoldlockMergeHit_TakesNoRange()
    {
        const string merge = "begin tran; merge t with (holdlock) using (select 20 k) s on t.k = s.k when matched then update set v = 1 when not matched then insert values (s.k, 0);";
        AreEqual(0, Probe(Keys, merge, "insert t values (15, 0)"));
        AreEqual("KEY X x1, OBJECT IX x1", Locks(Keys, merge));
    }

    /// <summary>
    /// A tx-scoped row lock is kept only on the rows the read returns: real
    /// takes it to read each row and lets it go on one that doesn't qualify.
    /// </summary>
    [TestMethod]
    [DataRow("begin tran; select * from t with (updlock) where k = 20", "KEY U x1, OBJECT IX x1")]
    [DataRow("begin tran; select * from t with (updlock) where v = 2", "KEY U x1, OBJECT IX x1")]
    [DataRow("set transaction isolation level repeatable read; begin tran; select * from t where v = 2", "KEY S x1, OBJECT IS x1")]
    [DataRow("begin tran; select * from t with (xlock) where k = 20", "KEY X x1, OBJECT IX x1")]
    public void TxScopedRowLocks_KeepOnlyTheQualifyingRows(string holder, string expected)
        => AreEqual(expected, Locks(Keys, holder));

    [TestMethod]
    public void UpdLockRead_LeavesTheOtherRowsWritable()
        => AreEqual(0, Probe(Keys, "select * from t with (updlock) where k = 20", "update t set v = 0 where k = 40"));

    /// <summary>
    /// A statement past real's escalation point trades its key locks for one
    /// table lock — S for a read, X once a U or X is among them — while two
    /// statements under it each keep theirs, the count being per statement.
    /// </summary>
    [TestMethod]
    [DataRow(6300, "set transaction isolation level serializable; begin tran; select count(*) from t", "OBJECT S x1")]
    [DataRow(6200, "set transaction isolation level serializable; begin tran; select count(*) from t", "KEY RangeS-S x6201, OBJECT IS x1")]
    [DataRow(7000, "set transaction isolation level serializable; begin tran; select count(*) from t with (updlock)", "OBJECT X x1")]
    [DataRow(6300, "set transaction isolation level repeatable read; begin tran; select count(*) from t", "OBJECT S x1")]
    [DataRow(8000, "set transaction isolation level serializable; begin tran; select count(*) from t where k <= 40000; select count(*) from t where k > 40000", "KEY RangeS-S x8001, OBJECT IS x1")]
    public void Escalation_FollowsRealsThresholdAndMode(int rows, string holder, string expected)
        => AreEqual(expected, Locks($"create table t (k int primary key, v int); insert t select value * 10, 0 from generate_series(1, {rows})", holder));

    /// <summary>A row lock of a clustered table is its key, which real reports as KEY.</summary>
    [TestMethod]
    public void ClusteredRowLock_ReportsAsKey()
        => AreEqual("KEY X x1, OBJECT IX x1", Locks(Keys, "begin tran; update t set v = 0 where k = 10"));

    /// <summary>A NOWAIT on an INSERT target zeroes the wait on a held range too.</summary>
    [TestMethod]
    public void InsertNowait_FailsAtOnceOnAHeldRange()
        => AreEqual(1222, Probe(Keys, Serializable + "select * from t where k = 25", "set lock_timeout -1; insert t with (nowait) values (22, 0)"));
}
