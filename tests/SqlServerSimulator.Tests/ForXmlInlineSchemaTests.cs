using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>FOR XML RAW</c> / <c>AUTO</c> with <c>XMLSCHEMA</c> and <c>XMLDATA</c>:
/// the inline XSD or XDR ahead of the rows, the namespace the rows then
/// carry, the session's schema numbering and the refusals. Every expected
/// document is SQL Server 2025's own for the same rows (probed 2026-10-17).
/// </summary>
[TestClass]
public sealed class ForXmlInlineSchemaTests
{
    private const string Xsd = """<xsd:schema targetNamespace="{0}"{1} xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:sqltypes="http://schemas.microsoft.com/sqlserver/2004/sqltypes" elementFormDefault="qualified"><xsd:import namespace="http://schemas.microsoft.com/sqlserver/2004/sqltypes" schemaLocation="http://schemas.microsoft.com/sqlserver/2004/sqltypes/sqltypes.xsd"/>""";

    private const string Varchar10 = """<xsd:simpleType><xsd:restriction base="sqltypes:varchar" sqltypes:localeId="1033" sqltypes:sqlCompareOptions="IgnoreCase IgnoreKanaType IgnoreWidth" sqltypes:sqlSortId="52"><xsd:maxLength value="10"/></xsd:restriction></xsd:simpleType>""";

    private static string Head(string target, string extra = "") => string.Format(System.Globalization.CultureInfo.InvariantCulture, Xsd, target, extra);

