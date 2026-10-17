using System.Data.Common;
using SqlServerSimulator.Schemas;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which trigger bodies may send rows (<see cref="TriggerBodyScan"/>), and
/// which DML statements of a streaming command therefore run on a thread of
/// their own, observed through <see cref="Simulation.StatementThreadRuns"/>.
/// That what goes out is the same either way is <c>TriggerStreamingTests</c>'
/// (public API) to show.
/// </summary>
[TestClass]
public sealed class TriggerStatementThreadTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("set nocount on; insert l select id from inserted")]
    [DataRow("set nocount on; insert into l (id) select i.id from inserted i join deleted d on d.id = i.id union all select 1")]
    [DataRow("declare @n int; select @n = count(*) from inserted; if @n > 1 rollback")]
    [DataRow("declare @n int; select top (1) @n = id from inserted order by id")]
    [DataRow("declare @n int = 0; select @n += id from inserted")]
    [DataRow("if exists (select 1 from inserted where id < 0) begin raiserror('no', 16, 1); rollback; end")]
    [DataRow("set @x = (select count(*) from inserted); print @x")]
    [DataRow("declare c cursor local for select id from inserted; open c; fetch next from c into @x; close c; deallocate c")]
    [DataRow("with c as (select id from inserted) insert l select id from c")]
    [DataRow("update l set id = i.id from l join (select id from inserted) i on i.id = l.id")]
    [DataRow("merge l using (select id from inserted) s on s.id = l.id when not matched then insert (id) values (s.id);")]
    [DataRow("insert l select x.id from inserted i cross apply (select i.id) x")]
    [DataRow("insert l values (1) insert l select 2")]
    public void BodyWithoutRows_SendsNone(string body)
        => AreEqual(TriggerBodyReach.None, TriggerBodyScan.Of(body) & TriggerBodyReach.SendsRows);

    [TestMethod]
    [DataRow("select * from inserted")]
    [DataRow("set nocount on; insert l select id from inserted; select 'done'")]
    [DataRow("insert l values (1) select 2")]
    [DataRow("insert l select 1 select 2")]
    [DataRow("if 1 = 1 select 1 else print 'x'")]
    [DataRow("begin (select 1) end")]
    [DataRow("with c as (select id from inserted) select id from c")]
    [DataRow("select @x, id from inserted")]
    [DataRow("select id into #t from inserted")]
    [DataRow("exec p")]
    [DataRow("execute ('select 1')")]
    [DataRow("sp_who")]
    [DataRow("declare c cursor for select id from inserted; open c; fetch next from c")]
    [DataRow("insert l output inserted.id select id from inserted")]
    [DataRow("dbcc useroptions")]
    [DataRow("readtext t.b @p 0 1")]
    [DataRow("waitfor (receive * from q)")]
    [DataRow("select 'unterminated")]
    public void BodyWithRows_MaySend(string body)
        => AreEqual(TriggerBodyReach.SendsRows, TriggerBodyScan.Of(body) & TriggerBodyReach.SendsRows);

    [TestMethod]
    [DataRow("print 'x'", false)]
    [DataRow("insert l values (1)", true)]
    [DataRow("delete l", true)]
    [DataRow("create table x (id int)", true)]
    [DataRow("set nocount on; disable trigger tr on l", true)]
    public void BodyThatWrites_MayFireAnother(string body, bool writes)
        => AreEqual(writes ? TriggerBodyReach.Writes : TriggerBodyReach.None, TriggerBodyScan.Of(body));

    private static Simulation With(params string[] batches)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, "create table t (id int primary key, v int); create table l (id int); create table p (id int primary key); create table c (id int primary key, pid int references p (id) on delete cascade)");
        foreach (var batch in batches)
            Execute(connection, batch);
        return simulation;
    }

    private static void Execute(DbConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>How many of <paramref name="text"/>'s statements, read through a reader, ran on a thread of their own.</summary>
    private static long ThreadRuns(Simulation simulation, string text)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        var before = Interlocked.Read(ref simulation.StatementThreadRuns);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = text;
            using var reader = command.ExecuteReader();
            while (reader.NextResult())
            {
            }
        }
        return Interlocked.Read(ref simulation.StatementThreadRuns) - before;
    }

    [TestMethod]
    public void RowlessTrigger_RunsOnTheCallersThread()
        => AreEqual(0, ThreadRuns(With("create trigger tr on t after insert as set nocount on; insert l select id from inserted"), "insert t values (1, 1)"));

    [TestMethod]
    public void RowSendingTrigger_TakesTheThread()
        => AreEqual(1, ThreadRuns(With("create trigger tr on t after insert as select id from inserted"), "insert t values (1, 1)"));

    /// <summary>
    /// With a row-sending trigger elsewhere, a statement keeps the thread
    /// wherever its trigger may write — that write may fire the other — and
    /// leaves it where its trigger can neither send nor write.
    /// </summary>
    [TestMethod]
    [DataRow("create trigger tr on t after insert as insert l values (1)", 1)]
    [DataRow("create trigger tr on t after insert as print 'x'", 0)]
    public void RowSendingTriggerElsewhere_KeepsTheThreadOnlyWhereReachable(string trigger, int runs)
        => AreEqual(runs, ThreadRuns(With(trigger, "create trigger tl on l after insert as select 1"), "insert t values (1, 1)"));

    [TestMethod]
    public void CascadedChildsRowSendingTrigger_TakesTheThread()
        => AreEqual(1, ThreadRuns(With("create trigger tc on c after delete as select id from deleted", "insert p values (1); insert c values (1, 1)"), "delete p where id = 1"));

    [TestMethod]
    public void AlterFlipsTheChoiceBetweenRuns()
    {
        var simulation = With("create trigger tr on t after update as set nocount on; insert l select id from inserted", "insert t values (1, 1)");
        const string update = "update t set v = v + 1 where id = 1";
        AreEqual(0, ThreadRuns(simulation, update));
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, "alter trigger tr on t after update as select id from inserted");
        AreEqual(1, ThreadRuns(simulation, update));
        Execute(connection, "alter trigger tr on t after update as set nocount on");
        AreEqual(0, ThreadRuns(simulation, update));
        Execute(connection, "begin tran");
        Execute(connection, "alter trigger tr on t after update as select 1");
        Execute(connection, "rollback");
        AreEqual(0, ThreadRuns(simulation, update));
        Execute(connection, "drop trigger tr; create table x (id int)");
        Execute(connection, "create trigger tx on x after insert as select 1");
        AreEqual(0, ThreadRuns(simulation, update));
    }
}
