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
        => AssertSqlError("select json_value('{}', 'no-leading-dollar')", 13607);

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
