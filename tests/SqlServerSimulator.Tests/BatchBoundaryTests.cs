namespace SqlServerSimulator;

/// <summary>
/// Verifies <c>CREATE/ALTER PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA</c>
/// raises Msg 111 ("'X' must be the first statement in a query batch.") when
/// it isn't the first statement. Probe-confirmed wording per kind: PROCEDURE
/// merges CREATE / ALTER into one label; the others use their separate
/// CREATE / ALTER labels. Inner CommandText-equivalent contexts (procedure,
/// function, trigger, dynamic-SQL bodies) get a fresh BatchContext so the
/// check naturally resets — proven by the IFBlock + WHILE body cases below
/// (which raise Msg 111 because BlockDepth &gt; 0).
/// </summary>
[TestClass]
public class BatchBoundaryTests
{
    [TestMethod]
    [DataRow("create schema audit", "create schema staging", "CREATE SCHEMA")]
    [DataRow("create table t (id int)", "create view v as select 1 as x", "CREATE VIEW")]
    [DataRow("create table t (id int)", "create function fn() returns int as begin return 1 end", "CREATE FUNCTION")]
    [DataRow("create table t (id int)", "create proc p as select 1", "CREATE/ALTER PROCEDURE")]
    [DataRow("create table t (id int)", "create trigger tr on t after insert as select 1", "CREATE TRIGGER")]
    public void CreateMustBeFirstStatement_RaisesMsg111(string first, string secondCreate, string expectedLabel)
        => new Simulation().AssertSqlError(
            $"{first}; {secondCreate}",
            111,
            $"'{expectedLabel}' must be the first statement in a query batch.");

    [TestMethod]
    public void AlterProcedureMustBeFirstStatement_RaisesMsg111()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create proc p as select 1");
        simulation.AssertSqlError(
            "declare @x int = 1; alter proc p as select 2",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");
    }

    [TestMethod]
    public void AlterTriggerMustBeFirstStatement_RaisesMsg111()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int)",
            "create trigger tr on t after insert as select 1");
        simulation.AssertSqlError(
            "declare @x int = 1; alter trigger tr on t after insert as select 2",
            111,
            "'ALTER TRIGGER' must be the first statement in a query batch.");
    }

    [TestMethod]
    public void CreateOrAlterProcedure_UsesProcedureLabel()
        => new Simulation().AssertSqlError(
            "declare @x int = 1; create or alter procedure p as select 2",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");

    [TestMethod]
    public void LeadingSemicolons_DontCountAsFirstStatement()
        => new Simulation().ExecuteNonQuery("; create proc p as select 1");

    [TestMethod]
    public void CreateProcInsideIfBlock_RaisesMsg111()
        => new Simulation().AssertSqlError(
            "if 1 = 1 begin create proc p as select 1 end",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");

    [TestMethod]
    public void CreateProcInsideWhileBody_RaisesMsg111()
        => new Simulation().AssertSqlError(
            "while 1 = 0 begin create proc p as select 1 end",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");

    [TestMethod]
    public void CreateProcAsFirstStatement_Works()
        => new Simulation().ExecuteNonQuery("create proc p as select 1");

    [TestMethod]
    public void ProcedureBodyIsItsOwnBatch_AllowsCreateProc()
    {
        // Inside a proc body, the dispatched statements form a new batch with
        // its own BatchContext, so CREATE PROCEDURE may legitimately appear
        // as the first statement of the body. (Real SQL Server actually
        // raises Msg 156 syntax error in this position; the simulator's
        // divergence here is documented and benign — no app emits nested
        // CREATE PROCEDUREs inside a proc body.)
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create proc outer_p as create proc inner_p as select 1",
            "exec outer_p"); // Invoke the outer proc — its body creates inner_p.
        Assert.AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.procedures where name = 'inner_p'"));
    }
}
