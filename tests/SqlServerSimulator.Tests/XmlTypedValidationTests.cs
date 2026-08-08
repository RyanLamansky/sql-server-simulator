using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Writing to an <c>xml(&lt;collection&gt;)</c> target validates the instance
/// against the collection and stores it in <b>canonical form</b> — the two
/// halves of what real SQL Server does on every typed write. Every expected
/// string and message here was probed against SQL Server 2025 on 2026-08-08.
/// </summary>
[TestClass]
public sealed class XmlTypedValidationTests
{
    /// <summary>A collection declaring one element per XSD primitive the canonicalizer renders.</summary>
    private const string PrimitiveCollection = """
        create xml schema collection xsn as N'
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:simpleType name="decList"><xs:list itemType="xs:decimal"/></xs:simpleType>
          <xs:simpleType name="decOrBool"><xs:union memberTypes="xs:decimal xs:boolean"/></xs:simpleType>
          <xs:element name="v">
            <xs:complexType><xs:sequence>
              <xs:element name="dec" type="xs:decimal" minOccurs="0"/>
              <xs:element name="int" type="xs:integer" minOccurs="0"/>
              <xs:element name="bool" type="xs:boolean" minOccurs="0"/>
              <xs:element name="dbl" type="xs:double" minOccurs="0"/>
              <xs:element name="flt" type="xs:float" minOccurs="0"/>
              <xs:element name="str" type="xs:string" minOccurs="0"/>
              <xs:element name="nstr" type="xs:normalizedString" minOccurs="0"/>
              <xs:element name="tok" type="xs:token" minOccurs="0"/>
              <xs:element name="uri" type="xs:anyURI" minOccurs="0"/>
              <xs:element name="dt" type="xs:dateTime" minOccurs="0"/>
              <xs:element name="dat" type="xs:date" minOccurs="0"/>
              <xs:element name="tm" type="xs:time" minOccurs="0"/>
              <xs:element name="dur" type="xs:duration" minOccurs="0"/>
              <xs:element name="hex" type="xs:hexBinary" minOccurs="0"/>
              <xs:element name="b64" type="xs:base64Binary" minOccurs="0"/>
              <xs:element name="lst" type="decList" minOccurs="0"/>
              <xs:element name="uni" type="decOrBool" minOccurs="0"/>
            </xs:sequence>
            <xs:attribute name="adec" type="xs:decimal"/>
          </xs:complexType>
          </xs:element>
        </xs:schema>'
        """;

