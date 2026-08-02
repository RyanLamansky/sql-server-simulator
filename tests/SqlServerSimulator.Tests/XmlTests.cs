using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the parse-and-store-but-no-search xml surface
/// (<c>xml</c> as a column type, <c>xml(schema_collection)</c> binding,
/// <c>CREATE/DROP XML SCHEMA COLLECTION</c>, <c>CREATE [PRIMARY] XML
/// INDEX</c>, plus <c>sys.xml_schema_collections</c> / <c>sys.xml_indexes</c>).
/// The XML method surface has its own homes: the path-evaluating methods are
/// exercised here alongside the DDL, and <c>.modify()</c> in
/// <see cref="XmlModifyTests"/>.
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
                and path_id is null and auto_created = 0
            """));
    }

    /// <summary>
    /// XML indexes take their <c>index_id</c> from real's dedicated 256000+
    /// range, sequenced per table in creation order: a table's first XML index
    /// is 256000, its second 256001, and the first on a second table is 256000
    /// again (all probe-confirmed against SQL Server 2025). Ordinary indexes
    /// keep their small ids.
    /// </summary>
    [TestMethod]
    public void XmlIndexIds_StartAt256000_PerTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.doc (id int not null primary key, body xml, note xml);
            create table dbo.other (id int not null primary key, body xml)
            """);
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        _ = sim.ExecuteNonQuery("create xml index sxml_doc on dbo.doc(body) using xml index pxml_doc for path");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_note on dbo.doc(note)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_other on dbo.other(body)");
        AreEqual(256000, sim.ExecuteScalar("select index_id from sys.xml_indexes where name = 'pxml_doc'"));
        AreEqual(256001, sim.ExecuteScalar("select index_id from sys.xml_indexes where name = 'sxml_doc'"));
        AreEqual(256002, sim.ExecuteScalar("select index_id from sys.xml_indexes where name = 'pxml_note'"));
        AreEqual(256000, sim.ExecuteScalar("select index_id from sys.xml_indexes where name = 'pxml_other'"));
        // The table's own PRIMARY KEY index keeps the small id range.
        AreEqual(1, sim.ExecuteScalar("select min(index_id) from sys.indexes where object_id = object_id('dbo.doc')"));
    }

    /// <summary>
    /// <c>sys.index_columns</c> keys the XML index's row on the same 256000+
    /// id, and the primary's internal node table is named after it
    /// (<c>xml_index_nodes_&lt;table object_id&gt;_&lt;index_id&gt;</c>,
    /// probe-confirmed).
    /// </summary>
    [TestMethod]
    public void XmlIndexId_ReachesIndexColumnsAndTheNodeTableName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.index_columns where object_id = object_id('dbo.doc') and index_id = 256000"));
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.objects
            where type = 'IT' and name = 'xml_index_nodes_' + cast(object_id('dbo.doc') as varchar(20)) + '_256000'
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

    /// <summary>
    /// Each primary XML index owns an internal node table (sys.objects type
    /// 'IT') whose object_id carries one sys.stats row per XML index (named per
    /// index); every XML index also gets a sys.index_columns row. DacFx's
    /// XML-index export INNER JOINs all three, so their absence orphans the
    /// index elements (client-side NRE).
    /// </summary>
    [TestMethod]
    public void XmlIndex_InternalNodeTable_And_Stats_And_IndexColumns_Surface()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int not null primary key, body xml)");
        _ = sim.ExecuteNonQuery("create primary xml index pxml_doc on dbo.doc(body)");
        _ = sim.ExecuteNonQuery("create xml index sxml_path on dbo.doc(body) using xml index pxml_doc for path");

        // One INTERNAL_TABLE per primary, parented to the base table, ms-shipped.
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.objects
            where type = 'IT' and type_desc = 'INTERNAL_TABLE'
              and parent_object_id = object_id('dbo.doc') and is_ms_shipped = 1
            """));
        // One stats row per XML index on the node table, named per index.
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from sys.stats s
            join sys.objects o on s.object_id = o.object_id
            where o.type = 'IT' and o.parent_object_id = object_id('dbo.doc')
              and s.name in ('pxml_doc', 'sxml_path')
            """));
        // One index_columns row per XML index, on the base table's object_id,
        // targeting the xml column (column_id 2), key_ordinal 0.
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from sys.index_columns ic
            join sys.xml_indexes xi on ic.object_id = xi.object_id and ic.index_id = xi.index_id
            where ic.object_id = object_id('dbo.doc') and ic.column_id = 2 and ic.key_ordinal = 0
            """));
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
    public void XmlModify_InSelectList_Raises8137()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body xml)");
        _ = sim.ExecuteNonQuery("insert into dbo.doc values (1, N'<r/>')");
        sim.AssertSqlError(
            "select body.modify('insert <c/> into (/r)[1]') from dbo.doc",
            8137,
            "Incorrect use of the XML data type method 'modify'. A non-mutator method is expected in this context.");
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

    /// <summary>
    /// A leading byte-order mark is dropped whenever a string becomes
    /// <c>xml</c> — probe-confirmed against SQL Server 2025 (2026-07-30) for a
    /// literal INSERT, a parameter, an explicit CAST and <c>SqlBulkCopy</c>
    /// alike. An <c>nvarchar</c> column keeps the mark, so the rule belongs to
    /// the type conversion rather than to the input path.
    /// </summary>
    [TestMethod]
    public void LeadingBom_IsStrippedByXmlConversion()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, x xml, nv nvarchar(max))");
        _ = sim.ExecuteNonQuery("insert t values (1, N'\uFEFF<a/>', N'\uFEFF<a/>')");
        _ = sim.ExecuteNonQuery("insert t values (2, cast(N'\uFEFF<a/>' as xml), N'-')");
        AreEqual("<a/>", sim.ExecuteScalar("select cast(x as nvarchar(max)) from t where id = 1"));
        AreEqual("<a/>", sim.ExecuteScalar("select cast(x as nvarchar(max)) from t where id = 2"));
        AreEqual("\uFEFF<a/>", sim.ExecuteScalar("select nv from t where id = 1"));
    }

    /// <inheritdoc cref="LeadingBom_IsStrippedByXmlConversion"/>
    [TestMethod]
    public void LeadingBom_IsStrippedFromAParameter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (x xml)");
        using var connection = sim.CreateOpenConnection();
        using (var insert = connection.CreateCommand("insert t values (@x)"))
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = "@x";
            parameter.Value = "\uFEFF<a/>";
            _ = insert.Parameters.Add(parameter);
            _ = insert.ExecuteNonQuery();
        }
        AreEqual("<a/>", connection.CreateCommand("select cast(x as nvarchar(max)) from t").ExecuteScalar());
    }

    /// <summary>A mark that isn't leading is content, not an encoding marker, and survives.</summary>
    [TestMethod]
    public void NonLeadingBom_Survives()
        => AreEqual("<a>x\uFEFFy</a>", new Simulation().ExecuteScalar(
            "create table t (x xml); insert t values (N'<a>x\uFEFFy</a>'); select cast(x as nvarchar(max)) from t"));
}
