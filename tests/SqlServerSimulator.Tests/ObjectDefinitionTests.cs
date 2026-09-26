using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>OBJECT_DEFINITION(object_id)</c>, <c>sys.sql_modules</c>, and
/// <c>INFORMATION_SCHEMA.ROUTINES.ROUTINE_DEFINITION</c> — the module
/// source-text introspection surface. Behavior probed against SQL Server 2025
/// (2026-05-27, the batch-text boundaries 2026-09-26): the stored definition
/// is the whole batch verbatim — leading comments and whitespace, trailing
/// semicolons and comments — with the leading verb normalized to CREATE for
/// ALTER / CREATE OR ALTER; NULL for non-modules and WITH ENCRYPTION.
/// </summary>
[TestClass]
public sealed class ObjectDefinitionTests
{
    private static object? Definition(Simulation sim, string twoPartName) =>
        sim.ExecuteScalar($"select object_definition(object_id('{twoPartName}'))");

    [TestMethod]
    public void Procedure_ReturnsVerbatimCreateText()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p1 @x int as select @x");
        AreEqual("create procedure dbo.p1 @x int as select @x", Definition(sim, "dbo.p1"));
    }

    [TestMethod]
    public void Procedure_PreservesCommentsAndSpacing()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create   proc dbo.p1 as /*body*/ select 1");
        AreEqual("create   proc dbo.p1 as /*body*/ select 1", Definition(sim, "dbo.p1"));
    }

    [TestMethod]
    [DataRow("  -- lead\n  create view dbo.v as select 1 a  ;  -- trail\n")]
    [DataRow("/* block */ create view dbo.v as select 1 a; ; ")]
    [DataRow(";create view dbo.v as select 1 a")]
    public void Definition_IsTheWholeBatch(string batch)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(batch);
        AreEqual(batch, Definition(sim, "dbo.v"));
    }

    [TestMethod]
    public void AlterAfterALeadingComment_NormalizesTheVerbInPlace()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v as select 1 a", " /*c*/ alter view dbo.v as select 2 a ; ");
        AreEqual(" /*c*/ CREATE view dbo.v as select 2 a ; ", Definition(sim, "dbo.v"));
    }

    [TestMethod]
    public void AlterProcedure_NormalizesLeadingVerbToCreate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p1 as select 1",
            "ALTER   PROCEDURE dbo.p1 as select 2");
        // ALTER text is stored with the leading verb rewritten to CREATE;
        // spacing after the verb is preserved (probe-confirmed).
        AreEqual("CREATE   PROCEDURE dbo.p1 as select 2", Definition(sim, "dbo.p1"));
    }

    [TestMethod]
    public void CreateOrAlterProcedure_CollapsesToCreate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("CREATE OR ALTER PROCEDURE dbo.p1 as select 1");
        // SQL Server removes the OR / ALTER keywords but keeps the surrounding
        // whitespace, so the canonical single-spaced form becomes 3 spaces.
        AreEqual("CREATE   PROCEDURE dbo.p1 as select 1", Definition(sim, "dbo.p1"));
    }

    [TestMethod]
    public void View_ReturnsCreateText()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v1 as select 1 as a");
        AreEqual("create view dbo.v1 as select 1 as a", Definition(sim, "dbo.v1"));
    }

    [TestMethod]
    public void ScalarFunction_IncludesTrailingEnd()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.fn1(@a int) returns int as begin return @a * 2 end");
        AreEqual("create function dbo.fn1(@a int) returns int as begin return @a * 2 end", Definition(sim, "dbo.fn1"));
    }

    [TestMethod]
    public void InlineTableValuedFunction_IncludesClosingParen()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.tvf1() returns table as return (select 1 as x)");
        AreEqual("create function dbo.tvf1() returns table as return (select 1 as x)", Definition(sim, "dbo.tvf1"));
    }

    [TestMethod]
    public void Trigger_ReturnsCreateText()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.trg1 on dbo.t after insert as select 1");
        AreEqual("create trigger dbo.trg1 on dbo.t after insert as select 1", Definition(sim, "dbo.trg1"));
    }

    private static void AssertNullDefinition(Simulation sim, string idExpr)
    {
        using var reader = sim.ExecuteReader($"select object_definition({idExpr})");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void NonModule_Table_ReturnsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (id int)");
        AssertNullDefinition(sim, "object_id('dbo.t')");
    }

    [TestMethod]
    public void NullId_ReturnsNull()
        => AssertNullDefinition(new Simulation(), "null");

    [TestMethod]
    public void MissingId_ReturnsNull()
        => AssertNullDefinition(new Simulation(), "123456");

    [TestMethod]
    public void SqlModules_RowPresentWithDefinitionAndFlagDefaults()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p1 as select 1");
        using var reader = sim.ExecuteReader("""
            select definition, uses_ansi_nulls, uses_quoted_identifier, is_schema_bound,
                   null_on_null_input, execute_as_principal_id
            from sys.sql_modules where object_id = object_id('dbo.p1')
            """);
        IsTrue(reader.Read());
        AreEqual("create procedure dbo.p1 as select 1", reader.GetString(0));
        IsTrue(reader.GetBoolean(1));  // uses_ansi_nulls
        IsTrue(reader.GetBoolean(2));  // uses_quoted_identifier
        IsFalse(reader.GetBoolean(3)); // is_schema_bound
        IsFalse(reader.GetBoolean(4)); // null_on_null_input
        IsTrue(reader.IsDBNull(5));    // execute_as_principal_id
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SqlModules_ScalarFunction_ReturnsNullOnNullInput_SetsFlag()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.fn1(@a int) returns int with returns null on null input as begin return @a end");
        IsTrue((bool)sim.ExecuteScalar("select null_on_null_input from sys.sql_modules where object_id = object_id('dbo.fn1')")!);
    }

    [TestMethod]
    public void RoutineDefinition_MatchesObjectDefinition()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p1 @x int as select @x");
        AreEqual("create procedure dbo.p1 @x int as select @x",
            sim.ExecuteScalar("select routine_definition from information_schema.routines where routine_name = 'p1'"));
    }
}
