using System.Data;
using System.Data.Common;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A repeated <c>SELECT</c> — at the top of a batch or in a procedure,
/// trigger, function or dynamic batch's body, as are the DML statements of
/// such a body — runs from a cached plan that skips its parse, so everything
/// the call reports has to come out as a fresh parse reports it: its result
/// sets, row counts, errors with their number, state, line and procedure, the
/// order those arrive in, and what the call leaves behind. Each test runs one
/// text several times against one simulation — the later runs replaying — and
/// against a second whose plan cache is emptied before every run, and compares
/// the transcripts.
/// </summary>
[TestClass]
public sealed class StatementPlanReplayTests
{
    private const string Tables = """
        create table t (id int primary key, name nvarchar(5) not null, v int not null check (v >= 0));
        insert t values (1, N'a', 1), (2, N'b', 2), (3, N'c', 3);
        create table log (n int identity primary key, id int, v int);
        """;

    private const string TableState = "select id, name, v from t order by id; select id, v from log order by n";

    /// <summary>One execution: statements to run first on the connection, then the text with these parameters.</summary>
    private sealed class Run(string? before, params (string Name, object Value)[] parameters)
    {
        public readonly string? Before = before;
        public readonly (string Name, object Value)[] Parameters = parameters;
    }

    private static Run With(params (string Name, object Value)[] parameters) => new(null, parameters);

    private static Run After(string before, params (string Name, object Value)[] parameters) => new(before, parameters);

    private static void AssertReplayMatchesFreshParse(string setup, string text, string state, params Run[] runs) =>
        AssertReplayMatchesFreshParse(setup, text, state, CommandType.Text, runs);

    private static void AssertReplayMatchesFreshParse(string setup, string text, string state, CommandType commandType, params Run[] runs)
    {
        var replayed = Transcript(setup, text, state, commandType, runs, freshEachRun: false);
        var fresh = Transcript(setup, text, state, commandType, runs, freshEachRun: true);
        AreEqual(fresh, replayed);
    }

    private static string Transcript(string setup, string text, string state, CommandType commandType, Run[] runs, bool freshEachRun)
    {
        var simulation = new Simulation();
        foreach (var batch in setup.Split("\ngo\n"))
            _ = simulation.ExecuteNonQuery(batch);
        using var connection = simulation.CreateOpenConnection();
        var transcript = new StringBuilder();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => transcript.Append("info ").Append(e.Errors[0].Number).Append(' ').Append(e.Errors[0].Procedure).Append(' ').AppendLine(e.Message);
        // Every run twice, so a fresh parse's transcript sets each beside a
        // replay's, whichever run records the plan.
        foreach (var run in runs.SelectMany(run => new[] { run, run }))
        {
            if (run.Before is { } before)
                Execute(connection, before, CommandType.Text, [], transcript);
            // Cleared from a dbo session of its own, since a run may impersonate
            // a principal FREEPROCCACHE refuses.
            if (freshEachRun)
                _ = simulation.ExecuteNonQuery("dbcc freeproccache with no_infomsgs");
            _ = transcript.AppendLine("--");
            Execute(connection, text, commandType, run.Parameters, transcript);
            Execute(connection, state, CommandType.Text, [], transcript);
        }
        return transcript.ToString();
    }

    private static void Execute(DbConnection connection, string text, CommandType commandType, (string Name, object Value)[] parameters, StringBuilder transcript)
    {
        using var command = connection.CreateCommand(text, parameters);
        command.CommandType = commandType;
        try
        {
            using var reader = command.ExecuteReader();
            do
            {
                _ = transcript.Append("set ").Append(reader.FieldCount).AppendLine();
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
                    .Append(" line ").Append(entry.LineNumber).Append(" in ").Append(entry.Procedure).Append(": ").AppendLine(entry.Message);
            }
        }
    }

    [TestMethod]
    public void SelectSequence_ErrorsSettleAsParsedOnes()
        => AssertReplayMatchesFreshParse(Tables,
            "\nselect id, 10 / (v - @z) from t order by id;\nselect count(*), @@error from t",
            "select @@rowcount, @@error, @@trancount; " + TableState,
            With(("@z", 0)),
            With(("@z", 2)),
            After("set xact_abort on; begin tran; update t set v = 9 where id = 3", ("@z", 1)),
            After("set xact_abort off; begin tran; update t set v = 8 where id = 3", ("@z", 1)),
            After("rollback", ("@z", 3)));

