using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Where informational messages land relative to a batch's results and
/// errors, and which of them real follows with Msg 3621 or Msg 8153. Every
/// shape here was probed through SqlClient 7 against SQL Server 2025
/// (2026-09-23): each message fires its own <c>InfoMessage</c> event as the
/// reader reaches it, while an error carries the messages its stretch of the
/// batch sent — the whole batch for <c>ExecuteNonQuery</c>, up to the next
/// result set for <c>ExecuteReader</c>, nothing for <c>NextResult</c>.
/// </summary>
[TestClass]
public sealed class MessageStreamTests
{
    private const string Terminated = "The statement has been terminated.";
    private const string NullEliminated = "Warning: Null value is eliminated by an aggregate or other SET operation.";

    private static (SimulatedDbConnection Connection, List<string> Log) Open(string setup = "create table t (a int primary key); insert t values (1)")
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = setup;
            _ = command.ExecuteNonQuery();
        }
        var log = new List<string>();
        connection.InfoMessage += (_, e) => log.Add($"event {e.Errors[0].Number}: {e.Message}");
        return (connection, log);
    }

    private static SimulatedSqlException NonQueryError(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
    }

    private static string[] Numbers(SimulatedSqlException ex) =>
        [.. ex.Errors.Select(e => $"{e.Number}: {e.Message}")];

    [TestMethod]
    public void Reader_FiresEachMessageWhereTheReaderReachesIt()
    {
        var (connection, log) = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "print 'a'; print 'b'; select 1; print 'c'; select 2; print 'd'";
        using (var reader = command.ExecuteReader())
        {
            log.Add("returned");
            while (reader.Read())
                log.Add("row");
            _ = reader.NextResult();
            log.Add("second");
            IsFalse(reader.NextResult());
        }
        CollectionAssert.AreEqual(
            new[] { "event 0: a", "event 0: b", "returned", "row", "event 0: c", "second", "event 0: d" },
            log);
    }

    [TestMethod]
    public void RowWritingError_IsFollowedByMsg3621()
    {
        var (connection, _) = Open();
        var ex = NonQueryError(connection, "insert t values (1)");
        AreEqual(2627, ex.Number);
        HasCount(2, ex.Errors);
        AreEqual(3621, ex.Errors[1].Number);
        AreEqual<byte>(0, ex.Errors[1].Class);
        AreEqual<byte>(0, ex.Errors[1].State);
        AreEqual(ex.Errors[0].Message + Environment.NewLine + Terminated, ex.Message);
    }

    /// <summary>
    /// Only an execution error of a statement that writes rows is followed by
    /// Msg 3621 — not a compilation error, a SELECT's error, an error that ends
    /// the batch, or one a TRY / CATCH handles.
    /// </summary>
    [TestMethod]
    [DataRow("insert t values (1/0)", true)]
    [DataRow("update t set a = a + 2147483647", true)]
    [DataRow("delete t where 1/0 = 1", true)]
    [DataRow("select * into u from t where 1/0 = 1", true)]
    [DataRow("create table n (a tinyint); insert n values (300)", true)]
    [DataRow("create table c (a int check (a > 5)); insert c values (1)", true)]
    [DataRow("create table d (a date); insert d values (1)", false)]
    [DataRow("select 1/0", false)]
    [DataRow("set xact_abort on; insert t values (1)", false)]
    [DataRow("insert t values ('x')", false)]
    public void Msg3621FollowsOnlyARowWritingExecutionError(string sql, bool terminated)
    {
        var (connection, _) = Open();
        var ex = NonQueryError(connection, sql);
        AreEqual(terminated, ex.Errors.Any(e => e.Number == 3621), string.Join(" | ", Numbers(ex)));
    }

    [TestMethod]
    public void CaughtError_SendsNoMsg3621()
    {
        var (connection, log) = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "begin try insert t values (1) end try begin catch print 'caught' end catch";
        _ = command.ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "event 0: caught" }, log);
    }

    [TestMethod]
    public void AlterColumnDataError_IsFollowedByMsg3621()
    {
        var (connection, _) = Open("create table n (a int); insert n values (300)");
        var ex = NonQueryError(connection, "alter table n alter column a tinyint");
        CollectionAssert.AreEqual(new[] { 220, 3621 }, ex.Errors.Select(e => e.Number).ToArray());
    }

    /// <summary>
    /// <c>ExecuteNonQuery</c> reads the whole batch: every error comes first,
    /// then every message the batch sent, in order — the PRINT ahead of the
    /// first error included.
    /// </summary>
    [TestMethod]
    public void NonQuery_ErrorsThenMessages()
    {
        var (connection, log) = Open();
        var ex = NonQueryError(connection, "print 'p'; insert t values (1); print 'a'; insert t values (1); print 'b'");
        CollectionAssert.AreEqual(
            new[] { 2627, 2627, 0, 3621, 0, 3621, 0 },
            ex.Errors.Select(e => e.Number).ToArray());
        CollectionAssert.AreEqual(
            new[] { "p", Terminated, "a", Terminated, "b" },
            ex.Errors.Skip(2).Select(e => e.Message).ToArray());
        IsEmpty(log);
    }

    /// <summary>
    /// <c>ExecuteReader</c> reads to the first result set: messages ahead of
    /// its error fire as events, and the error carries the ones after it.
    /// </summary>
    [TestMethod]
    public void ExecuteReader_ErrorCarriesTheMessagesAfterIt()
    {
        var (connection, log) = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "print 'p'; insert t values (1); print 'after'";
        var ex = Throws<SimulatedSqlException>(command.ExecuteReader);
        CollectionAssert.AreEqual(new[] { 2627, 3621, 0 }, ex.Errors.Select(e => e.Number).ToArray());
        AreEqual("after", ex.Errors[2].Message);
        CollectionAssert.AreEqual(new[] { "event 0: p" }, log);
    }

    /// <summary>
    /// <c>NextResult</c> throws the error alone; the Msg 3621 after it, and any
    /// message that follows, fire as events on the next advance.
    /// </summary>
    [TestMethod]
    public void NextResult_ThrowsTheErrorAlone()
    {
        var (connection, log) = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select 0; insert t values (1); print 'a'; select 1";
        using var reader = command.ExecuteReader();
        var ex = Throws<SimulatedSqlException>(() => reader.NextResult());
        HasCount(1, ex.Errors);
        AreEqual(2627, ex.Number);
        IsEmpty(log);
        IsTrue(reader.NextResult());
        CollectionAssert.AreEqual(new[] { "event 3621: " + Terminated, "event 0: a" }, log);
    }

    [TestMethod]
    [DataRow("use master; use master", 5701, "Changed database context to 'master'.")]
    [DataRow("set language british", 5703, "Changed language setting to British.")]
    [DataRow("set language english", 5703, "Changed language setting to us_english.")]
    public void SessionChanges_AreAnnounced(string sql, int number, string message)
    {
        var (connection, log) = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
        AreEqual($"event {number}: {message}", log[0]);
        IsTrue(log.All(entry => entry == log[0]));
    }

    /// <summary>
    /// A sequence whose first cache block — 50 values by default — is longer
    /// than the values it has left warns once, ahead of the first draw's
    /// result; <c>NO CACHE</c> never warns.
    /// </summary>
    [TestMethod]
    [DataRow("create sequence s as tinyint start with 250", true)]
    [DataRow("create sequence s as int start with 1 increment by -1 minvalue -20", true)]
    [DataRow("create sequence s as tinyint start with 200", false)]
    [DataRow("create sequence s as int start with 1 maxvalue 100 cache 60", false)]
    [DataRow("create sequence s as tinyint start with 250 no cache", false)]
    public void SequenceShortOfItsCache_WarnsOnTheFirstDraw(string create, bool warns)
    {
        var (connection, log) = Open(create);
        using var command = connection.CreateCommand();
        command.CommandText = "select next value for s; select next value for s";
        using (var reader = command.ExecuteReader())
        {
            log.Add("returned");
            while (reader.NextResult())
            {
            }
        }
        var expected = warns
            ? new[] { "event 11729: The sequence object 's' cache size is greater than the number of available values.", "returned" }
            : ["returned"];
        CollectionAssert.AreEqual(expected, log);
    }

    /// <summary>
    /// An <c>IF</c> condition's warning comes ahead of the branch it chose.
    /// </summary>
    [TestMethod]
    public void NullEliminatedInAnIfCondition_WarnsBeforeTheBranch()
    {
        var (connection, log) = Open("create table g (x int, y int); insert g values (1, null)");
        using var command = connection.CreateCommand();
        command.CommandText = "if (select sum(y) from g) is null print 'n'";
        _ = command.ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "event 8153: " + NullEliminated, "event 0: n" }, log);
    }

    [TestMethod]
    public void NullEliminatedByAnAggregate_WarnsAfterTheRows()
    {
        var (connection, log) = Open("create table g (x int, y int); insert g values (1, null), (2, 3)");
        using var command = connection.CreateCommand();
        command.CommandText = "select x, sum(y), max(y) from g group by x; print 'next'";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                log.Add("row");
            IsFalse(reader.NextResult());
        }
        CollectionAssert.AreEqual(new[] { "row", "row", "event 8153: " + NullEliminated, "event 0: next" }, log);
    }

    /// <summary>
    /// No warning where no aggregate skipped a NULL, where the aggregate
    /// doesn't warn (<c>COUNT(*)</c>, <c>STRING_AGG</c>), or with
    /// <c>ANSI_WARNINGS</c> off; a window aggregate and a scalar subquery's
    /// aggregate do warn.
    /// </summary>
    [TestMethod]
    [DataRow("select sum(y) from g where y is not null", false)]
    [DataRow("select count(*) from g", false)]
    [DataRow("select string_agg(cast(y as varchar(5)), ',') from g", false)]
    [DataRow("set ansi_warnings off; select sum(y) from g", false)]
    [DataRow("select count(y) from g", true)]
    [DataRow("select sum(y) over () from g", true)]
    [DataRow("select (select avg(y) from g)", true)]
    [DataRow("declare @s int; select @s = min(y) from g", true)]
    [DataRow("select 1 where exists (select sum(y) from g)", false)]
    [DataRow("select * from g pivot (sum(y) for x in ([1], [2], [3])) p", false)]
    public void NullEliminatedWarning(string sql, bool warns)
    {
        var (connection, log) = Open("create table g (x int, y int); insert g values (1, null), (2, 3)");
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
        AreEqual(warns ? 1 : 0, log.Count(entry => entry.StartsWith("event 8153", StringComparison.Ordinal)));
    }
}