    /// <summary>A simulation holding <c>dbo.tn(id int, note nvarchar(20), body xml(xsn))</c>.</summary>
    private static Simulation Primitives()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(PrimitiveCollection);
        _ = sim.ExecuteNonQuery("create table dbo.tn (id int, note nvarchar(20), body xml(xsn))");
        return sim;
    }

    /// <summary>Stores <paramref name="instance"/> and reads the canonical text back.</summary>
    private static object? Stored(string instance)
    {
        var sim = Primitives();
        _ = sim.ExecuteNonQuery($"insert dbo.tn values (1, N'x', N'{instance}')");
        return sim.ExecuteScalar("select cast(body as nvarchar(max)) from dbo.tn");
    }

    /// <summary>
    /// The exact-numeric family sheds a leading sign, leading zeros and
    /// trailing fractional zeros, and a zero value loses its sign.
    /// </summary>
    [TestMethod]
    public void ExactNumerics_CanonicalizeToTheirShortestForm()
    {
        AreEqual("<v><dec>1</dec></v>", Stored("<v><dec>1.00</dec></v>"));
        AreEqual("<v><dec>0.5</dec></v>", Stored("<v><dec>.5</dec></v>"));
        AreEqual("<v><dec>5</dec></v>", Stored("<v><dec>+5.</dec></v>"));
        AreEqual("<v><dec>0</dec></v>", Stored("<v><dec>-0.0</dec></v>"));
        AreEqual("<v><int>7</int></v>", Stored("<v><int>+007</int></v>"));
        AreEqual("<v adec=\"2.5\"/>", Stored("<v adec=\"2.50\"/>"));
    }

    /// <summary>
    /// The approximate pair follows XQuery's <c>fn:string</c> rule rather than
    /// XSD's own canonical form: plain inside <c>[1e-6, 1e6)</c>, scientific
    /// outside it with a mantissa carrying at least one fractional digit, and
    /// zero always scientific.
    /// </summary>
    [TestMethod]
    public void ApproximateNumerics_UsePlainNotationOnlyInsideTheirWindow()
    {
        AreEqual("<v><dbl>100</dbl></v>", Stored("<v><dbl>1.0E2</dbl></v>"));
        AreEqual("<v><dbl>0.5</dbl></v>", Stored("<v><dbl>0.50</dbl></v>"));
        AreEqual("<v><dbl>999999</dbl></v>", Stored("<v><dbl>999999</dbl></v>"));
        AreEqual("<v><dbl>1.0E6</dbl></v>", Stored("<v><dbl>1000000</dbl></v>"));
        AreEqual("<v><dbl>0.000001</dbl></v>", Stored("<v><dbl>1e-6</dbl></v>"));
        AreEqual("<v><dbl>5.0E-7</dbl></v>", Stored("<v><dbl>0.0000005</dbl></v>"));
        AreEqual("<v><dbl>0.0E0</dbl></v>", Stored("<v><dbl>0</dbl></v>"));
        AreEqual("<v><dbl>-0.0E0</dbl></v>", Stored("<v><dbl>-0</dbl></v>"));
        AreEqual("<v><dbl>INF</dbl></v>", Stored("<v><dbl>INF</dbl></v>"));
    }

    /// <summary>
    /// The significant-digit cap is SQL Server's own — fifteen for a
    /// <c>double</c> and seven for a <c>float</c>, not a shortest round trip.
    /// </summary>
    [TestMethod]
    public void ApproximateNumerics_CapAtFifteenAndSevenSignificantDigits()
    {
        AreEqual("<v><dbl>1.23456789012346E18</dbl></v>", Stored("<v><dbl>1234567890123456789</dbl></v>"));
        AreEqual("<v><flt>1.234568E18</flt></v>", Stored("<v><flt>1234567890123456789</flt></v>"));
    }

    /// <summary>
    /// The whiteSpace facet decides how much of the written text survives:
    /// <c>xs:string</c> keeps all of it, <c>xs:normalizedString</c> maps tabs
    /// to spaces without collapsing runs, and everything else collapses.
    /// </summary>
    [TestMethod]
    public void WhitespaceFacet_DecidesHowMuchTextSurvives()
    {
        AreEqual("<v><str>  a  b  </str></v>", Stored("<v><str>  a  b  </str></v>"));
        AreEqual("<v><nstr>  a   b  </nstr></v>", Stored("<v><nstr>  a &#x9; b  </nstr></v>"));
        AreEqual("<v><tok>a b</tok></v>", Stored("<v><tok>  a  b  </tok></v>"));
        AreEqual("<v><uri>a b</uri></v>", Stored("<v><uri>  a b  </uri></v>"));
    }

    /// <summary>
    /// The date/time family sheds trailing fractional-second zeros and writes
    /// either spelling of a zero offset as <c>Z</c>, while a real offset stays
    /// as written; <c>24:00:00</c> rolls the date, or the clock alone for
    /// <c>xs:time</c>.
    /// </summary>
    [TestMethod]
    public void DateTimeFamily_TrimsFractionsAndNormalizesTheZone()
    {
        AreEqual("<v><dt>2020-01-02T03:04:05+02:00</dt></v>", Stored("<v><dt>2020-01-02T03:04:05.000+02:00</dt></v>"));
        AreEqual("<v><dt>2020-01-02T03:04:05Z</dt></v>", Stored("<v><dt>2020-01-02T03:04:05+00:00</dt></v>"));
        AreEqual("<v><dt>2020-01-02T03:04:05Z</dt></v>", Stored("<v><dt>2020-01-02T03:04:05-00:00</dt></v>"));
        AreEqual("<v><dt>2020-01-03T00:00:00</dt></v>", Stored("<v><dt>2020-01-02T24:00:00</dt></v>"));
        AreEqual("<v><dat>2020-01-02Z</dat></v>", Stored("<v><dat>2020-01-02-00:00</dat></v>"));
        AreEqual("<v><tm>03:04:05.12</tm></v>", Stored("<v><tm>03:04:05.1200</tm></v>"));
        AreEqual("<v><tm>00:00:00</tm></v>", Stored("<v><tm>24:00:00</tm></v>"));
    }

    /// <summary>
    /// The remaining primitives: a boolean spells itself out, hex binary
    /// uppercases, base64 sheds its whitespace, a duration drops every zero
    /// field (the all-zero one writing <c>PT0S</c>), a list canonicalizes each
    /// item, and a union renders under the first member type that accepts the
    /// value — which is why <c>1</c> under <c>decimal | boolean</c> stays
    /// <c>1</c> rather than becoming <c>true</c>.
    /// </summary>
    [TestMethod]
    public void RemainingPrimitives_TakeTheirOwnCanonicalForm()
    {
        AreEqual("<v><bool>true</bool></v>", Stored("<v><bool>1</bool></v>"));
        AreEqual("<v><bool>false</bool></v>", Stored("<v><bool>0</bool></v>"));
        AreEqual("<v><hex>ABCD</hex></v>", Stored("<v><hex>ABcd</hex></v>"));
        AreEqual("<v><b64>YWJj</b64></v>", Stored("<v><b64>YW Jj</b64></v>"));
        AreEqual("<v><dur>P1Y2M3DT4H5M6S</dur></v>", Stored("<v><dur>P1Y2M3DT4H5M6.000S</dur></v>"));
        AreEqual("<v><dur>PT0S</dur></v>", Stored("<v><dur>P0Y</dur></v>"));
        AreEqual("<v><lst>1.5 2</lst></v>", Stored("<v><lst>  1.50   2.00  </lst></v>"));
        AreEqual("<v><uni>2.5</uni></v>", Stored("<v><uni>2.50</uni></v>"));
        AreEqual("<v><uni>1</uni></v>", Stored("<v><uni>1</uni></v>"));
    }

    /// <summary>
    /// The canonical value is what the row <em>holds</em>, so a trigger's
    /// <c>INSERTED</c> and an <c>OUTPUT … INTO</c> projection both read it —
    /// normalization happens on the way in, not on the way out.
    /// </summary>
    [TestMethod]
    public void TheCanonicalValueIsWhatEveryReaderSees()
    {
        var sim = Primitives();
        _ = sim.ExecuteNonQuery("create table dbo.seen (b nvarchar(200))");
        _ = sim.ExecuteNonQuery(
            "insert dbo.tn output cast(inserted.body as nvarchar(200)) into dbo.seen values (1, N'x', N'<v><dec>1.500</dec></v>')");
        AreEqual("<v><dec>1.5</dec></v>", sim.ExecuteScalar("select b from dbo.seen"));
    }

    /// <summary>
    /// A variable declared <c>xml(&lt;collection&gt;)</c> takes the same
    /// treatment as a column, on its initializer and on a later <c>SET</c>.
    /// </summary>
    [TestMethod]
    public void ATypedVariableNormalizesOnDeclarationAndOnAssignment()
    {
        var sim = Primitives();
        AreEqual(
            "<v><dec>1.5</dec></v>",
            sim.ExecuteScalar("declare @x xml(xsn) = N'<v><dec>1.500</dec></v>'; select cast(@x as nvarchar(200))"));
        AreEqual(
            "<v><dec>2.5</dec></v>",
            sim.ExecuteScalar("declare @x xml(xsn); set @x = N'<v><dec>2.500</dec></v>'; select cast(@x as nvarchar(200))"));
    }

    /// <summary>
    /// Only a value the statement actually assigns is validated: an UPDATE that
    /// never names the xml column neither re-reads its schema nor re-checks
    /// what is already stored.
    /// </summary>
    [TestMethod]
    public void AnUntouchedColumnIsNotRevalidated()
    {
        var sim = Primitives();
        _ = sim.ExecuteNonQuery("insert dbo.tn values (1, N'x', N'<v><dec>1.500</dec></v>')");
        _ = sim.ExecuteNonQuery("update dbo.tn set note = N'y'");
        AreEqual("<v><dec>1.5</dec></v>", sim.ExecuteScalar("select cast(body as nvarchar(max)) from dbo.tn"));
    }

    /// <summary>An untyped <c>xml</c> column keeps whatever text it was given.</summary>
    [TestMethod]
    public void AnUntypedColumnIsLeftAlone()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.u (body xml)");
        _ = sim.ExecuteNonQuery("insert dbo.u values (N'<v><dec>1.500</dec></v>')");
        AreEqual("<v><dec>1.500</dec></v>", sim.ExecuteScalar("select cast(body as nvarchar(max)) from dbo.u"));
    }

    /// <summary>A collection declaring a namespace, a required attribute and a bounded repeat.</summary>
    private static Simulation Validated()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create xml schema collection xv as N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns:t="urn:t" elementFormDefault="qualified">
              <xs:element name="r">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="a" type="xs:int"/>
                    <xs:element name="b" type="xs:string" minOccurs="0" maxOccurs="2"/>
                  </xs:sequence>
                  <xs:attribute name="k" type="xs:int" use="required"/>
                </xs:complexType>
              </xs:element>
              <xs:element name="s" type="xs:int"/>
            </xs:schema>'
            """);
        _ = sim.ExecuteNonQuery("create table dbo.tv (body xml(xv))");
        return sim;
    }

    private static void Rejects(string instance, int number, string message) =>
        Validated().AssertSqlError($"insert dbo.tv values (N'{instance}')", number, message);

    /// <summary>Each way a value can fail its declared type reports Msg 6926 at the offending node.</summary>
    [TestMethod]
    public void AnInvalidSimpleValue_Msg6926()
    {
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\"><t:a>zz</t:a></t:r>",
            6926,
            "XML Validation: Invalid simple type value: 'zz'. Location: /*:r[1]/*:a[1]");
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"zz\"><t:a>1</t:a></t:r>",
            6926,
            "XML Validation: Invalid simple type value: 'zz'. Location: /*:r[1]/@*:k");
        Rejects(
            "<t:s xmlns:t=\"urn:t\">99999999999</t:s>",
            6926,
            "XML Validation: Invalid simple type value: '99999999999'. Location: /*:s[1]");
    }

    /// <summary>
    /// Real splits the ways a content model can be broken: an element it didn't
    /// want here is Msg 6965 naming both sides, one it wanted but never got is
    /// Msg 6908 naming the parent, and one occurrence too many is Msg 6923.
    /// </summary>
    [TestMethod]
    public void ABrokenContentModel_Msg6965Or6908Or6923()
    {
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\"><t:a>1</t:a><t:zz>1</t:zz></t:r>",
            6965,
            "XML Validation: Invalid content. Expected element(s): '{urn:t}b'. Found: element '{urn:t}zz' instead. Location: /*:r[1]/*:zz[1].");
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\"><t:b>x</t:b><t:a>1</t:a></t:r>",
            6965,
            "XML Validation: Invalid content. Expected element(s): '{urn:t}a'. Found: element '{urn:t}b' instead. Location: /*:r[1]/*:b[1].");
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\"/>",
            6908,
            "XML Validation: Invalid content. Expected element(s): '{urn:t}a'. Location: /*:r[1]");
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\"><t:a>1</t:a><t:b>x</t:b><t:b>y</t:b><t:b>z</t:b></t:r>",
            6923,
            "XML Validation: Unexpected element(s): {urn:t}b. Location: /*:r[1]/*:b[3]");
    }

    /// <summary>
    /// The split between the two leftover-child errors is whether the model
    /// could still have taken <em>anything</em> here, not whether it already
    /// matched that name: against <c>dec?</c>, a stray beside an unused
    /// <c>dec</c> is Msg 6965 naming it, while one past a consumed <c>dec</c>
    /// leaves the model with nothing to offer and is Msg 6923.
    /// </summary>
    [TestMethod]
    public void ALeftoverChild_SplitsOnWhetherTheModelCouldStillTakeOne()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create xml schema collection xt as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">"
            + "<xs:element name=\"v\"><xs:complexType><xs:sequence>"
            + "<xs:element name=\"dec\" type=\"xs:decimal\" minOccurs=\"0\"/>"
            + "</xs:sequence></xs:complexType></xs:element></xs:schema>'");
        _ = sim.ExecuteNonQuery("create table dbo.tt (b xml(xt))");
        sim.AssertSqlError(
            "insert dbo.tt values (N'<v><nope/></v>')",
            6965,
            "XML Validation: Invalid content. Expected element(s): 'dec'. Found: element 'nope' instead. Location: /*:v[1]/*:nope[1].");
        sim.AssertSqlError(
            "insert dbo.tt values (N'<v><dec>1</dec><nope/></v>')",
            6923,
            "XML Validation: Unexpected element(s): nope. Location: /*:v[1]/*:nope[1]");
    }

    /// <summary>An attribute the type doesn't declare is Msg 6905; a required one left out is Msg 6906.</summary>
    [TestMethod]
    public void AnAttributeProblem_Msg6905Or6906()
    {
        Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\" q=\"2\"><t:a>1</t:a></t:r>",
            6905,
            "XML Validation: Attribute 'q' is not permitted in this context. Location: /*:r[1]/@*:q");
        Rejects(
            "<t:r xmlns:t=\"urn:t\"><t:a>1</t:a></t:r>",
            6906,
            "XML Validation: Required attribute 'k' is missing. Location: /*:r[1]");
    }

    /// <summary>
    /// A root the collection declares nowhere is Msg 6913 — including one
    /// written in no namespace against a schema that qualifies everything,
    /// which real refuses rather than skipping.
    /// </summary>
    [TestMethod]
    public void AnUndeclaredRoot_Msg6913()
    {
        Rejects(
            "<t:zzz xmlns:t=\"urn:t\"/>",
            6913,
            "XML Validation: Declaration not found for element '{urn:t}zzz'. Location: /*:zzz[1]");
        Rejects("<r k=\"1\"><a>1</a></r>", 6913, "XML Validation: Declaration not found for element 'r'. Location: /*:r[1]");
    }

    /// <summary>Character data inside an element-only type is Msg 6909, named against that element.</summary>
    [TestMethod]
    public void TextInsideElementOnlyContent_Msg6909()
        => Rejects(
            "<t:r xmlns:t=\"urn:t\" k=\"1\">text<t:a>1</t:a></t:r>",
            6909,
            "XML Validation: Text node is not allowed at this location, the type was defined with element only content or with simple content. Location: /*:r[1]");

    /// <summary>
    /// The value model stays CONTENT-typed under validation: several top-level
    /// elements are as legal here as they are for untyped <c>xml</c>, provided
    /// the collection declares each of them.
    /// </summary>
    [TestMethod]
    public void SeveralTopLevelElementsStayLegal()
    {
        var sim = Validated();
        _ = sim.ExecuteNonQuery(
            "insert dbo.tv values (N'<t:s xmlns:t=\"urn:t\">1</t:s><t:s xmlns:t=\"urn:t\">02</t:s>')");
        AreEqual(
            "<t:s xmlns:t=\"urn:t\">1</t:s><t:s xmlns:t=\"urn:t\">2</t:s>",
            sim.ExecuteScalar("select cast(body as nvarchar(max)) from dbo.tv"));
    }

    /// <summary>
    /// An <c>xsd:any</c> admits only the namespaces it names, and a child it
    /// admits is still typed against its own global declaration — the shape
    /// AdventureWorks' <c>AdditionalContactInfo</c> is built from, where
    /// <c>ContactRecord</c> lives in another namespace and declares an
    /// attribute of its own.
    /// </summary>
    [TestMethod]
    public void AWildcardChildIsTypedByItsGlobalDeclaration()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create xml schema collection xw as N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:outer" elementFormDefault="qualified">
              <xs:element name="outer">
                <xs:complexType mixed="true"><xs:complexContent mixed="true"><xs:restriction base="xs:anyType">
                  <xs:sequence><xs:any namespace="urn:inner" minOccurs="0" maxOccurs="unbounded"/></xs:sequence>
                </xs:restriction></xs:complexContent></xs:complexType>
              </xs:element>
            </xs:schema>
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:inner" elementFormDefault="qualified">
              <xs:element name="rec">
                <xs:complexType><xs:attribute name="when" type="xs:date"/></xs:complexType>
              </xs:element>
            </xs:schema>'
            """);
        _ = sim.ExecuteNonQuery("create table dbo.tw (body xml(xw))");

        // The wildcard admits it, and its own declaration types the attribute.
        _ = sim.ExecuteNonQuery(
            "insert dbo.tw values (N'<o:outer xmlns:o=\"urn:outer\" xmlns:i=\"urn:inner\">text<i:rec when=\"2020-01-02-00:00\"/></o:outer>')");
        AreEqual(
            "<o:outer xmlns:o=\"urn:outer\" xmlns:i=\"urn:inner\">text<i:rec when=\"2020-01-02Z\"/></o:outer>",
            sim.ExecuteScalar("select cast(body as nvarchar(max)) from dbo.tw"));

        // A namespace the wildcard doesn't name isn't admitted, and the
        // wildcard writes itself into the expected list as `{uri}*`.
        sim.AssertSqlError(
            "insert dbo.tw values (N'<o:outer xmlns:o=\"urn:outer\" xmlns:z=\"urn:zz\"><z:rec/></o:outer>')",
            6965,
            "XML Validation: Invalid content. Expected element(s): '{urn:inner}*'. Found: element '{urn:zz}rec' instead. Location: /*:outer[1]/*:rec[1].");

        // The wildcard's default processing is strict, so a name it admits but
        // nothing declares is Msg 6913 — the same error an undeclared root takes.
        sim.AssertSqlError(
            "insert dbo.tw values (N'<o:outer xmlns:o=\"urn:outer\" xmlns:i=\"urn:inner\"><i:nope/></o:outer>')",
            6913,
            "XML Validation: Declaration not found for element '{urn:inner}nope'. Location: /*:outer[1]/*:nope[1]");
    }
}
