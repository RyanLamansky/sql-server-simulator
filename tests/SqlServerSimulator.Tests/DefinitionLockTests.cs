using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A transaction's create, alter, drop or rename of an object holds the
/// object and its name to the transaction's end, so another session's
/// resolution, call, catalog read or change of the same name waits for it to
/// commit or roll back (probed 2026-10-10 against SQL Server 2025). The
/// simulator once released them with the statement, so two transactions
/// replacing one function each went ahead, and a rollback restored the
/// original over the other's committed body.
/// </summary>
[TestClass]
public sealed class DefinitionLockTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string DropIfExists = "if exists (select * from sys.objects where object_id = object_id(N'dbo.zzf')) drop function dbo.zzf";

    private static Simulation Functions()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.zzf() returns int as begin return 0 end",
            "create function dbo.other() returns int as begin return 9 end",
            "create procedure dbo.p as select 'p0'",
            "create sequence dbo.q as int start with 1",
            "create table dbo.t (a int)",
            "create table dbo.log (s varchar(20))");
        return sim;
    }

    private static string WaitType(DbConnection observer, DbConnection waiter) =>
        (string)observer.CreateCommand($"select wait_type from sys.dm_os_waiting_tasks where session_id = {waiter.CreateCommand("select @@spid").ExecuteScalar()}").ExecuteScalar()!;

    /// <summary>
    /// The reported race: two transactions each dropping the function if it
    /// exists and creating their own. The second waits on the first's new
    /// function, then drops and replaces it, where it once went ahead and
    /// the first's rollback restored the original over its committed body.
    /// </summary>
    [TestMethod]
    public async Task DropAndCreate_SecondTransactionWaitsThenReplaces()
    {
        var sim = Functions();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();

        _ = first.CreateCommand("begin tran").ExecuteNonQuery();
        _ = first.CreateCommand(DropIfExists).ExecuteNonQuery();
        _ = first.CreateCommand("create function dbo.zzf() returns int as begin return 1 end").ExecuteNonQuery();
        _ = second.CreateCommand("begin tran").ExecuteNonQuery();
        var dropped = await sim.StartBlocked(second, DropIfExists, TestContext.CancellationToken);
        AreEqual("LCK_M_SCH_S", WaitType(observer, second));

        _ = first.CreateCommand("commit").ExecuteNonQuery();
        _ = await dropped;
        _ = second.CreateCommand("create function dbo.zzf() returns int as begin return 2 end").ExecuteNonQuery();
        _ = second.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(2, observer.CreateCommand("select dbo.zzf()").ExecuteScalar());
    }

    /// <summary>
    /// Two transactions whose <c>IF EXISTS … DROP FUNCTION</c> both find the
    /// function before either drops it: one drops it, and the other waits on
    /// that drop and then finds nothing to drop (Msg 3701), as on real, where
    /// each once held its condition's Sch-S into the drop and the two
    /// deadlocked. The condition's second test waits on an uncommitted insert
    /// so that both have read the function before either goes on.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentDropIfExists_OneDropsTheOtherFindsNothing()
    {
        var sim = Functions();
        using var gate = sim.CreateOpenConnection();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        const string race = "if exists (select * from sys.objects where object_id = object_id(N'dbo.zzf') and type = N'FN')"
            + " and exists (select * from dbo.log) drop function dbo.zzf";

        _ = gate.CreateCommand("begin tran; insert dbo.log values ('gate')").ExecuteNonQuery();
        _ = first.CreateCommand("begin tran").ExecuteNonQuery();
        _ = second.CreateCommand("begin tran").ExecuteNonQuery();
        var firstDrop = await sim.StartBlocked(first, race, TestContext.CancellationToken);
        var secondDrop = await sim.StartBlocked(second, race, TestContext.CancellationToken);

        _ = gate.CreateCommand("commit").ExecuteNonQuery();
        var winner = await Task.WhenAny(firstDrop, secondDrop).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        _ = await winner;
        var (winning, losing, loser) = winner == firstDrop ? (first, second, secondDrop) : (second, first, firstDrop);
        _ = winning.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(3701, (await ThrowsAsync<SimulatedSqlException>(() => loser)).Number);
        _ = losing.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(DBNull.Value, gate.CreateCommand("select object_id(N'dbo.zzf')").ExecuteScalar());
    }

    /// <summary>
    /// An <c>IF</c> or <c>WHILE</c> condition lets go of its statement locks
    /// as it ends, so its body holds none on what the condition named and
    /// another transaction drops the function beside it, as on real (probed
    /// 2026-10-10 against SQL Server 2025). The body waits on an uncommitted
    /// insert, which keeps it running while the locks are read.
    /// </summary>
    [TestMethod]
    [DataRow("if object_id(N'dbo.zzf') is not null select count(*) from dbo.log")]
    [DataRow("if exists (select * from sys.objects where object_id = object_id(N'dbo.zzf')) select count(*) from dbo.log")]
    [DataRow("if dbo.zzf() = 0 select count(*) from dbo.log")]
    [DataRow("if not exists (select * from dbo.t) select count(*) from dbo.log")]
    [DataRow("declare @i int = 0; while dbo.zzf() = 0 and @i = 0 begin select count(*) from dbo.log; set @i = 1 end")]
    public async Task Condition_LocksGoBeforeTheBody(string statement)
    {
        var sim = Functions();
        using var gate = sim.CreateOpenConnection();
        using var session = sim.CreateOpenConnection();
        using var dropper = sim.CreateOpenConnection();

        _ = gate.CreateCommand("begin tran; insert dbo.log values ('gate')").ExecuteNonQuery();
        _ = session.CreateCommand("begin tran").ExecuteNonQuery();
        var body = await sim.StartBlocked(session, statement, TestContext.CancellationToken);
        AreEqual(0, dropper.CreateCommand(
            "select count(*) from sys.dm_tran_locks where resource_type = 'OBJECT' and request_session_id <> @@spid"
            + " and resource_associated_entity_id in (object_id(N'dbo.zzf'), object_id(N'dbo.t'))").ExecuteScalar());
        _ = dropper.CreateCommand("set lock_timeout 1000; begin tran; drop function dbo.zzf; rollback").ExecuteNonQuery();

        _ = gate.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, (await body).Single());
        _ = session.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// <c>OBJECT_ID</c> keeps no lock on the object it finds, where a call of
    /// the function holds its Sch-S for the statement (probed 2026-10-10
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("object_id(N'dbo.zzf')", 0)]
    [DataRow("dbo.zzf()", 1)]
    public void ObjectLocksHeldInTheStatement(string expression, int held)
    {
        using var connection = Functions().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            $"select {expression}, (select count(*) from sys.dm_tran_locks where request_session_id = @@spid and resource_type = 'OBJECT'"
            + " and resource_associated_entity_id = object_id(N'dbo.zzf'))").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(held, reader.GetInt32(1));
    }

    /// <summary>
    /// When the first rolls back, the waiting <c>OBJECT_ID</c> answers NULL —
    /// the function it waited on is gone, though the original is back — so
    /// the second skips its drop and its create meets the original: Msg 2714,
    /// which rolls its transaction back.
    /// </summary>
    [TestMethod]
    public async Task DropAndCreate_FirstRollsBack_SecondMeetsTheOriginal()
    {
        var sim = Functions();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();

        _ = first.CreateCommand("begin tran").ExecuteNonQuery();
        _ = first.CreateCommand(DropIfExists).ExecuteNonQuery();
        _ = first.CreateCommand("create function dbo.zzf() returns int as begin return 1 end").ExecuteNonQuery();
        _ = second.CreateCommand("begin tran").ExecuteNonQuery();
        var id = await sim.StartBlocked(second, "select object_id(N'dbo.zzf')", TestContext.CancellationToken);

        _ = first.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(DBNull.Value, (await id).Single());
        AreEqual(2714, Throws<SimulatedSqlException>(() => second.CreateCommand("create function dbo.zzf() returns int as begin return 2 end").ExecuteNonQuery()).Number);
        AreEqual(0, second.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(0, second.CreateCommand("select dbo.zzf()").ExecuteScalar());
    }

    /// <summary>
    /// A reference to an object another transaction altered waits for it,
    /// then reads the definition as that transaction left it.
    /// </summary>
    [TestMethod]
    [DataRow("alter function dbo.zzf() returns int as begin return 1 end", "select dbo.zzf()", "LCK_M_SCH_S", 1, 0)]
    [DataRow("alter procedure dbo.p as select 'p1'", "exec dbo.p", "LCK_M_SCH_S", "p1", "p0")]
    [DataRow("alter sequence dbo.q restart with 10", "select next value for dbo.q", "LCK_M_SCH_S", 10, 1)]
    [DataRow("alter function dbo.zzf() returns int as begin return 1 end", "exec('alter function dbo.zzf() returns int as begin return 2 end'); exec('select dbo.zzf()')", "LCK_M_SCH_M", 2, 2)]
    [DataRow("drop function dbo.zzf", "select object_id('dbo.zzf') - object_id('dbo.other') + 1", "LCK_M_S", null, 0)]
    public async Task Reference_WaitsForAnUncommittedChange(string change, string reference, string waitType, object? afterCommit, object? afterRollback)
    {
        foreach (var commit in new[] { true, false })
        {
            var sim = Functions();
            using var changer = sim.CreateOpenConnection();
            using var reader = sim.CreateOpenConnection();
            using var observer = sim.CreateOpenConnection();

            _ = changer.CreateCommand("begin tran").ExecuteNonQuery();
            _ = changer.CreateCommand(change).ExecuteNonQuery();
            var read = await sim.StartBlocked(reader, reference, TestContext.CancellationToken);
            AreEqual(waitType, WaitType(observer, reader));

            _ = changer.CreateCommand(commit ? "commit" : "rollback").ExecuteNonQuery();
            var expected = commit ? afterCommit : afterRollback;
            AreEqual(expected ?? DBNull.Value, (await read).Last(), commit ? "committed" : "rolled back");
        }
    }

    /// <summary>
    /// A catalog read meets the rows of a dropped function: one seeking its
    /// name waits, as does a scan, while one seeking another name, a
    /// <c>NOLOCK</c> read and a view of something else read on.
    /// </summary>
    [TestMethod]
    public async Task CatalogRead_WaitsOnTheRowsItMeets()
    {
        var sim = Functions();
        using var dropper = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();
        using var scanner = sim.CreateOpenConnection();

        _ = dropper.CreateCommand("begin tran; drop function dbo.zzf").ExecuteNonQuery();
        AreEqual(1, reader.CreateCommand("select count(*) from sys.objects where name = 'other'").ExecuteScalar());
        AreEqual(0, reader.CreateCommand("select count(*) from sys.objects with (nolock) where name = 'zzf'").ExecuteScalar());
        AreEqual(1, reader.CreateCommand("select count(*) from sys.schemas where name = 'dbo'").ExecuteScalar());
        var seek = await sim.StartBlocked(reader, "select count(*) from sys.objects where name = 'zzf'", TestContext.CancellationToken);
        var scan = await sim.StartBlocked(scanner, "select count(*) from sys.objects where type = 'FN'", TestContext.CancellationToken);

        _ = dropper.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(0, (await seek).Single());
        AreEqual(1, (await scan).Single());
    }

    /// <summary>
    /// A create of a name another transaction created waits for it and then
    /// finds the name taken; a table's create meets a name an object held
    /// before another transaction altered it as taken at once.
    /// </summary>
    [TestMethod]
    public async Task Create_WaitsForAnUncommittedCreate()
    {
        var sim = Functions();
        using var creator = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();

        _ = creator.CreateCommand("begin tran").ExecuteNonQuery();
        _ = creator.CreateCommand("create function dbo.nn() returns int as begin return 1 end").ExecuteNonQuery();
        _ = creator.CreateCommand("alter function dbo.zzf() returns int as begin return 1 end").ExecuteNonQuery();
        _ = second.CreateCommand("begin tran").ExecuteNonQuery();
        var tableError = Throws<SimulatedSqlException>(() => second.CreateCommand("create table dbo.zzf (a int)").ExecuteNonQuery());
        AreEqual((2714, 6), (tableError.Number, tableError.State));
        _ = second.CreateCommand("begin tran").ExecuteNonQuery();
        var created = await sim.StartBlocked(second, "create function dbo.nn() returns int as begin return 2 end", TestContext.CancellationToken);
        AreEqual("LCK_M_SCH_M", WaitType(observer, second));

        _ = creator.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(2714, (await ThrowsAsync<SimulatedSqlException>(() => created)).Number);
        AreEqual(0, second.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(1, observer.CreateCommand("select dbo.nn()").ExecuteScalar());
    }

    /// <summary>
    /// A definition statement's lock timeout inside a transaction ends the
    /// batch and rolls the transaction back, where a <c>CATCH</c> leaves it
    /// committable and outside a transaction it ends the statement alone; a
    /// read's ends its statement. Its state names what the wait was on: the
    /// object (56), a dropped object's name (51) or the name a create takes
    /// (47).
    /// </summary>
    [TestMethod]
    [DataRow("alter function dbo.zzf() returns int as begin return 1 end", "drop function dbo.zzf", 56, true)]
    [DataRow("drop function dbo.zzf", "drop function if exists dbo.zzf", 51, true)]
    [DataRow("drop function dbo.zzf", "create table dbo.zzf (a int)", 47, true)]
    [DataRow("alter function dbo.zzf() returns int as begin return 1 end", "select object_id('dbo.zzf')", 56, false)]
    [DataRow("drop function dbo.zzf", "select object_id('dbo.zzf')", 51, false)]
    public void LockTimeout_EndsADefinitionStatementsTransaction(string change, string statement, int state, bool endsTransaction)
    {
        var sim = Functions();
        using var changer = sim.CreateOpenConnection();
        using var session = sim.CreateOpenConnection();

        _ = changer.CreateCommand("begin tran").ExecuteNonQuery();
        _ = changer.CreateCommand(change).ExecuteNonQuery();
        _ = session.CreateCommand("set lock_timeout 50").ExecuteNonQuery();

        var error = Throws<SimulatedSqlException>(() => session.CreateCommand(statement).ExecuteNonQuery());
        AreEqual((1222, state), (error.Number, error.State), "outside a transaction");
        _ = session.CreateCommand($"begin tran; insert dbo.log values ('before')").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => session.CreateCommand($"{statement}; insert dbo.log values ('after')").ExecuteNonQuery());
        AreEqual(endsTransaction ? "0 0" : "1 2", session.CreateCommand("select concat(@@trancount, ' ', count(*)) from dbo.log").ExecuteScalar());
        _ = session.CreateCommand("if @@trancount > 0 rollback").ExecuteNonQuery();

        _ = session.CreateCommand("begin tran").ExecuteNonQuery();
        AreEqual("1222 1 1", Extensions.FirstColumn(session, $"begin try {statement} end try begin catch select concat(error_number(), ' ', xact_state(), ' ', @@trancount) end catch").Last());
        _ = session.CreateCommand("rollback").ExecuteNonQuery();
        _ = changer.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// Two transactions each altering the function the other altered
    /// deadlock, the one closing the cycle the victim; the other's change goes
    /// through.
    /// </summary>
    [TestMethod]
    public async Task OpposingAlters_Deadlock()
    {
        var sim = Functions();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();

        _ = first.CreateCommand("begin tran").ExecuteNonQuery();
        _ = first.CreateCommand("alter function dbo.zzf() returns int as begin return 1 end").ExecuteNonQuery();
        _ = second.CreateCommand("begin tran").ExecuteNonQuery();
        _ = second.CreateCommand("alter function dbo.other() returns int as begin return 1 end").ExecuteNonQuery();
        var firstAlter = await sim.StartBlocked(first, "alter function dbo.other() returns int as begin return 3 end", TestContext.CancellationToken);
        var victim = Throws<SimulatedSqlException>(() => second.CreateCommand("alter function dbo.zzf() returns int as begin return 4 end").ExecuteNonQuery());
        AreEqual((1205, 56), (victim.Number, victim.State));

        _ = await firstAlter;
        _ = first.CreateCommand("commit").ExecuteNonQuery();
        AreEqual("1 3", second.CreateCommand("select concat(dbo.zzf(), ' ', dbo.other())").ExecuteScalar());
    }

    /// <summary>
    /// A table created or dropped in a transaction holds its name as a
    /// module's does: a read waits, then finds the table as the transaction
    /// left it.
    /// </summary>
    [TestMethod]
    [DataRow("create table dbo.nt (a int)", "select count(*) from dbo.nt", false)]
    [DataRow("drop table dbo.t", "select count(*) from dbo.t", true)]
    public async Task Table_CreateOrDropHoldsItsName(string change, string read, bool commit)
    {
        var sim = Functions();
        using var changer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();

        _ = changer.CreateCommand($"begin tran; {change}").ExecuteNonQuery();
        var waiting = await sim.StartBlocked(reader, read, TestContext.CancellationToken);
        _ = changer.CreateCommand(commit ? "commit" : "rollback").ExecuteNonQuery();
        AreEqual(208, (await ThrowsAsync<SimulatedSqlException>(() => waiting)).Number);
    }
}
