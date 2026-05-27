using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>JSON_OBJECT</c> and <c>JSON_ARRAY</c> builders. Output strings probed
/// verbatim against SQL Server 2025: the default null clause is
/// builder-specific — <c>JSON_ARRAY</c> defaults to <c>ABSENT ON NULL</c> but
/// <c>JSON_OBJECT</c> defaults to <c>NULL ON NULL</c> (Microsoft documents this
/// explicitly; it is the opposite of the <c>FOR JSON</c> clause). Nested
/// <c>JSON_OBJECT</c> / <c>JSON_ARRAY</c> / <c>JSON_QUERY</c> results embed
/// raw; numbers / booleans render unquoted; varbinary base64-encodes;
/// datetime2 uses the <c>T</c>-separated ISO form.
/// </summary>
[TestClass]
public class JsonBuilderTests
{
    [TestMethod]
    public void JsonObject_Empty_ReturnsBraces()
        => AreEqual("{}", new Simulation().ExecuteScalar("select json_object()"));

    [TestMethod]
    public void JsonObject_SingleNumericValue()
        => AreEqual("{\"a\":1}", new Simulation().ExecuteScalar("select json_object('a': 1)"));

    [TestMethod]
    public void JsonObject_MultipleValues_Ordered()
        => AreEqual("{\"a\":1,\"b\":\"two\"}",
            new Simulation().ExecuteScalar("select json_object('a': 1, 'b': 'two')"));

    [TestMethod]
    public void JsonObject_DefaultIsNullOnNull()
        => AreEqual("{\"a\":1,\"b\":null}", new Simulation().ExecuteScalar(
            "select json_object('a': 1, 'b': cast(null as int))"));

    [TestMethod]
    public void JsonObject_NullOnNull_EmitsJsonNull()
        => AreEqual("{\"a\":1,\"b\":null}", new Simulation().ExecuteScalar(
            "select json_object('a': 1, 'b': cast(null as int) NULL ON NULL)"));

    [TestMethod]
    public void JsonObject_ExplicitAbsentOnNull_OmitsNulls()
        => AreEqual("{\"a\":1}", new Simulation().ExecuteScalar(
            "select json_object('a': 1, 'b': cast(null as int) ABSENT ON NULL)"));

    [TestMethod]
    public void JsonObject_NullKey_RaisesMsg13638()
        => new Simulation().AssertSqlError(
            "select json_object(cast(null as varchar): 1)", 13638);

    [TestMethod]
    public void JsonObject_DuplicateKeys_Preserved()
        => AreEqual("{\"a\":1,\"a\":2}",
            new Simulation().ExecuteScalar("select json_object('a': 1, 'a': 2)"));

    [TestMethod]
    public void JsonObject_NumericKey_CoercedToString()
        => AreEqual("{\"1\":\"hello\"}",
            new Simulation().ExecuteScalar("select json_object(1: 'hello')"));

    [TestMethod]
    public void JsonObject_KeyWithQuote_Escaped()
        => AreEqual("{\"with\\\"quote\":1}",
            new Simulation().ExecuteScalar("select json_object('with\"quote': 1)"));

    [TestMethod]
    public void JsonObject_NestedJsonObject_EmbedsRaw()
        => AreEqual("{\"nested\":{\"inner\":1}}",
            new Simulation().ExecuteScalar("select json_object('nested': json_object('inner': 1))"));

    [TestMethod]
    public void JsonObject_NestedJsonArray_EmbedsRaw()
        => AreEqual("{\"arr\":[1,2,3]}",
            new Simulation().ExecuteScalar("select json_object('arr': json_array(1,2,3))"));

    /// <summary>
    /// JSON_QUERY's path arg is optional in real SQL Server but required
    /// in the simulator — supply '$' explicitly to focus this test on
    /// JSON_OBJECT's raw-embed detection, not JSON_QUERY's signature.
    /// </summary>
    [TestMethod]
    public void JsonObject_JsonQueryResult_EmbedsRaw()
        => AreEqual("{\"s\":{\"x\":1}}",
            new Simulation().ExecuteScalar("select json_object('s': json_query('{\"x\":1}', '$'))"));

    [TestMethod]
    public void JsonObject_StringValueLookingLikeJson_StaysQuoted()
        => AreEqual("{\"s\":\"{\\\"x\\\":1}\"}",
            new Simulation().ExecuteScalar("select json_object('s': '{\"x\":1}')"));

    [TestMethod]
    public void JsonObject_BitTrue_RendersAsTrue()
        => AreEqual("{\"b\":true}",
            new Simulation().ExecuteScalar("select json_object('b': cast(1 as bit))"));

