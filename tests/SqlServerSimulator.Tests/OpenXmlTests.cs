using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>OPENXML</c> and the
/// <c>sp_xml_preparedocument</c> / <c>sp_xml_removedocument</c> pair that
/// stocks the session's document store. Every expectation is probe-confirmed
/// against SQL Server 2025; the deep-dive is in <c>docs/claude/xml.md</c>.
/// </summary>
[TestClass]
public sealed class OpenXmlTests
{
    /// <summary>A two-element document reused by the mapping tests.</summary>
    private const string PrepareTwoRows = """
        declare @h int;
        exec sp_xml_preparedocument @h output,
            '<root p="P"><a id="1" nm="x"><b>bb</b></a><a id="2" nm="y"><b>cc</b></a></root>';
        """;

    private static List<string?[]> ReadRows(DbDataReader reader)
    {
        var rows = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
            rows.Add(row);
        }
        return rows;
    }

    private static List<string?[]> Query(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        return ReadRows(reader);
    }

    private static void AssertRows(List<string?[]> actual, params string?[][] expected)
    {
        HasCount(expected.Length, actual, "row count");
        for (var i = 0; i < expected.Length; i++)
            CollectionAssert.AreEqual(expected[i], actual[i], $"row {i}");
    }

    // ---- handle lifecycle ----

    [TestMethod]
    public void Prepare_FirstHandleIsOne_AndReturnCodeIsZero()
        => AssertRows(Query(new Simulation(), """
            declare @h int, @rc int;
            exec @rc = sp_xml_preparedocument @h output, '<r/>';
            select @h, @rc
            """), ["1", "0"]);

    /// <summary>
    /// Real hands out odd handles two apart and never recycles a released one
    /// (probe-confirmed: removing handle 3 still leaves the next allocation at
    /// 7).
    /// </summary>
    [TestMethod]
    public void Prepare_HandlesAdvanceByTwo_AndAreNeverReused()
        => AssertRows(Query(new Simulation(), """
            declare @h1 int, @h2 int, @h3 int, @h4 int;
            exec sp_xml_preparedocument @h1 output, '<r/>';
            exec sp_xml_preparedocument @h2 output, '<r/>';
            exec sp_xml_preparedocument @h3 output, '<r/>';
            exec sp_xml_removedocument @h2;
            exec sp_xml_preparedocument @h4 output, '<r/>';
            select @h1, @h2, @h3, @h4
            """), ["1", "3", "5", "7"]);

    [TestMethod]
    public void PrepareSelectRemove_RoundTrips()
        => AreEqual(2, new Simulation().ExecuteScalar($"""
            {PrepareTwoRows}
            select count(*) from openxml(@h, '/root/a') with (id int)
            """));

    /// <summary>A handle outlives the batch that made it, on the same session.</summary>
    [TestMethod]
    public void Handle_SurvivesBatchBoundary()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("declare @h int; exec sp_xml_preparedocument @h output, '<r><a id=\"7\"/></r>'").ExecuteNonQuery();
        using var reader = connection.CreateCommand("declare @h int = 1; select id from openxml(@h, '/r/a') with (id int)").ExecuteReader();
        AssertRows(ReadRows(reader), ["7"]);
    }

    /// <summary>A rollback doesn't release a prepared document — the store isn't transactional.</summary>
    [TestMethod]
    public void Handle_SurvivesRollback()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            declare @h int;
            begin tran;
            exec sp_xml_preparedocument @h output, '<r><a id="7"/></r>';
            rollback;
            select id from openxml(@h, '/r/a') with (id int)
            """));

    /// <summary>Another session's handle is invisible, exactly as its <c>#temp</c> tables are.</summary>
    [TestMethod]
    public void Handle_IsInvisibleToAnotherSession()
    {
        var simulation = new Simulation();
        using var owner = simulation.CreateOpenConnection();
        _ = owner.CreateCommand("declare @h int; exec sp_xml_preparedocument @h output, '<r><a id=\"7\"/></r>'").ExecuteNonQuery();

        using var other = simulation.CreateOpenConnection();
        using var command = other.CreateCommand("declare @h int = 1; select id from openxml(@h, '/r/a') with (id int)");
        var exception = Throws<SimulatedSqlException>(command.ExecuteScalar);
        AreEqual(8179, exception.Number);
        AreEqual("Could not find prepared statement with handle 1.", exception.Message);
    }

    [TestMethod]
    public void UnknownHandle_RaisesMsg8179()
    {
        var exception = new Simulation().AssertSqlError(
            "declare @h int = 99; select * from openxml(@h, '/r/a') with (id int)", 8179);
        AreEqual("Could not find prepared statement with handle 99.", exception.Message);
        AreEqual(5, exception.State);
    }

    /// <summary>A NULL handle reports handle <c>0</c> — real coerces before the lookup.</summary>
    [TestMethod]
    public void NullHandle_ReportsHandleZero()
        => new Simulation().AssertSqlError(
            "declare @h int; select * from openxml(@h, '/r/a') with (id int)", 8179,
            "Could not find prepared statement with handle 0.");

    [TestMethod]
    public void RemoveTwice_RaisesMsg8179()
    {
        var exception = new Simulation().AssertSqlError("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<r/>';
            exec sp_xml_removedocument @h;
            exec sp_xml_removedocument @h;
            select 1
            """, 8179);
        AreEqual("Could not find prepared statement with handle 1.", exception.Message);
    }

    /// <summary>A removed handle is gone from <c>OPENXML</c>'s view too.</summary>
    [TestMethod]
    public void RemovedHandle_NoLongerReadable()
        => new Simulation().AssertSqlError("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<r><a id="1"/></r>';
            exec sp_xml_removedocument @h;
            select * from openxml(@h, '/r/a') with (id int)
            """, 8179);

    /// <summary>
    /// A document that won't parse is Msg 6602 state 2, attributed to the
    /// procedure. Real's detail sentence comes from MSXML and the simulator's
    /// from .NET, so only the surrounding shape is pinned.
    /// </summary>
    [TestMethod]
    public void MalformedDocument_RaisesMsg6602()
    {
        var exception = new Simulation().AssertSqlError(
            "declare @h int; exec sp_xml_preparedocument @h output, '<r><a></r>'; select 1", 6602);
        AreEqual(2, exception.State);
        AreEqual("sp_xml_preparedocument", exception.Procedure);
        StartsWith("The error description is '", exception.Message);
    }

    /// <summary>
    /// A caught Msg 6602 leaves both the handle and the return code unwritten
    /// — probe-confirmed, and the reason the failure path doesn't stage a code.
    /// </summary>
    [TestMethod]
    public void MalformedDocument_LeavesHandleAndReturnCodeUnwritten()
        => AssertRows(Query(new Simulation(), """
            declare @h int, @rc int;
            begin try exec @rc = sp_xml_preparedocument @h output, '<r><a></r>'; end try begin catch end catch;
            select @rc, @h
            """), new string?[] { null, null });

    /// <summary>
    /// An omitted or NULL document still allocates a handle (probe-confirmed rc
    /// 0) over a document with no nodes, so every rowpattern answers zero rows.
    /// </summary>
    [TestMethod]
    public void NullDocument_StillAllocatesHandle()
    {
        var simulation = new Simulation();
        AssertRows(Query(simulation, """
            declare @h int, @rc int;
            exec @rc = sp_xml_preparedocument @h output, null;
            select @h, @rc
            """), ["1", "0"]);
        AreEqual(0, simulation.ExecuteScalar("""
            declare @h int;
            exec sp_xml_preparedocument @h output, null;
            select count(*) from openxml(@h, '//*') with (id int)
            """));
        AreEqual(0, simulation.ExecuteScalar("""
            declare @h int;
            exec sp_xml_preparedocument @h output, null;
            select count(*) from openxml(@h, '/')
            """));
    }

    // ---- flags ----

    private const string FlagsDocument = """
        declare @h int;
        exec sp_xml_preparedocument @h output,
            '<root><a id="1" nm="x"><b>bb</b><nm>elemnm</nm></a></root>';
        """;

    /// <summary>
    /// The default flags (0) and flags 1 are both attribute-centric, 2 is
    /// element-centric, and 3 reads an attribute first and falls back to a
    /// child element. Bit 8 changes none of the mapping.
    /// </summary>
    [TestMethod]
    public void Flags_DriveTheDefaultColumnMapping()
    {
        var simulation = new Simulation();
        string?[] Map(string flags) => Query(simulation, $"""
            {FlagsDocument}
            select * from openxml(@h, '/root/a'{flags}) with (id int, nm varchar(20), b varchar(20))
            """)[0];

        CollectionAssert.AreEqual(new[] { "1", "x", null }, Map(string.Empty));
        CollectionAssert.AreEqual(new[] { "1", "x", null }, Map(", 1"));
        CollectionAssert.AreEqual(new[] { null, "elemnm", "bb" }, Map(", 2"));
        CollectionAssert.AreEqual(new[] { "1", "x", "bb" }, Map(", 3"));
        CollectionAssert.AreEqual(new[] { "1", "x", null }, Map(", 8"));
        CollectionAssert.AreEqual(new[] { "1", "x", null }, Map(", 9"));
        CollectionAssert.AreEqual(new[] { null, "elemnm", "bb" }, Map(", 10"));
        CollectionAssert.AreEqual(new[] { "1", "x", "bb" }, Map(", 11"));
    }

    [TestMethod]
    public void Flags_MayComeFromAVariable()
        => AreEqual("bb", new Simulation().ExecuteScalar($"""
            {FlagsDocument}
            declare @f int = 2;
            select b from openxml(@h, '/root/a', @f) with (id int, b varchar(20))
            """));

    private const string OverflowDocument = """
        declare @h int;
        exec sp_xml_preparedocument @h output,
            '<r><a id="1" nm="x" o="o"><b>bb</b><c>cc</c></a></r>';
        """;

    /// <summary>
    /// Without bit 8, <c>@mp:xmltext</c> is the row node's whole outer XML;
    /// with it, every node another column consumed is subtracted.
    /// </summary>
    [TestMethod]
    public void MetaProperty_XmlText_HonoursTheNotConsumedFlag()
    {
        var simulation = new Simulation();
        AreEqual("""<a id="1" nm="x" o="o"><b>bb</b><c>cc</c></a>""", simulation.ExecuteScalar($"""
            {OverflowDocument}
            select xt from openxml(@h, '/r/a', 1) with (id int, xt ntext '@mp:xmltext')
            """));
        AreEqual("""<a nm="x" o="o"><c>cc</c></a>""", simulation.ExecuteScalar($"""
            {OverflowDocument}
            select xt from openxml(@h, '/r/a', 11) with (id int, b varchar(9), xt ntext '@mp:xmltext')
            """));
    }

    // ---- colpatterns ----

    /// <summary>
    /// A colpattern is XPath 1.0 relative to the row node: an attribute step, a
    /// child path, <c>text()</c>, a parent step, a descendant step, and the
    /// context node itself (whose value is the concatenated descendant text).
    /// A pattern that matches nothing is NULL, not an error.
    /// </summary>
    [TestMethod]
    public void ColPatterns_SelectRelativeToTheRowNode()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output,
                '<root p="P"><a id="1"><b>bb</b><c><d>dd</d></c>txtA</a></root>';
            select * from openxml(@h, '/root/a') with (
                attr int '@id',
                child varchar(20) 'b',
                grand varchar(20) 'c/d',
                own varchar(20) 'text()',
                up varchar(20) '../@p',
                gone varchar(20) 'nosuch',
                deep varchar(20) './/d',
                self varchar(30) '.')
            """), ["1", "bb", "dd", "txtA", "P", null, "dd", "bbddtxtA"]);

    /// <summary>A colpattern matching several nodes takes the first.</summary>
    [TestMethod]
    public void ColPattern_MultipleMatches_TakesTheFirst()
        => AreEqual("b1", new Simulation().ExecuteScalar("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><a><b>b1</b><b>b2</b></a></root>';
            select v from openxml(@h, '/root/a') with (v varchar(20) 'b')
            """));

    /// <summary>The selected text routes through the ordinary string-to-type coercion.</summary>
    [TestMethod]
    public void ColumnType_CoercionFailure_RaisesMsg245()
        => new Simulation().AssertSqlError("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><a id="zz"/></root>';
            select id from openxml(@h, '/root/a') with (id int '@id')
            """, 245);

    [TestMethod]
    public void MetaProperties_ProjectNodeIdentity()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><a id="1"/><a id="2"/></root>';
            select * from openxml(@h, '/root/a') with (
                mid int '@mp:id',
                loc varchar(20) '@mp:localname',
                par int '@mp:parentid',
                pl varchar(20) '@mp:parentlocalname',
                pfx varchar(20) '@mp:prefix',
                prv int '@mp:prev')
            """),
            ["2", "a", "0", "root", null, null],
            ["4", "a", "0", "root", null, "2"]);

    // ---- WITH table ----

    [TestMethod]
    public void WithTable_TakesTheTablesColumnShape()
        => AssertRows(Query(new Simulation(), """
            create table oxm_t (id int, nm varchar(20), extra date null);
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><a id="1" nm="x"/><a id="2" nm="y"/></root>';
            select * from openxml(@h, '/root/a') with oxm_t
            """),
            ["1", "x", null],
            ["2", "y", null]);

    [TestMethod]
    public void WithTable_UnknownName_RaisesMsg208()
        => new Simulation().AssertSqlError("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><a id="1"/></root>';
            select * from openxml(@h, '/root/a') with oxm_nosuch
            """, 208);

    // ---- edge table ----

    /// <summary>
    /// The no-<c>WITH</c> edge table's nine columns, probe-confirmed against
    /// SQL Server 2025: <c>id</c> / <c>parentid</c> / <c>prev</c> are
    /// <c>bigint</c>, <c>nodetype</c> <c>int</c>, the four name columns
    /// <c>nvarchar(4000)</c>, and <c>text</c> is <c>ntext</c>.
    /// </summary>
    [TestMethod]
    public void EdgeTable_ColumnShape()
    {
        using var reader = new Simulation().ExecuteReader("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><a id="1">txt</a></root>';
            select * from openxml(@h, '/root/a')
            """);
        var names = new string[reader.FieldCount];
        var types = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            names[i] = reader.GetName(i);
            types[i] = reader.GetDataTypeName(i);
        }
        CollectionAssert.AreEqual(
            new[] { "id", "parentid", "nodetype", "localname", "prefix", "namespaceuri", "datatype", "prev", "text" },
            names);
        CollectionAssert.AreEqual(
            new[] { "bigint", "bigint", "int", "nvarchar", "nvarchar", "nvarchar", "nvarchar", "bigint", "ntext" },
            types);
    }

    /// <summary>
    /// The edge table is the matched nodes' whole subtrees in document order,
    /// with real's own numbering: the document element is <c>0</c> and still
    /// consumes a counter slot (so the next node is <c>2</c>), an element's
    /// attributes come before its children, and attribute value text nodes are
    /// numbered last. Probe-pinned against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void EdgeTable_PinnedRows()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root x="9"><a id="1">txt</a><a id="2"/></root>';
            select id, parentid, nodetype, localname, prev, cast(text as varchar(20))
            from openxml(@h, '/root')
            """),
            ["0", null, "1", "root", null, null],
            ["2", "0", "2", "x", null, null],
            ["8", "2", "3", "#text", null, "9"],
            ["3", "0", "1", "a", null, null],
            ["4", "3", "2", "id", null, null],
            ["9", "4", "3", "#text", null, "1"],
            ["5", "3", "3", "#text", null, "txt"],
            ["6", "0", "1", "a", "3", null],
            ["7", "6", "2", "id", null, null],
            ["10", "7", "3", "#text", null, "2"]);

    /// <summary>
    /// A text node opening its parent element's content is numbered one slot
    /// late, behind whichever node the walk reaches next — real's quirk, not
    /// document order.
    /// </summary>
    [TestMethod]
    public void EdgeTable_FirstChildTextIsNumberedLate()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root>t1<b/>t2<c/></root>';
            select id, localname, prev from openxml(@h, '/root')
            """),
            ["0", "root", null],
            ["3", "#text", null],
            ["2", "b", "3"],
            ["4", "#text", "2"],
            ["5", "c", "4"]);

    /// <summary>The edge table carries only the matched subtree, not the whole document.</summary>
    [TestMethod]
    public void EdgeTable_CoversOnlyTheMatchedSubtree()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root><z/><a id="1"/></root>';
            select id, parentid, localname from openxml(@h, '/root/a')
            """),
            ["3", "0", "a"],
            ["4", "3", "id"],
            ["5", "4", "#text"]);

    /// <summary>
    /// A rowpattern of <c>/</c> matches the document node: the edge table
    /// reports the document element's subtree with no wrapper row, and a
    /// <c>WITH</c> schema gets one all-NULL row, since the document node
    /// carries neither attributes nor a name.
    /// </summary>
    [TestMethod]
    public void DocumentNodeRowPattern()
    {
        var simulation = new Simulation();
        const string Prepare = """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<root a1="9"><a id="1"/></root>';
            """;
        AssertRows(Query(simulation, $"""
            {Prepare}
            select id, parentid, nodetype, localname from openxml(@h, '/')
            """),
            ["0", null, "1", "root"],
            ["2", "0", "2", "a1"],
            ["5", "2", "3", "#text"],
            ["3", "0", "1", "a"],
            ["4", "3", "2", "id"],
            ["6", "4", "3", "#text"]);
        AssertRows(Query(simulation, $"""
            {Prepare}
            select * from openxml(@h, '/') with (a1 int, x varchar(9) '@mp:localname')
            """), new string?[] { null, null });
    }

    /// <summary>Comments and processing instructions carry nodetype 8 / 7 with their content in <c>text</c>.</summary>
    [TestMethod]
    public void EdgeTable_CommentsAndProcessingInstructions()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<r><!--CM--><?PI PD?><a>tt</a></r>';
            select id, nodetype, localname, cast(text as varchar(20)) from openxml(@h, '/r')
            """),
            ["0", "1", "r", null],
            ["2", "8", "#comment", "CM"],
            ["3", "7", "PI", "PD"],
            ["4", "1", "a", null],
            ["5", "3", "#text", "tt"]);

    // ---- namespaces ----

    /// <summary>
    /// The third argument is a wrapper element whose <c>xmlns</c> attributes
    /// declare the prefixes the rowpattern and colpatterns may use — they need
    /// not be the prefixes the document itself wrote.
    /// </summary>
    [TestMethod]
    public void XPathNamespaces_DeclarePatternPrefixes()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output,
                '<r xmlns:p="urn:x"><p:a p:id="1"/><p:a p:id="2"/></r>',
                '<root xmlns:q="urn:x"/>';
            select id from openxml(@h, '/r/q:a') with (id int '@q:id')
            """),
            ["1"],
            ["2"]);

    /// <summary>A default namespace in the document is reached through a prefix bound to the same URI.</summary>
    [TestMethod]
    public void XPathNamespaces_ReachADefaultNamespacedDocument()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            declare @h int;
            exec sp_xml_preparedocument @h output,
                '<r xmlns="urn:d"><a id="1"/></r>',
                '<root xmlns:d="urn:d"/>';
            select id from openxml(@h, '/d:r/d:a') with (id int '@id')
            """));

    /// <summary>A namespace declaration surfaces in the edge table as an attribute with prefix <c>xmlns</c> and no URI.</summary>
    [TestMethod]
    public void EdgeTable_NamespaceDeclarations()
        => AssertRows(Query(new Simulation(), """
            declare @h int;
            exec sp_xml_preparedocument @h output, '<r xmlns:p="urn:x"><p:a/></r>';
            select id, nodetype, localname, prefix, namespaceuri from openxml(@h, '/')
            """),
            ["0", "1", "r", null, null],
            ["2", "2", "p", "xmlns", null],
            ["4", "3", "#text", null, null],
            ["3", "1", "a", "p", "urn:x"]);

    // ---- rowpattern dialect ----

    private const string AxisDocument = """
        declare @h int;
        exec sp_xml_preparedocument @h output,
            '<root><a id="1" t="p"/><a id="2" t="q"/><z><a id="3"/></z></root>';
        """;

    /// <summary>
    /// The rowpattern is XPath 1.0 — descendant shorthand, predicates,
    /// positional predicates, named axes and a relative path all work, because
    /// the pattern runs straight through the DOM's own engine.
    /// </summary>
    [TestMethod]
    public void RowPattern_IsXPath1()
    {
        var simulation = new Simulation();
        List<string?[]> Match(string pattern) => Query(simulation, $"""
            {AxisDocument}
            select id from openxml(@h, '{pattern}') with (id int)
            """);

        AssertRows(Match("//a"), ["1"], ["2"], ["3"]);
        AssertRows(Match("/root//a"), ["1"], ["2"], ["3"]);
        AssertRows(Match("/root/a[@t=\"p\"]"), ["1"]);
        AssertRows(Match("/root/a[1]"), ["1"]);
        AssertRows(Match("/root/descendant::a"), ["1"], ["2"], ["3"]);
        AssertRows(Match("/root/child::a"), ["1"], ["2"]);
        AssertRows(Match("root/a"), ["1"], ["2"]);
    }

    /// <summary>An attribute rowpattern makes each attribute a row; its own value is <c>'.'</c>.</summary>
    [TestMethod]
    public void RowPattern_MayMatchAttributes()
        => AssertRows(Query(new Simulation(), $"""
            {AxisDocument}
            select v from openxml(@h, '/root/a/@id') with (v varchar(9) '.')
            """), ["1"], ["2"]);

    [TestMethod]
    public void RowPattern_MayComeFromAVariable()
        => AreEqual(2, new Simulation().ExecuteScalar($"""
            {AxisDocument}
            declare @p varchar(50) = '/root/a';
            select count(*) from openxml(@h, @p) with (id int)
            """));

    /// <summary>A pattern the XPath engine refuses is Msg 6603 state 2 — rowpattern and colpattern alike.</summary>
    [TestMethod]
    public void BadRowPattern_RaisesMsg6603()
    {
        var exception = new Simulation().AssertSqlError("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<r><a/></r>';
            select * from openxml(@h, '/r/[[') with (id int)
            """, 6603);
        AreEqual(2, exception.State);
        StartsWith("XML parsing error: ", exception.Message);
    }

    [TestMethod]
    public void BadColPattern_RaisesMsg6603()
        => new Simulation().AssertSqlError("""
            declare @h int;
            exec sp_xml_preparedocument @h output, '<r><a/></r>';
            select * from openxml(@h, '/r/a') with (id int '[[')
            """, 6603);

    // ---- grammar ----

    /// <summary>
    /// The handle argument must be a variable — real reports Msg 102 on a
    /// literal, and on any expression combiner in the argument list.
    /// </summary>
    [TestMethod]
    public void LiteralHandle_RaisesMsg102()
        => new Simulation().ValidateSyntaxError("select * from openxml(99, '/r/a') with (id int)", "99");

    [TestMethod]
    public void ExpressionArgument_RaisesMsg102()
        => new Simulation().ValidateSyntaxError("""
            declare @h int;
            select * from openxml(@h, '/r/' + 'a') with (id int)
            """, "+");

    /// <summary>OPENXML is a rowset function, so a scalar position is a syntax error.</summary>
    [TestMethod]
    public void OpenXmlInSelectList_RaisesSyntaxError()
        => _ = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("declare @h int; select openxml(@h, '/a')"));

    // ---- composition ----

    [TestMethod]
    public void OpenXml_JoinsAndAliasesLikeAnyRowset()
        => AreEqual("x", new Simulation().ExecuteScalar($"""
            create table oxm_lookup (k int, label varchar(10));
            insert oxm_lookup values (1, 'x'), (2, 'y');
            {PrepareTwoRows}
            select l.label from openxml(@h, '/root/a') with (id int) o
            join oxm_lookup l on l.k = o.id
            where o.id = 1
            """));

    [TestMethod]
    public void OpenXml_FeedsSelectInto()
        => AreEqual(2, new Simulation().ExecuteScalar($"""
            {PrepareTwoRows}
            select id, nm into oxm_copy from openxml(@h, '/root/a') with (id int, nm varchar(10));
            select count(*) from oxm_copy
            """));

    /// <summary>Two OPENXML sources over the same handle compose in one query.</summary>
    [TestMethod]
    public void OpenXml_TwiceOverOneHandle()
        => AreEqual(4, new Simulation().ExecuteScalar($"""
            {PrepareTwoRows}
            select count(*)
            from openxml(@h, '/root/a') with (id int) o1
            cross join openxml(@h, '/root/a') with (id int) o2
            """));
}
