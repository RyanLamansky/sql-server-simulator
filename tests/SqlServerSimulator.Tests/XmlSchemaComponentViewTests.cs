using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The XML schema component catalog views — <c>sys.xml_schema_namespaces</c>,
/// <c>_components</c> and its per-kind views, <c>_facets</c>,
/// <c>_wildcard_namespaces</c> and <c>_component_placements</c> — each
/// expected value read from SQL Server 2025 over the same statements
/// (2026-10-16).
/// </summary>
[TestClass]
public sealed class XmlSchemaComponentViewTests
{
    private const string Collection = """
        create xml schema collection dbo.c1 as N'<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:a" xmlns:a="urn:a" elementFormDefault="qualified">
          <xsd:simpleType name="st1"><xsd:restriction base="xsd:string"><xsd:maxLength value="10"/><xsd:enumeration value="x"/></xsd:restriction></xsd:simpleType>
          <xsd:simpleType name="lst"><xsd:list itemType="xsd:int"/></xsd:simpleType>
          <xsd:simpleType name="un"><xsd:union memberTypes="xsd:int xsd:date"/></xsd:simpleType>
          <xsd:complexType name="ct1">
            <xsd:sequence>
              <xsd:element name="e1" type="xsd:int" minOccurs="0"/>
              <xsd:element name="e2" type="a:st1" maxOccurs="unbounded" default="x"/>
              <xsd:choice><xsd:element name="c1" type="xsd:string"/><xsd:element name="c2" type="xsd:string"/></xsd:choice>
              <xsd:any namespace="urn:c urn:b" processContents="lax"/>
            </xsd:sequence>
            <xsd:attribute name="at1" type="xsd:int" use="required"/>
            <xsd:attribute name="at2" type="xsd:string" fixed="f"/>
            <xsd:anyAttribute namespace="##targetNamespace"/>
          </xsd:complexType>
          <xsd:complexType name="ct2"><xsd:complexContent><xsd:extension base="a:ct1"><xsd:sequence><xsd:element name="z" type="xsd:int"/></xsd:sequence></xsd:extension></xsd:complexContent></xsd:complexType>
          <xsd:element name="root" type="a:ct1"/>
          <xsd:element name="head" type="xsd:string" abstract="true"/>
          <xsd:element name="mem" substitutionGroup="a:head" nillable="true"/>
          <xsd:attribute name="gat" type="xsd:boolean" default="true"/>
          <xsd:group name="grp"><xsd:choice><xsd:element name="g1" type="xsd:int"/><xsd:element name="g2" type="xsd:int"/></xsd:choice></xsd:group>
          <xsd:attributeGroup name="agrp"><xsd:attribute name="ag1" type="xsd:int"/></xsd:attributeGroup>
          <xsd:element name="anon"><xsd:complexType><xsd:sequence><xsd:group ref="a:grp" minOccurs="0"/></xsd:sequence><xsd:attributeGroup ref="a:agrp"/></xsd:complexType></xsd:element>
        </xsd:schema>';

        """;

    [TestMethod]
    public void TheSysCollection_IsListedInEveryDatabase()
    {
        var sim = new Simulation();
        AreEqual("1,4,-1,sys,2009-04-13 12:59:13.390", sim.ExecuteScalar(
            "select concat(xml_collection_id, ',', schema_id, ',', isnull(principal_id, -1), ',', name, ',', convert(varchar(30), create_date, 121)) from sys.xml_schema_collections"));
        AreEqual("2009-04-13 12:59:08.860", sim.ExecuteScalar("select convert(varchar(30), create_date, 121) from master.sys.xml_schema_collections"));
    }

    [TestMethod]
    public void TheSysCollection_HoldsTheBuiltInComponents()
        => AreEqual("100,82,16,112,19,3,0,0", new Simulation().ExecuteScalar("""
            select concat((select count(*) from sys.xml_schema_components), ',', (select count(*) from sys.xml_schema_types), ',',
                (select count(*) from sys.xml_schema_attributes), ',', (select count(*) from sys.xml_schema_facets), ',',
                (select count(*) from sys.xml_schema_component_placements), ',', (select count(*) from sys.xml_schema_namespaces), ',',
                (select count(*) from sys.xml_schema_elements), ',', (select count(*) from sys.xml_schema_wildcard_namespaces))
            """));

