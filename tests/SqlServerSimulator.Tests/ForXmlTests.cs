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

    [TestMethod]
    public void Auto_SetOperation_NotModeled()
        => Throws<NotSupportedException>(() => Xml("select id from t union all select id from u for xml auto"));

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
}
