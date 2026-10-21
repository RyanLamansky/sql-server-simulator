using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real parses a statement before it binds any of it, so a syntax error
/// reports ahead of the binding errors the same statement holds, and a few
/// names bind as real's grammar reads them (probed 2026-10-21 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class SyntaxOutranksBindingTests
{
    private const string Fixture = "create table zt (id int primary key, a int); create table zu (id int primary key, a int);";

    [TestMethod]
    [DataRow("select 1 from zt where id 1", 102, "Incorrect syntax near '1'.")]
    [DataRow("select 1 from zt where id 'x'", 102, "Incorrect syntax near 'x'.")]
    [DataRow("select 1 from zt where id [zz]", 102, "Incorrect syntax near 'zz'.")]
    [DataRow("declare @v int; select 1 from zt where id @v", 102, "Incorrect syntax near '@v'.")]
    [DataRow("select 1 from zt where id exists", 156, "Incorrect syntax near the keyword 'exists'.")]
    [DataRow("select 1 from zt where id null", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("select 1 from zt t join zu u on t.id 1", 102, "Incorrect syntax near '1'.")]
    [DataRow("if 1 'x' print 'x'", 102, "Incorrect syntax near 'x'.")]
    [DataRow("while 1 1 break", 102, "Incorrect syntax near '1'.")]
    [DataRow("select 1 from zt where id zz", 4145, "An expression of non-boolean type specified in a context where a condition is expected, near 'zz'.")]
    [DataRow("select 1 from zt where id begin", 4145, "An expression of non-boolean type specified in a context where a condition is expected, near 'begin'.")]
    public void AValueAfterANonBooleanCondition_IsTheSyntaxErrorAtIt(string statement, int number, string message)
        => new Simulation().AssertSqlError(Fixture + statement, number, message);

    [TestMethod]
    [DataRow("select zt..a from zt")]
    [DataRow("select t...a from zt t")]
    [DataRow("select 1 from zt t join zu u on t..id = u.id")]
    [DataRow("select a from zt group by zt..a")]
    [DataRow("update zt set a = 1 where zt..id = 1")]
    [DataRow("delete zt output deleted..id where a = 1")]
    [DataRow("insert into zt (id, a) output inserted..id values (9, 9)")]
    [DataRow("merge zt as t using zu as s on t.id = s..id when matched then update set a = s.a;")]
    public void ASourceThenAnEmptyPart_IsAnEmptyColumnName(string statement)
        => new Simulation().AssertSqlError(Fixture + statement, 207, "Invalid column name ''.");

    [TestMethod]
    [DataRow("select dbo.zt..a from zt")]
    [DataRow("select x..a from zt")]
    public void AnEmptyPartAfterNoSource_StaysUnbound(string statement)
        => _ = new Simulation().AssertSqlError(Fixture + statement, 4104);

    [TestMethod]
    [DataRow("select a from zt order by a offset a 0 rows", "0")]
    [DataRow("select a from zt order by a offset 0 rows fetch next a 1 rows only", "1")]
    [DataRow("select a from zt order by a offset 0 a rows", "a")]
    public void OffsetAndFetchGrammar_OutranksTheirCountsChecks(string statement, string near)
        => new Simulation().AssertSqlError(Fixture + statement, 102, $"Incorrect syntax near '{near}'.");

    [TestMethod]
    [DataRow("select 1 a + 2", "+")]
    [DataRow("select 1 a * 2", "*")]
    [DataRow("select 1 a - 2, 3", "-")]
    public void AnOperatorAfterAnAliasedElement_IsTheSyntaxErrorAtIt(string statement, string near)
        => new Simulation().AssertSqlError(statement, 102, $"Incorrect syntax near '{near}'.");

    [TestMethod]
    [DataRow("create index zix on zt")]
    [DataRow("create index zix on zt where a > 0")]
    [DataRow("create index zix on zt with (fillfactor = 80)")]
    [DataRow("create index zix on zt x (a)")]
    public void AnIndexWithoutAKeyList_RaisesMsg35336(string statement)
        => new Simulation().AssertSqlError(Fixture + statement, 35336,
            "The statement failed because specifying key list is missing when creating an index. Create the index with specifying key list .");

    [TestMethod]
    public void AValueWhereAnIndexKeyListBelongs_IsTheSyntaxErrorAtIt()
        => new Simulation().AssertSqlError(Fixture + "create index zix on zt 5", 102, "Incorrect syntax near '5'.");

    [TestMethod]
    [DataRow("create function zif (@x int) returns table as return (select id from dbo.zt where a = @x zzbogus)", "zzbogus")]
    [DataRow("create function zif (@x int) returns table as return select id from dbo.zt where a = @x )", ")")]
    [DataRow("create function zif (@x int) returns table as return (select id from dbo.zt a = @x)", "=")]
    [DataRow("create function zmf (@x int) returns @r table (id int) as begin zzbogus insert @r values (@x) return end", "zzbogus")]
    public void AStrayTokenInAFunctionBody_IsTheSyntaxErrorAtIt(string definition, string near)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Fixture);
        sim.AssertSqlError(definition, 102, $"Incorrect syntax near '{near}'.");
    }

    [TestMethod]
    public void ASchemaBoundSourceWithAnEmptySchemaPart_RaisesMsg4512()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Fixture);
        sim.AssertSqlError("create view zv with schemabinding as select id, a from .zt", 4512,
            "Cannot schema bind view 'zv' because name '.zt' is invalid for schema binding. Names must be in two-part format and an object cannot reference itself.");
    }

    [TestMethod]
    public void AnApplyBodysOrderByWithoutTop_RaisesMsg1033()
        => _ = new Simulation().AssertSqlError(Fixture + "select * from zt cross apply (select (1) id from zt z2 order by id) x", 1033);

    [TestMethod]
    [DataRow("select row_number() over (order by id rows between unbounded zzbogus preceding and current row) from zt", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("select row_number() over (order by id rows between unbounded preceding current row) from zt", 156, "Incorrect syntax near the keyword 'current'.")]
    [DataRow("select row_number() over (order by id rows unbounded preceding zzbogus) from zt", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("select row_number() over (order by id rows unbounded preceding) from zt", 10752, "The function 'row_number' may not have a window frame.")]
    public void ARankingFunctionsFrame_ParsesBeforeItsRefusal(string statement, int number, string message)
        => new Simulation().AssertSqlError(Fixture + statement, number, message);

    [TestMethod]
    [DataRow("select cast(a as zzbogus varchar(5)) from zt", "varchar")]
    [DataRow("select try_cast('x' as zzbogus int)", "int")]
    [DataRow("select * from openjson('[1,2]') with (v zzbogus int '$')", "int")]
    [DataRow("create function zf (@x int) returns zzbogus int as begin return @x end", "int")]
    [DataRow("create function zf (@x zzbogus int) returns int as begin return @x end", "int")]
    [DataRow("create function zmf (@x int) returns zzbogus @r table (id int) as begin return end", "@r")]
    public void ANameAfterAType_OutranksTheTypesLookup(string statement, string near)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Fixture);
        sim.AssertSqlError(statement, 102, $"Incorrect syntax near '{near}'.");
    }

    [TestMethod]
    public void TableAfterAnUnknownReturnType_IsMsg156()
        => new Simulation().AssertSqlError("create function zf () returns zzbogus table as return (select 1 a)", 156, "Incorrect syntax near the keyword 'table'.");

    [TestMethod]
    [DataRow("declare zzbogus c cursor for select 1", "c")]
    [DataRow("declare c zzbogus cursor for select 1", "zzbogus")]
    [DataRow("declare c cursor local zzbogus fast_forward for select 1", "zzbogus")]
    [DataRow("declare @c cursor; set @c = cursor zzbogus for select 1", "zzbogus")]
    public void AnUnknownCursorOption_RaisesMsg155(string statement, string option)
    {
        var ex = new Simulation().AssertSqlError(statement, 155);
        AreEqual($"'{option}' is not a recognized CURSOR option.", ex.Errors[0].Message);
        AreEqual((byte)2, ex.Errors[0].State);
    }

    [TestMethod]
    [DataRow("fetch next from @c into @c")]
    [DataRow("fetch next from @c into @x, @c")]
    public void ACursorVariableAsAFetchTarget_RaisesMsg16949(string fetch)
        => new Simulation().AssertSqlError($"""
            create table zt (id int); insert zt values (1);
            declare @c cursor; set @c = cursor for select id from zt; open @c; declare @x int;
            {fetch}
            """, 16949, "The variable '@c' is a cursor variable, but it is used in a place where a cursor variable is not valid.");

    [TestMethod]
    [DataRow("select 1 union all 1 + 1 from zt", 102, "Incorrect syntax near '1'.")]
    [DataRow("select id from zt with (nolock zzbogus, index(1))", 321, "\"zzbogus\" is not a recognized table hints option.")]
    [DataRow("select id from zt with (, index(1))", 102, "Incorrect syntax near ','.")]
    [DataRow("select * from zt t1 inner hash zzbogus join zt t2 on t1.id = t2.id", 155, "'hash' is not a recognized join option.")]
    [DataRow("select * from zt t1 left zzbogus outer join zt t2 on t1.id = t2.id", 156, "Incorrect syntax near the keyword 'outer'.")]
    [DataRow("select top zzbogus (1) id from zt order by a", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("select id from zt for zzbogus xml path('r')", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("create sequence zsq as zzbogus bigint", 102, "Incorrect syntax near 'bigint'.")]
    [DataRow("create sequence zsq; alter sequence zsq zsq restart", 102, "Incorrect syntax near 'zsq'.")]
    public void QueryAndSequenceGrammar_ReportsRealsError(string statement, int number, string message)
        => new Simulation().AssertSqlError(Fixture + statement, number, message);

    [TestMethod]
    [DataRow("select count(*) x group by ()")]
    [DataRow("select count(*) x having count(*) > 0")]
    public void AnAliasedFromlessSelect_TakesGroupByAndHaving(string statement)
        => AreEqual(1, new Simulation().ExecuteScalar(statement));

    [TestMethod]
    [DataRow("exec sp_executesql N'select @p', N'@p int', @p 1", "1")]
    [DataRow("exec sp_executesql N'select 1', N'@p int', @p @q = 1", "@q")]
    [DataRow("exec sp_help @p 1", "1")]
    public void ATokenAfterAVariableArgument_OutranksTheUndeclaredVariable(string statement, string near)
        => new Simulation().AssertSqlError(statement, 102, $"Incorrect syntax near '{near}'.");
}
