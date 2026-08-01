using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>sys.sql_modules.inline_type</c> / <c>is_inlineable</c> — the
/// scalar-UDF-inlining pair. A plain scalar function and an inline TVF both
/// report 1 / 1; a procedure, view, trigger and multi-statement TVF report
/// 0 / 0; and the probed disqualifier set drops a scalar function to 0 / 0.
/// Neither column is compatibility-level gated (probe-confirmed: a function
/// created at level 140 still reports 1 / 1). Probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ModuleInliningTests
{
    /// <summary>
    /// The <c>inline_type</c> / <c>is_inlineable</c> pair rendered as
    /// <c>"1|1"</c> — the two always agree here, since the one construct that
    /// parts them (<c>WITH INLINE = OFF</c>) isn't accepted by the grammar.
    /// </summary>
    private static string InlineFlags(Simulation simulation, string objectName)
        => (string)simulation.ExecuteScalar($"""
            select cast(cast(inline_type as int) as varchar(1)) + '|' + cast(cast(is_inlineable as int) as varchar(1))
            from sys.sql_modules where object_id = object_id('{objectName}')
            """)!;

    private static Simulation WithFunction(string body)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table dbo.t (id int, v int)", body);
        return simulation;
    }

    // ----- The module kinds -----

    [TestMethod]
    public void PlainScalarFunction_IsInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin return 1 end"), "dbo.f"));

    [TestMethod]
    public void InlineTableValuedFunction_IsInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns table as return select 1 as a"), "dbo.f"));

    [TestMethod]
    public void MultiStatementTableValuedFunction_IsNot()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns @r table (a int) as begin insert @r values (1); return end"), "dbo.f"));

    [TestMethod]
    public void Procedure_IsNot()
        => AreEqual("0|0", InlineFlags(WithFunction("create procedure dbo.p as select 1"), "dbo.p"));

    [TestMethod]
    public void View_IsNot()
        => AreEqual("0|0", InlineFlags(WithFunction("create view dbo.v as select 1 as a"), "dbo.v"));

    [TestMethod]
    public void Trigger_IsNot()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create trigger dbo.tr on dbo.t after insert as select 1"), "dbo.tr"));

    // ----- Probed inlineable despite looking otherwise -----

    [TestMethod]
    public void SchemaBound_StaysInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int with schemabinding as begin return 1 end"), "dbo.f"));

    [TestMethod]
    public void ReadingATable_StaysInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin return (select count(*) from dbo.t) end"), "dbo.f"));

    [TestMethod]
    public void IfElseWithOneReturn_StaysInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f(@x int) returns int as begin declare @r int; if @x > 0 set @r = 1 else set @r = 0; return @r end"),
            "dbo.f"));

    [TestMethod]
    public void PlainSelectAssignmentWithFrom_StaysInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin declare @s int; select @s = id from dbo.t; return @s end"),
            "dbo.f"));

    [TestMethod]
    public void SelfReferenceWithoutFrom_StaysInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin declare @s int = 0; select @s = @s + 1; return @s end"),
            "dbo.f"));

    [TestMethod]
    public void SessionAndMetadataScalars_StayInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin return @@spid + user_id() end"), "dbo.f"));

    [TestMethod]
    public void ExecuteAsCaller_StaysInlineable()
        => AreEqual("1|1", InlineFlags(
            WithFunction("create function dbo.f() returns int with execute as caller as begin return 1 end"), "dbo.f"));

    // ----- The probed disqualifiers -----

    [TestMethod]
    public void TimeDependentIntrinsic_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns datetime as begin return getdate() end"), "dbo.f"));

    [TestMethod]
    public void CurrentTimestamp_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns datetime as begin return current_timestamp end"), "dbo.f"));

    [TestMethod]
    public void RowCount_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin return @@rowcount end"), "dbo.f"));

    [TestMethod]
    public void TwoReturnStatements_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f(@x int) returns int as begin if @x > 0 return 1 return 0 end"), "dbo.f"));

    [TestMethod]
    public void WhileLoop_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin declare @i int = 0; while @i < 3 set @i = @i + 1; return @i end"),
            "dbo.f"));

    [TestMethod]
    public void TableVariable_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin declare @tv table (a int); insert @tv values (1); return (select count(*) from @tv) end"),
            "dbo.f"));

    [TestMethod]
    public void Recursion_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f(@x int) returns int as begin return case when @x <= 0 then 0 else dbo.f(@x - 1) end end"),
            "dbo.f"));

    [TestMethod]
    public void ExecuteAsOwner_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns int with execute as owner as begin return 1 end"), "dbo.f"));

    [TestMethod]
    public void XmlDataTypeMethod_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f(@d xml) returns int as begin return @d.value('(/a)[1]', 'int') end"), "dbo.f"));

    [TestMethod]
    public void VariableAccumulationOverATable_IsNotInlineable()
        => AreEqual("0|0", InlineFlags(
            WithFunction("create function dbo.f() returns int as begin declare @s int = 0; select @s = @s + id from dbo.t; return @s end"),
            "dbo.f"));

    // ----- Not compatibility-level gated -----

    [TestMethod]
    public void LoweringCompatibilityLevel_DoesNotChangeTheAnswer()
    {
        var simulation = WithFunction("create function dbo.f() returns int as begin return 1 end");
        _ = simulation.ExecuteNonQuery("alter database simulated set compatibility_level = 140");
        AreEqual("1|1", InlineFlags(simulation, "dbo.f"));
    }

    /// <summary>
    /// Inlineability isn't transitive: calling a function that can't be
    /// inlined leaves the caller inlineable (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void CallingANonInlineableFunction_StaysInlineable()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create function dbo.inner_fn() returns datetime as begin return getdate() end",
            "create function dbo.outer_fn() returns int as begin return datepart(year, dbo.inner_fn()) end");
        AreEqual("0|0", InlineFlags(simulation, "dbo.inner_fn"));
        AreEqual("1|1", InlineFlags(simulation, "dbo.outer_fn"));
    }
}
