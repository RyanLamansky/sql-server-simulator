using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// <c>JSON_MODIFY</c> edits the document's own text rather than rewriting it:
/// every byte the edit didn't touch — the input's whitespace, its key order,
/// its escaping — comes back as written. All expectations probe-confirmed
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class JsonModifyEditTests
{
    /// <summary>The whitespace inside and around the document both survive.</summary>
    [TestMethod]
    public void Replace_KeepsEverySpaceTheEditDidNotTouch()
        => AreEqual("  {\"a\" : 2}  ", ExecuteScalar("select json_modify('  {\"a\" : 1}  ', '$.a', 2)"));

    [TestMethod]
    public void Replace_NestedLeaf_LeavesTheRestAsWritten()
        => AreEqual("{ \"a\" : 1 , \"b\" : { \"c\" : 9 } }",
            ExecuteScalar("select json_modify('{ \"a\" : 1 , \"b\" : { \"c\" : 2 } }', '$.b.c', 9)"));

    /// <summary>The replaced span is the whole value, container and all.</summary>
    [TestMethod]
    public void Replace_ObjectValued_SwapsTheWholeSubtree()
        => AreEqual("{ \"a\" : \"txt\" }",
            ExecuteScalar("select json_modify('{ \"a\" : { \"b\" : 1 } }', '$.a', 'txt')"));

    [TestMethod]
    public void Replace_ArrayElement_InPlace()
        => AreEqual("[ 1 , 9 , 3 ]", ExecuteScalar("select json_modify('[ 1 , 2 , 3 ]', '$[1]', 9)"));

    /// <summary>Writing a value back over itself is byte-identical.</summary>
    [TestMethod]
    public void Replace_SameValue_IsByteIdentical()
        => AreEqual("{ \"a\" : 1 }", ExecuteScalar("select json_modify('{ \"a\" : 1 }', '$.a', 1)"));

    [TestMethod]
    public void Replace_MultilineDocument_KeepsItsLayout()
        => AreEqual("{\n  \"a\": 2\n}",
            ExecuteScalar("select json_modify(char(123) + char(10) + '  \"a\": 1' + char(10) + char(125), '$.a', 2)"));

    /// <summary>
    /// Whatever spacing convention the document uses, the inserted member is
    /// canonical: a comma, the quoted key, a colon, the value.
    /// </summary>
    [TestMethod]
    [DataRow("{\"a\":1}", "{\"a\":1,\"b\":2}")]
    [DataRow("{ \"a\" : 1 }", "{ \"a\" : 1 ,\"b\":2}")]
    [DataRow("{}", "{\"b\":2}")]
    [DataRow("{  }", "{  \"b\":2}")]
    public void Insert_GoesImmediatelyBeforeTheClosingBrace(string document, string expected)
        => AreEqual(expected, ExecuteScalar($"select json_modify('{document}', '$.b', 2)"));

    [TestMethod]
    public void Insert_NestedObject_JoinsThatObject()
        => AreEqual("{ \"a\" : { \"b\" : 1 ,\"c\":2} }",
            ExecuteScalar("select json_modify('{ \"a\" : { \"b\" : 1 } }', '$.a.c', 2)"));

    /// <summary>A property path over an array, or an index path over an object, names nothing.</summary>
    [TestMethod]
    [DataRow("[ 1 ]", "$.b")]
    [DataRow("{ \"a\" : 1 }", "$[0]")]
    [DataRow("{}", "$.x.y")]
    public void Insert_PathCannotApply_ReturnsTheInputVerbatim(string document, string path)
        => AreEqual(document, ExecuteScalar($"select json_modify('{document}', '{path}', 2)"));

    /// <summary>A NULL value has nothing to add, so a missing key stays missing.</summary>
    [TestMethod]
    public void Insert_NullValue_ReturnsTheInputVerbatim()
        => AreEqual("  { \"a\" : 1 }  ", ExecuteScalar("select json_modify('  { \"a\" : 1 }  ', '$.b', null)"));

    /// <summary>
    /// A deleted member takes the comma before it, or — when it is the
    /// container's first — the comma after it. The whitespace on the comma's
    /// far side stays put either way.
    /// </summary>
    [TestMethod]
    [DataRow("{\"a\":1,\"b\":2}", "$.a", "{\"b\":2}")]
    [DataRow("{\"a\":1,\"b\":2}", "$.b", "{\"a\":1}")]
    [DataRow("{\"a\":1,\"b\":2,\"c\":3}", "$.b", "{\"a\":1,\"c\":3}")]
    [DataRow("{ \"a\" : 1 , \"b\" : 2 }", "$.a", "{  \"b\" : 2 }")]
    [DataRow("{ \"a\" : 1 , \"b\" : 2 }", "$.b", "{ \"a\" : 1  }")]
    [DataRow("{ \"a\" : 1 , \"b\" : 2 , \"c\" : 3 }", "$.b", "{ \"a\" : 1  , \"c\" : 3 }")]
    [DataRow("{ \"a\" : 1 }", "$.a", "{  }")]
    [DataRow("{ \"a\" : { \"b\" : 1 } , \"c\" : 2 }", "$.a", "{  \"c\" : 2 }")]
    public void Delete_TakesTheMemberAndOneComma(string document, string path, string expected)
        => AreEqual(expected, ExecuteScalar($"select json_modify('{document}', '{path}', null)"));

    /// <summary>An array element has no name to drop, so a NULL writes JSON null into its slot.</summary>
    [TestMethod]
    public void Delete_ArrayElement_WritesJsonNull()
        => AreEqual("[ 1 , null , 3 ]", ExecuteScalar("select json_modify('[ 1 , 2 , 3 ]', '$[1]', null)"));

    /// <summary>Removing a key is a lax-mode reading of NULL; strict takes it as the value.</summary>
    [TestMethod]
    public void Delete_StrictPath_WritesJsonNullInstead()
        => AreEqual("{ \"a\" : null , \"b\" : 2 }",
            ExecuteScalar("select json_modify('{ \"a\" : 1 , \"b\" : 2 }', 'strict $.a', null)"));

    [TestMethod]
    [DataRow("{\"a\":[1,2]}", "{\"a\":[1,2,3]}")]
    [DataRow("{ \"a\" : [ 1 , 2 ] }", "{ \"a\" : [ 1 , 2 ,3] }")]
    [DataRow("{ \"a\" : [ ] }", "{ \"a\" : [ 3] }")]
    public void Append_GoesImmediatelyBeforeTheClosingBracket(string document, string expected)
        => AreEqual(expected, ExecuteScalar($"select json_modify('{document}', 'append $.a', 3)"));

    /// <summary>A segment-less <c>append $</c> reaches the root array.</summary>
    [TestMethod]
    public void Append_RootArray()
        => AreEqual("[ 1 ,2]", ExecuteScalar("select json_modify('[ 1 ]', 'append $', 2)"));

    /// <summary>An append onto a key the object lacks creates it holding a one-element array.</summary>
    [TestMethod]
    public void Append_MissingKey_CreatesTheArray()
        => AreEqual("{ \"a\" : 1 ,\"b\":[3]}", ExecuteScalar("select json_modify('{ \"a\" : 1 }', 'append $.b', 3)"));

    /// <summary>Which is where a NULL lands too, rather than being nothing to add.</summary>
    [TestMethod]
    public void Append_MissingKey_NullValue_CreatesTheArrayHoldingNull()
        => AreEqual("{ \"a\" : 1 ,\"b\":[null]}", ExecuteScalar("select json_modify('{ \"a\" : 1 }', 'append $.b', null)"));

    [TestMethod]
    public void Append_NullValue_AppendsJsonNull()
        => AreEqual("{ \"a\" : [ 1 ,null] }", ExecuteScalar("select json_modify('{ \"a\" : [ 1 ] }', 'append $.a', null)"));

    /// <summary>Nothing to append onto: a non-array target, an object root, an index past the end.</summary>
    [TestMethod]
    [DataRow("{ \"a\" : 1 }", "append $.a")]
    [DataRow("{ \"a\" : { \"b\" : 1 } }", "append $.a")]
    [DataRow("{ \"a\" : 1 }", "append $")]
    [DataRow("{ \"a\" : 1 }", "append $.b.c")]
    [DataRow("[ 1 ]", "append $[5]")]
    public void Append_NoArrayToJoin_ReturnsTheInputVerbatim(string document, string path)
        => AreEqual(document, ExecuteScalar($"select json_modify('{document}', '{path}', 3)"));

    /// <summary>Under strict, a present-but-not-an-array target is Msg 13621 rather than a no-op.</summary>
    [TestMethod]
    public void Append_StrictNonArray_RaisesMsg13621()
        => AssertSqlError("select json_modify('{ \"a\" : 1 }', 'append strict $.a', 3)", 13621,
            "Array cannot be found in the specified JSON path.");

    /// <summary>The prefix leads the path: <c>append</c> then the optional mode keyword.</summary>
    [TestMethod]
    public void Append_PrecedesTheModeKeyword()
        => AreEqual("{ \"a\" : [1,3] }", ExecuteScalar("select json_modify('{ \"a\" : [1] }', 'append lax $.a', 3)"));

    [TestMethod]
    [DataRow("strict append $.a")]
    [DataRow("lax append $.a")]
    [DataRow("append append $.a")]
    public void Append_OutOfOrder_RaisesMsg13607(string path)
        => AssertSqlError($"select json_modify('{{ \"a\" : [1] }}', '{path}', 3)", 13607);

    /// <summary>Only JSON_MODIFY takes the prefix.</summary>
    [TestMethod]
    [DataRow("json_value('{\"a\":[1]}', 'append $.a')")]
    [DataRow("json_query('{\"a\":[1]}', 'append $.a')")]
    [DataRow("json_path_exists('{\"a\":[1]}', 'append $.a')")]
    public void Append_OnAnotherFunction_RaisesMsg13607(string expression)
        => AssertSqlError($"select {expression}", 13607);

    /// <summary>
    /// <c>$</c> on its own names the whole document, which leaves JSON_MODIFY
    /// no slot to write into.
    /// </summary>
    [TestMethod]
    [DataRow("$")]
    [DataRow("lax $")]
    [DataRow("strict $")]
    public void RootPath_RaisesMsg13619(string path)
        => AssertSqlError($"select json_modify('{{\"a\":1}}', '{path}', 2)", 13619,
            "Unsupported JSON path found in argument 2 of JSON_MODIFY.");

    /// <summary>JSON_MODIFY's Msg 13608 carries State 2, where JSON_VALUE / JSON_QUERY report State 1.</summary>
    [TestMethod]
    [DataRow("json_modify('{\"a\":1}', 'strict $.b', 2)")]
    [DataRow("json_modify('[1]', 'strict $[3]', 2)")]
    [DataRow("json_modify('{}', 'strict $.x.y', 1)")]
    [DataRow("json_modify('[1]', 'append strict $[5]', 2)")]
    public void StrictMiss_RaisesMsg13608State2(string expression)
        => AreEqual(2, new Simulation().AssertSqlError($"select {expression}", 13608).State);

    /// <summary>
    /// The substituted value is rendered canonically: strings quoted and
    /// escaped (forward slash included, which real escapes here), numbers and
    /// booleans bare.
    /// </summary>
    [TestMethod]
    [DataRow("'y\"z'", "\"y\\\"z\"")]
    [DataRow("'a/b'", "\"a\\/b\"")]
    [DataRow("'{\"x\":1}'", "\"{\\\"x\\\":1}\"")]
    [DataRow("'a<b>&c'", "\"a<b>&c\"")]
    [DataRow("char(8) + char(12) + char(13) + char(10) + char(9)", "\"\\b\\f\\r\\n\\t\"")]
    [DataRow("char(27) + char(1)", "\"\\u001b\\u0001\"")]
    [DataRow("2.0", "2.0")]
    [DataRow("cast(1.50 as decimal(10, 2))", "1.50")]
    [DataRow("cast(123456789012345 as bigint)", "123456789012345")]
    [DataRow("-5", "-5")]
    [DataRow("cast(1 as bit)", "true")]
    public void SubstitutedValue_RendersCanonically(string value, string expected)
        => AreEqual($"{{ \"a\" : {expected} }}", ExecuteScalar($"select json_modify('{{ \"a\" : 1 }}', '$.a', {value})"));

    /// <summary>Non-ASCII stays literal rather than escaping to <c>\uHHHH</c>.</summary>
    [TestMethod]
    public void SubstitutedValue_NonAscii_StaysLiteral()
        => AreEqual("{ \"a\" : \"café\" }", ExecuteScalar("select json_modify('{ \"a\" : 1 }', '$.a', N'café')"));

    /// <summary>An inserted key is written the same way, from the path's own text.</summary>
    [TestMethod]
    public void InsertedKey_RendersFromThePath()
        => AreEqual("{\"a\":1,\"café\":2}", ExecuteScalar("select json_modify('{\"a\":1}', N'$.\"café\"', 2)"));

    /// <summary>
    /// A JSON-producing third argument embeds raw — the same detection the
    /// JSON_OBJECT / JSON_ARRAY builders use, so the nested text arrives with
    /// its own spacing intact.
    /// </summary>
    [TestMethod]
    [DataRow("json_query('{ \"z\" : 3 }')", "{ \"z\" : 3 }")]
    [DataRow("json_object('z': 1)", "{\"z\":1}")]
    [DataRow("json_array(1, 2)", "[1,2]")]
    [DataRow("json_modify('{\"q\":1}', '$.q', 2)", "{\"q\":2}")]
    public void SubstitutedValue_JsonProducer_EmbedsRaw(string value, string expected)
        => AreEqual($"{{ \"a\" : {expected} }}", ExecuteScalar($"select json_modify('{{ \"a\" : 1 }}', '$.a', {value})"));

    /// <summary>JSON_VALUE returns a string, so its result is quoted like any other.</summary>
    [TestMethod]
    public void SubstitutedValue_JsonValue_IsQuoted()
        => AreEqual("{ \"a\" : \"s\" }",
            ExecuteScalar("select json_modify('{ \"a\" : 1 }', '$.a', json_value('{\"q\":\"s\"}', '$.q'))"));

    /// <summary>The key an insert takes from the path leaves <c>/</c> literal.</summary>
    [TestMethod]
    public void InsertedKey_LeavesSolidusLiteral()
        => AreEqual("""{"a/b":"v"}""", ExecuteScalar("""select json_modify('{}', '$."a/b"', 'v')"""));

    // --- The written value's type (Msg 8116) ---

    /// <summary>
    /// The third argument takes the string family bar the legacy LOBs, the
    /// integer family, decimal / numeric, float, real and bit — and nothing
    /// else, a typed NULL of a refused type included (probe-confirmed against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cast(1 as money)", "money")]
    [DataRow("cast(1 as smallmoney)", "smallmoney")]
    [DataRow("cast('2020-01-01' as date)", "date")]
    [DataRow("cast('2020-01-01' as datetime)", "datetime")]
    [DataRow("cast('2020-01-01' as datetime2(3))", "datetime2")]
    [DataRow("cast('2020-01-01' as smalldatetime)", "smalldatetime")]
    [DataRow("cast('12:00' as time)", "time")]
    [DataRow("cast('2020-01-01' as datetimeoffset)", "datetimeoffset")]
    [DataRow("cast('00000000-0000-0000-0000-000000000000' as uniqueidentifier)", "uniqueidentifier")]
    [DataRow("cast(0x41 as varbinary(10))", "varbinary")]
    [DataRow("cast(0x41 as binary(1))", "binary")]
    [DataRow("cast('x' as text)", "text")]
    [DataRow("cast('x' as ntext)", "ntext")]
    [DataRow("cast('<a/>' as xml)", "xml")]
    [DataRow("cast(1 as sql_variant)", "sql_variant")]
    [DataRow("hierarchyid::Parse('/1/')", "hierarchyid")]
    [DataRow("geography::Point(1, 2, 4326)", "geography")]
    [DataRow("cast(null as date)", "date")]
    public void SubstitutedValue_RefusedType_RaisesMsg8116(string value, string typeName)
        => AssertSqlError($"select json_modify('{{\"a\":1}}', '$.a', {value})", 8116,
            $"Argument data type {typeName} is invalid for argument 3 of json_modify function.");

    /// <summary>Real binds the rule while compiling, so an empty rowset reports it too.</summary>
    [TestMethod]
    public void SubstitutedValue_RefusedType_ReportsOnAnEmptyRowset()
        => AssertSqlError("select json_modify('{\"a\":1}', '$.a', cast(null as date)) where 1 = 0", 8116,
            "Argument data type date is invalid for argument 3 of json_modify function.");

    /// <summary>The accepted set, each rendering its own JSON shape.</summary>
    [TestMethod]
    [DataRow("cast('x' as char(3))", "\"x  \"")]
    [DataRow("cast('x' as nchar(3))", "\"x  \"")]
    [DataRow("cast('x' as varchar(max))", "\"x\"")]
    [DataRow("cast(1.50 as numeric(10, 2))", "1.50")]
    [DataRow("cast(1 as tinyint)", "1")]
    [DataRow("cast(1 as bit)", "true")]
    public void SubstitutedValue_AcceptedType_Writes(string value, string expected)
        => AreEqual($"{{\"a\":{expected}}}", ExecuteScalar($"select json_modify('{{\"a\":1}}', '$.a', {value})"));

    /// <summary>
    /// An untyped <c>NULL</c> passes the gate, which is what leaves the
    /// delete-a-member form open.
    /// </summary>
    [TestMethod]
    public void SubstitutedValue_UntypedNull_Deletes()
        => AreEqual("{}", ExecuteScalar("select json_modify('{\"a\":1}', '$.a', null)"));
}
