using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A trigger's result set goes out as its client reads it while the statement
/// that fired it waits mid-way, as real sends it (probed 2026-10-09 against
/// SQL Server 2025 with a reader two rows into a trigger's 2,000 rows of
/// <c>char(2000)</c>): the firing statement holds its writes' locks and the
/// trigger's read its position, the trigger's statements after its
/// <c>SELECT</c> haven't run, and the request reads <c>suspended</c> on
/// <c>ASYNC_NETWORK_IO</c>. Closing the reader or its connection runs the rest
/// to its end; an error in the rows rolls the firing statement back.
/// </summary>
[TestClass]
public sealed class TriggerStreamingTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int Rows = 2000;

    private static Simulation WithTrigger(string body = "print 'before'; select k, v from big order by k; print 'after'; insert l values ('after select');")
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"""
            create table t (id int primary key, v int not null); insert t select value, value from generate_series(1, 10);
            create table big (k int primary key, v char(2000) not null, s varchar(10) null);
            insert big select value, 'x', cast(value as varchar(10)) from generate_series(1, {Rows});
            update big set s = 'x' where k = 1500;
            create table l (m varchar(50));
            """,
            $"create trigger tr on t after update, insert as begin {body} end");
        return sim;
    }

    private static short Spid(DbConnection connection) => (short)connection.CreateCommand("select @@spid").ExecuteScalar()!;

    private static string Locks(DbConnection observer, short spid) => string.Join(", ", Extensions.FirstColumn(observer, $"""
        select concat(resource_type, ' ', request_mode, iif(count(*) > 1, concat('x', count(*)), ''))
        from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE'
        group by resource_type, request_mode order by resource_type, request_mode
        """));

    private static object? Scalar(DbConnection connection, string sql) => connection.CreateCommand(sql).ExecuteScalar();

    private static DbDataReader ReadTwo(DbConnection connection, string sql = "update t set v = v + 1 where id = 1; select 'next'")
    {
        var reader = connection.CreateCommand(sql).ExecuteReader();
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

    [TestMethod]
    public void TriggerRows_GoOutWhileTheFiringStatementWaits()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        List<string> messages = [];
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.Add(e.Message);
        using (var rows = ReadTwo(connection))
        {
            AreEqual("before", string.Join(",", messages));
            AreEqual("KEY S, KEY X, OBJECT IS, OBJECT IX", Locks(other, spid));
            AreEqual("suspended ASYNC_NETWORK_IO SELECT", string.Join(", ", Extensions.FirstColumn(other, $"select concat(status, ' ', wait_type, ' ', command) from sys.dm_exec_requests where session_id = {spid}")));
            AreEqual(0, Scalar(other, "select count(*) from l with (nolock)"));
            AreEqual(Rows, ReadRest(rows));
            IsTrue(rows.NextResult());
            AreEqual("before,after", string.Join(",", messages));
            IsTrue(rows.Read());
            AreEqual("next", rows.GetString(0));
        }
        AreEqual(1, Scalar(other, "select count(*) from l"));
        AreEqual(2, Scalar(other, "select v from t where id = 1"));
        AreEqual("", Locks(other, spid));
    }

    [TestMethod]
    public void RepeatableRead_HoldsTheTriggersRowsProducedSoFar()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = ReadTwo(connection, "set transaction isolation level repeatable read; update t set v = v + 1 where id = 1");
        AreEqual("KEY Sx20, KEY X, OBJECT IS, OBJECT IX", Locks(other, spid));
        AreEqual(Rows, ReadRest(rows));
    }

    [TestMethod]
    [DataRow("after insert")]
    [DataRow("instead of update")]
    public void EveryTriggerKind_Streams(string kind)
    {
        var sim = WithTrigger();
        sim.ExecuteBatches("drop trigger tr", $"create trigger tr2 on t {kind} as begin select k, v from big order by k; insert l values ('after select'); end");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = ReadTwo(connection, kind.EndsWith("insert", StringComparison.Ordinal) ? "insert t values (100, 100)" : "update t set v = v + 1 where id = 1");
        AreEqual(0, Scalar(other, "select count(*) from l with (nolock)"));
        AreEqual(Rows, ReadRest(rows));
        AreEqual(1, Scalar(other, "select count(*) from l"));
    }

    [TestMethod]
    public void ClosingTheReader_RunsTheRestAndCommits()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        ReadTwo(connection).Dispose();
        AreEqual(1, Scalar(other, "select count(*) from l"));
        AreEqual(2, Scalar(other, "select v from t where id = 1"));
    }

    [TestMethod]
    public void ClosingTheConnection_RunsTheRestAndCommits()
    {
        var sim = WithTrigger();
        var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var rows = ReadTwo(connection, "begin tran; update t set v = v + 1 where id = 1; insert l values ('after'); commit");
        connection.Close();
        AreEqual(2, Scalar(other, "select count(*) from l"));
        AreEqual(2, Scalar(other, "select v from t where id = 1"));
        _ = Throws<InvalidOperationException>(() => rows.Read());
        connection.Dispose();
    }

    [TestMethod]
    public void ErrorInTheTriggersRows_RollsTheFiringStatementBack()
    {
        var sim = WithTrigger("select k, cast(s as int) from big order by k; insert l values ('after select');");
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using (var rows = ReadTwo(connection))
        {
            var read = 2;
            var error = Throws<SimulatedSqlException>(() =>
            {
                while (rows.Read())
                    read++;
            });
            AreEqual(245, error.Number);
            AreEqual("tr", error.Procedure);
            AreEqual(1499, read);
        }
        AreEqual(0, Scalar(other, "select count(*) from l"));
        AreEqual(1, Scalar(other, "select v from t where id = 1"));
        AreEqual("", Locks(other, spid));
    }

    /// <summary>
    /// Another command of the session meets the firing statement mid-way as it
    /// meets a DML statement's <c>OUTPUT</c> rows going out: it waits, and a
    /// <c>CommandTimeout</c> shorter than real's wait ends it first (probed
    /// 2026-10-09 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AnotherCommand_WaitsOnTheFiringStatement()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var rows = ReadTwo(connection);
        using var probe = connection.CreateCommand("select 1");
        probe.CommandTimeout = 1;
        AreEqual(-2, Throws<SimulatedSqlException>(probe.ExecuteScalar).Number);
        AreEqual(Rows, ReadRest(rows));
    }

    /// <summary>
    /// A write of another connection the same thread runs meets the firing
    /// statement's lock as any other session's — a wait its lock timeout ends —
    /// not as a lock the thread itself holds, since the statement waits on its
    /// client rather than running.
    /// </summary>
    [TestMethod]
    public void AnotherSessionOnTheSameThread_WaitsAsOnAnyOther()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using var rows = ReadTwo(connection);
        AreEqual(1222, Throws<SimulatedSqlException>(() => other.CreateCommand("set lock_timeout 0; update t set v = 0 where id = 1").ExecuteNonQuery()).Number);
        AreEqual(Rows, ReadRest(rows));
    }

    [TestMethod]
    public void Kill_EndsTheFiringStatement()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        using var rows = ReadTwo(connection);
        _ = other.CreateCommand($"kill {spid}").ExecuteNonQuery();
        AreEqual("", Locks(other, spid));
        AreEqual(1, Scalar(other, "select v from t where id = 1"));
        AreEqual(0, ThrowsExactly<SimulatedSqlException>(() => rows.Read()).Number);
    }

    [TestMethod]
    public void Cancel_EndsTheFiringStatement()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        var spid = Spid(connection);
        var command = connection.CreateCommand("update t set v = v + 1 where id = 1");
        using var rows = command.ExecuteReader();
        IsTrue(rows.Read());
        command.Cancel();
        AreEqual(0, ThrowsExactly<SimulatedSqlException>(() => ReadRest(rows)).Number);
        AreEqual(1, Scalar(other, "select v from t where id = 1"));
        AreEqual("", Locks(other, spid));
    }

    /// <summary>The trigger's statements run on the engine's invariant culture on the statement's own thread too.</summary>
    [TestMethod]
    public void TriggerRows_FormatInvariantly()
    {
        var sim = WithTrigger("select k, cast(k * 1.5 as varchar(20)) as f, v from big order by k;");
        using var connection = sim.CreateOpenConnection();
        using var rows = ReadTwo(connection);
        AreEqual("3.0", rows.GetString(1));
        AreEqual(Rows, ReadRest(rows));
    }

    [TestMethod]
    public void ExecuteNonQuery_RunsTheTriggerWhole()
    {
        var sim = WithTrigger();
        using var connection = sim.CreateOpenConnection();
        AreEqual(2, connection.CreateCommand("update t set v = v + 1 where id = 1").ExecuteNonQuery());
        AreEqual(1, Scalar(connection, "select count(*) from l"));
    }
}
