using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Numbered procedures — <c>CREATE PROCEDURE p;2</c>, a member of the group
/// <c>p</c> run as <c>EXEC p;2</c>. Every expectation probed 2026-09-26 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class NumberedProcedureTests
{
    private static Simulation Group()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure p as select 'one'",
            "create procedure p;2 @x int = 5 as select 'two' + str(@x, 1)",
            "create proc p ; 3 as select 'three'");
        return sim;
    }

    [TestMethod]
    [DataRow("exec p", "one")]
    [DataRow("exec p;1", "one")]
    [DataRow("exec p;2", "two5")]
    [DataRow("exec p;2 7", "two7")]
    [DataRow("exec dbo.p ; 3", "three")]
    [DataRow("exec sp_executesql N'exec p;2'", "two5")]
    public void Exec_RunsTheNumberedMember(string statement, string expected)
        => AreEqual(expected, Group().ExecuteScalar(statement));

    [TestMethod]
    public void TheGroup_ListsAsOneProcedureWithItsMembersNumbered()
    {
        var sim = Group();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.procedures where name = 'p'"));
        AreEqual("2:1|3:1", sim.ExecuteScalar("""
            select string_agg(concat(procedure_number, ':', iif(object_id = object_id('p'), 1, 0)), '|') within group (order by procedure_number)
            from sys.numbered_procedures
            """));
        AreEqual("2:@x:1", sim.ExecuteScalar("select concat(procedure_number, ':', name, ':', parameter_id) from sys.numbered_procedure_parameters"));
        AreEqual("create procedure p as select 'one'", sim.ExecuteScalar("select object_definition(object_id('p'))"));
    }

    [TestMethod]
    public void AMember_ReportsAsItsGroup()
    {
        var sim = Group();
        sim.ExecuteBatches("create procedure p;4 @a int as begin begin try declare @z int = 1 / 0 end try begin catch select concat(error_procedure(), ':', @@procid - object_id('p')) end catch end");
        AreEqual("p:0", sim.ExecuteScalar("exec p;4 1"));
        AreEqual("Procedure or function 'p' expects parameter '@a', which was not supplied.", sim.AssertSqlError("exec p;4", 201).Errors[0].Message);
    }

    [TestMethod]
    [DataRow("exec p;9", 2812, "")]
    [DataRow("create procedure p;2 as select 22", 2004, "p")]
    [DataRow("alter procedure p;9 as select 9", 208, "p")]
    [DataRow("create procedure q;2 as select 2", 2730, "q")]
    [DataRow("create procedure p;0 as select 0", 1005, "p")]
    [DataRow("create procedure p;32768 as select 0", 1005, "p")]
    [DataRow("drop procedure p;2", 102, "")]
    public void TheGroupsRules_RefuseWhereRealDoes(string statement, int number, string procedure)
        => AreEqual(procedure, Group().AssertSqlError(statement, number).Errors[0].Procedure ?? "");

    [TestMethod]
    public void AnInvalidNumber_NamesItsLine()
        => AreEqual("Line 2: Invalid procedure number (0). Must be between 1 and 32767.",
            Group().AssertSqlError("\ncreate procedure p;0 as select 0", 1005).Errors[0].Message);

    [TestMethod]
    public void AlterAndDrop_ActOnTheWholeGroup()
    {
        var sim = Group();
        sim.ExecuteBatches("alter procedure p;2 as select 'altered'", "alter procedure p as select 'base'", "create or alter procedure p;5 as select 'five'");
        AreEqual("altered", sim.ExecuteScalar("exec p;2"));
        AreEqual("five", sim.ExecuteScalar("exec p;5"));
        AreEqual("2,3,5", sim.ExecuteScalar("select string_agg(procedure_number, ',') within group (order by procedure_number) from sys.numbered_procedures"));
        AreEqual(0, sim.ExecuteScalar("drop procedure p; select count(*) from sys.numbered_procedures"));
    }

    [TestMethod]
    public void Rename_CarriesTheMembers()
    {
        var sim = Group();
        _ = sim.ExecuteNonQuery("exec sp_rename 'p', 'q'");
        AreEqual("three", sim.ExecuteScalar("exec q;3"));
    }

    [TestMethod]
    public void SpHelpText_RunsOnThroughTheMembers()
        => AreEqual("create procedure p as select 'one'create procedure p;2 @x int = 5 as select 'two' + str(@x, 1)create proc p ; 3 as select 'three'",
            string.Concat(Group().ExecuteReader("exec sp_helptext 'p'").EnumerateRecords().Select(record => record.GetString(0))));

    [TestMethod]
    [DataRow("create procedure p @a int, @A int as select 1")]
    [DataRow("create function f(@a int, @A int) returns table as return select 1 x")]
    public void ARepeatedParameter_IsMsg134(string statement)
        => new Simulation().AssertSqlError(statement, 134, "The variable name '@A' has already been declared. Variable names must be unique within a query batch or stored procedure.");

    [TestMethod]
    public void ACreateOverATakenName_NamesTheProcedure()
        => AreEqual("p", Group().AssertSqlError("create procedure p as select 2", 2714).Errors[0].Procedure);
}
