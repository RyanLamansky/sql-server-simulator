using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The canonical form SQL Server stores for a CHECK, DEFAULT or computed
/// column's <c>definition</c>, whatever was written: bracketed names,
/// parenthesized numeric literals, unspaced operators, <c>IN</c> and
/// <c>BETWEEN</c> desugared, <c>CAST</c> / <c>IIF</c> rewritten, and real's own
/// parenthesization in place of the written one. Every expectation probed
/// 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DefinitionCanonicalFormTests
{
    private const string Columns = "a int, b varchar(10), c int, d datetime, e nvarchar(20), f float, dto datetimeoffset";

    [TestMethod]
    [DataRow("a+b-c", "(([a]+[b])-[c])")]
    [DataRow("a-(b+c)", "([a]-([b]+[c]))")]
    [DataRow("(a*b)+c", "([a]*[b]+[c])")]
    [DataRow("a*(b+c)", "([a]*([b]+[c]))")]
    [DataRow("a*c/2", "(([a]*[c])/(2))")]
    [DataRow("a & c | 2 ^ 3", "((([a]&[c])|(2))^(3))")]
    [DataRow("-a*c", "( -([a]*[c]))")]
    [DataRow("-a+c", "(( -[a])+[c])")]
    [DataRow("- -a", "( -( -[a]))")]
    [DataRow("~a*c", "(~[a]*[c])")]
    [DataRow("~(a & 1)", "(~([a]&(1)))")]
    [DataRow("a/-c", "([a]/( -[c]))")]
    [DataRow("a*-1", "([a]*(-1))")]
    [DataRow("+a", "([a])")]
    [DataRow("((a))", "([a])")]
    [DataRow("a << 1", "(left_shift([a],(1)))")]
    public void Arithmetic_TakesRealsParentheses(string expression, string expected)
        => AreEqual(expected, ComputedDefinition(expression));

    [TestMethod]
    [DataRow(".5", "((0.5))")]
    [DataRow("1.", "((1.))")]
    [DataRow("0002", "((2))")]
    [DataRow("- 1", "((-1))")]
    [DataRow("-(-1)", "((1))")]
    [DataRow("2147483648", "((2147483648.))")]
    [DataRow("-2147483648", "((-2147483648))")]
    [DataRow("1.5e-3", "((1.5000000000000000e-003))")]
    [DataRow("$1.123456", "(($1.1235))")]
    [DataRow("-$5", "(($-5.0000))")]
    [DataRow("0x0a", "(0x0A)")]
    [DataRow("N'x' + 'it''s'", "(N'x'+'it''s')")]
    [DataRow("isnull(null, a)", "(isnull(NULL,[a]))")]
    public void Literals_RenderInTheirStoredForm(string expression, string expected)
        => AreEqual(expected, ComputedDefinition(expression));

    [TestMethod]
    [DataRow("cast(a as varchar)", "(CONVERT([varchar],[a]))")]
    [DataRow("cast(a as dec(5, 2))", "(CONVERT([decimal](5,2),[a]))")]
    [DataRow("cast(a as national char varying(5))", "(CONVERT([nvarchar](5),[a]))")]
    [DataRow("cast(a as double precision)", "(CONVERT([float],[a]))")]
    [DataRow("cast(a as rowversion)", "(CONVERT([timestamp],[a]))")]
    [DataRow("cast(a as nvarchar(max))", "(CONVERT([nvarchar](max),[a]))")]
    [DataRow("convert(varchar(10), d, 120)", "(CONVERT([varchar](10),[d],(120)))")]
    [DataRow("try_convert(int, b)", "(TRY_CAST([b] AS [int]))")]
    [DataRow("try_convert(varchar(5), a, 1)", "(TRY_CONVERT([varchar](5),[a],(1)))")]
    [DataRow("try_cast(b as decimal(4,1))", "(TRY_CAST([b] AS [decimal](4,1)))")]
    [DataRow("parse(b as int)", "(parse([b]  AS int))")]
    [DataRow("e collate Latin1_General_CS_AS", "(([e]) collate Latin1_General_CS_AS)")]
    [DataRow("dto at time zone 'UTC'", "(([dto] AT TIME ZONE 'UTC'))")]
    public void Conversions_RenderAsConvert(string expression, string expected)
        => AreEqual(expected, ComputedDefinition(expression));

    [TestMethod]
    [DataRow("UPPER(b) + Lower(e)", "(upper([b])+lower([e]))")]
    [DataRow("dateadd(dd, 1, d)", "(dateadd(day,(1),[d]))")]
    [DataRow("datepart(isowk, d)", "(datepart(iso_week,[d]))")]
    [DataRow("year(d)", "(datepart(year,[d]))")]
    [DataRow("current_timestamp", "(getdate())")]
    [DataRow("current_user", "(user_name())")]
    [DataRow("system_user", "(suser_sname())")]
    [DataRow("@@SPID", "(@@spid)")]
    [DataRow("trim('x' from b)", "(Trim('x' FROM [b]))")]
    [DataRow("compress(b)", "(Compress([b]))")]
    [DataRow("date_bucket(week, 1, d)", "(Date_Bucket(week,(1),[d]))")]
    [DataRow("json_object('a': b)", "(json_object('a':[b]))")]
    [DataRow("iif(a > 1, 1, 0)", "(case when [a]>(1) then (1) else (0) end)")]
    [DataRow("case a when 1 then 'x' end", "(case [a] when (1) then 'x'  end)")]
    [DataRow("case when a in (1, 2) then 1 else 0 end", "(case when [a]=(2) OR [a]=(1) then (1) else (0) end)")]
    [DataRow("dbo.f(a)", "([dbo].[f]([a]))")]
    public void Calls_TakeRealsNames(string expression, string expected)
        => AreEqual(expected, ComputedDefinition(expression));

    [TestMethod]
    [DataRow("a=1 or b='x' and c=2", "([a]=(1) OR [b]='x' AND [c]=(2))")]
    [DataRow("(a=1 or b='x') and c=2", "(([a]=(1) OR [b]='x') AND [c]=(2))")]
    [DataRow("(a=1 and b='x') and c=2", "([a]=(1) AND [b]='x' AND [c]=(2))")]
    [DataRow("a=1 and (b='x' and c=2)", "([a]=(1) AND ([b]='x' AND [c]=(2)))")]
    [DataRow("not (a=1 or b='x')", "(NOT ([a]=(1) OR [b]='x'))")]
    [DataRow("not (not a=1)", "(NOT NOT [a]=(1))")]
    [DataRow("a in (5, 3, 1)", "([a]=(1) OR [a]=(3) OR [a]=(5))")]
    [DataRow("a not in (1, 2)", "(NOT ([a]=(2) OR [a]=(1)))")]
    [DataRow("a in (null, 1)", "([a]=(1) OR [a]=NULL)")]
    [DataRow("c=1 or a in (1, 2)", "([c]=(1) OR ([a]=(2) OR [a]=(1)))")]
    [DataRow("a between 1 and 5 and c=1", "([a]>=(1) AND [a]<=(5) AND [c]=(1))")]
    [DataRow("c=1 and a between 1 and 5", "([c]=(1) AND ([a]>=(1) AND [a]<=(5)))")]
    [DataRow("a not between c and 5", "(NOT ([a]>=[c] AND [a]<=(5)))")]
    [DataRow("a !> 1 and a !< 0 and a != 3", "([a]<=(1) AND [a]>=(0) AND [a]<>(3))")]
    [DataRow("a + 1 > c * 2", "(([a]+(1))>[c]*(2))")]
    [DataRow("-a = 1", "(( -[a])=(1))")]
    [DataRow("a + 1 is null", "(([a]+(1)) IS NULL)")]
    [DataRow("b not like 'x%'", "(NOT [b] like 'x%')")]
    [DataRow("b like 'y%' escape '\\'", "([b] like 'y%' escape '\\' )")]
    [DataRow("b like '%' + e + '%'", "([b] like ('%'+[e])+'%')")]
    [DataRow("b = 'x' collate Latin1_General_BIN", "([b]=('x') collate Latin1_General_BIN)")]
    [DataRow("case when a > 1 then 1 else 0 end = 1", "(case when [a]>(1) then (1) else (0) end=(1))")]
    public void CheckPredicates_TakeRealsShape(string predicate, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t ({Columns}, constraint ck check ({predicate}));
            select definition from sys.check_constraints where name = 'ck'
            """));

    [TestMethod]
    [DataRow("default 0", "((0))")]
    [DataRow("default ((2))", "((2))")]
    [DataRow("default 2 * 3 + 1", "((2)*(3)+(1))")]
    [DataRow("default abs(-3)", "(abs((-3)))")]
    [DataRow("default null", "(NULL)")]
    [DataRow("default 1e2", "((1.0000000000000000e+002))")]
    public void Defaults_TakeTheCanonicalForm(string clause, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t (a int {clause});
            select definition from sys.default_constraints
            """));

    [TestMethod]
    public void AnUnrenderedShape_KeepsItsSourceText()
        => AreEqual("({fn ucase(b)})", ComputedDefinition("{fn ucase(b)}"));

    private static object? ComputedDefinition(string expression)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create function dbo.f (@a int) returns int as begin return @a end",
            $"create table t ({Columns}, k as {expression})");
        return simulation.ExecuteScalar("select definition from sys.computed_columns where name = 'k'");
    }
}
