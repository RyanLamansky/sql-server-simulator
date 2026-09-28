using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET IMPLICIT_TRANSACTIONS ON</c>: which statements open the session's
/// transaction, how it nests with <c>BEGIN TRANSACTION</c>, what ends it, and
/// how the option scopes to a module body.
/// </summary>
[TestClass]
public sealed class ImplicitTransactionsTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int primary key); insert t values (1); create sequence sq",
            "create function f() returns int as begin return 1 end",
            "create procedure p as select 1");
        return sim;
    }

    private static object? Run(DbConnection connection, string commandText) => connection.CreateCommand(commandText).ExecuteScalar();

    [TestMethod]
    [DataRow("select count(*) from t", 1)]
    [DataRow("select count(*) from sys.objects", 1)]
    [DataRow("declare @v table (a int); select count(*) from @v", 1)]
    [DataRow("create table #z (a int); select count(*) from #z", 1)]
    [DataRow("with c as (select a from t) select count(*) from c", 1)]
    [DataRow("select next value for sq", 1)]
    [DataRow("declare @x bigint; set @x = next value for sq", 1)]
    [DataRow("select dbo.f()", 1)]
    [DataRow("declare @x int = (select dbo.f())", 1)]
    [DataRow("declare @x int = (select count(*) from t)", 1)]
    [DataRow("select case when exists (select * from t) then 1 end", 1)]
    [DataRow("insert t values (5)", 1)]
    [DataRow("update t set a = a where 1 = 0", 1)]
    [DataRow("declare @v table (a int); delete @v", 1)]
    [DataRow("truncate table t", 1)]
    [DataRow("create table t2 (a int)", 1)]
    [DataRow("alter table t add b int", 1)]
    [DataRow("drop table t", 1)]
    [DataRow("grant select on t to public", 1)]
    [DataRow("update statistics t", 1)]
    [DataRow("declare c cursor for select 1", 1)]
    [DataRow("select 1", 0)]
    [DataRow("select object_id('t'), db_name(), concat('a', 'b')", 0)]
    [DataRow("select count(*) from string_split('a,b', ',')", 0)]
    [DataRow("select * from (values (1)) v (a)", 0)]
    [DataRow("with c as (select 1 a) select * from c", 0)]
    [DataRow("declare @x int = dbo.f()", 0)]
    [DataRow("declare @x int; select @x = dbo.f()", 0)]
    [DataRow("if exists (select * from t) print 'x'", 0)]
    [DataRow("if (select count(*) from t) > 0 print 'x'", 0)]
    [DataRow("while exists (select * from t where a = 9) break", 0)]
    [DataRow("exec p", 0)]
    [DataRow("exec sp_executesql N'select 1'", 0)]
    [DataRow("print 'x'", 0)]
    [DataRow("set nocount on", 0)]
    public void Statement_OpensTransaction(string statement, int expectedTranCount)
    {
        using var connection = Seeded().CreateOpenConnection();
        _ = Run(connection, "set implicit_transactions on");
        _ = Run(connection, statement);
        AreEqual(expectedTranCount, Run(connection, "select @@trancount"));
    }

    [TestMethod]
    public void TransactionStaysOpenAcrossBatches_UntilCommit()
    {
        var sim = Seeded();
        using (var connection = sim.CreateOpenConnection())
        {
            _ = Run(connection, "set implicit_transactions on; insert t values (2)");
            AreEqual(1, Run(connection, "select open_transaction_count from sys.dm_exec_sessions where session_id = @@spid"));
            AreEqual(0, Run(connection, "commit; select @@trancount"));
            _ = Run(connection, "insert t values (3)");
            AreEqual(1, Run(connection, "select @@trancount"));
        }

        // Closing the connection rolled the second transaction back.
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void BeginTransaction_NestsInsideTheImplicitOne()
    {
        using var connection = Seeded().CreateOpenConnection();
        AreEqual(2, Run(connection, "set implicit_transactions on; begin tran; select @@trancount"));
        AreEqual(1, Run(connection, "commit; select @@trancount"));
        AreEqual(0, Run(connection, "commit; select @@trancount"));
    }

    [TestMethod]
    public void OpeningStatement_ReadsItsOwnTransaction()
        => AreEqual(12, Seeded().ExecuteScalar("set implicit_transactions on; insert t select @@trancount + 10; select max(a) from t; rollback"));

    [TestMethod]
    public void SelectListRead_PrecedesALaterStatementsOpen()
        => AreEqual(0, Seeded().ExecuteScalar("set implicit_transactions on select @@trancount select a from t"));

    [TestMethod]
    public void StatementTerminatingError_LeavesTransactionOpen()
    {
        using var connection = Seeded().CreateOpenConnection();
        _ = Run(connection, "set implicit_transactions on");
        _ = Throws<SimulatedSqlException>(() => Run(connection, "insert t values (1)"));
        AreEqual(1, Run(connection, "select @@trancount"));
    }

    [TestMethod]
    [DataRow("insert t values ('x')")]
    [DataRow("select * from missing")]
    public void RollingBackOrCompileError_LeavesNoTransaction(string failing)
    {
        using var connection = Seeded().CreateOpenConnection();
        _ = Run(connection, "set implicit_transactions on");
        _ = Throws<SimulatedSqlException>(() => Run(connection, failing));
        AreEqual(0, Run(connection, "select @@trancount"));
    }

    [TestMethod]
    public void XactAbort_RollsTheImplicitTransactionBack()
    {
        using var connection = Seeded().CreateOpenConnection();
        _ = Run(connection, "set xact_abort on; set implicit_transactions on");
        _ = Throws<SimulatedSqlException>(() => Run(connection, "insert t values (1)"));
        AreEqual(0, Run(connection, "select @@trancount"));
    }

    [TestMethod]
    public void TryCatch_SeesTheOpenTransaction()
        => AreEqual("1 -1", Seeded().ExecuteScalar("""
            set implicit_transactions on
            declare @r varchar(10)
            begin try insert t values ('x') end try
            begin catch set @r = concat(@@trancount, ' ', xact_state()) end catch
            rollback
            select @r
            """));

    [TestMethod]
    public void Option_ReadsThroughAtAtOptions_AsItRuns()
        => AreEqual("0 0 2", Seeded().ExecuteScalar("""
            declare @before int = @@options & 2
            if 1 = 0 set implicit_transactions on
            declare @skipped int = @@options & 2
            set implicit_transactions on
            select concat(@before, ' ', @skipped, ' ', @@options & 2)
            """));

    [TestMethod]
    public void ProcedureSet_RevertsOnReturn_AndRaisesNoMsg266()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure p2 as begin set implicit_transactions on; insert t values (7); end");
        using var connection = sim.CreateOpenConnection();
        AreEqual("0 1", Run(connection, "exec p2; select concat(@@options & 2, ' ', @@trancount)"));
    }

    [TestMethod]
    public void ProcedureTurningItOff_BeforeReturning_RaisesMsg266()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure p2 as begin set implicit_transactions off; begin tran; end");
        using var connection = sim.CreateOpenConnection();
        _ = Run(connection, "set implicit_transactions on");
        AreEqual(266, Throws<SimulatedSqlException>(() => Run(connection, "exec p2")).Number);
        AreEqual(1, Run(connection, "select @@trancount"));
    }

    [TestMethod]
    public void DynamicSqlSet_RevertsWithItsBatch()
        => AreEqual("2 0", Seeded().ExecuteScalar("""
            declare @inner int
            exec sp_executesql N'set implicit_transactions on; set @o = @@options & 2', N'@o int output', @inner output
            select concat(@inner, ' ', @@options & 2)
            """));

    [TestMethod]
    public void AnsiDefaults_TurnsItOnWithItsSiblings()
    {
        using var connection = Seeded().CreateOpenConnection();
        AreEqual(5432, Run(connection, "select @@options"));
        AreEqual(5438, Run(connection, "set ansi_defaults on; select @@options"));
        IsTrue((bool)Run(connection, "select ansi_defaults from sys.dm_exec_sessions where session_id = @@spid")!);
        AreEqual(1, Run(connection, "declare @n int = (select count(*) from t); select @@trancount"));
        IsFalse((bool)Run(connection, "rollback; set implicit_transactions off; select ansi_defaults from sys.dm_exec_sessions where session_id = @@spid")!);
        AreEqual(4096, Run(connection, "set ansi_defaults off; select @@options"));
    }
}
