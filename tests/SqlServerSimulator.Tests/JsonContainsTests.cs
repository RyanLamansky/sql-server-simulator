using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's <c>JSON_CONTAINS</c> and the advanced JSON path array
/// accessors — <c>[*]</c>, <c>.*</c>, ranges, lists and <c>last</c> — that it
/// and the rest of the JSON family read. Every expectation was probed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class JsonContainsTests
{
    private const string Document = """{"a":1,"b":[1,2,3],"c":{"d":"x"},"e":null,"f":true,"g":false,"s":"Hi","n":1.50,"big":100}""";

    private static void AssertError(string commandText, int number, byte state, string message)
    {
        var ex = new Simulation().AssertSqlError(commandText, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual(state, ex.Errors[0].State);
    }

    private static object? Contains(string arguments, string document = Document) =>
        new Simulation().ExecuteScalar($"declare @j json = '{document}'; select json_contains(@j, {arguments})");

    [TestMethod]
    [DataRow("1, '$.a'", 1)]
    [DataRow("2, '$.a'", 0)]
    [DataRow("1.0, '$.a'", 1)]
    [DataRow("cast(1 as bigint), '$.a'", 1)]
    [DataRow("cast(1 as tinyint), '$.a'", 1)]
    [DataRow("1.5, '$.n'", 1)]
    [DataRow("1, '$.n'", 0)]
    [DataRow("100, '$.big'", 1)]
    [DataRow("'1', '$.a'", 0)]
    [DataRow("cast(1 as bit), '$.f'", 1)]
    [DataRow("cast(0 as bit), '$.g'", 1)]
    [DataRow("cast(1 as bit), '$.a'", 0)]
    [DataRow("1, '$.f'", 0)]
    [DataRow("'true', '$.f'", 0)]
    [DataRow("'hi', '$.s'", 1)]
    [DataRow("N'Hi ', '$.s'", 1)]
    [DataRow("'hi' collate Latin1_General_CS_AS, '$.s'", 0)]
    [DataRow("cast('Hi' as char(10)), '$.s'", 1)]
    [DataRow("'x', '$.c.d'", 1)]
    [DataRow("1, '$.c'", 0)]
    [DataRow("2, '$.b'", 0)]
    [DataRow("2, '$.b[*]'", 1)]
    [DataRow("5, '$.b[*]'", 0)]
    [DataRow("1, '$.e'", 0)]
    [DataRow("cast(null as int), '$.a'", 0)]
    [DataRow("1, 'strict $.a'", 1)]
    public void Matches(string arguments, int expected) => AreEqual(expected, Contains(arguments));

    [TestMethod]
    [DataRow("1, '$.z'")]
    [DataRow("1, 'strict $.z'")]
    [DataRow("1, '$.a[*]'")]
    [DataRow("1, '$.c[*]'")]
    [DataRow("1, '$.b[5]'")]
    [DataRow("1, '$.b[5 to 7]'")]
    [DataRow("1, '$.b[5, 6]'")]
    [DataRow("1, '$.b[*].x'")]
    public void PathFindingNothingIsNull(string arguments) => IsInstanceOfType<DBNull>(Contains(arguments));

    [TestMethod]
    [DataRow("[1,2,3]", "1", 1)]
    [DataRow("[1,2,3]", "4", 0)]
    [DataRow("[[1],2]", "1", 0)]
    [DataRow("[[1],2]", "2", 1)]
    [DataRow("[]", "1", 0)]
    [DataRow("[{\"a\":1},\"x\",null]", "'x'", 1)]
    public void WithoutPathSearchesTheRootArraysElements(string document, string value, int expected) =>
        AreEqual(expected, Contains(value, document));

    [TestMethod]
    public void WithoutPathOverAnObjectIsNull() => IsInstanceOfType<DBNull>(Contains("1", """{"a":1}"""));

    [TestMethod]
    [DataRow("'hel%'", 1)]
    [DataRow("'h_llo'", 1)]
    [DataRow("'H[a-f]llo'", 1)]
    [DataRow("'Hell'", 0)]
    [DataRow("'hel%' collate Latin1_General_CS_AS", 0)]
    public void LikeMode(string pattern, int expected) =>
        AreEqual(expected, Contains($"{pattern}, '$[*]', 1", """["Hello"]"""));

    [TestMethod]
    [DataRow("'hel%', '$[*]', 0", 0)]
    [DataRow("'hello', '$[*]', null", 1)]
    [DataRow("123, '$[*]', 1", 1)]
    [DataRow("'12%', '$[*]', 1", 0)]
    public void ModeReadsOnlyStrings(string arguments, int expected) => AreEqual(expected, Contains(arguments, """["hello", 123]"""));

    [TestMethod]
    [DataRow("'ab', '$[*]', 1", 0)]
    [DataRow("'ab ', '$[*]', 1", 1)]
    [DataRow("'ab', '$[*]'", 1)]
    public void LikeHasNoTrailingSpaceSlack(string arguments, int expected) => AreEqual(expected, Contains(arguments, """["ab "]"""));

    [TestMethod]
    [DataRow("N'€'", 1)]
    [DataRow("N'Ā'", 0)]
    [DataRow("N'A'", 1)]
    [DataRow("'A'", 1)]
    public void JsonStringsReadThroughTheDatabaseCodePage(string value, int expected) =>
        AreEqual(expected, Contains($"{value}, '$[*]'", """["€","Ā"]"""));

    [TestMethod]
    [DataRow("N'ア'", 0)]
    [DataRow("N'?'", 1)]
    [DataRow("'ア'", 1)]
    public void JsonStringOutsideTheCodePageIsAQuestionMark(string value, int expected) =>
        AreEqual(expected, Contains($"{value}, '$[*]'", """["ア"]"""));

    [TestMethod]
    public void NullDocumentIsNull() =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("declare @j json; select json_contains(@j, 1, '$.a', 5)"));

    [TestMethod]
    public void UntypedNullDocumentIsNull() =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select json_contains(null, 1, '$')"));

    [TestMethod]
    public void FiltersRows() =>
        AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int, j json);
            insert t values (1, '{"tags":["x","y"]}'), (2, '{"tags":["z"]}');
            select id from t where json_contains(j, 'x', '$.tags[*]') = 1
            """));

    [TestMethod]
    public void ResultIsNullableInt() =>
        AreEqual("int|1", new Simulation().ExecuteScalar("""
            declare @j json = '{"a":1}';
            select json_contains(@j, 1, '$.a') as r into #t;
            select type_name(system_type_id) + '|' + cast(is_nullable as varchar) from tempdb.sys.columns where object_id = object_id('tempdb..#t')
            """));

    [TestMethod]
    [DataRow("select json_contains('{\"a\":1}', 1, '$.a')", 1, (byte)1, "Argument data type varchar is invalid for argument 1 of json_contains function.")]
    [DataRow("declare @s nvarchar(max) = '{}'; select json_contains(@s, 1, '$.a')", 1, (byte)1, "Argument data type nvarchar(max) is invalid for argument 1 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, null, '$')", 2, (byte)1, "Argument data type NULL is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, cast(1 as float), '$')", 2, (byte)1, "Argument data type float is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, cast(1 as real), '$')", 2, (byte)1, "Argument data type real is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, cast(1 as money), '$')", 2, (byte)1, "Argument data type money is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, getdate(), '$')", 2, (byte)1, "Argument data type datetime is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 0x01, '$')", 2, (byte)1, "Argument data type varbinary is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, @j, '$')", 2, (byte)1, "Argument data type json is invalid for argument 2 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, 1)", 3, (byte)1, "Argument data type int is invalid for argument 3 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, null)", 2, (byte)8, "Argument data type NULL is invalid for argument 2 of JSON_CONTAINS function.")]
    [DataRow("declare @j json = '[1]', @p nvarchar(9); select json_contains(@j, 1, @p)", 2, (byte)8, "Argument data type NULL is invalid for argument 2 of JSON_CONTAINS function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$', 'x')", 4, (byte)1, "Argument data type varchar is invalid for argument 4 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$', cast(1 as bigint))", 4, (byte)1, "Argument data type bigint is invalid for argument 4 of json_contains function.")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$', cast(1 as bit))", 4, (byte)1, "Argument data type bit is invalid for argument 4 of json_contains function.")]
    public void ArgumentTypes(string commandText, int argument, byte state, string message)
    {
        _ = argument;
        AssertError(commandText, 8116, state, message);
    }

    [TestMethod]
    public void ArgumentTypesBindOverAnEmptyRowset() =>
        new Simulation().AssertSqlError("create table t (j json); select json_contains(j, cast(1 as float), '$') from t", 8116);

    [TestMethod]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$', 2)")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$', -1)")]
    [DataRow("declare @j json = '[1]', @v int; select json_contains(@j, @v, '$[*]', 5)")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$.z', 5)")]
    public void ModeOutsideZeroAndOne(string commandText) =>
        AssertError(commandText, 13692, 1, "The comparison_mode argument of JSON_CONTAINS must be 0 or 1.");

    [TestMethod]
    public void ModeIsCheckedPerRow() =>
        AreEqual(0, new Simulation().ExecuteScalar("create table t (j json, m int); select count(*) from (select json_contains(j, 1, '$', 7) c from t) x"));

    [TestMethod]
    [DataRow("declare @j json = '[1]'; select json_contains(@j)")]
    [DataRow("declare @j json = '[1]'; select json_contains(@j, 1, '$', 0, 1)")]
    public void ArgumentCount(string commandText) =>
        AssertError(commandText, 189, 1, "The json_contains function requires 2 to 4 arguments.");

    [TestMethod]
    [DataRow("$[*]", 1)]
    [DataRow("$[0]", 1)]
    [DataRow("$[last]", 3)]
    [DataRow("$[0 to 2]", 3)]
    [DataRow("$[1 to last]", 2)]
    [DataRow("$[last to last]", 3)]
    [DataRow("$[last, 0]", 1)]
    [DataRow("$[2, 0]", 1)]
    [DataRow("$[0, 0]", 1)]
    [DataRow("$[0 to 1, 2]", 3)]
    [DataRow("$[ 0 , 2 ]", 3)]
    [DataRow("$[ last ]", 3)]
    [DataRow("$[ * ]", 2)]
    [DataRow("$[1 to 1]", 2)]
    [DataRow("$[0 to 5000000000]", 1)]
    public void ArrayAccessorsSelect(string path, int value) =>
        AreEqual(1, Contains($"{value}, '{path}'", "[1,2,3]"));

    [TestMethod]
    [DataRow("$[1 to 7]", 0)]
    [DataRow("$[1, 6]", 0)]
    [DataRow("$[last to 0]", 0)]
    public void ArrayAccessorsReachingPastTheEnd(string path, int expected) =>
        AreEqual(expected, Contains($"9, '{path}'", "[1,2,3]"));

    [TestMethod]
    [DataRow("""{"a":1,"b":2}""", "2, '$.*'", 1)]
    [DataRow("""[{"a":1},{"b":2}]""", "2, '$[*].*'", 1)]
    [DataRow("""{"a":{"x":1},"b":{"x":2}}""", "2, '$.*.x'", 1)]
    [DataRow("{}", "9, '$.*'", 0)]
    [DataRow("""{"*":5}""", "5, '$.*'", 1)]
    [DataRow("""{"b":[{"c":[5,6]},{"c":[7]}]}""", "7, '$.b[*].c[*]'", 1)]
    [DataRow("""{"b":[{"c":[5,6]},{"c":[7]}]}""", "7, '$.b[*].c'", 0)]
    [DataRow("""{"a":[[1,2],[3]]}""", "3, '$.a[*][*]'", 1)]
    [DataRow("""{"a":[[1,2],[3]]}""", "3, '$.a[*]'", 0)]
    public void Wildcards(string document, string arguments, int expected) => AreEqual(expected, Contains(arguments, document));

    [TestMethod]
    public void PropertyWildcardOverAnArrayIsNull() => IsInstanceOfType<DBNull>(Contains("1, '$.*'", "[1,2]"));

    [TestMethod]
    [DataRow("$[*x]", 'x', 3, (byte)21)]
    [DataRow("$[*, 1]", ',', 3, (byte)14)]
    [DataRow("$[0, *]", '*', 5, (byte)14)]
    [DataRow("$[,0]", ',', 2, (byte)14)]
    [DataRow("$[0,]", ']', 4, (byte)14)]
    [DataRow("$[ ]", ']', 3, (byte)14)]
    [DataRow("$[*", '.', 3, (byte)14)]
    [DataRow("$[**]", '*', 3, (byte)14)]
    [DataRow("$.**", '*', 3, (byte)14)]
    [DataRow("$.*x", 'x', 3, (byte)14)]
    [DataRow("$[0 to]", ']', 6, (byte)14)]
    [DataRow("$[last to]", ']', 9, (byte)14)]
    [DataRow("$[0 to *]", '*', 7, (byte)14)]
    [DataRow("$[0 to 1 to 2]", 't', 9, (byte)14)]
    [DataRow("$[0 1]", '1', 4, (byte)14)]
    [DataRow("$[0to2]", 't', 3, (byte)15)]
    [DataRow("$[0 to 2x]", 'x', 8, (byte)15)]
    [DataRow("$[0 TO 2]", 'T', 4, (byte)21)]
    [DataRow("$[0 tox 1]", 't', 4, (byte)21)]
    [DataRow("$[0 to1]", 't', 4, (byte)21)]
    [DataRow("$[1 x]", 'x', 4, (byte)21)]
    [DataRow("$[0 to 1 x]", 'x', 9, (byte)21)]
    [DataRow("$[last x]", 'x', 7, (byte)21)]
    [DataRow("$[last - 1]", '-', 7, (byte)21)]
    [DataRow("$[LAST]", 'L', 2, (byte)21)]
    [DataRow("$[lastx]", 'l', 2, (byte)21)]
    [DataRow("$[last1]", 'l', 2, (byte)21)]
    [DataRow("$[l]", 'l', 2, (byte)21)]
    [DataRow("append $.b", 'a', 0, (byte)14)]
    public void MalformedAccessor(string path, char character, int position, byte state) =>
        AssertError(
            $"declare @j json = '[1]'; select json_contains(@j, 1, '{path}')",
            13607,
            state,
            $"JSON path is not properly formatted. Unexpected character '{character}' is found at position {position}.");

    [TestMethod]
    [DataRow("declare @j json = '[1,2,3]'; select json_contains(@j, 1, '$[2 to 0]')")]
    [DataRow("select json_value('{\"p\":[1]}', '$.p[2 to 0]')")]
    public void ReversedRange(string commandText) =>
        AssertError(commandText, 13660, 1, "Reversed indexing not yet supported for advanced JSON array accessors.");

    [TestMethod]
    public void ReversedRangeWaitsForADocument() =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("declare @j nvarchar(max); select json_value(@j, '$.p[2 to 1]')"));

    [TestMethod]
    [DataRow("$.p[last]", "3")]
    [DataRow("$.p[0 to 0]", "1")]
    [DataRow("strict $.p[last]", "3")]
    [DataRow("strict $.p[0 to 0]", "1")]
    public void JsonValueTakesOneSelectedValue(string path, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($$"""declare @j json = '{"p":[1,2,3]}'; select json_value(@j, '{{path}}')"""));

    [TestMethod]
    [DataRow("json_value(@j, '$.p[*]')")]
    [DataRow("json_value(@j, '$.p[1, 2]')")]
    [DataRow("json_query(@j, '$.q[*]')")]
    [DataRow("json_query(@j, '$.q[1, 1]')")]
    public void SeveralSelectedValuesAreNull(string call) =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($$"""declare @j json = '{"p":[1,2,3],"q":[[1],[2]]}'; select {{call}}"""));

    [TestMethod]
    [DataRow("json_value(@j, 'strict $.p[*]')", 13623, (byte)2)]
    [DataRow("json_query(@j, 'strict $.q[*]')", 13624, (byte)2)]
    [DataRow("json_value(@j, 'strict $.e[*]')", 13608, (byte)5)]
    [DataRow("json_value(@j, 'strict $.p[3 to 5]')", 13608, (byte)5)]
    public void StrictAdvancedPathsOverJson(string call, int number, byte state)
    {
        var ex = new Simulation().AssertSqlError($$"""declare @j json = '{"p":[1,2,3],"q":[[1],[2]],"e":[]}'; select {{call}}""", number);
        AreEqual(state, ex.Errors[0].State);
    }

    [TestMethod]
    public void JsonQueryTakesOneSelectedContainer() =>
        AreEqual("[2]", new Simulation().ExecuteScalar("""declare @j json = '{"q":[[1],[2]]}'; select json_query(@j, '$.q[last]')"""));

    [TestMethod]
    [DataRow("json_value(@j, '$.p[0 to 1]')")]
    [DataRow("json_value(@j, '$.p[*]')")]
    public void TextDocumentsTakeRangesAndWildcards(string call) =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($$"""declare @j nvarchar(max) = N'{"p":[1,2,3]}'; select {{call}}"""));

    [TestMethod]
    public void TextDocumentStrictMultipleValues() =>
        AssertError("""declare @j nvarchar(max) = N'{"p":[1,2,3]}'; select json_value(@j, 'strict $.p[*]')""", 13608, 2, "Property cannot be found on the specified JSON path.");

    [TestMethod]
    [DataRow("json_value(@j, '$.p[last]')", "Last operator", (byte)2)]
    [DataRow("json_path_exists(@j, '$.p[last]')", "Last operator", (byte)2)]
    [DataRow("json_value(@j, '$.p[1, 1]')", "Comma operator", (byte)5)]
    [DataRow("json_query(@j, '$.p[1, 1]')", "Comma operator", (byte)5)]
    [DataRow("json_path_exists(@j, '$.p[1, 9]')", "Comma operator", (byte)5)]
    [DataRow("json_modify(@j, '$.p[last]', 9)", "Last operator", (byte)2)]
    [DataRow("json_modify(@j, '$.p[*].a[last]', 9)", "Last operator", (byte)2)]
    [DataRow("json_modify(@j, '$.p[0, 1]', 9)", "Comma operator", (byte)5)]
    [DataRow("json_modify(@j, '$.p[*]', 9)", "JsonModify", (byte)4)]
    [DataRow("json_modify(@j, '$.p[0 to 1]', 9)", "JsonModify", (byte)4)]
    [DataRow("(select count(*) from openjson(@j, '$.p[*]'))", "OpenJson with default schema", (byte)2)]
    [DataRow("(select x from openjson(@j) with (x int '$.p[last]'))", "Last operator", (byte)2)]
    [DataRow("(select x from openjson(@j) with (x int '$.p[1,2]'))", "Comma operator", (byte)5)]
    public void TextDocumentRefusals(string call, string what, byte state) =>
        AssertError(
            $$"""declare @j nvarchar(max) = N'{"p":[1,2,3]}'; select {{call}}""",
            13660,
            state,
            $"{what} not yet supported for advanced JSON array accessors.");

    [TestMethod]
    public void TextDocumentRefusalWaitsForADocument() =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("declare @j nvarchar(max); select json_value(@j, '$.p[1,2]')"));

    [TestMethod]
    [DataRow("""{"p":[1,2,3],"o":{}}""", "$.p[last]", 1)]
    [DataRow("""{"p":[1,2,3],"o":{}}""", "$.p[5 to 7]", 0)]
    [DataRow("""{"p":[1,2,3],"o":{}}""", "$.p[1,9]", 1)]
    [DataRow("""{"p":[1,2,3],"o":{}}""", "$.p[2 to 9]", 1)]
    [DataRow("""{"p":[1,2,3],"o":{}}""", "$.o[*]", 0)]
    [DataRow("""{"p":[1,2,3],"o":{}}""", "$.q[*]", 0)]
    [DataRow("""{"a":[]}""", "$.a[*]", 1)]
    [DataRow("""{"a":[]}""", "$.a[*].x", 0)]
    [DataRow("""[{"x":1},{"y":2}]""", "$[*].x", 1)]
    [DataRow("""[{"x":1},{"y":2}]""", "$[*].z", 0)]
    [DataRow("{}", "$.*", 1)]
    [DataRow("[]", "$[last]", 0)]
    [DataRow("[1,2]", "$[last to 0]", 1)]
    public void JsonPathExists(string document, string path, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"declare @j json = '{document}'; select json_path_exists(@j, '{path}')"));

    [TestMethod]
    public void JsonPathExistsOverText() =>
        AreEqual(1, new Simulation().ExecuteScalar("""declare @j nvarchar(max) = N'{"p":[1,2,3]}'; select json_path_exists(@j, '$.p[*]')"""));

    [TestMethod]
    [DataRow("""{"p":[1,2,3]}""", "$.p[0 to 0]", """{"p":[9,2,3]}""")]
    [DataRow("""{"p":[1,2,3]}""", "$.p[last]", """{"p":[9,2,3]}""")]
    [DataRow("""{"p":[1,2,3]}""", "$.p[0, 1]", """{"p":[1,2,3]}""")]
    [DataRow("""{"p":[{"a":1},{"a":2}]}""", "$.p[last].a", """{"p":[{"a":9},{"a":2}]}""")]
    public void JsonModifyOverJsonWritesOneElement(string document, string path, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"declare @j json = '{document}'; select cast(json_modify(@j, '{path}', 9) as nvarchar(max))"));

    [TestMethod]
    [DataRow("$.p[*]")]
    [DataRow("$.*")]
    [DataRow("$.p[0 to 1]")]
    public void JsonModifyOverJsonRefusesManyValuedPaths(string path) =>
        AssertError($$"""declare @j json = '{"p":[1,2,3]}'; select json_modify(@j, '{{path}}', 9)""", 13660, 5, "JsonModify not yet supported for advanced JSON array accessors.");

    [TestMethod]
    [DataRow("strict $.p[0, 1]")]
    [DataRow("strict $.e[last]")]
    public void JsonModifyStrictMisses(string path) =>
        AssertError($$"""declare @j json = '{"p":[1,2,3],"e":[]}'; select json_modify(@j, '{{path}}', 9)""", 13608, 5, "Property cannot be found on the specified JSON path.");

    [TestMethod]
    public void JsonModifyLastOfAnEmptyArrayLeavesTheDocument() =>
        AreEqual("""{"e":[]}""", new Simulation().ExecuteScalar("""declare @j json = '{"e":[]}'; select cast(json_modify(@j, '$.e[last]', 9) as nvarchar(max))"""));

    [TestMethod]
    [DataRow("select count(*) from openjson(@j, '$.p[*]')", (byte)5)]
    [DataRow("select count(*) from openjson(@j, '$.*')", (byte)5)]
    [DataRow("select x from openjson(@j) with (x int '$.p[*]')", (byte)3)]
    [DataRow("select x from openjson(@j) with (x int '$.*')", (byte)3)]
    public void OpenJsonWildcardsOverJson(string query, byte state) =>
        AssertError($$"""declare @j json = '{"p":[1,2,3]}'; {{query}}""", 13665, state, "OpenJson support with complex path parameters not yet supported for JSON native data type.");

    [TestMethod]
    public void OpenJsonOpensTheOneSelectedValue() =>
        AreEqual("6,7", new Simulation().ExecuteScalar("""declare @j json = '{"p":[5,[6,7]]}'; select string_agg(value, ',') from openjson(@j, '$.p[last]')"""));

    [TestMethod]
    public void OpenJsonColumnReadsTheFirstSelectedValue() =>
        AreEqual("7|5", new Simulation().ExecuteScalar("""declare @j json = '{"p":[5,6,7]}'; select concat(x, '|', y) from openjson(@j) with (x int '$.p[last]', y int '$.p[0 to 0]')"""));

    [TestMethod]
    public void OpenJsonColumnOverTextReadsTheFirstSelectedValue() =>
        AreEqual("5|6", new Simulation().ExecuteScalar("""declare @j nvarchar(max) = N'{"p":[5,6,7]}'; select concat(x, '|', y, z) from openjson(@j) with (x int '$.p[*]', y int '$.p[1 to 2]', z nvarchar(max) '$.p[*]' as json)"""));

    [TestMethod]
    [DataRow("json_value(@j, 'append $.a')")]
    [DataRow("json_query(@j, 'append $.a')")]
    [DataRow("json_path_exists(@j, 'append $.a')")]
    public void AppendPrefixOutsideJsonModify(string call) =>
        AssertError($"declare @j nvarchar(max) = N'{{\"a\":[1]}}'; select {call}", 13607, 14, "JSON path is not properly formatted. Unexpected character 'a' is found at position 0.");
}
