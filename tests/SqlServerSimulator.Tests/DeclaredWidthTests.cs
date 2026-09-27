using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The width a string or binary type takes where it is declared rather than
/// cast — variables, procedure and function parameters, function returns,
/// <c>sp_executesql</c> parameters, alias types — and the refusals each site
/// words its own way. Probed 2026-09-27 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DeclaredWidthTests
{
    [TestMethod]
    public void AProcedureParameterWithoutAWidth_IsOneWide()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p @x varchar, @c char as select @x + '|' + @c");
        AreEqual("a|b", sim.ExecuteScalar("exec p 'abcdef', 'bcd'"));
    }

    [TestMethod]
    public void AnSpExecuteSqlParameterWithoutAWidth_IsOneWide()
        => AreEqual("a", new Simulation().ExecuteScalar("exec sp_executesql N'select @p', N'@p varchar', 'abcdef'"));

    [TestMethod]
    [DataRow("create function f(@x varchar(2)) returns int as begin return datalength(@x) end", "select dbo.f('abcdef')", 2)]
    [DataRow("create function f(@x varchar) returns int as begin return datalength(@x) end", "select dbo.f('abcdef')", 1)]
    [DataRow("create function f() returns varchar(2) as begin return 'abcdef' end", "select datalength(dbo.f())", 2)]
    [DataRow("create function f() returns varchar as begin return 'abcdef' end", "select datalength(dbo.f())", 1)]
    [DataRow("create function f(@x varchar(2)) returns table as return select @x x", "select datalength(x) from dbo.f('abcdef')", 2)]
    [DataRow("create function f(@x varchar(2)) returns @t table (x varchar(10)) as begin insert @t values (@x) return end", "select datalength(x) from dbo.f('abcdef')", 2)]
    public void AFunctionsParameterOrReturn_IsCutToItsWidth(string create, string call, int length)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(create);
        AreEqual(length, sim.ExecuteScalar(call));
    }

    [TestMethod]
    [DataRow("declare @x int(5) = 1; select @x", 2716, "Column, parameter, or variable #1: Cannot specify a column width on data type int.")]
    [DataRow("declare @x varchar(9000)", 131, "The size (9000) given to the type 'varchar' exceeds the maximum allowed for any data type (8000).")]
    [DataRow("declare @x nvarchar(5000)", 2717, "The size (5000) given to the parameter '@x' exceeds the maximum allowed (4000).")]
    [DataRow("create procedure p @x nvarchar(5000) as select 1", 2717, "The size (5000) given to the parameter '@x' exceeds the maximum allowed (4000).")]
    [DataRow("create function f() returns nvarchar(5000) as begin return 'a' end", 2717, "The size (5000) given to the parameter '' exceeds the maximum allowed (4000).")]
    [DataRow("create type t from varchar(9000)", 131, "The size (9000) given to the type 'varchar' exceeds the maximum allowed for any data type (8000).")]
    [DataRow("create type t from nvarchar(5000)", 2717, "The size (5000) given to the parameter 'nvarchar' exceeds the maximum allowed (4000).")]
    [DataRow("exec sp_executesql N'select @p', N'@q int, @p int(4)', 1, 2", 2716, "Column, parameter, or variable #2: Cannot specify a column width on data type int.")]
    [DataRow("exec sp_executesql N'select @p', N'@p nvarchar(5000)', 'a'", 2717, "The size (5000) given to the parameter '@p' exceeds the maximum allowed (4000).")]
    public void AnInvalidDeclaredWidth_IsWordedForItsSite(string statement, int number, string message)
        => new Simulation().AssertSqlError(statement, number, message);
}
