using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What an <c>xml(…)</c> type binds to and how the binding is checked: the
/// <c>DOCUMENT</c> facet on columns, variables and procedure parameters, the
/// collection resolving while the batch compiles, the argument shapes the XML
/// methods take, and the compile errors a schema collection's text raises.
/// Every expected value was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlCollectionBindingTests
{
    private const string Collection = """
        create xml schema collection c as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="xs:int"/></xs:schema>'
        """;

    private static Simulation Typed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Collection);
        return simulation;
    }

    // ---- DOCUMENT ------------------------------------------------------------

    [TestMethod]
    [DataRow("create table t (x xml(document c)); insert t values ('<a>1</a><a>2</a>')", (byte)1)]
    [DataRow("create table t (x xml(document dbo.c)); insert t values (null), ('<a>3</a>'); update t set x = '<a>1</a><a>2</a>'", (byte)1)]
    [DataRow("declare @x xml(document c) = '<a>1</a><a>2</a>'", (byte)1)]
    [DataRow("declare @x xml(document c) = '<a>1</a>'; set @x = ''", (byte)2)]
    [DataRow("declare @t table (x xml(document c)); insert @t values ('<a>1</a><a>1</a>')", (byte)1)]
    [DataRow("select cast('<!--c-->' as xml(document c))", (byte)2)]
    public void Document_RefusesAFragment(string batch, byte state) =>
        AreEqual(state, Typed().AssertSqlError(batch, 6901).State);

    [TestMethod]
    public void Document_AdmitsOneRootBesideCommentsAndPis() =>
        AreEqual("<a>1</a><!--c-->", Typed().ExecuteScalar("declare @x xml(document c) = '<a>1</a><!--c-->'; select convert(nvarchar(max), @x)"));

    [TestMethod]
    public void Document_IsReportedInTheCatalog()
    {
        var simulation = Typed();
        _ = simulation.ExecuteNonQuery("create table t (d xml(document c), k xml(content c), u xml)");
        using var reader = simulation.ExecuteReader("select name, is_xml_document from sys.columns where object_id = object_id('t') order by column_id");
        foreach (var (name, document) in new[] { ("d", true), ("k", false), ("u", false) })
        {
            IsTrue(reader.Read());
            AreEqual(name, reader.GetString(0));
            AreEqual(document, reader.GetBoolean(1));
        }
    }

    [TestMethod]
    public void ProcedureParameter_TakesTheTypedForm()
    {
        var simulation = Typed();
        _ = simulation.ExecuteNonQuery("create procedure p @x xml(document c) as select convert(nvarchar(max), @x)");
        AreEqual("<a>1</a>", simulation.ExecuteScalar("exec p '<a>01</a>'"));
        var refused = simulation.AssertSqlError("exec p '<a>1</a><a>2</a>'", 6901);
        AreEqual(0, refused.LineNumber);
        AreEqual("p", refused.Procedure);
        _ = simulation.AssertSqlError("exec p '<b/>'", 6913);
        using var reader = simulation.ExecuteReader("select is_xml_document, xml_collection_id - (select xml_collection_id from sys.xml_schema_collections where name = 'c') from sys.parameters where object_id = object_id('p')");
        IsTrue(reader.Read());
        IsTrue(reader.GetBoolean(0));
        AreEqual(0, reader.GetInt32(1));
    }

    // ---- the collection resolves while the batch compiles --------------------

    [TestMethod]
    [DataRow("declare @x xml(nosuch) = '<a/>'", "nosuch")]
    [DataRow("declare @x xml(dbo.nosuch)", "dbo.nosuch")]
    [DataRow("declare @x xml(content nosch.nosuch)", "nosch.nosuch")]
    [DataRow("declare @t table (x xml(nosuch))", "nosuch")]
    [DataRow("create table t (x xml(nosuch))", "nosuch")]
    [DataRow("create table t (a int); alter table t add x xml(nosuch)", "nosuch")]
    [DataRow("create procedure p @x xml(nosuch) as select 1", "nosuch")]
    public void AMissingCollection_IsMsg6314(string batch, string written) =>
        new Simulation().AssertSqlError(batch, 6314, $"Collection specified does not exist in metadata : '{written}'");

    [TestMethod]
    [DataRow("select cast('<a>1</a>' as xml(c2))")]
    [DataRow("declare @x xml(c2) = '<a>1</a>'")]
    [DataRow("create table t (x xml(c2))")]
    public void ACollectionTheBatchCreates_IsNotThereWhenItCompiles(string reference)
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError(
            "select 1 as ran into #ran; create xml schema collection c2 as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"a\" type=\"xs:int\"/></xs:schema>'; " + reference,
            6314);
        AreEqual("Collection specified does not exist in metadata : 'c2'", ex.Errors[0].Message);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.xml_schema_collections where name = 'c2'"));
    }

    // ---- method arguments are string literals --------------------------------

    [TestMethod]
    [DataRow("declare @x xml = '<a>1</a>', @p nvarchar(9) = N'/a'; select @x.value(@p, 'int')", "value", 1)]
    [DataRow("declare @x xml = '<a>1</a>', @t varchar(9) = 'int'; select @x.value('/a', @t)", "value", 2)]
    [DataRow("declare @x xml = '<a>1</a>'; select @x.query(N'/a' + N'')", "query", 1)]
    [DataRow("declare @x xml = '<a>1</a>'; select @x.exist(concat('/a', ''))", "exist", 1)]
    [DataRow("declare @x xml = '<a>1</a>'; select @x.value(null, 'int')", "value", 1)]
    [DataRow("declare @x xml = '<a>1</a>'; select @x.value('(/a)[1]', 1)", "value", 2)]
    [DataRow("declare @x xml = '<a>1</a>', @p nvarchar(9) = '/a'; select 1 from @x.nodes(@p) t(c)", "nodes", 1)]
    [DataRow("declare @x xml = '<a>1</a>', @p nvarchar(20) = N'delete /a'; set @x.modify(@p)", "modify", 1)]
    public void ANonLiteralArgument_IsMsg8172(string batch, string method, int position) =>
        new Simulation().AssertSqlError(batch, 8172, $"The argument {position} of the XML data type method \"{method}\" must be a string literal.");

    [TestMethod]
    public void ANonLiteralArgument_StopsTheBatchWhileItCompiles()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create table ran (a int); declare @x xml = '<a>1</a>'; begin try select @x.exist(N'/a' + N''); end try begin catch end catch", 8172);
        _ = IsInstanceOfType<DBNull>(simulation.ExecuteScalar("select object_id('ran')"));
    }

    [TestMethod]
    public void AParenthesizedLiteral_Passes() =>
        AreEqual(1, new Simulation().ExecuteScalar("declare @x xml = '<a>1</a>'; select @x.value(('(/a)[1]'), ('int'))"));

    // ---- a schema collection's compile errors ---------------------------------

    [TestMethod]
    [DataRow("""<xs:element name="a" type="nosuch"/>""", 2307, "Reference to an undefined name 'nosuch'")]
    [DataRow("""<xs:element name="a" type="xs:nosuch"/>""", 2308, "Reference to an undefined name 'nosuch' within namespace 'http://www.w3.org/2001/XMLSchema'")]
    [DataRow("""<xs:element name="a"><xs:complexType><xs:sequence><xs:element ref="b"/></xs:sequence></xs:complexType></xs:element>""", 2307, "Reference to an undefined name 'b'")]
    [DataRow("""<xs:element name="a"><xs:complexType><xs:attribute ref="xml:lang"/></xs:complexType></xs:element>""", 2308, "Reference to an undefined name 'lang' within namespace 'http://www.w3.org/XML/1998/namespace'")]
    [DataRow("""<xs:element name="a"><xs:complexType><xs:sequence><xs:group ref="g"/></xs:sequence><xs:attributeGroup ref="ag"/></xs:complexType></xs:element>""", 2307, "Reference to an undefined name 'g'")]
    [DataRow("""<xs:simpleType name="l"><xs:list itemType="zz"/></xs:simpleType>""", 2307, "Reference to an undefined name 'zz'")]
    [DataRow("""<xs:element name="a" type="t"/><xs:element name="a" type="xs:int"/>""", 2302, "The name \"a\" has already been defined in this scope.")]
    [DataRow("""<xs:attribute name="q" type="xs:int"/><xs:attribute name="q" type="xs:int"/>""", 2302, "The name \"q\" has already been defined in this scope.")]
    [DataRow("""<xs:simpleType name="s"><xs:restriction base="xs:string"><xs:maxLength value="abc"/></xs:restriction></xs:simpleType>""", 2309, "The value of \"value\" is not a valid number.")]
    [DataRow("""<xs:simpleType name="s"><xs:restriction base="xs:string"><xs:minInclusive value="1"/></xs:restriction></xs:simpleType>""", 2319, "This type may not have a 'minInclusive' facet. Location: '/*:schema[1]/*:simpleType[1]/*:restriction[1]/*:minInclusive[1]'.")]
    public void AnUncompilableSchema_IsRefused(string body, int number, string message) =>
        new Simulation().AssertSqlError($"create xml schema collection x as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">{body}</xs:schema>'", number, message);

    [TestMethod]
    public void AFacetInASecondDocument_IsLocatedThere() =>
        new Simulation().AssertSqlError(
            """create xml schema collection x as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="xs:int"/></xs:schema><xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:b"><xs:simpleType name="s"><xs:restriction base="xs:int"><xs:maxLength value="3"/></xs:restriction></xs:simpleType></xs:schema>'""",
            2319,
            "This type may not have a 'maxLength' facet. Location: '/*:schema[2]/*:simpleType[1]/*:restriction[1]/*:maxLength[1]'.");

    [TestMethod]
    public void TheSchemaCompileErrors_ActAsUnderXactAbort()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand(
            """begin tran; create xml schema collection x as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a" type="t"/></xs:schema>'; select 1""").ExecuteNonQuery());
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void AddedComponents_ResolveAgainstTheCollection()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""create xml schema collection x as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:group name="g"><xs:sequence><xs:element name="z"/></xs:sequence></xs:group><xs:complexType name="t"><xs:sequence><xs:element name="y"/></xs:sequence></xs:complexType></xs:schema>'""");
        _ = simulation.ExecuteNonQuery("""alter xml schema collection x add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a"><xs:complexType><xs:group ref="g"/></xs:complexType></xs:element><xs:element name="b" type="t"/></xs:schema>'""");
        _ = simulation.AssertSqlError("""alter xml schema collection x add N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="q" type="zz"/></xs:schema>'""", 2307);
    }

    [TestMethod]
    public void TheSqlTypesNamespace_Resolves() =>
        _ = new Simulation().ExecuteNonQuery("""create xml schema collection x as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:s="http://schemas.microsoft.com/sqlserver/2004/sqltypes"><xs:import namespace="http://schemas.microsoft.com/sqlserver/2004/sqltypes"/><xs:element name="a" type="s:varchar"/></xs:schema>'""");
}
