using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ClrContextConnectionTests
{
    private static readonly string[] Setup =
    [
        "create table t (a int, b nvarchar(10))",
        "create table log (a int)",
        "insert t values (1, N'one'), (2, NULL)",
        "create procedure dbo.sc @sql nvarchar(max) as external name simclr.Ctx.Scalar",
        "create procedure dbo.nq @sql nvarchar(max) as external name simclr.Ctx.NonQuery",
        "create procedure dbo.rd @sql nvarchar(max) as external name simclr.Ctx.Reader",
        "create procedure dbo.es @sql nvarchar(max) as external name simclr.Ctx.ExecSend",
        "create procedure dbo.ca @sql nvarchar(max) as external name simclr.Ctx.[Catch]",
    ];

    private static Simulation Sim(params string[] batches) => ClrFrameworkFixture.Simulation([.. Setup, .. batches]);

    /// <summary>Runs <paramref name="commandText"/>, answering the messages it sent, in order.</summary>
    private static List<string> Messages(Simulation sim, string commandText)
    {
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add(error.Message);
        };
        using var command = connection.CreateCommand(commandText);
        _ = command.ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    [Description("ExecuteScalar and ExecuteNonQuery run in the caller's database: a NULL reads DBNull, no row reads null, and a non-query counts only what its statements changed.")]
    public void ExecuteScalarAndNonQuery_RunInTheCallersSession()
    {
        var sim = Sim();
        CollectionAssert.AreEqual(
            new[] { "Int32:2", "DBNull", "(null)", "Int32:9", "affected=2", "affected=-1", "affected=3" },
            Messages(sim, """
                exec sc N'select count(*) from t'
                exec sc N'select b from t where a = 2'
                exec sc N'select b from t where a = 3'
                exec sc N'insert log values (5); select 9'
                exec nq N'update t set a = a'
                exec nq N'select * from t'
                exec nq N'insert log values (6); update log set a = 1; delete log where 1 = 0'
                """));
        AreEqual(2, sim.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    [Description("A reader walks each result set with its type names and field types, and RecordsAffected totals the batch's writes.")]
    public void ExecuteReader_WalksResultSets()
        => CollectionAssert.AreEqual(
            new[]
            {
                "cols a:int:Int32:SqlInt32 b:nvarchar:String:SqlString",
                "row Int32:1/SqlInt32 String:one/SqlString",
                "row Int32:2/SqlInt32 NULL/SqlString",
                "recs=-1",
                "cols :int:Int32:SqlInt32",
                "row Int32:1/SqlInt32",
                "recs=2",
            },
            Messages(Sim(), "exec rd N'select a, b from t order by a'; exec rd N'update t set a = a; select 1'"));

    [TestMethod]
    [Description("Typed getters read their own column type, the SqlTypes getters carry NULL and the collation's locale, and the wrong getter or a NULL throws what the provider throws.")]
    public void TypedGetters_ReadAsTheProviderDoes()
        => CollectionAssert.AreEqual(
            new[]
            {
                "1 2 1.50 v 1033 2020-01-02T00:00:00 12:34:00 <a /> True SqlInt32:7",
                "Data is Null. This method or property cannot be called on Null values.",
                "Unable to cast object of type 'System.Int32' to type 'System.String'.",
            },
            Messages(Sim("create procedure dbo.ty as external name simclr.Ctx.Typed"), "exec dbo.ty"));

    [TestMethod]
    [Description("SqlPipe.ExecuteAndSend sends the command's result sets to the client; its error reaches the client ahead of the routine's own Msg 6522.")]
    public void ExecuteAndSend_SendsToTheClient()
    {
        var sim = Sim();
        using var connection = sim.CreateOpenConnection();
        using (var reader = connection.CreateCommand("exec es N'select a from t order by a'").ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(1, reader.GetInt32(0));
            IsTrue(reader.Read());
            AreEqual(2, reader.GetInt32(0));
            IsFalse(reader.Read());
        }

        var ex = sim.AssertSqlError("exec es N'select 1/0'", 8134);
        AreEqual(6522, ex.Errors[1].Number);
        Contains("System.Data.SqlClient.SqlException: Divide by zero error encountered.", ex.Errors[1].Message);
    }

    [TestMethod]
    [Description("SqlPipe.Send(SqlDataReader) sends the rows after the one the reader is on.")]
    public void PipeSendReader_SendsWhatIsLeft()
    {
        var sim = Sim("create procedure dbo.sr @sql nvarchar(max) as external name simclr.Ctx.SendReader");
        using var connection = sim.CreateOpenConnection();
        using var reader = connection.CreateCommand("exec dbo.sr N'select 1 a union all select 2 union all select 3'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    [Description("Parameters bind by value and by type — an untyped string as nvarchar, a sized output cut to its size — and output values come back in both forms.")]
    public void Parameters_BindAndWriteBack()
    {
        var sim = Sim("create procedure dbo.prm @a int, @b int output as external name simclr.Ctx.Params");
        CollectionAssert.AreEqual(new[] { "String:nvarchar Int32:10 SqlInt32:10 String:zzzz" }, Messages(sim, "declare @b int; exec dbo.prm 5, @b output"));
        AreEqual(10, sim.ExecuteScalar("declare @b int; exec dbo.prm 5, @b output; select @b"));
    }

    [TestMethod]
    [Description("The in-process provider declares a decimal parameter, typed or not, as numeric (probed 2026-10-06 against SQL Server 2025).")]
    public void DecimalParameters_ReportNumeric()
    {
        var sim = Sim("create procedure dbo.dp as external name simclr.Ctx.DecimalParams");
        CollectionAssert.AreEqual(new[] { "String:numeric numeric 3" }, Messages(sim, "exec dbo.dp"));
    }

    [TestMethod]
    [Description("CommandType.StoredProcedure executes the named procedure with its output and return-value parameters.")]
    public void StoredProcedureCommand_ReadsOutputAndReturnValue()
        => CollectionAssert.AreEqual(
            new[] { "n=-1 b=Int32:40 rv=Int32:3" },
            Messages(
                Sim("create procedure dbo.tp @a int, @b int output as begin set @b = @a * 10; select @a; return 3 end",
                    "create procedure dbo.stp as external name simclr.Ctx.StoredProc"),
                "exec dbo.stp"));

    [TestMethod]
    [Description("One context connection at a time, one reader at a time, and no other connection from a SAFE assembly — each the provider's own exception inside Msg 6522.")]
    public void ProviderRefusals_SurfaceInsideMsg6522()
    {
        var sim = Sim(
            "create procedure dbo.two as external name simclr.Ctx.TwoConns",
            "create procedure dbo.rdr as external name simclr.Ctx.TwoReaders",
            "create procedure dbo.ext as external name simclr.Ctx.NonContext");
        Contains("System.InvalidOperationException: The context connection is already in use.", sim.AssertSqlError("exec dbo.two", 6522).Message);
        Contains("System.InvalidOperationException: There is already an open DataReader associated with this Command which must be closed first.", sim.AssertSqlError("exec dbo.rdr", 6522).Message);
        Contains("System.Security.SecurityException: Request for the permission of type 'System.Data.SqlClient.SqlClientPermission, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089' failed.", sim.AssertSqlError("exec dbo.ext", 6522).Message);
    }

    [TestMethod]
    [Description("A command's error is a SqlException the routine may catch, at line 0 and unseen by the client; uncaught, it is Msg 6522.")]
    public void CommandErrors_AreSqlExceptions()
    {
        var sim = Sim();
        CollectionAssert.AreEqual(
            new[] { "caught 8134 16 1 0 Divide by zero error encountered.", "then Int32:0" },
            Messages(sim, "exec ca N'select 1/0'"));
        var ex = sim.AssertSqlError("exec sc N'select * from nosuch'", 6522);
        AreEqual(1, ex.State);
        Contains("System.Data.SqlClient.SqlException: Invalid object name 'nosuch'.", ex.Message);
    }

    [TestMethod]
    [Description("What a command prints reaches only the connection's InfoMessage handlers, as one event.")]
    public void CommandMessages_ReachInfoMessageOnly()
        => CollectionAssert.AreEqual(
            new[] { "info: hello\r\nten" },
            Messages(Sim("create procedure dbo.msg as external name simclr.Ctx.Messages"), "exec dbo.msg"));

    [TestMethod]
    [Description("A command runs one level deeper than the routine, inside the caller's transaction.")]
    public void Commands_NestInsideTheRoutine()
        => CollectionAssert.AreEqual(
            new[] { "Int32:2", "Int32:1" },
            Messages(Sim(), "exec sc N'select @@nestlevel'; begin tran; exec sc N'select @@trancount'; commit"));

    [TestMethod]
    [Description("SqlConnection.BeginTransaction nests a transaction the routine commits or rolls back; rolling back one the caller held is Msg 3994, which the routine's throw turns into Msg 6549.")]
    public void SqlTransaction_BeginsInTheSession()
    {
        var sim = Sim("create procedure dbo.tr @commit bit as external name simclr.Ctx.[Tran]");
        CollectionAssert.AreEqual(new[] { "Int32:1", "Int32:1" }, Messages(sim, "exec dbo.tr 1; exec dbo.tr 0"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from log"));

        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; exec dbo.tr 0").ExecuteNonQuery());
        AreEqual(6549, ex.Number);
        Contains("tried to rollback a transaction that is not started in that CLR level", ex.Errors[0].Message);
        EndsWith(". User transaction, if any, will be rolled back.", ex.Errors[0].Message);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    [Description("A caller's transaction a command ended — ROLLBACK is Msg 3994, a COMMIT ending it Msg 3990 — is Msg 3991 once the routine returns, rolling it back, or under a TRY leaving it uncommittable; one the routine began and left open rolls back quietly.")]
    public void EndedEntryTransaction_IsMsg3991()
    {
        var sim = Sim();
        using var connection = sim.CreateOpenConnection();
        AreEqual(3991, Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; exec ca N'rollback'").ExecuteNonQuery()).Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        var uncommittable = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin try begin tran; exec ca N'commit' end try begin catch end catch").ExecuteNonQuery());
        CollectionAssert.AreEqual(
            new[] { "Uncommittable transaction is detected at the end of the batch. The transaction is rolled back.", "caught 3990 16 1 0 Transaction is not allowed to commit inside of a user defined routine, trigger or aggregate because the transaction is not started in that CLR level. Change application logic to enforce strict transaction nesting.", "then Int32:1" },
            uncommittable.Errors.Select(error => error.Message).ToArray());
        AreEqual(0, sim.ExecuteScalar("exec nq N'begin tran; insert log values (1)'; select @@trancount + (select count(*) from log)"));
    }

    [TestMethod]
    [Description("The connection reports the session's database once open, and a database change or a #temp it creates lasts only until the routine returns.")]
    public void RoutineScope_RevertsWhenTheRoutineReturns()
    {
        var sim = Sim("create procedure dbo.props as external name simclr.Ctx.Props");
        var version = sim.CreateOpenConnection().ServerVersion;
        CollectionAssert.AreEqual(new[] { "Closed []", $"Open [simulated] {version}", "String:master" }, Messages(sim, "exec dbo.props"));
        AreEqual("simulated", sim.ExecuteScalar("exec dbo.props; select db_name()"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("exec nq N'create table #x (a int)'; select object_id('tempdb..#x')"));
    }

    [TestMethod]
    [Description("A function marked DataAccessKind.Read reads through the context connection and sees no pipe; unmarked it is refused, SystemDataAccessKind.Read alone reads no user object, and a write is Msg 443.")]
    public void Functions_ReadOnlyWhenMarked()
    {
        var sim = Sim(
            "create function dbo.fr(@sql nvarchar(100)) returns int as external name simclr.Ctx.FnRead",
            "create function dbo.fn(@sql nvarchar(100)) returns int as external name simclr.Ctx.FnNone",
            "create function dbo.fnmax(@sql nvarchar(max)) returns int as external name simclr.Ctx.FnNone",
            "create function dbo.fs(@sql nvarchar(100)) returns int as external name simclr.Ctx.FnSys",
            "create function dbo.fw(@sql nvarchar(100)) returns int as external name simclr.Ctx.FnWrite",
            "create function dbo.fp() returns bit as external name simclr.Ctx.FnPipe",
            "create function dbo.tv(@sql nvarchar(100)) returns table (v int) as external name simclr.Ctx.TvfRead");
        AreEqual(2, sim.ExecuteScalar("select dbo.fr(N'select count(*) from t')"));
        IsTrue((bool)sim.ExecuteScalar("select dbo.fp()")!);
        AreEqual(3, sim.ExecuteScalar("select sum(v) from dbo.tv(N'select a from t')"));
        AreEqual(2, sim.AssertSqlError("select dbo.fn(N'select 1')", 6522).State);
        var unmarked = sim.AssertSqlError("select dbo.fnmax(N'select 1')", 6522);
        AreEqual(1, unmarked.State);
        Contains("Data access is not allowed in this context.", unmarked.Message);
        IsGreaterThan(0, (int)sim.ExecuteScalar("select dbo.fs(N'select count(*) from sys.objects')")!);
        Contains("System.Data.SqlClient.SqlException: This statement has attempted to access data whose access is restricted by the assembly.", sim.AssertSqlError("select dbo.fs(N'select count(*) from t')", 6522).Message);
        Contains("Invalid use of a side-effecting operator 'INSERT' within a function.", sim.AssertSqlError("select dbo.fw(N'insert log values (1)')", 6522).Message);
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
        // The write is refused as it is reached, after the statements ahead of
        // it ran, which ExecuteScalar reads past (probed 2026-10-06 against SQL
        // Server 2025).
        AreEqual(7, sim.ExecuteScalar("select dbo.fr(N'select 7; insert log values (1); select 8')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    [Description("A CLR trigger reads INSERTED and DELETED through the context connection, and writes from them.")]
    public void Trigger_ReadsInsertedAndDeleted()
    {
        var sim = Sim(
            "create trigger tr on t after insert, update, delete as external name simclr.Ctx.TrigCount",
            "create trigger tl on t after insert as external name simclr.Ctx.TrigLog");
        CollectionAssert.AreEqual(new[] { "ins=Int32:2 del=Int32:0" }, Messages(sim, "insert t values (10, N'a'), (20, N'b')"));
        CollectionAssert.AreEqual(new[] { "ins=Int32:2 del=Int32:2" }, Messages(sim, "update t set a = a + 1 where a > 5"));
        CollectionAssert.AreEqual(new[] { "ins=Int32:0 del=Int32:2" }, Messages(sim, "delete t where a > 5"));
        AreEqual(30, sim.ExecuteScalar("select sum(a) from log"));
    }

    [TestMethod]
    [Description("An INSTEAD OF trigger's context connection reads the rows the statement would have written.")]
    public void InsteadOfTrigger_ReadsInserted()
        => AreEqual(30, Sim(
                "create trigger tl on t instead of insert as external name simclr.Ctx.TrigLog")
            .ExecuteScalar("insert t values (10, N'a'), (20, N'b'); select sum(a) + (select count(*) from t where a > 5) from log"));

    [TestMethod]
    [Description("ExecuteAndSend from a trigger sends its result set among the firing statement's outcomes, and a trigger's command runs one level below the trigger inside its transaction.")]
    public void Trigger_SendsAndNests()
    {
        var sim = Sim(
            "create trigger ts on t after insert as external name simclr.Ctx.TrigSend",
            "create trigger tn on t after insert as external name simclr.Ctx.TrigNest");
        using var connection = sim.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.Add(e.Message);
        using (var reader = connection.CreateCommand("insert t values (20, N'b'), (10, N'a')").ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(10, reader.GetInt32(0));
            IsTrue(reader.Read());
            AreEqual(20, reader.GetInt32(0));
        }

        CollectionAssert.AreEqual(new[] { "Int32:211" }, messages);
    }

    [TestMethod]
    [Description("A trigger's context connection may not end the firing statement's transaction: a ROLLBACK is Msg 6549 when it throws out, and a caught error that ended it Msg 3991 when the trigger returns; either undoes the statement.")]
    public void Trigger_EndingTheTransaction_UndoesTheStatement()
    {
        var sim = Sim(
            "create table u (a int)",
            "create trigger tr on t after insert as external name simclr.Ctx.TrigRollback",
            "create trigger tu on u after insert as external name simclr.Ctx.TrigCatch");
        var rolledBack = sim.AssertSqlError("insert t values (10, N'a')", 6549);
        AreEqual(3621, rolledBack.Errors[1].Number);
        _ = sim.AssertSqlError("insert u values (1)", 3991);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t union all select count(*) from u order by 1 desc"));
    }

    [TestMethod]
    [Description("A DDL trigger's context connection reads EVENTDATA().")]
    public void DdlTrigger_ReadsEventData()
        => CollectionAssert.AreEqual(
            new[] { "String:zz" },
            Messages(Sim("create trigger td on database for create_table as external name simclr.Ctx.TrigDdl"), "create table zz (a int)"));
}
