using System.Data.Common;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A repeated <c>INSERT … VALUES</c>, <c>UPDATE</c> or <c>DELETE</c> runs from
/// a cached plan that skips its parse, so everything the statement reports has
/// to come out as a fresh parse of the same text reports it: its result sets,
/// its row counts, its errors with their number, state and line, the order
/// those arrive in, and what it leaves behind. Each test runs one text several
/// times against one simulation — the later runs replaying — and against a
/// second whose plan cache is emptied before every run, and compares the
/// transcripts.
/// </summary>
[TestClass]
public sealed class DmlPlanReplayTests
{
    private const string Tables = """
        create table p (id int primary key);
        create table t (
            id int identity primary key,
            name nvarchar(5) not null,
            v int not null check (v >= 0),
            pid int null references p(id),
            code char(3) null);
        insert p values (1), (2);
        """;

    private const string EfInsert = """
        SET IMPLICIT_TRANSACTIONS OFF;
        SET NOCOUNT ON;
        INSERT INTO [t] ([name], [v], [pid], [code])
        OUTPUT INSERTED.[id]
        VALUES (@p0, @p1, @p2, @p3);
        """;

    private const string EfUpdatePair = """
        SET NOCOUNT ON;
        UPDATE [t] SET [v] = @p0
        OUTPUT 1
        WHERE [id] = @p1;
        UPDATE [t] SET [name] = @p2
        OUTPUT 1
        WHERE [id] = @p3;
        """;

    /// <summary>One execution: statements to run first on the connection, then the text with these parameters.</summary>
    private sealed class Run(string? before, params (string Name, object Value)[] parameters)
    {
        public readonly string? Before = before;
        public readonly (string Name, object Value)[] Parameters = parameters;
    }

    private static Run With(params (string Name, object Value)[] parameters) => new(null, parameters);

    private static void AssertReplayMatchesFreshParse(string setup, string text, string state, params Run[] runs)
    {
        var replayed = Transcript(setup, text, state, runs, freshEachRun: false);
        var fresh = Transcript(setup, text, state, runs, freshEachRun: true);
        AreEqual(fresh, replayed);
    }

