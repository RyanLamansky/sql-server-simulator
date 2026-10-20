using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>XML_SCHEMA_NAMESPACE</c> regenerates a collection's XSD from its
/// components as real does; every expectation is real's own text (probed
/// 2026-10-20 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class XmlSchemaNamespaceTests
{
    [TestMethod]
    public void EveryComponentKind_SortedByKindThenName()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(@"create xml schema collection zsc as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:a"" xmlns=""urn:a"" elementFormDefault=""qualified"" attributeFormDefault=""unqualified"">
<xs:annotation><xs:documentation>doc</xs:documentation></xs:annotation>
<xs:simpleType name=""S""><xs:restriction base=""xs:string""><xs:maxLength value=""5""/><xs:enumeration value=""a""/><xs:enumeration value=""b""/></xs:restriction></xs:simpleType>
<xs:simpleType name=""L""><xs:list itemType=""xs:int""/></xs:simpleType>
<xs:simpleType name=""U""><xs:union memberTypes=""xs:int xs:boolean""/></xs:simpleType>
<xs:complexType name=""T"" mixed=""false""><xs:sequence><xs:element name=""i"" type=""xs:int"" minOccurs=""0"" maxOccurs=""unbounded""/><xs:element name=""s"" type=""S"" default=""a""/><xs:choice><xs:element name=""c1"" type=""xs:int""/><xs:element name=""c2"" nillable=""true"" type=""xs:int""/></xs:choice><xs:any namespace=""##other"" processContents=""lax"" minOccurs=""0""/></xs:sequence><xs:attribute name=""k"" type=""xs:int"" use=""required""/><xs:attribute name=""f"" type=""xs:string"" fixed=""x""/><xs:anyAttribute namespace=""##any""/></xs:complexType>
<xs:complexType name=""D""><xs:complexContent><xs:extension base=""T""><xs:sequence><xs:element name=""e"" type=""L""/></xs:sequence></xs:extension></xs:complexContent></xs:complexType>
<xs:element name=""r"" type=""T""/>
<xs:element name=""g"" type=""xs:string"" abstract=""true""/>
<xs:element name=""h"" type=""xs:string"" substitutionGroup=""g""/>
<xs:element name=""sc""><xs:complexType><xs:simpleContent><xs:extension base=""xs:decimal""><xs:attribute name=""u"" type=""xs:string""/></xs:extension></xs:simpleContent></xs:complexType></xs:element>
<xs:element name=""al""><xs:complexType><xs:all><xs:element name=""x"" type=""xs:int""/><xs:element ref=""h"" minOccurs=""0""/></xs:all></xs:complexType></xs:element>
<xs:attribute name=""ga"" type=""xs:int""/>
<xs:group name=""G""><xs:sequence><xs:element name=""gg"" type=""xs:int""/></xs:sequence></xs:group>
<xs:element name=""gr""><xs:complexType><xs:group ref=""G""/></xs:complexType></xs:element>
</xs:schema>';");
        AreEqual(@"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:a"" targetNamespace=""urn:a"" elementFormDefault=""qualified""><xsd:attribute name=""ga"" type=""xsd:int""/><xsd:element name=""al""><xsd:complexType><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:all><xsd:element name=""x"" type=""xsd:int""/><xsd:element ref=""t:h"" minOccurs=""0""/></xsd:all></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element><xsd:element name=""g"" type=""xsd:string"" abstract=""true""/><xsd:element name=""gr""><xsd:complexType><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:group ref=""t:G""/></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element><xsd:element name=""h"" type=""xsd:string"" substitutionGroup=""t:g""/><xsd:element name=""r"" type=""t:T""/><xsd:element name=""sc""><xsd:complexType><xsd:simpleContent><xsd:extension base=""xsd:decimal""><xsd:attribute name=""u"" type=""xsd:string""/></xsd:extension></xsd:simpleContent></xsd:complexType></xsd:element><xsd:complexType name=""D""><xsd:complexContent><xsd:extension base=""t:T""><xsd:sequence><xsd:element name=""e"" type=""t:L""/></xsd:sequence></xsd:extension></xsd:complexContent></xsd:complexType><xsd:complexType name=""T""><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element name=""i"" type=""xsd:int"" minOccurs=""0"" maxOccurs=""unbounded""/><xsd:element name=""s"" type=""t:S"" default=""a""/><xsd:choice><xsd:element name=""c1"" type=""xsd:int""/><xsd:element name=""c2"" type=""xsd:int"" nillable=""true""/></xsd:choice><xsd:any namespace=""##other"" processContents=""lax"" minOccurs=""0""/></xsd:sequence><xsd:attribute name=""k"" type=""xsd:int"" use=""required""/><xsd:attribute name=""f"" type=""xsd:string"" fixed=""x""/><xsd:anyAttribute/></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:simpleType name=""L""><xsd:list itemType=""xsd:int""/></xsd:simpleType><xsd:group name=""G""><xsd:sequence><xsd:element name=""gg"" type=""xsd:int""/></xsd:sequence></xsd:group><xsd:simpleType name=""S""><xsd:restriction base=""xsd:string""><xsd:maxLength value=""5""/><xsd:enumeration value=""a""/><xsd:enumeration value=""b""/></xsd:restriction></xsd:simpleType><xsd:simpleType name=""U""><xsd:union memberTypes=""xsd:int xsd:boolean""/></xsd:simpleType></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zsc') as nvarchar(max))"));
    }

    [TestMethod]
    public void SeveralNamespaces_ImportsAndForeignPrefixes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(@"create xml schema collection zs1 as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:b"" xmlns=""urn:b""><xs:element name=""rb""><xs:complexType><xs:sequence><xs:element name=""loc"" type=""xs:int""/><xs:element name=""q"" form=""qualified"" type=""xs:int""/></xs:sequence><xs:attribute name=""at"" form=""qualified"" type=""xs:int""/></xs:complexType></xs:element><xs:element name=""B"" type=""xs:int""/><xs:element name=""a"" type=""xs:int""/></xs:schema>';");
        _ = simulation.ExecuteNonQuery(@"alter xml schema collection zs1 add N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" xmlns:b=""urn:b"" xmlns=""urn:a"" targetNamespace=""urn:a"" elementFormDefault=""qualified""><xs:import namespace=""urn:b""/><xs:element name=""ra""><xs:annotation><xs:documentation>d</xs:documentation></xs:annotation><xs:complexType><xs:sequence><xs:element ref=""b:rb""/><xs:element name=""u"" form=""unqualified"" type=""xs:int"" minOccurs=""1"" maxOccurs=""1""/></xs:sequence><xs:attributeGroup ref=""AG""/></xs:complexType></xs:element><xs:attributeGroup name=""AG""><xs:attribute name=""p"" type=""xs:int"" default=""3""/></xs:attributeGroup><xs:simpleType name=""F""><xs:restriction base=""xs:decimal""><xs:enumeration value=""1""/><xs:totalDigits value=""5""/><xs:minInclusive value=""0""/><xs:pattern value=""\d+""/><xs:fractionDigits value=""2""/></xs:restriction></xs:simpleType><xs:simpleType name=""LU""><xs:list><xs:simpleType><xs:union><xs:simpleType><xs:restriction base=""xs:int""/></xs:simpleType></xs:union></xs:simpleType></xs:list></xs:simpleType><xs:complexType name=""CT"" block=""extension"" final=""restriction"" abstract=""true""><xs:sequence><xs:element name=""z"" type=""xs:int""/></xs:sequence></xs:complexType><xs:complexType name=""CR""><xs:complexContent><xs:restriction base=""CT2""><xs:sequence><xs:element name=""z"" type=""xs:int""/></xs:sequence></xs:restriction></xs:complexContent></xs:complexType><xs:complexType name=""CT2""><xs:sequence><xs:element name=""z"" type=""xs:int"" minOccurs=""0""/></xs:sequence></xs:complexType></xs:schema>';");
        _ = simulation.ExecuteNonQuery(@"alter xml schema collection zs1 add N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema""><xs:element name=""nn"" type=""xs:string""/></xs:schema>';");
        AreEqual(@"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:b"" targetNamespace=""urn:b"" attributeFormDefault=""qualified""><xsd:element name=""B"" type=""xsd:int""/><xsd:element name=""a"" type=""xsd:int""/><xsd:element name=""rb""><xsd:complexType><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element name=""loc"" type=""xsd:int""/><xsd:element name=""q"" type=""xsd:int"" form=""qualified""/></xsd:sequence><xsd:attribute name=""at"" type=""xsd:int""/></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element></xsd:schema><xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:ns1=""urn:b"" xmlns:t=""urn:a"" targetNamespace=""urn:a"" elementFormDefault=""qualified""><xsd:import namespace=""urn:b""/><xsd:element name=""ra""><xsd:complexType><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element ref=""ns1:rb""/><xsd:element name=""u"" type=""xsd:int"" form=""unqualified""/></xsd:sequence><xsd:attributeGroup ref=""t:AG""/></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element><xsd:attributeGroup name=""AG""><xsd:attribute name=""p"" type=""xsd:int"" default=""3""/></xsd:attributeGroup><xsd:complexType name=""CR""><xsd:complexContent><xsd:restriction base=""t:CT2""><xsd:sequence><xsd:element name=""z"" type=""xsd:int""/></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:complexType name=""CT"" abstract=""true"" block=""extension"" final=""restriction""><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element name=""z"" type=""xsd:int""/></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:complexType name=""CT2""><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element name=""z"" type=""xsd:int"" minOccurs=""0""/></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:simpleType name=""LU""><xsd:list><xsd:simpleType><xsd:union><xsd:simpleType><xsd:restriction base=""xsd:int""/></xsd:simpleType></xsd:union></xsd:simpleType></xsd:list></xsd:simpleType><xsd:simpleType name=""F""><xsd:restriction base=""xsd:decimal""><xsd:enumeration value=""1""/><xsd:totalDigits value=""5""/><xsd:minInclusive value=""0""/><xsd:pattern value=""\d+""/><xsd:fractionDigits value=""2""/></xsd:restriction></xsd:simpleType></xsd:schema><xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""nn"" type=""xsd:string""/></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zs1') as nvarchar(max))"));
    }

    [TestMethod]
    public void FormDefaults_FollowMostLocalDeclarations()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(@"create xml schema collection zs2 as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:m"" elementFormDefault=""qualified""><xs:element name=""r""><xs:complexType><xs:sequence><xs:element name=""e1"" form=""unqualified"" type=""xs:int""/><xs:element name=""e2"" form=""unqualified"" type=""xs:int""/><xs:element name=""e3"" type=""xs:int""/></xs:sequence><xs:attribute name=""a1"" form=""qualified"" type=""xs:int""/><xs:attribute name=""a2"" type=""xs:int""/><xs:attribute name=""a3"" type=""xs:int""/></xs:complexType></xs:element></xs:schema>';");
        AreEqual(@"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:m"" targetNamespace=""urn:m""><xsd:element name=""r""><xsd:complexType><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element name=""e1"" type=""xsd:int""/><xsd:element name=""e2"" type=""xsd:int""/><xsd:element name=""e3"" type=""xsd:int"" form=""qualified""/></xsd:sequence><xsd:attribute name=""a1"" type=""xsd:int"" form=""qualified""/><xsd:attribute name=""a2"" type=""xsd:int""/><xsd:attribute name=""a3"" type=""xsd:int""/></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zs2') as nvarchar(max))"));
    }

    [TestMethod]
    public void Namespaces_InTheOrderTheyWereAdded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(@"create xml schema collection zs3 as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema""><xs:element name=""nn"" type=""xs:string""/></xs:schema>';");
        _ = simulation.ExecuteNonQuery(@"alter xml schema collection zs3 add N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:z""><xs:element name=""zz"" type=""xs:string""/></xs:schema>';");
        _ = simulation.ExecuteNonQuery(@"alter xml schema collection zs3 add N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:c""><xs:element name=""cc"" type=""xs:string""/></xs:schema>';");
        AreEqual(@"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""nn"" type=""xsd:string""/></xsd:schema><xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:z"" targetNamespace=""urn:z""><xsd:element name=""zz"" type=""xsd:string""/></xsd:schema><xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:c"" targetNamespace=""urn:c""><xsd:element name=""cc"" type=""xsd:string""/></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zs3') as nvarchar(max))"));
    }

    [TestMethod]
    public void BlockDefault_AnonymousTypesWildcardsAndSimpleContent()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(@"create xml schema collection zs4 as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:p"" xmlns=""urn:p"" blockDefault=""extension"">
<xs:element name=""e1""><xs:simpleType><xs:restriction base=""xs:string""><xs:length value=""3""/></xs:restriction></xs:simpleType></xs:element>
<xs:element name=""e2"" type=""xs:int"" fixed=""7""/>
<xs:element name=""e3""/>
<xs:element name=""e4"" type=""xs:int"" block=""substitution"" final=""#all""/>
<xs:complexType name=""M"" mixed=""true""><xs:sequence minOccurs=""0""><xs:choice maxOccurs=""unbounded""><xs:element name=""m1"" type=""xs:int""/><xs:sequence><xs:element name=""m2"" type=""xs:int""/></xs:sequence></xs:choice></xs:sequence><xs:attribute name=""an""><xs:simpleType><xs:restriction base=""xs:int""><xs:maxInclusive value=""9""/></xs:restriction></xs:simpleType></xs:attribute></xs:complexType>
<xs:complexType name=""W""><xs:sequence><xs:any namespace=""##targetNamespace ##local urn:x"" processContents=""skip""/><xs:any namespace=""##other"" maxOccurs=""2""/></xs:sequence><xs:anyAttribute namespace=""urn:y"" processContents=""strict""/></xs:complexType>
<xs:complexType name=""SR""><xs:simpleContent><xs:restriction base=""SB""><xs:maxExclusive value=""100""/></xs:restriction></xs:simpleContent></xs:complexType>
<xs:complexType name=""SB""><xs:simpleContent><xs:extension base=""xs:decimal""><xs:attribute name=""u"" type=""xs:string"" default=""z""/></xs:extension></xs:simpleContent></xs:complexType>
<xs:complexType name=""E""/>
<xs:simpleType name=""UN""><xs:union memberTypes=""xs:int""><xs:simpleType><xs:restriction base=""xs:string""/></xs:simpleType></xs:union></xs:simpleType>
<xs:simpleType name=""WS""><xs:restriction base=""xs:string""><xs:whiteSpace value=""collapse""/><xs:pattern value=""a""/><xs:pattern value=""b""/></xs:restriction></xs:simpleType>
</xs:schema>';");
        AreEqual(@"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:p"" targetNamespace=""urn:p""><xsd:complexType name=""SB"" block=""extension""><xsd:simpleContent><xsd:extension base=""xsd:decimal""><xsd:attribute name=""u"" type=""xsd:string"" default=""z""/></xsd:extension></xsd:simpleContent></xsd:complexType><xsd:complexType name=""SR"" block=""extension""><xsd:simpleContent><xsd:restriction base=""t:SB""><xsd:maxExclusive value=""100""/></xsd:restriction></xsd:simpleContent></xsd:complexType><xsd:element name=""e1"" block=""extension""><xsd:simpleType><xsd:restriction base=""xsd:string""><xsd:length value=""3""/></xsd:restriction></xsd:simpleType></xsd:element><xsd:element name=""e2"" type=""xsd:int"" block=""extension"" fixed=""7""/><xsd:element name=""e3"" type=""xsd:anyType"" block=""extension""/><xsd:element name=""e4"" type=""xsd:int"" block=""substitution"" final=""#all""/><xsd:complexType name=""E"" block=""extension""><xsd:complexContent><xsd:restriction base=""xsd:anyType""/></xsd:complexContent></xsd:complexType><xsd:complexType name=""M"" block=""extension"" mixed=""true""><xsd:complexContent mixed=""true""><xsd:restriction base=""xsd:anyType""><xsd:sequence minOccurs=""0""><xsd:choice maxOccurs=""unbounded""><xsd:element name=""m1"" type=""xsd:int"" block=""extension""/><xsd:sequence><xsd:element name=""m2"" type=""xsd:int"" block=""extension""/></xsd:sequence></xsd:choice></xsd:sequence><xsd:attribute name=""an""><xsd:simpleType><xsd:restriction base=""xsd:int""><xsd:maxInclusive value=""9""/></xsd:restriction></xsd:simpleType></xsd:attribute></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:complexType name=""W"" block=""extension""><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:any namespace=""urn:p ##local urn:x"" processContents=""skip""/><xsd:any namespace=""##other"" maxOccurs=""2""/></xsd:sequence><xsd:anyAttribute namespace=""urn:y""/></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:simpleType name=""WS""><xsd:restriction base=""xsd:string""><xsd:whiteSpace value=""collapse""/><xsd:pattern value=""a""/><xsd:pattern value=""b""/></xsd:restriction></xsd:simpleType><xsd:simpleType name=""UN""><xsd:union memberTypes=""xsd:int""><xsd:simpleType><xsd:restriction base=""xsd:string""/></xsd:simpleType></xsd:union></xsd:simpleType></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zs4') as nvarchar(max))"));
    }

    [TestMethod]
    public void ElementAndAttributeAttributeOrder()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(@"create xml schema collection zs5 as N'<xs:schema xmlns:xs=""http://www.w3.org/2001/XMLSchema"" targetNamespace=""urn:q"" xmlns=""urn:q"">
<xs:element name=""hd"" type=""xs:string""/>
<xs:element name=""ge"" type=""xs:string"" substitutionGroup=""hd"" abstract=""true"" nillable=""true"" default=""d"" block=""restriction extension"" final=""extension""/>
<xs:element name=""ro""><xs:complexType><xs:sequence><xs:element name=""le"" type=""xs:string"" form=""qualified"" nillable=""true"" default=""x"" block=""#all"" minOccurs=""0"" maxOccurs=""5""/><xs:element name=""lf"" type=""xs:string"" fixed=""f"" maxOccurs=""2""/></xs:sequence><xs:attribute name=""la"" type=""xs:string"" form=""qualified"" use=""required"" fixed=""v""/><xs:attribute name=""lb"" type=""xs:string"" default=""w""/><xs:attribute ref=""ga"" use=""required""/><xs:attribute ref=""gb"" default=""r""/></xs:complexType></xs:element>
<xs:attribute name=""ga"" type=""xs:int""/>
<xs:attribute name=""gb"" type=""xs:string""/>
<xs:simpleType name=""st"" final=""list union""><xs:restriction base=""xs:int""/></xs:simpleType>
<xs:simpleType name=""sa"" final=""#all""><xs:restriction base=""xs:int""/></xs:simpleType>
<xs:complexType name=""ct"" block=""#all"" final=""extension""><xs:sequence/></xs:complexType>
</xs:schema>';");
        AreEqual(@"<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:t=""urn:q"" targetNamespace=""urn:q""><xsd:attribute name=""ga"" type=""xsd:int""/><xsd:attribute name=""gb"" type=""xsd:string""/><xsd:element name=""ge"" type=""xsd:string"" substitutionGroup=""t:hd"" abstract=""true"" block=""extension restriction"" final=""extension"" nillable=""true"" default=""d""/><xsd:element name=""hd"" type=""xsd:string""/><xsd:element name=""ro""><xsd:complexType><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence><xsd:element name=""le"" type=""xsd:string"" form=""qualified"" minOccurs=""0"" maxOccurs=""5"" block=""#all"" nillable=""true"" default=""x""/><xsd:element name=""lf"" type=""xsd:string"" maxOccurs=""2"" fixed=""f""/></xsd:sequence><xsd:attribute name=""la"" type=""xsd:string"" form=""qualified"" use=""required"" fixed=""v""/><xsd:attribute name=""lb"" type=""xsd:string"" default=""w""/><xsd:attribute ref=""t:ga"" use=""required""/><xsd:attribute ref=""t:gb"" default=""r""/></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element><xsd:complexType name=""ct"" block=""#all"" final=""extension""><xsd:complexContent><xsd:restriction base=""xsd:anyType""><xsd:sequence/></xsd:restriction></xsd:complexContent></xsd:complexType><xsd:simpleType name=""sa"" final=""#all""><xsd:restriction base=""xsd:int""/></xsd:simpleType><xsd:simpleType name=""st"" final=""list union""><xsd:restriction base=""xsd:int""/></xsd:simpleType></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zs5') as nvarchar(max))"));
    }

    [TestMethod]
    public void NamespaceArgument_NarrowsToOneSchema()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create xml schema collection zsc as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:a\" elementFormDefault=\"qualified\"><xs:element name=\"ra\" type=\"xs:int\"/></xs:schema>'");
        _ = simulation.ExecuteNonQuery("alter xml schema collection zsc add N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"rn\" type=\"xs:string\"/></xs:schema>'");
        AreEqual("<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:t=\"urn:a\" targetNamespace=\"urn:a\"><xsd:element name=\"ra\" type=\"xsd:int\"/></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zsc', N'urn:a') as nvarchar(max))"));
        AreEqual("<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"rn\" type=\"xsd:string\"/></xsd:schema>", simulation.ExecuteScalar("select cast(xml_schema_namespace(N'dbo', N'zsc', N'') as nvarchar(max))"));
        simulation.AssertSqlError("select xml_schema_namespace(N'dbo', N'zsc', N'URN:A')", 6312, "Could not find schema components with target namespace 'URN:A' in collection 'zsc'.");
        simulation.AssertSqlError("select xml_schema_namespace(N'dbo', N'zsc', null)", 6312, "Could not find schema components with target namespace '' in collection 'zsc'.");
    }
    private static List<string> Messages(Simulation simulation, string commandText)
    {
        var messages = new List<string>();
        using var connection = simulation.CreateOpenConnection();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand(commandText).ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void PatternOnANonStringType_WarnsOfRoundTripping()
    {
        var simulation = new Simulation();
        var warned = Messages(simulation, """
            create xml schema collection zw as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:w" xmlns="urn:w">
            <xs:simpleType name="A"><xs:restriction base="xs:int"><xs:pattern value="1"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="H"><xs:restriction base="A"><xs:maxInclusive value="1"/></xs:restriction></xs:simpleType>
            <xs:complexType name="T"><xs:sequence><xs:element name="le"><xs:simpleType><xs:restriction base="xs:int"><xs:pattern value="1"/></xs:restriction></xs:simpleType></xs:element></xs:sequence>
            <xs:attribute name="la"><xs:simpleType><xs:restriction base="xs:date"><xs:pattern value=".*"/></xs:restriction></xs:simpleType></xs:attribute></xs:complexType>
            <xs:simpleType name="B"><xs:restriction base="xs:anyURI"><xs:pattern value="1"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="S"><xs:restriction base="xs:string"><xs:pattern value="1"/></xs:restriction></xs:simpleType>
            </xs:schema>'
            """);
        const string Tail = "' is restricted by a facet 'pattern' that may impede full round-tripping of instances of this type";
        CollectionAssert.AreEqual(
            new[]
            {
                "6966:Warning: Type '{urn:w}A" + Tail,
                "6966:Warning: Type '{urn:w}H" + Tail,
                "6966:Warning: Type '#xmlns(ns=urn:w) xs-nun(/complexType(ns:T)/sequence()/le/simpleType())" + Tail,
                "6966:Warning: Type '#xmlns(ns=urn:w) xs-nun(/complexType(ns:T)/@la/simpleType())" + Tail,
            },
            warned);
        CollectionAssert.AreEqual(
            new[] { "6966:Warning: Type 'N" + Tail },
            Messages(simulation, """alter xml schema collection zw add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:simpleType name="N"><xs:restriction base="xs:float"><xs:pattern value="1"/></xs:restriction></xs:simpleType></xs:schema>'"""));
    }
    [TestMethod]
    public void RoundTripWarnings_FollowCompileOrder_AndOutliveARefusal()
    {
        const string Tail = "' is restricted by a facet 'pattern' that may impede full round-tripping of instances of this type";
        CollectionAssert.AreEqual(
            new[] { "6966:Warning: Type '{urn:w}A" + Tail, "6966:Warning: Type '{urn:w}H" + Tail },
            Messages(new Simulation(), """
                create xml schema collection zw as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:w" xmlns="urn:w">
                <xs:simpleType name="H"><xs:restriction base="A"><xs:maxInclusive value="1"/></xs:restriction></xs:simpleType>
                <xs:simpleType name="A"><xs:restriction base="xs:int"><xs:pattern value="1"/></xs:restriction></xs:simpleType></xs:schema>'
                """));
        CollectionAssert.AreEqual(
            new[] { "6966:Warning: Type '{urn:w}A" + Tail },
            Messages(new Simulation(), """
                begin try create xml schema collection zw as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:w" xmlns="urn:w">
                <xs:simpleType name="A"><xs:restriction base="xs:int"><xs:pattern value="1"/></xs:restriction></xs:simpleType>
                <xs:simpleType name="H"><xs:restriction base="A"><xs:maxInclusive value="5"/></xs:restriction></xs:simpleType></xs:schema>' end try begin catch end catch
                """));
    }
}