    [TestMethod]
    [DataRow(113, "113:1:int:SIMPLE_TYPE:RESTRICTION:112", "IN=-2147483648 IX=2147483647")]
    [DataRow(329, "329:2:xml:COMPLEX_TYPE:RESTRICTION:6", "")]
    [DataRow(381, "381:2::ELEMENT_WILDCARD:NONE:", "")]
    [DataRow(326, "326:2:money:SIMPLE_TYPE:RESTRICTION:19", "DT=19 DF=4 IX=922337203685477.5807 IN=-922337203685477.5808")]
    public void TheSysCollection_Component_MatchesReal(int id, string component, string facets)
    {
        var sim = new Simulation();
        AreEqual(component, sim.ExecuteScalar($"""
            select concat(xml_component_id, ':', xml_namespace_id, ':', name collate database_default, ':', kind_desc, ':', derivation_desc, ':', base_xml_component_id)
              from sys.xml_schema_components where xml_component_id = {id}
            """));
        AreEqual(facets, sim.ExecuteScalar($"""
            select isnull(string_agg(concat(kind collate database_default, '=', value collate database_default), ' ') within group (order by facet_id), '')
              from sys.xml_schema_facets where xml_component_id = {id}
            """));
    }

    [TestMethod]
    public void TheSysCollection_AttributesCarryTheirDefaults()
        => AreEqual("sqlDbType=NVarChar maxLength=1 localeId=-1 sqlCompareOptions=Default precision=18 supplementaryCharacters=false",
            new Simulation().ExecuteScalar("""
                select string_agg(concat(name collate database_default, '=', default_value collate database_default), ' ') within group (order by xml_component_id)
                  from sys.xml_schema_attributes where default_value is not null and default_value not in (N'0')
                """));

    [TestMethod]
    public void Components_AreNumberedDepthFirstInDocumentOrder()
        => AreEqual(
            "65536:1:1:st1:TSR:15: 65537:1:1:lst:TLN:: 65538:1:1:un:TUN:: 65539:1:1:ct1:TKR:6: 65540:1:0:-:MMN::65539 65541:1:1:e1:EEN::65540 "
                + "65542:1:1:e2:EEN::65540 65543:1:0:-:MMN::65540 65544:1:1:c1:EEN::65543 65545:1:1:c2:EEN::65543 65546:1:0:-:NWN::65540 "
                + "65547:1:0:at1:AAN::65539 65548:1:0:at2:AAN::65539 65549:1:0:-:NVN::65539 65550:1:1:ct2:TKX:65539: 65551:1:0:-:MMN::65550 "
                + "65552:1:1:z:EEN::65551 65553:1:1:root:EEN:: 65554:1:1:head:EEN:: 65555:1:1:mem:EES:65554: 65556:1:1:gat:AAN:: "
                + "65557:1:1:grp:MMN:: 65558:1:1:g1:EEN::65557 65559:1:1:g2:EEN::65557 65560:1:1:agrp:GGN:: 65561:1:0:ag1:AAN::65560 "
                + "65562:1:1:anon:EEN:: 65563:1:0:-:TKR:6:65562 65564:1:0:-:MMN::65563",
            new Simulation().ExecuteScalar(Collection + """
                select string_agg(concat(xml_component_id, ':', xml_namespace_id, ':', is_qualified, ':', isnull(name, '-'), ':', symbol_space, kind, derivation, ':',
                    base_xml_component_id, ':', scoping_xml_component_id), ' ') within group (order by xml_component_id)
                  from sys.xml_schema_components where xml_collection_id > 1
                """));

