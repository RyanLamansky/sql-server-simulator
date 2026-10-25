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

    /// <summary>
    /// A type's width, precision or scale is an <c>int</c> literal: a decimal
    /// one — a fraction, or a whole number past <c>int</c> — is Msg 1080
    /// echoing it where a column is declared and Msg 102 near it in any other
    /// type spec, and a float is Msg 102 everywhere (probed 2026-10-25 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select cast('a' as varchar({0}))", false)]
    [DataRow("select cast(1 as decimal(5, {0}))", false)]
    [DataRow("declare @v varchar({0})", false)]
    [DataRow("create procedure p @x varchar({0}) as select 1", false)]
    [DataRow("create function f(@x varchar({0})) returns int as begin return 1 end", false)]
    [DataRow("create type t from varchar({0})", false)]
    [DataRow("create sequence s as decimal({0}, 0)", false)]
    [DataRow("create partition function pf (varchar({0})) as range left for values ('a')", false)]
    [DataRow("exec sp_executesql N'select @p', N'@p varchar({0})', 'a'", false)]
    [DataRow("create table t (c varchar({0}))", true)]
    [DataRow("create table t (c decimal(5, {0}))", true)]
    [DataRow("create table t (i int); alter table t add c varchar({0})", true)]
    [DataRow("create table t (i int); alter table t alter column i varchar({0})", true)]
    [DataRow("declare @t table (c varchar({0}))", true)]
    [DataRow("create type t as table (c varchar({0}))", true)]
    [DataRow("create function f() returns @t table (c varchar({0})) as begin return end", true)]
    [DataRow("select * from openjson(N'[]') with (c varchar({0}))", true)]
    [DataRow("exec sp_executesql N'select 1 a' with result sets ((a varchar({0})))", true)]
    public void ANonIntegerWidth_IsWordedForItsSite(string statement, bool columnDefinition)
    {
        foreach (var literal in (ReadOnlySpan<string>)["1.5", "9999999999", "1e1"])
        {
            var number = columnDefinition && literal != "1e1" ? 1080 : 102;
            var ex = new Simulation().AssertSqlError(string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, literal), number);
            AreEqual(number == 1080 ? $"The integer value {literal} is out of range." : $"Incorrect syntax near '{literal}'.", ex.Errors[0].Message);
        }
    }

    /// <summary>
    /// Real reads a declaration's whole type spec — name, width list and the
    /// token that ends it — before it binds the name, so a malformed one is a
    /// syntax error ahead of a missing type, however many parts the name has
    /// and in a module as at top level (probed 2026-10-11 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("declare @a nosuch x", 102, "Incorrect syntax near 'x'.")]
    [DataRow("declare @a dbo.nosuch x", 102, "Incorrect syntax near 'x'.")]
    [DataRow("declare @a dbo.nosuch(1) x", 102, "Incorrect syntax near 'x'.")]
    [DataRow("declare @a dbo.nosuch(1 + 2)", 102, "Incorrect syntax near '+'.")]
    [DataRow("declare @a dbo.nosuch(1, 2, 3)", 102, "Incorrect syntax near ','.")]
    [DataRow("declare @a dbo.nosuch(max, 5)", 102, "Incorrect syntax near ','.")]
    [DataRow("declare @a dbo.nosuch()", 102, "Incorrect syntax near ')'.")]
    [DataRow("declare @a dbo.nosuch(abc)", 102, "Incorrect syntax near 'abc'.")]
    [DataRow("declare @a dbo.nosuch(-5)", 102, "Incorrect syntax near '-'.")]
    [DataRow("declare @a dbo.nosuch('x')", 102, "Incorrect syntax near 'x'.")]
    [DataRow("declare @a dbo.nosuch(1.5)", 102, "Incorrect syntax near '1.5'.")]
    [DataRow("declare @a dbo.nosuch((1))", 102, "Incorrect syntax near '('.")]
    [DataRow("declare @a xml(nosuchcol) x", 102, "Incorrect syntax near 'x'.")]
    [DataRow("declare @a x.dbo.nosuch(10)", 117, "The type name 'x.dbo.nosuch' contains more than the maximum number of prefixes. The maximum is 1.")]
    [DataRow("declare @a x.dbo.tt", 117, "The type name 'x.dbo.tt' contains more than the maximum number of prefixes. The maximum is 1.")]
    [DataRow("declare @a dbo.nosuch(10, 5), @b int = 1", 2715, "Column, parameter, or variable #1: Cannot find data type dbo.nosuch.")]
    [DataRow("declare @a dbo.nosuch(max) = 1, @b int = 1", 2715, "Column, parameter, or variable #1: Cannot find data type dbo.nosuch.")]
    [DataRow("declare @a dbo.tt(1)", 2716, "Column, parameter, or variable #1: Cannot specify a column width on data type dbo.tt.")]
    [DataRow("declare @a tt(max)", 2716, "Column, parameter, or variable #1: Cannot specify a column width on data type tt.")]
    [DataRow("declare @a dbo.tt(1, 2)", 192, "The scale must be less than or equal to the precision.")]
    [DataRow("declare @a dbo.tt(1.5)", 102, "Incorrect syntax near '1.5'.")]
    [DataRow("declare @a dbo.tt(1) x", 102, "Incorrect syntax near 'x'.")]
    [DataRow("create procedure p as begin declare @a nosuch x; select 1; end", 102, "Incorrect syntax near 'x'.")]
    [DataRow("create procedure p as begin declare @a dbo.nosuch x; select 1; end", 102, "Incorrect syntax near 'x'.")]
    [DataRow("create procedure p as begin declare @a dbo.nosuch(1 + (2)); select 1; end", 102, "Incorrect syntax near '+'.")]
    [DataRow("create procedure p as begin declare @a dbo.tt(1); select 1; end", 2716, "Column, parameter, or variable #1: Cannot specify a column width on data type dbo.tt.")]
    public void ATypeSpecReadBeforeItsNameBinds_RaisesItsSyntaxErrorFirst(string statement, int number, string message)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.tt as table (a int)");
        sim.AssertSqlError(statement, number, message);
    }
}
