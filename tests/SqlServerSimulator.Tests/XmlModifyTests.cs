using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>xml</c> type's <c>.modify()</c> mutator — the
/// three XML-DML statements (<c>insert</c> / <c>delete</c> /
/// <c>replace value of</c>), their content and value expression forms, the
/// static target checks, the two positions real accepts a mutator in
/// (<c>SET @x.modify(…)</c> and an UPDATE's <c>SET col.modify(…)</c>), and the
/// serialization shape a modified instance comes back in. Every expected
/// string and message here was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlModifyTests
{
    /// <summary>Runs <paramref name="dml"/> against <paramref name="instance"/> through an xml variable.</summary>
    private static object? ModifyVariable(string instance, string dml) =>
        new Simulation().ExecuteScalar($"declare @x xml = '{instance}'; set @x.modify('{dml}'); select @x");

    /// <summary>A simulation holding <c>dbo.doc(id int, body xml, tag nvarchar(20))</c> with one row.</summary>
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml, tag nvarchar(20))");
        _ = sim.ExecuteNonQuery("insert dbo.doc values (1, N'<r><a>1</a></r>', N'hello')");
        return sim;
    }

    [TestMethod]
    public void ReplaceValueOf_TextNode_Rewrites() =>
        AreEqual("<r><a>2</a></r>", ModifyVariable("<r><a>1</a></r>", "replace value of (/r/a/text())[1] with \"2\""));

    [TestMethod]
    public void ReplaceValueOf_EmptyString_LeavesSelfClosingElement() =>
        AreEqual("<r><a/></r>", ModifyVariable("<r><a>1</a></r>", "replace value of (/r/a/text())[1] with \"\""));

    [TestMethod]
    public void ReplaceValueOf_SqlVariable_Substitutes() =>
        AreEqual(
            "<r><a>zz</a></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r><a>1</a></r>'; declare @v nvarchar(10) = 'zz'; set @x.modify('replace value of (/r/a/text())[1] with sql:variable(\"@v\")'); select @x"));

    [TestMethod]
    public void ReplaceValueOf_SqlVariable_EscapesMarkupCharacters() =>
        AreEqual(
            "<r><a>x&amp;y</a></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r><a>1</a></r>'; declare @v nvarchar(10) = 'x&y'; set @x.modify('replace value of (/r/a/text())[1] with sql:variable(\"@v\")'); select @x"));

    [TestMethod]
    public void ReplaceValueOf_NonSingletonTarget_Raises2337() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; set @x.modify('replace value of (/r/a/text()) with \"9\"'); select @x",
            2337,
            "XQuery [modify()]: The target of 'replace' must be at most one node, found 'text *'");

    [TestMethod]
    public void ReplaceValueOf_InnerStepPredicateIsStillPlural_Raises2337() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; set @x.modify('replace value of (/r/a)[1]/text() with \"9\"'); select @x",
            2337,
            "XQuery [modify()]: The target of 'replace' must be at most one node, found 'text *'");

    [TestMethod]
    public void ReplaceValueOf_ElementTarget_Raises2356() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; set @x.modify('replace value of (/r/a)[1] with \"9\"'); select @x",
            2356,
            "XQuery [modify()]: The target of 'replace value of' must be a non-metadata attribute or an element with simple typed content, found 'element(a,xdt:untyped) ?'");

    [TestMethod]
    public void ReplaceValueOf_ConstructedXml_Raises9310() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>1</a></r>'; set @x.modify('replace value of (/r/a/text())[1] with <b/>'); select @x",
            9310,
            "XQuery [modify()]: The 'with' clause of 'replace value of' cannot contain constructed XML.");

    [TestMethod]
    public void ReplaceValueOf_NoWithClause_Raises2205() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('replace value of (/r)[1]'); select @x",
            2205,
            "XQuery [modify()]: \"with\" was expected.");

    [TestMethod]
    [DataRow("insert <b>2</b> into (/r)[1]", "<r><a>1</a><b>2</b></r>")]
    [DataRow("insert <b>2</b> as first into (/r)[1]", "<r><b>2</b><a>1</a></r>")]
    [DataRow("insert <b>2</b> as last into (/r)[1]", "<r><a>1</a><b>2</b></r>")]
    [DataRow("insert <b>2</b> before (/r/a)[1]", "<r><b>2</b><a>1</a></r>")]
    [DataRow("insert <b>2</b> after (/r/a)[1]", "<r><a>1</a><b>2</b></r>")]
    public void Insert_PositionalForms(string dml, string expected) =>
        AreEqual(expected, ModifyVariable("<r><a>1</a></r>", dml));

    [TestMethod]
    [DataRow("insert (<b/>, <c/>) into (/r)[1]", "<r><a>1</a><b/><c/></r>")]
    [DataRow("insert attribute n {\"v\"} into (/r/a)[1]", "<r><a n=\"v\">1</a></r>")]
    [DataRow("insert text{\"zz\"} into (/r/a)[1]", "<r><a>1zz</a></r>")]
    [DataRow("insert <!-- hi --> into (/r)[1]", "<r><a>1</a><!-- hi --></r>")]
    [DataRow("insert <?pi data?> into (/r)[1]", "<r><a>1</a><?pi data?></r>")]
    [DataRow("insert <n><m><o>3</o></m></n> into (/r)[1]", "<r><a>1</a><n><m><o>3</o></m></n></r>")]
    [DataRow("insert <n>{{braced}}</n> into (/r)[1]", "<r><a>1</a><n>{braced}</n></r>")]
    [DataRow("insert <n b=\"x>y\"/> into (/r)[1]", "<r><a>1</a><n b=\"x&gt;y\"/></r>")]
    [DataRow("insert <b/> into (/zzz)[1]", "<r><a>1</a></r>")]
    public void Insert_ContentForms(string dml, string expected) =>
        AreEqual(expected, ModifyVariable("<r><a>1</a></r>", dml));

    [TestMethod]
    public void Insert_SqlVariableCarryingXml_InsertsNodes() =>
        AreEqual(
            "<r><b>1</b></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r/>'; declare @v xml = '<b>1</b>'; set @x.modify('insert sql:variable(\"@v\") into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_EnclosedExpressionInElementContent() =>
        AreEqual(
            "<r><n>7</n></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r/>'; declare @v int = 7; set @x.modify('insert <n>{sql:variable(\"@v\")}</n> into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_EnclosedExpressionInAttributeValue() =>
        AreEqual(
            "<r><n a=\"7\"/></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r/>'; declare @v int = 7; set @x.modify('insert <n a=''{sql:variable(\"@v\")}''/> into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_EnclosedExpression_EscapesMarkupCharacters() =>
        AreEqual(
            "<r><n>a&lt;b</n></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r/>'; declare @v nvarchar(20) = 'a<b'; set @x.modify('insert <n>{sql:variable(\"@v\")}</n> into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_DuplicateAttribute_Raises6308() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a n=\"1\"/></r>'; set @x.modify('insert attribute n {\"2\"} into (/r/a)[1]'); select @x",
            6308,
            "XML well-formedness check: Duplicate attribute 'n'. Rewrite your XQuery so it returns well-formed XML.");

    [TestMethod]
    public void Insert_NonSingletonTarget_Raises2226() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a/><a/></r>'; set @x.modify('insert <b/> into (/r/a)'); select @x",
            2226,
            "XQuery [modify()]: The target of 'insert' must be a single node, found 'element(a,xdt:untyped) *'");

    [TestMethod]
    public void Insert_AttributeWithPosition_Raises2258() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a/></r>'; set @x.modify('insert attribute n {\"2\"} before (/r/a)[1]'); select @x",
            2258,
            "XQuery [modify()]: The position may not be specified when inserting an attribute node, found 'attribute(n,xdt:untypedAtomic)'");

    [TestMethod]
    public void Insert_IntoTextNode_Raises2240() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a>t</a></r>'; set @x.modify('insert attribute n {\"2\"} into (/r/a/text())[1]'); select @x",
            2240,
            "XQuery [modify()]: The target of 'insert into' must be an element/document node, found 'text ?'");

    [TestMethod]
    public void Insert_BeforeAttribute_Raises2249() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a n=\"1\"/></r>'; set @x.modify('insert <b/> after (/r/a/@n)[1]'); select @x",
            2249,
            "XQuery [modify()]: The target of 'insert before/after' must be an element/PI/comment/text node, found 'attribute(n,xdt:untypedAtomic) ?'");

    [TestMethod]
    public void Insert_AfterTextNode_Succeeds() =>
        AreEqual("<r><a>t<b/></a></r>", ModifyVariable("<r><a>t</a></r>", "insert <b/> after (/r/a/text())[1]"));

    [TestMethod]
    [DataRow("insert \"abc\" into (/r)[1]", "xs:string")]
    [DataRow("insert 5 into (/r)[1]", "xs:integer")]
    public void Insert_AtomicLiteral_Raises2207(string dml, string typeName) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; set @x.modify('{dml}'); select @x",
            2207,
            $"XQuery [modify()]: Only non-document nodes can be inserted. Found \"{typeName}\".");

    [TestMethod]
    [DataRow("nvarchar(20)", "'<b/>'", "xs:string ?")]
    [DataRow("int", "5", "xs:int ?")]
    [DataRow("bigint", "5", "xs:long ?")]
    [DataRow("decimal(9, 2)", "5", "xs:decimal ?")]
    public void Insert_AtomicVariable_Raises2207(string declaredType, string literal, string typeName) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; declare @v {declaredType} = {literal}; set @x.modify('insert sql:variable(\"@v\") into (/r)[1]'); select @x",
            2207,
            $"XQuery [modify()]: Only non-document nodes can be inserted. Found \"{typeName}\".");

    [TestMethod]
    [DataRow("delete /r/a", "<r/>")]
    [DataRow("delete /r/a/@n", "<r><a>t</a></r>")]
    [DataRow("delete /r/a/text()", "<r><a n=\"1\"/></r>")]
    [DataRow("delete (/r/a)[1]", "<r/>")]
    [DataRow("delete /r/zzz", "<r><a n=\"1\">t</a></r>")]
    public void Delete_RemovesEveryMatch(string dml, string expected) =>
        AreEqual(expected, ModifyVariable("<r><a n=\"1\">t</a></r>", dml));

    [TestMethod]
    public void Delete_RootElement_LeavesEmptyInstance() =>
        AreEqual(string.Empty, new Simulation().ExecuteScalar("declare @x xml = '<r><a/></r>'; set @x.modify('delete /r'); select @x"));

    [TestMethod]
    public void Delete_ContextNode_Raises2264() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><a/></r>'; set @x.modify('delete .'); select @x",
            2264,
            "XQuery [modify()]: Only non-document nodes may be deleted, found 'document { (element(*,xdt:untyped) ? & text ? & comment ? & processing-instruction ?) * }'");

    [TestMethod]
    public void Modify_NullVariable_Raises5302() =>
        new Simulation().AssertSqlError(
            "declare @x xml; set @x.modify('insert <b/> into (/r)[1]'); select @x",
            5302,
            "Mutator 'modify()' on '@x' cannot be called on a null value.");

    [TestMethod]
    public void Modify_NullColumnValue_Raises5302()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("insert dbo.doc values (2, null, null)");
        sim.AssertSqlError(
            "update dbo.doc set body.modify('insert <b/> into (/r)[1]')",
            5302,
            "Mutator 'modify()' on 'body' cannot be called on a null value.");
    }

    [TestMethod]
    [DataRow("select @x.modify('insert <b/> into (/r)[1]')")]
    [DataRow("set @x = @x.modify('insert <b/> into (/r)[1]')")]
    [DataRow("select 1 where @x.modify('insert <b/> into (/r)[1]') = 1")]
    public void Modify_InValuePosition_Raises8137(string statement) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; {statement}",
            8137,
            "Incorrect use of the XML data type method 'modify'. A non-mutator method is expected in this context.");

    [TestMethod]
    public void NonMutatorMethod_InMutatorPosition_Raises8113() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.value('/r', 'int')",
            8113,
            "Incorrect use of the XML data type method 'value'. A mutator method is expected in this context.");

    [TestMethod]
    public void Modify_OnNonXmlVariable_Raises258()
    {
        var ex = new Simulation().AssertSqlError("declare @x nvarchar(50) = '<r/>'; set @x.modify('insert <b/> into (/r)[1]')", 258);
        AreEqual("Cannot call methods on nvarchar.", ex.Message);
        AreEqual((byte)15, ex.Class);
    }

    [TestMethod]
    public void Modify_OnNonXmlColumn_Raises258() =>
        Seeded().AssertSqlError("update dbo.doc set id.modify('insert <b/> into (/r)[1]')", 258, "Cannot call methods on int.");

    [TestMethod]
    public void Modify_OnUnknownColumn_Raises207() =>
        Seeded().AssertSqlError("update dbo.doc set nope.modify('insert <b/> into (/r)[1]')", 207, "Invalid column name 'nope'.");

    [TestMethod]
    public void Modify_NotDataManipulation_Raises6305() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('/r')",
            6305,
            "XQuery data manipulation expression required in XML data type method.");

    [TestMethod]
    public void Modify_UnterminatedStatement_Raises2209() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('delete')",
            2209,
            "XQuery [modify()]: Syntax error near '<eof>'");

    [TestMethod]
    public void SetVariable_Modify_SetsRowCountToOne() =>
        AreEqual(1, new Simulation().ExecuteScalar("declare @x xml = '<r/>'; set @x.modify('insert <b/> into (/r)[1]'); select @@rowcount"));

    [TestMethod]
    public void Update_Modify_RewritesOnlyMatchedRows()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("insert dbo.doc values (2, N'<r><a>2</a></r>', N'other')");
        AreEqual(1, sim.ExecuteNonQuery("update dbo.doc set body.modify('replace value of (/r/a/text())[1] with \"z\"') where id = 1"));
        AreEqual("<r><a>z</a></r>", sim.ExecuteScalar("select body from dbo.doc where id = 1"));
        AreEqual("<r><a>2</a></r>", sim.ExecuteScalar("select body from dbo.doc where id = 2"));
    }

    [TestMethod]
    public void Update_Modify_NoMatchingRows_ReportsZeroRowCount() =>
        AreEqual(0, Seeded().ExecuteScalar("update dbo.doc set body.modify('insert <b/> into (/r)[1]') where id = 99; select @@rowcount"));

    [TestMethod]
    [DataRow("body.modify('insert <b/> into (/r)[1]'), tag = N'z'")]
    [DataRow("tag = N'z', body.modify('insert <b/> into (/r)[1]')")]
    public void Update_Modify_ComposesWithOrdinarySetClauses(string setList)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery($"update dbo.doc set {setList} where id = 1");
        AreEqual("<r><a>1</a><b/></r>", sim.ExecuteScalar("select body from dbo.doc"));
        AreEqual("z", sim.ExecuteScalar("select tag from dbo.doc"));
    }

    [TestMethod]
    public void Update_Modify_SqlColumnReadsTheRowBeingEdited()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("update dbo.doc set body.modify('replace value of (/r/a/text())[1] with sql:column(\"tag\")')");
        AreEqual("<r><a>hello</a></r>", sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void Update_Modify_SqlColumnOfNonXmlTypeInContentPosition_Raises2207() =>
        Seeded().AssertSqlError(
            "update dbo.doc set body.modify('insert sql:column(\"id\") into (/r)[1]')",
            2207,
            "XQuery [dbo.doc.body.modify()]: Only non-document nodes can be inserted. Found \"xs:int ?\".");

    [TestMethod]
    public void Update_Modify_OutputProjectsBothSides()
    {
        var sim = Seeded();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "update dbo.doc set body.modify('insert <b/> into (/r)[1]') output inserted.body as ins, deleted.body as del";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("<r><a>1</a><b/></r>", reader.GetString(0));
        AreEqual("<r><a>1</a></r>", reader.GetString(1));
    }

    [TestMethod]
    public void Update_Modify_FiresAfterTrigger()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create trigger dbo.tr_doc on dbo.doc after update as insert dbo.doc values (9, (select top 1 body from inserted), N'trig')");
        _ = sim.ExecuteNonQuery("update dbo.doc set body.modify('insert <b/> into (/r)[1]') where id = 1");
        AreEqual("<r><a>1</a><b/></r>", sim.ExecuteScalar("select body from dbo.doc where id = 9"));
    }

    [TestMethod]
    public void Update_Modify_RollsBackWithTheTransaction()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("begin tran; update dbo.doc set body.modify('insert <b/> into (/r)[1]'); rollback");
        AreEqual("<r><a>1</a></r>", sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void Update_Modify_ThroughStoredProcedure()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create procedure dbo.p_touch as update dbo.doc set body.modify('insert <b/> into (/r)[1]')");
        _ = sim.ExecuteNonQuery("exec dbo.p_touch");
        AreEqual("<r><a>1</a><b/></r>", sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void Update_Modify_ThroughUpdatableView()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create view dbo.v_doc as select id, body from dbo.doc");
        _ = sim.ExecuteNonQuery("update dbo.v_doc set body.modify('insert <b/> into (/r)[1]')");
        AreEqual("<r><a>1</a><b/></r>", sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void Update_Modify_OnTempTableAndTableVariable()
    {
        var sim = new Simulation();
        AreEqual(
            "<r><b/></r>",
            sim.ExecuteScalar("create table #t (x xml); insert #t values (N'<r/>'); update #t set x.modify('insert <b/> into (/r)[1]'); select x from #t"));
        AreEqual(
            "<r><b/></r>",
            sim.ExecuteScalar("declare @t table (x xml); insert @t values (N'<r/>'); update @t set x.modify('insert <b/> into (/r)[1]'); select x from @t"));
    }

    [TestMethod]
    public void Update_Modify_QualifiedColumnName_Raises102() =>
        _ = Seeded().AssertSqlError("update d set d.body.modify('insert <b/> into (/r)[1]') from dbo.doc d", 102);

    [TestMethod]
    public void Modify_DefaultElementNamespaceProlog_Resolves() =>
        AreEqual(
            "<r xmlns=\"urn:d\"><a>9</a></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r xmlns=\"urn:d\"><a>1</a></r>'; set @x.modify('declare default element namespace \"urn:d\"; replace value of (/r/a/text())[1] with \"9\"'); select @x"));

    [TestMethod]
    public void Insert_ConstructorTakesThePrologDefaultNamespace() =>
        AreEqual(
            "<r xmlns=\"urn:d\"><a/><b/></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r xmlns=\"urn:d\"><a/></r>'; set @x.modify('declare default element namespace \"urn:d\"; insert <b/> into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_UnqualifiedConstructorUnderNamespacedParent_DeclaresEmptyDefault() =>
        AreEqual(
            "<r xmlns=\"urn:d\"><a/><b xmlns=\"\"/></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r xmlns=\"urn:d\"><a/></r>'; set @x.modify('declare namespace d=\"urn:d\"; insert <b/> into (/d:r)[1]'); select @x"));

    [TestMethod]
    public void Insert_ConstructorTakesAPrologPrefix() =>
        AreEqual(
            "<r><p:b xmlns:p=\"urn:x\"/></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r/>'; set @x.modify('declare namespace p=\"urn:x\"; insert <p:b/> into (/r)[1]'); select @x"));

    [TestMethod]
    public void Modify_PrefixedNamespace_RoundTrips() =>
        AreEqual(
            "<r xmlns:p=\"urn:x\"><p:a>1</p:a><p:b xmlns:p=\"urn:x\"/></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r xmlns:p=\"urn:x\"><p:a>1</p:a></r>'; set @x.modify('declare namespace p=\"urn:x\"; insert <p:b xmlns:p=\"urn:x\"/> into (/r)[1]'); select @x"));

    [TestMethod]
    [DataRow("<r>  <a>1</a>   <b   c = \"2\"  />  </r>", "<r><a>1</a><b c=\"2\"/><z/></r>")]
    [DataRow("<?xml version=\"1.0\"?><r><a>1</a></r>", "<r><a>1</a><z/></r>")]
    [DataRow("<r><a><![CDATA[x<y]]></a></r>", "<r><a>x&lt;y</a><z/></r>")]
    [DataRow("<r><a></a></r>", "<r><a/><z/></r>")]
    public void Modify_ResultIsNormalizedLikeReal(string instance, string expected) =>
        AreEqual(expected, ModifyVariable(instance, "insert <z/> into (/r)[1]"));

    [TestMethod]
    [DataRow("insert <b/> after (/r)[1]", "<r/><b/>")]
    [DataRow("insert <b/> before (/r)[1]", "<b/><r/>")]
    public void Insert_ElementBesideTopLevelElement_ProducesFragment(string dml, string expected) =>
        AreEqual(expected, ModifyVariable("<r/>", dml));

    [TestMethod]
    public void Insert_IntoTheDocumentNode_AppendsAtTopLevel() =>
        AreEqual("<a/><c/>", ModifyVariable("<a/>", "insert <c/> into (/)[1]"));

    [TestMethod]
    public void Insert_AfterASecondTopLevelElement_Appends() =>
        AreEqual("<a/><b/><c/>", ModifyVariable("<a/><b/>", "insert <c/> after (/b)[1]"));

    [TestMethod]
    public void Update_InsertAfterTopLevelElement_StoresAFragment()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (body xml); insert dbo.doc values (N'<r/>')");
        _ = sim.ExecuteNonQuery("update dbo.doc set body.modify('insert <b/> after (/r)[1]')");
        AreEqual("<r/><b/>", sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void Delete_TopLevelElementOfAFragment_LeavesTheRest() =>
        AreEqual("<b/>", ModifyVariable("<a/><b/>", "delete /a"));

    [TestMethod]
    public void Insert_CommentBesideTopLevelElement_Succeeds() =>
        AreEqual("<r/><!--c-->", ModifyVariable("<r/>", "insert <!--c--> after (/r)[1]"));

    // ---- attribute insert position ----------------------------------------

    [TestMethod]
    [DataRow("<a/>", "insert attribute z {9} into (/a)[1]", "<a z=\"9\"/>")]
    [DataRow("<a b=\"1\"/>", "insert attribute z {9} into (/a)[1]", "<a b=\"1\" z=\"9\"/>")]
    [DataRow("<a b=\"1\" d=\"2\"/>", "insert attribute z {9} into (/a)[1]", "<a b=\"1\" z=\"9\" d=\"2\"/>")]
    [DataRow("<a m=\"1\" n=\"2\" o=\"3\" p=\"4\"/>", "insert attribute z {9} into (/a)[1]", "<a m=\"1\" z=\"9\" n=\"2\" o=\"3\" p=\"4\"/>")]
    [DataRow("<a m=\"1\" n=\"2\" o=\"3\" p=\"4\"/>", "insert (attribute z {9}, attribute y {8}) into (/a)[1]", "<a m=\"1\" z=\"9\" n=\"2\" y=\"8\" o=\"3\" p=\"4\"/>")]
    [DataRow("<a m=\"1\" n=\"2\" o=\"3\" p=\"4\"/>", "insert (attribute z {9}, attribute y {8}, attribute w {7}) into (/a)[1]", "<a m=\"1\" z=\"9\" n=\"2\" y=\"8\" o=\"3\" w=\"7\" p=\"4\"/>")]
    [DataRow("<a b=\"1\"/>", "insert (attribute z {9}, attribute y {8}, attribute w {7}) into (/a)[1]", "<a b=\"1\" z=\"9\" y=\"8\" w=\"7\"/>")]
    [DataRow("<a m=\"1\" n=\"2\"/>", "insert (attribute q {1}, attribute r {2}, attribute s {3}, attribute t {4}, attribute u {5}) into (/a)[1]", "<a m=\"1\" q=\"1\" n=\"2\" r=\"2\" s=\"3\" t=\"4\" u=\"5\"/>")]
    [DataRow("<a xmlns:p=\"u\" b=\"1\" d=\"2\"/>", "insert attribute z {9} into (/a)[1]", "<a xmlns:p=\"u\" z=\"9\" b=\"1\" d=\"2\"/>")]
    [DataRow("<a m=\"1\" n=\"2\"/>", "insert attribute z {9} as first into (/a)[1]", "<a m=\"1\" z=\"9\" n=\"2\"/>")]
    public void Insert_AttributeThreadsIntoTheNodeOrder(string instance, string dml, string expected) =>
        AreEqual(expected, ModifyVariable(instance, dml));

    [TestMethod]
    public void Insert_AttributeAcrossStatements_KeepsThreading() =>
        AreEqual(
            "<a m=\"1\" w=\"7\" y=\"8\" z=\"9\" n=\"2\" o=\"3\" p=\"4\"/>",
            new Simulation().ExecuteScalar("""
                declare @x xml = '<a m="1" n="2" o="3" p="4"/>';
                set @x.modify('insert attribute z {9} into (/a)[1]');
                set @x.modify('insert attribute y {8} into (/a)[1]');
                set @x.modify('insert attribute w {7} into (/a)[1]');
                select @x
                """));

    // ---- computed constructors --------------------------------------------

    [TestMethod]
    [DataRow("insert element n {\"v\"} into (/r)[1]", "<r><n>v</n></r>")]
    [DataRow("insert element n {} into (/r)[1]", "<r><n/></r>")]
    [DataRow("insert element n {element m {1}} into (/r)[1]", "<r><n><m>1</m></n></r>")]
    [DataRow("insert element n {attribute a {1}} into (/r)[1]", "<r><n a=\"1\"/></r>")]
    [DataRow("insert element n {<c/>} into (/r)[1]", "<r><n><c/></n></r>")]
    [DataRow("insert element n {\"a\",\"b\"} into (/r)[1]", "<r><n>a b</n></r>")]
    public void Insert_ComputedElementConstructor(string dml, string expected) =>
        AreEqual(expected, ModifyVariable("<r/>", dml));

    [TestMethod]
    public void Insert_ComputedElementTakesThePrologDefaultNamespace() =>
        AreEqual(
            "<r xmlns=\"urn:d\"><n>5</n></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r xmlns=\"urn:d\"/>'; set @x.modify('declare default element namespace \"urn:d\"; insert element n {5} into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_ComputedElementTakesAPrologPrefix() =>
        AreEqual(
            "<r><p:m xmlns:p=\"urn:x\">1</p:m></r>",
            new Simulation().ExecuteScalar("declare @x xml = '<r/>'; set @x.modify('declare namespace p=\"urn:x\"; insert element p:m {1} into (/r)[1]'); select @x"));

    [TestMethod]
    public void Insert_ComputedElementWithUndeclaredPrefix_Raises2229() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('insert element n:m {1} into (/r)[1]'); select @x",
            2229,
            "XQuery [modify()]: The name \"n\" does not denote a namespace.");

    [TestMethod]
    [DataRow("insert element {\"a\"} {1} into (/r)[1]")]
    [DataRow("insert attribute {\"z\"} {1} into (/r)[1]")]
    public void Insert_ComputedNameExpression_Raises9315(string dml) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; set @x.modify('{dml}'); select @x",
            9315,
            "XQuery [modify()]: Only constant expressions are supported for the name expression of computed element and attribute constructors.");

    [TestMethod]
    public void Insert_ComputedCommentConstructor_Raises9326() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('insert comment {\"c\"} into (/r)[1]'); select @x",
            9326,
            "XQuery [modify()]: Computed comment constructors are not supported.");

    [TestMethod]
    public void Insert_ComputedProcessingInstructionConstructor_Raises9325() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; set @x.modify('insert processing-instruction p {\"d\"} into (/r)[1]'); select @x",
            9325,
            "XQuery [modify()]: Computed processing instruction constructors are not supported.");

    [TestMethod]
    public void Insert_ComputedTextConstructor_Succeeds() =>
        AreEqual("<r>t</r>", ModifyVariable("<r/>", "insert text {\"t\"} into (/r)[1]"));

    // ---- statement-shape diagnostics --------------------------------------

    [TestMethod]
    [DataRow("/r")]
    [DataRow("1+1")]
    [DataRow("foo")]
    [DataRow("count(/r)")]
    [DataRow("deleted /r")]
    [DataRow("<a/>")]
    [DataRow("for $i in /r return $i")]
    [DataRow("let $i := 1 return $i")]
    [DataRow("if (1=1) then 1 else 2")]
    public void Modify_ArgumentParsesAsXQueryButIsNotXmlDml_Raises6305(string xquery) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; set @x.modify('{xquery}'); select @x",
            6305,
            "XQuery data manipulation expression required in XML data type method.");

    [TestMethod]
    [DataRow("(")]
    [DataRow("insert")]
    [DataRow("delete")]
    [DataRow("/r[")]
    public void Modify_ArgumentIsNotXQueryAtAll_Raises2209(string xquery) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; set @x.modify('{xquery}'); select @x",
            2209,
            "XQuery [modify()]: Syntax error near '<eof>'");

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Modify_EmptyArgument_Raises6306(string xquery) =>
        new Simulation().AssertSqlError(
            $"declare @x xml = '<r/>'; set @x.modify('{xquery}'); select @x",
            6306,
            "Invalid XQuery expression passed to XML data type method.");

    /// <summary>
    /// A collection typing <c>t:b</c> as <c>xsd:string</c> and <c>t:n</c> as
    /// <c>xsd:decimal</c> — both simple content, so both are legal
    /// <c>replace value of</c> targets where the untyped spelling is Msg 2356.
    /// </summary>
    private const string SimpleContentCollection = """
        create xml schema collection tsc as N'<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
          targetNamespace="urn:t" xmlns:t="urn:t" elementFormDefault="qualified">
          <xsd:element name="r"><xsd:complexType><xsd:sequence>
            <xsd:element name="b" type="xsd:string" minOccurs="0"/>
            <xsd:element name="n" type="xsd:decimal" minOccurs="0"/>
          </xsd:sequence></xsd:complexType></xsd:element></xsd:schema>'
        """;

    private static Simulation WithSimpleContentCollection()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(SimpleContentCollection);
        return sim;
    }

    /// <summary>
    /// An element the receiver's collection types with simple content is a
    /// legal target — the schema binding is the whole difference between this
    /// and <see cref="ReplaceValueOf_UntypedElementTarget_Raises2356"/>.
    /// </summary>
    [TestMethod]
    public void ReplaceValueOf_TypedElementTarget_Rewrites() =>
        AreEqual(
            "<t:r xmlns:t=\"urn:t\"><t:b>bye</t:b><t:n>1.5</t:n></t:r>",
            WithSimpleContentCollection().ExecuteScalar(
                """
                declare @x xml(tsc) = N'<t:r xmlns:t="urn:t"><t:b>hi</t:b><t:n>1.5</t:n></t:r>';
                set @x.modify('declare namespace t="urn:t"; replace value of (/t:r/t:b)[1] with "bye"');
                select @x
                """));

    /// <summary>
    /// The <c>with</c> clause is a whole XQuery expression, not a term list —
    /// the shape AdventureWorks' <c>Sales.iduSalesOrderDetail</c> writes.
    /// </summary>
    [TestMethod]
    public void ReplaceValueOf_ArithmeticOverTheInstance_Rewrites() =>
        AreEqual(
            "<t:r xmlns:t=\"urn:t\"><t:b>hi</t:b><t:n>3.5</t:n></t:r>",
            WithSimpleContentCollection().ExecuteScalar(
                """
                declare @x xml(tsc) = N'<t:r xmlns:t="urn:t"><t:b>hi</t:b><t:n>1.5</t:n></t:r>';
                set @x.modify('declare namespace t="urn:t"; replace value of (/t:r/t:n)[1] with data(/t:r/t:n)[1] + 2');
                select @x
                """));

    /// <summary>The typed binding reaches a column receiver too, not only a variable.</summary>
    [TestMethod]
    public void ReplaceValueOf_TypedColumnTarget_Rewrites()
    {
        var sim = WithSimpleContentCollection();
        _ = sim.ExecuteNonQuery("create table dbo.u3 (id int primary key, d xml(tsc))");
        _ = sim.ExecuteNonQuery("insert dbo.u3 values (1, N'<t:r xmlns:t=\"urn:t\"><t:b>hi</t:b></t:r>')");
        _ = sim.ExecuteNonQuery("update dbo.u3 set d.modify('declare namespace t=\"urn:t\"; replace value of (/t:r/t:b)[1] with \"bye\"')");
        AreEqual("<t:r xmlns:t=\"urn:t\"><t:b>bye</t:b></t:r>", sim.ExecuteScalar("select d from dbo.u3"));
    }

    /// <summary>Untyped <c>xml</c> keeps real's refusal — an element there holds no value.</summary>
    [TestMethod]
    public void ReplaceValueOf_UntypedElementTarget_Raises2356() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r><b>hi</b></r>'; set @x.modify('replace value of (/r/b)[1] with \"bye\"'); select @x",
            2356,
            "XQuery [modify()]: The target of 'replace value of' must be a non-metadata attribute or an element with simple typed content, found 'element(b,xdt:untyped) ?'");

    /// <summary>
    /// The mutator's target is the write target, not whatever the FROM clause
    /// also happens to call <c>d</c> — AdventureWorks' <c>Person.iuPerson</c>
    /// updates <c>Person.Person</c> from <c>inserted</c>, which carries a
    /// <c>Demographics</c> of its own.
    /// </summary>
    [TestMethod]
    public void JoinedUpdate_UnqualifiedMutatorTarget_BindsToTheWriteTarget()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.m1 (id int primary key, d xml)");
        _ = sim.ExecuteNonQuery("create table dbo.m3 (id int primary key, d xml)");
        _ = sim.ExecuteNonQuery("insert dbo.m1 values (1, N'<r a=\"x\"/>')");
        _ = sim.ExecuteNonQuery("insert dbo.m3 values (1, N'<r a=\"other\"/>')");
        _ = sim.ExecuteNonQuery("update dbo.m1 set d.modify('replace value of (/r/@a)[1] with \"q\"') from dbo.m3 where dbo.m1.id = dbo.m3.id");
        AreEqual("<r a=\"q\"/>", sim.ExecuteScalar("select d from dbo.m1"));
    }

    /// <summary>
    /// <c>sql:column</c> takes the multi-part form, and it binds against the
    /// whole statement's scope rather than the write target alone — the FROM
    /// clause it names parses after the SET list.
    /// </summary>
    [TestMethod]
    public void JoinedUpdate_SqlColumnNamesAJoinedSource_Substitutes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.m1 (id int primary key, d xml)");
        _ = sim.ExecuteNonQuery("create table dbo.m2 (id int primary key, tag nvarchar(20))");
        _ = sim.ExecuteNonQuery("insert dbo.m1 values (1, N'<r a=\"x\"><b>1</b></r>')");
        _ = sim.ExecuteNonQuery("insert dbo.m2 values (1, N'zz')");
        _ = sim.ExecuteNonQuery("update dbo.m1 set d.modify('replace value of (/r/@a)[1] with sql:column(\"m2.tag\")') from dbo.m2 where dbo.m1.id = dbo.m2.id");
        AreEqual("<r a=\"zz\"><b>1</b></r>", sim.ExecuteScalar("select d from dbo.m1"));
    }

    [TestMethod]
    public void Modify_OnTypedColumn_EditsWithoutSchemaValidation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"create xml schema collection xsc as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema""><xs:element name=""r"" /></xs:schema>'");
        _ = sim.ExecuteNonQuery("create table dbo.typed (body xml(xsc))");
        _ = sim.ExecuteNonQuery("insert dbo.typed values (N'<r><a>1</a></r>')");
        _ = sim.ExecuteNonQuery("update dbo.typed set body.modify('insert <undeclared/> into (/r)[1]')");
        AreEqual("<r><a>1</a><undeclared/></r>", sim.ExecuteScalar("select body from dbo.typed"));
    }
}
