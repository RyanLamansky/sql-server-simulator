using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// What real refuses in a module's parameter list and return type — a
/// <c>timestamp</c> where a function can't take one, <c>READONLY</c> on a
/// parameter that isn't table-valued, a type that doesn't resolve — and the
/// order those refusals take against the body's own errors (probed 2026-09-30
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ParameterDeclarationErrorTests
{
    [TestMethod]
    [DataRow("create function f(@p timestamp) returns int as begin return 1 end")]
    [DataRow("create function f(@p rowversion) returns int as begin return 1 end")]
    [DataRow("create function f(@a int, @p timestamp, @q timestamp) returns int as begin return 1 end")]
    [DataRow("create function f(@p timestamp = null) returns int as begin return 1 end")]
    [DataRow("create function f(@p timestamp) returns table as return select 1 x")]
    [DataRow("create function f(@p timestamp) returns @t table (x int) as begin return end")]
    [DataRow("create function f(@p timestamp) returns int as begin return (select a from nosuchtable) end")]
    [DataRow("create or alter function f(@p timestamp) returns int as begin return 1 end")]
    public void FunctionTimestampParameter_Msg2724(string sql)
    {
        var error = new Simulation().AssertSqlError(sql, 2724);
        AreEqual("Parameter or variable '@p' has an invalid data type.", error.Errors[0].Message);
        AreEqual(3, error.State);
    }

    /// <summary>The body binds first, so its errors outrank Msg 2724, which reports the statement's first line.</summary>
    [TestMethod]
    public void FunctionTimestampParameter_AfterTheBody()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create function f(@p timestamp) returns int as begin return (select nosuch from sys.objects) end", 207);
        AreEqual(1, simulation.AssertSqlError("create function f(\n@a int,\n@p timestamp)\nreturns @t table (x int)\nas begin return end", 2724).LineNumber);
    }

    /// <summary>An <c>ALTER</c> the refusal stops leaves the function as it was; a procedure takes the type.</summary>
    [TestMethod]
    public void AlterIsRefused_AndAProcedureTakesTheType()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create function f(@p int) returns int as begin return 7 end");
        _ = simulation.AssertSqlError("alter function f(@p timestamp) returns int as begin return 1 end", 2724);
        AreEqual(7, simulation.ExecuteScalar("select dbo.f(1)"));
        simulation.ExecuteBatches("create procedure p @p timestamp as select datalength(@p)");
        AreEqual(8, simulation.ExecuteScalar("exec p 0x0102"));
    }

    /// <summary>
    /// A <c>timestamp</c> return type is Msg 2733 at the line the statement
    /// ends on, ahead of the parameter list's own refusals and the body's.
    /// </summary>
    [TestMethod]
    [DataRow("create function f() returns timestamp as begin return 0x01 end")]
    [DataRow("create function f() returns rowversion as begin return 0x01 end")]
    [DataRow("create function f(@p int readonly) returns timestamp as begin return 0x01 end")]
    [DataRow("create function f(@p nosuch) returns timestamp as begin return 0x01 end")]
    [DataRow("create function f() returns timestamp as begin return (select nosuch from sys.objects) end")]
    public void TimestampReturnType_Msg2733(string sql)
        => new Simulation().AssertSqlError(sql, 2733, "The timestamp data type is invalid for return values.");

    [TestMethod]
    public void TimestampReturnType_ReportsTheLastLine()
        => AreEqual(7, new Simulation().AssertSqlError("create function f()\nreturns timestamp\nas\nbegin\ndeclare @x int = 1;\nreturn 0x01\nend", 2733).LineNumber);

    /// <summary>
    /// A <c>timestamp</c> column in a table a function declares is Msg 443 for
    /// the <c>TIMESTAMP</c> operator, behind the body's binder errors: the
    /// return table's at the return variable's line, and a body table
    /// variable's at its statement.
    /// </summary>
    [TestMethod]
    public void TimestampColumnInAFunctionTable_Msg443()
    {
        var simulation = new Simulation();
        var returnTable = simulation.AssertSqlError("create function f()\nreturns @t table (a int,\nx timestamp)\nas begin return end", 443);
        AreEqual("Invalid use of a side-effecting operator 'TIMESTAMP' within a function.", returnTable.Errors[0].Message);
        AreEqual(16, returnTable.State);
        AreEqual(2, returnTable.LineNumber);
        var withBody = simulation.AssertSqlError("create function f() returns @t table (x rowversion) as begin declare @v table (y timestamp); return end", 443);
        CollectionAssert.AreEqual(new[] { 443, 443 }, Numbers(withBody));
        AreEqual(4, simulation.AssertSqlError("create function f() returns int\nas\nbegin\ndeclare @v table (y int,\nz timestamp);\nreturn 1\nend", 443).LineNumber);
        var afterBody = simulation.AssertSqlError("create function f()\nreturns @t table (x int,\ny timestamp)\nas begin\ndeclare @y int = (select nosuch from sys.objects);\ninsert @t (x) values (1);\ndeclare @z int = (select nosuch2 from sys.objects);\nreturn end", 207);
        CollectionAssert.AreEqual(new[] { 207, 207, 443 }, Numbers(afterBody));
        CollectionAssert.AreEqual(new[] { 5, 7, 2 }, afterBody.Errors.Select(entry => entry.LineNumber).ToArray());
        simulation.ExecuteBatches("create procedure p as declare @v table (y int, z timestamp); select 1");
    }

    [TestMethod]
    [DataRow("create function f(@p int readonly) returns int as begin return 1 end")]
    [DataRow("create function f(@a int, @p varchar(10) readonly) returns int as begin return 1 end")]
    [DataRow("create function f(@p int = 1 readonly) returns int as begin return 1 end")]
    [DataRow("create function f(@p int readonly) returns table as return select 1 x")]
    [DataRow("create function f(@p int readonly) returns @t table (x int) as begin return end")]
    [DataRow("create function f(@p timestamp readonly) returns int as begin return 1 end")]
    [DataRow("create function f(@p int readonly) returns int as begin return (select nosuch from sys.objects) end")]
    [DataRow("create procedure p @p int readonly as select 1")]
    [DataRow("create procedure p @p int = 5 readonly as select nosuch from sys.objects")]
    [DataRow("exec sp_executesql N'select 1', N'@p int readonly', 1")]
    public void ReadOnlyScalarParameter_Msg346(string sql)
        => new Simulation().AssertSqlError(sql, 346, "The parameter \"@p\" can not be declared READONLY since it is not a table-valued parameter.");

    /// <summary>
    /// Every parameter's refusal is reported in order, each at its parameter's
    /// line, after the whole statement parsed — so a syntax error in the body
    /// outranks them, and <c>READONLY</c> closes the declaration.
    /// </summary>
    [TestMethod]
    public void ReadOnlyScalarParameter_Order()
    {
        var simulation = new Simulation();
        CollectionAssert.AreEqual(new[] { 346, 346 }, Numbers(simulation.AssertSqlError("create function f(@a int readonly, @p int readonly) returns int as begin return 1 end", 346)));
        CollectionAssert.AreEqual(new[] { 346, 346 }, Numbers(simulation.AssertSqlError("exec sp_executesql N'select 1', N'@a int readonly, @b int readonly', 1, 2", 346)));
        CollectionAssert.AreEqual(new[] { 346, 2715, 2724 }, Numbers(simulation.AssertSqlError("create procedure p @a int readonly, @b nosuch as select 1", 346)));
        AreEqual(3, simulation.AssertSqlError("create function f(\n@a int,\n@p\nint\nreadonly\n) returns int as begin return 1 end", 346).LineNumber);
        AreEqual(3, simulation.AssertSqlError("create procedure p\n@a int,\n@b\nvarchar(10)\n=\n'x'\nreadonly\nas select 1", 346).LineNumber);
        AreEqual(2, simulation.AssertSqlError("exec sp_executesql N'select 1', N'@a int,\n@b int readonly', 1, 2", 346).LineNumber);
        simulation.ValidateSyntaxError("create procedure p @a nosuch as select 1 +;", ";");
        simulation.ValidateSyntaxError("create function f(@a int readonly) returns int as begin return 1 x end", "x");
        simulation.ValidateSyntaxError("create procedure p @p int readonly output as select 1", "output");
    }

    /// <summary>
    /// A variable's or parameter's type that doesn't resolve is Msg 2715 state
    /// 3, followed by an informational Msg 2724 naming it — which a client
    /// reads after the errors, as SqlClient collects them.
    /// </summary>
    [TestMethod]
    [DataRow("declare @p nosuch", "#1: Cannot find data type nosuch.")]
    [DataRow("create procedure p @a int, @p dbo.nosuch as select 1", "#2: Cannot find data type dbo.nosuch.")]
    [DataRow("create function f(@p nosuch) returns table as return select 1 x", "#1: Cannot find data type nosuch.")]
    [DataRow("exec sp_executesql N'select 1', N'@p nosuch', 1", "#1: Cannot find data type nosuch.")]
    public void UnknownParameterType_Msg2715State3WithMsg2724(string sql, string cannotFind)
    {
        var error = new Simulation().AssertSqlError(sql, 2715);
        AreEqual(3, error.State);
        AreEqual("Column, parameter, or variable " + cannotFind, error.Errors[0].Message);
        AreEqual(2724, error.Errors[^1].Number);
        AreEqual(0, error.Errors[^1].Class);
        AreEqual(2, error.Errors[^1].State);
        AreEqual("Parameter or variable '@p' has an invalid data type.", error.Errors[^1].Message);
    }

    [TestMethod]
    public void UnknownParameterTypes_AllReported()
    {
        var error = new Simulation().AssertSqlError("create procedure p @a nosuch, @b nosuch2 as select 1", 2715);
        CollectionAssert.AreEqual(new[] { 2715, 2715, 2724, 2724 }, Numbers(error));
        var columnError = new Simulation().AssertSqlError("create table t (a int, b nosuch)", 2715);
        AreEqual(6, columnError.State);
        HasCount(1, columnError.Errors);
    }
    /// <summary>
    /// A parameter default that can't be assigned to its type refuses the
    /// <c>CREATE</c> or <c>ALTER</c> ahead of everything else the statement
    /// carries — Msg 206 state 2, or Msg 257 state 3 for a conversion real
    /// makes only explicitly — the first one alone, for every function kind
    /// and a procedure; a parameter whose type is missing reports that instead
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create function f(@p date = 1) returns int as begin return 1 end", 206, "Operand type clash: int is incompatible with date")]
    [DataRow("create function f(@p int = 1, @q date = 1) returns int as begin return nosuch end", 206, "Operand type clash: int is incompatible with date")]
    [DataRow("create function f(@p date = 1, @q timestamp) returns int as begin return 1 end", 206, "Operand type clash: int is incompatible with date")]
    [DataRow("create function f(@p xml = 1, @q date = 1) returns table as return select 1 a", 206, "Operand type clash: int is incompatible with xml")]
    [DataRow("create function f(@p int = 'a', @q date = 1.5) returns @r table (a int) as begin return end", 206, "Operand type clash: numeric is incompatible with date")]
    [DataRow("create function f(@p timestamp = 'abc') returns int as begin return 1 end", 257, "Implicit conversion from data type varchar to timestamp is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("create procedure p @p date = 1, @q xml = 1 as select 1", 206, "Operand type clash: int is incompatible with date")]
    [DataRow("create procedure p @p timestamp = 'x' as select 1", 257, "Implicit conversion from data type varchar to timestamp is not allowed. Use the CONVERT function to run this query.")]
    public void UnassignableDefault_RefusesTheModule(string sql, int number, string message)
    {
        var error = new Simulation().AssertSqlError(sql, number);
        HasCount(1, error.Errors);
        AreEqual(message, error.Errors[0].Message);
        AreEqual(number == 206 ? 2 : 3, error.State);
        AreEqual(sql.Contains("procedure", StringComparison.Ordinal) ? "p" : "f", error.Procedure);
    }

    [TestMethod]
    public void UnassignableDefault_YieldsToAMissingTypeAndASyntaxError()
    {
        var simulation = new Simulation();
        CollectionAssert.AreEqual(new[] { 2715, 2724 }, Numbers(simulation.AssertSqlError("create function f(@q nosuchtype, @p date = 1) returns int as begin return 1 end", 2715)));
        simulation.AssertSqlError("create function f(@p date = 1) returns int as begin return 1 + end", 156, "Incorrect syntax near the keyword 'end'.");
        simulation.ExecuteBatches(
            "create function f(@p int = 'abc', @q date = null, @r varbinary(10) = 1.5) returns int as begin return 1 end",
            "create function g(@p int) returns int as begin return 1 end");
        _ = simulation.AssertSqlError("alter function g(@p date = 1) returns int as begin return 1 end", 206);
    }

    /// <summary>
    /// A procedure default that fails to convert as the call binds is the
    /// call's error, at line 0 under the procedure, as an argument's is.
    /// </summary>
    [TestMethod]
    public void ProcedureDefaultFailingToConvert_IsTheCallsError()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create procedure p @p int = 'abc' as select @p");
        var error = simulation.AssertSqlError("\nexec p", 245);
        AreEqual(0, error.LineNumber);
        AreEqual("p", error.Procedure);
    }

    /// <summary>
    /// A <c>DECLARE</c>'s missing type is a parse-phase error on real: the
    /// batch or module body reports every one of them, and a table variable
    /// used without a declaration (Msg 1087), but none of its binder errors.
    /// </summary>
    [TestMethod]
    [DataRow("declare @a nosuch; select nosuchcol from sys.objects", new[] { 2715, 2724 })]
    [DataRow("select nosuchcol from sys.objects; declare @a nosuch", new[] { 2715, 2724 })]
    [DataRow("declare @a nosuch, @b nosuch2; select nosuchcol from sys.objects; select * from nosuchtable", new[] { 2715, 2715, 2724, 2724 })]
    [DataRow("declare @a dbo.nosuchtt; insert @a values (5); select nosuchcol from sys.objects", new[] { 2715, 1087, 2724 })]
    [DataRow("declare @t table (a nosuch); select nosuchcol from sys.objects", new[] { 2715 })]
    [DataRow("create procedure p as declare @a nosuch; select nosuchcol from sys.objects", new[] { 2715, 2724 })]
    public void DeclaredMissingType_SilencesTheBinder(string sql, int[] numbers)
        => CollectionAssert.AreEqual(numbers, Numbers(new Simulation().AssertSqlError(sql, 2715)));

    /// <summary>A syntax error and an undeclared variable still outrank it.</summary>
    [TestMethod]
    public void DeclaredMissingType_YieldsToTheParser()
    {
        var simulation = new Simulation();
        simulation.ValidateSyntaxError("declare @a nosuch; select 1 +", "+");
        _ = simulation.AssertSqlError("declare @a nosuch; select @undeclared", 137);
    }
}