    [TestMethod]
    public void MixedBatch_SelectsBesideStatementsWithNoPlan()
        => AssertReplayMatchesFreshParse(Tables,
            "SET NOCOUNT ON;\nDECLARE @n int = @z;\nSELECT [id], [v] / @n FROM [t] WHERE [id] >= @lo ORDER BY [id];\nUPDATE [t] SET [v] = [v] + @n WHERE [id] = @lo;\nSELECT @@ROWCOUNT, [name] FROM [t] WHERE [id] = @lo",
            TableState,
            With(("@z", 1), ("@lo", 1)),
            With(("@z", 0), ("@lo", 2)),
            With(("@z", -9), ("@lo", 3)),
            With(("@z", 2), ("@lo", 9)));

    private const string Procedure = """
        create procedure p @id int, @v int as
        set nocount off;
        select name, v from t where id = @id;
        update t set v = @v where id = @id;
        select @@rowcount, case when @@procid = object_id('p') then 'p' end;
        insert log (id, v) values (@id, @v);
        delete log where id = @id and v < 0;
        select v / (@v - 5) from t where id = @id;
        select name from t where id = @id + 1
        """;

    [TestMethod]
    public void Procedure_ResultsErrorsAndCounts()
        => AssertReplayMatchesFreshParse(Tables + "\ngo\n" + Procedure, "exec p @id, @v", TableState,
            With(("@id", 1), ("@v", 2)),
            With(("@id", 1), ("@v", -1)),
            With(("@id", 2), ("@v", 5)),
            With(("@id", 9), ("@v", 1)),
            After("set nocount on", ("@id", 3), ("@v", 4)),
            After("set nocount off; set xact_abort on", ("@id", 2), ("@v", 5)),
            After("set xact_abort off", ("@id", 3), ("@v", 6)));

    [TestMethod]
    public void Procedure_AsAnRpc()
        => AssertReplayMatchesFreshParse(Tables + "\ngo\n" + Procedure, "p", TableState, CommandType.StoredProcedure,
            With(("@id", 1), ("@v", 2)),
            With(("@id", 1), ("@v", -1)),
            With(("@id", 2), ("@v", 5)));

