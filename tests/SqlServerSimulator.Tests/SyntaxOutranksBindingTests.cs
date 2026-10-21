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
    [DataRow("exec sp_executesql N'select @p', N'@p int', @p 1", "1")]
    [DataRow("exec sp_executesql N'select 1', N'@p int', @p @q = 1", "@q")]
    [DataRow("exec sp_help @p 1", "1")]
    public void ATokenAfterAVariableArgument_OutranksTheUndeclaredVariable(string statement, string near)
        => new Simulation().AssertSqlError(statement, 102, $"Incorrect syntax near '{near}'.");
}
