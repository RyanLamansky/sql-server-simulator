using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The XQuery node constructors in the read methods — computed
/// <c>attribute</c> / <c>text</c>, direct comments and processing
/// instructions, attribute items hoisted into an enclosing element — and the
/// rule that makes constructed XML opaque (Msg 2373). Every expected value
/// and message was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlConstructorTests
{
    private const string Document = "<r a=\"1\" b=\"2\"><x>1</x><x>2</x></r>";

    private static string? Query(string xquery) =>
        (string?)new Simulation().ExecuteScalar(
            $"declare @x xml = '{Document}'; select convert(nvarchar(max), @x.query('{xquery}'))");

    private static bool Exists(string xquery) =>
        (bool)new Simulation().ExecuteScalar($"declare @x xml = '{Document}'; select @x.exist('{xquery}')")!;

    private static void QueryError(string xquery, int errorNumber, string expectedMessage) =>
        new Simulation().AssertSqlError($"declare @x xml = '{Document}'; select @x.query('{xquery}')", errorNumber, expectedMessage);

    // ---- computed attribute ------------------------------------------------

    [TestMethod]
    public void ComputedAttribute_LandsOnTheEnclosingElement() =>
        AreEqual("<a x=\"1\"/>", Query("<a>{attribute x {1}}</a>"));

    [TestMethod]
    public void ComputedAttribute_ContentAtomizesJoinedBySpaces()
    {
        AreEqual("<a x=\"1 2\"/>", Query("<a>{attribute x {1, 2}}</a>"));
        AreEqual("<e z=\"1 2\"/>", Query("<e>{attribute z {/r/x}}</e>"));
    }

    [TestMethod]
    public void ComputedAttribute_EmptyContentIsTheEmptyString()
    {
        AreEqual("<a x=\"\"/>", Query("<a>{attribute x {()}}</a>"));
        AreEqual("<e z=\"\"/>", Query("<e>{attribute z {}}</e>"));
    }

    [TestMethod]
    public void ComputedAttribute_AfterTextIsStillHoisted() =>
        AreEqual("<a b=\"2\">x</a>", Query("<a>x{attribute b {2}}</a>"));

    [TestMethod]
    public void ComputedAttribute_PrefixResolvesThroughTheProlog() =>
        AreEqual("<e xmlns:p=\"urn:p\" p:z=\"1\"/>", Query("declare namespace p=\"urn:p\"; <e>{attribute p:z {1}}</e>"));

    [TestMethod]
    public void ComputedAttribute_AtTheTopLevel_Raises2396() =>
        QueryError("attribute x {1}", 2396, "XQuery [query()]: Attribute may not appear outside of an element");

    [TestMethod]
    public void ComputedAttribute_InExist_IsANode() =>
        IsTrue(Exists("attribute z {()}"));

    [TestMethod]
    public void ComputedAttribute_NamedXmlns_Raises9316() =>
        QueryError("<e>{attribute xmlns {1}}</e>", 9316, "XQuery [query()]: Cannot use 'xmlns' in the name expression of computed attribute constructor.");

    [TestMethod]
    public void ComputedAttribute_UndeclaredPrefix_Raises2229() =>
        QueryError("<e>{attribute p:z {1}}</e>", 2229, "XQuery [query()]: The name \"p\" does not denote a namespace.");

    [TestMethod]
    public void ComputedAttribute_BesideAnAtomicInOneSequence_Raises2210() =>
        QueryError("element e {\"a\", attribute z {1}}", 2210, "XQuery [query()]: Heterogeneous sequences are not allowed: found 'xs:string' and 'attribute(z,xdt:untypedAtomic)'");

    [TestMethod]
    public void AttributeFromAPath_IsHoistedToo()
    {
        AreEqual("<e a=\"1\"/>", Query("<e>{/r/@a}</e>"));
        AreEqual("<e a=\"1\" b=\"2\"/>", Query("<e>{/r/@*}</e>"));
        AreEqual("<e b=\"2\" a=\"1\"/>", Query("<e>{(/r/@b, /r/@a)}</e>"));
        AreEqual("<e><f b=\"2\"/></e>", Query("<e><f>{/r/@b}</f></e>"));
    }

    [TestMethod]
    public void Attribute_AfterAnElementChild_Raises6307()
    {
        const string message = "XML well-formedness check: Attribute cannot appear outside of element declaration. Rewrite your XQuery so it returns well-formed XML.";
        QueryError("<e>{/r/x}{/r/@a}</e>", 6307, message);
        QueryError("<e><!--c-->{attribute z {1}}</e>", 6307, message);
        QueryError("element e {(/r/x)[1], attribute z {1}}", 6307, message);
    }

    [TestMethod]
    public void Attribute_AfterTextOrAnAtomic_IsFine()
    {
        AreEqual("<e z=\"1\">12</e>", Query("<e>{/r/x/text()}{attribute z {1}}</e>"));
        AreEqual("<e z=\"1\">1</e>", Query("<e>{1}{attribute z {1}}</e>"));
    }

    [TestMethod]
    public void Attribute_Duplicated_Raises6308()
    {
        QueryError("<e a=\"9\">{/r/@a}</e>", 6308, "XML well-formedness check: Duplicate attribute 'a'. Rewrite your XQuery so it returns well-formed XML.");
        QueryError("element e {attribute z {1}, attribute z {2}}", 6308, "XML well-formedness check: Duplicate attribute 'z'. Rewrite your XQuery so it returns well-formed XML.");
    }

    [TestMethod]
    public void TopLevelAttribute_Raises2396OrAt6307AmongOtherNodes()
    {
        QueryError("/r/@a", 2396, "XQuery [query()]: Attribute may not appear outside of an element");
        QueryError("for $i in /r/@* return $i", 2396, "XQuery [query()]: Attribute may not appear outside of an element");
        QueryError("/r/@a, /r/x", 6307, "XML well-formedness check: Attribute cannot appear outside of element declaration. Rewrite your XQuery so it returns well-formed XML.");
    }

    [TestMethod]
    public void XmlWellFormednessErrors_AbortTheBatchAndTheTransaction()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("begin tran; select cast('<r a=\"1\"/>' as xml).query('<e a=\"2\">{/r/@a}</e>'); select 1").ExecuteNonQuery());
        AreEqual(6308, error.Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    // ---- computed text -----------------------------------------------------

    [TestMethod]
    public void ComputedText_AtomizesItsContent()
    {
        AreEqual("abc", Query("text {\"abc\"}"));
        AreEqual("1 2", Query("text {/r/x}"));
        AreEqual("<a>a&lt;b</a>", Query("<a>{text {\"a<b\"}}</a>"));
        AreEqual("<a>ab</a>", Query("element a {text {\"a\"}, text {\"b\"}}"));
    }

    [TestMethod]
    public void ComputedText_EmptySequenceBuildsNoNode_EmptyStringBuildsOne()
    {
        IsFalse(Exists("text {()}"));
        IsTrue(Exists("text {\"\"}"));
        AreEqual("<e/>", Query("<e>{text {\"\"}}</e>"));
    }

    [TestMethod]
    public void ComputedText_TakesASingleExpression()
    {
        QueryError("text {1, 2}", 2205, "XQuery [query()]: \"}\" was expected.");
        QueryError("text {}", 2209, "XQuery [query()]: Syntax error near '}'");
    }

    // ---- direct comments and processing instructions ------------------------

    [TestMethod]
    public void DirectComment_KeepsItsTextLiterally()
    {
        AreEqual("<!-- c -->", Query("<!-- c -->"));
        AreEqual("<a>1<!-- {1} --></a>", Query("<a>{1}<!-- {1} --></a>"));
        AreEqual("<!--a--><?p d?><e/>", Query("<!--a-->, <?p d?>, <e/>"));
    }

    [TestMethod]
    public void DirectComment_WithADoubleHyphen_Raises9322() =>
        QueryError("<!--a--->", 9322, "XQuery [query()]: Two consecutive '-' can only appear in a comment constructor if they are used to close the comment ('-->').");

    [TestMethod]
    public void DirectProcessingInstruction_NormalizesTheSeparator()
    {
        AreEqual("<?pi data?>", Query("<?pi data?>"));
        AreEqual("<a><?pi data ?></a>", Query("<a><?pi  data ?></a>"));
        AreEqual("<?pi?>", Query("<?pi?>"));
    }

    [TestMethod]
    public void DirectProcessingInstruction_TargetRules()
    {
        QueryError("<?XML a?>", 2294, "XQuery [query()]: 'xml' is not allowed as a processing instruction target.");
        QueryError("<? p?>", 2278, "XQuery [query()]: A tag name may not start with the character ' '");
    }

    [TestMethod]
    public void Cdata_FoldsIntoTextInsideAnElement_AndIsASyntaxErrorOutside()
    {
        AreEqual("<a>x&lt;y&gt;z</a>", Query("<a>x<![CDATA[<y>]]>z</a>"));
        QueryError("<![CDATA[x]]>", 2209, "XQuery [query()]: Syntax error near '<!'");
    }

    // ---- direct element content --------------------------------------------

    [TestMethod]
    public void BoundaryWhitespace_IsDropped()
    {
        AreEqual("<a>1</a>", Query("<a>  {1}  </a>"));
        AreEqual("<a>  x  1</a>", Query("<a>  x  {1}  </a>"));
        AreEqual("<a>12</a>", Query("<a>{1}  {2}</a>"));
    }

    [TestMethod]
    public void AttributeValue_MixingAnExpressionWithText_Raises9313() =>
        QueryError("<a b=\"&lt;{1}\"/>", 9313, "XQuery [query()]: This version of the server does not support multiple expressions or expressions mixed with strings in an attribute constructor.");

    [TestMethod]
    public void StringLiteral_EntityReferences()
    {
        AreEqual("<a>a&lt;&amp;b</a>", Query("<a>{\"a&lt;&amp;b\"}</a>"));
        QueryError("<e>{attribute z {\"a<&\"}}</e>", 2282, "XQuery [query()]: Invalid entity reference");
    }

    // ---- constructed XML is opaque -------------------------------------------

    [TestMethod]
    [DataRow("(<a/>, <b/>)[2]", "'[]'")]
    [DataRow("<e>{attribute z {1}}</e>/@z", "'/'")]
    [DataRow("count(text {\"\"})", "count()")]
    [DataRow("empty(<a/>)", "empty()")]
    [DataRow("string(<!--ab-->)", "string()")]
    [DataRow("for $i in <a/> return $i", "'for'")]
    [DataRow("let $i := <a/> return $i", "'let'")]
    [DataRow("some $i in <a/> satisfies 1=1", "'some'")]
    [DataRow("<a/> = 1", "data()")]
    [DataRow("<a>1</a> + 1", "data()")]
    [DataRow("<a/> instance of element()", "'instance of'")]
    [DataRow("text {<a>1</a>}", "data()")]
    public void ConstructedXml_Raises2373(string xquery, string operation) =>
        QueryError(xquery, 2373, $"XQuery [query()]: {operation} is not supported with constructed XML");

    [TestMethod]
    public void ConstructedXml_PassesThroughConditionsAndSequences()
    {
        AreEqual("1", Query("if (<a/>) then 1 else 2"));
        AreEqual("false", Query("not(<a/>)"));
        AreEqual("<x>1</x><x>2</x>", Query("/r/x[<a/>]"));
        AreEqual("<w><a/></w>", Query("<w>{<a/>}</w>"));
    }

    [TestMethod]
    public void ConstructedXml_InValue_NamesTheOperationThatReachedIt()
    {
        new Simulation().AssertSqlError(
            "select cast('<r/>' as xml).value('(<a>1</a>)[1]', 'int')", 2373, "XQuery [value()]: '[]' is not supported with constructed XML");
        new Simulation().AssertSqlError(
            "select cast('<r/>' as xml).value('count(<a>1</a>)', 'int')", 2373, "XQuery [value()]: count() is not supported with constructed XML");
    }
}