    [TestMethod]
    public void Placements_CarryOccurrencesAndAttributeUses()
        => AreEqual(
            "65537.1>113:0:1:1: 65538.1>113:0:1:1: 65538.2>23:0:1:1: 65539.1>65540:0:1:1: 65539.2>65547:0:1:1: 65539.3>65548:1:0:1: "
                + "65539.4>65549:0:1:1: 65540.1>65541:0:0:1: 65540.2>65542:0:1:2147483647: 65540.3>65543:0:1:1: 65540.4>65546:0:1:1: "
                + "65541.1>113:0:1:1: 65542.1>65536:0:1:1: 65543.1>65544:0:1:1: 65543.2>65545:0:1:1: 65544.1>15:0:1:1: 65545.1>15:0:1:1: "
                + "65547.1>113:0:1:1: 65548.1>15:0:1:1: 65550.1>65551:0:1:1: 65551.1>65552:0:1:1: 65552.1>113:0:1:1: 65553.1>65539:0:1:1: "
                + "65554.1>15:0:1:1: 65555.1>15:0:1:1: 65556.1>16:0:1:1: 65557.1>65558:0:1:1: 65557.2>65559:0:1:1: 65558.1>113:0:1:1: "
                + "65559.1>113:0:1:1: 65560.1>65561:0:0:1: 65561.1>113:0:1:1: 65562.1>65563:0:1:1: 65563.1>65564:0:1:1: 65563.2>65560:0:1:1: "
                + "65564.1>65557:0:0:1:",
            new Simulation().ExecuteScalar(Collection + """
                select string_agg(concat(xml_component_id, '.', placement_id, '>', placed_xml_component_id, ':', is_default_fixed, ':', min_occurences, ':',
                    max_occurences, ':', default_value), ' ') within group (order by xml_component_id, placement_id)
                  from sys.xml_schema_component_placements where xml_component_id >= 65536
                """));

