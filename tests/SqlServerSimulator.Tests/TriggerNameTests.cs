using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which names a <c>CREATE TRIGGER</c> clashes with (probed 2026-09-28 against
/// SQL Server 2025). See <c>docs/claude/triggers.md</c>.
/// </summary>
[TestClass]
public sealed class TriggerNameTests
{
    private static Simulation With(params string[] batches)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(batches);
        return simulation;
    }

    [TestMethod]
    [DataRow("create procedure x as select 1")]
    [DataRow("create table x (a int)")]
    [DataRow("create trigger x on u after insert as select 1")]
    public void DmlTriggerOverTakenName(string holder)
    {
        var ex = With("create table t (a int); create table u (a int)", holder)
            .AssertSqlError("create trigger x on t after insert as select 1", 2714);
        AreEqual((2, "x"), (ex.State, ex.Procedure));
    }

    /// <summary>A database-scope DDL trigger's name lives in a namespace of its own.</summary>
    [TestMethod]
    [DataRow("create procedure x as select 1", "create trigger x on database for create_table as select 1")]
    [DataRow("create table t (a int); create table x (a int)", "create trigger x on database for create_table as select 1")]
    [DataRow("create table t (a int)", "create trigger x on t after insert as select 1", "create trigger x on database for create_table as select 1")]
    [DataRow("create trigger x on database for create_table as select 1", "create table x (a int)")]
    public void DdlTriggerNamesDoNotClashWithSchemaObjects(params string[] batches)
        => new Simulation().ExecuteBatches(batches);

    [TestMethod]
    public void SecondDdlTriggerOfTheSameNameClashes()
        => AreEqual(2, With("create trigger x on database for create_table as select 1")
            .AssertSqlError("create trigger x on database for drop_table as select 1", 2714).State);

    [TestMethod]
    public void AlterOfAMissingDdlTrigger()
        => AreEqual(6, With("create procedure x as select 1")
            .AssertSqlError("alter trigger x on database for create_table as select 1", 208).State);
}