    [TestMethod]
    public void JsonObject_BitFalse_RendersAsFalse()
        => AreEqual("{\"b\":false}",
            new Simulation().ExecuteScalar("select json_object('b': cast(0 as bit))"));

    [TestMethod]
    public void JsonObject_Date_RendersIsoDate()
        => AreEqual("{\"d\":\"2025-01-15\"}",
            new Simulation().ExecuteScalar("select json_object('d': cast('2025-01-15' as date))"));

    [TestMethod]
    public void JsonObject_DateTime2_UsesTSeparator()
        => AreEqual("{\"dt\":\"2025-01-15T12:34:56\"}",
            new Simulation().ExecuteScalar(
                "select json_object('dt': cast('2025-01-15 12:34:56' as datetime2(0)))"));

    [TestMethod]
    public void JsonObject_Varbinary_BeansAsBase64()
        => AreEqual("{\"b\":\"QUI=\"}",
            new Simulation().ExecuteScalar("select json_object('b': cast(0x4142 as varbinary))"));

    [TestMethod]
    public void JsonObject_Tab_EscapedToBackslashT()
        => AreEqual("{\"k\":\"tab\\there\"}",
            new Simulation().ExecuteScalar("select json_object('k': 'tab' + char(9) + 'here')"));

    [TestMethod]
    public void JsonObject_Newline_EscapedToBackslashN()
        => AreEqual("{\"k\":\"newline\\nhere\"}",
            new Simulation().ExecuteScalar("select json_object('k': 'newline' + char(10) + 'here')"));

    [TestMethod]
    public void JsonObject_Backslash_DoubleEscaped()
        => AreEqual("{\"k\":\"backslash\\\\here\"}",
            new Simulation().ExecuteScalar("select json_object('k': 'backslash\\here')"));

    [TestMethod]
    public void JsonObject_MissingColon_Msg102()
        => new Simulation().AssertSqlError("select json_object('k')", 102);

    [TestMethod]
    public void JsonObject_TrailingComma_Msg102()
        => new Simulation().AssertSqlError("select json_object('k': 1, )", 102);

    [TestMethod]
    public void JsonObject_EqualsSeparator_Msg102()
        => new Simulation().AssertSqlError("select json_object('k' = 1)", 102);

    [TestMethod]
    public void JsonObject_PartialNullClause_Msg102()
        => new Simulation().AssertSqlError("select json_object('k': 1 NULL)", 102);

    [TestMethod]
    public void JsonObject_ComplexKeyExpression_Evaluated()
        => AreEqual("{\"key1\":42}",
            new Simulation().ExecuteScalar("select json_object('key' + cast(1 as varchar): 42)"));

    // --- JSON_ARRAY ---

    [TestMethod]
    public void JsonArray_Empty_ReturnsBrackets()
        => AreEqual("[]", new Simulation().ExecuteScalar("select json_array()"));

    [TestMethod]
    public void JsonArray_SingleValue()
        => AreEqual("[1]", new Simulation().ExecuteScalar("select json_array(1)"));

    [TestMethod]
    public void JsonArray_MixedValues()
        => AreEqual("[1,\"two\",3.0]",
            new Simulation().ExecuteScalar("select json_array(1, 'two', 3.0)"));

    [TestMethod]
    public void JsonArray_DefaultIsAbsentOnNull()
        => AreEqual("[1,3]", new Simulation().ExecuteScalar("select json_array(1, null, 3)"));

    [TestMethod]
    public void JsonArray_NullOnNull_KeepsNulls()
        => AreEqual("[1,null,3]",
            new Simulation().ExecuteScalar("select json_array(1, null, 3 NULL ON NULL)"));

    [TestMethod]
    public void JsonArray_ExplicitAbsentOnNull_OmitsNulls()
        => AreEqual("[1,3]",
            new Simulation().ExecuteScalar("select json_array(1, null, 3 ABSENT ON NULL)"));

    [TestMethod]
    public void JsonArray_NestedJsonObject_EmbedsRaw()
        => AreEqual("[{\"k\":\"v\"}]",
            new Simulation().ExecuteScalar("select json_array(json_object('k': 'v'))"));

    [TestMethod]
    public void JsonArray_NestedJsonArray_EmbedsRaw()
        => AreEqual("[[1,2],[3]]",
            new Simulation().ExecuteScalar("select json_array(json_array(1,2), json_array(3))"));

    // --- Result type ---

    [TestMethod]
    public void JsonObject_ResultTypeIsNVarchar()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select json_object('a': 1) as v").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(string), reader.GetFieldType(0));
    }
}
