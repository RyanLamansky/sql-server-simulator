using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Another request of the reader's own session runs while the reader's
/// <c>SELECT</c> waits on it mid-result, as real interleaves a MARS session's
/// requests: the suspended statement holds what its position holds against
/// the other request as against another session, since real separates a
/// session's requests by the transaction each works in, and reads on as the
/// table stands after the other request's writes (probed 2026-10-05 and
/// 2026-10-08 against SQL Server 2025 over a MARS SqlClient connection, the
/// in-process connection being one). See <c>docs/claude/data-reader.md</c>.
/// </summary>
[TestClass]
public sealed class MarsInterleavingTests
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
            """);
        if (extra.Length != 0)
            sim.ExecuteBatches(extra);
        return sim;
    }

    private static short Spid(DbConnection connection) => (short)connection.CreateCommand("select @@spid").ExecuteScalar()!;

    private static string Locks(DbConnection observer, short spid) => string.Join(", ", Extensions.FirstColumn(observer, $"""
        select concat(resource_type, ' ', request_mode, iif(count(*) > 1, concat('x', count(*)), ''))
        from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE'
        group by resource_type, request_mode order by resource_type, request_mode
        """));

    /// <summary>Runs <paramref name="sql"/> on <paramref name="connection"/> under <c>LOCK_TIMEOUT 0</c>: the error number it raised, or 0.</summary>
    private static int Attempt(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        try
        {
            using var command = connection.CreateCommand($"set lock_timeout 0; {sql}");
            command.Transaction = transaction;
            _ = command.ExecuteNonQuery();
            return 0;
        }
        catch (SimulatedSqlException error)
        {
            return error.Number;
        }
    }

    private static DbDataReader Read(DbConnection connection, string sql, int rows, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        var reader = command.ExecuteReader();
        for (var read = 0; read < rows; read++)
            IsTrue(reader.Read());
        return reader;
    }

    /// <summary>The keys and values the reader reads from where it stands to its end.</summary>
    private static List<(int Key, string Value)> Rest(DbDataReader reader)
    {
        List<(int, string)> rows = [];
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1).TrimEnd()));
        return rows;
    }

    [TestMethod]
    public void AnotherRequest_RunsBesideTheSuspendedStatement()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, "select * from big", 2);
        AreEqual("KEY S, OBJECT IS", Locks(other, spid));
        AreEqual(Rows, connection.CreateCommand("select count(*) from big").ExecuteScalar());
        AreEqual("KEY S, OBJECT IS", Locks(other, spid));
        HasCount(Rows - 2, Rest(rows));
        AreEqual("", Locks(other, spid));
    }

    /// <summary>Two readers of one connection each hold their statement's position while the other reads.</summary>
    [TestMethod]
    public void TwoReaders_EachSuspendedMidResult()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var first = Read(connection, "select * from big", 2);
        using var second = Read(connection, "select * from heap", 2);
        AreEqual("KEY S, OBJECT ISx2, RID S", Locks(other, spid));
        for (var read = 0; read < 100; read++)
            IsTrue(second.Read());
        HasCount(Rows - 2, Rest(first));
        HasCount(Rows - 102, Rest(second));
        AreEqual("", Locks(other, spid));
    }

    /// <summary>
    /// The suspended reader holds against another request of its session what
    /// it holds against another session, and reads on as the table stands
    /// after that request's writes: a row updated ahead with its new value, a
    /// row deleted ahead not at all, keys inserted ahead in their place — or
    /// none of it at a versioned level (probed 2026-10-05 and 2026-10-08
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("read uncommitted", "", 0, 0, true)]
    [DataRow("read committed", "", 0, 1222, true)]
    [DataRow("repeatable read", "", 1222, 1222, true)]
    [DataRow("serializable", "", 1222, 1222, true)]
    [DataRow("read committed", "alter database current set read_committed_snapshot on", 0, 0, false)]
    [DataRow("snapshot", "alter database current set allow_snapshot_isolation on", 0, 0, false)]
    public void AnotherRequestsWrites_MeetTheSuspendedReadAsAnotherSessionsWould(string level, string extra, int behind, int position, bool seesWrites)
    {
        var sim = Big(extra);
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, $"set transaction isolation level {level}; select k, v from big", 2);
        AreEqual(behind, Attempt(connection, "update big set v = 'b' where k = 1"));
        AreEqual(position, Attempt(connection, "update big set v = 'p' where k = 20"));
        AreEqual(0, Attempt(connection, "update big set v = 'u' where k = 1500"));
        AreEqual(0, Attempt(connection, "delete big where k between 1700 and 1709"));
        AreEqual(0, Attempt(connection, $"insert big values ({Rows + 1}, 'i'), ({Rows + 2}, 'i')"));
        var rest = Rest(rows);
        var read = rest.ToDictionary(row => row.Key, row => row.Value);
        HasCount(seesWrites ? Rows - 2 - 10 + 2 : Rows - 2, rest);
        AreEqual(seesWrites ? "u" : "x", read[1500]);
        AreEqual(!seesWrites, read.ContainsKey(1705));
        AreEqual(seesWrites, read.ContainsKey(Rows + 2));
    }

    /// <summary>
    /// A key inserted between two the scan has yet to reach is read in its
    /// place, an <c>ORDER BY</c> the clustered key satisfies staying ordered.
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow(" order by k")]
    [DataRow(" order by k desc")]
    public void KeyInsertedAhead_IsReadInItsPlace(string order)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table big (k int primary key, v char(2000) not null); insert big select value * 2, 'x' from generate_series(1, {Rows})");
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, $"select k, v from big{order}", 2);
        var middle = order.EndsWith("desc", StringComparison.Ordinal) ? 1001 : (2 * Rows) - 1001;
        AreEqual(0, Attempt(connection, $"insert big values ({middle}, 'i')"));
        var keys = Rest(rows).Select(row => row.Key).ToList();
        HasCount(Rows - 1, keys);
        Contains(middle, keys);
        List<int> expected = order.EndsWith("desc", StringComparison.Ordinal) ? [.. keys.OrderDescending()] : [.. keys.Order()];
        CollectionAssert.AreEqual(expected, keys);
    }

    /// <summary>
    /// A write of the session's own waits on what its suspended reader holds
    /// until a timeout ends the wait, as real's mostly does: its deadlock
    /// monitor ended a few such waits with Msg 1205, most ran to their
    /// <c>CommandTimeout</c> (probed 2026-10-05 and 2026-10-08 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void OwnWrite_WaitsOnTheSuspendedReader()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, "set transaction isolation level repeatable read; select k, v from big", 2);
        using var write = connection.CreateCommand("update big set v = 'w' where k = 1");
        write.CommandTimeout = 1;
        AreEqual(-2, ThrowsExactly<SimulatedSqlException>(() => write.ExecuteNonQuery()).Number);
        HasCount(Rows - 2, Rest(rows));
        AreEqual(1, connection.CreateCommand("update big set v = 'w' where k = 1").ExecuteNonQuery());
    }

    /// <summary>A redefinition waits on the suspended reader's table lock; an index build shares it (probed 2026-10-05 against SQL Server 2025).</summary>
    [TestMethod]
    public void Ddl_WaitsOnTheSuspendedReader()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, "select k, v from big", 2);
        AreEqual(0, Attempt(connection, "create index ix on big (k desc)"));
        AreEqual(1222, Attempt(connection, "alter table big add c int"));
        AreEqual(1222, Attempt(connection, "truncate table big"));
        HasCount(Rows - 2, Rest(rows));
        AreEqual(0, Attempt(connection, "alter table big add c int"));
    }

    /// <summary>
    /// A request in the reader's own transaction reads beside it, and its
    /// first write finds the reader's statement run to its end first, so the
    /// reader reads none of its writes, as real's reader doesn't — real
    /// versions the writes it makes there (probed 2026-10-05 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(IsolationLevel.ReadCommitted, "update big set v = 'u' where k = 1500")]
    [DataRow(IsolationLevel.RepeatableRead, "update big set v = 'u' where k = 1500")]
    [DataRow(IsolationLevel.RepeatableRead, "alter table heap add c int")]
    public void RequestInTheReadersTransaction_LeavesItsReadAsItBegan(IsolationLevel level, string write)
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var transaction = connection.BeginTransaction(level);
        using var rows = Read(connection, "select k, v from big", 2, transaction);
        var held = Locks(other, spid);
        using (var lookup = connection.CreateCommand("select count(*) from sys.objects where name = 'big'"))
        {
            lookup.Transaction = transaction;
            AreEqual(1, lookup.ExecuteScalar());
        }
        AreEqual(held, Locks(other, spid));
        AreEqual(0, Attempt(connection, write, transaction));
        AreEqual(0, Attempt(connection, "update big set v = 'u' where k = 1500", transaction));
        AreEqual(0, Attempt(connection, "delete big where k = 1700", transaction));
        AreEqual(0, Attempt(connection, $"insert big values ({Rows + 1}, 'i')", transaction));
        var read = Rest(rows).ToDictionary(row => row.Key, row => row.Value);
        HasCount(Rows - 2, read);
        AreEqual("x", read[1500]);
        IsTrue(read.ContainsKey(1700));
        IsFalse(read.ContainsKey(Rows + 1));
        transaction.Rollback();
    }

    /// <summary>
    /// A local temp table is locked whole — S for a read, held to the
    /// statement's end or the transaction's by the level, X for a locking hint
    /// or a write, Sch-S alone for a read of no committed state — so another
    /// request's write waits on a reader suspended over it, listed in tempdb
    /// (probed 2026-10-09 against SQL Server 2025, which lists each object lock
    /// sixteen times, one per lock partition).
    /// </summary>
    [TestMethod]
    [DataRow("read committed", "", "OBJECT S", 1222, "")]
    [DataRow("read uncommitted", "", "OBJECT Sch-S", 0, "")]
    [DataRow("read committed", "with (nolock)", "OBJECT Sch-S", 0, "")]
    [DataRow("repeatable read", "", "OBJECT S", 1222, "OBJECT S")]
    [DataRow("serializable", "", "OBJECT S", 1222, "OBJECT S")]
    [DataRow("read committed", "with (holdlock)", "OBJECT S", 1222, "OBJECT S")]
    [DataRow("read committed", "with (updlock)", "OBJECT X", 1222, "OBJECT X")]
    [DataRow("read committed", "with (tablockx)", "OBJECT X", 1222, "OBJECT X")]
    public void TempTable_LockedWholeAgainstAnotherRequest(string level, string hint, string held, int write, string heldAfter)
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        var spid = Spid(connection);
        _ = connection.CreateCommand($"create table #t (k int primary key, v char(2000) not null); insert #t select value, 'x' from generate_series(1, {Rows})").ExecuteNonQuery();
        using (var rows = Read(connection, $"set transaction isolation level {level}; select k, v from #t {hint}", 2))
        {
            AreEqual(held, Locks(observer, spid));
            AreEqual(2, observer.CreateCommand($"select resource_database_id from sys.dm_tran_locks where request_session_id = {spid} and resource_type = 'OBJECT'").ExecuteScalar());
            AreEqual(write, Attempt(connection, "insert #t values (5000, 'i')"));
            HasCount(write == 0 ? Rows - 1 : Rows - 2, Rest(rows));
        }
        AreEqual("", Locks(observer, spid));
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        using (var count = connection.CreateCommand($"set transaction isolation level {level}; select count(*) from #t {hint}; set transaction isolation level read committed"))
        {
            count.Transaction = transaction;
            _ = count.ExecuteScalar();
        }
        AreEqual(heldAfter, Locks(observer, spid));
        transaction.Rollback();
        AreEqual("", Locks(observer, spid));
    }

    /// <summary>
    /// A cursor's scroll locks are the session's, so a request running beside
    /// a suspended reader updates the row its cursor holds.
    /// </summary>
    [TestMethod]
    public void PositionedUpdate_BesideASuspendedReader()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, "set transaction isolation level repeatable read; select k, v from big", 2);
        _ = connection.CreateCommand("declare c cursor scroll_locks for select k from heap where k <= 5 for update of v; open c; fetch next from c").ExecuteNonQuery();
        AreEqual(0, Attempt(connection, "update heap set v = 'c' where current of c"));
        _ = connection.CreateCommand("close c; deallocate c").ExecuteNonQuery();
        AreEqual(1222, Attempt(connection, "update big set v = 'b' where k = 1"));
        HasCount(Rows - 2, Rest(rows));
        AreEqual("c", sim.ExecuteScalar("select rtrim(v) from heap where k = 1"));
    }

    /// <summary>
    /// A suspended statement's snapshot stays its own while its session's
    /// other requests run, end and start statements: a write committed after
    /// it began, its versions swept, still reads as it was.
    /// </summary>
    [TestMethod]
    public void StatementSnapshot_StaysWithItsRequest()
    {
        var sim = Big("alter database current set read_committed_snapshot on");
        using var connection = sim.CreateOpenConnection();
        using var first = Read(connection, "select k, v from big", 2);
        using var second = Read(connection, "select k, v from heap", 2);
        HasCount(Rows - 2, Rest(first));
        _ = connection.CreateCommand("update heap set v = 'u' where k > 1000").ExecuteNonQuery();
        _ = connection.CreateCommand("update big set v = 'u' where k > 1000").ExecuteNonQuery();
        IsTrue(Rest(second).All(row => row.Value == "x"));
    }

    /// <summary>
    /// A suspended statement's LOB epoch stays its own too: the off-row
    /// values its sort holds images of aren't handed to another row while it
    /// waits, whatever its session's other requests end and begin meanwhile.
    /// </summary>
    [TestMethod]
    public void LobEpoch_StaysWithItsRequest()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table lob (k int primary key, v nvarchar(max) not null);
            insert lob select value, replicate(cast(value % 10 as nvarchar(max)), 6000) from generate_series(1, 200)
            """);
        using var connection = sim.CreateOpenConnection();
        using var first = Read(connection, "select k, v from lob order by checksum(k)", 1);
        using var second = Read(connection, "select k, v from lob order by checksum(k) desc", 1);
        while (first.Read())
        {
        }
        _ = connection.CreateCommand("update lob set v = replicate(N'u', 7000)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert lob select value + 1000, replicate(N'i', 6000) from generate_series(1, 200)").ExecuteNonQuery();
        var rows = Rest(second);
        HasCount(199, rows);
        IsTrue(rows.All(row => row.Value == new string((char)('0' + (row.Key % 10)), 6000)));
    }

    /// <summary>
    /// A suspended statement converts by its own request's <c>SET DATEFORMAT</c>
    /// as it runs on, whatever another request of the session set meanwhile.
    /// </summary>
    [TestMethod]
    public void SessionSettings_StayWithTheSuspendedStatement()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var rows = Read(connection, "set dateformat dmy; select k, v, convert(date, concat('13/01/', 2000 + k % 20)) from big", 2);
        _ = connection.CreateCommand("set dateformat mdy; select convert(date, '01/13/2020')").ExecuteScalar();
        var read = 2;
        while (rows.Read())
            read++;
        AreEqual(Rows, read);
    }

    /// <summary>
    /// A <c>SELECT</c> in a procedure or dynamic batch the reader's batch calls
    /// sends its rows as its client reads them too, holding its position while
    /// it waits (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec p")]
    [DataRow("exec ('select k, v from big')")]
    [DataRow("exec sp_executesql N'select k, v from big'")]
    public void CalledBodysSelect_StreamsAndHoldsItsPosition(string call)
    {
        var sim = Big("create procedure p as select k, v from big");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, call, 2);
        Contains("KEY S", Locks(other, spid));
        AreEqual(1222, Attempt(other, "update big set v = 'p' where k = 20"));
        AreEqual(0, Attempt(other, "update big set v = 'u' where k = 1500"));
        var read = Rest(rows).ToDictionary(row => row.Key, row => row.Value);
        AreEqual("u", read[1500]);
        AreEqual("", Locks(other, spid));
    }

    /// <summary>
    /// A DML statement's <c>OUTPUT</c> rows past what fits ahead of the client
    /// go out as it reads them, the statement — its atomicity and its locks —
    /// ending with its last: a cancel while they go out rolls it back, as
    /// real's does, and a reader closing reads the rest and commits it
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, Rows)]
    public void OutputRowsGoingOut_HoldTheStatementOpen(bool cancel, int committed)
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var command = connection.CreateCommand("update big set v = 'y' output inserted.k, inserted.v");
        var rows = command.ExecuteReader();
        for (var read = 0; read < 10; read++)
            IsTrue(rows.Read());
        AreEqual(3980, Attempt(connection, "select 1"));
        AreEqual(1222, Attempt(other, "select v from big where k = 1"));
        if (cancel)
            command.Cancel();
        rows.Dispose();
        AreEqual(committed, connection.CreateCommand("select count(*) from big where v = 'y'").ExecuteScalar());
        AreEqual(0, Attempt(other, "select v from big where k = 1"));
    }

    /// <summary>A procedure's DML statement sends its <c>OUTPUT</c> rows the same way.</summary>
    [TestMethod]
    public void ProceduresOutputRows_RollBackWithACancel()
    {
        var sim = Big("create procedure p as update big set v = 'y' output inserted.k, inserted.v");
        using var connection = sim.CreateOpenConnection();
        var command = connection.CreateCommand("exec p");
        var rows = command.ExecuteReader();
        for (var read = 0; read < 10; read++)
            IsTrue(rows.Read());
        command.Cancel();
        rows.Dispose();
        AreEqual(0, connection.CreateCommand("select count(*) from big where v = 'y'").ExecuteScalar());
    }
}
