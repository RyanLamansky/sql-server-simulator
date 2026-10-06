using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a trigger body's errors send after them, and what the body's
/// functions read of the firing statement (probed 2026-10-06 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class TriggerMessageAttributionTests
{
    private static Simulation Seeded(params ReadOnlySpan<string> batches)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int, b int); create table u (a int); create table l (a int)");
        simulation.ExecuteBatches(batches);
        return simulation;
    }

    private static string[] Entries(SimulatedSqlException ex) =>
        [.. ex.Errors.Select(error => $"{error.Number} L{error.LineNumber} {error.Procedure}".TrimEnd())];

    /// <summary>
    /// An error ending the body from a SELECT sending rows to the client is
    /// followed by Msg 3621 at that statement's line and in its module.
    /// </summary>
    [TestMethod]
    public void ClientSelect_TerminationNamesItsModuleAndLine()
    {
        var simulation = Seeded("create trigger tr on t after insert as begin\ndeclare @x int = 1;\nselect 1/0; end");
        var ex = simulation.AssertSqlError("insert t values (1, 1)", 8134);
        CollectionAssert.AreEqual(new[] { "8134 L3 tr", "3621 L3 tr" }, Entries(ex));
    }

    /// <summary>
    /// Any other statement writing nothing leaves it at line 1 outside any
    /// module, as a write does.
    /// </summary>
    [TestMethod]
    [DataRow("declare @x int;\nselect @x = 1/0;")]
    [DataRow("declare @x int;\nset @x = 1/0;")]
    [DataRow("declare @x int = 1;\nif 1/0 = 1 print 'x';")]
    [DataRow("declare @y int = 1;\ndeclare @x int = 1/0;")]
    [DataRow("declare @y int = 1;\nselect 1/a as c into #z from inserted;")]
    public void OtherNonWritingStatement_TerminationStaysAtLineOne(string body)
    {
        var simulation = Seeded($"create trigger tr on t after insert as begin\n{body} end");
        var ex = simulation.AssertSqlError("insert t values (0, 1)", 8134);
        CollectionAssert.AreEqual(new[] { "8134 L3 tr", "3621 L1" }, Entries(ex));
    }

    /// <summary>From a writing statement it stays at line 1 outside any module.</summary>
    [TestMethod]
    public void WritingStatement_TerminationStaysAtLineOne()
    {
        var simulation = Seeded("create trigger tr on t after insert as begin\ninsert l values (1/0); end");
        var ex = simulation.AssertSqlError("insert t values (1, 1)", 8134);
        CollectionAssert.AreEqual(new[] { "8134 L2 tr", "3621 L1" }, Entries(ex));
    }

    [TestMethod]
    public void ProcedureTheBodyCalls_TerminationNamesTheProcedure()
    {
        var simulation = Seeded("create procedure p as\nselect 1/0", "create trigger tr on t after insert as exec p");
        var ex = simulation.AssertSqlError("insert t values (1, 1)", 8134);
        CollectionAssert.AreEqual(new[] { "8134 L2 p", "3621 L2 p" }, Entries(ex));
    }

    [TestMethod]
    public void InnerTriggerOfAChain_TerminationNamesTheInnerTrigger()
    {
        var simulation = Seeded(
            "create trigger tu on u after insert as begin\nselect 1/0; end",
            "create trigger tr on t after insert as insert u values (1)");
        var ex = simulation.AssertSqlError("insert t values (1, 1)", 8134);
        CollectionAssert.AreEqual(new[] { "8134 L2 tu", "3621 L2 tu" }, Entries(ex));
    }

    [TestMethod]
    public void FunctionMissingObject_SendsNoTermination()
    {
        var simulation = Seeded(
            "create function f() returns int as begin return (select count(*) from nosuch) end",
            "create trigger tr on t after insert as insert l values (dbo.f())");
        var ex = simulation.AssertSqlError("insert t values (1, 1)", 208);
        DoesNotContain(3621, [.. ex.Errors.Select(error => error.Number)]);
    }

    /// <summary>
    /// With XACT_ABORT off in the body, a statement writing nothing earns its
    /// own Msg 3621, as it runs within the firing statement, and the body
    /// carries on.
    /// </summary>
    [TestMethod]
    public void XactAbortOff_NonWritingStatementIsFollowedByTermination()
    {
        var simulation = Seeded("create trigger tr on t after insert as begin set xact_abort off;\nselect 1/0; insert l values (1) end");
        var ex = simulation.AssertSqlError("insert t values (1, 1)", 8134);
        CollectionAssert.AreEqual(new[] { "8134 L2 tr", "3621 L2 tr" }, Entries(ex));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from l"));
    }

    /// <summary>
    /// A function or view the body reads sees the firing statement's
    /// <c>COLUMNS_UPDATED()</c>; a procedure or dynamic SQL it calls doesn't.
    /// </summary>
    [TestMethod]
    public void ColumnsUpdated_ReachesFunctionsButNotProcedures()
    {
        var simulation = Seeded(
            "create function f() returns varbinary(10) as begin return columns_updated() end",
            "create function itf() returns table as return select columns_updated() c",
            "create procedure p as insert l values (case when columns_updated() is null then 0 else 1 end)",
            "create trigger tr on t after update as begin insert l select cast(dbo.f() as int) + cast((select c from dbo.itf()) as int); exec p end",
            "insert t values (1, 2); update t set b = 3");
        CollectionAssert.AreEqual(new object[] { 4, 0 }, Extensions.FirstColumn(simulation.CreateOpenConnection(), "select a from l order by a desc"));
    }
}
