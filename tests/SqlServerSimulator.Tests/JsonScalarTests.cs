using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>JSON_VALUE</c> and <c>JSON_MODIFY</c>
/// scalar functions. These cover the EF Core 10 owned-types-as-JSON
/// read + partial-update emissions and a few raw-SQL shapes documented
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class JsonScalarTests
{
    [TestMethod]
    public void JsonValue_PropertyAccess()
        => AreEqual("Springfield", ExecuteScalar("select json_value('{\"city\":\"Springfield\",\"zip\":\"12345\"}', '$.city')"));

    [TestMethod]
    public void JsonValue_MissingPath_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{\"city\":\"X\"}', '$.missing')"));

    [TestMethod]
    public void JsonValue_NullJson_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value(null, '$.x')"));

    [TestMethod]
    public void JsonValue_NullPath_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{\"x\":1}', null)"));

    [TestMethod]
    public void JsonValue_BooleanValue_ReturnsLowercaseLiteral()
        => AreEqual("true", ExecuteScalar("select json_value('{\"flag\":true}', '$.flag')"));

    [TestMethod]
    public void JsonValue_NumberValue_ReturnsRawText()
        => AreEqual("42", ExecuteScalar("select json_value('{\"n\":42}', '$.n')"));

    [TestMethod]
    public void JsonValue_NullJsonValue_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{\"n\":null}', '$.n')"));

    /// <summary>
    /// JSON_VALUE is nvarchar(4000); a scalar string exactly 4000 chars long
    /// returns intact (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void JsonValue_ScalarAtCap_ReturnsValue()
        => AreEqual(4000, ((string)ExecuteScalar("select json_value('{\"a\":\"' + replicate(cast('x' as varchar(max)), 4000) + '\"}', '$.a')")!).Length);

    /// <summary>
    /// A scalar string longer than 4000 chars yields NULL in the default lax
    /// mode (probe-confirmed: 4000 → value, 4001 → NULL). Enforcing the cap
    /// also keeps the length-0 result within the bounded wire length prefix.
    /// </summary>
    [TestMethod]
    public void JsonValue_ScalarOverCap_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{\"a\":\"' + replicate(cast('x' as varchar(max)), 4001) + '\"}', '$.a')"));

    /// <summary>Lax mode: scalar-only — non-scalar match yields NULL, not the JSON text.</summary>
    [TestMethod]
    public void JsonValue_ObjectMatch_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{\"obj\":{\"a\":1}}', '$.obj')"));

    [TestMethod]
    public void JsonValue_ArrayMatch_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{\"arr\":[1,2,3]}', '$.arr')"));

    [TestMethod]
    public void JsonValue_NestedPropertyAccess()
        => AreEqual("inner", ExecuteScalar("select json_value('{\"a\":{\"b\":{\"c\":\"inner\"}}}', '$.a.b.c')"));

    [TestMethod]
    public void JsonValue_ArrayIndex()
        => AreEqual("middle", ExecuteScalar("select json_value('{\"items\":[\"first\",\"middle\",\"last\"]}', '$.items[1]')"));

    /// <summary>
    /// EF Core 10 emits this exact shape when transferring a value through a
    /// parameter wrapped as <c>{"":"&lt;value&gt;"}</c> so JSON_VALUE handles
    /// parameter type detection without needing per-type SqlParameter typing.
    /// </summary>
    [TestMethod]
    public void JsonValue_QuotedPropertyEmpty_EfWrapShape()
        => AreEqual("Shelbyville", ExecuteScalar("select json_value('{\"\":\"Shelbyville\"}', '$.\"\"')"));

    /// <summary>
    /// A document that isn't JSON text raises Msg 13609 whatever the path's
    /// lax / strict prefix says — see <see cref="JsonMalformedTextTests"/> for
    /// the full rule.
    /// </summary>
    [TestMethod]
    public void JsonValue_InvalidJson_RaisesMsg13609()
        => AssertSqlError("select json_value('{not valid}', '$.x')", 13609,
            "JSON text is not properly formatted. Unexpected character 'n' is found at position 1.");

    [TestMethod]
    public void JsonValue_StrictPrefix_OnMissingPathRaises()
        => AssertSqlError("select json_value('{\"a\":1}', 'strict $.missing')", 13608,
            "Property cannot be found on the specified JSON path.");

    [TestMethod]
    public void JsonValue_LaxPrefix_ExplicitParses()
        => AreEqual("v", ExecuteScalar("select json_value('{\"a\":\"v\"}', 'lax $.a')"));

    [TestMethod]
    public void JsonValue_InvalidPath_RaisesMsg13607()
        => AssertSqlError("select json_value('{}', 'no-leading-dollar')", 13607,
            "JSON path is not properly formatted. Unexpected character 'n' is found at position 0.");

    // --- Path grammar: whitespace ---

    /// <summary>
    /// Whitespace separates the path's tokens and may sit between any two of
    /// them, trailing the path included — and a mode keyword needs none
    /// behind it at all. Space, tab, line feed, form feed and carriage return
    /// all count; vertical tab and the non-breaking space do not (all
    /// probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("' $.a '")]
    [DataRow("'$. a'")]
    [DataRow("'$ .a'")]
    [DataRow("'  lax   $.a   '")]
    [DataRow("'lax$.a'")]
    [DataRow("'strict$.a'")]
    [DataRow("char(9) + '$.a' + char(9)")]
    [DataRow("char(10) + '$.a' + char(13)")]
    [DataRow("char(12) + '$.a'")]
    public void JsonValue_PathWhitespace_Tolerated(string path)
        => AreEqual("1", ExecuteScalar($"select json_value('{{\"a\":1}}', {path})"));

    /// <summary>Whitespace sits between segments and inside an index alike.</summary>
    [TestMethod]
    [DataRow("$.a . b", "7")]
    [DataRow("$.a . c[ 0 ]", "9")]
    [DataRow("$ . a . b", "7")]
    public void JsonValue_PathWhitespace_BetweenSegments(string path, string expected)
        => AreEqual(expected, ExecuteScalar($"select json_value('{{\"a\":{{\"b\":7,\"c\":[9]}}}}', '{path}')"));

    /// <summary>Trailing whitespace reaches JSON_MODIFY's edit too.</summary>
    [TestMethod]
    public void JsonModify_PathWhitespace_Edits()
        => AreEqual("{ \"a\" : 2 }", ExecuteScalar("select json_modify('{ \"a\" : 1 }', ' $.a ', 2)"));

    /// <summary>A keyword only counts as one when the word ends there.</summary>
    [TestMethod]
    public void JsonValue_KeywordRunOn_IsMalformed()
        => new Simulation().AssertSqlError("select json_value('{\"a\":1}', 'laxx$.a')", 13607,
            "JSON path is not properly formatted. Unexpected character 'l' is found at position 0.");

    // --- Path grammar: Msg 13607's character, position and State ---

    /// <summary>
    /// Msg 13607 names the character it stopped on and its zero-based index,
    /// with <c>.</c> at the path's length standing in for running off the
    /// end. The State byte names what the parser was reading: 22 where the
    /// <c>$</c> or a segment was due, 21 inside <c>[</c> before the digits,
    /// 15 inside <c>[</c> after them, 16 for an index above real's
    /// <c>uint</c> ceiling, 20 for a quoted name the path never closed, and
    /// 14 for the end of the path — which the grammar's own punctuation, the
    /// digits, and anything at all behind a quoted name report too. Every row
    /// probed verbatim against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("xyz", 'x', 0, 22)]
    [DataRow("$x", 'x', 1, 22)]
    [DataRow("$ x", 'x', 2, 22)]
    [DataRow("$.a b", 'b', 4, 22)]
    [DataRow("$.a!", '!', 3, 22)]
    [DataRow("$.a-b", '-', 3, 22)]
    [DataRow("$.-a", '-', 2, 22)]
    [DataRow("$.a[1]x", 'x', 6, 22)]
    [DataRow("$[0]y", 'y', 4, 22)]
    [DataRow("$$", '$', 1, 14)]
    [DataRow("$.$", '$', 2, 14)]
    [DataRow("$..a", '.', 2, 14)]
    [DataRow("$.9", '9', 2, 14)]
    [DataRow("$.[", '[', 2, 14)]
    [DataRow("$.a$b", '$', 3, 14)]
    [DataRow("$[0]1", '1', 4, 14)]
    [DataRow("$.a[]", ']', 4, 14)]
    [DataRow("$.a[ ]", ']', 5, 14)]
    [DataRow("$[\"a\"]", '"', 2, 14)]
    [DataRow("$.\"a\"x", 'x', 5, 14)]
    [DataRow("$.\"a\" x", 'x', 6, 14)]
    [DataRow("$.\"\"x", 'x', 4, 14)]
    [DataRow("$[a]", 'a', 2, 21)]
    [DataRow("$.a[-1]", '-', 4, 21)]
    [DataRow("$[1x]", 'x', 3, 15)]
    [DataRow("$[1$]", '$', 3, 14)]
    [DataRow("$.\"a", '.', 4, 20)]
    [DataRow("", '.', 0, 14)]
    [DataRow("$.", '.', 2, 14)]
    [DataRow("$[", '.', 2, 14)]
    [DataRow("$[0", '.', 3, 14)]
    [DataRow("$.a.", '.', 4, 14)]
    [DataRow("$.\"a\".", '.', 6, 14)]
    [DataRow("lax ", '.', 4, 14)]
    [DataRow("$[4294967296]", '6', 11, 16)]
    [DataRow("$[99999999999]", '9', 12, 16)]
    [DataRow("$[99999999999999999999]", '9', 12, 16)]
    public void JsonValue_MalformedPath_ReportsCharacterPositionAndState(string path, char character, int position, int state)
    {
        var ex = new Simulation().AssertSqlError($"select json_value('{{\"a\":1}}', '{path}')", 13607);
        AreEqual($"JSON path is not properly formatted. Unexpected character '{character}' is found at position {position}.", ex.Message);
        AreEqual((byte)state, ex.State);
    }

    /// <summary>
    /// An index real's <c>uint</c> ceiling still admits resolves — past every
    /// array there is, so the answer is NULL rather than an error.
    /// </summary>
    [TestMethod]
    [DataRow("$[2147483648]")]
    [DataRow("$[4294967295]")]
    public void JsonValue_HugeIndex_ResolvesToNull(string path)
        => IsInstanceOfType<DBNull>(ExecuteScalar($"select json_value('[1]', '{path}')"));

    /// <summary>Every function's path goes through the one parser.</summary>
    [TestMethod]
    [DataRow("select json_query('{\"a\":1}', '$.a b')")]
    [DataRow("select json_modify('{\"a\":1}', '$.a b', 2)")]
    [DataRow("select json_path_exists('{\"a\":1}', '$.a b')")]
    [DataRow("select * from openjson('{\"a\":1}') with (v int '$.a b')")]
    public void MalformedPath_ReportsTheSameWayEverywhere(string sql)
        => new Simulation().AssertSqlError(sql, 13607,
            "JSON path is not properly formatted. Unexpected character 'b' is found at position 4.");

    [TestMethod]
    public void JsonModify_ReplaceExistingProperty()
        => AreEqual("{\"city\":\"New\"}", ExecuteScalar("select json_modify('{\"city\":\"Old\"}', '$.city', 'New')"));

    [TestMethod]
    public void JsonModify_ReplaceNestedProperty()
        => AreEqual("{\"a\":{\"b\":\"Y\"}}", ExecuteScalar("select json_modify('{\"a\":{\"b\":\"X\"}}', '$.a.b', 'Y')"));

    [TestMethod]
    public void JsonModify_AppendToArray()
        => AreEqual("[1,2,3,4]", ExecuteScalar("select json_modify('[1,2,3]', 'append $', 4)"));

    /// <summary>
    /// An index at or past the array's end names no slot, so the document
    /// comes back untouched — appending is <c>append</c>'s job.
    /// </summary>
    [TestMethod]
    [DataRow("$[3]")]
    [DataRow("$[9]")]
    public void JsonModify_IndexPastEnd_IsANoOp(string path)
        => AreEqual("[1,2,3]", ExecuteScalar($"select json_modify('[1,2,3]', '{path}', 4)"));

    [TestMethod]
    public void JsonModify_ReplaceArrayElement()
        => AreEqual("[1,99,3]", ExecuteScalar("select json_modify('[1,2,3]', '$[1]', 99)"));

    [TestMethod]
    public void JsonModify_LaxMissing_AddsProperty()
        => AreEqual("{\"a\":1,\"b\":2}", ExecuteScalar("select json_modify('{\"a\":1}', '$.b', 2)"));

    [TestMethod]
    public void JsonModify_LaxNullValue_RemovesExistingProperty()
        => AreEqual("{\"a\":1}", ExecuteScalar("select json_modify('{\"a\":1,\"b\":2}', '$.b', null)"));

    [TestMethod]
    public void JsonModify_StrictMissing_RaisesMsg13608()
        => AssertSqlError("select json_modify('{\"a\":1}', 'strict $.b', 'x')", 13608,
            "Property cannot be found on the specified JSON path.");

    [TestMethod]
    public void JsonModify_StrictArrayOob_RaisesMsg13608()
        => AssertSqlError("select json_modify('[1,2]', 'strict $[5]', 99)", 13608);

    [TestMethod]
    public void JsonModify_NullJson_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_modify(null, '$.x', 1)"));

    [TestMethod]
    public void JsonModify_NumericValue_StaysJsonNumber()
        => AreEqual("{\"n\":42}", ExecuteScalar("select json_modify('{\"n\":0}', '$.n', 42)"));

    [TestMethod]
    public void JsonModify_BooleanValue_StaysJsonBoolean()
        => AreEqual("{\"flag\":true}", ExecuteScalar("select json_modify('{\"flag\":false}', '$.flag', cast(1 as bit))"));

    // The EF Core 10 SaveChanges shape for partial-update of an owned-as-JSON
    // scalar property: JSON_MODIFY with strict path + JSON_VALUE-from-parameter.
    [TestMethod]
    public void JsonModify_EfPartialUpdateShape()
    {
        var simulation = new Simulation();
        AreEqual(
            "{\"City\":\"Shelbyville\",\"Street\":\"1 Main\"}",
            simulation.ExecuteScalar("select json_modify('{\"City\":\"Springfield\",\"Street\":\"1 Main\"}', 'strict $.City', json_value('{\"\":\"Shelbyville\"}', '$.\"\"'))"));
    }

    [TestMethod]
    public void JsonQuery_ObjectMatch_ReturnsRawJson()
        => AreEqual("{\"a\":1,\"b\":2}", new Simulation().ExecuteScalar("select json_query('{\"obj\":{\"a\":1,\"b\":2}}', '$.obj')"));

    [TestMethod]
    public void JsonQuery_ArrayMatch_ReturnsRawJson()
        => AreEqual("[1,2,3]", new Simulation().ExecuteScalar("select json_query('{\"arr\":[1,2,3]}', '$.arr')"));

    /// <summary>Complement of <see cref="JsonValue_ObjectMatch_ReturnsNullLax"/>: JSON_QUERY returns NULL on scalar matches in lax mode.</summary>
    [TestMethod]
    public void JsonQuery_ScalarMatch_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select json_query('{\"n\":42}', '$.n')"));

    [TestMethod]
    public void JsonQuery_MissingPath_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select json_query('{\"x\":[1]}', '$.missing')"));

    [TestMethod]
    public void JsonQuery_NullJson_ReturnsNull()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select json_query(null, '$.x')"));

    [TestMethod]
    public void JsonQuery_NullPath_ReturnsNull()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select json_query('{\"x\":[1]}', null)"));

    /// <summary>
    /// The path argument is optional — <c>JSON_QUERY(json)</c> reads as
    /// <c>JSON_QUERY(json, '$')</c> and hands back the whole document. Real
    /// returns the input's own text, so interior whitespace survives while the
    /// padding outside the document does not.
    /// </summary>
    [TestMethod]
    [DataRow("json_query('{\"a\":1}')", "{\"a\":1}")]
    [DataRow("json_query('[1,2,3]')", "[1,2,3]")]
    [DataRow("json_query('  {\"a\" : 1 , \"b\":[1, 2]}  ')", "{\"a\" : 1 , \"b\":[1, 2]}")]
    public void JsonQuery_NoPath_ReturnsWholeDocument(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void JsonQuery_NoPath_NullJson_ReturnsNull()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select json_query(null)"));

    /// <summary>
    /// Only an object or an array is JSON text, so a root-level scalar is
    /// malformed input rather than a subtree-less document.
    /// </summary>
    [TestMethod]
    public void JsonQuery_NoPath_RootScalar_RaisesMsg13609()
        => new Simulation().AssertSqlError("select json_query('\"abc\"')", 13609,
            "JSON text is not properly formatted. Unexpected character '\"' is found at position 0.");

    /// <summary>The path-less form still embeds raw inside a JSON builder.</summary>
    [TestMethod]
    public void JsonQuery_NoPath_EmbedsRawInJsonObject()
        => AreEqual("{\"k\":{\"a\":1}}", new Simulation().ExecuteScalar("select json_object('k': json_query('{\"a\":1}'))"));

    /// <summary>Msg 189 names the accepted range verbatim, unlike JSON_VALUE's fixed-arity Msg 174.</summary>
    [TestMethod]
    public void JsonQuery_ThreeArguments_RaisesMsg189()
        => new Simulation().AssertSqlError(
            "select json_query('{\"a\":1}', '$', '$')",
            189,
            "The json_query function requires 1 to 2 arguments.");

    [TestMethod]
    public void JsonQuery_RoundTripThroughOpenJson()
        => AreEqual(
            "Spanish",
            new Simulation().ExecuteScalar("select [value] from openjson(json_query('{\"OtherLanguages\":[\"Spanish\"]}', '$.OtherLanguages'))"));

    /// <summary>ISJSON on a syntactically-valid JSON object returns 1.</summary>
    [TestMethod]
    public void IsJson_ValidObject_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select isjson('{\"a\":1,\"b\":\"x\"}')"));

    /// <summary>ISJSON on a syntactically-valid JSON array returns 1.</summary>
    [TestMethod]
    public void IsJson_ValidArray_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select isjson('[1,2,3]')"));

    /// <summary>ISJSON on garbage returns 0.</summary>
    [TestMethod]
    public void IsJson_InvalidText_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select isjson('not json at all')"));

    /// <summary>ISJSON on an unterminated object returns 0.</summary>
    [TestMethod]
    public void IsJson_UnterminatedObject_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select isjson('{\"a\":1')"));

    /// <summary>ISJSON on NULL input returns NULL.</summary>
    [TestMethod]
    public void IsJson_NullInput_ReturnsNull()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select isjson(cast(null as nvarchar(100)))"));

    /// <summary>The WWI CHECK-constraint shape: ISJSON(col)&lt;&gt;0 — should evaluate as the boolean predicate used in CK_*_Must_Be_Valid_JSON.</summary>
    [TestMethod]
    public void IsJson_BooleanPredicateShape_AcceptsValidJson()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, doc nvarchar(max) check (doc is null or isjson(doc) <> 0));
            insert t values (1, '{"valid":true}');
            insert t values (2, null);
            """);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>The CHECK constraint rejects garbage at INSERT.</summary>
    [TestMethod]
    public void IsJson_CheckConstraint_RejectsInvalidJson()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, doc nvarchar(max) check (doc is null or isjson(doc) <> 0));
            insert t values (1, 'not json')
            """, 547);
}
