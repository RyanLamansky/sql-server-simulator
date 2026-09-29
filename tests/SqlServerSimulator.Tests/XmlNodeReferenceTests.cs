using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The context item a read method starts from — the instance's document node,
/// or the node a <c>.nodes()</c> row references in place — and the namespace
/// bindings <c>.query()</c> writes on nodes it serializes out of their
/// document. Every expected value was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlNodeReferenceTests
{
    private static object? Scalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    // ---- the document node is the context item --------------------------------

    [TestMethod]
    public void RelativePath_StartsAtTheDocumentNode()
    {
        AreEqual(string.Empty, Scalar("declare @x xml = '<r><a/></r>'; select convert(nvarchar(max), @x.query('a'))"));
        AreEqual(1, Scalar("declare @x xml = '<r><a x=\"1\"/></r>'; select @x.value('(r/a/@x)[1]', 'int')"));
        IsTrue((bool)Scalar("declare @x xml = '<r><a/></r>'; select @x.exist('r')")!);
        _ = IsInstanceOfType<DBNull>(Scalar("declare @x xml = '<r>t</r>'; select @x.value('text()[1]', 'varchar(10)')"));
    }

    [TestMethod]
    public void Modify_PathsStartAtTheDocumentNodeToo()
    {
        AreEqual("<r><a/></r>", Scalar("declare @x xml = '<r><a/></r>'; set @x.modify('delete a'); select convert(nvarchar(max), @x)"));
        AreEqual("<r><a/><b/></r>", Scalar("declare @x xml = '<r><a/></r>'; set @x.modify('insert <b/> into (r)[1]'); select convert(nvarchar(max), @x)"));
        AreEqual("<r><a/></r><b/>", Scalar("declare @x xml = '<r><a/></r>'; set @x.modify('insert <b/> into .'); select convert(nvarchar(max), @x)"));
    }

    // ---- a .nodes() row references its node in place -----------------------------

    [TestMethod]
    public void NodesRow_ReachesItsParentAndItsDocument()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @x xml = '<r><a x=\"1\"><b/></a></r>'; "
            + "select c.value('@x', 'int'), convert(nvarchar(max), c.query('..')), convert(nvarchar(max), c.query('/r/a/b')), c.exist('/r'), "
            + "c.value('count(/r/a)', 'int'), c.value('local-name(..)', 'varchar(9)') from @x.nodes('/r/a') n(c)");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("<r><a x=\"1\"><b/></a></r>", reader.GetString(1));
        AreEqual("<b/>", reader.GetString(2));
        IsTrue(reader.GetBoolean(3));
        AreEqual(1, reader.GetInt32(4));
        AreEqual("r", reader.GetString(5));
    }

    [TestMethod]
    public void NodesRow_SiblingsAreStillThere() =>
        AreEqual(2, Scalar("declare @x xml = '<r><a/><a/></r>'; select top (1) c.value('count(../a)', 'int') from @x.nodes('/r/a') n(c)"));

    [TestMethod]
    public void NodesRow_OverAnAttribute_ReadsItsValue() =>
        AreEqual(1, Scalar("declare @x xml = '<r a=\"1\"/>'; select c.value('.', 'int') from @x.nodes('/r/@a') n(c)"));

    [TestMethod]
    public void NestedNodes_KeepTheOriginalDocument() =>
        AreEqual(3, Scalar("""
            declare @x xml = '<r><a><b>1</b></a><a><b>2</b></a></r>';
            select sum(d.value('count(/r/a/b)', 'int') + d.value('.', 'int') - 2)
            from @x.nodes('/r/a') n(c) cross apply c.nodes('b') m(d)
            """));

    // ---- namespace bindings on serialized nodes ------------------------------------

    [TestMethod]
    [DataRow("<r xmlns:p=\"urn:p\"><a/></r>", "/r/a", "<a/>")]
    [DataRow("<r xmlns:p=\"urn:p\"><a><p:b/></a></r>", "/r/a", "<a><p1:b xmlns:p1=\"urn:p\"/></a>")]
    [DataRow("<r xmlns:p=\"urn:p\"><a><p:b/><p:c/></a></r>", "/r/a", "<a><p1:b xmlns:p1=\"urn:p\"/><p2:c xmlns:p2=\"urn:p\"/></a>")]
    [DataRow("<r xmlns:p=\"urn:p\"><a><p:b><p:c/></p:b></a></r>", "/r/a", "<a><p1:b xmlns:p1=\"urn:p\"><p1:c/></p1:b></a>")]
    [DataRow("<r xmlns:p=\"urn:p\" xmlns:q=\"urn:q\"><a p:x=\"1\" q:y=\"2\"/></r>", "/r/a", "<a xmlns:p1=\"urn:p\" p1:x=\"1\" xmlns:p2=\"urn:q\" p2:y=\"2\"/>")]
    [DataRow("<r xmlns:q=\"urn:q\"><a q:z=\"1\"/><b q:z=\"2\"/></r>", "/r/a, /r/b", "<a xmlns:p1=\"urn:q\" p1:z=\"1\"/><b xmlns:p2=\"urn:q\" p2:z=\"2\"/>")]
    [DataRow("<r xmlns:q=\"urn:q\"><a xmlns:p1=\"urn:o\" p1:w=\"0\" q:z=\"1\"/></r>", "/r/a", "<a xmlns:p1=\"urn:o\" p1:w=\"0\" xmlns:p2=\"urn:q\" p2:z=\"1\"/>")]
    [DataRow("<r xmlns:p=\"urn:p\"><a xmlns:q=\"urn:q\"><p:b q:z=\"1\"/></a></r>", "/r/a", "<a xmlns:q=\"urn:q\"><p1:b xmlns:p1=\"urn:p\" q:z=\"1\"/></a>")]
    [DataRow("<r xmlns:p=\"urn:p\"><p:a p:x=\"1\"/></r>", "declare namespace p=\"urn:p\"; /r/p:a", "<p:a xmlns:p=\"urn:p\" p:x=\"1\"/>")]
    [DataRow("<r xmlns:p=\"urn:p\" xmlns:q=\"urn:q\"><p:a q:z=\"1\"><p:b/></p:a></r>", "declare namespace p=\"urn:p\"; /r/p:a", "<p:a xmlns:p=\"urn:p\" xmlns:p10=\"urn:q\" p10:z=\"1\"><p:b/></p:a>")]
    [DataRow("<r xmlns:p1=\"urn:x\" xmlns:q=\"urn:q\"><p1:a q:z=\"1\"/></r>", "declare namespace x=\"urn:x\"; /r/x:a", "<x:a xmlns:x=\"urn:x\" xmlns:p1=\"urn:q\" p1:z=\"1\"/>")]
    [DataRow("<r xmlns=\"urn:d\" xmlns:p=\"urn:p\"><a p:x=\"1\"/></r>", "declare default element namespace \"urn:d\"; /r/a", "<a xmlns=\"urn:d\" xmlns:p1=\"urn:p\" p1:x=\"1\"/>")]
    [DataRow("<r xmlns:p=\"urn:p\"><e p:x=\"1\"/></r>", "<w>{/r/e}</w>", "<w><e xmlns:p1=\"urn:p\" p1:x=\"1\"/></w>")]
    public void Query_BindsEachNamespaceWhereItIsUsed(string document, string xquery, string expected) =>
        AreEqual(expected, Scalar($"declare @x xml = '{document}'; select convert(nvarchar(max), @x.query('{xquery}'))"));

    [TestMethod]
    [DataRow("<r xmlns:p=\"urn:p\"><p:a/></r>", "declare namespace p=\"urn:p\"; /r/p:a", "<p1:a xmlns:p1=\"urn:p\"/>")]
    [DataRow("<r xmlns=\"urn:d\"><a><b/></a></r>", "declare default element namespace \"urn:d\"; /r/a", "<p1:a xmlns:p1=\"urn:d\"><p1:b/></p1:a>")]
    [DataRow("<r xmlns:p=\"urn:p\" xmlns:q=\"urn:q\"><p:a><q:b/></p:a></r>", "declare namespace p=\"urn:p\"; /r/p:a", "<p1:a xmlns:p1=\"urn:p\"><p2:b xmlns:p2=\"urn:q\"/></p1:a>")]
    [DataRow("<r xmlns:p=\"urn:p\"><p:a xmlns:p=\"urn:p\"/></r>", "declare namespace p=\"urn:p\"; /r/p:a", "<p:a xmlns:p=\"urn:p\"/>")]
    [DataRow("<p:r xmlns:p=\"urn:p\"><p:a/></p:r>", "declare namespace p=\"urn:p\"; /p:r", "<p:r xmlns:p=\"urn:p\"><p:a/></p:r>")]
    public void NodesRow_Query_HasNoPrologOfItsOwn(string document, string nodes, string expected) =>
        AreEqual(expected, Scalar($"declare @x xml = '{document}'; select convert(nvarchar(max), c.query('.')) from @x.nodes('{nodes}') n(c)"));

    // ---- the row column is readable only by the methods -----------------------

    [TestMethod]
    [DataRow("where datalength(c) > 0")]
    [DataRow("group by datalength(c)")]
    public void NodesColumn_ReadOutsideTheMethods_Raises493InEveryClause(string clause) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r><a/></r>'; select count(*) from @x.nodes('/r/a') n(c) {clause}",
            493,
            "The column 'c' that was returned from the nodes() method cannot be used directly. It can only be used with one of the four XML data type methods, exist(), nodes(), query(), and value(), or in IS NULL and IS NOT NULL checks.");

    [TestMethod]
    public void NodesColumn_ConvertedInsideIsNull_Raises525() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a/></r>'; select 1 from @x.nodes('/r/a') n(c) where cast(c as nvarchar(max)) is not null",
            525,
            "The column that was returned from the nodes() method cannot be converted to the data type nvarchar(max). It can only be used with one of the four XML data type methods, exist(), nodes(), query(), and value(), or in IS NULL and IS NOT NULL checks.");

    [TestMethod]
    public void NodesColumn_TestedForNull_IsFine() =>
        AreEqual(1, Scalar("declare @x xml = '<r><a/></r>'; select 1 from @x.nodes('/r/a') n(c) where c is not null"));
}
