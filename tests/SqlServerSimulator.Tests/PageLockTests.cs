using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>PAGLOCK</c> locks the pages a table's rows are on in place of the rows,
/// four rows of <c>int</c> and <c>char(2000)</c> to a page as real fills them
/// loaded in key order — three once the database versions its rows — so a
/// writer of any row of a locked page waits (probed 2026-10-09 against SQL
/// Server 2025 with a reader two rows into 2,000 such rows).
/// </summary>
[TestClass]
public sealed class PageLockTests
{
    private const int Rows = 2000;

    private static Simulation Big(string database = "")
    {
        var sim = new Simulation();
        if (database.Length != 0)
            _ = sim.ExecuteNonQuery(database);
        _ = sim.ExecuteNonQuery($"""
            create table t (id int primary key, v int not null, pad char(2000) not null default 'x');
            insert t (id, v) select value, value from generate_series(1, {Rows})
            """);
        return sim;
    }

    private static short Spid(DbConnection connection) => (short)connection.CreateCommand("select @@spid").ExecuteScalar()!;

    private static string Locks(DbConnection observer, short spid) => string.Join(", ", Extensions.FirstColumn(observer, $"""
        select concat(resource_type, ' ', request_mode, iif(count(*) > 1, concat('x', count(*)), ''))
        from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE'
        group by resource_type, request_mode order by resource_type, request_mode
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

    private static DbDataReader ReadTwo(DbConnection connection, string sql, DbTransaction? transaction)
    {
        var command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.Read());
        return reader;
    }

    /// <summary>
    /// A reader two rows in has produced twenty: under <c>READ COMMITTED</c> it
    /// holds the S of the page it stands on — rows 17 to 20 — and under
    /// <c>REPEATABLE READ</c> or <c>SERIALIZABLE</c> the five pages it read,
    /// with no row or range lock; <c>UPDLOCK</c> and <c>XLOCK</c> take the
    /// pages' U and X.
    /// </summary>
    [TestMethod]
    [DataRow(IsolationLevel.ReadCommitted, "paglock", "OBJECT IS, PAGE S", 0, 1222, 0, "0,0")]
    [DataRow(IsolationLevel.RepeatableRead, "paglock", "OBJECT IS, PAGE Sx5", 1222, 1222, 0, "0,0")]
    [DataRow(IsolationLevel.Serializable, "paglock", "OBJECT IS, PAGE Sx5", 1222, 1222, 0, "0,0")]
    [DataRow(IsolationLevel.ReadCommitted, "paglock, updlock", "OBJECT IX, PAGE Ux5", 1222, 1222, 0, "0,1222")]
    [DataRow(IsolationLevel.ReadCommitted, "paglock, xlock", "OBJECT IX, PAGE Xx5", 1222, 1222, 0, "1222,1222")]
    public void SuspendedReader_HoldsItsPages(IsolationLevel level, string hints, string held, int rowOne, int rowEighteen, int rowTwentyOne, string readsOfEighteen)
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = level == IsolationLevel.ReadCommitted ? null : reader.BeginTransaction(level);
        using var rows = ReadTwo(reader, $"select id, v, pad from t with ({hints})", transaction);
        AreEqual(held, Locks(other, spid));
        AreEqual(rowOne, Attempt(other, "update t set v = v where id = 1"));
        AreEqual(rowEighteen, Attempt(other, "update t set v = v where id = 18"));
        AreEqual(rowEighteen, Attempt(other, "update t with (rowlock) set v = v where id = 17"));
        AreEqual(rowTwentyOne, Attempt(other, "update t set v = v where id = 21"));
        AreEqual(readsOfEighteen, $"{Attempt(other, "select v from t where id = 18")},{Attempt(other, "select v from t with (updlock) where id = 18")}");
        AreEqual(0, Attempt(other, "select v from t with (nolock) where id = 18"));
        AreEqual(0, Attempt(other, $"insert t (id, v) values ({Rows + 1}, 0)"));
        while (rows.Read())
        {
        }
    }

    /// <summary>A page lock's timeout reports state 52, and an update it ended is followed by Msg 3621, as real's are.</summary>
    [TestMethod]
    public void PageLockTimeout_IsState52()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = ReadTwo(reader, "select id, v, pad from t with (paglock)", null);
        var error = Throws<SimulatedSqlException>(() => other.CreateCommand("set lock_timeout 0; update t set v = v where id = 18").ExecuteNonQuery());
        AreEqual(1222, error.Number);
        AreEqual(52, error.State);
        AreEqual(3621, error.Errors[^1].Number);
    }

    /// <summary>
    /// A row-versioning database's rows carry a fourteen-byte tag, three rows
    /// to a page, so the twenty rows produced are on seven.
    /// </summary>
    [TestMethod]
    public void VersionedRows_TakeThreeToAPage()
    {
        var sim = Big("alter database current set allow_snapshot_isolation on");
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(reader);
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        using var rows = ReadTwo(reader, "select id, v, pad from t with (paglock)", transaction);
        AreEqual("OBJECT IS, PAGE Sx7", Locks(other, spid));
        AreEqual(1222, Attempt(other, "update t set v = v where id = 21"));
        AreEqual(0, Attempt(other, "update t set v = v where id = 22"));
        while (rows.Read())
        {
        }
    }

    /// <summary>
    /// A write under <c>PAGLOCK</c> takes the X of each page it writes a row
    /// of, which the view shows in place of its rows' X, so a writer or a
    /// reader of another row of those pages waits.
    /// </summary>
    [TestMethod]
    public void Write_LocksItsPages()
    {
        var sim = Big();
        using var writer = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(writer);
        _ = writer.CreateCommand("begin tran; update t with (paglock) set v = v + 1 where id between 5 and 10").ExecuteNonQuery();
        AreEqual("OBJECT IX, PAGE Xx2", Locks(other, spid));
        AreEqual(1222, Attempt(other, "update t set v = v where id = 11"));
        AreEqual(0, Attempt(other, "update t set v = v where id = 13"));
        AreEqual(1222, Attempt(other, "select v from t where id = 12"));
        AreEqual(0, Attempt(other, "select v from t with (nolock) where id = 12"));
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual("", Locks(other, spid));
    }

    /// <summary>An insert lands on the last page, which a reader holding every page keeps it off.</summary>
    [TestMethod]
    public void Insert_MeetsTheLastPagesLock()
    {
        var sim = Big();
        using var reader = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var transaction = reader.BeginTransaction(IsolationLevel.RepeatableRead);
        var command = reader.CreateCommand("select count(*) from t with (paglock)");
        command.Transaction = transaction;
        AreEqual(Rows, command.ExecuteScalar());
        AreEqual(1222, Attempt(other, $"insert t (id, v) values ({Rows + 1}, 0)"));
        transaction.Rollback();
    }
}
