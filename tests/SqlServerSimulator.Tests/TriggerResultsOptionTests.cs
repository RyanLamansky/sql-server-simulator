using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>disallow results from triggers</c> server option: installed as 1, a
/// statement that would send a result set while a trigger runs raises Msg 524
/// in its place (probed 2026-10-17 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class TriggerResultsOptionTests
{
    private const string Disallow = "exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'disallow results from triggers', 1; reconfigure";

    /// <summary>A simulation with the option installed, <c>t</c> and <c>l</c>, and <paramref name="trigger"/> created.</summary>
    private static Simulation With(string trigger, params string[] more)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches([Disallow, "create table t (a int); create table l (a int)", trigger, .. more]);
        return simulation;
    }

    [TestMethod]
    public void StagedWithoutReconfigure_BodyStillSendsItsRows()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'disallow results from triggers', 1",
            "create table t (a int)",
            "create trigger tr on t after insert as select 7 as x");
        AreEqual(7, simulation.ExecuteScalar("insert t values (1)"));
        AreEqual("1|0", simulation.ExecuteScalar("select concat(cast(value as int), '|', cast(value_in_use as int)) from sys.configurations where name = 'disallow results from triggers'"));
    }

    [TestMethod]
    public void BodySelect_IsMsg524AtItsLine_AndTheFiringWriteRollsBack()
    {
        var simulation = With("""
            create trigger tr on t after insert as
            begin
                print 'before'
                insert l values (1)
                select 1 as x where 1 = 0
                print 'after'
            end
            """);
        var ex = simulation.AssertSqlError("insert t values (1); insert l values (2)", 524);
        AreEqual("A trigger returned a resultset and the server option 'disallow results from triggers' is true.", ex.Errors[0].Message);
        AreEqual(16, ex.Class);
        AreEqual("tr", ex.Procedure);
        AreEqual(5, ex.LineNumber);
        // The batch ended there, the INSERT after it never running.
        AreEqual(0, simulation.ExecuteScalar("select (select count(*) from t) + (select count(*) from l)"));
    }

    [TestMethod]
    public void Caught_LeavesNoTransaction()
        => AreEqual("524|0|0", With("create trigger tr on t after insert as select 1").ExecuteScalar("""
            begin try insert t values (1) end try
            begin catch declare @s int = xact_state(); select concat(error_number(), '|', @@trancount, '|', @s) end catch
            """));

    [TestMethod]
    public void CaughtInAnExplicitTransaction_DoomsIt()
        => AreEqual("524|1|-1", With("create trigger tr on t after insert as select 1").ExecuteScalar("""
            set xact_abort off;
            begin tran;
            begin try insert t values (1) end try
            begin catch declare @s int = xact_state(); select concat(error_number(), '|', @@trancount, '|', @s); rollback end catch
            """));

    [TestMethod]
    [DataRow("declare c cursor local for select a from inserted; open c; fetch next from c")]
    [DataRow("insert l output inserted.a select a from inserted")]
    [DataRow("dbcc useroptions")]
    [DataRow("exec ('select 1')")]
    [DataRow("set nocount on; select a from inserted")]
    public void ResultProducingStatement_IsMsg524(string body)
        => _ = With($"create trigger tr on t after insert as begin {body} end").AssertSqlError("insert t values (1)", 524);

    [TestMethod]
    [DataRow("if 1 = 0 select 1")]
    [DataRow("declare @v int; select @v = a from inserted")]
    [DataRow("select a into #x from inserted")]
    [DataRow("declare @v int; declare c cursor local for select a from inserted; open c; fetch next from c into @v")]
    [DataRow("declare @x table (a int); insert @x exec ('select 1')")]
    [DataRow("declare @x table (a int); insert l output inserted.a into @x select a from inserted")]
    [DataRow("exec ('declare @v int; select @v = 1')")]
    public void RowlessStatement_RunsOn(string body)
        => AreEqual(1, With($"create trigger tr on t after insert as begin {body} end").ExecuteScalar("insert t values (1); select count(*) from t"));

    [TestMethod]
    public void ProcedureTheTriggerCalls_ReportsAtItsOwnLine()
    {
        var ex = With(
            "create trigger tr on t after insert as exec p",
            """
            create procedure p as
            begin
                print 'in p'
                select 2 as fromp
            end
            """).AssertSqlError("insert t values (1)", 524);
        AreEqual("p", ex.Procedure);
        AreEqual(4, ex.LineNumber);
    }

    [TestMethod]
    public void DynamicBatchTheTriggerRuns_ReportsWithoutAProcedure()
    {
        var ex = With("""
            create trigger tr on t after insert as exec sp_executesql N'
            select 1 as dyn'
            """).AssertSqlError("insert t values (1)", 524);
        AreEqual("", ex.Procedure);
        AreEqual(2, ex.LineNumber);
    }

    [TestMethod]
    public void InsteadOfTrigger_IsMsg524()
        => _ = With("create trigger tr on t instead of insert as select 9").AssertSqlError("insert t values (1)", 524);

    [TestMethod]
    public void DdlTrigger_IsMsg524_AndTheDefinitionRollsBack()
    {
        var simulation = With("create trigger ddl on database for create_table as select 3 as ddlrow");
        AreEqual("ddl", simulation.AssertSqlError("create table zz (a int)", 524).Procedure);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('zz')"));
    }

    [TestMethod]
    public void TurnedOffAgain_BodySendsItsRows()
    {
        var simulation = With("create trigger tr on t after insert as select 7 as x");
        _ = simulation.ExecuteNonQuery("exec sp_configure 'disallow results from triggers', 0; reconfigure");
        AreEqual(7, simulation.ExecuteScalar("insert t values (1)"));
    }
}
