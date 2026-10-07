using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Typed <c>xml</c> past the column and variable writes: the
/// <c>CAST</c> / <c>CONVERT</c> target, the schema types an XQuery expression
/// over a typed value carries, <c>replace value of</c>'s type check, the
/// <c>xsi:nil</c> / <c>xsi:type</c> attributes, and
/// <c>ALTER XML SCHEMA COLLECTION … ADD</c>. Every expected value and message
/// was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlTypedSchemaTests
{
    private const string Schema = """
        create xml schema collection sc as '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="r"><xsd:complexType><xsd:sequence>
        <xsd:element name="d" type="xsd:decimal" minOccurs="0"/><xsd:element name="i" type="xsd:int" minOccurs="0"/>
        <xsd:element name="s" type="xsd:string" minOccurs="0" maxOccurs="unbounded"/><xsd:element name="b" type="xsd:boolean" minOccurs="0"/>
        <xsd:element name="dt" type="xsd:dateTime" minOccurs="0"/><xsd:element name="n" type="xsd:int" nillable="true" minOccurs="0"/>
        <xsd:element name="f" type="xsd:double" minOccurs="0"/></xsd:sequence><xsd:attribute name="a" type="xsd:int"/></xsd:complexType></xsd:element></xsd:schema>'
        """;

    private const string XsiNamespace = "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"";

    private static Simulation Typed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Schema);
        return simulation;
    }

    private static object? Scalar(string commandText) => Typed().ExecuteScalar(commandText);

    private static void Error(string commandText, int number, string message) => Typed().AssertSqlError(commandText, number, message);

    // ---- CAST / CONVERT ------------------------------------------------------

    [TestMethod]
    [DataRow("cast('<r><d>1.50</d></r>' as xml(sc))")]
    [DataRow("convert(xml(sc), '<r><d>1.50</d></r>')")]
    [DataRow("cast(cast('<r><d>1.50</d></r>' as xml) as xml(sc))")]
    [DataRow("cast('<r><d>1.50</d></r>' as xml(document sc))")]
    [DataRow("cast('<r><d>1.50</d></r>' as xml(content dbo.sc))")]
    [DataRow("convert(xml(sc), '<r><d>1.50</d></r>', 1)")]
    public void Cast_ValidatesAndCanonicalizes(string expression) =>
        AreEqual("<r><d>1.5</d></r>", Scalar($"select convert(nvarchar(max), {expression})"));

    [TestMethod]
    public void Cast_Refusals()
    {
        Error("select cast('<r><d>x</d></r>' as xml(sc))", 6926, "XML Validation: Invalid simple type value: 'x'. Location: /*:r[1]/*:d[1]");
        Error("select try_cast('<r><d>x</d></r>' as xml(sc))", 6926, "XML Validation: Invalid simple type value: 'x'. Location: /*:r[1]/*:d[1]");
        Error("select cast('<q/>' as xml(sc))", 6913, "XML Validation: Declaration not found for element 'q'. Location: /*:q[1]");
        Error("select cast('<r/><r/>' as xml(document sc))", 6901, "XML Validation: XML instance must be a document.");
        Error("select cast('<r/>' as xml(nope))", 6314, "Collection specified does not exist in metadata : 'nope'");
    }

    [TestMethod]
    public void Cast_ContentAdmitsSeveralRoots_AndNullOrEmptyPassThrough()
    {
        AreEqual("<r/><r/>", Scalar("select convert(nvarchar(max), cast('<r/><r/>' as xml(content sc)))"));
        _ = IsInstanceOfType<DBNull>(Scalar("select cast(null as xml(sc))"));
        AreEqual(string.Empty, Scalar("select convert(nvarchar(max), cast('' as xml(sc)))"));
    }

    [TestMethod]
    public void Cast_ResultIsTypedForItsMethods()
    {
        Error(
            "select cast('<r><d>1</d></r>' as xml(sc)).value('/r/d', 'decimal(9,2)')",
            2389,
            "XQuery [value()]: 'value()' requires a singleton (or empty sequence), found operand of type 'xs:decimal *'");
        AreEqual("2.5", Scalar("select cast('<r><d>1.50</d></r>' as xml(sc)).value('(/r/d)[1] + 1', 'varchar(20)')"));
    }

    // ---- static typing over a typed value --------------------------------------

    [TestMethod]
    public void TypedPath_ReportsItsSchemaType()
    {
        Error(
            "declare @x xml(sc) = '<r><s>a</s><s>b</s></r>'; select @x.value('/r/s', 'varchar(9)')",
            2389,
            "XQuery [value()]: 'value()' requires a singleton (or empty sequence), found operand of type 'xs:string *'");
        Error(
            "declare @x xml(sc) = '<r a=\"5\"/>'; select @x.query('data(/r/@a) + 1')",
            2389,
            "XQuery [query()]: '+' requires a singleton (or empty sequence), found operand of type 'xs:int *'");
        Error(
            "declare @x xml(sc) = '<r><d>1.5</d></r>'; select @x.query('(/r/d)[1] = \"1.5\"')",
            2234,
            "XQuery [query()]: The operator \"=\" cannot be applied to \"xs:decimal ?\" and \"xs:string\" operands.");
        Error(
            "declare @x xml(sc) = '<r><s>a</s></r>'; select @x.query('(/r/s)[1] + 1')",
            9308,
            "XQuery [query()]: The argument of '+' must be of a single numeric primitive type or 'http://www.w3.org/2004/07/xpath-datatypes#untypedAtomic'. Found argument of type 'xs:string ?'.");
        Error(
            "declare @x xml(sc) = '<r><d>2</d></r>'; select @x.query('/r/d/text()')",
            9312,
            "XQuery [query()]: 'text()' is not supported on simple typed or 'http://www.w3.org/2001/XMLSchema#anyType' elements, found 'element(d,xs:decimal) *'.");
    }

    [TestMethod]
    public void TypedArithmetic_ComputesInTheDeclaredType()
    {
        AreEqual("1234568", Scalar("declare @x xml(sc) = '<r><d>1234567</d></r>'; select convert(nvarchar(max), @x.query('(/r/d)[1] + 1'))"));
        AreEqual("0.666666", Scalar("declare @x xml(sc) = '<r><d>2</d></r>'; select convert(nvarchar(max), @x.query('(/r/d)[1] div 3'))"));
        AreEqual("1.234568E6", Scalar("declare @x xml(sc) = '<r><f>1234567</f></r>'; select convert(nvarchar(max), @x.query('(/r/f)[1] + 1'))"));
        AreEqual("true", Scalar("declare @x xml(sc) = '<r><d>1.5</d></r>'; select convert(nvarchar(max), @x.query('data((/r/d)[1]) instance of xs:decimal?'))"));
    }

    // ---- replace value of over a typed node -------------------------------------

    [TestMethod]
    [DataRow("(/r/d)[1] with \"abc\"", "<r><d>1</d></r>", "xs:string", "xs:decimal")]
    [DataRow("(/r/i)[1] with 1.5", "<r><i>1</i></r>", "xs:decimal", "xs:int")]
    [DataRow("(/r/@a)[1] with 7", "<r a=\"1\"/>", "xs:integer", "xs:int")]
    [DataRow("(/r/s)[1] with 5", "<r><s>a</s></r>", "xs:integer", "xs:string")]
    [DataRow("(/r/b)[1] with 1", "<r><b>true</b></r>", "xs:integer", "xs:boolean")]
    [DataRow("(/r/f)[1] with 1.5", "<r><f>1</f></r>", "xs:decimal", "xs:double")]
    [DataRow("(/r/d)[1] with (1, 2)", "<r><d>1</d></r>", "xs:integer +", "xs:decimal")]
    [DataRow("(/r/dt)[1] with \"2021-01-01T00:00:00Z\"", "<r><dt>2020-01-01T00:00:00Z</dt></r>", "xs:string", "xs:dateTime")]
    public void ReplaceValue_OfAnotherType_Raises2247(string replace, string instance, string valueType, string expected) =>
        Error(
            $"declare @x xml(sc) = '{instance}'; set @x.modify('replace value of {replace}')",
            2247,
            $"XQuery [modify()]: The value is of type \"{valueType}\", which is not a subtype of the expected type \"{expected}\".");

    [TestMethod]
    public void ReplaceValue_SqlVariableOfAStringType_Raises2247() =>
        Error(
            "declare @v nvarchar(9) = '2.5'; declare @x xml(sc) = '<r><d>1</d></r>'; set @x.modify('replace value of (/r/d)[1] with sql:variable(\"@v\")')",
            2247,
            "XQuery [modify()]: The value is of type \"xs:string\", which is not a subtype of the expected type \"xs:decimal\".");

    [TestMethod]
    [DataRow("<r><d>1</d></r>", "(/r/d)[1] with 2.5", "<r><d>2.5</d></r>")]
    [DataRow("<r><d>1.5</d></r>", "(/r/d)[1] with (/r/d)[1] + 1", "<r><d>2.5</d></r>")]
    [DataRow("<r><d>1234567</d></r>", "(/r/d)[1] with data((/r/d)[1]) + 1", "<r><d>1234568</d></r>")]
    [DataRow("<r><d>1.1234567890123456789</d></r>", "(/r/d)[1] with data((/r/d)[1]) * 3", "<r><d>3.37037</d></r>")]
    [DataRow("<r><d>1</d></r>", "(/r/d)[1] with xs:decimal(\"2.50\")", "<r><d>2.5</d></r>")]
    [DataRow("<r><b>true</b></r>", "(/r/b)[1] with false()", "<r><b>false</b></r>")]
    [DataRow("<r><dt>2020-01-01T00:00:00Z</dt></r>", "(/r/dt)[1] with xs:dateTime(\"2021-01-01T00:00:00Z\")", "<r><dt>2021-01-01T00:00:00Z</dt></r>")]
    public void ReplaceValue_OfTheDeclaredType_IsWritten(string instance, string replace, string expected) =>
        AreEqual(expected, Scalar($"declare @x xml(sc) = '{instance}'; set @x.modify('replace value of {replace}'); select convert(nvarchar(max), @x)"));

    [TestMethod]
    public void ReplaceValue_IntVariableIntoADecimal_IsWritten() =>
        AreEqual("<r><d>7</d></r>", Scalar("declare @v int = 7; declare @x xml(sc) = '<r><d>1</d></r>'; set @x.modify('replace value of (/r/d)[1] with sql:variable(\"@v\")'); select convert(nvarchar(max), @x)"));

    [TestMethod]
    public void ReplaceValue_ANillableElementWithTheEmptySequence_SetsNil() =>
        AreEqual(
            "<r><n xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/></r>",
            Scalar("declare @x xml(sc) = '<r><n>1</n></r>'; set @x.modify('replace value of (/r/n)[1] with ()'); select convert(nvarchar(max), @x)"));

    [TestMethod]
    public void TextStep_UnderATypedElement_Raises9312InModifyToo() =>
        Error(
            "declare @x xml(sc) = '<r><d>1</d></r>'; set @x.modify('replace value of (/r/d/text())[1] with \"1.5\"')",
            9312,
            "XQuery [modify()]: 'text()' is not supported on simple typed or 'http://www.w3.org/2001/XMLSchema#anyType' elements, found 'element(d,xs:decimal) *'.");

    // ---- xsi: attributes --------------------------------------------------------

    [TestMethod]
    public void XsiNil_OnANillableElement_IsKept_AndReadsAsNull()
    {
        var simulation = Typed();
        AreEqual(
            $"<r {XsiNamespace}><n xsi:nil=\"true\"/></r>",
            simulation.ExecuteScalar($"select convert(nvarchar(max), cast('<r {XsiNamespace}><n xsi:nil=\"true\"/></r>' as xml(sc)))"));
        _ = IsInstanceOfType<DBNull>(simulation.ExecuteScalar($"declare @x xml(sc) = '<r {XsiNamespace}><n xsi:nil=\"true\"/></r>'; select @x.value('(/r/n)[1]', 'int')"));
        AreEqual(
            $"<n {XsiNamespace} xsi:nil=\"true\"/>",
            simulation.ExecuteScalar($"declare @x xml(sc) = '<r {XsiNamespace}><n xsi:nil=\"true\"/></r>'; select convert(nvarchar(max), @x.query('/r/n'))"));
    }

    [TestMethod]
    public void XsiNil_Refusals()
    {
        Error(
            $"select cast('<r {XsiNamespace}><d xsi:nil=\"true\"/></r>' as xml(sc))",
            6917,
            "XML Validation: Element 'd' may not have xsi:nil=\"true\" because it was not defined as nillable or because it has a fixed value constraint. Location: /*:r[1]/*:d[1]");
        Error(
            $"select cast('<r {XsiNamespace}><n xsi:nil=\"true\">5</n></r>' as xml(sc))",
            6918,
            "XML Validation: Element 'n' must not have character or element children, because xsi:nil was set to true. Location: /*:r[1]/*:n[1]");
    }

    [TestMethod]
    public void XsiType_NamesADerivedTypeToValidateAgainst()
    {
        const string declarations = $"{XsiNamespace} xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"";
        AreEqual(
            $"<r {declarations}><d xsi:type=\"xs:int\">5</d></r>",
            Scalar($"select convert(nvarchar(max), cast('<r {declarations}><d xsi:type=\"xs:int\">5</d></r>' as xml(sc)))"));
        Error(
            $"select cast('<r {declarations}><d xsi:type=\"xs:int\">5.5</d></r>' as xml(sc))",
            6926,
            "XML Validation: Invalid simple type value: '5.5'. Location: /*:r[1]/*:d[1]");
        Error(
            $"select cast('<r {declarations}><d xsi:type=\"xs:string\">5</d></r>' as xml(sc))",
            6936,
            "XML Validation: Invalid cast for element 'd' from type '{http://www.w3.org/2001/XMLSchema}decimal' to type '{http://www.w3.org/2001/XMLSchema}string'. Location: /*:r[1]/*:d[1]");
        Error(
            $"select cast('<r {declarations}><d xsi:type=\"xs:nope\">5</d></r>' as xml(sc))",
            6914,
            "XML Validation: Type definition for type '{http://www.w3.org/2001/XMLSchema}nope' was not found, type definition is required before use in a type cast. Location: /*:r[1]/*:d[1]");
    }

    [TestMethod]
    public void OtherXsiAttributes_AreAccepted() =>
        AreEqual(
            $"<r {XsiNamespace} xsi:schemaLocation=\"urn:x a.xsd\" xsi:bogus=\"1\"/>",
            Scalar($"select convert(nvarchar(max), cast('<r {XsiNamespace} xsi:schemaLocation=\"urn:x a.xsd\" xsi:bogus=\"1\"/>' as xml(sc)))"));

    // ---- the collection itself ----------------------------------------------------

    [TestMethod]
    [DataRow("unique")]
    [DataRow("key")]
    public void IdentityConstraints_AreRefusedAtCreate(string construct) =>
        new Simulation().AssertSqlError(
            $"""
            create xml schema collection kc as '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="r"><xsd:complexType><xsd:sequence>
            <xsd:element name="i" maxOccurs="unbounded"><xsd:complexType><xsd:attribute name="id" type="xsd:int"/></xsd:complexType></xsd:element>
            </xsd:sequence></xsd:complexType><xsd:{construct} name="u"><xsd:selector xpath="i"/><xsd:field xpath="@id"/></xsd:{construct}></xsd:element></xsd:schema>'
            """,
            9336,
            $"The XML Schema syntax '{construct}' is not supported.");

    [TestMethod]
    public void AlterAdd_AddsComponents()
    {
        var simulation = Typed();
        _ = simulation.ExecuteNonQuery("""
            alter xml schema collection sc add '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t"><xsd:element name="t" type="xsd:int"/></xsd:schema>';
            declare @s nvarchar(max) = '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="z" type="xsd:int"/></xsd:schema>';
            alter xml schema collection dbo.sc add @s;
            alter xml schema collection sc add N''
            """);
        AreEqual("<t xmlns=\"urn:t\">5</t>", simulation.ExecuteScalar("select convert(nvarchar(max), cast('<t xmlns=\"urn:t\"> 05 </t>' as xml(sc)))"));
        AreEqual("<z>5</z>", simulation.ExecuteScalar("select convert(nvarchar(max), cast('<z>05</z>' as xml(sc)))"));
        AreEqual("<r><d>1.5</d></r>", simulation.ExecuteScalar("select convert(nvarchar(max), cast('<r><d>1.50</d></r>' as xml(sc)))"));
    }

    [TestMethod]
    public void AlterAdd_RollsBackWithTheTransaction() =>
        Error(
            """
            begin tran;
            alter xml schema collection sc add '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="z" type="xsd:int"/></xsd:schema>';
            rollback;
            select cast('<z>1</z>' as xml(sc))
            """,
            6913,
            "XML Validation: Declaration not found for element 'z'. Location: /*:z[1]");

    [TestMethod]
    public void AlterAdd_Refusals()
    {
        Error(
            "alter xml schema collection sc add '<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"r\" type=\"xsd:int\"/></xsd:schema>'",
            6310,
            "Altering existing schema components is not allowed.  There was an attempt to modify an existing XML Schema component, component namespace: '' component name: 'r' component kind:ELEMENT");
        Error("alter xml schema collection sc add 'garbage'", 2378, "Expected XML schema document");
        Error(
            "alter xml schema collection nope add '<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"/>'",
            6347,
            "Specified collection 'nope' cannot be altered because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void AlterAdd_AComponentOfAnotherKind_IsNotARedeclaration() =>
        AreEqual(1, Scalar("""
            alter xml schema collection sc add '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:complexType name="r"><xsd:sequence/></xsd:complexType></xsd:schema>';
            select 1
            """));

    private const string AddQ = """alter xml schema collection sc add '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="q" type="xsd:string"/></xsd:schema>'""";

    private const string Msg6323 = "The xml schema collection for variable '@x' has been altered while the batch was being executed. Remove all XML schema collection DDL operations it is dependent on from the batch, and re-run the batch.";

    /// <summary>
    /// A variable typed by a collection the batch altered refuses every
    /// assignment, whether it was declared before the alteration or after,
    /// and the batch's own EXEC altering it counts (probed 2026-10-07 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow($"{AddQ}; declare @x xml(sc) = '<q/>'")]
    [DataRow($"declare @x xml(sc); {AddQ}; set @x = '<r/>'")]
    [DataRow($"{AddQ}; declare @x xml(sc); select @x = '<r/>'")]
    [DataRow($"{AddQ}; declare @x xml(sc); set @x = null")]
    [DataRow("""exec('alter xml schema collection sc add ''<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="q" type="xsd:string"/></xsd:schema>'''); declare @x xml(sc); set @x = '<r/>'""")]
    public void AlterAdd_ThenAssigningAVariableItTypes_RaisesMsg6323(string batch) =>
        Error(batch, 6323, Msg6323);

    [TestMethod]
    public void Msg6323_RollsTheTransactionBack_PastATry()
    {
        using var connection = Typed().CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand($"""
            begin tran;
            {AddQ};
            declare @x xml(sc);
            begin try set @x = '<r/>' end try begin catch print 'caught' end catch
            print 'after'
            """).ExecuteNonQuery());
        AreEqual(6323, ex.Number);
        AreEqual(1, ex.Errors.Count);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    [DataRow($"{AddQ}; declare @x xml(sc)")]
    [DataRow($"declare @x xml(sc) = '<r/>'; {AddQ}")]
    [DataRow($"{AddQ}; declare @x xml; set @x = '<r/>'")]
    [DataRow($"{AddQ}; declare @t table (x xml(sc)); insert @t values ('<q/>')")]
    [DataRow($"{AddQ}; select cast('<q/>' as xml(sc))")]
    [DataRow($"if 1 = 0 {AddQ}; declare @x xml(sc); set @x = '<r/>'")]
    public void AlterAdd_NeighborsOfMsg6323_Run(string batch) =>
        _ = Typed().ExecuteNonQuery(batch);

    [TestMethod]
    public void AlterAdd_ALaterBatch_AssignsFreely()
    {
        var simulation = Typed();
        _ = simulation.ExecuteNonQuery(AddQ);
        AreEqual("<q>1</q>", simulation.ExecuteScalar("declare @x xml(sc) = '<q>1</q>'; select convert(nvarchar(max), @x)"));
    }
}