    private static System.Data.Common.DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table x (id int not null, s varchar(10), q xml, d datetimeoffset(2), v sql_variant);
            insert x values (1, 'a<b', '<q/>', '2020-01-01 00:00 +00:00', 1);
            create table p (pid int not null primary key, pn varchar(5) not null);
            create table c (cid int not null, pid int, cn nvarchar(5));
            insert p values (1, 'p1'), (2, 'p2');
            insert c values (10, 1, N'c1'), (11, 1, N'c2'), (12, 2, null);
            """).ExecuteNonQuery();
        return connection;
    }

    private static string Xml(System.Data.Common.DbConnection connection, string query) => (string)connection.CreateCommand(query).ExecuteScalar()!;

    [TestMethod]
    public void Raw_Xmlschema_DeclaresEachColumn_AndPutsTheRowsInItsNamespace()
    {
        using var connection = Seeded();
        AreEqual(
            Head("urn:schemas-microsoft-com:sql:SqlRowSet1") + """<xsd:element name="row"><xsd:complexType><xsd:sequence><xsd:element name="q" type="sqltypes:xml" minOccurs="0"/></xsd:sequence><xsd:attribute name="id" type="sqltypes:int" use="required"/><xsd:attribute name="s">""" + Varchar10
            + """</xsd:attribute></xsd:complexType></xsd:element></xsd:schema><row xmlns="urn:schemas-microsoft-com:sql:SqlRowSet1" id="1" s="a&lt;b"><q><q xmlns=""/></q></row>""",
            Xml(connection, "select id, s, q from x for xml raw, xmlschema"));
    }

    [TestMethod]
    public void Raw_Xmlschema_ElementsXsinil_TakesTheTargetUri_AndLeavesTheCounter()
    {
        using var connection = Seeded();
        AreEqual(
            Head("urn:my") + """<xsd:element name="item"><xsd:complexType><xsd:sequence><xsd:element name="id" type="sqltypes:int" nillable="1"/><xsd:element name="s" nillable="1">""" + Varchar10
            + """</xsd:element></xsd:sequence></xsd:complexType></xsd:element></xsd:schema><item xmlns="urn:my" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><id>1</id><s>a&lt;b</s></item>""",
            Xml(connection, "select id, s from x for xml raw('item'), elements xsinil, xmlschema('urn:my')"));
        Assert.StartsWith(Head("urn:schemas-microsoft-com:sql:SqlRowSet1"), Xml(connection, "select id from x for xml raw, xmlschema"));
        Assert.StartsWith(Head("urn:schemas-microsoft-com:sql:SqlRowSet2"), Xml(connection, "select id from x for xml raw, xmlschema"));
    }

    [TestMethod]
    public void Auto_Xmlschema_DeclaresEachLevel_TheChildByReference()
        => AreEqual(
            Head("urn:schemas-microsoft-com:sql:SqlRowSet1", " xmlns:schema=\"urn:schemas-microsoft-com:sql:SqlRowSet1\"") + """<xsd:element name="p"><xsd:complexType><xsd:sequence><xsd:element ref="schema:c" minOccurs="0" maxOccurs="unbounded"/></xsd:sequence><xsd:attribute name="pid" type="sqltypes:int" use="required"/></xsd:complexType></xsd:element><xsd:element name="c"><xsd:complexType><xsd:attribute name="cid" type="sqltypes:int" use="required"/><xsd:attribute name="cn"><xsd:simpleType><xsd:restriction base="sqltypes:nvarchar" sqltypes:localeId="1033" sqltypes:sqlCompareOptions="IgnoreCase IgnoreKanaType IgnoreWidth" sqltypes:sqlSortId="52"><xsd:maxLength value="5"/></xsd:restriction></xsd:simpleType></xsd:attribute></xsd:complexType></xsd:element></xsd:schema>"""
            + """<p xmlns="urn:schemas-microsoft-com:sql:SqlRowSet1" pid="1"><c cid="10" cn="c1"/><c cid="11" cn="c2"/></p><p xmlns="urn:schemas-microsoft-com:sql:SqlRowSet1" pid="2"><c cid="12"/></p>""",
            Xml(Seeded(), "select p.pid, c.cid, c.cn from p join c on c.pid = p.pid order by p.pid for xml auto, xmlschema"));

    [TestMethod]
    [DataRow("select id, s from x for xml raw, xmldata",
        """<Schema name="Schema1" xmlns="urn:schemas-microsoft-com:xml-data" xmlns:dt="urn:schemas-microsoft-com:datatypes"><ElementType name="row" content="empty" model="closed"><AttributeType name="id" dt:type="i4"/><AttributeType name="s" dt:type="string"/><attribute type="id"/><attribute type="s"/></ElementType></Schema><row xmlns="x-schema:#Schema1" id="1" s="a&lt;b"/>""")]
    [DataRow("select p.pid, c.cid from p join c on c.pid = p.pid order by p.pid for xml auto, elements, xmldata",
        """<Schema name="Schema1" xmlns="urn:schemas-microsoft-com:xml-data" xmlns:dt="urn:schemas-microsoft-com:datatypes"><ElementType name="p" content="eltOnly" model="closed" order="many"><element type="c" maxOccurs="*"/><element type="pid"/></ElementType><ElementType name="pid" content="textOnly" model="closed" dt:type="i4"/><ElementType name="c" content="eltOnly" model="closed" order="many"><element type="cid"/></ElementType><ElementType name="cid" content="textOnly" model="closed" dt:type="i4"/></Schema><p xmlns="x-schema:#Schema1"><pid>1</pid><c><cid>10</cid></c><c><cid>11</cid></c></p><p xmlns="x-schema:#Schema1"><pid>2</pid><c><cid>12</cid></c></p>""")]
    [DataRow("select id from x where 1 = 0 for xml raw, xmldata",
        """<Schema name="Schema1" xmlns="urn:schemas-microsoft-com:xml-data" xmlns:dt="urn:schemas-microsoft-com:datatypes"><ElementType name="row" content="empty" model="closed"><AttributeType name="id" dt:type="i4"/><attribute type="id"/></ElementType></Schema>""")]
    public void Xmldata_DeclaresAnXdrSchema(string query, string expected) => AreEqual(expected, Xml(Seeded(), query));

    private const string XdrHead = """<Schema name="Schema1" xmlns="urn:schemas-microsoft-com:xml-data" xmlns:dt="urn:schemas-microsoft-com:datatypes">""";

    /// <summary>
    /// <c>FOR XML EXPLICIT, XMLDATA</c> declares one open, mixed
    /// <c>ElementType</c> per tag with its attributes and element columns in
    /// column order — a child tag referenced by none — and an
    /// <c>ElementType</c> per element column after it; the identity directives
    /// type their attributes, and hidden, unnamed and <c>xmltext</c> columns
    /// declare nothing.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 as Tag, null as Parent, 1 as [C!1!A], N'e' as [C!1!E!element], 2 as [C!1!B], N'f' as [C!1!F!xml] for xml explicit, xmldata",
        XdrHead + """<ElementType name="C" content="mixed" model="open"><AttributeType name="A" dt:type="i4"/><AttributeType name="B" dt:type="i4"/><attribute type="A"/><element type="E"/><attribute type="B"/><element type="F"/></ElementType><ElementType name="E" content="textOnly" model="closed" dt:type="string"/><ElementType name="F" content="mixed" model="closed" dt:type="string"/></Schema><C xmlns="x-schema:#Schema1" A="1" B="2"><E>e</E><F>f</F></C>""")]
    [DataRow("select 1 as Tag, null as Parent, 1 as [C!1!Id!id], null as [O!2!No], null as [O!2!Amt] union all select 2, 1, 1, 10, 2.5 for xml explicit, xmldata",
        XdrHead + """<ElementType name="C" content="mixed" model="open"><AttributeType name="Id" dt:type="id"/><attribute type="Id"/></ElementType><ElementType name="O" content="mixed" model="open"><AttributeType name="No" dt:type="i4"/><AttributeType name="Amt" dt:type="number"/><attribute type="No"/><attribute type="Amt"/></ElementType></Schema><C xmlns="x-schema:#Schema1" Id="1"><O No="10" Amt="2.5"/></C>""")]
    [DataRow("select 1 as Tag, null as Parent, 1 as [C!1!Id!hide], N'z' as [C!1!!element], cast('2020-01-01' as date) as [C!1] for xml explicit, xmldata",
        XdrHead + """<ElementType name="C" content="mixed" model="open"></ElementType></Schema><C xmlns="x-schema:#Schema1">z2020-01-01</C>""")]
    [DataRow("select 1 as Tag, null as Parent, null as [C!1!E!elementxsinil] for xml explicit, xmldata",
        XdrHead + """<ElementType name="C" content="mixed" model="open"><element type="E"/></ElementType><ElementType name="E" content="textOnly" model="closed" dt:type="i4"/></Schema><C xmlns="x-schema:#Schema1" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><E xsi:nil="true"/></C>""")]
    [DataRow("select 1 as Tag, null as Parent, 1 as [C!1!Id] from x where 1 = 0 for xml explicit, xmldata",
        XdrHead + """<ElementType name="C" content="mixed" model="open"><AttributeType name="Id" dt:type="i4"/><attribute type="Id"/></ElementType></Schema>""")]
    public void Explicit_Xmldata_DeclaresEachTag(string query, string expected) => AreEqual(expected, Xml(Seeded(), query));

    /// <summary>
    /// Msg 6848 is raised as the statement runs, after the batch's earlier
    /// results, using up a schema number; the attribute name is what it names.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 as Tag, null as Parent, cast('2020-01-01' as date) as [C!1!D] for xml explicit, xmldata", "D")]
    [DataRow("select cast('2020-01-01' as date) d for xml raw, xmldata", "d")]
    public void Xmldata_UnmappedType_RaisesAsTheStatementRuns(string query, string column)
    {
        using var connection = Seeded();
        using (var reader = connection.CreateCommand("select 1; " + query).ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(1, reader.GetInt32(0));
            var error = Throws<SimulatedSqlException>(() => reader.NextResult() && reader.Read());
            AreEqual(6848, error.Number);
            Assert.Contains($"column '{column}'", error.Message);
        }
        Assert.StartsWith("""<Schema name="Schema2" """, Xml(connection, "select id from x for xml raw, xmldata"));
    }

    /// <summary>
    /// A string type's restriction names its collation's locale, comparison
    /// options, version, SQL sort order and supplementary characters.
    /// </summary>
    [TestMethod]
    [DataRow("Latin1_General_BIN", """sqltypes:localeId="1033" sqltypes:sqlCompareOptions="BinarySort""")]
    [DataRow("French_CI_AI", """sqltypes:localeId="1036" sqltypes:sqlCompareOptions="IgnoreCase IgnoreNonSpace IgnoreKanaType IgnoreWidth""")]
    [DataRow("Latin1_General_CI_AS_KS_WS", """sqltypes:localeId="1033" sqltypes:sqlCompareOptions="IgnoreCase""")]
    [DataRow("SQL_Latin1_General_CP1_CS_AS", """sqltypes:localeId="1033" sqltypes:sqlCompareOptions="IgnoreKanaType IgnoreWidth" sqltypes:sqlSortId="51""")]
    [DataRow("Latin1_General_100_CS_AS_KS_WS_SC", """sqltypes:localeId="1033" sqltypes:sqlCompareOptions="None" sqltypes:sqlCollationVersion="2" sqltypes:supplementaryCharacters="true""")]
    [DataRow("Latin1_General_100_BIN2_UTF8", """sqltypes:localeId="1033" sqltypes:sqlCompareOptions="BinarySort2" sqltypes:sqlCollationVersion="2""")]
    public void Xmlschema_StringRestriction_DescribesTheCollation(string collation, string attributes)
        => Assert.Contains($"""<xsd:restriction base="sqltypes:varchar" {attributes}"><xsd:maxLength value="5"/>""",
            Xml(Seeded(), $"select cast('a' as varchar(5)) collate {collation} a for xml raw, xmlschema"));

    [TestMethod]
    [DataRow("cast('10:00' as time(0))", """<xsd:restriction base="sqltypes:time"><xsd:pattern value="(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}?"/><xsd:maxInclusive value="12:59:59"/></xsd:restriction>""")]
    [DataRow("cast('10:00' as time(3))", """<xsd:restriction base="sqltypes:time"><xsd:pattern value="(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}(\.[0-9]{3})?"/><xsd:maxInclusive value="12:59:59.999"/></xsd:restriction>""")]
    [DataRow("cast(1 as numeric(5, 0))", """<xsd:restriction base="sqltypes:numeric"><xsd:totalDigits value="5"/><xsd:fractionDigits value="0"/></xsd:restriction>""")]
    [DataRow("cast(0x01 as varbinary(max))", """type="sqltypes:varbinary"/>""")]
    [DataRow("cast('a' as text)", """<xsd:restriction base="sqltypes:text" sqltypes:localeId="1033" sqltypes:sqlCompareOptions="IgnoreCase IgnoreKanaType IgnoreWidth" sqltypes:sqlSortId="52"/>""")]
    public void Xmlschema_MapsEachType(string expression, string declared)
        => Assert.Contains(declared, Xml(Seeded(), $"select {expression} c for xml raw, binary base64, xmlschema"));

    [TestMethod]
    public void Xmlschema_VariantElement_NamesItsValuesType()
        => Assert.Contains("""<row xmlns="urn:schemas-microsoft-com:sql:SqlRowSet1" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:sqltypes="http://schemas.microsoft.com/sqlserver/2004/sqltypes"><v xsi:type="sqltypes:int">1</v></row>""",
            Xml(Seeded(), "select v from x for xml raw, elements, xmlschema"));

    [TestMethod]
    public void ForXml_ZeroOffset_IsWrittenAsZ()
        => AreEqual("""<row d="2020-01-01T00:00:00Z"/>""", Xml(Seeded(), "select d from x for xml raw"));

    [TestMethod]
    [DataRow("select id from x for xml path, xmlschema", 6855)]
    [DataRow("select id from x for xml path, xmldata", 6855)]
    [DataRow("select id from x for xml raw('r'), xmldata", 6860)]
    [DataRow("select id from x for xml raw, xmldata, root", 6860)]
    [DataRow("select id from x for xml raw, xmlschema('')", 6862)]
    [DataRow("select v from x for xml raw, xmlschema", 6847)]
    [DataRow("select q from x for xml raw, xmldata", 6848)]
    [DataRow("select cast('a' as varchar(max)) a from x for xml raw, xmldata", 6848)]
    [DataRow("select id from x for xml raw, xmldata, xmlschema", 102)]
    public void InlineSchema_Refusals(string query, int number)
    {
        using var connection = Seeded();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand(query).ExecuteScalar());
        AreEqual(number, error.Number);
    }
}
