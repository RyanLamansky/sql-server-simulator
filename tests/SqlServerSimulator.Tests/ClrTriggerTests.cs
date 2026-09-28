using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ClrTriggerTests
{
    private static List<(string Message, int Line, string Procedure)> ExecuteCollectingMessages(Simulation sim, string commandText)
    {
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<(string, int, string)>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add((error.Message, error.LineNumber, error.Procedure));
        };
        using var command = connection.CreateCommand(commandText);
        _ = command.ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    [Description("SqlContext.TriggerContext reports the verb, the column count and which columns count as updated; Pipe.Send is a line-1 message naming the trigger.")]
    public void DmlTrigger_ReportsTriggerContext()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create table t (a int, b int, c int)",
            "create trigger tr on t after insert, update, delete as external name simclr.Trig.Report");
        AreEqual(("action=Insert cols=3 upd=111", 1, "tr"), ExecuteCollectingMessages(sim, "insert t values (1, 2, 3)").Single());
        AreEqual("action=Update cols=3 upd=010", ExecuteCollectingMessages(sim, "update t set b = 5").Single().Message);
        AreEqual("action=Update cols=3 upd=101", ExecuteCollectingMessages(sim, "update t set a = 1, c = 2 where 1 = 0").Single().Message);
        AreEqual("action=Delete cols=3 upd=111", ExecuteCollectingMessages(sim, "delete t").Single().Message);
    }

    [TestMethod]
    [Description("An INSTEAD OF trigger, on a table or a view, reads its own action; the table keeps no row.")]
    public void InsteadOfTrigger_ReportsTriggerContext()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create table t (a int, b int)",
            "create view v as select a, b from t",
            "create trigger tr on t instead of insert as external name simclr.Trig.Report",
            "create trigger trv on v instead of update as external name simclr.Trig.Report");
        AreEqual("action=Insert cols=2 upd=11", ExecuteCollectingMessages(sim, "insert t values (1, 2)").Single().Message);
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
        AreEqual("action=Update cols=2 upd=01", ExecuteCollectingMessages(sim, "update v set b = 1").Single().Message);
    }

    [TestMethod]
    [Description("A throw is Msg 6522 state 1 at the trigger's line 1, followed by Msg 3621, and rolls the statement and its transaction back.")]
    public void DmlTrigger_Throw_RollsBack()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create table t (a int)",
            "create trigger tr on t after insert as external name simclr.Trig.Throws");
        var ex = sim.AssertSqlError("begin tran; insert t values (1)", 6522);
        AreEqual((1, 1, "tr"), (ex.State, ex.LineNumber, ex.Procedure));
        Contains("System.InvalidOperationException: trigger boom", ex.Message);
        Contains("The statement has been terminated.", ex.Message);
        AreEqual("0|0", sim.ExecuteScalar("select concat_ws('|', @@trancount, count(*)) from t"));
        AreEqual("6522|tr|0", sim.ExecuteScalar(
            "begin try insert t values (1) end try begin catch select concat_ws('|', error_number(), error_procedure(), xact_state()) end catch"));
    }

    [TestMethod]
    [Description("A trigger's result set reaches the client; EventData is null for DML; an out-of-range IsUpdatedColumn throws from the context itself.")]
    public void DmlTrigger_ResultsEventDataAndBadColumn()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create table t (a int)",
            "create trigger tr on t after insert as external name simclr.Trig.Rows",
            "create trigger tr2 on t after insert as external name simclr.Trig.EventDataOnDml");
        AreEqual(7, sim.ExecuteScalar("insert t values (1)"));
        AreEqual("null", ExecuteCollectingMessages(sim, "insert t values (1)").Single().Message);
        sim.ExecuteBatches("create trigger tr3 on t after insert as external name simclr.Trig.BadColumn");
        var ex = sim.AssertSqlError("insert t values (1)", 6522);
        Contains("System.IndexOutOfRangeException: Index was outside the bounds of the array.", ex.Message);
        Contains("at Microsoft.SqlServer.Server.SqlTriggerContext.IsUpdatedColumn(Int32 columnOrdinal)", ex.Message);
    }

    [TestMethod]
    [Description("A DDL trigger reads the event type's number as its action and the event's document as EventData.")]
    public void DdlTrigger_ReportsEvent()
    {
        var sim = ClrFrameworkFixture.Simulation("create trigger td on database for create_table as external name simclr.Trig.Ddl");
        AreEqual(("action=CreateTable cols=0 event=create_table", 1, "td"), ExecuteCollectingMessages(sim, "create table x (a int)").Single());
        AreEqual("TA|CLR_TRIGGER|DATABASE", sim.ExecuteScalar("select concat_ws('|', type, type_desc, parent_class_desc) from sys.triggers where name = 'td'"));
    }

    [TestMethod]
    [Description("The catalog reports a CLR trigger as TA / CLR_TRIGGER with an assembly module and no T-SQL text.")]
    public void Catalog()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create table t (a int)",
            "create trigger tr on t after insert as external name simclr.Trig.Report");
        AreEqual("TA|CLR_TRIGGER", sim.ExecuteScalar("select concat_ws('|', type, type_desc) from sys.objects where name = 'tr'"));
        AreEqual("TA", sim.ExecuteScalar("select type from sys.triggers where name = 'tr'"));
        AreEqual("Trig|Report", sim.ExecuteScalar("select concat_ws('|', assembly_class, assembly_method) from sys.assembly_modules where object_id = object_id('tr')"));
        AreEqual(1, sim.ExecuteScalar("select case when object_definition(object_id('tr')) is null and objectproperty(object_id('tr'), 'IsTrigger') = 1 then 1 end"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.sql_modules where object_id = object_id('tr')"));
        _ = sim.AssertSqlError("exec sp_helptext 'tr'", 15197);
        _ = sim.AssertSqlError("drop assembly simclr", 6590);
    }

    [TestMethod]
    [Description("CREATE TRIGGER binds the method as real does: void, parameterless, no WITH ENCRYPTION, and no ALTER across the T-SQL / CLR line.")]
    [DataRow("create trigger t1 on t after insert as external name simclr.Trig.ReturnsInt", 6500)]
    [DataRow("create trigger t2 on t after insert as external name simclr.Trig.TakesArg", 6531)]
    [DataRow("create trigger t3 on t after insert as external name simclr.Trig.Nope", 6506)]
    [DataRow("create trigger t4 on t after insert as external name simclr.Nope.Report", 6505)]
    [DataRow("create trigger t5 on t after insert as external name nosuch.Trig.Report", 6528)]
    [DataRow("create trigger t6 on t after insert as external name simclr.Trig", 102)]
    [DataRow("create trigger t7 on t with encryption after insert as external name simclr.Trig.Report", 10324)]
    [DataRow("alter trigger tsql on t after insert as external name simclr.Trig.Report", 6530)]
    [DataRow("alter trigger tclr on t after insert as select 1", 2010)]
    public void Create_Refused(string sql, int number)
        => _ = ClrFrameworkFixture.Simulation(
            "create table t (a int)",
            "create trigger tsql on t after insert as select 1",
            "create trigger tclr on t after update as external name simclr.Trig.Report").AssertSqlError(sql, number);

    [TestMethod]
    [Description("SqlContext.TriggerContext is null in a procedure and refused in a function.")]
    public void TriggerContext_OutsideTriggers()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create procedure p as external name simclr.Trig.ProcReadsTrigger",
            "create function f() returns bit as external name simclr.Trig.FuncTrig");
        AreEqual("proc trig=False", ExecuteCollectingMessages(sim, "exec p").Single().Message);
        Contains("Data access is not allowed in this context.", sim.AssertSqlError("select dbo.f()", 6522).Message);
    }

    [TestMethod]
    [Description("A CLR trigger over a taken name is Msg 2714 at state 5, where a T-SQL one's is state 2.")]
    [DataRow("create procedure x as select 1")]
    [DataRow("create table x (a int)")]
    [DataRow("create trigger x on t after update as select 1")]
    public void Create_NameTaken_State5(string holder)
        => AreEqual(5, ClrFrameworkFixture.Simulation("create table t (a int)", holder)
            .AssertSqlError("create trigger x on t after insert as external name simclr.Trig.Report", 2714).State);
}
