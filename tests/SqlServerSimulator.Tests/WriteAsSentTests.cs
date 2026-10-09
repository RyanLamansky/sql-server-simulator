using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A DML statement whose client reads its <c>OUTPUT</c> rows as they come
/// writes each row as it sends it, as real's pipeline does: a reader partway
/// into them leaves only the rows written so far locked and changed, rows
/// ahead free for another session, and a row's error goes out after the rows
/// before it — while the shapes real's plan reads or writes whole first do
/// that here too (probed 2026-10-09 against SQL Server 2025 over a MARS
/// SqlClient connection, the in-process connection being one: 2,000 rows of
/// 2,010 bytes, a reader two rows in). See <c>docs/claude/data-reader.md</c>.
/// </summary>
[TestClass]
public sealed class WriteAsSentTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int Rows = 2000;

    /// <summary>The rows of 2,010 bytes real's first window holds.</summary>
    private const int Window = 20;

    private static Simulation Big(string extra = "")
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table t (id int primary key, v int not null, pad char(2000) not null default 'x');
            insert t (id, v) select value, 0 from generate_series(1, {Rows});
            """);
        if (extra.Length != 0)
            sim.ExecuteBatches(extra);
        return sim;
    }

    private static short Spid(DbConnection connection) => (short)connection.CreateCommand("select @@spid").ExecuteScalar()!;

    private static string Locks(DbConnection observer, short spid) => string.Join(", ", Extensions.FirstColumn(observer, $"""
        select concat(resource_type, ' ', request_mode, iif(count(*) > 1, concat('x', count(*)), ''))
        from sys.dm_tran_locks where request_session_id = {spid} and resource_type not in ('DATABASE', 'METADATA')
        group by resource_type, request_mode order by resource_type, request_mode
        """));

    /// <summary>Runs <paramref name="sql"/> on <paramref name="connection"/> under <c>LOCK_TIMEOUT 0</c>: the error number it raised, or 0.</summary>
    private static int Attempt(DbConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand($"set lock_timeout 0; {sql}");
            _ = command.ExecuteNonQuery();
            return 0;
        }
        catch (SimulatedSqlException error)
        {
            return error.Number;
        }
    }

    private static object? Scalar(DbConnection connection, string sql) => connection.CreateCommand(sql).ExecuteScalar();

    private static DbDataReader Read(DbConnection connection, string sql, int rows = 2)
    {
        var reader = connection.CreateCommand(sql).ExecuteReader();
        for (var read = 0; read < rows; read++)
            IsTrue(reader.Read());
        return reader;
    }

    /// <summary>Reads the rest of the reader's rows: how many it read in all, counting the <paramref name="read"/> already read.</summary>
    private static int ReadRest(DbDataReader reader, int read = 2)
    {
        while (reader.Read())
            read++;
        return read;
    }

    /// <summary>Reads the reader's rows to the error a row raised: how many it read, and the error's number.</summary>
    private static (int Read, int Error) ReadToError(DbDataReader reader, int read = 0)
    {
        try
        {
            while (reader.Read())
                read++;
        }
        catch (SimulatedSqlException error)
        {
            return (read, error.Number);
        }
        return (read, 0);
    }

    [TestMethod]
    public void Update_WritesTheRowsItSends()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, "update t set v = v + 1 output inserted.id, inserted.v, inserted.pad");
        AreEqual($"KEY Xx{Window}, OBJECT IX", Locks(other, spid));
        AreEqual(Window, Scalar(other, "select count(*) from t with (nolock) where v = 1"));
        AreEqual(1222, Attempt(other, "select v from t where id = 1"));
        // A row ahead is free, and another session's write to it is read as
        // written when the statement reaches it.
        AreEqual(0, Attempt(other, "select v from t where id = 1500"));
        AreEqual(0, Attempt(other, "update t set v = 100 where id = 1500"));
        AreEqual(Rows, ReadRest(rows));
        AreEqual(Rows, rows.RecordsAffected);
        AreEqual(101, Scalar(other, "select v from t where id = 1500"));
        AreEqual(Rows, Scalar(connection, "select count(*) from t where v >= 1"));
    }

    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, Rows)]
    public void Update_CancelRollsBackWhatItWrote_CloseWritesTheRest(bool cancel, int written)
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        var command = connection.CreateCommand("update t set v = v + 1 output inserted.id, inserted.v, inserted.pad");
        var rows = command.ExecuteReader();
        IsTrue(rows.Read());
        if (cancel)
            command.Cancel();
        rows.Dispose();
        AreEqual(written, Scalar(connection, "select count(*) from t where v = 1"));
    }

    /// <summary>
    /// A filtered walk reads each row as another session left it when it
    /// reaches it: a key inserted ahead is written, one deleted ahead isn't,
    /// and one updated ahead out of the filter is passed over.
    /// </summary>
    [TestMethod]
    public void Update_ReadsRowsAheadAsAnotherSessionLeftThem()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = Read(connection, "update t set v = v + 1 output inserted.id, inserted.v, inserted.pad where v >= 0");
        AreEqual(0, Attempt(other, "insert t (id, v) values (2500, 0)"));
        AreEqual(0, Attempt(other, "delete t where id = 1800"));
        AreEqual(0, Attempt(other, "update t set v = -5 where id = 1900"));
        AreEqual(Rows - 1, ReadRest(rows));
        AreEqual("1900:-5, 2500:1", string.Join(", ", Extensions.FirstColumn(other, "select concat(id, ':', v) from t where id in (1800, 1900, 2500) order by id")));
    }

    [TestMethod]
    public void Heap_WritesTheRowsItSends()
    {
        var sim = Big("create table h (id int not null, v int not null, pad char(2000) not null default 'x'); insert h (id, v) select value, 0 from generate_series(1, 2000)");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, "update h set v = v + 1 output inserted.id, inserted.v, inserted.pad");
        AreEqual($"OBJECT IX, RID Xx{Window}", Locks(other, spid));
        AreEqual(Window, Scalar(other, "select count(*) from h with (nolock) where v = 1"));
        AreEqual(Rows, ReadRest(rows));
    }

    /// <summary>
    /// A table with no nonclustered index sends each row before it deletes
    /// it, so the row the window ends on is held but not yet gone; with one,
    /// every row sent is gone.
    /// </summary>
    [TestMethod]
    [DataRow("", Rows - Window + 1, "KEY Xx20, OBJECT IX")]
    [DataRow("create index ixv on t (v)", Rows - Window, "KEY Xx40, OBJECT IX")]
    public void Delete_WritesTheRowsItSends(string index, int remaining, string locks)
    {
        var sim = Big(index);
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, "delete t output deleted.id, deleted.v, deleted.pad");
        AreEqual(locks, Locks(other, spid));
        AreEqual(remaining, Scalar(other, "select count(*) from t with (nolock)"));
        AreEqual(0, Attempt(other, "insert t (id, v) values (5000, 7)"));
        AreEqual(Rows + 1, ReadRest(rows));
        AreEqual(0, Scalar(other, "select count(*) from t"));
    }

    [TestMethod]
    public void Delete_ReferencedRowRefusedBeforeItIsSent()
    {
        var sim = Big("create table c (id int primary key, pid int not null references t (id)); insert c values (1, 1500)");
        using var connection = sim.CreateOpenConnection();
        using var rows = connection.CreateCommand("delete t output deleted.id, deleted.v, deleted.pad").ExecuteReader();
        AreEqual((1499, 547), ReadToError(rows));
        rows.Dispose();
        AreEqual(Rows, Scalar(connection, "select count(*) from t"));
    }

    [TestMethod]
    public void Insert_WritesTheRowsItSends()
    {
        var sim = Big("create table t2 (id int primary key, v int not null, pad char(2000) not null)");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, "insert t2 output inserted.id, inserted.v, inserted.pad select id, v, pad from t");
        AreEqual($"KEY Xx{Window}, OBJECT IS, OBJECT IX", Locks(other, spid));
        AreEqual(Window, Scalar(other, "select count(*) from t2 with (nolock)"));
        AreEqual(Rows, ReadRest(rows));
        AreEqual(Rows, rows.RecordsAffected);
    }

    /// <summary>A source read whole first lets an insert into its own table write as it sends, meeting none of its own rows.</summary>
    [TestMethod]
    public void InsertIntoItsSource_WritesTheRowsItSends()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = Read(connection, "insert t (id, v, pad) output inserted.id, inserted.v, inserted.pad select id + 10000, v, pad from t");
        AreEqual(Window, Scalar(other, "select count(*) from t with (nolock) where id > 10000"));
        AreEqual(Rows, ReadRest(rows));
        AreEqual(2 * Rows, Scalar(other, "select count(*) from t"));
    }

    [TestMethod]
    public void InsertWithIdentity_RecordsItsLastIdentity()
    {
        var sim = Big("create table ti (id int identity primary key, pad char(2000) not null)");
        using var connection = sim.CreateOpenConnection();
        using (var rows = Read(connection, "insert ti (pad) output inserted.id select pad from t"))
            AreEqual(Rows, ReadRest(rows));
        AreEqual(Rows, Convert.ToInt32(Scalar(connection, "select scope_identity()"), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The rows before a failing row go out ahead of its error, however few,
    /// and the statement is rolled back (probed 2026-10-09 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert s (id) output inserted.id values (2), (1), (3)", 1, 2627)]
    [DataRow("update t set v = case when id = 5 then 5000 else v + 1 end output inserted.id where id <= 10", 4, 547)]
    [DataRow("delete t output deleted.id, cast(case when deleted.id = 5 then 'x' else '1' end as int) where id <= 10", 4, 245)]
    [DataRow("update t set v = case when id = 1500 then 5000 else v + 1 end output inserted.id, inserted.pad", 1499, 547)]
    [DataRow("insert s output inserted.id, inserted.pad select id, pad from t where id <> 1 union all select 1, 'z'", Rows - 1, 2627)]
    [DataRow("merge s using (values (2), (1), (3)) x (id) on 1 = 0 when not matched then insert (id) values (x.id) output inserted.id;", 1, 2627)]
    public void RowsBeforeAnError_GoOutAheadOfIt(string sql, int read, int error)
    {
        var sim = Big("alter table t add constraint ck check (v < 1000); create table s (id int primary key, pad char(2000) not null default 'x'); insert s (id) values (1)");
        using var connection = sim.CreateOpenConnection();
        using (var rows = connection.CreateCommand(sql).ExecuteReader())
        {
            AreEqual((read, error), ReadToError(rows));
            AreEqual(-1, rows.RecordsAffected);
        }
        AreEqual(Rows, Scalar(connection, "select count(*) from t where v = 0"));
        AreEqual(1, Scalar(connection, "select count(*) from s"));
    }

    /// <summary>A TRY frame catching a row's error leaves the rows before it sent, and the statement counting 0.</summary>
    [TestMethod]
    public void RowsBeforeACaughtError_CountZero()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var rows = connection.CreateCommand("""
            begin try update t set v = case when id = 1500 then 1 / 0 else v + 1 end output inserted.id, inserted.pad; end try
            begin catch select error_number(); end catch
            """).ExecuteReader();
        AreEqual(1499, ReadRest(rows, 0));
        IsTrue(rows.NextResult());
        IsTrue(rows.Read());
        AreEqual(8134, rows.GetInt32(0));
        IsFalse(rows.NextResult());
        AreEqual(0, rows.RecordsAffected);
        AreEqual(0, Scalar(connection, "select count(*) from t where v >= 1"));
    }

    /// <summary>
    /// A row another session holds stops the statement where its walk meets
    /// it: the rows before it go out, then its lock timeout.
    /// </summary>
    [TestMethod]
    public void RowAnotherSessionHolds_StopsTheWriteWhereItIsMet()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var holding = other.BeginTransaction();
        using (var hold = other.CreateCommand("update t set v = -1 where id = 100"))
        {
            hold.Transaction = holding;
            _ = hold.ExecuteNonQuery();
        }
        using var rows = connection.CreateCommand("set lock_timeout 0; update t set v = v + 1 output inserted.id, inserted.v, inserted.pad").ExecuteReader();
        AreEqual((99, 1222), ReadToError(rows));
        rows.Dispose();
        holding.Rollback();
        AreEqual(0, Scalar(connection, "select count(*) from t where v >= 1"));
    }

    /// <summary>
    /// The shapes real's plan reads whole first hold U on every row they read
    /// until each is written: an update of a nonclustered index's column or a
    /// subquery in the statement, or a join reading the target again.
    /// </summary>
    [TestMethod]
    [DataRow("create index ixv on t (v)", "update t set v = v + 1 output inserted.id, inserted.v, inserted.pad", "KEY Ux1980, KEY Xx60, OBJECT IX")]
    [DataRow("", "update t set v = (select max(v) from t) + 1 output inserted.id, inserted.v, inserted.pad", "KEY Ux1980, KEY Xx20, OBJECT IX")]
    [DataRow("", "update a set v = b.v + 1 output inserted.id, inserted.v, inserted.pad from t a join t b on b.id = a.id", "KEY Ux1980, KEY Xx20, OBJECT IX")]
    [DataRow("", "delete t output deleted.id, deleted.v, deleted.pad where id in (select id from t where v = 0)", "KEY Ux1980, KEY Xx20, OBJECT IX")]
    public void ReadFirst_HoldsTheRowsAheadInU(string setup, string sql, string locks)
    {
        var sim = Big(setup);
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, sql);
        AreEqual(locks, Locks(other, spid));
        AreEqual(0, Attempt(other, "select v from t where id = 1500"));
        AreEqual(1222, Attempt(other, "update t set v = 7 where id = 1500"));
        AreEqual(Rows, ReadRest(rows));
    }

    /// <summary>
    /// The shapes real's plan writes whole first — an update of a clustered
    /// or unique key — write every row before the first goes out.
    /// </summary>
    [TestMethod]
    [DataRow("", "update t set id = id + 10000 output inserted.id, inserted.v, inserted.pad", "select count(*) from t with (nolock) where id > 10000")]
    [DataRow("update t set v = id; create unique index ux on t (v)", "update t set v = v + 100000 output inserted.id, inserted.v, inserted.pad", "select count(*) from t with (nolock) where v > 100000")]
    public void WholeFirst_WritesEveryRowFirst(string setup, string sql, string written)
    {
        var sim = Big(setup);
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = Read(connection, sql);
        AreEqual(Rows, Scalar(other, written));
        AreEqual(Rows, ReadRest(rows));
    }

    [TestMethod]
    public void JoinedUpdate_WritesTheRowsItSends()
    {
        var sim = Big("create table w (k int not null, w int not null); insert w select value, 3 from generate_series(1, 2000)");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, "update t set v = w.w output inserted.id, inserted.v, inserted.pad from t join w on w.k = t.id");
        AreEqual($"KEY Xx{Window}, OBJECT IS, OBJECT IX", Locks(other, spid));
        AreEqual(Window, Scalar(other, "select count(*) from t with (nolock) where v = 3"));
        AreEqual(Rows, ReadRest(rows));
    }

    /// <summary>
    /// A MERGE writes each action as it sends it — EF Core's insert shape
    /// matching no target row, and an upsert, whose matched rows ahead are
    /// held in U until each is written.
    /// </summary>
    [TestMethod]
    [DataRow("", "merge t2 using (select id, pad from t) as i on 1 = 0 when not matched then insert (id, v, pad) values (i.id, 5, i.pad) output inserted.id, inserted.pad;", "KEY Xx20, OBJECT IS, OBJECT IX", 20)]
    [DataRow("insert t2 select id, v, pad from t where id % 2 = 0", "merge t2 using t on t2.id = t.id when matched then update set v = t2.v + 1 when not matched then insert (id, v, pad) values (t.id, 5, t.pad) output inserted.id, $action, inserted.pad;", "KEY Ux990, KEY Xx20, OBJECT IS, OBJECT IX", 1010)]
    public void Merge_WritesTheActionsItSends(string setup, string sql, string locks, int seen)
    {
        var sim = Big("create table t2 (id int primary key, v int not null, pad char(2000) not null); " + setup);
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = Read(connection, sql);
        AreEqual(locks, Locks(other, spid));
        AreEqual(seen, Scalar(other, "select count(*) from t2 with (nolock)"));
        AreEqual(Rows, ReadRest(rows));
        AreEqual(Rows, Scalar(other, "select count(*) from t2"));
    }

    [TestMethod]
    public void WriteInATransaction_CancelRollsBackTheStatementOnly()
    {
        var sim = Big("create table m (i int)");
        using var connection = sim.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        var before = connection.CreateCommand("insert m values (1)");
        before.Transaction = transaction;
        _ = before.ExecuteNonQuery();
        var command = connection.CreateCommand("update t set v = v + 1 output inserted.id, inserted.v, inserted.pad");
        command.Transaction = transaction;
        var rows = command.ExecuteReader();
        IsTrue(rows.Read());
        command.Cancel();
        rows.Dispose();
        var check = connection.CreateCommand("select concat(@@trancount, ':', (select count(*) from t where v >= 1), ':', (select count(*) from m))");
        check.Transaction = transaction;
        AreEqual("1:0:1", check.ExecuteScalar());
    }

    [TestMethod]
    public void RowCount_SettlesWithTheLastRow()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        using var rows = connection.CreateCommand("update t set v = v + 1 output inserted.id, inserted.pad where id <= 1200; select @@rowcount").ExecuteReader();
        AreEqual(1200, ReadRest(rows, 0));
        IsTrue(rows.NextResult());
        IsTrue(rows.Read());
        AreEqual(1200, rows.GetInt32(0));
    }

    /// <summary>A command not reading its rows as they come writes them all first, as before.</summary>
    [TestMethod]
    public void ExecuteNonQuery_WritesTheWholeStatement()
    {
        var sim = Big();
        using var connection = sim.CreateOpenConnection();
        AreEqual(Rows, connection.CreateCommand("update t set v = v + 1 output inserted.id, inserted.pad").ExecuteNonQuery());
    }
}
