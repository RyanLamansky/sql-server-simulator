using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What an XQuery expression knows about the node it starts from and the
/// numbers it computes: a <c>.nodes()</c> column read from a nested query, the
/// context item's type inside a predicate on a typed step, a row standing on
/// an attribute, and <c>xs:decimal</c>'s exactness past a double's. Every
/// expected value was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlStaticContextTests
{
    private const string Rows = "declare @x xml = '<r><a>1</a><a>2</a></r>'; ";

    private const string TypedCollection = """
        create xml schema collection c as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="r"><xs:complexType><xs:sequence><xs:element name="s" type="xs:string" maxOccurs="unbounded"/><xs:element name="d" type="xs:decimal" maxOccurs="unbounded"/></xs:sequence><xs:attribute name="x" type="xs:int"/></xs:complexType></xs:element></xs:schema>'
        """;

    private static object? Scalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    // ---- a .nodes() column from a nested query ---------------------------------

    [TestMethod]
    [DataRow("select (select c for xml path) from @x.nodes('/r/a') t(c)")]
    [DataRow("select z.z from @x.nodes('/r/a') t(c) cross apply (select c as z) z")]
    [DataRow("select (select top 1 c) from @x.nodes('/r/a') t(c)")]
    [DataRow("select 1 from @x.nodes('/r/a') t(c) where exists (select c)")]
    [DataRow("select (select 1 from (select c as q) d) from @x.nodes('/r/a') t(c)")]
    [DataRow("select 1 from @x.nodes('/r/a') t(c) where 1 in (select count(*) from @x.nodes('/r') u(c) group by c)")]
    public void AnOuterReference_IsMsg493(string query) =>
        new Simulation().AssertSqlError(Rows + query, 493);

    [TestMethod]
    public void AnOuterConversion_IsMsg525() =>
        new Simulation().AssertSqlError(Rows + "select x.v from @x.nodes('/r/a') t(c) outer apply (select cast(c as varchar(10)) v) x", 525);

    [TestMethod]
    [DataRow("select sum(z.v) from @x.nodes('/r/a') t(c) cross apply (select c.value('.', 'int') as v) z", 3)]
    [DataRow("select (select c.value('.', 'int')) from @x.nodes('/r/a') t(c)", 1)]
    [DataRow("select (select count(*) where c is not null) from @x.nodes('/r/a') t(c)", 1)]
    public void AMethodOrANullTest_ReadsTheOuterColumn(string query, int expected) =>
        AreEqual(expected, Scalar(Rows + query));

    // ---- the context item's type in a predicate --------------------------------

    [TestMethod]
    [DataRow("/r/s[. = 1]", "xs:string", "xs:integer")]
    [DataRow("/r/d[. = \"2\"]", "xs:decimal", "xs:string")]
    [DataRow("/r/@x[. = \"1\"]", "xs:int", "xs:string")]
    public void ATypedContextItem_RefusesAMismatchedComparison(string path, string left, string right)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(TypedCollection);
        simulation.AssertSqlError($"declare @x xml(c) = '<r x=\"1\"><s>1</s><d>2</d></r>'; select @x.query('{path}')", 2234,
            $"XQuery [query()]: The operator \"=\" cannot be applied to \"{left}\" and \"{right}\" operands.");
    }

    [TestMethod]
    [DataRow("/r/s[. = \"1\"]", "<s>1</s>")]
    [DataRow("/r/d[. ge 2]", "<d>2</d>")]
    [DataRow("/r/d[. + 1 = 3]", "<d>2</d>")]
    [DataRow("/r/s[string-length(.) = 1]", "<s>1</s>")]
    public void ATypedContextItem_ComparesAsItsType(string path, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(TypedCollection);
        AreEqual(expected, simulation.ExecuteScalar($"declare @x xml(c) = '<r x=\"1\"><s>1</s><d>2</d></r>'; select convert(nvarchar(max), @x.query('{path}'))"));
    }

    // ---- a row standing on an attribute ----------------------------------------

    [TestMethod]
    [DataRow("value('@x', 'int')", 2219, "XQuery [value()]: There is no attribute named '@x' in the type 'attribute(*,xdt:untypedAtomic)'.")]
    [DataRow("value('*[1]', 'int')", 2261, "XQuery [value()]: There is no element named '*' in the type 'attribute(*,xdt:untypedAtomic)'.")]
    [DataRow("query('text()')", 2377, "XQuery [query()]: Result of 'text()' expression is statically 'empty'")]
    public void AnAttributeRow_HasNoChildrenOrAttributes(string call, int number, string message) =>
        new Simulation().AssertSqlError($"declare @x xml = '<r><a x=\"1\"/></r>'; select c.{call} from @x.nodes('/r/a/@*') t(c)", number, message);

    [TestMethod]
    public void AnAttributeRow_NamesItsAttributeInTheType() =>
        new Simulation().AssertSqlError("declare @x xml = '<r><a x=\"1\"/></r>'; select c.exist('b') from @x.nodes('/r/a/@x') t(c)", 2261,
            "XQuery [exist()]: There is no element named 'b' in the type 'attribute(x,xdt:untypedAtomic)'.");

    [TestMethod]
    public void AnAttributeRow_ReadsItselfAndItsParent()
    {
        AreEqual(1, Scalar("declare @x xml = '<r><a x=\"1\"/></r>'; select c.value('.', 'int') from @x.nodes('/r/a/@*') t(c)"));
        AreEqual(1, Scalar("declare @x xml = '<r><a x=\"1\"/></r>'; select c.value('../@x', 'int') from @x.nodes('/r/a/@x') t(c)"));
    }

    // ---- exact numbers ----------------------------------------------------------

    [TestMethod]
    [DataRow("12345678901234567890 + 1", "12345678901234567891")]
    [DataRow("9007199254740993", "9007199254740993")]
    [DataRow("9007199254740993 * 2", "18014398509481986")]
    [DataRow("xs:integer(\"9007199254740993\") + 0", "9007199254740993")]
    [DataRow("9223372036854775807 + 1", "9223372036854775808")]
    [DataRow("123456789012345678901234567 + 1", "123456789012345678901234568")]
    [DataRow("9007199254740993 mod 10", "3")]
    [DataRow("-9007199254740993 - 1", "-9007199254740994")]
    [DataRow("9007199254740993 div 1", "9007199254740993")]
    [DataRow("xs:decimal(9007199254740993) + 0.5", "9007199254740993.5")]
    [DataRow("sum((9007199254740993, 1))", "9007199254740994")]
    [DataRow("max((9007199254740993, 9007199254740992))", "9007199254740993")]
    [DataRow("avg((9007199254740993, 9007199254740993))", "9007199254740993")]
    [DataRow("round(9007199254740993)", "9007199254740993")]
    [DataRow("9007199254740993 = 9007199254740992", "false")]
    [DataRow("9007199254740993 > 9007199254740992", "true")]
    [DataRow("1.000000000000000000001", "1")]
    [DataRow("0.00000000005", "0.0000000001")]
    [DataRow("0.000000000049", "0")]
    [DataRow("1.99999999999", "2")]
    [DataRow("xs:decimal(\"0.00000000005\")", "0.0000000001")]
    [DataRow("0.00000000005 * 1", "0")]
    [DataRow("1.23456789012345678 * 1", "1.234568")]
    [DataRow("123456789.123456789 + 1", "123456790.123456789")]
    [DataRow("9999999999999999999999999999 + 1", "")]
    public void ExactNumbers_ComputeAsNumeric38_10(string expression, string expected) =>
        AreEqual(expected, Scalar($"declare @x xml = ''; select convert(nvarchar(max), @x.query('{expression}'))"));

    [TestMethod]
    public void ExactNumbers_ReachValueWhole()
    {
        AreEqual(9007199254740993L, Scalar("declare @x xml = ''; select @x.value('9007199254740993', 'bigint')"));
        AreEqual(9007199254740993m, Scalar("declare @x xml = ''; select @x.value('9007199254740993 + 0', 'decimal(20,0)')"));
    }

    [TestMethod]
    [DataRow("99999999999999999999999999999")]
    [DataRow("1234567890123456789012345678901234567890")]
    public void ALiteralPast28IntegerDigits_IsMsg2342(string literal) =>
        new Simulation().AssertSqlError($"declare @x xml = ''; select @x.query('{literal}')", 2342, "XQuery [query()]: Invalid numeric constant.");
}
