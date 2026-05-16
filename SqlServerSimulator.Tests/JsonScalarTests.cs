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

    [TestMethod]
    public void JsonValue_InvalidJson_ReturnsNullLax()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select json_value('{not valid}', '$.x')"));

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
        => AreEqual("[1,2,3,4]", ExecuteScalar("select json_modify('[1,2,3]', '$[3]', 4)"));

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
