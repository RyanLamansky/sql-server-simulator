using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How a function is called and what a body sees of its nesting, and the
/// value form of <c>RETURN</c> outside one (probed 2026-10-06 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class FunctionCallShapeTests
{
    [TestMethod]
    [DataRow("exec dbo.f")]
    [DataRow("exec dbo.g")]
    [DataRow("declare @x int; exec @x = dbo.f")]
    public void ExecOfATableValuedFunction_IsMsg2809(string sql)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create function f() returns table as return select 1 a",
            "create function g() returns @r table (a int) as begin return; end");
        var name = sql.Contains(".g", StringComparison.Ordinal) ? "g" : "f";
        simulation.AssertSqlError(sql, 2809, $"The request for procedure '{name}' failed because '{name}' is a table valued function object.");
    }

    /// <summary>
    /// A function refused for an OUTPUT parameter goes on to refuse each
    /// valued RETURN of its body, read as a batch's.
    /// </summary>
    [TestMethod]
    [DataRow("create function f(@a int output) returns int as begin return 1; end", 1)]
    [DataRow("create function f(@a int output) returns int as begin if 1=1 return 1; return 2; end", 2)]
    [DataRow("create function f(@a int output) returns table as return select 1 a", 0)]
    public void OutputParameter_IsFollowedByMsg178PerValuedReturn(string sql, int returns)
    {
        var ex = new Simulation().AssertSqlError(sql, 181);
        CollectionAssert.AreEqual(new[] { 181 }.Concat(Enumerable.Repeat(178, returns)).ToArray(), ex.Errors.Select(error => error.Number).ToArray());
    }

    /// <summary>A valued RETURN outside a module parses its value before the refusal, so the value's own parse errors win.</summary>
    [TestMethod]
    [DataRow("return @nosuch", 137)]
    [DataRow("return 1 +;", 102)]
    [DataRow("return nosuch(1)", 195)]
    [DataRow("return (select nosuch from sys.objects)", 178)]
    [DataRow("return 1/0", 178)]
    public void ValuedReturnOutsideAModule(string sql, int number)
    {
        var ex = new Simulation().AssertSqlError(sql, number);
        HasCount(1, ex.Errors);
    }

    /// <summary>A view or inline function inlines into its caller, so a body reads the caller's <c>@@NESTLEVEL</c>.</summary>
    [TestMethod]
    public void NestLevel_InAViewOrInlineFunction_IsTheCallers()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create view v as select @@nestlevel n",
            "create function itf() returns table as return select @@nestlevel n",
            "create function mtf() returns @r table (n int) as begin insert @r values (@@nestlevel); return; end",
            "create procedure p as select (select n from v) + (select n from dbo.itf()) * 10 + (select n from dbo.mtf()) * 100");
        AreEqual(0, simulation.ExecuteScalar("select n from v"));
        AreEqual(0, simulation.ExecuteScalar("select n from dbo.itf()"));
        AreEqual(1, simulation.ExecuteScalar("select n from dbo.mtf()"));
        AreEqual(211, simulation.ExecuteScalar("exec p"));
    }
}