    [TestMethod]
    public void ElementsAndAttributes_CarryQualificationAndValueConstraints()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Collection);
        AreEqual("e1:11000: e2:11000:x c1:11000: c2:11000: z:11000: root:11000: head:11010: mem:11001: g1:11000: g2:11000: anon:11000:", sim.ExecuteScalar("""
            select string_agg(concat(name, ':', is_qualified, must_be_qualified, is_default_fixed, is_abstract, is_nillable, ':', default_value), ' ') within group (order by xml_component_id)
              from sys.xml_schema_elements where xml_collection_id > 1
            """));
        AreEqual("at1:000: at2:001:f gat:110:true ag1:000:", sim.ExecuteScalar("""
            select string_agg(concat(name, ':', is_qualified, must_be_qualified, is_default_fixed, ':', default_value), ' ') within group (order by xml_component_id)
              from sys.xml_schema_attributes where xml_collection_id > 1
            """));
    }

    [TestMethod]
    public void ModelGroupsAndWildcards_DescribeTheirCompositorsAndNamespaces()
        => AreEqual(
            "65540SXSD_SEQUENCE_GROUP 65543CXSD_CHOICE_GROUP 65551SXSD_SEQUENCE_GROUP 65557CXSD_CHOICE_GROUP 65564SXSD_SEQUENCE_GROUP | "
                + "65546ELEMENT_WILDCARD:LAX_VALIDATION:0 65549ATTRIBUTE_WILDCARD:STRICT_VALIDATION:0 | 65546=urn:b 65546=urn:c 65549=urn:a",
            new Simulation().ExecuteScalar(Collection + """
                select concat(
                    (select string_agg(concat(xml_component_id, compositor collate database_default, compositor_desc), ' ') within group (order by xml_component_id)
                       from sys.xml_schema_model_groups where xml_collection_id > 1), ' | ',
                    (select string_agg(concat(xml_component_id, kind_desc, ':', process_content_desc, ':', disallow_namespaces), ' ') within group (order by xml_component_id)
                       from sys.xml_schema_wildcards where xml_collection_id > 1), ' | ',
                    (select string_agg(concat(xml_component_id, '=', namespace), ' ') within group (order by xml_component_id, namespace)
                       from sys.xml_schema_wildcard_namespaces where xml_component_id >= 65536))
                """));

    [TestMethod]
    public void Wildcards_OtherAnyAndLocal_ListTheirNamespaces()
        => AreEqual("65536:1: 65538:1:urn:t 65540:0: 65540:0:urn:t", new Simulation().ExecuteScalar("""
            create xml schema collection dbo.w as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t">
              <xs:complexType name="any"><xs:anyAttribute/></xs:complexType>
              <xs:complexType name="other"><xs:anyAttribute namespace="##other"/></xs:complexType>
              <xs:complexType name="list"><xs:anyAttribute namespace="##local ##targetNamespace"/></xs:complexType>
            </xs:schema>';
            select string_agg(concat(w.xml_component_id - 1, ':', w.disallow_namespaces, ':', n.namespace collate database_default), ' ')
                within group (order by w.xml_component_id, n.namespace)
              from sys.xml_schema_wildcards w left join sys.xml_schema_wildcard_namespaces n on n.xml_component_id = w.xml_component_id
             where w.xml_collection_id > 1
            """));

    [TestMethod]
    public void AReference_NumbersAGlobalNotYetReached_WhereItIsReached()
        => AreEqual("1:h1:ES:2 2:hd:EN: 3:hdty:KR:-65529 4:ty:KX:3 5:ag:GN: 6:ag3:GN: 7:y:AN: 8:x:AN: 9:item:SR:10 10:item0:SR:-65422", new Simulation().ExecuteScalar("""
            create xml schema collection dbo.f as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:o" targetNamespace="urn:o">
              <xs:element name="h1" type="t:ty" substitutionGroup="t:hd"/>
              <xs:complexType name="ty"><xs:complexContent><xs:extension base="t:hdty"><xs:attributeGroup ref="t:ag"/></xs:extension></xs:complexContent></xs:complexType>
              <xs:attributeGroup name="ag"><xs:attributeGroup ref="t:ag3"/><xs:attribute name="x" type="t:item"/></xs:attributeGroup>
              <xs:attributeGroup name="ag3"><xs:attribute name="y" type="xs:int"/></xs:attributeGroup>
              <xs:simpleType name="item"><xs:restriction base="t:item0"><xs:maxInclusive value="10"/></xs:restriction></xs:simpleType>
              <xs:simpleType name="item0"><xs:restriction base="xs:int"/></xs:simpleType>
              <xs:complexType name="hdty"/>
              <xs:element name="hd" type="t:hdty"/>
            </xs:schema>';
            select string_agg(concat(xml_component_id - 65535, ':', name, ':', kind, derivation, ':', base_xml_component_id - 65535), ' ') within group (order by xml_component_id)
              from sys.xml_schema_components where xml_collection_id > 1
            """));

    [TestMethod]
    public void AlterAdd_DrawsFromTheDatabaseCounter_WhichADropNeverRewinds()
        => AreEqual("a:r:1:65536 a:z:2:65538 a:r2:1:65539 c:b:1:65540 | a:1= a:2=urn:z c:1=urn:b", new Simulation().ExecuteScalar("""
            create xml schema collection dbo.a as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="r" type="xs:int"/></xs:schema>';
            create xml schema collection dbo.b as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:b"><xs:element name="b" type="xs:int"/></xs:schema>';
            alter xml schema collection dbo.a add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:z"><xs:element name="z" type="xs:int"/></xs:schema>';
            alter xml schema collection dbo.a add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="r2" type="xs:int"/></xs:schema>';
            drop xml schema collection dbo.b;
            create xml schema collection dbo.c as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:b"><xs:element name="b" type="xs:int"/></xs:schema>';
            select concat(
                (select string_agg(concat(c.name, ':', x.name collate database_default, ':', x.xml_namespace_id, ':', x.xml_component_id), ' ') within group (order by x.xml_component_id)
                   from sys.xml_schema_components x join sys.xml_schema_collections c on c.xml_collection_id = x.xml_collection_id where x.xml_collection_id > 1), ' | ',
                (select string_agg(concat(c.name, ':', n.xml_namespace_id, '=', n.name collate database_default), ' ') within group (order by c.name, n.xml_namespace_id)
                   from sys.xml_schema_namespaces n join sys.xml_schema_collections c on c.xml_collection_id = n.xml_collection_id where n.xml_collection_id > 1))
            """));

    [TestMethod]
    public void IdsSpent_ByRollbacksAndRefusals_StaySpent()
        => AreEqual("k:3:3 m:4:5", new Simulation().ExecuteScalar("""
            begin tran;
            create xml schema collection dbo.rb as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="xs:int"/><xs:element name="b" type="xs:int"/></xs:schema>';
            rollback;
            begin try create xml schema collection dbo.bad as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="nosuch"/></xs:schema>'; end try begin catch end catch;
            create xml schema collection dbo.k as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="k" type="xs:int"/></xs:schema>';
            begin try create xml schema collection dbo.k as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>'; end try begin catch end catch;
            begin try
                alter xml schema collection dbo.k add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="k2" type="xs:int"/><xs:element name="k" type="xs:int"/></xs:schema>';
            end try begin catch end catch;
            create xml schema collection dbo.m as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="m" type="xs:int"/></xs:schema>';
            select string_agg(concat(c.name, ':', c.xml_collection_id - 65535, ':', x.xml_component_id - 65535), ' ') within group (order by x.xml_component_id)
              from sys.xml_schema_components x join sys.xml_schema_collections c on c.xml_collection_id = x.xml_collection_id where x.xml_collection_id > 1
            """));

    [TestMethod]
    public void Facets_ReadAsTheirTypesWhitespaceLeavesThem()
        => AreEqual(
            "s1.1:EU:ENUMERATION:0:[ a  b ] s1.2:LG:LENGTH:0:[3] s2.1:EU:ENUMERATION:0:[a b] s2.2:PT:PATTERN:0:[ x ] s3.1:WS:WHITESPACE_NORMALIZATION:0:[collapse] "
                + "s3.2:EU:ENUMERATION:0:[a b] d.1:DT:TOTAL_DIGITS:1:[5] d.2:DF:FRACTION_DIGITS:0:[2] d.3:EN:MINIMUM_EXCLUSIVE_VALUE:0:[01.50] d.4:IX:MAXIMUM_INCLUSIVE_VALUE:0:[100]",
            new Simulation().ExecuteScalar("""
                create xml schema collection dbo.fc as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                  <xs:simpleType name="s1"><xs:restriction base="xs:string"><xs:enumeration value=" a  b "/><xs:length value=" 3 "/></xs:restriction></xs:simpleType>
                  <xs:simpleType name="s2"><xs:restriction base="xs:token"><xs:enumeration value=" a  b "/><xs:pattern value=" x "/></xs:restriction></xs:simpleType>
                  <xs:simpleType name="s3"><xs:restriction base="xs:string"><xs:whiteSpace value="collapse"/><xs:enumeration value=" a  b "/></xs:restriction></xs:simpleType>
                  <xs:simpleType name="d"><xs:restriction base="xs:decimal"><xs:totalDigits value="5" fixed="true"/><xs:fractionDigits value="2"/><xs:minExclusive value="01.50"/><xs:maxInclusive value="100"/></xs:restriction></xs:simpleType>
                </xs:schema>';
                select string_agg(concat(c.name collate database_default, '.', f.facet_id, ':', f.kind collate database_default, ':', f.kind_desc, ':', f.is_fixed, ':[',
                    f.value collate database_default, ']'), ' ') within group (order by f.xml_component_id, f.facet_id)
                  from sys.xml_schema_facets f join sys.xml_schema_components c on c.xml_component_id = f.xml_component_id where f.xml_component_id >= 65536
                """));

    [TestMethod]
    public void BlockAndFinal_FollowTheSchemaDefaults()
        => AreEqual("ct:11100100 plain:00011000 lst:00000011 | le:11001000: g:11100111:5 u:11001010: | la:11 lc:00", new Simulation().ExecuteScalar("""
            create xml schema collection dbo.fl as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns:t="urn:t" attributeFormDefault="qualified" blockDefault="restriction" finalDefault="extension">
              <xs:complexType name="ct" abstract="true" mixed="true" block="extension" final="restriction"><xs:sequence><xs:element name="le" type="xs:int" form="qualified"/></xs:sequence><xs:attribute name="la" type="xs:int"/><xs:attribute name="lc" type="xs:int" form="unqualified"/></xs:complexType>
              <xs:complexType name="plain"/>
              <xs:simpleType name="lst" final="list union"><xs:list itemType="xs:int"/></xs:simpleType>
              <xs:element name="g" type="xs:int" block="substitution" final="#all" fixed="5"/>
              <xs:element name="u"/>
            </xs:schema>';
            select concat(
                (select string_agg(concat(name, ':', is_abstract, allows_mixed_content, is_extension_blocked, is_restriction_blocked, is_final_extension, is_final_restriction,
                    is_final_list_member, is_final_union_member), ' ') within group (order by xml_component_id) from sys.xml_schema_types where xml_collection_id > 1), ' | ',
                (select string_agg(concat(name, ':', is_qualified, must_be_qualified, is_default_fixed, is_extension_blocked, is_restriction_blocked, is_substitution_blocked,
                    is_final_extension, is_final_restriction, ':', default_value), ' ') within group (order by xml_component_id) from sys.xml_schema_elements where xml_collection_id > 1), ' | ',
                (select string_agg(concat(name, ':', is_qualified, must_be_qualified), ' ') within group (order by xml_component_id) from sys.xml_schema_attributes where xml_collection_id > 1))
            """));

    [TestMethod]
    public void TheViews_TakeRealsObjectIds()
        => AreEqual(
            "message_type_xml_schema_collection_usages=-478 xml_schema_attributes=-456 xml_schema_collections=-463 xml_schema_component_placements=-459 "
                + "xml_schema_components=-451 xml_schema_elements=-454 xml_schema_facets=-453 xml_schema_model_groups=-455 xml_schema_namespaces=-450 "
                + "xml_schema_types=-452 xml_schema_wildcard_namespaces=-458 xml_schema_wildcards=-457",
            new Simulation().ExecuteScalar("""
                select string_agg(concat(name, '=', object_id), ' ') within group (order by name) from sys.all_objects
                 where name like 'xml[_]schema[_]%' or name = 'message_type_xml_schema_collection_usages'
                """));

    [TestMethod]
    public void AnAlterAdd_ShowsAtOnce_AndLeavesWithItsRollback()
    {
        using var connection = new Simulation().CreateOpenConnection();
        const string Count = "select count(*) from sys.xml_schema_components where xml_collection_id > 1";
        _ = connection.CreateCommand("create xml schema collection dbo.a as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"r\" type=\"xs:int\"/></xs:schema>'").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand(Count).ExecuteScalar());
        _ = connection.CreateCommand("begin tran; alter xml schema collection dbo.a add N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"s\" type=\"xs:int\"/></xs:schema>'").ExecuteNonQuery();
        AreEqual(2, connection.CreateCommand(Count).ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand(Count).ExecuteScalar());
    }

    [TestMethod]
    public void ARestrictedUser_SeesTheSysCollection_AndTheCollectionsItMayView()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create user u without login;
            create user v without login;
            create user w without login;
            create xml schema collection dbo.seen as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="s" type="xs:int"/></xs:schema>';
            create xml schema collection dbo.hidden as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="h" type="xs:int"/></xs:schema>';
            grant references on xml schema collection::dbo.seen to u;
            grant execute on xml schema collection::dbo.seen to v;
            grant view definition on schema::dbo to w;
            """);
        AreEqual("sys,seen|4|101|20", sim.ExecuteScalar("""
            execute as user = 'u';
            select concat((select string_agg(name, ',') within group (order by xml_collection_id) from sys.xml_schema_collections), '|',
                (select count(*) from sys.xml_schema_namespaces), '|', (select count(*) from sys.xml_schema_components), '|',
                (select count(*) from sys.xml_schema_component_placements));
            revert;
            """));
        AreEqual("sys|100", sim.ExecuteScalar("""
            execute as user = 'v';
            select concat((select string_agg(name, ',') from sys.xml_schema_collections), '|', (select count(*) from sys.xml_schema_components));
            revert;
            """));
        AreEqual("sys,seen,hidden|102", sim.ExecuteScalar("""
            execute as user = 'w';
            select concat((select string_agg(name, ',') within group (order by xml_collection_id) from sys.xml_schema_collections), '|',
                (select count(*) from sys.xml_schema_components));
            revert;
            """));
    }

    [TestMethod]
    public void Restore_KeepsEachSegmentsComponentIds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create database d",
            """
            use d;
            create xml schema collection dbo.a as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="r" type="xs:int"/></xs:schema>';
            create xml schema collection dbo.b as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="b" type="xs:int"/></xs:schema>';
            alter xml schema collection dbo.a add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:z"><xs:element name="z" type="xs:int"/></xs:schema>';
            """);
        const string Listing = "use d; select string_agg(concat(name collate database_default, xml_component_id), ' ') within group (order by xml_component_id) from sys.xml_schema_components where xml_collection_id > 1";
        AreEqual("r65536 b65537 z65538", sim.ExecuteScalar(Listing));
        _ = sim.ExecuteNonQuery("backup database d to disk = N'd.bak'; drop database d");
        _ = sim.ExecuteNonQuery("restore database d from disk = N'd.bak'");
        AreEqual("r65536 b65537 z65538", sim.ExecuteScalar(Listing));
        AreEqual(65539, sim.ExecuteScalar("""
            use d;
            create xml schema collection dbo.c as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="c" type="xs:int"/></xs:schema>';
            select xml_component_id from sys.xml_schema_components where name = N'c'
            """));
    }
}
