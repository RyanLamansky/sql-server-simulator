using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How an instance's XML schema collection types a path step by step: each
/// step reaches the declarations its context's content models allow — a
/// derived type's and a substitution group's included — so a name nothing
/// there declares is a compile-time refusal quoting the context's type, and
/// atomizing an element of complex or empty content is refused. Every
/// expectation is real's (probed 2026-10-20 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class TypedXmlPathTypingTests
{
    private const string Collection = """
        create xml schema collection zsc as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns="urn:t" elementFormDefault="qualified">
        <xs:complexType name="RT"><xs:sequence><xs:element name="i" type="xs:int" maxOccurs="3"/>
          <xs:element name="c" minOccurs="0"><xs:complexType><xs:sequence><xs:element name="n" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
          <xs:element ref="g" minOccurs="0"/>
          <xs:element name="m" minOccurs="0"><xs:complexType mixed="true"><xs:sequence><xs:element name="b" type="xs:string" minOccurs="0"/></xs:sequence></xs:complexType></xs:element>
          <xs:element name="sc" minOccurs="0"><xs:complexType><xs:simpleContent><xs:extension base="xs:decimal"><xs:attribute name="u" type="xs:string"/></xs:extension></xs:simpleContent></xs:complexType></xs:element>
          <xs:element name="e" minOccurs="0"><xs:complexType/></xs:element>
        </xs:sequence><xs:attribute name="k" type="xs:int"/></xs:complexType>
        <xs:complexType name="XT"><xs:complexContent><xs:extension base="RT"><xs:sequence><xs:element name="x2" type="xs:int"/></xs:sequence><xs:anyAttribute namespace="##other"/></xs:extension></xs:complexContent></xs:complexType>
        <xs:element name="r" type="RT"/>
        <xs:element name="xr" type="XT"/>
        <xs:element name="g" type="xs:string"/>
        <xs:element name="h" substitutionGroup="g" type="xs:string"/>
        <xs:element name="a"><xs:complexType><xs:choice><xs:element name="i" type="xs:int"/><xs:element name="j" type="xs:int"/></xs:choice></xs:complexType></xs:element>
        </xs:schema>'
        """;

    private const string Prolog = "declare namespace t=\"urn:t\"; ";

    private static Simulation Typed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Collection);
        return simulation;
    }

    [TestMethod]
    [DataRow("(/t:r/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:r,t{urn:t}:RT) *\".")]
    [DataRow("(/t:a/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:a,#anonymous) *\".")]
    [DataRow("(/t:r/t:c/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:c,#anonymous) *\".")]
    [DataRow("(/t:r/t:i/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:i,xs:int) *\".")]
    [DataRow("(/t:r/t:g/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:g,xs:string) *\".")]
    [DataRow("(/t:r/zz)[1]", 2261, "There is no element named 'zz' in the type 'element(t{urn:t}:r,t{urn:t}:RT) *'.")]
    [DataRow("(/t:r/*:zz)[1]", 2261, "There is no element named 'zz' in the type 'element(t{urn:t}:r,t{urn:t}:RT) *'.")]
    [DataRow("(/t:zz)[1]", 2262, "There is no element named '{urn:t}:zz'")]
    [DataRow("(/zz)[1]", 2260, "There is no element named 'zz'")]
    [DataRow("(/t:a/*/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"(element(t{urn:t}:i,xs:int) | element(t{urn:t}:j,xs:int)) *\".")]
    [DataRow("(/t:a//t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"(element(t{urn:t}:a,#anonymous) | element(t{urn:t}:i,xs:int) | comment | processing-instruction | text | element(t{urn:t}:j,xs:int)) *\".")]
    [DataRow("(/t:r[t:zz])[1]/t:i", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:r,t{urn:t}:RT)\".")]
    [DataRow("for $e in /t:r return $e/t:zz", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:r,t{urn:t}:RT)\".")]
    [DataRow("count(/t:r/t:zz)", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:r,t{urn:t}:RT) *\".")]
    [DataRow("(/t:xr/@zz)[1]", 2219, "There is no attribute named '@zz' in the type 'element(t{urn:t}:xr,t{urn:t}:XT) *'.")]
    [DataRow("(/t:r/t:i/@zz)[1]", 2219, "There is no attribute named '@zz' in the type 'element(t{urn:t}:i,xs:int) *'.")]
    [DataRow("declare namespace o=\"urn:o\"; (/t:r/@o:zz)[1]", 2221, "There is no attribute named '@{urn:o}:zz' in the type 'element(t{urn:t}:r,t{urn:t}:RT) *'.")]
    [DataRow("(/t:r/t:c/text())[1]", 2377, "Result of 'text()' expression is statically 'empty'")]
    [DataRow("(/t:r/t:c)[1]", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 't{urn:t}:c' within inferred type 'element(t{urn:t}:c,#anonymous) ?'.")]
    [DataRow("(/t:r)[1]", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 't{urn:t}:RT' within inferred type 'element(t{urn:t}:r,t{urn:t}:RT) ?'.")]
    [DataRow("data((/t:r/t:m)[1])", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 't{urn:t}:m' within inferred type 'element(t{urn:t}:m,#anonymous) ?'.")]
    [DataRow("(/t:r/t:c/..)[1]/t:zz", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 'xs:anyType' within inferred type 'element(t{urn:t}:zz,xs:anyType) *'.")]
    [DataRow("(/t:r/t:*)[1]", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 't{urn:t}:c' within inferred type '(element(t{urn:t}:i,xs:int) | element(t{urn:t}:c,#anonymous) | element(t{urn:t}:g,xs:string) | element(t{urn:t}:h,xs:string) | element(t{urn:t}:m,#anonymous) | element(t{urn:t}:sc,#anonymous) | element(t{urn:t}:e,#anonymous) | element(t{urn:t}:x2,xs:int)) ?'.")]
    [DataRow("(/t:r/t:e)[1]", 2213, "Cannot atomize/apply data() on expression that contains type 't{urn:t}:e' within inferred type 'element(t{urn:t}:e,#anonymous) ?'")]
    public void UnreachableOrUnatomizableStep_IsRefusedAsTheBatchCompiles(string path, int number, string message) =>
        Typed().AssertSqlError(
            $"declare @x xml(zsc) = null; select @x.value('{Prolog}{path.Replace("'", "''", StringComparison.Ordinal)}', 'nvarchar(9)')",
            number,
            "XQuery [value()]: " + message);

    [TestMethod]
    [DataRow("/t:r", "t:zz[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:r,t{urn:t}:RT)\".")]
    [DataRow("/t:r/t:i", "(t:i)[1]", 2263, "There is no element named \"{urn:t}:i\" in the type \"element(t{urn:t}:i,xs:int)\".")]
    [DataRow("/t:r", ".", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 't{urn:t}:RT' within inferred type 'element(t{urn:t}:r,t{urn:t}:RT)'.")]
    [DataRow("/t:r", "(/t:zz)[1]", 2262, "There is no element named '{urn:t}:zz'")]
    [DataRow("/t:r/t:i", "(../t:zz)[1]", 9314, "Cannot implicitly atomize or apply 'fn:data()' to complex content elements, found type 'xs:anyType' within inferred type 'element(t{urn:t}:zz,xs:anyType) ?'.")]
    [DataRow("/t:r", "(t:c/t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:c,#anonymous) ?\".")]
    [DataRow("/t:r", "(@zz)[1]", 2219, "There is no attribute named '@zz' in the type 'element(t{urn:t}:r,t{urn:t}:RT)'.")]
    [DataRow("//t:c", "(t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:c,#anonymous)\".")]
    [DataRow("/t:a/t:*", "(t:zz)[1]", 2263, "There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:i,xs:int) | element(t{urn:t}:j,xs:int)\".")]
    public void NodesRow_IsTypedAsOneNodeOfItsPath(string rows, string path, int number, string message) =>
        Typed().AssertSqlError(
            $"declare @x xml(zsc) = null; select c.value('{Prolog}{path}', 'nvarchar(9)') from @x.nodes('{Prolog}{rows}') n(c)",
            number,
            "XQuery [value()]: " + message);

    [TestMethod]
    public void NestedNodesRow_IsTypedFromItsOwnRow() =>
        Typed().AssertSqlError(
            $"declare @x xml(zsc) = null; select c2.value('{Prolog}(t:zz)[1]', 'int') from @x.nodes('{Prolog}/t:r') n(c) cross apply c.nodes('{Prolog}t:c') m(c2)",
            2263,
            "XQuery [value()]: There is no element named \"{urn:t}:zz\" in the type \"element(t{urn:t}:c,#anonymous)\".");

    [TestMethod]
    [DataRow("/t:r/t:mi", 2389, "'value()' requires a singleton (or empty sequence), found operand of type 't{urn:t}:MyInt *'")]
    [DataRow("/t:r/t:m2", 2389, "'value()' requires a singleton (or empty sequence), found operand of type 't{urn:t}:My2 *'")]
    [DataRow("/t:r/t:an", 2389, "'value()' requires a singleton (or empty sequence), found operand of type '<anonymous> *'")]
    [DataRow("/t:r/t:sc", 2389, "'value()' requires a singleton (or empty sequence), found operand of type 'xs:decimal *'")]
    [DataRow("/t:r/t:sn", 2389, "'value()' requires a singleton (or empty sequence), found operand of type 't{urn:t}:MyInt *'")]
    [DataRow("/t:r/@ma", 2389, "'value()' requires a singleton (or empty sequence), found operand of type 't{urn:t}:MyInt *'")]
    [DataRow("/t:r/t:sc[. = \"a\"]", 2234, "The operator \"=\" cannot be applied to \"xs:decimal\" and \"xs:string\" operands.")]
    [DataRow("/t:r/t:mi[. = \"a\"]", 2234, "The operator \"=\" cannot be applied to \"t{urn:t}:MyInt\" and \"xs:string\" operands.")]
    [DataRow("/t:r/t:mi/text()", 9312, "'text()' is not supported on simple typed or 'http://www.w3.org/2001/XMLSchema#anyType' elements, found 'element(t{urn:t}:mi,t{urn:t}:MyInt) *'.")]
    [DataRow("/t:r/t:sc/text()", 9312, "'text()' is not supported on simple typed or 'http://www.w3.org/2001/XMLSchema#anyType' elements, found 'element(t{urn:t}:sc,#anonymous) *'.")]
    public void DerivedSimpleTypesAndSimpleContent_AreQuotedByTheirOwnNames(string path, int number, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create xml schema collection zd as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns="urn:t" elementFormDefault="qualified">
            <xs:simpleType name="MyInt"><xs:restriction base="xs:int"><xs:maxInclusive value="100"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="My2"><xs:restriction base="MyInt"><xs:minInclusive value="1"/></xs:restriction></xs:simpleType>
            <xs:element name="r"><xs:complexType><xs:sequence>
            <xs:element name="mi" type="MyInt" maxOccurs="3"/><xs:element name="m2" type="My2" maxOccurs="3"/>
            <xs:element name="an" maxOccurs="3"><xs:simpleType><xs:restriction base="xs:string"><xs:maxLength value="3"/></xs:restriction></xs:simpleType></xs:element>
            <xs:element name="sc" maxOccurs="3"><xs:complexType><xs:simpleContent><xs:extension base="xs:decimal"><xs:attribute name="u" type="xs:string"/></xs:extension></xs:simpleContent></xs:complexType></xs:element>
            <xs:element name="sn" maxOccurs="3"><xs:complexType><xs:simpleContent><xs:extension base="MyInt"/></xs:simpleContent></xs:complexType></xs:element>
            </xs:sequence><xs:attribute name="ma" type="MyInt"/></xs:complexType></xs:element></xs:schema>'
            """);
        simulation.AssertSqlError(
            $"declare @x xml(zd) = null; select @x.value('{Prolog}{path}', 'int')",
            number,
            "XQuery [value()]: " + message);
    }

    [TestMethod]
    public void DeclaredSteps_Read() =>
        AreEqual("1|9|s|2.50|ab|1", Typed().ExecuteScalar($"""
            declare @x xml(zsc) = N'<r xmlns="urn:t"><i>1</i><c><n>ab</n></c><h>s</h><sc>2.5</sc></r>';
            declare @d xml(zsc) = N'<xr xmlns="urn:t"><i>1</i><x2>9</x2></xr>';
            select concat_ws('|', @x.value('{Prolog}(/t:r/t:i)[1]', 'int'), @d.value('{Prolog}(/t:xr/t:x2)[1]', 'int'),
                @x.value('{Prolog}(/t:r/t:h)[1]', 'nvarchar(9)'), @x.value('{Prolog}(/t:r/t:sc)[1]', 'decimal(9,2)'),
                @x.value('{Prolog}string((/t:r/t:c)[1])', 'nvarchar(9)'), @x.exist('{Prolog}/t:r/t:c/t:n'))
            """));

    [TestMethod]
    [DataRow("skip", 0)]
    [DataRow("lax", 9314)]
    [DataRow("strict", 2263)]
    public void ContentWildcard_TypesWhatItAdmitsByItsProcessContents(string processContents, int number)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create xml schema collection w as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" elementFormDefault="qualified">
            <xs:element name="r"><xs:complexType><xs:sequence><xs:any namespace="urn:w" processContents="{processContents}" minOccurs="0"/></xs:sequence></xs:complexType></xs:element></xs:schema>'
            """);
        const string Query = "declare @x xml(w) = null; select @x.value('declare namespace t=\"urn:t\"; declare namespace w=\"urn:w\"; (/t:r/w:q)[1]', 'nvarchar(9)')";
        if (number == 0)
            AreEqual(DBNull.Value, simulation.ExecuteScalar(Query));
        else
            _ = simulation.AssertSqlError(Query, number);
    }

    [TestMethod]
    public void SubstitutionGroupMember_StandsInForItsHead()
    {
        var simulation = Typed();
        AreEqual("<r xmlns=\"urn:t\"><i>1</i><h>s</h></r>", simulation.ExecuteScalar("declare @x xml(zsc) = N'<r xmlns=\"urn:t\"><i>1</i><h>s</h></r>'; select cast(@x as nvarchar(max))"));
        simulation.AssertSqlError(
            "declare @x xml(zsc) = N'<r xmlns=\"urn:t\"><i>1</i><h>s</h><g>x</g></r>'",
            6965,
            "XML Validation: Invalid content. Expected element(s): '{urn:t}m','{urn:t}sc','{urn:t}e'. Found: element '{urn:t}g' instead. Location: /*:r[1]/*:g[1].");
    }

    [TestMethod]
    public void DocumentChildren_InterleaveTheGlobalElements()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create xml schema collection zs as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" elementFormDefault="qualified">
            <xs:element name="m3" type="xs:int"/><xs:element name="m1"><xs:complexType/></xs:element><xs:element name="m2" type="xs:string"/></xs:schema>'
            """);
        simulation.AssertSqlError(
            $"declare @x xml(zs) = null; select @x.value('{Prolog}(/*/t:zz)[1]', 'int')",
            2263,
            "XQuery [value()]: There is no element named \"{urn:t}:zz\" in the type \"(element(t{urn:t}:m3,xs:int) ? & element(t{urn:t}:m1,#anonymous) ? & element(t{urn:t}:m2,xs:string) ?) *\".");
    }

    [TestMethod]
    public void UntypedNamespacedSelfStep_QuotesTheNamespace() =>
        new Simulation().AssertSqlError(
            "declare @x xml = '<r/>'; select @x.value('declare namespace t=\"urn:t\"; /t:r/t:i/self::t:j', 'int')",
            2263,
            "XQuery [value()]: There is no element named \"{urn:t}:j\" in the type \"element(t{urn:t}:i,xdt:untyped) *\".");

    [TestMethod]
    [DataRow("select 1,\n @x\n.value(\n\n'/a'\n, 'int')", 6)]
    [DataRow("select @x.value('\n\n/a', 'int')", 4)]
    [DataRow("select @x.value('declare namespace z=\"u\";\n /a', 'int')", 3)]
    [DataRow("select @x.exist('/a\n/b\n[1 + \"x\"]')", 4)]
    [DataRow("select @x.value('(/a\n/b\n)[1] + \n\"x\"', 'int')", 2)]
    public void XQueryError_ReportsTheLineTheArgumentPlacesIt(string statement, int line) =>
        AreEqual(line, Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery("declare @x xml = '<a/>';\n" + statement)).Errors[0].LineNumber);
    private const string ListCollection = """
        create xml schema collection zl as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns="urn:t" elementFormDefault="qualified">
        <xs:simpleType name="L"><xs:list itemType="xs:int"/></xs:simpleType>
        <xs:element name="r"><xs:complexType><xs:sequence><xs:element name="l" type="L"/></xs:sequence></xs:complexType></xs:element></xs:schema>'
        """;

    [TestMethod]
    [DataRow("sum(/t:r/t:l)", "6")]
    [DataRow("count(/t:r/t:l)", "1")]
    [DataRow("count(data(/t:r/t:l))", "3")]
    [DataRow("avg(/t:r/t:l)", "2")]
    [DataRow("max(/t:r/t:l)", "3")]
    [DataRow("data((/t:r/t:l)[1])[3]", "3")]
    [DataRow("/t:r/t:l = 2", "true")]
    [DataRow("(data(/t:r/t:l))[1] instance of xs:int", "true")]
    [DataRow("distinct-values(data(/t:r/t:l))[2]", "2")]
    [DataRow("string((/t:r/t:l)[1])", "1 2 3")]
    public void ListTypedElement_AtomizesIntoItsItems(string path, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(ListCollection);
        AreEqual(expected, simulation.ExecuteScalar($"declare @x xml(zl) = N'<r xmlns=\"urn:t\"><l>1 2 3</l></r>'; select @x.value('{Prolog}{path}', 'nvarchar(20)')"));
    }

    [TestMethod]
    [DataRow("(/t:r/t:l)[1]", "'value()' requires a singleton (or empty sequence), found operand of type 'xs:int *'")]
    [DataRow("(/t:r/t:l)[1] + 1", "'+' requires a singleton (or empty sequence), found operand of type 'xs:int *'")]
    [DataRow("(/t:r/t:l)[1] eq 2", "'eq' requires a singleton (or empty sequence), found operand of type 'xs:int *'")]
    public void ListTypedElement_IsNoSingleton(string path, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(ListCollection);
        simulation.AssertSqlError($"declare @x xml(zl) = null; select @x.value('{Prolog}{path}', 'int')", 2389, "XQuery [value()]: " + message);
    }
}
