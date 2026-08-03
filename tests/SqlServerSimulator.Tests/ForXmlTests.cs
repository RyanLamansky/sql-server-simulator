using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// FOR XML result serialization — RAW / AUTO / PATH modes, ELEMENTS / XSINIL /
/// ROOT options, value formatting, position-dependent escaping, NULL handling,
/// and the parse/build error set. Expected strings are probe-confirmed against
/// SQL Server 2025 (local sweepdb, tables t / u).
/// </summary>
[TestClass]
public sealed class ForXmlTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, a int, b int, s varchar(20), n nvarchar(20), d datetime);
            insert t values (1,10,20,'x','y','2020-01-01'),(2,30,40,'z','w','2021-06-15'),(3,50,60,'q','r','2022-03-10');
            create table u (id int, a int);
            insert u values (1,10),(2,20);
            """);
        return sim;
    }

    private static string Xml(string query) => (string)Seeded().ExecuteScalar(query)!;

    /// <summary>
    /// Parent / child / grandchild fixture for the AUTO nesting matrix, with
    /// two parents sharing a name (collapse by value), a childless parent
    /// (outer join), a NULL child column, and an all-NULL middle table. The
    /// queries alias the tables <c>p</c> / <c>c</c> / <c>g</c> / <c>nl</c>, so
    /// the element names match the probe transcript verbatim.
    /// </summary>
    private const string JoinFixture = """
        create table pp (id int, nm nvarchar(20));
        create table cc (id int, pid int, cnm nvarchar(20), amt decimal(9,2));
        create table gg (id int, pid int, gnm nvarchar(20));
        create table pp2 (id int, nm nvarchar(20));
        insert pp values (1,'alpha'),(2,'beta'),(3,'gamma'),(4,'alpha');
        insert cc values (10,1,'a1',1.5),(11,1,'a2',2.5),(12,2,'b1',null),(13,4,'d1',9.0);
        insert gg values (100,1,'g1'),(101,1,'g2');
        insert pp2 values (1,null),(2,null),(3,null),(4,null);
        """;

    private static string JoinXml(string query)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(JoinFixture);
        return (string)sim.ExecuteScalar(query)!;
    }

    // ---- RAW ----

    [TestMethod]
    public void Raw_AttributeCentric()
        => AreEqual("""<row id="1" a="10"/><row id="2" a="30"/><row id="3" a="50"/>""",
            Xml("select id, a from t for xml raw"));

    [TestMethod]
    public void Raw_NamedElement()
        => AreEqual("""<item id="1" a="10"/><item id="2" a="30"/><item id="3" a="50"/>""",
            Xml("select id, a from t for xml raw('item')"));

    [TestMethod]
    public void Raw_Elements()
        => AreEqual("<row><id>1</id><a>10</a></row><row><id>2</id><a>30</a></row><row><id>3</id><a>50</a></row>",
            Xml("select id, a from t for xml raw, elements"));

    [TestMethod]
    public void Raw_UnnamedColumn_Msg6809()
    {
        var ex = Seeded().AssertSqlError("select id, a+b from t for xml raw", 6809);
        Contains("Unnamed tables cannot be used as XML identifiers", ex.Message);
    }

    // ---- AUTO ----

    [TestMethod]
    public void Auto_Flat_AttributeCentric()
        => AreEqual("""<t id="1" a="10"/><t id="2" a="30"/><t id="3" a="50"/>""",
            Xml("select id, a from t for xml auto"));

    [TestMethod]
    public void Auto_Flat_Elements()
        => AreEqual("<t><id>1</id><a>10</a></t><t><id>2</id><a>30</a></t><t><id>3</id><a>50</a></t>",
            Xml("select id, a from t for xml auto, elements"));

    [TestMethod]
    public void Auto_UnaliasedTable_KeepsWrittenName()
        => AreEqual("""<dbo.pp id="1"/><dbo.pp id="2"/><dbo.pp id="3"/><dbo.pp id="4"/>""",
            JoinXml("select id from dbo.pp order by id for xml auto"));

    [TestMethod]
    public void Auto_NoFromClause_Msg6800()
    {
        var ex = new Simulation().AssertSqlError("select 1 as a, 2 as b for xml auto", 6800);
        Contains("requires at least one table", ex.Message);
    }

    // ---- AUTO over a set-operation result ----

    [TestMethod]
    public void Auto_SetOperation_NamesFirstBranchSource()
        => AreEqual("""<t id="1" a="10"/><t id="2" a="30"/><t id="3" a="50"/><t id="1" a="10"/><t id="2" a="20"/>""",
            Xml("select id, a from t union all select id, a from u for xml auto"));

    [TestMethod]
    public void Auto_SetOperation_NamesFirstBranchAlias()
        => AreEqual("""<t1 id="1" a="10"/><t1 id="2" a="30"/><t1 id="3" a="50"/><t1 id="1" a="10"/><t1 id="2" a="20"/>""",
            Xml("select id, a from t t1 union all select id, a from u u1 for xml auto"));

    [TestMethod]
    public void Auto_SetOperation_TakesFirstBranchColumnAliases()
        => AreEqual("""<t x="1" y="10"/><t x="2" y="30"/><t x="3" y="50"/><t x="1" y="10"/><t x="2" y="20"/>""",
            Xml("select id as x, a as y from t union all select id, a from u for xml auto"));

    /// <summary>
    /// A set operator flattens AUTO's nesting: the first branch's join
    /// contributes only its <em>first</em> source's name, and every column
    /// lands on that one element.
    /// </summary>
    [TestMethod]
    public void Auto_SetOperation_FlattensJoinNesting()
        => AreEqual("""<t id="1" a="10"/><t id="2" a="20"/><t id="1" a="10"/><t id="2" a="20"/>""",
            Xml("select t.id, u.a from t join u on t.id=u.id union all select id, a from u for xml auto"));

    [TestMethod]
    public void Auto_SetOperation_SurvivesOrderBy()
        => AreEqual("""<t id="3" a="50"/><t id="2" a="30"/><t id="2" a="20"/><t id="1" a="10"/><t id="1" a="10"/>""",
            Xml("select id, a from t union all select id, a from u order by id desc for xml auto"));

    [TestMethod]
    public void Auto_SetOperation_WithElementsAndRoot()
        => AreEqual("<r><t><id>1</id><a>10</a></t><t><id>2</id><a>30</a></t><t><id>3</id><a>50</a></t><t><id>1</id><a>10</a></t><t><id>2</id><a>20</a></t></r>",
            Xml("select id, a from t union all select id, a from u for xml auto, elements, root('r')"));

    [TestMethod]
    public void Auto_SetOperation_FromLessFirstBranch_Msg6800()
    {
        var ex = Seeded().AssertSqlError("select 9 as id union all select id from t for xml auto", 6800);
        Contains("requires at least one table", ex.Message);
    }

    // ---- AUTO join nesting ----

    [TestMethod]
    public void AutoNesting_SecondTableNestsUnderFirst()
        => AreEqual("""<p id="1" nm="alpha"><c id="10" cnm="a1"/><c id="11" cnm="a2"/></p><p id="2" nm="beta"><c id="12" cnm="b1"/></p><p id="4" nm="alpha"><c id="13" cnm="d1"/></p>""",
            JoinXml("select p.id, p.nm, c.id, c.cnm from pp p join cc c on c.pid=p.id order by p.id, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_ColumnsGroupByTable_NotSelectOrder()
        => AreEqual("""<p id="1" nm="alpha"><c cnm="a1"/><c cnm="a2"/></p><p id="2" nm="beta"><c cnm="b1"/></p><p id="4" nm="alpha"><c cnm="d1"/></p>""",
            JoinXml("select p.id, c.cnm, p.nm from pp p join cc c on c.pid=p.id order by p.id, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_GroupedColumnsPrecedeChildren_UnderElements()
        => AreEqual("<p><id>1</id><nm>alpha</nm><c><cnm>a1</cnm></c><c><cnm>a2</cnm></c></p><p><id>2</id><nm>beta</nm><c><cnm>b1</cnm></c></p><p><id>4</id><nm>alpha</nm><c><cnm>d1</cnm></c></p>",
            JoinXml("select p.id, c.cnm, p.nm from pp p join cc c on c.pid=p.id order by p.id, c.id for xml auto, elements"));

    [TestMethod]
    public void AutoNesting_LevelOrderFollowsFirstColumn()
        => AreEqual("""<c cnm="a1"><p nm="alpha"/></c><c cnm="a2"><p nm="alpha"/></c><c cnm="b1"><p nm="beta"/></c><c cnm="d1"><p nm="alpha"/></c>""",
            JoinXml("select c.cnm, p.nm from pp p join cc c on c.pid=p.id order by c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_OuterJoinNullSide_EmitsEmptyElement()
        => AreEqual("""<p id="1" nm="alpha"><c cnm="a1"/><c cnm="a2"/></p><p id="2" nm="beta"><c cnm="b1"/></p><p id="3" nm="gamma"><c/></p><p id="4" nm="alpha"><c cnm="d1"/></p>""",
            JoinXml("select p.id, p.nm, c.cnm from pp p left join cc c on c.pid=p.id order by p.id, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_ThreeTables_NestLinearly()
        => AreEqual("""<p id="1"><c cnm="a1"><g gnm="g1"/><g gnm="g2"/></c><c cnm="a2"><g gnm="g1"/><g gnm="g2"/></c></p>""",
            JoinXml("select p.id, c.cnm, g.gnm from pp p join cc c on c.pid=p.id join gg g on g.pid=p.id order by p.id, c.id, g.id for xml auto"));

    [TestMethod]
    public void AutoNesting_ComputedColumn_JoinsPrecedingTable()
        => AreEqual("""<p id="1"><c cnm="a1" calc="alphaX"/><c cnm="a2" calc="alphaX"/></p><p id="2"><c cnm="b1" calc="betaX"/></p><p id="4"><c cnm="d1" calc="alphaX"/></p>""",
            JoinXml("select p.id, c.cnm, p.nm + 'X' as calc from pp p join cc c on c.pid=p.id order by p.id, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_ComputedColumnFirst_JoinsFirstLevel()
        => AreEqual("""<p calc="alphaX" id="1"><c cnm="a1"/><c cnm="a2"/></p><p calc="betaX" id="2"><c cnm="b1"/></p><p calc="alphaX" id="4"><c cnm="d1"/></p>""",
            JoinXml("select p.nm + 'X' as calc, p.id, c.cnm from pp p join cc c on c.pid=p.id order by p.id, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_LeadingLiteral_JoinsWhicheverLevelComesFirst()
        => AreEqual("""<c k="lit" cnm="a1"><p nm="alpha"/></c><c k="lit" cnm="a2"><p nm="alpha"/></c><c k="lit" cnm="b1"><p nm="beta"/></c><c k="lit" cnm="d1"><p nm="alpha"/></c>""",
            JoinXml("select 'lit' as k, c.cnm, p.nm from pp p join cc c on c.pid=p.id order by c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_CastOfInnerColumn_IsComputed_SoThatTableHasNoLevel()
        => AreEqual("""<p id="1" cs="10"/><p id="1" cs="11"/>""",
            JoinXml("select p.id, cast(c.id as nvarchar(5)) as cs from pp p join cc c on c.pid=p.id where p.id=1 order by c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_AllComputed_NamesTheFirstSource()
        => AreEqual("""<pp a="1"/><pp a="1"/><pp a="1"/><pp a="1"/>""",
            JoinXml("select 1 as a from pp for xml auto"));

    [TestMethod]
    public void AutoNesting_Aggregate_JoinsPrecedingTable()
        => AreEqual("""<p id="1"><c cnm="a1" n="1"/><c cnm="a2" n="1"/></p><p id="2"><c cnm="b1" n="1"/></p><p id="4"><c cnm="d1" n="1"/></p>""",
            JoinXml("select p.id, c.cnm, count(*) as n from pp p join cc c on c.pid=p.id group by p.id, c.cnm for xml auto"));

    [TestMethod]
    public void AutoNesting_EqualParentValues_Collapse()
        => AreEqual("""<p nm="alpha"><c cnm="a1"/><c cnm="a2"/><c cnm="d1"/></p><p nm="beta"><c cnm="b1"/></p>""",
            JoinXml("select p.nm, c.cnm from pp p join cc c on c.pid=p.id order by p.nm, c.cnm for xml auto"));

    [TestMethod]
    public void AutoNesting_ParentCollapse_IsConsecutiveOnly()
        => AreEqual("""<p id="1"><c cnm="a1"/></p><p id="4"><c cnm="d1"/></p><p id="2"><c cnm="b1"/></p><p id="1"><c cnm="a2"/></p>""",
            JoinXml("select p.id, c.cnm from pp p join cc c on c.pid=p.id order by case when c.id=11 then 3 when c.id=12 then 2 else 1 end, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_NullParentValues_Collapse()
        => AreEqual("""<p id="1"><nl><c cnm="a1"/><c cnm="a2"/></nl></p><p id="2"><nl><c cnm="b1"/></nl></p><p id="4"><nl><c cnm="d1"/></nl></p>""",
            JoinXml("select p.id, nl.nm, c.cnm from pp2 nl join pp p on p.id=nl.id join cc c on c.pid=p.id order by p.id, c.id for xml auto"));

    [TestMethod]
    public void AutoNesting_InnermostLevel_NeverCollapses()
        => AreEqual("""<p id="1"><c cnm="a1"/><c cnm="a1"/><c cnm="a2"/><c cnm="a2"/></p>""",
            JoinXml("select p.id, c.cnm from pp p join cc c on c.pid=p.id join gg g on g.pid=1 where p.id=1 order by c.id, g.id for xml auto"));

    [TestMethod]
    public void AutoNesting_SingleLevel_NeverCollapses()
        => AreEqual("""<p id="1"/><p id="1"/>""",
            JoinXml("select p.id from pp p join gg g on g.pid=1 where p.id=1 for xml auto"));

    [TestMethod]
    public void AutoNesting_XmlColumn_SuppressesCollapse()
        => AreEqual("""<p id="1"><doc><a/></doc><c cnm="c1"/></p><p id="1"><doc><a/></doc><c cnm="c2"/></p>""",
            (string)XmlColumnSimulation().ExecuteScalar(
                "select p.id, p.doc, c.cnm from xp p join xc c on c.pid=p.id order by c.cnm for xml auto")!);

    [TestMethod]
    public void AutoNesting_Root()
        => AreEqual("""<r><p id="1"><c cnm="a1"/><c cnm="a2"/></p></r>""",
            JoinXml("select p.id, c.cnm from pp p join cc c on c.pid=p.id where p.id=1 order by c.id for xml auto, root('r')"));

    [TestMethod]
    public void AutoNesting_XsinilDeclaredOnOutermostElement()
        => AreEqual("""<p xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><id>2</id><c><cnm>b1</cnm><amt xsi:nil="true"/></c></p>""",
            JoinXml("select p.id, c.cnm, c.amt from pp p join cc c on c.pid=p.id where p.id=2 for xml auto, elements xsinil"));

    [TestMethod]
    public void AutoNesting_ComputedAfterInnerTable_StaysWithIt()
        => AreEqual("<p><id>1</id><nm>alpha</nm><c><cnm>a1</cnm><calc>a1!</calc></c><c><cnm>a2</cnm><calc>a2!</calc></c></p>",
            JoinXml("select p.id, c.cnm, c.cnm+'!' as calc, p.nm from pp p join cc c on c.pid=p.id where p.id=1 order by c.id for xml auto, elements"));

    // ---- PATH ----

    [TestMethod]
    public void Path_Default_ElementCentric()
        => AreEqual("<row><id>1</id><a>10</a></row><row><id>2</id><a>30</a></row><row><id>3</id><a>50</a></row>",
            Xml("select id, a from t for xml path"));

    [TestMethod]
    public void Path_NamedRow()
        => AreEqual("<r><id>1</id><a>10</a></r><r><id>2</id><a>30</a></r><r><id>3</id><a>50</a></r>",
            Xml("select id, a from t for xml path('r')"));

    [TestMethod]
    public void Path_Attribute()
        => AreEqual("""<r id="1"><a>10</a></r><r id="2"><a>30</a></r><r id="3"><a>50</a></r>""",
            Xml("select id as [@id], a from t for xml path('r')"));

    [TestMethod]
    public void Path_TextNode()
        => AreEqual("""<r id="1">10</r><r id="2">30</r><r id="3">50</r>""",
            Xml("select id as [@id], a as [text()] from t for xml path('r')"));

    [TestMethod]
    public void Path_SlashNesting()
        => AreEqual("<r><p><c>1</c></p></r><r><p><c>2</c></p></r><r><p><c>3</c></p></r>",
            Xml("select id as [p/c] from t for xml path('r')"));

    [TestMethod]
    public void Path_SharedParent()
        => AreEqual("<r><a><b>1</b><c>10</c></a></r>",
            Xml("select id as [a/b], a as [a/c] from t where id = 1 for xml path('r')"));

    [TestMethod]
    public void Path_EmptyRowTag()
        => AreEqual("<id>1</id><id>2</id><id>3</id>",
            Xml("select id from t for xml path('')"));

    [TestMethod]
    public void Path_UnnamedColumn_IsText()
        => AreEqual("<r><id>1</id>30</r><r><id>2</id>70</r><r><id>3</id>110</r>",
            Xml("select id, a+b from t for xml path('r')"));

    [TestMethod]
    public void Path_TextOnly()
        => AreEqual("<r>1</r><r>2</r><r>3</r>",
            Xml("select id as [text()] from t for xml path('r')"));

    /// <summary>
    /// A NULL drops the child element it would have filled, but the row element
    /// always stands — a row whose whole content is NULL is <c>&lt;row/&gt;</c>,
    /// not a missing row (probe-confirmed across RAW, PATH and every leaf kind).
    /// </summary>
    [TestMethod]
    [DataRow("select cast(null as varchar(5)) as a from t where id = 1 for xml raw")]
    [DataRow("select cast(null as varchar(5)) as a from t where id = 1 for xml raw, elements")]
    [DataRow("select cast(null as varchar(5)) as a from t where id = 1 for xml path")]
    [DataRow("select cast(null as varchar(5)) as [text()] from t where id = 1 for xml path")]
    [DataRow("select cast(null as varchar(5)) as [data()] from t where id = 1 for xml path")]
    [DataRow("select cast(null as varchar(5)) from t where id = 1 for xml path")]
    public void AllNullRow_StillEmitsTheRowElement(string query)
        => AreEqual("<row/>", Xml(query));

    [TestMethod]
    public void Path_AttributeUnderEmptyRowTag_Msg6864()
    {
        var ex = Seeded().AssertSqlError("select id as [@id] from t for xml path('')", 6864);
        Contains("Row tag omission", ex.Message);
    }

    [TestMethod]
    public void Path_AttributeAfterElement_Msg6852()
    {
        var ex = Seeded().AssertSqlError("select a as b, id as [@id] from t where id = 1 for xml path('r')", 6852);
        Contains("must not come after a non-attribute-centric sibling", ex.Message);
    }

    // ---- data() and same-name concatenation ----

    [TestMethod]
    public void Path_Data_SpaceJoinsAcrossRows()
        => AreEqual("10 30 50", Xml("select a as [data()] from t for xml path('')"));

    [TestMethod]
    public void Path_SameNameElements_Concatenate()
        => AreEqual("<r><x>1020</x></r>", Xml("select 10 as [x], 20 as [x] from t where id = 1 for xml path('r')"));

    [TestMethod]
    public void Path_TextThenData_NoLeadingSpace()
        => AreEqual("<r>a1 2</r>", Xml("select 'a' as [text()], 1 as [data()], 2 as [data()] from t where id = 1 for xml path('r')"));

    // ---- The remaining PATH node functions ----

    /// <summary>
    /// <c>comment()</c> and <c>processing-instruction(target)</c> build their
    /// own node kinds, <c>node()</c> and <c>*</c> are text content, and each
    /// takes a path prefix and keeps its position among its siblings. Neither
    /// constructor escapes its value — real writes it raw, so a <c>?&gt;</c> in
    /// a processing instruction closes it early and produces XML that won't
    /// re-parse (probe-confirmed, quirk and all).
    /// </summary>
    [TestMethod]
    [DataRow("select 'x' as [comment()] from t where id = 1", "<r><!--x--></r>")]
    [DataRow("select 'x' as [processing-instruction(foo)] from t where id = 1", "<r><?foo x?></r>")]
    [DataRow("select 'x' as [node()] from t where id = 1", "<r>x</r>")]
    [DataRow("select 'x' as [*] from t where id = 1", "<r>x</r>")]
    [DataRow("select 'x' as [a/comment()] from t where id = 1", "<r><a><!--x--></a></r>")]
    [DataRow("select 'x' as [a/processing-instruction(pi)] from t where id = 1", "<r><a><?pi x?></a></r>")]
    [DataRow("select 'x' as [a/*] from t where id = 1", "<r><a>x</a></r>")]
    [DataRow("select 1 as a, 'x' as [comment()], 2 as b from t where id = 1", "<r><a>1</a><!--x--><b>2</b></r>")]
    [DataRow("select 1 as [a/comment()], 2 as [a/b] from t where id = 1", "<r><a><!--1--><b>2</b></a></r>")]
    [DataRow("select 'a' as [comment()], 'b' as [comment()] from t where id = 1", "<r><!--a--><!--b--></r>")]
    [DataRow("select 'a' as [node()], 'b' as [node()] from t where id = 1", "<r>ab</r>")]
    [DataRow("select 1 as [@id], 'x' as [comment()] from t where id = 1", """<r id="1"><!--x--></r>""")]
    // Neither constructor escapes; node() takes the same escaping as text().
    [DataRow("select '<b>&' as [comment()] from t where id = 1", "<r><!--<b>&--></r>")]
    [DataRow("select '<b>&' as [processing-instruction(p)] from t where id = 1", "<r><?p <b>&?></r>")]
    [DataRow("select '<b>&' as [node()] from t where id = 1", "<r>&lt;b&gt;&amp;</r>")]
    // The separator is one space, then the value as written.
    [DataRow("select ' x ' as [processing-instruction(p)] from t where id = 1", "<r><?p  x ?></r>")]
    [DataRow("select '' as [processing-instruction(p)] from t where id = 1", "<r><?p ?></r>")]
    [DataRow("select '' as [comment()] from t where id = 1", "<r><!----></r>")]
    // A NULL writes nothing, and a comment breaks a run of data() atoms.
    [DataRow("select cast(null as varchar(5)) as [comment()] from t where id = 1", "<r/>")]
    [DataRow("select cast(null as varchar(5)) as [processing-instruction(p)] from t where id = 1", "<r/>")]
    [DataRow("select cast(null as varchar(5)) as [node()] from t where id = 1", "<r/>")]
    [DataRow("select 'a' as [data()], 'b' as [comment()], 'c' as [data()] from t where id = 1", "<r>a<!--b-->c</r>")]
    // A target real doesn't reserve; only the exactly-lowercase 'xml' is refused.
    [DataRow("select 'x' as [processing-instruction(XmL)] from t where id = 1", "<r><?XmL x?></r>")]
    [DataRow("select 'x' as [processing-instruction(xmla)] from t where id = 1", "<r><?xmla x?></r>")]
    public void Path_NodeFunction_ProbedShape(string query, string expected)
        => AreEqual(expected, Xml(query + " for xml path('r')"));

    [TestMethod]
    public void Path_Comment_UnderOmittedRowTag()
        => AreEqual("<!--x-->", Xml("select 'x' as [comment()] from t where id = 1 for xml path('')"));

    /// <summary>
    /// A NULL under a comment / processing-instruction step stays absent under
    /// XSINIL, where the text-shaped steps get the nil marker.
    /// </summary>
    [TestMethod]
    [DataRow("[a/comment()]", """<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>""")]
    [DataRow("[a/processing-instruction(p)]", """<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>""")]
    [DataRow("[a/text()]", """<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a xsi:nil="true"/></r>""")]
    [DataRow("[a/node()]", """<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a xsi:nil="true"/></r>""")]
    public void Path_NullNodeFunction_UnderXsinil(string alias, string expected)
        => AreEqual(expected, Xml($"select cast(null as varchar(5)) as {alias} from t where id = 1 for xml path('r'), elements xsinil"));

    /// <summary>
    /// A comment can't carry the pair of dashes that would close it, nor end in
    /// the dash that would make one — real reports the two cases at different
    /// states while serializing the row.
    /// </summary>
    [TestMethod]
    [DataRow("a--b", 2)]
    [DataRow("--", 2)]
    [DataRow("a-", 3)]
    [DataRow("-", 3)]
    public void Path_CommentWithDashes_Msg9322(string value, int state)
    {
        var ex = Seeded().AssertSqlError($"select '{value}' as [comment()] from t where id = 1 for xml path('r')", 9322);
        AreEqual("Two consecutive '-' can only appear in a comment constructor if they are used to close the comment ('-->').", ex.Message);
        AreEqual(state, ex.State);
    }

    [TestMethod]
    public void Path_CommentWithInteriorDash_Serializes()
        => AreEqual("<r><!--a-b--></r>", Xml("select 'a-b' as [comment()] from t where id = 1 for xml path('r')"));

    /// <summary>
    /// The node functions are matched ordinally and carry no namespace prefix,
    /// so every other spelling falls through to the XML-name rules and trips on
    /// its own <c>(</c> or <c>*</c>.
    /// </summary>
    [TestMethod]
    [DataRow("[COMMENT()]", '(')]
    [DataRow("[TEXT()]", '(')]
    [DataRow("[DATA()]", '(')]
    [DataRow("[NODE()]", '(')]
    [DataRow("[PROCESSING-INSTRUCTION(a)]", '(')]
    [DataRow("[comment (   )]", ' ')]
    [DataRow("[comment(a)]", '(')]
    [DataRow("[comment()/a]", '(')]
    [DataRow("[@*]", '*')]
    [DataRow("[*/a]", '*')]
    public void Path_UnrecognizedNodeFunction_Msg6850(string alias, char offender)
    {
        var ex = Seeded().AssertSqlError($"select 1 as {alias} from t for xml path", 6850);
        Contains($"'{offender}'(0x{(int)offender:X4}) is the first character at fault", ex.Message);
        StartsWith("Column name ", ex.Message);
    }

    /// <summary>
    /// A namespace prefix on a node function is refused whichever way it fails:
    /// undeclared it is the prefix error, declared it is the name error the
    /// step's own <c>(</c> raises.
    /// </summary>
    [TestMethod]
    public void Path_PrefixedNodeFunction_IsRefused()
    {
        var undeclared = Seeded().AssertSqlError("select 1 as [a:comment()] from t for xml path", 6846);
        AreEqual("XML name space prefix 'a' declaration is missing for FOR XML column name 'a:comment()'.", undeclared.Message);
        var declared = Seeded().AssertSqlError(
            "with xmlnamespaces('urn:u' as a) select 1 as [a:comment()] from t for xml path", 6850);
        Contains("'('(0x0028) is the first character at fault", declared.Message);
    }

    /// <summary>
    /// A processing-instruction target is an XML name with no <c>:</c>
    /// allowance, and real's Msg 6850 leaves the name-kind word empty for it —
    /// the message leads with a space and quotes the whole alias.
    /// </summary>
    [TestMethod]
    [DataRow("[processing-instruction(1a)]", '1')]
    [DataRow("[processing-instruction(a b)]", ' ')]
    [DataRow("[processing-instruction(a:b)]", ':')]
    public void Path_InvalidProcessingInstructionTarget_Msg6850(string alias, char offender)
    {
        var ex = Seeded().AssertSqlError($"select 1 as {alias} from t for xml path", 6850);
        AreEqual(
            $" name '{alias[1..^1]}' contains an invalid XML identifier as required by FOR XML; '{offender}'(0x{(int)offender:X4}) is the first character at fault.",
            ex.Message);
    }

    [TestMethod]
    public void Path_ProcessingInstructionWithoutTarget_Msg6854()
    {
        var ex = Seeded().AssertSqlError("select 1 as [processing-instruction()] from t for xml path", 6854);
        AreEqual(
            "Invalid column alias 'processing-instruction()' for formatting column as XML processing instruction in FOR XML PATH - it must be in 'processing-instruction(target)' format.",
            ex.Message);
    }

    [TestMethod]
    public void Path_ProcessingInstructionTargetXml_Msg6879()
    {
        var ex = Seeded().AssertSqlError("select 1 as [processing-instruction(xml)] from t for xml path", 6879);
        AreEqual(
            "'xml' is an invalid XML processing instruction target. Possible attempt to construct XML declaration using XML processing instruction constructor. XML declaration construction with FOR XML is not supported.",
            ex.Message);
    }

    /// <summary>
    /// An <c>xml</c>-typed column has no text form, so every node function that
    /// writes one refuses it; <c>node()</c> / <c>*</c> and a plain element step
    /// embed its nodes instead.
    /// </summary>
    [TestMethod]
    [DataRow("[text()]")]
    [DataRow("[data()]")]
    [DataRow("[comment()]")]
    [DataRow("[processing-instruction(p)]")]
    [DataRow("[p/text()]")]
    public void Path_XmlColumnUnderNodeFunction_Msg6853(string alias)
    {
        var ex = XmlColumnSimulation().AssertSqlError($"select doc as {alias} from xp where id = 1 for xml path('r')", 6853);
        AreEqual($"Column '{alias[1..^1]}': the last step in the path can't be applied to XML data type or CLR type in FOR XML PATH.", ex.Message);
    }

    [TestMethod]
    [DataRow("[node()]", "<r><a/></r>")]
    [DataRow("[*]", "<r><a/></r>")]
    [DataRow("[p/node()]", "<r><p><a/></p></r>")]
    public void Path_XmlColumnUnderNodeOrStar_Embeds(string alias, string expected)
        => AreEqual(expected,
            (string)XmlColumnSimulation().ExecuteScalar($"select doc as {alias} from xp where id = 1 for xml path('r')")!);

    // ---- ROOT ----

    [TestMethod]
    public void Root_Default()
        => AreEqual("""<root><row id="1"/><row id="2"/><row id="3"/></root>""",
            Xml("select id from t for xml raw, root"));

    [TestMethod]
    public void Root_Named()
        => AreEqual("""<rows><row id="1"/><row id="2"/><row id="3"/></rows>""",
            Xml("select id from t for xml raw, root('rows')"));

    [TestMethod]
    public void Root_Empty_Msg6861()
    {
        var ex = Seeded().AssertSqlError("select id from t for xml raw, root('')", 6861);
        Contains("Empty root tag name", ex.Message);
    }

    // ---- XSINIL ----

    [TestMethod]
    public void Xsinil_PerRowNamespace()
        => AreEqual("""<row xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><id>1</id><x xsi:nil="true"/></row>""",
            Xml("select id, cast(null as int) as x from t where id = 1 for xml raw, elements xsinil"));

    [TestMethod]
    public void Xsinil_WithRoot_NamespaceOnRootOnly()
        => AreEqual("""<root xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><t><id>1</id><x xsi:nil="true"/></t></root>""",
            Xml("select id, cast(null as int) as x from t where id = 1 for xml auto, root, elements xsinil"));

    [TestMethod]
    public void Xsinil_EmptyRowTag()
        => AreEqual("""<id xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">1</id><x xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/>""",
            Xml("select id, cast(null as int) as x from t where id = 1 for xml path(''), elements xsinil"));

    // ---- NULL (ABSENT default) ----

    [TestMethod]
    public void Null_Attribute_Omitted()
        => AreEqual("""<row id="1"/>""",
            Xml("select id, cast(null as int) as x from t where id = 1 for xml raw"));

    [TestMethod]
    public void Null_Element_Absent()
        => AreEqual("<row><id>1</id></row>",
            Xml("select id, cast(null as int) as x from t where id = 1 for xml raw, elements"));

    // ---- empty rowset ----

    [TestMethod]
    public void EmptyRowset_IsNull()
        => IsNull(Seeded().ExecuteScalar("select id from t where 1 = 0 for xml raw"));

    // ---- value formatting ----

    [TestMethod]
    public void Value_Bit()
        => AreEqual("""<row a="1" b="0"/>""",
            Xml("select cast(1 as bit) as a, cast(0 as bit) as b from t where id = 1 for xml raw"));

    [TestMethod]
    public void Value_FloatReal()
        => AreEqual("""<row a="1.500000000000000e+000" b="1.5000000e+000"/>""",
            Xml("select cast(1.5 as float) as a, cast(1.5 as real) as b from t where id = 1 for xml raw"));

    [TestMethod]
    public void Value_DecimalMoney()
        => AreEqual("""<row a="12.3400" b="1.5000"/>""",
            Xml("select cast(12.34 as decimal(6,4)) as a, cast(1.5 as money) as b from t where id = 1 for xml raw"));

    [TestMethod]
    public void Value_DatetimeDate()
        => AreEqual("""<row a="2020-01-02T03:04:05.123" b="2020-01-02"/>""",
            Xml("select cast('2020-01-02T03:04:05.123' as datetime) as a, cast('2020-01-02' as date) as b from t where id = 1 for xml raw"));

    [TestMethod]
    public void Value_TimeDatetimeoffset()
        => AreEqual("""<row a="03:04:05.1234567" b="2020-01-02T03:04:05.1200000+05:30"/>""",
            Xml("select cast('03:04:05.1234567' as time) as a, cast('2020-01-02T03:04:05.12+05:30' as datetimeoffset) as b from t where id = 1 for xml raw"));

    [TestMethod]
    public void Value_Datetime2SmalldatetimeBigint()
        => AreEqual("""<row a="2020-01-02T03:04:05.1234567" b="2020-01-02T03:04:00" c="123"/>""",
            Xml("select cast('2020-01-02 03:04:05.1234567' as datetime2(7)) as a, cast('2020-01-02 03:04' as smalldatetime) as b, cast(123 as bigint) as c from t where id = 1 for xml raw"));

    [TestMethod]
    public void Value_Uniqueidentifier_Uppercase()
        => AreEqual("""<r a="6F9619FF-8B86-D011-B42D-00C04FC964FF"/>""",
            Xml("select cast('6f9619ff-8b86-d011-b42d-00c04fc964ff' as uniqueidentifier) as [@a] from t where id = 1 for xml path('r')"));

    [TestMethod]
    public void Value_Binary_Base64InPath()
        => AreEqual("<r><a>AQL/</a></r>",
            Xml("select cast(0x0102FF as varbinary(3)) as a from t where id = 1 for xml path('r'), elements"));

    [TestMethod]
    public void Value_Binary_RawRejected_Msg6829()
    {
        var ex = Seeded().AssertSqlError("select cast(0x0102FF as varbinary(3)) as a from t where id = 1 for xml raw", 6829);
        Contains("do not support addressing binary data as URLs", ex.Message);
    }

    [TestMethod]
    public void Value_Binary_AutoRejected_Msg6830()
    {
        var ex = Seeded().AssertSqlError("select cast(0x0102FF as varbinary(3)) as a from t where id = 1 for xml auto", 6830);
        Contains("could not find the table owning", ex.Message);
    }

    // ---- escaping ----

    [TestMethod]
    public void Escape_ElementText()
        => AreEqual("<r><v>a&lt;b&gt;&amp;\"'c</v></r>",
            Xml("select N'a<b>&\"''c' as v from t where id = 1 for xml path('r'), elements"));

    [TestMethod]
    public void Escape_AttributeValue()
        => AreEqual("""<r v="a&lt;b&gt;&amp;&quot;'c"/>""",
            Xml("select N'a<b>&\"''c' as [@v] from t where id = 1 for xml path('r')"));

    [TestMethod]
    public void Escape_AttributeWhitespace()
        => AreEqual("""<r v="&#x09;&#x0A;&#x0D;"/>""",
            Xml("select char(9)+char(10)+char(13) as [@v] from t where id = 1 for xml path('r')"));

    // ---- TYPE ----

    [TestMethod]
    public void Type_NestedSubquery_EmbedsNodes()
        => AreEqual("<p><id>1</id><kids><c><cnm>a1</cnm></c><c><cnm>a2</cnm></c></kids></p><p><id>2</id><kids><c><cnm>b1</cnm></c></kids></p>",
            JoinXml("select p.id, (select c.cnm from cc c where c.pid = p.id order by c.id for xml path('c'), type) as kids from pp p where p.id < 3 order by p.id for xml path('p')"));

    [TestMethod]
    public void Untyped_NestedSubquery_EscapesText()
        => AreEqual("<p><id>1</id><kids>&lt;c&gt;&lt;cnm&gt;a1&lt;/cnm&gt;&lt;/c&gt;&lt;c&gt;&lt;cnm&gt;a2&lt;/cnm&gt;&lt;/c&gt;</kids></p>",
            JoinXml("select p.id, (select c.cnm from cc c where c.pid = p.id order by c.id for xml path('c')) as kids from pp p where p.id = 1 for xml path('p')"));

    [TestMethod]
    public void Type_UnnamedNestedColumn_InlinesChildNodes()
        => AreEqual("""<p id="1"><c cid="10"/><c cid="11"/></p>""",
            JoinXml("select p.id as [@id], (select c.id as [@cid] from cc c where c.pid = p.id order by c.id for xml path('c'), type) from pp p where p.id = 1 for xml path('p')"));

    [TestMethod]
    public void Type_ResultColumnIsUnnamedXml()
    {
        using var reader = Seeded().CreateCommand("select id from t for xml path('p'), type").ExecuteReader();
        AreEqual("", reader.GetName(0));
        AreEqual("xml", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void Untyped_ResultColumnIsNamedString()
    {
        using var reader = Seeded().CreateCommand("select id from t for xml path('p')").ExecuteReader();
        AreEqual("XML_F52E2B61-18A1-11d1-B105-00805F49916B", reader.GetName(0));
        AreEqual("nvarchar", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void Type_EmptyRowset_YieldsOneNullRow()
    {
        using var reader = Seeded().CreateCommand("select id from t where 1 = 0 for xml path('p'), type").ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Untyped_EmptyRowset_YieldsNoRows()
    {
        using var reader = Seeded().CreateCommand("select id from t where 1 = 0 for xml path('p')").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Type_WithRoot()
        => AreEqual("""<r><row id="1"/><row id="2"/><row id="3"/></r>""",
            Xml("select id from t for xml raw, type, root('r')"));

    [TestMethod]
    public void Type_ConsumedByXmlMethod()
        => AreEqual(10, Seeded().ExecuteScalar(
            "declare @x xml = (select id, a from t where id = 1 for xml path('p'), type); select @x.value('(/p/a)[1]', 'int')"));

    [TestMethod]
    public void Type_InFromPosition()
        => AreEqual("<p><id>1</id></p><p><id>2</id></p><p><id>3</id></p>",
            Xml("select * from (select id from t for xml path('p'), type) d(x)"));

    [TestMethod]
    public void Type_InsideAuto_NestsAndSuppressesLevelCollapse()
        => AreEqual("""<p id="1"><kids><c cid="10"/><c cid="11"/></kids><c cnm="a1"/></p><p id="1"><kids><c cid="10"/><c cid="11"/></kids><c cnm="a2"/></p>""",
            JoinXml("select p.id, (select c2.id as [@cid] from cc c2 where c2.pid = p.id order by c2.id for xml path('c'), type) as kids, c.cnm from pp p join cc c on c.pid = p.id where p.id = 1 order by c.id for xml auto"));

    // ---- xml-typed columns ----

    private static Simulation XmlColumnSimulation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table xp (id int, doc xml, txt nvarchar(100));
            create table xc (pid int, cnm nvarchar(10));
            insert xp values (1, '<a/>', '<a/>'), (2, null, null);
            insert xc values (1, 'c1'), (1, 'c2');
            """);
        return sim;
    }

    [TestMethod]
    public void XmlColumn_Path_EmbedsNodesWhileStringEscapes()
        => AreEqual("<p><id>1</id><doc><a/></doc><txt>&lt;a/&gt;</txt></p><p><id>2</id></p>",
            (string)XmlColumnSimulation().ExecuteScalar("select id, doc, txt from xp order by id for xml path('p')")!);

    [TestMethod]
    public void XmlColumn_Raw_BecomesChildElementNotAttribute()
        => AreEqual("""<row id="1" txt="&lt;a/&gt;"><doc><a/></doc></row><row id="2"/>""",
            (string)XmlColumnSimulation().ExecuteScalar("select id, doc, txt from xp order by id for xml raw")!);

    [TestMethod]
    public void XmlColumn_AsPathAttribute_Msg6851()
    {
        var ex = XmlColumnSimulation().AssertSqlError("select doc as [@a] from xp where id = 1 for xml path('p')", 6851);
        Contains("invalid data type for attribute-centric", ex.Message);
    }

    // ---- RAW / AUTO name escaping ----

    [TestMethod]
    public void RawName_AsciiEscapes()
        => AreEqual("""<row a_x0020_b="1" _x0031_a="1" a_x0024_b="1" a_x005F_x0020_b="1" a-b="1" _x002D_a="1" a.b="1" _x002E_a="1" _a="1" a:b="1" _x003A_a="1" a_x005F_xzzzz_b="1" xmlfoo="1" XMLfoo="1" xml="1" a_x0023_b="1" _x0024_a="1" a1="1" _x005F_x0041_="1"/>""",
            Xml("""
                select 1 as [a b], 1 as [1a], 1 as [a$b], 1 as [a_x0020_b], 1 as [a-b], 1 as [-a],
                       1 as [a.b], 1 as [.a], 1 as [_a], 1 as [a:b], 1 as [:a], 1 as [a_xzzzz_b],
                       1 as [xmlfoo], 1 as [XMLfoo], 1 as [xml], 1 as [a#b], 1 as [$a], 1 as [a1],
                       1 as [_x0041_]
                for xml raw
                """));

    /// <summary>
    /// The non-ASCII half of the escaping table. The classification is the XML
    /// 1.0 fourth-edition <c>Name</c> production, so <c>é</c> (a base
    /// character) and <c>·</c> / <c>ͥ</c> (an extender / combining mark, in
    /// non-first position) pass while <c>«</c>, <c>×</c>, <c>€</c> and the
    /// fullwidth <c>Ａ</c> escape; a supplementary code point takes one
    /// six-hex-digit escape rather than a per-surrogate one.
    /// </summary>
    [TestMethod]
    public void RawName_NonAsciiEscapes()
        => AreEqual("""<row a_Xzzzz_b="1" a_x005F_x_b="1" _="1" __="1" _x005F_x="1" x_="1" aé="1" éa="1" a_x00AB_b="1" _x00AB_a="1" a_x00D7_b="1" a·b="1" _x00B7_a="1" 漢字="1" a_x20AC_b="1" a_x01D400_b="1" _x01D400_a="1" a_x0365_b="1"/>""",
            Xml("""
                select 1 as [a_Xzzzz_b], 1 as [a_x_b], 1 as [_], 1 as [__], 1 as [_x], 1 as [x_],
                       1 as [aé], 1 as [éa], 1 as [a«b], 1 as [«a], 1 as [a×b], 1 as [a·b], 1 as [·a],
                       1 as [漢字], 1 as [a€b], 1 as [a𝐀b], 1 as [𝐀a], 1 as [aͥb]
                for xml raw
                """));

    [TestMethod]
    public void RawName_ElementsAndXsinil_EscapeToo()
        => AreEqual("""<row xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a_x0020_b>1</a_x0020_b><c_x0020_d xsi:nil="true"/></row>""",
            Xml("select 1 as [a b], null as [c d] for xml raw, elements xsinil"));

    [TestMethod]
    public void AutoName_TempTableAndColumn_Escape()
        => AreEqual("""<_x0023_tmp id="1" c_x0020_d="2"/>""",
            (string)new Simulation().ExecuteScalar("""
                create table #tmp (id int, [c d] int);
                insert #tmp values (1, 2);
                select id, [c d] from #tmp for xml auto
                """)!);

    [TestMethod]
    public void AutoName_TableAlias_Escapes()
        => AreEqual("""<a_x0020_b id="1"/><a_x0020_b id="2"/><a_x0020_b id="3"/>""",
            Xml("select id from t as [a b] for xml auto"));

    // ---- PATH / explicit names are rejected, not escaped ----

    [TestMethod]
    public void PathName_Space_Msg6850()
        => Seeded().AssertSqlError("select 1 as [a b] from t for xml path", 6850,
            "Column name 'a b' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void PathName_LeadingDigit_Msg6850()
        => Seeded().AssertSqlError("select 1 as [1a] from t for xml path", 6850,
            "Column name '1a' contains an invalid XML identifier as required by FOR XML; '1'(0x0031) is the first character at fault.");

    [TestMethod]
    public void PathName_LeadingColon_Msg6850()
        => Seeded().AssertSqlError("select 1 as [:a] from t for xml path", 6850,
            "Column name ':a' contains an invalid XML identifier as required by FOR XML; ':'(0x003A) is the first character at fault.");

    [TestMethod]
    public void PathName_Tab_Msg6850()
        => Seeded().AssertSqlError("select 1 as [a\tb] from t for xml path", 6850,
            "Column name 'a\tb' contains an invalid XML identifier as required by FOR XML; '\t'(0x0009) is the first character at fault.");

    [TestMethod]
    public void PathName_FaultInLaterStep_QuotesWholeAlias()
        => Seeded().AssertSqlError("select 1 as [x/y z] from t for xml path", 6850,
            "Column name 'x/y z' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void PathName_Attribute_Msg6850()
        => Seeded().AssertSqlError("select 1 as [@a b] from t for xml path", 6850,
            "Column name '@a b' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void PathName_BareAtSign_Msg6850()
        => Seeded().AssertSqlError("select 1 as [@] from t for xml path", 6850,
            "Column name '@' contains an invalid XML identifier as required by FOR XML; '@'(0x0040) is the first character at fault.");

    [TestMethod]
    [DataRow("[/a]", "/a")]
    [DataRow("[a/]", "a/")]
    [DataRow("[a//b]", "a//b")]
    public void PathName_EmptyStep_Msg6849(string alias, string quoted)
        => Seeded().AssertSqlError($"select 1 as {alias} from t for xml path", 6849,
            $"FOR XML PATH error in column '{quoted}' - '//' and leading and trailing '/' are not allowed in simple path expressions.");

    [TestMethod]
    public void PathName_AlreadyEscapedText_PassesThrough()
        => AreEqual("<row><a_x0020_b>1</a_x0020_b></row>",
            Xml("select 1 as [a_x0020_b] from t where id = 1 for xml path"));

    /// <summary>
    /// A supplementary character is a legal XML name character, so PATH takes
    /// it verbatim — where RAW / AUTO escape the same character (probed both
    /// ways: the two validators disagree in real).
    /// </summary>
    [TestMethod]
    public void PathName_SupplementaryCharacter_PassesThrough()
        => AreEqual("<row><a𝐀>1</a𝐀></row>",
            Xml("select 1 as [a𝐀] from t where id = 1 for xml path"));

    [TestMethod]
    public void PathName_UndeclaredPrefix_Msg6846()
        => Seeded().AssertSqlError("select 1 as [a:b] from t for xml path", 6846,
            "XML name space prefix 'a' declaration is missing for FOR XML column name 'a:b'.");

    [TestMethod]
    public void PathName_PrefixCheckPrecedesCharacterCheck_Msg6846()
        => Seeded().AssertSqlError("select 1 as [a b:c] from t for xml path", 6846,
            "XML name space prefix 'a b' declaration is missing for FOR XML column name 'a b:c'.");

    [TestMethod]
    public void PathName_XmlPrefix_IsPredefined()
        => AreEqual("<row><xml:lang>1</xml:lang></row>",
            Xml("select 1 as [xml:lang] from t where id = 1 for xml path"));

    [TestMethod]
    public void PathName_XmlPrefixIsCaseSensitive_Msg6846()
        => Seeded().AssertSqlError("select 1 as [XML:a] from t for xml path", 6846,
            "XML name space prefix 'XML' declaration is missing for FOR XML column name 'XML:a'.");

    [TestMethod]
    [DataRow("[xmlns]")]
    [DataRow("[xmlns:a]")]
    [DataRow("[@xmlns]")]
    public void PathName_Xmlns_Msg6867(string alias)
        => Seeded().AssertSqlError($"select 1 as {alias} from t for xml path", 6867,
            "'xmlns' is invalid in XML tag name in FOR XML PATH, or when WITH XMLNAMESPACES is used with FOR XML.");

    [TestMethod]
    public void RowName_Invalid_Msg6850_EvenInRaw()
        => Seeded().AssertSqlError("select id from t for xml raw('r x')", 6850,
            "Row name 'r x' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void RowName_IsNotAPath_Msg6850()
        => Seeded().AssertSqlError("select id from t for xml path('a/b')", 6850,
            "Row name 'a/b' contains an invalid XML identifier as required by FOR XML; '/'(0x002F) is the first character at fault.");

    [TestMethod]
    public void RootName_Invalid_Msg6850()
        => Seeded().AssertSqlError("select id from t for xml raw, root('t y')", 6850,
            "ROOT name 't y' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void RootName_UndeclaredPrefix_Msg6846()
        => Seeded().AssertSqlError("select id from t for xml raw, root('a:b')", 6846,
            "XML name space prefix 'a' declaration is missing for FOR XML ROOT name 'a:b'.");

    [TestMethod]
    public void RowName_CheckedBeforeRootName()
        => Seeded().AssertSqlError("select id from t for xml raw('r x'), root('t y')", 6850,
            "Row name 'r x' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void RawName_Empty_Msg6864()
    {
        var ex = Seeded().AssertSqlError("select id from t for xml raw('')", 6864);
        Contains("Row tag omission", ex.Message);
    }

    [TestMethod]
    public void RawName_EmptyWithElements_OmitsRowTag()
        => AreEqual("<id>1</id><id>2</id><id>3</id>",
            Xml("select id from t for xml raw(''), elements"));

    // ---- WITH XMLNAMESPACES ----

    [TestMethod]
    public void Namespaces_Raw_DeclaresOnEveryRowElement()
        => AreEqual("""<row xmlns:p="urn:x" id="1" a="10"/><row xmlns:p="urn:x" id="2" a="30"/><row xmlns:p="urn:x" id="3" a="50"/>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml raw"));

    [TestMethod]
    public void Namespaces_Auto_DeclaresOnEveryRowElement()
        => AreEqual("""<t xmlns:p="urn:x" id="1" a="10"/><t xmlns:p="urn:x" id="2" a="30"/><t xmlns:p="urn:x" id="3" a="50"/>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml auto"));

    [TestMethod]
    public void Namespaces_Path_DeclaresOnEveryRowElement()
        => AreEqual("""<row xmlns:p="urn:x"><id>1</id><a>10</a></row><row xmlns:p="urn:x"><id>2</id><a>30</a></row><row xmlns:p="urn:x"><id>3</id><a>50</a></row>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml path"));

    /// <summary>With a ROOT wrapper the declarations move to it — the row elements carry none.</summary>
    [TestMethod]
    public void Namespaces_Root_DeclaresOnRootOnly()
        => AreEqual("""<r xmlns:p="urn:x"><row id="1" a="10"/><row id="2" a="30"/><row id="3" a="50"/></r>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml raw, root('r')"));

    [TestMethod]
    public void Namespaces_AutoRoot_DeclaresOnRootOnly()
        => AreEqual("""<r xmlns:p="urn:x"><t id="1" a="10"/><t id="2" a="30"/><t id="3" a="50"/></r>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml auto, root('r')"));

    /// <summary>
    /// Row-tag omission has no row element, so every top-level element the row
    /// content produces carries the declarations instead.
    /// </summary>
    [TestMethod]
    public void Namespaces_PathRowTagOmitted_DeclaresOnEachTopLevelElement()
        => AreEqual("""<id xmlns:p="urn:x">1</id><a xmlns:p="urn:x">10</a><id xmlns:p="urn:x">2</id><a xmlns:p="urn:x">30</a><id xmlns:p="urn:x">3</id><a xmlns:p="urn:x">50</a>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml path('')"));

    [TestMethod]
    public void Namespaces_PathRowTagOmitted_BareTextCarriesNone()
        => AreEqual("""1<a xmlns:p="urn:x">10</a>2<a xmlns:p="urn:x">30</a>3<a xmlns:p="urn:x">50</a>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id as [text()], a from t for xml path('')"));

    [TestMethod]
    public void Namespaces_RawRowTagOmitted_DeclaresOnEachTopLevelElement()
        => AreEqual("""<id xmlns:p="urn:x">1</id><a xmlns:p="urn:x">10</a><id xmlns:p="urn:x">2</id><a xmlns:p="urn:x">30</a><id xmlns:p="urn:x">3</id><a xmlns:p="urn:x">50</a>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, a from t for xml raw(''), elements"));

    /// <summary>DEFAULT emits an unprefixed xmlns, which the element names then inherit by XML scoping.</summary>
    [TestMethod]
    public void Namespaces_Default_EmitsUnprefixedDeclaration()
        => AreEqual("""<t xmlns="urn:d" id="1"/><t xmlns="urn:d" id="2"/><t xmlns="urn:d" id="3"/>""",
            Xml("with xmlnamespaces (default 'urn:d') select id from t for xml auto"));

    /// <summary>Declarations emit in reverse declaration order, DEFAULT taking its written position.</summary>
    [TestMethod]
    public void Namespaces_EmitInReverseDeclarationOrder()
        => AreEqual("""<row xmlns:r="urn:z" xmlns:q="urn:y" xmlns:p="urn:x" id="1"/><row xmlns:r="urn:z" xmlns:q="urn:y" xmlns:p="urn:x" id="2"/><row xmlns:r="urn:z" xmlns:q="urn:y" xmlns:p="urn:x" id="3"/>""",
            Xml("with xmlnamespaces ('urn:x' as p, 'urn:y' as q, 'urn:z' as r) select id from t for xml raw"));

    [TestMethod]
    public void Namespaces_DefaultKeepsItsWrittenPositionInReverseOrder()
        => AreEqual("""<row xmlns:q="urn:y" xmlns="urn:d" xmlns:p="urn:x" id="1"/><row xmlns:q="urn:y" xmlns="urn:d" xmlns:p="urn:x" id="2"/><row xmlns:q="urn:y" xmlns="urn:d" xmlns:p="urn:x" id="3"/>""",
            Xml("with xmlnamespaces ('urn:x' as p, default 'urn:d', 'urn:y' as q) select id from t for xml raw"));

    [TestMethod]
    public void Namespaces_Xsinil_DeclaresXsiFirst()
        => AreEqual("""<row xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:d" xmlns:p="urn:x"><id>1</id><z xsi:nil="true"/></row><row xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:d" xmlns:p="urn:x"><id>2</id><z xsi:nil="true"/></row><row xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:d" xmlns:p="urn:x"><id>3</id><z xsi:nil="true"/></row>""",
            Xml("with xmlnamespaces ('urn:x' as p, default 'urn:d') select id, cast(null as int) as z from t for xml path, elements xsinil"));

    [TestMethod]
    public void Namespaces_XsinilWithRoot_BothLandOnRoot()
        => AreEqual("""<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:p="urn:x"><row><id>1</id><z xsi:nil="true"/></row><row><id>2</id><z xsi:nil="true"/></row><row><id>3</id><z xsi:nil="true"/></row></r>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, cast(null as int) as z from t for xml raw, elements xsinil, root('r')"));

    // ---- WITH XMLNAMESPACES: prefixed names ----

    [TestMethod]
    public void Namespaces_PathElementAndAttributeAliases()
        => AreEqual("""<row xmlns:p="urn:x" p:b="10"><p:a>1</p:a></row><row xmlns:p="urn:x" p:b="30"><p:a>2</p:a></row><row xmlns:p="urn:x" p:b="50"><p:a>3</p:a></row>""",
            Xml("with xmlnamespaces ('urn:x' as p) select a as [@p:b], id as [p:a] from t for xml path"));

    [TestMethod]
    public void Namespaces_PathNestedPrefixedSteps()
        => AreEqual("""<row xmlns:p="urn:x"><p:a><p:b>1</p:b></p:a></row><row xmlns:p="urn:x"><p:a><p:b>2</p:b></p:a></row><row xmlns:p="urn:x"><p:a><p:b>3</p:b></p:a></row>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id as [p:a/p:b] from t for xml path"));

    [TestMethod]
    public void Namespaces_PrefixedRowTag()
        => AreEqual("""<p:row xmlns:q="urn:y" xmlns:p="urn:x"><p:a><q:b>1</q:b></p:a></p:row><p:row xmlns:q="urn:y" xmlns:p="urn:x"><p:a><q:b>2</q:b></p:a></p:row><p:row xmlns:q="urn:y" xmlns:p="urn:x"><p:a><q:b>3</q:b></p:a></p:row>""",
            Xml("with xmlnamespaces ('urn:x' as p, 'urn:y' as q) select id as [p:a/q:b] from t for xml path('p:row')"));

    [TestMethod]
    public void Namespaces_PrefixedRootName()
        => AreEqual("""<p:r xmlns:p="urn:x"><row><p:a>1</p:a></row><row><p:a>2</p:a></row><row><p:a>3</p:a></row></p:r>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id as [p:a] from t for xml path, root('p:r')"));

    [TestMethod]
    public void Namespaces_PrefixedRawElementName()
        => AreEqual("""<p:e xmlns:p="urn:x" p:a="1"/><p:e xmlns:p="urn:x" p:a="2"/><p:e xmlns:p="urn:x" p:a="3"/>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id as [p:a] from t for xml raw('p:e')"));

    /// <summary>The prefix match is ordinal: a clause declaring <c>p</c> still refuses <c>P:a</c>.</summary>
    [TestMethod]
    public void Namespaces_PrefixMatchIsOrdinal_Msg6846()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p) select id as [P:a] from t for xml path", 6846,
            "XML name space prefix 'P' declaration is missing for FOR XML column name 'P:a'.");

    [TestMethod]
    public void Namespaces_UndeclaredPrefixStillRejected_Msg6846()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p) select id as [q:a] from t for xml path", 6846,
            "XML name space prefix 'q' declaration is missing for FOR XML column name 'q:a'.");

    [TestMethod]
    public void Namespaces_XmlnsNameStillRejected_Msg6867()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p) select id as [xmlns:a] from t for xml path", 6867,
            "'xmlns' is invalid in XML tag name in FOR XML PATH, or when WITH XMLNAMESPACES is used with FOR XML.");

    // ---- WITH XMLNAMESPACES: scope and grammar ----

    /// <summary>The clause scopes the whole statement, so a nested FOR XML re-declares on its own element.</summary>
    [TestMethod]
    public void Namespaces_NestedForXml_RedeclaresOnInnerElement()
        => AreEqual("""<outer xmlns:p="urn:x"><id>1</id><inner xmlns:p="urn:x"><a>10</a></inner></outer><outer xmlns:p="urn:x"><id>2</id><inner xmlns:p="urn:x"><a>20</a></inner></outer><outer xmlns:p="urn:x"><id>3</id></outer>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id, (select a from u where u.id=t.id for xml path('inner'), type) from t for xml path('outer')"));

    [TestMethod]
    public void Namespaces_ScalarSubqueryForXml_Declares()
        => AreEqual("""<row xmlns:p="urn:x"><id>1</id></row><row xmlns:p="urn:x"><id>2</id></row><row xmlns:p="urn:x"><id>3</id></row>""",
            Xml("with xmlnamespaces ('urn:x' as p) select (select id from t for xml path, type) as q"));

    [TestMethod]
    public void Namespaces_ComposeWithCteList()
        => AreEqual("""<row xmlns:p="urn:x" id="1"/><row xmlns:p="urn:x" id="2"/><row xmlns:p="urn:x" id="3"/>""",
            Xml("with xmlnamespaces ('urn:x' as p), c as (select id from t) select id from c for xml raw"));

    [TestMethod]
    public void Namespaces_SetOperationAuto_Declares()
        => AreEqual("""<t xmlns:p="urn:x" id="1"/><t xmlns:p="urn:x" id="2"/><t xmlns:p="urn:x" id="3"/><t xmlns:p="urn:x" id="1"/><t xmlns:p="urn:x" id="2"/>""",
            Xml("with xmlnamespaces ('urn:x' as p) select id from t union all select id from u for xml auto"));

    /// <summary>The predefined xml prefix binds only to its own URI, and emits no declaration.</summary>
    [TestMethod]
    public void Namespaces_PredefinedXmlPrefix_EmitsNothing()
        => AreEqual("""<row id="1"/><row id="2"/><row id="3"/>""",
            Xml("with xmlnamespaces ('http://www.w3.org/XML/1998/namespace' as xml) select id from t for xml raw"));

    /// <summary>The prefix scopes one statement, so the next one in the batch declares nothing.</summary>
    [TestMethod]
    public void Namespaces_ScopeEndsWithTheStatement()
    {
        using var connection = Seeded().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "with xmlnamespaces ('urn:x' as p) select id from t where id=1 for xml raw; select id from t where id=1 for xml raw";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("""<row xmlns:p="urn:x" id="1"/>""", reader.GetString(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("""<row id="1"/>""", reader.GetString(0));
    }

    [TestMethod]
    public void Namespaces_ViewBody_Accepted()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create view vv as with xmlnamespaces ('urn:x' as p) select id, a from t");
        AreEqual(1, sim.ExecuteScalar("select id from vv where id=1"));
    }

    [TestMethod]
    public void Namespaces_DynamicSql_Declares()
        => AreEqual("""<row xmlns:p="urn:x" id="1"/>""",
            Xml("exec('with xmlnamespaces (''urn:x'' as p) select id from t where id=1 for xml raw')"));

    [TestMethod]
    public void Namespaces_AfterCte_Msg102()
        => Seeded().AssertSqlError("with c as (select id from t), xmlnamespaces ('urn:x' as p) select id from c for xml raw", 102,
            "Incorrect syntax near 'xmlnamespaces'.");

    [TestMethod]
    public void Namespaces_EmptyList_Msg102()
        => Seeded().AssertSqlError("with xmlnamespaces () select id from t for xml raw", 102,
            "Incorrect syntax near ')'.");

    // ---- WITH XMLNAMESPACES: rejections ----

    [TestMethod]
    public void Namespaces_XmlPrefixWrongUri_Msg6872State1()
    {
        var ex = Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as xml) select id from t for xml raw", 6872);
        AreEqual("XML namespace prefix 'xml' can only be associated with the URI http://www.w3.org/XML/1998/namespace. This URI cannot be used with other prefixes.", ex.Message);
        AreEqual(1, ex.State);
    }

    [TestMethod]
    public void Namespaces_XmlUriWrongPrefix_Msg6872State2()
    {
        var ex = Seeded().AssertSqlError("with xmlnamespaces ('http://www.w3.org/XML/1998/namespace' as p) select id from t for xml raw", 6872);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    public void Namespaces_XmlnsPrefix_Msg6871()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as xmlns) select id from t for xml raw", 6871,
            "Prefix 'xmlns' used in WITH XMLNAMESPACES is reserved and cannot be used as a user-defined prefix.");

    [TestMethod]
    public void Namespaces_DelimitedXmlnsPrefix_Msg6871()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as [xmlns]) select id from t for xml raw", 6871);

    [TestMethod]
    public void Namespaces_DuplicatePrefix_Msg6869()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p, 'urn:y' as p) select id from t for xml raw", 6869,
            "Attempt to redefine namespace prefix 'p'");

    [TestMethod]
    public void Namespaces_DuplicateDefault_Msg6869NamesDefault()
        => Seeded().AssertSqlError("with xmlnamespaces (default 'urn:d', default 'urn:e') select id from t for xml raw", 6869,
            "Attempt to redefine namespace prefix 'default'");

    [TestMethod]
    public void Namespaces_PrefixNotAnXmlName_Msg6870()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as [p q]) select id from t for xml raw", 6870,
            "Prefix 'p q' used in WITH XMLNAMESPACES clause contains an invalid XML identifier. ' '(0x0020) is the first character at fault.");

    [TestMethod]
    public void Namespaces_PrefixStartsWithDigit_Msg6870()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as [1p]) select id from t for xml raw", 6870,
            "Prefix '1p' used in WITH XMLNAMESPACES clause contains an invalid XML identifier. '1'(0x0031) is the first character at fault.");

    [TestMethod]
    public void Namespaces_EmptyUri_Msg6874()
        => Seeded().AssertSqlError("with xmlnamespaces ('' as p) select id from t for xml raw", 6874,
            "Empty URI is not allowed in WITH XMLNAMESPACES clause.");

    [TestMethod]
    public void Namespaces_EmptyDefaultUri_Msg6874()
        => Seeded().AssertSqlError("with xmlnamespaces (default '') select id from t for xml raw", 6874);

    /// <summary>The prefix rules outrank the URI rule: an empty URI on a reserved prefix still reports the prefix.</summary>
    [TestMethod]
    public void Namespaces_PrefixRulesPrecedeEmptyUri()
        => Seeded().AssertSqlError("with xmlnamespaces ('' as xml) select id from t", 6872);

    /// <summary>The clause is validated even on a statement carrying no FOR XML at all.</summary>
    [TestMethod]
    public void Namespaces_ValidatedWithoutForXml()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p, 'urn:y' as p) select id from t", 6869);

    [TestMethod]
    public void Namespaces_RedefiningXsiUnderXsinil_Msg6873()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as xsi) select id, cast(null as int) as z from t for xml raw, elements xsinil", 6873,
            "Redefinition of 'xsi' XML namespace prefix is not supported with ELEMENTS XSINIL option of FOR XML.");

    [TestMethod]
    public void Namespaces_WithExplicitMode_Msg6868()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p) select id from t for xml explicit", 6868,
            "The following FOR XML features are not supported with WITH XMLNAMESPACES list: EXPLICIT mode, XMLSCHEMA and XMLDATA directives.");

    [TestMethod]
    public void Namespaces_WithXmlschema_Msg6868()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p) select id from t for xml raw, xmlschema", 6868);

    [TestMethod]
    public void Namespaces_OnWriteStatementSelect_StillMsg6819()
        => Seeded().AssertSqlError("with xmlnamespaces ('urn:x' as p) select id into zz from t for xml raw", 6819,
            "The FOR XML clause is not allowed in a SELECT INTO statement.");

    // ---- BINARY BASE64 ----

    /// <summary>
    /// Binary fixture: <c>bt</c> carries a single-column primary key and a NULL
    /// binary row, <c>bn</c> has no key at all, and <c>bc</c> has a composite
    /// key whose value needs escaping.
    /// </summary>
    private const string BinaryFixture = """
        create table bt (id int primary key, bin varbinary(10), s nvarchar(10));
        insert bt values (1,0x0102,'p'),(2,null,'q');
        create table bn (id int, bin varbinary(10));
        insert bn values (1,0x0102);
        create table bc (k1 int not null, k2 nvarchar(5) not null, bin varbinary(10), constraint pk_bc primary key (k1,k2));
        insert bc values (1,'a&b',0x0A0B);
        """;

    private static Simulation Binary()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(BinaryFixture);
        return sim;
    }

    private static string BinaryXml(string query) => (string)Binary().ExecuteScalar(query)!;

    [TestMethod]
    public void BinaryBase64_Raw()
        => AreEqual("""<row id="1" bin="AQI="/><row id="2"/>""",
            BinaryXml("select id, bin from bt for xml raw, binary base64"));

    [TestMethod]
    public void BinaryBase64_Auto()
        => AreEqual("""<bt id="1" bin="AQI="/><bt id="2"/>""",
            BinaryXml("select id, bin from bt for xml auto, binary base64"));

    [TestMethod]
    public void BinaryBase64_ComposesWithOtherOptions()
        => AreEqual("<r><row><id>1</id><bin>AQI=</bin></row><row><id>2</id></row></r>",
            BinaryXml("select id, bin from bt for xml raw, elements, binary base64, root('r')"));

    /// <summary>PATH base64-encodes binary whether or not the option is written.</summary>
    [TestMethod]
    public void BinaryBase64_PathIsUnaffected()
    {
        const string Expected = "<row><id>1</id><bin>AQI=</bin></row><row><id>2</id></row>";
        AreEqual(Expected, BinaryXml("select id, bin from bt for xml path"));
        AreEqual(Expected, BinaryXml("select id, bin from bt for xml path, binary base64"));
    }

    [TestMethod]
    public void BinaryBase64_LegacyImageColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table im (id int, b image); insert im values (1,0x0102)");
        AreEqual("""<row id="1" b="AQI="/>""", sim.ExecuteScalar("select id, b from im for xml raw, binary base64"));
    }

    [TestMethod]
    public void Binary_HexIsNotAValidEncoding_Msg102()
        => Binary().AssertSqlError("select id, bin from bt for xml raw, binary hex", 102,
            "Incorrect syntax near 'hex'.");

    [TestMethod]
    public void Binary_RawWithoutOption_Msg6829()
        => Binary().AssertSqlError("select id, bin from bt for xml raw", 6829,
            "FOR XML EXPLICIT and RAW modes currently do not support addressing binary data as URLs in column 'bin'. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.");

    // ---- AUTO binary dbobject addressing (no BINARY BASE64) ----

    [TestMethod]
    public void AutoBinary_AddressesDbobjectUrl()
        => AreEqual("""<bt id="1" bin="dbobject/bt[@id='1']/@bin"/><bt id="2"/>""",
            BinaryXml("select id, bin from bt for xml auto"));

    /// <summary>The reference is written from base names — the element takes the alias, the URL doesn't.</summary>
    [TestMethod]
    public void AutoBinary_UrlUsesBaseNamesNotAliases()
    {
        AreEqual("""<b id="1" bin="dbobject/bt[@id='1']/@bin"/><b id="2"/>""",
            BinaryXml("select id, bin from bt b for xml auto"));
        AreEqual("""<bt zz="1" bin="dbobject/bt[@id='1']/@bin"/><bt zz="2"/>""",
            BinaryXml("select id as zz, bin from bt for xml auto"));
    }

    [TestMethod]
    public void AutoBinary_UnderElements()
        => AreEqual("<bt><id>1</id><bin>dbobject/bt[@id='1']/@bin</bin></bt><bt><id>2</id></bt>",
            BinaryXml("select id, bin from bt for xml auto, elements"));

    /// <summary>A composite key joins its terms with real's URL-escaped separator; the value keeps ordinary XML escaping.</summary>
    [TestMethod]
    public void AutoBinary_CompositeKeyAndEscaping()
        => AreEqual("""<bc k1="1" k2="a&amp;b" bin="dbobject/bc[@k1='1'%20and%20@k2='a&amp;b']/@bin"/>""",
            BinaryXml("select k1, k2, bin from bc for xml auto"));

    [TestMethod]
    public void AutoBinary_TwoAliasesOfOneColumnShareTheUrl()
        => AreEqual("""<bt id="1" bin="dbobject/bt[@id='1']/@bin" b2="dbobject/bt[@id='1']/@bin"/><bt id="2"/>""",
            BinaryXml("select id, bin, bin as b2 from bt for xml auto"));

    [TestMethod]
    public void AutoBinary_NoPrimaryKey_Msg6831()
        => Binary().AssertSqlError("select id, bin from bn for xml auto", 6831,
            "FOR XML AUTO requires primary keys to create references for 'bin'. Select primary keys, or use BINARY BASE64 to obtain binary data in encoded form if no primary keys exist.");

    [TestMethod]
    public void AutoBinary_KeyNotProjected_Msg6831()
        => Binary().AssertSqlError("select bin from bt for xml auto", 6831);

    [TestMethod]
    public void AutoBinary_PartialCompositeKeyProjected_Msg6831()
        => Binary().AssertSqlError("select k1, bin from bc for xml auto", 6831);

    [TestMethod]
    public void AutoBinary_ComputedColumn_Msg6830()
        => Binary().AssertSqlError("select id, cast(0x01 as varbinary(4)) as c from bt for xml auto", 6830,
            "FOR XML AUTO could not find the table owning the following column 'c' to create a URL address for it. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.");

    [TestMethod]
    public void AutoBinary_DerivedTable_Msg6830()
        => Binary().AssertSqlError("select id, bin from (select * from bt) d for xml auto", 6830);

    [TestMethod]
    public void AutoBinary_SetOperation_Msg6830()
        => Binary().AssertSqlError("select id, bin from bt union all select id, bin from bn for xml auto", 6830);

    // ---- FOR XML on a SELECT that doesn't return to the client ----

    private static Simulation WriteTarget()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table z (x nvarchar(max))");
        return sim;
    }

    [TestMethod]
    public void InsertSelect_ForXml_Msg6819()
        => WriteTarget().AssertSqlError("insert z select 1 as a for xml raw", 6819,
            "The FOR XML clause is not allowed in a INSERT statement.");

    [TestMethod]
    public void InsertSelect_ForXml_PrecedesNameErrors()
        => WriteTarget().AssertSqlError("insert z select 1 as [a b] for xml path", 6819,
            "The FOR XML clause is not allowed in a INSERT statement.");

    [TestMethod]
    public void SelectInto_ForXml_Msg6819()
        => WriteTarget().AssertSqlError("select 1 as a into z2 for xml raw", 6819,
            "The FOR XML clause is not allowed in a SELECT INTO statement.");

    [TestMethod]
    public void AssignmentSelect_ForXml_Msg6819State3()
    {
        var ex = WriteTarget().AssertSqlError("declare @x nvarchar(max); select @x = 1 for xml raw", 6819);
        AreEqual("The FOR XML clause is not allowed in a ASSIGNMENT statement.", ex.Message);
        AreEqual(3, ex.State);
    }

    [TestMethod]
    public void InsertSelect_NestedForXmlSubquery_Allowed()
    {
        var sim = WriteTarget();
        _ = sim.ExecuteNonQuery("insert z select (select 1 as a for xml raw)");
        AreEqual("""<row a="1"/>""", sim.ExecuteScalar("select x from z"));
    }

    [TestMethod]
    public void InsertSelect_ForXmlDerivedTable_Allowed()
    {
        var sim = WriteTarget();
        _ = sim.ExecuteNonQuery("insert z select d.v from (select 1 as a for xml raw) d(v)");
        AreEqual("""<row a="1"/>""", sim.ExecuteScalar("select x from z"));
    }

    [TestMethod]
    public void SetVariable_ForXmlSubquery_Allowed()
        => AreEqual("""<row a="1"/>""",
            new Simulation().ExecuteScalar("declare @x nvarchar(max); set @x = (select 1 as a for xml raw); select @x"));

    // ---- duplicate options (every mode) ----

    [TestMethod]
    public void DuplicateType_Msg102NearXml()
        => new Simulation().AssertSqlError("select 1 a for xml raw, type, type", 102, "Incorrect syntax near 'XML'.");

    [TestMethod]
    public void DuplicateRoot_Msg102NearXml()
        => new Simulation().AssertSqlError("select 1 a for xml raw, root('a'), root('b')", 102, "Incorrect syntax near 'XML'.");

    [TestMethod]
    public void DuplicateBinaryBase64_Msg102NearXml()
        => new Simulation().AssertSqlError("select 1 a for xml auto, binary base64, binary base64", 102, "Incorrect syntax near 'XML'.");

    /// <summary>ELEMENTS repeats the same way, whether or not the second one carries a modifier.</summary>
    [TestMethod]
    public void DuplicateElements_Msg102NearXml()
    {
        new Simulation().AssertSqlError("select 1 a for xml raw, elements, elements", 102, "Incorrect syntax near 'XML'.");
        new Simulation().AssertSqlError("select 1 a for xml raw, elements xsinil, elements", 102, "Incorrect syntax near 'XML'.");
        new Simulation().AssertSqlError("select 1 a for xml path, elements, elements", 102, "Incorrect syntax near 'XML'.");
    }

    /// <summary>An intervening option doesn't reset the rule.</summary>
    [TestMethod]
    public void DuplicateOptionAcrossOthers_Msg102NearXml()
        => new Simulation().AssertSqlError("select 1 a for xml raw, type, root, type", 102, "Incorrect syntax near 'XML'.");

    [TestMethod]
    public void EachOptionOnceIsFine()
        => AreEqual("<r><row><a>1</a></row></r>",
            (string)new Simulation().ExecuteScalar("select 1 a for xml raw, elements, root('r')")!);

    // ---- EXPLICIT: the universal table ----

    private static string ExplicitXml(string query) => (string)new Simulation().ExecuteScalar(query)!;

    /// <summary>
    /// A universal table whose row order is a separate column, so the ordering
    /// the tree-building protocol depends on can be driven without the sort key
    /// entering the projection.
    /// </summary>
    private const string UniversalTable = """
        create table ut (ord int, tg int, pr int, pid int, cid int, gid int);
        """;

    private static string ProtocolXml(string rows, string query)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(UniversalTable + "insert ut values " + rows);
        return (string)sim.ExecuteScalar(query)!;
    }

    private const string ProtocolQuery =
        "select tg as Tag, pr as Parent, pid as [p!1!id], cid as [c!2!cid], gid as [g!3!gid] from ut order by ord for xml explicit";

    [TestMethod]
    public void Explicit_SingleTag()
        => AreEqual("""<emp id="1" nm="bob"/>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [emp!1!id], 'bob' as [emp!1!nm] for xml explicit"));

    [TestMethod]
    public void Explicit_TwoLevelNesting()
        => AreEqual("""<p id="1"><c cid="10"/><c cid="11"/></p><p id="2"><c cid="12"/></p>""",
            ProtocolXml("(1,1,null,1,null,null),(2,2,1,null,10,null),(3,2,1,null,11,null),(4,1,null,2,null,null),(5,2,1,null,12,null)", ProtocolQuery));

    /// <summary>A row for an outer tag closes everything the inner rows opened.</summary>
    [TestMethod]
    public void Explicit_ThreeLevelsAndSiblingClose()
        => AreEqual("""<p id="1"><c cid="10"><g gid="100"/></c><c cid="11"/></p><p id="2"/>""",
            ProtocolXml("(1,1,null,1,null,null),(2,2,1,null,10,null),(3,3,2,null,null,100),(4,2,1,null,11,null),(5,1,null,2,null,null)", ProtocolQuery));

    /// <summary>Rows never collapse by value the way AUTO's levels do — every row opens its own element.</summary>
    [TestMethod]
    public void Explicit_IdenticalRowsDoNotMerge()
        => AreEqual("""<p id="1"><c cid="10"/></p><p id="1"/>""",
            ProtocolXml("(1,1,null,1,null,null),(2,2,1,null,10,null),(3,1,null,1,null,null)", ProtocolQuery));

    /// <summary>Parent 0 means the document level, exactly as NULL does.</summary>
    [TestMethod]
    public void Explicit_ParentZeroIsDocumentLevel()
        => AreEqual("""<e a="1"/>""",
            ExplicitXml("select 1 as Tag, 0 as Parent, 1 as [e!1!a] for xml explicit"));

    /// <summary>A tag-column value belonging to another tag is ignored, however non-NULL.</summary>
    [TestMethod]
    public void Explicit_ColumnsOfOtherTagsAreIgnored()
        => AreEqual("""<p id="1"/>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [p!1!id], 99 as [c!2!cid] for xml explicit"));

    /// <summary>Attributes belong to the start tag, so they precede content whatever the written order.</summary>
    [TestMethod]
    public void Explicit_AttributesPrecedeContent()
        => AreEqual("<e b=\"y\"><a>x</a><c>z</c></e>",
            ExplicitXml("select 1 as Tag, null as Parent, 'x' as [e!1!a!element], 'y' as [e!1!b], 'z' as [e!1!c!element] for xml explicit"));

    [TestMethod]
    public void Explicit_TextContentAndChildElementsInterleave()
        => AreEqual("<e>A<k>B</k>C</e>",
            ExplicitXml("select 1 as Tag, null as Parent, 'A' as [e!1], 'B' as [e!1!k!element], 'C' as [e!1] for xml explicit"));

    // ---- EXPLICIT: the column-name grammar ----

    /// <summary>An absent or empty attribute name puts the value in the element's own text.</summary>
    [TestMethod]
    public void Explicit_UnnamedAttributeIsText()
    {
        AreEqual("<e>txt</e>", ExplicitXml("select 1 as Tag, null as Parent, 'txt' as [e!1] for xml explicit"));
        AreEqual("<e>v</e>", ExplicitXml("select 1 as Tag, null as Parent, 'v' as [e!1!] for xml explicit"));
        AreEqual("<e>v</e>", ExplicitXml("select 1 as Tag, null as Parent, 'v' as [e!1!!element] for xml explicit"));
        AreEqual("<e>abcd</e>", ExplicitXml("select 1 as Tag, null as Parent, 'ab' as [e!1], 'cd' as [e!1!] for xml explicit"));
    }

    /// <summary>Directive words are matched case-insensitively.</summary>
    [TestMethod]
    public void Explicit_DirectiveNamesAreCaseInsensitive()
    {
        AreEqual("<e><a>txt</a></e>", ExplicitXml("select 1 as Tag, null as Parent, 'txt' as [e!1!a!element] for xml explicit"));
        AreEqual("<e><a>txt</a></e>", ExplicitXml("select 1 as Tag, null as Parent, 'txt' as [e!1!a!ELEMENT] for xml explicit"));
        AreEqual("<e><a>txt</a></e>", ExplicitXml("select 1 as Tag, null as Parent, 'txt' as [e!1!a!Element] for xml explicit"));
    }

    /// <summary>NULL: attributes and elements alike vanish, only elementxsinil marks it.</summary>
    [TestMethod]
    public void Explicit_NullHandlingPerDirective()
    {
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, null as [e!1!a] for xml explicit"));
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, null as [e!1!a!element] for xml explicit"));
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, null as [e!1] for xml explicit"));
        AreEqual("""<e xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a xsi:nil="true"/></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, null as [e!1!a!elementxsinil] for xml explicit"));
    }

    /// <summary>
    /// The xsi declaration follows the same placement rule the other modes use
    /// — the ROOT when there is one, else every top-level element — and any
    /// elementxsinil column arms it whether or not a NULL turns up.
    /// </summary>
    [TestMethod]
    public void Explicit_XsinilDeclarationPlacement()
    {
        AreEqual("""<r xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><e><a xsi:nil="true"/></e></r>""",
            ExplicitXml("select 1 as Tag, null as Parent, null as [e!1!a!elementxsinil] for xml explicit, root('r')"));
        AreEqual("""<e xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" z="5"><a>v</a></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 5 as [e!1!z], 'v' as [e!1!a!elementxsinil] for xml explicit"));
        AreEqual("""<p xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><a xsi:nil="true"/><c/></p>""",
            ExplicitXml("select 1 as Tag, null as Parent, null as [p!1!a!elementxsinil], null as [c!2!b] union all select 2,1,null,null for xml explicit"));
    }

    /// <summary>The xml directive is a passthrough — no escaping, no well-formedness check.</summary>
    [TestMethod]
    public void Explicit_XmlDirectiveIsRawPassthrough()
    {
        AreEqual("<e><a><b>x</b></a></e>", ExplicitXml("select 1 as Tag, null as Parent, '<b>x</b>' as [e!1!a!xml] for xml explicit"));
        AreEqual("<e><b>x</b></e>", ExplicitXml("select 1 as Tag, null as Parent, '<b>x</b>' as [e!1!!xml] for xml explicit"));
        AreEqual("<e><x>a & b < c</x></e>", ExplicitXml("select 1 as Tag, null as Parent, 'a & b < c' as [e!1!x!xml] for xml explicit"));
        AreEqual("<e><x><a></x></e>", ExplicitXml("select 1 as Tag, null as Parent, '<a>' as [e!1!x!xml] for xml explicit"));
    }

    [TestMethod]
    public void Explicit_CdataDirective()
    {
        AreEqual("<e><![CDATA[x<y]]></e>", ExplicitXml("select 1 as Tag, null as Parent, 'x<y' as [e!1!!cdata] for xml explicit"));
        AreEqual("<e><a><![CDATA[zz]]></a></e>", ExplicitXml("select 1 as Tag, null as Parent, 'zz' as [e!1!a!cdata] for xml explicit"));
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, cast(null as varchar(9)) as [e!1!!cdata] for xml explicit"));
    }

    /// <summary>A CDATA section can't escape, so real breaks it apart after the first ']' of every ']]&gt;'.</summary>
    [TestMethod]
    public void Explicit_CdataSplitsOnSectionTerminator()
    {
        AreEqual("<e><![CDATA[a]]]><![CDATA[]>b]]></e>", ExplicitXml("select 1 as Tag, null as Parent, 'a]]>b' as [e!1!!cdata] for xml explicit"));
        AreEqual("<e><![CDATA[a]]b]]></e>", ExplicitXml("select 1 as Tag, null as Parent, 'a]]b' as [e!1!!cdata] for xml explicit"));
        AreEqual("<e><![CDATA[a]]]]><![CDATA[]>b]]></e>", ExplicitXml("select 1 as Tag, null as Parent, 'a]]]>b' as [e!1!!cdata] for xml explicit"));
        AreEqual("<e><![CDATA[a]]]><![CDATA[]>]]]><![CDATA[]>b]]></e>", ExplicitXml("select 1 as Tag, null as Parent, 'a]]>]]>b' as [e!1!!cdata] for xml explicit"));
    }

    /// <summary>A hidden column emits nothing but still declares its tag.</summary>
    [TestMethod]
    public void Explicit_HideDirective()
    {
        AreEqual("""<e a="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], 'secret' as [e!1!h!hide] for xml explicit"));
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, 'v' as [e!1!!hide] for xml explicit"));
        AreEqual("""<e a="1"><f/></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], cast(null as int) as [f!2!b!hide] union all select 2,1,null,9 for xml explicit"));
    }

    /// <summary>hide combines with a content directive; the column just disappears.</summary>
    [TestMethod]
    public void Explicit_HideBeatsContentDirective()
    {
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a!element!hide] for xml explicit"));
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a!hide!element] for xml explicit"));
        AreEqual("<e/>", ExplicitXml("select 1 as Tag, null as Parent, '<b/>' as [e!1!a!xml!hide] for xml explicit"));
    }

    /// <summary>Outside an inline schema, id / idref / nmtoken serialize as ordinary attributes.</summary>
    [TestMethod]
    public void Explicit_IdentityDirectivesAreOrdinaryAttributes()
    {
        AreEqual("""<e a="1" b="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a!id], 1 as [e!1!b!idref] for xml explicit"));
        AreEqual("""<e a="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a!nmtoken] for xml explicit"));
        AreEqual("<e>v</e>", ExplicitXml("select 1 as Tag, null as Parent, 'v' as [e!1!!id] for xml explicit"));
        AreEqual("<e><a>1</a></e>", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a!id!element] for xml explicit"));
    }

    /// <summary>Duplicate attribute names on one tag pass straight through — real writes the ill-formed markup too.</summary>
    [TestMethod]
    public void Explicit_DuplicateAttributeNamesPassThrough()
        => AreEqual("""<e a="1" a="2"/>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], 2 as [e!1!a] for xml explicit"));

    /// <summary>EXPLICIT emits names verbatim — neither RAW's escaping nor PATH's rejection applies.</summary>
    [TestMethod]
    public void Explicit_NamesAreWrittenVerbatim()
    {
        AreEqual("""<e f a b="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e f!1!a b] for xml explicit"));
        AreEqual("""<1e 2a="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [1e!1!2a] for xml explicit"));
        AreEqual("""<p:e q:a="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [p:e!1!q:a] for xml explicit"));
        AreEqual("""<e xmlns="1"/>""", ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!xmlns] for xml explicit"));
    }

    // ---- EXPLICIT: xmltext ----

    /// <summary>An unnamed xmltext folds the overflow element's attributes and content onto the tag itself.</summary>
    [TestMethod]
    public void Explicit_XmlTextMergesOntoTag()
        => AreEqual("""<e a="1" x="1">t</e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], '<over x=\"1\">t</over>' as [e!1!!xmltext] for xml explicit"));

    /// <summary>A named one keeps the attributes but takes the column's name.</summary>
    [TestMethod]
    public void Explicit_XmlTextNamedBecomesChildElement()
    {
        AreEqual("""<e a="1"><ov x="1">t</ov></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], '<over x=\"1\">t</over>' as [e!1!ov!xmltext] for xml explicit"));
        AreEqual("<e><ov/></e>", ExplicitXml("select 1 as Tag, null as Parent, '<over/>' as [e!1!ov!xmltext] for xml explicit"));
    }

    /// <summary>Merged attributes land after the tag's own, and content keeps its select-list position.</summary>
    [TestMethod]
    public void Explicit_XmlTextOrdering()
        => AreEqual("""<e a="1" p="1"><k/>txt<c>z</c></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], '<over p=\"1\"><k/>txt</over>' as [e!1!!xmltext], 'z' as [e!1!c!element] for xml explicit"));

    /// <summary>An overflow attribute the row already wrote is dropped; the element still stops self-closing.</summary>
    [TestMethod]
    public void Explicit_XmlTextAttributeCollisionAndForcedOpenTag()
    {
        AreEqual("""<e a="1"></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], '<over a=\"9\"/>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e a="1"></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], '<over/>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e a="1"/>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a], cast(null as varchar(99)) as [e!1!!xmltext] for xml explicit"));
    }

    /// <summary>The overflow's content passes through as written, whitespace and nesting included.</summary>
    [TestMethod]
    public void Explicit_XmlTextContentIsVerbatim()
    {
        AreEqual("""<e p="1">  <k  ></k>  </e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over  p = \"1\" >  <k  ></k>  </over>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e xmlns="urn:z"><k/></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over xmlns=\"urn:z\"><k/></over>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("<e><a>in</a></e>",
            ExplicitXml("select 1 as Tag, null as Parent, '<a><a>in</a></a>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e p="1">z</e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '  <a p=\"1\">z</a>  ' as [e!1!!xmltext] for xml explicit"));
        AreEqual("<e></e>", ExplicitXml("select 1 as Tag, null as Parent, '<a/><b/>' as [e!1!!xmltext] for xml explicit"));
    }

    /// <summary>
    /// An overflow attribute value keeps its source text, only the delimiter
    /// normalizing to <c>"</c> — so <c>&gt;</c> stays literal, an entity stays
    /// an entity, and a <c>"</c> out of a single-quoted value comes back
    /// unescaped (ill-formed markup real writes too).
    /// </summary>
    [TestMethod]
    public void Explicit_XmlTextAttributeValuesAreVerbatim()
    {
        AreEqual("""<e p="a&amp;b" q="c&lt;d"></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over p=''a&amp;b'' q=\"c&lt;d\"/>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e p="a>b">z</e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over p=\"a>b\">z</over>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e p="a&gt;b"></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over p=\"a&gt;b\"/>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e p="a"b"></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over p=''a\"b''/>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e p="a&#x09;b&#x0A;c"></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over p=\"a&#x09;b&#x0A;c\"/>' as [e!1!!xmltext] for xml explicit"));
        AreEqual("""<e><ov p="a>b"/></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, '<over p=\"a>b\"/>' as [e!1!ov!xmltext] for xml explicit"));
    }

    // ---- EXPLICIT: options ----

    [TestMethod]
    public void Explicit_Root()
        => AreEqual("""<r><e a="1"/></r>""",
            ExplicitXml("select 1 as Tag, null as Parent, 1 as [e!1!a] for xml explicit, root('r')"));

    [TestMethod]
    public void Explicit_BinaryBase64()
        => AreEqual("""<e a="AQI="/>""",
            ExplicitXml("select 1 as Tag, null as Parent, cast(0x0102 as varbinary(9)) as [e!1!a] for xml explicit, binary base64"));

    [TestMethod]
    public void Explicit_ValueFormattingMatchesTheOtherModes()
        => AreEqual("""<e bt="1" f="1.500000000000000e+000" d="2020-01-02T03:04:05" g="0E984725-C51C-4BF4-9960-E1C80E27ABA0"/>""",
            ExplicitXml("""
                select 1 as Tag, null as Parent, cast(1 as bit) as [e!1!bt], cast(1.5 as float) as [e!1!f],
                       cast('2020-01-02T03:04:05' as datetime2(3)) as [e!1!d],
                       cast('0E984725-C51C-4BF4-9960-E1C80E27ABA0' as uniqueidentifier) as [e!1!g]
                for xml explicit
                """));

    /// <summary>Element text and attribute values take the mode-independent escaping table.</summary>
    [TestMethod]
    public void Explicit_Escaping()
        => AreEqual("""<e y="a&quot;b"><x>a&amp;b&lt;c&gt;d</x></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, 'a&b<c>d' as [e!1!x!element], 'a\"b' as [e!1!y] for xml explicit"));

    /// <summary>An empty rowset behaves as it does in every mode: no row, or one NULL under TYPE.</summary>
    [TestMethod]
    public void Explicit_EmptyRowset()
    {
        IsNull(new Simulation().ExecuteScalar("select 1 as Tag, null as Parent, 1 as [e!1!a] where 1=0 for xml explicit"));
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select 1 as Tag, null as Parent, 1 as [e!1!a] where 1=0 for xml explicit, type"));
    }

    /// <summary>
    /// An <c>xml</c>-typed column becomes a child element holding its nodes,
    /// the rule RAW and AUTO also take (an attribute can't hold nodes). Real
    /// re-serializes the embedded fragment and stamps <c>xmlns=""</c> on its
    /// unprefixed top-level elements in EXPLICIT alone; the simulator embeds
    /// the stored text as the other modes do.
    /// </summary>
    [TestMethod]
    public void Explicit_XmlTypedColumnEmbedsAsNodes()
        => AreEqual("<e><a><b>x</b></a></e>",
            ExplicitXml("select 1 as Tag, null as Parent, cast('<b>x</b>' as xml) as [e!1!a] for xml explicit"));

    [TestMethod]
    public void Explicit_NestedTypeSubqueryEmbedsAsNodes()
        => AreEqual("""<e><a><i v="9"/></a></e>""",
            ExplicitXml("select 1 as Tag, null as Parent, (select 1 as Tag, null as Parent, 9 as [i!1!v] for xml explicit, type) as [e!1!a] for xml explicit"));

    // ---- EXPLICIT: compile-time rejections ----

    [TestMethod]
    public void Explicit_FewerThanThreeColumns_Msg6801()
    {
        new Simulation().AssertSqlError("select 1 as [e!1!a] for xml explicit", 6801,
            "FOR XML EXPLICIT requires at least three columns, including the tag column, the parent column, and at least one data column.");
        _ = new Simulation().AssertSqlError("select 1 as Tag, 1 as [e!1!a] for xml explicit", 6801);
    }

    [TestMethod]
    public void Explicit_InvalidColumnName_Msg6802()
    {
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as plaincol for xml explicit", 6802,
            "FOR XML EXPLICIT query contains the invalid column name 'plaincol'. Use the TAGNAME!TAGID!ATTRIBUTENAME[!..] format where TAGID is a positive integer.");
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!] for xml explicit", 6802);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!0!a] for xml explicit", 6802);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!x!a] for xml explicit", 6802);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!-1!a] for xml explicit", 6802);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [!1!a] for xml explicit", 6802);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 for xml explicit", 6802);
    }

    /// <summary>Tag ids are unbounded; only the row values have to match a declared one.</summary>
    [TestMethod]
    public void Explicit_LargeTagNumbers()
    {
        AreEqual("""<e a="1"/>""", ExplicitXml("select 255 as Tag, null as Parent, 1 as [e!255!a] for xml explicit"));
        AreEqual("""<e a="1"/>""", ExplicitXml("select 100000 as Tag, null as Parent, 1 as [e!100000!a] for xml explicit"));
    }

    /// <summary>Tag and Parent must both be int; a compile-time miss is state 1.</summary>
    [TestMethod]
    public void Explicit_TagAndParentTypes_Msg6803And6804State1()
    {
        var tag = new Simulation().AssertSqlError("select '1' as Tag, null as Parent, 1 as [e!1!a] for xml explicit", 6803);
        AreEqual("FOR XML EXPLICIT requires the first column to hold positive integers that represent XML tag IDs.", tag.Message);
        AreEqual(1, tag.State);
        _ = new Simulation().AssertSqlError("select cast(1 as bigint) as Tag, null as Parent, 1 as [e!1!a] for xml explicit", 6803);
        _ = new Simulation().AssertSqlError("select cast(1 as smallint) as Tag, null as Parent, 1 as [e!1!a] for xml explicit", 6803);
        var parent = new Simulation().AssertSqlError("select 1 as Tag, cast(null as bigint) as Parent, 1 as [e!1!a] for xml explicit", 6804);
        AreEqual("FOR XML EXPLICIT requires the second column to hold NULL or nonnegative integers that represent XML parent tag IDs.", parent.Message);
        AreEqual(1, parent.State);
    }

    /// <summary>The type check runs even over an empty rowset — it is compile time.</summary>
    [TestMethod]
    public void Explicit_TypeCheckIsCompileTime()
        => new Simulation().AssertSqlError("select cast(1 as bigint) as Tag, null as Parent, 1 as [e!1!a] where 1=0 for xml explicit", 6803);

    [TestMethod]
    public void Explicit_TagAndParentNames_Msg6820()
    {
        new Simulation().AssertSqlError("select null as Parent, 1 as Tag, 1 as [e!1!a] for xml explicit", 6820,
            "FOR XML EXPLICIT requires column 1 to be named 'TAG' instead of 'Parent'.");
        new Simulation().AssertSqlError("select 1 as Tag, 1 as zz, 1 as [e!1!a] for xml explicit", 6820,
            "FOR XML EXPLICIT requires column 2 to be named 'PARENT' instead of 'zz'.");
        AreEqual("""<e a="1"/>""", ExplicitXml("select 1 as tag, null as parent, 1 as [e!1!a] for xml explicit"));
    }

    [TestMethod]
    public void Explicit_TagRedeclaredWithAnotherName_Msg6812()
    {
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a], 2 as [f!1!b] for xml explicit", 6812,
            "XML tag ID 1 that was originally declared as 'e' is being redeclared as 'f'.");
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a], 2 as [E!1!b] for xml explicit", 6812,
            "XML tag ID 1 that was originally declared as 'e' is being redeclared as 'E'.");
    }

    [TestMethod]
    public void Explicit_UnknownDirective_Msg6824()
    {
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!bogus] for xml explicit", 6824,
            "In the FOR XML EXPLICIT clause, mode 'bogus' in a column name is invalid.");
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 'v' as [e!1!!] for xml explicit", 6824,
            "In the FOR XML EXPLICIT clause, mode '' in a column name is invalid.");
    }

    /// <summary>Directive-combination rules, in real's own check order.</summary>
    [TestMethod]
    public void Explicit_DirectiveCombinations()
    {
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!element!element] for xml explicit", 6817,
            "FOR XML EXPLICIT cannot combine multiple occurrences of ELEMENT, XML, XMLTEXT, and CDATA in column name 'e!1!a!element!element'.");
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!cdata!element] for xml explicit", 6817);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!elementxsinil!element] for xml explicit", 6817);
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!id!id] for xml explicit", 6813,
            "FOR XML EXPLICIT cannot combine multiple occurrences of ID, IDREF, IDREFS, NMTOKEN, and/or NMTOKENS in column name 'e!1!a!id!id'.");
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!id!idref] for xml explicit", 6813);
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!hide!hide] for xml explicit", 6835,
            "FOR XML EXPLICIT field 'e!1!a!hide!hide' can specify the directive HIDE only once.");
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!id!hide] for xml explicit", 6815,
            "In the FOR XML EXPLICIT clause, ID, IDREF, IDREFS, NMTOKEN, and NMTOKENS attributes cannot be hidden in 'e!1!a!id!hide'.");
        // A duplicate HIDE outranks both of the other combination rules.
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!element!element!hide!hide] for xml explicit", 6835);
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!element!element!id!id] for xml explicit", 6813);
    }

    [TestMethod]
    public void Explicit_Idrefs_Msg6826()
    {
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!idrefs] for xml explicit", 6826,
            "Every IDREFS or NMTOKENS column in a FOR XML EXPLICIT query must appear in a separate SELECT clause, and the instances must be ordered directly after the element to which they belong.");
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a!nmtokens] for xml explicit", 6826);
    }

    [TestMethod]
    public void Explicit_SecondXmlTextOnOneTag_Msg6827()
        => new Simulation().AssertSqlError(
            "select 1 as Tag, null as Parent, '<o1 p=\"1\"/>' as [e!1!!xmltext], '<o2 q=\"2\"/>' as [e!1!!xmltext] for xml explicit", 6827,
            "FOR XML EXPLICIT queries allow only one XMLTEXT column per tag. Column 'e!1!!xmltext' declares another XMLTEXT column that is not permitted.");

    /// <summary>The binary scan runs ahead of the universal table's own shape checks.</summary>
    [TestMethod]
    public void Explicit_BinaryWithoutBase64_Msg6829()
    {
        new Simulation().AssertSqlError("select 1 as Tag, null as Parent, cast(0x01 as varbinary(4)) as [e!1!b] for xml explicit", 6829,
            "FOR XML EXPLICIT and RAW modes currently do not support addressing binary data as URLs in column 'e!1!b'. Remove the column, or use the BINARY BASE64 mode, or create the URL directly using the 'dbobject/TABLE[@PK1=\"V1\"]/@COLUMN' syntax.");
        _ = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as zz, cast(0x01 as varbinary(4)) as [e!1!b] for xml explicit", 6829);
        _ = new Simulation().AssertSqlError("select 1 as Tag, cast(0x01 as varbinary(4)) as b for xml explicit", 6829);
    }

    [TestMethod]
    public void Explicit_ElementsOption_Msg6825()
        => new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a] for xml explicit, elements", 6825,
            "ELEMENTS option is only allowed in RAW, AUTO, and PATH modes of FOR XML.");

    [TestMethod]
    public void Explicit_RowTagArgument_Msg6859()
    {
        var ex = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a] for xml explicit('x')", 6859);
        AreEqual("Row tag name is only allowed with RAW or PATH mode of FOR XML.", ex.Message);
        AreEqual(15, ex.Class);
        _ = new Simulation().AssertSqlError("select 1 a for xml auto('x')", 6859);
    }

    [TestMethod]
    public void Explicit_XmlSchema_Msg3625()
    {
        var ex = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!1!a] for xml explicit, xmlschema", 3625);
        AreEqual("'Inline XSD for FOR XML EXPLICIT' is not yet implemented.", ex.Message);
        AreEqual(17, ex.State);
    }

    [TestMethod]
    public void Explicit_WithXmlNamespaces_Msg6868()
        => new Simulation().AssertSqlError(
            "with xmlnamespaces('urn:x' as p) select 1 as Tag, null as Parent, 1 as [e!1!a] for xml explicit", 6868,
            "The following FOR XML features are not supported with WITH XMLNAMESPACES list: EXPLICIT mode, XMLSCHEMA and XMLDATA directives.");

    [TestMethod]
    public void Explicit_InsertSource_Msg6819()
        => WriteTarget().AssertSqlError("insert z select 1 as Tag, null as Parent, 1 as [e!1!a] for xml explicit", 6819,
            "The FOR XML clause is not allowed in a INSERT statement.");

    // ---- EXPLICIT: per-row protocol violations ----

    /// <summary>A Tag value that isn't a positive integer is the same message at state 2.</summary>
    [TestMethod]
    public void Explicit_TagValue_Msg6803State2()
    {
        foreach (var tag in new[] { "0", "-1", "cast(null as int)" })
        {
            var ex = new Simulation().AssertSqlError($"select {tag} as Tag, null as Parent, 1 as [e!1!a] for xml explicit", 6803);
            AreEqual(2, ex.State);
        }
    }

    [TestMethod]
    public void Explicit_NegativeParentValue_Msg6804State2()
    {
        var ex = new Simulation().AssertSqlError("select 1 as Tag, -1 as Parent, 1 as [e!1!a] for xml explicit", 6804);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    public void Explicit_UndeclaredTagValue_Msg6806()
    {
        var ex = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 1 as [e!2!a] for xml explicit", 6806);
        AreEqual("Undeclared tag ID 1 is used in a FOR XML EXPLICIT query.", ex.Message);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    public void Explicit_UndeclaredParentValue_Msg6807()
    {
        var ex = new Simulation().AssertSqlError("select 1 as Tag, 5 as Parent, 1 as [e!1!a] for xml explicit", 6807);
        AreEqual("Undeclared parent tag ID 5 is used in a FOR XML EXPLICIT query.", ex.Message);
        AreEqual(2, ex.State);
    }

    /// <summary>Rows are taken in the order they arrive — a child ahead of its parent is an error, not a re-sort.</summary>
    [TestMethod]
    public void Explicit_ParentNotOpen_Msg6833()
    {
        new Simulation().AssertSqlError("select 1 as Tag, 1 as Parent, 1 as [e!1!a] for xml explicit", 6833,
            "Parent tag ID 1 is not among the open tags. FOR XML EXPLICIT requires parent tags to be opened first. Check the ordering of the result set.");
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(UniversalTable + "insert ut values (1,2,1,null,10,null),(2,1,null,1,null,null)");
        _ = sim.AssertSqlError(ProtocolQuery, 6833);
    }

    [TestMethod]
    public void Explicit_CircularParentTags_Msg6805()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(UniversalTable + "insert ut values (1,1,null,1,null,null),(2,2,1,null,10,null),(3,1,2,2,null,null)");
        var ex = sim.AssertSqlError(ProtocolQuery, 6805);
        AreEqual("FOR XML EXPLICIT stack overflow occurred. Circular parent tag relationships are not allowed.", ex.Message);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    public void Explicit_XmlTextInvalidDocument_Msg6834()
    {
        var noRoot = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 'plain' as [e!1!!xmltext] for xml explicit", 6834);
        AreEqual("XMLTEXT field '' contains an invalid XML document. Check the root tag and its attributes.", noRoot.Message);
        AreEqual(1, noRoot.State);
        var named = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, 'plain' as [e!1!ov!xmltext] for xml explicit", 6834);
        AreEqual("XMLTEXT field 'ov' contains an invalid XML document. Check the root tag and its attributes.", named.Message);
        var malformed = new Simulation().AssertSqlError("select 1 as Tag, null as Parent, '<a>' as [e!1!!xmltext] for xml explicit", 6834);
        AreEqual(2, malformed.State);
    }
}
