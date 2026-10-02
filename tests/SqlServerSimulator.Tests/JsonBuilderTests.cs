using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

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

    // --- Solidus escaping ---

    /// <summary>
    /// Real escapes <c>/</c> as <c>\/</c> in a built string — in the value
    /// and in the key alike (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("json_object('a': 'a/b')", """{"a":"a\/b"}""")]
    [DataRow("json_object('k/1': 'v')", """{"k\/1":"v"}""")]
    [DataRow("json_array('a/b')", """["a\/b"]""")]
    [DataRow("json_array(json_object('a': 'http://x/y'))", """[{"a":"http:\/\/x\/y"}]""")]
    public void Builders_EscapeSolidus(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    // --- Declared decimal scale ---

    /// <summary>
    /// The builders write the raw numeric value rather than formatting from
    /// the declared type, so the trailing zeros SQL Server's <c>decimal</c>
    /// and <c>money</c> values carry reach the output — <c>cast(1 as
    /// numeric(10, 2))</c> is <c>1.00</c>, and every <c>money</c> value shows
    /// its fixed scale of 4 (probed verbatim against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("json_array(cast(1 as numeric(10, 2)))", "[1.00]")]
    [DataRow("json_array(cast(1 as numeric(10, 0)))", "[1]")]
    [DataRow("json_array(cast(1 as money), cast(1 as smallmoney))", "[1.0000,1.0000]")]
    [DataRow("json_array(cast(1 as numeric(10, 2)) + 0)", "[1.00]")]
    [DataRow("""json_object('a': cast(1.5 as numeric(10, 4)))""", """{"a":1.5000}""")]
    [DataRow("""json_modify('{"a":0}', '$.a', cast(1 as numeric(10, 2)))""", """{"a":1.00}""")]
    public void Builders_DecimalAndMoney_WriteTheDeclaredScale(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    // --- Result type ---

    [TestMethod]
    public void JsonObject_ResultTypeIsNVarchar()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select json_object('a': 1) as v").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(string), reader.GetFieldType(0));
    }

    [TestMethod]
    [DataRow("json_array(cast('2020-01-02' as datetime))", "[\"2020-01-02T00:00:00\"]")]
    [DataRow("json_array(cast('2020-01-02' as datetime2))", "[\"2020-01-02T00:00:00\"]")]
    [DataRow("json_object('a': cast('03:04:05' as time))", "{\"a\":\"03:04:05\"}")]
    [DataRow("json_array(cast('2020-01-02 03:04:05' as datetimeoffset(0)))", "[\"2020-01-02T03:04:05Z\"]")]
    [DataRow("json_object('a': cast('2020-01-02 03:04:05.5 -05:30' as datetimeoffset(2)))", "{\"a\":\"2020-01-02T03:04:05.50-05:30\"}")]
    [DataRow("(select cast('2020-01-02 03:04:05' as datetimeoffset(0)) a for json path)", "[{\"a\":\"2020-01-02T03:04:05Z\"}]")]
    public void DateTimes_RenderAsForJsonDoes(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    // ---- JSON text and value rendering (probed 2026-10-02 against SQL Server 2025) ----

    [TestMethod]
    [DataRow("select json_object('a':isnull(json_query('[1]'), '[]'))", "{\"a\":[1]}")]
    [DataRow("select json_object('a':coalesce(null, json_query('[1]')))", "{\"a\":[1]}")]
    [DataRow("select json_object('a':nullif(json_query('[1]'), N'x'))", "{\"a\":[1]}")]
    [DataRow("select json_object('a':v) from (select json_array(1) as v) d", "{\"a\":[1]}")]
    [DataRow("with c as (select json_array(1) as v) select json_object('a':v) from c", "{\"a\":[1]}")]
    [DataRow("select json_object('a':(select json_query('[1]')))", "{\"a\":[1]}")]
    [DataRow("select json_object('a':(select 1 as q for json path))", "{\"a\":[{\"q\":1}]}")]
    [DataRow("select json_object('a':(select 1 as q for json path, root('r')))", "{\"a\":{\"r\":[{\"q\":1}]}}")]
    [DataRow("select json_object('a':(select 1 as q for json path, without_array_wrapper))", "{\"a\":\"{\\\"q\\\":1}\"}")]
    [DataRow("select json_object('a':cast(json_query('[1]') as nvarchar(max)))", "{\"a\":\"[1]\"}")]
    [DataRow("select json_object('a':json_query('[1]') + N'')", "{\"a\":\"[1]\"}")]
    [DataRow("select json_object('a':isnull(N'[0]', json_query('[1]')))", "{\"a\":\"[0]\"}")]
    [DataRow("select json_object('a':(select json_arrayagg(n) from (values (1), (2)) v(n)))", "{\"a\":[1,2]}")]
    public void JsonText_EmbedsAsJsonWhereverItsTypeTravels(string sql, string expected)
        => AreEqual(expected, ExecuteScalar(sql));

    [TestMethod]
    public void JsonText_ThroughUnionAll_KeepsTheMarkOnlyWhileBounded()
    {
        var sim = new Simulation();
        AreEqual("{\"k\":[1]}|{\"k\":p}", sim.ExecuteScalar("select string_agg(r, '|') from (select json_object('k':u) as r from (select json_query(N'[1]') as u union all select N'p') d) q"));
        AreEqual("{\"k\":\"[1]\"}|{\"k\":\"[2]\"}", sim.ExecuteScalar("select string_agg(r, '|') from (select json_object('k':u) as r from (select json_array(1) as u union all select json_array(2)) d) q"));
    }

    [TestMethod]
    public void JsonText_StoredBySelectInto_IsPlainText()
        => AreEqual("{\"k\":\"{\\\"d\\\":1}\"}", new Simulation().ExecuteScalar("select json_query('{\"d\":1}') as c into #q; select json_object('k':c) from #q"));

    [TestMethod]
    [DataRow("cast(1 as sql_variant)", "[1]")]
    [DataRow("cast(cast('2024-01-01' as date) as sql_variant)", "[\"2024-01-01\"]")]
    [DataRow("cast(cast(1 as bit) as sql_variant)", "[true]")]
    [DataRow("cast(0x01 as image)", "[\"AQ==\"]")]
    [DataRow("cast(1 as rowversion)", "[\"AAAAAAAAAAE=\"]")]
    [DataRow("cast('9999-12-31 23:59:59.997' as datetime)", "[\"9999-12-31T23:59:59.997\"]")]
    public void JsonArray_RendersEachTypeAsRealDoes(string value, string expected)
        => AreEqual(expected, ExecuteScalar($"select json_array({value})"));

    [TestMethod]
    [DataRow("cast(1.5 as float)", "{\"1.500000000000000e+000\":1}")]
    [DataRow("0x41", "{\"QQ==\":1}")]
    [DataRow("cast(1 as bit)", "{\"true\":1}")]
    [DataRow("cast('2024-01-02 03:04:05' as datetime)", "{\"2024-01-02T03:04:05\":1}")]
    [DataRow("cast(1.5 as money)", "{\"1.5000\":1}")]
    [DataRow("cast(1 as sql_variant)", "{\"1\":1}")]
    public void JsonObject_Key_IsWrittenAsItsValueWouldBe(string key, string expected)
        => AreEqual(expected, ExecuteScalar($"select json_object({key}:1)"));

    [TestMethod]
    [DataRow("select json_object('k':cast('/1/' as hierarchyid))", "json_object and json_objectagg does not support CLR type as parameters", 2)]
    [DataRow("select json_object(geometry::Point(1, 2, 0):1)", "json_object and json_objectagg does not support CLR type as parameters", 2)]
    [DataRow("select json_array(geography::Point(1, 2, 4326))", "json_array does not support CLR type as parameters", 3)]
    [DataRow("select json_arrayagg(cast('/1/' as hierarchyid)) from (values (1)) v(a)", "json_arrayagg does not support CLR type as parameters", 1)]
    [DataRow("select json_objectagg('k':cast('/1/' as hierarchyid)) from (values (1)) v(a)", "json_object and json_objectagg does not support CLR type as parameters", 2)]
    public void Builders_RefuseClrTypes(string sql, string message, int state)
    {
        var error = new Simulation().AssertSqlError(sql, 13666);
        AreEqual(message, error.Errors[0].Message);
        AreEqual((byte)state, error.Errors[0].State);
    }

    [TestMethod]
    public void JsonObject_EqualsInPlaceOfColon_ReportsTheTokenAfterTheValue()
        => new Simulation().ValidateSyntaxError("select json_object('a'=1)", ")");
}