    private static string Transcript(string setup, string text, string state, Run[] runs, bool freshEachRun)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(setup);
        using var connection = simulation.CreateOpenConnection();
        var transcript = new StringBuilder();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => transcript.Append("info ").Append(e.Errors[0].Number).Append(' ').AppendLine(e.Message);
        foreach (var run in runs)
        {
            if (run.Before is { } before)
                Execute(connection, before, [], transcript);
            if (freshEachRun)
                Execute(connection, "dbcc freeproccache with no_infomsgs", [], new StringBuilder());
            _ = transcript.AppendLine("--");
            Execute(connection, text, run.Parameters, transcript);
            Execute(connection, state, [], transcript);
        }
        return transcript.ToString();
    }

    private static void Execute(DbConnection connection, string text, (string Name, object Value)[] parameters, StringBuilder transcript)
    {
        using var command = connection.CreateCommand(text, parameters);
        try
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                        _ = transcript.Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)).Append('|');
                    _ = transcript.AppendLine();
                }
            }
            while (reader.NextResult());
            _ = transcript.Append("affected ").Append(reader.RecordsAffected).AppendLine();
        }
        catch (SimulatedSqlException error)
        {
            foreach (var entry in error.Errors)
            {
                _ = transcript.Append("error ").Append(entry.Number).Append(" class ").Append(entry.Class).Append(" state ").Append(entry.State)
                    .Append(" line ").Append(entry.LineNumber).Append(": ").AppendLine(entry.Message);
            }
        }
    }

    private const string TableState = "select id, name, v, pid, code, @@trancount from t order by id";

    [TestMethod]
    public void EfInsert_ConstraintConversionAndTruncationErrors()
        => AssertReplayMatchesFreshParse(Tables, EfInsert, TableState + "; select scope_identity(), @@rowcount",
            With(("@p0", "a"), ("@p1", 1), ("@p2", 1), ("@p3", "aaa")),
            With(("@p0", "b"), ("@p1", -1), ("@p2", 1), ("@p3", "bbb")),
            With(("@p0", "c"), ("@p1", 2), ("@p2", 9), ("@p3", "ccc")),
            With(("@p0", "toolong"), ("@p1", 3), ("@p2", 1), ("@p3", "ddd")),
            With(("@p0", "e"), ("@p1", "x"), ("@p2", 1), ("@p3", "eee")),
            With(("@p0", "g"), ("@p1", 5), ("@p2", 2), ("@p3", "aaa")),
            With(("@p0", "h"), ("@p1", 6), ("@p2", 2), ("@p3", "hhh")));

    [TestMethod]
    public void NotNullViolation()
        => AssertReplayMatchesFreshParse(Tables, "insert into t (name, v) values (@n, case when @n = 'b' then null else 1 end)", TableState,
            With(("@n", "a")),
            With(("@n", "b")),
            With(("@n", "c")));

    [TestMethod]
    public void DuplicatePrimaryAndUniqueKeys()
        => AssertReplayMatchesFreshParse(
            "create table k (id int primary key, v int unique)",
            "insert into k (id, v) values (@id, @v)",
            "select id, v from k order by id",
            With(("@id", 1), ("@v", 1)),
            With(("@id", 1), ("@v", 2)),
            With(("@id", 2), ("@v", 1)),
            With(("@id", 2), ("@v", 3)));

    [TestMethod]
    public void EfUpdatePair_ErrorInEitherStatement_LinesAndContinuation()
        => AssertReplayMatchesFreshParse(
            Tables + "insert t (name, v, pid, code) values (N'a', 1, 1, 'aaa'), (N'b', 2, 2, 'bbb');",
            EfUpdatePair,
            TableState,
            With(("@p0", 10), ("@p1", 1), ("@p2", "bb"), ("@p3", 2)),
            With(("@p0", -5), ("@p1", 1), ("@p2", "cc"), ("@p3", 2)),
            With(("@p0", 11), ("@p1", 1), ("@p2", "waytoolong"), ("@p3", 2)),
            new Run("alter database scoped configuration set verbose_truncation_warnings = off", ("@p0", 11), ("@p1", 1), ("@p2", "waytoolong"), ("@p3", 2)),
            With(("@p0", 11), ("@p1", 1), ("@p2", "waytoolong"), ("@p3", 2)),
            new Run("alter database scoped configuration set verbose_truncation_warnings = on", ("@p0", 11), ("@p1", 1), ("@p2", "waytoolong"), ("@p3", 2)),
            With(("@p0", "nope"), ("@p1", 1), ("@p2", "dd"), ("@p3", 2)),
            With(("@p0", 12), ("@p1", 99), ("@p2", "ee"), ("@p3", 2)));

    [TestMethod]
    public void EfUpdatePair_UnderXactAbort_StopsTheBatch()
        => AssertReplayMatchesFreshParse(
            Tables + "insert t (name, v, pid, code) values (N'a', 1, 1, 'aaa'), (N'b', 2, 2, 'bbb');",
            EfUpdatePair,
            TableState,
            new Run("set xact_abort on", ("@p0", -5), ("@p1", 1), ("@p2", "cc"), ("@p3", 2)),
            With(("@p0", -6), ("@p1", 1), ("@p2", "dd"), ("@p3", 2)),
            new Run("set xact_abort off", ("@p0", 7), ("@p1", 1), ("@p2", "ee"), ("@p3", 2)));

    [TestMethod]
    public void Delete_ForeignKeyViolationThenSuccess()
        => AssertReplayMatchesFreshParse(
            Tables + "insert t (name, v, pid) values (N'a', 1, 1);",
            "SET NOCOUNT ON;\nDELETE FROM [p]\nOUTPUT 1\nWHERE [id] = @p0;",
            "select id from p order by id",
            With(("@p0", 1)),
            With(("@p0", 2)),
            new Run("delete t", ("@p0", 1)),
            With(("@p0", 1)));

    [TestMethod]
    public void TriggerRollback_OnTheEfTriggerShape()
        => AssertReplayMatchesFreshParse(
            """
            create table tt (id int primary key, v int not null);
            insert tt values (1, 1);
            create table audit (v int);
            """,
            "SET NOCOUNT ON;\nUPDATE [tt] SET [v] = @p0\nWHERE [id] = @p1;\nSELECT @@ROWCOUNT;",
            "select v, @@trancount from tt; select count(*) from audit",
            new Run("""
                create trigger tt_guard on tt after update as
                begin
                    insert audit select v from inserted;
                    if exists (select * from inserted where v > 100)
                        rollback;
                end
                """, ("@p0", 5), ("@p1", 1)),
            With(("@p0", 500), ("@p1", 1)),
            With(("@p0", 6), ("@p1", 1)),
            new Run("disable trigger tt_guard on tt", ("@p0", 600), ("@p1", 1)),
            new Run("enable trigger tt_guard on tt", ("@p0", 700), ("@p1", 1)));

    [TestMethod]
    public void TriggeredTarget_ClientOutputFollowsTheTriggersEnabledState()
        => AssertReplayMatchesFreshParse(
            """
            create table tt (id int primary key, v int not null);
            insert tt values (1, 1);
            """,
            "UPDATE [tt] SET [v] = @p0 OUTPUT 1 WHERE [id] = @p1;",
            "select v from tt",
            new Run("create trigger tt_noop on tt after update as return", ("@p0", 5), ("@p1", 1)),
            new Run("disable trigger tt_noop on tt", ("@p0", 6), ("@p1", 1)),
            With(("@p0", 7), ("@p1", 1)),
            new Run("enable trigger tt_noop on tt", ("@p0", 8), ("@p1", 1)));

    [TestMethod]
    public void DivideByZero_InTheSetList()
        => AssertReplayMatchesFreshParse(
            "create table d (id int primary key, v int); insert d values (1, 10)",
            "update d set v = v / @x where id = 1",
            "select v from d",
            With(("@x", 2)),
            With(("@x", 0)),
            new Run("set arithabort off; set ansi_warnings off", ("@x", 0)),
            new Run("set arithabort on; set ansi_warnings on", ("@x", 5)));

    [TestMethod]
    public void IdentityInsert_IsReadAsTheStatementRuns()
        => AssertReplayMatchesFreshParse(
            Tables,
            "insert into t (id, name, v) values (@id, @n, @v)",
            TableState,
            With(("@id", 10), ("@n", "a"), ("@v", 1)),
            new Run("set identity_insert t on", ("@id", 11), ("@n", "b"), ("@v", 2)),
            new Run("set identity_insert t off", ("@id", 12), ("@n", "c"), ("@v", 3)));

    [TestMethod]
    public void TopParameter_TrimsTheParsedTuplesPerRun()
        => AssertReplayMatchesFreshParse(
            "create table n (v int)",
            "insert top (@n) into n values (1), (2), (3)",
            "select count(*) from n",
            With(("@n", 1)),
            With(("@n", 3)),
            With(("@n", 2)));

    [TestMethod]
    public void TopParameterAndRowcount_OnUpdateAndDelete()
        => AssertReplayMatchesFreshParse(
            "create table n (id int primary key, v int); insert n values (1, 0), (2, 0), (3, 0), (4, 0)",
            "update top (@n) n set v = v + 1; delete top (@d) n where v > 1",
            "select id, v from n order by id",
            With(("@n", 1), ("@d", 0)),
            With(("@n", 3), ("@d", 1)),
            new Run("set rowcount 1", ("@n", 4), ("@d", 5)),
            new Run("set rowcount 0", ("@n", 4), ("@d", 5)));

    [TestMethod]
    public void OutputStar_FollowsASchemaChange()
        => AssertReplayMatchesFreshParse(
            "create table s (id int primary key, v int)",
            "insert into s (id, v) output inserted.* values (@id, @v)",
            "select count(*) from s",
            With(("@id", 1), ("@v", 1)),
            With(("@id", 2), ("@v", 2)),
            new Run("alter table s add w int null", ("@id", 3), ("@v", 3)),
            With(("@id", 4), ("@v", 4)));

    [TestMethod]
    public void InsideAnExplicitTransaction()
        => AssertReplayMatchesFreshParse(
            Tables,
            "insert into t (name, v) values (@n, @v); update t set v = v + 1 where name = @n",
            TableState,
            new Run("begin tran", ("@n", "a"), ("@v", 1)),
            new Run("rollback", ("@n", "b"), ("@v", 2)),
            new Run("begin tran", ("@n", "c"), ("@v", -1)),
            new Run("commit", ("@n", "d"), ("@v", 4)));

    [TestMethod]
    public void ErrorLine_OfAReplayedStatementDeepInABatch()
        => AssertReplayMatchesFreshParse(
            Tables,
            """
            set nocount on;

            insert into t (name, v) values (@n, 1);


            update t
               set v = @v
             where name = @n;
            select @@error, @@rowcount;
            """,
            TableState,
            With(("@n", "a"), ("@v", 2)),
            With(("@n", "b"), ("@v", -2)),
            With(("@n", "c"), ("@v", "x")));

    [TestMethod]
    public void ReplayInACatchingTryBlock_AndOutsideIt()
        => AssertReplayMatchesFreshParse(
            Tables,
            """
            insert into t (name, v) values (@n, @v);
            begin try
                insert into t (name, v) values (@n, @v);
            end try
            begin catch
                select error_number(), error_line();
            end catch
            """,
            TableState,
            With(("@n", "a"), ("@v", 1)),
            With(("@n", "b"), ("@v", -1)),
            With(("@n", "c"), ("@v", 2)));

    [TestMethod]
    public void IdentityRowversionDefaultsAndComputedColumns_EvaluatePerRun()
        => AssertReplayMatchesFreshParse(
            """
            create sequence s start with 100;
            create table g (
                id int identity(10, 5) primary key,
                v int not null,
                d int not null default (next value for s),
                c as v * 2,
                rv rowversion);
            """,
            "insert into g (v) output inserted.id, inserted.d, inserted.c values (@v); update g set v = v + @v output inserted.c where id = scope_identity()",
            "select id, v, d, c, rv, @@dbts from g order by id",
            With(("@v", 1)),
            With(("@v", 2)),
            With(("@v", 3)));

    [TestMethod]
    public void QueryStore_RecordsReplayedStatementsAsParsedOnes()
        => AssertReplayMatchesFreshParse(
            Tables + "alter database current set query_store (query_capture_mode = all);",
            "SET NOCOUNT ON;\nINSERT INTO [t] ([name], [v])\nOUTPUT INSERTED.[id]\nVALUES (@p0, @p1);\nUPDATE [t] SET [v] = @p1 + 1\nWHERE [name] = @p0;",
            """
            select qt.query_sql_text, sum(rs.count_executions), min(rs.execution_type)
            from sys.query_store_query_text qt
            join sys.query_store_query q on q.query_text_id = qt.query_text_id
            join sys.query_store_plan p on p.query_id = q.query_id
            join sys.query_store_runtime_stats rs on rs.plan_id = p.plan_id
            where qt.query_sql_text like '%[[]t]%'
            group by qt.query_sql_text
            order by 1
            """,
            With(("@p0", "a"), ("@p1", 1)),
            With(("@p0", "b"), ("@p1", -5)),
            With(("@p0", "c"), ("@p1", 3)));

    [TestMethod]
    public void ReplayOnAnotherSession_AfterTheRecordingSessionCloses()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table r (id int primary key, v int); insert r values (1, 0)");
        const string text = "update r set v = v + @d output inserted.v where id = 1";
        for (var run = 1; run <= 3; run++)
        {
            using var connection = simulation.CreateOpenConnection();
            using var command = connection.CreateCommand(text, ("@d", run));
            AreEqual(run * (run + 1) / 2, command.ExecuteScalar());
        }
    }

    [TestMethod]
    public void Replay_WaitsOnAnotherSessionsLockWithItsOwnTimeout()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table r (id int primary key, v int); insert r values (1, 0), (2, 0)");
        const string text = "update r set v = v + 1 where id = @id";
        using var first = simulation.CreateOpenConnection();
        using (var command = first.CreateCommand(text, ("@id", 2)))
            AreEqual(1, command.ExecuteNonQuery());

        using var holder = simulation.CreateOpenConnection();
        using (var command = holder.CreateCommand("begin tran; update r set v = 5 where id = 1"))
            _ = command.ExecuteNonQuery();

        using var replayer = simulation.CreateOpenConnection();
        using (var command = replayer.CreateCommand("set lock_timeout 50"))
            _ = command.ExecuteNonQuery();
        using (var command = replayer.CreateCommand(text, ("@id", 1)))
            AreEqual(1222, Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Number);
    }

    [TestMethod]
    public void ConcurrentReplays_OfOneUpdatePlan_ApplyEveryIncrement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table c (id int primary key, v int not null)");
        const int workers = 8, iterations = 200;
        using (var setup = simulation.CreateOpenConnection())
        {
            for (var id = 0; id < workers; id++)
            {
                using var command = setup.CreateCommand("insert into c (id, v) values (@id, 0)", ("@id", id));
                _ = command.ExecuteNonQuery();
            }
        }

        _ = Parallel.For(0, workers, worker =>
        {
            using var connection = simulation.CreateOpenConnection();
            for (var i = 0; i < iterations; i++)
            {
                using var command = connection.CreateCommand("SET NOCOUNT ON;\nUPDATE [c] SET [v] = [v] + @d\nOUTPUT INSERTED.[v]\nWHERE [id] = @id;", ("@d", worker + 1), ("@id", worker));
                AreEqual((i + 1) * (worker + 1), command.ExecuteScalar());
            }
        });
        AreEqual(iterations * workers * (workers + 1) / 2, simulation.ExecuteScalar("select sum(v) from c"));
    }
}
