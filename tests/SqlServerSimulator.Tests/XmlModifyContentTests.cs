using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>.modify()</c>'s insert content as the full XQuery expression real takes —
/// paths copying nodes out of the instance, FLWOR, conditionals, typed
/// <c>sql:</c> accessors — and the empty-sequence rules of
/// <c>replace value of</c>. Every expected value and message was probed
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlModifyContentTests
{
    private static string? Modify(string instance, string dml, string declarations = "") =>
        (string?)new Simulation().ExecuteScalar(
            $"{declarations} declare @x xml = '{instance}'; set @x.modify('{dml}'); select convert(nvarchar(max), @x)");

    [TestMethod]
    [DataRow("insert <a>{for $i in (1,2) return $i}</a> into (/r)[1]", "<r><b>7</b><a>1 2</a></r>")]
    [DataRow("insert <a>{/r/b}</a> into (/r)[1]", "<r><b>7</b><a><b>7</b></a></r>")]
    [DataRow("insert <a>{data(/r/b)}</a> into (/r)[1]", "<r><b>7</b><a>7</a></r>")]
    [DataRow("insert /r/b into (/r)[1]", "<r><b>7</b><b>7</b></r>")]
    [DataRow("insert (/r/b)[1] before (/r/b)[1]", "<r><b>7</b><b>7</b></r>")]
    [DataRow("insert element a {/r/b/text()} into (/r)[1]", "<r><b>7</b><a>7</a></r>")]
    [DataRow("insert attribute a {/r/b} into (/r)[1]", "<r a=\"7\"><b>7</b></r>")]
    [DataRow("insert <a x=\"{/r/b}\"/> into (/r)[1]", "<r><b>7</b><a x=\"7\"/></r>")]
    [DataRow("insert if (1=1) then <a/> else <c/> into (/r)[1]", "<r><b>7</b><a/></r>")]
    [DataRow("insert /r/b/text() into (/r)[1]", "<r><b>7</b>7</r>")]
    [DataRow("insert <a>{count(/r/b)}</a> into (/r)[1]", "<r><b>7</b><a>1</a></r>")]
    [DataRow("insert for $i in /r/b return <a>{$i/text()}</a> into (/r)[1]", "<r><b>7</b><a>7</a></r>")]
    [DataRow("insert <a>{(/r/b)[1] + 1}</a> into (/r)[1]", "<r><b>7</b><a>8</a></r>")]
    [DataRow("insert /r into (/r/b)[1]", "<r><b>7<r><b>7</b></r></b></r>")]
    public void Insert_ContentIsAnyExpression(string dml, string expected) =>
        AreEqual(expected, Modify("<r><b>7</b></r>", dml));

    [TestMethod]
    public void Insert_AnAttributeFromThePath_LandsOnTheTarget() =>
        AreEqual("<r b=\"1\"><c b=\"1\"/></r>", Modify("<r b=\"1\"><c/></r>", "insert /r/@b into (/r/c)[1]"));

    [TestMethod]
    public void Insert_ContentChecks()
    {
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><b>7</b></r>'; set @x.modify('insert <a>{/r/b + 1}</a> into (/r)[1]')",
            2389,
            "XQuery [modify()]: '+' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><b>7</b></r>'; set @x.modify('insert text {concat(\"x\", /r/b)} into (/r)[1]')",
            2389,
            "XQuery [modify()]: 'concat()' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><b>7</b></r>'; set @x.modify('insert \"s\" into (/r)[1]')",
            2207,
            "XQuery [modify()]: Only non-document nodes can be inserted. Found \"xs:string\".");
    }

    [TestMethod]
    [DataRow("decimal(5,2) = 1.50", "<r><a>1.5</a></r>")]
    [DataRow("datetime = '2020-01-02'", "<r><a>2020-01-02T00:00:00.000</a></r>")]
    [DataRow("bit = 1", "<r><a>true</a></r>")]
    public void Insert_SqlVariable_TakesItsXQueryForm(string declaration, string expected) =>
        AreEqual(expected, Modify("<r/>", "insert <a>{sql:variable(\"@v\")}</a> into (/r)[1]", $"declare @v {declaration};"));

    [TestMethod]
    [DataRow("float = 1e10", "<r a=\"1.0E10\"/>")]
    [DataRow("money = 1.5", "<r a=\"1.5\"/>")]
    public void ReplaceValue_SqlVariable_TakesItsXQueryForm(string declaration, string expected) =>
        AreEqual(expected, Modify("<r a=\"1\"/>", "replace value of (/r/@a)[1] with sql:variable(\"@v\")", $"declare @v {declaration};"));

    [TestMethod]
    public void SqlVariable_Undeclared_Raises9501() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('insert <a>{sql:variable(\"@nope\")}</a> into (/r)[1]')",
            9501,
            "XQuery: Unable to resolve sql:variable('@nope'). The variable must be declared as a scalar TSQL variable.");

    [TestMethod]
    public void ReplaceValue_OfText_WithTheWrittenEmptySequence_RemovesIt() =>
        AreEqual("<r><a/></r>", Modify("<r><a>1</a></r>", "replace value of (/r/a/text())[1] with ()"));

    [TestMethod]
    public void ReplaceValue_OfText_WithAnExpressionThatIsEmpty_Raises6325() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; set @x.modify('replace value of (/r/a/text())[1] with (/r/b)[1]')",
            6325,
            "XQuery: Replacing the value of a node with an empty sequence is allowed only if '()' is used as the new value expression. The new value expression evaluated to an empty sequence but it is not '()'.");

    [TestMethod]
    [DataRow("()")]
    [DataRow("(/r/@b)[1]")]
    [DataRow("sql:variable(\"@n\")")]
    public void ReplaceValue_OfAnAttribute_WithAnEmptySequence_Raises6320(string value) =>
        new Simulation().AssertSqlError(
            $"declare @n int; declare @x xml = '<r a=\"1\"/>'; set @x.modify('replace value of (/r/@a)[1] with {value}')",
            6320,
            "XQuery: Only nillable elements or text nodes can be updated with empty sequence");

    [TestMethod]
    public void ReplaceValue_EmptyRefusal_EndsTheBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand(
            "begin tran; declare @x xml = '<r a=\"1\"/>'; set @x.modify('replace value of (/r/@a)[1] with ()'); select 1").ExecuteNonQuery());
        AreEqual(6320, error.Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Insert_IntoTheDocumentNode_AppendsATopLevelNode() =>
        AreEqual("<r><a/></r><b/>", Modify("<r><a/></r>", "insert <b/> into ."));

    [TestMethod]
    public void Insert_ExplicitDeclaration_IsKept_PrologOneIsDropped()
    {
        AreEqual(
            "<r xmlns:p=\"urn:x\"><p:a>1</p:a><p:b/></r>",
            Modify("<r xmlns:p=\"urn:x\"><p:a>1</p:a></r>", "declare namespace p=\"urn:x\"; insert <p:b/> into (/r)[1]"));
        AreEqual(
            "<r xmlns:p=\"urn:x\"><p:a>1</p:a><q:b xmlns:q=\"urn:x\"/></r>",
            Modify("<r xmlns:p=\"urn:x\"><p:a>1</p:a></r>", "insert <q:b xmlns:q=\"urn:x\"/> into (/r)[1]"));
    }
}