    [TestMethod]
    public void Procedure_TryCatchReportsTheBodysErrors()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create procedure q @v int as
            begin try
                update t set v = @v where id = 1;
                select 10 / @v from t where id = 1;
                insert t (id, name, v) values (@v, N'n', 1);
            end try
            begin catch
                select error_number(), error_line(), error_procedure(), error_severity(), error_state(), @@rowcount;
            end catch
            select v from t where id = 1
            """,
            "exec q @v", TableState,
            With(("@v", 1)),
            With(("@v", 0)),
            With(("@v", -1)),
            With(("@v", 4)),
            With(("@v", 2)));

    [TestMethod]
    public void Procedure_LoopsAndBranches()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create procedure w @n int as
            set nocount on;
            declare @i int = 0, @s int = 0;
            while @i < @n
            begin
                select @s = @s + v from t where id = @i % 3 + 1;
                if @i % 2 = 0
                    select v from t where id = 1;
                else
                begin
                    update t set v = v + 1 where id = 2;
                    select name from t where id = 2;
                end
                set @i += 1;
            end
            select @s, @@rowcount
            """,
            "exec w @n", TableState,
            With(("@n", 0)),
            With(("@n", 1)),
            With(("@n", 4)),
            With(("@n", 3)));

    [TestMethod]
    public void Procedure_ReadsEachCallersTempTable()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create procedure tp as
            select * from #c;
            update #c set x = x + 1;
            select * from t where id = 1
            """,
            "exec tp", TableState,
            After("create table #c (x int); insert #c values (1)"),
            After("drop table #c; create table #c (y nvarchar(5), x int); insert #c values (N'y', 2), (N'z', 3)"),
            After("drop table #c; create table #c (x bigint, z int); insert #c values (5, 6)"));

    [TestMethod]
    public void Procedure_SeesSchemaChangesAndItsOwnAlteration()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create procedure s @id int as
            select * from t where id = @id;
            update t set v = v + 1 output inserted.* where id = @id
            """,
            "exec s @id", TableState,
            With(("@id", 1)),
            After("alter table t add w int null", ("@id", 1)),
            After("update t set w = 7", ("@id", 2)),
            After("alter procedure s @id int as select name from t where id = @id", ("@id", 3)),
            After("alter table t drop column w", ("@id", 3)));

    [TestMethod]
    public void Procedure_UnderTheCallersSettings()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create procedure d as
            select datepart(month, cast('01/02/2020' as datetime)), v from t where id = 1;
            select name + null, v from t where id = 2;
            select count(*) from t where name = null
            """,
            "exec d", TableState,
            With(),
            After("set dateformat dmy"),
            After("set concat_null_yields_null off"),
            After("set dateformat mdy; set concat_null_yields_null on"),
            After("set ansi_nulls off"),
            After("set ansi_nulls on; set transaction isolation level serializable"),
            After("set transaction isolation level read committed"));

    private const string Principals = """
        create table m (id int primary key, s varchar(20) masked with (function = 'email()'));
        insert m values (1, 'ann@example.com'), (2, 'bob@example.com');
        create user seer without login;
        create user blind without login;
        create user owner2 without login;
        go
        create schema s2 authorization owner2;
        go
        create table s2.secret (id int primary key, v int);
        insert s2.secret values (1, 10), (2, 20);
        go
        create procedure readm @id int as
        select s from m where id = @id;
        update m set s = s where id = @id;
        select v from s2.secret where id = @id;
        select user_name()
        go
        create procedure readas @id int with execute as owner as
        select s, user_name() from m where id = @id
        go
        grant execute on readm to seer, blind;
        grant execute on readas to seer, blind;
        grant unmask to seer;
        grant select on s2.secret to seer;
        """;

    private static Run As(string user, params (string Name, object Value)[] parameters) =>
        new($"revert; execute as user = '{user}'", parameters);

    [TestMethod]
    public void Procedure_PrincipalsInAlternation_ChainsMasksAndBrokenChains()
        => AssertReplayMatchesFreshParse(Principals, "exec readm @id", "revert; select id, s from m order by id",
            As("blind", ("@id", 1)),
            As("seer", ("@id", 2)),
            As("blind", ("@id", 2)),
            new Run("revert", ("@id", 1)),
            As("seer", ("@id", 1)));

    [TestMethod]
    public void ExecuteAsOwner_PrincipalsInAlternation()
        => AssertReplayMatchesFreshParse(Principals, "exec readas @id", "revert",
            As("blind", ("@id", 1)),
            As("seer", ("@id", 2)),
            As("blind", ("@id", 2)));

    [TestMethod]
    public void RowLevelSecurity_ReadsTheSessionContextOfEachCall()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create function dbo.allow(@id int) returns table with schemabinding as
                return select 1 as ok where @id <= cast(session_context(N'max') as int)
            go
            create security policy f add filter predicate dbo.allow(id) on dbo.t with (state = on)
            go
            create procedure r as
            select id, name from t;
            update t set v = v + 1;
            select @@rowcount
            """,
            "exec r", "select count(*) from t",
            After("exec sp_set_session_context N'max', 1"),
            After("exec sp_set_session_context N'max', 3"),
            After("exec sp_set_session_context N'max', 2"));

    [TestMethod]
    public void Trigger_BodyStatementsAroundItsPseudoTables()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            create table counter (id int primary key, n int not null);
            insert counter values (1, 0);
            go
            create trigger tr on log after insert as
            begin
                set nocount on;
                update counter set n = n + 1 where id = 1;
                insert t (id, name, v) select id + 100, N'x', v from inserted;
                if exists (select * from inserted where v > 50)
                    rollback;
                select n from counter where id = 1;
            end
            """,
            "insert log (id, v) values (@id, @v)", TableState + "; select n, @@trancount from counter",
            With(("@id", 1), ("@v", 1)),
            With(("@id", 2), ("@v", -1)),
            With(("@id", 3), ("@v", 60)),
            With(("@id", 4), ("@v", 2)));

    private const string AuditTables = """
        create table a (id int primary key, v int not null, note nvarchar(max) null, twice as v * 2);
        insert a (id, v, note) values (1, 1, N'one'), (2, 2, replicate(cast(N'b' as nvarchar(max)), 5000)), (3, 3, null);
        create table audit (n int identity primary key, op char(1), id int, old_v int, new_v int, note_len int, mask varbinary(8), v_updated bit check (v_updated is not null));
        """;

    private const string AuditTrigger = """
        create trigger tr on a after insert, update, delete as
        begin
            set nocount on;
            insert audit (op, id, old_v, new_v, note_len, mask, v_updated)
            select case when d.id is null then 'I' when i.id is null then 'D' else 'U' end,
                coalesce(i.id, d.id), d.v, i.v, len(coalesce(i.note, d.note)), columns_updated(),
                case when update(v) then 1 when i.v < 0 then null else 0 end
            from inserted i full join deleted d on d.id = i.id;
            select count(*), sum(twice), max(len(note)) from inserted;
            update a set note = d.note + N'!' from a join deleted d on d.id = a.id where a.note is null and d.v > 100;
        end
        """;

    private const string AuditState = "select id, v, len(note), twice from a order by id; select n, op, id, old_v, new_v, note_len, mask, v_updated from audit order by n";

    [TestMethod]
    public void Trigger_AuditOverInsertedAndDeleted_EveryVerbAndRowCount()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + AuditTrigger,
            """
            insert a (id, v, note) values (@id, @v, replicate(cast(N'x' as nvarchar(max)), @len));
            update a set v = v + 1 where id <= @id;
            update a set note = N'n' + cast(@id as nvarchar(9)) where id = @id;
            delete a where id = @id;
            insert a (id, v, note) select id + 10, v, note from a where id < @id;
            delete a where id > 10
            """,
            AuditState,
            With(("@id", 4), ("@v", 4), ("@len", 3)),
            With(("@id", 5), ("@v", 7), ("@len", 6000)),
            With(("@id", 6), ("@v", -1), ("@len", 1)),
            With(("@id", 7), ("@v", 150), ("@len", 9000)));

    [TestMethod]
    public void Trigger_AuditFiredByMerge_AllThreeActions()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + AuditTrigger,
            """
            merge a as tgt
            using (values (1, @v), (@id, 9), (@id + 1, @v)) as s (id, v)
            on tgt.id = s.id
            when matched and s.v > 5 then delete
            when matched then update set v = s.v
            when not matched then insert (id, v, note) values (s.id, s.v, replicate(cast(N'm' as nvarchar(max)), @len));
            """,
            AuditState,
            With(("@id", 4), ("@v", 2), ("@len", 2)),
            With(("@id", 5), ("@v", 8), ("@len", 7000)),
            With(("@id", 2), ("@v", -3), ("@len", 1)),
            With(("@id", 9), ("@v", 3), ("@len", 4)));

    [TestMethod]
    public void Trigger_SchemaOfItsTableChangesBetweenFirings()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + """
            create trigger tr on a after insert, update as
            begin
                select * from inserted;
                select d.*, i.v from deleted d join inserted i on i.id = d.id;
            end
            """,
            "update a set v = v + @d where id = 1; insert a (id, v) values (@d + 20, @d); delete a where id > 20",
            "select * from a order by id; select * from audit order by n",
            With(("@d", 1)),
            After("alter table a add extra int null default 7", ("@d", 2)),
            After("alter table a drop column twice", ("@d", 3)),
            After("alter table a alter column v bigint not null", ("@d", 4)));

    [TestMethod]
    public void Trigger_InsteadOf_OnATableAndThroughAView()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + """
            create view av as select id, v, note from a where id < 100
            go
            create trigger ia on a instead of insert as
            begin
                set nocount on;
                insert audit (op, id, new_v, note_len, v_updated) select 'X', id, v, len(note), case when update(v) then 1 else 0 end from inserted;
                insert a (id, v, note) select id, abs(v), note from inserted where v <> 0;
            end
            go
            create trigger iv on av instead of update, delete as
            begin
                set nocount on;
                insert audit (op, id, old_v, new_v, v_updated) select case when i.id is null then 'R' else 'W' end, d.id, d.v, i.v, case when update(note) then 1 else 0 end
                from deleted d left join inserted i on i.id = d.id;
                update a set v = i.v * 10 from a join inserted i on i.id = a.id;
                delete a from a join deleted d on d.id = a.id where not exists (select * from inserted);
            end
            """,
            """
            insert a (id, v, note) values (@id, @v, N'n'), (@id + 50, 0, null);
            update av set v = @v where id = @id;
            update av set note = N'q' where id = 1;
            delete av where id = @id - 1;
            delete a where id > 3
            """,
            AuditState,
            With(("@id", 4), ("@v", 2)),
            With(("@id", 5), ("@v", -2)),
            With(("@id", 6), ("@v", 0)),
            With(("@id", 7), ("@v", -9)));

    [TestMethod]
    public void Trigger_RecursiveAndNestedFirings_ReadTheirOwnRows()
        => AssertReplayMatchesFreshParse(AuditTables + "\nalter database current set recursive_triggers on;\ngo\n" + """
            create table b (id int primary key, v int not null);
            go
            create trigger ta on a after insert as
            begin
                set nocount on;
                insert audit (op, id, new_v, v_updated) select 'A', id, v, trigger_nestlevel() from inserted;
                if trigger_nestlevel() < 3
                    insert a (id, v) select id + 100, v + 1 from inserted where id < 100;
                insert b (id, v) select id, v from inserted where id < 100;
                select trigger_nestlevel(), count(*) from inserted;
            end
            go
            create trigger tb on b after insert as
            begin
                set nocount on;
                insert audit (op, id, new_v, v_updated) select 'B', id, v, 1 from inserted;
                update a set v = a.v + i.v from a join inserted i on i.id = a.id;
            end
            """,
            "insert a (id, v) values (@id, @v), (@id + 1, @v); select id, v from b order by id; delete a where id >= 10; delete b",
            AuditState,
            With(("@id", 10), ("@v", 1)),
            With(("@id", 20), ("@v", 2)),
            With(("@id", 30), ("@v", 3)));

    [TestMethod]
    public void Trigger_ErrorsAndRollbackOverItsPseudoTables()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + """
            create trigger tr on a after update as
            begin
                set nocount on;
                insert audit (op, id, old_v, new_v, v_updated) select 'U', i.id, d.v, i.v / (i.v - 3), case when i.v = 4 then null else 1 end
                from inserted i join deleted d on d.id = i.id;
                if exists (select * from inserted where v > 50)
                begin
                    rollback;
                    raiserror (N'too big', 16, 1);
                end
            end
            """,
            """
            begin try
                update a set v = @v where id = 1;
                select @@trancount, @@rowcount;
            end try
            begin catch
                select error_number(), error_line(), error_procedure(), error_message(), @@trancount;
            end catch;
            update a set v = @v where id = 2;
            """,
            AuditState + "; select @@trancount",
            With(("@v", 1)),
            With(("@v", 3)),
            With(("@v", 4)),
            With(("@v", 60)),
            After("begin tran", ("@v", 5)),
            After("set xact_abort on", ("@v", 3)),
            After("set xact_abort off; while @@trancount > 0 rollback", ("@v", 7)));

    [TestMethod]
    public void Trigger_MergeAndCursorOverInserted()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + """
            create table totals (id int primary key, total int not null);
            go
            create trigger tr on a after insert, update as
            begin
                set nocount on;
                merge totals as t
                using inserted as i on t.id = i.id
                when matched then update set total = t.total + i.v
                when not matched then insert (id, total) values (i.id, i.v);
                declare @id int, @v int;
                declare c cursor local fast_forward for select id, v from inserted order by id;
                open c;
                fetch next from c into @id, @v;
                while @@fetch_status = 0
                begin
                    insert audit (op, id, new_v, v_updated) values ('C', @id, @v, 0);
                    fetch next from c into @id, @v;
                end
                close c;
                deallocate c;
            end
            """,
            "insert a (id, v) values (@id, @v), (@id + 1, @v + 1); update a set v = v + @v where id between 1 and @id; delete a where id >= 10",
            AuditState + "; select id, total from totals order by id",
            With(("@id", 10), ("@v", 1)),
            With(("@id", 20), ("@v", 2)),
            With(("@id", 30), ("@v", 3)));

    [TestMethod]
    public void Trigger_GlobalCursorItLeavesOpen_KeepsItsFiringsRows()
        => AssertReplayMatchesFreshParse(AuditTables + "\ngo\n" + """
            create trigger tr on a after insert as
            begin
                set nocount on;
                if cursor_status('global', 'gc') >= -1
                    deallocate gc;
                declare gc cursor global dynamic for select id, v from inserted;
                open gc;
                select id, v from inserted;
            end
            """,
            "insert a (id, v) values (@id, @v), (@id + 1, @v); delete a where id >= 10",
            AuditState,
            With(("@id", 10), ("@v", 1)),
            After("fetch next from gc; fetch next from gc", ("@id", 20), ("@v", 2)),
            After("fetch first from gc; fetch next from gc", ("@id", 30), ("@v", 3)));

    [TestMethod]
    public void ConcurrentFirings_OfOneTrigger_ReadTheirOwnRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table c (id int primary key, v int not null); create table c_log (id int, v int, total int)");
        _ = simulation.ExecuteNonQuery("""
            create trigger c_tr on c after insert, update as
            set nocount on;
            insert c_log (id, v, total) select i.id, i.v, (select sum(v) from inserted) from inserted i left join deleted d on d.id = i.id
            """);
        const int workers = 8, iterations = 200;
        _ = Parallel.For(0, workers, worker =>
        {
            using var connection = simulation.CreateOpenConnection();
            for (var i = 0; i < iterations; i++)
            {
                using var command = connection.CreateCommand("insert c (id, v) values (@id, @v), (@id + 1, @v)", ("@id", ((worker * iterations) + i) * 2), ("@v", worker + 1));
                _ = command.ExecuteNonQuery();
            }
        });
        AreEqual(0, simulation.ExecuteScalar("select count(*) from c_log where total <> 2 * v"));
        AreEqual(workers * iterations * 2, simulation.ExecuteScalar("select count(*) from c_log l join c on c.id = l.id and c.v = l.v"));
    }

    [TestMethod]
    public void ScalarFunction_SelectInItsBody()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create function dbo.f(@id int) returns int with inline = off as
            begin
                declare @r int;
                select @r = v * 10 from t where id = @id;
                return @r / (@id - 2);
            end
            """,
            "select id, dbo.f(id) from t where id <> @skip order by id", TableState,
            With(("@skip", 2)),
            With(("@skip", 9)),
            With(("@skip", 3)));

    [TestMethod]
    public void MultiStatementFunction_Body()
        => AssertReplayMatchesFreshParse(Tables + "\n" + """
            go
            create function dbo.tf(@min int) returns @r table (id int, v int) as
            begin
                declare @n int;
                select @n = count(*) from t where v >= @min;
                insert @r select id, v / (@n - 1) from t where v >= @min;
                return;
            end
            """,
            "select id, v from dbo.tf(@min) order by id", TableState,
            With(("@min", 1)),
            With(("@min", 3)),
            With(("@min", 2)));

    [TestMethod]
    public void DynamicSql_ExecutesqlAndExec()
        => AssertReplayMatchesFreshParse(Tables,
            """
            exec sp_executesql N'select name from t where id = @id; update t set v = @v where id = @id; select @@rowcount', N'@id int, @v int', @id = @a, @v = @b;
            exec (N'select count(*) from t where v > 2; delete log where id < 0')
            """,
            TableState,
            With(("@a", 1), ("@b", 3)),
            With(("@a", 2), ("@b", -3)),
            With(("@a", 3), ("@b", 7)));

    [TestMethod]
    public void QueryStore_RecordsReplayedBodyStatementsAsParsedOnes()
        => AssertReplayMatchesFreshParse(Tables + "\nalter database current set query_store (query_capture_mode = all);\ngo\n" + Procedure,
            "exec p @id, @v",
            """
            select qt.query_sql_text, sum(rs.count_executions), min(rs.execution_type), object_name(q.object_id)
            from sys.query_store_query_text qt
            join sys.query_store_query q on q.query_text_id = qt.query_text_id
            join sys.query_store_plan p on p.query_id = q.query_id
            join sys.query_store_runtime_stats rs on rs.plan_id = p.plan_id
            where q.object_id <> 0
            group by qt.query_sql_text, q.object_id
            order by 1
            """,
            With(("@id", 1), ("@v", 2)),
            With(("@id", 2), ("@v", 5)),
            With(("@id", 3), ("@v", 1)));

    [TestMethod]
    public void ConcurrentCalls_OfOneProcedure_ReadTheirOwnRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table c (id int primary key, v int not null)");
        _ = simulation.ExecuteNonQuery("""
            create procedure bump @id int, @d int as
            set nocount on;
            update c set v = v + @d where id = @id;
            select v from c where id = @id
            """);
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
                using var command = connection.CreateCommand("exec bump @id, @d", ("@id", worker), ("@d", worker + 1));
                AreEqual((i + 1) * (worker + 1), command.ExecuteScalar());
            }
        });
        AreEqual(iterations * workers * (workers + 1) / 2, simulation.ExecuteScalar("select sum(v) from c"));
    }
}
