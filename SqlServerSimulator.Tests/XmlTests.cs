using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the parse-and-store-but-no-search xml surface
/// (<c>xml</c> as a column type, <c>xml(schema_collection)</c> binding,
/// <c>CREATE/DROP XML SCHEMA COLLECTION</c>, <c>CREATE [PRIMARY] XML
/// INDEX</c>, plus <c>sys.xml_schema_collections</c> / <c>sys.xml_indexes</c>).
/// XPath / XQuery methods (<c>.value()</c> / <c>.nodes()</c> / <c>.query()</c>
/// / <c>.exist()</c> / <c>.modify()</c>) raise <see cref="NotSupportedException"/>
/// at execute time.
/// </summary>
[TestClass]
public sealed class XmlTests
{
    [TestMethod]
    public void XmlColumn_AcceptsAndRoundTrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<root><child>hi</child></root>')");
        var read = sim.ExecuteScalar("select body from dbo.doc where id = 1");
        AreEqual("<root><child>hi</child></root>", read);
    }

    [TestMethod]
    public void XmlColumn_NullStoresAsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml null)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, null)");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void SysColumns_ReportsXmlTypeIdentity()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        AreEqual("xml", sim.ExecuteScalar(@"
            select t.name from sys.columns c
            join sys.types t on t.user_type_id = c.user_type_id
            where c.object_id = object_id('dbo.doc') and c.name = 'body'"));
    }

    [TestMethod]
    public void CreateXmlSchemaCollection_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"create xml schema collection xsc1 as N'<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""root"" type=""xsd:string"" /></xsd:schema>'");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.xml_schema_collections where name = 'xsc1'"));
    }

    [TestMethod]
    public void CreateXmlSchemaCollection_QualifiedSchema_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create schema audit");
        _ = sim.ExecuteNonQuery("create xml schema collection audit.xsc1 as N'<xsd:schema/>'");
        var schemaId = sim.ExecuteScalar("select schema_id from sys.xml_schema_collections where name = 'xsc1'");
        var auditId = sim.ExecuteScalar("select schema_id from sys.schemas where name = 'audit'");
        AreEqual(auditId, schemaId);
    }

    [TestMethod]
    public void CreateXmlSchemaCollection_DuplicateName_Raises219()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        _ = sim.AssertSqlError("create xml schema collection xsc1 as N'<xsd:schema/>'", 219);
    }

    [TestMethod]
    public void XmlColumn_WithSchemaCollection_Binds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml(xsc1))");
        // The binding round-trips through INSERT/SELECT — payload still stores
        // as raw text since no XSD validation runs.
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<hi/>')");
        AreEqual("<hi/>", sim.ExecuteScalar("select body from dbo.doc"));
    }

    [TestMethod]
    public void XmlColumn_WithContentDiscriminator_Parses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml(content xsc1))");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables where name = 'doc'"));
    }

    [TestMethod]
    public void XmlColumn_WithDocumentDiscriminator_Parses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml(document xsc1))");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables where name = 'doc'"));
    }

    [TestMethod]
    public void XmlColumn_WithUnknownCollection_Raises208()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("create table dbo.doc (id int, body xml(no_such_collection))", 208);
    }

    [TestMethod]
    public void DropXmlSchemaCollection_Removes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        _ = sim.ExecuteNonQuery("drop xml schema collection xsc1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.xml_schema_collections"));
    }

    [TestMethod]
    public void CreatePrimaryXmlIndex_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.xml_indexes"));
        AreEqual("XML", sim.ExecuteScalar("select type_desc from sys.xml_indexes where name = 'pxml_doc'"));
    }

    [TestMethod]
    public void CreateSecondaryXmlIndex_PathValueProperty_Stores()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        _ = sim.ExecuteNonQuery("create xml index sxml_path on dbo.doc(body) using xml index pxml_doc for path");
        _ = sim.ExecuteNonQuery("create xml index sxml_value on dbo.doc(body) using xml index pxml_doc for value");
        _ = sim.ExecuteNonQuery("create xml index sxml_property on dbo.doc(body) using xml index pxml_doc for property");
        AreEqual(4, sim.ExecuteScalar("select count(*) from sys.xml_indexes"));
        AreEqual("PATH", sim.ExecuteScalar("select secondary_type_desc from sys.xml_indexes where name = 'sxml_path'"));
        AreEqual("VALUE", sim.ExecuteScalar("select secondary_type_desc from sys.xml_indexes where name = 'sxml_value'"));
        AreEqual("PROPERTY", sim.ExecuteScalar("select secondary_type_desc from sys.xml_indexes where name = 'sxml_property'"));
    }

    [TestMethod]
    public void SecondaryXmlIndex_UsingId_LinksToPrimary()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        _ = sim.ExecuteNonQuery("create xml index sxml_path on dbo.doc(body) using xml index pxml_doc for path");
        var primaryId = sim.ExecuteScalar("select index_id from sys.xml_indexes where name = 'pxml_doc'");
        var secondaryUsingId = sim.ExecuteScalar("select using_xml_index_id from sys.xml_indexes where name = 'sxml_path'");
        AreEqual(primaryId, secondaryUsingId);
    }

    [TestMethod]
    public void CreateXmlIndex_DuplicateName_Raises2714()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml on dbo.doc(body)");
        _ = sim.AssertSqlError("create primary xml index pxml on dbo.doc(body)", 2714);
    }

    [TestMethod]
    public void XmlValue_Method_RaisesNotSupportedAtExecute()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r><c>hi</c></r>')");
        var ex = ThrowsExactly<NotSupportedException>(() =>
            sim.ExecuteScalar("select body.value('(/r/c)[1]', 'nvarchar(50)') from dbo.doc"));
        Contains(".value()", ex.Message);
    }

    [TestMethod]
    public void XmlQuery_Method_RaisesNotSupportedAtExecute()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r/>')");
        var ex = ThrowsExactly<NotSupportedException>(() =>
            sim.ExecuteScalar("select body.query('/r') from dbo.doc"));
        Contains(".query()", ex.Message);
    }

    [TestMethod]
    public void XmlExist_Method_RaisesNotSupportedAtExecute()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r/>')");
        var ex = ThrowsExactly<NotSupportedException>(() =>
            sim.ExecuteScalar("select body.exist('/r') from dbo.doc"));
        Contains(".exist()", ex.Message);
    }

    [TestMethod]
    public void CreateViewWithXmlMethod_Succeeds_FailsAtExecute()
    {
        // CREATE VIEW body parses cleanly (proc/view bodies parse for name
        // resolution but XML methods defer their NotSupportedException to
        // run-time). The query against the view fails on first row.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r/>')");
        _ = sim.ExecuteNonQuery("create view dbo.v_doc as select body.value('(/r)[1]', 'nvarchar(50)') as v from dbo.doc");
        _ = ThrowsExactly<NotSupportedException>(() =>
            sim.ExecuteScalar("select v from dbo.v_doc"));
    }

    [TestMethod]
    public void XmlSchemaCollections_StartsAt65536()
    {
        // Probe-confirmed: SQL Server's first user xml_collection_id is 65536.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        AreEqual(65536, sim.ExecuteScalar("select xml_collection_id from sys.xml_schema_collections where name = 'xsc1'"));
    }

    [TestMethod]
    public void CastXmlToNvarchar_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r/>')");
        AreEqual("<r/>", sim.ExecuteScalar("select cast(body as nvarchar(max)) from dbo.doc"));
    }
}
