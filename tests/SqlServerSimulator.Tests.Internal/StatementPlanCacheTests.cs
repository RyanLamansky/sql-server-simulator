using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which <c>SELECT</c> statements, and which statements of a procedure,
/// trigger, function or dynamic batch's body, run from a cached statement plan,
/// observed through <see cref="Simulation.SelectPlanHits"/> and
/// <see cref="Simulation.DmlPlanHits"/>: a body's queries and writes replay
/// from the call after the one that recorded them, while one reading what
/// belongs to its call or session, or compiling every time it runs, parses
/// each time. Whether a replay reports what a fresh parse reports is
/// <c>StatementPlanReplayTests</c>' (public API) to show.
/// </summary>
[TestClass]
public sealed class StatementPlanCacheTests
{
    private const string Setup = """
        create table t (id int primary key, v int not null);
        insert t values (1, 1), (2, 2);
        create table log (id int, v int);
        """;

    private static void Execute(DbConnection connection, string text, CommandType commandType = CommandType.Text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandType = commandType;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    /// <summary>
    /// Runs <see cref="Setup"/> and <paramref name="before"/> (batches split on
    /// <c>go</c> lines), then <paramref name="text"/> three times, and returns
    /// the <c>SELECT</c> and DML statements it replayed.
    /// </summary>
    private static (long Selects, long Writes) ReplaysOverThreeRuns(string before, string text, CommandType commandType = CommandType.Text)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        foreach (var batch in before.Split("\ngo\n", StringSplitOptions.RemoveEmptyEntries))
            Execute(connection, batch);
        var (selects, writes) = (simulation.SelectPlanHits, simulation.DmlPlanHits);
        for (var run = 0; run < 3; run++)
            Execute(connection, text, commandType);
        return (simulation.SelectPlanHits - selects, simulation.DmlPlanHits - writes);
    }

    private const string Procedure = """
        create procedure p as
        set nocount on;
        declare @n int;
        select @n = count(*) from t;
        if @n > 0
        begin
            select id, v from t where id = @n;
            update t set v = v + 1 where id = 1;
        end
        insert log values (@n, 0)
        """;

    [TestMethod]
    public void Procedure_ReplaysItsQueriesAndWrites()
        => AreEqual((4L, 4L), ReplaysOverThreeRuns(Procedure, "exec p"));

    [TestMethod]
    public void Procedure_CalledAsAnRpc_Replays()
        => AreEqual((4L, 4L), ReplaysOverThreeRuns(Procedure, "p", CommandType.StoredProcedure));

    [TestMethod]
    public void Procedure_InALoop_ReplaysEveryCallAfterTheFirst()
        => AreEqual((8L, 8L), ReplaysOverThreeRuns(
            "create procedure q @i int as select v from t where id = @i; update t set v = v + 1 where id = @i",
            "declare @i int = 0; while @i < 3 begin exec q @i; set @i += 1 end"));

    [TestMethod]
    public void ProcedureCreatedWithRecompile_ParsesEveryCall()
        => AreEqual((0L, 0L), ReplaysOverThreeRuns(Procedure.Replace("create procedure p as", "create procedure p with recompile as", StringComparison.Ordinal), "exec p"));

    [TestMethod]
    public void ExecWithRecompile_ParsesEveryCall()
        => AreEqual((0L, 0L), ReplaysOverThreeRuns(Procedure, "exec p with recompile"));

    [TestMethod]
    public void OptionRecompile_ParsesItsStatementEveryCall()
        => AreEqual((0L, 2L), ReplaysOverThreeRuns(
            "create procedure r as select v from t where id = 1 option (recompile); update t set v = 0 where id = 2",
            "exec r"));

    [TestMethod]
    public void CallersTempTable_ParsesEveryCall()
        => AreEqual((0L, 2L), ReplaysOverThreeRuns(
            "create table #c (a int)\ngo\ncreate procedure c as select a from #c; update t set v = 0 where id = 2",
            "exec c"));

    [TestMethod]
    public void TableValuedParameter_ParsesEveryCall()
        => AreEqual((0L, 0L), ReplaysOverThreeRuns(
            "create type ids as table (id int)\ngo\ncreate procedure tv @ids ids readonly as select id from @ids; update t set v = 0 where id in (select id from @ids)",
            "declare @x ids; insert @x values (1); exec tv @x"));

