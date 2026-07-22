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
    public void Auto_Join_Deferred()
        => Throws<NotSupportedException>(() => Xml("select t.id, u.a from t join u on t.id = u.id for xml auto"));

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
}
