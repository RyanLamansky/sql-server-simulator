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

    [TestMethod]
    [DataRow("declare @x xml = '<a/>'; set @x.modify('insert <b/> into (/a)[1]') zzbogus", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("declare @x xml = '<a/>'; set @x.modify(zzbogus 'insert <b/> into (/a)[1]')", 102, "Incorrect syntax near 'insert <b/> into (/a)[1]'.")]
    [DataRow("declare @x xml = '<a/>'; set @x.modify()", 174, "The modify function requires 1 argument(s).")]
    [DataRow("declare @x xml = '<a/>'; select @x.value('.' 'int')", 102, "Incorrect syntax near 'int'.")]
    [DataRow("declare @x xml = '<a/>'; select @x.value(zzbogus '.', 'int')", 102, "Incorrect syntax near '.'.")]
    [DataRow("declare @x xml = '<a/>'; select @x.query(@zz, 1)", 137, "Must declare the scalar variable \"@zz\".")]
    [DataRow("select pi(1", 102, "Incorrect syntax near '1'.")]
    [DataRow("select pi(()", 102, "Incorrect syntax near ')'.")]
    [DataRow("select pi(@zz)", 137, "Must declare the scalar variable \"@zz\".")]
    [DataRow("select abs(@zz, 1)", 137, "Must declare the scalar variable \"@zz\".")]
    [DataRow("select pi(zzcol)", 174, "The pi function requires 0 argument(s).")]
    [DataRow("select geometry::Point(1, 2 3)", 102, "Incorrect syntax near '3'.")]
    [DataRow("select geometry::Point(1, 2)", 174, "The Point function requires 3 argument(s).")]
    [DataRow("declare @g geometry = geometry::Point(1, 2, 0); select @g.STBuffer(1 2)", 102, "Incorrect syntax near '2'.")]
    [DataRow("declare @h hierarchyid = '/1/'; select @h.GetAncestor(1 2)", 102, "Incorrect syntax near '2'.")]
    [DataRow("declare @h hierarchyid = '/1/'; select @h.GetAncestor(1, 2 3)", 102, "Incorrect syntax near '3'.")]
    [DataRow("begin try select 1 end try begin catch select error_number(; end catch", 102, "Incorrect syntax near ';'.")]
    public void AMethodOrFunctionArgumentList_ParsesBeforeItsCountAndLiterals(string statement, int number, string message)
        => new Simulation().AssertSqlError(statement, number, message);

    [TestMethod]
    [DataRow("alter index all on zt rebuild with (sort_in_tempdb = on on)", 156, "Incorrect syntax near the keyword 'on'.")]
    [DataRow("alter index all on zt rebuild with (sort_in_tempdb = off (1))", 102, "Incorrect syntax near '('.")]
    [DataRow("alter index all on zt rebuild with (online = off zzbogus)", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("alter index all on zt rebuild with (data_compression = none zzbogus)", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("create index zi on zt (a) with (pad_index = on, statistics_norecompute = off zzbogus)", 102, "Incorrect syntax near 'zzbogus'.")]
    public void AnIndexOptionsWordValue_EndsItsEntry(string statement, int number, string message)
        => new Simulation().AssertSqlError(Fixture + statement, number, message);

    [TestMethod]
    public void AnIndexOptionsWordValue_StillTakesItsNestedListAndPartitions()
        => AreEqual(1, new Simulation().ExecuteScalar(Fixture + """
            alter index all on zt rebuild with (online = on (wait_at_low_priority (max_duration = 0 minutes, abort_after_wait = none)));
            alter index all on zt rebuild partition = all with (data_compression = row on partitions (1));
            select count(*) from sys.indexes where object_id = object_id('zt') and index_id = 1
            """));

    [TestMethod]
    [DataRow("select 1 $identity", "$identity")]
    [DataRow("select 1 as $action", "$action")]
    [DataRow("merge zt as t using zu as s on t.id = s.id when not matched then insert (id, a) values (s.id, s.a) output $action $action;", "$action")]
    [DataRow("merge zt as t using zu as s on t.id = s.id when not matched then insert (id, a) values (s.id, s.a) output inserted.id $identity;", "$identity")]
    [DataRow("delete zt output deleted.id as $action", "$action")]
    public void ADollarWord_IsNoAlias(string statement, string near)
        => new Simulation().AssertSqlError(Fixture + statement, 102, $"Incorrect syntax near '{near}'.");

    [TestMethod]
    public void ABracketedDollarName_IsAnAlias()
        => AreEqual("$x", new Simulation().ExecuteScalar("select name from sys.dm_exec_describe_first_result_set(N'select 1 [$x]', null, 0)"));

    [TestMethod]
    [DataRow("select * from zt pivot pivot (sum(a) for id in ([1])) as p", "pivot")]
    [DataRow("select * from zt pivot (sum(a) for id in in ([1])) as p", "in")]
    [DataRow("select * from zt pivot (sum(a) for id in ([1]) as p", "as")]
    [DataRow("select * from zt unpivot (v for c in (a) as u", "as")]
    public void AKeywordWhereRotationSyntaxExpectsAParenthesis_IsMsg156(string statement, string near)
        => new Simulation().AssertSqlError(Fixture + statement, 156, $"Incorrect syntax near the keyword '{near}'.");

    [TestMethod]
    [DataRow("disable zzbogus trigger t on zt", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("enable enable trigger all on zt", 102, "Incorrect syntax near 'enable'.")]
    [DataRow("create partition zz function zpf (int) as range right for values (1)", 156, "Incorrect syntax near the keyword 'function'.")]
    [DataRow("create partition zps as partition zpf all to ([primary])", 156, "Incorrect syntax near the keyword 'as'.")]
    [DataRow("create partition schemex zps as partition zpf all to ([primary])", 102, "Incorrect syntax near 'schemex'.")]
    [DataRow("drop partition zz function zpf", 156, "Incorrect syntax near the keyword 'function'.")]
    [DataRow("with xmlnamespaces ('urn:a' as p) zzbogus select 1 as [p:x] for xml path('r')", 102, "Incorrect syntax near 'zzbogus'.")]
    [DataRow("dbcc checktable ('zt') with zzbogus no_infomsgs", 102, "Incorrect syntax near 'no_infomsgs'.")]
    [DataRow("dbcc show_statistics ('zt', 'zix') with zzbogus stat_header", 102, "Incorrect syntax near 'stat_header'.")]
    [DataRow("create statistics zs on zt (a) with zzbogus fullscan", 102, "Incorrect syntax near 'fullscan'.")]
    [DataRow("create statistics zs on zt (a) with , norecompute", 102, "Incorrect syntax near ','.")]
    [DataRow("update statistics zt with zzbogus norecompute", 102, "Incorrect syntax near 'norecompute'.")]
    [DataRow("raiserror ('x', 10, 1) with zzbogus nowait", 102, "Incorrect syntax near 'nowait'.")]
    public void StatementGrammar_ReportsRealsSyntaxError(string statement, int number, string message)
        => new Simulation().AssertSqlError(Fixture + statement, number, message);

    [TestMethod]
    [DataRow("dbcc checktable ('zt') with zzbogus", 195, "'zzbogus' is not a recognized option.")]
    [DataRow("create statistics zs on zt (a) with zzbogus", 155, "'zzbogus' is not a recognized CREATE STATISTICS option.")]
    public void AnUnknownOptionWordAlone_KeepsItsRefusal(string statement, int number, string message)
        => new Simulation().AssertSqlError(Fixture + statement, number, message);
}