    [TestMethod]
    public void Trigger_ReplaysItsQueriesAndWrites()
        => AreEqual((2L, 4L + 2), ReplaysOverThreeRuns(
            "create trigger tr on log after insert as begin set nocount on; select count(*) from t; update t set v = v + 1 where id = 2; insert t select id + 100, v from inserted where 1 = 0 end",
            "insert log values (1, 1)"));

    [TestMethod]
    public void Trigger_ReplaysWhatReadsPseudoTables()
        => AreEqual((2L, 2L + 2), ReplaysOverThreeRuns(
            "create trigger tr on log after insert as begin set nocount on; select count(*) from inserted i join deleted d on d.id = i.id; insert t select id + 100, v from inserted where 1 = 0 end",
            "insert log values (1, 1)"));

    /// <summary>
    /// A firing nested inside another on the same table reads tables of its
    /// own, so its statements parse, and leave the outer firing's plans be.
    /// </summary>
    [TestMethod]
    public void Trigger_NestedFiringOnItsOwnTable_ParsesWithoutDisplacingTheOuterPlans()
        => AreEqual((0L, 2L + 4), ReplaysOverThreeRuns(
            "alter database current set recursive_triggers on\ngo\ncreate trigger tr on log after insert as begin set nocount on; insert t select id + 100 * trigger_nestlevel(), v from inserted where 1 = 0; if trigger_nestlevel() < 2 insert log select id, v from inserted end",
            "insert log values (1, 1)"));

    [TestMethod]
    public void Trigger_InsteadOfThroughAView_Replays()
        => AreEqual((2L, 2L), ReplaysOverThreeRuns(
            "create view lv as select id, v from log\ngo\ncreate trigger tr on lv instead of insert as begin set nocount on; select count(*) from inserted; insert log select id, v from inserted end",
            "insert lv values (1, 1)"));

    /// <summary>
    /// The <c>MERGE</c> fires the trigger for its update and its insert, and the
    /// <c>DELETE</c> once more, so of the body's nine runs the first records
    /// and the other eight replay, beside the batch's own two writes' two
    /// replays each.
    /// </summary>
    [TestMethod]
    public void Trigger_FiredForEachMergeAction_Replays()
        => AreEqual((2L, 2L + 2 + 8), ReplaysOverThreeRuns(
            "create trigger tr on t after insert, update, delete as begin set nocount on; insert log select coalesce(i.id, d.id), i.v from inserted i full join deleted d on d.id = i.id end",
            "merge t using (values (1, 5), (9, 9)) s (id, v) on t.id = s.id when matched then update set v = s.v when not matched then insert values (s.id, s.v); select count(*) from t; delete t where id = 9"));

    /// <summary>
    /// A <c>GLOBAL</c> cursor left open over <c>inserted</c> keeps the firing's
    /// tables, so the next firing builds a new pair and records afresh.
    /// </summary>
    [TestMethod]
    public void Trigger_LeavingAGlobalCursorOpenOverItsRows_RecordsEachFiring()
        => AreEqual((0L, 2L), ReplaysOverThreeRuns(
            "create trigger tr on log after insert as begin set nocount on; if cursor_status('global', 'gc') >= -1 deallocate gc; declare gc cursor global for select id from inserted; open gc; select count(*) from inserted end",
            "insert log values (1, 1)"));

    [TestMethod]
    public void ScalarFunction_ReplaysItsQuery()
        => AreEqual((5L, 0L), ReplaysOverThreeRuns(
            "create function dbo.f(@id int) returns int with inline = off as begin declare @v int; select @v = v from t where id = @id; return @v end",
            "select dbo.f(id) from t"));

    [TestMethod]
    public void DynamicBatch_Replays()
        => AreEqual((2L, 2L), ReplaysOverThreeRuns(
            "",
            "exec sp_executesql N'select v from t where id = @id; update t set v = v + 1 where id = @id', N'@id int', @id = 1"));

    [TestMethod]
    public void AlteredProcedure_RecordsAgainThenReplays()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        Execute(connection, "create procedure a as select v from t where id = 1");
        Execute(connection, "exec a");
        Execute(connection, "exec a");
        AreEqual(1L, simulation.SelectPlanHits);
        Execute(connection, "alter procedure a as select v from t where id = 2");
        var recorded = simulation.SelectPlanRecordings;
        Execute(connection, "exec a");
        AreEqual(recorded + 1, simulation.SelectPlanRecordings);
        Execute(connection, "exec a");
        AreEqual(2L, simulation.SelectPlanHits);
        Execute(connection, "dbcc freeproccache");
        Execute(connection, "exec a");
        AreEqual(2L, simulation.SelectPlanHits);
    }
}
