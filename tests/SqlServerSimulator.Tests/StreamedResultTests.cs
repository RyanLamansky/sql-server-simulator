using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>SELECT</c> sends its rows as its client reads them, running about
/// 40,000 bytes ahead of an in-process reader — the packet the client is in
/// plus the four a MARS connection grants — and holding, while it waits on
/// the reader, what real holds at that position (probed 2026-10-08 against
/// SQL Server 2025 with a reader two rows into 2,000 and 20,000 rows of
/// <c>char(2000)</c>): <c>IS</c> on the table under <c>READ COMMITTED</c>,
/// which keeps a redefinition out while writers go on, and S on the row the
/// scan stands on; the key locks of the
/// rows produced so far under <c>REPEATABLE READ</c>; <c>Sch-S</c> alone under
/// <c>NOLOCK</c> and the versioned levels. The request reads as
/// <c>suspended</c> on <c>ASYNC_NETWORK_IO</c>, and a row an uncommitted
/// writer holds stops the reader when it gets there.
/// </summary>
[TestClass]
public sealed class StreamedResultTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int Rows = 2000;

    private static Simulation Big(string extra = "")
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table big (k int primary key, v char(2000) not null);
            insert big select value, 'x' from generate_series(1, {Rows});
            create table heap (k int not null, v char(2000) not null);
            insert heap select value, 'x' from generate_series(1, {Rows});
            {extra}
            """);
        return sim;
    }

    private static short Spid(DbConnection connection) => (short)connection.CreateCommand("select @@spid").ExecuteScalar()!;

    /// <summary>The locks <paramref name="spid"/> holds beside its database lock, as <c>TYPE mode×count</c>.</summary>
    private static string Locks(DbConnection observer, short spid) => string.Join(", ", Extensions.FirstColumn(observer, $"""
        select concat(resource_type, ' ', request_mode, iif(count(*) > 1, concat('x', count(*)), ''))
        from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE'
        group by resource_type, request_mode order by resource_type, request_mode
        """));

    private static string Request(DbConnection observer, short spid) => string.Join(", ", Extensions.FirstColumn(observer, $"""
        select concat(status, ' ', wait_type, ' ', command) from sys.dm_exec_requests where session_id = {spid}
        """));

    /// <summary>Runs <paramref name="sql"/> under <c>LOCK_TIMEOUT 0</c> in a transaction rolled back after it: the error number it raised, or 0.</summary>
    private static int Attempt(DbConnection connection, string sql)
    {
        try
        {
            _ = connection.CreateCommand($"set lock_timeout 0; begin tran; {sql}; if @@trancount > 0 rollback").ExecuteNonQuery();
            return 0;
        }
        catch (SimulatedSqlException error)
        {
            if ((int)connection.CreateCommand("select @@trancount").ExecuteScalar()! > 0)
                _ = connection.CreateCommand("rollback").ExecuteNonQuery();
            return error.Number;
        }
    }

    private static DbDataReader ReadTwo(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.Read());
        return reader;
    }

    private static int ReadRest(DbDataReader reader, int read = 2)
    {
        while (reader.Read())
            read++;
        return read;
    }

    /// <summary>
    /// A <c>READ COMMITTED</c> reader holds the table's <c>IS</c> and S on the
    /// row its scan stands on — the last it produced, twenty rows of 2,007
    /// wire bytes into the result — so a writer of that row waits while one
    /// of any other goes on; real holds the current page's S instead over a
    /// table ten times the size, its scan locking pages.
    /// </summary>
    [TestMethod]
    public void ReadCommitted_SuspendedReader_HoldsIntentSharedAndItsPosition()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);

        using (var rows = ReadTwo(reader, "select * from big"))
        {
            AreEqual("KEY S, OBJECT IS", Locks(other, spid));
            AreEqual("suspended ASYNC_NETWORK_IO SELECT", Request(other, spid));
            AreEqual(1222, Attempt(other, "alter table big add c int"));
            AreEqual(0, Attempt(other, "update big set v = v where k = 1"));
            AreEqual(1222, Attempt(other, "update big set v = v where k = 20"));
            AreEqual(0, Attempt(other, "update big set v = v where k = 21"));
            AreEqual(0, Attempt(other, $"update big set v = v where k = {Rows - 10}"));
            AreEqual(Rows, ReadRest(rows));
            AreEqual("", Locks(other, spid));
        }
        AreEqual(0, Attempt(other, "alter table big add c int"));
    }

    [TestMethod]
    public void RepeatableRead_SuspendedReader_HoldsTheRowsProducedSoFar()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);

        using (var rows = ReadTwo(reader, "select * from big", transaction))
        {
            AreEqual("KEY Sx20, OBJECT IS", Locks(other, spid));
            AreEqual(1222, Attempt(other, "update big set v = v where k = 1"));
            AreEqual(1222, Attempt(other, "update big set v = v where k = 20"));
            AreEqual(0, Attempt(other, "update big set v = v where k = 21"));
            AreEqual(0, Attempt(other, $"insert big values ({Rows + 1}, 'y')"));
            AreEqual(Rows, ReadRest(rows));
        }
        AreEqual(1222, Attempt(other, $"update big set v = v where k = {Rows - 10}"));
        transaction.Rollback();
    }

    [TestMethod]
    public void RepeatableRead_Heap_HoldsRowLocks() => AreEqual("OBJECT IS, RID Sx20", HeldWhileSuspended(Big(), "select * from heap", IsolationLevel.RepeatableRead, "heap"));

    [TestMethod]
    public void NoLock_HoldsSchemaStabilityAlone() => AreEqual("OBJECT Sch-S", HeldWhileSuspended(Big(), "select * from big with (nolock)", IsolationLevel.ReadCommitted));

    [TestMethod]
    public void ReadCommittedSnapshot_HoldsSchemaStabilityAlone() => AreEqual("OBJECT Sch-S", HeldWhileSuspended(Big("alter database current set read_committed_snapshot on"), "select * from big", IsolationLevel.ReadCommitted));

    [TestMethod]
    public void Snapshot_HoldsSchemaStabilityAlone() => AreEqual("OBJECT Sch-S", HeldWhileSuspended(Big("alter database current set allow_snapshot_isolation on"), "select * from big", IsolationLevel.Snapshot));

    /// <summary>
    /// A sort reads all of its input before its first row goes out, so under
    /// <c>REPEATABLE READ</c> every row is locked at once, while
    /// <c>READ COMMITTED</c> still holds the table's <c>IS</c> until the
    /// sorted rows have gone (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(IsolationLevel.ReadCommitted, "OBJECT IS")]
    [DataRow(IsolationLevel.RepeatableRead, "KEY Sx2000, OBJECT IS")]
    public void Sort_ReadsItsInputBeforeItsFirstRow(IsolationLevel level, string held) =>
        AreEqual(held, HeldWhileSuspended(Big(), "select * from big order by checksum(k)", level));

    /// <summary>
    /// However the read comes to lock its rows to the transaction's end — the
    /// level set by <c>SET TRANSACTION ISOLATION LEVEL</c> and a transaction
    /// begun in SQL, a transaction the API began at that level, or a table
    /// hint — and whether it orders by the clustered key or not, a reader
    /// suspended mid-result holds the rows it has produced and none ahead
    /// (probed 2026-10-08 against SQL Server 2025, whose API-begun
    /// <c>REPEATABLE READ</c> reader of <c>ORDER BY id</c> held the keys of the
    /// rows sent; the simulator's sorted that read, locking every row first).
    /// </summary>
    [TestMethod]
    [DataRow("set", "repeatable read", "", "S")]
    [DataRow("set", "repeatable read", "order by k", "S")]
    [DataRow("api", "repeatable read", "", "S")]
    [DataRow("api", "repeatable read", "order by k", "S")]
    [DataRow("api", "repeatable read", "order by k desc", "S")]
    [DataRow("hint", "repeatableread", "", "S")]
    [DataRow("hint", "repeatableread", "order by k", "S")]
    [DataRow("set", "serializable", "", "RangeS-S")]
    [DataRow("set", "serializable", "order by k", "RangeS-S")]
    [DataRow("api", "serializable", "", "RangeS-S")]
    [DataRow("api", "serializable", "order by k", "RangeS-S")]
    [DataRow("hint", "serializable", "", "RangeS-S")]
    [DataRow("hint", "holdlock", "order by k", "RangeS-S")]
    public void RowLockingRead_HoldsTheRowsProducedHoweverItsLevelCame(string entry, string level, string order, string keyMode)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(100) not null); insert big select value, 'x' from generate_series(1, {EscalatingRows})");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        DbTransaction? transaction = null;
        switch (entry)
        {
            case "set":
                _ = reader.CreateCommand($"set transaction isolation level {level}; begin tran").ExecuteNonQuery();
                break;
            case "api":
                transaction = reader.BeginTransaction(level == "serializable" ? IsolationLevel.Serializable : IsolationLevel.RepeatableRead);
                break;
            default:
                transaction = reader.BeginTransaction();
                break;
        }
        using (var rows = ReadTwo(reader, $"select * from big{(entry == "hint" ? $" with ({level})" : "")} {order}", transaction))
        {
            var keys = (int)other.CreateCommand($"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and resource_type = 'KEY' and request_mode = '{keyMode}'").ExecuteScalar()!;
            IsGreaterThan(2, keys);
            IsLessThan(EscalatingRows / 4, keys);
            AreEqual($"KEY {keyMode}x{keys}, OBJECT IS", Locks(other, spid));
            AreEqual(1222, Attempt(other, order.EndsWith("desc", StringComparison.Ordinal) ? $"update big set v = v where k = {EscalatingRows}" : "update big set v = v where k = 1"));
            AreEqual(0, Attempt(other, $"update big set v = v where k = {EscalatingRows / 2}"));
            AreEqual(EscalatingRows, ReadRest(rows));
        }
        if (transaction is null)
            _ = reader.CreateCommand("rollback").ExecuteNonQuery();
        else
            transaction.Rollback();
    }

    /// <summary>Past real's escalation threshold, so a read that locked every row would show as a table lock.</summary>
    private const int EscalatingRows = 7000;

    private static string HeldWhileSuspended(Simulation sim, string sql, IsolationLevel level, string table = "big")
    {
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = level == IsolationLevel.ReadCommitted ? null : reader.BeginTransaction(level);
        using var rows = ReadTwo(reader, sql, transaction);
        var held = Locks(other, spid);
        AreEqual(1222, Attempt(other, $"alter table {table} add c int"));
        AreEqual(Rows, ReadRest(rows));
        return held;
    }

    /// <summary>
    /// A table hint decides what the suspended reader holds through the same
    /// resolution a statement's locks take, so each hint's footprint is real's
    /// own as of the reader's position (probed 2026-10-08 against SQL Server
    /// 2025 over a MARS connection), less the page intent locks and the
    /// sixteen partitions of an object lock real takes on its host, neither of
    /// which the simulator models — a scan that locks rows holds the S of the
    /// row it stands on, as real's does over a table this size, and
    /// <c>PAGLOCK</c>'s the page's, its pages under <c>REPEATABLE READ</c>
    /// (probed 2026-10-09): <paramref name="rowOne"/> is what an update of
    /// the first row meets, <paramref name="tableX"/> what a <c>TABLOCKX</c>
    /// update of a row ahead meets, and a column added to the table always
    /// waits.
    /// </summary>
    [TestMethod]
    [DataRow(IsolationLevel.ReadCommitted, "", "nolock", "OBJECT Sch-S", 0, 0)]
    [DataRow(IsolationLevel.ReadCommitted, "", "readuncommitted", "OBJECT Sch-S", 0, 0)]
    [DataRow(IsolationLevel.ReadCommitted, "", "tablock", "OBJECT S", 1222, 1222)]
    [DataRow(IsolationLevel.RepeatableRead, "", "tablock", "OBJECT S", 1222, 1222)]
    [DataRow(IsolationLevel.Serializable, "", "tablock", "OBJECT S", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "tablockx", "OBJECT X", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "holdlock", "KEY RangeS-Sx20, OBJECT IS", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "serializable", "KEY RangeS-Sx20, OBJECT IS", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "repeatableread", "KEY Sx20, OBJECT IS", 1222, 1222)]
    [DataRow(IsolationLevel.RepeatableRead, "", "readcommitted", "KEY S, OBJECT IS", 0, 1222)]
    [DataRow(IsolationLevel.Serializable, "", "readcommitted", "KEY S, OBJECT IS", 0, 1222)]
    [DataRow(IsolationLevel.RepeatableRead, "", "readcommittedlock", "KEY S, OBJECT IS", 0, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "updlock", "KEY Ux20, OBJECT IX", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "xlock", "KEY Xx20, OBJECT IX", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "paglock", "OBJECT IS, PAGE S", 0, 1222)]
    [DataRow(IsolationLevel.RepeatableRead, "", "paglock", "OBJECT IS, PAGE Sx5", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "", "rowlock", "KEY S, OBJECT IS", 0, 1222)]
    [DataRow(IsolationLevel.RepeatableRead, "", "rowlock", "KEY Sx20, OBJECT IS", 1222, 1222)]
    [DataRow(IsolationLevel.ReadCommitted, "alter database current set read_committed_snapshot on", "readcommitted", "OBJECT Sch-S", 0, 0)]
    [DataRow(IsolationLevel.ReadCommitted, "alter database current set read_committed_snapshot on", "tablock", "OBJECT Sch-S", 0, 0)]
    [DataRow(IsolationLevel.ReadCommitted, "alter database current set read_committed_snapshot on", "readcommittedlock", "KEY S, OBJECT IS", 0, 1222)]
    public void Hint_DecidesWhatTheSuspendedReaderHolds(IsolationLevel level, string database, string hint, string held, int rowOne, int tableX)
    {
        var sim = Big(database);
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = level == IsolationLevel.ReadCommitted ? null : reader.BeginTransaction(level);
        using var rows = ReadTwo(reader, $"select * from big with ({hint})", transaction);
        AreEqual(held, Locks(other, spid));
        AreEqual(1222, Attempt(other, "alter table big add c int"));
        AreEqual(rowOne, Attempt(other, "update big set v = v where k = 1"));
        AreEqual(tableX, Attempt(other, $"update big with (tablockx) set v = v where k = {Rows - 10}"));
        AreEqual(Rows, ReadRest(rows));
    }

    /// <summary>
    /// <c>READPAST</c> passes a row a writer locked after the read began when
    /// the scan reaches it, and <c>NOWAIT</c> raises Msg 1222 there, the rows
    /// before it gone out (probed 2026-10-08 against SQL Server 2025, whose
    /// row-locked <c>READPAST</c> scan reads 19,999 rows of 20,000).
    /// </summary>
    [TestMethod]
    public void ReadPast_PassesARowLockedAfterTheReadBegan()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        using var rows = ReadTwo(reader, "select * from big with (readpast)");
        _ = writer.CreateCommand("begin tran; update big set v = 'z' where k = 1500").ExecuteNonQuery();
        AreEqual(Rows - 1, ReadRest(rows));
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void NoWait_RefusesARowLockedAfterTheReadBegan()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        using var rows = ReadTwo(reader, "select * from big with (nowait)");
        _ = writer.CreateCommand("begin tran; update big set v = 'z' where k = 1500").ExecuteNonQuery();
        var read = 2;
        AreEqual(1222, ThrowsExactly<SimulatedSqlException>(() =>
        {
            while (rows.Read())
                read++;
        }).Number);
        AreEqual(1499, read);
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// A <c>SERIALIZABLE</c> scan locks each key as it reaches it, so a key
    /// inserted ahead of a suspended reader goes in and the scan reads it when
    /// it gets there, while one behind it waits — no phantom either way, as
    /// the transaction's next count shows (probed 2026-10-08 against SQL
    /// Server 2025: twenty RangeS-S held, an insert past the last key going
    /// ahead).
    /// </summary>
    [TestMethod]
    public void Serializable_ScanLocksKeysAsItReachesThem()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(2000) not null); insert big select value * 2, 'x' from generate_series(1, {Rows})");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.Serializable);
        using (var rows = ReadTwo(reader, "select * from big", transaction))
        {
            AreEqual("KEY RangeS-Sx20, OBJECT IS", Locks(other, spid));
            AreEqual(1222, Attempt(other, "insert big values (5, 'y')"));
            _ = other.CreateCommand($"insert big values (3001, 'y'), ({(Rows * 2) + 7}, 'y')").ExecuteNonQuery();
            AreEqual(Rows + 2, ReadRest(rows));
        }
        AreEqual(1222, Attempt(other, $"insert big values ({(Rows * 2) + 9}, 'y')"));
        using var count = reader.CreateCommand("select count(*) from big");
        count.Transaction = transaction;
        AreEqual(Rows + 2, count.ExecuteScalar());
    }

    /// <summary>
    /// A <c>SERIALIZABLE</c> scan filtered by a predicate no key seeks on, or
    /// taking <c>UPDLOCK</c> / <c>XLOCK</c>, locks each key as it reaches it in
    /// its range mode too — every key it passes, a row its predicate rejects
    /// included — so a suspended reader holds the ranges behind it alone and
    /// an insert or update ahead goes in (probed 2026-10-08 against SQL Server
    /// 2025: twenty keys for twenty rows sent, sixty for a predicate keeping
    /// one row in three).
    /// </summary>
    [TestMethod]
    [DataRow("", "where v like 'x%'", "RangeS-S", 20, "IS")]
    [DataRow("", "where k % 6 = 0", "RangeS-S", 60, "IS")]
    [DataRow(", updlock", "", "RangeS-U", 20, "IX")]
    [DataRow(", xlock", "", "RangeX-X", 20, "IX")]
    public void Serializable_FilteredOrUpdatingScan_LocksKeysAsItReachesThem(string hint, string where, string mode, int keys, string objectMode)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(2000) not null); insert big select value * 2, 'x' from generate_series(1, {Rows})");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction();
        using (var rows = ReadTwo(reader, $"select * from big with (serializable{hint}) {where}", transaction))
        {
            AreEqual($"KEY {mode}x{keys}, OBJECT {objectMode}", Locks(other, spid));
            AreEqual(1222, Attempt(other, "insert big values (5, 'y')"));
            AreEqual(0, Attempt(other, $"insert big values ({Rows + 1}, 'y')"));
            AreEqual(0, Attempt(other, $"update big set v = 'y' where k = {Rows}"));
            _ = ReadRest(rows);
        }
        AreEqual(1222, Attempt(other, $"insert big values ({Rows + 1}, 'y')"));
        transaction.Rollback();
    }

    /// <summary>
    /// A <c>SERIALIZABLE</c> seek — a range either way, an <c>IN</c> list, a
    /// descending scan — locks each key as it reaches it, so a reader suspended
    /// two rows in holds twenty keys, a write behind it waits and one ahead goes
    /// in and is read when the seek gets there. Past its last row an ascending
    /// read locks the key after its range, a descending one the key below it,
    /// and a descending scan takes the infinity anchor first (probed 2026-10-09
    /// against SQL Server 2025, every row).
    /// </summary>
    [TestMethod]
    [DataRow("where k between 400 and 3600", "KEY RangeS-Sx20, OBJECT IS", "insert big values (401, 'y')", "insert big values (3001, 'y')", 1602, "KEY RangeS-Sx1603, OBJECT IS")]
    [DataRow("where k between 400 and 3600 order by k desc", "KEY RangeS-Sx20, OBJECT IS", "insert big values (3599, 'y')", "insert big values (401, 'y')", 1602, "KEY RangeS-Sx1603, OBJECT IS")]
    [DataRow("where k between 400 and 3601 order by k desc", "KEY RangeS-Sx21, OBJECT IS", "insert big values (3601, 'y')", "insert big values (3001, 'y')", 1602, "KEY RangeS-Sx1604, OBJECT IS")]
    [DataRow("where k > 400 and k < 3600 order by k desc", "KEY RangeS-Sx21, OBJECT IS", "insert big values (3599, 'y')", "insert big values (401, 'y')", 1600, "KEY RangeS-Sx1602, OBJECT IS")]
    [DataRow("where k between 400 and 9000 order by k desc", "KEY RangeS-Sx21, OBJECT IS", "insert big values (4001, 'y')", "insert big values (401, 'y')", 1802, "KEY RangeS-Sx1804, OBJECT IS")]
    [DataRow("order by k desc", "KEY RangeS-Sx21, OBJECT IS", "insert big values (4001, 'y')", "insert big values (3, 'y')", 2001, "KEY RangeS-Sx2002, OBJECT IS")]
    [DataRow("where k > 400 order by k", "KEY RangeS-Sx20, OBJECT IS", "insert big values (401, 'y')", "insert big values (4001, 'y')", 1801, "KEY RangeS-Sx1802, OBJECT IS")]
    [DataRow("where k in (40, 80, 120, 160, 200, 240, 280, 320, 360, 400, 440, 480, 520, 560, 600, 640, 680, 720, 760, 800, 840, 880, 920, 960, 1000) order by k", "KEY Sx20, OBJECT IS", "update big set v = 'y' where k = 80", "update big set v = 'y' where k = 1000", 25, "KEY Sx25, OBJECT IS")]
    [DataRow("where k in (40, 80, 120, 160, 200, 240, 280, 320, 360, 400, 440, 480, 520, 560, 600, 640, 680, 720, 760, 800, 840, 880, 920, 960, 1000) order by k desc", "KEY Sx20, OBJECT IS", "update big set v = 'y' where k = 1000", "update big set v = 'y' where k = 40", 25, "KEY Sx25, OBJECT IS")]
    [DataRow("with (updlock) where k between 400 and 3600", "KEY RangeS-Ux20, OBJECT IX", "insert big values (401, 'y')", "insert big values (3001, 'y')", 1602, "KEY RangeS-Ux1603, OBJECT IX")]
    [DataRow("with (xlock) where k between 400 and 3600 order by k desc", "KEY RangeX-Xx20, OBJECT IX", "insert big values (3599, 'y')", "insert big values (401, 'y')", 1602, "KEY RangeX-Xx1603, OBJECT IX")]
    public void Serializable_SeekLocksKeysAsItReachesThem(string query, string held, string blocked, string free, int read, string heldAtEnd)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(2000) not null); insert big select value * 2, 'x' from generate_series(1, {Rows})");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.Serializable);
        using (var rows = ReadTwo(reader, $"select * from big {query}", transaction))
        {
            AreEqual(held, Locks(other, spid));
            AreEqual(1222, Attempt(other, blocked));
            _ = other.CreateCommand(free).ExecuteNonQuery();
            AreEqual(read, ReadRest(rows));
        }
        AreEqual(heldAtEnd, Locks(other, spid));
        transaction.Rollback();
    }

    /// <summary>
    /// A <c>SERIALIZABLE</c> seek of an equality prefix of a composite key, and
    /// one through a nonclustered index — whose key locks real counts toward
    /// escalation apart from its rows' — lock as they reach each key too, the
    /// nonclustered seek taking each row's S as it looks it up (probed
    /// 2026-10-09 against SQL Server 2025: 3,603 range locks and 3,602 row
    /// locks held at the end, unescalated).
    /// </summary>
    [TestMethod]
    public void Serializable_PrefixAndNonclusteredSeeks_LockKeysAsTheyReachThem()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table c (a int not null, b int not null, v char(2000) not null default 'x', primary key (a, b));
            insert c (a, b) select a.value, b.value * 2 from generate_series(1, 9) a cross join generate_series(1, 300) b;
            create table n (k int primary key, v int not null, pad char(2000) not null default 'x', index ixv (v));
            insert n (k, v) select value * 2, value * 2 from generate_series(1, 4000);
            """);
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.Serializable);
        using (var rows = ReadTwo(reader, "select * from c where a = 5 order by a, b", transaction))
        {
            AreEqual("KEY RangeS-Sx20, OBJECT IS", Locks(other, spid));
            AreEqual(1222, Attempt(other, "insert c (a, b) values (5, 3)"));
            AreEqual(1222, Attempt(other, "insert c (a, b) values (4, 601)"));
            _ = other.CreateCommand("insert c (a, b) values (5, 599)").ExecuteNonQuery();
            AreEqual(301, ReadRest(rows));
        }
        AreEqual("KEY RangeS-Sx302, OBJECT IS", Locks(other, spid));
        using (var rows = ReadTwo(reader, "select * from n with (index (ixv)) where v between 400 and 7600", transaction))
        {
            AreEqual("KEY RangeS-Sx322, KEY Sx20, OBJECT ISx2", Locks(other, spid));
            AreEqual(1222, Attempt(other, "update n set pad = 'y' where k = 402"));
            AreEqual(0, Attempt(other, "update n set pad = 'y' where k = 7000"));
            AreEqual(3601, ReadRest(rows));
        }
        AreEqual("KEY RangeS-Sx3904, KEY Sx3601, OBJECT ISx2", Locks(other, spid));
        transaction.Rollback();
    }

    /// <summary>
    /// A procedure's body streams its rows whether a batch's <c>EXEC</c> or an
    /// RPC calls it, and the call holds no lock on the procedure once its body
    /// runs — real takes its Sch-S only to compile — so another session drops
    /// it while the reader is suspended in it (probed 2026-10-09 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProcedureBody_StreamsAndHoldsNoLockOnTheProcedure(bool rpc)
    {
        var sim = Big();
        _ = sim.ExecuteNonQuery("create procedure p as select * from big");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var command = reader.CreateCommand(rpc ? "p" : "exec p");
        if (rpc)
            command.CommandType = CommandType.StoredProcedure;
        using (var rows = command.ExecuteReader())
        {
            IsTrue(rows.Read());
            IsTrue(rows.Read());
            AreEqual("KEY S, OBJECT IS", Locks(other, spid));
            AreEqual(0, Attempt(other, "drop procedure p"));
            AreEqual(Rows, ReadRest(rows));
        }
        AreEqual("", Locks(other, spid));
    }

    /// <summary>
    /// A key another transaction inserted ahead of a suspended
    /// <c>SERIALIZABLE</c> scan and hasn't committed stops the scan there, as
    /// any locked row does.
    /// </summary>
    [TestMethod]
    public void Serializable_ScanMeetsAnUncommittedInsertAhead()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(2000) not null); insert big select value * 2, 'x' from generate_series(1, {Rows})");
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        using var transaction = reader.BeginTransaction(IsolationLevel.Serializable);
        using var rows = ReadTwo(reader, "set lock_timeout 0; select * from big", transaction);
        _ = writer.CreateCommand("begin tran; insert big values (3001, 'y')").ExecuteNonQuery();
        var read = 2;
        AreEqual(1222, ThrowsExactly<SimulatedSqlException>(() =>
        {
            while (rows.Read())
                read++;
        }).Number);
        AreEqual(1500, read);
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>A result that fits the window has gone out whole, its statement over before the reader reads it.</summary>
    [TestMethod]
    public void SmallResult_HoldsNothing()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var rows = ReadTwo(reader, "select top (10) * from big");
        AreEqual("", Locks(other, spid));
        AreEqual(0, Attempt(other, "alter table big add c int"));
    }

    /// <summary>
    /// The reader meets a row another transaction is writing when it gets
    /// there, not when it starts: the rows before it go out, and the wait is
    /// its statement's — a <c>LOCK_TIMEOUT</c> ends it with Msg 1222 (probed
    /// 2026-10-08 against SQL Server 2025 at row 18,996 of 20,000 before row
    /// 19,000, a page lock away).
    /// </summary>
    [TestMethod]
    public void UncommittedWriter_StopsTheReaderWhereItsRowIs()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update big set v = 'z' where k = 1500").ExecuteNonQuery();

        using var rows = reader.CreateCommand("set lock_timeout 0; select k from big").ExecuteReader();
        var read = 0;
        var error = ThrowsExactly<SimulatedSqlException>(() =>
        {
            while (rows.Read())
                read++;
        });
        AreEqual(1222, error.Number);
        AreEqual(1499, read);
    }

    /// <summary>
    /// A statement's own <c>FOR JSON</c> or <c>FOR XML</c> document goes out in
    /// chunks as its rows serialize, so a reader a chunk in holds the key locks
    /// of the rows serialized so far (probed 2026-10-09 against SQL Server 2025
    /// under <c>REPEATABLE READ</c>); the chunks make the same document, and
    /// its count is still the rows serialized.
    /// </summary>
    [TestMethod]
    [DataRow("for json path", 11)]
    [DataRow("for xml path", 10)]
    public void ForClauseDocument_GoesOutAsItsRowsSerialize(string clause, int held)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int not null, pad char(2000) not null default 'x'); insert t (id, v) select value, value from generate_series(1, 2000)");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        var whole = (string)reader.CreateCommand($"select (select id, v, pad from t {clause})").ExecuteScalar()!;
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        var text = new System.Text.StringBuilder();
        using (var command = reader.CreateCommand($"select id, v, pad from t {clause}"))
        {
            command.Transaction = transaction;
            using var rows = command.ExecuteReader();
            IsTrue(rows.Read());
            _ = text.Append(rows.GetString(0));
            AreEqual(held, KeyLocks(other, spid, "S"));
            AreEqual(0, Attempt(other, "update t set pad = 'y' where id = 1500"));
            while (rows.Read())
                _ = text.Append(rows.GetString(0));
        }
        AreEqual(whole, text.ToString());
        using var count = reader.CreateCommand("select @@rowcount");
        count.Transaction = transaction;
        AreEqual(2000, count.ExecuteScalar());
        transaction.Rollback();
    }

    /// <summary>
    /// A locking read lets go the lock of a row its own <c>WHERE</c> rejects,
    /// however the predicate reads the row, as real's scan applies such a
    /// predicate itself; one reached through a subquery or a join's
    /// <c>ON</c> filters above the scan, which keeps every lock it took
    /// (probed 2026-10-09 against SQL Server 2025, a reader two rows in).
    /// </summary>
    [TestMethod]
    [DataRow("read committed", "select id, v, pad from t with (updlock) where v % 3 = 0", "U", 20, 666)]
    [DataRow("read committed", "select id, v, pad from t with (xlock) where v % 3 = 0", "X", 20, 666)]
    [DataRow("repeatable read", "select id, v, pad from t where v % 3 = 0", "S", 20, 666)]
    [DataRow("read committed", "select id, v, pad from t with (updlock) where v % 3 = 0 and newid() is not null", "U", 20, 666)]
    [DataRow("read committed", "select id, v, pad from t with (updlock) where v % 3 = 0 or id < 0", "U", 20, 666)]
    [DataRow("read committed", "select id, v, pad from t with (updlock) where v % 3 = 0 order by id", "U", 20, 666)]
    [DataRow("read committed", "select t.id, t.v, t.pad from t with (updlock) join (values (0), (1)) x (n) on t.v % 3 = x.n * 9", "U", 60, 2000)]
    [DataRow("read committed", "select id, v, pad from t with (updlock) where (select count(*) from t u where u.id = t.id and t.v % 3 = 0) = 1", "U", 60, 2000)]
    public void LockingRead_LetsGoARowItsWhereRejects(string level, string query, string mode, int held, int heldAtEnd)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int not null, pad char(2000) not null default 'x'); insert t (id, v) select value, value from generate_series(1, 2000)");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        _ = reader.CreateCommand($"set transaction isolation level {level}; begin tran").ExecuteNonQuery();
        using (var rows = reader.CreateCommand(query).ExecuteReader())
        {
            IsTrue(rows.Read());
            IsTrue(rows.Read());
            AreEqual(held, KeyLocks(other, spid, mode));
            AreEqual(held == 20 ? 0 : 1222, Attempt(other, "update t set pad = 'y' where id = 4"));
            while (rows.Read())
            {
            }
        }
        AreEqual(heldAtEnd, KeyLocks(other, spid, mode));
        _ = reader.CreateCommand("rollback").ExecuteNonQuery();
    }

    private static int KeyLocks(DbConnection observer, short spid, string mode) =>
        (int)observer.CreateCommand($"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and resource_type = 'KEY' and request_mode = '{mode}'").ExecuteScalar()!;

    /// <summary>
    /// While the statement waits on a row an uncommitted writer holds, its
    /// client reads every row that begins in a packet the statement filled
    /// before the wait — the rest of the packet it was filling never goes out
    /// — until its <c>CommandTimeout</c> ends the wait, and a wait inside the
    /// first packet holds up <c>ExecuteReader</c> itself (probed 2026-10-09
    /// against SQL Server 2025, MARS or not).
    /// </summary>
    [TestMethod]
    [DataRow(2000, 3, 0)]
    [DataRow(2000, 9, 8)]
    [DataRow(2000, 12, 8)]
    [DataRow(2000, 100, 96)]
    [DataRow(100, 100, 75)]
    [DataRow(100, 301, 299)]
    public void UncommittedWriter_RowsBeforeItGoOutWhileTheStatementWaits(int width, int held, int readable)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table b (k int primary key, v char({width}) not null); insert b select value, 'x' from generate_series(1, 3000)");
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand($"begin tran; update b set v = 'z' where k = {held}").ExecuteNonQuery();

        using var command = reader.CreateCommand("select k, v from b");
        command.CommandTimeout = 1;
        var read = 0;
        var error = ThrowsExactly<SimulatedSqlException>(() =>
        {
            using var rows = command.ExecuteReader();
            while (rows.Read())
                read++;
        });
        AreEqual(-2, error.Number);
        AreEqual(readable, read);
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// A <c>CommandTimeout</c> ends the wait for the writer's row with Msg -2;
    /// the time the client spends between reads doesn't count against it.
    /// </summary>
    [TestMethod]
    public void UncommittedWriter_CommandTimeoutEndsTheWait()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update big set v = 'z' where k = 1500").ExecuteNonQuery();

        using var command = reader.CreateCommand("select * from big");
        command.CommandTimeout = 1;
        using var rows = command.ExecuteReader();
        IsTrue(rows.Read());
        Thread.Sleep(1500);
        var read = 1;
        var error = ThrowsExactly<SimulatedSqlException>(() =>
        {
            while (rows.Read())
                read++;
        });
        AreEqual(-2, error.Number);
        AreEqual(1499, read);
    }

    /// <summary>
    /// A reader waiting on the writer's row reads it once the writer commits,
    /// as the committed row it then is.
    /// </summary>
    [TestMethod]
    public async Task UncommittedWriter_ReaderWaitsThenReadsTheCommittedRow()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        var spid = Spid(reader);
        _ = writer.CreateCommand("begin tran; update big set v = 'z' where k = 1500").ExecuteNonQuery();

        var waiting = await sim.StartBlocked(reader, "select v from big", TestContext.CancellationToken);
        AreEqual("LCK_M_S", observer.CreateCommand($"select wait_type from sys.dm_os_waiting_tasks where session_id = {spid}").ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        var values = await waiting;
        HasCount(Rows, values);
        AreEqual("z", ((string)values[1499]!).TrimEnd());
    }

    /// <summary>
    /// A <c>REPEATABLE READ</c> reader holding its first rows and a writer
    /// holding one ahead of it, each waiting on the other, deadlock: the
    /// reader, closing the cycle as it reads on, is the victim (probed
    /// 2026-10-08 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task SuspendedReader_DeadlocksWithAWriterAheadOfIt()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using var rows = ReadTwo(reader, "select * from big", transaction);

        var write = await sim.StartBlocked(writer, "begin tran; update big set v = 'q' where k = 1500; update big set v = 'q' where k = 1; commit", TestContext.CancellationToken);
        AreEqual(1205, ThrowsExactly<SimulatedSqlException>(() => ReadRest(rows)).Number);
        _ = await write;
    }

    /// <summary>
    /// A committed change to a row the reader has yet to reach is what it
    /// reads there under <c>READ COMMITTED</c>; the statement's snapshot keeps
    /// it out under <c>READ_COMMITTED_SNAPSHOT</c>.
    /// </summary>
    [TestMethod]
    [DataRow("", "z")]
    [DataRow("alter database current set read_committed_snapshot on", "x")]
    public void CommittedChangeAhead_ReadByItsLevel(string extra, string expected)
    {
        var sim = Big(extra);
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        using var rows = ReadTwo(reader, "select v from big");
        _ = writer.CreateCommand("update big set v = 'z' where k = 1500").ExecuteNonQuery();
        var values = new List<string> { "", "" };
        while (rows.Read())
            values.Add(rows.GetString(0).TrimEnd());
        AreEqual(expected, values[1499]);
    }

    [TestMethod]
    public void ReaderDispose_DrainsAndReleases()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        ReadTwo(reader, "select * from big").Dispose();
        AreEqual("", Locks(other, spid));
        AreEqual(Rows, reader.CreateCommand("select @@rowcount").ExecuteScalar());
    }

    /// <summary>
    /// Closing the connection closes its reader first, which runs the rest of
    /// the batch to its end, as SqlClient's close does (probed 2026-10-09
    /// against SQL Server 2025: a transaction the batch began and committed
    /// after the result committed).
    /// </summary>
    [TestMethod]
    public void ConnectionClose_RunsTheRestOfTheBatch()
    {
        var sim = Big("create table l (m varchar(20))");
        var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var rows = ReadTwo(reader, "begin tran; insert l values ('in tran'); select * from big; insert l values ('after'); commit");
        reader.Close();
        AreEqual(2, other.CreateCommand("select count(*) from l").ExecuteScalar());
        _ = Throws<InvalidOperationException>(() => rows.Read());
        reader.Dispose();
    }

    [TestMethod]
    public void ConnectionClose_Releases()
    {
        var sim = Big();
        var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using var rows = ReadTwo(reader, "select * from big", transaction);
        reader.Close();
        AreEqual("", Locks(other, spid));
        _ = Throws<InvalidOperationException>(() => rows.Read());
        reader.Dispose();
    }

    /// <summary>
    /// <c>KILL</c> ends a session whose statement waits on its client at
    /// once, its locks with it, and the reader's next read finds the
    /// connection broken (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Kill_EndsTheSuspendedStatement()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using var rows = ReadTwo(reader, "select * from big", transaction);
        _ = other.CreateCommand($"kill {spid}").ExecuteNonQuery();
        AreEqual("", Locks(other, spid));
        AreEqual(0, ThrowsExactly<SimulatedSqlException>(() => rows.Read()).Number);
    }

    /// <summary>A cancel reaches a suspended reader at its next read, as Msg 0.</summary>
    [TestMethod]
    public void Cancel_EndsTheReaderAtItsNextRead()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        var command = reader.CreateCommand("select * from big");
        using var rows = command.ExecuteReader();
        IsTrue(rows.Read());
        command.Cancel();
        AreEqual(0, ThrowsExactly<SimulatedSqlException>(() => ReadRest(rows)).Number);
        AreEqual("", Locks(other, spid));
    }

    /// <summary>A replayed cached plan sends its rows the same way.</summary>
    [TestMethod]
    public void CachedPlanReplay_Streams()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        for (var run = 0; run < 3; run++)
        {
            using var rows = ReadTwo(reader, "select * from big");
            AreEqual("KEY S, OBJECT IS", Locks(other, spid));
            AreEqual(Rows, ReadRest(rows));
        }
    }

    /// <summary>
    /// A row's error after rows have gone out ends the result set there, and
    /// the batch carries on: the next statement reads <c>@@ROWCOUNT</c> 0
    /// and <c>@@ERROR</c> 8134 (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ErrorAfterRows_EndsTheResultThenTheBatchGoesOn()
    {
        using var reader = Big().ExecuteReader("select v, 1 / (k - 1500) from big; select @@rowcount, @@error");
        var read = 0;
        AreEqual(8134, ThrowsExactly<SimulatedSqlException>(() =>
        {
            while (reader.Read())
                read++;
        }).Number);
        AreEqual(1499, read);
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        AreEqual(8134, reader.GetInt32(1));
    }

    /// <summary>
    /// Inside a <c>TRY</c> the rows before the error go out and the result set
    /// ends without it; the <c>CATCH</c> reads the error (probed 2026-10-08
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ErrorAfterRows_InTry_ReachesTheCatch()
    {
        using var reader = Big().ExecuteReader("begin try select v, 1 / (k - 1500) from big end try begin catch select error_number(), @@rowcount end catch");
        AreEqual(1499, ReadRest(reader, 0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(8134, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
    }

    /// <summary>
    /// Under <c>XACT_ABORT</c> a row's error after rows have gone out ends the
    /// batch and rolls its transaction back (probed 2026-10-08 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void ErrorAfterRows_UnderXactAbort_EndsTheBatch()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using (var reader = connection.CreateCommand("set xact_abort on; begin tran; insert heap values (0, 'y'); select v, 1 / (k - 1500) from big; select @@trancount").ExecuteReader())
        {
            AreEqual(8134, ThrowsExactly<SimulatedSqlException>(() => ReadRest(reader, 0)).Number);
            IsFalse(reader.NextResult());
        }
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(0, connection.CreateCommand("select count(*) from heap where k = 0").ExecuteScalar());
    }

    /// <summary>
    /// Query Store times and counts a streamed statement once its last row is
    /// out, its plan replayed or parsed.
    /// </summary>
    [TestMethod]
    public void QueryStore_CountsAStreamedStatementsRows()
    {
        var sim = Big("alter database simulated set query_store (query_capture_mode = all)");
        for (var run = 0; run < 3; run++)
        {
            using var reader = sim.ExecuteReader("select * from big");
            AreEqual(Rows, ReadRest(reader, 0));
        }
        using var stats = sim.ExecuteReader("""
            select sum(rs.count_executions), max(rs.last_rowcount) from sys.query_store_runtime_stats rs
            join sys.query_store_plan p on p.plan_id = rs.plan_id
            join sys.query_store_query q on q.query_id = p.query_id
            join sys.query_store_query_text t on t.query_text_id = q.query_text_id
            where t.query_sql_text = 'select * from big'
            """);
        IsTrue(stats.Read());
        AreEqual(3L, stats.GetInt64(0));
        AreEqual(Rows, stats.GetInt64(1));
    }

    /// <summary>
    /// The batch's next statement runs once the reader has read the result's
    /// last rows, seeing what the result's statement left.
    /// </summary>
    [TestMethod]
    public void NextStatement_ReadsTheStreamedCount()
    {
        using var reader = Big().ExecuteReader("select * from big; select @@rowcount");
        AreEqual(Rows, ReadRest(reader, 0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(Rows, reader.GetInt32(0));
    }

    /// <summary>
    /// A procedure or dynamic batch an <c>EXECUTE … WITH RESULT SETS</c> runs
    /// streams its rows through the declared types, so a <c>REPEATABLE
    /// READ</c> reader two rows in holds the keys of the twenty rows produced,
    /// and a value that won't convert raises where the rows reach it (probed
    /// 2026-10-09 against SQL Server 2025: twenty keys, the error after 1,499
    /// rows for a failure at row 1,500).
    /// </summary>
    [TestMethod]
    [DataRow("exec p with result sets ((k bigint, v char(2000)))")]
    [DataRow("exec ('select k, v from big') with result sets ((k bigint, v char(2000)))")]
    [DataRow("exec sp_executesql N'select k, v from big' with result sets ((k bigint, v char(2000)))")]
    public void WithResultSets_StreamsThroughTheDeclaredTypes(string exec)
    {
        var sim = Big();
        _ = sim.ExecuteNonQuery("create procedure p as select k, v from big");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using (var rows = ReadTwo(reader, exec, transaction))
        {
            AreEqual("KEY Sx20, OBJECT IS", Locks(other, spid));
            AreEqual("suspended ASYNC_NETWORK_IO SELECT", Request(other, spid));
            AreEqual(typeof(long), rows.GetFieldType(0));
            AreEqual(0, Attempt(other, "update big set v = v where k = 21"));
            // Another request of the session runs beside the suspended body.
            using var beside = reader.CreateCommand("select count(*) from big");
            beside.Transaction = transaction;
            AreEqual(Rows, beside.ExecuteScalar());
            AreEqual(Rows, ReadRest(rows));
        }
        transaction.Rollback();
    }

    [TestMethod]
    public void WithResultSets_ConversionFailureRaisesWhereTheRowsReachIt()
    {
        var sim = Big();
        _ = sim.ExecuteNonQuery("create procedure p as select k, case when k = 1500 then 'x' else cast(k as varchar(10)) end as n, v from big");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using var rows = ReadTwo(reader, "exec p with result sets ((k int, n int, v char(2000)))", transaction);
        AreEqual("KEY Sx20, OBJECT IS", Locks(other, spid));
        var read = 2;
        var error = Throws<SimulatedSqlException>(() =>
        {
            while (rows.Read())
                read++;
        });
        AreEqual(8114, error.Number);
        AreEqual(1499, read);
    }

    /// <summary>
    /// Under <c>READ COMMITTED</c> each scan the last row came through holds
    /// the S of the row it stands on while the reader waits — the outer
    /// table's and a correlated subquery's alike — as real's do (probed
    /// 2026-10-09 against SQL Server 2025: two <c>KEY S</c>, three beside an
    /// <c>EXISTS</c> over a third table); a subquery over the same table
    /// stands on the row the outer scan does.
    /// </summary>
    [TestMethod]
    [DataRow("select * from big where (select count(*) from u where u.k = big.k) = 1", "KEY Sx2, OBJECT ISx2", 1222)]
    [DataRow("select * from big where (select count(*) from u where u.k = big.k) = 1 and exists (select 1 from w where w.k = big.k)", "KEY Sx3, OBJECT ISx3", 1222)]
    [DataRow("select * from big cross apply (select top (1) n from u where u.k = big.k) x", "KEY Sx2, OBJECT ISx2", 1222)]
    [DataRow("select * from big where (select count(*) from big b where b.k = big.k) = 1", "KEY S, OBJECT IS", 0)]
    public void ReadCommitted_EachScanOfTheLastRowHoldsItsPosition(string query, string held, int innerRowWaits)
    {
        var sim = Big("""
            create table u (k int primary key, n int not null); insert u select value, value from generate_series(1, 2000);
            create table w (k int primary key, n int not null); insert w select value, value from generate_series(1, 2000);
            """);
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using (var rows = ReadTwo(reader, query))
        {
            AreEqual(held, Locks(other, spid));
            AreEqual(1222, Attempt(other, "update big set v = v where k = 20"));
            AreEqual(innerRowWaits, Attempt(other, "update u set n = n where k = 20"));
            AreEqual(0, Attempt(other, "update u set n = n where k = 21"));
            AreEqual(Rows, ReadRest(rows));
        }
        AreEqual("", Locks(other, spid));
    }

    /// <summary>
    /// The statement refills its window three packets at a time, as its
    /// client reads into each third packet, so a <c>REPEATABLE READ</c>
    /// reader holds real's count of keys wherever it stands (probed
    /// 2026-10-09 against SQL Server 2025 with 2,007-byte rows).
    /// </summary>
    [TestMethod]
    [DataRow(2, 20)]
    [DataRow(10, 32)]
    [DataRow(20, 32)]
    [DataRow(21, 44)]
    [DataRow(68, 80)]
    [DataRow(69, 92)]
    [DataRow(100, 116)]
    [DataRow(300, 319)]
    public void RepeatableRead_WindowRefillsThreePacketsAtATime(int read, int held)
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using var command = reader.CreateCommand("select * from big");
        command.Transaction = transaction;
        using (var rows = command.ExecuteReader())
        {
            for (var i = 0; i < read; i++)
                IsTrue(rows.Read());
            AreEqual(held, KeyLocks(other, spid, "S"));
        }
        transaction.Rollback();
    }
}
