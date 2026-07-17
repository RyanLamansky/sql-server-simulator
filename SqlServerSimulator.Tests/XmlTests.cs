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
    public void TypedXmlColumn_SysColumnsXmlCollectionId_JoinsBackToCollection()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create xml schema collection xsc1 as N'<xsd:schema/>'");
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml(xsc1))");
        // The typed column's sys.columns.xml_collection_id resolves to the
        // collection through sys.xml_schema_collections (the join DacFx's
        // reverse-engineering query relies on).
        AreEqual("xsc1", sim.ExecuteScalar("""
            select x.name from sys.columns c
              join sys.xml_schema_collections x on c.xml_collection_id = x.xml_collection_id
             where c.object_id = object_id('dbo.doc') and c.name = 'body'
            """));
        // The untyped id column reports the non-nullable 0 default.
        AreEqual(0, sim.ExecuteScalar("select xml_collection_id from sys.columns where object_id = object_id('dbo.doc') and name = 'id'"));
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

    /// <summary>
    /// sys.xml_indexes carries the full 26-column shape DacFx's XML-index
    /// reverse-engineering query reads. Primary indexes report xml_index_type 0
    /// / 'PRIMARY_XML'; secondary indexes report 1 / 'SECONDARY_XML'. The shared
    /// index-admin tail mirrors the fresh-index modeled defaults (is_hypothetical
    /// false so DacFx's `is_hypothetical = 0` filter matches, allow_row_locks /
    /// allow_page_locks true, fill_factor 0, has_filter false, path_id 0).
    /// </summary>
    [TestMethod]
    public void SysXmlIndexes_WidenedColumns_PrimaryAndSecondary()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        _ = sim.ExecuteNonQuery("create xml index sxml_value on dbo.doc(body) using xml index pxml_doc for value");

        AreEqual((byte)0, sim.ExecuteScalar("select xml_index_type from sys.xml_indexes where name = 'pxml_doc'"));
        AreEqual("PRIMARY_XML", sim.ExecuteScalar("select xml_index_type_description from sys.xml_indexes where name = 'pxml_doc'"));
        AreEqual((byte)1, sim.ExecuteScalar("select xml_index_type from sys.xml_indexes where name = 'sxml_value'"));
        AreEqual("SECONDARY_XML", sim.ExecuteScalar("select xml_index_type_description from sys.xml_indexes where name = 'sxml_value'"));

        // The whole appended column tail resolves in one projection with the
        // fresh-index default values (a missing column would fail Msg 207).
        AreEqual(2, sim.ExecuteScalar<int>("""
            select count(*) from sys.xml_indexes where is_hypothetical = 0
                and allow_row_locks = 1 and allow_page_locks = 1
                and fill_factor = 0 and is_padded = 0 and is_disabled = 0
                and is_unique = 0 and is_unique_constraint = 0 and ignore_dup_key = 0
                and is_ignored_in_optimization = 0 and has_filter = 0
                and filter_definition is null and data_space_id = 1
                and path_id = 0 and auto_created = 0
            """));
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
    public void XmlValue_Method_ExtractsScalar()
        => AreEqual("hi", new Simulation().ExecuteScalar("""
            create table dbo.doc (id int, body xml);
            insert into dbo.doc values (1, N'<r><c>hi</c></r>');
            select body.value('(/r/c)[1]', 'nvarchar(50)') from dbo.doc
            """));

    [TestMethod]
    public void XmlQuery_Method_SerializesMatchedNodes()
        => AreEqual("<c>a</c><c>b</c>", new Simulation().ExecuteScalar("""
            create table dbo.doc (id int, body xml);
            insert into dbo.doc values (1, N'<r><c>a</c><c>b</c></r>');
            select cast(body.query('/r/c') as nvarchar(max)) from dbo.doc
            """));

    [TestMethod]
    public void XmlExist_Method_ReturnsBit()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r><c>x</c></r>')");
        IsTrue((bool)sim.ExecuteScalar("select body.exist('/r/c') from dbo.doc")!);
        IsFalse((bool)sim.ExecuteScalar("select body.exist('/r/missing') from dbo.doc")!);
    }

    [TestMethod]
    public void XmlExist_NullInstance_ReturnsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, NULL)");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select body.exist('/r') from dbo.doc"));
    }

    [TestMethod]
    public void XmlModify_Method_RaisesNotSupportedAtExecute()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r/>')");
        var ex = ThrowsExactly<NotSupportedException>(() =>
            sim.ExecuteScalar("select body.modify('insert <c/> into (/r)[1]') from dbo.doc"));
        Contains(".modify()", ex.Message);
    }

    [TestMethod]
    public void CreateViewWithXmlValue_ProjectsExtractedScalar()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r><c>hi</c></r>')");
        _ = sim.ExecuteNonQuery("create view dbo.v_doc as select body.value('(/r/c)[1]', 'nvarchar(50)') as v from dbo.doc");
        AreEqual("hi", sim.ExecuteScalar("select v from dbo.v_doc"));
    }

    [TestMethod]
    public void XmlNodes_CrossApply_ShredsRows()
    {
        // .nodes() as a CROSS APPLY rowset source, with relative .value()
        // against each shredded node — the AdventureWorks vJobCandidate* shape.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r><c>a</c><c>b</c><c>c</c></r>')");
        AreEqual(3, sim.ExecuteScalar("""
            select count(*) from dbo.doc
            cross apply body.nodes('/r/c') as n(ref)
            """));
        AreEqual("a|b|c", sim.ExecuteScalar("""
            select string_agg(n.ref.value('(.)[1]', 'nvarchar(10)'), '|') from dbo.doc
            cross apply body.nodes('/r/c') as n(ref)
            """));
    }

    [TestMethod]
    public void XmlNodes_OuterApply_NullXmlYieldsNoRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, NULL)");
        // OUTER APPLY null-fills the right side when the lateral plan is empty,
        // so the single left row survives with a NULL shredded value.
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from dbo.doc
            outer apply body.nodes('/r/c') as n(ref)
            """));
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

    [TestMethod]
    public void XmlSchemaNamespace_ReturnsCollectionXsdAsXml()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"create xml schema collection xsc1 as N'<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""/>'");
        AreEqual(
            @"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""/>",
            sim.ExecuteScalar("select cast(XML_SCHEMA_NAMESPACE(N'dbo', N'xsc1') as nvarchar(max))"));
    }

    [TestMethod]
    public void XmlSchemaNamespace_DacFxCollectionScriptingQuery_Runs()
    {
        // The DacFx bacpac-export query shape over sys.xml_schema_collections;
        // with no user collections the function must still parse and type.
        var sim = new Simulation();
        AreEqual(DBNull.Value, sim.ExecuteScalar(
            "SELECT * FROM (SELECT XML_SCHEMA_NAMESPACE(SCHEMA_NAME([xsc].[schema_id]), [xsc].[name]) AS [Document] " +
            "FROM [sys].[xml_schema_collections] [xsc] WITH (NOLOCK) WHERE xsc.name <> N'sys') AS [_results]") ?? DBNull.Value);
    }

    [TestMethod]
    public void XmlSchemaNamespace_UnknownCollection_Raises6314()
    {
        // Probe-confirmed: real raises 6314 even for the built-in sys collection.
        var sim = new Simulation();
        _ = sim.AssertSqlError("select XML_SCHEMA_NAMESPACE(N'dbo', N'nope')", 6314);
        _ = sim.AssertSqlError("select XML_SCHEMA_NAMESPACE(N'sys', N'sys')", 6314);
    }

    [TestMethod]
    public void XmlSchemaNamespace_NullArgument_Raises8116()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("select XML_SCHEMA_NAMESPACE(NULL, N'x')", 8116);
    }
}
