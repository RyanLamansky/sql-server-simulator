using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The typed half of the XQuery evaluator: <c>xs:</c> constructor functions,
/// <c>cast as</c>, <c>instance of</c>, the numeric types arithmetic computes
/// in and renders by, the <c>sql:variable</c> / <c>sql:column</c> accessors in
/// the read methods, the node comparisons and the named axes. Every expected
/// value and message was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlTypedAtomicTests
{
    private const string Document = "<r a=\"1\" b=\"2\"><x>1</x><x>2</x><y>abc</y><z>1234567</z></r>";

    private static string? Query(string xquery) =>
        (string?)new Simulation().ExecuteScalar(
            $"declare @x xml = '{Document}'; select convert(nvarchar(max), @x.query('{xquery}'))");

    private static void QueryError(string xquery, int errorNumber, string expectedMessage) =>
        new Simulation().AssertSqlError($"declare @x xml = '{Document}'; select @x.query('{xquery}')", errorNumber, expectedMessage);

    // ---- constructor functions and cast as -----------------------------------

    [TestMethod]
    [DataRow("xs:integer(\"-007\")", "-7")]
    [DataRow("xs:decimal(\"1.50\")", "1.5")]
    [DataRow("xs:double(\"1e20\")", "1.0E20")]
    [DataRow("xs:double(\"0.000001\")", "0.000001")]
    [DataRow("xs:double(\"1234567.891\")", "1.234567891E6")]
    [DataRow("xs:double(\"INF\")", "INF")]
    [DataRow("xs:float(\"0.1\")", "0.1")]
    [DataRow("xs:boolean(\"true\")", "true")]
    [DataRow("xs:dateTime(\"2020-01-02T03:04:05.000+02:00\")", "2020-01-02T03:04:05+02:00")]
    [DataRow("xs:time(\"24:00:00\")", "00:00:00")]
    [DataRow("xs:hexBinary(\"ab\")", "AB")]
    [DataRow("xs:token(\" a  b \")", "a b")]
    [DataRow("xs:long(\"9223372036854775807\")", "9223372036854775807")]
    [DataRow("xs:integer(1.7)", "1")]
    [DataRow("xs:string(1.50)", "1.5")]
    [DataRow("xdt:untypedAtomic(\"a\")", "a")]
    [DataRow("xs:integer((/r/@a)[1]) + 1", "2")]
    [DataRow("(xs:integer(\"5\"), 1.5, xs:double(\"2\"))", "5 1.5 2")]
    public void ConstructorFunction_RendersTheCanonicalForm(string xquery, string expected) =>
        AreEqual(expected, Query(xquery));

    [TestMethod]
    public void ConstructorFunction_Refusals()
    {
        QueryError("xs:integer(\"abc\")", 9319, "XQuery [query()]: Static simple type validation: Invalid simple type value 'abc'.");
        QueryError("xs:unsignedByte(\"256\")", 9319, "XQuery [query()]: Static simple type validation: Invalid simple type value '256'.");
        QueryError("xs:integer(())", 2365, "XQuery [query()]: Cannot explicitly convert from 'empty' to 'xs:integer'");
        QueryError("xs:integer(/r/x)", 2365, "XQuery [query()]: Cannot explicitly convert from 'xdt:untypedAtomic *' to 'xs:integer'");
        QueryError("xs:QName(\"a\")", 2365, "XQuery [query()]: Cannot explicitly convert from 'xs:string' to 'xs:QName'");
        QueryError("xs:integer(1,2)", 2238, "XQuery [query()]: Too many arguments in call to function 'xs:integer'");
        QueryError("xs:integer()", 2236, "XQuery [query()]: There are not enough actual arguments in the call to function \"xs:integer\".");
        QueryError("xs:nope(\"1.5\")", 2395, "XQuery [query()]: There is no function '{http://www.w3.org/2001/XMLSchema}:nope()'");
        QueryError("xs:untypedAtomic(\"a\")", 2395, "XQuery [query()]: There is no function '{http://www.w3.org/2001/XMLSchema}:untypedAtomic()'");
    }

    [TestMethod]
    public void Cast_OfAValueFromTheInstanceThatDoesNotConvert_IsEmpty()
    {
        AreEqual(string.Empty, Query("(/r/y)[1] cast as xs:integer?"));
        AreEqual("0", Query("count(xs:integer((/r/y)[1]))"));
        _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($"select cast('{Document}' as xml).value('xs:integer((/r/y)[1])', 'int')"));
    }

    [TestMethod]
    public void CastAs_BindsTighterThanArithmetic()
    {
        AreEqual("5", Query("\"5\" cast as xs:integer?"));
        AreEqual("5.5", Query("\"5.50\" cast as xs:decimal?"));
        AreEqual("2", Query("(/r/@a)[1] cast as xs:integer? + 1"));
        AreEqual("true", Query("1 cast as xs:boolean?"));
        AreEqual("1000", Query("\"1e3\" cast as xs:double?"));
    }

    [TestMethod]
    public void CastAs_Refusals()
    {
        QueryError("\"5\" cast as xs:integer", 9301, "XQuery [query()]: In this version of the server, 'cast as <type>' is not available. Please use the 'cast as <type> ?' syntax.");
        QueryError("\"x\" cast as xs:nope?", 2232, "XQuery [query()]: The name \"xs:nope\" does not denote a defined type.");
        QueryError("(1,2) cast as xs:integer?", 2365, "XQuery [query()]: Cannot explicitly convert from 'xs:integer +' to 'xs:integer ?'");
        QueryError("\"1.0\" cast as xs:integer?", 9319, "XQuery [query()]: Static simple type validation: Invalid simple type value '1.0'.");
        QueryError("\"x\" castable as xs:integer", 9335, "XQuery [query()]: The XQuery syntax 'castable as' is not supported.");
    }

    // ---- instance of -------------------------------------------------------

    [TestMethod]
    [DataRow("(/r)[1] instance of element()", "true")]
    [DataRow("(/r)[1] instance of element(q)?", "false")]
    [DataRow("(/r/@a)[1] instance of attribute()?", "true")]
    [DataRow("(/r/x/text())[1] instance of text()?", "true")]
    [DataRow("1 instance of xs:integer", "true")]
    [DataRow("1 instance of xs:decimal", "true")]
    [DataRow("1.5 instance of xs:integer", "false")]
    [DataRow("\"a\" instance of xs:string", "true")]
    [DataRow("data((/r/@a)[1]) instance of xdt:untypedAtomic?", "true")]
    [DataRow("() instance of empty()", "true")]
    [DataRow("1 instance of item()", "true")]
    [DataRow("if ((/r)[1] instance of element()?) then 1 else 2", "1")]
    public void InstanceOf(string xquery, string expected) =>
        AreEqual(expected, Query(xquery));

    [TestMethod]
    public void InstanceOf_Refusals()
    {
        QueryError("/r instance of element()", 2389, "XQuery [query()]: 'instance of' requires a singleton (or empty sequence), found operand of type 'element(r,xdt:untyped) *'");
        QueryError("(1,2) instance of xs:integer*", 2389, "XQuery [query()]: 'instance of' requires a singleton (or empty sequence), found operand of type 'xs:integer +'");
        QueryError("1 instance of xs:nope", 2232, "XQuery [query()]: The name \"xs:nope\" does not denote a defined type.");
        QueryError("(/r)[1] instance of document-node()?", 9335, "XQuery [query()]: The XQuery syntax 'document-node()' is not supported.");
    }

    // ---- arithmetic types ----------------------------------------------------

    [TestMethod]
    [DataRow("data((/r/z)[1]) + 1", "1.234568E6")]
    [DataRow("sum(/r/z) * 10", "1.234567E7")]
    [DataRow("max(/r/z)", "1.234567E6")]
    [DataRow("-(/r/z)[1]", "-1.234567E6")]
    [DataRow("1234567 + 1", "1234568")]
    [DataRow("0.1 + 0.2", "0.3")]
    [DataRow("1 div 3", "0.333333")]
    [DataRow("2 div 3", "0.666666")]
    [DataRow("1.23456789 * 1.1", "1.358025")]
    [DataRow("(/r/x)[1] div 3", "0.333333333333333")]
    [DataRow("1e0 div 3", "0.333333333333333")]
    [DataRow("1 div 0", "")]
    [DataRow("xs:double(\"1\") div 0", "INF")]
    [DataRow("avg((1, 2, 2))", "1.6666666666")]
    [DataRow("round(-2.5)", "-2")]
    [DataRow("round(2.5)", "3")]
    public void Arithmetic_ComputesAndRendersInItsStaticType(string xquery, string expected) =>
        AreEqual(expected, Query(xquery));

    [TestMethod]
    public void Arithmetic_Refusals()
    {
        QueryError("2 idiv 1", 9335, "XQuery [query()]: The XQuery syntax 'idiv' is not supported.");
        QueryError("/r/x + 1", 2389, "XQuery [query()]: '+' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'");
        QueryError("number(\"12345678\")", 2374, "XQuery [query()]: A node or set of nodes is required for number()");
    }

    [TestMethod]
    public void Value_ReadsARenderedDoubleThroughItsText() =>
        new Simulation().AssertSqlError(
            $"select cast('{Document}' as xml).value('sum(/r/z) * 10', 'int')",
            245,
            "Conversion failed when converting the nvarchar value '1.234567E7' to data type int.");

    // ---- sql:variable / sql:column in the read methods -----------------------

    [TestMethod]
    [DataRow("int = 5", "5")]
    [DataRow("decimal(5,2) = 1.50", "1.5")]
    [DataRow("money = 1.2345", "1.2345")]
    [DataRow("float = 1.5e10", "1.5E10")]
    [DataRow("real = 0.1", "0.1")]
    [DataRow("bit = 1", "true")]
    [DataRow("bigint = 9223372036854775807", "9223372036854775807")]
    [DataRow("datetime = '2020-01-02T03:04:05'", "2020-01-02T03:04:05.000")]
    [DataRow("smalldatetime = '2020-01-02T03:04:00'", "2020-01-02T03:04:00.000")]
    [DataRow("datetime2(3) = '2020-01-02T03:04:05.123'", "2020-01-02T03:04:05.123")]
    [DataRow("date = '2020-01-02'", "2020-01-02")]
    [DataRow("time(2) = '03:04:05.12'", "03:04:05.12")]
    [DataRow("datetimeoffset = '2020-01-02T03:04:05+02:00'", "2020-01-02T03:04:05.0000000+02:00")]
    [DataRow("uniqueidentifier = '6F9619FF-8B86-D011-B42D-00C04FC964FF'", "6F9619FF-8B86-D011-B42D-00C04FC964FF")]
    [DataRow("varbinary(4) = 0x0102", "AQI=")]
    [DataRow("nvarchar(10) = N'a<b'", "a&lt;b")]
    public void SqlVariable_RendersInItsMappedType(string declaration, string expected) =>
        AreEqual(
            $"<a>{expected}</a>",
            new Simulation().ExecuteScalar($"declare @v {declaration}; select convert(nvarchar(max), cast('<r/>' as xml).query('<a>{{sql:variable(\"@v\")}}</a>'))"));

    [TestMethod]
    public void SqlVariable_NullIsTheEmptySequence() =>
        AreEqual("<a/>", new Simulation().ExecuteScalar("declare @v int; select convert(nvarchar(max), cast('<r/>' as xml).query('<a>{sql:variable(\"@v\")}</a>'))"));

    [TestMethod]
    public void SqlVariable_IsTypedByItsSqlType()
    {
        var simulation = new Simulation();
        AreEqual("true", simulation.ExecuteScalar("declare @v int = 5; select convert(nvarchar(max), cast('<r/>' as xml).query('sql:variable(\"@v\") instance of xs:int'))"));
        AreEqual("6", simulation.ExecuteScalar("declare @v int = 5; select convert(nvarchar(max), cast('<r/>' as xml).query('sql:variable(\"@v\") + 1'))"));
        IsTrue((bool)simulation.ExecuteScalar("declare @v int = 5; select cast('<r><a>05</a></r>' as xml).exist('/r/a[. = sql:variable(\"@v\")]')")!);
        IsFalse((bool)simulation.ExecuteScalar("declare @v varchar(9) = '5'; select cast('<r><a>05</a></r>' as xml).exist('/r/a[. = sql:variable(\"@v\")]')")!);
        simulation.AssertSqlError(
            "declare @v int = 5; select cast('<r/>' as xml).query('sql:variable(\"@v\") = \"5\"')",
            2234,
            "XQuery [query()]: The operator \"=\" cannot be applied to \"xs:int ?\" and \"xs:string\" operands.");
        simulation.AssertSqlError(
            "declare @v nvarchar(10) = N'5'; select cast('<r/>' as xml).query('sql:variable(\"@v\") + 1')",
            9308,
            "XQuery [query()]: The argument of '+' must be of a single numeric primitive type or 'http://www.w3.org/2004/07/xpath-datatypes#untypedAtomic'. Found argument of type 'xs:string ?'.");
    }

    [TestMethod]
    public void SqlVariable_Refusals()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError(
            "select cast('<r/>' as xml).query('sql:variable(\"@nope\")')",
            9501,
            "XQuery: Unable to resolve sql:variable('@nope'). The variable must be declared as a scalar TSQL variable.");
        simulation.AssertSqlError(
            "declare @v int = 5; select cast('<r/>' as xml).query('sql:variable(\"v\")')",
            9519,
            "XQuery: The name supplied to sql:variable('v') is not a valid SQL variable name. Variable names must start with the '@' symbol followed by at least one character.");
        simulation.AssertSqlError(
            "declare @v xml = '<q/>'; select cast('<r/>' as xml).query('<a>{sql:variable(\"@v\")}</a>')",
            9342,
            "XQuery [query()]: An XML instance is only supported as the direct source of an insert using sql:column/sql:variable.");
        simulation.AssertSqlError(
            "declare @v sql_variant = 1; select cast('<r/>' as xml).query('<a>{sql:variable(\"@v\")}</a>')",
            9344,
            "XQuery [query()]: The SQL type 'sql_variant' is not supported with sql:column() and sql:variable().");
        simulation.AssertSqlError(
            "declare @v int = 5; select cast('<r/>' as xml).query('sql:variable(\"@v\", 1)')",
            2238,
            "XQuery [query()]: Too many arguments in call to function 'variable'");
        simulation.AssertSqlError(
            "declare @v int = 5; select cast('<r/>' as xml).query('sql:variable(@v)')",
            2225,
            "XQuery [query()]: A string literal was expected");
        simulation.AssertSqlError(
            "select cast('<r/>' as xml).query('sql:nope(\"@v\")')",
            2395,
            "XQuery [query()]: There is no function '{urn:schemas-microsoft-com:xml-sql}:nope()'");
    }

    [TestMethod]
    public void SqlColumn_BindsAgainstTheQueryScope()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, d xml); insert t values (2, '<r><a>1</a><a>2</a></r>')");
        AreEqual(2, simulation.ExecuteScalar("select d.value('(/r/a[. = sql:column(\"id\")])[1]', 'int') from t"));
        AreEqual("<x>2</x>", simulation.ExecuteScalar("select convert(nvarchar(max), d.query('<x>{sql:column(\"q.id\")}</x>')) from t as q"));
        AreEqual("<x>3</x>", simulation.ExecuteScalar("select convert(nvarchar(max), d.query('<x>{sql:column(\"z.v\")}</x>')) from t cross apply (select 3 as v) z"));
        AreEqual(1, simulation.ExecuteScalar("select 1 from t where d.exist('/r[sql:column(\"id\") = 2]') = 1"));
        simulation.AssertSqlError("select d.query('sql:column(\"nope\")') from t", 207, "Invalid column name 'nope'.");
        simulation.AssertSqlError(
            "select d.query('<x>{sql:column(\"t.id\")}</x>') from t as q",
            107,
            "The column prefix 't' does not match with a table name or alias name used in the query.");
        simulation.AssertSqlError(
            "select d.query('<x>{sql:column(\"d\")}</x>') from t",
            9342,
            "XQuery [t.d.query()]: An XML instance is only supported as the direct source of an insert using sql:column/sql:variable.");
    }

    [TestMethod]
    public void SqlColumn_WithNoQueryScope_Raises207() =>
        new Simulation().AssertSqlError("select cast('<r/>' as xml).query('<x>{sql:column(\"id\")}</x>')", 207, "Invalid column name 'id'.");

    // ---- node comparisons ----------------------------------------------------

    [TestMethod]
    public void NodeComparisons()
    {
        AreEqual("true", Query("(/r/x)[1] is (/r/x)[1]"));
        AreEqual("false", Query("(/r/x)[1] is (/r/y)[1]"));
        AreEqual("true", Query("(/r/x)[1] << (/r/x)[2]"));
        AreEqual("true", Query("(/r/x)[2] >> (/r/x)[1]"));
        QueryError("/r/x[1] is /r/x[1]", 2389, "XQuery [query()]: 'is' requires a singleton (or empty sequence), found operand of type 'element(x,xdt:untyped) *'");
        QueryError("() is (/r/x)[1]", 2234, "XQuery [query()]: The operator \"is\" cannot be applied to \"empty\" and \"element(x,xdt:untyped) ?\" operands.");
    }

    // ---- named axes and the unsupported syntax -----------------------------

    [TestMethod]
    [DataRow("/child::r/child::x", "<x>1</x><x>2</x>")]
    [DataRow("data(/r/attribute::*)", "1 2")]
    [DataRow("/descendant::x", "<x>1</x><x>2</x>")]
    [DataRow("/r/descendant-or-self::x", "<x>1</x><x>2</x>")]
    [DataRow("/r/x/self::x", "<x>1</x><x>2</x>")]
    [DataRow("/r/x/parent::q", "")]
    [DataRow("/descendant-or-self::node()/y", "<y>abc</y>")]
    public void NamedAxes(string xquery, string expected) =>
        AreEqual(expected, Query(xquery));

    [TestMethod]
    public void NamedAxes_Refusals()
    {
        QueryError("/r/following-sibling::x", 9335, "XQuery [query()]: The XQuery syntax 'following-sibling' is not supported.");
        QueryError("/r/ancestor::x", 9335, "XQuery [query()]: The XQuery syntax 'ancestor' is not supported.");
        QueryError("/r/namespace::x", 2392, "XQuery [query()]: 'namespace::' is not a valid axis");
        QueryError("/r/child :: x", 2209, "XQuery [query()]: Syntax error near 'child'");
        QueryError("/r/self::x", 2261, "XQuery [query()]: There is no element named 'x' in the type 'element(r,xdt:untyped) *'.");
    }

    [TestMethod]
    [DataRow("typeswitch (/r) case element() return 1 default return 2", "typeswitch")]
    [DataRow("validate {/r}", "validate")]
    [DataRow("ordered {/r}", "ordered")]
    [DataRow("declare function local:f() {1}; 1", "declare function")]
    [DataRow("declare variable $v := 1; $v", "declare variable")]
    public void UnsupportedSyntax_Raises9335(string xquery, string construct) =>
        QueryError(xquery, 9335, $"XQuery [query()]: The XQuery syntax '{construct}' is not supported.");

    [TestMethod]
    public void Prolog_VersionDeclarationAndDefaultFunctionNamespace_AreAccepted()
    {
        AreEqual("<x>1</x><x>2</x>", Query("xquery version \"1.0\"; /r/x"));
        AreEqual("<x>1</x><x>2</x>", Query("declare default function namespace \"urn:x\"; /r/x"));
        QueryError("declare boundary-space preserve; <a> </a>", 2209, "XQuery [query()]: Syntax error near 'declare'");
    }
}
